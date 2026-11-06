using UnityEngine;
using UnityEngine.Rendering;
using Urbe.Game.Sim;
using Urbe.Sim.City;

namespace Urbe.Game.Rendering.Zones
{
    /// <summary>
    /// Capa de celdas de zona (M3-08, GDD 5.4): una malla dinámica con un cuadrado por celda de 8 m, coloreado por zona (verde residencial, azul comercial,
    /// ámbar industrial, violeta oficinas) y más intenso cuanto mayor la densidad; las celdas ya ocupadas por un edificio se atenúan y las libres sin zona
    /// se dibujan como una rejilla tenue para ver dónde se puede pintar. Solo se reconstruye mientras está visible (cuatro veces por segundo)
    /// y reutiliza sus arrays: sin asignaciones en régimen estable.
    /// </summary>
    [DefaultExecutionOrder(56)]
    public sealed class ZoneOverlayRenderer : MonoBehaviour
    {
        public static readonly Color32 ColorR = new Color32(76, 204, 89, 255);
        public static readonly Color32 ColorC = new Color32(64, 140, 242, 255);
        public static readonly Color32 ColorI = new Color32(242, 191, 51, 255);
        public static readonly Color32 ColorO = new Color32(158, 115, 235, 255);

        [SerializeField] private SimHost? host;
        [SerializeField] private Material? material;
        [SerializeField] private float refreshSeconds = 0.25f;

        private Mesh? _mesh;
        private Vector3[] _v = new Vector3[4096];
        private Color32[] _c = new Color32[4096];
        private int[] _t = new int[6144];
        private float _next;
        private bool _visible;
        private bool _dirty = true;

        public bool Visible
        {
            get => _visible;
            set { if (_visible == value) return; _visible = value; _dirty = true; if (!value) _mesh?.Clear(); }
        }

        /// <summary>Celdas dibujadas en la última reconstrucción (zonificadas y libres) y cuántas tienen zona; para pruebas.</summary>
        public int CellsDrawn { get; private set; }
        public int ZonedDrawn { get; private set; }

        public void MarkDirty() => _dirty = true;

        public static Color32 ColorOf(ZoneType z)
        {
            switch (z)
            {
                case ZoneType.Residential: return ColorR;
                case ZoneType.Commercial: return ColorC;
                case ZoneType.Industrial: return ColorI;
                case ZoneType.Office: return ColorO;
                default: return new Color32(255, 255, 255, 255);
            }
        }

        private void OnEnable()
        {
            if (host == null) host = FindFirstObjectByType<SimHost>();
            if (host != null) host.CommandExecuted += OnCommand;
        }

        private void OnDisable()
        {
            if (host != null) host.CommandExecuted -= OnCommand;
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
        }

        private void OnCommand(CityCommand _, CityResult __) => _dirty = true;

        private void LateUpdate()
        {
            if (!_visible || host == null || host.City == null || material == null) return;
            if (_mesh == null) { _mesh = new Mesh { name = "ZonaCeldas", indexFormat = IndexFormat.UInt32 }; _mesh.MarkDynamic(); }
            float now = Time.unscaledTime;
            if (now >= _next || _dirty)
            {
                _next = now + refreshSeconds;
                _dirty = false;
                Rebuild(host.City.Zones);
            }
            Graphics.DrawMesh(_mesh, Matrix4x4.identity, material, 0);
        }

        private void Grow(int quads)
        {
            int need = quads * 4;
            if (need <= _v.Length) return;
            int c = Mathf.NextPowerOfTwo(need);
            System.Array.Resize(ref _v, c);
            System.Array.Resize(ref _c, c);
            System.Array.Resize(ref _t, c / 4 * 6);
        }

        private void Rebuild(ZoneGrid zones)
        {
            Grow(zones.CellIndexBound);
            int n = 0, zoned = 0;
            const float half = 3.7f, y = 0.07f;
            for (int i = 0; i < zones.CellIndexBound; i++)
            {
                if (!zones.IsCellAlive(i)) continue;
                ref readonly ZoneCell cell = ref zones.GetCell(i);
                Color32 col;
                if (cell.Zone == ZoneType.None) col = new Color32(255, 255, 255, 26);
                else
                {
                    col = ColorOf(cell.Zone);
                    float a = cell.Density <= 1 ? 0.45f : (cell.Density == 2 ? 0.58f : 0.72f);
                    if (cell.Building >= 0) a *= 0.30f;
                    col.a = (byte)(a * 255f);
                    zoned++;
                }
                float dx = cell.Dir.X, dz = cell.Dir.Z;
                float l = Mathf.Sqrt(dx * dx + dz * dz);
                if (l < 1e-4f) { dx = 1f; dz = 0f; } else { dx /= l; dz /= l; }
                float px = -dz, pz = dx; // perpendicular
                float ax = dx * half, az = dz * half, bx = px * half, bz = pz * half;
                float cx = cell.Pos.X, cz = cell.Pos.Z;
                int vi = n * 4;
                _v[vi] = new Vector3(cx - ax - bx, y, cz - az - bz);
                _v[vi + 1] = new Vector3(cx + ax - bx, y, cz + az - bz);
                _v[vi + 2] = new Vector3(cx + ax + bx, y, cz + az + bz);
                _v[vi + 3] = new Vector3(cx - ax + bx, y, cz - az + bz);
                _c[vi] = _c[vi + 1] = _c[vi + 2] = _c[vi + 3] = col;
                int ti = n * 6;
                _t[ti] = vi; _t[ti + 1] = vi + 1; _t[ti + 2] = vi + 2; _t[ti + 3] = vi; _t[ti + 4] = vi + 2; _t[ti + 5] = vi + 3;
                n++;
            }
            CellsDrawn = n;
            ZonedDrawn = zoned;
            _mesh!.Clear();
            if (n == 0) return;
            _mesh.SetVertices(_v, 0, n * 4);
            _mesh.SetColors(_c, 0, n * 4);
            _mesh.SetIndexBufferParams(n * 6, IndexFormat.UInt32);
            _mesh.SetIndexBufferData(_t, 0, 0, n * 6, MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
            _mesh.SetSubMesh(0, new SubMeshDescriptor(0, n * 6, MeshTopology.Triangles), MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
            _mesh.bounds = new Bounds(Vector3.zero, new Vector3(4200f, 20f, 4200f));
        }
    }
}
