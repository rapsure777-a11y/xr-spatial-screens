using System;
using System.Collections.Generic;
using UnityEngine;
using XrSpatial.Core;

namespace XrSpatial.Spatial
{
    public enum ToolMode { Idle, Place, Crop }

    /// <summary>Something drawn in front of the world that wants the pointer first (the palette). Return true (and the distance) when the ray is on it.</summary>
    public interface IUiLayer
    {
        bool HandlePointer(in PointerState s, out float hitDistance);
    }

    /// <summary>
    /// The interaction state machine. "Draw where the screen should exist": in Place mode each trigger press drops one of four points (at the laser tip, or at the
    /// controller itself in touch mode); the fourth closes the quad and creates the screen. In Idle mode corners can be dragged individually (they stay planar), a
    /// whole screen can be grabbed (grip) and moved/turned/scaled, and in Crop mode a rectangle dragged on a screen becomes its own panel.
    /// </summary>
    public sealed class SurfaceTool : MonoBehaviour
    {
        public SpatialWorkspace Workspace;
        public IPointerSource Pointer;
        public IUiLayer Ui;

        public ToolMode Mode { get; private set; } = ToolMode.Idle;
        public PanelView Selected { get; private set; }
        public bool InteractMode { get; set; }
        public bool TouchPlacement { get; set; }
        public int PlacedCount => m_Placed.Count;
        public string Message { get; private set; } = "";
        public Vector2 LastHitUv { get; private set; }
        public PanelView HoverPanel { get; private set; }
        public bool PointerOverUi { get; private set; }
        public event Action StateChanged;

        // Tuning
        public float PlaceDistance = 1.6f;
        public float CornerPickRadius = 0.045f;
        public float MinCornerDistance = 0.03f;
        /// <summary>New screens drawn as a roughly rectangular quad are fitted to the picture's aspect ratio (shrunk inside the drawn area) so the picture is not stretched; deliberate trapezoids are left as drawn.</summary>
        public bool AutoFitAspect = true;
        /// <summary>Panel sizing experiment. While on, dragging a corner keeps the picture's (or crop's) own aspect; off = the normal freeform corner drag.</summary>
        public bool LockAspect;
        /// <summary>Set by the app: the headset's display pixels per degree at the centre of view, and the head position in the same space as the panel corners.</summary>
        public Func<float> HeadsetPpd;
        public Func<Vector3> HeadPosition;

        readonly List<Vector3> m_Placed = new List<Vector3>();
        float m_MessageUntil;

        enum Drag { None, Corner, Panel, Crop }
        Drag m_Drag;
        PanelView m_DragPanel;
        int m_DragCorner;
        float m_DragDist;
        Vector3 m_DragOffset;
        Vector3[] m_DragStart;
        Quaternion m_GrabRot0;
        Vector3 m_GrabTip0, m_GrabCentroid0;
        float m_GrabScale = 1f, m_GrabPush;
        Vector2 m_CropA, m_CropB;

        LineRenderer m_Laser, m_Preview, m_CropBox;
        Transform m_Reticle;
        Material m_LaserMat, m_PreviewMat, m_ReticleMat;
        PointerState m_State;

        // ------------------------------------------------------------------ setup

        void Awake()
        {
            m_LaserMat = Materials.NewUnlit(new Color(0.5f, 0.9f, 1f, 0.55f), false);
            m_PreviewMat = Materials.NewUnlit(new Color(0.4f, 1f, 0.5f, 0.95f), true);
            m_ReticleMat = Materials.NewUnlit(new Color(1f, 1f, 1f, 0.95f), true);
            m_Laser = NewLine("Laser", m_LaserMat, 0.0025f, 2);
            m_Preview = NewLine("PlacePreview", m_PreviewMat, 0.006f, 0);
            m_CropBox = NewLine("CropBox", m_PreviewMat, 0.005f, 4); m_CropBox.loop = true; m_CropBox.enabled = false;
            var r = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(r.GetComponent<Collider>());
            r.name = "Reticle"; r.transform.SetParent(transform, false); r.transform.localScale = Vector3.one * 0.02f;
            r.GetComponent<MeshRenderer>().sharedMaterial = m_ReticleMat;
            m_Reticle = r.transform;
        }

