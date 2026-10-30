using System;
using System.Globalization;
using System.IO;
using Urbe.Sim.Runner.Render;

namespace Urbe.Sim.Runner
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            Console.WriteLine("URBE Sim.Runner v0.1.0");
            string? png = null, gif = null, csv = null;
            string scenarioName = "grid";
            string layerName = "vehicles";
            float seconds = 20f;
            ulong seed = 1;
            bool bench = false, quick = false;
            string? benchJson = null;
            int vehicles = 1000, size = 800, frameTicks = 10;
            bool seedSet = false, vehiclesSet = false, secondsSet = false, tripsSet = false, frameSet = false;
            float? openAt = null;
            float tripsAfter = 0.3f, trunk = 4f;
            float tripsPerSecond = 3f; // M1-07b: con el JunctionController por defecto, 8 viajes/s saturan la cuadricula 8x8

            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--bench": bench = true; break;
                        case "--quick": quick = true; break;
                        case "--bench-json": benchJson = Next(args, ref i); break;
                        case "--render": png = Next(args, ref i); break;
                        case "--csv": csv = Next(args, ref i); break;
                        case "--trips-after": tripsAfter = float.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                        case "--trunk": trunk = float.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                        case "--open-at": openAt = float.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                        case "--trips-per-sec": tripsSet = true; tripsPerSecond = float.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                        case "--gif": gif = Next(args, ref i); break;
                        case "--seconds": secondsSet = true; seconds = float.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                        case "--seed": seedSet = true; seed = ulong.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                        case "--scenario": scenarioName = Next(args, ref i); break;
                        case "--layer": layerName = Next(args, ref i); break;
                        case "--vehicles": vehiclesSet = true; vehicles = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                        case "--size": size = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                        case "--frame-ticks": frameSet = true; frameTicks = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                        default: Console.Error.WriteLine("Argumento desconocido: " + args[i]); return 2;
                    }
                }
            }
            catch (Exception e) when (e is FormatException || e is ArgumentException)
            {
                Console.Error.WriteLine("Argumentos invalidos: " + e.Message);
                return 2;
            }

            if (bench) return Bench.Run(quick, benchJson, seed);
            if (png == null && gif == null && csv == null) return 0;
            if (layerName != "vehicles" && layerName != "flow") { Console.Error.WriteLine("--layer: vehicles | flow"); return 2; }
            if (frameTicks < 1) frameTicks = 1;

            if (scenarioName == "bottleneck")
            {
                if (!seedSet) seed = 7;
                if (!vehiclesSet) vehicles = 2000;
                if (!secondsSet) seconds = 1300f;
                if (!tripsSet) tripsPerSecond = 4f;
                if (!frameSet) frameTicks = 80;
                return Demo.Run(gif, png, seconds, seed, vehicles, tripsPerSecond, frameTicks, openAt ?? 120f, tripsAfter, trunk);
            }

            Scenario sc = Scenarios.Create(scenarioName, seed, vehicles, tripsPerSecond);
            RenderLayer layer = layerName == "flow" ? RenderLayer.Flow : RenderLayer.Vehicles;
            int totalTicks = (int)Math.Round(seconds / sc.Vehicles.Config.Dt);
            using SceneRenderer? renderer = png != null || gif != null ? new SceneRenderer(sc, size, layer) : null;

            GifWriter? writer = null;
            StreamWriter? csvWriter = null;
            var indices = new byte[size * size];
            try
            {
                if (csv != null)
                {
                    csvWriter = new StreamWriter(csv, false, new System.Text.UTF8Encoding(false));
                    sc.Sim.AttachCsv(csvWriter);
                }
                if (gif != null)
                {
                    int delay = Math.Max(1, (int)Math.Round(100.0 / 10.0)); // 10 fps de GIF
                    writer = new GifWriter(File.Create(gif), size, size, FixedPalette.Build(), delay);
                }
                for (int t = 1; t <= totalTicks; t++)
                {
                    sc.Step();
                    if (writer != null && renderer != null && t % frameTicks == 0)
                    {
                        renderer.Draw();
                        renderer.ToIndexed(indices);
                        writer.AddFrame(indices);
                    }
                }
                if (renderer != null)
                {
                    renderer.Draw();
                    if (png != null) renderer.SavePng(png);
                }
                Console.WriteLine("Simulados " + totalTicks + " ticks (" + seconds.ToString("0.#", CultureInfo.InvariantCulture)
                    + " s), vehiculos vivos " + sc.Vehicles.AliveCount + ", flujo " + sc.Sim.Metrics.CityFlowPercent.ToString("0.0", CultureInfo.InvariantCulture)
                    + " %, viajes generados " + sc.Sim.Trips.Generated + ", completados " + sc.Sim.Trips.Completed + ", pendientes " + sc.Sim.Trips.Pending + ", perdidos " + sc.Sim.Trips.Lost + " (sin ruta " + sc.Sim.Trips.Unroutable + "), descartados " + sc.Sim.Trips.Dropped + (writer != null ? ", fotogramas GIF " + writer.FrameCount : ""));
                Urbe.Sim.Traffic.GridlockStats gl = sc.Sim.Metrics.Gridlock;
                Console.WriteLine("Bloqueo total: ciclos " + gl.Cycles + ", pasos forzados " + gl.ForcedPasses + "/" + gl.ForcedAttempts + ", re-rutas " + gl.Reroutes + ", bajas " + gl.Despawns + ", sin resolver " + gl.Unresolved);
            }
            finally
            {
                writer?.Dispose();
                csvWriter?.Dispose();
            }
            return 0;
        }

        private static string Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) throw new ArgumentException("Falta el valor de " + args[i]);
            return args[++i];
        }
    }
}
