using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using XrSpatial.Capture;
using XrSpatial.Core;
using XrSpatial.Spatial;

namespace XrSpatial.App
{
    public enum BackgroundMode { Void, Grid, Passthrough }

    /// <summary>
    /// Builds the whole application at runtime (no scene dependencies): rig and camera, the workspace of screens, the editing tool, the palette, the desktop control
    /// panel and input forwarding. Chooses VR controllers when a headset is active and the simulated desktop pointer otherwise.
    /// Command line: --xrss-pattern | --xrss-process NAME [--xrss-title TEXT] | --xrss-monitor N | --xrss-layout KEY | --xrss-desktop | --xrss-quickscreen | --xrss-shot FILE
    /// </summary>
    public sealed partial class SpatialApp : MonoBehaviour
    {
        [Serializable]
        public class Options
        {
            public bool forceDesktop;
            public bool showControlPanel = true;
            public bool autoLoadLast = true;
            public bool parseCommandLine = true;
            public string layoutDirectory;
            public BackgroundMode background = BackgroundMode.Grid;
        }

        public Options Settings = new Options();
        public static SpatialApp Instance { get; private set; }
        public SpatialWorkspace Workspace { get; private set; }
        public SurfaceTool Tool { get; private set; }
        public PalettePanel Palette { get; private set; }
        public KeyboardPanel VirtualKeyboard { get; private set; }
        public ControlPanel Control { get; private set; }
        public InputForwarder Forwarder { get; private set; }
        public Camera Cam { get; private set; }
        public Transform XrOrigin { get; private set; }
        public IPointerSource Pointer { get; private set; }
        public bool XrActive { get; private set; }

        string m_ShotPath; float m_ShotDelay = 5f, m_QuickCreatedAt;
        bool m_QuickPending; float m_QuickDeadline;
        GameObject m_Grid;

        /// <summary>Creates the application with explicit options (tests, tools). Awake/Start run with the options already set.</summary>
        public static SpatialApp Create(Options options)
        {
            var go = new GameObject("XrSpatialApp");
            go.SetActive(false);
            var app = go.AddComponent<SpatialApp>();
            app.Settings = options ?? new Options();
            go.SetActive(true);
            return app;
        }

        void Awake()
        {
            if (Instance && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            // Headset build (native Android app on the Steam Frame): the windows come from the PC host over the network and the room is the passthrough background.
            if (Application.platform == RuntimePlatform.Android) { RemoteHost.Enable(); Settings.showControlPanel = false; Settings.background = BackgroundMode.Passthrough; }
            if (Settings.parseCommandLine && Array.IndexOf(Environment.GetCommandLineArgs(), "--xrss-desktop") >= 0) Settings.forceDesktop = true;
            Application.runInBackground = true;
            Application.targetFrameRate = -1;
            DontDestroyOnLoad(gameObject);
            if (Settings.forceDesktop) StopXr();
            BuildRig();
        }

        void Start()
        {
            XrActive = !Settings.forceDesktop && XRSettings.isDeviceActive && XRGeneralSettings.Instance != null && XRGeneralSettings.Instance.Manager.activeLoader != null;
            if (XrActive) SetFloorTracking();
            // Text on virtual monitors needs more pixels than the runtime's default eye resolution gives: render a little above it (headset build only).
            if (XrActive && Application.platform == RuntimePlatform.Android) { XRSettings.eyeTextureResolutionScale = 1.25f; Debug.Log($"[XrSpatial] eye texture scale 1.25 -> {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}"); }
            BuildWorld();
            ApplyCommandLine();
        }

        /// <summary>Shuts the OpenXR loader down so the flat window shows this camera (not the headset mirror) even when a headset is connected.</summary>
        static void StopXr()
        {
            var m = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
            if (m != null && m.isInitializationComplete) { m.StopSubsystems(); m.DeinitializeLoader(); }
        }

        /// <summary>Floor-relative tracking: y = 0 is the floor, so saved layouts keep their height across sessions (falls back to the device origin if the runtime refuses).</summary>
        void SetFloorTracking()
        {
            var subs = new List<XRInputSubsystem>();
            SubsystemManager.GetSubsystems(subs);
            foreach (var s in subs)
            {
                if (s.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor)) continue;
                Debug.Log("[XrSpatial] floor tracking origin not available; using the device origin (layouts are head-relative)");
            }
        }

        // ------------------------------------------------------------------ rig

