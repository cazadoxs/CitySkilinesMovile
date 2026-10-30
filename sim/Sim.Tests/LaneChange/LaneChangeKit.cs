using System;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Urbe.Sim.Traffic;
using Urbe.Sim.Vehicles;
using Xunit;

namespace Urbe.Sim.Tests.LaneChange
{
    internal static class LCK
    {
        /// <summary>
        /// Cuadricula como la del Runner: una avenida (2+2) cada tres lineas en ambos ejes y calles (1+1) en el resto; las avenidas
        /// atraen mas viajes.
        /// </summary>
        public static TrafficSimulation Grid(ulong seed, float tripsPerSecond, int cells, int maxVehicles, bool laneChange = true, float spacing = 100f)
        {
            var net = new RoadNetwork(RoadTypeCatalog.CreateDefault(), new NetworkConfig());
            int m = cells + 1;
            var nodes = new EntityId[m, m];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++)
                    nodes[i, j] = net.AddNodeRaw(new Vec2(i * spacing, j * spacing), 0f);
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++)
                {
                    if (i + 1 < m) Add(net, nodes[i, j], nodes[i + 1, j], j % 3 == 0 ? RoadTypeIds.Avenue4 : RoadTypeIds.Street2);
                    if (j + 1 < m) Add(net, nodes[i, j], nodes[i, j + 1], i % 3 == 0 ? RoadTypeIds.Avenue4 : RoadTypeIds.Street2);
                }
            var opt = new TrafficSimulationOptions { VehicleCapacity = Math.Max(maxVehicles * 2, 256) };
            opt.Vehicle = new VehicleConfig { DriverVariation = true, LaneChangeEnabled = laneChange };
            opt.Trips.TripsPerSecond = tripsPerSecond;
            opt.Trips.MaxActiveVehicles = maxVehicles;
            var sim = new TrafficSimulation(net, seed, opt);
            for (int i = 0; i < net.SegmentIndexBound; i++)
            {
                EntityId seg = net.SegmentIdAt(i);
                if (!seg.IsValid) continue;
                bool avenue = net.GetSegment(seg).RoadTypeId == RoadTypeIds.Avenue4;
                sim.Trips.AddDemandPoint(seg, 1f, avenue ? 3f : 1f);
            }
            return sim;
        }

        private static void Add(RoadNetwork net, EntityId a, EntityId b, ushort type)
        {
            net.AddSegmentRaw(a, b, Bezier3.FromLine(net.GetNode(a).Position, net.GetNode(b).Position), type);
        }

        /// <summary>
        /// Comprueba las listas de cada carril: orden por S, hueco entre vehiculos consecutivos &gt;= MinGap y, si hay un cambio de
        /// carril en curso, hueco del seguidor del carril viejo respecto al fantasma. Devuelve el menor hueco visto.
        /// </summary>
        public static float CheckNoOverlap(VehicleSystem s, ref string? error)
        {
            RoadNetwork n = s.Network;
            float min = float.PositiveInfinity;
            float tol = s.Config.MinGap - 1e-3f;
            int lanes = n.SegmentIndexBound * RoadNetwork.MaxLanesPerSegment;
            for (int lane = 0; lane < lanes; lane++)
            {
                var id = new LaneId(lane);
                int head = s.LaneHead(id);
                int prev = -1;
                for (int i = head; i >= 0; i = s.GetVehicle(i).Behind)
                {
                    ref readonly Vehicle v = ref s.GetVehicle(i);
                    if (prev >= 0)
                    {
                        ref readonly Vehicle a = ref s.GetVehicle(prev);
                        float gap = a.S - s.GetVehicleType(a.Type).Length - v.S;
                        if (gap < min) min = gap;
                        if (gap < tol && error == null)
                            error = "solape en carril " + id + ": hueco " + gap + " entre " + prev + " (S=" + a.S + ") y " + i + " (S=" + v.S + ") tick " + s.CurrentTick;
                    }
                    prev = i;
                }
                for (int g = s.LaneChangeGhost(id); g >= 0; g = s.GetVehicle(g).GhostNext)
                {
                    // Seguidor mas cercano por detras del fantasma en este carril.
                    ref readonly Vehicle gv = ref s.GetVehicle(g);
                    float ratio = n.GetLane(id).Length / n.GetLane(new LaneId(gv.Track)).Length;
                    float gs = gv.S * ratio;
                    int nearest = -1;
                    for (int i = head; i >= 0; i = s.GetVehicle(i).Behind)
                    {
                        if (s.GetVehicle(i).S < gs) { nearest = i; break; }
                    }
                    if (nearest < 0) continue;
                    float gg = gs - s.GetVehicleType(gv.Type).Length - s.GetVehicle(nearest).S;
                    if (gg < min) min = gg;
                    if (gg < tol && error == null)
                        error = "solape con fantasma en " + id + ": hueco " + gg + " tick " + s.CurrentTick;
                }
            }
            return min;
        }
    }
}
