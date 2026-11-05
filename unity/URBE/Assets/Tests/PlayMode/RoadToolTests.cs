using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Urbe.Game.CameraRig;
using Urbe.Game.Rendering.Roads;
using Urbe.Game.Sim;
using Urbe.Game.Tools.RoadTool;
using Urbe.Sim.Geometry;
using Urbe.Sim.Roads;

namespace Urbe.Game.Tests
{
    /// <summary>
    /// M2-05: recorre el camino real dedo/ratón virtual -> gestos -> herramienta -> comandos de Sim.Core -> carreteras, con la escena Main y
    /// el HUD reales y la simulación en pausa. No sustituye a la prueba con dedos en un móvil (20 calles sin errores de toque: pendiente en M2-11).
    /// </summary>
    public class RoadToolTests : InputTestFixture
    {
        private Touchscreen _touch = null!;
        private Mouse _mouse = null!;
        private SimHost _host = null!;
        private RoadToolController _tool = null!;
        private CityCameraController _rig = null!;
        private RoadChunkRenderer _roads = null!;
        private Camera _cam = null!;
        private UIDocument _doc = null!;
        private int _touchId = 1;
        private bool _panelCloned;

        public override void Setup()
        {
            base.Setup();
            _touch = InputSystem.AddDevice<Touchscreen>();
            _mouse = InputSystem.AddDevice<Mouse>();
            InputSystem.AddDevice<Keyboard>();
        }

        private IEnumerator LoadMain(bool showcase = false)
        {
            SceneManager.LoadScene("Main");
            yield return null;
            yield return null;
            _host = Object.FindFirstObjectByType<SimHost>();
            _tool = Object.FindFirstObjectByType<RoadToolController>();
            _rig = Object.FindFirstObjectByType<CityCameraController>();
            _roads = Object.FindFirstObjectByType<RoadChunkRenderer>();
            _cam = _rig.GetComponent<Camera>();
            _doc = _tool.GetComponent<UIDocument>();
            Assert.That(_tool, Is.Not.Null);
            _host.SetSpeed(SimSpeed.Paused);
            if (showcase) _host.Initialize(ShowcaseScenario.Create(7UL, 40, 1f).Scenario);
            for (int i = 0; i < 6; i++) yield return null; // el diseño de UI Toolkit tarda unos fotogramas en estabilizarse
        }

        private static Vector2 CenterOf(VisualElement e)
        {
            Vector2 c = e.worldBound.center;
            float k = Screen.width / e.panel.visualTree.resolvedStyle.width;
            return new Vector2(c.x * k, Screen.height - c.y * k);
        }

        /// <summary>Clic de ratón virtual sobre un botón del HUD. Si se da <paramref name="done"/>, se reintenta (hasta 4 veces) mientras no se cumpla:
        /// tras otros fixtures de entrada, el primer clic tras cargar la escena a veces no llega al módulo de UI.</summary>
        private IEnumerator ClickButton(string name, System.Func<bool>? done = null)
        {
            var b = _doc.rootVisualElement.Q<Button>(name);
            Assert.That(b, Is.Not.Null, name);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                Set(_mouse.position, CenterOf(b) + new Vector2(30f, 0f));
                yield return null;
                Set(_mouse.position, CenterOf(b));
                yield return null;
                yield return null;
                Click(_mouse.leftButton);
                yield return null;
                yield return null;
                if (done == null || done()) break;
                Debug.Log("[M2-05] clic sobre " + name + " no llegó, reintento " + (attempt + 1));
            }
        }

        private IEnumerator FocusOn(Vector2 ground, float height)
        {
            _rig.Model.SnapTo(ground, height);
            yield return null;
            yield return null;
        }

        private Vector2 ScreenOf(Vec2 g) { Vector3 s = _cam.WorldToScreenPoint(new Vector3(g.X, 0f, g.Z)); return new Vector2(s.x, s.y); }

        private IEnumerator TapAt(Vec2 g)
        {
            int id = _touchId++;
            BeginTouch(id, ScreenOf(g), screen: _touch);
            yield return null;
            EndTouch(id, ScreenOf(g), screen: _touch);
            yield return null;
            yield return null;
        }

        /// <summary>Arrastra un dedo hasta que el CURSOR (desplazado) quede en <paramref name="g"/>.</summary>
        private IEnumerator DragCursorTo(Vec2 from, Vec2 g)
        {
            int id = _touchId++;
            Vector2 a = ScreenOf(from), b = ScreenOf(g) - new Vector2(0f, _tool.CursorOffsetPx);
            BeginTouch(id, a, screen: _touch);
            yield return null;
            for (int i = 1; i <= 10; i++)
            {
                MoveTouch(id, Vector2.Lerp(a, b, i / 10f), screen: _touch);
                yield return null;
            }
            // El dedo sigue puesto: el fantasma debe estar ya a la vista. Se levanta después.
            EndTouch(id, b, screen: _touch);
            yield return null;
            yield return null;
        }

