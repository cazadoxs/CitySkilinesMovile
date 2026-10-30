using System;

namespace Urbe.Sim.Geometry
{
    /// <summary>Curva de Bezier cubica en el plano (X, Z).</summary>
    public readonly struct Bezier3
    {
        public readonly Vec2 P0;
        public readonly Vec2 P1;
        public readonly Vec2 P2;
        public readonly Vec2 P3;

        public Bezier3(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3)
        {
            P0 = p0;
            P1 = p1;
            P2 = p2;
            P3 = p3;
        }

        /// <summary>Recta a-b como cubica (controles a 1/3 y 2/3): velocidad constante.</summary>
        public static Bezier3 FromLine(Vec2 a, Vec2 b)
        {
            return new Bezier3(a, Vec2.Lerp(a, b, 1f / 3f), Vec2.Lerp(a, b, 2f / 3f), b);
        }

        public Vec2 Evaluate(float t)
        {
            float u = 1f - t;
            float b0 = u * u * u;
            float b1 = 3f * u * u * t;
            float b2 = 3f * u * t * t;
            float b3 = t * t * t;
            return new Vec2(
                b0 * P0.X + b1 * P1.X + b2 * P2.X + b3 * P3.X,
                b0 * P0.Z + b1 * P1.Z + b2 * P2.Z + b3 * P3.Z);
        }

        /// <summary>Primera derivada respecto a t (no normalizada).</summary>
        public Vec2 Derivative(float t)
        {
            float u = 1f - t;
            float a = 3f * u * u;
            float b = 6f * u * t;
            float c = 3f * t * t;
            return new Vec2(
                a * (P1.X - P0.X) + b * (P2.X - P1.X) + c * (P3.X - P2.X),
                a * (P1.Z - P0.Z) + b * (P2.Z - P1.Z) + c * (P3.Z - P2.Z));
        }

        /// <summary>Segunda derivada respecto a t.</summary>
        public Vec2 SecondDerivative(float t)
        {
            float u = 1f - t;
            Vec2 d0 = (P2 - P1 * 2f + P0) * (6f * u);
            Vec2 d1 = (P3 - P2 * 2f + P1) * (6f * t);
            return d0 + d1;
        }

        /// <summary>Tangente unitaria. Si la derivada es nula (punto de control coincidente) usa un fallback.</summary>
        public Vec2 Tangent(float t)
        {
            Vec2 d = Derivative(t);
            if (d.LengthSquared < 1e-12f)
            {
                // Derivada degenerada en un extremo: usa la cuerda mas cercana disponible.
                d = t < 0.5f ? (P2 - P0) : (P3 - P1);
                if (d.LengthSquared < 1e-12f) d = P3 - P0;
            }
            return d.Normalized;
        }

        /// <summary>De Casteljau: divide en [0,t] y [t,1].</summary>
        public void Split(float t, out Bezier3 a, out Bezier3 b)
        {
            Vec2 p01 = Vec2.Lerp(P0, P1, t);
            Vec2 p12 = Vec2.Lerp(P1, P2, t);
            Vec2 p23 = Vec2.Lerp(P2, P3, t);
            Vec2 p012 = Vec2.Lerp(p01, p12, t);
            Vec2 p123 = Vec2.Lerp(p12, p23, t);
            Vec2 m = Vec2.Lerp(p012, p123, t);
            a = new Bezier3(P0, p01, p012, m);
            b = new Bezier3(m, p123, p23, P3);
        }

        /// <summary>AABB de los puntos de control: cota conservadora de la curva.</summary>
        public Aabb2 Bounds
        {
            get
            {
                float minX = Math.Min(Math.Min(P0.X, P1.X), Math.Min(P2.X, P3.X));
                float maxX = Math.Max(Math.Max(P0.X, P1.X), Math.Max(P2.X, P3.X));
                float minZ = Math.Min(Math.Min(P0.Z, P1.Z), Math.Min(P2.Z, P3.Z));
                float maxZ = Math.Max(Math.Max(P0.Z, P1.Z), Math.Max(P2.Z, P3.Z));
                return new Aabb2(new Vec2(minX, minZ), new Vec2(maxX, maxZ));
            }
        }

        /// <summary>Parametro t en [0,1] del punto de la curva mas cercano a <paramref name="point"/>.</summary>
        public float ClosestT(Vec2 point)
        {
            const int Coarse = 16;
            float bestT = 0f;
            float bestD = float.MaxValue;
            for (int i = 0; i <= Coarse; i++)
            {
                float t = (float)i / Coarse;
                float d = (Evaluate(t) - point).LengthSquared;
                if (d < bestD)
                {
                    bestD = d;
                    bestT = t;
                }
            }

            // Newton sobre f(t) = (B(t)-p) . B'(t), acotado al intervalo grueso vecino.
            float lo = Math.Max(0f, bestT - 1f / Coarse);
            float hi = Math.Min(1f, bestT + 1f / Coarse);
            float x = bestT;
            for (int it = 0; it < 8; it++)
            {
                Vec2 diff = Evaluate(x) - point;
                Vec2 d1 = Derivative(x);
                float f = Vec2.Dot(diff, d1);
                float df = Vec2.Dot(d1, d1) + Vec2.Dot(diff, SecondDerivative(x));
                if (df <= 1e-9f) break;
                float nx = x - f / df;
                if (nx < lo) nx = lo;
                else if (nx > hi) nx = hi;
                if (Math.Abs(nx - x) < 1e-7f)
                {
                    x = nx;
                    break;
                }
                x = nx;
            }

            return (Evaluate(x) - point).LengthSquared <= bestD ? x : bestT;
        }
    }
}
