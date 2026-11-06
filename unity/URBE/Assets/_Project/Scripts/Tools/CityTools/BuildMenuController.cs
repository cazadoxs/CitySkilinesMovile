using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using UnityEngine.UIElements;
using Urbe.Game.CameraRig;
using Urbe.Game.Rendering.Zones;
using Urbe.Game.Sim;
using Urbe.Game.Tools.RoadTool;
using Urbe.Game.UI;
using Urbe.Sim.City;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

namespace Urbe.Game.Tools.CityTools
{
    public enum BuildCategory { None, Roads, Zones, Utilities, Services, Demolish }
    public enum ZonePaintMode { Brush, Fill }
    public enum DemolishKind { Road, Service, Zone }

    /// <summary>
    /// Barra de construcción por categorías (M3-08, GDD 12.7 y docs/figma.md): Carreteras (la herramienta de M2), Zonas (R, C, I, O, densidad,
    /// pincel, relleno y borrar), Agua y luz, Servicios y Demoler. Lo bloqueado por hito se ve con su requisito. Colocar un servicio muestra una vista previa
    /// de su huella (verde válida, roja con el motivo en texto) y se confirma con OK (táctil) o con un clic (ratón). Las órdenes son las de Sim.Core
    /// (<see cref="PaintZoneCommand"/>, <see cref="PlaceServiceCommand"/>) y cuestan dinero real. Textos en español.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class BuildMenuController : MonoBehaviour
    {
        public static readonly float[] BrushSizes = { 16f, 32f, 56f };
        private static readonly Color Valid = new Color(0.25f, 0.80f, 0.42f, 0.55f);
        private static readonly Color Invalid = new Color(0.95f, 0.20f, 0.20f, 0.62f);
        private static readonly Color Outline = new Color(1f, 1f, 1f, 0.9f);
        private static readonly ServiceType[] Utilities = { ServiceType.WaterPump, ServiceType.SewageOutlet, ServiceType.WindTurbine, ServiceType.CoalPlant };
        private static readonly ServiceType[] Services =
            { ServiceType.Landfill, ServiceType.HealthCenter, ServiceType.FireStation, ServiceType.PoliceStation, ServiceType.School, ServiceType.HighSchool, ServiceType.Park };

        [SerializeField] private SimHost host = null!;
        [SerializeField] private CityCameraController? cameraController;
        [SerializeField] private RoadToolController? roadTool;
        [SerializeField] private RoadGhostRenderer? overlay;
        [SerializeField] private ZoneOverlayRenderer? zoneOverlay;
        [SerializeField] private CityHudController? hud;

        private VisualElement _root = null!;
        private VisualElement _sub = null!, _catBar = null!;
        private Label _message = null!;
        private Button _build = null!, _traffic = null!;
        private readonly Dictionary<BuildCategory, Button> _catButtons = new Dictionary<BuildCategory, Button>();
        private readonly Dictionary<string, Button> _items = new Dictionary<string, Button>();

        private bool _open;
        private BuildCategory _cat = BuildCategory.None;
        private ZoneType _zone = ZoneType.Residential;
        private int _density = 1;
        private ZonePaintMode _zoneMode = ZonePaintMode.Brush;
        private bool _erase;
        private int _brushIndex = 1;
        private DemolishKind _demolish = DemolishKind.Road;
        private ServiceType _service = ServiceType.None;
        private int _lastMilestone = -1;

        // vista previa
        private bool _hasCursor;
        private Vec2 _cursor;
        private Vec2 _svcPos, _svcFront;
        private CityResult _svcResult = CityResult.InvalidArgument;
        private bool _svcSnapped;
        private bool _previewDirty;
        private readonly List<Vec2> _stroke = new List<Vec2>();
        private bool _dragging;
        private Vector2 _lastHoverPx;
        private string _note = "";
        private float _noteUntil;
        private EntityId _fillSegment = EntityId.None;
        private bool _touchMode;
        private bool _narrow;

        // ------------------------------------------------------------------ estado público (pruebas e inspector)

        public bool IsOpen => _open;
        public BuildCategory Category => _cat;
        public ZoneType SelectedZone => _zone;
        public int Density => _density;
        public ZonePaintMode ZoneMode => _zoneMode;
        public bool Erase => _erase;
        public float BrushRadius => BrushSizes[_brushIndex] * 0.5f;
        public ServiceType SelectedService => _service;
        public CityResult ServicePreviewResult => _svcResult;
        public Vec2 ServicePreviewPos => _svcPos;
        public string MessageText => _message.text;
        public bool MessageVisible => _message.ClassListContains("show");
        public VisualElement SubPanel => _sub;
        public Button? Item(string name) => _items.TryGetValue(name, out Button? b) ? b : null;
        public Button CategoryButton(BuildCategory c) => _catButtons[c];

        /// <summary>true si un toque en el mapa lo consume una herramienta (si no, el toque inspecciona un edificio).</summary>
        public bool WantsTaps
        {
            get
            {
                if (!_open) return false;
                switch (_cat)
                {
                    case BuildCategory.Roads: case BuildCategory.Zones: return true;
                    case BuildCategory.Utilities: case BuildCategory.Services: return _service != ServiceType.None;
                    case BuildCategory.Demolish: return _demolish == DemolishKind.Road;
                    default: return false;
                }
            }
        }

        public event Action? Changed;

        // ------------------------------------------------------------------ ciclo de vida

        private void OnEnable()
        {
            if (host == null) host = FindFirstObjectByType<SimHost>();
            if (cameraController == null) cameraController = FindFirstObjectByType<CityCameraController>();
            if (roadTool == null) roadTool = FindFirstObjectByType<RoadToolController>();
            if (hud == null) hud = FindFirstObjectByType<CityHudController>();
            _root = GetComponent<UIDocument>().rootVisualElement;
            _sub = _root.Q<VisualElement>("sub-panel");
            _catBar = _root.Q<VisualElement>("category-bar");
            _message = _root.Q<Label>("menu-message");
            _build = _root.Q<Button>("btn-build");
            _traffic = _root.Q<Button>("btn-traffic");
            Hook("cat-roads", BuildCategory.Roads);
            Hook("cat-zones", BuildCategory.Zones);
            Hook("cat-utilities", BuildCategory.Utilities);
            Hook("cat-services", BuildCategory.Services);
            Hook("cat-demolish", BuildCategory.Demolish);
            _build.clicked += ToggleOpen;
            if (cameraController != null)
            {
                cameraController.Tapped += OnTapped;
                cameraController.ToolDragged += OnToolDragged;
            }
            if (host != null) host.SimulationChanged += OnSimulationChanged;
            RefreshChrome();
        }

        private void Hook(string name, BuildCategory cat)
        {
            Button b = _root.Q<Button>(name);
            _catButtons[cat] = b;
            b.clicked += () => SelectCategory(cat);
        }

        private void OnDisable()
        {
            if (_build != null) _build.clicked -= ToggleOpen;
            if (cameraController != null)
            {
                cameraController.Tapped -= OnTapped;
                cameraController.ToolDragged -= OnToolDragged;
            }
            if (host != null) host.SimulationChanged -= OnSimulationChanged;
            if (zoneOverlay != null) zoneOverlay.Visible = false;
        }

        private void OnSimulationChanged(Urbe.Sim.Traffic.TrafficSimulation _)
        {
            _lastMilestone = -1;
            if (_open) Close();
        }

        // ------------------------------------------------------------------ apertura y categorías

        public void ToggleOpen() { if (_open) Close(); else Open(); }

        public void Open()
        {
            if (_open) return;
            _open = true;
            SelectCategory(BuildCategory.Roads);
        }

        public void Close()
        {
            _open = false;
            SelectCategory(BuildCategory.None);
        }

        public void SelectCategory(BuildCategory cat)
        {
            if (cat != BuildCategory.None) _open = true;
            // Se apaga lo anterior antes de encender lo nuevo (la herramienta de carreteras reinicia ToolActive de la cámara).
            bool roadWanted = cat == BuildCategory.Roads || (cat == BuildCategory.Demolish && _demolish == DemolishKind.Road);
            if (roadTool != null && !roadWanted) roadTool.SetActive(false);
            _stroke.Clear();
            _dragging = false;
            _hasCursor = false;
            _fillSegment = EntityId.None;
            _cat = cat;
            if (cat != BuildCategory.Utilities && cat != BuildCategory.Services) _service = ServiceType.None;
            if (cat == BuildCategory.Utilities && Array.IndexOf(Utilities, _service) < 0) _service = ServiceType.None;
            if (cat == BuildCategory.Services && Array.IndexOf(Services, _service) < 0) _service = ServiceType.None;
            if (roadTool != null && roadWanted)
            {
                roadTool.SetActive(true);
                if (cat == BuildCategory.Demolish) roadTool.SetMode(RoadToolMode.Demolish);
                else if (roadTool.Model != null && roadTool.Model.Mode == RoadToolMode.Demolish) roadTool.SetMode(RoadToolMode.Straight);
            }
            if (cameraController != null && !roadWanted) cameraController.ToolActive = cat == BuildCategory.Zones;
            if (cat == BuildCategory.Zones) EnsureUnlockedSelection();
            RebuildSubPanel();
            RefreshChrome();
            _previewDirty = true;
            ShowMessage(HelpFor(cat), 0f);
            Changed?.Invoke();
        }

        private string HelpFor(BuildCategory cat)
        {
            switch (cat)
            {
                case BuildCategory.Zones: return "Elige tipo y densidad; arrastra para pintar zona junto a las calles.";
                case BuildCategory.Utilities: return "Elige una instalación y tócala en el mapa junto a una calle.";
                case BuildCategory.Services: return "Elige un servicio y tócalo en el mapa junto a una calle.";
                case BuildCategory.Demolish: return "Elige qué quieres derribar.";
                default: return "";
            }
        }

        private void RefreshChrome()
        {
            _build.text = _open ? "Cerrar" : "Construir";
            _build.EnableInClassList("toggled", _open);
            _catBar.EnableInClassList("open", _open);
            foreach (var kv in _catButtons) kv.Value.EnableInClassList("toggled", _open && kv.Key == _cat);
            bool sub = _open && (_cat == BuildCategory.Zones || _cat == BuildCategory.Utilities || _cat == BuildCategory.Services || _cat == BuildCategory.Demolish);
            _sub.EnableInClassList("open", sub);
            if (zoneOverlay != null) zoneOverlay.Visible = _open && _cat == BuildCategory.Zones;
            _traffic.style.display = _open ? DisplayStyle.None : DisplayStyle.Flex; // la barra de categorías ocupa su sitio
            _root.Q<VisualElement>("hud-root")?.EnableInClassList("menu-open", _open);
        }

        // ------------------------------------------------------------------ paneles

        private Button Btn(string name, string text, Action click, string? cls = null)
        {
            var b = new Button(click) { text = text, name = name };
            b.AddToClassList("hud-button");
            b.AddToClassList("item");
            if (cls != null) b.AddToClassList(cls);
            _items[name] = b;
            return b;
        }

        private VisualElement Group()
        {
            var g = new VisualElement();
            g.AddToClassList("tool-group");
            return g;
        }

        private static ZoneType[] ZoneTypes => new[] { ZoneType.Residential, ZoneType.Commercial, ZoneType.Industrial, ZoneType.Office };

        private static int MinDensity(ZoneType z) => z == ZoneType.Office ? 2 : 1;

        private static int[] DensitiesOf(ZoneType z)
        {
            switch (z)
            {
                case ZoneType.Residential: return new[] { 1, 2, 3 };
                case ZoneType.Commercial: return new[] { 1, 2 };
                case ZoneType.Office: return new[] { 2, 3 };
                default: return new[] { 1 };
            }
        }

        private static string DensityLabel(ZoneType z, int d)
        {
            if (z == ZoneType.Commercial) return d == 1 ? "Baja" : "Alta";
            return d == 1 ? "Baja" : (d == 2 ? "Media" : "Alta");
        }

        private static string ShortZoneName(ZoneType z)
        {
            switch (z)
            {
                case ZoneType.Residential: return "Resid.";
                case ZoneType.Commercial: return "Comerc.";
                case ZoneType.Industrial: return "Indust.";
                default: return "Oficin.";
            }
        }

        private bool IsNarrow() { float w = _root.resolvedStyle.width; return !float.IsNaN(w) && w > 0f && w < 520f; }

        private static string ZoneLetter(ZoneType z) => z == ZoneType.Residential ? "r" : (z == ZoneType.Commercial ? "c" : (z == ZoneType.Industrial ? "i" : "o"));

        private void RebuildSubPanel()
        {
            _sub.Clear();
            _items.Clear();
            CitySimulation? city = host != null ? host.City : null;
            if (city == null) return;
            _lastMilestone = city.MilestoneIndex;
            switch (_cat)
            {
                case BuildCategory.Zones: BuildZonesPanel(city); break;
                case BuildCategory.Utilities: BuildServicePanel(city, Utilities); break;
                case BuildCategory.Services: BuildServicePanel(city, Services); break;
                case BuildCategory.Demolish: BuildDemolishPanel(); break;
            }
        }

        private void BuildZonesPanel(CitySimulation city)
        {
            VisualElement g1 = Group();
            foreach (ZoneType z in ZoneTypes)
            {
                ZoneType zz = z;
                int md = MinDensity(z);
                bool locked = !city.IsZoneUnlocked(z, md);
                string label = _narrow ? ShortZoneName(z) : CityText.ZoneName(z);
                if (locked) label += "\n" + CityText.ShortRequirement(city.Balance.ZoneMilestone(z, md), city.Balance);
                Button b = Btn("zone-" + z, label, () => SelectZone(zz), "zone-" + ZoneLetter(z));
                b.EnableInClassList("locked", locked);
                b.EnableInClassList("toggled", _zone == z);
                g1.Add(b);
            }
            _sub.Add(g1);

            int[] dens = DensitiesOf(_zone);
            if (dens.Length > 1)
            {
                VisualElement g2 = Group();
                foreach (int d in dens)
                {
                    int dd = d;
                    bool locked = !city.IsZoneUnlocked(_zone, d);
                    string label = DensityLabel(_zone, d);
                    if (locked) label += "\n" + CityText.ShortRequirement(city.Balance.ZoneMilestone(_zone, d), city.Balance);
                    Button b = Btn("dens-" + d, label, () => SelectDensity(dd));
                    b.EnableInClassList("locked", locked);
                    b.EnableInClassList("toggled", _density == d);
                    g2.Add(b);
                }
                _sub.Add(g2);
            }

            VisualElement g3 = Group();
            g3.Add(Btn("zmode-brush", "Pincel\n" + BrushSizes[_brushIndex] + " m", OnBrushButton));
            g3.Add(Btn("zmode-fill", "Relleno", () => SetZoneMode(ZonePaintMode.Fill)));
            g3.Add(Btn("zerase", "Borrar", () => SetErase(!_erase), "zone-erase"));
            _sub.Add(g3);

            VisualElement g4 = Group();
            g4.Add(Btn("zundo", "Deshacer", Undo));
            _sub.Add(g4);
            RefreshZoneButtons();
        }

        private void RefreshZoneButtons()
        {
            if (_cat != BuildCategory.Zones) return;
            foreach (ZoneType z in ZoneTypes) if (_items.TryGetValue("zone-" + z, out Button? b)) b.EnableInClassList("toggled", _zone == z && !_erase);
            foreach (int d in new[] { 1, 2, 3 }) if (_items.TryGetValue("dens-" + d, out Button? b)) b.EnableInClassList("toggled", _density == d && !_erase);
            if (_items.TryGetValue("zmode-brush", out Button? bb)) bb.EnableInClassList("toggled", _zoneMode == ZonePaintMode.Brush);
            if (_items.TryGetValue("zmode-fill", out Button? bf)) bf.EnableInClassList("toggled", _zoneMode == ZonePaintMode.Fill);
            if (_items.TryGetValue("zerase", out Button? be)) be.EnableInClassList("toggled", _erase);
            if (_items.TryGetValue("zmode-brush", out Button? bs)) bs.text = "Pincel\n" + BrushSizes[_brushIndex] + " m";
        }

        private void BuildServicePanel(CitySimulation city, ServiceType[] list)
        {
            VisualElement g = Group();
            foreach (ServiceType t in list)
            {
                ServiceType tt = t;
                ServiceInfo info = city.Balance.Service(t);
                bool locked = !city.IsServiceUnlocked(t);
                string sub = locked ? CityText.ShortRequirement(info.UnlockMilestone, city.Balance) : CityText.Money(info.Cost);
                Button b = Btn("svc-" + t, CityText.ServiceName(t) + "\n" + sub, () => SelectService(tt));
                b.EnableInClassList("locked", locked);
                b.EnableInClassList("toggled", _service == t);
                g.Add(b);
            }
            _sub.Add(g);
            VisualElement g2 = Group();
            g2.Add(Btn("svc-undo", "Deshacer", Undo));
            Button ok = Btn("svc-ok", "OK", () => ConfirmService(), "tool-confirm");
            ok.SetEnabled(false);
            g2.Add(ok);
            _sub.Add(g2);
        }

        private void BuildDemolishPanel()
        {
            VisualElement g = Group();
            g.Add(Btn("dem-road", "Calles", () => SetDemolish(DemolishKind.Road)));
            g.Add(Btn("dem-service", "Servicios", () => SetDemolish(DemolishKind.Service)));
            g.Add(Btn("dem-zone", "Zonas", () => SetDemolish(DemolishKind.Zone)));
            _sub.Add(g);
            VisualElement g2 = Group();
            g2.Add(Btn("dem-undo", "Deshacer", Undo));
            _sub.Add(g2);
            foreach (var kv in new[] { ("dem-road", DemolishKind.Road), ("dem-service", DemolishKind.Service), ("dem-zone", DemolishKind.Zone) })
                _items[kv.Item1].EnableInClassList("toggled", _demolish == kv.Item2);
        }

        // ------------------------------------------------------------------ acciones de los botones

        public void SelectZone(ZoneType z)
        {
            CitySimulation? city = host.City;
            if (city == null) return;
            int md = MinDensity(z);
            if (!city.IsZoneUnlocked(z, md))
            {
                ShowMessage(CityText.ZoneName(z) + ": " + CityText.Requirement(city.Balance.ZoneMilestone(z, md), city.Balance) + ".", 3.5f, true);
                return;
            }
            _zone = z;
            _erase = false;
            if (Array.IndexOf(DensitiesOf(z), _density) < 0 || !city.IsZoneUnlocked(z, _density)) _density = md;
            RebuildSubPanel();
            ShowMessage(BrushText(), 0f);
            _previewDirty = true;
            Changed?.Invoke();
        }

        public void SelectDensity(int d)
        {
            CitySimulation? city = host.City;
            if (city == null) return;
            if (!city.IsZoneUnlocked(_zone, d))
            {
                ShowMessage("Densidad " + DensityLabel(_zone, d).ToLowerInvariant() + ": " + CityText.Requirement(city.Balance.ZoneMilestone(_zone, d), city.Balance) + ".", 3.5f, true);
                return;
            }
            _density = d;
            _erase = false;
            RefreshZoneButtons();
            ShowMessage(BrushText(), 0f);
            _previewDirty = true;
            Changed?.Invoke();
        }

        /// <summary>Pincel: si ya estaba elegido, cambia el tamaño (16, 32, 56 m); si no, lo elige.</summary>
        private void OnBrushButton()
        {
            if (_zoneMode == ZonePaintMode.Brush) CycleBrush();
            else SetZoneMode(ZonePaintMode.Brush);
        }

        public void SetZoneMode(ZonePaintMode m)
        {
            _zoneMode = m;
            _stroke.Clear();
            RefreshZoneButtons();
            ShowMessage(BrushText(), 0f);
            _previewDirty = true;
            Changed?.Invoke();
        }

        public void SetErase(bool on)
        {
            _erase = on;
            RefreshZoneButtons();
            ShowMessage(BrushText(), 0f);
            _previewDirty = true;
            Changed?.Invoke();
        }

        public void CycleBrush()
        {
            _brushIndex = (_brushIndex + 1) % BrushSizes.Length;
            RefreshZoneButtons();
            ShowMessage(BrushText(), 0f);
            _previewDirty = true;
        }

        private void EnsureUnlockedSelection()
        {
            CitySimulation? city = host != null ? host.City : null;
            if (city == null) return;
            if (!city.IsZoneUnlocked(_zone, MinDensity(_zone))) _zone = ZoneType.Residential;
            if (!city.IsZoneUnlocked(_zone, _density)) _density = MinDensity(_zone);
        }

        private string BrushText()
        {
            if (_erase) return _zoneMode == ZonePaintMode.Brush ? "Borrar zona: arrastra sobre las celdas." : "Borrar zona: toca una calle para quitar la zona de sus lados.";
            string kind = CityText.ZoneName(_zone) + " " + (DensitiesOf(_zone).Length > 1 ? DensityLabel(_zone, _density).ToLowerInvariant() : "");
            return _zoneMode == ZonePaintMode.Brush ? kind.Trim() + ": arrastra para pintar (pincel " + BrushSizes[_brushIndex] + " m)." : kind.Trim() + ": toca una calle para zonificar sus lados.";
        }

        public void SetDemolish(DemolishKind k)
        {
            _demolish = k;
            if (k == DemolishKind.Zone)
            {
                _erase = true;
                _zoneMode = ZonePaintMode.Brush;
                SelectCategory(BuildCategory.Zones);
                return;
            }
            SelectCategory(BuildCategory.Demolish);
            if (k == DemolishKind.Service) ShowMessage("Toca un edificio de servicio y pulsa Derribar en su panel.", 0f);
        }

        public void SelectService(ServiceType t)
        {
            CitySimulation? city = host.City;
            if (city == null) return;
            ServiceInfo info = city.Balance.Service(t);
            if (!city.IsServiceUnlocked(t))
            {
                ShowMessage(CityText.ServiceName(t) + ": " + CityText.Requirement(info.UnlockMilestone, city.Balance) + ".", 3.5f, true);
                return;
            }
            _service = t;
            _hasCursor = false;
            _svcResult = CityResult.InvalidArgument;
            foreach (var kv in _items) if (kv.Key.StartsWith("svc-", StringComparison.Ordinal)) kv.Value.EnableInClassList("toggled", kv.Key == "svc-" + t);
            if (_items.TryGetValue("svc-ok", out Button? ok)) ok.SetEnabled(false);
            ShowMessage(CityText.ServiceName(t) + " · " + CityText.Money(info.Cost) + " · " + CityText.ServiceEffect(t) + ". Tócalo en el mapa junto a una calle.", 0f);
            _previewDirty = true;
            Changed?.Invoke();
        }

        public void Undo()
        {
            if (host.UndoLast())
            {
                if (zoneOverlay != null) zoneOverlay.MarkDirty();
                ShowMessage("Deshecho.", 2f);
            }
        }

        // ------------------------------------------------------------------ mensajes

        private void ShowMessage(string text, float seconds, bool bad = false)
        {
            _note = text;
            _noteUntil = seconds > 0f ? Time.unscaledTime + seconds : float.PositiveInfinity;
            _message.text = text;
            _message.EnableInClassList("show", text.Length > 0 && _open);
            _message.EnableInClassList("tool-bad", bad);
        }

        private void ClearMessageIfExpired()
        {
            if (_note.Length > 0 && Time.unscaledTime >= _noteUntil)
            {
                string back = HelpFor(_cat);
                if (_cat == BuildCategory.Zones) back = BrushText();
                ShowMessage(back, 0f);
            }
        }

        // ------------------------------------------------------------------ entrada

        private static bool UsingTouch => EnhancedTouchSupport.enabled && Touch.activeTouches.Count > 0;

        private static bool PointerDown()
        {
            if (UsingTouch) return true;
            Mouse m = Mouse.current;
            return m != null && m.leftButton.isPressed;
        }

        private bool TryGround(Vector2 screenPx, bool offset, out Vec2 ground)
        {
            ground = default;
            if (cameraController == null) return false;
            float off = offset && UsingTouch && roadTool != null ? roadTool.CursorOffsetPx : 0f;
            if (!cameraController.Model.TryGroundPoint(screenPx + new Vector2(0f, off), out Vector2 g)) return false;
            ground = new Vec2(g.x, g.y);
            return true;
        }

        /// <summary>Toque o clic en el mapa (píxeles de pantalla). Público para que las pruebas usen el mismo camino que la entrada real.</summary>
        public void OnTapped(Vector2 screen)
        {
            if (!_open || host == null || host.City == null) return;
            if (!TryGround(screen, false, out Vec2 g)) return;
            switch (_cat)
            {
                case BuildCategory.Zones:
                    if (_zoneMode == ZonePaintMode.Brush) PaintBrush(new List<Vec2> { g });
                    else PaintFill(g);
                    break;
                case BuildCategory.Utilities:
                case BuildCategory.Services:
                    if (_service == ServiceType.None) return;
                    UpdateServicePreview(g);
                    if (!_touchMode && _svcResult == CityResult.Ok) ConfirmService();
                    else if (_svcResult != CityResult.Ok) ShowMessage(ServiceMessage(), 5f, true);
                    else ShowMessage(ServiceMessage() + " Pulsa OK para construir.", 0f);
                    break;
            }
        }

        public void OnToolDragged(Vector2 from, Vector2 to)
        {
            if (!_open || _cat != BuildCategory.Zones || _zoneMode != ZonePaintMode.Brush) return;
            if (!TryGround(to, true, out Vec2 g)) return;
            _dragging = true;
            _cursor = g;
            _hasCursor = true;
            float spacing = Mathf.Max(4f, BrushRadius * 0.6f);
            if (_stroke.Count == 0 || Vec2.Distance(_stroke[_stroke.Count - 1], g) >= spacing) _stroke.Add(g);
            _previewDirty = true;
        }

        private void Update()
        {
            TrackPointerKind();
            if (!_open || host == null || host.City == null) { return; }
            CitySimulation city = host.City;
            if (city.MilestoneIndex != _lastMilestone || IsNarrow() != _narrow) { _narrow = IsNarrow(); _sub.EnableInClassList("narrow", _narrow); RebuildSubPanel(); }
            ClearMessageIfExpired();
            if (_dragging && !PointerDown()) CommitStroke();

            Keyboard kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame)
            {
                if (_service != ServiceType.None) { _service = ServiceType.None; RebuildSubPanel(); _previewDirty = true; }
                else Close();
            }
            if (kb != null && kb.enterKey.wasPressedThisFrame && _service != ServiceType.None) ConfirmService();

            // Ratón sin pulsar: la vista previa sigue al cursor.
            Mouse mouse = Mouse.current;
            if (mouse != null && !UsingTouch && !mouse.leftButton.isPressed && cameraController != null)
            {
                Vector2 pos = mouse.position.ReadValue();
                if ((pos - _lastHoverPx).sqrMagnitude > 4f && !(cameraController.IsOverUi?.Invoke(pos) ?? false) && TryGround(pos, false, out Vec2 g))
                {
                    _lastHoverPx = pos;
                    if (_cat == BuildCategory.Zones)
                    {
                        _cursor = g; _hasCursor = true; _previewDirty = true;
                        if (_zoneMode == ZonePaintMode.Fill) _fillSegment = NearestSegment(g);
                    }
                    else if ((_cat == BuildCategory.Utilities || _cat == BuildCategory.Services) && _service != ServiceType.None)
                    {
                        UpdateServicePreview(g);
                        ShowMessage(ServiceMessage(), 0f, _svcResult != CityResult.Ok);
                    }
                }
            }
        }

