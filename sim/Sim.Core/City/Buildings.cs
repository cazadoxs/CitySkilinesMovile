using System;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Random;

namespace Urbe.Sim.City
{
    /// <summary>Problemas de un edificio (GDD 4.12). Cada bit tiene un motivo que la interfaz puede mostrar (<see cref="BuildingRules.Describe"/>).</summary>
    [Flags]
    public enum BuildingProblems : ushort
    {
        None = 0,
        NoPower = 1,
        NoWater = 2,
        NoSewage = 4,
        /// <summary>La calle del edificio no esta conectada con la conexion exterior.</summary>
        Disconnected = 8,
        NoWorkers = 16,
        NoCustomers = 32,
        Abandoned = 64,
        /// <summary>La zona pintada bajo el edificio ya no es la suya: acabara abandonando el sitio (GDD 4.4).</summary>
        WrongZone = 128,
        NoGarbage = 256,
        NoHealth = 512,
        Polluted = 1024,
    }

    public enum BuildingKind : byte { Zoned = 0, Service = 1 }

    /// <summary>
    /// Edificio (ADR 005): de zona (crece solo) o de servicio (lo coloca el jugador). Struct en array contiguo con id estable.
    /// <c>Yaw</c> es el giro en radianes alrededor de Y con 0 mirando a +Z (atan2(frenteX, frenteZ)): el frente apunta a la calle.
    /// </summary>
    public struct Building
    {
        public EntityId Id;
        public BuildingKind Kind;
        public Vec2 Pos;
        public float Yaw;
        /// <summary>Celdas de ancho (a lo largo de la calle) y de fondo.</summary>
        public byte Width;
        public byte Depth;
        public ZoneType Zone;
        public byte Density;
        public byte Level;
        /// <summary>Estilo visual (0 Mediterraneo, 1 Ensanche).</summary>
        public byte Style;
        /// <summary>Semilla de la gramatica de forma: con ella y la parcela Unity genera siempre el mismo edificio.</summary>
        public uint Seed;
        /// <summary>Residentes maximos (R) o empleos totales (C, I, O) en este nivel.</summary>
        public ushort Capacity;
        public BuildingProblems Problems;
        /// <summary>Segmento de la calle a la que da el frente (acceso por carretera y redes).</summary>
        public EntityId Segment;
        /// <summary>Tipo de servicio (<see cref="ServiceType"/>) si Kind es Service.</summary>
        public byte ServiceType;
        public byte CellCount;
        /// <summary>Residentes que viven (R) o trabajadores empleados (C, I, O).</summary>
        public ushort Occupants;
        /// <summary>Empleos de baja y de alta cualificacion (C, I, O) y los cubiertos de cada tipo.</summary>
        public ushort JobsLow;
        public ushort JobsHigh;
        public ushort FilledLow;
        public ushort FilledHigh;
        /// <summary>Actualizaciones seguidas cumpliendo los requisitos del siguiente nivel.</summary>
        public ushort LevelProgress;
        /// <summary>Actualizaciones seguidas con un suministro basico caido.</summary>
        public ushort BadSupply;
        /// <summary>Actualizaciones desde que esta abandonado.</summary>
        public ushort AbandonedFor;
        /// <summary>Fraccion de agua y de luz recibida (0 a 255).</summary>
        public byte WaterRatio;
        public byte PowerRatio;
        /// <summary>Dia del calendario en que se construyo.</summary>
        public int BornDay;
        public byte Happiness;
        /// <summary>Educacion media de los residentes por 10 (0 a 30).</summary>
        public byte EducationTimes10;

        public bool IsAbandoned => (Problems & BuildingProblems.Abandoned) != 0;
        public bool IsAlive => Id.IsValid;
    }

    public enum BuildingEventKind : byte { Created = 0, LevelChanged = 1, Abandoned = 2, Demolished = 3 }

