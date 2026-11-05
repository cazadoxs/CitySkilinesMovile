# Licencias y origen de los assets

Regla (CLAUDE.md): todo asset nuevo se registra aquí con archivo, fuente, autor, licencia, fecha y modificaciones.
Hoy **no hay ningún asset de terceros**: todo es original y se regenera desde código del repositorio.

| Archivo | Fuente | Autor | Licencia | Fecha | Modificaciones |
|---|---|---|---|---|---|
| `unity/URBE/Assets/_Project/Art/Vehicles/Vehicles.fbx` (20 mallas: 10 vehículos x 2 LOD) | Original, modelado por script: `tools/blender/make_vehicles.py` (Blender, geometría procedimental) | Proyecto URBE (generado con Claude) | Original del proyecto, sin dependencias externas | 2026-11-05 | Regenerable con `blender --background --python tools/blender/make_vehicles.py` |
| `unity/URBE/Assets/_Project/Art/Vehicles/VehicleAtlas.png` (64x64) | Original, pintado por el mismo script (celdas de color plano) | Proyecto URBE | Original del proyecto | 2026-11-05 | Ninguna |
| `unity/URBE/Assets/_Project/Art/Vehicles/Vehicle.shader` | Original (HLSL escrito para URP) | Proyecto URBE | Original del proyecto | 2026-11-05 | Ninguna |
| `unity/URBE/Assets/_Project/Art/Roads/` (atlas y shader de carreteras) | Original, generado por `RoadAtlasBuilder` | Proyecto URBE | Original del proyecto | 2026-10 | Ninguna |
| `audio/music/*.ogg` | Original, sintetizada con `tools/music/` | Proyecto URBE | Original del proyecto | 2026-10 | Ninguna |

## Decisiones
- 2026-11-05 (M2-07): se descarta el Kenney Car Kit (CC0) y cualquier descarga externa de vehículos; los 10 modelos son originales para no depender de licencias ni de estilos ajenos.
- Paquetes de Unity (URP, Input System, Burst, Collections, etc.) se usan bajo la licencia del Unity Package Manager y no se redistribuyen en este repositorio.
