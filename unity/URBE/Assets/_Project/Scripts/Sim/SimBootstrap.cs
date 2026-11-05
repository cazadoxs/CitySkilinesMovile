using UnityEngine;

namespace Urbe.Game.Sim
{
    /// <summary>Arranca el escenario Grid en el SimHost de la escena Main (M2-02). Los escenarios reales llegarán con el guardado.</summary>
    [DefaultExecutionOrder(-90)]
    public sealed class SimBootstrap : MonoBehaviour
    {
        [SerializeField] private SimHost host = null!;
        [SerializeField] private ulong seed = 12345UL;
        [SerializeField] private int maxVehicles = 300;
        [SerializeField] private float tripsPerSecond = 4f;
        [SerializeField] private int cells = 8;
        [SerializeField] private float spacing = 100f;

        public SimHost Host => host;

        private void Start()
        {
            if (host == null) host = FindFirstObjectByType<SimHost>();
            host.Initialize(GridScenario.Create(seed, maxVehicles, tripsPerSecond, cells, spacing));
        }
    }
}
