using System;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;

namespace Urbe.Sim.Roads
{
    // API publica de mutacion: primitivas exactas (Raw) y edicion de alto nivel con snap y cruces.
    public sealed partial class RoadNetwork
    {
        private const int MaxCrossings = 32;
        private const int MaxPlanPoints = MaxCrossings + 2;
        /// <summary>Dos calles a menos de esto a lo largo de <see cref="OverlapMinRun"/> metros se consideran solapadas.</summary>
        private const float OverlapDistance = 0.25f;
        private const float OverlapMinRun = 3f;

        private enum PointKind : byte { Free, Existing, Split }

        private struct PlanPoint
        {
            public PointKind Kind;
            public EntityId Node;   // Existing: el nodo; Free/Split: None hasta aplicar
            public EntityId Seg;    // Split: segmento a dividir
            public float TSeg;      // Split: parametro en Seg
            public float TNew;      // parametro en la curva nueva
            public Vec2 Pos;
            public float Elev;
        }

        private readonly PlanPoint[] _plan = new PlanPoint[MaxPlanPoints];
        private readonly PlanPoint[] _cross = new PlanPoint[MaxCrossings];
        private readonly Bezier3[] _pieces = new Bezier3[MaxPlanPoints - 1];
        private readonly Vec2[] _joint = new Vec2[MaxPlanPoints];
        private readonly int[] _group = new int[MaxPlanPoints];
        private readonly EntityId[] _cand = new EntityId[1024];
        private readonly CurveHit[] _hits = new CurveHit[16];
        private int _planCount;
        private Bezier3 _planCurve;
        private ushort _planType;

        // ------------------------------------------------------------------ primitivas exactas

        public EntityId AddNodeRaw(Vec2 pos, float elevation)
        {
            EntityId id = AddNodeInternal(pos, elevation, null);
            FinishMutation();
            return id;
        }

        /// <summary>Alta exacta sin snap ni cruces. Devuelve None (sin cambios) si se rechaza.</summary>
        public EntityId AddSegmentRaw(EntityId a, EntityId b, in Bezier3 curve, ushort roadTypeId)
        {
            EntityId id = AddSegmentInternal(a, b, curve, roadTypeId, SegmentFlags.None, null);
            if (id.IsValid) FinishMutation();
            return id;
        }

        // ------------------------------------------------------------------ mutaciones de alto nivel

        public bool SplitSegment(EntityId seg, float t, NetworkEdit? edit)
        {
            if (!_segIds.IsAlive(seg) || !(t > 0f && t < 1f)) return false;
            edit ??= _scratchEdit;
            edit.Clear();
            SplitInternal(seg, t, edit, out _, out _, out _);
            FinishMutation();
            return true;
        }

        public bool RemoveSegment(EntityId seg, NetworkEdit? edit)
        {
            if (!_segIds.IsAlive(seg)) return false;
            edit ??= _scratchEdit;
            edit.Clear();
            RemoveSegmentInternal(seg, edit);
            FinishMutation();
            return true;
        }

        public ValidationResult CanAddSegment(in SegmentRequest req)
        {
            return Plan(req);
        }

        public bool AddSegment(in SegmentRequest req, NetworkEdit? edit)
        {
            edit ??= _scratchEdit;
            edit.Clear();
            if (Plan(req) != ValidationResult.Ok) return false;
            bool ok = ApplyPlan(edit);
            if (!ok)
            {
                // Fallo al aplicar (limite de conexiones): se revierte todo y se avisa sin dejar rastro en 'edit'.
                UndoEdit(edit);
                edit.Clear();
            }
            FinishMutation();
            return ok;
        }

        // ------------------------------------------------------------------ planificacion

        private float LerpElev(EntityId a, EntityId b, float t)
        {
            float ea = _nodes[a.Index].Elevation;
            return ea + (_nodes[b.Index].Elevation - ea) * t;
        }

        private float DistanceAtParam(int si, float t)
        {
            ref readonly Segment s = ref _segments[si];
            return ArcLengthTable.DistanceAtT(s.Curve, new ReadOnlySpan<float>(_arcTables, s.ArcTableOffset, ArcSamples), t);
        }

