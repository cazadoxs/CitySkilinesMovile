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
