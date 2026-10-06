using System.Collections;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using XrSpatial.Capture;

namespace XrSpatial.Tests
{
    /// <summary>
    /// End-to-end capture: starts the real helper process against a real window (Tools/ClickTest, a window with a light title bar over a dark-blue client area) and checks
    /// that frames arrive through shared memory with the right size, orientation (row 0 is the image top) and window geometry. Skipped if the fixtures are not built
    /// (`dotnet build -c Release` in Tools/XrssCapture and Tools/ClickTest).
    /// </summary>
    public class CaptureIntegrationTests
    {
        Process m_Window;

        [TearDown]
        public void TearDown()
        {
            try { if (m_Window != null && !m_Window.HasExited) m_Window.Kill(); } catch { }
        }

        [UnityTest]
        public IEnumerator RealWindow_IsCapturedThroughTheHelper_WithCorrectOrientationAndGeometry()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string fixture = Path.Combine(root, "Tools", "ClickTest", "bin", "Release", "net8.0-windows", "ClickTest.exe");
            if (SidecarBackend.FindHelper() == null) Assert.Ignore("capture helper not built");
            if (!File.Exists(fixture)) Assert.Ignore("ClickTest fixture not built");
            string log = Path.Combine(Path.GetTempPath(), "xrss-itest-" + System.Guid.NewGuid().ToString("N") + ".txt");
            m_Window = Process.Start(new ProcessStartInfo(fixture, $"1500 200 600 400 \"{log}\"") { UseShellExecute = false });
            yield return new WaitForSecondsRealtime(2.0f);

            using var backend = new SidecarBackend(new CaptureRequest { titleContains = "XRSS ClickTest" });
            Texture2D tex = null;
            float deadline = Time.realtimeSinceStartup + 15f;
            bool got = false;
            while (Time.realtimeSinceStartup < deadline && !got) { got = backend.PollInto(ref tex); yield return null; }
            Assert.IsTrue(got, "no frame arrived: " + backend.Status);
            Assert.NotNull(tex);
            Assert.GreaterOrEqual(tex.width, 600); Assert.GreaterOrEqual(tex.height, 400);
            Assert.Less(tex.width, 800); Assert.Less(tex.height, 600);

            // The captured area is the whole visible window (title bar included): it is the window's frame, a little larger than the 600x400 client area.
            var win = backend.Window;
            Assert.IsTrue(win.IsValid, "window geometry missing");
            Assert.AreEqual(tex.width, win.width, 4);
            Assert.AreEqual(tex.height, win.height, 4);
            Assert.AreEqual(1500, win.x, 12); Assert.AreEqual(200, win.y, 12);
            Assert.AreNotEqual(0L, win.hwnd);

            // Orientation: image row 0 (the title bar, light grey) is texture row 0; the client area (dark blue) is above it in texture space.
            Color top = tex.GetPixel(tex.width / 2, 6);
            Color body = tex.GetPixel(tex.width / 2, tex.height / 2 + 40);
            Assert.Greater(top.r + top.g + top.b, 2.0f, "the title bar should be light: got " + top);
            Assert.Less(body.r, 0.5f); Assert.Greater(body.b, body.r, "the client area is dark blue: got " + body);
            Object.Destroy(tex);
        }

        [UnityTest]
        public IEnumerator MissingWindow_ReportsAClearErrorAndEnds()
        {
            if (SidecarBackend.FindHelper() == null) Assert.Ignore("capture helper not built");
            using var backend = new SidecarBackend(new CaptureRequest { titleContains = "no such window " + System.Guid.NewGuid().ToString("N") });
            Texture2D tex = null;
            float deadline = Time.realtimeSinceStartup + 12f;
            while (Time.realtimeSinceStartup < deadline && !backend.HasEnded) { backend.PollInto(ref tex); yield return null; }
            Assert.IsTrue(backend.HasEnded, "the helper should exit when nothing matches");
            StringAssert.Contains("no matching window", backend.Status);
        }

        [Test]
        public void PatternBackend_ProducesAnUprightRedTopLeftBlock()
        {
            var b = new PatternBackend();
            Texture2D tex = null;
            Assert.IsTrue(b.PollInto(ref tex));
            Assert.AreEqual(1920, tex.width);
            // Row 0 is the TOP of the picture and the top-left block is red (the pattern writes BGRA bytes so red really is red).
            var tl = tex.GetPixel(40, 40);
            var bl = tex.GetPixel(40, tex.height - 40);
            Assert.Greater(tl.r, 0.6f); Assert.Less(tl.b, 0.4f);
            Assert.Greater(bl.r, 0.6f); Assert.Greater(bl.g, 0.6f); Assert.Less(bl.b, 0.4f);        // bottom-left is yellow
            Object.DestroyImmediate(tex);
        }
    }
}
