using System;
using System.Collections.Generic;
using Urbe.Sim.City;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Xunit;

namespace Urbe.Sim.Tests.City
{
    public class PopulationTests
    {
        private static float X0(CitySimulation c) => c.OutsidePosition.X + 150f;

        private static void Place(CitySimulation c, ServiceType t, float x, float z)
            => Assert.Equal(CityResult.Ok, c.Execute(new PlaceServiceCommand(t, CityKit.V(x, z))));

        private static CityBalance Open()
        {
            var bal = new CityBalance();
            foreach (ServiceInfo s in bal.Services) s.UnlockMilestone = 0;
            return bal;
        }

        /// <summary>Barrio completo con ciudadanos reales: casas, comercio al sur e industria al norte, con agua y luz.</summary>
        internal static CitySimulation RealTown(ulong seed = 5, CityBalance? bal = null, bool services = false)
        {
            CitySimulation c = CityKit.NewReal(seed, bal ?? Open());
            float x = X0(c);
            CityKit.MustRoad(c, x, 0, x + 160, 0);
            CityKit.MustRoad(c, x + 160, 0, x + 320, 0);
            CityKit.MustRoad(c, x + 160, -120, x + 160, 0);
            CityKit.MustRoad(c, x + 160, 0, x + 160, 120);
            Place(c, ServiceType.WaterPump, x + 20, 22);
            Place(c, ServiceType.WaterPump, x + 45, 22);
            Place(c, ServiceType.SewageOutlet, x + 20, -22);
            for (int i = 0; i < 4; i++) Place(c, ServiceType.WindTurbine, x + 45 + 22 * i, -22);
            for (int i = 0; i < 4; i++) Place(c, ServiceType.WindTurbine, x + 72 + 22 * i, 22);
            c.Step();
            c.Execute(PaintZoneCommand.FillSegment(CityKit.SegmentAt(c, x + 100, 0), 3, ZoneType.Residential, 1));
            c.Execute(PaintZoneCommand.Lasso(new[] { CityKit.V(x + 170, 1), CityKit.V(x + 315, 1), CityKit.V(x + 315, 45), CityKit.V(x + 170, 45) }, ZoneType.Industrial, 1));
            c.Execute(PaintZoneCommand.Lasso(new[] { CityKit.V(x + 170, -1), CityKit.V(x + 315, -1), CityKit.V(x + 315, -45), CityKit.V(x + 170, -45) }, ZoneType.Commercial, 1));
            c.Execute(PaintZoneCommand.FillSegment(CityKit.SegmentAt(c, x + 160, 60), 3, ZoneType.Residential, 1));
            c.Execute(PaintZoneCommand.FillSegment(CityKit.SegmentAt(c, x + 160, -60), 3, ZoneType.Residential, 1));
            if (services)
            {
                CityKit.MustRoad(c, x + 320, 0, x + 400, 0);
                Place(c, ServiceType.School, x + 360, 20);
            }
            return c;
        }

        [Fact]
        public void PeopleMoveInAndGetJobs()
        {
            CitySimulation c = RealTown();
            c.Run(30000);
            PopulationSystem p = c.Population;
            Assert.True(p.Count > 100, "poblacion " + p.Count);
            Assert.Equal(p.Count, c.Stats.Population);
            Assert.True(c.Stats.Population <= c.Stats.HousingCapacity);
            int employed = 0, adults = 0;
            for (int i = 0; i < p.CitizenIndexBound; i++)
            {
                if (!p.IsAlive(i)) continue;
                ref readonly Citizen z = ref p.Get(i);
                Assert.True(c.Buildings.IsAlive(p.GetHousehold(z.Household).Home));
                if (z.Age >= 18 && z.Age < 65) { adults++; if (z.Workplace.IsValid) { employed++; Assert.True(c.Buildings.IsAlive(z.Workplace)); } }
            }
            Assert.True(adults > 40);
            Assert.True(employed > adults / 3, employed + " empleados de " + adults);
            // Los contadores de los edificios cuadran con los ciudadanos (tras un barrido).
            c.RecomputeStats();
            long filled = 0;
            for (int i = 0; i < c.Buildings.IndexBound; i++)
                if (c.Buildings.IsAlive(i) && c.Buildings.Get(i).Kind == BuildingKind.Zoned && c.Buildings.Get(i).Zone != ZoneType.Residential)
                {
                    ref readonly Building b = ref c.Buildings.Get(i);
                    Assert.True(b.FilledLow <= b.JobsLow && b.FilledHigh <= b.JobsHigh);
                    filled += b.FilledLow + b.FilledHigh;
                }
            Assert.Equal(employed, (int)filled);
        }

