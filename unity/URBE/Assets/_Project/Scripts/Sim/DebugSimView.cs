using UnityEngine;
using Urbe.Sim.Roads;

namespace Urbe.Game.Sim
{
    /// <summary>
    /// Vista de depuración provisional: carreteras como cintas planas y vehículos como cajas instanciadas, interpolados desde
    /// <see cref="SimHost.Interpolate"/>. La sustituyen M2-04 (mallas de carreteras) y M2-06 (render instanciado de vehículos).
    /// Sin asignaciones por fotograma tras la construcción.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public sealed class DebugSimView : MonoBehaviour
    {
        private const int BatchSize = 1023;

        [SerializeField] private SimHost host = null!;
        [SerializeField] private Material roadMaterial = null!;
        [SerializeField] private Material vehicleMaterial = null!;
        [SerializeField] private Material groundMaterial = null!;
        [SerializeField] private Vector3 vehicleSize = new Vector3(2f, 1.6f, 4.5f);

        private Mesh? _roadMesh;
        private Mesh? _vehicleMesh;
        private Mesh? _groundMesh;
        private readonly Matrix4x4[] _batch = new Matrix4x4[BatchSize];
        private Bounds _bounds;

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
            if (_roadMesh != null) Destroy(_roadMesh);
            if (_vehicleMesh != null) Destroy(_vehicleMesh);
            if (_groundMesh != null) Destroy(_groundMesh);
        }

        private void Rebuild()
        {
            RoadNetwork net = host.Network!;
            if (_roadMesh != null) Destroy(_roadMesh);
            _roadMesh = BuildRoadMesh(net, out _bounds);
            if (_vehicleMesh == null) _vehicleMesh = BuildBoxMesh();
            if (_groundMesh == null) _groundMesh = BuildGroundMesh();
        }

        private void LateUpdate()
        {
            if (_roadMesh == null) return;
            Vector3 c = _bounds.center;
            Graphics.DrawMesh(_groundMesh, Matrix4x4.TRS(new Vector3(c.x, -0.05f, c.z), Quaternion.identity, new Vector3(_bounds.size.x + 600f, 1f, _bounds.size.z + 600f)), groundMaterial, 0);
            Graphics.DrawMesh(_roadMesh, Matrix4x4.identity, roadMaterial, 0);

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

        private static Mesh BuildRoadMesh(RoadNetwork net, out Bounds bounds)
        {
            const int steps = 8;
            var verts = new System.Collections.Generic.List<Vector3>();
            var tris = new System.Collections.Generic.List<int>();
            for (int s = 0; s < net.SegmentIndexBound; s++)
            {
                var id = net.SegmentIdAt(s);
                if (!id.IsValid) continue;
                ref readonly Segment seg = ref net.GetSegment(id);
                float half = net.Types.Get(seg.RoadTypeId).TotalWidth * 0.5f;
                int baseIndex = verts.Count;
                for (int i = 0; i <= steps; i++)
                {
                    float t = i / (float)steps;
                    var p = seg.Curve.Evaluate(t);
                    var tg = seg.Curve.Tangent(t).Normalized;
                    var nrm = tg.Perp;
                    verts.Add(new Vector3(p.X + nrm.X * half, 0.02f, p.Z + nrm.Z * half));
                    verts.Add(new Vector3(p.X - nrm.X * half, 0.02f, p.Z - nrm.Z * half));
                    if (i > 0)
                    {
                        int a = baseIndex + (i - 1) * 2;
                        tris.Add(a); tris.Add(a + 2); tris.Add(a + 1);
                        tris.Add(a + 1); tris.Add(a + 2); tris.Add(a + 3);
                    }
                }
            }
            var mesh = new Mesh { name = "DebugRoads", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            bounds = mesh.bounds;
            return mesh;
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
