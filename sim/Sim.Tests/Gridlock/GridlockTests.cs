using System;
using System.Collections.Generic;
using Urbe.Sim.Ids;
using Urbe.Sim.Junctions;
using Urbe.Sim.Roads;
using Urbe.Sim.Tests.Junctions;
using Urbe.Sim.Tests.Roads;
using Urbe.Sim.Tests.Vehicles;
using Urbe.Sim.Traffic;
using Urbe.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace Urbe.Sim.Tests.Gridlock
{
    /// <summary>Puerta que simula un paso forzado "no seguro": no llega al controlador y queda pendiente hasta que se cancela.</summary>
    internal sealed class ForceFilterGate : IJunctionGate, IVehicleViewConsumer, IForcedPassGate
    {
        public readonly JunctionController Inner;
        public bool RefuseAll;
        public readonly HashSet<int> Refuse = new HashSet<int>();
        private readonly HashSet<int> _pending = new HashSet<int>();

        public ForceFilterGate(RoadNetwork net) { Inner = new JunctionController(net); }

        public void Bind(IVehicleView view, int vehicleCapacity) => Inner.Bind(view, vehicleCapacity);
        public void OnNetworkChanged(ReadOnlySpan<EntityId> changedNodes) => Inner.OnNetworkChanged(changedNodes);
        public void BeginTick(ulong tick) => Inner.BeginTick(tick);
        public void RequestEntry(in EntryRequest req) => Inner.RequestEntry(in req);
        public ReadOnlySpan<EntryGrant> ResolveRequests() => Inner.ResolveRequests();
        public void OnEntered(int connection, int vehicle) => Inner.OnEntered(connection, vehicle);
        public void OnExited(int connection, int vehicle) => Inner.OnExited(connection, vehicle);
        public void OnPermitCancelled(int vehicle) => Inner.OnPermitCancelled(vehicle);

        public void OnVehicleRemoved(int vehicle)
        {
            _pending.Remove(vehicle);
            Inner.OnVehicleRemoved(vehicle);
        }

        public void ForceNextGrant(int vehicle) => ForceNextGrant(vehicle, true);

        public void ForceNextGrant(int vehicle, bool exitInCycle)
        {
            Calls.Add((vehicle, exitInCycle));
            if (RefuseAll || Refuse.Contains(vehicle)) _pending.Add(vehicle);
            else Inner.ForceNextGrant(vehicle, exitInCycle);
        }

        public readonly List<(int Vehicle, bool ExitInCycle)> Calls = new List<(int, bool)>();

        public bool IsForced(int vehicle) => _pending.Contains(vehicle) || Inner.IsForced(vehicle);

        public void CancelForce(int vehicle)
        {
            _pending.Remove(vehicle);
            Inner.CancelForce(vehicle);
        }
    }

    internal sealed class AnyReroute : IRerouteSource
    {
        public bool CanReroute(int vehicle) => true;
    }

    public class GridlockTests
    {
        private const int DetectTicks = 200;
        private readonly ITestOutputHelper _out;

        public GridlockTests(ITestOutputHelper output) { _out = output; }

        private static TrafficSimulationOptions Options(Action<GridlockConfig>? tweak = null)
        {
            var o = new TrafficSimulationOptions();
            tweak?.Invoke(o.Gridlock);
            return o;
        }

        /// <summary>Detector de colisiones de M1-07 en las 4 esquinas y hueco entre cuerpos consecutivos, tick a tick.</summary>
        private sealed class SafetyMonitor
        {
            private readonly BlockScenario _sc;
            private readonly JunctionNode[] _layouts = new JunctionNode[4];
            private readonly List<(int, int, float, float)> _scratch = new List<(int, int, float, float)>();
            private readonly List<(long, float, float)> _pieces = new List<(long, float, float)>();
            public int Collisions;
            public float MinChainGap = float.MaxValue;

            public SafetyMonitor(BlockScenario sc)
            {
                _sc = sc;
                var b = new JunctionBuilder(new JunctionConfig());
                for (int k = 0; k < 4; k++) _layouts[k] = b.Build(sc.Net, sc.Corners[k]);
            }

            public void Check()
            {
                for (int k = 0; k < 4; k++) Collisions += JK.CountCollisions(_sc.Vehicles, _layouts[k], _scratch);
                float g = JK.MinChainGap(_sc.Vehicles, _pieces);
                if (g < MinChainGap) MinChainGap = g;
            }
        }

        private static int MinWait(BlockScenario sc)
        {
            int m = int.MaxValue;
            for (int i = 0; i < sc.Vehicles.HighWater; i++)
            {
                ref readonly Vehicle v = ref sc.Vehicles.GetVehicle(i);
                if (v.IsAlive && v.WaitTicks < m) m = v.WaitTicks;
            }
            return m;
        }

        // ------------------------------------------------------------------ escenario de bloqueo provocado

        [Fact]
        public void Lock_WithoutResolver_IsAPermanentDeadlock()
        {
            var sc = new BlockScenario(1, Options(g => g.Enabled = false));
            Assert.True(sc.Lock());
            Assert.Equal(52, sc.Vehicles.AliveCount);
            sc.Sim.Run(1200);
            Assert.Equal(52, sc.Vehicles.AliveCount);
            Assert.Equal(52, sc.StoppedCount(1));
            Assert.Equal(0, sc.Sim.Metrics.Gridlock.Cycles);
        }

        [Theory]
        [InlineData(1UL, true)]
        [InlineData(2UL, true)]
        [InlineData(3UL, false)]
        [InlineData(4UL, true)]
        public void Lock_IsDetectedAndResolvedWithin60Seconds_WithoutCollisions(ulong seed, bool clockwise)
        {
            var sc = new BlockScenario(seed, null, null, clockwise);
            Assert.True(sc.Lock());
            var mon = new SafetyMonitor(sc);
            GridlockStats st = sc.Sim.Metrics.Gridlock;
            int minWait = MinWait(sc);
            int firstAttempt = -1, resolved = -1;
            int maxWaitSeen = 0;
            for (int t = 1; t <= 6000; t++)
            {
                sc.Sim.Step();
                mon.Check();
                int mw = sc.MaxWait();
                if (mw > maxWaitSeen) maxWaitSeen = mw;
                if (firstAttempt < 0 && st.ForcedAttempts > 0) firstAttempt = t;
                if (firstAttempt >= 0 && resolved < 0 && mw < DetectTicks) resolved = t;
                if (sc.Vehicles.AliveCount == 0) break;
            }
            _out.WriteLine("minWait " + minWait + " primer intento " + firstAttempt + " resuelto " + resolved + " vivos " + sc.Vehicles.AliveCount
                + " ciclos " + st.Cycles + " intentos " + st.ForcedAttempts + " pasos " + st.ForcedPasses + " fallos " + st.ForcedFailed + " bajas " + st.Despawns);

            // Ni antes de X segundos de espera de todo el ciclo ni mucho despues (la ronda tarda <= 1 s).
            Assert.InRange(firstAttempt, DetectTicks - minWait - 1, DetectTicks - minWait + 30);
            // Aceptacion de M1-11: resuelto (nadie lleva mas de X parado) en < 60 s simulados desde el bloqueo.
            Assert.InRange(resolved, 1, 600);
            Assert.True(st.Cycles >= 1 && st.ForcedPasses >= 1, "sin ciclo o sin paso forzado");
            Assert.Equal(0, st.Despawns);
            Assert.Equal(0, st.Unresolved);
            // El anillo se vacia solo, sin llegar a ninguna baja (la mayor espera queda muy por debajo del tiempo de desaparicion).
            Assert.Equal(0, sc.Vehicles.AliveCount);
            Assert.True(maxWaitSeen < 1200, "espera maxima " + maxWaitSeen);
            Assert.Equal(0, mon.Collisions);
            Assert.True(mon.MinChainGap >= sc.Vehicles.Config.MinGap - 1e-3f, "solape en cadena " + mon.MinChainGap);
        }

        [Fact]
        public void Lock_WithDespawnDisabled_IsResolvedWithStepsOneAndTwoOnly()
        {
            var sc = new BlockScenario(5, Options(g => g.DespawnStuckVehicles = false));
            Assert.True(sc.Lock());
            GridlockStats st = sc.Sim.Metrics.Gridlock;
            int resolved = -1;
            for (int t = 1; t <= 6000 && sc.Vehicles.AliveCount > 0; t++)
            {
                sc.Sim.Step();
                if (resolved < 0 && st.ForcedAttempts > 0 && sc.MaxWait() < DetectTicks) resolved = t;
            }
            Assert.InRange(resolved, 1, 600);
            Assert.Equal(0, sc.Vehicles.AliveCount);
            Assert.Equal(0, st.Despawns);
            Assert.Equal(0, st.Unresolved);
        }

        // ------------------------------------------------------------------ escalada: paso forzado no seguro -> re-ruta -> baja

        [Fact]
        public void ForcedPassNotSafe_IsCancelledAndTheNextCandidateIsTried()
        {
            // Los 3 primeros por espera tienen un paso forzado no seguro (la puerta no lo concede); el cuarto si.
            ForceFilterGate fg = null!;
            var sc = new BlockScenario(1, Options(g => g.MaxForcedPerCycle = 1), net => fg = new ForceFilterGate(net));
            fg.Refuse.Add(0);
            fg.Refuse.Add(1);
            fg.Refuse.Add(2);
            Assert.True(sc.Lock());
            GridlockStats st = sc.Sim.Metrics.Gridlock;
            int firstPass = -1, drained = -1;
            for (int t = 1; t <= 8000 && drained < 0; t++)
            {
                sc.Sim.Step();
                if (firstPass < 0 && st.ForcedPasses > 0) firstPass = t;
                if (sc.Vehicles.AliveCount == 0) drained = t;
            }
            _out.WriteLine("primer paso " + firstPass + " vaciado " + drained + " intentos " + st.ForcedAttempts + " fallos " + st.ForcedFailed + " pasos " + st.ForcedPasses);
            Assert.True(st.ForcedFailed >= 3, "fallos " + st.ForcedFailed);
            // 20 s de deteccion + 3 intentos no seguros de 4 s: el primer paso llega antes de 60 s; el anillo acaba vaciandose.
            Assert.InRange(firstPass, 1, 600);
            Assert.True(drained > 0, "el anillo no se vacio");
            Assert.Equal(0, st.Reroutes);
            Assert.Equal(0, st.Despawns);
            // Los intentos fallidos se cancelaron: la puerta no los deja pendientes.
            foreach (int v in new[] { 0, 1, 2 }) Assert.False(fg.IsForced(v));
        }

        [Fact]
        public void ForcedPasses_OfARing_AreFlaggedAsExitInCycle_AndTheRingStillClears()
        {
            ForceFilterGate gate = null!;
            var sc = new BlockScenario(1, null, net => gate = new ForceFilterGate(net));
            Assert.True(sc.Lock());
            GridlockStats st = sc.Sim.Metrics.Gridlock;
            for (int t = 1; t <= 900 && sc.Vehicles.AliveCount > 0 && sc.MaxWait() >= DetectTicks; t++) sc.Sim.Step();
            Assert.True(st.ForcedAttempts >= 1);
            Assert.True(gate.Calls.Count >= 1);
            // En un anillo el carril de salida de cada forzado lo ocupa otro miembro del ciclo: no se exige hueco previo.
            foreach (var c in gate.Calls) Assert.True(c.ExitInCycle);
            Assert.True(sc.MaxWait() < DetectTicks);
        }

        [Fact]
        public void ForcedPassImpossible_FallsBackToRerouteAndTheRingClears()
        {
            ForceFilterGate gate = null!;
            var sc = new BlockScenario(1, Options(g => g.Enabled = false), net => gate = new ForceFilterGate(net));
            gate.RefuseAll = true;
            Assert.True(sc.Lock());
            var stats = new GridlockStats();
            var resolver = new GridlockResolver(sc.Net, sc.Vehicles, gate, new AnyReroute(), new GridlockConfig(), stats);
            int firstReroute = -1;
            var lanes = new LaneId[2];
            for (int t = 1; t <= 6000 && sc.Vehicles.AliveCount > 0; t++)
            {
                sc.Sim.Step();
                resolver.Step();
                if (firstReroute < 0 && stats.Reroutes > 0) firstReroute = t;
                // Hace de generador de viajes: da a cada vehiculo marcado una ruta que sale por el ramal de la esquina siguiente.
                for (int i = 0; i < sc.Vehicles.HighWater; i++)
                {
                    ref readonly Vehicle v = ref sc.Vehicles.GetVehicle(i);
                    if (!v.IsAlive || (v.Flags & VehicleFlags.NeedsReroute) == 0) continue;
                    LaneId cur = sc.Vehicles.GetRouteStep(i, v.RouteCursor).Lane;
                    int k = Array.IndexOf(sc.RingLanes, cur);
                    Assert.True(k >= 0);
                    lanes[0] = cur;
                    lanes[1] = sc.StubOut[(k + 1) % 4];
                    sc.Vehicles.TryReplaceRoute(sc.Vehicles.IdOf(i), lanes, sc.Net.GetLane(lanes[1]).Length * 0.5f);
                }
            }
            _out.WriteLine("primera re-ruta " + firstReroute + " intentos " + stats.ForcedAttempts + " fallos " + stats.ForcedFailed + " rutas " + stats.Reroutes
                + " bajas " + stats.Despawns + " vivos " + sc.Vehicles.AliveCount);
            Assert.True(stats.ForcedAttempts >= 4 && stats.ForcedFailed >= 4 && stats.ForcedPasses == 0);
            Assert.True(stats.Reroutes >= 1);
            // 20 s de deteccion + 4 s del paso forzado fallido + la re-ruta: antes de 60 s.
            Assert.InRange(firstReroute, 1, 600);
            Assert.Equal(0, sc.Vehicles.AliveCount);
        }

        [Fact]
        public void NothingElseWorks_DespawnClearsTheCycleAfterTheConfiguredTime()
        {
            ForceFilterGate gate = null!;
            var sc = new BlockScenario(1, Options(g => g.DespawnSeconds = 60f), net => gate = new ForceFilterGate(net));
            gate.RefuseAll = true;
            Assert.True(sc.Lock());
            GridlockStats st = sc.Sim.Metrics.Gridlock;
            int maxWaitAtLock = sc.MaxWait();
            int firstDespawn = -1;
            for (int t = 1; t <= 3000; t++)
            {
                sc.Sim.Step();
                if (firstDespawn < 0 && st.Despawns > 0) firstDespawn = t;
            }
            _out.WriteLine("primera baja " + firstDespawn + " bajas " + st.Despawns + " vivos " + sc.Vehicles.AliveCount + " fallos " + st.ForcedFailed);
            // No antes de que el vehiculo mas esperado lleve DespawnSeconds (60 s) parado, y poco despues.
            Assert.InRange(firstDespawn, 600 - maxWaitAtLock - 1, 600);
            Assert.True(st.Despawns >= 1);
            Assert.Equal(0, st.Reroutes); // sin generador de viajes que las rehaga
            Assert.Equal(0, st.ForcedPasses);
            // Con el hueco el anillo vuelve a fluir (sale gente por su cuenta aunque el paso forzado siga sin servir).
            Assert.True(sc.Vehicles.AliveCount <= 52 - st.Despawns - 5, "vivos " + sc.Vehicles.AliveCount);
        }

        [Fact]
        public void DespawnDisabled_UnsolvableCycleIsReportedOnceAndStaysStable()
        {
            ForceFilterGate gate = null!;
            var sc = new BlockScenario(1, Options(g => g.DespawnStuckVehicles = false), net => gate = new ForceFilterGate(net));
            gate.RefuseAll = true;
            Assert.True(sc.Lock());
            GridlockStats st = sc.Sim.Metrics.Gridlock;
            sc.Sim.Run(500);
            long attempts = st.ForcedAttempts, failed = st.ForcedFailed;
            Assert.Equal(1, st.Unresolved);
            sc.Sim.Run(3000);
            // Sin cuelgues ni bucles: ni mas intentos, ni bajas, ni mas avisos; el anillo sigue quieto y los pasos forzados cancelados.
            Assert.Equal(attempts, st.ForcedAttempts);
            Assert.Equal(failed, st.ForcedFailed);
            Assert.Equal(4, st.ForcedAttempts);
            Assert.Equal(1, st.Unresolved);
            Assert.Equal(0, st.Despawns);
            Assert.Equal(0, st.Reroutes);
            Assert.Equal(52, sc.Vehicles.AliveCount);
            for (int v = 0; v < 4; v++) Assert.False(gate.IsForced(v));
        }

        // ------------------------------------------------------------------ sin falsos positivos

        [Fact]
        public void NormalGridTraffic_HasNoGridlockResolutions()
        {
            RoadNetwork net = VK.MakeGrid(6, 100f);
            var opt = new TrafficSimulationOptions { VehicleCapacity = 2048, Vehicle = new VehicleConfig { DriverVariation = true } };
            opt.Trips.TripsPerSecond = 2f;
            opt.Trips.MaxActiveVehicles = 600;
            var sim = new TrafficSimulation(net, 21, opt);
            for (int i = 0; i < net.SegmentIndexBound; i++)
            {
                EntityId seg = net.SegmentIdAt(i);
                if (seg.IsValid) sim.Trips.AddDemandPoint(seg, 1f, 1f);
            }
            sim.Run(5000);
            GridlockStats st = sim.Metrics.Gridlock;
            _out.WriteLine("vivos " + sim.Vehicles.AliveCount + " flujo " + sim.Metrics.CityFlowPercent);
            Assert.True(sim.Vehicles.AliveCount > 50);
            Assert.Equal(0, st.Cycles);
            Assert.Equal(0, st.ForcedAttempts);
            Assert.Equal(0, st.Resolutions);
            Assert.Equal(0, st.Unresolved);
        }

        [Fact]
        public void HeavyCongestion_WithoutACycleOfWaits_HasNoGridlockResolutions()
        {
            // Manzana saturada por una demanda excesiva (flujo del 10-20 %): hay colas largas y esperas de decenas de segundos, pero
            // no un ciclo cerrado de esperas, asi que no se actua.
            var opt = new TrafficSimulationOptions { VehicleCapacity = 1024 };
            opt.Trips.TripsPerSecond = 8f;
            opt.Trips.MaxActiveVehicles = 400;
            var sc = new BlockScenario(1, opt);
            for (int k = 0; k < 4; k++) sc.Sim.Trips.AddDemandPoint(sc.Stubs[k], 1f, 1f);
            sc.Sim.Run(6000);
            GridlockStats st = sc.Sim.Metrics.Gridlock;
            _out.WriteLine("flujo " + sc.Sim.Metrics.CityFlowPercent + " vivos " + sc.Vehicles.AliveCount + " espera max " + sc.MaxWait());
            Assert.True(sc.Sim.Metrics.CityFlowPercent < 40f, "el escenario deberia estar saturado");
            Assert.Equal(0, st.Cycles);
            Assert.Equal(0, st.Resolutions);
        }

        // ------------------------------------------------------------------ determinismo, coste y asignaciones

        private static ulong RunHash(ulong seed, int ticks)
        {
            var sc = new BlockScenario(seed);
            Assert.True(sc.Lock());
            ulong h = 0;
            GridlockStats st = sc.Sim.Metrics.Gridlock;
            for (int t = 0; t < ticks; t++)
            {
                sc.Sim.Step();
                if (t % 100 == 0)
                    h = h * 1099511628211UL + (sc.Vehicles.ComputeHash() ^ (sc.Ctrl!.ComputeHash() * 31UL)
                        ^ (ulong)(st.ForcedAttempts * 7 + st.ForcedPasses * 13 + st.Cycles * 17 + st.Despawns * 19));
            }
            return h;
        }

        [Fact]
        public void Determinism_SameSeedSameResult()
        {
            Assert.Equal(RunHash(7, 1500), RunHash(7, 1500));
            Assert.NotEqual(RunHash(7, 1500), RunHash(8, 1500));
        }

        [Fact]
        public void TinyBudgets_SpreadTheWorkOverManyTicksAndStillResolve()
        {
            var sc = new BlockScenario(1, Options(g =>
            {
                g.ScanPerTick = 7;
                g.StepsPerTick = 3;
            }));
            Assert.True(sc.Lock());
            GridlockStats st = sc.Sim.Metrics.Gridlock;
            int resolved = -1;
            for (int t = 1; t <= 900; t++)
            {
                sc.Sim.Step();
                if (resolved < 0 && st.ForcedAttempts > 0 && sc.MaxWait() < DetectTicks) resolved = t;
            }
            Assert.InRange(resolved, 1, 600);
            Assert.True(st.ForcedPasses >= 1);
        }

        [Fact]
        public void NoAllocationsPerTick_WhileDetectingAndResolving()
        {
            var sc = new BlockScenario(1);
            Assert.True(sc.Lock());
            sc.Sim.Run(20); // calentamiento
            long before = GC.GetAllocatedBytesForCurrentThread();
            sc.Sim.Run(1500); // incluye deteccion, pasos forzados y vaciado del anillo
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(sc.Sim.Metrics.Gridlock.ForcedPasses > 0);
            Assert.True(after - before == 0, "bytes asignados: " + (after - before));
        }

        // ------------------------------------------------------------------ ganchos

        [Fact]
        public void JunctionController_ForceCanBeQueriedAndCancelled()
        {
            var sc = new BlockScenario(1);
            JunctionController c = sc.Ctrl!;
            Assert.False(c.IsForced(3));
            c.ForceNextGrant(3);
            Assert.True(c.IsForced(3));
            c.CancelForce(3);
            Assert.False(c.IsForced(3));
            c.CancelForce(99999); // fuera de rango: no hace nada
            Assert.False(c.IsForced(-1));
        }

        [Fact]
        public void GapOnlyGate_ForceCanBeQueriedAndCancelled()
        {
            var g = new GapOnlyGate(Kit.New());
            g.ForceNextGrant(5);
            Assert.True(g.IsForced(5));
            Assert.False(g.IsForced(6));
            g.CancelForce(6);
            Assert.True(g.IsForced(5));
            g.CancelForce(5);
            Assert.False(g.IsForced(5));
        }

        [Fact]
        public void MarkForReroute_TruncatesTheRouteAndTheTripGeneratorGivesANewOne()
        {
            RoadNetwork net = VK.MakeGrid(5, 100f);
            var opt = new TrafficSimulationOptions { VehicleCapacity = 512, Vehicle = new VehicleConfig { DriverVariation = true } };
            opt.Trips.TripsPerSecond = 1.5f;
            var sim = new TrafficSimulation(net, 3, opt);
            for (int i = 0; i < net.SegmentIndexBound; i++)
            {
                EntityId seg = net.SegmentIdAt(i);
                if (seg.IsValid) sim.Trips.AddDemandPoint(seg, 1f, 1f);
            }
            int marked = -1;
            for (int t = 0; t < 3000 && marked < 0; t++)
            {
                sim.Step();
                for (int i = 0; i < sim.Vehicles.HighWater; i++)
                {
                    ref readonly Vehicle v = ref sim.Vehicles.GetVehicle(i);
                    if (!v.IsAlive || !sim.Trips.CanReroute(i) || v.RouteCursor + 3 > v.RouteLast) continue;
                    if (!sim.Vehicles.MarkForReroute(sim.Vehicles.IdOf(i))) continue;
                    ref readonly Vehicle w = ref sim.Vehicles.GetVehicle(i);
                    Assert.True((w.Flags & VehicleFlags.NeedsReroute) != 0);
                    Assert.Equal(w.RouteCursor, w.RouteLast);
                    marked = i;
                    break;
                }
            }
            Assert.True(marked >= 0, "no habia ningun candidato");
            long before = sim.Trips.Rerouted;
            sim.Run(600);
            Assert.True(sim.Trips.Rerouted > before, "el generador no le dio otra ruta");
        }
    }
}
