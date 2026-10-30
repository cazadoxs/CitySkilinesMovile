# ADR 002 — Vehículos: IDM, líder, transiciones y snapshot

Estado: aceptado · Tarjeta: M1-06a · Implementa: M1-06b · Extiende: M1-07 (intersecciones), M1-08 (carriles) · GDD §8.1, §8.6-8.7, §8.14-8.15 · Depende de ADR 001

## Contexto

El sistema 5 del tick (§8.1) mueve todos los vehículos físicos a 10 Hz: 16.000 en PC, 3.000 en gama media dentro de ≤ 8 ms/fotograma de
simulación total. Debe ser determinista (mismo dispositivo y build), no asignar memoria por tick y dejar un punto de enganche limpio para que
M1-07 resuelva conflictos, reservas, anti-bloqueo y semáforos sin reescribir el movimiento.

## Decisión

### Datos (namespace `Urbe.Sim.Vehicles`, clase `VehicleSystem`)
Un vehículo está siempre sobre una **pista**: un carril (`LaneId`) o una conexión de intersección (índice de `LaneConnection`, válido en la época actual).
```
struct Vehicle {                         // ~48 B; Vehicle[] contiguo indexado por EntityId.Index (IdAllocator propio)
  float S;            // m recorridos sobre la pista actual, 0..Length
  float V;            // m/s, siempre ≥ 0
  float A;            // última aceleración (debug, estadísticas)
  int   Track;        // LaneId.Value o índice de conexión
  int   Ahead, Behind;// índices del vehículo de delante/detrás en la MISMA pista (-1 = ninguno)
  int   RouteOffset;  ushort RouteLength; ushort RouteCursor;   // ruta en RoutePool; cursor = paso (carril) actual
  float TargetS;      // posición de llegada en el último carril
  float SpeedFactor;  // factor del conductor, Pcg32 en [0,9, 1,1] al nacer (0 si DriverVariation desactivado → 1,0)
  ushort WaitTicks;   // ticks seguidos con V < 0,1 m/s (re-ruta y gridlock, M1-11)
  byte  Type;         // VehicleTypeId
  TrackKind Kind;     // Lane | Connection
  VehicleFlags Flags; // HasEntryPermit, Arrived, NeedsReroute, Frozen (tests)
}
```
Ocupación por pista, fuera de la red (ADR 001): `int[] _laneHead, _laneTail` indexados por `LaneId.Value` y `int[] _connHead, _connTail` por índice de conexión.
**Head = vehículo más adelantado (mayor S), Tail = el último.** `float[] _laneReserved`: longitud reservada por vehículos con permiso que aún no han entrado en ese carril.

### Parámetros IDM por tipo (`VehicleType`, datos)
| Tipo | v0 factor | v_max | a_max | b (cómoda) | T | s0 | longitud |
|---|---|---|---|---|---|---|---|
| Coche (0) | 1,0 | 36 m/s | 1,5 m/s² | 2,0 m/s² | 1,2 s | 2,0 m | 4,5 m |
| Camión (1) | 0,9 | 25 m/s | 0,8 m/s² | 1,5 m/s² | 1,8 s | 3,0 m | 10,0 m |

Comunes: `b_emergency = 9 m/s²` (cota inferior de la aceleración), `δ = 4` (se calcula como `x²·x²`, sin `Math.Pow`), `MinGap = 0,5 m`,
`StopMargin = 1,0 m`, `LookaheadMax = 120 m`, `dt = 0,1 s`.
`v0 = min(Track.SpeedLimit · v0factor · SpeedFactor, v_max)`. **Anticipación**: si la siguiente pista de la ruta tiene límite menor `v0n`, a distancia `d`
del final: `v0 = min(v0, sqrt(v0n² + 2·b·d))`. IDM: `a = a_max·[1 − (v/v0)⁴ − (s*/s)²]`, `s* = s0 + max(0, v·T + v·Δv / (2·sqrt(a_max·b)))`,
con `s = max(gap, 0,1)`; sin líder se omite el término de interacción. Resultado acotado a `[-b_emergency, a_max]`.

### Líder: lista enlazada intrusiva por pista (decisión)
Cada pista mantiene una **lista doblemente enlazada ordenada por S** (`Ahead/Behind` en el struct). El líder es `Ahead`: **O(1), sin ordenar nunca**.
El orden es un invariante, no algo que se recalcula: el clamp de posición impide adelantar dentro de una pista, se entra por la cola (S≈0) y se sale por
la cabeza. Una inserción a mitad (aparición, cambio de carril de M1-08) recorre desde la cola comparando S: O(k) con k pequeño y poco frecuente.
Se descarta el array ordenado reconstruido cada tick: exige un *counting sort* por pista + ordenaciones de 16.000 elementos cada tick (≈ O(N log k))
y búferes extra para lograr lo mismo que la lista da gratis; su ventaja de localidad de caché no compensa con N = 16.000 (≈ 770 KB de `Vehicle`).

