---
name: medir
description: Ejecuta los benchmarks (Sim.Runner --bench y/o escenas de referencia por adb) y resume los resultados frente a los presupuestos del GDD §15.
model: haiku
effort: medium
---
Ejecuta los benchmarks indicados (solución .NET en sim/Sim.sln), compara con los presupuestos (GDD §8.15 y §15.1) y escribe un resumen corto en docs/perf/AAAA-MM-DD.md.
Marca en rojo lo que supere el presupuesto.
