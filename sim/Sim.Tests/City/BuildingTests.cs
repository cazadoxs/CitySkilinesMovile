using System;
using System.Collections.Generic;
using Urbe.Sim.City;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Xunit;

namespace Urbe.Sim.Tests.City
{
    public class BuildingTests
    {
        /// <summary>Dos calles que se cruzan, con R en la horizontal, C al sur e I al norte.</summary>
        private static CitySimulation Town(CityBalance? bal = null, ulong seed = 3, bool supply = true)
        {
            CitySimulation c = CityKit.New(seed, bal);
            CityKit.MustRoad(c, -160, 0, 0, 0);
            CityKit.MustRoad(c, 0, 0, 160, 0);
            CityKit.MustRoad(c, 0, -160, 0, 0);
            CityKit.MustRoad(c, 0, 0, 0, 160);
            if (supply) c.SupplyOverride = true;
            c.Step();
            Paint(c, -80, 0, ZoneType.Residential, 1);
            Paint(c, 80, 0, ZoneType.Residential, 2);
            Paint(c, 0, -80, ZoneType.Commercial, 1);
            Paint(c, 0, 80, ZoneType.Industrial, 1);
            return c;
        }

        private static void Paint(CitySimulation c, float x, float z, ZoneType zone, int density)
        {
            EntityId seg = CityKit.SegmentAt(c, x, z);
            Assert.Equal(CityResult.Ok, c.Execute(PaintZoneCommand.FillSegment(seg, 4, zone, density)));
        }

        private static void AssertBuildingsConsistent(CitySimulation c)
        {
            var seen = new HashSet<int>();
            BuildingSystem bs = c.Buildings;
            int alive = 0;
            for (int i = 0; i < bs.IndexBound; i++)
            {
                if (!bs.IsAlive(i)) continue;
                alive++;
                ReadOnlySpan<int> cells = bs.CellsOf(i);
                Assert.Equal(bs.Get(i).Width * bs.Get(i).Depth, cells.Length);
                for (int k = 0; k < cells.Length; k++)
                {
                    Assert.True(c.Zones.IsCellAlive(cells[k]));
                    Assert.Equal(i, c.Zones.GetCell(cells[k]).Building);
                    Assert.True(seen.Add(cells[k]), "celda compartida entre edificios");
                }
            }
            Assert.Equal(alive, bs.Count);
            // Toda celda ocupada apunta a un edificio vivo que la contiene.
            for (int ci = 0; ci < c.Zones.CellIndexBound; ci++)
            {
                if (!c.Zones.IsCellAlive(ci)) continue;
                int b = c.Zones.GetCell(ci).Building;
                if (b < 0) continue;
                Assert.True(bs.IsAlive(b));
                Assert.Contains(ci, seen);
            }
        }

        [Fact]
        public void NoGrowthWithoutWaterAndPower()
        {
            CitySimulation c = Town(supply: false);
            c.Run(3000);
            Assert.Equal(0, c.Buildings.Count);
            c.SupplyOverride = true;
            c.Run(3000);
            Assert.True(c.Buildings.Count > 3);
        }

