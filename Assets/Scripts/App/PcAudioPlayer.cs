using UnityEngine;
using XrSpatial.Capture;

namespace XrSpatial.App
{
    /// <summary>
    /// Plays the PC's sound (<see cref="RemoteAudio"/>) through the headset. A silent looping clip keeps an AudioSource running so Unity calls
    /// <see cref="OnAudioFilterRead"/> on the audio thread, which overwrites the buffer with the PC's sound. Plain 2D sound (not positioned in the room).
    /// A log line every 5 s reports the buffer level, so lag and dropouts can be diagnosed afterwards.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class PcAudioPlayer : MonoBehaviour
    {
        AudioSource m_Source;
        int m_OutRate = 48000;
        float m_NextLog;
        long m_LastPackets, m_LastUnderruns, m_LastDrops;

        public static PcAudioPlayer Create(Transform parent)
        {
            var go = new GameObject("PcAudio");
            go.transform.SetParent(parent, false);
            return go.AddComponent<PcAudioPlayer>();
        }

        void Awake()
        {
            m_OutRate = AudioSettings.outputSampleRate;
            m_Source = GetComponent<AudioSource>();
            var clip = AudioClip.Create("pc-audio", 1024, 1, m_OutRate, false);
            m_Source.clip = clip; m_Source.loop = true; m_Source.spatialBlend = 0f; m_Source.volume = 1f; m_Source.playOnAwake = false;
            m_Source.Play();
            Debug.Log($"[XrSpatial] audio output {m_OutRate} Hz");
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            if (!RemoteHost.SoundEnabled) { System.Array.Clear(data, 0, data.Length); return; }
            RemoteAudio.Fill(data, channels, m_OutRate);
        }

        void Update()
        {
            if (!m_Source.isPlaying && enabled) m_Source.Play();
            if (Time.unscaledTime < m_NextLog) return;
            m_NextLog = Time.unscaledTime + 5f;
            if (!RemoteHost.SoundEnabled) return;
            long p = RemoteAudio.Packets, u = RemoteAudio.Underruns, d = RemoteAudio.Drops;
            Debug.Log($"[XrSpatial] audio: {p - m_LastPackets} packets, buffered {RemoteAudio.BufferedMs:0} ms, underruns +{u - m_LastUnderruns}, drops +{d - m_LastDrops}");
            m_LastPackets = p; m_LastUnderruns = u; m_LastDrops = d;
        }
    }
}
