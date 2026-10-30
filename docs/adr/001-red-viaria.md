# ADR 001 — Red viaria: nodos, segmentos, carriles y conexiones

Estado: aceptado · Tarjeta: M1-03a · Implementa: M1-03b · Usan: M1-04, M1-05, M1-06b, M1-07 · GDD §4.2-4.3, §5.3, §5.14, §8.4, §8.14

## Contexto

La red viaria es la base de pathfinding, vehículos, intersecciones y comandos con deshacer. Debe vivir en `Sim.Core` (sin Unity),
ser determinista, no asignar memoria en bucles calientes y aguantar redes de ~20.000 segmentos. El GDD §8.4 fija la forma
general (Node/Segment/Lane/LaneConn, época, índice espacial); este ADR fija tipos, reglas y API para que M1-03b los implemente sin ambigüedad.

## Decisión

### Convenciones
- Unidades: metros, segundos, m/s (los km/h solo en datos; se convierten al cargar). Plano XZ, Y arriba (elevación aparte, `float`).
- `Urbe.Sim.Geometry.Vec2 { float X; float Z; }` (readonly struct propio) y `Bezier3 { Vec2 P0, P1, P2, P3; }` + tabla de longitud por arco de M1-02. Si M1-02 usó otros nombres, prevalecen los suyos.
- **Circulación por la derecha.** Derecha de una dirección `d`: `Right(d) = (d.Z, -d.X)`. `Cross(a,b) = a.X*b.Z - a.Z*b.X`.
  Ángulo de giro `θ = atan2(Cross(din, dout), Dot(din, dout))`: **θ < 0 = derecha, θ > 0 = izquierda.**
- Todo en el namespace `Urbe.Sim.Roads`. Ids con `EntityId` + `IdAllocator` (uno para nodos y otro para segmentos).

### Tipos de carretera (datos)
`sealed class RoadType` (inmutable, cargada de JSON más adelante; en M1, `RoadTypeCatalog.CreateDefault()`):
`ushort Id; string Key; byte LanesForward; byte LanesBackward` (0 = sentido único); `float LaneWidth, MedianWidth, MarginWidth`
(acera o arcén, por lado); `float SpeedLimit` (m/s); `float MaxSlope; bool Zonable`. `TotalWidth = 2·Margin + Median + (F+B)·LaneWidth`.

| Id | Key | F+B | Carril | Mediana | Margen | km/h |
|---|---|---|---|---|---|---|
| 0 | `street_2` | 1+1 | 3,0 | 0 | 2,0 | 40 |
| 1 | `oneway_2` | 2+0 | 3,0 | 0 | 2,0 | 40 |
| 2 | `avenue_4` | 2+2 | 3,25 | 1,0 | 2,5 | 50 |
| 3 | `avenue_6` | 3+3 | 3,5 | 2,0 | 3,0 | 60 |
| 4 | `rural_2` | 1+1 | 3,5 | 0 | 1,5 | 80 |
| 5 | `highway_4` | 2+2 | 3,75 | 3,0 | 2,5 | 100 |

Invertir un sentido único = intercambiar `NodeA/NodeB` e invertir la curva (no hay flag de sentido).