        LineRenderer NewLine(string name, Material mat, float width, int points)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var l = go.AddComponent<LineRenderer>();
            l.useWorldSpace = true; l.sharedMaterial = mat; l.widthMultiplier = width; l.positionCount = points;
            l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; l.numCornerVertices = 2; l.numCapVertices = 2;
            return l;
        }

        // ------------------------------------------------------------------ commands (palette, keyboard, tests)

        public void Say(string msg, float seconds = 3f) { Message = msg; m_MessageUntil = Time.unscaledTime + seconds; StateChanged?.Invoke(); }

        public void BeginPlace()
        {
            CancelDrag();
            if (Workspace.Layout.sources.Count == 0) { Say("Add a source first (desktop panel), or use the test pattern"); return; }
            m_Placed.Clear();
            Mode = ToolMode.Place;
            InteractMode = false;
            Say("Point where the TOP-LEFT corner goes and press the trigger (4 points, clockwise)", 6f);
            StateChanged?.Invoke();
        }

        public void BeginCrop()
        {
            CancelDrag(); m_Placed.Clear();
            Mode = ToolMode.Crop; InteractMode = false;
            Say("Drag a rectangle on a screen to cut out a new panel", 5f);
            StateChanged?.Invoke();
        }

        public void SetIdle() { CancelDrag(); m_Placed.Clear(); Mode = ToolMode.Idle; StateChanged?.Invoke(); }

        public void Select(PanelView p)
        {
            if (Selected == p) return;
            if (Selected) Selected.SetHighlight(PanelHighlight.None);
            Selected = p;
            if (Selected) Selected.SetHighlight(PanelHighlight.Selected);
            if (p && !string.IsNullOrEmpty(p.Def.sourceId)) Workspace.ActiveSourceId = p.Def.sourceId;
            StateChanged?.Invoke();
        }

        public void DeleteSelected()
        {
            if (!Selected) { Say("Select a screen first (point at it, trigger)"); return; }
            var p = Selected; Select(null); Workspace.RemoveSurface(p); Say("Screen deleted");
        }

        public void ToggleInteract()
        {
            InteractMode = !InteractMode;
            if (InteractMode) { SetIdle(); Select(null); }
            Say(InteractMode ? "Interact: trigger = click on the screen under the laser" : "Interact off: edit mode", 3f);
            StateChanged?.Invoke();
        }

        public void ToggleTouch() { TouchPlacement = !TouchPlacement; Say(TouchPlacement ? "Touch placement: points drop at the controller tip" : "Laser placement: points drop at the laser end", 3f); StateChanged?.Invoke(); }

        public void CycleSource()
        {
            var list = Workspace.Layout.sources;
            if (list.Count == 0) { Say("No sources yet"); return; }
            int i = list.FindIndex(s => s.id == Workspace.ActiveSourceId);
            Workspace.ActiveSourceId = list[(i + 1) % list.Count].id;
            if (Selected) { Selected.Def.sourceId = Workspace.ActiveSourceId; Selected.Rebind(Workspace.GetSource(Workspace.ActiveSourceId)); Workspace.Touch(Selected); }
            Say("Source: " + (list.Find(s => s.id == Workspace.ActiveSourceId)?.label ?? Workspace.ActiveSourceId), 3f);
            StateChanged?.Invoke();
        }

        public void RotateSelectedPicture()
        {
            if (!Selected) { Say("Select a screen first"); return; }
            Selected.Def.corners = QuadMath.RotateCorners(Selected.Def.corners, 1);
            Workspace.Touch(Selected);
            Say("Picture turned a quarter", 2f);
        }

        /// <summary>Reshapes the selected screen into a rectangle with its picture's true aspect ratio (shrinking inside the current extents).</summary>
        public void FitSelectedAspect()
        {
            if (!Selected) { Say("Select a screen first"); return; }
            var src = Selected.Source;
            if (src == null || !src.HasFrame) { Say("No picture yet to measure"); return; }
            float aspect = Selected.Def.crop.width * src.Aspect / Mathf.Max(0.001f, Selected.Def.crop.height);
            Selected.Def.corners = QuadMath.FitAspect(Selected.Def.corners, aspect);
            Workspace.Touch(Selected);
            Say("Fitted to the picture's shape", 2f);
        }

