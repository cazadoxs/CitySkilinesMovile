using System;
using System.Collections.Generic;
using Urbe.Sim.Ids;
using Urbe.Sim.Junctions;
using Urbe.Sim.Random;
using Urbe.Sim.Roads;
using Urbe.Sim.Tests.Roads;
using Urbe.Sim.Traffic;
using Urbe.Sim.Vehicles;
using Xunit;

namespace Urbe.Sim.Tests.Gridlock
{
    /// <summary>
    /// Manzana cuadrada de 100 m de lado con 4 cruces (en cada esquina muere un ramal de 100 m: son T). Los coches dan 0-6 pasos
    /// por el anillo y salen por el ramal de la esquina donde acaban. <see cref="Lock"/> provoca el bloqueo total: llena los 4
    /// tramos con los primeros de cada tramo parados y los suelta cuando todo el anillo esta quieto; cada primero quiere seguir
    /// por el anillo, cuyo tramo siguiente esta lleno: ciclo de espera de 52 coches.
    /// </summary>
    internal sealed class BlockScenario
    {
        public readonly RoadNetwork Net;
        public readonly TrafficSimulation Sim;
        public readonly JunctionController? Ctrl;
        /// <summary>Segmentos del anillo en el orden de marcha; el carril 0 de avance de cada uno es el del anillo.</summary>
        public readonly EntityId[] Ring = new EntityId[4];
        public readonly EntityId[] Stubs = new EntityId[4];
        public readonly EntityId[] Corners = new EntityId[4];
        public readonly LaneId[] RingLanes = new LaneId[4];
        /// <summary>Carril de salida del ramal de cada esquina.</summary>
        public readonly LaneId[] StubOut = new LaneId[4];
        private readonly LaneId[] _route = new LaneId[16];
        private Pcg32 _rng;
        public int Spawned;
        /// <summary>Tick en que se solto el bloqueo (fin de <see cref="Lock"/>).</summary>
        public ulong LockTick;

        public BlockScenario(ulong seed, TrafficSimulationOptions? options = null, Func<RoadNetwork, IJunctionGate>? gateFactory = null, bool clockwise = true, float side = 100f)
        {
            Net = Kit.New();
            float h = side / 2f;
            (float, float)[] pts = clockwise
                ? new[] { (-h, h), (h, h), (h, -h), (-h, -h) }
                : new[] { (-h, h), (-h, -h), (h, -h), (h, h) };
            for (int k = 0; k < 4; k++) Corners[k] = Kit.Node(Net, pts[k].Item1, pts[k].Item2);
            for (int k = 0; k < 4; k++) Ring[k] = Kit.Seg(Net, Corners[k], Corners[(k + 1) % 4], RoadTypeIds.Street2);
            for (int k = 0; k < 4; k++)
            {
                float sx = pts[k].Item1 > 0 ? 100f : -100f;
                EntityId far = Kit.Node(Net, pts[k].Item1 + sx, pts[k].Item2);
                Stubs[k] = Kit.Seg(Net, far, Corners[k], RoadTypeIds.Street2);
                StubOut[k] = new LaneId(Stubs[k].Index, Net.GetSegment(Stubs[k]).LanesForward);
            }
            for (int k = 0; k < 4; k++) RingLanes[k] = new LaneId(Ring[k].Index, 0);
            options ??= new TrafficSimulationOptions();
            options.VehicleCapacity = Math.Max(options.VehicleCapacity, 256);
            options.Vehicle = new VehicleConfig { DriverVariation = true };
            IJunctionGate gate;
            if (gateFactory != null) gate = gateFactory(Net);
            else gate = Ctrl = new JunctionController(Net);
            Sim = new TrafficSimulation(Net, seed, options, gate);
            _rng = new Pcg32(seed, 0x474C4B);
        }

        public VehicleSystem Vehicles => Sim.Vehicles;

        /// <summary>
        /// Alta en el carril del anillo <paramref name="k"/> con <paramref name="steps"/> pasos de anillo (0 = sale por el ramal de la
        /// esquina siguiente) y salida por el ramal de la esquina donde acaba.
        /// </summary>
        public bool TrySpawnOnRing(int k, int steps, float startS, bool frozen, out EntityId id)
        {
            int n = 0;
            for (int j = 0; j <= steps; j++) _route[n++] = RingLanes[(k + j) % 4];
            _route[n++] = StubOut[(k + steps + 1) % 4];
            float target = Net.GetLane(_route[n - 1]).Length * 0.5f;
            if (!Vehicles.TrySpawn(VehicleTypeId.Car, new ReadOnlySpan<LaneId>(_route, 0, n), startS, target, 0f, out id)) return false;
            Spawned++;
            if (frozen) Vehicles.SetFrozen(id, true);
            return true;
        }

        /// <summary>
        /// Provoca el bloqueo: primeros de cada tramo parados (con un paso mas por el anillo), el resto aparece al inicio del tramo
        /// hasta llenarlo; cuando el anillo entero esta quieto y no caben mas, se sueltan los primeros. Devuelve false si no se llena.
        /// </summary>
        public bool Lock(int maxTicks = 3000)
        {
            var heads = new List<EntityId>();
            for (int k = 0; k < 4; k++)
            {
                float len = Net.GetLane(RingLanes[k]).Length;
                if (!TrySpawnOnRing(k, 1 + _rng.NextInt(3), len - 1f, true, out EntityId id)) return false;
                heads.Add(id);
            }
            var fails = new int[4];
            for (int t = 0; t < maxTicks; t++)
            {
                if (t % 5 == 0)
                {
                    for (int k = 0; k < 4; k++)
                        fails[k] = TrySpawnOnRing(k, _rng.NextInt(0, 3), 4.5f, false, out _) ? 0 : fails[k] + 1;
                }
                Sim.Step();
                if (t % 5 != 0 || t < 100) continue;
                bool full = true;
                for (int k = 0; k < 4; k++) if (fails[k] < 3) full = false;
                if (full && StoppedCount(1) == Vehicles.AliveCount)
                {
                    foreach (EntityId h in heads) Vehicles.SetFrozen(h, false);
                    LockTick = Vehicles.CurrentTick;
                    return true;
                }
            }
            return false;
        }

        public int StoppedCount(int minTicks)
        {
            int n = 0;
            for (int i = 0; i < Vehicles.HighWater; i++)
            {
                ref readonly Vehicle v = ref Vehicles.GetVehicle(i);
                if (v.IsAlive && v.WaitTicks >= minTicks) n++;
            }
            return n;
        }

        public int MaxWait()
        {
            int m = 0;
            for (int i = 0; i < Vehicles.HighWater; i++)
            {
                ref readonly Vehicle v = ref Vehicles.GetVehicle(i);
                if (v.IsAlive && v.WaitTicks > m) m = v.WaitTicks;
            }
            return m;
        }
    }
}
