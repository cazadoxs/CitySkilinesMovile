using System;
using System.Collections.Generic;
using Urbe.Sim.Ids;
using Urbe.Sim.Junctions;
using Urbe.Sim.Roads;
using Urbe.Sim.Tests.Junctions;
using Urbe.Sim.Tests.Roads;
using Urbe.Sim.Traffic;
using Urbe.Sim.Vehicles;
using Xunit;

namespace Urbe.Sim.Tests.Vehicles
{
    /// <summary>M1-17: deuda de la revision de M1 (puntos 8, 9, 10, 16, 17, 18 y 19 de docs/reviews/M1-13.md).</summary>
    public class ReviewDebtTests
    {
        // ------------------------------------------------------------------ punto 8

        private sealed class RerouteScene
        {
            public RoadNetwork Net = null!;
            public TrafficSimulation Sim = null!;
            public EntityId S1, S2;
            public EntityId Obstacle;
            public int Car = -1;
        }

        /// <summary>
        /// S0 -> S1 (corto: su carril mide 8 m, menos que un camion) -> S2 -> S3 y un desvio entre los nodos de S2. Un obstaculo parado
        /// al inicio de S2 impide entrar al camion del generador, que queda en el final de S1 con la trasera aun en la conexion de
        /// entrada. Luego se elimina S2: la ruta se rompe y el camion necesita re-ruta.
        /// </summary>
        private static RerouteScene BuildReroute(Action<TripGeneratorConfig>? tweak = null)
        {
            var sc = new RerouteScene();
            RoadNetwork n = sc.Net = Kit.New();
            EntityId n0 = Kit.Node(n, 0, 0), n1 = Kit.Node(n, 100, 0), n2 = Kit.Node(n, 120, 0), n3 = Kit.Node(n, 220, 0);
            EntityId n4 = Kit.Node(n, 170, 60), n5 = Kit.Node(n, 320, 0), n6 = Kit.Node(n, 100, 80);
            EntityId s0 = Kit.Seg(n, n0, n1, RoadTypeIds.Street2);
            sc.S1 = Kit.Seg(n, n1, n2, RoadTypeIds.Street2);
            sc.S2 = Kit.Seg(n, n2, n3, RoadTypeIds.Street2);
            Kit.Seg(n, n1, n6, RoadTypeIds.Street2);
            Kit.Seg(n, n2, n4, RoadTypeIds.Street2);
            Kit.Seg(n, n4, n3, RoadTypeIds.Street2);
            EntityId s3 = Kit.Seg(n, n3, n5, RoadTypeIds.Street2);
            Assert.True(n.GetLane(new LaneId(sc.S1.Index, 0)).Length < 10f);
            var opt = new TrafficSimulationOptions { VehicleCapacity = 64, Vehicle = new VehicleConfig { DriverVariation = false } };
            opt.Trips.TripsPerSecond = 0.5f;
            opt.Trips.MaxActiveVehicles = 2;
            opt.Trips.TruckFraction = 1f;
            tweak?.Invoke(opt.Trips);
            sc.Sim = new TrafficSimulation(n, 7, opt);
            sc.Sim.Trips.AddDemandPoint(s0, 1f, 0f);
            sc.Sim.Trips.AddDemandPoint(s3, 0f, 1f);
            // Obstaculo al inicio de S2: no queda hueco para un camion (13 m) al otro lado.
            Assert.True(sc.Sim.Vehicles.TrySpawn(VehicleTypeId.Car, new[] { new LaneId(sc.S2.Index, 0) }, 6f, 50f, 0f, out sc.Obstacle));
            sc.Sim.Vehicles.SetFrozen(sc.Obstacle, true);
            return sc;
        }

        /// <summary>Avanza hasta que el camion del generador esta parado al final de S1 con la trasera en la conexion y rompe su ruta.</summary>
        private static void StopAndBreakRoute(RerouteScene sc)
        {
            VehicleSystem veh = sc.Sim.Vehicles;
            int s1Lane = new LaneId(sc.S1.Index, 0).Value;
            for (int t = 0; t < 3000 && sc.Car < 0; t++)
            {
                sc.Sim.Step();
                for (int i = 0; i < veh.HighWater; i++)
                {
                    ref readonly Vehicle v = ref veh.GetVehicle(i);
                    if (!v.IsAlive || i == sc.Obstacle.Index || v.Kind != TrackKind.Lane || v.Track != s1Lane) continue;
                    if (v.PrevConnection >= 0 && v.V < 0.05f && v.WaitTicks > 20) sc.Car = i;
                }
            }
            Assert.True(sc.Car >= 0, "el camion no se paro con la trasera en la conexion");
            Assert.True(sc.Net.RemoveSegment(sc.S2, null));
            sc.Sim.Step();
            ref readonly Vehicle c = ref veh.GetVehicle(sc.Car);
            Assert.True((c.Flags & VehicleFlags.NeedsReroute) != 0);
            Assert.True(c.PrevConnection >= 0);
        }

