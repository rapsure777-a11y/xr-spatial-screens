using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using UnityEngine;

namespace XrSpatial.App
{
    /// <summary>
    /// THROWAWAY pairing spike (branch claude/pairing-spike). Runs once at start-up only when tlsspike.flag exists in the app data folder; the flag holds "host port serverSha256".
    /// Answers: can the Frame's IL2CPP runtime (1) make a self-signed client certificate, (2) do mutual TLS and pin the server's certificate, which TLS version results,
    /// and (3) hear mDNS answers (multicast lock). Everything is logged with the tag SPIKE; nothing else in the app changes.
    /// </summary>
    public static class TlsSpike
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Start()
        {
            string flag = Path.Combine(Application.persistentDataPath, "tlsspike.flag");
            if (!File.Exists(flag)) return;
            string[] p = File.ReadAllText(flag).Trim().Split(' ');
            var t = new Thread(() => Run(p[0], int.Parse(p[1]), p[2])) { IsBackground = true };
            t.Start();
        }

        static void Log(string s) { Debug.Log("SPIKE " + s); }

        static void Run(string host, int port, string pinned)
        {
            Log("start host=" + host + ":" + port + " runtime=" + Environment.Version + " os=" + Environment.OSVersion);
            Mdns();
            Tls(host, port, pinned, null, (SslProtocols)12288 | SslProtocols.Tls12, "noclientcert-tls1.3-or-1.2");
            Tls(host, port, pinned, null, SslProtocols.Tls12, "noclientcert-tls1.2-only");
            Tls(host, port, "00" + pinned.Substring(2), null, SslProtocols.Tls12, "noclientcert-wrong-pin-must-fail");
            Crypto();
            X509Certificate2 client = MakeClientCert();
            if (client != null)
            {
                Tls(host, port, pinned, client, (SslProtocols)12288 | SslProtocols.Tls12, "clientcert-tls1.3-or-1.2");
                Tls(host, port, pinned, client, SslProtocols.Tls12, "clientcert-tls1.2-only");
            }
            Log("done");
        }

