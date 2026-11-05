using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using Urbe.Game.Sim;
using Urbe.Sim.Geometry;
using Urbe.Sim.Ids;
using Urbe.Sim.Roads;
using Debug = UnityEngine.Debug;

namespace Urbe.Game.Rendering.Roads
{
    /// <summary>
    /// Lado Unity del render de carreteras (ADR 003): rejilla de chunks de 128 m con un <see cref="Mesh"/> por chunk y un solo material, caché de
    /// geometría por pieza (segmento o nodo) y regeneración local. Se suscribe a los cambios de la red (<c>Subscribe/ConsumeChanges</c>): un
    /// segmento sucio ensucia sus dos nodos y un nodo sucio ensucia sus segmentos. Las piezas sucias se reconstruyen en el hilo principal con
    /// un presupuesto de <see cref="BudgetMs"/> por fotograma; una edición normal termina en el mismo fotograma.
    /// </summary>
    [DefaultExecutionOrder(60)]
    public sealed class RoadChunkRenderer : MonoBehaviour
    {
        public const float ChunkSize = 128f;
        public const float BudgetMs = 4f;

        [SerializeField] private SimHost? host;
        [SerializeField] private Material? material;

        private sealed class PieceMesh
        {
            public RoadVertex[] Vertices = Array.Empty<RoadVertex>();
            public int[] Indices = Array.Empty<int>();
            public int VertexCount;
            public int IndexCount;
            public int ChunkKey = int.MinValue;
            public Vector3 BoundsMin, BoundsMax;
        }

        private sealed class Chunk
        {
            public int Key;
            public GameObject Go = null!;
            public Mesh Mesh = null!;
            public MeshFilter Filter = null!;
            public readonly List<int> Segments = new List<int>();
            public readonly List<int> Nodes = new List<int>();
            public bool Dirty;
            public int Vertices;
            public int Triangles;
        }