    /// <summary>Cambio de edificio para el render: Unity consume la cola y crea, actualiza o quita la instancia (sin asignar).</summary>
    public readonly struct BuildingEvent
    {
        public readonly BuildingEventKind Kind;
        public readonly EntityId Id;
        public readonly byte Level;

        public BuildingEvent(BuildingEventKind kind, EntityId id, byte level)
        {
            Kind = kind;
            Id = id;
            Level = level;
        }
    }

    /// <summary>Reglas puras de edificios de zona (testeables sin ciudad).</summary>
    public static class BuildingRules
    {
        public static string Describe(BuildingProblems p)
        {
            switch (p)
            {
                case BuildingProblems.NoPower: return "Sin electricidad: la red no produce lo suficiente o el edificio esta desconectado";
                case BuildingProblems.NoWater: return "Sin agua: no hay bombeo suficiente o el edificio esta desconectado";
                case BuildingProblems.NoSewage: return "Sin alcantarillado: falta capacidad de desague";
                case BuildingProblems.Disconnected: return "La calle no esta conectada con la autopista de entrada";
                case BuildingProblems.NoWorkers: return "Faltan trabajadores con la cualificacion adecuada";
                case BuildingProblems.NoCustomers: return "Faltan clientes";
                case BuildingProblems.Abandoned: return "Abandonado";
                case BuildingProblems.WrongZone: return "La zona pintada ya no es la del edificio";
                case BuildingProblems.NoGarbage: return "Sin recogida de basura";
                case BuildingProblems.NoHealth: return "Sin cobertura sanitaria";
                case BuildingProblems.Polluted: return "Aire contaminado";
                default: return "";
            }
        }

        /// <summary>Tamano de parcela en celdas (ancho a lo largo de la calle, fondo) segun zona y densidad: rango inclusive.</summary>
        public static void ParcelRange(ZoneType zone, int density, out int wMin, out int wMax, out int hMin, out int hMax)
        {
            switch (zone)
            {
                case ZoneType.Residential:
                    if (density <= 1) { wMin = 1; wMax = 2; hMin = 1; hMax = 2; }
                    else if (density == 2) { wMin = 2; wMax = 3; hMin = 2; hMax = 3; }
                    else { wMin = 3; wMax = 4; hMin = 3; hMax = 4; }
                    return;
                case ZoneType.Commercial:
                    if (density <= 1) { wMin = 1; wMax = 2; hMin = 1; hMax = 2; }
                    else { wMin = 2; wMax = 3; hMin = 2; hMax = 3; }
                    return;
                case ZoneType.Industrial:
                    wMin = 2; wMax = 4; hMin = 2; hMax = 3;
                    return;
                default:
                    if (density <= 2) { wMin = 2; wMax = 3; hMin = 2; hMax = 3; }
                    else { wMin = 3; wMax = 4; hMin = 3; hMax = 4; }
                    return;
            }
        }

        /// <summary>
        /// Nivel maximo que permiten el valor del suelo, los servicios cubiertos y la educacion media (GDD 4.5).
        /// <paramref name="services"/> es una mascara de <see cref="CoverageType"/>: bit (1 &lt;&lt; tipo).
        /// </summary>
        public static int MaxLevel(CityBalance bal, ZoneType zone, float landValue, int services, float education)
        {
            int level = 1;
            for (int next = 2; next <= 5; next++)
            {
                if (landValue < bal.LevelLandValue[next - 1]) break;
                if (!ServicesFor(services, next)) break;
                bool needsEdu = zone == ZoneType.Residential || zone == ZoneType.Office;
                if (needsEdu && education < bal.LevelEducation[next - 1]) break;
                level = next;
            }
            return level;
        }

        private static bool Has(int mask, CoverageType t) => (mask & (1 << (int)t)) != 0;

        public static bool ServicesFor(int mask, int level)
        {
            if (level >= 2 && !(Has(mask, CoverageType.Garbage) && Has(mask, CoverageType.Health))) return false;
            if (level >= 3 && !(Has(mask, CoverageType.Police) && Has(mask, CoverageType.Fire) && Has(mask, CoverageType.Park))) return false;
            if (level >= 4 && !Has(mask, CoverageType.Transit)) return false;
            if (level >= 5 && !Has(mask, CoverageType.Leisure)) return false;
            return true;
        }

