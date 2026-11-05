using System;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;

namespace Urbe.Sim.City
{
    // Orquestacion del tick y enganche de los sistemas de ciudad (orden de GDD 8.1).
    public sealed partial class CitySimulation
    {
        private DemandState _demand;
        private readonly float[] _taxRates = new float[5];

        public BuildingSystem Buildings { get; private set; } = null!;
        public CityStats Stats { get; } = new CityStats();
        /// <summary>Demanda actual de cada zona, de -100 a +100.</summary>
        public DemandState Demand => _demand;

        /// <summary>
        /// Sandbox y pruebas: si no es null, el suministro de agua, luz y alcantarillado y las coberturas de servicio se dan por
        /// cumplidos (true) o por inexistentes (false), sin mirar las redes.
        /// </summary>
        public bool? SupplyOverride { get; set; }

        public float TaxRate(ZoneType zone) => _taxRates[(int)zone];

        private void InitSystems(ulong seed)
        {
            for (int i = 0; i < _taxRates.Length; i++) _taxRates[i] = Balance.DefaultTaxRate;
            Buildings = new BuildingSystem(this, seed ^ 0x4255494CUL);
            InitServices();
            _demand.R = Balance.DemandBaseR;
            _demand.I = Balance.DemandBaseI;
        }

        /// <summary>Regenera lo que depende de la red viaria (celdas de zona, edificios sobre ellas y, mas adelante, redes).</summary>
        private void AfterNetworkChange()
        {
            if (Zones.Refresh())
            {
                Buildings.OnCellsRemapped();
                ValidateServices();
            }
            Supply.Dirty = true;
        }

        /// <summary>Un tick de ciudad, en el orden de GDD 8.1.</summary>
        public void Step()
        {
            ulong tick = Traffic.Tick;
            ProcessCommands();
            AfterNetworkChange();
            Supply.Update(tick);
            Coverage.Step(tick);
            if (tick % (ulong)Balance.StatsInterval == 0) RecomputeStats();
            if (tick % (ulong)Balance.DemandInterval == 0) UpdateDemand();
            if (tick % (ulong)Balance.GrowthInterval == 0) Buildings.Grow(_demand);
            Buildings.UpdateSlice(tick);
            Traffic.Step();
        }

        private void UpdateDemand()
        {
            DemandState target = DemandModel.Compute(Balance, Stats, TaxRate(ZoneType.Residential), TaxRate(ZoneType.Commercial),
                TaxRate(ZoneType.Industrial), TaxRate(ZoneType.Office));
            const float k = 0.25f;
            _demand.R = DemandModel.Smooth(_demand.R, target.R, k);
            _demand.C = DemandModel.Smooth(_demand.C, target.C, k);
            _demand.I = DemandModel.Smooth(_demand.I, target.I, k);
            _demand.O = DemandModel.Smooth(_demand.O, target.O, k);
        }

        /// <summary>Barrido de totales: poblacion, vivienda y empleo (cada StatsInterval ticks).</summary>
        public void RecomputeStats()
        {
            CityStats s = Stats;
            s.Buildings = Buildings.Count;
            s.AbandonedBuildings = 0;
            s.HousingCapacity = 0;
            s.JobsLow = s.JobsHigh = s.FilledLow = s.FilledHigh = 0;
            s.CommercialJobs = s.IndustrialJobs = s.OfficeJobs = 0;
            int residents = 0;
            int bound = Buildings.IndexBound;
            for (int i = 0; i < bound; i++)
            {
                if (!Buildings.IsAlive(i)) continue;
                ref readonly Building b = ref Buildings.Get(i);
                if (b.Kind != BuildingKind.Zoned) continue;
                if (b.IsAbandoned) { s.AbandonedBuildings++; continue; }
                if (b.Zone == ZoneType.Residential)
                {
                    s.HousingCapacity += b.Capacity;
                    residents += b.Occupants;
                    continue;
                }
                s.JobsLow += b.JobsLow;
                s.JobsHigh += b.JobsHigh;
                s.FilledLow += b.FilledLow;
                s.FilledHigh += b.FilledHigh;
                int jobs = b.JobsLow + b.JobsHigh;
                if (b.Zone == ZoneType.Commercial) s.CommercialJobs += jobs;
                else if (b.Zone == ZoneType.Industrial) s.IndustrialJobs += jobs;
                else s.OfficeJobs += jobs;
            }
            if (Balance.VirtualPopulation)
            {
                s.Population = residents;
                s.Adults = (int)(residents * Balance.WorkforceFraction / 0.9f);
                int workforce = (int)(residents * Balance.WorkforceFraction);
                int filled = s.FilledLow + s.FilledHigh;
                int unemployed = Math.Max(0, workforce - filled);
                s.UnemployedHigh = (int)(unemployed * 0.15f);
                s.UnemployedLow = unemployed - s.UnemployedHigh;
                s.AverageHappiness = 60f;
            }
            s.HousingVacant = Math.Max(0, s.HousingCapacity - s.Population);
            RecomputeStatsExtra();
        }

