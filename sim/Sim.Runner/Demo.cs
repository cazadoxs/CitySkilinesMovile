using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SkiaSharp;
using Urbe.Sim.Commands;
using Urbe.Sim.Runner.Render;

namespace Urbe.Sim.Runner
{
    /// <summary>
    /// Demo de cierre de M1 (M1-14): escenario "bottleneck". Dos zonas unidas por un unico puente; a mitad de la simulacion
    /// se encola un <see cref="BuildRoadCommand"/> con una avenida 2+2 y el atasco se disuelve. Genera un GIF (fotogramas con rotulo)
    /// y un PNG resumen (antes / despues + grafico de flujo %).
    /// </summary>
    public static class Demo
    {
        public const int Width = 960, Height = 440;

        public static int Run(string? gifPath, string? pngPath, float seconds, ulong seed, int vehicles, float tripsPerSecond, int frameTicks, float openAt, float tripsAfter, float trunk)
        {
            Scenarios.TrunkWeight = trunk;
            Scenario sc = Scenarios.Bottleneck(seed, vehicles, tripsPerSecond);
            Scenario control = Scenarios.Bottleneck(seed, vehicles, tripsPerSecond); // mismo escenario sin la avenida nueva
            var controlFlow = new List<float>();
            float dt = sc.Vehicles.Config.Dt;
            int totalTicks = (int)Math.Round(seconds / dt);
            int openTick = (int)Math.Round(openAt / dt);
            int sampleEvery = (int)Math.Round(1f / dt);
            var flow = new List<float>();
            var active = new List<int>();

            using var renderer = new SceneRenderer(sc, Width, RenderLayer.Vehicles, Height);
            using var beforeBmp = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var afterBmp = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            GifWriter? writer = null;
            var indices = new byte[Width * Height];
            // Instantanea "antes": unos 5 s simulados antes de abrir la via (el atasco ya esta formado).
            int beforeTick = Math.Max(1, openTick - (int)Math.Round(5f / dt));
            int afterTick = Math.Min(totalTicks, openTick + (int)Math.Round(300f / dt));
            try
            {
                if (gifPath != null)
                {
                    const int delay = 20; // centesimas: 5 fotogramas por segundo
                    writer = new GifWriter(File.Create(gifPath), Width, Height, FixedPalette.Build(), delay);
                }
                for (int t = 1; t <= totalTicks; t++)
                {
                    if (t == openTick && sc.Relief.HasValue) sc.Sim.Commands.Enqueue(new BuildRoadCommand(sc.Relief.Value));
                    if (t == openTick) { sc.Sim.Trips.Config.TripsPerSecond = tripsAfter; control.Sim.Trips.Config.TripsPerSecond = tripsAfter; }
                    sc.Step();
                    control.Step();
                    if (t > openTick && t % 50 == 0) Reroute(sc);
                    if (t == openTick) Console.WriteLine("Avenida nueva: comandos rechazados " + sc.Sim.Commands.LastRejected + ", segmentos " + sc.Network.SegmentCount);
                    if (t % sampleEvery == 0)
                    {
                        flow.Add(sc.Sim.Metrics.CityFlowPercent);
                        controlFlow.Add(control.Sim.Metrics.CityFlowPercent);
                        active.Add(sc.Vehicles.AliveCount);
                        if ((t / sampleEvery) % 10 == 0)
                            Console.WriteLine("t=" + (t * dt).ToString("0", CultureInfo.InvariantCulture) + " s  activos " + sc.Vehicles.AliveCount + "  flujo " + sc.Sim.Metrics.CityFlowPercent.ToString("0.0", CultureInfo.InvariantCulture)
                                + " %  generados " + sc.Sim.Trips.Generated + " completados " + sc.Sim.Trips.Completed + " perdidos " + sc.Sim.Trips.Lost + " re-rutas " + sc.Sim.Trips.Rerouted);
                    }
                    bool wantGif = writer != null && t % frameTicks == 0;
                    bool wantBefore = t == beforeTick, wantAfter = t == afterTick;
                    if (wantGif || wantBefore || wantAfter)
                    {
                        Annotate(renderer, t * dt, openAt, sc.Sim.Metrics.CityFlowPercent);
                        renderer.Draw();
                        if (wantGif) { renderer.ToIndexed(indices); writer!.AddFrame(indices); }
                        if (wantBefore) renderer.Bitmap.CopyTo(beforeBmp);
                        if (wantAfter) renderer.Bitmap.CopyTo(afterBmp);
                    }
                }
                if (pngPath != null) Summary(pngPath, beforeBmp, afterBmp, flow, controlFlow, active, openAt, vehicles);
                int peakActive = 0;
                foreach (int a in active) peakActive = Math.Max(peakActive, a);
                Console.WriteLine("Pico de vehiculos activos: " + peakActive);
                Report(flow, openAt, writer?.FrameCount ?? 0);
                Console.WriteLine("Control sin avenida: flujo final " + Mean(controlFlow, controlFlow.Count - 60, controlFlow.Count).ToString("0.0", CultureInfo.InvariantCulture) + " %, activos " + control.Vehicles.AliveCount);
                var gl = sc.Sim.Metrics.Gridlock;
                Console.WriteLine("Bloqueo total: ciclos " + gl.Cycles + ", pasos forzados " + gl.ForcedPasses + "/" + gl.ForcedAttempts + ", re-rutas " + gl.Reroutes + ", bajas " + gl.Despawns + ", sin resolver " + gl.Unresolved + "; pendientes " + sc.Sim.Trips.Pending + ", descartados " + sc.Sim.Trips.Dropped);
            }
            finally
            {
                writer?.Dispose();
            }
            return 0;
        }

