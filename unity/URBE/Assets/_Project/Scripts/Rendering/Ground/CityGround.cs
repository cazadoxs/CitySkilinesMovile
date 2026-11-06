using UnityEngine;
using Urbe.Game.Sim;
using Urbe.Sim.Roads;

namespace Urbe.Game.Rendering.Ground
{
    /// <summary>
    /// Suelo de la ciudad (M3-07): césped natural y suave (una sola textura sin repetición, sin cuadrícula) con el tamaño real del mapa (2 x 2 km), un entorno más oscuro fuera
    /// y un borde naranja que marca los límites donde ya no se puede construir. Sin GameObjects por baldosa: dos mallas y cuatro franjas.
    /// Sin ciudad (escenarios de demostración) se ajusta a la caja de la red + 300 m.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public sealed class CityGround : MonoBehaviour
    {
        [SerializeField] private SimHost host = null!;
        [SerializeField] private Material groundMaterial = null!;

        public const float TileMeters = 100f;
        public const float BorderWidth = 5f;

        private Mesh? _quad;
        private Material? _grass, _outside, _border;
        private Texture2D? _tex;
        private float _halfX, _halfZ, _cx, _cz;
        private bool _ready;

        /// <summary>Semilado del mapa jugable en metros (ancho y fondo), para pruebas y para la cámara.</summary>
        public Vector2 HalfSize => new Vector2(_halfX, _halfZ);

        private void OnEnable()
        {
            if (host == null) host = FindFirstObjectByType<SimHost>();
            host.SimulationChanged += OnSimChanged;
            if (host.IsInitialized) Rebuild();
        }

        private void OnDisable()
        {
            if (host != null) host.SimulationChanged -= OnSimChanged;
        }

        private void OnDestroy()
        {
            if (_quad != null) Destroy(_quad);
            if (_tex != null) Destroy(_tex);
            if (_grass != null) Destroy(_grass);
            if (_outside != null) Destroy(_outside);
            if (_border != null) Destroy(_border);
        }

        private void OnSimChanged(Urbe.Sim.Traffic.TrafficSimulation _) => Rebuild();

        private void Rebuild()
        {
            if (host.City != null)
            {
                _halfX = _halfZ = host.City.Balance.MapHalfSize;
                _cx = _cz = 0f;
            }
            else
            {
                Bounds b = NetworkBounds(host.Network!);
                _cx = b.center.x; _cz = b.center.z;
                _halfX = b.extents.x + 300f; _halfZ = b.extents.z + 300f;
            }
            if (_quad == null) _quad = BuildQuad();
            if (_tex == null) _tex = BuildTexture();
            if (_grass == null)
            {
                _grass = new Material(groundMaterial) { name = "Suelo" };
                _grass.SetTexture("_BaseMap", _tex);
                _grass.SetColor("_BaseColor", Color.white);
                _outside = new Material(groundMaterial) { name = "FueraDelMapa" };
                _outside.SetColor("_BaseColor", new Color(0.30f, 0.42f, 0.30f));
                _border = new Material(groundMaterial) { name = "BordeDelMapa" };
                _border.SetColor("_BaseColor", new Color(1f, 0.62f, 0.12f));
            }
            _grass.SetTextureScale("_BaseMap", Vector2.one);
            _ready = true;
        }

        private void LateUpdate()
        {
            if (!_ready) return;
            // Fuera del mapa (más oscuro) y el suelo jugable encima, unos centímetros por encima para no pelear con el fondo.
            Graphics.DrawMesh(_quad, Matrix4x4.TRS(new Vector3(_cx, -0.12f, _cz), Quaternion.identity, new Vector3(12000f, 1f, 12000f)), _outside, 0);
            Graphics.DrawMesh(_quad, Matrix4x4.TRS(new Vector3(_cx, -0.08f, _cz), Quaternion.identity, new Vector3(_halfX * 2f, 1f, _halfZ * 2f)), _grass, 0);
            float w = BorderWidth;
            DrawStrip(_cx, _cz - _halfZ - w * 0.5f, _halfX * 2f + w * 2f, w);
            DrawStrip(_cx, _cz + _halfZ + w * 0.5f, _halfX * 2f + w * 2f, w);
            DrawStrip(_cx - _halfX - w * 0.5f, _cz, w, _halfZ * 2f);
            DrawStrip(_cx + _halfX + w * 0.5f, _cz, w, _halfZ * 2f);
        }

        private void DrawStrip(float x, float z, float sx, float sz)
        {
            Graphics.DrawMesh(_quad, Matrix4x4.TRS(new Vector3(x, -0.04f, z), Quaternion.identity, new Vector3(sx, 1f, sz)), _border, 0);
        }

        private static Bounds NetworkBounds(RoadNetwork net)
        {
            var b = new Bounds(Vector3.zero, Vector3.zero);
            bool first = true;
            for (int i = 0; i < net.NodeIndexBound; i++)
            {
                var id = net.NodeIdAt(i);
                if (!id.IsValid) continue;
                var p = net.GetNode(id).Position;
                var v = new Vector3(p.X, 0f, p.Z);
                if (first) { b = new Bounds(v, Vector3.zero); first = false; }
                else b.Encapsulate(v);
            }
            return b;
        }

        private static Mesh BuildQuad()
        {
            var m = new Mesh { name = "SueloQuad" };
            m.vertices = new[] { new Vector3(-0.5f, 0, -0.5f), new Vector3(-0.5f, 0, 0.5f), new Vector3(0.5f, 0, 0.5f), new Vector3(0.5f, 0, -0.5f) };
            m.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
            m.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 20000f);
            return m;
        }

        /// <summary>
        /// Textura de césped natural que cubre todo el mapa sin repetirse: ruido de valor fractal (cinco octavas, de 500 m a 30 m) que
        /// mezcla tres verdes suaves, sin ningún dibujo regular que parezca una cuadrícula.
        /// </summary>
        private static Texture2D BuildTexture()
        {
            const int n = 1024;
            var t = new Texture2D(n, n, TextureFormat.RGB24, true) { name = "SueloCesped", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var greenA = new Color(0.55f, 0.73f, 0.43f);
            var greenB = new Color(0.50f, 0.68f, 0.40f);
            var dry = new Color(0.62f, 0.72f, 0.42f);
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = x / (float)n, v = y / (float)n;
                    float f = Fractal(u * 4f, v * 4f, 5);
                    float patch = Fractal(u * 9f + 40f, v * 9f + 17f, 3);
                    Color c = Color.Lerp(greenB, greenA, f);
                    c = Color.Lerp(c, dry, Mathf.Clamp01((patch - 0.55f) * 2.2f) * 0.5f);
                    float fine = (Hash(x * 7 + 3, y * 13 + 5) - 0.5f) * 0.03f;
                    px[y * n + x] = new Color(c.r + fine, c.g + fine, c.b + fine * 0.8f, 1f);
                }
            t.SetPixels(px);
            t.Apply(true);
            return t;
        }

        private static float Fractal(float x, float y, int octaves)
        {
            float sum = 0f, amp = 0.5f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += Noise(x, y, o * 31) * amp;
                norm += amp;
                x *= 2f; y *= 2f; amp *= 0.5f;
            }
            return sum / norm;
        }

        private static float Noise(float x, float y, int salt)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float fx = x - xi, fy = y - yi;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float a = Hash(xi + salt, yi), b = Hash(xi + 1 + salt, yi), c = Hash(xi + salt, yi + 1), d = Hash(xi + 1 + salt, yi + 1);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        private static float Hash(int x, int y)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777215f;
            }
        }
    }
}
