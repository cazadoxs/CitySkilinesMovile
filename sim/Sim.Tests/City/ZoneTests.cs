using System;
using Urbe.Sim.City;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Xunit;

namespace Urbe.Sim.Tests.City
{
    internal static class CityKit
    {
        public static CitySimulation New(ulong seed = 1, CityBalance? bal = null) => new CitySimulation(seed, bal);

        public static Vec2 V(float x, float z) => new Vec2(x, z);

        public static CityResult Road(CitySimulation c, float x0, float z0, float x1, float z1, ushort type = RoadTypeIds.Street2)
            => c.Execute(new BuildRoadCityCommand(SegmentRequest.Straight(V(x0, z0), V(x1, z1), type)));

        public static void MustRoad(CitySimulation c, float x0, float z0, float x1, float z1, ushort type = RoadTypeIds.Street2)
            => Assert.Equal(CityResult.Ok, Road(c, x0, z0, x1, z1, type));

        /// <summary>Todos los segmentos zonificables vivos.</summary>
        public static EntityId SegmentAt(CitySimulation c, float x, float z)
        {
            Assert.True(c.Network.NearestSegment(V(x, z), 4f, out EntityId seg, out _, out _));
            return seg;
        }

        /// <summary>Comprueba el invariante de celdas: ninguna dentro de una calzada y ninguna solapada con otra.</summary>
        public static void AssertCellsValid(CitySimulation c)
        {
            ZoneGrid z = c.Zones;
            int bound = z.CellIndexBound;
            for (int i = 0; i < bound; i++)
            {
                if (!z.IsCellAlive(i)) continue;
                ref readonly ZoneCell a = ref z.GetCell(i);
                Assert.True(c.Network.IsSegmentAlive(a.Segment), "celda de un segmento muerto");
                for (int s = 0; s < c.Network.SegmentIndexBound; s++)
                {
                    EntityId id = c.Network.SegmentIdAt(s);
                    if (!id.IsValid) continue;
                    ref readonly Segment sg = ref c.Network.GetSegment(id);
                    float half = c.Network.Types.Get(sg.RoadTypeId).TotalWidth * 0.5f;
                    float d = Vec2.Distance(sg.Curve.Evaluate(sg.Curve.ClosestT(a.Pos)), a.Pos);
                    // El centro de la celda queda fuera de toda calzada con margen.
                    Assert.True(d >= half + 2f - 0.01f, "celda " + i + " dentro de la calzada del segmento " + s + " (d=" + d + ")");
                }
                for (int j = i + 1; j < bound; j++)
                {
                    if (!z.IsCellAlive(j)) continue;
                    ref readonly ZoneCell b = ref z.GetCell(j);
                    if (Vec2.Distance(a.Pos, b.Pos) < 6.4f) Assert.Fail("celdas " + i + " y " + j + " solapadas a " + Vec2.Distance(a.Pos, b.Pos));
                }
            }
        }
    }

    public class ZoneTests
    {
        [Fact]
        public void StraightStreetGeneratesCellsOnBothSides()
        {
            CitySimulation c = CityKit.New();
            CityKit.MustRoad(c, 0, 0, 160, 0);
            c.Step();
            EntityId seg = CityKit.SegmentAt(c, 80, 0);
            int n = c.Zones.AlongCount(seg);
            Assert.InRange(n, 18, 20);
            // 2 lados x n x 4 de fondo en una calle recta sin cruces.
            Assert.Equal(2 * n * 4, c.Zones.CellCount - CountOtherCells(c, seg));
            // El fondo 0 queda pegado a la calzada: ancho/2 + 4.
            int cell = c.Zones.CellAt(seg, 0, 3, 0);
            Assert.True(cell >= 0);
            ref readonly ZoneCell zc = ref c.Zones.GetCell(cell);
            Assert.InRange(Math.Abs(zc.Pos.Z), 8.9f, 9.1f);
            Assert.Equal(0, zc.Depth);
            // Dir apunta a la calzada.
            Assert.True(zc.Dir.Z * zc.Pos.Z < 0f);
            CityKit.AssertCellsValid(c);
        }

        private static int CountOtherCells(CitySimulation c, EntityId seg)
        {
            int others = 0;
            for (int i = 0; i < c.Zones.CellIndexBound; i++)
                if (c.Zones.IsCellAlive(i) && c.Zones.GetCell(i).Segment != seg) others++;
            return others;
        }

