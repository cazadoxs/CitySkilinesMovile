using System;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;

namespace Urbe.Sim.Roads
{
    public sealed partial class RoadNetwork
    {
        private struct GridEntry
        {
            public int Segment;
            public int Next;
        }

        private float _invCell;
        private int _cellsX;
        private int _cellsZ;

        // Segmentos: cabezas por celda + pool con lista libre.
        private int[] _cellHead = Array.Empty<int>();
        private GridEntry[] _pool = new GridEntry[256];
        private int _poolCount;
        private int _poolFree = -1;
        private int[] _cellMark = Array.Empty<int>();
        private int _regStamp;
        private Aabb2[] _segBox = Array.Empty<Aabb2>();
        private int[] _segQueryStamp = Array.Empty<int>();
        private int _queryStamp;

        // Nodos: una sola celda por nodo.
        private int[] _nodeCellHead = Array.Empty<int>();
        private int[] _nodeNext = Array.Empty<int>();

        private void InitGrid(float cellSize)
        {
            _invCell = 1f / cellSize;
            _cellsX = Math.Max(1, (int)Math.Ceiling((_worldMax.X - _worldMin.X) / cellSize));
            _cellsZ = Math.Max(1, (int)Math.Ceiling((_worldMax.Z - _worldMin.Z) / cellSize));
            int cells = _cellsX * _cellsZ;
            _cellHead = new int[cells];
            _nodeCellHead = new int[cells];
            _cellMark = new int[cells];
            for (int i = 0; i < cells; i++)
            {
                _cellHead[i] = -1;
                _nodeCellHead[i] = -1;
            }
            _segBox = new Aabb2[_segCap];
            _segQueryStamp = new int[_segCap];
            _nodeNext = new int[_nodeCap];
        }

        private void GrowNodeGrid(int cap)
        {
            Array.Resize(ref _nodeNext, cap);
        }

        private void GrowSegmentIndex(int cap)
        {
            Array.Resize(ref _segBox, cap);
            Array.Resize(ref _segQueryStamp, cap);
        }

        private int CellX(float x)
        {
            int c = (int)Math.Floor((x - _worldMin.X) * _invCell);
            return c < 0 ? 0 : (c >= _cellsX ? _cellsX - 1 : c);
        }

        private int CellZ(float z)
        {
            int c = (int)Math.Floor((z - _worldMin.Z) * _invCell);
            return c < 0 ? 0 : (c >= _cellsZ ? _cellsZ - 1 : c);
        }

        private bool InWorld(Vec2 p)
        {
            return p.X >= _worldMin.X && p.X <= _worldMax.X && p.Z >= _worldMin.Z && p.Z <= _worldMax.Z;
        }

        private static Vec2 PolyPoint(in Bezier3 curve, int i)
        {
            return curve.Evaluate((float)i / PolylineSamples);
        }

        // ------------------------------------------------------------------ nodos en la rejilla

        private void NodeGridAdd(int ni)
        {
            int cell = CellZ(_nodes[ni].Position.Z) * _cellsX + CellX(_nodes[ni].Position.X);
            _nodeNext[ni] = _nodeCellHead[cell];
            _nodeCellHead[cell] = ni;
        }

        private void NodeGridRemove(int ni)
        {
            int cell = CellZ(_nodes[ni].Position.Z) * _cellsX + CellX(_nodes[ni].Position.X);
            int prev = -1;
            for (int e = _nodeCellHead[cell]; e >= 0; prev = e, e = _nodeNext[e])
            {
                if (e != ni) continue;
                if (prev < 0) _nodeCellHead[cell] = _nodeNext[e]; else _nodeNext[prev] = _nodeNext[e];
                return;
            }
        }

        /// <summary>Nodo en esa posicion exacta (igualdad de bits); None si no hay. Con varios, el de menor indice.</summary>
        public EntityId FindNodeAt(Vec2 pos)
        {
            int cell = CellZ(pos.Z) * _cellsX + CellX(pos.X);
            int best = -1;
            for (int e = _nodeCellHead[cell]; e >= 0; e = _nodeNext[e])
            {
                if (_nodes[e].Position == pos && (best < 0 || e < best)) best = e;
            }
            return best < 0 ? EntityId.None : _nodes[best].Id;
        }

