using System;
using Urbe.Sim.Roads;

namespace Urbe.Game.Rendering.Roads
{
    /// <summary>
    /// Sección transversal de un tipo de carretera, en metros laterales respecto a la curva central (+ = derecha de A hacia B, igual que
    /// <c>Lane.Offset</c>). Se calcula una vez por tipo a partir de <see cref="RoadType"/>; ADR 003, "Segmentos: perfil extruido".
    /// </summary>
    public readonly struct RoadProfile
    {
        /// <summary>Altura de la acera sobre la calzada (m).</summary>
        public const float CurbHeight = 0.15f;

        public readonly float LeftOuter;
        public readonly float LeftCurb;
        public readonly float MedianL;
        public readonly float MedianR;
        public readonly float RightCurb;
        public readonly float RightOuter;
        public readonly bool HasMedian;
        /// <summary>Rango lateral de los carriles Forward (A hacia B) y Backward, para las líneas de detención.</summary>
        public readonly float FwdMin, FwdMax, BwdMin, BwdMax;
        public readonly bool HasForward, HasBackward;
        /// <summary>Columna del atlas de este tipo.</summary>
        public readonly int Column;
        public readonly ushort TypeId;

        public RoadProfile(RoadType t)
        {
            int f = t.LanesForward, b = t.LanesBackward;
            float w = t.LaneWidth;
            float med = t.MedianWidth;
            HasForward = f > 0;
            HasBackward = b > 0;
            HasMedian = med > 0.001f && b > 0;
            if (b == 0)
            {
                // Sentido único: carriles centrados (Lane.Offset de RoadNetwork.BuildLanes).
                LeftCurb = -f * w * 0.5f;
                RightCurb = f * w * 0.5f;
                MedianL = MedianR = 0f;
                FwdMin = LeftCurb;
                FwdMax = RightCurb;
                BwdMin = BwdMax = 0f;
            }
            else
            {
                float m = med * 0.5f;
                LeftCurb = -(m + b * w);
                RightCurb = m + f * w;
                MedianL = -m;
                MedianR = m;
                FwdMin = m;
                FwdMax = RightCurb;
                BwdMin = LeftCurb;
                BwdMax = -m;
            }
            LeftOuter = LeftCurb - t.MarginWidth;
            RightOuter = RightCurb + t.MarginWidth;
            Column = Math.Min((int)t.Id, RoadAtlasLayout.TypeColumns - 1);
            TypeId = t.Id;
        }

        public float TotalWidth => RightOuter - LeftOuter;
        public float RoadwayHalfWidth => (RightCurb - LeftCurb) * 0.5f;
        public float Margin => RightOuter - RightCurb;

        /// <summary>Perfiles de todos los tipos de un catálogo (el índice es el Id del tipo).</summary>
        public static RoadProfile[] BuildAll(RoadTypeCatalog catalog)
        {
            var arr = new RoadProfile[catalog.Count];
            for (int i = 0; i < arr.Length; i++) arr[i] = new RoadProfile(catalog.Get((ushort)i));
            return arr;
        }
    }

    /// <summary>
    /// Distribución del atlas de carreteras (ADR 003, "Materiales y atlas"): columnas, una por tipo de carretera con su sección pintada a lo
    /// ancho, más columnas lisas (acera, asfalto, bordillo, césped de mediana, pintura). <c>u</c> cruza la sección; <c>v</c> es la
    /// distancia en metros / <see cref="TileMeters"/> y envuelve en toda la textura. Lo comparten el generador del atlas (Editor) y las mallas.
    /// </summary>
    public static class RoadAtlasLayout
    {
        public const int Width = 2048;
        public const int Height = 512;
        public const float TileMeters = 12f;
        /// <summary>Margen interior de cada columna (px), para que los mipmaps no mezclen columnas vecinas.</summary>
        public const int Inset = 6;
        public const int TypeColumns = 6;
        public const int TypeColumnWidth = 224;
        public const int FlatColumnWidth = 128;

        public const int ColSidewalk = TypeColumns;
        public const int ColAsphalt = TypeColumns + 1;
        public const int ColCurb = TypeColumns + 2;
        public const int ColGrass = TypeColumns + 3;
        public const int ColPaint = TypeColumns + 4;
        public const int ColumnCount = TypeColumns + 5;

        public static int ColumnX0(int col)
        {
            if (col < TypeColumns) return col * TypeColumnWidth;
            return TypeColumns * TypeColumnWidth + (col - TypeColumns) * FlatColumnWidth;
        }

        public static int ColumnWidth(int col) => col < TypeColumns ? TypeColumnWidth : FlatColumnWidth;

        /// <summary>Rango útil de u (sin márgenes) de una columna.</summary>
        public static void URange(int col, out float u0, out float u1)
        {
            int x0 = ColumnX0(col);
            u0 = (x0 + Inset) / (float)Width;
            u1 = (x0 + ColumnWidth(col) - Inset) / (float)Width;
        }

        public static float UCenter(int col)
        {
            URange(col, out float a, out float b);
            return (a + b) * 0.5f;
        }
    }
}
