using System;
using Urbe.Sim.Geometry;
using Urbe.Sim.Roads;

namespace Urbe.Sim.City
{
    // Enganche de la economia, los campos de rejilla y los hitos de poblacion con el tick de ciudad (M3-05).
    public sealed partial class CitySimulation
    {
        private readonly int[] _milestoneQueue = new int[16];
        private int _milestoneHead, _milestoneCount;

        public EconomySystem Economy { get; private set; } = null!;
        public FieldSystem Fields { get; private set; } = null!;
        /// <summary>Hito de poblacion alcanzado (0 Asentamiento a 10 Megalopolis), segun la mayor poblacion historica.</summary>
        public int MilestoneIndex { get; private set; }
        public string MilestoneName => Milestones.Get(Balance, MilestoneIndex).Name;

        private void InitEconomy()
        {
            Economy = new EconomySystem(this);
            Fields = new FieldSystem(this);
            CheckMilestones();
        }

        internal void AddMoney(long amount) { _money += amount; }

        /// <summary>Cambia la tasa de impuestos de una zona (1 a 29 %, GDD 4.10).</summary>
        public void SetTaxRate(ZoneType zone, float percent)
        {
            if (zone == ZoneType.None) return;
            _taxRates[(int)zone] = Math.Max(1f, Math.Min(29f, percent));
        }

        public bool IsZoneUnlocked(ZoneType zone, int density) => MilestoneIndex >= Balance.ZoneMilestone(zone, density);

        public bool IsRoadUnlocked(ushort roadType) => MilestoneIndex >= Balance.RoadMilestone(roadType);

        /// <summary>Siguiente hito alcanzado que la interfaz aun no ha mostrado (para el aviso de "Has llegado a Pueblo").</summary>
        public bool TryDequeueMilestone(out int milestone)
        {
            if (_milestoneCount == 0) { milestone = -1; return false; }
            milestone = _milestoneQueue[_milestoneHead];
            _milestoneHead = (_milestoneHead + 1) % _milestoneQueue.Length;
            _milestoneCount--;
            return true;
        }

        private void CheckMilestones()
        {
            int idx = Milestones.IndexFor(Balance, PeakPopulation);
            while (MilestoneIndex < idx)
            {
                MilestoneIndex++;
                if (_milestoneCount < _milestoneQueue.Length)
                {
                    _milestoneQueue[(_milestoneHead + _milestoneCount) % _milestoneQueue.Length] = MilestoneIndex;
                    _milestoneCount++;
                }
            }
        }

        partial void LandValueExtra(Vec2 p, ref float value) { value = Fields.LandValueAt(p); }

        partial void PollutionExtra(Vec2 p, ref float penalty)
        {
            penalty = Math.Min(Balance.AirHappinessMax, Fields.AirPollutionAt(p) * Balance.AirHappinessPerUnit);
        }

        /// <summary>Cuanta contaminacion del aire hay en un punto (unidades arbitrarias, 0 = aire limpio).</summary>
        public float AirPollutionAt(Vec2 p) => Fields.AirPollutionAt(p);
    }

    /// <summary>Cambia la tasa de impuestos de una zona. Deshacer restaura la anterior.</summary>
    public sealed class SetTaxRateCommand : CityCommand
    {
        private readonly ZoneType _zone;
        private readonly float _rate;
        private float _prev;

        public SetTaxRateCommand(ZoneType zone, float percent) { _zone = zone; _rate = percent; }

        public override CityResult Validate(CitySimulation city)
            => Result = _zone == ZoneType.None || !(_rate >= 1f && _rate <= 29f) ? CityResult.InvalidArgument : CityResult.Ok;

        public override bool Apply(CitySimulation city)
        {
            _prev = city.TaxRate(_zone);
            city.SetTaxRate(_zone, _rate);
            return true;
        }

        public override void Undo(CitySimulation city) => city.SetTaxRate(_zone, _prev);
    }
}