        /// <summary>Nodo mas cercano a menos de <paramref name="radius"/> (inclusive); None si no hay. Empate: menor indice.</summary>
        public EntityId NearestNode(Vec2 p, float radius)
        {
            int cx0 = CellX(p.X - radius), cx1 = CellX(p.X + radius);
            int cz0 = CellZ(p.Z - radius), cz1 = CellZ(p.Z + radius);
            float r2 = radius * radius;
            int best = -1;
            float bestD = float.MaxValue;
            for (int cz = cz0; cz <= cz1; cz++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    for (int e = _nodeCellHead[cz * _cellsX + cx]; e >= 0; e = _nodeNext[e])
                    {
                        float d = (_nodes[e].Position - p).LengthSquared;
                        if (d > r2) continue;
                        if (d < bestD || (d == bestD && e < best))
                        {
                            bestD = d;
                            best = e;
                        }
                    }
                }
            }
            return best < 0 ? EntityId.None : _nodes[best].Id;
        }

        // ------------------------------------------------------------------ segmentos en la rejilla

        private int AllocEntry(int segment, int next)
        {
            int e;
            if (_poolFree >= 0)
            {
                e = _poolFree;
                _poolFree = _pool[e].Next;
            }
            else
            {
                if (_poolCount == _pool.Length) Array.Resize(ref _pool, _poolCount * 2);
                e = _poolCount++;
            }
            _pool[e].Segment = segment;
            _pool[e].Next = next;
            return e;
        }

