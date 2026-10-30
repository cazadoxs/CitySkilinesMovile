using System;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Sim.Vehicles
{
    public enum TrackKind : byte { Lane, Connection }

    [Flags]
    public enum VehicleFlags : byte
    {
        None = 0,
        Alive = 1,
        /// <summary>Tiene permiso (y espacio reservado) para entrar en la conexion hacia el siguiente carril de la ruta.</summary>
        HasEntryPermit = 2,
        Arrived = 4,
        /// <summary>La ruta quedo rota por una edicion de la red: se trunco y espera una nueva (M1-05) o baja.</summary>
        NeedsReroute = 8,
        /// <summary>Inmovil (tests, obstaculos).</summary>
        Frozen = 16,
    }

    /// <summary>Un paso de ruta: un carril de un segmento. El segmento lleva su generacion para detectar bajas.</summary>
    public readonly struct RouteStep
    {
        public readonly EntityId Segment;

        /// <summary>
        /// Carril del paso, con dos lecturas segun la posicion en la ruta: en el paso del cursor (<see cref="Vehicle.RouteCursor"/>) es el
        /// carril ACTUAL del vehiculo (cambia con los cambios de carril); en los pasos siguientes es el carril de LLEGADA previsto al
        /// entrar en ese segmento (lo replanifica el <see cref="LanePlanner"/> y puede cambiar mientras no haya permiso). Para el carril
        /// actual usa <see cref="VehicleSystem.GetCurrentLane"/>.
        /// </summary>
        public readonly LaneId Lane;

        public RouteStep(EntityId segment, LaneId lane)
        {
            Segment = segment;
            Lane = lane;
        }
    }

    /// <summary>Segmento y sentido de marcha; la ruta "de alto nivel" que producira el pathfinding.</summary>
    public readonly struct RouteSegment
    {
        public readonly EntityId Segment;
        public readonly bool Forward;

        public RouteSegment(EntityId segment, bool forward)
        {
            Segment = segment;
            Forward = forward;
        }
    }

    /// <summary>
    /// Estado de un vehiculo (ADR 002 + 004). S es la posicion del FRENTE sobre la pista actual (carril o conexion).
    /// En un carril, cursor = paso actual; en una conexion, cursor = paso del carril de origen (el destino es cursor + 1).
    /// M1-08: <c>Lane</c> del paso del cursor es SIEMPRE el carril actual (el cambio de carril lo reescribe); el de los pasos
    /// siguientes es el carril de LLEGADA planeado, al que no tiene por que conectar el carril de salida actual (hay cambio de carril).
    /// </summary>
    public struct Vehicle
    {
        public float S;
        public float V;
        public float A;
        public int Track;
        public int Ahead;
        public int Behind;
        public int RouteOffset;
        public ushort RouteLength;
        public ushort RouteCursor;
        /// <summary>Indice del ultimo paso de la ruta (RouteLength - 1 salvo que se trunque por una edicion).</summary>
        public ushort RouteLast;
        public float TargetS;
        public float SpeedFactor;
        public ushort WaitTicks;
        /// <summary>Espacio reservado en el carril de destino mientras HasEntryPermit (centimetros).</summary>
        public ushort ReservedCm;
        public byte Type;
        public TrackKind Kind;
        public VehicleFlags Flags;
        /// <summary>Conexion de la que el frente ya salio pero la trasera aun no (-1 = ninguna). ADR 004.</summary>
        public int PrevConnection;
        public int Generation;
        /// <summary>Carril del que el vehiculo se esta desplazando lateralmente (cambio de carril en curso, M1-08); -1 = ninguno.</summary>
        public int LcFrom;
        /// <summary>Siguiente fantasma en la lista de fantasmas del carril LcFrom (-1 = fin). Solo valido mientras LcTicks &gt; 0.</summary>
        public int GhostNext;
        /// <summary>Desplazamiento lateral inicial respecto al carril actual (m, + = derecha del sentido de marcha). Solo para el snapshot.</summary>
        public float LcLateral0;
        /// <summary>Ticks que quedan de la maniobra de cambio de carril (0 = sin maniobra).</summary>
        public byte LcTicks;
        /// <summary>Ticks de espera tras una maniobra antes de poder iniciar otra.</summary>
        public byte LcCooldown;
        /// <summary>Ticks parado en un carril que no conecta con el siguiente paso sin conseguir hueco para cambiar.</summary>
        public ushort LcWait;

        public bool IsAlive => (Flags & VehicleFlags.Alive) != 0;
    }
}