        [Fact]
        public void TripsLeaveFromRealBuildingsAndArrive()
        {
            CitySimulation c = RealTown();
            c.Run(30000);
            // A primera hora de un dia laborable la gente sale de casa: busca el primer lunes por la manana.
            int guard = 0;
            while (!(c.Clock.DayOfWeek == 0 && c.Clock.Hour > 5.9f && c.Clock.Hour < 6.1f) && guard++ < 30000) c.Step();
            long before = c.Trips.PhysicalTrips + c.Trips.AbstractTrips + c.Trips.WalkTrips + c.Trips.TransitTrips;
            bool sawToWork = false, sawDest = false;
            for (int t = 0; t < 450; t++)
            {
                c.Step();
                if (t % 30 != 0) continue;
                for (int i = 0; i < c.Population.CitizenIndexBound; i++)
                {
                    if (!c.Population.IsAlive(i)) continue;
                    ref readonly Citizen z = ref c.Population.Get(i);
                    if (z.State != CitizenState.ToWork) continue;
                    sawToWork = true;
                    // El destino es su trabajo o colegio y el origen su casa.
                    Assert.Equal(z.Workplace.Index, z.Dest);
                    Assert.Equal(c.Population.GetHousehold(z.Household).Home.Index, z.Location);
                    sawDest = true;
                }
            }
            Assert.True(sawToWork && sawDest);
            long after = c.Trips.PhysicalTrips + c.Trips.AbstractTrips + c.Trips.WalkTrips + c.Trips.TransitTrips;
            Assert.True(after - before > 20, "viajes " + (after - before));
            Assert.True(c.Trips.PhysicalTrips > 0);
            // Pasadas las primeras horas, la mayoria ya esta en su trabajo.
            c.Run(675);
            var sc = new int[6];
            for (int i = 0; i < c.Population.CitizenIndexBound; i++) if (c.Population.IsAlive(i)) sc[(int)c.Population.Get(i).State]++;
            string st = string.Join(",", sc) + " phys=" + c.Trips.PhysicalTrips + " abs=" + c.Trips.AbstractTrips + " walk=" + c.Trips.WalkTrips + " transit=" + c.Trips.TransitTrips + " done=" + c.Trips.TripsCompleted + " lost=" + c.Trips.TripsLost + " hour=" + c.Clock.Hour + " day=" + c.Clock.DayOfWeek;
            int atWork = 0, employed = 0;
            for (int i = 0; i < c.Population.CitizenIndexBound; i++)
            {
                if (!c.Population.IsAlive(i)) continue;
                ref readonly Citizen z = ref c.Population.Get(i);
                if (z.Workplace.IsValid && z.Age >= 18) { employed++; if (z.State == CitizenState.AtWork) { atWork++; Assert.Equal(z.Workplace.Index, z.Location); } }
            }
            Assert.True(atWork > employed / 2, atWork + " en el trabajo de " + employed + " estados " + st);
            Assert.True(c.Trips.TripsCompleted > 20);
        }

        [Fact]
        public void OverTheVehicleCapTripsBecomeAbstractAndLoadTheSegments()
        {
            var bal = Open();
            bal.PhysicalVehicleCap = 3;
            CitySimulation c = RealTown(6, bal);
            c.Run(30000);
            int guard = 0;
            while (!(c.Clock.DayOfWeek == 0 && c.Clock.Hour > 5.9f && c.Clock.Hour < 6.1f) && guard++ < 30000) c.Step();
            float maxLoad = 0f;
            int maxAlive = 0;
            for (int t = 0; t < 1200; t++)
            {
                c.Step();
                maxAlive = Math.Max(maxAlive, c.Traffic.Vehicles.AliveCount);
                float[]? load = c.Traffic.ExtraLoad;
                if (load != null) for (int i = 0; i < load.Length; i++) maxLoad = Math.Max(maxLoad, load[i]);
            }
            Assert.True(c.Trips.AbstractTrips > 5, "abstractos " + c.Trips.AbstractTrips);
            Assert.True(maxAlive <= 3 + 2, "vehiculos fisicos " + maxAlive);
            Assert.True(maxLoad > 0f);
            // Terminados todos los viajes abstractos, la carga vuelve a cero (sin deriva).
            // Sin ocio ni compras y a mitad del domingo ya han terminado los viajes del viernes: la carga vuelve a cero (sin deriva).
            c.Balance.LeisureChance = 0f;
            c.Balance.ShopChance = 0f;
            guard = 0;
            while (!(c.Clock.DayOfWeek == 6 && c.Clock.Hour > 12f) && guard++ < 40000) c.Step();
            Assert.Equal(0, c.Trips.ActiveAbstract);
            float rest = 0f;
            for (int i = 0; i < c.Traffic.ExtraLoad!.Length; i++) rest += c.Traffic.ExtraLoad[i];
            Assert.True(rest < 0.001f, "carga residual " + rest);
            Assert.True(c.Trips.TripsLost < c.Trips.TripsCompleted);
        }

        [Fact]
        public void ExtraLoadLowersTheCongestionFactorTheRouterSees()
        {
            CitySimulation c = CityKit.New();
            CityKit.MustRoad(c, 0, 0, 100, 0);
            c.Traffic.ExtraLoad = new float[c.Network.SegmentIndexBound];
            int seg = CityKit.SegmentAt(c, 50, 0).Index;
            c.Run(350);
            Assert.True(c.Traffic.Paths.Congestion![seg] > 0.95f);
            c.Traffic.ExtraLoad[seg] = 1f;
            c.Run(350);
            Assert.InRange(c.Traffic.Paths.Congestion![seg], 0.4f, 0.55f);
        }