        // raw primitives the app-layer pairing alternative would need
        static void Crypto()
        {
            try
            {
                using (var k = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                {
                    byte[] data = Encoding.UTF8.GetBytes("challenge");
                    byte[] sig = k.SignData(data, HashAlgorithmName.SHA256);
                    var pub = ECDsa.Create(); pub.ImportParameters(k.ExportParameters(false));
                    Log("ECDSA P-256 sign/verify ok=" + pub.VerifyData(data, sig, HashAlgorithmName.SHA256) + " sigLen=" + sig.Length);
                }
            }
            catch (Exception e) { Log("ECDSA FAILED " + e.GetType().Name + ": " + e.Message); }
            try { using (var r = RSA.Create(2048)) Log("RSA-2048 generate ok, sign ok=" + (r.SignData(new byte[] { 1 }, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).Length == 256)); }
            catch (Exception e) { Log("RSA FAILED " + e.GetType().Name + ": " + e.Message); }
            try { Log("HMAC/SHA256/AES-GCM: " + (new HMACSHA256(new byte[16]).ComputeHash(new byte[1]).Length == 32)); using (var g = new AesGcm(new byte[16])) { var ct = new byte[1]; var tag = new byte[16]; g.Encrypt(new byte[12], new byte[1], ct, tag); Log("AesGcm ok"); } }
            catch (Exception e) { Log("AES-GCM FAILED " + e.GetType().Name + ": " + e.Message); }
            try { var b = new byte[16]; RandomNumberGenerator.Create().GetBytes(b); var kdf = new Rfc2898DeriveBytes("pw", b, 1000); Log("PBKDF2 ok " + kdf.GetBytes(8).Length); }
            catch (Exception e) { Log("PBKDF2 FAILED " + e.GetType().Name + ": " + e.Message); }
        }

        static X509Certificate2 MakeClientCert()
        {
            try
            {
                using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                {
                    var req = new CertificateRequest("CN=XrSpatialScreens headset spike", key, HashAlgorithmName.SHA256);
                    using (var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10)))
                    {
                        Log("client cert created in memory, sha256=" + Hex(SHA256.Create().ComputeHash(self.RawData)));
                        byte[] pfx = self.Export(X509ContentType.Pfx, "spike");
                        var c = new X509Certificate2(pfx, "spike");
                        Log("client cert pfx round trip ok, has private key=" + c.HasPrivateKey);
                        return c;
                    }
                }
            }
            catch (Exception e) { Log("CLIENT CERT FAILED " + e.GetType().Name + ": " + e.Message); return null; }
        }

        static void Tls(string host, int port, string pinned, X509Certificate2 client, SslProtocols protocols, string label)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using (var tcp = new TcpClient())
                {
                    tcp.NoDelay = true;
                    tcp.Connect(host, port);
                    string seen = null;
                    using (var ssl = new SslStream(tcp.GetStream(), false, (s, c, ch, e) =>
                    {
                        seen = Hex(SHA256.Create().ComputeHash(c.GetRawCertData()));
                        return string.Equals(seen, pinned, StringComparison.OrdinalIgnoreCase);
                    }))
                    {
                        ssl.AuthenticateAsClient("xrss-pc", client == null ? null : new X509CertificateCollection { client }, protocols, false);
                        Log(label + " handshake ok in " + sw.ElapsedMilliseconds + " ms, protocol=" + ssl.SslProtocol + ", server pin matched=" + (seen != null));
                        byte[] hello = Encoding.UTF8.GetBytes("hello from the frame\n");
                        ssl.Write(hello, 0, hello.Length);
                        var buf = new byte[256];
                        int n = ssl.Read(buf, 0, buf.Length);
                        Log(label + " reply: " + Encoding.UTF8.GetString(buf, 0, n).Trim());
                        if (label == "noclientcert-tls1.3-or-1.2")
                        {
                            var one = new byte[] { 1 };
                            var t0 = System.Diagnostics.Stopwatch.StartNew();
                            const int rounds = 200;
                            for (int i = 0; i < rounds; i++) { ssl.Write(one, 0, 1); ssl.Read(buf, 0, 1); }
                            Log(label + " " + rounds + " tiny round trips: avg " + (t0.Elapsed.TotalMilliseconds / rounds).ToString("F2") + " ms");
                        }
                    }
                }
            }
            catch (Exception e) { Log(label + " FAILED " + e.GetType().Name + ": " + e.Message + (e.InnerException != null ? " / " + e.InnerException.Message : "")); }
        }

        // ---- mDNS: take a multicast lock, join 224.0.0.251:5353, send a PTR query for _xrss._tcp.local, log what comes back for 6 s
        static void Mdns()
        {
            AndroidJavaObject lockObj = null;
            try
            {
                AndroidJNI.AttachCurrentThread();
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var wifi = activity.Call<AndroidJavaObject>("getSystemService", "wifi"))
                {
                    lockObj = wifi.Call<AndroidJavaObject>("createMulticastLock", "xrss-spike");
                    lockObj.Call("acquire");
                    Log("multicast lock held=" + lockObj.Call<bool>("isHeld"));
                }
            }
            catch (Exception e) { Log("multicast lock FAILED " + e.GetType().Name + ": " + e.Message); }
            try
            {
                var group = IPAddress.Parse("224.0.0.251");
                var udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
                udp.JoinMulticastGroup(group);
                udp.Client.ReceiveTimeout = 1000;
                var q = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
                foreach (var l in new[] { "_xrss", "_tcp", "local" }) { var x = Encoding.ASCII.GetBytes(l); q.Add((byte)x.Length); q.AddRange(x); }
                q.AddRange(new byte[] { 0, 0, 12, 0, 1 });
                udp.Send(q.ToArray(), q.Count, new IPEndPoint(group, 5353));
                Log("mdns query sent");
                var end = DateTime.UtcNow.AddSeconds(6);
                int got = 0;
                while (DateTime.UtcNow < end)
                {
                    try
                    {
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        byte[] d = udp.Receive(ref from);
                        string s = Encoding.ASCII.GetString(d);
                        if (s.IndexOf("_xrss", StringComparison.Ordinal) >= 0 && (d[2] & 0x80) != 0)
                        {
                            got++;
                            if (got <= 3) Log("mdns ANSWER from " + from.Address + " (" + d.Length + " bytes, last 4 bytes = " + d[d.Length - 4] + "." + d[d.Length - 3] + "." + d[d.Length - 2] + "." + d[d.Length - 1] + ")");
                        }
                    }
                    catch (SocketException) { }
                }
                Log("mdns answers heard=" + got);
                udp.Close();
            }
            catch (Exception e) { Log("mdns FAILED " + e.GetType().Name + ": " + e.Message); }
            try { if (lockObj != null) lockObj.Call("release"); } catch { }
        }

        static string Hex(byte[] b) { var sb = new StringBuilder(); foreach (var x in b) sb.Append(x.ToString("X2")); return sb.ToString(); }
    }
}
