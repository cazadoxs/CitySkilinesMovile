using System;
using System.Collections.Generic;
using Urbe.Sim.Ids;
using Urbe.Sim.Random;
using Urbe.Sim.Roads;
using Urbe.Sim.Tests.Roads;
using Urbe.Sim.Vehicles;
using Xunit;

namespace Urbe.Sim.Tests.Vehicles
{
    public class VehicleMovementTests
    {
        // 1. Velocidad libre.
        [Fact]
        public void FreeSpeed_ConvergesToLimit_AndNeverExceedsIt()
        {
            RoadNetwork n = Kit.New();
            EntityId[] chain = VK.Chain(n, 5, 200f, RoadTypeIds.Avenue4);
            VehicleSystem s = VK.Sys(n);
            var lanes = new LaneId[chain.Length];
            for (int i = 0; i < lanes.Length; i++) lanes[i] = VK.Lane(n, chain[i], true);
            EntityId id = VK.Spawn(s, VehicleTypeId.Car, lanes, 0f, 195f);

            float limit = n.GetLane(lanes[0]).SpeedLimit;
            Assert.Equal(50f / 3.6f, limit, 3);
            float maxV = 0f;
            for (int t = 0; t < 600; t++)
            {
                s.Update();
                maxV = Math.Max(maxV, s.GetVehicle(id).V);
            }
            Assert.True(s.IsAlive(id), "no deberia haber llegado aun");
            Assert.True(Math.Abs(s.GetVehicle(id).V - limit) < 0.01f * limit, "V = " + s.GetVehicle(id).V);
            Assert.True(maxV <= limit * 1.001f, "maxV = " + maxV);
        }

        // 2. Frenada tras vehiculo parado.
        [Fact]
        public void Braking_StopsAtS0_BehindFrozenVehicle_WithoutOverlap()
        {
            RoadNetwork n = Kit.New();
            EntityId seg = Kit.SegAt(n, 0, 0, 400, 0, RoadTypeIds.Avenue4);
            VehicleSystem s = VK.Sys(n);
            LaneId lane = VK.Lane(n, seg, true);
            EntityId blocker = VK.Spawn(s, VehicleTypeId.Car, new[] { lane }, 300f, 400f);
            s.SetFrozen(blocker, true);
            EntityId f = VK.Spawn(s, VehicleTypeId.Car, new[] { lane }, 100f, 400f, 13f);

            float minA = 0f;
            for (int t = 0; t < 900; t++)
            {
                s.Update();
                ref readonly Vehicle v = ref s.GetVehicle(f);
                minA = Math.Min(minA, v.A);
                float gap = s.GetVehicle(blocker).S - 4.5f - v.S;
                Assert.True(gap >= 0.5f - 1e-4f, "solape/hueco " + gap + " en tick " + t);
            }
            float finalGap = s.GetVehicle(blocker).S - 4.5f - s.GetVehicle(f).S;
            Assert.True(finalGap >= 1.5f && finalGap <= 2.5f, "hueco final " + finalGap);
            Assert.True(s.GetVehicle(f).V < 0.05f);
            Assert.True(minA >= -9.0001f, "decel " + minA);
        }

        [Fact]
        public void Braking_VeryCloseStart_StaysSafe()
        {
            // Aparece rapido y muy cerca de un parado: el clamp evita atravesarlo aunque la deceleracion sea brusca.
            RoadNetwork n = Kit.New();
            EntityId seg = Kit.SegAt(n, 0, 0, 300, 0, RoadTypeIds.Avenue4);
            VehicleSystem s = VK.Sys(n);
            LaneId lane = VK.Lane(n, seg, true);
            EntityId blocker = VK.Spawn(s, VehicleTypeId.Car, new[] { lane }, 100f, 300f);
            s.SetFrozen(blocker, true);
            EntityId f = VK.Spawn(s, VehicleTypeId.Truck, new[] { lane }, 80f, 300f, 20f);
            for (int t = 0; t < 300; t++)
            {
                s.Update();
                float gap = s.GetVehicle(blocker).S - 4.5f - s.GetVehicle(f).S;
                Assert.True(gap >= -1e-3f, "atraveso al parado: " + gap);
            }
        }

