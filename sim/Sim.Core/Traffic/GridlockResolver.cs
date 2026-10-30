using System;
using Urbe.Sim.Ids;
using Urbe.Sim.Junctions;
using Urbe.Sim.Roads;
using Urbe.Sim.Vehicles;

namespace Urbe.Sim.Traffic
{
    /// <summary>
    /// Deteccion y resolucion de bloqueo total (GDD 8.7, M1-11).
    /// <para>
    /// Deteccion: grafo de espera donde el vehiculo A espera a B si B es su lider, si B ocupa o reserva la conexion que A necesita
    /// (conflicto fisico) o si B es la cola del carril de salida lleno de A (ver <c>Blocker</c>). Solo participan los vehiculos parados
    /// al menos <see cref="GridlockConfig.DetectSeconds"/>. Cada vehiculo espera a lo sumo a uno (el que lleva mas parado), asi que
    /// un ciclo se encuentra siguiendo la cadena desde cada vehiculo no visitado en la ronda; las marcas llevan un sello creciente
    /// (sin limpiar arrays). Una ronda reparte su trabajo en varios ticks (<see cref="GridlockConfig.ScanPerTick"/>,
    /// <see cref="GridlockConfig.StepsPerTick"/>) y se puede retomar a mitad de un recorrido.
    /// </para>
    /// <para>
    /// Resolucion por orden, un intento a la vez por ciclo: 1) paso forzado del que lleva mas parado (la puerta nunca se salta el
    /// conflicto fisico real: si en <see cref="GridlockConfig.ForceTimeoutSeconds"/> no se concede, se cancela y se prueba el
    /// siguiente); 2) re-ruta de los que puede rehacer el generador de viajes; 3) si <see cref="GridlockConfig.DespawnStuckVehicles"/>,
    /// baja del que lleva mas parado cuando supera <see cref="GridlockConfig.DespawnSeconds"/>. Sin asignaciones por tick.
    /// </para>
    /// </summary>
    public sealed partial class GridlockResolver
    {
        private const byte StageNone = 0;
        private const byte StageForcePending = 1;
        private const byte StageForceDone = 2;
        private const byte StageForceFailed = 3;
        private const byte StageRerouted = 4;
        private const byte StageExhausted = 5;

        private readonly RoadNetwork _net;
        private readonly VehicleSystem _veh;
        private readonly IForcedPassGate? _force;
        private readonly JunctionController? _junction;
        private readonly IRerouteSource? _reroutes;
        private readonly GridlockConfig _cfg;
        private readonly GridlockStats _stats;

        private readonly int _detectTicks;
        private readonly int _repeatTicks;
        private readonly int _memoryTicks;
        private readonly int _forceTimeoutTicks;
        private readonly int _graceTicks;
        private readonly int _despawnTicks;
        private readonly int _cooldownTicks;

        // Por vehiculo.
        private readonly int[] _visit;
        private readonly int[] _pos;
        private readonly byte[] _stage;
        private readonly ulong[] _stageTick;
        private readonly int[] _stageGen;
        private readonly int[] _path;
        private readonly int[] _cand;
        /// <summary>Ultimo tick en que el vehiculo estuvo en un ciclo (0 = nunca) y su generacion.</summary>
        private readonly ulong[] _epTick;
        private readonly int[] _epGen;
        private ulong _now;

        // Estado de la ronda.
        private bool _roundActive;
        private ulong _nextRound;
        private int _cursor;
        private int _scanHigh;
        private int _scanBase = 1;
        private int _walkId;
        private bool _walking;
        private int _pathLen;
        private int _cur;
        private ulong _lastDespawn;
        private bool _hasDespawned;

        public GridlockConfig Config => _cfg;
        public GridlockStats Stats => _stats;

