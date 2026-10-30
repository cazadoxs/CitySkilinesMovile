using System;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;

namespace Urbe.Sim.Roads
{
    // Operaciones estructurales internas (sin epoca): crear/quitar nodos y segmentos y regenerar conexiones.
    public sealed partial class RoadNetwork
    {
        private EntityId AddNodeInternal(Vec2 pos, float elevation, NetworkEdit? edit)
        {
            EntityId id = _nodeIds.Allocate();
            EnsureNodeCapacity(id.Index);
            if (id.Index >= _nodeHigh) _nodeHigh = id.Index + 1;
            ref Node n = ref _nodes[id.Index];
            n = default;
            n.Id = id;
            n.Position = pos;
            n.Elevation = elevation;
            n.Kind = JunctionKind.None;
            NodeGridAdd(id.Index);
            TouchNode(id);
            edit?.AddCreatedNode(id);
            return id;
        }

        private void RemoveNodeInternal(EntityId id, NetworkEdit? edit)
        {
            int ni = id.Index;
            NodeGridRemove(ni);
            ReleaseNodeBlock(ni);
            TouchNode(id);
            _nodes[ni] = default;
            _nodes[ni].Id = EntityId.None;
            _nodeIds.Free(id);
            edit?.NodeRemoved(id);
        }

        private void BuildLanes(int si)
        {
            ref readonly Segment sg = ref _segments[si];
            RoadType type = _types.Get(sg.RoadTypeId);
            int f = sg.LanesForward, b = sg.LanesBackward;
            float w = type.LaneWidth;
            for (int slot = 0; slot < f + b; slot++)
            {
                bool fwd = slot < f;
                int k = fwd ? slot : slot - f;
                float offset;
                if (b == 0) offset = ((f - 1) * 0.5f - k) * w;
                else if (fwd) offset = type.MedianWidth * 0.5f + (f - k - 0.5f) * w;
                else offset = -(type.MedianWidth * 0.5f + (b - k - 0.5f) * w);

                ref Lane l = ref _lanes[si * MaxLanesPerSegment + slot];
                l.Segment = sg.Id;
                l.Slot = (byte)slot;
                l.Direction = fwd ? LaneDirection.Forward : LaneDirection.Backward;
                l.Kind = LaneKind.Car;
                l.Offset = offset;
                l.Length = OffsetCurve.Length(sg.Curve, offset, sg.TLaneA, sg.TLaneB, PolylineSamples);
                l.SpeedLimit = type.SpeedLimit;
                l.ConnFirst = 0;
                l.ConnCount = 0;
            }
        }

        // ------------------------------------------------------------------ retranqueo

        /// <summary>Retranqueo minimo de un extremo en un nodo Junction (m).</summary>
        public const float MinSetback = 4f;

        /// <summary>
        /// Retranqueo del segmento <paramref name="si"/> en el nodo <paramref name="ni"/>: 0 salvo en nodos Junction (>= 3 segmentos), donde es
        /// max(TotalWidth/2 de los otros segmentos) + 1 m, al menos <see cref="MinSetback"/>, y como mucho la mitad del segmento menos 1,5 m
        /// (cada extremo se acota por separado para que el otro no dependa de este nodo). Solo depende del conjunto de segmentos del nodo.
        /// </summary>
        private float ComputeSetback(int ni, int si)
        {
            ref readonly Node nd = ref _nodes[ni];
            if (nd.SegmentCount < 3) return 0f;
            int adj = ni * MaxSegmentsPerNode;
            float half = 0f;
            for (int k = 0; k < nd.SegmentCount; k++)
            {
                int oi = _nodeSegments[adj + k].Index;
                if (oi == si) continue;
                float w = _types.Get(_segments[oi].RoadTypeId).TotalWidth * 0.5f;
                if (w > half) half = w;
            }
            float sb = half + 1f;
            if (sb < MinSetback) sb = MinSetback;
            float cap = _segments[si].Length * 0.5f - 1.5f;
            if (cap < 0f) cap = 0f;
            return sb < cap ? sb : cap;
        }

