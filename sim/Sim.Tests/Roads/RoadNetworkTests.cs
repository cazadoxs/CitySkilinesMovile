using System;
using System.Collections.Generic;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Xunit;

namespace Urbe.Sim.Tests.Roads
{
    // Los 15 tests obligatorios de M1-03b (ADR 001), numerados en el nombre.
    public class RoadNetworkTests
    {
        // ---------------------------------------------------------------- 1

        public static readonly object[][] TypeRows =
        {
            // tipo, F, B, offsets de los carriles por slot
            new object[] { RoadTypeIds.Street2, 1, 1, new[] { 1.5f, -1.5f } },
            new object[] { RoadTypeIds.OneWay2, 2, 0, new[] { 1.5f, -1.5f } },
            new object[] { RoadTypeIds.Avenue4, 2, 2, new[] { 5.375f, 2.125f, -5.375f, -2.125f } },
            new object[] { RoadTypeIds.Avenue6, 3, 3, new[] { 9.75f, 6.25f, 2.75f, -9.75f, -6.25f, -2.75f } },
            new object[] { RoadTypeIds.Rural2, 1, 1, new[] { 1.75f, -1.75f } },
            new object[] { RoadTypeIds.Highway4, 2, 2, new[] { 7.125f, 3.375f, -7.125f, -3.375f } },
        };

        [Theory]
        [MemberData(nameof(TypeRows))]
        public void T01_Straight_TwoDeadEnds_LanesOffsetsAndLengths(ushort type, int f, int b, float[] offsets)
        {
            RoadNetwork n = Kit.New();
            Assert.True(Kit.Add(n, 0, 0, 100, 0, type));
            Assert.Equal(2, n.NodeCount);
            Assert.Equal(1, n.SegmentCount);
            Assert.Equal(2, Kit.CountKind(n, JunctionKind.DeadEnd));
            EntityId seg = Kit.AllSegments(n)[0];
            Assert.Equal(f, n.GetSegment(seg).LanesForward);
            Assert.Equal(b, n.GetSegment(seg).LanesBackward);
            Assert.Equal(f + b, n.GetLaneCount(seg));
            Assert.Equal(100f, n.GetSegment(seg).Length, 2);
            RoadType rt = n.Types.Get(type);
            for (int slot = 0; slot < f + b; slot++)
            {
                ref readonly Lane l = ref n.GetLane(n.GetLane(seg, slot));
                Assert.Equal(offsets[slot], l.Offset, 4);
                Assert.Equal(slot < f ? LaneDirection.Forward : LaneDirection.Backward, l.Direction);
                Assert.Equal(100f, l.Length, 2); // recta: el desplazamiento no cambia la longitud
                Assert.Equal(rt.SpeedLimit, l.SpeedLimit, 4);
            }
            Kit.AssertInvariants(n);
        }

        [Fact]
        public void T01_Catalog_MatchesAdrTable()
        {
            RoadTypeCatalog c = RoadTypeCatalog.CreateDefault();
            Assert.Equal(6, c.Count);
            string[] keys = { "street_2", "oneway_2", "avenue_4", "avenue_6", "rural_2", "highway_4" };
            float[] kmh = { 40, 40, 50, 60, 80, 100 };
            float[] width = { 10, 10, 19, 29, 10, 23 };
            for (int i = 0; i < 6; i++)
            {
                RoadType t = c.Get((ushort)i);
                Assert.Equal(keys[i], t.Key);
                Assert.Equal(i, t.Id);
                Assert.Equal(kmh[i] / 3.6f, t.SpeedLimit, 4);
                Assert.Equal(width[i], t.TotalWidth, 4);
                Assert.Equal(i, c.IndexOfKey(keys[i]));
            }
            Assert.True(c.Get(RoadTypeIds.OneWay2).IsOneWay);
            Assert.Equal(-1, c.IndexOfKey("nope"));
        }

        [Fact]
        public void T01_SampleLane_PositionAndTangent()
        {
            RoadNetwork n = Kit.New();
            Kit.Add(n, 0, 0, 100, 0, RoadTypeIds.Street2);
            EntityId seg = Kit.AllSegments(n)[0];
            // Forward: derecha de +X es -Z.
            n.SampleLane(n.GetLane(seg, 0), 50f, out Vec2 p, out Vec2 t);
            Assert.True(p.ApproxEquals(Kit.V(50, -1.5f), 1e-3f));
            Assert.True(t.ApproxEquals(Kit.V(1, 0), 1e-4f));
            // Backward: arranca en B, avanza hacia A, por el lado +Z.
            n.SampleLane(n.GetLane(seg, 1), 0f, out p, out t);
            Assert.True(p.ApproxEquals(Kit.V(100, 1.5f), 1e-3f));
            Assert.True(t.ApproxEquals(Kit.V(-1, 0), 1e-4f));
            n.SampleLane(n.GetLane(seg, 1), 1000f, out p, out _); // se acota a la longitud
            Assert.True(p.ApproxEquals(Kit.V(0, 1.5f), 1e-3f));
        }

        // ---------------------------------------------------------------- 2

        [Fact]
        public void T02_Snap_ToNode_39Yes_41No()
        {
            RoadNetwork n = Kit.New();
            Kit.SegAt(n, 0, 0, 100, 0, RoadTypeIds.Street2);
            // Extremo a 3,9 m del nodo (100,0): se engancha.
            Assert.True(Kit.Add(n, 103.9f, 80, 103.9f, 0, RoadTypeIds.Street2));
            Assert.Equal(3, n.NodeCount);
            Assert.Equal(2, n.SegmentCount);
            Assert.Equal(2, n.GetNodeSegments(Kit.At(n, 100, 0)).Length);

            RoadNetwork m = Kit.New();
            Kit.SegAt(m, 0, 0, 100, 0, RoadTypeIds.Street2);
            // A 4,1 m: nodo nuevo (y no cae en el interior del segmento: esta mas alla del extremo).
            Assert.True(Kit.Add(m, 104.1f, 80, 104.1f, 0, RoadTypeIds.Street2));
            Assert.Equal(4, m.NodeCount);
            Assert.Equal(1, m.GetNodeSegments(Kit.At(m, 100, 0)).Length);
            Assert.True(m.FindNodeAt(Kit.V(104.1f, 0)).IsValid);
        }

