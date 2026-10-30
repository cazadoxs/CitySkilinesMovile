using System;
using System.Runtime.CompilerServices;

namespace Urbe.Sim.Geometry
{
    /// <summary>Vector 2D en el plano horizontal (X, Z), en metros. Sin System.Numerics.</summary>
    public readonly struct Vec2 : IEquatable<Vec2>
    {
        public readonly float X;
        public readonly float Z;

        public Vec2(float x, float z)
        {
            X = x;
            Z = z;
        }

        public static Vec2 Zero => new Vec2(0f, 0f);

        public float LengthSquared => X * X + Z * Z;
        public float Length => (float)Math.Sqrt(X * X + Z * Z);

        /// <summary>Vector unitario; el vector cero devuelve cero.</summary>
        public Vec2 Normalized
        {
            get
            {
                float len = Length;
                return len > 1e-12f ? new Vec2(X / len, Z / len) : new Vec2(0f, 0f);
            }
        }

        /// <summary>Giro de 90 grados a la izquierda: (-Z, X).</summary>
        public Vec2 Perp => new Vec2(-Z, X);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Z * b.Z;

        /// <summary>Producto vectorial escalar 2D: a.X*b.Z - a.Z*b.X.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Cross(Vec2 a, Vec2 b) => a.X * b.Z - a.Z * b.X;

        public static Vec2 Lerp(Vec2 a, Vec2 b, float t) => new Vec2(a.X + (b.X - a.X) * t, a.Z + (b.Z - a.Z) * t);

        public static float Distance(Vec2 a, Vec2 b) => (a - b).Length;

        public bool ApproxEquals(Vec2 other, float eps)
        {
            return Math.Abs(X - other.X) <= eps && Math.Abs(Z - other.Z) <= eps;
        }

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Z + b.Z);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Z - b.Z);
        public static Vec2 operator -(Vec2 a) => new Vec2(-a.X, -a.Z);
        public static Vec2 operator *(Vec2 a, float s) => new Vec2(a.X * s, a.Z * s);
        public static Vec2 operator *(float s, Vec2 a) => new Vec2(a.X * s, a.Z * s);
        public static Vec2 operator /(Vec2 a, float s) => new Vec2(a.X / s, a.Z / s);
        public static bool operator ==(Vec2 a, Vec2 b) => a.X == b.X && a.Z == b.Z;
        public static bool operator !=(Vec2 a, Vec2 b) => !(a == b);

        public bool Equals(Vec2 other) => this == other;
        public override bool Equals(object? obj) => obj is Vec2 v && this == v;
        public override int GetHashCode() => unchecked((X.GetHashCode() * 397) ^ Z.GetHashCode());
        public override string ToString() => "(" + X + ", " + Z + ")";
    }

    /// <summary>Caja alineada a los ejes en el plano (X, Z).</summary>
    public readonly struct Aabb2
    {
        public readonly Vec2 Min;
        public readonly Vec2 Max;

        public Aabb2(Vec2 min, Vec2 max)
        {
            Min = min;
            Max = max;
        }

        public Vec2 Size => Max - Min;

        public bool Overlaps(in Aabb2 o, float margin = 0f)
        {
            return Min.X <= o.Max.X + margin && Max.X >= o.Min.X - margin
                && Min.Z <= o.Max.Z + margin && Max.Z >= o.Min.Z - margin;
        }
    }
}
