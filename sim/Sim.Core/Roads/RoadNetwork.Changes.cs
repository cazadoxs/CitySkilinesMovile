using System;
using Urbe.Sim.Ids;

namespace Urbe.Sim.Roads
{
    /// <summary>Estado de un segmento justo antes de la primera mutacion que un suscriptor aun no ha consumido (M1-15).</summary>
    public readonly struct SegmentPreState
    {
        public readonly float Length;
        public readonly float SetbackA;
        public readonly float SetbackB;
        private readonly float _l0, _l1, _l2, _l3, _l4, _l5, _l6, _l7;

        public SegmentPreState(float length, float setbackA, float setbackB, ReadOnlySpan<float> laneLengths, int laneCount)
        {
            Length = length;
            SetbackA = setbackA;
            SetbackB = setbackB;
            _l0 = laneCount > 0 ? laneLengths[0] : 0f;
            _l1 = laneCount > 1 ? laneLengths[1] : 0f;
            _l2 = laneCount > 2 ? laneLengths[2] : 0f;
            _l3 = laneCount > 3 ? laneLengths[3] : 0f;
            _l4 = laneCount > 4 ? laneLengths[4] : 0f;
            _l5 = laneCount > 5 ? laneLengths[5] : 0f;
            _l6 = laneCount > 6 ? laneLengths[6] : 0f;
            _l7 = laneCount > 7 ? laneLengths[7] : 0f;
        }

        /// <summary>Longitud que tenia el carril de esa ranura (0 si no existia).</summary>
        public float LaneLength(int slot)
        {
            switch (slot)
            {
                case 0: return _l0;
                case 1: return _l1;
                case 2: return _l2;
                case 3: return _l3;
                case 4: return _l4;
                case 5: return _l5;
                case 6: return _l6;
                case 7: return _l7;
                default: return 0f;
            }
        }
    }

    /// <summary>
    /// Un segmento dividido en dos (M1-15): lo que un consumidor necesita para recolocar lo que habia sobre el segmento eliminado.
    /// <see cref="CutDistance"/> es la distancia sobre la curva central desde el nodo A del segmento viejo hasta el corte; la primera
    /// mitad cubre [0, CutDistance] y la segunda [CutDistance, OldLength]. Las mitades heredan tipo, sentidos y numero de carriles,
    /// asi que la ranura de un carril es la misma en el viejo y en las dos mitades.
    /// </summary>
    public readonly struct SegmentSplit
    {
        public readonly EntityId Old;
        public readonly EntityId Half1;
        public readonly EntityId Half2;
        public readonly EntityId Node;
        public readonly float CutDistance;
        public readonly float OldLength;
        public readonly byte LanesForward;

        public SegmentSplit(EntityId old, EntityId half1, EntityId half2, EntityId node, float cutDistance, float oldLength, byte lanesForward)
        {
            Old = old;
            Half1 = half1;
            Half2 = half2;
            Node = node;
            CutDistance = cutDistance;
            OldLength = oldLength;
            LanesForward = lanesForward;
        }
    }

    // Registro de cambios con un cursor por suscriptor (M1-15). Cada mutacion anade eventos a un log compartido; cada suscriptor
    // lo lee a su ritmo (vehiculos, mallas de Unity, metricas...) sin quitarselos a los demas. Sin suscriptores no se registra
    // nada. Cuando todos los cursores alcanzan el final el log se vacia, y si se llena se compacta el prefijo ya leido por todos:
    // en regimen estable no asigna memoria (solo crece si un suscriptor deja de consumir).
    public sealed partial class RoadNetwork
    {
        private enum ChangeKind : byte { Node, Segment, Split }

        private struct ChangeEvent
        {
            public ChangeKind Kind;
            public bool HasPre;
            public EntityId A;
            public SegmentSplit Split;
            public SegmentPreState Pre;
        }

        private ChangeEvent[] _log = new ChangeEvent[64];
        private int _logCount;
        private long _logOffset;

        private long[] _subCursor = new long[4];
        private bool[] _subActive = new bool[4];
        private int _subCount;

        // Deduplicado por mutacion (un nodo o segmento se registra una vez por mutacion).
        private int[] _nodeTouchStamp = Array.Empty<int>();
        private int[] _nodeTouchGen = Array.Empty<int>();
        private int[] _segTouchStamp = Array.Empty<int>();
        private int[] _segTouchGen = Array.Empty<int>();
        private int _mutStamp = 1;

