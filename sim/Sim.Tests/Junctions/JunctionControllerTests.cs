using System;
using System.Collections.Generic;
using Urbe.Sim.Ids;
using Urbe.Sim.Junctions;
using Urbe.Sim.Random;
using Urbe.Sim.Roads;
using Urbe.Sim.Tests.Roads;
using Urbe.Sim.Tests.Vehicles;
using Urbe.Sim.Vehicles;
using Xunit;

namespace Urbe.Sim.Tests.Junctions
{
    /// <summary>Vista de vehiculos falsa: hueco por carril y trasera por (vehiculo, conexion).</summary>
    internal sealed class FakeView : IVehicleView
    {
        public readonly Dictionary<int, float> Free = new Dictionary<int, float>();
        public readonly Dictionary<(int, int), float> Rear = new Dictionary<(int, int), float>();
        public float DefaultFree = 1000f;

        public float FreeSpace(LaneId lane) => Free.TryGetValue(lane.Value, out float f) ? f : DefaultFree;

        public float RearOnConnection(int vehicle, int connection) =>
            Rear.TryGetValue((vehicle, connection), out float r) ? r : float.PositiveInfinity;
    }

    /// <summary>Tests 5-10 del ADR 004 sobre el controlador (con vista falsa o con vehiculos reales) y ganchos de M1-11.</summary>
    public class JunctionControllerTests
    {
        private readonly Xunit.Abstractions.ITestOutputHelper _out;

        public JunctionControllerTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        private static (JunctionController, FakeView) Ctrl(RoadNetwork n, JunctionConfig? cfg = null)
        {
            var jc = new JunctionController(n, cfg);
            var view = new FakeView();
            jc.Bind(view, 256);
            return (jc, view);
        }

        private static EntryRequest Req(RoadNetwork n, int vehicle, int conn, float dist = 10f, float speed = 5f, float space = 6.5f, ushort stopped = 0)
        {
            ref readonly LaneConnection c = ref n.GetConnection(conn);
            return new EntryRequest
            {
                Vehicle = vehicle,
                From = c.From,
                Connection = conn,
                To = c.To,
                DistanceToStop = dist,
                Speed = speed,
                Space = space,
                StoppedTicks = stopped,
            };
        }

        private static List<int> Resolve(JunctionController jc, ulong tick, params EntryRequest[] reqs)
        {
            jc.BeginTick(tick);
            foreach (EntryRequest r in reqs) jc.RequestEntry(in r);
            var list = new List<int>();
            foreach (EntryGrant g in jc.ResolveRequests()) list.Add(g.Vehicle);
            return list;
        }

        private static int Local(RoadNetwork n, int conn) => n.GetConnection(conn).LocalIndex;

        // 5. Rangos: el recto principal gana a la izquierda opuesta; la izquierda pasa cuando el recto rebasa su zona.
        [Fact]
        public void Rank_MajorStraightBeatsOpposingLeft_LeftGoesAfterZone()
        {
            Crossing x = JK.X(RoadTypeIds.Street2, RoadTypeIds.Street2);
            RoadNetwork n = x.Net;
            (JunctionController jc, FakeView view) = Ctrl(n);
            JunctionNode jn = jc.GetJunction(x.Center)!;
            int sE = x.ConnTurn(0, TurnType.Straight), lW = x.ConnTurn(2, TurnType.Left);
            Assert.True(jn.IsMajorApproach(jn.Gates[Local(n, sE)].Approach));
            Assert.True((jn.Gates[Local(n, lW)].ConflictMask & (1UL << Local(n, sE))) != 0);

            // Misma distancia y velocidad, en el mismo tick: solo el recto (vehiculo de indice mayor para que no gane por indice).
            List<int> g = Resolve(jc, 1, Req(n, 7, lW), Req(n, 9, sE));
            Assert.Equal(new[] { 9 }, g);
            Assert.Equal(GrantDecision.DeniedClaimed, jc.GetLastDecision(7));

            // El recto entra: su trasera aun antes del fin de la zona -> la izquierda sigue esperando.
            jc.OnEntered(sE, 9);
            float otherEnd = JK.Find(jn, Local(n, lW), Local(n, sE)).OtherEndDm * 0.1f;
            view.Rear[(9, sE)] = otherEnd - 0.5f;
            Assert.Empty(Resolve(jc, 2, Req(n, 7, lW)));
            // La trasera rebasa la zona: pasa aunque el recto aun ocupe su conexion.
            view.Rear[(9, sE)] = otherEnd + 0.01f;
            Assert.Equal(new[] { 7 }, Resolve(jc, 3, Req(n, 7, lW)));
            Assert.Equal(1, jc.GetGate(sE).Occupants);
        }

