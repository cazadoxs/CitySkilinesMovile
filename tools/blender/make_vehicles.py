"""
URBE M2-07: genera los 10 vehiculos originales (estilo chunky low-poly) con dos LOD cada uno y un atlas de una sola textura.

Uso (desde la raiz del repo):
    blender --background --python tools/blender/make_vehicles.py -- [--out RUTA_CARPETA] [--preview RUTA.png]

Salida en --out (por defecto unity/URBE/Assets/_Project/Art/Vehicles):
    Vehicles.fbx        20 mallas "Veh_<nombre>_LOD0|LOD1", escala real en metros, origen en el suelo y centro del vehiculo,
                        frente hacia +Z en Unity (+Y en Blender), parado sobre y=0.
    VehicleAtlas.png    64x64, 8x8 celdas de 8 px. Alfa 255 = pintura que el shader tine por instancia; alfa 0 = color fijo.
    Vehicles.stats.json triangulos, tamano y celdas por modelo (lo leen las pruebas y la tarjeta).

Todo se construye por codigo: no hay ficheros externos ni licencias de terceros (ver ASSETS_LICENSES.md).
"""
import json
import math
import os
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

# ---------------------------------------------------------------- atlas
# Celdas de 8x8 px en una rejilla 8x8 (indice = fila*8 + columna, fila 0 abajo). (r, g, b, alfa)
CELLS = {
    "PAINT": (0, (226, 226, 226, 255)),   # unica celda que se tine
    "GLASS": (1, (58, 82, 104, 0)),
    "TIRE": (2, (28, 28, 32, 0)),
    "HUB": (3, (150, 152, 160, 0)),
    "HEAD": (4, (255, 246, 196, 0)),
    "TAIL": (5, (214, 38, 38, 0)),
    "TRIM": (6, (48, 50, 58, 0)),
    "TAXI": (7, (248, 196, 28, 0)),
    "WHITE": (8, (240, 240, 240, 0)),
    "RED": (9, (204, 40, 40, 0)),
    "GREEN": (10, (70, 150, 78, 0)),
    "BLUE": (11, (50, 104, 206, 0)),
    "ORANGE": (12, (240, 140, 30, 0)),
    "CARGO": (13, (214, 208, 196, 0)),
    "CHASSIS": (14, (76, 76, 84, 0)),
    "LAMPB": (15, (70, 130, 255, 0)),
    "DKGREEN": (16, (44, 108, 56, 0)),
}
GRID = 8


