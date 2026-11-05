using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Urbe.Game.CameraRig;
using Urbe.Game.Sim;

namespace Urbe.Game.UI
{
    /// <summary>
    /// HUD mínimo (M2-08, GDD 11.2 y 11.8): pausa y velocidades, contador de vehículos y flujo %. UI Toolkit (Hud.uxml + Hud.uss);
    /// la escala sale del PanelSettings (844x390 de referencia, 1 px = 1 dp) y respeta el área segura del dispositivo.
    /// Atajos de PC (GDD 11.7): Espacio pausa, 1/2/3 velocidades. Textos en español de momento (localización en otra tarjeta).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class HudController : MonoBehaviour
    {
        private const long PollMs = 250;

        [SerializeField] private SimHost host = null!;
        [SerializeField] private CityCameraController? cameraController;
        [SerializeField] private StyleSheet? styleSheet;

        private UIDocument _doc = null!;
        private VisualElement _root = null!;
        private VisualElement _hudRoot = null!;
        private Label _vehicles = null!;
        private Label _flow = null!;
        private VisualElement _flowChip = null!;
        private Button _pause = null!, _x1 = null!, _x2 = null!, _x3 = null!;
        private Rect _lastSafe;
        private Vector2Int _lastScreen;
        private int _shownVehicles = -1, _shownFlow = -1;

        public string VehicleText => _vehicles.text;
        public string FlowText => _flow.text;
        public VisualElement Root => _root;

        private void OnEnable()
        {
            _doc = GetComponent<UIDocument>();
            if (host == null) host = FindFirstObjectByType<SimHost>();
            if (cameraController == null) cameraController = FindFirstObjectByType<CityCameraController>();

            _root = _doc.rootVisualElement;
            if (styleSheet != null && !_root.styleSheets.Contains(styleSheet)) _root.styleSheets.Add(styleSheet);
            _hudRoot = _root.Q<VisualElement>("hud-root");
            _vehicles = _root.Q<Label>("vehicle-count");
            _flow = _root.Q<Label>("flow-value");
            _flowChip = _root.Q<VisualElement>("flow-chip");
            _pause = _root.Q<Button>("btn-pause");
            _x1 = _root.Q<Button>("btn-x1");
            _x2 = _root.Q<Button>("btn-x2");
            _x3 = _root.Q<Button>("btn-x3");

            _pause.clicked += OnPause;
            _x1.clicked += OnX1;
            _x2.clicked += OnX2;
            _x3.clicked += OnX3;
            host.SpeedChanged += OnSpeedChanged;
            if (cameraController != null) cameraController.IsOverUi = IsOverHud;

            _root.schedule.Execute(Refresh).Every(PollMs);
            OnSpeedChanged(host.Speed);
            Refresh();
            ApplySafeArea(true);
        }

        private void OnDisable()
        {
            if (host != null) host.SpeedChanged -= OnSpeedChanged;
            if (cameraController != null && cameraController.IsOverUi == IsOverHud) cameraController.IsOverUi = null;
            if (_pause != null) { _pause.clicked -= OnPause; _x1.clicked -= OnX1; _x2.clicked -= OnX2; _x3.clicked -= OnX3; }
        }

        private void OnPause() => host.TogglePause();
        private void OnX1() => host.SetSpeed(SimSpeed.X1);
        private void OnX2() => host.SetSpeed(SimSpeed.X2);
        private void OnX3() => host.SetSpeed(SimSpeed.X3);

        private void Update()
        {
            ApplySafeArea(false);
            Keyboard kb = Keyboard.current;
            if (kb == null) return;
            if (kb.spaceKey.wasPressedThisFrame) host.TogglePause();
            if (kb.digit1Key.wasPressedThisFrame) host.SetSpeed(SimSpeed.X1);
            if (kb.digit2Key.wasPressedThisFrame) host.SetSpeed(SimSpeed.X2);
            if (kb.digit3Key.wasPressedThisFrame) host.SetSpeed(SimSpeed.X3);
        }

        // ------------------------------------------------------------------ estado

        private void OnSpeedChanged(SimSpeed speed)
        {
            _pause.EnableInClassList("active", speed == SimSpeed.Paused);
            _x1.EnableInClassList("active", speed == SimSpeed.X1);
            _x2.EnableInClassList("active", speed == SimSpeed.X2);
            _x3.EnableInClassList("active", speed == SimSpeed.X3);
        }

        /// <summary>Actualiza contadores solo si cambian (evita crear cadenas y relayout cada tick).</summary>
        public void Refresh()
        {
            if (host == null || !host.IsInitialized) return;
            SimMetricsView m = host.Metrics;
            if (m.VehicleCount != _shownVehicles)
            {
                _shownVehicles = m.VehicleCount;
                _vehicles.text = _shownVehicles.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            int flow = Mathf.Clamp(Mathf.RoundToInt(m.FlowPercent), 0, 100);
            if (flow != _shownFlow)
            {
                _shownFlow = flow;
                _flow.text = flow.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
                _flowChip.EnableInClassList("flow-warn", flow < 70 && flow >= 40);
                _flowChip.EnableInClassList("flow-bad", flow < 40);
            }
        }

        // ------------------------------------------------------------------ interfaz y área segura

        /// <summary>true si el punto de pantalla (píxeles, origen abajo a la izquierda) está sobre algún elemento del HUD.</summary>
        public bool IsOverHud(Vector2 screenPx)
        {
            if (_root?.panel == null) return false;
            Vector2 panelPos = RuntimePanelUtils.ScreenToPanel(_root.panel, new Vector2(screenPx.x, Screen.height - screenPx.y));
            VisualElement picked = _root.panel.Pick(panelPos);
            return picked != null && picked != _root && picked != _hudRoot;
        }

        /// <summary>Aplica Screen.safeArea como relleno del HUD (muescas, cámara frontal, barra de gestos).</summary>
        private void ApplySafeArea(bool force)
        {
            Rect safe = Screen.safeArea;
            var screen = new Vector2Int(Screen.width, Screen.height);
            if (!force && safe == _lastSafe && screen == _lastScreen) return;
            float panelW = _root.resolvedStyle.width;
            if (float.IsNaN(panelW) || panelW <= 0f || screen.x <= 0) return; // el layout aún no existe: se reintenta en el siguiente fotograma
            _lastSafe = safe;
            _lastScreen = screen;
            float k = panelW / screen.x;
            _hudRoot.style.paddingLeft = safe.xMin * k;
            _hudRoot.style.paddingRight = (screen.x - safe.xMax) * k;
            _hudRoot.style.paddingTop = (screen.y - safe.yMax) * k;
            _hudRoot.style.paddingBottom = safe.yMin * k;
        }
    }
}
