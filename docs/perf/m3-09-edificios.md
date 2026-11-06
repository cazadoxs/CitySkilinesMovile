# M3-09 · Render de edificios: medición con 5.000 edificios

Generado por `BuildingPerfTests` (PlayMode, editor de Unity 6000.1.4f1, batchmode con gráficos, PC). 5.000 instancias de prueba repartidas por el mapa de 2 x 2 km,
con el catálogo real (105 modelos distintos usados de 115, zonas, niveles y variantes mezclados, un 6 % abandonados) en 256 chunks de 128 m.
Render: `BuildingChunkRenderer` (RenderMeshInstanced, un cubo por modelo y LOD, descarte por frustum a nivel de chunk, LOD1 a más de 260 m, caja con tinte a más de 700 m).

| Vista | Edificios dibujados | LOD0 | Cajas lejanas | Chunks visibles | Draw calls de edificios | Draw calls del fotograma | CPU media (ms) | CPU p95 (ms) | Bytes gestionados/fotograma |
|---|---|---|---|---|---|---|---|---|---|
| vista general (todo el mapa) | 5000 | 0 | 5000 | 256 | 5 | n/d | 0,273 | 0,283 | 0 |
| vista media (700 m) | 1406 | 0 | 1045 | 64 | 98 | n/d | 0,531 | 0,564 | 0 |
| vista de calle (150 m) | 220 | 110 | 0 | 10 | 132 | n/d | 0,635 | 0,667 | 0 |

Los draw calls del fotograma (Profiler/UnityStats) incluyen suelo, carreteras, interfaz y edificios; en batchmode no siempre están disponibles (n/d). La CPU es solo la del renderer (reunir cubos y enviar instancias); la GPU no se mide en batchmode.
No medido: dispositivo móvil (pendiente de una pasada en el móvil, igual que M2-06).
