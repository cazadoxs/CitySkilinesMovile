using System;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Sim.City
{
    /// <summary>
    /// Celda de zona de 8x8 m (ADR 005). Pertenece a un lado de un segmento zonificable, no a una rejilla global.
    /// <c>Dir</c> es el vector unitario desde la celda hacia la calzada (su "frente"); la celda es un cuadrado orientado segun Dir.
    /// <c>Side</c> 0 = derecha del sentido A a B, 1 = izquierda; <c>Along</c> = orden a lo largo del segmento; <c>Depth</c> 0 = junto a la calle.
    /// </summary>
    public struct ZoneCell
    {
        public Vec2 Pos;
        public Vec2 Dir;
        public EntityId Segment;
        public byte Side;
        public byte Along;
        public byte Depth;
        public ZoneType Zone;
        public byte Density;
        /// <summary>Indice del edificio que ocupa la celda (-1 = libre).</summary>
        public int Building;

        public bool IsAlive => Segment.IsValid;
    }

    /// <summary>
    /// Celdas de zona de toda la ciudad (GDD 4.4 y 5.4). Se generan a ambos lados de cada carretera zonificable cada 8 m de arco entre
    /// los retranqueos, hasta 4 de fondo, descartando las que chocan con una calzada o con otra celda. Al cambiar la red solo se
    /// regeneran los segmentos afectados (los tocados y los que tienen cerca) y la zona pintada se conserva por posicion.
    /// </summary>
    public sealed class ZoneGrid
    {
        public const float CellSize = 8f;
        public const float CellHalf = 4f;
        private const float Shrink = 3.95f;
        private const int ArcSamples = ArcLengthTable.DefaultSamples;

        private readonly RoadNetwork _net;
        private readonly CityBalance _bal;
        private readonly int _sub;
        private readonly int _maxDepth;

        private ZoneCell[] _cells = new ZoneCell[1024];
        private int _high;
        private int[] _free = new int[256];
        private int _freeCount;
        private int _alive;

        // Indice espacial de 8 m: lista enlazada de celdas por casilla.
        private readonly Vec2 _min;
        private readonly int _binsX, _binsZ;
        private readonly int[] _binHead;
        private int[] _binNext = new int[1024];

        // Por indice de segmento.
        private int[]?[] _segCells = new int[]?[64];
        private int[] _segAlong = new int[64];
        private int[] _segGen = new int[64];
        private Aabb2[] _segArea = new Aabb2[64];
        private int[] _stamp = new int[64];
        private int _curStamp;

        private readonly float[] _arc = new float[ArcSamples];
        private readonly EntityId[] _nbr = new EntityId[256];
        private int _nbrCount;

        // Contadores por zona.
        private readonly int[] _zoned = new int[5];
        private readonly int[] _zonedFree = new int[5];

        // Resultado del ultimo Refresh: edificios cuyas celdas se regeneraron y correspondencia vieja -> nueva.
        private int[] _remap = new int[1024];
        private int[] _affected = new int[64];
        private int _affectedCount;

        private struct OldCell
        {
            public Vec2 Pos;
            public ZoneType Zone;
            public byte Density;
            public int Building;
            public int Index;
        }

        private OldCell[] _old = new OldCell[256];
        private int _oldCount;
        private int[] _new = new int[256];
        private int _newCount;
        private bool[] _claimed = new bool[256];

        public ZoneGrid(RoadNetwork network, CityBalance balance)
        {
            _net = network ?? throw new ArgumentNullException(nameof(network));
            _bal = balance ?? throw new ArgumentNullException(nameof(balance));
            _maxDepth = Math.Max(1, Math.Min(_bal.ZoneMaxDepth, 8));
            float half = _bal.MapHalfSize;
            _min = new Vec2(-half, -half);
            _binsX = Math.Max(1, (int)Math.Ceiling(2f * half / CellSize));
            _binsZ = _binsX;
            _binHead = new int[_binsX * _binsZ];
            for (int i = 0; i < _binHead.Length; i++) _binHead[i] = -1;
            _sub = network.Subscribe();
            // Si la red ya tiene segmentos, se genera todo la primera vez.
            if (network.SegmentCount > 0) GenerateAll();
        }

        // ------------------------------------------------------------------ consulta

        /// <summary>Cota superior de los indices de celda (para iterar con <see cref="IsCellAlive"/>).</summary>
        public int CellIndexBound => _high;
        public int CellCount => _alive;
        public bool IsCellAlive(int index) => index >= 0 && index < _high && _cells[index].Segment.IsValid;
        public ref readonly ZoneCell GetCell(int index) => ref _cells[index];
        /// <summary>Celdas con esa zona (libres u ocupadas).</summary>
        public int ZonedCount(ZoneType zone) => _zoned[(int)zone];
        /// <summary>Celdas con esa zona y sin edificio.</summary>
        public int ZonedFreeCount(ZoneType zone) => _zonedFree[(int)zone];
        public int MaxDepth => _maxDepth;

        /// <summary>Numero de celdas a lo largo del segmento (0 si no tiene celdas).</summary>
        public int AlongCount(EntityId segment)
        {
            int si = segment.Index;
            if (si < 0 || si >= _segCells.Length || _segCells[si] == null || _segGen[si] != segment.Generation) return 0;
            return _segAlong[si];
        }

        /// <summary>Celda en (lado, posicion a lo largo, fondo) del segmento, o -1.</summary>
        public int CellAt(EntityId segment, int side, int along, int depth)
        {
            int n = AlongCount(segment);
            if (n == 0 || side < 0 || side > 1 || along < 0 || along >= n || depth < 0 || depth >= _maxDepth) return -1;
            return _segCells[segment.Index]![(side * n + along) * _maxDepth + depth];
        }

        /// <summary>Celda mas cercana a <paramref name="p"/> dentro de <paramref name="maxDist"/>, o -1.</summary>
        public int FindCell(Vec2 p, float maxDist)
        {
            int best = -1;
            float bestD = maxDist * maxDist;
            int r = (int)Math.Ceiling(maxDist / CellSize);
            int bx = BinX(p.X), bz = BinZ(p.Z);
            for (int z = bz - r; z <= bz + r; z++)
            {
                if (z < 0 || z >= _binsZ) continue;
                for (int x = bx - r; x <= bx + r; x++)
                {
                    if (x < 0 || x >= _binsX) continue;
                    for (int c = _binHead[z * _binsX + x]; c >= 0; c = _binNext[c])
                    {
                        float d = (_cells[c].Pos - p).LengthSquared;
                        if (d < bestD || (d == bestD && best >= 0 && c < best)) { bestD = d; best = c; }
                    }
                }
            }
            return best;
        }

        /// <summary>Celdas cuyo centro esta en el circulo, en orden de indice. Devuelve cuantas escribio (como mucho result.Length).</summary>
        public int QueryCircle(Vec2 center, float radius, int[] result)
        {
            int n = 0;
            int r = (int)Math.Ceiling(radius / CellSize) + 1;
            int bx = BinX(center.X), bz = BinZ(center.Z);
            float r2 = radius * radius;
            for (int z = bz - r; z <= bz + r; z++)
            {
                if (z < 0 || z >= _binsZ) continue;
                for (int x = bx - r; x <= bx + r; x++)
                {
                    if (x < 0 || x >= _binsX) continue;
                    for (int c = _binHead[z * _binsX + x]; c >= 0; c = _binNext[c])
                    {
                        if ((_cells[c].Pos - center).LengthSquared > r2 || n >= result.Length) continue;
                        result[n++] = c;
                    }
                }
            }
            Array.Sort(result, 0, n);
            return n;
        }

        /// <summary>Celdas cuyo centro esta dentro del poligono (regla par-impar), en orden de indice.</summary>
        public int QueryPolygon(Vec2[] polygon, int[] result)
        {
            if (polygon.Length < 3) return 0;
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < polygon.Length; i++)
            {
                minX = Math.Min(minX, polygon[i].X); maxX = Math.Max(maxX, polygon[i].X);
                minZ = Math.Min(minZ, polygon[i].Z); maxZ = Math.Max(maxZ, polygon[i].Z);
            }
            int n = 0;
            int x0 = Math.Max(0, BinX(minX)), x1 = Math.Min(_binsX - 1, BinX(maxX));
            int z0 = Math.Max(0, BinZ(minZ)), z1 = Math.Min(_binsZ - 1, BinZ(maxZ));
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                    for (int c = _binHead[z * _binsX + x]; c >= 0; c = _binNext[c])
                    {
                        if (n >= result.Length || !PointInPolygon(_cells[c].Pos, polygon)) continue;
                        result[n++] = c;
                    }
            Array.Sort(result, 0, n);
            return n;
        }

        /// <summary>Celdas de un segmento hasta cierto fondo (1 a 4), ambos lados, en orden de indice.</summary>
        public int QuerySegment(EntityId segment, int depth, int[] result)
        {
            int n = AlongCount(segment);
            int count = 0;
            if (n == 0) return 0;
            int[] list = _segCells[segment.Index]!;
            for (int k = 0; k < list.Length && count < result.Length; k++)
            {
                if (list[k] < 0 || k % _maxDepth >= depth) continue;
                result[count++] = list[k];
            }
            Array.Sort(result, 0, count);
            return count;
        }

        private static bool PointInPolygon(Vec2 p, Vec2[] poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                Vec2 a = poly[i], b = poly[j];
                if ((a.Z > p.Z) != (b.Z > p.Z) && p.X < (b.X - a.X) * (p.Z - a.Z) / (b.Z - a.Z) + a.X) inside = !inside;
            }
            return inside;
        }

        // ------------------------------------------------------------------ pintar

        /// <summary>Normaliza la densidad a la que admite cada zona (R 1-3, C 1-2, I 1, O 2-3).</summary>
        public static byte NormalizeDensity(ZoneType zone, int density)
        {
            switch (zone)
            {
                case ZoneType.Residential: return (byte)Math.Max(1, Math.Min(3, density));
                case ZoneType.Commercial: return (byte)Math.Max(1, Math.Min(2, density));
                case ZoneType.Industrial: return 1;
                case ZoneType.Office: return (byte)Math.Max(2, Math.Min(3, density));
                default: return 0;
            }
        }

        /// <summary>Cambia la zona de una celda; devuelve true si cambio algo. La zona None borra.</summary>
        public bool SetZone(int cell, ZoneType zone, int density)
        {
            if (!IsCellAlive(cell)) return false;
            ref ZoneCell c = ref _cells[cell];
            byte d = NormalizeDensity(zone, density);
            if (c.Zone == zone && c.Density == d) return false;
            Count(ref c, -1);
            c.Zone = zone;
            c.Density = d;
            Count(ref c, +1);
            return true;
        }

        /// <summary>Marca la celda como ocupada o libre por un edificio (mantiene los contadores).</summary>
        public void SetBuilding(int cell, int building)
        {
            if (!IsCellAlive(cell)) return;
            ref ZoneCell c = ref _cells[cell];
            if (c.Building == building) return;
            Count(ref c, -1);
            c.Building = building;
            Count(ref c, +1);
        }

        private void Count(ref ZoneCell c, int sign)
        {
            if (c.Zone == ZoneType.None) return;
            _zoned[(int)c.Zone] += sign;
            if (c.Building < 0) _zonedFree[(int)c.Zone] += sign;
        }

        // ------------------------------------------------------------------ regeneracion tras cambios de red

        /// <summary>Edificios cuyas celdas se regeneraron en el ultimo <see cref="Refresh"/> (indices de edificio, sin repetir).</summary>
        public ReadOnlySpan<int> AffectedBuildings => new ReadOnlySpan<int>(_affected, 0, _affectedCount);

        /// <summary>Celda nueva que sustituye a una celda vieja del ultimo Refresh (o -1 si se perdio). Solo para celdas con edificio.</summary>
        public int RemapOf(int oldCell) => oldCell >= 0 && oldCell < _remap.Length ? _remap[oldCell] : -1;

        /// <summary>Regenera lo que cambio en la red desde la ultima llamada. True si hubo cambios.</summary>
        public bool Refresh()
        {
            _affectedCount = 0;
            if (!_net.HasPendingChanges(_sub)) return false;
            _net.ConsumeChanges(_sub, out _, out ReadOnlySpan<EntityId> segs, out _);
            EnsureSegCapacity(_net.SegmentIndexBound);
            NextStamp();
            for (int k = 0; k < segs.Length; k++)
            {
                int si = segs[k].Index;
                if (si >= _stamp.Length) continue;
                _stamp[si] = _curStamp;
                Aabb2 area = default;
                bool have = false;
                EntityId cur = _net.SegmentIdAt(si);
                if (cur.IsValid)
                {
                    area = Expand(_net.GetSegment(cur).Curve.Bounds, _bal.ZoneNeighborRadius);
                    have = true;
                }
                if (_segCells[si] != null)
                {
                    area = have ? Union(area, _segArea[si]) : _segArea[si];
                    have = true;
                }
                if (!have) continue;
                int n = _net.QuerySegments(area, _nbr);
                for (int q = 0; q < n; q++) if (_nbr[q].Index < _stamp.Length) _stamp[_nbr[q].Index] = _curStamp;
            }
            Regenerate();
            return true;
        }

        private void GenerateAll()
        {
            EnsureSegCapacity(_net.SegmentIndexBound);
            NextStamp();
            for (int i = 0; i < _net.SegmentIndexBound; i++) if (_net.SegmentIdAt(i).IsValid) _stamp[i] = _curStamp;
            _net.ConsumeChanges(_sub, out _, out _, out _);
            Regenerate();
        }

        private void Regenerate()
        {
            int bound = Math.Min(_net.SegmentIndexBound, _stamp.Length);
            // 1) Recoger y liberar las celdas viejas de los segmentos marcados.
            _oldCount = 0;
            int oldHigh = _high;
            for (int si = 0; si < _stamp.Length; si++)
            {
                if (_stamp[si] != _curStamp) continue;
                int[]? list = _segCells[si];
                if (list == null) continue;
                for (int k = 0; k < list.Length; k++)
                {
                    int c = list[k];
                    if (c < 0) continue;
                    if (_oldCount == _old.Length) Array.Resize(ref _old, _oldCount * 2);
                    ref ZoneCell zc = ref _cells[c];
                    _old[_oldCount++] = new OldCell { Pos = zc.Pos, Zone = zc.Zone, Density = zc.Density, Building = zc.Building, Index = c };
                    FreeCell(c);
                }
                _segCells[si] = null;
                _segAlong[si] = 0;
            }
            if (_remap.Length < oldHigh) _remap = new int[Math.Max(oldHigh, _remap.Length * 2)];
            for (int i = 0; i < oldHigh; i++) _remap[i] = -1;

            // 2) Generar las nuevas en orden de indice de segmento.
            _newCount = 0;
            for (int si = 0; si < bound; si++)
            {
                if (_stamp[si] != _curStamp) continue;
                EntityId id = _net.SegmentIdAt(si);
                if (id.IsValid) Generate(id);
            }

            // 3) Heredar zona por posicion y trasladar los edificios.
            for (int n = 0; n < _newCount; n++)
            {
                int ci = _new[n];
                ref ZoneCell nc = ref _cells[ci];
                int bestOld = -1;
                float bestD = _bal.ZoneInheritRadius * _bal.ZoneInheritRadius;
                for (int o = 0; o < _oldCount; o++)
                {
                    if (_old[o].Zone == ZoneType.None) continue;
                    float d = (_old[o].Pos - nc.Pos).LengthSquared;
                    if (d < bestD) { bestD = d; bestOld = o; }
                }
                if (bestOld >= 0)
                {
                    nc.Zone = _old[bestOld].Zone;
                    nc.Density = _old[bestOld].Density;
                }
            }
            if (_claimed.Length < _newCount) _claimed = new bool[Math.Max(_newCount, _claimed.Length * 2)];
            for (int n = 0; n < _newCount; n++) _claimed[n] = false;
            for (int o = 0; o < _oldCount; o++)
            {
                if (_old[o].Building < 0) continue;
                AddAffected(_old[o].Building);
                int bestN = -1;
                float bestD = _bal.ZoneInheritRadius * _bal.ZoneInheritRadius;
                for (int n = 0; n < _newCount; n++)
                {
                    if (_claimed[n]) continue;
                    float d = (_cells[_new[n]].Pos - _old[o].Pos).LengthSquared;
                    if (d < bestD) { bestD = d; bestN = n; }
                }
                if (bestN < 0) continue;
                _claimed[bestN] = true;
                _remap[_old[o].Index] = _new[bestN];
                _cells[_new[bestN]].Building = _old[o].Building;
            }
            // Contadores de las nuevas celdas.
            for (int n = 0; n < _newCount; n++) Count(ref _cells[_new[n]], +1);
        }

        private void AddAffected(int building)
        {
            for (int i = 0; i < _affectedCount; i++) if (_affected[i] == building) return;
            if (_affectedCount == _affected.Length) Array.Resize(ref _affected, _affectedCount * 2);
            _affected[_affectedCount++] = building;
        }

        private void Generate(EntityId id)
        {
            int si = id.Index;
            ref readonly Segment sg = ref _net.GetSegment(id);
            RoadType type = _net.Types.Get(sg.RoadTypeId);
            _segGen[si] = id.Generation;
            _segArea[si] = Expand(sg.Curve.Bounds, _bal.ZoneNeighborRadius);
            _segAlong[si] = 0;
            if (!type.Zonable) return;
            float usable = sg.Length - sg.SetbackA - sg.SetbackB;
            int n = (int)(usable / CellSize);
            if (n <= 0) return;
            if (n > 120) n = 120;
            float start = sg.SetbackA + (usable - n * CellSize) * 0.5f;
            float half = type.TotalWidth * 0.5f;
            Bezier3 curve = sg.Curve;
            var table = new ArcLengthTable(in curve, _arc, 0, ArcSamples);
            _nbrCount = _net.QuerySegments(_segArea[si], _nbr);
            var list = new int[2 * n * _maxDepth];
            for (int i = 0; i < list.Length; i++) list[i] = -1;
            for (int side = 0; side < 2; side++)
            {
                for (int a = 0; a < n; a++)
                {
                    float dist = start + CellHalf + a * CellSize;
                    Vec2 c = OffsetCurve.PointAtDistance(curve, table, dist, 0f, out Vec2 tangent);
                    Vec2 right = OffsetCurve.Right(tangent);
                    Vec2 normal = side == 0 ? right : -right;
                    for (int depth = 0; depth < _maxDepth; depth++)
                    {
                        Vec2 pos = c + normal * (half + CellHalf + depth * CellSize);
                        if (!CellFits(id, in sg, half, pos, -normal)) break;
                        int ci = AllocCell();
                        ref ZoneCell zc = ref _cells[ci];
                        zc.Pos = pos;
                        zc.Dir = -normal;
                        zc.Segment = id;
                        zc.Side = (byte)side;
                        zc.Along = (byte)a;
                        zc.Depth = (byte)depth;
                        zc.Zone = ZoneType.None;
                        zc.Density = 0;
                        zc.Building = -1;
                        BinAdd(ci);
                        list[(side * n + a) * _maxDepth + depth] = ci;
                        if (_newCount == _new.Length) Array.Resize(ref _new, _newCount * 2);
                        _new[_newCount++] = ci;
                    }
                }
            }
            _segCells[si] = list;
            _segAlong[si] = n;
        }

        private bool CellFits(EntityId self, in Segment sg, float halfSelf, Vec2 pos, Vec2 dir)
        {
            float lim = _bal.MapHalfSize - CellHalf;
            if (Math.Abs(pos.X) > lim || Math.Abs(pos.Z) > lim) return false;
            // Su propia calzada (curvas cerradas): el centro de la celda debe quedar claramente fuera.
            float t = sg.Curve.ClosestT(pos);
            if (Vec2.Distance(sg.Curve.Evaluate(t), pos) < halfSelf + 2f) return false;
            for (int q = 0; q < _nbrCount; q++)
            {
                EntityId nb = _nbr[q];
                if (nb == self || !_net.IsSegmentAlive(nb)) continue;
                ref readonly Segment ns = ref _net.GetSegment(nb);
                if (IsContinuation(in sg, in ns)) continue;
                float halfOther = _net.Types.Get(ns.RoadTypeId).TotalWidth * 0.5f;
                float nt = ns.Curve.ClosestT(pos);
                if (Vec2.Distance(ns.Curve.Evaluate(nt), pos) < halfOther + CellHalf - 0.01f) return false;
            }
            // Solape con otra celda ya existente (SAT entre cuadrados orientados).
            Vec2 u = dir, v = dir.Perp;
            int bx = BinX(pos.X), bz = BinZ(pos.Z);
            for (int z = bz - 1; z <= bz + 1; z++)
            {
                if (z < 0 || z >= _binsZ) continue;
                for (int x = bx - 1; x <= bx + 1; x++)
                {
                    if (x < 0 || x >= _binsX) continue;
                    for (int c = _binHead[z * _binsX + x]; c >= 0; c = _binNext[c])
                        if (SquaresOverlap(pos, u, v, _cells[c].Pos, _cells[c].Dir, _cells[c].Dir.Perp)) return false;
                }
            }
            return true;
        }

        /// <summary>Dos segmentos que se unen en un nodo de solo dos segmentos son la misma carretera (no hay cruce entre ellos).</summary>
        private bool IsContinuation(in Segment a, in Segment b)
        {
            EntityId shared = EntityId.None;
            if (a.NodeA == b.NodeA || a.NodeA == b.NodeB) shared = a.NodeA;
            else if (a.NodeB == b.NodeA || a.NodeB == b.NodeB) shared = a.NodeB;
            return shared.IsValid && _net.GetNode(shared).SegmentCount == 2;
        }

        private static bool SquaresOverlap(Vec2 ca, Vec2 ua, Vec2 va, Vec2 cb, Vec2 ub, Vec2 vb)
        {
            Vec2 d = cb - ca;
            if (d.LengthSquared > 8f * 8f * 2f) return false;
            return !Separated(d, ua, ua, va, ub, vb) && !Separated(d, va, ua, va, ub, vb)
                && !Separated(d, ub, ua, va, ub, vb) && !Separated(d, vb, ua, va, ub, vb);
        }

        private static bool Separated(Vec2 d, Vec2 axis, Vec2 ua, Vec2 va, Vec2 ub, Vec2 vb)
        {
            float ra = Shrink * (Math.Abs(Vec2.Dot(ua, axis)) + Math.Abs(Vec2.Dot(va, axis)));
            float rb = Shrink * (Math.Abs(Vec2.Dot(ub, axis)) + Math.Abs(Vec2.Dot(vb, axis)));
            return Math.Abs(Vec2.Dot(d, axis)) >= ra + rb;
        }

        // ------------------------------------------------------------------ almacenamiento

        private int AllocCell()
        {
            int ci;
            if (_freeCount > 0) ci = _free[--_freeCount];
            else
            {
                ci = _high++;
                if (ci >= _cells.Length)
                {
                    Array.Resize(ref _cells, _cells.Length * 2);
                    Array.Resize(ref _binNext, _cells.Length);
                }
            }
            _alive++;
            return ci;
        }

        private void FreeCell(int ci)
        {
            ref ZoneCell c = ref _cells[ci];
            Count(ref c, -1);
            BinRemove(ci);
            c = default;
            c.Segment = EntityId.None;
            c.Building = -1;
            _alive--;
            if (_freeCount == _free.Length) Array.Resize(ref _free, _freeCount * 2);
            _free[_freeCount++] = ci;
        }

        private int BinX(float x) => Math.Max(0, Math.Min(_binsX - 1, (int)Math.Floor((x - _min.X) / CellSize)));
        private int BinZ(float z) => Math.Max(0, Math.Min(_binsZ - 1, (int)Math.Floor((z - _min.Z) / CellSize)));

        private void BinAdd(int ci)
        {
            int b = BinZ(_cells[ci].Pos.Z) * _binsX + BinX(_cells[ci].Pos.X);
            _binNext[ci] = _binHead[b];
            _binHead[b] = ci;
        }

        private void BinRemove(int ci)
        {
            int b = BinZ(_cells[ci].Pos.Z) * _binsX + BinX(_cells[ci].Pos.X);
            int prev = -1;
            for (int c = _binHead[b]; c >= 0; prev = c, c = _binNext[c])
            {
                if (c != ci) continue;
                if (prev < 0) _binHead[b] = _binNext[c]; else _binNext[prev] = _binNext[c];
                return;
            }
        }

        private void EnsureSegCapacity(int bound)
        {
            if (bound <= _segCells.Length) return;
            int cap = _segCells.Length;
            while (cap < bound) cap *= 2;
            Array.Resize(ref _segCells, cap);
            Array.Resize(ref _segAlong, cap);
            Array.Resize(ref _segGen, cap);
            Array.Resize(ref _segArea, cap);
            Array.Resize(ref _stamp, cap);
        }

        private void NextStamp()
        {
            if (++_curStamp == int.MaxValue)
            {
                Array.Clear(_stamp, 0, _stamp.Length);
                _curStamp = 1;
            }
        }

        private static Aabb2 Expand(Aabb2 b, float r) => new Aabb2(new Vec2(b.Min.X - r, b.Min.Z - r), new Vec2(b.Max.X + r, b.Max.Z + r));

        private static Aabb2 Union(Aabb2 a, Aabb2 b) =>
            new Aabb2(new Vec2(Math.Min(a.Min.X, b.Min.X), Math.Min(a.Min.Z, b.Min.Z)), new Vec2(Math.Max(a.Max.X, b.Max.X), Math.Max(a.Max.Z, b.Max.Z)));

        // ------------------------------------------------------------------ hash

        public ulong ComputeHash()
        {
            ulong h = HashUtil.Mix(0x5A4F4E45UL, (ulong)_alive);
            // Por posicion y no por indice: el hash no depende del orden de asignacion de huecos.
            ulong sum = 0;
            for (int i = 0; i < _high; i++)
            {
                ref readonly ZoneCell c = ref _cells[i];
                if (!c.Segment.IsValid) continue;
                ulong x = HashUtil.MixF(HashUtil.MixF(0x43454C4CUL, c.Pos.X), c.Pos.Z);
                x = HashUtil.Mix(x, (ulong)c.Zone | ((ulong)c.Density << 8) | ((ulong)(uint)(c.Building + 1) << 16));
                unchecked { sum += x; }
            }
            return HashUtil.Mix(h, sum);
        }
    }
}
