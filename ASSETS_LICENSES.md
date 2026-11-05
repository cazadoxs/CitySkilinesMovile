# Licencias y origen de los assets

Regla (CLAUDE.md): todo asset nuevo se registra aquí con archivo, fuente, autor, licencia, fecha y modificaciones.
Hay **un asset de terceros con origen pendiente** (`lowpoly building.fbx`, ver abajo); el resto es original y se regenera desde código del repositorio.

| Archivo | Fuente | Autor | Licencia | Fecha | Modificaciones |
|---|---|---|---|---|---|
| `unity/URBE/Assets/_Project/Art/Vehicles/Vehicles.fbx` (20 mallas: 10 vehículos x 2 LOD) | Original, modelado por script: `tools/blender/make_vehicles.py` (Blender, geometría procedimental) | Proyecto URBE (generado con Claude) | Original del proyecto, sin dependencias externas | 2026-11-05 | Regenerable con `blender --background --python tools/blender/make_vehicles.py` |
| `unity/URBE/Assets/_Project/Art/Vehicles/VehicleAtlas.png` (64x64) | Original, pintado por el mismo script (celdas de color plano) | Proyecto URBE | Original del proyecto | 2026-11-05 | Ninguna |
| `unity/URBE/Assets/_Project/Art/Vehicles/Vehicle.shader` | Original (HLSL escrito para URP) | Proyecto URBE | Original del proyecto | 2026-11-05 | Ninguna |
| `unity/URBE/Assets/lowpoly building.fbx` (pack de 100 edificios low-poly, 4 materiales planos, ~111 k triángulos) | **ORIGEN PENDIENTE DE CONFIRMAR** (lo aportó Álvaro; no se conoce la tienda ni el autor) | Desconocido | **Uso comercial declarado por Álvaro, sin comprobante.** Pendiente: localizar la fuente y guardar su licencia antes de publicar | 2026-11-05 | Ninguna: el fichero original no se toca |
| `unity/URBE/Assets/_Project/Art/Buildings/Bld_*.fbx` y `Svc_*.fbx` (115 modelos, LOD0/LOD1) | **Derivados.** Residencial, comercial y oficinas: 58 de los edificios del pack anterior, re-pintados, escalados, con plantas repetidas y geometría propia (ventanas, puertas, toldos, balcones, rótulos, props de azotea). Industria y los 10 servicios: modelado propio por script | Proyecto URBE (generado con Claude) sobre el modelo de Álvaro | Heredan la condición del pack (origen pendiente) en los `Bld_R_*`, `Bld_C_*` y `Bld_O_*`; `Bld_I_*` y `Svc_*` son originales | 2026-11-05 | `blender --background --python tools/blender/make_buildings.py`; si el pack se descarta, hay que regenerar R, C y O con otra fuente |
| `unity/URBE/Assets/_Project/Art/Buildings/BuildingAtlas.png` (64x64) y `Buildings.manifest.json` | Original, generados por el mismo script | Proyecto URBE | Original del proyecto | 2026-11-05 | Ninguna |
| `unity/URBE/Assets/_Project/Art/Roads/` (atlas y shader de carreteras) | Original, generado por `RoadAtlasBuilder` | Proyecto URBE | Original del proyecto | 2026-10 | Ninguna |
| `audio/music/*.ogg` | Original, sintetizada con `tools/music/` | Proyecto URBE | Original del proyecto | 2026-10 | Ninguna |

## Decisiones
- 2026-11-05 (M2-07): se descarta el Kenney Car Kit (CC0) y cualquier descarga externa de vehículos; los 10 modelos son originales para no depender de licencias ni de estilos ajenos.
- Paquetes de Unity (URP, Input System, Burst, Collections, etc.) se usan bajo la licencia del Unity Package Manager y no se redistribuyen en este repositorio.