        /// <summary>
        /// Aviso de navegacion: tras abrir la via, los vehiculos que llevan un rato detenidos al frente de su carril piden ruta nueva
        /// (<see cref="Urbe.Sim.Vehicles.VehicleSystem.MarkForReroute"/>; el generador de viajes se la rehace con la congestion publicada).
        /// </summary>
        private static void Reroute(Scenario sc)
        {
            var vs = sc.Vehicles;
            for (int i = 0; i < vs.HighWater; i++)
            {
                ref readonly Urbe.Sim.Vehicles.Vehicle v = ref vs.GetVehicle(i);
                if (!v.IsAlive || v.V > 0.3f || v.WaitTicks < 30) continue;
                vs.MarkForReroute(vs.IdOf(i));
            }
        }

        private static void Annotate(SceneRenderer r, float t, float openAt, float flowPercent)
        {
            var white = new SKColor(235, 238, 242);
            var red = new SKColor(255, 110, 90);
            var green = new SKColor(120, 235, 130);
            if (t < 40f) { r.Caption = "Llegan los primeros coches al puente"; r.CaptionColor = white; }
            else if (t < openAt) { r.Caption = "Atasco en el unico puente"; r.CaptionColor = red; }
            else if (t < openAt + 40f) { r.Caption = "Nueva avenida abierta"; r.CaptionColor = green; }
            else if (flowPercent < 60f) { r.Caption = "La cola se reparte entre el puente y la avenida"; r.CaptionColor = green; }
            else { r.Caption = "Atasco resuelto"; r.CaptionColor = green; }
        }

        private static float Mean(List<float> v, int from, int to)
        {
            from = Math.Max(0, from); to = Math.Min(v.Count, to);
            if (to <= from) return 0f;
            double s = 0;
            for (int i = from; i < to; i++) s += v[i];
            return (float)(s / (to - from));
        }

        private static void Report(List<float> flow, float openAt, int frames)
        {
            int o = (int)openAt;
            float min = 100f; int minAt = 0;
            for (int i = 0; i < flow.Count; i++) { if (i > 30 && flow[i] < min) { min = flow[i]; minAt = i; } }
            Console.WriteLine("Flujo minimo " + min.ToString("0.0", CultureInfo.InvariantCulture) + " % en t=" + minAt + " s");
            Console.WriteLine("Flujo medio antes (" + (o - 30) + "-" + o + " s): " + Mean(flow, o - 30, o).ToString("0.0", CultureInfo.InvariantCulture)
                + " %; despues (ultimos 60 s): " + Mean(flow, flow.Count - 60, flow.Count).ToString("0.0", CultureInfo.InvariantCulture) + " %; fotogramas GIF " + frames);
        }

