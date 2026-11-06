using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using SkiaSharp;
using Urbe.Sim.City;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Urbe.Sim.Runner.Bot;

namespace Urbe.Sim.Runner
{
    /// <summary>
    /// Escenario "city" (M3-06): un bot juega una ciudad desde el mapa vacio hasta una poblacion objetivo. Escribe un CSV con una fila por
    /// dia de juego (poblacion, demanda, dinero, trafico, ms por tick...) y opcionalmente un PNG del mapa final.
    /// </summary>
    public static class CityRun
    {
        public sealed class Result
        {
            public bool Reached;
            public long Ticks;
            public int Days;
            public int Population;
            public ulong Hash;
            public double ReachMsPerTick;
            public double HoldAvgMs, HoldP95Ms, HoldMaxMs;
            public int HoldVehiclesAlive;
        }

        public static int Run(ulong seed, int targetPop, int maxDays, string? csvPath, string? pngPath, int holdTicks, bool verbose)
        {
            Result r = Play(seed, targetPop, maxDays, csvPath, pngPath, holdTicks, verbose, out CitySimulation city);
            Console.WriteLine("Ciudad: " + (r.Reached ? "alcanzo " : "NO alcanzo ") + targetPop + " habitantes; poblacion " + r.Population + " en el dia " + r.Days
                + " (" + r.Ticks + " ticks), hash " + r.Hash.ToString("X16", CultureInfo.InvariantCulture));
            if (r.Reached)
                Console.WriteLine("Coste por tick: media hasta el objetivo " + r.ReachMsPerTick.ToString("0.000", CultureInfo.InvariantCulture)
                    + " ms; con la ciudad ya en " + r.Population + " habitantes (" + holdTicks + " ticks): media " + r.HoldAvgMs.ToString("0.000", CultureInfo.InvariantCulture)
                    + " ms, p95 " + r.HoldP95Ms.ToString("0.000", CultureInfo.InvariantCulture) + " ms, maximo " + r.HoldMaxMs.ToString("0.000", CultureInfo.InvariantCulture)
                    + " ms, vehiculos fisicos vivos " + r.HoldVehiclesAlive);
            return r.Reached ? 0 : 1;
        }

        public static Result Play(ulong seed, int targetPop, int maxDays, string? csvPath, string? pngPath, int holdTicks, bool verbose, out CitySimulation city)
        {
            city = new CitySimulation(seed);
            CitySimulation c = city;
            var bot = new CityBot(c);
            StreamWriter? csv = null;
            if (csvPath != null)
            {
                csv = new StreamWriter(csvPath, false, new System.Text.UTF8Encoding(false));
                csv.Write("day,week,hito,poblacion,hogares,edificios,abandonados,demanda_R,demanda_C,demanda_I,demanda_O,dinero,ingresos_sem,gastos_sem,empleos,parados,felicidad,educacion,"
                    + "luz_prod,luz_dem,agua_prod,agua_dem,vehiculos,viajes_fisicos,viajes_abstractos,viajes_a_pie,viajes_transporte,viajes_perdidos,camiones,flujo_pct,calles,km_calles,servicios,ms_tick\n");
            }
            var r = new Result();
            var sw = new Stopwatch();
            double windowMs = 0;
            long windowTicks = 0;
            double totalMs = 0;
            long total = 0;
            int think = 50;
            int logEvery = CityClock.TicksPerDay;
            int lastLoggedDay = -1;
            long maxTicks = (long)maxDays * CityClock.TicksPerDay;
            // Primero se levanta lo basico: el bot piensa en el tick 0 para tener luz, agua y la primera calle antes de que llegue nadie.
            bot.Think();
            while (total < maxTicks)
            {
                sw.Restart();
                c.Step();
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds;
                windowMs += ms; windowTicks++; totalMs += ms; total++;
                if (total % think == 0) bot.Think();
                int day = c.Clock.Day;
                if (total % logEvery == 0 && day != lastLoggedDay)
                {
                    lastLoggedDay = day;
                    if (csv != null) WriteRow(csv, c, bot, windowMs / Math.Max(1, windowTicks));
                    if (verbose && day % 10 == 0) Console.WriteLine(Summary(c, bot, windowMs / Math.Max(1, windowTicks)));
                    windowMs = 0; windowTicks = 0;
                }
                if (c.Stats.Population >= targetPop) { r.Reached = true; break; }
            }
            r.Ticks = total;
            r.Days = c.Clock.Day;
            r.Population = c.Stats.Population;
            r.ReachMsPerTick = totalMs / Math.Max(1, total);
            if (r.Reached && holdTicks > 0)
            {
                var samples = new double[holdTicks];
                for (int t = 0; t < holdTicks; t++)
                {
                    sw.Restart();
                    c.Step();
                    sw.Stop();
                    samples[t] = sw.Elapsed.TotalMilliseconds;
                    if ((total + t + 1) % think == 0) bot.Think();
                    if ((total + t + 1) % logEvery == 0 && csv != null) WriteRow(csv, c, bot, samples[t]);
                }
                Array.Sort(samples);
                double sum = 0;
                foreach (double s in samples) sum += s;
                r.HoldAvgMs = sum / samples.Length;
                r.HoldP95Ms = samples[(int)(samples.Length * 0.95)];
                r.HoldMaxMs = samples[samples.Length - 1];
                r.HoldVehiclesAlive = c.Traffic.Vehicles.AliveCount;
                r.Population = c.Stats.Population;
                r.Ticks = total + holdTicks;
            }
            r.Hash = c.ComputeHash();
            csv?.Dispose();
            if (pngPath != null) CityMapRenderer.Save(c, pngPath, 1400);
            return r;
        }

