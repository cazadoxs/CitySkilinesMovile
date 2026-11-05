using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using Urbe.Game.CameraRig;

namespace Urbe.Game.Tests
{
    /// <summary>
    /// M2-03: recorre el camino real Input System -> GestureInterpreter -> CameraRigModel -> Transform con dispositivos simulados
    /// (pantalla táctil y ratón virtuales). No sustituye a la prueba en un móvil: no mide tacto, latencia ni sensibilidad.
    /// </summary>
    public class CameraInputTests : InputTestFixture
    {
        private GameObject _go = null!;
        private CityCameraController _cam = null!;
        private Touchscreen _touch = null!;
        private Mouse _mouse = null!;

        public override void Setup()
        {
            base.Setup();
            _touch = InputSystem.AddDevice<Touchscreen>();
            _mouse = InputSystem.AddDevice<Mouse>();
            _go = new GameObject("Cam");
            _go.AddComponent<Camera>();
            _cam = _go.AddComponent<CityCameraController>();
        }

        public override void TearDown()
        {
            Object.Destroy(_go);
            base.TearDown();
        }

        private static Vector2 Px(float fx, float fy) => new Vector2(Screen.width * fx, Screen.height * fy);

        [UnityTest]
        public IEnumerator UnDedo_Arrastra_YLaCamaraSeMueve()
        {
            yield return null;
            Vector2 f0 = _cam.Model.Focus;
            BeginTouch(1, Px(0.3f, 0.5f), screen: _touch);
            yield return null;
            for (int i = 1; i <= 8; i++)
            {
                MoveTouch(1, Px(0.3f + i * 0.04f, 0.5f), screen: _touch);
                yield return null;
            }
            EndTouch(1, Px(0.62f, 0.5f), screen: _touch);
            yield return null;
            Assert.That(Vector2.Distance(_cam.Model.Focus, f0), Is.GreaterThan(5f));
            Assert.That(_cam.Model.Focus.x, Is.LessThan(f0.x), "Arrastrar a la derecha mueve el mundo con el dedo, el enfoque va a la izquierda");
        }

        [UnityTest]
        public IEnumerator Pellizco_AcercaLaCamara()
        {
            yield return null;
            float h0 = _cam.Model.Height;
            BeginTouch(1, Px(0.45f, 0.5f), screen: _touch);
            BeginTouch(2, Px(0.55f, 0.5f), screen: _touch);
            yield return null;
            for (int i = 1; i <= 8; i++)
            {
                MoveTouch(1, Px(0.45f - i * 0.02f, 0.5f), screen: _touch);
                MoveTouch(2, Px(0.55f + i * 0.02f, 0.5f), screen: _touch);
                yield return null;
            }
            Assert.That(_cam.Model.Height, Is.LessThan(h0 * 0.8f));
            EndTouch(1, Px(0.29f, 0.5f), screen: _touch);
            EndTouch(2, Px(0.71f, 0.5f), screen: _touch);
            yield return null;
        }

        [UnityTest]
        public IEnumerator RuedaDelRaton_HaceZoom()
        {
            yield return null;
            float h0 = _cam.Model.Height;
            Set(_mouse.position, Px(0.5f, 0.5f));
            Set(_mouse.scroll, new Vector2(0f, 120f));
            yield return null;
            Set(_mouse.scroll, Vector2.zero);
            yield return new WaitForSecondsRealtime(0.8f);
            Assert.That(_cam.Model.Height, Is.LessThan(h0));
        }

        [UnityTest]
        public IEnumerator DobleToque_CentraYAcerca_YSobreLaInterfazSeIgnora()
        {
            yield return null;
            _cam.IsOverUi = p => p.x < Screen.width * 0.2f;
            float h0 = _cam.Model.Height;
            // Toque dentro de la interfaz: debe ignorarse.
            BeginTouch(1, Px(0.1f, 0.5f), screen: _touch);
            yield return null;
            for (int i = 1; i <= 6; i++) { MoveTouch(1, Px(0.1f + i * 0.05f, 0.5f), screen: _touch); yield return null; }
            EndTouch(1, Px(0.4f, 0.5f), screen: _touch);
            yield return null;
            Assert.That(_cam.Model.IsCoasting, Is.False);

            // Doble toque en el mapa.
            for (int k = 0; k < 2; k++)
            {
                BeginTouch(2 + k, Px(0.6f, 0.4f), screen: _touch);
                yield return null;
                EndTouch(2 + k, Px(0.6f, 0.4f), screen: _touch);
                yield return null;
            }
            yield return null;
            Assert.That(_cam.Model.IsMovingToGoal);
            float t0 = Time.realtimeSinceStartup;
            while (_cam.Model.IsMovingToGoal && Time.realtimeSinceStartup - t0 < 4f) yield return null;
            Assert.That(_cam.Model.Height, Is.EqualTo(h0 * 0.5f).Within(1f));
        }
    }
}
