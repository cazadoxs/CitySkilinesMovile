using System;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Urbe.Sim.Vehicles;

namespace Urbe.Sim.Junctions
{
    /// <summary>
    /// Puerta de intersecciones del ADR 004: cola de peticiones por tick, resolucion en orden canonico (rango + envejecimiento,
    /// espera, vehiculo), mascara de conflictos con liberacion parcial, anti-bloqueo por hueco a la salida, stop y semaforos de
    /// fases fijas. Sin asignaciones en el tick (todo preasignado en <see cref="Bind"/> y al reconstruir nodos).
    /// </summary>
    public sealed partial class JunctionController : IJunctionGate, IVehicleViewConsumer, IForcedPassGate
    {
        private const int LocalBits = 6;
        private const int LocalMask = (1 << LocalBits) - 1;
        private const int MaxVehicles = 1 << 20;
        private const int MaxNodes = 1 << 24;

        private readonly RoadNetwork _net;
        private readonly JunctionConfig _cfg;
        private readonly JunctionBuilder _builder;
        private IVehicleView? _view;

        private JunctionNode?[] _nodes = new JunctionNode?[64];
        private int[] _nodeMark = new int[64];
        private int _markStamp;
        private int[] _signalNodes = new int[16];
        private int _signalCount;
        private ulong _tick;

        // Por vehiculo.
        private int _vehCap;
        private uint[] _firstReq = Array.Empty<uint>();
        private int[] _permitRef = Array.Empty<int>();
        private int[] _occRef = Array.Empty<int>();
        private int[] _occPrev = Array.Empty<int>();
        private int[] _occNext = Array.Empty<int>();
        private bool[] _forced = Array.Empty<bool>();
        private GrantDecision[] _decision = Array.Empty<GrantDecision>();

        // Cola del tick.
        private EntryRequest[] _queue = Array.Empty<EntryRequest>();
        private int[] _qNode = Array.Empty<int>();
        private int[] _qLocal = Array.Empty<int>();
        private byte[] _qEff = Array.Empty<byte>();
        private ulong[] _keys = Array.Empty<ulong>();
        private int[] _order = Array.Empty<int>();
        private ulong[] _tmpKeys = Array.Empty<ulong>();
        private int[] _tmpIdx = Array.Empty<int>();
        private readonly int[] _counts = new int[256];
        private EntryGrant[] _grants = Array.Empty<EntryGrant>();
        private int _qCount;
        private int _gCount;

        // Hueco ya concedido por carril de salida en el nodo que se esta resolviendo.
        private readonly int[] _scrLane = new int[RoadNetwork.MaxConnectionsPerNode];
        private readonly float[] _scrSpace = new float[RoadNetwork.MaxConnectionsPerNode];
        private int _scrCount;

        public JunctionStats Stats { get; } = new JunctionStats();

        public JunctionController(RoadNetwork network, JunctionConfig? config = null)
        {
            _net = network ?? throw new ArgumentNullException(nameof(network));
            _cfg = config ?? new JunctionConfig();
            _builder = new JunctionBuilder(_cfg);
            EnsureVehicleCapacity(64);
            for (int i = 0; i < _net.NodeIndexBound; i++)
            {
                EntityId id = _net.NodeIdAt(i);
                if (!id.IsValid) continue;
                EnsureNodeCapacity(i);
                _nodes[i] = _builder.Build(_net, id, null);
            }
            RebuildSignalList();
        }

        public JunctionConfig Config => _cfg;
        public RoadNetwork Network => _net;
        public ulong CurrentTick => _tick;
        /// <summary>Cruces con semaforo.</summary>
        public int SignalCount => _signalCount;

        public void Bind(IVehicleView view, int vehicleCapacity)
        {
            _view = view ?? throw new ArgumentNullException(nameof(view));
            if (vehicleCapacity > MaxVehicles) throw new ArgumentOutOfRangeException(nameof(vehicleCapacity), "maximo 2^20 vehiculos (clave de orden)");
            EnsureVehicleCapacity(vehicleCapacity);
        }

        // ------------------------------------------------------------------ capacidad (fuera del tick)

        private void EnsureVehicleCapacity(int cap)
        {
            if (cap <= _vehCap) return;
            int old = _vehCap;
            int n = Math.Max(cap, Math.Max(64, old * 2));
            Array.Resize(ref _firstReq, n);
            Array.Resize(ref _forced, n);
            Array.Resize(ref _decision, n);
            GrowFill(ref _permitRef, n, -1);
            GrowFill(ref _occRef, n, -1);
            GrowFill(ref _occPrev, n, -1);
            GrowFill(ref _occNext, n, -1);
            Array.Resize(ref _queue, n);
            Array.Resize(ref _qNode, n);
            Array.Resize(ref _qLocal, n);
            Array.Resize(ref _qEff, n);
            Array.Resize(ref _keys, n);
            Array.Resize(ref _order, n);
            Array.Resize(ref _tmpKeys, n);
            Array.Resize(ref _tmpIdx, n);
            Array.Resize(ref _grants, n);
            _vehCap = n;
        }