        [Fact]
        public void P8_RerouteWhileTheRearIsStillInAConnection_WaitsInsteadOfBurningAttemptsAndAbandoning()
        {
            RerouteScene sc = BuildReroute();
            StopAndBreakRoute(sc);
            VehicleSystem veh = sc.Sim.Vehicles;
            EntityId car = veh.IdOf(sc.Car);
            long lost0 = sc.Sim.Trips.Lost;
            // 30 s: antes se agotaban los 20 intentos (unos 4 s) y el viaje se abandonaba.
            for (int t = 0; t < 300; t++) sc.Sim.Step();
            Assert.True(veh.IsAlive(car), "el viaje se abandono con la trasera aun en la conexion");
            Assert.Equal(lost0, sc.Sim.Trips.Lost);
            Assert.Equal(0, sc.Sim.Trips.Rerouted);
        }

        [Fact]
        public void P8_RerouteBlockedForeverInAConnection_IsAbandonedAfterTheBlockedLimit()
        {
            RerouteScene sc = BuildReroute(c => c.MaxRerouteBlockedSeconds = 5f);
            StopAndBreakRoute(sc);
            VehicleSystem veh = sc.Sim.Vehicles;
            EntityId car = veh.IdOf(sc.Car);
            for (int t = 0; t < 30; t++) sc.Sim.Step();
            Assert.True(veh.IsAlive(car));
            for (int t = 0; t < 100; t++) sc.Sim.Step();
            Assert.False(veh.IsAlive(car));
            Assert.True(sc.Sim.Trips.Lost >= 1);
        }

        // ------------------------------------------------------------------ punto 16

        [Fact]
        public void P16_ReplaceRouteInsideAConnection_KeepsThePermitWhenTheExitDoesNotChange()
        {
            VK.TJunction tj = VK.MakeT();
            RoadNetwork n = tj.Net;
            var gate = new RecordingGate(n);
            VehicleSystem s = VK.Sys(n, 64, 1, false, gate);
            LaneId stem = VK.Lane(n, tj.Stem, true), east = VK.Lane(n, tj.East, true);
            EntityId f = VK.Spawn(s, VehicleTypeId.Car, new[] { stem, east }, 0f, 60f);
            for (int g = 0; g < 2000 && s.GetVehicle(f).Kind != TrackKind.Connection; g++) s.Update();
            ref readonly Vehicle v = ref s.GetVehicle(f);
            Assert.Equal(TrackKind.Connection, v.Kind);
            Assert.True((v.Flags & VehicleFlags.HasEntryPermit) != 0);
            float reserved = s.ReservedLength(east);
            Assert.True(reserved > 0f);
            Assert.True(s.TryReplaceRoute(f, new[] { stem, east }, 70f));
            Assert.True((v.Flags & VehicleFlags.HasEntryPermit) != 0, "el permiso se retiro aunque la salida no cambia");
            Assert.Equal(reserved, s.ReservedLength(east), 3);
            Assert.Equal(0, gate.Cancelled);
            VK.Run(s, 1500);
            Assert.False(s.IsAlive(f));
            Assert.Equal(1, s.ArrivedCount);
            Assert.Equal(0f, s.ReservedLength(east), 3);
            VK.CheckInvariants(s);
        }

        // ------------------------------------------------------------------ punto 17

        [Fact]
        public void P17_GetCurrentLane_IsTheCursorLaneNotTheNextArrivalLane()
        {
            VK.TJunction tj = VK.MakeT();
            VehicleSystem s = VK.Sys(tj.Net, 64);
            LaneId stem = VK.Lane(tj.Net, tj.Stem, true), east = VK.Lane(tj.Net, tj.East, true);
            EntityId f = VK.Spawn(s, VehicleTypeId.Car, new[] { stem, east }, 0f, 60f);
            Assert.Equal(stem, s.GetCurrentLane(f.Index));
            Assert.Equal(east, s.GetRouteStep(f.Index, 1).Lane);
            for (int g = 0; g < 2000 && s.GetVehicle(f).Kind != TrackKind.Connection; g++) s.Update();
            Assert.Equal(stem, s.GetCurrentLane(f.Index)); // dentro de la conexion el cursor sigue en el carril de origen
            for (int g = 0; g < 2000 && s.GetVehicle(f).Kind == TrackKind.Connection; g++) s.Update();
            Assert.Equal(east, s.GetCurrentLane(f.Index));
        }