        // 3-5. Anillo: sin solapes, sin NaN, invariantes de listas.
        [Fact]
        public void Ring_50Vehicles_3000Ticks_NoOverlap_ListsConsistent()
        {
            RoadNetwork n = Kit.New();
            EntityId[] ring = VK.MakeRing(n);
            VehicleSystem s = VK.Sys(n, 256, 7, variation: true);

            // Orden del anillo por carril: pista -> (anillo, inicio acumulado).
            var startOf = new Dictionary<long, (int ring, float start)>();
            var ringLen = new float[2];
            for (int slot = 0; slot < 2; slot++)
            {
                float cum = 0f;
                for (int k = 0; k < ring.Length; k++)
                {
                    LaneId l = new LaneId(ring[k].Index, slot), nx = new LaneId(ring[(k + 1) % ring.Length].Index, slot);
                    startOf[Key(TrackKind.Lane, l.Value)] = (slot, cum);
                    cum += n.GetLane(l).Length;
                    int c = VK.ConnIndex(n, l, nx);
                    Assert.True(c >= 0);
                    startOf[Key(TrackKind.Connection, c)] = (slot, cum);
                    cum += n.GetConnection(c).Length;
                }
                ringLen[slot] = cum;
            }

            var pos = new List<(int ring, float pos, float len)>();
            float MinRingGap()
            {
                pos.Clear();
                for (int i = 0; i < s.HighWater; i++)
                {
                    ref readonly Vehicle v = ref s.GetVehicle(i);
                    if (!v.IsAlive) continue;
                    var (r, st) = startOf[Key(v.Kind, v.Track)];
                    pos.Add((r, st + v.S, s.GetVehicleType(v.Type).Length));
                }
                float min = float.MaxValue;
                for (int b = 0; b < pos.Count; b++)
                {
                    float best = float.MaxValue, bestLen = 0f;
                    for (int a = 0; a < pos.Count; a++)
                    {
                        if (a == b || pos[a].ring != pos[b].ring) continue;
                        float d = (pos[a].pos - pos[b].pos) % ringLen[pos[b].ring];
                        if (d < 0f) d += ringLen[pos[b].ring];
                        if (d < best) { best = d; bestLen = pos[a].len; }
                    }
                    if (best != float.MaxValue) min = Math.Min(min, best - bestLen);
                }
                return min;
            }

            var rng = new Pcg32(99, 3);
            int spawned = 0;
            for (int attempt = 0; attempt < 5000 && spawned < 50; attempt++)
            {
                int slot = rng.NextInt(2);
                int k = rng.NextInt(ring.Length);
                var lanes = new LaneId[96];
                for (int j = 0; j < lanes.Length; j++) lanes[j] = new LaneId(ring[(k + j) % ring.Length].Index, slot);
                float segLen = n.GetLane(lanes[0]).Length;
                float sPos = 4.5f + rng.NextFloat() * (segLen - 4.5f);
                byte type = rng.NextInt(5) == 0 ? VehicleTypeId.Truck : VehicleTypeId.Car;
                if (!s.TrySpawn(type, lanes, sPos, segLen * 0.5f, 0f, out EntityId nid)) continue;
                // Un vehiculo recien creado no debe quedar pegado a otro de una pista anterior (el alta solo mira su pista y la siguiente).
                if (MinRingGap() < 2f) { s.Despawn(nid); continue; }
                spawned++;
            }
            Assert.Equal(50, spawned);

            float minSeen = float.MaxValue, minA = 0f;
            int slowTicks = 0;
            for (int t = 0; t < 3000; t++)
            {
                s.Update();
                VK.CheckInvariants(s);
                float gap = MinRingGap();
                minSeen = Math.Min(minSeen, gap);
                Assert.True(gap >= 0.5f - 1e-3f, "hueco " + gap + " en tick " + t);
                for (int i = 0; i < s.HighWater; i++)
                {
                    minA = Math.Min(minA, s.GetVehicle(i).A);
                    if (s.GetVehicle(i).IsAlive && s.GetVehicle(i).V < 3f) slowTicks++;
                }
            }
            Assert.True(minA >= -9.0001f, "decel " + minA);
            Assert.True(slowTicks > 1000, "el anillo no llego a congestionarse: el test seria trivial");
            Assert.True(minSeen < 6f);
            Assert.Equal(50, s.AliveCount); // 3.000 ticks no bastan para completar 96 pasos
        }

