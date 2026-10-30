using System.Threading;
using Urbe.Sim.Geometry;

namespace Urbe.Sim.Vehicles
{
    /// <summary>Estado de un vehiculo para el render. Position es la del FRENTE del vehiculo.</summary>
    public struct VehicleSnapshot
    {
        public int Id;
        public ushort Generation;
        public byte Type;
        public Vec2 Position;
        public float Elevation;
        public Vec2 Tangent;
        public float Speed;
    }

    /// <summary>Un snapshot publicado: los datos son validos mientras el lector lo tenga adquirido.</summary>
    public readonly struct SnapshotFrame
    {
        public readonly VehicleSnapshot[]? Data;
        public readonly int Count;
        public readonly ulong Tick;

        public SnapshotFrame(VehicleSnapshot[]? data, int count, ulong tick)
        {
            Data = data;
            Count = count;
            Tick = tick;
        }

        public bool IsValid => Data != null;
    }

    /// <summary>
    /// Triple bufer (ADR 002). Un escritor (la simulacion) y un lector (el render). El estado empaqueta (anterior, actual, mascara
    /// de bufers retenidos por el lector) en un int que se cambia con Interlocked: el escritor nunca toca un bufer publicado
    /// ni retenido; si no hay ninguno libre se salta ese snapshot (el render conserva el ultimo).
    /// </summary>
    public sealed class SnapshotRing
    {
        private const int None = 3;
        private readonly VehicleSnapshot[][] _bufs = new VehicleSnapshot[3][];
        private readonly int[] _counts = new int[3];
        private readonly ulong[] _ticks = new ulong[3];
        private int _state = None | (None << 2);
        private int _writing = -1;

        public SnapshotRing(int capacity)
        {
            for (int i = 0; i < 3; i++) _bufs[i] = new VehicleSnapshot[capacity];
        }

        public int Capacity => _bufs[0].Length;

        /// <summary>Escritor: pide un bufer libre. Devuelve false si el lector retiene los otros dos.</summary>
        public bool TryBeginWrite(out VehicleSnapshot[] buffer)
        {
            int s = Volatile.Read(ref _state);
            int prev = s & 3, cur = (s >> 2) & 3, held = (s >> 4) & 7;
            for (int w = 0; w < 3; w++)
            {
                if (w == prev || w == cur || ((held >> w) & 1) != 0) continue;
                _writing = w;
                buffer = _bufs[w];
                return true;
            }
            _writing = -1;
            buffer = _bufs[0];
            return false;
        }

        /// <summary>Escritor: publica el bufer que devolvio <see cref="TryBeginWrite"/>.</summary>
        public void Publish(int count, ulong tick)
        {
            int w = _writing;
            _counts[w] = count;
            _ticks[w] = tick;
            _writing = -1;
            while (true)
            {
                int s = Volatile.Read(ref _state);
                int cur = (s >> 2) & 3, held = (s >> 4) & 7;
                int ns = cur | (w << 2) | (held << 4);
                if (Interlocked.CompareExchange(ref _state, ns, s) == s) return;
            }
        }

        /// <summary>
        /// Lector: retiene el par (anterior, actual) hasta <see cref="Release"/>. Devuelve false si aun no hay ningun snapshot.
        /// <paramref name="previous"/> no es valido hasta que haya dos publicados.
        /// </summary>
        public bool Acquire(out SnapshotFrame previous, out SnapshotFrame current)
        {
            while (true)
            {
                int s = Volatile.Read(ref _state);
                int prev = s & 3, cur = (s >> 2) & 3;
                if (cur == None)
                {
                    previous = default;
                    current = default;
                    return false;
                }
                int held = (1 << cur) | (prev != None ? 1 << prev : 0);
                int ns = (s & 0xF) | (held << 4);
                if (Interlocked.CompareExchange(ref _state, ns, s) != s) continue;
                current = new SnapshotFrame(_bufs[cur], _counts[cur], _ticks[cur]);
                previous = prev != None ? new SnapshotFrame(_bufs[prev], _counts[prev], _ticks[prev]) : default;
                return true;
            }
        }

        public void Release()
        {
            while (true)
            {
                int s = Volatile.Read(ref _state);
                int ns = s & 0xF;
                if (Interlocked.CompareExchange(ref _state, ns, s) == s) return;
            }
        }
    }
}
