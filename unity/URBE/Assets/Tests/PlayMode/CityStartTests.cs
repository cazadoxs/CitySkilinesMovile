using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Urbe.Game.Rendering.Ground;
using Urbe.Game.Sim;
using Urbe.Game.UI;
using Urbe.Sim.City;
using Urbe.Sim.Roads;

namespace Urbe.Game.Tests
{
    /// <summary>M3-07: la escena Main arranca una ciudad vacía con dinero, autopista, suelo con límites y los datos de la ciudad en el HUD.</summary>
    public class CityStartTests : InputTestFixture
    {
        [UnityTest]
        public IEnumerator Main_ArrancaCiudadVacia_ConDinero_Y_Hud()
        {
            SceneManager.LoadScene("Main");
            yield return null;
            yield return null;
            var host = Object.FindFirstObjectByType<SimHost>();
            var hud = Object.FindFirstObjectByType<CityHudController>();
            Assert.That(host.City, Is.Not.Null, "SimHost aloja la CitySimulation");
            CitySimulation city = host.City!;
            Assert.That(city.Money, Is.EqualTo(city.Balance.StartMoney));
            Assert.That(city.Network.SegmentCount, Is.EqualTo(1), "solo la autopista de entrada");
            Assert.That(city.Stats.Population, Is.EqualTo(0));
            Assert.That(city.Buildings.Count, Is.EqualTo(0));
            Assert.That(city.Balance.MapHalfSize, Is.EqualTo(1000f), "mapa de 2 x 2 km");
            Assert.That(city.Network.GetSegment(city.OutsideSegment).RoadTypeId, Is.EqualTo(RoadTypeIds.Highway4));
            Assert.That(Object.FindFirstObjectByType<CityGround>().HalfSize.x, Is.EqualTo(1000f));
            yield return null;
            hud.Refresh();
            Assert.That(hud.MoneyText, Is.EqualTo(CityText.Money(city.Balance.StartMoney)));
            Assert.That(hud.PopText, Is.EqualTo("0"));
            Assert.That(hud.MilestoneText, Is.EqualTo("Asentamiento"));
            Assert.That(hud.DateText, Does.StartWith("Lun 06:"));
            Assert.That(hud.TipVisible, "el consejo inicial se ve al empezar");
            Assert.That(hud.TipText, Does.Contain("calle"));
            var cam = Object.FindFirstObjectByType<Camera>();
            var doc = hud.GetComponent<UnityEngine.UIElements.UIDocument>();
            yield return CaptureUtil.Shot(cam, doc, 1688, 780, "m3-inicio.png");
        }

        [Test]
        public void CityText_DineroConPuntos()
        {
            Assert.That(CityText.Money(70000), Is.EqualTo("70.000"));
            Assert.That(CityText.Money(999), Is.EqualTo("999"));
            Assert.That(CityText.Money(-1234567), Is.EqualTo("-1.234.567"));
        }
    }
}