        // 6. Orden canonico: las mismas peticiones en 10 permutaciones dan las mismas concesiones.
        [Fact]
        public void CanonicalOrder_PermutationsGiveIdenticalGrants()
        {
            RoadNetwork n = VK.MakeGrid(3, 80f);
            var rng = new Pcg32(99, 3);
            var conns = new List<int>();
            for (int i = 0; i < n.NodeIndexBound; i++)
            {
                EntityId id = n.NodeIdAt(i);
                if (!id.IsValid || n.GetNode(id).SegmentCount < 3) continue;
                int first = n.GetNode(id).ConnFirst;
                for (int k = 0; k < n.GetNode(id).ConnCount; k++) conns.Add(first + k);
            }
            var tickReqs = new List<EntryRequest[]>();
            for (int t = 0; t < 4; t++)
            {
                var reqs = new EntryRequest[50];
                for (int v = 0; v < 50; v++)
                {
                    int c = conns[rng.NextInt(conns.Count)];
                    reqs[v] = Req(n, v + 3 * t, c, 1f + 20f * rng.NextFloat(), 8f * rng.NextFloat(), rng.NextInt(4) == 0 ? 13f : 6.5f, (ushort)rng.NextInt(30));
                }
                tickReqs.Add(reqs);
            }
            var free = new Dictionary<int, float>();
            foreach (int c in conns) free[n.GetConnection(c).To.Value] = 5f + 30f * rng.NextFloat();

            List<(int, int)>? reference = null;
            for (int p = 0; p < 10; p++)
            {
                var jc = new JunctionController(n);
                var view = new FakeView();
                foreach (var kv in free) view.Free[kv.Key] = kv.Value;
                jc.Bind(view, 512);
                var all = new List<(int, int)>();
                var prng = new Pcg32((ulong)p + 1, 11);
                for (int t = 0; t < tickReqs.Count; t++)
                {
                    EntryRequest[] reqs = (EntryRequest[])tickReqs[t].Clone();
                    for (int i = reqs.Length - 1; i > 0; i--)
                    {
                        int j = prng.NextInt(i + 1);
                        (reqs[i], reqs[j]) = (reqs[j], reqs[i]);
                    }
                    jc.BeginTick((ulong)(t + 1));
                    foreach (EntryRequest r in reqs) jc.RequestEntry(in r);
                    foreach (EntryGrant g in jc.ResolveRequests()) all.Add((g.Vehicle, g.Connection));
                }
                if (reference == null) reference = all;
                else Assert.Equal(reference, all);
            }
            Assert.True(reference!.Count > 10 && reference.Count < 200, "concesiones = " + reference.Count);
        }

