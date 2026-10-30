using System;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Xunit;

namespace Urbe.Sim.Tests.Roads
{
    /// <summary>M1-07b: retranqueo de los carriles en los nodos Junction (ADR 001, "Cambios durante la implementacion").</summary>
    public class RoadSetbackTests
    {
        private static LaneId LaneEndingAt(RoadNetwork n, EntityId seg, EntityId node)
        {
            ref readonly Segment s = ref n.GetSegment(seg);
            return new LaneId(seg.Index, s.NodeB == node ? 0 : s.LanesForward);
        }

        [Fact]
        public void Junction_LanesStopSetbackMetersFromNode_Continuation_DeadEnd_DoNot()
        {
            RoadNetwork n = Kit.New();
            EntityId c = Kit.Node(n, 0, 0);
            EntityId e = Kit.Node(n, 100, 0), w = Kit.Node(n, -100, 0), no = Kit.Node(n, 0, 100), so = Kit.Node(n, 0, -100);
            EntityId sE = Kit.Seg(n, e, c, RoadTypeIds.Street2), sW = Kit.Seg(n, w, c, RoadTypeIds.Street2);
            Kit.Seg(n, no, c, RoadTypeIds.Street2);
            Kit.Seg(n, so, c, RoadTypeIds.Street2);
            // street_2: TotalWidth 10 -> mitad 5 + 1 = 6 m.
            ref readonly Segment se = ref n.GetSegment(sE);
            Assert.Equal(6f, se.SetbackB, 3);
            Assert.Equal(0f, se.SetbackA, 3);
            for (int slot = 0; slot < 2; slot++)
            {
                ref readonly Lane l = ref n.GetLane(new LaneId(sE.Index, slot));
                Assert.InRange(l.Length, 93.9f, 94.1f);
            }
            // Extremo del carril de entrada: a 6 m del centro; inicio del de salida, tambien.
            LaneId entry = LaneEndingAt(n, sE, c);
            n.SampleLane(entry, n.GetLane(entry).Length, out Vec2 pEnd, out _);
            Assert.InRange(Vec2.Distance(pEnd, Kit.V(0, 0)), 6f, 6.4f);
            n.SampleLane(entry, 0f, out Vec2 pStart, out _);
            Assert.InRange(pStart.X, 99.99f, 100.01f); // s = 0 en el extremo lejano (sin retranqueo)
            LaneId exit = new LaneId(sW.Index, n.GetSegment(sW).NodeB == c ? n.GetSegment(sW).LanesForward : 0);
            n.SampleLane(exit, 0f, out Vec2 pExit, out Vec2 tan);
            Assert.InRange(Vec2.Distance(pExit, Kit.V(0, 0)), 6f, 6.4f);
            Assert.True(tan.X < -0.99f);
            // Conexiones: de extremo a extremo, cruzando el area (>= 6 m las rectas).
            foreach (LaneConnection cn in n.GetConnectionsAt(c))
            {
                Assert.True(Vec2.Distance(cn.Curve.P0, Kit.V(0, 0)) >= 6f - 1e-3f);
                Assert.True(Vec2.Distance(cn.Curve.P3, Kit.V(0, 0)) >= 6f - 1e-3f);
                Assert.True(cn.Length > 6f);
            }
            // Un tramo con nodos extremos Continuation/DeadEnd no se recorta.
            RoadNetwork m = Kit.New();
            EntityId a = Kit.Node(m, 0, 0), b = Kit.Node(m, 50, 0), d = Kit.Node(m, 100, 0);
            EntityId s1 = Kit.Seg(m, a, b, RoadTypeIds.Street2);
            Kit.Seg(m, b, d, RoadTypeIds.Street2);
            Assert.Equal(0f, m.GetSegment(s1).SetbackA);
            Assert.Equal(0f, m.GetSegment(s1).SetbackB);
            Assert.InRange(m.GetLane(new LaneId(s1.Index, 0)).Length, 49.99f, 50.01f);
        }

        [Fact]
        public void SetbackUsesWidestOtherSegment_AndIsCappedOnShortSegments()
        {
            RoadNetwork n = Kit.New();
            EntityId c = Kit.Node(n, 0, 0);
            EntityId sStreet = Kit.Seg(n, Kit.Node(n, 100, 0), c, RoadTypeIds.Street2);
            Kit.Seg(n, Kit.Node(n, -100, 0), c, RoadTypeIds.Avenue6);
            EntityId sShort = Kit.Seg(n, Kit.Node(n, 0, 9), c, RoadTypeIds.Street2);
            // La calle ve la avenida de 6 carriles (TotalWidth 6+2+21=29 -> 14,5 + 1); el brazo corto (9 m) queda acotado a 9/2 - 1,5 = 3 m.
            Assert.Equal(15.5f, n.GetSegment(sStreet).SetbackB, 3);
            Assert.Equal(3f, n.GetSegment(sShort).SetbackB, 3);
            Assert.InRange(n.GetLane(new LaneId(sShort.Index, 0)).Length, 5.99f, 6.01f);
        }

