using System;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Sim.Junctions
{
    /// <summary>
    /// Construccion estatica de un cruce (ADR 004): mascaras y zonas de conflicto, control, via principal, rangos y fases.
    /// Solo se usa al reconstruir un nodo (ediciones); los buffers de trabajo son de la instancia y se reutilizan.
    /// </summary>
    public sealed class JunctionBuilder
    {
        private const int MaxConn = RoadNetwork.MaxConnectionsPerNode;
        private const int MaxSamples = 64;
        private const int TableSamples = 16;
        private const sbyte KindNone = -1;

        private readonly JunctionConfig _cfg;
        private readonly Vec2[] _pts = new Vec2[MaxConn * MaxSamples];
        private readonly float[] _sArc = new float[MaxConn * MaxSamples];
        private readonly float[] _tbl = new float[MaxConn * TableSamples];
        private readonly int[] _cnt = new int[MaxConn];
        private readonly float[] _len = new float[MaxConn];
        private readonly float[] _minX = new float[MaxConn], _minZ = new float[MaxConn], _maxX = new float[MaxConn], _maxZ = new float[MaxConn];
        private readonly byte[] _slot = new byte[MaxConn];
        private readonly byte[] _toSlot = new byte[MaxConn];
        private readonly sbyte[] _kind = new sbyte[MaxConn * MaxConn];
        private readonly float[] _zs = new float[MaxConn * MaxConn];
        private readonly float[] _ze = new float[MaxConn * MaxConn];
        private readonly bool[] _markI = new bool[MaxSamples];
        private readonly bool[] _markJ = new bool[MaxSamples];
        private readonly CurveHit[] _hits = new CurveHit[8];
        private readonly byte[] _entrySlots = new byte[RoadNetwork.MaxSegmentsPerNode];

        public JunctionBuilder(JunctionConfig config)
        {
            _cfg = config ?? throw new ArgumentNullException(nameof(config));
        }

        public JunctionConfig Config => _cfg;

        /// <summary>Jerarquia de un tipo de via: SpeedLimit_kmh + 10 (F + B).</summary>
        public static int Hierarchy(RoadType type)
        {
            return (int)Math.Round(type.SpeedLimit * 3.6f) + 10 * (type.LanesForward + type.LanesBackward);
        }

        /// <summary>Control efectivo de un nodo segun su ControlId (0 = auto) y su forma.</summary>
        public static ControlKind ResolveControl(RoadNetwork net, EntityId node)
        {
            ushort id = net.GetNode(node).ControlId;
            if (id == JunctionControlIds.Priority) return ControlKind.Priority;
            if (id == JunctionControlIds.Stop) return ControlKind.Stop;
            if (id == JunctionControlIds.Signal) return ControlKind.Signal;
            ReadOnlySpan<EntityId> segs = net.GetNodeSegments(node);
            int entries = 0, wide = 0;
            for (int s = 0; s < segs.Length; s++)
            {
                ref readonly Segment sg = ref net.GetSegment(segs[s]);
                bool atA = sg.NodeA.Index == node.Index;
                if ((atA ? sg.LanesBackward : sg.LanesForward) > 0) entries++;
                if (sg.LanesForward + sg.LanesBackward >= 4) wide++;
            }
            return (entries == 3 || entries == 4) && wide >= 3 ? ControlKind.Signal : ControlKind.Priority;
        }

        /// <summary>Construye (o rehace sobre <paramref name="reuse"/>) los datos del cruce. El estado dinamico queda a cero.</summary>
        public JunctionNode Build(RoadNetwork net, EntityId node, JunctionNode? reuse = null)
        {
            JunctionNode jn = reuse ?? new JunctionNode();
            ReadOnlySpan<LaneConnection> conns = net.GetConnectionsAt(node);
            ReadOnlySpan<EntityId> segs = net.GetNodeSegments(node);
            int n = conns.Length;
            jn.Node = node;
            jn.ConnCount = n;
            jn.SegmentCount = (byte)segs.Length;
            if (jn.Gates.Length < n) jn.Gates = new ConnGate[n];

            for (int i = 0; i < n; i++)
            {
                _slot[i] = SlotOf(segs, conns[i].From.SegmentIndex);
                _toSlot[i] = SlotOf(segs, conns[i].To.SegmentIndex);
                Sample(i, conns[i]);
            }

            ComputePairs(conns, n);
            FillEntries(jn, n);

            jn.Control = ResolveControl(net, node);
            ChooseMajor(net, jn, conns, segs, n);
            AssignRules(jn, conns, n);
            BuildPhases(net, jn, conns, segs, n);
            jn.ResetDynamic();
            return jn;
        }

        private static byte SlotOf(ReadOnlySpan<EntityId> segs, int segIndex)
        {
            for (int s = 0; s < segs.Length; s++)
                if (segs[s].Index == segIndex) return (byte)s;
            return JunctionNode.NoSlot;
        }

        // ------------------------------------------------------------------ muestreo

        /// <summary>
        /// Muestrea la conexion uniformemente en t con al menos una muestra por <see cref="JunctionConfig.ZoneSampleStep"/> de arco y
        /// las suficientes para que la poligonal no se separe de la curva mas de <see cref="JunctionConfig.ChordTolerance"/>
        /// (cota (1/8) dt^2 max|B''|), hasta 64. Guarda la posicion y el arco (sobre la longitud de la red) de cada muestra.
        /// </summary>
        private void Sample(int i, in LaneConnection c)
        {
            Span<float> tbl = new Span<float>(_tbl, i * TableSamples, TableSamples);
            ArcLengthTable.Build(c.Curve, tbl);
            float total = tbl[TableSamples - 1];
            float len = c.Length;
            _len[i] = len;
            int baseIdx = i * MaxSamples;
            int m;
            if (len < 1e-3f || total < 1e-6f)
            {
                m = 1;
                _pts[baseIdx] = c.Curve.P0;
                _sArc[baseIdx] = 0f;
            }
            else
            {
                Bezier3 b = c.Curve;
                float d2 = 6f * Math.Max((b.P0 - b.P1 * 2f + b.P2).Length, (b.P1 - b.P2 * 2f + b.P3).Length);
                double tol = _cfg.ChordTolerance > 1e-4f ? _cfg.ChordTolerance : 1e-4f;
                int byCurv = (int)Math.Ceiling(Math.Sqrt(d2 / (8.0 * tol))) + 1;
                int byArc = (int)Math.Ceiling(len / _cfg.ZoneSampleStep) + 1;
                int max = Math.Min(MaxSamples, Math.Max(2, _cfg.MaxZoneSamples));
                m = Math.Max(byCurv, byArc);
                if (m < 2) m = 2;
                if (m > max) m = max;
                float scale = len / total;
                for (int q = 0; q < m; q++)
                {
                    float t = (float)q / (m - 1);
                    _pts[baseIdx + q] = b.Evaluate(t);
                    _sArc[baseIdx + q] = q == m - 1 ? len : ArcLengthTable.DistanceAtT(b, tbl, t) * scale;
                }
            }
            _cnt[i] = m;
            float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
            for (int q = 0; q < m; q++)
            {
                Vec2 p = _pts[baseIdx + q];
                if (p.X < x0) x0 = p.X;
                if (p.Z < z0) z0 = p.Z;
                if (p.X > x1) x1 = p.X;
                if (p.Z > z1) z1 = p.Z;
            }
            _minX[i] = x0; _minZ[i] = z0; _maxX[i] = x1; _maxZ[i] = z1;
        }

        /// <summary>Arco (sobre la longitud de la red) de la muestra q de la conexion i.</summary>
        private float SampleS(int i, int q) => _sArc[i * MaxSamples + q];

        // ------------------------------------------------------------------ pares

        private void ComputePairs(ReadOnlySpan<LaneConnection> conns, int n)
        {
            float cd = _cfg.ConflictDistance;
            for (int i = 0; i < n; i++)
            {
                _kind[i * MaxConn + i] = KindNone;
                for (int j = i + 1; j < n; j++)
                {
                    _kind[i * MaxConn + j] = KindNone;
                    _kind[j * MaxConn + i] = KindNone;
                    if (conns[i].From == conns[j].From) continue;
                    bool merge = conns[i].To == conns[j].To;
                    bool near = _minX[i] - cd <= _maxX[j] && _minX[j] - cd <= _maxX[i] && _minZ[i] - cd <= _maxZ[j] && _minZ[j] - cd <= _maxZ[i];
                    if (!near && !merge) continue;

                    int mi = _cnt[i], mj = _cnt[j];
                    for (int q = 0; q < mi; q++) _markI[q] = false;
                    for (int q = 0; q < mj; q++) _markJ[q] = false;
                    bool any = false;
                    int si = mi > 1 ? mi - 1 : 1;
                    int sj = mj > 1 ? mj - 1 : 1;
                    for (int a = 0; a < si; a++)
                    {
                        Vec2 a0 = _pts[i * MaxSamples + a];
                        Vec2 a1 = mi > 1 ? _pts[i * MaxSamples + a + 1] : a0;
                        for (int b = 0; b < sj; b++)
                        {
                            Vec2 b0 = _pts[j * MaxSamples + b];
                            Vec2 b1 = mj > 1 ? _pts[j * MaxSamples + b + 1] : b0;
                            if (SegmentDistance(a0, a1, b0, b1) >= cd) continue;
                            any = true;
                            _markI[a] = true;
                            if (mi > 1) _markI[a + 1] = true;
                            _markJ[b] = true;
                            if (mj > 1) _markJ[b + 1] = true;
                        }
                    }

                    int hits = 0;
                    if (!merge && _len[i] >= 1e-3f && _len[j] >= 1e-3f)
                        hits = Intersections.CurveCurve(conns[i].Curve, conns[j].Curve, _hits);
                    if (!merge && !any && hits == 0) continue;

                    sbyte kind = merge ? (sbyte)ConflictKind.Merge : (sbyte)ConflictKind.Cross;
                    _kind[i * MaxConn + j] = kind;
                    _kind[j * MaxConn + i] = kind;
                    Zone(i, _markI, merge, conns[i].Curve, hits, true, out _zs[i * MaxConn + j], out _ze[i * MaxConn + j]);
                    Zone(j, _markJ, merge, conns[j].Curve, hits, false, out _zs[j * MaxConn + i], out _ze[j * MaxConn + i]);
                }
            }
        }

        private void Zone(int i, bool[] marks, bool merge, in Bezier3 curve, int hits, bool hitIsA, out float start, out float end)
        {
            float len = _len[i];
            int m = _cnt[i];
            int first = -1, last = -1;
            for (int q = 0; q < m; q++)
            {
                if (!marks[q]) continue;
                if (first < 0) first = q;
                last = q;
            }
            if (first < 0)
            {
                start = merge ? len : 0f;
                end = len;
            }
            else
            {
                start = SampleS(i, first) - _cfg.ZonePad;
                end = SampleS(i, last) + _cfg.ZonePad;
            }
            if (hits > 0 && len >= 1e-3f)
            {
                ReadOnlySpan<float> tbl = new ReadOnlySpan<float>(_tbl, i * TableSamples, TableSamples);
                float total = tbl[TableSamples - 1];
                float scale = total > 1e-6f ? len / total : 0f;
                for (int h = 0; h < hits; h++)
                {
                    float t = hitIsA ? _hits[h].TA : _hits[h].TB;
                    float s = ArcLengthTable.DistanceAtT(curve, tbl, t) * scale;
                    if (s < start) start = s;
                    if (s > end) end = s;
                }
            }
            if (merge) end = len;
            if (start < 0f) start = 0f;
            if (end > len) end = len;
            if (start > end) start = end;
        }

        /// <summary>Distancia minima entre los segmentos a0-a1 y b0-b1 (admite segmentos degenerados).</summary>
        public static float SegmentDistance(Vec2 a0, Vec2 a1, Vec2 b0, Vec2 b1)
        {
            if (Intersections.SegmentSegment(a0, a1, b0, b1, out _, out _)) return 0f;
            float d = PointSegment(a0, b0, b1);
            float e = PointSegment(a1, b0, b1);
            if (e < d) d = e;
            e = PointSegment(b0, a0, a1);
            if (e < d) d = e;
            e = PointSegment(b1, a0, a1);
            if (e < d) d = e;
            return d;
        }

        public static float PointSegment(Vec2 p, Vec2 a, Vec2 b)
        {
            Vec2 ab = b - a;
            float l2 = ab.LengthSquared;
            if (l2 < 1e-12f) return Vec2.Distance(p, a);
            float t = Vec2.Dot(p - a, ab) / l2;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            return Vec2.Distance(p, a + ab * t);
        }

        private void FillEntries(JunctionNode jn, int n)
        {
            int total = 0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    if (_kind[i * MaxConn + j] != KindNone) total++;
            if (jn.Entries.Length < total) jn.Entries = new ConflictEntry[total];
            jn.EntryCount = total;
            int pos = 0;
            for (int i = 0; i < n; i++)
            {
                ref ConnGate g = ref jn.Gates[i];
                g = default;
                g.ConflictFirst = pos;
                ulong mask = 0;
                for (int j = 0; j < n; j++)
                {
                    sbyte k = _kind[i * MaxConn + j];
                    if (k == KindNone) continue;
                    mask |= 1UL << j;
                    jn.Entries[pos++] = new ConflictEntry
                    {
                        Other = (byte)j,
                        Kind = (ConflictKind)k,
                        StartDm = DmDown(_zs[i * MaxConn + j]),
                        EndDm = DmUp(_ze[i * MaxConn + j]),
                        OtherEndDm = DmUp(_ze[j * MaxConn + i]),
                    };
                }
                g.ConflictMask = mask;
                g.ConflictCount = (byte)(pos - g.ConflictFirst);
            }
        }

        private static ushort DmDown(float m)
        {
            double v = Math.Floor(m * 10.0 + 1e-4);
            return (ushort)(v < 0 ? 0 : (v > ushort.MaxValue ? ushort.MaxValue : v));
        }

        private static ushort DmUp(float m)
        {
            double v = Math.Ceiling(m * 10.0 - 1e-4);
            return (ushort)(v < 0 ? 0 : (v > ushort.MaxValue ? ushort.MaxValue : v));
        }

        // ------------------------------------------------------------------ via principal, reglas y rangos

        private static Vec2 OutTangent(RoadNetwork net, EntityId seg, int nodeIndex)
        {
            ref readonly Segment s = ref net.GetSegment(seg);
            return s.NodeA.Index == nodeIndex ? s.Curve.Tangent(0f) : -s.Curve.Tangent(1f);
        }

        private static bool HasStraight(ReadOnlySpan<LaneConnection> conns, byte[] slot, byte[] toSlot, int n, int a, int b)
        {
            for (int i = 0; i < n; i++)
            {
                if (conns[i].Turn != TurnType.Straight) continue;
                if ((slot[i] == a && toSlot[i] == b) || (slot[i] == b && toSlot[i] == a)) return true;
            }
            return false;
        }

        private void ChooseMajor(RoadNetwork net, JunctionNode jn, ReadOnlySpan<LaneConnection> conns, ReadOnlySpan<EntityId> segs, int n)
        {
            jn.MajorA = JunctionNode.NoSlot;
            jn.MajorB = JunctionNode.NoSlot;
            int cnt = segs.Length;
            if (cnt < 3) return; // nodos de 1 o 2 segmentos: todos principales
            int bestA = -1, bestB = -1, bestMin = 0, bestMax = 0;
            float bestDot = 0f;
            for (int a = 0; a < cnt; a++)
            {
                int ha = Hierarchy(net.Types.Get(net.GetSegment(segs[a]).RoadTypeId));
                Vec2 ta = OutTangent(net, segs[a], jn.Node.Index);
                for (int b = a + 1; b < cnt; b++)
                {
                    if (!HasStraight(conns, _slot, _toSlot, n, a, b)) continue;
                    int hb = Hierarchy(net.Types.Get(net.GetSegment(segs[b]).RoadTypeId));
                    int mn = Math.Min(ha, hb), mx = Math.Max(ha, hb);
                    float dot = Vec2.Dot(ta, OutTangent(net, segs[b], jn.Node.Index));
                    bool better;
                    if (bestA < 0) better = true;
                    else if (mn != bestMin) better = mn > bestMin;
                    else if (mx != bestMax) better = mx > bestMax;
                    else if (dot != bestDot) better = dot < bestDot; // mas opuestos = |180 - angulo| menor
                    else better = false; // a y b crecientes: gana la menor ranura
                    if (!better) continue;
                    bestA = a; bestB = b; bestMin = mn; bestMax = mx; bestDot = dot;
                }
            }
            if (bestA < 0) return;
            jn.MajorA = (byte)bestA;
            jn.MajorB = (byte)bestB;
        }

        private static byte RankOf(bool major, TurnType turn)
        {
            if (major)
            {
                switch (turn)
                {
                    case TurnType.Straight: return 6;
                    case TurnType.Right: return 6;
                    case TurnType.Left: return 5;
                    default: return 4;
                }
            }
            switch (turn)
            {
                case TurnType.Straight: return 2;
                case TurnType.Right: return 3;
                case TurnType.Left: return 1;
                default: return 0;
            }
        }

        private void AssignRules(JunctionNode jn, ReadOnlySpan<LaneConnection> conns, int n)
        {
            for (int i = 0; i < n; i++)
            {
                ref ConnGate g = ref jn.Gates[i];
                g.Approach = _slot[i];
                g.Turn = conns[i].Turn;
                bool major = jn.IsMajorApproach(_slot[i]);
                if (jn.Control == ControlKind.Signal)
                {
                    g.Rule = ApproachRule.Signal;
                    g.Rank = RankOf(true, g.Turn);
                }
                else
                {
                    g.Rule = major ? ApproachRule.Free : (jn.Control == ControlKind.Stop ? ApproachRule.Stop : ApproachRule.Yield);
                    g.Rank = RankOf(major, g.Turn);
                }
            }
        }

        // ------------------------------------------------------------------ fases

        private void BuildPhases(RoadNetwork net, JunctionNode jn, ReadOnlySpan<LaneConnection> conns, ReadOnlySpan<EntityId> segs, int n)
        {
            jn.PhaseCount = 0;
            jn.CycleTicks = 0;
            for (int i = 0; i < n; i++) jn.Gates[i].PhaseBits = 0;
            if (jn.Control != ControlKind.Signal) return;

            // Accesos de entrada en orden CCW (ranuras ascendentes).
            int entryCount = 0;
            for (int s = 0; s < segs.Length; s++)
            {
                ref readonly Segment sg = ref net.GetSegment(segs[s]);
                bool atA = sg.NodeA.Index == jn.Node.Index;
                if ((atA ? sg.LanesBackward : sg.LanesForward) > 0) _entrySlots[entryCount++] = (byte)s;
            }
            if (entryCount == 0) return;

            Span<byte> phaseOf = stackalloc byte[RoadNetwork.MaxSegmentsPerNode];
            for (int s = 0; s < phaseOf.Length; s++) phaseOf[s] = JunctionNode.NoSlot;
            int phases = 0;
            if (jn.MajorA != JunctionNode.NoSlot)
            {
                phaseOf[jn.MajorA] = 0;
                phaseOf[jn.MajorB] = 0;
                phases = 1;
                int restA = -1, restB = -1, rest = 0;
                for (int e = 0; e < entryCount; e++)
                {
                    int s = _entrySlots[e];
                    if (phaseOf[s] != JunctionNode.NoSlot) continue;
                    if (rest == 0) restA = s; else restB = s;
                    rest++;
                }
                if (rest == 2 && HasStraight(conns, _slot, _toSlot, n, restA, restB))
                {
                    phaseOf[restA] = 1;
                    phaseOf[restB] = 1;
                    phases = 2;
                }
                else
                {
                    for (int e = 0; e < entryCount && phases < JunctionNode.MaxPhases; e++)
                    {
                        int s = _entrySlots[e];
                        if (phaseOf[s] != JunctionNode.NoSlot) continue;
                        phaseOf[s] = (byte)phases++;
                    }
                }
            }
            else
            {
                for (int e = 0; e < entryCount && phases < JunctionNode.MaxPhases; e++) phaseOf[_entrySlots[e]] = (byte)phases++;
            }

            jn.PhaseCount = (byte)phases;
            int cycle = 0;
            for (int p = 0; p < phases; p++)
            {
                ushort g = p == 0 ? _cfg.GreenMajor : _cfg.GreenOther;
                jn.PhaseGreen[p] = g;
                cycle += g + _cfg.Amber + _cfg.AllRed;
            }
            jn.CycleTicks = cycle;
            for (int i = 0; i < n; i++)
            {
                byte p = phaseOf[_slot[i]];
                if (p != JunctionNode.NoSlot) jn.Gates[i].PhaseBits = (byte)(1 << p);
            }
        }
    }
}
