using Urbe.Sim.Roads;

namespace Urbe.Sim.Junctions
{
    /// <summary>Tipo de control del cruce (ADR 004).</summary>
    public enum ControlKind : byte { Priority, Stop, Signal }

    /// <summary>Valores de <see cref="Node.ControlId"/>: tabla fija de M1-07.</summary>
    public static class JunctionControlIds
    {
        public const ushort Auto = 0;
        public const ushort Priority = 1;
        public const ushort Stop = 2;
        public const ushort Signal = 3;
    }

    public enum ApproachRule : byte { Free, Yield, Stop, Signal }

    public enum ConflictKind : byte { Cross, Merge }

    public enum SignalStage : byte { Green, Amber, AllRed }

    public enum SignalAspect : byte { Red, Amber, Green }

    /// <summary>Motivo de la ultima decision sobre una peticion (diagnostico y tests).</summary>
    public enum GrantDecision : byte { None, Granted, DeniedSignal, DeniedStop, DeniedClaimed, DeniedConflict, DeniedExitFull }

    /// <summary>Conflicto de la conexion i con <see cref="Other"/>. Zonas en decimetros (Start hacia abajo, End hacia arriba). 8 B.</summary>
    public struct ConflictEntry
    {
        public byte Other;
        public ConflictKind Kind;
        /// <summary>Inicio de la zona comun sobre i.</summary>
        public ushort StartDm;
        /// <summary>Fin de la zona comun sobre i.</summary>
        public ushort EndDm;
        /// <summary>Fin de la zona comun sobre Other (liberacion parcial).</summary>
        public ushort OtherEndDm;
    }

    /// <summary>Estado y datos estaticos de una conexion del cruce (indice = LocalIndex).</summary>
    public struct ConnGate
    {
        public ulong ConflictMask;
        public int ConflictFirst;
        public byte ConflictCount;
        /// <summary>Ranura (orden CCW del nodo) del segmento de origen.</summary>
        public byte Approach;
        public byte Rank;
        public ApproachRule Rule;
        /// <summary>Bit p = verde en la fase p.</summary>
        public byte PhaseBits;
        public TurnType Turn;
        public ushort Permits;
        public ushort Occupants;
        /// <summary>Ocupante mas rezagado (cola de la lista de ocupacion) o -1.</summary>
        public int LastEntrant;
        /// <summary>Ocupante mas adelantado (cabeza de la lista) o -1.</summary>
        public int FirstOccupant;
    }

    /// <summary>Estado del cruce en el tick actual.</summary>
    public struct JunctionState
    {
        public byte Phase;
        public SignalStage Stage;
        public ushort StageTicksLeft;
        /// <summary>Mayor espera (ticks) de las peticiones de este nodo en el tick <see cref="WaitTick"/>.</summary>
        public ushort OldestWaitTicks;
        public ulong WaitTick;
    }

    /// <summary>Contadores acumulados del controlador.</summary>
    public sealed class JunctionStats
    {
        public long Requests;
        public long Granted;
        public long DeniedSignal;
        public long DeniedStop;
        public long DeniedClaimed;
        public long DeniedConflict;
        public long DeniedExitFull;

        public void Reset()
        {
            Requests = Granted = DeniedSignal = DeniedStop = DeniedClaimed = DeniedConflict = DeniedExitFull = 0;
        }
    }

    /// <summary>Constantes del ADR 004 (valores por defecto). Ticks de 0,1 s.</summary>
    public sealed class JunctionConfig
    {
        public float Dt = 0.1f;
        public float ConflictDistance = 2.5f;
        public float ZoneSampleStep = 1.0f;
        public int MaxZoneSamples = 64;
        /// <summary>Separacion maxima entre la poligonal de muestras y la curva (M1-07: muestreo adaptativo a la curvatura).</summary>
        public float ChordTolerance = 0.05f;
        public float ZonePad = 1.0f;
        public float StopZone = 5.0f;
        public ushort StopDwellTicks = 10;
        public float StopSpeed = 0.3f;
        public int AgingTicks = 50;
        public int AgingMax = 7;
        public float AmberBrake = 3.0f;
        /// <summary>Espera minima parado de un giro a la izquierda para despejar en ambar.</summary>
        public ushort AmberClearStoppedTicks = 20;
        public ushort GreenMajor = 250;
        public ushort GreenOther = 150;
        public ushort Amber = 30;
        public ushort AllRed = 20;
    }
}