        // Deduplicado por consulta.
        private int[] _nodeColStamp = Array.Empty<int>();
        private int[] _nodeColGen = Array.Empty<int>();
        private int[] _segColStamp = Array.Empty<int>();
        private int[] _segColGen = Array.Empty<int>();
        private int _colStamp = 1;

        // Resultado de la ultima consulta.
        private EntityId[] _cNodes = new EntityId[32];
        private long[] _cNodeKeys = new long[32];
        private int _cNodeCount;
        private EntityId[] _cSegs = new EntityId[32];
        private long[] _cSegKeys = new long[32];
        private int[] _cSegOrder = new int[32];
        private SegmentPreState[] _cPreTmp = new SegmentPreState[32];
        private bool[] _cHasPreTmp = new bool[32];
        private EntityId[] _cSegsSorted = new EntityId[32];
        private SegmentPreState[] _cPre = new SegmentPreState[32];
        private bool[] _cHasPre = new bool[32];
        private int _cSegCount;
        private SegmentSplit[] _cSplits = new SegmentSplit[16];
        private int _cSplitCount;

        private uint _versionSeq;

        private void InitChangeLog(int nodeCap, int segCap)
        {
            GrowNodeChangeArrays(nodeCap);
            GrowSegmentChangeArrays(segCap);
        }

        private void GrowNodeChangeArrays(int cap)
        {
            Array.Resize(ref _nodeTouchStamp, cap);
            Array.Resize(ref _nodeTouchGen, cap);
            Array.Resize(ref _nodeColStamp, cap);
            Array.Resize(ref _nodeColGen, cap);
        }

        private void GrowSegmentChangeArrays(int cap)
        {
            Array.Resize(ref _segTouchStamp, cap);
            Array.Resize(ref _segTouchGen, cap);
            Array.Resize(ref _segColStamp, cap);
            Array.Resize(ref _segColGen, cap);
        }

        // ------------------------------------------------------------------ suscripcion

        /// <summary>
        /// Da de alta un suscriptor. Arranca con el cursor al final: no ve lo anterior a su suscripcion, asi que debe inicializarse con
        /// el estado completo de la red (iterando nodos y segmentos) y despues consumir solo los cambios.
        /// </summary>
        public int Subscribe()
        {
            int id = -1;
            for (int i = 0; i < _subActive.Length; i++)
                if (!_subActive[i]) { id = i; break; }
            if (id < 0)
            {
                id = _subActive.Length;
                Array.Resize(ref _subActive, id * 2);
                Array.Resize(ref _subCursor, id * 2);
            }
            _subActive[id] = true;
            _subCursor[id] = _logOffset + _logCount;
            _subCount++;
            return id;
        }

        /// <summary>Da de baja al suscriptor (libera su cursor para que el log pueda compactarse).</summary>
        public void Unsubscribe(int subscriber)
        {
            if (subscriber < 0 || subscriber >= _subActive.Length || !_subActive[subscriber]) return;
            _subActive[subscriber] = false;
            _subCount--;
            if (_subCount == 0)
            {
                _logOffset += _logCount;
                _logCount = 0;
            }
        }

        /// <summary>True si el suscriptor tiene cambios sin consumir.</summary>
        public bool HasPendingChanges(int subscriber)
        {
            CheckSubscriber(subscriber);
            return _subCursor[subscriber] < _logOffset + _logCount;
        }

        private void CheckSubscriber(int subscriber)
        {
            if (subscriber < 0 || subscriber >= _subActive.Length || !_subActive[subscriber])
                throw new ArgumentException("Suscriptor invalido", nameof(subscriber));
        }

        /// <summary>
        /// Entrega los nodos y segmentos tocados desde la ultima consulta de ese suscriptor (ordenados por indice; incluye los liberados)
        /// y avanza su cursor. Los spans valen hasta la siguiente mutacion o consulta de cualquier suscriptor.
        /// </summary>
        public void ConsumeChanges(int subscriber, out ReadOnlySpan<EntityId> nodes, out ReadOnlySpan<EntityId> segments)
        {
            ConsumeChanges(subscriber, out nodes, out segments, out _);
        }