### Lookahead (vehículo sin `Ahead`)
Sea `d = Track.Length − S`. En orden, hasta encontrar líder o superar `LookaheadMax`:
1. Fin de ruta en esta pista: líder virtual parado en `TargetS` (gap = `TargetS − S`).
2. Si la pista es un carril y **no** tiene `HasEntryPermit`: líder virtual parado en el final (gap = `d − StopMargin`, Δv = v). Fin.
3. Siguiente pista de la ruta (conexión si estoy en carril; carril si estoy en conexión): si tiene `Tail`, gap = `d + Tail.S − Tail.Longitud`,
   Δv = `V − Tail.V`. Si está vacía, `d += Length` y se repite con la pista siguiente (máximo 2 pistas por delante).
4. Si no hay nada: sin líder (solo término libre).
Los vehículos de **otras** conexiones que confluyen o se cruzan no se ven aquí: es trabajo de M1-07.

### Ruta: `RoutePool`
`struct RouteStep { EntityId Segment; LaneId Lane; }` (12 B) en un único `RouteStep[]`. Bloques por clases de tamaño potencia de dos (8…1024 pasos)
con lista libre por clase; `Allocate(int length) → offset`, `Free(offset, length)`. Solo crece (`Array.Resize`) si se agota, nunca en régimen estable.
Cada paso es un carril de un segmento; la conexión entre `Step[i].Lane` y `Step[i+1].Lane` se busca en `GetConnectionsFrom(Step[i].Lane)` por `To`
(búsqueda lineal, ≤ ~6). Hasta M1-08, M1-06b resuelve los carriles al crear la ruta **de atrás hacia delante**: en el último segmento el carril 0;
en cada paso anterior, el carril de menor slot con conexión al carril ya elegido. M1-08 podrá reescribir `Lane` in situ.

### Orden del tick (sistema 5) — determinista
0. **Época**: si `network.Epoch` cambió: vehículos cuyo segmento actual ya no está vivo → `Arrived` (se eliminan, contador `DespawnedByEdit`); en
   conexión → se remapea por `(From, To)` o se elimina; rutas con segmentos muertos por delante → `NeedsReroute` (M1-05). Se reconstruyen `_conn*` desde cero.
1. **Permisos** (vehículos en orden ascendente de índice): todo cabeza de carril sin permiso con `d ≤ V²/(2·b) + V·dt + StopMargin + 5 m` llama a
   `gate.RequestEntry`. Si concede → `HasEntryPermit` y `_laneReserved[to] += longitud + s0`.
2. **Aceleraciones** (solo lectura del estado viejo): para cada vehículo, líder/lookahead, IDM → `_accel[i]` y `_maxAdvance[i] = gap − MinGap`
   (≥ 0; +∞ sin líder). Fase sin efectos laterales: paralelizable más adelante.
3. **Integración** (por vehículo, sin leer a otros): Euler semi-implícito `V' = max(0, V + a·dt)`; `ds = V'·dt`; si `ds > maxAdvance` →
   `ds = maxAdvance`, `V' = min(V', ds/dt)`. `S += ds`. **Nunca velocidad negativa ni solape**: como el líder solo avanza, usar su posición vieja es conservador.
4. **Transiciones** (índice ascendente): mientras `S > Track.Length`: sacar de la cabeza de la pista, `S −= Length`, pasar a la siguiente pista e
   insertar ordenado desde la cola. Carril → conexión: solo con permiso (`gate.OnEntered`). Conexión → carril: `gate.OnExited`, quitar permiso,
   `_laneReserved[to] −= …`, `RouteCursor++`. Llegada (`S ≥ TargetS` en el último paso) → `Arrived`.
5. **Bajas**: los `Arrived` se sacan de su lista, se libera la ruta y el id (índice ascendente). `WaitTicks` se actualiza aquí.
6. **Snapshot**.

### Enganche para M1-07
```
public interface IJunctionGate {
  bool RequestEntry(in EntryRequest req);  // req: int Vehicle; LaneId From; int Connection; LaneId To; float DistanceToStop; float Speed; float Length
  void OnEntered(int connection, int vehicle);
  void OnExited(int connection, int vehicle);
  void BeginTick(ulong tick);              // fases de semáforo, limpieza de reservas
}
```
M1-06b entrega `GapOnlyGate`: concede si `FreeSpace(To) ≥ Length + s0`, con `FreeSpace = (Tail ? Tail.S − Tail.Longitud : To.Length) − _laneReserved[To]`
(anti-bloqueo básico: no entrar sin hueco a la salida). **M1-07** sustituye la puerta por una con máscaras de conflicto, prioridad, reserva,
semáforos y paso forzado (M1-11), y puede revocar un permiso no usado. Lo que M1-06b **no** hace: conflictos entre conexiones, confluencias, ceda/stop,
semáforos, cambio de carril (M1-08). Por eso sus tests usan redes sin flujos que se crucen.

