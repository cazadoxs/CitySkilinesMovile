using System;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;

namespace Urbe.Sim.Roads
{
    public enum JunctionKind : byte { None, DeadEnd, Continuation, Junction }

    [Flags]
    public enum SegmentFlags : byte
    {
        None = 0,
        /// <summary>Reservado para M2 (puentes).</summary>
        Bridge = 1,
        /// <summary>Reservado para M2 (tuneles).</summary>
        Tunnel = 2,
    }

    public enum LaneDirection : byte { Forward, Backward }

    public enum LaneKind : byte { Car }

    public enum TurnType : byte { Straight, Right, Left, UTurn }

    public struct Node
    {
        public EntityId Id;
        public Vec2 Position;
        public float Elevation;
        public byte SegmentCount;
        public JunctionKind Kind;
        /// <summary>Reservado para M1-07 (semaforos, prioridades).</summary>
        public ushort ControlId;
        public int ConnFirst;
        public ushort ConnCount;
        public ushort ConnCapacity;
    }

    public struct Segment
    {
        public EntityId Id;
        public EntityId NodeA;
        public EntityId NodeB;
        public Bezier3 Curve;
        public float Length;
        public ushort RoadTypeId;
        public byte LanesForward;
        public byte LanesBackward;
        public SegmentFlags Flags;
        public int ArcTableOffset;
    }

    /// <summary>Id de carril: <c>segmentIndex * 8 + slot</c>. Sin allocator propio.</summary>
    public readonly struct LaneId : IEquatable<LaneId>, IComparable<LaneId>
    {
        public readonly int Value;

        public LaneId(int value) { Value = value; }

        public LaneId(int segmentIndex, int slot) { Value = segmentIndex * RoadNetwork.MaxLanesPerSegment + slot; }

        public static LaneId None => new LaneId(-1);
        public bool IsValid => Value >= 0;
        public int SegmentIndex => Value >> 3;
        public int Slot => Value & 7;

        public bool Equals(LaneId other) => Value == other.Value;
        public int CompareTo(LaneId other) => Value.CompareTo(other.Value);
        public override bool Equals(object? obj) => obj is LaneId o && Equals(o);
        public override int GetHashCode() => Value;
        public static bool operator ==(LaneId a, LaneId b) => a.Value == b.Value;
        public static bool operator !=(LaneId a, LaneId b) => a.Value != b.Value;
        public override string ToString() => IsValid ? "Lane(" + SegmentIndex + ":" + Slot + ")" : "Lane(None)";
    }

    public struct Lane
    {
        public EntityId Segment;
        public byte Slot;
        public LaneDirection Direction;
        public LaneKind Kind;
        /// <summary>Desplazamiento lateral respecto a la curva central; + = derecha de A hacia B.</summary>
        public float Offset;
        public float Length;
        public float SpeedLimit;
        public int ConnFirst;
        public ushort ConnCount;
    }

    public struct LaneConnection
    {
        public LaneId From;
        public LaneId To;
        public EntityId Node;
        public Bezier3 Curve;
        public float Length;
        public float SpeedLimit;
        public TurnType Turn;
        public byte LocalIndex;

        public LaneConnection(LaneId from, LaneId to, EntityId node, in Bezier3 curve, float length, float speedLimit, TurnType turn, byte localIndex)
        {
            From = from;
            To = to;
            Node = node;
            Curve = curve;
            Length = length;
            SpeedLimit = speedLimit;
            Turn = turn;
            LocalIndex = localIndex;
        }
    }
}
