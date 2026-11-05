using System;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Sim.City
{
    /// <summary>Datos de un edificio de servicio (ADR 005): huella en celdas, coste, mantenimiento, hito que lo desbloquea y efectos.</summary>
    public sealed class ServiceInfo
    {
        public ServiceType Type;
        public string Key = "";
        /// <summary>Huella en celdas de 8 m: ancho a lo largo de la calle y fondo.</summary>
        public byte Width, Depth;
        public int Cost;
        /// <summary>Mantenimiento por semana al 100 % de presupuesto.</summary>
        public int Maintenance;
        /// <summary>Poblacion que desbloquea el servicio (GDD 3.3).</summary>
        public int UnlockPopulation;
        public float WaterProduction;
        public float SewerCapacity;
        public float PowerProduction;
        public float AirPollution;
        public float GroundPollution;
        /// <summary>Cobertura que da, o -1.</summary>
        public int Coverage = -1;
        /// <summary>Plazas de alumnos de un colegio o instituto.</summary>
        public int Students;
        /// <summary>Suma al valor del suelo cercano (puntos en el centro) y radio en metros.</summary>
        public float LandValueBonus;
        public float LandValueRadius;
        /// <summary>Consumo propio de agua y luz.</summary>
        public float Water, Power;
        public bool IsProducer => WaterProduction > 0f || SewerCapacity > 0f || PowerProduction > 0f;
    }

    public static class ServiceCatalog
    {
        public static ServiceInfo[] CreateDefault()
        {
            var a = new ServiceInfo[12];
            a[(int)ServiceType.None] = new ServiceInfo { Type = ServiceType.None, Key = "none" };
            a[(int)ServiceType.WaterPump] = new ServiceInfo { Type = ServiceType.WaterPump, Key = "water_pump", Width = 2, Depth = 2, Cost = 4000, Maintenance = 90, WaterProduction = 1200f };
            a[(int)ServiceType.SewageOutlet] = new ServiceInfo { Type = ServiceType.SewageOutlet, Key = "sewage_outlet", Width = 2, Depth = 2, Cost = 3000, Maintenance = 70, SewerCapacity = 1500f, GroundPollution = 4f };
            a[(int)ServiceType.WindTurbine] = new ServiceInfo { Type = ServiceType.WindTurbine, Key = "wind_turbine", Width = 2, Depth = 2, Cost = 4500, Maintenance = 40, PowerProduction = 160f };
            a[(int)ServiceType.CoalPlant] = new ServiceInfo { Type = ServiceType.CoalPlant, Key = "coal_plant", Width = 3, Depth = 3, Cost = 26000, Maintenance = 420, PowerProduction = 1600f, AirPollution = 45f, LandValueBonus = -25f, LandValueRadius = 110f };
            a[(int)ServiceType.Landfill] = new ServiceInfo { Type = ServiceType.Landfill, Key = "landfill", Width = 3, Depth = 3, Cost = 12000, Maintenance = 160, UnlockPopulation = 400, Coverage = (int)CoverageType.Garbage, GroundPollution = 12f, LandValueBonus = -12f, LandValueRadius = 80f, Water = 10f, Power = 15f };
            a[(int)ServiceType.HealthCenter] = new ServiceInfo { Type = ServiceType.HealthCenter, Key = "health_center", Width = 2, Depth = 2, Cost = 8000, Maintenance = 200, UnlockPopulation = 400, Coverage = (int)CoverageType.Health, LandValueBonus = 8f, LandValueRadius = 90f, Water = 20f, Power = 25f };
            a[(int)ServiceType.FireStation] = new ServiceInfo { Type = ServiceType.FireStation, Key = "fire_station", Width = 2, Depth = 2, Cost = 9000, Maintenance = 220, UnlockPopulation = 1000, Coverage = (int)CoverageType.Fire, LandValueBonus = 4f, LandValueRadius = 80f, Water = 10f, Power = 20f };
            a[(int)ServiceType.PoliceStation] = new ServiceInfo { Type = ServiceType.PoliceStation, Key = "police_station", Width = 2, Depth = 2, Cost = 9000, Maintenance = 220, UnlockPopulation = 1000, Coverage = (int)CoverageType.Police, LandValueBonus = 5f, LandValueRadius = 80f, Water = 10f, Power = 20f };
            a[(int)ServiceType.School] = new ServiceInfo { Type = ServiceType.School, Key = "school", Width = 3, Depth = 2, Cost = 12000, Maintenance = 260, UnlockPopulation = 1000, Coverage = (int)CoverageType.School, Students = 300, LandValueBonus = 6f, LandValueRadius = 100f, Water = 25f, Power = 30f };
            a[(int)ServiceType.HighSchool] = new ServiceInfo { Type = ServiceType.HighSchool, Key = "high_school", Width = 3, Depth = 3, Cost = 25000, Maintenance = 420, UnlockPopulation = 5000, Coverage = (int)CoverageType.HighSchool, Students = 400, LandValueBonus = 8f, LandValueRadius = 110f, Water = 35f, Power = 45f };
            a[(int)ServiceType.Park] = new ServiceInfo { Type = ServiceType.Park, Key = "park", Width = 2, Depth = 2, Cost = 3000, Maintenance = 30, UnlockPopulation = 400, Coverage = (int)CoverageType.Park, LandValueBonus = 22f, LandValueRadius = 90f };
            return a;
        }
    }

    // ---------------------------------------------------------------------------------------------- ordenes

    /// <summary>
    /// Coloca un edificio de servicio (GDD 5.5): se ajusta a la carretera mas cercana y gira para mirarla; ocupa las celdas bajo su
    /// huella. Rechaza con motivo si no esta desbloqueado, no alcanza el dinero, choca con una calzada o un edificio, o no hay calle cerca.
    /// </summary>
    public sealed class PlaceServiceCommand : CityCommand
    {
        private readonly ServiceType _type;
        private readonly Vec2 _pos;
        private EntityId _built = EntityId.None;
        private long _paid;

        /// <summary>Edificio creado por el ultimo Apply.</summary>
        public EntityId Building => _built;

        public PlaceServiceCommand(ServiceType type, Vec2 position)
        {
            _type = type;
            _pos = position;
        }

        public override CityResult Validate(CitySimulation city) => Result = city.CheckService(_type, _pos, out _, out _, out _, null, out _);

        public override bool Apply(CitySimulation city)
        {
            int[] cells = city.QueryBuffer;
            CityResult r = city.CheckService(_type, _pos, out Vec2 pos, out float yaw, out EntityId seg, cells, out int n);
            if (r != CityResult.Ok) { Result = r; return false; }
            ServiceInfo info = city.Balance.Service(_type);
            int idx = city.Buildings.AddService(pos, yaw, info.Width, info.Depth, (byte)_type, seg, new ReadOnlySpan<int>(cells, 0, n));
            _built = city.Buildings.IdAt(idx);
            _paid = info.Cost;
            city.Spend(_paid, CityExpense.Construction);
            city.NotifyServicesChanged();
            Result = CityResult.Ok;
            return true;
        }

        public override void Undo(CitySimulation city)
        {
            if (city.Buildings.IsAlive(_built)) city.Buildings.Demolish(_built.Index);
            city.Refund(_paid, CityExpense.Construction);
            city.NotifyServicesChanged();
            _built = EntityId.None;
        }
    }

    /// <summary>Derriba un edificio de servicio (sin reembolso). Deshacer lo vuelve a colocar donde estaba, sin coste.</summary>
    public sealed class DemolishServiceCommand : CityCommand
    {
        private readonly EntityId _target;
        private ServiceType _type;
        private Vec2 _pos;
        private EntityId _again = EntityId.None;

        public DemolishServiceCommand(EntityId building) { _target = building; }

        public override CityResult Validate(CitySimulation city)
        {
            if (!city.Buildings.IsAlive(_target) || city.Buildings.Get(_target).Kind != BuildingKind.Service) return Result = CityResult.InvalidTarget;
            return Result = CityResult.Ok;
        }

        public override bool Apply(CitySimulation city)
        {
            if (Validate(city) != CityResult.Ok) return false;
            ref readonly Building b = ref city.Buildings.Get(_target);
            _type = (ServiceType)b.ServiceType;
            _pos = b.Pos;
            city.Buildings.Demolish(_target.Index);
            city.NotifyServicesChanged();
            return true;
        }

        public override void Undo(CitySimulation city)
        {
            int[] cells = city.QueryBuffer;
            if (city.CheckService(_type, _pos, out Vec2 pos, out float yaw, out EntityId seg, cells, out int n, true) != CityResult.Ok) return;
            ServiceInfo info = city.Balance.Service(_type);
            int idx = city.Buildings.AddService(pos, yaw, info.Width, info.Depth, (byte)_type, seg, new ReadOnlySpan<int>(cells, 0, n));
            _again = city.Buildings.IdAt(idx);
            city.NotifyServicesChanged();
        }
    }

    // ---------------------------------------------------------------------------------------------- colocacion

    public sealed partial class CitySimulation
    {
        /// <summary>Mayor poblacion alcanzada (los hitos no se pierden al bajar de poblacion).</summary>
        public int PeakPopulation { get; private set; }

        public bool IsServiceUnlocked(ServiceType type) => PeakPopulation >= Balance.Service(type).UnlockPopulation;

        /// <summary>
        /// Comprueba y resuelve la colocacion de un servicio: ajusta la posicion a la calle mas cercana, calcula el giro y las celdas
        /// que cubre. <paramref name="cells"/> (si no es null) recibe esas celdas.
        /// </summary>
        internal CityResult CheckService(ServiceType type, Vec2 want, out Vec2 pos, out float yaw, out EntityId segment,
            int[]? cells, out int cellCount, bool free = false)
        {
            pos = want;
            yaw = 0f;
            segment = EntityId.None;
            cellCount = 0;
            if (type == ServiceType.None || (int)type >= Balance.Services.Length) return CityResult.InvalidArgument;
            ServiceInfo info = Balance.Service(type);
            float lim = Balance.MapHalfSize - 12f;
            if (Math.Abs(want.X) > lim || Math.Abs(want.Z) > lim) return CityResult.OutOfBounds;
            if (!free && !IsServiceUnlocked(type)) return CityResult.NotUnlocked;
            if (!free && _money < info.Cost) return CityResult.InsufficientFunds;

            float halfD = info.Depth * ZoneGrid.CellHalf;
            float halfW = info.Width * ZoneGrid.CellHalf;
            float reach = 60f + halfD;
            if (!Network.NearestSegment(want, reach, out EntityId seg, out float t, out float dist)) return CityResult.NoRoadAccess;
            ref readonly Segment sg = ref Network.GetSegment(seg);
            float roadHalf = Network.Types.Get(sg.RoadTypeId).TotalWidth * 0.5f;
            Vec2 onRoad = sg.Curve.Evaluate(t);
            Vec2 outward = want - onRoad;
            if (outward.LengthSquared < 0.01f)
            {
                Vec2 tan = sg.Curve.Tangent(t);
                outward = OffsetCurve.Right(tan);
            }
            outward = outward.Normalized;
            // Ajuste: el borde delantero queda a 1 m de la acera.
            pos = onRoad + outward * (roadHalf + 1f + halfD);
            Vec2 front = -outward;
            yaw = (float)Math.Atan2(front.X, front.Z);
            Vec2 u = front, v = front.Perp;

            if (Math.Abs(pos.X) > lim || Math.Abs(pos.Z) > lim) return CityResult.OutOfBounds;

            // No puede pisar la calzada de ninguna otra calle (cuatro esquinas y centro).
            float radius = (float)Math.Sqrt(halfW * halfW + halfD * halfD);
            if (FootprintOnRoad(pos, u, v, halfD, halfW)) return CityResult.Overlaps;

            // Celdas bajo la huella: todas deben estar libres.
            int[] buf = cells ?? _svcCells;
            int count = Zones.QueryCircle(pos, radius + 8f, _svcCand);
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                int ci = _svcCand[i];
                ref readonly ZoneCell c = ref Zones.GetCell(ci);
                Vec2 rel = c.Pos - pos;
                if (Math.Abs(Vec2.Dot(rel, u)) > halfD + 2f || Math.Abs(Vec2.Dot(rel, v)) > halfW + 2f) continue;
                if (c.Building >= 0) return CityResult.Overlaps;
                if (n >= BuildingSystem.MaxCellsPerBuilding) return CityResult.Overlaps;
                buf[n++] = ci;
            }
            // Otros servicios sin celdas: se comprueba por distancia entre centros.
            int bound = Buildings.IndexBound;
            for (int i = 0; i < bound; i++)
            {
                if (!Buildings.IsAlive(i)) continue;
                ref readonly Building o = ref Buildings.Get(i);
                if (o.Kind != BuildingKind.Service) continue;
                float need = (Math.Max(o.Width, o.Depth) + Math.Max(info.Width, info.Depth)) * ZoneGrid.CellHalf * 0.85f;
                if (Vec2.Distance(o.Pos, pos) < need) return CityResult.Overlaps;
            }
            segment = seg;
            cellCount = n;
            return CityResult.Ok;
        }


        /// <summary>True si alguna esquina o el centro de la huella cae dentro de la calzada de cualquier calle.</summary>
        internal bool FootprintOnRoad(Vec2 pos, Vec2 u, Vec2 v, float halfD, float halfW)
        {
            float radius = (float)Math.Sqrt(halfW * halfW + halfD * halfD);
            var box = new Aabb2(new Vec2(pos.X - radius - 30f, pos.Z - radius - 30f), new Vec2(pos.X + radius + 30f, pos.Z + radius + 30f));
            int ns = Network.QuerySegments(box, _svcSeg);
            for (int q = 0; q < ns; q++)
            {
                ref readonly Segment o = ref Network.GetSegment(_svcSeg[q]);
                float half = Network.Types.Get(o.RoadTypeId).TotalWidth * 0.5f;
                for (int k = 0; k < 5; k++)
                {
                    Vec2 p = k == 0 ? pos : pos + u * ((k & 1) == 0 ? halfD : -halfD) + v * (k <= 2 ? halfW : -halfW);
                    float d = Vec2.Distance(o.Curve.Evaluate(o.Curve.ClosestT(p)), p);
                    // El borde frontal esta a 1 m de la acera de su propia calle: solo se exige no entrar en la calzada.
                    if (d < half - 0.2f) return true;
                }
            }
            return false;
        }
        private readonly EntityId[] _svcSeg = new EntityId[128];
        private readonly int[] _svcCand = new int[512];
        private readonly int[] _svcCells = new int[BuildingSystem.MaxCellsPerBuilding];

        private static bool RectOverlapsCell(Vec2 c, Vec2 u, Vec2 v, float hu, float hv, Vec2 cp, Vec2 cdir)
        {
            const float h = 3.95f;
            Vec2 cu = cdir, cv = cdir.Perp;
            Vec2 d = cp - c;
            // Ejes: los del rectangulo y los de la celda.
            for (int k = 0; k < 4; k++)
            {
                Vec2 axis = k == 0 ? u : k == 1 ? v : k == 2 ? cu : cv;
                float ra = hu * Math.Abs(Vec2.Dot(u, axis)) + hv * Math.Abs(Vec2.Dot(v, axis));
                float rb = h * (Math.Abs(Vec2.Dot(cu, axis)) + Math.Abs(Vec2.Dot(cv, axis)));
                if (Math.Abs(Vec2.Dot(d, axis)) >= ra + rb) return false;
            }
            return true;
        }
    }
}