        /// <summary>
        /// Como la sobrecarga corta y ademas los segmentos divididos (en orden cronologico) para recolocar lo que habia sobre ellos.
        /// <see cref="TryGetPreState"/> da el estado previo de los segmentos tocados.
        /// </summary>
        public void ConsumeChanges(int subscriber, out ReadOnlySpan<EntityId> nodes, out ReadOnlySpan<EntityId> segments, out ReadOnlySpan<SegmentSplit> splits)
        {
            CheckSubscriber(subscriber);
            if (++_colStamp == int.MaxValue)
            {
                Array.Clear(_nodeColStamp, 0, _nodeColStamp.Length);
                Array.Clear(_segColStamp, 0, _segColStamp.Length);
                _colStamp = 1;
            }
            _cNodeCount = 0;
            _cSegCount = 0;
            _cSplitCount = 0;
            int start = (int)(_subCursor[subscriber] - _logOffset);
            for (int e = start; e < _logCount; e++)
            {
                ref ChangeEvent ev = ref _log[e];
                switch (ev.Kind)
                {
                    case ChangeKind.Node:
                    {
                        int i = ev.A.Index;
                        if (_nodeColStamp[i] == _colStamp && _nodeColGen[i] == ev.A.Generation) break;
                        _nodeColStamp[i] = _colStamp;
                        _nodeColGen[i] = ev.A.Generation;
                        if (_cNodeCount == _cNodes.Length)
                        {
                            Array.Resize(ref _cNodes, _cNodeCount * 2);
                            Array.Resize(ref _cNodeKeys, _cNodeCount * 2);
                        }
                        _cNodes[_cNodeCount] = ev.A;
                        _cNodeKeys[_cNodeCount] = Key(ev.A);
                        _cNodeCount++;
                        break;
                    }
                    case ChangeKind.Segment:
                    {
                        int i = ev.A.Index;
                        if (_segColStamp[i] == _colStamp && _segColGen[i] == ev.A.Generation) break;
                        _segColStamp[i] = _colStamp;
                        _segColGen[i] = ev.A.Generation;
                        if (_cSegCount == _cSegs.Length) GrowSegmentScratch();
                        _cSegs[_cSegCount] = ev.A;
                        _cSegKeys[_cSegCount] = Key(ev.A);
                        _cSegOrder[_cSegCount] = _cSegCount;
                        _cPreTmp[_cSegCount] = ev.Pre;
                        _cHasPreTmp[_cSegCount] = ev.HasPre;
                        _cSegCount++;
                        break;
                    }
                    default:
                        if (_cSplitCount == _cSplits.Length) Array.Resize(ref _cSplits, _cSplitCount * 2);
                        _cSplits[_cSplitCount++] = ev.Split;
                        break;
                }
            }
            _subCursor[subscriber] = _logOffset + _logCount;

            // Orden por indice (y generacion) sin asignar: claves long + Array.Sort de claves/valores.
            Array.Sort(_cNodeKeys, _cNodes, 0, _cNodeCount);
            Array.Sort(_cSegKeys, _cSegOrder, 0, _cSegCount);
            for (int k = 0; k < _cSegCount; k++)
            {
                int o = _cSegOrder[k];
                _cSegsSorted[k] = _cSegs[o];
                _cPre[k] = _cPreTmp[o];
                _cHasPre[k] = _cHasPreTmp[o];
            }
            nodes = new ReadOnlySpan<EntityId>(_cNodes, 0, _cNodeCount);
            segments = new ReadOnlySpan<EntityId>(_cSegsSorted, 0, _cSegCount);
            splits = new ReadOnlySpan<SegmentSplit>(_cSplits, 0, _cSplitCount);
            CompactLog();
        }

        /// <summary>
        /// Estado previo (longitud, retranqueos y longitud de cada carril) del segmento tal como lo vio el suscriptor en su ultima
        /// consulta, para los segmentos que ya existian entonces. False si es nuevo (creado desde la ultima consulta) o no esta en ella.
        /// </summary>
        public bool TryGetPreState(EntityId segment, out SegmentPreState state)
        {
            long key = Key(segment);
            int lo = 0, hi = _cSegCount - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                long k = Key(_cSegsSorted[mid]);
                if (k < key) lo = mid + 1;
                else if (k > key) hi = mid - 1;
                else
                {
                    state = _cPre[mid];
                    return _cHasPre[mid];
                }
            }
            state = default;
            return false;
        }

        private static long Key(EntityId id) => ((long)id.Index << 32) | (uint)id.Generation;