        [Fact]
        public void T02_Snap_ToSegmentInterior_SplitsAndMakesT()
        {
            RoadNetwork n = Kit.New();
            Kit.SegAt(n, 0, 0, 100, 0, RoadTypeIds.Street2);
            var edit = new NetworkEdit();
            // El extremo termina a 2 m del eje, en la mitad: split + T.
            Assert.True(n.AddSegment(SegmentRequest.Straight(Kit.V(50, 80), Kit.V(50, 2), RoadTypeIds.Street2), edit));
            Assert.Equal(3, n.SegmentCount);
            Assert.Equal(4, n.NodeCount);
            EntityId t = Kit.At(n, 50, 0);
            Assert.Equal(3, n.GetNodeSegments(t).Length);
            Assert.Equal(JunctionKind.Junction, n.GetNode(t).Kind);
            Assert.Equal(1, edit.RemovedSegments.Length);
            Assert.Equal(3, edit.CreatedSegments.Length); // dos mitades + el nuevo
            Assert.Equal(2, edit.CreatedNodes.Length);    // nodo de corte + extremo libre
            Kit.AssertInvariants(n);
        }

        // ---------------------------------------------------------------- 3

        [Fact]
        public void T03_Crossing_TwoLines_MakesX()
        {
            RoadNetwork n = Kit.New();
            Kit.SegAt(n, -50, 0, 50, 0, RoadTypeIds.Street2);
            Assert.True(Kit.Add(n, 0, -50, 0, 50, RoadTypeIds.Street2));
            Assert.Equal(4, n.SegmentCount);
            Assert.Equal(5, n.NodeCount);
            Assert.Equal(1, Kit.CountKind(n, JunctionKind.Junction));
            Assert.Equal(4, Kit.CountKind(n, JunctionKind.DeadEnd));
            EntityId c = Kit.At(n, 0, 0);
            Assert.Equal(4, n.GetNodeSegments(c).Length);
            Kit.AssertInvariants(n);
        }

        [Fact]
        public void T03_Crossing_ThreeCuts_InOrderOfT()
        {
            RoadNetwork n = Kit.New();
            float[] xs = { 50f, -50f, 0f }; // creados fuera de orden a proposito
            foreach (float x in xs) Kit.SegAt(n, x, -40, x, 40, RoadTypeIds.Street2);
            var edit = new NetworkEdit();
            Assert.True(n.AddSegment(SegmentRequest.Straight(Kit.V(-100, 0), Kit.V(100, 0), RoadTypeIds.Street2), edit));
            // 3 verticales partidas en 2 + 4 trozos nuevos.
            Assert.Equal(10, n.SegmentCount);
            Assert.Equal(11, n.NodeCount);
            Assert.Equal(3, Kit.CountKind(n, JunctionKind.Junction));
            // Los trozos nuevos encadenan -100 -> -50 -> 0 -> 50 -> 100 en ese orden.
            float[] chain = { -100, -50, 0, 50, 100 };
            for (int i = 0; i + 1 < chain.Length; i++)
            {
                EntityId a = Kit.At(n, chain[i], 0);
                EntityId b = Kit.At(n, chain[i + 1], 0);
                EntityId s = Kit.ArmTo(n, a, chain[i + 1], 0);
                Assert.Equal(b, Kit.OtherNode(n, s, a));
                Assert.Equal(50f, n.GetSegment(s).Length, 1);
            }
            Assert.Equal(3, edit.RemovedSegments.Length);
            Assert.Equal(10 - 0, n.SegmentCount);
            Kit.AssertInvariants(n);
        }

        // ---------------------------------------------------------------- 4

        [Fact]
        public void T04_Crossing_NearNode_ReusesTheNode()
        {
            RoadNetwork n = Kit.New();
            EntityId s = Kit.SegAt(n, 0, -50, 0, 50, RoadTypeIds.Street2);
            EntityId end = Kit.At(n, 0, 50);
            // Cruza a 5 m del extremo (0,50): hay que reutilizar ese nodo.
            Assert.True(Kit.Add(n, -50, 45, 50, 45, RoadTypeIds.Street2));
            Assert.Equal(4, n.NodeCount);
            Assert.Equal(3, n.SegmentCount);
            Assert.True(n.IsSegmentAlive(s)); // la vertical no se divide
            Assert.Equal(3, n.GetNodeSegments(end).Length);
            foreach (EntityId seg in Kit.AllSegments(n)) Assert.True(n.GetSegment(seg).Length >= 8f);
            Kit.AssertInvariants(n);
        }

        // ---------------------------------------------------------------- 5