        public GridlockResolver(RoadNetwork network, VehicleSystem vehicles, IJunctionGate? gate, IRerouteSource? reroutes,
            GridlockConfig? config = null, GridlockStats? stats = null)
        {
            _net = network ?? throw new ArgumentNullException(nameof(network));
            _veh = vehicles ?? throw new ArgumentNullException(nameof(vehicles));
            _force = gate as IForcedPassGate;
            _junction = gate as JunctionController;
            _reroutes = reroutes;
            _cfg = config ?? new GridlockConfig();
            _stats = stats ?? new GridlockStats();
            float dt = vehicles.Config.Dt;
            _detectTicks = Ticks(_cfg.DetectSeconds, dt);
            _repeatTicks = Math.Min(_detectTicks, Ticks(_cfg.RepeatDetectSeconds, dt));
            _memoryTicks = Ticks(_cfg.EpisodeMemorySeconds, dt);
            _forceTimeoutTicks = Ticks(_cfg.ForceTimeoutSeconds, dt);
            _graceTicks = Ticks(_cfg.ActionGraceSeconds, dt);
            _despawnTicks = Ticks(_cfg.DespawnSeconds, dt);
            _cooldownTicks = Ticks(_cfg.DespawnCooldownSeconds, dt);
            int cap = vehicles.Capacity;
            _visit = new int[cap];
            _pos = new int[cap];
            _stage = new byte[cap];
            _stageTick = new ulong[cap];
            _stageGen = new int[cap];
            _path = new int[cap];
            _cand = new int[cap];
            _epTick = new ulong[cap];
            _epGen = new int[cap];
        }

        private static int Ticks(float seconds, float dt)
        {
            int t = (int)Math.Round(seconds / dt);
            return t < 1 ? 1 : t;
        }

        // ------------------------------------------------------------------ tick

        /// <summary>Un paso por tick, tras <c>VehicleSystem.Update</c>. Coste acotado por <c>ScanPerTick</c> y <c>StepsPerTick</c>.</summary>
        public void Step()
        {
            if (!_cfg.Enabled) return;
            ulong now = _now = _veh.CurrentTick;
            if (!_roundActive)
            {
                if (now < _nextRound) return;
                StartRound(now);
            }
            RunRound(now);
        }

        private void StartRound(ulong now)
        {
            if (_walkId > int.MaxValue - _veh.Capacity - 8)
            {
                // Sin desbordar: se reinician las marcas (una vez cada ~500.000 rondas).
                Array.Clear(_visit, 0, _visit.Length);
                _walkId = 0;
            }
            _scanBase = _walkId + 1;
            _cursor = 0;
            _scanHigh = _veh.HighWater;
            _walking = false;
            _roundActive = true;
            _nextRound = now + (ulong)Math.Max(1, _cfg.ScanIntervalTicks);
        }

        private void RunRound(ulong now)
        {
            int scan = _cfg.ScanPerTick < 1 ? 1 : _cfg.ScanPerTick;
            int steps = _cfg.StepsPerTick < 1 ? 1 : _cfg.StepsPerTick;
            while (true)
            {
                if (_walking)
                {
                    WalkSteps(now, ref steps);
                    if (_walking) return; // presupuesto agotado: se retoma el siguiente tick
                }
                if (_cursor >= _scanHigh)
                {
                    _roundActive = false;
                    return;
                }
                if (scan <= 0) return;
                scan--;
                int i = _cursor;
                Filter(i, now);
                if (Qualifies(i) && _visit[i] < _scanBase)
                {
                    if (steps <= 0) return; // se vuelve a examinar este indice el siguiente tick
                    _walkId++;
                    _pathLen = 0;
                    _cur = i;
                    _walking = true;
                }
                _cursor++;
            }
        }

