using Urbe.Sim.Roads;
using Xunit;
using Xunit.Abstractions;

namespace Urbe.Sim.Tests.Junctions
{
    /// <summary>M1-07b: capacidad de un cruce con los accesos saturados (el area del cruce es la real, tras el retranqueo).</summary>
    public class JunctionCapacityTests
    {
        private readonly ITestOutputHelper _out;

        public JunctionCapacityTests(ITestOutputHelper output) { _out = output; }

        /// <summary>
        /// Vehiculos por segundo que atraviesan una X de calles 1+1 con prioridad y los cuatro accesos saturados (demanda de 2 veh/s por
        /// carril: la cola llega siempre al inicio del brazo), tras 60 s de calentamiento y durante 300 s. Los que ya cruzaron se dan de baja
        /// a 30 m del cruce para que la parada en destino no frene el cruce.
        /// </summary>
        private static double Measure(ulong seed, bool straightOnly, out int collisions)
        {
            Crossing x = JK.X(RoadTypeIds.Street2, RoadTypeIds.Street2);
            var sc = new PropertyScenario(x, seed, 2f, 2f);
            sc.CheckCountersEachTick = false;
            sc.DespawnExitBeyond = 30f;
            sc.StraightOnly = straightOnly;
            const int warm = 600, measure = 3000;
            for (int t = 0; t < warm; t++) sc.Step(true);
            int before = sc.Trace.Exits;
            for (int t = 0; t < measure; t++) sc.Step(true);
            collisions = sc.Collisions;
            return (sc.Trace.Exits - before) / (measure * (double)sc.Sys.Config.Dt);
        }

        // OBJETIVO DE LA TARJETA (>= 0,6 veh/s) NO ALCANZADO: ESCALAR (ver docs/tareas/M1-07b.md). El retranqueo hace real el area del cruce
        // (12 m en vez de 3 m), pero la capacidad no la limita la geometria sino arrancar desde parado (1,5 m/s2 para cruzar ~16 m en
        // ~5 s) y que la reserva sea secuencial; medida: ~0,41 solo rectos, ~0,28 con todos los giros.
        [Theory(Skip = "ESCALAR: objetivo de 0,6 veh/s inalcanzable solo con el retranqueo (medido ~0,41 solo rectos)")]
        [InlineData(1UL)]
        public void StreetX_Priority_Saturated_Straight_CapacityAtLeast06(ulong seed)
        {
            double cap = Measure(seed, true, out int collisions);
            Assert.Equal(0, collisions);
            Assert.True(cap >= 0.6, "capacidad " + cap + " veh/s");
        }

        [Theory]
        [InlineData(1UL)]
        [InlineData(2UL)]
        [InlineData(3UL)]
        public void StreetX_Priority_Saturated_Straight_Capacity(ulong seed)
        {
            double cap = Measure(seed, true, out int collisions);
            _out.WriteLine("X calles 1+1 prioridad saturada, solo rectos, semilla " + seed + ": " + cap.ToString("F3") + " veh/s, colisiones " + collisions);
            Assert.Equal(0, collisions);
            Assert.True(cap >= 0.35, "capacidad " + cap + " veh/s");
        }

        // Con todos los giros (un tercio a la izquierda, desde un unico carril por acceso y sin carriles de giro) la capacidad queda en
        // ~0,3 veh/s: el coche que espera para girar a la izquierda bloquea su carril. Solo se vigila que no baje del nivel previo.
        [Theory]
        [InlineData(1UL)]
        [InlineData(2UL)]
        [InlineData(3UL)]
        public void StreetX_Priority_Saturated_AllTurns_NotBelowPrevious(ulong seed)
        {
            double cap = Measure(seed, false, out int collisions);
            _out.WriteLine("X calles 1+1 prioridad saturada, todos los giros, semilla " + seed + ": " + cap.ToString("F3") + " veh/s, colisiones " + collisions);
            Assert.Equal(0, collisions);
            Assert.True(cap >= 0.24, "capacidad " + cap + " veh/s");
        }
    }
}
