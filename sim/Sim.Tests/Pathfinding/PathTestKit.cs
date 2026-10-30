using System;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Pathfinding;
using Urbe.Sim.Random;
using Urbe.Sim.Roads;
using Xunit;

namespace Urbe.Sim.Tests.Pathfinding
{
    internal static class PathKit
    {
        public static RoadNetwork NewNet() => new RoadNetwork(RoadTypeCatalog.CreateDefault(), new NetworkConfig());

        public static EntityId Node(RoadNetwork n, float x, float z) => n.AddNodeRaw(new Vec2(x, z), 0f);

        public static EntityId Seg(RoadNetwork n, EntityId a, EntityId b, ushort type)
        {
            Vec2 pa = n.GetNode(a).Position, pb = n.GetNode(b).Position;
            return n.AddSegmentRaw(a, b, Bezier3.FromLine(pa, pb), type);
        }

        /// <summary>Cuadricula de nx * nz nodos con calles de 'step' metros, centrada en el origen del mundo.</summary>
        public static EntityId[] BuildGrid(RoadNetwork n, int nx, int nz, float step, ushort type)
        {
            var nodes = new EntityId[nx * nz];
            float x0 = -(nx - 1) * step * 0.5f, z0 = -(nz - 1) * step * 0.5f;
            for (int j = 0; j < nz; j++)
                for (int i = 0; i < nx; i++)
                    nodes[j * nx + i] = Node(n, x0 + i * step, z0 + j * step);
            for (int j = 0; j < nz; j++)
                for (int i = 0; i < nx; i++)
                {
                    if (i + 1 < nx) Assert.True(Seg(n, nodes[j * nx + i], nodes[j * nx + i + 1], type).IsValid);
                    if (j + 1 < nz) Assert.True(Seg(n, nodes[j * nx + i], nodes[(j + 1) * nx + i], type).IsValid);
                }
            return nodes;
        }

        public static EntityId SegmentBetween(RoadNetwork n, EntityId a, EntityId b)
        {
            foreach (EntityId s in n.GetNodeSegments(a).ToArray())
            {
                ref readonly Segment sg = ref n.GetSegment(s);
                if ((sg.NodeA == a && sg.NodeB == b) || (sg.NodeA == b && sg.NodeB == a)) return s;
            }
            throw new InvalidOperationException("sin segmento entre los nodos");
        }

        public static RoadNetwork RandomNetwork(ulong seed)
        {
            var rng = new Pcg32(seed, 54);
            RoadNetwork n = NewNet();
            int nodeCount = rng.NextInt(8, 26);
            var ids = new EntityId[nodeCount];
            var pos = new Vec2[nodeCount];
            for (int i = 0; i < nodeCount; i++)
            {
                Vec2 p;
                bool ok;
                do
                {
                    p = new Vec2(rng.NextInt(-1000, 1000), rng.NextInt(-1000, 1000));
                    ok = true;
                    for (int k = 0; k < i; k++) if (Vec2.Distance(p, pos[k]) < 30f) ok = false;
                } while (!ok);
                pos[i] = p;
                ids[i] = Node(n, p.X, p.Z);
            }
            ushort[] types = { RoadTypeIds.Street2, RoadTypeIds.OneWay2, RoadTypeIds.Avenue4, RoadTypeIds.Rural2, RoadTypeIds.Highway4 };
            int target = nodeCount * 2;
            for (int k = 0; k < target * 3 && n.SegmentCount < target; k++)
            {
                int a = rng.NextInt(nodeCount), b = rng.NextInt(nodeCount);
                if (a == b) continue;
                Seg(n, ids[a], ids[b], types[rng.NextInt(types.Length)]);
            }
            return n;
        }

        public static float[] RandomCongestion(RoadNetwork n, ulong seed)
        {
            var rng = new Pcg32(seed, 7);
            var cf = new float[n.SegmentIndexBound];
            for (int i = 0; i < cf.Length; i++) cf[i] = 0.2f + 0.8f * rng.NextFloat();
            return cf;
        }

        public static EntityId[] AliveSegments(RoadNetwork n)
        {
            var list = new System.Collections.Generic.List<EntityId>();
            for (int i = 0; i < n.SegmentIndexBound; i++)
                if (n.SegmentIdAt(i).IsValid) list.Add(n.SegmentIdAt(i));
            return list.ToArray();
        }

