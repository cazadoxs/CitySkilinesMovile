using System;
using Urbe.Sim.City;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Xunit;

namespace Urbe.Sim.Tests.City
{
    public class ServiceTests
    {
        private static CityBalance Open()
        {
            var bal = new CityBalance();
            // Para probar la colocacion sin tener que llegar a poblaciones altas.
            foreach (ServiceInfo s in bal.Services) s.UnlockPopulation = 0;
            return bal;
        }

        /// <summary>Calle principal que sale del final de la autopista, con una transversal; todo conectado al exterior.</summary>
        private static CitySimulation Connected(CityBalance? bal = null, ulong seed = 5)
        {
            CitySimulation c = CityKit.New(seed, bal ?? Open());
            float x = c.OutsidePosition.X + 150f; // fin de la autopista
            CityKit.MustRoad(c, x, 0, x + 160, 0);
            CityKit.MustRoad(c, x + 160, 0, x + 320, 0);
            CityKit.MustRoad(c, x + 160, -120, x + 160, 0);
            CityKit.MustRoad(c, x + 160, 0, x + 160, 120);
            c.Step();
            return c;
        }

        private static float X0(CitySimulation c) => c.OutsidePosition.X + 150f;

        private static EntityId Place(CitySimulation c, ServiceType t, float x, float z)
        {
            var cmd = new PlaceServiceCommand(t, CityKit.V(x, z));
            Assert.Equal(CityResult.Ok, c.Execute(cmd));
            return cmd.Building;
        }

        [Fact]
        public void PlacingSnapsToRoadFacesItAndOccupiesCells()
        {
            CitySimulation c = Connected();
            long m0 = c.Money;
            float x = X0(c) + 60f;
            var cmd = new PlaceServiceCommand(ServiceType.WaterPump, CityKit.V(x, 22));
            Assert.Equal(CityResult.Ok, c.Execute(cmd));
            ref readonly Building b = ref c.Buildings.Get(cmd.Building);
            Assert.Equal(BuildingKind.Service, b.Kind);
            Assert.Equal(m0 - 4000, c.Money);
            // Pegado a la calle (semiancho 5 + 1 m + media huella de 8 m) y mirando a ella (-Z).
            Assert.InRange(b.Pos.Z, 13.5f, 14.5f);
            Assert.True(Math.Cos(b.Yaw) < -0.9);
            ReadOnlySpan<int> cells = c.Buildings.CellsOf(cmd.Building.Index);
            Assert.True(cells.Length >= 4);
            for (int i = 0; i < cells.Length; i++) Assert.Equal(cmd.Building.Index, c.Zones.GetCell(cells[i]).Building);
            // Deshacer devuelve el dinero y libera las celdas.
            Assert.True(c.Undo());
            Assert.Equal(m0, c.Money);
            Assert.False(c.Buildings.IsAlive(cmd.Building));
            for (int i = 0; i < cells.Length; i++) Assert.Equal(-1, c.Zones.GetCell(cells[i]).Building);
        }

        [Fact]
        public void PlacementRejectionsHaveReasons()
        {
            CitySimulation c = Connected(new CityBalance());
            float x = X0(c) + 60f;
            // Landfill necesita 400 habitantes.
            Assert.Equal(CityResult.NotUnlocked, c.Execute(new PlaceServiceCommand(ServiceType.Landfill, CityKit.V(x, 22))));
            Assert.Equal(CityResult.Ok, c.Execute(new PlaceServiceCommand(ServiceType.WaterPump, CityKit.V(x, 22))));
            // Encima de otro servicio.
            Assert.Equal(CityResult.Overlaps, c.Execute(new PlaceServiceCommand(ServiceType.WindTurbine, CityKit.V(x + 4, 24))));
            // Lejos de cualquier calle.
            Assert.Equal(CityResult.NoRoadAccess, c.Execute(new PlaceServiceCommand(ServiceType.WindTurbine, CityKit.V(x, 400))));
            // Fuera del mapa.
            Assert.Equal(CityResult.OutOfBounds, c.Execute(new PlaceServiceCommand(ServiceType.WindTurbine, CityKit.V(5000, 0))));
            // Sin dinero.
            var poor = new CityBalance { StartMoney = 100 };
            foreach (ServiceInfo s in poor.Services) s.UnlockPopulation = 0;
            CitySimulation p = CityKit.New(1, poor);
            float px = p.OutsidePosition.X + 150f;
            p.Execute(new BuildRoadCityCommand(SegmentRequest.Straight(CityKit.V(px, 0), CityKit.V(px + 20, 0), RoadTypeIds.Street2)));
            Assert.Equal(CityResult.InsufficientFunds, p.Execute(new PlaceServiceCommand(ServiceType.WaterPump, CityKit.V(px + 10, 20))));
        }

