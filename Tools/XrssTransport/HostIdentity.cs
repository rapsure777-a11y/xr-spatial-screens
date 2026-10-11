using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace XrssTransport
{
    /// <summary>
    /// The PC's identity: a stable random host id and a self-signed ECDSA P-256 certificate (the one the headset pins). Stored as one file, <c>host-identity.dat</c>,
    /// whose whole content is DPAPI-protected JSON {version, hostId, pfx (base64), createdUtc}. Replacing the identity (<see cref="Regenerate"/>) gives a NEW host id and certificate, so every
    /// paired device (bound to the old host id) stops authenticating and must pair again; nothing is migrated on purpose.
    /// </summary>
    public sealed class HostIdentity : IDisposable
    {
        const int FormatVersion = 1;
        public byte[] HostId { get; private set; }
        public X509Certificate2 Certificate { get; private set; }
        public byte[] Fingerprint => AuthCrypto.Fingerprint(Certificate.RawData);
        public string HostIdHex => AuthCrypto.Hex(HostId);
        public DateTime CreatedUtc { get; private set; }

        readonly string m_Path;
        readonly IProtector m_Protector;

        HostIdentity(string path, IProtector protector) { m_Path = path; m_Protector = protector; }

        public static HostIdentity LoadOrCreate(string directory, IProtector protector, ITransportLog log = null)
        {
            Directory.CreateDirectory(directory);
            var id = new HostIdentity(Path.Combine(directory, "host-identity.dat"), protector);
            if (File.Exists(id.m_Path))
            {
                try { id.Load(); return id; }
                catch (Exception e) { (log ?? new NullTransportLog()).Info("host identity unreadable (" + e.GetType().Name + "), creating a new one: every device must pair again"); }
            }
            id.Create();
            (log ?? new NullTransportLog()).Info("host identity created");
            return id;
        }

        /// <summary>Make a new host id and certificate. Devices paired to the old identity no longer match.</summary>
        public void Regenerate() { Certificate?.Dispose(); Create(); }

        void Create()
        {
            HostId = AuthCrypto.Random(AuthCrypto.IdLength);
            CreatedUtc = DateTime.UtcNow;
            using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                var req = new CertificateRequest("CN=XR Spatial Screens host " + AuthCrypto.Hex(HostId).Substring(0, 8), key, HashAlgorithmName.SHA256);
                req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
                using (var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20)))
                {
                    byte[] pfx = self.Export(X509ContentType.Pfx, "xrss");
                    Certificate = new X509Certificate2(pfx, "xrss", X509KeyStorageFlags.DefaultKeySet);
                    Save(pfx);
                }
            }
        }

        void Save(byte[] pfx)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(new Blob { Version = FormatVersion, HostId = AuthCrypto.Hex(HostId), Pfx = Convert.ToBase64String(pfx), CreatedUtc = CreatedUtc });
            byte[] protectedBytes = m_Protector.Protect(json);
            Array.Clear(json, 0, json.Length);
            string tmp = m_Path + ".tmp";
            File.WriteAllBytes(tmp, protectedBytes);
            File.Move(tmp, m_Path, true);
        }

        void Load()
        {
            byte[] json = m_Protector.Unprotect(File.ReadAllBytes(m_Path));
            var blob = JsonSerializer.Deserialize<Blob>(json);
            Array.Clear(json, 0, json.Length);
            if (blob == null || blob.Version != FormatVersion) throw new InvalidDataException("unknown identity format");
            HostId = AuthCrypto.FromHex(blob.HostId);
            CreatedUtc = blob.CreatedUtc;
            Certificate = new X509Certificate2(Convert.FromBase64String(blob.Pfx), "xrss", X509KeyStorageFlags.DefaultKeySet);
        }

        public void Dispose() { Certificate?.Dispose(); }

        sealed class Blob { public int Version { get; set; } public string HostId { get; set; } public string Pfx { get; set; } public DateTime CreatedUtc { get; set; } }
    }
}
