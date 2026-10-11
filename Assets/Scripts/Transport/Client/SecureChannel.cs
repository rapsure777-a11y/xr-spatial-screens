using System;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;

namespace XrssTransport.Client
{
    /// <summary>The PC presented a certificate that is not the one this headset trusts for it.</summary>
    public sealed class PinMismatchException : Exception
    {
        public byte[] Presented;
        public PinMismatchException(byte[] presented) : base("the PC's certificate does not match the saved one") { Presented = presented; }
    }

    /// <summary>A TLS 1.2 connection to a PC host that has NOT yet authenticated the headset. Nothing of the application protocol may be sent on it until authentication succeeded.</summary>
    public sealed class SecureConnection : IDisposable
    {
        public TcpClient Tcp;
        public SslStream Ssl;
        public byte[] PresentedFingerprint;
        public SslProtocols Protocol;
        public long TcpMs, TlsMs;
        public string Address; public int Port;
        public void Dispose() { try { Ssl?.Dispose(); } catch { } try { Tcp?.Close(); } catch { } }
    }

    /// <summary>
    /// TLS 1.2 to the PC. For a paired PC the certificate must match the saved SHA-256 fingerprint or the handshake fails (<see cref="PinMismatchException"/>); there is no fallback that skips the check.
    /// For first-time pairing (<c>pinnedFingerprint == null</c>) the certificate is not yet trusted: its fingerprint is only recorded, and it is authenticated by the six-digit code the user compares
    /// afterwards. Every blocking step has a timeout and closes the socket when it expires. The Frame's runtime has TLS 1.2 only, no client certificates (docs/PAIRING_SPIKE_RESULTS.md).
    /// </summary>
    public static class SecureChannel
    {
        public static SecureConnection Connect(string address, int port, byte[] pinnedFingerprint, int timeoutMs)
        {
            var c = new SecureConnection { Address = address, Port = port };
            var tcp = new TcpClient { NoDelay = true };
            c.Tcp = tcp;
            try
            {
                var sw = Stopwatch.StartNew();
                var connect = tcp.ConnectAsync(address, port);
                if (!Wait(connect, timeoutMs)) throw new TimeoutException("connect timed out");
                c.TcpMs = sw.ElapsedMilliseconds; sw.Restart();
                bool pinFailed = false; byte[] seen = null;
                var ssl = new SslStream(tcp.GetStream(), false, (s, cert, chain, errors) =>
                {
                    seen = AuthCrypto.Fingerprint(cert.GetRawCertData());
                    if (pinnedFingerprint == null) return true;                          // pairing: authenticated afterwards by the comparison code
                    bool ok = AuthCrypto.MacEquals(seen, pinnedFingerprint);
                    if (!ok) pinFailed = true;
                    return ok;
                });
                c.Ssl = ssl;
                var handshake = Task.Run(() => ssl.AuthenticateAsClient("xrss-pc", null, SslProtocols.Tls12, false));
                if (!Wait(handshake, timeoutMs)) throw new TimeoutException("TLS handshake timed out");
                if (handshake.IsFaulted)
                {
                    if (pinFailed) throw new PinMismatchException(seen);
                    throw handshake.Exception.GetBaseException();
                }
                if (seen == null) throw new AuthenticationException("no certificate was presented");
                c.PresentedFingerprint = seen; c.TlsMs = sw.ElapsedMilliseconds; c.Protocol = ssl.SslProtocol;
                return c;
            }
            catch (Exception) { c.Dispose(); throw; }
        }

        /// <summary>Waits for a task without ever blocking longer than <paramref name="ms"/>; false on timeout. Exceptions stay on the task.</summary>
        public static bool Wait(Task t, int ms)
        {
            try { return t.Wait(ms); } catch (AggregateException) { return true; }       // finished (faulted): the caller inspects the task
        }
    }
}
