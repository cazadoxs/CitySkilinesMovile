using System.Text;
using UnityEngine;
using UnityEngine.UIElements;
using Urbe.Game.CameraRig;
using Urbe.Game.Sim;
using Urbe.Game.UI;
using Urbe.Sim.City;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;

namespace Urbe.Game.Tools.CityTools
{
    /// <summary>
    /// Panel de edificio (M3-08, GDD 12.7): al tocar un edificio, si ninguna herramienta consume el toque, muestra tipo, nivel, habitantes o empleos,
    /// problemas con el motivo en texto (<see cref="BuildingRules.Describe"/>) y, en los servicios, mantenimiento y botón Derribar. El edificio queda resaltado.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class BuildingInspector : MonoBehaviour
    {
        [SerializeField] private SimHost host = null!;
        [SerializeField] private CityCameraController? cameraController;
        [SerializeField] private BuildMenuController? menu;
        [SerializeField] private CityHudController? hud;

        private VisualElement _panel = null!;
        private Label _title = null!, _body = null!;
        private Button _close = null!, _demolish = null!;
        private EntityId _selected = EntityId.None;
        private float _nextRefresh;

        public bool IsOpen => _panel.ClassListContains("show");
        public EntityId Selected => _selected;
        public string TitleText => _title.text;
        public string BodyText => _body.text;
        public bool DemolishVisible => _demolish.resolvedStyle.display != DisplayStyle.None && IsOpen;

        private void OnEnable()
        {
            if (host == null) host = FindFirstObjectByType<SimHost>();
            if (cameraController == null) cameraController = FindFirstObjectByType<CityCameraController>();
            if (menu == null) menu = FindFirstObjectByType<BuildMenuController>();
            if (hud == null) hud = FindFirstObjectByType<CityHudController>();
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            _panel = root.Q<VisualElement>("building-panel");
            _title = root.Q<Label>("bp-title");
            _body = root.Q<Label>("bp-body");
            _close = root.Q<Button>("btn-bp-close");
            _demolish = root.Q<Button>("btn-bp-demolish");
            _close.clicked += Close;
            _demolish.clicked += DemolishSelected;
            if (cameraController != null) cameraController.Tapped += OnTapped;
            if (menu != null) menu.Changed += OnMenuChanged;
            host.SimulationChanged += OnSimulationChanged;
        }

        private void OnDisable()
        {
            if (_close != null) _close.clicked -= Close;
            if (_demolish != null) _demolish.clicked -= DemolishSelected;
            if (cameraController != null) cameraController.Tapped -= OnTapped;
            if (menu != null) menu.Changed -= OnMenuChanged;
            if (host != null) host.SimulationChanged -= OnSimulationChanged;
        }

        private void OnSimulationChanged(Urbe.Sim.Traffic.TrafficSimulation _) => Close();

        private void OnMenuChanged()
        {
            if (menu != null && menu.WantsTaps && IsOpen) Close();
        }

        public void Close()
        {
            _selected = EntityId.None;
            _panel.RemoveFromClassList("show");
            menu?.ClearHighlight();
        }

        /// <summary>Toque en pantalla (píxeles). Público para que las pruebas usen el mismo camino que la entrada real.</summary>
        public void OnTapped(Vector2 screen)
        {
            CitySimulation? city = host.City;
            if (city == null || cameraController == null) return;
            if (menu != null && menu.WantsTaps) return;
            if (!cameraController.Model.TryGroundPoint(screen, out Vector2 g)) return;
            int idx = Pick(city.Buildings, new Vec2(g.x, g.y));
            if (idx < 0) { Close(); return; }
            Select(city, idx);
        }

        /// <summary>Índice del edificio cuya huella (con 5 m de margen, la altura del edificio desplaza el toque) está más cerca del punto, o -1.</summary>
        public static int Pick(BuildingSystem bs, Vec2 p)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < bs.IndexBound; i++)
            {
                if (!bs.IsAlive(i)) continue;
                ref readonly Building b = ref bs.Get(i);
                float dx = p.X - b.Pos.X, dz = p.Z - b.Pos.Z;
                float sin = Mathf.Sin(b.Yaw), cos = Mathf.Cos(b.Yaw);
                float lz = dx * sin + dz * cos;       // a lo largo del frente
                float lx = dx * cos - dz * sin;       // a lo ancho
                float hx = b.Width * ZoneGrid.CellHalf + 5f, hz = b.Depth * ZoneGrid.CellHalf + 5f;
                if (Mathf.Abs(lx) > hx || Mathf.Abs(lz) > hz) continue;
                float d = dx * dx + dz * dz;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        public void Select(CitySimulation city, int index)
        {
            BuildingSystem bs = city.Buildings;
            if (!bs.IsAlive(index)) return;
            _selected = bs.IdAt(index);
            Fill(city);
            _panel.AddToClassList("show");
            Highlight(bs.Get(index));
        }

        private void Highlight(in Building b)
        {
            Vec2 front = new Vec2(Mathf.Sin(b.Yaw), Mathf.Cos(b.Yaw));
            menu?.Highlight(b.Pos, front, b.Width * ZoneGrid.CellSize, b.Depth * ZoneGrid.CellSize);
        }

        private void Update()
        {
            if (!IsOpen || host.City == null || Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.5f;
            if (!host.City.Buildings.IsAlive(_selected)) { Close(); return; }
            Fill(host.City);
        }

        private void Fill(CitySimulation city)
        {
            ref readonly Building b = ref city.Buildings.Get(_selected);
            var sb = new StringBuilder();
            if (b.Kind == BuildingKind.Service)
            {
                var t = (ServiceType)b.ServiceType;
                _title.text = CityText.ServiceName(t);
                sb.Append("Servicio municipal: ").Append(CityText.ServiceEffect(t)).Append('.');
                sb.Append("\nMantenimiento: ").Append(CityText.Money((long)city.Economy.ServiceMaintenance(in b))).Append(" por semana");
                _demolish.style.display = DisplayStyle.Flex;
            }
            else
            {
                string zone = CityText.ZoneName(b.Zone);
                _title.text = zone + " (densidad " + CityText.DensityName(b.Density) + ")";
                sb.Append("Nivel ").Append(b.Level).Append(" de 5");
                if (b.Zone == ZoneType.Residential)
                    sb.Append("\nHabitantes: ").Append(b.Occupants).Append(" de ").Append(b.Capacity);
                else
                    sb.Append("\nEmpleos: ").Append(b.FilledLow + b.FilledHigh).Append(" de ").Append(b.JobsLow + b.JobsHigh);
                if (b.Happiness > 0) sb.Append("\nFelicidad: ").Append(b.Happiness).Append(" %");
                _demolish.style.display = DisplayStyle.None;
            }
            string problems = CityText.Problems(b.Problems);
            sb.Append('\n');
            sb.Append(problems.Length > 0 ? problems : "Sin problemas.");
            string text = sb.ToString();
            if (_body.text != text) _body.text = text;
        }

        private void DemolishSelected()
        {
            CitySimulation? city = host.City;
            if (city == null || !city.Buildings.IsAlive(_selected)) return;
            CityResult r = host.Execute(new DemolishServiceCommand(_selected));
            if (r == CityResult.Ok) { hud?.ShowToast("Servicio derribado.", CityHudController.ToastKind.Info, 3f); Close(); }
            else hud?.ShowToast(CityText.Reject(r, city), CityHudController.ToastKind.Bad, 3f);
        }
    }
}
