# ADR 005 — La ciudad: zonas, edificios, población, servicios y dinero

Estado: aceptado · Tarjetas: M3-01 a M3-10 · GDD §3.3, §4.4-4.5, §4.7, §4.10-4.11, §5.4-5.5, §6.2, §8.1-8.3, §8.10-8.13

## Contexto

URBE es un city builder; el tráfico es solo su base. Hasta M2 no hay ciudad: nada se zonifica, nada crece y no hay dinero. Este ADR fija el núcleo para una partida jugable: mapa vacío, trazar calles, zonificar, dar agua y luz, ver crecer la ciudad, cobrar impuestos y pagar servicios. Todo lo que no decide aquí se toma del GDD.

## Decisión

### Raíz
- `Urbe.Sim.City.CitySimulation` posee `RoadNetwork`, `TrafficSimulation` y los sistemas nuevos. Los tests de tráfico siguen usando `TrafficSimulation` directamente.
- `Step()` sigue el orden de GDD §8.1. Frecuencias del GDD, repartidas por lotes.
- Calendario: 1 día = 3.600 ticks (6 min a ×1). 1 semana = 7 días.
- Todo el estado de ciudad entra en `ComputeHash()`. Mismas semilla y comandos, mismo hash. Sin `Dictionary` iterado, sin `float` dependiente de orden.
- Equilibrio en una clase de datos `CityBalance` con valores por defecto. `Sim.Core` no lee JSON; Unity podrá rellenarla más adelante.

### Mapa y arranque
- Partida nueva: mapa plano de 2×2 km, vacío, con una autopista que entra por un borde y termina en un nodo. Esa es la conexión exterior: por ahí llegan inmigrantes, mercancías y salidas de exportación. Terreno y agua llegan después.
- Dinero inicial 70.000. Hitos de §3.3 desbloquean elementos.

### Zonas (M3-01)
- Las celdas no son una rejilla global: pertenecen a un lado de un segmento zonificable. `ZoneCell { Vec2 Pos; Vec2 Dir; EntityId Segment; byte Side, Along, Depth; ZoneType Zone; byte Density; int Building }`, 8×8 m, cada 8 m de arco entre retranqueos, hasta 4 de fondo.
- Conflictos: índice espacial de 8 m. Se descarta la celda dentro de otra calzada o que solape una celda anterior, en orden estable de segmento.
- Al cambiar la red se regeneran solo los segmentos afectados, con `Subscribe/ConsumeChanges`. La zona pintada se conserva por posición, incluso tras un split. Si un edificio pierde sus celdas, se demuele.
- Zonas de M3: R baja/media/alta, C baja/alta, I, O. Mixta en M5. Comandos pintar y borrar con deshacer.

### Edificios que crecen (M3-02)
- `Building { EntityId Id; Vec2 Pos; float Yaw; byte Width, Depth; ZoneType Zone; byte Density, Level, Style; uint Seed; ushort Capacity; BuildingFlags Problems; ... }`.
- Parcela: rectángulo de 1×1 a 4×4 celdas libres de la misma zona, frente a calle. Tamaño según densidad y semilla.
- Demanda −100..100 según §8.12 y crecimiento según demanda × valor del suelo × servicios, con límite de edificios nuevos por lote.
- Requisitos y niveles según §4.5. Sin electricidad o agua sostenidas, el edificio se abandona.
- Evento para render: cola de cambios (creado, nivel, abandonado, demolido) que Unity consume, sin asignaciones.

### Redes y servicios (M3-03)
- Agua, alcantarillado y electricidad van bajo las calles: una componente conexa por grupo de segmentos unidos. Union-find reconstruido al cambiar la red. Producción frente a consumo, déficit por orden estable y prioridad (§8.10).
- `ServiceBuilding` con tipo de un catálogo en C#: bomba de agua, desagüe, eólica, carbón, vertedero, centro de salud, bomberos, policía, colegio, parque. Se engancha al segmento más cercano.
- Cobertura de M3: Dijkstra sobre tiempo a flujo libre desde cada servicio, repartido en el tiempo, guardado por segmento. El despacho real de vehículos y el fuego son M4.

### Población (M3-04)
- `Citizen` y `Household` en arrays, según §8.3. Inmigración si hay demanda R y vivienda libre; emigración si la felicidad es baja. Empleo por cualificación, envejecimiento y educación diarios por lotes.
- Rutina diaria: casa, trabajo o colegio, compras, casa. Los viajes salen del edificio real y entran en `TripGenerator`/vehículos.
- Tráfico híbrido: con el tope de agentes físicos superado, el viaje es abstracto, dura el coste de ruta y suma flujo estadístico al segmento.
- Comercio e industria generan camiones hacia y desde la conexión exterior.

### Campos y economía (M3-05)
- Valor del suelo y contaminación del aire en rejilla de 32 m por bandas (§8.11).
- Impuestos y mantenimiento según §8.13, semanales. Saldo, ingresos y gastos por categoría. Coste de construir contra el saldo, con rechazo y motivo.
- Hitos §3.3 y lista de desbloqueos. Bancarrota: M5.

### Unity
- Unity lee estado y envía comandos; no simula. `SimHost` pasa a alojar `CitySimulation`.
- Edificios: gramática determinista en C# (`semilla`, parcela, zona, nivel) que produce una lista de instancias de piezas. Kit modular propio generado con script de Blender, dos estilos: Mediterráneo y Ensanche. Instancing por pieza y por chunk, sin regenerar lo que no cambia. A distancia, un impostor de caja con tinte.
- **Cambio en M3-09 (catálogo en vez de gramática):** en lugar de componer piezas, hay un catálogo de 115 modelos completos (`Bld_<R|C|I|O>_<low|med|high>_L<1-5>_<A|B|C>` y `Svc_<tipo>`, con LOD0 y LOD1, atlas común con tinte en el alfa). Unity elige por edificio el modelo más próximo a zona, densidad, nivel y huella de la parcela (puntuación 100 x densidad + 10 x nivel + huella) y la semilla del edificio decide entre los empates: misma semilla, mismo modelo. Se escala a la parcela con límites y se tiñe por zona y semilla (paletas por zona). Render instanciado por chunks de 128 m reaccionando a los eventos de `BuildingSystem`; LOD1 a 260 m y caja con tinte a 700 m; los abandonados son oscuros. Esto sustituye a la gramática y a los dos estilos de arriba.
- Interfaz según §11.2, §12.7 y `docs/figma.md`: barra de construcción por categorías, dinero, población, fecha, barras RCI, panel de edificio con problemas y motivos.

## Alternativas descartadas
- **Rejilla global de zonas:** no sigue las calles curvas, que son el pilar 1 del juego.
- **Un GameObject por edificio:** no escala en móvil.
- **Mallas de edificio combinadas por chunk desde el principio:** más memoria y sin datos que lo pidan.

## Consecuencias
- Sin guardado hasta M5: si hace falta probar partidas largas, el bot de `Sim.Runner` las reproduce.
- Sin terreno, ríos ni bomba contaminada todavía.
- Cobertura por radio temporal a flujo libre: un atasco aún no retrasa servicios hasta M4.
