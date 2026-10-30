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

        public bool IsAlive => (Flags & VehicleFlags.Alive) != 0;
    }
}
