# ADR 004 — Intersecciones: conflictos, prioridad, reserva, anti-bloqueo y semáforos

Estado: aceptado · Tarjeta e implementación: M1-07 · Usan: M1-08, M1-11 · GDD §4.3, §8.6, §8.7 · Depende de ADR 001 y ADR 002 (enmienda su §Enganche)

## Contexto

ADR 002 mueve los vehículos con IDM y deja `GapOnlyGate`, que no ve los flujos que se cruzan ni confluyen. M1-07 debe garantizar que dos
vehículos en conexiones en conflicto nunca ocupan a la vez la zona común, respetar prioridad, ceda/stop y semáforos de fases fijas, no taponar el
cruce y no dejar a nadie esperando para siempre en un cruce aislado. Todo determinista, sin asignaciones por tick y con coste acotado por petición.

## Decisión

Namespace `Urbe.Sim.Intersections`. Clase principal `JunctionController : IJunctionGate`; construcción estática en `JunctionBuilder`.

### Constantes (`JunctionConfig`, valores por defecto)
`ConflictDistance = 2,5 m` · `ZoneSampleStep = 1,0 m` (máx. 64 muestras por conexión) · `ZonePad = 1,0 m` · `StopZone = 5,0 m` ·
`StopDwellTicks = 10` · `StopSpeed = 0,3 m/s` · `AgingTicks = 50` (5 s) · `AgingMax = 7` · `AmberBrake = 3,0 m/s²` ·
semáforo: `GreenMajor = 250`, `GreenOther = 150`, `Amber = 30`, `AllRed = 20` ticks (25 s, 15 s, 3 s, 2 s).

### Máscara de conflictos (en `JunctionBuilder.BuildNode`, solo al reconstruir el nodo)
Para cada par `(i, j)`, `i ≠ j`, de conexiones del nodo (`LocalIndex` = índice − `Node.ConnFirst`, ≤ 63):
1. **Mismo `From`** (divergencia): **sin conflicto**. Los separa el seguimiento (ver "Enmiendas", lookahead 3 bis).
2. **Mismo `To`** (convergencia): conflicto `Merge`.
3. Si no: conflicto `Cross` si `Intersections.CurveCurve(i.Curve, j.Curve, hits[8]) > 0` o si alguna muestra de `i` queda a
   `< ConflictDistance` de la polilínea de `j` (cuasi-cruce). Antes, descarte rápido si `Bounds` inflados 2,5 m no se solapan.

**Zona de conflicto** de `i` respecto a `j`: se muestrea `i` cada 1 m por longitud de arco (`ArcLengthTable` en `stackalloc`) y se marcan las
muestras a `< 2,5 m` de la polilínea de `j`; zona = `[primera − ZonePad, última + ZonePad]` recortada a `[0, L_i]`. En `Merge` se fuerza `End = L`.
Los puntos de corte de `CurveCurve` (convertidos con `DistanceAtT`) quedan siempre dentro de la zona (se amplía si hiciera falta).
```
struct ConflictEntry { byte Other; ConflictKind Kind /*Cross, Merge*/; ushort StartDm, EndDm /*zona en i*/; ushort OtherEndDm /*fin de zona en j*/; } // 8 B
struct ConnGate { ulong ConflictMask; int ConflictFirst; byte ConflictCount; byte Approach /*slot del segmento From*/; byte Rank;
                  ApproachRule Rule /*Free, Yield, Stop, Signal*/; byte PhaseBits; ushort Permits, Occupants; int LastEntrant; }
```
Decímetros: `Start` hacia abajo, `End` hacia arriba (conservador). `ConflictMask` simétrica e irreflexiva; las entradas de cada conexión van
ordenadas por `Other`. `ConnGate[]` y `ConflictEntry[]` usan el mismo esquema de bloques por nodo que ADR 001 (reusar si cabe, añadir al final,
compactar con > 50 % de huecos, solo en ediciones). Coste de construcción ≤ ~2 ms para una X 2+2; nunca en el tick.

