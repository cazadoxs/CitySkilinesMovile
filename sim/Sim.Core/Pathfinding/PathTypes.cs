using System;
using Urbe.Sim.Ids;

namespace Urbe.Sim.Pathfinding
{
    /// <summary>Un paso de la ruta: segmento y sentido (true = de NodeA a NodeB).</summary>
    public readonly struct RouteStep : IEquatable<RouteStep>
    {
        public readonly EntityId Segment;
        public readonly bool Forward;

        public RouteStep(EntityId segment, bool forward)
        {
            Segment = segment;
            Forward = forward;
        }

        public bool Equals(RouteStep other) => Segment == other.Segment && Forward == other.Forward;
        public override bool Equals(object? obj) => obj is RouteStep o && Equals(o);
        public override int GetHashCode() => unchecked(Segment.GetHashCode() * 2 + (Forward ? 1 : 0));
        public static bool operator ==(RouteStep a, RouteStep b) => a.Equals(b);
        public static bool operator !=(RouteStep a, RouteStep b) => !a.Equals(b);
        public override string ToString() => Segment + (Forward ? "+" : "-");
    }

    /// <summary>Sentidos de circulacion admitidos en el origen o el destino de una busqueda.</summary>
    [Flags]
    public enum TravelDirs : byte
    {
        None = 0,
        Forward = 1,
        Backward = 2,
        Both = 3,
    }

    /// <summary>Prioridad de una peticion (menor valor = se atiende antes). GDD 8.5.</summary>
    public enum PathPriority : byte
    {
        Emergency = 0,
        Service = 1,
        PublicTransport = 2,
        Private = 3,
    }

    public enum PathStatus : byte
    {
        /// <summary>Id desconocido, ya sobrescrito o liberado.</summary>
        None = 0,
        Pending = 1,
        Found = 2,
        NotFound = 3,
        /// <summary>Hay ruta pero no cabe en el almacen de resultados de la cola.</summary>
        TooLong = 4,
    }

    /// <summary>
    /// Opciones por consulta. <c>new PathOptions()</c> = sin aleatoriedad; <see cref="Default"/> = +-10 %.
    /// El coste devuelto es el percibido por el conductor (con su ruido).
    /// </summary>
    public struct PathOptions
    {
        /// <summary>Tipo de vehiculo (clave de cache; reservado para restricciones por tipo, GDD 8.5).</summary>
        public byte VehicleType;
        /// <summary>Semilla del conductor: el ruido es una funcion hash pura de (semilla, estado), sin RNG con estado.</summary>
        public uint DriverSeed;
        /// <summary>Amplitud relativa del ruido sobre el coste percibido, en [0, 0,5]. 0,1 = +-10 %.</summary>
        public float NoiseAmplitude;
        /// <summary>true = ignora la cache de rutas (ni lee ni escribe).</summary>
        public bool NoCache;

        public static PathOptions Default => new PathOptions { NoiseAmplitude = 0.1f };

        public static PathOptions WithSeed(uint driverSeed, byte vehicleType = 0)
            => new PathOptions { DriverSeed = driverSeed, VehicleType = vehicleType, NoiseAmplitude = 0.1f };
    }

    /// <summary>Penalizaciones de coste en segundos (GDD 8.5).</summary>
    public sealed class PathCostConfig
    {
        public float RightTurnPenalty = 2f;
        public float LeftTurnPenalty = 5f;
        public float UTurnPenalty = 30f;
        public float StraightPenalty = 0f;
        /// <summary>Se suma al entrar a un nodo de tipo Junction.</summary>
        public float JunctionPenalty = 2f;
        /// <summary>Cota inferior del factor de congestion (evita velocidades nulas).</summary>
        public float MinCongestionFactor = 0.05f;
        /// <summary>
        /// false (por defecto): la heuristica no se reduce por la amplitud del ruido; con ruido &gt; 0 la ruta puede
        /// ser peor que la optima percibida en una fraccion pequena (irrelevante: el coste percibido ya es aleatorio) y
        /// la busqueda explora mucho menos. true: heuristica escalada por (1 - amplitud), optima respecto al coste percibido.
        /// Sin ruido ambas son exactas.
        /// </summary>
        public bool AdmissibleNoise = false;
    }
}
