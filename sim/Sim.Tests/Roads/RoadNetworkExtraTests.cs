using System;
using System.Collections.Generic;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Xunit;

namespace Urbe.Sim.Tests.Roads
{
    // Pruebas adicionales a las 15 obligatorias: limites, validaciones, geometria de carriles y estres.
    public class RoadNetworkExtraTests
    {
        private static EntityId Hub(RoadNetwork n, ushort type, int spokes, double firstAngle = 0.0)
        {
            EntityId hub = Kit.Node(n, 0, 0);
            for (int i = 0; i < spokes; i++)
            {
                double ang = firstAngle + i * Math.PI / 4;
                EntityId e = Kit.Node(n, (float)(100 * Math.Cos(ang)), (float)(100 * Math.Sin(ang)));
                Assert.True(Kit.Seg(n, hub, e, type).IsValid, "radio " + i);
            }
            return hub;
        }

        [Fact]
        public void ConnectionLimit_RawAddRejectedWithoutChanges()
        {
            RoadNetwork n = Kit.New();
            EntityId hub = Hub(n, RoadTypeIds.Avenue6, 7);
            Assert.True(n.GetConnectionsAt(hub).Length <= RoadNetwork.MaxConnectionsPerNode);
            // 8.o radio de 3+3 carriles: 8 entradas x 9 conexiones = 72 > 64.
            EntityId e = Kit.Node(n, (float)(100 * Math.Cos(7 * Math.PI / 4)), (float)(100 * Math.Sin(7 * Math.PI / 4)));
            ulong h2 = n.ComputeHash();
            ulong ep2 = n.Epoch;
            var before = Kit.ConnsAt(n, hub);
            Assert.False(Kit.Seg(n, hub, e, RoadTypeIds.Avenue6).IsValid);
            Assert.Equal(h2, n.ComputeHash());
            Assert.Equal(ep2, n.Epoch);
            Assert.Equal(7, n.GetNode(hub).SegmentCount);
            Assert.Equal(before.Length, n.GetConnectionsAt(hub).Length);
            Kit.AssertInvariants(n);
        }

        [Fact]
        public void ConnectionLimit_HighLevelAddRollsBackSplits()
        {
            RoadNetwork n = Kit.New();
            // 7 radios de 3+3 carriles salvo el de 90 grados, que se intenta por alto nivel.
            EntityId hub = Kit.Node(n, 0, 0);
            for (int i = 0; i < 8; i++)
            {
                if (i == 2) continue;
                double ang = i * Math.PI / 4;
                EntityId e = Kit.Node(n, (float)(100 * Math.Cos(ang)), (float)(100 * Math.Sin(ang)));
                Assert.True(Kit.Seg(n, hub, e, RoadTypeIds.Avenue6).IsValid);
            }
            Kit.SegAt(n, -30, 60, 30, 60, RoadTypeIds.Street2); // travesia que el nuevo radio cortaria
            ulong h0 = n.ComputeHash();
            int nodes0 = n.NodeCount, segs0 = n.SegmentCount;

            var req = SegmentRequest.Straight(Kit.V(0, 150), Kit.V(0, 0), RoadTypeIds.Avenue6);
            Assert.Equal(ValidationResult.Ok, n.CanAddSegment(req)); // el limite de conexiones solo se ve al aplicar
            var edit = new NetworkEdit();
            Assert.False(n.AddSegment(req, edit));
            Assert.Equal(h0, n.ComputeHash());
            Assert.Equal(nodes0, n.NodeCount);
            Assert.Equal(segs0, n.SegmentCount);
            Assert.Equal(0, edit.CreatedSegments.Length);
            Assert.Equal(0, edit.RemovedSegments.Length);
            Kit.AssertInvariants(n);

            // Con un radio menos el mismo trazado si cabe.
            Assert.True(n.RemoveSegment(Kit.ArmTo(n, Kit.At(n, 0, 0), 100, 0), edit));
            Assert.True(n.AddSegment(req, edit));
            Assert.Equal(7, n.GetNode(Kit.At(n, 0, 0)).SegmentCount);
            Kit.AssertInvariants(n);
        }

