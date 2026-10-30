using System;
using System.Collections.Generic;
using Urbe.Sim.Ids;
using Urbe.Sim.Pathfinding;
using Urbe.Sim.Random;
using Urbe.Sim.Roads;
using Urbe.Sim.Vehicles;
using PathStep = Urbe.Sim.Pathfinding.RouteStep;

namespace Urbe.Sim.Traffic
{
    /// <summary>Punto de demanda: un segmento con peso de generacion (origen) y de atraccion (destino).</summary>
    public readonly struct DemandPoint
    {
        public readonly EntityId Segment;
        public readonly float GenerationWeight;
        public readonly float AttractionWeight;

        public DemandPoint(EntityId segment, float generationWeight, float attractionWeight)
        {
            Segment = segment;
            GenerationWeight = generationWeight;
            AttractionWeight = attractionWeight;
        }
    }

    public sealed class TripGeneratorConfig
    {
        /// <summary>Viajes nuevos por segundo simulado.</summary>
        public float TripsPerSecond = 5f;
        public float TruckFraction = 0.1f;
        /// <summary>Viajes simultaneos esperando ruta o hueco de salida (cola acotada: si esta llena, el viaje se descarta).</summary>
        public int MaxPending = 256;
        /// <summary>No se generan viajes si vivos + pendientes alcanzan esta cifra.</summary>
        public int MaxActiveVehicles = int.MaxValue;
        /// <summary>Pasos maximos de una ruta (debe ser &lt;= el de la <see cref="PathRequestQueue"/>).</summary>
        public int MaxRouteLength = 128;
        public int SpawnRetryTicks = 10;
        /// <summary>Un viaje que no consigue salir en este tiempo se pierde.</summary>
        public float MaxWaitSeconds = 120f;
        public int MaxRerouteAttempts = 20;
        /// <summary>Rutas que se piden por viaje si las anteriores no tienen cadena de carriles valida.</summary>
        public int MaxPlans = 8;
        /// <summary>Posicion de salida y de llegada dentro del carril, como fraccion de la longitud del segmento.</summary>
        public float EndpointFraction = 0.4f;
    }

    /// <summary>
    /// Generador de viajes (GDD 8.1 paso 2): elige origen y destino con pesos (Pcg32 con semilla), pide la ruta a la cola de
    /// pathfinding, crea el vehiculo y reintenta mientras el carril de salida este lleno. Tambien re-rutea los vehiculos con
    /// <c>NeedsReroute</c>. Invariante: Generated = Completed + Lost + vivos + Pending. Sin asignaciones por tick.
    /// </summary>
    public sealed class TripGenerator : IRerouteSource
    {
        private enum State : byte { Free, Awaiting, Ready }

        private struct Trip
        {
            public State State;
            public byte Type;
            public int RequestId;
            public ulong Created;
            public ulong NextAttempt;
            public EntityId Origin;
            public EntityId Destination;
            public byte Plans;
            public int RouteLength;
        }

        private readonly RoadNetwork _net;
        private readonly VehicleSystem _veh;
        private readonly PathRequestQueue _queue;
        private readonly TripGeneratorConfig _cfg;
        private Pcg32 _rng;

        private readonly List<DemandPoint> _points = new List<DemandPoint>();
        private float[] _cumGen = Array.Empty<float>();
        private float[] _cumAttr = Array.Empty<float>();
        private bool _dirty = true;

        private readonly Trip[] _trips;
        private readonly PathStep[] _tripRoutes;
        private readonly PathStep[] _scratch;
        private readonly int[] _choice;
        private readonly LaneId[] _laneScratch;
        private float _acc;
        private int _pending;

        // Por indice de vehiculo.
        private readonly bool[] _tracked;
        private readonly ulong[] _spawnTick;
        private readonly EntityId[] _destSeg;
        private readonly int[] _rerouteId;
        private readonly byte[] _rerouteTries;
        private readonly int[] _rerouteList;
        private int _rerouteCount;
        private readonly bool[] _inRerouteList;

        private int _lastArrived;
        private double _tripSecondsTotal;

