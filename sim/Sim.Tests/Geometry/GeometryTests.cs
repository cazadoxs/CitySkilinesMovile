using System;
using Urbe.Sim.Geometry;
using Xunit;

namespace Urbe.Sim.Tests.Geometry
{
    public class GeometryTests
    {
        private static double RefLength(in Bezier3 c, int steps = 10000)
        {
            double sum = 0;
            double h = 1.0 / steps;
            for (int i = 0; i < steps; i++)
            {
                double t0 = i * h, t1 = t0 + h;
                Vec2 d0 = c.Derivative((float)t0), d1 = c.Derivative((float)((t0 + t1) / 2)), d2 = c.Derivative((float)t1);
                sum += h / 6.0 * (Math.Sqrt((double)d0.X * d0.X + (double)d0.Z * d0.Z)
                    + 4 * Math.Sqrt((double)d1.X * d1.X + (double)d1.Z * d1.Z)
                    + Math.Sqrt((double)d2.X * d2.X + (double)d2.Z * d2.Z));
            }
            return sum;
        }

        private static Bezier3 Quarter(float r)
        {
            const float k = 0.5523f;
            return new Bezier3(new Vec2(r, 0), new Vec2(r, k * r), new Vec2(k * r, r), new Vec2(0, r));
        }

        [Fact]
        public void Vec2_Basics()
        {
            var a = new Vec2(3, 4);
            Assert.Equal(5f, a.Length, 5);
            Assert.Equal(new Vec2(-4, 3), a.Perp);
            Assert.Equal(1f, Vec2.Cross(new Vec2(1, 0), new Vec2(0, 1)));
            Assert.Equal(11f, Vec2.Dot(a, new Vec2(1, 2)));
            Assert.True(a.Normalized.ApproxEquals(new Vec2(0.6f, 0.8f), 1e-6f));
            Assert.Equal(new Vec2(1.5f, 2f), Vec2.Lerp(Vec2.Zero, a, 0.5f));
            Assert.Equal(Vec2.Zero, Vec2.Zero.Normalized);
        }

        [Fact]
        public void Line100_LengthIs100()
        {
            var table = new ArcLengthTable(Bezier3.FromLine(new Vec2(0, 0), new Vec2(100, 0)));
            Assert.Equal(100f, table.TotalLength, 3);
            Assert.Equal(0.25f, table.TAtDistance(25f), 4);
        }

        [Fact]
        public void QuarterCircle_Length()
        {
            float r = 50f;
            var table = new ArcLengthTable(Quarter(r));
            double expected = Math.PI * r / 2;
            Assert.True(Math.Abs(table.TotalLength - expected) / expected < 0.001);
        }

        public static readonly object[][] Curves =
        {
            new object[] { new Bezier3(new Vec2(0, 0), new Vec2(40, 60), new Vec2(60, -60), new Vec2(100, 0)) }, // S
            new object[] { new Bezier3(new Vec2(0, 0), new Vec2(120, 20), new Vec2(-20, 20), new Vec2(100, 0)) }, // cerrada
            new object[] { new Bezier3(new Vec2(0, 0), new Vec2(80, 0), new Vec2(80, 0), new Vec2(0, 10)) }, // horquilla
            new object[] { Quarter(30f) },
        };

        [Theory]
        [MemberData(nameof(Curves))]
        public void Table_MatchesReference(Bezier3 c)
        {
            double expected = RefLength(c);
            var table = new ArcLengthTable(c);
            Assert.True(Math.Abs(table.TotalLength - expected) / expected < 0.001);
            // Tambien con solo 16 muestras.
            var t16 = new ArcLengthTable(c, 16);
            Assert.True(Math.Abs(t16.TotalLength - expected) / expected < 0.001);
        }