        private static void GrowFill(ref int[] a, int n, int fill)
        {
            int old = a.Length;
            Array.Resize(ref a, n);
            for (int i = old; i < n; i++) a[i] = fill;
        }

        private void EnsureNodeCapacity(int index)
        {
            if (index >= MaxNodes) throw new InvalidOperationException("demasiados nodos para la clave de orden");
            if (index < _nodes.Length) return;
            int n = _nodes.Length;
            while (n <= index) n *= 2;
            Array.Resize(ref _nodes, n);
            Array.Resize(ref _nodeMark, n);
        }

        // ------------------------------------------------------------------ consulta

        /// <summary>Datos del cruce, o null si el nodo no existe o no esta construido.</summary>
        public JunctionNode? GetJunction(EntityId node)
        {
            if (node.Index < 0 || node.Index >= _nodes.Length) return null;
            JunctionNode? jn = _nodes[node.Index];
            return jn != null && jn.Node == node ? jn : null;
        }

        public ref readonly ConnGate GetGate(int connection)
        {
            ref readonly LaneConnection c = ref _net.GetConnection(connection);
            return ref NodeFor(c.Node.Index, c.Node).Gates[c.LocalIndex];
        }

        public ReadOnlySpan<ConflictEntry> GetConflicts(int connection)
        {
            ref readonly LaneConnection c = ref _net.GetConnection(connection);
            return NodeFor(c.Node.Index, c.Node).ConflictsOf(c.LocalIndex);
        }

        public ControlKind GetControl(EntityId node) => GetJunction(node)?.Control ?? ControlKind.Priority;

        public JunctionState GetState(EntityId node)
        {
            JunctionNode? jn = GetJunction(node);
            return jn != null ? jn.State : default;
        }

        /// <summary>Mayor espera de las peticiones del nodo en el tick actual (gancho M1-11).</summary>
        public ushort GetOldestWaitTicks(EntityId node)
        {
            JunctionNode? jn = GetJunction(node);
            return jn != null && jn.State.WaitTick == _tick ? jn.State.OldestWaitTicks : (ushort)0;
        }

        /// <summary>Aspecto del semaforo para la conexion (indice absoluto); verde si el cruce no tiene semaforo.</summary>
        public SignalAspect GetSignalAspect(EntityId node, int connection)
        {
            JunctionNode? jn = GetJunction(node);
            if (jn == null) return SignalAspect.Green;
            ref readonly LaneConnection c = ref _net.GetConnection(connection);
            if (c.Node != node) return SignalAspect.Green;
            return jn.AspectOf(c.LocalIndex);
        }

        /// <summary>Ticks desde la primera peticion pendiente del vehiculo (0 si no hay). Gancho M1-11.</summary>
        public ushort GetWaitTicks(int vehicle)
        {
            if (vehicle < 0 || vehicle >= _vehCap) return 0;
            uint first = _firstReq[vehicle];
            if (first == 0) return 0;
            ulong w = _tick + 1 - first;
            return w > ushort.MaxValue ? ushort.MaxValue : (ushort)w;
        }

        /// <summary>Resultado de la ultima peticion del vehiculo (diagnostico).</summary>
        public GrantDecision GetLastDecision(int vehicle) => vehicle >= 0 && vehicle < _vehCap ? _decision[vehicle] : GrantDecision.None;

        /// <summary>
        /// Gancho M1-11: la proxima peticion del vehiculo va primera en su nodo y se salta semaforo, stop, reclamacion y hueco a la
        /// salida, nunca la comprobacion de conflictos. Dura hasta que se concede o el vehiculo se da de baja.
        /// </summary>
        public void ForceNextGrant(int vehicle)
        {
            if (vehicle < 0) return;
            if (vehicle >= _vehCap) EnsureVehicleCapacity(vehicle + 1);
            _forced[vehicle] = true;
        }

        /// <summary>True mientras el vehiculo tiene un paso forzado sin conceder (M1-11).</summary>
        public bool IsForced(int vehicle) => vehicle >= 0 && vehicle < _vehCap && _forced[vehicle];

        /// <summary>Retira el paso forzado pendiente (M1-11): si no es seguro no debe seguir reclamando el cruce.</summary>
        public void CancelForce(int vehicle)
        {
            if (vehicle >= 0 && vehicle < _vehCap) _forced[vehicle] = false;
        }

