using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using XrSpatial.App;
using XrSpatial.Spatial;

namespace XrSpatial.Tests
{
    /// <summary>The wrist/floating palette must be clickable with the laser (it was not: the hit test divided by the canvas scale twice).</summary>
    public class PaletteTests
    {
        SpatialApp m_App;
        ScriptedPointer m_Ptr;
        string m_Dir;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            m_Dir = Path.Combine(Path.GetTempPath(), "xrss-palette-" + System.Guid.NewGuid().ToString("N"));
            m_App = SpatialApp.Create(new SpatialApp.Options { forceDesktop = true, showControlPanel = false, autoLoadLast = false, parseCommandLine = false, layoutDirectory = m_Dir, background = BackgroundMode.Void });
            yield return null;
            m_Ptr = new ScriptedPointer { HeadTransform = m_App.Cam.transform };
            m_App.Tool.Pointer = m_Ptr;                  // the real palette stays attached as the tool's UI layer
            m_App.Workspace.AutoSave = false;
            m_App.AddPattern();                          // placing needs a source
            yield return null; yield return null;       // let the palette place itself
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (m_App) Object.Destroy(m_App.gameObject);
            yield return null;
            if (Directory.Exists(m_Dir)) Directory.Delete(m_Dir, true);
        }

        IEnumerator Press(Vector3 target)
        {
            m_Ptr.Aim(m_App.Cam.transform.position, target);
            yield return null;                                                   // hover frame
            m_Ptr.State.triggerDown = true; m_Ptr.State.triggerHeld = true;
            yield return null;
            m_Ptr.State.triggerHeld = false; m_Ptr.State.triggerUp = true;
            yield return null;
        }

        [UnityTest]
        public IEnumerator PointingTheLaserAtANewScreenButtonAndPullingTheTrigger_StartsPlacing()
        {
            Assert.IsTrue(m_App.Palette.Visible);
            Assert.IsTrue(m_App.Palette.TryGetButtonWorldCenter("New screen", out var at), "palette button not found");
            Assert.AreEqual(ToolMode.Idle, m_App.Tool.Mode);
            yield return Press(at);
            Assert.AreEqual(ToolMode.Place, m_App.Tool.Mode, "the click on the palette button did nothing");
        }

        [UnityTest]
        public IEnumerator PointingAtTheEmptySpaceNextToThePalette_DoesNotPressAnything()
        {
            Assert.IsTrue(m_App.Palette.TryGetButtonWorldCenter("New screen", out var at));
            var off = at + m_App.Palette.transform.right * 0.8f;               // well outside the 0.31 m wide palette
            yield return Press(off);
            Assert.AreEqual(ToolMode.Idle, m_App.Tool.Mode);
        }
    }
}