        /// <summary>Recuerda si el último puntero fue un dedo o el ratón: con dedo colocar un servicio pide OK; con ratón, un clic basta.</summary>
        private void TrackPointerKind()
        {
            if (UsingTouch) { _touchMode = true; return; }
            Mouse m = Mouse.current;
            if (m != null && (m.delta.ReadValue().sqrMagnitude > 0.01f || m.leftButton.wasPressedThisFrame)) _touchMode = false;
        }

        private void LateUpdate()
        {
            if (!_previewDirty || overlay == null) return;
            _previewDirty = false;
            RenderPreview();
        }

        // ------------------------------------------------------------------ zonas

        private ZoneType EffectiveZone => _erase ? ZoneType.None : _zone;

        private void PaintBrush(List<Vec2> points)
        {
            CitySimulation city = host.City!;
            var cmds = new List<CityCommand>(points.Count);
            foreach (Vec2 p in points) cmds.Add(PaintZoneCommand.Brush(p, BrushRadius, EffectiveZone, _density));
            CityCommand cmd = cmds.Count == 1 ? cmds[0] : new GroupCityCommand(cmds);
            CityResult r = host.Execute(cmd);
            AfterZone(r, city, cmd);
        }

        private void PaintFill(Vec2 g)
        {
            CitySimulation city = host.City!;
            EntityId seg = NearestSegment(g);
            if (!seg.IsValid) { ShowMessage("Toca cerca de una calle para zonificar sus lados.", 3f, true); return; }
            CityCommand cmd = PaintZoneCommand.FillSegment(seg, city.Balance.ZoneMaxDepth, EffectiveZone, _density);
            AfterZone(host.Execute(cmd), city, cmd);
        }