        /// <summary>The aspect (width / height) of what the panel shows: the crop region of the source picture. False while there is no picture to measure.</summary>
        static bool PanelAspect(PanelView p, out float aspect)
        {
            aspect = 0f;
            var src = p ? p.Source : null;
            if (src == null || !src.HasFrame) return false;
            aspect = p.Def.crop.width * src.Aspect / Mathf.Max(0.001f, p.Def.crop.height);
            return aspect > 0.05f;
        }

        /// <summary>Source pixels, panel size and angles for the selected panel, as one line (also used as the Labs readout).</summary>
        public bool SizingReport(out string text, out PanelSizing.Sharpness snap)
        {
            text = ""; snap = default;
            if (!Selected) { text = "Select a screen first"; return false; }
            var src = Selected.Source;
            if (src == null || !src.HasFrame) { text = "No picture yet to measure"; return false; }
            var c = Selected.Def.crop;
            float pw = c.width * src.Width, ph = c.height * src.Height;
            float ppd = HeadsetPpd != null ? HeadsetPpd() : 20f;
            var q = Selected.Def.corners;
            float dist = HeadPosition != null ? Vector3.Distance(HeadPosition(), QuadMath.Centroid(q)) : PlaceDistance;
            snap = PanelSizing.Compute(pw, ph, ppd, dist);
            var size = QuadMath.Size(q);
            float angW = 2f * Mathf.Atan(size.x * 0.5f / Mathf.Max(0.2f, dist)) * Mathf.Rad2Deg, angH = 2f * Mathf.Atan(size.y * 0.5f / Mathf.Max(0.2f, dist)) * Mathf.Rad2Deg;
            text = $"Source {src.Width}x{src.Height}, shown {pw:0}x{ph:0}px. Panel now {size.x:0.00}x{size.y:0.00} m at {dist:0.0} m = {angW:0}x{angH:0} deg, {PanelSizing.CentrePpd(pw, size.x, dist):0} px/deg (headset {ppd:0}). Sharp size {snap.widthM:0.00}x{snap.heightM:0.00} m = {snap.angWidthDeg:0}x{snap.angHeightDeg:0} deg.";
            return true;
        }

        /// <summary>Resizes the selected panel (about its centre, same plane and orientation) so its source pixels match the headset's pixels per degree. Nothing else changes.</summary>
        public void SnapSharpness()
        {
            if (!SizingReport(out string text, out var snap)) { Say(text); return; }
            Selected.Def.corners = PanelSizing.WithSize(Selected.Def.corners, snap.widthM, snap.heightM);
            Workspace.Touch(Selected);
            Say($"Sharp size {snap.widthM:0.00}x{snap.heightM:0.00} m, {snap.sourcePpd:0} px/deg" + (snap.clamped ? " (limited)" : "") + ". Details in Labs.", 8f);
        }

        public void ToggleLockAspect() { LockAspect = !LockAspect; Say(LockAspect ? "Aspect locked: corner drags keep the picture's shape" : "Freeform: corners move freely", 3f); }

        public void ResetSelectedCrop()
        {
            if (!Selected) return;
            Selected.Def.crop = new Rect(0, 0, 1, 1);
            Workspace.Touch(Selected);
        }

        /// <summary>Test/automation entry: creates a screen directly from four points as if they had been placed (same validation and ordering).</summary>
        public PanelView CreateFromPoints(IList<Vector3> points, Vector3 viewer, string sourceId = null)
        {
            var q = QuadMath.Planarise(QuadMath.OrderForViewer(points, viewer));
            if (!QuadMath.IsValidQuad(q, MinCornerDistance)) { Say("Those four points do not make a flat, convex screen. Try again."); return null; }
            var live = Workspace.GetSource(sourceId ?? Workspace.ActiveSourceId);
            if (AutoFitAspect && live != null && live.HasFrame && QuadMath.IsNearParallelogram(q)) q = QuadMath.FitAspect(q, live.Aspect);
            var p = Workspace.AddSurface(sourceId ?? Workspace.ActiveSourceId, q);
            Select(p);
            return p;
        }

        void CancelDrag()
        {
            m_Drag = Drag.None; m_DragPanel = null;
            m_CropBox.enabled = false;
        }

