using System;
using System.Globalization;
using System.IO;
using Urbe.Sim.Commands;
using Urbe.Sim.Pathfinding;
using Urbe.Sim.Roads;
using Urbe.Sim.Vehicles;

namespace Urbe.Sim.Traffic
{
    public sealed class TrafficSimulationOptions
    {
        public int VehicleCapacity = 4096;
        public VehicleConfig Vehicle = new VehicleConfig { DriverVariation = true };
        public TripGeneratorConfig Trips = new TripGeneratorConfig();
        public TrafficMetricsConfig Metrics = new TrafficMetricsConfig();
        public PathCostConfig? PathCost;
        /// <summary>Peticiones de ruta que se atienden por tick.</summary>
        public int PathsPerTick = 16;
        public int PathResultCapacity = 512;
        /// <summary>Cada cuanto se pasa el flujo por segmento al pathfinding como factor de congestion.</summary>
        public float CongestionPublishSeconds = 30f;
    }

    /// <summary>
    /// Agrupa red, vehiculos, puerta de cruces, pathfinding, generador de viajes y metricas, y avanza un tick en el orden del
    /// GDD 8.1: comandos, viajes, cola de rutas, movimiento, metricas. La puerta es inyectable (GapOnlyGate por defecto).
    /// </summary>
    public sealed class TrafficSimulation
    {
        private readonly TrafficSimulationOptions _opt;
        private readonly float[] _noFactors = Array.Empty<float>();
        private float[] _factors;
        private ulong _lastEpoch;
        private readonly int _publishTicks;
        private int _publishCounter;

        private TextWriter? _csv;
        private readonly int _ticksPerMinute;
        private long _csvGenerated, _csvCompleted;
        private double _csvSeconds;

        public RoadNetwork Network { get; }
        public VehicleSystem Vehicles { get; }
        public PathFinder Paths { get; }
        public PathRequestQueue PathQueue { get; }
        public TripGenerator Trips { get; }
        public TrafficMetrics Metrics { get; }
        public CommandQueue Commands { get; } = new CommandQueue();
        public CommandHistory History { get; } = new CommandHistory();

        public ulong Tick => Vehicles.CurrentTick;
        public float Seconds => Vehicles.CurrentTick * Vehicles.Config.Dt;

        public TrafficSimulation(RoadNetwork network, ulong seed, TrafficSimulationOptions? options = null, IJunctionGate? gate = null)
        {
            _opt = options ?? new TrafficSimulationOptions();
            Network = network ?? throw new ArgumentNullException(nameof(network));
            Vehicles = new VehicleSystem(network, gate ?? new GapOnlyGate(network), _opt.VehicleCapacity, seed, _opt.Vehicle);
            Paths = new PathFinder(network, _opt.PathCost);
            PathQueue = new PathRequestQueue(Paths, _opt.PathResultCapacity, _opt.Trips.MaxRouteLength);
            Trips = new TripGenerator(network, Vehicles, PathQueue, seed, _opt.Trips);
            Metrics = new TrafficMetrics(network, Vehicles, _opt.Metrics);
            _factors = _noFactors;
            _publishTicks = Math.Max(1, (int)Math.Round(_opt.CongestionPublishSeconds / Vehicles.Config.Dt));
            _ticksPerMinute = Math.Max(1, (int)Math.Round(60f / Vehicles.Config.Dt));
            _lastEpoch = network.Epoch;
            if (network.SegmentIndexBound > 0) Paths.BuildLandmarks();
            PublishCongestion();
        }

        /// <summary>Adjunta el volcado CSV (una linea por minuto simulado). El llamador es dueno del escritor.</summary>
        public void AttachCsv(TextWriter writer)
        {
            _csv = writer ?? throw new ArgumentNullException(nameof(writer));
            writer.Write("minute,active_vehicles,trips_generated,trips_completed,avg_trip_seconds,flow_percent\n");
            _csvGenerated = Trips.Generated;
            _csvCompleted = Trips.Completed;
            _csvSeconds = Trips.TripSecondsTotal;
        }

        public void Step()
        {
            if (Commands.Count > 0) Commands.Drain(Network, History);
            if (Network.Epoch != _lastEpoch)
            {
                _lastEpoch = Network.Epoch;
                if (!Paths.LandmarksReady && Network.SegmentIndexBound > 0) Paths.BuildLandmarks();
            }
            Trips.Tick();
            PathQueue.Process(_opt.PathsPerTick);
            Vehicles.Update();
            Trips.CollectFinished();
            Metrics.Sample();
            if (++_publishCounter >= _publishTicks)
            {
                _publishCounter = 0;
                PublishCongestion();
            }
            if (_csv != null && Vehicles.CurrentTick % (ulong)_ticksPerMinute == 0) WriteCsvLine();
        }

        public void Run(int ticks)
        {
            for (int i = 0; i < ticks; i++) Step();
        }

        /// <summary>Copia el flujo por segmento al array de congestion del pathfinding (con cota inferior) y avisa del cambio.</summary>
        private void PublishCongestion()
        {
            float[] flow = Metrics.SegmentFlow;
            bool fresh = _factors.Length != flow.Length;
            if (fresh) _factors = new float[flow.Length];
            float min = _opt.Metrics.MinCongestionFactor;
            for (int i = 0; i < flow.Length; i++) _factors[i] = flow[i] < min ? min : flow[i];
            if (fresh) Paths.Congestion = _factors;
            else Paths.NotifyCongestionChanged();
        }

        private void WriteCsvLine()
        {
            TextWriter w = _csv!;
            long gen = Trips.Generated - _csvGenerated;
            long done = Trips.Completed - _csvCompleted;
            double secs = Trips.TripSecondsTotal - _csvSeconds;
            _csvGenerated = Trips.Generated;
            _csvCompleted = Trips.Completed;
            _csvSeconds = Trips.TripSecondsTotal;
            CultureInfo inv = CultureInfo.InvariantCulture;
            long minute = (long)(Vehicles.CurrentTick / (ulong)_ticksPerMinute);
            w.Write(minute.ToString(inv));
            w.Write(',');
            w.Write(Vehicles.AliveCount.ToString(inv));
            w.Write(',');
            w.Write(gen.ToString(inv));
            w.Write(',');
            w.Write(done.ToString(inv));
            w.Write(',');
            w.Write((done > 0 ? secs / done : 0.0).ToString("0.0", inv));
            w.Write(',');
            w.Write(Metrics.CityFlowPercent.ToString("0.00", inv));
            w.Write('\n');
        }
    }
}