        [Fact]
        public void SnapToSegment_NearEndpoint_UsesTheNodeInstead()
        {
            RoadNetwork n = Kit.New();
            Kit.SegAt(n, 0, 0, 100, 0, RoadTypeIds.Street2);
            Assert.True(Kit.Add(n, 6, 80, 6, 2, RoadTypeIds.Street2)); // cae a 6 m del nodo (0,0): quedaria un trozo < 8 m
            Assert.Equal(2, n.SegmentCount); // el segmento no se divide
            Assert.Equal(3, n.NodeCount);
            Assert.Equal(2, n.GetNode(Kit.At(n, 0, 0)).SegmentCount);
            Kit.AssertInvariants(n);
        }

        [Fact]
        public void Elevation_FiltersCrossings()
        {
            RoadNetwork n = Kit.New();
            Kit.SegAt(n, -50, 0, 50, 0, RoadTypeIds.Street2);
            var bridge = new SegmentRequest(Kit.V(0, -50), Kit.V(0, -17), Kit.V(0, 17), Kit.V(0, 50), RoadTypeIds.Street2, 10f, 10f);
            Assert.True(n.AddSegment(bridge, new NetworkEdit()));
            Assert.Equal(2, n.SegmentCount); // sin cruce: pasa por encima
            Assert.Equal(4, n.NodeCount);

            RoadNetwork m = Kit.New();
            Kit.SegAt(m, -50, 0, 50, 0, RoadTypeIds.Street2);
            var ramp = new SegmentRequest(Kit.V(0, -50), Kit.V(0, -17), Kit.V(0, 17), Kit.V(0, 50), RoadTypeIds.Street2, 1f, 1f);
            Assert.True(m.AddSegment(ramp, new NetworkEdit()));
            Assert.Equal(4, m.SegmentCount); // diferencia < 2 m: cruce
        }

        [Fact]
        public void SplitNode_TakesInterpolatedElevation()
        {
            RoadNetwork n = Kit.New();
            EntityId a = Kit.Node(n, 0, 0, 0f), b = Kit.Node(n, 100, 0, 10f);
            EntityId s = Kit.Seg(n, a, b, RoadTypeIds.Street2);
            var edit = new NetworkEdit();
            Assert.True(n.SplitSegment(s, 0.25f, edit));
            Assert.Equal(2.5f, n.GetNode(edit.CreatedNodes[0]).Elevation, 3);
        }

        [Fact]
        public void Validation_Results()
        {
            RoadNetwork n = Kit.New();
            Kit.SegAt(n, -100, 0, 100, 0, RoadTypeIds.Street2);
            ushort st = RoadTypeIds.Street2;
            Assert.Equal(ValidationResult.UnknownRoadType, n.CanAddSegment(SegmentRequest.Straight(Kit.V(0, 20), Kit.V(0, 80), 99)));
            Assert.Equal(ValidationResult.OutOfBounds, n.CanAddSegment(SegmentRequest.Straight(Kit.V(0, 20), Kit.V(0, 9000), st)));
            Assert.Equal(ValidationResult.TooShort, n.CanAddSegment(SegmentRequest.Straight(Kit.V(0, 20), Kit.V(0, 26), st)));
            Assert.Equal(ValidationResult.TooLong, n.CanAddSegment(SegmentRequest.Straight(Kit.V(0, 20), Kit.V(0, 260), st)));
            Assert.Equal(ValidationResult.Ok, n.CanAddSegment(SegmentRequest.Straight(Kit.V(0, 20), Kit.V(0, 200), st)));
            // Ambos extremos en el mismo nodo.
            Assert.Equal(ValidationResult.SameNode, n.CanAddSegment(new SegmentRequest(Kit.V(100, 1), Kit.V(150, 80), Kit.V(60, 80), Kit.V(100, -1), st)));
            // Cruce a 5 m del inicio libre: el trozo nuevo seria < 8 m.
            Assert.Equal(ValidationResult.TooShort, n.CanAddSegment(SegmentRequest.Straight(Kit.V(0, -5), Kit.V(0, 80), st)));
            // Angulos: cruce muy tendido y salida de un nodo casi paralela a otra via.
            Assert.Equal(ValidationResult.AngleTooSharp, n.CanAddSegment(SegmentRequest.Straight(Kit.V(-80, -5), Kit.V(80, 5), st)));
            Assert.Equal(ValidationResult.AngleTooSharp, n.CanAddSegment(SegmentRequest.Straight(Kit.V(100, 0), Kit.V(0, 20), st)));
            Assert.Equal(ValidationResult.Ok, n.CanAddSegment(SegmentRequest.Straight(Kit.V(100, 0), Kit.V(60, 60), st)));
            Assert.False(n.AddSegment(SegmentRequest.Straight(Kit.V(0, 20), Kit.V(0, 260), st), new NetworkEdit()));
            Assert.Equal(1, n.SegmentCount);
        }