        // ------------------------------------------------------------------ referencia: Dijkstra de fuerza bruta

        /// <summary>
        /// Dijkstra O(V^2) sobre estados (segmento, sentido) construido directamente de las LaneConnection, con la misma
        /// definicion de coste que el A* pero sin compartir codigo con el. Devuelve +inf si no hay ruta.
        /// </summary>
        public static double BruteForceCost(RoadNetwork n, PathCostConfig cfg, float[]? cong, EntityId origin, TravelDirs od,
            EntityId dest, TravelDirs dd)
        {
            int states = n.SegmentIndexBound * 2;
            var dist = new double[states];
            var done = new bool[states];
            for (int i = 0; i < states; i++) dist[i] = double.PositiveInfinity;

            double Trav(int state)
            {
                ref readonly Segment sg = ref n.GetSegment(n.SegmentIdAt(state >> 1));
                double limit = n.Types.Get(sg.RoadTypeId).SpeedLimit;
                double f = cong != null && (state >> 1) < cong.Length ? Math.Clamp(cong[state >> 1], cfg.MinCongestionFactor, 1f) : 1.0;
                return sg.Length / (limit * f);
            }

            for (int d = 0; d < 2; d++)
                if (((int)od & (1 << d)) != 0) dist[origin.Index * 2 + d] = Trav(origin.Index * 2 + d);

            while (true)
            {
                int u = -1;
                for (int i = 0; i < states; i++)
                    if (!done[i] && !double.IsPositiveInfinity(dist[i]) && (u < 0 || dist[i] < dist[u])) u = i;
                if (u < 0) return double.PositiveInfinity;
                done[u] = true;
                if ((u >> 1) == dest.Index && ((int)dd & (1 << (u & 1))) != 0) return dist[u];
                EntityId segId = n.SegmentIdAt(u >> 1);
                ref readonly Segment sg = ref n.GetSegment(segId);
                LaneDirection want = (u & 1) == 0 ? LaneDirection.Forward : LaneDirection.Backward;
                for (int slot = 0; slot < sg.LanesForward + sg.LanesBackward; slot++)
                {
                    LaneId lane = new LaneId(u >> 1, slot);
                    if (n.GetLane(lane).Direction != want) continue;
                    foreach (LaneConnection c in n.GetConnectionsFrom(lane).ToArray())
                    {
                        ref readonly Lane to = ref n.GetLane(c.To);
                        int v = to.Segment.Index * 2 + (to.Direction == LaneDirection.Forward ? 0 : 1);
                        double pen = c.Turn == TurnType.Right ? cfg.RightTurnPenalty
                            : c.Turn == TurnType.Left ? cfg.LeftTurnPenalty
                            : c.Turn == TurnType.UTurn ? cfg.UTurnPenalty : cfg.StraightPenalty;
                        if (n.GetNode(c.Node).Kind == JunctionKind.Junction) pen += cfg.JunctionPenalty;
                        double nd = dist[u] + pen + Trav(v);
                        if (nd < dist[v]) dist[v] = nd;
                    }
                }
            }
        }

        /// <summary>Hay una LaneConnection que lleva del paso a al paso b.</summary>
        public static bool Connected(RoadNetwork n, RouteStep a, RouteStep b)
        {
            ref readonly Segment sg = ref n.GetSegment(a.Segment);
            LaneDirection want = a.Forward ? LaneDirection.Forward : LaneDirection.Backward;
            for (int slot = 0; slot < sg.LanesForward + sg.LanesBackward; slot++)
            {
                LaneId lane = n.GetLane(a.Segment, slot);
                if (n.GetLane(lane).Direction != want) continue;
                foreach (LaneConnection c in n.GetConnectionsFrom(lane).ToArray())
                {
                    ref readonly Lane to = ref n.GetLane(c.To);
                    if (to.Segment == b.Segment && (to.Direction == LaneDirection.Forward) == b.Forward) return true;
                }
            }
            return false;
        }

        public static PathOptions NoNoise => new PathOptions { NoCache = true };
    }
}