        public long Generated { get; private set; }
        public long Completed { get; private set; }
        /// <summary>Viajes que no llegaron: sin ruta, sin hueco a tiempo, vehiculo dado de baja por una edicion o re-ruta imposible.</summary>
        public long Lost { get; private set; }
        public long Unroutable { get; private set; }
        /// <summary>Viajes descartados al nacer (cola llena o tope de vehiculos); no cuentan como generados.</summary>
        public long Dropped { get; private set; }
        public long Rerouted { get; private set; }
        public int Pending => _pending;

        /// <summary>True si el vehiculo lo dio de alta este generador (y por tanto puede rehacerle la ruta; M1-11).</summary>
        public bool CanReroute(int vehicle) => vehicle >= 0 && vehicle < _tracked.Length && _tracked[vehicle];
        /// <summary>Suma de tiempos de viaje (s) de los completados.</summary>
        public double TripSecondsTotal => _tripSecondsTotal;
        public TripGeneratorConfig Config => _cfg;
        public int DemandPointCount => _points.Count;

        public TripGenerator(RoadNetwork network, VehicleSystem vehicles, PathRequestQueue queue, ulong seed,
            TripGeneratorConfig? config = null)
        {
            _net = network ?? throw new ArgumentNullException(nameof(network));
            _veh = vehicles ?? throw new ArgumentNullException(nameof(vehicles));
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _cfg = config ?? new TripGeneratorConfig();
            _rng = new Pcg32(seed, 0x545249UL);
            int cap = vehicles.Capacity;
            _trips = new Trip[_cfg.MaxPending];
            _tripRoutes = new PathStep[_cfg.MaxPending * _cfg.MaxRouteLength];
            _scratch = new PathStep[_cfg.MaxRouteLength];
            _choice = new int[_cfg.MaxRouteLength * 8];
            _laneScratch = new LaneId[_cfg.MaxRouteLength];
            _tracked = new bool[cap];
            _spawnTick = new ulong[cap];
            _destSeg = new EntityId[cap];
            _rerouteId = new int[cap];
            _rerouteTries = new byte[cap];
            _rerouteList = new int[cap];
            _inRerouteList = new bool[cap];
        }

        public void AddDemandPoint(EntityId segment, float generationWeight, float attractionWeight)
        {
            if (generationWeight < 0f || attractionWeight < 0f) throw new ArgumentOutOfRangeException(nameof(generationWeight));
            _points.Add(new DemandPoint(segment, generationWeight, attractionWeight));
            _dirty = true;
        }

        public void ClearDemand()
        {
            _points.Clear();
            _dirty = true;
        }

        // ------------------------------------------------------------------ tick

        /// <summary>Genera viajes, recoge rutas resueltas, da de alta vehiculos y gestiona re-rutas. Antes de procesar la cola.</summary>
        public void Tick()
        {
            ulong now = _veh.CurrentTick;
            _acc += _cfg.TripsPerSecond * _veh.Config.Dt;
            while (_acc >= 1f)
            {
                _acc -= 1f;
                CreateTrip(now);
            }
            ProcessTrips(now);
            ProcessReroutes();
        }

        /// <summary>Tras <c>VehicleSystem.Update</c>: contabiliza llegadas/bajas y marca vehiculos que necesitan re-ruta.</summary>
        public void CollectFinished()
        {
            ulong now = _veh.CurrentTick;
            float dt = _veh.Config.Dt;
            int removed = 0;
            double seconds = 0;
            int high = _veh.HighWater;
            for (int i = 0; i < high; i++)
            {
                if (!_tracked[i]) continue;
                ref readonly Vehicle v = ref _veh.GetVehicle(i);
                if (!v.IsAlive)
                {
                    _tracked[i] = false;
                    removed++;
                    seconds += (now - _spawnTick[i]) * dt;
                    continue;
                }
                if ((v.Flags & VehicleFlags.NeedsReroute) != 0 && !_inRerouteList[i])
                {
                    _inRerouteList[i] = true;
                    _rerouteId[i] = 0;
                    _rerouteTries[i] = 0;
                    _rerouteList[_rerouteCount++] = i;
                }
            }
            int arrived = _veh.ArrivedCount - _lastArrived;
            _lastArrived = _veh.ArrivedCount;
            if (arrived > removed) arrived = removed;
            Completed += arrived;
            Lost += removed - arrived;
            if (removed > 0 && arrived > 0) _tripSecondsTotal += seconds * arrived / removed;
        }

