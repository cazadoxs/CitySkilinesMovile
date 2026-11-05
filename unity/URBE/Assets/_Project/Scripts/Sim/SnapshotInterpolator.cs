using System;
using Urbe.Sim.Vehicles;

namespace Urbe.Game.Sim
{
    /// <summary>Vehículo ya interpolado, listo para pintar. Posición en mundo Unity (x, y = elevación, z).</summary>
    public struct InterpolatedVehicle
    {
        public int Id;
        public byte Type;
        public float X, Y, Z;
        /// <summary>Dirección de avance (unitaria) en el plano XZ.</summary>
        public float DirX, DirZ;
        public float Speed;
    }

    /// <summary>
    /// Interpola entre los dos últimos snapshots de la simulación (GDD 19.5). Empareja vehículos por Id+generación; los que
    /// acaban de aparecer se pintan en su posición actual. Reutiliza sus arrays: sin asignaciones por fotograma.
    /// </summary>
    public sealed class SnapshotInterpolator
    {
        private int[] _prevIndexById;      // Id -> índice en previous + 1 (0 = no está)
        private ushort[] _prevGenById;
        private int[] _stamp;              // marca de fotograma para no limpiar el mapa
        private int _frame;

        public InterpolatedVehicle[] Output { get; private set; }
        public int Count { get; private set; }

        public SnapshotInterpolator(int capacity)
        {
            _prevIndexById = new int[capacity];
            _prevGenById = new ushort[capacity];
            _stamp = new int[capacity];
            Output = new InterpolatedVehicle[capacity];
        }

        /// <summary>Rellena <see cref="Output"/>/<see cref="Count"/> para <paramref name="alpha"/> en [0, 1] entre previous y current.</summary>
        public void Interpolate(in SnapshotFrame previous, in SnapshotFrame current, float alpha)
        {
            Count = 0;
            if (!current.IsValid) return;
            if (alpha < 0f) alpha = 0f; else if (alpha > 1f) alpha = 1f;
            if (current.Count > Output.Length) Grow(current.Count);

            _frame++;
            VehicleSnapshot[]? pd = previous.IsValid ? previous.Data : null;
            if (pd != null)
            {
                for (int i = 0; i < previous.Count; i++)
                {
                    int id = pd[i].Id;
                    if (id < 0 || id >= _stamp.Length) continue;
                    _stamp[id] = _frame;
                    _prevIndexById[id] = i + 1;
                    _prevGenById[id] = pd[i].Generation;
                }
            }

            VehicleSnapshot[] cd = current.Data!;
            int n = current.Count;
            for (int i = 0; i < n; i++)
            {
                ref readonly VehicleSnapshot c = ref cd[i];
                float px = c.Position.X, pz = c.Position.Z, py = c.Elevation;
                float dx = c.Tangent.X, dz = c.Tangent.Z;
                if (pd != null && c.Id >= 0 && c.Id < _stamp.Length && _stamp[c.Id] == _frame && _prevGenById[c.Id] == c.Generation)
                {
                    ref readonly VehicleSnapshot p = ref pd[_prevIndexById[c.Id] - 1];
                    px = p.Position.X + (c.Position.X - p.Position.X) * alpha;
                    pz = p.Position.Z + (c.Position.Z - p.Position.Z) * alpha;
                    py = p.Elevation + (c.Elevation - p.Elevation) * alpha;
                    dx = p.Tangent.X + (c.Tangent.X - p.Tangent.X) * alpha;
                    dz = p.Tangent.Z + (c.Tangent.Z - p.Tangent.Z) * alpha;
                }
                float len = (float)Math.Sqrt(dx * dx + dz * dz);
                if (len > 1e-5f) { dx /= len; dz /= len; } else { dx = c.Tangent.X; dz = c.Tangent.Z; }
                ref InterpolatedVehicle o = ref Output[i];
                o.Id = c.Id; o.Type = c.Type;
                o.X = px; o.Y = py; o.Z = pz;
                o.DirX = dx; o.DirZ = dz;
                o.Speed = c.Speed;
            }
            Count = n;
        }

        private void Grow(int needed)
        {
            int size = Math.Max(needed, Output.Length * 2);
            Array.Resize(ref _prevIndexById, size);
            Array.Resize(ref _prevGenById, size);
            Array.Resize(ref _stamp, size);
            var bigger = new InterpolatedVehicle[size];
            Output = bigger;
        }
    }
}