        /// <summary>
        /// Recalcula el retranqueo de los extremos de todos los segmentos del nodo (tras cambiar su conjunto de segmentos). Los segmentos que
        /// cambian se marcan como tocados y sus carriles se acortan o alargan; las conexiones del nodo las regenera el llamador.
        /// </summary>
        private void RefreshNodeGeometry(int ni)
        {
            int adj = ni * MaxSegmentsPerNode;
            int count = _nodes[ni].SegmentCount;
            for (int k = 0; k < count; k++)
            {
                EntityId sid = _nodeSegments[adj + k];
                int si = sid.Index;
                ref Segment sg = ref _segments[si];
                float sa = ComputeSetback(sg.NodeA.Index, si);
                float sbk = ComputeSetback(sg.NodeB.Index, si);
                if (sa == sg.SetbackA && sbk == sg.SetbackB) continue;
                TouchSegmentExisting(sid);
                sg.SetbackA = sa;
                sg.SetbackB = sbk;
                var table = new ReadOnlySpan<float>(_arcTables, sg.ArcTableOffset, ArcSamples);
                sg.TLaneA = sa > 0f ? ArcLengthTable.TAtDistance(sg.Curve, table, sa) : 0f;
                sg.TLaneB = sbk > 0f ? ArcLengthTable.TAtDistance(sg.Curve, table, sg.Length - sbk) : 1f;
                int lanes = sg.LanesForward + sg.LanesBackward;
                for (int slot = 0; slot < lanes; slot++)
                {
                    ref Lane l = ref _lanes[si * MaxLanesPerSegment + slot];
                    l.Length = OffsetCurve.Length(sg.Curve, l.Offset, sg.TLaneA, sg.TLaneB, PolylineSamples);
                }
            }
        }

        /// <summary>Crea la estructura del segmento (id, carriles, tabla de arco, rejilla, adyacencia) sin tocar conexiones.</summary>
        private EntityId InsertSegment(EntityId a, EntityId b, in Bezier3 curve, ushort typeId, SegmentFlags flags)
        {
            RoadType type = _types.Get(typeId);
            EntityId id = _segIds.Allocate();
            int si = id.Index;
            EnsureSegmentCapacity(si);
            if (si >= _segHigh) _segHigh = si + 1;
            ref Segment s = ref _segments[si];
            s.Id = id;
            s.NodeA = a;
            s.NodeB = b;
            s.Curve = curve;
            s.RoadTypeId = typeId;
            s.LanesForward = type.LanesForward;
            s.LanesBackward = type.LanesBackward;
            s.Flags = flags;
            s.ArcTableOffset = si * ArcSamples;
            var table = new Span<float>(_arcTables, s.ArcTableOffset, ArcSamples);
            ArcLengthTable.Build(curve, table);
            s.Length = ArcLengthTable.TotalLengthOf(table);
            s.SetbackA = 0f;
            s.SetbackB = 0f;
            s.TLaneA = 0f;
            s.TLaneB = 1f;
            BuildLanes(si);
            GridRegister(si, true);
            AdjInsert(a.Index, id);
            AdjInsert(b.Index, id);
            RefreshKind(a.Index);
            RefreshKind(b.Index);
            TouchSegmentNew(id);
            TouchNode(a);
            TouchNode(b);
            RefreshNodeGeometry(a.Index);
            RefreshNodeGeometry(b.Index);
            return id;
        }

        /// <summary>Deshace InsertSegment (libera el id). No toca conexiones ni nodos huerfanos.</summary>
        private void DetachSegment(EntityId id)
        {
            int si = id.Index;
            EntityId a = _segments[si].NodeA;
            EntityId b = _segments[si].NodeB;
            TouchSegmentExisting(id);
            GridRegister(si, false);
            AdjRemove(a.Index, id);
            AdjRemove(b.Index, id);
            for (int slot = 0; slot < MaxLanesPerSegment; slot++)
            {
                ref Lane l = ref _lanes[si * MaxLanesPerSegment + slot];
                l = default;
                l.Segment = EntityId.None;
            }
            _segments[si] = default;
            _segments[si].Id = EntityId.None;
            _segIds.Free(id);
            RefreshKind(a.Index);
            RefreshKind(b.Index);
            TouchNode(a);
            TouchNode(b);
            RefreshNodeGeometry(a.Index);
            RefreshNodeGeometry(b.Index);
        }

        /// <summary>Alta completa con comprobaciones y conexiones. Devuelve None (sin cambios de estado) si se rechaza.</summary>
        private EntityId AddSegmentInternal(EntityId a, EntityId b, in Bezier3 curve, ushort typeId, SegmentFlags flags, NetworkEdit? edit)
        {
            if (!_types.IsValidId(typeId)) return EntityId.None;
            if (!_nodeIds.IsAlive(a) || !_nodeIds.IsAlive(b) || a == b) return EntityId.None;
            if (FindSegmentBetween(a, b).IsValid) return EntityId.None;
            if (_nodes[a.Index].SegmentCount >= MaxSegmentsPerNode || _nodes[b.Index].SegmentCount >= MaxSegmentsPerNode) return EntityId.None;

            uint verA = _nodes[a.Index].Version, verB = _nodes[b.Index].Version;
            EntityId id = InsertSegment(a, b, curve, typeId, flags);
            int ca = GenerateNode(a.Index, _genA, out bool ofA);
            int cb = GenerateNode(b.Index, _genB, out bool ofB);
            if (ofA || ofB)
            {
                DetachSegment(id);
                // Rechazo sin cambios: los nodos vuelven a su version (no hay epoca nueva, asi que nadie debe ver un cambio).
                _nodes[a.Index].Version = verA;
                _nodes[b.Index].Version = verB;
                return EntityId.None;
            }
            CommitNode(a.Index, _genA, ca);
            CommitNode(b.Index, _genB, cb);
            edit?.AddCreatedSegment(id);
            return id;
        }

