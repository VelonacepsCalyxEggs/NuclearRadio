using BepInEx;
using BepInEx.Configuration;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using UnityEngine;

namespace NuclearOptionRadio
{
    /// <summary>
    ///  A plugin that plays MPEG Icecast (or just a stream) from http, in NO.
    /// </summary>
    /// <remarks>
    /// Currently, there are some problems, like use of a non-thread safe stuff in a thread,
    /// MPEG 1 Layer 3 only being supported,
    /// And only mp3 being supported, I should add OGG and other popular streamed formats.
    /// Perhaps later also try to add realtime metadata extraction, to disable it on screen when a track changes. (Icy-Metadata)
    /// Also maybe not use the NAudio library, and write my own functions to handle everything I need.
    /// Right now Linux is also not supported due to it using NAudio.WinMM;
    /// </remarks>
    [BepInPlugin("com.func_kenobi.radio", "Nuclear Radio", "0.0.1")]
    public class RadioPlugin : BaseUnityPlugin
    {
        public static ConfigEntry<bool> SpatialAudio;
        public static ConfigEntry<string> StreamUrl;
        public static ConfigEntry<float> AudioVolume;

        private BufferedWaveProvider waveProvider;
        private Thread networkThread;
        private volatile bool isPlaying = false;
        private AudioSource audioSource;
        private IMp3FrameDecompressor currentDecompressor;

        void Awake()
        {
            // Config bindings
            StreamUrl = Config.Bind(
                "General",
                "StreamUrl",
                "https://radio.funckenobi42.space/stream.mp3",
                "Set the stream URL, currently only MPEG is supported."
            );
            SpatialAudio = Config.Bind(
                "Sound",
                "EnableSpatial",
                true,
                "Enable or disable spatial audio on the AudioSource"
            );
            AudioVolume = Config.Bind(
                "Sound",
                "Volume",
                50f,
                new ConfigDescription("Volume", new AcceptableValueRange<float>(0f, 100f))
            );
            // Config change handlers.
            AudioVolume.SettingChanged += OnVolumeSettingChanged;
            SpatialAudio.SettingChanged += OnSpatialSettingChanged;
            StreamUrl.SettingChanged += OnStreamUrlSettingChanged;

            // Setup AudioSource
            audioSource = gameObject.GetComponent<AudioSource>() ?? gameObject.AddComponent<AudioSource>();
            audioSource.volume = AudioVolume.Value / 100f;
            audioSource.spatialize = SpatialAudio.Value;
            audioSource.loop = false;

            Logger.LogInfo("NAudio Radio Plugin Loaded!");
        }

        void Update()
        {
            if (UnityInput.Current.GetKeyDown(KeyCode.K))
            {
                if (!isPlaying) StartRadio();
                else StopRadio();
            }
        }

        void StartRadio()
        {
            if (isPlaying) return;
            isPlaying = true;
            Logger.LogInfo("Connecting to stream via NAudio...");

            networkThread = new Thread(StreamProcessor);
            networkThread.IsBackground = true;
            networkThread.Start();

            audioSource.Play();
        }

        void StopRadio()
        {
            if (!isPlaying) return;

            isPlaying = false;

            audioSource.Stop();

            // wait for network thread to finish
            if (networkThread != null && networkThread.IsAlive)
            {
                networkThread.Join(1000);
                if (networkThread.IsAlive)
                    networkThread.Interrupt(); 
                networkThread = null;
            }

            if (waveProvider != null)
            {
                waveProvider.ClearBuffer();
                waveProvider = null;
            }

            if (currentDecompressor != null)
            {
                currentDecompressor.Dispose();
                currentDecompressor = null;
            }

            Logger.LogInfo("Radio stopped and buffers cleared.");
        }
        private void OnVolumeSettingChanged(object sender, EventArgs e)
        {
            ApplyVolume();
            Logger.LogInfo($"Volume changed to {AudioVolume.Value}");
        }

        private void OnSpatialSettingChanged(object sender, EventArgs e)
        {
            ApplySpatial();
            Logger.LogInfo($"Spatial changed to {SpatialAudio.Value}");
        }
        private void OnStreamUrlSettingChanged(object sender, EventArgs e)
        {
            ApplyStreamUrl();
            Logger.LogInfo($"Stream URL changed to {StreamUrl.Value}");
        }

        private void ApplyVolume()
        {
            audioSource.volume = AudioVolume.Value / 100f;
        }
        private void ApplySpatial()
        {
            audioSource.spatialize = SpatialAudio.Value;
        }
        private void ApplyStreamUrl()
        {
            StopRadio();
            // Not the best implementation.
            bool isValid = Uri.TryCreate(StreamUrl.Value, UriKind.Absolute, out Uri uriResult)
                           && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps)
                           && uriResult.Host.Contains(".")
                           && uriResult.Host.Split('.').Last().Length > 1; // check TLD.

