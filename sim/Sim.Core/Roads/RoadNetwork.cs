using System;
using System.Collections.Generic;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;

namespace Urbe.Sim.Roads
{
    /// <summary>
    /// Red viaria (ADR 001): nodos, segmentos, carriles, conexiones, indice espacial, epoca y hash.
    /// Las consultas no asignan memoria; las mutaciones pueden hacerlo al crecer arrays (duplican capacidad).
    /// </summary>
    public sealed partial class RoadNetwork
    {
        public const int MaxSegmentsPerNode = 8;
        public const int MaxLanesPerSegment = 8;
        public const int MaxConnectionsPerNode = 64;
        /// <summary>Tramos de polilinea de un segmento para el indice espacial y la longitud de carril.</summary>
        public const int PolylineSamples = 16;

        private const int ArcSamples = ArcLengthTable.DefaultSamples;
        private const int ConnArcSamples = 8;

        private readonly RoadTypeCatalog _types;
        private readonly float _snapRadius;
        private readonly float _minSegmentLength;
        private readonly float _maxSegmentLength;
        private readonly float _cosMinAngle;
        private readonly float _sinMinAngle;
        private readonly float _maxCrossElevDelta;
        private readonly Vec2 _worldMin;
        private readonly Vec2 _worldMax;

        private ulong _epoch;
        private ulong _graphEpoch;

        // ---- Nodos ----
        private readonly IdAllocator _nodeIds;
        private Node[] _nodes;
        private EntityId[] _nodeSegments;
        private int _nodeCap;
        private int _nodeHigh;

        // ---- Segmentos y carriles ----
        private readonly IdAllocator _segIds;
        private Segment[] _segments;
        private Lane[] _lanes;
        private float[] _arcTables;
        private int _segCap;
        private int _segHigh;

        // ---- Conexiones ----
        private LaneConnection[] _conns;
        private float[] _connArc;
        private int _connUsed;
        private int _connHoles;

        private readonly NetworkEdit _scratchEdit = new NetworkEdit();

        public RoadNetwork(RoadTypeCatalog types, NetworkConfig config)
        {
            if (types == null) throw new ArgumentNullException(nameof(types));
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (config.CellSize <= 0f || config.WorldSize.X <= 0f || config.WorldSize.Z <= 0f)
                throw new ArgumentException("Mundo o celda invalidos", nameof(config));
            _types = types;
            _snapRadius = config.SnapRadius;
            _minSegmentLength = config.MinSegmentLength;
            _maxSegmentLength = config.MaxSegmentLength;
            double ang = config.MinJunctionAngleDegrees * Math.PI / 180.0;
            _cosMinAngle = (float)Math.Cos(ang);
            _sinMinAngle = (float)Math.Sin(ang);
            _maxCrossElevDelta = config.MaxCrossElevationDelta;
            _worldMin = config.WorldMin;
            _worldMax = config.WorldMin + config.WorldSize;

            _nodeCap = Math.Max(4, config.InitialNodeCapacity);
            _segCap = Math.Max(4, config.InitialSegmentCapacity);
            _nodeIds = new IdAllocator(_nodeCap);
            _segIds = new IdAllocator(_segCap);
            _nodes = new Node[_nodeCap];
            _nodeSegments = new EntityId[_nodeCap * MaxSegmentsPerNode];
            _segments = new Segment[_segCap];
            _lanes = new Lane[_segCap * MaxLanesPerSegment];
            _arcTables = new float[_segCap * ArcSamples];
            InitChangeLog(_nodeCap, _segCap);
            _conns = new LaneConnection[256];
            _connArc = new float[256 * ConnArcSamples];
            for (int i = 0; i < _nodeCap; i++) _nodes[i].Id = EntityId.None;
            for (int i = 0; i < _segCap; i++) _segments[i].Id = EntityId.None;
            for (int i = 0; i < _lanes.Length; i++) _lanes[i].Segment = EntityId.None;

            InitGrid(config.CellSize);
        }

        // ------------------------------------------------------------------ propiedades

        public ulong Epoch => _epoch;

        /// <summary>
        /// Epoca topologica/geometrica: solo sube con cambios que alteran el grafo de carreteras (nodos, segmentos, conexiones),
        /// no con cambios de control de nodo (<see cref="SetNodeControl"/>). La usan pathfinding, su cache y los landmarks.
        /// </summary>
        public ulong GraphEpoch => _graphEpoch;
        public int NodeCount => _nodeIds.AliveCount;
        public int SegmentCount => _segIds.AliveCount;
        public RoadTypeCatalog Types => _types;

