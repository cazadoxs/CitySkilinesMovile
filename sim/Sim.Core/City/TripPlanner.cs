using System;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Pathfinding;
using Urbe.Sim.Random;
using Urbe.Sim.Roads;
using Urbe.Sim.Vehicles;
using PathStep = Urbe.Sim.Pathfinding.RouteStep;

namespace Urbe.Sim.City
{
    /// <summary>
    /// Rutina diaria de los ciudadanos y sus viajes (GDD 4.11, 8.1 paso 3, 8.3): casa, trabajo o colegio, compras, casa. Los viajes salen
    /// del edificio real y entran en el <c>TripGenerator</c>. Con el tope de vehiculos fisicos superado el viaje es abstracto (trafico
    /// hibrido): dura el coste de la ruta y suma carga estadistica a los segmentos por los que pasa. Andar o ir en transporte (sin coche
    /// o a menos de 120 m) es un temporizador sin carga. Tambien despacha camiones de mercancias desde y hacia la conexion exterior.
    /// </summary>
    public sealed class TripPlanner
    {
        private const float PhysicalLoadCars = 7.5f;

        private enum AbsState : byte { Free = 0, Awaiting = 1, Active = 2 }

        private struct AbstractTrip
        {
            public AbsState State;
            public int Citizen;
            public int Request;
            public ulong EndTick;
            public ulong Created;
            /// <summary>Distancia en linea recta (m): acota la duracion para que la congestion estimada no se dispare.</summary>
            public float Distance;
            public int RouteStart;
            public ushort RouteLength;
        }

        private readonly CitySimulation _city;
        private readonly PopulationSystem _pop;
        private readonly CityBalance _bal;
        private Pcg32 _rng;

        private bool[] _inFlight = new bool[1024];
        private ulong[] _deadline = new ulong[1024];

        private readonly AbstractTrip[] _abs;
        private readonly int[] _absSeg;
        private readonly float[] _absW;
        private int _absFree;
        private float[] _load = new float[256];
        private readonly PathStep[] _scratch;

        // Contadores para el CSV y los tests.
        public long PhysicalTrips { get; private set; }
        public long AbstractTrips { get; private set; }
        public long WalkTrips { get; private set; }
        public long TransitTrips { get; private set; }
        public long TripsCompleted { get; private set; }
        public long TripsLost { get; private set; }
        public long Trucks { get; private set; }
        /// <summary>Viajes abstractos en curso ahora mismo.</summary>
        public int ActiveAbstract { get; private set; }

        public TripPlanner(CitySimulation city, PopulationSystem population, ulong seed)
        {
            _city = city;
            _pop = population;
            _bal = city.Balance;
            _rng = new Pcg32(seed, 0x54524950UL);
            _abs = new AbstractTrip[_bal.AbstractCapacity];
            _absSeg = new int[_bal.AbstractCapacity * _bal.AbstractRouteLength];
            _absW = new float[_bal.AbstractCapacity * _bal.AbstractRouteLength];
            _scratch = new PathStep[Math.Max(_bal.AbstractRouteLength, 128)];
            city.Traffic.Trips.TripEnded = OnPhysicalEnded;
            city.Traffic.ExtraLoad = _load;
        }

        public bool IsInFlight(int citizen) => citizen < _inFlight.Length && _inFlight[citizen];

        private void EnsureCitizen(int ci)
        {
            if (ci < _inFlight.Length) return;
            int cap = _inFlight.Length;
            while (cap <= ci) cap *= 2;
            Array.Resize(ref _inFlight, cap);
            Array.Resize(ref _deadline, cap);
        }

        // ------------------------------------------------------------------ tick

        public void Step(ulong tick)
        {
            RoutineSlice(tick);
            ProcessAbstract(tick);
        }

        private void RoutineSlice(ulong tick)
        {
            int slices = _bal.RoutineSlices;
            int phase = (int)(tick % (ulong)slices);
            var clock = new CityClock(tick);
            int day = clock.Day;
            float hour = clock.Hour;
            bool weekend = clock.IsWeekend;
            int bound = _pop.CitizenIndexBound;
            for (int i = phase; i < bound; i += slices)
            {
                if (!_pop.IsAlive(i)) continue;
                ref Citizen c = ref _pop.GetMutable(i);
                EnsureCitizen(i);
                switch (c.State)
                {
                    case CitizenState.AtHome:
                        AtHome(i, ref c, tick, day, hour, weekend);
                        break;
                    case CitizenState.AtWork:
                    {
                        bool student = (c.Flags & CitizenFlags.Student) != 0;
                        float stay = (student ? _bal.SchoolHours : _bal.WorkHours) * CityClock.TicksPerHour;
                        stay += (c.StartOffset % 6) * 6f;
                        if (tick - c.StateSince < stay) break;
                        if (PopulationSystem.Hash((uint)i, (uint)day * 3u + 1u) % 100 < _bal.ShopChance * 100f && TryShop(i, ref c, tick)) break;
                        GoHome(i, ref c, tick);
                        break;
                    }
                    case CitizenState.AtShop:
                        if (tick - c.StateSince >= (ulong)_bal.ShopStayTicks) GoHome(i, ref c, tick);
                        break;
                    default:
                        if (_inFlight[i] && tick > _deadline[i]) Arrive(i, tick, false);
                        else if (!_inFlight[i]) Arrive(i, tick, true);
                        break;
                }
            }
        }

