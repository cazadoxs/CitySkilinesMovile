using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Urbe.Game.CameraRig;
using Urbe.Game.Rendering.Ground;
using Urbe.Game.Rendering.Roads;
using Urbe.Game.Rendering.Traffic;
using Urbe.Game.Rendering.Vehicles;
using Urbe.Game.Tools.RoadTool;
using Urbe.Game.Sim;
using Urbe.Game.UI;

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

            Material roadSurface = RoadAtlasBuilder.EnsureAll();
            Material ground = EnsureUnlit("DebugGround", Color.white, false);
            Material vehicleMat = VehicleAssetBuilder.EnsureMaterial();
            VehicleModelSet vehicleSet = VehicleAssetBuilder.EnsureModelSet();

            var sun = new GameObject("Sol");
            var light = sun.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1f;
            sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var simGo = new GameObject("Simulacion");
            var host = simGo.AddComponent<SimHost>();
            var boot = simGo.AddComponent<SimBootstrap>();
            SetRef(boot, "host", host);
            var groundView = simGo.AddComponent<CityGround>();
            SetRef(groundView, "host", host);
            SetRef(groundView, "groundMaterial", ground);
            var vehicles = simGo.AddComponent<VehicleRenderer>();
            SetRef(vehicles, "host", host);
            SetRef(vehicles, "models", vehicleSet);
            SetRef(vehicles, "material", vehicleMat);

            var roadsGo = new GameObject("Carreteras");
            var roads = roadsGo.AddComponent<RoadChunkRenderer>();
            SetRef(roads, "host", host);
            SetRef(roads, "material", roadSurface);

            var camGo = new GameObject("Camara") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.72f, 0.83f, 0.92f);
            cam.nearClipPlane = 1f;
            cam.farClipPlane = 6000f;
            camGo.AddComponent<AudioListener>();
            var rig = camGo.AddComponent<CityCameraController>();
            var rigSo = new SerializedObject(rig);
            rigSo.FindProperty("settings.boundsMin").vector2Value = new Vector2(-1100f, -1100f);
            rigSo.FindProperty("settings.boundsMax").vector2Value = new Vector2(1100f, 1100f);
            rigSo.FindProperty("startFocus").vector2Value = new Vector2(-780f, 0f);
            rigSo.FindProperty("startHeight").floatValue = 300f;
            rigSo.ApplyModifiedPropertiesWithoutUndo();
            camGo.transform.SetPositionAndRotation(new Vector3(-800f, 260f, -90f), Quaternion.Euler(55f, 0f, 0f));

            var ghostGo = new GameObject("FantasmaCarreteras");
            var ghost = ghostGo.AddComponent<RoadGhostRenderer>();
            SetRef(ghost, "roadMaterial", roadSurface);
            SetRef(ghost, "overlayMaterial", EnsureOverlayMaterial());

            var trafficGo = new GameObject("CapaTrafico");
            var traffic = trafficGo.AddComponent<TrafficLayer>();
            SetRef(traffic, "host", host);
            SetRef(traffic, "roads", roads);

            BuildHud(host, rig, roads, ghost, traffic);

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("[URBE] Escena generada: " + ScenePath);
        }

        private static void BuildHud(SimHost host, CityCameraController rig, RoadChunkRenderer roads, RoadGhostRenderer ghost, TrafficLayer traffic)
        {
            const string uiFolder = "Assets/_Project/UI/HUD";
            var panel = EnsurePanelSettings();
            var hudGo = new GameObject("HUD");
            var doc = hudGo.AddComponent<UIDocument>();
            doc.panelSettings = panel;
            doc.visualTreeAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uiFolder + "/Hud.uxml");
            // UI Toolkit recibe los eventos de puntero/teclado por el EventSystem; con solo el Input System nuevo hace falta su módulo.
            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<InputSystemUIInputModule>();
            var hud = hudGo.AddComponent<HudController>();
            SetRef(hud, "host", host);
            SetRef(hud, "cameraController", rig);
            SetRef(hud, "styleSheet", AssetDatabase.LoadAssetAtPath<StyleSheet>(uiFolder + "/Hud.uss"));
            SetRef(hud, "trafficLayer", traffic);
            var cityHud = hudGo.AddComponent<CityHudController>();
            SetRef(cityHud, "host", host);
            var tool = hudGo.AddComponent<RoadToolController>();
            SetRef(tool, "host", host);
            SetRef(tool, "cameraController", rig);
            SetRef(tool, "roads", roads);
            SetRef(tool, "ghost", ghost);
            SetRef(tool, "pricesJson", AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/_Project/Data/road-prices.json"));
        }

        /// <summary>
        /// PanelSettings del HUD: escala con la pantalla sobre 844x390 (el móvil horizontal lógico de docs/figma.md), así que 1 px de USS
        /// es 1 dp en un móvil y la interfaz crece sola en PC. El ajuste mezcla ancho y alto (0,5) para que sirva también en vertical.
        /// </summary>
        private static PanelSettings EnsurePanelSettings()
        {
            const string path = "Assets/_Project/UI/HUD/HudPanelSettings.asset";
            var ps = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (ps == null)
            {
                ps = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(ps, path);
            }
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(844, 390);
            ps.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            ps.match = 0.5f;
            ps.clearColor = false;
            ps.sortingOrder = 10;
            EditorUtility.SetDirty(ps);
            return ps;
        }

        /// <summary>Material de líneas y marcadores de la herramienta de carreteras (colores por vértice, sin luz).</summary>
        private static Material EnsureOverlayMaterial()
        {
            string path = $"{MaterialsFolder}/ToolOverlay.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Sprites/Default"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.renderQueue = 3100;
            EditorUtility.SetDirty(m);
            return m;
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
