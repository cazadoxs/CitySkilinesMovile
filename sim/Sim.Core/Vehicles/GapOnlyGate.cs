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
        private int _forced = -1;

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
        }

        /// <summary>Gancho M1-11: en la siguiente resolucion la peticion de este vehiculo va primera y se concede sin mirar el hueco.</summary>
        public void ForceNextGrant(int vehicle) => _forced = vehicle;

        public bool IsForced(int vehicle) => vehicle >= 0 && _forced == vehicle;

        public void CancelForce(int vehicle)
        {
            if (_forced == vehicle) _forced = -1;
        }

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

            if (_forced >= 0)
            {
                for (int i = 0; i < _queueCount; i++)
                {
                    if (_queue[i].Vehicle != _forced) continue;
                    Grant(in _queue[i]);
                    _queue[i].Vehicle = -1;
                    _forced = -1;
                    break;
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
            if (_forced == vehicle) _forced = -1;
        }

        public void OnPermitCancelled(int vehicle) { }
    }
}
