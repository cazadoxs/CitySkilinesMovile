using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Sim.Vehicles
{
    public sealed partial class VehicleSystem
    {
        /// <summary>
        /// M1-11 (bloqueo total, paso 2): pide una nueva ruta para el vehiculo. Lo deja como tras una edicion de la red que rompe su
        /// ruta: se trunca en el carril actual (retira el permiso de entrada si lo tenia), se detiene al final del carril y queda
        /// <see cref="VehicleFlags.NeedsReroute"/> hasta que <see cref="TryReplaceRoute"/> le de otra ruta (la rehace el generador de
        /// viajes) o se le de de baja. Solo vale para el primero de un carril con ruta por delante y sin la trasera en una conexion.
        /// </summary>
        public bool MarkForReroute(EntityId id)
        {
            if (!IsAlive(id)) return false;
            ref Vehicle v = ref _veh[id.Index];
            if (v.Kind != TrackKind.Lane || v.PrevConnection >= 0 || v.Ahead >= 0) return false;
            if ((v.Flags & (VehicleFlags.NeedsReroute | VehicleFlags.Frozen | VehicleFlags.Arrived)) != 0) return false;
            if (v.RouteCursor >= v.RouteLast) return false;
            if ((v.Flags & VehicleFlags.HasEntryPermit) != 0)
            {
                _laneReservedCm[_routes.Steps[v.RouteOffset + v.RouteCursor + 1].Lane.Value] -= v.ReservedCm;
                v.ReservedCm = 0;
                v.Flags &= ~VehicleFlags.HasEntryPermit;
                _gate.OnPermitCancelled(id.Index);
            }
            v.RouteLast = v.RouteCursor;
            v.Flags |= VehicleFlags.NeedsReroute;
            float stop = _net.GetLane(new LaneId(v.Track)).Length - _cfg.StopMargin - _types[v.Type].S0;
            v.TargetS = stop > 0f ? stop : 0f;
            return true;
        }
    }
}
