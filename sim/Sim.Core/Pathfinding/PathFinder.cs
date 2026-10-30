using System;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Sim.Pathfinding
{
    /// <summary>
    /// A* sobre el grafo de segmentos dirigidos con giros (GDD 8.5, ADR 001).
    /// <para>
    /// Estado de busqueda = (segmento, sentido) = <c>segmentIndex * 2 + (forward ? 0 : 1)</c>: "haber recorrido el
    /// segmento y estar en su nodo final". Una transicion existe solo si hay alguna <see cref="LaneConnection"/> desde un
    /// carril de ese segmento/sentido a un carril del siguiente (respeta sentidos unicos y cambios de sentido solo en
    /// fin de via). El grafo plano (CSR) se reconstruye de forma perezosa cuando cambia <see cref="RoadNetwork.GraphEpoch"/>.
    /// </para>
    /// <para>
    /// Coste = longitud / (limite * factor de congestion) + penalizacion de giro + penalizacion de cruce, multiplicado
    /// por un ruido por conductor en [1-a, 1+a] que es un hash puro de (semilla, estado). Sin asignaciones por busqueda:
    /// los arrays crecen solo cuando crece la red.
    /// </para>
    /// <para>No es seguro entre hilos; una instancia por hilo/sistema.</para>
    /// </summary>
    public sealed partial class PathFinder
    {
        private const int CacheMaxRouteLength = 128;

        private readonly RoadNetwork _net;
        private readonly PathCostConfig _cfg;
        private readonly PathRouteCache? _cache;

        // ---- Grafo plano por epoca ----
        private bool _graphBuilt;
        private ulong _graphEpoch;
        private int _stateCount;
        private int[] _edgeFirst = new int[0];
        private int[] _edgeTo = new int[0];
        private float[] _edgePen = new float[0];
        private float[] _baseTime = new float[0];
        private float[] _endX = new float[0];
        private float[] _endZ = new float[0];
        private int _edgeCount;
        private float _vmax = 1f;

        // ---- Congestion (la rellena otro sistema) ----
        private float[]? _congestion;

        // ---- Diagnostico ----
        public int CacheHits { get; private set; }
        public int CacheMisses { get; private set; }
        public int SearchCount { get; private set; }
        public int ExpandedStates { get; private set; }

        public PathFinder(RoadNetwork network, PathCostConfig? config = null, int cacheCapacity = 1024)
        {
            _net = network ?? throw new ArgumentNullException(nameof(network));
            _cfg = config ?? new PathCostConfig();
            if (cacheCapacity > 0) _cache = new PathRouteCache(cacheCapacity, CacheMaxRouteLength);
        }

        public RoadNetwork Network => _net;
        public PathCostConfig Config => _cfg;

        /// <summary>
        /// Factor de congestion por indice de segmento, en (0, 1] (1 = fluido). null o indice fuera de rango = 1.
        /// El array pertenece al llamador (que lo suaviza y actualiza cada N ticks); tras cambiarlo debe llamar a
        /// <see cref="NotifyCongestionChanged"/>.
        /// </summary>
        public float[]? Congestion
        {
            get => _congestion;
            set { _congestion = value; CongestionEpoch++; }
        }

        /// <summary>
        /// "Epoca de congestion cuantizada" de la clave de cache. La sube el llamador (a su cadencia) con
        /// <see cref="NotifyCongestionChanged"/>; las rutas cacheadas con otra epoca de congestion no se reutilizan.
        /// </summary>
        public uint CongestionEpoch { get; private set; }

        public void NotifyCongestionChanged() { CongestionEpoch++; }

        // ------------------------------------------------------------------ API publica

        /// <summary>Entre dos estados exactos (segmento + sentido).</summary>
        public bool FindPath(RouteStep origin, RouteStep destination, in PathOptions options, Span<RouteStep> output,
            out int length, out float cost)
        {
            return FindPath(origin.Segment, origin.Forward ? TravelDirs.Forward : TravelDirs.Backward,
                destination.Segment, destination.Forward ? TravelDirs.Forward : TravelDirs.Backward,
                options, output, out length, out cost);
        }

        /// <summary>Entre segmentos, admitiendo cualquier sentido en origen y destino.</summary>
        public bool FindPath(EntityId originSegment, EntityId destinationSegment, in PathOptions options,
            Span<RouteStep> output, out int length, out float cost)
        {
            return FindPath(originSegment, TravelDirs.Both, destinationSegment, TravelDirs.Both, options, output, out length, out cost);
        }

        /// <summary>
        /// Entre posiciones: usa el segmento mas cercano a cada punto (hasta <paramref name="snapDistance"/>) y admite
        /// ambos sentidos.
        /// </summary>
        public bool FindPath(Vec2 origin, Vec2 destination, float snapDistance, in PathOptions options,
            Span<RouteStep> output, out int length, out float cost)
        {
            length = 0;
            cost = 0f;
            if (!_net.NearestSegment(origin, snapDistance, out EntityId a, out _, out _)) return false;
            if (!_net.NearestSegment(destination, snapDistance, out EntityId b, out _, out _)) return false;
            return FindPath(a, TravelDirs.Both, b, TravelDirs.Both, options, output, out length, out cost);
        }

        /// <summary>
        /// Busqueda general. Devuelve true y la ruta (incluye el segmento de origen y el de destino) si existe.
        /// Sin ruta: false y <c>length == 0</c>. Si <paramref name="output"/> es corto: false y <c>length</c> = pasos
        /// necesarios (&gt; 0). <paramref name="cost"/> es el coste percibido (con el ruido del conductor).
        /// El coste del segmento de origen y del de destino se cuenta entero.
        /// </summary>
        public bool FindPath(EntityId originSegment, TravelDirs originDirs, EntityId destinationSegment, TravelDirs destinationDirs,
            in PathOptions options, Span<RouteStep> output, out int length, out float cost)
        {
            length = 0;
            cost = 0f;
            originDirs &= TravelDirs.Both;
            destinationDirs &= TravelDirs.Both;
            if (originDirs == TravelDirs.None || destinationDirs == TravelDirs.None) return false;
            if (!_net.IsSegmentAlive(originSegment) || !_net.IsSegmentAlive(destinationSegment)) return false;
            EnsureGraph();

            float amp = options.NoiseAmplitude;
            if (!(amp > 0f)) amp = 0f; else if (amp > 0.5f) amp = 0.5f;

            int oSeg = originSegment.Index, dSeg = destinationSegment.Index;
            bool useCache = _cache != null && !options.NoCache;
            ulong k1 = 0, k2 = 0, k3 = 0;
            if (useCache)
            {
                k1 = (uint)((oSeg << 2) | (int)originDirs) | ((ulong)(uint)((dSeg << 2) | (int)destinationDirs) << 32);
                k2 = options.VehicleType | ((ulong)CongestionEpoch << 8);
                k3 = options.DriverSeed | ((ulong)(uint)BitConverterSingleToInt32(amp) << 32);
                int n = _cache!.TryGet(k1, k2, k3, _cacheTmp, out float cachedCost);
                if (n >= 0)
                {
                    CacheHits++;
                    cost = cachedCost;
                    return EmitRoute(_cacheTmp.AsSpan(0, n), output, out length);
                }
                CacheMisses++;
            }

            int len = Search(oSeg, originDirs, dSeg, destinationDirs, options.DriverSeed, amp, out float c);
            if (useCache)
                _cache!.Put(k1, k2, k3, len > 0 ? new ReadOnlySpan<int>(_route, 0, len) : ReadOnlySpan<int>.Empty, c);
            cost = c;
            return EmitRoute(new ReadOnlySpan<int>(_route, 0, len), output, out length);
        }

        private readonly int[] _cacheTmp = new int[CacheMaxRouteLength];

        private static int BitConverterSingleToInt32(float v) => BitConverter.SingleToInt32Bits(v);

        private bool EmitRoute(ReadOnlySpan<int> states, Span<RouteStep> output, out int length)
        {
            length = states.Length;
            if (states.Length == 0) return false;
            if (states.Length > output.Length) return false;
            for (int i = 0; i < states.Length; i++)
            {
                int st = states[i];
                output[i] = new RouteStep(_net.SegmentIdAt(st >> 1), (st & 1) == 0);
            }
            return true;
        }

        // ------------------------------------------------------------------ grafo por epoca

        private void EnsureGraph()
        {
            if (_graphBuilt && _graphEpoch == _net.GraphEpoch) return;
            RebuildGraph();
            _graphBuilt = true;
            _graphEpoch = _net.GraphEpoch;
            _cache?.Clear();
        }

        private static void Grow<T>(ref T[] array, int needed)
        {
            if (array.Length >= needed) return;
            int cap = Math.Max(needed, array.Length * 2);
            Array.Resize(ref array, cap);
        }

        private void RebuildGraph()
        {
            int segBound = _net.SegmentIndexBound;
            _stateCount = segBound * 2;
            Grow(ref _edgeFirst, _stateCount + 1);
            Grow(ref _baseTime, _stateCount);
            Grow(ref _endX, _stateCount);
            Grow(ref _endZ, _stateCount);
            Grow(ref _nodes, _stateCount);
            Grow(ref _edgeTo, 16);
            Grow(ref _edgePen, 16);

            RoadTypeCatalog types = _net.Types;
            float vmax = 0f;
            int edges = 0;
            for (int si = 0; si < segBound; si++)
            {
                EntityId segId = _net.SegmentIdAt(si);
                for (int dir = 0; dir < 2; dir++)
                {
                    int state = si * 2 + dir;
                    _edgeFirst[state] = edges;
                    if (!segId.IsValid)
                    {
                        _baseTime[state] = 0f;
                        _endX[state] = 0f;
                        _endZ[state] = 0f;
                        continue;
                    }
                    ref readonly Segment seg = ref _net.GetSegment(segId);
                    float limit = types.Get(seg.RoadTypeId).SpeedLimit;
                    if (limit > vmax) vmax = limit;
                    _baseTime[state] = seg.Length / limit;
                    EntityId endNode = dir == 0 ? seg.NodeB : seg.NodeA;
                    ref readonly Node node = ref _net.GetNode(endNode);
                    _endX[state] = node.Position.X;
                    _endZ[state] = node.Position.Z;
                    float junctionPen = node.Kind == JunctionKind.Junction ? _cfg.JunctionPenalty : 0f;

                    int first = edges;
                    int laneCount = seg.LanesForward + seg.LanesBackward;
                    LaneDirection want = dir == 0 ? LaneDirection.Forward : LaneDirection.Backward;
                    for (int slot = 0; slot < laneCount; slot++)
                    {
                        LaneId lane = new LaneId(si, slot);
                        if (_net.GetLane(lane).Direction != want) continue;
                        ReadOnlySpan<LaneConnection> conns = _net.GetConnectionsFrom(lane);
                        for (int k = 0; k < conns.Length; k++)
                        {
                            ref readonly LaneConnection c = ref conns[k];
                            ref readonly Lane to = ref _net.GetLane(c.To);
                            int target = to.Segment.Index * 2 + (to.Direction == LaneDirection.Forward ? 0 : 1);
                            float pen = TurnPenalty(c.Turn) + junctionPen;
                            int found = -1;
                            for (int e = first; e < edges; e++)
                                if (_edgeTo[e] == target) { found = e; break; }
                            if (found >= 0)
                            {
                                if (pen < _edgePen[found]) _edgePen[found] = pen;
                                continue;
                            }
                            if (edges == _edgeTo.Length)
                            {
                                Grow(ref _edgeTo, edges + 1);
                                Grow(ref _edgePen, edges + 1);
                            }
                            _edgeTo[edges] = target;
                            _edgePen[edges] = pen;
                            edges++;
                        }
                    }
                }
            }
            _edgeFirst[_stateCount] = edges;
            _edgeCount = edges;
            _vmax = vmax > 0f ? vmax : 1f;
        }

        private float TurnPenalty(TurnType t)
        {
            switch (t)
            {
                case TurnType.Right: return _cfg.RightTurnPenalty;
                case TurnType.Left: return _cfg.LeftTurnPenalty;
                case TurnType.UTurn: return _cfg.UTurnPenalty;
                default: return _cfg.StraightPenalty;
            }
        }

        /// <summary>Estados y aristas del grafo plano actual (diagnostico).</summary>
        public int GraphEdgeCount { get { EnsureGraph(); return _edgeCount; } }
    }
}
