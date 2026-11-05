using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Urbe.Game.Rendering.Roads;
using Urbe.Sim.Geometry;

namespace Urbe.Game.Tools.RoadTool
{
    /// <summary>
    /// Fantasma de la herramienta de carreteras (M2-05): pinta las piezas con <see cref="RoadGeometry.BuildSegment"/>, el mismo código que las
    /// carreteras reales, teñidas de azul (válido) o rojo (motivo de rechazo), más guías y marcadores en una malla ligera con color por vértice.
    /// </summary>
    public sealed class RoadGhostRenderer : MonoBehaviour
    {
        private static readonly VertexAttributeDescriptor[] Layout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 1),
        };

        [SerializeField] private Material? roadMaterial;
        [SerializeField] private Material? overlayMaterial;

        private Material? _ghostMat;
        private Mesh? _ghostMesh;
        private Mesh? _overlayMesh;
        private readonly MeshBuffers _buf = new MeshBuffers();
        private RoadVertex[] _upload = new RoadVertex[512];
        private ushort[] _idx16 = new ushort[1536];
        private int[] _idx32 = new int[1536];
        private bool _showGhost;
        private bool _showOverlay;
        private readonly List<Vector3> _ov = new List<Vector3>();
        private readonly List<Color32> _oc = new List<Color32>();
        private readonly List<int> _ot = new List<int>();

        public static readonly Color ValidTint = new Color(0.25f, 0.62f, 1f, 0.50f);
        public static readonly Color InvalidTint = new Color(0.95f, 0.18f, 0.18f, 0.62f);
        public static readonly Color DemolishTint = new Color(1f, 0.35f, 0.15f, 0.62f);

        public Material? RoadMaterial { get => roadMaterial; set { roadMaterial = value; _ghostMat = null; } }
        public Material? OverlayMaterial { get => overlayMaterial; set => overlayMaterial = value; }
        public int GhostTriangles => _showGhost ? _buf.TriangleCount : 0;

        private void OnDestroy()
        {
            if (_ghostMesh != null) Destroy(_ghostMesh);
            if (_overlayMesh != null) Destroy(_overlayMesh);
            if (_ghostMat != null) Destroy(_ghostMat);
        }

        public void HideAll()
        {
            _showGhost = false;
            _showOverlay = false;
        }

        /// <summary>Construye la malla del fantasma con la geometría real. <paramref name="lift"/> la sube unos centímetros para no pelear con la carretera.</summary>
        public void ShowGhost(IReadOnlyList<RoadPiece> pieces, RoadProfile[] profiles, Color tint, float lift = 0.12f)
        {
            if (pieces.Count == 0) { _showGhost = false; return; }
            _buf.Clear();
            for (int i = 0; i < pieces.Count; i++)
            {
                RoadPiece p = pieces[i];
                p.ElevA += lift;
                p.ElevB += lift;
                RoadGeometry.BuildSegment(p, profiles[p.RoadTypeId], _buf);
            }
            Upload(ref _ghostMesh, "RoadGhost");
            if (roadMaterial != null)
            {
                if (_ghostMat == null) _ghostMat = new Material(roadMaterial) { name = "RoadGhost (instancia)" };
                _ghostMat.SetColor("_Tint", tint);
            }
            _showGhost = true;
        }

        public void HideGhost() => _showGhost = false;

        private void Upload(ref Mesh? mesh, string name)
        {
            int vc = _buf.VertexCount, ic = _buf.IndexCount;
            if (mesh == null) { mesh = new Mesh { name = name }; mesh.MarkDynamic(); }
            if (_upload.Length < vc) _upload = new RoadVertex[Mathf.NextPowerOfTwo(vc)];
            System.Array.Copy(_buf.Vertices, _upload, vc);
            const MeshUpdateFlags flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;
            mesh.Clear();
            mesh.SetVertexBufferParams(vc, Layout);
            mesh.SetVertexBufferData(_upload, 0, 0, vc, 0, flags);
            if (vc <= 65535)
            {
                if (_idx16.Length < ic) _idx16 = new ushort[Mathf.NextPowerOfTwo(ic)];
                for (int i = 0; i < ic; i++) _idx16[i] = (ushort)_buf.Indices[i];
                mesh.SetIndexBufferParams(ic, IndexFormat.UInt16);
                mesh.SetIndexBufferData(_idx16, 0, 0, ic, flags);
            }
            else
            {
                if (_idx32.Length < ic) _idx32 = new int[Mathf.NextPowerOfTwo(ic)];
                System.Array.Copy(_buf.Indices, _idx32, ic);
                mesh.SetIndexBufferParams(ic, IndexFormat.UInt32);
                mesh.SetIndexBufferData(_idx32, 0, 0, ic, flags);
            }
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, ic, MeshTopology.Triangles), flags);
            Vector3 mn = _buf.BoundsMin, mx = _buf.BoundsMax;
            mesh.bounds = new Bounds((mn + mx) * 0.5f, mx - mn + Vector3.one);
        }

        // ------------------------------------------------------------------ guías y marcadores

        public void BeginOverlay()
        {
            _ov.Clear();
            _oc.Clear();
            _ot.Clear();
        }

        /// <summary>Línea plana de <paramref name="width"/> metros entre dos puntos del suelo.</summary>
        public void AddLine(Vec2 a, Vec2 b, float width, Color color, float y = 0.3f)
        {
            Vec2 d = b - a;
            float len = d.Length;
            if (len < 1e-3f) return;
            Vec2 n = new Vec2(-d.Z, d.X) * (width * 0.5f / len);
            AddQuad(new Vector3(a.X - n.X, y, a.Z - n.Z), new Vector3(a.X + n.X, y, a.Z + n.Z),
                new Vector3(b.X + n.X, y, b.Z + n.Z), new Vector3(b.X - n.X, y, b.Z - n.Z), color);
        }

        /// <summary>Línea discontinua (guías).</summary>
        public void AddDashed(Vec2 a, Vec2 b, float width, float dash, Color color, float y = 0.3f)
        {
            Vec2 d = b - a;
            float len = d.Length;
            if (len < 1e-3f) return;
            Vec2 u = d / len;
            for (float s = 0f; s < len; s += dash * 2f)
                AddLine(a + u * s, a + u * Mathf.Min(s + dash, len), width, color, y);
        }

        /// <summary>Marcador cuadrado centrado.</summary>
        public void AddMarker(Vec2 c, float size, Color color, float y = 0.35f)
        {
            float h = size * 0.5f;
            AddQuad(new Vector3(c.X - h, y, c.Z - h), new Vector3(c.X - h, y, c.Z + h), new Vector3(c.X + h, y, c.Z + h), new Vector3(c.X + h, y, c.Z - h), color);
        }

        private void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
        {
            int i = _ov.Count;
            _ov.Add(a); _ov.Add(b); _ov.Add(c); _ov.Add(d);
            for (int k = 0; k < 4; k++) _oc.Add(color);
            // Doble cara: el material de sprites no recorta caras, pero así no depende de la orientación.
            _ot.Add(i); _ot.Add(i + 1); _ot.Add(i + 2); _ot.Add(i); _ot.Add(i + 2); _ot.Add(i + 3);
            _ot.Add(i); _ot.Add(i + 2); _ot.Add(i + 1); _ot.Add(i); _ot.Add(i + 3); _ot.Add(i + 2);
        }

        public void EndOverlay()
        {
            if (_overlayMesh == null) { _overlayMesh = new Mesh { name = "RoadToolOverlay" }; _overlayMesh.MarkDynamic(); }
            _overlayMesh.Clear();
            if (_ov.Count == 0) { _showOverlay = false; return; }
            _overlayMesh.SetVertices(_ov);
            _overlayMesh.SetColors(_oc);
            _overlayMesh.SetTriangles(_ot, 0, false);
            _overlayMesh.RecalculateBounds();
            _showOverlay = true;
        }

        private void LateUpdate()
        {
            if (_showGhost && _ghostMesh != null && _ghostMat != null) Graphics.DrawMesh(_ghostMesh, Matrix4x4.identity, _ghostMat, 0);
            if (_showOverlay && _overlayMesh != null && overlayMaterial != null) Graphics.DrawMesh(_overlayMesh, Matrix4x4.identity, overlayMaterial, 0);
        }
    }
}