        [Fact]
        public void T05_Split_ContinuityAndLength()
        {
            RoadNetwork n = Kit.New();
            EntityId a = Kit.Node(n, 0, 0), b = Kit.Node(n, 100, 0);
            var curve = new Bezier3(Kit.V(0, 0), Kit.V(40, 60), Kit.V(60, -60), Kit.V(100, 0));
            EntityId seg = n.AddSegmentRaw(a, b, curve, RoadTypeIds.Street2);
            float total = n.GetSegment(seg).Length;
            var edit = new NetworkEdit();
            Assert.True(n.SplitSegment(seg, 0.3f, edit));
            Assert.False(n.IsSegmentAlive(seg));
            Assert.Equal(2, n.SegmentCount);
            Assert.Equal(3, n.NodeCount);
            EntityId mid = edit.CreatedNodes[0];
            Assert.Equal(JunctionKind.Continuation, n.GetNode(mid).Kind);
            EntityId h1 = edit.CreatedSegments[0], h2 = edit.CreatedSegments[1];
            ref readonly Segment s1 = ref n.GetSegment(h1);
            ref readonly Segment s2 = ref n.GetSegment(h2);
            Assert.Equal(a, s1.NodeA);
            Assert.Equal(mid, s1.NodeB);
            Assert.Equal(mid, s2.NodeA);
            Assert.Equal(b, s2.NodeB);
            Assert.True((s1.Curve.P3 - s2.Curve.P0).Length < 1e-4f);
            Assert.True((n.GetNode(mid).Position - s1.Curve.P3).Length < 1e-4f);
            Assert.True(s1.Curve.Tangent(1f).ApproxEquals(s2.Curve.Tangent(0f), 1e-4f));
            Assert.True(Math.Abs(s1.Length + s2.Length - total) / total < 0.001f);
            Assert.True(s1.Curve.Evaluate(0.5f).ApproxEquals(curve.Evaluate(0.15f), 1e-3f));
            Assert.Equal(1f, edit.RemovedSegments.Length);
            Assert.False(n.SplitSegment(seg, 0.5f, edit)); // id obsoleto
            Assert.False(n.SplitSegment(h1, 0f, edit));
            Kit.AssertInvariants(n);
        }

        // ---------------------------------------------------------------- 6

        private static RoadNetwork BuildT(ushort mainType, ushort stemType)
        {
            RoadNetwork n = Kit.New();
            Kit.SegAt(n, -100, 0, 100, 0, mainType);
            Assert.True(Kit.Add(n, 0, -100, 0, 0, stemType));
            return n;
        }

        [Fact]
        public void T06_T_OneLane_StemGetsRightAndLeft_MainStraightPlusTurn()
        {
            RoadNetwork n = BuildT(RoadTypeIds.Street2, RoadTypeIds.Street2);
            EntityId c = Kit.At(n, 0, 0);
            Assert.Equal(JunctionKind.Junction, n.GetNode(c).Kind);
            var all = Kit.ConnsAt(n, c);
            Assert.Equal(6, all.Length);
            Assert.Equal(2, Kit.Count(all, TurnType.Straight));
            Assert.Equal(2, Kit.Count(all, TurnType.Right));
            Assert.Equal(2, Kit.Count(all, TurnType.Left));
            Assert.Equal(0, Kit.Count(all, TurnType.UTurn));

            EntityId stem = Kit.ArmTo(n, c, 0, -100);
            EntityId west = Kit.ArmTo(n, c, -100, 0);
            EntityId east = Kit.ArmTo(n, c, 100, 0);
            // Tallo (sube hacia +Z): a la izquierda va al oeste, a la derecha al este.
            LaneConnection[] fromStem = Kit.ConnsFrom(n, Entry(n, stem, c));
            Assert.Equal(2, fromStem.Length);
            foreach (LaneConnection k in fromStem)
            {
                if (k.Turn == TurnType.Left) Assert.Equal(west, Kit.SegmentOf(n, k.To));
                else
                {
                    Assert.Equal(TurnType.Right, k.Turn);
                    Assert.Equal(east, Kit.SegmentOf(n, k.To));
                }
            }
            // Via principal desde el oeste (hacia +X): recto al este, derecha al tallo.
            LaneConnection[] fromWest = Kit.ConnsFrom(n, Entry(n, west, c));
            Assert.Equal(2, fromWest.Length);
            foreach (LaneConnection k in fromWest)
            {
                if (k.Turn == TurnType.Straight) Assert.Equal(east, Kit.SegmentOf(n, k.To));
                else
                {
                    Assert.Equal(TurnType.Right, k.Turn);
                    Assert.Equal(stem, Kit.SegmentOf(n, k.To));
                }
            }
            // Desde el este (hacia -X): recto al oeste, izquierda al tallo.
            LaneConnection[] fromEast = Kit.ConnsFrom(n, Entry(n, east, c));
            foreach (LaneConnection k in fromEast)
            {
                if (k.Turn == TurnType.Straight) Assert.Equal(west, Kit.SegmentOf(n, k.To));
                else
                {
                    Assert.Equal(TurnType.Left, k.Turn);
                    Assert.Equal(stem, Kit.SegmentOf(n, k.To));
                }
            }
            Kit.AssertInvariants(n);
        }

        /// <summary>Carril de entrada mas a la derecha del segmento hacia el nodo.</summary>
        private static LaneId Entry(RoadNetwork n, EntityId seg, EntityId node, int index = 0)
        {
            ref readonly Segment s = ref n.GetSegment(seg);
            return s.NodeB == node ? n.GetLane(seg, index) : n.GetLane(seg, s.LanesForward + index);
        }

        private static LaneId Exit(RoadNetwork n, EntityId seg, EntityId node, int index = 0)
        {
            ref readonly Segment s = ref n.GetSegment(seg);
            return s.NodeA == node ? n.GetLane(seg, index) : n.GetLane(seg, s.LanesForward + index);
        }

        [Fact]
        public void T06_T_TwoLaneStem_SplitsRightAndLeftByHalves()
        {
            RoadNetwork n = BuildT(RoadTypeIds.Avenue4, RoadTypeIds.OneWay2);
            // oneway_2 hacia el nodo: 2 carriles de entrada sin Recto -> carril 0 Derecha, carril 1 Izquierda.
            EntityId c = Kit.At(n, 0, 0);
            EntityId stem = Kit.ArmTo(n, c, 0, -100);
            LaneConnection[] l0 = Kit.ConnsFrom(n, Entry(n, stem, c, 0));
            LaneConnection[] l1 = Kit.ConnsFrom(n, Entry(n, stem, c, 1));
            Assert.Single(l0);
            Assert.Single(l1);
            Assert.Equal(TurnType.Right, l0[0].Turn);
            Assert.Equal(TurnType.Left, l1[0].Turn);
            Assert.Equal(0, Kit.LaneIndex(n, l0[0].To));
            Assert.Equal(1, Kit.LaneIndex(n, l1[0].To));
        }

