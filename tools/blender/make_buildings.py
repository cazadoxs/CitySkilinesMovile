"""
URBE M3-09: kit de edificios a partir del pack "lowpoly building.fbx" de Alvaro (100 edificios sueltos) mas geometria propia.

Uso (desde la raiz del repo):
    blender --background --python tools/blender/make_buildings.py -- [--out CARPETA] [--src FBX] [--preview PNG] [--only PREFIJO]

Que hace:
  * Importa el pack, limpia cada edificio (soldar vertices, normales, origen en el suelo) y lo usa como CASCARON:
    residencial (todos), comercial y oficinas se parten de los edificios del pack, que se
    - rotan para que la puerta mire a +Y, se escalan en planta a una huella de multiplos de 8 m,
    - ganan plantas REPITIENDO una banda de planta (no se estiran las ventanas),
    - se re-pintan sobre un atlas compartido (blanco = pared tintable, rojo = cubierta, azul = cristal, naranja = puerta),
    - reciben geometria propia: ventanas con marco, puerta, balcones, toldos rayados, rotulos, climatizadores, depositos, antenas.
  * Industria y los 10 servicios se modelan enteros aqui (naves, chimeneas, silos, aerogenerador, estanques...).
  * Dos LOD por modelo (LOD1 sin detalles menudos y con planos coplanares disueltos), un FBX por modelo y un manifest JSON.

Salida en --out (por defecto unity/URBE/Assets/_Project/Art/Buildings):
    <id>.fbx                 mallas <id>_LOD0 y <id>_LOD1, metros reales, base centrada en el origen (y=0), frente hacia +Z en Unity
    BuildingAtlas.png        64x64, 8x8 celdas de 8 px. Alfa 255 = pared que el shader tine por instancia; alfa 0 = color fijo
                             (mismo convenio que VehicleAtlas.png; sirve el shader Urbe/Vehicle)
    Buildings.manifest.json  id, zona, densidad, nivel, huella en celdas, altura, triangulos LOD0 y LOD1, origen

Ids: Bld_<zona>_<densidad>_L<nivel>_<variante> (zona R/C/I/O; densidad low/med/high; variante A/B/C) y Svc_<tipo>.
"""
import json
import math
import os
import random
import re
import sys
import zlib

import bmesh
import bpy
from mathutils import Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
GRID = 8
CELL_M = 8.0

# ---------------------------------------------------------------- atlas
CELL_DEFS = [
    # paredes (alfa 255 = tintables por instancia)
    ("WALL_CREAM", (238, 228, 206, 255)), ("WALL_PEACH", (240, 190, 156, 255)), ("WALL_YELLOW", (246, 222, 138, 255)),
    ("WALL_MINT", (186, 224, 196, 255)), ("WALL_SKY", (172, 208, 234, 255)), ("WALL_WHITE", (246, 246, 246, 255)),
    ("WALL_BRICK", (196, 108, 84, 255)), ("WALL_STONE", (206, 200, 190, 255)), ("WALL_CONC", (176, 180, 186, 255)),
    ("WALL_STEEL", (150, 172, 192, 255)), ("WALL_DARKG", (100, 108, 120, 255)), ("WALL_SAND", (226, 202, 158, 255)),
    ("WALL_METAL", (168, 178, 186, 255)), ("WALL_ROSE", (236, 176, 184, 255)), ("WALL_LILAC", (196, 184, 226, 255)),
    # cubiertas
    ("ROOF_TERRA", (196, 86, 58, 0)), ("ROOF_SLATE", (84, 94, 112, 0)), ("ROOF_BROWN", (122, 80, 58, 0)),
    ("ROOF_GREEN", (70, 128, 92, 0)), ("ROOF_BLUE", (66, 102, 156, 0)), ("ROOF_FLAT", (118, 118, 126, 0)),
    ("ROOF_REDDARK", (150, 46, 44, 0)), ("ROOF_ORANGE", (224, 130, 52, 0)),
    # cristal, puertas, molduras
    ("GLASS", (110, 164, 190, 0)), ("GLASS_OFF", (74, 124, 170, 0)), ("GLASS_DARK", (46, 70, 104, 0)),
    ("DOOR_ORANGE", (230, 120, 40, 0)), ("DOOR_BROWN", (112, 72, 50, 0)), ("DOOR_BLUE", (60, 96, 170, 0)),
    ("TRIM_DARK", (54, 56, 64, 0)), ("TRIM_WHITE", (244, 244, 244, 0)), ("TRIM_GREY", (150, 152, 160, 0)),
    # toldos y rotulos
    ("AWN_RED", (214, 54, 54, 0)), ("AWN_BLUE", (56, 114, 204, 0)), ("AWN_GREEN", (62, 160, 92, 0)),
    ("AWN_YELLOW", (244, 200, 52, 0)), ("AWN_ORANGE", (240, 140, 40, 0)), ("AWN_PURPLE", (150, 84, 172, 0)),
    ("AWN_TEAL", (44, 168, 168, 0)),
    # industria y servicios
    ("CHIM_BRICK", (160, 92, 72, 0)), ("RED", (206, 42, 42, 0)), ("WHITE", (244, 244, 244, 0)), ("TANK", (204, 210, 216, 0)),
    ("GRASS", (104, 178, 92, 0)), ("TREE", (50, 132, 72, 0)), ("TRUNK", (108, 76, 52, 0)), ("WATER", (70, 150, 222, 0)),
    ("COAL", (40, 40, 46, 0)), ("HAZARD", (244, 196, 30, 0)), ("FIRE", (204, 40, 36, 0)), ("POLICE", (40, 72, 156, 0)),
    ("PATH", (216, 198, 152, 0)), ("DUMP", (124, 100, 74, 0)), ("SCHOOL", (244, 204, 96, 0)), ("SOLAR", (38, 60, 112, 0)),
    ("TREE_LIGHT", (112, 190, 90, 0)), ("TREE_PINK", (240, 150, 170, 0)),
]
C = {name: i for i, (name, _) in enumerate(CELL_DEFS)}
WALL_IDS = {i for n, i in C.items() if n.startswith("WALL_")}
assert len(CELL_DEFS) <= GRID * GRID