        [Fact]
        public void CannotPlaceOverBuiltZoneBuilding()
        {
            CitySimulation c = Connected();
            float x = X0(c) + 80f;
            c.SupplyOverride = true;
            EntityId seg = CityKit.SegmentAt(c, x, 0);
            c.Execute(PaintZoneCommand.FillSegment(seg, 2, ZoneType.Residential, 1));
            c.Run(4000);
            Assert.True(c.Buildings.Count > 3);
            // Un edificio de zona ocupa celdas: poner un servicio justo encima se rechaza.
            int found = -1;
            for (int i = 0; i < c.Buildings.IndexBound; i++) if (c.Buildings.IsAlive(i) && c.Buildings.Get(i).Kind == BuildingKind.Zoned) { found = i; break; }
            Vec2 p = c.Buildings.Get(found).Pos;
            Assert.Equal(CityResult.Overlaps, c.Execute(new PlaceServiceCommand(ServiceType.WaterPump, p)));
        }

        private static void Utilities(CitySimulation c, int turbines, int pumps = 2)
        {
            float x = X0(c);
            for (int i = 0; i < pumps; i++) Place(c, ServiceType.WaterPump, x + 20 + 22 * i, 22);
            Place(c, ServiceType.SewageOutlet, x + 20, -22);
            for (int i = 0; i < turbines; i++) Place(c, ServiceType.WindTurbine, x + 60 + 22 * i, -22);
        }

        [Fact]
        public void RealNetworksGateGrowthAndReportProblems()
        {
            CitySimulation c = Connected();
            float x = X0(c);
            c.Execute(PaintZoneCommand.FillSegment(CityKit.SegmentAt(c, x + 80, 0), 3, ZoneType.Residential, 1));
            c.Run(2500);
            Assert.Equal(0, c.Buildings.Count - 0); // sin redes no crece nada (los servicios aun no existen)
            // Solo agua y alcantarillado: sigue sin crecer, falta luz.
            Place(c, ServiceType.WaterPump, x + 20, 22);
            Place(c, ServiceType.SewageOutlet, x + 20, -22);
            c.Run(2500);
            Assert.Equal(2, c.Buildings.Count);
            // Con luz crece.
            Place(c, ServiceType.WindTurbine, x + 60, -22);
            Place(c, ServiceType.WindTurbine, x + 82, -22);
            c.Execute(PaintZoneCommand.FillSegment(CityKit.SegmentAt(c, x + 240, 0), 3, ZoneType.Industrial, 1));
            c.Run(6000);
            c.Supply.Query(CityKit.SegmentAt(c, x + 80, 0), 1, out float qw, out float qp, out bool qs, out bool qc);
            Assert.True(c.Buildings.Count > 8, "edificios " + c.Buildings.Count + " w=" + qw + " p=" + qp + " s=" + qs + " c=" + qc + " pp=" + c.Supply.PowerProduction + " pd=" + c.Supply.PowerDemand + " dem=" + c.Demand.R + " free=" + c.Zones.ZonedFreeCount(ZoneType.Residential) + " comps=" + c.Supply.ComponentCount);
            Assert.True(c.Supply.PowerDemand > 0f);
            Assert.True(c.Supply.WaterProduction >= 1200f);
        }