        // ------------------------------------------------------------------ creacion

        private void Rebuild()
        {
            int n = _points.Count;
            if (_cumGen.Length != n)
            {
                _cumGen = new float[n];
                _cumAttr = new float[n];
            }
            float g = 0f, a = 0f;
            for (int i = 0; i < n; i++)
            {
                g += _points[i].GenerationWeight;
                a += _points[i].AttractionWeight;
                _cumGen[i] = g;
                _cumAttr[i] = a;
            }
            _dirty = false;
        }

        private int Pick(float[] cum)
        {
            int n = cum.Length;
            float total = cum[n - 1];
            if (!(total > 0f)) return -1;
            float r = _rng.NextFloat() * total;
            int lo = 0, hi = n - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (cum[mid] > r) hi = mid; else lo = mid + 1;
            }
            return lo;
        }

        private void CreateTrip(ulong now)
        {
            if (_dirty) Rebuild();
            if (_points.Count < 2) return;
            if (_pending >= _cfg.MaxPending || _veh.AliveCount + _pending >= _cfg.MaxActiveVehicles)
            {
                Dropped++;
                return;
            }
            int o = Pick(_cumGen);
            int d = Pick(_cumAttr);
            for (int k = 0; k < 4 && d == o && d >= 0; k++) d = Pick(_cumAttr);
            if (o < 0 || d < 0 || o == d) return;
            EntityId os = _points[o].Segment, ds = _points[d].Segment;
            if (!_net.IsSegmentAlive(os) || !_net.IsSegmentAlive(ds)) return;

            byte type = _rng.NextFloat() < _cfg.TruckFraction ? VehicleTypeId.Truck : VehicleTypeId.Car;
            var req = new PathRequest
            {
                OriginSegment = os,
                OriginDirs = TravelDirs.Both,
                DestinationSegment = ds,
                DestinationDirs = TravelDirs.Both,
                Options = PathOptions.WithSeed(_rng.NextUInt(), type),
                Priority = PathPriority.Private,
            };
            if (!_queue.TryEnqueue(in req, out int id))
            {
                Dropped++;
                return;
            }
            for (int s = 0; s < _trips.Length; s++)
            {
                if (_trips[s].State != State.Free) continue;
                _trips[s] = new Trip { State = State.Awaiting, Type = type, RequestId = id, Created = now, NextAttempt = now, Origin = os, Destination = ds, Plans = 1 };
                _pending++;
                Generated++;
                return;
            }
            Dropped++; // inalcanzable: _pending < MaxPending garantiza un hueco
        }

        private void ProcessTrips(ulong now)
        {
            ulong maxWait = (ulong)(_cfg.MaxWaitSeconds / _veh.Config.Dt);
            for (int s = 0; s < _trips.Length; s++)
            {
                ref Trip t = ref _trips[s];
                if (t.State == State.Free) continue;
                if (t.State == State.Awaiting)
                {
                    PathStatus st = _queue.GetStatus(t.RequestId);
                    if (st == PathStatus.Pending)
                    {
                        if (now - t.Created > maxWait) Fail(ref t, false);
                        continue;
                    }
                    int off = s * _cfg.MaxRouteLength;
                    if (st != PathStatus.Found
                        || !_queue.TryGetResult(t.RequestId, new Span<PathStep>(_tripRoutes, off, _cfg.MaxRouteLength), out int len, out _)
                        || len < 1)
                    {
                        Fail(ref t, true);
                        continue;
                    }
                    t.RouteLength = len;
                    t.State = State.Ready;
                }
                if (now < t.NextAttempt) continue;
                if (TrySpawn(s, ref t, now) || t.State != State.Ready) continue;
                if (now - t.Created > maxWait) Fail(ref t, false);
                else t.NextAttempt = now + (ulong)_cfg.SpawnRetryTicks;
            }
        }

        private void Fail(ref Trip t, bool unroutable)
        {
            t.State = State.Free;
            _pending--;
            Lost++;
            if (unroutable) Unroutable++;
        }