        [Fact]
        public void CityGrowsAlongZonesWithValidParcels()
        {
            CitySimulation c = Town();
            c.Run(12000);
            BuildingSystem bs = c.Buildings;
            Assert.True(bs.Count > 20, "edificios: " + bs.Count);
            AssertBuildingsConsistent(c);
            var zonesSeen = new HashSet<ZoneType>();
            for (int i = 0; i < bs.IndexBound; i++)
            {
                if (!bs.IsAlive(i)) continue;
                ref readonly Building b = ref bs.Get(i);
                zonesSeen.Add(b.Zone);
                BuildingRules.ParcelRange(b.Zone, b.Density, out int wMin, out int wMax, out int hMin, out int hMax);
                Assert.InRange((int)b.Width, 1, wMax);
                Assert.InRange((int)b.Depth, 1, hMax);
                Assert.True(b.Capacity > 0);
                Assert.True(b.Segment.IsValid && c.Network.IsSegmentAlive(b.Segment));
                // Todas las celdas son de su zona y su densidad, y la primera linea esta pegada a la calle.
                ReadOnlySpan<int> cells = bs.CellsOf(i);
                bool front = false;
                for (int k = 0; k < cells.Length; k++)
                {
                    ref readonly ZoneCell zc = ref c.Zones.GetCell(cells[k]);
                    Assert.Equal(b.Zone, zc.Zone);
                    Assert.Equal(b.Density, zc.Density);
                    if (zc.Depth == 0) front = true;
                }
                Assert.True(front);
                if (b.Zone == ZoneType.Residential) { Assert.Equal(0, b.JobsLow + b.JobsHigh); Assert.True(b.Occupants > 0); }
                else Assert.Equal(b.Capacity, b.JobsLow + b.JobsHigh);
            }
            Assert.Contains(ZoneType.Residential, zonesSeen);
            Assert.Contains(ZoneType.Commercial, zonesSeen);
            Assert.Contains(ZoneType.Industrial, zonesSeen);
            Assert.True(c.Stats.Population > 50);
        }

        [Fact]
        public void EventsMatchBuildingCount()
        {
            CitySimulation c = Town();
            c.Run(8000);
            int created = 0, demolished = 0;
            while (c.Buildings.TryDequeueEvent(out BuildingEvent e))
            {
                if (e.Kind == BuildingEventKind.Created) created++;
                else if (e.Kind == BuildingEventKind.Demolished) demolished++;
            }
            Assert.True(created > 0);
            Assert.Equal(c.Buildings.Count, created - demolished);
            Assert.Equal(0, c.Buildings.DroppedEvents);
        }

        [Fact]
        public void LevelRulesFollowLandValueServicesAndEducation()
        {
            var bal = new CityBalance();
            int all = 0x1FF;
            int Mask(params CoverageType[] t) { int m = 0; foreach (CoverageType x in t) m |= 1 << (int)x; return m; }
            Assert.Equal(1, BuildingRules.MaxLevel(bal, ZoneType.Residential, 10f, all, 3f));
            Assert.Equal(2, BuildingRules.MaxLevel(bal, ZoneType.Residential, 25f, Mask(CoverageType.Garbage, CoverageType.Health), 0.6f));
            // Sin sanidad no pasa de 1 aunque el suelo valga.
            Assert.Equal(1, BuildingRules.MaxLevel(bal, ZoneType.Residential, 90f, Mask(CoverageType.Garbage), 3f));
            // Nivel 3 pide policia, bomberos y parque; nivel 4 transporte; nivel 5 ocio.
            int l3 = Mask(CoverageType.Garbage, CoverageType.Health, CoverageType.Police, CoverageType.Fire, CoverageType.Park);
            Assert.Equal(3, BuildingRules.MaxLevel(bal, ZoneType.Residential, 90f, l3, 3f));
            Assert.Equal(4, BuildingRules.MaxLevel(bal, ZoneType.Residential, 90f, l3 | Mask(CoverageType.Transit), 3f));
            Assert.Equal(5, BuildingRules.MaxLevel(bal, ZoneType.Residential, 90f, all, 3f));
            // La educacion solo cuenta para residencial y oficinas.
            Assert.Equal(1, BuildingRules.MaxLevel(bal, ZoneType.Residential, 90f, all, 0.2f));
            Assert.Equal(5, BuildingRules.MaxLevel(bal, ZoneType.Industrial, 90f, all, 0f));
        }

