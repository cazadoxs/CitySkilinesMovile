using System;

namespace Urbe.Sim.Geometry
{
    /// <summary>
    /// Helpers de curva desplazada lateralmente (carriles). Offset positivo = derecha segun el
    /// sentido de avance (se circula por la derecha); la derecha es -Perp de la tangente.
    /// </summary>
    public static class OffsetCurve
    {
        /// <summary>Normal derecha unitaria de una tangente unitaria.</summary>
        public static Vec2 Right(Vec2 tangent) => new Vec2(tangent.Z, -tangent.X);

        public static Vec2 PointAt(in Bezier3 curve, float t, float offset)
        {
            return curve.Evaluate(t) + Right(curve.Tangent(t)) * offset;
        }

        public static Vec2 PointAtDistance(in Bezier3 curve, in ArcLengthTable table, float d, float offset)
        {
            return PointAt(curve, table.TAtDistance(d), offset);
        }

        /// <summary>Punto desplazado y tangente (la tangente del desplazamiento coincide con la de la curva base).</summary>
        public static Vec2 PointAtDistance(in Bezier3 curve, in ArcLengthTable table, float d, float offset, out Vec2 tangent)
        {
            float t = table.TAtDistance(d);
            tangent = curve.Tangent(t);
            return curve.Evaluate(t) + Right(tangent) * offset;
        }

        public static Vec2 TangentAtDistance(in Bezier3 curve, in ArcLengthTable table, float d)
        {
            return curve.Tangent(table.TAtDistance(d));
        }

        /// <summary>Longitud aproximada de la curva desplazada, como polilinea de <paramref name="samples"/> tramos uniformes en t.</summary>
        public static float Length(in Bezier3 curve, float offset, int samples = 32)
        {
            if (samples < 1) samples = 1;
            Vec2 prev = PointAt(curve, 0f, offset);
            float sum = 0f;
            for (int i = 1; i <= samples; i++)
            {
                Vec2 p = PointAt(curve, (float)i / samples, offset);
                sum += Vec2.Distance(prev, p);
                prev = p;
            }
            return sum;
        }
    }
}