        private void AtHome(int i, ref Citizen c, ulong tick, int day, float hour, bool weekend)
        {
            if (c.LastTripDay == day) return;
            BuildingSystem bs = _city.Buildings;
            bool student = (c.Flags & CitizenFlags.Student) != 0;
            bool hasPlace = c.Workplace.IsValid && bs.IsAlive(c.Workplace) && !bs.Get(c.Workplace).IsAbandoned;
            if (hasPlace && !weekend)
            {
                float start = student ? 7f + (c.StartOffset % 10) * 0.1f : 6f + c.StartOffset * 0.1f;
                if (hour >= start && hour < start + 4f)
                {
                    c.LastTripDay = day;
                    Begin(i, ref c, c.Workplace.Index, CitizenState.ToWork, tick);
                }
                return;
            }
            // Sin trabajo, jubilados, ninos sin colegio y fines de semana: salida de compras u ocio.
            if (c.Age < 6) return;
            if (hour >= 10f && hour < 19f && PopulationSystem.Hash((uint)i, (uint)day * 3u + 2u) % 100 < _bal.LeisureChance * 100f)
            {
                c.LastTripDay = day;
                TryShop(i, ref c, tick);
            }
        }

        private bool TryShop(int i, ref Citizen c, ulong tick)
        {
            ReadOnlySpan<int> shops = _pop.Commercial;
            if (shops.Length == 0) return false;
            BuildingSystem bs = _city.Buildings;
            int from = c.Location;
            if (!bs.IsAlive(from)) return false;
            Vec2 p = bs.Get(from).Pos;
            int best = -1;
            float bestD = float.MaxValue;
            int tries = Math.Min(8, shops.Length);
            for (int k = 0; k < tries; k++)
            {
                int bi = shops[_rng.NextInt(shops.Length)];
                if (!bs.IsAlive(bi) || bs.Get(bi).IsAbandoned) continue;
                float d = Vec2.Distance(p, bs.Get(bi).Pos);
                if (d < bestD) { bestD = d; best = bi; }
            }
            if (best < 0 || best == from) return false;
            Begin(i, ref c, best, CitizenState.ToShop, tick);
            return true;
        }

        private void GoHome(int i, ref Citizen c, ulong tick)
        {
            BuildingSystem bs = _city.Buildings;
            EntityId home = _pop.GetHousehold(c.Household).Home;
            if (!bs.IsAlive(home)) { c.State = CitizenState.AtHome; return; }
            if (c.Location == home.Index) { c.State = CitizenState.AtHome; c.StateSince = tick; return; }
            Begin(i, ref c, home.Index, CitizenState.ToHome, tick);
        }

        // ------------------------------------------------------------------ viajes

        private void Begin(int i, ref Citizen c, int dest, CitizenState state, ulong tick)
        {
            BuildingSystem bs = _city.Buildings;
            c.State = state;
            c.Dest = dest;
            c.StateSince = tick;
            int from = c.Location;
            if (!bs.IsAlive(from) || !bs.IsAlive(dest) || from == dest) { _inFlight[i] = false; Arrive(i, tick, true); return; }
            ref readonly Building a = ref bs.Get(from);
            ref readonly Building b = ref bs.Get(dest);
            float dist = Vec2.Distance(a.Pos, b.Pos);
            bool car = _pop.GetHousehold(c.Household).Cars > 0;
            _deadline[i] = tick + (ulong)_bal.MaxTripTicks;
            if (dist < _bal.WalkDistance)
            {
                WalkTrips++;
                StartTimer(i, tick, dist * _bal.AbstractDetour / _bal.WalkSpeed);
                return;
            }
            bool reachable = _city.Supply.ComponentOf(a.Segment) >= 0 && _city.Supply.ComponentOf(a.Segment) == _city.Supply.ComponentOf(b.Segment);
            if (!car || !reachable)
            {
                TransitTrips++;
                StartTimer(i, tick, 10f + dist * _bal.AbstractDetour / _bal.TransitSpeed);
                return;
            }
            var traffic = _city.Traffic;
            bool room = traffic.Vehicles.AliveCount + traffic.Trips.Pending < _bal.PhysicalVehicleCap && traffic.Trips.Pending < _bal.MaxPendingPhysical;
            if (room && traffic.Trips.RequestTrip(a.Segment, b.Segment, VehicleTypeId.Car, i + 1))
            {
                _inFlight[i] = true;
                PhysicalTrips++;
                return;
            }
            StartAbstract(i, tick, a.Segment, b.Segment, dist);
        }

