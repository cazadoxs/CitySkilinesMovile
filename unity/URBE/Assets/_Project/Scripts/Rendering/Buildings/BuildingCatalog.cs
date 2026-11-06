using System;
using System.Collections.Generic;
using UnityEngine;
using Urbe.Sim.City;

namespace Urbe.Game.Rendering.Buildings
{
    /// <summary>
    /// Catálogo de los 115 modelos de edificio (M3-09): zona, densidad, nivel, huella en celdas, tamaño en metros y las mallas LOD0 y LOD1.
    /// Lo genera <c>BuildingAssetBuilder</c> a partir de Art/Buildings/Buildings.manifest.json y los FBX; no se edita a mano.
    /// Las mallas miden metros reales, origen en el centro de la base, frente hacia +Z.
    /// </summary>
    [CreateAssetMenu(menuName = "URBE/Catálogo de edificios")]
    public sealed class BuildingCatalog : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            public string id;
            /// <summary>"R", "C", "I", "O" o "SVC" (servicio).</summary>
            public string zone;
            /// <summary>1 baja, 2 media, 3 alta; 0 en servicios.</summary>
            public int density;
            /// <summary>1 a 5; 0 en servicios.</summary>
            public int level;
            public int footprintW, footprintD;
            public Vector3 size;
            public Mesh lod0;
            public Mesh lod1;
            public string variant;
        }

        public Entry[] entries = Array.Empty<Entry>();
    }

    /// <summary>
    /// Elige el modelo de cada edificio de la simulación (M3-09). Sustituye a la gramática de piezas del ADR 005: en vez de componer
    /// piezas, se escoge del catálogo el modelo más próximo a zona, densidad, nivel y parcela, y a igualdad de puntuación decide la semilla
    /// del edificio (misma semilla, mismo modelo). Funciones puras, sin dibujar, para probarlas en EditMode.
    /// </summary>
    public sealed class BuildingModelPicker
    {
        private readonly BuildingCatalog _catalog;
        private readonly List<int>[] _byZone = new List<int>[5];
        private readonly Dictionary<string, int> _service = new Dictionary<string, int>();

        public BuildingModelPicker(BuildingCatalog catalog)
        {
            _catalog = catalog;
            for (int i = 0; i < _byZone.Length; i++) _byZone[i] = new List<int>();
            for (int i = 0; i < catalog.entries.Length; i++)
            {
                ref readonly BuildingCatalog.Entry e = ref catalog.entries[i];
                if (e.zone == "SVC") _service[e.id.StartsWith("Svc_", StringComparison.Ordinal) ? e.id.Substring(4) : e.id] = i;
                else
                {
                    int z = ZoneIndex(e.zone);
                    if (z > 0) _byZone[z].Add(i);
                }
            }
        }

        public int Count => _catalog.entries.Length;
        public BuildingCatalog.Entry Entry(int index) => _catalog.entries[index];

        public static int ZoneIndex(string zone)
        {
            switch (zone)
            {
                case "R": return (int)ZoneType.Residential;
                case "C": return (int)ZoneType.Commercial;
                case "I": return (int)ZoneType.Industrial;
                case "O": return (int)ZoneType.Office;
                default: return 0;
            }
        }

        /// <summary>Nombre del modelo de servicio (tras "Svc_") de cada tipo de servicio del núcleo.</summary>
        public static string ServiceKey(ServiceType t)
        {
            switch (t)
            {
                case ServiceType.WaterPump: return "water_pump";
                case ServiceType.SewageOutlet: return "drain";
                case ServiceType.WindTurbine: return "wind";
                case ServiceType.CoalPlant: return "coal";
                case ServiceType.Landfill: return "landfill";
                case ServiceType.HealthCenter: return "health";
                case ServiceType.FireStation: return "fire";
                case ServiceType.PoliceStation: return "police";
                case ServiceType.School:
                case ServiceType.HighSchool: return "school";
                case ServiceType.Park: return "park";
                default: return "";
            }
        }

        /// <summary>Índice del modelo de un servicio, o -1 si no hay.</summary>
        public int PickService(ServiceType t) => _service.TryGetValue(ServiceKey(t), out int i) ? i : -1;

        /// <summary>
        /// Modelo para un edificio de zona: puntuación = 100 x diferencia de densidad + 10 x diferencia de nivel + diferencia de huella en
        /// celdas; entre los de menor puntuación decide <paramref name="seed"/>. -1 si el catálogo no tiene la zona.
        /// </summary>
        public int PickZoned(ZoneType zone, int density, int level, int cellsW, int cellsD, uint seed)
        {
            List<int> list = _byZone[(int)zone];
            if (list.Count == 0) return -1;
            int best = int.MaxValue, bestCount = 0;
            for (int k = 0; k < list.Count; k++)
            {
                int s = Score(_catalog.entries[list[k]], density, level, cellsW, cellsD);
                if (s < best) { best = s; bestCount = 1; }
                else if (s == best) bestCount++;
            }
            uint h = seed * 2654435761u + 0x9E3779B9u;
            int pick = (int)((h >> 8) % (uint)bestCount);
            for (int k = 0; k < list.Count; k++)
            {
                if (Score(_catalog.entries[list[k]], density, level, cellsW, cellsD) != best) continue;
                if (pick-- == 0) return list[k];
            }
            return list[0];
        }

        private static int Score(in BuildingCatalog.Entry e, int density, int level, int w, int d)
            => Math.Abs(e.density - density) * 100 + Math.Abs(e.level - level) * 10 + Math.Abs(e.footprintW - w) + Math.Abs(e.footprintD - d);

        /// <summary>
        /// Escala del modelo para llenar la parcela (con 1 m de margen por lado) dentro de unos límites para no deformar. Los servicios
        /// escalan uniformes; los edificios de zona estiran ancho y fondo por separado y la altura sigue su media.
        /// </summary>
        public static Vector3 FitScale(in BuildingCatalog.Entry e, int cellsW, int cellsD, bool service)
        {
            float pw = cellsW * 8f - 2f, pd = cellsD * 8f - 2f;
            float sw = e.size.x > 0.1f ? pw / e.size.x : 1f, sd = e.size.z > 0.1f ? pd / e.size.z : 1f;
            if (service)
            {
                float u = Mathf.Clamp(Mathf.Min(sw, sd), 0.75f, 1.6f);
                return new Vector3(u, u, u);
            }
            sw = Mathf.Clamp(sw, 0.8f, 1.35f);
            sd = Mathf.Clamp(sd, 0.8f, 1.35f);
            float sy = Mathf.Clamp((sw + sd) * 0.5f, 0.85f, 1.25f);
            return new Vector3(sw, sy, sd);
        }

        private static readonly Color32[][] Palettes =
        {
            new Color32[0],
            // residencial: pasteles cálidos y suaves
            new[] { new Color32(0xE9, 0xB8, 0x8E, 255), new Color32(0xE8, 0xD2, 0x9A, 255), new Color32(0xD9, 0x9A, 0x8C, 255), new Color32(0xB9, 0xCF, 0x9E, 255), new Color32(0xE4, 0xC6, 0xC0, 255), new Color32(0xC9, 0xB9, 0xDA, 255), new Color32(0xF0, 0xE4, 0xC8, 255), new Color32(0xA9, 0xC9, 0xD6, 255) },
            // comercial: colores vivos pero limpios
            new[] { new Color32(0x7F, 0xB3, 0xE6, 255), new Color32(0xF0, 0xC0, 0x5A, 255), new Color32(0xE8, 0x8A, 0x78, 255), new Color32(0x8F, 0xD0, 0xB0, 255), new Color32(0xE6, 0xE0, 0xD0, 255), new Color32(0xC8, 0x9C, 0xD8, 255) },
            // industrial: grises y ocres sobrios
            new[] { new Color32(0xB8, 0xBC, 0xC0, 255), new Color32(0xC9, 0xB4, 0x8A, 255), new Color32(0x9F, 0xAE, 0xB8, 255), new Color32(0xD0, 0xA0, 0x7C, 255), new Color32(0xA8, 0xB8, 0x9C, 255) },
            // oficinas: azules y neutros
            new[] { new Color32(0x9F, 0xC4, 0xE6, 255), new Color32(0xC9, 0xD3, 0xDC, 255), new Color32(0x8F, 0xAE, 0xCF, 255), new Color32(0xB5, 0xD6, 0xD0, 255), new Color32(0xE0, 0xE4, 0xE8, 255) },
        };

        /// <summary>Tinte (lineal, alfa 1) de un edificio según su zona y semilla; el alfa del atlas decide qué parte se tiñe.</summary>
        public static Vector4 TintFor(ZoneType zone, uint seed)
        {
            int zi = (int)zone > 0 && (int)zone < Palettes.Length ? (int)zone : 1;
            Color32[] p = Palettes[zi];
            uint h = seed * 2246822519u + 374761393u;
            Color c = ((Color)p[(int)((h >> 12) % (uint)p.Length)]).linear;
            return new Vector4(c.r, c.g, c.b, 1f);
        }

        /// <summary>Tinte de un edificio abandonado: la alfa 0 oscurece todo el modelo (ver el shader Urbe/Vehicle).</summary>
        public static Vector4 AbandonedTint => new Vector4(0.35f, 0.33f, 0.32f, 0f);
    }
}
