using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Urbe.Game.Rendering.Roads;
using Urbe.Sim.Roads;

namespace Urbe.Game.Editor
{
    /// <summary>
    /// Genera el atlas de carreteras (ADR 003) por código, sin assets externos: una columna por tipo con su sección pintada a lo ancho
    /// (acera con losas, bordillo, asfalto, marcas viales, mediana) y columnas lisas para acera, asfalto, bordillo, césped y pintura.
    /// Las marcas discontinuas se repiten cada <see cref="RoadAtlasLayout.TileMeters"/> m en v, así que no hay costuras.
    /// También crea el material <c>RoadSurface.mat</c> con el shader <c>Urbe/RoadSurface</c>.
    /// </summary>
    public static class RoadAtlasBuilder
    {
        public const string Folder = "Assets/_Project/Art/Roads";
        public const string AtlasPath = Folder + "/RoadAtlas.png";
        public const string ShaderPath = Folder + "/RoadSurface.shader";
        public const string MaterialPath = Folder + "/RoadSurface.mat";

        private static readonly Vector3 Asphalt = new Vector3(0.205f, 0.215f, 0.240f);
        private static readonly Vector3 Sidewalk = new Vector3(0.800f, 0.790f, 0.760f);
        private static readonly Vector3 CurbTop = new Vector3(0.905f, 0.895f, 0.865f);
        private static readonly Vector3 CurbFace = new Vector3(0.700f, 0.690f, 0.670f);
        private static readonly Vector3 Grass = new Vector3(0.420f, 0.640f, 0.320f);
        private static readonly Vector3 Concrete = new Vector3(0.640f, 0.640f, 0.620f);
        private static readonly Vector3 White = new Vector3(0.950f, 0.950f, 0.920f);
        private static readonly Vector3 Yellow = new Vector3(0.960f, 0.770f, 0.180f);

        /// <summary>Marca vial: línea longitudinal centrada en <c>Lateral</c>. <c>Dash</c> = 0 es continua; si no, longitud de la raya y periodo en m.</summary>
        private struct Line
        {
            public float Lateral, Width, DashOn, DashPeriod;
            public Vector3 Color;
        }

        public static Material EnsureAll()
        {
            Directory.CreateDirectory(Folder);
            BuildAtlas();
            return EnsureMaterial();
        }

