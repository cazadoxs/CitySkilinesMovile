using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Urbe.Game.CameraRig;
using Urbe.Game.Rendering.Buildings;
using Urbe.Game.Rendering.Zones;
using Urbe.Game.Sim;
using Urbe.Game.Tools.CityTools;
using Urbe.Game.Tools.RoadTool;
using Urbe.Game.UI;
using Urbe.Sim.City;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;

namespace Urbe.Game.Tests
{
    /// <summary>
    /// M3-08: una partida de principio a fin como la jugaría Álvaro, por la interfaz real (clics en los botones del HUD y toques en el mapa con un
    /// dedo virtual): abrir Construir, una calle desde la autopista, calles transversales, zonas (pincel y relleno), bomba de agua, desagüe y eólicas con
    /// vista previa, y subir la velocidad. Después la simulación corre rápido y se comprueba que aparecen edificios en pantalla y sube la población.
    /// Genera docs/media/m3-partida.png (mapa temprano con la barra abierta y las zonas) y docs/media/m3-ciudad.png (ciudad crecida con el panel de edificio).
    /// </summary>
    public class CityPlayTests : InputTestFixture
    {
        private SimHost _host = null!;
        private CityCameraController _rig = null!;
        private Camera _cam = null!;
        private UIDocument _doc = null!;
        private BuildMenuController _menu = null!;
        private BuildingInspector _inspector = null!;
        private CityHudController _hud = null!;
        private RoadToolController _road = null!;
        private BuildingChunkRenderer _br = null!;
        private ZoneOverlayRenderer _zones = null!;
        private Touchscreen _touch = null!;
        private Mouse _mouse = null!;
        private int _touchId = 1;

        public override void Setup()
        {
            base.Setup();
            _touch = InputSystem.AddDevice<Touchscreen>();
            _mouse = InputSystem.AddDevice<Mouse>();
            InputSystem.AddDevice<Keyboard>();
        }

        private IEnumerator LoadMain()
        {
            SceneManager.LoadScene("Main");
            yield return null;
            yield return null;
            _host = Object.FindFirstObjectByType<SimHost>();
            _rig = Object.FindFirstObjectByType<CityCameraController>();
            _cam = _rig.GetComponent<Camera>();
            _doc = Object.FindFirstObjectByType<UIDocument>();
            _menu = Object.FindFirstObjectByType<BuildMenuController>();
            _inspector = Object.FindFirstObjectByType<BuildingInspector>();
            _hud = Object.FindFirstObjectByType<CityHudController>();
            _road = Object.FindFirstObjectByType<RoadToolController>();
            _br = Object.FindFirstObjectByType<BuildingChunkRenderer>();
            _zones = Object.FindFirstObjectByType<ZoneOverlayRenderer>();
            Assert.That(_menu, Is.Not.Null, "la escena lleva BuildMenuController");
            Assert.That(_inspector, Is.Not.Null, "la escena lleva BuildingInspector");
            _host.SetSpeed(SimSpeed.Paused);
            for (int i = 0; i < 6; i++) yield return null; // el diseño de UI Toolkit tarda unos fotogramas en estabilizarse
        }

        // ------------------------------------------------------------------ entrada virtual

        private static Vector2 CenterOf(VisualElement e)
        {
            Vector2 c = e.worldBound.center;
            float k = Screen.width / e.panel.visualTree.resolvedStyle.width;
            return new Vector2(c.x * k, Screen.height - c.y * k);
        }