### Estructuras (arrays de structs, índice = `EntityId.Index`)
```
struct Node    { Vec2 Position; float Elevation; byte SegmentCount; JunctionKind Kind;   // None, DeadEnd, Continuation, Junction
                 ushort ControlId /*M1-07*/; int ConnFirst; ushort ConnCount; ushort ConnCapacity; }
// Adyacencia: EntityId[] _nodeSegments, stride MaxSegmentsPerNode = 8, ordenados por ángulo CCW (desde +X) de la tangente que sale del nodo.
struct Segment { EntityId NodeA, NodeB; Bezier3 Curve; float Length; ushort RoadTypeId;
                 byte LanesForward, LanesBackward; SegmentFlags Flags; int ArcTableOffset; }
readonly struct LaneId { int Value; }   // Value = segmentIndex * 8 + slot. Sin allocator propio.
struct Lane    { EntityId Segment; byte Slot; LaneDirection Direction /*Forward A→B, Backward B→A*/; LaneKind Kind /*Car en M1*/;
                 float Offset /*lateral, + = derecha de A→B*/; float Length; float SpeedLimit; int ConnFirst; ushort ConnCount; }
struct LaneConnection { LaneId From, To; EntityId Node; Bezier3 Curve; float Length; float SpeedLimit; TurnType Turn; byte LocalIndex; }
enum TurnType : byte { Straight, Right, Left, UTurn }
```
- `Lane[]` tiene capacidad `segmentCapacity * 8` (`MaxLanesPerSegment = 8`); un hueco libre tiene `Segment = None`.
- Los carriles **no** guardan listas de vehículos: las cabezas/colas viven en el sistema de vehículos (ADR 002), así la red no tiene estado dinámico.
- `LaneConnection[]` global: cada nodo tiene un bloque `[ConnFirst, ConnFirst+ConnCapacity)`; dentro, ordenado por `(From.Value, To.Value)`,
  así cada carril tiene su rango contiguo (`Lane.ConnFirst/ConnCount`, salidas en el nodo final del carril). Al reconstruir un nodo, si cabe se reusa
  el bloque; si no, se añade al final y el viejo queda como hueco; se compacta cuando los huecos superan el 50 % (solo en ediciones).
  Los índices de conexión **solo son válidos dentro de una época**. `MaxConnectionsPerNode = 64` (`LocalIndex` cabe en una máscara `ulong` para M1-07).

### Generación de carriles de un segmento
Slots `0..F-1` = Forward, `F..F+B-1` = Backward; dentro de cada sentido, **índice 0 = carril derecho (bordillo)**. Desplazamiento lateral:
- Doble sentido: `Forward k: +(Median/2 + (F-k-0.5)·W)`; `Backward k: -(Median/2 + (B-k-0.5)·W)`.
- Sentido único: `k: ((F-1)/2 - k)·W`.
`Lane.Length` = longitud de la polilínea desplazada (16 muestras de la tabla de arco). Posición en carril `s ∈ [0, Length]` → `u = s·Seg.Length/Lane.Length`
(Backward: `u = Seg.Length - …` y tangente negada), punto = `Curve(u) + Right(tangenteA→B)·Offset`. `SpeedLimit` = el del tipo.

### Generación de conexiones en un nodo (se reconstruye entero cada vez que cambia el nodo)
1. **Entradas** = carriles que terminan en el nodo; **salidas** = segmentos con ≥1 carril que sale del nodo. `din` = tangente de llegada, `dout` = tangente de salida.
2. **Clase de cada salida** respecto a cada segmento de entrada (la propia vía no es salida salvo fin de vía):
   `|θ| ≤ 30°` Recto · `-150° ≤ θ < -30°` Derecha · `30° < θ ≤ 150°` Izquierda · `|θ| > 150°` cambio de sentido (no se genera en cruces).
   Solo hay **una** salida Recta: la de menor `|θ|`; las demás se reclasifican por signo. Si el nodo tiene 2 segmentos (`Continuation`), la otra es siempre Recta.
3. **Qué carriles de entrada sirven a cada clase** (n carriles de entrada, 0 = derecho):
   n = 1 → todas las clases. n ≥ 2 con Recto: carril 0 → Derecha+Recto, carril n-1 → Izquierda+Recto, intermedios → Recto.
   Sin Recto (tallo de una T): carriles `0..⌈n/2⌉-1` → Derecha, `⌊n/2⌋..n-1` → Izquierda. Una clase ausente simplemente desaparece; si solo existe
   una clase, todos los carriles la sirven. Varias salidas de la misma clase (nodos de 5+) comparten el mismo conjunto de carriles.