        /// <summary>
        /// Registra (o retira) el segmento en las celdas de las cajas de sus 16 tramos de polilinea inflados con
        /// TotalWidth/2. Al registrar guarda tambien la caja global del segmento.
        /// </summary>
        private void GridRegister(int si, bool add)
        {
            ref readonly Segment sg = ref _segments[si];
            float h = _types.Get(sg.RoadTypeId).TotalWidth * 0.5f;
            int stamp = ++_regStamp;
            Vec2 prev = PolyPoint(sg.Curve, 0);
            float allMinX = float.MaxValue, allMinZ = float.MaxValue, allMaxX = float.MinValue, allMaxZ = float.MinValue;
            for (int i = 1; i <= PolylineSamples; i++)
            {
                Vec2 cur = PolyPoint(sg.Curve, i);
                float minX = Math.Min(prev.X, cur.X) - h, maxX = Math.Max(prev.X, cur.X) + h;
                float minZ = Math.Min(prev.Z, cur.Z) - h, maxZ = Math.Max(prev.Z, cur.Z) + h;
                prev = cur;
                if (minX < allMinX) allMinX = minX;
                if (minZ < allMinZ) allMinZ = minZ;
                if (maxX > allMaxX) allMaxX = maxX;
                if (maxZ > allMaxZ) allMaxZ = maxZ;
                int cx0 = CellX(minX), cx1 = CellX(maxX), cz0 = CellZ(minZ), cz1 = CellZ(maxZ);
                for (int cz = cz0; cz <= cz1; cz++)
                {
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        int cell = cz * _cellsX + cx;
                        if (_cellMark[cell] == stamp) continue;
                        _cellMark[cell] = stamp;
                        if (add) CellAdd(cell, si); else CellRemove(cell, si);
                    }
                }
            }
            if (add) _segBox[si] = new Aabb2(new Vec2(allMinX, allMinZ), new Vec2(allMaxX, allMaxZ));
        }

        private void CellAdd(int cell, int si)
        {
            _cellHead[cell] = AllocEntry(si, _cellHead[cell]);
        }

        private void CellRemove(int cell, int si)
        {
            int prev = -1;
            for (int e = _cellHead[cell]; e >= 0; prev = e, e = _pool[e].Next)
            {
                if (_pool[e].Segment != si) continue;
                int next = _pool[e].Next;
                if (prev < 0) _cellHead[cell] = next; else _pool[prev].Next = next;
                _pool[e].Next = _poolFree;
                _poolFree = e;
                return;
            }
        }

        private int NextQueryStamp()
        {
            if (++_queryStamp == int.MaxValue)
            {
                Array.Clear(_segQueryStamp, 0, _segQueryStamp.Length);
                _queryStamp = 1;
            }
            return _queryStamp;
        }

        /// <summary>Un tramo de polilinea inflado del segmento solapa la caja.</summary>
        private bool SegmentTouchesBox(int si, in Aabb2 box)
        {
            if (!_segBox[si].Overlaps(box)) return false;
            ref readonly Segment sg = ref _segments[si];
            float h = _types.Get(sg.RoadTypeId).TotalWidth * 0.5f;
            Vec2 prev = PolyPoint(sg.Curve, 0);
            for (int i = 1; i <= PolylineSamples; i++)
            {
                Vec2 cur = PolyPoint(sg.Curve, i);
                var pb = new Aabb2(
                    new Vec2(Math.Min(prev.X, cur.X) - h, Math.Min(prev.Z, cur.Z) - h),
                    new Vec2(Math.Max(prev.X, cur.X) + h, Math.Max(prev.Z, cur.Z) + h));
                if (pb.Overlaps(box)) return true;
                prev = cur;
            }
            return false;
        }

        /// <summary>
        /// Segmentos cuyo ancho (cajas infladas de los 16 tramos) toca <paramref name="box"/>, ordenados por indice.
        /// Si no caben todos en <paramref name="results"/> se devuelven los de menor indice. Devuelve cuantos.
        /// </summary>
        public int QuerySegments(in Aabb2 box, Span<EntityId> results)
        {
            if (results.Length == 0) return 0;
            int count = 0;
            int stamp = NextQueryStamp();
            int cx0 = CellX(box.Min.X), cx1 = CellX(box.Max.X);
            int cz0 = CellZ(box.Min.Z), cz1 = CellZ(box.Max.Z);
            for (int cz = cz0; cz <= cz1; cz++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    for (int e = _cellHead[cz * _cellsX + cx]; e >= 0; e = _pool[e].Next)
                    {
                        int si = _pool[e].Segment;
                        if (_segQueryStamp[si] == stamp) continue;
                        _segQueryStamp[si] = stamp;
                        if (!SegmentTouchesBox(si, box)) continue;

                        // Insercion ordenada por indice (busqueda binaria + desplazamiento).
                        int lo = 0, hi = count;
                        while (lo < hi)
                        {
                            int mid = (lo + hi) >> 1;
                            if (results[mid].Index < si) lo = mid + 1; else hi = mid;
                        }
                        if (count < results.Length)
                        {
                            for (int k = count; k > lo; k--) results[k] = results[k - 1];
                            results[lo] = _segments[si].Id;
                            count++;
                        }
                        else if (lo < count)
                        {
                            for (int k = count - 1; k > lo; k--) results[k] = results[k - 1];
                            results[lo] = _segments[si].Id;
                        }
                    }
                }
            }
            return count;
        }

        /// <summary>Segmento mas cercano a <paramref name="p"/> (distancia a su eje) dentro de <paramref name="maxDist"/>.</summary>
        public bool NearestSegment(Vec2 p, float maxDist, out EntityId seg, out float t, out float dist)
        {
            seg = EntityId.None;
            t = 0f;
            dist = 0f;
            var box = new Aabb2(new Vec2(p.X - maxDist, p.Z - maxDist), new Vec2(p.X + maxDist, p.Z + maxDist));
            int stamp = NextQueryStamp();
            int cx0 = CellX(box.Min.X), cx1 = CellX(box.Max.X);
            int cz0 = CellZ(box.Min.Z), cz1 = CellZ(box.Max.Z);
            int best = -1;
            float bestD = float.MaxValue;
            float bestT = 0f;
            for (int cz = cz0; cz <= cz1; cz++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    for (int e = _cellHead[cz * _cellsX + cx]; e >= 0; e = _pool[e].Next)
                    {
                        int si = _pool[e].Segment;
                        if (_segQueryStamp[si] == stamp) continue;
                        _segQueryStamp[si] = stamp;
                        if (!_segBox[si].Overlaps(box)) continue;
                        ref readonly Segment sg = ref _segments[si];
                        float ct = sg.Curve.ClosestT(p);
                        float d = Vec2.Distance(sg.Curve.Evaluate(ct), p);
                        if (d > maxDist) continue;
                        if (d < bestD || (d == bestD && si < best))
                        {
                            bestD = d;
                            best = si;
                            bestT = ct;
                        }
                    }
                }
            }
            if (best < 0) return false;
            seg = _segments[best].Id;
            t = bestT;
            dist = bestD;
            return true;
        }
    }
}
