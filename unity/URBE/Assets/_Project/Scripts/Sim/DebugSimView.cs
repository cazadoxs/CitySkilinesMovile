using UnityEngine;
using Urbe.Sim.Roads;

namespace Urbe.Game.Sim
{
    /// <summary>
    /// Vista de depuración provisional: suelo plano. Las carreteras las dibuja <c>RoadChunkRenderer</c> (M2-04b) y los vehículos
    /// <c>VehicleRenderer</c> (M2-06); aquí ya solo queda el suelo.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public sealed class DebugSimView : MonoBehaviour
    {
        [SerializeField] private SimHost host = null!;
        [SerializeField] private Material groundMaterial = null!;

        private Mesh? _groundMesh;
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
            if (_groundMesh != null) Destroy(_groundMesh);
        }

        private void Rebuild()
        {
            RoadNetwork net = host.Network!;
            _bounds = NetworkBounds(net);
            if (_groundMesh == null) _groundMesh = BuildGroundMesh();
            _ready = true;
        }

        private void LateUpdate()
        {
            if (!_ready) return;
            Vector3 c = _bounds.center;
            Graphics.DrawMesh(_groundMesh, Matrix4x4.TRS(new Vector3(c.x, -0.08f, c.z), Quaternion.identity, new Vector3(_bounds.size.x + 600f, 1f, _bounds.size.z + 600f)), groundMaterial, 0);
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
