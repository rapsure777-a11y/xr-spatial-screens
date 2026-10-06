using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using XrSpatial.Capture;
using XrSpatial.Spatial;

namespace XrSpatial.Tests
{
    public class CaptureProtocolTests
    {
        [Test]
        public void FrameProtocol_ConstantsMatchTheCaptureHelpersCopy()
        {
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Tools", "XrssCapture", "FrameProtocol.cs"));
            Assert.IsTrue(File.Exists(path), path);
            string text = File.ReadAllText(path);
            var helper = new Dictionary<string, long>();
            foreach (Match m in Regex.Matches(text, @"public const (?:uint|int) (\w+) = (0x[0-9A-Fa-f]+|\d+)"))
                helper[m.Groups[1].Value] = m.Groups[2].Value.StartsWith("0x") ? System.Convert.ToInt64(m.Groups[2].Value, 16) : long.Parse(m.Groups[2].Value);
            // Multi-declarations ("public const int A = 1, B = 2, ...") need their own pass.
            foreach (Match line in Regex.Matches(text, @"public const (?:uint|int) ([^;]+);"))
                foreach (Match kv in Regex.Matches(line.Groups[1].Value, @"(\w+) = (0x[0-9A-Fa-f]+|\d+)"))
                    helper[kv.Groups[1].Value] = kv.Groups[2].Value.StartsWith("0x") ? System.Convert.ToInt64(kv.Groups[2].Value, 16) : long.Parse(kv.Groups[2].Value);
            int compared = 0;
            foreach (var f in typeof(FrameProtocol).GetFields())
            {
                if (!f.IsLiteral) continue;
                Assert.IsTrue(helper.TryGetValue(f.Name, out long v), "helper is missing " + f.Name);
                Assert.AreEqual(System.Convert.ToInt64(f.GetRawConstantValue()), v, f.Name);
                compared++;
            }
            Assert.Greater(compared, 20);
        }

        [Test]
        public void FrameProtocol_HeaderFieldsDoNotOverlapAndFitTheHeader()
        {
            var offs = new[] { FrameProtocol.OffMagic, FrameProtocol.OffVersion, FrameProtocol.OffHeaderSize, FrameProtocol.OffSlotCapacity, FrameProtocol.OffMaxWidth, FrameProtocol.OffMaxHeight,
                FrameProtocol.OffFrameCounter, FrameProtocol.OffLatestSlot, FrameProtocol.OffWidth, FrameProtocol.OffHeight, FrameProtocol.OffStride, FrameProtocol.OffFlags,
                FrameProtocol.OffClientX, FrameProtocol.OffClientY, FrameProtocol.OffClientW, FrameProtocol.OffClientH, FrameProtocol.OffHelperPid, FrameProtocol.OffHwnd,
                FrameProtocol.OffDropped, FrameProtocol.OffSourceW, FrameProtocol.OffSourceH, FrameProtocol.OffCaptureFps };
            var seen = new HashSet<int>();
            foreach (var o in offs) { Assert.IsTrue(seen.Add(o), "duplicate offset " + o); Assert.Less(o + 4, FrameProtocol.HeaderSize + 1); Assert.AreEqual(0, o % 4); }
            Assert.AreEqual(FrameProtocol.OffTimestampUs + 8, FrameProtocol.OffHelperPid, "the 64-bit timestamp occupies 64..71");
            Assert.AreEqual(2L * 3840 * 2160 * 4 + FrameProtocol.HeaderSize, FrameProtocol.TotalSize(3840, 2160));
        }

        [Test]
        public void CaptureCatalog_ParsesTheHelpersWindowListJson()
        {
            string json = "[{\"hwnd\":5311028,\"title\":\"notes.txt - Notepad\",\"pid\":21180,\"process\":\"Notepad\",\"x\":32,\"y\":34,\"w\":2568,\"h\":1028},{\"hwnd\":67674,\"title\":\"A \\\"quoted\\\" title\",\"pid\":6600,\"process\":\"WindowsTerminal\",\"x\":182,\"y\":182,\"w\":1113,\"h\":627}]";
            var w = CaptureCatalog.Parse(json);
            Assert.AreEqual(2, w.Length);
            Assert.AreEqual("Notepad", w[0].process); Assert.AreEqual(2568, w[0].w); Assert.AreEqual(5311028L, w[0].hwnd);
            Assert.AreEqual("A \"quoted\" title", w[1].title);
            Assert.AreEqual(0, CaptureCatalog.Parse("").Length);
            Assert.AreEqual(0, CaptureCatalog.Parse("[]").Length);
        }

        [Test]
        public void InputForwarder_MapsPictureCoordinatesToDesktopPixels()
        {
            // A 1920x1080 window whose top-left is at (100, 50) on the desktop.
            var tl = InputForwarder.SourceToDesktop(new Vector2(0f, 0f), 100, 50, 1920, 1080);
            var br = InputForwarder.SourceToDesktop(new Vector2(1f, 1f), 100, 50, 1920, 1080);
            var mid = InputForwarder.SourceToDesktop(new Vector2(0.5f, 0.5f), 100, 50, 1920, 1080);
            Assert.AreEqual(new Vector2Int(100, 50), tl);
            Assert.AreEqual(new Vector2Int(100 + 1919, 50 + 1079), br, "clamped to the last pixel inside the window");
            Assert.AreEqual(new Vector2Int(100 + 960, 50 + 540), mid);
            // Absolute SendInput coordinates span the virtual desktop (which may start at a negative origin with a second monitor on the left).
            var a = InputForwarder.DesktopToAbsolute(new Vector2Int(-1920, 0), -1920, 0, 3840, 1080);
            var b = InputForwarder.DesktopToAbsolute(new Vector2Int(1919, 1079), -1920, 0, 3840, 1080);
            Assert.AreEqual(0, a.x); Assert.AreEqual(65535, b.x); Assert.AreEqual(65535, b.y);
        }

        [Test]
        public void PanelUv_ThroughACrop_LandsOnTheRightWindowPixel()
        {
            // A crop of the right-hand half of the window: the panel's centre must forward to the middle of that half.
            var crop = new Rect(0.5f, 0f, 0.5f, 1f);
            var src = XrSpatial.Core.QuadMath.PanelUvToSource(new Vector2(0.5f, 0.5f), crop);
            var px = InputForwarder.SourceToDesktop(src, 0, 0, 2000, 1000);
            Assert.AreEqual(1500, px.x); Assert.AreEqual(500, px.y);
        }
    }
}
