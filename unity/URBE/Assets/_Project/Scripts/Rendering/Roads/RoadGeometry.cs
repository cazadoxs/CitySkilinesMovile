using System;
using UnityEngine;
using Urbe.Sim.Geometry;

namespace Urbe.Game.Rendering.Roads
{
    /// <summary>Marco local de una sección: tangente y derecha horizontales (plano X,Z), pendiente y altura de la curva central.</summary>
    public struct SectionFrame
    {
        public Vector2 Tangent;
        public Vector2 Right;
        public float Slope;
        public float BaseY;
        public Vector3 Center;
    }

    /// <summary>
    /// Mallas de segmentos de carretera (ADR 003): perfil extruido a lo largo de la curva central entre T0 y T1, con muestreo adaptativo
    /// por arco. C# puro (sin Mesh ni GameObject): escribe en <see cref="MeshBuffers"/>. Sin estado estático: seguro de llamar desde cualquier hilo.
    /// </summary>
    public static class RoadGeometry
    {
        public const int PointCount = 10;
        // Índices de los 10 puntos de la sección, de izquierda a derecha (sin mediana, 3 a 6 quedan en el eje y no se usan).
        public const int PLeftOuter = 0;
        public const int PLeftCurbTop = 1;
        public const int PLeftCurbBottom = 2;
        public const int PMedLBottom = 3;
        public const int PMedLTop = 4;
        public const int PMedRTop = 5;
        public const int PMedRBottom = 6;
        public const int PRightCurbBottom = 7;
        public const int PRightCurbTop = 8;
        public const int PRightOuter = 9;

        public const int MaxRows = 96;
        public const float MaxRowAngleDegrees = 8f;
        public const float MaxRowSpacing = 40f;
        private const int Substeps = 48;
        private const float StopLineOffset = 0.9f;
        private const float StopLineThickness = 0.55f;
        private const float PaintLift = 0.02f;

        /// <summary>
        /// Filas del segmento: una nueva cuando el ángulo acumulado supera 8° o la distancia 40 m. Siempre incluye T0 y T1 exactos
        /// (mínimo 2 filas). <paramref name="rowS"/> es la distancia acumulada sobre la curva central (para la coordenada v).
        /// </summary>
        public static int SampleRows(in RoadPiece p, Span<float> rowT, Span<float> rowS)
        {
            float cosMax = Mathf.Cos(MaxRowAngleDegrees * Mathf.Deg2Rad);
            int n = 0;
            rowT[n] = p.T0;
            rowS[n] = 0f;
            n++;
            Vec2 lastPos = p.Curve.Evaluate(p.T0);
            Vec2 lastTan = p.Curve.Tangent(p.T0);
            Vec2 prevPos = lastPos;
            float s = 0f;
            for (int i = 1; i <= Substeps; i++)
            {
                float t = i == Substeps ? p.T1 : p.T0 + (p.T1 - p.T0) * (i / (float)Substeps);
                Vec2 pos = p.Curve.Evaluate(t);
                Vec2 tan = p.Curve.Tangent(t);
                s += (pos - prevPos).Length;
                prevPos = pos;
                if (i == Substeps) break;
                bool angle = Vec2.Dot(tan, lastTan) < cosMax;
                bool dist = (pos - lastPos).Length > MaxRowSpacing;
                if ((angle || dist) && n < MaxRows - 1)
                {
                    rowT[n] = t;
                    rowS[n] = s;
                    n++;
                    lastPos = pos;
                    lastTan = tan;
                }
            }
            rowT[n] = p.T1;
            rowS[n] = s;
            n++;
            return n;
        }

