using System;
using System.Collections.Generic;
using Urbe.Sim.Ids;
using Urbe.Sim.Pathfinding;
using Urbe.Sim.Roads;
using Xunit;

namespace Urbe.Sim.Tests.Pathfinding
{
    public class PathFinderTests
    {
        private static readonly float Street = 40f / 3.6f;

        [Fact]
        public void Optimal_vs_bruteforce_dijkstra_in_50_random_networks()
        {
            int compared = 0, reachable = 0;
            for (ulong seed = 1; seed <= 50; seed++)
            {
                RoadNetwork net = PathKit.RandomNetwork(seed);
                var cfg = new PathCostConfig();
                var finder = new PathFinder(net, cfg, 0);
                float[] cong = PathKit.RandomCongestion(net, seed);
                finder.Congestion = (seed % 2 == 0) ? cong : null;
                if (seed % 3 != 0)
                {
                    finder.LandmarkCount = 1 + (int)(seed % 8);
                    finder.BuildLandmarks();
                    Assert.True(finder.LandmarksReady);
                }
                float[]? used = finder.Congestion;
                EntityId[] segs = PathKit.AliveSegments(net);
                Assert.True(segs.Length >= 4);
                var rng = new Urbe.Sim.Random.Pcg32(seed * 31, 3);
                var route = new RouteStep[512];
                for (int k = 0; k < 20; k++)
                {
                    EntityId o = segs[rng.NextInt(segs.Length)], d = segs[rng.NextInt(segs.Length)];
                    TravelDirs od = (TravelDirs)rng.NextInt(1, 4), dd = (TravelDirs)rng.NextInt(1, 4);
                    double expected = PathKit.BruteForceCost(net, cfg, used, o, od, d, dd);
                    bool ok = finder.FindPath(o, od, d, dd, PathKit.NoNoise, route, out int len, out float cost);
                    compared++;
                    if (double.IsPositiveInfinity(expected))
                    {
                        Assert.False(ok);
                        Assert.Equal(0, len);
                        continue;
                    }
                    reachable++;
                    Assert.True(ok, "seed " + seed + " par " + k + ": A* no encontro ruta");
                    Assert.True(Math.Abs(cost - expected) <= 1e-3 * Math.Max(1.0, expected),
                        "seed " + seed + " par " + k + ": A* " + cost + " vs Dijkstra " + expected);
                    // La ruta es valida: empieza/acaba donde toca y cada paso conecta con el siguiente.
                    Assert.Equal(o, route[0].Segment);
                    Assert.Equal(d, route[len - 1].Segment);
                    for (int i = 0; i + 1 < len; i++) Assert.True(PathKit.Connected(net, route[i], route[i + 1]));
                }
            }
            Assert.Equal(1000, compared);
            Assert.True(reachable > 200, "las redes aleatorias deberian tener muchas parejas conectadas: " + reachable);
        }

        [Fact]
        public void Straight_line_cost_is_length_over_speed_plus_junction_penalties()
        {
            RoadNetwork net = PathKit.NewNet();
            EntityId[] n = PathKit.BuildGrid(net, 4, 2, 100f, RoadTypeIds.Street2);
            EntityId s0 = PathKit.SegmentBetween(net, n[0], n[1]);
            EntityId s2 = PathKit.SegmentBetween(net, n[2], n[3]);
            var finder = new PathFinder(net);
            var route = new RouteStep[16];
            Assert.True(finder.FindPath(new RouteStep(s0, true), new RouteStep(s2, true), PathKit.NoNoise, route, out int len, out float cost));
            Assert.Equal(3, len);
            // 3 segmentos de 100 m a 40 km/h + 2 cruces (los nodos intermedios tienen 3 segmentos: Junction).
            Assert.Equal(3 * 100f / Street + 2 * 2f, cost, 2);
            Assert.All(route.AsSpan(0, 3).ToArray(), s => Assert.True(s.Forward));
        }

