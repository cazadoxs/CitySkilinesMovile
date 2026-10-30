using Xunit;

// Los benchmarks con Stopwatch (Trait Category=Bench) necesitan la maquina para ellos: se ejecuta un test a la vez.
// La suite entera tarda ~1-2 s, asi que no se pierde nada.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
