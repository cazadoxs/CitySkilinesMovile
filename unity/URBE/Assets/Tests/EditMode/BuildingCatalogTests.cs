using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Urbe.Game.Rendering.Buildings;
using Urbe.Sim.City;

namespace Urbe.Game.Tests
{
    /// <summary>M3-09: el catálogo importado y la elección de modelo (zona, densidad, nivel, parcela y semilla).</summary>
    public class BuildingCatalogTests
    {
        private static BuildingCatalog Load()
        {
            var c = AssetDatabase.LoadAssetAtPath<BuildingCatalog>("Assets/_Project/Art/Buildings/BuildingCatalog.asset");
            Assert.That(c, Is.Not.Null, "falta el catálogo: menú URBE > Importar edificios");
            return c;
        }

        [Test]
        public void Catalogo_Tiene115Modelos_ConLod0YLod1()
        {
            BuildingCatalog c = Load();
            Assert.That(c.entries.Length, Is.EqualTo(115));
            foreach (BuildingCatalog.Entry e in c.entries)
            {
                Assert.That(e.lod0, Is.Not.Null, e.id);
                Assert.That(e.lod1, Is.Not.Null, e.id);
                Assert.That(e.lod1.triangles.Length, Is.LessThanOrEqualTo(e.lod0.triangles.Length), e.id + ": el LOD1 no es más pesado");
                Assert.That(e.size.x, Is.GreaterThan(1f), e.id);
                Assert.That(e.lod0.bounds.min.y, Is.GreaterThan(-0.2f), e.id + ": la base está en el suelo");
            }
        }

        [Test]
        public void Elegir_MismaSemilla_MismoModelo_Y_SemillasDistintas_DanVariedad()
        {
            var p = new BuildingModelPicker(Load());
            int a = p.PickZoned(ZoneType.Residential, 1, 2, 2, 2, 777u);
            Assert.That(p.PickZoned(ZoneType.Residential, 1, 2, 2, 2, 777u), Is.EqualTo(a), "determinista");
            var seen = new HashSet<int>();
            for (uint s = 0; s < 200; s++) seen.Add(p.PickZoned(ZoneType.Residential, 1, 2, 2, 2, s));
            Assert.That(seen.Count, Is.GreaterThanOrEqualTo(2), "las variantes A, B y C salen según la semilla");
        }

        [Test]
        public void Elegir_RespetaZonaDensidadYNivel()
        {
            BuildingCatalog c = Load();
            var p = new BuildingModelPicker(c);
            foreach (ZoneType z in new[] { ZoneType.Residential, ZoneType.Commercial, ZoneType.Industrial, ZoneType.Office })
                for (int level = 1; level <= 5; level++)
                {
                    int m = p.PickZoned(z, z == ZoneType.Residential ? 2 : 1, level, 2, 2, 5u);
                    Assert.That(m, Is.GreaterThanOrEqualTo(0), z + " L" + level);
                    Assert.That(BuildingModelPicker.ZoneIndex(c.entries[m].zone), Is.EqualTo((int)z));
                    Assert.That(c.entries[m].level, Is.EqualTo(level));
                }
            int r2 = p.PickZoned(ZoneType.Residential, 3, 4, 3, 3, 1u);
            Assert.That(c.entries[r2].density, Is.EqualTo(3), "densidad alta pide modelo de densidad alta");
            Assert.That(c.entries[r2].footprintW, Is.EqualTo(3), "la huella más próxima a la parcela");
        }

        [Test]
        public void Servicios_TodosTienenModelo()
        {
            var p = new BuildingModelPicker(Load());
            foreach (ServiceType t in System.Enum.GetValues(typeof(ServiceType)))
            {
                if (t == ServiceType.None) continue;
                Assert.That(p.PickService(t), Is.GreaterThanOrEqualTo(0), t.ToString());
            }
        }

        [Test]
        public void EscalaDeParcela_NoDeformaDeMas()
        {
            BuildingCatalog c = Load();
            foreach (BuildingCatalog.Entry e in c.entries)
            {
                Vector3 s = BuildingModelPicker.FitScale(e, e.footprintW, e.footprintD, e.zone == "SVC");
                Assert.That(s.x, Is.InRange(0.75f, 1.6f), e.id);
                Assert.That(s.y, Is.InRange(0.75f, 1.6f), e.id);
                Assert.That(s.z, Is.InRange(0.75f, 1.6f), e.id);
            }
        }

        [Test]
        public void Tintes_SonEstables_Y_ElAbandonadoOscurece()
        {
            Assert.That(BuildingModelPicker.TintFor(ZoneType.Commercial, 9u), Is.EqualTo(BuildingModelPicker.TintFor(ZoneType.Commercial, 9u)));
            Assert.That(BuildingModelPicker.TintFor(ZoneType.Residential, 3u).w, Is.EqualTo(1f));
            Assert.That(BuildingModelPicker.AbandonedTint.w, Is.EqualTo(0f), "alfa 0 oscurece todo el modelo en el shader");
        }
    }
}
