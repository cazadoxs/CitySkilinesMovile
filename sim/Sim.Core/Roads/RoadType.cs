using System;

namespace Urbe.Sim.Roads
{
    /// <summary>Ids de los tipos por defecto de <see cref="RoadTypeCatalog.CreateDefault"/>.</summary>
    public static class RoadTypeIds
    {
        public const ushort Street2 = 0;
        public const ushort OneWay2 = 1;
        public const ushort Avenue4 = 2;
        public const ushort Avenue6 = 3;
        public const ushort Rural2 = 4;
        public const ushort Highway4 = 5;
    }

    /// <summary>Tipo de carretera (dato inmutable). Unidades: metros y m/s.</summary>
    public sealed class RoadType
    {
        public ushort Id { get; }
        public string Key { get; }
        public byte LanesForward { get; }
        public byte LanesBackward { get; }
        public float LaneWidth { get; }
        public float MedianWidth { get; }
        public float MarginWidth { get; }
        public float SpeedLimit { get; }
        public float MaxSlope { get; }
        public bool Zonable { get; }

        public RoadType(ushort id, string key, byte lanesForward, byte lanesBackward, float laneWidth, float medianWidth,
            float marginWidth, float speedLimit, float maxSlope, bool zonable)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (lanesForward + lanesBackward < 1 || lanesForward + lanesBackward > RoadNetwork.MaxLanesPerSegment)
                throw new ArgumentOutOfRangeException(nameof(lanesForward), "F+B debe estar entre 1 y 8");
            Id = id;
            Key = key;
            LanesForward = lanesForward;
            LanesBackward = lanesBackward;
            LaneWidth = laneWidth;
            MedianWidth = medianWidth;
            MarginWidth = marginWidth;
            SpeedLimit = speedLimit;
            MaxSlope = maxSlope;
            Zonable = zonable;
        }

        public bool IsOneWay => LanesBackward == 0;

        public float TotalWidth => 2f * MarginWidth + MedianWidth + (LanesForward + LanesBackward) * LaneWidth;
    }

    /// <summary>Tabla de tipos; el indice en el array es el <see cref="RoadType.Id"/>.</summary>
    public sealed class RoadTypeCatalog
    {
        private readonly RoadType[] _types;

        public RoadTypeCatalog(RoadType[] types)
        {
            if (types == null) throw new ArgumentNullException(nameof(types));
            _types = (RoadType[])types.Clone();
            for (int i = 0; i < _types.Length; i++)
            {
                if (_types[i] == null || _types[i].Id != i)
                    throw new ArgumentException("El tipo en la posicion " + i + " debe tener Id == " + i, nameof(types));
            }
        }

        public int Count => _types.Length;

        public bool IsValidId(ushort id) => id < _types.Length;

        public RoadType Get(ushort id) => _types[id];

        /// <summary>Busca por clave; devuelve -1 si no existe.</summary>
        public int IndexOfKey(string key)
        {
            for (int i = 0; i < _types.Length; i++)
                if (_types[i].Key == key) return i;
            return -1;
        }

        private const float KmhToMs = 1f / 3.6f;

        /// <summary>Los 6 tipos de ADR 001 (km/h se convierten a m/s aqui).</summary>
        public static RoadTypeCatalog CreateDefault()
        {
            return new RoadTypeCatalog(new[]
            {
                new RoadType(RoadTypeIds.Street2, "street_2", 1, 1, 3.0f, 0f, 2.0f, 40f * KmhToMs, 0.12f, true),
                new RoadType(RoadTypeIds.OneWay2, "oneway_2", 2, 0, 3.0f, 0f, 2.0f, 40f * KmhToMs, 0.12f, true),
                new RoadType(RoadTypeIds.Avenue4, "avenue_4", 2, 2, 3.25f, 1.0f, 2.5f, 50f * KmhToMs, 0.10f, true),
                new RoadType(RoadTypeIds.Avenue6, "avenue_6", 3, 3, 3.5f, 2.0f, 3.0f, 60f * KmhToMs, 0.08f, true),
                new RoadType(RoadTypeIds.Rural2, "rural_2", 1, 1, 3.5f, 0f, 1.5f, 80f * KmhToMs, 0.12f, false),
                new RoadType(RoadTypeIds.Highway4, "highway_4", 2, 2, 3.75f, 3.0f, 2.5f, 100f * KmhToMs, 0.06f, false),
            });
        }
    }
}
