using Urbe.Sim.Roads;

namespace Urbe.Sim.Commands
{
    /// <summary>
    /// Orden del jugador sobre la red viaria. Es un dato pequeno (lo crea la interfaz y lo encola); al aplicarse
    /// guarda lo necesario para deshacerse. Undo solo es valido justo despues de un Apply exitoso y en orden LIFO.
    /// </summary>
    public interface ICommand
    {
        /// <summary>Comprueba si Apply tendria exito sin tocar la red.</summary>
        ValidationResult Validate(RoadNetwork network);

        /// <summary>Aplica el comando; false (sin cambios) si falla. Se puede volver a llamar tras Undo (rehacer).</summary>
        bool Apply(RoadNetwork network);

        /// <summary>Restaura el estado anterior al ultimo Apply.</summary>
        void Undo(RoadNetwork network);
    }
}
