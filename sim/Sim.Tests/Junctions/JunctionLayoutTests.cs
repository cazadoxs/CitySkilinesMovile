using System;
using System.Collections.Generic;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Junctions;
using Urbe.Sim.Random;
using Urbe.Sim.Roads;
using Urbe.Sim.Tests.Roads;
using Xunit;

namespace Urbe.Sim.Tests.Junctions
{
    /// <summary>Tests 1-4 y 11 del ADR 004: mascara, zonas, via principal y fases.</summary>
    public class JunctionLayoutTests
    {
        private static JunctionNode Layout(Crossing x) => new JunctionBuilder(new JunctionConfig()).Build(x.Net, x.Center);

        private static int Local(Crossing x, int conn) => x.Net.GetConnection(conn).LocalIndex;

        private static ConflictKind? Kind(JunctionNode jn, Crossing x, int ci, int cj)
        {
            int i = Local(x, ci), j = Local(x, cj);
            if ((jn.Gates[i].ConflictMask & (1UL << j)) == 0) return null;
            return JK.Find(jn, i, j).Kind;
        }

        /// <summary>Distancia minima fina entre dos conexiones (muestras cada 0,1 m de arco).</summary>
        private static float FineDistance(in LaneConnection a, in LaneConnection b)
        {
            List<Vec2> pa = Dense(a), pb = Dense(b);
            float best = float.MaxValue;
            foreach (Vec2 p in pa)
                foreach (Vec2 q in pb)
                {
                    float d = Vec2.Distance(p, q);
                    if (d < best) best = d;
                }
            return best;
        }

        private static List<Vec2> Dense(in LaneConnection c)
        {
            var list = new List<Vec2>();
            if (c.Length < 1e-3f)
            {
                list.Add(c.Curve.P0);
                return list;
            }
            var table = new ArcLengthTable(c.Curve, 64);
            int m = (int)Math.Ceiling(table.TotalLength / 0.1f) + 1;
            for (int q = 0; q < m; q++) list.Add(c.Curve.Evaluate(table.TAtDistance(table.TotalLength * q / (m - 1))));
            return list;
        }

        private static void CheckSymmetric(JunctionNode jn, RoadNetwork n)
        {
            ReadOnlySpan<LaneConnection> conns = n.GetConnectionsAt(jn.Node);
            for (int i = 0; i < jn.ConnCount; i++)
            {
                Assert.True((jn.Gates[i].ConflictMask & (1UL << i)) == 0, "reflexiva en " + i);
                Assert.Equal(Bits.PopCount(jn.Gates[i].ConflictMask), jn.Gates[i].ConflictCount);
                int prev = -1;
                foreach (ConflictEntry e in jn.ConflictsOf(i))
                {
                    Assert.True(e.Other > prev, "entradas sin ordenar");
                    prev = e.Other;
                    Assert.True((jn.Gates[e.Other].ConflictMask & (1UL << i)) != 0, "no simetrica " + i + "-" + e.Other);
                    Assert.Equal(e.Kind, JK.Find(jn, e.Other, i).Kind);
                    Assert.Equal(JK.Find(jn, e.Other, i).EndDm, e.OtherEndDm);
                    Assert.True(e.StartDm <= e.EndDm);
                    Assert.True(conns[i].From != conns[e.Other].From, "mismo From en conflicto");
                }
            }
        }

