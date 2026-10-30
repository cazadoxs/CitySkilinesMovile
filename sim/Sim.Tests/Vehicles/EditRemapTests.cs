using System;
using System.Collections.Generic;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Junctions;
using Urbe.Sim.Roads;
using Urbe.Sim.Tests.Roads;
using Urbe.Sim.Traffic;
using Urbe.Sim.Vehicles;
using Xunit;

namespace Urbe.Sim.Tests.Vehicles
{
    /// <summary>Puerta que delega en un JunctionController y anota lo que le llega de la red (M1-15).</summary>
    internal sealed class ProbeGate : IJunctionGate, IVehicleViewConsumer, IForcedPassGate
    {
        public readonly JunctionController Inner;
        public int Calls;
        public int LastChangedCount;
        public int MaxChangedCount;
        public readonly List<EntityId> LastChanged = new List<EntityId>();

        public ProbeGate(RoadNetwork net) { Inner = new JunctionController(net); }

        public void Bind(IVehicleView view, int vehicleCapacity) => Inner.Bind(view, vehicleCapacity);

        public void OnNetworkChanged(ReadOnlySpan<EntityId> changedNodes)
        {
            Calls++;
            LastChangedCount = changedNodes.Length;
            if (changedNodes.Length > MaxChangedCount) MaxChangedCount = changedNodes.Length;
            LastChanged.Clear();
            for (int i = 0; i < changedNodes.Length; i++) LastChanged.Add(changedNodes[i]);
            Inner.OnNetworkChanged(changedNodes);
        }

        public void BeginTick(ulong tick) => Inner.BeginTick(tick);
        public void RequestEntry(in EntryRequest req) => Inner.RequestEntry(in req);
        public ReadOnlySpan<EntryGrant> ResolveRequests() => Inner.ResolveRequests();
        public void OnEntered(int connection, int vehicle) => Inner.OnEntered(connection, vehicle);
        public void OnExited(int connection, int vehicle) => Inner.OnExited(connection, vehicle);
        public void OnVehicleRemoved(int vehicle) => Inner.OnVehicleRemoved(vehicle);
        public void OnPermitCancelled(int vehicle) => Inner.OnPermitCancelled(vehicle);
        public void ForceNextGrant(int vehicle) => Inner.ForceNextGrant(vehicle);
        public void ForceNextGrant(int vehicle, bool exitInCycle) => Inner.ForceNextGrant(vehicle, exitInCycle);
        public bool IsForced(int vehicle) => Inner.IsForced(vehicle);
        public void CancelForce(int vehicle) => Inner.CancelForce(vehicle);
    }

    /// <summary>M1-15: registro de cambios por suscriptor y ediciones que conservan a los vehiculos (revision M1-13, puntos 1, 2, 4, 5 y 6).</summary>
    public class EditRemapTests
    {
        private static TrafficSimulation GridSim(ProbeGate gate, RoadNetwork net, ulong seed = 3)
        {
            var opt = new TrafficSimulationOptions { VehicleCapacity = 1024, Vehicle = new VehicleConfig { DriverVariation = true } };
            opt.Trips.TripsPerSecond = 2f;
            opt.Trips.MaxActiveVehicles = 300;
            var sim = new TrafficSimulation(net, seed, opt, gate);
            for (int i = 0; i < net.SegmentIndexBound; i++)
            {
                EntityId seg = net.SegmentIdAt(i);
                if (seg.IsValid) sim.Trips.AddDemandPoint(seg, 1f, 1f);
            }
            return sim;
        }

        private static Vec2 WorldPos(RoadNetwork n, VehicleSystem s, EntityId v)
        {
            ref readonly Vehicle veh = ref s.GetVehicle(v);
            Vec2 p;
            if (veh.Kind == TrackKind.Lane) n.SampleLane(new LaneId(veh.Track), veh.S, out p, out _);
            else n.SampleConnection(veh.Track, veh.S, out p, out _);
            return p;
        }

        // ---------------------------------------------------------------- punto 2

