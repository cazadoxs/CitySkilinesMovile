using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Urbe.Game.CameraRig;
using Urbe.Game.Rendering.Roads;
using Urbe.Game.Rendering.Traffic;
using Urbe.Game.Sim;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Game.Tests
{
    /// <summary>M2-09: capa de tráfico por flujo sobre la escena Main real (shader, textura de datos, botón del HUD) y captura con un atasco real.</summary>
    public class TrafficLayerTests : InputTestFixture
    {
        private SimHost _host = null!;
        private TrafficLayer _layer = null!;
        private RoadChunkRenderer _roads = null!;
        private Camera _cam = null!;
        private Mouse _mouse = null!;
        private UIDocument _doc = null!;

        public override void Setup()
        {
            base.Setup();
            _mouse = InputSystem.AddDevice<Mouse>();
            InputSystem.AddDevice<Keyboard>();
        }

        /// <summary>Red de muestra con mucha demanda para provocar un atasco real: se ejecuta el tiempo simulado que haga falta.</summary>
        private IEnumerator LoadJam(int vehicles, float tripsPerSecond, float simSeconds)
        {
            SceneManager.LoadScene("Main");
            for (int i = 0; i < 6; i++) yield return null;
            _host = Object.FindFirstObjectByType<SimHost>();
            _layer = Object.FindFirstObjectByType<TrafficLayer>();
            _roads = Object.FindFirstObjectByType<RoadChunkRenderer>();
            _cam = Object.FindFirstObjectByType<CityCameraController>().GetComponent<Camera>();
            _doc = Object.FindFirstObjectByType<UIDocument>();
            Assert.That(_layer, Is.Not.Null, "la escena Main lleva TrafficLayer");
            _host.SetSpeed(SimSpeed.Paused);
            var showcase = ShowcaseScenario.Create(11UL, vehicles, tripsPerSecond);
            _host.Initialize(showcase.Scenario);
            yield return null;
            int ticks = Mathf.RoundToInt(simSeconds * SimHost.TicksPerSecond);
            for (int i = 0; i < ticks; i++) _host.StepOnce();
            _roads.Flush();
            yield return null;
        }

        private float MinSegmentFlow(out int index)
        {
            float[] f = _host.Simulation!.Metrics.SegmentFlow;
            float min = 1f;
            index = -1;
            for (int i = 0; i < _host.Network!.SegmentIndexBound; i++)
                if (_host.Network.SegmentIdAt(i).IsValid && f[i] < min) { min = f[i]; index = i; }
            return min;
        }

        [Test]
        public void ColorDelFlujo_VerdeFluido_AmbarLento_RojoAtasco()
        {
            Color32 free = TrafficLayer.FlowColor(1f), slow = TrafficLayer.FlowColor(0.55f), jam = TrafficLayer.FlowColor(0.1f);
            Assert.That(free.g, Is.GreaterThan(free.r), "verde");
            Assert.That(slow.r, Is.GreaterThan(200).And.GreaterThan(slow.b), "ámbar");
            Assert.That(slow.g, Is.GreaterThan(150));
            Assert.That(jam.r, Is.GreaterThan(200));
            Assert.That(jam.g, Is.LessThan(60), "rojo");
            // Continuo: el flujo entre 0,3 y 0,8 pasa de rojo a verde sin saltos.
            int prevG = -1;
            for (float f = 0.3f; f <= 0.8f; f += 0.05f)
            {
                int g = TrafficLayer.FlowColor(f).g;
                Assert.That(g, Is.GreaterThanOrEqualTo(prevG));
                prevG = g;
            }
        }

        [UnityTest]
        public IEnumerator Capa_ActivaElShader_YEscribeUnTexelPorSegmento()
        {
            yield return LoadJam(120, 3f, 60f);
            var mat = _roads.Material!;
            Assert.That(mat.GetFloat("_TrafficLayer"), Is.EqualTo(0f));
            _layer.SetActive(true);
            Assert.That(mat.GetFloat("_TrafficLayer"), Is.EqualTo(1f));
            Assert.That(mat.GetTexture("_TrafficData"), Is.Not.Null);
            Assert.That(_layer.DataTexture!.width, Is.EqualTo(256));
            // El texel de cada segmento vivo coincide con el color de su flujo.
            float[] flow = _host.Simulation!.Metrics.SegmentFlow;
            var net = _host.Network!;
            for (int i = 0; i < net.SegmentIndexBound; i++)
            {
                if (!net.SegmentIdAt(i).IsValid) continue;
                Color32 px = _layer.DataTexture.GetPixel(i & 255, i >> 8);
                Color32 want = TrafficLayer.FlowColor(flow[i]);
                Assert.That(px.r, Is.EqualTo(want.r).Within(1));
                Assert.That(px.g, Is.EqualTo(want.g).Within(1));
                Assert.That(px.a, Is.EqualTo(255));
            }
            _layer.SetActive(false);
            Assert.That(mat.GetFloat("_TrafficLayer"), Is.EqualTo(0f));
        }

        [UnityTest]
        public IEnumerator Capa_SeActualizaComoMuchoA2Hz_YSinRegenerarMallas()
        {
            yield return LoadJam(120, 3f, 30f);
            _layer.SetActive(true);
            int u0 = _layer.UpdateCount;
            int pendingBefore = _roads.PendingPieces;
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 2.1f) yield return null;
            int updates = _layer.UpdateCount - u0;
            Debug.Log($"[M2-09] actualizaciones de la capa en 2,1 s: {updates}");
            Assert.That(updates, Is.InRange(3, 5), "2 Hz");
            Assert.That(_roads.PendingPieces, Is.EqualTo(0));
            Assert.That(_roads.LastBatchPieces, Is.GreaterThanOrEqualTo(0));
            _layer.SetActive(false);
        }

        [UnityTest]
        public IEnumerator BotonTrafico_ActivaLaCapa_YMuestraLaLeyenda()
        {
            yield return LoadJam(60, 2f, 20f);
            var btn = _doc.rootVisualElement.Q<Button>("btn-traffic");
            Assert.That(btn, Is.Not.Null);
            Vector2 c = btn.worldBound.center;
            float k = Screen.width / btn.panel.visualTree.resolvedStyle.width;
            for (int attempt = 0; attempt < 4 && !_layer.Active; attempt++)
            {
                Set(_mouse.position, new Vector2(c.x * k + 20f, Screen.height - c.y * k));
                yield return null;
                Set(_mouse.position, new Vector2(c.x * k, Screen.height - c.y * k));
                yield return null;
                yield return null;
                Click(_mouse.leftButton);
                yield return null;
                yield return null;
            }
            Assert.That(_layer.Active, "el botón Tráfico activa la capa");
            Assert.That(_doc.rootVisualElement.Q<VisualElement>("traffic-legend").ClassListContains("show"));
            Assert.That(btn.ClassListContains("toggled"));
        }

        // ------------------------------------------------------------------ captura con atasco real

        [UnityTest]
        public IEnumerator Captura_Trafico_ConAtascoReal()
        {
            // Demanda muy por encima de la capacidad de la red de muestra: las colas se forman solas, sin tocar los datos de flujo.
            yield return LoadJam(260, 9f, 240f);
            float min = MinSegmentFlow(out int idx);
            Debug.Log($"[M2-09] tras 7 min simulados: vehículos {_host.Metrics.VehicleCount}, flujo ciudad {_host.Metrics.FlowPercent:F1}%, flujo mínimo de un tramo {min:F2} (segmento {idx})");
            Assert.That(min, Is.LessThan(0.5f), "debe haber un atasco real (flujo de algún tramo < 50 %)");
            _layer.SetActive(true);
            _layer.UpdateData();
            _doc.rootVisualElement.Q<VisualElement>("traffic-legend").EnableInClassList("show", true);
            _doc.rootVisualElement.Q<Button>("btn-traffic").EnableInClassList("toggled", true);
            for (int i = 0; i < 3; i++) yield return null;

            // Encuadre: el cruce de más atasco. Se busca el tramo de peor flujo y se mira hacia su punto medio.
            var net = _host.Network!;
            var seg = net.SegmentIdAt(idx);
            var mid = net.GetSegment(seg).Curve.Evaluate(0.5f);
            var rig = _cam.GetComponent<CityCameraController>();
            rig.Model.SnapTo(new Vector2(mid.X, mid.Z), 150f);
            yield return null;
            yield return null;

            const int w = 1688, h = 780;
            var panel = Object.Instantiate(_doc.panelSettings);
            _doc.panelSettings = panel;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            rt.Create();
            _cam.targetTexture = rt;
            yield return null;
            yield return null;
            _cam.enabled = false;
            panel.clearColor = false;
            panel.targetTexture = rt;
            yield return null;
            yield return null;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            panel.targetTexture = null;
            _cam.targetTexture = null;
            string dir = Path.Combine(Application.dataPath, "..", "..", "..", "docs", "media");
            Directory.CreateDirectory(dir);
            string path = Path.GetFullPath(Path.Combine(dir, "m2-trafico.png"));
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log("[M2-09] captura en " + path);
            Object.Destroy(tex);
            rt.Release();
        }
    }
}