        // 1. Mascara de una X 1+1 (street_2). Brazos: 0 = E, 1 = N, 2 = O, 3 = S.
        [Fact]
        public void Mask_StreetX_SymmetricAndExpectedKinds()
        {
            Crossing x = JK.X(RoadTypeIds.Street2, RoadTypeIds.Street2);
            JunctionNode jn = Layout(x);
            RoadNetwork n = x.Net;
            CheckSymmetric(jn, n);

            int sW = x.ConnTurn(2, TurnType.Straight), sE = x.ConnTurn(0, TurnType.Straight), sN = x.ConnTurn(1, TurnType.Straight);
            int lW = x.ConnTurn(2, TurnType.Left), rE = x.ConnTurn(0, TurnType.Right), rW = x.ConnTurn(2, TurnType.Right);
            Assert.True(sW >= 0 && sE >= 0 && sN >= 0 && lW >= 0 && rE >= 0 && rW >= 0);
            Assert.Equal(ConflictKind.Cross, Kind(jn, x, sW, sN));           // recto O-E x recto N-S
            Assert.Equal(ConflictKind.Cross, Kind(jn, x, lW, sE));           // izquierda desde O x recto E-O
            Assert.Equal(n.GetConnection(lW).To, n.GetConnection(rE).To);
            Assert.Equal(ConflictKind.Merge, Kind(jn, x, lW, rE));           // izquierda desde O x derecha desde E (misma salida)
            Assert.Equal(n.GetConnection(rW).To, n.GetConnection(sN).To);
            Assert.Equal(ConflictKind.Merge, Kind(jn, x, rW, sN));           // derecha desde O x recto N-S
            Assert.Null(Kind(jn, x, sW, sE));                                // rectos opuestos
            // Derechas opuestas: con la geometria de ADR 001 (sin retranqueo) las curvas de giro a la derecha de una calle de 1+1
            // hacen un lazo que pasa a ~1,2 m de la opuesta, asi que hay conflicto por el criterio de distancia. En la X de
            // avenidas (abajo) no lo hay.
            bool near = FineDistance(n.GetConnection(rW), n.GetConnection(rE)) < 2.5f;
            Assert.Equal(near, Kind(jn, x, rW, rE) != null);
            // Mismo From nunca.
            ReadOnlySpan<LaneConnection> conns = n.GetConnectionsAt(x.Center);
            for (int i = 0; i < conns.Length; i++)
                for (int j = 0; j < conns.Length; j++)
                    if (i != j && conns[i].From == conns[j].From) Assert.True((jn.Gates[i].ConflictMask & (1UL << j)) == 0);
        }

        [Fact]
        public void Mask_AvenueX_OppositeRightsAndStraightsDoNotConflict()
        {
            Crossing x = JK.X(RoadTypeIds.Avenue4, RoadTypeIds.Avenue4);
            JunctionNode jn = Layout(x);
            CheckSymmetric(jn, x.Net);
            Assert.Null(Kind(jn, x, x.ConnTurn(0, TurnType.Right), x.ConnTurn(2, TurnType.Right)));
            Assert.Null(Kind(jn, x, x.ConnTurn(0, TurnType.Straight, 0), x.ConnTurn(2, TurnType.Straight, 0)));
            Assert.Equal(ConflictKind.Cross, Kind(jn, x, x.ConnTurn(0, TurnType.Straight, 1), x.ConnTurn(1, TurnType.Right)));
        }