        [Fact]
        public void DeficitCutsLowPriorityFirstAndProblemsNameTheCause()
        {
            CitySimulation c = Connected();
            float x = X0(c);
            Utilities(c, 0, 1);
            Place(c, ServiceType.WindTurbine, x + 100, -22); // 160 de luz
            c.SupplyOverride = null;
            c.Execute(PaintZoneCommand.FillSegment(CityKit.SegmentAt(c, x + 80, 0), 4, ZoneType.Residential, 1));
            c.Execute(PaintZoneCommand.FillSegment(CityKit.SegmentAt(c, x + 240, 0), 4, ZoneType.Industrial, 1));
            c.Run(20000);
            // Demanda de luz muy por encima de lo producido: la industria (clase 3) se queda sin luz antes que las casas.
            EntityId resSeg = CityKit.SegmentAt(c, x + 80, 0), indSeg = CityKit.SegmentAt(c, x + 240, 0);
            c.Supply.Query(resSeg, 1, out _, out float resPower, out _, out _);
            c.Supply.Query(indSeg, 3, out _, out float indPower, out _, out _);
            Assert.True(c.Supply.PowerDemand > c.Supply.PowerProduction);
            Assert.True(resPower >= indPower);
            Assert.True(indPower < 1f);
            bool noPower = false;
            for (int i = 0; i < c.Buildings.IndexBound; i++)
                if (c.Buildings.IsAlive(i) && (c.Buildings.Get(i).Problems & BuildingProblems.NoPower) != 0 && c.Buildings.Get(i).Kind == BuildingKind.Zoned) noPower = true;
            Assert.True(noPower);
            Assert.Contains("electricidad", BuildingRules.Describe(BuildingProblems.NoPower));
        }

        [Fact]
        public void CuttingTheRoadIsolatesPartOfTheCityAndRebuildingRestoresIt()
        {
            CitySimulation c = Connected();
            float x = X0(c);
            Utilities(c, 3);
            c.Execute(PaintZoneCommand.FillSegment(CityKit.SegmentAt(c, x + 240, 0), 3, ZoneType.Residential, 1));
            c.Run(5000);
            EntityId far = CityKit.SegmentAt(c, x + 240, 0);
            Assert.True(c.Supply.IsConnectedToOutside(far));
            Assert.True(c.CanGrowAt(far));
            int built = c.Buildings.Count;
            Assert.True(built > 5);
            // Se corta la calle entre el centro (con los servicios) y la zona residencial.
            EntityId cut = CityKit.SegmentAt(c, x + 240, 0);
            // La zona residencial esta en el tramo x+160..x+320; el corte demuele el tramo anterior al cruce.
            EntityId link = CityKit.SegmentAt(c, x + 80, 0);
            Assert.Equal(CityResult.Ok, c.Execute(new DemolishRoadCityCommand(link)));
            c.Run(30);
            Assert.False(c.Supply.IsConnectedToOutside(CityKit.SegmentAt(c, x + 240, 0)));
            c.Supply.Query(CityKit.SegmentAt(c, x + 240, 0), 1, out float w, out float p, out _, out bool conn);
            Assert.Equal(0f, w);
            Assert.Equal(0f, p);
            Assert.False(conn);
            c.Run(100);
            bool disconnected = false;
            for (int i = 0; i < c.Buildings.IndexBound; i++)
                if (c.Buildings.IsAlive(i) && (c.Buildings.Get(i).Problems & BuildingProblems.Disconnected) != 0) disconnected = true;
            Assert.True(disconnected);
            Assert.False(c.CanGrowAt(far));
            // Deshacer reconecta y los problemas desaparecen.
            Assert.True(c.Undo());
            c.Run(200);
            Assert.True(c.Supply.IsConnectedToOutside(CityKit.SegmentAt(c, x + 240, 0)));
            for (int i = 0; i < c.Buildings.IndexBound; i++)
                if (c.Buildings.IsAlive(i) && c.Buildings.Get(i).Kind == BuildingKind.Zoned)
                    Assert.Equal(BuildingProblems.None, c.Buildings.Get(i).Problems & BuildingProblems.Disconnected);
        }

