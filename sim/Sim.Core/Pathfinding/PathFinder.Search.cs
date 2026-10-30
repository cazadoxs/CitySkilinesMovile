using System;
using System.Runtime.CompilerServices;

namespace Urbe.Sim.Pathfinding
{
    public sealed partial class PathFinder
    {
        /// <summary>MethodImplOptions.AggressiveOptimization (no existe en netstandard2.1): sin tiering ni modo depuracion.</summary>
        internal const MethodImplOptions AggressiveOpt = (MethodImplOptions)512;

        private struct SearchNode
        {
            public int Stamp;   // == _searchId: G/Parent validos en esta busqueda
            public int Closed;  // == _searchId: ya expandido
            public float G;
            public int Parent;
            public float H;     // heuristica, calculada una sola vez por estado y busqueda
        }

        private SearchNode[] _nodes = new SearchNode[0];
        private int _searchId;
        private int[] _route = new int[64];

        // Monticulo binario (insercion perezosa: puede haber duplicados; se descartan al sacar si ya estan cerrados).
        private struct HeapEntry { public long K; public int Val; }
        private HeapEntry[] _heap = new HeapEntry[256];
        private int _heapCount;

        // Estado del objetivo de la busqueda en curso.
        private int _goalSeg;
        private int _goalDirs;
        private bool _goalHasA, _goalHasB;
        private float _goalAx, _goalAz, _goalBx, _goalBz;
        private float _hScale;

        /// <summary>Busca; deja la ruta en <c>_route</c> (codigos de estado) y devuelve su longitud (0 = sin ruta).</summary>
        [MethodImpl(AggressiveOpt)]
        private int Search(int originSeg, TravelDirs originDirs, int destSeg, TravelDirs destDirs, uint seed, float amp, out float cost)
        {
            SearchCount++;
            cost = 0f;
            if (++_searchId == int.MaxValue)
            {
                Array.Clear(_nodes, 0, _nodes.Length);
                _searchId = 1;
            }
            int sid = _searchId;
            _heapCount = 0;

            // Objetivo: la ruta termina al recorrer el segmento de destino; el punto de entrada de cada sentido
            // es su nodo inicial (Forward = NodeA, Backward = NodeB).
            _goalSeg = destSeg;
            _goalDirs = (int)destDirs;
            Geometry.Vec2 pa = _net.GetNode(_net.GetSegment(_net.SegmentIdAt(destSeg)).NodeA).Position;
            Geometry.Vec2 pb = _net.GetNode(_net.GetSegment(_net.SegmentIdAt(destSeg)).NodeB).Position;
            _goalHasA = (_goalDirs & 1) != 0;
            _goalHasB = (_goalDirs & 2) != 0;
            _goalAx = pa.X; _goalAz = pa.Z; _goalBx = pb.X; _goalBz = pb.Z;
            // Admisible a flujo libre y sin ruido: la congestion solo encarece. Con ruido, solo si AdmissibleNoise (ver PathCostConfig).
            // TODO(ALT): sustituir/combinar por landmarks (max con la cota del triangulo) en Heuristic(); punto de extension.
            float hNoise = _cfg.AdmissibleNoise ? 1f - amp : 1f;
            _hScale = hNoise / _vmax;

            // ALT si hay landmarks de esta epoca; si no, euclidea.
            _useAlt = _lmBuilt && _lmEpoch == _graphEpoch && _lmCount > 0;
            if (_useAlt)
            {
                _altScale = hNoise * (1f - 1e-5f);
                _goalCount = 0;
                if (_goalHasA) { _goalState0 = destSeg * 2; _goalCount = 1; }
                if (_goalHasB)
                {
                    if (_goalCount == 0) _goalState0 = destSeg * 2 + 1; else _goalState1 = destSeg * 2 + 1;
                    _goalCount++;
                }
                PrepareAltGoals();
            }

            float[]? cong = _congestion;
            float minCf = _cfg.MinCongestionFactor;
            SearchNode[] nodes = _nodes;
            int[] edgeFirst = _edgeFirst, edgeTo = _edgeTo;
            float[] edgePen = _edgePen, baseTime = _baseTime;

            for (int d = 0; d < 2; d++)
            {
                if (((int)originDirs & (1 << d)) == 0) continue;
                int st = originSeg * 2 + d;
                float g = StateCost(st, 0f, baseTime, cong, minCf, seed, amp);
                ref SearchNode n = ref nodes[st];
                if (n.Stamp == sid && g >= n.G) continue;
                float h0 = Heuristic(st);
                if (h0 >= Far) continue; // el objetivo es inalcanzable desde aqui (ALT)
                n.Stamp = sid;
                n.G = g;
                n.H = h0;
                n.Parent = -1;
                HeapPush(Quant(g + h0), Quant(h0), st);
            }

            int expanded = 0;
            while (_heapCount > 0)
            {
                int cur = HeapPop(out _);
                ref SearchNode cn = ref nodes[cur];
                if (cn.Closed == sid) continue;
                cn.Closed = sid;
                expanded++;
                if ((cur >> 1) == _goalSeg && (_goalDirs & (1 << (cur & 1))) != 0)
                {
                    ExpandedStates += expanded;
                    cost = cn.G;
                    return Reconstruct(cur);
                }
                float gCur = cn.G;
                int eEnd = edgeFirst[cur + 1];
                for (int e = edgeFirst[cur]; e < eEnd; e++)
                {
                    int t = edgeTo[e];
                    ref SearchNode tn = ref nodes[t];
                    if (tn.Closed == sid) continue;
                    float ng = gCur + StateCost(t, edgePen[e], baseTime, cong, minCf, seed, amp);
                    float h;
                    if (tn.Stamp == sid)
                    {
                        if (ng >= tn.G) continue;
                        h = tn.H;
                    }
                    else
                    {
                        h = Heuristic(t);
                        if (h >= Far) { tn.Closed = sid; continue; } // inalcanzable: no se vuelve a evaluar
                        tn.Stamp = sid;
                        tn.H = h;
                    }
                    tn.G = ng;
                    tn.Parent = cur;
                    HeapPush(Quant(ng + h), Quant(h), t);
                }
            }
            ExpandedStates += expanded;
            return 0;
        }

