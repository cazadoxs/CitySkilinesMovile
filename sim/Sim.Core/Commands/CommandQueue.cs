using Urbe.Sim.Roads;

namespace Urbe.Sim.Commands
{
    /// <summary>Cola de comandos de la interfaz; se vacia en orden al inicio del tick (GDD 8.1 paso 1).</summary>
    public sealed class CommandQueue
    {
        private ICommand?[] _items = new ICommand?[16];
        private int _head;
        private int _count;

        public int Count => _count;

        /// <summary>Comandos rechazados por validacion en el ultimo Drain.</summary>
        public int LastRejected { get; private set; }

        public void Enqueue(ICommand command)
        {
            if (_count == _items.Length)
            {
                var bigger = new ICommand?[_items.Length * 2];
                for (int i = 0; i < _count; i++) bigger[i] = _items[(_head + i) % _items.Length];
                _items = bigger;
                _head = 0;
            }
            _items[(_head + _count) % _items.Length] = command;
            _count++;
        }

        /// <summary>Aplica todos los comandos encolados por orden de llegada. Devuelve cuantos se aplicaron.</summary>
        public int Drain(RoadNetwork network, CommandHistory history)
        {
            int applied = 0;
            LastRejected = 0;
            while (_count > 0)
            {
                ICommand cmd = _items[_head]!;
                _items[_head] = null;
                _head = (_head + 1) % _items.Length;
                _count--;
                if (history.Execute(network, cmd) == ValidationResult.Ok) applied++;
                else LastRejected++;
            }
            _head = 0;
            return applied;
        }
    }
}
