using System;
using System.Collections.Generic;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Junctions;
using Urbe.Sim.Random;
using Urbe.Sim.Roads;
using Urbe.Sim.Tests.Junctions;
using Urbe.Sim.Tests.Roads;
using Urbe.Sim.Tests.Vehicles;
using Urbe.Sim.Traffic;
using Urbe.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace Urbe.Sim.Tests.LaneChange
{
    /// <summary>M1-08: eleccion de carril anticipada y cambio de carril con aceptacion de hueco.</summary>
    public class LaneChangeTests
    {
        private readonly ITestOutputHelper _out;

        public LaneChangeTests(ITestOutputHelper output) { _out = output; }

        // ------------------------------------------------------------------ eleccion anticipada (LanePlanner)

        /// <summary>Avenida O-E con un tramo corto de 15 m antes de un cruce en T con una calle al norte (giro a la izquierda).</summary>
        private sealed class ShortLeg
        {
            public RoadNetwork Net = Kit.New();
            public EntityId Long, Short, East, North;
        }

        private static ShortLeg MakeShortLeg()
        {
            var w = new ShortLeg();
            RoadNetwork n = w.Net;
            EntityId a = Kit.Node(n, -250, 0), n1 = Kit.Node(n, -100, 0), n2 = Kit.Node(n, -85, 0);
            EntityId e = Kit.Node(n, 100, 0), north = Kit.Node(n, -85, 150);
            w.Long = Kit.Seg(n, a, n1, RoadTypeIds.Avenue4);
            w.Short = Kit.Seg(n, n1, n2, RoadTypeIds.Avenue4);
            w.East = Kit.Seg(n, n2, e, RoadTypeIds.Avenue4);
            w.North = Kit.Seg(n, n2, north, RoadTypeIds.Street2);
            return w;
        }

        [Fact]
        public void Planner_ShortSegment_AnticipatesTheTurnInThePreviousSegment()
        {
            ShortLeg w = MakeShortLeg();
            var planner = new LanePlanner(w.Net);
            var route = new[] { new RouteSegment(w.Long, true), new RouteSegment(w.Short, true), new RouteSegment(w.North, true) };
            var lanes = new LaneId[3];
            // Sale del tramo largo por el carril derecho (0): el cambio al izquierdo se hace en el tramo largo, no en el corto.
            Assert.True(planner.Plan(route, new LaneId(w.Long.Index, 0), 5f, false, LaneId.None, 1, lanes));
            Assert.Equal(new LaneId(w.Long.Index, 0), lanes[0]);
            Assert.Equal(1, lanes[1].Slot); // llega al tramo corto ya por el carril izquierdo
            Assert.Equal(w.Short.Index, lanes[1].SegmentIndex);
            // Libre: ningun cambio de carril en todo el recorrido.
            Assert.True(planner.Plan(route, 1, lanes));
            Assert.Equal(1, lanes[0].Slot);
            Assert.Equal(1, lanes[1].Slot);
        }

        [Fact]
        public void Planner_LongSegment_ChangesLateInTheSegmentThatNeedsIt()
        {
            RoadNetwork n = Kit.New();
            EntityId a = Kit.Node(n, -300, 0), c = Kit.Node(n, 0, 0), e = Kit.Node(n, 150, 0), no = Kit.Node(n, 0, 150);
            EntityId s1 = Kit.Seg(n, a, c, RoadTypeIds.Avenue4), s2 = Kit.Seg(n, c, e, RoadTypeIds.Avenue4), s3 = Kit.Seg(n, c, no, RoadTypeIds.Street2);
            var planner = new LanePlanner(n);
            var lanes = new LaneId[2];
            // Forzado en el carril derecho: un unico cambio (coste minimo), el planificador no inventa mas.
            var route = new[] { new RouteSegment(s1, true), new RouteSegment(s3, true) };
            Assert.True(planner.Plan(route, new LaneId(s1.Index, 0), 0f, false, LaneId.None, 9, lanes));
            Assert.Equal(0, lanes[0].Slot);
            Assert.Equal(s3.Index, lanes[1].SegmentIndex);
            Assert.NotEqual(s2.Index, lanes[1].SegmentIndex);
        }

        [Fact]
        public void Planner_UnconnectedSegments_Fail()
        {
            ShortLeg w = MakeShortLeg();
            var planner = new LanePlanner(w.Net);
            var lanes = new LaneId[2];
            // North -> Long no se conectan (la calle norte entra al cruce, el tramo largo sale del otro lado).
            Assert.False(planner.Plan(new[] { new RouteSegment(w.North, true), new RouteSegment(w.Long, true) }, 1, lanes));
            // Sentido sin carriles (la avenida tiene, la calle del norte en sentido contrario si; un solo sentido no).
            RoadNetwork n = Kit.New();
            EntityId a = Kit.Node(n, 0, 0), b = Kit.Node(n, 100, 0);
            EntityId ow = Kit.Seg(n, a, b, RoadTypeIds.OneWay2);
            var p2 = new LanePlanner(n);
            Assert.False(p2.Plan(new[] { new RouteSegment(ow, false) }, 1, new LaneId[1]));
            Assert.True(p2.Plan(new[] { new RouteSegment(ow, true) }, 1, new LaneId[1]));
        }

        // ------------------------------------------------------------------ giro a la izquierda desde una avenida 2+2

        [Theory]
        [InlineData(1UL, 0.02f)]
        [InlineData(2UL, 0.02f)]
        [InlineData(3UL, 0.02f)]
        [InlineData(4UL, 0.03f)]
        public void LeftTurn_FromAvenue2Plus2_AtLeast95PercentReachTheLeftLaneWithoutStopping(ulong seed, float spawnProbability)
        {
            Crossing x = JK.X(RoadTypeIds.Avenue4, RoadTypeIds.Street2);
            RoadNetwork net = x.Net;
            var sys = new VehicleSystem(net, new JunctionController(net), 512, seed, new VehicleConfig { DriverVariation = true });
            var rng = new Pcg32(seed, 3);
            LaneId left = x.Entry(2, 1);
            // Rutas: giro a la izquierda (norte), recto (este, carril izquierdo) y giro a la derecha (sur, carril derecho).
            LaneId exitN = x.Exit(1), exitE = x.Exit(0, 1), exitS = x.Exit(3);
            int cap = sys.Capacity;
            var tracked = new bool[cap];
            var reached = new bool[cap];
            var stopped = new bool[cap];
            int leftTurners = 0, ok = 0, bad = 0, changes0 = 0;
            for (int t = 0; t < 30000; t++)
            {
                if (rng.NextFloat() < spawnProbability) // 0,2-0,3 vehiculos/s (por debajo de la capacidad del cruce)
                {
                    float r = rng.NextFloat();
                    float speed = 11f + 2f * rng.NextFloat();
                    if (r < 0.4f)
                    {
                        if (sys.TrySpawnPlanned(VehicleTypeId.Car, new[] { x.Entry(2, 0), exitN }, 5f, 30f, speed, out EntityId id))
                        {
                            tracked[id.Index] = true; reached[id.Index] = false; stopped[id.Index] = false;
                            leftTurners++;
                        }
                    }
                    else if (r < 0.75f)
                    {
                        sys.TrySpawnPlanned(VehicleTypeId.Car, new[] { x.Entry(2, 1), exitE }, 5f, 30f, speed, out _);
                    }
                    else
                    {
                        sys.TrySpawnPlanned(VehicleTypeId.Car, new[] { x.Entry(2, 0), exitS }, 5f, 30f, speed, out _);
                    }
                }
                sys.Update();
                for (int i = 0; i < sys.HighWater; i++)
                {
                    if (!tracked[i]) continue;
                    ref readonly Vehicle v = ref sys.GetVehicle(i);
                    if (!v.IsAlive || v.RouteCursor > 0 || v.Kind == TrackKind.Connection) { Close(i); continue; }
                    if (reached[i]) continue;
                    if (sys.GetRouteStep(i, 0).Lane == left) reached[i] = true;
                    else if (v.V < 0.5f && v.S > 8f) stopped[i] = true;
                }
            }
            void Close(int i)
            {
                if (!tracked[i]) return;
                tracked[i] = false;
                if (reached[i] && !stopped[i]) ok++; else bad++;
            }
            for (int i = 0; i < sys.HighWater; i++) if (tracked[i]) Close(i);
            changes0 = sys.LaneChangeCount;
            _out.WriteLine("semilla " + seed + " p=" + spawnProbability + " giros a la izquierda: " + leftTurners + ", llegan bien " + ok + ", mal " + bad + ", cambios " + changes0
                + ", relajados " + sys.LaneChangeRelaxed + ", re-rutas " + sys.LaneChangeRerouted);
            Assert.True(ok + bad >= 150, "muestra pequena: " + (ok + bad));
            Assert.True(ok >= 0.95 * (ok + bad), ok + " de " + (ok + bad));
            Assert.True(changes0 >= ok);
        }

        // ------------------------------------------------------------------ maniobra, fantasma, snapshot

        private static (Crossing x, VehicleSystem s) Cross(ulong seed = 1)
        {
            Crossing x = JK.X(RoadTypeIds.Avenue4, RoadTypeIds.Street2);
            return (x, new VehicleSystem(x.Net, new JunctionController(x.Net), 256, seed, new VehicleConfig { DriverVariation = false }));
        }

        private static EntityId SpawnLeft(Crossing x, VehicleSystem s, float startS, float speed)
        {
            Assert.True(s.TrySpawnPlanned(VehicleTypeId.Car, new[] { x.Entry(2, 0), x.Exit(1) }, startS, 30f, speed, out EntityId id));
            return id;
        }

        [Fact]
        public void LaneChange_MovesTheVehicleAndTheRouteToTheAdjacentLane_ThenEndsTheManeuver()
        {
            var (x, s) = Cross();
            EntityId id = SpawnLeft(x, s, 10f, 12f);
            LaneId from = x.Entry(2, 0), to = x.Entry(2, 1);
            s.Update();
            ref readonly Vehicle v = ref s.GetVehicle(id.Index);
            Assert.Equal(to.Value, v.Track);
            Assert.Equal(to, s.GetRouteStep(id.Index, 0).Lane);
            Assert.Equal(s.Config.LaneChangeTicks, v.LcTicks);
            Assert.Equal(id.Index, s.LaneChangeGhost(from));
            Assert.Equal(1, s.LaneChangeCount);
            VK.CheckInvariants(s);
            for (int t = 0; t < s.Config.LaneChangeTicks; t++) s.Update();
            Assert.Equal(0, v.LcTicks);
            Assert.Equal(-1, s.LaneChangeGhost(from));
            Assert.Equal(1, s.LaneChangeCount); // no vuelve a cambiar: el carril ya conecta
            VK.CheckInvariants(s);
        }

        [Fact]
        public void Snapshot_LateralOffsetDecaysSmoothlyAndPositionIsContinuous()
        {
            var (x, s) = Cross();
            EntityId id = SpawnLeft(x, s, 10f, 12f);
            Vec2 prev = default;
            float prevLat = 0f;
            bool havePrev = false;
            var lats = new List<float>();
            for (int t = 0; t < 40; t++)
            {
                s.Update();
                Assert.True(s.Snapshots.Acquire(out _, out SnapshotFrame f));
                VehicleSnapshot snap = default;
                for (int k = 0; k < f.Count; k++) if (f.Data![k].Id == id.Index) snap = f.Data[k];
                s.Snapshots.Release();
                if (havePrev)
                {
                    // Un tick a ~12 m/s avanza ~1,2 m; la posicion no da saltos laterales de ancho de carril.
                    Assert.True(Vec2.Distance(prev, snap.Position) < 2.0f, "salto de " + Vec2.Distance(prev, snap.Position) + " m en el tick " + t);
                    Assert.True(Math.Abs(snap.Lateral) <= Math.Abs(prevLat) + 1e-4f, "el desplazamiento lateral debe decaer");
                }
                lats.Add(snap.Lateral);
                prev = snap.Position;
                prevLat = snap.Lateral;
                havePrev = true;
            }
            // Cambia hacia la izquierda: arranca desplazado a la derecha del carril nuevo (+) y termina en 0.
            Assert.True(lats[0] > 3.0f && lats[0] < 3.6f, "lateral inicial " + lats[0]);
            Assert.Equal(0f, lats[lats.Count - 1]);
        }

        [Fact]
        public void Ghost_FollowerInTheOldLaneKeepsBehindTheVehicleThatIsLeaving()
        {
            var (x, s) = Cross();
            // Un coche rapido pegado por detras del que va a cambiar, en el mismo carril.
            EntityId lead = SpawnLeft(x, s, 40f, 10f);
            Assert.True(s.TrySpawnPlanned(VehicleTypeId.Car, new[] { x.Entry(2, 0), x.Exit(3) }, 20f, 30f, 14f, out EntityId chaser));
            string? error = null;
            float minGap = float.PositiveInfinity;
            for (int t = 0; t < 80; t++)
            {
                s.Update();
                minGap = Math.Min(minGap, LCK.CheckNoOverlap(s, ref error));
            }
            _out.WriteLine("hueco minimo " + minGap);
            Assert.Null(error);
            Assert.True(s.LaneChangeCount >= 1);
            Assert.Equal(x.Entry(2, 1).Value, s.GetVehicle(lead.Index).Track);
            Assert.True(s.IsAlive(chaser));
        }

        [Fact]
        public void GapAcceptance_RefusesWhileAFastFollowerWouldHaveToBrakeHard_ThenChangesAfterItPasses()
        {
            var (x, s) = Cross();
            EntityId c = SpawnLeft(x, s, 70f, 8f);
            // Seguidor rapido en el carril izquierdo, 25 m por detras: meterse delante le obligaria a frenar mas de 3 m/s2.
            Assert.True(s.TrySpawnPlanned(VehicleTypeId.Car, new[] { x.Entry(2, 1), x.Exit(0, 1) }, 41f, 30f, 14f, out EntityId f));
            s.Update();
            Assert.Equal(x.Entry(2, 0).Value, s.GetVehicle(c.Index).Track);
            Assert.Equal(0, s.LaneChangeCount);
            Assert.True(s.LaneChangeDenials[(int)LaneChangeDenial.FollowerBrake] + s.LaneChangeDenials[(int)LaneChangeDenial.FollowerGap] > 0);
            float followerA = 0f;
            int changedAt = -1;
            for (int t = 1; t < 400 && changedAt < 0; t++)
            {
                s.Update();
                if (s.LaneChangeCount > 0) changedAt = t;
            }
            Assert.True(changedAt > 1, "el cambio debe esperar al hueco");
            // En el tick del cambio, quien esta detras del vehiculo en el carril nuevo no frena mas de b_safe (+ urgencia, aqui casi 0).
            ref readonly Vehicle cv = ref s.GetVehicle(c.Index);
            Assert.Equal(x.Entry(2, 1).Value, cv.Track);
            int fol = cv.Behind;
            if (fol >= 0) followerA = s.GetVehicle(fol).A;
            _out.WriteLine("cambio en el tick " + changedAt + ", aceleracion del seguidor " + followerA);
            Assert.True(followerA >= -(s.Config.BSafe + s.Config.LaneChangeUrgentExtraBrake) - 0.3f, "seguidor frena " + followerA);
        }

        [Fact]
        public void Blocked_VehicleWaitsBeforeTheStopLine_ThenAsksForReroute()
        {
            var (x, s) = Cross();
            // Carril izquierdo lleno de coches parados de punta a punta: no hay hueco nunca.
            float laneLen = x.Net.GetLane(x.Entry(2, 1)).Length;
            for (float pos = laneLen - 1f; pos > 25f; pos -= 7f)
            {
                Assert.True(s.TrySpawnPlanned(VehicleTypeId.Car, new[] { x.Entry(2, 1), x.Exit(0, 1) }, pos, 30f, 0f, out EntityId fz));
                s.SetFrozen(fz, true);
            }
            EntityId id = SpawnLeft(x, s, 5f, 12f);
            string? error = null;
            for (int t = 0; t < 900; t++)
            {
                s.Update();
                LCK.CheckNoOverlap(s, ref error);
            }
            Assert.Null(error);
            ref readonly Vehicle v = ref s.GetVehicle(id.Index);
            Assert.Equal(TrackKind.Lane, v.Kind); // no entro al cruce
            Assert.Equal(x.Entry(2, 0).Value, v.Track);
            Assert.True(v.S <= laneLen - s.Config.StopMargin + 0.1f, "paso la linea de parada: " + v.S);
            Assert.True((v.Flags & VehicleFlags.NeedsReroute) != 0, "ultimo recurso: re-ruta");
            Assert.Equal(1, s.LaneChangeRerouted);
            Assert.Equal(0, s.LaneChangeCount);
        }

        [Fact]
        public void Blocked_WhenTheCurrentLaneAlsoConnects_PlanIsRelaxedAndTheVehicleContinues()
        {
            var (x, s) = Cross();
            float laneLen = x.Net.GetLane(x.Entry(2, 1)).Length;
            for (float pos = laneLen - 1f; pos > 25f; pos -= 7f)
            {
                Assert.True(s.TrySpawnPlanned(VehicleTypeId.Car, new[] { x.Entry(2, 1), x.Exit(0, 1) }, pos, 30f, 0f, out EntityId fz));
                s.SetFrozen(fz, true);
            }
            // Recto hacia el carril 1 de salida: pide salir por el carril izquierdo, pero el derecho tambien conecta con la salida.
            Assert.True(s.TrySpawnPlanned(VehicleTypeId.Car, new[] { x.Entry(2, 0), x.Exit(0, 1) }, 5f, 30f, 12f, out EntityId id));
            for (int t = 0; t < 600 && s.GetRouteStep(id.Index, 1).Lane != x.Exit(0, 0); t++) s.Update();
            Assert.Equal(1, s.LaneChangeRelaxed);
            Assert.Equal(x.Exit(0, 0), s.GetRouteStep(id.Index, 1).Lane);
            for (int t = 0; t < 400 && s.IsAlive(id); t++) s.Update();
            Assert.False(s.IsAlive(id), "debe llegar a su destino saliendo por el carril derecho");
            Assert.Equal(0, s.LaneChangeRerouted);
        }

        // ------------------------------------------------------------------ red completa

        [Fact]
        public void Grid_1000Trips_NoneIsUnroutableAndLaneChangesHappen()
        {
            TrafficSimulation sim = LCK.Grid(11, 3f, 8, 600);
            int ticks = 0;
            while (sim.Trips.Generated < 1000 && ticks < 8000)
            {
                sim.Step();
                ticks++;
            }
            sim.Run(1500);
            TripGenerator t = sim.Trips;
            _out.WriteLine("generados " + t.Generated + " completados " + t.Completed + " perdidos " + t.Lost + " sin ruta " + t.Unroutable
                + " cambios " + sim.Vehicles.LaneChangeCount + " relajados " + sim.Vehicles.LaneChangeRelaxed + " re-rutas " + sim.Vehicles.LaneChangeRerouted
                + " denegaciones " + string.Join(",", sim.Vehicles.LaneChangeDenials));
            Assert.True(t.Generated >= 1000, "generados " + t.Generated);
            Assert.Equal(0, t.Unroutable);
            Assert.True(sim.Vehicles.LaneChangeCount > 100, "cambios " + sim.Vehicles.LaneChangeCount);
            Assert.True(t.Completed > 500, "completados " + t.Completed);
            Assert.Equal(t.Generated, t.Completed + sim.Vehicles.AliveCount + t.Pending + t.Lost);
        }

        public static IEnumerable<object[]> Seeds()
        {
            for (ulong s = 1; s <= 5; s++) yield return new object[] { s };
        }

        [Theory]
        [MemberData(nameof(Seeds))]
        public void Grid_10000Ticks_NoOverlapWithFrequentLaneChanges(ulong seed)
        {
            TrafficSimulation sim = LCK.Grid(seed, 5f, 6, 700);
            string? error = null;
            float minGap = float.PositiveInfinity;
            int newManeuvers = 0;
            float worstFollowerA = 0f;
            VehicleSystem hs = sim.Vehicles;
            for (int t = 0; t < 10000; t++)
            {
                sim.Step();
                minGap = Math.Min(minGap, LCK.CheckNoOverlap(hs, ref error));
                if (error != null) break;
                // Seguidor del que acaba de cambiar (LcTicks recien puesto): su aceleracion tras el cambio.
                for (int i = 0; i < hs.HighWater; i++)
                {
                    ref readonly Vehicle v = ref hs.GetVehicle(i);
                    if (!v.IsAlive || v.LcTicks != hs.Config.LaneChangeTicks) continue;
                    newManeuvers++;
                    if (v.Behind >= 0) worstFollowerA = Math.Min(worstFollowerA, hs.GetVehicle(v.Behind).A);
                }
            }
            _out.WriteLine("semilla " + seed + ": cambios " + hs.LaneChangeCount + " (maniobras vistas " + newManeuvers + "), hueco minimo " + minGap.ToString("F2")
                + ", peor aceleracion del seguidor " + worstFollowerA.ToString("F2") + ", relajados " + hs.LaneChangeRelaxed + ", re-rutas " + hs.LaneChangeRerouted
                + ", vivos " + hs.AliveCount + ", completados " + sim.Trips.Completed + ", sin ruta " + sim.Trips.Unroutable);
            Assert.Null(error);
            Assert.True(hs.LaneChangeCount >= 200, "pocos cambios de carril: " + hs.LaneChangeCount);
            Assert.True(minGap >= hs.Config.MinGap - 1e-3f, "hueco minimo " + minGap);
            Assert.Equal(0, sim.Trips.Unroutable);
            VK.CheckInvariants(hs);
        }

        private static ulong HashRun(ulong seed, int ticks)
        {
            TrafficSimulation sim = LCK.Grid(seed, 5f, 6, 600);
            sim.Run(ticks);
            return sim.Vehicles.ComputeHash() ^ ((ulong)sim.Vehicles.LaneChangeCount << 40);
        }

        [Fact]
        public void Determinism_SameSeedSameState_DifferentSeedDifferent()
        {
            Assert.Equal(HashRun(3, 4000), HashRun(3, 4000));
            Assert.NotEqual(HashRun(3, 4000), HashRun(4, 4000));
        }

        [Fact]
        public void SteadyState_WithLaneChanges_ZeroAllocationsPerTick()
        {
            TrafficSimulation sim = LCK.Grid(31, 4f, 6, 600);
            sim.Run(3000); // calentamiento: colas, caches y pools
            int changes0 = sim.Vehicles.LaneChangeCount;
            long before = GC.GetAllocatedBytesForCurrentThread();
            sim.Run(1000);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(sim.Vehicles.LaneChangeCount - changes0 > 20, "sin cambios de carril en la ventana medida");
            Assert.Equal(0L, bytes);
        }

        [Fact]
        public void Disabled_VehiclesNeverChangeLane()
        {
            TrafficSimulation sim = LCK.Grid(5, 4f, 6, 400, laneChange: false);
            sim.Run(3000);
            Assert.Equal(0, sim.Vehicles.LaneChangeCount);
        }
    }
}
