using System;
using System.Collections.Generic;
using Urbe.Sim.Junctions;
using Urbe.Sim.Roads;
using Xunit;
using Xunit.Abstractions;

namespace Urbe.Sim.Tests.Junctions
{
    /// <summary>Tests 12-14 del ADR 004: propiedades en 10.000 ticks, control negativo y determinismo.</summary>
    public class JunctionPropertyTests
    {
        private const int DemandTicks = 10000;
        private const int DrainTicks = 1500;
        private const int MaxWait = 600;

        private readonly ITestOutputHelper _out;

        public JunctionPropertyTests(ITestOutputHelper output) { _out = output; }

        public static IEnumerable<object[]> Seeds()
        {
            for (ulong s = 1; s <= 5; s++) yield return new object[] { s };
        }

        private static Crossing ScenarioA() => JK.X(RoadTypeIds.Street2, RoadTypeIds.Street2);

        private static Crossing ScenarioB()
        {
            Crossing x = JK.T(RoadTypeIds.Street2, RoadTypeIds.Avenue4);
            Assert.True(x.Net.SetNodeControl(x.Center, JunctionControlIds.Stop));
            return x;
        }

        private static Crossing ScenarioC() => JK.X(RoadTypeIds.Avenue4, RoadTypeIds.Avenue4);

        private void Run(string name, Crossing x, ulong seed, float lmin, float lmax, ControlKind expected)
        {
            var sc = new PropertyScenario(x, seed, lmin, lmax);
            Assert.Equal(expected, sc.Ctrl!.GetControl(x.Center));
            int maxPending = 0;
            for (int t = 0; t < DemandTicks; t++)
            {
                sc.Step(true);
                int p = sc.Trace.PendingMaxWait();
                if (p > maxPending) maxPending = p;
            }
            int drained = 0;
            while (sc.Sys.AliveCount > 0 && drained < DrainTicks)
            {
                sc.Step(false);
                int p = sc.Trace.PendingMaxWait();
                if (p > maxPending) maxPending = p;
                drained++;
            }
            JunctionStats st = sc.Ctrl.Stats;
            string msg = string.Format("{0} semilla {1}: huecoMin={15:F2} creados={2} salidas={3} colisiones={4} esperaMax={5} pendienteMax={6} vivos={7} vaciado={8} " +
                "concedidas={9} senal={10} stop={11} reclamada={12} conflicto={13} salidaLlena={14}",
                name, seed, sc.Spawned, sc.Trace.Exits, sc.Collisions, sc.Trace.MaxWait, maxPending, sc.Sys.AliveCount, drained,
                st.Granted, st.DeniedSignal, st.DeniedStop, st.DeniedClaimed, st.DeniedConflict, st.DeniedExitFull, sc.MinChainGap);
            _out.WriteLine(msg);
            Assert.True(sc.CounterError == null, sc.CounterError);
            Assert.True(sc.Spawned > 50, msg);
            Assert.Equal(0, sc.Collisions);
            Assert.True(sc.MinChainGap >= sc.Sys.Config.MinGap - 1e-3f, "solape en cadena: " + sc.MinChainGap);
            Assert.True(sc.Trace.MaxWait < MaxWait && maxPending < MaxWait, msg);
            Assert.True(sc.Sys.AliveCount == 0, msg);
        }

        // 12A. X street_2 x street_2 con prioridad.
        [Theory]
        [MemberData(nameof(Seeds))]
        public void PropertyA_StreetX_Priority(ulong seed) => Run("A", ScenarioA(), seed, 0.02f, 0.05f, ControlKind.Priority);

        // 12B. T street_2 sobre avenue_4 con stop.
        [Theory]
        [MemberData(nameof(Seeds))]
        public void PropertyB_T_Stop(ulong seed) => Run("B", ScenarioB(), seed, 0.02f, 0.05f, ControlKind.Stop);

        // 12C. X avenue_4 x avenue_4 con semaforo automatico.
        [Theory]
        [MemberData(nameof(Seeds))]
        public void PropertyC_AvenueX_Signal(ulong seed) => Run("C", ScenarioC(), seed, 0.02f, 0.06f, ControlKind.Signal);

        // 13. Control negativo: con GapOnlyGate el detector encuentra colisiones.
        [Fact]
        public void Negative_GapOnlyGate_DetectsCollisions()
        {
            var sc = new PropertyScenario(ScenarioA(), 1, 0.02f, 0.05f, useGapOnly: true);
            for (int t = 0; t < DemandTicks; t++) sc.Step(true);
            _out.WriteLine("GapOnlyGate colisiones = " + sc.Collisions);
            Assert.True(sc.Collisions > 0, "el detector no encontro colisiones con GapOnlyGate");
        }

        // 14. Determinismo: misma semilla -> mismo hash de vehiculos + controlador cada 100 ticks.
        [Fact]
        public void Determinism_ScenarioC_SameSeed_SameHashes()
        {
            List<ulong> a = Hashes(3), b = Hashes(3), c = Hashes(4);
            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
        }

        private static List<ulong> Hashes(ulong seed)
        {
            var sc = new PropertyScenario(ScenarioC(), seed, 0.02f, 0.06f) { CheckCountersEachTick = false };
            var list = new List<ulong>();
            for (int t = 1; t <= 3000; t++)
            {
                sc.Step(true);
                if (t % 100 == 0) list.Add(sc.Sys.ComputeHash() ^ (sc.Ctrl!.ComputeHash() * 31UL));
            }
            return list;
        }
    }
}
