using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Urbe.Game.Sim;
using Urbe.Sim.Commands;
using Urbe.Sim.Roads;

namespace Urbe.Game.Tests
{
    public class SimHostTests
    {
        private GameObject _go = null!;
        private SimHost _host = null!;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("SimHost");
            _host = _go.AddComponent<SimHost>();
            _host.Initialize(GridScenario.Create(seed: 5UL, maxVehicles: 200, tripsPerSecond: 6f));
        }

        [TearDown]
        public void TearDown() => Object.Destroy(_go);

        [UnityTest]
        public IEnumerator X1_Da10TicksPorSegundoReales()
        {
            yield return null;
            ulong t0 = _host.Tick;
            float s0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - s0 < 3f) yield return null;
            float elapsed = Time.realtimeSinceStartup - s0;
            double tps = (_host.Tick - t0) / (double)elapsed;
            Debug.Log($"[M2-02] ticks/s medidos en el editor (batchmode) a x1: {tps:F2} ({_host.Tick - t0} ticks en {elapsed:F2}s); tick medio {_host.Metrics.AvgTickMs:F2} ms");
            Assert.That(tps, Is.InRange(9.0, 11.0));
            Assert.That(_host.Metrics.DroppedTicks, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Pausa_DetieneLosTicks_YReanudaALaUltimaVelocidad()
        {
            yield return null;
            _host.SetSpeed(SimSpeed.X2);
            yield return new WaitForSecondsRealtime(0.5f);
            _host.TogglePause();
            Assert.That(_host.IsPaused);
            ulong t = _host.Tick;
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(_host.Tick, Is.EqualTo(t));
            _host.TogglePause();
            Assert.That(_host.Speed, Is.EqualTo(SimSpeed.X2));
        }

        [UnityTest]
        public IEnumerator X3_Da30TicksPorSegundo()
        {
            yield return null;
            _host.SetSpeed(SimSpeed.X3);
            ulong t0 = _host.Tick;
            float s0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - s0 < 2f) yield return null;
            double tps = (_host.Tick - t0) / (double)(Time.realtimeSinceStartup - s0);
            Debug.Log($"[M2-02] ticks/s a x3: {tps:F2}");
            Assert.That(tps, Is.InRange(27.0, 33.0));
        }

        [Test]
        public void ComandosSeAplicanEntreTicks_YStepOnceAvanzaUno()
        {
            var cmd = new CountingCommand();
            _host.Enqueue(cmd);
            Assert.That(_host.PendingCommands, Is.EqualTo(1));
            Assert.That(cmd.Applied, Is.EqualTo(0), "Encolar no aplica");
            ulong t = _host.Tick;
            _host.StepOnce();
            Assert.That(_host.Tick, Is.EqualTo(t + 1));
            Assert.That(cmd.Applied, Is.EqualTo(1));
            Assert.That(_host.PendingCommands, Is.EqualTo(0));
        }

        [Test]
        public void Interpolate_DevuelveVehiculos_SinAsignarEnElBucle()
        {
            for (int i = 0; i < 400; i++) _host.StepOnce();
            SnapshotInterpolator it = _host.Interpolate();
            Assert.That(it.Count, Is.GreaterThan(0));
            Assert.That(it.Count, Is.EqualTo(_host.Metrics.VehicleCount).Within(2));
            _host.Interpolate();
            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++) _host.Interpolate();
            long after = System.GC.GetAllocatedBytesForCurrentThread();
            Assert.That(after - before, Is.LessThan(1024), "Interpolate no debería asignar por llamada");
        }

        [Test]
        public void Ticked_SeEmitePorCadaTick()
        {
            int n = 0;
            _host.Ticked += _ => n++;
            for (int i = 0; i < 5; i++) _host.StepOnce();
            Assert.That(n, Is.EqualTo(5));
        }

        private sealed class CountingCommand : ICommand
        {
            public int Applied;
            public ValidationResult Validate(RoadNetwork network) => ValidationResult.Ok;
            public bool Apply(RoadNetwork network) { Applied++; return true; }
            public void Undo(RoadNetwork network) { }
        }
    }
}
