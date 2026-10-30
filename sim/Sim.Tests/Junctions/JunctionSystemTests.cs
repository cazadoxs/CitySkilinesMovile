using System;
using System.Diagnostics;
using Urbe.Sim.Ids;
using Urbe.Sim.Junctions;
using Urbe.Sim.Roads;
using Urbe.Sim.Tests.Vehicles;
using Urbe.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace Urbe.Sim.Tests.Junctions
{
    /// <summary>Test 15 del ADR 004 (epoca y asignaciones) y benchmark con cruces reales.</summary>
    public class JunctionSystemTests
    {
        private readonly ITestOutputHelper _out;

        public JunctionSystemTests(ITestOutputHelper output) { _out = output; }

        // 15a. Cambiar el control y reconstruir el nodo con vehiculos dentro no lanza ni descuadra contadores.
        [Fact]
        public void Epoch_ControlChangeWithVehiclesInside_KeepsCountersConsistent()
        {
            Crossing x = JK.X(RoadTypeIds.Street2, RoadTypeIds.Street2);
            var sc = new PropertyScenario(x, 8, 0.05f, 0.08f);
            ushort[] controls = { JunctionControlIds.Stop, JunctionControlIds.Signal, JunctionControlIds.Priority, JunctionControlIds.Auto };
            int changes = 0, insideAtChange = 0;
            for (int t = 0; t < 4000; t++)
            {
                if (t % 500 == 250)
                {
                    int inside = 0;
                    for (int i = 0; i < sc.Sys.HighWater; i++)
                        if (sc.Sys.GetVehicle(i).IsAlive && JK.BodyOn(sc.Sys, i, out _, out _, out _)) inside++;
                    insideAtChange += inside;
                    Assert.True(x.Net.SetNodeControl(x.Center, controls[changes % controls.Length]));
                    changes++;
                }
                sc.Step(true);
                Assert.True(sc.CounterError == null, sc.CounterError);
                Assert.Equal(sc.Ctrl!.GetJunction(x.Center)!.Control,
                    JunctionBuilder.ResolveControl(x.Net, x.Center));
            }
            Assert.Equal(0, sc.Collisions);
            Assert.True(insideAtChange > 0, "ningun cambio pillo vehiculos dentro");
            int drained = 0;
            while (sc.Sys.AliveCount > 0 && drained++ < 3000) sc.Step(false);
            Assert.Equal(0, sc.Sys.AliveCount);
            Assert.True(sc.CounterError == null, sc.CounterError);
        }

        // 15b. Edicion de la red (un brazo nuevo en el cruce) con vehiculos dentro.
        [Fact]
        public void Epoch_AddArmWithVehiclesInside_NoThrow_CountersConsistent()
        {
            Crossing x = JK.Make(new ushort?[] { RoadTypeIds.Street2, null, RoadTypeIds.Street2, RoadTypeIds.Street2 });
            var sc = new PropertyScenario(x, 9, 0.05f, 0.08f);
            for (int t = 0; t < 1500; t++) sc.Step(true);
            EntityId outer = x.Net.AddNodeRaw(Tests.Roads.Kit.V(0, 150f), 0f);
            Assert.True(x.Net.AddSegmentRaw(outer, x.Center, Urbe.Sim.Geometry.Bezier3.FromLine(Tests.Roads.Kit.V(0, 150f), Tests.Roads.Kit.V(0, 0)), RoadTypeIds.Street2).IsValid);
            for (int t = 0; t < 1500; t++)
            {
                sc.Step(true);
                Assert.True(sc.CounterError == null, sc.CounterError);
            }
            Assert.Equal(4, sc.Ctrl!.GetJunction(x.Center)!.SegmentCount);
        }

        // 15c. 1.000 ticks con ~500 vehiculos en cruces reales sin asignar memoria.
        [Fact]
        public void Update_WithJunctions_AllocatesZeroBytes()
        {
            RoadNetwork n = VK.MakeGrid(8, 100f);
            var jc = new JunctionController(n);
            VehicleSystem s = new VehicleSystem(n, jc, 2000, 5, new VehicleConfig { DriverVariation = true });
            var pop = new VK.Population(n, s, 500, 9);
            for (int t = 0; t < 600; t++)
            {
                pop.TopUp(64);
                s.Update();
            }
            Assert.True(s.AliveCount > 300, "vivos = " + s.AliveCount);
            VK.Run(s, 20);
            long granted0 = jc.Stats.Granted;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int t = 0; t < 1000; t++) s.Update();
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, after - before);
            Assert.True(jc.Stats.Granted - granted0 > 100, "pocas concesiones medidas");
        }

        // Benchmark informativo: 3.000 vehiculos en cuadricula 12x12 con cruces reales (prioridad en X y T).
        [Fact]
        [Trait("Category", "Bench")]
        public void Bench_3000Vehicles_GridWithJunctions_MsPerTick()
        {
            RoadNetwork n = VK.MakeGrid(12, 120f);
            var jc = new JunctionController(n);
            VehicleSystem s = new VehicleSystem(n, jc, 4000, 5, new VehicleConfig { DriverVariation = true });
            var pop = new VK.Population(n, s, 3000, 9);
            for (int t = 0; t < 800; t++)
            {
                pop.TopUp(256);
                s.Update();
            }
            int ticks = 300;
            var sw = new Stopwatch();
            long req0 = jc.Stats.Requests, gr0 = jc.Stats.Granted;
            for (int t = 0; t < ticks; t++)
            {
                pop.TopUp(256);
                sw.Start();
                s.Update();
                sw.Stop();
            }
            double ms = sw.Elapsed.TotalMilliseconds / ticks;
            string line = string.Format("BENCH junctions vehiculos={0} ms/tick={1:F3} peticiones/tick={2:F0} concesiones/tick={3:F1}",
                s.AliveCount, ms, (jc.Stats.Requests - req0) / (double)ticks, (jc.Stats.Granted - gr0) / (double)ticks);
            _out.WriteLine(line);
            Console.WriteLine(line);
            Assert.True(s.AliveCount > 2500);
            Assert.True(ms < 50.0, "ms/tick = " + ms);
        }
    }
}
