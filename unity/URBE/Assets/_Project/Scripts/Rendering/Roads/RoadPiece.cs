using System;
using Urbe.Sim.Geometry;

namespace Urbe.Game.Rendering.Roads
{
    [Flags]
    public enum RoadPieceFlags : byte
    {
        None = 0,
        /// <summary>Cierra la mediana elevada con una cara vertical en el extremo A / B (cruces y finales de calle).</summary>
        MedianCapStart = 1,
        MedianCapEnd = 2,
        /// <summary>Línea de detención de los carriles que entran en el cruce del extremo A / B.</summary>
        StopLineStart = 4,
        StopLineEnd = 8,
    }

    /// <summary>
    /// Entrada del constructor de mallas (ADR 003). No es un id de la red: así la herramienta de carreteras pinta el fantasma con el
    /// mismo código sin tocar la simulación. Extruye entre <see cref="T0"/> y <see cref="T1"/> de la curva central.
    /// </summary>
    public struct RoadPiece
    {
        public Bezier3 Curve;
        public float T0;
        public float T1;
        public ushort RoadTypeId;
        /// <summary>Elevación de la curva en t = 0 y t = 1 (interpolación lineal en t, como los nodos de la red).</summary>
        public float ElevA;
        public float ElevB;
        /// <summary>Índice del segmento en la red (−1 si no es de la red, p. ej. el fantasma). Va al canal uv1 para la capa de tráfico.</summary>
        public int SegmentIndex;
        public RoadPieceFlags Flags;
        /// <summary>
        /// Inglete en continuaciones con quiebro entre dos segmentos del mismo tipo (ADR 003, "Cambios durante la implementación"): la sección
        /// de ese extremo se desplaza lateral x k a lo largo de la tangente, así que el corte queda en la bisectriz y no hay hueco ni solape.
        /// </summary>
        public float MitreStart;
        public float MitreEnd;

        public RoadPiece(Bezier3 curve, ushort roadTypeId, float t0 = 0f, float t1 = 1f, float elevA = 0f, float elevB = 0f,
            int segmentIndex = -1, RoadPieceFlags flags = RoadPieceFlags.None)
        {
            Curve = curve;
            RoadTypeId = roadTypeId;
            T0 = t0;
            T1 = t1;
            ElevA = elevA;
            ElevB = elevB;
            SegmentIndex = segmentIndex;
            Flags = flags;
            MitreStart = 0f;
            MitreEnd = 0f;
        }
    }
}
