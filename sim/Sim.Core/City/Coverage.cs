using System;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Sim.City
{
    /// <summary>
    /// Cobertura de servicios por tiempo de llegada (GDD 4.7): Dijkstra multifuente sobre la red a flujo libre desde todos los edificios
    /// de un tipo, guardado por segmento. Se recalcula un tipo cada <c>CoverageInterval</c> ticks y solo si cambio la red o los servicios.
    /// El despacho real de vehiculos y el efecto de los atascos son M4 (ADR 005).
    /// </summary>
    public sealed class CoverageSystem
    {
        public const int TypeCount = 9;
        public const ushort None = ushort.MaxValue;

        private readonly CitySimulation _city;
        private readonly RoadNetwork _net;
        private readonly CityBalance _bal;

        private ushort[][] _time = new ushort[TypeCount][];
        private int[] _mask = new int[64];
        private readonly bool[] _dirty = new bool[TypeCount];
        private ulong _epoch = ulong.MaxValue;
        private int _next;

        // Dijkstra
        private float[] _dist = new float[64];
        private float[] _heapKey = new float[256];
        private int[] _heapNode = new int[256];
        private int _heapCount;

        public CoverageSystem(CitySimulation city)
        {
            _city = city;
            _net = city.Network;
            _bal = city.Balance;
            for (int i = 0; i < TypeCount; i++)
            {
                _time[i] = new ushort[64];
                _dirty[i] = true;
            }
        }

        public void MarkAllDirty()
        {
            for (int i = 0; i < TypeCount; i++) _dirty[i] = true;
        }

        /// <summary>Un paso de reparto en el tiempo: recalcula, como mucho, un tipo de cobertura.</summary>
        public void Step(ulong tick)
        {
            if (tick % (ulong)_bal.CoverageInterval != 0) return;
            if (_epoch != _net.GraphEpoch)
            {
                _epoch = _net.GraphEpoch;
                MarkAllDirty();
            }
            for (int k = 0; k < TypeCount; k++)
            {
                int t = (_next + k) % TypeCount;
                if (!_dirty[t]) continue;
                _next = (t + 1) % TypeCount;
                Recompute(t);
                return;
            }
        }

        /// <summary>Mascara de coberturas (bit 1 &lt;&lt; CoverageType) que alcanzan al segmento.</summary>
        public int MaskAt(EntityId segment)
        {
            if (!_net.IsSegmentAlive(segment) || segment.Index >= _mask.Length) return 0;
            return _mask[segment.Index];
        }

        /// <summary>Segundos a flujo libre desde el servicio mas cercano de ese tipo (65535 = ninguno).</summary>
        public ushort TimeAt(CoverageType type, EntityId segment)
        {
            ushort[] t = _time[(int)type];
            if (!_net.IsSegmentAlive(segment) || segment.Index >= t.Length) return None;
            return t[segment.Index];
        }

        public bool IsCovered(CoverageType type, EntityId segment) => (MaskAt(segment) & (1 << (int)type)) != 0;

        // ------------------------------------------------------------------ calculo

        private void Recompute(int type)
        {
            _dirty[type] = false;
            int nb = _net.NodeIndexBound, sb = _net.SegmentIndexBound;
            EnsureCapacity(nb, sb);
            for (int i = 0; i < nb; i++) _dist[i] = float.MaxValue;
            _heapCount = 0;
            BuildingSystem bs = _city.Buildings;
            int bound = bs.IndexBound;
            for (int i = 0; i < bound; i++)
            {
                if (!bs.IsAlive(i)) continue;
                ref readonly Building b = ref bs.Get(i);
                if (b.Kind != BuildingKind.Service || b.IsAbandoned) continue;
                if (_bal.Service((ServiceType)b.ServiceType).Coverage != type) continue;
                if (!_net.IsSegmentAlive(b.Segment)) continue;
                ref readonly Segment sg = ref _net.GetSegment(b.Segment);
                float speed = _net.Types.Get(sg.RoadTypeId).SpeedLimit;
                float half = 0.5f * sg.Length / speed;
                Relax(sg.NodeA.Index, half);
                Relax(sg.NodeB.Index, half);
            }
            while (_heapCount > 0)
            {
                Pop(out float d, out int node);
                if (d > _dist[node]) continue;
                EntityId nid = _net.NodeIdAt(node);
                if (!nid.IsValid) continue;
                ReadOnlySpan<EntityId> segs = _net.GetNodeSegments(nid);
                for (int k = 0; k < segs.Length; k++)
                {
                    ref readonly Segment sg = ref _net.GetSegment(segs[k]);
                    int other = sg.NodeA.Index == node ? sg.NodeB.Index : sg.NodeA.Index;
                    float cost = sg.Length / _net.Types.Get(sg.RoadTypeId).SpeedLimit;
                    Relax(other, d + cost);
                }
            }
            ushort[] times = _time[type];
            float limit = _bal.CoverageSeconds[type];
            int bit = 1 << type;
            for (int s = 0; s < sb; s++)
            {
                EntityId id = _net.SegmentIdAt(s);
                if (!id.IsValid)
                {
                    times[s] = None;
                    _mask[s] &= ~bit;
                    continue;
                }
                ref readonly Segment sg = ref _net.GetSegment(id);
                float da = _dist[sg.NodeA.Index], db = _dist[sg.NodeB.Index];
                float best = Math.Min(da, db);
                if (best == float.MaxValue) { times[s] = None; _mask[s] &= ~bit; continue; }
                float t = best + 0.25f * sg.Length / _net.Types.Get(sg.RoadTypeId).SpeedLimit;
                times[s] = (ushort)Math.Min(65534f, t);
                if (t <= limit) _mask[s] |= bit; else _mask[s] &= ~bit;
            }
        }

        private void EnsureCapacity(int nodes, int segs)
        {
            if (_dist.Length < nodes) _dist = new float[Math.Max(nodes, _dist.Length * 2)];
            if (_mask.Length < segs)
            {
                int cap = Math.Max(segs, _mask.Length * 2);
                Array.Resize(ref _mask, cap);
                for (int i = 0; i < TypeCount; i++)
                {
                    int old = _time[i].Length;
                    Array.Resize(ref _time[i], cap);
                    for (int k = old; k < cap; k++) _time[i][k] = None;
                }
            }
        }

        private void Relax(int node, float d)
        {
            if (d >= _dist[node]) return;
            _dist[node] = d;
            if (_heapCount == _heapKey.Length)
            {
                Array.Resize(ref _heapKey, _heapCount * 2);
                Array.Resize(ref _heapNode, _heapCount * 2);
            }
            int i = _heapCount++;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (_heapKey[p] <= d) break;
                _heapKey[i] = _heapKey[p];
                _heapNode[i] = _heapNode[p];
                i = p;
            }
            _heapKey[i] = d;
            _heapNode[i] = node;
        }

        private void Pop(out float key, out int node)
        {
            key = _heapKey[0];
            node = _heapNode[0];
            _heapCount--;
            if (_heapCount == 0) return;
            float k = _heapKey[_heapCount];
            int n = _heapNode[_heapCount];
            int i = 0;
            while (true)
            {
                int c = 2 * i + 1;
                if (c >= _heapCount) break;
                if (c + 1 < _heapCount && _heapKey[c + 1] < _heapKey[c]) c++;
                if (_heapKey[c] >= k) break;
                _heapKey[i] = _heapKey[c];
                _heapNode[i] = _heapNode[c];
                i = c;
            }
            _heapKey[i] = k;
            _heapNode[i] = n;
        }

        public ulong ComputeHash()
        {
            ulong h = 0x434F5645UL;
            int sb = Math.Min(_net.SegmentIndexBound, _mask.Length);
            for (int s = 0; s < sb; s++) h = HashUtil.Mix(h, (ulong)(uint)_mask[s] | ((ulong)(uint)s << 32));
            return h;
        }
    }
}