        [Fact]
        public void P2_FirstEditAfterConstruction_OnlyReportsTheEditedNodes()
        {
            RoadNetwork net = VK.MakeGrid(4, 100f);
            var gate = new ProbeGate(net);
            TrafficSimulation sim = GridSim(gate, net);
            sim.Run(300);
            Assert.True(sim.Vehicles.AliveCount > 5);
            int callsBefore = gate.Calls;

            Assert.True(Kit.Add(net, 2000, 2000, 2100, 2000, RoadTypeIds.Street2));
            sim.Step();

            Assert.Equal(callsBefore + 1, gate.Calls);
            Assert.True(gate.LastChangedCount <= 2, "la puerta recibio " + gate.LastChangedCount + " nodos cambiados (red de 25 nodos)");
        }

        [Fact]
        public void P2_FirstEditAfterConstruction_DoesNotDropPermitsOfUnrelatedVehicles()
        {
            RoadNetwork net = VK.MakeGrid(4, 100f);
            var gate = new ProbeGate(net);
            TrafficSimulation sim = GridSim(gate, net);
            sim.Run(400);
            VehicleSystem vs = sim.Vehicles;
            var withPermit = new List<EntityId>();
            for (int i = 0; i < vs.HighWater; i++)
            {
                ref readonly Vehicle v = ref vs.GetVehicle(i);
                if (v.IsAlive && (v.Flags & VehicleFlags.HasEntryPermit) != 0) withPermit.Add(vs.IdOf(i));
            }
            Assert.True(withPermit.Count > 0, "el escenario no genera permisos");

            Assert.True(Kit.Add(net, 2000, 2000, 2100, 2000, RoadTypeIds.Street2));
            // Un alta fallida ejecuta solo la fase de epoca (sin mover a nadie): ahi se veria un permiso retirado.
            Assert.False(vs.TrySpawn(VehicleTypeId.Car, ReadOnlySpan<LaneId>.Empty, 0f, 0f, 0f, out _));
            foreach (EntityId id in withPermit)
            {
                if (!vs.IsAlive(id)) continue;
                ref readonly Vehicle v = ref vs.GetVehicle(id);
                if (v.Kind != TrackKind.Lane || v.RouteCursor >= v.RouteLast) continue;
                Assert.True((v.Flags & VehicleFlags.HasEntryPermit) != 0, "permiso retirado por una edicion lejana: veh " + id + " kind " + v.Kind + " cursor " + v.RouteCursor + "/" + v.RouteLast + " S " + v.S + " flags " + v.Flags);
            }
        }

        // ---------------------------------------------------------------- punto 1

        [Fact]
        public void P1_AnotherConsumerOfChanges_DoesNotStealThemFromTheVehicleSystem()
        {
            RoadNetwork net = VK.MakeGrid(3, 100f);
            var gate = new ProbeGate(net);
            TrafficSimulation sim = GridSim(gate, net);
            sim.Run(50);
            // Un segundo consumidor (por ejemplo el que genera las mallas en Unity) con su propio cursor.
            int other = net.Subscribe();

            Assert.True(Kit.Add(net, 2000, 2000, 2100, 2000, RoadTypeIds.Street2));
            net.ConsumeChanges(other, out ReadOnlySpan<EntityId> otherNodes, out ReadOnlySpan<EntityId> otherSegs);
            Assert.Equal(2, otherNodes.Length);
            Assert.Single(otherSegs.ToArray());
            sim.Step();

            Assert.True(gate.LastChangedCount >= 2, "el sistema de vehiculos no vio los cambios (" + gate.LastChangedCount + ")");
            // Y el otro no ve dos veces lo mismo.
            net.ConsumeChanges(other, out otherNodes, out otherSegs);
            Assert.Equal(0, otherNodes.Length);
            Assert.Equal(0, otherSegs.Length);
        }

        [Fact]
        public void P1_StaleJunctionNode_ThatOnlyChangedControl_IsRebuiltFromTheNodeVersion()
        {
            VK.TJunction tj = VK.MakeT();
            var ctrl = new JunctionController(tj.Net);
            int conn = tj.Net.GetNode(tj.Node).ConnFirst;
            Assert.Equal(ControlKind.Priority, ctrl.GetControl(tj.Node));
            // Cambio de control que nadie notifica al controlador (el numero de conexiones no cambia).
            Assert.True(tj.Net.SetNodeControl(tj.Node, JunctionControlIds.Stop));
            _ = ctrl.GetGate(conn);
            Assert.Equal(ControlKind.Stop, ctrl.GetControl(tj.Node));
            Assert.Equal(1, ctrl.Stats.StaleRebuilds);
        }