        void BuildRig()
        {
            var origin = new GameObject("XROrigin").transform;
            origin.SetParent(transform, false);
            XrOrigin = origin;
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.transform.SetParent(origin, false);
            camGo.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            Cam = camGo.AddComponent<Camera>();
            Cam.nearClipPlane = 0.05f; Cam.farClipPlane = 60f;
            Cam.clearFlags = CameraClearFlags.SolidColor; Cam.backgroundColor = new Color(0.01f, 0.012f, 0.02f, 0f);
            if (Application.platform == RuntimePlatform.Android) Cam.backgroundColor = Color.clear;       // transparent black: the Frame's compositor shows the room wherever alpha is 0
            Cam.allowMSAA = true; Cam.allowHDR = false;
            camGo.AddComponent<AudioListener>();
            var data = Cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = false; data.antialiasing = AntialiasingMode.None;
            if (Settings.forceDesktop) { Cam.stereoTargetEye = StereoTargetEyeMask.None; Cam.fieldOfView = 65f; }      // flat window, not the headset
        }

        void BuildWorld()
        {
            Workspace = SpatialWorkspace.Create(XrOrigin);
            if (!string.IsNullOrEmpty(Settings.layoutDirectory)) Workspace.LayoutDirectory = Settings.layoutDirectory;
            Tool = new GameObject("SurfaceTool").AddComponent<SurfaceTool>();
            Tool.transform.SetParent(XrOrigin, false);
            Tool.Workspace = Workspace;

            if (XrActive) Pointer = XrPointerSource.Create(XrOrigin, Cam);
            else { var dp = DesktopPointerSource.Create(Cam); Pointer = dp; dp.Blocker = p => Control != null && Control.Visible && Control.ContainsScreenPoint(p); }
            Tool.Pointer = Pointer;

            Palette = PalettePanel.Create(XrOrigin, Tool, Workspace, Pointer);
            Palette.OnAddPattern = () => AddPattern();
            Palette.OnPickWindow = w => UseWindow(w);
            Palette.OnPickMonitor = i => AddMonitor(i);
            VirtualKeyboard = KeyboardPanel.Create(XrOrigin, Tool, Pointer);
            Palette.OnToggleKeyboard = () => VirtualKeyboard.Toggle();
            Tool.Ui = new UiLayers(VirtualKeyboard, Palette);                              // the keyboard sits in front of the palette
            Forwarder =new GameObject("InputForwarder").AddComponent<InputForwarder>();
            Forwarder.transform.SetParent(XrOrigin, false);
            Forwarder.Tool = Tool;
            if (RemoteHost.Active)
            {
                Forwarder.Enabled = false;                                           // the Windows forwarder cannot run here; the PC host injects the input
                var remote = new GameObject("RemoteInputForwarder").AddComponent<RemoteInputForwarder>();
                remote.transform.SetParent(XrOrigin, false);
                remote.Tool = Tool;
                new GameObject("HeadsetKeyboard").AddComponent<HeadsetKeyboardForwarder>().transform.SetParent(XrOrigin, false);       // a keyboard connected to the Frame types into the last clicked window
            }
            if (Settings.showControlPanel) { Control = gameObject.AddComponent<ControlPanel>(); Control.App = this; }
            SetBackground(Settings.background);

            if (Settings.autoLoadLast) Workspace.LoadLayout(PlayerPrefs.GetString("xrss.lastkey", "default"));
            else Workspace.LoadLayout("default");
        }

        // ------------------------------------------------------------------ background (void / grid / passthrough-ready)

        public void SetBackground(BackgroundMode mode)
        {
            Settings.background = mode;
            if (m_Grid) { Destroy(m_Grid); m_Grid = null; }
            if (mode == BackgroundMode.Passthrough)
            {
                // Hook for MR: an OpenXR environment blend mode / passthrough layer. The render path already uses a transparent clear colour and opaque panels, so
                // enabling the runtime's passthrough later requires no change to panels. Not implemented (Steam Frame passthrough APIs are deliberately not a dependency).
                if (Application.platform == RuntimePlatform.Android) Debug.Log("[XrSpatial] Passthrough background: the camera clears to transparent and the OpenXR alpha-blend mode is requested (PassthroughFeature); the Frame's compositor shows the room.");
                else Debug.Log("[XrSpatial] Passthrough background requested: not available to a streamed PC OpenXR app on this runtime (blend mode list is opaque only), using the void. See Docs/PASSTHROUGH.md.");
                return;
            }
            if (mode == BackgroundMode.Grid) m_Grid = BuildGrid();
        }

