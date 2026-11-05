using UnityEngine;
using Urbe.Game.Rendering.Roads;
using Urbe.Game.Sim;

namespace Urbe.Game.Rendering.Traffic
{
    /// <summary>
    /// Capa de tráfico por flujo (M2-09, GDD 8.7, ADR 003 "Capa de tráfico"): colorea la calzada de cada tramo según su flujo (velocidad real
    /// sobre velocidad libre, suavizado por el núcleo en <c>TrafficMetrics.SegmentFlow</c>). Escribe un texel por segmento en una textura de
    /// 256x256 (índice = SegmentIndex) como mucho a 2 Hz y el shader la lee con el índice de segmento del vértice (uv1): no regenera mallas.
    /// Verde = fluido, amarillo = lento, rojo = atasco; la leyenda del HUD lo dice también con texto (no solo color).
    /// </summary>
    [DefaultExecutionOrder(70)]
    public sealed class TrafficLayer : MonoBehaviour
    {
        public const int TextureSize = 256;
        public const int MaxSegments = TextureSize * TextureSize;
        public const float UpdateInterval = 0.5f;

        [SerializeField] private SimHost? host;
        [SerializeField] private RoadChunkRenderer? roads;

        private Texture2D? _data;
        private readonly Color32[] _pixels = new Color32[MaxSegments];
        private bool _active;
        private float _nextUpdate;

        public bool Active => _active;
        public int UpdateCount { get; private set; }
        public Texture2D? DataTexture => _data;

        // Paleta (verde, ámbar, rojo): se distinguen también por luminosidad y por la leyenda con texto.
        private static readonly Color Free = new Color(0.20f, 0.78f, 0.36f);
        private static readonly Color Slow = new Color(0.98f, 0.78f, 0.15f);
        private static readonly Color Jam = new Color(0.92f, 0.12f, 0.12f);

        private void OnEnable()
        {
            if (host == null) host = FindFirstObjectByType<SimHost>();
            if (roads == null) roads = FindFirstObjectByType<RoadChunkRenderer>();
        }

        private void OnDestroy()
        {
            if (_data != null) Destroy(_data);
        }

        public void Toggle() => SetActive(!_active);

        public void SetActive(bool on)
        {
            _active = on;
            Material? m = roads != null ? roads.Material : null;
            if (m != null)
            {
                if (on) EnsureTexture(m);
                m.SetFloat("_TrafficLayer", on ? 1f : 0f);
            }
            if (on) UpdateData();
        }

        private void EnsureTexture(Material m)
        {
            if (_data == null)
            {
                _data = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false, true)
                {
                    name = "TrafficData", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                };
            }
            m.SetTexture("_TrafficData", _data);
        }

        private void Update()
        {
            if (!_active || Time.unscaledTime < _nextUpdate) return;
            _nextUpdate = Time.unscaledTime + UpdateInterval;
            UpdateData();
        }

        /// <summary>Vuelca el flujo de cada segmento en la textura de datos. Sin asignaciones tras la primera vez.</summary>
        public void UpdateData()
        {
            if (host == null || !host.IsInitialized || host.Simulation == null || _data == null) return;
            var net = host.Network!;
            float[] flow = host.Simulation.Metrics.SegmentFlow;
            int bound = Mathf.Min(net.SegmentIndexBound, MaxSegments);
            for (int i = 0; i < bound; i++)
            {
                bool alive = net.SegmentIdAt(i).IsValid;
                _pixels[i] = alive ? FlowColor(i < flow.Length ? flow[i] : 1f) : new Color32(0, 0, 0, 0);
            }
            _data.SetPixelData(_pixels, 0);
            _data.Apply(false);
            UpdateCount++;
        }

        /// <summary>Color de un flujo en [0,1]: verde desde 0,8, ámbar hacia 0,55 y rojo desde 0,3.</summary>
        public static Color32 FlowColor(float flow)
        {
            Color c;
            if (flow >= 0.8f) c = Free;
            else if (flow >= 0.55f) c = Color.Lerp(Slow, Free, (flow - 0.55f) / 0.25f);
            else if (flow >= 0.3f) c = Color.Lerp(Jam, Slow, (flow - 0.3f) / 0.25f);
            else c = Jam;
            return new Color32((byte)Mathf.RoundToInt(c.r * 255f), (byte)Mathf.RoundToInt(c.g * 255f), (byte)Mathf.RoundToInt(c.b * 255f), 255);
        }
    }
}
