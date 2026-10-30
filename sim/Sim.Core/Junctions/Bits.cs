using System;

namespace Urbe.Sim.Junctions
{
    /// <summary>popcount y ctz sin BitOperations (no existe en netstandard2.1).</summary>
    public static class Bits
    {
        private static readonly byte[] DeBruijn64 =
        {
            0, 1, 2, 53, 3, 7, 54, 27, 4, 38, 41, 8, 34, 55, 48, 28,
            62, 5, 39, 46, 44, 42, 22, 9, 24, 35, 59, 56, 49, 18, 29, 11,
            63, 52, 6, 26, 37, 40, 33, 47, 61, 45, 43, 21, 23, 58, 17, 10,
            51, 25, 36, 32, 60, 20, 57, 16, 50, 31, 19, 15, 30, 14, 13, 12,
        };

        public static int PopCount(ulong x)
        {
            unchecked
            {
                x -= (x >> 1) & 0x5555555555555555UL;
                x = (x & 0x3333333333333333UL) + ((x >> 2) & 0x3333333333333333UL);
                x = (x + (x >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
                return (int)((x * 0x0101010101010101UL) >> 56);
            }
        }

        /// <summary>Indice del bit menos significativo a 1 (64 si x == 0).</summary>
        public static int TrailingZeroCount(ulong x)
        {
            if (x == 0) return 64;
            unchecked
            {
                return DeBruijn64[((x & (0UL - x)) * 0x022FDD63CC95386DUL) >> 58];
            }
        }
    }

    /// <summary>Ordenacion radix LSD de claves ulong unicas con su permutacion, sin asignaciones (buffers del llamador).</summary>
    public static class SortUtil
    {
        /// <summary>
        /// Ordena <paramref name="keys"/>[0..n) ascendente y aplica la misma permutacion a <paramref name="idx"/>. Los temporales deben
        /// tener al menos n elementos y <paramref name="counts"/> 256. Salta los bytes en los que todas las claves coinciden.
        /// </summary>
        public static void Sort(ulong[] keys, int[] idx, int n, ulong[] tmpKeys, int[] tmpIdx, int[] counts)
        {
            if (n < 2) return;
            if (n <= 24)
            {
                InsertionSort(keys, idx, n);
                return;
            }
            ulong[] srcK = keys, dstK = tmpKeys;
            int[] srcI = idx, dstI = tmpIdx;
            for (int shift = 0; shift < 64; shift += 8)
            {
                Array.Clear(counts, 0, 256);
                for (int i = 0; i < n; i++) counts[(int)((srcK[i] >> shift) & 0xFF)]++;
                if (counts[(int)((srcK[0] >> shift) & 0xFF)] == n) continue;
                int sum = 0;
                for (int b = 0; b < 256; b++)
                {
                    int c = counts[b];
                    counts[b] = sum;
                    sum += c;
                }
                for (int i = 0; i < n; i++)
                {
                    int b = (int)((srcK[i] >> shift) & 0xFF);
                    int pos = counts[b]++;
                    dstK[pos] = srcK[i];
                    dstI[pos] = srcI[i];
                }
                ulong[] tk = srcK; srcK = dstK; dstK = tk;
                int[] ti = srcI; srcI = dstI; dstI = ti;
            }
            if (!ReferenceEquals(srcK, keys))
            {
                Array.Copy(srcK, keys, n);
                Array.Copy(srcI, idx, n);
            }
        }

        private static void InsertionSort(ulong[] keys, int[] idx, int n)
        {
            for (int a = 1; a < n; a++)
            {
                ulong k = keys[a];
                int v = idx[a];
                int b = a - 1;
                while (b >= 0 && keys[b] > k)
                {
                    keys[b + 1] = keys[b];
                    idx[b + 1] = idx[b];
                    b--;
                }
                keys[b + 1] = k;
                idx[b + 1] = v;
            }
        }
    }
}
