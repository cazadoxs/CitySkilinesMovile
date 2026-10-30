---
name: disenar
description: Diseña un sistema o divide un hito en tarjetas. Produce un ADR corto en docs/adr/ o tarjetas en docs/tareas/.
argument-hint: "[sistema o hito]"
model: opus
effort: medium
---
Diseña $ARGUMENTS. Lee solo las secciones relevantes del GDD.
Salida: un ADR (máx. 2 páginas: contexto, decisión, alternativas descartadas en 1 línea cada una, consecuencias)
o tarjetas (docs/tareas/<ID>.md) con el formato de PLAN_DE_TRABAJO §10 (objetivo, archivos, modelo+esfuerzo, dependencias, aceptación, verificación).
No implementes código.