        [Fact]
        public void Respects_one_way_streets()
        {
            RoadNetwork net = PathKit.NewNet();
            EntityId n0 = PathKit.Node(net, 0, 0), n1 = PathKit.Node(net, 100, 0), n2 = PathKit.Node(net, 200, 0);
            EntityId a = PathKit.Seg(net, n0, n1, RoadTypeIds.Street2);
            EntityId b = PathKit.Seg(net, n2, n1, RoadTypeIds.OneWay2); // el trafico fluye de n2 a n1
            var finder = new PathFinder(net);
            var route = new RouteStep[8];

            // a -> b: habria que entrar a b desde n1, contra el sentido.
            Assert.False(finder.FindPath(a, b, PathKit.NoNoise, route, out int len, out _));
            Assert.Equal(0, len);

            // b -> a: permitido.
            Assert.True(finder.FindPath(b, a, PathKit.NoNoise, route, out len, out _));
            Assert.Equal(2, len);
            Assert.Equal(new RouteStep(b, true), route[0]);
            Assert.Equal(new RouteStep(a, false), route[1]);
        }

        [Fact]
        public void Detour_goes_around_a_wrong_way_one_way_street()
        {
            // Cuadrado n0-n1-n2-n3 cuyo lado n0-n1 es de sentido unico n1->n0: de n0 a n1 hay que rodear.
            RoadNetwork net = PathKit.NewNet();
            EntityId west = PathKit.Node(net, -100, 0), east = PathKit.Node(net, 200, 0);
            EntityId n0 = PathKit.Node(net, 0, 0), n1 = PathKit.Node(net, 100, 0), n2 = PathKit.Node(net, 100, 100), n3 = PathKit.Node(net, 0, 100);
            EntityId head = PathKit.Seg(net, west, n0, RoadTypeIds.Street2);
            EntityId tail = PathKit.Seg(net, n1, east, RoadTypeIds.Street2);
            EntityId direct = PathKit.Seg(net, n1, n0, RoadTypeIds.OneWay2);
            EntityId e12 = PathKit.Seg(net, n1, n2, RoadTypeIds.Street2);
            EntityId e23 = PathKit.Seg(net, n2, n3, RoadTypeIds.Street2);
            EntityId e30 = PathKit.Seg(net, n3, n0, RoadTypeIds.Street2);
            var finder = new PathFinder(net);
            var route = new RouteStep[8];
            Assert.True(finder.FindPath(new RouteStep(head, true), new RouteStep(tail, true), PathKit.NoNoise, route, out int len, out _));
            Assert.Equal(5, len);
            for (int i = 0; i < len; i++) Assert.NotEqual(direct, route[i].Segment);
            Assert.Equal(new RouteStep(e30, false), route[1]);
            Assert.Equal(new RouteStep(e23, false), route[2]);
            Assert.Equal(new RouteStep(e12, false), route[3]);
            // En sentido contrario si se puede usar el sentido unico.
            Assert.True(finder.FindPath(new RouteStep(tail, false), new RouteStep(head, false), PathKit.NoNoise, route, out len, out _));
            Assert.Equal(3, len);
            Assert.Equal(direct, route[1].Segment);
        }

        [Fact]
        public void U_turn_only_at_dead_end_with_30s_penalty()
        {
            RoadNetwork net = PathKit.NewNet();
            EntityId n0 = PathKit.Node(net, 0, 0), n1 = PathKit.Node(net, 100, 0);
            EntityId a = PathKit.Seg(net, n0, n1, RoadTypeIds.Street2);
            var finder = new PathFinder(net);
            var route = new RouteStep[4];
            Assert.True(finder.FindPath(new RouteStep(a, true), new RouteStep(a, false), PathKit.NoNoise, route, out int len, out float cost));
            Assert.Equal(2, len);
            Assert.Equal(2 * 100f / Street + 30f, cost, 2);
            // Mismo sentido: un solo paso, coste de un segmento.
            Assert.True(finder.FindPath(new RouteStep(a, true), new RouteStep(a, true), PathKit.NoNoise, route, out len, out cost));
            Assert.Equal(1, len);
            Assert.Equal(100f / Street, cost, 2);
        }

