using System;
using Urbe.Sim.Ids;
using Urbe.Sim.Random;
using Urbe.Sim.Roads;

namespace Urbe.Sim.Vehicles
{
    /// <summary>
    /// Sistema 5 del tick (ADR 002 + 004): vehiculos con IDM sobre la red viaria. Cada vehiculo esta en una pista (carril o conexion);
    /// cada pista mantiene una lista intrusiva ordenada por S (cabeza = mayor S). Sin asignaciones por tick en regimen estable.
    /// </summary>
    public sealed partial class VehicleSystem : IVehicleView
    {
        private const float Inf = float.PositiveInfinity;

        private readonly RoadNetwork _net;
        private readonly IJunctionGate _gate;
        private readonly VehicleType[] _types;
        private readonly VehicleConfig _cfg;
        private readonly int _capacity;

        private readonly Vehicle[] _veh;
        private readonly IdAllocator _ids;
        private int _high;
        private int _alive;

        // Ocupacion por pista (fuera de la red). Head = vehiculo mas adelantado, Tail = el ultimo.
        private int[] _laneHead = Array.Empty<int>();
        private int[] _laneTail = Array.Empty<int>();
        /// <summary>Ultimo vehiculo que salio del carril por el frente y cuya trasera sigue en el (ADR 004, lookahead 3 bis).</summary>
        private int[] _laneLast = Array.Empty<int>();
        private int[] _laneReservedCm = Array.Empty<int>();
        private int[] _connHead = Array.Empty<int>();
        private int[] _connTail = Array.Empty<int>();
        /// <summary>Cabeza de la lista de vehiculos que se estan desplazando lateralmente DESDE este carril (cambio de carril en curso, M1-08); -1 = ninguno.</summary>
        private int[] _laneGhost = Array.Empty<int>();

        private readonly float[] _accel;
        private readonly float[] _maxAdv;
        private readonly float[] _vCap;

        private readonly RoutePool _routes = new RoutePool();
        private readonly LaneId[] _laneScratch = new LaneId[RoutePool.MaxBlock];
        private readonly RouteSegment[] _segScratch = new RouteSegment[RoutePool.MaxBlock];
        private readonly LanePlanner _planner;
        private readonly SnapshotRing _ring;

        private Pcg32 _rng;
        private ulong _epoch;
        private ulong _tick;

        public int SpawnedCount { get; private set; }
        public int ArrivedCount { get; private set; }
        public int DespawnedByEdit { get; private set; }
        /// <summary>Motivo del ultimo <c>TrySpawn</c> fallido (o <see cref="SpawnFailure.None"/> si fue bien).</summary>
        public SpawnFailure LastSpawnFailure { get; private set; }
        /// <summary>Planificador de carriles (M1-08), por si otro sistema necesita resolver cadenas de carriles.</summary>
        public LanePlanner Planner => _planner;

        public VehicleSystem(RoadNetwork network, IJunctionGate? gate, int capacity, ulong seed,
            VehicleConfig? config = null, VehicleType[]? types = null)
        {
            _net = network ?? throw new ArgumentNullException(nameof(network));
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _cfg = config ?? new VehicleConfig();
            _types = types ?? VehicleType.CreateDefaults();
            _gate = gate ?? new GapOnlyGate(network);
            _veh = new Vehicle[capacity];
            _ids = new IdAllocator(capacity);
            _accel = new float[capacity];
            _maxAdv = new float[capacity];
            _vCap = new float[capacity];
            _rng = new Pcg32(seed, 0x564548UL);
            _ring = new SnapshotRing(capacity);
            _planner = new LanePlanner(network);
            _epoch = network.Epoch;
            _netSub = network.Subscribe();
            EnsureTrackCapacity();
            if (_gate is IVehicleViewConsumer consumer) consumer.Bind(this, capacity);
        }

        // ------------------------------------------------------------------ consulta publica

        public int Capacity => _capacity;
        public int AliveCount => _alive;
        /// <summary>Mayor indice de vehiculo usado + 1.</summary>
        public int HighWater => _high;
        public ulong CurrentTick => _tick;
        public RoadNetwork Network => _net;
        public RoutePool Routes => _routes;
        public SnapshotRing Snapshots => _ring;
        public VehicleConfig Config => _cfg;
        public VehicleType GetVehicleType(int typeId) => _types[typeId];

