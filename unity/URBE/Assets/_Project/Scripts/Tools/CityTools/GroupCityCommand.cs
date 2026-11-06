using System.Collections.Generic;
using Urbe.Sim.City;

namespace Urbe.Game.Tools.CityTools
{
    /// <summary>
    /// Varias órdenes de ciudad como una sola en el historial (M3-08): un trazo de pincel de zona con muchas pasadas se deshace de una vez.
    /// Deshacer revierte en orden inverso solo las que se aplicaron.
    /// </summary>
    public sealed class GroupCityCommand : CityCommand
    {
        private readonly List<CityCommand> _commands;
        private readonly List<CityCommand> _applied = new List<CityCommand>();

        public GroupCityCommand(List<CityCommand> commands) { _commands = commands; }

        public int Count => _commands.Count;

        public override CityResult Validate(CitySimulation city)
        {
            if (_commands.Count == 0) return Result = CityResult.InvalidArgument;
            for (int i = 0; i < _commands.Count; i++)
            {
                CityResult r = _commands[i].Validate(city);
                if (r != CityResult.Ok) return Result = r;
            }
            return Result = CityResult.Ok;
        }

        public override bool Apply(CitySimulation city)
        {
            _applied.Clear();
            for (int i = 0; i < _commands.Count; i++)
            {
                CityCommand c = _commands[i];
                if (c.Validate(city) == CityResult.Ok && c.Apply(city)) _applied.Add(c);
            }
            Result = _applied.Count > 0 ? CityResult.Ok : CityResult.InvalidTarget;
            return _applied.Count > 0;
        }

        public override void Undo(CitySimulation city)
        {
            for (int i = _applied.Count - 1; i >= 0; i--) _applied[i].Undo(city);
            _applied.Clear();
        }
    }
}
