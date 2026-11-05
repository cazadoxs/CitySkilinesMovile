using System;
using UnityEngine;
using Urbe.Game.Sim;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Urbe.Sim.Traffic;

namespace Urbe.Game.Tests
{
    /// <summary>
    /// Red de muestra para las capturas (M2-04b, M2-09): una avenida de 4 carriles con un cruce en X (calle), un cruce en T, un cruce en curva
    /// (dos calles curvas que parten de la avenida) y un cruce en Y al final. Cabe en 360 x 170 m.
    /// </summary>
    public static class ShowcaseScenario
    {
        // Puntos de interés (x, z) para colocar la cámara.
        public static readonly Vector2 CrossX = new Vector2(70f, 50f);
        public static readonly Vector2 CrossT = new Vector2(150f, 50f);
        public static readonly Vector2 CrossCurves = new Vector2(230f, 50f);
        public static readonly Vector2 CrossY = new Vector2(300f, 50f);

        public sealed class Result
        {
            public SimScenario Scenario = null!;
            public EntityId AvenueSegmentA;   // primer tramo de la avenida (para forzar atascos en las pruebas)
            public EntityId[] Avenue = Array.Empty<EntityId>();
        }

        public static Result Create(ulong seed = 7UL, int maxVehicles = 120, float tripsPerSecond = 2.5f)
        {
            var net = new RoadNetwork(RoadTypeCatalog.CreateDefault(), new NetworkConfig());
            EntityId N(float x, float z) => net.AddNodeRaw(new Vec2(x, z), 0f);
            EntityId S(EntityId a, EntityId b, ushort type) =>
                net.AddSegmentRaw(a, b, Bezier3.FromLine(net.GetNode(a).Position, net.GetNode(b).Position), type);

            EntityId a0 = N(0, 50), a1 = N(70, 50), a2 = N(150, 50), a3 = N(230, 50), a4 = N(300, 50);
            var avenue = new[]
            {
                S(a0, a1, RoadTypeIds.Avenue4), S(a1, a2, RoadTypeIds.Avenue4), S(a2, a3, RoadTypeIds.Avenue4), S(a3, a4, RoadTypeIds.Avenue4),
            };
            // X con una calle que cruza la avenida en (70, 50), con final de calle en ambos extremos.
            S(N(70, -40), a1, RoadTypeIds.Street2);
            S(a1, N(70, 140), RoadTypeIds.Street2);
            // T: una calle sale hacia el norte en (150, 50).
            S(a2, N(150, 130), RoadTypeIds.Street2);
            // Cruce con dos calles curvas (cuartos de círculo) que salen de (230, 50) hacia el sur y el norte.
            EntityId cs = N(300, -30), cn = N(300, 130);
            net.AddSegmentRaw(a3, cs, new Bezier3(new Vec2(230, 50), new Vec2(230, 10), new Vec2(262, -30), new Vec2(300, -30)), RoadTypeIds.Street2);
            net.AddSegmentRaw(a3, cn, new Bezier3(new Vec2(230, 50), new Vec2(230, 90), new Vec2(262, 130), new Vec2(300, 130)), RoadTypeIds.Street2);
            // Y: la avenida se abre en dos calles hacia el este.
            S(a4, N(370, 95), RoadTypeIds.Street2);
            S(a4, N(370, 5), RoadTypeIds.Street2);

            var opt = new TrafficSimulationOptions { VehicleCapacity = Math.Max(maxVehicles * 2, 256) };
            opt.Trips.TripsPerSecond = tripsPerSecond;
            opt.Trips.MaxActiveVehicles = maxVehicles;
            var sim = new TrafficSimulation(net, seed, opt);
            for (int i = 0; i < net.SegmentIndexBound; i++)
            {
                EntityId seg = net.SegmentIdAt(i);
                if (!seg.IsValid) continue;
                bool av = net.GetSegment(seg).RoadTypeId == RoadTypeIds.Avenue4;
                sim.Trips.AddDemandPoint(seg, 1f, av ? 3f : 1f);
            }
            return new Result { Scenario = new SimScenario { Network = net, Sim = sim }, AvenueSegmentA = avenue[0], Avenue = avenue };
        }
    }
}
