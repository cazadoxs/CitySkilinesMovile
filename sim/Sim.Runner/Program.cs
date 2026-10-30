using System;

namespace Urbe.Sim.Runner
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            Console.WriteLine("URBE Sim.Runner v0.1.0");
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--bench")
                {
                    Console.WriteLine("--bench: pendiente M1-10/M1-12");
                }
                else if (args[i] == "--render")
                {
                    string path = i + 1 < args.Length ? args[++i] : "<ruta>";
                    Console.WriteLine("--render " + path + ": pendiente M1-10/M1-12");
                }
            }
            return 0;
        }
    }
}