        /// <summary>Conexion local (nodo &lt;&lt; 6 | local) del permiso pendiente del vehiculo, o -1 (tests).</summary>
        public int PermitRefOf(int vehicle) => vehicle >= 0 && vehicle < _vehCap ? _permitRef[vehicle] : -1;

        // ------------------------------------------------------------------ IJunctionGate

        public void OnNetworkChanged(ReadOnlySpan<EntityId> changedNodes)
        {
            if (changedNodes.Length == 0) return;
            _markStamp++;
            for (int k = 0; k < changedNodes.Length; k++)
            {
                EnsureNodeCapacity(changedNodes[k].Index);
                _nodeMark[changedNodes[k].Index] = _markStamp;
            }
            ForgetVehiclesAtMarkedNodes();
            for (int k = 0; k < changedNodes.Length; k++)
            {
                EntityId id = changedNodes[k];
                int idx = id.Index;
                if (_net.IsNodeAlive(id)) _nodes[idx] = _builder.Build(_net, id, _nodes[idx]);
                else if (_nodes[idx] != null && _nodes[idx]!.Node == id) _nodes[idx] = null;
            }
            RebuildSignalList();
        }

        /// <summary>Olvida permisos y ocupacion de los vehiculos en nodos marcados (el sistema de vehiculos los vuelve a registrar).</summary>
        private void ForgetVehiclesAtMarkedNodes()
        {
            for (int v = 0; v < _vehCap; v++)
            {
                int pr = _permitRef[v];
                if (pr >= 0 && _nodeMark[pr >> LocalBits] == _markStamp) _permitRef[v] = -1;
                int or = _occRef[v];
                if (or >= 0 && _nodeMark[or >> LocalBits] == _markStamp)
                {
                    _occRef[v] = -1;
                    _occPrev[v] = -1;
                    _occNext[v] = -1;
                }
            }
        }

        private void RebuildSignalList()
        {
            _signalCount = 0;
            for (int i = 0; i < _nodes.Length; i++)
            {
                JunctionNode? jn = _nodes[i];
                if (jn == null || !jn.IsSignal) continue;
                if (_signalCount == _signalNodes.Length) Array.Resize(ref _signalNodes, _signalCount * 2);
                _signalNodes[_signalCount++] = i;
                jn.UpdateSignal(_tick, _cfg);
            }
        }

        /// <summary>Nodo construido para el indice; si falta o esta obsoleto (cambio no notificado) se reconstruye (asigna).</summary>
        private JunctionNode NodeFor(int ni, EntityId id)
        {
            JunctionNode? jn = ni < _nodes.Length ? _nodes[ni] : null;
            if (jn != null && jn.Node == id && jn.ConnCount == _net.GetNode(id).ConnCount) return jn;
            EnsureNodeCapacity(ni);
            _markStamp++;
            _nodeMark[ni] = _markStamp;
            ForgetVehiclesAtMarkedNodes();
            jn = _builder.Build(_net, id, jn);
            _nodes[ni] = jn;
            RebuildSignalList();
            return jn;
        }

        public void BeginTick(ulong tick)
        {
            _tick = tick;
            _qCount = 0;
            _gCount = 0;
            for (int k = 0; k < _signalCount; k++) _nodes[_signalNodes[k]]!.UpdateSignal(tick, _cfg);
        }

        public void RequestEntry(in EntryRequest req)
        {
            int v = req.Vehicle;
            if (v < 0) return;
            if (v >= _vehCap) EnsureVehicleCapacity(v + 1);
            // Quien pide no tiene permiso en el sistema de vehiculos: un permiso que aun conste aqui es obsoleto.
            if (_permitRef[v] >= 0) ReleasePermit(v);
            if (_firstReq[v] == 0) _firstReq[v] = (uint)(_tick + 1);
            if (_qCount == _queue.Length) EnsureVehicleCapacity(_qCount + 1);
            _queue[_qCount++] = req;
            Stats.Requests++;
        }

        public void OnEntered(int connection, int vehicle)
        {
            if (vehicle < 0) return;
            if (vehicle >= _vehCap) EnsureVehicleCapacity(vehicle + 1);
            ref readonly LaneConnection c = ref _net.GetConnection(connection);
            int ni = c.Node.Index;
            JunctionNode jn = NodeFor(ni, c.Node);
            int local = c.LocalIndex;
            int r = (ni << LocalBits) | local;
            if (_permitRef[vehicle] == r)
            {
                ReleasePermit(vehicle);
                _firstReq[vehicle] = 0;
            }
            if (_occRef[vehicle] >= 0) RemoveOccupant(vehicle);
            InsertOccupant(jn, local, connection, vehicle, r);
        }

