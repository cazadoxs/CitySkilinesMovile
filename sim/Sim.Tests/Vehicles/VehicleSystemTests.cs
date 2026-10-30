using System;
using System.Collections.Generic;
using System.Diagnostics;
using Urbe.Sim.Ids;
using Urbe.Sim.Random;
using Urbe.Sim.Roads;
using Urbe.Sim.Tests.Roads;
using Urbe.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace Urbe.Sim.Tests.Vehicles
{
    public class VehicleSystemTests
    {
        private readonly ITestOutputHelper _out;

        public VehicleSystemTests(ITestOutputHelper output) { _out = output; }

        // 10. RoutePool.
        [Fact]
        public void RoutePool_RandomAllocFree_NoOverlap_NoGrowthAfterWarmup()
        {
            var pool = new RoutePool(1 << 16);
            var rng = new Pcg32(42, 1);
            var live = new List<(int off, int len)>();
            int capacityAfterWarmup = 0, growthsAfterWarmup = 0, topAfterWarmup = 0;
            for (int op = 0; op < 10000; op++)
            {
                if (op == 5000)
                {
                    capacityAfterWarmup = pool.Capacity;
                    growthsAfterWarmup = pool.Growths;
                    topAfterWarmup = pool.Top;
                }
                bool alloc = live.Count < 8 || (live.Count < 40 && rng.NextInt(2) == 0);
                if (alloc)
                {
                    int len = 1 + rng.NextInt(rng.NextInt(4) == 0 ? 1024 : 40);
                    int off = pool.Allocate(len);
                    Assert.True(off >= 0);
                    int size = RoutePool.BlockSize(len);
                    foreach (var (o, l) in live)
                    {
                        int sz = RoutePool.BlockSize(l);
                        Assert.True(off + size <= o || o + sz <= off, "bloques solapados");
                    }
                    Assert.True(off + size <= pool.Top);
                    live.Add((off, len));
                }
                else
                {
                    int k = rng.NextInt(live.Count);
                    pool.Free(live[k].off, live[k].len);
                    live.RemoveAt(k);
                }
            }
            Assert.Equal(growthsAfterWarmup, pool.Growths);
            Assert.Equal(capacityAfterWarmup, pool.Capacity);
            Assert.True(pool.Top - topAfterWarmup <= 4096, "el pool siguio creciendo: " + (pool.Top - topAfterWarmup));
            Assert.Equal(live.Count, pool.BlocksInUse);
            Assert.Equal(-1, pool.Allocate(0));
            Assert.Equal(-1, pool.Allocate(1025));
        }

        // 12. Determinismo.
        private static List<ulong> RunScenario(ulong seed, int ticks)
        {
            RoadNetwork n = VK.MakeGrid(5, 100f);
            VehicleSystem s = VK.Sys(n, 800, seed, variation: true);
            var pop = new VK.Population(n, s, 400, seed + 1000);
            var hashes = new List<ulong>();
            for (int t = 1; t <= ticks; t++)
            {
                pop.TopUp();
                s.Update();
                if (t % 100 == 0) hashes.Add(s.ComputeHash());
            }
            return hashes;
        }

        [Fact]
        public void Determinism_SameSeed_SameHashEvery100Ticks_For2000Ticks()
        {
            List<ulong> a = RunScenario(21, 2000);
            List<ulong> b = RunScenario(21, 2000);
            Assert.Equal(20, a.Count);
            Assert.Equal(a, b);
            List<ulong> c = RunScenario(22, 2000);
            Assert.NotEqual(a, c);
        }

        // 14. Asignaciones.
        [Fact]
        public void Update_AllocatesZeroBytes_InSteadyState()
        {
            RoadNetwork n = VK.MakeGrid(8, 100f);
            VehicleSystem s = VK.Sys(n, 4000, 5, variation: true);
            var pop = new VK.Population(n, s, 2000, 9);
            for (int t = 0; t < 600; t++)
            {
                pop.TopUp(256);
                s.Update();
            }
            Assert.True(s.AliveCount > 1500, "alive = " + s.AliveCount);
            VK.Run(s, 20);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int t = 0; t < 100; t++) s.Update();
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, after - before);
        }

        // 15. Benchmark informativo.
        [Fact]
        [Trait("Category", "Bench")]
        public void Bench_3000Vehicles_Grid_MsPerTick()
        {
            RoadNetwork n = VK.MakeGrid(12, 120f);
            VehicleSystem s = VK.Sys(n, 4000, 5, variation: true);
            var pop = new VK.Population(n, s, 3000, 9);
            for (int t = 0; t < 800; t++)
            {
                pop.TopUp(256);
                s.Update();
            }
            int ticks = 300;
            var sw = new Stopwatch();
            for (int t = 0; t < ticks; t++)
            {
                pop.TopUp(256);
                sw.Start();
                s.Update();
                sw.Stop();
            }
            double ms = sw.Elapsed.TotalMilliseconds / ticks;
            _out.WriteLine("BENCH vehiculos={0} ms/tick={1:F3}", s.AliveCount, ms);
            Console.WriteLine("BENCH vehiculos={0} ms/tick={1:F3}", s.AliveCount, ms);
            Assert.True(s.AliveCount > 2500);
            Assert.True(ms < 50.0, "ms/tick = " + ms); // cota laxa: la cifra es informativa
        }
    }
}