        private static PlanPoint NewPoint(PointKind kind, Vec2 pos, float elev)
        {
            PlanPoint p = default;
            p.Kind = kind;
            p.Node = EntityId.None;
            p.Seg = EntityId.None;
            p.Pos = pos;
            p.Elev = elev;
            return p;
        }

        private PlanPoint ExistingPoint(EntityId node)
        {
            PlanPoint p = NewPoint(PointKind.Existing, _nodes[node.Index].Position, _nodes[node.Index].Elevation);
            p.Node = node;
            return p;
        }

        /// <summary>
        /// Decide como cortar un segmento existente en t: si algun trozo quedaria menor que MinSegmentLength
        /// se usa el extremo mas proximo (nodo existente); si no, hay que dividir.
        /// </summary>
        private PlanPoint ResolveCut(EntityId seg, float t)
        {
            ref readonly Segment s = ref _segments[seg.Index];
            float before = DistanceAtParam(seg.Index, t);
            float after = s.Length - before;
            if (before < _minSegmentLength || after < _minSegmentLength)
                return ExistingPoint(before <= after ? s.NodeA : s.NodeB);
            PlanPoint p = NewPoint(PointKind.Split, s.Curve.Evaluate(t), LerpElev(s.NodeA, s.NodeB, t));
            p.Seg = seg;
            p.TSeg = t;
            return p;
        }

        private PlanPoint ResolveAnchor(Vec2 p, float elev)
        {
            EntityId n = NearestNode(p, _snapRadius);
            if (n.IsValid) return ExistingPoint(n);
            if (NearestSegment(p, _snapRadius, out EntityId seg, out float t, out _)) return ResolveCut(seg, t);
            return NewPoint(PointKind.Free, p, elev);
        }

        private bool IsIncident(EntityId seg, EntityId node)
        {
            ref readonly Segment s = ref _segments[seg.Index];
            return s.NodeA == node || s.NodeB == node;
        }

        private static float ApproxLength(in Bezier3 c)
        {
            float sum = 0f;
            for (int i = 0; i < 4; i++) sum += ArcLengthTable.SegmentLength(c, i * 0.25f, (i + 1) * 0.25f);
            return sum;
        }

        /// <summary>Parte la curva nueva en trozos entre los puntos del plan, encajando los extremos en _joint.</summary>
        private void BuildPieces(in Bezier3 curve, int pointCount)
        {
            Bezier3 rem = curve;
            float tPrev = 0f;
            int pieces = pointCount - 1;
            for (int i = 0; i < pieces; i++)
            {
                Bezier3 piece;
                if (i < pieces - 1)
                {
                    float t = _plan[i + 1].TNew;
                    float denom = 1f - tPrev;
                    float u = denom > 1e-6f ? (t - tPrev) / denom : 1f;
                    rem.Split(u, out piece, out Bezier3 next);
                    rem = next;
                    tPrev = t;
                }
                else
                {
                    piece = rem;
                }
                Vec2 p0 = _joint[i], p3 = _joint[i + 1];
                _pieces[i] = new Bezier3(p0, piece.P1 + (p0 - piece.P0), piece.P2 + (p3 - piece.P3), p3);
            }
        }

