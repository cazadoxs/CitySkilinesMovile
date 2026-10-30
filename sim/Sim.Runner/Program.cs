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
            int vehicles = 1000, size = 800, frameTicks = 10;
            float tripsPerSecond = 8f;

            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--bench": Console.WriteLine("--bench: pendiente M1-12"); break;
                        case "--render": png = Next(args, ref i); break;
                        case "--csv": csv = Next(args, ref i); break;
                        case "--trips-per-sec": tripsPerSecond = float.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                        case "--gif": gif = Next(args, ref i); break;
                        case "--seconds": seconds = float.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                        case "--seed": seed = ulong.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                        case "--scenario": scenarioName = Next(args, ref i); break;
                        case "--layer": layerName = Next(args, ref i); break;
                        case "--vehicles": vehicles = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                        case "--size": size = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                        case "--frame-ticks": frameTicks = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                        default: Console.Error.WriteLine("Argumento desconocido: " + args[i]); return 2;
                    }
                }
            }
            catch (Exception e) when (e is FormatException || e is ArgumentException)
            {
                Console.Error.WriteLine("Argumentos invalidos: " + e.Message);
                return 2;
            }

            if (png == null && gif == null && csv == null) return 0;
            if (layerName != "vehicles" && layerName != "flow") { Console.Error.WriteLine("--layer: vehicles | flow"); return 2; }
            if (frameTicks < 1) frameTicks = 1;

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
