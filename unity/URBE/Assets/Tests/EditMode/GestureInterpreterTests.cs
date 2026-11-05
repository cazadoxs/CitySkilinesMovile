using NUnit.Framework;
using UnityEngine;
using Urbe.Game.CameraRig;

namespace Urbe.Game.Tests
{
    public class GestureInterpreterTests
    {
        private const float Dt = 1f / 60f;

        private static GestureOutput None(GestureInterpreter g) => g.Update(Dt, 0, default, default);
        private static GestureOutput One(GestureInterpreter g, Vector2 p) => g.Update(Dt, 1, p, default);
        private static GestureOutput Two(GestureInterpreter g, Vector2 a, Vector2 b) => g.Update(Dt, 2, a, b);

        [Test]
        public void ArrastrarConUnDedo_DesplazaLaCamara_TrasLaHolgura()
        {
            var g = new GestureInterpreter();
            One(g, new Vector2(500f, 500f));
            GestureOutput o = One(g, new Vector2(505f, 500f));
            Assert.That(o.HasPan, Is.False, "Dentro de la holgura no se mueve");
            o = One(g, new Vector2(540f, 500f));
            Assert.That(o.HasPan);
            o = One(g, new Vector2(560f, 500f));
            Assert.That(o.HasPan);
            Assert.That(o.PanTo.x - o.PanFrom.x, Is.EqualTo(20f).Within(1e-3f));
        }

        [Test]
        public void ConHerramienta_UnDedoEsDeLaHerramienta_NoDeLaCamara()
        {
            var g = new GestureInterpreter { ToolActive = true };
            One(g, new Vector2(500f, 500f));
            One(g, new Vector2(540f, 500f));
            GestureOutput o = One(g, new Vector2(580f, 500f));
            Assert.That(o.HasPan, Is.False);
            Assert.That(o.ToolDragging);
        }

        [Test]
        public void Toque_YDobleToque()
        {
            var g = new GestureInterpreter();
            One(g, new Vector2(300f, 300f));
            GestureOutput o = None(g);
            Assert.That(o.Tap);
            // Segundo toque enseguida y cerca.
            for (int i = 0; i < 5; i++) None(g);
            One(g, new Vector2(305f, 302f));
            o = None(g);
            Assert.That(o.DoubleTap);
            Assert.That(o.Tap, Is.False);
        }

        [Test]
        public void DosToquesLejosEnElTiempo_SonDosToquesSimples()
        {
            var g = new GestureInterpreter();
            One(g, new Vector2(300f, 300f));
            Assert.That(None(g).Tap);
            for (int i = 0; i < 40; i++) None(g); // > 0,32 s
            One(g, new Vector2(300f, 300f));
            GestureOutput o = None(g);
            Assert.That(o.Tap);
            Assert.That(o.DoubleTap, Is.False);
        }

        [Test]
        public void ToqueLargo_SeEmiteUnaVezSinSoltar()
        {
            var g = new GestureInterpreter();
            int longs = 0;
            for (int i = 0; i < 60; i++) if (One(g, new Vector2(100f, 100f)).LongPress) longs++;
            Assert.That(longs, Is.EqualTo(1));
            Assert.That(None(g).Tap, Is.False, "Un toque largo no cuenta como toque");
        }

        [Test]
        public void Pellizco_AcercaConFactorMayorQueUno()
        {
            var g = new GestureInterpreter();
            Two(g, new Vector2(400f, 500f), new Vector2(600f, 500f));
            GestureOutput o = Two(g, new Vector2(380f, 500f), new Vector2(620f, 500f));
            Assert.That(o.ZoomFactor, Is.EqualTo(240f / 200f).Within(1e-4f));
            Assert.That(o.Pivot, Is.EqualTo(new Vector2(500f, 500f)));
        }

        [Test]
        public void GiroDeDosDedos_RotaLaCamara_YConHerramientaRotaElObjeto()
        {
            var g = new GestureInterpreter();
            Two(g, new Vector2(400f, 500f), new Vector2(600f, 500f));
            // Segundo dedo sube: ángulo antihorario de ~ +5,7 grados.
            GestureOutput o = Two(g, new Vector2(400f, 500f), new Vector2(600f, 520f));
            Assert.That(o.RotateDeg, Is.GreaterThan(5f));
            Assert.That(o.ObjectRotateDeg, Is.EqualTo(0f));

            var t = new GestureInterpreter { ToolActive = true };
            Two(t, new Vector2(400f, 500f), new Vector2(600f, 500f));
            o = Two(t, new Vector2(400f, 500f), new Vector2(600f, 520f));
            Assert.That(o.RotateDeg, Is.EqualTo(0f));
            Assert.That(o.ObjectRotateDeg, Is.GreaterThan(5f));
        }

        [Test]
        public void DosDedosEnVertical_Inclinan_YEnHorizontal_Desplazan()
        {
            var g = new GestureInterpreter();
            Vector2 a = new Vector2(400f, 300f), b = new Vector2(600f, 300f);
            Two(g, a, b);
            GestureOutput last = default;
            bool tilted = false, panned = false;
            for (int i = 0; i < 10; i++)
            {
                a.y += 12f; b.y += 12f;
                last = Two(g, a, b);
                if (last.TiltDeg != 0f) tilted = true;
                if (last.HasPan) panned = true;
            }
            Assert.That(tilted);
            Assert.That(panned, Is.False);
            Assert.That(last.TiltDeg, Is.LessThan(0f), "Deslizar hacia arriba lleva la vista hacia el horizonte");

            var h = new GestureInterpreter();
            a = new Vector2(400f, 300f); b = new Vector2(600f, 300f);
            Two(h, a, b);
            tilted = false; panned = false;
            for (int i = 0; i < 10; i++)
            {
                a.x += 12f; b.x += 12f;
                last = Two(h, a, b);
                if (last.TiltDeg != 0f) tilted = true;
                if (last.HasPan) panned = true;
            }
            Assert.That(panned);
            Assert.That(tilted, Is.False);
        }

        [Test]
        public void SoltarUnDedoDeDos_NoDaSaltosEnElDedoQueQueda()
        {
            var g = new GestureInterpreter();
            Two(g, new Vector2(400f, 500f), new Vector2(600f, 500f));
            Two(g, new Vector2(400f, 500f), new Vector2(600f, 500f));
            GestureOutput o = One(g, new Vector2(600f, 500f));
            Assert.That(o.HasPan, Is.False);
            o = One(g, new Vector2(610f, 500f));
            Assert.That(o.HasPan);
            Assert.That(o.PanFrom, Is.EqualTo(new Vector2(600f, 500f)));
            Assert.That(None(g).Tap, Is.False);
        }
    }
}