def cell_uv(idx):
    return ((idx % GRID + 0.5) / GRID, (idx // GRID + 0.5) / GRID)


def make_atlas_image(size=64):
    img = bpy.data.images.new("BuildingAtlas", size, size, alpha=True)
    px = [0.5, 0.5, 0.5, 0.0] * (size * size)
    cs = size // GRID
    for i, (_, rgba) in enumerate(CELL_DEFS):
        cx, cy = i % GRID, i // GRID
        for y in range(cy * cs, (cy + 1) * cs):
            for x in range(cx * cs, (cx + 1) * cs):
                o = (y * size + x) * 4
                px[o:o + 4] = [rgba[0] / 255, rgba[1] / 255, rgba[2] / 255, rgba[3] / 255]
    img.pixels = px
    img.alpha_mode = "CHANNEL_PACKED"
    return img


def pick(seq, key, var):
    """Elige de una lista de forma estable segun un texto y el numero de variante (variantes contiguas distintas)."""
    return seq[(zlib.crc32(key.encode()) + var * 5) % len(seq)]


# ---------------------------------------------------------------- constructor de geometria
class Builder:
    """Acumula caras en un bmesh; material_index guarda el indice de celda del atlas hasta finish()."""

    def __init__(self, bm=None):
        self.bm = bm if bm is not None else bmesh.new()

    # -- primitivas
    def poly(self, vs, cell, inside=None):
        try:
            f = self.bm.faces.new([self.bm.verts.new(Vector(v)) for v in vs])
        except ValueError:
            return None
        f.material_index = C[cell] if isinstance(cell, str) else cell
        if inside is not None:
            f.normal_update()
            if (f.calc_center_median() - Vector(inside)).dot(f.normal) < 0:
                f.normal_flip()
        return f

    def hexa(self, P, side, top=None, bottom=None, inside=None):
        """Solido de 8 puntos: P[0..3] base (antihoraria vista desde arriba), P[4..7] cara superior."""
        b0, b1, b2, b3, t0, t1, t2, t3 = [Vector(p) for p in P]
        ins = inside or sum((Vector(p) for p in P), Vector()) / 8.0
        top = top or side
        sides = side if isinstance(side, (list, tuple)) else [side] * 4
        self.poly((t0, t1, t2, t3), top, ins)
        if bottom:
            self.poly((b0, b3, b2, b1), bottom, ins)
        self.poly((b0, b1, t1, t0), sides[0], ins)   # -y
        self.poly((b1, b2, t2, t1), sides[1], ins)   # +x
        self.poly((b2, b3, t3, t2), sides[2], ins)   # +y
        self.poly((b3, b0, t0, t3), sides[3], ins)   # -x

    def box(self, x0, x1, y0, y1, z0, z1, cell, top=None, bottom=None, sides=None):
        P = [(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0), (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)]
        self.hexa(P, sides or cell, top or cell, bottom)

    def frustum(self, bot, top, side, topcell=None, bottom=None):
        """bot=(x0,x1,y0,y1,z), top=(x0,x1,y0,y1,z): cuña o toldo inclinado."""
        bx0, bx1, by0, by1, bz = bot
        tx0, tx1, ty0, ty1, tz = top
        P = [(bx0, by0, bz), (bx1, by0, bz), (bx1, by1, bz), (bx0, by1, bz), (tx0, ty0, tz), (tx1, ty0, tz), (tx1, ty1, tz), (tx0, ty1, tz)]
        self.hexa(P, side, topcell or side, bottom)

    def obox(self, c, size, rot, cell, top=None):
        """Caja orientada: c centro, size (sx,sy,sz), rot Matrix 3x3."""
        sx, sy, sz = size[0] / 2, size[1] / 2, size[2] / 2
        loc = [(-sx, -sy, -sz), (sx, -sy, -sz), (sx, sy, -sz), (-sx, sy, -sz), (-sx, -sy, sz), (sx, -sy, sz), (sx, sy, sz), (-sx, sy, sz)]
        P = [Vector(c) + rot @ Vector(p) for p in loc]
        self.hexa(P, cell, top or cell, None, inside=c)

    def cyl(self, cx, cy, r0, r1, z0, z1, cell, seg=8, top=None, bottom=None, rot=0.0):
        """Tronco de cilindro/cono: r0 abajo, r1 arriba (r1=0 -> cono)."""
        ins = (cx, cy, (z0 + z1) / 2)
        ring0 = [(cx + r0 * math.cos(rot + 2 * math.pi * i / seg), cy + r0 * math.sin(rot + 2 * math.pi * i / seg), z0) for i in range(seg)]
        ring1 = [(cx + r1 * math.cos(rot + 2 * math.pi * i / seg), cy + r1 * math.sin(rot + 2 * math.pi * i / seg), z1) for i in range(seg)]
        for i in range(seg):
            j = (i + 1) % seg
            if r1 > 1e-6:
                self.poly((ring0[i], ring0[j], ring1[j], ring1[i]), cell, ins)
            else:
                self.poly((ring0[i], ring0[j], (cx, cy, z1)), cell, ins)
        if top and r1 > 1e-6:
            self.poly(ring1, top, ins)
        if bottom:
            self.poly(ring0, bottom, ins)

    def gable(self, x0, x1, y0, y1, z0, z1, rh, wall, roof, axis="y", ovh=0.0, gcell=None):
        """Nave con cubierta a dos aguas. axis = direccion de la cumbrera."""
        gcell = gcell or wall
        ins = ((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2)

        def P(u, v, z):  # u a lo largo de la cumbrera, v a lo ancho
            return (v, u, z) if axis == "y" else (u, v, z)
        if axis == "y":
            u0, u1, v0, v1 = y0, y1, x0, x1
        else:
            u0, u1, v0, v1 = x0, x1, y0, y1
        vm = (v0 + v1) / 2
        zr = z1 + rh
        # paredes largas
        self.poly((P(u0, v0, z0), P(u1, v0, z0), P(u1, v0, z1), P(u0, v0, z1)), wall, ins)
        self.poly((P(u1, v1, z0), P(u0, v1, z0), P(u0, v1, z1), P(u1, v1, z1)), wall, ins)
        # hastiales
        self.poly((P(u0, v1, z0), P(u0, v0, z0), P(u0, v0, z1), P(u0, vm, zr), P(u0, v1, z1)), gcell, ins)
        self.poly((P(u1, v0, z0), P(u1, v1, z0), P(u1, v1, z1), P(u1, vm, zr), P(u1, v0, z1)), gcell, ins)
        # faldones (con alero)
        self.poly((P(u0 - ovh, v0 - ovh, z1 - ovh * rh / (v1 - v0) * 2), P(u1 + ovh, v0 - ovh, z1 - ovh * rh / (v1 - v0) * 2), P(u1 + ovh, vm, zr), P(u0 - ovh, vm, zr)), roof, ins)
        self.poly((P(u1 + ovh, v1 + ovh, z1 - ovh * rh / (v1 - v0) * 2), P(u0 - ovh, v1 + ovh, z1 - ovh * rh / (v1 - v0) * 2), P(u0 - ovh, vm, zr), P(u1 + ovh, vm, zr)), roof, ins)

    def sawtooth(self, x0, x1, y0, y1, z0, n, wall, roof, glass, rh=1.6):
        """Cubierta en diente de sierra: n dientes a lo largo de y, vertical de cristal mirando a +y."""
        step = (y1 - y0) / n
        for i in range(n):
            a, b = y0 + i * step, y0 + (i + 1) * step
            ins = ((x0 + x1) / 2, (a + b) / 2, z0 + rh / 2)
            self.poly(((x0, a, z0), (x1, a, z0), (x1, a, z0 + rh), (x0, a, z0 + rh)), roof if i else wall, ins)
            self.poly(((x0, a, z0 + rh), (x1, a, z0 + rh), (x1, b, z0), (x0, b, z0)), roof, ins)  # faldon suave
            self.poly(((x1, a, z0), (x1, b, z0), (x1, a, z0 + rh)), wall, ins)
            self.poly(((x0, b, z0), (x0, a, z0), (x0, a, z0 + rh)), wall, ins)
            self.poly(((x0, a + 0.0, z0 + rh), (x0, a, z0), (x1, a, z0), (x1, a, z0 + rh)), glass, (ins[0], ins[1] + 5, ins[2]))

    def quad_on(self, centre, n, t, ww, hh, cell, eps):
        """Cuadrado pegado a una pared: normal n, tangente t horizontal."""
        up = Vector((0, 0, 1))
        c = Vector(centre) + n * eps
        a, b = t * (ww / 2), up * (hh / 2)
        f = self.poly((c - a - b, c + a - b, c + a + b, c - a + b), cell)
        if f is not None:
            f.normal_update()
            if f.normal.dot(n) < 0:
                f.normal_flip()
        return f

    # -- acabado
    def tris(self):
        return sum(len(f.verts) - 2 for f in self.bm.faces)

    def bounds(self):
        vs = [v.co for v in self.bm.verts]
        mn = Vector([min(v[i] for v in vs) for i in range(3)])
        mx = Vector([max(v[i] for v in vs) for i in range(3)])
        return mn, mx

    def finish(self, name):
        bm = self.bm
        bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-4)
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


def centre_on_origin(b, cells):
    """Base en y=0 (z Blender) y huella centrada en el origen."""
    mn, mx = b.bounds()
    off = Vector(((mn.x + mx.x) / 2, (mn.y + mx.y) / 2, mn.z))
    bmesh.ops.translate(b.bm, vec=-off, verts=b.bm.verts)


# ---------------------------------------------------------------- detalles reutilizables
def windowize(b, wall_ids, glass, frame, fh=3.2, spacing=2.6, ww=1.2, wh=1.5, frames=True, avoid=(), skip_below=-1.0, skip_face=None, door_gap=None):
    """Ventanas (marco + cristal) sobre cada pared vertical rectangular de los ids dados."""
    bm = b.bm
    faces = [f for f in bm.faces if f.material_index in wall_ids and abs(f.normal.z) < 0.15]
    for f in faces:
        n = Vector((f.normal.x, f.normal.y, 0))
        if n.length < 1e-6:
            continue
        n.normalize()
        t = Vector((-n.y, n.x, 0))
        us = [v.co.dot(t) for v in f.verts]
        zs = [v.co.z for v in f.verts]
        u0, u1, z0, z1 = min(us), max(us), min(zs), max(zs)
        w, h = u1 - u0, z1 - z0
        if w < 2.2 or h < 2.4 or f.calc_area() < 0.85 * w * h:
            continue
        if skip_face is not None and skip_face(f):
            continue
        cols = max(1, int((w - 0.2) / spacing))
        rows = max(1, int(h / fh))
        cu, rh = w / cols, h / rows
        if cu < ww + 0.5 or rh < wh + 0.5:
            continue
        c0 = f.calc_center_median()
        d = c0.dot(n)
        for r in range(rows):
            zc = z0 + (r + 0.5) * rh + 0.1
            if zc < skip_below:
                continue
            for c in range(cols):
                uc = u0 + (c + 0.5) * cu
                p = n * d + t * uc + Vector((0, 0, zc))
                if any((p - a[0]).length < a[1] for a in avoid):
                    continue
                if frames:
                    b.quad_on(p, n, t, ww + 0.28, wh + 0.28, frame, 0.03)
                b.quad_on(p, n, t, ww, wh, glass, 0.07)


def find_front(b, wall_ids):
    """Pared +Y mas ancha que toca el suelo."""
    best = None
    for f in b.bm.faces:
        if f.material_index not in wall_ids or f.normal.y < 0.95:
            continue
        xs = [v.co.x for v in f.verts]
        zs = [v.co.z for v in f.verts]
        if min(zs) > 0.3:
            continue
        w = max(xs) - min(xs)
        if best is None or w > best["w"] + 1e-3 or (abs(w - best["w"]) < 1e-3 and f.verts[0].co.y > best["y"]):
            best = dict(f=f, w=w, x0=min(xs), x1=max(xs), y=f.verts[0].co.y, zmax=max(zs))
    return best


def top_faces(b, tol=0.35):
    """Caras horizontales de la cota mas alta: [(centro, area, (hx, hy))] de mayor a menor."""
    ups = [f for f in b.bm.faces if f.normal.z > 0.95]
    if not ups:
        return []
    zt = max(f.calc_center_median().z for f in ups)
    out = []
    for f in ups:
        c = f.calc_center_median()
        if c.z >= zt - tol:
            xs = [v.co.x for v in f.verts]
            ys = [v.co.y for v in f.verts]
            out.append((c, f.calc_area(), ((max(xs) - min(xs)) / 2, (max(ys) - min(ys)) / 2)))
    out.sort(key=lambda t: -t[1])
    return out


def roof_props(b, rng, kinds, detail=True):
    tf = top_faces(b)
    if not tf:
        return
    c, area, (hx, hy) = tf[0]
    z = c.z
    if area < 6:
        return
    ps = []
    for k in kinds:
        px = c.x + rng.uniform(-0.55, 0.55) * hx
        py = c.y + rng.uniform(-0.55, 0.55) * hy
        ps.append((k, px, py))
    for k, px, py in ps:
        if k == "chimney":
            b.box(px - 0.35, px + 0.35, py - 0.35, py + 0.35, z - 1.5, z + 1.3, "CHIM_BRICK", top="TRIM_DARK")
            b.box(px - 0.45, px + 0.45, py - 0.45, py + 0.45, z + 1.3, z + 1.5, "TRIM_DARK")
        elif k == "ac":
            b.box(px - 0.8, px + 0.8, py - 0.5, py + 0.5, z, z + 0.7, "TRIM_GREY", top="TRIM_WHITE")
            if detail:
                b.cyl(px - 0.4, py, 0.28, 0.28, z + 0.7, z + 0.78, "TRIM_DARK", seg=6, top="TRIM_DARK")
        elif k == "tank":
            if detail:
                for sx in (-0.6, 0.6):
                    for sy in (-0.6, 0.6):
                        b.box(px + sx - 0.07, px + sx + 0.07, py + sy - 0.07, py + sy + 0.07, z, z + 1.0, "TRIM_DARK")
            b.cyl(px, py, 1.1, 1.1, z + 1.0, z + 2.4, "TANK", seg=8, top="TRIM_GREY")
            b.cyl(px, py, 1.1, 0.0, z + 2.4, z + 3.0, "TRIM_GREY", seg=8)
        elif k == "antenna":
            b.box(px - 0.08, px + 0.08, py - 0.08, py + 0.08, z, z + 5.5, "TRIM_DARK")
            if detail:
                b.box(px - 0.8, px + 0.8, py - 0.05, py + 0.05, z + 4.4, z + 4.5, "TRIM_DARK")
                b.box(px - 0.5, px + 0.5, py - 0.05, py + 0.05, z + 5.0, z + 5.1, "TRIM_DARK")
        elif k == "stair":
            b.box(px - 1.3, px + 1.3, py - 1.0, py + 1.0, z, z + 2.4, "WALL_CONC", top="ROOF_FLAT")
            b.box(px - 0.45, px + 0.45, py - 1.04, py - 0.96, z, z + 1.8, "DOOR_BLUE") if detail else None
        elif k == "solar":
            for i in range(2):
                b.obox((px + i * 1.9 - 0.9, py, z + 0.55), (1.7, 1.2, 0.08), Matrix.Rotation(math.radians(-25), 3, "X"), "SOLAR", top="SOLAR")
        elif k == "dish":
            b.cyl(px, py, 0.05, 0.05, z, z + 1.0, "TRIM_GREY", seg=4)
            b.cyl(px, py, 0.7, 0.2, z + 1.0, z + 1.35, "TRIM_WHITE", seg=8, top="TRIM_WHITE")
        elif k == "helipad":
            b.cyl(px, py, min(hx, hy) * 0.7, min(hx, hy) * 0.7, z, z + 0.2, "ROOF_FLAT", seg=12, top="HAZARD")


def balconies(b, rng, wall_ids, every=2, cap=40):
    """Balcones (losa + barandilla de cristal) en las paredes +Y anchas de varias plantas."""
    n_added = 0
    for f in list(b.bm.faces):
        if f.material_index not in wall_ids or f.normal.y < 0.95:
            continue
        xs = [v.co.x for v in f.verts]
        zs = [v.co.z for v in f.verts]
        w, h = max(xs) - min(xs), max(zs) - min(zs)
        if w < 5 or h < 5.5 or f.calc_area() < 0.85 * w * h:
            continue
        y = f.verts[0].co.y
        cols = max(1, int((w - 0.2) / 2.6))
        cu = w / cols
        rows = max(1, int(h / 3.2))
        rh = h / rows
        for r in range(1, rows):
            if r % every:
                continue
            for c in range(cols):
                if c % 2:
                    continue
                if n_added >= cap:
                    return
                xc = min(xs) + (c + 0.5) * cu
                zb = min(zs) + r * rh - 0.55
                b.box(xc - 1.0, xc + 1.0, y, y + 1.0, zb, zb + 0.18, "TRIM_WHITE", bottom="TRIM_GREY")
                b.box(xc - 1.0, xc + 1.0, y + 0.92, y + 1.0, zb + 0.18, zb + 1.0, "GLASS")
                n_added += 1


def add_door(b, front, cell, rng, porch=False):
    if front is None:
        return None
    xm = (front["x0"] + front["x1"]) / 2 + rng.uniform(-0.2, 0.2) * max(0, front["w"] - 2.5)
    y = front["y"]
    n = Vector((0, 1, 0))
    t = Vector((1, 0, 0))
    b.quad_on((xm, y, 1.05), n, t, 1.15, 2.1, cell, 0.06)
    b.quad_on((xm, y, 1.05), n, t, 1.45, 2.3, "TRIM_WHITE", 0.03)
    if porch:
        b.box(xm - 1.0, xm + 1.0, y, y + 1.1, 2.45, 2.6, "TRIM_WHITE", bottom="TRIM_GREY")
        for sx in (-0.92, 0.92):
            b.box(xm + sx - 0.05, xm + sx + 0.05, y + 1.0, y + 1.1, 0, 2.45, "TRIM_WHITE")
    return (Vector((xm, y, 1.1)), 1.5)


def storefront(b, front, awn_cells, sign_cell, rng, sign_bars=True, detail=True, ground_h=3.2):
    """Escaparates + puerta + toldo rayado + rotulo en la pared frontal."""
    if front is None:
        return
    y, x0, x1, w = front["y"], front["x0"], front["x1"], front["w"]
    n, t = Vector((0, 1, 0)), Vector((1, 0, 0))
    inset = 0.55
    xm = (x0 + x1) / 2 + rng.choice([-1, 1]) * max(0.0, (w - 3.0) * 0.22)
    # zocalo
    b.box(x0 - 0.04, x1 + 0.04, y, y + 0.14, 0, 0.5, "TRIM_DARK", top="TRIM_GREY")
    # escaparates a cada lado de la puerta
    gx0, gx1 = x0 + inset, x1 - inset
    for (a, bb) in ((gx0, xm - 0.9), (xm + 0.9, gx1)):
        if bb - a > 1.0:
            b.quad_on(((a + bb) / 2, y, 1.65), n, t, bb - a, 2.1, "GLASS", 0.05)
            b.quad_on(((a + bb) / 2, y, 1.65), n, t, bb - a + 0.2, 2.3, "TRIM_DARK", 0.025)
    if detail:
        b.quad_on((xm, y, 1.1), n, t, 1.4, 2.2, "TRIM_DARK", 0.04)
        b.quad_on((xm, y, 1.05), n, t, 1.1, 2.0, "GLASS", 0.07)
    # toldo rayado
    stripes = max(4, int(w / 1.0))
    stripes += stripes % 2
    sw = (x1 - x0) / stripes
    zt, zb, out = 3.35, 2.75, 1.4
    for i in range(stripes):
        cc = awn_cells[0] if i % 2 == 0 else "TRIM_WHITE"
        a, bb = x0 + i * sw, x0 + (i + 1) * sw
        P = [(a, y, zt - 0.02), (bb, y, zt - 0.02), (bb, y + out, zb - 0.02), (a, y + out, zb - 0.02), (a, y, zt), (bb, y, zt), (bb, y + out, zb), (a, y + out, zb)]
        b.hexa(P, cc, cc)
    # rotulo sobre el toldo
    if front["zmax"] > 5.0:
        sw2 = min(w - 1.0, 6.5)
        b.box(xm - sw2 / 2, xm + sw2 / 2, y, y + 0.3, 3.55, 4.65, sign_cell, top="TRIM_DARK")
        if sign_bars and detail:
            n_b = 3
            for k in range(n_b):
                bw = sw2 * (0.7 if k == 0 else 0.45) * 0.9
                b.box(xm - bw / 2, xm + bw / 2, y + 0.3, y + 0.34, 4.2 - k * 0.3, 4.32 - k * 0.3, "TRIM_WHITE")
    else:
        b.box(x0, x1, y + out, y + out + 0.08, zb - 0.28, zb, sign_cell)


# ---------------------------------------------------------------- pack: importacion y analisis
PACK_MATS = {"White": "WALL", "Red": "ROOF", "Blue": "GLASS", "Orange": "DOOR"}


class Source:
    def __init__(self, name, bm):
        self.name = name
        self.bm = bm
        xs = [v.co.x for v in bm.verts]
        ys = [v.co.y for v in bm.verts]
        zs = [v.co.z for v in bm.verts]
        self.dx, self.dy, self.h = max(xs) - min(xs), max(ys) - min(ys), max(zs) - min(zs)
        self.tris = sum(len(f.verts) - 2 for f in bm.faces)
        self.n_glass = sum(1 for f in bm.faces if f.material_index == 3)
        ga = sum(f.calc_area() for f in bm.faces if f.material_index == 3)
        wa = sum(f.calc_area() for f in bm.faces if f.material_index == 0 and abs(f.normal.z) < 0.15)
        self.glassy = ga > 0.10 * max(wa, 1.0)     # ya trae sus ventanas modeladas
        self.door_dir = None
        dr = [f.calc_center_median() for f in bm.faces if f.material_index == 1]
        if dr:
            cx = (max(xs) + min(xs)) / 2
            cy = (max(ys) + min(ys)) / 2
            d = sum(((c.x - cx, c.y - cy) and Vector((c.x - cx, c.y - cy, 0)) for c in dr), Vector())
            if d.length > 1e-3:
                self.door_dir = math.atan2(d.y, d.x)


def load_pack(path):
    bpy.ops.import_scene.fbx(filepath=path)
    out = {}
    objs = [o for o in bpy.data.objects if o.type == "MESH"]
    for o in objs:
        m = re.search(r"\d+(?:\.\d+)?", o.name)
        key = "B" + m.group(0).replace(".", "_")
        bm = bmesh.new()
        bm.from_mesh(o.data)
        bm.transform(o.matrix_world)
        for l in list(bm.loops.layers.uv):
            bm.loops.layers.uv.remove(l)
        for l in list(bm.loops.layers.color):
            bm.loops.layers.color.remove(l)
        # material_index -> 0 White, 1 Orange(door), 2 Red(roof), 3 Blue(glass)
        names = [mt.name for mt in o.data.materials]
        order = {"White": 0, "Orange": 1, "Red": 2, "Blue": 3}
        for f in bm.faces:
            f.material_index = order.get(names[f.material_index], 0) if f.material_index < len(names) else 0
        bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-4)
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
        mn = Vector([min(v.co[i] for v in bm.verts) for i in range(3)])
        mx = Vector([max(v.co[i] for v in bm.verts) for i in range(3)])
        bmesh.ops.translate(bm, vec=-Vector(((mn.x + mx.x) / 2, (mn.y + mx.y) / 2, mn.z)), verts=bm.verts)
        out[key] = Source(key, bm)
    for o in objs:
        bpy.data.objects.remove(o)
    return out


def prepare_pack_bm(src, rot90, sx, sy, sz, palette, glass_cell, top_flat_cell):
    """Copia, rota, escala y mapea materiales del pack a celdas del atlas."""
    bm = src.bm.copy()
    if rot90:
        bmesh.ops.rotate(bm, cent=(0, 0, 0), matrix=Matrix.Rotation(math.radians(90 * rot90), 3, "Z"), verts=bm.verts)
    bmesh.ops.scale(bm, vec=Vector((sx, sy, sz)), space=Matrix.Identity(4), verts=bm.verts)
    bm.normal_update()
    for f in bm.faces:
        mi = f.material_index
        if mi == 0:      # blanco: pared o terraza
            f.material_index = top_flat_cell if f.normal.z > 0.7 else palette["wall"]
        elif mi == 1:
            f.material_index = palette["door"]
        elif mi == 2:
            f.material_index = palette["roof"]
        else:
            f.material_index = glass_cell
    return bm


def find_band(bm, min_gap=2.0):
    """Franja horizontal sin cubiertas ni resaltes donde se puede repetir una planta."""
    zmax = max(v.co.z for v in bm.verts)
    top_limit = zmax
    levels = {0.0}
    for f in bm.faces:
        nz = abs(f.normal.z)
        zs = [v.co.z for v in f.verts]
        if 0.05 < nz < 0.95:
            top_limit = min(top_limit, min(zs))
        elif nz >= 0.95:
            levels.add(round(zs[0], 3))
    levels = sorted(l for l in levels if l <= top_limit + 1e-4)
    levels.append(top_limit)
    best = None
    for a, b_ in zip(levels, levels[1:]):
        if best is None or b_ - a > best[1] - best[0]:
            best = (a, b_)
    if best is None or best[1] - best[0] < min_gap:
        return None
    g = best[1] - best[0]
    h = min(g * 0.7, 3.4)
    z0 = best[0] + (g - h) / 2
    return z0, z0 + h


def repeat_band(bm, z0, z1, k):
    """Inserta k copias de la franja [z0,z1] y sube lo de arriba: se repiten plantas en vez de estirar."""
    if k <= 0:
        return
    for z in (z0, z1):
        bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], dist=1e-5, plane_co=(0, 0, z), plane_no=(0, 0, 1))
    eps = 1e-3
    band = [f for f in bm.faces if all(z0 - eps <= v.co.z <= z1 + eps for v in f.verts) and abs(f.normal.z) < 0.5]
    upper = [f for f in bm.faces if f not in set(band) and min(v.co.z for v in f.verts) >= z1 - eps]
    h = z1 - z0
    ret = bmesh.ops.split(bm, geom=upper)
    up_verts = {v for f in upper if f.is_valid for v in f.verts}
    bmesh.ops.translate(bm, vec=(0, 0, k * h), verts=list(up_verts))
    for i in range(1, k + 1):
        d = bmesh.ops.duplicate(bm, geom=band)
        vs = [e for e in d["geom"] if isinstance(e, bmesh.types.BMVert)]
        bmesh.ops.translate(bm, vec=(0, 0, i * h), verts=vs)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-4)