        private void CommitStroke()
        {
            _dragging = false;
            if (_stroke.Count == 0) return;
            var pts = new List<Vec2>(_stroke);
            _stroke.Clear();
            _previewDirty = true;
            PaintBrush(pts);
        }

        private void AfterZone(CityResult r, CitySimulation city, CityCommand cmd)
        {
            if (zoneOverlay != null) zoneOverlay.MarkDirty();
            _previewDirty = true;
            if (r == CityResult.Ok) ShowMessage(_erase ? "Zona borrada." : CityText.ZoneName(_zone) + " pintada. Los edificios aparecerán si hay demanda, agua y luz.", 3f);
            else ShowMessage(CityText.Reject(r, city), 3.5f, true);
            Changed?.Invoke();
        }

        private EntityId NearestSegment(Vec2 g)
        {
            CitySimulation? city = host.City;
            if (city == null) return EntityId.None;
            if (!city.Network.NearestSegment(g, 30f, out EntityId seg, out _, out _)) return EntityId.None;
            return seg == city.OutsideSegment ? EntityId.None : seg;
        }

        // ------------------------------------------------------------------ servicios

        private string ServiceMessage()
        {
            CitySimulation city = host.City!;
            ServiceInfo info = city.Balance.Service(_service);
            string head = CityText.ServiceName(_service) + " · " + CityText.Money(info.Cost);
            if (!_hasCursor) return head + " · " + CityText.ServiceEffect(_service) + ".";
            if (_svcResult == CityResult.Ok) return head + " · " + CityText.ServiceEffect(_service) + ".";
            return head + " · " + CityText.Reject(_svcResult, city);
        }

