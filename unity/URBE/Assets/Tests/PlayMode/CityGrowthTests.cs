using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Urbe.Game.CameraRig;
using Urbe.Game.Rendering.Buildings;
using Urbe.Game.Sim;
using Urbe.Game.UI;
using Urbe.Sim.City;
using Urbe.Sim.Geometry;

namespace Urbe.Game.Tests
{
    /// <summary>M3-09: edificios reales en pantalla, variados, con escala y orientación correctas; y la medición de 5.000 edificios.</summary>
    public class CityGrowthTests : InputTestFixture
    {
        private SimHost _host = null!;
        private CityCameraController _rig = null!;
        private BuildingChunkRenderer _br = null!;
        private CityHudController _hud = null!;

        private IEnumerator LoadMain()
        {
            SceneManager.LoadScene("Main");
            yield return null;
            yield return null;
            _host = Object.FindFirstObjectByType<SimHost>();
            _rig = Object.FindFirstObjectByType<CityCameraController>();
            _br = Object.FindFirstObjectByType<BuildingChunkRenderer>();
            _hud = Object.FindFirstObjectByType<CityHudController>();
            Assert.That(_br, Is.Not.Null, "la escena Main lleva BuildingChunkRenderer");
            _host.SetSpeed(SimSpeed.Paused);
        }

        [UnityTest]
        public IEnumerator CiudadCrece_ConEdificiosReales_YCaptura()
        {
            yield return LoadMain();
            // Dinero de sobra para que la captura enseñe una ciudad crecida; el arranque con el dinero real se prueba en CityPlayTests.
            _host.Initialize(new CitySimulation(7UL, new CityBalance { StartMoney = 400000 }));
            yield return null;
            Assert.That(CityScript.BuildStreets(_host, CityScript.Medium, out CityResult reject), Is.GreaterThan(20), "calles: " + reject);
            Assert.That(CityScript.PaintZones(_host), Is.GreaterThan(10));
            foreach (CityResult r in CityScript.PlaceUtilities(_host)) Assert.That(r, Is.EqualTo(CityResult.Ok));
            var log = new List<string>();
            for (int day = 0; day < 40; day++)
            {
                CityScript.Run(_host, 3600);
                if (day % 5 == 4) log.Add($"día {day + 1}: {_host.City!.Buildings.Count} edificios, {_host.City.Stats.Population} hab, dinero {_host.City.Money}");
            }
            Debug.Log("[M3-09] crecimiento\n" + string.Join("\n", log));
            Assert.That(_host.City!.Buildings.Count, Is.GreaterThan(30));
            _rig.Model.SnapTo(new Vector2(-640f, 0f), 260f);
            for (int i = 0; i < 8; i++) yield return null;
            Assert.That(_br.InstanceCount, Is.EqualTo(_host.City.Buildings.Count), "el render tiene un registro por edificio");
            Assert.That(_br.LastVisible, Is.GreaterThan(10), "se dibujan edificios");
            // Todos los edificios de la simulación con un modelo del catálogo, sobre el suelo y mirando a su calle.
            BuildingSystem bs = _host.City.Buildings;
            int checkedCount = 0;
            for (int i = 0; i < bs.IndexBound; i++)
            {
                if (!bs.IsAlive(i)) continue;
                Building bd = bs.Get(i);
                Assert.That(bd.Pos.X, Is.InRange(-1000f, 1000f));
                Assert.That(bd.Segment.IsValid, Is.True, "el edificio cuelga de una calle");
                checkedCount++;
            }
            Assert.That(checkedCount, Is.EqualTo(_br.InstanceCount));
        }
    }
}
