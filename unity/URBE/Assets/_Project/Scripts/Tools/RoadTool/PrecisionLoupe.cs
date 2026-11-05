using UnityEngine;

namespace Urbe.Game.Tools.RoadTool
{
    /// <summary>
    /// Lupa de precisión (GDD 11.3, toque largo con herramienta): una segunda cámara ortográfica cenital muy acercada que dibuja en un
    /// RenderTexture, el cual el HUD muestra en un círculo junto al dedo. Solo renderiza mientras la lupa está visible.
    /// </summary>
    public sealed class PrecisionLoupe : MonoBehaviour
    {
        private Camera _cam = null!;
        private Camera? _main;
        private RenderTexture _rt = null!;

        public RenderTexture Texture => _rt;
        /// <summary>Metros de suelo que caben en la mitad de la lupa.</summary>
        public float HalfSizeMeters = 14f;

        public void Init(Camera? main)
        {
            _main = main;
            _rt = new RenderTexture(256, 256, 16, RenderTextureFormat.ARGB32) { name = "LupaRT", antiAliasing = 2 };
            _rt.Create();
            _cam = gameObject.AddComponent<Camera>();
            _cam.orthographic = true;
            _cam.orthographicSize = HalfSizeMeters;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = main != null ? main.backgroundColor : new Color(0.56f, 0.72f, 0.47f);
            _cam.nearClipPlane = 1f;
            _cam.farClipPlane = 400f;
            _cam.targetTexture = _rt;
            _cam.depth = -10f;
            _cam.enabled = false;
        }

        public void SetEnabled(bool on)
        {
            if (_cam != null) _cam.enabled = on;
        }

        public void LookAt(Vector2 ground)
        {
            float yaw = _main != null ? _main.transform.eulerAngles.y : 0f;
            transform.SetPositionAndRotation(new Vector3(ground.x, 150f, ground.y), Quaternion.Euler(90f, yaw, 0f));
            _cam.orthographicSize = HalfSizeMeters;
        }

        private void OnDestroy()
        {
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
        }
    }
}
