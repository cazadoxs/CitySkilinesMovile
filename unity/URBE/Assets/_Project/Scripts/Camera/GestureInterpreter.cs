using System;
using UnityEngine;

namespace Urbe.Game.CameraRig
{
    /// <summary>Umbrales de los gestos. Los píxeles se escalan con el DPI en el controlador (<see cref="ScaledForDpi"/>).</summary>
    [Serializable]
    public struct GestureConfig
    {
        public float slopPx;
        public float tapMaxSeconds;
        public float doubleTapSeconds;
        public float doubleTapRadiusPx;
        public float longPressSeconds;
        public float tiltDegreesPerPx;
        [Tooltip("En dos dedos, el movimiento es inclinación si |dy| supera este múltiplo de |dx|.")]
        public float tiltVerticalRatio;

        public static GestureConfig Default => new GestureConfig
        {
            slopPx = 14f,
            tapMaxSeconds = 0.25f,
            doubleTapSeconds = 0.32f,
            doubleTapRadiusPx = 56f,
            longPressSeconds = 0.5f,
            tiltDegreesPerPx = 0.12f,
            tiltVerticalRatio = 1.8f,
        };

        /// <summary>Escala los umbrales en píxeles a partir del DPI de la pantalla (160 dpi = 1x).</summary>
        public GestureConfig ScaledForDpi(float dpi)
        {
            float s = Mathf.Clamp(dpi <= 1f ? 1f : dpi / 160f, 0.75f, 4f);
            GestureConfig c = this;
            c.slopPx *= s; c.doubleTapRadiusPx *= s; c.tiltDegreesPerPx /= s;
            return c;
        }
    }

    /// <summary>Resultado de un fotograma de gestos. Todo lo que no se mencione vale 0/false (ZoomFactor vale 1).</summary>
    public struct GestureOutput
    {
        /// <summary>Arrastre de cámara (1 dedo sin herramienta, o 2 dedos): mantener bajo el dedo el punto del suelo.</summary>
        public bool HasPan;
        public Vector2 PanFrom, PanTo;
        /// <summary>Pellizco: &gt; 1 acerca. Con 2 dedos. Pivote = centro entre los dedos.</summary>
        public float ZoomFactor;
        public Vector2 Pivot;
        /// <summary>Giro de dos dedos, en grados para sumar a la guiñada de la cámara (sin herramienta).</summary>
        public float RotateDeg;
        /// <summary>Con herramienta activa, el mismo giro rota el objeto que se coloca.</summary>
        public float ObjectRotateDeg;
        /// <summary>Deslizar dos dedos en vertical: grados para <see cref="CameraRigModel.Tilt"/> (positivo = más cenital).</summary>
        public float TiltDeg;
        /// <summary>Con herramienta activa, el arrastre de 1 dedo es de la herramienta (trazar, pintar), no de la cámara.</summary>
        public bool ToolDragging;
        public Vector2 ToolDragFrom, ToolDragTo;
        public bool Tap, DoubleTap, LongPress;
        public Vector2 TapPosition;
        /// <summary>Hay algún dedo en pantalla.</summary>
        public bool Touching;
    }

    /// <summary>
    /// Interpreta dedos (GDD 11.3) sin depender de Unity Input: recibe 0, 1 o 2 puntos por fotograma y devuelve qué gesto es.
    /// Sin herramienta: 1 dedo desplaza, 2 dedos desplazan/zoom/giran, deslizar 2 dedos en vertical inclina, doble toque centra,
    /// toque largo = información. Con herramienta: 1 dedo es de la herramienta y el giro rota el objeto.
    /// Desambiguación 2 dedos: tras superar la holgura, un movimiento del centro mayoritariamente vertical es inclinar y el resto
    /// es desplazar; el pellizco y el giro siempre funcionan a la vez. Sin asignaciones.
    /// </summary>
    public sealed class GestureInterpreter
    {
        private enum TwoFingerMode { Undecided, Pan, Tilt }

        public GestureConfig Config;
        /// <summary>true cuando hay una herramienta de construcción activa (tabla de GDD 11.3, columna derecha).</summary>
        public bool ToolActive;

        private float _clock;
        private int _prevCount;
        private Vector2 _prev0, _prev1;

        // 1 dedo
        private Vector2 _start;
        private float _startTime;
        private bool _dragging;
        private bool _longFired;
        private bool _hadTwo;
        private Vector2 _last0;
        private bool _toolDragActive;

        // 2 dedos
        private TwoFingerMode _mode;
        private Vector2 _cumCentroid;

