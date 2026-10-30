using System;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Sim.Vehicles
{
    /// <summary>
    /// Eleccion de carril anticipada (M1-08, GDD 8.6). Dada una ruta por segmentos y sentido, elige el carril de LLEGADA a cada
    /// segmento (el que alcanza la conexion del nodo anterior) minimizando los cambios de carril necesarios: programacion dinamica
    /// sobre (segmento, carril de llegada). El carril de salida de un segmento es el mas cercano al de llegada que conecta con el
    /// de llegada del siguiente; cada cambio cuesta mas cuanto mas corto es el segmento, de modo que el giro del segmento siguiente
    /// (y el de 1-2 segmentos despues, por encadenamiento) se resuelve en el nodo anterior si el segmento es corto.
    /// <para>
    /// Cualquier ruta cuyos segmentos consecutivos tengan alguna <see cref="LaneConnection"/> entre si tiene plan (los cambios de
    /// carril cubren la diferencia), asi que ya no hay rutas "sin cadena de carriles". Sin asignaciones por llamada; no es seguro entre hilos.
    /// </para>
    /// </summary>
    public sealed class LanePlanner
    {
        /// <summary>Coste fijo de cada cambio de carril.</summary>
        public const float ChangeBaseCost = 2f;
        /// <summary>Coste adicional por cambio = coef / metros disponibles (los segmentos cortos penalizan mas).</summary>
        public const float ChangeLengthCoef = 150f;
        /// <summary>Distancia minima razonable por cambio; por debajo se suma <see cref="InfeasiblePenalty"/> por carril que falta.</summary>
        public const float MinLengthPerChange = 20f;
        public const float InfeasiblePenalty = 100f;
        /// <summary>Ruido por salto, determinista por semilla, que reparte los viajes entre carriles equivalentes (nunca compensa un cambio).</summary>
        public const float NoiseAmplitude = 0.02f;

        private const float Inf = float.PositiveInfinity;
        private const int Lanes = RoadNetwork.MaxLanesPerSegment;

        private readonly RoadNetwork _net;
        private float[] _cost;
        private sbyte[] _prev;
        private readonly uint[] _reach = new uint[Lanes];

        public LanePlanner(RoadNetwork network, int initialSegments = 128)
        {
            _net = network ?? throw new ArgumentNullException(nameof(network));
            int n = Math.Max(initialSegments, 4);
            _cost = new float[n * Lanes];
            _prev = new sbyte[n * Lanes];
        }

        /// <summary>Plan sin restricciones: el carril inicial es libre y se asume el segmento entero disponible.</summary>
        public bool Plan(ReadOnlySpan<RouteSegment> route, uint seed, Span<LaneId> arrival)
            => Plan(route, LaneId.None, -1f, false, LaneId.None, seed, arrival);

        /// <summary>
        /// Escribe en <paramref name="arrival"/> el carril de llegada de cada segmento. <paramref name="forcedFirst"/> fija el carril del
        /// primer segmento (re-ruta: el carril actual) y <paramref name="forcedSecond"/> el del segundo (vehiculo ya en la conexion).
        /// <paramref name="firstStartS"/> &gt;= 0 es la posicion del vehiculo en el primer carril (acorta la distancia disponible para cambiar).
        /// <paramref name="firstNoChange"/>: el primer segmento se abandona desde el carril de llegada, sin cambiar. False si la ruta no es plausible.
        /// </summary>
        public bool Plan(ReadOnlySpan<RouteSegment> route, LaneId forcedFirst, float firstStartS, bool firstNoChange,
            LaneId forcedSecond, uint seed, Span<LaneId> arrival)
        {
            int n = route.Length;
            if (n < 1 || arrival.Length < n) return false;
            if (_cost.Length < n * Lanes)
            {
                int cap = Math.Max(n, _cost.Length / Lanes * 2);
                _cost = new float[cap * Lanes];
                _prev = new sbyte[cap * Lanes];
            }
            for (int k = 0; k < n; k++)
                if (!_net.IsSegmentAlive(route[k].Segment)) return false;

            // k = 0
            Range(route[0], out int lo, out int hi);
            if (hi <= lo) return false;
            if (forcedFirst.IsValid && forcedFirst.SegmentIndex != route[0].Segment.Index) return false;
            for (int e = 0; e < Lanes; e++)
            {
                _cost[e] = Inf;
                _prev[e] = -1;
            }
            for (int e = lo; e < hi; e++)
            {
                if (forcedFirst.IsValid && forcedFirst.Slot != e) continue;
                _cost[e] = Noise(seed, 0, e);
            }

            for (int k = 0; k + 1 < n; k++)
            {
                RouteSegment cur = route[k], nxt = route[k + 1];
                Range(cur, out lo, out hi);
                Range(nxt, out int nlo, out int nhi);
                if (nhi <= nlo) return false;
                BuildReach(cur, lo, hi, nxt);
                int nb = (k + 1) * Lanes;
                for (int e = 0; e < Lanes; e++)
                {
                    _cost[nb + e] = Inf;
                    _prev[nb + e] = -1;
                }
                bool first = k == 0;
                for (int e = lo; e < hi; e++)
                {
                    float c0 = _cost[k * Lanes + e];
                    if (c0 == Inf) continue;
                    float avail = _net.GetLane(new LaneId(cur.Segment.Index, e)).Length;
                    if (first && firstStartS >= 0f) avail -= firstStartS;
                    if (avail < 1f) avail = 1f;
                    for (int e2 = nlo; e2 < nhi; e2++)
                    {
                        if (first && forcedSecond.IsValid && (forcedSecond.SegmentIndex != nxt.Segment.Index || forcedSecond.Slot != e2)) continue;
                        int best = 99;
                        for (int d = lo; d < hi; d++)
                        {
                            if (((_reach[d] >> e2) & 1u) == 0) continue;
                            int dist = d > e ? d - e : e - d;
                            if (dist < best) best = dist;
                        }
                        if (best == 99) continue;
                        if (first && firstNoChange && best != 0) continue;
                        float c = c0 + ChangeCost(best, avail) + Noise(seed, k + 1, e2);
                        if (c < _cost[nb + e2])
                        {
                            _cost[nb + e2] = c;
                            _prev[nb + e2] = (sbyte)e;
                        }
                    }
                }
            }

            int lb = (n - 1) * Lanes;
            int bestE = -1;
            float bestC = Inf;
            for (int e = 0; e < Lanes; e++)
            {
                if (_cost[lb + e] < bestC)
                {
                    bestC = _cost[lb + e];
                    bestE = e;
                }
            }
            if (bestE < 0) return false;
            int slot = bestE;
            for (int k = n - 1; k >= 0; k--)
            {
                arrival[k] = new LaneId(route[k].Segment.Index, slot);
                slot = _prev[k * Lanes + slot];
            }
            return true;
        }

        /// <summary>Coste de hacer <paramref name="changes"/> cambios de carril teniendo <paramref name="avail"/> metros.</summary>
        public static float ChangeCost(int changes, float avail)
        {
            if (changes <= 0) return 0f;
            float c = changes * (ChangeBaseCost + ChangeLengthCoef / avail);
            if (avail < changes * MinLengthPerChange) c += InfeasiblePenalty * (changes - avail / MinLengthPerChange);
            return c;
        }

        private void Range(RouteSegment r, out int lo, out int hi)
        {
            ref readonly Segment sg = ref _net.GetSegment(r.Segment);
            lo = r.Forward ? 0 : sg.LanesForward;
            hi = r.Forward ? sg.LanesForward : sg.LanesForward + sg.LanesBackward;
        }

        /// <summary>_reach[d] = mascara de ranuras del segmento siguiente alcanzables con una conexion desde el carril de salida d.</summary>
        private void BuildReach(RouteSegment cur, int lo, int hi, RouteSegment nxt)
        {
            for (int d = 0; d < Lanes; d++) _reach[d] = 0;
            for (int d = lo; d < hi; d++)
            {
                ReadOnlySpan<LaneConnection> conns = _net.GetConnectionsFrom(new LaneId(cur.Segment.Index, d));
                for (int q = 0; q < conns.Length; q++)
                    if (conns[q].To.SegmentIndex == nxt.Segment.Index) _reach[d] |= 1u << conns[q].To.Slot;
            }
        }

        private static float Noise(uint seed, int k, int e)
        {
            unchecked
            {
                uint h = seed ^ ((uint)k * 0x9E3779B1u) ^ ((uint)e * 0x85EBCA6Bu);
                h ^= h >> 16;
                h *= 0x7FEB352Du;
                h ^= h >> 15;
                h *= 0x846CA68Bu;
                h ^= h >> 16;
                return (h & 0xFFFFu) * (NoiseAmplitude / 65536f);
            }
        }
    }
}