        // 2. Zonas: los cortes de CurveCurve caen dentro; Merge acaba en L; Start <= End.
        [Fact]
        public void Zones_ContainCurveHits_MergeEndsAtLength()
        {
            var crossings = new[]
            {
                JK.X(RoadTypeIds.Street2, RoadTypeIds.Street2),
                JK.X(RoadTypeIds.Avenue4, RoadTypeIds.Avenue4),
                JK.X(RoadTypeIds.Avenue6, RoadTypeIds.Street2),
                JK.T(RoadTypeIds.Street2, RoadTypeIds.Avenue4),
            };
            var hits = new CurveHit[8];
            int checkedHits = 0;
            foreach (Crossing x in crossings)
            {
                JunctionNode jn = Layout(x);
                ReadOnlySpan<LaneConnection> conns = x.Net.GetConnectionsAt(x.Center);
                for (int i = 0; i < jn.ConnCount; i++)
                {
                    foreach (ConflictEntry e in jn.ConflictsOf(i))
                    {
                        Assert.True(e.StartDm <= e.EndDm);
                        Assert.True(e.EndDm <= (int)Math.Ceiling(conns[i].Length * 10f) + 1);
                        if (e.Kind == ConflictKind.Merge)
                        {
                            Assert.Equal(conns[i].To, conns[e.Other].To);
                            Assert.Equal((int)Math.Ceiling(conns[i].Length * 10.0 - 1e-4), e.EndDm);
                            continue;
                        }
                        if (conns[i].Length < 1e-3f || conns[e.Other].Length < 1e-3f) continue;
                        int h = Intersections.CurveCurve(conns[i].Curve, conns[e.Other].Curve, hits);
                        var ti = new ArcLengthTable(conns[i].Curve, 64);
                        for (int k = 0; k < h; k++)
                        {
                            float s = ti.DistanceAtT(hits[k].TA) * conns[i].Length / ti.TotalLength;
                            Assert.True(s >= e.StartDm * 0.1f - 0.05f && s <= e.EndDm * 0.1f + 0.05f,
                                "corte en " + s + " fuera de [" + e.StartDm + ", " + e.EndDm + "] dm");
                            checkedHits++;
                        }
                    }
                }
            }
            Assert.True(checkedHits > 10, "pocos cortes comprobados: " + checkedHits);
        }

        // 3. 200 nodos aleatorios: Merge <=> mismo To; nunca mismo From; distancia fina < 2,3 => conflicto, > 2,7 => no.
        [Fact]
        public void RandomNodes_ConflictMatchesFineDistance()
        {
            var rng = new Pcg32(2024, 7);
            ushort[] types = { RoadTypeIds.Street2, RoadTypeIds.OneWay2, RoadTypeIds.Avenue4, RoadTypeIds.Avenue6 };
            var builder = new JunctionBuilder(new JunctionConfig());
            int nodes = 0, near = 0, far = 0;
            while (nodes < 200)
            {
                RoadNetwork n = Kit.New();
                EntityId c = n.AddNodeRaw(Kit.V(0, 0), 0f);
                int arms = 3 + rng.NextInt(3);
                int start = rng.NextInt(8);
                var used = new bool[8];
                int made = 0;
                for (int k = 0; k < arms; k++)
                {
                    int slot = (start + k * (8 / arms) + rng.NextInt(2)) % 8;
                    if (used[slot]) continue;
                    used[slot] = true;
                    double ang = (slot * 45.0 + (rng.NextFloat() * 20.0 - 10.0)) * Math.PI / 180.0;
                    float len = 40f + 40f * rng.NextFloat();
                    EntityId o = n.AddNodeRaw(Kit.V((float)(Math.Cos(ang) * len), (float)(Math.Sin(ang) * len)), 0f);
                    ushort type = types[rng.NextInt(types.Length)];
                    bool inward = rng.NextInt(2) == 0;
                    EntityId a = inward ? o : c, b = inward ? c : o;
                    if (n.AddSegmentRaw(a, b, Bezier3.FromLine(n.GetNode(a).Position, n.GetNode(b).Position), type).IsValid) made++;
                }
                if (made < 3 || n.GetConnectionsAt(c).Length < 2) continue;
                nodes++;
                JunctionNode jn = builder.Build(n, c);
                ReadOnlySpan<LaneConnection> conns = n.GetConnectionsAt(c);
                for (int i = 0; i < conns.Length; i++)
                {
                    for (int j = i + 1; j < conns.Length; j++)
                    {
                        bool bit = (jn.Gates[i].ConflictMask & (1UL << j)) != 0;
                        if (conns[i].From == conns[j].From) { Assert.False(bit, "mismo From"); continue; }
                        if (conns[i].To == conns[j].To)
                        {
                            Assert.True(bit);
                            Assert.Equal(ConflictKind.Merge, JK.Find(jn, i, j).Kind);
                            continue;
                        }
                        if (bit) Assert.Equal(ConflictKind.Cross, JK.Find(jn, i, j).Kind);
                        float d = FineDistance(conns[i], conns[j]);
                        if (d < 2.3f) { Assert.True(bit, "d=" + d + " sin conflicto L=" + conns[i].Length + "/" + conns[j].Length + " " + conns[i].Turn + "/" + conns[j].Turn + " P " + conns[i].Curve.P0 + conns[i].Curve.P1 + conns[i].Curve.P2 + conns[i].Curve.P3 + " Q " + conns[j].Curve.P0 + conns[j].Curve.P1 + conns[j].Curve.P2 + conns[j].Curve.P3); near++; }
                        else if (d > 2.7f) { Assert.False(bit, "d=" + d + " con conflicto"); far++; }
                    }
                }
            }
            Assert.True(near > 100 && far > 100, "near=" + near + " far=" + far);
        }

