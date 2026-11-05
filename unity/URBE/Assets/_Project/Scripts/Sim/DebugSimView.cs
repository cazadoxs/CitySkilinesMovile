using UnityEngine;
using Urbe.Sim.Roads;

namespace Urbe.Game.Sim
{
    /// <summary>
    /// Vista de depuración provisional: suelo plano y vehículos como cajas instanciadas, interpolados desde
    /// <see cref="SimHost.Interpolate"/>. Las carreteras ya no se dibujan aquí (M2-04b: <c>RoadChunkRenderer</c>); M2-06 sustituirá los vehículos.
    /// Sin asignaciones por fotograma tras la construcción.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public sealed class DebugSimView : MonoBehaviour
    {
        private const int BatchSize = 1023;

        [SerializeField] private SimHost host = null!;
        [SerializeField] private Material vehicleMaterial = null!;
        [SerializeField] private Material groundMaterial = null!;
        [SerializeField] private Vector3 vehicleSize = new Vector3(2f, 1.6f, 4.5f);

        private Mesh? _vehicleMesh;
        private Mesh? _groundMesh;
        private readonly Matrix4x4[] _batch = new Matrix4x4[BatchSize];
        private Bounds _bounds;
        private bool _ready;

        private void OnEnable()
        {
            if (host == null) host = FindFirstObjectByType<SimHost>();
            host.SimulationChanged += OnSimChanged;
            if (host.IsInitialized) Rebuild();
        }

        private void OnSimChanged(Urbe.Sim.Traffic.TrafficSimulation _) => Rebuild();

        private void OnDisable()
        {
            if (host != null) host.SimulationChanged -= OnSimChanged;
        }

        private void OnDestroy()
        {
            if (_vehicleMesh != null) Destroy(_vehicleMesh);
            if (_groundMesh != null) Destroy(_groundMesh);
        }

        private void Rebuild()
        {
            RoadNetwork net = host.Network!;
            _bounds = NetworkBounds(net);
            if (_vehicleMesh == null) _vehicleMesh = BuildBoxMesh();
            if (_groundMesh == null) _groundMesh = BuildGroundMesh();
            _ready = true;
        }

        private void LateUpdate()
        {
            if (!_ready) return;
            Vector3 c = _bounds.center;
            Graphics.DrawMesh(_groundMesh, Matrix4x4.TRS(new Vector3(c.x, -0.08f, c.z), Quaternion.identity, new Vector3(_bounds.size.x + 600f, 1f, _bounds.size.z + 600f)), groundMaterial, 0);

            SnapshotInterpolator v = host.Interpolate();
            InterpolatedVehicle[] o = v.Output;
            int n = v.Count, filled = 0;
            for (int i = 0; i < n; i++)
            {
                ref InterpolatedVehicle q = ref o[i];
                // Position es el frente del vehículo: se retrasa media longitud para centrar la caja.
                float cx = q.X - q.DirX * vehicleSize.z * 0.5f;
                float cz = q.Z - q.DirZ * vehicleSize.z * 0.5f;
                float yaw = Mathf.Atan2(q.DirX, q.DirZ) * Mathf.Rad2Deg;
                _batch[filled++] = Matrix4x4.TRS(new Vector3(cx, q.Y + vehicleSize.y * 0.5f + 0.1f, cz), Quaternion.Euler(0f, yaw, 0f), vehicleSize);
                if (filled == BatchSize)
                {
                    Graphics.DrawMeshInstanced(_vehicleMesh, 0, vehicleMaterial, _batch, filled);
                    filled = 0;
                }
            }
            if (filled > 0) Graphics.DrawMeshInstanced(_vehicleMesh, 0, vehicleMaterial, _batch, filled);
        }

        /// <summary>Caja que envuelve los nodos de la red (para dimensionar el suelo).</summary>
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

        private static Mesh BuildBoxMesh()
        {
            GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh m = Instantiate(tmp.GetComponent<MeshFilter>().sharedMesh);
            Destroy(tmp);
            return m;
        }

        private static Mesh BuildGroundMesh()
        {
            var m = new Mesh { name = "DebugGround" };
            m.vertices = new[] { new Vector3(-0.5f, 0, -0.5f), new Vector3(-0.5f, 0, 0.5f), new Vector3(0.5f, 0, 0.5f), new Vector3(0.5f, 0, -0.5f) };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.RecalculateBounds();
            return m;
        }
    }
}
