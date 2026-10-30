using System;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;

namespace Urbe.Sim.Roads
{
    public sealed partial class RoadNetwork
    {
        private const float Cos30 = 0.8660254037844387f;
        private const float Cos150 = -0.8660254037844387f;

        // Clases de salida respecto a una entrada.
        private const int ClsNone = 0, ClsStraight = 1, ClsRight = 2, ClsLeft = 3;

        // Scratch de generacion (por posicion de adyacencia del nodo).
        private readonly bool[] _gAtA = new bool[MaxSegmentsPerNode];
        private readonly int[] _gSeg = new int[MaxSegmentsPerNode];
        private readonly int[] _gEntry = new int[MaxSegmentsPerNode];
        private readonly int[] _gExit = new int[MaxSegmentsPerNode];
        private readonly int[] _gCls = new int[MaxSegmentsPerNode];
        private readonly Vec2[] _gOut = new Vec2[MaxSegmentsPerNode];

        private readonly LaneConnection[] _genA = new LaneConnection[MaxConnectionsPerNode];
        private readonly LaneConnection[] _genB = new LaneConnection[MaxConnectionsPerNode];

        // ------------------------------------------------------------------ adyacencia

        private static int HalfOf(Vec2 v) => (v.Z > 0f || (v.Z == 0f && v.X > 0f)) ? 0 : 1;

        /// <summary>Orden CCW desde +X sin trigonometria: negativo si a va antes que b.</summary>
        private static int CompareAngle(Vec2 a, Vec2 b)
        {
            int ha = HalfOf(a), hb = HalfOf(b);
            if (ha != hb) return ha - hb;
            float c = Vec2.Cross(a, b);
            return c > 0f ? -1 : (c < 0f ? 1 : 0);
        }

        private Vec2 OutTangent(EntityId seg, int nodeIndex)
        {
            ref readonly Segment s = ref _segments[seg.Index];
            return s.NodeA.Index == nodeIndex ? s.Curve.Tangent(0f) : -s.Curve.Tangent(1f);
        }

        private void AdjInsert(int ni, EntityId seg)
        {
            int baseIdx = ni * MaxSegmentsPerNode;
            int count = _nodes[ni].SegmentCount;
            Vec2 tan = OutTangent(seg, ni);
            int pos = 0;
            while (pos < count)
            {
                EntityId other = _nodeSegments[baseIdx + pos];
                int c = CompareAngle(OutTangent(other, ni), tan);
                if (c > 0 || (c == 0 && other.Index > seg.Index)) break;
                pos++;
            }
            for (int k = count; k > pos; k--) _nodeSegments[baseIdx + k] = _nodeSegments[baseIdx + k - 1];
            _nodeSegments[baseIdx + pos] = seg;
            _nodes[ni].SegmentCount = (byte)(count + 1);
        }

        private void AdjRemove(int ni, EntityId seg)
        {
            int baseIdx = ni * MaxSegmentsPerNode;
            int count = _nodes[ni].SegmentCount;
            for (int i = 0; i < count; i++)
            {
                if (_nodeSegments[baseIdx + i] != seg) continue;
                for (int k = i + 1; k < count; k++) _nodeSegments[baseIdx + k - 1] = _nodeSegments[baseIdx + k];
                _nodes[ni].SegmentCount = (byte)(count - 1);
                return;
            }
        }

        private void RefreshKind(int ni)
        {
            int c = _nodes[ni].SegmentCount;
            _nodes[ni].Kind = c == 0 ? JunctionKind.None : (c == 1 ? JunctionKind.DeadEnd : (c == 2 ? JunctionKind.Continuation : JunctionKind.Junction));
        }

        /// <summary>Segmento que une a y b (cualquier sentido); None si no existe.</summary>
        private EntityId FindSegmentBetween(EntityId a, EntityId b)
        {
            int baseIdx = a.Index * MaxSegmentsPerNode;
            int count = _nodes[a.Index].SegmentCount;
            for (int i = 0; i < count; i++)
            {
                EntityId sid = _nodeSegments[baseIdx + i];
                ref readonly Segment s = ref _segments[sid.Index];
                if (s.NodeA.Index == b.Index || s.NodeB.Index == b.Index) return sid;
            }
            return EntityId.None;
        }

