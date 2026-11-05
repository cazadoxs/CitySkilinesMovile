using System;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Sim.City
{
    /// <summary>Informe de una semana (GDD 4.10): ingresos por zona y gastos por categoria.</summary>
    public struct WeekReport
    {
        public int Week;
        public long TaxResidential, TaxCommercial, TaxIndustrial, TaxOffice;
        public long ServiceMaintenance, RoadMaintenance, Construction;
        public long MoneyAtEnd;
        public int Population;

        public long Income => TaxResidential + TaxCommercial + TaxIndustrial + TaxOffice;
        public long Expenses => ServiceMaintenance + RoadMaintenance + Construction;
        public long Net => Income - Expenses;
    }

    /// <summary>
    /// Economia de la ciudad (GDD 4.10 y 8.13): impuestos semanales por zona, mantenimiento de servicios y carreteras, informe semanal con
    /// historial, y estimacion del balance semanal con el estado actual. Sin prestamos ni bancarrota (M5).
    /// </summary>
    public sealed class EconomySystem
    {
        public const int HistoryWeeks = 52;

        private readonly CitySimulation _city;
        private readonly CityBalance _bal;
        private readonly int[] _budget = new int[16];
        private readonly WeekReport[] _history = new WeekReport[HistoryWeeks];
        private int _historyCount;
        private WeekReport _current;
        private long _lastWeekProcessed = -1;

        /// <summary>Informe de la ultima semana completa.</summary>
        public WeekReport LastWeek { get; private set; }
        /// <summary>Lo acumulado desde el inicio de la semana en curso.</summary>
        public WeekReport CurrentWeek => _current;
        public int HistoryCount => Math.Min(_historyCount, HistoryWeeks);
        /// <summary>Informes pasados, del mas antiguo (0) al mas reciente.</summary>
        public WeekReport History(int index) => _history[(_historyCount - HistoryCount + index) % HistoryWeeks];

        public EconomySystem(CitySimulation city)
        {
            _city = city;
            _bal = city.Balance;
            for (int i = 0; i < _budget.Length; i++) _budget[i] = _bal.DefaultServiceBudget;
        }

        /// <summary>Presupuesto de un servicio en % (50 a 150); multiplica su mantenimiento.</summary>
        public int GetBudget(ServiceType type) => _budget[(int)type];

        public void SetBudget(ServiceType type, int percent) => _budget[(int)type] = Math.Max(50, Math.Min(150, percent));

        internal void Record(CityExpense category, long amount)
        {
            if (category == CityExpense.Construction) _current.Construction += amount;
        }

        // ------------------------------------------------------------------ formulas

        /// <summary>Impuesto semanal de un edificio de zona: base por ocupante x nivel x (tasa / 9 %) x ocupacion (GDD 8.13).</summary>
        public float BuildingTax(in Building b)
        {
            if (b.Kind != BuildingKind.Zoned || b.IsAbandoned || b.Zone == ZoneType.None) return 0f;
            float occupants = b.Zone == ZoneType.Residential ? b.Occupants : b.FilledLow + b.FilledHigh;
            float level = 1f + _bal.TaxLevelBonus * (b.Level - 1);
            float rate = _city.TaxRate(b.Zone) / 9f;
            return occupants * _bal.TaxPerOccupant[(int)b.Zone] * level * rate;
        }

        public float ServiceMaintenance(in Building b)
        {
            if (b.Kind != BuildingKind.Service) return 0f;
            ServiceInfo info = _bal.Service((ServiceType)b.ServiceType);
            return info.Maintenance * _budget[b.ServiceType] / 100f;
        }

        /// <summary>Mantenimiento semanal de todas las carreteras.</summary>
        public float RoadMaintenance()
        {
            RoadNetwork net = _city.Network;
            float sum = 0f;
            int bound = net.SegmentIndexBound;
            for (int i = 0; i < bound; i++)
            {
                EntityId id = net.SegmentIdAt(i);
                if (!id.IsValid) continue;
                ref readonly Segment sg = ref net.GetSegment(id);
                int t = sg.RoadTypeId < _bal.RoadMaintenancePerMeter.Length ? sg.RoadTypeId : 0;
                sum += sg.Length * _bal.RoadMaintenancePerMeter[t];
            }
            return sum;
        }

        /// <summary>Balance semanal esperado con el estado actual de la ciudad: lo que recaudaria y gastaria si la semana acabase ahora.</summary>
        public void Estimate(out long income, out long expenses)
        {
            BuildingSystem bs = _city.Buildings;
            float tax = 0f, upkeep = 0f;
            int bound = bs.IndexBound;
            for (int i = 0; i < bound; i++)
            {
                if (!bs.IsAlive(i)) continue;
                ref readonly Building b = ref bs.Get(i);
                tax += BuildingTax(in b);
                upkeep += ServiceMaintenance(in b);
            }
            income = (long)tax;
            expenses = (long)(upkeep + RoadMaintenance());
        }

        // ------------------------------------------------------------------ tick

        /// <summary>Al empezar cada semana (domingo-lunes a las 06:00) cobra impuestos y paga mantenimiento.</summary>
        public void Step(ulong tick)
        {
            if (tick == 0) return;
            var clock = new CityClock(tick);
            if (clock.TickOfDay != 0 || clock.DayOfWeek != 0) return;
            long week = clock.Week;
            if (week == _lastWeekProcessed) return;
            _lastWeekProcessed = week;
            Close((int)week - 1);
        }

        private void Close(int week)
        {
            BuildingSystem bs = _city.Buildings;
            float r = 0, c = 0, ind = 0, o = 0, upkeep = 0;
            int bound = bs.IndexBound;
            for (int i = 0; i < bound; i++)
            {
                if (!bs.IsAlive(i)) continue;
                ref readonly Building b = ref bs.Get(i);
                if (b.Kind == BuildingKind.Service) { upkeep += ServiceMaintenance(in b); continue; }
                float t = BuildingTax(in b);
                switch (b.Zone)
                {
                    case ZoneType.Residential: r += t; break;
                    case ZoneType.Commercial: c += t; break;
                    case ZoneType.Industrial: ind += t; break;
                    default: o += t; break;
                }
            }
            WeekReport w = _current;
            w.Week = week;
            w.TaxResidential = (long)r;
            w.TaxCommercial = (long)c;
            w.TaxIndustrial = (long)ind;
            w.TaxOffice = (long)o;
            w.ServiceMaintenance = (long)upkeep;
            w.RoadMaintenance = (long)RoadMaintenance();
            _city.AddMoney(w.Income - w.ServiceMaintenance - w.RoadMaintenance);
            w.MoneyAtEnd = _city.Money;
            w.Population = _city.Stats.Population;
            LastWeek = w;
            _history[_historyCount % HistoryWeeks] = w;
            _historyCount++;
            _current = default;
        }

        public ulong ComputeHash()
        {
            ulong h = HashUtil.Mix(0x45434F4EUL, (ulong)_historyCount);
            h = HashUtil.Mix(h, (ulong)LastWeek.Income);
            h = HashUtil.Mix(h, (ulong)(LastWeek.ServiceMaintenance + LastWeek.RoadMaintenance));
            h = HashUtil.Mix(h, (ulong)_current.Construction);
            for (int i = 0; i < _budget.Length; i++) h = HashUtil.Mix(h, (ulong)_budget[i]);
            return h;
        }
    }

    /// <summary>Hito de poblacion (GDD 3.3) y lo que desbloquea, para la interfaz.</summary>
    public sealed class MilestoneInfo
    {
        public int Index;
        public string Name = "";
        public int Population;
        public string[] Unlocks = Array.Empty<string>();
    }

    public static class Milestones
    {
        private static readonly string[] Names =
        {
            "Asentamiento", "Aldea", "Pueblo", "Villa", "Ciudad pequena", "Ciudad", "Ciudad grande", "Gran ciudad", "Capital", "Metropolis", "Megalopolis",
        };

        private static readonly string[][] Unlocks =
        {
            new[] { "streets", "zones_low_density", "water_pump", "sewage_outlet", "wind_turbine", "coal_plant" },
            new[] { "landfill", "health_center", "park" },
            new[] { "fire_station", "police_station", "school" },
            new string[0],
            new[] { "zones_medium_density", "zones_office", "high_school", "avenue_4" },
            new[] { "zones_high_density" },
            new[] { "avenue_6" },
            new string[0], new string[0], new string[0], new string[0],
        };

        public static MilestoneInfo Get(CityBalance bal, int index)
        {
            return new MilestoneInfo { Index = index, Name = Names[index], Population = bal.MilestonePopulation[index], Unlocks = Unlocks[index] };
        }

        public static int Count => Names.Length;

        /// <summary>Hito mas alto cuya poblacion se ha alcanzado.</summary>
        public static int IndexFor(CityBalance bal, int peakPopulation)
        {
            int idx = 0;
            for (int i = 1; i < bal.MilestonePopulation.Length; i++) if (peakPopulation >= bal.MilestonePopulation[i]) idx = i;
            return idx;
        }
    }

    /// <summary>
    /// Campos de rejilla de 32 m (GDD 8.11): valor del suelo (0 a 100) y contaminacion del aire. Se recalculan por bandas: cada pasada
    /// reparte la influencia de 1/8 de las fuentes (servicios e industria) en un acumulador, y a la octava se publica el resultado.
    /// </summary>
    public sealed class FieldSystem
    {
        public const int Bands = 8;

        private readonly CitySimulation _city;
        private readonly CityBalance _bal;
        private readonly int _size;
        private readonly float _cell;
        private readonly Vec2 _min;
        private readonly float[] _land;
        private readonly float[] _air;
        private readonly float[] _landAcc;
        private readonly float[] _airAcc;
        private int _band;

        public int GridSize => _size;
        public float CellMeters => _cell;
        /// <summary>Valor del suelo por celda (fila a fila desde la esquina minima), de 0 a 100.</summary>
        public float[] LandValueGrid => _land;
        public float[] AirPollutionGrid => _air;

        public FieldSystem(CitySimulation city)
        {
            _city = city;
            _bal = city.Balance;
            _cell = _bal.FieldCellSize;
            _size = Math.Max(1, (int)Math.Ceiling(2f * _bal.MapHalfSize / _cell));
            _min = new Vec2(-_bal.MapHalfSize, -_bal.MapHalfSize);
            _land = new float[_size * _size];
            _air = new float[_size * _size];
            _landAcc = new float[_size * _size];
            _airAcc = new float[_size * _size];
            for (int i = 0; i < _land.Length; i++) _land[i] = _bal.DefaultLandValue;
        }

        public float LandValueAt(Vec2 p) => _land[Index(p)];
        public float AirPollutionAt(Vec2 p) => _air[Index(p)];

        private int Index(Vec2 p)
        {
            int x = Math.Max(0, Math.Min(_size - 1, (int)Math.Floor((p.X - _min.X) / _cell)));
            int z = Math.Max(0, Math.Min(_size - 1, (int)Math.Floor((p.Z - _min.Z) / _cell)));
            return z * _size + x;
        }

        public void Step(ulong tick)
        {
            if (tick % (ulong)_bal.FieldInterval != 0) return;
            BuildingSystem bs = _city.Buildings;
            int bound = bs.IndexBound;
            for (int i = _band; i < bound; i += Bands)
            {
                if (!bs.IsAlive(i)) continue;
                ref readonly Building b = ref bs.Get(i);
                if (b.IsAbandoned) continue;
                if (b.Kind == BuildingKind.Service)
                {
                    ServiceInfo info = _bal.Service((ServiceType)b.ServiceType);
                    if (info.LandValueBonus != 0f) Scatter(_landAcc, b.Pos, info.LandValueRadius, info.LandValueBonus);
                    if (info.AirPollution > 0f) Scatter(_airAcc, b.Pos, _bal.IndustryAirRadius * 1.5f, info.AirPollution);
                }
                else if (b.Zone == ZoneType.Industrial)
                {
                    Scatter(_airAcc, b.Pos, _bal.IndustryAirRadius, (b.JobsLow + b.JobsHigh) * _bal.IndustryAirPerJob);
                    Scatter(_landAcc, b.Pos, _bal.IndustryLandValueRadius, _bal.IndustryLandValue);
                }
                else if (b.Zone == ZoneType.Residential && b.Level >= 3)
                {
                    Scatter(_landAcc, b.Pos, 70f, 2f);
                }
            }
            if (++_band < Bands) return;
            _band = 0;
            for (int i = 0; i < _land.Length; i++)
            {
                float air = _airAcc[i];
                float v = _bal.DefaultLandValue + _landAcc[i] - air * _bal.AirLandValuePerUnit;
                _land[i] = v < 0f ? 0f : (v > 100f ? 100f : v);
                _air[i] = air;
                _landAcc[i] = 0f;
                _airAcc[i] = 0f;
            }
        }

        /// <summary>Suma <paramref name="amount"/> con caida lineal hasta <paramref name="radius"/> a las celdas cuyo centro queda dentro.</summary>
        private void Scatter(float[] target, Vec2 p, float radius, float amount)
        {
            int x0 = Math.Max(0, (int)Math.Floor((p.X - radius - _min.X) / _cell));
            int x1 = Math.Min(_size - 1, (int)Math.Floor((p.X + radius - _min.X) / _cell));
            int z0 = Math.Max(0, (int)Math.Floor((p.Z - radius - _min.Z) / _cell));
            int z1 = Math.Min(_size - 1, (int)Math.Floor((p.Z + radius - _min.Z) / _cell));
            for (int z = z0; z <= z1; z++)
            {
                float cz = _min.Z + (z + 0.5f) * _cell;
                for (int x = x0; x <= x1; x++)
                {
                    float cx = _min.X + (x + 0.5f) * _cell;
                    float dx = cx - p.X, dz = cz - p.Z;
                    float d = (float)Math.Sqrt(dx * dx + dz * dz);
                    if (d >= radius) continue;
                    target[z * _size + x] += amount * (1f - d / radius);
                }
            }
        }

        public ulong ComputeHash()
        {
            ulong h = 0x4649454CUL;
            ulong sum = 0;
            for (int i = 0; i < _land.Length; i++)
                unchecked { sum += HashUtil.MixF(HashUtil.MixF((ulong)(uint)i, _land[i]), _air[i]); }
            return HashUtil.Mix(h, sum);
        }
    }
}