        public static Material EnsureMaterial()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null) shader = Shader.Find("Urbe/RoadSurface");
            if (shader == null) throw new InvalidOperationException("No se encuentra el shader Urbe/RoadSurface");
            var m = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (m == null)
            {
                m = new Material(shader);
                AssetDatabase.CreateAsset(m, MaterialPath);
            }
            m.shader = shader;
            m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(AtlasPath));
            m.SetFloat("_TrafficLayer", 0f);
            m.enableInstancing = false;
            EditorUtility.SetDirty(m);
            AssetDatabase.SaveAssets();
            return m;
        }

        public static void BuildAtlas()
        {
            int w = RoadAtlasLayout.Width, h = RoadAtlasLayout.Height;
            var px = new Color32[w * h];
            RoadTypeCatalog catalog = RoadTypeCatalog.CreateDefault();

            for (int col = 0; col < RoadAtlasLayout.ColumnCount; col++)
            {
                int x0 = RoadAtlasLayout.ColumnX0(col);
                int cw = RoadAtlasLayout.ColumnWidth(col);
                if (col < RoadAtlasLayout.TypeColumns) PaintType(px, x0, cw, h, new RoadProfile(catalog.Get((ushort)col)), catalog.Get((ushort)col));
                else PaintFlat(px, x0, cw, h, col);
            }
            // Margen sobrante (a la derecha de la última columna): asfalto.
            int used = RoadAtlasLayout.ColumnX0(RoadAtlasLayout.ColumnCount - 1) + RoadAtlasLayout.FlatColumnWidth;
            for (int y = 0; y < h; y++)
                for (int x = used; x < w; x++) px[y * w + x] = ToColor(Asphalt);

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes(AtlasPath, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(AtlasPath, ImportAssetOptions.ForceSynchronousImport);
            var imp = (TextureImporter)AssetImporter.GetAtPath(AtlasPath);
            imp.textureType = TextureImporterType.Default;
            imp.sRGBTexture = true;
            imp.mipmapEnabled = true;
            imp.wrapMode = TextureWrapMode.Repeat;
            imp.filterMode = FilterMode.Trilinear;
            imp.anisoLevel = 8;
            imp.npotScale = TextureImporterNPOTScale.None;
            imp.textureCompression = TextureImporterCompression.Uncompressed;
            imp.alphaSource = TextureImporterAlphaSource.None;
            imp.SaveAndReimport();
        }

        // ------------------------------------------------------------------ columnas de tipo

        private static void PaintType(Color32[] px, int x0, int cw, int h, in RoadProfile pr, RoadType type)
        {
            Line[] lines = MarkingsFor(type, pr);
            int usable = cw - 2 * RoadAtlasLayout.Inset;
            float total = pr.TotalWidth;
            float mPerPx = total / usable;
            float sPerPx = RoadAtlasLayout.TileMeters / h;
            Vector3 median = type.Id == RoadTypeIds.Highway4 ? Concrete : Grass;

            for (int y = 0; y < h; y++)
            {
                float s0 = y * sPerPx, s1 = (y + 1) * sPerPx;
                for (int x = 0; x < cw; x++)
                {
                    int ux = Mathf.Clamp(x - RoadAtlasLayout.Inset, 0, usable - 1);
                    float l0 = pr.LeftOuter + ux * mPerPx, l1 = l0 + mPerPx;
                    float lat = (l0 + l1) * 0.5f;
                    Vector3 c;
                    if (lat < pr.LeftCurb || lat > pr.RightCurb)
                    {
                        float edge = lat < pr.LeftCurb ? pr.LeftCurb - lat : lat - pr.RightCurb;
                        // Autopista y rural: arcén asfaltado (un poco más claro) en vez de acera.
                        c = pr.HasShoulder ? Shade(AsphaltColor(lat, x0 + x, y), 1.18f) : SidewalkColor(s0, x0 + x, y, edge);
                    }
                    else if (pr.HasMedian && lat >= pr.MedianL && lat <= pr.MedianR)
                    {
                        c = Shade(median, 1f + 0.05f * (Hash(x0 + x, y) - 0.5f));
                    }
                    else
                    {
                        c = AsphaltColor(lat, x0 + x, y);
                        // Bordes pálidos junto a los bordillos de la calzada (la mediana también tiene los suyos).
                    }
                    for (int k = 0; k < lines.Length; k++)
                    {
                        float cov = CoverageLine(lines[k], l0, l1, s0, s1);
                        if (cov > 0f) c = Vector3.Lerp(c, lines[k].Color, cov);
                    }
                    px[y * RoadAtlasLayout.Width + x0 + x] = ToColor(c);
                }
            }
        }

        private static Line[] MarkingsFor(RoadType t, in RoadProfile pr)
        {
            const float lw = 0.14f;
            switch (t.Id)
            {
                case RoadTypeIds.Street2:
                    return new[] { new Line { Lateral = 0f, Width = lw, DashOn = 3f, DashPeriod = 6f, Color = Yellow } };
                case RoadTypeIds.OneWay2:
                    return new[] { new Line { Lateral = 0f, Width = lw, DashOn = 3f, DashPeriod = 6f, Color = White } };
                case RoadTypeIds.Rural2:
                    return new[]
                    {
                        new Line { Lateral = -0.12f, Width = lw, Color = Yellow },
                        new Line { Lateral = 0.12f, Width = lw, Color = Yellow },
                        new Line { Lateral = pr.LeftCurb + 0.35f, Width = lw, Color = White },
                        new Line { Lateral = pr.RightCurb - 0.35f, Width = lw, Color = White },
                    };
                case RoadTypeIds.Highway4:
                {
                    float lane = t.LaneWidth;
                    float m = t.MedianWidth * 0.5f;
                    return new[]
                    {
                        new Line { Lateral = pr.LeftCurb + 0.35f, Width = 0.18f, Color = White },
                        new Line { Lateral = pr.RightCurb - 0.35f, Width = 0.18f, Color = White },
                        new Line { Lateral = -m - 0.35f, Width = 0.18f, Color = White },
                        new Line { Lateral = m + 0.35f, Width = 0.18f, Color = White },
                        new Line { Lateral = -(m + lane), Width = lw, DashOn = 3f, DashPeriod = 12f, Color = White },
                        new Line { Lateral = m + lane, Width = lw, DashOn = 3f, DashPeriod = 12f, Color = White },
                    };
                }
                default:
                {
                    // Avenidas: separadores discontinuos entre carriles del mismo sentido y línea continua junto a la mediana.
                    float m = t.MedianWidth * 0.5f;
                    var list = new System.Collections.Generic.List<Line>();
                    for (int k = 1; k < t.LanesForward; k++)
                    {
                        float off = m + k * t.LaneWidth;
                        list.Add(new Line { Lateral = off, Width = lw, DashOn = 3f, DashPeriod = 6f, Color = White });
                        list.Add(new Line { Lateral = -off, Width = lw, DashOn = 3f, DashPeriod = 6f, Color = White });
                    }
                    list.Add(new Line { Lateral = m + 0.30f, Width = lw, Color = White });
                    list.Add(new Line { Lateral = -m - 0.30f, Width = lw, Color = White });
                    return list.ToArray();
                }
            }
        }

        // Recubrimiento de un píxel (l0..l1 lateral, s0..s1 longitudinal) por una línea con o sin rayas.
        private static float CoverageLine(in Line ln, float l0, float l1, float s0, float s1)
        {
            float a = Overlap(l0, l1, ln.Lateral - ln.Width * 0.5f, ln.Lateral + ln.Width * 0.5f);
            if (a <= 0f) return 0f;
            if (ln.DashPeriod <= 0f) return a;
            float cov = 0f;
            int n = Mathf.RoundToInt(RoadAtlasLayout.TileMeters / ln.DashPeriod);
            for (int k = 0; k < n; k++)
            {
                float d0 = k * ln.DashPeriod + 0.5f;
                cov += Overlap(s0, s1, d0, d0 + ln.DashOn);
            }
            return a * Mathf.Clamp01(cov);
        }

        private static float Overlap(float a0, float a1, float b0, float b1)
        {
            float lo = Mathf.Max(a0, b0), hi = Mathf.Min(a1, b1);
            return hi > lo ? (hi - lo) / (a1 - a0) : 0f;
        }

        // ------------------------------------------------------------------ colores base

        private static Vector3 SidewalkColor(float s, int hx, int hy, float edge)
        {
            // Losas de 1,5 m a lo largo de la calle: junta fina y ligera variación de brillo por losa.
            float slab = Mathf.Floor(s / 1.5f);
            float inSlab = s - slab * 1.5f;
            float v = 1f + 0.04f * (Hash((int)slab * 7 + 3, 11) - 0.5f) + 0.03f * (Hash(hx, hy) - 0.5f);
            Vector3 c = Shade(Sidewalk, v);
            if (inSlab < 0.035f || inSlab > 1.5f - 0.035f) c = Shade(c, 0.90f);
            if (edge < 0.18f) c = CurbTop;
            return c;
        }

        private static Vector3 AsphaltColor(float lat, int hx, int hy)
        {
            float n = Hash(hx, hy) - 0.5f;
            float v = 1f + 0.07f * n;
            return Shade(Asphalt, v);
        }

        private static void PaintFlat(Color32[] px, int x0, int cw, int h, int col)
        {
            float sPerPx = RoadAtlasLayout.TileMeters / h;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < cw; x++)
                {
                    Vector3 c;
                    switch (col)
                    {
                        case RoadAtlasLayout.ColSidewalk: c = SidewalkColor((y + 0.5f) * sPerPx, x0 + x, y, 1f); break;
                        case RoadAtlasLayout.ColAsphalt: c = AsphaltColor(0f, x0 + x, y); break;
                        case RoadAtlasLayout.ColCurb: c = Shade(CurbFace, 1f + 0.04f * (Hash(x0 + x, y) - 0.5f)); break;
                        case RoadAtlasLayout.ColGrass: c = Shade(Grass, 1f + 0.08f * (Hash(x0 + x, y) - 0.5f)); break;
                        default: c = White; break;
                    }
                    px[y * RoadAtlasLayout.Width + x0 + x] = ToColor(c);
                }
            }
        }

        private static Vector3 Shade(Vector3 c, float k) => new Vector3(Mathf.Clamp01(c.x * k), Mathf.Clamp01(c.y * k), Mathf.Clamp01(c.z * k));

        private static Color32 ToColor(Vector3 c) =>
            new Color32((byte)Mathf.RoundToInt(Mathf.Clamp01(c.x) * 255f), (byte)Mathf.RoundToInt(Mathf.Clamp01(c.y) * 255f),
                (byte)Mathf.RoundToInt(Mathf.Clamp01(c.z) * 255f), 255);

        private static float Hash(int x, int y)
        {
            unchecked
            {
                uint h = (uint)x * 374761393u + (uint)y * 668265263u;
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }
    }
}
