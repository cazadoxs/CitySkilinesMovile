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
    /// Los landmarks valen para una unica epoca de la red: tras cambiarla se ignoran (se vuelve a la heuristica
    /// euclidea) hasta que el llamador invoque <see cref="BuildLandmarks"/>, que cuesta ~2K Dijkstra completos y por
    /// tanto debe llamarse fuera del camino critico (p. ej. cuando la cola esta vacia o con un presupuesto aparte).
    /// TODO(ALT incremental): recalcular solo por partes tras cambios locales, como pide el GDD.
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
        public bool LandmarksReady => _lmBuilt && _lmEpoch == _net.Epoch && _lmCount > 0;

        /// <summary>Calcula los landmarks de la epoca actual (determinista: farthest-point, empates por indice).</summary>
        public void BuildLandmarks()
        {
            EnsureGraph();
            int k = LandmarkCount;
            _lmBuilt = true;
            _lmEpoch = _graphEpoch;
            _lmCount = 0;
            _cache?.Clear(); // las rutas cacheadas pueden diferir en empates segun la heuristica
            if (k <= 0 || _stateCount == 0) return;

            BuildReverseGraph();
            int n = _stateCount;
            int stride = 2 * k;
            Grow(ref _lm, n * stride);
            Grow(ref _lmTmp, n);
            Grow(ref _lmMin, n);
            for (int i = 0; i < n; i++) _lmMin[i] = Unreachable;

            // Primer landmark: el estado mas lejano (alcanzable) desde el primer estado con aristas.
            int start = -1;
            for (int s = 0; s < n && start < 0; s++)
                if (_edgeFirst[s + 1] > _edgeFirst[s]) start = s;
            if (start < 0) return;
            LandmarkDijkstra(start, false, _lmTmp);
            int next = Farthest(_lmTmp);
            if (next < 0) return;

            for (int li = 0; li < k && next >= 0; li++)
            {
                LandmarkDijkstra(next, false, _lmTmp);
                for (int v = 0; v < n; v++)
                {
                    float d = _lmTmp[v];
                    _lm[v * stride + li] = d;
                    if (d < _lmMin[v]) _lmMin[v] = d;
                }
                LandmarkDijkstra(next, true, _lmTmp);
                for (int v = 0; v < n; v++) _lm[v * stride + k + li] = _lmTmp[v];
                _lmCount = li + 1;
                _lmMin[next] = 0f;
                next = Farthest(_lmMin);
            }
            _lmStride = stride;
            _lmHalf = k;
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

        /// <summary>Dijkstra a flujo libre. reverse=false: d(src -&gt; v); reverse=true: d(v -&gt; src). Inalcanzable = +inf.</summary>
        private void LandmarkDijkstra(int src, bool reverse, float[] dist)
        {
            int n = _stateCount;
            for (int i = 0; i < n; i++) dist[i] = Unreachable;
            dist[src] = 0f;
            _heapCount = 0;
            HeapPush(0, 0, src);
            while (_heapCount > 0)
            {
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
            _heapCount = 0;
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
