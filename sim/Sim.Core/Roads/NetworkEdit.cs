using System;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;

namespace Urbe.Sim.Roads
{
    public enum ValidationResult : byte
    {
        Ok = 0,
        UnknownRoadType,
        OutOfBounds,
        /// <summary>Los dos extremos (o un cruce y un extremo) acaban en el mismo nodo.</summary>
        SameNode,
        TooShort,
        TooLong,
        TooManyCrossings,
        DuplicateSegment,
        NodeFull,
        AngleTooSharp,
        /// <summary>Solo lo detecta AddSegment al aplicar (se deshace solo); CanAddSegment no puede preverlo.</summary>
        TooManyConnections,
        /// <summary>El objetivo de un comando (segmento a demoler) no existe, esta repetido o no hay objetivos.</summary>
        InvalidTarget,
    }

    public readonly struct SegmentRequest
    {
        public readonly Vec2 Start;
        public readonly Vec2 C1;
        public readonly Vec2 C2;
        public readonly Vec2 End;
        public readonly float ElevStart;
        public readonly float ElevEnd;
        public readonly ushort RoadTypeId;

        public SegmentRequest(Vec2 start, Vec2 c1, Vec2 c2, Vec2 end, ushort roadTypeId, float elevStart = 0f, float elevEnd = 0f)
        {
            Start = start;
            C1 = c1;
            C2 = c2;
            End = end;
            RoadTypeId = roadTypeId;
            ElevStart = elevStart;
            ElevEnd = elevEnd;
        }

        /// <summary>Peticion recta (controles a 1/3 y 2/3).</summary>
        public static SegmentRequest Straight(Vec2 a, Vec2 b, ushort roadTypeId, float elevStart = 0f, float elevEnd = 0f)
        {
            return new SegmentRequest(a, Vec2.Lerp(a, b, 1f / 3f), Vec2.Lerp(a, b, 2f / 3f), b, roadTypeId, elevStart, elevEnd);
        }
    }

    /// <summary>Datos de un segmento eliminado: bastan para recrearlo con FindNodeAt/AddNodeRaw + AddSegmentRaw.</summary>
    public readonly struct SegmentRecord
    {
        public readonly Vec2 PosA;
        public readonly Vec2 PosB;
        public readonly float ElevA;
        public readonly float ElevB;
        public readonly Bezier3 Curve;
        public readonly ushort RoadTypeId;

        public SegmentRecord(Vec2 posA, Vec2 posB, float elevA, float elevB, in Bezier3 curve, ushort roadTypeId)
        {
            PosA = posA;
            PosB = posB;
            ElevA = elevA;
            ElevB = elevB;
            Curve = curve;
            RoadTypeId = roadTypeId;
        }
    }

    /// <summary>
    /// Registro de lo que hizo una mutacion de alto nivel (buffer reutilizable). Una entidad creada y eliminada
    /// dentro de la misma llamada no aparece en ninguna lista. Inverso: RemoveSegment de los creados y luego
    /// AddSegmentRaw de los registros.
    /// </summary>
    public sealed class NetworkEdit
    {
        private EntityId[] _nodes = new EntityId[16];
        private EntityId[] _segments = new EntityId[16];
        private SegmentRecord[] _removed = new SegmentRecord[16];
        private int _nodeCount;
        private int _segmentCount;
        private int _removedCount;

        public ReadOnlySpan<EntityId> CreatedNodes => new ReadOnlySpan<EntityId>(_nodes, 0, _nodeCount);
        public ReadOnlySpan<EntityId> CreatedSegments => new ReadOnlySpan<EntityId>(_segments, 0, _segmentCount);
        public ReadOnlySpan<SegmentRecord> RemovedSegments => new ReadOnlySpan<SegmentRecord>(_removed, 0, _removedCount);

        public void Clear()
        {
            _nodeCount = 0;
            _segmentCount = 0;
            _removedCount = 0;
        }

        internal void AddCreatedNode(EntityId id)
        {
            if (_nodeCount == _nodes.Length) Array.Resize(ref _nodes, _nodeCount * 2);
            _nodes[_nodeCount++] = id;
        }

        internal void AddCreatedSegment(EntityId id)
        {
            if (_segmentCount == _segments.Length) Array.Resize(ref _segments, _segmentCount * 2);
            _segments[_segmentCount++] = id;
        }

        internal void NodeRemoved(EntityId id)
        {
            RemoveFrom(_nodes, ref _nodeCount, id);
        }

        /// <summary>Si el segmento se creo en esta misma edicion se olvida; si no, se guarda su registro.</summary>
        internal void SegmentRemoved(EntityId id, in SegmentRecord record)
        {
            if (RemoveFrom(_segments, ref _segmentCount, id)) return;
            if (_removedCount == _removed.Length) Array.Resize(ref _removed, _removedCount * 2);
            _removed[_removedCount++] = record;
        }

        private static bool RemoveFrom(EntityId[] arr, ref int count, EntityId id)
        {
            for (int i = 0; i < count; i++)
            {
                if (arr[i] != id) continue;
                for (int j = i + 1; j < count; j++) arr[j - 1] = arr[j];
                count--;
                return true;
            }
            return false;
        }
    }
}