        /// <summary>
        /// ÚNICA forma de obtener puntos de una sección (ADR 003, "Sin grietas"): el segmento y el cruce la llaman con el mismo t, así que
        /// los vértices compartidos son idénticos bit a bit. <paramref name="pts"/> recibe los 10 puntos de <c>P*</c>.
        /// </summary>
        public static void CrossSectionAt(in RoadPiece p, float t, in RoadProfile pr, Span<Vector3> pts, out SectionFrame frame)
        {
            Vec2 c = p.Curve.Evaluate(t);
            Vec2 tg = p.Curve.Tangent(t);
            Vec2 r = OffsetCurve.Right(tg);
            float baseY = p.ElevA + (p.ElevB - p.ElevA) * t;
            const float h = RoadProfile.CurbHeight;

            frame.Tangent = new Vector2(tg.X, tg.Z);
            frame.Right = new Vector2(r.X, r.Z);
            frame.BaseY = baseY;
            Vec2 chord = p.Curve.P3 - p.Curve.P0;
            frame.Slope = (p.ElevB - p.ElevA) / Mathf.Max(1f, chord.Length);
            frame.Center = new Vector3(c.X, baseY, c.Z);

            // Inglete: solo en las filas de los extremos y solo si la pieza lo pide.
            float k = t == p.T0 ? p.MitreStart : (t == p.T1 ? p.MitreEnd : 0f);
            Vec2 sh = k != 0f ? tg * k : Vec2.Zero;

            pts[PLeftOuter] = At(c, r, sh, pr.LeftOuter, baseY + h);
            pts[PLeftCurbTop] = At(c, r, sh, pr.LeftCurb, baseY + h);
            pts[PLeftCurbBottom] = At(c, r, sh, pr.LeftCurb, baseY);
            if (pr.HasMedian)
            {
                pts[PMedLBottom] = At(c, r, sh, pr.MedianL, baseY);
                pts[PMedLTop] = At(c, r, sh, pr.MedianL, baseY + h);
                pts[PMedRTop] = At(c, r, sh, pr.MedianR, baseY + h);
                pts[PMedRBottom] = At(c, r, sh, pr.MedianR, baseY);
            }
            else
            {
                pts[PMedLBottom] = pts[PMedLTop] = pts[PMedRTop] = pts[PMedRBottom] = new Vector3(c.X, baseY, c.Z);
            }
            pts[PRightCurbBottom] = At(c, r, sh, pr.RightCurb, baseY);
            pts[PRightCurbTop] = At(c, r, sh, pr.RightCurb, baseY + h);
            pts[PRightOuter] = At(c, r, sh, pr.RightOuter, baseY + h);
        }

        // sh = k x tangente: el desplazamiento por inglete crece con el lateral del punto.
        private static Vector3 At(Vec2 c, Vec2 r, Vec2 sh, float lateral, float y) =>
            new Vector3(c.X + r.X * lateral + sh.X * lateral, y, c.Z + r.Z * lateral + sh.Z * lateral);

        public static Vector3 UpNormal(in SectionFrame f) => new Vector3(-f.Slope * f.Tangent.x, 1f, -f.Slope * f.Tangent.y).normalized;

        private static float LateralOf(in RoadProfile pr, int pointIndex)
        {
            switch (pointIndex)
            {
                case PLeftOuter: return pr.LeftOuter;
                case PLeftCurbTop:
                case PLeftCurbBottom: return pr.LeftCurb;
                case PMedLBottom:
                case PMedLTop: return pr.MedianL;
                case PMedRTop:
                case PMedRBottom: return pr.MedianR;
                case PRightCurbBottom:
                case PRightCurbTop: return pr.RightCurb;
                default: return pr.RightOuter;
            }
        }

        private enum NormalKind : byte { Up, Right, Left }

