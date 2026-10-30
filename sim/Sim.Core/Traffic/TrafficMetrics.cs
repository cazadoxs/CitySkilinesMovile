using System;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Urbe.Sim.Vehicles;

namespace Urbe.Sim.Traffic
{
    public sealed class TrafficMetricsConfig
    {
        /// <summary>Ventana movil del flujo de la ciudad, en segundos simulados.</summary>
        public float WindowSeconds = 60f;
        /// <summary>Granularidad de la ventana (cada cubo agrupa este tiempo).</summary>
        public float BucketSeconds = 1f;
        /// <summary>Cada cuanto se actualiza el flujo por segmento.</summary>
        public float SegmentUpdateSeconds = 1f;
        /// <summary>Constante de tiempo de la media movil exponencial por segmento.</summary>
        public float SegmentSmoothingSeconds = 30f;
        /// <summary>Un segmento sin vehiculos vuelve hacia 1 con esta constante de tiempo.</summary>
        public float SegmentRecoverySeconds = 20f;
        /// <summary>Cota inferior del factor de congestion que se entrega al pathfinding.</summary>
        public float MinCongestionFactor = 0.1f;
    }

    /// <summary>
    /// Flujo de trafico (GDD 8.7). Ciudad: sum(velocidad real) / sum(velocidad libre) en una ventana movil, es decir la media
    /// de (v real / v libre) ponderada por la distancia que cada vehiculo recorreria a velocidad libre (un vehiculo parado
    /// pesa igual que uno fluido: con pesos por distancia real los atascos desaparecerian de la media). Velocidad libre =
    /// la deseada del vehiculo en su pista (limite x factor de tipo x factor de conductor, con tope del tipo). Por segmento:
    /// v media / v libre, suavizada con media movil exponencial. Sin asignaciones en regimen estable.
    /// </summary>
    public sealed class TrafficMetrics
    {
        private readonly RoadNetwork _net;
        private readonly VehicleSystem _veh;
        private readonly TrafficMetricsConfig _cfg;

        private readonly double[] _bucketV;
        private readonly double[] _bucketF;
        private int _bucketPos;
        private int _bucketsFilled;
        private double _ringV, _ringF;
        private double _curV, _curF;
        private readonly int _ticksPerBucket;
        private int _ticksInBucket;

        private float[] _segFlow;
        private float[] _segV;
        private float[] _segF;
        private readonly int _ticksPerSegUpdate;
        private int _ticksInSegUpdate;
        private readonly float _alpha;
        private readonly float _alphaIdle;
        private readonly int _netSub;
        private ulong _lastEpoch;

        public TrafficMetrics(RoadNetwork network, VehicleSystem vehicles, TrafficMetricsConfig? config = null)
        {
            _net = network ?? throw new ArgumentNullException(nameof(network));
            _veh = vehicles ?? throw new ArgumentNullException(nameof(vehicles));
            _cfg = config ?? new TrafficMetricsConfig();
            float dt = vehicles.Config.Dt;
            _ticksPerBucket = Math.Max(1, (int)Math.Round(_cfg.BucketSeconds / dt));
            int buckets = Math.Max(1, (int)Math.Round(_cfg.WindowSeconds / _cfg.BucketSeconds));
            _bucketV = new double[buckets];
            _bucketF = new double[buckets];
            _ticksPerSegUpdate = Math.Max(1, (int)Math.Round(_cfg.SegmentUpdateSeconds / dt));
            float interval = _ticksPerSegUpdate * dt;
            _alpha = 1f - (float)Math.Exp(-interval / _cfg.SegmentSmoothingSeconds);
            _alphaIdle = 1f - (float)Math.Exp(-interval / _cfg.SegmentRecoverySeconds);
            int n = Math.Max(network.SegmentIndexBound, 4);
            _segFlow = NewOnes(n);
            _segV = new float[n];
            _segF = new float[n];
            _lastEpoch = network.Epoch;
            _netSub = network.Subscribe();
        }

        /// <summary>
        /// Se invoca (una vez por cambio de epoca de la red) tras reiniciar el flujo de los segmentos creados o eliminados; el dueno
        /// (<see cref="TrafficSimulation"/>) lo usa para republicar la congestion al pathfinding.
        /// </summary>
        public Action? SegmentFlowReset;

        private static float[] NewOnes(int n)
        {
            var a = new float[n];
            for (int i = 0; i < n; i++) a[i] = 1f;
            return a;
        }

        public TrafficMetricsConfig Config => _cfg;

        /// <summary>Contadores de deteccion y resolucion de bloqueo total (M1-11); los actualiza el <see cref="GridlockResolver"/>.</summary>
        public GridlockStats Gridlock { get; } = new GridlockStats();

        /// <summary>Flujo de la ciudad en [0, 1] (1 si no hay vehiculos).</summary>
        public float CityFlow
        {
            get
            {
                double f = _ringF + _curF;
                return f > 1e-9 ? (float)Math.Min(1.0, (_ringV + _curV) / f) : 1f;
            }
        }

        public float CityFlowPercent => CityFlow * 100f;

        /// <summary>Flujo suavizado por indice de segmento, en [0, 1]. El array puede cambiar de instancia si la red crece.</summary>
        public float[] SegmentFlow => _segFlow;

        public float GetSegmentFlow(int segmentIndex) => segmentIndex >= 0 && segmentIndex < _segFlow.Length ? _segFlow[segmentIndex] : 1f;

