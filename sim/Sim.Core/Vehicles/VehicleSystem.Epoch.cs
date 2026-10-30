using System;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Sim.Vehicles
{
    public sealed partial class VehicleSystem
    {
        /// <summary>
        /// Fase 0 (ADR 002 + 004): la red cambio de epoca. Consume los cambios (avisa a la puerta), da de baja a los vehiculos cuyo
        /// carril o conexion ya no existe, remapea las conexiones por (From, To), trunca las rutas rotas (NeedsReroute) y reconstruye
        /// las listas, las reservas y el rastreo de trasera. Solo ocurre tras ediciones de la red (puede asignar memoria).
        /// </summary>
        private void HandleEpoch()
        {
            _net.ConsumeChanges(out ReadOnlySpan<EntityId> changedNodes, out _);
            EnsureTrackCapacity();
            _gate.OnNetworkChanged(changedNodes);

            Array.Fill(_laneHead, -1);
            Array.Fill(_laneTail, -1);
            Array.Fill(_laneLast, -1);
            Array.Fill(_laneReservedCm, 0);
            Array.Fill(_connHead, -1);
            Array.Fill(_connTail, -1);

            RouteStep[] steps = _routes.Steps;
            for (int i = 0; i < _high; i++)
            {
                ref Vehicle v = ref _veh[i];
                if (!v.IsAlive) continue;
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
                        if (cursor >= 1 && _net.IsSegmentAlive(steps[off + cursor - 1].Segment))
                            pc = FindConn(steps[off + cursor - 1].Lane.Value, steps[off + cursor].Lane.Value);
                        if (pc < 0)
                        {
                            v.PrevConnection = -1;
                        }
                        else
                        {
                            v.PrevConnection = pc;
                        }
                    }
                }

                if (dead)
                {
                    DespawnedByEdit++;
                    FreeRecord(i);
                    continue;
                }

                // Ruta por delante: primer punto roto -> se trunca ahi y queda NeedsReroute.
                int last = v.RouteLast;
                int firstBad = -1;
                for (int k = cursor + (v.Kind == TrackKind.Connection ? 1 : 0); k <= last; k++)
                {
                    if (!_net.IsSegmentAlive(steps[off + k].Segment)) { firstBad = k; break; }
                    if (k < last && FindConn(steps[off + k].Lane.Value, steps[off + k + 1].Lane.Value) < 0) { firstBad = k + 1; break; }
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