# ---------------------------------------------------------------- especificaciones del catalogo
# (celdas_x, celdas_y, altura_objetivo_m)
RES = {
    "low": [(1, 1, 4.5), (1, 1, 6.5), (2, 1, 7.5), (2, 1, 9.5), (2, 2, 11)],
    "med": [(2, 1, 9.5), (2, 2, 12), (2, 2, 15), (3, 2, 18), (3, 3, 21)],
    "high": [(2, 2, 26), (2, 2, 34), (3, 2, 42), (3, 3, 50), (3, 3, 58)],
}
COM = {
    "low": [(1, 1, 4.5), (2, 1, 5), (2, 1, 8), (2, 2, 9), (3, 2, 12)],
    "high": [(2, 2, 16), (2, 2, 24), (3, 2, 32), (3, 3, 40), (3, 3, 52)],
}
OFF = [(2, 1, 14), (2, 2, 22), (2, 2, 32), (3, 3, 44), (3, 3, 58)]
VARS = ["A", "B", "C"]

RES_WALLS = ["WALL_CREAM", "WALL_PEACH", "WALL_YELLOW", "WALL_SAND", "WALL_BRICK", "WALL_MINT", "WALL_SKY", "WALL_STONE", "WALL_ROSE", "WALL_LILAC"]
RES_ROOFS = ["ROOF_TERRA", "ROOF_BROWN", "ROOF_SLATE", "ROOF_GREEN", "ROOF_BLUE", "ROOF_REDDARK"]
RES_DOORS = ["DOOR_ORANGE", "DOOR_BROWN", "DOOR_BLUE"]
COM_WALLS = ["WALL_WHITE", "WALL_SKY", "WALL_PEACH", "WALL_MINT", "WALL_YELLOW", "WALL_STONE", "WALL_LILAC", "WALL_CREAM"]
COM_ROOFS = ["ROOF_FLAT", "ROOF_TERRA", "ROOF_SLATE", "ROOF_BLUE"]
AWNINGS = ["AWN_RED", "AWN_BLUE", "AWN_GREEN", "AWN_YELLOW", "AWN_ORANGE", "AWN_PURPLE", "AWN_TEAL"]
OFF_WALLS = ["WALL_STEEL", "WALL_WHITE", "WALL_CONC", "WALL_DARKG", "WALL_SKY", "WALL_STONE"]
OFF_GLASS = ["GLASS_OFF", "GLASS", "GLASS_DARK"]