        // 7. Anti-bloqueo.
        [Fact]
        public void ExitFull_DeniesWithoutRoom_AndShortExitNeedsEmptyLane()
        {
            Crossing x = JK.X(RoadTypeIds.Street2, RoadTypeIds.Street2);
            RoadNetwork n = x.Net;
            (JunctionController jc, FakeView view) = Ctrl(n);
            int sW = x.ConnTurn(2, TurnType.Straight);
            LaneId exitE = n.GetConnection(sW).To;
            view.Free[exitE.Value] = 6.0f;
            Assert.Empty(Resolve(jc, 1, Req(n, 1, sW, space: 6.5f)));
            Assert.Equal(GrantDecision.DeniedExitFull, jc.GetLastDecision(1));
            view.Free[exitE.Value] = 6.5f;
            Assert.Equal(new[] { 1 }, Resolve(jc, 2, Req(n, 1, sW, space: 6.5f)));
            jc.OnVehicleRemoved(1);

            // Dos peticiones a la misma salida (recto desde O y derecha desde N... misma salida E) con hueco para una: la de mas prioridad.
            int rN = -1;
            foreach (LaneConnection c in n.GetConnectionsFrom(x.Entry(1)).ToArray())
                if (c.To == exitE) rN = x.Conn(c.From, c.To);
            Assert.True(rN >= 0);
            view.Free[exitE.Value] = 8f;
            List<int> g = Resolve(jc, 3, Req(n, 4, rN, space: 6.5f), Req(n, 5, sW, space: 6.5f));
            Assert.Single(g);
            int winner = jc.GetJunction(x.Center)!.Gates[Local(n, rN)].Rank > jc.GetJunction(x.Center)!.Gates[Local(n, sW)].Rank ? 4 : 5;
            Assert.Equal(winner, g[0]);

            // Salida mas corta que el camion: solo con el carril vacio y sin reservas.
            Crossing s = JK.Make(new ushort?[] { RoadTypeIds.Street2, RoadTypeIds.Street2, RoadTypeIds.Street2, null }, 150f);
            RoadNetwork ns = s.Net;
            EntityId shortNode = ns.AddNodeRaw(Kit.V(0, -9f), 0f);
            EntityId shortArm = ns.AddSegmentRaw(s.Center, shortNode, Urbe.Sim.Geometry.Bezier3.FromLine(Kit.V(0, 0), Kit.V(0, -9f)), RoadTypeIds.Street2);
            Assert.True(shortArm.IsValid);
            (JunctionController js, FakeView vs) = Ctrl(ns);
            LaneId shortExit = new LaneId(shortArm.Index, 0); // sale del centro (Forward desde el centro)
            int toShort = -1;
            foreach (LaneConnection c in ns.GetConnectionsFrom(s.Entry(2)).ToArray())
                if (c.To == shortExit) toShort = s.Conn(c.From, c.To);
            Assert.True(toShort >= 0);
            float laneLen = ns.GetLane(shortExit).Length;
            Assert.True(laneLen < 13f);
            vs.Free[shortExit.Value] = laneLen - 0.5f;
            Assert.Empty(Resolve(js, 1, Req(ns, 1, toShort, space: 13f)));
            vs.Free[shortExit.Value] = laneLen;
            Assert.Equal(new[] { 1 }, Resolve(js, 2, Req(ns, 1, toShort, space: 13f)));
        }

        // 8. Liberacion parcial con zona interior (avenidas, giros a la derecha que se cruzan).
        [Fact]
        public void PartialRelease_GrantsOnceOccupantPassedZone()
        {
            Crossing x = JK.X(RoadTypeIds.Avenue4, RoadTypeIds.Avenue4);
            RoadNetwork n = x.Net;
            Assert.True(n.SetNodeControl(x.Center, JunctionControlIds.Priority));
            (JunctionController jc, FakeView view) = Ctrl(n);
            JunctionNode jn = jc.GetJunction(x.Center)!;
            // Busca un par (c, j) con zona de j que acaba antes del final de j.
            int c = -1, j = -1;
            float otherEnd = 0f;
            int first = n.GetNode(x.Center).ConnFirst;
            for (int i = 0; i < jn.ConnCount && c < 0; i++)
            {
                foreach (ConflictEntry e in jn.ConflictsOf(i))
                {
                    float lj = n.GetConnection(first + e.Other).Length;
                    if (e.Kind == ConflictKind.Cross && e.OtherEndDm * 0.1f < lj - 1f && e.OtherEndDm > 5)
                    {
                        c = first + i;
                        j = first + e.Other;
                        otherEnd = e.OtherEndDm * 0.1f;
                        break;
                    }
                }
            }
            Assert.True(c >= 0, "no hay par con zona interior");
            jc.BeginTick(1);
            jc.OnEntered(j, 50);
            view.Rear[(50, j)] = otherEnd - 0.2f;
            Assert.Empty(Resolve(jc, 2, Req(n, 1, c)));
            Assert.Equal(GrantDecision.DeniedConflict, jc.GetLastDecision(1));
            view.Rear[(50, j)] = otherEnd + 0.05f;
            Assert.Equal(new[] { 1 }, Resolve(jc, 3, Req(n, 1, c)));
            Assert.Equal(1, jc.GetGate(j).Occupants);
            // Con un permiso pendiente en j se bloquea siempre.
            jc.OnVehicleRemoved(1);
            Assert.Equal(new[] { 2 }, Resolve(jc, 4, Req(n, 2, j)));
            Assert.Empty(Resolve(jc, 5, Req(n, 1, c)));
        }

