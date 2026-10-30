using System;

namespace Urbe.Sim.Geometry
{
    /// <summary>Par de parametros (tA, tB) de una interseccion entre dos curvas.</summary>
    public readonly struct CurveHit
    {
        public readonly float TA;
        public readonly float TB;

        public CurveHit(float ta, float tb)
        {
            TA = ta;
            TB = tb;
        }
    }

    public static class Intersections
    {
        public const float Tolerance = 0.01f;
        private const float DedupeDistance = 0.05f;
        private const int MaxDepth = 40;
        private const int NodeBudget = 50000;

        /// <summary>Interseccion segmento a0-a1 con b0-b1. tA y tB en [0,1]. Paralelos: false.</summary>
        public static bool SegmentSegment(Vec2 a0, Vec2 a1, Vec2 b0, Vec2 b1, out float tA, out float tB)
        {
            tA = 0f;
            tB = 0f;
            Vec2 r = a1 - a0;
            Vec2 s = b1 - b0;
            float denom = Vec2.Cross(r, s);
            if (Math.Abs(denom) < 1e-12f) return false;
            Vec2 qp = b0 - a0;
            float ta = Vec2.Cross(qp, s) / denom;
            float tb = Vec2.Cross(qp, r) / denom;
            if (ta < 0f || ta > 1f || tb < 0f || tb > 1f) return false;
            tA = ta;
            tB = tb;
            return true;
        }

        /// <summary>
        /// Intersecciones curva-curva por subdivision recursiva con AABB hasta 0,01 m.
        /// Escribe hasta results.Length pares sin asignar memoria y devuelve cuantos hay.
        /// </summary>
        public static int CurveCurve(in Bezier3 a, in Bezier3 b, Span<CurveHit> results)
        {
            int count = 0;
            int budget = NodeBudget;
            Recurse(a, a, 0f, 1f, b, 0f, 1f, results, ref count, ref budget, 0);
            return count;
        }

        private static void Recurse(in Bezier3 root, in Bezier3 a, float a0, float a1, in Bezier3 b, float b0, float b1,
            Span<CurveHit> results, ref int count, ref int budget, int depth)
        {
            if (count >= results.Length || budget <= 0) return;
            budget--;

            Aabb2 ba = a.Bounds;
            Aabb2 bb = b.Bounds;
            if (!ba.Overlaps(bb)) return;

            Vec2 sa = ba.Size;
            Vec2 sb = bb.Size;
            bool smallA = sa.X <= Tolerance && sa.Z <= Tolerance;
            bool smallB = sb.X <= Tolerance && sb.Z <= Tolerance;

            if ((smallA && smallB) || depth >= MaxDepth)
            {
                AddHit(root, 0.5f * (a0 + a1), 0.5f * (b0 + b1), results, ref count);
                return;
            }

            if (smallA)
            {
                b.Split(0.5f, out Bezier3 b1c, out Bezier3 b2c);
                float bm = 0.5f * (b0 + b1);
                Recurse(root, a, a0, a1, b1c, b0, bm, results, ref count, ref budget, depth + 1);
                Recurse(root, a, a0, a1, b2c, bm, b1, results, ref count, ref budget, depth + 1);
                return;
            }

            if (smallB)
            {
                a.Split(0.5f, out Bezier3 a1c, out Bezier3 a2c);
                float am = 0.5f * (a0 + a1);
                Recurse(root, a1c, a0, am, b, b0, b1, results, ref count, ref budget, depth + 1);
                Recurse(root, a2c, am, a1, b, b0, b1, results, ref count, ref budget, depth + 1);
                return;
            }

            a.Split(0.5f, out Bezier3 aL, out Bezier3 aR);
            b.Split(0.5f, out Bezier3 bL, out Bezier3 bR);
            float amid = 0.5f * (a0 + a1);
            float bmid = 0.5f * (b0 + b1);
            Recurse(root, aL, a0, amid, bL, b0, bmid, results, ref count, ref budget, depth + 1);
            Recurse(root, aL, a0, amid, bR, bmid, b1, results, ref count, ref budget, depth + 1);
            Recurse(root, aR, amid, a1, bL, b0, bmid, results, ref count, ref budget, depth + 1);
            Recurse(root, aR, amid, a1, bR, bmid, b1, results, ref count, ref budget, depth + 1);
        }

        private static void AddHit(in Bezier3 root, float ta, float tb, Span<CurveHit> results, ref int count)
        {
            Vec2 p = root.Evaluate(ta);
            for (int i = 0; i < count; i++)
            {
                if (Vec2.Distance(root.Evaluate(results[i].TA), p) < DedupeDistance) return;
            }
            results[count++] = new CurveHit(ta, tb);
        }
    }
}
