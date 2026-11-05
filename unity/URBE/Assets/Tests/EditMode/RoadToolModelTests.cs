using NUnit.Framework;
using UnityEngine;
using Urbe.Game.Tools.RoadTool;
using Urbe.Sim.Commands;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;

namespace Urbe.Game.Tests
{
    /// <summary>M2-05: lógica de la herramienta de carreteras sin Unity: ajustes, guías, validación con motivo, coste y comandos con deshacer.</summary>
    public class RoadToolModelTests
    {
        private RoadNetwork _net = null!;
        private RoadToolModel _tool = null!;

        [SetUp]
        public void SetUp()
        {
            _net = new RoadNetwork(RoadTypeCatalog.CreateDefault(), new NetworkConfig());
            _tool = new RoadToolModel(_net, RoadPrices.Default());
        }

        private static float AngleOf(Vec2 from, Vec2 to) => Mathf.Atan2(to.Z - from.Z, to.X - from.X) * Mathf.Rad2Deg;

        [Test]
        public void AjusteA15Grados_Recta()
        {
            _tool.Tap(new Vec2(100, 100));
            _tool.Tap(new Vec2(100 + 80 * Mathf.Cos(17f * Mathf.Deg2Rad), 100 + 80 * Mathf.Sin(17f * Mathf.Deg2Rad)));
            Assert.That(AngleOf(_tool.Start, _tool.End), Is.EqualTo(15f).Within(0.05f));
            Assert.That((_tool.End - _tool.Start).Length, Is.EqualTo(80f).Within(0.05f), "el ajuste no cambia la longitud");
            Assert.That(_tool.Plan.Valid, Is.True, _tool.Plan.Message);
        }

        [Test]
        public void AjusteA45Y90_YSinAjusteSiEstaLejosDeUnMultiplo()
        {
            _tool.Tap(new Vec2(0, 0));
            _tool.Tap(new Vec2(60, 62)); // ~45,9°
            Assert.That(AngleOf(_tool.Start, _tool.End), Is.EqualTo(45f).Within(0.05f));
            _tool.Cancel();
            _tool.Tap(new Vec2(0, 0));
            Vec2 raw = new Vec2(80 * Mathf.Cos(22.5f * Mathf.Deg2Rad), 80 * Mathf.Sin(22.5f * Mathf.Deg2Rad));
            _tool.Tap(raw);
            Assert.That(AngleOf(_tool.Start, _tool.End), Is.EqualTo(22.5f).Within(0.05f), "a 7,5° de ambos múltiplos queda libre");
        }

        [Test]
        public void Guia_ProlongacionDeLaViaQueAcabaEnElInicio()
        {
            // Calle que acaba en (0,0) viniendo del oeste con un ligero ángulo de 10°: su prolongación va hacia 10°.
            var a = _net.AddNodeRaw(new Vec2(-100f * Mathf.Cos(10f * Mathf.Deg2Rad), -100f * Mathf.Sin(10f * Mathf.Deg2Rad)), 0f);
            var b = _net.AddNodeRaw(new Vec2(0, 0), 0f);
            _net.AddSegmentRaw(a, b, Bezier3.FromLine(_net.GetNode(a).Position, _net.GetNode(b).Position), RoadTypeIds.Street2);
            _tool.Tap(new Vec2(1f, 0.5f)); // cerca del nodo: se ajusta a él
            Assert.That(_tool.Start.Length, Is.LessThan(0.01f));
            _tool.Tap(new Vec2(90f * Mathf.Cos(13f * Mathf.Deg2Rad), 90f * Mathf.Sin(13f * Mathf.Deg2Rad)));
            Assert.That(AngleOf(_tool.Start, _tool.End), Is.EqualTo(10f).Within(0.1f), "se pega a la guía (10°), no al múltiplo de 15°");
            Assert.That(_tool.GuideDirections.Count, Is.GreaterThan(0));
        }

