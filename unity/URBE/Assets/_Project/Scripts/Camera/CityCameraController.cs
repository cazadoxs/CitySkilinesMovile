using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

namespace Urbe.Game.CameraRig
{
    /// <summary>
    /// Cámara de ciudad (M2-03): lee Input System (táctil, ratón y teclado), lo traduce con <see cref="GestureInterpreter"/> y mueve
    /// la cámara con <see cref="CameraRigModel"/>. La lógica vive en esas dos clases puras; aquí solo hay entrada y transform.
    /// Ratón (GDD 11.7): izquierdo arrastra (como 1 dedo), medio arrastra siempre, derecho gira e inclina, rueda hace zoom hacia el cursor,
    /// doble clic centra y acerca. Teclado: WASD o flechas desplazan (Mayús x3), Q/E giran, RePág/AvPág inclinan.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [DefaultExecutionOrder(100)]
    public sealed class CityCameraController : MonoBehaviour
    {
        [SerializeField] private CameraRigSettings settings = new CameraRigSettings();
        [SerializeField] private Vector2 startFocus = new Vector2(400f, 400f);
        [SerializeField] private float startHeight = 600f;
        [SerializeField] private float startYaw = 0f;
        [SerializeField] private float startPitch = 55f;
        [SerializeField, Tooltip("Velocidad del teclado en alturas por segundo.")] private float keyboardPanSpeed = 1.1f;
        [SerializeField] private float keyboardRotateDegPerSec = 70f;
        [SerializeField] private float keyboardTiltDegPerSec = 40f;
        [SerializeField] private float mouseRotateDegPerPx = 0.25f;
        [SerializeField] private float mouseTiltDegPerPx = 0.2f;
        [SerializeField] private float wheelZoomPerNotch = 1.2f;

        private Camera _cam = null!;
        private CameraRigModel _model = null!;
        private GestureInterpreter _gestures = null!;
        private readonly int[] _blockedIds = new int[10];
        private int _blockedCount;
        private bool _mouseBlocked;
        private bool _wasTouching;
        private Vector2 _lastMouse;

        /// <summary>Cámara y modelo (lectura para otras tarjetas: picking, minimapa...).</summary>
        public CameraRigModel Model => _model;

        /// <summary>true cuando hay una herramienta de construcción activa: el arrastre de 1 dedo deja de mover la cámara.</summary>
        public bool ToolActive { get => _gestures.ToolActive; set => _gestures.ToolActive = value; }

        /// <summary>Devuelve true si el punto de pantalla (píxeles) cae sobre la interfaz y la cámara debe ignorarlo (lo fija el HUD).</summary>
        public Func<Vector2, bool>? IsOverUi { get; set; }

        public event Action<Vector2>? Tapped;
        public event Action<Vector2>? LongPressed;
        /// <summary>Arrastre de 1 dedo (o ratón) con herramienta activa: de, a (píxeles).</summary>
        public event Action<Vector2, Vector2>? ToolDragged;
        /// <summary>Giro de 2 dedos con herramienta activa: grados.</summary>
        public event Action<float>? ObjectRotated;

        private void Awake()
        {
            _cam = GetComponent<Camera>();
            _model = new CameraRigModel(settings, startFocus, startHeight, startYaw, startPitch);
            _gestures = new GestureInterpreter(GestureConfig.Default.ScaledForDpi(Screen.dpi));
            _cam.fieldOfView = settings.verticalFov;
        }

        private void OnEnable()
        {
            EnhancedTouchSupport.Enable();
            if (Mouse.current != null) _lastMouse = Mouse.current.position.ReadValue();
        }

        private void OnDisable() => EnhancedTouchSupport.Disable();

        private void LateUpdate()
        {
            float dt = Time.unscaledDeltaTime;
            _model.ScreenSize = new Vector2(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height));

            int count = GatherPointers(out Vector2 p0, out Vector2 p1);
            GestureOutput g = _gestures.Update(dt, count, p0, p1);
            ApplyGestures(in g, dt);
            ApplyMouseExtras(dt);
            ApplyKeyboard(dt);

            if (g.Touching)
            {
                if (!_wasTouching) _model.StopInertia();
                _model.HoldAll();
            }
            _wasTouching = g.Touching;

            _model.Step(dt);
            ApplyToCamera();
        }

        private void ApplyGestures(in GestureOutput g, float dt)
        {
            if (g.HasPan) _model.PanScreen(g.PanFrom, g.PanTo, dt);
            if (Mathf.Abs(g.ZoomFactor - 1f) > 1e-5f) _model.Zoom(g.ZoomFactor, g.Pivot, dt);
            if (g.RotateDeg != 0f) _model.Rotate(g.RotateDeg, dt);
            if (g.TiltDeg != 0f) _model.Tilt(g.TiltDeg, dt);
            if (g.ObjectRotateDeg != 0f) ObjectRotated?.Invoke(g.ObjectRotateDeg);
            if (g.ToolDragging) ToolDragged?.Invoke(g.ToolDragFrom, g.ToolDragTo);
            if (g.Tap) Tapped?.Invoke(g.TapPosition);
            if (g.LongPress) LongPressed?.Invoke(g.TapPosition);
            if (g.DoubleTap && !_gestures.ToolActive && _model.TryGroundPoint(g.TapPosition, out Vector2 ground))
                _model.FocusOn(ground, 0.5f);
        }

