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

        /// <summary>Peticion de via de alivio (escenario bottleneck): se encola a mitad de la simulacion.</summary>
        public Urbe.Sim.Roads.SegmentRequest? Relief;

        public void Step() => Sim.Step();
    }

    /// <summary>Escenarios de demostracion.</summary>
    public static class Scenarios
    {
        public static Scenario Create(string name, ulong seed, int maxVehicles, float tripsPerSecond)
        {
            if (name == "grid") return Grid(seed, maxVehicles, tripsPerSecond, 8, 100f);
            if (name == "bottleneck") return Bottleneck(seed, maxVehicles, tripsPerSecond);
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

        public static float TrunkWeight = 4f;
        public static float SideWeight = 0.25f;

        /// <summary>
        /// Demo M1-14: barrio residencial (izquierda, 12x8 celdas de 80 m) y zona de empleo (derecha, 8x8) unidos por UN puente de
        /// calle estrecha (street_2, 190 m). El barrio tiene calles de un solo sentido hacia el este y dos avenidas troncales
        /// (filas 3 y 5) que acaban en una "cabeza" sin cruces laterales: la fila 3 sigue por el puente. Toda la demanda va de izquierda
        /// a derecha y sale sobre todo de las troncales, asi el cuello de botella es el puente y no los cruces interiores.
        /// <see cref="Scenario.Relief"/> es la avenida 2+2 que se abrira despues entre la cabeza de la fila 5 y la zona de empleo.
        /// </summary>
        public static Scenario Bottleneck(ulong seed, int maxVehicles, float tripsPerSecond)
        {
            var net = new RoadNetwork(RoadTypeCatalog.CreateDefault(), new NetworkConfig());
            const int lc = 12, rc = 8, rows = 8;
            const float sp = 80f, gap = 190f;
            const int mid = 3, alt = 5; // filas troncales: puente (mid) y avenida nueva (alt)
            float rightX = lc * sp + gap;
            var left = new EntityId[lc + 1, rows + 1];
            var right = new EntityId[rc + 1, rows + 1];
            for (int j = 0; j <= rows; j++)
            {
                for (int i = 0; i <= lc; i++)
                    if (i < lc || j == mid || j == alt) left[i, j] = net.AddNodeRaw(new Vec2(i * sp, j * sp), 0f);
                for (int i = 0; i <= rc; i++) right[i, j] = net.AddNodeRaw(new Vec2(rightX + i * sp, j * sp), 0f);
            }
            for (int j = 0; j <= rows; j++)
            {
                bool spine = j == mid || j == alt;
                for (int i = 0; i < lc; i++)
                {
                    if (i + 1 < lc || spine) AddSeg(net, left[i, j], left[i + 1, j], spine ? RoadTypeIds.Avenue4 : RoadTypeIds.OneWay2);
                    if (j < rows && i < lc) AddSeg(net, left[i, j], left[i, j + 1], RoadTypeIds.Street2);
                }
                if (j < rows) AddSeg(net, left[lc - 1, j], left[lc - 1, j + 1], RoadTypeIds.Street2);
                for (int i = 0; i < rc; i++)
                    AddSeg(net, right[i, j], right[i + 1, j], spine ? RoadTypeIds.Avenue4 : RoadTypeIds.Street2);
                if (j < rows) for (int i = 0; i <= rc; i++) AddSeg(net, right[i, j], right[i, j + 1], RoadTypeIds.Street2);
            }
            EntityId bridge = net.AddSegmentRaw(left[lc, mid], right[0, mid],
                Bezier3.FromLine(net.GetNode(left[lc, mid]).Position, net.GetNode(right[0, mid]).Position), RoadTypeIds.Street2);

            var opt = new TrafficSimulationOptions { VehicleCapacity = Math.Max(maxVehicles * 2, 256) };
            opt.Trips.TripsPerSecond = tripsPerSecond;
            opt.Trips.MaxActiveVehicles = maxVehicles;
            opt.Trips.MaxPending = 512;
            opt.PathsPerTick = 32;
            var sim = new TrafficSimulation(net, seed, opt);
            for (int i = 0; i < net.SegmentIndexBound; i++)
            {
                EntityId seg = net.SegmentIdAt(i);
                if (!seg.IsValid || seg == bridge) continue;
                Vec2 c = net.GetSegment(seg).Curve.Evaluate(0.5f);
                if (c.X > rightX) sim.Trips.AddDemandPoint(seg, 0f, 1f); // zona de empleo: atrae
                else
                {
                    bool trunk = Math.Abs(c.Z - mid * sp) < 1f || Math.Abs(c.Z - alt * sp) < 1f;
                    sim.Trips.AddDemandPoint(seg, trunk ? TrunkWeight : SideWeight, 0f); // barrio: genera
                }
            }
            var sc = new Scenario { Network = net, Sim = sim };
            sc.Relief = SegmentRequest.Straight(net.GetNode(left[lc, alt]).Position, net.GetNode(right[0, alt]).Position, RoadTypeIds.Avenue4);
            return sc;
        }

        private static void AddSeg(RoadNetwork net, EntityId a, EntityId b, ushort type)
        {
            net.AddSegmentRaw(a, b, Bezier3.FromLine(net.GetNode(a).Position, net.GetNode(b).Position), type);
        }
    }
}
