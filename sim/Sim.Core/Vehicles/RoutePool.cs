using System;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Sim.Vehicles
{
    /// <summary>
    /// Pool de rutas (ADR 002): un unico RouteStep[] con bloques de tamano potencia de dos (8..1024) y lista libre
    /// intrusiva por clase. Solo crece si se agota; en regimen estable no asigna.
    /// </summary>
    public sealed class RoutePool
    {
        public const int MinBlock = 8;
        public const int MaxBlock = 1024;
        private const int ClassCount = 8; // 8,16,32,64,128,256,512,1024

        private RouteStep[] _steps;
        private int _top;
        private readonly int[] _freeHead = new int[ClassCount];
        private int _blocksInUse;
        private int _growths;

        public RoutePool(int initialCapacity = 4096)
        {
            _steps = new RouteStep[Math.Max(initialCapacity, MaxBlock)];
            for (int i = 0; i < ClassCount; i++) _freeHead[i] = -1;
        }

        public RouteStep[] Steps => _steps;
        public int Capacity => _steps.Length;
        public int Top => _top;
        public int BlocksInUse => _blocksInUse;
        /// <summary>Veces que el array ha crecido (diagnostico).</summary>
        public int Growths => _growths;

        /// <summary>Tamano del bloque que se reserva para una ruta de <paramref name="length"/> pasos.</summary>
        public static int BlockSize(int length)
        {
            int size = MinBlock;
            while (size < length) size <<= 1;
            return size;
        }

        private static int ClassOf(int size)
        {
            int c = 0;
            while ((MinBlock << c) < size) c++;
            return c;
        }

        /// <summary>Reserva un bloque para <paramref name="length"/> pasos (1..1024) y devuelve su offset; -1 si la longitud es invalida.</summary>
        public int Allocate(int length)
        {
            if (length < 1 || length > MaxBlock) return -1;
            int size = BlockSize(length);
            int c = ClassOf(size);
            int off = _freeHead[c];
            if (off >= 0)
            {
                _freeHead[c] = _steps[off].Lane.Value;
            }
            else
            {
                if (_top + size > _steps.Length)
                {
                    Array.Resize(ref _steps, Math.Max(_steps.Length * 2, _top + size));
                    _growths++;
                }
                off = _top;
                _top += size;
            }
            _blocksInUse++;
            return off;
        }

        public void Free(int offset, int length)
        {
            if (offset < 0) return;
            int c = ClassOf(BlockSize(length));
            // Lista libre intrusiva: el primer paso guarda el offset del siguiente en Lane.Value.
            _steps[offset] = new RouteStep(EntityId.None, new LaneId(_freeHead[c]));
            _freeHead[c] = offset;
            _blocksInUse--;
        }
    }
}