        [Test]
        public void Validacion_DemasiadoCorta_FueraDelMapa_Duplicada_ConMotivoEnTexto()
        {
            _tool.Tap(new Vec2(0, 0));
            _tool.Tap(new Vec2(5, 0));
            Assert.That(_tool.Plan.Valid, Is.False);
            StringAssert.Contains("corta", _tool.Plan.Message);
            Assert.That(_tool.CanConfirm, Is.False);

            _tool.Cancel();
            _tool.Tap(new Vec2(3900, 0));
            _tool.Tap(new Vec2(4100, 0));
            StringAssert.Contains("Fuera del mapa", _tool.Plan.Message);

            // Duplicada: mismo tramo entre dos nodos existentes.
            _tool.Cancel();
            var a = _net.AddNodeRaw(new Vec2(0, 200), 0f);
            var b = _net.AddNodeRaw(new Vec2(80, 200), 0f);
            _net.AddSegmentRaw(a, b, Bezier3.FromLine(new Vec2(0, 200), new Vec2(80, 200)), RoadTypeIds.Street2);
            _tool.Tap(new Vec2(0, 200));
            _tool.Tap(new Vec2(80, 200));
            StringAssert.Contains("Ya existe", _tool.Plan.Message);
        }

        [Test]
        public void Validacion_AnguloAgudoConUnaCalleExistente_SeRechazaSegunElAncho()
        {
            var a = _net.AddNodeRaw(new Vec2(0, 0), 0f);
            var b = _net.AddNodeRaw(new Vec2(100, 0), 0f);
            var c = _net.AddNodeRaw(new Vec2(-100, 0), 0f);
            _net.AddSegmentRaw(a, b, Bezier3.FromLine(new Vec2(0, 0), new Vec2(100, 0)), RoadTypeIds.Street2);
            _net.AddSegmentRaw(a, c, Bezier3.FromLine(new Vec2(0, 0), new Vec2(-100, 0)), RoadTypeIds.Street2);
            _tool.Tap(new Vec2(0, 0));
            _tool.Tap(new Vec2(90f * Mathf.Cos(30f * Mathf.Deg2Rad), 90f * Mathf.Sin(30f * Mathf.Deg2Rad)));
            Assert.That(_tool.Plan.Valid, Is.False);
            StringAssert.Contains("Ángulo demasiado agudo", _tool.Plan.Message);

            // 90°: válido (una T).
            _tool.Cancel();
            _tool.Tap(new Vec2(0, 0));
            _tool.Tap(new Vec2(0, 90));
            Assert.That(_tool.Plan.Valid, Is.True, _tool.Plan.Message);
            // Una avenida a 60° con una calle exigiría más ángulo que una calle a 60°.
            Assert.That(_tool.MinJunctionAngle(RoadTypeIds.Avenue4, RoadTypeIds.Avenue4), Is.GreaterThan(_tool.MinJunctionAngle(RoadTypeIds.Street2, RoadTypeIds.Street2)));
        }

        [Test]
        public void Curva_RadioMinimo_YCurvaValida()
        {
            _tool.SetMode(RoadToolMode.Curve);
            _tool.Tap(new Vec2(0, 0));
            _tool.Tap(new Vec2(60, 0));   // control
            _tool.Tap(new Vec2(60, 60));  // final: giro de 90° con radio holgado
            Assert.That(_tool.Plan.Valid, Is.True, _tool.Plan.Message);
            Assert.That(_tool.GhostPieces.Count, Is.EqualTo(1));
            _tool.Cancel();
            _tool.Tap(new Vec2(0, 0));
            _tool.Tap(new Vec2(30, 0));
            _tool.Tap(new Vec2(30, 4));   // curva casi en U: radio ínfimo
            Assert.That(_tool.Plan.Valid, Is.False);
            StringAssert.Contains("demasiado cerrada", _tool.Plan.Message);
        }

        [Test]
        public void Coste_PorMetroYTipo_YDivisionEnTramosDeHasta200m()
        {
            _tool.Tap(new Vec2(-400, 0));
            _tool.Tap(new Vec2(50, 0));
            Assert.That(_tool.Plan.Valid, Is.True, _tool.Plan.Message);
            Assert.That(_tool.Plan.Segments, Is.EqualTo(3), "450 m en tramos de 190 m como máximo");
            Assert.That(_tool.Plan.Length, Is.EqualTo(450f).Within(1f));
            Assert.That(_tool.Plan.Cost, Is.EqualTo(450f * 8f).Within(10f));
            _tool.SetRoadType(RoadTypeIds.Avenue4);
            Assert.That(_tool.Plan.Cost, Is.EqualTo(450f * 22f).Within(25f));
        }

