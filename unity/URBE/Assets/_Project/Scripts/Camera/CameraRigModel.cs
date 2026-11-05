using System;
using UnityEngine;

namespace Urbe.Game.CameraRig
{
    /// <summary>Límites y respuesta de la cámara (GDD 11.4). Valores serializables para ajustarlos desde el inspector.</summary>
    [Serializable]
    public sealed class CameraRigSettings
    {
        [Header("Límites (GDD 11.4)")]
        public float minHeight = 15f;
        public float maxHeight = 3000f;
        public float minPitch = 25f;
        public float maxPitch = 85f;
        [Tooltip("Rectángulo del mundo (x, z) donde puede estar el punto de enfoque.")]
        public Vector2 boundsMin = new Vector2(-2000f, -2000f);
        public Vector2 boundsMax = new Vector2(2000f, 2000f);

        [Header("Inercia (por segundo; mayor = frena antes)")]
        public float panDamping = 5f;
        public float zoomDamping = 9f;
        public float rotateDamping = 7f;
        public float tiltDamping = 9f;
        [Tooltip("Velocidad a la que se acerca al destino de un doble toque (por segundo).")]
        public float focusSpeed = 7f;

        [Header("Óptica")]
        public float verticalFov = 40f;
    }

    /// <summary>
    /// Modelo puro de la cámara de ciudad (sin MonoBehaviour ni entrada, testeable en EditMode). El enfoque (Focus) es un punto del
    /// suelo (y = 0); la cámara orbita a su alrededor con guiñada (Yaw), inclinación (Pitch, 25 a 85 grados bajo el horizonte) y altura
    /// (Height, 15 m a 3 km). Todo movimiento tiene inercia suave con amortiguación exponencial y, al topar con un límite, se pierde la
    /// velocidad en lugar de rebotar.
    /// </summary>
    public sealed class CameraRigModel
    {
        public CameraRigSettings Settings { get; }

        public Vector2 Focus { get; private set; }
        public float Height { get; private set; }
        public float Yaw { get; private set; }
        public float Pitch { get; private set; }

        /// <summary>Tamaño de pantalla en píxeles (para convertir arrastres en desplazamientos del mundo).</summary>
        public Vector2 ScreenSize { get; set; } = new Vector2(1920f, 1080f);

        private Vector2 _panVel;      // m/s en el suelo
        private float _zoomVel;       // ln(altura)/s (positivo = acercar)
        private Vector2 _zoomPivotPx; // píxeles
        private float _yawVel;        // grados/s
        private float _pitchVel;      // grados/s

        private bool _panTouched, _zoomTouched, _yawTouched, _pitchTouched;

        private bool _hasGoal;
        private Vector2 _goalFocus;
        private float _goalHeight;

        public CameraRigModel(CameraRigSettings? settings = null, Vector2? focus = null, float height = 400f, float yaw = 0f, float pitch = 55f)
        {
            Settings = settings ?? new CameraRigSettings();
            Focus = focus ?? Vector2.zero;
            Height = Mathf.Clamp(height, Settings.minHeight, Settings.maxHeight);
            Yaw = yaw;
            Pitch = Mathf.Clamp(pitch, Settings.minPitch, Settings.maxPitch);
            ClampFocus();
        }

        /// <summary>Hay un destino de "centrar y acercar" en curso.</summary>
        public bool IsMovingToGoal => _hasGoal;

        /// <summary>Distancia del ojo al punto de enfoque a lo largo de la dirección de vista.</summary>
        public float Distance => Height / Mathf.Max(0.05f, Mathf.Sin(Pitch * Mathf.Deg2Rad));

        // ------------------------------------------------------------------ pose

        public Quaternion Rotation => Quaternion.Euler(Pitch, Yaw, 0f);

        public Vector3 Position => new Vector3(Focus.x, 0f, Focus.y) - Rotation * Vector3.forward * Distance;

        public void GetPose(out Vector3 position, out Quaternion rotation)
        {
            rotation = Rotation;
            position = new Vector3(Focus.x, 0f, Focus.y) - rotation * Vector3.forward * Distance;
        }

