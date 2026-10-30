using Urbe.Sim.Junctions;
using Urbe.Sim.Roads;
using Urbe.Sim.Vehicles;

namespace Urbe.Sim.Traffic
{
    public sealed partial class GridlockResolver
    {
        /// <summary>
        /// A quien espera el vehiculo <paramref name="v"/> (arista del grafo de espera), o -1. Solo cuenta quien esta parado desde hace
        /// al menos el umbral (los que se mueven no forman bloqueos). Casos:
        /// el lider de su pista; el primero de una conexion, la cola del carril siguiente; con permiso, la cola de la conexion (o del
        /// carril de salida); sin permiso y rechazado por hueco a la salida, la cola de ese carril; rechazado por conflicto, el
        /// vehiculo que ocupa o reserva la conexion en conflicto (el que lleva mas parado). Semaforo, stop y reclamaciones del tick no
        /// forman ciclos: se resuelven solos.
        /// </summary>
        private int Blocker(int v)
        {
            ref readonly Vehicle a = ref _veh.GetVehicle(v);
            if (a.Ahead >= 0) return Waiting(a.Ahead);
            if ((a.Flags & VehicleFlags.Frozen) != 0 || a.RouteCursor >= a.RouteLast) return -1;
            LaneId to = _veh.GetRouteStep(v, a.RouteCursor + 1).Lane;
            if (a.Kind == TrackKind.Connection) return Waiting(_veh.LaneTail(to));
            if (a.PrevConnection >= 0) return -1; // la trasera aun cruza: lo que le queda es avanzar

            int conn = FindConnection(new LaneId(a.Track), to);
            if (conn < 0) return -1;
            if ((a.Flags & VehicleFlags.HasEntryPermit) != 0)
            {
                int ct = _veh.ConnectionTail(conn);
                return Waiting(ct >= 0 ? ct : _veh.LaneTail(to));
            }

            if (_junction != null)
            {
                switch (_junction.GetLastDecision(v))
                {
                    case GrantDecision.DeniedExitFull: return ExitBlocker(v, conn, to);
                    case GrantDecision.DeniedConflict: return ConflictBlocker(v, conn);
                    default: return -1;
                }
            }
            // Puerta sin motivos de rechazo (GapOnlyGate): solo cuenta el hueco a la salida.
            float laneLen = _net.GetLane(to).Length;
            VehicleType t = _veh.GetVehicleType(a.Type);
            float need = t.Space < laneLen ? t.Space : laneLen;
            return _veh.FreeSpace(to) + 1e-4f < need ? ExitBlocker(v, conn, to) : -1;
        }

        /// <summary><paramref name="b"/> si existe y esta parado desde hace al menos el umbral; si no, -1.</summary>
        private int Waiting(int b) => b >= 0 && Qualifies(b) ? b : -1;

        /// <summary>Cola del carril de salida lleno; si esta vacio (hueco reservado), quien ocupa o va a ocupar una conexion hacia el.</summary>
        private int ExitBlocker(int v, int conn, LaneId to)
        {
            int tail = _veh.LaneTail(to);
            if (tail >= 0 && tail != v) return Waiting(tail);
            ref readonly LaneConnection c = ref _net.GetConnection(conn);
            ref readonly Node n = ref _net.GetNode(c.Node);
            int best = -1, bestWait = -1;
            for (int k = 0; k < n.ConnCount; k++)
            {
                int idx = n.ConnFirst + k;
                ref readonly LaneConnection o = ref _net.GetConnection(idx);
                if (o.To != to) continue;
                int cand = _veh.ConnectionTail(idx);
                if (cand < 0)
                {
                    int head = _veh.LaneHead(o.From);
                    if (head >= 0 && head != v && HasPermitTo(head, to)) cand = head;
                }
                Consider(cand, ref best, ref bestWait);
            }
            return best;
        }

        /// <summary>El vehiculo que mas lleva parado entre los que ocupan o reservan una conexion en conflicto con <paramref name="conn"/>.</summary>
        private int ConflictBlocker(int v, int conn)
        {
            ref readonly LaneConnection c = ref _net.GetConnection(conn);
            JunctionNode? jn = _junction!.GetJunction(c.Node);
            if (jn == null) return -1;
            ulong busy = jn.Gates[c.LocalIndex].ConflictMask & (jn.PermitMask | jn.OccupiedMask);
            int baseConn = conn - c.LocalIndex;
            int best = -1, bestWait = -1;
            while (busy != 0)
            {
                int j = Bits.TrailingZeroCount(busy);
                busy &= busy - 1;
                ref readonly ConnGate gj = ref jn.Gates[j];
                if (gj.Permits > 0)
                {
                    ref readonly LaneConnection oj = ref _net.GetConnection(baseConn + j);
                    int head = _veh.LaneHead(oj.From);
                    if (head >= 0 && head != v && HasPermitTo(head, oj.To)) Consider(head, ref best, ref bestWait);
                }
                if (gj.Occupants > 0 && gj.LastEntrant != v) Consider(gj.LastEntrant, ref best, ref bestWait);
            }
            return best;
        }

        private void Consider(int cand, ref int best, ref int bestWait)
        {
            if (cand < 0 || !Qualifies(cand)) return;
            int w = _veh.GetVehicle(cand).WaitTicks;
            if (w > bestWait)
            {
                best = cand;
                bestWait = w;
            }
        }

        private bool HasPermitTo(int vehicle, LaneId to)
        {
            ref readonly Vehicle h = ref _veh.GetVehicle(vehicle);
            return h.Kind == TrackKind.Lane && (h.Flags & VehicleFlags.HasEntryPermit) != 0 && h.RouteCursor < h.RouteLast
                && _veh.GetRouteStep(vehicle, h.RouteCursor + 1).Lane == to;
        }

        private int FindConnection(LaneId from, LaneId to)
        {
            System.ReadOnlySpan<LaneConnection> span = _net.GetConnectionsFrom(from);
            for (int i = 0; i < span.Length; i++)
                if (span[i].To == to) return _net.GetLane(from).ConnFirst + i;
            return -1;
        }
    }
}