        [Fact]
        public void DemolishingAProducerRemovesSupplyAndUndoRestoresIt()
        {
            CitySimulation c = Connected();
            float x = X0(c);
            Utilities(c, 3);
            c.Run(100);
            float power = c.Supply.PowerProduction;
            Assert.Equal(3 * 160f, power);
            var turbine = new System.Collections.Generic.List<EntityId>();
            for (int i = 0; i < c.Buildings.IndexBound; i++)
                if (c.Buildings.IsAlive(i) && c.Buildings.Get(i).ServiceType == (byte)ServiceType.WindTurbine) turbine.Add(c.Buildings.Get(i).Id);
            Assert.Equal(3, turbine.Count);
            Assert.Equal(CityResult.Ok, c.Execute(new DemolishServiceCommand(turbine[0])));
            c.Run(60);
            Assert.Equal(2 * 160f, c.Supply.PowerProduction);
            Assert.True(c.Undo());
            c.Run(60);
            Assert.Equal(3 * 160f, c.Supply.PowerProduction);
            Assert.Equal(CityResult.InvalidTarget, c.Execute(new DemolishServiceCommand(EntityId.None)));
        }

        [Fact]
        public void CoverageIsTheFreeFlowTravelTimeFromTheServiceNotAnEuclideanRadius()
        {
            CitySimulation c = Connected();
            float x = X0(c);
            // Una calle larga en recta: la cobertura de 100 s a 40 km/h (11,1 m/s) llega a unos 1.100 m.
            Place(c, ServiceType.HealthCenter, x + 30, 22);
            c.Run(c.Balance.CoverageInterval * CoverageSystem.TypeCount * 2);
            EntityId near = CityKit.SegmentAt(c, x + 30, 0);
            Assert.True(c.Coverage.IsCovered(CoverageType.Health, near));
            Assert.True(c.Coverage.TimeAt(CoverageType.Health, near) < 20);
            // Otros tipos no se cubren con un centro de salud.
            Assert.False(c.Coverage.IsCovered(CoverageType.Police, near));
            // Un tramo aislado (sin conexion por carretera) esta en el mismo radio euclideo pero sin cobertura.
            CityKit.MustRoad(c, x + 80, 60, x + 130, 60);
            c.Run(c.Balance.CoverageInterval * CoverageSystem.TypeCount * 2);
            EntityId island = CityKit.SegmentAt(c, x + 105, 60);
            Assert.Equal(CoverageSystem.None, c.Coverage.TimeAt(CoverageType.Health, island));
            Assert.False(c.Coverage.IsCovered(CoverageType.Health, island));
            // Uniendolo, entra en la cobertura.
            CityKit.MustRoad(c, x + 80, 0, x + 80, 60);
            c.Run(c.Balance.CoverageInterval * CoverageSystem.TypeCount * 2);
            Assert.True(c.Coverage.TimeAt(CoverageType.Health, CityKit.SegmentAt(c, x + 105, 60)) < 200);
            Assert.True(c.Coverage.IsCovered(CoverageType.Health, CityKit.SegmentAt(c, x + 105, 60)));
        }

        [Fact]
        public void ServicesLiftTheLevelCap()
        {
            var bal = Open();
            bal.DefaultLandValue = 100f;
            bal.LevelUpUpdates = 5;
            bal.VirtualPopulation = true;
            CitySimulation c = Connected(bal);
            float x = X0(c);
            Utilities(c, 4);
            c.Execute(PaintZoneCommand.FillSegment(CityKit.SegmentAt(c, x + 80, 0), 3, ZoneType.Residential, 1));
            c.Run(8000);
            Assert.Equal(1, MaxLevel(c)); // sin recogida de basura ni sanidad no pasa de 1
            Place(c, ServiceType.Landfill, x + 200, 30);
            Place(c, ServiceType.HealthCenter, x + 240, -30);
            c.Run(8000);
            Assert.Equal(2, MaxLevel(c));
        }

        private static int MaxLevel(CitySimulation c)
        {
            int max = 0;
            for (int i = 0; i < c.Buildings.IndexBound; i++)
                if (c.Buildings.IsAlive(i) && c.Buildings.Get(i).Kind == BuildingKind.Zoned) max = Math.Max(max, c.Buildings.Get(i).Level);
            return max;
        }

        [Fact]
        public void DeterministicWithServices()
        {
            ulong Run()
            {
                CitySimulation c = Connected(seed: 9);
                Utilities(c, 2);
                c.Execute(PaintZoneCommand.FillSegment(CityKit.SegmentAt(c, X0(c) + 80, 0), 3, ZoneType.Residential, 1));
                c.Run(5000);
                return c.ComputeHash();
            }
            Assert.Equal(Run(), Run());
        }
    }
}