        private int AllocAbs()
        {
            int n = _abs.Length;
            for (int k = 0; k < n; k++)
            {
                int s = (_absFree + k) % n;
                if (_abs[s].State == AbsState.Free) { _absFree = (s + 1) % n; return s; }
            }
            return -1;
        }

        private void StartTimer(int ci, ulong tick, float seconds)
        {
            int s = AllocAbs();
            if (s < 0) { _inFlight[ci] = false; Arrive(ci, tick, true); return; }
            ulong ticks = (ulong)Math.Max(1f, seconds / _city.Traffic.Vehicles.Config.Dt);
            _abs[s] = new AbstractTrip { State = AbsState.Active, Citizen = ci, Request = 0, EndTick = tick + ticks, Created = tick, RouteLength = 0 };
            _inFlight[ci] = true;
            ActiveAbstract++;
        }

        private void StartAbstract(int ci, ulong tick, EntityId from, EntityId to, float dist)
        {
            AbstractTrips++;
            int s = AllocAbs();
            if (s < 0) { _inFlight[ci] = false; Arrive(ci, tick, true); return; }
            var req = new PathRequest
            {
                OriginSegment = from,
                OriginDirs = TravelDirs.Both,
                DestinationSegment = to,
                DestinationDirs = TravelDirs.Both,
                Options = PathOptions.WithSeed(PopulationSystem.Hash((uint)ci, (uint)tick), VehicleTypeId.Car),
                Priority = PathPriority.Private,
            };
            if (!_city.Traffic.PathQueue.TryEnqueue(in req, out int id))
            {
                // Sin hueco en la cola de rutas: duracion estimada por distancia, sin carga.
                StartTimer(ci, tick, dist * _bal.AbstractDetour / 8f);
                return;
            }
            _abs[s] = new AbstractTrip { State = AbsState.Awaiting, Citizen = ci, Request = id, Created = tick, Distance = dist };
            _inFlight[ci] = true;
            ActiveAbstract++;
        }

        private void ProcessAbstract(ulong tick)
        {
            RoadNetwork net = _city.Network;
            var queue = _city.Traffic.PathQueue;
            float dt = _city.Traffic.Vehicles.Config.Dt;
            for (int s = 0; s < _abs.Length; s++)
            {
                ref AbstractTrip t = ref _abs[s];
                if (t.State == AbsState.Free) continue;
                if (t.State == AbsState.Awaiting)
                {
                    PathStatus st = queue.GetStatus(t.Request);
                    if (st == PathStatus.Pending)
                    {
                        if (tick - t.Created > (ulong)_bal.MaxTripTicks) FinishAbs(s, tick, false);
                        continue;
                    }
                    if (st != PathStatus.Found || !queue.TryGetResult(t.Request, _scratch, out int len, out float cost) || len < 1)
                    {
                        // Sin ruta: llega "por otros medios" tras un tiempo razonable.
                        t.State = AbsState.Active;
                        t.EndTick = tick + 600;
                        t.RouteLength = 0;
                        continue;
                    }
                    int n = Math.Min(len, _bal.AbstractRouteLength);
                    EnsureLoad(net.SegmentIndexBound);
                    t.RouteStart = s * _bal.AbstractRouteLength;
                    for (int k = 0; k < n; k++)
                    {
                        EntityId seg = _scratch[k].Segment;
                        float w = 0f;
                        if (net.IsSegmentAlive(seg))
                        {
                            ref readonly Segment sg = ref net.GetSegment(seg);
                            int lanes = Math.Max(1, (int)(_scratch[k].Forward ? sg.LanesForward : sg.LanesBackward));
                            w = PhysicalLoadCars / (lanes * Math.Max(sg.Length, 8f));
                        }
                        _absSeg[t.RouteStart + k] = seg.Index;
                        _absW[t.RouteStart + k] = w;
                        _load[seg.Index] += w;
                    }
                    t.RouteLength = (ushort)n;
                    float freeFlow = t.Distance * _bal.AbstractDetour / 12f;
                    float seconds = Math.Max(freeFlow * 0.8f, Math.Min(cost * 1.1f, freeFlow * 3f));
                    t.EndTick = tick + (ulong)Math.Min(_bal.MaxTripTicks * 0.8f, Math.Max(1f, seconds / dt));
                    t.State = AbsState.Active;
                    continue;
                }
                if (tick >= t.EndTick) FinishAbs(s, tick, true);
            }
        }