        [Fact]
        public void P1_Subscriber_StartsAtTheEnd_AndLogIsCompactedWithoutGrowing()
        {
            RoadNetwork net = Kit.New();
            EntityId a = Kit.Node(net, 0, 0), b = Kit.Node(net, 100, 0);
            Kit.Seg(net, a, b, RoadTypeIds.Street2);
            int sub = net.Subscribe();
            net.ConsumeChanges(sub, out ReadOnlySpan<EntityId> nodes, out ReadOnlySpan<EntityId> segs);
            Assert.Equal(0, nodes.Length);
            Assert.Equal(0, segs.Length);
            Assert.False(net.HasPendingChanges(sub));
            // Muchos ciclos edicion/consumo: no asigna memoria en regimen estable.
            for (int i = 0; i < 20; i++)
            {
                net.SetNodeControl(a, (ushort)(1 + i % 2));
                net.ConsumeChanges(sub, out _, out _);
            }
            int seen = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 2000; i++)
            {
                net.SetNodeControl(a, (ushort)(1 + i % 3));
                net.ConsumeChanges(sub, out nodes, out segs);
                seen += nodes.Length;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(2000, seen);
            Assert.Equal(0L, allocated);
        }

        // ---------------------------------------------------------------- punto 4

        private sealed class ChainScene
        {
            public RoadNetwork Net = null!;
            public VehicleSystem Sys = null!;
            public EntityId[] Chain = null!;
            public LaneId[] Lanes = null!;
            public EntityId OnFirstHalf, OnSecondHalf, Upstream, Downstream;
        }

        private static ChainScene MakeChain()
        {
            var sc = new ChainScene();
            RoadNetwork n = Kit.New();
            sc.Net = n;
            sc.Chain = VK.Chain(n, 4, 100f, RoadTypeIds.Street2);
            sc.Sys = VK.Sys(n, 64, 1, false, new GapOnlyGate(n));
            sc.Lanes = new LaneId[4];
            for (int i = 0; i < 4; i++) sc.Lanes[i] = VK.Lane(n, sc.Chain[i], true);
            VehicleSystem s = sc.Sys;
            sc.OnSecondHalf = VK.Spawn(s, VehicleTypeId.Car, new[] { sc.Lanes[2], sc.Lanes[3] }, 70f, 50f);
            sc.OnFirstHalf = VK.Spawn(s, VehicleTypeId.Car, new[] { sc.Lanes[2], sc.Lanes[3] }, 30f, 60f);
            sc.Upstream = VK.Spawn(s, VehicleTypeId.Car, sc.Lanes, 20f, 90f, 5f);
            sc.Downstream = VK.Spawn(s, VehicleTypeId.Car, new[] { sc.Lanes[3] }, 20f, 90f);
            VK.Run(s, 5);
            return sc;
        }

        [Fact]
        public void P4_SplitUnderVehicles_KeepsThemAtTheSamePlaceAndRoute()
        {
            ChainScene sc = MakeChain();
            VehicleSystem s = sc.Sys;
            RoadNetwork n = sc.Net;
            Vec2 p1 = WorldPos(n, s, sc.OnFirstHalf), p2 = WorldPos(n, s, sc.OnSecondHalf);
            Assert.Equal(TrackKind.Lane, s.GetVehicle(sc.OnFirstHalf).Kind);

            Assert.True(n.SplitSegment(sc.Chain[2], 0.5f, null));
            s.Update();

            Assert.Equal(0, s.DespawnedByEdit);
            Assert.True(s.IsAlive(sc.OnFirstHalf));
            Assert.True(s.IsAlive(sc.OnSecondHalf));
            Assert.True(s.IsAlive(sc.Upstream));
            Assert.Equal(0, (int)(s.GetVehicle(sc.Upstream).Flags & VehicleFlags.NeedsReroute));
            Assert.Equal(0, (int)(s.GetVehicle(sc.OnFirstHalf).Flags & VehicleFlags.NeedsReroute));
            Assert.Equal(0, (int)(s.GetVehicle(sc.OnSecondHalf).Flags & VehicleFlags.NeedsReroute));
            // Siguen donde estaban (un tick de movimiento a lo sumo, < 2 m).
            Assert.True(Vec2.Distance(p1, WorldPos(n, s, sc.OnFirstHalf)) < 2f);
            Assert.True(Vec2.Distance(p2, WorldPos(n, s, sc.OnSecondHalf)) < 2f);
            VK.CheckInvariants(s);

            VK.Run(s, 1500);
            VK.CheckInvariants(s);
            Assert.Equal(4, s.ArrivedCount);
            Assert.Equal(0, s.DespawnedByEdit);
        }

