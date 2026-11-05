using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Urbe.Game.CameraRig;
using Urbe.Game.Rendering.Roads;
using Urbe.Game.Rendering.Vehicles;
using Urbe.Game.Sim;

namespace Urbe.Game.Tests
{
    /// <summary>M2-06: render instanciado de vehículos sobre la escena Main real. Mide draw calls, CPU y asignaciones en PC y captura m2-vehiculos.png.</summary>
    public class VehicleRenderTests
    {
        private SimHost _host = null!;
        private VehicleRenderer _vr = null!;
        private CityCameraController _rig = null!;
        private Camera _cam = null!;

        private IEnumerator LoadMain(SimScenario scenario, int simTicks)
        {
            SceneManager.LoadScene("Main");
            yield return null;
            yield return null;
            _host = Object.FindFirstObjectByType<SimHost>();
            _vr = Object.FindFirstObjectByType<VehicleRenderer>();
            _rig = Object.FindFirstObjectByType<CityCameraController>();
            _cam = _rig.GetComponent<Camera>();
            Assert.That(_vr, Is.Not.Null, "la escena Main lleva VehicleRenderer");
            Assert.That(Object.FindFirstObjectByType<DebugSimView>(), Is.Not.Null);
            _host.SetSpeed(SimSpeed.Paused);
            _host.Initialize(scenario);
            yield return null;
            for (int i = 0; i < simTicks; i++) _host.StepOnce();
            Object.FindFirstObjectByType<RoadChunkRenderer>().Flush();
            yield return null;
            yield return null;
        }

