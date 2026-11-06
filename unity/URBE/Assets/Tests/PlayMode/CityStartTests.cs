using UnityEngine.UIElements;
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
            Assert.That(city.Network.SegmentCount, Is.EqualTo(12), "al empezar solo existe la autovía que cruza el mapa (12 tramos)");
            for (int i = 0; i < city.Network.SegmentIndexBound; i++)
                if (city.Network.SegmentIdAt(i).IsValid)
                    Assert.That(city.Network.GetSegment(city.Network.SegmentIdAt(i)).RoadTypeId, Is.EqualTo(RoadTypeIds.Highway4), "ninguna calle inicial");
            Assert.That(city.Zones.CellCount, Is.EqualTo(0), "ninguna celda de zona inicial: la autovía no es zonificable");
            var boot = Object.FindFirstObjectByType<SimBootstrap>();
            var demo = typeof(SimBootstrap).GetField("demoGrid", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            Assert.That((bool)demo.GetValue(boot)!, Is.False, "Main.unity no arranca la rejilla de demostración (demoGrid = false)");
            Assert.That(city.Stats.Population, Is.EqualTo(0));
            Assert.That(city.Buildings.Count, Is.EqualTo(0));
            Assert.That(city.Balance.MapHalfSize, Is.EqualTo(1000f), "mapa de 2 x 2 km");
            Assert.That(city.Network.GetSegment(city.OutsideSegment).RoadTypeId, Is.EqualTo(RoadTypeIds.Highway4));
            Assert.That(city.OutsideSegmentFor(0), Is.Not.EqualTo(city.OutsideSegmentFor(1)), "hay conexión exterior en los dos extremos");
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

        [UnityTest]
        public IEnumerator SelectorDeCarreteras_SeisTipos_ConPrecio_Y_Seleccion()
        {
            SceneManager.LoadScene("Main");
            yield return null;
            yield return null;
            var road = Object.FindFirstObjectByType<Urbe.Game.Tools.RoadTool.RoadToolController>();
            var host = Object.FindFirstObjectByType<SimHost>();
            var doc = road.GetComponent<UnityEngine.UIElements.UIDocument>();
            yield return null;
            string[] names = { "btn-type-street", "btn-type-oneway", "btn-type-avenue", "btn-type-avenue6", "btn-type-rural", "btn-type-highway" };
            for (int i = 0; i < 6; i++)
            {
                var b = doc.rootVisualElement.Q<UnityEngine.UIElements.Button>(names[i]);
                Assert.That(b, Is.Not.Null, names[i]);
                Assert.That(b.Q<UnityEngine.UIElements.Label>(className: "road-type-price").text, Is.EqualTo(host.City!.Balance.RoadCostPerMeter[i] + "/m"));
                Assert.That(b.Q<Urbe.Game.Tools.RoadTool.RoadIcon>(), Is.Not.Null, "icono");
                road.SetType((ushort)i);
                Assert.That(road.Model!.RoadTypeId, Is.EqualTo((ushort)i));
            }
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
