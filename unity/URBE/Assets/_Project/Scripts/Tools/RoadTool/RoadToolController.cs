using System.Globalization;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using UnityEngine.UIElements;
using Urbe.Game.CameraRig;
using Urbe.Game.Rendering.Roads;
using Urbe.Game.Sim;
using Urbe.Game.UI;
using Urbe.Sim.Commands;
using Urbe.Sim.Geometry;
using Urbe.Sim.Roads;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

namespace Urbe.Game.Tools.RoadTool
{
    /// <summary>
    /// Herramienta de carreteras táctil (M2-05, GDD 5.3 y 11.3). Une la entrada de <see cref="CityCameraController"/> (toque, arrastre con
    /// herramienta activa, toque largo) con <see cref="RoadToolModel"/>, pinta el fantasma y el panel del HUD y envía los comandos a
    /// <see cref="SimHost"/>. Con el dedo el cursor va desplazado hacia arriba para que el dedo no tape el fantasma, y el toque largo
    /// abre una lupa. Se puede construir con la simulación en pausa (<see cref="SimHost.FlushCommands"/>). Textos en español por ahora.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class RoadToolController : MonoBehaviour
    {
        [SerializeField] private SimHost host = null!;
        [SerializeField] private CityCameraController? cameraController;
        [SerializeField] private RoadChunkRenderer? roads;
        [SerializeField] private RoadGhostRenderer? ghost;
        [SerializeField] private TextAsset? pricesJson;
        [SerializeField, Tooltip("Desplazamiento del cursor sobre el dedo, en dp (con ratón no se aplica).")] private float fingerOffsetDp = 56f;

        private RoadToolModel? _model;
        private VisualElement _root = null!;
        private VisualElement _panel = null!, _loupeEl = null!;
        private Label _message = null!;
        private readonly System.Collections.Generic.List<Button> _typeButtons = new System.Collections.Generic.List<Button>();
        private readonly System.Collections.Generic.List<ushort> _typeIds = new System.Collections.Generic.List<ushort>();
        private readonly System.Collections.Generic.List<Label> _typePrices = new System.Collections.Generic.List<Label>();
        private RoadPrices? _prices;
        private Button _build = null!, _straight = null!, _curve = null!, _free = null!, _demolish = null!;
        private Button _undo = null!, _cancel = null!, _confirm = null!;
        private bool _active;
        private bool _dragging, _dragFirst;
        private bool _dirty = true;
        private bool _loupe;
        private bool _loupeForced;
        private Vector2 _lastHover;
        private Vector2 _lastPointerScreen;
        private PrecisionLoupe? _loupeCam;
        private int _builtCount;
        private string _note = "";
        private float _noteUntil;

        public bool IsActive => _active;
        public RoadToolModel? Model => _model;
        public bool IsLoupeVisible => _loupe;
        public string MessageText => _message.text;
        public bool MessageIsError => _message.ClassListContains("tool-bad");
        public VisualElement Panel => _panel;
        /// <summary>Cuántas calles ha construido esta herramienta (diagnóstico y pruebas).</summary>
        public int BuiltCount => _builtCount;

        // ------------------------------------------------------------------ ciclo de vida

        private void OnEnable()
        {
            var doc = GetComponent<UIDocument>();
            _root = doc.rootVisualElement;
            if (host == null) host = FindFirstObjectByType<SimHost>();
            if (cameraController == null) cameraController = FindFirstObjectByType<CityCameraController>();
            if (roads == null) roads = FindFirstObjectByType<RoadChunkRenderer>();
            if (ghost == null) ghost = FindFirstObjectByType<RoadGhostRenderer>();

            _panel = _root.Q<VisualElement>("road-panel");
            _loupeEl = _root.Q<VisualElement>("loupe");
            _message = _root.Q<Label>("tool-message");
            _build = _root.Q<Button>("btn-build");
            BuildTypeButtons();
            _straight = _root.Q<Button>("btn-mode-straight");
            _curve = _root.Q<Button>("btn-mode-curve");
            _free = _root.Q<Button>("btn-mode-free");
            _demolish = _root.Q<Button>("btn-mode-demolish");
            _undo = _root.Q<Button>("btn-undo");
            _cancel = _root.Q<Button>("btn-cancel");
            _confirm = _root.Q<Button>("btn-confirm");


            _straight.clicked += OnStraight;
            _curve.clicked += OnCurve;
            _free.clicked += OnFree;
            _demolish.clicked += OnDemolish;
            _undo.clicked += Undo;
            _cancel.clicked += Cancel;
            _confirm.clicked += OnConfirm;

            host.SimulationChanged += OnSimulationChanged;
            if (cameraController != null)
            {
                cameraController.Tapped += OnTapped;
                cameraController.ToolDragged += OnToolDragged;
                cameraController.LongPressed += OnLongPressed;
            }
            if (host.IsInitialized) OnSimulationChanged(host.Simulation!);
            RefreshUi();
        }

        private void OnDisable()
        {
            if (host != null) host.SimulationChanged -= OnSimulationChanged;
            if (cameraController != null)
            {
                cameraController.Tapped -= OnTapped;
                cameraController.ToolDragged -= OnToolDragged;
                cameraController.LongPressed -= OnLongPressed;
                cameraController.ToolActive = false;
            }
            if (_build != null)
            {
                _straight.clicked -= OnStraight;
                _curve.clicked -= OnCurve; _free.clicked -= OnFree; _demolish.clicked -= OnDemolish; _undo.clicked -= Undo;
                _cancel.clicked -= Cancel; _confirm.clicked -= OnConfirm;
            }
            if (ghost != null) ghost.HideAll();
        }

        private void OnSimulationChanged(Urbe.Sim.Traffic.TrafficSimulation sim)
        {
            ushort type = _model != null ? _model.RoadTypeId : RoadTypeIds.Street2;
            RoadToolMode mode = _model != null ? _model.Mode : RoadToolMode.Straight;
            RoadPrices prices = host.City != null ? CityPrices(host.City.Balance) : RoadPrices.Parse(pricesJson != null ? pricesJson.text : null);
            _prices = prices;
            _model = new RoadToolModel(sim.Network, prices);
            if (host.City != null) { Urbe.Sim.City.CitySimulation city = host.City; _model.IsProtected = id => city.IsOutsideConnection(id); }
            RefreshTypePrices();
            _model.SetRoadType(type);
            _model.SetMode(mode);
            _dirty = true;
        }

        /// <summary>Precios por metro de la economía de la ciudad (M3-07), en el orden de <see cref="RoadTypeIds"/>.</summary>
        public static RoadPrices CityPrices(Urbe.Sim.City.CityBalance bal)
        {
            string[] keys = { "street_2", "oneway_2", "avenue_4", "avenue_6", "rural_2", "highway_4" };
            var p = new RoadPrices { note = "Precios de CityBalance", entries = new RoadPrices.Entry[keys.Length] };
            for (int i = 0; i < keys.Length; i++) p.entries[i] = new RoadPrices.Entry { key = keys[i], perMeter = bal.RoadCostPerMeter[i] };
            return p;
        }

        /// <summary>Aviso corto en la línea de mensajes (por ejemplo "bloqueado hasta ...") que no pisa el plan hasta que pasa el tiempo.</summary>
        public void ShowNote(string text, float seconds = 3.5f)
        {
            _note = text;
            _noteUntil = Time.unscaledTime + seconds;
            _dirty = true;
            RefreshUi();
        }

        // ------------------------------------------------------------------ botones

        public void ToggleActive() => SetActive(!_active);

        public void SetActive(bool on)
        {
            _active = on;
            if (cameraController != null) cameraController.ToolActive = on;
            if (!on) { _model?.Cancel(); HideLoupe(); ghost?.HideAll(); }
            _dirty = true;
            RefreshUi();
        }

        /// <summary>Los 6 tipos del catálogo (todos abiertos desde el inicio, el coste es el freno): id, nombre, botón y ayuda.</summary>
        public static readonly (ushort id, string name, string button, string tip)[] TypeInfo =
        {
            (RoadTypeIds.Street2, "Calle", "btn-type-street", "Calle de doble sentido, 2 carriles, 40 km/h. Se puede zonificar."),
            (RoadTypeIds.OneWay2, "Sentido único", "btn-type-oneway", "Calle de un solo sentido, 2 carriles, 40 km/h. Se puede zonificar."),
            (RoadTypeIds.Avenue4, "Avenida 4", "btn-type-avenue", "Avenida de 4 carriles con mediana, 50 km/h. Se puede zonificar."),
            (RoadTypeIds.Avenue6, "Avenida 6", "btn-type-avenue6", "Avenida de 6 carriles con mediana, 60 km/h. Se puede zonificar."),
            (RoadTypeIds.Rural2, "Rural", "btn-type-rural", "Carretera rural de 2 carriles, 80 km/h, con arcén y sin aceras. No se puede zonificar."),
            (RoadTypeIds.Highway4, "Autopista", "btn-type-highway", "Autopista de 4 carriles con mediana, 100 km/h, con arcén y sin aceras. No se puede zonificar."),
        };

        private void BuildTypeButtons()
        {
            VisualElement row = _root.Q<VisualElement>("road-types");
            row.Clear();
            _typeButtons.Clear(); _typeIds.Clear(); _typePrices.Clear();
            RoadTypeCatalog catalog = host.Network != null ? host.Network.Types : RoadTypeCatalog.CreateDefault();
            foreach (var info in TypeInfo)
            {
                var b = new Button { name = info.button, tooltip = info.tip };
                b.AddToClassList("hud-button");
                b.AddToClassList("tool-button");
                b.AddToClassList("road-type");
                ushort id = info.id;
                b.clicked += () => SetType(id);
                b.Add(new RoadIcon(catalog.Get(id)));
                var name = new Label(info.name); name.AddToClassList("road-type-name"); name.pickingMode = PickingMode.Ignore;
                var price = new Label("-"); price.AddToClassList("road-type-price"); price.pickingMode = PickingMode.Ignore;
                b.Add(name); b.Add(price);
                row.Add(b);
                _typeButtons.Add(b); _typeIds.Add(id); _typePrices.Add(price);
            }
            RefreshTypePrices();
        }

        private void RefreshTypePrices()
        {
            if (_prices == null) return;
            string[] keys = { "street_2", "oneway_2", "avenue_4", "avenue_6", "rural_2", "highway_4" };
            for (int i = 0; i < _typePrices.Count && i < keys.Length; i++) _typePrices[i].text = Mathf.RoundToInt(_prices.PerMeter(keys[i])) + "/m";
        }
        private void OnStraight() => SetMode(RoadToolMode.Straight);
        private void OnCurve() => SetMode(RoadToolMode.Curve);
        private void OnFree() => SetMode(RoadToolMode.Free);
        private void OnDemolish() => SetMode(RoadToolMode.Demolish);

        public void SetType(ushort id) { _model?.SetRoadType(id); _dirty = true; RefreshUi(); }
        public void SetMode(RoadToolMode mode) { _model?.SetMode(mode); _dirty = true; RefreshUi(); }

        public void Cancel() { _model?.Cancel(); HideLoupe(); _dirty = true; RefreshUi(); }

        public void Undo()
        {
            if (host.UndoLast()) { _model?.Cancel(); _dirty = true; RefreshUi(); }
        }

        /// <summary>Confirma el plan válido: encola los comandos de Sim.Core y los aplica ya (también con la simulación en pausa).</summary>
        private void OnConfirm() => Confirm();

        public bool Confirm()
        {
            if (_model == null || !_model.CanConfirm) return false;
            bool demolish = _model.Mode == RoadToolMode.Demolish;
            int applied;
            if (host.City != null)
            {
                applied = 0;
                foreach (Urbe.Sim.City.CityCommand c in _model.BuildCityCommands())
                {
                    Urbe.Sim.City.CityResult r = host.Execute(c);
                    if (r == Urbe.Sim.City.CityResult.Ok) applied++;
                    else { ShowNote(CityText.Reject(r, host.City, c.RoadResult)); break; }
                }
            }
            else
            {
                var cmds = _model.BuildCommands();
                foreach (ICommand c in cmds) host.Enqueue(c);
                applied = host.FlushCommands();
            }
            if (applied == 0) return false;
            if (!demolish) _builtCount += applied;
            if (demolish) _model.Cancel(); else _model.ContinueFromEnd();
            HideLoupe();
            _dirty = true;
            RefreshUi();
            return true;
        }

        // ------------------------------------------------------------------ entrada

        public float CursorOffsetPx
        {
            get
            {
                float dpi = Screen.dpi <= 1f ? 160f : Screen.dpi;
                return fingerOffsetDp * Mathf.Clamp(dpi / 160f, 0.75f, 4f);
            }
        }

        private static bool UsingTouch => EnhancedTouchSupport.enabled && Touch.activeTouches.Count > 0;

        private bool TryGround(Vector2 screenPx, bool offset, out Vec2 ground)
        {
            ground = default;
            if (cameraController == null) return false;
            Vector2 p = offset && UsingTouch ? screenPx + new Vector2(0f, CursorOffsetPx) : screenPx;
            if (!cameraController.Model.TryGroundPoint(p, out Vector2 g)) return false;
            ground = new Vec2(g.x, g.y);
            return true;
        }

        /// <summary>Toque (o clic) en pantalla, en píxeles. Público para que las pruebas pasen por el mismo camino que la entrada real.</summary>
        public void OnTapped(Vector2 screen)
        {
            if (!_active || _model == null) return;
            if (TryGround(screen, false, out Vec2 g)) { _model.Tap(g); _dirty = true; }
            HideLoupe();
        }

        public void OnToolDragged(Vector2 from, Vector2 to)
        {
            if (!_active || _model == null) return;
            _lastPointerScreen = to;
            bool first = !_dragging;
            _dragging = true;
            if (first) _dragFirst = true;
            if (TryGround(to, true, out Vec2 g)) { _model.Drag(g, _dragFirst); _dirty = true; }
            _dragFirst = false;
            if (_loupe) UpdateLoupe();
        }

        public void OnLongPressed(Vector2 screen)
        {
            if (!_active) return;
            _lastPointerScreen = screen;
            ShowLoupe();
        }

        private bool PointerDown()
        {
            if (UsingTouch) return true;
            Mouse m = Mouse.current;
            return m != null && m.leftButton.isPressed;
        }

        // ------------------------------------------------------------------ bucle

        private void Update()
        {
            if (_note.Length > 0 && Time.unscaledTime >= _noteUntil) { _note = ""; RefreshUi(); }
            if (!_active || _model == null) return;
            if (host.City != null) _model.Budget = host.City.Money;
            if (_dragging && !PointerDown()) { _dragging = false; if (!_loupe) { /* el ghost queda pendiente hasta confirmar */ } HideLoupe(); }
            if (_loupe && !_loupeForced && !PointerDown()) HideLoupe();

            Keyboard kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.enterKey.wasPressedThisFrame) Confirm();
                if (kb.escapeKey.wasPressedThisFrame) Cancel();
                if (kb.zKey.wasPressedThisFrame && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed)) Undo();
            }

            // Ratón sin pulsar: el final sigue al cursor (previsualización).
            Mouse mouse = Mouse.current;
            if (mouse != null && !UsingTouch && !mouse.leftButton.isPressed && _model.HasStart && cameraController != null)
            {
                Vector2 pos = mouse.position.ReadValue();
                if ((pos - _lastHover).sqrMagnitude > 4f && !(cameraController.IsOverUi?.Invoke(pos) ?? false)
                    && TryGround(pos, false, out Vec2 g))
                {
                    _lastHover = pos;
                    _model.Hover(g);
                    _dirty = true;
                }
            }
        }

        private void LateUpdate()
        {
            if (!_active || _model == null || ghost == null || roads == null) return;
            if (_loupe) UpdateLoupe();
            if (!_dirty) return;
            _dirty = false;
            RefreshVisuals();
            RefreshUi();
        }

        // ------------------------------------------------------------------ visuales

        private void RefreshVisuals()
        {
            RoadToolModel m = _model!;
            RoadGhostRenderer g = ghost!;
            if (m.GhostPieces.Count > 0 && roads!.Profiles.Length > 0)
            {
                Color tint = m.Mode == RoadToolMode.Demolish ? RoadGhostRenderer.DemolishTint
                    : (m.Plan.Valid ? RoadGhostRenderer.ValidTint : RoadGhostRenderer.InvalidTint);
                g.ShowGhost(m.GhostPieces, roads.Profiles, tint);
            }
            else g.HideGhost();

            // Guías y marcadores: el ancho crece con la distancia de la cámara para verse siempre.
            float dist = cameraController != null ? cameraController.Model.Distance : 100f;
            float w = Mathf.Clamp(dist * 0.004f, 0.25f, 4f);
            g.BeginOverlay();
            if (m.HasStart)
            {
                Color guide = new Color(1f, 1f, 1f, 0.8f);
                foreach (Vec2 d in m.GuideDirections)
                {
                    g.AddDashed(m.Start - d * 120f, m.Start + d * 260f, w * 0.7f, w * 4f, guide);
                }
                g.AddMarker(m.Start, w * 4f, new Color(0.25f, 0.62f, 1f, 1f));
                if (m.HasControl) g.AddMarker(m.Control, w * 3f, new Color(1f, 0.85f, 0.2f, 1f));
                if (m.HasEnd) g.AddMarker(m.End, w * 4f, m.Plan.Valid ? new Color(0.25f, 0.85f, 0.5f, 1f) : new Color(0.95f, 0.2f, 0.2f, 1f));
                if (m.Mode == RoadToolMode.Curve && m.HasControl)
                {
                    g.AddLine(m.Start, m.Control, w * 0.5f, new Color(1f, 0.85f, 0.2f, 0.7f));
                    if (m.HasEnd) g.AddLine(m.Control, m.End, w * 0.5f, new Color(1f, 0.85f, 0.2f, 0.7f));
                }
            }
            g.EndOverlay();
        }

        private void RefreshUi()
        {
            if (_panel == null) return;
            _panel.EnableInClassList("open", _active);
            if (_model == null) return;
            for (int i = 0; i < _typeButtons.Count; i++) _typeButtons[i].EnableInClassList("toggled", _model.RoadTypeId == _typeIds[i]);
            _straight.EnableInClassList("toggled", _model.Mode == RoadToolMode.Straight);
            _curve.EnableInClassList("toggled", _model.Mode == RoadToolMode.Curve);
            _free.EnableInClassList("toggled", _model.Mode == RoadToolMode.Free);
            _demolish.EnableInClassList("toggled", _model.Mode == RoadToolMode.Demolish);
            _confirm.SetEnabled(_model.CanConfirm);
            _undo.SetEnabled(host.UndoCount > 0);
            bool noteOn = Time.unscaledTime < _noteUntil && _note.Length > 0;
            string text = noteOn ? _note : (_model.Plan.Message ?? "");
            _message.EnableInClassList("show", _active && !string.IsNullOrEmpty(text));
            _message.EnableInClassList("tool-bad", noteOn || (_model.Plan.HasGeometry && !_model.Plan.Valid));
            if (_message.text != text) _message.text = text;
        }

        // ------------------------------------------------------------------ lupa

        private void ShowLoupe()
        {
            if (_loupeCam == null)
            {
                var go = new GameObject("Lupa");
                go.transform.SetParent(transform, false);
                _loupeCam = go.AddComponent<PrecisionLoupe>();
                _loupeCam.Init(cameraController != null ? cameraController.GetComponent<Camera>() : Camera.main);
                _loupeEl.style.backgroundImage = new StyleBackground(Background.FromRenderTexture(_loupeCam.Texture));
                var cross = new VisualElement();
                cross.AddToClassList("loupe-cross");
                _loupeEl.Add(cross);
            }
            _loupe = true;
            _loupeEl.AddToClassList("show");
            _loupeCam.SetEnabled(true);
            UpdateLoupe();
        }

        private void HideLoupe()
        {
            if (!_loupe) return;
            _loupe = false;
            _loupeForced = false;
            _loupeEl?.RemoveFromClassList("show");
            if (_loupeCam != null) _loupeCam.SetEnabled(false);
        }

        private void UpdateLoupe()
        {
            if (_loupeCam == null || cameraController == null) return;
            Vector2 target = _lastPointerScreen;
            if (cameraController.Model.TryGroundPoint(target + (UsingTouch ? new Vector2(0f, CursorOffsetPx) : Vector2.zero), out Vector2 g))
                _loupeCam.LookAt(g);
            // La lupa va arriba a la izquierda del dedo para no taparse con él (si no cabe, a la derecha).
            if (_root.panel == null) return;
            Vector2 panelPos = RuntimePanelUtils.ScreenToPanel(_root.panel, new Vector2(target.x, Screen.height - target.y));
            Vector2 local = _loupeEl.parent.WorldToLocal(panelPos);
            float size = 120f;
            float x = local.x - size - 40f, y = local.y - size - 70f;
            if (x < 4f) x = local.x + 40f;
            if (y < 4f) y = local.y + 70f;
            _loupeEl.style.left = x;
            _loupeEl.style.top = y;
        }

        /// <summary>Prueba: posiciona la lupa sin pasar por el gesto de toque largo.</summary>
        public void DebugShowLoupeAt(Vector2 screen) { _lastPointerScreen = screen; _loupeForced = true; ShowLoupe(); }
        public string FormatCost(float v) => v.ToString("N0", CultureInfo.InvariantCulture);
    }
}
