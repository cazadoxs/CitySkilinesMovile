using UnityEditor;
using UnityEngine;
using Urbe.Game.Tools.RoadTool;
using System.Collections.Generic;
using NUnit.Framework;
using Urbe.Game.Tools.CityTools;
using Urbe.Game.UI;
using Urbe.Sim.City;
using Urbe.Sim.Geometry;
using Urbe.Sim.Roads;

namespace Urbe.Game.Tests
{
    /// <summary>M3-07 y M3-08: textos de la ciudad y órdenes agrupadas (un trazo de pincel se deshace de una vez).</summary>
    public class CityToolsTests
    {
        [Test]
        public void Textos_Requisitos_Y_Motivos()
        {
            var bal = new CityBalance();
            Assert.That(CityText.Requirement(4, bal), Is.EqualTo("requiere Ciudad pequeña (5.000 habitantes)"));
            Assert.That(CityText.ShortRequirement(1, bal), Is.EqualTo("400 hab."));
            var city = new CitySimulation(1UL);
            Assert.That(CityText.Reject(CityResult.InsufficientFunds, city), Does.Contain("dinero"));
            Assert.That(CityText.Reject(CityResult.NoRoadAccess, city), Does.Contain("calle"));
            Assert.That(CityText.Reject(CityResult.OutOfBounds, city), Does.Contain("mapa"));
            Assert.That(CityText.Problems(BuildingProblems.NoWater | BuildingProblems.NoPower), Does.Contain("agua").And.Contain("electricidad"));
            Assert.That(CityText.Problems(BuildingProblems.None), Is.EqualTo(""));
            Assert.That(CityText.Clock(new CityClock(0)), Is.EqualTo("Lun 06:00"));
        }

        [Test]
        public void GroupCityCommand_AplicaTodo_Y_DeshaceDeUnaVez()
        {
            var city = new CitySimulation(2UL, new CityBalance { StartMoney = 100000, LegacyStubHighway = true });
            Assert.That(city.Execute(new BuildRoadCityCommand(SegmentRequest.Straight(new Vec2(-838f, 0f), new Vec2(-678f, 0f), RoadTypeIds.Street2))), Is.EqualTo(CityResult.Ok));
            int zoned(ZoneType z) { int n = 0; for (int i = 0; i < city.Zones.CellIndexBound; i++) if (city.Zones.IsCellAlive(i) && city.Zones.GetCell(i).Zone == z) n++; return n; }
            var cmds = new List<CityCommand>
            {
                PaintZoneCommand.Brush(new Vec2(-800f, 20f), 16f, ZoneType.Residential, 1),
                PaintZoneCommand.Brush(new Vec2(-760f, 20f), 16f, ZoneType.Residential, 1),
                PaintZoneCommand.Brush(new Vec2(-720f, 20f), 16f, ZoneType.Residential, 1),
            };
            int undo0 = city.History.UndoCount;
            Assert.That(city.Execute(new GroupCityCommand(cmds)), Is.EqualTo(CityResult.Ok));
            Assert.That(zoned(ZoneType.Residential), Is.GreaterThan(5));
            Assert.That(city.History.UndoCount, Is.EqualTo(undo0 + 1), "una sola entrada en el historial");
            Assert.That(city.Undo(), Is.True);
            Assert.That(zoned(ZoneType.Residential), Is.EqualTo(0), "deshacer quita todo el trazo");
        }

        [Test]
        public void RoadPricesJson_CoincideConCityBalance_Y_TodosLosTiposEstanAbiertos()
        {
            var json = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/_Project/Data/road-prices.json");
            Assert.That(json, Is.Not.Null);
            var prices = RoadPrices.Parse(json.text);
            var bal = new CityBalance();
            Assert.That(prices.entries.Length, Is.EqualTo(6));
            for (int i = 0; i < 6; i++)
            {
                Assert.That(prices.PerMeter(new[] { "street_2", "oneway_2", "avenue_4", "avenue_6", "rural_2", "highway_4" }[i]), Is.EqualTo(bal.RoadCostPerMeter[i]), "precio del tipo " + i);
                Assert.That(bal.RoadMilestone((ushort)i), Is.EqualTo(0), "tipo " + i + " abierto desde el inicio");
            }
        }

        [Test]
        public void GroupCityCommand_Vacio_EsInvalido()
        {
            var city = new CitySimulation(3UL);
            Assert.That(city.Execute(new GroupCityCommand(new List<CityCommand>())), Is.EqualTo(CityResult.InvalidArgument));
        }
    }
}