        /// <summary>UnityStats.drawCalls (solo editor) por reflexión: la prueba de PlayMode no referencia UnityEditor.</summary>
        private static int EditorDrawCalls()
        {
            var t = System.Type.GetType("UnityEditor.UnityStats, UnityEditor");
            var p = t?.GetProperty("drawCalls", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            return p != null ? (int)p.GetValue(null) : 0;
        }

        private static float Percentile(List<float> v, float p)
        {
            v.Sort();
            return v[Mathf.Min(v.Count - 1, (int)(v.Count * p))];
        }

        /// <summary>Mide 200 fotogramas con la cámara en la posición dada; devuelve (media, p95) de CPU del renderer en ms.</summary>
        private IEnumerator Measure(VehicleDrawMode mode, string label, float lodDistance)
        {
            _vr.SetMode(mode);
            _vr.LodDistance = lodDistance;
            for (int i = 0; i < 30; i++) yield return null; // calentamiento: arrays, shaders, buffers
            var cpu = new List<float>(200);
            long maxAlloc = 0;
            int maxDraw = 0, visible = 0, culled = 0;
            using (var rec = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count"))
            {
                long maxFrameDraw = 0;
                for (int i = 0; i < 200; i++)
                {
                    yield return null;
                    cpu.Add(_vr.LastCpuMs);
                    maxAlloc = System.Math.Max(maxAlloc, _vr.LastAllocBytes);
                    maxDraw = System.Math.Max(maxDraw, _vr.LastDrawCalls);
                    visible = _vr.LastVisible;
                    culled = _vr.LastCulled;
                    if (rec.Valid) maxFrameDraw = System.Math.Max(maxFrameDraw, rec.LastValue);
                    maxFrameDraw = System.Math.Max(maxFrameDraw, EditorDrawCalls());
                }
                float mean = 0f;
                foreach (float c in cpu) mean += c;
                mean /= cpu.Count;
                Debug.Log($"[M2-06] {label} | {mode} | vehiculos visibles {visible} (descartados {culled}) | draw calls de vehiculos {maxDraw} | " +
                          $"draw calls del fotograma (Profiler, incl. carreteras y suelo) {maxFrameDraw} | CPU renderer media {mean:F3} ms, p95 {Percentile(cpu, 0.95f):F3} ms | " +
                          $"bytes gestionados por fotograma (max) {maxAlloc}");
                Assert.That(visible, Is.GreaterThan(2000), label + ": debe haber ~3.000 vehículos dibujándose");
                Assert.That(maxDraw, Is.LessThan(10), label + ": draw calls de vehículos");
                Assert.That(maxAlloc, Is.EqualTo(0), label + ": sin asignaciones por fotograma");
            }
        }

        [UnityTest]
        public IEnumerator Tres_Mil_Vehiculos_MenosDeDiezDrawCalls_SinAsignaciones_AmbosCaminos()
        {
            yield return LoadMain(GridScenario.Create(5UL, 3000, 80f, 25, 100f), 900);
            Assert.That(_host.Simulation!.Vehicles.AliveCount, Is.GreaterThan(2700), "la simulación tiene ~3.000 vehículos activos");
            _rig.enabled = false;
            // Toda la cuadrícula (2.400 x 2.400 m) a la vista: casi todo en LOD1.
            _cam.transform.SetPositionAndRotation(new Vector3(1200f, 2600f, 1200f), Quaternion.Euler(90f, 0f, 0f));
            _cam.farClipPlane = 6000f;
            foreach (VehicleDrawMode m in new[] { VehicleDrawMode.RenderMeshInstanced, VehicleDrawMode.BatchRendererGroup })
            {
                yield return Measure(m, "toda la ciudad (LOD1 casi todo)", 120f);
                // Peor caso de variedad: todos los vehículos en LOD0 (3 coches + camión = 4 mallas).
                yield return Measure(m, "toda la ciudad forzada a LOD0", 100000f);
            }
        }

        [UnityTest]
        public IEnumerator Captura_Vehiculos_Cerca_YMediaDistancia()
        {
            var sc = ShowcaseScenario.Create(21UL, 450, 14f);
            yield return LoadMain(sc.Scenario, 1200);
            _rig.enabled = false;
            const int w = 1688, h = 780;
            var mosaic = new Texture2D(w, h * 2, TextureFormat.RGBA32, false);
            // Arriba: cerca (30 m, plano medio sobre el cruce en X). Abajo: media distancia (140 m, avenida y cruces).
            yield return Tile(mosaic, 1, w, h, new Vector3(ShowcaseScenario.CrossX.x + 20f, 34f, ShowcaseScenario.CrossX.y - 30f), 52f);
            yield return Tile(mosaic, 0, w, h, new Vector3(190f, 150f, 0f), 58f);
            string dir = Path.Combine(Application.dataPath, "..", "..", "..", "docs", "media");
            Directory.CreateDirectory(dir);
            string path = Path.GetFullPath(Path.Combine(dir, "m2-vehiculos.png"));
            File.WriteAllBytes(path, mosaic.EncodeToPNG());
            Debug.Log($"[M2-06] captura en {path}; visibles {_vr.LastVisible}, draw calls {_vr.LastDrawCalls}");
        }

        [UnityTest]
        public IEnumerator Main_ArrancaSola_Y_DibujaVehiculosSinPasosPrevios()
        {
            SceneManager.LoadScene("Main");
            for (int i = 0; i < 120; i++) yield return null;
            var host = Object.FindFirstObjectByType<SimHost>();
            var vr = Object.FindFirstObjectByType<VehicleRenderer>();
            Assert.That(host.IsInitialized, "SimBootstrap inicia la simulación");
            Assert.That(host.Tick, Is.GreaterThan(0UL), "la simulación avanza sola");
            Assert.That(vr.LastVisible, Is.GreaterThan(0), "los vehículos se dibujan");
            Assert.That(vr.LastDrawCalls, Is.InRange(1, 9));
        }

        [UnityTest]
        public IEnumerator Captura_BatchRendererGroup_ParaComparar()
        {
            var sc = ShowcaseScenario.Create(21UL, 450, 14f);
            yield return LoadMain(sc.Scenario, 1200);
            _rig.enabled = false;
            _vr.SetMode(VehicleDrawMode.BatchRendererGroup);
            yield return null;
            const int w = 1688, h = 780;
            var mosaic = new Texture2D(w, h * 2, TextureFormat.RGBA32, false);
            yield return Tile(mosaic, 1, w, h, new Vector3(ShowcaseScenario.CrossX.x + 20f, 34f, ShowcaseScenario.CrossX.y - 30f), 52f);
            yield return Tile(mosaic, 0, w, h, new Vector3(190f, 150f, 0f), 58f);
            string path = Path.Combine(Path.GetTempPath(), "m2-vehiculos-brg.png");
            File.WriteAllBytes(path, mosaic.EncodeToPNG());
            Assert.That(_vr.Mode, Is.EqualTo(VehicleDrawMode.BatchRendererGroup));
            Debug.Log("[M2-06] captura BRG en " + path);
        }

        private IEnumerator Tile(Texture2D mosaic, int row, int w, int h, Vector3 pos, float pitch)
        {
            _cam.transform.SetPositionAndRotation(pos, Quaternion.Euler(pitch, 0f, 0f));
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            rt.Create();
            _cam.targetTexture = rt;
            for (int i = 0; i < 4; i++) yield return null;
            RenderTexture.active = rt;
            var tile = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tile.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tile.Apply();
            RenderTexture.active = null;
            mosaic.SetPixels(0, row * h, w, h, tile.GetPixels());
            mosaic.Apply();
            Object.Destroy(tile);
            _cam.targetTexture = null;
            rt.Release();
        }
    }
}
