using System.Text;
using Urbe.Sim.City;
using Urbe.Sim.Roads;

namespace Urbe.Game.UI
{
    /// <summary>
    /// Textos en español de la ciudad (M3-07, M3-08): nombres de zonas, servicios e hitos, motivos de rechazo de las órdenes, requisitos de
    /// desbloqueo y formato de dinero. Funciones puras, sin Unity, para probarlas en EditMode. La localización completa llegará en otra tarjeta.
    /// </summary>
    public static class CityText
    {
        private static readonly string[] MilestoneNames =
        {
            "Asentamiento", "Aldea", "Pueblo", "Villa", "Ciudad pequeña", "Ciudad", "Ciudad grande", "Gran ciudad", "Capital", "Metrópolis", "Megalópolis",
        };

        private static readonly string[] Days = { "Lun", "Mar", "Mié", "Jue", "Vie", "Sáb", "Dom" };

        public static string MilestoneName(int index) => MilestoneNames[index < 0 ? 0 : (index >= MilestoneNames.Length ? MilestoneNames.Length - 1 : index)];

        /// <summary>Dinero con puntos de millar: 70000 es "70.000".</summary>
        public static string Money(long v)
        {
            bool neg = v < 0;
            string digits = (neg ? -v : v).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            for (int i = 0; i < digits.Length; i++)
            {
                if (i > 0 && (digits.Length - i) % 3 == 0) sb.Append('.');
                sb.Append(digits[i]);
            }
            return neg ? "-" + sb : sb.ToString();
        }

        public static string SignedMoney(long v) => (v >= 0 ? "+" : "") + Money(v);

        /// <summary>Habitantes con puntos de millar, y "k" a partir de 100.000.</summary>
        public static string Population(int v) => Money(v);

        public static string DayName(int dayOfWeek) => Days[((dayOfWeek % 7) + 7) % 7];

        public static string Clock(CityClock c)
        {
            int h = (int)c.Hour;
            int m = (int)((c.Hour - h) * 60f);
            return DayName(c.DayOfWeek) + " " + h.ToString("00") + ":" + m.ToString("00");
        }

        public static string DateLine(CityClock c) => "Año " + (c.Year + 1) + " · sem " + (c.Week % CityClock.WeeksPerYear + 1);

        public static string ZoneName(ZoneType z)
        {
            switch (z)
            {
                case ZoneType.Residential: return "Residencial";
                case ZoneType.Commercial: return "Comercial";
                case ZoneType.Industrial: return "Industrial";
                case ZoneType.Office: return "Oficinas";
                default: return "Sin zona";
            }
        }

        public static string DensityName(int density) => density <= 1 ? "baja" : (density == 2 ? "media" : "alta");

        public static string ServiceName(ServiceType t)
        {
            switch (t)
            {
                case ServiceType.WaterPump: return "Bomba de agua";
                case ServiceType.SewageOutlet: return "Desagüe";
                case ServiceType.WindTurbine: return "Turbina eólica";
                case ServiceType.CoalPlant: return "Central de carbón";
                case ServiceType.Landfill: return "Vertedero";
                case ServiceType.HealthCenter: return "Centro de salud";
                case ServiceType.FireStation: return "Bomberos";
                case ServiceType.PoliceStation: return "Policía";
                case ServiceType.School: return "Colegio";
                case ServiceType.HighSchool: return "Instituto";
                case ServiceType.Park: return "Parque";
                default: return "Servicio";
            }
        }

        /// <summary>Qué hace un servicio, en una frase.</summary>
        public static string ServiceEffect(ServiceType t)
        {
            switch (t)
            {
                case ServiceType.WaterPump: return "da agua a las calles conectadas";
                case ServiceType.SewageOutlet: return "recoge las aguas residuales";
                case ServiceType.WindTurbine: return "da electricidad (poca)";
                case ServiceType.CoalPlant: return "da mucha electricidad y contamina";
                case ServiceType.Landfill: return "recoge la basura";
                case ServiceType.HealthCenter: return "cubre la sanidad";
                case ServiceType.FireStation: return "cubre los incendios";
                case ServiceType.PoliceStation: return "cubre la seguridad";
                case ServiceType.School: return "educa a los niños";
                case ServiceType.HighSchool: return "educa a los jóvenes";
                case ServiceType.Park: return "sube el valor del suelo";
                default: return "";
            }
        }

        public static string Requirement(int milestone, CityBalance bal)
        {
            int pop = bal.MilestonePopulation[milestone < 0 ? 0 : (milestone >= bal.MilestonePopulation.Length ? bal.MilestonePopulation.Length - 1 : milestone)];
            return "requiere " + MilestoneName(milestone) + " (" + Money(pop) + " habitantes)";
        }

        public static string ShortRequirement(int milestone, CityBalance bal)
            => Money(bal.MilestonePopulation[milestone < 0 ? 0 : (milestone >= bal.MilestonePopulation.Length ? bal.MilestonePopulation.Length - 1 : milestone)]) + " hab.";

        /// <summary>Motivo de rechazo de una orden de la ciudad en texto para el jugador.</summary>
        public static string Reject(CityResult r, CitySimulation city, ValidationResult road = ValidationResult.Ok)
        {
            switch (r)
            {
                case CityResult.OutOfBounds: return "Fuera del mapa.";
                case CityResult.NotUnlocked: return "Todavía bloqueado: sigue creciendo.";
                case CityResult.InsufficientFunds: return "No tienes dinero suficiente.";
                case CityResult.Overlaps: return "No cabe: choca con una calle o con otro edificio.";
                case CityResult.NoRoadAccess: return "Tiene que estar junto a una calle (a menos de 60 m).";
                case CityResult.InvalidTarget: return "No hay nada que hacer ahí.";
                case CityResult.InvalidArgument: return "Orden no válida.";
                case CityResult.RoadRejected: return Tools.RoadTool.RoadToolModel.Reason(road);
                default: return "";
            }
        }

        /// <summary>Texto de unos problemas de edificio (uno por línea) con <see cref="BuildingRules.Describe"/>.</summary>
        public static string Problems(BuildingProblems p)
        {
            if (p == BuildingProblems.None) return "";
            var sb = new StringBuilder();
            for (int bit = 1; bit <= (int)BuildingProblems.Polluted; bit <<= 1)
            {
                var f = (BuildingProblems)bit;
                if ((p & f) == 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("• ").Append(BuildingRules.Describe(f));
            }
            return sb.ToString();
        }
    }
}