        private void FinishAbs(int s, ulong tick, bool ok)
        {
            ref AbstractTrip t = ref _abs[s];
            for (int k = 0; k < t.RouteLength; k++)
            {
                int si = _absSeg[t.RouteStart + k];
                if (si < _load.Length) _load[si] = Math.Max(0f, _load[si] - _absW[t.RouteStart + k]);
            }
            int ci = t.Citizen;
            t.State = AbsState.Free;
            t.RouteLength = 0;
            ActiveAbstract--;
            _inFlight[ci] = false;
            Arrive(ci, tick, ok);
        }

        private void EnsureLoad(int bound)
        {
            if (_load.Length >= bound) return;
            var bigger = new float[Math.Max(bound, _load.Length * 2)];
            Array.Copy(_load, bigger, _load.Length);
            _load = bigger;
            _city.Traffic.ExtraLoad = _load;
        }

        private void OnPhysicalEnded(int tag, bool arrived)
        {
            int ci = tag - 1;
            if (ci < 0 || ci >= _inFlight.Length || !_inFlight[ci]) return;
            _inFlight[ci] = false;
            Arrive(ci, _city.Tick, arrived);
        }

        private void Arrive(int ci, ulong tick, bool ok)
        {
            ref Citizen c = ref _pop.GetMutable(ci);
            _inFlight[ci] = false;
            if ((c.Flags & CitizenFlags.Zombie) != 0) { _pop.Release(ci); return; }
            if (ok) TripsCompleted++; else TripsLost++;
            BuildingSystem bs = _city.Buildings;
            EntityId home = _pop.GetHousehold(c.Household).Home;
            switch (c.State)
            {
                case CitizenState.ToWork:
                    c.State = bs.IsAlive(c.Dest) ? CitizenState.AtWork : CitizenState.AtHome;
                    break;
                case CitizenState.ToShop:
                    c.State = bs.IsAlive(c.Dest) ? CitizenState.AtShop : CitizenState.AtHome;
                    break;
                default:
                    c.State = CitizenState.AtHome;
                    break;
            }
            c.Location = c.State == CitizenState.AtHome ? (bs.IsAlive(home) ? home.Index : c.Location) : c.Dest;
            c.StateSince = tick;
        }

        // ------------------------------------------------------------------ camiones

        /// <summary>Un edificio con empleos de comercio o industria a veces recibe o envia un camion por la conexion exterior.</summary>
        internal void MaybeTruck(int building)
        {
            ref readonly Building b = ref _city.Buildings.Get(building);
            int jobs = b.JobsLow + b.JobsHigh;
            if (jobs == 0 || b.Zone == ZoneType.Office) return;
            if (_rng.NextFloat() >= jobs * _bal.TruckChancePerJob) return;
            var traffic = _city.Traffic;
            if (traffic.Vehicles.AliveCount + traffic.Trips.Pending >= _bal.PhysicalVehicleCap) return;
            if (!_city.Network.IsSegmentAlive(b.Segment) || !_city.Supply.IsConnectedToOutside(b.Segment)) return;
            bool export = b.Zone == ZoneType.Industrial && _rng.NextFloat() < _bal.TruckVehiclePerExport;
            EntityId outside = _city.OutsideSegmentFor((int)Trucks);
            if (!outside.IsValid) return;
            EntityId from = export ? b.Segment : outside;
            EntityId to = export ? outside : b.Segment;
            if (traffic.Trips.RequestTrip(from, to, VehicleTypeId.Truck, 0)) Trucks++;
        }

        public ulong ComputeHash()
        {
            ulong h = HashUtil.Mix(0x54524950UL, _rng.State);
            h = HashUtil.Mix(h, _rng.Increment);
            h = HashUtil.Mix(h, (ulong)(PhysicalTrips + AbstractTrips * 3 + WalkTrips * 7 + TransitTrips * 11));
            h = HashUtil.Mix(h, (ulong)(TripsCompleted + TripsLost * 13 + Trucks * 17));
            h = HashUtil.Mix(h, (ulong)ActiveAbstract);
            return h;
        }
    }
}
