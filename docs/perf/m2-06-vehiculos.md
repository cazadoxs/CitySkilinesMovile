# Render instanciado de vehículos: medición en PC (M2-06, 2026-11-05)

**Estado:** medido SOLO en PC. La elección entre `RenderMeshInstanced` y `BatchRendererGroup` es **provisional** hasta medir en el móvil de Álvaro (en esta sesión no había ninguno conectado).

## Método
- Unity 6000.1.4f1, Editor en batchmode con GPU (sin `-nographics`), escena `Main` real, URP móvil. Prueba: `VehicleRenderTests.Tres_Mil_Vehiculos_MenosDeDiezDrawCalls_SinAsignaciones_AmbosCaminos`.
- Escena: cuadrícula 25x25 de 100 m (2.400 x 2.400 m), 3.000 vehículos pedidos y ~2.990 vivos tras 900 ticks; cámara cenital a 2.600 m con toda la ciudad a la vista (2.644 vehículos pasan el descarte por frustum; el resto cae en el borde).
- 200 fotogramas medidos tras 30 de calentamiento. **CPU del renderer** = reunir los vehículos (frustum, LOD, matrices) + enviarlos a la GPU, medido con `Stopwatch` dentro de `VehicleRenderer.LateUpdate`. **No incluye** el tiempo de GPU ni la parte de render del hilo de render de Unity.
- Dos variantes: LOD por distancia normal (con la cámara tan alta casi todo es LOD1) y **todo forzado a LOD0** (peor caso de geometría).
- Asignaciones: `GC.GetAllocatedBytesForCurrentThread()` antes y después de la llamada, máximo de los 200 fotogramas.

## Resultados (PC de Álvaro, Editor)
| Camino | Variante | Visibles | Draw calls de vehículos | CPU media | CPU p95 | Bytes gestionados/fotograma |
|---|---|---|---|---|---|---|
| RenderMeshInstanced | LOD normal | 2.644 | 5 | 0,346 ms | 0,353 ms | 0 |
| RenderMeshInstanced | todo LOD0 | 2.644 | 5 | 0,352 ms | 0,356 ms | 0 |
| BatchRendererGroup | LOD normal | 2.644 | 4 | 0,387 ms | 0,413 ms | 0 |
| BatchRendererGroup | todo LOD0 | 2.644 | 4 | 0,413 ms | 0,601 ms | 0 |

(Otra ejecución dio 0,35 / 0,35 y 0,41 / 0,41 ms: la variación entre ejecuciones es de ~10 %.)

- **Draw calls < 10: cumplido.** El peor caso de variedad son 4 modelos (3 coches + camión) x 2 LOD = 8 mallas distintas, así que nunca pasa de 8 más los troceos de 1.023 instancias de `RenderMeshInstanced` (hoy el sedán, con ~1.190, se parte en 2: de ahí 5 frente a 4). El fotograma completo (carreteras en chunks, suelo, HUD) da **13** draw calls según `UnityStats.drawCalls`; la cifra de la tarjeta se refiere a los vehículos.
- **Asignaciones por fotograma: 0** en ambos caminos (BRG reserva memoria nativa para sus comandos de dibujo, no gestionada).
- Verificación visual: las capturas de ambos caminos son idénticas a ojo (cerca y a media distancia), con tinte por instancia correcto.

## Elección provisional: RenderMeshInstanced
Motivos (solo PC): ~15 % menos de CPU que BRG, sin `unsafe` ni shader DOTS, un único camino de shader y menos riesgo en GLES/Vulkan. BRG sigue implementado y se activa con `VehicleRenderer.SetMode(VehicleDrawMode.BatchRendererGroup)`.

## Pendiente (marcado, sin cifras inventadas)
- Repetir esta misma prueba en el móvil de Álvaro (CPU y, sobre todo, GPU y fps con el panel de depuración). BRG podría ganar ahí por evitar el coste por llamada y el límite de 1.023 instancias; si es así, se cambia el modo por defecto.
- Coste de GPU con muchos vehículos en LOD0 (cámara cerca de un atasco) y en una GPU móvil.
- Sombras de vehículos (desactivadas) y faros/luces (fuera de alcance de la tarjeta).
