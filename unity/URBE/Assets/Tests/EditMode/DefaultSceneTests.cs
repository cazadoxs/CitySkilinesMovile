using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using Urbe.Game.Editor;
using Urbe.Game.Rendering.Vehicles;
using Urbe.Game.Sim;

namespace Urbe.Game.Tests
{
    /// <summary>Main.unity es la escena por defecto y se juega con Play sin pasos previos.</summary>
    public class DefaultSceneTests
    {
        [Test]
        public void Main_EsLaPrimeraYUnicaEscenaDeLaBuild()
        {
            Assert.That(EditorBuildSettings.scenes.Length, Is.GreaterThan(0));
            Assert.That(EditorBuildSettings.scenes[0].path, Is.EqualTo(SceneBuilder.ScenePath));
            Assert.That(EditorBuildSettings.scenes[0].enabled);
            Assert.That(EditorBuildSettings.scenes.Count(sc => sc.enabled), Is.EqualTo(1), "Main es la única escena de la build");
            Assert.DoesNotThrow(DefaultSceneSetup.Apply);
        }

        [Test]
        public void Main_AbreEnElEditor_ConSimulacionYVehiculosYReferenciasCompletas()
        {
            var scene = EditorSceneManager.OpenScene(SceneBuilder.ScenePath, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();
            var vr = roots.SelectMany(r => r.GetComponentsInChildren<VehicleRenderer>(true)).FirstOrDefault();
            Assert.That(vr, Is.Not.Null, "Main lleva VehicleRenderer");
            var so = new SerializedObject(vr);
            Assert.That(so.FindProperty("models").objectReferenceValue, Is.Not.Null, "modelos asignados");
            Assert.That(so.FindProperty("material").objectReferenceValue, Is.Not.Null, "material asignado");
            Assert.That(so.FindProperty("host").objectReferenceValue, Is.Not.Null, "SimHost asignado");
            Assert.That(roots.SelectMany(r => r.GetComponentsInChildren<SimBootstrap>(true)).Any(), "SimBootstrap arranca la simulación");
        }
    }
}