        [Fact]
        public void CurvedRoadHasNoOverlapsAndNoCellsOnTheRoad()
        {
            CitySimulation c = CityKit.New();
            var req = new SegmentRequest(CityKit.V(0, 0), CityKit.V(60, 0), CityKit.V(100, 40), CityKit.V(100, 100), RoadTypeIds.Street2);
            Assert.Equal(CityResult.Ok, c.Execute(new BuildRoadCityCommand(req)));
            c.Step();
            Assert.True(c.Zones.CellCount > 40);
            CityKit.AssertCellsValid(c);
        }

        [Fact]
        public void TightCurveDropsInnerCells()
        {
            CitySimulation c = CityKit.New();
            // Curva cerrada: las celdas del lado interior de fondo alto se descartan sin solaparse.
            var req = new SegmentRequest(CityKit.V(0, 0), CityKit.V(50, 0), CityKit.V(50, 40), CityKit.V(0, 40), RoadTypeIds.Street2);
            Assert.Equal(CityResult.Ok, c.Execute(new BuildRoadCityCommand(req)));
            c.Step();
            CityKit.AssertCellsValid(c);
        }

        [Fact]
        public void CrossingRoadsDoNotOverlapAndJunctionIsClear()
        {
            CitySimulation c = CityKit.New();
            CityKit.MustRoad(c, -100, 0, 100, 0);
            CityKit.MustRoad(c, 0, -100, 0, 100, RoadTypeIds.Avenue4);
            c.Step();
            CityKit.AssertCellsValid(c);
            // Nada de celdas en el cruce.
            Assert.True(c.Zones.FindCell(CityKit.V(0, 0), 12f) < 0);
            Assert.True(c.Zones.CellCount > 200);
        }

        [Fact]
        public void NonZonableRoadsHaveNoCells()
        {
            CitySimulation c = CityKit.New();
            c.Step();
            Assert.Equal(0, c.Zones.CellCount); // solo la autopista
            CityKit.MustRoad(c, 0, 0, 100, 0, RoadTypeIds.Rural2);
            c.Step();
            Assert.Equal(0, c.Zones.CellCount);
        }

        [Fact]
        public void PaintBrushAndEraseWithUndo()
        {
            CitySimulation c = CityKit.New();
            CityKit.MustRoad(c, 0, 0, 160, 0);
            c.Step();
            var paint = PaintZoneCommand.Brush(CityKit.V(80, 0), 30f, ZoneType.Residential, 1);
            Assert.Equal(CityResult.Ok, c.Execute(paint));
            int painted = c.Zones.ZonedCount(ZoneType.Residential);
            Assert.True(painted > 10);
            Assert.Equal(painted, paint.Changed);
            // Repintar lo mismo no cambia nada.
            var again = PaintZoneCommand.Brush(CityKit.V(80, 0), 30f, ZoneType.Residential, 1);
            c.Execute(again);
            Assert.Equal(0, again.Changed);
            // Cambiar a comercial y deshacer vuelve a residencial.
            c.Execute(PaintZoneCommand.Brush(CityKit.V(80, 0), 30f, ZoneType.Commercial, 2));
            Assert.Equal(0, c.Zones.ZonedCount(ZoneType.Residential));
            Assert.True(c.Undo());
            Assert.Equal(painted, c.Zones.ZonedCount(ZoneType.Residential));
            Assert.Equal(0, c.Zones.ZonedCount(ZoneType.Commercial));
            // Borrar y deshacer.
            c.Execute(PaintZoneCommand.Brush(CityKit.V(80, 0), 60f, ZoneType.None));
            Assert.Equal(0, c.Zones.ZonedCount(ZoneType.Residential));
            Assert.True(c.Undo());
            Assert.Equal(painted, c.Zones.ZonedCount(ZoneType.Residential));
        }

        [Fact]
        public void LassoAndSegmentFill()
        {
            CitySimulation c = CityKit.New();
            CityKit.MustRoad(c, 0, 0, 160, 0);
            c.Step();
            EntityId seg = CityKit.SegmentAt(c, 80, 0);
            Assert.Equal(CityResult.Ok, c.Execute(PaintZoneCommand.FillSegment(seg, 2, ZoneType.Industrial)));
            int n = c.Zones.AlongCount(seg);
            Assert.Equal(2 * n * 2, c.Zones.ZonedCount(ZoneType.Industrial));
            var lasso = PaintZoneCommand.Lasso(new[] { CityKit.V(10, -50), CityKit.V(60, -50), CityKit.V(60, 50), CityKit.V(10, 50) }, ZoneType.Residential, 3);
            Assert.Equal(CityResult.Ok, c.Execute(lasso));
            Assert.True(c.Zones.ZonedCount(ZoneType.Residential) > 20);
            // La densidad se normaliza por zona: industrial siempre 1.
            int cell = c.Zones.CellAt(seg, 0, 15, 0);
            Assert.Equal((byte)1, c.Zones.GetCell(cell).Density);
        }