            if (isValid)
            {
                StartRadio();
            }
            else
            {
                Logger.LogInfo($"Was not a valid Stream URL.");
            }
        }
        void StreamProcessor()
        {
            IMp3FrameDecompressor decompressor = null;
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(StreamUrl.Value);
                request.AllowReadStreamBuffering = false;

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream networkStream = response.GetResponseStream())
                {
                    byte[] readBuffer = new byte[8192];
                    List<byte> frameBuffer = new List<byte>(65536);
                    byte[] pcmBuffer = new byte[16384 * 4];

                    while (isPlaying)
                    {
                        int bytesRead = networkStream.Read(readBuffer, 0, readBuffer.Length);
                        if (bytesRead == 0) break;

                        frameBuffer.AddRange(readBuffer.Take(bytesRead));

                        int offset = 0;
                        while (offset < frameBuffer.Count)
                        {
                            // Find MP3 sync word
                            if (frameBuffer[offset] != 0xFF)
                            {
                                offset++;
                                continue;
                            }
                            if (offset + 1 >= frameBuffer.Count) break;
                            if ((frameBuffer[offset + 1] & 0xE0) != 0xE0)
                            {
                                offset++;
                                continue;
                            }

                            int frameLength = GetMp3FrameLength(frameBuffer, offset);
                            if (frameLength <= 0)
                            {
                                offset++;
                                continue;
                            }

                            if (offset + frameLength > frameBuffer.Count) break;

                            // extract a complete frame into a MemoryStream (seekable)
                            byte[] frameBytes = frameBuffer.GetRange(offset, frameLength).ToArray();
                            using (MemoryStream frameStream = new MemoryStream(frameBytes))
                            {
                                Mp3Frame frame = Mp3Frame.LoadFromStream(frameStream);
                                if (frame == null) break;

                                if (decompressor == null)
                                {
                                    decompressor = CreateFrameDecompressor(frame);
                                    currentDecompressor = decompressor; // store for cleanup
                                    waveProvider = new BufferedWaveProvider(decompressor.OutputFormat);
                                    waveProvider.BufferDuration = TimeSpan.FromSeconds(20);
                                    Logger.LogInfo($"MP3 decoder initialized: {decompressor.OutputFormat}");
                                }

                                int decompressed = decompressor.DecompressFrame(frame, pcmBuffer, 0);
                                if (decompressed > 0)
                                {
                                    waveProvider.AddSamples(pcmBuffer, 0, decompressed);
                                }
                            }

                            // remove the processed frame from the buffer
                            frameBuffer.RemoveRange(offset, frameLength);
                            // offset stays the same because next frame may start right after
                        }

                        // prevent buffer bloat
                        if (frameBuffer.Count > 1024 * 1024)
                            frameBuffer.RemoveRange(0, frameBuffer.Count - 65536);

                        // avoid overfilling the WaveProvider if Unity can't keep up
                        if (waveProvider != null && waveProvider.BufferedBytes > waveProvider.BufferLength * 0.9)
                            Thread.Sleep(50);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Streaming Error: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                // ensure cleanup even on error
                if (decompressor != null && decompressor != currentDecompressor)
                    decompressor.Dispose();
                if (currentDecompressor != null)
                {
                    currentDecompressor.Dispose();
                    currentDecompressor = null;
                }
                waveProvider = null;
                isPlaying = false;
            }
        }

        private int GetMp3FrameLength(List<byte> buffer, int offset)
        {
            if (offset + 3 >= buffer.Count) return 0;

            byte b1 = buffer[offset + 1];
            byte b2 = buffer[offset + 2];

            int version = (b1 >> 3) & 0x03;
            int layer = (b1 >> 1) & 0x03;
            int bitrateIndex = (b2 >> 4) & 0x0F;
            int sampleRateIndex = (b2 >> 2) & 0x03;
            int padding = (b2 >> 1) & 0x01;

            // only MPEG1 Layer3 for now.
            if (version != 3 || layer != 1) return 0;

            int[] bitrates = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
            int[] sampleRates = { 44100, 48000, 32000, 0 };

            if (bitrateIndex >= bitrates.Length) return 0;
            int bitrate = bitrates[bitrateIndex] * 1000;
            int sampleRate = sampleRateIndex < sampleRates.Length ? sampleRates[sampleRateIndex] : 0;
            if (bitrate == 0 || sampleRate == 0) return 0;

            return (144 * bitrate / sampleRate) + padding;
        }
        private IMp3FrameDecompressor CreateFrameDecompressor(Mp3Frame frame)
        {
            WaveFormat waveFormat = new Mp3WaveFormat(frame.SampleRate, 2, frame.BitRate, frame.FrameLength);
            return new AcmMp3FrameDecompressor(waveFormat);
        }
        void OnAudioFilterRead(float[] data, int channels)
        {
            if (!isPlaying || waveProvider == null) return;

            // bufferedWaveProvider outputs 16-bit PCM (2 bytes per sample)
            byte[] byteBuffer = new byte[data.Length * 2];
            int bytesRead = waveProvider.Read(byteBuffer, 0, byteBuffer.Length);

            // convert 16-bit to float (-1..1)
            int samplesRead = bytesRead / 2;
            for (int i = 0; i < samplesRead && i < data.Length; i++)
            {
                short sample = BitConverter.ToInt16(byteBuffer, i * 2);
                data[i] = sample / 32768f;
            }

            // fill any remaining samples with silence
            for (int i = samplesRead; i < data.Length; i++)
            {
                data[i] = 0f;
            }
        }

        void OnDisable()
        {
            StopRadio();
        }
    }
}