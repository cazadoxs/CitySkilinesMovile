using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Urbe.Game.CameraRig;
using Urbe.Game.Rendering.Buildings;
using Urbe.Game.Sim;
using Urbe.Sim.City;

namespace Urbe.Game.Tests
{
    /// <summary>
    /// M3-09: mide el render de 5.000 edificios (instancias de prueba repartidas por el mapa con el catálogo real: zonas, niveles y variantes
    /// mezclados, unos abandonados) desde dos distancias de cámara. Comprueba que no se asigna memoria por fotograma y apunta los números
    /// en docs/perf/m3-09-edificios.md (draw calls del renderer y del fotograma completo, CPU en ms, instancias visibles).
    /// </summary>
    public class BuildingPerfTests : UnityEngine.InputSystem.InputTestFixture
    {
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

        private sealed class Result
        {
            public string Label = "";
            public int Visible, Lod0, Boxes, Chunks, DrawCalls, FrameDrawCalls;
            public float Mean, P95;
            public long MaxAlloc;
        }

        private IEnumerator Measure(Camera cam, BuildingChunkRenderer br, string label, Vector3 pos, Quaternion rot, List<Result> results)
        {
            cam.transform.SetPositionAndRotation(pos, rot);
            cam.nearClipPlane = 5f;
            cam.farClipPlane = 6000f; // el rig ajusta los planos con la altura; aquí se fijan para ver el mapa entero
            for (int i = 0; i < 30; i++) yield return null; // calentamiento: arrays, shaders
            var cpu = new List<float>(200);
            var r = new Result { Label = label };
            using (var rec = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count"))
            {
                for (int i = 0; i < 200; i++)
                {
                    yield return null;
                    cpu.Add(br.LastCpuMs);
                    r.MaxAlloc = System.Math.Max(r.MaxAlloc, br.LastAllocBytes);
                    r.DrawCalls = System.Math.Max(r.DrawCalls, br.LastDrawCalls);
                    r.Visible = br.LastVisible;
                    r.Lod0 = br.LastLod0;
                    r.Boxes = br.LastBoxes;
                    r.Chunks = br.LastChunksVisible;
                    long f = rec.Valid ? rec.LastValue : 0;
                    r.FrameDrawCalls = (int)System.Math.Max(r.FrameDrawCalls, System.Math.Max(f, EditorDrawCalls()));
                }
            }
            float mean = 0f;
            foreach (float c in cpu) mean += c;
            r.Mean = mean / cpu.Count;
            r.P95 = Percentile(cpu, 0.95f);
            results.Add(r);
            Debug.Log($"[M3-09] {label}: visibles {r.Visible} (LOD0 {r.Lod0}, cajas {r.Boxes}) en {r.Chunks} chunks | draw calls de edificios {r.DrawCalls} | " +
                      $"draw calls del fotograma {r.FrameDrawCalls} | CPU media {r.Mean:F3} ms, p95 {r.P95:F3} ms | bytes por fotograma (max) {r.MaxAlloc}");
        }

        [UnityTest]
        public IEnumerator CincoMilEdificios_DrawCalls_Y_Milisegundos()
        {
            SceneManager.LoadScene("Main");
            yield return null;
            yield return null;
            var host = Object.FindFirstObjectByType<SimHost>();
            var br = Object.FindFirstObjectByType<BuildingChunkRenderer>();
            var rig = Object.FindFirstObjectByType<CityCameraController>();
            host.SetSpeed(SimSpeed.Paused);
            rig.enabled = false;
            var cam = rig.GetComponent<Camera>();
            yield return null;
            var picker = br.Picker!;
            Assert.That(picker.Count, Is.EqualTo(115));

            // 5.000 edificios en una rejilla de 72 x 70 sobre el mapa (28 m): todas las zonas, niveles y variantes.
            var zones = new[] { ZoneType.Residential, ZoneType.Commercial, ZoneType.Industrial, ZoneType.Office };
            uint seed = 1234567u;
            int n = 0;
            var used = new HashSet<int>();
            for (int gx = 0; gx < 72 && n < 5000; gx++)
                for (int gz = 0; gz < 70 && n < 5000; gz++)
                {
                    seed = seed * 1664525u + 1013904223u;
                    ZoneType z = zones[(seed >> 8) % 4];
                    int level = 1 + (int)((seed >> 12) % 5);
                    int dens = z == ZoneType.Residential ? 1 + (int)((seed >> 16) % 3) : (z == ZoneType.Commercial ? 1 + (int)((seed >> 16) % 2) : (z == ZoneType.Office ? 2 : 1));
                    int w = 1 + (int)((seed >> 20) % 3), d = 1 + (int)((seed >> 24) % 3);
                    int model = picker.PickZoned(z, dens, level, w, d, seed);
                    Assert.That(model, Is.GreaterThanOrEqualTo(0));
                    used.Add(model);
                    Vector3 sc = BuildingModelPicker.FitScale(picker.Entry(model), w, d, false);
                    bool abandoned = (seed >> 28) == 0;
                    Vector4 tint = abandoned ? BuildingModelPicker.AbandonedTint : BuildingModelPicker.TintFor(z, seed);
                    br.AddDebugInstance(model, new Vector3(-980f + gx * 27.5f, 0f, -960f + gz * 27.5f), ((seed >> 5) % 4) * 90f, sc, tint);
                    n++;
                }
            Assert.That(br.InstanceCount, Is.EqualTo(5000));
            Debug.Log($"[M3-09] 5.000 instancias de {used.Count} modelos distintos en {br.ChunkCount} chunks");

            var results = new List<Result>();
            // Toda la ciudad a la vista (casi todo en LOD1) y vista de calle (cerca, LOD0, la mayoría descartada por frustum).
            yield return Measure(cam, br, "vista general (todo el mapa)", new Vector3(0f, 3000f, -52f), Quaternion.Euler(89f, 0f, 0f), results);
            yield return Measure(cam, br, "vista media (700 m)", new Vector3(0f, 520f, -450f), Quaternion.Euler(50f, 0f, 0f), results);
            yield return Measure(cam, br, "vista de calle (150 m)", new Vector3(0f, 110f, -90f), Quaternion.Euler(45f, 0f, 0f), results);

            foreach (Result r in results)
            {
                Assert.That(r.MaxAlloc, Is.EqualTo(0), r.Label + ": sin asignaciones por fotograma");
                Assert.That(r.Visible, Is.GreaterThan(0), r.Label);
                Assert.That(r.DrawCalls, Is.LessThan(260), r.Label + ": un cubo por modelo y LOD (115 x 2) como máximo");
            }
            WriteReport(results, used.Count, br.ChunkCount);
        }

        private static void WriteReport(List<Result> results, int models, int chunks)
        {
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "docs", "perf", "m3-09-edificios.md"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# M3-09 · Render de edificios: medición con 5.000 edificios");
            sb.AppendLine();
            sb.AppendLine("Generado por `BuildingPerfTests` (PlayMode, editor de Unity 6000.1.4f1, batchmode con gráficos, PC). 5.000 instancias de prueba repartidas por el mapa de 2 x 2 km,");
            sb.AppendLine($"con el catálogo real ({models} modelos distintos usados de 115, zonas, niveles y variantes mezclados, un 6 % abandonados) en {chunks} chunks de 128 m.");
            sb.AppendLine("Render: `BuildingChunkRenderer` (RenderMeshInstanced, un cubo por modelo y LOD, descarte por frustum a nivel de chunk, LOD1 a más de 260 m, caja con tinte a más de 700 m).");
            sb.AppendLine();
            sb.AppendLine("| Vista | Edificios dibujados | LOD0 | Cajas lejanas | Chunks visibles | Draw calls de edificios | Draw calls del fotograma | CPU media (ms) | CPU p95 (ms) | Bytes gestionados/fotograma |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
            foreach (Result r in results)
                sb.AppendLine($"| {r.Label} | {r.Visible} | {r.Lod0} | {r.Boxes} | {r.Chunks} | {r.DrawCalls} | {(r.FrameDrawCalls > 0 ? r.FrameDrawCalls.ToString() : "n/d")} | {r.Mean:F3} | {r.P95:F3} | {r.MaxAlloc} |");
            sb.AppendLine();
            sb.AppendLine("Los draw calls del fotograma (Profiler/UnityStats) incluyen suelo, carreteras, interfaz y edificios; en batchmode no siempre están disponibles (n/d). La CPU es solo la del renderer (reunir cubos y enviar instancias); la GPU no se mide en batchmode.");
            sb.AppendLine("No medido: dispositivo móvil (pendiente de una pasada en el móvil, igual que M2-06).");
            File.WriteAllText(path, sb.ToString());
        }
    }
}