        GameObject BuildGrid()
        {
            var go = new GameObject("FloorGrid");
            go.transform.SetParent(XrOrigin, false);
            var verts = new List<Vector3>(); var cols = new List<Color>(); var idx = new List<int>();
            const int n = 12; const float step = 1f;
            for (int i = -n; i <= n; i++)
            {
                float a = Mathf.Clamp01(1f - Mathf.Abs(i) / (float)n);
                var c = new Color(0.3f, 0.45f, 0.6f, 0.05f + 0.22f * a * a);
                var c0 = new Color(c.r, c.g, c.b, c.a * (i == 0 ? 2.2f : 1f));
                for (int k = 0; k < 2; k++)
                {
                    int b = verts.Count;
                    verts.Add(k == 0 ? new Vector3(-n * step, 0f, i * step) : new Vector3(i * step, 0f, -n * step));
                    verts.Add(k == 0 ? new Vector3(n * step, 0f, i * step) : new Vector3(i * step, 0f, n * step));
                    cols.Add(new Color(c0.r, c0.g, c0.b, c0.a * 0.2f)); cols.Add(new Color(c0.r, c0.g, c0.b, c0.a * 0.2f));
                    idx.Add(b); idx.Add(b + 1);
                }
            }
            var m = new Mesh { name = "Grid" };
            m.SetVertices(verts); m.SetColors(cols); m.SetIndices(idx, MeshTopology.Lines, 0);
            go.AddComponent<MeshFilter>().sharedMesh = m;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = Materials.NewUnlit(Color.white, false);
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        // ------------------------------------------------------------------ source and screen helpers

        public SourceDef AddPattern()
        {
            return Workspace.AddSource(new SourceDef { kind = "pattern", label = "Test pattern" });
        }

        public SourceDef AddMonitor(int index)
        {
            return Workspace.AddSource(new SourceDef { kind = "monitor", monitorIndex = index, label = "Monitor " + index });
        }

        /// <summary>Capture a window the user picked. The layout is per application: choosing a game first adopts that game's saved layout (if any).</summary>
        public SourceDef UseWindow(CapturableWindow w)
        {
            string key = string.IsNullOrEmpty(w.process) ? w.title : w.process;
            var ws = Workspace;
            if (ws.Layout.surfaces.Count == 0 && ws.Layout.appKey != key)
            {
                var existing = Layout.Load(ws.LayoutDirectory, key);
                if (existing != null && existing.sources.Exists(s => s.kind == "window" && string.Equals(s.processName, w.process, StringComparison.OrdinalIgnoreCase)))
                {
                    ws.LoadLayout(key); PlayerPrefs.SetString("xrss.lastkey", key);
                    return ws.Layout.sources.Count > 0 ? ws.Layout.sources[0] : null;
                }
                ws.Layout.appKey = key;
            }
            PlayerPrefs.SetString("xrss.lastkey", ws.Layout.appKey);
            // Reuse an existing source for the same process/title instead of duplicating it.
            var dup = ws.Layout.sources.Find(s => s.kind == "window" && string.Equals(s.processName, w.process, StringComparison.OrdinalIgnoreCase) && s.titleContains == ShortTitle(w.title));
            if (dup != null) { ws.ActiveSourceId = dup.id; return dup; }
            return ws.AddSource(new SourceDef { kind = "window", processName = w.process, titleContains = ShortTitle(w.title), label = w.process + ": " + ShortTitle(w.title) });
        }

        /// <summary>A stable fragment of a window title (titles often contain changing document names/counters).</summary>
        static string ShortTitle(string t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            int cut = t.IndexOfAny(new[] { '-', '|', '—', '–' });
            return (cut > 3 ? t.Substring(0, cut) : t).Trim();
        }

        /// <summary>A rectangle in front of the head with the active source's aspect, for trying things without drawing four points.</summary>
        public PanelView AddQuickScreen()
        {
            if (Workspace.Layout.sources.Count == 0) AddPattern();
            var src = Workspace.GetSource(Workspace.ActiveSourceId);
            float aspect = src != null ? src.Aspect : 16f / 9f;
            var head = Cam.transform;
            var q = QuadMath.RectInFront(XrOrigin.InverseTransformPoint(head.position), XrOrigin.InverseTransformDirection(head.forward), Vector3.up, 1.8f, 1.8f, aspect);
            var p = Workspace.AddSurface(Workspace.ActiveSourceId, q);
            Tool.Select(p);
            return p;
        }

        // ------------------------------------------------------------------ command line

        void ApplyCommandLine()
        {
            if (!Settings.parseCommandLine) return;
            var a = Environment.GetCommandLineArgs();
            string Arg(string n) { int i = Array.IndexOf(a, n); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
            bool Has(string n) => Array.IndexOf(a, n) >= 0;
            string layout = Arg("--xrss-layout");
            if (layout != null) Workspace.LoadLayout(layout);
            if (Has("--xrss-ephemeral")) { Workspace.LoadLayout("ephemeral-" + Guid.NewGuid().ToString("N")); Workspace.AutoSave = false; }    // empty layout, nothing saved: for scripted tests
            if (Has("--xrss-pattern")) AddPattern();
            string proc = Arg("--xrss-process"), title = Arg("--xrss-title");
            if (proc != null || title != null) Workspace.AddSource(new SourceDef { kind = "window", processName = proc, titleContains = title, label = proc ?? title });
            string mon = Arg("--xrss-monitor");
            if (mon != null && int.TryParse(mon, out int mi)) AddMonitor(mi);
            if (Has("--xrss-demo"))
            {
                var src = AddPattern();
                Cam.fieldOfView = Settings.forceDesktop ? 85f : Cam.fieldOfView;
                Workspace.AutoSave = false;
                DemoLayout.Build(Workspace, src.id, 1920f / 1080f, XrOrigin.InverseTransformPoint(Cam.transform.position));
            }
            if (Has("--xrss-quickscreen")) { m_QuickPending = true; m_QuickDeadline = Time.realtimeSinceStartup + 8f; }
            if (Arg("--xrss-selftest-click") is string stc)
            {
                var parts = stc.Split(',');
                if (parts.Length == 2 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float tsx) && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float tsy))
                { m_QuickPending = true; m_QuickDeadline = Time.realtimeSinceStartup + 10f; StartCoroutine(SelfTestClick(tsx, tsy)); }
            }
            m_ShotPath = Arg("--xrss-shot");
            if (Arg("--xrss-shot-delay") is string sf && float.TryParse(sf, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float n)) m_ShotDelay = n;
        }