        /// <summary>Mayor indice de nodo usado + 1 (para iterar con <see cref="NodeIdAt"/>).</summary>
        public int NodeIndexBound => _nodeHigh;
        public int SegmentIndexBound => _segHigh;

        /// <summary>Capacidad total reservada para conexiones, incluidos huecos (diagnostico).</summary>
        public int ConnectionStorageUsed => _connUsed;
        public int ConnectionStorageHoles => _connHoles;

        public EntityId NodeIdAt(int index) => index >= 0 && index < _nodeHigh ? _nodes[index].Id : EntityId.None;
        public EntityId SegmentIdAt(int index) => index >= 0 && index < _segHigh ? _segments[index].Id : EntityId.None;

        // ------------------------------------------------------------------ consultas basicas

        public bool IsNodeAlive(EntityId id) => _nodeIds.IsAlive(id);
        public bool IsSegmentAlive(EntityId id) => _segIds.IsAlive(id);

        /// <summary>Sin comprobacion de vida: el llamador usa <see cref="IsNodeAlive"/> si el id puede estar obsoleto.</summary>
        public ref readonly Node GetNode(EntityId id) => ref _nodes[id.Index];

        public ref readonly Segment GetSegment(EntityId id) => ref _segments[id.Index];

        public ref readonly Lane GetLane(LaneId id) => ref _lanes[id.Value];

        public ReadOnlySpan<EntityId> GetNodeSegments(EntityId node)
        {
            return new ReadOnlySpan<EntityId>(_nodeSegments, node.Index * MaxSegmentsPerNode, _nodes[node.Index].SegmentCount);
        }

        public int GetLaneCount(EntityId seg)
        {
            ref readonly Segment s = ref _segments[seg.Index];
            return s.LanesForward + s.LanesBackward;
        }

        public LaneId GetLane(EntityId seg, int slot) => new LaneId(seg.Index, slot);

        public ReadOnlySpan<LaneConnection> GetConnectionsFrom(LaneId lane)
        {
            ref readonly Lane l = ref _lanes[lane.Value];
            return new ReadOnlySpan<LaneConnection>(_conns, l.ConnFirst, l.ConnCount);
        }

        public ReadOnlySpan<LaneConnection> GetConnectionsAt(EntityId node)
        {
            ref readonly Node n = ref _nodes[node.Index];
            return new ReadOnlySpan<LaneConnection>(_conns, n.ConnFirst, n.ConnCount);
        }

        public ref readonly LaneConnection GetConnection(int index) => ref _conns[index];

        /// <summary>Posicion y tangente de avance en el carril a distancia <paramref name="s"/> (acotada a [0, Length]).</summary>
        public void SampleLane(LaneId lane, float s, out Vec2 pos, out Vec2 tangent)
        {
            ref readonly Lane l = ref _lanes[lane.Value];
            ref readonly Segment sg = ref _segments[l.Segment.Index];
            // s -> distancia u sobre la curva central: proporcional dentro del tramo entre retranqueos [SetbackA, Length - SetbackB].
            float uLo = sg.SetbackA;
            float uSpan = sg.Length - sg.SetbackA - sg.SetbackB;
            float u = 0f;
            if (l.Length > 1e-6f)
            {
                if (s < 0f) s = 0f; else if (s > l.Length) s = l.Length;
                u = s * uSpan / l.Length;
            }
            u = l.Direction == LaneDirection.Backward ? uLo + uSpan - u : uLo + u;
            float t = ArcLengthTable.TAtDistance(sg.Curve, new ReadOnlySpan<float>(_arcTables, sg.ArcTableOffset, ArcSamples), u);
            Vec2 tan = sg.Curve.Tangent(t);
            pos = sg.Curve.Evaluate(t) + OffsetCurve.Right(tan) * l.Offset;
            tangent = l.Direction == LaneDirection.Backward ? -tan : tan;
        }

