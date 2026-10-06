using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using XrSpatial.App;
using Debug = UnityEngine.Debug;

namespace XrSpatial.Editor
{
    /// <summary>One-click and batch-mode project setup, scene creation and Windows player build (Windows x64, D3D11, Linear, URP, OpenXR single-pass instanced).</summary>
    public static class Automation
    {
        const string ScenePath = "Assets/Scenes/Main.unity";
        public const string BuildDir = "Builds/Windows";
        public const string ExeName = "XrSpatialScreens.exe";

        static readonly HashSet<string> k_Profiles = new HashSet<string>
        {
            "ValveIndexControllerProfile", "OculusTouchControllerProfile", "MetaQuestTouchPlusControllerProfile", "MetaQuestTouchProControllerProfile",
            "HTCViveControllerProfile", "KHRSimpleControllerProfile", "HPReverbG2ControllerProfile", "SteamFrameControllerProfile",
        };

        [MenuItem("XR Spatial/Configure Project")]
        public static void ConfigureProject()
        {
            PlayerSettings.companyName = "Gamebreak Labs";
            PlayerSettings.productName = "XR Spatial Screens";
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            PlayerSettings.runInBackground = true;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1280; PlayerSettings.defaultScreenHeight = 720; PlayerSettings.resizableWindow = true;
            PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_Standard);
            ConfigureQuality();
            ConfigureXR();
            AssetDatabase.SaveAssets();
            Debug.Log("[XrSpatial] project configured");
        }

        static void ConfigureQuality()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset"))
            {
                var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (!asset || !asset.name.StartsWith("PC")) continue;
                asset.msaaSampleCount = 4; asset.supportsHDR = false; asset.supportsCameraDepthTexture = false; asset.supportsCameraOpaqueTexture = false;
                asset.shadowDistance = 10f;
                EditorUtility.SetDirty(asset);
                foreach (var r in asset.rendererDataList)
                {
                    if (!r) continue;
                    foreach (var f in r.rendererFeatures) if (f && f.GetType().Name.Contains("AmbientOcclusion")) { f.SetActive(false); EditorUtility.SetDirty(f); }
                    EditorUtility.SetDirty(r);
                }
            }
            var names = QualitySettings.names;
            for (int i = 0; i < names.Length; i++) if (names[i] == "PC") QualitySettings.SetQualityLevel(i, true);
        }

        static void ConfigureXR()
        {
            const BuildTargetGroup group = BuildTargetGroup.Standalone;
            EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.settingsKey, out XRGeneralSettingsPerBuildTarget perTarget);
            if (!perTarget)
            {
                Directory.CreateDirectory("Assets/XR");
                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perTarget, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.settingsKey, perTarget, true);
            }
            var settings = perTarget.SettingsForBuildTarget(group);
            if (!settings)
            {
                settings = ScriptableObject.CreateInstance<XRGeneralSettings>();
                settings.name = "Standalone Settings";
                perTarget.SetSettingsForBuildTarget(group, settings);
                AssetDatabase.AddObjectToAsset(settings, perTarget);
            }
            if (!settings.Manager)
            {
                var manager = ScriptableObject.CreateInstance<XRManagerSettings>();
                manager.name = "Standalone Providers";
                AssetDatabase.AddObjectToAsset(manager, perTarget);
                settings.Manager = manager;
            }
            settings.InitManagerOnStart = true;
            EditorUtility.SetDirty(settings); EditorUtility.SetDirty(perTarget);
            bool assigned = XRPackageMetadataStore.AssignLoader(settings.Manager, "UnityEngine.XR.OpenXR.OpenXRLoader", group);
            Debug.Log($"[XrSpatial] OpenXR loader assigned: {assigned}");
            UnityEditor.XR.OpenXR.Features.FeatureHelpers.RefreshFeatures(group);
            var oxr = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            if (oxr)
            {
                oxr.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
                oxr.depthSubmissionMode = OpenXRSettings.DepthSubmissionMode.Depth24Bit;
                foreach (var feature in oxr.GetFeatures<OpenXRInteractionFeature>()) feature.enabled = k_Profiles.Contains(feature.GetType().Name);
                EditorUtility.SetDirty(oxr);
            }
            else Debug.LogError("[XrSpatial] OpenXR settings not found for Standalone.");
        }

        [MenuItem("XR Spatial/Create Main Scene")]
        public static void CreateScene()
        {
            Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("SpatialApp").AddComponent<SpatialApp>();
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Debug.Log("[XrSpatial] scene created: " + ScenePath);
        }

        /// <summary>Makes sure the panel/unlit shaders are in the build even if nothing references them as assets (they are also under Resources).</summary>
        static void EnsureShadersIncluded()
        {
            var gs = AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset");
            var so = new SerializedObject(gs);
            var list = so.FindProperty("m_AlwaysIncludedShaders");
            foreach (var name in new[] { "XrSpatial/Panel", "XrSpatial/Unlit" })
            {
                var sh = Shader.Find(name);
                if (!sh) continue;
                bool has = false;
                for (int i = 0; i < list.arraySize; i++) if (list.GetArrayElementAtIndex(i).objectReferenceValue == sh) has = true;
                if (has) continue;
                list.InsertArrayElementAtIndex(list.arraySize);
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = sh;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        [MenuItem("XR Spatial/Build Windows Player")]
        public static void BuildPlayer()
        {
            EnsureShadersIncluded();
            Directory.CreateDirectory(BuildDir);
            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath }, locationPathName = Path.Combine(BuildDir, ExeName), target = BuildTarget.StandaloneWindows64, options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(opts);
            Debug.Log($"[XrSpatial] Build {report.summary.result}: {report.summary.totalErrors} errors, {report.summary.totalSize / (1024 * 1024)} MB -> {opts.locationPathName}");
            if (report.summary.result != BuildResult.Succeeded) { if (Application.isBatchMode) EditorApplication.Exit(1); return; }
            CopyHelper();
        }

        /// <summary>Copies the capture helper next to the player (publish output if present, else the Release build).</summary>
        static void CopyHelper()
        {
            string publish = "Tools/XrssCapture/publish";
            string release = "Tools/XrssCapture/bin/Release/net8.0-windows10.0.22621.0/win-x64";
            string src = File.Exists(Path.Combine(publish, "XrssCapture.exe")) ? publish : release;
            if (!File.Exists(Path.Combine(src, "XrssCapture.exe"))) { Debug.LogWarning("[XrSpatial] capture helper not built; the player will only offer the test pattern"); return; }
            string dst = Path.Combine(BuildDir, "XrssCapture");
            if (Directory.Exists(dst)) Directory.Delete(dst, true);
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            Debug.Log($"[XrSpatial] helper copied from {src}");
        }

        /// <summary>Batch entry: configure, create the scene, build.</summary>
        public static void SetupAndBuild()
        {
            ConfigureProject();
            CreateScene();
            AssetDatabase.Refresh();
            BuildPlayer();
        }
    }
}
