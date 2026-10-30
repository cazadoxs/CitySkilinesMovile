using System;
using System.Collections.Generic;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Random;
using Urbe.Sim.Roads;
using Urbe.Sim.Tests.Roads;
using Urbe.Sim.Vehicles;
using Xunit;

namespace Urbe.Sim.Tests.Vehicles
{
    /// <summary>Puerta de pruebas: delega en una GapOnlyGate y cuenta eventos, o deniega siempre.</summary>
    internal sealed class RecordingGate : IJunctionGate, IVehicleViewConsumer
    {
        public readonly GapOnlyGate Inner;
        public bool DenyAll;
        public int Entered, Exited, Removed, NetworkChanges, Requests, Cancelled;

        public RecordingGate(RoadNetwork net) { Inner = new GapOnlyGate(net); }

        public void Bind(IVehicleView view, int vehicleCapacity) => Inner.Bind(view, vehicleCapacity);
        public void OnNetworkChanged(ReadOnlySpan<EntityId> changedNodes) { NetworkChanges++; Inner.OnNetworkChanged(changedNodes); }
        public void BeginTick(ulong tick) => Inner.BeginTick(tick);
        public void RequestEntry(in EntryRequest req) { Requests++; Inner.RequestEntry(in req); }

        public ReadOnlySpan<EntryGrant> ResolveRequests()
        {
            ReadOnlySpan<EntryGrant> g = Inner.ResolveRequests();
            return DenyAll ? ReadOnlySpan<EntryGrant>.Empty : g;
        }

        public void OnEntered(int connection, int vehicle) { Entered++; Inner.OnEntered(connection, vehicle); }
        public void OnExited(int connection, int vehicle) { Exited++; Inner.OnExited(connection, vehicle); }
        public void OnVehicleRemoved(int vehicle) { Removed++; Inner.OnVehicleRemoved(vehicle); }
        public void OnPermitCancelled(int vehicle) { Cancelled++; Inner.OnPermitCancelled(vehicle); }
    }

    internal static class VK
    {
        public static VehicleSystem Sys(RoadNetwork n, int capacity = 512, ulong seed = 1, bool variation = false, IJunctionGate? gate = null)
        {
            var cfg = new VehicleConfig { DriverVariation = variation };
            return new VehicleSystem(n, gate ?? new GapOnlyGate(n), capacity, seed, cfg);
        }

        public static LaneId Lane(RoadNetwork n, EntityId seg, bool forward, int idx = 0)
        {
            return forward ? new LaneId(seg.Index, idx) : new LaneId(seg.Index, n.GetSegment(seg).LanesForward + idx);
        }

        public static void Run(VehicleSystem s, int ticks)
        {
            for (int i = 0; i < ticks; i++) s.Update();
        }

        public static EntityId Spawn(VehicleSystem s, byte type, LaneId[] lanes, float startS, float targetS, float speed = 0f)
        {
            Assert.True(s.TrySpawn(type, lanes, startS, targetS, speed, out EntityId id), "no se pudo crear el vehiculo");
            return id;
        }

        /// <summary>Busqueda en anchura sobre carriles y conexiones hasta el primer carril de <paramref name="target"/>.</summary>
        public static LaneId[]? Route(RoadNetwork n, LaneId start, EntityId target)
        {
            var prev = new Dictionary<int, int>();
            var queue = new Queue<int>();
            prev[start.Value] = -1;
            queue.Enqueue(start.Value);
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                if (new LaneId(cur).SegmentIndex == target.Index)
                {
                    var path = new List<LaneId>();
                    for (int c = cur; c >= 0; c = prev[c]) path.Add(new LaneId(c));
                    path.Reverse();
                    return path.ToArray();
                }
                foreach (LaneConnection c in n.GetConnectionsFrom(new LaneId(cur)).ToArray())
                {
                    if (prev.ContainsKey(c.To.Value)) continue;
                    prev[c.To.Value] = cur;
                    queue.Enqueue(c.To.Value);
                }
            }
            return null;
        }