        /// <summary>Calcula dónde acabará el servicio (se ajusta a la calle más cercana y mira a ella) y si se puede construir ahí.</summary>
        public void UpdateServicePreview(Vec2 want)
        {
            CitySimulation city = host.City!;
            _cursor = want;
            _hasCursor = true;
            ServiceInfo info = city.Balance.Service(_service);
            float halfD = info.Depth * ZoneGrid.CellHalf;
            _svcSnapped = false;
            _svcPos = want;
            _svcFront = new Vec2(0f, 1f);
            if (city.Network.NearestSegment(want, 60f + halfD, out EntityId seg, out float t, out _))
            {
                ref readonly Segment sg = ref city.Network.GetSegment(seg);
                float roadHalf = city.Network.Types.Get(sg.RoadTypeId).TotalWidth * 0.5f;
                Vec2 onRoad = sg.Curve.Evaluate(t);
                Vec2 outward = want - onRoad;
                if (outward.LengthSquared < 0.01f) outward = sg.Curve.Tangent(t).Perp;
                outward = outward.Normalized;
                _svcPos = onRoad + outward * (roadHalf + 1f + halfD);
                _svcFront = -outward;
                _svcSnapped = true;
            }
            _svcResult = new PlaceServiceCommand(_service, want).Validate(city);
            if (_items.TryGetValue("svc-ok", out Button? ok)) ok.SetEnabled(_svcResult == CityResult.Ok);
            _previewDirty = true;
        }

