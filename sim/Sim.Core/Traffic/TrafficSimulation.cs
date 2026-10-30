using System;
using System.Globalization;
using System.IO;
using Urbe.Sim.Commands;
using Urbe.Sim.Junctions;
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
        /// <summary>Presupuesto de expansiones de Dijkstra por tick para reconstruir los landmarks ALT tras un cambio de grafo (M1-16).</summary>
        public int LandmarkBudgetPerTick = 2000;
        public int PathResultCapacity = 512;
        /// <summary>Cada cuanto se pasa el flujo por segmento al pathfinding como factor de congestion.</summary>
        public float CongestionPublishSeconds = 30f;
        /// <summary>
        /// Usa la puerta simple <see cref="GapOnlyGate"/> (sin prioridad, conflictos ni semaforos) en vez del
        /// <see cref="JunctionController"/> (ADR 004), que es la puerta por defecto. Solo para pruebas y comparativas.
        /// </summary>
        public bool UseGapOnlyGate;
        /// <summary>Deteccion y resolucion de bloqueo total (GDD 8.7, M1-11): tiempos y opcion de desaparicion.</summary>
        public GridlockConfig Gridlock = new GridlockConfig();
    }

    /// <summary>
    /// Agrupa red, vehiculos, puerta de cruces, pathfinding, generador de viajes y metricas, y avanza un tick en el orden del
    /// GDD 8.1: comandos, viajes, cola de rutas, movimiento, bloqueo total (M1-11), metricas. La puerta es inyectable; por defecto es el JunctionController (M1-07b) y GapOnlyGate solo con la opcion explicita.
    /// </summary>
    public sealed class TrafficSimulation
    {
        private readonly TrafficSimulationOptions _opt;
        private readonly float[] _noFactors = Array.Empty<float>();
        private float[] _factors;
        private ulong _lastGraphEpoch;
        private readonly int _publishTicks;
        private int _publishCounter;

        private TextWriter? _csv;
        private readonly int _ticksPerMinute;
        private long _csvGenerated, _csvCompleted;
        private double _csvSeconds;

        public RoadNetwork Network { get; }
        /// <summary>Puerta de cruces en uso (JunctionController por defecto).</summary>
        public IJunctionGate Gate { get; }
        public VehicleSystem Vehicles { get; }
        public PathFinder Paths { get; }
        public PathRequestQueue PathQueue { get; }
        public TripGenerator Trips { get; }
        public TrafficMetrics Metrics { get; }
        public GridlockResolver Gridlock { get; }
        public CommandQueue Commands { get; } = new CommandQueue();
        public CommandHistory History { get; } = new CommandHistory();

        public ulong Tick => Vehicles.CurrentTick;
        public float Seconds => Vehicles.CurrentTick * Vehicles.Config.Dt;

        public TrafficSimulation(RoadNetwork network, ulong seed, TrafficSimulationOptions? options = null, IJunctionGate? gate = null)
        {
            _opt = options ?? new TrafficSimulationOptions();
            Network = network ?? throw new ArgumentNullException(nameof(network));
            IJunctionGate junctions = gate ?? (_opt.UseGapOnlyGate ? (IJunctionGate)new GapOnlyGate(network) : new JunctionController(network));
            Gate = junctions;
            Vehicles = new VehicleSystem(network, junctions, _opt.VehicleCapacity, seed, _opt.Vehicle);
            Paths = new PathFinder(network, _opt.PathCost);
            PathQueue = new PathRequestQueue(Paths, _opt.PathResultCapacity, _opt.Trips.MaxRouteLength);
            Trips = new TripGenerator(network, Vehicles, PathQueue, seed, _opt.Trips);
            Metrics = new TrafficMetrics(network, Vehicles, _opt.Metrics);
            Metrics.SegmentFlowReset = PublishCongestion;
            Gridlock = new GridlockResolver(network, Vehicles, junctions, Trips, _opt.Gridlock, Metrics.Gridlock);
            _factors = _noFactors;
            _publishTicks = Math.Max(1, (int)Math.Round(_opt.CongestionPublishSeconds / Vehicles.Config.Dt));
            _ticksPerMinute = Math.Max(1, (int)Math.Round(60f / Vehicles.Config.Dt));
            _lastGraphEpoch = network.GraphEpoch;
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
            if (Network.GraphEpoch != _lastGraphEpoch)
            {
                _lastGraphEpoch = Network.GraphEpoch;
                if (Network.SegmentIndexBound > 0) Paths.StartLandmarkBuild();
            }
            if (Paths.LandmarksBuilding) Paths.AdvanceLandmarks(_opt.LandmarkBudgetPerTick);
            Trips.Tick();
            PathQueue.Process(_opt.PathsPerTick);
            Vehicles.Update();
            Gridlock.Step();
            Trips.CollectFinished();
            Metrics.Sample();
            if (++_publishCounter >= _publishTicks)
            {
                _publishCounter = 0;
                PublishCongestion();
            }
            if (_csv != null && Vehicles.CurrentTick % (ulong)_ticksPerMinute == 0) WriteCsvLine();
        }

        /// <summary>
        /// Hash del estado completo (GDD 8.14, M1-12): red, vehiculos (con su RNG), cruces y semaforos, RNG del generador de viajes
        /// y contadores enteros de viajes, bloqueo total y cola. Solo enteros: no depende de formato ni de cultura.
        /// </summary>
        public ulong ComputeHash()
        {
            ulong h = Mix(0x5452414646494355UL, Vehicles.CurrentTick);
            h = Mix(h, Network.ComputeHash());
            h = Mix(h, Vehicles.ComputeHash());
            if (Gate is JunctionController jc) h = Mix(h, jc.ComputeHash());
            h = Mix(h, Trips.RngState);
            h = Mix(h, Trips.RngIncrement);
            h = Mix(h, (ulong)Trips.Generated);
            h = Mix(h, (ulong)Trips.Completed);
            h = Mix(h, (ulong)Trips.Lost);
            h = Mix(h, (ulong)Trips.Unroutable);
            h = Mix(h, (ulong)Trips.Dropped);
            h = Mix(h, (ulong)Trips.Rerouted);
            h = Mix(h, (ulong)Trips.Pending);
            h = Mix(h, (ulong)Vehicles.AliveCount);
            GridlockStats g = Metrics.Gridlock;
            h = Mix(h, (ulong)g.Cycles);
            h = Mix(h, (ulong)g.ForcedAttempts);
            h = Mix(h, (ulong)g.ForcedPasses);
            h = Mix(h, (ulong)g.ForcedFailed);
            h = Mix(h, (ulong)g.Reroutes);
            h = Mix(h, (ulong)g.Despawns);
            h = Mix(h, (ulong)g.Unresolved);
            return h;
        }

        private static ulong Mix(ulong h, ulong x)
        {
            unchecked
            {
                ulong z = (h ^ x) + 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
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
