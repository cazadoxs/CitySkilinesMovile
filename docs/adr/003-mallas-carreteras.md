# ADR 003 — Mallas de carreteras e intersecciones

Estado: aceptado · Tarjeta: M2-04a · Implementa: M2-04b · Usan: M2-05, M2-09 · GDD §5.3, §8.4, §8.15

## Contexto

La red viaria vive en `Sim.Core` y no sabe de Unity. Hay que convertirla en mallas para móvil: curvas libres, cruces en T, X e Y sin huecos,
regeneración local en menos de 5 ms y pocos draw calls con redes de hasta unos 20.000 segmentos. Los datos ya existen: `Segment.Curve`,
`TLaneA/TLaneB` y `SetbackA/SetbackB` (retranqueo), `GetNodeSegments` (ordenados CCW) y `Subscribe/ConsumeChanges` para saber qué cambió.

## Decisión

### Capas
- `Rendering/Roads/RoadGeometry` y `IntersectionGeometry`: C# puro, sin `Mesh` ni `GameObject`. Escriben en buffers reutilizables y se testean en EditMode.
- `Rendering/Roads/RoadChunkRenderer`: el lado Unity. Mantiene chunks y sube mallas.
- La entrada del constructor es un struct `RoadPiece { Bezier3 Curve; float T0, T1; ushort RoadTypeId; float ElevA, ElevB; int SegmentIndex; }`, **no** un id de la red.
  Así la herramienta de carreteras (M2-05) pinta el fantasma con el mismo código sin tocar la simulación.

### Segmentos: perfil extruido
- Se extruye una sección transversal a lo largo de la curva central entre `T0 = TLaneA` y `T1 = TLaneB`.
- Sección: acera (cara superior +0,15 m), bordillo vertical, calzada, bordillo, acera. La mediana con ancho > 0 se añade como isleta elevada. Medidas de `RoadType`.
- Muestreo adaptativo por arco: una fila nueva cuando el ángulo acumulado supera 8° o la distancia supera 40 m. Mínimo 2 filas.
- La posición lateral sale de la normal de la curva, igual que `Lane.Offset` (+ = derecha de A hacia B), para que los coches queden centrados en sus carriles.
- Elevación lineal entre nodos. Puentes y túneles quedan fuera de M2, pero el struct ya lleva las dos elevaciones.

### Sin grietas: una única función de sección
- `CrossSectionAt(piece, t, buffer)` es la **única** forma de obtener puntos de una sección. El segmento y el cruce la llaman con el mismo `t`,
  así que los vértices compartidos son idénticos bit a bit. Esa es la garantía contra huecos, no una tolerancia.

### Intersecciones
Según `Node.Kind`:
- `Continuation`: si los dos segmentos tienen la misma sección y tangente continua, no hay malla de nodo. Si cambia el ancho, una tira de transición entre las dos secciones.
- `DeadEnd`: tapa semicircular de la calzada con acera alrededor (6 sectores).
- `Junction`, en el orden CCW de `GetNodeSegments`:
  1. Sección final de cada segmento: borde izquierdo y derecho de calzada y de acera, en su `TLane`.
  2. Entre cada segmento `i` y el siguiente, una esquina: Bézier cuadrática del borde derecho de `i` al izquierdo de `i+1`, con control en el corte de las dos rectas de borde. Si son casi paralelas (`|cross| < 0,05`), control en el punto medio. 4 tramos por esquina, tanto en el borde de calzada como en el de acera.
  3. Calzada: abanico desde el centroide del contorno cerrado. Si algún triángulo sale invertido (contorno no estrellado, ángulos muy agudos), se triangula por recorte de orejas. Este caso se testea.
  4. Acera: tira entre el contorno de calzada y el de acera solo en las esquinas. Las bocas quedan abiertas hacia su segmento.
- La calzada del cruce usa la zona lisa del atlas: sin marcas. Las líneas de detención van en un quad a +0,005 m, al final de cada segmento de entrada con el cruce.

### Materiales y atlas
- Un solo material URP Unlit con iluminación simple en shader propio (o Simple Lit si el coste lo permite). Atlas generado por script de Editor, sin assets externos.
- Atlas en **columnas**: una por tipo de carretera con su sección pintada a lo ancho, más una de acera y una de asfalto liso. `u` cruza la sección; `v` es distancia en metros / 12, y envuelve en toda la textura, de modo que las marcas discontinuas se repiten sin costuras. Margen de 4 px entre columnas por los mipmaps.

