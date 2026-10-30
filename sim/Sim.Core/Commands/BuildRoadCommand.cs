using System;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Sim.Commands
{
    /// <summary>Construye una carretera (con snap y cruces). Inverso: demoler lo creado y restaurar lo eliminado por cortes.</summary>
    public sealed class BuildRoadCommand : ICommand
    {
        private readonly NetworkEdit _edit = new NetworkEdit();
        private SegmentRecord[] _created = Array.Empty<SegmentRecord>();
        private SegmentRecord[] _removed = Array.Empty<SegmentRecord>();

        public SegmentRequest Request { get; }

        public BuildRoadCommand(in SegmentRequest request)
        {
            Request = request;
        }

        public ValidationResult Validate(RoadNetwork network)
        {
            return network.CanAddSegment(Request);
        }

        public bool Apply(RoadNetwork network)
        {
            if (!network.AddSegment(Request, _edit)) return false;
            // Por geometria, no por id: los ids cambian si comandos posteriores se deshacen y rehacen.
            var created = new SegmentRecord[_edit.CreatedSegments.Length];
            for (int i = 0; i < created.Length; i++) created[i] = RoadRestore.Capture(network, _edit.CreatedSegments[i]);
            _created = created;
            _removed = _edit.RemovedSegments.ToArray();
            return true;
        }

        public void Undo(RoadNetwork network)
        {
            for (int i = _created.Length - 1; i >= 0; i--)
            {
                EntityId id = RoadRestore.Find(network, _created[i]);
                if (id.IsValid) network.RemoveSegment(id, null);
            }
            RoadRestore.Restore(network, _removed);
            _created = Array.Empty<SegmentRecord>();
            _removed = Array.Empty<SegmentRecord>();
        }
    }
}