        /// <summary>Residentes o empleos de un edificio recien creado o recalculado.</summary>
        public static int CapacityOf(CityBalance bal, ZoneType zone, int density, int level, int cells)
        {
            float perCell;
            switch (zone)
            {
                case ZoneType.Residential: perCell = bal.ResidentsPerCell[Math.Max(0, Math.Min(2, density - 1))]; break;
                case ZoneType.Commercial: perCell = bal.JobsPerCellCommercial[Math.Max(0, Math.Min(1, density - 1))]; break;
                case ZoneType.Industrial: perCell = bal.JobsPerCellIndustrial; break;
                default: perCell = bal.JobsPerCellOffice[Math.Max(0, Math.Min(2, density - 1))]; break;
            }
            float v = perCell * cells * bal.LevelCapacityMult[Math.Max(1, Math.Min(5, level)) - 1];
            return Math.Max(1, (int)Math.Round(v));
        }

        public static float HighSkillShare(CityBalance bal, ZoneType zone)
        {
            switch (zone)
            {
                case ZoneType.Commercial: return bal.HighSkillShareCommercial;
                case ZoneType.Industrial: return bal.HighSkillShareIndustrial;
                case ZoneType.Office: return 1f;
                default: return 0f;
            }
        }
    }

    /// <summary>
    /// Edificios de la ciudad: parcelas, crecimiento segun demanda, niveles, abandono y demolicion (GDD 4.5, 8.12). El 1/20 de los
    /// edificios se actualiza en cada tick (GDD 8.1 paso 9). Los cambios se publican en una cola de eventos para el render.
    /// </summary>
    public sealed class BuildingSystem
    {
        public const int MaxCellsPerBuilding = 16;

        private readonly CitySimulation _city;
        private readonly ZoneGrid _zones;
        private readonly CityBalance _bal;
        private Pcg32 _rng;

        private Building[] _b = new Building[512];
        private int[] _links = new int[512 * MaxCellsPerBuilding];
        private readonly IdAllocator _ids = new IdAllocator(512);
        private int _high;
        private int _count;

        private BuildingEvent[] _events = new BuildingEvent[4096];
        private int _evHead, _evCount;

        /// <summary>Se incrementa con cada alta o baja: los sistemas que cachean listas de edificios se reconstruyen.</summary>
        public uint Version { get; private set; }
        public int Count => _count;
        public int IndexBound => _high;
        /// <summary>Eventos perdidos porque la cola estaba llena (nadie los consumia).</summary>
        public int DroppedEvents { get; private set; }

        public BuildingSystem(CitySimulation city, ulong seed)
        {
            _city = city;
            _zones = city.Zones;
            _bal = city.Balance;
            _rng = new Pcg32(seed, 0x42554C44UL);
        }

        public bool IsAlive(int index) => index >= 0 && index < _high && _b[index].Id.IsValid;
        public bool IsAlive(EntityId id) => _ids.IsAlive(id);
        public ref readonly Building Get(int index) => ref _b[index];
        public ref readonly Building Get(EntityId id) => ref _b[id.Index];
        internal ref Building GetMutable(int index) => ref _b[index];
        public EntityId IdAt(int index) => index >= 0 && index < _high ? _b[index].Id : EntityId.None;

        /// <summary>Celdas del edificio (indices de <see cref="ZoneGrid"/>).</summary>
        public ReadOnlySpan<int> CellsOf(int index) => new ReadOnlySpan<int>(_links, index * MaxCellsPerBuilding, _b[index].CellCount);

        // ------------------------------------------------------------------ eventos

        public bool TryDequeueEvent(out BuildingEvent e)
        {
            if (_evCount == 0) { e = default; return false; }
            e = _events[_evHead];
            _evHead = (_evHead + 1) % _events.Length;
            _evCount--;
            return true;
        }

        public int PendingEvents => _evCount;

