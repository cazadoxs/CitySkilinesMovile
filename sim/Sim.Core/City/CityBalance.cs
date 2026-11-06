using System;
using Urbe.Sim.Roads;

namespace Urbe.Sim.City
{
    /// <summary>
    /// Todos los numeros de equilibrio de la ciudad con sus valores por defecto (ADR 005). Sim.Core no lee JSON: Unity podra rellenar
    /// una instancia mas adelante. Los cambios de equilibrio van aqui y no repartidos por el codigo.
    /// </summary>
    public sealed class CityBalance
    {
        // ---------------------------------------------------------------- mapa y dinero
        /// <summary>Medio lado del mapa en metros (2 x 2 km).</summary>
        public float MapHalfSize = 1000f;
        public long StartMoney = 70000;
        /// <summary>Coste de construir un metro de carretera por tipo (indice = RoadTypeIds).</summary>
        public int[] RoadCostPerMeter = { 12, 10, 28, 45, 8, 60 };

        // ---------------------------------------------------------------- trafico
        /// <summary>Capacidad del array de vehiculos y tope de vehiculos fisicos simultaneos; el resto de viajes es abstracto (GDD 8.3).</summary>
        public int VehicleCapacity = 4096;
        public int PhysicalVehicleCap = 1200;

        // ---------------------------------------------------------------- zonas
        /// <summary>Anchura de calzada maxima que un cruce puede tener a cada lado de una celda (para ampliar consultas).</summary>
        public float ZoneNeighborRadius = 45f;
        /// <summary>Distancia maxima entre la posicion de una celda vieja y la nueva para heredar su zona tras regenerar.</summary>
        public float ZoneInheritRadius = 4.5f;
        /// <summary>Profundidad maxima de celdas pintables por lado (GDD 4.4).</summary>
        public int ZoneMaxDepth = 4;
        // ---------------------------------------------------------------- edificios (M3-02)
        /// <summary>Ticks entre dos actualizaciones de un mismo edificio (se reparte 1/N por tick, GDD 8.1).</summary>
        public int BuildingUpdateInterval = 20;
        public int GrowthInterval = 20;
        public int DemandInterval = 30;
        public int StatsInterval = 100;
        public int MaxNewBuildingsPerBatch = 2;
        public float GrowthMinDemand = 5f;
        public int GrowthCandidates = 6;
        /// <summary>Residentes por celda segun densidad 1, 2 y 3 (nivel 1).</summary>
        public float[] ResidentsPerCell = { 5f, 10f, 20f };
        public float[] JobsPerCellCommercial = { 3f, 6f };
        public float JobsPerCellIndustrial = 5f;
        public float[] JobsPerCellOffice = { 8f, 8f, 12f };
        public float[] LevelCapacityMult = { 1f, 1.35f, 1.8f, 2.4f, 3.1f };
        public float HighSkillShareCommercial = 0.25f;
        public float HighSkillShareIndustrial = 0.10f;
        /// <summary>Valor del suelo y educacion media necesarios para cada nivel (GDD 4.5).</summary>
        public int[] LevelLandValue = { 0, 20, 40, 60, 80 };
        public float[] LevelEducation = { 0f, 0.5f, 1.0f, 1.8f, 2.4f };
        /// <summary>Actualizaciones seguidas cumpliendo los requisitos para subir de nivel.</summary>
        public int LevelUpUpdates = 60;
        /// <summary>Actualizaciones seguidas sin agua o luz para abandonar, y con la zona cambiada (GDD 4.4).</summary>
        public int AbandonUpdates = 45;
        public int WrongZoneAbandonUpdates = 15;
        /// <summary>Actualizaciones que un edificio abandonado sigue en pie antes de demolerse solo.</summary>
        public int DemolishAbandonedUpdates = 90;
        public float DefaultLandValue = 30f;
        public float DemandBaseR = 35f;
        public float DemandBaseC = 5f;
        public float DemandBaseI = 30f;
        public float CommercialJobsPerResident = 0.10f;
        public float IndustrialJobsPerResident = 0.14f;
        public float DefaultTaxRate = 9f;
        /// <summary>Si true los edificios se llenan solos (sin ciudadanos): para pruebas de edificios, redes y economia (M3-02 a M3-05).</summary>
        public bool VirtualPopulation = false;
        public float WorkforceFraction = 0.55f;


        // ---------------------------------------------------------------- redes y servicios (M3-03)
        public ServiceInfo[] Services = ServiceCatalog.CreateDefault();
        public ServiceInfo Service(ServiceType type) => Services[(int)type];
        /// <summary>Consumo de agua y luz por residente y por empleo; la industria gasta mas luz.</summary>
        public float WaterPerResident = 0.5f, WaterPerJob = 0.25f, PowerPerResident = 0.4f, PowerPerJob = 0.6f, IndustrialPowerMult = 2f;
        /// <summary>Ticks entre recalculos de las redes (tambien al cambiar la red o los servicios).</summary>
        public int NetworkInterval = 50;
        /// <summary>Segundos a flujo libre desde el servicio hasta donde llega su cobertura (indice = CoverageType). La ciudad mide 2 km.</summary>
        public float[] CoverageSeconds = { 150f, 100f, 100f, 90f, 120f, 180f, 60f, 300f, 120f };
        public int CoverageInterval = 20;
        /// <summary>Distancia maxima del eje de una calle a la que se engancha un servicio.</summary>
        public float ServiceReach = 60f;

