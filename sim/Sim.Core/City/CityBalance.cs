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

        public int RoadCost(ushort roadType, float length)
        {
            int idx = roadType < RoadCostPerMeter.Length ? roadType : 0;
            return (int)Math.Ceiling(RoadCostPerMeter[idx] * length);
        }
    }
}
