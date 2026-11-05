using System;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Urbe.Sim.Traffic;

namespace Urbe.Game.Sim
{
    /// <summary>Red + simulación de un escenario de demostración (porte de Sim.Runner.Scenarios.Grid, sin referenciar Sim.Runner).</summary>
    public sealed class SimScenario
    {
        public RoadNetwork Network = null!;
        public TrafficSimulation Sim = null!;
    }

    /// <summary>Escenarios de demostración que arranca el bootstrap.</summary>
    public static class GridScenario
    {
        /// <summary>Cuadrícula de calles con una avenida cada 3 líneas en ambos ejes; las avenidas atraen más viajes.</summary>
        public static SimScenario Create(ulong seed = 12345UL, int maxVehicles = 300, float tripsPerSecond = 4f, int cells = 8, float spacing = 100f)
        {
            var net = new RoadNetwork(RoadTypeCatalog.CreateDefault(), new NetworkConfig());
            int m = cells + 1;
            var nodes = new EntityId[m, m];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++)
                    nodes[i, j] = net.AddNodeRaw(new Vec2(i * spacing, j * spacing), 0f);
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++)
                {
                    if (i + 1 < m) AddSeg(net, nodes[i, j], nodes[i + 1, j], j % 3 == 0 ? RoadTypeIds.Avenue4 : RoadTypeIds.Street2);
                    if (j + 1 < m) AddSeg(net, nodes[i, j], nodes[i, j + 1], i % 3 == 0 ? RoadTypeIds.Avenue4 : RoadTypeIds.Street2);
                }
            var opt = new TrafficSimulationOptions { VehicleCapacity = Math.Max(maxVehicles * 2, 256) };
            opt.Trips.TripsPerSecond = tripsPerSecond;
            opt.Trips.MaxActiveVehicles = maxVehicles;
            var sim = new TrafficSimulation(net, seed, opt);
            for (int i = 0; i < net.SegmentIndexBound; i++)
            {
                EntityId seg = net.SegmentIdAt(i);
                if (!seg.IsValid) continue;
                bool avenue = net.GetSegment(seg).RoadTypeId == RoadTypeIds.Avenue4;
                sim.Trips.AddDemandPoint(seg, 1f, avenue ? 3f : 1f);
            }
            return new SimScenario { Network = net, Sim = sim };
        }

        private static void AddSeg(RoadNetwork net, EntityId a, EntityId b, ushort type)
        {
            net.AddSegmentRaw(a, b, Bezier3.FromLine(net.GetNode(a).Position, net.GetNode(b).Position), type);
        }
    }
}
