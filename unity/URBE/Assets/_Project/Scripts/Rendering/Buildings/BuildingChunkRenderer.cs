using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using Urbe.Game.Sim;
using Urbe.Sim.City;
using Urbe.Sim.Ids;

namespace Urbe.Game.Rendering.Buildings
{
    /// <summary>
    /// Render instanciado de los edificios (M3-09, ADR 005): sin GameObject por edificio. Reacciona a los eventos de
    /// <see cref="BuildingSystem.TryDequeueEvent"/> (creado, nivel, abandonado, derribado) y guarda por edificio la matriz y el tinte;
    /// los edificios se agrupan en chunks de 128 m (rejilla fija) que se descartan por frustum, y cada edificio visible va al LOD0 o LOD1 según su
    /// distancia, y a más de 700 m son una caja con tinte (impostor, ADR 005). Se dibuja con <c>RenderMeshInstanced</c> (hasta 1023 instancias por llamada), un cubo por modelo y LOD. Edificios abandonados: oscuros.
    /// El modelo sale de <see cref="BuildingModelPicker"/> (catálogo por zona, densidad, nivel y parcela; la semilla decide entre los empates).
    /// </summary>
    [DefaultExecutionOrder(58)]
    public sealed class BuildingChunkRenderer : MonoBehaviour
    {
        public const int LodCount = 2;
        public const float ChunkMeters = 128f;
        private const int Chunk = 1023;
        private static readonly int TintId = Shader.PropertyToID("_Tint");

        [SerializeField] private SimHost? host;
        [SerializeField] private BuildingCatalog? catalog;
        [SerializeField] private Material? material;
        [SerializeField, Tooltip("Distancia (m) a partir de la cual un edificio se dibuja con el LOD1.")] private float lodDistance = 260f;
        [SerializeField, Tooltip("Distancia (m) a partir de la cual el edificio es una caja con tinte (un solo draw call para toda la ciudad lejana).")] private float impostorDistance = 700f;
        [SerializeField, Tooltip("Distancia (m) a partir de la cual no se dibuja.")] private float maxDistance = 4500f;

        private BuildingModelPicker? _picker;
        private CitySimulation? _city;
        private int _dropped;

        // registro por índice de edificio de la simulación
        private Matrix4x4[] _m = new Matrix4x4[256];
        private Matrix4x4[] _box = new Matrix4x4[256];
        private Vector4[] _tint = new Vector4[256];
        private short[] _model = new short[256];
        private int[] _chunkOf = new int[256];
        private bool[] _abandoned = new bool[256];
        private int _instanceBound;
        private int _alive;

        // chunks: clave -> lista de índices
        private readonly Dictionary<int, ChunkData> _chunks = new Dictionary<int, ChunkData>();
        private readonly List<int> _chunkKeys = new List<int>();

        // cubos por (modelo x LOD)
        private Matrix4x4[][] _bm = Array.Empty<Matrix4x4[]>();
        private Vector4[][] _bt = Array.Empty<Vector4[]>();
        private int[] _bc = Array.Empty<int>();

        private readonly Plane[] _planes = new Plane[6];
        private MaterialPropertyBlock? _mpb;
        private readonly Vector4[] _scratch = new Vector4[Chunk];
        private readonly Bounds _bounds = new Bounds(Vector3.zero, new Vector3(20000f, 400f, 20000f));
        private Camera? _camera;
        private Mesh? _boxMesh;
        private Material? _boxMat;

        private sealed class ChunkData
        {
            public readonly List<int> Items = new List<int>(64);
            public Bounds Box;
        }

        // ------------------------------------------------------------------ medidas

        public int InstanceCount => _alive;
        public int ChunkCount => _chunks.Count;
        public int LastDrawCalls { get; private set; }
        public int LastVisible { get; private set; }
        public int LastLod0 { get; private set; }
        public int LastChunksVisible { get; private set; }
        /// <summary>Tiempo de CPU del último fotograma en reunir y enviar los edificios (ms).</summary>
        public float LastCpuMs { get; private set; }
        public long LastAllocBytes { get; private set; }
        public float LodDistance { get => lodDistance; set => lodDistance = value; }
        public float ImpostorDistance { get => impostorDistance; set => impostorDistance = value; }
        public int LastBoxes { get; private set; }
        public BuildingModelPicker? Picker => _picker;

        public void Configure(SimHost h, BuildingCatalog c, Material m) { host = h; catalog = c; material = m; Setup(); }

