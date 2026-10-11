// Pairing spike server (throwaway). Mutual-TLS echo server with a persisted self-signed ECDSA P-256 certificate, plus a minimal mDNS responder for _xrss._tcp.
// usage: PairingSpike [port=5601]. Prints its certificate SHA-256 so the headset can pin it. Accepts any client certificate and logs its SHA-256.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

static class Program
{
    static int s_Port = 5601;

    static async Task Main(string[] args)
    {
        if (args.Length > 0) s_Port = int.Parse(args[0]);
        var cert = LoadOrCreateCert();
        string fp = Convert.ToHexString(SHA256.HashData(cert.RawData));
        Console.WriteLine("server certificate sha256: " + fp);
        Console.WriteLine("listening on 0.0.0.0:" + s_Port);
        new Thread(Mdns) { IsBackground = true }.Start();

        var listener = new TcpListener(IPAddress.Any, s_Port);
        listener.Start();
        while (true)
        {
            var tcp = await listener.AcceptTcpClientAsync();
            _ = Task.Run(() => Serve(tcp, cert));
        }
    }

    static X509Certificate2 LoadOrCreateCert()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XrSpatialScreens", "spike");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "server.pfx");
        if (File.Exists(path)) return new X509Certificate2(path, "spike");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=XrSpatialScreens spike host", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2") }, false));
        using var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var pfx = self.Export(X509ContentType.Pfx, "spike");
        File.WriteAllBytes(path, pfx);
        return new X509Certificate2(pfx, "spike");
    }

    static async Task Serve(TcpClient tcp, X509Certificate2 cert)
    {
        string who = tcp.Client.RemoteEndPoint.ToString();
        try
        {
            tcp.NoDelay = true;
            using var ssl = new SslStream(tcp.GetStream(), false, (s, c, ch, e) =>
            {
                Console.WriteLine("[" + who + "] client cert: " + (c == null ? "NONE" : Convert.ToHexString(SHA256.HashData(c.GetRawCertData()))) + " policyErrors=" + e);
                return true; // spike: server auth is what is pinned here; client certificate optional
            });
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = cert,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            });
            Console.WriteLine("[" + who + "] handshake ok protocol=" + ssl.SslProtocol + " cipher=" + ssl.NegotiatedCipherSuite);
            var buf = new byte[256];
            int n = await ssl.ReadAsync(buf, 0, buf.Length);
            Console.WriteLine("[" + who + "] got: " + Encoding.UTF8.GetString(buf, 0, n).Trim());
            var reply = Encoding.UTF8.GetBytes("hello from pc, you negotiated " + ssl.SslProtocol + "\n");
            await ssl.WriteAsync(reply, 0, reply.Length);
            await ssl.FlushAsync();
            // echo loop so the headset can time round trips over this link
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int rounds = 0;
            while (rounds < 500 && (n = await ssl.ReadAsync(buf, 0, buf.Length)) > 0) { await ssl.WriteAsync(buf, 0, n); rounds++; }
            Console.WriteLine("[" + who + "] echoed " + rounds + " messages in " + sw.ElapsedMilliseconds + " ms");
        }
        catch (Exception e) { Console.WriteLine("[" + who + "] FAILED: " + e.GetType().Name + ": " + e.Message); }
        finally { tcp.Dispose(); }
    }

    // ---- minimal mDNS responder: answers queries mentioning _xrss with PTR + SRV + TXT + A, and announces every 3 s
    static void Mdns()
    {
        var group = IPAddress.Parse("224.0.0.251");
        var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
        var addrs = new List<IPAddress>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || !ni.SupportsMulticast) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address) && !ua.Address.ToString().StartsWith("169."))
                {
                    try { udp.JoinMulticastGroup(group, ua.Address); addrs.Add(ua.Address); Console.WriteLine("mdns: joined on " + ni.Name + " " + ua.Address); } catch { }
                }
        }
        var dest = new IPEndPoint(group, 5353);
        new Thread(() => { while (true) { foreach (var a in addrs) Send(udp, dest, a); Thread.Sleep(3000); } }) { IsBackground = true }.Start();
        while (true)
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            byte[] data;
            try { data = udp.Receive(ref from); } catch { continue; }
            if (data.Length < 12 || (data[2] & 0x80) != 0) continue; // only queries
            if (Encoding.ASCII.GetString(data).IndexOf("_xrss", StringComparison.Ordinal) < 0) continue;
            Console.WriteLine("mdns: query from " + from.Address);
            foreach (var a in addrs) Send(udp, dest, a);
        }
    }

    static void Name(List<byte> b, string name) { foreach (var l in name.Split('.')) { var x = Encoding.ASCII.GetBytes(l); b.Add((byte)x.Length); b.AddRange(x); } b.Add(0); }
    static void U16(List<byte> b, int v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
    static void U32(List<byte> b, int v) { U16(b, v >> 16); U16(b, v & 0xFFFF); }
    static void Rec(List<byte> b, string name, int type, byte[] rdata) { Name(b, name); U16(b, type); U16(b, type == 12 ? 1 : 0x8001); U32(b, 120); U16(b, rdata.Length); b.AddRange(rdata); }

    static void Send(UdpClient udp, IPEndPoint dest, IPAddress ip)
    {
        const string svc = "_xrss._tcp.local", inst = "Spike PC._xrss._tcp.local", host = "xrss-spike-pc.local";
        var b = new List<byte>();
        U16(b, 0); U16(b, 0x8400); U16(b, 0); U16(b, 4); U16(b, 0); U16(b, 0);
        var t = new List<byte>(); Name(t, inst); Rec(b, svc, 12, t.ToArray());
        t = new List<byte>(); U16(t, 0); U16(t, 0); U16(t, s_Port); Name(t, host); Rec(b, inst, 33, t.ToArray());
        var txt = Encoding.ASCII.GetBytes("v=1"); var tx = new List<byte> { (byte)txt.Length }; tx.AddRange(txt); Rec(b, inst, 16, tx.ToArray());
        Rec(b, host, 1, ip.GetAddressBytes());
        try { udp.Send(b.ToArray(), b.Count, dest); } catch { }
    }
}