### Snapshot para el render
`struct VehicleSnapshot { int Id; ushort Generation; byte Type; Vec2 Position; float Elevation; Vec2 Tangent; float Speed; }` (~32 B), en orden de índice.
`SnapshotRing` con **3** búferes preasignados a la capacidad: la simulación escribe en uno y publica con `Interlocked.Exchange` el par
(anterior, actual) con su número de tick; el render interpola por `Id`+`Generation` entre ambos y nunca lee el que se está escribiendo.
Posición con `SampleLane`/`SampleConnection` (ADR 001).

### Hash y rendimiento
`ComputeHash()`: vehículos vivos en orden de índice → bits de `S, V`, `Track`, `Kind`, `RouteCursor`, `Flags`; más el estado del `Pcg32` del sistema.
Presupuesto orientativo de M1-06b: 16.000 vehículos ≤ 2 ms por tick en la máquina de CI y **0 B asignados por tick** tras el calentamiento.

## Alternativas descartadas
- Array ordenado por carril reconstruido cada tick: coste de ordenación por tick para un invariante que la lista mantiene gratis.
- Euler explícito (posición con la velocidad vieja): peor estabilidad al frenar; el semi-implícito cuesta lo mismo.
- Actualización en una sola pasada in situ: el resultado dependería del orden de índices y podría solapar vehículos.
- Ruta como `List<int>` por vehículo: asignaciones y basura del GC en cada viaje.
- Cabezas de lista dentro de `Lane` (como en el GDD): mezclaría estado dinámico en la red y su hash; se guardan en `VehicleSystem`.
- Doble búfer de snapshot: el render necesita dos snapshots completos para interpolar más el que se escribe; con dos habría que bloquear.

## Consecuencias
- El líder dentro de la pista es O(1) y el lookahead mira como mucho 2 pistas: el coste por vehículo es casi constante.
- El clamp garantiza la ausencia de solapes en una cadena; los solapes entre conexiones que se cruzan siguen siendo posibles hasta M1-07.
- Editar la red bajo los vehículos los elimina o los marca para re-ruta: aceptable en M1, se refinará con el deshacer en M2.

### Tests obligatorios de M1-06b
1. Velocidad libre: carril recto de 1.000 m a 50 km/h, sin variación de conductor → tras 60 s, `|V − 13,89| < 1 %` y nunca la supera en > 0,1 %.
2. Frenada tras vehículo parado (`Frozen`) a 200 m: el seguidor se detiene con hueco en `[s0 − 0,5, s0 + 0,5]`, deceleración ≥ −9 m/s², sin solape.
3. Sin solapes: 50 vehículos aleatorios (Pcg32) en un circuito cerrado, 3.000 ticks; cada tick, en cada lista, `S[ahead] − Len[ahead] − S[i] ≥ MinGap − 1e-4`, también entre pistas.
4. Nunca `V < 0`, nunca `NaN`/`∞` en `S, V, A`.
5. Invariantes de lista: orden por S, `Head/Tail` y `Ahead/Behind` coherentes, cada vehículo en exactamente una lista.
6. Transición carril → conexión → carril en una T girando a derecha y a izquierda: cursor de ruta correcto, llega y se da de baja.
7. Lookahead: carril de salida con un vehículo parado al inicio → el que llega frena antes de entrar en la conexión.
8. Puerta que siempre deniega → se detiene con `S ≤ Length − StopMargin + 0,01` y `V = 0`; con `GapOnlyGate` y carril de salida lleno, espera y luego pasa.
9. Anticipación: entra en una conexión de giro a ≤ su `SpeedLimit` + 0,5 m/s.
10. `RoutePool`: 10.000 altas/bajas aleatorias sin solapes entre bloques y sin crecer tras el calentamiento.
11. Snapshot: nº de entradas = vivos; distancia al eje del carril < 0,01 m; anterior/actual con ticks consecutivos.
12. Determinismo: dos ejecuciones con la misma semilla → hash idéntico cada 100 ticks durante 2.000 ticks.
13. Época: eliminar un segmento con vehículos encima no lanza excepciones; esos vehículos se dan de baja y los que lo tenían por delante quedan `NeedsReroute`.
14. Asignaciones: `GC.GetAllocatedBytesForCurrentThread()` sin cambios durante 100 ticks con 2.000 vehículos.
15. Benchmark (no bloqueante): 16.000 vehículos en una cuadrícula, ms por tick en el informe.
