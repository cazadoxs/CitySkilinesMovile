using System;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Junctions;
using Urbe.Sim.Pathfinding;
using Urbe.Sim.Random;
using Urbe.Sim.Roads;
using Urbe.Sim.Traffic;
using Xunit;

namespace Urbe.Sim.Tests.Pathfinding
{
    // M1-16, punto 3 de la revision M1-13: landmarks con presupuesto por tick y GraphEpoch.
    public class LandmarkBudgetTests
    {
        private static TrafficSimulation MakeSim(int budget, out RoadNetwork net, out EntityId[] nodes)
        {
            net = PathKit.NewNet();
            nodes = PathKit.BuildGrid(net, 12, 12, 100f, RoadTypeIds.Street2);
            var opt = new TrafficSimulationOptions { LandmarkBudgetPerTick = budget };
            return new TrafficSimulation(net, 7, opt);
        }

        [Fact]
        public void GraphEpoch_ignores_control_changes_but_not_topology()
        {
            RoadNetwork net = PathKit.NewNet();
            EntityId[] n = PathKit.BuildGrid(net, 3, 3, 100f, RoadTypeIds.Street2);
            ulong g0 = net.GraphEpoch, e0 = net.Epoch;
            Assert.True(net.SetNodeControl(n[4], JunctionControlIds.Stop));
            Assert.Equal(g0, net.GraphEpoch);
            Assert.True(net.Epoch > e0);
            PathKit.Node(net, 3000, 3000);
            Assert.True(net.GraphEpoch > g0);
        }

        [Fact]
        public void SetNodeControl_does_not_rebuild_landmarks_nor_clear_cache()
        {
            TrafficSimulation sim = MakeSim(1000, out RoadNetwork net, out EntityId[] n);
            Assert.True(sim.Paths.LandmarksReady);
            sim.Step();
            net.SetNodeControl(n[5 * 12 + 5], JunctionControlIds.Signal);
            sim.Step();
            Assert.True(sim.Paths.LandmarksReady);
            Assert.False(sim.Paths.LandmarksBuilding);
        }

        [Fact]
        public void Topology_change_does_not_build_synchronously_and_finishes_at_a_deterministic_tick()
        {
            int Run()
            {
                TrafficSimulation sim = MakeSim(500, out RoadNetwork net, out _);
                PathKit.Node(net, 3000, 3000); // cambio topologico
                sim.Step();
                Assert.False(sim.Paths.LandmarksReady, "no debe reconstruirse de golpe dentro de Step");
                Assert.True(sim.Paths.LandmarksBuilding);
                int ticks = 1;
                while (sim.Paths.LandmarksBuilding && ticks < 100000) { sim.Step(); ticks++; }
                Assert.True(sim.Paths.LandmarksReady);
                return ticks;
            }
            int a = Run(), b = Run();
            Assert.Equal(a, b);
            Assert.True(a > 5, "con presupuesto 500 debe repartirse en varios ticks (" + a + ")");
        }

        [Fact]
        public void Incremental_build_gives_same_routes_as_full_build_even_with_searches_in_between()
        {
            RoadNetwork net = PathKit.NewNet();
            PathKit.BuildGrid(net, 12, 12, 100f, RoadTypeIds.Street2);
            EntityId[] segs = PathKit.AliveSegments(net);
            var full = new PathFinder(net, null, 0);
            var inc = new PathFinder(net, null, 0);
            full.BuildLandmarks();
            inc.StartLandmarkBuild();
            var ra = new RouteStep[256];
            var rb = new RouteStep[256];
            var rng = new Pcg32(9, 9);
            int guard = 0;
            while (!inc.AdvanceLandmarks(300)) // con busquedas intercaladas (no deben pisar el monticulo del Dijkstra a medias)
            {
                EntityId o = segs[rng.NextInt(segs.Length)], d = segs[rng.NextInt(segs.Length)];
                Assert.True(inc.FindPath(o, d, PathKit.NoNoise, rb, out _, out _));
                Assert.True(++guard < 100000);
            }
            Assert.True(full.LandmarksReady && inc.LandmarksReady);
            for (int i = 0; i < 200; i++)
            {
                EntityId o = segs[rng.NextInt(segs.Length)], d = segs[rng.NextInt(segs.Length)];
                PathOptions opt = PathOptions.WithSeed((uint)i);
                opt.NoCache = true;
                bool okA = full.FindPath(o, d, opt, ra, out int la, out float ca);
                bool okB = inc.FindPath(o, d, opt, rb, out int lb, out float cb);
                Assert.Equal(okA, okB);
                Assert.Equal(la, lb);
                Assert.Equal(ca, cb);
                for (int k = 0; k < la; k++) Assert.Equal(ra[k], rb[k]);
            }
        }

        [Fact]
        public void Graph_change_during_build_restarts_it()
        {
            RoadNetwork net = PathKit.NewNet();
            PathKit.BuildGrid(net, 8, 8, 100f, RoadTypeIds.Street2);
            var f = new PathFinder(net, null, 0);
            f.StartLandmarkBuild();
            Assert.False(f.AdvanceLandmarks(200));
            PathKit.Node(net, 3000, 3000);
            Assert.False(f.LandmarksReady);
            int guard = 0;
            while (!f.AdvanceLandmarks(200)) Assert.True(++guard < 100000);
            Assert.True(f.LandmarksReady);
        }
    }
}
