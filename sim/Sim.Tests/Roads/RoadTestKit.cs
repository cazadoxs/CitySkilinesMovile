using System;
using System.Collections.Generic;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Xunit;

namespace Urbe.Sim.Tests.Roads
{
    internal static class Kit
    {
        public static RoadNetwork New(NetworkConfig? cfg = null)
            => new RoadNetwork(RoadTypeCatalog.CreateDefault(), cfg ?? new NetworkConfig());

        public static Vec2 V(float x, float z) => new Vec2(x, z);

        public static EntityId Node(RoadNetwork n, float x, float z, float elev = 0f) => n.AddNodeRaw(V(x, z), elev);

        /// <summary>Segmento recto exacto entre dos nodos.</summary>
        public static EntityId Seg(RoadNetwork n, EntityId a, EntityId b, ushort type)
        {
            Vec2 pa = n.GetNode(a).Position, pb = n.GetNode(b).Position;
            return n.AddSegmentRaw(a, b, Bezier3.FromLine(pa, pb), type);
        }

        /// <summary>Segmento recto entre dos puntos creando los nodos (o reutilizando los exactos).</summary>
        public static EntityId SegAt(RoadNetwork n, float x0, float z0, float x1, float z1, ushort type)
        {
            EntityId a = n.FindNodeAt(V(x0, z0));
            if (!a.IsValid) a = Node(n, x0, z0);
            EntityId b = n.FindNodeAt(V(x1, z1));
            if (!b.IsValid) b = Node(n, x1, z1);
            return Seg(n, a, b, type);
        }

        public static bool Add(RoadNetwork n, float x0, float z0, float x1, float z1, ushort type, NetworkEdit? edit = null)
            => n.AddSegment(SegmentRequest.Straight(V(x0, z0), V(x1, z1), type), edit ?? new NetworkEdit());

        public static EntityId At(RoadNetwork n, float x, float z)
        {
            EntityId id = n.NearestNode(V(x, z), 0.5f);
            Assert.True(id.IsValid, "no hay nodo en (" + x + ", " + z + ")");
            return id;
        }

        public static EntityId OtherNode(RoadNetwork n, EntityId seg, EntityId node)
        {
            ref readonly Segment s = ref n.GetSegment(seg);
            return s.NodeA == node ? s.NodeB : s.NodeA;
        }

        /// <summary>Segmento del nodo cuyo otro extremo esta en (x, z).</summary>
        public static EntityId ArmTo(RoadNetwork n, EntityId node, float x, float z)
        {
            foreach (EntityId s in n.GetNodeSegments(node).ToArray())
            {
                if (Vec2.Distance(n.GetNode(OtherNode(n, s, node)).Position, V(x, z)) < 0.5f) return s;
            }
            throw new InvalidOperationException("sin brazo hacia (" + x + ", " + z + ")");
        }

        public static EntityId[] AllNodes(RoadNetwork n)
        {
            var list = new List<EntityId>();
            for (int i = 0; i < n.NodeIndexBound; i++)
                if (n.NodeIdAt(i).IsValid) list.Add(n.NodeIdAt(i));
            return list.ToArray();
        }

        public static EntityId[] AllSegments(RoadNetwork n)
        {
            var list = new List<EntityId>();
            for (int i = 0; i < n.SegmentIndexBound; i++)
                if (n.SegmentIdAt(i).IsValid) list.Add(n.SegmentIdAt(i));
            return list.ToArray();
        }

        public static int CountKind(RoadNetwork n, JunctionKind kind)
        {
            int c = 0;
            foreach (EntityId id in AllNodes(n)) if (n.GetNode(id).Kind == kind) c++;
            return c;
        }

        /// <summary>Indice del carril dentro de su sentido (0 = derecho).</summary>
        public static int LaneIndex(RoadNetwork n, LaneId lane)
        {
            ref readonly Lane l = ref n.GetLane(lane);
            ref readonly Segment s = ref n.GetSegment(l.Segment);
            return l.Direction == LaneDirection.Forward ? l.Slot : l.Slot - s.LanesForward;
        }

        public static EntityId SegmentOf(RoadNetwork n, LaneId lane) => n.GetLane(lane).Segment;

        public static LaneConnection[] ConnsAt(RoadNetwork n, EntityId node) => n.GetConnectionsAt(node).ToArray();

        public static LaneConnection[] ConnsFrom(RoadNetwork n, LaneId lane) => n.GetConnectionsFrom(lane).ToArray();