        [Fact]
        public void T06_Connections_SpeedsAndGeometryMatchLanes()
        {
            RoadNetwork n = BuildT(RoadTypeIds.Street2, RoadTypeIds.Street2);
            EntityId c = Kit.At(n, 0, 0);
            float v = n.Types.Get(RoadTypeIds.Street2).SpeedLimit;
            var all = n.GetConnectionsAt(c);
            for (int i = 0; i < all.Length; i++)
            {
                ref readonly LaneConnection k = ref all[i];
                float f = k.Turn == TurnType.Straight ? 1f : k.Turn == TurnType.Left ? 0.5f : 0.35f;
                Assert.Equal(Math.Max(4f, v * f), k.SpeedLimit, 4);
                Assert.True(k.Length >= 0f);
                int idx = n.GetConnectionsAt(c).Length > 0 ? FindIndex(n, c, i) : 0;
                // Empieza donde termina el carril de origen y acaba donde empieza el de destino.
                n.SampleLane(k.From, n.GetLane(k.From).Length, out Vec2 endPos, out Vec2 endTan);
                n.SampleLane(k.To, 0f, out Vec2 startPos, out Vec2 startTan);
                n.SampleConnection(idx, 0f, out Vec2 p0, out Vec2 t0);
                n.SampleConnection(idx, k.Length, out Vec2 p1, out Vec2 t1);
                Assert.True(p0.ApproxEquals(endPos, 0.05f));
                Assert.True(p1.ApproxEquals(startPos, 0.05f));
                Assert.True(t0.ApproxEquals(endTan, 0.02f));
                Assert.True(t1.ApproxEquals(startTan, 0.02f));
            }
        }

        private static int FindIndex(RoadNetwork n, EntityId node, int local)
        {
            ref readonly Node nd = ref n.GetNode(node);
            return nd.ConnFirst + local;
        }

        // ---------------------------------------------------------------- 7

        [Fact]
        public void T07_X_OneLane_TwelveConnections_NoUTurn()
        {
            RoadNetwork n = Kit.New();
            Kit.SegAt(n, -50, 0, 50, 0, RoadTypeIds.Street2);
            Assert.True(Kit.Add(n, 0, -50, 0, 50, RoadTypeIds.Street2));
            EntityId c = Kit.At(n, 0, 0);
            var all = Kit.ConnsAt(n, c);
            Assert.Equal(12, all.Length);
            Assert.Equal(0, Kit.Count(all, TurnType.UTurn));
            Assert.Equal(4, Kit.Count(all, TurnType.Straight));
            Assert.Equal(4, Kit.Count(all, TurnType.Right));
            Assert.Equal(4, Kit.Count(all, TurnType.Left));
            foreach (EntityId arm in n.GetNodeSegments(c).ToArray())
                Assert.Equal(3, Kit.ConnsFrom(n, Entry(n, arm, c)).Length);
            Kit.AssertInvariants(n);
        }

        // ---------------------------------------------------------------- 8

        [Fact]
        public void T08_X_TwoPlusTwo_LaneMapping()
        {
            RoadNetwork n = Kit.New();
            Kit.SegAt(n, -50, 0, 50, 0, RoadTypeIds.Avenue4);
            Assert.True(Kit.Add(n, 0, -50, 0, 50, RoadTypeIds.Avenue4));
            EntityId c = Kit.At(n, 0, 0);
            Assert.Equal(16, n.GetConnectionsAt(c).Length);
            foreach (EntityId arm in n.GetNodeSegments(c).ToArray())
            {
                // Carril 0 (derecho): Derecha -> carril 0 y Recto -> carril 0.
                LaneConnection[] r = Kit.ConnsFrom(n, Entry(n, arm, c, 0));
                Assert.Equal(2, r.Length);
                Assert.Equal(1, Kit.Count(r, TurnType.Right));
                Assert.Equal(1, Kit.Count(r, TurnType.Straight));
                foreach (LaneConnection k in r) Assert.Equal(0, Kit.LaneIndex(n, k.To));
                // Carril 1 (izquierdo): Recto -> carril 1 e Izquierda -> carril 1.
                LaneConnection[] l = Kit.ConnsFrom(n, Entry(n, arm, c, 1));
                Assert.Equal(2, l.Length);
                Assert.Equal(1, Kit.Count(l, TurnType.Straight));
                Assert.Equal(1, Kit.Count(l, TurnType.Left));
                foreach (LaneConnection k in l) Assert.Equal(1, Kit.LaneIndex(n, k.To));
            }
            Kit.AssertInvariants(n);
        }

        [Fact]
        public void T08_ThreeLanes_MiddleLaneOnlyGoesStraight()
        {
            RoadNetwork n = Kit.New();
            Kit.SegAt(n, -50, 0, 50, 0, RoadTypeIds.Avenue6);
            Assert.True(Kit.Add(n, 0, -50, 0, 50, RoadTypeIds.Avenue6));
            EntityId c = Kit.At(n, 0, 0);
            foreach (EntityId arm in n.GetNodeSegments(c).ToArray())
            {
                Assert.Equal(2, Kit.ConnsFrom(n, Entry(n, arm, c, 0)).Length);
                LaneConnection[] mid = Kit.ConnsFrom(n, Entry(n, arm, c, 1));
                Assert.Single(mid);
                Assert.Equal(TurnType.Straight, mid[0].Turn);
                Assert.Equal(1, Kit.LaneIndex(n, mid[0].To));
                Assert.Equal(2, Kit.ConnsFrom(n, Entry(n, arm, c, 2)).Length);
            }
        }