### Control del cruce
`enum ControlKind : byte { Priority, Stop, Signal }`. `Node.ControlId` indexa una tabla fija: **0 = Auto**, 1 = Priority, 2 = Stop, 3 = Signal
(tiempos por defecto). M1-07 añade `RoadNetwork.SetNodeControl(EntityId, ushort)` (época +1, nodo cambiado, `ControlId` entra en el hash).
**Auto**: `Signal` si el nodo tiene 3 o 4 accesos de entrada y ≥ 3 segmentos con `LanesForward + LanesBackward ≥ 4`; si no, `Priority`.

**Vía principal** (también ordena las fases): jerarquía `H(seg) = SpeedLimit_kmh + 10·(F+B)` del `RoadType`. Candidatas = pares de segmentos
`(a, b)` unidos por al menos una conexión `Straight` en algún sentido. Se elige por `(min H desc, max H desc, |180° − ángulo(a,b)| asc, min slot asc)`.
`MajorA/MajorB` = slots elegidos (255 si no hay par: todos los accesos son principales). Nodos de 2 segmentos: todos principales.
**Reglas por acceso**: principal → `Free`; secundario → `Yield` en `Priority`, `Stop` en `Stop`; en `Signal` todos `Signal`.

**Rango** (mayor = más prioridad; codifica "giro a la izquierda cede al recto opuesto" y "secundario cede a principal"):

| Acceso | Recto | Derecha | Izquierda | Cambio de sentido |
|---|---|---|---|---|
| Principal (y todos en `Signal`) | 6 | 6 | 5 | 4 |
| Secundario | 2 | 3 | 1 | 0 |

`EffPriority = Rank + min(AgingMax, waitTicks / AgingTicks)` (envejecimiento). `waitTicks = tick − _firstRequestTick[vehicle]`.

### Semáforos (fases fijas, estado sin memoria)
Accesos de entrada en orden CCW del nodo. Fase 0 = los dos accesos del par principal (todas sus conexiones, la izquierda en "permitido": cede por
rango). Con el resto: si quedan 2 accesos unidos por `Straight` → fase 1 con ambos; si no, una fase por acceso restante en orden CCW. Sin par
principal → una fase por acceso. T → 2 fases; X → 2 fases; máximo 8 (`PhaseBits`, bit p = verde en fase p). Cada fase p: `Green_p` (250 la
fase 0, 150 las demás) → `Amber` 30 → `AllRed` 20. `CycleTicks` = suma. Estado = función de `t = tick % CycleTicks` (sin desfase en M1), calculado
en `BeginTick` solo para los nodos de la lista `_signalNodes` (ordenada por índice) y cacheado en `JunctionState { Phase; SignalStage Stage; ushort StageTicksLeft; }`.
Render: `SignalAspect GetSignalAspect(EntityId node, int connection)` → `Red, Amber, Green`. El adaptativo queda fuera.

### Protocolo de reserva (enmienda la interfaz de ADR 002)
```
public interface IJunctionGate {
  void OnNetworkChanged(ReadOnlySpan<EntityId> changedNodes); // fase 0, antes del remapeo de vehículos
  void BeginTick(ulong tick);                                 // tras la fase 0: semáforos, limpiar cola
  void RequestEntry(in EntryRequest req);                     // fase 1a: SOLO encola
  ReadOnlySpan<EntryGrant> ResolveRequests();                 // fase 1b: decide en orden canónico
  void OnEntered(int connection, int vehicle);
  void OnExited(int connection, int vehicle);                 // cuando la TRASERA sale de la conexión
  void OnVehicleRemoved(int vehicle);                         // bajas (fases 0 y 5)
}
struct EntryRequest { int Vehicle; LaneId From; int Connection; LaneId To; float DistanceToStop /*Lane.Length − S*/; float Speed;
                      float Space /*Length + s0*/; ushort StoppedTicks /*Vehicle.WaitTicks*/; }
struct EntryGrant   { int Vehicle; int Connection; float Space; }
public interface IVehicleView { float FreeSpace(LaneId lane); float RearOnConnection(int vehicle, int connection); }
```
**Cuándo se pide**: fase 1a, cada tick, toda cabeza de carril sin permiso con `d ≤ V²/(2·b) + V·dt + StopMargin + 5 m` (ADR 002). Si se deniega,
vuelve a pedir el tick siguiente; la cola se vacía en `BeginTick`. La primera petición fija `_firstRequestTick[v]` (`uint[]`, 0 = ninguna, se guarda
`tick + 1`); se borra en `OnEntered` y `OnVehicleRemoved`.