        // ------------------------------------------------------------------ generacion

        private static void LaneEndpoint(in Bezier3 curve, in Lane lane, float t, out Vec2 pos, out Vec2 travel)
        {
            Vec2 tan = curve.Tangent(t);
            pos = curve.Evaluate(t) + OffsetCurve.Right(tan) * lane.Offset;
            travel = lane.Direction == LaneDirection.Forward ? tan : -tan;
        }

        private void Emit(LaneConnection[] dst, ref int n, int node, int ei, int eIdx, int xj, int xIdx, TurnType turn, ref bool overflow)
        {
            if (n >= MaxConnectionsPerNode)
            {
                overflow = true;
                return;
            }
            int es = _gSeg[ei];
            int xs = _gSeg[xj];
            ref readonly Segment esg = ref _segments[es];
            ref readonly Segment xsg = ref _segments[xs];
            int eSlot = _gAtA[ei] ? esg.LanesForward + eIdx : eIdx;
            int xSlot = _gAtA[xj] ? xIdx : xsg.LanesForward + xIdx;
            ref readonly Lane el = ref _lanes[es * MaxLanesPerSegment + eSlot];
            ref readonly Lane xl = ref _lanes[xs * MaxLanesPerSegment + xSlot];

            LaneEndpoint(esg.Curve, el, _gAtA[ei] ? 0f : 1f, out Vec2 p0, out Vec2 t0);
            LaneEndpoint(xsg.Curve, xl, _gAtA[xj] ? 0f : 1f, out Vec2 p3, out Vec2 t3);
            float h = 0.4f * Vec2.Distance(p0, p3);
            var curve = new Bezier3(p0, p0 + t0 * h, p3 - t3 * h, p3);

            float f;
            switch (turn)
            {
                case TurnType.Straight: f = 1f; break;
                case TurnType.Left: f = 0.5f; break;
                case TurnType.Right: f = 0.35f; break;
                default: f = 0.25f; break;
            }
            float speed = Math.Max(4f, Math.Min(el.SpeedLimit, xl.SpeedLimit) * f);
            dst[n++] = new LaneConnection(new LaneId(es, eSlot), new LaneId(xs, xSlot), _nodes[node].Id, curve, 0f, speed, turn, 0);
        }

        /// <summary>
        /// Genera las conexiones del nodo en <paramref name="dst"/> (ordenadas por (From, To), con LocalIndex).
        /// Se detiene en 64 y avisa por <paramref name="overflow"/>.
        /// </summary>
        private int GenerateNode(int ni, LaneConnection[] dst, out bool overflow)
        {
            overflow = false;
            int cnt = _nodes[ni].SegmentCount;
            if (cnt == 0) return 0;
            int adj = ni * MaxSegmentsPerNode;
            for (int i = 0; i < cnt; i++)
            {
                int si = _nodeSegments[adj + i].Index;
                ref readonly Segment sg = ref _segments[si];
                bool atA = sg.NodeA.Index == ni;
                _gSeg[i] = si;
                _gAtA[i] = atA;
                _gOut[i] = atA ? sg.Curve.Tangent(0f) : -sg.Curve.Tangent(1f);
                _gEntry[i] = atA ? sg.LanesBackward : sg.LanesForward;
                _gExit[i] = atA ? sg.LanesForward : sg.LanesBackward;
            }

            int n = 0;
            if (cnt == 1)
            {
                // Fin de via: cambio de sentido del carril de entrada mas a la izquierda al de salida mas a la izquierda.
                if (_gEntry[0] > 0 && _gExit[0] > 0)
                    Emit(dst, ref n, ni, 0, _gEntry[0] - 1, 0, _gExit[0] - 1, TurnType.UTurn, ref overflow);
            }
            else
            {
                for (int i = 0; i < cnt; i++)
                {
                    int entries = _gEntry[i];
                    if (entries == 0) continue;
                    GenerateFromEntry(dst, ref n, ni, cnt, i, entries, ref overflow);
                }
            }

            // Orden (From, To) por insercion (n <= 64) y LocalIndex.
            for (int a = 1; a < n; a++)
            {
                LaneConnection key = dst[a];
                int b = a - 1;
                while (b >= 0 && (dst[b].From.Value > key.From.Value
                    || (dst[b].From.Value == key.From.Value && dst[b].To.Value > key.To.Value)))
                {
                    dst[b + 1] = dst[b];
                    b--;
                }
                dst[b + 1] = key;
            }
            for (int a = 0; a < n; a++) dst[a].LocalIndex = (byte)a;
            return n;
        }