        // ---------------------------------------------------------------- 9

        [Fact]
        public void T09_Continuation_LaneCountChanges()
        {
            RoadNetwork n = Kit.New();
            EntityId l = Kit.Node(n, -100, 0), m = Kit.Node(n, 0, 0), r = Kit.Node(n, 100, 0);
            EntityId big = Kit.Seg(n, l, m, RoadTypeIds.Avenue4);
            EntityId small = Kit.Seg(n, m, r, RoadTypeIds.Street2);
            Assert.Equal(JunctionKind.Continuation, n.GetNode(m).Kind);
            var all = Kit.ConnsAt(n, m);
            Assert.Equal(4, all.Length);
            Assert.Equal(4, Kit.Count(all, TurnType.Straight));
            // 2+2 -> 1+1 (sentido +X): los dos carriles van al unico carril.
            Assert.Single(Kit.ConnsFrom(n, Entry(n, big, m, 0)));
            Assert.Single(Kit.ConnsFrom(n, Entry(n, big, m, 1)));
            Assert.Equal(0, Kit.LaneIndex(n, Kit.ConnsFrom(n, Entry(n, big, m, 0))[0].To));
            Assert.Equal(0, Kit.LaneIndex(n, Kit.ConnsFrom(n, Entry(n, big, m, 1))[0].To));
            // 1+1 -> 2+2 (sentido -X): el carril unico se abre a los dos.
            LaneConnection[] back = Kit.ConnsFrom(n, Entry(n, small, m, 0));
            Assert.Equal(2, back.Length);
            Assert.Equal(0, Kit.LaneIndex(n, back[0].To) + Kit.LaneIndex(n, back[1].To) - 1);
            Assert.NotEqual(back[0].To, back[1].To);
            Kit.AssertInvariants(n);
        }

        [Fact]
        public void T09_OneWay_AgainstDirection_NoConnections()
        {
            RoadNetwork n = Kit.New();
            EntityId l = Kit.Node(n, -100, 0), m = Kit.Node(n, 0, 0), r = Kit.Node(n, 100, 0);
            Kit.Seg(n, l, m, RoadTypeIds.OneWay2);
            Kit.Seg(n, r, m, RoadTypeIds.OneWay2); // los dos entran en m
            Assert.Empty(Kit.ConnsAt(n, m));

            RoadNetwork k = Kit.New();
            EntityId l2 = Kit.Node(k, -100, 0), m2 = Kit.Node(k, 0, 0), r2 = Kit.Node(k, 100, 0);
            Kit.Seg(k, l2, m2, RoadTypeIds.OneWay2);
            Kit.Seg(k, m2, r2, RoadTypeIds.OneWay2); // cadena en el mismo sentido
            Assert.Equal(2, Kit.ConnsAt(k, m2).Length);
        }

        // ---------------------------------------------------------------- 10

        [Fact]
        public void T10_DeadEnd_UTurnOnlyOnTwoWay()
        {
            RoadNetwork n = Kit.New();
            Kit.Add(n, 0, 0, 100, 0, RoadTypeIds.Street2);
            foreach (EntityId node in Kit.AllNodes(n))
            {
                var c = Kit.ConnsAt(n, node);
                Assert.Single(c);
                Assert.Equal(TurnType.UTurn, c[0].Turn);
                Assert.NotEqual(c[0].From, c[0].To);
                Assert.Equal(Kit.SegmentOf(n, c[0].From), Kit.SegmentOf(n, c[0].To));
            }

            RoadNetwork a4 = Kit.New();
            Kit.Add(a4, 0, 0, 100, 0, RoadTypeIds.Avenue4);
            foreach (EntityId node in Kit.AllNodes(a4))
            {
                var c = Kit.ConnsAt(a4, node);
                Assert.Single(c);
                Assert.Equal(1, Kit.LaneIndex(a4, c[0].From)); // el mas a la izquierda
                Assert.Equal(1, Kit.LaneIndex(a4, c[0].To));
            }

            RoadNetwork o = Kit.New();
            Kit.Add(o, 0, 0, 100, 0, RoadTypeIds.OneWay2);
            foreach (EntityId node in Kit.AllNodes(o)) Assert.Empty(Kit.ConnsAt(o, node));
        }

        // ---------------------------------------------------------------- 11

        [Fact]
        public void T11_RemoveSegment_OrphansRemoved_TBecomesContinuation()
        {
            RoadNetwork n = BuildT(RoadTypeIds.Street2, RoadTypeIds.Street2);
            EntityId c = Kit.At(n, 0, 0);
            EntityId stem = Kit.ArmTo(n, c, 0, -100);
            EntityId stemEnd = Kit.At(n, 0, -100);
            var edit = new NetworkEdit();
            Assert.True(n.RemoveSegment(stem, edit));
            Assert.False(n.IsSegmentAlive(stem));
            Assert.False(n.IsNodeAlive(stemEnd));
            Assert.True(n.IsNodeAlive(c));
            Assert.Equal(JunctionKind.Continuation, n.GetNode(c).Kind);
            Assert.Equal(2, n.SegmentCount);
            Assert.Equal(3, n.NodeCount);
            Assert.Equal(1, edit.RemovedSegments.Length);
            Assert.False(n.RemoveSegment(stem, edit)); // ya no existe

            // Quitar una mitad: el nodo central pasa a fin de via; el otro extremo desaparece.
            EntityId west = Kit.ArmTo(n, c, -100, 0);
            EntityId westEnd = Kit.At(n, -100, 0);
            Assert.True(n.RemoveSegment(west, edit));
            Assert.False(n.IsNodeAlive(westEnd));
            Assert.Equal(JunctionKind.DeadEnd, n.GetNode(c).Kind);
            Assert.Single(Kit.ConnsAt(n, c));
            Assert.True(n.RemoveSegment(Kit.AllSegments(n)[0], edit));
            Assert.Equal(0, n.SegmentCount);
            Assert.Equal(0, n.NodeCount);
            Assert.False(n.IsNodeAlive(c));
        }

