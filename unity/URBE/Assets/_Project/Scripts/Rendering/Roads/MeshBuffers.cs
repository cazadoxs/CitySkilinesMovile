using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Urbe.Game.Rendering.Roads
{
    /// <summary>Vértice compacto de carretera (ADR 003): posición float3, normal SNorm8x4 (w = máscara de calzada), uv0 float2, uv1 float (segmento).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RoadVertex
    {
        public Vector3 Position;
        public sbyte Nx, Ny, Nz, Nw;
        public Vector2 Uv;
        public float Segment;
    }

    /// <summary>
    /// Buffers reutilizables donde escribe la geometría (C# puro, sin Mesh ni GameObject). Crecen por duplicación; <see cref="Clear"/>
    /// no libera memoria. Los triángulos se orientan solos hacia la normal pedida, así que ningún llamador tiene que razonar sobre el orden.
    /// </summary>
    public sealed class MeshBuffers
    {
        public RoadVertex[] Vertices = new RoadVertex[256];
        public int VertexCount;
        public int[] Indices = new int[768];
        public int IndexCount;
        public Vector3 BoundsMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        public Vector3 BoundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);

        public int TriangleCount => IndexCount / 3;

        public void Clear()
        {
            VertexCount = 0;
            IndexCount = 0;
            BoundsMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            BoundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        }

        /// <summary>Máscara de calzada que viaja en normal.w: la capa de tráfico solo tiñe lo que tenga 1.</summary>
        public const sbyte RoadMask = 127;

        public int AddVertex(Vector3 pos, Vector3 normal, sbyte mask, float u, float v, float segment)
        {
            if (VertexCount == Vertices.Length) Array.Resize(ref Vertices, VertexCount * 2);
            ref RoadVertex r = ref Vertices[VertexCount];
            r.Position = pos;
            r.Nx = ToSnorm(normal.x);
            r.Ny = ToSnorm(normal.y);
            r.Nz = ToSnorm(normal.z);
            r.Nw = mask;
            r.Uv = new Vector2(u, v);
            r.Segment = segment;
            if (pos.x < BoundsMin.x) BoundsMin.x = pos.x;
            if (pos.y < BoundsMin.y) BoundsMin.y = pos.y;
            if (pos.z < BoundsMin.z) BoundsMin.z = pos.z;
            if (pos.x > BoundsMax.x) BoundsMax.x = pos.x;
            if (pos.y > BoundsMax.y) BoundsMax.y = pos.y;
            if (pos.z > BoundsMax.z) BoundsMax.z = pos.z;
            return VertexCount++;
        }

        private static sbyte ToSnorm(float f)
        {
            int v = Mathf.RoundToInt(Mathf.Clamp(f, -1f, 1f) * 127f);
            return (sbyte)v;
        }

        public void AddTriangleRaw(int a, int b, int c)
        {
            if (IndexCount + 3 > Indices.Length) Array.Resize(ref Indices, Indices.Length * 2);
            Indices[IndexCount++] = a;
            Indices[IndexCount++] = b;
            Indices[IndexCount++] = c;
        }

        /// <summary>Triángulo cuyo lado visible (sentido horario en Unity) mira hacia <paramref name="facing"/>.</summary>
        public void AddTriangle(int a, int b, int c, Vector3 facing)
        {
            Vector3 pa = Vertices[a].Position, pb = Vertices[b].Position, pc = Vertices[c].Position;
            Vector3 n = Vector3.Cross(pb - pa, pc - pa);
            if (Vector3.Dot(n, facing) >= 0f) AddTriangleRaw(a, b, c);
            else AddTriangleRaw(a, c, b);
        }

        /// <summary>Cuad a0-b0 (fila 0) y a1-b1 (fila 1), orientado hacia <paramref name="facing"/>.</summary>
        public void AddQuad(int a0, int b0, int a1, int b1, Vector3 facing)
        {
            AddTriangle(a0, a1, b0, facing);
            AddTriangle(b0, a1, b1, facing);
        }

        /// <summary>
        /// Tira entre dos polilíneas paralelas <paramref name="a"/> y <paramref name="b"/> (misma cantidad de puntos), con la normal por punto.
        /// u constante en cada borde y v por punto.
        /// </summary>
        public void AddStrip(ReadOnlySpan<Vector3> a, ReadOnlySpan<Vector3> b, ReadOnlySpan<Vector3> normals, float uA, float uB,
            ReadOnlySpan<float> v, sbyte mask, float segment)
        {
            int n = a.Length;
            int first = VertexCount;
            for (int i = 0; i < n; i++)
            {
                AddVertex(a[i], normals[i], mask, uA, v[i], segment);
                AddVertex(b[i], normals[i], mask, uB, v[i], segment);
            }
            for (int i = 0; i < n - 1; i++)
            {
                int k = first + i * 2;
                AddQuad(k, k + 1, k + 2, k + 3, normals[i] + normals[i + 1]);
            }
        }

        public void CopyFrom(MeshBuffers other)
        {
            if (Vertices.Length < other.VertexCount) Array.Resize(ref Vertices, other.VertexCount);
            if (Indices.Length < other.IndexCount) Array.Resize(ref Indices, other.IndexCount);
            Array.Copy(other.Vertices, Vertices, other.VertexCount);
            Array.Copy(other.Indices, Indices, other.IndexCount);
            VertexCount = other.VertexCount;
            IndexCount = other.IndexCount;
            BoundsMin = other.BoundsMin;
            BoundsMax = other.BoundsMax;
        }
    }
}