        // 4. Via principal.
        [Fact]
        public void MajorRoad_PicksHigherHierarchy_AndIsStable()
        {
            Crossing t = JK.T(RoadTypeIds.Street2, RoadTypeIds.Avenue4);
            JunctionNode jt = Layout(t);
            AssertMajor(t, jt, 0, 2);
            Assert.Equal(ApproachRule.Free, jt.Gates[Local(t, t.ConnTurn(0, TurnType.Straight))].Rule);
            Assert.Equal(ApproachRule.Yield, jt.Gates[Local(t, t.ConnTurn(3, TurnType.Left))].Rule);

            Crossing xa = JK.X(RoadTypeIds.Avenue4, RoadTypeIds.Street2);
            AssertMajor(xa, Layout(xa), 0, 2);
            Crossing xb = JK.X(RoadTypeIds.Street2, RoadTypeIds.Avenue4);
            AssertMajor(xb, Layout(xb), 1, 3);

            Crossing e1 = JK.X(RoadTypeIds.Street2, RoadTypeIds.Street2), e2 = JK.X(RoadTypeIds.Street2, RoadTypeIds.Street2);
            JunctionNode j1 = Layout(e1), j2 = Layout(e2), j1b = Layout(e1);
            Assert.Equal(j1.MajorA, j2.MajorA);
            Assert.Equal(j1.MajorB, j2.MajorB);
            Assert.Equal(j1.MajorA, j1b.MajorA);
            Assert.NotEqual(JunctionNode.NoSlot, j1.MajorA);

            // Nodo de 2 segmentos: todos principales.
            Crossing c2 = JK.Make(new ushort?[] { RoadTypeIds.Street2, null, RoadTypeIds.Avenue4, null });
            JunctionNode jc2 = Layout(c2);
            Assert.Equal(JunctionNode.NoSlot, jc2.MajorA);
            for (int i = 0; i < jc2.ConnCount; i++) Assert.Equal(ApproachRule.Free, jc2.Gates[i].Rule);
        }

        private static void AssertMajor(Crossing x, JunctionNode jn, int armA, int armB)
        {
            ReadOnlySpan<EntityId> segs = x.Net.GetNodeSegments(x.Center);
            Assert.NotEqual(JunctionNode.NoSlot, jn.MajorA);
            var got = new HashSet<int> { x.ArmOfSegment(segs[jn.MajorA].Index), x.ArmOfSegment(segs[jn.MajorB].Index) };
            Assert.True(got.SetEquals(new[] { armA, armB }), "principal = " + string.Join(",", got));
        }