        [Fact]
        public void No_route_returns_false()
        {
            RoadNetwork net = PathKit.NewNet();
            EntityId a = PathKit.Seg(net, PathKit.Node(net, 0, 0), PathKit.Node(net, 100, 0), RoadTypeIds.Street2);
            EntityId b = PathKit.Seg(net, PathKit.Node(net, 0, 500), PathKit.Node(net, 100, 500), RoadTypeIds.Street2);
            var finder = new PathFinder(net);
            var route = new RouteStep[8];
            Assert.False(finder.FindPath(a, b, PathKit.NoNoise, route, out int len, out _));
            Assert.Equal(0, len);
            // Segmento muerto o direcciones vacias.
            Assert.False(finder.FindPath(a, TravelDirs.None, b, TravelDirs.Both, PathKit.NoNoise, route, out _, out _));
            Assert.True(net.RemoveSegment(b, null));
            Assert.False(finder.FindPath(a, b, PathKit.NoNoise, route, out _, out _));
        }

        [Fact]
        public void Output_too_short_reports_needed_length()
        {
            RoadNetwork net = PathKit.NewNet();
            EntityId[] n = PathKit.BuildGrid(net, 5, 1, 100f, RoadTypeIds.Street2);
            EntityId first = PathKit.SegmentBetween(net, n[0], n[1]);
            EntityId last = PathKit.SegmentBetween(net, n[3], n[4]);
            var finder = new PathFinder(net);
            Assert.False(finder.FindPath(new RouteStep(first, true), new RouteStep(last, true), PathKit.NoNoise, new RouteStep[2], out int len, out _));
            Assert.Equal(4, len);
        }

        [Fact]
        public void Prefers_straight_over_a_faster_detour_with_turns_and_flips_when_turns_are_free()
        {
            // Recto: A(0,0) -> C(200,0) -> D(300,0) con calles (40 km/h).
            // Desvio: A -> P(0,100) (izquierda) -> Q(200,100) (derecha) -> C (derecha) por autopista (100 km/h), 400 m.
            RoadNetwork net = PathKit.NewNet();
            EntityId origin0 = PathKit.Node(net, -100, 0);
            EntityId a = PathKit.Node(net, 0, 0), c = PathKit.Node(net, 200, 0), d = PathKit.Node(net, 300, 0);
            EntityId p = PathKit.Node(net, 0, 100), q = PathKit.Node(net, 200, 100);
            EntityId sOrigin = PathKit.Seg(net, origin0, a, RoadTypeIds.Street2);
            EntityId sAC = PathKit.Seg(net, a, c, RoadTypeIds.Street2);
            EntityId sCD = PathKit.Seg(net, c, d, RoadTypeIds.Street2);
            EntityId sAP = PathKit.Seg(net, a, p, RoadTypeIds.Highway4);
            EntityId sPQ = PathKit.Seg(net, p, q, RoadTypeIds.Highway4);
            EntityId sQC = PathKit.Seg(net, q, c, RoadTypeIds.Highway4);

            var route = new RouteStep[16];
            var finder = new PathFinder(net);
            Assert.True(finder.FindPath(new RouteStep(sOrigin, true), new RouteStep(sCD, true), PathKit.NoNoise, route, out int len, out float costDefault));
            Assert.Equal(3, len); // origen, A-C, C-D: recto
            Assert.Equal(sAC, route[1].Segment);

            var free = new PathCostConfig { LeftTurnPenalty = 0f, RightTurnPenalty = 0f };
            var finder2 = new PathFinder(net, free);
            Assert.True(finder2.FindPath(new RouteStep(sOrigin, true), new RouteStep(sCD, true), PathKit.NoNoise, route, out len, out float costFree));
            Assert.Equal(5, len); // origen, A-P, P-Q, Q-C, C-D: el desvio rapido gana sin penalizar giros
            Assert.Equal(sAP, route[1].Segment);
            Assert.Equal(sQC, route[3].Segment);
            Assert.True(costFree < costDefault);
            _ = sPQ;
        }

