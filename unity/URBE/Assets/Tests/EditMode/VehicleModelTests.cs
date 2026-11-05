using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Urbe.Game.Editor;
using Urbe.Game.Rendering.Vehicles;
using Urbe.Game.Sim;

namespace Urbe.Game.Tests
{
    /// <summary>M2-07: los 10 vehículos de Blender (tamaño real, LOD, orientación, atlas) y el reparto del batcher (M2-06).</summary>
    public class VehicleModelTests
    {
        // Longitud x ancho x alto en metros (igual que SPEC de tools/blender/make_vehicles.py).
        private static readonly float[][] Spec =
        {
            new[] { 3.7f, 1.66f, 1.50f }, new[] { 4.3f, 1.80f, 1.46f }, new[] { 4.7f, 1.94f, 1.80f }, new[] { 5.3f, 2.0f, 2.25f },
            new[] { 9.4f, 2.5f, 3.55f }, new[] { 11.4f, 2.55f, 3.42f }, new[] { 4.3f, 1.80f, 1.63f }, new[] { 5.7f, 2.1f, 2.62f },
            new[] { 8.6f, 2.5f, 3.38f }, new[] { 4.4f, 1.90f, 1.22f },
        };

        private static VehicleModelSet LoadSet()
        {
            AssetDatabase.Refresh();
            return VehicleAssetBuilder.EnsureModelSet();
        }

        [Test]
        public void DiezModelos_ConDosLod_YElSegundoTieneMenosTriangulos()
        {
            var set = LoadSet();
            Assert.That(set.entries.Length, Is.EqualTo(10));
            foreach (var e in set.entries)
            {
                Assert.That(e.lod0, Is.Not.Null, e.name + " LOD0");
                Assert.That(e.lod1, Is.Not.Null, e.name + " LOD1");
                Assert.That(e.lod1.triangles.Length, Is.LessThan(e.lod0.triangles.Length), e.name);
                Assert.That(e.lod0.triangles.Length / 3, Is.LessThan(500), e.name + " presupuesto de triángulos del LOD0");
                Assert.That(e.lod1.triangles.Length / 3, Is.LessThan(200), e.name + " presupuesto de triángulos del LOD1");
            }
        }

        [Test]
        public void Medidas_SonLasReales_EnMetros_YApoyadasEnElSuelo()
        {
            var set = LoadSet();
            for (int i = 0; i < 10; i++)
            {
                foreach (Mesh m in new[] { set.entries[i].lod0, set.entries[i].lod1 })
                {
                    Vector3 s = m.bounds.size;
                    string n = $"{set.entries[i].name} {m.name}";
                    Assert.That(s.z, Is.EqualTo(Spec[i][0]).Within(Spec[i][0] * 0.10f), n + " largo (Z)");
                    Assert.That(s.x, Is.EqualTo(Spec[i][1]).Within(Spec[i][1] * 0.10f), n + " ancho (X)");
                    Assert.That(s.y, Is.EqualTo(Spec[i][2]).Within(Spec[i][2] * 0.10f), n + " alto (Y)");
                    Assert.That(m.bounds.min.y, Is.InRange(-0.02f, 0.06f), n + " apoyado en y = 0");
                    Assert.That(m.bounds.center.x, Is.InRange(-0.05f, 0.05f), n + " centrado en X");
                    Assert.That(m.bounds.center.z, Is.InRange(-0.15f, 0.15f), n + " centrado en Z");
                }
            }
            // El coche sedán es la referencia de la tarjeta: ~4,3 x 1,8 m.
            Vector3 sedan = set.entries[VehicleModelIds.CarSedan].lod0.bounds.size;
            Assert.That(sedan.z, Is.InRange(4.1f, 4.6f));
            Assert.That(sedan.x, Is.InRange(1.7f, 1.95f));
        }

