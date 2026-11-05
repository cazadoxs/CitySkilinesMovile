using NUnit.Framework;
using Urbe.Game.Sim;
using Urbe.Sim.Geometry;
using Urbe.Sim.Vehicles;

namespace Urbe.Game.Tests
{
    public class SnapshotInterpolatorTests
    {
        private static VehicleSnapshot V(int id, ushort gen, float x, float z, float tx, float tz) =>
            new VehicleSnapshot { Id = id, Generation = gen, Position = new Vec2(x, z), Tangent = new Vec2(tx, tz), Speed = 5f };

        [Test]
        public void InterpolaPosicionLinealmente()
        {
            var prev = new SnapshotFrame(new[] { V(0, 1, 0f, 0f, 1f, 0f) }, 1, 10);
            var cur = new SnapshotFrame(new[] { V(0, 1, 10f, 0f, 1f, 0f) }, 1, 11);
            var it = new SnapshotInterpolator(8);
            it.Interpolate(in prev, in cur, 0.25f);
            Assert.That(it.Count, Is.EqualTo(1));
            Assert.That(it.Output[0].X, Is.EqualTo(2.5f).Within(1e-4f));
            Assert.That(it.Output[0].DirX, Is.EqualTo(1f).Within(1e-4f));
        }

        [Test]
        public void VehiculoNuevo_SePintaEnSuPosicionActual()
        {
            var prev = new SnapshotFrame(new[] { V(0, 1, 0f, 0f, 1f, 0f) }, 1, 10);
            var cur = new SnapshotFrame(new[] { V(0, 1, 10f, 0f, 1f, 0f), V(1, 1, 50f, 5f, 0f, 1f) }, 2, 11);
            var it = new SnapshotInterpolator(8);
            it.Interpolate(in prev, in cur, 0.5f);
            Assert.That(it.Count, Is.EqualTo(2));
            Assert.That(it.Output[1].X, Is.EqualTo(50f));
            Assert.That(it.Output[1].Z, Is.EqualTo(5f));
        }

        [Test]
        public void IdReutilizadoConOtraGeneracion_NoInterpolaConElViejo()
        {
            var prev = new SnapshotFrame(new[] { V(0, 1, 0f, 0f, 1f, 0f) }, 1, 10);
            var cur = new SnapshotFrame(new[] { V(0, 2, 100f, 0f, 1f, 0f) }, 1, 11);
            var it = new SnapshotInterpolator(8);
            it.Interpolate(in prev, in cur, 0.5f);
            Assert.That(it.Output[0].X, Is.EqualTo(100f));
        }

        [Test]
        public void SinSnapshotPrevio_UsaElActual()
        {
            var cur = new SnapshotFrame(new[] { V(0, 1, 7f, 3f, 0f, 1f) }, 1, 1);
            var it = new SnapshotInterpolator(8);
            it.Interpolate(default, in cur, 0.9f);
            Assert.That(it.Output[0].X, Is.EqualTo(7f));
        }

        [Test]
        public void SinSnapshots_CountEsCero()
        {
            var it = new SnapshotInterpolator(8);
            it.Interpolate(default, default, 0.5f);
            Assert.That(it.Count, Is.EqualTo(0));
        }
    }
}