        /// <summary>Coste percibido de entrar al estado: (penalizaciones + longitud/velocidad efectiva) * ruido.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | AggressiveOpt)]
        private static float StateCost(int state, float pen, float[] baseTime, float[]? cong, float minCf, uint seed, float amp)
        {
            float t = baseTime[state];
            if (cong != null)
            {
                int si = state >> 1;
                if (si < cong.Length)
                {
                    float cf = cong[si];
                    if (cf < minCf) cf = minCf; else if (cf > 1f) cf = 1f;
                    t /= cf;
                }
            }
            float c = pen + t;
            if (amp > 0f)
            {
                uint h = unchecked(seed ^ ((uint)state * 0x9E3779B1u));
                h ^= h >> 16; h = unchecked(h * 0x85EBCA6Bu);
                h ^= h >> 13; h = unchecked(h * 0xC2B2AE35u);
                h ^= h >> 16;
                c *= 1f + amp * ((h >> 8) * (2f / 16777216f) - 1f);
            }
            return c;
        }

        /// <summary>Cota inferior del coste restante desde el final del estado: distancia euclidea / velocidad maxima.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | AggressiveOpt)]
        private float Heuristic(int state)
        {
            if (_useAlt) return AltHeuristic(state);
            if ((state >> 1) == _goalSeg && (_goalDirs & (1 << (state & 1))) != 0) return 0f;
            float x = _endX[state], z = _endZ[state];
            float best = float.MaxValue;
            if (_goalHasA)
            {
                float dx = x - _goalAx, dz = z - _goalAz;
                best = dx * dx + dz * dz;
            }
            if (_goalHasB)
            {
                float dx = x - _goalBx, dz = z - _goalBz;
                float d2 = dx * dx + dz * dz;
                if (d2 < best) best = d2;
            }
            return (float)Math.Sqrt(best) * _hScale;
        }

        private int Reconstruct(int goal)
        {
            int len = 0;
            for (int s = goal; s >= 0; s = _nodes[s].Parent) len++;
            if (_route.Length < len) _route = new int[Math.Max(len, _route.Length * 2)];
            int i = len;
            for (int s = goal; s >= 0; s = _nodes[s].Parent) _route[--i] = s;
            return len;
        }

        // ------------------------------------------------------------------ monticulo

        /// <summary>Cuantiza segundos a 1/1024 s para ordenar el monticulo con desempate estable.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | AggressiveOpt)]
        private static int Quant(float seconds) => seconds >= 1.5e6f ? 1_536_000_000 : (int)(seconds * 1024f);

        /// <summary>key = f cuantizado; tie = h cuantizado (a igual f, primero el mas cercano al objetivo: evita explorar toda la rejilla de empates).</summary>
        [MethodImpl(AggressiveOpt)]
        private void HeapPush(int key, int tie, int val)
        {
            if (_heapCount == _heap.Length) Array.Resize(ref _heap, _heapCount * 2);
            HeapEntry[] h = _heap;
            long k = ((long)key << 32) | (uint)tie;
            int i = _heapCount++;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (k >= h[p].K) break;
                h[i] = h[p];
                i = p;
            }
            h[i].K = k;
            h[i].Val = val;
        }

        [MethodImpl(AggressiveOpt)]
        private int HeapPop(out int key)
        {
            HeapEntry[] h = _heap;
            int topVal = h[0].Val;
            key = (int)(h[0].K >> 32);
            int n = --_heapCount;
            if (n > 0)
            {
                long ek = h[n].K;
                int ev = h[n].Val;
                int i = 0;
                while (true)
                {
                    int c = 2 * i + 1;
                    if (c >= n) break;
                    if (c + 1 < n && h[c + 1].K < h[c].K) c++;
                    if (h[c].K >= ek) break;
                    h[i] = h[c];
                    i = c;
                }
                h[i].K = ek;
                h[i].Val = ev;
            }
            return topVal;
        }
    }
}
