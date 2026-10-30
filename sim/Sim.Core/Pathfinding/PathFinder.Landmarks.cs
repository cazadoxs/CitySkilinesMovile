using System;
using System.Runtime.CompilerServices;

namespace Urbe.Sim.Pathfinding
{
    /// <summary>
    /// Heuristica ALT (A*, Landmarks, desigualdad triangular; GDD 8.5) sobre el grafo plano de estados.
    /// <para>
    /// Por cada landmark L se guardan, con los costes a flujo libre y sin ruido, <c>dF[v] = d(L -&gt; v)</c> y
    /// <c>dB[v] = d(v -&gt; L)</c>. Para un objetivo t: <c>d(v -&gt; t) &gt;= dF[t] - dF[v]</c> y
    /// <c>d(v -&gt; t) &gt;= dB[v] - dB[t]</c>. Sigue siendo admisible con congestion (solo encarece) y con el ruido
    /// (se escala por 1 - amplitud), y es consistente.
    /// </para>
    /// <para>
    /// Los landmarks valen para una unica epoca de grafo (<see cref="RoadNetwork.GraphEpoch"/>): tras cambiarla se
    /// ignoran (heuristica euclidea) hasta terminar una nueva construccion. Esta es incremental
    /// (<see cref="StartLandmarkBuild"/> + <see cref="AdvanceLandmarks"/> con presupuesto por tick, 2K+1 Dijkstra en total);
    /// <see cref="BuildLandmarks"/> la hace de golpe (arranque, tests, benchmarks).
    /// </para>
    /// </summary>
    public sealed partial class PathFinder
    {
        /// <summary>Distancia "inalcanzable" en las tablas de landmarks (finita para evitar ramas); Far = umbral de poda.</summary>
        private const float Unreachable = 1e30f;
        private const float Far = 1e29f;

        private int _lmCount;
        private bool _lmBuilt;
        private ulong _lmEpoch;
        private float[] _lm = new float[0];      // stride 2K por estado: [0,K) = d(L->v), [K,2K) = d(v->L)
        private float[] _lmTmp = new float[0];
        private float[] _lmMin = new float[0];
        private int[] _revFirst = new int[0];
        private int[] _revFrom = new int[0];
        private float[] _revW = new float[0];
        private int[] _revCursor = new int[0];

        // Estado de la busqueda en curso
        private bool _useAlt;
        private int _lmStride;
        private int _lmHalf;
        private int _goalCount;
        private int _goalState0, _goalState1;
        private float _altScale;

        /// <summary>Numero de landmarks a calcular en la proxima <see cref="BuildLandmarks"/> (0 = ALT desactivado).</summary>
        public int LandmarkCount { get; set; } = 6;

        /// <summary>true si hay landmarks calculados para la epoca actual de la red.</summary>
        public bool LandmarksReady => _lmBuilt && _lmEpoch == _net.GraphEpoch && _lmCount > 0;

        // ---- Construccion incremental (presupuesto por tick) ----
        private enum LmPhase : byte { Idle, Init, Probe, Forward, Backward }
        private LmPhase _lmPhase;
        private ulong _lmBuildEpoch;
        private int _lmK, _lmBuilding, _lmNext;
        private bool _dkReverse;
        private float[] _dkDist = new float[0];
        private HeapEntry[] _lmHeap = new HeapEntry[256];
        private int _lmHeapCount;

        /// <summary>true mientras hay una construccion de landmarks en curso (<see cref="AdvanceLandmarks"/> la hace avanzar).</summary>
        public bool LandmarksBuilding => _lmPhase != LmPhase.Idle;

        /// <summary>Calcula los landmarks de la epoca de grafo actual de golpe (determinista: farthest-point, empates por indice).</summary>
        public void BuildLandmarks()
        {
            StartLandmarkBuild();
            AdvanceLandmarks(int.MaxValue);
        }

        /// <summary>
        /// Descarta los landmarks actuales y empieza (o reinicia) una construccion incremental para la epoca de grafo actual.
        /// Mientras no termine se usa la heuristica euclidea. No hace trabajo pesado: este se reparte en <see cref="AdvanceLandmarks"/>.
        /// </summary>
        public void StartLandmarkBuild()
        {
            _lmBuilt = false;
            _lmCount = 0;
            _lmPhase = LmPhase.Init;
        }