MARGIN = {"R": 2.0, "C": 3.0, "O": 2.2}
TRI_CAP = 3000


def score_candidate(src, rot, fx, fy, H, zone, used_fam, used_all, want_glassy):
    dx, dy = (src.dx, src.dy) if rot % 2 == 0 else (src.dy, src.dx)
    sx = (fx - MARGIN[zone]) / dx
    sy = (fy - MARGIN[zone]) / dy
    if not (0.55 < sx < 2.6 and 0.55 < sy < 2.6):
        return None
    asp = abs(math.log(sx / sy))
    if asp > 0.42:
        return None
    sm = math.sqrt(sx * sy)
    sz = min(max(sm, 0.85), 1.7 if src.glassy else 1.6)
    h0 = src.h * sz
    if src.glassy:
        hh = h0
    else:
        hh = h0
        k = max(0, int(round((H - h0) / 3.0)))
        hh = h0 + k * 3.0
    cost = asp * 3.0 + abs(math.log(sm)) * 1.2 + abs(math.log(max(hh, 1) / H)) * 7.0
    if src.tris * (1.0 if src.glassy else 1.0) > 2200:
        cost += 4
    cost += 6.0 * used_fam.get(src.name, 0) + 2.0 * used_all.get(src.name, 0)
    if want_glassy and not src.glassy:
        cost += 2.5
    if not want_glassy and src.glassy:
        cost += 1.2
    return cost, sx, sy, sm


def boundary_edges(bm):
    return sum(1 for e in bm.edges if len(e.link_faces) == 1)


def repeat_ok(src, rot, sx, sy, sm, H):
    """True si repetir plantas no abre la malla (ejecuta el mismo recorrido y compara aristas abiertas)."""
    if src.glassy:
        return True
    sz = min(max(sm, 0.85), 1.6)
    bm = prepare_pack_bm(src, rot, sx, sy, sz, dict(wall=0, roof=0, door=0), 0, 0)
    h0 = max(v.co.z for v in bm.verts)
    band = find_band(bm)
    ok = True
    if band is not None:
        k = min(14, max(0, int(round((H - h0) / (band[1] - band[0])))))
        if k > 0:
            before = boundary_edges(bm)
            repeat_band(bm, band[0], band[1], k)
            ok = boundary_edges(bm) <= before
    bm.free()
    return ok


def choose_source(pack, fx, fy, H, zone, used_fam, used_all, want_glassy, front_needed=False, tall_min=0.0):
    best = None
    for src in pack.values():
        if src.tris > 2600:
            continue
        if zone in "CO" and src.dx * src.dy < 20:
            continue
        for rot in range(4):
            sc = score_candidate(src, rot, fx, fy, H, zone, used_fam, used_all, want_glassy)
            if sc is None:
                continue
            cost = sc[0]
            # preferir la puerta mirando a +Y tras rotar
            if src.door_dir is not None:
                ang = (src.door_dir + math.radians(90 * rot)) % (2 * math.pi)
                dd = abs((ang - math.pi / 2 + math.pi) % (2 * math.pi) - math.pi)
                cost += 0.6 * dd / math.pi
            if best is None or cost < best[0]:
                best = (cost, src, rot, sc[1], sc[2], sc[3])
    return best


# ---------------------------------------------------------------- construccion de edificios del pack
def build_pack_building(src, rot, sx, sy, sm, zone, dens, level, var, H, rng, key, detail=True, window_opts=None):
    window_opts = window_opts or dict(frames=True, spacing=2.6)
    if zone == "R":
        crown = RES_ROOFS if dens == "low" else (RES_ROOFS[:3] + ["WALL_STONE", "WALL_CONC", "WALL_SAND"] if dens == "med" else ["WALL_STONE", "WALL_CONC", "WALL_SAND", "WALL_BRICK", "ROOF_TERRA", "WALL_WHITE"])
        pal = dict(wall=C[pick(RES_WALLS, key + "w", var)], roof=C[pick(crown, key + "r", var)], door=C[pick(RES_DOORS, key + "d", var)])
        glass, frame, flat = C["GLASS"], "TRIM_WHITE", C["ROOF_FLAT"]
    elif zone == "C":
        crown = ["WALL_CONC", "WALL_STONE", "WALL_WHITE"] if dens == "high" else COM_ROOFS
        pal = dict(wall=C[pick(COM_WALLS, key + "w", var)], roof=C[pick(crown, key + "r", var)], door=C["DOOR_BROWN"])
        glass, frame, flat = C["GLASS"], "TRIM_WHITE", C["ROOF_FLAT"]
    else:
        pal = dict(wall=C[pick(OFF_WALLS, key + "w", var)], roof=C[pick(["WALL_CONC", "WALL_STEEL", "WALL_WHITE"], key + "r", var)], door=C["TRIM_DARK"])
        glass, frame, flat = C[pick(OFF_GLASS, key + "g", var)], "TRIM_DARK", C["ROOF_FLAT"]
    sz = min(max(sm, 0.85), 1.7 if src.glassy else 1.6)
    bm = prepare_pack_bm(src, rot, sx, sy, sz, pal, glass, flat)
    b = Builder(bm)
    if not src.glassy:
        band = find_band(bm)
        h0 = max(v.co.z for v in bm.verts)
        if band is not None:
            unit = band[1] - band[0]
            k = max(0, int(round((H - h0) / unit)))
            k = min(k, 14)
            repeat_band(bm, band[0], band[1], k)
    bm.normal_update()
    # cubierta: las partes superiores horizontales blancas ya son ROOF_FLAT; las rojas toman el color de cubierta
    walls = {pal["wall"]}
    mn, mx = b.bounds()
    front = find_front(b, walls)
    avoid = []
    if detail:
        if zone == "R":
            d = add_door(b, front, pal["door"], rng, porch=(dens == "low" and level >= 2))
            if d:
                avoid.append(d)
            windowize(b, walls, glass, frame, avoid=avoid, **window_opts) if not src.glassy else None
            if dens != "low" and level >= 3:
                balconies(b, rng, walls, every=2 if dens == "high" else 1, cap=24)
            kinds = {"low": ["chimney"] if rng.random() < 0.7 else ["ac"], "med": ["ac", "ac", "tank"], "high": ["tank", "stair", "antenna", "ac"]}[dens]
            if dens == "low" and level >= 4:
                kinds = ["chimney", "ac"]
            if dens != "low" and level >= 4 and rng.random() < 0.5:
                kinds = kinds + ["solar"]
            roof_props(b, rng, kinds, detail)
        elif zone == "C":
            awn = pick(AWNINGS, key + "a", var)
            storefront(b, front, [awn], pick(AWNINGS, key + "s", var + 2), rng, detail=detail)
            sk = None
            if front is not None:
                sk = front["f"]
            windowize(b, walls, glass, frame, skip_below=0.0, skip_face=(lambda f, sk=sk: False), **window_opts) if not src.glassy else None
            # quitar ventanas del zocalo frontal: las del piso 0 solapan el escaparate -> se generan antes con skip_below
            roof_props(b, rng, ["ac", "ac", "antenna"] if dens == "high" else ["ac", "ac"], detail)
        else:
            if front is not None:
                w = front["w"]
                b.box(front["x0"] + w * 0.3, front["x1"] - w * 0.3, front["y"], front["y"] + 1.6, 3.1, 3.3, "TRIM_DARK", bottom="TRIM_DARK")
                b.quad_on(((front["x0"] + front["x1"]) / 2, front["y"], 1.5), Vector((0, 1, 0)), Vector((1, 0, 0)), w * 0.36, 2.8, "GLASS_DARK", 0.05)
                avoid.append((Vector(((front["x0"] + front["x1"]) / 2, front["y"], 1.5)), w * 0.3))
            windowize(b, walls, glass, frame, avoid=avoid, fh=3.6, wh=1.9, ww=1.6, **window_opts) if not src.glassy else None
            roof_props(b, rng, ["stair", "ac", "antenna", "dish"] if H > 30 else ["ac", "ac", "dish"], detail)
            if H > 50 and level == 5:
                roof_props(b, rng, ["helipad"], detail)
    return b


