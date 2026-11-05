using System;

namespace Urbe.Sim.City
{
    /// <summary>Tipo de zona pintable (GDD 4.4). La densidad va aparte (1 = baja, 2 = media, 3 = alta).</summary>
    public enum ZoneType : byte { None = 0, Residential = 1, Commercial = 2, Industrial = 3, Office = 4 }

    /// <summary>Resultado de una orden del jugador sobre la ciudad. Ok = 0; el resto es el motivo del rechazo.</summary>
    public enum CityResult : byte
    {
        Ok = 0,
        InvalidArgument,
        OutOfBounds,
        /// <summary>El elemento aun no esta desbloqueado por el hito de poblacion (GDD 3.3).</summary>
        NotUnlocked,
        InsufficientFunds,
        /// <summary>La huella choca con una calzada o con otro edificio.</summary>
        Overlaps,
        /// <summary>No hay una carretera lo bastante cerca para enganchar el edificio.</summary>
        NoRoadAccess,
        InvalidTarget,
        /// <summary>La red viaria rechazo la orden; el motivo esta en <see cref="CityCommand.RoadResult"/>.</summary>
        RoadRejected,
    }

    /// <summary>Utilidades de hash de estado (solo enteros, sin dependencia de cultura ni de formato).</summary>
    public static class HashUtil
    {
        public static ulong Mix(ulong h, ulong x)
        {
            unchecked
            {
                ulong z = (h ^ x) + 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        public static ulong MixF(ulong h, float v) => Mix(h, (uint)BitConverter.SingleToInt32Bits(v));
    }
}

namespace Urbe.Sim.City
{
    /// <summary>Categorias de gasto (informe semanal de GDD 4.10).</summary>
    public enum CityExpense : byte { Construction = 0, Maintenance = 1, Services = 2 }

    /// <summary>Categorias de ingreso: impuestos por zona.</summary>
    public enum CityIncome : byte { TaxResidential = 0, TaxCommercial = 1, TaxIndustrial = 2, TaxOffice = 3, Other = 4 }
}