        private static long Key(TrackKind k, int track) => ((long)(int)k << 32) | (uint)track;

        // Calle larga con 200 vehiculos entrando y variacion de conductor: sin solapes.
        [Fact]
        public void LongStreet_200Vehicles_NoOverlap()
        {
            RoadNetwork n = Kit.New();
            EntityId[] chain = VK.Chain(n, 10, 200f, RoadTypeIds.Avenue4);
            VehicleSystem s = VK.Sys(n, 512, 5, variation: true);
            var lanes = new LaneId[chain.Length];
            for (int i = 0; i < lanes.Length; i++) lanes[i] = VK.Lane(n, chain[i], true);
            var rng = new Pcg32(5, 5);
            int spawned = 0;
            var order = new List<(float pos, float len)>();
            int maxAlive = 0;
            for (int t = 0; t < 9000; t++)
            {
                if (spawned < 200)
                {
                    byte type = rng.NextInt(4) == 0 ? VehicleTypeId.Truck : VehicleTypeId.Car;
                    if (s.TrySpawn(type, lanes, 0f, 190f, 9f, out _)) spawned++;
                }
                s.Update();
                if ((t & 15) == 0) VK.CheckInvariants(s);
                order.Clear();
                for (int i = 0; i < s.HighWater; i++)
                {
                    ref readonly Vehicle v = ref s.GetVehicle(i);
                    if (!v.IsAlive) continue;
                    float global;
                    if (v.Kind == TrackKind.Lane)
                    {
                        global = Array.IndexOf(chain, n.GetLane(new LaneId(v.Track)).Segment) * 200f + v.S;
                    }
                    else
                    {
                        var c = n.GetConnection(v.Track);
                        global = Array.IndexOf(chain, n.GetLane(c.From).Segment) * 200f + 200f + v.S;
                    }
                    order.Add((global, s.GetVehicleType(v.Type).Length));
                }
                maxAlive = Math.Max(maxAlive, order.Count);
                order.Sort((a, b) => b.pos.CompareTo(a.pos));
                for (int i = 1; i < order.Count; i++)
                {
                    float gap = order[i - 1].pos - order[i - 1].len - order[i].pos;
                    Assert.True(gap >= 0.5f - 1e-3f, "solape " + gap + " en tick " + t);
                }
            }
            Assert.Equal(200, spawned);
            Assert.True(maxAlive > 30);
        }

        // 7 y 8. Lookahead y puerta.
        [Fact]
        public void Lookahead_StoppedVehicleAtStartOfExitLane_FollowerStopsBeforeEntering()
        {
            VK.TJunction tj = VK.MakeT();
            RoadNetwork n = tj.Net;
            VehicleSystem s = VK.Sys(n);
            LaneId stem = VK.Lane(n, tj.Stem, true);
            LaneId east = VK.Lane(n, tj.East, true);
            EntityId blocker = VK.Spawn(s, VehicleTypeId.Car, new[] { east }, 5f, 50f);
            s.SetFrozen(blocker, true);
            EntityId f = VK.Spawn(s, VehicleTypeId.Car, new[] { stem, east }, 0f, 50f);
            bool entered = false;
            for (int t = 0; t < 600; t++)
            {
                s.Update();
                if (s.GetVehicle(f).Kind == TrackKind.Connection) entered = true;
            }
            Assert.False(entered);
            ref readonly Vehicle v = ref s.GetVehicle(f);
            float len = n.GetLane(stem).Length;
            Assert.True(v.S <= len - 1f + 0.01f, "S = " + v.S);
            Assert.True(v.V < 0.05f);
        }

        [Fact]
        public void Lookahead_StoppedVehicleFarInExitLane_FollowerEntersAndStopsBehindIt()
        {
            VK.TJunction tj = VK.MakeT();
            RoadNetwork n = tj.Net;
            VehicleSystem s = VK.Sys(n);
            LaneId stem = VK.Lane(n, tj.Stem, true);
            LaneId east = VK.Lane(n, tj.East, true);
            EntityId blocker = VK.Spawn(s, VehicleTypeId.Car, new[] { east }, 40f, 90f);
            s.SetFrozen(blocker, true);
            EntityId f = VK.Spawn(s, VehicleTypeId.Car, new[] { stem, east }, 0f, 90f);
            for (int t = 0; t < 1200; t++)
            {
                s.Update();
                ref readonly Vehicle v = ref s.GetVehicle(f);
                float gap;
                if (v.Kind == TrackKind.Lane && v.RouteCursor == 1) gap = s.GetVehicle(blocker).S - 4.5f - v.S;
                else gap = float.MaxValue;
                Assert.True(gap >= 0.5f - 1e-3f, "hueco " + gap);
            }
            ref readonly Vehicle w = ref s.GetVehicle(f);
            Assert.Equal(TrackKind.Lane, w.Kind);
            Assert.Equal(1, w.RouteCursor);
            float finalGap = s.GetVehicle(blocker).S - 4.5f - w.S;
            Assert.True(finalGap >= 1.5f && finalGap <= 2.5f, "hueco final " + finalGap);
        }

