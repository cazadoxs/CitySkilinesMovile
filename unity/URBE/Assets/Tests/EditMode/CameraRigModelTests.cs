using NUnit.Framework;
using UnityEngine;
using Urbe.Game.CameraRig;

namespace Urbe.Game.Tests
{
    public class CameraRigModelTests
    {
        private static CameraRigModel Make(float height = 400f, float pitch = 55f, float yaw = 0f)
        {
            var m = new CameraRigModel(new CameraRigSettings(), new Vector2(100f, 200f), height, yaw, pitch);
            m.ScreenSize = new Vector2(1920f, 1080f);
            return m;
        }

        [Test]
        public void CentroDePantalla_ApuntaAlEnfoque()
        {
            CameraRigModel m = Make();
            Assert.That(m.TryGroundPoint(new Vector2(960f, 540f), out Vector2 g));
            Assert.That(g.x, Is.EqualTo(100f).Within(0.05f));
            Assert.That(g.y, Is.EqualTo(200f).Within(0.05f));
        }

        [Test]
        public void ZoomEstaLimitadoA15mY3km()
        {
            CameraRigModel m = Make(400f);
            for (int i = 0; i < 100; i++) m.Zoom(1.5f, new Vector2(960f, 540f), 0.016f);
            Assert.That(m.Height, Is.EqualTo(15f).Within(1e-3f));
            for (int i = 0; i < 100; i++) m.Zoom(1f / 1.5f, new Vector2(960f, 540f), 0.016f);
            Assert.That(m.Height, Is.EqualTo(3000f).Within(1e-2f));
        }

        [Test]
        public void InclinacionEstaLimitadaA25Y85()
        {
            CameraRigModel m = Make();
            m.Tilt(500f, 0.016f);
            Assert.That(m.Pitch, Is.EqualTo(85f));
            m.Tilt(-500f, 0.016f);
            Assert.That(m.Pitch, Is.EqualTo(25f));
        }

        [Test]
        public void ZoomConPivote_MantieneElPuntoBajoElCursor()
        {
            CameraRigModel m = Make();
            var pivot = new Vector2(1500f, 300f);
            Assert.That(m.TryGroundPoint(pivot, out Vector2 before));
            m.Zoom(2f, pivot, 0.016f);
            Assert.That(m.TryGroundPoint(pivot, out Vector2 after));
            Assert.That(Vector2.Distance(before, after), Is.LessThan(0.5f));
            Assert.That(m.Height, Is.EqualTo(200f).Within(0.01f));
        }

        [Test]
        public void ArrastrarMantieneElPuntoBajoElDedo()
        {
            CameraRigModel m = Make(yaw: 33f);
            var from = new Vector2(900f, 500f);
            var to = new Vector2(1000f, 450f);
            Assert.That(m.TryGroundPoint(from, out Vector2 g0));
            m.PanScreen(from, to, 0.016f);
            Assert.That(m.TryGroundPoint(to, out Vector2 g1));
            Assert.That(Vector2.Distance(g0, g1), Is.LessThan(0.2f));
        }

        [Test]
        public void Inercia_ContinuaTrasSoltarYSeDetieneSinRebote()
        {
            CameraRigModel m = Make();
            // Arrastre rápido: 20 fotogramas empujando hacia la derecha.
            for (int i = 0; i < 20; i++)
            {
                m.PanScreen(new Vector2(900f, 540f), new Vector2(930f, 540f), 0.016f);
                m.Step(0.016f);
            }
            Vector2 released = m.Focus;
            Vector2 prev = released;
            float maxStep = 0f;
            for (int i = 0; i < 300; i++)
            {
                m.Step(0.016f);
                maxStep = Mathf.Max(maxStep, (m.Focus - prev).magnitude);
                prev = m.Focus;
            }
            Assert.That((m.Focus - released).magnitude, Is.GreaterThan(1f), "Debe seguir deslizando tras soltar");
            Assert.That(m.IsCoasting, Is.False, "Debe terminar parándose");
            // Sin rebote: a lo largo de la deriva el enfoque no cambia de sentido en x.
            CameraRigModel m2 = Make();
            for (int i = 0; i < 20; i++) { m2.PanScreen(new Vector2(900f, 540f), new Vector2(930f, 540f), 0.016f); m2.Step(0.016f); }
            float lastX = m2.Focus.x;
            for (int i = 0; i < 300; i++)
            {
                m2.Step(0.016f);
                Assert.That(m2.Focus.x - lastX, Is.LessThanOrEqualTo(0.0001f), "Arrastrar a la derecha mueve el enfoque a la izquierda y nunca vuelve hacia atrás");
                lastX = m2.Focus.x;
            }
        }

