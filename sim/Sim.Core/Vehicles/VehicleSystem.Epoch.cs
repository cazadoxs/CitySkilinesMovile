using System;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Sim.Vehicles
{
    public sealed partial class VehicleSystem
    {
        private int _netSub = -1;

        // Recolocacion tras una edicion (M1-15).
        private RouteStep[] _expScratch = new RouteStep[RoutePool.MaxBlock + 8];
        private EntityId[] _pcSeg = new EntityId[16];
        private float[] _pcB0 = new float[16];
        private float[] _pcB1 = new float[16];
        private int _pcCount;
        private byte _pcLanesForward;
        private int[] _segRemapStamp = new int[16];
        private int _remapStamp;

        /// <summary>
        /// Se invoca (vehiculo, segmento nuevo) cuando la ruta de un vehiculo que NO esta pendiente de re-ruta acaba en un segmento
        /// dividido: el destino pasa a ser la mitad que contiene su punto de llegada (el generador de viajes actualiza el suyo).
        /// </summary>
        public event Action<int, EntityId>? DestinationSegmentChanged;

        /// <summary>Vehiculos cuyo segmento se dividio y que se recolocaron en la mitad que les corresponde (M1-15).</summary>
        public int RemappedBySplit { get; private set; }
        /// <summary>Vehiculos cuya posicion se remapeo por un cambio de retranqueo de su carril (M1-15).</summary>
        public int RemappedBySetback { get; private set; }
        /// <summary>Bajas por edicion causadas por no caber tras un cambio de retranqueo (ultimo recurso, ya incluidas en <see cref="DespawnedByEdit"/>).</summary>
        public int DespawnedBySetback { get; private set; }

        /// <summary>
        /// Fase 0 (ADR 002 + 004): la red cambio de epoca. Consume los cambios de este sistema (cursor propio en el registro de la red),
        /// avisa a la puerta, recoloca a los vehiculos de los segmentos divididos en la mitad correcta (reescribiendo sus rutas), remapea
        /// la posicion en los carriles cuyo retranqueo cambio, da de baja a los vehiculos cuyo carril o conexion ya no existe, remapea las
        /// conexiones por (From, To), trunca las rutas rotas (NeedsReroute) y reconstruye las listas, las reservas y el rastreo de trasera.
        /// Solo ocurre tras ediciones de la red (puede asignar memoria).
        /// </summary>
        private void HandleEpoch()
        {
            _net.ConsumeChanges(_netSub, out ReadOnlySpan<EntityId> changedNodes, out ReadOnlySpan<EntityId> changedSegs, out ReadOnlySpan<SegmentSplit> splits);
            EnsureTrackCapacity();
            _gate.OnNetworkChanged(changedNodes);
            MarkRemappedSegments(changedSegs);

            Array.Fill(_laneHead, -1);
            Array.Fill(_laneTail, -1);
            Array.Fill(_laneLast, -1);
            Array.Fill(_laneReservedCm, 0);
            Array.Fill(_laneGhost, -1);
            Array.Fill(_connHead, -1);
            Array.Fill(_connTail, -1);

            for (int i = 0; i < _high; i++)
            {
                ref Vehicle v = ref _veh[i];
                if (!v.IsAlive) continue;
                bool movedPastFirstPiece = false;
                if (splits.Length > 0 && RouteHasSplitStep(i, splits))
                {
                    if (RewriteRouteForSplits(i, splits, out movedPastFirstPiece)) RemappedBySplit++;
                }
                if (_remapStamp != 0) RemapOnRetainedSegments(i);

                RouteStep[] steps = _routes.Steps;
                int off = v.RouteOffset;
                int cursor = v.RouteCursor;
                bool dead = !_net.IsSegmentAlive(steps[off + cursor].Segment);

                if (!dead && v.Kind == TrackKind.Connection)
                {
                    // El destino es cursor + 1; lo normal es que exista y la conexion se remapee por (From, To).
                    int nc = -1;
                    if (cursor + 1 <= v.RouteLast && _net.IsSegmentAlive(steps[off + cursor + 1].Segment))
                        nc = FindConn(steps[off + cursor].Lane.Value, steps[off + cursor + 1].Lane.Value);
                    if (nc < 0) dead = true;
                    else
                    {
                        v.Track = nc;
                        float cl = _net.GetConnection(nc).Length;
                        if (v.S > cl) v.S = cl;
                    }
                }
                else if (!dead)
                {
                    float ll = _net.GetLane(new LaneId(v.Track)).Length;
                    if (v.S > ll) v.S = ll;
                    if (v.PrevConnection >= 0)
                    {
                        int pc = -1;
                        if (!movedPastFirstPiece && cursor >= 1 && _net.IsSegmentAlive(steps[off + cursor - 1].Segment))
                            pc = FindConn(steps[off + cursor - 1].Lane.Value, steps[off + cursor].Lane.Value);
                        v.PrevConnection = pc;
                    }
                }

                if (dead)
                {
                    DespawnedByEdit++;
                    FreeRecord(i);
                    continue;
                }
                // Las maniobras de cambio de carril en curso se dan por terminadas (los fantasmas se reinician).
                v.LcFrom = -1;
                v.GhostNext = -1;
                v.LcTicks = 0;

                // Ruta por delante: primer punto roto -> se trunca ahi y queda NeedsReroute.
                int last = v.RouteLast;
                int firstBad = -1;
                for (int k = cursor + (v.Kind == TrackKind.Connection ? 1 : 0); k <= last; k++)
                {
                    if (!_net.IsSegmentAlive(steps[off + k].Segment)) { firstBad = k; break; }
                    if (k < last && NearestDeparture(steps[off + k].Lane.Value, steps[off + k + 1].Lane.Value) < 0) { firstBad = k + 1; break; }
                }
                if (firstBad >= 0)
                {
                    v.RouteLast = (ushort)(firstBad - 1);
                    v.Flags |= VehicleFlags.NeedsReroute;
                    float stop = _net.GetLane(steps[off + v.RouteLast].Lane).Length - _cfg.StopMargin - _types[v.Type].S0;
                    v.TargetS = stop > 0f ? stop : 0f;
                }

                // Permisos: se retiran los de carriles que acaban en un nodo cambiado (la puerta los olvido) y se conservan el resto.
                if (v.Kind == TrackKind.Lane && (v.Flags & VehicleFlags.HasEntryPermit) != 0)
                {
                    bool keep = v.RouteCursor < v.RouteLast;
                    if (keep)
                    {
                        ref readonly Lane l = ref _net.GetLane(new LaneId(v.Track));
                        ref readonly Segment sg = ref _net.GetSegment(l.Segment);
                        EntityId endNode = l.Direction == LaneDirection.Forward ? sg.NodeB : sg.NodeA;
                        keep = !ContainsNode(changedNodes, endNode);
                    }
                    if (!keep)
                    {
                        v.Flags &= ~VehicleFlags.HasEntryPermit;
                        v.ReservedCm = 0;
                    }
                }
                if ((v.Flags & VehicleFlags.HasEntryPermit) != 0)
                    _laneReservedCm[steps[off + v.RouteCursor + 1].Lane.Value] += v.ReservedCm;

                InsertSorted(i);
            }

            if (_remapStamp != 0) ResolveOverlaps(changedSegs);

            // Rastreo de trasera y ocupacion de la puerta para los vehiculos sobre conexiones.
            for (int i = 0; i < _high; i++)
            {
                ref Vehicle v = ref _veh[i];
                if (!v.IsAlive) continue;
                int conn = v.Kind == TrackKind.Connection ? v.Track : v.PrevConnection;
                if (conn < 0) continue;
                ref readonly LaneConnection c = ref _net.GetConnection(conn);
                int from = c.From.Value;
                float progress = (v.Kind == TrackKind.Connection ? v.S : c.Length + v.S) - _types[v.Type].Length;
                int cur = _laneLast[from];
                if (cur < 0 || progress < RearProgress(cur)) _laneLast[from] = i;
                if (ContainsNode(changedNodes, c.Node)) _gate.OnEntered(conn, i);
            }

            _epoch = _net.Epoch;
        }

        private float RearProgress(int vi)
        {
            ref Vehicle v = ref _veh[vi];
            int conn = v.Kind == TrackKind.Connection ? v.Track : v.PrevConnection;
            float cl = v.Kind == TrackKind.Connection ? 0f : _net.GetConnection(conn).Length;
            return cl + v.S - _types[v.Type].Length;
        }

        // ------------------------------------------------------------------ retranqueo (punto 5)

        private const float RemapEps = 1e-4f;

        /// <summary>Marca los segmentos que siguen vivos y cuyo retranqueo o longitud de carril cambio (sus S hay que remapear).</summary>
        private void MarkRemappedSegments(ReadOnlySpan<EntityId> changedSegs)
        {
            _remapStamp = 0;
            int bound = _net.SegmentIndexBound;
            if (_segRemapStamp.Length < bound) Array.Resize(ref _segRemapStamp, Math.Max(bound, _segRemapStamp.Length * 2));
            Array.Clear(_segRemapStamp, 0, _segRemapStamp.Length);
            for (int k = 0; k < changedSegs.Length; k++)
            {
                EntityId id = changedSegs[k];
                if (!_net.IsSegmentAlive(id) || !_net.TryGetPreState(id, out SegmentPreState pre)) continue;
                ref readonly Segment sg = ref _net.GetSegment(id);
                bool changed = Math.Abs(pre.SetbackA - sg.SetbackA) > RemapEps || Math.Abs(pre.SetbackB - sg.SetbackB) > RemapEps;
                int lanes = sg.LanesForward + sg.LanesBackward;
                for (int slot = 0; slot < lanes && !changed; slot++)
                    changed = Math.Abs(pre.LaneLength(slot) - _net.GetLane(new LaneId(id.Index, slot)).Length) > RemapEps;
                if (!changed) continue;
                _segRemapStamp[id.Index] = 1;
                _remapStamp = 1;
            }
        }

        /// <summary>Distancia sobre la curva central correspondiente a la posicion s de un carril con el estado previo dado.</summary>
        private static float ToCentral(in SegmentPreState pre, int slot, bool forward, float s)
        {
            float span = pre.Length - pre.SetbackA - pre.SetbackB;
            float laneLen = pre.LaneLength(slot);
            if (span < 1e-3f || laneLen < 1e-3f) return forward ? pre.SetbackA : pre.SetbackA + span;
            float r = s / laneLen;
            if (r < 0f) r = 0f; else if (r > 1f) r = 1f;
            return forward ? pre.SetbackA + r * span : pre.SetbackA + span - r * span;
        }

        /// <summary>Posicion en el carril (acotada a [0, Length]) de la distancia <paramref name="u"/> sobre la curva central del segmento vivo.</summary>
        private float FromCentral(EntityId seg, int slot, bool forward, float u)
        {
            ref readonly Segment sg = ref _net.GetSegment(seg);
            float laneLen = _net.GetLane(new LaneId(seg.Index, slot)).Length;
            float span = sg.Length - sg.SetbackA - sg.SetbackB;
            if (span < 1e-3f) return 0f;
            float x = forward ? u - sg.SetbackA : sg.SetbackA + span - u;
            float s = x * laneLen / span;
            if (s < 0f) s = 0f; else if (s > laneLen) s = laneLen;
            return s;
        }

        /// <summary>Segmento vivo con retranqueo distinto: remapea S (si esta en un carril suyo) y TargetS (si el paso final es suyo).</summary>
        private void RemapOnRetainedSegments(int vi)
        {
            ref Vehicle v = ref _veh[vi];
            RouteStep[] steps = _routes.Steps;
            int off = v.RouteOffset;
            bool moved = false;
            if (v.Kind == TrackKind.Lane)
            {
                LaneId lane = new LaneId(v.Track);
                if (lane.SegmentIndex < _segRemapStamp.Length && _segRemapStamp[lane.SegmentIndex] != 0)
                {
                    EntityId seg = steps[off + v.RouteCursor].Segment;
                    if (_net.IsSegmentAlive(seg) && _net.TryGetPreState(seg, out SegmentPreState pre))
                    {
                        bool fwd = _net.GetLane(lane).Direction == LaneDirection.Forward;
                        float u = ToCentral(pre, lane.Slot, fwd, v.S);
                        v.S = FromCentral(seg, lane.Slot, fwd, u);
                        moved = true;
                    }
                }
            }
            RouteStep lastStep = steps[off + v.RouteLast];
            if (lastStep.Lane.SegmentIndex < _segRemapStamp.Length && _segRemapStamp[lastStep.Lane.SegmentIndex] != 0
                && _net.IsSegmentAlive(lastStep.Segment) && _net.TryGetPreState(lastStep.Segment, out SegmentPreState lp))
            {
                bool fwd = _net.GetLane(lastStep.Lane).Direction == LaneDirection.Forward;
                float u = ToCentral(lp, lastStep.Lane.Slot, fwd, v.TargetS);
                v.TargetS = FromCentral(lastStep.Segment, lastStep.Lane.Slot, fwd, u);
            }
            if (moved) RemappedBySetback++;
        }

        /// <summary>
        /// Tras remapear, vuelve a separar a los vehiculos de los carriles cuyo retranqueo cambio: cada uno como mucho a (trasera del de
        /// delante - hueco minimo). El que no cabe (quedaria con el frente antes del inicio del carril) se da de baja como ultimo recurso.
        /// </summary>
        private void ResolveOverlaps(ReadOnlySpan<EntityId> changedSegs)
        {
            for (int k = 0; k < changedSegs.Length; k++)
            {
                EntityId seg = changedSegs[k];
                if (!_net.IsSegmentAlive(seg) || seg.Index >= _segRemapStamp.Length || _segRemapStamp[seg.Index] == 0) continue;
                int lanes = _net.GetLaneCount(seg);
                for (int slot = 0; slot < lanes; slot++)
                {
                    int lane = seg.Index * RoadNetwork.MaxLanesPerSegment + slot;
                    int ahead = _laneHead[lane];
                    if (ahead < 0) continue;
                    float len = _net.GetLane(new LaneId(lane)).Length;
                    if (_veh[ahead].S > len) _veh[ahead].S = len;
                    int cur = _veh[ahead].Behind;
                    while (cur >= 0)
                    {
                        int next = _veh[cur].Behind;
                        float max = _veh[ahead].S - _types[_veh[ahead].Type].Length - _cfg.MinGap;
                        if (_veh[cur].S > max)
                        {
                            if (max < 0f)
                            {
                                RemoveVehicle(cur);
                                DespawnedByEdit++;
                                DespawnedBySetback++;
                                cur = next;
                                continue;
                            }
                            _veh[cur].S = max;
                        }
                        ahead = cur;
                        cur = next;
                    }
                }
            }
        }

        // ------------------------------------------------------------------ segmentos divididos (punto 4)

        private static int FindSplit(EntityId old, ReadOnlySpan<SegmentSplit> splits)
        {
            for (int i = 0; i < splits.Length; i++)
                if (splits[i].Old == old) return i;
            return -1;
        }

        private bool RouteHasSplitStep(int vi, ReadOnlySpan<SegmentSplit> splits)
        {
            ref Vehicle v = ref _veh[vi];
            RouteStep[] steps = _routes.Steps;
            int off = v.RouteOffset;
            for (int k = 0; k <= v.RouteLast; k++)
            {
                EntityId seg = steps[off + k].Segment;
                if (!_net.IsSegmentAlive(seg) && FindSplit(seg, splits) >= 0) return true;
            }
            return false;
        }

        /// <summary>Trozos vivos en los que quedo el segmento eliminado, de A a B (relleno de _pc*). False si alguna mitad se elimino despues.</summary>
        private bool ExpandPieces(EntityId seg, ReadOnlySpan<SegmentSplit> splits, float b0)
        {
            if (_net.IsSegmentAlive(seg))
            {
                if (_pcCount == _pcSeg.Length)
                {
                    int n = _pcCount * 2;
                    Array.Resize(ref _pcSeg, n);
                    Array.Resize(ref _pcB0, n);
                    Array.Resize(ref _pcB1, n);
                }
                _pcSeg[_pcCount] = seg;
                _pcB0[_pcCount] = b0;
                _pcB1[_pcCount] = b0 + _net.GetSegment(seg).Length;
                _pcCount++;
                return true;
            }
            int si = FindSplit(seg, splits);
            if (si < 0) return false;
            SegmentSplit sp = splits[si];
            _pcLanesForward = sp.LanesForward;
            return ExpandPieces(sp.Half1, splits, b0) && ExpandPieces(sp.Half2, splits, b0 + sp.CutDistance);
        }

        /// <summary>Indice (en orden de marcha) del trozo que contiene la distancia u sobre la curva central.</summary>
        private int PieceIndex(float u, bool forward)
        {
            int np = _pcCount;
            for (int j = 0; j < np - 1; j++)
            {
                int p = forward ? j : np - 1 - j;
                if (forward ? u <= _pcB1[p] : u >= _pcB0[p]) return j;
            }
            return np - 1;
        }

        /// <summary>
        /// Reescribe la ruta del vehiculo sustituyendo cada paso sobre un segmento dividido por sus trozos (un paso pasa a dos o mas) y
        /// recoloca al vehiculo (y su punto de llegada) en el trozo que le corresponde por distancia de arco. Si no se puede (el segmento
        /// o una mitad desaparecio, el estado previo no esta) el paso queda tal cual y el flujo normal lo trunca o da de baja.
        /// </summary>
        private bool RewriteRouteForSplits(int vi, ReadOnlySpan<SegmentSplit> splits, out bool movedPastFirstPiece)
        {
            movedPastFirstPiece = false;
            ref Vehicle v = ref _veh[vi];
            RouteStep[] steps = _routes.Steps;
            int off = v.RouteOffset;
            int cursor = v.RouteCursor;
            int last = v.RouteLast;
            bool isConn = v.Kind == TrackKind.Connection;
            int cnt = 0, newCursor = -1, newLast = -1;
            float newS = v.S, newTarget = v.TargetS;
            int newTrack = v.Track;
            bool overflow = false;

            for (int k = 0; k <= last && !overflow; k++)
            {
                RouteStep st = steps[off + k];
                bool resolved = false;
                if (!_net.IsSegmentAlive(st.Segment) && FindSplit(st.Segment, splits) >= 0)
                {
                    _pcCount = 0;
                    resolved = ExpandPieces(st.Segment, splits, 0f);
                }
                if (!resolved)
                {
                    if (cnt == RoutePool.MaxBlock) { overflow = true; break; }
                    _expScratch[cnt] = st;
                    if (k == cursor) newCursor = cnt;
                    if (k == last) newLast = cnt;
                    cnt++;
                    continue;
                }

                int np = _pcCount;
                int slot = st.Lane.Slot;
                bool fwd = slot < _pcLanesForward;
                int curJ = -1, tgtJ = np - 1;
                float uCur = 0f, uTgt = 0f;
                bool havePre = _net.TryGetPreState(st.Segment, out SegmentPreState pre);
                if (k == cursor && !isConn)
                {
                    if (havePre)
                    {
                        uCur = ToCentral(pre, slot, fwd, v.S);
                        curJ = PieceIndex(uCur, fwd);
                    }
                    else
                    {
                        curJ = 0;
                    }
                }
                if (k == last && havePre)
                {
                    uTgt = ToCentral(pre, slot, fwd, v.TargetS);
                    tgtJ = PieceIndex(uTgt, fwd);
                    if (curJ > tgtJ) tgtJ = curJ;
                }
                else if (k == last)
                {
                    tgtJ = np - 1;
                }

                for (int j = 0; j <= tgtJ; j++)
                {
                    if (cnt == RoutePool.MaxBlock) { overflow = true; break; }
                    int p = fwd ? j : np - 1 - j;
                    var lane = new LaneId(_pcSeg[p].Index, slot);
                    _expScratch[cnt] = new RouteStep(_pcSeg[p], lane);
                    if (k == cursor && ((isConn && j == np - 1) || (!isConn && j == curJ)))
                    {
                        newCursor = cnt;
                        if (!isConn)
                        {
                            newTrack = lane.Value;
                            newS = havePre ? FromCentral(_pcSeg[p], slot, fwd, uCur - _pcB0[p]) : v.S;
                            if (j > 0) movedPastFirstPiece = true;
                        }
                    }
                    if (k == last && j == tgtJ)
                    {
                        newLast = cnt;
                        if (havePre) newTarget = FromCentral(_pcSeg[p], slot, fwd, uTgt - _pcB0[p]);
                    }
                    cnt++;
                }
            }

            if (newCursor < 0 || newLast < 0 || newLast < newCursor)
            {
                // Rutas truncadas por desbordamiento o cursor no resuelto: se deja la ruta como estaba.
                movedPastFirstPiece = false;
                return false;
            }

            int noff = _routes.Allocate(cnt);
            if (noff < 0) { movedPastFirstPiece = false; return false; }
            RouteStep[] ns = _routes.Steps;
            Array.Copy(_expScratch, 0, ns, noff, cnt);
            EntityId oldDest = steps[off + last].Segment;
            EntityId newDest = _expScratch[newLast].Segment;
            if (oldDest != newDest && (v.Flags & VehicleFlags.NeedsReroute) == 0) DestinationSegmentChanged?.Invoke(vi, newDest);
            _routes.Free(v.RouteOffset, v.RouteLength);
            v.RouteOffset = noff;
            v.RouteLength = (ushort)cnt;
            v.RouteCursor = (ushort)newCursor;
            v.RouteLast = (ushort)newLast;
            v.S = newS;
            v.TargetS = newTarget;
            v.Track = newTrack;
            return true;
        }

        /// <summary>Busqueda binaria por indice en la lista de nodos cambiados (ordenada por indice y generacion).</summary>
        private static bool ContainsNode(ReadOnlySpan<EntityId> sorted, EntityId id)
        {
            int lo = 0, hi = sorted.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int idx = sorted[mid].Index;
                if (idx < id.Index) lo = mid + 1;
                else if (idx > id.Index) hi = mid - 1;
                else
                {
                    // Puede haber varias generaciones con el mismo indice: busca hacia ambos lados.
                    for (int k = mid; k >= 0 && sorted[k].Index == id.Index; k--) if (sorted[k] == id) return true;
                    for (int k = mid + 1; k < sorted.Length && sorted[k].Index == id.Index; k++) if (sorted[k] == id) return true;
                    return false;
                }
            }
            return false;
        }
    }
}
