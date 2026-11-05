using System.Diagnostics;
using UnityEngine;
using Urbe.Game.Sim;

namespace Urbe.Game.Rendering.Vehicles
{
    public enum VehicleDrawMode
    {
        RenderMeshInstanced = 0,
        BatchRendererGroup = 1,
    }

    /// <summary>
    /// Render instanciado de los vehículos (M2-06, GDD 8.6 y 8.15). Lee <see cref="SimHost.Interpolate"/> una vez por fotograma (LateUpdate),
    /// reparte en cubos modelo x LOD con descarte por frustum y los dibuja con el camino elegido. Sin GameObjects por vehículo ni asignaciones por fotograma.
    /// El camino por defecto es provisional hasta medir en el móvil (docs/perf/m2-06-vehiculos.md).
    /// </summary>
    [DefaultExecutionOrder(60)]
    public sealed class VehicleRenderer : MonoBehaviour
    {
        [SerializeField] private SimHost? host;
        [SerializeField] private VehicleModelSet? models;
        [SerializeField] private Material? material;
        [SerializeField] private VehicleDrawMode mode = VehicleDrawMode.RenderMeshInstanced;
        [SerializeField, Tooltip("Distancia (m) a partir de la cual se usa el LOD1.")] private float lodDistance = 120f;

        private VehicleBatcher? _batcher;
        private IVehicleDrawer? _drawer;
        private Camera? _camera;

        public VehicleDrawMode Mode => mode;
        public float LodDistance { get => lodDistance; set => lodDistance = value; }
        public int LastDrawCalls { get; private set; }
        public int LastVisible { get; private set; }
        public int LastCulled { get; private set; }
        /// <summary>Tiempo de CPU del último fotograma en reunir y enviar los vehículos (ms).</summary>
        public float LastCpuMs { get; private set; }
        /// <summary>Bytes gestionados asignados en el ultimo fotograma por este componente (0 en regimen estable).</summary>
        public long LastAllocBytes { get; private set; }
        public VehicleBatcher? Batcher => _batcher;

        private void OnEnable()
        {
            if (host == null) host = FindFirstObjectByType<SimHost>();
            CreateDrawer();
        }

        private void OnDisable()
        {
            _drawer?.Dispose();
            _drawer = null;
        }

        public void SetMode(VehicleDrawMode m)
        {
            mode = m;
            if (isActiveAndEnabled) CreateDrawer();
        }

        private void CreateDrawer()
        {
            _drawer?.Dispose();
            _drawer = null;
            if (models == null || material == null || models.entries.Length == 0) return;
            _batcher = new VehicleBatcher(models) { LodDistance = lodDistance };
            _drawer = mode == VehicleDrawMode.BatchRendererGroup
                ? new BrgDrawer(material, models)
                : new InstancedMeshDrawer(material);
        }

        private void LateUpdate()
        {
            if (_drawer == null || _batcher == null || host == null || !host.IsInitialized) return;
            if (_camera == null) _camera = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();
            long a0 = System.GC.GetAllocatedBytesForCurrentThread();
            long t0 = Stopwatch.GetTimestamp();
            SnapshotInterpolator v = host.Interpolate();
            _batcher.LodDistance = lodDistance;
            _batcher.Build(v.Output, v.Count, _camera);
            LastDrawCalls = _drawer.Draw(_batcher, models!);
            LastCpuMs = (float)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            LastAllocBytes = System.GC.GetAllocatedBytesForCurrentThread() - a0;
            LastVisible = _batcher.Visible;
            LastCulled = _batcher.Culled;
        }
    }
}