        [Test]
        public void ElBordeDelMundoNoRebota_YPierdeLaVelocidad()
        {
            var s = new CameraRigSettings { boundsMin = new Vector2(0f, 0f), boundsMax = new Vector2(500f, 500f) };
            var m = new CameraRigModel(s, new Vector2(490f, 250f), 400f);
            m.ScreenSize = new Vector2(1920f, 1080f);
            for (int i = 0; i < 10; i++) { m.PanWorld(new Vector2(30f, 0f), 0.016f); m.Step(0.016f); }
            Assert.That(m.Focus.x, Is.EqualTo(500f));
            for (int i = 0; i < 100; i++)
            {
                m.Step(0.016f);
                Assert.That(m.Focus.x, Is.EqualTo(500f), "No rebota hacia dentro");
            }
        }

        [Test]
        public void Rotar_DaLaVueltaSinSaltos()
        {
            CameraRigModel m = Make();
            m.Rotate(-10f, 0.016f);
            Assert.That(m.Yaw, Is.EqualTo(350f).Within(1e-3f));
            m.Rotate(25f, 0.016f);
            Assert.That(m.Yaw, Is.EqualTo(15f).Within(1e-3f));
        }

        [Test]
        public void DobleToque_CentraYAcercaConAnimacionSuave()
        {
            CameraRigModel m = Make(400f);
            m.FocusOn(new Vector2(300f, 300f), 0.5f);
            Assert.That(m.IsMovingToGoal);
            float lastH = m.Height;
            for (int i = 0; i < 400 && m.IsMovingToGoal; i++)
            {
                m.Step(0.016f);
                Assert.That(m.Height, Is.LessThanOrEqualTo(lastH + 1e-3f), "La altura baja de forma monótona");
                lastH = m.Height;
            }
            Assert.That(m.IsMovingToGoal, Is.False);
            Assert.That(m.Focus, Is.EqualTo(new Vector2(300f, 300f)));
            Assert.That(m.Height, Is.EqualTo(200f).Within(0.1f));
        }

        [Test]
        public void ImpulsoDeRueda_SeReparteConLaInercia()
        {
            CameraRigModel m = Make(400f);
            m.ZoomImpulse(1.2f, new Vector2(960f, 540f));
            for (int i = 0; i < 200; i++) m.Step(0.016f);
            // El impulso se reparte hasta alcanzar el factor pedido, sin pasarse.
            Assert.That(m.Height, Is.EqualTo(400f / 1.2f).Within(1f));
        }

        [Test]
        public void PoseMiraAlEnfoque()
        {
            CameraRigModel m = Make(500f, 60f, 90f);
            m.GetPose(out Vector3 pos, out Quaternion rot);
            Assert.That(pos.y, Is.EqualTo(500f).Within(0.01f));
            Vector3 fwd = rot * Vector3.forward;
            float t = -pos.y / fwd.y;
            Assert.That(pos.x + fwd.x * t, Is.EqualTo(100f).Within(0.05f));
            Assert.That(pos.z + fwd.z * t, Is.EqualTo(200f).Within(0.05f));
        }
    }
}