        // ---------------------------------------------------------------- 12

        [Fact]
        public void T12_Epoch_PlusOnePerPublicMutation()
        {
            RoadNetwork n = Kit.New();
            ulong e = n.Epoch;
            EntityId a = n.AddNodeRaw(Kit.V(-50, 0), 0f);
            Assert.Equal(e + 1, n.Epoch);
            EntityId b = n.AddNodeRaw(Kit.V(50, 0), 0f);
            Assert.Equal(e + 2, n.Epoch);
            EntityId s = Kit.Seg(n, a, b, RoadTypeIds.Street2);
            Assert.Equal(e + 3, n.Epoch);
            Assert.False(Kit.Seg(n, a, b, RoadTypeIds.Street2).IsValid); // duplicado: rechazado
            Assert.Equal(e + 3, n.Epoch);

            // Una llamada de alto nivel que hace varias operaciones internas (2 cortes + 2 nodos + 3 segmentos): +1.
            ulong before = n.Epoch;
            Assert.True(Kit.Add(n, 0, -50, 0, 50, RoadTypeIds.Street2));
            Assert.Equal(before + 1, n.Epoch);
            // Consultas y validaciones no la mueven.
            n.CanAddSegment(SegmentRequest.Straight(Kit.V(100, 100), Kit.V(150, 100), RoadTypeIds.Street2));
            n.QuerySegments(new Aabb2(Kit.V(-100, -100), Kit.V(100, 100)), new EntityId[16]);
            n.NearestNode(Kit.V(0, 0), 10f);
            n.ComputeHash();
            Assert.Equal(before + 1, n.Epoch);
            // Rechazo sin cambios: no sube.
            Assert.False(Kit.Add(n, 0, 0, 3, 0, RoadTypeIds.Street2)); // demasiado corto
            Assert.Equal(before + 1, n.Epoch);

            before = n.Epoch;
            EntityId any = Kit.AllSegments(n)[0];
            Assert.True(n.SplitSegment(any, 0.5f, new NetworkEdit()));
            Assert.Equal(before + 1, n.Epoch);
            Assert.True(n.RemoveSegment(Kit.AllSegments(n)[0], new NetworkEdit()));
            Assert.Equal(before + 2, n.Epoch);
            Assert.False(n.RemoveSegment(s, new NetworkEdit())); // obsoleto
            Assert.Equal(before + 2, n.Epoch);
        }

        [Fact]
        public void T12_ConsumeChanges_ListsTouchedSortedAndClears()
        {
            RoadNetwork n = Kit.New();
            int sub = n.Subscribe();
            EntityId a = Kit.Node(n, 0, 0), b = Kit.Node(n, 100, 0);
            EntityId s = Kit.Seg(n, a, b, RoadTypeIds.Street2);
            n.ConsumeChanges(sub, out ReadOnlySpan<EntityId> nodes, out ReadOnlySpan<EntityId> segs);
            Assert.Equal(new[] { a, b }, nodes.ToArray());
            Assert.Equal(new[] { s }, segs.ToArray());
            n.ConsumeChanges(sub, out nodes, out segs);
            Assert.Equal(0, nodes.Length);
            Assert.Equal(0, segs.Length);

            // Dividir toca los extremos (sus carriles cambian), el nodo nuevo y las tres ids de segmento.
            var edit = new NetworkEdit();
            Assert.True(n.SplitSegment(s, 0.5f, edit));
            n.ConsumeChanges(sub, out nodes, out segs);
            Assert.Equal(3, nodes.Length);
            Assert.Equal(3, segs.Length); // la liberada + las dos mitades
            Assert.Contains(s, segs.ToArray());
            Assert.Contains(edit.CreatedSegments[0], segs.ToArray());
            Assert.Contains(edit.CreatedSegments[1], segs.ToArray());
            for (int i = 1; i < nodes.Length; i++) Assert.True(nodes[i - 1].Index < nodes[i].Index);
            for (int i = 1; i < segs.Length; i++) Assert.True(segs[i - 1].Index <= segs[i].Index);

            Assert.True(n.RemoveSegment(edit.CreatedSegments[0], edit));
            n.ConsumeChanges(sub, out nodes, out segs);
            Assert.Single(segs.ToArray());
            Assert.Equal(2, nodes.Length); // el extremo A (huerfano, eliminado) y el nodo central
        }

        // ---------------------------------------------------------------- 13

