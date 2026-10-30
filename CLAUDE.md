# URBE: instrucciones para Claude

## Qué es el juego

URBE es un city builder de simulación profunda al estilo de Cities: Skylines, pensado desde cero para móvil (Android principal, PC secundario). Se juega con carreteras libres y curvas, ciudadanos con vida propia y tráfico real cuyos atascos tienen consecuencias. Los edificios crecen con variedad visual, no son todos iguales. Gratis con demo y desbloqueo único; sin anuncios, sin temporizadores, sin gemas y 100 % offline.

## Mapa de documentos

- `docs/GDD.md`: qué es el juego y cómo funciona. Fuente de verdad del diseño. Si una tarea lo contradice, se actualiza el GDD primero.
- `docs/PLAN_DE_TRABAJO.md`: quién hace qué, modelos de IA, skills, hitos y tarjetas M0 a M2.
- `docs/tareas/<ID>.md`: una tarjeta por tarea (por ejemplo `M0-03.md`).
- `docs/adr/`: decisiones técnicas (Architecture Decision Records).
- `tools/music/`: generador de banda sonora (`render.py`, entre otros).
- `audio/music/`: música renderizada en OGG y `tracks.json`.

## Estructura prevista del repo

```
/docs                    GDD, plan de trabajo, decisiones (ADR)
/sim                     Sim.Core (C#), Sim.Tests, Sim.Runner, Sim.sln
/unity/URBE              proyecto de Unity (Assets/_Project: Scripts, Art, UI, Audio, Data, Scenes)
/tools                   blender (Python), comfyui (workflows), scripts (validaciones)
/audio, /tools/music     música y su generador
/ASSETS_LICENSES.md      licencias y origen de cada asset externo
```

`Sim.Core` se incluye en Unity como paquete local o DLL compilada desde `/sim`, para que haya una sola fuente de verdad.

## Reglas clave

- `Sim.Core` es C# netstandard2.1 y no depende de Unity. Se testea en la nube con `dotnet test sim/Sim.sln`.
- En bucles calientes, sin asignaciones (nada de `new`, LINQ ni boxing por frame).
- RNG propio con semilla (PCG32 o xoshiro). Nunca `System.Random` ni `UnityEngine.Random` dentro de `Sim.Core`.
- Orden de iteración estable: nada de depender del orden de un `Dictionary` o de hashes no deterministas.
- Commits con formato `tipo(área): resumen [ID]`, por ejemplo `feat(sim): grafo de carreteras [M0-03]`.
- Una tarjeta por sesión. Al terminar, se cierra la tarjeta y no se empieza otra.
- Sala limpia: no descompilar juegos comerciales ni usar herramientas de ingeniería inversa; no copiar código GPL (Micropolis, OpenTTD).
- Assets nuevos (incluida música externa) registrados en `ASSETS_LICENSES.md` con archivo, fuente, autor, licencia, fecha y modificaciones.
- No leer el GDD entero. Leer solo la sección que cita la tarjeta.

## Regla de modelos (resumen de PLAN §4.2)

- **Sonnet medium** por defecto. Es el caballo de batalla para sistemas, tests, Unity, interfaz y herramientas.
- **Opus medium** solo para diseño, ADR, dividir hitos en tarjetas y revisión del núcleo de simulación.
- **Opus high** solo para tarjetas críticas: intersecciones y anti-bloqueo (M1-07), determinismo y guardado (M5).
- **Haiku** para datos JSON, traducciones, documentación, changelogs, mensajes de commit, búsquedas y lotes.
- **Escalado:** tras 2 fallos en la misma tarjeta, se sube un escalón (Haiku medium, Sonnet medium, Sonnet high, Opus medium, Opus high). Nunca se salta directo a max.
- El esfuerzo más bajo que funcione es el primer ahorro. Bajar el esfuerzo antes de cambiar de modelo.

## Skills y subagentes del proyecto

- Skills (`/nombre`): `/tarea`, `/disenar`, `/datos`, `/revisar`, `/assets`, `/medir`.
- Subagentes: `explorador`, `documentador`, `probador`, `revisor-sim`.
- Detalle en `docs/PLAN_DE_TRABAJO.md` §6.