def cell_uv(idx):
    return ((idx % GRID + 0.5) / GRID, (idx // GRID + 0.5) / GRID)


def make_atlas_image(size=64):
    img = bpy.data.images.new("VehicleAtlas", size, size, alpha=True)
    px = [0.0] * (size * size * 4)
    cs = size // GRID
    for _, (idx, rgba) in CELLS.items():
        cx, cy = idx % GRID, idx // GRID
        for y in range(cy * cs, (cy + 1) * cs):
            for x in range(cx * cs, (cx + 1) * cs):
                o = (y * size + x) * 4
                px[o:o + 4] = [rgba[0] / 255, rgba[1] / 255, rgba[2] / 255, rgba[3] / 255]
    # celdas sin usar: gris medio opaco
    used = {v[0] for v in CELLS.values()}
    for idx in range(GRID * GRID):
        if idx in used:
            continue
        cx, cy = idx % GRID, idx // GRID
        for y in range(cy * cs, (cy + 1) * cs):
            for x in range(cx * cs, (cx + 1) * cs):
                o = (y * size + x) * 4
                px[o:o + 4] = [0.5, 0.5, 0.5, 0.0]
    img.pixels = px
    img.alpha_mode = "CHANNEL_PACKED"
    return img


# ---------------------------------------------------------------- constructor de geometria
class Builder:
    """Acumula caras en un bmesh; el material_index de cada cara guarda el indice de celda hasta el final."""

    def __init__(self):
        self.bm = bmesh.new()

    def _face(self, vs, cell):
        f = self.bm.faces.new(vs)
        f.material_index = CELLS[cell][0]
        return f

    def frustum(self, bot, top, side="PAINT", roof=None, floor="TRIM", front=None, back=None, sides=None):
        """Hexaedro cuya base y cara superior son rectangulos distintos (cuña/cabina).
        bot = (x0, x1, y0, y1, z), top = (x0, x1, y0, y1, z). front=+y, back=-y."""
        bx0, bx1, by0, by1, bz = bot
        tx0, tx1, ty0, ty1, tz = top
        v = self.bm.verts.new
        b0, b1, b2, b3 = v((bx0, by0, bz)), v((bx1, by0, bz)), v((bx1, by1, bz)), v((bx0, by1, bz))
        t0, t1, t2, t3 = v((tx0, ty0, tz)), v((tx1, ty0, tz)), v((tx1, ty1, tz)), v((tx0, ty1, tz))
        front = front or side
        back = back or side
        sides = sides or side
        roof = roof or side
        self._face((b0, b3, b2, b1), floor)
        self._face((t0, t1, t2, t3), roof)
        self._face((b3, t3, t2, b2), front)   # +y
        self._face((b0, b1, t1, t0), back)    # -y
        self._face((b1, b2, t2, t1), sides)   # +x
        self._face((b0, t0, t3, b3), sides)   # -x

    def box(self, x0, x1, y0, y1, z0, z1, mat="PAINT", **kw):
        self.frustum((x0, x1, y0, y1, z0), (x0, x1, y0, y1, z1), side=mat, roof=kw.get("roof", mat), floor=kw.get("floor", mat))

    def wheel(self, x, y, r, w, segs=10, hub="HUB"):
        n0 = len(self.bm.faces)
        m = Matrix.Translation((x, y, r)) @ Matrix.Rotation(math.pi / 2, 4, "Y")
        bmesh.ops.create_cone(self.bm, cap_ends=True, cap_tris=False, segments=segs, radius1=r / math.cos(math.pi / segs), radius2=r / math.cos(math.pi / segs), depth=w, matrix=m, calc_uvs=False)
        self.bm.faces.ensure_lookup_table()
        for f in list(self.bm.faces)[n0:]:
            f.material_index = CELLS[hub][0] if abs(f.normal.x) > 0.9 else CELLS["TIRE"][0]

    def finish(self, name, tint_cells_to_paint=False):
        bm = self.bm
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
        uv = bm.loops.layers.uv.new("UVMap")
        for f in bm.faces:
            u, v = cell_uv(f.material_index)
            for l in f.loops:
                l[uv].uv = (u, v)
            f.material_index = 0
            f.smooth = False
        bm.normal_update()
        me = bpy.data.meshes.new(name)
        bm.to_mesh(me)
        bm.free()
        return me


# ---------------------------------------------------------------- modelos
def car(b, lod, L, W, H, wr, body_top, cab0, cab1, hood=0.3, trunk=0.25, paint="PAINT", sport=False, roof_sign=False, hatch=False, clear=0.22):
    """Turismo: cuerpo bajo achaflanado + cabina de cristal. y positivo = frente."""
    hw, hl = W / 2, L / 2
    bw = hw - 0.04
    # cuerpo (el capó y el maletero bajan hacia los extremos)
    b.frustum((-bw, bw, -hl + 0.12, hl - 0.12, clear), (-bw + 0.06, bw - 0.06, -hl + 0.12 + trunk, hl - 0.12 - hood, body_top), side=paint, roof=paint, floor="CHASSIS", front=paint, back=paint)
    # cabina
    cw = bw - 0.10
    b.frustum((-cw, cw, cab0, cab1, body_top), (-cw + 0.16, cw - 0.16, cab0 + 0.55, cab1 - (0.35 if hatch else 0.55), H), side="GLASS", roof=paint, floor=paint)
    if lod == 0:
        # parachoques, faros, parrilla
        b.box(-bw - 0.03, bw + 0.03, hl - 0.22, hl + 0.02, clear + 0.02, clear + 0.28, "TRIM")
        b.box(-bw - 0.03, bw + 0.03, -hl - 0.02, -hl + 0.22, clear + 0.02, clear + 0.28, "TRIM")
        zl = clear + 0.36
        for sx in (-1, 1):
            b.box(sx * (bw - 0.30) - 0.20, sx * (bw - 0.30) + 0.20, hl - 0.16, hl + 0.01, zl, zl + 0.16, "HEAD")
            b.box(sx * (bw - 0.30) - 0.18, sx * (bw - 0.30) + 0.18, -hl - 0.01, -hl + 0.12, zl, zl + 0.16, "TAIL")
        b.box(-0.45, 0.45, hl - 0.14, hl + 0.015, zl + 0.01, zl + 0.15, "TRIM")
        # retrovisores
        for sx in (-1, 1):
            b.box(sx * (cw + 0.10) - 0.07, sx * (cw + 0.10) + 0.07, cab1 - 0.55, cab1 - 0.38, body_top + 0.10, body_top + 0.24, "TRIM")
        if sport:
            b.box(-bw + 0.1, bw - 0.1, -hl - 0.05, -hl + 0.30, body_top + 0.05, body_top + 0.12, "TRIM")
            b.box(-bw + 0.25, -bw + 0.35, -hl + 0.05, -hl + 0.25, body_top - 0.10, body_top + 0.06, "TRIM")
            b.box(bw - 0.35, bw - 0.25, -hl + 0.05, -hl + 0.25, body_top - 0.10, body_top + 0.06, "TRIM")
    if roof_sign:
        b.box(-0.30, 0.30, (cab0 + cab1) / 2 - 0.14, (cab0 + cab1) / 2 + 0.14, H, H + 0.17, "WHITE")
    ax = hw - 0.12
    wb = L * 0.34
    seg = 10 if lod == 0 else 6
    for sx in (-1, 1):
        for sy in (-1, 1):
            b.wheel(sx * ax, sy * wb, wr, 0.24 if lod == 0 else 0.28, seg, "HUB" if lod == 0 else "TIRE")


def van(b, lod, L, W, H, wr, paint="PAINT", ambulance=False, accent="RED"):
    hw, hl = W / 2, L / 2
    bw = hw - 0.03
    b.frustum((-bw, bw, -hl + 0.1, hl - 0.1, 0.25), (-bw + 0.04, bw - 0.04, -hl + 0.1, hl - 0.1, 1.05), side=paint, roof=paint, floor="CHASSIS")
    # cabina frontal con parabrisas inclinado
    b.frustum((-bw + 0.02, bw - 0.02, hl - 1.75, hl - 0.1, 1.05), (-bw + 0.12, bw - 0.12, hl - 1.45, hl - 0.75, H - 0.25), side="GLASS", roof=paint, floor=paint, back=paint)
    # caja trasera
    b.frustum((-bw + 0.02, bw - 0.02, -hl + 0.1, hl - 1.75, 1.05), (-bw + 0.08, bw - 0.08, -hl + 0.12, hl - 1.75, H), side=paint, roof=paint, floor=paint)
    if lod == 0:
        b.box(-bw - 0.03, bw + 0.03, hl - 0.20, hl + 0.02, 0.28, 0.58, "TRIM")
        b.box(-bw - 0.03, bw + 0.03, -hl - 0.02, -hl + 0.20, 0.28, 0.58, "TRIM")
        for sx in (-1, 1):
            b.box(sx * (bw - 0.3) - 0.2, sx * (bw - 0.3) + 0.2, hl - 0.16, hl + 0.01, 0.62, 0.82, "HEAD")
            b.box(sx * (bw - 0.2) - 0.1, sx * (bw - 0.2) + 0.1, -hl - 0.01, -hl + 0.1, 0.65, 1.15, "TAIL")
        if ambulance:
            b.box(-bw - 0.008, bw + 0.008, -hl + 0.5, hl - 1.8, 1.1, 1.38, accent)
            b.box(-bw - 0.008, bw + 0.008, -hl + 0.5, hl - 1.8, 1.55, 1.65, accent)
            b.box(-0.62, 0.62, hl - 1.9, hl - 1.2, H, H + 0.12, "TRIM")
            b.box(-0.60, -0.04, hl - 1.85, hl - 1.25, H + 0.12, H + 0.26, "RED")
            b.box(0.04, 0.60, hl - 1.85, hl - 1.25, H + 0.12, H + 0.26, "LAMPB")
    ax = hw - 0.12
    seg = 10 if lod == 0 else 6
    for sx in (-1, 1):
        for sy in (-1, 1):
            b.wheel(sx * ax, sy * L * 0.31, wr, 0.26, seg, "HUB" if lod == 0 else "TIRE")


def truck(b, lod, garbage=False):
    L, W = (8.6 if garbage else 9.4), 2.5
    hl, bw = L / 2, W / 2 - 0.05
    cab_len = 2.1
    # chasis
    b.box(-bw + 0.2, bw - 0.2, -hl + 0.2, hl - 0.1, 0.45, 0.95, "CHASSIS")
    # cabina
    cabp = "GREEN" if garbage else "PAINT"
    b.frustum((-bw, bw, hl - cab_len - 0.05, hl - 0.05, 0.95), (-bw + 0.05, bw - 0.05, hl - cab_len + 0.15, hl - 0.45, 3.0), side=cabp, roof=cabp, floor="CHASSIS", front="GLASS", back=cabp)
    b.box(-bw - 0.02, bw + 0.02, hl - 0.08, hl + 0.05, 0.55, 0.95, "TRIM")  # paragolpes
    if garbage:
        # tolva compactadora detras de la cabina
        b.frustum((-bw, bw, -hl + 0.1, hl - cab_len - 0.2, 0.95), (-bw + 0.05, bw - 0.05, -hl + 0.1, hl - cab_len - 0.2, 3.2), side="DKGREEN", roof="GREEN", floor="CHASSIS", back="TRIM")
        if lod == 0:
            b.box(-bw + 0.1, bw - 0.1, -hl - 0.25, -hl + 0.12, 0.8, 1.9, "TRIM")
            b.box(-0.9, 0.9, hl - cab_len - 0.9, hl - cab_len - 0.2, 3.2, 3.38, "ORANGE")
    else:
        # caja de carga
        b.frustum((-bw, bw, -hl + 0.05, hl - cab_len - 0.2, 0.95), (-bw + 0.02, bw - 0.02, -hl + 0.05, hl - cab_len - 0.2, 3.55), side="CARGO", roof="WHITE", floor="CHASSIS", back="CARGO")
        if lod == 0:
            b.box(-bw + 0.3, bw - 0.3, -hl - 0.03, -hl + 0.03, 1.0, 1.15, "TRIM")
    if lod == 0:
        for sx in (-1, 1):
            b.box(sx * (bw - 0.4) - 0.25, sx * (bw - 0.4) + 0.25, hl - 0.04, hl + 0.06, 0.95, 1.2, "HEAD")
            b.box(sx * (bw - 0.3) - 0.15, sx * (bw - 0.3) + 0.15, -hl - 0.02, -hl + 0.1, 0.55, 0.85, "TAIL")
    seg = 12 if lod == 0 else 6
    ax = W / 2 - 0.14
    for sx in (-1, 1):
        b.wheel(sx * ax, hl - 1.3, 0.5, 0.34, seg, "HUB" if lod == 0 else "TIRE")
        b.wheel(sx * ax, -hl + 1.5, 0.5, 0.34, seg, "HUB" if lod == 0 else "TIRE")
        b.wheel(sx * ax, -hl + 2.6, 0.5, 0.34, seg, "HUB" if lod == 0 else "TIRE")


def bus(b, lod):
    L, W, H = 11.4, 2.55, 3.2
    hl, bw = L / 2, W / 2 - 0.03
    b.frustum((-bw, bw, -hl + 0.1, hl - 0.1, 0.45), (-bw + 0.06, bw - 0.06, -hl + 0.1, hl - 0.1, 1.0), side="PAINT", roof="PAINT", floor="CHASSIS")
    # banda de ventanas y techo
    b.frustum((-bw + 0.06, bw - 0.06, -hl + 0.1, hl - 0.1, 1.0), (-bw + 0.12, bw - 0.12, -hl + 0.2, hl - 0.15, 2.8), side="GLASS", roof="PAINT", floor="PAINT", back="PAINT")
    b.box(-bw + 0.12, bw - 0.12, -hl + 0.2, hl - 0.15, 2.8, H, "PAINT")
    if lod == 0:
        for k in range(5):  # montantes entre ventanas
            y = -hl + 1.2 + k * 2.2
            b.box(-bw - 0.005, bw + 0.005, y - 0.07, y + 0.07, 1.0, 2.8, "PAINT")
        b.box(-bw - 0.01, bw + 0.01, hl - 0.2, hl + 0.01, 0.4, 0.8, "TRIM")
        b.box(-bw - 0.01, bw + 0.01, -hl - 0.01, -hl + 0.2, 0.4, 0.8, "TRIM")
        for sx in (-1, 1):
            b.box(sx * (bw - 0.4) - 0.22, sx * (bw - 0.4) + 0.22, hl - 0.12, hl + 0.015, 0.55, 0.78, "HEAD")
            b.box(sx * (bw - 0.3) - 0.14, sx * (bw - 0.3) + 0.14, -hl - 0.01, -hl + 0.12, 0.55, 0.85, "TAIL")
        b.box(-0.9, 0.9, hl - 0.15, hl + 0.02, 2.6, 2.95, "TRIM")  # cartel destino
        b.box(-0.7, 0.7, -0.9, 0.9, H, H + 0.22, "WHITE")           # climatizador
    seg = 12 if lod == 0 else 6
    ax = W / 2 - 0.14
    for sx in (-1, 1):
        b.wheel(sx * ax, hl - 1.9, 0.5, 0.34, seg, "HUB" if lod == 0 else "TIRE")
        b.wheel(sx * ax, -hl + 2.0, 0.5, 0.34, seg, "HUB" if lod == 0 else "TIRE")


# nombre -> (constructor, descripcion). Dimensiones objetivo en metros (L x A x H) en MODELS_SPEC.
def build_model(name, lod):
    b = Builder()
    if name == "car_small":
        car(b, lod, 3.7, 1.66, 1.50, 0.29, 0.92, -0.95, 1.05, hood=0.35, trunk=0.12, hatch=True)
    elif name == "car_sedan":
        car(b, lod, 4.3, 1.80, 1.46, 0.31, 0.88, -1.15, 1.05, hood=0.45, trunk=0.35)
    elif name == "car_suv":
        car(b, lod, 4.7, 1.94, 1.80, 0.38, 1.05, -1.65, 1.2, hood=0.35, trunk=0.12, hatch=True, clear=0.28)
    elif name == "sport":
        car(b, lod, 4.4, 1.90, 1.22, 0.32, 0.70, -0.95, 0.75, hood=0.7, trunk=0.45, sport=True, clear=0.14)
    elif name == "taxi":
        car(b, lod, 4.3, 1.80, 1.46, 0.31, 0.88, -1.15, 1.05, hood=0.45, trunk=0.35, paint="TAXI", roof_sign=True)
    elif name == "van":
        van(b, lod, 5.3, 2.0, 2.25, 0.36)
    elif name == "ambulance":
        van(b, lod, 5.7, 2.1, 2.5, 0.37, paint="WHITE", ambulance=True)
    elif name == "truck":
        truck(b, lod)
    elif name == "garbage":
        truck(b, lod, garbage=True)
    elif name == "bus":
        bus(b, lod)
    else:
        raise ValueError(name)
    return b.finish(f"Veh_{name}_LOD{lod}")


MODELS = ["car_small", "car_sedan", "car_suv", "van", "truck", "bus", "taxi", "ambulance", "garbage", "sport"]
# Longitud x ancho x alto objetivo (m); la prueba comprueba +-10 %.
SPEC = {
    "car_small": (3.7, 1.66, 1.50), "car_sedan": (4.3, 1.80, 1.46), "car_suv": (4.7, 1.94, 1.80), "van": (5.3, 2.0, 2.25),
    "truck": (9.4, 2.5, 3.55), "bus": (11.4, 2.55, 3.42), "taxi": (4.3, 1.80, 1.63), "ambulance": (5.7, 2.1, 2.62),
    "garbage": (8.6, 2.5, 3.38), "sport": (4.4, 1.90, 1.22),
}


# ---------------------------------------------------------------- escena y exportacion
def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "unity", "URBE", "Assets", "_Project", "Art", "Vehicles")
    preview = None
    i = 0
    while i < len(argv):
        if argv[i] == "--out":
            out = argv[i + 1]; i += 2
        elif argv[i] == "--preview":
            preview = argv[i + 1]; i += 2
        else:
            i += 1
    return os.path.abspath(out), preview


def main():
    out, preview = parse_args()
    os.makedirs(out, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    img = make_atlas_image()
    png = os.path.join(out, "VehicleAtlas.png")
    img.filepath_raw = png
    img.file_format = "PNG"
    img.save()

    mat = bpy.data.materials.new("VehicleAtlas")
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = nt.nodes["Principled BSDF"]
    tex = nt.nodes.new("ShaderNodeTexImage")
    tex.image = img
    tex.interpolation = "Closest"
    nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])

    stats = {}
    objs = {}
    for name in MODELS:
        for lod in (0, 1):
            me = build_model(name, lod)
            me.materials.append(mat)
            ob = bpy.data.objects.new(me.name, me)
            bpy.context.scene.collection.objects.link(ob)
            tris = sum(len(p.vertices) - 2 for p in me.polygons)
            vs = [v.co for v in me.vertices]
            mn = [min(v[i] for v in vs) for i in range(3)]
            mx = [max(v[i] for v in vs) for i in range(3)]
            stats.setdefault(name, {})[f"lod{lod}"] = {"tris": tris, "verts": len(me.vertices)}
            if lod == 0:
                stats[name]["size_m"] = {"length": round(mx[1] - mn[1], 3), "width": round(mx[0] - mn[0], 3), "height": round(mx[2] - mn[2], 3), "z_min": round(mn[2], 3)}
            objs[(name, lod)] = ob
    with open(os.path.join(out, "Vehicles.stats.json"), "w") as fh:
        json.dump(stats, fh, indent=2, sort_keys=True)

    bpy.ops.object.select_all(action="DESELECT")
    for ob in objs.values():
        ob.select_set(True)
    bpy.ops.export_scene.fbx(
        filepath=os.path.join(out, "Vehicles.fbx"), use_selection=True, object_types={"MESH"},
        axis_forward="Z", axis_up="Y", bake_space_transform=True, apply_unit_scale=True, global_scale=1.0,
        mesh_smooth_type="FACE", use_mesh_modifiers=False, add_leaf_bones=False, path_mode="STRIP", embed_textures=False,
        use_triangles=True)
    print("[URBE] Vehiculos exportados a", out)
    for k, v in stats.items():
        print("  ", k, v)

    if preview:
        render_preview(objs, preview)