4. **Carril a carril** (conjunto de entrada de tamaño a, salida con m carriles): Recto usa los m carriles; Derecha usa los `m' = min(a,m)` carriles de la
   derecha (`0..m'-1`); Izquierda los `m'` de la izquierda (`m-m'..m-1`). Fórmula única con k = 0..a-1: si `m' ≥ a` → `k` conecta con
   `[⌊k·m'/a⌋, ⌊(k+1)·m'/a⌋-1]`; si `m' < a` → con `⌊k·m'/a⌋` (más offset del lado). Cubre 2+2→1+1, 1+1→2+2 y 3→2.
5. **Fin de vía** (`DeadEnd`, 1 segmento de doble sentido): solo un cambio de sentido del carril de entrada más a la izquierda al carril de salida más a la
   izquierda. Sentido único sin salida: ninguna conexión. Nodo sin segmentos: se elimina.
6. **Geometría**: Bézier de fin de carril a inicio de carril, con asas `0,4·|P3-P0|` a lo largo de las tangentes. `SpeedLimit = min(límites)·f`,
   `f` = 1,0 recto, 0,5 izquierda, 0,35 derecha, 0,25 cambio de sentido, mínimo 4 m/s.
Las prohibiciones y flechas editables (§4.3) se aplican después como filtro en `EditJunction` (fuera de M1).

### Edición
- `SnapRadius = 4 m`, `MinSegmentLength = 8 m`, `MaxSegmentLength = 200 m`, ángulo mínimo en cruce 25° (lo valida M1-04 con `CanAddSegment`).
- **Snap**: un extremo a ≤ 4 m de un nodo usa ese nodo (los puntos de control se trasladan con el extremo). Si no, a ≤ 4 m del interior de un segmento → `SplitSegment` en el `t` más cercano.
- **Cruces**: se calculan las intersecciones de la nueva curva con los segmentos del índice espacial (M1-02), solo si `|ΔElevación| < 2 m` en el punto.
  Se ordenan por `t` de la nueva curva; cada una divide el segmento existente y la nueva. Si un corte dejaría un trozo < `MinSegmentLength`, se usa el nodo
  existente más próximo en lugar de crear uno.
- **Split en t**: De Casteljau (mitades exactas); nuevo nodo `Continuation` en `P(t)`; ambas mitades heredan tipo y flags; el segmento original se libera.
- Rechazo: segmento duplicado entre los mismos dos nodos, nodo con más de 8 segmentos, más de 64 conexiones en un nodo.
- `RemoveSegment` elimina nodos huérfanos. **No** se fusionan automáticamente nodos `Continuation` (deshacer más simple).

### Época
`ulong Epoch` sube **una vez por llamada pública mutadora** (no por operación interna). Se acumulan `ChangedNodes` y `ChangedSegments` (buffers
reutilizables, ordenados por índice) hasta `ConsumeChanges()`. Invalida: caché de rutas y *landmarks* afectados (M1-05), mallas de los segmentos y nodos
cambiados (render), índices de conexión (ADR 002) y rutas que contengan segmentos liberados (se detecta por generación del `EntityId`).

### Índice espacial
Rejilla uniforme de **32 m** (GDD §5.14) sobre `NetworkConfig.WorldMin/WorldSize` (8 km × 8 km por defecto → 250×250 celdas): `int[] _cellHead` +
pool `GridEntry { int Segment; int Next; }` con lista libre. Un segmento se registra en las celdas de las cajas de sus 16 tramos de polilínea infladas con
`TotalWidth/2`. Las consultas deduplican con un array de sellos por segmento y devuelven resultados **ordenados por índice**.