        /// <summary>Escribe la malla del segmento (calzada, bordillos, aceras, mediana, cierres de mediana y líneas de detención) en <paramref name="dst"/>.</summary>
        public static void BuildSegment(in RoadPiece p, in RoadProfile pr, MeshBuffers dst)
        {
            Span<float> rowT = stackalloc float[MaxRows];
            Span<float> rowS = stackalloc float[MaxRows];
            Span<Vector3> pts = stackalloc Vector3[MaxRows * PointCount];
            Span<SectionFrame> frames = stackalloc SectionFrame[MaxRows];
            int rows = SampleRows(p, rowT, rowS);
            for (int r = 0; r < rows; r++)
                CrossSectionAt(p, rowT[r], pr, pts.Slice(r * PointCount, PointCount), out frames[r]);

            float seg = p.SegmentIndex;
            RoadAtlasLayout.URange(pr.Column, out float tu0, out float tu1);
            float curbU = RoadAtlasLayout.UCenter(RoadAtlasLayout.ColCurb);
            float width = pr.TotalWidth;

            Span<Vector3> a = stackalloc Vector3[MaxRows];
            Span<Vector3> b = stackalloc Vector3[MaxRows];
            Span<Vector3> n = stackalloc Vector3[MaxRows];
            Span<float> v = stackalloc float[MaxRows];
            for (int r = 0; r < rows; r++) v[r] = rowS[r] / RoadAtlasLayout.TileMeters;

            Strip(dst, pts, frames, rows, PLeftOuter, PLeftCurbTop, NormalKind.Up, pr, tu0, tu1, width, false, 0, seg, a, b, n, v);
            Strip(dst, pts, frames, rows, PLeftCurbTop, PLeftCurbBottom, NormalKind.Right, pr, curbU, curbU, width, true, 0, seg, a, b, n, v);
            if (pr.HasMedian)
            {
                Strip(dst, pts, frames, rows, PLeftCurbBottom, PMedLBottom, NormalKind.Up, pr, tu0, tu1, width, false, MeshBuffers.RoadMask, seg, a, b, n, v);
                Strip(dst, pts, frames, rows, PMedLBottom, PMedLTop, NormalKind.Left, pr, curbU, curbU, width, true, 0, seg, a, b, n, v);
                Strip(dst, pts, frames, rows, PMedLTop, PMedRTop, NormalKind.Up, pr, tu0, tu1, width, false, 0, seg, a, b, n, v);
                Strip(dst, pts, frames, rows, PMedRTop, PMedRBottom, NormalKind.Right, pr, curbU, curbU, width, true, 0, seg, a, b, n, v);
                Strip(dst, pts, frames, rows, PMedRBottom, PRightCurbBottom, NormalKind.Up, pr, tu0, tu1, width, false, MeshBuffers.RoadMask, seg, a, b, n, v);
            }
            else
            {
                Strip(dst, pts, frames, rows, PLeftCurbBottom, PRightCurbBottom, NormalKind.Up, pr, tu0, tu1, width, false, MeshBuffers.RoadMask, seg, a, b, n, v);
            }
            Strip(dst, pts, frames, rows, PRightCurbBottom, PRightCurbTop, NormalKind.Left, pr, curbU, curbU, width, true, 0, seg, a, b, n, v);
            Strip(dst, pts, frames, rows, PRightCurbTop, PRightOuter, NormalKind.Up, pr, tu0, tu1, width, false, 0, seg, a, b, n, v);

            if (pr.HasMedian)
            {
                if ((p.Flags & RoadPieceFlags.MedianCapStart) != 0) MedianCap(dst, pts.Slice(0, PointCount), frames[0], false, curbU, seg);
                if ((p.Flags & RoadPieceFlags.MedianCapEnd) != 0) MedianCap(dst, pts.Slice((rows - 1) * PointCount, PointCount), frames[rows - 1], true, curbU, seg);
            }
            if ((p.Flags & RoadPieceFlags.StopLineStart) != 0 && pr.HasBackward) StopLine(dst, p, p.T0, 1f, pr.BwdMin, pr.BwdMax, seg);
            if ((p.Flags & RoadPieceFlags.StopLineEnd) != 0 && pr.HasForward) StopLine(dst, p, p.T1, -1f, pr.FwdMin, pr.FwdMax, seg);
        }