        /// <summary>Muestrea los vehiculos tras <c>VehicleSystem.Update</c>.</summary>
        public void Sample()
        {
            int bound = _net.SegmentIndexBound;
            if (bound > _segFlow.Length) Grow(bound);
            if (_net.Epoch != _lastEpoch) ResetChangedSegments();

            double sv = 0, sf = 0;
            int high = _veh.HighWater;
            for (int i = 0; i < high; i++)
            {
                ref readonly Vehicle v = ref _veh.GetVehicle(i);
                if (!v.IsAlive) continue;
                float limit;
                int seg;
                if (v.Kind == TrackKind.Lane)
                {
                    var lane = new LaneId(v.Track);
                    limit = _net.GetLane(lane).SpeedLimit;
                    seg = lane.SegmentIndex;
                }
                else
                {
                    limit = _net.GetConnection(v.Track).SpeedLimit;
                    seg = -1;
                }
                VehicleType t = _veh.GetVehicleType(v.Type);
                float free = limit * t.V0Factor * v.SpeedFactor;
                if (free > t.VMax) free = t.VMax;
                if (free < 0.1f) free = 0.1f;
                float real = v.V < free ? v.V : free;
                sv += real;
                sf += free;
                if (seg >= 0 && seg < _segV.Length)
                {
                    _segV[seg] += real;
                    _segF[seg] += free;
                }
            }
            _curV += sv;
            _curF += sf;

            if (++_ticksInBucket >= _ticksPerBucket)
            {
                _ticksInBucket = 0;
                if (_bucketsFilled == _bucketV.Length)
                {
                    _ringV -= _bucketV[_bucketPos];
                    _ringF -= _bucketF[_bucketPos];
                }
                else
                {
                    _bucketsFilled++;
                }
                _bucketV[_bucketPos] = _curV;
                _bucketF[_bucketPos] = _curF;
                _ringV += _curV;
                _ringF += _curF;
                _curV = 0;
                _curF = 0;
                _bucketPos = (_bucketPos + 1) % _bucketV.Length;
            }

            if (++_ticksInSegUpdate >= _ticksPerSegUpdate)
            {
                _ticksInSegUpdate = 0;
                UpdateSegments(bound);
            }
        }

        /// <summary>
        /// El indice de un segmento eliminado se reutiliza: el flujo (EMA) del tramo nuevo no debe heredar el atasco del derribado.
        /// Se reinician a 1 los segmentos creados o eliminados desde el ultimo muestreo; las mitades de un segmento dividido heredan
        /// el flujo del original (es la misma via). Los segmentos que solo cambiaron de retranqueo conservan el suyo.
        /// </summary>
        private void ResetChangedSegments()
        {
            _lastEpoch = _net.Epoch;
            _net.ConsumeChanges(_netSub, out _, out ReadOnlySpan<EntityId> segs, out ReadOnlySpan<SegmentSplit> splits);
            int bound = _net.SegmentIndexBound;
            if (bound > _segFlow.Length) Grow(bound);
            // Flujo de partida de las mitades, leido antes de reiniciar nada (el indice del original puede reutilizarse).
            if (_splitFlow.Length < splits.Length) _splitFlow = new float[Math.Max(splits.Length, _splitFlow.Length * 2)];
            for (int k = 0; k < splits.Length; k++)
            {
                EntityId old = splits[k].Old;
                float f = old.Index < _segFlow.Length ? _segFlow[old.Index] : 1f;
                for (int j = 0; j < k; j++)
                {
                    if (splits[j].Half1 == old || splits[j].Half2 == old) f = _splitFlow[j];
                }
                _splitFlow[k] = f;
            }
            for (int k = 0; k < segs.Length; k++)
            {
                EntityId id = segs[k];
                if (id.Index >= _segFlow.Length) continue;
                // Vivo y ya existente antes: conserva su flujo; en cualquier otro caso (eliminado, o creado desde la ultima consulta) se reinicia.
                if (_net.IsSegmentAlive(id) && _net.TryGetPreState(id, out _)) continue;
                _segFlow[id.Index] = 1f;
                _segV[id.Index] = 0f;
                _segF[id.Index] = 0f;
            }
            for (int k = 0; k < splits.Length; k++)
            {
                SegmentSplit sp = splits[k];
                if (_net.IsSegmentAlive(sp.Half1) && sp.Half1.Index < _segFlow.Length) _segFlow[sp.Half1.Index] = _splitFlow[k];
                if (_net.IsSegmentAlive(sp.Half2) && sp.Half2.Index < _segFlow.Length) _segFlow[sp.Half2.Index] = _splitFlow[k];
            }
            SegmentFlowReset?.Invoke();
        }

        private float[] _splitFlow = new float[8];

        private void UpdateSegments(int bound)
        {
            for (int s = 0; s < bound; s++)
            {
                float f = _segF[s];
                if (f > 1e-6f)
                {
                    float ratio = _segV[s] / f;
                    _segFlow[s] += _alpha * (ratio - _segFlow[s]);
                }
                else
                {
                    _segFlow[s] += _alphaIdle * (1f - _segFlow[s]);
                }
                _segV[s] = 0f;
                _segF[s] = 0f;
            }
        }

        private void Grow(int bound)
        {
            int n = Math.Max(bound, _segFlow.Length * 2);
            int old = _segFlow.Length;
            Array.Resize(ref _segFlow, n);
            Array.Resize(ref _segV, n);
            Array.Resize(ref _segF, n);
            for (int i = old; i < n; i++) _segFlow[i] = 1f;
        }
    }
}