        // ------------------------------------------------------------------ frame

        void Update()
        {
            if (!XrActive) DesktopHotkeys();
            if (m_QuickPending)
            {
                // Wait for the first frame so the screen gets the captured window's real aspect ratio.
                var live = Workspace != null ? Workspace.GetSource(Workspace.ActiveSourceId) : null;
                if (live != null && live.HasFrame || Time.realtimeSinceStartup > m_QuickDeadline) { m_QuickPending = false; AddQuickScreen(); m_QuickCreatedAt = Time.realtimeSinceStartup; }

            }
            if (m_ShotPath != null && Time.realtimeSinceStartup > m_ShotDelay && !m_QuickPending && Time.realtimeSinceStartup > m_QuickCreatedAt + 1.2f)
            {
                string path = m_ShotPath; m_ShotPath = null;
                StartCoroutine(Shot(path));
            }
        }

        System.Collections.IEnumerator Shot(string path)
        {
            yield return new WaitForEndOfFrame();
            Debug.Log($"[XrSpatial] state: xr={XrActive} panels={Workspace.Panels.Count} sources={Workspace.Layout.sources.Count} cam={Cam.transform.position} rot={Cam.transform.eulerAngles} fov={Cam.fieldOfView} screen={Screen.width}x{Screen.height} frames={Time.frameCount} t={Time.realtimeSinceStartup:0.0}s");
            foreach (var p in Workspace.Panels) Debug.Log($"[XrSpatial] panel {p.Def.id} corners={p.Def.corners[0]} {p.Def.corners[2]} src={p.Source?.Status} frame={p.Source?.FrameCount} hasTex={p.Source?.View != null}");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            ScreenCapture.CaptureScreenshot(Path.GetFullPath(path));
            yield return null; yield return null;
            Debug.Log("[XrSpatial] screenshot written: " + path);
            Application.Quit();
        }

        void DesktopHotkeys()
        {
            var kb = Keyboard.current;
            if (kb == null || Tool == null || GUIUtility.keyboardControl != 0) return;   // not while typing in the control panel
            if (kb.nKey.wasPressedThisFrame) Tool.BeginPlace();
            if (kb.cKey.wasPressedThisFrame) Tool.BeginCrop();
            if (kb.xKey.wasPressedThisFrame || kb.deleteKey.wasPressedThisFrame) Tool.DeleteSelected();
            if (kb.iKey.wasPressedThisFrame) Tool.ToggleInteract();
            if (kb.rKey.wasPressedThisFrame) Tool.RotateSelectedPicture();
            if (kb.f1Key.wasPressedThisFrame && Control) Control.Visible = !Control.Visible;
            if (kb.ctrlKey.isPressed && kb.sKey.wasPressedThisFrame) Workspace.SaveNow();
        }

        void OnDestroy() { if (Instance == this) Instance = null; }
    }
}
