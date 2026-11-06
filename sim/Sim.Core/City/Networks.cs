using System;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Sim.City
{
    /// <summary>
    /// Agua, alcantarillado y electricidad bajo las calles (GDD 8.10, ADR 005). Cada grupo de segmentos unidos por nodos es una
    /// componente conexa (union-find reconstruido cuando cambia el grafo de carreteras). En cada componente se compara produccion y
    /// consumo; el deficit se reparte por prioridad: servicios, residencial, comercial, industrial y oficinas (el reparto dentro de una
    /// clase es uniforme: todos reciben la misma fraccion). Una componente sin conexion con la autopista de entrada tambien se marca.
    /// </summary>
    public sealed class SupplyNetworks
    {
        public const int Classes = 4;

        private readonly CitySimulation _city;
        private readonly RoadNetwork _net;
        private readonly CityBalance _bal;

        private int[] _parent = new int[64];
        private int[] _root = new int[64];
        private ulong _epoch = ulong.MaxValue;
        private int _outsideRoot = -1;

        // Por componente (indice = nodo raiz).
        private float[] _waterProd = new float[64];
        private float[] _powerProd = new float[64];
        private float[] _sewerCap = new float[64];
        private float[] _sewerDem = new float[64];
        private float[] _waterDem = new float[64 * Classes];
        private float[] _powerDem = new float[64 * Classes];
        private float[] _waterRatio = new float[64 * Classes];
        private float[] _powerRatio = new float[64 * Classes];
        private float[] _sewerRatio = new float[64];

        public bool Dirty { get; set; } = true;

        // Totales de la ciudad para el HUD.
        public float WaterProduction { get; private set; }
        public float WaterDemand { get; private set; }
        public float PowerProduction { get; private set; }
        public float PowerDemand { get; private set; }
        public float SewerCapacity { get; private set; }
        public float SewerDemand { get; private set; }
        /// <summary>Numero de componentes conexas de la red viaria.</summary>
        public int ComponentCount { get; private set; }

        public SupplyNetworks(CitySimulation city)
        {
            _city = city;
            _net = city.Network;
            _bal = city.Balance;
        }

        public static int ClassOf(ZoneType zone, bool service)
        {
            if (service) return 0;
            switch (zone)
            {
                case ZoneType.Commercial: return 2;
                case ZoneType.Industrial:
                case ZoneType.Office: return 3;
                default: return 1;
            }
        }

        /// <summary>Recalcula si hace falta (cambio de red o de servicios) o cada NetworkInterval ticks.</summary>
        public void Update(ulong tick)
        {
            if (Dirty || tick % (ulong)_bal.NetworkInterval == 0) Recompute();
        }

        // ------------------------------------------------------------------ estructura (union-find)

        private int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]];
                x = _parent[x];
            }
            return x;
        }

        private void EnsureStructure()
        {
            if (_epoch == _net.GraphEpoch) return;
            _epoch = _net.GraphEpoch;
            int n = _net.NodeIndexBound;
            if (_parent.Length < n)
            {
                int cap = _parent.Length;
                while (cap < n) cap *= 2;
                _parent = new int[cap];
                _root = new int[cap];
                _waterProd = new float[cap];
                _powerProd = new float[cap];
                _sewerCap = new float[cap];
                _sewerDem = new float[cap];
                _sewerRatio = new float[cap];
                _waterDem = new float[cap * Classes];
                _powerDem = new float[cap * Classes];
                _waterRatio = new float[cap * Classes];
                _powerRatio = new float[cap * Classes];
            }
            for (int i = 0; i < n; i++) _parent[i] = i;
            int sb = _net.SegmentIndexBound;
            for (int s = 0; s < sb; s++)
            {
                EntityId id = _net.SegmentIdAt(s);
                if (!id.IsValid) continue;
                ref readonly Segment sg = ref _net.GetSegment(id);
                int a = Find(sg.NodeA.Index), b = Find(sg.NodeB.Index);
                if (a == b) continue;
                // La raiz es siempre el indice menor: independiente del orden de union.
                if (a < b) _parent[b] = a; else _parent[a] = b;
            }
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                _root[i] = Find(i);
                if (_root[i] == i && _net.NodeIdAt(i).IsValid) count++;
            }
            ComponentCount = count;
            _outsideRoot = -1;
            EntityId outSeg = _city.OutsideSegment;
            if (!_net.IsSegmentAlive(outSeg)) outSeg = _city.OutsideSegmentFor(1);
            if (_net.IsSegmentAlive(outSeg)) _outsideRoot = _root[_net.GetSegment(outSeg).NodeA.Index];
            Dirty = true;
        }

        /// <summary>Componente (nodo raiz) de la calle, o -1 si no existe.</summary>
        public int ComponentOf(EntityId segment)
        {
            if (!_net.IsSegmentAlive(segment)) return -1;
            EnsureStructure();
            return _root[_net.GetSegment(segment).NodeA.Index];
        }

        public bool IsConnectedToOutside(EntityId segment)
        {
            int c = ComponentOf(segment);
            return c >= 0 && c == _outsideRoot;
        }

        // ------------------------------------------------------------------ balance

        private void Recompute()
        {
            Dirty = false;
            EnsureStructure();
            int n = _net.NodeIndexBound;
            Array.Clear(_waterProd, 0, n);
            Array.Clear(_powerProd, 0, n);
            Array.Clear(_sewerCap, 0, n);
            Array.Clear(_sewerDem, 0, n);
            Array.Clear(_waterDem, 0, n * Classes);
            Array.Clear(_powerDem, 0, n * Classes);
            BuildingSystem bs = _city.Buildings;
            int bound = bs.IndexBound;
            for (int i = 0; i < bound; i++)
            {
                if (!bs.IsAlive(i)) continue;
                ref readonly Building b = ref bs.Get(i);
                if (b.IsAbandoned || !_net.IsSegmentAlive(b.Segment)) continue;
                int comp = _root[_net.GetSegment(b.Segment).NodeA.Index];
                if (b.Kind == BuildingKind.Service)
                {
                    ServiceInfo info = _bal.Service((ServiceType)b.ServiceType);
                    _waterProd[comp] += info.WaterProduction;
                    _powerProd[comp] += info.PowerProduction;
                    _sewerCap[comp] += info.SewerCapacity;
                    if (!info.IsProducer)
                    {
                        _waterDem[comp * Classes] += info.Water;
                        _powerDem[comp * Classes] += info.Power;
                        _sewerDem[comp] += info.Water;
                    }
                    continue;
                }
                float water, power;
                if (b.Zone == ZoneType.Residential)
                {
                    water = b.Capacity * _bal.WaterPerResident;
                    power = b.Capacity * _bal.PowerPerResident;
                }
                else
                {
                    water = b.Capacity * _bal.WaterPerJob;
                    power = b.Capacity * _bal.PowerPerJob * (b.Zone == ZoneType.Industrial ? _bal.IndustrialPowerMult : 1f);
                }
                int cls = ClassOf(b.Zone, false);
                _waterDem[comp * Classes + cls] += water;
                _powerDem[comp * Classes + cls] += power;
                _sewerDem[comp] += water;
            }
            float wp = 0, wd = 0, pp = 0, pd = 0, sc = 0, sd = 0;
            for (int c = 0; c < n; c++)
            {
                if (_root[c] != c) continue;
                float availW = _waterProd[c], availP = _powerProd[c];
                for (int k = 0; k < Classes; k++)
                {
                    float dw = _waterDem[c * Classes + k], dp = _powerDem[c * Classes + k];
                    _waterRatio[c * Classes + k] = dw <= 0f ? (availW > 0f ? 1f : 0f) : Clamp01(availW / dw);
                    _powerRatio[c * Classes + k] = dp <= 0f ? (availP > 0f ? 1f : 0f) : Clamp01(availP / dp);
                    availW = Math.Max(0f, availW - dw);
                    availP = Math.Max(0f, availP - dp);
                    wd += dw;
                    pd += dp;
                }
                _sewerRatio[c] = _sewerDem[c] <= 0f ? (_sewerCap[c] > 0f ? 1f : 0f) : Clamp01(_sewerCap[c] / _sewerDem[c]);
                wp += _waterProd[c];
                pp += _powerProd[c];
                sc += _sewerCap[c];
                sd += _sewerDem[c];
            }
            WaterProduction = wp; WaterDemand = wd; PowerProduction = pp; PowerDemand = pd; SewerCapacity = sc; SewerDemand = sd;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        /// <summary>Fraccion de agua y luz que recibe un edificio de esa clase en la calle, si hay alcantarillado y si esta conectado al exterior.</summary>
        public void Query(EntityId segment, int cls, out float water, out float power, out bool sewage, out bool connected)
        {
            water = power = 0f;
            sewage = connected = false;
            int c = ComponentOf(segment);
            if (c < 0) return;
            water = _waterRatio[c * Classes + cls];
            power = _powerRatio[c * Classes + cls];
            sewage = _sewerRatio[c] >= 0.5f;
            connected = c == _outsideRoot;
        }

        public ulong ComputeHash()
        {
            ulong h = HashUtil.Mix(0x53555050UL, (ulong)ComponentCount);
            h = HashUtil.MixF(h, WaterProduction); h = HashUtil.MixF(h, WaterDemand);
            h = HashUtil.MixF(h, PowerProduction); h = HashUtil.MixF(h, PowerDemand);
            h = HashUtil.MixF(h, SewerCapacity); h = HashUtil.MixF(h, SewerDemand);
            return h;
        }
    }
}
