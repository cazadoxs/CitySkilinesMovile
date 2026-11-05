using System;
using Urbe.Sim.City;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Xunit;

namespace Urbe.Sim.Tests.City
{
    public class EconomyTests
    {
        private static CityBalance Open()
        {
            var bal = new CityBalance();
            foreach (ServiceInfo s in bal.Services) s.UnlockMilestone = 0;
            return bal;
        }

        private static CitySimulation Town(CityBalance bal, ulong seed = 3)
        {
            CitySimulation c = CityKit.New(seed, bal);
            CityKit.MustRoad(c, -160, 0, 0, 0);
            CityKit.MustRoad(c, 0, 0, 160, 0);
            CityKit.MustRoad(c, 0, -160, 0, 0);
            CityKit.MustRoad(c, 0, 0, 0, 160);
            c.SupplyOverride = true;
            c.Step();
            foreach ((float x, float z, ZoneType zone, int d) in new[]
            {
                (-80f, 0f, ZoneType.Residential, 1), (80f, 0f, ZoneType.Residential, 1), (0f, -80f, ZoneType.Commercial, 1), (0f, 80f, ZoneType.Industrial, 1),
            })
                c.Execute(PaintZoneCommand.FillSegment(CityKit.SegmentAt(c, x, z), 3, zone, d));
            return c;
        }

        private static void RunToWeekBoundary(CitySimulation c)
        {
            int guard = 0;
            int before = c.Economy.HistoryCount;
            while (c.Economy.HistoryCount == before && guard++ < 200000) c.Step();
            Assert.True(guard < 200000);
        }

        [Fact]
        public void WeeklyReportMatchesMoneyAndEstimate()
        {
            CitySimulation c = Town(Open());
            c.Run(CityClock.TicksPerDay * 8);
            // El tick anterior al cierre de semana: la estimacion y el cobro son del mismo estado.
            int guard = 0;
            while (!(c.Clock.DayOfWeek == 6 && c.Clock.TickOfDay > CityClock.TicksPerDay - 3) && guard++ < 100000) c.Step();
            c.Economy.Estimate(out long estIncome, out long estExpenses);
            long money = c.Money;
            int weeks = c.Economy.HistoryCount;
            RunToWeekBoundary(c);
            WeekReport w = c.Economy.LastWeek;
            Assert.Equal(weeks + 1, c.Economy.HistoryCount);
            Assert.True(w.Income > 0 && w.TaxResidential > 0 && w.TaxCommercial > 0 && w.TaxIndustrial > 0);
            Assert.True(w.ServiceMaintenance == 0 && w.RoadMaintenance > 0);
            Assert.Equal(money + w.Income - w.ServiceMaintenance - w.RoadMaintenance, c.Money);
            Assert.InRange(w.Income, estIncome - 5, estIncome + 5);
            Assert.Equal(w.MoneyAtEnd, c.Money);
            // Historial acumulado.
            Assert.Equal(w.Week, c.Economy.History(c.Economy.HistoryCount - 1).Week);
        }

        [Fact]
        public void TaxRateScalesIncomeAndDemand()
        {
            CitySimulation c = Town(Open());
            c.Run(CityClock.TicksPerDay * 4);
            c.Economy.Estimate(out long at9, out _);
            Assert.True(at9 > 0);
            c.SetTaxRate(ZoneType.Residential, 18f);
            c.SetTaxRate(ZoneType.Commercial, 18f);
            c.SetTaxRate(ZoneType.Industrial, 18f);
            c.Economy.Estimate(out long at18, out _);
            Assert.InRange(at18, at9 * 2 - 3, at9 * 2 + 3);
            // Limites 1 a 29 y orden con deshacer.
            c.SetTaxRate(ZoneType.Office, 99f);
            Assert.Equal(29f, c.TaxRate(ZoneType.Office));
            Assert.Equal(CityResult.InvalidArgument, c.Execute(new SetTaxRateCommand(ZoneType.None, 10f)));
            Assert.Equal(CityResult.Ok, c.Execute(new SetTaxRateCommand(ZoneType.Residential, 12f)));
            Assert.Equal(12f, c.TaxRate(ZoneType.Residential));
            Assert.True(c.Undo());
            Assert.Equal(18f, c.TaxRate(ZoneType.Residential));
            // Impuestos altos frenan el crecimiento: la demanda baja respecto de la misma ciudad con impuestos normales.
            CitySimulation low = Town(Open());
            low.Run(CityClock.TicksPerDay * 4);
            c.Run(CityClock.TicksPerDay);
            low.Run(CityClock.TicksPerDay);
            Assert.True(c.Demand.R < low.Demand.R);
        }