        private ValidationResult Plan(in SegmentRequest req)
        {
            _planCount = 0;
            if (!_types.IsValidId(req.RoadTypeId)) return ValidationResult.UnknownRoadType;
            if (!InWorld(req.Start) || !InWorld(req.End)) return ValidationResult.OutOfBounds;

            PlanPoint first = ResolveAnchor(req.Start, req.ElevStart);
            PlanPoint last = ResolveAnchor(req.End, req.ElevEnd);
            first.TNew = 0f;
            last.TNew = 1f;
            if (first.Kind == PointKind.Existing && last.Kind == PointKind.Existing && first.Node == last.Node)
                return ValidationResult.SameNode;

            var curve = new Bezier3(first.Pos, req.C1 + (first.Pos - req.Start), req.C2 + (last.Pos - req.End), last.Pos);

            // ---- cruces con la red existente, ordenados por t de la curva nueva
            int nc = 0;
            int candCount = QuerySegments(curve.Bounds, _cand);
            if (candCount >= _cand.Length) return ValidationResult.TooManyCrossings; // el buffer pudo truncar candidatos
            for (int q = 0; q < candCount; q++)
            {
                EntityId sid = _cand[q];
                if (first.Kind == PointKind.Existing && IsIncident(sid, first.Node)) continue;
                if (last.Kind == PointKind.Existing && IsIncident(sid, last.Node)) continue;
                if (first.Kind == PointKind.Split && first.Seg == sid) continue;
                if (last.Kind == PointKind.Split && last.Seg == sid) continue;

                ref readonly Segment s = ref _segments[sid.Index];
                // Calle dibujada sobre otra (solape o casi colinealidad): se rechaza ya, sin subdividir (M1-16). A distinta
                // altura (paso elevado) no cuenta.
                if (Math.Abs(first.Elev + (last.Elev - first.Elev) * 0.5f - LerpElev(s.NodeA, s.NodeB, 0.5f)) < _maxCrossElevDelta
                    && Intersections.RunsAlong(curve, s.Curve, OverlapDistance, OverlapMinRun))
                    return ValidationResult.AngleTooSharp;
                int nh = Intersections.CurveCurve(curve, s.Curve, _hits);
                for (int h = 0; h < nh; h++)
                {
                    float tn = _hits[h].TA, ts = _hits[h].TB;
                    float eNew = first.Elev + (last.Elev - first.Elev) * tn;
                    float eOld = LerpElev(s.NodeA, s.NodeB, ts);
                    if (Math.Abs(eNew - eOld) >= _maxCrossElevDelta) continue;

                    PlanPoint cp = ResolveCut(sid, ts);
                    cp.TNew = tn;
                    if (cp.Kind == PointKind.Existing)
                    {
                        bool dup = false;
                        for (int k = 0; k < nc; k++)
                            if (_cross[k].Kind == PointKind.Existing && _cross[k].Node == cp.Node) { dup = true; break; }
                        if (dup) continue;
                    }
                    if (nc == MaxCrossings) return ValidationResult.TooManyCrossings;
                    int pos = nc;
                    while (pos > 0 && _cross[pos - 1].TNew > tn)
                    {
                        _cross[pos] = _cross[pos - 1];
                        pos--;
                    }
                    _cross[pos] = cp;
                    nc++;
                }
            }

            _plan[0] = first;
            for (int k = 0; k < nc; k++) _plan[k + 1] = _cross[k];
            _plan[nc + 1] = last;
            _planCount = nc + 2;
            _planCurve = curve;
            _planType = req.RoadTypeId;
            int pieces = _planCount - 1;

            for (int i = 0; i < _planCount; i++) _joint[i] = _plan[i].Pos;
            BuildPieces(curve, _planCount);

            // ---- longitudes de los trozos nuevos
            for (int i = 0; i < pieces; i++)
            {
                float len = ApproxLength(_pieces[i]);
                if (len < _minSegmentLength - 1e-3f) return ValidationResult.TooShort;
                if (len > _maxSegmentLength + 1e-3f) return ValidationResult.TooLong;
            }

            // ---- separacion entre cortes de un mismo segmento existente
            for (int i = 0; i < _planCount; i++)
            {
                if (_plan[i].Kind != PointKind.Split) continue;
                float di = DistanceAtParam(_plan[i].Seg.Index, _plan[i].TSeg);
                for (int j = i + 1; j < _planCount; j++)
                {
                    if (_plan[j].Kind != PointKind.Split || _plan[j].Seg != _plan[i].Seg) continue;
                    float dj = DistanceAtParam(_plan[j].Seg.Index, _plan[j].TSeg);
                    if (Math.Abs(dj - di) < _minSegmentLength) return ValidationResult.TooShort;
                }
            }

            // ---- nodos existentes: sin repeticiones, grado maximo
            for (int i = 0; i < _planCount; i++)
            {
                if (_plan[i].Kind != PointKind.Existing) continue;
                for (int j = 0; j < i; j++)
                    if (_plan[j].Kind == PointKind.Existing && _plan[j].Node == _plan[i].Node) return ValidationResult.SameNode;
                int add = (i == 0 || i == _planCount - 1) ? 1 : 2;
                if (_nodes[_plan[i].Node.Index].SegmentCount + add > MaxSegmentsPerNode) return ValidationResult.NodeFull;
            }

            // ---- duplicados
            for (int i = 0; i < pieces; i++)
            {
                if (_plan[i].Kind == PointKind.Existing && _plan[i + 1].Kind == PointKind.Existing
                    && FindSegmentBetween(_plan[i].Node, _plan[i + 1].Node).IsValid)
                    return ValidationResult.DuplicateSegment;
            }

            // ---- angulo minimo en cruces
            for (int i = 0; i < _planCount; i++)
            {
                bool hasOut = i < _planCount - 1;
                bool hasIn = i > 0;
                Vec2 dOut = hasOut ? _pieces[i].Tangent(0f) : Vec2.Zero;
                Vec2 dIn = hasIn ? -_pieces[i - 1].Tangent(1f) : Vec2.Zero;
                if (_plan[i].Kind == PointKind.Existing)
                {
                    EntityId node = _plan[i].Node;
                    int adj = node.Index * MaxSegmentsPerNode;
                    int cnt = _nodes[node.Index].SegmentCount;
                    for (int e = 0; e < cnt; e++)
                    {
                        Vec2 et = OutTangent(_nodeSegments[adj + e], node.Index);
                        if (hasOut && Vec2.Dot(dOut, et) > _cosMinAngle) return ValidationResult.AngleTooSharp;
                        if (hasIn && Vec2.Dot(dIn, et) > _cosMinAngle) return ValidationResult.AngleTooSharp;
                    }
                }
                else if (_plan[i].Kind == PointKind.Split)
                {
                    Vec2 st = _segments[_plan[i].Seg.Index].Curve.Tangent(_plan[i].TSeg);
                    Vec2 nt = hasOut ? dOut : -dIn;
                    if (Math.Abs(Vec2.Cross(nt, st)) < _sinMinAngle) return ValidationResult.AngleTooSharp;
                }
            }

            return ValidationResult.Ok;
        }

