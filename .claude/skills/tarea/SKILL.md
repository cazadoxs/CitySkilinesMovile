---
name: tarea
description: Ejecuta una tarjeta de tarea de docs/tareas por su ID (por ejemplo M1-05) siguiendo sus criterios de aceptación.
argument-hint: "[ID-de-tarjeta]"
model: sonnet
effort: medium
---
Lee SOLO la tarjeta $0 en docs/tareas/$0.md y los archivos que cita (no leas el GDD entero salvo la sección que indique).
1. Implementa lo pedido con el mínimo cambio necesario.
2. Ejecuta la verificación de la tarjeta (tests/benchmark/build; la solución .NET está en sim/Sim.sln).
3. Actualiza la tarjeta: estado, notas de traspaso (qué se hizo, qué queda, problemas).
4. Commit con mensaje "feat(<área>): <resumen> [$0]".
Si fallas 2 veces, para y escribe en la tarjeta "ESCALAR: <motivo>".