        [Fact]
        public void BuildingsLevelUpWhenRequirementsHold()
        {
            var bal = new CityBalance { DefaultLandValue = 100f, LevelUpUpdates = 8 };
            CitySimulation c = Town(bal);
            c.Run(14000);
            int maxLevel = 0;
            for (int i = 0; i < c.Buildings.IndexBound; i++)
                if (c.Buildings.IsAlive(i)) maxLevel = Math.Max(maxLevel, c.Buildings.Get(i).Level);
            Assert.True(maxLevel >= 4, "nivel maximo " + maxLevel);
            bool leveled = false;
            while (c.Buildings.TryDequeueEvent(out BuildingEvent e)) if (e.Kind == BuildingEventKind.LevelChanged) leveled = true;
            Assert.True(leveled);
            // Un edificio de nivel alto tiene mas capacidad que su nivel 1.
            for (int i = 0; i < c.Buildings.IndexBound; i++)
            {
                if (!c.Buildings.IsAlive(i)) continue;
                ref readonly Building b = ref c.Buildings.Get(i);
                Assert.Equal(BuildingRules.CapacityOf(bal, b.Zone, b.Density, b.Level, b.Width * b.Depth), (int)b.Capacity);
            }
        }

        [Fact]
        public void LowLandValueKeepsBuildingsAtLevelOne()
        {
            var bal = new CityBalance { DefaultLandValue = 5f, LevelUpUpdates = 4 };
            CitySimulation c = Town(bal);
            c.Run(8000);
            for (int i = 0; i < c.Buildings.IndexBound; i++)
                if (c.Buildings.IsAlive(i)) Assert.Equal((byte)1, c.Buildings.Get(i).Level);
        }

        [Fact]
        public void LosingSupplyAbandonsThenDemolishesAndFreesCells()
        {
            var bal = new CityBalance { AbandonUpdates = 5, DemolishAbandonedUpdates = 5 };
            CitySimulation c = Town(bal);
            c.Run(8000);
            int built = c.Buildings.Count;
            Assert.True(built > 10);
            int freeBefore = c.Zones.ZonedFreeCount(ZoneType.Residential);
            c.SupplyOverride = false;
            c.Run(5 * 20 + 40);
            int abandoned = 0;
            for (int i = 0; i < c.Buildings.IndexBound; i++)
                if (c.Buildings.IsAlive(i) && c.Buildings.Get(i).IsAbandoned)
                {
                    abandoned++;
                    Assert.NotEqual(BuildingProblems.None, c.Buildings.Get(i).Problems & (BuildingProblems.NoWater | BuildingProblems.NoPower));
                }
            Assert.True(abandoned > 0);
            c.Run(5 * 20 + 40);
            Assert.Equal(0, c.Buildings.Count);
            Assert.True(c.Zones.ZonedFreeCount(ZoneType.Residential) > freeBefore);
            AssertBuildingsConsistent(c);
        }

        [Fact]
        public void NewRoadDemolishesOnlyBuildingsItCuts()
        {
            CitySimulation c = Town();
            c.Run(10000);
            BuildingSystem bs = c.Buildings;
            var before = new List<EntityId>();
            for (int i = 0; i < bs.IndexBound; i++) if (bs.IsAlive(i)) before.Add(bs.Get(i).Id);
            Assert.True(before.Count > 15);
            // Calle nueva que atraviesa la zona residencial del este.
            Assert.Equal(CityResult.Ok, CityKit.Road(c, 60, -40, 60, 40));
            AssertBuildingsConsistent(c);
            int survivors = 0;
            foreach (EntityId id in before) if (bs.IsAlive(id)) survivors++;
            Assert.InRange(survivors, before.Count / 2, before.Count - 1);
            // Los que sobreviven siguen con celdas validas y fuera de la nueva calzada.
            for (int i = 0; i < bs.IndexBound; i++)
            {
                if (!bs.IsAlive(i)) continue;
                Vec2 p = bs.Get(i).Pos;
                Assert.False(Math.Abs(p.X - 60f) < 5f && Math.Abs(p.Z) < 40f, "edificio sobre la calzada nueva");
            }
        }