        /// <summary>
        /// Avanza la construccion gastando como mucho ~<paramref name="budget"/> expansiones de nodo de Dijkstra (las pasadas O(n)
        /// de copia/seleccion se cargan al presupuesto y pueden excederlo una vez). Determinista: el tick en que termina solo depende
        /// del grafo y del presupuesto. Devuelve true si no queda trabajo pendiente.
        /// </summary>
        public bool AdvanceLandmarks(int budget)
        {
            if (_lmPhase == LmPhase.Idle) return true;
            if (_lmPhase != LmPhase.Init && _lmBuildEpoch != _net.GraphEpoch) _lmPhase = LmPhase.Init; // el grafo cambio: reiniciar
            // Monticulo propio: una busqueda de ruta entre dos llamadas no debe pisar el de un Dijkstra a medias.
            HeapEntry[] th = _heap; int tc = _heapCount;
            _heap = _lmHeap; _heapCount = _lmHeapCount;
            try { RunLandmarkBuild(ref budget); }
            finally { _lmHeap = _heap; _lmHeapCount = _heapCount; _heap = th; _heapCount = tc; }
            return _lmPhase == LmPhase.Idle;
        }

        private void RunLandmarkBuild(ref int budget)
        {
            int n, stride, k;
            while (budget > 0 && _lmPhase != LmPhase.Idle)
            {
                switch (_lmPhase)
                {
                    case LmPhase.Init:
                    {
                        EnsureGraph();
                        _lmBuildEpoch = _graphEpoch;
                        _lmBuilt = false;
                        _lmCount = 0;
                        _lmBuilding = 0;
                        _lmK = LandmarkCount;
                        _cache?.Clear();
                        n = _stateCount;
                        budget -= n + _edgeCount;
                        int start = -1;
                        if (_lmK > 0 && n > 0)
                        {
                            for (int s = 0; s < n && start < 0; s++)
                                if (_edgeFirst[s + 1] > _edgeFirst[s]) start = s;
                        }
                        if (start < 0) { FinishLandmarks(); break; }
                        BuildReverseGraph();
                        Grow(ref _lm, n * 2 * _lmK);
                        Grow(ref _lmTmp, n);
                        Grow(ref _lmMin, n);
                        for (int i = 0; i < n; i++) _lmMin[i] = Unreachable;
                        DijkstraBegin(start, false, _lmTmp);
                        _lmPhase = LmPhase.Probe;
                        break;
                    }
                    case LmPhase.Probe:
                        if (!DijkstraRun(ref budget)) return;
                        _lmNext = Farthest(_lmTmp);
                        budget -= _stateCount;
                        if (_lmNext < 0) { FinishLandmarks(); break; }
                        DijkstraBegin(_lmNext, false, _lmTmp);
                        _lmPhase = LmPhase.Forward;
                        break;
                    case LmPhase.Forward:
                    {
                        if (!DijkstraRun(ref budget)) return;
                        n = _stateCount; k = _lmK; stride = 2 * k;
                        int li = _lmBuilding;
                        for (int v = 0; v < n; v++)
                        {
                            float d = _lmTmp[v];
                            _lm[v * stride + li] = d;
                            if (d < _lmMin[v]) _lmMin[v] = d;
                        }
                        budget -= n;
                        DijkstraBegin(_lmNext, true, _lmTmp);
                        _lmPhase = LmPhase.Backward;
                        break;
                    }
                    default: // Backward
                    {
                        if (!DijkstraRun(ref budget)) return;
                        n = _stateCount; k = _lmK; stride = 2 * k;
                        int li = _lmBuilding;
                        for (int v = 0; v < n; v++) _lm[v * stride + k + li] = _lmTmp[v];
                        _lmBuilding = li + 1;
                        _lmMin[_lmNext] = 0f;
                        _lmNext = Farthest(_lmMin);
                        budget -= 2 * n;
                        if (_lmBuilding >= k || _lmNext < 0) { FinishLandmarks(); break; }
                        DijkstraBegin(_lmNext, false, _lmTmp);
                        _lmPhase = LmPhase.Forward;
                        break;
                    }
                }
            }
        }

        private void FinishLandmarks()
        {
            _lmCount = _lmBuilding;
            _lmStride = 2 * _lmK;
            _lmHalf = _lmK;
            _lmEpoch = _lmBuildEpoch;
            _lmBuilt = true;
            _lmPhase = LmPhase.Idle;
            _cache?.Clear(); // las rutas cacheadas pueden diferir en empates segun la heuristica
        }

        /// <summary>Estado con mayor distancia finita y &gt; 0 (empate: menor indice); -1 si no hay.</summary>
        private int Farthest(float[] dist)
        {
            int best = -1;
            float bd = 0f;
            for (int v = 0; v < _stateCount; v++)
            {
                float d = dist[v];
                if (d < Far && d > bd) { bd = d; best = v; }
            }
            return best;
        }

        private void BuildReverseGraph()
        {
            int n = _stateCount;
            Grow(ref _revFirst, n + 1);
            Grow(ref _revCursor, n + 1);
            Grow(ref _revFrom, Math.Max(1, _edgeCount));
            Grow(ref _revW, Math.Max(1, _edgeCount));
            for (int i = 0; i <= n; i++) _revFirst[i] = 0;
            for (int e = 0; e < _edgeCount; e++) _revFirst[_edgeTo[e] + 1]++;
            for (int i = 0; i < n; i++) _revFirst[i + 1] += _revFirst[i];
            for (int i = 0; i < n; i++) _revCursor[i] = _revFirst[i];
            for (int u = 0; u < n; u++)
            {
                for (int e = _edgeFirst[u]; e < _edgeFirst[u + 1]; e++)
                {
                    int v = _edgeTo[e];
                    int p = _revCursor[v]++;
                    _revFrom[p] = u;
                    _revW[p] = _edgePen[e] + _baseTime[v];
                }
            }
        }

