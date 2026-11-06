using System.Collections.Generic;
using Urbe.Game.Sim;
using Urbe.Sim.City;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Game.Tests
{
    /// <summary>
    /// Guion de una partida para las pruebas: hace con las órdenes del jugador (las mismas que envía la interfaz) lo que haría Álvaro: una calle
    /// desde la autopista, transversales, zonas, bomba de agua, desagüe y eólicas. Cuesta dinero de verdad.
    /// </summary>
    public static class CityScript
    {
        public static readonly Vec2 HighwayEnd = new Vec2(-838f, 0f);

        public struct Layout
        {
            public int MainSegments;   // tramos de 80 m de la calle principal hacia el este
            public int Crosses;        // calles transversales (cada 160 m), a cada lado
            public int CrossSegments;  // tramos de 80 m por lado
        }

        public static readonly Layout Medium = new Layout { MainSegments = 9, Crosses = 5, CrossSegments = 3 };
        public static readonly Layout Small = new Layout { MainSegments = 6, Crosses = 3, CrossSegments = 3 };

        /// <summary>Construye las calles; devuelve cuántas órdenes se aceptaron. Resultado de la primera rechazada en <paramref name="firstReject"/>.</summary>
        public static int BuildStreets(SimHost host, Layout l, out CityResult firstReject)
        {
            firstReject = CityResult.Ok;
            int ok = 0;
            Vec2 a = HighwayEnd;
            for (int i = 0; i < l.MainSegments; i++)
            {
                Vec2 b = new Vec2(a.X + 80f, 0f);
                ok += Road(host, a, b, ref firstReject);
                a = b;
            }
            for (int c = 0; c < l.Crosses; c++)
            {
                float x = HighwayEnd.X + 160f * (c + 1);
                for (int side = -1; side <= 1; side += 2)
                {
                    Vec2 p = new Vec2(x, 0f);
                    for (int i = 0; i < l.CrossSegments; i++)
                    {
                        Vec2 q = new Vec2(x, p.Z + 80f * side);
                        ok += Road(host, p, q, ref firstReject);
                        p = q;
                    }
                }
            }
            return ok;
        }

        private static int Road(SimHost host, Vec2 a, Vec2 b, ref CityResult firstReject)
        {
            CityResult r = host.Execute(new BuildRoadCityCommand(SegmentRequest.Straight(a, b, RoadTypeIds.Street2)));
            if (r != CityResult.Ok && firstReject == CityResult.Ok) firstReject = r;
            return r == CityResult.Ok ? 1 : 0;
        }

        /// <summary>Pinta zonas a ambos lados de cada calle: comercial en la principal, residencial al norte e industrial al sur.</summary>
        public static int PaintZones(SimHost host, int density = 1)
        {
            CitySimulation city = host.City!;
            RoadNetwork net = city.Network;
            int painted = 0;
            for (int i = 0; i < net.SegmentIndexBound; i++)
            {
                EntityId seg = net.SegmentIdAt(i);
                if (!seg.IsValid || seg == city.OutsideSegment) continue;
                Vec2 mid = net.GetSegment(seg).Curve.Evaluate(0.5f);
                ZoneType z = System.Math.Abs(mid.Z) < 5f ? ZoneType.Commercial : (mid.Z > 0f ? ZoneType.Residential : ZoneType.Industrial);
                if (host.Execute(PaintZoneCommand.FillSegment(seg, 4, z, density)) == CityResult.Ok) painted++;
            }
            return painted;
        }

        /// <summary>Bomba de agua, desagüe y dos turbinas eólicas junto a la calle principal, hacia la autopista (a la izquierda de las zonas).</summary>
        public static List<CityResult> PlaceUtilities(SimHost host)
        {
            var results = new List<CityResult>();
            results.Add(host.Execute(new PlaceServiceCommand(ServiceType.WaterPump, new Vec2(-818f, 38f))));
            results.Add(host.Execute(new PlaceServiceCommand(ServiceType.SewageOutlet, new Vec2(-818f, -38f))));
            results.Add(host.Execute(new PlaceServiceCommand(ServiceType.WindTurbine, new Vec2(-780f, 46f))));
            results.Add(host.Execute(new PlaceServiceCommand(ServiceType.WindTurbine, new Vec2(-780f, -46f))));
            results.Add(host.Execute(new PlaceServiceCommand(ServiceType.CoalPlant, new Vec2(-598f, 66f))));
            return results;
        }

        public static void Run(SimHost host, int ticks)
        {
            for (int i = 0; i < ticks; i++) host.StepOnce();
        }
    }
}
