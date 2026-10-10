using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using XrSpatial.App;
using XrSpatial.Core;
using XrSpatial.Spatial;

namespace XrSpatial.Tests
{
    /// <summary>A saved layout can remember many sources. Only the ones a screen uses (and the active one) may capture and decode on load; unused generated test patterns are dropped. (The headset ran at 26 fps with 23 remembered sources and 2 screens.)</summary>
    public class SourceStartupTests
    {
        SpatialApp m_App;
        string m_Dir;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            m_Dir = Path.Combine(Path.GetTempPath(), "xrss-startup-" + System.Guid.NewGuid().ToString("N"));
            m_App = SpatialApp.Create(new SpatialApp.Options { forceDesktop = true, showControlPanel = false, autoLoadLast = false, parseCommandLine = false, layoutDirectory = m_Dir, background = BackgroundMode.Void });
            yield return null;
            m_App.Workspace.AutoSave = false;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (m_App) Object.Destroy(m_App.gameObject);
            yield return null;
            if (Directory.Exists(m_Dir)) Directory.Delete(m_Dir, true);
        }

        [UnityTest]
        public IEnumerator Load_StartsOnlySourcesAScreenUses_DropsUnusedPatterns_AndKeepsRememberedWindows()
        {
            var ws = m_App.Workspace;
            var used = ws.AddSource(new SourceDef { kind = "pattern", label = "used" });
            var corners = new[] { new Vector3(-1, 1, 2), new Vector3(1, 1, 2), new Vector3(1, -1, 2), new Vector3(-1, -1, 2) };
            ws.AddSurface(used.id, corners);
            for (int i = 0; i < 6; i++) ws.AddSource(new SourceDef { kind = "pattern", label = "stray " + i });
            var win = ws.AddSource(new SourceDef { kind = "window", processName = "NoSuchApp", titleContains = "nothing", label = "remembered window" });
            Assert.AreEqual(8, ws.LiveSourceCount, "while editing, every added source runs");
            ws.SaveNow();
            yield return null;

            string key = ws.Layout.appKey;
            ws.LoadLayout(key);
            yield return null;
            Assert.IsNotNull(ws.Layout.FindSource(used.id), "the source a screen uses is kept");
            Assert.IsNull(ws.Layout.sources.Find(s => s.label == "stray 0"), "unused generated patterns are dropped");
            Assert.IsNotNull(ws.Layout.FindSource(win.id), "a remembered window source is kept in the layout");
            Assert.AreEqual(1, ws.LiveSourceCount, "only the source the screen uses is capturing");
            Assert.IsNotNull(ws.GetSource(win.id), "asking for a remembered source starts it on demand");
            Assert.AreEqual(2, ws.LiveSourceCount);
        }

        [UnityTest]
        public IEnumerator TestPatternButton_ReusesAnUnusedPattern_AndClearUnusedKeepsOnlyWhatScreensShow()
        {
            var ws = m_App.Workspace;
            var corners = new[] { new Vector3(-1, 1, 2), new Vector3(1, 1, 2), new Vector3(1, -1, 2), new Vector3(-1, -1, 2) };
            var shown = ws.AddSource(new SourceDef { kind = "pattern", label = "shown" });
            ws.AddSurface(shown.id, corners);
            int before = ws.Layout.sources.Count;
            var first = m_App.AddPattern();                                    // pattern nobody shows yet
            for (int i = 0; i < 5; i++) Assert.AreEqual(first.id, m_App.AddPattern().id, "pressing Test pattern again reuses the spare one");
            Assert.AreEqual(before + 1, ws.Layout.sources.Count);
            ws.AddSource(new SourceDef { kind = "window", processName = "NoSuchApp", titleContains = "x", label = "unused window" });
            yield return null;

            Assert.AreEqual(2, ws.ClearUnusedSources(), "the spare pattern and the unused window go");
            Assert.AreEqual(1, ws.Layout.sources.Count);
            Assert.AreEqual(shown.id, ws.Layout.sources[0].id);
            Assert.AreEqual(1, ws.Layout.surfaces.Count, "screens are never touched");
            Assert.AreEqual(shown.id, ws.ActiveSourceId, "the active source falls back to one that exists");
            Assert.AreEqual(0, ws.ClearUnusedSources(), "nothing left to clear");
        }
    }
}