        [Fact]
        public void Validation_TooManyCrossings()
        {
            RoadNetwork n = Kit.New();
            for (int i = 0; i < 39; i++)
            {
                float x = -190f + i * 10f;
                Kit.SegAt(n, x, -30, x, 30, RoadTypeIds.Street2);
            }
            Assert.Equal(ValidationResult.TooManyCrossings,
                n.CanAddSegment(SegmentRequest.Straight(Kit.V(-200, 0), Kit.V(200, 0), RoadTypeIds.Street2)));
            // Con 30 verticales (por debajo del limite de 32) sigue valiendo.
            RoadNetwork m = Kit.New();
            for (int i = 0; i < 19; i++)
            {
                float x = -90f + i * 10f;
                Kit.SegAt(m, x, -30, x, 30, RoadTypeIds.Street2);
            }
            Assert.Equal(ValidationResult.Ok,
                m.CanAddSegment(SegmentRequest.Straight(Kit.V(-100, 0), Kit.V(100, 0), RoadTypeIds.Street2)));
        }

        [Fact]
        public void RoadType_RejectsInvalidLaneCount()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RoadType(0, "x", 0, 0, 3f, 0f, 1f, 10f, 0.1f, true));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RoadType(0, "x", 5, 4, 3f, 0f, 1f, 10f, 0.1f, true));
            Assert.Throws<ArgumentException>(() => new RoadTypeCatalog(new[] { new RoadType(3, "x", 1, 1, 3f, 0f, 1f, 10f, 0.1f, true) }));
        }

        [Fact]
        public void CurvedSegment_InnerLaneIsShorterThanOuter()
        {
            RoadNetwork n = Kit.New();
            EntityId a = Kit.Node(n, 0, 0), b = Kit.Node(n, 100, -100);
            // Sale hacia +X y llega hacia -Z: giro a la derecha, el carril derecho es el interior.
            var curve = new Bezier3(Kit.V(0, 0), Kit.V(55, 0), Kit.V(100, -45), Kit.V(100, -100));
            EntityId s = n.AddSegmentRaw(a, b, curve, RoadTypeIds.Street2);
            float len = n.GetSegment(s).Length;
            float inner = n.GetLane(n.GetLane(s, 0)).Length;
            float outer = n.GetLane(n.GetLane(s, 1)).Length;
            Assert.True(inner < len - 1f);
            Assert.True(outer > len + 1f);
            // Final del carril derecho: extremo B desplazado a la derecha de la tangente de llegada (-Z -> derecha = -X).
            n.SampleLane(n.GetLane(s, 0), inner, out Vec2 p, out Vec2 t);
            Assert.True(p.ApproxEquals(Kit.V(100f - 1.5f, -100f), 0.05f));
            Assert.True(t.ApproxEquals(Kit.V(0, -1), 1e-3f));
            // El carril contrario arranca en B por su lado (+X) y avanza hacia A.
            n.SampleLane(n.GetLane(s, 1), 0f, out p, out t);
            Assert.True(p.ApproxEquals(Kit.V(100f + 1.5f, -100f), 0.05f));
            Assert.True(t.ApproxEquals(Kit.V(0, 1), 1e-3f));
            Kit.AssertInvariants(n);
        }

        [Fact]
        public void Adjacency_SortedCounterClockwise_RegardlessOfCreationOrder()
        {
            RoadNetwork n = Kit.New();
            EntityId hub = Kit.Node(n, 0, 0);
            int[] order = { 5, 2, 7, 0, 3, 6, 1, 4 };
            foreach (int i in order)
            {
                double ang = i * Math.PI / 4;
                EntityId e = Kit.Node(n, (float)(100 * Math.Cos(ang)), (float)(100 * Math.Sin(ang)));
                Assert.True(Kit.Seg(n, hub, e, RoadTypeIds.Street2).IsValid);
            }
            var segs = n.GetNodeSegments(hub).ToArray();
            for (int i = 0; i < 8; i++)
            {
                Vec2 far = n.GetNode(Kit.OtherNode(n, segs[i], hub)).Position;
                double ang = Math.Atan2(far.Z, far.X);
                if (ang < -1e-6) ang += 2 * Math.PI;
                Assert.Equal(i * Math.PI / 4, ang, 4);
            }
            Kit.AssertInvariants(n);
        }

        [Fact]
        public void NearestNodeAndSegment_Basics()
        {
            RoadNetwork n = Kit.New();
            EntityId a = Kit.Node(n, 0, 0), b = Kit.Node(n, 100, 0);
            EntityId s = Kit.Seg(n, a, b, RoadTypeIds.Street2);
            Assert.Equal(a, n.NearestNode(Kit.V(1, 1), 3f));
            Assert.False(n.NearestNode(Kit.V(10, 10), 3f).IsValid);
            Assert.Equal(b, n.NearestNode(Kit.V(60, 0), 50f));
            Assert.True(n.NearestSegment(Kit.V(40, 5), 6f, out EntityId seg, out float t, out float d));
            Assert.Equal(s, seg);
            Assert.Equal(0.4f, t, 3);
            Assert.Equal(5f, d, 3);
            Assert.False(n.NearestSegment(Kit.V(40, 50), 6f, out _, out _, out _));
            Assert.Equal(0, n.QuerySegments(new Aabb2(Kit.V(500, 500), Kit.V(600, 600)), new EntityId[4]));
            Assert.Equal(1, n.QuerySegments(new Aabb2(Kit.V(40, -20), Kit.V(60, -4)), new EntityId[4])); // ancho de calzada
            Assert.Equal(0, n.QuerySegments(new Aabb2(Kit.V(40, -20), Kit.V(60, -6)), new EntityId[4]));
            // Fuera del mundo: se acota a las celdas del borde sin fallar.
            Assert.Equal(0, n.QuerySegments(new Aabb2(Kit.V(-99999, -99999), Kit.V(-9000, -9000)), new EntityId[4]));
        }

        [Fact]
        public void OneWay_SwappedNodes_ReversesTheDirection()
        {
            RoadNetwork n = Kit.New();
            EntityId a = Kit.Node(n, 0, 0), b = Kit.Node(n, 100, 0);
            EntityId fwd = Kit.Seg(n, a, b, RoadTypeIds.OneWay2);
            n.SampleLane(n.GetLane(fwd, 0), 0f, out Vec2 p, out Vec2 t);
            Assert.True(t.ApproxEquals(Kit.V(1, 0), 1e-4f));

            RoadNetwork m = Kit.New();
            EntityId a2 = Kit.Node(m, 0, 0), b2 = Kit.Node(m, 100, 0);
            EntityId rev = m.AddSegmentRaw(b2, a2, Bezier3.FromLine(Kit.V(100, 0), Kit.V(0, 0)), RoadTypeIds.OneWay2);
            m.SampleLane(m.GetLane(rev, 0), 0f, out p, out t);
            Assert.True(t.ApproxEquals(Kit.V(-1, 0), 1e-4f));
            Assert.True(p.ApproxEquals(Kit.V(100f, 1.5f), 1e-3f)); // el carril derecho al ir hacia -X queda en +Z
        }

        [Fact]
        public void Growth_ManySegments_KeepsEverythingConsistent()
        {
            var cfg = new NetworkConfig { InitialNodeCapacity = 4, InitialSegmentCapacity = 4 };
            RoadNetwork n = Kit.New(cfg);
            int added = 0;
            for (int ix = 0; ix < 12; ix++)
            {
                for (int iz = 0; iz < 12; iz++)
                {
                    float x = ix * 50f, z = iz * 50f;
                    if (ix < 11 && Kit.SegAt(n, x, z, x + 50f, z, RoadTypeIds.Street2).IsValid) added++;
                    if (iz < 11 && Kit.SegAt(n, x, z, x, z + 50f, RoadTypeIds.Street2).IsValid) added++;
                }
            }
            Assert.Equal(added, n.SegmentCount);
            Assert.Equal(12 * 12, n.NodeCount);
            Kit.AssertInvariants(n);
            // Rejilla completa: los nodos interiores son cruces de 4 brazos con 12 conexiones.
            Assert.Equal(12, n.GetConnectionsAt(Kit.At(n, 100, 100)).Length);
            Assert.Equal(4, Kit.CountKind(n, JunctionKind.Continuation)); // las 4 esquinas
        }

        private static ulong Churn(int seed, bool verifyUndo, out int compactions, out int removals)
        {
            RoadNetwork n = Kit.New();
            var rng = new Urbe.Sim.Random.Pcg32((ulong)seed, 7);
            var edit = new NetworkEdit();
            compactions = 0;
            removals = 0;
            int prevUsed = 0;
            for (int op = 0; op < 350; op++)
            {
                bool remove = n.SegmentCount > 10 && rng.NextInt(100) < 35;
                if (remove)
                {
                    EntityId[] all = Kit.AllSegments(n);
                    Assert.True(n.RemoveSegment(all[rng.NextInt(all.Length)], edit));
                    removals++;
                }
                else
                {
                    float x = rng.NextFloat() * 500f - 250f, z = rng.NextFloat() * 500f - 250f;
                    double ang = rng.NextDouble() * Math.PI * 2;
                    float len = 15f + rng.NextFloat() * 120f;
                    float x1 = x + (float)(Math.Cos(ang) * len), z1 = z + (float)(Math.Sin(ang) * len);
                    ushort type = (ushort)rng.NextInt(6);
                    var req = SegmentRequest.Straight(Kit.V(x, z), Kit.V(x1, z1), type);
                    ulong h0 = n.ComputeHash();
                    bool ok = n.AddSegment(req, edit);
                    if (ok && verifyUndo && rng.NextInt(100) < 30)
                    {
                        Kit.Undo(n, edit);
                        Assert.Equal(h0, n.ComputeHash());
                    }
                    else if (!ok)
                    {
                        Assert.Equal(h0, n.ComputeHash());
                    }
                }
                if (n.ConnectionStorageUsed < prevUsed) compactions++;
                prevUsed = n.ConnectionStorageUsed;
                Kit.AssertInvariants(n);
            }
            return n.ComputeHash();
        }

        [Fact]
        public void Churn_RandomEdits_InvariantsUndoAndDeterminism()
        {
            ulong h1 = Churn(99, true, out int compactions, out int removals);
            ulong h2 = Churn(99, true, out _, out _);
            Assert.Equal(h1, h2);
            Assert.True(removals > 20);
            Assert.True(compactions > 0, "la compactacion de conexiones nunca se activo");
            Assert.NotEqual(h1, Churn(100, true, out _, out _));
        }
    }
}