# ---------------------------------------------------------------- industria (modelado entero)
def build_industry(level, var, lod, rng):
    b = Builder()
    detail = lod == 0
    cells = [(1, 1), (2, 1), (2, 2), (3, 2), (3, 3)][level - 1]
    fx, fy = cells[0] * CELL_M - 3.0, cells[1] * CELL_M - 3.0
    wallc = ["WALL_METAL", "WALL_CONC", "WALL_STEEL", "WALL_STONE"][(level + var) % 4]
    roofc = ["ROOF_FLAT", "ROOF_BLUE", "ROOF_SLATE", "ROOF_GREEN"][var]
    x0, x1, y0, y1 = -fx / 2, fx / 2, -fy / 2, fy / 2

    def chimney(cx, cy, h, r=1.1, striped=True):
        b.cyl(cx, cy, r * 1.3, r, 0, 2.0, "WALL_CONC", seg=8)
        n = 5
        for i in range(n):
            za = 2.0 + (h - 2.0) * i / n
            zb = 2.0 + (h - 2.0) * (i + 1) / n
            ra = r * (1 - 0.25 * i / n)
            rb = r * (1 - 0.25 * (i + 1) / n)
            b.cyl(cx, cy, ra, rb, za, zb, "RED" if (i % 2 == 0 and striped) else "WHITE" if striped else "CHIM_BRICK", seg=8)
        b.cyl(cx, cy, r * 0.8, r * 0.8, h - 0.2, h + 0.25, "TRIM_DARK", seg=8, top="COAL")

    def tank(cx, cy, r, h, cell="TANK", dome=True):
        b.cyl(cx, cy, r, r, 0, h, cell, seg=10, top="TRIM_GREY" if not dome else None)
        if dome:
            b.cyl(cx, cy, r, 0.0, h, h + r * 0.45, "TRIM_GREY", seg=10)

    def bays(x0_, x1_, y_, n, wmax, cell="DOOR_BLUE"):
        sp = (x1_ - x0_) / n
        for i in range(n):
            xc = x0_ + (i + 0.5) * sp
            b.quad_on((xc, y_, 2.1), Vector((0, 1, 0)), Vector((1, 0, 0)), min(wmax, sp - 1.0), 4.0, cell, 0.06)
            b.quad_on((xc, y_, 2.1), Vector((0, 1, 0)), Vector((1, 0, 0)), min(wmax, sp - 1.0) + 0.4, 4.2, "HAZARD", 0.03)

    flip = -1 if var == 1 else 1
    if level == 1:
        b.gable(x0, x1, y0, y1, 0, 4.2, 1.8, wallc, roofc, axis="x" if var != 2 else "y", ovh=0.3)
        bays(x0 + 1, x1 - 1, y1, 1, 4.2)
        b.box(x1 - 1.4, x1 - 0.4, y0 + 0.6, y0 + 1.6, 0, 6.5, "CHIM_BRICK", top="TRIM_DARK")
        if detail:
            b.box(x0 + 0.5, x0 + 2.5, y1 - 0.01, y1 + 1.5, 0, 0.9, "TRIM_GREY")
    elif level == 2:
        b.gable(x0, x1 - 6, y0, y1, 0, 5.0, 2.0, wallc, roofc, axis="y", ovh=0.3)
        b.box(x1 - 6, x1, y0 + 2, y1, 0, 3.2, "WALL_CONC", top="ROOF_FLAT")
        bays(x0 + 1, x1 - 7, y1, 2, 4.0)
        chimney(x1 - 3, y0 + 4, 12.0, 0.9)
        tank(x1 - 3, y1 - 2.5, 1.6, 3.4) if var != 1 else tank(x0 + 3, y0 + 2, 1.8, 3.0)
        if detail:
            for i in range(3):
                b.box(x0 + 2 + i * 3, x0 + 3.2 + i * 3, y1 + 0.3, y1 + 1.2, 0, 1.0, "HAZARD")
    elif level == 3:
        n = 4
        b.box(x0, x1, y0, y1 - 4, 0, 5.0, wallc, top="ROOF_FLAT")
        b.sawtooth(x0, x1, y0, y1 - 4, 5.0, n, wallc, roofc, "GLASS_OFF", rh=1.8)
        b.box(x0 + 1, x1 - 1, y1 - 4, y1, 0, 3.4, "WALL_CONC", top="ROOF_FLAT")
        bays(x0 + 2, x1 - 2, y1, 3, 4.0)
        chimney(x1 - 3, y0 + 3, 18.0, 1.1)
        b.box(x0 - 1.4, x0, y0 + 2, y1 - 6, 0, 3.0, "WALL_CONC", top="ROOF_FLAT")
        tank(x0 - 3.0 if False else x0 - 1.4 + 0.7, y1 - 1.6, 1.2, 4.5)
        if detail:
            b.box(x0 + 1, x1 - 1, y1 - 4.1, y1 - 3.6, 5.0, 5.3, "TRIM_DARK")
    elif level == 4:
        b.box(x0, x1, y0, y0 + 14, 0, 7.0, wallc, top="ROOF_FLAT")
        b.gable(x0, x1, y0 + 14, y1, 0, 5.0, 2.0, wallc, roofc, axis="x", ovh=0.3)
        bays(x0 + 2, x1 - 2, y1, 3, 4.6)
        chimney(x0 + 4, y0 + 4, 24.0, 1.3)
        chimney(x0 + 9, y0 + 4, 20.0, 1.1)
        tank(x1 + 0.0 - 3.5, y0 + 4, 2.4, 9.0)
        tank(x1 - 3.5, y0 + 10, 2.0, 7.0)
        if detail:
            b.box(x0 + 2, x1 - 2, y0 + 14.0, y0 + 14.8, 5.0, 5.8, "TRIM_DARK")
            for i in range(5):
                b.box(x0 + 3 + i * 3.5, x0 + 4.4 + i * 3.5, y0 + 13.8, y0 + 14.1, 3, 7.0, "HAZARD")
    else:
        b.gable(x0, x1, y0 + 10, y1, 0, 6.5, 2.6, wallc, roofc, axis="x", ovh=0.3)
        b.box(x0, x1 - 8, y0, y0 + 10, 0, 9.0, "WALL_CONC", top="ROOF_FLAT")
        b.box(x1 - 8, x1, y0, y0 + 10, 0, 5.0, wallc, top="ROOF_FLAT")
        bays(x0 + 2, x1 - 2, y1, 4, 4.6)
        for i, (h, r) in enumerate([(30.0, 1.5), (26.0, 1.3), (22.0, 1.1)]):
            chimney(x0 + 4 + i * 5.0, y0 + 4.5, h, r)
        for i in range(3):
            tank(x1 - 4.0, y0 + 20 + i * 0.0 if False else y0 + 14 + i * 4.2, 1.6, 8.0 - i, "TANK")
        if detail:
            b.box(x0 + 1, x1 - 1, y0 + 10.1, y0 + 10.7, 9.0, 9.7, "TRIM_DARK")
            for i in range(6):
                b.box(x0 + 2 + i * 4.6, x0 + 3.0 + i * 4.6, y0 + 9.6, y0 + 10.4, 5.0, 9.0, "HAZARD")
    if level >= 2 and var == 2:
        # silos de grano/cemento junto a la nave
        for i in range(3 if level >= 4 else 2):
            sx = x0 + 1.8 + i * 3.1
            b.cyl(sx, y0 + 1.8, 1.4, 1.4, 0, 9.0 + i * 1.5, "TANK", seg=9)
            b.cyl(sx, y0 + 1.8, 1.4, 0.0, 9.0 + i * 1.5, 10.4 + i * 1.5, "TRIM_GREY", seg=9)
        b.box(x0 + 0.4, x0 + 1.0 + 2 * 3.1 + 2.4, y0 + 3.6, y0 + 4.2, 6.0, 6.4, "TRIM_DARK") if detail else None
    if level >= 2 and var == 1:
        roof_props(b, rng, ["solar", "ac"], detail) if detail else None
    if detail:
        wc = {C[wallc], C["WALL_CONC"]}
        windowize(b, wc, C["GLASS"], "TRIM_DARK", fh=4.6, spacing=3.6, ww=2.2, wh=1.0, frames=True, skip_below=0.0)
    if var == 1:
        bmesh.ops.scale(b.bm, vec=Vector((-1, 1, 1)), verts=b.bm.verts)
        bmesh.ops.reverse_faces(b.bm, faces=b.bm.faces[:])
    return b