        private void Push(BuildingEventKind kind, in Building b)
        {
            if (_evCount == _events.Length)
            {
                DroppedEvents++;
                _evHead = (_evHead + 1) % _events.Length;
                _evCount--;
            }
            _events[(_evHead + _evCount) % _events.Length] = new BuildingEvent(kind, b.Id, b.Level);
            _evCount++;
        }

        // ------------------------------------------------------------------ alta y baja

        private int Allocate()
        {
            EntityId id = _ids.Allocate();
            int i = id.Index;
            if (i >= _b.Length)
            {
                int cap = _b.Length * 2;
                while (cap <= i) cap *= 2;
                Array.Resize(ref _b, cap);
                Array.Resize(ref _links, cap * MaxCellsPerBuilding);
            }
            if (i >= _high) _high = i + 1;
            _b[i] = default;
            _b[i].Id = id;
            _count++;
            Version++;
            return i;
        }

        /// <summary>Crea un edificio de servicio ya validado por el llamador (M3-03). Las celdas que cubre quedan ocupadas.</summary>
        internal int AddService(Vec2 pos, float yaw, byte width, byte depth, byte serviceType, EntityId segment, ReadOnlySpan<int> cells)
        {
            int i = Allocate();
            ref Building b = ref _b[i];
            b.Kind = BuildingKind.Service;
            b.Pos = pos;
            b.Yaw = yaw;
            b.Width = width;
            b.Depth = depth;
            b.Level = 1;
            b.BornDay = _city.Clock.Day;
            b.ServiceType = serviceType;
            b.Segment = segment;
            b.Seed = _rng.NextUInt();
            b.Style = StyleAt(pos);
            LinkCells(i, cells);
            Push(BuildingEventKind.Created, in b);
            return i;
        }

        private void LinkCells(int i, ReadOnlySpan<int> cells)
        {
            int n = Math.Min(cells.Length, MaxCellsPerBuilding);
            for (int k = 0; k < n; k++)
            {
                _links[i * MaxCellsPerBuilding + k] = cells[k];
                _zones.SetBuilding(cells[k], i);
            }
            _b[i].CellCount = (byte)n;
        }

        /// <summary>Derriba un edificio, libera sus celdas y avisa al render.</summary>
        public void Demolish(int index)
        {
            if (!IsAlive(index)) return;
            ref Building b = ref _b[index];
            Push(BuildingEventKind.Demolished, in b);
            _city.OnBuildingRemoved(index);
            for (int k = 0; k < b.CellCount; k++)
            {
                int c = _links[index * MaxCellsPerBuilding + k];
                if (c >= 0 && _zones.IsCellAlive(c) && _zones.GetCell(c).Building == index) _zones.SetBuilding(c, -1);
            }
            _ids.Free(b.Id);
            b = default;
            b.Id = EntityId.None;
            _count--;
            Version++;
        }

        /// <summary>Tras regenerar las celdas: traslada los edificios afectados a sus celdas nuevas o los demuele si perdieron alguna.</summary>
        internal void OnCellsRemapped()
        {
            ReadOnlySpan<int> affected = _zones.AffectedBuildings;
            for (int a = 0; a < affected.Length; a++)
            {
                int i = affected[a];
                if (!IsAlive(i)) continue;
                ref Building b = ref _b[i];
                bool lost = false;
                for (int k = 0; k < b.CellCount; k++)
                {
                    int old = _links[i * MaxCellsPerBuilding + k];
                    int nw = _zones.RemapOf(old);
                    _links[i * MaxCellsPerBuilding + k] = nw;
                    if (nw < 0) lost = true;
                }
                if (lost) { Demolish(i); continue; }
                if (b.CellCount > 0) b.Segment = _zones.GetCell(_links[i * MaxCellsPerBuilding]).Segment;
            }
        }

        internal byte StyleAt(Vec2 p)
        {
            // Barrios de unos 250 m alternan de estilo (distritos con estilo propio llegan mas adelante).
            int gx = (int)Math.Floor(p.X / 250f), gz = (int)Math.Floor(p.Z / 250f);
            return (byte)((gx + gz) & 1);
        }