        [Fact]
        public void Gate_DenyAll_StopsAtStopLine()
        {
            VK.TJunction tj = VK.MakeT();
            RoadNetwork n = tj.Net;
            var gate = new RecordingGate(n) { DenyAll = true };
            VehicleSystem s = VK.Sys(n, 64, 1, false, gate);
            LaneId stem = VK.Lane(n, tj.Stem, true);
            LaneId east = VK.Lane(n, tj.East, true);
            EntityId f = VK.Spawn(s, VehicleTypeId.Car, new[] { stem, east }, 0f, 50f, 8f);
            VK.Run(s, 600);
            ref readonly Vehicle v = ref s.GetVehicle(f);
            Assert.Equal(TrackKind.Lane, v.Kind);
            Assert.Equal(0, v.RouteCursor);
            Assert.True(v.S <= n.GetLane(stem).Length - 1f + 0.01f, "S = " + v.S);
            Assert.True(v.V < 0.05f);
            Assert.True(gate.Requests > 0);
            Assert.Equal(0, gate.Entered);
        }

        [Fact]
        public void Gate_GapOnly_FullExit_WaitsThenPasses()
        {
            VK.TJunction tj = VK.MakeT();
            RoadNetwork n = tj.Net;
            VehicleSystem s = VK.Sys(n);
            LaneId stem = VK.Lane(n, tj.Stem, true);
            LaneId east = VK.Lane(n, tj.East, true);
            // Carril de salida lleno de parados desde el principio.
            var blockers = new List<EntityId>();
            for (float pos = 96f; pos > 7f; pos -= 8f)
            {
                EntityId b = VK.Spawn(s, VehicleTypeId.Car, new[] { east }, pos, 100f);
                s.SetFrozen(b, true);
                blockers.Add(b);
            }
            EntityId f = VK.Spawn(s, VehicleTypeId.Car, new[] { stem, east }, 0f, 99f, 5f);
            VK.Run(s, 400);
            Assert.Equal(TrackKind.Lane, s.GetVehicle(f).Kind);
            Assert.Equal(0, s.GetVehicle(f).RouteCursor);
            Assert.True(s.GetVehicle(f).V < 0.05f);
            foreach (EntityId b in blockers) Assert.True(s.Despawn(b));
            bool crossed = false;
            for (int t = 0; t < 1200 && s.IsAlive(f); t++)
            {
                s.Update();
                if (s.IsAlive(f) && s.GetVehicle(f).RouteCursor == 1) crossed = true;
            }
            Assert.True(crossed);
            Assert.False(s.IsAlive(f));
            Assert.Equal(1, s.ArrivedCount);
        }

        // 6. Transicion carril -> conexion -> carril en una T, a derecha e izquierda.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TJunction_Turns_RouteCursorAndArrival(bool toEast)
        {
            VK.TJunction tj = VK.MakeT();
            RoadNetwork n = tj.Net;
            var gate = new RecordingGate(n);
            VehicleSystem s = VK.Sys(n, 64, 1, false, gate);
            LaneId stem = VK.Lane(n, tj.Stem, true);
            LaneId exit = toEast ? VK.Lane(n, tj.East, true) : VK.Lane(n, tj.West, false);
            int ci = VK.ConnIndex(n, stem, exit);
            Assert.True(ci >= 0);
            Assert.Equal(toEast ? TurnType.Right : TurnType.Left, n.GetConnection(ci).Turn);
            EntityId f = VK.Spawn(s, VehicleTypeId.Car, new[] { stem, exit }, 0f, 60f);
            bool onConn = false;
            int lastCursor = 0;
            for (int t = 0; t < 1500 && s.IsAlive(f); t++)
            {
                s.Update();
                if (!s.IsAlive(f)) break;
                ref readonly Vehicle v = ref s.GetVehicle(f);
                if (v.Kind == TrackKind.Connection)
                {
                    onConn = true;
                    Assert.Equal(0, v.RouteCursor);
                    Assert.Equal(ci, v.Track);
                }
                Assert.True(v.RouteCursor >= lastCursor);
                lastCursor = v.RouteCursor;
                VK.CheckInvariants(s);
            }
            Assert.True(onConn);
            Assert.False(s.IsAlive(f));
            Assert.Equal(1, s.ArrivedCount);
            Assert.Equal(0, s.AliveCount);
            Assert.Equal(1, gate.Entered);
            Assert.Equal(1, gate.Exited);
            Assert.Equal(1, gate.Removed);
        }