        private static string Summary(CitySimulation c, CityBot bot, double msTick)
        {
            CityStats s = c.Stats;
            CultureInfo inv = CultureInfo.InvariantCulture;
            return "dia " + c.Clock.Day + " (" + c.MilestoneName + ") pob " + s.Population + " edif " + s.Buildings + " dinero " + c.Money + " demanda R/C/I/O "
                + c.Demand.R.ToString("0", inv) + "/" + c.Demand.C.ToString("0", inv) + "/" + c.Demand.I.ToString("0", inv) + "/" + c.Demand.O.ToString("0", inv)
                + " paro " + s.Unemployed + " feliz " + s.AverageHappiness.ToString("0", inv) + " veh " + c.Traffic.Vehicles.AliveCount
                + " ms/tick " + msTick.ToString("0.000", inv) + " libres R/C/I " + c.Zones.CountFreeFront(ZoneType.Residential) + "/" + c.Zones.CountFreeFront(ZoneType.Commercial) + "/" + c.Zones.CountFreeFront(ZoneType.Industrial)
                + " luz " + c.Supply.PowerProduction.ToString("0", inv) + "/" + c.Supply.PowerDemand.ToString("0", inv) + " agua " + c.Supply.WaterProduction.ToString("0", inv) + "/" + c.Supply.WaterDemand.ToString("0", inv)
                + " calles " + bot.RoadsBuilt + " serv " + bot.ServicesBuilt + " fallos " + bot.FailedAttempts + " " + bot.Status;
        }

        private static void WriteRow(StreamWriter w, CitySimulation c, CityBot bot, double msTick)
        {
            CityStats s = c.Stats;
            CultureInfo inv = CultureInfo.InvariantCulture;
            WeekReport wk = c.Economy.LastWeek;
            c.Economy.Estimate(out long inc, out long exp);
            long services = 0;
            for (int i = 0; i < c.Buildings.IndexBound; i++) if (c.Buildings.IsAlive(i) && c.Buildings.Get(i).Kind == BuildingKind.Service) services++;
            float km = 0f;
            for (int i = 0; i < c.Network.SegmentIndexBound; i++) { EntityId id = c.Network.SegmentIdAt(i); if (id.IsValid) km += c.Network.GetSegment(id).Length; }
            string[] f =
            {
                c.Clock.Day.ToString(inv), c.Clock.Week.ToString(inv), c.MilestoneIndex.ToString(inv), s.Population.ToString(inv), s.Households.ToString(inv),
                s.Buildings.ToString(inv), s.AbandonedBuildings.ToString(inv), c.Demand.R.ToString("0.0", inv), c.Demand.C.ToString("0.0", inv), c.Demand.I.ToString("0.0", inv),
                c.Demand.O.ToString("0.0", inv), c.Money.ToString(inv), inc.ToString(inv), exp.ToString(inv), s.Jobs.ToString(inv), s.Unemployed.ToString(inv),
                s.AverageHappiness.ToString("0.0", inv), s.AverageEducation.ToString("0.00", inv), c.Supply.PowerProduction.ToString("0", inv), c.Supply.PowerDemand.ToString("0", inv),
                c.Supply.WaterProduction.ToString("0", inv), c.Supply.WaterDemand.ToString("0", inv), c.Traffic.Vehicles.AliveCount.ToString(inv),
                c.Trips.PhysicalTrips.ToString(inv), c.Trips.AbstractTrips.ToString(inv), c.Trips.WalkTrips.ToString(inv), c.Trips.TransitTrips.ToString(inv),
                c.Trips.TripsLost.ToString(inv), c.Trips.Trucks.ToString(inv), c.Traffic.Metrics.CityFlowPercent.ToString("0.0", inv), c.Network.SegmentCount.ToString(inv),
                (km / 1000f).ToString("0.00", inv), services.ToString(inv), msTick.ToString("0.000", inv),
            };
            w.Write(string.Join(",", f));
            w.Write('\n');
            _ = wk;
        }
    }

