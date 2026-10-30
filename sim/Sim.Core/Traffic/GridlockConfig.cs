namespace Urbe.Sim.Traffic
{
    /// <summary>
    /// Deteccion y resolucion de bloqueo total (GDD 8.7, M1-11). Los tiempos van en segundos simulados. La opcion de partida
    /// "vehiculos atascados" (GDD 3.2) se traduce a <see cref="DespawnStuckVehicles"/> y <see cref="DespawnSeconds"/>
    /// (Realista: false; Normal: true y 600; Relajado: true y 180).
    /// </summary>
    public sealed class GridlockConfig
    {
        public bool Enabled = true;
        /// <summary>X del GDD: un vehiculo participa en un ciclo si lleva parado al menos esto.</summary>
        public float DetectSeconds = 20f;
        /// <summary>
        /// Un vehiculo que ya formo parte de un ciclo reciente (<see cref="EpisodeMemorySeconds"/>) vuelve a contar tras esta espera
        /// mucho menor: tras romper el ciclo, el mismo anillo se vuelve a trabar sin esperar otros X segundos.
        /// </summary>
        public float RepeatDetectSeconds = 3f;
        /// <summary>Cuanto tiempo se recuerda que un vehiculo estuvo en un ciclo.</summary>
        public float EpisodeMemorySeconds = 60f;
        /// <summary>Cada cuantos ticks empieza una ronda de deteccion.</summary>
        public int ScanIntervalTicks = 10;
        /// <summary>Indices de vehiculo que examina la ronda por tick (la ronda se reparte en varios ticks).</summary>
        public int ScanPerTick = 1024;
        /// <summary>Evaluaciones de "quien espera a quien" por tick (acota el coste del recorrido del grafo).</summary>
        public int StepsPerTick = 512;
        /// <summary>Un paso forzado que no se concede en este tiempo no es seguro: se cancela y se prueba otro candidato.</summary>
        public float ForceTimeoutSeconds = 4f;
        /// <summary>
        /// Pasos forzados simultaneos por ciclo (por orden de espera): en un anillo hace falta que avance un vehiculo en cada tramo
        /// para que el hueco circule por todos a la vez. Cada uno sigue sujeto al conflicto fisico real de la puerta.
        /// </summary>
        public int MaxForcedPerCycle = 8;
        /// <summary>Tras conceder un paso forzado o pedir una re-ruta se espera este tiempo a ver si el ciclo se deshace.</summary>
        public float ActionGraceSeconds = 3f;
        /// <summary>Paso 3: dar de baja vehiculos del ciclo (opcion de partida; por defecto si).</summary>
        public bool DespawnStuckVehicles = true;
        /// <summary>Tiempo parado a partir del cual un vehiculo de un ciclo no resuelto desaparece.</summary>
        public float DespawnSeconds = 120f;
        /// <summary>Entre dos bajas del paso 3 (da tiempo a que el hueco se propague).</summary>
        public float DespawnCooldownSeconds = 5f;
    }

    /// <summary>Contadores acumulados de <see cref="GridlockResolver"/> (tambien en <c>TrafficMetrics.Gridlock</c>).</summary>
    public sealed class GridlockStats
    {
        /// <summary>Ciclos de espera mutua detectados como episodios nuevos (ningun miembro estaba ya en un episodio reciente).</summary>
        public long Cycles;
        /// <summary>Paso 1: pasos forzados pedidos a la puerta.</summary>
        public long ForcedAttempts;
        /// <summary>Paso 1: pasos forzados concedidos (el vehiculo entro en el cruce).</summary>
        public long ForcedPasses;
        /// <summary>Paso 1: pasos forzados cancelados por no ser seguros (conflicto fisico real) en el tiempo de espera.</summary>
        public long ForcedFailed;
        /// <summary>Paso 2: re-rutas pedidas.</summary>
        public long Reroutes;
        /// <summary>Paso 3: vehiculos dados de baja.</summary>
        public long Despawns;
        /// <summary>Ciclos que agotaron los pasos disponibles sin deshacerse (una vez por episodio y vehiculo mas esperado).</summary>
        public long Unresolved;

        public long Resolutions => ForcedPasses + Reroutes + Despawns;

        public void Reset()
        {
            Cycles = ForcedAttempts = ForcedPasses = ForcedFailed = Reroutes = Despawns = Unresolved = 0;
        }
    }

    /// <summary>Quien puede rehacer la ruta de un vehiculo marcado <c>NeedsReroute</c> (paso 2 del bloqueo total): el generador de viajes.</summary>
    public interface IRerouteSource
    {
        bool CanReroute(int vehicle);
    }
}