        public void OnExited(int connection, int vehicle)
        {
            if (vehicle < 0 || vehicle >= _vehCap) return;
            ref readonly LaneConnection c = ref _net.GetConnection(connection);
            int r = (c.Node.Index << LocalBits) | c.LocalIndex;
            if (_occRef[vehicle] == r) RemoveOccupant(vehicle);
        }

        public void OnVehicleRemoved(int vehicle)
        {
            if (vehicle < 0 || vehicle >= _vehCap) return;
            if (_permitRef[vehicle] >= 0) ReleasePermit(vehicle);
            if (_occRef[vehicle] >= 0) RemoveOccupant(vehicle);
            _firstReq[vehicle] = 0;
            _forced[vehicle] = false;
            _decision[vehicle] = GrantDecision.None;
        }

        public void OnPermitCancelled(int vehicle)
        {
            if (vehicle < 0 || vehicle >= _vehCap) return;
            if (_permitRef[vehicle] >= 0) ReleasePermit(vehicle);
        }

        // ------------------------------------------------------------------ permisos y ocupacion

        private void ReleasePermit(int v)
        {
            int r = _permitRef[v];
            _permitRef[v] = -1;
            JunctionNode? jn = _nodes[r >> LocalBits];
            int local = r & LocalMask;
            if (jn == null || local >= jn.ConnCount) return;
            ref ConnGate g = ref jn.Gates[local];
            if (g.Permits > 0) g.Permits--;
            if (g.Permits == 0) jn.PermitMask &= ~(1UL << local);
        }

        /// <summary>Inserta en la lista de ocupacion ordenada por trasera (cabeza = mas adelantado, cola = LastEntrant).</summary>
        private void InsertOccupant(JunctionNode jn, int local, int connection, int v, int r)
        {
            ref ConnGate g = ref jn.Gates[local];
            if (g.Occupants == 0)
            {
                g.FirstOccupant = -1;
                g.LastEntrant = -1;
            }
            int p = g.LastEntrant;
            if (_view != null && p >= 0)
            {
                float rv = _view.RearOnConnection(v, connection);
                while (p >= 0 && _view.RearOnConnection(p, connection) < rv) p = _occPrev[p];
            }
            int next = p >= 0 ? _occNext[p] : g.FirstOccupant;
            _occPrev[v] = p;
            _occNext[v] = next;
            if (p >= 0) _occNext[p] = v; else g.FirstOccupant = v;
            if (next >= 0) _occPrev[next] = v; else g.LastEntrant = v;
            if (g.Occupants < ushort.MaxValue) g.Occupants++;
            jn.OccupiedMask |= 1UL << local;
            _occRef[v] = r;
        }

        private void RemoveOccupant(int v)
        {
            int r = _occRef[v];
            _occRef[v] = -1;
            int p = _occPrev[v], nx = _occNext[v];
            _occPrev[v] = -1;
            _occNext[v] = -1;
            JunctionNode? jn = _nodes[r >> LocalBits];
            int local = r & LocalMask;
            if (jn == null || local >= jn.ConnCount) return;
            ref ConnGate g = ref jn.Gates[local];
            if (p >= 0) _occNext[p] = nx; else g.FirstOccupant = nx;
            if (nx >= 0) _occPrev[nx] = p; else g.LastEntrant = p;
            if (g.Occupants > 0) g.Occupants--;
            if (g.Occupants == 0)
            {
                g.FirstOccupant = -1;
                g.LastEntrant = -1;
                jn.OccupiedMask &= ~(1UL << local);
            }
        }

        // ------------------------------------------------------------------ hash

        /// <summary>Hash del estado: por nodo (indice) mascaras, contadores, LastEntrant y semaforo; y las primeras peticiones.</summary>
        public ulong ComputeHash()
        {
            ulong h = Mix(0x4A554E43UL, (ulong)_signalCount);
            for (int i = 0; i < _nodes.Length; i++)
            {
                JunctionNode? jn = _nodes[i];
                if (jn == null) continue;
                h = Mix(h, (ulong)i);
                h = Mix(h, jn.PermitMask);
                h = Mix(h, jn.OccupiedMask);
                h = Mix(h, ((ulong)jn.State.Phase << 24) | ((ulong)jn.State.Stage << 16) | jn.State.StageTicksLeft);
                for (int c = 0; c < jn.ConnCount; c++)
                {
                    ref readonly ConnGate g = ref jn.Gates[c];
                    if (g.Permits == 0 && g.Occupants == 0) continue;
                    h = Mix(h, ((ulong)c << 48) | ((ulong)g.Permits << 32) | ((ulong)g.Occupants << 16));
                    h = Mix(h, (uint)g.LastEntrant);
                }
            }
            for (int v = 0; v < _vehCap; v++)
            {
                if (_firstReq[v] == 0) continue;
                h = Mix(h, ((ulong)(uint)v << 32) | _firstReq[v]);
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