        [Test]
        public void ElFrente_EsMasZ_FarosAdelanteYPilotosAtras()
        {
            var set = LoadSet();
            foreach (int idx in new[] { VehicleModelIds.CarSedan, VehicleModelIds.CarSuv, VehicleModelIds.Truck })
            {
                Mesh m = set.entries[idx].lod0;
                Vector3[] v = m.vertices;
                Vector2[] uv = m.uv;
                float head = 0, tail = 0; int nh = 0, nt = 0;
                for (int k = 0; k < v.Length; k++)
                {
                    // Celdas del atlas (rejilla 8x8): HEAD = 4, TAIL = 5, ambas en la fila inferior.
                    if (uv[k].y > 0.125f) continue;
                    int col = (int)(uv[k].x * 8f);
                    if (col == 4) { head += v[k].z; nh++; }
                    else if (col == 5) { tail += v[k].z; nt++; }
                }
                Assert.That(nh, Is.GreaterThan(0));
                Assert.That(nt, Is.GreaterThan(0));
                Assert.That(head / nh, Is.GreaterThan(0.5f), set.entries[idx].name + ": los faros miran a +Z");
                Assert.That(tail / nt, Is.LessThan(-0.5f), set.entries[idx].name + ": los pilotos miran a -Z");
            }
        }

        [Test]
        public void Atlas_Es64x64_PuntoSinMipmapsYConAlfaDeTinte()
        {
            AssetDatabase.Refresh();
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(VehicleAssetBuilder.AtlasPath);
            Assert.That(tex, Is.Not.Null);
            Assert.That(tex.width, Is.EqualTo(64));
            Assert.That(tex.height, Is.EqualTo(64));
            Assert.That(tex.filterMode, Is.EqualTo(FilterMode.Point));
            Assert.That(tex.mipmapCount, Is.EqualTo(1));
            string path = Path.GetFullPath(VehicleAssetBuilder.AtlasPath);
            var raw = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Assert.That(raw.LoadImage(File.ReadAllBytes(path)));
            Assert.That(raw.GetPixel(4, 4).a, Is.EqualTo(1f).Within(0.01f), "celda PAINT: alfa 1 = se tine");
            Assert.That(raw.GetPixel(12, 4).a, Is.EqualTo(0f).Within(0.01f), "celda GLASS: alfa 0 = color fijo");
            UnityEngine.Object.DestroyImmediate(raw);
        }

        [Test]
        public void TipoDelNucleo_SeMapea_CocheVariado_CamionUnico()
        {
            var counts = new int[VehicleModelIds.Count];
            for (int id = 0; id < 3000; id++) counts[VehicleModelIds.Pick(0, id)]++;
            Assert.That(counts[VehicleModelIds.CarSmall], Is.GreaterThan(600));
            Assert.That(counts[VehicleModelIds.CarSedan], Is.GreaterThan(1000));
            Assert.That(counts[VehicleModelIds.CarSuv], Is.GreaterThan(400));
            Assert.That(counts.Count(c => c > 0), Is.EqualTo(3), "el coche usa solo 3 modelos (límite de draw calls)");
            Assert.That(VehicleModelIds.Pick(1, 17), Is.EqualTo(VehicleModelIds.Truck));
            Assert.That(VehicleModelIds.Pick(0, 5), Is.EqualTo(VehicleModelIds.Pick(0, 5)), "estable por id");
        }

        [Test]
        public void Batcher_ReparteLos3000_EnComoMuchoCuatroCubos_SinAsignarEnRegimen()
        {
            var set = LoadSet();
            var b = new VehicleBatcher(set);
            var arr = new InterpolatedVehicle[3000];
            for (int i = 0; i < arr.Length; i++)
                arr[i] = new InterpolatedVehicle { Id = i, Type = (byte)(i % 10 == 0 ? 1 : 0), X = i % 100 * 5f, Z = i / 100 * 5f, DirX = 0f, DirZ = 1f };
            b.Build(arr, arr.Length, null);
            Assert.That(b.Counts.Sum(), Is.EqualTo(3000));
            Assert.That(b.Counts.Count(c => c > 0), Is.LessThanOrEqualTo(4), "sin cámara todo es LOD0: 3 coches + camión");
            int bk = VehicleModelIds.Truck * VehicleBatcher.LodCount;
            Matrix4x4 m = b.Matrices[bk][0];
            Assert.That(m.m23, Is.LessThan(arr[0].Z), "el centro va detrás del frente");
            Assert.That(m.MultiplyVector(Vector3.forward).z, Is.EqualTo(1f).Within(1e-4f), "DirZ = 1 deja el modelo mirando a +Z");
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int k = 0; k < 20; k++) b.Build(arr, arr.Length, null);
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(delta, Is.EqualTo(0), "sin asignaciones tras el primer reparto");
        }
    }
}
