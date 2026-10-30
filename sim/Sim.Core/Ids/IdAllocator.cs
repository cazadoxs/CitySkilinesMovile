using System;

namespace Urbe.Sim.Ids
{
    public sealed class IdAllocator
    {
        private int[] _generations;
        private bool[] _alive;
        private int[] _free;
        private int _freeCount;
        private int _nextIndex;

        public IdAllocator(int initialCapacity = 64)
        {
            if (initialCapacity < 1) initialCapacity = 1;
            _generations = new int[initialCapacity];
            _alive = new bool[initialCapacity];
            _free = new int[initialCapacity];
        }

        public int Capacity => _generations.Length;
        public int AliveCount => _nextIndex - _freeCount;

        public EntityId Allocate()
        {
            int index;
            if (_freeCount > 0)
            {
                index = _free[--_freeCount];
            }
            else
            {
                if (_nextIndex == _generations.Length) Grow();
                index = _nextIndex++;
            }
            _alive[index] = true;
            return new EntityId(index, _generations[index]);
        }

        public bool Free(EntityId id)
        {
            if (!IsAlive(id)) return false;
            _alive[id.Index] = false;
            _generations[id.Index]++;
            _free[_freeCount++] = id.Index;
            return true;
        }

        public bool IsAlive(EntityId id)
        {
            return id.Index >= 0 && id.Index < _nextIndex
                && _alive[id.Index] && _generations[id.Index] == id.Generation;
        }

        private void Grow()
        {
            int n = _generations.Length * 2;
            Array.Resize(ref _generations, n);
            Array.Resize(ref _alive, n);
            Array.Resize(ref _free, n);
        }
    }
}
