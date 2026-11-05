using NUnit.Framework;
using Urbe.Game.Sim;

namespace Urbe.Game.Tests
{
    /// <summary>M2-01: Sim.Core (DLL) carga en Unity y una TrafficSimulation avanza ticks.</summary>
    public class SimCoreIntegrationTests
    {
        [Test]
        public void TrafficSimulation_Avanza100Ticks_YEsDeterminista()
        {
            ulong Run()
            {
                SimScenario sc = GridScenario.Create(seed: 777UL, maxVehicles: 100, tripsPerSecond: 5f);
                Assert.That(sc.Network.SegmentIndexBound, Is.GreaterThan(0));
                for (int i = 0; i < 100; i++) sc.Sim.Step();
                Assert.That(sc.Sim.Tick, Is.EqualTo(100UL));
                return sc.Sim.ComputeHash();
            }
            ulong a = Run(), b = Run();
            Assert.That(a, Is.EqualTo(b), "Misma semilla y mismos ticks deben dar el mismo hash en el mismo dispositivo");
        }

        [Test]
        public void TrafficSimulation_GeneraVehiculos()
        {
            SimScenario sc = GridScenario.Create(seed: 1UL, maxVehicles: 100, tripsPerSecond: 10f);
            for (int i = 0; i < 300; i++) sc.Sim.Step();
            Assert.That(sc.Sim.Vehicles.AliveCount, Is.GreaterThan(0));
        }
    }
}