        /// <summary>Invariantes de listas (test 5 del ADR) y valores finitos (test 4).</summary>
        public static void CheckInvariants(VehicleSystem s)
        {
            RoadNetwork n = s.Network;
            var seen = new bool[s.HighWater];
            int visited = 0;
            for (int lane = 0; lane < n.SegmentIndexBound * RoadNetwork.MaxLanesPerSegment; lane++)
                visited += CheckList(s, TrackKind.Lane, lane, s.LaneHead(new LaneId(lane)), s.LaneTail(new LaneId(lane)), seen);
            for (int c = 0; c < n.ConnectionStorageUsed; c++)
                visited += CheckList(s, TrackKind.Connection, c, s.ConnectionHead(c), s.ConnectionTail(c), seen);
            Assert.Equal(s.AliveCount, visited);
            for (int i = 0; i < s.HighWater; i++)
            {
                ref readonly Vehicle v = ref s.GetVehicle(i);
                if (!v.IsAlive) continue;
                Assert.True(seen[i], "vehiculo vivo fuera de toda lista");
                Assert.True(v.V >= 0f && !float.IsNaN(v.V) && !float.IsInfinity(v.V), "V invalida " + v.V);
                Assert.True(!float.IsNaN(v.S) && !float.IsInfinity(v.S) && v.S >= 0f, "S invalida " + v.S);
                Assert.True(!float.IsNaN(v.A) && !float.IsInfinity(v.A), "A invalida");
            }
        }

        private static int CheckList(VehicleSystem s, TrackKind kind, int track, int head, int tail, bool[] seen)
        {
            int count = 0;
            int prevIdx = -1;
            for (int i = head; i >= 0; i = s.GetVehicle(i).Behind)
            {
                ref readonly Vehicle v = ref s.GetVehicle(i);
                Assert.True(v.IsAlive);
                Assert.False(seen[i], "vehiculo en dos listas");
                seen[i] = true;
                Assert.Equal(kind, v.Kind);
                Assert.Equal(track, v.Track);
                Assert.Equal(prevIdx, v.Ahead);
                if (prevIdx >= 0) Assert.True(s.GetVehicle(prevIdx).S >= v.S, "lista desordenada");
                prevIdx = i;
                count++;
                Assert.True(count <= s.AliveCount, "ciclo en la lista");
            }
            Assert.Equal(tail, prevIdx);
            return count;
        }

        // ------------------------------------------------------------------ redes

        /// <summary>Cadena recta de <paramref name="count"/> segmentos de <paramref name="segLen"/> metros a lo largo de +X.</summary>
        public static EntityId[] Chain(RoadNetwork n, int count, float segLen, ushort type)
        {
            var segs = new EntityId[count];
            for (int i = 0; i < count; i++) segs[i] = Kit.SegAt(n, i * segLen, 0, (i + 1) * segLen, 0, type);
            return segs;
        }

        public sealed class TJunction
        {
            public RoadNetwork Net = null!;
            public EntityId Stem, West, East;
            public EntityId Node;
        }

        /// <summary>Calle principal O-E (segmentos A y B) y tallo S hacia el nodo central (0,0). Brazos de 100 m.</summary>
        public static TJunction MakeT(ushort stemType = RoadTypeIds.Street2, ushort mainType = RoadTypeIds.Street2, float arm = 100f)
        {
            var t = new TJunction { Net = Kit.New() };
            RoadNetwork n = t.Net;
            t.West = Kit.SegAt(n, -arm, 0, 0, 0, mainType);
            t.East = Kit.SegAt(n, 0, 0, arm, 0, mainType);
            t.Stem = Kit.SegAt(n, 0, -arm, 0, 0, stemType);
            t.Node = Kit.At(n, 0, 0);
            return t;
        }