        [Test]
        public void Confirmar_ConstruyeConComandosDeSimCore_YDeshacerRestaura()
        {
            var history = new CommandHistory();
            _tool.Tap(new Vec2(0, 0));
            _tool.Tap(new Vec2(120, 0));
            Assert.That(_tool.CanConfirm, Is.True, _tool.Plan.Message);
            ulong h0 = _net.ComputeHash();
            foreach (ICommand c in _tool.BuildCommands()) Assert.That(history.Execute(_net, c), Is.EqualTo(ValidationResult.Ok));
            Assert.That(_net.SegmentCount, Is.EqualTo(1));
            _tool.ContinueFromEnd();
            Assert.That(_tool.Start.X, Is.EqualTo(120f).Within(0.01f), "el dibujo sigue desde el final");
            _tool.Tap(new Vec2(120, 90));
            foreach (ICommand c in _tool.BuildCommands()) history.Execute(_net, c);
            Assert.That(_net.SegmentCount, Is.EqualTo(2));
            Assert.That(history.Undo(_net));
            Assert.That(history.Undo(_net));
            Assert.That(_net.SegmentCount, Is.EqualTo(0));
            Assert.That(_net.ComputeHash(), Is.EqualTo(h0));
        }

        [Test]
        public void Demoler_SeleccionaTramo_Valida_YSeDeshace()
        {
            var a = _net.AddNodeRaw(new Vec2(0, 0), 0f);
            var b = _net.AddNodeRaw(new Vec2(100, 0), 0f);
            _net.AddSegmentRaw(a, b, Bezier3.FromLine(new Vec2(0, 0), new Vec2(100, 0)), RoadTypeIds.Street2);
            _tool.SetMode(RoadToolMode.Demolish);
            _tool.Tap(new Vec2(300, 300));
            Assert.That(_tool.CanConfirm, Is.False);
            _tool.Tap(new Vec2(50, 3));
            Assert.That(_tool.CanConfirm, Is.True);
            StringAssert.Contains("Demoler tramo", _tool.Plan.Message);
            var history = new CommandHistory();
            foreach (ICommand c in _tool.BuildCommands()) history.Execute(_net, c);
            Assert.That(_net.SegmentCount, Is.EqualTo(0));
            history.Undo(_net);
            Assert.That(_net.SegmentCount, Is.EqualTo(1));
        }

        [Test]
        public void Libre_SimplificaElTrazoEnSegmentosSuaves()
        {
            _tool.SetMode(RoadToolMode.Free);
            _tool.Tap(new Vec2(0, 0));
            for (int i = 1; i <= 40; i++)
            {
                float x = i * 3f;
                _tool.Drag(new Vec2(x, 30f * Mathf.Sin(x / 40f)), i == 1);
            }
            Assert.That(_tool.Plan.HasGeometry);
            Assert.That(_tool.Plan.Valid, Is.True, _tool.Plan.Message);
            Assert.That(_tool.Requests.Count, Is.InRange(2, 12), "pocos segmentos Bézier, no uno por punto");
            for (int i = 1; i < _tool.Requests.Count; i++)
                Assert.That((_tool.Requests[i].Start - _tool.Requests[i - 1].End).Length, Is.LessThan(0.01f), "tramos encadenados");
        }

        [Test]
        public void Fantasma_UsaLaMismaGeometriaQueElConstructor()
        {
            _tool.Tap(new Vec2(0, 0));
            _tool.Tap(new Vec2(100, 0));
            Assert.That(_tool.GhostPieces.Count, Is.EqualTo(1));
            var piece = _tool.GhostPieces[0];
            Assert.That(piece.RoadTypeId, Is.EqualTo(RoadTypeIds.Street2));
            Assert.That(piece.Curve.P0, Is.EqualTo(_tool.Requests[0].Start));
            Assert.That(piece.Curve.P3, Is.EqualTo(_tool.Requests[0].End));
        }
    }
}