        // ------------------------------------------------------------------ frame

        void Update()
        {
            if (Pointer == null || Workspace == null) return;
            m_State = Pointer.Poll();
            var s = m_State;
            InteractLive = false; InteractPanel = null; InteractTracking = s.valid; InteractState = s;       // the forwarder sees fresh state every frame, never a stale hover
            if (!s.valid) { m_Laser.enabled = false; m_Reticle.gameObject.SetActive(false); return; }
            m_Laser.enabled = true; m_Reticle.gameObject.SetActive(true);

            PointerOverUi = false; float uiDist = float.MaxValue;
            if (Ui != null && m_Drag == Drag.None) PointerOverUi = Ui.HandlePointer(s, out uiDist);

            bool consumed = false;
            if (!PointerOverUi)
            {
                switch (Mode)
                {
                    case ToolMode.Place: UpdatePlace(s); consumed = true; break;
                    case ToolMode.Crop: UpdateCrop(s); consumed = true; break;
                }
                if (!consumed) UpdateIdle(s);
            }
            else { HoverPanel = null; ClearHover(); }

            DrawLaser(s, PointerOverUi ? uiDist : -1f);
            if (!string.IsNullOrEmpty(Message) && Time.unscaledTime > m_MessageUntil) { Message = ""; StateChanged?.Invoke(); }
        }

        // ------------------------------------------------------------------ idle: hover, select, drag corners, grab panels

        void ClearHover()
        {
            foreach (var p in Workspace.Panels) if (p && p != Selected) p.SetHighlight(PanelHighlight.None);
            if (Selected) Selected.SetHighlight(PanelHighlight.Selected);
        }

        void UpdateIdle(PointerState s)
        {
            var ray = s.ray;
            if (m_Drag == Drag.Corner) { DragCorner(s); return; }
            if (m_Drag == Drag.Panel) { GrabPanel(s); return; }

            // Hover: nearest corner (within a pick radius that grows with distance) or the nearest panel hit.
            PanelView hover = null; int hoverCorner = -1; float bestAlong = float.MaxValue; Vector2 hoverUv = default; float hoverDist = float.MaxValue;
            foreach (var p in Workspace.Panels)
            {
                if (!p || !p.Def.visible) continue;
                int c = QuadMath.NearestCornerToRay(ray, p.Def.corners, CornerPickRadius + 0.02f * 1f, out float along);
                if (c >= 0 && !InteractMode && along < bestAlong) { hover = p; hoverCorner = c; bestAlong = along; hoverDist = along; }
                if (hoverCorner < 0 && p.Raycast(ray, out var uv, out float d) && d < hoverDist) { hover = p; hoverUv = uv; hoverDist = d; }
            }
            HoverPanel = hover;
            if (hover && hoverCorner < 0) LastHitUv = hoverUv;
            ClearHover();
            if (hover && hover != Selected) hover.SetHighlight(PanelHighlight.Hover, hoverCorner);
            else if (hover) hover.SetHighlight(PanelHighlight.Selected, hoverCorner);

            if (InteractMode) { UpdateInteract(s, hover, hoverUv); return; }

            if (s.triggerDown)
            {
                if (hover && hoverCorner >= 0)
                {
                    Select(hover);
                    m_Drag = Drag.Corner; m_DragPanel = hover; m_DragCorner = hoverCorner;
                    m_DragDist = Vector3.Dot(hover.Def.corners[hoverCorner] - ray.origin, ray.direction);
                    m_DragOffset = hover.Def.corners[hoverCorner] - (ray.origin + ray.direction * m_DragDist);
                    m_DragStart = (Vector3[])hover.Def.corners.Clone();
                    Pointer.Haptic(0.3f, 0.04f);
                }
                else if (hover) { Select(hover); Pointer.Haptic(0.2f, 0.03f); }
                else Select(null);
            }
            if (s.gripDown && hover)
            {
                Select(hover);
                m_Drag = Drag.Panel; m_DragPanel = hover;
                m_DragStart = (Vector3[])hover.Def.corners.Clone();
                m_GrabCentroid0 = QuadMath.Centroid(m_DragStart);
                float d = hoverDist < float.MaxValue ? hoverDist : Vector3.Dot(m_GrabCentroid0 - ray.origin, ray.direction);
                m_DragDist = Mathf.Max(0.3f, d);
                m_GrabTip0 = ray.origin + ray.direction * m_DragDist;
                m_GrabRot0 = s.rotation; m_GrabScale = 1f; m_GrabPush = 0f;
                Pointer.Haptic(0.3f, 0.05f);
            }
            if (s.secondaryDown && Selected) { RotateSelectedPicture(); }
        }