    /// <summary>Vista cenital de la ciudad: calles, celdas zonificadas, edificios por zona y nivel, servicios.</summary>
    public static class CityMapRenderer
    {
        public static void Save(CitySimulation c, string path, int size)
        {
            float half = c.Balance.MapHalfSize;
            // Se encuadra lo construido.
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < c.Network.NodeIndexBound; i++)
            {
                EntityId id = c.Network.NodeIdAt(i);
                if (!id.IsValid) continue;
                Vec2 p = c.Network.GetNode(id).Position;
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
            }
            minX -= 60f; maxX += 60f; minZ -= 60f; maxZ += 60f;
            float scale = Math.Min(size / (maxX - minX), size / (maxZ - minZ));
            int w = (int)((maxX - minX) * scale), h = (int)((maxZ - minZ) * scale);
            using var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(new SKColor(236, 240, 226));
            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
            SKPoint P(Vec2 v) => new SKPoint((v.X - minX) * scale, h - (v.Z - minZ) * scale);
            // Celdas zonificadas (suave).
            for (int i = 0; i < c.Zones.CellIndexBound; i++)
            {
                if (!c.Zones.IsCellAlive(i)) continue;
                ref readonly ZoneCell z = ref c.Zones.GetCell(i);
                if (z.Zone == ZoneType.None || z.Building >= 0) continue;
                fill.Color = ZoneColor(z.Zone).WithAlpha(70);
                SKPoint p = P(z.Pos);
                canvas.DrawRect(p.X - 4f * scale, p.Y - 4f * scale, 8f * scale, 8f * scale, fill);
            }
            // Calles.
            for (int s = 0; s < c.Network.SegmentIndexBound; s++)
            {
                EntityId id = c.Network.SegmentIdAt(s);
                if (!id.IsValid) continue;
                ref readonly Segment sg = ref c.Network.GetSegment(id);
                float width = c.Network.Types.Get(sg.RoadTypeId).TotalWidth;
                stroke.Color = sg.RoadTypeId == RoadTypeIds.Highway4 ? new SKColor(90, 90, 100) : new SKColor(120, 120, 128);
                stroke.StrokeWidth = Math.Max(1.5f, width * scale);
                SKPoint a = P(sg.Curve.P0), b = P(sg.Curve.P3);
                canvas.DrawLine(a, b, stroke);
            }
            // Edificios.
            for (int i = 0; i < c.Buildings.IndexBound; i++)
            {
                if (!c.Buildings.IsAlive(i)) continue;
                ref readonly Building b = ref c.Buildings.Get(i);
                SKColor col;
                if (b.Kind == BuildingKind.Service) col = ServiceColor((ServiceType)b.ServiceType);
                else
                {
                    col = ZoneColor(b.Zone);
                    float k = 0.55f + 0.1f * b.Level;
                    col = new SKColor((byte)(col.Red * k), (byte)(col.Green * k), (byte)(col.Blue * k));
                    if (b.IsAbandoned) col = new SKColor(60, 60, 60);
                }
                fill.Color = col;
                var u = new Vec2((float)Math.Sin(b.Yaw), (float)Math.Cos(b.Yaw));
                Vec2 v = u.Perp;
                float hw = b.Width * 4f - 0.5f, hd = b.Depth * 4f - 0.5f;
                using var shape = new SKPath();
                shape.MoveTo(P(b.Pos + u * hd + v * hw));
                shape.LineTo(P(b.Pos + u * hd - v * hw));
                shape.LineTo(P(b.Pos - u * hd - v * hw));
                shape.LineTo(P(b.Pos - u * hd + v * hw));
                shape.Close();
                canvas.DrawPath(shape, fill);
            }
            using var image = SKImage.FromBitmap(bmp);
            using SKData data = image.Encode(SKEncodedImageFormat.Png, 90);
            using FileStream fs = File.Create(path);
            data.SaveTo(fs);
            _ = half;
        }

        private static SKColor ZoneColor(ZoneType z)
        {
            switch (z)
            {
                case ZoneType.Residential: return new SKColor(80, 170, 90);
                case ZoneType.Commercial: return new SKColor(70, 120, 220);
                case ZoneType.Industrial: return new SKColor(222, 180, 50);
                case ZoneType.Office: return new SKColor(150, 90, 200);
                default: return new SKColor(200, 200, 200);
            }
        }

        private static SKColor ServiceColor(ServiceType t)
        {
            switch (t)
            {
                case ServiceType.WaterPump: return new SKColor(40, 190, 230);
                case ServiceType.SewageOutlet: return new SKColor(110, 90, 60);
                case ServiceType.WindTurbine: return new SKColor(250, 250, 250);
                case ServiceType.CoalPlant: return new SKColor(30, 30, 30);
                case ServiceType.Landfill: return new SKColor(140, 120, 90);
                case ServiceType.HealthCenter: return new SKColor(235, 60, 70);
                case ServiceType.FireStation: return new SKColor(230, 110, 30);
                case ServiceType.PoliceStation: return new SKColor(30, 60, 160);
                case ServiceType.School: case ServiceType.HighSchool: return new SKColor(250, 220, 60);
                case ServiceType.Park: return new SKColor(60, 220, 120);
                default: return new SKColor(255, 0, 255);
            }
        }
    }
}