        [Fact]
        public void Ranks_FollowTable()
        {
            Crossing t = JK.T(RoadTypeIds.Street2, RoadTypeIds.Avenue4);
            JunctionNode jn = Layout(t);
            Assert.Equal(6, jn.Gates[Local(t, t.ConnTurn(0, TurnType.Straight))].Rank);
            Assert.Equal(5, jn.Gates[Local(t, t.ConnTurn(0, TurnType.Left))].Rank);
            Assert.Equal(6, jn.Gates[Local(t, t.ConnTurn(2, TurnType.Right))].Rank);
            Assert.Equal(1, jn.Gates[Local(t, t.ConnTurn(3, TurnType.Left))].Rank);
            Assert.Equal(3, jn.Gates[Local(t, t.ConnTurn(3, TurnType.Right))].Rank);
        }

        [Fact]
        public void Control_AutoAndExplicit()
        {
            Crossing a = JK.X(RoadTypeIds.Street2, RoadTypeIds.Street2);
            Assert.Equal(ControlKind.Priority, JunctionBuilder.ResolveControl(a.Net, a.Center));
            Crossing c = JK.X(RoadTypeIds.Avenue4, RoadTypeIds.Avenue4);
            Assert.Equal(ControlKind.Signal, JunctionBuilder.ResolveControl(c.Net, c.Center));
            Crossing t = JK.T(RoadTypeIds.Avenue4, RoadTypeIds.Avenue4);
            Assert.Equal(ControlKind.Signal, JunctionBuilder.ResolveControl(t.Net, t.Center));
            Crossing ts = JK.T(RoadTypeIds.Street2, RoadTypeIds.Avenue4);
            Assert.Equal(ControlKind.Priority, JunctionBuilder.ResolveControl(ts.Net, ts.Center));
            ulong e = ts.Net.Epoch, h = ts.Net.ComputeHash();
            Assert.True(ts.Net.SetNodeControl(ts.Center, JunctionControlIds.Stop));
            Assert.Equal(e + 1, ts.Net.Epoch);
            Assert.NotEqual(h, ts.Net.ComputeHash());
            ts.Net.ConsumeChanges(out ReadOnlySpan<EntityId> changed, out _);
            Assert.True(changed.ToArray().Length >= 1 && Array.IndexOf(changed.ToArray(), ts.Center) >= 0);
            Assert.Equal(ControlKind.Stop, JunctionBuilder.ResolveControl(ts.Net, ts.Center));
            JunctionNode jn = Layout(ts);
            Assert.Equal(ApproachRule.Stop, jn.Gates[Local(ts, ts.ConnTurn(3, TurnType.Left))].Rule);
            Assert.Equal(ApproachRule.Free, jn.Gates[Local(ts, ts.ConnTurn(0, TurnType.Straight))].Rule);
            Assert.False(ts.Net.SetNodeControl(EntityId.None, 1));
        }