        private void GrowSegmentScratch()
        {
            int n = _cSegCount * 2;
            Array.Resize(ref _cSegs, n);
            Array.Resize(ref _cSegKeys, n);
            Array.Resize(ref _cSegOrder, n);
            Array.Resize(ref _cPreTmp, n);
            Array.Resize(ref _cHasPreTmp, n);
            Array.Resize(ref _cSegsSorted, n);
            Array.Resize(ref _cPre, n);
            Array.Resize(ref _cHasPre, n);
        }

        /// <summary>Si todos los suscriptores han leido todo, el log se vacia (sin mover memoria).</summary>
        private void CompactLog()
        {
            if (_logCount == 0) return;
            long end = _logOffset + _logCount;
            for (int i = 0; i < _subActive.Length; i++)
                if (_subActive[i] && _subCursor[i] < end) return;
            _logOffset = end;
            _logCount = 0;
        }

        private ref ChangeEvent AppendEvent()
        {
            if (_logCount == _log.Length)
            {
                // Primero se descarta el prefijo que ya leyeron todos; solo si no hay prefijo se crece.
                long min = long.MaxValue;
                for (int i = 0; i < _subActive.Length; i++)
                    if (_subActive[i] && _subCursor[i] < min) min = _subCursor[i];
                int drop = min == long.MaxValue ? _logCount : (int)(min - _logOffset);
                if (drop > 0)
                {
                    Array.Copy(_log, drop, _log, 0, _logCount - drop);
                    _logCount -= drop;
                    _logOffset += drop;
                }
                else
                {
                    Array.Resize(ref _log, _log.Length * 2);
                }
            }
            return ref _log[_logCount++];
        }

        // ------------------------------------------------------------------ registro desde las mutaciones

        /// <summary>
        /// Marca el nodo como cambiado: sube su version (el respaldo de los cruces la compara) y, si hay suscriptores, lo registra
        /// una vez por mutacion.
        /// </summary>
        private void TouchNode(EntityId id)
        {
            int i = id.Index;
            _nodes[i].Version = ++_versionSeq;
            if (_subCount == 0) return;
            if (_nodeTouchStamp[i] == _mutStamp && _nodeTouchGen[i] == id.Generation) return;
            _nodeTouchStamp[i] = _mutStamp;
            _nodeTouchGen[i] = id.Generation;
            ref ChangeEvent ev = ref AppendEvent();
            ev = default;
            ev.Kind = ChangeKind.Node;
            ev.A = id;
        }

        /// <summary>Segmento nuevo (sin estado previo).</summary>
        private void TouchSegmentNew(EntityId id)
        {
            LogSegment(id, false);
        }

        /// <summary>Segmento existente que va a cambiar: llamar ANTES de modificarlo para guardar su estado previo.</summary>
        private void TouchSegmentExisting(EntityId id)
        {
            LogSegment(id, true);
        }

        private void LogSegment(EntityId id, bool existing)
        {
            if (_subCount == 0) return;
            int i = id.Index;
            if (_segTouchStamp[i] == _mutStamp && _segTouchGen[i] == id.Generation) return;
            _segTouchStamp[i] = _mutStamp;
            _segTouchGen[i] = id.Generation;
            ref ChangeEvent ev = ref AppendEvent();
            ev = default;
            ev.Kind = ChangeKind.Segment;
            ev.A = id;
            if (!existing) return;
            ref readonly Segment sg = ref _segments[i];
            int lanes = sg.LanesForward + sg.LanesBackward;
            int baseLane = i * MaxLanesPerSegment;
            Span<float> tmp = stackalloc float[MaxLanesPerSegment];
            for (int slot = 0; slot < lanes && slot < MaxLanesPerSegment; slot++) tmp[slot] = _lanes[baseLane + slot].Length;
            ev.HasPre = true;
            ev.Pre = new SegmentPreState(sg.Length, sg.SetbackA, sg.SetbackB, tmp, lanes);
        }

        private void LogSplit(EntityId old, EntityId half1, EntityId half2, EntityId node, float cut, float oldLength, byte lanesForward)
        {
            if (_subCount == 0) return;
            ref ChangeEvent ev = ref AppendEvent();
            ev = default;
            ev.Kind = ChangeKind.Split;
            ev.Split = new SegmentSplit(old, half1, half2, node, cut, oldLength, lanesForward);
        }

        /// <summary>Version del nodo: sube cada vez que se toca (conexiones, control, geometria). 0 si el nodo no existe.</summary>
        public uint GetNodeVersion(EntityId node) => _nodeIds.IsAlive(node) ? _nodes[node.Index].Version : 0u;
    }
}
