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
        public EntityId OutsideSegment => SegmentAtNode(OutsideNodeWest);
        public EntityId OutsideNodeWest { get; private set; } = EntityId.None;
        public EntityId OutsideNodeEast { get; private set; } = EntityId.None;
        /// <summary>Extremo oeste de la autovia (en el borde del mapa).</summary>
        public Vec2 OutsidePosition { get; private set; }
        /// <summary>Un nodo de la autovia cerca del inicio, util para enganchar una primera calle.</summary>
        public Vec2 HighwayAnchor { get; private set; }
        /// <summary>Tramos con que empieza la partida (solo la autovia).</summary>
        public int StartSegmentCount => _startSegments;
        private int _startSegments;

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
            _startSegments = Network.SegmentCount;
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

        /// <summary>
        /// Partida nueva: una autovia cruza el mapa de borde a borde con una ligera curva en S; sus dos extremos son la conexion exterior.
        /// Las calles del jugador se enganchan a mitad de la autovia (la red parte el tramo y crea el cruce). Con
        /// <see cref="CityBalance.LegacyStubHighway"/> solo hay un tramo recto de 150 m (las pruebas de bajo nivel lo usan).
        /// </summary>
        private void BuildStartHighway()
        {
            float half = Balance.MapHalfSize;
            if (Balance.LegacyStubHighway)
            {
                float xs = -half + 12f;
                var a = new Vec2(xs, 0f);
                var b = new Vec2(xs + 150f, 0f);
                OutsideNodeWest = Network.AddNodeRaw(a, 0f);
                EntityId nb = Network.AddNodeRaw(b, 0f);
                Network.AddSegmentRaw(OutsideNodeWest, nb, Bezier3.FromLine(a, b), RoadTypeIds.Highway4);
                OutsidePosition = a;
                HighwayAnchor = b;
                return;
            }
            const int count = 12;
            float x0 = -half + 12f, x1 = half - 12f;
            var pts = new Vec2[count + 1];
            var nodes = new EntityId[count + 1];
            for (int i = 0; i <= count; i++)
            {
                float x = x0 + (x1 - x0) * i / count;
                pts[i] = new Vec2(x, HighwayY(x));
                nodes[i] = Network.AddNodeRaw(pts[i], 0f);
            }
            for (int i = 0; i < count; i++)
            {
                Vec2 t0 = (pts[Math.Min(i + 1, count)] - pts[Math.Max(i - 1, 0)]) * 0.5f;
                Vec2 t1 = (pts[Math.Min(i + 2, count)] - pts[i]) * 0.5f;
                var curve = new Bezier3(pts[i], pts[i] + t0 * (1f / 3f), pts[i + 1] - t1 * (1f / 3f), pts[i + 1]);
                Network.AddSegmentRaw(nodes[i], nodes[i + 1], curve, RoadTypeIds.Highway4);
            }
            OutsideNodeWest = nodes[0];
            OutsideNodeEast = nodes[count];
            OutsidePosition = pts[0];
            HighwayAnchor = pts[3];
        }

        /// <summary>Ordenada de la autovia inicial en x: una curva en S de poca amplitud (maximo unos 10 grados de pendiente).</summary>
        public float HighwayY(float x)
        {
            float half = Balance.MapHalfSize;
            float u = (x - (-half + 12f)) / (2f * (half - 12f));
            return 55f * (float)Math.Sin(2.0 * Math.PI * u);
        }

        /// <summary>Segmento vivo unido a un nodo de conexion exterior (None si no queda ninguno).</summary>
        private EntityId SegmentAtNode(EntityId node)
        {
            if (!node.IsValid || !Network.IsNodeAlive(node)) return EntityId.None;
            ReadOnlySpan<EntityId> segs = Network.GetNodeSegments(node);
            return segs.Length > 0 ? segs[0] : EntityId.None;
        }

        /// <summary>Conexion exterior numero <paramref name="salt"/> (alterna oeste y este para repartir la entrada de trafico).</summary>
        public EntityId OutsideSegmentFor(int salt)
        {
            EntityId w = SegmentAtNode(OutsideNodeWest), e = SegmentAtNode(OutsideNodeEast);
            if (!e.IsValid) return w;
            if (!w.IsValid) return e;
            return (salt & 1) == 0 ? w : e;
        }

        /// <summary>True si el segmento toca un extremo de la autovia (la conexion exterior), que no se puede demoler.</summary>
        public bool IsOutsideConnection(EntityId segment)
        {
            if (!Network.IsSegmentAlive(segment)) return false;
            ref readonly Segment sg = ref Network.GetSegment(segment);
            return sg.NodeA == OutsideNodeWest || sg.NodeB == OutsideNodeWest
                || (OutsideNodeEast.IsValid && (sg.NodeA == OutsideNodeEast || sg.NodeB == OutsideNodeEast));
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
