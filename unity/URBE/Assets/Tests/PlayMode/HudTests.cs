using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Urbe.Game.Sim;
using Urbe.Game.UI;

namespace Urbe.Game.Tests
{
    /// <summary>M2-08: carga la escena Main real con el HUD y comprueba datos, clics, objetivos de 48 dp y captura de pantalla.</summary>
    public class HudTests : InputTestFixture
    {
        private SimHost _host = null!;
        private HudController _hud = null!;
        private Mouse _mouse = null!;

        public override void Setup()
        {
            base.Setup();
            _mouse = InputSystem.AddDevice<Mouse>();
            InputSystem.AddDevice<Keyboard>();
        }

        private IEnumerator LoadMain(float secondsOfSim)
        {
            SceneManager.LoadScene("Main");
            yield return null;
            yield return null;
            _host = Object.FindFirstObjectByType<SimHost>();
            _hud = Object.FindFirstObjectByType<HudController>();
            Assert.That(_host, Is.Not.Null);
            Assert.That(_hud, Is.Not.Null);
            // Los vehículos tardan en aparecer: avanzamos la simulación a mano para no depender del reloj.
            yield return null;
            int ticks = Mathf.RoundToInt(secondsOfSim * SimHost.TicksPerSecond);
            for (int i = 0; i < ticks; i++) _host.StepOnce();
            _hud.Refresh();
            yield return null;
        }

        private static Vector2 ElementCenterToScreen(VisualElement e)
        {
            Rect r = e.worldBound;
            Vector2 c = r.center;
            float k = Screen.width / e.panel.visualTree.resolvedStyle.width;
            return new Vector2(c.x * k, Screen.height - c.y * k);
        }

        [UnityTest]
        public IEnumerator Hud_MuestraVehiculosYFlujo()
        {
            yield return LoadMain(60f);
            Assert.That(int.Parse(_hud.VehicleText), Is.GreaterThan(0), "El contador de vehículos refleja la simulación");
            Assert.That(_hud.FlowText, Does.EndWith("%"));
            Assert.That(_hud.VehicleText, Is.EqualTo(_host.Metrics.VehicleCount.ToString()));
        }

        [UnityTest]
        public IEnumerator Botones_Miden48dp_YSonTocables()
        {
            yield return LoadMain(1f);
            yield return null;
            foreach (string name in new[] { "btn-pause", "btn-x1", "btn-x2", "btn-x3" })
            {
                var b = _hud.Root.Q<Button>(name);
                Assert.That(b, Is.Not.Null, name);
                // Alto/ancho resueltos en unidades del panel (1 unidad = 1 dp de referencia).
                Assert.That(b.resolvedStyle.minWidth.value, Is.GreaterThanOrEqualTo(48f), name + " min-width");
                Assert.That(b.resolvedStyle.minHeight.value, Is.GreaterThanOrEqualTo(48f), name + " min-height");
                // El layout se redondea a píxeles físicos, de ahí la tolerancia de un 2 %.
                Assert.That(b.resolvedStyle.width, Is.GreaterThanOrEqualTo(47f), name + " ancho");
                Assert.That(b.resolvedStyle.height, Is.GreaterThanOrEqualTo(47f), name + " alto");
            }
        }

        [UnityTest]
        public IEnumerator ClicEnLosBotones_CambiaLaVelocidad_YPausa()
        {
            yield return LoadMain(1f);
            yield return null;
            var x3 = _hud.Root.Q<Button>("btn-x3");
            Vector2 p = ElementCenterToScreen(x3);
            Set(_mouse.position, p);
            yield return null;
            Click(_mouse.leftButton);
            yield return null;
            yield return null;
            Assert.That(_host.Speed, Is.EqualTo(SimSpeed.X3));
            Assert.That(x3.ClassListContains("active"));

            var pause = _hud.Root.Q<Button>("btn-pause");
            Set(_mouse.position, ElementCenterToScreen(pause));
            yield return null;
            Click(_mouse.leftButton);
            yield return null;
            yield return null;
            Assert.That(_host.IsPaused);
            Assert.That(pause.ClassListContains("active"));
        }

        [UnityTest]
        public IEnumerator AtajosDeTeclado_EspacioY123()
        {
            yield return LoadMain(1f);
            var kb = Keyboard.current;
            Press(kb.digit2Key);
            yield return null;
            Release(kb.digit2Key);
            yield return null;
            Assert.That(_host.Speed, Is.EqualTo(SimSpeed.X2));
            Press(kb.spaceKey);
            yield return null;
            Release(kb.spaceKey);
            yield return null;
            Assert.That(_host.IsPaused);
            Press(kb.spaceKey);
            yield return null;
            Release(kb.spaceKey);
            yield return null;
            Assert.That(_host.Speed, Is.EqualTo(SimSpeed.X2), "Espacio reanuda a la última velocidad");
        }

        [UnityTest]
        public IEnumerator IsOverHud_DistingueInterfazDeMapa()
        {
            yield return LoadMain(1f);
            yield return null;
            var x1 = _hud.Root.Q<Button>("btn-x1");
            Assert.That(_hud.IsOverHud(ElementCenterToScreen(x1)));
            Assert.That(_hud.IsOverHud(new Vector2(Screen.width * 0.5f, Screen.height * 0.3f)), Is.False);
        }

        [UnityTest]
        public IEnumerator Captura_EscenaConHud_Horizontal() { yield return Capture(1688, 780, "m2-hud.png"); }

        [UnityTest]
        public IEnumerator Captura_EscenaConHud_Vertical() { yield return Capture(780, 1688, "m2-hud-vertical.png"); }

        /// <summary>
        /// En batchmode no hay "fin de fotograma" ni ventana, así que se renderiza a mano: la cámara dibuja la escena en un RenderTexture
        /// y el panel de UI Toolkit (PanelSettings.targetTexture) se pinta encima en ese mismo RenderTexture.
        /// El tamaño 1688x780 equivale a un móvil horizontal de 844x390 dp a 2x; el vertical es el mismo girado.
        /// </summary>
        private IEnumerator Capture(int w, int h, string fileName)
        {
            yield return LoadMain(90f);
            var cam = Object.FindFirstObjectByType<Camera>();
            var panel = _hud.GetComponent<UIDocument>().panelSettings;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            // 1) La cámara dibuja la escena (incluidos los Graphics.DrawMesh de LateUpdate) en el RenderTexture durante fotogramas normales.
            cam.targetTexture = rt;
            yield return null;
            yield return null;
            // 2) Se apaga la cámara (no vuelve a limpiar) y el panel de UI se pinta encima en el mismo RenderTexture.
            cam.enabled = false;
            panel.clearColor = false;
            panel.targetTexture = rt;
            yield return null;
            yield return null;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            panel.targetTexture = null;
            cam.targetTexture = null;
            string dir = Path.Combine(Application.dataPath, "..", "..", "..", "docs", "media");
            Directory.CreateDirectory(dir);
            string path = Path.GetFullPath(Path.Combine(dir, fileName));
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log($"[M2-08] captura {w}x{h} en {path}; vehiculos en HUD: {_hud.VehicleText}, flujo {_hud.FlowText}");
            Object.Destroy(tex);
            rt.Release();
        }
    }
}
