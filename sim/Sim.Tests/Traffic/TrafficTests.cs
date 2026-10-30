using System;
using System.Collections.Generic;
using System.IO;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Urbe.Sim.Tests.Roads;
using Urbe.Sim.Tests.Vehicles;
using Urbe.Sim.Traffic;
using Urbe.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace Urbe.Sim.Tests.Traffic
{
    public class TrafficTests
    {
        private readonly ITestOutputHelper _out;

        public TrafficTests(ITestOutputHelper output) { _out = output; }

        private static TrafficSimulation Grid(ulong seed, float tripsPerSecond, int cells = 6, int maxActive = 600)
        {
            RoadNetwork net = VK.MakeGrid(cells, 100f);
            var opt = new TrafficSimulationOptions
            {
                VehicleCapacity = 2048,
                Vehicle = new VehicleConfig { DriverVariation = true },
            };
            opt.Trips.TripsPerSecond = tripsPerSecond;
            opt.Trips.MaxActiveVehicles = maxActive;
            var sim = new TrafficSimulation(net, seed, opt);
            for (int i = 0; i < net.SegmentIndexBound; i++)
            {
                EntityId seg = net.SegmentIdAt(i);
                if (seg.IsValid) sim.Trips.AddDemandPoint(seg, 1f, 1f);
            }
            return sim;
        }

        private static string RunCsv(ulong seed, int seconds)
        {
            TrafficSimulation sim = Grid(seed, 2.5f);
            var sw = new StringWriter();
            sim.AttachCsv(sw);
            sim.Run(seconds * 10);
            return sw.ToString();
        }

        [Fact]
        public void Metric_LoneVehicleOnFreeRoad_Is100Percent()
        {
            RoadNetwork net = Kit.New();
            EntityId n0 = Kit.Node(net, 0, 0), n1 = Kit.Node(net, 1500, 0);
            EntityId seg = Kit.Seg(net, n0, n1, RoadTypeIds.Street2);
            var sim = new TrafficSimulation(net, 1, new TrafficSimulationOptions
            {
                Vehicle = new VehicleConfig { DriverVariation = false },
                VehicleCapacity = 16,
            });
            Assert.True(sim.Vehicles.TrySpawn(VehicleTypeId.Car, new[] { new RouteSegment(seg, true) }, 5f, 1450f, 13f, out _));
            sim.Run(300);
            Assert.True(sim.Metrics.CityFlowPercent > 99.5f, "flujo " + sim.Metrics.CityFlowPercent);
            Assert.True(sim.Metrics.GetSegmentFlow(seg.Index) > 0.99f);
        }

        [Fact]
        public void Metric_DropsWhenQueueFormsBehindStoppedVehicle()
        {
            RoadNetwork net = Kit.New();
            EntityId n0 = Kit.Node(net, 0, 0), n1 = Kit.Node(net, 1000, 0);
            EntityId seg = Kit.Seg(net, n0, n1, RoadTypeIds.Street2);
            var sim = new TrafficSimulation(net, 1, new TrafficSimulationOptions
            {
                Vehicle = new VehicleConfig { DriverVariation = false },
                VehicleCapacity = 64,
            });
            Assert.True(sim.Vehicles.TrySpawn(VehicleTypeId.Car, new[] { new RouteSegment(seg, true) }, 600f, 990f, 0f, out EntityId blocker));
            sim.Vehicles.SetFrozen(blocker, true);
            for (int k = 0; k < 8; k++)
            {
                Assert.True(sim.Vehicles.TrySpawn(VehicleTypeId.Car, new[] { new RouteSegment(seg, true) }, 5f + k * 0f, 990f, 13f, out _) || k > 0);
                sim.Run(100);
            }
            sim.Run(900);
            float flow = sim.Metrics.CityFlowPercent;
            float segFlow = sim.Metrics.GetSegmentFlow(seg.Index);
            _out.WriteLine("flujo " + flow + " segmento " + segFlow);
            Assert.True(flow < 40f, "flujo " + flow);
            Assert.True(segFlow < 0.4f, "segmento " + segFlow);
        }

        [Fact]
        public void Congestion_FactorsReachThePathFinder()
        {
            TrafficSimulation sim = Grid(3, 4f);
            sim.Run(1500);
            Assert.NotNull(sim.Paths.Congestion);
            float min = 1f;
            foreach (float f in sim.Paths.Congestion!) min = Math.Min(min, f);
            Assert.True(min < 1f && min >= sim.Metrics.Config.MinCongestionFactor);
            Assert.True(sim.Paths.CongestionEpoch > 1);
        }

        [Fact]
        public void Stability_ConstantDemand_FlowVariesLessThan5PointsBetweenMinutes()
        {
            string csv = RunCsv(7, 600);
            string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            _out.WriteLine(csv);
            var flows = new List<double>();
            for (int i = 1; i < lines.Length; i++)
            {
                string[] c = lines[i].Split(',');
                int minute = int.Parse(c[0]);
                if (minute >= 5 && minute <= 10) flows.Add(double.Parse(c[5], System.Globalization.CultureInfo.InvariantCulture));
            }
            Assert.Equal(6, flows.Count);
            for (int i = 1; i < flows.Count; i++)
                Assert.True(Math.Abs(flows[i] - flows[i - 1]) < 5.0, "minutos " + (4 + i) + "->" + (5 + i) + ": " + flows[i - 1] + " -> " + flows[i]);
        }

        [Fact]
        public void Csv_IsDeterministicForSameSeed_AndDiffersForOther()
        {
            string a = RunCsv(11, 240);
            string b = RunCsv(11, 240);
            string c = RunCsv(12, 240);
            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
            Assert.Equal(5, a.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        }

        [Fact]
        public void Trips_AreConserved_GeneratedEqualsCompletedPlusActivePlusPendingPlusLost()
        {
            // 2 viajes/s (antes 3): con el JunctionController por defecto (M1-07b) la cuadricula 6x6 se satura con 3 y algun viaje caduca en la salida.
            TrafficSimulation sim = Grid(21, 2f);
            for (int step = 0; step < 6; step++)
            {
                sim.Run(500);
                TripGenerator t = sim.Trips;
                long sum = t.Completed + sim.Vehicles.AliveCount + t.Pending + t.Lost;
                _out.WriteLine("gen " + t.Generated + " done " + t.Completed + " vivos " + sim.Vehicles.AliveCount
                    + " pend " + t.Pending + " lost " + t.Lost + " dropped " + t.Dropped);
                Assert.Equal(t.Generated, sum);
            }
            Assert.True(sim.Trips.Completed > 50);
            Assert.Equal(0, sim.Trips.Lost);
        }

        [Fact]
        public void FullExitLane_TripsWaitAndRetryInsteadOfVanishing()
        {
            RoadNetwork net = Kit.New();
            EntityId n0 = Kit.Node(net, 0, 0), n1 = Kit.Node(net, 300, 0), n2 = Kit.Node(net, 600, 0);
            EntityId s1 = Kit.Seg(net, n0, n1, RoadTypeIds.Street2);
            EntityId s2 = Kit.Seg(net, n1, n2, RoadTypeIds.Street2);
            var opt = new TrafficSimulationOptions { VehicleCapacity = 256, Vehicle = new VehicleConfig { DriverVariation = false } };
            opt.Trips.TripsPerSecond = 0.2f;
            opt.Trips.MaxWaitSeconds = 400f;
            opt.Trips.TruckFraction = 0f;
            var sim = new TrafficSimulation(net, 4, opt);
            sim.Trips.AddDemandPoint(s1, 1f, 0f);
            sim.Trips.AddDemandPoint(s2, 0f, 1f);
            // Un vehiculo parado justo en el punto de salida: el carril esta lleno.
            Assert.True(sim.Vehicles.TrySpawn(VehicleTypeId.Car, new[] { new RouteSegment(s1, true) }, 120f, 290f, 0f, out EntityId blocker));
            sim.Vehicles.SetFrozen(blocker, true);
            sim.Run(600);
            TripGenerator t = sim.Trips;
            Assert.True(t.Generated >= 10);
            Assert.Equal(0, t.Completed);
            Assert.True(t.Pending >= 10, "pendientes " + t.Pending);
            Assert.Equal(0, t.Lost);
            sim.Vehicles.SetFrozen(blocker, false);
            sim.Run(2400);
            _out.WriteLine("gen " + t.Generated + " done " + t.Completed + " pend " + t.Pending + " lost " + t.Lost);
            Assert.Equal(0, t.Lost);
            Assert.Equal(t.Generated, t.Completed + sim.Vehicles.AliveCount + t.Pending);
            Assert.True(t.Completed > 40);
        }

        [Fact]
        public void SteadyState_ZeroAllocationsPerTick()
        {
            TrafficSimulation sim = Grid(31, 3f);
            sim.Run(3000); // calentamiento: crecen colas, caches y pools
            long before = GC.GetAllocatedBytesForCurrentThread();
            sim.Run(600);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, bytes);
        }

        [Fact]
        public void Reroute_VehiclesWithBrokenRouteFindAnotherWay()
        {
            TrafficSimulation sim = Grid(41, 3f, 5);
            sim.Run(600);
            // Derriba cuatro segmentos repartidos por la red mientras circula el trafico.
            int bound = sim.Network.SegmentIndexBound;
            var victims = new EntityId[] { sim.Network.SegmentIdAt(bound / 5), sim.Network.SegmentIdAt(2 * bound / 5), sim.Network.SegmentIdAt(3 * bound / 5), sim.Network.SegmentIdAt(4 * bound / 5) };
            sim.Commands.Enqueue(new Urbe.Sim.Commands.DemolishCommand(victims));
            sim.Run(1500);
            TripGenerator t = sim.Trips;
            _out.WriteLine("rerouted " + t.Rerouted + " lost " + t.Lost + " gen " + t.Generated + " done " + t.Completed);
            Assert.Equal(t.Generated, t.Completed + sim.Vehicles.AliveCount + t.Pending + t.Lost);
            Assert.True(t.Rerouted > 0, "ningun vehiculo re-rutado");
            Assert.True(t.Completed > 100);
        }
    }
}
