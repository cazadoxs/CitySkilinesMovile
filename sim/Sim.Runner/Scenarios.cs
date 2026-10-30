using System;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Traffic;
using Urbe.Sim.Roads;
using Urbe.Sim.Vehicles;

namespace Urbe.Sim.Runner
{
    /// <summary>Escenario de demostracion: una red y su <see cref="TrafficSimulation"/> (generador de viajes + metricas).</summary>
    public sealed class Scenario
    {
        public RoadNetwork Network = null!;
        public TrafficSimulation Sim = null!;
        public VehicleSystem Vehicles => Sim.Vehicles;

        public void Step() => Sim.Step();
    }

    /// <summary>Escenarios de demostracion.</summary>
    public static class Scenarios
    {
        public static Scenario Create(string name, ulong seed, int maxVehicles, float tripsPerSecond)
        {
            if (name == "grid") return Grid(seed, maxVehicles, tripsPerSecond, 8, 100f);
            throw new ArgumentException("Escenario desconocido: " + name);
        }

        /// <summary>Cuadricula de calles con una avenida cada 3 lineas en ambos ejes; las avenidas atraen mas viajes.</summary>
        public static Scenario Grid(ulong seed, int maxVehicles, float tripsPerSecond, int cells, float spacing, int pathsPerTick = 0)
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
            if (pathsPerTick > 0) opt.PathsPerTick = pathsPerTick;
            var sim = new TrafficSimulation(net, seed, opt);
            for (int i = 0; i < net.SegmentIndexBound; i++)
            {
                EntityId seg = net.SegmentIdAt(i);
                if (!seg.IsValid) continue;
                bool avenue = net.GetSegment(seg).RoadTypeId == RoadTypeIds.Avenue4;
                sim.Trips.AddDemandPoint(seg, 1f, avenue ? 3f : 1f);
            }
            return new Scenario { Network = net, Sim = sim };
        }

        private static void AddSeg(RoadNetwork net, EntityId a, EntityId b, ushort type)
        {
            net.AddSegmentRaw(a, b, Bezier3.FromLine(net.GetNode(a).Position, net.GetNode(b).Position), type);
        }
    }
}