        [Fact]
        public void SegmentRoute_ResolvesLanesAndArrives()
        {
            VK.TJunction tj = VK.MakeT(RoadTypeIds.Avenue4, RoadTypeIds.Avenue4);
            RoadNetwork n = tj.Net;
            VehicleSystem s = VK.Sys(n);
            var route = new[] { new RouteSegment(tj.Stem, true), new RouteSegment(tj.East, true) };
            Assert.True(s.TrySpawn(VehicleTypeId.Car, route, 0f, 60f, 0f, out EntityId f));
            Assert.Equal(VK.Lane(n, tj.Stem, true), s.GetRouteStep(f.Index, 0).Lane);
            Assert.Equal(VK.Lane(n, tj.East, true), s.GetRouteStep(f.Index, 1).Lane);
            VK.Run(s, 1500);
            Assert.False(s.IsAlive(f));
            Assert.Equal(1, s.ArrivedCount);
            // Ruta imposible (sentido contrario al de las conexiones).
            var bad = new[] { new RouteSegment(tj.East, true), new RouteSegment(tj.Stem, true) };
            Assert.False(s.TrySpawn(VehicleTypeId.Car, bad, 0f, 10f, 0f, out _));
        }

        // 9. Anticipacion del limite de la conexion.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Anticipation_EntersTurnAtOrBelowItsSpeedLimit(bool toEast)
        {
            VK.TJunction tj = VK.MakeT();
            RoadNetwork n = tj.Net;
            VehicleSystem s = VK.Sys(n);
            LaneId stem = VK.Lane(n, tj.Stem, true);
            LaneId exit = toEast ? VK.Lane(n, tj.East, true) : VK.Lane(n, tj.West, false);
            int ci = VK.ConnIndex(n, stem, exit);
            float limit = n.GetConnection(ci).SpeedLimit;
            Assert.True(limit < n.GetLane(stem).SpeedLimit);
            EntityId f = VK.Spawn(s, VehicleTypeId.Car, new[] { stem, exit }, 0f, 60f, n.GetLane(stem).SpeedLimit);
            float vEntry = -1f;
            float maxOnConn = 0f;
            for (int t = 0; t < 1200 && s.IsAlive(f); t++)
            {
                s.Update();
                if (!s.IsAlive(f)) break;
                ref readonly Vehicle v = ref s.GetVehicle(f);
                if (v.Kind == TrackKind.Connection)
                {
                    if (vEntry < 0f) vEntry = v.V;
                    maxOnConn = Math.Max(maxOnConn, v.V);
                }
            }
            Assert.True(vEntry >= 0f);
            Assert.True(vEntry <= limit + 0.5f, "entrada a " + vEntry + " con limite " + limit);
            Assert.True(maxOnConn <= limit + 0.5f);
        }

        // 4. Nunca V < 0 en una poblacion densa.
        [Fact]
        public void Grid_Population_NeverNegativeOrNaN_ListsConsistent()
        {
            RoadNetwork n = VK.MakeGrid(4, 100f);
            VehicleSystem s = VK.Sys(n, 600, 3, variation: true);
            var pop = new VK.Population(n, s, 200, 11) { Steps = 6 };
            for (int t = 0; t < 1500; t++)
            {
                pop.TopUp();
                s.Update();
                if ((t & 15) == 0) VK.CheckInvariants(s);
            }
            VK.CheckInvariants(s);
            Assert.True(s.ArrivedCount > 20);
        }

