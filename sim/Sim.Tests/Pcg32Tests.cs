using Urbe.Sim.Random;
using Xunit;

namespace Urbe.Sim.Tests
{
    public class Pcg32Tests
    {
        [Fact]
        public void SameSeedSameSequence()
        {
            var a = new Pcg32(123, 7);
            var b = new Pcg32(123, 7);
            for (int i = 0; i < 1000; i++) Assert.Equal(a.NextUInt(), b.NextUInt());
        }

        [Fact]
        public void DifferentSeedsDiffer()
        {
            var a = new Pcg32(1, 7);
            var b = new Pcg32(2, 7);
            bool diff = false;
            for (int i = 0; i < 16; i++) diff |= a.NextUInt() != b.NextUInt();
            Assert.True(diff);
        }

        [Fact]
        public void ReferenceVector()
        {
            var r = new Pcg32(42, 54);
            uint[] expected = { 0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e };
            foreach (uint e in expected) Assert.Equal(e, r.NextUInt());
        }

        [Fact]
        public void StateRoundTrip()
        {
            var a = new Pcg32(5, 9);
            a.NextUInt();
            var b = Pcg32.FromState(a.State, a.Increment);
            for (int i = 0; i < 10; i++) Assert.Equal(a.NextUInt(), b.NextUInt());
        }

        [Fact]
        public void NextIntInRange()
        {
            var r = new Pcg32(1, 1);
            for (int i = 0; i < 10000; i++)
            {
                int v = r.NextInt(7);
                Assert.InRange(v, 0, 6);
                int w = r.NextInt(-5, 5);
                Assert.InRange(w, -5, 4);
                float f = r.NextFloat();
                Assert.True(f >= 0f && f < 1f);
                double d = r.NextDouble();
                Assert.True(d >= 0.0 && d < 1.0);
            }
        }

        [Fact]
        public void RoughlyUniform()
        {
            var r = new Pcg32(99, 3);
            const int k = 10, n = 100000;
            var counts = new int[k];
            for (int i = 0; i < n; i++) counts[r.NextInt(k)]++;
            double exp = n / (double)k, chi = 0;
            for (int i = 0; i < k; i++) chi += (counts[i] - exp) * (counts[i] - exp) / exp;
            Assert.True(chi < 40.0, "chi2=" + chi);
        }
    }
}
