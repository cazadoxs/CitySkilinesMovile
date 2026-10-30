using System;

namespace Urbe.Sim.Random
{
    /// <summary>PCG32 (XSH RR) canonico de pcg-random.org.</summary>
    public struct Pcg32
    {
        private const ulong Multiplier = 6364136223846793005UL;
        private ulong _state;
        private ulong _inc;

        public Pcg32(ulong seed, ulong sequence)
        {
            _state = 0;
            _inc = (sequence << 1) | 1UL;
            NextUInt();
            _state += seed;
            NextUInt();
        }

        public ulong State => _state;
        public ulong Increment => _inc;

        public static Pcg32 FromState(ulong state, ulong increment)
        {
            var r = new Pcg32();
            r._state = state;
            r._inc = increment | 1UL;
            return r;
        }

        public uint NextUInt()
        {
            ulong old = _state;
            _state = unchecked(old * Multiplier + _inc);
            uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
            int rot = (int)(old >> 59);
            return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
        }

        /// <summary>Entero en [0, maxExclusive) sin sesgo (Lemire).</summary>
        public int NextInt(int maxExclusive)
        {
            if (maxExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive));
            uint range = (uint)maxExclusive;
            ulong m = (ulong)NextUInt() * range;
            uint l = (uint)m;
            if (l < range)
            {
                uint t = unchecked((uint)(-(int)range)) % range;
                while (l < t)
                {
                    m = (ulong)NextUInt() * range;
                    l = (uint)m;
                }
            }
            return (int)(m >> 32);
        }

        public int NextInt(int min, int maxExclusive)
        {
            if (maxExclusive <= min) throw new ArgumentOutOfRangeException(nameof(maxExclusive));
            uint span = unchecked((uint)(maxExclusive - min));
            ulong m = (ulong)NextUInt() * span;
            uint l = (uint)m;
            if (l < span)
            {
                uint t = unchecked(0u - span) % span;
                while (l < t)
                {
                    m = (ulong)NextUInt() * span;
                    l = (uint)m;
                }
            }
            return unchecked(min + (int)(m >> 32));
        }

        public float NextFloat() => (NextUInt() >> 8) * (1.0f / 16777216.0f);

        public double NextDouble()
        {
            ulong a = NextUInt() >> 5; // 27 bits
            ulong b = NextUInt() >> 6; // 26 bits
            return (a * 67108864.0 + b) * (1.0 / 9007199254740992.0);
        }
    }
}