        [Fact]
        public void ZoneSurvivesCrossingStreetAndSplit()
        {
            CitySimulation c = CityKit.New();
            CityKit.MustRoad(c, 0, 0, 160, 0);
            c.Step();
            EntityId seg = CityKit.SegmentAt(c, 80, 0);
            c.Execute(PaintZoneCommand.FillSegment(seg, 4, ZoneType.Residential, 2));
            int before = c.Zones.ZonedCount(ZoneType.Residential);
            Assert.True(before > 100);
            // Una calle que cruza a mitad parte el segmento en dos.
            CityKit.MustRoad(c, 80, -100, 80, 100);
            c.Step();
            CityKit.AssertCellsValid(c);
            int after = c.Zones.ZonedCount(ZoneType.Residential);
            // Se pierden las celdas del cruce, pero se conserva casi todo lo demas, con su densidad.
            Assert.InRange(after, before * 0.7, before);
            int far = c.Zones.FindCell(CityKit.V(20, 9), 5f);
            Assert.True(far >= 0);
            Assert.Equal(ZoneType.Residential, c.Zones.GetCell(far).Zone);
            Assert.Equal((byte)2, c.Zones.GetCell(far).Density);
            // Las celdas nuevas de la calle transversal no tienen zona.
            int fresh = c.Zones.FindCell(CityKit.V(89, 60), 5f);
            Assert.True(fresh >= 0);
            Assert.Equal(ZoneType.None, c.Zones.GetCell(fresh).Zone);
        }

        [Fact]
        public void DemolishingRoadRemovesItsCells()
        {
            CitySimulation c = CityKit.New();
            CityKit.MustRoad(c, 0, 0, 160, 0);
            c.Step();
            Assert.True(c.Zones.CellCount > 0);
            EntityId seg = CityKit.SegmentAt(c, 80, 0);
            Assert.Equal(CityResult.Ok, c.Execute(new DemolishRoadCityCommand(seg)));
            c.Step();
            Assert.Equal(0, c.Zones.CellCount);
            Assert.True(c.Undo());
            c.Step();
            Assert.True(c.Zones.CellCount > 0);
        }

        [Fact]
        public void RoadsCostMoneyAndAreRejectedWhenBroke()
        {
            CitySimulation c = CityKit.New();
            long m0 = c.Money;
            CityKit.MustRoad(c, 0, 0, 100, 0);
            Assert.Equal(m0 - 1200, c.Money); // 100 m a 12 por metro
            Assert.True(c.Undo());
            Assert.Equal(m0, c.Money);
            var poor = new CityBalance { StartMoney = 500 };
            CitySimulation p = CityKit.New(1, poor);
            Assert.Equal(CityResult.InsufficientFunds, CityKit.Road(p, 0, 0, 100, 0));
            Assert.Equal(500, p.Money);
            Assert.Equal(CityResult.OutOfBounds, CityKit.Road(c, 0, 0, 5000, 0));
        }

        [Fact]
        public void DeterministicHash()
        {
            ulong Run(bool extra)
            {
                CitySimulation c = CityKit.New(7);
                CityKit.MustRoad(c, 0, 0, 160, 0);
                CityKit.MustRoad(c, 80, -100, 80, 100);
                c.Execute(PaintZoneCommand.Brush(CityKit.V(80, 0), 50f, ZoneType.Residential, 1));
                if (extra) c.Execute(PaintZoneCommand.Brush(CityKit.V(20, 0), 10f, ZoneType.Commercial, 1));
                c.Run(200);
                return c.ComputeHash();
            }
            Assert.Equal(Run(false), Run(false));
            Assert.NotEqual(Run(false), Run(true));
        }

        [Fact]
        public void QueuedCommandsApplyAtTickStartAndReportRejections()
        {
            CitySimulation c = CityKit.New();
            c.Enqueue(new BuildRoadCityCommand(SegmentRequest.Straight(CityKit.V(0, 0), CityKit.V(100, 0), RoadTypeIds.Street2)));
            Assert.Equal(0, c.Network.SegmentCount - 1);
            c.Step();
            Assert.Equal(1, c.AppliedCommands);
            c.Enqueue(new BuildRoadCityCommand(SegmentRequest.Straight(CityKit.V(0, 0), CityKit.V(9000, 0), RoadTypeIds.Street2)));
            c.Step();
            Assert.Equal(1, c.RejectedCommands);
            Assert.Equal(CityResult.OutOfBounds, c.LastRejectReason);
        }
    }
}
