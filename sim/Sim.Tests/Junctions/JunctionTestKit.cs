using System;
using System.Collections.Generic;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Junctions;
using Urbe.Sim.Random;
using Urbe.Sim.Roads;
using Urbe.Sim.Tests.Roads;
using Urbe.Sim.Vehicles;
using Xunit;

namespace Urbe.Sim.Tests.Junctions
{
    /// <summary>Cruce de pruebas: brazos creados desde el extremo exterior hacia el centro (carriles Forward = de entrada).</summary>
    internal sealed class Crossing
    {
        public RoadNetwork Net = null!;
        public EntityId Center;
        /// <summary>Brazos por direccion: 0 = +X (este), 1 = +Z (norte), 2 = -X (oeste), 3 = -Z (sur); None si no existe.</summary>
        public EntityId[] Arms = new EntityId[4];

        public LaneId Entry(int arm, int idx = 0) => new LaneId(Arms[arm].Index, idx);

        public LaneId Exit(int arm, int idx = 0) => new LaneId(Arms[arm].Index, Net.GetSegment(Arms[arm]).LanesForward + idx);

        public IEnumerable<LaneId> EntryLanes()
        {
            for (int a = 0; a < 4; a++)
            {
                if (!Arms[a].IsValid) continue;
                for (int k = 0; k < Net.GetSegment(Arms[a]).LanesForward; k++) yield return Entry(a, k);
            }
        }

        /// <summary>Indice absoluto de la conexion from -> to (o -1).</summary>
        public int Conn(LaneId from, LaneId to)
        {
            var span = Net.GetConnectionsFrom(from);
            for (int i = 0; i < span.Length; i++)
                if (span[i].To == to) return Net.GetLane(from).ConnFirst + i;
            return -1;
        }

        /// <summary>Conexion desde el brazo <paramref name="from"/> con el giro dado (la primera si hay varias).</summary>
        public int ConnTurn(int from, TurnType turn, int lane = -1)
        {
            int lanes = Net.GetSegment(Arms[from]).LanesForward;
            for (int k = 0; k < lanes; k++)
            {
                if (lane >= 0 && k != lane) continue;
                LaneId l = Entry(from, k);
                var span = Net.GetConnectionsFrom(l);
                for (int i = 0; i < span.Length; i++)
                    if (span[i].Turn == turn) return Net.GetLane(l).ConnFirst + i;
            }
            return -1;
        }

        public int ArmOfSegment(int segIndex)
        {
            for (int a = 0; a < 4; a++) if (Arms[a].IsValid && Arms[a].Index == segIndex) return a;
            return -1;
        }
    }

    internal static class JK
    {
        private static readonly (float, float)[] Dirs = { (1f, 0f), (0f, 1f), (-1f, 0f), (0f, -1f) };

        /// <summary>Cruce con brazos en las direcciones indicadas por <paramref name="types"/> (null = sin brazo).</summary>
        public static Crossing Make(ushort?[] types, float arm = 150f)
        {
            var x = new Crossing { Net = Kit.New() };
            RoadNetwork n = x.Net;
            x.Center = n.AddNodeRaw(Kit.V(0, 0), 0f);
            for (int a = 0; a < 4; a++)
            {
                x.Arms[a] = EntityId.None;
                if (types[a] == null) continue;
                EntityId outer = n.AddNodeRaw(Kit.V(Dirs[a].Item1 * arm, Dirs[a].Item2 * arm), 0f);
                x.Arms[a] = n.AddSegmentRaw(outer, x.Center, Bezier3.FromLine(n.GetNode(outer).Position, Kit.V(0, 0)), types[a]!.Value);
                Assert.True(x.Arms[a].IsValid);
            }
            return x;
        }

        /// <summary>X con via E-O de tipo <paramref name="ew"/> y N-S de tipo <paramref name="ns"/>.</summary>
        public static Crossing X(ushort ew, ushort ns, float arm = 150f) => Make(new ushort?[] { ew, ns, ew, ns }, arm);

