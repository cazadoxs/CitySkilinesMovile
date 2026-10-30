using System;

namespace Urbe.Sim.Pathfinding
{
    /// <summary>
    /// Cache de rutas con tabla hash abierta (sondeo lineal, 8 intentos) y capacidad fija. Si no hay hueco en la
    /// ventana de sondeo se reemplaza la entrada de la posicion base. Determinista: depende solo de la secuencia de
    /// llamadas. Guarda codigos de estado (segmento*2 + sentido); solo es valida dentro de una epoca de la red.
    /// </summary>
    internal sealed class PathRouteCache
    {
        private const int Probes = 8;

        private readonly int _mask;
        private readonly int _maxLen;
        private readonly ulong[] _k1;
        private readonly ulong[] _k2;
        private readonly ulong[] _k3;
        private readonly int[] _len; // -1 = vacio; 0 = sin ruta; >0 = pasos
        private readonly float[] _cost;
        private readonly int[] _steps;

        public PathRouteCache(int capacity, int maxRouteLength)
        {
            int cap = 16;
            while (cap < capacity) cap <<= 1;
            _mask = cap - 1;
            _maxLen = maxRouteLength;
            _k1 = new ulong[cap];
            _k2 = new ulong[cap];
            _k3 = new ulong[cap];
            _len = new int[cap];
            _cost = new float[cap];
            _steps = new int[cap * maxRouteLength];
            Clear();
        }

        public int Capacity => _mask + 1;
        public int MaxRouteLength => _maxLen;

        public void Clear()
        {
            for (int i = 0; i < _len.Length; i++) _len[i] = -1;
        }

        private int Home(ulong k1, ulong k2, ulong k3)
        {
            ulong h = Mix(k1 ^ Mix(k2 ^ Mix(k3)));
            return (int)(h >> 20) & _mask;
        }

        private static ulong Mix(ulong x)
        {
            unchecked
            {
                x += 0x9E3779B97F4A7C15UL;
                x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
                x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
                return x ^ (x >> 31);
            }
        }

        /// <summary>Devuelve la longitud (0 = sin ruta cacheada) y copia los estados; -1 si no esta; -2 si no cabe.</summary>
        public int TryGet(ulong k1, ulong k2, ulong k3, Span<int> states, out float cost)
        {
            int home = Home(k1, k2, k3);
            for (int p = 0; p < Probes; p++)
            {
                int i = (home + p) & _mask;
                if (_len[i] < 0) break;
                if (_k1[i] == k1 && _k2[i] == k2 && _k3[i] == k3)
                {
                    int n = _len[i];
                    cost = _cost[i];
                    if (n > states.Length) return -2;
                    new ReadOnlySpan<int>(_steps, i * _maxLen, n).CopyTo(states);
                    return n;
                }
            }
            cost = 0f;
            return -1;
        }

        public void Put(ulong k1, ulong k2, ulong k3, ReadOnlySpan<int> states, float cost)
        {
            if (states.Length > _maxLen) return;
            int home = Home(k1, k2, k3);
            int slot = home;
            for (int p = 0; p < Probes; p++)
            {
                int i = (home + p) & _mask;
                if (_len[i] < 0 || (_k1[i] == k1 && _k2[i] == k2 && _k3[i] == k3)) { slot = i; break; }
            }
            _k1[slot] = k1;
            _k2[slot] = k2;
            _k3[slot] = k3;
            _len[slot] = states.Length;
            _cost[slot] = cost;
            states.CopyTo(new Span<int>(_steps, slot * _maxLen, states.Length));
        }
    }
}