        private SegmentRecord RecordOf(int si)
        {
            ref readonly Segment s = ref _segments[si];
            ref readonly Node na = ref _nodes[s.NodeA.Index];
            ref readonly Node nb = ref _nodes[s.NodeB.Index];
            return new SegmentRecord(na.Position, nb.Position, na.Elevation, nb.Elevation, s.Curve, s.RoadTypeId);
        }

        /// <summary>Elimina el segmento y los nodos que queden huerfanos; regenera los demas.</summary>
        private void RemoveSegmentInternal(EntityId id, NetworkEdit? edit)
        {
            int si = id.Index;
            EntityId a = _segments[si].NodeA;
            EntityId b = _segments[si].NodeB;
            if (edit != null)
            {
                SegmentRecord rec = RecordOf(si);
                edit.SegmentRemoved(id, rec);
            }
            DetachSegment(id);
            AfterDetach(a, edit);
            AfterDetach(b, edit);
        }

        private void AfterDetach(EntityId node, NetworkEdit? edit)
        {
            if (_nodes[node.Index].SegmentCount == 0) RemoveNodeInternal(node, edit);
            else RebuildNodeForced(node.Index);
        }

        /// <summary>Divide el segmento en t: nodo Continuation nuevo y dos mitades que heredan tipo y flags.</summary>
        private void SplitInternal(EntityId seg, float t, NetworkEdit? edit, out EntityId node, out EntityId half1, out EntityId half2)
        {
            int si = seg.Index;
            EntityId a = _segments[si].NodeA;
            EntityId b = _segments[si].NodeB;
            ushort typeId = _segments[si].RoadTypeId;
            SegmentFlags flags = _segments[si].Flags;
            Bezier3 curve = _segments[si].Curve;
            float elev = _nodes[a.Index].Elevation + (_nodes[b.Index].Elevation - _nodes[a.Index].Elevation) * t;
            curve.Split(t, out Bezier3 c1, out Bezier3 c2);
            float oldLength = _segments[si].Length;
            float cut = DistanceAtParam(si, t);
            byte lanesForward = _segments[si].LanesForward;

            if (edit != null)
            {
                SegmentRecord rec = RecordOf(si);
                edit.SegmentRemoved(seg, rec);
            }
            DetachSegment(seg);
            node = AddNodeInternal(c1.P3, elev, edit);
            half1 = InsertSegment(a, node, c1, typeId, flags);
            half2 = InsertSegment(node, b, c2, typeId, flags);
            LogSplit(seg, half1, half2, node, cut, oldLength, lanesForward);
            edit?.AddCreatedSegment(half1);
            edit?.AddCreatedSegment(half2);
            RebuildNodeForced(a.Index);
            RebuildNodeForced(b.Index);
            RebuildNodeForced(node.Index);
        }

        /// <summary>Deshace lo registrado en <paramref name="edit"/> (para la reversion interna tras un fallo).</summary>
        private void UndoEdit(NetworkEdit edit)
        {
            ReadOnlySpan<EntityId> created = edit.CreatedSegments;
            for (int i = created.Length - 1; i >= 0; i--)
            {
                if (_segIds.IsAlive(created[i])) RemoveSegmentInternal(created[i], null);
            }
            ReadOnlySpan<SegmentRecord> removed = edit.RemovedSegments;
            for (int i = 0; i < removed.Length; i++)
            {
                SegmentRecord r = removed[i];
                EntityId na = FindNodeAt(r.PosA);
                if (!na.IsValid) na = AddNodeInternal(r.PosA, r.ElevA, null);
                EntityId nb = FindNodeAt(r.PosB);
                if (!nb.IsValid) nb = AddNodeInternal(r.PosB, r.ElevB, null);
                AddSegmentInternal(na, nb, r.Curve, r.RoadTypeId, SegmentFlags.None, null);
            }
            ReadOnlySpan<EntityId> nodes = edit.CreatedNodes;
            for (int i = 0; i < nodes.Length; i++)
            {
                if (_nodeIds.IsAlive(nodes[i]) && _nodes[nodes[i].Index].SegmentCount == 0) RemoveNodeInternal(nodes[i], null);
            }
        }

        private void FinishMutation()
        {
            _epoch++;
            if (++_mutStamp == int.MaxValue)
            {
                Array.Clear(_nodeTouchStamp, 0, _nodeTouchStamp.Length);
                Array.Clear(_segTouchStamp, 0, _segTouchStamp.Length);
                _mutStamp = 1;
            }
            MaybeCompactConnections();
        }
    }
}