        partial void RecomputeStatsExtra();

        // ------------------------------------------------------------------ enganches de los edificios con el resto de sistemas

        internal void OnBuildingAdded(int index)
        {
            if (Balance.VirtualPopulation) Buildings.GetMutable(index).EducationTimes10 = 30;
            OnBuildingAddedExtra(index);
        }

        internal void OnBuildingRemoved(int index) { OnBuildingRemovedExtra(index); }

        internal void OnBuildingAbandoned(int index) { OnBuildingAbandonedExtra(index); }

        partial void OnBuildingAddedExtra(int index);
        partial void OnBuildingRemovedExtra(int index);
        partial void OnBuildingAbandonedExtra(int index);

        /// <summary>Llena un edificio sin ciudadanos reales (poblacion virtual): sube un poco por actualizacion hasta su capacidad.</summary>
        internal void UpdateOccupancy(int index)
        {
            if (!Balance.VirtualPopulation) return;
            ref Building b = ref Buildings.GetMutable(index);
            if (b.Zone == ZoneType.Residential)
            {
                int step = Math.Max(1, b.Capacity / 8);
                if (b.Occupants < b.Capacity) b.Occupants = (ushort)Math.Min(b.Capacity, b.Occupants + step);
                else if (b.Occupants > b.Capacity) b.Occupants = b.Capacity;
                return;
            }
            if (Stats.Unemployed > 0 || Stats.Population == 0)
            {
                if (b.FilledLow < b.JobsLow) b.FilledLow = (ushort)Math.Min(b.JobsLow, b.FilledLow + Math.Max(1, b.JobsLow / 8));
                if (b.FilledHigh < b.JobsHigh && Stats.UnemployedHigh > 0) b.FilledHigh = (ushort)Math.Min(b.JobsHigh, b.FilledHigh + Math.Max(1, b.JobsHigh / 8));
            }
            b.Occupants = (ushort)(b.FilledLow + b.FilledHigh);
        }

        // ------------------------------------------------------------------ servicios del entorno (se sustituyen por los sistemas reales)

        /// <summary>True si en esa calle pueden crecer edificios: acceso por carretera, agua y luz.</summary>
        public bool CanGrowAt(EntityId segment)
        {
            SupplyAt(segment, ZoneType.None, out float water, out float power, out _, out bool connected);
            return connected && water >= 0.5f && power >= 0.5f;
        }

        /// <summary>Agua y luz recibidas (0 a 1) por la calle, alcantarillado disponible y conexion con el exterior.</summary>
        internal void SupplyAt(EntityId segment, ZoneType zone, out float water, out float power, out bool sewage, out bool connected)
        {
            bool ok = SupplyOverride ?? false;
            water = power = ok ? 1f : 0f;
            sewage = connected = ok;
            SupplyAtExtra(segment, zone, ref water, ref power, ref sewage, ref connected);
        }

        partial void SupplyAtExtra(EntityId segment, ZoneType zone, ref float water, ref float power, ref bool sewage, ref bool connected);

        /// <summary>Valor del suelo (0 a 100) en un punto.</summary>
        public float LandValueAt(Vec2 p)
        {
            float v = Balance.DefaultLandValue;
            LandValueExtra(p, ref v);
            return v;
        }

        partial void LandValueExtra(Vec2 p, ref float value);

        /// <summary>Mascara de coberturas (bit 1 &lt;&lt; CoverageType) que alcanzan a esa calle.</summary>
        internal int CoverageMaskAt(EntityId segment)
        {
            int mask = SupplyOverride == true ? 0x1FF : 0;
            CoverageMaskExtra(segment, ref mask);
            return mask;
        }

        partial void CoverageMaskExtra(EntityId segment, ref int mask);

        internal float EducationOf(int building) => Buildings.Get(building).EducationTimes10 / 10f;

        internal void UpdateService(int index) { UpdateServiceExtra(index); }

        partial void UpdateServiceExtra(int index);
    }
}
