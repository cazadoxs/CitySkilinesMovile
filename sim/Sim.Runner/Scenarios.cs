using System;
using System.Collections.Generic;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Random;
using Urbe.Sim.Roads;
using Urbe.Sim.Vehicles;

namespace Urbe.Sim.Runner
{
    /// <summary>Escenario de demostracion: red + sistema de vehiculos + reposicion de poblacion.</summary>
    public sealed class Scenario
    {
        public RoadNetwork Network = null!;
        public VehicleSystem Vehicles = null!;
        public Scenarios.Population? Population;

        /// <summary>Un tick: repone vehiculos y avanza la simulacion.</summary>
        public void Step()
        {
            Population?.TopUp();
            Vehicles.Update();
        }
    }

    /// <summary>Escenarios de demostracion. El generador de viajes real (M1-09) sustituira a <see cref="Population"/>.</summary>
    public static class Scenarios
    {
        public static Scenario Create(string name, ulong seed, int vehicles)
        {
            if (name == "grid") return Grid(seed, vehicles, 8, 100f);
            throw new ArgumentException("Escenario desconocido: " + name);
        }

        /// <summary>Cuadricula de calles con una avenida cada 3 lineas en ambos ejes.</summary>
        public static Scenario Grid(ulong seed, int vehicles, int cells, float spacing)
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
            var cfg = new VehicleConfig { DriverVariation = true };
            var sys = new VehicleSystem(net, new GapOnlyGate(net), Math.Max(vehicles * 2, 256), seed, cfg);
            return new Scenario { Network = net, Vehicles = sys, Population = new Population(net, sys, vehicles, seed) };
        }

        private static void AddSeg(RoadNetwork net, EntityId a, EntityId b, ushort type)
        {
            net.AddSegmentRaw(a, b, Bezier3.FromLine(net.GetNode(a).Position, net.GetNode(b).Position), type);
        }

        /// <summary>Mantiene ~Target vehiculos con rutas de paseo aleatorio de 20 pasos (placeholder hasta M1-09).</summary>
        public sealed class Population
        {
            private readonly RoadNetwork _n;
            private readonly VehicleSystem _s;
            private Pcg32 _rng;
            private readonly LaneId[] _buf = new LaneId[64];
            private readonly LaneId[] _lanes;
            public int Target;
            public int Steps = 20;

            public Population(RoadNetwork n, VehicleSystem s, int target, ulong seed)
            {
                _n = n;
                _s = s;
                Target = target;
                _rng = new Pcg32(seed, 77);
                var list = new List<LaneId>();
                for (int i = 0; i < n.SegmentIndexBound; i++)
                {
                    EntityId seg = n.SegmentIdAt(i);
                    if (!seg.IsValid) continue;
                    for (int slot = 0; slot < n.GetLaneCount(seg); slot++) list.Add(new LaneId(i, slot));
                }
                _lanes = list.ToArray();
            }

            public void TopUp(int maxAttempts = 64)
            {
                for (int a = 0; a < maxAttempts && _s.AliveCount < Target; a++)
                {
                    LaneId start = _lanes[_rng.NextInt(_lanes.Length)];
                    int len = RandomWalk(start);
                    if (len < 2) continue;
                    byte type = _rng.NextInt(5) == 0 ? VehicleTypeId.Truck : VehicleTypeId.Car;
                    float last = _n.GetLane(_buf[len - 1]).Length;
                    _s.TrySpawn(type, new ReadOnlySpan<LaneId>(_buf, 0, len), 0f, last * 0.5f, 0f, out _);
                }
            }

            private int RandomWalk(LaneId start)
            {
                int count = 0;
                LaneId cur = start;
                _buf[count++] = cur;
                while (count < Steps)
                {
                    ReadOnlySpan<LaneConnection> conns = _n.GetConnectionsFrom(cur);
                    if (conns.Length == 0) break;
                    int pick = _rng.NextInt(conns.Length);
                    for (int k = 0; k < conns.Length && conns[pick].Turn == TurnType.UTurn && conns.Length > 1; k++)
                        pick = (pick + 1) % conns.Length;
                    cur = conns[pick].To;
                    _buf[count++] = cur;
                }
                return count;
            }
        }
    }
}