        // 11. Snapshot.
        [Fact]
        public void Snapshot_CountsPositionAndConsecutiveTicks()
        {
            RoadNetwork n = Kit.New();
            EntityId seg = Kit.SegAt(n, 0, 0, 300, 0, RoadTypeIds.Street2);
            VehicleSystem s = VK.Sys(n);
            LaneId fwd = VK.Lane(n, seg, true), bwd = VK.Lane(n, seg, false);
            Assert.False(s.Snapshots.Acquire(out _, out _));
            for (int i = 0; i < 5; i++)
            {
                VK.Spawn(s, VehicleTypeId.Car, new[] { fwd }, 10f + 20f * i, 290f);
                VK.Spawn(s, VehicleTypeId.Truck, new[] { bwd }, 20f + 25f * i, 290f);
            }
            VK.Run(s, 30);
            Assert.True(s.Snapshots.Acquire(out SnapshotFrame prev, out SnapshotFrame cur));
            try
            {
                Assert.Equal(s.AliveCount, cur.Count);
                Assert.Equal(s.AliveCount, prev.Count);
                Assert.Equal(prev.Tick + 1, cur.Tick);
                Assert.Equal(s.CurrentTick, cur.Tick);
                for (int i = 0; i < cur.Count; i++)
                {
                    VehicleSnapshot snap = cur.Data![i];
                    ref readonly Vehicle v = ref s.GetVehicle(snap.Id);
                    Assert.Equal(v.Generation, snap.Generation);
                    LaneId lane = new LaneId(v.Track);
                    float offset = n.GetLane(lane).Offset;
                    Assert.True(Math.Abs(Math.Abs(snap.Position.Z) - Math.Abs(offset)) < 0.01f);
                    float expX = n.GetLane(lane).Direction == LaneDirection.Forward ? v.S : 300f - v.S;
                    Assert.True(Math.Abs(snap.Position.X - expX) < 0.02f);
                    Assert.Equal(v.V, snap.Speed);
                    Assert.True(Math.Abs(Math.Abs(snap.Tangent.X) - 1f) < 1e-3f);
                }
            }
            finally { s.Snapshots.Release(); }
        }

        [Fact]
        public void SnapshotRing_ReaderHoldingBuffers_WriterSkipsInsteadOfOverwriting()
        {
            var ring = new SnapshotRing(4);
            for (ulong t = 1; t <= 3; t++)
            {
                Assert.True(ring.TryBeginWrite(out VehicleSnapshot[] b));
                b[0].Id = (int)t;
                ring.Publish(1, t);
            }
            Assert.True(ring.Acquire(out SnapshotFrame prev, out SnapshotFrame cur));
            Assert.Equal(2UL, prev.Tick);
            Assert.Equal(3UL, cur.Tick);
            // El lector retiene 2 y 3: solo queda libre el bufer de 1; una publicacion mas es posible, la siguiente se salta.
            Assert.True(ring.TryBeginWrite(out VehicleSnapshot[] w));
            w[0].Id = 4;
            ring.Publish(1, 4);
            Assert.False(ring.TryBeginWrite(out _));
            Assert.Equal(2, prev.Data![0].Id);
            Assert.Equal(3, cur.Data![0].Id);
            ring.Release();
            Assert.True(ring.TryBeginWrite(out _));
        }

