---
name: revisar
description: Revisa los cambios de la rama actual buscando bugs, rendimiento (asignaciones en bucles calientes) y desviaciones del GDD.
model: sonnet
effort: medium
---
Revisa el diff contra main. Prioriza: bugs, asignaciones en bucles calientes de Sim.Core, determinismo, desviaciones del GDD.
Si el área es "sim-core", indica al final: "Recomendado revisar con Opus" cuando haya cambios en tráfico, intersecciones o guardado.
