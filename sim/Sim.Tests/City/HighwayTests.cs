using System;
using Urbe.Sim.City;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Xunit;

namespace Urbe.Sim.Tests.City
{
    /// <summary>M3-11 punto 1: la partida empieza solo con una autovia que cruza el mapa de borde a borde.</summary>
    public class HighwayTests
    {
        [Fact]
        public void StartsWithOnlyAHighwayCrossingTheMap()
        {
            var c = new CitySimulation(1);
            int segs = 0;
            float minX = float.MaxValue, maxX = float.MinValue;
            for (int i = 0; i < c.Network.SegmentIndexBound; i++)
            {
                EntityId id = c.Network.SegmentIdAt(i);
                if (!id.IsValid) continue;
                segs++;
                ref readonly Segment sg = ref c.Network.GetSegment(id);
                Assert.Equal(RoadTypeIds.Highway4, sg.RoadTypeId);
                minX = Math.Min(minX, c.Network.GetNode(sg.NodeA).Position.X);
                maxX = Math.Max(maxX, c.Network.GetNode(sg.NodeB).Position.X);
            }
            Assert.Equal(12, segs);
            Assert.True(minX < -c.Balance.MapHalfSize + 20f && maxX > c.Balance.MapHalfSize - 20f, "cruza de borde a borde");
            Assert.Equal(0, c.Buildings.Count);
            Assert.Equal(0, c.Stats.Population);
            Assert.True(c.OutsideSegmentFor(0).IsValid && c.OutsideSegmentFor(1).IsValid && c.OutsideSegmentFor(0) != c.OutsideSegmentFor(1));
        }

        [Fact]
        public void HighwayHasALightCurve()
        {
            var c = new CitySimulation(1);
            float maxZ = float.MinValue, minZ = float.MaxValue;
            for (int i = 0; i < c.Network.NodeIndexBound; i++)
            {
                EntityId id = c.Network.NodeIdAt(i);
                if (!id.IsValid) continue;
                float z = c.Network.GetNode(id).Position.Z;
                maxZ = Math.Max(maxZ, z); minZ = Math.Min(minZ, z);
            }
            Assert.InRange(maxZ - minZ, 60f, 140f);
        }

        [Fact]
        public void StreetCanHookIntoTheMiddleOfTheHighwayAndSplitsIt()
        {
            var c = new CitySimulation(1);
            Vec2 p = c.HighwayAnchor;
            int segsBefore = c.Network.SegmentCount;
            var req = SegmentRequest.Straight(p, new Vec2(p.X, p.Z + 120f), RoadTypeIds.Street2);
            Assert.Equal(CityResult.Ok, c.Execute(new BuildRoadCityCommand(req)));
            Assert.Equal(segsBefore + 1, c.Network.SegmentCount);
            // Tambien en mitad de un tramo (no en un nodo existente): parte el tramo en dos y crea un cruce.
            float x = p.X + 80f;
            var req2 = SegmentRequest.Straight(new Vec2(x, c.HighwayY(x)), new Vec2(x, c.HighwayY(x) - 100f), RoadTypeIds.Street2);
            Assert.Equal(CityResult.Ok, c.Execute(new BuildRoadCityCommand(req2)));
            Assert.Equal(segsBefore + 3, c.Network.SegmentCount);
        }

        [Fact]
        public void HighwayMarginsCannotBeZonedAndEndsCannotBeDemolished()
        {
            var c = new CitySimulation(1);
            Vec2 p = c.HighwayAnchor;
            var req = SegmentRequest.Straight(p, new Vec2(p.X, p.Z + 120f), RoadTypeIds.Street2);
            Assert.Equal(CityResult.Ok, c.Execute(new BuildRoadCityCommand(req)));
            c.Step();
            Assert.Equal(CityResult.Ok, c.Execute(PaintZoneCommand.Brush(new Vec2(p.X + 60f, p.Z - 10f), 40f, ZoneType.Residential, 1)));
            for (int i = 0; i < c.Zones.CellIndexBound; i++)
            {
                if (!c.Zones.IsCellAlive(i)) continue;
                ref readonly ZoneCell cell = ref c.Zones.GetCell(i);
                Assert.True(c.Network.Types.Get(c.Network.GetSegment(cell.Segment).RoadTypeId).Zonable, "hay celdas junto a la autovia");
            }
            // Los dos tramos de los extremos estan protegidos; uno del medio, no.
            EntityId west = c.OutsideSegmentFor(0), east = c.OutsideSegmentFor(1);
            Assert.True(c.IsOutsideConnection(west) && c.IsOutsideConnection(east));
            Assert.Equal(CityResult.Protected, c.Execute(new DemolishRoadCityCommand(west)));
            Assert.Equal(CityResult.Protected, c.Execute(new DemolishRoadCityCommand(east)));
            Assert.True(c.Network.IsSegmentAlive(west) && c.Network.IsSegmentAlive(east));
        }

        [Fact]
        public void EveryRoadTypeCarriesVehiclesAndCostsItsPricePerMeter()
        {
            for (ushort t = 0; t < 6; t++)
            {
                var c = new CitySimulation(3, new CityBalance { StartMoney = 1000000 });
                Vec2 p = c.HighwayAnchor;
                // Una calle de 160 m de este tipo que sale de la autovia (dos tramos), y un coche (via de sentido unico incluida) por ella.
                var a = SegmentRequest.Straight(p, new Vec2(p.X, p.Z + 80f), t);
                var b = SegmentRequest.Straight(new Vec2(p.X, p.Z + 80f), new Vec2(p.X, p.Z + 160f), t);
                long m0 = c.Money;
                Assert.Equal(CityResult.Ok, c.Execute(new BuildRoadCityCommand(a)));
                Assert.Equal(CityResult.Ok, c.Execute(new BuildRoadCityCommand(b)));
                Assert.InRange(m0 - c.Money, 2L * c.Balance.RoadCost(t, 80f) - 2, 2L * c.Balance.RoadCost(t, 80f) + 4 * c.Balance.RoadCostPerMeter[t]);
                c.Step();
                EntityId far = c.Network.NearestSegment(new Vec2(p.X, p.Z + 120f), 4f, out EntityId fs, out _, out _) ? fs : EntityId.None;
                EntityId near = c.OutsideSegmentFor(0);
                Assert.True(c.Traffic.Trips.RequestTrip(near, far, Urbe.Sim.Vehicles.VehicleTypeId.Car, 0), "viaje en tipo " + t);
                c.Run(1500);
                Assert.True(c.Traffic.Trips.Completed > 0 || c.Traffic.Vehicles.AliveCount > 0, "el coche circula en el tipo " + t);
            }
        }

        [Fact]
        public void EveryRoadTypeIsUnlockedFromTheStart()
        {
            var c = new CitySimulation(1);
            for (ushort t = 0; t < c.Network.Types.Count; t++) Assert.True(c.IsRoadUnlocked(t), "tipo " + t);
        }
    }
}