### Chunks y actualización
- Rejilla de 128 m. Un segmento pertenece al chunk de su punto medio; un nodo, al de su posición. Las mallas pueden sobresalir: el `bounds` se calcula de lo que contiene.
- Un `Mesh` por chunk, mismo material: el SRP Batcher y el culling por chunk mantienen los draw calls en los chunks visibles.
- Caché de geometría por pieza (segmento o nodo). Reconstruir un chunk es copiar sus piezas, sin recalcular las que no cambiaron.
- Suscripción con `Subscribe()` y `ConsumeChanges`. Un segmento sucio ensucia sus dos nodos, y un nodo sucio ensucia sus segmentos incidentes, porque su retranqueo pudo cambiar.
- Cola de piezas sucias, procesada en hilo principal con presupuesto de 4 ms por frame medido con `Stopwatch`. Una edición normal termina en el mismo frame.
- Vértice compacto con `SetVertexBufferParams`: posición float3, normal SNorm8×4, uv0 float2, uv1 float (índice de segmento). Índices de 32 bits solo si el chunk supera 65.535 vértices. Sin asignaciones al rebuild salvo crecimiento de buffers.

### Capa de tráfico (M2-09)
- `uv1` lleva `SegmentIndex` (−1 en cruces, que usan el color neutro). El shader lee una textura de datos de 256×256 RGBA32 indexada por segmento y mezcla ese color cuando la capa está activa.
- La capa se actualiza como mucho a 2 Hz escribiendo una textura. No regenera mallas.

## Alternativas descartadas
- **Un GameObject por segmento:** demasiados objetos y draw calls en móvil.
- **Calcomanías para las marcas viales:** sobredibujo caro en GPU móvil y problemas de z-fighting.
- **Booleanas o CSG para los cruces:** frágiles y lentas; aquí los contornos se conocen de antemano.
- **Generar en Jobs/Burst desde el principio:** complica sin datos que lo pidan. Si la medición lo exige, los buffers ya son planos y se puede portar.

## Consecuencias
- El colisionado no existe: se selecciona con el índice espacial de la red y un plano, no con física.
- Sin LOD en M2. Con 8 vértices por fila, una red grande se acerca al millón de vértices antes de culling. M6 reducirá filas a distancia si la medición en móvil lo pide.
- Los ángulos muy agudos pueden dar esquinas feas: la validación de M2-05 debe rechazarlos con mensaje antes que parchearlos en la malla.

## Cambios durante la implementación (M2-04b)

- **Ruta:** el código vive en `unity/URBE/Assets/_Project/Scripts/Rendering/Roads/` (GDD 19.3), no en `Assets/Scripts/`.
- **`RoadPiece` lleva además** `Flags` (cierre de mediana y línea de detención por extremo) y `MitreStart/MitreEnd`. La sección es un `Span<Vector3>` de 10 puntos (`CrossSectionAt`) en vez de un buffer genérico.
- **Continuaciones con quiebro: inglete, no malla de nodo.** Entre dos segmentos del mismo tipo con giro de hasta 100° el corte de cada extremo se desplaza `lateral × tan(φ/2)` a lo largo de la tangente y los dos coinciden en la bisectriz: sin hueco ni solape y sin malla. Con otro tipo, giro mayor o segmentos demasiado cortos se usa el nodo genérico (puede solapar en el interior del codo). Motivo: un giro de 90° en dos segmentos es lo más habitual al dibujar y el nodo genérico solapaba.
- **Vértices por fila:** unos 18 a 22 (tira con vértices propios por cara para normales duras del bordillo), no 8. Una red de 20.200 segmentos da 1,75 M de vértices (46 MB, 1,2 M de triángulos) antes de culling: M10/M6 deberán reducir (LOD o fusionar vértices).
- **Línea de detención** a +0,02 m (no 0,005): con 700 m de altura de cámara 5 mm provoca z-fighting.
- **`normal.w`** lleva la máscara de calzada (127) que usa la capa de tráfico para teñir solo el asfalto.
- **Ángulos agudos:** el retranqueo del núcleo (`semiancho + 1`) no depende del ángulo, así que dos brazos a menos de unos 55° (calles) o 67° (avenidas) se solapan más allá del retranqueo. La validación de M2-05 lo rechaza (ver tarjeta M2-05).
- **Atlas:** 2048×512 (columnas de 224 px por tipo y de 128 px lisas), sin compresión por ahora (4 MB con mips); pendiente comprimir en móvil.
