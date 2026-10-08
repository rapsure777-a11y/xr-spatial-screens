using System;
using UnityEngine;

namespace XrSpatial.Capture
{
    /// <summary>
    /// The PC's sound as it arrives from the host: 16-bit stereo chunks pushed from the network thread, read by the audio thread through a short jitter buffer.
    /// Playback only starts once about <see cref="PrimeMs"/> of sound is buffered and falls silent (and re-primes) on underrun; if the buffer grows past <see cref="MaxMs"/> the oldest
    /// sound is dropped so the sound never drifts far behind the picture. Resamples to the device's output rate with linear interpolation.
    /// </summary>
    public static class RemoteAudio
    {
        public const int PrimeMs = 70, TargetMs = 90, MaxMs = 220;
        const int RingFrames = 48000 * 2;                            // two seconds at 48 kHz

        static readonly object s_Lock = new object();
        static readonly float[] s_Ring = new float[RingFrames * 2];   // interleaved L R
        static int s_Read, s_Count;                                  // read position and buffered sound, in frames
        static int s_Rate = 48000;
        static double s_Frac;
        static bool s_Primed;

        public static long Packets { get; private set; }
        public static long Underruns { get; private set; }
        public static long Drops { get; private set; }
        public static float BufferedMs { get { lock (s_Lock) return s_Count * 1000f / s_Rate; } }

        /// <summary>Appends 16-bit little-endian interleaved stereo PCM.</summary>
        public static void Push(byte[] pcm, int bytes, int rate)
        {
            int frames = bytes / 4;
            lock (s_Lock)
            {
                if (rate != s_Rate) { s_Rate = rate; s_Count = 0; s_Primed = false; s_Frac = 0; }
                int w = (s_Read + s_Count) % RingFrames;
                for (int i = 0; i < frames; i++)
                {
                    if (s_Count == RingFrames) { s_Read = (s_Read + 1) % RingFrames; s_Count--; Drops++; }
                    s_Ring[w * 2] = BitConverter.ToInt16(pcm, i * 4) / 32768f;
                    s_Ring[w * 2 + 1] = BitConverter.ToInt16(pcm, i * 4 + 2) / 32768f;
                    w = (w + 1) % RingFrames; s_Count++;
                }
                Packets++;
                int max = s_Rate * MaxMs / 1000, target = s_Rate * TargetMs / 1000;
                if (s_Count > max) { int drop = s_Count - target; s_Read = (s_Read + drop) % RingFrames; s_Count -= drop; Drops++; }
            }
        }

        /// <summary>Fills an interleaved output buffer (audio thread). Silence while priming or after an underrun.</summary>
        public static void Fill(float[] data, int channels, int outRate)
        {
            int frames = data.Length / channels;
            lock (s_Lock)
            {
                if (!s_Primed)
                {
                    if (s_Count >= s_Rate * PrimeMs / 1000) s_Primed = true;
                    else { Array.Clear(data, 0, data.Length); return; }
                }
                double step = s_Rate / (double)outRate;
                for (int i = 0; i < frames; i++)
                {
                    if (s_Count < 2) { s_Primed = false; Underruns++; Array.Clear(data, i * channels, (frames - i) * channels); return; }
                    int a = s_Read, b = (s_Read + 1) % RingFrames;
                    float t = (float)s_Frac;
                    float l = s_Ring[a * 2] + (s_Ring[b * 2] - s_Ring[a * 2]) * t;
                    float r = s_Ring[a * 2 + 1] + (s_Ring[b * 2 + 1] - s_Ring[a * 2 + 1]) * t;
                    data[i * channels] = l;
                    if (channels > 1) data[i * channels + 1] = r;
                    for (int c = 2; c < channels; c++) data[i * channels + c] = 0f;
                    s_Frac += step;
                    while (s_Frac >= 1.0) { s_Frac -= 1.0; s_Read = (s_Read + 1) % RingFrames; s_Count--; }
                }
            }
        }

        public static void Reset() { lock (s_Lock) { s_Count = 0; s_Primed = false; s_Frac = 0; } }
    }
}
