using System;
using Urbe.Sim.Ids;

namespace Urbe.Sim.City
{
    // Enganche de las redes de suministro, los servicios y su cobertura con el tick de ciudad (M3-03).
    public sealed partial class CitySimulation
    {
        public SupplyNetworks Supply { get; private set; } = null!;
        public CoverageSystem Coverage { get; private set; } = null!;

        private void InitServices()
        {
            Supply = new SupplyNetworks(this);
            Coverage = new CoverageSystem(this);
        }

        /// <summary>Avisa de que se coloco o derribo un servicio: las redes y las coberturas se recalculan pronto.</summary>
        internal void NotifyServicesChanged()
        {
            Supply.Dirty = true;
            Coverage.MarkAllDirty();
        }

        partial void OnBuildingAddedExtra(int index) { Supply.Dirty = true; }
        partial void OnBuildingRemovedExtra(int index) { Supply.Dirty = true; }
        partial void OnBuildingAbandonedExtra(int index) { Supply.Dirty = true; }

        partial void SupplyAtExtra(EntityId segment, ZoneType zone, ref float water, ref float power, ref bool sewage, ref bool connected)
        {
            if (SupplyOverride.HasValue) return;
            Supply.Query(segment, SupplyNetworks.ClassOf(zone, false), out water, out power, out sewage, out connected);
        }

        partial void CoverageMaskExtra(EntityId segment, ref int mask)
        {
            if (SupplyOverride.HasValue) return;
            mask |= Coverage.MaskAt(segment);
        }

        partial void RecomputeStatsExtra()
        {
            if (Stats.Population > PeakPopulation) PeakPopulation = Stats.Population;
        }

        /// <summary>Mantenimiento de un servicio: lo reengancha si su calle desaparecio y refleja sus problemas de suministro.</summary>
        partial void UpdateServiceExtra(int index)
        {
            ref Building b = ref Buildings.GetMutable(index);
            if (!Network.IsSegmentAlive(b.Segment))
            {
                if (Network.NearestSegment(b.Pos, Balance.ServiceReach, out EntityId seg, out _, out _)) b.Segment = seg;
            }
            ServiceInfo info = Balance.Service((ServiceType)b.ServiceType);
            BuildingProblems p = BuildingProblems.None;
            if (!Network.IsSegmentAlive(b.Segment)) p |= BuildingProblems.Disconnected;
            else if (!info.IsProducer)
            {
                SupplyAt(b.Segment, ZoneType.None, out _, out _, out _, out bool connected);
                if (!connected) p |= BuildingProblems.Disconnected;
                if (!SupplyOverride.HasValue)
                {
                    Supply.Query(b.Segment, 0, out float water, out float power, out _, out _);
                    if (water < 0.5f) p |= BuildingProblems.NoWater;
                    if (power < 0.5f) p |= BuildingProblems.NoPower;
                }
            }
            b.Problems = p;
        }

        /// <summary>Tras cambiar la red: los servicios se reenganchan a su calle mas cercana o se derriban si una calle nueva los pisa.</summary>
        private void ValidateServices()
        {
            int bound = Buildings.IndexBound;
            for (int i = 0; i < bound; i++)
            {
                if (!Buildings.IsAlive(i)) continue;
                ref Building b = ref Buildings.GetMutable(i);
                if (b.Kind != BuildingKind.Service) continue;
                if (!Network.IsSegmentAlive(b.Segment) && Network.NearestSegment(b.Pos, Balance.ServiceReach, out EntityId seg, out _, out _)) b.Segment = seg;
                var u = new Geometry.Vec2((float)Math.Sin(b.Yaw), (float)Math.Cos(b.Yaw));
                if (FootprintOnRoad(b.Pos, u, u.Perp, b.Depth * ZoneGrid.CellHalf, b.Width * ZoneGrid.CellHalf)) Buildings.Demolish(i);
            }
            NotifyServicesChanged();
        }
    }
}
