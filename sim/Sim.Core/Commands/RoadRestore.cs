using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Sim.Commands
{
    internal static class RoadRestore
    {
        /// <summary>Recrea segmentos eliminados: reutiliza el nodo en esa posicion exacta o lo crea con AddNodeRaw.</summary>
        public static void Restore(RoadNetwork network, SegmentRecord[] records)
        {
            for (int i = records.Length - 1; i >= 0; i--)
            {
                SegmentRecord r = records[i];
                EntityId a = NodeFor(network, r.PosA, r.ElevA);
                EntityId b = NodeFor(network, r.PosB, r.ElevB);
                network.AddSegmentRaw(a, b, r.Curve, r.RoadTypeId);
            }
        }

        /// <summary>Segmento vivo con exactamente la geometria del registro (los ids cambian al deshacer/rehacer); None si no hay.</summary>
        public static EntityId Find(RoadNetwork network, in SegmentRecord r)
        {
            EntityId a = network.FindNodeAt(r.PosA);
            if (!a.IsValid) return EntityId.None;
            foreach (EntityId s in network.GetNodeSegments(a))
            {
                ref readonly Segment seg = ref network.GetSegment(s);
                if (seg.RoadTypeId != r.RoadTypeId || seg.NodeA != a) continue;
                if (seg.Curve.P0 == r.Curve.P0 && seg.Curve.P1 == r.Curve.P1 && seg.Curve.P2 == r.Curve.P2 && seg.Curve.P3 == r.Curve.P3
                    && network.GetNode(seg.NodeB).Position == r.PosB) return s;
            }
            return EntityId.None;
        }

        public static SegmentRecord Capture(RoadNetwork network, EntityId id)
        {
            ref readonly Segment seg = ref network.GetSegment(id);
            ref readonly Node a = ref network.GetNode(seg.NodeA);
            ref readonly Node b = ref network.GetNode(seg.NodeB);
            return new SegmentRecord(a.Position, b.Position, a.Elevation, b.Elevation, seg.Curve, seg.RoadTypeId);
        }

        private static EntityId NodeFor(RoadNetwork network, Vec2 pos, float elev)
        {
            EntityId id = network.FindNodeAt(pos);
            return id.IsValid ? id : network.AddNodeRaw(pos, elev);
        }
    }
}
