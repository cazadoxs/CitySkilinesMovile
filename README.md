# URBE

City builder de simulación profunda para móvil (Android principal, PC secundario), inspirado en Cities: Skylines y pensado desde cero. Carreteras libres y curvas, ciudadanos con vida propia, tráfico real cuyos atascos tienen consecuencias y edificios que crecen con variedad visual. Gratis con demo y desbloqueo único, sin anuncios ni temporizadores, 100 % offline.

Título provisional.

## Estado actual

- **M0 (fundación) en curso.** Se están montando el repositorio, la documentación y la base de la simulación.
- **Banda sonora v1 hecha.** Seis temas de ambiente y dos jingles (hito y logro) en `audio/music/`, generados por `tools/music/`.

## Compilar y testear la simulación

Requiere el SDK de .NET 8.

```bash
dotnet test sim/Sim.sln
```

> `sim/` aún no existe en el repo: se crea en la tarjeta M0 correspondiente.

## Regenerar la música

Requiere Python 3.

```bash
python3 tools/music/render.py --all                  # OGG en audio/music/ + tracks.json
python3 tools/music/render.py --all --stems          # además, capas base, melodía y ritmo
python3 tools/music/render.py --all --mp3 DIR        # copias MP3 para escuchar fuera del juego
```

## Documentación

- [docs/GDD.md](docs/GDD.md): diseño del juego (fuente de verdad).
- [docs/PLAN_DE_TRABAJO.md](docs/PLAN_DE_TRABAJO.md): quién hace qué, modelos de IA, hitos y tarjetas.
- [CLAUDE.md](CLAUDE.md): instrucciones para Claude Code dentro del repo.
- `docs/tareas/`: una tarjeta por tarea.
- `docs/adr/`: decisiones técnicas.
- [ASSETS_LICENSES.md](ASSETS_LICENSES.md): licencias de assets externos (pendiente de crear).

## Licencia

Pendiente de decidir por el director (Álvaro). Hasta entonces, el código no tiene licencia de uso público.