        /// <summary>Prepara un Dijkstra a flujo libre. reverse=false: d(src -&gt; v); reverse=true: d(v -&gt; src). Inalcanzable = 1e30.</summary>
        private void DijkstraBegin(int src, bool reverse, float[] dist)
        {
            int n = _stateCount;
            for (int i = 0; i < n; i++) dist[i] = Unreachable;
            dist[src] = 0f;
            _dkReverse = reverse;
            _dkDist = dist;
            _heapCount = 0;
            HeapPush(0, 0, src);
        }

        /// <summary>Continua el Dijkstra preparado hasta agotar el presupuesto (1 por nodo sacado). true si termino.</summary>
        private bool DijkstraRun(ref int budget)
        {
            float[] dist = _dkDist;
            bool reverse = _dkReverse;
            while (_heapCount > 0)
            {
                if (budget <= 0) return false;
                budget--;
                int u = HeapPop(out int qkey);
                float key = dist[u];
                if (qkey > Quant(key)) continue;
                if (!reverse)
                {
                    for (int e = _edgeFirst[u]; e < _edgeFirst[u + 1]; e++)
                    {
                        int v = _edgeTo[e];
                        float nd = key + _edgePen[e] + _baseTime[v];
                        if (nd < dist[v]) { dist[v] = nd; HeapPush(Quant(nd), 0, v); }
                    }
                }
                else
                {
                    for (int e = _revFirst[u]; e < _revFirst[u + 1]; e++)
                    {
                        int v = _revFrom[e];
                        float nd = key + _revW[e];
                        if (nd < dist[v]) { dist[v] = nd; HeapPush(Quant(nd), 0, v); }
                    }
                }
            }
            return true;
        }

        /// <summary>Copia las filas de los estados objetivo a vectores locales (una vez por busqueda).</summary>
        private void PrepareAltGoals()
        {
            int k = _lmCount, half = _lmHalf;
            if (_goalF0.Length < k)
            {
                _goalF0 = new float[k]; _goalB0 = new float[k];
                _goalF1 = new float[k]; _goalB1 = new float[k];
            }
            int b0 = _goalState0 * _lmStride;
            for (int i = 0; i < k; i++) { _goalF0[i] = _lm[b0 + i]; _goalB0[i] = _lm[b0 + half + i]; }
            if (_goalCount > 1)
            {
                int b1 = _goalState1 * _lmStride;
                for (int i = 0; i < k; i++) { _goalF1[i] = _lm[b1 + i]; _goalB1[i] = _lm[b1 + half + i]; }
            }
        }

        private float[] _goalF0 = new float[0], _goalB0 = new float[0], _goalF1 = new float[0], _goalB1 = new float[0];

        /// <summary>
        /// Cota ALT de d(v -&gt; objetivo) (ya escalada); &gt;= Far si v no puede llegar al objetivo. Inalcanzable =
        /// Unreachable (1e30, finito): las restas dan "sin informacion" (negativo o 0) o "imposible" (enorme) sin ramas.
        /// Con dos estados objetivo (ambos sentidos) toma el minimo de las dos cotas.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | AggressiveOpt)]
        private float AltHeuristic(int v)
        {
            if ((v >> 1) == _goalSeg && (_goalDirs & (1 << (v & 1))) != 0) return 0f;
            float[] lm = _lm;
            int k = _lmCount, half = _lmHalf;
            int bv = v * _lmStride;
            float[] f0 = _goalF0, b0 = _goalB0;
            float h0 = 0f;
            if (_goalCount == 1)
            {
                for (int i = 0; i < k; i++)
                {
                    float d1 = f0[i] - lm[bv + i];
                    float d2 = lm[bv + half + i] - b0[i];
                    h0 = Math.Max(h0, Math.Max(d1, d2));
                }
                return h0 * _altScale;
            }
            float[] f1 = _goalF1, b1 = _goalB1;
            float h1 = 0f;
            for (int i = 0; i < k; i++)
            {
                float vf = lm[bv + i], vb = lm[bv + half + i];
                float d1 = f0[i] - vf, d2 = vb - b0[i];
                float e1 = f1[i] - vf, e2 = vb - b1[i];
                h0 = Math.Max(h0, Math.Max(d1, d2));
                h1 = Math.Max(h1, Math.Max(e1, e2));
            }
            return (h0 < h1 ? h0 : h1) * _altScale;
        }
    }
}