# ---------------------------------------------------------------- servicios (modelado entero)
def build_service(kind, lod, rng):
    b = Builder()
    detail = lod == 0
    if kind == "water_pump":
        # 8x8: caseta con bomba y torre de deposito
        b.box(-3.2, 1.0, -3.0, 1.5, 0, 3.4, "WALL_STONE", top="ROOF_BLUE")
        b.box(-3.4, 1.2, -3.2, 1.7, 3.4, 3.7, "ROOF_BLUE", top="ROOF_BLUE")
        b.quad_on((-1.0, 1.5, 1.2), Vector((0, 1, 0)), Vector((1, 0, 0)), 1.5, 2.3, "DOOR_BLUE", 0.05)
        for i in range(2):
            b.quad_on((-2.3 + i * 2.8, 1.5, 2.2), Vector((0, 1, 0)), Vector((1, 0, 0)), 0.9, 0.9, "GLASS", 0.05)
        for sx in (2.2, 3.6):
            for sy in (-2.4, -0.6):
                b.box(sx - 0.15, sx + 0.15, sy - 0.15, sy + 0.15, 0, 7.5, "TRIM_GREY")
        b.cyl(2.9, -1.5, 2.4, 2.4, 7.5, 11.0, "WATER", seg=10, top="TRIM_GREY")
        b.cyl(2.9, -1.5, 2.4, 0.0, 11.0, 12.2, "TRIM_GREY", seg=10)
        b.box(1.0, 2.8, 0.2, 0.6, 0, 1.0, "TRIM_GREY")
        b.cyl(-0.8, 2.4, 0.5, 0.5, 0, 0.9, "WATER", seg=8, top="WATER")
        if detail:
            b.cyl(2.9, -1.5, 0.12, 0.12, 0, 7.5, "TRIM_DARK", seg=4)
            b.box(1.0, 4.4, 1.6, 2.0, 0, 0.8, "TRIM_GREY")
    elif kind == "drain":
        # 8x8: estacion de desague: caseta + rejilla + tuberia a un canal
        b.box(-3.4, 0.4, -3.4, 0.2, 0, 3.0, "WALL_CONC", top="ROOF_FLAT")
        b.box(-3.6, 0.6, -3.6, 0.4, 3.0, 3.25, "ROOF_FLAT")
        b.quad_on((-1.5, 0.2, 1.2), Vector((0, 1, 0)), Vector((1, 0, 0)), 1.4, 2.2, "DOOR_BROWN", 0.05)
        b.box(1.0, 3.8, -3.8, 3.8, 0, 0.3, "WALL_CONC", top="WATER")       # canal
        b.box(0.7, 1.0, -3.8, 3.8, 0, 0.9, "WALL_CONC")
        b.box(3.8, 4.0, -3.8, 3.8, 0, 0.9, "WALL_CONC")
        b.cyl(0.9, -1.6, 0.9, 0.9, 1.1, 2.2, "TRIM_GREY", seg=8)
        b.box(-3.4, 0.4, 0.8, 3.6, 0, 0.35, "TRIM_DARK", top="TRIM_GREY")  # rejilla
        for i in range(8):
            b.box(-3.3 + i * 0.45, -3.1 + i * 0.45, 0.85, 3.55, 0.35, 0.5, "TRIM_DARK")
        b.cyl(-1.8, -1.6, 0.7, 0.7, 3.25, 4.4, "TRIM_GREY", seg=8, top="TRIM_DARK")
        if detail:
            b.cyl(2.4, 2.6, 0.7, 0.7, 0.3, 1.4, "TRIM_GREY", seg=8, top="TRIM_DARK")
    elif kind == "wind":
        # 8x8: aerogenerador de 42 m
        b.cyl(0, 0, 2.4, 2.4, 0, 0.7, "WALL_CONC", seg=10, top="WALL_CONC")
        b.cyl(0, 0, 0.9, 0.5, 0.7, 30, "WHITE", seg=10)
        b.box(-0.55, 0.55, -1.2, 1.3, 30, 31.4, "TRIM_WHITE" if False else "WHITE", top="TRIM_GREY")
        b.box(-0.35, 0.35, 1.3, 1.9, 30.2, 31.2, "TRIM_GREY")
        hub = Vector((0, 2.0, 30.7))
        for k in range(3):
            ang = math.radians(90 + k * 120)
            dirv = Vector((math.cos(ang), 0, math.sin(ang)))
            rot = Matrix.Rotation(ang - math.radians(90) + math.radians(90), 3, "Y")
            # pala: caja larga y estrecha orientada en el plano XZ
            ln = 13.0
            centre = hub + dirv * (ln / 2 + 0.9)
            n = Vector((0, 1, 0))
            R = Matrix(((dirv.x, 0, -dirv.z), (0, 1, 0), (dirv.z, 0, dirv.x))) if False else Matrix((dirv, Vector((0, 1, 0)), dirv.cross(Vector((0, 1, 0))))).transposed()
            b.obox(centre, (ln, 0.35, 1.1), R, "WHITE", top="WHITE")
            if detail:
                b.obox(hub + dirv * (ln + 0.3), (1.6, 0.4, 0.5), R, "RED")
        # buje: cilindro a lo largo de y
        b.obox(Vector((0, 2.1, 30.7)), (1.1, 0.8, 1.1), Matrix.Identity(3), "TRIM_WHITE")
        if detail:
            b.box(-1.2, 1.2, -0.2, 0.2, 0.7, 1.6, "TRIM_GREY")
    elif kind == "coal":
        # 24x24: central termica
        b.box(-10, 3, -10, 6, 0, 14, "WALL_CONC", top="ROOF_FLAT")                # sala de calderas
        b.box(-10.4, 3.4, -10.4, 6.4, 14, 14.5, "TRIM_DARK")
        b.box(3, 10, -10, 0, 0, 9, "WALL_STEEL", top="ROOF_FLAT")                # turbinas
        b.gable(-10, 3, 6, 11, 0, 6, 2, "WALL_METAL", "ROOF_SLATE", axis="x")      # almacen
        for i, (cx, cy, h) in enumerate([(-7.0, -5.0, 40.0), (-2.0, -5.0, 34.0)]):
            b.cyl(cx, cy, 2.4, 2.4, 14.5, 18, "WALL_CONC", seg=10)
            for j in range(6):
                za = 18 + (h - 18) * j / 6
                zb = 18 + (h - 18) * (j + 1) / 6
                r0 = 2.2 - 0.9 * j / 6
                r1 = 2.2 - 0.9 * (j + 1) / 6
                b.cyl(cx, cy, r0, r1, za, zb, "RED" if j % 2 == 0 else "WHITE", seg=10)
            b.cyl(cx, cy, 1.1, 1.1, h - 0.2, h + 0.3, "TRIM_DARK", seg=10, top="COAL")
        # montones de carbon
        b.cyl(7.0, 6.0, 4.0, 1.0, 0, 3.6, "COAL", seg=9, top="COAL")
        # cinta transportadora
        b.box(3, 12, 1.0, 1.8, 7.5, 7.8, "TRIM_DARK")
        for xx in (4.0, 7.5, 11.0):
            b.box(xx - 0.2, xx + 0.2, 1.1, 1.7, 0, 7.5, "TRIM_GREY")
        # torre de refrigeracion hiperbolica
        cx, cy = 7.0, -5.0 - 6.0 - 0.0 if False else -16.0 + 12.0
        # tuberia entre sala y chimeneas
        b.box(-9.0, -0.5, -7.5, -6.8, 14.5, 15.2, "TRIM_GREY")
        if detail:
            b.box(-4.5, -3.5, 6.4, 6.9, 0, 6, "HAZARD")
            b.quad_on((-3, 6.0 + 5.0, 3.0), Vector((0, 1, 0)), Vector((1, 0, 0)), 3.0, 4.0, "DOOR_BLUE", 0.05)
            for i in range(4):
                b.box(-9 + i * 3.0, -8 + i * 3.0, 6.0, 6.3, 3.0, 12.0, "HAZARD")
        # recinto 24x24 con marcas para fijar la huella
        b.box(-11.9, 11.9, -11.9, 11.9, 0, 0.12, "PATH", top="PATH")
    elif kind == "landfill":
        # 24x24: monton de residuos + caseta + camino
        b.box(-11.9, 11.9, -11.9, 11.9, 0, 0.12, "DUMP", top="DUMP")
        b.cyl(-1.5, -2.0, 11.0, 5.0, 0.12, 6.0, "DUMP", seg=12, top="GRASS")
        b.cyl(-2.5, -3.0, 5.0, 0.0, 6.0, 8.4, "GRASS", seg=12)
        b.cyl(5.5, 4.5, 4.5, 1.6, 0.12, 3.2, "DUMP", seg=9, top="TRIM_DARK")
        b.box(5.5, 11.2, -11.2, -6.0, 0, 3.2, "WALL_SAND", top="ROOF_BROWN")                  # oficina
        b.box(5.3, 11.4, -11.4, -5.8, 3.2, 3.45, "ROOF_BROWN")
        b.quad_on((8.3, -6.0, 1.2), Vector((0, 1, 0)), Vector((1, 0, 0)), 1.3, 2.2, "DOOR_ORANGE", 0.05)
        b.gable(-11.5, -4.5, 5.0, 11.5, 0.12, 3.2, 1.6, "WALL_METAL", "ROOF_SLATE", axis="y")       # nave de clasificacion
        # contenedores
        for i, cc in enumerate(["AWN_GREEN", "AWN_BLUE", "AWN_YELLOW", "AWN_RED"]):
            b.box(-1.0 + i * 2.6, 0.9 + i * 2.6, 8.4, 11.0, 0.12, 2.0, cc, top="TRIM_DARK")
        if detail:
            for i in range(6):
                a = rng.random() * math.tau
                b.box(math.cos(a) * 6.0 - 0.4, math.cos(a) * 6.0 + 0.4, math.sin(a) * 5.0 - 0.9, math.sin(a) * 5.0 - 0.1, 3.0, 3.6, rng.choice(["WHITE", "TRIM_GREY", "AWN_BLUE", "AWN_RED"]))
    elif kind == "health":
        # 16x16: centro de salud blanco con cruz
        b.box(-6.5, 6.5, -6.5, 5.0, 0, 11.0, "WHITE", top="ROOF_FLAT")
        b.box(-6.9, 6.9, -6.9, 5.4, 11.0, 11.4, "TRIM_GREY")
        b.box(-3.5, 3.5, 5.0, 7.2, 0, 3.4, "WALL_MINT", top="TRIM_WHITE")                          # marquesina
        b.box(-3.7, 3.7, 5.0, 7.4, 3.4, 3.65, "TRIM_WHITE")
        for sx in (-3.4, 3.4):
            b.box(sx - 0.15, sx + 0.15, 6.8, 7.2, 0, 3.4, "TRIM_WHITE")
        b.quad_on((0, 5.0, 1.5), Vector((0, 1, 0)), Vector((1, 0, 0)), 3.0, 2.8, "GLASS", 0.06)
        # cruz roja
        b.quad_on((0, 5.0, 8.4), Vector((0, 1, 0)), Vector((1, 0, 0)), 0.9, 3.0, "RED", 0.07)
        b.quad_on((0, 5.0, 8.4), Vector((0, 1, 0)), Vector((1, 0, 0)), 3.0, 0.9, "RED", 0.07)
        windowize(b, {C["WHITE"]}, C["GLASS"], "TRIM_GREY", frames=detail, spacing=2.8, wh=1.4, avoid=[(Vector((0, 5.0, 8.4)), 2.4), (Vector((0, 5.0, 1.5)), 3.2)]) if detail else None
        roof_props(b, rng, ["helipad", "ac"], detail)
        b.box(2.2, 8.0, -6.4, -1.0, 0, 4.4, "WALL_MINT", top="ROOF_FLAT")                          # ala de urgencias
    elif kind == "fire":
        # 16x8: parque de bomberos con 3 puertas y torre de mangueras
        b.box(-7.2, 7.2, -3.0, 3.2, 0, 7.0, "WALL_BRICK", top="ROOF_FLAT")
        b.box(-7.5, 7.5, -3.4, 3.6, 7.0, 7.4, "TRIM_WHITE")
        b.box(5.0, 7.4, -3.0, -0.6, 7.4, 16.0, "WALL_BRICK", top="FIRE")
        b.box(4.8, 7.6, -3.2, -0.4, 16.0, 16.4, "TRIM_WHITE")
        for i in range(3):
            xc = -5.2 + i * 4.3
            b.box(xc - 1.6, xc + 1.6, 3.2, 3.3, 0.0, 4.6, "FIRE")
            b.quad_on((xc, 3.3, 2.25), Vector((0, 1, 0)), Vector((1, 0, 0)), 2.8, 4.1, "TRIM_WHITE", 0.03)
            b.quad_on((xc, 3.3, 2.25), Vector((0, 1, 0)), Vector((1, 0, 0)), 2.4, 3.8, "FIRE", 0.05)
            if detail:
                for k in range(4):
                    b.quad_on((xc, 3.3, 0.9 + k * 0.9), Vector((0, 1, 0)), Vector((1, 0, 0)), 2.4, 0.12, "TRIM_WHITE", 0.07)
        b.box(-6.5, 6.5, 3.2, 3.5, 5.4, 6.6, "FIRE")
        b.quad_on((0, 3.5, 6.0), Vector((0, 1, 0)), Vector((1, 0, 0)), 5.0, 0.8, "TRIM_WHITE", 0.03) if detail else None
        if detail:
            b.quad_on((6.2, -0.58, 12.0), Vector((0, 1, 0)), Vector((1, 0, 0)), 1.0, 1.1, "GLASS", 0.05)
            b.quad_on((6.2, -0.58, 9.5), Vector((0, 1, 0)), Vector((1, 0, 0)), 1.0, 1.1, "GLASS", 0.05)
    elif kind == "police":
        b.box(-7.2, 7.2, -3.4, 3.4, 0, 8.0, "WALL_SKY", top="ROOF_FLAT")
        b.box(-7.5, 7.5, -3.7, 3.7, 8.0, 8.4, "POLICE")
        b.box(-7.5, 7.5, 3.4, 3.7, 6.3, 8.0, "POLICE")
        b.box(-7.2, 7.2, 3.4, 3.55, 0, 0.8, "TRIM_DARK")
        b.box(-2.6, 2.6, 3.4, 5.4, 0, 3.4, "WALL_STONE", top="POLICE")
        b.quad_on((0, 5.4, 1.5), Vector((0, 1, 0)), Vector((1, 0, 0)), 2.0, 2.8, "GLASS", 0.05)
        # escudo/estrella en el friso
        b.box(-0.7, 0.7, 3.7, 3.82, 6.5, 7.8, "HAZARD")
        for i, xx in enumerate((-5.4, -3.2, 3.2, 5.4)):
            if detail:
                for r in range(2):
                    b.quad_on((xx, 3.4, 2.0 + r * 2.9), Vector((0, 1, 0)), Vector((1, 0, 0)), 1.4, 1.5, "GLASS", 0.06)
                    b.quad_on((xx, 3.4, 2.0 + r * 2.9), Vector((0, 1, 0)), Vector((1, 0, 0)), 1.7, 1.8, "TRIM_WHITE", 0.03)
        # mastil y luz
        b.box(5.5, 5.7, -1.5, -1.3, 8.4, 13, "TRIM_GREY")
        b.box(5.0, 6.2, -1.9, -0.9, 13, 13.4, "POLICE")
    elif kind == "school":
        # 24x16: ala baja en L + reloj + pista
        b.box(-11, 4, -4, 2, 0, 7.2, "SCHOOL", top="ROOF_TERRA")
        b.box(-11.4, 4.4, -4.4, 2.4, 7.2, 7.6, "ROOF_TERRA")
        b.box(-11, -4, 2, 6.5, 0, 7.2, "SCHOOL", top="ROOF_TERRA")
        b.box(-11.4, -3.6, 1.6, 6.9, 7.2, 7.6, "ROOF_TERRA")
        b.box(-1.8, 1.8, 2, 3.4, 0, 4.0, "WALL_CREAM", top="ROOF_TERRA")
        b.quad_on((0, 3.4, 1.6), Vector((0, 1, 0)), Vector((1, 0, 0)), 2.4, 2.6, "DOOR_BLUE", 0.05)
        b.box(-1.9, 1.9, -1.6, 1.6, 7.6, 15.0, "SCHOOL", top="ROOF_TERRA")
        b.quad_on((0, 1.6, 12.2), Vector((0, 1, 0)), Vector((1, 0, 0)), 2.0, 2.0, "TRIM_WHITE", 0.05)
        b.quad_on((0, 1.6, 12.2), Vector((0, 1, 0)), Vector((1, 0, 0)), 1.5, 1.5, "TRIM_DARK", 0.07)
        b.cyl(0, 0, 2.6, 0.0, 15.0, 18.0, "ROOF_TERRA", seg=4, rot=math.pi / 4)
        windowize(b, {C["SCHOOL"]}, C["GLASS"], "TRIM_WHITE", fh=3.6, spacing=2.6, wh=1.7, frames=detail, avoid=[(Vector((0, 3.4, 1.6)), 2.3), (Vector((0, 1.6, 12.2)), 2.5)]) if detail else None
        # patio con pista y arboles
        b.box(5.0, 11.8, -2.6, 7.2, 0, 0.12, "ROOF_BLUE", top="ROOF_BLUE")
        for (px, py) in ((6.0, 7.6), (10.4, 7.6)):
            b.box(px - 0.1, px + 0.1, py - 0.1, py + 0.1, 0.12, 2.6, "TRIM_WHITE")
        for (px, py) in ((-9.0, -6.5), (-4.0, -6.8), (8.5, -6.2)):
            b.cyl(px, py, 0.18, 0.15, 0, 1.6, "TRUNK", seg=5)
            b.cyl(px, py, 1.5, 1.1, 1.4, 3.4, "TREE", seg=7)
            b.cyl(px, py, 1.1, 0.0, 3.4, 4.6, "TREE", seg=7)
    elif kind == "park":
        # 16x16: cesped, caminos, estanque, arboles, banco, farolas
        b.box(-7.8, 7.8, -7.8, 7.8, 0, 0.1, "GRASS", top="GRASS")
        b.box(-0.9, 0.9, -7.8, 7.8, 0.1, 0.14, "PATH", top="PATH")
        b.box(-7.8, 7.8, -0.9, 0.9, 0.1, 0.14, "PATH", top="PATH")
        b.cyl(0, 0, 2.6, 2.6, 0.1, 0.5, "TRIM_GREY", seg=12, top="WATER")
        b.cyl(0, 0, 0.5, 0.4, 0.5, 1.8, "TRIM_GREY", seg=8, top="WATER")
        spots = [(-5.0, -5.0, 1.0), (5.0, -5.0, 1.2), (-5.0, 5.2, 1.1), (5.2, 5.0, 0.9), (-3.0, 3.0, 0.7), (3.4, -3.0, 0.8)]
        folcs = ["TREE", "TREE_LIGHT", "TREE", "TREE_PINK", "TREE_LIGHT", "TREE"]
        for (px, py, s), fc in zip(spots, folcs):
            b.cyl(px, py, 0.28 * s, 0.22 * s, 0.1, 1.9 * s, "TRUNK", seg=5)
            b.cyl(px, py, 2.0 * s, 1.5 * s, 1.7 * s, 3.8 * s, fc, seg=7, top=fc)
            b.cyl(px, py, 1.4 * s, 0.0, 3.8 * s, 5.2 * s, fc, seg=7) if fc != "TREE_PINK" else b.cyl(px, py, 1.5 * s, 0.9 * s, 3.8 * s, 4.8 * s, fc, seg=7, top=fc)
        # bancos y farolas
        for (px, py, rz) in ((2.2, 1.9, 0), (-2.2, -1.9, 0), (1.9, -2.2, 1), (-1.9, 2.2, 1)):
            if rz == 0:
                b.box(px - 0.8, px + 0.8, py - 0.25, py + 0.25, 0.4, 0.5, "TRUNK", top="TRUNK")
                b.box(px - 0.8, px + 0.8, py + 0.2, py + 0.25, 0.5, 1.0, "TRUNK")
                b.box(px - 0.7, px - 0.6, py - 0.2, py + 0.2, 0.1, 0.4, "TRIM_DARK")
                b.box(px + 0.6, px + 0.7, py - 0.2, py + 0.2, 0.1, 0.4, "TRIM_DARK")
            else:
                b.box(px - 0.25, px + 0.25, py - 0.8, py + 0.8, 0.4, 0.5, "TRUNK", top="TRUNK")
                b.box(px - 0.25, px - 0.2, py - 0.8, py + 0.8, 0.5, 1.0, "TRUNK")
        for (px, py) in ((-6.4, 0.0), (6.4, 0.0), (0.0, 6.4), (0.0, -6.4)):
            b.box(px - 0.07, px + 0.07, py - 0.07, py + 0.07, 0.1, 3.4, "TRIM_DARK")
            b.box(px - 0.3, px + 0.3, py - 0.3, py + 0.3, 3.4, 3.7, "HAZARD", top="TRIM_WHITE")
        # parterres de flores
        if detail:
            for (px, py, fc) in ((-6.0, -2.4, "AWN_RED"), (-6.0, 2.4, "AWN_YELLOW"), (6.0, 2.4, "AWN_PURPLE"), (6.0, -2.4, "AWN_ORANGE")):
                b.box(px - 1.0, px + 1.0, py - 0.7, py + 0.7, 0.1, 0.35, fc, top=fc)
    else:
        raise ValueError(kind)
    return b


