using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace Urbe.Game.Rendering.Vehicles
{
    public interface IVehicleDrawer : IDisposable
    {
        string Name { get; }
        /// <summary>Envía los cubos del batcher a la GPU. Devuelve el número de draw calls enviados.</summary>
        int Draw(VehicleBatcher batcher, VehicleModelSet set);
    }

    /// <summary>Camino A: <c>Graphics.RenderMeshInstanced</c>, hasta 1023 instancias por llamada; el tinte va en un array del MaterialPropertyBlock.</summary>
    public sealed class InstancedMeshDrawer : IVehicleDrawer
    {
        private const int Chunk = 1023;
        private static readonly int TintId = Shader.PropertyToID("_Tint");
        private readonly Material _material;
        private readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
        private readonly Vector4[] _scratch = new Vector4[Chunk];
        private readonly Bounds _bounds = new Bounds(Vector3.zero, new Vector3(20000f, 200f, 20000f));

        public string Name => "RenderMeshInstanced";

        public InstancedMeshDrawer(Material material)
        {
            _material = material;
        }

        public int Draw(VehicleBatcher batcher, VehicleModelSet set)
        {
            int calls = 0;
            for (int b = 0; b < batcher.BucketCount; b++)
            {
                int count = batcher.Counts[b];
                if (count == 0) continue;
                VehicleModelSet.Entry e = set.entries[b / VehicleBatcher.LodCount];
                Mesh mesh = (b % VehicleBatcher.LodCount == 0) ? e.lod0 : e.lod1;
                var rp = new RenderParams(_material)
                {
                    shadowCastingMode = ShadowCastingMode.Off,
                    receiveShadows = false,
                    worldBounds = _bounds,
                    matProps = _mpb,
                };
                for (int start = 0; start < count; start += Chunk)
                {
                    int n = Math.Min(Chunk, count - start);
                    Array.Copy(batcher.Tints[b], start, _scratch, 0, n);
                    _mpb.SetVectorArray(TintId, _scratch);
                    Graphics.RenderMeshInstanced(rp, mesh, 0, batcher.Matrices[b], n, start);
                    calls++;
                }
            }
            return calls;
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Camino B: <c>BatchRendererGroup</c>. Un único lote con un GraphicsBuffer crudo en capas (matrices objeto a mundo, mundo a objeto y tinte);
    /// un comando de dibujo por (modelo, LOD) con instancias consecutivas. Subida de datos con SetData por fotograma, sin asignaciones gestionadas.
    /// </summary>
    public sealed unsafe class BrgDrawer : IVehicleDrawer
    {
        private static readonly int ObjectToWorldId = Shader.PropertyToID("unity_ObjectToWorld");
        private static readonly int WorldToObjectId = Shader.PropertyToID("unity_WorldToObject");
        private static readonly int TintId = Shader.PropertyToID("_Tint");

        private BatchRendererGroup? _brg;
        private GraphicsBuffer? _buffer;
        private BatchID _batch;
        private readonly BatchMaterialID _matId;
        private readonly BatchMeshID[] _meshIds;
        private int _capacity;
        private float[] _o2w = Array.Empty<float>();
        private float[] _w2o = Array.Empty<float>();
        private float[] _tint = Array.Empty<float>();
        private readonly int[] _cmdStart;
        private readonly int[] _cmdCount;
        private readonly int[] _cmdMesh;
        private int _cmds;
        private int _total;

        public string Name => "BatchRendererGroup";

        public BrgDrawer(Material material, VehicleModelSet set)
        {
            _brg = new BatchRendererGroup(OnPerformCulling, IntPtr.Zero);
            _brg.SetGlobalBounds(new Bounds(Vector3.zero, new Vector3(20000f, 200f, 20000f)));
            _matId = _brg.RegisterMaterial(material);
            int n = set.entries.Length * VehicleBatcher.LodCount;
            _meshIds = new BatchMeshID[n];
            for (int i = 0; i < n; i++)
            {
                VehicleModelSet.Entry e = set.entries[i / VehicleBatcher.LodCount];
                _meshIds[i] = _brg.RegisterMesh((i % VehicleBatcher.LodCount == 0) ? e.lod0 : e.lod1);
            }
            _cmdStart = new int[n];
            _cmdCount = new int[n];
            _cmdMesh = new int[n];
            Resize(2048);
        }

        private void Resize(int capacity)
        {
            if (_buffer != null)
            {
                _brg!.RemoveBatch(_batch);
                _buffer.Dispose();
            }
            _capacity = capacity;
            int o2wOff = 64;
            int w2oOff = o2wOff + capacity * 48;
            int tintOff = w2oOff + capacity * 48;
            int bytes = tintOff + capacity * 16;
            _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, bytes / 4, 4);
            _buffer.SetData(new float[16], 0, 0, 16); // matriz cero para instancias no válidas (solo al crecer)
            _o2w = new float[capacity * 12];
            _w2o = new float[capacity * 12];
            _tint = new float[capacity * 4];
            var md = new NativeArray<MetadataValue>(3, Allocator.Temp);
            md[0] = new MetadataValue { NameID = ObjectToWorldId, Value = 0x80000000u | (uint)o2wOff };
            md[1] = new MetadataValue { NameID = WorldToObjectId, Value = 0x80000000u | (uint)w2oOff };
            md[2] = new MetadataValue { NameID = TintId, Value = 0x80000000u | (uint)tintOff };
            _batch = _brg!.AddBatch(md, _buffer.bufferHandle);
            md.Dispose();
        }

        public int Draw(VehicleBatcher batcher, VehicleModelSet set)
        {
            int total = 0;
            for (int b = 0; b < batcher.BucketCount; b++) total += batcher.Counts[b];
            if (total > _capacity)
            {
                int cap = _capacity;
                while (cap < total) cap *= 2;
                Resize(cap);
            }
            int idx = 0, cmds = 0;
            for (int b = 0; b < batcher.BucketCount; b++)
            {
                int count = batcher.Counts[b];
                if (count == 0) continue;
                _cmdStart[cmds] = idx;
                _cmdCount[cmds] = count;
                _cmdMesh[cmds] = b;
                cmds++;
                Matrix4x4[] m = batcher.Matrices[b];
                Vector4[] t = batcher.Tints[b];
                for (int i = 0; i < count; i++, idx++)
                {
                    ref Matrix4x4 a = ref m[i];
                    int o = idx * 12;
                    // float3x4 empaquetado por columnas: columna 0, 1, 2 y traslación (3 floats cada una).
                    _o2w[o + 0] = a.m00; _o2w[o + 1] = a.m10; _o2w[o + 2] = a.m20;
                    _o2w[o + 3] = a.m01; _o2w[o + 4] = a.m11; _o2w[o + 5] = a.m21;
                    _o2w[o + 6] = a.m02; _o2w[o + 7] = a.m12; _o2w[o + 8] = a.m22;
                    _o2w[o + 9] = a.m03; _o2w[o + 10] = a.m13; _o2w[o + 11] = a.m23;
                    // Inversa de una transformación rígida: R^T y -R^T t.
                    _w2o[o + 0] = a.m00; _w2o[o + 1] = a.m01; _w2o[o + 2] = a.m02;
                    _w2o[o + 3] = a.m10; _w2o[o + 4] = a.m11; _w2o[o + 5] = a.m12;
                    _w2o[o + 6] = a.m20; _w2o[o + 7] = a.m21; _w2o[o + 8] = a.m22;
                    _w2o[o + 9] = -(a.m00 * a.m03 + a.m10 * a.m13 + a.m20 * a.m23);
                    _w2o[o + 10] = -(a.m01 * a.m03 + a.m11 * a.m13 + a.m21 * a.m23);
                    _w2o[o + 11] = -(a.m02 * a.m03 + a.m12 * a.m13 + a.m22 * a.m23);
                    int k = idx * 4;
                    _tint[k] = t[i].x; _tint[k + 1] = t[i].y; _tint[k + 2] = t[i].z; _tint[k + 3] = t[i].w;
                }
            }
            _cmds = cmds;
            _total = total;
            if (total > 0)
            {
                int o2wFloat = 64 / 4;
                int w2oFloat = o2wFloat + _capacity * 12;
                int tintFloat = w2oFloat + _capacity * 12;
                _buffer!.SetData(_o2w, 0, o2wFloat, total * 12);
                _buffer.SetData(_w2o, 0, w2oFloat, total * 12);
                _buffer.SetData(_tint, 0, tintFloat, total * 4);
            }
            return cmds;
        }

        private JobHandle OnPerformCulling(BatchRendererGroup group, BatchCullingContext context, BatchCullingOutput output, IntPtr userContext)
        {
            var dc = new BatchCullingOutputDrawCommands();
            int n = _cmds;
            if (n > 0)
            {
                dc.drawCommandCount = n;
                dc.drawRangeCount = 1;
                dc.visibleInstanceCount = _total;
                dc.drawCommands = (BatchDrawCommand*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<BatchDrawCommand>() * n, 16, Allocator.TempJob);
                dc.drawRanges = (BatchDrawRange*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<BatchDrawRange>(), 4, Allocator.TempJob);
                dc.visibleInstances = (int*)UnsafeUtility.Malloc(4 * _total, 16, Allocator.TempJob);
                dc.drawCommandPickingInstanceIDs = null;
                dc.instanceSortingPositions = null;
                dc.instanceSortingPositionFloatCount = 0;
                for (int i = 0; i < n; i++)
                {
                    int start = _cmdStart[i];
                    dc.drawCommands[i] = new BatchDrawCommand
                    {
                        visibleOffset = (uint)start,
                        visibleCount = (uint)_cmdCount[i],
                        batchID = _batch,
                        materialID = _matId,
                        meshID = _meshIds[_cmdMesh[i]],
                        submeshIndex = 0,
                        splitVisibilityMask = 0xff,
                        flags = BatchDrawCommandFlags.None,
                        sortingPosition = 0,
                    };
                    for (int j = 0; j < _cmdCount[i]; j++) dc.visibleInstances[start + j] = start + j;
                }
                dc.drawRanges[0] = new BatchDrawRange
                {
                    drawCommandsBegin = 0,
                    drawCommandsCount = (uint)n,
                    filterSettings = new BatchFilterSettings
                    {
                        renderingLayerMask = 0xffffffff,
                        layer = 0,
                        motionMode = MotionVectorGenerationMode.Camera,
                        shadowCastingMode = ShadowCastingMode.Off,
                        receiveShadows = false,
                        staticShadowCaster = false,
                        allDepthSorted = false,
                    },
                };
            }
            output.drawCommands[0] = dc;
            return default;
        }

        public void Dispose()
        {
            if (_brg != null)
            {
                _brg.Dispose();
                _brg = null;
            }
            if (_buffer != null)
            {
                _buffer.Dispose();
                _buffer = null;
            }
        }
    }
}
