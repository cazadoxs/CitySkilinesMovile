using System;
using UnityEngine;

namespace Urbe.Game.Rendering.Vehicles
{
    /// <summary>
    /// Mallas de los 10 vehículos con sus dos LOD (M2-07). Se genera con <c>VehicleAssetBuilder</c> a partir de Vehicles.fbx; el orden es el de
    /// <see cref="VehicleModelIds"/>. Las mallas miden metros reales, origen en el centro del vehículo y el suelo, frente hacia +Z.
    /// </summary>
    [CreateAssetMenu(menuName = "URBE/Conjunto de vehículos")]
    public sealed class VehicleModelSet : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            public string name;
            public Mesh lod0;
            public Mesh lod1;
        }

        public Entry[] entries = Array.Empty<Entry>();
    }

    /// <summary>Índices de modelo en <see cref="VehicleModelSet.entries"/> y su correspondencia con los tipos del núcleo.</summary>
    public static class VehicleModelIds
    {
        public const int CarSmall = 0, CarSedan = 1, CarSuv = 2, Van = 3, Truck = 4, Bus = 5, Taxi = 6, Ambulance = 7, Garbage = 8, Sport = 9;
        public const int Count = 10;
        public static readonly string[] Names =
            { "car_small", "car_sedan", "car_suv", "van", "truck", "bus", "taxi", "ambulance", "garbage", "sport" };

        // Ids de tipo del núcleo (Sim.Core/Vehicles/VehicleType.cs): 0 = coche, 1 = camión.
        private const byte TypeCar = 0, TypeTruck = 1;

        /// <summary>
        /// Elige modelo para un vehículo del núcleo. Hoy el núcleo solo emite coche y camión: coche = pequeño 35 % / sedán 45 % / SUV 20 %
        /// (según el id, estable); camión = camión. Se limita a 4 modelos para que el peor caso sean 4 modelos x 2 LOD = 8 draw calls (&lt; 10).
        /// Furgoneta, autobús, taxi, ambulancia, recogida de basura y deportivo ya están modelados y se activan al añadir esos tipos al núcleo.
        /// </summary>
        public static int Pick(byte type, int id)
        {
            if (type == TypeTruck) return Truck;
            if (type != TypeCar) return CarSedan;
            uint h = (uint)id * 2654435761u;
            int r = (int)((h >> 8) % 100u);
            return r < 35 ? CarSmall : r < 80 ? CarSedan : CarSuv;
        }

        /// <summary>Modelos de pintura fija (el tinte por instancia no se aplica: el atlas ya los colorea).</summary>
        public static bool IsFixedColor(int model) => model == Taxi || model == Ambulance || model == Garbage;
    }
}
