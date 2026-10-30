using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Urbe.Sim.Junctions;
using Urbe.Sim.Traffic;
using Xunit;

namespace Urbe.Sim.Tests.Golden
{
    /// <summary>
    /// Tests de repeticion (GDD 19.6): escenario fijo + semilla + N ticks, y el hash de estado en cada checkpoint debe coincidir con
    /// <c>golden.json</c>. Si falla, el comportamiento de la simulacion ha cambiado: o es un bug, o es intencionado y entonces se
    /// regenera con <c>URBE_UPDATE_GOLDEN=1 dotnet test sim/Sim.sln --filter Category=Golden</c> y se revisa el diff del JSON.
    /// </summary>
    [Trait("Category", "Golden")]
    public class GoldenTests
    {
        private const ulong Seed = 20261030UL;
        private const ulong OtherSeed = Seed + 1;

        private static string GoldenPath([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "golden.json");

        private static string Hex(ulong h) => h.ToString("x16");

        private static List<string> Run(string name, ulong seed, out GoldenScenarios.Built built)
        {
            built = GoldenScenarios.Create(name, seed);
            var list = new List<string>();
            for (int t = 0; t < GoldenScenarios.Ticks; t++)
            {
                built.Script?.Invoke(t);
                built.Sim.Step();
                if ((t + 1) % GoldenScenarios.CheckpointEvery == 0) list.Add(Hex(built.Sim.ComputeHash()));
            }
            return list;
        }

        public static IEnumerable<object[]> AllScenarios()
        {
            foreach (string n in GoldenScenarios.Names) yield return new object[] { n };
        }

        [Fact]
        public void Golden_HashesMatchStoredCheckpoints()
        {
            string path = GoldenPath();
            var current = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string name in GoldenScenarios.Names) current[name] = Run(name, Seed, out _);

            if (Environment.GetEnvironmentVariable("URBE_UPDATE_GOLDEN") == "1")
            {
                var doc = new SortedDictionary<string, object>(StringComparer.Ordinal)
                {
                    ["seed"] = Seed,
                    ["ticks"] = GoldenScenarios.Ticks,
                    ["checkpointEvery"] = GoldenScenarios.CheckpointEvery,
                    ["scenarios"] = current,
                };
                File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }) + "\n");
                return;
            }

            Assert.True(File.Exists(path), "Falta golden.json; generalo con URBE_UPDATE_GOLDEN=1");
            using JsonDocument stored = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = stored.RootElement;
            Assert.Equal(Seed, root.GetProperty("seed").GetUInt64());
            JsonElement scen = root.GetProperty("scenarios");
            foreach (var kv in current)
            {
                JsonElement arr = scen.GetProperty(kv.Key);
                Assert.Equal(kv.Value.Count, arr.GetArrayLength());
                for (int i = 0; i < kv.Value.Count; i++)
                    Assert.True(arr[i].GetString() == kv.Value[i],
                        kv.Key + ": hash distinto en el tick " + ((i + 1) * GoldenScenarios.CheckpointEvery) + " (guardado " + arr[i].GetString() + ", actual " + kv.Value[i] + "). Cambio de comportamiento: si es intencionado, regenera con URBE_UPDATE_GOLDEN=1");
            }
        }

        [Theory]
        [MemberData(nameof(AllScenarios))]
        public void TwoRuns_SameSeed_SameHashes(string name)
        {
            Assert.Equal(Run(name, Seed, out _), Run(name, Seed, out _));
        }

        [Theory]
        [MemberData(nameof(AllScenarios))]
        public void DifferentSeed_DifferentHash(string name)
        {
            List<string> a = Run(name, Seed, out _);
            List<string> b = Run(name, OtherSeed, out _);
            Assert.NotEqual(a[a.Count - 1], b[b.Count - 1]);
        }

        [Fact]
        public void Scenarios_ExerciseWhatTheyClaim()
        {
            Run("calles-prioridad", Seed, out GoldenScenarios.Built a);
            Assert.True(a.Sim.Trips.Completed > 50, "completados " + a.Sim.Trips.Completed);
            Assert.Equal(0, ((JunctionController)a.Sim.Gate).SignalCount);

            Run("avenidas-semaforos", Seed, out GoldenScenarios.Built b);
            Assert.True(b.Sim.Trips.Completed > 50, "completados " + b.Sim.Trips.Completed);
            Assert.True(((JunctionController)b.Sim.Gate).SignalCount > 0, "sin semaforos");

            Run("manzana-gridlock", Seed, out GoldenScenarios.Built c);
            GridlockStats g = c.Sim.Metrics.Gridlock;
            Assert.True(g.Cycles > 0, "no se provoco el bloqueo total");
        }
    }
}