        [Fact]
        public void LifeCycleBirthsAgeingAndDeaths()
        {
            var bal = Open();
            bal.DaysPerAgeYear = 1;
            bal.BirthChancePerDay = 0.08f;
            CitySimulation c = RealTown(7, bal);
            c.Run(30000);
            int pop0 = c.Population.Count;
            Assert.True(pop0 > 60);
            c.Run(CityClock.TicksPerDay * 14);
            Assert.True(c.Population.Born > 0, "nacimientos " + c.Population.Born);
            // Con un ano por dia hay envejecimiento visible: aparecen mayores de 60 o mueren ancianos.
            int oldest = 0;
            for (int i = 0; i < c.Population.CitizenIndexBound; i++)
                if (c.Population.IsAlive(i)) oldest = Math.Max(oldest, c.Population.Get(i).Age);
            Assert.True(oldest > 25);
            Assert.True(c.Population.Died > 0 || oldest >= 55, "muertes " + c.Population.Died);
            // Ningun ciudadano vivo queda sin hogar ni en un hogar inexistente.
            for (int i = 0; i < c.Population.CitizenIndexBound; i++)
                if (c.Population.IsAlive(i)) Assert.True(c.Population.GetHousehold(c.Population.Get(i).Household).IsAlive);
        }

        [Fact]
        public void ChildrenGoToSchoolAndGetEducated()
        {
            var bal = Open();
            bal.DaysPerAgeYear = 1;
            CitySimulation c = RealTown(8, bal, services: true);
            c.Run(30000 + CityClock.TicksPerDay * 2);
            int students = 0, enrolled = 0;
            for (int i = 0; i < c.Population.CitizenIndexBound; i++)
            {
                if (!c.Population.IsAlive(i)) continue;
                ref readonly Citizen z = ref c.Population.Get(i);
                if ((z.Flags & CitizenFlags.Student) == 0) continue;
                students++;
                if (z.Workplace.IsValid && c.Buildings.Get(z.Workplace).Kind == BuildingKind.Service) enrolled++;
            }
            Assert.True(students > 3, "estudiantes " + students + " pop=" + c.Population.Count + " children=" + c.Stats.Children);
            Assert.True(enrolled > 0, "matriculados " + enrolled);
            // Los que fueron al colegio y ya cumplieron 16 tienen educacion 1 como minimo.
            c.Run(CityClock.TicksPerDay * 10);
            int attended = 0;
            for (int i = 0; i < c.Population.CitizenIndexBound; i++)
            {
                if (!c.Population.IsAlive(i)) continue;
                ref readonly Citizen z = ref c.Population.Get(i);
                if ((z.Flags & CitizenFlags.AttendedSchool) == 0 || z.Age < 16) continue;
                attended++;
                Assert.True(z.Education >= 1);
            }
            Assert.True(attended > 0, "titulados " + attended);
        }


        [Fact]
        public void LosingWaterEmptiesTheCity()
        {
            CitySimulation c = RealTown(9);
            c.Run(30000);
            int pop = c.Population.Count;
            Assert.True(pop > 80);
            var pumps = new List<EntityId>();
            for (int i = 0; i < c.Buildings.IndexBound; i++)
                if (c.Buildings.IsAlive(i) && c.Buildings.Get(i).ServiceType == (byte)ServiceType.WaterPump) pumps.Add(c.Buildings.Get(i).Id);
            foreach (EntityId id in pumps) Assert.Equal(CityResult.Ok, c.Execute(new DemolishServiceCommand(id)));
            c.Run(CityClock.TicksPerDay * 10);
            Assert.True(c.Population.Count < pop / 2, "poblacion " + c.Population.Count + " de " + pop);
            Assert.True(c.Population.Emigrated > 0);
            // Y con agua de nuevo la ciudad vuelve a llenarse.
            float x = X0(c);
            Place(c, ServiceType.WaterPump, x + 20, 22);
            Place(c, ServiceType.WaterPump, x + 45, 22);
            c.Run(CityClock.TicksPerDay * 12);
            Assert.True(c.Population.Count > 40);
        }

        [Fact]
        public void IndustryAndShopsReceiveTrucksFromTheOutside()
        {
            CitySimulation c = RealTown(10);
            c.Run(40000);
            Assert.True(c.Trips.Trucks > 0, "camiones " + c.Trips.Trucks);
        }


        [Fact]
        public void SteadyStateTicksBarelyAllocate()
        {
            CitySimulation c = RealTown(14);
            c.Run(40000);
            c.Run(2000);
            long before = GC.GetAllocatedBytesForCurrentThread();
            c.Run(3000);
            long perTick = (GC.GetAllocatedBytesForCurrentThread() - before) / 3000;
            Assert.True(perTick < 600, "bytes por tick: " + perTick);
        }
        [Fact]
        public void DeterministicWithCitizens()
        {
            ulong Run(ulong seed)
            {
                CitySimulation c = RealTown(seed);
                c.Run(32000);
                return c.ComputeHash();
            }
            Assert.Equal(Run(12), Run(12));
            Assert.NotEqual(Run(12), Run(13));
        }
    }
}
