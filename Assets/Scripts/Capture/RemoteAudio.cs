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
                float gain = s_Gain;
                for (int i = 0; i < frames; i++)
                {
                    if (s_Count < 2) { s_Primed = false; Underruns++; Array.Clear(data, i * channels, (frames - i) * channels); return; }
                    int a = s_Read, b = (s_Read + 1) % RingFrames;
                    float t = (float)s_Frac;
                    float l = s_Ring[a * 2] + (s_Ring[b * 2] - s_Ring[a * 2]) * t;
                    float r = s_Ring[a * 2 + 1] + (s_Ring[b * 2 + 1] - s_Ring[a * 2 + 1]) * t;
                    if (gain != 1f) { l = Limit(l * gain); r = Limit(r * gain); }
                    data[i * channels] = l;
                    if (channels > 1) data[i * channels + 1] = r;
                    for (int c = 2; c < channels; c++) data[i * channels + c] = 0f;
                    s_Frac += step;
                    while (s_Frac >= 1.0) { s_Frac -= 1.0; s_Read = (s_Read + 1) % RingFrames; s_Count--; }
                }
            }
        }

        public static void Reset() { lock (s_Lock) { s_Count = 0; s_Primed = false; s_Frac = 0; } }

        // ------------------------------------------------------------------ volume

        static readonly float[] GainSteps = { 0f, 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f, 2.5f, 3f };
        static volatile float s_Gain = 1f;
        const string GainPref = "xrss.volume";

        /// <summary>Volume multiplier applied to the PC's sound: 1 is the PC's own level, above 1 boosts it (with a soft limiter so loud peaks do not distort).</summary>
        public static float Gain => s_Gain;

        /// <summary>Restores the saved volume (main thread, at start-up).</summary>
        public static void LoadGain() => s_Gain = Mathf.Clamp(PlayerPrefs.GetFloat(GainPref, 1f), 0f, 3f);

        /// <summary>One step up or down the volume scale (0, 25, 50, 75, 100, 125, 150, 200, 250, 300 percent); remembered. Main thread only.</summary>
        public static void StepGain(int direction)
        {
            int nearest = 0;
            for (int i = 1; i < GainSteps.Length; i++) if (Mathf.Abs(GainSteps[i] - s_Gain) < Mathf.Abs(GainSteps[nearest] - s_Gain)) nearest = i;
            s_Gain = GainSteps[Mathf.Clamp(nearest + direction, 0, GainSteps.Length - 1)];
            PlayerPrefs.SetFloat(GainPref, s_Gain); PlayerPrefs.Save();
        }

        /// <summary>Soft knee: untouched up to 0.8, then compressed smoothly towards 1 so boosted peaks stay below clipping.</summary>
        static float Limit(float x)
        {
            float a = Mathf.Abs(x);
            if (a <= 0.8f) return x;
            float y = 0.8f + 0.2f * (float)System.Math.Tanh((a - 0.8f) / 0.2f);
            return x < 0f ? -y : y;
        }
    }
}