        /// <summary>T: principal E-O y tallo al sur.</summary>
        public static Crossing T(ushort stem, ushort main, float arm = 150f) => Make(new ushort?[] { main, null, main, stem }, arm);

        /// <summary>Nodo con <paramref name="arms"/> brazos en los angulos dados (grados), todos del mismo tipo.</summary>
        public static (RoadNetwork, EntityId) Star(float[] anglesDeg, ushort[] types, float arm = 60f)
        {
            RoadNetwork n = Kit.New();
            EntityId c = n.AddNodeRaw(Kit.V(0, 0), 0f);
            for (int i = 0; i < anglesDeg.Length; i++)
            {
                double r = anglesDeg[i] * Math.PI / 180.0;
                EntityId o = n.AddNodeRaw(Kit.V((float)(Math.Cos(r) * arm), (float)(Math.Sin(r) * arm)), 0f);
                EntityId s = n.AddSegmentRaw(o, c, Bezier3.FromLine(n.GetNode(o).Position, Kit.V(0, 0)), types[i]);
                Assert.True(s.IsValid);
            }
            return (n, c);
        }

        public static VehicleSystem Sys(RoadNetwork n, IJunctionGate gate, int capacity = 512, ulong seed = 1, bool variation = false)
        {
            return new VehicleSystem(n, gate, capacity, seed, new VehicleConfig { DriverVariation = variation });
        }