        [Theory]
        [MemberData(nameof(Curves))]
        public void TAtDistance_MonotonicAndConsistent(Bezier3 c)
        {
            var table = new ArcLengthTable(c);
            float total = table.TotalLength;
            float prev = -1f;
            for (int i = 0; i <= 100; i++)
            {
                float d = total * i / 100f;
                float t = table.TAtDistance(d);
                Assert.True(t >= prev);
                prev = t;
                Assert.True(Math.Abs(table.DistanceAtT(t) - d) < 0.005f * Math.Max(1f, total / 100f) + 0.01f);
            }
            Assert.Equal(0f, table.TAtDistance(-5f));
            Assert.Equal(1f, table.TAtDistance(total + 5f));
        }

        [Fact]
        public void SharedBuffer_StaticVariant()
        {
            var c1 = Bezier3.FromLine(new Vec2(0, 0), new Vec2(10, 0));
            var c2 = Quarter(20f);
            var shared = new float[64];
            ArcLengthTable.Build(c1, new Span<float>(shared, 0, 16));
            ArcLengthTable.Build(c2, new Span<float>(shared, 16, 24));
            Assert.Equal(10f, ArcLengthTable.TotalLengthOf(new ReadOnlySpan<float>(shared, 0, 16)), 3);
            var s2 = new ReadOnlySpan<float>(shared, 16, 24);
            Assert.True(Math.Abs(ArcLengthTable.TotalLengthOf(s2) - Math.PI * 10) / (Math.PI * 10) < 0.001);
            float t = ArcLengthTable.TAtDistance(c2, s2, ArcLengthTable.TotalLengthOf(s2) / 2);
            Assert.Equal(0.5f, t, 2);
            var inst = new ArcLengthTable(c2, shared, 40, 24);
            Assert.Equal(ArcLengthTable.TotalLengthOf(s2), inst.TotalLength, 3);
        }

        [Fact]
        public void Split_PreservesCurve()
        {
            var c = new Bezier3(new Vec2(0, 0), new Vec2(40, 60), new Vec2(60, -60), new Vec2(100, 0));
            c.Split(0.3f, out var a, out var b);
            for (int i = 0; i <= 10; i++)
            {
                float u = i / 10f;
                Assert.True(a.Evaluate(u).ApproxEquals(c.Evaluate(0.3f * u), 1e-3f));
                Assert.True(b.Evaluate(u).ApproxEquals(c.Evaluate(0.3f + 0.7f * u), 1e-3f));
            }
        }

        [Fact]
        public void Offset_OfLine_IsParallelAtExactDistance()
        {
            // Avance hacia +X; la derecha es -Z (Perp izquierda = +Z).
            var c = Bezier3.FromLine(new Vec2(0, 0), new Vec2(100, 0));
            var table = new ArcLengthTable(c);
            Vec2 p = OffsetCurve.PointAtDistance(c, table, 40f, 3.5f, out Vec2 tan);
            Assert.True(p.ApproxEquals(new Vec2(40f, -3.5f), 1e-3f));
            Assert.True(tan.ApproxEquals(new Vec2(1, 0), 1e-5f));
            Vec2 q = OffsetCurve.PointAtDistance(c, table, 40f, -3.5f);
            Assert.True(q.ApproxEquals(new Vec2(40f, 3.5f), 1e-3f));
            Assert.Equal(100f, OffsetCurve.Length(c, 3.5f), 3);
        }

        [Fact]
        public void Offset_OfArc_Length()
        {
            // Giro a la izquierda (antihorario): la derecha es el lado exterior -> radio mayor.
            float r = 50f;
            var c = Quarter(r);
            double right = OffsetCurve.Length(c, 2f);
            double left = OffsetCurve.Length(c, -2f);
            Assert.True(Math.Abs(right - Math.PI / 2 * 52) / (Math.PI / 2 * 52) < 0.002);
            Assert.True(Math.Abs(left - Math.PI / 2 * 48) / (Math.PI / 2 * 48) < 0.002);
        }