        [Fact]
        public void Left_turn_costs_more_than_right_turn()
        {
            // Cruce en T de 4 brazos: desde el oeste, destino al norte (izquierda) vs al sur (derecha).
            RoadNetwork net = PathKit.NewNet();
            EntityId c = PathKit.Node(net, 0, 0);
            EntityId w = PathKit.Node(net, -100, 0), nn = PathKit.Node(net, 0, 100), s = PathKit.Node(net, 0, -100);
            EntityId sw = PathKit.Seg(net, w, c, RoadTypeIds.Street2);
            EntityId sn = PathKit.Seg(net, c, nn, RoadTypeIds.Street2);
            EntityId ss = PathKit.Seg(net, c, s, RoadTypeIds.Street2);
            var finder = new PathFinder(net);
            var route = new RouteStep[4];
            Assert.True(finder.FindPath(new RouteStep(sw, true), new RouteStep(sn, true), PathKit.NoNoise, route, out _, out float left));
            Assert.True(finder.FindPath(new RouteStep(sw, true), new RouteStep(ss, true), PathKit.NoNoise, route, out _, out float right));
            Assert.Equal(5f - 2f, left - right, 2);
        }

        [Fact]
        public void Congestion_diverts_the_route()
        {
            // Dos caminos paralelos de igual longitud entre dos nodos; congestionar uno desvia por el otro.
            RoadNetwork net = PathKit.NewNet();
            EntityId a = PathKit.Node(net, 0, 0), b = PathKit.Node(net, 200, 0);
            EntityId up = PathKit.Node(net, 100, 100), down = PathKit.Node(net, 100, -100);
            EntityId pre = PathKit.Seg(net, PathKit.Node(net, -100, 0), a, RoadTypeIds.Street2);
            EntityId post = PathKit.Seg(net, b, PathKit.Node(net, 300, 0), RoadTypeIds.Street2);
            EntityId au = PathKit.Seg(net, a, up, RoadTypeIds.Street2), ub = PathKit.Seg(net, up, b, RoadTypeIds.Street2);
            EntityId ad = PathKit.Seg(net, a, down, RoadTypeIds.Street2), db = PathKit.Seg(net, down, b, RoadTypeIds.Street2);
            var finder = new PathFinder(net);
            var route = new RouteStep[8];

            Assert.True(finder.FindPath(pre, post, PathKit.NoNoise, route, out int len, out float freeCost));
            Assert.Equal(4, len);
            EntityId first = route[1].Segment;
            EntityId otherFirst = first == au ? ad : au;

            var cong = new float[net.SegmentIndexBound];
            Array.Fill(cong, 1f);
            cong[first.Index] = 0.1f;
            finder.Congestion = cong;
            Assert.True(finder.FindPath(pre, post, PathKit.NoNoise, route, out len, out float cost2));
            Assert.Equal(otherFirst, route[1].Segment);
            // El camino libre es el de abajo (dos giros a la derecha = 4 s); el de arriba lleva dos izquierdas (10 s).
            Assert.Equal(ad, first);
            Assert.Equal(freeCost + 6f, cost2, 2);
            _ = ub; _ = db;
        }