        private RoadNetwork? _net;
        private int _subscriber = -1;
        private RoadProfile[] _profiles = Array.Empty<RoadProfile>();
        private IntersectionGeometry? _junctions;
        private readonly MeshBuffers _scratch = new MeshBuffers();
        private PieceMesh?[] _segPieces = new PieceMesh?[64];
        private PieceMesh?[] _nodePieces = new PieceMesh?[64];
        private bool[] _segDirty = new bool[64];
        private bool[] _nodeDirty = new bool[64];
        private readonly List<int> _segQueue = new List<int>();
        private readonly List<int> _nodeQueue = new List<int>();
        private readonly Dictionary<int, Chunk> _chunks = new Dictionary<int, Chunk>();
        private readonly List<Chunk> _dirtyChunks = new List<Chunk>();
        private RoadVertex[] _upload = new RoadVertex[1024];
        private int[] _uploadIdx = new int[3072];
        private ushort[] _uploadIdx16 = new ushort[3072];
        private static readonly VertexAttributeDescriptor[] Layout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 1),
        };

        private Material? _runtimeMaterial;

        /// <summary>
        /// Material de ejecución (copia del asset): la capa de tráfico cambia sus propiedades sin tocar el asset compartido,
        /// que si no quedaría modificado entre sesiones del editor y entre pruebas.
        /// </summary>
        public Material? Material
        {
            get
            {
                if (_runtimeMaterial == null && material != null) _runtimeMaterial = new Material(material) { name = material.name + " (ejecución)" };
                return _runtimeMaterial;
            }
            set { material = value; _runtimeMaterial = null; }
        }
        public RoadNetwork? Network => _net;
        public RoadProfile[] Profiles => _profiles;
        public int ChunkCount => _chunks.Count;
        /// <summary>Piezas (segmentos y nodos) pendientes de reconstruir.</summary>
        public int PendingPieces => _segQueue.Count + _nodeQueue.Count;
        public int PendingChunks => _dirtyChunks.Count;
        /// <summary>Milisegundos y piezas de la última tanda que hizo trabajo (medición de regeneración local).</summary>
        public float LastBatchMs { get; private set; }
        public int LastBatchPieces { get; private set; }
        public int TotalVertices { get; private set; }
        public int TotalTriangles { get; private set; }

        private void OnEnable()
        {
            if (host == null) host = FindFirstObjectByType<SimHost>();
            if (host != null)
            {
                host.SimulationChanged += OnSimulationChanged;
                if (host.IsInitialized && host.Network != null) Attach(host.Network);
            }
        }

        private void OnDisable()
        {
            if (host != null) host.SimulationChanged -= OnSimulationChanged;
            Detach();
        }

        private void OnSimulationChanged(Urbe.Sim.Traffic.TrafficSimulation sim) => Attach(sim.Network);

        // ------------------------------------------------------------------ enlace con la red

        /// <summary>Empieza a pintar esta red (libera la anterior). Todo queda sucio; se reconstruye en el siguiente <see cref="Process"/>.</summary>
        public void Attach(RoadNetwork net)
        {
            Detach();
            _net = net ?? throw new ArgumentNullException(nameof(net));
            _profiles = RoadProfile.BuildAll(net.Types);
            _junctions = new IntersectionGeometry(_profiles);
            _subscriber = net.Subscribe();
            EnsureCapacity(net.SegmentIndexBound, net.NodeIndexBound);
            for (int i = 0; i < net.SegmentIndexBound; i++)
                if (net.SegmentIdAt(i).IsValid) MarkSegment(i);
            for (int i = 0; i < net.NodeIndexBound; i++)
                if (net.NodeIdAt(i).IsValid) MarkNode(i);
        }

        public void Detach()
        {
            if (_net != null && _subscriber >= 0) _net.Unsubscribe(_subscriber);
            _subscriber = -1;
            _net = null;
            foreach (Chunk c in _chunks.Values) DestroyChunk(c);
            _chunks.Clear();
            _dirtyChunks.Clear();
            _segQueue.Clear();
            _nodeQueue.Clear();
            Array.Clear(_segPieces, 0, _segPieces.Length);
            Array.Clear(_nodePieces, 0, _nodePieces.Length);
            Array.Clear(_segDirty, 0, _segDirty.Length);
            Array.Clear(_nodeDirty, 0, _nodeDirty.Length);
            TotalVertices = 0;
            TotalTriangles = 0;
        }

        private void EnsureCapacity(int segs, int nodes)
        {
            if (segs > _segPieces.Length)
            {
                int n = Math.Max(segs, _segPieces.Length * 2);
                Array.Resize(ref _segPieces, n);
                Array.Resize(ref _segDirty, n);
            }
            if (nodes > _nodePieces.Length)
            {
                int n = Math.Max(nodes, _nodePieces.Length * 2);
                Array.Resize(ref _nodePieces, n);
                Array.Resize(ref _nodeDirty, n);
            }
        }

        private void MarkSegment(int i)
        {
            if (_segDirty[i]) return;
            _segDirty[i] = true;
            _segQueue.Add(i);
        }

        private void MarkNode(int i)
        {
            if (_nodeDirty[i]) return;
            _nodeDirty[i] = true;
            _nodeQueue.Add(i);
        }

        // ------------------------------------------------------------------ bucle

        private void LateUpdate()
        {
            if (_net == null) return;
            Process(BudgetMs);
        }

        /// <summary>Recoge los cambios de la red y reconstruye lo sucio dentro del presupuesto. Devuelve true si queda trabajo.</summary>
        public bool Process(float budgetMs)
        {
            if (_net == null) return false;
            CollectChanges();
            if (_segQueue.Count == 0 && _nodeQueue.Count == 0 && _dirtyChunks.Count == 0) return false;

            long t0 = Stopwatch.GetTimestamp();
            int pieces = 0;
            // Nodos primero: las líneas de detención y cierres de mediana de los segmentos dependen del tipo de nodo, no al revés,
            // pero el orden fijo hace la reconstrucción determinista.
            while (_nodeQueue.Count > 0 && (Elapsed(t0) < budgetMs || pieces == 0))
            {
                int i = _nodeQueue[_nodeQueue.Count - 1];
                _nodeQueue.RemoveAt(_nodeQueue.Count - 1);
                _nodeDirty[i] = false;
                RebuildNode(i);
                pieces++;
            }
            while (_segQueue.Count > 0 && (Elapsed(t0) < budgetMs || pieces == 0))
            {
                int i = _segQueue[_segQueue.Count - 1];
                _segQueue.RemoveAt(_segQueue.Count - 1);
                _segDirty[i] = false;
                RebuildSegment(i);
                pieces++;
            }
            if (_segQueue.Count == 0 && _nodeQueue.Count == 0)
            {
                int ci = 0;
                while (ci < _dirtyChunks.Count && (Elapsed(t0) < budgetMs || ci == 0))
                {
                    RebuildChunk(_dirtyChunks[ci]);
                    ci++;
                }
                _dirtyChunks.RemoveRange(0, ci);
            }
            LastBatchMs = (float)Elapsed(t0);
            LastBatchPieces = pieces;
            return PendingPieces > 0 || _dirtyChunks.Count > 0;
        }

        /// <summary>Procesa todo lo pendiente sin límite de tiempo (pruebas, carga inicial).</summary>
        public void Flush()
        {
            int guard = 100000;
            while (guard-- > 0 && Process(float.MaxValue)) { }
        }

        private static double Elapsed(long t0) => (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

        private void CollectChanges()
        {
            RoadNetwork net = _net!;
            if (!net.HasPendingChanges(_subscriber)) return;
            net.ConsumeChanges(_subscriber, out ReadOnlySpan<EntityId> nodes, out ReadOnlySpan<EntityId> segs);
            EnsureCapacity(net.SegmentIndexBound, net.NodeIndexBound);
            int firstSeg = _segQueue.Count;
            for (int i = 0; i < segs.Length; i++) MarkSegment(segs[i].Index);
            for (int i = 0; i < nodes.Length; i++) MarkNode(nodes[i].Index);
            // Un segmento sucio ensucia sus dos nodos...
            for (int i = 0; i < segs.Length; i++)
            {
                EntityId id = net.SegmentIdAt(segs[i].Index);
                if (!id.IsValid) continue;
                ref readonly Segment s = ref net.GetSegment(id);
                MarkNode(s.NodeA.Index);
                MarkNode(s.NodeB.Index);
            }
            // ...y un nodo sucio ensucia sus segmentos incidentes (su retranqueo y sus extremos pudieron cambiar).
            int nodeCount = _nodeQueue.Count;
            for (int q = 0; q < nodeCount; q++)
            {
                EntityId nid = net.NodeIdAt(_nodeQueue[q]);
                if (!nid.IsValid) continue;
                ReadOnlySpan<EntityId> inc = net.GetNodeSegments(nid);
                for (int k = 0; k < inc.Length; k++) MarkSegment(inc[k].Index);
            }
            _ = firstSeg;
        }

        // ------------------------------------------------------------------ piezas

        private void RebuildSegment(int index)
        {
            RoadNetwork net = _net!;
            EntityId id = net.SegmentIdAt(index);
            if (!id.IsValid || !net.IsSegmentAlive(id))
            {
                RemovePiece(_segPieces, index, isNode: false);
                return;
            }
            RoadPiece piece = RoadPieces.FromSegment(net, id);
            _scratch.Clear();
            RoadGeometry.BuildSegment(piece, _profiles[piece.RoadTypeId], _scratch);
            Vec2 mid = piece.Curve.Evaluate(0.5f);
            StorePiece(_segPieces, index, isNode: false, mid.X, mid.Z);
        }

        private void RebuildNode(int index)
        {
            RoadNetwork net = _net!;
            EntityId id = net.NodeIdAt(index);
            if (!id.IsValid || !net.IsNodeAlive(id))
            {
                RemovePiece(_nodePieces, index, isNode: true);
                return;
            }
            _scratch.Clear();
            _junctions!.BuildNode(net, id, _scratch);
            if (_scratch.VertexCount == 0)
            {
                RemovePiece(_nodePieces, index, isNode: true);
                return;
            }
            Vec2 pos = net.GetNode(id).Position;
            StorePiece(_nodePieces, index, isNode: true, pos.X, pos.Z);
        }

        private void StorePiece(PieceMesh?[] store, int index, bool isNode, float x, float z)
        {
            PieceMesh p = store[index] ??= new PieceMesh();
            if (p.Vertices.Length < _scratch.VertexCount) p.Vertices = new RoadVertex[_scratch.VertexCount];
            if (p.Indices.Length < _scratch.IndexCount) p.Indices = new int[_scratch.IndexCount];
            Array.Copy(_scratch.Vertices, p.Vertices, _scratch.VertexCount);
            Array.Copy(_scratch.Indices, p.Indices, _scratch.IndexCount);
            p.VertexCount = _scratch.VertexCount;
            p.IndexCount = _scratch.IndexCount;
            p.BoundsMin = _scratch.BoundsMin;
            p.BoundsMax = _scratch.BoundsMax;

            int key = ChunkKeyOf(x, z);
            if (p.ChunkKey != key)
            {
                if (p.ChunkKey != int.MinValue) DetachFromChunk(p.ChunkKey, index, isNode);
                p.ChunkKey = key;
                Chunk c = GetOrCreateChunk(key);
                (isNode ? c.Nodes : c.Segments).Add(index);
            }
            MarkChunkDirty(p.ChunkKey);
        }

        private void RemovePiece(PieceMesh?[] store, int index, bool isNode)
        {
            PieceMesh? p = store[index];
            if (p == null || p.ChunkKey == int.MinValue) return;
            DetachFromChunk(p.ChunkKey, index, isNode);
            MarkChunkDirty(p.ChunkKey);
            p.ChunkKey = int.MinValue;
            p.VertexCount = 0;
            p.IndexCount = 0;
        }

        private void DetachFromChunk(int key, int index, bool isNode)
        {
            if (!_chunks.TryGetValue(key, out Chunk? c)) return;
            (isNode ? c.Nodes : c.Segments).Remove(index);
            MarkChunkDirty(key);
        }

        private static int ChunkKeyOf(float x, float z)
        {
            int cx = Mathf.FloorToInt(x / ChunkSize) + 32768;
            int cz = Mathf.FloorToInt(z / ChunkSize) + 32768;
            return ((cx & 0xFFFF) << 16) | (cz & 0xFFFF);
        }

        // ------------------------------------------------------------------ chunks

        private Chunk GetOrCreateChunk(int key)
        {
            if (_chunks.TryGetValue(key, out Chunk? c)) return c;
            c = new Chunk { Key = key };
            int cx = (int)((uint)key >> 16) - 32768, cz = (key & 0xFFFF) - 32768;
            c.Go = new GameObject("Chunk " + cx + "," + cz);
            c.Go.transform.SetParent(transform, false);
            c.Mesh = new Mesh { name = "RoadChunk " + cx + "," + cz };
            c.Mesh.MarkDynamic();
            c.Filter = c.Go.AddComponent<MeshFilter>();
            c.Filter.sharedMesh = c.Mesh;
            var mr = c.Go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = Material;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            _chunks.Add(key, c);
            return c;
        }

        private void MarkChunkDirty(int key)
        {
            if (!_chunks.TryGetValue(key, out Chunk? c) || c.Dirty) return;
            c.Dirty = true;
            _dirtyChunks.Add(c);
        }

        private void RebuildChunk(Chunk c)
        {
            c.Dirty = false;
            int vCount = 0, iCount = 0;
            for (int i = 0; i < c.Segments.Count; i++) { PieceMesh? p = _segPieces[c.Segments[i]]; if (p != null) { vCount += p.VertexCount; iCount += p.IndexCount; } }
            for (int i = 0; i < c.Nodes.Count; i++) { PieceMesh? p = _nodePieces[c.Nodes[i]]; if (p != null) { vCount += p.VertexCount; iCount += p.IndexCount; } }
            TotalVertices += vCount - c.Vertices;
            TotalTriangles += iCount / 3 - c.Triangles;
            c.Vertices = vCount;
            c.Triangles = iCount / 3;

            if (vCount == 0)
            {
                c.Mesh.Clear();
                TotalTriangles += 0;
                if (c.Go.activeSelf) c.Go.SetActive(false);
                return;
            }
            if (!c.Go.activeSelf) c.Go.SetActive(true);
            if (_upload.Length < vCount) _upload = new RoadVertex[Mathf.NextPowerOfTwo(vCount)];
            if (_uploadIdx.Length < iCount) { _uploadIdx = new int[Mathf.NextPowerOfTwo(iCount)]; _uploadIdx16 = new ushort[_uploadIdx.Length]; }

            Vector3 bmin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), bmax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            int vo = 0, io = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                List<int> list = pass == 0 ? c.Segments : c.Nodes;
                PieceMesh?[] store = pass == 0 ? _segPieces : _nodePieces;
                for (int i = 0; i < list.Count; i++)
                {
                    PieceMesh? p = store[list[i]];
                    if (p == null || p.VertexCount == 0) continue;
                    Array.Copy(p.Vertices, 0, _upload, vo, p.VertexCount);
                    for (int k = 0; k < p.IndexCount; k++) _uploadIdx[io + k] = p.Indices[k] + vo;
                    vo += p.VertexCount;
                    io += p.IndexCount;
                    bmin = Vector3.Min(bmin, p.BoundsMin);
                    bmax = Vector3.Max(bmax, p.BoundsMax);
                }
            }

            const MeshUpdateFlags flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;
            Mesh m = c.Mesh;
            m.Clear();
            m.SetVertexBufferParams(vCount, Layout);
            m.SetVertexBufferData(_upload, 0, 0, vCount, 0, flags);
            if (vCount <= 65535)
            {
                for (int k = 0; k < iCount; k++) _uploadIdx16[k] = (ushort)_uploadIdx[k];
                m.SetIndexBufferParams(iCount, IndexFormat.UInt16);
                m.SetIndexBufferData(_uploadIdx16, 0, 0, iCount, flags);
            }
            else
            {
                m.SetIndexBufferParams(iCount, IndexFormat.UInt32);
                m.SetIndexBufferData(_uploadIdx, 0, 0, iCount, flags);
            }
            m.SetSubMesh(0, new SubMeshDescriptor(0, iCount, MeshTopology.Triangles), flags);
            var b = new Bounds((bmin + bmax) * 0.5f, bmax - bmin);
            m.bounds = b;
        }

        private static void DestroyChunk(Chunk c)
        {
            if (Application.isPlaying)
            {
                if (c.Mesh != null) Destroy(c.Mesh);
                if (c.Go != null) Destroy(c.Go);
            }
            else
            {
                if (c.Mesh != null) DestroyImmediate(c.Mesh);
                if (c.Go != null) DestroyImmediate(c.Go);
            }
        }

        /// <summary>Vértices de una pieza de segmento (pruebas): copia de la caché.</summary>
        public int CopySegmentVertices(int segmentIndex, List<Vector3> into)
        {
            PieceMesh? p = segmentIndex < _segPieces.Length ? _segPieces[segmentIndex] : null;
            if (p == null) return 0;
            for (int i = 0; i < p.VertexCount; i++) into.Add(p.Vertices[i].Position);
            return p.VertexCount;
        }

        public int CopyNodeVertices(int nodeIndex, List<Vector3> into)
        {
            PieceMesh? p = nodeIndex < _nodePieces.Length ? _nodePieces[nodeIndex] : null;
            if (p == null) return 0;
            for (int i = 0; i < p.VertexCount; i++) into.Add(p.Vertices[i].Position);
            return p.VertexCount;
        }

        private void OnDestroy() => Detach();
    }
}
