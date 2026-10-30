using System;
using Urbe.Sim.Roads;

namespace Urbe.Sim.Commands
{
    /// <summary>Historial con deshacer/rehacer de pila acotada (al llenarse se descarta el comando mas antiguo).</summary>
    public sealed class CommandHistory
    {
        private readonly ICommand?[] _undo;
        private int _undoStart;
        private int _undoCount;
        private readonly ICommand?[] _redo;
        private int _redoCount;

        public CommandHistory(int capacity = 100)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _undo = new ICommand?[capacity];
            _redo = new ICommand?[capacity];
        }

        public int Capacity => _undo.Length;
        public int UndoCount => _undoCount;
        public int RedoCount => _redoCount;

        /// <summary>Valida y aplica; si tiene exito se registra y se vacia la pila de rehacer.</summary>
        public ValidationResult Execute(RoadNetwork network, ICommand command)
        {
            ValidationResult v = command.Validate(network);
            if (v != ValidationResult.Ok) return v;
            if (!command.Apply(network)) return ValidationResult.TooManyConnections;
            ClearRedo();
            PushUndo(command);
            return ValidationResult.Ok;
        }

        public bool Undo(RoadNetwork network)
        {
            if (_undoCount == 0) return false;
            int idx = (_undoStart + _undoCount - 1) % _undo.Length;
            ICommand cmd = _undo[idx]!;
            _undo[idx] = null;
            _undoCount--;
            cmd.Undo(network);
            if (_redoCount == _redo.Length)
            {
                Array.Copy(_redo, 1, _redo, 0, _redoCount - 1);
                _redoCount--;
            }
            _redo[_redoCount++] = cmd;
            return true;
        }

        public bool Redo(RoadNetwork network)
        {
            if (_redoCount == 0) return false;
            ICommand cmd = _redo[_redoCount - 1]!;
            if (!cmd.Apply(network)) return false;
            _redo[--_redoCount] = null;
            PushUndo(cmd);
            return true;
        }

        public void Clear()
        {
            while (_undoCount > 0) _undo[(_undoStart + --_undoCount) % _undo.Length] = null;
            _undoStart = 0;
            ClearRedo();
        }

        private void PushUndo(ICommand cmd)
        {
            if (_undoCount == _undo.Length)
            {
                _undo[_undoStart] = cmd;
                _undoStart = (_undoStart + 1) % _undo.Length;
            }
            else
            {
                _undo[(_undoStart + _undoCount) % _undo.Length] = cmd;
                _undoCount++;
            }
        }

        private void ClearRedo()
        {
            while (_redoCount > 0) _redo[--_redoCount] = null;
        }
    }
}