        // 9a. Stop: condiciones de la concesion.
        [Fact]
        public void Stop_RequiresStoppedNearLine()
        {
            Crossing t = JK.T(RoadTypeIds.Street2, RoadTypeIds.Avenue4);
            RoadNetwork n = t.Net;
            Assert.True(n.SetNodeControl(t.Center, JunctionControlIds.Stop));
            (JunctionController jc, _) = Ctrl(n);
            int lS = t.ConnTurn(3, TurnType.Left);
            Assert.Empty(Resolve(jc, 1, Req(n, 1, lS, dist: 3f, speed: 1f, stopped: 50)));
            Assert.Equal(GrantDecision.DeniedStop, jc.GetLastDecision(1));
            Assert.Empty(Resolve(jc, 2, Req(n, 1, lS, dist: 3f, speed: 0.2f, stopped: 9)));
            Assert.Empty(Resolve(jc, 3, Req(n, 1, lS, dist: 6f, speed: 0f, stopped: 50)));
            Assert.Equal(new[] { 1 }, Resolve(jc, 4, Req(n, 1, lS, dist: 4f, speed: 0.2f, stopped: 10)));
            // El principal no para.
            Assert.Equal(new[] { 2 }, Resolve(jc, 5, Req(n, 2, t.ConnTurn(0, TurnType.Straight), dist: 20f, speed: 13f)));
        }

        // 9b. Stop con vehiculos reales: cada concesion del tallo llega con el vehiculo parado en la linea.
        [Fact]
        public void Stop_RealVehicles_StopBeforeEveryGrant()
        {
            Crossing t = JK.T(RoadTypeIds.Street2, RoadTypeIds.Avenue4);
            RoadNetwork n = t.Net;
            Assert.True(n.SetNodeControl(t.Center, JunctionControlIds.Stop));
            var jc = new JunctionController(n);
            var trace = new TracingGate(jc, n, t.Center) { Record = true };
            VehicleSystem s = JK.Sys(n, trace, 64, 3);
            int granted = 0;
            foreach (TurnType turn in new[] { TurnType.Left, TurnType.Right })
            {
                int conn = t.ConnTurn(3, turn);
                LaneId exit = n.GetConnection(conn).To;
                EntityId id = VK.Spawn(s, VehicleTypeId.Car, new[] { t.Entry(3), exit }, 60f, 100f, 11f);
                for (int k = 0; k < 400 && s.IsAlive(id); k++)
                {
                    s.Update();
                    foreach (EntryGrant g in trace.LastGrants)
                    {
                        if (g.Vehicle != id.Index) continue;
                        EntryRequest r = trace.LastRequests.Find(q => q.Vehicle == g.Vehicle);
                        Assert.True(r.Speed <= 0.3f && r.StoppedTicks >= 10 && r.DistanceToStop <= 5f,
                            "concedido con v=" + r.Speed + " parado=" + r.StoppedTicks + " d=" + r.DistanceToStop);
                        granted++;
                    }
                }
                Assert.False(s.IsAlive(id), "el vehiculo del tallo no llego");
            }
            Assert.Equal(2, granted);
        }

        // 10. Envejecimiento: izquierda del secundario con flujo principal de 0,25 veh/s por sentido.
        [Theory]
        [InlineData(7, true)]
        [InlineData(0, false)]
        public void Aging_LetsSecondaryLeftThrough(int agingMax, bool expectEnter)
        {
            Crossing t = JK.T(RoadTypeIds.Street2, RoadTypeIds.Street2);
            RoadNetwork n = t.Net;
            var cfg = new JunctionConfig { AgingMax = agingMax };
            var jc = new JunctionController(n, cfg);
            Assert.Equal(ControlKind.Priority, jc.GetControl(t.Center));
            JunctionNode jn = jc.GetJunction(t.Center)!;
            int lS = t.ConnTurn(3, TurnType.Left);
            int sE = t.ConnTurn(0, TurnType.Straight), sW = t.ConnTurn(2, TurnType.Straight);
            Assert.True((jn.Gates[Local(n, lS)].ConflictMask & (1UL << Local(n, sE))) != 0);
            Assert.True((jn.Gates[Local(n, lS)].ConflictMask & (1UL << Local(n, sW))) != 0);
            Assert.False(jn.IsMajorApproach(jn.Gates[Local(n, lS)].Approach));

            VehicleSystem s = JK.Sys(n, jc, 256, 5);
            LaneId exE = n.GetConnection(sE).To, exW = n.GetConnection(sW).To;
            void Major(int tick)
            {
                if (tick % 40 == 0) s.TrySpawn(VehicleTypeId.Car, new[] { t.Entry(0), exE }, 0f, 140f, 11f, out _);
                if (tick % 40 == 20) s.TrySpawn(VehicleTypeId.Car, new[] { t.Entry(2), exW }, 0f, 140f, 11f, out _);
            }
            int tk = 0;
            for (; tk < 600; tk++) { Major(tk); s.Update(); }
            EntityId left = VK.Spawn(s, VehicleTypeId.Car, new[] { t.Entry(3), n.GetConnection(lS).To }, 110f, 100f, 8f);
            int firstReq = -1, entered = -1;
            for (int k = 0; k < 1300; k++, tk++)
            {
                Major(tk);
                s.Update();
                if (firstReq < 0 && jc.GetWaitTicks(left.Index) > 0) firstReq = k - 1;
                ref readonly Vehicle v = ref s.GetVehicle(left);
                if (s.IsAlive(left) && (v.Kind == TrackKind.Connection || v.PrevConnection >= 0)) { entered = k; break; }
                if (!s.IsAlive(left)) { entered = k; break; }
            }
            Assert.True(firstReq >= 0, "no llego a pedir");
            _out.WriteLine("AgingMax={0}: primera peticion {1}, entrada {2} (espera {3} ticks)", agingMax, firstReq, entered, entered < 0 ? -1 : entered - firstReq);
            if (expectEnter)
            {
                Assert.True(entered >= 0 && entered - firstReq <= 400, "entro tras " + (entered - firstReq) + " ticks");
            }
            else
            {
                Assert.True(entered < 0 || entered - firstReq > 1200, "sin envejecimiento entro tras " + (entered - firstReq) + " ticks");
            }
        }