        private void OnEnable()
        {
            if (host == null) host = FindFirstObjectByType<SimHost>();
            Setup();
        }

        private void Setup()
        {
            if (catalog == null || catalog.entries.Length == 0 || host == null) return;
            _picker = new BuildingModelPicker(catalog);
            int b = catalog.entries.Length * LodCount + 1; // el último cubo es el de las cajas lejanas
            _bm = new Matrix4x4[b][];
            _bt = new Vector4[b][];
            _bc = new int[b];
            for (int i = 0; i < b; i++) { _bm[i] = new Matrix4x4[64]; _bt[i] = new Vector4[64]; }
            if (_boxMesh == null) _boxMesh = BuildBoxMesh();
            if (_boxMat == null && material != null)
            {
                _boxMat = new Material(material) { name = "EdificiosCaja" };
                _boxMat.SetTexture("_BaseMap", Texture2D.whiteTexture); // alfa 1: toda la caja toma el tinte
            }
            host.SimulationChanged -= OnSimulationChanged;
            host.SimulationChanged += OnSimulationChanged;
            if (host.IsInitialized) Attach(host.City);
        }

        private void OnDisable()
        {
            if (host != null) host.SimulationChanged -= OnSimulationChanged;
        }

        private void OnSimulationChanged(Urbe.Sim.Traffic.TrafficSimulation _) => Attach(host!.City);

        private void Attach(CitySimulation? city)
        {
            _city = city;
            Clear();
            if (city != null) FullRescan();
        }

        private void Clear()
        {
            _chunks.Clear();
            _chunkKeys.Clear();
            Array.Clear(_model, 0, _model.Length);
            for (int i = 0; i < _model.Length; i++) _model[i] = -1;
            _instanceBound = 0;
            _alive = 0;
        }

        // ------------------------------------------------------------------ sincronización con la simulación

        /// <summary>Recorre todos los edificios de la simulación (al instalar una ciudad o si se perdieron eventos).</summary>
        public void FullRescan()
        {
            if (_city == null) return;
            Clear();
            BuildingSystem bs = _city.Buildings;
            while (bs.TryDequeueEvent(out _)) { }
            _dropped = bs.DroppedEvents;
            for (int i = 0; i < bs.IndexBound; i++) if (bs.IsAlive(i)) Upsert(bs, i);
        }

        private void Drain()
        {
            if (_city == null || _picker == null) return;
            BuildingSystem bs = _city.Buildings;
            if (bs.DroppedEvents != _dropped) { FullRescan(); return; }
            while (bs.TryDequeueEvent(out BuildingEvent e))
            {
                int idx = e.Id.Index;
                if (e.Kind == BuildingEventKind.Demolished) { Remove(idx); continue; }
                if (!bs.IsAlive(idx)) continue;
                Upsert(bs, idx);
            }
        }

        private void EnsureCapacity(int n)
        {
            if (n <= _m.Length) return;
            int c = Mathf.NextPowerOfTwo(n);
            int old = _model.Length;
            Array.Resize(ref _m, c);
            Array.Resize(ref _box, c);
            Array.Resize(ref _tint, c);
            Array.Resize(ref _model, c);
            Array.Resize(ref _chunkOf, c);
            Array.Resize(ref _abandoned, c);
            for (int i = old; i < c; i++) _model[i] = -1;
        }

        private void Upsert(BuildingSystem bs, int idx)
        {
            EnsureCapacity(idx + 1);
            ref readonly Building b = ref bs.Get(idx);
            int model;
            Vector3 scale;
            if (b.Kind == BuildingKind.Service)
            {
                model = _picker!.PickService((ServiceType)b.ServiceType);
                scale = model >= 0 ? BuildingModelPicker.FitScale(_picker.Entry(model), b.Width, b.Depth, true) : Vector3.one;
            }
            else
            {
                model = _picker!.PickZoned(b.Zone, b.Density, b.Level, b.Width, b.Depth, b.Seed);
                scale = model >= 0 ? BuildingModelPicker.FitScale(_picker.Entry(model), b.Width, b.Depth, false) : Vector3.one;
            }
            if (model < 0) { Remove(idx); return; }
            bool had = _model[idx] >= 0;
            if (had) DetachFromChunk(idx);
            Matrix4x4 m = Matrix4x4.TRS(new Vector3(b.Pos.X, 0f, b.Pos.Z), Quaternion.Euler(0f, b.Yaw * Mathf.Rad2Deg, 0f), scale);
            _m[idx] = m;
            _box[idx] = BoxMatrix(new Vector3(b.Pos.X, 0f, b.Pos.Z), b.Yaw * Mathf.Rad2Deg, _picker.Entry(model).size, scale);
            _model[idx] = (short)model;
            _abandoned[idx] = b.IsAbandoned;
            _tint[idx] = b.IsAbandoned ? BuildingModelPicker.AbandonedTint
                : (b.Kind == BuildingKind.Service ? new Vector4(0.82f, 0.86f, 0.9f, 1f) : BuildingModelPicker.TintFor(b.Zone, b.Seed));
            if (!had) _alive++;
            if (idx >= _instanceBound) _instanceBound = idx + 1;
            AttachToChunk(idx, b.Pos.X, b.Pos.Z, _picker.Entry(model).size, scale);
        }

