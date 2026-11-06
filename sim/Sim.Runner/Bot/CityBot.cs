using System;
using System.Collections.Generic;
using Urbe.Sim.City;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Sim.Runner.Bot
{
    /// <summary>
    /// Jugador automatico de la ciudad (M3-06): construye calles sobre una cuadricula de 80 m que crece desde el final de la autopista,
    /// zonifica por distritos (comercio en el eje, industria al norte, vivienda en el resto) segun la demanda, levanta agua, luz y
    /// alcantarillado antes de que falten, y coloca los servicios cuando se desbloquean. Usa solo la API publica de ordenes y es
    /// determinista (no usa azar). Sirve de prueba de equilibrio: si el bot llega a 10.000 habitantes con saldo sostenible, el ciclo
    /// completo de la ciudad funciona.
    /// </summary>
    public sealed class CityBot
    {
        public const float Spacing = 80f;
        private const int ReserveMoney = 2500;

        private struct Seg
        {
            public bool H;
            public int I, J;
            public Seg(bool h, int i, int j) { H = h; I = i; J = j; }
        }

        private readonly CitySimulation _c;
        private readonly Vec2 _origin;
        private readonly int _iMax, _jMax;
        private readonly bool[,] _built;   // [h?0:1, index]
        private readonly ZoneType[,] _painted;
        private readonly ZoneType[,] _over;
        private readonly bool[] _nodeExists;
        private readonly List<Seg> _pendingPaint = new List<Seg>();

        public int RoadsBuilt { get; private set; }
        public int ServicesBuilt { get; private set; }
        public int FailedAttempts { get; private set; }
        public string Status { get; private set; } = "";

        private int NodeIndex(int i, int j) => i * (2 * _jMax + 1) + (j + _jMax);
        private Vec2 NodePos(int i, int j) => new Vec2(_origin.X + i * Spacing, j * Spacing);
        private int SegIndex(Seg s) => s.I * (2 * _jMax + 1) + (s.J + _jMax);

        public CityBot(CitySimulation city)
        {
            _c = city;
            Vec2 end = city.Network.GetNode(city.Network.GetSegment(city.OutsideSegment).NodeB).Position;
            _origin = end;
            _iMax = (int)((city.Balance.MapHalfSize - 24f - _origin.X) / Spacing);
            _jMax = (int)((city.Balance.MapHalfSize - 24f) / Spacing);
            int n = (_iMax + 1) * (2 * _jMax + 1);
            _built = new bool[2, n];
            _painted = new ZoneType[2, n];
            _over = new ZoneType[2, n];
            _nodeExists = new bool[n];
            _nodeExists[NodeIndex(0, 0)] = true;
        }

        // ------------------------------------------------------------------ cuadricula

        private bool InGrid(int i, int j) => i >= 0 && i <= _iMax && j >= -_jMax && j <= _jMax;

        private bool Valid(Seg s) => s.H ? InGrid(s.I, s.J) && InGrid(s.I + 1, s.J) : InGrid(s.I, s.J) && InGrid(s.I, s.J + 1);

        private bool IsBuilt(Seg s) => _built[s.H ? 0 : 1, SegIndex(s)];

        private void Ends(Seg s, out int ai, out int aj, out int bi, out int bj)
        {
            ai = s.I; aj = s.J;
            bi = s.H ? s.I + 1 : s.I;
            bj = s.H ? s.J : s.J + 1;
        }

        private Vec2 Mid(Seg s)
        {
            Ends(s, out int ai, out int aj, out int bi, out int bj);
            return (NodePos(ai, aj) + NodePos(bi, bj)) * 0.5f;
        }

        /// <summary>Distrito de un segmento: comercio en el eje y dos transversales, industria al norte, vivienda en el resto.</summary>
        private ZoneType DistrictOf(Seg s)
        {
            ZoneType o = _over[s.H ? 0 : 1, SegIndex(s)];
            return o != ZoneType.None ? o : BaseDistrict(s);
        }

        private ZoneType BaseDistrict(Seg s)
        {
            float mi = s.H ? s.I + 0.5f : s.I;
            float mj = s.H ? s.J : s.J + 0.5f;
            if (mj >= 3f && mi >= 2f) return ZoneType.Industrial;
            if (s.H && s.J == 2 && s.I >= 2 && s.I <= 5) return ZoneType.Office;
            if (s.H && s.J == 0 && s.I >= 1 && s.I <= 7) return ZoneType.Commercial;
            if (!s.H && (s.I == 3 || s.I == 6) && Math.Abs(mj) <= 2.5f) return ZoneType.Commercial;
            return ZoneType.Residential;
        }

        private IEnumerable<Seg> AllSegments()
        {
            for (int i = 0; i <= _iMax; i++)
                for (int j = -_jMax; j <= _jMax; j++)
                {
                    var h = new Seg(true, i, j);
                    if (Valid(h)) yield return h;
                    var v = new Seg(false, i, j);
                    if (Valid(v)) yield return v;
                }
        }

        // ------------------------------------------------------------------ bucle principal

        /// <summary>Un paso de decision. Llamar cada pocos segundos simulados.</summary>
        public void Think()
        {

            Status = "";
            _thinks++;
            AdjustTaxes();
            PaintPending();
            PlaceUtilities();
            for (int k = 0; k < 4; k++) if (!Expand()) break;
            PlaceServices();
        }

        private bool CanSpend(int cost) => _c.Money >= cost + ReserveMoney;

        // ------------------------------------------------------------------ impuestos

        private void AdjustTaxes()
        {
            _c.Economy.Estimate(out long income, out long expenses);
            long net = income - expenses;
            float rate = _c.TaxRate(ZoneType.Residential);
            if (net < 0 && _c.Money < 6000 && rate < 12f) Apply(rate + 1f);
            else if (_c.Money > 30000 && net > 0 && rate > 9f) Apply(rate - 1f);
        }

        private void Apply(float rate)
        {
            _c.SetTaxRate(ZoneType.Residential, rate);
            _c.SetTaxRate(ZoneType.Commercial, rate);
            _c.SetTaxRate(ZoneType.Industrial, rate);
            _c.SetTaxRate(ZoneType.Office, rate);
        }

        // ------------------------------------------------------------------ servicios

        private int Count(ServiceType t)
        {
            int n = 0;
            BuildingSystem bs = _c.Buildings;
            for (int i = 0; i < bs.IndexBound; i++)
                if (bs.IsAlive(i) && bs.Get(i).Kind == BuildingKind.Service && bs.Get(i).ServiceType == (byte)t) n++;
            return n;
        }

        private void PlaceUtilities()
        {
            SupplyNetworks sup = _c.Supply;
            int pop = _c.Stats.Population;
            // Si aun no hay nada, se arranca con lo basico antes de gastar en calles.
            float growth = 1.35f;
            float needPower = sup.PowerDemand * growth + 80f;
            float needWater = sup.WaterDemand * growth + 150f;
            float needSewer = sup.WaterDemand * growth + 150f;
            if (sup.PowerProduction < needPower)
            {
                bool coal = pop >= 900 && CanSpend(_c.Balance.Service(ServiceType.CoalPlant).Cost) && sup.PowerDemand > 900f;
                ServiceType t = coal ? ServiceType.CoalPlant : ServiceType.WindTurbine;
                if (CanSpend(_c.Balance.Service(t).Cost)) PlaceNear(t, coal ? FarEast() : _origin);
            }
            if (sup.WaterProduction < needWater && CanSpend(_c.Balance.Service(ServiceType.WaterPump).Cost)) PlaceNear(ServiceType.WaterPump, _origin);
            if (sup.SewerCapacity < needSewer && CanSpend(_c.Balance.Service(ServiceType.SewageOutlet).Cost)) PlaceNear(ServiceType.SewageOutlet, _origin);
        }

        private Vec2 FarEast() => new Vec2(_origin.X + Spacing * Math.Min(_iMax, 14), Spacing * 4);

        private Vec2 Center()
        {
            // Centro de masa de las casas (o del eje si aun no hay).
            double x = 0, z = 0;
            int n = 0;
            BuildingSystem bs = _c.Buildings;
            for (int i = 0; i < bs.IndexBound; i++)
            {
                if (!bs.IsAlive(i)) continue;
                ref readonly Building b = ref bs.Get(i);
                if (b.Kind != BuildingKind.Zoned || b.Zone != ZoneType.Residential) continue;
                x += b.Pos.X; z += b.Pos.Z; n++;
            }
            return n == 0 ? NodePos(3, 0) : new Vec2((float)(x / n), (float)(z / n));
        }

        private void PlaceServices()
        {
            int pop = _c.Stats.Population;
            Vec2 center = Center();
            // (tipo, cuantos hacen falta con esta poblacion)
            Want(ServiceType.Landfill, pop >= 350 ? 1 + pop / 5000 : 0, center);
            Want(ServiceType.HealthCenter, pop >= 350 ? 1 + pop / 2500 : 0, center);
            Want(ServiceType.Park, pop >= 350 ? 1 + pop / 700 : 0, center);
            Want(ServiceType.FireStation, pop >= 900 ? 1 + pop / 3000 : 0, center);
            Want(ServiceType.PoliceStation, pop >= 900 ? 1 + pop / 3000 : 0, center);
            Want(ServiceType.School, pop >= 900 ? 1 + pop / 1500 : 0, center);
            Want(ServiceType.HighSchool, pop >= 4800 ? 1 + pop / 6000 : 0, center);
        }

        private void Want(ServiceType t, int target, Vec2 anchor)
        {
            if (target <= 0 || !_c.IsServiceUnlocked(t)) return;
            if (!CanSpend(_c.Balance.Service(t).Cost)) return;
            if (Count(t) >= target) return;
            PlaceNear(t, anchor);
        }

        /// <summary>Prueba posiciones junto a las calles, de la mas cercana al ancla a la mas lejana, hasta que una se acepta.</summary>
        private readonly int[] _backoff = new int[16];
        private Vec2 _frontier;
        private int _thinks;

        private bool PlaceNear(ServiceType t, Vec2 anchor)
        {
            if (_backoff[(int)t] > _thinks) return false;
            if (PlaceNearOnce(t, anchor)) return true;
            if (_frontier != default && PlaceNearOnce(t, _frontier)) return true;
            _backoff[(int)t] = _thinks + 10;
            return false;
        }

        private bool PlaceNearOnce(ServiceType t, Vec2 anchor)
        {
            RoadNetwork net = _c.Network;
            var cands = new List<(float d, int order, Vec2 pos)>();
            int order = 0;
            for (int s = 0; s < net.SegmentIndexBound; s++)
            {
                EntityId id = net.SegmentIdAt(s);
                if (!id.IsValid) continue;
                ref readonly Segment sg = ref net.GetSegment(id);
                if (sg.RoadTypeId == RoadTypeIds.Highway4) continue;
                float half = net.Types.Get(sg.RoadTypeId).TotalWidth * 0.5f;
                for (int k = 0; k < 4; k++)
                {
                    float tt = 0.2f + 0.2f * k;
                    Vec2 p = sg.Curve.Evaluate(tt);
                    Vec2 n = OffsetCurve.Right(sg.Curve.Tangent(tt));
                    for (int side = -1; side <= 1; side += 2)
                    {
                        Vec2 pos = p + n * (side * (half + 12f));
                        cands.Add((Vec2.Distance(pos, anchor), order++, pos));
                    }
                }
            }
            cands.Sort((a, b) => a.d != b.d ? a.d.CompareTo(b.d) : a.order.CompareTo(b.order));
            int tries = Math.Min(cands.Count, 200);
            for (int i = 0; i < tries; i++)
            {
                var cmd = new PlaceServiceCommand(t, cands[i].pos);
                if (_c.Execute(cmd) == CityResult.Ok)
                {
                    ServicesBuilt++;
                    return true;
                }
                FailedAttempts++;
            }
            return false;
        }

        // ------------------------------------------------------------------ calles y zonas

        private int FreeCells(ZoneType z) => _c.Zones.CountFreeFront(z);

        private bool Expand()
        {
            ZoneType[] order = { ZoneType.Residential, ZoneType.Commercial, ZoneType.Industrial, ZoneType.Office };
            // La zona con mas demanda y menos suelo libre manda.
            ZoneType best = ZoneType.None;
            float bestScore = 4f;
            foreach (ZoneType z in order)
            {
                if (z == ZoneType.Office && !_c.IsZoneUnlocked(z, 2)) continue;
                float d = _c.Demand.Get(z);
                int free = FreeCells(z);
                int want = Wanted(z);
                if (free >= want) continue;
                float score = d + (want - free) * 0.5f;
                if (d > 4f && score > bestScore) { bestScore = score; best = z; }
            }
            if (best == ZoneType.None)
            {
                Status = "esperando demanda";
                return false;
            }
            if (!CanSpend(_c.Balance.RoadCost(RoadTypeIds.Street2, Spacing))) { Status = "sin dinero para calles"; return false; }
            // Primero se intenta zonificar una calle ya construida de ese distrito y sin zona; si no, se construye la siguiente.
            if (PaintExistingFor(best)) return true;
            return BuildNextFor(best);
        }

        private int Wanted(ZoneType z)
        {
            int pop = Math.Max(_c.Stats.Population, 100);
            switch (z)
            {
                case ZoneType.Residential: return 30 + pop / 200;
                case ZoneType.Commercial: return 12 + pop / 600;
                case ZoneType.Industrial: return 14 + pop / 500;
                default: return 10 + pop / 800;
            }
        }

        private int DensityFor(ZoneType z)
        {
            for (int d = 3; d >= 1; d--)
                if (_c.IsZoneUnlocked(z, ZoneGrid.NormalizeDensity(z, d))) return d;
            return 1;
        }

        private bool PaintExistingFor(ZoneType z)
        {
            foreach (Seg s in AllSegments())
            {
                if (!IsBuilt(s) || _painted[s.H ? 0 : 1, SegIndex(s)] != ZoneType.None) continue;
                ZoneType d = DistrictOf(s);
                if (d != z) continue;
                Paint(s, z);
                return true;
            }
            return false;
        }

        private void PaintPending()
        {
            for (int k = 0; k < _pendingPaint.Count; k++)
            {
                Seg s = _pendingPaint[k];
                ZoneType z = DistrictOf(s);
                Paint(s, z);
            }
            _pendingPaint.Clear();
        }

        private void Paint(Seg s, ZoneType z)
        {
            int idx = s.H ? 0 : 1;
            if (!_c.Network.NearestSegment(Mid(s), 6f, out EntityId seg, out _, out _)) return;
            int density = DensityFor(z);
            if (!_c.IsZoneUnlocked(z, density)) return;
            CityResult r = _c.Execute(PaintZoneCommand.FillSegment(seg, 4, z, density));
            if (r == CityResult.Ok) _painted[idx, SegIndex(s)] = z;
        }


        /// <summary>Construye la siguiente calle hacia el distrito de la zona pedida (un camino de calles aun sin construir).</summary>
        private bool BuildNextFor(ZoneType z, bool fallback = false)
        {
            // BFS 0-1 sobre la cuadricula desde todos los nodos existentes hacia el segmento sin construir del distrito mas barato.
            int n = _nodeExists.Length;
            var dist = new int[n];
            var prev = new int[n];
            for (int i = 0; i < n; i++) { dist[i] = int.MaxValue; prev[i] = -1; }
            var queue = new List<int>();
            for (int i = 0; i <= _iMax; i++)
                for (int j = -_jMax; j <= _jMax; j++)
                    if (_nodeExists[NodeIndex(i, j)]) { dist[NodeIndex(i, j)] = 0; queue.Add(NodeIndex(i, j)); }
            // Dijkstra simple con cola ordenada por (dist, indice): la cuadricula es pequena.
            var done = new bool[n];
            while (true)
            {
                int u = -1;
                int bd = int.MaxValue;
                for (int k = 0; k < queue.Count; k++)
                {
                    int q = queue[k];
                    if (!done[q] && dist[q] < bd) { bd = dist[q]; u = q; }
                }
                if (u < 0) break;
                done[u] = true;
                int ui = u / (2 * _jMax + 1), uj = u % (2 * _jMax + 1) - _jMax;
                for (int dir = 0; dir < 4; dir++)
                {
                    int vi = ui + (dir == 0 ? 1 : dir == 1 ? -1 : 0), vj = uj + (dir == 2 ? 1 : dir == 3 ? -1 : 0);
                    if (!InGrid(vi, vj)) continue;
                    var s = dir < 2 ? new Seg(true, Math.Min(ui, vi), uj) : new Seg(false, ui, Math.Min(uj, vj));
                    int w = IsBuilt(s) ? 0 : 1;
                    int v = NodeIndex(vi, vj);
                    if (dist[u] + w < dist[v])
                    {
                        dist[v] = dist[u] + w;
                        prev[v] = u;
                        if (!queue.Contains(v)) queue.Add(v);
                    }
                }
            }
            // Mejor segmento objetivo: sin construir, del distrito pedido, con un extremo alcanzable al menor coste; desempata por cercania al origen.
            // Mejor segmento objetivo: sin construir, del distrito pedido, con un extremo alcanzable al menor coste; desempata por cercania al origen.
            Seg bestSeg = default;
            int bestCost = int.MaxValue;
            int bestNode = -1;
            float bestDist = float.MaxValue;
            foreach (Seg s in AllSegments())
            {
                if (IsBuilt(s) || DistrictOf(s) != z) continue;
                Ends(s, out int ai, out int aj, out int bi, out int bj);
                int na = NodeIndex(ai, aj), nb = NodeIndex(bi, bj);
                int node = dist[na] <= dist[nb] ? na : nb;
                int cost = dist[node];
                if (cost == int.MaxValue) continue;
                float d = Vec2.Distance(Mid(s), NodePos(0, 0)) + Math.Abs(Mid(s).Z) * 0.3f;
                if (cost < bestCost || (cost == bestCost && d < bestDist)) { bestCost = cost; bestDist = d; bestSeg = s; bestNode = node; }
            }
            if (bestNode < 0 && !fallback)
            {
                // Se acabo el distrito: se reconvierte el siguiente segmento residencial libre (el mas cercano al origen).
                foreach (Seg s in AllSegments())
                {
                    if (IsBuilt(s) || BaseDistrict(s) != ZoneType.Residential || _over[s.H ? 0 : 1, SegIndex(s)] != ZoneType.None) continue;
                    Ends(s, out int ai, out int aj, out int bi, out int bj);
                    if (dist[NodeIndex(ai, aj)] == int.MaxValue && dist[NodeIndex(bi, bj)] == int.MaxValue) continue;
                    float d = Vec2.Distance(Mid(s), NodePos(0, 0));
                    if (d < bestDist || bestDist == float.MaxValue) { bestDist = d; bestSeg = s; }
                }
                if (bestDist == float.MaxValue) { Status = "sin sitio para " + z; return false; }
                _over[bestSeg.H ? 0 : 1, SegIndex(bestSeg)] = z;
                return BuildNextFor(z, true);
            }
            if (bestNode < 0) { Status = "sin sitio para " + z; return false; }
            if (bestCost == 0)
            {
                return BuildSeg(bestSeg);
            }
            // El objetivo esta lejos de la red: se construye el primer tramo del camino (el que sale de un nodo existente).
            int cur = bestNode;
            while (dist[cur] > 1 && prev[cur] >= 0) cur = prev[cur];
            if (prev[cur] < 0) return false;
            int pu = prev[cur];
            int pui = pu / (2 * _jMax + 1), puj = pu % (2 * _jMax + 1) - _jMax;
            int pvi = cur / (2 * _jMax + 1), pvj = cur % (2 * _jMax + 1) - _jMax;
            Seg first = pvi != pui ? new Seg(true, Math.Min(pui, pvi), puj) : new Seg(false, pui, Math.Min(puj, pvj));
            if (IsBuilt(first)) return false;
            return BuildSeg(first);
        }

        private bool BuildSeg(Seg s)
        {
            Ends(s, out int ai, out int aj, out int bi, out int bj);
            var req = SegmentRequest.Straight(NodePos(ai, aj), NodePos(bi, bj), RoadTypeIds.Street2);
            CityResult r = _c.Execute(new BuildRoadCityCommand(req));
            if (r == CityResult.Ok)
            {
                _built[s.H ? 0 : 1, SegIndex(s)] = true;
                _nodeExists[NodeIndex(ai, aj)] = true;
                _nodeExists[NodeIndex(bi, bj)] = true;
                RoadsBuilt++;
                _pendingPaint.Add(s);
                _frontier = Mid(s);
                return true;
            }
            else
            {
                FailedAttempts++;
                Status = "calle rechazada: " + r;
                return false;
            }
        }
    }
}
