using System;

namespace Urbe.Sim.Geometry
{
    /// <summary>
    /// Tabla de longitud por arco de una Bezier3. Guarda longitudes acumuladas en N muestras
    /// uniformes en t (muestra 0 = 0, muestra N-1 = longitud total), calculadas con
    /// Gauss-Legendre de 5 puntos por intervalo.
    /// Puede vivir en un array compartido (la red viaria guarda muchas tablas en un solo float[]);
    /// las consultas no asignan memoria.
    /// </summary>
    public readonly struct ArcLengthTable
    {
        public const int DefaultSamples = 24;

        private static readonly float[] GlX = { 0f, -0.5384693101056831f, 0.5384693101056831f, -0.9061798459386640f, 0.9061798459386640f };
        private static readonly float[] GlW = { 0.5688888888888889f, 0.4786286704993665f, 0.4786286704993665f, 0.2369268850561891f, 0.2369268850561891f };

        private readonly Bezier3 _curve;
        private readonly float[] _buffer;
        private readonly int _offset;
        private readonly int _count;

        /// <summary>Crea la tabla con un array propio (una sola asignacion).</summary>
        public ArcLengthTable(in Bezier3 curve, int samples = DefaultSamples)
            : this(curve, new float[samples], 0, samples)
        {
        }

        /// <summary>Crea la tabla escribiendo en <c>buffer[offset .. offset+samples)</c>.</summary>
        public ArcLengthTable(in Bezier3 curve, float[] buffer, int offset, int samples)
        {
            if (samples < 2) throw new ArgumentOutOfRangeException(nameof(samples));
            _curve = curve;
            _buffer = buffer;
            _offset = offset;
            _count = samples;
            Build(curve, new Span<float>(buffer, offset, samples));
        }

        public Bezier3 Curve => _curve;
        public int SampleCount => _count;
        public float TotalLength => _buffer[_offset + _count - 1];

        public float TAtDistance(float d, bool refine = true) => TAtDistance(_curve, Samples, d, refine);

        public float DistanceAtT(float t) => DistanceAtT(_curve, Samples, t);

        private ReadOnlySpan<float> Samples => new ReadOnlySpan<float>(_buffer, _offset, _count);

        // ---- Variante estatica sobre Span (tablas en un float[] compartido) ----

        /// <summary>Rellena <paramref name="table"/> (Length &gt;= 2) con longitudes acumuladas.</summary>
        public static void Build(in Bezier3 curve, Span<float> table)
        {
            int n = table.Length;
            table[0] = 0f;
            float acc = 0f;
            for (int i = 1; i < n; i++)
            {
                acc += SegmentLength(curve, (float)(i - 1) / (n - 1), (float)i / (n - 1));
                table[i] = acc;
            }
        }

        public static float TotalLengthOf(ReadOnlySpan<float> table) => table[table.Length - 1];

        /// <summary>Longitud del tramo [t0, t1] por Gauss-Legendre de 5 puntos.</summary>
        public static float SegmentLength(in Bezier3 curve, float t0, float t1)
        {
            float half = 0.5f * (t1 - t0);
            float mid = 0.5f * (t1 + t0);
            float sum = 0f;
            for (int k = 0; k < 5; k++)
                sum += GlW[k] * curve.Derivative(mid + half * GlX[k]).Length;
            return sum * half;
        }

        /// <summary>Longitud acumulada hasta t (preciso: tabla + integracion del resto del intervalo).</summary>
        public static float DistanceAtT(in Bezier3 curve, ReadOnlySpan<float> table, float t)
        {
            int n = table.Length;
            if (t <= 0f) return 0f;
            if (t >= 1f) return table[n - 1];
            float scaled = t * (n - 1);
            int i = (int)scaled;
            if (i >= n - 1) i = n - 2;
            float t0 = (float)i / (n - 1);
            return table[i] + SegmentLength(curve, t0, t);
        }

        /// <summary>t correspondiente a la distancia d (acotada a [0, total]).</summary>
        public static float TAtDistance(in Bezier3 curve, ReadOnlySpan<float> table, float d, bool refine = true)
        {
            int n = table.Length;
            if (d <= 0f) return 0f;
            float total = table[n - 1];
            if (d >= total) return 1f;

            // Busqueda binaria: mayor i con table[i] <= d.
            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (table[mid] <= d) lo = mid; else hi = mid;
            }

            float segLen = table[hi] - table[lo];
            float frac = segLen > 1e-9f ? (d - table[lo]) / segLen : 0f;
            float tLo = (float)lo / (n - 1);
            float tHi = (float)hi / (n - 1);
            float t = tLo + (tHi - tLo) * frac;

            if (refine)
            {
                // Dos pasos de Newton con distancia exacta del intervalo.
                for (int it = 0; it < 2; it++)
                {
                    float err = table[lo] + SegmentLength(curve, tLo, t) - d;
                    float speed = curve.Derivative(t).Length;
                    if (speed < 1e-6f) break;
                    float nt = t - err / speed;
                    if (nt < tLo) nt = tLo; else if (nt > tHi) nt = tHi;
                    t = nt;
                }
            }

            return t;
        }
    }
}