        // ------------------------------------------------------------------ entrada

        /// <summary>Reúne hasta dos punteros: dedos que no empezaron sobre la interfaz, o el botón izquierdo del ratón como un dedo.</summary>
        private int GatherPointers(out Vector2 p0, out Vector2 p1)
        {
            p0 = p1 = default;
            int n = 0;
            int id0 = 0, id1 = 0;

            var touches = Touch.activeTouches;
            for (int i = 0; i < touches.Count; i++)
            {
                Touch t = touches[i];
                if (t.phase == UnityEngine.InputSystem.TouchPhase.Began && IsOverUi != null && IsOverUi(t.screenPosition)) AddBlocked(t.touchId);
                bool ended = t.phase == UnityEngine.InputSystem.TouchPhase.Ended || t.phase == UnityEngine.InputSystem.TouchPhase.Canceled;
                if (IsBlocked(t.touchId))
                {
                    if (ended) RemoveBlocked(t.touchId);
                    continue;
                }
                if (ended) continue; // un dedo que se levanta deja de contar en el mismo fotograma
                if (n == 0) { p0 = t.screenPosition; id0 = t.touchId; n = 1; }
                else if (n == 1) { p1 = t.screenPosition; id1 = t.touchId; n = 2; }
            }
            if (n == 2 && id1 < id0)
            {
                (p0, p1) = (p1, p0);
            }
            if (n > 0) return n;

            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                Vector2 pos = mouse.position.ReadValue();
                if (mouse.leftButton.wasPressedThisFrame) _mouseBlocked = IsOverUi != null && IsOverUi(pos);
                if (mouse.leftButton.isPressed && !_mouseBlocked) { p0 = pos; return 1; }
            }
            return 0;
        }

        private void ApplyMouseExtras(float dt)
        {
            Mouse mouse = Mouse.current;
            if (mouse == null || Touch.activeTouches.Count > 0) return;
            Vector2 pos = mouse.position.ReadValue();
            bool overUi = IsOverUi != null && IsOverUi(pos);

            float scroll = mouse.scroll.ReadValue().y;
            if (scroll != 0f && !overUi)
            {
                float notches = Mathf.Abs(scroll) > 20f ? scroll / 120f : scroll;
                _model.ZoomImpulse(Mathf.Pow(wheelZoomPerNotch, notches), pos);
            }

            Vector2 delta = pos - _lastMouse;
            _lastMouse = pos;
            if (overUi && !mouse.middleButton.isPressed && !mouse.rightButton.isPressed) return;
            if (mouse.middleButton.isPressed)
                _model.PanScreen(pos - delta, pos, dt);
            if (mouse.rightButton.isPressed)
            {
                _model.Rotate(delta.x * mouseRotateDegPerPx, dt);
                _model.Tilt(-delta.y * mouseTiltDegPerPx, dt);
            }
        }

        private void ApplyKeyboard(float dt)
        {
            Keyboard kb = Keyboard.current;
            if (kb == null) return;
            float x = (kb.dKey.isPressed || kb.rightArrowKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed || kb.leftArrowKey.isPressed ? 1f : 0f);
            float y = (kb.wKey.isPressed || kb.upArrowKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed || kb.downArrowKey.isPressed ? 1f : 0f);
            if (x != 0f || y != 0f)
            {
                float speed = _model.Height * keyboardPanSpeed * (kb.leftShiftKey.isPressed ? 3f : 1f);
                float yaw = _model.Yaw * Mathf.Deg2Rad;
                Vector2 fwd = new Vector2(Mathf.Sin(yaw), Mathf.Cos(yaw));
                Vector2 right = new Vector2(Mathf.Cos(yaw), -Mathf.Sin(yaw));
                Vector2 dir = (right * x + fwd * y).normalized;
                _model.PanWorld(dir * speed * dt, dt);
            }
            float r = (kb.eKey.isPressed ? 1f : 0f) - (kb.qKey.isPressed ? 1f : 0f);
            if (r != 0f) _model.Rotate(r * keyboardRotateDegPerSec * dt, dt);
            float t = (kb.pageDownKey.isPressed ? 1f : 0f) - (kb.pageUpKey.isPressed ? 1f : 0f);
            if (t != 0f) _model.Tilt(t * keyboardTiltDegPerSec * dt, dt);
        }

        private void AddBlocked(int id)
        {
            if (_blockedCount < _blockedIds.Length) _blockedIds[_blockedCount++] = id;
        }

        private bool IsBlocked(int id)
        {
            for (int i = 0; i < _blockedCount; i++) if (_blockedIds[i] == id) return true;
            return false;
        }

        private void RemoveBlocked(int id)
        {
            for (int i = 0; i < _blockedCount; i++)
            {
                if (_blockedIds[i] != id) continue;
                _blockedIds[i] = _blockedIds[--_blockedCount];
                return;
            }
        }

        // ------------------------------------------------------------------ cámara

        private void ApplyToCamera()
        {
            _model.GetPose(out Vector3 pos, out Quaternion rot);
            transform.SetPositionAndRotation(pos, rot);
            float dist = _model.Distance;
            _cam.nearClipPlane = Mathf.Clamp(dist * 0.02f, 0.3f, 40f);
            _cam.farClipPlane = Mathf.Max(dist * 6f, 500f);
        }
    }
}