### API pública mínima (`public sealed class RoadNetwork`)
```
RoadNetwork(RoadTypeCatalog types, NetworkConfig config);   ulong Epoch { get; }   int NodeCount, SegmentCount { get; }
// Mutaciones de alto nivel (snap + cruces). Registran lo hecho en 'edit' (buffer reutilizable) para que M1-04 construya el inverso.
ValidationResult CanAddSegment(in SegmentRequest req);           // req: Vec2 Start, C1, C2, End; float ElevStart, ElevEnd; ushort RoadTypeId
bool AddSegment(in SegmentRequest req, NetworkEdit edit);
bool SplitSegment(EntityId seg, float t, NetworkEdit edit);      // edit recibe nodo y las dos mitades
bool RemoveSegment(EntityId seg, NetworkEdit edit);
// Primitivas exactas, sin snap ni cruces (las usa el deshacer):
EntityId AddNodeRaw(Vec2 pos, float elevation);  EntityId AddSegmentRaw(EntityId a, EntityId b, in Bezier3 curve, ushort roadTypeId);
EntityId FindNodeAt(Vec2 pos);                   // igualdad exacta de bits
// Consultas
bool IsAlive(EntityId node|seg);  ref readonly Node GetNode(EntityId);  ref readonly Segment GetSegment(EntityId);  ref readonly Lane GetLane(LaneId);
ReadOnlySpan<EntityId> GetNodeSegments(EntityId node);  int GetLaneCount(EntityId seg);  LaneId GetLane(EntityId seg, int slot);
ReadOnlySpan<LaneConnection> GetConnectionsFrom(LaneId lane);  ReadOnlySpan<LaneConnection> GetConnectionsAt(EntityId node);
ref readonly LaneConnection GetConnection(int index);
void SampleLane(LaneId lane, float s, out Vec2 pos, out Vec2 tangent);  void SampleConnection(int index, float s, out Vec2 pos, out Vec2 tangent);
int QuerySegments(in Aabb2 box, Span<EntityId> results);
bool NearestSegment(Vec2 p, float maxDist, out EntityId seg, out float t, out float dist);  EntityId NearestNode(Vec2 p, float radius);
ulong ComputeHash();  void ConsumeChanges(out ReadOnlySpan<EntityId> nodes, out ReadOnlySpan<EntityId> segments);
```
`NetworkEdit` guarda: nodos y segmentos creados, y `SegmentRecord { Vec2 PosA, PosB; float ElevA, ElevB; Bezier3 Curve; ushort RoadTypeId }`
de los eliminados. Inverso de M1-04: `RemoveSegment` de los creados, después `AddSegmentRaw` de los registros (nodos con `FindNodeAt` o `AddNodeRaw`).

**Hash canónico**: suma módulo 2⁶⁴ de `SplitMix64(bits de los campos)` de cada nodo vivo (posición, elevación) y cada segmento vivo (curva, tipo).
Es conmutativo, así que no depende de índices ni generaciones: construir → deshacer da el mismo hash aunque cambien los ids. Carriles y conexiones son
derivados y no entran; la época tampoco.

### Tests obligatorios de M1-03b
1. Recta entre dos puntos libres: 2 nodos `DeadEnd`, 1 segmento, nº de carriles, offsets y longitudes por tipo (tabla anterior).
2. Snap a nodo a 3,9 m sí, a 4,1 m no. Extremo sobre el interior de un segmento → split y T (nodo con 3 segmentos).
3. Cruce de dos rectas → X: 4 segmentos, 1 nodo `Junction`; tres cruces sobre la nueva curva se procesan en orden de `t`.
4. Cruce a < 8 m de un nodo existente reutiliza el nodo (sin segmentos diminutos).
5. `SplitSegment`: continuidad de posición (< 1e-4 m) y de tangente, suma de longitudes con error < 0,1 %.
6. T con 1+1: tallo → 1 Derecha + 1 Izquierda; vía principal → Recto + giro. Conteo exacto de conexiones y `TurnType`.
7. X con 1+1: 12 conexiones (3 por entrada), ningún cambio de sentido.
8. X 2+2 con 2+2: carril 0 → Derecha (a carril 0) + Recto (a 0); carril 1 → Recto (a 1) + Izquierda (a carril 1).
9. Continuación 2+2 → 1+1 y 1+1 → 2+2 con la fórmula del paso 4; sentido único contra dirección no genera conexiones.
10. Fin de vía: un cambio de sentido en doble sentido, ninguno en sentido único.
11. `RemoveSegment`: nodos huérfanos eliminados, T vuelve a `Continuation`, ids viejos `IsAlive == false`.
12. Época +1 exacto por llamada pública; `ConsumeChanges` lista los nodos y segmentos tocados.
13. Índice espacial frente a fuerza bruta con 500 segmentos aleatorios (Pcg32): mismos resultados, ordenados.
14. Hash: añadir con cruce → deshacer con `NetworkEdit` → hash idéntico al original.
15. Más de 8 segmentos en un nodo y segmento duplicado se rechazan. Misma secuencia de llamadas → mismos ids y mismo hash.