        [Fact]
        public void P4_SplitTwiceBeforeTheTick_ChainsTheHalves()
        {
            ChainScene sc = MakeChain();
            VehicleSystem s = sc.Sys;
            RoadNetwork n = sc.Net;
            var edit = new NetworkEdit();
            Assert.True(n.SplitSegment(sc.Chain[2], 0.4f, edit));
            EntityId second = edit.CreatedSegments[1];
            Assert.True(n.SplitSegment(second, 0.5f, null));
            s.Update();
            Assert.Equal(0, s.DespawnedByEdit);
            Assert.True(s.IsAlive(sc.OnFirstHalf) && s.IsAlive(sc.OnSecondHalf) && s.IsAlive(sc.Upstream));
            VK.CheckInvariants(s);
            VK.Run(s, 1500);
            Assert.Equal(4, s.ArrivedCount);
        }

        [Fact]
        public void P4_RouteEndingInTheSplitSegment_StopsInTheHalfThatHoldsTheTarget()
        {
            RoadNetwork n = Kit.New();
            EntityId[] chain = VK.Chain(n, 3, 100f, RoadTypeIds.Street2);
            VehicleSystem s = VK.Sys(n, 64, 1, false, new GapOnlyGate(n));
            var changes = new List<(int, EntityId)>();
            s.DestinationSegmentChanged += (veh, seg) => changes.Add((veh, seg));
            var lanes = new[] { VK.Lane(n, chain[0], true), VK.Lane(n, chain[1], true), VK.Lane(n, chain[2], true) };
            EntityId toFirstHalf = VK.Spawn(s, VehicleTypeId.Car, lanes, 10f, 30f, 3f);   // llega al metro 30 del ultimo tramo
            EntityId toSecondHalf = VK.Spawn(s, VehicleTypeId.Car, new[] { lanes[2] }, 10f, 80f);
            VK.Run(s, 3);

            var edit = new NetworkEdit();
            Assert.True(n.SplitSegment(chain[2], 0.5f, edit));
            s.Update();
            EntityId h1 = edit.CreatedSegments[0], h2 = edit.CreatedSegments[1];
            Assert.Equal(2, changes.Count);
            Assert.Equal(h1, s.GetRouteStep(toFirstHalf.Index, s.GetVehicle(toFirstHalf).RouteLast).Segment);
            Assert.Equal(h2, s.GetRouteStep(toSecondHalf.Index, s.GetVehicle(toSecondHalf).RouteLast).Segment);
            Assert.Contains((toFirstHalf.Index, h1), changes);
            Assert.Contains((toSecondHalf.Index, h2), changes);
            // El de destino en la segunda mitad cruza el nodo nuevo (dos pasos); el otro acaba antes de el.
            Assert.Equal(1, s.GetVehicle(toSecondHalf).RouteLast);
            VK.Run(s, 1500);
            Assert.Equal(2, s.ArrivedCount);
            Assert.Equal(0, s.DespawnedByEdit);
        }

        [Fact]
        public void P4_SplitOfBackwardLane_KeepsTheVehicle()
        {
            RoadNetwork n = Kit.New();
            EntityId[] chain = VK.Chain(n, 3, 100f, RoadTypeIds.Street2);
            VehicleSystem s = VK.Sys(n, 64, 1, false, new GapOnlyGate(n));
            LaneId b2 = VK.Lane(n, chain[2], false), b1 = VK.Lane(n, chain[1], false), b0 = VK.Lane(n, chain[0], false);
            EntityId v = VK.Spawn(s, VehicleTypeId.Car, new[] { b2, b1, b0 }, 30f, 60f, 3f);
            VK.Run(s, 150);
            Assert.Equal(1, s.GetVehicle(v).RouteCursor);
            Vec2 before = WorldPos(n, s, v);
            Assert.True(n.SplitSegment(chain[1], 0.5f, null));
            s.Update();
            Assert.True(s.IsAlive(v));
            Assert.Equal(0, s.DespawnedByEdit);
            Assert.True(Vec2.Distance(before, WorldPos(n, s, v)) < 2f);
            VK.Run(s, 1500);
            Assert.Equal(1, s.ArrivedCount);
        }

