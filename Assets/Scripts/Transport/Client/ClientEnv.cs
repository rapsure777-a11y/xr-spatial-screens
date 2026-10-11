using System;

namespace XrssTransport.Client
{
    /// <summary>
    /// Everything the client transport needs from the platform, behind one small interface so the same client code runs on the Steam Frame (Unity, see UnityClientEnv), in the Unity editor
    /// and in the .NET test project. No feature code (panels, capture, input...) is reachable from here, and nothing here reaches into them.
    /// </summary>
    public interface IClientEnv
    {
        /// <summary>Directory for the device identity and paired-host credentials. On the Frame: the app's internal, app-private files directory.</summary>
        string SecretDir { get; }
        /// <summary>Directory for non-secret settings (connection mode, typed address).</summary>
        string SettingsDir { get; }
        /// <summary>One log line. Callers never pass secrets, challenges, responses, nonces or full credentials.</summary>
        void Log(string message);
        /// <summary>Android needs a Wi-Fi multicast lock to receive mDNS; elsewhere this is a no-op. Dispose to release.</summary>
        IDisposable AcquireMulticastLock();
    }

    public sealed class NullMulticastLock : IDisposable { public void Dispose() { } }

    /// <summary>A console/test environment.</summary>
    public sealed class SimpleClientEnv : IClientEnv
    {
        public string SecretDir { get; set; }
        public string SettingsDir { get; set; }
        public Action<string> Sink = _ => { };
        public void Log(string message) => Sink(message);
        public IDisposable AcquireMulticastLock() => new NullMulticastLock();
    }
}