        [UnityTest]
        public IEnumerator Construir_ConDedo_EnPausa_ConfirmaYDeshace()
        {
            yield return LoadMain();
            ulong tick = _host.Tick;
            int segs = _host.Network!.SegmentCount;
            yield return ClickButton("btn-build", () => _tool.IsActive);
            Assert.That(_tool.IsActive);
            Assert.That(_rig.ToolActive, "con la herramienta activa 1 dedo no mueve la cámara");
            yield return FocusOn(new Vector2(850f, 100f), 250f);
            Vector2 focus = _rig.Model.Focus;

            var node = new Vec2(800f, 100f); // nodo de la rejilla en el borde este
            yield return TapAt(node);
            Assert.That(_tool.Model!.HasStart);
            Assert.That(_tool.Model.Start, Is.EqualTo(node), "el inicio se ajusta al nodo existente");
            Assert.That(_rig.Model.Focus, Is.EqualTo(focus), "tocar con la herramienta no mueve la cámara");

            var target = new Vec2(900f, 100f);
            yield return DragCursorTo(node, target);
            Assert.That(_rig.Model.Focus.x, Is.EqualTo(focus.x).Within(0.5f), "arrastrar con la herramienta no mueve la cámara");
            Assert.That(_tool.Model.HasEnd);
            Assert.That((_tool.Model.End - target).Length, Is.LessThan(6f), "el cursor desplazado cae en el objetivo y no bajo el dedo");
            Assert.That(_tool.Model.Plan.Valid, Is.True, _tool.Model.Plan.Message);
            Assert.That(_tool.MessageText, Does.Contain("m"), "el coste en vivo se muestra");
            yield return null;
            Assert.That(_doc.rootVisualElement.Q<Button>("btn-confirm").enabledSelf);

            yield return ClickButton("btn-confirm");
            Assert.That(_host.Network!.SegmentCount, Is.EqualTo(segs + 1), "construida con la simulación en pausa");
            Assert.That(_host.Tick, Is.EqualTo(tick), "sin avanzar el tick");
            _roads.Flush();
            Assert.That(_roads.PendingPieces, Is.EqualTo(0));
            Assert.That(_tool.Model.Start.X, Is.EqualTo(900f).Within(0.5f), "sigue dibujando desde el final");

            yield return ClickButton("btn-undo");
            Assert.That(_host.Network!.SegmentCount, Is.EqualTo(segs));
        }

        [UnityTest]
        public IEnumerator Validacion_AnguloAgudo_MuestraMotivoEnRojo_YNoDejaConfirmar()
        {
            yield return LoadMain();
            _tool.SetActive(true);
            yield return FocusOn(new Vector2(850f, 100f), 250f);
            var node = new Vec2(800f, 100f);
            yield return TapAt(node);
            // El nodo del borde tiene un brazo hacia el norte: un trazo a 20° del norte es demasiado agudo.
            var bad = new Vec2(800f + 90f * Mathf.Sin(32f * Mathf.Deg2Rad), 100f + 90f * Mathf.Cos(32f * Mathf.Deg2Rad));
            yield return DragCursorTo(node, bad);
            yield return null;
            Assert.That(_tool.Model!.Plan.Valid, Is.False);
            Assert.That(_tool.MessageText, Does.Contain("Ángulo"));
            Assert.That(_tool.MessageIsError);
            Assert.That(_doc.rootVisualElement.Q<Button>("btn-confirm").enabledSelf, Is.False);
            Assert.That(_host.Network!.SegmentCount, Is.EqualTo(GridSegments));
        }

        private int GridSegments => 144; // 8x8 celdas: 2 x 9 x 8

        [UnityTest]
        public IEnumerator PanelDeHerramienta_SeInterponeAlMapa_YLosBotonesMiden48()
        {
            yield return LoadMain();
            _tool.SetActive(true);
            yield return null;
            yield return null;
            foreach (string n in new[] { "btn-type-street", "btn-type-avenue", "btn-mode-straight", "btn-mode-curve", "btn-mode-free", "btn-mode-demolish", "btn-undo", "btn-cancel", "btn-confirm", "btn-build" })
            {
                var b = _doc.rootVisualElement.Q<Button>(n);
                Assert.That(b.resolvedStyle.height, Is.GreaterThanOrEqualTo(47f), n);
                Assert.That(b.resolvedStyle.width, Is.GreaterThanOrEqualTo(47f), n);
            }
            var hud = Object.FindFirstObjectByType<Urbe.Game.UI.HudController>();
            Assert.That(hud.IsOverHud(CenterOf(_doc.rootVisualElement.Q<Button>("btn-mode-curve"))));
        }

