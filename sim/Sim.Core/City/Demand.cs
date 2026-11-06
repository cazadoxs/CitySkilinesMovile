using System;

namespace Urbe.Sim.City
{
    /// <summary>Demanda RCI+O de -100 a +100 (GDD 8.12), la que muestran las barras de la interfaz.</summary>
    public struct DemandState
    {
        public float R;
        public float C;
        public float I;
        public float O;

        public float Get(ZoneType zone)
        {
            switch (zone)
            {
                case ZoneType.Residential: return R;
                case ZoneType.Commercial: return C;
                case ZoneType.Industrial: return I;
                case ZoneType.Office: return O;
                default: return 0f;
            }
        }
    }

    /// <summary>Totales de la ciudad que recalcula un barrido periodico (poblacion, vivienda, empleo). Los lee la interfaz y la demanda.</summary>
    public sealed class CityStats
    {
        public int Population;
        public int Children;
        public int Adults;
        public int Seniors;
        public int Households;
        public int HousingCapacity;
        public int HousingVacant;
        public int JobsLow, JobsHigh, FilledLow, FilledHigh;
        public int CommercialJobs, IndustrialJobs, OfficeJobs;
        /// <summary>Adultos sin trabajo con educacion baja (0-1) y alta (2-3).</summary>
        public int UnemployedLow, UnemployedHigh;
        public int Buildings, AbandonedBuildings;
        public float AverageHappiness = 60f;
        public float AverageEducation;
        public int Students;
        public int Unemployed => UnemployedLow + UnemployedHigh;
        public int JobsFree => JobsLow + JobsHigh - FilledLow - FilledHigh;
        public int Jobs => JobsLow + JobsHigh;
    }

    /// <summary>
    /// Formulas de demanda (GDD 8.12). Cada demanda va de -100 a +100; se suaviza para que las barras no salten. Los coeficientes
    /// estan en <see cref="CityBalance"/>. Funcion pura de los totales para poder testearla.
    /// </summary>
    public static class DemandModel
    {
        public static DemandState Compute(CityBalance bal, CityStats s, float taxR, float taxC, float taxI, float taxO)
        {
            float pop = s.Population;
            var d = new DemandState();

            // Residencial: empleos libres sin trabajadores, atractivo, felicidad e impuestos; frena si sobra vivienda.
            float vacantFrac = s.HousingCapacity > 0 ? (float)s.HousingVacant / s.HousingCapacity : 0f;
            float jobsGap = s.JobsFree - s.Unemployed;
            float r = bal.DemandBaseR;
            r += 55f * Clamp(jobsGap / Math.Max(20f, 0.15f * pop), -1f, 1f);
            r -= 80f * Clamp((vacantFrac - 0.15f) / 0.35f, 0f, 1f);
            if (pop > 0f) r += (s.AverageHappiness - 55f) * 0.6f;
            r -= (taxR - 9f) * 6f;
            d.R = Clamp(r, -100f, 100f);

            // Comercial: clientes (poblacion) frente a empleos comerciales existentes.
            float needC = pop * bal.CommercialJobsPerResident;
            float c = 100f * (needC + bal.DemandBaseC - s.CommercialJobs) / (needC + 25f);
            c -= (taxC - 9f) * 6f;
            d.C = Clamp(c, -100f, 100f);

            // Industrial: parados poco cualificados que pueden trabajar, mercancias que piden los vecinos y la exportacion.
            float needI = pop * bal.IndustrialJobsPerResident;
            float i = bal.DemandBaseI;
            i += 60f * Clamp((s.UnemployedLow - (s.JobsLow - s.FilledLow)) / Math.Max(10f, 0.08f * pop + 10f), -1f, 1f);
            i += 40f * Clamp((needI + 20f - s.IndustrialJobs) / (needI + 40f), -1f, 1f);
            i -= (taxI - 9f) * 6f;
            d.I = Clamp(i, -100f, 100f);

            // Oficinas: universitarios o titulados sin trabajo cualificado.
            float o = 100f * Clamp((s.UnemployedHigh - (s.JobsHigh - s.FilledHigh)) / Math.Max(12f, 0.04f * pop + 12f), -1f, 1f);
            o -= (taxO - 9f) * 6f;
            d.O = Clamp(o, -100f, 100f);
            return d;
        }

        public static float Smooth(float old, float target, float k) => old + (target - old) * k;

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
