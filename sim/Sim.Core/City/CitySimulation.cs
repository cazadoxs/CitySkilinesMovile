using System;
using System.Collections.Generic;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Random;
using Urbe.Sim.Roads;
using Urbe.Sim.Traffic;

namespace Urbe.Sim.City
{
    /// <summary>
    /// Raiz de la ciudad (ADR 005): posee la red viaria, la simulacion de trafico y los sistemas de ciudad (zonas, edificios, redes,
    /// poblacion, economia). <see cref="Step"/> sigue el orden de GDD 8.1. Mismas semilla y ordenes, mismo hash.
    /// Unity lee el estado y envia ordenes con <see cref="Enqueue"/> (o <see cref="Execute"/> fuera del tick); no simula.
    /// </summary>
    public sealed partial class CitySimulation
    {
        private Pcg32 _rng;
        private long _money;
        private readonly List<CityCommand> _queue = new List<CityCommand>();
        private readonly List<CityCommand> _drain = new List<CityCommand>();

        public CityBalance Balance { get; }
        public RoadNetwork Network { get; }
        public TrafficSimulation Traffic { get; }
        public ZoneGrid Zones { get; }
        public CityHistory History { get; } = new CityHistory();

        /// <summary>Tramo de autopista por el que entran inmigrantes y mercancias (la conexion exterior).</summary>
        public EntityId OutsideSegment { get; private set; } = EntityId.None;
        public Vec2 OutsidePosition { get; private set; }

        /// <summary>Saldo de la ciudad.</summary>
        public long Money => _money;
        public ulong Tick => Traffic.Tick;
        public CityClock Clock => new CityClock(Traffic.Tick);

        /// <summary>Resultado de la ultima orden encolada y rechazada, y cuantas se rechazaron en total.</summary>
        public CityResult LastRejectReason { get; private set; }
        public int RejectedCommands { get; private set; }
        public int AppliedCommands { get; private set; }

        internal readonly int[] QueryBuffer = new int[1 << 16];

        public CitySimulation(ulong seed, CityBalance? balance = null, TrafficSimulationOptions? trafficOptions = null)
        {
            Balance = balance ?? new CityBalance();
            _rng = new Pcg32(seed, 0x43495459UL);
            _money = Balance.StartMoney;
            float half = Balance.MapHalfSize;
            var cfg = new NetworkConfig { WorldMin = new Vec2(-half, -half), WorldSize = new Vec2(2f * half, 2f * half) };
            Network = new RoadNetwork(RoadTypeCatalog.CreateDefault(), cfg);
            BuildStartHighway();
            TrafficSimulationOptions opt = trafficOptions ?? CreateTrafficOptions(Balance);
            Traffic = new TrafficSimulation(Network, seed ^ 0x54524146UL, opt);
            Zones = new ZoneGrid(Network, Balance);
            InitSystems(seed);
        }

        /// <summary>Opciones de trafico para la ciudad: sin generacion aleatoria de viajes (los viajes los crean los ciudadanos).</summary>
        public static TrafficSimulationOptions CreateTrafficOptions(CityBalance balance)
        {
            var opt = new TrafficSimulationOptions { VehicleCapacity = balance.VehicleCapacity };
            opt.Trips.TripsPerSecond = 0f;
            opt.Trips.MaxActiveVehicles = balance.PhysicalVehicleCap;
            opt.Trips.MaxPending = 512;
            opt.PathsPerTick = 24;
            return opt;
        }

        /// <summary>Partida nueva: una autopista entra por el borde oeste y termina en un nodo; esa es la conexion exterior.</summary>
        private void BuildStartHighway()
        {
            float x0 = -Balance.MapHalfSize + 12f;
            var a = new Vec2(x0, 0f);
            var b = new Vec2(x0 + 150f, 0f);
            EntityId na = Network.AddNodeRaw(a, 0f);
            EntityId nb = Network.AddNodeRaw(b, 0f);
            OutsideSegment = Network.AddSegmentRaw(na, nb, Bezier3.FromLine(a, b), RoadTypeIds.Highway4);
            OutsidePosition = a;
        }

        // ------------------------------------------------------------------ ordenes

        /// <summary>Encola una orden; se aplica al inicio del siguiente tick (GDD 8.1 paso 1).</summary>
        public void Enqueue(CityCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            _queue.Add(command);
        }

        /// <summary>Valida y aplica una orden ahora (fuera de Step). Devuelve Ok o el motivo del rechazo.</summary>
        public CityResult Execute(CityCommand command)
        {
            CityResult r = History.Execute(this, command);
            if (r == CityResult.Ok) { AppliedCommands++; AfterNetworkChange(); }
            else { RejectedCommands++; LastRejectReason = r; }
            return r;
        }

        public bool Undo() { bool ok = History.Undo(this); if (ok) AfterNetworkChange(); return ok; }
        public bool Redo() { bool ok = History.Redo(this); if (ok) AfterNetworkChange(); return ok; }

        private void ProcessCommands()
        {
            if (_queue.Count == 0) return;
            _drain.AddRange(_queue);
            _queue.Clear();
            for (int i = 0; i < _drain.Count; i++) Execute(_drain[i]);
            _drain.Clear();
        }

        // ------------------------------------------------------------------ dinero

        public bool CanAfford(long amount) => _money >= amount;

        /// <summary>Resta dinero (el llamador ya comprobo que alcanza; el saldo puede quedar negativo por mantenimiento).</summary>
        internal void Spend(long amount, CityExpense category)
        {
            _money -= amount;
            Economy.Record(category, amount);
        }

        internal void Refund(long amount, CityExpense category)
        {
            _money += amount;
            Economy.Record(category, -amount);
        }

        // ------------------------------------------------------------------ desbloqueos

        /// <summary>True si el tipo y la densidad de zona estan desbloqueados por el hito de poblacion (GDD 3.3).</summary>

        // ------------------------------------------------------------------ tick

        public void Run(int ticks)
        {
            for (int i = 0; i < ticks; i++) Step();
        }

        // ------------------------------------------------------------------ hash

        /// <summary>Hash del estado completo de la ciudad (GDD 8.14): trafico, red, zonas, dinero y RNG.</summary>
        public ulong ComputeHash()
        {
            ulong h = HashUtil.Mix(0x43495459UL, Traffic.Tick);
            h = HashUtil.Mix(h, Traffic.ComputeHash());
            h = HashUtil.Mix(h, Zones.ComputeHash());
            h = HashUtil.Mix(h, Buildings.ComputeHash());
            h = HashUtil.Mix(h, Supply.ComputeHash());
            h = HashUtil.Mix(h, Coverage.ComputeHash());
            h = HashUtil.Mix(h, Population.ComputeHash());
            h = HashUtil.Mix(h, Trips.ComputeHash());
            h = HashUtil.Mix(h, Economy.ComputeHash());
            h = HashUtil.Mix(h, Fields.ComputeHash());
            h = HashUtil.Mix(h, (ulong)MilestoneIndex);
            for (int i = 0; i < _taxRates.Length; i++) h = HashUtil.MixF(h, _taxRates[i]);
            h = HashUtil.Mix(h, (ulong)PeakPopulation);
            h = HashUtil.MixF(h, _demand.R); h = HashUtil.MixF(h, _demand.C); h = HashUtil.MixF(h, _demand.I); h = HashUtil.MixF(h, _demand.O);
            h = HashUtil.Mix(h, (ulong)_money);
            h = HashUtil.Mix(h, _rng.State);
            h = HashUtil.Mix(h, _rng.Increment);
            return h;
        }
    }
}
