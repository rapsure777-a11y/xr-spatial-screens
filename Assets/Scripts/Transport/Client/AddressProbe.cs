using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace XrssTransport.Client
{
    /// <summary>
    /// A PC usually has several addresses (for example the router network and the Valve dongle link), and they are not equal: the dongle link answers in about 1 ms, the router path in tens of milliseconds
    /// with spikes. Picking the first address found would silently cost the user streaming latency. This measures how long a plain TCP connect takes to each candidate, all at once, and returns them fastest
    /// first. No data is sent; the probe connection is closed at once. Trust is unaffected: whatever address wins still has to present the saved certificate.
    /// </summary>
    public static class AddressProbe
    {
        /// <summary>Milliseconds to connect, or -1 if the address could not be reached within the timeout.</summary>
        public delegate long ConnectMs(string address, int port, int timeoutMs);

        public static long TcpConnectMs(string address, int port, int timeoutMs)
        {
            using (var c = new TcpClient())
            {
                var sw = Stopwatch.StartNew();
                try { var t = c.ConnectAsync(address, port); if (!t.Wait(timeoutMs) || !c.Connected) return -1; return Math.Max(0, sw.ElapsedMilliseconds); }
                catch (Exception) { return -1; }
            }
        }

        /// <summary>Returns the reachable addresses fastest first (unreachable ones are dropped). Once one address has answered, the others get a short grace period (default 150 ms) so a dead address does not slow every connect.</summary>
        public static List<(string address, long ms)> Rank(IEnumerable<string> addresses, int port, int timeoutMs = 1500, int graceMs = 150, ConnectMs connect = null)
        {
            connect = connect ?? TcpConnectMs;
            var list = addresses.Where(a => !string.IsNullOrEmpty(a)).Distinct().ToList();
            var results = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
            var tasks = list.Select(a => Task.Run(() => { results[a] = connect(a, port, timeoutMs); })).ToArray();
            var sw = Stopwatch.StartNew(); long firstOk = -1;
            while (!tasks.All(t => t.IsCompleted) && sw.ElapsedMilliseconds < timeoutMs + 200)
            {
                if (firstOk < 0 && results.Values.Any(v => v >= 0)) firstOk = sw.ElapsedMilliseconds;
                if (firstOk >= 0 && sw.ElapsedMilliseconds - firstOk > graceMs) break;
                Task.Delay(5).Wait();
            }
            return results.Where(kv => kv.Value >= 0).OrderBy(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
        }
    }
}
