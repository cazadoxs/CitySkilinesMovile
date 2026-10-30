using Urbe.Sim.Geometry;

namespace Urbe.Sim.Roads
{
    /// <summary>Parametros de la red (ADR 001). RoadNetwork copia los valores en su constructor.</summary>
    public sealed class NetworkConfig
    {
        public Vec2 WorldMin = new Vec2(-4000f, -4000f);
        public Vec2 WorldSize = new Vec2(8000f, 8000f);
        /// <summary>Lado de celda del indice espacial (GDD 5.14).</summary>
        public float CellSize = 32f;
        public float SnapRadius = 4f;
        public float MinSegmentLength = 8f;
        public float MaxSegmentLength = 200f;
        /// <summary>Angulo minimo entre vias en un cruce.</summary>
        public float MinJunctionAngleDegrees = 25f;
        /// <summary>Solo hay cruce si la diferencia de elevacion es menor que esto.</summary>
        public float MaxCrossElevationDelta = 2f;
        public int InitialNodeCapacity = 256;
        public int InitialSegmentCapacity = 256;
    }
}