        // ------------------------------------------------------------------ crecimiento

        /// <summary>Intenta crear edificios segun la demanda de cada zona (GDD 8.12). Se llama cada <c>GrowthInterval</c> ticks.</summary>
        public void Grow(in DemandState demand)
        {
            int created = 0;
            for (int z = 1; z <= 4 && created < _bal.MaxNewBuildingsPerBatch; z++)
            {
                var zone = (ZoneType)z;
                float d = demand.Get(zone);
                if (d < _bal.GrowthMinDemand || _zones.ZonedFreeCount(zone) == 0) continue;
                // Probabilidad proporcional a la demanda; con demanda alta, hasta dos intentos.
                int tries = d >= 60f ? 2 : 1;
                for (int t = 0; t < tries && created < _bal.MaxNewBuildingsPerBatch; t++)
                {
                    if (_rng.NextFloat() * 100f >= d) continue;
                    if (TrySpawn(zone)) created++;
                }
            }
        }

        private bool TrySpawn(ZoneType zone)
        {
            int bound = _zones.CellIndexBound;
            if (bound == 0) return false;
            int start = _rng.NextInt(bound);
            int best = -1;
            float bestScore = float.MinValue;
            int found = 0;
            for (int n = 0; n < bound && found < _bal.GrowthCandidates; n++)
            {
                int ci = start + n;
                if (ci >= bound) ci -= bound;
                if (!_zones.IsCellAlive(ci)) continue;
                ref readonly ZoneCell c = ref _zones.GetCell(ci);
                if (c.Zone != zone || c.Building >= 0 || c.Depth != 0) continue;
                if (!_city.CanGrowAt(c.Segment)) continue;
                found++;
                float score = _city.LandValueAt(c.Pos) + _rng.NextFloat() * 10f;
                if (score > bestScore) { bestScore = score; best = ci; }
            }
            if (best < 0) return false;
            return PlaceParcel(best);
        }

        /// <summary>Elige el mayor rectangulo libre y de la misma zona que parte de la celda de frente <paramref name="anchor"/>.</summary>
        private bool PlaceParcel(int anchor)
        {
            ref readonly ZoneCell a = ref _zones.GetCell(anchor);
            ZoneType zone = a.Zone;
            int density = a.Density;
            BuildingRules.ParcelRange(zone, density, out int wMin, out int wMax, out int hMin, out int hMax);
            int w = _rng.NextInt(wMin, wMax + 1);
            int h = _rng.NextInt(hMin, hMax + 1);
            EntityId seg = a.Segment;
            int side = a.Side, along = a.Along;
            int dir = _rng.NextInt(2) == 0 ? 1 : -1;
            int[] cells = _cellScratch;
            for (int ww = w; ww >= 1; ww--)
            {
                for (int hh = h; hh >= 1; hh--)
                {
                    int a0 = dir > 0 ? along : along - (ww - 1);
                    if (TryRect(seg, side, a0, ww, hh, zone, density, cells)) return Create(zone, density, ww, hh, cells, ww * hh);
                    a0 = dir > 0 ? along - (ww - 1) : along;
                    if (ww > 1 && TryRect(seg, side, a0, ww, hh, zone, density, cells)) return Create(zone, density, ww, hh, cells, ww * hh);
                }
            }
            return false;
        }

        private readonly int[] _cellScratch = new int[MaxCellsPerBuilding];

        private bool TryRect(EntityId seg, int side, int a0, int w, int h, ZoneType zone, int density, int[] outCells)
        {
            if (a0 < 0 || w * h > MaxCellsPerBuilding) return false;
            int n = 0;
            for (int x = 0; x < w; x++)
            {
                for (int d = 0; d < h; d++)
                {
                    int ci = _zones.CellAt(seg, side, a0 + x, d);
                    if (ci < 0) return false;
                    ref readonly ZoneCell c = ref _zones.GetCell(ci);
                    if (c.Zone != zone || c.Density != density || c.Building >= 0) return false;
                    outCells[n++] = ci;
                }
            }
            return true;
        }