        [Fact]
        public void T13_SpatialIndex_MatchesBruteForce()
        {
            RoadNetwork n = Kit.New();
            var rng = new Urbe.Sim.Random.Pcg32(1234, 1);
            var ids = new List<EntityId>();
            for (int i = 0; i < 500; i++)
            {
                float x = rng.NextFloat() * 2000f - 1000f, z = rng.NextFloat() * 2000f - 1000f;
                float dx = rng.NextFloat() * 300f - 150f, dz = rng.NextFloat() * 300f - 150f;
                Vec2 p0 = Kit.V(x, z), p3 = Kit.V(x + dx, z + dz);
                var curve = new Bezier3(p0,
                    p0 + Kit.V(rng.NextFloat() * 120f - 60f, rng.NextFloat() * 120f - 60f),
                    p3 + Kit.V(rng.NextFloat() * 120f - 60f, rng.NextFloat() * 120f - 60f), p3);
                EntityId a = n.AddNodeRaw(p0, 0f), b = n.AddNodeRaw(p3, 0f);
                EntityId s = n.AddSegmentRaw(a, b, curve, (ushort)rng.NextInt(6));
                Assert.True(s.IsValid);
                ids.Add(s);
            }
            // Quita 100 para probar tambien el desregistro.
            for (int i = 0; i < 100; i++)
            {
                int k = rng.NextInt(ids.Count);
                Assert.True(n.RemoveSegment(ids[k], new NetworkEdit()));
                ids.RemoveAt(k);
            }
            ids.Sort((p, q) => p.Index.CompareTo(q.Index));
            Assert.Equal(400, n.SegmentCount);

            var buffer = new EntityId[600];
            for (int q = 0; q < 250; q++)
            {
                float cx = rng.NextFloat() * 2200f - 1100f, cz = rng.NextFloat() * 2200f - 1100f;
                float hw = 1f + rng.NextFloat() * 200f, hh = 1f + rng.NextFloat() * 200f;
                var box = new Aabb2(Kit.V(cx - hw, cz - hh), Kit.V(cx + hw, cz + hh));
                var expected = new List<EntityId>();
                foreach (EntityId s in ids) if (BruteTouches(n, s, box)) expected.Add(s);
                int count = n.QuerySegments(box, buffer);
                Assert.Equal(expected.Count, count);
                for (int i = 0; i < count; i++) Assert.Equal(expected[i], buffer[i]);

                // Con un buffer corto se obtienen los de menor indice.
                var small = new EntityId[3];
                int sc = n.QuerySegments(box, small);
                Assert.Equal(Math.Min(3, expected.Count), sc);
                for (int i = 0; i < sc; i++) Assert.Equal(expected[i], small[i]);
            }

            // NearestSegment frente a fuerza bruta.
            for (int q = 0; q < 150; q++)
            {
                Vec2 p = Kit.V(rng.NextFloat() * 2000f - 1000f, rng.NextFloat() * 2000f - 1000f);
                float maxDist = 5f + rng.NextFloat() * 60f;
                EntityId best = EntityId.None;
                float bestD = float.MaxValue, bestT = 0f;
                foreach (EntityId s in ids)
                {
                    Bezier3 c = n.GetSegment(s).Curve;
                    float t = c.ClosestT(p);
                    float d = Vec2.Distance(c.Evaluate(t), p);
                    if (d <= maxDist && d < bestD) { bestD = d; best = s; bestT = t; }
                }
                bool found = n.NearestSegment(p, maxDist, out EntityId seg, out float tt, out float dist);
                Assert.Equal(best.IsValid, found);
                if (found)
                {
                    Assert.Equal(best, seg);
                    Assert.Equal(bestT, tt, 5);
                    Assert.Equal(bestD, dist, 4);
                }
            }
            Kit.AssertInvariants(n);
        }

        private static bool BruteTouches(RoadNetwork n, EntityId s, in Aabb2 box)
        {
            ref readonly Segment sg = ref n.GetSegment(s);
            float h = n.Types.Get(sg.RoadTypeId).TotalWidth * 0.5f;
            Vec2 prev = sg.Curve.Evaluate(0f / 16f);
            for (int i = 1; i <= 16; i++)
            {
                Vec2 cur = sg.Curve.Evaluate(i / 16f);
                var pb = new Aabb2(
                    new Vec2(Math.Min(prev.X, cur.X) - h, Math.Min(prev.Z, cur.Z) - h),
                    new Vec2(Math.Max(prev.X, cur.X) + h, Math.Max(prev.Z, cur.Z) + h));
                if (pb.Overlaps(box)) return true;
                prev = cur;
            }
            return false;
        }

        // ---------------------------------------------------------------- 14

        [Fact]
        public void T14_Hash_AddWithCrossing_Undo_RestoresHash()
        {
            RoadNetwork n = Kit.New();
            Kit.SegAt(n, -100, 0, 100, 0, RoadTypeIds.Avenue4);
            Kit.SegAt(n, -60, 80, 60, 80, RoadTypeIds.Street2);
            Kit.SegAt(n, 150, -50, 150, 50, RoadTypeIds.Rural2);
            ulong h0 = n.ComputeHash();
            int nodes0 = n.NodeCount, segs0 = n.SegmentCount;

            var edit = new NetworkEdit();
            // Cruza la avenida y la calle, y empieza enganchada al extremo de la rural.
            var req = new SegmentRequest(Kit.V(150, 50), Kit.V(110, 120), Kit.V(-30, 120), Kit.V(-30, -60), RoadTypeIds.Street2);
            Assert.Equal(ValidationResult.Ok, n.CanAddSegment(req));
            Assert.True(n.AddSegment(req, edit));
            Assert.NotEqual(h0, n.ComputeHash());
            Assert.True(edit.RemovedSegments.Length >= 1);
            Kit.AssertInvariants(n);

            Kit.Undo(n, edit);
            Assert.Equal(h0, n.ComputeHash());
            Assert.Equal(nodes0, n.NodeCount);
            Assert.Equal(segs0, n.SegmentCount);
            Kit.AssertInvariants(n);
        }

        [Fact]
        public void T14_Hash_IgnoresIdsAndCreationOrder()
        {
            RoadNetwork a = Kit.New();
            Kit.SegAt(a, 0, 0, 100, 0, RoadTypeIds.Street2);
            Kit.SegAt(a, 100, 0, 100, 100, RoadTypeIds.Avenue4);
            RoadNetwork b = Kit.New();
            Kit.SegAt(b, 100, 0, 100, 100, RoadTypeIds.Avenue4);
            Kit.SegAt(b, 0, 0, 100, 0, RoadTypeIds.Street2);
            Assert.Equal(a.ComputeHash(), b.ComputeHash());
            // Cambiar un tipo cambia el hash.
            RoadNetwork c = Kit.New();
            Kit.SegAt(c, 0, 0, 100, 0, RoadTypeIds.Street2);
            Kit.SegAt(c, 100, 0, 100, 100, RoadTypeIds.Avenue6);
            Assert.NotEqual(a.ComputeHash(), c.ComputeHash());
        }

