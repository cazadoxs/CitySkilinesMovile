using System;
using Urbe.Sim.City;
using Urbe.Sim.Runner.Bot;
using Xunit;

namespace Urbe.Sim.Tests.City
{
    public class BotTests
    {
        private static CitySimulation Play(ulong seed, int days)
        {
            var c = new CitySimulation(seed);
            var bot = new CityBot(c);
            bot.Think();
            long ticks = (long)days * CityClock.TicksPerDay;
            for (long t = 1; t <= ticks; t++)
            {
                c.Step();
                if (t % 50 == 0) bot.Think();
            }
            return c;
        }

        [Fact]
        public void BotBuildsAWorkingCityFromAnEmptyMap()
        {
            CitySimulation c = Play(1, 12);
            Assert.True(c.Network.SegmentCount > 5);
            Assert.True(c.Buildings.Count > 40, "edificios " + c.Buildings.Count);
            Assert.True(c.Stats.Population > 300, "poblacion " + c.Stats.Population);
            Assert.True(c.Supply.PowerProduction > 0 && c.Supply.WaterProduction > 0 && c.Supply.SewerCapacity > 0);
            Assert.True(c.Economy.HistoryCount >= 1 && c.Economy.LastWeek.Income > 0);
            Assert.True(c.Trips.TripsCompleted > 0);
            Assert.True(c.Money > -10000);
        }

        [Fact]
        public void BotRunIsDeterministicByHash()
        {
            ulong a = Play(7, 6).ComputeHash();
            Assert.Equal(a, Play(7, 6).ComputeHash());
            Assert.NotEqual(a, Play(8, 6).ComputeHash());
        }

        /// <summary>Golden: si cambia el equilibrio, el bot o el orden de los sistemas, este hash cambia a proposito (actualizarlo y anotarlo en la tarjeta).</summary>
        [Fact]
        public void BotGoldenHash()
        {
            CitySimulation c = Play(1, 10);
            Assert.Equal(GoldenHash, c.ComputeHash());
        }

        private const ulong GoldenHash = 3437216878634329644UL;
    }
}