        public bool IsAlive(EntityId id)
        {
            return id.Index >= 0 && id.Index < _high && _veh[id.Index].IsAlive && _veh[id.Index].Generation == id.Generation;
        }

        public ref readonly Vehicle GetVehicle(int index) => ref _veh[index];

        public ref readonly Vehicle GetVehicle(EntityId id) => ref _veh[id.Index];

        public EntityId IdOf(int index) => new EntityId(index, _veh[index].Generation);

        public int LaneHead(LaneId lane) => lane.Value < _laneHead.Length ? _laneHead[lane.Value] : -1;
        public int LaneTail(LaneId lane) => lane.Value < _laneTail.Length ? _laneTail[lane.Value] : -1;
        public int ConnectionHead(int connection) => connection < _connHead.Length ? _connHead[connection] : -1;
        public int ConnectionTail(int connection) => connection < _connTail.Length ? _connTail[connection] : -1;

        public RouteStep GetRouteStep(int vehicle, int step) => _routes.Steps[_veh[vehicle].RouteOffset + step];

        public ushort GetWaitTicks(int vehicle) => _veh[vehicle].WaitTicks;

        /// <summary>Longitud reservada (m) en el carril por vehiculos con permiso que aun no han entrado.</summary>
        public float ReservedLength(LaneId lane) => lane.Value < _laneReservedCm.Length ? _laneReservedCm[lane.Value] * 0.01f : 0f;

        // IVehicleView
        public float FreeSpace(LaneId lane)
        {
            int tail = lane.Value < _laneTail.Length ? _laneTail[lane.Value] : -1;
            float rear = tail >= 0 ? _veh[tail].S - _types[_veh[tail].Type].Length : _net.GetLane(lane).Length;
            return rear - _laneReservedCm[lane.Value] * 0.01f;
        }

        public float TailSpeed(LaneId lane)
        {
            int tail = lane.Value < _laneTail.Length ? _laneTail[lane.Value] : -1;
            return tail >= 0 ? _veh[tail].V : 0f;
        }

        public bool NextApproach(LaneId lane, float maxDistance, out int connection, out float distance, out float speed)
        {
            connection = -1;
            distance = 0f;
            speed = 0f;
            if (lane.Value < 0 || lane.Value >= _laneHead.Length) return false;
            float len = _net.GetLane(lane).Length;
            for (int i = _laneHead[lane.Value]; i >= 0; i = _veh[i].Behind)
            {
                ref Vehicle v = ref _veh[i];
                float d = len - v.S;
                if (d > maxDistance) return false;
                if ((v.Flags & VehicleFlags.HasEntryPermit) != 0) continue;
                if (v.RouteCursor >= v.RouteLast) return false;
                // Dentro de su horizonte ya pide permiso: lo decide el orden canonico de la puerta (y puede frenar a tiempo).
                if (d <= RequestHorizon(in v)) return false;
                connection = FindConn(v.Track, _routes.Steps[v.RouteOffset + v.RouteCursor + 1].Lane.Value);
                distance = d;
                speed = v.V;
                return connection >= 0;
            }
            return false;
        }

        /// <summary>Distancia al final del carril a la que el vehiculo empieza a pedir permiso (fase 1a).</summary>
        private float RequestHorizon(in Vehicle v)
        {
            float b = _types[v.Type].B;
            return v.V * v.V / (2f * b) + v.V * _cfg.Dt + _cfg.StopMargin + _cfg.RequestExtra;
        }

        public float RearOnConnection(int vehicle, int connection)
        {
            ref Vehicle v = ref _veh[vehicle];
            float len = _types[v.Type].Length;
            if (v.Kind == TrackKind.Connection && v.Track == connection) return v.S - len;
            if (v.Kind == TrackKind.Lane && v.PrevConnection == connection) return _net.GetConnection(connection).Length + v.S - len;
            return Inf;
        }

        // ------------------------------------------------------------------ control manual