        private void GenerateFromEntry(LaneConnection[] dst, ref int n, int ni, int cnt, int i, int entries, ref bool overflow)
        {
            Vec2 din = -_gOut[i];

            // Unica salida Recta: la de menor |theta| dentro de 30 grados (con 2 segmentos, la otra siempre).
            int straight = -1;
            float bestDot = -2f;
            for (int j = 0; j < cnt; j++)
            {
                _gCls[j] = ClsNone;
                if (j == i || _gExit[j] == 0) continue;
                float d = Vec2.Dot(din, _gOut[j]);
                if (cnt == 2) straight = j;
                else if (d >= Cos30 && d > bestDot)
                {
                    bestDot = d;
                    straight = j;
                }
            }

            bool hasS = false, hasR = false, hasL = false;
            for (int j = 0; j < cnt; j++)
            {
                if (j == i || _gExit[j] == 0) continue;
                if (j == straight)
                {
                    _gCls[j] = ClsStraight;
                    hasS = true;
                    continue;
                }
                float d = Vec2.Dot(din, _gOut[j]);
                if (d < Cos150) continue; // cambio de sentido: no se genera en cruces
                if (Vec2.Cross(din, _gOut[j]) < 0f)
                {
                    _gCls[j] = ClsRight;
                    hasR = true;
                }
                else
                {
                    _gCls[j] = ClsLeft;
                    hasL = true;
                }
            }

            // Carriles de entrada (0 = derecho) que sirven a cada clase: rangos [lo, hi].
            int sLo = 0, sHi = entries - 1, rLo = 0, rHi = entries - 1, lLo = 0, lHi = entries - 1;
            if (entries >= 2)
            {
                if (hasS)
                {
                    rLo = 0; rHi = 0;
                    lLo = entries - 1; lHi = entries - 1;
                }
                else if (hasR && hasL)
                {
                    rHi = (entries + 1) / 2 - 1;
                    lLo = entries / 2;
                }
            }

            for (int j = 0; j < cnt; j++)
            {
                int cls = _gCls[j];
                if (cls == ClsNone) continue;
                int lo, hi, m = _gExit[j];
                TurnType turn;
                if (cls == ClsStraight) { lo = sLo; hi = sHi; turn = TurnType.Straight; }
                else if (cls == ClsRight) { lo = rLo; hi = rHi; turn = TurnType.Right; }
                else { lo = lLo; hi = lHi; turn = TurnType.Left; }

                int a = hi - lo + 1;
                int mp, off;
                if (cls == ClsStraight) { mp = m; off = 0; }
                else if (cls == ClsRight) { mp = Math.Min(a, m); off = 0; }
                else { mp = Math.Min(a, m); off = m - mp; }

                for (int k = 0; k < a; k++)
                {
                    if (mp >= a)
                    {
                        int first = k * mp / a;
                        int last = (k + 1) * mp / a - 1;
                        for (int e = first; e <= last; e++) Emit(dst, ref n, ni, i, lo + k, j, off + e, turn, ref overflow);
                    }
                    else
                    {
                        Emit(dst, ref n, ni, i, lo + k, j, off + k * mp / a, turn, ref overflow);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ almacenamiento de bloques

        private void EnsureConnCapacity(int needed)
        {
            if (needed <= _conns.Length) return;
            int cap = _conns.Length;
            while (cap < needed) cap *= 2;
            Array.Resize(ref _conns, cap);
            Array.Resize(ref _connArc, cap * ConnArcSamples);
        }

        /// <summary>Escribe las conexiones generadas como bloque del nodo y reasigna los rangos de sus carriles.</summary>
        private void CommitNode(int ni, LaneConnection[] src, int n)
        {
            ref Node nd = ref _nodes[ni];
            if (n > nd.ConnCapacity)
            {
                _connHoles += nd.ConnCapacity;
                int cap = (n + 3) & ~3;
                EnsureConnCapacity(_connUsed + cap);
                nd.ConnFirst = _connUsed;
                nd.ConnCapacity = (ushort)cap;
                _connUsed += cap;
            }
            int first = nd.ConnFirst;
            for (int q = 0; q < n; q++)
            {
                int idx = first + q;
                _conns[idx] = src[q];
                var tbl = new Span<float>(_connArc, idx * ConnArcSamples, ConnArcSamples);
                ArcLengthTable.Build(_conns[idx].Curve, tbl);
                _conns[idx].Length = ArcLengthTable.TotalLengthOf(tbl);
            }
            nd.ConnCount = (ushort)n;
            AssignLaneRanges(ni);
            TouchNode(nd.Id);
        }

        /// <summary>Pone Lane.ConnFirst/ConnCount de los carriles de entrada del nodo a partir de su bloque.</summary>
        private void AssignLaneRanges(int ni)
        {
            ref readonly Node nd = ref _nodes[ni];
            int adj = ni * MaxSegmentsPerNode;
            for (int i = 0; i < nd.SegmentCount; i++)
            {
                int si = _nodeSegments[adj + i].Index;
                ref readonly Segment sg = ref _segments[si];
                bool atA = sg.NodeA.Index == ni;
                int from = atA ? sg.LanesForward : 0;
                int count = atA ? sg.LanesBackward : sg.LanesForward;
                for (int q = 0; q < count; q++)
                {
                    ref Lane l = ref _lanes[si * MaxLanesPerSegment + from + q];
                    l.ConnFirst = 0;
                    l.ConnCount = 0;
                }
            }
            int prev = -1;
            for (int q = 0; q < nd.ConnCount; q++)
            {
                int idx = nd.ConnFirst + q;
                int fromValue = _conns[idx].From.Value;
                ref Lane l = ref _lanes[fromValue];
                if (fromValue != prev)
                {
                    l.ConnFirst = idx;
                    l.ConnCount = 0;
                    prev = fromValue;
                }
                l.ConnCount++;
            }
        }

        /// <summary>Regenera el nodo sin poder fallar (si pasa de 64 conserva las primeras 64).</summary>
        private void RebuildNodeForced(int ni)
        {
            int n = GenerateNode(ni, _genA, out _);
            CommitNode(ni, _genA, n);
        }

        private void ReleaseNodeBlock(int ni)
        {
            ref Node nd = ref _nodes[ni];
            _connHoles += nd.ConnCapacity;
            nd.ConnFirst = 0;
            nd.ConnCount = 0;
            nd.ConnCapacity = 0;
        }

        /// <summary>Compacta _conns eliminando huecos cuando superan el 50 %. Solo se llama desde ediciones.</summary>
        private void MaybeCompactConnections()
        {
            if (_connHoles == 0 || _connHoles * 2 <= _connUsed) return;
            int live = _connUsed - _connHoles;
            var newConns = new LaneConnection[Math.Max(64, live + live / 2 + 64)];
            var newArc = new float[newConns.Length * ConnArcSamples];
            int pos = 0;
            for (int ni = 0; ni < _nodeHigh; ni++)
            {
                ref Node nd = ref _nodes[ni];
                if (!nd.Id.IsValid || nd.ConnCapacity == 0) continue;
                Array.Copy(_conns, nd.ConnFirst, newConns, pos, nd.ConnCount);
                Array.Copy(_connArc, nd.ConnFirst * ConnArcSamples, newArc, pos * ConnArcSamples, nd.ConnCount * ConnArcSamples);
                nd.ConnFirst = pos;
                pos += nd.ConnCapacity;
            }
            _conns = newConns;
            _connArc = newArc;
            _connUsed = pos;
            _connHoles = 0;
            for (int ni = 0; ni < _nodeHigh; ni++)
            {
                if (_nodes[ni].Id.IsValid) AssignLaneRanges(ni);
            }
        }
    }
}
