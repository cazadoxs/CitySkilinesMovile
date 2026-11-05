using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Urbe.Game.Editor
{
    /// <summary>
    /// Configuración reproducible del proyecto (M2-01): URP móvil/PC, solo el Input System nuevo, ajustes de Android y orientación.
    /// Se lanza desde el menú URBE o con <c>-executeMethod Urbe.Game.Editor.ProjectSetup.Apply</c>.
    /// </summary>
    public static class ProjectSetup
    {
        public const string SettingsFolder = "Assets/_Project/Settings";

        [MenuItem("URBE/Configurar proyecto")]
        public static void Apply()
        {
            Directory.CreateDirectory(SettingsFolder);
            UniversalRenderPipelineAsset mobile = EnsureUrp("URP-Mobile", mobile: true);
            UniversalRenderPipelineAsset pc = EnsureUrp("URP-PC", mobile: false);

            // Un asset de URP por nivel de calidad: los 3 primeros niveles (bajo) usan el móvil; el resto el de PC.
            int levels = QualitySettings.names.Length;
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < levels; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = i < 3 ? mobile : pc;
            }
            QualitySettings.SetQualityLevel(Mathf.Clamp(current, 0, levels - 1), false);
            GraphicsSettings.defaultRenderPipeline = mobile;

            PlayerSettings.companyName = "URBE";
            PlayerSettings.productName = "URBE";
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;
            PlayerSettings.allowedAutorotateToPortrait = true;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android, "com.urbe.game");
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

            SetInputSystemOnly();
            AssetDatabase.SaveAssets();
            Debug.Log("[URBE] Proyecto configurado (URP, Input System, Android).");
        }

        private static UniversalRenderPipelineAsset EnsureUrp(string name, bool mobile)
        {
            string path = $"{SettingsFolder}/{name}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
            if (existing != null) return existing;

            var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(rendererData, $"{SettingsFolder}/{name}-Renderer.asset");
            UniversalRenderPipelineAsset asset = UniversalRenderPipelineAsset.Create(rendererData);
            asset.supportsHDR = !mobile;
            asset.msaaSampleCount = mobile ? 1 : 4;
            asset.shadowDistance = mobile ? 120f : 300f;
            asset.renderScale = 1f;
            asset.supportsCameraDepthTexture = false;
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        /// <summary>Active Input Handling = Input System Package (New). Requiere reiniciar el editor para aplicarse.</summary>
        private static void SetInputSystemOnly()
        {
            Object[] all = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (all == null || all.Length == 0) return;
            var so = new SerializedObject(all[0]);
            SerializedProperty p = so.FindProperty("activeInputHandler");
            if (p != null && p.intValue != 1)
            {
                p.intValue = 1;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }
    }
}