        private void Remove(int idx)
        {
            if (idx < 0 || idx >= _model.Length || _model[idx] < 0) return;
            DetachFromChunk(idx);
            _model[idx] = -1;
            _alive--;
        }

        private static int KeyOf(float x, float z)
        {
            int cx = Mathf.FloorToInt(x / ChunkMeters), cz = Mathf.FloorToInt(z / ChunkMeters);
            return (cx + 512) * 4096 + (cz + 512);
        }

        private void AttachToChunk(int idx, float x, float z, Vector3 size, Vector3 scale)
        {
            int key = KeyOf(x, z);
            if (!_chunks.TryGetValue(key, out ChunkData? c))
            {
                c = new ChunkData();
                int cx = Mathf.FloorToInt(x / ChunkMeters), cz = Mathf.FloorToInt(z / ChunkMeters);
                c.Box = new Bounds(new Vector3((cx + 0.5f) * ChunkMeters, 25f, (cz + 0.5f) * ChunkMeters), new Vector3(ChunkMeters + 64f, 120f, ChunkMeters + 64f));
                _chunks[key] = c;
                _chunkKeys.Add(key);
            }
            c.Items.Add(idx);
            _chunkOf[idx] = key;
        }

        private void DetachFromChunk(int idx)
        {
            if (_chunks.TryGetValue(_chunkOf[idx], out ChunkData? c))
            {
                int p = c.Items.IndexOf(idx);
                if (p >= 0) { c.Items[p] = c.Items[c.Items.Count - 1]; c.Items.RemoveAt(c.Items.Count - 1); }
            }
        }

        private static Matrix4x4 BoxMatrix(Vector3 pos, float yawDeg, Vector3 size, Vector3 scale)
            => Matrix4x4.TRS(pos, Quaternion.Euler(0f, yawDeg, 0f), new Vector3(size.x * scale.x, size.y * scale.y, size.z * scale.z));