        [Fact]
        public void SplitThenJoin_ContinuationBecomesJunction_RecortaVecinosYLosMarca_HashIntacto()
        {
            RoadNetwork n = Kit.New();
            Assert.True(Kit.Add(n, -100, 0, 100, 0, RoadTypeIds.Street2));
            EntityId main = n.SegmentIdAt(0);
            Assert.Equal(0f, n.GetSegment(main).SetbackA);
            float fullLen = n.GetLane(new LaneId(main.Index, 0)).Length;
            n.ConsumeChanges(out _, out _);
            ulong epoch = n.Epoch;

            // Una calle que termina en el interior: split + T. Las dos mitades pasan a tener el nodo como Junction.
            Assert.True(Kit.Add(n, 0, 100, 0, 0, RoadTypeIds.Street2));
            EntityId center = Kit.At(n, 0, 0);
            Assert.Equal(JunctionKind.Junction, n.GetNode(center).Kind);
            Assert.True(n.Epoch > epoch);
            n.ConsumeChanges(out ReadOnlySpan<EntityId> nodes, out ReadOnlySpan<EntityId> segs);
            ulong hashT = n.ComputeHash();
            int checkedArms = 0;
            foreach (EntityId s in n.GetNodeSegments(center).ToArray())
            {
                ref readonly Segment sg = ref n.GetSegment(s);
                bool atA = sg.NodeA == center;
                Assert.Equal(6f, atA ? sg.SetbackA : sg.SetbackB, 3);
                Assert.Equal(0f, atA ? sg.SetbackB : sg.SetbackA, 3);
                Assert.True(Contains(segs, s), "segmento recortado sin marcar como cambiado");
                checkedArms++;
            }
            Assert.Equal(3, checkedArms);
            Assert.True(Contains(nodes, center));

            // Quitar la calle: el nodo vuelve a Continuation y las mitades recuperan el 100 % de su carril.
            n.ConsumeChanges(out _, out _);
            EntityId stem = Kit.ArmTo(n, center, 0, 100);
            Assert.True(n.RemoveSegment(stem, new NetworkEdit()));
            Assert.Equal(JunctionKind.Continuation, n.GetNode(center).Kind);
            n.ConsumeChanges(out _, out ReadOnlySpan<EntityId> after);
            foreach (EntityId s in n.GetNodeSegments(center).ToArray())
            {
                Assert.Equal(0f, n.GetSegment(s).SetbackA);
                Assert.Equal(0f, n.GetSegment(s).SetbackB);
                Assert.True(Contains(after, s), "segmento ampliado sin marcar como cambiado");
                Assert.InRange(n.GetLane(new LaneId(s.Index, 0)).Length, 99.9f, fullLen + 0.01f);
            }
            // El retranqueo es derivado: no entra en el hash (misma red => mismo hash, con o sin retranqueo en sus segmentos).
            RoadNetwork twin = Kit.New();
            Assert.True(Kit.Add(twin, -100, 0, 100, 0, RoadTypeIds.Street2));
            Assert.True(Kit.Add(twin, 0, 100, 0, 0, RoadTypeIds.Street2));
            Assert.Equal(hashT, twin.ComputeHash());
        }

        [Fact]
        public void SampleLane_IsMonotoneAndConsistentWithLaneLength_OnCurvedJunctionArm()
        {
            RoadNetwork n = Kit.New();
            EntityId c = Kit.Node(n, 0, 0);
            EntityId far = Kit.Node(n, 120, 40);
            var curve = new Bezier3(Kit.V(120, 40), Kit.V(80, 40), Kit.V(40, 10), Kit.V(0, 0));
            EntityId arm = n.AddSegmentRaw(far, c, curve, RoadTypeIds.Street2);
            Assert.True(arm.IsValid);
            Kit.Seg(n, Kit.Node(n, -100, 0), c, RoadTypeIds.Street2);
            Kit.Seg(n, Kit.Node(n, 0, -100), c, RoadTypeIds.Street2);
            ref readonly Segment sg = ref n.GetSegment(arm);
            Assert.True(sg.SetbackB > 5f);
            for (int slot = 0; slot < 2; slot++)
            {
                LaneId lane = new LaneId(arm.Index, slot);
                float len = n.GetLane(lane).Length;
                n.SampleLane(lane, 0f, out Vec2 prev, out _);
                float sum = 0f;
                const int steps = 400;
                for (int k = 1; k <= steps; k++)
                {
                    n.SampleLane(lane, len * k / steps, out Vec2 p, out _);
                    sum += Vec2.Distance(prev, p);
                    prev = p;
                }
                // Longitud del carril = longitud real del tramo muestreado.
                Assert.InRange(sum, len * 0.995f, len * 1.005f);
                n.SampleLane(lane, len + 50f, out Vec2 endP, out _);
                n.SampleLane(lane, len, out Vec2 endQ, out _);
                Assert.True(Vec2.Distance(endP, endQ) < 1e-4f);
            }
            // El carril de entrada (Forward, hacia B=centro) empieza en el extremo lejano y acaba en el retranqueo (a >= 6 m del centro).
            LaneId fwd = new LaneId(arm.Index, 0);
            n.SampleLane(fwd, n.GetLane(fwd).Length, out Vec2 pe, out _);
            Assert.InRange(Vec2.Distance(pe, Kit.V(0, 0)), 5.5f, 7.5f);
            // El de salida (Backward) empieza en el retranqueo y acaba en el extremo lejano.
            LaneId bwd = new LaneId(arm.Index, 1);
            n.SampleLane(bwd, 0f, out Vec2 ps, out _);
            Assert.InRange(Vec2.Distance(ps, Kit.V(0, 0)), 5.5f, 7.5f);
            n.SampleLane(bwd, n.GetLane(bwd).Length, out Vec2 pf, out _);
            Assert.InRange(Vec2.Distance(pf, Kit.V(120, 40)), 0f, 1.6f);
        }

        private static bool Contains(ReadOnlySpan<EntityId> span, EntityId id)
        {
            for (int i = 0; i < span.Length; i++) if (span[i] == id) return true;
            return false;
        }
    }
}
