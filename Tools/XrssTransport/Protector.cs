using System;
using System.Security.Cryptography;
using System.Text;

namespace XrssTransport
{
    /// <summary>Protects bytes at rest. The production implementation is Windows DPAPI; tests can substitute another.</summary>
    public interface IProtector
    {
        byte[] Protect(byte[] data);
        byte[] Unprotect(byte[] data);
    }

    /// <summary>
    /// Windows DPAPI, CurrentUser scope: only the same Windows user on the same machine can decrypt. A fixed application entropy string separates our blobs from other DPAPI users of the account.
    /// Anyone running code as that Windows user can still ask DPAPI to decrypt: that is the documented limit of this protection (the threat model excludes malware on the PC).
    /// </summary>
    public sealed class DpapiProtector : IProtector
    {
        static readonly byte[] Entropy = Encoding.ASCII.GetBytes("XrSpatialScreens.Transport.v1");
        public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
    }
}