        void DragCorner(PointerState s)
        {
            var ray = s.ray;
            if (s.triggerUp || !s.triggerHeld) { FinishCornerDrag(); return; }
            m_DragDist = Mathf.Clamp(m_DragDist + s.stick.y * 1.5f * Time.deltaTime, 0.2f, 12f);
            var target = ray.origin + ray.direction * m_DragDist + m_DragOffset;
            var q = (Vector3[])m_DragPanel.Def.corners.Clone();
            int i = m_DragCorner;
            // Keep the quad flat: the dragged corner is constrained to the plane through the other three.
            Vector3 a = q[(i + 1) % 4], b = q[(i + 2) % 4], c = q[(i + 3) % 4];
            Vector3 n = Vector3.Cross(a - b, c - b);
            if (n.sqrMagnitude > 1e-8f) { n.Normalize(); target -= n * Vector3.Dot(target - b, n); }
            q[i] = target;
            if (LockAspect && PanelAspect(m_DragPanel, out float lockAspect)) q = PanelSizing.ResizeCornerLocked(m_DragStart, i, ray.origin + ray.direction * m_DragDist + m_DragOffset, lockAspect);
            if (QuadMath.IsValidQuad(q, MinCornerDistance)) { m_DragPanel.Def.corners = q; m_DragPanel.Rebuild(); }
        }

        void FinishCornerDrag()
        {
            if (m_DragPanel)
            {
                m_DragPanel.Def.corners = QuadMath.Planarise(m_DragPanel.Def.corners);
                if (!QuadMath.IsValidQuad(m_DragPanel.Def.corners, MinCornerDistance)) m_DragPanel.Def.corners = m_DragStart;
                Workspace.Touch(m_DragPanel);
            }
            CancelDrag();
        }

        void GrabPanel(PointerState s)
        {
            var ray = s.ray;
            if (s.gripUp || !s.gripHeld)
            {
                if (m_DragPanel) { m_DragPanel.Def.corners = QuadMath.Planarise(m_DragPanel.Def.corners); Workspace.Touch(m_DragPanel); }
                CancelDrag(); return;
            }
            m_DragDist = Mathf.Clamp(m_DragDist + s.stick.y * 1.6f * Time.deltaTime, 0.25f, 14f);
            m_GrabScale = Mathf.Clamp(m_GrabScale * (1f + s.stick.x * 1.2f * Time.deltaTime), 0.2f, 5f);
            var tip = ray.origin + ray.direction * m_DragDist;
            var rot = s.rotation * Quaternion.Inverse(m_GrabRot0);          // how far the hand has turned since the grab
            var q = new Vector3[4];
            for (int i = 0; i < 4; i++)
                q[i] = m_GrabCentroid0 + (tip - m_GrabTip0) + rot * ((m_DragStart[i] - m_GrabCentroid0) * m_GrabScale);
            if (QuadMath.IsValidQuad(q, MinCornerDistance)) { m_DragPanel.Def.corners = q; m_DragPanel.Rebuild(); }
        }

        // ------------------------------------------------------------------ interact (forward clicks to the captured app)

        public bool WantsClick { get; private set; }
        public PanelView InteractPanel { get; private set; }
        public Vector2 InteractUv { get; private set; }
        public PointerState InteractState { get; private set; }
        /// <summary>The pointer is tracked this frame (a lost controller is not).</summary>
        public bool InteractTracking { get; private set; }
        /// <summary>The pointer is tracked and in the scene (not over the palette) this frame, so InteractPanel/InteractUv are current.</summary>
        public bool InteractLive { get; private set; }