**Orden canónico** (no depende del orden de llegada): clave `ulong = node(24 b) | (15 − EffPriority)(4 b) | (0xFFFF − min(wait, 0xFFFF))(16 b) | vehicle(20 b)`;
ordenación propia sin asignaciones (`SortUtil.Sort(ulong[] keys, int[] idx, int n)`, radix LSD; no `Array.Sort`, que en Mono puede asignar).
Claves únicas → resultado idéntico con cualquier permutación de entrada.

**Evaluación** de cada petición, en ese orden, para la conexión `c` del nodo `n` (al cambiar de nodo: `claim = 0` y se vacía `scratch`):
1. `Signal`: denegar si `c` no está en verde ni ámbar de la fase actual o si `Stage = AllRed`. En verde, solo si
   `DistanceToStop ≤ max(Speed, 2)·(StageTicksLeft·dt + Amber·dt)`. En ámbar, solo si `DistanceToStop ≤ Speed²/(2·AmberBrake) + 1 m`
   o si es giro a la izquierda parado (`StoppedTicks ≥ 20`) con `DistanceToStop ≤ StopZone` (despeje). Motivo `Signal`, sin reclamar.
2. `Rule = Stop`: denegar salvo `Speed ≤ StopSpeed`, `DistanceToStop ≤ StopZone` y `StoppedTicks ≥ StopDwellTicks`. Motivo `MustStop`, sin reclamar.
3. **Reclamada**: si `ConflictMask[c] & claim ≠ 0` → denegar (`Claimed`), sin reclamar.
4. **Conflicto**: si `ConflictMask[c] & (PermitMask | OccupiedMask) ≠ 0`, recorrer las `ConflictEntry` de `c` (≤ 63): con `j = Other`, bloquea si
   `Permits[j] > 0` o si `Occupants[j] > 0` y `view.RearOnConnection(LastEntrant[j], j) < OtherEndDm/10` (**liberación parcial**: el más rezagado de
   `j` ya pasó la zona común). Si bloquea → denegar (`Conflict`) y `claim |= 1 << c`.
5. **Anti-bloqueo**: `need = min(Space, Lane(To).Length)`; si `view.FreeSpace(To) − scratch[To] < need` → denegar (`ExitFull`), sin reclamar.
   `FreeSpace` = ADR 002 (incluye `_laneReserved`, es decir, los que ya van por una conexión hacia `To`). Un carril de salida solo recibe de su nodo
   de origen, así que `scratch` es un array local de ≤ 64 pares `(LaneId, float)`.
6. **Conceder**: `Permits[c]++`, bit en `PermitMask`, `claim |= 1 << c`, `scratch[To] += Space`, añadir `EntryGrant`.

Reclamar en (4) hace que una petición de más rango que espera no sea adelantada por otras de menos rango que la cruzan; con el envejecimiento,
cualquier petición acaba siendo la primera de su nodo. `OnEntered`: `Permits--`, `Occupants++`, `LastEntrant = v`, máscaras al día.
`OnExited`: `Occupants--` (FIFO: si `v == LastEntrant` entonces `Occupants` pasa a 0 y `LastEntrant = −1`). Contadores `JunctionStats`
(`Granted, DeniedSignal, DeniedStop, DeniedClaimed, DeniedConflict, DeniedExitFull`) por controlador.

### Enmiendas a ADR 002 (las aplica M1-07 en `Vehicles/`)
- Fase 1 → **1a** peticiones (índice ascendente) · `ResolveRequests()` · **1b** aplicar concesiones en su orden (`HasEntryPermit`, `_laneReserved[To] += Space`).
  `GapOnlyGate` se adapta: encola y resuelve por índice de vehículo con el mismo `scratch`.
- `Vehicle.PrevConnection` (`int`, −1): se fija al pasar de conexión a carril; en la fase 4, cuando `S ≥ Length` en el carril → `gate.OnExited(prev, v)`.
  Si el frente entra en otra conexión antes (carril más corto que el vehículo), se llama antes a `OnExited`. `RearOnConnection(v, j)` =
  `S − Len` si está en `j`; `L_j + S − Len` si `PrevConnection == j`; `+∞` si no.
