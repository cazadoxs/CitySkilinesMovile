using Urbe.Sim.Ids;
using Urbe.Sim.Pathfinding;
using Urbe.Sim.Roads;
using Xunit;

namespace Urbe.Sim.Tests.Pathfinding
{
    public class PathRequestQueueTests
    {
        private static (RoadNetwork net, PathFinder finder, EntityId a, EntityId b) Setup()
        {
            RoadNetwork net = PathKit.NewNet();
            EntityId[] n = PathKit.BuildGrid(net, 4, 1, 100f, RoadTypeIds.Street2);
            var finder = new PathFinder(net);
            return (net, finder, PathKit.SegmentBetween(net, n[0], n[1]), PathKit.SegmentBetween(net, n[2], n[3]));
        }

        private static PathRequest Req(EntityId a, EntityId b, PathPriority p) => new PathRequest
        {
            OriginSegment = a, OriginDirs = TravelDirs.Both, DestinationSegment = b, DestinationDirs = TravelDirs.Both,
            Options = PathOptions.WithSeed(1), Priority = p,
        };

        [Fact]
        public void Higher_priority_first_and_fifo_within_priority()
        {
            var (_, finder, a, b) = Setup();
            var q = new PathRequestQueue(finder);
            PathPriority[] order =
            {
                PathPriority.Private, PathPriority.Private, PathPriority.PublicTransport, PathPriority.Service,
                PathPriority.Emergency, PathPriority.Private, PathPriority.Emergency, PathPriority.Service,
            };
            var ids = new int[order.Length];
            for (int i = 0; i < order.Length; i++) Assert.True(q.TryEnqueue(Req(a, b, order[i]), out ids[i]));
            Assert.Equal(8, q.PendingCount);

            Assert.Equal(3, q.Process(3));
            // Las dos emergencias (FIFO) y despues el primer servicio.
            Assert.Equal(1, q.GetCompletionOrder(ids[4]));
            Assert.Equal(2, q.GetCompletionOrder(ids[6]));
            Assert.Equal(3, q.GetCompletionOrder(ids[3]));
            Assert.Equal(PathStatus.Pending, q.GetStatus(ids[0]));
            Assert.Equal(PathStatus.Pending, q.GetStatus(ids[2]));

            Assert.Equal(5, q.Process(100)); // el presupuesto sobrante no se consume
            Assert.Equal(0, q.PendingCount);
            Assert.Equal(0, q.Process(10));
            // Resto: servicio 7, transporte 2, particulares 0, 1, 5 (en ese orden).
            Assert.Equal(4, q.GetCompletionOrder(ids[7]));
            Assert.Equal(5, q.GetCompletionOrder(ids[2]));
            Assert.Equal(6, q.GetCompletionOrder(ids[0]));
            Assert.Equal(7, q.GetCompletionOrder(ids[1]));
            Assert.Equal(8, q.GetCompletionOrder(ids[5]));
        }

        [Fact]
        public void Budget_limits_requests_per_call_and_results_are_queryable_by_id()
        {
            var (_, finder, a, b) = Setup();
            var q = new PathRequestQueue(finder);
            Assert.True(q.TryEnqueue(Req(a, b, PathPriority.Private), out int id1));
            Assert.True(q.TryEnqueue(Req(b, a, PathPriority.Private), out int id2));
            Assert.NotEqual(id1, id2);
            Assert.Equal(PathStatus.Pending, q.GetStatus(id1));
            Assert.Equal(1, q.Process(1));
            Assert.Equal(PathStatus.Found, q.GetStatus(id1));
            Assert.Equal(PathStatus.Pending, q.GetStatus(id2));
            Assert.Equal(PathStatus.None, q.GetStatus(999));

            var route = new RouteStep[8];
            Assert.True(q.TryGetResult(id1, route, out int len, out float cost));
            Assert.Equal(3, len);
            Assert.Equal(a, route[0].Segment);
            Assert.True(cost > 0f);
            Assert.False(q.TryGetResult(id2, route, out _, out _)); // aun pendiente
            Assert.False(q.TryGetResult(id1, new RouteStep[1], out len, out _));
            Assert.Equal(3, len);
        }

        [Fact]
        public void Unreachable_request_reports_not_found()
        {
            RoadNetwork net = PathKit.NewNet();
            EntityId a = PathKit.Seg(net, PathKit.Node(net, 0, 0), PathKit.Node(net, 100, 0), RoadTypeIds.Street2);
            EntityId b = PathKit.Seg(net, PathKit.Node(net, 0, 500), PathKit.Node(net, 100, 500), RoadTypeIds.Street2);
            var q = new PathRequestQueue(new PathFinder(net));
            Assert.True(q.TryEnqueue(Req(a, b, PathPriority.Emergency), out int id));
            q.Process(1);
            Assert.Equal(PathStatus.NotFound, q.GetStatus(id));
            Assert.False(q.TryGetResult(id, new RouteStep[4], out int len, out _));
            Assert.Equal(0, len);
        }

        [Fact]
        public void Route_longer_than_store_is_too_long()
        {
            RoadNetwork net = PathKit.NewNet();
            EntityId[] n = PathKit.BuildGrid(net, 8, 1, 100f, RoadTypeIds.Street2);
            var q = new PathRequestQueue(new PathFinder(net), 4, 3);
            Assert.True(q.TryEnqueue(Req(PathKit.SegmentBetween(net, n[0], n[1]), PathKit.SegmentBetween(net, n[6], n[7]), PathPriority.Private), out int id));
            q.Process(1);
            Assert.Equal(PathStatus.TooLong, q.GetStatus(id));
        }

        [Fact]
        public void Pending_requests_apply_backpressure_when_result_ring_is_full()
        {
            var (_, finder, a, b) = Setup();
            var q = new PathRequestQueue(finder, 4, 8);
            for (int i = 0; i < 4; i++) Assert.True(q.TryEnqueue(Req(a, b, PathPriority.Private), out _));
            Assert.False(q.TryEnqueue(Req(a, b, PathPriority.Private), out int none));
            Assert.Equal(0, none);
            q.Process(1);
            Assert.True(q.TryEnqueue(Req(a, b, PathPriority.Private), out _));
        }

        [Fact]
        public void Queue_grows_beyond_initial_capacity_preserving_order()
        {
            var (_, finder, a, b) = Setup();
            var q = new PathRequestQueue(finder, 512, 8);
            var ids = new int[300];
            for (int i = 0; i < ids.Length; i++) Assert.True(q.TryEnqueue(Req(a, b, PathPriority.Private), out ids[i]));
            Assert.Equal(300, q.Process(1000));
            for (int i = 0; i < ids.Length; i++) Assert.Equal(i + 1, q.GetCompletionOrder(ids[i]));
        }
    }
}
