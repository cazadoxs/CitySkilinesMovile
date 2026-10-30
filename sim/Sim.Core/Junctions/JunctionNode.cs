using System;
using Urbe.Sim.Ids;

namespace Urbe.Sim.Junctions
{
    /// <summary>
    /// Datos de un cruce (ADR 004): una <see cref="ConnGate"/> por conexion (indice = LocalIndex), sus <see cref="ConflictEntry"/>
    /// (ordenadas por conexion y, dentro, por Other), control, via principal y fases. Lo rellena <see cref="JunctionBuilder"/> al
    /// reconstruir el nodo; los arrays se reutilizan si caben.
    /// </summary>
    public sealed class JunctionNode
    {
        public const int MaxPhases = 8;
        public const byte NoSlot = 255;

        public EntityId Node = EntityId.None;
        /// <summary>Version del nodo de la red (<c>RoadNetwork.GetNodeVersion</c>) con la que se construyo; si difiere, los datos estan obsoletos.</summary>
        public uint BuiltVersion;
        public int ConnCount;
        public ConnGate[] Gates = Array.Empty<ConnGate>();
        public ConflictEntry[] Entries = Array.Empty<ConflictEntry>();
        public int EntryCount;
        public ulong PermitMask;
        public ulong OccupiedMask;
        public ControlKind Control;
        /// <summary>Ranuras del par principal (255 si no hay: todos los accesos son principales).</summary>
        public byte MajorA = NoSlot;
        public byte MajorB = NoSlot;
        public byte SegmentCount;
        public byte PhaseCount;
        public readonly ushort[] PhaseGreen = new ushort[MaxPhases];
        public int CycleTicks;
        public JunctionState State;

        public bool IsSignal => Control == ControlKind.Signal && PhaseCount > 0;

        public ReadOnlySpan<ConflictEntry> ConflictsOf(int local)
        {
            ref readonly ConnGate g = ref Gates[local];
            return new ReadOnlySpan<ConflictEntry>(Entries, g.ConflictFirst, g.ConflictCount);
        }

        public bool IsMajorApproach(int slot) => MajorA == NoSlot || slot == MajorA || slot == MajorB;

        /// <summary>Pone a cero permisos, ocupacion y mascaras dinamicas.</summary>
        public void ResetDynamic()
        {
            for (int i = 0; i < ConnCount; i++)
            {
                ref ConnGate g = ref Gates[i];
                g.Permits = 0;
                g.Occupants = 0;
                g.LastEntrant = -1;
                g.FirstOccupant = -1;
            }
            PermitMask = 0;
            OccupiedMask = 0;
            State = default;
        }

        /// <summary>Estado del semaforo para el tick (funcion de tick % CycleTicks, sin memoria).</summary>
        public void UpdateSignal(ulong tick, JunctionConfig cfg)
        {
            if (!IsSignal) return;
            int t = (int)(tick % (ulong)CycleTicks);
            for (int p = 0; p < PhaseCount; p++)
            {
                int g = PhaseGreen[p];
                if (t < g) { SetStage(p, SignalStage.Green, g - t); return; }
                t -= g;
                if (t < cfg.Amber) { SetStage(p, SignalStage.Amber, cfg.Amber - t); return; }
                t -= cfg.Amber;
                if (t < cfg.AllRed) { SetStage(p, SignalStage.AllRed, cfg.AllRed - t); return; }
                t -= cfg.AllRed;
            }
            SetStage(0, SignalStage.Green, PhaseGreen[0]);
        }

        private void SetStage(int phase, SignalStage stage, int left)
        {
            State.Phase = (byte)phase;
            State.Stage = stage;
            State.StageTicksLeft = (ushort)left;
        }

        /// <summary>Aspecto de la conexion local en el estado actual (verde si el cruce no tiene semaforo).</summary>
        public SignalAspect AspectOf(int local)
        {
            if (!IsSignal) return SignalAspect.Green;
            if (((Gates[local].PhaseBits >> State.Phase) & 1) == 0) return SignalAspect.Red;
            switch (State.Stage)
            {
                case SignalStage.Green: return SignalAspect.Green;
                case SignalStage.Amber: return SignalAspect.Amber;
                default: return SignalAspect.Red;
            }
        }
    }
}
