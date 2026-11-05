using System;
using UnityEngine;
using Urbe.Game.Sim;

namespace Urbe.Game.Rendering.Vehicles
{
    /// <summary>
    /// Reparte los vehículos interpolados en cubos (modelo x LOD): matriz de mundo y tinte por instancia, con descarte por frustum.
    /// Es común a los dos caminos de dibujo (RenderMeshInstanced y BatchRendererGroup) para que la comparación sea justa.
    /// Los arrays crecen por duplicación solo si hace falta; en régimen estable no asigna memoria.
    /// </summary>
    public sealed class VehicleBatcher
    {
        public const int LodCount = 2;
        public const float YLift = 0.02f;

        private static readonly Color32[] Palette =
        {
            new Color32(0xE8, 0xE8, 0xE4, 255), new Color32(0x2B, 0x2F, 0x38, 255), new Color32(0xC9, 0x3A, 0x3A, 255),
            new Color32(0x2F, 0x6F, 0xC9, 255), new Color32(0x7A, 0x85, 0x92, 255), new Color32(0xE0, 0xA1, 0x2A, 255),
            new Color32(0x3F, 0x9A, 0x5B, 255), new Color32(0x9A, 0x4F, 0xB0, 255), new Color32(0xD9, 0x6C, 0x2F, 255),
            new Color32(0x5B, 0xB6, 0xC9, 255),
        };

        private static readonly Vector4[] PaletteLinear = BuildLinearPalette();

        public readonly int ModelCount;
        public readonly Matrix4x4[][] Matrices;
        public readonly Vector4[][] Tints;
        public readonly int[] Counts;
        private readonly float[] _length;
        private readonly Plane[] _planes = new Plane[6];
        public float LodDistance = 120f;
        public int Visible { get; private set; }
        public int Culled { get; private set; }

        public int BucketCount => ModelCount * LodCount;

        public VehicleBatcher(VehicleModelSet set)
        {
            ModelCount = set.entries.Length;
            int b = ModelCount * LodCount;
            Matrices = new Matrix4x4[b][];
            Tints = new Vector4[b][];
            Counts = new int[b];
            _length = new float[ModelCount];
            for (int m = 0; m < ModelCount; m++)
            {
                _length[m] = set.entries[m].lod0 != null ? set.entries[m].lod0.bounds.size.z : 4.5f;
                for (int l = 0; l < LodCount; l++)
                {
                    Matrices[m * LodCount + l] = new Matrix4x4[256];
                    Tints[m * LodCount + l] = new Vector4[256];
                }
            }
        }

        private static Vector4[] BuildLinearPalette()
        {
            var p = new Vector4[Palette.Length];
            for (int i = 0; i < p.Length; i++)
            {
                Color c = ((Color)Palette[i]).linear;
                p[i] = new Vector4(c.r, c.g, c.b, 1f);
            }
            return p;
        }

        public static Vector4 TintFor(int id)
        {
            uint h = (uint)id * 2246822519u + 374761393u;
            return PaletteLinear[(int)((h >> 12) % (uint)PaletteLinear.Length)];
        }

        /// <summary>Rellena los cubos. <paramref name="cam"/> puede ser null (sin descarte y todo en LOD0).</summary>
        public void Build(InterpolatedVehicle[] v, int n, Camera? cam)
        {
            Array.Clear(Counts, 0, Counts.Length);
            bool cull = cam != null;
            Vector3 cp = default;
            if (cam != null)
            {
                GeometryUtility.CalculateFrustumPlanes(cam, _planes);
                cp = cam.transform.position;
            }
            float lodSq = LodDistance * LodDistance;
            int visible = 0;
            for (int i = 0; i < n; i++)
            {
                ref InterpolatedVehicle q = ref v[i];
                int model = VehicleModelIds.Pick(q.Type, q.Id);
                if (model >= ModelCount) model = 0;
                float len = _length[model];
                float cx = q.X - q.DirX * len * 0.5f;
                float cz = q.Z - q.DirZ * len * 0.5f;
                float cy = q.Y + YLift;
                int lod = 0;
                if (cull)
                {
                    float r = len * 0.5f + 1f;
                    bool outside = false;
                    for (int p = 0; p < 6; p++)
                    {
                        Plane pl = _planes[p];
                        if (pl.normal.x * cx + pl.normal.y * cy + pl.normal.z * cz + pl.distance < -r) { outside = true; break; }
                    }
                    if (outside) continue;
                    float dx = cx - cp.x, dy = cy - cp.y, dz = cz - cp.z;
                    if (dx * dx + dy * dy + dz * dz > lodSq) lod = 1;
                }
                int b = model * LodCount + lod;
                int c = Counts[b];
                if (c == Matrices[b].Length)
                {
                    Array.Resize(ref Matrices[b], c * 2);
                    Array.Resize(ref Tints[b], c * 2);
                }
                // Giro sobre Y con sen = DirX, cos = DirZ (el frente del modelo es +Z).
                Matrix4x4 m = default;
                m.m00 = q.DirZ; m.m02 = q.DirX; m.m03 = cx;
                m.m11 = 1f; m.m13 = cy;
                m.m20 = -q.DirX; m.m22 = q.DirZ; m.m23 = cz;
                m.m33 = 1f;
                Matrices[b][c] = m;
                Tints[b][c] = TintFor(q.Id);
                Counts[b] = c + 1;
                visible++;
            }
            Visible = visible;
            Culled = n - visible;
        }
    }
}
