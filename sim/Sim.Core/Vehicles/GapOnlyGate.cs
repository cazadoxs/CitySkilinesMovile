using System;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Sim.Vehicles
{
    /// <summary>
    /// Puerta minima de M1-06b: concede si hay hueco a la salida (no entrar sin sitio al otro lado). No conoce conflictos,
    /// prioridades ni semaforos (M1-07). Resuelve por orden de peticion, que es indice ascendente de vehiculo.
    /// </summary>
    public sealed class GapOnlyGate : IJunctionGate, IVehicleViewConsumer, IForcedPassGate
    {
        private readonly RoadNetwork _net;
        private IVehicleView? _view;
        private EntryRequest[] _queue = new EntryRequest[64];
        private EntryGrant[] _grants = new EntryGrant[64];
        private int _queueCount;
        private int _grantCount;
        private float[] _scratch = new float[256];
        private int[] _touched = new int[64];
        private int _touchedCount;
        // Pasos forzados pendientes por vehiculo (varios a la vez); se limpian al concederse, cancelarse o darse de baja.
        private bool[] _forced = new bool[64];
        private bool[] _forcedNeedsExit = new bool[64];
        private int _forcedCount;

        public GapOnlyGate(RoadNetwork network)
        {
            _net = network ?? throw new ArgumentNullException(nameof(network));
        }

        public void Bind(IVehicleView view, int vehicleCapacity)
        {
            _view = view;
            int cap = Math.Max(64, vehicleCapacity);
            if (_queue.Length < cap) _queue = new EntryRequest[cap];
            if (_grants.Length < cap) _grants = new EntryGrant[cap];
            if (_touched.Length < cap) _touched = new int[cap];
            EnsureForced(cap);
        }

        private void EnsureForced(int cap)
        {
            if (_forced.Length >= cap) return;
            int n = Math.Max(cap, _forced.Length * 2);
            Array.Resize(ref _forced, n);
            Array.Resize(ref _forcedNeedsExit, n);
        }

        private void ClearForced(int vehicle)
        {
            if (vehicle < 0 || vehicle >= _forced.Length || !_forced[vehicle]) return;
            _forced[vehicle] = false;
            _forcedNeedsExit[vehicle] = false;
            _forcedCount--;
        }

        /// <summary>Gancho M1-11: en la siguiente resolucion la peticion de este vehiculo va primera y se concede sin mirar el hueco.</summary>
        public void ForceNextGrant(int vehicle) => ForceNextGrant(vehicle, true);

        /// <summary>Con <paramref name="exitInCycle"/> false el forzado sigue exigiendo hueco del tamano del vehiculo a la salida.</summary>
        public void ForceNextGrant(int vehicle, bool exitInCycle)
        {
            if (vehicle < 0) return;
            EnsureForced(vehicle + 1);
            if (!_forced[vehicle])
            {
                _forced[vehicle] = true;
                _forcedCount++;
            }
            _forcedNeedsExit[vehicle] = !exitInCycle;
        }

        public bool IsForced(int vehicle) => vehicle >= 0 && vehicle < _forced.Length && _forced[vehicle];

        public void CancelForce(int vehicle) => ClearForced(vehicle);

        public void OnNetworkChanged(ReadOnlySpan<EntityId> changedNodes) { }

        public void BeginTick(ulong tick)
        {
            _queueCount = 0;
            _grantCount = 0;
        }

        public void RequestEntry(in EntryRequest req)
        {
            if (_queueCount == _queue.Length) Array.Resize(ref _queue, _queueCount * 2);
            _queue[_queueCount++] = req;
        }

        public ReadOnlySpan<EntryGrant> ResolveRequests()
        {
            IVehicleView view = _view ?? throw new InvalidOperationException("GapOnlyGate sin vista: crea el VehicleSystem con esta puerta");
            _grantCount = 0;
            _touchedCount = 0;
            if (_grants.Length < _queueCount) Array.Resize(ref _grants, _queue.Length);
            if (_touched.Length < _queueCount) Array.Resize(ref _touched, _queue.Length);

            if (_forcedCount > 0)
            {
                // Los forzados van primero (en orden de peticion) y no miran el hueco, salvo el minimo si la salida no es del ciclo.
                for (int i = 0; i < _queueCount; i++)
                {
                    int fv = _queue[i].Vehicle;
                    if (fv < 0 || fv >= _forced.Length || !_forced[fv]) continue;
                    if (_forcedNeedsExit[fv])
                    {
                        float laneLen = _net.GetLane(_queue[i].To).Length;
                        float minGap = _queue[i].Length < laneLen ? _queue[i].Length : laneLen;
                        if (view.FreeSpace(_queue[i].To) - Scratch(_queue[i].To.Value) + 1e-4f < minGap) continue;
                    }
                    Grant(in _queue[i]);
                    _queue[i].Vehicle = -1;
                    ClearForced(fv);
                }
            }

            for (int i = 0; i < _queueCount; i++)
            {
                ref EntryRequest r = ref _queue[i];
                if (r.Vehicle < 0) continue;
                float laneLen = _net.GetLane(r.To).Length;
                float need = r.Space < laneLen ? r.Space : laneLen;
                float free = view.FreeSpace(r.To) - Scratch(r.To.Value);
                if (free + 1e-4f < need) continue;
                Grant(in r);
            }

            for (int i = 0; i < _touchedCount; i++) _scratch[_touched[i]] = 0f;
            return new ReadOnlySpan<EntryGrant>(_grants, 0, _grantCount);
        }

        private float Scratch(int lane) => lane < _scratch.Length ? _scratch[lane] : 0f;

        private void Grant(in EntryRequest r)
        {
            int lane = r.To.Value;
            if (lane >= _scratch.Length)
            {
                int n = _scratch.Length;
                while (n <= lane) n *= 2;
                Array.Resize(ref _scratch, n);
            }
            _scratch[lane] += r.Space;
            _touched[_touchedCount++] = lane;
            _grants[_grantCount++] = new EntryGrant { Vehicle = r.Vehicle, Connection = r.Connection, Space = r.Space };
        }

        public void OnEntered(int connection, int vehicle) { }

        public void OnExited(int connection, int vehicle) { }

        public void OnVehicleRemoved(int vehicle)
        {
            ClearForced(vehicle);
        }

        public void OnPermitCancelled(int vehicle) { }
    }
}
