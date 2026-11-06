using UnityEngine;
using Urbe.Sim.City;

namespace Urbe.Game.Sim
{
    /// <summary>
    /// Arranca la partida en el SimHost de la escena Main. M3-07: por defecto es una ciudad nueva (mapa vacío de 2 x 2 km con la autopista
    /// de entrada y el dinero inicial de <see cref="CityBalance"/>). El escenario de rejilla con tráfico (M2) queda como demostración opcional.
    /// </summary>
    [DefaultExecutionOrder(-90)]
    public sealed class SimBootstrap : MonoBehaviour
    {
        [SerializeField] private SimHost host = null!;
        [SerializeField] private ulong seed = 12345UL;
        [SerializeField, Tooltip("Arranca la rejilla de demostración de M2 en vez de una ciudad vacía.")] private bool demoGrid;
        [SerializeField] private int maxVehicles = 300;
        [SerializeField] private float tripsPerSecond = 4f;
        [SerializeField] private int cells = 8;
        [SerializeField] private float spacing = 100f;

        public SimHost Host => host;

        private void Start()
        {
            if (host == null) host = FindFirstObjectByType<SimHost>();
            if (demoGrid) host.Initialize(GridScenario.Create(seed, maxVehicles, tripsPerSecond, cells, spacing));
            else host.Initialize(new CitySimulation(seed));
        }
    }
}