        // ------------------------------------------------------------------ puntos 18 y 10 (carril equivocado)

        private static (Crossing x, VehicleSystem s, JunctionController jc) Cross(Action<VehicleConfig>? tweak = null)
        {
            Crossing x = JK.X(RoadTypeIds.Avenue4, RoadTypeIds.Street2);
            var cfg = new VehicleConfig { DriverVariation = false };
            tweak?.Invoke(cfg);
            var jc = new JunctionController(x.Net);
            return (x, new VehicleSystem(x.Net, jc, 256, 1, cfg), jc);
        }

        /// <summary>Carril izquierdo del brazo 2 lleno de coches parados y un coche en el derecho que quiere girar a la izquierda.</summary>
        private static EntityId BlockedWrongLane(Crossing x, VehicleSystem s)
        {
            float laneLen = x.Net.GetLane(x.Entry(2, 1)).Length;
            for (float pos = laneLen - 1f; pos > 25f; pos -= 7f)
            {
                Assert.True(s.TrySpawnPlanned(VehicleTypeId.Car, new[] { x.Entry(2, 1), x.Exit(0, 1) }, pos, 30f, 0f, out EntityId fz));
                s.SetFrozen(fz, true);
            }
            Assert.True(s.TrySpawnPlanned(VehicleTypeId.Car, new[] { x.Entry(2, 0), x.Exit(1) }, 5f, 30f, 12f, out EntityId id));
            return id;
        }

        [Fact]
        public void P18_RequestReroute_StopsAtTheEndOfTheLaneLikeMarkForReroute()
        {
            var (x, s, _) = Cross();
            EntityId id = BlockedWrongLane(x, s);
            for (int t = 0; t < 900 && s.LaneChangeRerouted == 0; t++) s.Update();
            Assert.Equal(1, s.LaneChangeRerouted);
            ref readonly Vehicle v = ref s.GetVehicle(id.Index);
            Assert.True((v.Flags & VehicleFlags.NeedsReroute) != 0);
            float laneLen = x.Net.GetLane(x.Entry(2, 0)).Length;
            Assert.Equal(laneLen - s.Config.StopMargin - s.GetVehicleType(v.Type).S0, v.TargetS, 3);
        }

        [Fact]
        public void P10_WrongLaneVehicle_WaitsForTheVehicleThatBlocksItsLaneChange()
        {
            var (x, s, jc) = Cross(c => { c.LaneChangeRelaxWaitTicks = 100000; c.LaneChangeRerouteWaitTicks = 100000; });
            EntityId id = BlockedWrongLane(x, s);
            var resolver = new GridlockResolver(x.Net, s, jc, null);
            for (int t = 0; t < 400; t++) s.Update();
            ref readonly Vehicle v = ref s.GetVehicle(id.Index);
            Assert.Equal(x.Entry(2, 0).Value, v.Track);
            Assert.True(v.WaitTicks >= resolver.Config.DetectSeconds / s.Config.Dt);
            int blocker = s.LaneChangeBlocker(id.Index);
            Assert.True(blocker >= 0);
            Assert.Equal(x.Entry(2, 1).Value, s.GetVehicle(blocker).Track);
            Assert.Equal(blocker, resolver.WaitsFor(id.Index));
            // Un vehiculo en un carril que ya conecta no tiene bloqueador de cambio de carril.
            Assert.Equal(-1, s.LaneChangeBlocker(blocker));
        }

        // ------------------------------------------------------------------ puntos 19 y 9 (a nivel de puerta)

        private static EntryRequest Req(RoadNetwork n, int vehicle, int conn, float length = 4.5f)
        {
            ref readonly LaneConnection c = ref n.GetConnection(conn);
            return new EntryRequest
            {
                Vehicle = vehicle,
                From = c.From,
                Connection = conn,
                To = c.To,
                DistanceToStop = 5f,
                Speed = 0f,
                Space = length + 2f,
                Length = length,
                StoppedTicks = 100,
            };
        }