        /// <summary>Punto del suelo (x, z) bajo un píxel de pantalla, o false si el rayo no toca el suelo (por encima del horizonte).</summary>
        public bool TryGroundPoint(Vector2 screenPx, out Vector2 ground)
        {
            GetPose(out Vector3 pos, out Quaternion rot);
            float nx = screenPx.x / ScreenSize.x * 2f - 1f;
            float ny = screenPx.y / ScreenSize.y * 2f - 1f;
            float tanHalf = Mathf.Tan(Settings.verticalFov * 0.5f * Mathf.Deg2Rad);
            float aspect = ScreenSize.x / ScreenSize.y;
            Vector3 d = rot * Vector3.forward + rot * Vector3.right * (nx * tanHalf * aspect) + rot * Vector3.up * (ny * tanHalf);
            if (d.y >= -1e-4f) { ground = default; return false; }
            float t = -pos.y / d.y;
            ground = new Vector2(pos.x + d.x * t, pos.z + d.z * t);
            return true;
        }

        // ------------------------------------------------------------------ entradas

        /// <summary>
        /// Arrastre: mantiene bajo el dedo el mismo punto del suelo. <paramref name="dt"/> sirve para estimar la velocidad de la inercia.
        /// </summary>
        public void PanScreen(Vector2 fromPx, Vector2 toPx, float dt)
        {
            if (!TryGroundPoint(fromPx, out Vector2 a) || !TryGroundPoint(toPx, out Vector2 b)) return;
            PanWorld(a - b, dt);
        }

        /// <summary>Desplaza el enfoque (metros). Con <paramref name="dt"/> &gt; 0 recuerda la velocidad para la inercia.</summary>
        public void PanWorld(Vector2 delta, float dt)
        {
            CancelGoal();
            Focus += delta;
            _panTouched = true;
            if (dt > 1e-5f) _panVel = Vector2.Lerp(_panVel, delta / dt, 0.6f);
            ClampFocus();
        }

        /// <summary>Zoom directo (pellizco): factor &gt; 1 acerca. El punto del suelo bajo <paramref name="pivotPx"/> se queda donde está.</summary>
        public void Zoom(float factor, Vector2 pivotPx, float dt)
        {
            if (factor <= 0f || Mathf.Abs(factor - 1f) < 1e-6f) { _zoomTouched = true; return; }
            CancelGoal();
            ApplyZoom(factor, pivotPx);
            _zoomTouched = true;
            _zoomPivotPx = pivotPx;
            if (dt > 1e-5f) _zoomVel = Mathf.Lerp(_zoomVel, Mathf.Log(factor) / dt, 0.6f);
        }

        /// <summary>Impulso de zoom (rueda del ratón): se reparte con la inercia en unas décimas de segundo.</summary>
        public void ZoomImpulse(float factor, Vector2 pivotPx)
        {
            if (factor <= 0f) return;
            CancelGoal();
            _zoomVel += Mathf.Log(factor) * Settings.zoomDamping;
            _zoomPivotPx = pivotPx;
        }

        public void Rotate(float degrees, float dt)
        {
            CancelGoal();
            Yaw = Wrap(Yaw + degrees);
            _yawTouched = true;
            if (dt > 1e-5f) _yawVel = Mathf.Lerp(_yawVel, degrees / dt, 0.6f);
        }

        /// <summary>Inclina: positivo = más cenital (hacia 85 grados), negativo = hacia el horizonte (25 grados).</summary>
        public void Tilt(float degrees, float dt)
        {
            CancelGoal();
            SetPitch(Pitch + degrees);
            _pitchTouched = true;
            if (dt > 1e-5f) _pitchVel = Mathf.Lerp(_pitchVel, degrees / dt, 0.6f);
        }

        /// <summary>Doble toque: centra el punto y se acerca (altura x factor) con un movimiento suave.</summary>
        public void FocusOn(Vector2 groundPoint, float heightFactor = 0.5f)
        {
            _hasGoal = true;
            _goalFocus = ClampToBounds(groundPoint);
            _goalHeight = Mathf.Clamp(Height * heightFactor, Settings.minHeight, Settings.maxHeight);
            _panVel = Vector2.zero; _zoomVel = 0f; _yawVel = 0f; _pitchVel = 0f;
        }

        public void CancelGoal() => _hasGoal = false;

        /// <summary>Frena en seco la inercia y cancela la animación (un dedo nuevo toca la pantalla).</summary>
        public void StopInertia()
        {
            CancelGoal();
            _panVel = Vector2.zero; _zoomVel = 0f; _yawVel = 0f; _pitchVel = 0f;
        }

        /// <summary>Marca todos los canales como tocados este fotograma: mientras hay dedos puestos no corre ninguna inercia.</summary>
        public void HoldAll() => _panTouched = _zoomTouched = _yawTouched = _pitchTouched = true;