        /// <summary>
        /// La ruta por segmentos no tiene cadena de carriles valida (el pathfinding no conoce los carriles y no hay cambio de
        /// carril): pide otra ruta con mas ruido de conductor; tras <see cref="TripGeneratorConfig.MaxPlans"/> intentos, el viaje se pierde.
        /// </summary>
        private void Replan(ref Trip t, ulong now)
        {
            if (t.Plans >= _cfg.MaxPlans)
            {
                Fail(ref t, true);
                return;
            }
            var req = new PathRequest
            {
                OriginSegment = t.Origin,
                OriginDirs = TravelDirs.Both,
                DestinationSegment = t.Destination,
                DestinationDirs = TravelDirs.Both,
                Options = PathOptions.WithSeed(_rng.NextUInt(), t.Type),
                Priority = PathPriority.Private,
            };
            req.Options.NoiseAmplitude = Math.Min(0.5f, 0.1f + 0.1f * t.Plans);
            if (!_queue.TryEnqueue(in req, out int id))
            {
                t.NextAttempt = now + (ulong)_cfg.SpawnRetryTicks;
                return;
            }
            t.Plans++;
            t.RequestId = id;
            t.State = State.Awaiting;
        }

        private bool TrySpawn(int slot, ref Trip t, ulong now)
        {
            int off = slot * _cfg.MaxRouteLength;
            if (!ResolveLanes(_tripRoutes, off, t.RouteLength, LaneId.None, out float startS, out float targetS)) { Replan(ref t, now); return false; }
            VehicleType vt = _veh.GetVehicleType(t.Type);
            if (startS < vt.Length) startS = Math.Min(vt.Length, _net.GetLane(_laneScratch[0]).Length);
            if (t.RouteLength == 1 && targetS < startS) targetS = startS;
            if (!_veh.TrySpawn(t.Type, new ReadOnlySpan<LaneId>(_laneScratch, 0, t.RouteLength), startS, targetS, 0f, out EntityId id))
                return false;
            int i = id.Index;
            // Si el alta provoco el cambio de epoca, el vehiculo anterior de este hueco ya se dio de baja sin pasar por CollectFinished.
            if (_tracked[i]) Lost++;
            _tracked[i] = true;
            _spawnTick[i] = now;
            _destSeg[i] = t.Destination;
            _rerouteId[i] = 0;
            _rerouteTries[i] = 0;
            _inRerouteList[i] = false;
            t.State = State.Free;
            _pending--;
            return true;
        }

        // ------------------------------------------------------------------ re-ruta

        private void ProcessReroutes()
        {
            int w = 0;
            for (int r = 0; r < _rerouteCount; r++)
            {
                int i = _rerouteList[r];
                ref readonly Vehicle v = ref _veh.GetVehicle(i);
                if (!v.IsAlive || (v.Flags & VehicleFlags.NeedsReroute) == 0)
                {
                    _inRerouteList[i] = false;
                    continue;
                }
                if (!StepReroute(i, in v)) _rerouteList[w++] = i;
                else _inRerouteList[i] = false;
            }
            _rerouteCount = w;
        }

        /// <summary>True si el vehiculo sale de la lista (re-rutado o dado de baja).</summary>
        private bool StepReroute(int i, in Vehicle v)
        {
            LaneId cur = _veh.GetRouteStep(i, v.RouteCursor).Lane;
            EntityId curSeg = _net.SegmentIdAt(cur.SegmentIndex);
            EntityId dest = _destSeg[i];
            if (!curSeg.IsValid || !_net.IsSegmentAlive(dest))
            {
                Abandon(i);
                return true;
            }
            if (_rerouteId[i] == 0)
            {
                bool fwd = cur.Slot < _net.GetSegment(curSeg).LanesForward;
                var req = new PathRequest
                {
                    OriginSegment = curSeg,
                    OriginDirs = fwd ? TravelDirs.Forward : TravelDirs.Backward,
                    DestinationSegment = dest,
                    DestinationDirs = TravelDirs.Both,
                    Options = PathOptions.WithSeed((uint)(i * 2654435761u) ^ (uint)_veh.CurrentTick, v.Type),
                    Priority = PathPriority.Private,
                };
                if (_queue.TryEnqueue(in req, out int id)) _rerouteId[i] = id;
                return false;
            }
            PathStatus st = _queue.GetStatus(_rerouteId[i]);
            if (st == PathStatus.Pending) return false;
            if (st == PathStatus.NotFound || st == PathStatus.TooLong)
            {
                Abandon(i);
                return true;
            }
            if (st == PathStatus.Found && _queue.TryGetResult(_rerouteId[i], _scratch, out int len, out _)
                && len >= 1 && _scratch[0].Segment == curSeg && ResolveLanes(_scratch, 0, len, cur, out _, out float targetS)
                && _veh.TryReplaceRoute(_veh.IdOf(i), new ReadOnlySpan<LaneId>(_laneScratch, 0, len), targetS))
            {
                Rerouted++;
                _rerouteId[i] = 0;
                return true;
            }
            _rerouteId[i] = 0;
            if (++_rerouteTries[i] > _cfg.MaxRerouteAttempts)
            {
                Abandon(i);
                return true;
            }
            return false;
        }