        // 11a. Numero de fases y ciclo exacto.
        [Fact]
        public void Phases_CountAndCycle()
        {
            var cfg = new JunctionConfig();
            Crossing t = JK.T(RoadTypeIds.Avenue4, RoadTypeIds.Avenue4);
            JunctionNode jt = Layout(t);
            Assert.Equal(2, jt.PhaseCount);
            Assert.Equal(250 + 30 + 20 + 150 + 30 + 20, jt.CycleTicks);
            Crossing x = JK.X(RoadTypeIds.Avenue4, RoadTypeIds.Avenue4);
            JunctionNode jx = Layout(x);
            Assert.Equal(2, jx.PhaseCount);
            Assert.Equal(500, jx.CycleTicks);
            // Las dos vias de la X van en fases distintas; los dos accesos de cada via, en la misma.
            byte pe = jx.Gates[Local(x, x.ConnTurn(0, TurnType.Straight))].PhaseBits;
            Assert.Equal(pe, jx.Gates[Local(x, x.ConnTurn(2, TurnType.Straight))].PhaseBits);
            Assert.NotEqual(pe, jx.Gates[Local(x, x.ConnTurn(1, TurnType.Straight))].PhaseBits);

            // 5 accesos: par recto (0 y 180 grados) en la fase 0 y una fase por cada uno de los otros tres.
            (RoadNetwork n5, EntityId c5) = JK.Star(new[] { 0f, 90f, 180f, 225f, 315f },
                new[] { RoadTypeIds.Street2, RoadTypeIds.Street2, RoadTypeIds.Street2, RoadTypeIds.Street2, RoadTypeIds.Street2 });
            Assert.True(n5.SetNodeControl(c5, JunctionControlIds.Signal));
            JunctionNode j5 = new JunctionBuilder(cfg).Build(n5, c5);
            Assert.Equal(4, j5.PhaseCount);
            Assert.Equal(250 + 50 + 3 * (150 + 50), j5.CycleTicks);
            var bySlot = new Dictionary<int, byte>();
            for (int i = 0; i < j5.ConnCount; i++)
            {
                byte bits = j5.Gates[i].PhaseBits;
                Assert.Equal(1, Bits.PopCount(bits));
                if (bySlot.TryGetValue(j5.Gates[i].Approach, out byte prev)) Assert.Equal(prev, bits);
                bySlot[j5.Gates[i].Approach] = bits;
            }
            Assert.Equal(1, bySlot[j5.MajorA]);
            Assert.Equal(1, bySlot[j5.MajorB]);
            Assert.Equal(5, bySlot.Count);

            // Secuencia periodica y estados.
            int cycle = jx.CycleTicks;
            var states = new List<(byte, SignalStage, ushort)>();
            for (int tick = 0; tick < 2 * cycle; tick++)
            {
                jx.UpdateSignal((ulong)tick, cfg);
                states.Add((jx.State.Phase, jx.State.Stage, jx.State.StageTicksLeft));
            }
            for (int tick = 0; tick < cycle; tick++) Assert.Equal(states[tick], states[tick + cycle]);
            Assert.Equal((0, SignalStage.Green, (ushort)250), states[0]);
            Assert.Equal((0, SignalStage.Amber, (ushort)30), states[250]);
            Assert.Equal((0, SignalStage.AllRed, (ushort)20), states[280]);
            Assert.Equal((1, SignalStage.Green, (ushort)150), states[300]);
            Assert.Equal((1, SignalStage.Amber, (ushort)30), states[450]);
            Assert.Equal((1, SignalStage.AllRed, (ushort)1), states[499]);
        }

        [Fact]
        public void Bits_PopCountAndTrailingZeros()
        {
            for (int b = 0; b < 64; b++)
            {
                ulong x = 1UL << b;
                Assert.Equal(b, Bits.TrailingZeroCount(x));
                Assert.Equal(b, Bits.TrailingZeroCount(x | (x << 1)));
                Assert.Equal(1, Bits.PopCount(x));
                Assert.Equal(b + 1, Bits.PopCount(x | (x - 1)));
            }
            Assert.Equal(64, Bits.TrailingZeroCount(0));
            Assert.Equal(64, Bits.PopCount(ulong.MaxValue));
        }

        [Fact]
        public void SortUtil_MatchesArraySort()
        {
            var rng = new Pcg32(5, 5);
            foreach (int n in new[] { 0, 1, 5, 24, 25, 300, 2000 })
            {
                var keys = new ulong[n];
                var idx = new int[n];
                var seen = new HashSet<ulong>();
                for (int i = 0; i < n; i++)
                {
                    ulong k;
                    do { k = ((ulong)rng.NextUInt() << 32 | rng.NextUInt()) >> rng.NextInt(40); } while (!seen.Add(k));
                    keys[i] = k;
                    idx[i] = i;
                }
                var expected = (ulong[])keys.Clone();
                Array.Sort(expected);
                var orig = (ulong[])keys.Clone();
                SortUtil.Sort(keys, idx, n, new ulong[n], new int[n], new int[256]);
                Assert.Equal(expected, keys);
                for (int i = 0; i < n; i++) Assert.Equal(orig[idx[i]], keys[i]);
            }
        }
    }
}