        void UpdateInteract(PointerState s, PanelView hover, Vector2 uv)
        {
            InteractLive = true;
            InteractPanel = hover && hover.Def.interactive && hover.Source != null ? hover : null;
            InteractUv = uv; InteractState = s;
            if (s.gripDown && hover) { InteractMode = false; Select(hover); StateChanged?.Invoke(); }
        }

        // ------------------------------------------------------------------ place four points

        Vector3 PlacementPoint(PointerState s)
        {
            if (TouchPlacement) return s.ray.origin + s.ray.direction * 0.08f;       // the controller's tip
            // Snap to the first existing panel under the laser (so points can lie on another screen), otherwise a free point at the adjustable distance.
            float best = float.MaxValue; Vector3 snap = default; bool snapped = false;
            foreach (var p in Workspace.Panels)
                if (p && p.Raycast(s.ray, out _, out float d) && d < best) { best = d; snap = s.ray.origin + s.ray.direction * d; snapped = true; }
            return snapped && best < PlaceDistance + 1.5f ? snap : s.ray.origin + s.ray.direction * PlaceDistance;
        }

        void UpdatePlace(PointerState s)
        {
            if (Mathf.Abs(s.stick.y) > 0.2f) PlaceDistance = Mathf.Clamp(PlaceDistance + s.stick.y * 1.5f * Time.deltaTime, 0.3f, 12f);
            var point = PlacementPoint(s);
            m_Reticle.position = point;
            // Preview polyline: placed points, then the live point.
            m_Preview.enabled = true;
            m_Preview.positionCount = m_Placed.Count + 1;
            for (int i = 0; i < m_Placed.Count; i++) m_Preview.SetPosition(i, m_Placed[i]);
            m_Preview.SetPosition(m_Placed.Count, point);
            if (s.secondaryDown || s.menuDown)
            {
                if (m_Placed.Count > 0) { m_Placed.RemoveAt(m_Placed.Count - 1); Say("Removed the last point", 2f); }
                else SetIdle();
                return;
            }
            if (!s.triggerDown) return;
            if (m_Placed.Count > 0 && (point - m_Placed[m_Placed.Count - 1]).magnitude < MinCornerDistance) { Say("Move away from the last point", 2f); return; }
            m_Placed.Add(point);
            Pointer.Haptic(0.5f, 0.06f);
            string[] names = { "top-left", "top-right", "bottom-right", "bottom-left" };
            if (m_Placed.Count < 4) { Say($"Point {m_Placed.Count} placed. Next: {names[m_Placed.Count]}", 5f); StateChanged?.Invoke(); return; }

            var viewer = Pointer.Head ? Pointer.Head.position : s.ray.origin;
            var placed = new List<Vector3>(m_Placed);
            m_Placed.Clear(); m_Preview.positionCount = 0; m_Preview.enabled = false;
            var panel = CreateFromPoints(placed, viewer);
            Mode = ToolMode.Idle;
            if (panel) Say("Screen created. Drag its corners or grip to move it.", 4f);
            StateChanged?.Invoke();
        }

        // ------------------------------------------------------------------ crop

        void UpdateCrop(PointerState s)
        {
            PanelView hover = null; Vector2 uv = default; float best = float.MaxValue;
            foreach (var p in Workspace.Panels)
                if (p && p.Raycast(s.ray, out var u, out float d) && d < best) { hover = p; uv = u; best = d; }
            HoverPanel = hover;
            ClearHover();
            if (hover) hover.SetHighlight(PanelHighlight.Hover);
            if (m_Drag != Drag.Crop)
            {
                if (s.triggerDown && hover) { m_Drag = Drag.Crop; m_DragPanel = hover; m_CropA = hover.UvToSource(uv); m_CropB = m_CropA; Pointer.Haptic(0.3f, 0.04f); }
                if (s.secondaryDown || s.menuDown) SetIdle();
                return;
            }
            // Dragging: follow the laser on the same panel plane (clamped to the picture).
            if (QuadMath.RayToUv(s.ray, m_DragPanel.Def.corners, out var cuv, out _, false)) m_CropB = m_DragPanel.UvToSource(new Vector2(Mathf.Clamp01(cuv.x), Mathf.Clamp01(cuv.y)));
            var crop = QuadMath.CropFromPoints(ClampToCrop(m_CropA, m_DragPanel.Def.crop), ClampToCrop(m_CropB, m_DragPanel.Def.crop));
            DrawCropBox(m_DragPanel, crop);
            if (s.triggerUp || !s.triggerHeld)
            {
                m_CropBox.enabled = false;
                var parent = m_DragPanel;
                m_Drag = Drag.None; m_DragPanel = null;
                if (crop.width < 0.02f || crop.height < 0.02f) { Say("That rectangle is too small", 2f); return; }
                CreateCropPanel(parent, crop);
                Mode = ToolMode.Idle;
                StateChanged?.Invoke();
            }
        }