        [Fact]
        public void MaintenanceFollowsBudgetAndRoadLength()
        {
            CitySimulation c = CityKit.New(1, Open());
            float x = c.OutsidePosition.X + 150f;
            CityKit.MustRoad(c, x, 0, x + 100, 0);
            c.Economy.Estimate(out _, out long baseExpenses);
            // Highway de 150 m a 0,5 + calle de 100 m a 0,12.
            Assert.InRange(baseExpenses, 86, 88);
            Assert.Equal(CityResult.Ok, c.Execute(new PlaceServiceCommand(ServiceType.WindTurbine, CityKit.V(x + 50, 20))));
            c.Economy.Estimate(out _, out long withTurbine);
            Assert.Equal(baseExpenses + 40, withTurbine);
            c.Economy.SetBudget(ServiceType.WindTurbine, 150);
            c.Economy.Estimate(out _, out long at150);
            Assert.Equal(baseExpenses + 60, at150);
            c.Economy.SetBudget(ServiceType.WindTurbine, 10);
            Assert.Equal(50, c.Economy.GetBudget(ServiceType.WindTurbine));
        }

        [Fact]
        public void BuildingMoneyIsRejectedWithAReasonAndCountsInTheReport()
        {
            var bal = Open();
            bal.StartMoney = 7000;
            CitySimulation c = CityKit.New(1, bal);
            float x = c.OutsidePosition.X + 150f;
            Assert.Equal(CityResult.Ok, CityKit.Road(c, x, 0, x + 100, 0));
            Assert.Equal(CityResult.InsufficientFunds, c.Execute(new PlaceServiceCommand(ServiceType.CoalPlant, CityKit.V(x + 50, 30))));
            Assert.Equal(7000 - 1200, c.Money);
            Assert.Equal(1200, c.Economy.CurrentWeek.Construction);
            Assert.Equal(CityResult.Ok, c.Execute(new PlaceServiceCommand(ServiceType.WindTurbine, CityKit.V(x + 50, 30))));
            Assert.Equal(1200 + 4500, c.Economy.CurrentWeek.Construction);
            Assert.True(c.Undo());
            Assert.Equal(1200, c.Economy.CurrentWeek.Construction);
        }

        [Fact]
        public void MilestonesUnlockRoadsZonesAndServicesInOrder()
        {
            var bal = new CityBalance { MilestonePopulation = new[] { 0, 20, 40, 60, 80, 100, 200, 400, 800, 1600, 3200 } };
            CitySimulation c = Town(bal);
            // Al empezar solo baja densidad y calles basicas; avenidas, media densidad, oficinas y servicios de poblacion estan cerrados.
            Assert.Equal(0, c.MilestoneIndex);
            Assert.Equal(CityResult.NotUnlocked, CityKit.Road(c, 0, 160, 0, 200, RoadTypeIds.Avenue4));
            Assert.Equal(CityResult.NotUnlocked, c.Execute(PaintZoneCommand.Brush(CityKit.V(80, 0), 20f, ZoneType.Residential, 2)));
            Assert.Equal(CityResult.NotUnlocked, c.Execute(PaintZoneCommand.Brush(CityKit.V(80, 0), 20f, ZoneType.Office, 2)));
            Assert.False(c.IsServiceUnlocked(ServiceType.Landfill));
            Assert.True(c.IsServiceUnlocked(ServiceType.WaterPump));
            c.Run(20000);
            Assert.True(c.MilestoneIndex >= 5, "hito " + c.MilestoneIndex + " pob " + c.Stats.Population);
            var seen = new System.Collections.Generic.List<int>();
            while (c.TryDequeueMilestone(out int m)) seen.Add(m);
            for (int i = 0; i < seen.Count; i++) Assert.Equal(i + 1, seen[i]);
            Assert.True(c.IsServiceUnlocked(ServiceType.School));
            Assert.Equal(CityResult.Ok, CityKit.Road(c, 0, 160, 0, 200, RoadTypeIds.Avenue4));
            Assert.Equal(CityResult.Ok, c.Execute(PaintZoneCommand.Brush(CityKit.V(80, 0), 20f, ZoneType.Residential, 3)));
            Assert.Equal("Aldea", Milestones.Get(new CityBalance(), 1).Name);
            Assert.Equal(400, Milestones.Get(new CityBalance(), 1).Population);
            // El hito no se pierde al bajar la poblacion.
            int idx = c.MilestoneIndex;
            c.SupplyOverride = false;
            c.Run(30000);
            Assert.Equal(idx, c.MilestoneIndex);
        }

