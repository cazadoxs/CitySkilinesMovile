using System;
using System.Diagnostics;
using UnityEngine;
using Urbe.Sim.City;
using Urbe.Sim.Commands;
using Urbe.Sim.Roads;
using Urbe.Sim.Traffic;
using Urbe.Sim.Vehicles;

namespace Urbe.Game.Sim
{
    /// <summary>Velocidades de simulación (GDD 11.2): multiplicador sobre el tiempo real.</summary>
    public enum SimSpeed
    {
        Paused = 0,
        X1 = 1,
        X2 = 2,
        X3 = 3,
    }

    /// <summary>Métricas de la simulación para la interfaz y la depuración. Struct para leerlo sin asignar.</summary>
    public readonly struct SimMetricsView
    {
        public readonly ulong Tick;
        public readonly float SimSeconds;
        public readonly int VehicleCount;
        /// <summary>Flujo de la ciudad, 0 a 100 (100 si no hay vehículos).</summary>
        public readonly float FlowPercent;
        public readonly int PendingTrips;
        /// <summary>Ticks por segundo REAL medidos en la última ventana (10 x velocidad si el bucle aguanta).</summary>
        public readonly float MeasuredTicksPerSecond;
        public readonly float LastTickMs;
        public readonly float AvgTickMs;
        public readonly float MaxTickMs;
        public readonly long DroppedTicks;

        public SimMetricsView(ulong tick, float simSeconds, int vehicles, float flow, int pending, float tps,
            float lastMs, float avgMs, float maxMs, long dropped)
        {
            Tick = tick; SimSeconds = simSeconds; VehicleCount = vehicles; FlowPercent = flow; PendingTrips = pending;
            MeasuredTicksPerSecond = tps; LastTickMs = lastMs; AvgTickMs = avgMs; MaxTickMs = maxMs; DroppedTicks = dropped;
        }
    }

