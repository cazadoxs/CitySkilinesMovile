---
name: datos
description: Genera o actualiza datos del juego (JSON de edificios, estilos, políticas, traducciones, prompts de ComfyUI) validando contra los esquemas.
argument-hint: "[qué datos]"
model: haiku
effort: medium
---
Genera $ARGUMENTS en unity/URBE/Assets/_Project/Data/ (o tools/comfyui/prompts/).
Valida con el esquema JSON correspondiente (tools/scripts/validate-data). No toques código C#.