        private static void Summary(string pngPath, SKBitmap before, SKBitmap after, List<float> flow, List<float> controlFlow, List<int> active, float openAt, int maxVeh)
        {
            const int chartH = 320, pad = 12;
            int peak = 1;
            foreach (int a in active) peak = Math.Max(peak, a);
            int w = Width * 2 + pad * 3, h = Height + chartH + pad * 3 + 40;
            using var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var c = new SKCanvas(bmp);
            c.Clear(new SKColor(16, 19, 24));
            using var font = new SKFont(SKTypeface.Default, 18f);
            using var small = new SKFont(SKTypeface.Default, 14f);
            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(235, 238, 242) };
            using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f };
            c.DrawText("URBE · M1 · atasco en un unico puente y avenida nueva (" + peak + " vehiculos activos en el pico)", pad, 26, SKTextAlign.Left, font, fill);
            c.DrawBitmap(before, pad, 40 + pad);
            c.DrawBitmap(after, Width + pad * 2, 40 + pad);

            float cx0 = pad + 56, cx1 = w - pad - 20, cy0 = Height + 40 + pad * 2 + 20, cy1 = h - pad - 34;
            float total = Math.Max(1, flow.Count);
            float X(float s) => cx0 + (cx1 - cx0) * s / total;
            float Y(float pct) => cy1 - (cy1 - cy0) * pct / 100f;
            line.StrokeWidth = 1f;
            for (int p = 0; p <= 100; p += 20)
            {
                line.Color = new SKColor(255, 255, 255, 40);
                c.DrawLine(cx0, Y(p), cx1, Y(p), line);
                c.DrawText(p + " %", pad, Y(p) + 5, SKTextAlign.Left, small, fill);
            }
            for (int s = 0; s <= total; s += 50)
                c.DrawText(s + " s", X(s) - 12, cy1 + 20, SKTextAlign.Left, small, fill);
            // Umbrales de la tarjeta: < 40 % atasco, > 60 % recuperado.
            line.Color = new SKColor(255, 110, 90, 150); c.DrawLine(cx0, Y(40), cx1, Y(40), line);
            line.Color = new SKColor(120, 235, 130, 150); c.DrawLine(cx0, Y(60), cx1, Y(60), line);
            // Vehiculos activos (escala 0..maxVeh) y flujo %.
            using var curve = new SKPath();
            for (int i = 0; i < active.Count; i++)
            {
                float x = X(i + 1), y = Y(100f * active[i] / peak);
                if (i == 0) curve.MoveTo(x, y); else curve.LineTo(x, y);
            }
            line.StrokeWidth = 2f; line.Color = new SKColor(140, 160, 200, 200); c.DrawPath(curve, line);
            curve.Rewind();
            for (int i = 0; i < flow.Count; i++)
            {
                float x = X(i + 1), y = Y(flow[i]);
                if (i == 0) curve.MoveTo(x, y); else curve.LineTo(x, y);
            }
            line.StrokeWidth = 3f; line.Color = new SKColor(255, 200, 60); c.DrawPath(curve, line);
            curve.Rewind();
            for (int i = 0; i < controlFlow.Count; i++)
            {
                float x = X(i + 1), y = Y(controlFlow[i]);
                if (i == 0) curve.MoveTo(x, y); else curve.LineTo(x, y);
            }
            line.StrokeWidth = 2.5f; line.Color = new SKColor(255, 110, 90, 220); c.DrawPath(curve, line);
            // Marca de apertura.
            line.StrokeWidth = 2f; line.Color = new SKColor(120, 235, 130);
            c.DrawLine(X(openAt), cy0, X(openAt), cy1, line);
            fill.Color = new SKColor(120, 235, 130);
            c.DrawText("t = " + (int)openAt + " s: se abre la avenida", X(openAt) + 8, cy1 - 10, SKTextAlign.Left, small, fill);
            fill.Color = new SKColor(255, 200, 60);
            c.DrawText("flujo % (amarillo)", cx0 + 8, cy0 + 16, SKTextAlign.Left, small, fill);
            fill.Color = new SKColor(255, 110, 90);
            c.DrawText("flujo % sin la avenida (rojo, misma simulacion de control)", cx0 + 8, cy0 + 52, SKTextAlign.Left, small, fill);
            fill.Color = new SKColor(140, 160, 200);
            c.DrawText("vehiculos activos (azul, 100 % = pico de " + peak + ")", cx0 + 8, cy0 + 34, SKTextAlign.Left, small, fill);
            fill.Color = new SKColor(235, 238, 242);
            c.DrawText("Izquierda: justo antes de abrir la via (t = " + (int)(openAt - 5f) + " s). Derecha: 300 s despues de abrirla.", pad, h - 12, SKTextAlign.Left, small, fill);

            using SKImage img = SKImage.FromBitmap(bmp);
            using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = File.Create(pngPath);
            data.SaveTo(fs);
        }
    }
}