        [Fact]
        public void LandValueAndAirPollutionFollowParksAndIndustry()
        {
            CitySimulation c = CityKit.New(1, Open());
            float x = c.OutsidePosition.X + 150f;
            CityKit.MustRoad(c, x, 0, x + 160, 0);
            CityKit.MustRoad(c, x + 160, 0, x + 320, 0);
            c.Step();
            Vec2 near = CityKit.V(x + 40, 0), far = CityKit.V(x + 300, 0);
            c.Run(c.Balance.FieldInterval * FieldSystem.Bands * 2);
            Assert.Equal(c.Balance.DefaultLandValue, c.LandValueAt(near), 0.001);
            Assert.Equal(c.Balance.DefaultLandValue, c.LandValueAt(far), 0.001);
            Assert.Equal(CityResult.Ok, c.Execute(new PlaceServiceCommand(ServiceType.Park, CityKit.V(x + 40, 20))));
            c.Run(c.Balance.FieldInterval * FieldSystem.Bands * 2);
            Assert.True(c.LandValueAt(near) > c.Balance.DefaultLandValue + 10f, "valor junto al parque " + c.LandValueAt(near));
            Assert.Equal(c.Balance.DefaultLandValue, c.LandValueAt(far), 0.001);
            // La central de carbon baja el suelo y ensucia el aire a su alrededor; lejos no.
            Assert.Equal(CityResult.Ok, c.Execute(new PlaceServiceCommand(ServiceType.CoalPlant, CityKit.V(x + 250, 30))));
            c.Run(c.Balance.FieldInterval * FieldSystem.Bands * 2);
            Assert.True(c.AirPollutionAt(far) > 10f, "aire " + c.AirPollutionAt(far));
            Assert.True(c.LandValueAt(far) < c.Balance.DefaultLandValue - 5f);
            Assert.True(c.AirPollutionAt(near) < c.AirPollutionAt(far));
            Assert.True(c.PollutionPenaltyAt(far) > c.PollutionPenaltyAt(near));
            // Derribar la central limpia el aire.
            EntityId plant = EntityId.None;
            for (int i = 0; i < c.Buildings.IndexBound; i++) if (c.Buildings.IsAlive(i) && c.Buildings.Get(i).ServiceType == (byte)ServiceType.CoalPlant) plant = c.Buildings.Get(i).Id;
            Assert.Equal(CityResult.Ok, c.Execute(new DemolishServiceCommand(plant)));
            c.Run(c.Balance.FieldInterval * FieldSystem.Bands * 2);
            Assert.Equal(0f, c.AirPollutionAt(far), 0.001);
        }

        [Fact]
        public void IndustryPollutesAndLowersLandValueNearby()
        {
            CitySimulation c = Town(Open());
            c.Run(10000);
            Vec2 indPos = default;
            bool found = false;
            for (int i = 0; i < c.Buildings.IndexBound && !found; i++)
                if (c.Buildings.IsAlive(i) && c.Buildings.Get(i).Zone == ZoneType.Industrial) { indPos = c.Buildings.Get(i).Pos; found = true; }
            Assert.True(found);
            Assert.True(c.AirPollutionAt(indPos) > 3f);
            Assert.True(c.LandValueAt(indPos) < c.Balance.DefaultLandValue);
            Assert.True(c.AirPollutionAt(CityKit.V(-120, 0)) < c.AirPollutionAt(indPos));
        }

        [Fact]
        public void DeterministicWithEconomy()
        {
            ulong Run()
            {
                CitySimulation c = Town(Open(), 21);
                c.Run(CityClock.TicksPerDay * 9);
                return c.ComputeHash();
            }
            Assert.Equal(Run(), Run());
        }
    }
}