        [Fact]
        public void Driver_noise_is_deterministic_and_varies_between_drivers()
        {
            RoadNetwork net = PathKit.NewNet();
            EntityId[] n = PathKit.BuildGrid(net, 8, 8, 100f, RoadTypeIds.Street2);
            EntityId o = PathKit.SegmentBetween(net, n[0], n[1]);
            EntityId d = PathKit.SegmentBetween(net, n[62], n[63]);
            var finder = new PathFinder(net, null, 0);
            var routeA = new RouteStep[64];
            var routeB = new RouteStep[64];
            var distinct = new HashSet<string>();
            for (uint seed = 1; seed <= 30; seed++)
            {
                PathOptions opt = PathOptions.WithSeed(seed);
                opt.NoCache = true;
                Assert.True(finder.FindPath(o, d, opt, routeA, out int la, out float ca));
                Assert.True(finder.FindPath(o, d, opt, routeB, out int lb, out float cb));
                Assert.Equal(la, lb);
                Assert.Equal(ca, cb);
                for (int i = 0; i < la; i++) Assert.Equal(routeA[i], routeB[i]);
                distinct.Add(string.Join(",", routeA.AsSpan(0, la).ToArray()));
            }
            Assert.True(distinct.Count > 1, "el ruido por conductor deberia producir rutas distintas en una cuadricula");
        }

        [Fact]
        public void Same_input_gives_same_route_across_independent_instances()
        {
            string Run()
            {
                RoadNetwork net = PathKit.RandomNetwork(77);
                var finder = new PathFinder(net);
                finder.Congestion = PathKit.RandomCongestion(net, 5);
                EntityId[] segs = PathKit.AliveSegments(net);
                var route = new RouteStep[256];
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < segs.Length; i++)
                {
                    PathOptions opt = PathOptions.WithSeed((uint)(i * 7 + 1));
                    bool ok = finder.FindPath(segs[i], segs[(i * 5 + 3) % segs.Length], opt, route, out int len, out float cost);
                    sb.Append(ok).Append(':').Append(cost.ToString("R")).Append(':').Append(string.Join(",", route.AsSpan(0, len).ToArray())).Append(';');
                }
                return sb.ToString();
            }
            Assert.Equal(Run(), Run());
        }

        [Fact]
        public void Position_overload_snaps_to_nearest_segments()
        {
            RoadNetwork net = PathKit.NewNet();
            EntityId[] n = PathKit.BuildGrid(net, 4, 1, 100f, RoadTypeIds.Street2);
            var finder = new PathFinder(net);
            var route = new RouteStep[8];
            Assert.True(finder.FindPath(new Urbe.Sim.Geometry.Vec2(-140f, 3f), new Urbe.Sim.Geometry.Vec2(140f, -2f), 20f, PathKit.NoNoise, route, out int len, out _));
            Assert.Equal(3, len);
            Assert.False(finder.FindPath(new Urbe.Sim.Geometry.Vec2(0f, 900f), new Urbe.Sim.Geometry.Vec2(140f, -2f), 20f, PathKit.NoNoise, route, out len, out _));
            Assert.Equal(0, len);
        }

        // ------------------------------------------------------------------ ALT / ruido

        [Fact]
        public void Landmarks_are_valid_for_one_epoch_only_and_do_not_change_costs()
        {
            RoadNetwork net = PathKit.NewNet();
            EntityId[] n = PathKit.BuildGrid(net, 10, 10, 100f, RoadTypeIds.Street2);
            EntityId o = PathKit.SegmentBetween(net, n[0], n[1]);
            EntityId d = PathKit.SegmentBetween(net, n[98], n[99]);
            var finder = new PathFinder(net, null, 0);
            var route = new RouteStep[64];
            Assert.False(finder.LandmarksReady);
            Assert.True(finder.FindPath(o, d, PathKit.NoNoise, route, out int l1, out float euclid));
            finder.BuildLandmarks();
            Assert.True(finder.LandmarksReady);
            int e0 = finder.ExpandedStates;
            Assert.True(finder.FindPath(o, d, PathKit.NoNoise, route, out int l2, out float alt));
            int altExpanded = finder.ExpandedStates - e0;
            Assert.Equal(euclid, alt, 2); // mismo coste (puede variar la ruta concreta en empates)
            Assert.True(l1 > 0 && l2 > 0);

            PathKit.Node(net, 5000, 5000); // sube la epoca: los landmarks quedan obsoletos y se ignoran
            Assert.False(finder.LandmarksReady);
            e0 = finder.ExpandedStates;
            Assert.True(finder.FindPath(o, d, PathKit.NoNoise, route, out _, out float after));
            Assert.Equal(euclid, after, 2);
            Assert.True(finder.ExpandedStates - e0 > altExpanded, "ALT debe expandir menos estados que la euclidea");
        }

