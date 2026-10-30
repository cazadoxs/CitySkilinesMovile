using System;
using Urbe.Sim.Commands;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Urbe.Sim.Traffic;
using Urbe.Sim.Vehicles;

namespace Urbe.Sim.Tests.Golden
{
    /// <summary>
    /// Escenarios fijos de los tests de repeticion (GDD 19.6). Todo se construye con <see cref="BuildRoadCommand"/> (la ruta que usara
    /// la interfaz) y una semilla; cambiar cualquier numero de aqui cambia los hashes guardados (es un cambio intencionado).
    /// </summary>
    internal static class GoldenScenarios
    {
        public const int Ticks = 3000;
        public const int CheckpointEvery = 500;

        public static readonly string[] Names = { "calles-prioridad", "avenidas-semaforos", "manzana-gridlock" };

        public sealed class Built
        {
            public TrafficSimulation Sim = null!;
            /// <summary>Guion extra por tick (solo la manzana: provocar y soltar el bloqueo). Se llama antes de cada Step.</summary>
            public Action<int>? Script;
        }

        public static Built Create(string name, ulong seed)
        {
            switch (name)
            {
                case "calles-prioridad": return Grid(seed, 6, 100f, false, 3f, 700);
                case "avenidas-semaforos": return Grid(seed, 7, 100f, true, 4f, 1000);
                case "manzana-gridlock": return Block(seed);
                default: throw new ArgumentException(name);
            }
        }

        private static TrafficSimulation NewSim(RoadNetwork net, ulong seed, int maxVehicles, float tripsPerSecond)
        {
            var opt = new TrafficSimulationOptions { VehicleCapacity = Math.Max(maxVehicles * 2, 256) };
            opt.Trips.TripsPerSecond = tripsPerSecond;
            opt.Trips.MaxActiveVehicles = maxVehicles;
            return new TrafficSimulation(net, seed, opt);
        }

        private static void Enqueue(TrafficSimulation sim, Vec2 a, Vec2 b, ushort type)
            => sim.Commands.Enqueue(new BuildRoadCommand(SegmentRequest.Straight(a, b, type)));

        /// <summary>Cuadricula de <paramref name="cells"/> x <paramref name="cells"/> celdas; con avenidas, una cada 3 lineas en ambos ejes.</summary>
        private static Built Grid(ulong seed, int cells, float spacing, bool avenues, float tripsPerSecond, int maxVehicles)
        {
            var net = new RoadNetwork(RoadTypeCatalog.CreateDefault(), new NetworkConfig());
            TrafficSimulation sim = NewSim(net, seed, maxVehicles, tripsPerSecond);
            int m = cells + 1;
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++)
                {
                    var p = new Vec2(i * spacing, j * spacing);
                    if (i + 1 < m) Enqueue(sim, p, new Vec2((i + 1) * spacing, j * spacing), avenues && j % 3 == 0 ? RoadTypeIds.Avenue4 : RoadTypeIds.Street2);
                    if (j + 1 < m) Enqueue(sim, p, new Vec2(i * spacing, (j + 1) * spacing), avenues && i % 3 == 0 ? RoadTypeIds.Avenue4 : RoadTypeIds.Street2);
                }
            sim.Commands.Drain(net, sim.History);
            AddDemand(sim, net);
            return new Built { Sim = sim };
        }

        private static void AddDemand(TrafficSimulation sim, RoadNetwork net)
        {
            for (int i = 0; i < net.SegmentIndexBound; i++)
            {
                EntityId seg = net.SegmentIdAt(i);
                if (!seg.IsValid) continue;
                bool avenue = net.GetSegment(seg).RoadTypeId == RoadTypeIds.Avenue4;
                sim.Trips.AddDemandPoint(seg, 1f, avenue ? 3f : 1f);
            }
        }

        private static EntityId FindSegment(RoadNetwork net, Vec2 a, Vec2 b)
        {
            for (int i = 0; i < net.SegmentIndexBound; i++)
            {
                EntityId s = net.SegmentIdAt(i);
                if (!s.IsValid) continue;
                ref readonly Segment seg = ref net.GetSegment(s);
                if (net.GetNode(seg.NodeA).Position.Equals(a) && net.GetNode(seg.NodeB).Position.Equals(b)) return s;
            }
            throw new InvalidOperationException("segmento no encontrado");
        }

        /// <summary>
        /// Manzana de 100 m con 4 cruces en T (un ramal de 100 m por esquina). Guion: se congelan los primeros de cada tramo y se
        /// llenan los 4 tramos del anillo; en el tick 400 se sueltan y el anillo queda en espera mutua hasta que actua el anti-bloqueo.
        /// </summary>
        private static Built Block(ulong seed)
        {
            var net = new RoadNetwork(RoadTypeCatalog.CreateDefault(), new NetworkConfig());
            TrafficSimulation sim = NewSim(net, seed, 120, 0f);
            const float h = 50f;
            var pts = new[] { new Vec2(-h, h), new Vec2(h, h), new Vec2(h, -h), new Vec2(-h, -h) };
            var far = new Vec2[4];
            for (int k = 0; k < 4; k++) Enqueue(sim, pts[k], pts[(k + 1) % 4], RoadTypeIds.Street2);
            for (int k = 0; k < 4; k++)
            {
                far[k] = new Vec2(pts[k].X + (pts[k].X > 0 ? 100f : -100f), pts[k].Z);
                Enqueue(sim, far[k], pts[k], RoadTypeIds.Street2);
            }
            sim.Commands.Drain(net, sim.History);
            var ringLane = new LaneId[4];
            var stubOut = new LaneId[4];
            for (int k = 0; k < 4; k++)
            {
                EntityId r = FindSegment(net, pts[k], pts[(k + 1) % 4]);
                ringLane[k] = new LaneId(r.Index, 0);
                EntityId s = FindSegment(net, far[k], pts[k]);
                stubOut[k] = new LaneId(s.Index, net.GetSegment(s).LanesForward);
            }
            var heads = new EntityId[4];
            var route = new LaneId[8];
            var rng = new Urbe.Sim.Random.Pcg32(seed, 0x474C4B);
            bool Spawn(int k, int steps, float s, bool frozen, out EntityId id)
            {
                int n = 0;
                for (int j = 0; j <= steps; j++) route[n++] = ringLane[(k + j) % 4];
                route[n++] = stubOut[(k + steps + 1) % 4];
                float target = net.GetLane(route[n - 1]).Length * 0.5f;
                if (!sim.Vehicles.TrySpawn(VehicleTypeId.Car, new ReadOnlySpan<LaneId>(route, 0, n), s, target, 0f, out id)) return false;
                if (frozen) sim.Vehicles.SetFrozen(id, true);
                return true;
            }
            Action<int> script = t =>
            {
                if (t == 0)
                {
                    for (int k = 0; k < 4; k++)
                        Spawn(k, 1 + rng.NextInt(3), net.GetLane(ringLane[k]).Length - 1f, true, out heads[k]);
                }
                else if (t < 400 && t % 5 == 0)
                {
                    for (int k = 0; k < 4; k++) Spawn(k, rng.NextInt(0, 3), 4.5f, false, out _);
                }
                else if (t == 400)
                {
                    for (int k = 0; k < 4; k++) sim.Vehicles.SetFrozen(heads[k], false);
                }
            };
            return new Built { Sim = sim, Script = script };
        }
    }
}