        private static void Strip(MeshBuffers dst, ReadOnlySpan<Vector3> pts, ReadOnlySpan<SectionFrame> frames, int rows, int ia, int ib,
            NormalKind kind, in RoadProfile pr, float u0, float u1, float width, bool flatU, sbyte mask, float seg,
            Span<Vector3> a, Span<Vector3> b, Span<Vector3> n, ReadOnlySpan<float> v)
        {
            for (int r = 0; r < rows; r++)
            {
                a[r] = pts[r * PointCount + ia];
                b[r] = pts[r * PointCount + ib];
                SectionFrame f = frames[r];
                switch (kind)
                {
                    case NormalKind.Up: n[r] = UpNormal(f); break;
                    case NormalKind.Right: n[r] = new Vector3(f.Right.x, 0f, f.Right.y); break;
                    default: n[r] = new Vector3(-f.Right.x, 0f, -f.Right.y); break;
                }
            }
            float ua, ub;
            if (flatU) { ua = u0; ub = u1; }
            else
            {
                float l0 = (LateralOf(pr, ia) - pr.LeftOuter) / width;
                float l1 = (LateralOf(pr, ib) - pr.LeftOuter) / width;
                ua = u0 + (u1 - u0) * l0;
                ub = u0 + (u1 - u0) * l1;
            }
            dst.AddStrip(a.Slice(0, rows), b.Slice(0, rows), n.Slice(0, rows), ua, ub, v.Slice(0, rows), mask, seg);
        }

        // Cara vertical que cierra la mediana en un extremo; mira hacia fuera del segmento.
        private static void MedianCap(MeshBuffers dst, ReadOnlySpan<Vector3> row, in SectionFrame f, bool atEnd, float curbU, float seg)
        {
            Vector3 dir = new Vector3(f.Tangent.x, 0f, f.Tangent.y) * (atEnd ? 1f : -1f);
            int i0 = dst.AddVertex(row[PMedLTop], dir, 0, curbU, 0f, seg);
            int i1 = dst.AddVertex(row[PMedRTop], dir, 0, curbU, 0f, seg);
            int i2 = dst.AddVertex(row[PMedLBottom], dir, 0, curbU, 0.01f, seg);
            int i3 = dst.AddVertex(row[PMedRBottom], dir, 0, curbU, 0.01f, seg);
            dst.AddQuad(i0, i1, i2, i3, dir);
        }

        // Línea de detención: cuad pintado sobre la calzada, antes de la sección final, solo sobre los carriles que entran en el cruce.
        // dirSign: +1 si el cruce queda antes de T0 (la línea va hacia t creciente), −1 si queda después de T1.
        private static void StopLine(MeshBuffers dst, in RoadPiece p, float tEnd, float dirSign, float latMin, float latMax, float seg)
        {
            float paint = RoadAtlasLayout.UCenter(RoadAtlasLayout.ColPaint);
            Vec2 d1 = p.Curve.Derivative(tEnd);
            float speed = Mathf.Max(0.5f, d1.Length);
            float tNear = tEnd + dirSign * StopLineOffset / speed;
            float tFar = tEnd + dirSign * (StopLineOffset + StopLineThickness) / speed;
            const float margin = 0.12f;
            float lo = latMin + margin, hi = latMax - margin;
            if (hi <= lo) return;
            Span<Vector3> q = stackalloc Vector3[4];
            for (int k = 0; k < 2; k++)
            {
                float t = k == 0 ? tNear : tFar;
                Vec2 c = p.Curve.Evaluate(t);
                Vec2 r = OffsetCurve.Right(p.Curve.Tangent(t));
                float baseY = p.ElevA + (p.ElevB - p.ElevA) * t + PaintLift;
                q[k * 2] = new Vector3(c.X + r.X * lo, baseY, c.Z + r.Z * lo);
                q[k * 2 + 1] = new Vector3(c.X + r.X * hi, baseY, c.Z + r.Z * hi);
            }
            int i0 = dst.AddVertex(q[0], Vector3.up, 0, paint, 0.5f, seg);
            int i1 = dst.AddVertex(q[1], Vector3.up, 0, paint, 0.5f, seg);
            int i2 = dst.AddVertex(q[2], Vector3.up, 0, paint, 0.5f, seg);
            int i3 = dst.AddVertex(q[3], Vector3.up, 0, paint, 0.5f, seg);
            dst.AddQuad(i0, i1, i2, i3, Vector3.up);
        }
    }
}