        // ------------------------------------------------------------------ aplicacion

        private bool ApplyPlan(NetworkEdit edit)
        {
            int count = _planCount;
            for (int i = 0; i < count; i++)
            {
                ref PlanPoint pt = ref _plan[i];
                if (pt.Kind == PointKind.Free)
                {
                    pt.Node = AddNodeInternal(pt.Pos, pt.Elev, edit);
                }
                else if (pt.Kind == PointKind.Split && !pt.Node.IsValid)
                {
                    // Todos los cortes del mismo segmento, por t creciente, dividiendo siempre la mitad restante.
                    int g = 0;
                    for (int j = i; j < count; j++)
                    {
                        if (_plan[j].Kind != PointKind.Split || _plan[j].Seg != pt.Seg) continue;
                        int pos = g;
                        while (pos > 0 && _plan[_group[pos - 1]].TSeg > _plan[j].TSeg)
                        {
                            _group[pos] = _group[pos - 1];
                            pos--;
                        }
                        _group[pos] = j;
                        g++;
                    }
                    EntityId cur = pt.Seg;
                    float tBase = 0f;
                    for (int k = 0; k < g; k++)
                    {
                        ref PlanPoint cut = ref _plan[_group[k]];
                        float u = (cut.TSeg - tBase) / (1f - tBase);
                        SplitInternal(cur, u, edit, out EntityId node, out _, out EntityId h2);
                        cut.Node = node;
                        cur = h2;
                        tBase = cut.TSeg;
                    }
                }
            }

            for (int i = 0; i < count; i++) _joint[i] = _nodes[_plan[i].Node.Index].Position;
            BuildPieces(_planCurve, count);
            for (int i = 0; i < count - 1; i++)
            {
                EntityId id = AddSegmentInternal(_plan[i].Node, _plan[i + 1].Node, _pieces[i], _planType, SegmentFlags.None, edit);
                if (!id.IsValid) return false;
            }
            return true;
        }
    }
}