        /// <summary>Cubo de lado 1 con la base en y = 0 (para escalarlo a la caja del edificio).</summary>
        private static Mesh BuildBoxMesh()
        {
            var v = new List<Vector3>(24);
            var n = new List<Vector3>(24);
            var uv = new List<Vector2>(24);
            var t = new List<int>(36);
            void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 nrm)
            {
                int i = v.Count;
                v.Add(a); v.Add(b); v.Add(c); v.Add(d);
                for (int k = 0; k < 4; k++) { n.Add(nrm); uv.Add(new Vector2(0.5f, 0.5f)); }
                t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3);
            }
            const float h = 0.5f;
            Face(new Vector3(-h, 0, h), new Vector3(-h, 1, h), new Vector3(h, 1, h), new Vector3(h, 0, h), Vector3.forward);
            Face(new Vector3(h, 0, -h), new Vector3(h, 1, -h), new Vector3(-h, 1, -h), new Vector3(-h, 0, -h), Vector3.back);
            Face(new Vector3(h, 0, h), new Vector3(h, 1, h), new Vector3(h, 1, -h), new Vector3(h, 0, -h), Vector3.right);
            Face(new Vector3(-h, 0, -h), new Vector3(-h, 1, -h), new Vector3(-h, 1, h), new Vector3(-h, 0, h), Vector3.left);
            Face(new Vector3(-h, 1, h), new Vector3(-h, 1, -h), new Vector3(h, 1, -h), new Vector3(h, 1, h), Vector3.up);
            var m = new Mesh { name = "CajaEdificio" };
            m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv); m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        // ------------------------------------------------------------------ instancias sueltas (mediciones)

        /// <summary>Añade un edificio de prueba que no viene de la simulación (medir 5.000 edificios sin esperar a que crezcan). Devuelve su índice.</summary>
        public int AddDebugInstance(int model, Vector3 pos, float yawDeg, Vector3 scale, Vector4 tint)
        {
            if (_picker == null) return -1;
            int idx = _instanceBound;
            EnsureCapacity(idx + 1);
            _m[idx] = Matrix4x4.TRS(pos, Quaternion.Euler(0f, yawDeg, 0f), scale);
            _box[idx] = BoxMatrix(pos, yawDeg, _picker.Entry(model).size, scale);
            _model[idx] = (short)model;
            _tint[idx] = tint;
            _abandoned[idx] = false;
            _alive++;
            _instanceBound = idx + 1;
            AttachToChunk(idx, pos.x, pos.z, _picker.Entry(model).size, scale);
            return idx;
        }

        // ------------------------------------------------------------------ dibujo

        private void LateUpdate()
        {
            if (_picker == null || material == null || catalog == null) return;
            if (_camera == null) _camera = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();
            if (_camera == null) return;
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            long t0 = Stopwatch.GetTimestamp();
            Drain();
            Array.Clear(_bc, 0, _bc.Length);
            GeometryUtility.CalculateFrustumPlanes(_camera, _planes);
            Vector3 cp = _camera.transform.position;
            float lodSq = lodDistance * lodDistance, maxSq = maxDistance * maxDistance, impSq = impostorDistance * impostorDistance;
            int boxBucket = _bc.Length - 1;
            int visible = 0, lod0 = 0, chunksVisible = 0;
            for (int k = 0; k < _chunkKeys.Count; k++)
            {
                ChunkData c = _chunks[_chunkKeys[k]];
                if (c.Items.Count == 0) continue;
                if (!GeometryUtility.TestPlanesAABB(_planes, c.Box)) continue;
                chunksVisible++;
                for (int n = 0; n < c.Items.Count; n++)
                {
                    int i = c.Items[n];
                    Matrix4x4 m = _m[i];
                    float dx = m.m03 - cp.x, dy = -cp.y, dz = m.m23 - cp.z;
                    float d2 = dx * dx + dy * dy + dz * dz;
                    if (d2 > maxSq) continue;
                    bool box = d2 > impSq;
                    int lod = d2 > lodSq ? 1 : 0;
                    int bk = box ? boxBucket : _model[i] * LodCount + lod;
                    int cnt = _bc[bk];
                    if (cnt == _bm[bk].Length) { Array.Resize(ref _bm[bk], cnt * 2); Array.Resize(ref _bt[bk], cnt * 2); }
                    _bm[bk][cnt] = box ? _box[i] : m;
                    _bt[bk][cnt] = _tint[i];
                    _bc[bk] = cnt + 1;
                    visible++;
                    if (!box && lod == 0) lod0++;
                }
            }
            _mpb ??= new MaterialPropertyBlock();
            int calls = 0;
            var rp = new RenderParams(material)
            {
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                worldBounds = _bounds,
                matProps = _mpb,
            };
            for (int b = 0; b < _bc.Length; b++)
            {
                int count = _bc[b];
                if (count == 0) continue;
                Mesh? mesh;
                RenderParams p = rp;
                if (b == boxBucket) { mesh = _boxMesh; p.material = _boxMat; }
                else
                {
                    BuildingCatalog.Entry e = catalog.entries[b / LodCount];
                    mesh = (b % LodCount == 0) ? e.lod0 : e.lod1;
                }
                if (mesh == null) continue;
                for (int start = 0; start < count; start += Chunk)
                {
                    int n = Math.Min(Chunk, count - start);
                    Array.Copy(_bt[b], start, _scratch, 0, n);
                    _mpb.SetVectorArray(TintId, _scratch);
                    Graphics.RenderMeshInstanced(p, mesh, 0, _bm[b], n, start);
                    calls++;
                }
            }
            LastDrawCalls = calls;
            LastVisible = visible;
            LastLod0 = lod0;
            LastBoxes = _bc[boxBucket];
            LastChunksVisible = chunksVisible;
            LastCpuMs = (float)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            LastAllocBytes = GC.GetAllocatedBytesForCurrentThread() - a0;
        }
    }
}
