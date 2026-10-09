using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Diagnostics;

/// <summary>
/// Captures what the PC is playing (WASAPI loopback of the default output device), converts it to 16-bit stereo and hands it on in small chunks.
/// The headset adds its own short jitter buffer. Loopback delivers nothing while the PC is silent, which the headset treats as silence.
/// </summary>
sealed class AudioSender : IDisposable
{
    readonly WasapiLoopbackCapture m_Capture;
    readonly Action<byte[], int, int> m_Send;               // pcm16 stereo bytes, sample rate, channels (always 2)
    public string DeviceName { get; }
    public long Packets, Bytes;
    long m_FirstTicks, m_LastTicks; double m_MaxGapMs;

    public AudioSender(Action<byte[], int, int> send)
    {
        m_Send = send;
        var dev = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        DeviceName = dev.FriendlyName;
        m_Capture = new WasapiLoopbackCapture(dev);
        m_Capture.DataAvailable += OnData;
        m_Capture.StartRecording();
        Console.WriteLine($"audio: capturing '{DeviceName}' {m_Capture.WaveFormat.SampleRate} Hz, {m_Capture.WaveFormat.Channels} ch, {m_Capture.WaveFormat.Encoding}");
    }

    unsafe void OnData(object sender, WaveInEventArgs e)
    {
        var wf = m_Capture.WaveFormat;
        int ch = wf.Channels;
        if (e.BytesRecorded == 0 || ch == 0) return;
        long now = Stopwatch.GetTimestamp();
        if (m_LastTicks != 0) { double gap = (now - m_LastTicks) * 1000.0 / Stopwatch.Frequency; if (gap > m_MaxGapMs) m_MaxGapMs = gap; }
        m_LastTicks = now; if (m_FirstTicks == 0) m_FirstTicks = now;

        int bytesPerSample = wf.BitsPerSample / 8;
        int frames = e.BytesRecorded / (bytesPerSample * ch);
        var pcm = new byte[frames * 4];
        fixed (byte* src = e.Buffer) fixed (byte* dst = pcm)
        {
            short* o = (short*)dst;
            for (int i = 0; i < frames; i++)
            {
                float l, r;
                if (wf.Encoding == WaveFormatEncoding.IeeeFloat || (wf.Encoding == WaveFormatEncoding.Extensible && bytesPerSample == 4 && wf.BitsPerSample == 32))
                {
                    float* f = (float*)(src + (long)i * ch * 4);
                    l = f[0]; r = ch > 1 ? f[1] : f[0];
                }
                else if (bytesPerSample == 2)
                {
                    short* s = (short*)(src + (long)i * ch * 2);
                    l = s[0] / 32768f; r = (ch > 1 ? s[1] : s[0]) / 32768f;
                }
                else { l = r = 0f; }
                o[i * 2] = (short)Math.Clamp((int)(l * 32767f), -32768, 32767);
                o[i * 2 + 1] = (short)Math.Clamp((int)(r * 32767f), -32768, 32767);
            }
        }
        Packets++; Bytes += pcm.Length;
        m_Send(pcm, wf.SampleRate, 2);
    }

    /// <summary>One line for the host log: how many packets arrived since the last call and the longest gap between them (capture cadence).</summary>
    public string TakeStats()
    {
        double maxGap = m_MaxGapMs; long p = Packets; Packets = 0; m_MaxGapMs = 0;
        return $"audio: {p} packets in the last interval, longest gap {maxGap:0} ms";
    }

    public void Dispose()
    {
        try { m_Capture.DataAvailable -= OnData; m_Capture.StopRecording(); } catch { }
        m_Capture.Dispose();
    }
}