        [Fact]
        public void RepaintedZoneReplacesTheBuilding()
        {
            var bal = new CityBalance { WrongZoneAbandonUpdates = 3, DemolishAbandonedUpdates = 3 };
            CitySimulation c = Town(bal);
            c.Run(8000);
            int res = 0;
            for (int i = 0; i < c.Buildings.IndexBound; i++) if (c.Buildings.IsAlive(i) && c.Buildings.Get(i).Zone == ZoneType.Residential) res++;
            Assert.True(res > 3, "res=" + res + " total=" + c.Buildings.Count + " demR=" + c.Demand.R + " pop=" + c.Stats.Population + " free=" + c.Zones.ZonedFreeCount(ZoneType.Residential));
            // Se repinta toda la residencial de baja densidad con la zona que mas demanda tenga, con impuestos minimos para asegurarla.
            ZoneType target = c.Demand.C >= c.Demand.I ? ZoneType.Commercial : ZoneType.Industrial;
            c.SetTaxRate(target, 1f);
            Assert.Equal(CityResult.Ok, c.Execute(PaintZoneCommand.Brush(CityKit.V(-80, 0), 120f, target, 1)));
            c.Run(400);
            for (int i = 0; i < c.Buildings.IndexBound; i++)
            {
                if (!c.Buildings.IsAlive(i)) continue;
                ref readonly Building b = ref c.Buildings.Get(i);
                if (b.Pos.X < -20f && Math.Abs(b.Pos.Z) < 40f) Assert.NotEqual(ZoneType.Residential, b.Zone);
            }
            c.Run(6000);
            int newCommercial = 0;
            for (int i = 0; i < c.Buildings.IndexBound; i++)
                if (c.Buildings.IsAlive(i) && c.Buildings.Get(i).Zone == target && c.Buildings.Get(i).Pos.X < -20f && Math.Abs(c.Buildings.Get(i).Pos.Z) < 40f) newCommercial++;
            Assert.True(newCommercial > 0, "dem R=" + c.Demand.R + " C=" + c.Demand.C + " I=" + c.Demand.I + " pop=" + c.Stats.Population + " cj=" + c.Stats.CommercialJobs + " freeC=" + c.Zones.ZonedFreeCount(ZoneType.Commercial) + " n=" + c.Buildings.Count);
            AssertBuildingsConsistent(c);
        }

        [Fact]
        public void DemandReactsToTheCityState()
        {
            var bal = new CityBalance();
            var s = new CityStats { Population = 1000, HousingCapacity = 1000, HousingVacant = 0, AverageHappiness = 60f };
            // Sin empleos libres y con parados, la residencial cae; sin comercio, el comercial pide.
            s.UnemployedLow = 300;
            DemandState d = DemandModel.Compute(bal, s, 9, 9, 9, 9);
            Assert.True(d.R < 40f);
            Assert.True(d.C > 50f);
            Assert.True(d.I > 40f);
            // Mucha vivienda vacia frena la residencial.
            var vac = new CityStats { Population = 100, HousingCapacity = 1000, HousingVacant = 900, AverageHappiness = 60f };
            Assert.True(DemandModel.Compute(bal, vac, 9, 9, 9, 9).R < 0f);
            // Los impuestos altos bajan la demanda.
            Assert.True(DemandModel.Compute(bal, s, 20, 20, 20, 20).C < d.C);
            // Titulados sin trabajo cualificado piden oficinas.
            var edu = new CityStats { Population = 2000, UnemployedHigh = 200 };
            Assert.True(DemandModel.Compute(bal, edu, 9, 9, 9, 9).O > 50f);
            Assert.True(d.O <= 0f);
        }

        [Fact]
        public void DeterministicWithBuildings()
        {
            ulong Run()
            {
                CitySimulation c = Town(seed: 11);
                c.Run(6000);
                return c.ComputeHash();
            }
            ulong a = Run();
            Assert.Equal(a, Run());
            CitySimulation other = Town(seed: 12);
            other.Run(6000);
            Assert.NotEqual(a, other.ComputeHash());
        }
    }
}