- **Lookahead 3 bis** (divergencia): `int[] _laneLastEntrant` por carril; si la conexión siguiente está vacía y el último que salió de este carril
  sigue con frente en una conexión del mismo nodo, es el líder (`gap = d + S − Len`).
- Época: `OnNetworkChanged` pone a cero el estado de los nodos reconstruidos; después el sistema de vehículos llama `OnEntered` por cada vehículo
  remapeado sobre sus conexiones y retira los permisos hacia esos nodos (vuelven a pedir).

### Anti-inanición, anti-deadlock y gancho para M1-11
En un cruce aislado no hay ciclo de espera: quien ocupa una conexión tiene hueco reservado a la salida y siempre avanza; quien espera lo hace por
ocupantes que salen; el envejecimiento acota la espera de los rangos bajos (secundario a la izquierda supera al recto principal en ≤ 35 s).
Gancho: `ushort GetWaitTicks(int vehicle)`, `JunctionState.OldestWaitTicks` (máximo de la cola del tick) y `void ForceNextGrant(int vehicle)`:
en la siguiente resolución esa petición va primera y se salta los pasos 1, 2, 3 y 5, **nunca el 4**. La detección de ciclos entre nodos es de M1-11.

### Rendimiento y determinismo
Por petición: O(1) si `ConflictMask[c] & ocupación == 0`; si no, O(ConflictCount[c]) ≤ 63, cada paso O(1). Ordenación O(R) (radix) con R = peticiones.
`BeginTick` O(nodos con semáforo). Cero asignaciones: colas, claves y concesiones preasignadas a la capacidad de vehículos (crecen solo fuera del tick).
`popcount`/`ctz` propios (`Bits`, De Bruijn): `BitOperations` no existe en netstandard2.1. `ComputeHash()`: por nodo en orden de índice,
máscaras, `Permits`, `Occupants`, `LastEntrant` y estado de semáforo; más `_firstRequestTick` de los vehículos vivos.

## Alternativas descartadas
- Conceder dentro de `RequestEntry` (inmediato): el resultado dependería del orden de índices de los vehículos.
- Reserva por franjas de tiempo (tipo AIM): exige predecir trayectorias y re-reservar al frenar; la zona + liberación parcial basta.
- Matriz densa 64×64 de puntos de conflicto por nodo: 16 KB por nodo; las entradas dispersas ocupan ~1 KB en una X 2+2.
- Solo `CurveCurve` para el conflicto: no detecta cuasi-cruces ni da la extensión de la zona en ángulos rasantes.
- Prioridad a la derecha en cruces equivalentes: interbloqueo con 4 llegadas simultáneas; se elige una vía principal determinista.
- Revocar permisos al pasar a ámbar: obliga a frenadas imposibles; la seguridad ya la da la máscara y el verde exige ETA.
- Divergencia como conflicto: cada coche pararía en la línea detrás del anterior; el seguimiento 3 bis lo resuelve sin perder capacidad.

## Consecuencias
- La seguridad no depende del tipo de control: el semáforo y la prioridad solo deciden quién pide; la máscara siempre se comprueba.
- Reservar a distancia de frenado reduce algo la capacidad (≈ 4 s de antelación a 50 km/h); se acepta a cambio de no frenar en seco.
- Un secundario no ve a un principal que aún no pide (más allá del horizonte); el principal, si llega, frena: realista y acotado.
- Cambio de carril (M1-08) o apariciones a mitad de un carril de salida pueden invadir hueco reservado: deben respetar `_laneReserved`.
- Interfaz de ADR 002 cambiada (`RequestEntry` ya no devuelve `bool`); M1-06b solo adapta `GapOnlyGate` y las fases 1a/1b.