        [Fact]
        public void Admissible_noise_mode_never_returns_a_costlier_route_than_default_mode()
        {
            RoadNetwork net = PathKit.NewNet();
            EntityId[] n = PathKit.BuildGrid(net, 12, 12, 100f, RoadTypeIds.Street2);
            EntityId[] segs = PathKit.AliveSegments(net);
            var fast = new PathFinder(net, null, 0);
            var exact = new PathFinder(net, new PathCostConfig { AdmissibleNoise = true }, 0);
            fast.BuildLandmarks();
            exact.BuildLandmarks();
            var rf = new RouteStep[128];
            var re = new RouteStep[128];
            var rng = new Urbe.Sim.Random.Pcg32(4, 4);
            for (int i = 0; i < 100; i++)
            {
                EntityId o = segs[rng.NextInt(segs.Length)], d = segs[rng.NextInt(segs.Length)];
                PathOptions opt = PathOptions.WithSeed((uint)i);
                opt.NoCache = true;
                Assert.True(fast.FindPath(o, d, opt, rf, out _, out float cf));
                Assert.True(exact.FindPath(o, d, opt, re, out _, out float ce));
                Assert.True(ce <= cf + 1e-3f, "el modo admisible no puede costar mas: " + ce + " vs " + cf);
                Assert.True(cf <= ce * 1.12f + 1e-3f, "el modo rapido deberia quedar cerca del optimo percibido: " + cf + " vs " + ce);
            }
        }

        // ------------------------------------------------------------------ cache

        [Fact]
        public void Cache_hits_on_repeat_and_returns_identical_route()
        {
            RoadNetwork net = PathKit.NewNet();
            EntityId[] n = PathKit.BuildGrid(net, 6, 6, 100f, RoadTypeIds.Street2);
            EntityId o = PathKit.SegmentBetween(net, n[0], n[1]);
            EntityId d = PathKit.SegmentBetween(net, n[34], n[35]);
            var finder = new PathFinder(net, null, 64);
            var r1 = new RouteStep[32];
            var r2 = new RouteStep[32];
            PathOptions opt = PathOptions.WithSeed(9, 2);
            Assert.True(finder.FindPath(o, d, opt, r1, out int l1, out float c1));
            Assert.Equal(1, finder.SearchCount);
            Assert.True(finder.FindPath(o, d, opt, r2, out int l2, out float c2));
            Assert.Equal(1, finder.SearchCount);
            Assert.Equal(1, finder.CacheHits);
            Assert.Equal(l1, l2);
            Assert.Equal(c1, c2);
            for (int i = 0; i < l1; i++) Assert.Equal(r1[i], r2[i]);

            // Otro tipo de vehiculo o semilla = otra clave.
            PathOptions other = opt;
            other.VehicleType = 3;
            Assert.True(finder.FindPath(o, d, other, r2, out _, out _));
            Assert.Equal(2, finder.SearchCount);

            // NoCache ignora la cache.
            PathOptions nc = opt;
            nc.NoCache = true;
            Assert.True(finder.FindPath(o, d, nc, r2, out _, out _));
            Assert.Equal(3, finder.SearchCount);
        }