        // 11b. Semaforo: concesiones solo en verde, o en ambar si no puede parar o es la izquierda que despeja.
        [Fact]
        public void Signal_GrantsOnlyOnGreenOrAllowedAmber()
        {
            Crossing x = JK.X(RoadTypeIds.Avenue4, RoadTypeIds.Avenue4);
            RoadNetwork n = x.Net;
            (JunctionController jc, _) = Ctrl(n);
            JunctionNode jn = jc.GetJunction(x.Center)!;
            Assert.True(jn.IsSignal);
            int[] straights = { x.ConnTurn(0, TurnType.Straight), x.ConnTurn(1, TurnType.Straight) };
            int left = x.ConnTurn(0, TurnType.Left);
            int greens = 0, ambers = 0;
            for (ulong tick = 1; tick <= (ulong)jn.CycleTicks * 2; tick++)
            {
                foreach (int c in straights)
                {
                    List<int> g = Resolve(jc, tick, Req(n, 1, c, dist: 3f, speed: 0f));
                    SignalAspect a = jc.GetSignalAspect(x.Center, c);
                    Assert.Equal(a == SignalAspect.Green, g.Count == 1);
                    if (g.Count == 1) greens++;
                    jc.OnVehicleRemoved(1);
                    // Quien no puede parar pasa en ambar.
                    g = Resolve(jc, tick, Req(n, 2, c, dist: 1f, speed: 3f));
                    Assert.Equal(a != SignalAspect.Red, g.Count == 1);
                    jc.OnVehicleRemoved(2);
                }
                List<int> gl = Resolve(jc, tick, Req(n, 3, left, dist: 3f, speed: 0f, stopped: 25));
                SignalAspect al = jc.GetSignalAspect(x.Center, left);
                Assert.Equal(al != SignalAspect.Red, gl.Count == 1);
                if (al == SignalAspect.Amber && gl.Count == 1) ambers++;
                jc.OnVehicleRemoved(3);
                // En verde lejos y lento al final del verde: no llega antes del rojo.
            }
            Assert.True(greens > 0 && ambers == 2 * 30, "verdes=" + greens + " ambares=" + ambers);

            // Verde con ETA: lejos y lento con poco verde restante -> se deniega.
            jc.BeginTick(240);
            Assert.Equal(SignalStage.Green, jc.GetState(x.Center).Stage);
            Assert.Empty(Resolve(jc, 240, Req(n, 4, straights[0], dist: 40f, speed: 2f)));
            Assert.Equal(GrantDecision.DeniedSignal, jc.GetLastDecision(4));
            Assert.Equal(new[] { 4 }, Resolve(jc, 10, Req(n, 4, straights[0], dist: 40f, speed: 2f)));
        }