        // ---------------------------------------------------------------- 15

        [Fact]
        public void T15_MoreThanEightSegments_Rejected()
        {
            RoadNetwork n = Kit.New();
            EntityId hub = Kit.Node(n, 0, 0);
            for (int i = 0; i < 8; i++)
            {
                double ang = i * Math.PI / 4;
                EntityId e = Kit.Node(n, (float)(100 * Math.Cos(ang)), (float)(100 * Math.Sin(ang)));
                Assert.True(Kit.Seg(n, hub, e, RoadTypeIds.Street2).IsValid);
            }
            Assert.Equal(8, n.GetNode(hub).SegmentCount);
            EntityId extra = Kit.Node(n, 100, 40);
            ulong h = n.ComputeHash();
            ulong ep = n.Epoch;
            Assert.False(Kit.Seg(n, hub, extra, RoadTypeIds.Street2).IsValid);
            Assert.Equal(ep, n.Epoch);
            Assert.Equal(8, n.SegmentCount);
            // Por alto nivel: termina en el nodo lleno.
            var req = SegmentRequest.Straight(Kit.V(60, 160), Kit.V(1, 1), RoadTypeIds.Street2);
            Assert.Equal(ValidationResult.NodeFull, n.CanAddSegment(req));
            Assert.False(n.AddSegment(req, new NetworkEdit()));
            Assert.Equal(8, n.SegmentCount);
            Assert.Equal(8, n.GetNode(hub).SegmentCount);
            Assert.Equal(h, n.ComputeHash());
            Kit.AssertInvariants(n);
        }

        [Fact]
        public void T15_DuplicateSegment_Rejected()
        {
            RoadNetwork n = Kit.New();
            EntityId a = Kit.Node(n, 0, 0), b = Kit.Node(n, 60, 0);
            Assert.True(Kit.Seg(n, a, b, RoadTypeIds.Street2).IsValid);
            Assert.False(n.AddSegmentRaw(a, b, Bezier3.FromLine(Kit.V(0, 0), Kit.V(60, 0)), RoadTypeIds.Street2).IsValid);
            Assert.False(n.AddSegmentRaw(b, a, Bezier3.FromLine(Kit.V(60, 0), Kit.V(0, 0)), RoadTypeIds.Street2).IsValid);
            Assert.False(n.AddSegmentRaw(a, a, Bezier3.FromLine(Kit.V(0, 0), Kit.V(0, 0)), RoadTypeIds.Street2).IsValid);
            var req = new SegmentRequest(Kit.V(1, 1), Kit.V(20, 30), Kit.V(40, 30), Kit.V(59, 1), RoadTypeIds.Street2);
            Assert.Equal(ValidationResult.DuplicateSegment, n.CanAddSegment(req));
            Assert.False(n.AddSegment(req, new NetworkEdit()));
            Assert.Equal(1, n.SegmentCount);
        }

        private static void Script(RoadNetwork n, List<EntityId> log)
        {
            Kit.SegAt(n, -200, 0, 200, 0, RoadTypeIds.Avenue4);
            var edit = new NetworkEdit();
            Assert.True(n.AddSegment(SegmentRequest.Straight(Kit.V(-100, -100), Kit.V(-100, 100), RoadTypeIds.Street2), edit));
            log.AddRange(edit.CreatedSegments.ToArray());
            Assert.True(n.AddSegment(SegmentRequest.Straight(Kit.V(100, 100), Kit.V(100, -100), RoadTypeIds.Avenue6), edit));
            log.AddRange(edit.CreatedSegments.ToArray());
            var curved = new SegmentRequest(Kit.V(-30, -60), Kit.V(-30, -20), Kit.V(30, 20), Kit.V(30, 60), RoadTypeIds.Rural2);
            Assert.Equal(ValidationResult.Ok, n.CanAddSegment(curved));
            Assert.True(n.AddSegment(curved, edit));
            log.AddRange(edit.CreatedSegments.ToArray());
            Assert.True(n.SplitSegment(log[0], 0.4f, edit));
            log.AddRange(edit.CreatedSegments.ToArray());
            Assert.True(n.RemoveSegment(log[3], edit));
            var last = SegmentRequest.Straight(Kit.V(0, 150), Kit.V(0, 20), RoadTypeIds.OneWay2);
            Assert.Equal(ValidationResult.Ok, n.CanAddSegment(last));
            Assert.True(n.AddSegment(last, edit));
            log.AddRange(edit.CreatedSegments.ToArray());
        }

        [Fact]
        public void T15_SameCallSequence_SameIdsAndHash()
        {
            RoadNetwork a = Kit.New(), b = Kit.New();
            var la = new List<EntityId>();
            var lb = new List<EntityId>();
            Script(a, la);
            Script(b, lb);
            Assert.Equal(la, lb);
            Assert.Equal(a.ComputeHash(), b.ComputeHash());
            Assert.Equal(a.Epoch, b.Epoch);
            Assert.Equal(a.NodeCount, b.NodeCount);
            for (int i = 0; i < a.SegmentIndexBound; i++) Assert.Equal(a.SegmentIdAt(i), b.SegmentIdAt(i));
            for (int i = 0; i < a.NodeIndexBound; i++) Assert.Equal(a.NodeIdAt(i), b.NodeIdAt(i));
            Kit.AssertInvariants(a);
        }
    }
}
