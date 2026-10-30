using System;
using System.Diagnostics;
using Urbe.Sim.Ids;
using Urbe.Sim.Pathfinding;
using Urbe.Sim.Random;
using Urbe.Sim.Roads;
using Xunit;
using Xunit.Abstractions;

namespace Urbe.Sim.Tests.Pathfinding
{
    public class PathBenchmarkTests
    {
        private readonly ITestOutputHelper _out;

        public PathBenchmarkTests(ITestOutputHelper output) { _out = output; }

        /// <summary>
        /// Aceptacion M1-05: 1.000 rutas aleatorias entre segmentos distintos en una cuadricula de 50x50 nodos
        /// (calles de 100 m, street_2), sin cache, en menos de 50 ms en total. Se mide la mediana de 5 tandas de 1.000
        /// rutas tras 50 de calentamiento; el grafo plano y los landmarks se calculan una vez por epoca antes de medir
        /// (su coste se imprime aparte). Conductores con ruido +-10 % (semilla distinta por ruta).
        /// </summary>
        [Fact]
        [Trait("Category", "Bench")]
        public void Grid_50x50_1000_random_routes_without_cache_under_50ms()
        {
            RoadNetwork net = PathKit.NewNet();
            PathKit.BuildGrid(net, 50, 50, 100f, RoadTypeIds.Street2);
            Assert.Equal(2 * 50 * 49, net.SegmentCount);
            EntityId[] segs = PathKit.AliveSegments(net);
            var finder = new PathFinder(net, null, 0);
            var route = new RouteStep[512];

            var sw = Stopwatch.StartNew();
            int edges = finder.GraphEdgeCount; // fuerza la construccion del grafo de la epoca
            sw.Stop();
            double graphMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            finder.BuildLandmarks(); // una vez por epoca, fuera del camino critico
            sw.Stop();
            double lmMs = sw.Elapsed.TotalMilliseconds;
            Assert.True(finder.LandmarksReady);

            var rng = new Pcg32(12345, 1);
            var pairs = new (EntityId, EntityId)[1000];
            for (int i = 0; i < pairs.Length; i++)
            {
                EntityId o = segs[rng.NextInt(segs.Length)], d;
                do { d = segs[rng.NextInt(segs.Length)]; } while (d == o);
                pairs[i] = (o, d);
            }

            for (int i = 0; i < 50; i++) // calentamiento
                finder.FindPath(pairs[i].Item1, pairs[i].Item2, PathOptions.WithSeed((uint)i), route, out _, out _);

            const int Runs = 5;
            var times = new double[Runs];
            long totalLen = 0;
            int searches0 = finder.SearchCount, expanded0 = finder.ExpandedStates;
            for (int r = 0; r < Runs; r++)
            {
                totalLen = 0;
                sw.Restart();
                for (int i = 0; i < pairs.Length; i++)
                {
                    PathOptions opt = PathOptions.WithSeed((uint)i);
                    opt.NoCache = true;
                    Assert.True(finder.FindPath(pairs[i].Item1, pairs[i].Item2, opt, route, out int len, out _));
                    totalLen += len;
                }
                sw.Stop();
                times[r] = sw.Elapsed.TotalMilliseconds;
            }
            double expPerRoute = (finder.ExpandedStates - expanded0) / (double)(finder.SearchCount - searches0);
            Array.Sort(times);
            double median = times[Runs / 2];
            _out.WriteLine("BENCH 1000 rutas 50x50 sin cache: mediana " + median.ToString("F2") + " ms (min " + times[0].ToString("F2")
                + ", max " + times[Runs - 1].ToString("F2") + "); grafo (" + edges + " aristas): "
                + graphMs.ToString("F2") + " ms; landmarks (" + finder.LandmarkCount + "): " + lmMs.ToString("F1")
                + " ms; estados expandidos/ruta: " + expPerRoute.ToString("F0") + "; longitud media: " + (totalLen / 1000.0).ToString("F1"));
            Assert.True(median < 50.0, "1000 rutas tardaron " + median.ToString("F2") + " ms (mediana; limite 50 ms)");
        }
    }
}
