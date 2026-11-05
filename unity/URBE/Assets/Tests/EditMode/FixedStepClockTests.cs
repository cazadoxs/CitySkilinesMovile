using NUnit.Framework;
using Urbe.Game.Sim;

namespace Urbe.Game.Tests
{
    public class FixedStepClockTests
    {
        [Test]
        public void A60Fps_X1_Da10TicksPorSegundo()
        {
            var c = new FixedStepClock(0.1f);
            int ticks = 0;
            for (int i = 0; i < 600; i++) ticks += c.Advance(1f / 60f, 1f); // 10 s
            Assert.That(ticks, Is.InRange(99, 100));
        }

        [TestCase(1f, 100)]
        [TestCase(2f, 200)]
        [TestCase(3f, 300)]
        public void VelocidadMultiplicaLosTicks(float speed, int expected)
        {
            var c = new FixedStepClock(0.1f, 100);
            int ticks = 0;
            for (int i = 0; i < 600; i++) ticks += c.Advance(1f / 60f, speed);
            Assert.That(ticks, Is.InRange(expected - 1, expected));
        }

        [Test]
        public void Pausa_NoAvanzaNiTocaElAlpha()
        {
            var c = new FixedStepClock(0.1f);
            c.Advance(0.05f, 1f);
            float a = c.Alpha;
            for (int i = 0; i < 100; i++) Assert.That(c.Advance(0.016f, 0f), Is.EqualTo(0));
            Assert.That(c.Alpha, Is.EqualTo(a).Within(1e-6f));
            Assert.That(a, Is.EqualTo(0.5f).Within(1e-4f));
        }

        [Test]
        public void FotogramaLargo_SeLimitaYSeCuentanLosDescartados()
        {
            var c = new FixedStepClock(0.1f, 8);
            int ticks = c.Advance(5f, 1f); // 50 ticks de golpe
            Assert.That(ticks, Is.EqualTo(8));
            Assert.That(c.DroppedTicks, Is.InRange(41, 42)); // 5 s / 0,1 s = 49 o 50 según el redondeo de float
            Assert.That(c.Alpha, Is.InRange(0f, 1f));
        }

        [Test]
        public void AlphaSiempreEnRango()
        {
            var c = new FixedStepClock(0.1f);
            for (int i = 0; i < 1000; i++)
            {
                c.Advance(0.013f + (i % 7) * 0.002f, 2f);
                Assert.That(c.Alpha, Is.InRange(0f, 1f));
            }
        }
    }
}