SERVICE_SPEC = {
    "water_pump": (1, 1), "drain": (1, 1), "wind": (1, 1), "coal": (3, 3), "landfill": (3, 3),
    "health": (2, 2), "fire": (2, 1), "police": (2, 1), "school": (3, 2), "park": (2, 2),
}
SERVICE_FRONT = {}


# ---------------------------------------------------------------- LOD1
def make_lod1(b, strict_ratio, lod0_tris):
    bm = b.bm
    bmesh.ops.dissolve_limit(bm, angle_limit=math.radians(2.0), verts=bm.verts[:], edges=bm.edges[:], delimit={"MATERIAL"})
    bm.normal_update()
    t = b.tris()
    if t > lod0_tris * strict_ratio:
        # colapso de aristas: el rango de la celda UV se mantiene por cara, asi que el color no se mancha mucho
        tri = bmesh.ops.triangulate(bm, faces=bm.faces[:])
        me = bpy.data.meshes.new("tmp")
        bm.to_mesh(me)
        ob = bpy.data.objects.new("tmp", me)
        bpy.context.scene.collection.objects.link(ob)
        mod = ob.modifiers.new("d", "DECIMATE")
        mod.ratio = max(0.08, lod0_tris * strict_ratio / max(1, t))
        mod.use_collapse_triangulate = True
        dg = bpy.context.evaluated_depsgraph_get()
        ev = ob.evaluated_get(dg)
        me2 = bpy.data.meshes.new_from_object(ev)
        bm.clear()
        bm.from_mesh(me2)
        bpy.data.objects.remove(ob)
        bpy.data.meshes.remove(me)
        bpy.data.meshes.remove(me2)
        # material_index conserva el id de celda: tras el colapso se toma el de la cara original (to_mesh lo guarda)
    return b


# ---------------------------------------------------------------- catalogo
class Entry:
    pass


def mesh_stats(me):
    tris = sum(len(p.vertices) - 2 for p in me.polygons)
    vs = [v.co for v in me.vertices]
    mn = [min(v[i] for v in vs) for i in range(3)]
    mx = [max(v[i] for v in vs) for i in range(3)]
    return tris, mn, mx


