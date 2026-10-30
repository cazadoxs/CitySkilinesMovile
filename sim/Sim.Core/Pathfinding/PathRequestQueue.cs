using System;
using Urbe.Sim.Ids;

namespace Urbe.Sim.Pathfinding
{
    /// <summary>Peticion de ruta para <see cref="PathRequestQueue"/>.</summary>
    public struct PathRequest
    {
        public EntityId OriginSegment;
        public TravelDirs OriginDirs;
        public EntityId DestinationSegment;
        public TravelDirs DestinationDirs;
        public PathOptions Options;
        public PathPriority Priority;
    }

    /// <summary>
    /// Cola de peticiones con prioridades (Emergencia &gt; Servicio &gt; Transporte publico &gt; Particular), FIFO dentro
    /// de cada una. El presupuesto es un numero de peticiones por llamada a <see cref="Process"/> (determinista, sin
    /// reloj); el llamador (Unity) lo traduce desde su presupuesto en ms. Los resultados se consultan por id y viven en
    /// un anillo de <c>resultCapacity</c> huecos: un id sin leer se pierde cuando se reutiliza su hueco (los pendientes
    /// bloquean el alta, que devuelve false). Sin asignaciones tras el calentamiento (las colas crecen al doble).
    /// </summary>
    public sealed class PathRequestQueue
    {
        private struct Slot
        {
            public int Id;
            public PathStatus Status;
            public int Length;
            public float Cost;
            public int Order;
        }

        private sealed class Fifo
        {
            public PathRequest[] Items = new PathRequest[64];
            public int[] Ids = new int[64];
            public int Head;
            public int Count;

            public void Push(in PathRequest r, int id)
            {
                if (Count == Items.Length)
                {
                    var ni = new PathRequest[Count * 2];
                    var nd = new int[Count * 2];
                    for (int i = 0; i < Count; i++)
                    {
                        int src = (Head + i) % Items.Length;
                        ni[i] = Items[src];
                        nd[i] = Ids[src];
                    }
                    Items = ni;
                    Ids = nd;
                    Head = 0;
                }
                int pos = (Head + Count) % Items.Length;
                Items[pos] = r;
                Ids[pos] = id;
                Count++;
            }

            public int Pop(out PathRequest r)
            {
                r = Items[Head];
                int id = Ids[Head];
                Head = (Head + 1) % Items.Length;
                Count--;
                return id;
            }
        }

        private const int PriorityCount = 4;

        private readonly PathFinder _finder;
        private readonly int _resultCapacity;
        private readonly int _maxRouteLength;
        private readonly Fifo[] _fifos = new Fifo[PriorityCount];
        private readonly Slot[] _slots;
        private readonly RouteStep[] _steps;
        private readonly RouteStep[] _scratch;
        private int _nextId = 1;
        private int _completed;
        private int _pending;

        public PathRequestQueue(PathFinder finder, int resultCapacity = 512, int maxRouteLength = 256)
        {
            _finder = finder ?? throw new ArgumentNullException(nameof(finder));
            if (resultCapacity < 1) throw new ArgumentOutOfRangeException(nameof(resultCapacity));
            if (maxRouteLength < 1) throw new ArgumentOutOfRangeException(nameof(maxRouteLength));
            _resultCapacity = resultCapacity;
            _maxRouteLength = maxRouteLength;
            for (int i = 0; i < PriorityCount; i++) _fifos[i] = new Fifo();
            _slots = new Slot[resultCapacity];
            _steps = new RouteStep[resultCapacity * maxRouteLength];
            _scratch = new RouteStep[maxRouteLength];
        }

        public int PendingCount => _pending;

        private int SlotOf(int id) => (id - 1) % _resultCapacity;

        /// <summary>Encola una peticion. False si el hueco de resultado que le tocaria sigue ocupado por una pendiente.</summary>
        public bool TryEnqueue(in PathRequest request, out int id)
        {
            id = 0;
            int pr = (int)request.Priority;
            if (pr < 0 || pr >= PriorityCount) pr = PriorityCount - 1;
            int candidate = _nextId;
            ref Slot s = ref _slots[SlotOf(candidate)];
            if (s.Status == PathStatus.Pending) return false;
            _nextId = candidate == int.MaxValue ? 1 : candidate + 1;
            s.Id = candidate;
            s.Status = PathStatus.Pending;
            s.Length = 0;
            s.Cost = 0f;
            s.Order = 0;
            _fifos[pr].Push(request, candidate);
            _pending++;
            id = candidate;
            return true;
        }

        /// <summary>
        /// Atiende hasta <paramref name="maxRequests"/> peticiones, siempre la de mayor prioridad y, a igualdad, la mas
        /// antigua. Devuelve cuantas ha resuelto.
        /// </summary>
        public int Process(int maxRequests)
        {
            int done = 0;
            while (done < maxRequests)
            {
                Fifo? f = null;
                for (int i = 0; i < PriorityCount; i++)
                {
                    if (_fifos[i].Count > 0) { f = _fifos[i]; break; }
                }
                if (f == null) break;
                int id = f.Pop(out PathRequest r);
                ref Slot s = ref _slots[SlotOf(id)];
                bool ok = _finder.FindPath(r.OriginSegment, r.OriginDirs, r.DestinationSegment, r.DestinationDirs,
                    r.Options, _scratch, out int len, out float cost);
                s.Cost = cost;
                s.Order = ++_completed;
                if (ok)
                {
                    s.Status = PathStatus.Found;
                    s.Length = len;
                    Array.Copy(_scratch, 0, _steps, SlotOf(id) * _maxRouteLength, len);
                }
                else
                {
                    s.Status = len > 0 ? PathStatus.TooLong : PathStatus.NotFound;
                    s.Length = 0;
                }
                _pending--;
                done++;
            }
            return done;
        }

        public PathStatus GetStatus(int id)
        {
            if (id < 1) return PathStatus.None;
            ref readonly Slot s = ref _slots[SlotOf(id)];
            return s.Id == id ? s.Status : PathStatus.None;
        }

        /// <summary>Orden de resolucion (1 = la primera resuelta por esta cola); 0 si no esta resuelta.</summary>
        public int GetCompletionOrder(int id)
        {
            if (id < 1) return 0;
            ref readonly Slot s = ref _slots[SlotOf(id)];
            return s.Id == id && s.Status != PathStatus.Pending ? s.Order : 0;
        }

        /// <summary>
        /// Copia el resultado de una peticion resuelta. Devuelve true solo si hay ruta y cabe en <paramref name="output"/>
        /// (si no cabe, <paramref name="length"/> dice cuanto hace falta). El hueco no se libera: se reutiliza solo.
        /// </summary>
        public bool TryGetResult(int id, Span<RouteStep> output, out int length, out float cost)
        {
            length = 0;
            cost = 0f;
            if (id < 1) return false;
            int si = SlotOf(id);
            ref readonly Slot s = ref _slots[si];
            if (s.Id != id || s.Status != PathStatus.Found) return false;
            cost = s.Cost;
            length = s.Length;
            if (s.Length > output.Length) return false;
            new ReadOnlySpan<RouteStep>(_steps, si * _maxRouteLength, s.Length).CopyTo(output);
            return true;
        }
    }
}