        public void SetFrozen(EntityId id, bool frozen)
        {
            if (!IsAlive(id)) return;
            ref Vehicle v = ref _veh[id.Index];
            if (frozen)
            {
                v.Flags |= VehicleFlags.Frozen;
                v.V = 0f;
                v.A = 0f;
            }
            else
            {
                v.Flags &= ~VehicleFlags.Frozen;
            }
        }

        /// <summary>Baja inmediata (fuera del tick).</summary>
        public bool Despawn(EntityId id)
        {
            if (!IsAlive(id)) return false;
            RemoveVehicle(id.Index);
            return true;
        }

        /// <summary>Da de baja a todos los vehiculos marcados NeedsReroute (cuando no hay pathfinding que los rehaga).</summary>
        public int DespawnNeedsReroute()
        {
            int n = 0;
            for (int i = 0; i < _high; i++)
            {
                if ((_veh[i].Flags & (VehicleFlags.Alive | VehicleFlags.NeedsReroute)) != (VehicleFlags.Alive | VehicleFlags.NeedsReroute)) continue;
                RemoveVehicle(i);
                n++;
            }
            return n;
        }

        // ------------------------------------------------------------------ altas

        /// <summary>
        /// Alta con ruta de carriles. lanes[0] es el carril inicial (el vehiculo aparece en <paramref name="startS"/>) y cada par
        /// consecutivo debe tener conexion. <paramref name="targetS"/> es la posicion de llegada en el ultimo carril.
        /// Falla si no hay hueco, la ruta no es valida o el sistema esta lleno.
        /// </summary>
        public bool TrySpawn(byte type, ReadOnlySpan<LaneId> lanes, float startS, float targetS, float startSpeed, out EntityId id)
            => SpawnCore(type, lanes, startS, targetS, startSpeed, false, out id);

        /// <summary>
        /// Alta con cadena de carriles de LLEGADA (M1-08): <paramref name="arrival"/>[k] es el carril por el que el vehiculo entra al
        /// segmento k; entre dos consecutivos solo hace falta que algun carril del segmento k conecte con el de llegada del k+1 (el
        /// vehiculo cambia de carril dentro del segmento). Falla si no hay hueco, la ruta no es valida o el sistema esta lleno.
        /// </summary>
        public bool TrySpawnPlanned(byte type, ReadOnlySpan<LaneId> arrival, float startS, float targetS, float startSpeed, out EntityId id)
            => SpawnCore(type, arrival, startS, targetS, startSpeed, true, out id);

        private bool SpawnCore(byte type, ReadOnlySpan<LaneId> lanes, float startS, float targetS, float startSpeed, bool planned, out EntityId id)
        {
            id = EntityId.None;
            LastSpawnFailure = SpawnFailure.InvalidRoute;
            if (_net.Epoch != _epoch) HandleEpoch();
            if (type >= _types.Length || lanes.Length < 1 || lanes.Length > RoutePool.MaxBlock) return false;
            if (_alive >= _capacity)
            {
                LastSpawnFailure = SpawnFailure.NoRoom;
                return false;
            }
            VehicleType t = _types[type];

            for (int k = 0; k < lanes.Length; k++)
            {
                if (!IsUsableLane(lanes[k])) return false;
                if (k > 0 && (planned ? NearestDeparture(lanes[k - 1].Value, lanes[k].Value) : FindConn(lanes[k - 1].Value, lanes[k].Value)) < 0) return false;
            }
            int first = lanes[0].Value;
            float firstLen = _net.GetLane(lanes[0]).Length;
            if (!(startS >= 0f) || startS > firstLen) return false;
            float lastLen = _net.GetLane(lanes[lanes.Length - 1]).Length;
            if (!(targetS >= 0f) || targetS > lastLen) return false;
            if (lanes.Length == 1 && targetS < startS) return false;
            if (!HasSpawnRoom(first, startS, t) || !NextTrackFree(lanes, startS, t))
            {
                LastSpawnFailure = SpawnFailure.NoRoom;
                return false;
            }

            int off = _routes.Allocate(lanes.Length);
            if (off < 0) return false;
            RouteStep[] steps = _routes.Steps;
            for (int k = 0; k < lanes.Length; k++)
                steps[off + k] = new RouteStep(_net.GetLane(lanes[k]).Segment, lanes[k]);

            EntityId eid = _ids.Allocate();
            int i = eid.Index;
            if (i >= _high) _high = i + 1;
            _alive++;
            SpawnedCount++;
            ref Vehicle v = ref _veh[i];
            v = default;
            v.S = startS;
            v.V = startSpeed < 0f ? 0f : startSpeed;
            v.Track = first;
            v.Ahead = -1;
            v.Behind = -1;
            v.RouteOffset = off;
            v.RouteLength = (ushort)lanes.Length;
            v.RouteCursor = 0;
            v.RouteLast = (ushort)(lanes.Length - 1);
            v.TargetS = targetS;
            v.SpeedFactor = _cfg.DriverVariation ? 0.9f + 0.2f * _rng.NextFloat() : 1f;
            v.Type = type;
            v.Kind = TrackKind.Lane;
            v.PrevConnection = -1;
            v.LcFrom = -1;
            v.GhostNext = -1;
            v.Generation = eid.Generation;
            v.Flags = VehicleFlags.Alive;
            InsertSorted(i);
            id = eid;
            LastSpawnFailure = SpawnFailure.None;
            return true;
        }

