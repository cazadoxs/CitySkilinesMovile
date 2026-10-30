using System;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Sim.Commands
{
    /// <summary>Demuele uno o varios segmentos (los nodos huerfanos desaparecen). Inverso: AddSegmentRaw de los registros.</summary>
    public sealed class DemolishCommand : ICommand
    {
        private readonly NetworkEdit _edit = new NetworkEdit();
        private readonly EntityId[] _targets;
        private SegmentRecord[] _removed = Array.Empty<SegmentRecord>();
        private bool _hasRecords;

        public DemolishCommand(EntityId segment)
        {
            _targets = new[] { segment };
        }

        public DemolishCommand(EntityId[] segments)
        {
            _targets = (EntityId[])segments.Clone();
        }

        /// <summary>Ids de los segmentos al crear el comando. Tras un deshacer pueden cambiar; se re-resuelven por geometria.</summary>
        public ReadOnlySpan<EntityId> Targets => _targets;

        public ValidationResult Validate(RoadNetwork network)
        {
            if (_targets.Length == 0) return ValidationResult.InvalidTarget;
            for (int i = 0; i < _targets.Length; i++)
            {
                EntityId id = Resolve(network, i);
                if (!id.IsValid) return ValidationResult.InvalidTarget;
                for (int j = 0; j < i; j++)
                {
                    if (Resolve(network, j) == id) return ValidationResult.InvalidTarget;
                }
            }
            return ValidationResult.Ok;
        }

        public bool Apply(RoadNetwork network)
        {
            if (Validate(network) != ValidationResult.Ok) return false;
            var ids = new EntityId[_targets.Length];
            for (int i = 0; i < ids.Length; i++) ids[i] = Resolve(network, i);
            var records = new SegmentRecord[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                network.RemoveSegment(ids[i], _edit);
                records[i] = _edit.RemovedSegments[0];
            }
            _removed = records;
            _hasRecords = true;
            return true;
        }

        public void Undo(RoadNetwork network)
        {
            RoadRestore.Restore(network, _removed);
        }

        // Tras el primer Apply los ids pueden haber cambiado (deshacer de comandos anteriores): se busca por geometria.
        private EntityId Resolve(RoadNetwork network, int i)
        {
            if (!_hasRecords) return network.IsSegmentAlive(_targets[i]) ? _targets[i] : EntityId.None;
            return RoadRestore.Find(network, _removed[i]);
        }
    }
}