        // doble toque
        private float _lastTapTime = -10f;
        private Vector2 _lastTapPos;

        public GestureInterpreter() { Config = GestureConfig.Default; }
        public GestureInterpreter(GestureConfig config) { Config = config; }

        public GestureOutput Update(float dt, int touchCount, Vector2 p0, Vector2 p1)
        {
            _clock += dt;
            var o = new GestureOutput { ZoomFactor = 1f, Touching = touchCount > 0 };

            if (touchCount == 1)
            {
                if (_prevCount != 1)
                {
                    if (_prevCount == 0)
                    {
                        _start = p0; _startTime = _clock; _dragging = false; _longFired = false; _hadTwo = false; _toolDragActive = false;
                    }
                    else
                    {
                        // 2 -> 1 dedo: el que queda empieza a arrastrar desde donde está, sin saltos.
                        _start = p0; _dragging = true; _hadTwo = true;
                    }
                }
                else
                {
                    if (!_dragging && (p0 - _start).sqrMagnitude > Config.slopPx * Config.slopPx) _dragging = true;
                    if (_dragging)
                    {
                        if (ToolActive || _toolDragActive)
                        {
                            _toolDragActive = true;
                            o.ToolDragging = true; o.ToolDragFrom = _prev0; o.ToolDragTo = p0;
                        }
                        else
                        {
                            o.HasPan = true; o.PanFrom = _prev0; o.PanTo = p0;
                        }
                    }
                    else if (!_longFired && !_hadTwo && _clock - _startTime >= Config.longPressSeconds)
                    {
                        _longFired = true;
                        o.LongPress = true; o.TapPosition = p0;
                    }
                }
                _last0 = p0;
            }
            else if (touchCount >= 2)
            {
                Vector2 c = (p0 + p1) * 0.5f;
                if (_prevCount != 2)
                {
                    _mode = TwoFingerMode.Undecided; _cumCentroid = Vector2.zero; _hadTwo = true;
                }
                else
                {
                    Vector2 pc = (_prev0 + _prev1) * 0.5f;
                    float d = (p1 - p0).magnitude, pd = (_prev1 - _prev0).magnitude;
                    if (pd > 1f && d > 1f) { o.ZoomFactor = d / pd; o.Pivot = c; }
                    float angle = Mathf.Atan2((p1 - p0).y, (p1 - p0).x);
                    float pAngle = Mathf.Atan2((_prev1 - _prev0).y, (_prev1 - _prev0).x);
                    float dAng = Mathf.DeltaAngle(pAngle * Mathf.Rad2Deg, angle * Mathf.Rad2Deg);
                    if (ToolActive) o.ObjectRotateDeg = dAng; else o.RotateDeg = dAng;

                    Vector2 dc = c - pc;
                    if (_mode == TwoFingerMode.Undecided)
                    {
                        _cumCentroid += dc;
                        if (_cumCentroid.sqrMagnitude > Config.slopPx * Config.slopPx)
                            _mode = Mathf.Abs(_cumCentroid.y) > Mathf.Abs(_cumCentroid.x) * Config.tiltVerticalRatio ? TwoFingerMode.Tilt : TwoFingerMode.Pan;
                    }
                    else if (_mode == TwoFingerMode.Pan)
                    {
                        o.HasPan = true; o.PanFrom = pc; o.PanTo = c;
                    }
                    else
                    {
                        // Deslizar hacia arriba lleva la vista hacia el horizonte (menos cenital).
                        o.TiltDeg = -dc.y * Config.tiltDegreesPerPx;
                    }
                }
            }
            else // 0 dedos
            {
                if (_prevCount == 1 && !_dragging && !_hadTwo && !_longFired && _clock - _startTime <= Config.tapMaxSeconds)
                {
                    bool dbl = _clock - _lastTapTime <= Config.doubleTapSeconds
                               && (_last0 - _lastTapPos).sqrMagnitude <= Config.doubleTapRadiusPx * Config.doubleTapRadiusPx;
                    o.TapPosition = _last0;
                    if (dbl) { o.DoubleTap = true; _lastTapTime = -10f; }
                    else { o.Tap = true; _lastTapTime = _clock; _lastTapPos = _last0; }
                }
            }

            _prevCount = Mathf.Min(touchCount, 2);
            _prev0 = p0; _prev1 = p1;
            return o;
        }

        public void Reset()
        {
            _prevCount = 0; _dragging = false; _longFired = false; _hadTwo = false; _toolDragActive = false;
            _mode = TwoFingerMode.Undecided; _lastTapTime = -10f;
        }
    }
}
