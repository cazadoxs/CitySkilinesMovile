using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;
using Urbe.Game.Sim;
using Urbe.Sim.City;

namespace Urbe.Game.UI
{
    /// <summary>
    /// Datos de la ciudad en el HUD (M3-07, GDD 11.2): dinero con balance semanal, fecha, población, hito y barras RCI; y la parte de
    /// avisos de M3-08: un único aviso a la vez (sin apilar) para agua, luz y alcantarillado, un cartel de hito alcanzado y un consejo
    /// inicial con lo mínimo para empezar (calle, zonas, agua, desagüe y luz). Se actualiza por sondeo (4 veces por segundo), no por fotograma.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class CityHudController : MonoBehaviour
    {
        private const long PollMs = 250;
        private const float WarnCooldown = 45f;

        public enum ToastKind { Info, Warn, Bad }

        [SerializeField] private SimHost host = null!;

        private VisualElement _root = null!;
        private Label _money = null!, _delta = null!, _date = null!, _dateSub = null!, _pop = null!, _milestone = null!, _milestoneSub = null!;
        private VisualElement _moneyChip = null!, _rciR = null!, _rciC = null!, _rciI = null!, _rciO = null!;
        private VisualElement _tip = null!;
        private Label _tipBody = null!, _toast = null!, _banner = null!;
        private Button _tipClose = null!;
        private VisualElement[] _cityChips = null!;
        private bool _tipDismissed;
        private float _toastUntil, _bannerUntil, _tipDoneAt = -1f;
        private int _toastPriority;
        private float _nextEstimate;
        private long _income, _expenses;
        private readonly Dictionary<string, float> _cooldown = new Dictionary<string, float>();

        // estado de los pasos del consejo (público para pruebas)
        public bool StepRoad { get; private set; }
        public bool StepZones { get; private set; }
        public bool StepWater { get; private set; }
        public bool StepSewage { get; private set; }
        public bool StepPower { get; private set; }

        public string MoneyText => _money.text;
        public string DateText => _date.text;
        public string PopText => _pop.text;
        public string MilestoneText => _milestone.text;
        public string ToastText => _toast.ClassListContains("show") ? _toast.text : "";
        public string BannerText => _banner.ClassListContains("show") ? _banner.text : "";
        public bool TipVisible => _tip.ClassListContains("show");
        public string TipText => _tipBody.text;
        public float RciHeightPercent(char zone) => FillOf(zone).resolvedStyle.height;

        private VisualElement FillOf(char z) => z == 'R' ? _rciR : (z == 'C' ? _rciC : (z == 'I' ? _rciI : _rciO));

        private void OnEnable()
        {
            if (host == null) host = FindFirstObjectByType<SimHost>();
            _root = GetComponent<UIDocument>().rootVisualElement;
            _money = _root.Q<Label>("money-value");
            _delta = _root.Q<Label>("money-delta");
            _date = _root.Q<Label>("date-value");
            _dateSub = _root.Q<Label>("date-sub");
            _pop = _root.Q<Label>("pop-value");
            _milestone = _root.Q<Label>("milestone-value");
            _milestoneSub = _root.Q<Label>("milestone-sub");
            _moneyChip = _root.Q<VisualElement>("money-chip");
            _rciR = _root.Q<VisualElement>("rci-r");
            _rciC = _root.Q<VisualElement>("rci-c");
            _rciI = _root.Q<VisualElement>("rci-i");
            _rciO = _root.Q<VisualElement>("rci-o");
            _tip = _root.Q<VisualElement>("tip-panel");
            _tipBody = _root.Q<Label>("tip-body");
            _tipClose = _root.Q<Button>("btn-tip-close");
            _toast = _root.Q<Label>("toast");
            _banner = _root.Q<Label>("banner");
            _cityChips = new[] { _moneyChip, _root.Q<VisualElement>("date-chip"), _root.Q<VisualElement>("pop-chip"), _root.Q<VisualElement>("milestone-chip"), _root.Q<VisualElement>("rci") };
            _tipClose.clicked += OnTipClose;
            host.SimulationChanged += OnSimulationChanged;
            _root.schedule.Execute(Refresh).Every(PollMs);
            Refresh();
        }

        private void OnDisable()
        {
            if (_tipClose != null) _tipClose.clicked -= OnTipClose;
            if (host != null) host.SimulationChanged -= OnSimulationChanged;
        }

        private void OnSimulationChanged(Urbe.Sim.Traffic.TrafficSimulation _) { _nextEstimate = 0f; Refresh(); }

        private void OnTipClose()
        {
            _tipDismissed = true;
            _tip.RemoveFromClassList("show");
        }

        /// <summary>Muestra un aviso (uno solo a la vez). Uno de menor prioridad no pisa a otro todavía visible; el mismo texto no se repite.</summary>
        public bool ShowToast(string text, ToastKind kind = ToastKind.Info, float seconds = 5f, string? key = null)
        {
            float now = Time.unscaledTime;
            bool visible = _toast.ClassListContains("show") && now < _toastUntil;
            int prio = (int)kind;
            if (visible && (prio < _toastPriority || _toast.text == text)) return false;
            if (key != null)
            {
                if (_cooldown.TryGetValue(key, out float until) && now < until) return false;
                _cooldown[key] = now + WarnCooldown;
            }
            _toast.text = text;
            _toast.EnableInClassList("warn", kind == ToastKind.Warn);
            _toast.EnableInClassList("bad", kind == ToastKind.Bad);
            _toast.AddToClassList("show");
            _toastUntil = now + seconds;
            _toastPriority = prio;
            return true;
        }

        public void ShowBanner(string text, float seconds = 7f)
        {
            _banner.text = text;
            _banner.AddToClassList("show");
            _bannerUntil = Time.unscaledTime + seconds;
        }

        // ------------------------------------------------------------------ sondeo

        public void Refresh()
        {
            if (host == null || !host.IsInitialized) return;
            CitySimulation? city = host.City;
            bool isCity = city != null;
            for (int i = 0; i < _cityChips.Length; i++) _cityChips[i].style.display = isCity ? DisplayStyle.Flex : DisplayStyle.None;
            if (city == null) { _tip.RemoveFromClassList("show"); return; }
            float now = Time.unscaledTime;
            if (_toast.ClassListContains("show") && now >= _toastUntil) _toast.RemoveFromClassList("show");
            if (_banner.ClassListContains("show") && now >= _bannerUntil) _banner.RemoveFromClassList("show");

            long money = city.Money;
            _money.text = CityText.Money(money);
            _moneyChip.EnableInClassList("chip-bad", money < 0);
            _moneyChip.EnableInClassList("chip-warn", money >= 0 && money < 3000);
            if (now >= _nextEstimate)
            {
                _nextEstimate = now + 1f;
                city.Economy.Estimate(out _income, out _expenses);
                long net = _income - _expenses;
                _delta.text = CityText.SignedMoney(net) + "/sem";
                _delta.EnableInClassList("chip-delta-up", net >= 0);
                _delta.EnableInClassList("chip-delta-down", net < 0);
                ScanBuildings(city);
            }
            CityClock clock = city.Clock;
            _date.text = CityText.Clock(clock);
            _dateSub.text = CityText.DateLine(clock);
            _pop.text = CityText.Population(city.Stats.Population);
            int ms = city.MilestoneIndex;
            _milestone.text = CityText.MilestoneName(ms);
            _milestoneSub.text = ms + 1 < Milestones.Count ? "Siguiente: " + CityText.Money(city.Balance.MilestonePopulation[ms + 1]) + " hab." : "";
            SetRci(_rciR, city.Demand.R);
            SetRci(_rciC, city.Demand.C);
            SetRci(_rciI, city.Demand.I);
            SetRci(_rciO, city.Demand.O);
            while (city.TryDequeueMilestone(out int reached))
            {
                MilestoneInfo info = Milestones.Get(city.Balance, reached);
                string unlocks = UnlockList(info.Unlocks);
                ShowBanner("¡Has llegado a " + CityText.MilestoneName(reached) + "!" + (unlocks.Length > 0 ? "\nDesbloqueas: " + unlocks : ""));
            }
            UpdateTip(city);
        }

        private static void SetRci(VisualElement fill, float demand)
        {
            float d = Mathf.Clamp(demand, -100f, 100f) / 100f * 50f;
            if (d >= 0f)
            {
                fill.style.top = StyleKeyword.Auto;
                fill.style.bottom = Length.Percent(50f);
                fill.style.height = Length.Percent(d);
                fill.style.opacity = 1f;
            }
            else
            {
                fill.style.bottom = StyleKeyword.Auto;
                fill.style.top = Length.Percent(50f);
                fill.style.height = Length.Percent(-d);
                fill.style.opacity = 0.45f;
            }
        }

        // ------------------------------------------------------------------ avisos y consejo

        private void ScanBuildings(CitySimulation city)
        {
            BuildingSystem bs = city.Buildings;
            int noWater = 0, noPower = 0, noSewage = 0, disconnected = 0;
            bool pump = false, outlet = false, power = false;
            int bound = bs.IndexBound;
            for (int i = 0; i < bound; i++)
            {
                if (!bs.IsAlive(i)) continue;
                ref readonly Building b = ref bs.Get(i);
                if (b.Kind == BuildingKind.Service)
                {
                    var t = (ServiceType)b.ServiceType;
                    if (t == ServiceType.WaterPump) pump = true;
                    else if (t == ServiceType.SewageOutlet) outlet = true;
                    else if (t == ServiceType.WindTurbine || t == ServiceType.CoalPlant) power = true;
                    continue;
                }
                BuildingProblems p = b.Problems;
                if ((p & BuildingProblems.NoWater) != 0) noWater++;
                if ((p & BuildingProblems.NoPower) != 0) noPower++;
                if ((p & BuildingProblems.NoSewage) != 0) noSewage++;
                if ((p & BuildingProblems.Disconnected) != 0) disconnected++;
            }
            StepRoad = city.Network.SegmentCount > 1;
            StepZones = city.Zones.ZonedCount(ZoneType.Residential) + city.Zones.ZonedCount(ZoneType.Commercial)
                + city.Zones.ZonedCount(ZoneType.Industrial) + city.Zones.ZonedCount(ZoneType.Office) > 0;
            StepWater = pump;
            StepSewage = outlet;
            StepPower = power;

            if (disconnected > 0)
                ShowToast(disconnected + " edificios sin conexión: su calle no llega a la autopista de entrada.", ToastKind.Bad, 6f, "disc");
            else if (noWater > 0)
                ShowToast(noWater + (noWater == 1 ? " edificio sin agua" : " edificios sin agua") + ": coloca una bomba de agua junto a una calle conectada (Construir > Agua y luz).", ToastKind.Warn, 6f, "water");
            else if (noPower > 0)
                ShowToast(noPower + (noPower == 1 ? " edificio sin luz" : " edificios sin luz") + ": coloca una turbina eólica o una central (Construir > Agua y luz).", ToastKind.Warn, 6f, "power");
            else if (noSewage > 0)
                ShowToast(noSewage + (noSewage == 1 ? " edificio sin desagüe" : " edificios sin desagüe") + ": coloca un desagüe (Construir > Agua y luz).", ToastKind.Warn, 6f, "sewage");
        }

        private void UpdateTip(CitySimulation city)
        {
            bool done = StepRoad && StepZones && StepWater && StepSewage && StepPower;
            float now = Time.unscaledTime;
            if (done && _tipDoneAt < 0f) _tipDoneAt = now;
            bool show = !_tipDismissed && (!done || now < _tipDoneAt + 10f);
            _tip.EnableInClassList("show", show);
            if (!show) return;
            var sb = new StringBuilder();
            Line(sb, StepRoad, "Una calle desde la autopista");
            Line(sb, StepZones, "Zonas junto a la calle");
            Line(sb, StepWater, "Bomba de agua");
            Line(sb, StepSewage, "Desagüe");
            Line(sb, StepPower, "Turbina eólica");
            sb.Append('\n');
            if (done) sb.Append("¡Listo! Los edificios crecerán solos. Sube la velocidad.");
            else if (!StepRoad) sb.Append("Ahora: Construir > Carreteras y traza una calle que salga del final de la autopista (a la izquierda).");
            else if (!StepZones) sb.Append("Ahora: Construir > Zonas, elige Residencial y pinta junto a la calle.");
            else if (!StepWater) sb.Append("Ahora: Construir > Agua y luz > Bomba de agua, junto a una calle.");
            else if (!StepSewage) sb.Append("Ahora: Construir > Agua y luz > Desagüe.");
            else sb.Append("Ahora: Construir > Agua y luz > Turbina eólica.");
            string text = sb.ToString();
            if (_tipBody.text != text) _tipBody.text = text;
        }

        private static void Line(StringBuilder sb, bool ok, string text)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(ok ? "[x] " : "[  ] ").Append(text);
        }

        private static string UnlockList(string[] keys)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < keys.Length; i++)
            {
                string n = UnlockName(keys[i]);
                if (n.Length == 0) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(n);
            }
            return sb.ToString();
        }

        private static string UnlockName(string key)
        {
            switch (key)
            {
                case "landfill": return "vertedero";
                case "health_center": return "centro de salud";
                case "park": return "parque";
                case "fire_station": return "bomberos";
                case "police_station": return "policía";
                case "school": return "colegio";
                case "zones_medium_density": return "densidad media";
                case "zones_office": return "oficinas";
                case "high_school": return "instituto";
                case "avenue_4": return "avenidas";
                case "zones_high_density": return "densidad alta";
                case "avenue_6": return "avenidas de 6 carriles";
                default: return "";
            }
        }
    }
}
