using System;
using System.Linq;

namespace XrssTransport.Client
{
    /// <summary>
    /// Checks the shared formulas against the fixed vectors of Docs/PAIRING_TEST_VECTORS.md. Runs at startup on the headset (so a runtime that computes something differently, for example an IL2CPP or
    /// Android crypto quirk, is visible in the log immediately) and in every test suite. Cheap: four hashes.
    /// </summary>
    public static class VectorSelfTest
    {
        static byte[] Range(int from, int count) => Enumerable.Range(from, count).Select(i => (byte)i).ToArray();

        public static bool Run(out string detail)
        {
            try
            {
                string mac = AuthCrypto.Hex(AuthCrypto.AuthMac(Range(1, 32), Range(0xA0, 16), Range(0xB0, 16), Range(0xC0, 32)));
                string commit = AuthCrypto.Hex(AuthCrypto.Commit(Range(0x40, 32)));
                string sas = AuthCrypto.Sas(Range(0x80, 32), Range(0x40, 32), Range(0x60, 32), Range(0xB0, 16));
                string fp = AuthCrypto.Hex(AuthCrypto.Fingerprint(Range(0, 100)));
                bool ok = mac == "4B4C0724A544C9147BCA888F9ED853D779BDE968EA7AF0405911EE9DB0634D30"
                       && commit == "CB7C0122587F929E5610667A913CBDB3446B3C08799F61688055D7FC420A419B"
                       && sas == "165978"
                       && fp == "BCE0AFF19CF5AA6A7469A30D61D04E4376E4BBF6381052EE9E7F33925C954D52"
                       && AuthCrypto.MacEquals(Range(1, 8), Range(1, 8)) && !AuthCrypto.MacEquals(Range(1, 8), Range(2, 8));
                detail = ok ? "auth vectors match" : "auth vectors DIFFER (mac " + mac.Substring(0, 8) + ", commit " + commit.Substring(0, 8) + ", sas " + sas + ", fp " + fp.Substring(0, 8) + ")";
                return ok;
            }
            catch (Exception e) { detail = "auth self-test threw " + e.GetType().Name + ": " + e.Message; return false; }
        }
    }
}