## Tests (sim/Sim.Tests/Intersections/)
**Escenarios de propiedades**: (A) X `street_2`×`street_2`, `Priority` · (B) T con tallo `street_2` sobre `avenue_4`, `ControlId = 2` (Stop) ·
(C) X `avenue_4`×`avenue_4`, `Signal` (auto). Brazos de 150 m. Semillas Pcg32 1-5. Demanda por carril de entrada `λ ~ U[0,02; 0,05]` veh/s (A, B) y
`U[0,02; 0,06]` (C), por debajo de la capacidad; aparición en S = 0 solo si la cola del carril está a ≥ 15 m; 80 % coche, 20 % camión; salida uniforme
entre los giros posibles. 10.000 ticks con demanda y luego vaciado hasta 1.500 ticks sin demanda.
**"Sin colisiones en el cruce"**: cada tick, tras la fase 5, para todo par de vehículos `a ≠ b` del mismo nodo con cuerpo en conexiones `i ≠ j` y bit
`j` en `ConflictMask[i]`: cuerpo de `a` en `i` = `[S − Len, S]` si su frente está en `i`, o `[L_i − (Len − S), L_i]` si `PrevConnection == i`.
Colisión si el cuerpo de `a` corta `[Start_i(j), End_i(j)]` **y** el de `b` corta `[Start_j(i), End_j(i)]`. Debe haber 0. Además, el test 3 de ADR 002
(sin solapes en cadenas) en todas las pistas.
**"Sin bloqueos permanentes"**: todo vehículo con primera petición en el tick `t0` llama `OnExited` en ese nodo antes de `t0 + 600` (60 s simulados), y al
final del vaciado `alive == 0`.

1. Máscara X 1+1: simétrica, irreflexiva; recto O-E × recto N-S `Cross`; izquierda O→S × recto E-O `Cross`; izquierda O→S × derecha E→S `Merge`;
   derecha O→S × recto N→S `Merge`; rectos opuestos y derechas opuestas sin conflicto; mismo `From` nunca.
2. Zonas: todo corte de `CurveCurve` dentro de `[Start, End]` en ambas conexiones; `Merge` con `End = L`; `Start ≤ End`.
3. 200 nodos aleatorios (Pcg32): `Merge` ⇔ mismo `To`; ninguna pareja con mismo `From`; con distinto `From`, distancia mínima fina (paso 0,1 m) < 2,3 m ⇒ conflicto y > 2,7 m ⇒ sin conflicto.
4. Vía principal: T `street_2` en `avenue_4` → avenida; X `avenue_4`×`street_2` → avenida; X equivalente → mismo par en 2 construcciones.
5. Rangos: recto principal e izquierda opuesta piden en el mismo tick → solo el recto; la izquierda pasa cuando el recto rebasa su zona.
6. Orden canónico: las mismas 50 peticiones en 10 permutaciones aleatorias → lista de concesiones idéntica.
7. Anti-bloqueo: salida con hueco < `Space` → `ExitFull`; dos peticiones a la misma salida con hueco para una → solo la de más prioridad;
   salida más corta que el camión → se concede solo con la salida vacía y sin reservas.
8. Liberación parcial: con un ocupante en `j` antes de `End_j(c)` se deniega `c`; justo después se concede con `j` aún ocupada.
9. Stop: ninguna concesión con `Speed > 0,3` o `StoppedTicks < 10`; el vehículo para en `DistanceToStop ≤ 5 m`.
10. Envejecimiento: secundario a la izquierda con flujo principal de 0,25 veh/s por sentido entra en ≤ 40 s; sin envejecimiento (`AgingMax = 0`) no entra en 120 s.
11. Fases: T → 2 fases, X → 2 fases, nodo de 5 accesos → 1 por acceso fuera del par; secuencia verde/ámbar/todo-rojo periódica con `CycleTicks`
    exacto; ninguna concesión en rojo ni en todo-rojo; en ámbar solo quien no puede parar o el despeje de la izquierda.
12. Propiedades A, B y C (arriba): 0 colisiones, ninguna espera > 600 ticks, vaciado completo; invariantes cada tick (`Permits`, `Occupants`,
    máscaras y `LastEntrant` coinciden con un recuento de fuerza bruta).
13. Control negativo: el escenario A con `GapOnlyGate` detecta colisiones > 0 (el detector funciona).
14. Determinismo: dos ejecuciones del escenario C con la misma semilla → hash de vehículos + controlador idéntico cada 100 ticks.
15. Época y asignaciones: cambiar `ControlId` y reconstruir un nodo con vehículos dentro no lanza ni descuadra contadores; 1.000 ticks con 500
    vehículos en cruces sin bytes asignados (`GC.GetAllocatedBytesForCurrentThread`).
