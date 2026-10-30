using System.Diagnostics;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Xunit;

namespace Urbe.Sim.Tests.Roads
{
    // M1-16, puntos 7 y 15 de la revision M1-13.
    public class EditValidationFastTests
    {
        private static readonly Bezier3 Big = new Bezier3(Kit.V(-70, 0), Kit.V(-25, 6), Kit.V(25, 6), Kit.V(70, 0));

        // Red con un tramo B que coincide con la parte central (t en [0.25, 0.75]) de la curva Big, desplazado offsetZ.
        private static RoadNetwork WithOverlappedMiddle(float offsetZ)
        {
            Big.Split(0.25f, out _, out Bezier3 rest);
            rest.Split(0.5f / 0.75f, out Bezier3 mid, out _);
            var shift = Kit.V(0, offsetZ);
            RoadNetwork n = Kit.New();
            EntityId a = Kit.Node(n, mid.P0.X, mid.P0.Z + offsetZ), b = Kit.Node(n, mid.P3.X, mid.P3.Z + offsetZ);
            Assert.True(n.AddSegmentRaw(a, b, new Bezier3(mid.P0 + shift, mid.P1 + shift, mid.P2 + shift, mid.P3 + shift), RoadTypeIds.Street2).IsValid);
            return n;
        }

        private static SegmentRequest DrawBig() => new SegmentRequest(Big.P0, Big.P1, Big.P2, Big.P3, RoadTypeIds.Street2);

        [Theory]
        [InlineData(0f)]
        [InlineData(0.004f)]
        [InlineData(0.15f)]
        public void Drawing_over_an_existing_street_is_rejected_fast(float offsetZ)
        {
            RoadNetwork n = WithOverlappedMiddle(offsetZ);
            SegmentRequest over = DrawBig();
            ValidationResult r = n.CanAddSegment(over);
            Assert.Equal(ValidationResult.AngleTooSharp, r);
            int reps = 20;
            double best = double.MaxValue;
            for (int round = 0; round < 5; round++)
            {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < reps; i++) n.CanAddSegment(over);
                sw.Stop();
                best = System.Math.Min(best, sw.Elapsed.TotalMilliseconds / reps);
            }
            Assert.True(best < 0.5, "CanAddSegment sobre una calle existente (" + r + ") tarda " + best.ToString("F3") + " ms");
        }

        [Fact]
        public void Crossing_at_a_legal_shallow_angle_is_not_taken_for_an_overlap()
        {
            RoadNetwork n = Kit.New();
            Kit.SegAt(n, -100, 0, 100, 0, RoadTypeIds.Street2);
            Assert.Equal(ValidationResult.Ok,
                n.CanAddSegment(SegmentRequest.Straight(Kit.V(-80, -46), Kit.V(80, 46), RoadTypeIds.Street2))); // ~30 grados con el eje (minimo legal 25)
        }

        [Fact]
        public void Legit_crossing_at_minimum_angle_is_still_accepted()
        {
            RoadNetwork n = Kit.New();
            Kit.SegAt(n, -100, 0, 100, 0, RoadTypeIds.Street2);
            // ~30 grados respecto al eje: valido.
            float dx = 60f, dz = dx * 0.577f;
            Assert.Equal(ValidationResult.Ok,
                n.CanAddSegment(SegmentRequest.Straight(Kit.V(-dx, -dz), Kit.V(dx, dz), RoadTypeIds.Street2)));
        }

        [Fact]
        public void More_than_buffer_candidates_is_reported_as_too_many_crossings()
        {
            RoadNetwork n = Kit.New();
            int added = 0;
            // Muchos segmentos cortos en el triangulo superior-izquierdo de la caja de la curva nueva, lejos de ella.
            for (float z = 40f; z <= 140f && added < 1300; z += 0.4f)
                for (float x = 0f; x <= 126f && added < 1300; x += 9f)
                {
                    if (z - x < 25f) continue;
                    if (Kit.SegAt(n, x, z, x + 8f, z, RoadTypeIds.Street2).IsValid) added++;
                }
            Assert.True(added > 1100, "segmentos creados: " + added);
            var req = SegmentRequest.Straight(Kit.V(0, 0), Kit.V(140, 140), RoadTypeIds.Street2);
            Assert.True(n.QuerySegments(new Aabb2(Kit.V(0, 0), Kit.V(140, 140)), new EntityId[1024]) >= 1024);
            Assert.Equal(ValidationResult.TooManyCrossings, n.CanAddSegment(req));
        }
    }
}