        /// <summary>
        /// Alta con ruta de segmentos y sentido. Los carriles de llegada los elige el <see cref="LanePlanner"/> (eleccion anticipada,
        /// M1-08): se minimizan los cambios de carril y estos se hacen en marcha. <paramref name="laneSeed"/> reparte los viajes entre
        /// carriles equivalentes (0 = semilla derivada del contador de altas). Falla (<see cref="LastSpawnFailure"/>) si la ruta no
        /// es valida (segmentos no conectados) o no hay hueco.
        /// </summary>
        public bool TrySpawn(byte type, ReadOnlySpan<RouteSegment> segments, float startS, float targetS, float startSpeed, out EntityId id, uint laneSeed = 0)
        {
            id = EntityId.None;
            LastSpawnFailure = SpawnFailure.InvalidRoute;
            if (_net.Epoch != _epoch) HandleEpoch();
            int n = segments.Length;
            if (n < 1 || n > RoutePool.MaxBlock) return false;
            uint seed = laneSeed != 0 ? laneSeed : unchecked((uint)(SpawnedCount + 1) * 2654435761u);
            if (!_planner.Plan(segments, LaneId.None, startS, false, LaneId.None, seed, _laneScratch)) return false;
            return SpawnCore(type, new ReadOnlySpan<LaneId>(_laneScratch, 0, n), startS, targetS, startSpeed, true, out id);
        }

        private bool IsUsableLane(LaneId lane)
        {
            if (lane.Value < 0 || lane.SegmentIndex >= _net.SegmentIndexBound) return false;
            if (lane.Value >= _laneHead.Length) return false;
            EntityId seg = _net.SegmentIdAt(lane.SegmentIndex);
            if (!seg.IsValid || !_net.IsSegmentAlive(seg)) return false;
            return lane.Slot < _net.GetLaneCount(seg);
        }

        /// <summary>Si el carril inicial acaba pronto, comprueba que el vehiculo que ya esta en la conexion o el carril siguiente no queda pegado.</summary>
        private bool NextTrackFree(ReadOnlySpan<LaneId> lanes, float s, VehicleType t)
        {
            if (lanes.Length < 2) return true;
            int conn = FindConn(lanes[0].Value, lanes[1].Value);
            if (conn < 0)
            {
                // Con cambio de carril el carril inicial puede no conectar: se mira la conexion del carril de salida mas cercano.
                int dep = NearestDeparture(lanes[0].Value, lanes[1].Value);
                conn = dep >= 0 ? FindConn(dep, lanes[1].Value) : -1;
                if (conn < 0) return true;
            }
            float d = _net.GetLane(lanes[0]).Length - s;
            float need = t.S0 + _cfg.MinGap;
            int ct = _connTail[conn];
            if (ct >= 0) return d + _veh[ct].S - _types[_veh[ct].Type].Length >= need;
            int lt = _laneTail[lanes[1].Value];
            if (lt >= 0) return d + _net.GetConnection(conn).Length + _veh[lt].S - _types[_veh[lt].Type].Length >= need;
            return true;
        }