        public static int Count(LaneConnection[] conns, TurnType turn)
        {
            int c = 0;
            foreach (LaneConnection k in conns) if (k.Turn == turn) c++;
            return c;
        }

        /// <summary>Inverso de una edicion segun el ADR (RemoveSegment de los creados, AddSegmentRaw de los registros).</summary>
        public static void Undo(RoadNetwork n, NetworkEdit edit)
        {
            EntityId[] created = edit.CreatedSegments.ToArray();
            SegmentRecord[] removed = edit.RemovedSegments.ToArray();
            var scratch = new NetworkEdit();
            for (int i = created.Length - 1; i >= 0; i--)
                if (n.IsSegmentAlive(created[i])) n.RemoveSegment(created[i], scratch);
            foreach (SegmentRecord r in removed)
            {
                EntityId a = n.FindNodeAt(r.PosA);
                if (!a.IsValid) a = n.AddNodeRaw(r.PosA, r.ElevA);
                EntityId b = n.FindNodeAt(r.PosB);
                if (!b.IsValid) b = n.AddNodeRaw(r.PosB, r.ElevB);
                Assert.True(n.AddSegmentRaw(a, b, r.Curve, r.RoadTypeId).IsValid);
            }
        }

        /// <summary>Comprueba los invariantes estructurales de toda la red.</summary>
        public static void AssertInvariants(RoadNetwork n)
        {
            int totalNodeConns = 0, totalLaneConns = 0;
            foreach (EntityId node in AllNodes(n))
            {
                ref readonly Node nd = ref n.GetNode(node);
                var segs = n.GetNodeSegments(node).ToArray();
                Assert.Equal(nd.SegmentCount, segs.Length);
                JunctionKind expected = segs.Length == 0 ? JunctionKind.None : segs.Length == 1 ? JunctionKind.DeadEnd
                    : segs.Length == 2 ? JunctionKind.Continuation : JunctionKind.Junction;
                Assert.Equal(expected, nd.Kind);
                // Adyacencia ordenada CCW desde +X.
                double prev = -1;
                foreach (EntityId s in segs)
                {
                    Assert.True(n.IsSegmentAlive(s));
                    ref readonly Segment sg = ref n.GetSegment(s);
                    Vec2 tan = sg.NodeA == node ? sg.Curve.Tangent(0f) : -sg.Curve.Tangent(1f);
                    double ang = Math.Atan2(tan.Z, tan.X);
                    if (ang < 0) ang += 2 * Math.PI;
                    Assert.True(ang >= prev - 1e-6, "adyacencia no ordenada");
                    prev = ang;
                }
                var conns = n.GetConnectionsAt(node);
                totalNodeConns += conns.Length;
                Assert.True(conns.Length <= RoadNetwork.MaxConnectionsPerNode);
                for (int i = 0; i < conns.Length; i++)
                {
                    Assert.Equal(i, conns[i].LocalIndex);
                    Assert.Equal(node, conns[i].Node);
                    if (i > 0)
                    {
                        bool ordered = conns[i - 1].From.Value < conns[i].From.Value
                            || (conns[i - 1].From.Value == conns[i].From.Value && conns[i - 1].To.Value < conns[i].To.Value);
                        Assert.True(ordered, "conexiones sin ordenar por (From, To)");
                    }
                    Assert.True(n.IsSegmentAlive(n.GetLane(conns[i].From).Segment));
                    Assert.True(n.IsSegmentAlive(n.GetLane(conns[i].To).Segment));
                }
            }
            foreach (EntityId seg in AllSegments(n))
            {
                ref readonly Segment sg = ref n.GetSegment(seg);
                Assert.Equal(sg.LanesForward + sg.LanesBackward, n.GetLaneCount(seg));
                for (int slot = 0; slot < n.GetLaneCount(seg); slot++)
                {
                    LaneId lid = n.GetLane(seg, slot);
                    ref readonly Lane l = ref n.GetLane(lid);
                    Assert.Equal(seg, l.Segment);
                    EntityId endNode = l.Direction == LaneDirection.Forward ? sg.NodeB : sg.NodeA;
                    var conns = n.GetConnectionsFrom(lid);
                    totalLaneConns += conns.Length;
                    foreach (LaneConnection c in conns)
                    {
                        Assert.Equal(lid, c.From);
                        Assert.Equal(endNode, c.Node);
                    }
                }
            }
            Assert.Equal(totalNodeConns, totalLaneConns);
            Assert.True(n.ConnectionStorageHoles * 2 <= Math.Max(1, n.ConnectionStorageUsed) || n.ConnectionStorageHoles == 0);
        }
    }
}