        [Fact]
        public void SegmentSegment_Cross()
        {
            Assert.True(Intersections.SegmentSegment(new Vec2(0, 0), new Vec2(10, 10), new Vec2(0, 10), new Vec2(10, 0), out float ta, out float tb));
            Assert.Equal(0.5f, ta, 5);
            Assert.Equal(0.5f, tb, 5);
            Assert.False(Intersections.SegmentSegment(new Vec2(0, 0), new Vec2(1, 0), new Vec2(0, 1), new Vec2(1, 1), out _, out _));
            Assert.False(Intersections.SegmentSegment(new Vec2(0, 0), new Vec2(1, 0), new Vec2(5, -1), new Vec2(5, 1), out _, out _));
        }

        [Fact]
        public void CurveCurve_CrossingX()
        {
            var a = Bezier3.FromLine(new Vec2(0, 0), new Vec2(100, 100));
            var b = Bezier3.FromLine(new Vec2(0, 100), new Vec2(100, 0));
            Span<CurveHit> hits = stackalloc CurveHit[8];
            int n = Intersections.CurveCurve(a, b, hits);
            Assert.Equal(1, n);
            Assert.Equal(0.5f, hits[0].TA, 2);
            Assert.Equal(0.5f, hits[0].TB, 2);
        }

        [Fact]
        public void CurveCurve_CurvedTwoCrossings()
        {
            var a = new Bezier3(new Vec2(0, 0), new Vec2(30, 80), new Vec2(70, 80), new Vec2(100, 0));
            var b = Bezier3.FromLine(new Vec2(0, 30), new Vec2(100, 30));
            Span<CurveHit> hits = stackalloc CurveHit[8];
            int n = Intersections.CurveCurve(a, b, hits);
            Assert.Equal(2, n);
            for (int i = 0; i < n; i++)
                Assert.True(Math.Abs(a.Evaluate(hits[i].TA).Z - 30f) < 0.05f);
        }

        [Fact]
        public void CurveCurve_NoCrossing()
        {
            var a = Bezier3.FromLine(new Vec2(0, 0), new Vec2(100, 0));
            var b = new Bezier3(new Vec2(0, 10), new Vec2(30, 40), new Vec2(70, 40), new Vec2(100, 10));
            Span<CurveHit> hits = stackalloc CurveHit[8];
            Assert.Equal(0, Intersections.CurveCurve(a, b, hits));
        }

        [Fact]
        public void ClosestT_Works()
        {
            var line = Bezier3.FromLine(new Vec2(0, 0), new Vec2(100, 0));
            Assert.Equal(0.3f, line.ClosestT(new Vec2(30, 17)), 3);
            Assert.Equal(0f, line.ClosestT(new Vec2(-20, 5)), 4);
            Assert.Equal(1f, line.ClosestT(new Vec2(150, 5)), 4);

            var c = new Bezier3(new Vec2(0, 0), new Vec2(40, 60), new Vec2(60, -60), new Vec2(100, 0));
            for (int i = 0; i <= 10; i++)
            {
                float t = i / 10f;
                Vec2 p = c.Evaluate(t);
                Assert.True(c.Evaluate(c.ClosestT(p)).ApproxEquals(p, 0.01f));
            }
            // Brute force: ningun muestreo fino mejora el resultado.
            Vec2 q = new Vec2(55, 20);
            float best = (c.Evaluate(c.ClosestT(q)) - q).Length;
            for (int i = 0; i <= 2000; i++)
                Assert.True((c.Evaluate(i / 2000f) - q).Length >= best - 0.01f);
        }

        [Fact]
        public void Bounds_ContainCurve()
        {
            var c = new Bezier3(new Vec2(0, 0), new Vec2(40, 60), new Vec2(60, -60), new Vec2(100, 0));
            var bb = c.Bounds;
            for (int i = 0; i <= 50; i++)
            {
                Vec2 p = c.Evaluate(i / 50f);
                Assert.True(p.X >= bb.Min.X && p.X <= bb.Max.X && p.Z >= bb.Min.Z && p.Z <= bb.Max.Z);
            }
        }
    }
}