        private bool HasSpawnRoom(int lane, float s, VehicleType t)
        {
            // Un vehiculo que se desplaza lateralmente fuera de este carril (M1-08) sigue ocupando su sitio hasta acabar la maniobra.
            if (_laneGhost[lane] >= 0)
            {
                float need = t.S0 + _cfg.MinGap;
                float laneLen = _net.GetLane(new LaneId(lane)).Length;
                for (int g = _laneGhost[lane]; g >= 0; g = _veh[g].GhostNext)
                {
                    float gs = _veh[g].S * (laneLen / _net.GetLane(new LaneId(_veh[g].Track)).Length);
                    if (gs + need > s - t.Length && gs - _types[_veh[g].Type].Length - need < s) return false;
                }
            }
            int behind = -1;
            int ahead = _laneTail[lane];
            while (ahead >= 0 && _veh[ahead].S < s)
            {
                behind = ahead;
                ahead = _veh[ahead].Ahead;
            }
            if (ahead >= 0 && _veh[ahead].S - _types[_veh[ahead].Type].Length - s < t.S0 + _cfg.MinGap) return false;
            if (behind >= 0)
            {
                if (s - t.Length - _veh[behind].S < t.S0 + _cfg.MinGap) return false;
            }
            else if (_laneReservedCm[lane] > 0)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Sustituye la ruta de un vehiculo (re-ruta tras una edicion). lanes[0] debe ser el carril del paso actual (si esta en una
        /// conexion, lanes[0] es el carril de origen y lanes[1] el de destino). Falla si la ruta no es valida o el vehiculo
        /// aun tiene la trasera en una conexion (reintentar un tick despues).
        /// </summary>
        public bool TryReplaceRoute(EntityId id, ReadOnlySpan<LaneId> lanes, float targetS)
            => ReplaceRoute(id, lanes, targetS, false);

        /// <summary>
        /// Re-ruta por segmentos (M1-08): el primer segmento debe ser el actual; los carriles de llegada los planifica el
        /// <see cref="LanePlanner"/> a partir del carril actual (y, si el vehiculo esta en una conexion, del de destino ya fijado).
        /// </summary>
        public bool TryReplaceRoute(EntityId id, ReadOnlySpan<RouteSegment> segments, float targetS)
        {
            int n = segments.Length;
            if (!IsAlive(id) || n < 1 || n > RoutePool.MaxBlock) return false;
            if (_net.Epoch != _epoch) HandleEpoch();
            ref Vehicle v = ref _veh[id.Index];
            if (v.PrevConnection >= 0) return false;
            RouteStep[] old = _routes.Steps;
            LaneId cur = old[v.RouteOffset + v.RouteCursor].Lane;
            LaneId second = v.Kind == TrackKind.Connection ? old[v.RouteOffset + v.RouteCursor + 1].Lane : LaneId.None;
            uint seed = unchecked((uint)id.Index * 2654435761u ^ (uint)_tick);
            if (!_planner.Plan(segments, cur, v.Kind == TrackKind.Lane ? v.S : -1f, false, second, seed, _laneScratch)) return false;
            return ReplaceRoute(id, new ReadOnlySpan<LaneId>(_laneScratch, 0, n), targetS, true);
        }

        private bool ReplaceRoute(EntityId id, ReadOnlySpan<LaneId> lanes, float targetS, bool planned)
        {
            if (!IsAlive(id) || lanes.Length < 1 || lanes.Length > RoutePool.MaxBlock) return false;
            if (_net.Epoch != _epoch) HandleEpoch();
            ref Vehicle v = ref _veh[id.Index];
            if (v.PrevConnection >= 0) return false;
            RouteStep[] old = _routes.Steps;
            LaneId cur = old[v.RouteOffset + v.RouteCursor].Lane;
            if (lanes[0] != cur) return false;
            if (v.Kind == TrackKind.Connection)
            {
                if (lanes.Length < 2 || lanes[1] != old[v.RouteOffset + v.RouteCursor + 1].Lane) return false;
            }
            for (int k = 0; k < lanes.Length; k++)
            {
                if (!IsUsableLane(lanes[k])) return false;
                if (k > 0 && (planned ? NearestDeparture(lanes[k - 1].Value, lanes[k].Value) : FindConn(lanes[k - 1].Value, lanes[k].Value)) < 0) return false;
            }
            if (!(targetS >= 0f) || targetS > _net.GetLane(lanes[lanes.Length - 1]).Length) return false;
            int off = _routes.Allocate(lanes.Length);
            if (off < 0) return false;
            RouteStep[] steps = _routes.Steps;
            for (int k = 0; k < lanes.Length; k++) steps[off + k] = new RouteStep(_net.GetLane(lanes[k]).Segment, lanes[k]);
            // Con el reintento un permiso concedido sigue valido solo si el siguiente carril no cambia.
            if ((v.Flags & VehicleFlags.HasEntryPermit) != 0 && (lanes.Length < 2 || v.Kind != TrackKind.Lane || lanes[1] != old[v.RouteOffset + v.RouteCursor + 1].Lane))
            {
                _laneReservedCm[old[v.RouteOffset + v.RouteCursor + 1].Lane.Value] -= v.ReservedCm;
                v.ReservedCm = 0;
                v.Flags &= ~VehicleFlags.HasEntryPermit;
                _gate.OnPermitCancelled(id.Index);
            }
            _routes.Free(v.RouteOffset, v.RouteLength);
            v.RouteOffset = off;
            v.RouteLength = (ushort)lanes.Length;
            v.RouteLast = (ushort)(lanes.Length - 1);
            v.RouteCursor = 0;
            v.TargetS = targetS;
            v.Flags &= ~VehicleFlags.NeedsReroute;
            return true;
        }

        // ------------------------------------------------------------------ utilidades internas

        /// <summary>Indice absoluto de la conexion fromLane -> toLane, o -1.</summary>
        private int FindConn(int fromLane, int toLane)
        {
            ReadOnlySpan<LaneConnection> span = _net.GetConnectionsFrom(new LaneId(fromLane));
            for (int i = 0; i < span.Length; i++)
                if (span[i].To.Value == toLane) return _net.GetLane(new LaneId(fromLane)).ConnFirst + i;
            return -1;
        }

        private static int Cm(float meters) => (int)(meters * 100f + 0.5f);

        private ref int HeadRef(TrackKind k, int track)
        {
            if (k == TrackKind.Lane) return ref _laneHead[track];
            return ref _connHead[track];
        }

        private ref int TailRef(TrackKind k, int track)
        {
            if (k == TrackKind.Lane) return ref _laneTail[track];
            return ref _connTail[track];
        }

        /// <summary>Inserta el vehiculo en la lista de su pista, ordenado por S, empezando por la cola.</summary>
        private void InsertSorted(int vi)
        {
            ref Vehicle v = ref _veh[vi];
            ref int head = ref HeadRef(v.Kind, v.Track);
            ref int tail = ref TailRef(v.Kind, v.Track);
            int cur = tail;
            if (cur < 0)
            {
                v.Ahead = -1;
                v.Behind = -1;
                head = vi;
                tail = vi;
                return;
            }
            if (_veh[cur].S >= v.S)
            {
                v.Behind = -1;
                v.Ahead = cur;
                _veh[cur].Behind = vi;
                tail = vi;
                return;
            }
            while (true)
            {
                int nx = _veh[cur].Ahead;
                if (nx < 0 || _veh[nx].S >= v.S) break;
                cur = nx;
            }
            int ahead = _veh[cur].Ahead;
            v.Behind = cur;
            v.Ahead = ahead;
            _veh[cur].Ahead = vi;
            if (ahead >= 0) _veh[ahead].Behind = vi;
            else head = vi;
        }

        private void Unlink(int vi)
        {
            ref Vehicle v = ref _veh[vi];
            int a = v.Ahead, b = v.Behind;
            if (a >= 0) _veh[a].Behind = b;
            else HeadRef(v.Kind, v.Track) = b;
            if (b >= 0) _veh[b].Ahead = a;
            else TailRef(v.Kind, v.Track) = a;
            v.Ahead = -1;
            v.Behind = -1;
        }

        /// <summary>Quita el vehiculo de todas las estructuras (lista, reserva, rastreo de trasera, puerta, ruta, id).</summary>
        private void RemoveVehicle(int vi)
        {
            ref Vehicle v = ref _veh[vi];
            Unlink(vi);
            if ((v.Flags & VehicleFlags.HasEntryPermit) != 0)
            {
                int toStep = v.RouteOffset + v.RouteCursor + 1;
                _laneReservedCm[_routes.Steps[toStep].Lane.Value] -= v.ReservedCm;
            }
            if (v.Kind == TrackKind.Connection) ClearLast(_net.GetConnection(v.Track).From.Value, vi);
            else if (v.PrevConnection >= 0) ClearLast(_net.GetConnection(v.PrevConnection).From.Value, vi);
            if (v.LcTicks > 0) EndManeuver(vi);
            FreeRecord(vi);
        }

        private void ClearLast(int lane, int vi)
        {
            if (_laneLast[lane] == vi) _laneLast[lane] = -1;
        }

        /// <summary>Libera ruta e id y avisa a la puerta. No toca listas ni reservas.</summary>
        private void FreeRecord(int vi)
        {
            ref Vehicle v = ref _veh[vi];
            _gate.OnVehicleRemoved(vi);
            _routes.Free(v.RouteOffset, v.RouteLength);
            _ids.Free(new EntityId(vi, v.Generation));
            v.Flags = VehicleFlags.None;
            v.Ahead = -1;
            v.Behind = -1;
            _alive--;
        }

        private void EnsureTrackCapacity()
        {
            int laneCap = Math.Max(_net.SegmentIndexBound, 4) * RoadNetwork.MaxLanesPerSegment;
            if (laneCap > _laneHead.Length)
            {
                laneCap = Math.Max(laneCap, _laneHead.Length * 2);
                GrowFill(ref _laneHead, laneCap, -1);
                GrowFill(ref _laneTail, laneCap, -1);
                GrowFill(ref _laneLast, laneCap, -1);
                GrowFill(ref _laneReservedCm, laneCap, 0);
                GrowFill(ref _laneGhost, laneCap, -1);
            }
            int connCap = Math.Max(_net.ConnectionStorageUsed, 64);
            if (connCap > _connHead.Length)
            {
                connCap = Math.Max(connCap, _connHead.Length * 2);
                GrowFill(ref _connHead, connCap, -1);
                GrowFill(ref _connTail, connCap, -1);
            }
        }

        private static void GrowFill(ref int[] array, int size, int fill)
        {
            int old = array.Length;
            Array.Resize(ref array, size);
            for (int i = old; i < size; i++) array[i] = fill;
        }

        // ------------------------------------------------------------------ hash

        /// <summary>
        /// Hash del estado: vehiculos vivos en orden de indice (S, V, pista, tipo de pista, cursor, flags, espera) y el RNG del sistema.
        /// </summary>
        public ulong ComputeHash()
        {
            ulong h = Mix(0x5645484943UL, _rng.State);
            h = Mix(h, _rng.Increment);
            h = Mix(h, (ulong)_alive);
            for (int i = 0; i < _high; i++)
            {
                ref Vehicle v = ref _veh[i];
                if (!v.IsAlive) continue;
                h = Mix(h, (ulong)i);
                h = Mix(h, (uint)BitConverter.SingleToInt32Bits(v.S));
                h = Mix(h, (uint)BitConverter.SingleToInt32Bits(v.V));
                h = Mix(h, (uint)v.Track);
                h = Mix(h, ((ulong)(byte)v.Kind << 40) | ((ulong)v.RouteCursor << 24) | ((ulong)(byte)v.Flags << 8) | v.Type);
                h = Mix(h, v.WaitTicks | ((ulong)v.LcTicks << 16));
            }
            return h;
        }

        private static ulong Mix(ulong h, ulong x)
        {
            unchecked
            {
                ulong z = (h ^ x) + 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }
    }
}