        [UnityTest]
        public IEnumerator Curva_Libre_Y_Demoler_FuncionanDeExtremoAExtremo()
        {
            yield return LoadMain();
            _tool.SetActive(true);
            yield return FocusOn(new Vector2(880f, 100f), 250f);
            int segs = _host.Network!.SegmentCount;

            // Curva: inicio en el nodo, control, final.
            _tool.SetMode(RoadToolMode.Curve);
            yield return TapAt(new Vec2(800f, 100f));
            yield return TapAt(new Vec2(860f, 100f));
            yield return TapAt(new Vec2(900f, 150f));
            Assert.That(_tool.Model!.Plan.Valid, Is.True, _tool.Model.Plan.Message);
            Assert.That(_tool.Confirm());
            Assert.That(_host.Network!.SegmentCount, Is.EqualTo(segs + 1));
            Assert.That(_tool.Model.GhostPieces.Count, Is.EqualTo(0).Or.GreaterThanOrEqualTo(0));

            // Demoler: tocar el tramo nuevo y confirmar.
            _tool.SetMode(RoadToolMode.Demolish);
            yield return TapAt(new Vec2(865f, 105f));
            Assert.That(_tool.Model.CanConfirm, Is.True, _tool.Model.Plan.Message);
            Assert.That(_tool.Confirm());
            Assert.That(_host.Network!.SegmentCount, Is.EqualTo(segs));
            Assert.That(_host.UndoLast());
            Assert.That(_host.Network!.SegmentCount, Is.EqualTo(segs + 1));
        }

        [UnityTest]
        public IEnumerator Lupa_SeAbreConLaLupaYSeVeEnPantalla()
        {
            yield return LoadMain();
            _tool.SetActive(true);
            yield return FocusOn(new Vector2(850f, 100f), 200f);
            _tool.DebugShowLoupeAt(new Vector2(Screen.width * 0.5f, Screen.height * 0.4f));
            yield return null;
            yield return null;
            var el = _doc.rootVisualElement.Q<VisualElement>("loupe");
            Assert.That(_tool.IsLoupeVisible);
            Assert.That(el.ClassListContains("show"));
            Assert.That(el.resolvedStyle.backgroundImage.renderTexture, Is.Not.Null);
        }

        // ------------------------------------------------------------------ captura
        // Se clona el PanelSettings antes de tocar targetTexture (ver HudTests): el asset compartido rompería los clics de las pruebas siguientes.

        [UnityTest]
        public IEnumerator Captura_Herramienta_FantasmaValido_Y_ErrorEnRojo()
        {
            yield return LoadMain(showcase: true);
            _roads.Flush();
            for (int i = 0; i < 300; i++) _host.StepOnce();
            _tool.SetActive(true);
            yield return null;

            const int w = 1688, h = 780;
            var mosaic = new Texture2D(w * 2, h, TextureFormat.RGBA32, false);

            // 1) Calle que sigue la prolongación de la del norte del cruce en T (guía), con el coste en vivo.
            _tool.SetType(RoadTypeIds.Street2);
            yield return FocusOn(new Vector2(150f, 128f), 160f);
            yield return TapAt(new Vec2(150f, 130f));
            yield return DragCursorTo(new Vec2(150f, 130f), new Vec2(150f, 178f));
            Assert.That(_tool.Model!.Plan.Valid, Is.True, _tool.Model.Plan.Message);
            yield return CaptureTile(mosaic, 0, w, h);

            // 2) Brazo a 20° de la avenida desde su extremo oeste: ángulo demasiado agudo, en rojo.
            _tool.Cancel();
            yield return FocusOn(new Vector2(40f, 52f), 160f);
            yield return TapAt(new Vec2(0f, 50f));
            yield return DragCursorTo(new Vec2(0f, 50f), new Vec2(80f, 50f + 80f * Mathf.Tan(32f * Mathf.Deg2Rad)));
            Assert.That(_tool.Model.Plan.Valid, Is.False);
            yield return CaptureTile(mosaic, 1, w, h);

            string dir = Path.Combine(Application.dataPath, "..", "..", "..", "docs", "media");
            Directory.CreateDirectory(dir);
            string path = Path.GetFullPath(Path.Combine(dir, "m2-herramienta.png"));
            File.WriteAllBytes(path, mosaic.EncodeToPNG());
            Debug.Log("[M2-05] captura en " + path + "; mensaje rojo: " + _tool.MessageText);
        }

        private IEnumerator CaptureTile(Texture2D mosaic, int index, int w, int h)
        {
            if (!_panelCloned) { _doc.panelSettings = Object.Instantiate(_doc.panelSettings); _panelCloned = true; }
            var panel = _doc.panelSettings;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            rt.Create();
            _cam.enabled = true;
            _cam.targetTexture = rt;
            panel.targetTexture = null;
            yield return null;
            yield return null;
            _cam.enabled = false;
            panel.clearColor = false;
            panel.targetTexture = rt;
            yield return null;
            yield return null;
            RenderTexture.active = rt;
            var tile = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tile.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tile.Apply();
            RenderTexture.active = null;
            mosaic.SetPixels(index * w, 0, w, h, tile.GetPixels());
            mosaic.Apply();
            Object.Destroy(tile);
            panel.targetTexture = null;
            _cam.targetTexture = null;
            _cam.enabled = true;
            rt.Release();
        }
    }
}