        public void SampleConnection(int index, float s, out Vec2 pos, out Vec2 tangent)
        {
            ref readonly LaneConnection c = ref _conns[index];
            if (c.Length < 1e-3f)
            {
                // Conexion de longitud nula (carriles alineados): punto y direccion del inicio del carril de destino.
                SampleLane(c.To, 0f, out pos, out tangent);
                return;
            }
            if (s < 0f) s = 0f; else if (s > c.Length) s = c.Length;
            float t = ArcLengthTable.TAtDistance(c.Curve, new ReadOnlySpan<float>(_connArc, index * ConnArcSamples, ConnArcSamples), s);
            pos = c.Curve.Evaluate(t);
            tangent = c.Curve.Tangent(t);
        }

        /// <summary>
        /// Cambia el control del cruce (ADR 004: 0 = auto, 1 = prioridad, 2 = stop, 3 = semaforo). Epoca +1 y nodo cambiado,
        /// para que las intersecciones lo reconstruyan. Devuelve false si el nodo no existe; sin cambios si el valor es el mismo.
        /// </summary>
        public bool SetNodeControl(EntityId node, ushort controlId)
        {
            if (!_nodeIds.IsAlive(node)) return false;
            ref Node n = ref _nodes[node.Index];
            if (n.ControlId == controlId) return true;
            n.ControlId = controlId;
            TouchNode(node);
            ulong graphEpoch = _graphEpoch;
            FinishMutation();
            _graphEpoch = graphEpoch; // el control no cambia el grafo de rutas
            return true;
        }

        // ------------------------------------------------------------------ hash

        /// <summary>
        /// Hash canonico: suma modulo 2^64 de SplitMix64 por nodo vivo (posicion, elevacion, control si no es 0) y segmento vivo (curva, tipo).
        /// No depende de indices, generaciones, carriles, conexiones ni epoca.
        /// </summary>
        public ulong ComputeHash()
        {
            ulong sum = 0;
            for (int i = 0; i < _nodeHigh; i++)
            {
                ref readonly Node n = ref _nodes[i];
                if (!n.Id.IsValid) continue;
                ulong h = SplitMix64(0x4E4F4445UL);
                h = Fold(h, n.Position.X);
                h = Fold(h, n.Position.Z);
                h = Fold(h, n.Elevation);
                if (n.ControlId != 0) h = SplitMix64(h ^ ((ulong)n.ControlId << 32));
                unchecked { sum += h; }
            }
            for (int i = 0; i < _segHigh; i++)
            {
                ref readonly Segment s = ref _segments[i];
                if (!s.Id.IsValid) continue;
                ulong h = SplitMix64(0x5345474DUL);
                h = Fold(h, s.Curve.P0.X); h = Fold(h, s.Curve.P0.Z);
                h = Fold(h, s.Curve.P1.X); h = Fold(h, s.Curve.P1.Z);
                h = Fold(h, s.Curve.P2.X); h = Fold(h, s.Curve.P2.Z);
                h = Fold(h, s.Curve.P3.X); h = Fold(h, s.Curve.P3.Z);
                h = SplitMix64(h ^ s.RoadTypeId);
                unchecked { sum += h; }
            }
            return sum;
        }

        private static ulong Fold(ulong h, float v)
        {
            return SplitMix64(h ^ (uint)BitConverter.SingleToInt32Bits(v));
        }

        private static ulong SplitMix64(ulong x)
        {
            unchecked
            {
                ulong z = x + 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        // ------------------------------------------------------------------ capacidad

        private void EnsureNodeCapacity(int index)
        {
            if (index < _nodeCap) return;
            int old = _nodeCap;
            int cap = old;
            while (cap <= index) cap *= 2;
            Array.Resize(ref _nodes, cap);
            Array.Resize(ref _nodeSegments, cap * MaxSegmentsPerNode);
            GrowNodeChangeArrays(cap);
            for (int i = old; i < cap; i++) _nodes[i].Id = EntityId.None;
            _nodeCap = cap;
            GrowNodeGrid(cap);
        }

        private void EnsureSegmentCapacity(int index)
        {
            if (index < _segCap) return;
            int old = _segCap;
            int cap = old;
            while (cap <= index) cap *= 2;
            Array.Resize(ref _segments, cap);
            Array.Resize(ref _lanes, cap * MaxLanesPerSegment);
            Array.Resize(ref _arcTables, cap * ArcSamples);
            GrowSegmentChangeArrays(cap);
            for (int i = old; i < cap; i++) _segments[i].Id = EntityId.None;
            for (int i = old * MaxLanesPerSegment; i < _lanes.Length; i++) _lanes[i].Segment = EntityId.None;
            _segCap = cap;
            GrowSegmentIndex(cap);
        }
    }
}