        // 13. Epoca.
        [Fact]
        public void Epoch_RemovingSegmentUnderVehicles_DespawnsThemAndFlagsUpstream()
        {
            RoadNetwork n = Kit.New();
            EntityId[] chain = VK.Chain(n, 4, 100f, RoadTypeIds.Street2);
            var gate = new RecordingGate(n);
            VehicleSystem s = VK.Sys(n, 64, 1, false, gate);
            var lanes = new LaneId[4];
            for (int i = 0; i < 4; i++) lanes[i] = VK.Lane(n, chain[i], true);
            EntityId onSeg = VK.Spawn(s, VehicleTypeId.Car, new[] { lanes[2] }, 50f, 99f);
            EntityId upstream = VK.Spawn(s, VehicleTypeId.Car, lanes, 20f, 90f, 5f);
            EntityId downstream = VK.Spawn(s, VehicleTypeId.Car, new[] { lanes[3] }, 20f, 90f);
            VK.Run(s, 10);

            Assert.True(n.RemoveSegment(chain[2], null));
            s.Update();

            Assert.False(s.IsAlive(onSeg));
            Assert.True(s.IsAlive(upstream));
            Assert.True(s.IsAlive(downstream));
            Assert.Equal(1, s.DespawnedByEdit);
            Assert.Equal(1, gate.NetworkChanges);
            Assert.True((s.GetVehicle(upstream).Flags & VehicleFlags.NeedsReroute) != 0);
            Assert.Equal(1, s.GetVehicle(upstream).RouteLast); // truncada tras el tramo 2
            Assert.True((s.GetVehicle(downstream).Flags & VehicleFlags.NeedsReroute) == 0);
            VK.CheckInvariants(s);

            VK.Run(s, 600);
            VK.CheckInvariants(s);
            // El truncado se detiene al final de su tramo sin llegar ni salirse.
            ref readonly Vehicle v = ref s.GetVehicle(upstream);
            Assert.Equal(TrackKind.Lane, v.Kind);
            Assert.True(v.S < n.GetLane(lanes[1]).Length);
            Assert.True(v.V < 0.05f);
            Assert.Equal(1, s.DespawnNeedsReroute());
            Assert.False(s.IsAlive(upstream));
        }

        [Fact]
        public void Epoch_RebuildingJunctionUnderVehicleOnConnection_RemapsAndArrives()
        {
            VK.TJunction tj = VK.MakeT();
            RoadNetwork n = tj.Net;
            var gate = new RecordingGate(n);
            VehicleSystem s = VK.Sys(n, 64, 1, false, gate);
            LaneId stem = VK.Lane(n, tj.Stem, true);
            LaneId east = VK.Lane(n, tj.East, true);
            EntityId f = VK.Spawn(s, VehicleTypeId.Car, new[] { stem, east }, 0f, 60f);
            int guard = 0;
            while (s.GetVehicle(f).Kind != TrackKind.Connection && guard++ < 2000) s.Update();
            Assert.Equal(TrackKind.Connection, s.GetVehicle(f).Kind);
            int enteredBefore = gate.Entered;

            // Un cuarto brazo en el nodo central regenera todas sus conexiones.
            EntityId far = n.AddNodeRaw(Kit.V(0f, 100f), 0f);
            Assert.True(n.AddSegmentRaw(tj.Node, far, Urbe.Sim.Geometry.Bezier3.FromLine(Kit.V(0f, 0f), Kit.V(0f, 100f)), RoadTypeIds.Street2).IsValid);
            s.Update();
            Assert.True(s.IsAlive(f));
            Assert.Equal(1, gate.NetworkChanges);
            Assert.Equal(enteredBefore + 1, gate.Entered); // la puerta recibe OnEntered del remapeo
            VK.CheckInvariants(s);
            VK.Run(s, 1500);
            Assert.False(s.IsAlive(f));
            Assert.Equal(1, s.ArrivedCount);
            Assert.Equal(1, gate.Exited);
        }

        [Fact]
        public void TryReplaceRoute_AfterEdit_ClearsNeedsReroute()
        {
            RoadNetwork n = Kit.New();
            EntityId a = Kit.SegAt(n, 0, 0, 100, 0, RoadTypeIds.Street2);
            EntityId b = Kit.SegAt(n, 100, 0, 200, 0, RoadTypeIds.Street2);
            EntityId alt = Kit.SegAt(n, 100, 0, 100, 100, RoadTypeIds.Street2);
            VehicleSystem s = VK.Sys(n);
            LaneId la = VK.Lane(n, a, true), lb = VK.Lane(n, b, true), lalt = VK.Lane(n, alt, true);
            EntityId f = VK.Spawn(s, VehicleTypeId.Car, new[] { la, lb }, 0f, 90f);
            VK.Run(s, 5);
            Assert.True(n.RemoveSegment(b, null));
            s.Update();
            Assert.True((s.GetVehicle(f).Flags & VehicleFlags.NeedsReroute) != 0);
            Assert.True(s.TryReplaceRoute(f, new[] { la, lalt }, 90f));
            Assert.True((s.GetVehicle(f).Flags & VehicleFlags.NeedsReroute) == 0);
            VK.Run(s, 1500);
            Assert.False(s.IsAlive(f));
            Assert.Equal(1, s.ArrivedCount);
        }
    }
}