        private bool Create(ZoneType zone, int density, int w, int h, int[] cells, int count)
        {
            Vec2 sum = Vec2.Zero;
            for (int k = 0; k < count; k++) sum = sum + _zones.GetCell(cells[k]).Pos;
            Vec2 center = sum / count;
            ref readonly ZoneCell first = ref _zones.GetCell(cells[0]);
            Vec2 front = first.Dir;
            int i = Allocate();
            ref Building b = ref _b[i];
            b.Kind = BuildingKind.Zoned;
            b.Pos = center;
            b.Yaw = (float)Math.Atan2(front.X, front.Z);
            b.Width = (byte)w;
            b.Depth = (byte)h;
            b.Zone = zone;
            b.Density = (byte)density;
            b.Level = 1;
            b.BornDay = _city.Clock.Day;
            b.Style = StyleAt(center);
            b.Seed = _rng.NextUInt();
            b.Segment = first.Segment;
            LinkCells(i, new ReadOnlySpan<int>(cells, 0, count));
            ApplyCapacity(ref b);
            _city.OnBuildingAdded(i);
            Push(BuildingEventKind.Created, in b);
            return true;
        }

        /// <summary>Recalcula residentes o empleos segun el nivel actual.</summary>
        internal void ApplyCapacity(ref Building b)
        {
            int cap = BuildingRules.CapacityOf(_bal, b.Zone, b.Density, b.Level, b.Width * b.Depth);
            b.Capacity = (ushort)Math.Min(cap, ushort.MaxValue);
            if (b.Zone == ZoneType.Residential)
            {
                b.JobsLow = 0;
                b.JobsHigh = 0;
                return;
            }
            int high = (int)Math.Round(cap * BuildingRules.HighSkillShare(_bal, b.Zone));
            b.JobsHigh = (ushort)high;
            b.JobsLow = (ushort)(cap - high);
            if (b.FilledLow > b.JobsLow) b.FilledLow = b.JobsLow;
            if (b.FilledHigh > b.JobsHigh) b.FilledHigh = b.JobsHigh;
        }

        // ------------------------------------------------------------------ actualizacion periodica

        /// <summary>Actualiza el 1/N de los edificios que le toca a este tick (N = BuildingUpdateInterval).</summary>
        public void UpdateSlice(ulong tick)
        {
            int interval = _bal.BuildingUpdateInterval;
            int phase = (int)(tick % (ulong)interval);
            for (int i = phase; i < _high; i += interval)
            {
                if (!_b[i].Id.IsValid) continue;
                Update(i);
            }
        }

        private void Update(int i)
        {
            ref Building b = ref _b[i];
            if (b.Kind == BuildingKind.Service) { _city.UpdateService(i); return; }

            if (b.IsAbandoned)
            {
                if (++b.AbandonedFor >= _bal.DemolishAbandonedUpdates) Demolish(i);
                return;
            }

            // Suministro (GDD 4.5: sin electricidad o agua sostenidas el edificio se abandona).
            _city.SupplyAt(b.Segment, b.Zone, out float water, out float power, out bool sewage, out bool connected);
            b.WaterRatio = (byte)(water * 255f + 0.5f);
            b.PowerRatio = (byte)(power * 255f + 0.5f);
            BuildingProblems p = b.Problems & (BuildingProblems.NoWorkers | BuildingProblems.NoCustomers | BuildingProblems.Polluted);
            if (water < 0.5f) p |= BuildingProblems.NoWater;
            if (power < 0.5f) p |= BuildingProblems.NoPower;
            if (!sewage) p |= BuildingProblems.NoSewage;
            if (!connected) p |= BuildingProblems.Disconnected;

            // Zona pintada distinta de la del edificio.
            int wrong = 0;
            for (int k = 0; k < b.CellCount; k++)
            {
                int c = _links[i * MaxCellsPerBuilding + k];
                if (c >= 0 && _zones.IsCellAlive(c) && _zones.GetCell(c).Zone != b.Zone) wrong++;
            }
            if (wrong > 0) p |= BuildingProblems.WrongZone;

            bool noSupply = (p & (BuildingProblems.NoWater | BuildingProblems.NoPower | BuildingProblems.Disconnected)) != 0;
            if (noSupply || wrong > 0)
            {
                if (b.BadSupply < ushort.MaxValue) b.BadSupply++;
            }
            else if (b.BadSupply > 0) b.BadSupply = (ushort)(b.BadSupply > 2 ? b.BadSupply - 2 : 0);
            b.Problems = p;
            if (b.BadSupply >= (wrong > 0 ? _bal.WrongZoneAbandonUpdates : _bal.AbandonUpdates))
            {
                Abandon(i);
                return;
            }
            _city.UpdateOccupancy(i);
            UpdateLevel(i);
        }

