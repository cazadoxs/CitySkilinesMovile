using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Urbe.Sim.Runner
{
    /// <summary>
    /// <c>--bench</c> (GDD 19.6, M1-12): cuadriculas con 1.000 / 3.000 / 6.000 / 16.000 vehiculos (presupuestos de GDD 8.15), calentamiento
    /// hasta llenar la red y luego N ticks medidos. Mide <c>TrafficSimulation.Step()</c> completo (viajes, rutas, movimiento, cruces,
    /// bloqueo total y metricas). Informativo: no hay umbrales.
    /// </summary>
    public static class Bench
    {
        private sealed class Result
        {
            public int Target, Cells, Warmup, Measured, Alive, Segments;
            public double Mean, P50, P95, Max, BytesPerTick, Flow;
        }

        public static int Run(bool quick, string? jsonPath, ulong seed)
        {
            int[] sizes = quick ? new[] { 1000, 3000 } : new[] { 1000, 3000, 6000, 16000 };
            int warmMax = quick ? 1500 : 3000, measure = quick ? 150 : 500;
            CultureInfo inv = CultureInfo.InvariantCulture;
            Console.WriteLine("Bench: " + (quick ? "quick" : "completo") + ", semilla " + seed + ", " + (IntPtr.Size * 8) + " bit, " + Environment.ProcessorCount
                + " nucleos, " + (Stopwatch.IsHighResolution ? "cronometro de alta resolucion" : "cronometro basico"));
            Console.WriteLine("objetivo | celdas | segmentos | calent. | ticks | activos | flujo % | ms/tick media | p50 | p95 | max | B/tick");
            var results = new Result[sizes.Length];
            for (int k = 0; k < sizes.Length; k++)
            {
                results[k] = One(sizes[k], seed, warmMax, measure);
                Result r = results[k];
                Console.WriteLine(string.Format(inv, "{0,8} | {1,6} | {2,9} | {3,7} | {4,5} | {5,7} | {6,7:F1} | {7,13:F3} | {8:F3} | {9:F3} | {10:F3} | {11:F0}",
                    r.Target, r.Cells, r.Segments, r.Warmup, r.Measured, r.Alive, r.Flow, r.Mean, r.P50, r.P95, r.Max, r.BytesPerTick));
            }
            if (jsonPath != null) WriteJson(jsonPath, results, quick, seed);
            return 0;
        }

        private static Result One(int target, ulong seed, int warmMax, int measure)
        {
            int cells = (int)Math.Ceiling(Math.Sqrt(target / 5.0));
            float tps = target / 60f;
            int pathsPerTick = (int)Math.Ceiling(tps * 0.1f * 2f) + 8;
            Scenario sc = Scenarios.Grid(seed, target, tps, cells, 100f, pathsPerTick);
            int warm = 0;
            while (warm < warmMax && (warm < 300 || sc.Vehicles.AliveCount < target * 0.95))
            {
                sc.Step();
                warm++;
            }
            var ms = new double[measure];
            long alloc0 = GC.GetAllocatedBytesForCurrentThread();
            double sumAlive = 0;
            for (int t = 0; t < measure; t++)
            {
                long a = Stopwatch.GetTimestamp();
                sc.Step();
                ms[t] = (Stopwatch.GetTimestamp() - a) * 1000.0 / Stopwatch.Frequency;
                sumAlive += sc.Vehicles.AliveCount;
            }
            long alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
            double sum = 0, max = 0;
            for (int t = 0; t < measure; t++) { sum += ms[t]; if (ms[t] > max) max = ms[t]; }
            Array.Sort(ms);
            return new Result
            {
                Target = target, Cells = cells, Segments = sc.Network.SegmentCount, Warmup = warm, Measured = measure,
                Alive = (int)Math.Round(sumAlive / measure), Flow = sc.Sim.Metrics.CityFlowPercent,
                Mean = sum / measure, P50 = ms[measure / 2], P95 = ms[(int)(measure * 0.95)], Max = max,
                BytesPerTick = alloc / (double)measure,
            };
        }

        private static void WriteJson(string path, Result[] rs, bool quick, ulong seed)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("{\n  \"quick\": ").Append(quick ? "true" : "false").Append(",\n  \"seed\": ").Append(seed.ToString(inv))
              .Append(",\n  \"cores\": ").Append(Environment.ProcessorCount.ToString(inv)).Append(",\n  \"results\": [\n");
            for (int i = 0; i < rs.Length; i++)
            {
                Result r = rs[i];
                sb.Append("    { \"target\": ").Append(r.Target.ToString(inv))
                  .Append(", \"cells\": ").Append(r.Cells.ToString(inv))
                  .Append(", \"segments\": ").Append(r.Segments.ToString(inv))
                  .Append(", \"warmupTicks\": ").Append(r.Warmup.ToString(inv))
                  .Append(", \"measuredTicks\": ").Append(r.Measured.ToString(inv))
                  .Append(", \"activeVehicles\": ").Append(r.Alive.ToString(inv))
                  .Append(", \"flowPercent\": ").Append(r.Flow.ToString("0.0", inv))
                  .Append(", \"msMean\": ").Append(r.Mean.ToString("0.0000", inv))
                  .Append(", \"msP50\": ").Append(r.P50.ToString("0.0000", inv))
                  .Append(", \"msP95\": ").Append(r.P95.ToString("0.0000", inv))
                  .Append(", \"msMax\": ").Append(r.Max.ToString("0.0000", inv))
                  .Append(", \"bytesPerTick\": ").Append(r.BytesPerTick.ToString("0.0", inv))
                  .Append(" }").Append(i + 1 < rs.Length ? ",\n" : "\n");
            }
            sb.Append("  ]\n}\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
