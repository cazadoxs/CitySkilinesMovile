using System;

namespace Urbe.Sim.Runner.Render
{
    /// <summary>Paleta fija de 256 colores: cubo 6x6x6 (216) mas 40 grises. Cuantizacion al color mas cercano.</summary>
    public static class FixedPalette
    {
        private const int Greys = 40;

        public static byte[] Build()
        {
            var p = new byte[768];
            int k = 0;
            for (int r = 0; r < 6; r++)
                for (int g = 0; g < 6; g++)
                    for (int b = 0; b < 6; b++)
                    {
                        p[k++] = (byte)(r * 51);
                        p[k++] = (byte)(g * 51);
                        p[k++] = (byte)(b * 51);
                    }
            for (int i = 0; i < Greys; i++)
            {
                byte v = (byte)Math.Round(255.0 * (i + 0.5) / Greys);
                p[k++] = v; p[k++] = v; p[k++] = v;
            }
            return p;
        }

        public static byte Index(byte r, byte g, byte b)
        {
            int ri = (r + 25) / 51, gi = (g + 25) / 51, bi = (b + 25) / 51;
            int dr = r - ri * 51, dg = g - gi * 51, db = b - bi * 51;
            int cubeErr = dr * dr + dg * dg + db * db;
            int grey = (r + g + b) / 3;
            int gx = Math.Min(Greys - 1, grey * Greys / 256);
            int gv = (int)Math.Round(255.0 * (gx + 0.5) / Greys);
            int er = r - gv, eg = g - gv, eb = b - gv;
            int greyErr = er * er + eg * eg + eb * eb;
            return greyErr < cubeErr ? (byte)(216 + gx) : (byte)(ri * 36 + gi * 6 + bi);
        }
    }
}