        private void Abandon(int i)
        {
            _veh.Despawn(_veh.IdOf(i));
        }

        /// <summary>
        /// Elige un carril por paso (en _laneScratch) tal que cada par consecutivo tenga conexion: programacion dinamica hacia
        /// atras, prefiriendo la menor ranura. Con <paramref name="forcedFirst"/> valido, el primer carril es ese.
        /// </summary>
        private bool ResolveLanes(PathStep[] steps, int off, int len, LaneId forcedFirst, out float startS, out float targetS)
        {
            startS = 0f;
            targetS = 0f;
            for (int k = len - 1; k >= 0; k--)
            {
                PathStep st = steps[off + k];
                if (!_net.IsSegmentAlive(st.Segment)) return false;
                ref readonly Segment sg = ref _net.GetSegment(st.Segment);
                int lo = st.Forward ? 0 : sg.LanesForward;
                int hi = st.Forward ? sg.LanesForward : sg.LanesForward + sg.LanesBackward;
                int any = 0;
                for (int slot = 0; slot < 8; slot++)
                {
                    int c = -2;
                    if (slot >= lo && slot < hi)
                    {
                        var cand = new LaneId(st.Segment.Index, slot);
                        if (k == len - 1) c = -1;
                        else
                        {
                            PathStep nx = steps[off + k + 1];
                            ref readonly Segment ns = ref _net.GetSegment(nx.Segment);
                            int nlo = nx.Forward ? 0 : ns.LanesForward;
                            int nhi = nx.Forward ? ns.LanesForward : ns.LanesForward + ns.LanesBackward;
                            for (int ns2 = nlo; ns2 < nhi; ns2++)
                            {
                                if (_choice[(k + 1) * 8 + ns2] == -2) continue;
                                if (HasConnection(cand, new LaneId(nx.Segment.Index, ns2))) { c = ns2; break; }
                            }
                        }
                    }
                    _choice[k * 8 + slot] = c;
                    if (c != -2) any++;
                }
                if (any == 0) return false;
            }
            int first = -1;
            if (forcedFirst.IsValid)
            {
                if (forcedFirst.SegmentIndex != steps[off].Segment.Index || _choice[forcedFirst.Slot] == -2) return false;
                first = forcedFirst.Slot;
            }
            else
            {
                for (int slot = 0; slot < 8; slot++)
                    if (_choice[slot] != -2) { first = slot; break; }
            }
            if (first < 0) return false;
            int cur = first;
            for (int k = 0; k < len; k++)
            {
                _laneScratch[k] = new LaneId(steps[off + k].Segment.Index, cur);
                cur = _choice[k * 8 + cur];
            }
            startS = _net.GetLane(_laneScratch[0]).Length * _cfg.EndpointFraction;
            targetS = _net.GetLane(_laneScratch[len - 1]).Length * _cfg.EndpointFraction;
            return true;
        }

        private bool HasConnection(LaneId from, LaneId to)
        {
            ReadOnlySpan<LaneConnection> span = _net.GetConnectionsFrom(from);
            for (int i = 0; i < span.Length; i++)
                if (span[i].To == to) return true;
            return false;
        }
    }
}
