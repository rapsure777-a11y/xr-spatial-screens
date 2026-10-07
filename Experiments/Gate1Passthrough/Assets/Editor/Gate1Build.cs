using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEditor.SceneManagement;

namespace Gate1
{
    /// <summary>Batch-mode setup and build. Pass 1 (Setup) configures the project; pass 2 (BuildAndroid) makes the APK. Run them in separate editor launches.</summary>
    public static class Gate1Build
    {
        const string ScenePath = "Assets/Gate1.unity";
        const string ApkPath = "Builds/Gate1Passthrough.apk";

        public static void Setup()
        {
            // new Input System backend (TrackedPoseDriver); takes effect after the editor restarts, so this is its own pass
            var ps = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            var so = new SerializedObject(ps[0]);
            so.FindProperty("activeInputHandler").intValue = 1;
            so.ApplyModifiedPropertiesWithoutUndo();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Directory.CreateDirectory("Assets");
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            PlayerSettings.companyName = "Gamebreak Labs";
            PlayerSettings.productName = "Gate1 Passthrough";
            var android = NamedBuildTarget.Android;
            PlayerSettings.SetApplicationIdentifier(android, "com.gamebreaklabs.gate1passthrough");
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel30;
            PlayerSettings.colorSpace = ColorSpace.Linear;

            // keep Unlit/Color in the player (it is only found by name at runtime)
            var gs = AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset");
            var gso = new SerializedObject(gs);
            var list = gso.FindProperty("m_AlwaysIncludedShaders");
            var shader = Shader.Find("Unlit/Color");
            bool have = false;
            for (int i = 0; i < list.arraySize; i++) if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) have = true;
            if (!have) { list.arraySize++; list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader; gso.ApplyModifiedPropertiesWithoutUndo(); }

            foreach (var group in new[] { BuildTargetGroup.Android, BuildTargetGroup.Standalone })
            {
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
                    settings.name = group + " Settings";
                    perTarget.SetSettingsForBuildTarget(group, settings);
                    AssetDatabase.AddObjectToAsset(settings, perTarget);
                }
                if (!settings.Manager)
                {
                    var manager = ScriptableObject.CreateInstance<XRManagerSettings>();
                    manager.name = group + " Providers";
                    AssetDatabase.AddObjectToAsset(manager, perTarget);
                    settings.Manager = manager;
                }
                settings.InitManagerOnStart = true;
                bool ok = XRPackageMetadataStore.AssignLoader(settings.Manager, "UnityEngine.XR.OpenXR.OpenXRLoader", group);
                Debug.Log($"[Gate1] OpenXR loader assigned for {group}: {ok}");
                UnityEditor.XR.OpenXR.Features.FeatureHelpers.RefreshFeatures(group);
                var oxr = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
                if (oxr != null)
                {
                    foreach (var f in oxr.GetFeatures())
                    {
                        if (f is PassthroughFeature) { f.enabled = true; Debug.Log($"[Gate1] feature enabled for {group}: {f.name}"); }
                    }
                    EditorUtility.SetDirty(oxr);
                }
                EditorUtility.SetDirty(settings); EditorUtility.SetDirty(perTarget);
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[Gate1] setup done");
        }

        public static void BuildAndroid()
        {
            Directory.CreateDirectory("Builds");
            // tools unpacked from the Unity Hub download cache into a user folder (no admin rights needed)
            UnityEditor.Android.AndroidExternalToolsSettings.sdkRootPath = @"C:\Users\fence\UnityAndroid\SDK";
            UnityEditor.Android.AndroidExternalToolsSettings.ndkRootPath = @"C:\Users\fence\UnityAndroid\NDK\android-ndk-r27c";
            UnityEditor.Android.AndroidExternalToolsSettings.jdkRootPath = @"C:\Users\fence\UnityAndroid\OpenJDK";
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            EditorUserBuildSettings.buildAppBundle = false;
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { ScenePath }, locationPathName = ApkPath, target = BuildTarget.Android, options = BuildOptions.None });
            Debug.Log("[Gate1] build result: " + report.summary.result + ", size " + report.summary.totalSize + ", errors " + report.summary.totalErrors);
            EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}