## Alternativas descartadas
- Quadtree: rebalanceo y orden de recorrido más complejos; la densidad viaria es casi uniforme y el GDD fija una rejilla de 32 m.
- `Dictionary` celda → lista (spatial hash): orden de iteración no garantizado; la rejilla densa cabe en memoria (62.500 `int`).
- Allocator propio para carriles: innecesario; `LaneId` derivado del segmento es estable y gratis.
- Conexiones con id estable entre épocas: complica la reconstrucción; los vehículos remapean por `(From, To)` al cambiar la época.
- Hash en orden de índice: rompería "construir → deshacer = mismo hash" porque el deshacer no recupera los mismos índices.
- Fusión automática de nodos de continuación: ahorra segmentos pero complica el inverso de los comandos.

## Consecuencias
- Memoria: `Lane` ~40 B × 8 por segmento (≈ 6,4 MB a 20.000 segmentos); aceptable, y a cambio no hay fragmentación de carriles.
- Las reglas de giro son deterministas y sin estado: cualquier nodo se puede reconstruir en cualquier momento con el mismo resultado.
- M1-07 añade `ControlId`, máscaras de conflicto por `LocalIndex` y filtros de giro sin cambiar las estructuras.
- Elevación: solo filtra cruces; puentes y túneles (rampas, pendiente) llegan con la herramienta de carreteras de M2.

## Cambios durante la implementación (M1-07b: retranqueo de carriles)
- Los carriles ya no llegan al punto del nodo en los nodos `Junction` (≥ 3 segmentos). Cada extremo se recorta `setback = máx(TotalWidth/2 de los otros segmentos del nodo) + 1 m` (mínimo 4 m;
  0 en `DeadEnd`/`Continuation`), acotado por extremo a `Length/2 − 1,5 m`. Calle × calle: 6 m (área de ~12 m); calle con avenida de 6 carriles: 15,5 m.
- `Segment` guarda `SetbackA/B` y `TLaneA/B` (t de la curva donde empiezan los carriles). Son derivados: no entran en el hash (`ComputeHash` no cambia) ni en `NetworkEdit`.
  Se recalculan en `InsertSegment`/`DetachSegment` para todos los segmentos de los nodos afectados (por lo que un split que convierte un `Continuation` en `Junction` recorta
  a sus vecinos y quitar el tercer segmento los restituye); los que cambian se marcan con `TouchSegment` y salen en `ConsumeChanges`.
- `Lane.Length` es la longitud del tramo retranqueado y s = 0 es su inicio retranqueado; `SampleLane` mapea s → distancia sobre la curva central de forma proporcional dentro de
  `[SetbackA, Length − SetbackB]` (invertido en carriles `Backward`). Con retranqueo 0 el resultado es idéntico al anterior.
- Las conexiones van del extremo retranqueado del carril de entrada al del de salida (`LaneEndpoint` usa `TLaneA/B`): cruzan el área real del cruce (rectas ≥ 12 m en una X de calles
  1+1; antes ≈ 0). Efecto en ADR 004: las derechas opuestas y el recto que no cruza la derecha de la secundaria dejan de estar en conflicto; las zonas se recalculan solas al regenerar el nodo.
- Un carril de salida o de entrada es ~retranqueo más corto que su brazo: el código que fije posiciones por `Length` del segmento (tests, generadores) debe usar `Lane.Length`.