        [Fact]
        public void Cache_is_invalidated_when_network_epoch_changes()
        {
            RoadNetwork net = PathKit.NewNet();
            EntityId a = PathKit.Node(net, 0, 0), b = PathKit.Node(net, 100, 0), c = PathKit.Node(net, 200, 0);
            EntityId s1 = PathKit.Seg(net, a, b, RoadTypeIds.Street2);
            EntityId s2 = PathKit.Seg(net, b, c, RoadTypeIds.Street2);
            var finder = new PathFinder(net, null, 64);
            var route = new RouteStep[8];
            PathOptions opt = PathOptions.WithSeed(1);
            Assert.True(finder.FindPath(s1, s2, opt, route, out _, out _));
            Assert.True(finder.FindPath(s1, s2, opt, route, out _, out _));
            Assert.Equal(1, finder.CacheHits);

            // Cualquier cambio de la red sube la epoca y vacia la cache: se recalcula.
            ulong before = net.Epoch;
            EntityId d = PathKit.Node(net, 200, 100);
            Assert.True(net.Epoch > before);
            Assert.True(finder.FindPath(s1, s2, opt, route, out _, out _));
            Assert.Equal(1, finder.CacheHits);
            Assert.Equal(2, finder.SearchCount);

            // Y la ruta cacheada no sobrevive a la demolicion de uno de sus segmentos.
            Assert.True(finder.FindPath(s1, s2, opt, route, out _, out _));
            Assert.Equal(2, finder.CacheHits);
            Assert.True(net.RemoveSegment(s2, null));
            Assert.False(finder.FindPath(s1, s2, opt, route, out int len, out _));
            Assert.Equal(0, len);
            _ = d;
        }

        [Fact]
        public void Cache_key_includes_congestion_epoch()
        {
            RoadNetwork net = PathKit.NewNet();
            EntityId[] n = PathKit.BuildGrid(net, 3, 1, 100f, RoadTypeIds.Street2);
            EntityId s1 = PathKit.SegmentBetween(net, n[0], n[1]);
            EntityId s2 = PathKit.SegmentBetween(net, n[1], n[2]);
            var finder = new PathFinder(net, null, 64);
            var route = new RouteStep[8];
            PathOptions opt = PathOptions.WithSeed(1);
            Assert.True(finder.FindPath(s1, s2, opt, route, out _, out float c1));
            Assert.True(finder.FindPath(s1, s2, opt, route, out _, out _));
            Assert.Equal(1, finder.CacheHits);

            var cong = new float[net.SegmentIndexBound];
            Array.Fill(cong, 0.5f);
            finder.Congestion = cong; // sube la epoca de congestion
            Assert.True(finder.FindPath(s1, s2, opt, route, out _, out float c2));
            Assert.Equal(1, finder.CacheHits);
            Assert.True(c2 > c1);
        }

        [Fact]
        public void Cache_with_tiny_capacity_replaces_entries_and_stays_correct()
        {
            RoadNetwork net = PathKit.NewNet();
            EntityId[] n = PathKit.BuildGrid(net, 6, 6, 100f, RoadTypeIds.Street2);
            EntityId[] segs = PathKit.AliveSegments(net);
            var cached = new PathFinder(net, null, 16);
            var plain = new PathFinder(net, null, 0);
            var ra = new RouteStep[64];
            var rb = new RouteStep[64];
            for (int rep = 0; rep < 4; rep++)
                for (int i = 0; i < 100; i++)
                {
                    int k = (i * 7) % 40; // 40 consultas distintas rotando sobre una cache de 16 entradas
                    EntityId o = segs[(k * 7) % segs.Length], d = segs[(k * 13 + 5) % segs.Length];
                    PathOptions opt = PathOptions.WithSeed((uint)k);
                    bool a = cached.FindPath(o, d, opt, ra, out int la, out float ca);
                    bool b = plain.FindPath(o, d, opt, rb, out int lb, out float cb);
                    Assert.Equal(b, a);
                    Assert.Equal(lb, la);
                    Assert.Equal(cb, ca);
                    for (int q = 0; q < la; q++) Assert.Equal(rb[q], ra[q]);
                }
            Assert.True(cached.CacheHits > 0);
        }
    }
}