        private static List<int> Resolve(IJunctionGate gate, ulong tick, params EntryRequest[] reqs)
        {
            gate.BeginTick(tick);
            foreach (EntryRequest r in reqs) gate.RequestEntry(in r);
            var list = new List<int>();
            foreach (EntryGrant g in gate.ResolveRequests()) list.Add(g.Vehicle);
            return list;
        }

        [Fact]
        public void P19_GapOnlyGate_KeepsSeveralForcedPassesUntilEachOneIsGranted()
        {
            Crossing t = JK.T(RoadTypeIds.Street2, RoadTypeIds.Street2);
            RoadNetwork n = t.Net;
            int cl = t.ConnTurn(3, TurnType.Left), cr = t.ConnTurn(3, TurnType.Right);
            var gate = new GapOnlyGate(n);
            var view = new FakeView { DefaultFree = 0f };
            gate.Bind(view, 16);
            gate.ForceNextGrant(3);
            gate.ForceNextGrant(5);
            Assert.True(gate.IsForced(3));
            Assert.True(gate.IsForced(5));
            gate.CancelForce(7); // no forzado: no cambia nada
            Assert.True(gate.IsForced(3) && gate.IsForced(5));
            // Sin hueco a la salida se conceden los dos forzados (y solo ellos), y dejan de estar forzados al concederse.
            List<int> granted = Resolve(gate, 1, Req(n, 3, cl), Req(n, 5, cr), Req(n, 8, cl));
            Assert.Equal(new[] { 3, 5 }, granted);
            Assert.False(gate.IsForced(3));
            Assert.False(gate.IsForced(5));
            gate.ForceNextGrant(9);
            gate.CancelForce(9);
            gate.ForceNextGrant(10);
            gate.OnVehicleRemoved(10);
            Assert.False(gate.IsForced(9));
            Assert.False(gate.IsForced(10));
        }

        [Fact]
        public void P9_ForcedPass_StillNeedsTheVehicleLengthAtTheExitUnlessTheExitIsInTheCycle_JunctionController()
        {
            Crossing t = JK.T(RoadTypeIds.Street2, RoadTypeIds.Avenue4);
            RoadNetwork n = t.Net;
            int cl = t.ConnTurn(3, TurnType.Left);
            int to = n.GetConnection(cl).To.Value;

            var jc = new JunctionController(n);
            var view = new FakeView();
            jc.Bind(view, 64);
            view.Free[to] = 1f; // un coche mide 4,5 m: no cabe
            jc.ForceNextGrant(1, false);
            Assert.Empty(Resolve(jc, 1, Req(n, 1, cl)));
            Assert.Equal(GrantDecision.DeniedExitFull, jc.GetLastDecision(1));
            Assert.True(jc.IsForced(1), "el forzado sigue pendiente");
            view.Free[to] = 4.6f; // cabe el vehiculo (no el vehiculo mas s0): minimo
            Assert.Equal(new[] { 1 }, Resolve(jc, 2, Req(n, 1, cl)));
            Assert.False(jc.IsForced(1));

            var jc2 = new JunctionController(n);
            var view2 = new FakeView();
            jc2.Bind(view2, 64);
            view2.Free[to] = -3f; // el carril de salida es del ciclo: se salta el hueco
            jc2.ForceNextGrant(2, true);
            Assert.Equal(new[] { 2 }, Resolve(jc2, 1, Req(n, 2, cl)));
        }

        [Fact]
        public void P9_ForcedPass_StillNeedsTheVehicleLengthAtTheExitUnlessTheExitIsInTheCycle_GapOnlyGate()
        {
            Crossing t = JK.T(RoadTypeIds.Street2, RoadTypeIds.Avenue4);
            RoadNetwork n = t.Net;
            int cl = t.ConnTurn(3, TurnType.Left);
            int to = n.GetConnection(cl).To.Value;
            var gate = new GapOnlyGate(n);
            var view = new FakeView();
            gate.Bind(view, 16);
            view.Free[to] = 1f;
            gate.ForceNextGrant(1, false);
            Assert.Empty(Resolve(gate, 1, Req(n, 1, cl)));
            Assert.True(gate.IsForced(1));
            view.Free[to] = 4.6f;
            Assert.Equal(new[] { 1 }, Resolve(gate, 2, Req(n, 1, cl)));
            Assert.False(gate.IsForced(1));
            view.Free[to] = -3f;
            gate.ForceNextGrant(2, true);
            Assert.Equal(new[] { 2 }, Resolve(gate, 3, Req(n, 2, cl)));
        }
    }
}