        /// <summary>Clic de ratón sobre un botón; reintenta (hasta 5 veces) mientras no se cumpla <paramref name="done"/>.</summary>
        private IEnumerator ClickButton(string name, System.Func<bool>? done = null)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                Button? b = _doc.rootVisualElement.Q<Button>(name) ?? _menu.Item(name);
                Assert.That(b, Is.Not.Null, name);
                Set(_mouse.position, CenterOf(b!) + new Vector2(30f, 0f));
                yield return null;
                Set(_mouse.position, CenterOf(b!));
                yield return null;
                yield return null;
                Click(_mouse.leftButton);
                yield return null;
                yield return null;
                yield return null;
                if (done == null || done()) break;
            }
        }

        private IEnumerator FocusOn(Vec2 ground, float height = 280f)
        {
            _rig.Model.SnapTo(new Vector2(ground.X, ground.Z), height);
            for (int i = 0; i < 3; i++) yield return null;
        }

        /// <summary>Encuadra de modo que <paramref name="target"/> quede en el tercio alto de la pantalla, por encima de los paneles de la barra de construcción.</summary>
        private IEnumerator FocusAbove(Vec2 target, float height = 280f)
        {
            yield return FocusOn(new Vec2(target.X, target.Z - height * 0.17f), height);
        }

        private Vector2 ScreenOf(Vec2 g) { Vector3 s = _cam.WorldToScreenPoint(new Vector3(g.X, 0f, g.Z)); return new Vector2(s.x, s.y); }

        private string PickName(Vector2 screenPx)
        {
            var panel = _doc.rootVisualElement.panel;
            VisualElement? v = panel.Pick(RuntimePanelUtils.ScreenToPanel(panel, new Vector2(screenPx.x, Screen.height - screenPx.y)));
            return v == null ? "nada" : v.name + "/" + v.GetType().Name;
        }

        private IEnumerator TapAt(Vec2 g)
        {
            int id = _touchId++;
            // Dos toques seguidos y cercanos serían un doble toque (0,32 s): se espera en tiempo real entre toques, como un dedo humano.
            yield return new WaitForSecondsRealtime(0.4f);
            BeginTouch(id, ScreenOf(g), screen: _touch);
            yield return null;
            EndTouch(id, ScreenOf(g), screen: _touch);
            yield return null;
            yield return null;
        }

        /// <summary>Arrastra un dedo hasta que el CURSOR (desplazado hacia arriba del dedo) quede en <paramref name="to"/>.</summary>
        private IEnumerator DragCursor(Vec2 from, Vec2 to)
        {
            int id = _touchId++;
            Vector2 offset = new Vector2(0f, _road.CursorOffsetPx);
            Vector2 a = ScreenOf(from) - offset, b = ScreenOf(to) - offset;
            BeginTouch(id, a, screen: _touch);
            yield return null;
            for (int i = 1; i <= 12; i++)
            {
                MoveTouch(id, Vector2.Lerp(a, b, i / 12f), screen: _touch);
                yield return null;
            }
            EndTouch(id, b, screen: _touch);
            yield return null;
            yield return null;
            yield return null;
        }

        // ------------------------------------------------------------------ pasos de la partida

        private IEnumerator BuildStreet(Vec2 from, Vec2 to, bool newChain)
        {
            if (newChain) yield return ClickButton("btn-cancel");
            bool vertical = Mathf.Abs(to.Z - from.Z) > Mathf.Abs(to.X - from.X);
            // Las calles que van hacia arriba de la pantalla se encuadran más lejos para que los dos extremos queden entre la barra superior y la inferior.
            // El panel de carreteras (con el selector de 6 tipos) ocupa la mitad baja de la pantalla: el trazo se encuadra en el tercio alto.
            float fh = vertical ? 700f : 300f;
            Vec2 mid = Vec2.Lerp(from, to, 0.5f);
            yield return FocusOn(new Vec2(mid.X, mid.Z - fh * 0.18f), fh);
            yield return TapAt(from);
            yield return TapAt(to);
            Assert.That(_road.Model!.HasEnd, $"el trazo tiene final: {_road.MessageText} from={from.X:F0},{from.Z:F0} ({ScreenOf(from)}) to={to.X:F0},{to.Z:F0} ({ScreenOf(to)}) screen={Screen.width}x{Screen.height} start={_road.Model.HasStart} startPos={_road.Model.Start.X:F0},{_road.Model.Start.Z:F0} overUiTo={PickName(ScreenOf(to))} overUiFrom={PickName(ScreenOf(from))}");
            Assert.That(_road.Model.CanConfirm, "el trazo es válido: " + _road.MessageText);
            long money = _host.City!.Money;
            int before = _host.City.Network.SegmentCount;
            yield return ClickButton("btn-confirm", () => _host.City.Network.SegmentCount > before);
            Assert.That(_host.City.Network.SegmentCount, Is.GreaterThan(before), "la calle se construyó");
            Assert.That(_host.City.Money, Is.LessThan(money), "la calle cuesta dinero de verdad");
        }

        private IEnumerator PlaceViaUi(string button, Vec2 where, ServiceType type)
        {
            yield return ClickButton(button, () => _menu.SelectedService == type);
            Assert.That(_menu.SelectedService, Is.EqualTo(type));
            yield return FocusAbove(where, 240f);
            yield return TapAt(where);
            Assert.That(_menu.ServicePreviewResult, Is.EqualTo(CityResult.Ok), type + ": la vista previa es válida: " + _menu.MessageText);
            int count = _host.City!.Buildings.Count;
            long money = _host.City.Money;
            yield return ClickButton("svc-ok", () => _host.City.Buildings.Count > count);
            Assert.That(_host.City.Buildings.Count, Is.GreaterThan(count), type + " construido");
            Assert.That(_host.City.Money, Is.LessThan(money));
        }

        private int CountServices(ServiceType t)
        {
            int n = 0;
            BuildingSystem bs = _host.City!.Buildings;
            for (int i = 0; i < bs.IndexBound; i++) if (bs.IsAlive(i) && bs.Get(i).Kind == BuildingKind.Service && (ServiceType)bs.Get(i).ServiceType == t) n++;
            return n;
        }

        private void RunDays(float days)
        {
            int ticks = Mathf.RoundToInt(days * CityClock.TicksPerDay);
            for (int i = 0; i < ticks; i++) _host.StepOnce();
        }

        private int ZonedCells(ZoneType z)
        {
            int n = 0;
            ZoneGrid zg = _host.City!.Zones;
            for (int i = 0; i < zg.CellIndexBound; i++) if (zg.IsCellAlive(i) && zg.GetCell(i).Zone == z) n++;
            return n;
        }

        // Las coordenadas de la partida se escribieron para un barrio con la calle principal en z = 0 que sale hacia el este; W lo traslada
        // al barrio nuevo, al norte de la autovía (que cruza el mapa con una curva de +-55 m) y unido a ella por una calle de enganche.
        private const float Dx = 344f, Dz = 335f;
        private static Vec2 W(float x, float z) => new Vec2(x + Dx, z + Dz);
        private static Vec2 Inv(Vec2 w) => new Vec2(w.X - Dx, w.Z - Dz);
        private static bool IsHighwayOrLink(Urbe.Sim.Roads.RoadNetwork net, EntityId seg)
        {
            if (net.GetSegment(seg).RoadTypeId == Urbe.Sim.Roads.RoadTypeIds.Highway4) return true;
            Vec2 mid = net.GetSegment(seg).Curve.Evaluate(0.5f);   // la calle de enganche sube por x = -494 desde la autovía hasta el barrio
            return Mathf.Abs(mid.X - (-838f + Dx)) < 1f && mid.Z < Dz - 1f;
        }

        [UnityTest]
        public IEnumerator PartidaCompleta_PorLaInterfaz_ConstruyeYCrece()
        {
            yield return LoadMain();
            CitySimulation city = _host.City!;
            long startMoney = city.Money;

            // --- consejo inicial y barra de construcción
            Assert.That(_hud.TipVisible);
            yield return ClickButton("btn-tip-close", () => !_hud.TipVisible);
            Assert.That(_hud.TipVisible, Is.False, "el consejo se cierra");
            yield return ClickButton("btn-build", () => _menu.IsOpen);
            Assert.That(_menu.IsOpen, "Construir abre la barra");
            Assert.That(_menu.Category, Is.EqualTo(BuildCategory.Roads));
            Assert.That(_road.IsActive, "Carreteras activa la herramienta de calles");
            foreach (string cat in new[] { "cat-roads", "cat-zones", "cat-utilities", "cat-services", "cat-demolish" })
                Assert.That(_doc.rootVisualElement.Q<Button>(cat).resolvedStyle.minHeight.value, Is.GreaterThanOrEqualTo(48f), cat + " mide 48 dp");

            // --- 1) una calle desde el final de la autopista y dos transversales
            // La partida empieza solo con la autovía: la primera calle sube desde un nodo de la autovía (en dos tramos) hasta el barrio.
            Vec2 anchor = city.HighwayAnchor;
            Vec2 g1 = new Vec2(anchor.X, anchor.Z + 140f), g2 = W(-838f, 0f);
            yield return BuildStreet(anchor, g1, false);
            yield return BuildStreet(g1, g2, false);
            yield return BuildStreet(g2, W(-678f, 0f), false);
            yield return BuildStreet(W(-678f, 0f), W(-518f, 0f), false);
            yield return BuildStreet(W(-518f, 0f), W(-358f, 0f), false);
            yield return BuildStreet(W(-678f, 0f), W(-678f, 160f), true);
            yield return BuildStreet(W(-678f, 0f), W(-678f, -160f), true);
            Assert.That(city.Network.SegmentCount, Is.GreaterThanOrEqualTo(6));

            // --- 2) zonas: relleno residencial en la transversal norte, pincel comercial arrastrando por la calle principal, relleno industrial al sur
            yield return ClickButton("cat-zones", () => _menu.Category == BuildCategory.Zones);
            Assert.That(_menu.Category, Is.EqualTo(BuildCategory.Zones));
            Assert.That(_zones.Visible, "al zonificar se ve la capa de celdas");
            Assert.That(_road.IsActive, Is.False, "al salir de Carreteras la herramienta de calles se apaga");
            Assert.That(_rig.ToolActive, "con el pincel un dedo no mueve la cámara");
            Assert.That(_menu.Item("zone-Office")!.ClassListContains("locked"), "oficinas bloqueadas por hito, con su requisito");
            Assert.That(_menu.Item("zone-Office")!.text, Does.Contain("hab."));
            Assert.That(_menu.Item("dens-3")!.ClassListContains("locked"), "densidad alta bloqueada");

            foreach (Button ib in _menu.SubPanel.Query<Button>().ToList())
            {
                Assert.That(ib.resolvedStyle.minHeight.value, Is.GreaterThanOrEqualTo(48f), ib.name + " min-height");
                Assert.That(ib.resolvedStyle.minWidth.value, Is.GreaterThanOrEqualTo(48f), ib.name + " min-width");
            }
            yield return ClickButton("zmode-fill", () => _menu.ZoneMode == ZonePaintMode.Fill);
            yield return FocusAbove(W(-678f, 80f), 260f);
            yield return TapAt(W(-678f, 80f));
            Assert.That(ZonedCells(ZoneType.Residential), Is.GreaterThan(20), "el relleno pinta residencial a los dos lados: " + _menu.MessageText);

            yield return ClickButton("zone-Industrial", () => _menu.SelectedZone == ZoneType.Industrial);
            yield return FocusAbove(W(-678f, -80f), 260f);
            yield return TapAt(W(-678f, -80f));
            Assert.That(ZonedCells(ZoneType.Industrial), Is.GreaterThan(20), "el relleno pinta industrial");

            yield return ClickButton("zone-Commercial", () => _menu.SelectedZone == ZoneType.Commercial);
            yield return ClickButton("zmode-brush", () => _menu.ZoneMode == ZonePaintMode.Brush);
            yield return ClickButton("zmode-brush"); // Pincel ya elegido: cambia el tamaño a 56 m
            Assert.That(_menu.BrushRadius, Is.EqualTo(28f));
            yield return FocusAbove(W(-520f, 0f), 300f);
            int undoBefore = _host.UndoCount;
            yield return DragCursor(W(-620f, 0f), W(-420f, 0f));
            Assert.That(ZonedCells(ZoneType.Commercial), Is.GreaterThan(20), "el pincel arrastrado pinta comercial: " + _menu.MessageText);
            Assert.That(_host.UndoCount, Is.EqualTo(undoBefore + 1), "un trazo de pincel es una sola orden en el historial");

            // borrar un trozo y deshacer
            int commercial = ZonedCells(ZoneType.Commercial);
            yield return ClickButton("zerase", () => _menu.Erase);
            yield return TapAt(W(-520f, 0f));
            Assert.That(ZonedCells(ZoneType.Commercial), Is.LessThan(commercial), "Borrar quita zona");
            yield return ClickButton("zundo");
            Assert.That(ZonedCells(ZoneType.Commercial), Is.EqualTo(commercial), "Deshacer devuelve lo borrado");
            yield return ClickButton("zerase", () => !_menu.Erase);

            // captura temprana: mapa con la barra abierta, las zonas coloreadas y ningún edificio todavía
            Assert.That(city.Buildings.Count, Is.EqualTo(0), "los edificios aún no han crecido");
            yield return FocusOn(W(-590f, 0f), 330f);
            for (int i = 0; i < 6; i++) yield return null;
            yield return CaptureUtil.Shot(_cam, _doc, 1688, 780, "m3-partida.png");

            // --- 3) agua y luz con vista previa (táctil: tocar para previsualizar y OK para construir)
            yield return ClickButton("cat-utilities", () => _menu.Category == BuildCategory.Utilities);
            Assert.That(_zones.Visible, Is.False);
            yield return ClickButton("svc-WaterPump", () => _menu.SelectedService == ServiceType.WaterPump);
            yield return FocusAbove(W(-700f, 330f), 240f);
            // un sitio sin calle cerca: la vista previa se rechaza con el motivo en texto
            yield return TapAt(W(-700f, 330f));
            Assert.That(_menu.ServicePreviewResult, Is.EqualTo(CityResult.NoRoadAccess));
            Assert.That(_menu.MessageText, Does.Contain("calle"), "motivo en texto: " + _menu.MessageText);
            yield return PlaceViaUi("svc-WaterPump", W(-780f, 44f), ServiceType.WaterPump);
            yield return PlaceViaUi("svc-SewageOutlet", W(-780f, -44f), ServiceType.SewageOutlet);
            yield return PlaceViaUi("svc-WindTurbine", W(-740f, 46f), ServiceType.WindTurbine);
            yield return PlaceViaUi("svc-WindTurbine", W(-740f, -46f), ServiceType.WindTurbine);
            Assert.That(CountServices(ServiceType.WindTurbine), Is.EqualTo(2));
            // servicios aún bloqueados: se ven con el requisito
            yield return ClickButton("cat-services", () => _menu.Category == BuildCategory.Services);
            Assert.That(_menu.Item("svc-Landfill")!.ClassListContains("locked"), "el vertedero espera al hito Aldea");
            Assert.That(_menu.Item("svc-Landfill")!.text, Does.Contain("400 hab."));

            // --- 4) cerrar la barra, subir la velocidad y dejar crecer
            yield return ClickButton("btn-build", () => !_menu.IsOpen);
            Assert.That(_menu.IsOpen, Is.False);
            yield return ClickButton("btn-x3", () => _host.Speed == SimSpeed.X3);
            Assert.That(_host.Speed, Is.EqualTo(SimSpeed.X3), "el botón x3 sube la velocidad");
            long money0 = city.Money;
            var log = new List<string>();
            for (int step = 0; step < 6; step++)
            {
                RunDays(5f);     // la velocidad máxima es la de este bucle: sin esperar al reloj
                yield return null;
                log.Add($"día {(step + 1) * 5}: {city.Buildings.Count} edificios, {city.Stats.Population} hab, dinero {city.Money}, hito {city.MilestoneName}");
            }
            Debug.Log("[M3-08] partida\n" + string.Join("\n", log));
            Assert.That(city.Buildings.Count, Is.GreaterThan(30), "aparecen edificios");
            Assert.That(city.Stats.Population, Is.GreaterThan(50), "sube la población");
            Assert.That(_br.InstanceCount, Is.EqualTo(city.Buildings.Count), "cada edificio tiene su instancia en pantalla");
            _hud.Refresh();
            Assert.That(_hud.PopText, Is.EqualTo(CityText.Population(city.Stats.Population)));
            Assert.That(city.Money, Is.Not.EqualTo(money0));

            // --- 5) ciudad crecida: se amplía con más calles y zonas (órdenes de ciudad) y se deja correr; luego se toca un edificio
            foreach (float x in new[] { -518f, -358f })
                foreach (float s in new[] { 1f, -1f })
                {
                    Assert.That(_host.Execute(new BuildRoadCityCommand(Urbe.Sim.Roads.SegmentRequest.Straight(W(x, 0f), W(x, 160f * s), Urbe.Sim.Roads.RoadTypeIds.Street2))), Is.EqualTo(CityResult.Ok));
                }
            Urbe.Sim.Roads.RoadNetwork net = city.Network;
            for (int i = 0; i < net.SegmentIndexBound; i++)
            {
                EntityId seg = net.SegmentIdAt(i);
                if (!seg.IsValid || IsHighwayOrLink(net, seg)) continue;
                Vec2 mid = Inv(net.GetSegment(seg).Curve.Evaluate(0.5f));
                if (mid.X < -600f && System.Math.Abs(mid.Z) > 5f) continue; // la primera transversal ya está zonificada
                ZoneType z = System.Math.Abs(mid.Z) < 5f ? ZoneType.Commercial : (mid.Z > 0f ? ZoneType.Residential : ZoneType.Industrial);
                host_Execute(PaintZoneCommand.FillSegment(seg, 4, z, 1));
            }
            for (int step = 0; step < 6; step++)
            {
                RunDays(10f);
                yield return null;
            }
            // --- 4b) la industria gasta mucha luz: el aviso lo dice y se pone una central de carbón por la interfaz
            int noPower = 0;
            for (int i = 0; i < city.Buildings.IndexBound; i++) if (city.Buildings.IsAlive(i) && (city.Buildings.Get(i).Problems & BuildingProblems.NoPower) != 0) noPower++;
            Debug.Log($"[M3-08] edificios sin luz tras la ampliación: {noPower}");
            Assert.That(noPower, Is.GreaterThan(0), "con dos eólicas la industria se queda sin luz");
            yield return ClickButton("btn-build", () => _menu.IsOpen);
            yield return ClickButton("cat-utilities", () => _menu.Category == BuildCategory.Utilities);
            yield return ClickButton("svc-CoalPlant", () => _menu.SelectedService == ServiceType.CoalPlant);
            bool coalOk = false;
            foreach (var spot in new[] { W(-598f, 55f), W(-598f, -55f), W(-440f, 55f), W(-440f, -55f), W(-770f, 70f) })
            {
                yield return FocusAbove(spot, 240f);
                yield return TapAt(spot);
                if (_menu.ServicePreviewResult != CityResult.Ok) continue;
                yield return ClickButton("svc-ok", () => CountServices(ServiceType.CoalPlant) > 0);
                coalOk = CountServices(ServiceType.CoalPlant) > 0;
                break;
            }
            Assert.That(coalOk, "la central de carbón se construyó: " + _menu.MessageText);
            yield return ClickButton("btn-build", () => !_menu.IsOpen);
            for (int step = 0; step < 4; step++) { RunDays(10f); yield return null; }

            var flags = new Dictionary<string, int>();
            for (int i = 0; i < city.Buildings.IndexBound; i++)
            {
                if (!city.Buildings.IsAlive(i)) continue;
                Building bb = city.Buildings.Get(i);
                string zone = bb.Kind == BuildingKind.Service ? "servicio" : bb.Zone.ToString();
                foreach (BuildingProblems pf in System.Enum.GetValues(typeof(BuildingProblems)))
                    if (pf != BuildingProblems.None && (bb.Problems & pf) != 0) { string k = zone + ":" + pf; flags[k] = flags.TryGetValue(k, out int c0) ? c0 + 1 : 1; }
            }
            Debug.Log("[M3-08] problemas por zona: " + string.Join(", ", flags));
            Debug.Log($"[M3-08] ciudad: {city.Buildings.Count} edificios, {city.Stats.Population} hab, dinero {city.Money}, hito {city.MilestoneName}, abandonados {city.Stats.AbandonedBuildings}");
            Assert.That(city.Buildings.Count, Is.GreaterThan(80));

            // ampliación 2: la calle principal sigue hacia el este con más transversales; zonas mezcladas, un parque y un centro de salud (ya desbloqueados)
            var seen = new HashSet<int>();
            for (int i = 0; i < net.SegmentIndexBound; i++) if (net.SegmentIdAt(i).IsValid) seen.Add(i);
            Assert.That(city.IsServiceUnlocked(ServiceType.Park), "con el hito Aldea se desbloquea el parque");
            for (float x = -358f; x < -40f; x += 160f)
                Assert.That(_host.Execute(new BuildRoadCityCommand(Urbe.Sim.Roads.SegmentRequest.Straight(W(x, 0f), W(x + 160f, 0f), Urbe.Sim.Roads.RoadTypeIds.Street2))), Is.EqualTo(CityResult.Ok));
            foreach (float x in new[] { -198f, -38f })
                foreach (float sgn in new[] { 1f, -1f })
                    Assert.That(_host.Execute(new BuildRoadCityCommand(Urbe.Sim.Roads.SegmentRequest.Straight(W(x, 0f), W(x, 160f * sgn), Urbe.Sim.Roads.RoadTypeIds.Street2))), Is.EqualTo(CityResult.Ok));
            for (int i = 0; i < net.SegmentIndexBound; i++)
            {
                EntityId seg = net.SegmentIdAt(i);
                if (!seg.IsValid || seen.Contains(i) || IsHighwayOrLink(net, seg)) continue;
                Vec2 mid = Inv(net.GetSegment(seg).Curve.Evaluate(0.5f));
                ZoneType z = System.Math.Abs(mid.Z) < 5f ? ZoneType.Commercial : (mid.Z > 0f ? ZoneType.Residential : (mid.X < -100f ? ZoneType.Industrial : ZoneType.Residential));
                host_Execute(PaintZoneCommand.FillSegment(seg, 4, z, 1));
            }
            host_Execute(new PlaceServiceCommand(ServiceType.Park, W(-300f, 52f)));
            host_Execute(new PlaceServiceCommand(ServiceType.HealthCenter, W(-120f, -52f)));
            for (int step = 0; step < 4; step++) { RunDays(10f); yield return null; }
            // con el hito Pueblo se desbloquean bomberos, policía y colegio: la cobertura sube el nivel de los edificios
            var wanted = new[] { ServiceType.Landfill, ServiceType.FireStation, ServiceType.PoliceStation, ServiceType.School, ServiceType.Park, ServiceType.HealthCenter };
            var spots = new[] { W(-440f, 58f), W(-440f, -58f), W(-280f, 58f), W(-280f, -58f), W(-120f, 58f), W(-600f, -58f), W(-600f, 58f), W(-200f, -90f), W(-360f, 100f) };
            int placed = 0;
            foreach (ServiceType t in wanted)
            {
                if (!city.IsServiceUnlocked(t)) continue;
                foreach (Vec2 sp in spots)
                    if (_host.Execute(new PlaceServiceCommand(t, sp)) == CityResult.Ok) { placed++; break; }
            }
            Debug.Log($"[M3-08] servicios de ciudad colocados: {placed} de {wanted.Length}");
            for (int step = 0; step < 6; step++) { RunDays(10f); yield return null; }
            Debug.Log($"[M3-08] ciudad ampliada: {city.Buildings.Count} edificios, {city.Stats.Population} hab, dinero {city.Money}, hito {city.MilestoneName}, abandonados {city.Stats.AbandonedBuildings}, niveles {LevelHistogram(city)}");
            Assert.That(city.Stats.Population, Is.GreaterThan(655), "la ciudad ampliada tiene más habitantes");

            // tocar un edificio de zona con la barra cerrada abre su panel
            BuildingSystem bs = city.Buildings;
            int pick = -1;
            for (int i = 0; i < bs.IndexBound; i++)
                if (bs.IsAlive(i) && bs.Get(i).Kind == BuildingKind.Zoned && !bs.Get(i).IsAbandoned && (pick < 0 || bs.Get(i).Level > bs.Get(pick).Level)) pick = i;
            Assert.That(pick, Is.GreaterThanOrEqualTo(0));
            Building target = bs.Get(pick);
            yield return FocusOn(target.Pos, 150f);
            yield return TapAt(target.Pos);
            Assert.That(_inspector.IsOpen, "tocar un edificio abre su panel");
            Assert.That(_inspector.TitleText, Does.Contain(CityText.ZoneName(target.Zone)));
            Assert.That(_inspector.BodyText, Does.Contain("Nivel " + target.Level));

            // panel de un servicio: mantenimiento y Derribar
            int svc = -1;
            for (int i = 0; i < bs.IndexBound; i++) if (bs.IsAlive(i) && bs.Get(i).Kind == BuildingKind.Service && (ServiceType)bs.Get(i).ServiceType == ServiceType.WaterPump) svc = i;
            Assert.That(svc, Is.GreaterThanOrEqualTo(0));
            yield return FocusOn(bs.Get(svc).Pos, 120f);
            yield return TapAt(bs.Get(svc).Pos);
            Assert.That(_inspector.TitleText, Is.EqualTo("Bomba de agua"));
            Assert.That(_inspector.BodyText, Does.Contain("Mantenimiento"));
            Assert.That(_inspector.DemolishVisible);

            // Derribar un servicio desde su panel (un parque de prueba) y el panel de Demoler
            Vec2 parkSpot = default;
            foreach (Vec2 sp in new[] { W(-360f, 100f), W(-200f, 100f), W(-520f, 100f), W(-40f, 100f), W(-360f, -100f) })
                if (_host.Execute(new PlaceServiceCommand(ServiceType.Park, sp)) == CityResult.Ok) { parkSpot = sp; break; }
            int parks = CountServices(ServiceType.Park);
            int parkIdx = -1;
            for (int i = 0; i < bs.IndexBound; i++) if (bs.IsAlive(i) && bs.Get(i).Kind == BuildingKind.Service && (ServiceType)bs.Get(i).ServiceType == ServiceType.Park) parkIdx = i;
            Assert.That(parkIdx, Is.GreaterThanOrEqualTo(0));
            yield return FocusOn(bs.Get(parkIdx).Pos, 120f);
            yield return TapAt(bs.Get(parkIdx).Pos);
            Assert.That(_inspector.TitleText, Is.EqualTo("Parque"));
            yield return ClickButton("btn-bp-demolish", () => CountServices(ServiceType.Park) < parks);
            Assert.That(CountServices(ServiceType.Park), Is.EqualTo(parks - 1), "Derribar quita el servicio");
            Assert.That(_inspector.IsOpen, Is.False, "el panel se cierra al derribar");
            yield return ClickButton("btn-build", () => _menu.IsOpen);
            yield return ClickButton("cat-demolish", () => _menu.Category == BuildCategory.Demolish);
            Assert.That(_menu.Item("dem-road"), Is.Not.Null);
            Assert.That(_road.IsActive, "Demoler > Calles usa la herramienta de calles");
            Assert.That(_road.Model!.Mode, Is.EqualTo(RoadToolMode.Demolish));
            yield return ClickButton("dem-zone", () => _menu.Category == BuildCategory.Zones);
            Assert.That(_menu.Erase, "Demoler > Zonas es el pincel de borrar");
            yield return ClickButton("btn-build", () => !_menu.IsOpen);

            // captura de la ciudad crecida, con el panel de un edificio de zona abierto
            _inspector.Select(city, pick);
            yield return FocusOn(W(-300f, 40f), 270f);
            for (int i = 0; i < 8; i++) yield return null;
            Assert.That(_br.LastVisible, Is.GreaterThan(30), "edificios dibujados en pantalla");
            yield return CaptureUtil.Shot(_cam, _doc, 1688, 780, "m3-ciudad.png");
            Assert.That(city.Money, Is.LessThan(startMoney * 10));
        }

        private void host_Execute(CityCommand c) => _host.Execute(c);

        private static string LevelHistogram(CitySimulation city)
        {
            var h = new int[6];
            for (int i = 0; i < city.Buildings.IndexBound; i++)
                if (city.Buildings.IsAlive(i) && city.Buildings.Get(i).Kind == BuildingKind.Zoned) h[city.Buildings.Get(i).Level]++;
            return $"L1 {h[1]}, L2 {h[2]}, L3 {h[3]}, L4 {h[4]}, L5 {h[5]}";
        }
    }
}