        /// <summary>Anillo cuadrado antihorario de 8 segmentos oneway_2 (2 carriles), perimetro de 600 m por carril.</summary>
        public static EntityId[] MakeRing(RoadNetwork n, float side = 150f)
        {
            float h = side / 2f;
            var pts = new[]
            {
                (0f, 0f), (h, 0f), (side, 0f), (side, h), (side, side), (h, side), (0f, side), (0f, h),
            };
            var segs = new EntityId[pts.Length];
            var nodes = new EntityId[pts.Length];
            for (int i = 0; i < pts.Length; i++) nodes[i] = Kit.Node(n, pts[i].Item1, pts[i].Item2);
            for (int i = 0; i < pts.Length; i++) segs[i] = Kit.Seg(n, nodes[i], nodes[(i + 1) % pts.Length], RoadTypeIds.OneWay2);
            return segs;
        }

        public static int ConnIndex(RoadNetwork n, LaneId from, LaneId to)
        {
            var span = n.GetConnectionsFrom(from);
            for (int i = 0; i < span.Length; i++)
                if (span[i].To == to) return n.GetLane(from).ConnFirst + i;
            return -1;
        }

        /// <summary>Cuadricula de <paramref name="cells"/> x <paramref name="cells"/> manzanas con calles street_2 (o la que se indique).</summary>
        public static RoadNetwork MakeGrid(int cells, float spacing, ushort type = RoadTypeIds.Street2)
        {
            RoadNetwork n = Kit.New();
            int m = cells + 1;
            var nodes = new EntityId[m, m];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++)
                    nodes[i, j] = Kit.Node(n, i * spacing, j * spacing);
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++)
                {
                    if (i + 1 < m) Kit.Seg(n, nodes[i, j], nodes[i + 1, j], type);
                    if (j + 1 < m) Kit.Seg(n, nodes[i, j], nodes[i, j + 1], type);
                }
            return n;
        }

        /// <summary>Ruta aleatoria sobre la red: sigue conexiones al azar (sin cambio de sentido si hay otra salida).</summary>
        public static int RandomWalk(RoadNetwork n, ref Pcg32 rng, LaneId start, int steps, LaneId[] outLanes)
        {
            int count = 0;
            LaneId cur = start;
            outLanes[count++] = cur;
            while (count < steps)
            {
                var conns = n.GetConnectionsFrom(cur);
                if (conns.Length == 0) break;
                int pick = rng.NextInt(conns.Length);
                // Evita el cambio de sentido si hay alternativa.
                for (int k = 0; k < conns.Length && conns[pick].Turn == TurnType.UTurn && conns.Length > 1; k++)
                    pick = (pick + 1) % conns.Length;
                cur = conns[pick].To;
                outLanes[count++] = cur;
            }
            return count;
        }

        /// <summary>Mantiene ~<c>target</c> vehiculos en una red con rutas aleatorias de 20 pasos.</summary>
        public sealed class Population
        {
            private readonly RoadNetwork _n;
            private readonly VehicleSystem _s;
            private Pcg32 _rng;
            private readonly LaneId[] _buf = new LaneId[64];
            private readonly int _laneCount;
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
                _laneCount = _lanes.Length;
            }

            /// <summary>Intenta repoblar hasta el objetivo con un maximo de intentos por llamada.</summary>
            public void TopUp(int maxAttempts = 64)
            {
                for (int a = 0; a < maxAttempts && _s.AliveCount < Target; a++)
                {
                    LaneId start = _lanes[_rng.NextInt(_laneCount)];
                    int len = RandomWalk(_n, ref _rng, start, Steps, _buf);
                    if (len < 2) continue;
                    byte type = _rng.NextInt(5) == 0 ? VehicleTypeId.Truck : VehicleTypeId.Car;
                    float last = _n.GetLane(_buf[len - 1]).Length;
                    _s.TrySpawn(type, new ReadOnlySpan<LaneId>(_buf, 0, len), 0f, last * 0.5f, 0f, out _);
                }
            }
        }
    }
}