        /// <summary>Cuerpo del vehiculo sobre la conexion <paramref name="conn"/>: [ini, fin] en metros de la conexion, o false.</summary>
        public static bool BodyOn(VehicleSystem s, int vi, out int conn, out float start, out float end)
        {
            ref readonly Vehicle v = ref s.GetVehicle(vi);
            float len = s.GetVehicleType(v.Type).Length;
            conn = -1;
            start = end = 0f;
            if (v.Kind == TrackKind.Connection)
            {
                conn = v.Track;
                start = v.S - len;
                end = v.S;
                return true;
            }
            if (v.PrevConnection >= 0)
            {
                conn = v.PrevConnection;
                float l = s.Network.GetConnection(conn).Length;
                start = l - (len - v.S);
                end = l;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Detector de colisiones del ADR 004 para el nodo: pares de vehiculos con cuerpo en conexiones i != j en conflicto cuyas
        /// zonas comunes estan ocupadas a la vez. Devuelve el numero de pares.
        /// </summary>
        public static int CountCollisions(VehicleSystem s, JunctionNode layout, List<(int, int, float, float)> scratch)
        {
            RoadNetwork n = s.Network;
            scratch.Clear();
            for (int i = 0; i < s.HighWater; i++)
            {
                if (!s.GetVehicle(i).IsAlive) continue;
                if (!BodyOn(s, i, out int conn, out float a, out float b)) continue;
                if (n.GetConnection(conn).Node != layout.Node) continue;
                scratch.Add((i, n.GetConnection(conn).LocalIndex, a, b));
            }
            int hits = 0;
            for (int x = 0; x < scratch.Count; x++)
            {
                for (int y = x + 1; y < scratch.Count; y++)
                {
                    var (va, ci, a0, a1) = scratch[x];
                    var (vb, cj, b0, b1) = scratch[y];
                    if (ci == cj) continue;
                    if ((layout.Gates[ci].ConflictMask & (1UL << cj)) == 0) continue;
                    ConflictEntry eij = Find(layout, ci, cj), eji = Find(layout, cj, ci);
                    bool inA = a0 <= eij.EndDm * 0.1f && a1 >= eij.StartDm * 0.1f;
                    bool inB = b0 <= eji.EndDm * 0.1f && b1 >= eji.StartDm * 0.1f;
                    if (inA && inB) hits++;
                }
            }
            return hits;
        }

        /// <summary>
        /// Trozos de cuerpo de cada vehiculo por pista (frente y lo que sobresale hacia atras en las pistas anteriores) y menor hueco
        /// entre trozos consecutivos de la misma pista.
        /// </summary>
        public static float MinChainGap(VehicleSystem s, List<(long, float, float)> pieces)
        {
            RoadNetwork n = s.Network;
            pieces.Clear();
            for (int i = 0; i < s.HighWater; i++)
            {
                ref readonly Vehicle v = ref s.GetVehicle(i);
                if (!v.IsAlive) continue;
                float len = s.GetVehicleType(v.Type).Length;
                float rear = v.S - len;
                pieces.Add((Key(v.Kind, v.Track), Math.Max(0f, rear), v.S));
                float rest = -rear;
                if (rest <= 0f) continue;
                if (v.Kind == TrackKind.Connection)
                {
                    LaneId from = n.GetConnection(v.Track).From;
                    float lf = n.GetLane(from).Length;
                    pieces.Add((Key(TrackKind.Lane, from.Value), Math.Max(0f, lf - rest), lf));
                }
                else if (v.PrevConnection >= 0)
                {
                    float lc = n.GetConnection(v.PrevConnection).Length;
                    pieces.Add((Key(TrackKind.Connection, v.PrevConnection), Math.Max(0f, lc - rest), lc));
                    rest -= lc;
                    if (rest > 0f)
                    {
                        LaneId from = n.GetConnection(v.PrevConnection).From;
                        float lf = n.GetLane(from).Length;
                        pieces.Add((Key(TrackKind.Lane, from.Value), Math.Max(0f, lf - rest), lf));
                    }
                }
            }
            pieces.Sort((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item3.CompareTo(b.Item3));
            float min = float.MaxValue;
            for (int k = 1; k < pieces.Count; k++)
            {
                if (pieces[k].Item1 != pieces[k - 1].Item1) continue;
                float gap = pieces[k].Item2 - pieces[k - 1].Item3;
                if (gap < min) min = gap;
            }
            return min;
        }

        private static long Key(TrackKind k, int track) => ((long)k << 32) | (uint)track;

        public static ConflictEntry Find(JunctionNode jn, int i, int j)
        {
            foreach (ConflictEntry e in jn.ConflictsOf(i))
                if (e.Other == j) return e;
            throw new InvalidOperationException("sin entrada " + i + "->" + j);
        }

        /// <summary>
        /// Recuento de fuerza bruta de permisos y ocupantes del nodo frente al controlador (test 12). Devuelve null si cuadra o
        /// el motivo si no.
        /// </summary>
        public static string? CheckCounters(VehicleSystem s, JunctionController jc, EntityId node)
        {
            RoadNetwork n = s.Network;
            JunctionNode jn = jc.GetJunction(node)!;
            int cc = jn.ConnCount;
            var permits = new int[cc];
            var occ = new int[cc];
            var last = new int[cc];
            var lastRear = new float[cc];
            int first = n.GetNode(node).ConnFirst;
            for (int c = 0; c < cc; c++) { last[c] = -1; lastRear[c] = float.MaxValue; }
            for (int i = 0; i < s.HighWater; i++)
            {
                ref readonly Vehicle v = ref s.GetVehicle(i);
                if (!v.IsAlive) continue;
                if ((v.Flags & VehicleFlags.HasEntryPermit) != 0 && v.Kind == TrackKind.Lane)
                {
                    LaneId to = s.GetRouteStep(i, v.RouteCursor + 1).Lane;
                    var span = n.GetConnectionsFrom(new LaneId(v.Track));
                    for (int k = 0; k < span.Length; k++)
                        if (span[k].To == to && span[k].Node == node) permits[span[k].LocalIndex]++;
                }
                int conn = v.Kind == TrackKind.Connection ? v.Track : v.PrevConnection;
                if (conn >= 0 && n.GetConnection(conn).Node == node)
                {
                    int l = n.GetConnection(conn).LocalIndex;
                    occ[l]++;
                    float rear = s.RearOnConnection(i, conn);
                    if (rear < lastRear[l]) { lastRear[l] = rear; last[l] = i; }
                }
            }
            ulong pm = 0, om = 0;
            for (int c = 0; c < cc; c++)
            {
                ConnGate g = jn.Gates[c];
                if (g.Permits != permits[c]) return "Permits[" + c + "] " + g.Permits + " != " + permits[c];
                if (g.Occupants != occ[c]) return "Occupants[" + c + "] " + g.Occupants + " != " + occ[c];
                if (occ[c] > 0 && g.LastEntrant != last[c])
                {
                    float r1 = s.RearOnConnection(g.LastEntrant, first + c);
                    if (Math.Abs(r1 - lastRear[c]) > 1e-4f) return "LastEntrant[" + c + "] " + g.LastEntrant + " != " + last[c];
                }
                if (occ[c] == 0 && g.LastEntrant != -1) return "LastEntrant[" + c + "] deberia ser -1";
                if (permits[c] > 0) pm |= 1UL << c;
                if (occ[c] > 0) om |= 1UL << c;
            }
            if (pm != jn.PermitMask) return "PermitMask";
            if (om != jn.OccupiedMask) return "OccupiedMask";
            return null;
        }
    }

    /// <summary>Envuelve una puerta y mide la espera desde la primera peticion hasta la salida de la trasera, por vehiculo.</summary>
    internal sealed class TracingGate : IJunctionGate, IVehicleViewConsumer
    {
        private readonly IJunctionGate _inner;
        private readonly RoadNetwork _net;
        private readonly EntityId _node;
        private ulong _tick;
        private long[] _first = new long[0];
        public int MaxWait;
        public int Exits;
        /// <summary>Salidas por segmento de origen (indice del segmento).</summary>
        public readonly Dictionary<int, int> ExitsBySegment = new Dictionary<int, int>();
        public readonly List<EntryRequest> LastRequests = new List<EntryRequest>();
        public readonly List<EntryGrant> LastGrants = new List<EntryGrant>();
        public bool Record { get; set; }

        public TracingGate(IJunctionGate inner, RoadNetwork net, EntityId node)
        {
            _inner = inner;
            _net = net;
            _node = node;
        }

        public void Bind(IVehicleView view, int vehicleCapacity)
        {
            _first = new long[vehicleCapacity];
            for (int i = 0; i < _first.Length; i++) _first[i] = -1;
            if (_inner is IVehicleViewConsumer c) c.Bind(view, vehicleCapacity);
        }

        public void OnNetworkChanged(ReadOnlySpan<EntityId> changedNodes) => _inner.OnNetworkChanged(changedNodes);

        public void BeginTick(ulong tick)
        {
            _tick = tick;
            LastRequests.Clear();
            LastGrants.Clear();
            _inner.BeginTick(tick);
        }

        public void RequestEntry(in EntryRequest req)
        {
            if (_net.GetConnection(req.Connection).Node == _node && _first[req.Vehicle] < 0) _first[req.Vehicle] = (long)_tick;
            if (Record) LastRequests.Add(req);
            _inner.RequestEntry(in req);
        }

        public ReadOnlySpan<EntryGrant> ResolveRequests()
        {
            ReadOnlySpan<EntryGrant> g = _inner.ResolveRequests();
            if (Record) foreach (EntryGrant x in g) LastGrants.Add(x);
            return g;
        }

        public void OnEntered(int connection, int vehicle) => _inner.OnEntered(connection, vehicle);

        public void OnExited(int connection, int vehicle)
        {
            if (_net.GetConnection(connection).Node == _node && _first[vehicle] >= 0)
            {
                int w = (int)((long)_tick - _first[vehicle]);
                if (w > MaxWait) MaxWait = w;
                _first[vehicle] = -1;
                Exits++;
                int seg = _net.GetConnection(connection).From.SegmentIndex;
                ExitsBySegment[seg] = ExitsBySegment.TryGetValue(seg, out int k) ? k + 1 : 1;
            }
            _inner.OnExited(connection, vehicle);
        }

        public void OnVehicleRemoved(int vehicle)
        {
            _first[vehicle] = -1;
            _inner.OnVehicleRemoved(vehicle);
        }

        public void OnPermitCancelled(int vehicle) => _inner.OnPermitCancelled(vehicle);

        /// <summary>Mayor espera de los que aun no han salido.</summary>
        public int PendingMaxWait()
        {
            int m = 0;
            for (int i = 0; i < _first.Length; i++)
            {
                if (_first[i] < 0) continue;
                int w = (int)((long)_tick - _first[i]);
                if (w > m) m = w;
            }
            return m;
        }
    }

    /// <summary>Escenario de propiedades del ADR 004 (A, B, C): demanda aleatoria por carril de entrada, deteccion y recuentos.</summary>
    internal sealed class PropertyScenario
    {
        public readonly Crossing X;
        public readonly VehicleSystem Sys;
        public readonly JunctionController? Ctrl;
        public readonly TracingGate Trace;
        /// <summary>Zonas del cruce para el detector; se reconstruye si cambia la epoca de la red (si no, quedaria obsoleto tras editar).</summary>
        public JunctionNode Layout { get; private set; }
        private ulong _layoutEpoch;
        private readonly JunctionConfig _layoutCfg;
        private readonly List<LaneId> _entries = new List<LaneId>();
        private readonly List<float> _lambda = new List<float>();
        private Pcg32 _rng;
        private readonly List<(int, int, float, float)> _scratch = new List<(int, int, float, float)>();
        private readonly List<(long, float, float)> _pieces = new List<(long, float, float)>();
        public int Collisions;
        /// <summary>Menor hueco visto entre cuerpos consecutivos en una misma pista (test 3 de ADR 002, tambien entre pistas).</summary>
        public float MinChainGap = float.MaxValue;
        public int Spawned;
        public string? CounterError;
        public bool CheckCountersEachTick = true;
        /// <summary>
        /// Si &gt; 0, da de baja a los vehiculos que ya cruzaron y pasan de esta S en el carril de salida: asi la cola de llegada
        /// (parada en el destino) no frena el cruce y se mide su capacidad.
        /// </summary>
        public float DespawnExitBeyond;
        /// <summary>Si es true, los vehiculos que aparecen solo eligen el movimiento recto.</summary>
        public bool StraightOnly;
        /// <summary>
        /// M1-07b: si es true los vehiculos aparecen en movimiento (a la velocidad del ultimo del carril, o la libre si esta vacio) con el
        /// hueco de equilibrio del IDM (s0 + v*T); asi la oferta no la limita arrancar desde parado al inicio del brazo (con false, ~0,19
        /// veh/s por carril como mucho) y los accesos quedan saturados de verdad.
        /// </summary>
        public bool SpawnMoving;

        /// <summary>Salidas acumuladas desde los brazos E y O (la via principal de una X simetrica).</summary>
        public long MajorExits
        {
            get
            {
                long n = 0;
                foreach (int a in new[] { 0, 2 })
                    if (X.Arms[a].IsValid && Trace.ExitsBySegment.TryGetValue(X.Arms[a].Index, out int k)) n += k;
                return n;
            }
        }

        public PropertyScenario(Crossing x, ulong seed, float lambdaMin, float lambdaMax, bool useGapOnly = false, int capacity = 1024, JunctionConfig? config = null)
        {
            X = x;
            _rng = new Pcg32(seed, 1234);
            IJunctionGate inner;
            if (useGapOnly) inner = new GapOnlyGate(x.Net);
            else inner = Ctrl = new JunctionController(x.Net, config);
            Trace = new TracingGate(inner, x.Net, x.Center);
            Sys = new VehicleSystem(x.Net, Trace, capacity, seed, new VehicleConfig { DriverVariation = true });
            _layoutCfg = config ?? new JunctionConfig();
            Layout = new JunctionBuilder(_layoutCfg).Build(x.Net, x.Center);
            _layoutEpoch = x.Net.Epoch;
            foreach (LaneId l in x.EntryLanes())
            {
                _entries.Add(l);
                _lambda.Add(lambdaMin + (lambdaMax - lambdaMin) * _rng.NextFloat());
            }
        }

        public void Step(bool demand)
        {
            if (demand) SpawnAll();
            Sys.Update();
            if (DespawnExitBeyond > 0f)
            {
                for (int i = 0; i < Sys.HighWater; i++)
                {
                    ref readonly Vehicle v = ref Sys.GetVehicle(i);
                    if (v.IsAlive && v.Kind == TrackKind.Lane && v.RouteCursor >= 1 && v.S > DespawnExitBeyond) Sys.Despawn(Sys.IdOf(i));
                }
            }
            if (X.Net.Epoch != _layoutEpoch)
            {
                Layout = new JunctionBuilder(_layoutCfg).Build(X.Net, X.Center);
                _layoutEpoch = X.Net.Epoch;
            }
            Collisions += JK.CountCollisions(Sys, Layout, _scratch);
            float g = JK.MinChainGap(Sys, _pieces);
            if (g < MinChainGap) MinChainGap = g;
            if (Ctrl != null && CheckCountersEachTick && CounterError == null)
            {
                string? e = JK.CheckCounters(Sys, Ctrl, X.Center);
                if (e != null) CounterError = "tick " + Sys.CurrentTick + ": " + e;
            }
        }

        private bool Allowed(TurnType t) => StraightOnly ? t == TurnType.Straight : t != TurnType.UTurn;

        private void SpawnAll()
        {
            RoadNetwork n = X.Net;
            float dt = Sys.Config.Dt;
            for (int k = 0; k < _entries.Count; k++)
            {
                if (_rng.NextFloat() >= _lambda[k] * dt) continue;
                LaneId lane = _entries[k];
                int tail = Sys.LaneTail(lane);
                byte type = _rng.NextInt(5) == 0 ? VehicleTypeId.Truck : VehicleTypeId.Car;
                float speed = 0f;
                if (SpawnMoving)
                {
                    VehicleType vt = Sys.GetVehicleType(type);
                    speed = Math.Min(n.GetLane(lane).SpeedLimit * vt.V0Factor, vt.VMax);
                    if (tail >= 0)
                    {
                        ref readonly Vehicle t = ref Sys.GetVehicle(tail);
                        if (t.V < speed) speed = t.V;
                        if (t.S - Sys.GetVehicleType(t.Type).Length < vt.S0 + 0.5f + speed * vt.T) continue;
                    }
                }
                else if (tail >= 0)
                {
                    ref readonly Vehicle t = ref Sys.GetVehicle(tail);
                    if (t.S - Sys.GetVehicleType(t.Type).Length < 15f) continue;
                }
                var conns = n.GetConnectionsFrom(lane);
                int count = 0;
                for (int i = 0; i < conns.Length; i++) if (Allowed(conns[i].Turn)) count++;
                if (count == 0) continue;
                int pick = _rng.NextInt(count);
                LaneId exit = LaneId.None;
                for (int i = 0; i < conns.Length; i++)
                {
                    if (!Allowed(conns[i].Turn)) continue;
                    if (pick-- == 0) { exit = conns[i].To; break; }
                }
                float target = n.GetLane(exit).Length - 5f;
                if (Sys.TrySpawn(type, new[] { lane, exit }, 0f, target, speed, out _)) Spawned++;
            }
        }
    }
}