        /// <summary>Construye el servicio de la vista previa. Devuelve true si se construyó; si no, el motivo queda en el mensaje.</summary>
        public bool ConfirmService()
        {
            CitySimulation? city = host.City;
            if (city == null || _service == ServiceType.None || !_hasCursor) return false;
            ServiceInfo info = city.Balance.Service(_service);
            CityResult r = host.Execute(new PlaceServiceCommand(_service, _cursor));
            if (r != CityResult.Ok)
            {
                _svcResult = r;
                ShowMessage(CityText.ServiceName(_service) + ": " + CityText.Reject(r, city), 5f, true);
                _previewDirty = true;
                return false;
            }
            hud?.ShowToast(CityText.ServiceName(_service) + " construido (−" + CityText.Money(info.Cost) + ").", CityHudController.ToastKind.Info, 3f);
            _hasCursor = false;
            _svcResult = CityResult.InvalidArgument;
            if (_items.TryGetValue("svc-ok", out Button? ok)) ok.SetEnabled(false);
            _previewDirty = true;
            ShowMessage(CityText.ServiceName(_service) + " construido. Puedes colocar otro o elegir otra cosa.", 3f);
            Changed?.Invoke();
            return true;
        }

        // ------------------------------------------------------------------ vista previa

        private void RenderPreview()
        {
            RoadGhostRenderer g = overlay!;
            g.BeginOverlay();
            CitySimulation? city = host != null ? host.City : null;
            if (_open && city != null)
            {
                float dist = cameraController != null ? cameraController.Model.Distance : 100f;
                float w = Mathf.Clamp(dist * 0.004f, 0.3f, 4f);
                if (_cat == BuildCategory.Zones)
                {
                    Color zc = _erase ? new Color(0.95f, 0.25f, 0.2f, 1f) : (Color)ZoneOverlayRenderer.ColorOf(_zone);
                    if (_zoneMode == ZonePaintMode.Brush)
                    {
                        foreach (Vec2 p in _stroke) g.AddDisc(p, BrushRadius, new Color(zc.r, zc.g, zc.b, 0.30f), 24, 0.18f);
                        if (_hasCursor)
                        {
                            g.AddDisc(_cursor, BrushRadius, new Color(zc.r, zc.g, zc.b, 0.25f), 24, 0.2f);
                            g.AddRing(_cursor, BrushRadius, w, new Color(zc.r, zc.g, zc.b, 0.95f), 40, 0.28f);
                        }
                    }
                    else if (_fillSegment.IsValid && city.Network.IsSegmentAlive(_fillSegment))
                    {
                        ref readonly Segment sg = ref city.Network.GetSegment(_fillSegment);
                        Vec2 prev = sg.Curve.Evaluate(0f);
                        for (int i = 1; i <= 16; i++)
                        {
                            Vec2 cur = sg.Curve.Evaluate(i / 16f);
                            g.AddLine(prev, cur, w * 3f, new Color(zc.r, zc.g, zc.b, 0.8f), 0.28f);
                            prev = cur;
                        }
                    }
                }
                else if ((_cat == BuildCategory.Utilities || _cat == BuildCategory.Services) && _service != ServiceType.None && _hasCursor)
                {
                    ServiceInfo info = city.Balance.Service(_service);
                    Color c = _svcResult == CityResult.Ok ? Valid : Invalid;
                    float rw = info.Width * ZoneGrid.CellSize, rd = info.Depth * ZoneGrid.CellSize;
                    g.AddRect(_svcPos, _svcFront, rw, rd, c, 0.25f);
                    g.AddRectOutline(_svcPos, _svcFront, rw, rd, w, Outline, 0.3f);
                    // Marca del frente: una línea corta hacia la calle.
                    g.AddLine(_svcPos, _svcPos + _svcFront * (rd * 0.5f + 4f), w * 1.5f, Outline, 0.3f);
                    if (_svcSnapped) g.AddMarker(_cursor, w * 3f, new Color(1f, 1f, 1f, 0.7f), 0.32f);
                }
            }
            g.EndOverlay();
        }

        /// <summary>Resalta un edificio (contorno) en la capa de herramientas; lo usa el inspector.</summary>
        public void Highlight(Vec2 center, Vec2 front, float width, float depth)
        {
            if (overlay == null) return;
            float dist = cameraController != null ? cameraController.Model.Distance : 100f;
            float w = Mathf.Clamp(dist * 0.004f, 0.3f, 4f);
            overlay.BeginOverlay();
            overlay.AddRectOutline(center, front, width + 2f, depth + 2f, w * 1.5f, new Color(1f, 0.85f, 0.2f, 0.95f), 0.3f);
            overlay.EndOverlay();
        }

        public void ClearHighlight()
        {
            if (overlay == null) return;
            overlay.BeginOverlay();
            overlay.EndOverlay();
            _previewDirty = true;
        }
    }
}
