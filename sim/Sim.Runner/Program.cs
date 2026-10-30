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
            string? png = null, gif = null;
            string scenarioName = "grid";
            string layerName = "vehicles";
            float seconds = 20f;
            ulong seed = 1;
            int vehicles = 1000, size = 800, frameTicks = 10;

            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--bench": Console.WriteLine("--bench: pendiente M1-12"); break;
                        case "--render": png = Next(args, ref i); break;
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

            if (png == null && gif == null) return 0;
            if (layerName != "vehicles" && layerName != "flow") { Console.Error.WriteLine("--layer: vehicles | flow"); return 2; }
            if (frameTicks < 1) frameTicks = 1;

            Scenario sc = Scenarios.Create(scenarioName, seed, vehicles);
            RenderLayer layer = layerName == "flow" ? RenderLayer.Flow : RenderLayer.Vehicles;
            int totalTicks = (int)Math.Round(seconds / sc.Vehicles.Config.Dt);
            using var renderer = new SceneRenderer(sc, size, layer);

            GifWriter? writer = null;
            var indices = new byte[size * size];
            try
            {
                if (gif != null)
                {
                    int delay = Math.Max(1, (int)Math.Round(100.0 / 10.0)); // 10 fps de GIF
                    writer = new GifWriter(File.Create(gif), size, size, FixedPalette.Build(), delay);
                }
                for (int t = 1; t <= totalTicks; t++)
                {
                    sc.Step();
                    if (writer != null && t % frameTicks == 0)
                    {
                        renderer.Draw();
                        renderer.ToIndexed(indices);
                        writer.AddFrame(indices);
                    }
                }
                renderer.Draw();
                if (png != null) renderer.SavePng(png);
                Console.WriteLine("Simulados " + totalTicks + " ticks (" + seconds.ToString("0.#", CultureInfo.InvariantCulture)
                    + " s), vehiculos vivos " + sc.Vehicles.AliveCount + (writer != null ? ", fotogramas GIF " + writer.FrameCount : ""));
            }
            finally
            {
                writer?.Dispose();
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