        /// <summary>
        /// Mantiene la etapa de resolucion del vehiculo: confirma o cancela un paso forzado pendiente y olvida a quien ya se mueve o
        /// ya no existe.
        /// </summary>
        private void Filter(int i, ulong now)
        {
            byte st = _stage[i];
            if (st == StageNone) return;
            ref readonly Vehicle v = ref _veh.GetVehicle(i);
            if (!v.IsAlive || v.Generation != _stageGen[i])
            {
                _stage[i] = StageNone;
                return;
            }
            if (st == StageForcePending)
            {
                if (_force == null || !_force.IsForced(i))
                {
                    _stage[i] = StageForceDone;
                    _stageTick[i] = now;
                    _stats.ForcedPasses++;
                }
                else if (now - _stageTick[i] >= (ulong)_forceTimeoutTicks)
                {
                    _force.CancelForce(i);
                    _stage[i] = StageForceFailed;
                    _stats.ForcedFailed++;
                }
                return;
            }
            if (_veh.GetWaitTicks(i) < _repeatTicks) _stage[i] = StageNone;
        }

        private bool Qualifies(int i)
        {
            ref readonly Vehicle v = ref _veh.GetVehicle(i);
            if (!v.IsAlive || (v.Flags & VehicleFlags.Arrived) != 0) return false;
            int wait = v.WaitTicks;
            if (wait >= _detectTicks) return true;
            if (wait < _repeatTicks) return false;
            // Ya estuvo en un ciclo reciente: se vuelve a trabar, no hace falta esperar otros X segundos.
            return _epTick[i] != 0 && _epGen[i] == v.Generation && _now - _epTick[i] <= (ulong)_memoryTicks;
        }

        /// <summary>Sigue la cadena de esperas desde <see cref="_cur"/>; se detiene al cerrar un ciclo, al salir del grafo o sin presupuesto.</summary>
        private void WalkSteps(ulong now, ref int steps)
        {
            while (steps > 0)
            {
                steps--;
                int v = _cur;
                if (!Qualifies(v))
                {
                    _walking = false;
                    return;
                }
                _visit[v] = _walkId;
                _pos[v] = _pathLen;
                _path[_pathLen++] = v;
                int nx = Blocker(v);
                if (nx < 0 || nx == v || !Qualifies(nx))
                {
                    _walking = false;
                    return;
                }
                if (_visit[nx] == _walkId)
                {
                    OnCycle(_pos[nx], _pathLen, now);
                    _walking = false;
                    return;
                }
                if (_visit[nx] >= _scanBase)
                {
                    _walking = false; // ya examinado en esta ronda
                    return;
                }
                _cur = nx;
            }
        }

        // ------------------------------------------------------------------ resolucion

