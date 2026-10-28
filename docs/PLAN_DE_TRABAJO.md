# URBE — Plan de Trabajo

> **Versión:** 0.1 · **Fecha:** 2026-10-28
> **Documento hermano:** [GDD.md](GDD.md) (qué es el juego y cómo funciona).
> Este documento explica **quién hace qué, con qué modelo de IA, con qué herramientas, en qué orden y cómo se trabaja cada día** para construir URBE gastando los mínimos tokens posibles.

---

## Índice

- [1. Cómo trabajamos (resumen)](#1-cómo-trabajamos-resumen)
- [2. Roles](#2-roles)
- [3. Herramientas: qué corre dónde](#3-herramientas-qué-corre-dónde)
- [4. Modelos de IA y esfuerzo](#4-modelos-de-ia-y-esfuerzo)
- [5. Chuleta de comandos](#5-chuleta-de-comandos)
- [6. Skills y subagentes del proyecto](#6-skills-y-subagentes-del-proyecto)
- [7. Protocolo de sesión](#7-protocolo-de-sesión)
- [8. Reglas de ahorro de tokens](#8-reglas-de-ahorro-de-tokens)
- [9. Orden de trabajo: hitos](#9-orden-de-trabajo-hitos)
- [10. Tarjetas de tarea detalladas (M0–M2)](#10-tarjetas-de-tarea-detalladas-m0m2)
- [11. Épicas de M3 a M7](#11-épicas-de-m3-a-m7)
- [12. Pipeline de assets paso a paso](#12-pipeline-de-assets-paso-a-paso)
- [13. Análisis de builds y sala limpia](#13-análisis-de-builds-y-sala-limpia)
- [14. Riesgos de producción](#14-riesgos-de-producción)
- [15. ChatGPT y Gemini (opcional)](#15-chatgpt-y-gemini-opcional)
- [16. Próximos pasos inmediatos](#16-próximos-pasos-inmediatos)

---

## 1. Cómo trabajamos (resumen)

```
                ┌──────────────────────────────┐
                │  Álvaro (director)           │
                │  decide · aprueba · prueba   │
                └──────────────┬───────────────┘
                               │ prioridades, feedback
        ┌──────────────────────┼──────────────────────────┐
        ▼                      ▼                          ▼
 Opus 5.5 (medium)      Sonnet 5.5 (medium)        Haiku 5.5 (low/medium)
 diseña, divide en      implementa tarjetas,       datos, JSON, prompts,
 tarjetas, revisa       tests, Unity, interfaz     licencias, documentación,
 lo crítico                                        lotes de assets
        │                      │                          │
        └──────────── GitHub (repo único) ────────────────┘
                 │                               │
       Sesiones en la NUBE                Sesiones en el PC de Álvaro
       (Sim.Core, tests, docs,            (Unity, Blender, ComfyUI,
        Figma/Canva/Adobe, música)         adb + móvil, descargas)
```

**Reglas básicas:**
1. **Una tarjeta de tarea = una sesión** con el modelo y el esfuerzo que indica la tarjeta.
2. **Opus diseña poco y bien. Sonnet ejecuta. Haiku hace lo repetitivo.**
3. **Todo pasa por GitHub:** lo que se hace en el PC y en la nube se une en el mismo repo.
4. **Cada hito termina con algo jugable o visible** que Álvaro prueba.

---

## 2. Roles

| Rol | Quién | Responsabilidades |
|---|---|---|
| **Director** | Álvaro | Prioridades, decisiones de diseño, aprobar el final de cada hito, probar en su móvil y dar opinión, nombre y estética. **Tareas que solo él puede hacer** (abajo) |
| **Arquitecto / responsable técnico** | Claude · Opus 5.5 | Diseño técnico de los sistemas críticos, división de los hitos en tarjetas, revisión del código del núcleo de la simulación, depuración difícil, actualizar el GDD |
| **Desarrollador** | Claude · Sonnet 5.5 | Implementar las tarjetas, tests, scripts de Unity, interfaz, herramientas del editor, scripts de Blender y ComfyUI, builds, medición del rendimiento |
| **Asistente** | Claude · Haiku 5.5 | JSON de datos (edificios, estilos, políticas), traducciones, prompts de ComfyUI, descargas de assets y registro de licencias, procesado por lotes, changelog, explorar el código, resúmenes de logs |

**Tareas que solo puede hacer Álvaro** (requieren su identidad, su dinero o su criterio):
- Iniciar sesión con su **Unity ID** en el PC (la licencia Personal se activa una vez).
- Crear y verificar la **cuenta de desarrollador de Google Play** (25 $, verificación de identidad) y, más adelante, **Steamworks** (tarifa de Steam Direct).
- **Pagos** de cualquier tipo.
- Aceptar los avisos de permisos de las herramientas cuando aparezcan (Desktop Commander, Remote Control).
- **Probar en la mano:** cómo se siente el juego en el móvil (Claude puede instalar, lanzar y capturar por `adb`, pero no "sentir" el control).
- Reclutar probadores para la prueba cerrada de Google Play.
- Decisiones de diseño (las primeras ya están tomadas: ver [GDD §20.2](GDD.md#202-decisiones-tomadas-2026-10-28)).

**Todo lo demás lo hace Claude, también en el PC** (con el permiso de control que ha dado Álvaro), con las herramientas de la sección siguiente.

---

## 3. Herramientas: qué corre dónde

### 3.1 Mapa de herramientas

| Herramienta | Dónde corre | Cómo la controla Claude | Para qué |
|---|---|---|---|
| **GitHub** (repo `cazadoxs/CitySkilinesMovile`) | Nube | Git + MCP de GitHub | Código, documentos, PR, CI |
| **.NET SDK 8+** | Nube y PC | Bash / terminal | `Sim.Core`, tests, `Sim.Runner` |
| **GitHub Actions** | Nube de GitHub | Archivos YAML del repo | CI: build y tests de `Sim.Core` |
| **Figma** | Nube (conector) | MCP de Figma | Bocetos de la interfaz, componentes, iconos |
| **Canva / Adobe Express** | Nube (conector) | MCP de Canva / Adobe | Arte de tienda, capturas retocadas, logo, redes |
| **Unity 6 LTS** (Editor + Android Build Support) | PC | Skill `unity-cli` (CLI de Unity: abrir proyecto, ejecutar C# en el editor, builds, tests) + MCP de Unity si se configura | Proyecto del juego |
| **Blender** | PC | Terminal: `blender --background --python script.py` (MCP de Blender opcional) | Limpieza de assets, LOD, paleta, horneado VAT |
| **ComfyUI** | PC (GPU) | API HTTP local (`http://127.0.0.1:8188`) con scripts de Python o `curl` | Imágenes de referencia, imagen → 3D, iconos |
| **Android platform-tools (`adb`)** | PC + móvil por USB o Wi-Fi | Terminal | Instalar builds, lanzar, capturas, logs, medir fps |
| **Navegador** | PC | Skills `chrome-browser` / `computer-use` | Descargar assets que piden clics, revisar tiendas |
| **Generador de música** (`tools/music/`) | Nube y PC | Python + numpy + ffmpeg | Banda sonora original (GDD §14.1) |
| **Análisis de builds** (Unity Build Report, `bundletool`, APK Analyzer) | PC | Unity CLI / terminal (ver [13](#13-análisis-de-builds-y-sala-limpia)) | Medir qué ocupa espacio en nuestros APK y AAB |

### 3.2 Cómo se conecta Claude al PC (por orden de preferencia)

1. **Claude Code instalado en el PC** (app de escritorio o CLI) abierto en la carpeta del repo clonado. Es lo **más robusto**: acceso local completo a Unity, Blender, ComfyUI y `adb`, y las skills de Unity funcionan directamente.
   - Recomendado para todos los hitos con Unity (M2 en adelante).
   - Comparte el mismo repo de GitHub que las sesiones de la nube.
2. **Desktop Commander** (MCP, ya conectado a esta cuenta): permite a una sesión controlar el PC (terminal, archivos, procesos).
   - **Aviso:** en la sesión de planificación se ha desconectado y reconectado varias veces. No se depende de él para tareas largas.
3. **Remote Control** desde una sesión en la nube: conecta la sesión con una carpeta del PC.
   - **Limitación actual:** solo carpetas que sean la raíz de un repo Git en **Mac o Linux**. Si el PC es Windows, se usan las opciones 1 o 2.

### 3.3 Puesta a punto del PC (tarjeta M0-05)

Instalar o verificar:
- Git y Git LFS.
- .NET SDK 8+.
- Unity Hub + **Unity 6 LTS** con Android Build Support (SDK, NDK, OpenJDK).
- Blender (LTS).
- ComfyUI con un modelo imagen → 3D (**revisar su licencia de uso comercial**).
- Android platform-tools.
- Node.js (para herramientas auxiliares).
- Claude Code.

Después: clonar el repo y configurar el MCP de Unity. Álvaro solo inicia sesión en Unity Hub y activa la depuración USB en su móvil.

---

## 4. Modelos de IA y esfuerzo

### 4.1 Datos de referencia

| Modelo | Esfuerzos disponibles | Por defecto | Precio API orientativo (entrada/salida por 1M de tokens) | Coste relativo |
|---|---|---|---|---|
| **Haiku 5.5** | low, medium, high, xhigh, max | medium | 0,10 $ / 0,50 $ | **1×** |
| **Sonnet 5.5** | low, medium, high, xhigh, max | high | 2 $ / 10 $ | **≈ 20×** |
| **Opus 5.5** | low, medium, high, xhigh, max | medium | 4 $ / 20 $ | **≈ 40×** |

- En el **plan Pro** no se paga por token, pero el **límite de uso** se consume más rápido con los modelos grandes y con más esfuerzo. La proporción sirve de guía.
- Más esfuerzo = más "pensamiento" = más tokens. **Bajar el esfuerzo es el primer ahorro** antes de cambiar de modelo.
- ⚠️ En el plan Pro, el modelo **por defecto** de Claude Code es **Opus 5.5**. Hay que **cambiar a Sonnet o Haiku a propósito** con `/model` (ver [5](#5-chuleta-de-comandos)).

### 4.2 Reglas de asignación

| Modelo + esfuerzo | Cuándo | Cuándo NO |
|---|---|---|
| **Opus · medium** (el Opus por defecto) | Diseñar un sistema nuevo, dividir un hito en tarjetas, revisar el código del núcleo de la simulación antes de cerrar un hito, depurar un fallo que Sonnet no resolvió en 2 intentos, actualizar el GDD | Implementar código rutinario, datos, interfaz simple |
| **Opus · high** (solo si hace falta) | Algoritmos críticos con mucho riesgo: intersecciones y anti-bloqueo (M1-07), determinismo y formato de guardado (M5), bugs entre varios sistemas que Opus medium no resolvió | Todo lo demás |
| **Opus · xhigh / max** | **Nunca** salvo emergencia bloqueante tras fallar Opus high | — |
| **Sonnet · medium** (el caballo de batalla) | Implementar el 70 % de las tarjetas: sistemas, tests, Unity, interfaz, herramientas, scripts | Tareas puramente mecánicas (las hace Haiku) |
| **Sonnet · high** | Piezas complejas: pathfinding, IDM y carriles, mallas de carreteras, herramienta táctil de carreteras, instancing, edificios procedurales, guardado compacto | Arreglos pequeños |
| **Sonnet · low** | Arreglos pequeños y localizados, ajustes de estilo, renombrados, tests sueltos, revisar una captura | Diseño nuevo |
| **Haiku · medium** | JSON de datos, traducciones, prompts de ComfyUI, scripts sencillos, descargas y licencias, ejecutar pipelines ya escritos, resumir logs y CSV | Lógica de la simulación |
| **Haiku · low** | Buscar en el código, listar archivos, changelog, mensajes de commit, tareas de una línea | Cualquier cosa con decisiones |

**Escalado:** si un modelo falla 2 veces en la misma tarjeta, se sube **un** escalón: Haiku medium → Sonnet medium → Sonnet high → Opus medium → Opus high. Nunca se salta directo a max.

### 4.3 Tabla de tareas del proyecto → modelo

| Tarea típica | Modelo · esfuerzo |
|---|---|
| Escribir o actualizar el GDD y el plan; dividir un hito en tarjetas | Opus · medium |
| Diseño técnico de un sistema nuevo (nota de 1–2 páginas en `docs/adr/`) | Opus · medium |
| Diseño de intersecciones, anti-bloqueo y determinismo | Opus · high |
| Revisar el código de `Sim.Core` antes de cerrar un hito | Opus · medium |
| Implementar geometría, grafo, A\*, IDM, despacho, redes | Sonnet · medium / high |
| Mallas procedurales de carreteras y edificios | Sonnet · high |
| Herramientas táctiles (carreteras, zonas, plano) | Sonnet · high |
| Interfaz con UI Toolkit (paneles, menús) | Sonnet · medium |
| Shaders (VAT, tinte, capas, viento) | Sonnet · high |
| Tests unitarios y de propiedades | Sonnet · medium |
| Scripts de Blender (limpieza, LOD, paleta, VAT) | Sonnet · medium (escribirlos) → Haiku · medium (ejecutarlos por lotes) |
| Workflows y scripts de la API de ComfyUI | Sonnet · medium |
| Prompts de ComfyUI (cientos de piezas) | Haiku · medium |
| JSON de edificios, estilos, políticas, hitos | Haiku · medium (borrador) → Sonnet · low (revisión puntual) |
| Traducciones ES ↔ EN y otros idiomas | Haiku · medium |
| Descargar packs CC0 y registrar licencias | Haiku · medium |
| Builds de Android, instalar por `adb`, capturas, logs | Sonnet · low (la primera vez, medium) |
| Medir el rendimiento y resumir CSV | Haiku · medium → Sonnet · medium si hay que actuar |
| Bocetos en Figma | Sonnet · medium |
| Arte de tienda en Canva o Adobe | Sonnet · low |
| Changelog, notas de versión, mensajes de commit | Haiku · low |
| Explorar el código ("¿dónde se calcula X?") | Haiku · low (subagente `explorador`) |

### 4.4 Reparto orientativo del uso (plan Pro)

| Modelo | % aproximado de las sesiones | % aproximado del consumo |
|---|---|---|
| Haiku | 30 % | ≈ 3 % |
| Sonnet | 60 % | ≈ 65 % |
| Opus (casi siempre medium) | 10 % | ≈ 32 % |

**Señales de alarma:** si a mitad de semana se ha gastado más del 60 % del límite, se pasan las tarjetas de Opus a "pendiente de revisión" y se sigue solo con Sonnet medium y Haiku.

---

## 5. Chuleta de comandos

> Verificado con la documentación oficial de Claude Code (code.claude.com, configuración de modelos, subagentes y skills), consultada el 2026-10-28.

### 5.1 Elegir el modelo

```text
/model sonnet        ← cambia a Sonnet 5.5 en esta sesión (o abre el selector sin argumento)
/model haiku         ← Haiku 5.5
/model opus          ← Opus 5.5
/model opusplan      ← Opus en modo plan y Sonnet al ejecutar (muy útil: diseña Opus, implementa Sonnet)
/model default       ← vuelve al predeterminado de la cuenta (en Pro: Opus 5.5)
```

- En el selector de `/model`: **Enter** cambia y lo guarda como predeterminado; **s** cambia solo para esta sesión.
- Al arrancar desde la terminal:

```bash
claude --model sonnet --effort medium
```

### 5.2 Elegir el esfuerzo

```text
/effort              ← abre el deslizador
/effort low          ← low | medium | high | xhigh | max
/effort medium
/effort auto         ← borra el nivel guardado para el modelo activo
```

- Variable de entorno: `CLAUDE_CODE_EFFORT_LEVEL=medium`.
- En `settings.json`: `"effortLevel": "medium"` (acepta low/medium/high/xhigh, **no** max) o niveles por modelo en `modelSettings`.

### 5.3 Gestión del contexto y del uso

```text
/clear      ← vacía la conversación: úsalo SIEMPRE al cambiar de tarjeta
/compact    ← resume la conversación si una tarjeta se alarga
/context    ← muestra qué ocupa el contexto
/usage      ← uso del plan (alias: /cost)
```

- **Modo plan:** se activa con `Shift+Tab` (o pidiéndolo). Con `opusplan`, el plan lo hace Opus y la ejecución Sonnet.

### 5.4 Configuración recomendada del proyecto (`.claude/settings.json`)

```json
{
  "model": "sonnet",
  "effortLevel": "medium"
}
```

Así **cada sesión empieza en Sonnet medium** y solo se sube a Opus cuando la tarjeta lo pide. Es el mayor ahorro posible.

---

## 6. Skills y subagentes del proyecto

Se crean en la tarjeta **M0-04**. Las skills (`.claude/skills/<nombre>/SKILL.md`) y los subagentes (`.claude/agents/<nombre>.md`) pueden **fijar su propio modelo y esfuerzo** en el frontmatter (`model`, `effort`). Así cada tipo de trabajo usa automáticamente el modelo barato que le toca.

### 6.1 Skills (se invocan con `/nombre`)

**`/tarea <ID>`**: ejecuta una tarjeta del plan.

```markdown
---
name: tarea
description: Ejecuta una tarjeta de tarea de docs/tareas por su ID (por ejemplo M1-05) siguiendo sus criterios de aceptación.
argument-hint: "[ID-de-tarjeta]"
model: sonnet
effort: medium
---
Lee SOLO la tarjeta $0 en docs/tareas/ y los archivos que cita (no leas el GDD entero salvo la sección que indique).
1. Implementa lo pedido con el mínimo cambio necesario.
2. Ejecuta la verificación de la tarjeta (tests/benchmark/build).
3. Actualiza la tarjeta: estado, notas de traspaso (qué se hizo, qué queda, problemas).
4. Commit con mensaje "feat(<área>): <resumen> [$0]".
Si fallas 2 veces, para y escribe en la tarjeta "ESCALAR: <motivo>".
```

**`/disenar <tema>`**: diseño técnico breve.

```markdown
---
name: disenar
description: Diseña un sistema o divide un hito en tarjetas. Produce un ADR corto en docs/adr/ o tarjetas en docs/tareas/.
argument-hint: "[sistema o hito]"
model: opus
effort: medium
---
Diseña $ARGUMENTS. Lee solo las secciones relevantes del GDD.
Salida: un ADR (máx. 2 páginas: contexto, decisión, alternativas descartadas en 1 línea cada una, consecuencias)
o tarjetas con el formato de PLAN_DE_TRABAJO §10 (objetivo, archivos, modelo+esfuerzo, dependencias, aceptación, verificación).
No implementes código.
```

**`/datos <qué>`**: datos JSON, traducciones, prompts.

```markdown
---
name: datos
description: Genera o actualiza datos del juego (JSON de edificios, estilos, políticas, traducciones, prompts de ComfyUI) validando contra los esquemas.
argument-hint: "[qué datos]"
model: haiku
effort: medium
---
Genera $ARGUMENTS en unity/URBE/Assets/_Project/Data/ (o tools/comfyui/prompts/).
Valida con el esquema JSON correspondiente (tools/scripts/validate-data). No toques código C#.
```

**`/revisar [área]`**: revisión de código antes de cerrar.

```markdown
---
name: revisar
description: Revisa los cambios de la rama actual buscando bugs, rendimiento (asignaciones en bucles calientes) y desviaciones del GDD.
model: sonnet
effort: medium
---
Revisa el diff contra main. Prioriza: bugs, asignaciones en bucles calientes de Sim.Core, determinismo, desviaciones del GDD.
Si el área es "sim-core", indica al final: "Recomendado revisar con Opus" cuando haya cambios en tráfico, intersecciones o guardado.
```

**`/assets <lote>`**: ejecuta el pipeline de assets ya escrito.

```markdown
---
name: assets
description: Ejecuta el pipeline de assets (descarga CC0, Blender headless, ComfyUI, validación) sobre un lote y registra licencias.
argument-hint: "[lote o carpeta]"
model: haiku
effort: medium
---
Procesa el lote $ARGUMENTS con los scripts de tools/ (no los modifiques).
Registra cada archivo en ASSETS_LICENSES.md. Genera una hoja de contactos PNG para revisión visual.
Si un script falla, para y reporta el error (no lo arregles: eso es una tarjeta para Sonnet).
```

**`/medir`**: rendimiento.

```markdown
---
name: medir
description: Ejecuta los benchmarks (Sim.Runner --bench y/o escenas de referencia por adb) y resume los resultados frente a los presupuestos del GDD §15.
model: haiku
effort: medium
---
Ejecuta los benchmarks indicados, compara con los presupuestos (GDD §8.15 y §15.1) y escribe un resumen corto en docs/perf/AAAA-MM-DD.md.
Marca en rojo lo que supere el presupuesto.
```

### 6.2 Subagentes (Claude los usa por su cuenta o se piden por nombre)

**`explorador`**: búsquedas baratas.

```markdown
---
name: explorador
description: Busca en el código y la documentación dónde está algo y devuelve rutas y fragmentos cortos. Úsalo antes de leer archivos grandes.
tools: Read, Grep, Glob
model: haiku
effort: low
---
Encuentra lo pedido y devuelve solo: rutas, líneas y un resumen de 3 frases. No propongas cambios.
```

**`documentador`**: mantiene los documentos al día.

```markdown
---
name: documentador
description: Actualiza documentación (tarjetas, changelog, README, ASSETS_LICENSES) a partir de cambios ya hechos.
tools: Read, Grep, Glob, Edit, Write
model: haiku
effort: low
---
Actualiza solo la documentación indicada, con frases cortas. No cambies código.
```

**`probador`**: tests.

```markdown
---
name: probador
description: Ejecuta dotnet test y benchmarks; si falla algo trivial (nombre, import, assert desactualizado por un cambio intencionado) lo arregla, si no, lo reporta.
tools: Read, Grep, Glob, Edit, Bash
model: sonnet
effort: low
---
Ejecuta los tests. Arregla solo fallos triviales. Para el resto, devuelve: test, error, causa probable.
```

**`revisor-sim`**: revisión experta del núcleo.

```markdown
---
name: revisor-sim
description: Revisión experta de cambios en Sim.Core (tráfico, intersecciones, pathfinding, determinismo, guardado). Solo al cerrar hitos o ante bugs difíciles.
tools: Read, Grep, Glob
model: opus
effort: medium
---
Revisa los cambios de Sim.Core indicados. Devuelve una lista priorizada: bug real (con escenario que lo dispara), riesgo de rendimiento, riesgo de determinismo.
```

---

## 7. Protocolo de sesión

### 7.1 Antes de empezar

1. Elegir la **siguiente tarjeta disponible** (todas sus dependencias ✅) del hito actual.
2. Abrir la sesión donde indica la tarjeta (☁️ nube / 🖥️ PC).
3. Poner el **modelo y el esfuerzo** de la tarjeta (`/model …`, `/effort …`) o usar la skill `/tarea <ID>`, que ya los fija.
4. **Plantilla de arranque** (si no se usa `/tarea`):

```text
Tarjeta M1-05 (docs/tareas/M1-05.md). Lee solo esa tarjeta y los archivos que cita.
Objetivo: <copiar objetivo>. Criterio de aceptación: <copiar>.
No leas el GDD completo; si necesitas contexto, lee solo la sección indicada.
Al terminar: ejecuta la verificación, actualiza la tarjeta con notas de traspaso y haz commit.
```

### 7.2 Durante

- **Una tarjeta por sesión.** Si aparece trabajo nuevo, se crea otra tarjeta; no se amplía la actual.
- **Commits pequeños** con formato `tipo(área): resumen [ID]`. Ejemplos: `feat(sim): A* con caché [M1-05]`, `fix(ui): lupa en zurdos [M2-04]`.
- **Tests antes de cerrar.** Si no hay forma de testear, se explica en la tarjeta por qué.

### 7.3 Al terminar

1. Actualizar la tarjeta:
   - **Estado:** ✅ hecho / ⏸️ bloqueado / 🔺 escalar.
   - **Notas de traspaso:** qué se hizo, qué queda, decisiones tomadas, trampas descubiertas. Máximo 10 líneas.
2. Commit y push a la rama de trabajo.
3. `/clear` antes de la siguiente tarjeta.

### 7.4 Definición de "hecho"

- [ ] Cumple los criterios de aceptación de la tarjeta.
- [ ] Tests nuevos o actualizados en verde (`dotnet test` o tests de Unity).
- [ ] Sin asignaciones de memoria nuevas en los bucles calientes de `Sim.Core` (si aplica).
- [ ] Rendimiento dentro del presupuesto (si la tarjeta lo menciona).
- [ ] Datos nuevos validados con su esquema.
- [ ] Assets nuevos registrados en `ASSETS_LICENSES.md`.
- [ ] Tarjeta actualizada con las notas de traspaso.

### 7.5 Ramas y revisiones

- **`main`:** siempre compila y pasa los tests.
- **Ramas de trabajo:** `feat/<ID>-<slug>` (por ejemplo, `feat/M1-05-astar`). Las sesiones en la nube usan la rama que les asigna la plataforma.
- **Pull requests:** uno por tarjeta o grupo pequeño. CI en verde + `/revisar` (Sonnet). En los cambios de tráfico, intersecciones o guardado, además el subagente `revisor-sim` (Opus medium).
- **Álvaro** aprueba **el final de cada hito** (demo jugable), no cada PR, para no frenar el ritmo. Puede revisar cualquier PR si quiere.

---

## 8. Reglas de ahorro de tokens

1. **Sonnet medium por defecto** en el proyecto (`.claude/settings.json`). Opus solo cuando la tarjeta lo dice.
2. **Opus en medium.** High solo en las tarjetas marcadas como críticas. Nunca xhigh ni max salvo emergencia.
3. **`/clear` entre tarjetas.** El contexto viejo se paga en cada mensaje.
4. **Tarjetas autocontenidas:** la tarjeta cita los archivos y la sección exacta del GDD. El GDD completo no se lee nunca entero en una tarea.
5. **Buscar con el subagente `explorador`** (Haiku) antes de leer archivos grandes.
6. **Citar rutas, no pegar archivos** en el chat.
7. **Datos en JSON** para que los cambie Haiku sin tocar código.
8. **Scripts reutilizables:** Sonnet escribe el script una vez y Haiku lo ejecuta cien veces (assets, validaciones, benchmarks).
9. **`opusplan`** para las tarjetas que necesitan un plan cuidadoso: Opus planifica y Sonnet ejecuta en la misma sesión.
10. **No pedir "explícame todo" a Opus.** Pedir decisiones concretas y cortas (ADR de 1–2 páginas).
11. **Agrupar lo trivial:** varios arreglos minúsculos en una sola sesión de Haiku low o Sonnet low.
12. **Revisar `/usage` cada pocos días** y aplicar la alarma de [4.4](#44-reparto-orientativo-del-uso-plan-pro).
13. **Las salidas largas** (logs, CSV) se resumen con Haiku antes de dárselas a Sonnet u Opus.
14. **No usar plantillas de "decenas de agentes"** (como Claude Code Game Studios, con unos 48 agentes): multiplican el consumo. Nos quedamos con 4 skills y 4 subagentes ligeros.

---

## 9. Orden de trabajo: hitos

```
M0 Fundación ──► M1 Tráfico sin motor ──► M2 Porción jugable Unity ──► M3 Ciudad que crece
                                                                          │
M7 Lanzamiento ◄── M6 Arte, vida y rendimiento ◄── M5 Economía, guardado y menús ◄── M4 Servicios y consecuencias
```

| Hito | Objetivo | Demo que lo cierra (la prueba Álvaro) | Dónde | Modelo principal | Sesiones estimadas* |
|---|---|---|---|---|---|
| **M0** Fundación | Documentos, PC preparado, skills, bocetos, packs CC0 | Bocetos de Figma + PC listo + repo organizado | ☁️ + 🖥️ | Opus medium (docs), Sonnet, Haiku | 8–10 |
| **M1** Tráfico sin motor | `Sim.Core`: red viaria, rutas, coches con IDM, intersecciones, atascos, visor PNG | GIF o PNG de 2.000 coches en una cuadrícula: se forma un atasco en un cuello de botella y se resuelve al añadir una vía | ☁️ | Sonnet medium/high + Opus en lo crítico | 15–20 |
| **M2** Porción jugable | Unity: cámara táctil, dibujar carreteras, coches instanciados, capa de tráfico, build en Android | En **su móvil**: dibujar calles con el dedo y ver coches y atascos a 30 fps | 🖥️ | Sonnet high/medium | 15–20 |
| **M3** Ciudad que crece | Zonas, edificios procedurales (2 estilos), ciudadanos y rutinas, agua y luz bajo las calles, demanda RCI | Ciudad de 10.000 habitantes que crece sola con variedad visible | ☁️ + 🖥️ | Sonnet high, Opus medium (diseño) | 20–25 |
| **M4** Servicios y consecuencias | Despacho de servicios, isócronas, basura, salud, bomberos, policía, muerte, campos, cadena de causas, 25 capas, bus | Provocar un atasco y ver cómo un incendio se propaga, con la cadena de causas | ☁️ + 🖥️ | Sonnet medium/high | 25–30 |
| **M5** Economía, guardado y menús | Economía, hitos, guardado compacto, todos los menús y ajustes, tutorial, ES/EN, tranvía y metro, modo plano y plantillas | Partida completa de 1 h: guardar, cerrar, cargar (< 15 s), con menús completos | ☁️ + 🖥️ | Sonnet medium, Opus high (guardado) | 25–30 |
| **M6** Arte, vida y rendimiento | 5 estilos, VAT, día y noche, clima, audio, gamas, optimización, escenas de referencia, **prueba cerrada en Google Play** | 100.000 habitantes a 30 fps en gama media; probadores externos jugando | 🖥️ | Sonnet + Haiku (assets) | 30–40 |
| **M7** Lanzamiento | Compra única, demo, ficha de tienda, Steam e itch, correcciones, marketing | Juego publicado en Google Play | 🖥️ + ☁️ | Sonnet medium, Haiku | 10–15 |

\* *Sesión = una tarjeta (normalmente 30–90 minutos). Son estimaciones para planificar el uso del plan Pro, no plazos.*

**Lista "después del lanzamiento"** (no bloquea la 1.0): editor de mapas, mods de datos (móvil y PC), 3 estilos más (Asia densa, Modernista, Eco-futurista) y más mapas, trolebús, desastres avanzados, taller online, música con licencia (si se decide). **iOS: descartado.**

---

## 10. Tarjetas de tarea detalladas (M0–M2)

**Formato de tarjeta:** `ID · Título` | **Modelo · esfuerzo** | **Dónde** | **Depende de** | **Aceptación / verificación**.
En la tarjeta M0-09 se copian a `docs/tareas/<ID>.md` (un archivo por tarjeta) para que `/tarea <ID>` las lea sin cargar este documento.

### 10.1 M0 — Fundación

| ID | Tarea | Modelo · esfuerzo | Dónde | Depende | Aceptación / verificación |
|---|---|---|---|---|---|
| M0-01 | Escribir el GDD y el Plan de Trabajo | Opus · medium | ☁️ | — | ✅ Estos dos documentos |
| M0-02 | Revisión del GDD por Álvaro y decisiones abiertas (§20.2) | **Álvaro** (+ Opus · medium para aplicar los cambios) | — | M0-01 | ✅ Decisiones aplicadas; GDD 0.2 |
| M0-03 | `CLAUDE.md` (< 100 líneas: resumen, reglas, mapa de docs, regla de modelos) + `README.md` | Haiku · medium | ☁️ | M0-02 | Ambos archivos existen; `CLAUDE.md` tiene < 100 líneas |
| M0-04 | Crear `.claude/settings.json`, las skills (§6.1) y los subagentes (§6.2) | Sonnet · low | ☁️ | M0-03 | `/tarea`, `/disenar`, `/datos`, `/revisar`, `/assets`, `/medir` aparecen en el menú `/`; los subagentes aparecen en `/agents` |
| M0-05 | Puesta a punto del PC (§3.3) + clonar el repo + MCP de Unity | Sonnet · medium (🖥️ vía Desktop Commander o Claude Code local); **Álvaro:** login en Unity Hub y depuración USB | 🖥️ | — | Informe con versiones instaladas; `unity --version`, `blender --version`, `adb devices` y `dotnet --version` responden; ComfyUI responde en :8188 |
| M0-06 | Bocetos en Figma: menú principal, HUD, barra de construcción abierta, panel de edificio con cadena de causas, herramienta de carreteras, capas + **2 bocetos en vertical** (HUD y panel) | Sonnet · medium | ☁️ (Figma) | M0-02 | Archivo de Figma con 8 pantallas y componentes base; enlace en `docs/` |
| M0-07 | Descargar los packs CC0 iniciales (Kenney City Kit Roads/Suburban/Commercial/Industrial, Car Kit; Quaternius ciudad y personajes; KayKit City Builder Bits), comprobar licencias y crear `ASSETS_LICENSES.md` | Haiku · medium | 🖥️ | M0-05 | Packs en `art_source/` (fuera de Unity, con Git LFS o ignorados) + registro completo |
| M0-08 | Esqueleto de CI de GitHub Actions para `dotnet build/test` | Haiku · medium | ☁️ | M1-01 | El workflow pasa en verde en un PR |
| M0-09 | Copiar las tarjetas de M1 y M2 a `docs/tareas/<ID>.md` | Haiku · low | ☁️ | M0-04 | Un archivo por tarjeta con el formato de la tabla |
| M0-10 | Banda sonora original v1: generador `tools/music/`, 6 pistas + 2 jingles, página de escucha | Opus · medium (esta sesión) | ☁️ | M0-02 | ✅ Pistas en `audio/music/`; verificación automática (picos, sonoridad, bucles) superada |

### 10.2 M1 — Tráfico sin motor (todo en la nube)

| ID | Tarea | Modelo · esfuerzo | Depende | Aceptación / verificación |
|---|---|---|---|---|
| M1-01 | Solución `.NET`: `Sim.Core` (netstandard2.1), `Sim.Tests` (xUnit), `Sim.Runner` (consola). RNG PCG32, ids con generación, `Directory.Build.props` (nullable, warnings como errores) | Sonnet · low | M0-04 | `dotnet test` en verde con un test de ejemplo; RNG reproducible con semilla |
| M1-02 | Geometría: Bézier cúbica, longitud por arco (tabla de muestreo), punto y tangente por distancia, desplazamiento lateral (carriles), intersección de curvas | Sonnet · medium | M1-01 | Tests con valores conocidos; error de longitud < 0,1 % |
| M1-03a | **Diseño (ADR):** estructuras de red viaria, grafo de carriles, conexiones automáticas, época de la red, índice espacial | Opus · medium | M1-01 | `docs/adr/001-red-viaria.md` (≤ 2 páginas) |
| M1-03b | Implementar la red viaria según el ADR 001 | Sonnet · high | M1-02, M1-03a | Tests: crear, dividir y unir segmentos, cruces en T y en X, conexiones de carril correctas |
| M1-04 | Comandos `BuildRoad`, `Demolish` con `Validate`/`Apply`/inverso (deshacer) | Sonnet · medium | M1-03b | Tests: construir → deshacer → el hash de estado es igual al original |
| M1-05 | Pathfinding: A\* sobre segmentos con giros, coste por tiempo + penalizaciones, caché por época, cola con presupuesto | Sonnet · high | M1-03b | Tests de rutas óptimas en grafos de prueba; benchmark: 1.000 rutas en una cuadrícula de 50×50 < 50 ms en la nube |
| M1-06a | **Diseño (ADR):** modelo de vehículo (IDM), líder y *lookahead*, actualización por carril, snapshot | Opus · medium | M1-03b | `docs/adr/002-vehiculos.md` |
| M1-06b | Implementar el movimiento con IDM | Sonnet · high | M1-06a, M1-05 | Tests: no hay solapes; frenada correcta tras un vehículo parado; velocidad libre = límite |
| M1-07 | **Intersecciones:** máscara de conflictos, prioridad, reserva, anti-bloqueo, semáforos con fases fijas | **Opus · high** (diseño + implementación del núcleo; tarjeta crítica) | M1-06b | Tests de propiedades: 10.000 ticks aleatorios sin colisiones en el cruce ni bloqueos permanentes en un cruce simple |
| M1-08 | Elección de carril anticipada + cambio de carril con aceptación de hueco | Sonnet · high | M1-07 | Test: en un cruce con giro a la izquierda, ≥ 95 % de los vehículos llegan al carril correcto sin parar |
| M1-09 | Generador de viajes de prueba (orígenes y destinos aleatorios con pesos) + métrica "flujo de tráfico %" | Sonnet · medium | M1-06b | Métrica estable; CSV por minuto simulado |
| M1-10 | Visor PNG/GIF en `Sim.Runner --render` (carreteras, vehículos coloreados por velocidad, capa de flujo) con **SkiaSharp** (licencia MIT) | Sonnet · medium | M1-06b | Genera `out.png` y `out.gif` de 20 s simulados |
| M1-11 | Detección y resolución de *gridlock* (paso forzado → re-ruta → desaparición según la opción) | Sonnet · high | M1-07 | Escenario de bloqueo provocado: se resuelve en < 60 s simulados |
| M1-12 | Tests de repetición (golden) + `Sim.Runner --bench` + informe | Sonnet · medium | M1-09 | Golden reproducible; benchmark en `docs/perf/` |
| M1-13 | Revisión de cierre de M1 (subagente `revisor-sim`) y lista de deuda técnica | Opus · medium | M1-12 | Lista priorizada; arreglos críticos convertidos en tarjetas |
| M1-14 | **Demo M1:** GIF de 2.000 coches con un atasco y su solución | Haiku · low (ejecutar y publicar) | M1-13 | GIF en el PR y en el devlog; Álvaro aprueba |

### 10.3 M2 — Porción jugable en Unity (PC)

| ID | Tarea | Modelo · esfuerzo | Depende | Aceptación / verificación |
|---|---|---|---|---|
| M2-01 | Crear el proyecto de Unity 6 LTS (URP móvil, Input System, UI Toolkit, Burst, Collections, Addressables), estructura de carpetas (GDD §19.3) e integración de `Sim.Core` como paquete local o DLL | Sonnet · medium | M0-05, M1-12 | El proyecto abre sin errores; un test de PlayMode crea un `SimState` |
| M2-02 | `SimHost`: bucle de ticks, velocidades, cola de comandos, snapshots, interpolación | Sonnet · medium | M2-01 | 10 ticks por segundo estables; la interpolación es suave a 60 fps en el editor |
| M2-03 | Cámara: gestos táctiles (GDD §11.3) + ratón y teclado, límites e inercia | Sonnet · medium | M2-01 | Probado en el editor (simulador de dispositivo) y en el móvil |
| M2-04a | **Diseño (ADR):** generación de mallas de carreteras e intersecciones y cacheo | Opus · medium | M1-03b | `docs/adr/003-mallas-carreteras.md` |
| M2-04b | Generación de mallas de segmentos e intersecciones (perfil extruido, aceras, marcas viales en el atlas) | Sonnet · high | M2-04a | Cruces en T, X y Y y curvas sin huecos ni solapes visibles; regeneración local < 5 ms |
| M2-05 | Herramienta de carreteras táctil: fantasma, recta, curva y libre, ajuste a 15° y guías, cursor desplazado, lupa, ✓, coste en vivo, motivos de rojo | Sonnet · high | M2-04b, M2-02 | Dibujar 20 calles en el móvil sin errores de toque; las validaciones se muestran en texto |
| M2-06 | Render instanciado de vehículos: comparar BatchRendererGroup frente a `RenderMeshInstanced` en el dispositivo; usar el mejor | Sonnet · high | M2-02 | 3.000 vehículos con < 10 draw calls; medido en el móvil de Álvaro |
| M2-07 | Importar y adaptar a la paleta los vehículos CC0 (Kenney Car Kit) con el script de Blender | Sonnet · medium (script) → Haiku · medium (lote) | M0-07 | 10 vehículos con LOD y tinte; registrados en las licencias |
| M2-08 | HUD mínimo en UI Toolkit: velocidad, pausa, contador de vehículos, flujo % | Sonnet · medium | M2-02 | Funciona en móvil y PC con escalado |
| M2-09 | Capa de tráfico: color por tramo según el flujo | Sonnet · medium | M2-04b | Se ve el atasco en rojo en tiempo real |
| M2-10 | Build Android (AAB y APK de pruebas, IL2CPP ARM64, API objetivo 36, Vulkan), instalar por `adb`, capturar el fps con el panel de depuración | Sonnet · medium | M2-05, M2-06 | APK instalado en el móvil de Álvaro; informe de fps |
| M2-11 | **Prueba en la mano** y lista de sensaciones (gestos, precisión, legibilidad) | **Álvaro** + Haiku · low (convertir notas en tarjetas) | M2-10 | Lista de mejoras convertida en tarjetas |
| M2-12 | Revisión de cierre de M2 + **demo** (vídeo corto grabado por `adb screenrecord`) | Sonnet · medium | M2-11 | Vídeo de 30 s en el devlog; Álvaro aprueba |

---

## 11. Épicas de M3 a M7

Al empezar cada hito, **Opus · medium** ejecuta `/disenar M<n>` y convierte estas épicas en tarjetas con el formato anterior.

### M3 — Ciudad que crece

| Épica | Contenido (GDD) | Modelo principal |
|---|---|---|
| Zonas | Celdas a los lados de las carreteras, pinceles, demanda RCI (§4.4, §5.4, §8.12) | Sonnet · medium |
| Edificios procedurales | Gramática de forma, ensamblado en jobs, instancias por pieza, 2 estilos (Mediterráneo y Ensanche) (§6.2–6.6) | Opus · medium (ADR) → Sonnet · high |
| Kit modular de 2 estilos | ~110 piezas: CC0 adaptado + ComfyUI + Blender (§13.5–13.6) | Sonnet · medium (scripts) → Haiku · medium (lotes) |
| Ciudadanos y rutinas | Structs, hogares, viajes, tráfico híbrido (§8.3) | Sonnet · high + `revisor-sim` |
| Redes bajo las calles | Agua, alcantarillado y electricidad automáticas (§8.10) | Sonnet · medium |
| Peatones (sin VAT todavía) | Grafo de peatones y cápsulas simples (§8.5) | Sonnet · medium |
| Demo M3 | 10.000 habitantes creciendo solos con variedad | Haiku · low |

### M4 — Servicios y consecuencias

| Épica | Contenido (GDD) | Modelo principal |
|---|---|---|
| Despacho de servicios | Peticiones, asignación por coste real, capacidades (§8.9) | Sonnet · high |
| Isócronas | Dijkstra con límite de tiempo, repartido en el tiempo (§8.9) | Sonnet · medium |
| Servicios | Basura, salud, muerte, bomberos (propagación del fuego), policía (§4.7) | Sonnet · medium |
| Campos de rejilla | Contaminación (3), ruido, valor del suelo, crimen (§8.11) | Sonnet · medium (+ Burst si el profiler lo pide) |
| Cadena de causas | `CauseRecord`, frases, botones "ir a" (§8.8) | Opus · medium (diseño) → Sonnet · medium |
| 25 capas de información | Texturas de campo, colores por tramo y por edificio (§10) | Sonnet · medium |
| Herramientas de tráfico | Inspector, explorador de rutas, editor de cruces, asesor (§9) | Sonnet · high |
| Autobús | Líneas, paradas, cocheras, multimodal (§4.8) | Sonnet · high |
| Interfaz de información | Paneles de edificio, ciudadano, vehículo y distrito (§11.5) | Sonnet · medium |

### M5 — Economía, guardado y menús

| Épica | Contenido (GDD) | Modelo principal |
|---|---|---|
| Economía | Impuestos, presupuestos, préstamos, informes (§4.10, §8.13) | Sonnet · medium |
| Hitos y desbloqueos | Tabla de hitos, casillas de mapa (§3.3) | Haiku · medium (datos) + Sonnet · low |
| **Guardado compacto** | Formato por bloques, empaquetado de bits, semillas, Zstd, migraciones, escritura atómica (§16) | **Opus · high** (diseño) → Sonnet · high |
| Menús completos | Mapa de pantallas, ajustes, pausa, cargar partida (§12.1–12.5) | Sonnet · medium |
| Interfaz vertical | Distribución secundaria en vertical, hojas deslizables, rotación sin perder el estado (§11.2) | Sonnet · medium |
| Modo plano, plantillas, copiar y pegar, simetría | (§5.6–5.7) | Sonnet · high |
| Distritos y políticas | (§4.6) | Sonnet · medium |
| Tranvía y metro | (§4.8) | Sonnet · medium |
| Tutorial y "¿Cómo funciona?" | (§11.11) | Sonnet · medium + Haiku (textos) |
| Localización ES/EN | (§11.10) | Haiku · medium |
| Balanceo con ciudades bot | `Sim.Runner` multi-año, ajuste de JSON (§8.16) | Haiku · medium (ejecutar) + Opus · medium (interpretar 1 vez) |

### M6 — Arte, vida y rendimiento

| Épica | Contenido (GDD) | Modelo principal |
|---|---|---|
| 3 estilos más (5 en total) | Kits modulares (§6.4) | Haiku · medium (lotes) + Sonnet · low (revisión) |
| Servicios y monumentos con modelo final | ~80 edificios únicos (§12.8) | Haiku · medium (pipeline) + Sonnet · medium |
| VAT de peatones | Horneado en Blender, shader, instancing, LOD de animación (§7) | Sonnet · high |
| Shaders de vida | Tintes, suciedad, ventanas de noche, viento, agua, nieve (§6.6, §7.4) | Sonnet · high |
| Día y noche, clima | (§4.14) | Sonnet · medium |
| Audio | Integrar la banda sonora con capas adaptativas (`render.py --stems`), ampliar a 8–10 pistas con el generador, ambiente por densidad, tráfico agregado (§14) | Sonnet · medium (sistema) + Haiku · medium (renderizar pistas nuevas, buscar efectos CC0) |
| Gamas y optimización | Detección, escalones, impostores, culling, memoria (§15) | Sonnet · high + `/medir` (Haiku) |
| Escenas de referencia | 25.000 / 50.000 / 100.000 / 200.000 habitantes con cámara grabada, CSV por `adb` (§15.6) | Sonnet · medium |
| Análisis del tamaño del build | Unity Build Report, `bundletool` y APK Analyzer sobre nuestros AAB (§13 de este plan) | Sonnet · medium |
| **Prueba cerrada en Google Play** | AAB, ficha mínima, más de 12 probadores durante 14 días (verificar la cifra vigente) | Sonnet · medium + **Álvaro** (cuenta, probadores) |

### M7 — Lanzamiento

| Épica | Contenido | Modelo principal |
|---|---|---|
| Compra única y demo | Unity IAP (Google Play Billing), límites de la demo, caché de la licencia offline (§17–18) | Sonnet · high |
| Ficha de tienda | Textos ES/EN, capturas en modo foto, icono, gráfico destacado, tráiler (Canva / Adobe) | Haiku · medium (textos) + Sonnet · low (arte) |
| Formularios de Google Play | Seguridad de datos, clasificación de contenido, público objetivo | Sonnet · low + **Álvaro** (envío) |
| PC: Steam e itch | Builds de Windows y Linux, Steamworks.NET (logros, nube), página de Steam | Sonnet · medium + **Álvaro** (cuentas) |
| Correcciones finales | Lista de bugs de la prueba cerrada | Sonnet · medium / Opus · medium para los difíciles |
| Devlog y marketing | Publicaciones, clips cortos | Haiku · medium |
| **Nombre definitivo** | Lluvia de ideas, comprobar que no esté registrado en las tiendas ni como marca evidente, decisión final | Haiku · medium (ideas) + **Álvaro** (decide) |

---

## 12. Pipeline de assets paso a paso

| Paso | Qué | Quién (modelo · esfuerzo) | Herramienta | Salida |
|---|---|---|---|---|
| 1 | Elegir los packs y piezas necesarios para el estilo o lote | Sonnet · low | Lectura del GDD §6, §12.8 | Lista del lote (`tools/batches/<lote>.json`) |
| 2 | Descargar los packs CC0 y **verificar la licencia** en la página oficial | Haiku · medium | Terminal (`curl`) o navegador en el PC | `art_source/<pack>/` + filas en `ASSETS_LICENSES.md` |
| 3 | Generar las referencias que faltan | Haiku · medium (prompts) | API de ComfyUI | Imágenes en `art_source/gen/<lote>/` |
| 4 | Imagen → 3D de las piezas generadas | Haiku · medium (ejecutar el workflow) | ComfyUI (modelo con licencia comercial válida) | `.glb` en bruto |
| 5 | Limpieza y normalización | Haiku · medium (ejecutar) / Sonnet · medium (si hay que tocar el script) | `blender --background --python tools/blender/normalize.py` | `.fbx` / `.glb` limpios + JSON de metadatos |
| 6 | Validación automática + hoja de contactos | Haiku · medium | `tools/scripts/validate_assets.py` + render de miniaturas en Blender | Informe + `contact_sheet.png` |
| 7 | Revisión visual | Sonnet · low (mira la hoja de contactos); en caso de duda, **Álvaro** | Imagen | Aprobado / rehacer |
| 8 | Importación en Unity | Automática (AssetPostprocessor escrito por Sonnet en M2/M3) | Unity CLI | Prefabs de piezas + catálogo actualizado |
| 9 | Animaciones VAT | Haiku · medium (lote) | `tools/blender/bake_vat.py` → importador VAT | Texturas VAT + datos de clips |

**Regla:** si un paso automático falla, **Haiku no arregla el script**: crea una tarjeta para Sonnet. Así los scripts no se corrompen con parches baratos.

### 12.1 Música

La banda sonora sale de `tools/music/` (GDD §14.1). Cada pista es una definición en `tools/music/tracks.py`: tonalidad, tempo, progresión, secciones, instrumentos y semilla.

| Acción | Comando | Quién (modelo · esfuerzo) |
|---|---|---|
| Regenerar todas las mezclas | `python3 tools/music/render.py --all` | Haiku · low |
| Exportar las capas para Unity | `python3 tools/music/render.py --all --stems` | Haiku · low |
| Ajustar una pista según la opinión de Álvaro ("más lenta", "menos batería", "otra melodía") | Cambiar parámetros o semilla en `tracks.py` y renderizar esa pista: `--track <id>` | Sonnet · low |
| Añadir pistas nuevas | Nueva definición en `tracks.py` (Sonnet · low la escribe) → renderizar y verificar (Haiku · medium) | Sonnet · low + Haiku · medium |
| Instrumentos o efectos nuevos en el sintetizador | Cambios en `synth.py` / `mix.py` | Sonnet · medium |

Toda pista nueva pasa la verificación automática (`render.py --check`): picos, sonoridad, unión del bucle y duración.

---

## 13. Análisis de builds y sala limpia

**Decisión del director:** no se usan herramientas de ingeniería inversa (REA descartado).

**Para medir y reducir el tamaño de nuestros builds** (objetivo ≤ 150 MB, GDD §16.1) bastan herramientas estándar:
- **Unity Build Report:** el informe del editor tras cada build, con lo que ocupa cada asset, textura, malla y audio.
- **`bundletool`** (Google): tamaño de descarga real por dispositivo a partir del AAB (`bundletool get-size total`).
- **APK Analyzer** de Android Studio: contenido del APK, librerías nativas y recursos.
- **Símbolos de IL2CPP:** para leer los crashes de **nuestras** builds en la consola de Google Play.

**Sala limpia:**
- Para inspirarnos en Cities: Skylines usamos su **comportamiento observable**, la documentación pública y la experiencia de la comunidad.
- Escribimos nuestra propia implementación.
- No se descompilan juegos comerciales ni se extraen assets de juegos o apps de terceros.
- No se copia código GPL (GDD §2.3).

---

## 14. Riesgos de producción

| Riesgo | Señal | Mitigación |
|---|---|---|
| Quedarse sin uso del plan Pro a mitad de semana | `/usage` > 60 % antes del jueves | Solo Sonnet medium y Haiku hasta el reinicio; las tarjetas de Opus esperan (§4.4) |
| Las tarjetas crecen sin control | Sesiones de más de 2 h con la misma tarjeta | Partir la tarjeta; aplicar `/compact` o `/clear` |
| Desconexiones del control del PC | Desktop Commander se cae | Usar Claude Code local en el PC (§3.2) |
| Opus usado para cosas rutinarias | Mucho consumo con poco avance | `.claude/settings.json` con Sonnet por defecto; las skills fijan el modelo |
| Calidad irregular de los assets de IA | Hojas de contactos rechazadas | Más peso de los packs CC0 y del modelado manual en las piezas clave |
| Alcance de la 1.0 desmesurado | Hitos que duplican su estimación | Pasar épicas a la lista "después del lanzamiento" (§9) |
| Rendimiento real peor que el estimado | El informe de M2-10 por debajo de 30 fps | Ajustar gamas y presupuestos antes de M3; nunca más tarde |
| Cambio de requisitos de las tiendas | Avisos en la consola de Google Play | Revisión de requisitos al inicio de M6 y M7 |
| Licencias de assets mal registradas | Assets sin fila en `ASSETS_LICENSES.md` | Validación automática en CI: todo asset del proyecto debe estar registrado |

---

## 15. ChatGPT y Gemini (opcional)

Álvaro tiene ChatGPT gratis y Gemini Go. **No forman parte del flujo principal** (todo lo hace Claude), pero se pueden usar sin gastar el plan de Claude para:
- **Lluvia de ideas** de nombres del juego, de calles y de eventos.
- **Imágenes de concepto rápidas** para comparar estilos antes de producir piezas.
- **Segunda opinión** sobre textos de la tienda o del tutorial.

Lo que salga de ahí se trae al repo como **dato o referencia** y Claude lo integra.

---

## 16. Próximos pasos inmediatos

Para Álvaro:
- [x] Leer el GDD y el plan y tomar las primeras decisiones (GDD §20.2) → **M0-02** ✅
- [ ] Escuchar la banda sonora v1 y decir qué cambiar (tempo, instrumentos, ambiente).
- [ ] Decidir si el trabajo en el PC se hará con **Claude Code instalado en el PC** (recomendado) o con Desktop Commander.
- [ ] Tener a mano: el login de Unity, el móvil Android con la depuración USB activada y un cable.

Para Claude (en este orden, con el modelo indicado):
1. **M0-03** `CLAUDE.md` + `README.md` → Haiku · medium.
2. **M0-04** settings, skills y subagentes → Sonnet · low.
3. **M0-09** tarjetas a `docs/tareas/` → Haiku · low.
4. **M1-01** solución .NET → Sonnet · low (puede ir en paralelo a M0-05).
5. **M0-05** puesta a punto del PC → Sonnet · medium (🖥️).
6. **M0-06** bocetos en Figma y **M0-07** packs CC0 → Sonnet · medium / Haiku · medium.