        private void Abandon(int i)
        {
            ref Building b = ref _b[i];
            b.Problems |= BuildingProblems.Abandoned;
            b.AbandonedFor = 0;
            _city.OnBuildingAbandoned(i);
            b.Occupants = 0;
            b.FilledLow = 0;
            b.FilledHigh = 0;
            Push(BuildingEventKind.Abandoned, in b);
        }

        private void UpdateLevel(int i)
        {
            ref Building b = ref _b[i];
            if (b.Level >= 5 || b.Problems != BuildingProblems.None && (b.Problems & (BuildingProblems.NoWater | BuildingProblems.NoPower | BuildingProblems.NoSewage)) != 0)
            {
                b.LevelProgress = 0;
                return;
            }
            float lv = _city.LandValueAt(b.Pos);
            int services = _city.CoverageMaskAt(b.Segment);
            float edu = b.Zone == ZoneType.Residential || b.Zone == ZoneType.Office ? _city.EducationOf(i) : 3f;
            int max = BuildingRules.MaxLevel(_bal, b.Zone, lv, services, edu);
            if (max > b.Level)
            {
                if (++b.LevelProgress >= _bal.LevelUpUpdates)
                {
                    b.Level++;
                    b.LevelProgress = 0;
                    ApplyCapacity(ref b);
                    Push(BuildingEventKind.LevelChanged, in b);
                }
            }
            else b.LevelProgress = 0;
        }

        // ------------------------------------------------------------------ hash

        public ulong ComputeHash()
        {
            ulong h = HashUtil.Mix(0x4255494CUL, (ulong)_count);
            h = HashUtil.Mix(h, _rng.State);
            h = HashUtil.Mix(h, _rng.Increment);
            for (int i = 0; i < _high; i++)
            {
                ref readonly Building b = ref _b[i];
                if (!b.Id.IsValid) continue;
                h = HashUtil.Mix(h, (ulong)(uint)i | ((ulong)(uint)b.Id.Generation << 32));
                h = HashUtil.MixF(h, b.Pos.X);
                h = HashUtil.MixF(h, b.Pos.Z);
                h = HashUtil.Mix(h, (ulong)b.Zone | ((ulong)b.Density << 8) | ((ulong)b.Level << 16) | ((ulong)b.Kind << 24) | ((ulong)b.ServiceType << 32) | ((ulong)b.Width << 40) | ((ulong)b.Depth << 48));
                h = HashUtil.Mix(h, b.Seed | ((ulong)b.Capacity << 32) | ((ulong)(ushort)b.Problems << 48));
                h = HashUtil.Mix(h, (ulong)b.Occupants | ((ulong)b.FilledLow << 16) | ((ulong)b.FilledHigh << 32) | ((ulong)b.LevelProgress << 48));
                h = HashUtil.Mix(h, (ulong)b.BadSupply | ((ulong)b.AbandonedFor << 16) | ((ulong)b.WaterRatio << 32) | ((ulong)b.PowerRatio << 40) | ((ulong)b.Happiness << 48));
            }
            return h;
        }
    }
}
