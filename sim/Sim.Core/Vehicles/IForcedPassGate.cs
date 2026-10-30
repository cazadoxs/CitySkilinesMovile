namespace Urbe.Sim.Vehicles
{
    /// <summary>
    /// Opcional de una puerta (M1-11): "paso forzado" para romper un bloqueo total. La peticion del vehiculo va primera en su nodo y se
    /// salta semaforo, stop, reclamacion y hueco a la salida, pero NUNCA la comprobacion de conflicto fisico (ADR 004, paso 4).
    /// </summary>
    public interface IForcedPassGate
    {
        /// <summary>Marca al vehiculo: su proxima peticion va primera. Dura hasta que se concede, se cancela o el vehiculo se da de baja.</summary>
        void ForceNextGrant(int vehicle);

        /// <summary>True mientras el vehiculo tiene un paso forzado sin conceder.</summary>
        bool IsForced(int vehicle);

        /// <summary>Retira el paso forzado pendiente (no seguro tras el tiempo de espera): la peticion vuelve a tratarse como las demas.</summary>
        void CancelForce(int vehicle);
    }
}
