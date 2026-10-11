using System;

namespace XrssTransport
{
    /// <summary>Where transport events go. Never receives secrets, challenges, responses or full device ids (callers pass a short id).</summary>
    public interface ITransportLog { void Info(string message); }

    public sealed class ConsoleTransportLog : ITransportLog { public void Info(string message) => Console.WriteLine("[transport] " + message); }

    public sealed class NullTransportLog : ITransportLog { public void Info(string message) { } }

    public static class LogIds
    {
        /// <summary>First 4 bytes of a device id, hex: enough to tell devices apart in a log, not enough to use.</summary>
        public static string Short(string deviceIdHex) => string.IsNullOrEmpty(deviceIdHex) ? "?" : deviceIdHex.Substring(0, Math.Min(8, deviceIdHex.Length));
    }
}
