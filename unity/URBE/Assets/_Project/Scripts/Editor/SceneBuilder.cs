using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Urbe.Game.CameraRig;
using Urbe.Game.Sim;

namespace Urbe.Game.Editor
{
    /// <summary>
    /// Genera Assets/_Project/Scenes/Main.unity de forma reproducible (M2-02). Menú URBE o
    /// -executeMethod Urbe.Game.Editor.SceneBuilder.BuildMain. Cada tarjeta posterior amplía este script (cámara, HUD...)
    /// en vez de editar la escena a mano.
    /// </summary>
    public static class SceneBuilder
    {
        public const string ScenePath = "Assets/_Project/Scenes/Main.unity";
        private const string MaterialsFolder = "Assets/_Project/Art/Materials";

        [MenuItem("URBE/Generar escena Main")]
        public static void BuildMain()
        {
            Directory.CreateDirectory("Assets/_Project/Scenes");
            Directory.CreateDirectory(MaterialsFolder);
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            Material road = EnsureUnlit("DebugRoad", new Color(0.23f, 0.25f, 0.29f), false);
            Material ground = EnsureUnlit("DebugGround", new Color(0.62f, 0.72f, 0.58f), false);
            Material vehicle = EnsureUnlit("DebugVehicle", new Color(0.95f, 0.55f, 0.15f), true);

            var sun = new GameObject("Sol");
            var light = sun.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1f;
            sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var simGo = new GameObject("Simulacion");
            var host = simGo.AddComponent<SimHost>();
            var boot = simGo.AddComponent<SimBootstrap>();
            SetRef(boot, "host", host);
            var view = simGo.AddComponent<DebugSimView>();
            SetRef(view, "host", host);
            SetRef(view, "roadMaterial", road);
            SetRef(view, "groundMaterial", ground);
            SetRef(view, "vehicleMaterial", vehicle);

            var camGo = new GameObject("Camara") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.72f, 0.83f, 0.92f);
            cam.nearClipPlane = 1f;
            cam.farClipPlane = 6000f;
            camGo.AddComponent<AudioListener>();
            var rig = camGo.AddComponent<CityCameraController>();
            var rigSo = new SerializedObject(rig);
            rigSo.FindProperty("settings.boundsMin").vector2Value = new Vector2(-300f, -300f);
            rigSo.FindProperty("settings.boundsMax").vector2Value = new Vector2(1100f, 1100f);
            rigSo.FindProperty("startFocus").vector2Value = new Vector2(400f, 400f);
            rigSo.FindProperty("startHeight").floatValue = 700f;
            rigSo.ApplyModifiedPropertiesWithoutUndo();
            camGo.transform.SetPositionAndRotation(new Vector3(400f, 700f, -120f), Quaternion.Euler(55f, 0f, 0f));

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("[URBE] Escena generada: " + ScenePath);
        }

        private static Material EnsureUnlit(string name, Color color, bool instancing)
        {
            string path = $"{MaterialsFolder}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetColor("_BaseColor", color);
            m.enableInstancing = instancing;
            EditorUtility.SetDirty(m);
            return m;
        }

        private static void SetRef(Object target, string field, Object value)
        {
            var so = new SerializedObject(target);
            so.FindProperty(field).objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