def build_catalog(pack, only=None):
    """Devuelve la lista de definiciones: dict(id, zone, density, level, variant, cells, make=lambda lod -> Builder)."""
    items = []
    used_all = {}
    # residencial
    for dens in ("low", "med", "high"):
        used_fam = {}
        for level in range(1, 6):
            fx_c, fy_c, H = RES[dens][level - 1]
            for vi, var in enumerate(VARS):
                items.append(dict(zone="R", dens=dens, level=level, var=vi, vname=var, cells=(fx_c, fy_c), H=H, fam=used_fam, kind="pack"))
    for dens in ("low", "high"):
        used_fam = {}
        for level in range(1, 6):
            fx_c, fy_c, H = COM[dens][level - 1]
            for vi, var in enumerate(VARS):
                items.append(dict(zone="C", dens=dens, level=level, var=vi, vname=var, cells=(fx_c, fy_c), H=H, fam=used_fam, kind="pack"))
    for level in range(1, 6):
        fx_c, fy_c, H = OFF[level - 1]
        for vi, var in enumerate(VARS):
            items.append(dict(zone="O", dens="high", level=level, var=vi, vname=var, cells=(fx_c, fy_c), H=H, fam={}, kind="pack"))
    for level in range(1, 6):
        for vi, var in enumerate(VARS):
            cells = [(1, 1), (2, 1), (2, 2), (3, 2), (3, 3)][level - 1]
            items.append(dict(zone="I", dens="med", level=level, var=vi, vname=var, cells=cells, H=0, fam={}, kind="industry"))
    for kind, cells in SERVICE_SPEC.items():
        items.append(dict(zone="S", dens="", level=0, var=0, vname="", cells=cells, H=0, fam={}, kind="service", svc=kind))
    for it in items:
        it["id"] = f"Svc_{it['svc']}" if it["zone"] == "S" else f"Bld_{it['zone']}_{it['dens']}_L{it['level']}_{it['vname']}"
    if only:
        items = [i for i in items if i["id"].startswith(only)]
    # asignar fuentes del pack de forma voraz por familia (variedad)
    for it in items:
        if it["kind"] != "pack":
            continue
        fx, fy = it["cells"][0] * CELL_M, it["cells"][1] * CELL_M
        want_glassy = it["zone"] == "O" or (it["zone"] == "C" and it["dens"] == "high") or (it["zone"] == "R" and it["dens"] == "high" and False)
        cands = dict(pack)
        while True:
            ch = choose_source(cands, fx, fy, it["H"], it["zone"], it["fam"], used_all, want_glassy)
            if ch is None:
                raise RuntimeError("sin fuente para " + it["id"])
            cost, src, rot, sx, sy, sm = ch
            if repeat_ok(src, rot, sx, sy, sm, it["H"]):
                break
            del cands[src.name]      # esta fuente se abre al repetir plantas: se descarta para este modelo
        it.update(src=src, rot=rot, sx=sx, sy=sy, sm=sm)
        it["fam"][src.name] = it["fam"].get(src.name, 0) + 1
        used_all[src.name] = used_all.get(src.name, 0) + 1
    return items


def make_model(it, lod, opts):
    rng = random.Random(zlib.crc32(it["id"].encode()))
    cells = it["cells"]
    if it["kind"] == "pack":
        b = build_pack_building(it["src"], it["rot"], it["sx"], it["sy"], it["sm"], it["zone"], it["dens"], it["level"], it["var"], it["H"], rng,
                                it["id"], detail=(lod == 0), window_opts=opts)
        if lod == 1:
            bmesh.ops.dissolve_limit(b.bm, angle_limit=math.radians(2.0), verts=b.bm.verts[:], edges=b.bm.edges[:], delimit={"MATERIAL"})
    elif it["kind"] == "industry":
        b = build_industry(it["level"], it["var"], lod, rng)
    else:
        b = build_service(it["svc"], lod, rng)
        if lod == 1:
            bmesh.ops.dissolve_limit(b.bm, angle_limit=math.radians(2.0), verts=b.bm.verts[:], edges=b.bm.edges[:], delimit={"MATERIAL"})
    centre_on_origin(b, cells)
    return b


# ---------------------------------------------------------------- escena, exportacion y lamina
def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    a = dict(out=os.path.join(ROOT, "unity", "URBE", "Assets", "_Project", "Art", "Buildings"),
             src=os.path.join(ROOT, "unity", "URBE", "Assets", "lowpoly building.fbx"), preview=None, only=None)
    i = 0
    while i < len(argv):
        if argv[i].startswith("--") and argv[i][2:] in a and i + 1 < len(argv):
            a[argv[i][2:]] = argv[i + 1]
            i += 2
        else:
            i += 1
    a["out"] = os.path.abspath(a["out"])
    return a


def main():
    args = parse_args()
    out = args["out"]
    os.makedirs(out, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    pack = load_pack(args["src"])
    img = make_atlas_image()
    img.filepath_raw = os.path.join(out, "BuildingAtlas.png")
    img.file_format = "PNG"
    img.save()
    mat = bpy.data.materials.new("BuildingAtlas")
    mat.use_nodes = True
    nt = mat.node_tree
    tex = nt.nodes.new("ShaderNodeTexImage")
    tex.image = img
    tex.interpolation = "Closest"
    nt.links.new(tex.outputs["Color"], nt.nodes["Principled BSDF"].inputs["Base Color"])

    items = build_catalog(pack, args["only"])
    manifest = []
    built = {}
    for it in items:
        mes = {}
        for lod in (0, 1):
            cap = 12000 if it["zone"] == "S" else TRI_CAP
            tries = [dict(frames=True, spacing=2.6), dict(frames=False, spacing=2.6), dict(frames=False, spacing=3.6), dict(frames=False, spacing=5.0), dict(frames=False, spacing=7.0)]
            for opts in tries:
                b = make_model(it, lod, opts)
                if lod == 1 or b.tris() <= cap:
                    break
            if lod == 1 and it.get("lod0_tris") and b.tris() > 0.3 * it["lod0_tris"] and it["zone"] != "S":
                make_lod1(b, 0.3, it["lod0_tris"])
            me = b.finish(f"{it['id']}_LOD{lod}")
            me.materials.append(mat)
            mes[lod] = me
            if lod == 0:
                it["lod0_tris"] = sum(len(p.vertices) - 2 for p in me.polygons)
        objs = []
        for lod in (0, 1):
            ob = bpy.data.objects.new(mes[lod].name, mes[lod])
            bpy.context.scene.collection.objects.link(ob)
            objs.append(ob)
        t0, mn, mx = mesh_stats(mes[0])
        t1 = mesh_stats(mes[1])[0]
        bpy.ops.object.select_all(action="DESELECT")
        for ob in objs:
            ob.select_set(True)
        bpy.ops.export_scene.fbx(filepath=os.path.join(out, it["id"] + ".fbx"), use_selection=True, object_types={"MESH"}, axis_forward="Z", axis_up="Y",
                                 bake_space_transform=True, apply_unit_scale=True, global_scale=1.0, mesh_smooth_type="FACE", use_mesh_modifiers=False,
                                 add_leaf_bones=False, path_mode="STRIP", embed_textures=False, use_triangles=True)
        built[it["id"]] = objs
        cx, cy = it["cells"]
        manifest.append(dict(
            id=it["id"], fbx=it["id"] + ".fbx", zone=it["zone"] if it["zone"] != "S" else "SVC", density=it["dens"] or None,
            level=it["level"] or None, variant=it["vname"] or None, footprint_cells=[cx, cy],
            size_m=dict(width=round(mx[0] - mn[0], 2), depth=round(mx[1] - mn[1], 2), height=round(mx[2] - mn[2], 2)),
            height_m=round(mx[2] - mn[2], 2), tris_lod0=t0, tris_lod1=t1,
            source=("pack:" + it["src"].name) if it["kind"] == "pack" else "procedural", tint="alpha-atlas (Urbe/Vehicle)"))
        print(f"{it['id']:28s} cells={cx}x{cy} h={mx[2]-mn[2]:5.1f} bbox=({mx[0]-mn[0]:5.1f},{mx[1]-mn[1]:5.1f}) tris {t0:5d}/{t1:5d} {manifest[-1]['source']}")
        # fuera de la escena: se re-enlazan solo para la lamina
        for ob in objs:
            bpy.context.scene.collection.objects.unlink(ob)
    with open(os.path.join(out, "Buildings.manifest.json"), "w", encoding="utf-8") as fh:
        json.dump(dict(grid_cell_m=CELL_M, atlas="BuildingAtlas.png", front="+Z (Unity)", models=manifest), fh, indent=1)
    print("[URBE] Edificios exportados:", len(manifest), "a", out)
    if args["preview"]:
        render_sheet(items, built, manifest, args["preview"])


def render_sheet(items, built, manifest, path):
    scene = bpy.context.scene
    rows = [("R", "low"), ("C", "low"), ("S", ""), ("R", "med"), ("I", "med"), ("R", "high"), ("C", "high"), ("O", "high")]
    by = {m["id"]: m for m in manifest}
    ids = {}
    y = 0.0
    xmax = 0.0
    lay = []
    for z, d in rows:
        row = [i for i in items if i["zone"] == z and i["dens"] == d]
        row.sort(key=lambda i: (i["level"], i["var"]))
        if z == "S":
            row = [i for i in items if i["zone"] == "S"]
        x = 0.0
        tallest = 0.0
        for it in row:
            fx = it["cells"][0] * CELL_M
            lay.append((it["id"], x + fx / 2, y))
            x += fx + 4
            tallest = max(tallest, by[it["id"]]["height_m"])
        xmax = max(xmax, x)
        lay.append(("_h", y, tallest))
        y -= 24.0 + tallest * 0.62
    for lid, px, py in lay:
        if lid == "_h":
            continue
        for ob in built[lid]:
            if ob.name.endswith("LOD1"):
                continue
            scene.collection.objects.link(ob)
            ob.location = (xmax - px, py, 0)
    ymin = y - 20
    bpy.ops.mesh.primitive_plane_add(size=1, location=(xmax / 2, ymin / 2 - 150, -0.05))
    pl = bpy.context.object
    pl.scale = (xmax + 400, -ymin + 700, 1)
    gm = bpy.data.materials.new("Ground")
    gm.diffuse_color = (0.64, 0.74, 0.58, 1)
    pl.data.materials.append(gm)
    cam = bpy.data.cameras.new("Cam")
    cam.type = "ORTHO"
    cam.sensor_fit = "HORIZONTAL"
    cam.ortho_scale = xmax * 1.03
    co = bpy.data.objects.new("Cam", cam)
    scene.collection.objects.link(co)
    el = math.radians(46)
    d = 800
    hmax = max(m["height_m"] for m in manifest)
    depth = -ymin + 8
    cxm, cym = xmax / 2, (ymin + 14) / 2
    zc = hmax * 0.4
    co.location = (cxm, cym + d * math.cos(el), zc + d * math.sin(el))
    co.rotation_euler = (math.radians(90) - el, 0, math.radians(180))
    cam.clip_end = 3000
    scene.camera = co
    sun = bpy.data.objects.new("Sol", bpy.data.lights.new("Sol", "SUN"))
    sun.data.energy = 2.6
    sun.data.angle = math.radians(14)
    sun.rotation_euler = (math.radians(48), 0, math.radians(180 + 28))
    scene.collection.objects.link(sun)
    scene.render.engine = "BLENDER_EEVEE"
    scene.render.resolution_x = 3000
    ppm = 3000 / (xmax * 1.03)
    scene.render.resolution_y = int((depth * math.sin(el) + hmax * math.cos(el) * 0.9 + 14) * ppm)
    scene.world = bpy.data.worlds.new("W")
    scene.world.color = (0.86, 0.92, 1.0)
    scene.world.use_nodes = False
    scene.view_settings.view_transform = "Standard"
    scene.render.filepath = os.path.abspath(path)
    scene.render.image_settings.compression = 90
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    bpy.ops.render.render(write_still=True)
    print("[URBE] Lamina:", path)


if __name__ == "__main__":
    main()