        private void OnCycle(int start, int end, ulong now)
        {
            if (end - start < 2) return;
            bool fresh = true, busy = false, exhausted = false;
            int candidates = 0;
            int bestReroute = -1, bestRerouteWait = -1;
            int maxVehicle = -1, maxWait = -1;
            for (int k = start; k < end; k++)
            {
                int m = _path[k];
                if (!Qualifies(m)) return; // recorrido a medias obsoleto
                ref readonly Vehicle v = ref _veh.GetVehicle(m);
                if (_epTick[m] != 0 && _epGen[m] == v.Generation && now - _epTick[m] <= (ulong)_memoryTicks) fresh = false;
                byte st = _stage[m];
                if (st != StageNone && _stageGen[m] != v.Generation) _stage[m] = st = StageNone;
                if (st == StageExhausted) exhausted = true;
                if (st == StageForcePending) busy = true;
                else if ((st == StageForceDone || st == StageRerouted) && now - _stageTick[m] < (ulong)_graceTicks) busy = true;
                int wait = v.WaitTicks;
                if (wait > maxWait)
                {
                    maxWait = wait;
                    maxVehicle = m;
                }
                if (!IsLaneHead(in v)) continue;
                if (st == StageNone && CanForce(m)) _cand[candidates++] = m;
                if (st != StageRerouted && st != StageExhausted && wait > bestRerouteWait && CanReroute(m)) { bestReroute = m; bestRerouteWait = wait; }
            }
            // Memoria del episodio: los miembros vuelven a contar enseguida si se traban otra vez.
            for (int k = start; k < end; k++)
            {
                int m = _path[k];
                _epTick[m] = now;
                _epGen[m] = _veh.GetVehicle(m).Generation;
            }
            if (fresh) _stats.Cycles++;
            if (busy) return;

            // Paso 1: pasos forzados, de mas a menos espera.
            if (candidates > 0)
            {
                int limit = _cfg.MaxForcedPerCycle < 1 ? 1 : _cfg.MaxForcedPerCycle;
                for (int n = 0; n < limit && candidates > 0; n++)
                {
                    int bi = 0;
                    for (int c = 1; c < candidates; c++)
                        if (_veh.GetVehicle(_cand[c]).WaitTicks > _veh.GetVehicle(_cand[bi]).WaitTicks) bi = c;
                    int pick = _cand[bi];
                    _cand[bi] = _cand[--candidates];
                    _force!.ForceNextGrant(pick);
                    Mark(pick, StageForcePending, now);
                    _stats.ForcedAttempts++;
                }
                return;
            }
            // Paso 2: re-ruta (de uno en uno).
            if (bestReroute >= 0)
            {
                if (_veh.MarkForReroute(_veh.IdOf(bestReroute))) _stats.Reroutes++;
                Mark(bestReroute, StageRerouted, now);
                return;
            }
            // Paso 3: desaparicion segun la opcion de partida.
            if (_cfg.DespawnStuckVehicles)
            {
                if (maxWait < _despawnTicks) return; // aun no toca
                if (_hasDespawned && now - _lastDespawn < (ulong)_cooldownTicks) return;
                if (!StillCycle(start, end)) return;
                _veh.Despawn(_veh.IdOf(maxVehicle));
                _stage[maxVehicle] = StageNone;
                _stats.Despawns++;
                _lastDespawn = now;
                _hasDespawned = true;
                return;
            }
            // Sin desaparicion: queda informado, una vez por episodio.
            if (!exhausted && maxVehicle >= 0)
            {
                Mark(maxVehicle, StageExhausted, now);
                _stats.Unresolved++;
            }
        }

        private void Mark(int vehicle, byte stage, ulong now)
        {
            _stage[vehicle] = stage;
            _stageTick[vehicle] = now;
            _stageGen[vehicle] = _veh.GetVehicle(vehicle).Generation;
        }

        /// <summary>Antes de una baja (destructiva): comprueba que cada miembro sigue esperando al siguiente.</summary>
        private bool StillCycle(int start, int end)
        {
            for (int k = start; k < end; k++)
            {
                int next = k + 1 < end ? _path[k + 1] : _path[start];
                if (!Qualifies(_path[k]) || Blocker(_path[k]) != next) return false;
            }
            return true;
        }

        private static bool IsLaneHead(in Vehicle v) => v.Kind == TrackKind.Lane && v.Ahead < 0 && v.PrevConnection < 0;

        /// <summary>Candidato a paso forzado: primero de su carril, sin permiso, con ruta por delante y que no sea un rechazo por conflicto.</summary>
        private bool CanForce(int i)
        {
            if (_force == null) return false;
            ref readonly Vehicle v = ref _veh.GetVehicle(i);
            if ((v.Flags & (VehicleFlags.HasEntryPermit | VehicleFlags.NeedsReroute | VehicleFlags.Frozen | VehicleFlags.Arrived)) != 0) return false;
            if (v.RouteCursor >= v.RouteLast) return false;
            // Un rechazo por conflicto fisico no lo arregla un paso forzado (la puerta nunca se lo salta): mejor probar a otro.
            if (_junction != null && _junction.GetLastDecision(i) == GrantDecision.DeniedConflict) return false;
            return true;
        }

        private bool CanReroute(int i)
        {
            if (_reroutes == null || !_reroutes.CanReroute(i)) return false;
            ref readonly Vehicle v = ref _veh.GetVehicle(i);
            if ((v.Flags & (VehicleFlags.NeedsReroute | VehicleFlags.Frozen | VehicleFlags.Arrived)) != 0) return false;
            return v.RouteCursor < v.RouteLast;
        }
    }
}