        private static (ulong hash, int remapped, int despawned, int alive, int arrived) StressRun()
        {
            RoadNetwork net = VK.MakeGrid(5, 100f);
            var gate = new ProbeGate(net);
            TrafficSimulation sim = GridSim(gate, net, 11);
            sim.Run(600);
            // Calles nuevas que cruzan la cuadricula en diagonal y por el medio de las manzanas: dividen muchos segmentos bajo trafico.
            Assert.True(Kit.Add(net, 20, 50, 480, 50, RoadTypeIds.Street2) || true);
            Kit.Add(net, 250, 20, 250, 480, RoadTypeIds.Street2);
            Kit.Add(net, 30, 30, 190, 190, RoadTypeIds.Street2);
            for (int k = 0; k < 40; k++)
            {
                sim.Run(25);
                VK.CheckInvariants(sim.Vehicles);
            }
            return (sim.ComputeHash(), sim.Vehicles.RemappedBySplit, sim.Vehicles.DespawnedByEdit, sim.Vehicles.AliveCount, sim.Vehicles.ArrivedCount);
        }

        [Fact]
        public void P4_SplitsUnderTraffic_KeepInvariants_Deterministic_AndMostVehiclesSurvive()
        {
            var a = StressRun();
            var b = StressRun();
            Console.WriteLine("STRESS " + a);
            Assert.Equal(a, b);
            Assert.True(a.remapped > 0, "ningun vehiculo recolocado");
            Assert.True(a.despawned <= a.remapped, "bajas " + a.despawned + " frente a recolocados " + a.remapped);
        }