    /// <summary>
    /// Puente simulación a Unity (GDD 19.5). Posee la TrafficSimulation, ejecuta ticks de 0,1 s (10 por segundo)
    /// con un acumulador de paso fijo según la velocidad, aplica la cola de comandos entre ticks y publica snapshots que el render
    /// interpola. Orden de ejecución -100 para ir antes que el resto de scripts del fotograma.
    /// Uso desde otras tarjetas: enviar cambios con <see cref="Enqueue"/> (nunca tocar <see cref="Simulation"/> directamente
    /// salvo para leer), pintar leyendo <see cref="Interpolate"/> y suscribirse a <see cref="Ticked"/> y <see cref="SpeedChanged"/>.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class SimHost : MonoBehaviour
    {
        public const float TickDt = 0.1f;
        public const int TicksPerSecond = 10;
        private const int MetricsWindowTicks = 30;

        [SerializeField, Tooltip("Máximo de ticks por fotograma; el exceso se descarta (DroppedTicks).")]
        private int maxTicksPerFrame = 8;

        private FixedStepClock _clock = null!;
        private SnapshotInterpolator? _interp;
        private CitySimulation? _city;
        private TrafficSimulation? _sim;
        private RoadNetwork? _network;
        private SimSpeed _speed = SimSpeed.X1;
        private SimSpeed _lastRunningSpeed = SimSpeed.X1;
        private bool _frameHeld;

        // métricas (sin asignaciones)
        private double _lastTickMs, _sumTickMs, _maxTickMs;
        private int _windowTicks;
        private long _windowStartStamp;
        private float _measuredTps;
        private float _avgTickMs;
        private float _maxWindowMs;

        /// <summary>Simulación en curso (solo lectura desde fuera; los cambios entran por <see cref="Enqueue"/>).</summary>
        public TrafficSimulation? Simulation => _sim;
        public RoadNetwork? Network => _network;
        /// <summary>Ciudad en curso (M3-07), o null si el host aloja solo una simulación de tráfico (pruebas y escenarios de demostración).</summary>
        public CitySimulation? City => _city;
        public bool IsInitialized => _sim != null;

        public SimSpeed Speed => _speed;
        public bool IsPaused => _speed == SimSpeed.Paused;
        /// <summary>Última velocidad distinta de pausa (a la que vuelve <see cref="TogglePause"/>).</summary>
        public SimSpeed LastRunningSpeed => _lastRunningSpeed;
        public ulong Tick => _sim != null ? _sim.Tick : 0UL;
        /// <summary>Fracción [0,1) del tick en curso, para interpolar el render.</summary>
        public float Alpha => EnsureClock().Alpha;

        /// <summary>Tras cada tick de simulación (hilo principal, dentro de Update de SimHost).</summary>
        public event Action<ulong>? Ticked;
        public event Action<SimSpeed>? SpeedChanged;
        /// <summary>Al instalar una simulación nueva (los renders deben reconstruir lo que dependa de la red).</summary>
        public event Action<TrafficSimulation>? SimulationChanged;

        private FixedStepClock EnsureClock() => _clock ??= new FixedStepClock(TickDt, maxTicksPerFrame);

        private void Awake() => EnsureClock();

        /// <summary>Instala una simulación. Las suscripciones a eventos se conservan.</summary>
        public void Initialize(TrafficSimulation sim, RoadNetwork network)
        {
            if (_city != null && !ReferenceEquals(_city.Traffic, sim)) _city = null;
            FixedStepClock clock = EnsureClock();
            _sim = sim ?? throw new ArgumentNullException(nameof(sim));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _interp = new SnapshotInterpolator(sim.Vehicles.Capacity);
            clock.Reset();
            ReleaseFrame();
            ResetMetrics();
            SimulationChanged?.Invoke(sim);
        }

        public void Initialize(SimScenario scenario)
        {
            _city = null;
            Initialize(scenario.Sim, scenario.Network);
        }

        /// <summary>Instala una ciudad (M3-07): el tráfico es el de la ciudad y los comandos del jugador son <see cref="CityCommand"/>.</summary>
        public void Initialize(CitySimulation city)
        {
            if (city == null) throw new ArgumentNullException(nameof(city));
            _city = city;
            Initialize(city.Traffic, city.Network);
        }

        // ------------------------------------------------------------------ velocidad

        public void SetSpeed(SimSpeed speed)
        {
            if (speed == _speed) return;
            _speed = speed;
            if (speed != SimSpeed.Paused) _lastRunningSpeed = speed;
            SpeedChanged?.Invoke(speed);
        }

        /// <summary>Pausa, o vuelve a la última velocidad si ya estaba en pausa.</summary>
        public void TogglePause() => SetSpeed(IsPaused ? _lastRunningSpeed : SimSpeed.Paused);

        /// <summary>Ejecuta exactamente un tick (útil con la simulación en pausa y en pruebas). Aplica la cola de comandos.</summary>
        public void StepOnce()
        {
            if (_sim == null) return;
            RunTick();
        }

        // ------------------------------------------------------------------ comandos

        /// <summary>Encola un comando; se aplica al inicio del siguiente tick (GDD 8.1 paso 1), nunca a mitad de uno.</summary>
        public void Enqueue(ICommand command)
        {
            if (_sim == null) throw new InvalidOperationException("SimHost sin inicializar");
            _sim.Commands.Enqueue(command);
        }

        public int PendingCommands => _sim != null ? _sim.Commands.Count : 0;

        /// <summary>
        /// Ejecuta ya una orden de ciudad (fuera del tick, en el hilo principal, también con la simulación en pausa) y devuelve su resultado.
        /// Notifica <see cref="CommandExecuted"/> para que la interfaz y los renders reaccionen.
        /// </summary>
        public CityResult Execute(CityCommand command)
        {
            if (_city == null) throw new InvalidOperationException("SimHost sin ciudad");
            CityResult r = _city.Execute(command);
            CommandExecuted?.Invoke(command, r);
            return r;
        }

        /// <summary>Tras cada orden de ciudad ejecutada con <see cref="Execute"/> (aceptada o rechazada).</summary>
        public event Action<CityCommand, CityResult>? CommandExecuted;

        /// <summary>
        /// Aplica ya los comandos encolados sin avanzar el tick (construir en pausa, M2-05). Devuelve cuántos se aplicaron.
        /// Seguro: se llama en el hilo principal y nunca a mitad de un tick.
        /// </summary>
        public int FlushCommands()
        {
            if (_sim == null || _sim.Commands.Count == 0) return 0;
            return _sim.Commands.Drain(_sim.Network, _sim.History);
        }

        /// <summary>Deshace el último comando aplicado (también en pausa). Los comandos pendientes se aplican antes.</summary>
        public bool UndoLast()
        {
            if (_sim == null) return false;
            if (_city != null) return _city.Undo();
            FlushCommands();
            return _sim.History.Undo(_sim.Network);
        }

        public bool RedoLast()
        {
            if (_sim == null) return false;
            if (_city != null) return _city.Redo();
            FlushCommands();
            return _sim.History.Redo(_sim.Network);
        }

        public int UndoCount => _city != null ? _city.History.UndoCount : (_sim != null ? _sim.History.UndoCount : 0);
        public int RedoCount => _city != null ? _city.History.RedoCount : (_sim != null ? _sim.History.RedoCount : 0);

        // ------------------------------------------------------------------ lectura para render

        /// <summary>
        /// Interpola entre los dos últimos snapshots con el <see cref="Alpha"/> actual. El resultado vive en
        /// <see cref="SnapshotInterpolator.Output"/> (0..Count) y es válido hasta la siguiente llamada. Sin asignaciones.
        /// Llamar una vez por fotograma desde el render (por ejemplo en LateUpdate).
        /// </summary>
        public SnapshotInterpolator Interpolate()
        {
            if (_sim == null || _interp == null) throw new InvalidOperationException("SimHost sin inicializar");
            if (_sim.Vehicles.Snapshots.Acquire(out SnapshotFrame prev, out SnapshotFrame cur))
            {
                try { _interp.Interpolate(in prev, in cur, EnsureClock().Alpha); }
                finally { _sim.Vehicles.Snapshots.Release(); }
            }
            else
            {
                _interp.Interpolate(default, default, 0f);
            }
            return _interp;
        }

        /// <summary>Acceso directo al par de snapshots (datos crudos). Hay que llamar a <see cref="ReleaseFrame"/>.</summary>
        public bool TryAcquireFrame(out SnapshotFrame previous, out SnapshotFrame current)
        {
            previous = default; current = default;
            if (_sim == null || _frameHeld) return false;
            _frameHeld = _sim.Vehicles.Snapshots.Acquire(out previous, out current);
            return _frameHeld;
        }

        public void ReleaseFrame()
        {
            if (!_frameHeld || _sim == null) return;
            _sim.Vehicles.Snapshots.Release();
            _frameHeld = false;
        }

        /// <summary>Métricas actuales (flujo, vehículos, ticks reales por segundo, coste del tick).</summary>
        public SimMetricsView Metrics
        {
            get
            {
                if (_sim == null) return default;
                return new SimMetricsView(_sim.Tick, _sim.Seconds, _sim.Vehicles.AliveCount, _sim.Metrics.CityFlowPercent,
                    _sim.Trips.Pending, _measuredTps, (float)_lastTickMs, _avgTickMs, _maxWindowMs, EnsureClock().DroppedTicks);
            }
        }

        // ------------------------------------------------------------------ bucle

        private void Update()
        {
            if (_sim == null) return;
            int ticks = EnsureClock().Advance(Time.unscaledDeltaTime, (float)_speed);
            for (int i = 0; i < ticks; i++) RunTick();
        }

        private void RunTick()
        {
            long t0 = Stopwatch.GetTimestamp();
            if (_city != null) _city.Step(); else _sim!.Step();
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            _lastTickMs = ms;
            _sumTickMs += ms;
            if (ms > _maxTickMs) _maxTickMs = ms;
            if (++_windowTicks >= MetricsWindowTicks)
            {
                long now = Stopwatch.GetTimestamp();
                double secs = (now - _windowStartStamp) / (double)Stopwatch.Frequency;
                _measuredTps = secs > 1e-6 ? (float)(_windowTicks / secs) : 0f;
                _avgTickMs = (float)(_sumTickMs / _windowTicks);
                _maxWindowMs = (float)_maxTickMs;
                _windowStartStamp = now;
                _windowTicks = 0; _sumTickMs = 0; _maxTickMs = 0;
            }
            Ticked?.Invoke(_sim.Tick);
        }

        private void ResetMetrics()
        {
            _lastTickMs = _sumTickMs = _maxTickMs = 0;
            _windowTicks = 0;
            _windowStartStamp = Stopwatch.GetTimestamp();
            _measuredTps = 0f; _avgTickMs = 0f; _maxWindowMs = 0f;
        }

        private void OnDestroy() => ReleaseFrame();
    }
}