        // Gancho M1-11: ForceNextGrant se salta stop y hueco, nunca el conflicto; esperas.
        [Fact]
        public void ForceNextGrant_SkipsStopAndExit_NeverConflict()
        {
            Crossing t = JK.T(RoadTypeIds.Street2, RoadTypeIds.Avenue4);
            RoadNetwork n = t.Net;
            Assert.True(n.SetNodeControl(t.Center, JunctionControlIds.Stop));
            (JunctionController jc, FakeView view) = Ctrl(n);
            int lS = t.ConnTurn(3, TurnType.Left);
            view.Free[n.GetConnection(lS).To.Value] = 0f;
            Assert.Empty(Resolve(jc, 1, Req(n, 1, lS, speed: 5f)));
            Assert.Empty(Resolve(jc, 2, Req(n, 1, lS, speed: 5f)));
            Assert.Equal((ushort)1, jc.GetWaitTicks(1));
            Assert.Equal((ushort)1, jc.GetOldestWaitTicks(t.Center));
            jc.ForceNextGrant(1);
            Assert.Equal(new[] { 1 }, Resolve(jc, 3, Req(n, 1, lS, speed: 5f)));
            jc.OnEntered(lS, 1);
            Assert.Equal((ushort)0, jc.GetWaitTicks(1));

            // Con un ocupante en conflicto, el forzado espera.
            JunctionNode jn = jc.GetJunction(t.Center)!;
            int sE = n.GetNode(t.Center).ConnFirst + jn.ConflictsOf(Local(n, lS))[0].Other;
            jc.ForceNextGrant(2);
            view.Free[n.GetConnection(sE).To.Value] = 1000f;
            view.Rear[(1, lS)] = -1f;
            Assert.True((jn.Gates[Local(n, sE)].ConflictMask & (1UL << Local(n, lS))) != 0);
            Assert.Empty(Resolve(jc, 4, Req(n, 2, sE)));
            Assert.Equal(GrantDecision.DeniedConflict, jc.GetLastDecision(2));
            jc.OnExited(lS, 1);
            Assert.Equal(new[] { 2 }, Resolve(jc, 5, Req(n, 2, sE)));
        }

        // Permiso cancelado por re-ruta: la puerta lo olvida (cambio en Vehicles/ de M1-07).
        [Fact]
        public void PermitCancelled_OnRouteChange_ReleasesGate()
        {
            Crossing t = JK.T(RoadTypeIds.Street2, RoadTypeIds.Street2);
            RoadNetwork n = t.Net;
            var jc = new JunctionController(n);
            var rec = new RecordingGateAdapter(jc);
            VehicleSystem s = JK.Sys(n, rec, 16, 1);
            int lS = t.ConnTurn(3, TurnType.Left), rS = t.ConnTurn(3, TurnType.Right);
            EntityId id = VK.Spawn(s, VehicleTypeId.Car, new[] { t.Entry(3), n.GetConnection(lS).To }, 130f, 100f, 5f);
            for (int k = 0; k < 20 && (s.GetVehicle(id).Flags & VehicleFlags.HasEntryPermit) == 0; k++) s.Update();
            Assert.True((s.GetVehicle(id).Flags & VehicleFlags.HasEntryPermit) != 0);
            Assert.Equal(1, jc.GetGate(lS).Permits);
            Assert.True(s.TryReplaceRoute(id, new[] { t.Entry(3), n.GetConnection(rS).To }, 100f));
            Assert.Equal(1, rec.Cancelled);
            Assert.Equal(0, jc.GetGate(lS).Permits);
            Assert.Equal(0UL, jc.GetJunction(t.Center)!.PermitMask);
        }

        private sealed class RecordingGateAdapter : IJunctionGate, IVehicleViewConsumer
        {
            private readonly JunctionController _inner;
            public int Cancelled;
            public RecordingGateAdapter(JunctionController inner) { _inner = inner; }
            public void Bind(IVehicleView view, int vehicleCapacity) => _inner.Bind(view, vehicleCapacity);
            public void OnNetworkChanged(ReadOnlySpan<EntityId> changedNodes) => _inner.OnNetworkChanged(changedNodes);
            public void BeginTick(ulong tick) => _inner.BeginTick(tick);
            public void RequestEntry(in EntryRequest req) => _inner.RequestEntry(in req);
            public ReadOnlySpan<EntryGrant> ResolveRequests() => _inner.ResolveRequests();
            public void OnEntered(int connection, int vehicle) => _inner.OnEntered(connection, vehicle);
            public void OnExited(int connection, int vehicle) => _inner.OnExited(connection, vehicle);
            public void OnVehicleRemoved(int vehicle) => _inner.OnVehicleRemoved(vehicle);
            public void OnPermitCancelled(int vehicle) { Cancelled++; _inner.OnPermitCancelled(vehicle); }
        }
    }
}