        /// <summary>Coloca la cámara sin animación (arranque de escena).</summary>
        public void SnapTo(Vector2 focus, float height)
        {
            CancelGoal();
            Focus = focus;
            Height = Mathf.Clamp(height, Settings.minHeight, Settings.maxHeight);
            ClampFocus();
            _panVel = Vector2.zero; _zoomVel = 0f; _yawVel = 0f; _pitchVel = 0f;
        }

        // ------------------------------------------------------------------ paso de tiempo

        /// <summary>Avanza la inercia de los canales que el usuario no está tocando este fotograma y rearma las marcas.</summary>
        public void Step(float dt)
        {
            if (dt <= 0f) { ClearTouched(); return; }

            if (_hasGoal)
            {
                float k = 1f - Mathf.Exp(-Settings.focusSpeed * dt);
                Focus = Vector2.Lerp(Focus, _goalFocus, k);
                Height = Mathf.Exp(Mathf.Lerp(Mathf.Log(Height), Mathf.Log(_goalHeight), k));
                if ((Focus - _goalFocus).sqrMagnitude < 0.01f && Mathf.Abs(Height - _goalHeight) < 0.05f)
                {
                    Focus = _goalFocus; Height = _goalHeight; _hasGoal = false;
                }
            }

            if (!_panTouched)
            {
                Vector2 want = Focus + _panVel * Integral(Settings.panDamping, dt);
                Focus = want;
                ClampFocus();
                if (Focus != want) _panVel = Vector2.zero; // topó con el borde: sin rebote
                _panVel *= Mathf.Exp(-Settings.panDamping * dt);
                if (_panVel.sqrMagnitude < 0.0004f) _panVel = Vector2.zero;
            }
            if (!_zoomTouched && Mathf.Abs(_zoomVel) > 1e-4f)
            {
                float h0 = Height;
                ApplyZoom(Mathf.Exp(_zoomVel * Integral(Settings.zoomDamping, dt)), _zoomPivotPx);
                if (Mathf.Approximately(h0, Height)) _zoomVel = 0f; // topó con el límite de altura
                _zoomVel *= Mathf.Exp(-Settings.zoomDamping * dt);
            }
            else if (!_zoomTouched) _zoomVel = 0f;
            if (!_yawTouched)
            {
                Yaw = Wrap(Yaw + _yawVel * Integral(Settings.rotateDamping, dt));
                _yawVel *= Mathf.Exp(-Settings.rotateDamping * dt);
                if (Mathf.Abs(_yawVel) < 0.02f) _yawVel = 0f;
            }
            if (!_pitchTouched)
            {
                float p0 = Pitch;
                SetPitch(Pitch + _pitchVel * Integral(Settings.tiltDamping, dt));
                if (Mathf.Approximately(p0, Pitch)) _pitchVel = 0f;
                _pitchVel *= Mathf.Exp(-Settings.tiltDamping * dt);
                if (Mathf.Abs(_pitchVel) < 0.02f) _pitchVel = 0f;
            }
            ClearTouched();
        }

        /// <summary>Hay inercia o animación pendiente (la cámara se está moviendo sola).</summary>
        public bool IsCoasting => _hasGoal || _panVel != Vector2.zero || Mathf.Abs(_zoomVel) > 1e-4f || _yawVel != 0f || _pitchVel != 0f;

        // ------------------------------------------------------------------ internos

        /// <summary>Desplazamiento exacto de una velocidad que decae exponencialmente durante dt, por unidad de velocidad inicial.</summary>
        private static float Integral(float damping, float dt) => (1f - Mathf.Exp(-damping * dt)) / damping;

        private void ApplyZoom(float factor, Vector2 pivotPx)
        {
            bool hasBefore = TryGroundPoint(pivotPx, out Vector2 before);
            Height = Mathf.Clamp(Height / factor, Settings.minHeight, Settings.maxHeight);
            if (hasBefore && TryGroundPoint(pivotPx, out Vector2 after))
            {
                Focus += before - after;
                ClampFocus();
            }
        }

        private void SetPitch(float value) => Pitch = Mathf.Clamp(value, Settings.minPitch, Settings.maxPitch);

        private void ClampFocus() => Focus = ClampToBounds(Focus);

        private Vector2 ClampToBounds(Vector2 p) => new Vector2(
            Mathf.Clamp(p.x, Settings.boundsMin.x, Settings.boundsMax.x),
            Mathf.Clamp(p.y, Settings.boundsMin.y, Settings.boundsMax.y));

        private static float Wrap(float deg)
        {
            deg %= 360f;
            return deg < 0f ? deg + 360f : deg;
        }

        private void ClearTouched() => _panTouched = _zoomTouched = _yawTouched = _pitchTouched = false;
    }
}