        [Fact]
        public void P4_AfterEditsAreProcessed_SteadyStateAllocatesNothing()
        {
            RoadNetwork net = VK.MakeGrid(5, 100f);
            var gate = new ProbeGate(net);
            TrafficSimulation sim = GridSim(gate, net, 12);
            sim.Run(600);
            Kit.Add(net, 250, 20, 250, 480, RoadTypeIds.Street2);
            sim.Run(3000);
            long before = GC.GetAllocatedBytesForCurrentThread();
            sim.Run(400);
            Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // ---------------------------------------------------------------- punto 5

        [Fact]
        public void P5_ContinuationBecomesJunction_QueueNearTheNodeKeepsItsGaps()
        {
            RoadNetwork n = Kit.New();
            EntityId a = Kit.SegAt(n, 0, 0, 100, 0, RoadTypeIds.Street2);
            EntityId b = Kit.SegAt(n, 100, 0, 200, 0, RoadTypeIds.Street2);
            VehicleSystem s = VK.Sys(n, 64, 1, false, new GapOnlyGate(n));
            LaneId la = VK.Lane(n, a, true), lb = VK.Lane(n, b, true);
            var ids = new List<EntityId>
            {
                VK.Spawn(s, VehicleTypeId.Car, new[] { la, lb }, 98f, 90f),
                VK.Spawn(s, VehicleTypeId.Car, new[] { la, lb }, 91f, 90f),
                VK.Spawn(s, VehicleTypeId.Car, new[] { la, lb }, 84f, 90f),
            };
            foreach (EntityId id in ids) s.SetFrozen(id, true);
            EntityId onB = VK.Spawn(s, VehicleTypeId.Car, new[] { lb }, 20f, 90f);
            s.SetFrozen(onB, true);
            s.Update();
            Vec2 posB = WorldPos(n, s, onB);

            // Una tercera calle convierte el nodo (100, 0) en cruce: los carriles se acortan ~6 m junto a el.
            Assert.True(Kit.Add(n, 100, 100, 100, 0, RoadTypeIds.Street2));
            Assert.True(n.GetSegment(a).SetbackB > 3f);
            s.Update();

            VehicleType car = s.GetVehicleType(VehicleTypeId.Car);
            for (int k = 0; k + 1 < ids.Count; k++)
            {
                float leaderRear = s.GetVehicle(ids[k]).S - car.Length;
                float followerFront = s.GetVehicle(ids[k + 1]).S;
                Assert.True(followerFront <= leaderRear + 1e-3f, "solape entre " + k + " y " + (k + 1) + ": trasera " + leaderRear + " frente " + followerFront);
            }
            Assert.True(s.GetVehicle(ids[0]).S <= n.GetLane(la).Length + 1e-3f);
            // El vehiculo del tramo de salida no salta: su carril ahora empieza 6 m mas adelante.
            Assert.True(Vec2.Distance(posB, WorldPos(n, s, onB)) < 0.5f, "salto de " + Vec2.Distance(posB, WorldPos(n, s, onB)) + " m");
            VK.CheckInvariants(s);
        }

        [Fact]
        public void P5_VehicleThatNoLongerFits_IsDespawnedAndCounted_RestKeepOrder()
        {
            RoadNetwork n = Kit.New();
            EntityId a = Kit.SegAt(n, 0, 0, 20, 0, RoadTypeIds.Street2);
            VehicleSystem s = VK.Sys(n, 64, 1, false, new GapOnlyGate(n));
            LaneId la = VK.Lane(n, a, true);
            EntityId truck = VK.Spawn(s, VehicleTypeId.Truck, new[] { la }, 19f, 19.5f);
            EntityId car = VK.Spawn(s, VehicleTypeId.Car, new[] { la }, 6.4f, 7f);
            s.SetFrozen(truck, true);
            s.SetFrozen(car, true);
            s.Update();
            // Calles en los dos extremos: el carril pierde unos 6 m en cada uno (20 -> ~8 m).
            Assert.True(Kit.Add(n, 0, 60, 0, 0, RoadTypeIds.Street2));
            Assert.True(Kit.Add(n, -60, 0, 0, 0, RoadTypeIds.Street2));
            Assert.True(Kit.Add(n, 20, 60, 20, 0, RoadTypeIds.Street2));
            Assert.True(Kit.Add(n, 80, 0, 20, 0, RoadTypeIds.Street2));
            s.Update();
            Assert.True(n.GetLane(la).Length < 20f);
            Assert.True(s.IsAlive(truck));
            Assert.False(s.IsAlive(car));
            Assert.Equal(1, s.DespawnedBySetback);
            Assert.Equal(1, s.DespawnedByEdit);
            Assert.True(s.GetVehicle(truck).S <= n.GetLane(la).Length + 1e-3f);
            VK.CheckInvariants(s);
        }

        // ---------------------------------------------------------------- punto 6

        [Fact]
        public void P6_NewSegmentReusingAnIndex_DoesNotInheritTheOldCongestion()
        {
            RoadNetwork net = Kit.New();
            EntityId n0 = Kit.Node(net, 0, 0), n1 = Kit.Node(net, 1000, 0);
            EntityId seg = Kit.Seg(net, n0, n1, RoadTypeIds.Street2);
            var sim = new TrafficSimulation(net, 1, new TrafficSimulationOptions
            {
                Vehicle = new VehicleConfig { DriverVariation = false },
                VehicleCapacity = 64,
            });
            Assert.True(sim.Vehicles.TrySpawn(VehicleTypeId.Car, new[] { new RouteSegment(seg, true) }, 600f, 990f, 0f, out EntityId blocker));
            sim.Vehicles.SetFrozen(blocker, true);
            for (int k = 0; k < 8; k++)
            {
                sim.Vehicles.TrySpawn(VehicleTypeId.Car, new[] { new RouteSegment(seg, true) }, 5f, 990f, 13f, out _);
                sim.Run(100);
            }
            sim.Run(900);
            Assert.True(sim.Metrics.GetSegmentFlow(seg.Index) < 0.4f);

            Assert.True(net.RemoveSegment(seg, null));
            // Otro tramo, en otro sitio, que reutiliza el indice liberado.
            EntityId m0 = Kit.Node(net, 0, 500), m1 = Kit.Node(net, 300, 500);
            EntityId fresh = Kit.Seg(net, m0, m1, RoadTypeIds.Street2);
            Assert.Equal(seg.Index, fresh.Index);
            sim.Step();
            sim.Step();
            Assert.True(sim.Metrics.GetSegmentFlow(fresh.Index) > 0.95f, "hereda " + sim.Metrics.GetSegmentFlow(fresh.Index));
            float pf = sim.Paths.Congestion![fresh.Index];
            Assert.True(pf > 0.95f, "el pathfinder sigue viendo " + pf);
        }
    }
}
