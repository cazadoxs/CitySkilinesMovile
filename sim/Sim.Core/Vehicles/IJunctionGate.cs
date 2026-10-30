using System;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Sim.Vehicles
{
    /// <summary>Peticion de entrada a una conexion (fase 1a). ADR 004.</summary>
    public struct EntryRequest
    {
        public int Vehicle;
        public LaneId From;
        public int Connection;
        public LaneId To;
        /// <summary>Lane.Length - S.</summary>
        public float DistanceToStop;
        public float Speed;
        /// <summary>Length + s0 del vehiculo.</summary>
        public float Space;
        /// <summary>Vehicle.WaitTicks.</summary>
        public ushort StoppedTicks;
    }

    public struct EntryGrant
    {
        public int Vehicle;
        public int Connection;
        public float Space;
    }

    /// <summary>Consultas del sistema de vehiculos que necesita una puerta. ADR 004.</summary>
    public interface IVehicleView
    {
        /// <summary>Hueco libre al inicio del carril: (trasera de la cola o Length) menos lo reservado.</summary>
        float FreeSpace(LaneId lane);

        /// <summary>
        /// Posicion de la trasera del vehiculo medida desde el inicio de la conexion: S - Len si esta en ella, Lc + S - Len
        /// si ya esta en el carril siguiente (PrevConnection), +infinito si no tiene cuerpo en ella.
        /// </summary>
        float RearOnConnection(int vehicle, int connection);
    }

    /// <summary>Opcional: la puerta recibe la vista y la capacidad de vehiculos al crearse el sistema.</summary>
    public interface IVehicleViewConsumer
    {
        void Bind(IVehicleView view, int vehicleCapacity);
    }

    /// <summary>
    /// Punto de enganche con las intersecciones (ADR 002 enmendado por ADR 004). Orden por tick:
    /// OnNetworkChanged (si hay cambio de epoca) -> BeginTick -> RequestEntry* -> ResolveRequests -> OnEntered/OnExited/OnVehicleRemoved.
    /// </summary>
    public interface IJunctionGate
    {
        void OnNetworkChanged(ReadOnlySpan<EntityId> changedNodes);
        void BeginTick(ulong tick);
        /// <summary>Fase 1a: solo encola.</summary>
        void RequestEntry(in EntryRequest req);
        /// <summary>Fase 1b: decide en orden canonico; el span vale hasta el siguiente BeginTick.</summary>
        ReadOnlySpan<EntryGrant> ResolveRequests();
        void OnEntered(int connection, int vehicle);
        /// <summary>Cuando la TRASERA del vehiculo sale de la conexion.</summary>
        void OnExited(int connection, int vehicle);
        /// <summary>Baja del vehiculo (despawn, llegada o borrado por edicion) estuviera donde estuviera.</summary>
        void OnVehicleRemoved(int vehicle);
    }
}
