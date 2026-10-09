using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace XrSpatial.XR
{
    /// <summary>
    /// Fetches the real controller models from the OpenXR runtime: XR_EXT_interaction_render_model lists the models of the controllers in use, XR_EXT_render_model hands
    /// out each model as a binary glTF plus a space that follows the controller. Unity's OpenXR plugin wraps neither, so this calls them through xrGetInstanceProcAddr.
    /// Everything is optional: if an extension, a function or a model is missing, <see cref="Active"/> / <see cref="TryGetModel"/> simply say no and the app keeps its placeholder.
    /// The only intrusive part is wrapping xrWaitFrame (to read the predicted display time that xrLocateSpace needs); it calls straight through.
    /// </summary>
#if UNITY_EDITOR
    [UnityEditor.XR.OpenXR.Features.OpenXRFeature(UiName = "Controller render models (EXT)", BuildTargetGroups = new[] { BuildTargetGroup.Android },
        Company = "Gamebreak Labs", Desc = "Reads the controller models from the runtime (XR_EXT_render_model, XR_EXT_interaction_render_model).", Version = "0.1",
        OpenxrExtensionStrings = "XR_EXT_uuid XR_EXT_render_model XR_EXT_interaction_render_model", FeatureId = FeatureIdString)]
#endif
    public sealed class ControllerRenderModelFeature : OpenXRFeature
    {
        public const string FeatureIdString = "com.gamebreak.xrspatial.controllerrendermodel";
        public static string Report = "controller render models: feature not started";

        const int TypeCreateInfo = 1000300000, TypePropsGetInfo = 1000300001, TypeProps = 1000300002, TypeSpaceCreate = 1000300003,
                  TypeAssetCreate = 1000300006, TypeAssetDataGetInfo = 1000300007, TypeAssetData = 1000300008, TypeEnumIds = 1000301000, TypeTopPathGetInfo = 1000301003,
                  TypeSpaceLocation = 42;

        [StructLayout(LayoutKind.Sequential)] struct Uuid { public ulong a, b; }
        [StructLayout(LayoutKind.Sequential)] struct Posef { public float qx, qy, qz, qw, px, py, pz; }
        [StructLayout(LayoutKind.Sequential)] struct SpaceLocation { public int type; public IntPtr next; public ulong flags; public Posef pose; }
        [StructLayout(LayoutKind.Sequential)] struct CreateInfo { public int type; public IntPtr next; public ulong renderModelId; public uint gltfExtensionCount; public IntPtr gltfExtensions; }
        [StructLayout(LayoutKind.Sequential)] struct Header { public int type; public IntPtr next; }
        [StructLayout(LayoutKind.Sequential)] struct Props { public int type; public IntPtr next; public Uuid cacheId; public uint animatableNodeCount; }
        [StructLayout(LayoutKind.Sequential)] struct SpaceCreate { public int type; public IntPtr next; public ulong renderModel; }
        [StructLayout(LayoutKind.Sequential)] struct AssetCreate { public int type; public IntPtr next; public Uuid cacheId; }
        [StructLayout(LayoutKind.Sequential)] struct AssetData { public int type; public IntPtr next; public uint capacity; public uint count; public IntPtr buffer; }
        [StructLayout(LayoutKind.Sequential)] struct TopPathInfo { public int type; public IntPtr next; public uint count; public IntPtr paths; }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GipaFn(ulong instance, IntPtr name, out IntPtr fn);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int WaitFrameFn(ulong session, IntPtr info, IntPtr state);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int StringToPathFn(ulong instance, [MarshalAs(UnmanagedType.LPStr)] string s, out ulong path);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumIdsFn(ulong session, ref Header info, uint capacity, out uint count, [Out] ulong[] ids);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CreateModelFn(ulong session, ref CreateInfo info, out ulong model);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DestroyFn(ulong handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetPropsFn(ulong model, ref Header info, ref Props props);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CreateSpaceFn(ulong session, ref SpaceCreate info, out ulong space);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CreateAssetFn(ulong session, ref AssetCreate info, out ulong asset);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetAssetDataFn(ulong asset, ref Header info, ref AssetData data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetTopPathFn(ulong model, ref TopPathInfo info, out ulong path);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int LocateFn(ulong space, ulong baseSpace, long time, ref SpaceLocation loc);

        static ControllerRenderModelFeature s_Self;
        static GipaFn s_OrigGipa, s_HookGipa;
        static WaitFrameFn s_OrigWait, s_HookWait;
        static IntPtr s_HookGipaPtr, s_HookWaitPtr;
        static long s_DisplayTime;

        EnumIdsFn m_EnumIds; CreateModelFn m_CreateModel; DestroyFn m_DestroyModel, m_DestroyAsset; GetPropsFn m_GetProps; CreateSpaceFn m_CreateSpace; CreateAssetFn m_CreateAsset;
        GetAssetDataFn m_GetAssetData; GetTopPathFn m_GetTopPath; StringToPathFn m_StringToPath; LocateFn m_Locate;
        ulong m_Instance, m_Session, m_AppSpace, m_PathLeft, m_PathRight;
        bool m_Ready;

        sealed class HandModel { public ulong id, model, space; public byte[] glb; }
        readonly HandModel[] m_Hands = new HandModel[2];                  // 0 = left, 1 = right
        readonly List<ulong> m_AllIds = new List<ulong>();
        string m_Log = "";

        /// <summary>True when the extensions are on and every function was found (models may still be unavailable).</summary>
        public static bool Active => s_Self != null && s_Self.m_Ready;

        // ---- wrapping xrGetInstanceProcAddr just to see xrWaitFrame's predicted display time ----

        protected override IntPtr HookGetInstanceProcAddr(IntPtr func)
        {
            try
            {
                s_OrigGipa = Marshal.GetDelegateForFunctionPointer<GipaFn>(func);
                s_HookGipa = HookedGipa; s_HookWait = HookedWait;                          // static fields keep the delegates alive
                s_HookGipaPtr = Marshal.GetFunctionPointerForDelegate(s_HookGipa);
                s_HookWaitPtr = Marshal.GetFunctionPointerForDelegate(s_HookWait);
                return s_HookGipaPtr;
            }
            catch (Exception e) { Debug.Log("[XrSpatial] render model: could not wrap xrGetInstanceProcAddr (" + e.Message + ")"); return func; }
        }

        [AOT.MonoPInvokeCallback(typeof(GipaFn))]
        static int HookedGipa(ulong instance, IntPtr name, out IntPtr fn)
        {
            int r = s_OrigGipa(instance, name, out fn);
            try
            {
                if (r >= 0 && fn != IntPtr.Zero && Marshal.PtrToStringAnsi(name) == "xrWaitFrame")
                {
                    s_OrigWait = Marshal.GetDelegateForFunctionPointer<WaitFrameFn>(fn);
                    fn = s_HookWaitPtr;
                }
            }
            catch { /* leave fn as the runtime gave it */ }
            return r;
        }

        [AOT.MonoPInvokeCallback(typeof(WaitFrameFn))]
        static int HookedWait(ulong session, IntPtr info, IntPtr state)
        {
            int r = s_OrigWait(session, info, state);
            if (r >= 0 && state != IntPtr.Zero) s_DisplayTime = Marshal.ReadInt64(state, 16);      // XrFrameState: type, next, predictedDisplayTime
            return r;
        }

        // ---- lifecycle ----

        T Fn<T>(string name) where T : class
        {
            var buf = Marshal.StringToHGlobalAnsi(name);
            try
            {
                if (s_OrigGipa(m_Instance, buf, out var p) < 0 || p == IntPtr.Zero) return null;
                return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        protected override bool OnInstanceCreate(ulong xrInstance)
        {
            s_Self = this; m_Instance = xrInstance; m_Ready = false;
            try
            {
                bool a = OpenXRRuntime.IsExtensionEnabled("XR_EXT_render_model"), b = OpenXRRuntime.IsExtensionEnabled("XR_EXT_interaction_render_model");
                if (s_OrigGipa == null || !a || !b) { Report = $"controller render models: extensions not enabled (render_model {a}, interaction_render_model {b})"; Debug.Log("[XrSpatial] " + Report); return true; }
                m_StringToPath = Fn<StringToPathFn>("xrStringToPath"); m_EnumIds = Fn<EnumIdsFn>("xrEnumerateInteractionRenderModelIdsEXT");
                m_CreateModel = Fn<CreateModelFn>("xrCreateRenderModelEXT"); m_DestroyModel = Fn<DestroyFn>("xrDestroyRenderModelEXT"); m_GetProps = Fn<GetPropsFn>("xrGetRenderModelPropertiesEXT");
                m_CreateSpace = Fn<CreateSpaceFn>("xrCreateRenderModelSpaceEXT"); m_CreateAsset = Fn<CreateAssetFn>("xrCreateRenderModelAssetEXT"); m_DestroyAsset = Fn<DestroyFn>("xrDestroyRenderModelAssetEXT");
                m_GetAssetData = Fn<GetAssetDataFn>("xrGetRenderModelAssetDataEXT"); m_GetTopPath = Fn<GetTopPathFn>("xrGetRenderModelPoseTopLevelUserPathEXT"); m_Locate = Fn<LocateFn>("xrLocateSpace");
                m_Ready = m_StringToPath != null && m_EnumIds != null && m_CreateModel != null && m_GetProps != null && m_CreateSpace != null && m_CreateAsset != null && m_GetAssetData != null && m_Locate != null;
                if (m_Ready)
                {
                    m_StringToPath(m_Instance, "/user/hand/left", out m_PathLeft); m_StringToPath(m_Instance, "/user/hand/right", out m_PathRight);
                    Report = "controller render models: extensions on, functions found";
                }
                else Report = "controller render models: some functions were not found";
            }
            catch (Exception e) { m_Ready = false; Report = "controller render models: setup failed: " + e.Message; }
            Debug.Log("[XrSpatial] " + Report);
            return true;
        }

        protected override void OnSessionCreate(ulong xrSession) { m_Session = xrSession; }
        protected override void OnAppSpaceChange(ulong xrSpace) { m_AppSpace = xrSpace; }

        protected override void OnSessionDestroy(ulong xrSession)
        {
            m_Session = 0; m_Hands[0] = m_Hands[1] = null; m_AllIds.Clear();      // the runtime frees the models and spaces with the session
        }

        protected override void OnInstanceDestroy(ulong xrInstance) { m_Ready = false; if (s_Self == this) s_Self = null; }

        // ---- loading (call from the main thread, repeatedly, until both hands have a model) ----

        /// <summary>Asks the runtime for the controller models again; true when both hands now have one.</summary>
        public static bool Refresh() => s_Self != null && s_Self.DoRefresh();

        bool DoRefresh()
        {
            if (!m_Ready || m_Session == 0) return false;
            try
            {
                var info = new Header { type = TypeEnumIds };
                int r = m_EnumIds(m_Session, ref info, 0, out uint n, null);
                if (r < 0 || n == 0) { Note($"enumerate ids: result {r}, {n} models"); return false; }
                var ids = new ulong[n];
                r = m_EnumIds(m_Session, ref info, n, out n, ids);
                if (r < 0) { Note("enumerate ids (2nd call): result " + r); return false; }
                foreach (ulong id in ids)
                {
                    if (m_AllIds.Contains(id)) continue;
                    m_AllIds.Add(id);
                    LoadOne(id, (int)n);
                }
            }
            catch (Exception e) { Note("refresh failed: " + e.Message); }
            return m_Hands[0] != null && m_Hands[1] != null;
        }

        void LoadOne(ulong id, int total)
        {
            var ci = new CreateInfo { type = TypeCreateInfo, renderModelId = id };
            int r = m_CreateModel(m_Session, ref ci, out ulong model);
            if (r < 0 || model == 0) { Note($"create model {id}: result {r}"); return; }
            var pi = new Header { type = TypePropsGetInfo }; var props = new Props { type = TypeProps };
            r = m_GetProps(model, ref pi, ref props);
            if (r < 0) { Note($"model properties {id}: result {r}"); return; }
            var ac = new AssetCreate { type = TypeAssetCreate, cacheId = props.cacheId };
            r = m_CreateAsset(m_Session, ref ac, out ulong asset);
            if (r < 0 || asset == 0) { Note($"create asset {id}: result {r}"); return; }
            var gi = new Header { type = TypeAssetDataGetInfo }; var data = new AssetData { type = TypeAssetData };
            r = m_GetAssetData(asset, ref gi, ref data);
            if (r < 0 || data.count == 0) { Note($"asset size {id}: result {r}, {data.count} bytes"); return; }
            var buf = Marshal.AllocHGlobal((int)data.count);
            byte[] glb;
            try
            {
                data.capacity = data.count; data.buffer = buf;
                r = m_GetAssetData(asset, ref gi, ref data);
                if (r < 0) { Note($"asset data {id}: result {r}"); return; }
                glb = new byte[data.count]; Marshal.Copy(buf, glb, 0, (int)data.count);
            }
            finally { Marshal.FreeHGlobal(buf); }
            m_DestroyAsset?.Invoke(asset);                                          // the bytes are copied; the asset handle is not needed any more
            var sc = new SpaceCreate { type = TypeSpaceCreate, renderModel = model };
            r = m_CreateSpace(m_Session, ref sc, out ulong space);
            if (r < 0 || space == 0) { Note($"create model space {id}: result {r}"); return; }

            int hand = -1;                                                           // which hand: ask the runtime, else fall back to the order
            if (m_GetTopPath != null)
            {
                var paths = Marshal.AllocHGlobal(16);
                try
                {
                    Marshal.WriteInt64(paths, 0, (long)m_PathLeft); Marshal.WriteInt64(paths, 8, (long)m_PathRight);
                    var ti = new TopPathInfo { type = TypeTopPathGetInfo, count = 2, paths = paths };
                    r = m_GetTopPath(model, ref ti, out ulong path);
                    if (r >= 0) hand = path == m_PathLeft ? 0 : path == m_PathRight ? 1 : -1;
                    Note($"model {id}: top level user path result {r}, path {(hand == 0 ? "left" : hand == 1 ? "right" : "unknown")}");
                }
                finally { Marshal.FreeHGlobal(paths); }
            }
            if (hand < 0) hand = m_Hands[0] == null ? 0 : 1;
            m_Hands[hand] = new HandModel { id = id, model = model, space = space, glb = glb };
            Note($"model {id} -> {(hand == 0 ? "left" : "right")} hand: {glb.Length} bytes of glTF, {props.animatableNodeCount} animatable nodes");
        }

        void Note(string s) { m_Log = s; Report = "controller render models: " + s; Debug.Log("[XrSpatial] render model: " + s); }

        // ---- results ----

        public static bool TryGetModel(bool left, out byte[] glb)
        {
            glb = null;
            var h = s_Self?.m_Hands[left ? 0 : 1];
            if (h == null) return false;
            glb = h.glb; return true;
        }

        /// <summary>The model's pose in the app (tracking) space, converted to Unity's coordinate system; false when it cannot be located right now.</summary>
        public static bool TryLocate(bool left, out Vector3 position, out Quaternion rotation)
        {
            position = default; rotation = Quaternion.identity;
            var f = s_Self;
            var h = f?.m_Hands[left ? 0 : 1];
            if (h == null || f.m_AppSpace == 0 || s_DisplayTime == 0) return false;
            try
            {
                var loc = new SpaceLocation { type = TypeSpaceLocation };
                if (f.m_Locate(h.space, f.m_AppSpace, s_DisplayTime, ref loc) < 0 || (loc.flags & 3) != 3) return false;      // orientation and position valid
                position = new Vector3(loc.pose.px, loc.pose.py, -loc.pose.pz);
                rotation = new Quaternion(-loc.pose.qx, -loc.pose.qy, loc.pose.qz, loc.pose.qw);
                return true;
            }
            catch { return false; }
        }
    }
}