        // ---------------------------------------------------------------- poblacion y rutinas (M3-04)
        public int MaxPopulation = 60000;
        public int ImmigrationInterval = 60;
        /// <summary>Hogares que llegan como mucho por lote de inmigracion (con empleo de sobra y felicidad media).</summary>
        public float ImmigrationMaxPerBatch = 2.5f;
        public int ImmigrantHouseholdMax = 4;
        public float ChildChance = 0.4f;
        /// <summary>Dias de juego por ano de edad: una semana = un ano, para que se vea el ciclo vital en una partida normal.</summary>
        public int DaysPerAgeYear = 7;
        public float BirthChancePerDay = 0.004f;
        public float CarOwnership = 0.7f;
        public int EmploymentInterval = 40;
        public int RoutineSlices = 30;
        public float WorkHours = 8f;
        public float SchoolHours = 6f;
        public float ShopChance = 0.35f;
        public float LeisureChance = 0.3f;
        public int ShopStayTicks = 100;
        /// <summary>Por debajo de esta distancia (m) o sin coche la gente no conduce. Velocidades de pie y en transporte en m/s: mas altas que reales porque un dia dura 6 minutos.</summary>
        public float WalkDistance = 120f;
        public float WalkSpeed = 6f;
        public float TransitSpeed = 14f;
        public float AbstractDetour = 1.3f;
        /// <summary>Si un viaje no termina en este tiempo (ticks) se da por llegado: red de seguridad ante vehiculos perdidos.</summary>
        public int MaxTripTicks = 4000;
        /// <summary>Si ya hay tantos viajes fisicos esperando hueco para salir, los nuevos se simulan de forma abstracta (la red no absorbe mas).</summary>
        public int MaxPendingPhysical = 40;
        public int AbstractCapacity = 3000;
        public int AbstractRouteLength = 64;
        public int UnhappyThreshold = 28;
        public int UnhappyLeaveDays = 3;
        public float TruckChancePerJob = 0.0004f;
        public float TruckVehiclePerExport = 0.7f;

        // ---------------------------------------------------------------- economia, campos e hitos (M3-05)
        /// <summary>Impuesto semanal base por residente (R) o por empleo cubierto (C, I, O) al 9 % (indice = ZoneType), GDD 8.13.</summary>
        public float[] TaxPerOccupant = { 0f, 6f, 9f, 8f, 12f };
        /// <summary>Sube la recaudacion un 20 % por nivel por encima del 1.</summary>
        public float TaxLevelBonus = 0.2f;
        /// <summary>Mantenimiento semanal por metro de carretera (indice = tipo de carretera).</summary>
        public float[] RoadMaintenancePerMeter = { 0.12f, 0.10f, 0.28f, 0.45f, 0.08f, 0.5f };
        /// <summary>Presupuesto de cada servicio en % (50 a 150): multiplica su mantenimiento. Su efecto sobre la eficiencia llega con el despacho (M4).</summary>
        public int DefaultServiceBudget = 100;
        /// <summary>Poblacion de cada hito (GDD 3.3): 0 Asentamiento ... 10 Megalopolis.</summary>
        public int[] MilestonePopulation = { 0, 400, 1000, 2500, 5000, 10000, 20000, 40000, 70000, 100000, 200000 };
        public int MilestoneRoadAvenue4 = 4;
        public int MilestoneRoadAvenue6 = 6;
        /// <summary>Resolucion de los campos de valor del suelo y contaminacion del aire (m) y ticks entre pasadas (se reparten en 8).</summary>
        public float FieldCellSize = 32f;
        public int FieldInterval = 40;
        public float IndustryAirPerJob = 0.25f;
        public float IndustryAirRadius = 130f;
        public float IndustryLandValue = -10f;
        public float IndustryLandValueRadius = 90f;
        /// <summary>Puntos de felicidad que resta cada unidad de contaminacion (con tope) y puntos de valor del suelo.</summary>
        public float AirHappinessPerUnit = 0.25f;
        public float AirHappinessMax = 20f;
        public float AirLandValuePerUnit = 0.4f;

        /// <summary>Hito de poblacion que desbloquea una zona y densidad (GDD 3.3): baja densidad desde el inicio, media y oficinas en 4, alta en 5.</summary>
        public int ZoneMilestone(ZoneType zone, int density)
        {
            if (zone == ZoneType.Residential) return density >= 3 ? 5 : (density == 2 ? 4 : 0);
            if (zone == ZoneType.Commercial) return density >= 2 ? 5 : 0;
            if (zone == ZoneType.Office) return density >= 3 ? 5 : 4;
            return 0;
        }

        public int RoadMilestone(ushort roadType)
        {
            if (roadType == RoadTypeIds.Avenue4) return MilestoneRoadAvenue4;
            if (roadType == RoadTypeIds.Avenue6) return MilestoneRoadAvenue6;
            return 0;
        }

        public int RoadCost(ushort roadType, float length)
        {
            int idx = roadType < RoadCostPerMeter.Length ? roadType : 0;
            return (int)Math.Ceiling(RoadCostPerMeter[idx] * length);
        }
    }
}
