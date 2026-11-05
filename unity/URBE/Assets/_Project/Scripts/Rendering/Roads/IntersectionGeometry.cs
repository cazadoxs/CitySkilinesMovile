using System;
using UnityEngine;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Game.Rendering.Roads
{
    /// <summary>Convierte segmentos de la red en <see cref="RoadPiece"/> con los indicadores de extremo (cierres de mediana, líneas de detención).</summary>
    public static class RoadPieces
    {
        public static RoadPiece FromSegment(RoadNetwork net, EntityId seg)
        {
            ref readonly Segment s = ref net.GetSegment(seg);
            RoadPieceFlags flags = RoadPieceFlags.None;
            ClassifyEnd(net, s.NodeA, RoadPieceFlags.MedianCapStart, RoadPieceFlags.StopLineStart, ref flags);
            ClassifyEnd(net, s.NodeB, RoadPieceFlags.MedianCapEnd, RoadPieceFlags.StopLineEnd, ref flags);
            var piece = new RoadPiece(s.Curve, s.RoadTypeId, s.TLaneA, s.TLaneB, net.GetNode(s.NodeA).Elevation, net.GetNode(s.NodeB).Elevation,
                seg.Index, flags);
            piece.MitreStart = IntersectionGeometry.MitreK(net, s.NodeA, seg);
            piece.MitreEnd = IntersectionGeometry.MitreK(net, s.NodeB, seg);
            return piece;
        }

        private static void ClassifyEnd(RoadNetwork net, EntityId node, RoadPieceFlags cap, RoadPieceFlags stop, ref RoadPieceFlags flags)
        {
            int count = net.GetNode(node).SegmentCount;
            if (!IntersectionGeometry.NeedsNodeMesh(net, node)) return;
            flags |= cap;
            if (count >= 3) flags |= stop;
        }
    }

    /// <summary>
    /// Mallas de nodos (ADR 003, "Intersecciones"): final de calle (tapa semicircular de 6 sectores), cruces de 3 a 8 segmentos (esquinas con
    /// Bézier cuadrática y calzada en abanico, con recorte de orejas si el contorno no es estrellado) y continuaciones con cambio de ancho o
    /// quiebro. Todo parte de <see cref="RoadGeometry.CrossSectionAt"/> con el mismo t que usa el segmento, así que los vértices compartidos
    /// coinciden exactamente. Instancia con buffers de trabajo reutilizables (no es segura entre hilos).
    /// </summary>
    public sealed class IntersectionGeometry
    {
        public const int CornerSteps = 4;
        public const int DeadEndSectors = 6;
        private const float ParallelEps = 0.05f;
        /// <summary>cos(0,36°): por encima de esto dos segmentos con el mismo perfil se consideran continuos y no llevan malla de nodo.</summary>
        private const float ContinuousDot = 0.99998f;
        private const int MaxEnds = RoadNetwork.MaxSegmentsPerNode;
        private const int CornerPoints = CornerSteps + 1;

        private struct End
        {
            public Vector3 LB, LT, LO;   // borde izquierdo mirando hacia fuera del nodo: calzada (abajo del bordillo), borde de acera, exterior
            public Vector3 RB, RT, RO;   // borde derecho
            public Vector2 Dir;          // dirección hacia fuera del nodo
        }

        private readonly RoadProfile[] _profiles;
        private readonly End[] _ends = new End[MaxEnds];
        private readonly Vector3[] _cornerB = new Vector3[MaxEnds * CornerPoints];
        private readonly Vector3[] _cornerT = new Vector3[MaxEnds * CornerPoints];
        private readonly Vector3[] _cornerO = new Vector3[MaxEnds * CornerPoints];
        private Vector3[] _contour = new Vector3[MaxEnds * (2 + CornerSteps - 1) + 4];
        private int[] _order = new int[MaxEnds * (2 + CornerSteps - 1) + 4];
        private readonly Vector3[] _tmpA = new Vector3[CornerPoints];
        private readonly Vector3[] _tmpB = new Vector3[CornerPoints];
        private readonly Vector3[] _tmpN = new Vector3[CornerPoints];
        private readonly float[] _tmpV = new float[CornerPoints];

        public IntersectionGeometry(RoadProfile[] profiles)
        {
            _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        }

        /// <summary>Máximo giro (grados) que se resuelve con inglete; más cerrado se usa el nodo genérico (puede solapar en el interior del codo).</summary>
        public const float MaxMitreTurnDegrees = 100f;
        private const float MaxMitreShiftFraction = 0.35f;

        /// <summary>Prueba/diagnóstico: veces que la calzada de un cruce se triangulizó por recorte de orejas en vez de abanico.</summary>
        public int EarClipCount { get; private set; }
        /// <summary>Prueba: fuerza el recorte de orejas aunque el abanico sea válido.</summary>
        public bool ForceEarClipping { get; set; }

        /// <summary>
        /// True si el nodo necesita malla propia. Una continuación entre dos segmentos del mismo tipo no la necesita: si son tangentes se
        /// unen tal cual y si hay un quiebro de hasta 100° se cortan en inglete (<see cref="MitreK"/>).
        /// </summary>
        public static bool NeedsNodeMesh(RoadNetwork net, EntityId node)
        {
            ref readonly Node n = ref net.GetNode(node);
            if (n.SegmentCount == 0) return false;
            if (n.SegmentCount != 2) return true;
            ReadOnlySpan<EntityId> segs = net.GetNodeSegments(node);
            ref readonly Segment s0 = ref net.GetSegment(segs[0]);
            ref readonly Segment s1 = ref net.GetSegment(segs[1]);
            if (s0.RoadTypeId != s1.RoadTypeId) return true;
            Vec2 d0 = OutDir(in s0, node);
            Vec2 d1 = OutDir(in s1, node);
            float dot = Vec2.Dot(d0, d1);
            if (dot <= -ContinuousDot) return false;
            return !CanMitre(net, in s0, in s1, dot);
        }

        private static bool CanMitre(RoadNetwork net, in Segment s0, in Segment s1, float dotOut)
        {
            // Giro = 180° menos el ángulo entre las salidas. Solo se admite hasta MaxMitreTurnDegrees.
            if (dotOut > Mathf.Cos((180f - MaxMitreTurnDegrees) * Mathf.Deg2Rad)) return false;
            var prof = new RoadProfile(net.Types.Get(s0.RoadTypeId));
            float maxLat = Mathf.Max(Mathf.Abs(prof.LeftOuter), Mathf.Abs(prof.RightOuter));
            float k = Mathf.Tan(Mathf.Acos(Mathf.Clamp(-dotOut, -1f, 1f)) * 0.5f);
            return k * maxLat <= MaxMitreShiftFraction * Mathf.Min(s0.Length, s1.Length);
        }

        /// <summary>
        /// Coeficiente de inglete (desplazamiento a lo largo de la tangente por metro lateral) del extremo del segmento en este nodo, o 0 si el nodo no
        /// es una continuación en inglete. El primer segmento del nodo recibe +tan(phi/2) y el segundo -tan(phi/2), con phi el giro de uno al otro.
        /// </summary>
        public static float MitreK(RoadNetwork net, EntityId node, EntityId seg)
        {
            ref readonly Node n = ref net.GetNode(node);
            if (n.SegmentCount != 2) return 0f;
            ReadOnlySpan<EntityId> segs = net.GetNodeSegments(node);
            ref readonly Segment s0 = ref net.GetSegment(segs[0]);
            ref readonly Segment s1 = ref net.GetSegment(segs[1]);
            if (s0.RoadTypeId != s1.RoadTypeId) return 0f;
            Vec2 o0 = OutDir(in s0, node), o1 = OutDir(in s1, node);
            float dot = Vec2.Dot(o0, o1);
            if (dot <= -ContinuousDot || !CanMitre(net, in s0, in s1, dot)) return 0f;
            Vec2 dIn = -o0;
            float sin = Vec2.Cross(dIn, o1);
            float cos = Vec2.Dot(dIn, o1);
            float tanHalf = sin / (1f + cos);
            return segs[0] == seg ? tanHalf : -tanHalf;
        }

        private static Vec2 OutDir(in Segment s, EntityId node)
        {
            return s.NodeA == node ? s.Curve.Tangent(s.TLaneA) : -s.Curve.Tangent(s.TLaneB);
        }

        /// <summary>Malla del nodo en <paramref name="dst"/> (vacía si no hace falta).</summary>
        public void BuildNode(RoadNetwork net, EntityId node, MeshBuffers dst)
        {
            int count = net.GetNode(node).SegmentCount;
            if (count == 0 || !NeedsNodeMesh(net, node)) return;
            if (count == 1) BuildDeadEnd(net, node, dst);
            else BuildJunction(net, node, dst);
        }

        // ------------------------------------------------------------------ final de calle

        private void BuildDeadEnd(RoadNetwork net, EntityId node, MeshBuffers dst)
        {
            EntityId segId = net.GetNodeSegments(node)[0];
            ref readonly Segment s = ref net.GetSegment(segId);
            bool atA = s.NodeA == node;
            RoadPiece piece = RoadPieces.FromSegment(net, segId);
            RoadProfile pr = _profiles[s.RoadTypeId];
            float t = atA ? s.TLaneA : s.TLaneB;
            Span<Vector3> pts = stackalloc Vector3[RoadGeometry.PointCount];
            RoadGeometry.CrossSectionAt(piece, t, pr, pts, out SectionFrame f);

            Vector3 lcb = pts[RoadGeometry.PLeftCurbBottom], rcb = pts[RoadGeometry.PRightCurbBottom];
            Vector3 lct = pts[RoadGeometry.PLeftCurbTop], rct = pts[RoadGeometry.PRightCurbTop];
            Vector3 lo = pts[RoadGeometry.PLeftOuter], ro = pts[RoadGeometry.PRightOuter];
            Vector3 center = (lcb + rcb) * 0.5f;
            Vector2 e2 = new Vector2(rcb.x - lcb.x, rcb.z - lcb.z);
            float r = e2.magnitude * 0.5f;
            e2 = e2.normalized;
            Vector2 fwd2 = atA ? -f.Tangent : f.Tangent;
            float margin = pr.Margin;
            const float h = RoadProfile.CurbHeight;

            int n = DeadEndSectors + 1;
            var bottom = new Vector3[n];
            var top = new Vector3[n];
            var outer = new Vector3[n];
            var inward = new Vector3[n];
            var vv = new float[n];
            for (int k = 0; k < n; k++)
            {
                float th = Mathf.PI * (1f - k / (float)DeadEndSectors);
                float cs = Mathf.Cos(th), sn = Mathf.Sin(th);
                Vector3 dir = new Vector3(e2.x * cs + fwd2.x * sn, 0f, e2.y * cs + fwd2.y * sn);
                bottom[k] = center + dir * r;
                top[k] = bottom[k] + new Vector3(0f, h, 0f);
                outer[k] = center + dir * (r + margin) + new Vector3(0f, h, 0f);
                inward[k] = -dir;
                vv[k] = k * 0.1f;
            }
            // Los extremos son exactamente los de la sección del segmento (sin error de seno y coseno).
            bottom[0] = lcb; bottom[n - 1] = rcb;
            top[0] = lct; top[n - 1] = rct;
            outer[0] = lo; outer[n - 1] = ro;

            // Calzada en abanico
            float asphaltU = RoadAtlasLayout.UCenter(RoadAtlasLayout.ColAsphalt);
            int c0 = dst.AddVertex(center, Vector3.up, MeshBuffers.RoadMask, asphaltU, center.z / RoadAtlasLayout.TileMeters, -1f);
            int first = dst.VertexCount;
            for (int k = 0; k < n; k++)
                dst.AddVertex(bottom[k], Vector3.up, MeshBuffers.RoadMask, asphaltU, bottom[k].z / RoadAtlasLayout.TileMeters, -1f);
            for (int k = 0; k < n - 1; k++) dst.AddTriangle(c0, first + k, first + k + 1, Vector3.up);

            float curbU = RoadAtlasLayout.UCenter(RoadAtlasLayout.ColCurb);
            float walkU = RoadAtlasLayout.UCenter(RoadAtlasLayout.ColSidewalk);
            dst.AddStrip(bottom, top, inward, curbU, curbU, vv, 0, -1f);
            var up = new Vector3[n];
            var wv = new float[n];
            for (int k = 0; k < n; k++) { up[k] = Vector3.up; wv[k] = top[k].z / RoadAtlasLayout.TileMeters; }
            dst.AddStrip(top, outer, up, walkU, walkU, wv, 0, -1f);
        }

        // ------------------------------------------------------------------ cruces

        private void BuildJunction(RoadNetwork net, EntityId node, MeshBuffers dst)
        {
            ReadOnlySpan<EntityId> segs = net.GetNodeSegments(node);
            int count = segs.Length;
            Span<Vector3> pts = stackalloc Vector3[RoadGeometry.PointCount];
            for (int k = 0; k < count; k++)
            {
                ref readonly Segment s = ref net.GetSegment(segs[k]);
                bool atA = s.NodeA == node;
                RoadPiece piece = RoadPieces.FromSegment(net, segs[k]);
                RoadProfile pr = _profiles[s.RoadTypeId];
                float t = atA ? s.TLaneA : s.TLaneB;
                RoadGeometry.CrossSectionAt(piece, t, pr, pts, out SectionFrame f);
                ref End e = ref _ends[k];
                if (atA)
                {
                    // Mirando hacia fuera del nodo (a lo largo de A hacia B), la izquierda es la lateral negativa.
                    e.LB = pts[RoadGeometry.PLeftCurbBottom]; e.LT = pts[RoadGeometry.PLeftCurbTop]; e.LO = pts[RoadGeometry.PLeftOuter];
                    e.RB = pts[RoadGeometry.PRightCurbBottom]; e.RT = pts[RoadGeometry.PRightCurbTop]; e.RO = pts[RoadGeometry.PRightOuter];
                    e.Dir = f.Tangent;
                }
                else
                {
                    e.LB = pts[RoadGeometry.PRightCurbBottom]; e.LT = pts[RoadGeometry.PRightCurbTop]; e.LO = pts[RoadGeometry.PRightOuter];
                    e.RB = pts[RoadGeometry.PLeftCurbBottom]; e.RT = pts[RoadGeometry.PLeftCurbTop]; e.RO = pts[RoadGeometry.PLeftOuter];
                    e.Dir = -f.Tangent;
                }
            }

            // Esquinas: del borde izquierdo (lado CCW) del segmento k al borde derecho del siguiente.
            for (int k = 0; k < count; k++)
            {
                int nx = (k + 1) % count;
                Corner(_ends[k].LB, _ends[k].Dir, _ends[nx].RB, _ends[nx].Dir, _cornerB, k * CornerPoints);
                Corner(_ends[k].LT, _ends[k].Dir, _ends[nx].RT, _ends[nx].Dir, _cornerT, k * CornerPoints);
                Corner(_ends[k].LO, _ends[k].Dir, _ends[nx].RO, _ends[nx].Dir, _cornerO, k * CornerPoints);
            }

            // Contorno de la calzada en sentido antihorario: boca de k (de su derecha a su izquierda) y esquina hasta el siguiente.
            int m = 0;
            int cap = count * (2 + CornerSteps - 1);
            if (_contour.Length < cap) { _contour = new Vector3[cap]; _order = new int[cap]; }
            for (int k = 0; k < count; k++)
            {
                _contour[m++] = _ends[k].RB;
                _contour[m++] = _ends[k].LB;
                for (int j = 1; j < CornerSteps; j++) _contour[m++] = _cornerB[k * CornerPoints + j];
            }
            EmitRoadway(dst, m);

            float curbU = RoadAtlasLayout.UCenter(RoadAtlasLayout.ColCurb);
            float walkU = RoadAtlasLayout.UCenter(RoadAtlasLayout.ColSidewalk);
            for (int k = 0; k < count; k++)
            {
                for (int j = 0; j < CornerPoints; j++)
                {
                    int idx = k * CornerPoints + j;
                    _tmpA[j] = _cornerB[idx];
                    _tmpB[j] = _cornerT[idx];
                    // Normal del bordillo: hacia el interior de la calzada (a la izquierda del sentido antihorario).
                    int j0 = Math.Max(0, j - 1), j1 = Math.Min(CornerSteps, j + 1);
                    Vector3 d = _cornerB[k * CornerPoints + j1] - _cornerB[k * CornerPoints + j0];
                    Vector3 nrm = new Vector3(-d.z, 0f, d.x).normalized;
                    _tmpN[j] = nrm;
                    _tmpV[j] = 0f;
                }
                dst.AddStrip(_tmpA, _tmpB, _tmpN, curbU, curbU, _tmpV, 0, -1f);
                for (int j = 0; j < CornerPoints; j++)
                {
                    int idx = k * CornerPoints + j;
                    _tmpA[j] = _cornerT[idx];
                    _tmpB[j] = _cornerO[idx];
                    _tmpN[j] = Vector3.up;
                    _tmpV[j] = _cornerT[idx].z / RoadAtlasLayout.TileMeters;
                }
                dst.AddStrip(_tmpA, _tmpB, _tmpN, walkU, walkU, _tmpV, 0, -1f);
            }
        }

        /// <summary>
        /// Esquina entre el borde p0 (dirección hacia fuera d0) y el borde p1 (d1): Bézier cuadrática con control en el corte de las dos rectas
        /// de borde. Si son casi paralelas o el corte queda en el lado equivocado o demasiado lejos, control en el punto medio.
        /// </summary>
        private static void Corner(Vector3 p0, Vector2 d0, Vector3 p1, Vector2 d1, Vector3[] outPts, int offset)
        {
            Vector2 a = new Vector2(p0.x, p0.z), b = new Vector2(p1.x, p1.z);
            Vector2 chord = b - a;
            Vector2 ctrl = (a + b) * 0.5f;
            float cr = Cross(d0, d1);
            if (Mathf.Abs(cr) >= ParallelEps)
            {
                float s = Cross(chord, d1) / cr;
                float u = Cross(chord, d0) / cr;
                float lim = chord.magnitude * 3f + 1f;
                if (s < 0f && u < 0f && -s <= lim && -u <= lim) ctrl = a + d0 * s;
            }
            outPts[offset] = p0;
            outPts[offset + CornerSteps] = p1;
            for (int j = 1; j < CornerSteps; j++)
            {
                float t = j / (float)CornerSteps;
                float w0 = (1f - t) * (1f - t), w1 = 2f * t * (1f - t), w2 = t * t;
                outPts[offset + j] = new Vector3(
                    w0 * a.x + w1 * ctrl.x + w2 * b.x,
                    p0.y + (p1.y - p0.y) * t,
                    w0 * a.y + w1 * ctrl.y + w2 * b.y);
            }
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        // ------------------------------------------------------------------ calzada: abanico o recorte de orejas

        private void EmitRoadway(MeshBuffers dst, int m)
        {
            float asphaltU = RoadAtlasLayout.UCenter(RoadAtlasLayout.ColAsphalt);
            int first = dst.VertexCount;
            for (int i = 0; i < m; i++)
                dst.AddVertex(_contour[i], Vector3.up, MeshBuffers.RoadMask, asphaltU, _contour[i].z / RoadAtlasLayout.TileMeters, -1f);

            Vector3 c = Vector3.zero;
            for (int i = 0; i < m; i++) c += _contour[i];
            c /= m;
            float area = SignedArea(m);
            // Contorno degenerado (todos los puntos alineados): no hay nada que dibujar.
            if (Mathf.Abs(area) < 1e-4f) return;
            float sign = area > 0f ? 1f : -1f;

            bool fanOk = !ForceEarClipping;
            for (int i = 0; fanOk && i < m; i++)
            {
                Vector3 p = _contour[i] - c, q = _contour[(i + 1) % m] - c;
                if ((p.x * q.z - p.z * q.x) * sign < -1e-5f) { fanOk = false; break; }
            }
            if (fanOk)
            {
                int ci = dst.AddVertex(c, Vector3.up, MeshBuffers.RoadMask, asphaltU, c.z / RoadAtlasLayout.TileMeters, -1f);
                for (int i = 0; i < m; i++) dst.AddTriangle(ci, first + i, first + (i + 1) % m, Vector3.up);
            }
            else
            {
                EarClipCount++;
                EarClip(dst, first, m, sign);
            }
        }

        private float SignedArea(int m)
        {
            float a = 0f;
            for (int i = 0; i < m; i++)
            {
                Vector3 p = _contour[i], q = _contour[(i + 1) % m];
                a += p.x * q.z - q.x * p.z;
            }
            return a * 0.5f;
        }

        private void EarClip(MeshBuffers dst, int first, int m, float sign)
        {
            int n = m;
            for (int i = 0; i < n; i++) _order[i] = i;
            int guard = n * n + 8;
            int pos = 0;
            while (n > 3 && guard-- > 0)
            {
                int i0 = _order[(pos + n - 1) % n], i1 = _order[pos % n], i2 = _order[(pos + 1) % n];
                Vector3 a = _contour[i0], b = _contour[i1], c = _contour[i2];
                float cr = ((b.x - a.x) * (c.z - a.z) - (b.z - a.z) * (c.x - a.x)) * sign;
                bool ear = cr > 1e-6f;
                if (ear)
                {
                    for (int k = 0; k < n && ear; k++)
                    {
                        int ik = _order[k];
                        if (ik == i0 || ik == i1 || ik == i2) continue;
                        if (InsideTri(_contour[ik], a, b, c, sign)) ear = false;
                    }
                }
                if (ear || Mathf.Abs(cr) <= 1e-6f)
                {
                    if (ear) dst.AddTriangle(first + i0, first + i1, first + i2, Vector3.up);
                    for (int k = pos % n; k < n - 1; k++) _order[k] = _order[k + 1];
                    n--;
                    pos = pos % Math.Max(n, 1);
                }
                else
                {
                    pos = (pos + 1) % n;
                }
            }
            if (n == 3) dst.AddTriangle(first + _order[0], first + _order[1], first + _order[2], Vector3.up);
        }

        private static bool InsideTri(Vector3 p, Vector3 a, Vector3 b, Vector3 c, float sign)
        {
            float d1 = ((b.x - a.x) * (p.z - a.z) - (b.z - a.z) * (p.x - a.x)) * sign;
            float d2 = ((c.x - b.x) * (p.z - b.z) - (c.z - b.z) * (p.x - b.x)) * sign;
            float d3 = ((a.x - c.x) * (p.z - c.z) - (a.z - c.z) * (p.x - c.x)) * sign;
            return d1 > 1e-6f && d2 > 1e-6f && d3 > 1e-6f;
        }
    }
}