def render_preview(objs, path):
    """Fila LOD0 y fila LOD1 (Workbench, textura del atlas) para juzgar los modelos a ojo."""
    scene = bpy.context.scene
    x = 0.0
    xs = {}
    for name in MODELS:
        xs[name] = x
        x += max(SPEC[name][1], 2.3) + 1.8
    total = x
    for (name, lod), ob in objs.items():
        ob.location = (xs[name] - total / 2, 0 if lod == 0 else -17.0, 0)
        if lod == 1:
            ob.location.y = -17.0
    # suelo gris
    bpy.ops.mesh.primitive_plane_add(size=1, location=(0, -8.5, -0.01))
    pl = bpy.context.object
    pl.scale = (total + 8, 44, 1)
    cam = bpy.data.cameras.new("Cam")
    cam.type = "ORTHO"
    cam.ortho_scale = total * 1.04
    co = bpy.data.objects.new("Cam", cam)
    scene.collection.objects.link(co)
    co.location = (0, -53, 38)
    co.rotation_euler = (math.radians(52), 0, 0)
    scene.camera = co
    scene.render.engine = "BLENDER_EEVEE"
    sun = bpy.data.objects.new("Sol", bpy.data.lights.new("Sol", "SUN"))
    sun.data.energy = 3.5
    sun.rotation_euler = (math.radians(50), math.radians(10), math.radians(-35))
    scene.collection.objects.link(sun)
    scene.render.resolution_x = 2400
    scene.render.resolution_y = 1100
    scene.render.film_transparent = False
    scene.world = bpy.data.worlds.new("W")
    scene.world.color = (0.62, 0.78, 0.9)
    scene.world.use_nodes = False
    scene.render.filepath = path
    scene.view_settings.view_transform = "Standard"
    bpy.ops.render.render(write_still=True)
    print("[URBE] Vista previa:", path)


if __name__ == "__main__":
    main()