        static Vector2 ClampToCrop(Vector2 src, Rect parentCrop) => new Vector2(Mathf.Clamp(src.x, parentCrop.xMin, parentCrop.xMax), Mathf.Clamp(src.y, parentCrop.yMin, parentCrop.yMax));

        void DrawCropBox(PanelView p, Rect crop)
        {
            m_CropBox.enabled = true;
            var c = new[] { new Vector2(crop.xMin, crop.yMin), new Vector2(crop.xMax, crop.yMin), new Vector2(crop.xMax, crop.yMax), new Vector2(crop.xMin, crop.yMax) };
            for (int i = 0; i < 4; i++)
                m_CropBox.SetPosition(i, QuadMath.UvToWorld(QuadMath.SourceToPanelUv(c[i], p.Def.crop), p.Def.corners) + QuadMath.FrontNormal(p.Def.corners) * 0.004f);
        }

        /// <summary>Creates an independent panel showing <paramref name="crop"/> of the parent's source, the same physical scale as the parent, placed just above it.</summary>
        public PanelView CreateCropPanel(PanelView parent, Rect crop)
        {
            var src = parent.Source;
            float srcAspect = src != null ? src.Aspect : 16f / 9f;
            var pq = parent.Def.corners;
            var psize = QuadMath.Size(pq);
            // World metres per source-x unit on the parent; keep that scale (clamped) so text stays the same physical size.
            float metresPerSrcX = psize.x / Mathf.Max(0.05f, parent.Def.crop.width);
            float w = Mathf.Clamp(crop.width * metresPerSrcX, 0.15f, 3f);
            float aspect = crop.width * srcAspect / Mathf.Max(0.001f, crop.height);
            float h = w / Mathf.Max(0.1f, aspect);
            var right = (pq[QuadMath.TR] - pq[QuadMath.TL]).normalized;
            var up = (pq[QuadMath.TL] - pq[QuadMath.BL]).normalized;
            var n = QuadMath.FrontNormal(pq);
            var centre = QuadMath.Centroid(pq) + up * (psize.y * 0.5f + h * 0.5f + 0.06f) + n * 0.03f;
            var corners = QuadMath.RectAt(centre, right, up, w, h);
            var p = Workspace.AddSurface(parent.Def.sourceId, corners, crop, "crop");
            Select(p);
            Say("Cropped panel created above the original: grip to move it.", 4f);
            return p;
        }

        // ------------------------------------------------------------------ visuals

        void DrawLaser(PointerState s, float uiDistance)
        {
            float len = 3f; bool hit = false; Vector3 end;
            if (uiDistance > 0f) { len = uiDistance; hit = true; }
            else if (HoverPanel && Mode != ToolMode.Place && QuadMath.RayToUv(s.ray, HoverPanel.Def.corners, out _, out float d)) { len = d; hit = true; }
            end = s.ray.origin + s.ray.direction * len;
            if (Mode == ToolMode.Place) { end = PlacementPoint(s); }
            if (m_Drag == Drag.Corner) end = m_DragPanel.Def.corners[m_DragCorner];
            m_Laser.SetPosition(0, s.ray.origin + s.ray.direction * 0.03f);
            m_Laser.SetPosition(1, end);
            m_Reticle.position = end;
            m_Reticle.gameObject.SetActive(hit || Mode == ToolMode.Place || m_Drag != Drag.None);
            m_Reticle.localScale = Vector3.one * Mathf.Clamp(Vector3.Distance(end, s.ray.origin) * 0.008f, 0.01f, 0.05f);
            if (Mode != ToolMode.Place && m_Preview.enabled) { m_Preview.enabled = false; }
        }
    }
}
