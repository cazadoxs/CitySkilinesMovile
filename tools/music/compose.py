"""Arreglo: convierte la definición de una pista en notas por instrumento.

Estructura por defecto (en compases): intro 4 · A1 8 · B1 8 · puente 4 · A2 8 · B2 (resto − 4) · final 4.
Las melodías se construyen con motivos de 2 compases (A A' B A'') para que se puedan recordar.
"""
from dataclasses import dataclass, field

import numpy as np

from theory import Chord, MELODY_POOLS, NOTE_PC, SCALES, nearest, pitches_in_range, voice


@dataclass
class Note:
    start: float  # segundos
    dur: float
    midi: int
    vel: float
    pan: float = 0.0


@dataclass
class Section:
    name: str
    bars: int
    prog: str = "A"
    energy: float = 0.9
    bass: str = "groove"   # none | calm | groove
    ep: str = "comp"       # none | calm | comp
    lead: str = "none"     # none | a | a2 | b
    counter: bool = False
    drums: str = "none"
    fill: bool = False


@dataclass
class Score:
    loop_s: float
    notes: dict = field(default_factory=dict)  # instrumento -> [Note]
    hits: list = field(default_factory=list)   # (segundos, pieza, velocidad)


DRUM_PATTERNS = {
    "soft":   {"kick": "x.........x.....", "rim": "....x.......x...", "hat": "..o...o...o...o.", "shaker": "o.o.o.o.o.o.o.o."},
    "boom":   {"kick": "X......x..x.....", "snare": "....X.......X...", "hat": "x.o.x.o.x.o.x.oo"},
    "busy":   {"kick": "X.....x...x..x..", "snare": "....X.......X..o", "hat": "xoxoxoxoxoxoxoxo", "ohat": "..............x."},
    "half":   {"kick": "X.........o.....", "snare": "........X.......", "hat": "x...o...x...o...", "shaker": "..o...o...o...o."},
    "sparse": {"kick": "x...............", "shaker": "..o...o...o...o."},
}
_VEL = {"X": 1.0, "x": 0.75, "o": 0.42}

COMP_PATTERNS = {
    "comp": [
        [(0, 1.5), (1.5, 0.5), (2.5, 1.5)],
        [(0, 0.75), (0.75, 0.75), (2, 1.0), (3.5, 0.5)],
        [(0.5, 1.0), (2.0, 1.5)],
        [(0, 2.0), (2.5, 1.5)],
    ],
    "calm": [[(0, 4.0)], [(0, 2.0), (2.0, 2.0)]],
}

BASS_PATTERNS = {
    "calm": [[(0, 3.75, "r")]],
    "groove": [
        [(0, 1.5, "r"), (1.5, 0.5, "r"), (2.5, 1.25, "5")],
        [(0, 1.0, "r"), (1.75, 0.25, "r"), (2, 1.5, "8"), (3.5, 0.5, "a")],
        [(0, 2.5, "r"), (2.75, 0.75, "5"), (3.5, 0.5, "a")],
    ],
}


def default_sections(total_bars: int, main: str, alt: str) -> list:
    rest = total_bars - 32
    b2, outro = (rest - 4, 4) if rest >= 8 else (0, rest)
    secs = [
        Section("intro", 4, "A", 0.75, bass="calm", ep="calm"),
        Section("a1", 8, "A", 0.9, lead="a", drums=main, fill=True),
        Section("b1", 8, "B", 1.0, lead="b", counter=True, drums=alt, fill=True),
        Section("puente", 4, "A", 0.72, bass="calm", ep="calm", drums="sparse"),
        Section("a2", 8, "A", 0.95, lead="a2", drums=main, fill=True),
    ]
    if b2:
        secs.append(Section("b2", b2, "B", 1.0, lead="b", counter=True, drums=alt, fill=True))
    if outro:
        secs.append(Section("final", outro, "A", 0.75, bass="calm", ep="calm", drums="sparse"))
    return secs


# --- Melodía ---------------------------------------------------------------------------

def _rhythm(rng, density: float):
    """Motivo rítmico de 2 compases en corcheas: (posición, duración) en corcheas."""
    strength = (1.0, 0.3, 0.65, 0.4, 0.85, 0.3, 0.6, 0.45)
    slots = [s for s in range(16) if rng.random() < min(0.95, strength[s % 8] * density * (0.35 if s >= 14 else 1))]
    while len(slots) < 3:
        slots = sorted(set(slots) | {int(rng.choice([0, 4, 8, 12]))})
    while len(slots) > 8:
        slots.remove(int(rng.choice([s for s in slots if s % 4])) if any(s % 4 for s in slots) else slots[-1])
    slots.sort()
    out = []
    for i, s in enumerate(slots):
        gap = (slots[i + 1] if i + 1 < len(slots) else 16) - s
        dur = gap if rng.random() < 0.7 else max(1, gap - 1)
        out.append((s, min(dur, 4)))
    return out


def _steps(rng, n: int):
    return list(rng.choice([-3, -2, -1, 0, 1, 2, 3], size=n, p=[0.04, 0.13, 0.3, 0.06, 0.3, 0.13, 0.04]))


def _phrase(rng, chords, pool, low, high, density, final_rng=None):
    """Frase de 8 compases: A (0-1) · A' (2-3) · B (4-5) · A'' (6-7), con el mismo contorno en las A."""
    ra, rb = _rhythm(rng, density), _rhythm(rng, density * 1.1)
    sa, sb = _steps(rng, len(ra)), _steps(rng, len(rb))
    plan = [(ra, sa, 0, False), (ra, sa, 2, False), (rb, sb, 4, False), (ra, sa, 6, True)]
    if final_rng is not None:
        rf = _rhythm(final_rng, density)
        plan[3] = (rf, _steps(final_rng, len(rf)), 6, True)
    out, prev = [], (low + high) / 2
    for rhythm, steps, bar0, final in plan:
        pitch = None
        for i, (slot, dur) in enumerate(rhythm):
            chord = chords[(bar0 + slot // 8) % len(chords)]
            tones = pitches_in_range(chord.pcs, low, high)
            if pitch is None:
                pitch = nearest(tones, prev)
            else:
                j = pool.index(nearest(pool, pitch)) + int(steps[i])
                pitch = pool[min(max(j, 0), len(pool) - 1)]
            if slot % 4 == 0:
                pitch = nearest(tones, pitch)
            if final and i == len(rhythm) - 1:
                pitch = nearest(pitches_in_range({chord.root_pc, (chord.root_pc + chord.intervals[1]) % 12}, low, high), pitch)
                dur = max(dur, 16 - slot - 2)
            accent = 0.16 if slot % 4 == 0 else 0.0
            out.append((bar0 * 8 + slot, dur, pitch, 0.62 + accent + rng.uniform(-0.05, 0.05)))
        prev = pitch
    return out


# --- Composición completa ---------------------------------------------------------------

def compose(spec: dict) -> Score:
    rng = np.random.default_rng(spec["seed"])
    key_pc = NOTE_PC[spec["key"]]
    mode = spec["mode"]
    beat = 60.0 / spec["bpm"]
    bar = 4 * beat
    sixteenth = beat / 4
    swing = spec.get("swing", 0.2) * sixteenth
    progs = {name: [Chord(key_pc, num, q) for num, q in chords] for name, chords in spec["progressions"].items()}
    sections = spec.get("sections") or default_sections(spec["bars"], spec["drums"], spec.get("drums_b", spec["drums"]))
    total_bars = sum(s.bars for s in sections)
    score = Score(loop_s=total_bars * bar)
    for name in ("pad", "bass", "ep", "lead", "counter"):
        score.notes[name] = []

    low, high = spec.get("lead_range", (67, 84))
    pool_pcs = {(key_pc + p) % 12 for p in MELODY_POOLS[mode]}
    pool = pitches_in_range(pool_pcs, low, high)
    density = spec.get("density", 0.55)
    phrases = {
        "a": _phrase(np.random.default_rng([spec["seed"], 1]), progs["A"] * 2, pool, low, high, density),
        "a2": _phrase(np.random.default_rng([spec["seed"], 1]), progs["A"] * 2, pool, low, high, density,
                      final_rng=np.random.default_rng([spec["seed"], 2])),
        "b": _phrase(np.random.default_rng([spec["seed"], 3]), progs["B"] * 2, pool, low, high, density * 1.1),
    }
    comp_choice = {k: [v[i] for i in rng.choice(len(v), 2)] for k, v in COMP_PATTERNS.items()}
    bass_choice = {k: [v[i] for i in rng.choice(len(v), 2)] for k, v in BASS_PATTERNS.items()}

    def jitter(ms):
        return rng.uniform(-ms, ms) / 1000.0

    t0, prev_pad, prev_ep = 0.0, None, None
    for sec in sections:
        prog = progs[sec.prog]
        e = sec.energy
        for b in range(sec.bars):
            bar_t = t0 + b * bar
            chord = prog[b % len(prog)]
            next_chord = prog[(b + 1) % len(prog)]

            prev_pad = voice(chord.tones(drop_root=True), 52, 72, prev_pad)
            for m in prev_pad:
                score.notes["pad"].append(Note(bar_t, bar * 0.98, m, 0.55 * e))

            if sec.ep != "none":
                prev_ep = voice(chord.tones(drop_root=True), 55, 74, prev_ep)
                for pos, dur in comp_choice[sec.ep][b % 2]:
                    start = bar_t + pos * beat + max(0.0, jitter(18))
                    vel = (0.5 + rng.uniform(-0.06, 0.06)) * e * (1.0 if pos % 1 == 0 else 0.85)
                    for i, m in enumerate(prev_ep):
                        score.notes["ep"].append(Note(start + i * 0.009, dur * beat * 0.95, m, vel))

            if sec.bass != "none":
                root = 36 + (chord.root_pc - 36) % 12
                for pos, dur, kind in bass_choice[sec.bass][b % 2]:
                    if kind == "r":
                        m = root
                    elif kind == "5":
                        m = root + 7 if root + 7 <= 50 else root - 5
                    elif kind == "8":
                        m = root + 12
                    else:  # aproximación cromática a la siguiente fundamental
                        m = 36 + (next_chord.root_pc - 36) % 12 - 1
                    score.notes["bass"].append(Note(bar_t + pos * beat + jitter(6), dur * beat * 0.92, m,
                                                    (0.8 + rng.uniform(-0.05, 0.05)) * e))

            if sec.counter and b % 2 == 1:
                tones = pitches_in_range(chord.pcs, 76, 88)
                for k, pos in enumerate((1.5, 2.5, 3.5)):
                    m = tones[(k * 2 + b) % len(tones)]
                    score.notes["counter"].append(Note(bar_t + pos * beat + jitter(8), beat * 0.9, m, 0.38 * e, 0.4))

            if sec.drums != "none":
                pattern = DRUM_PATTERNS[sec.drums]
                last = sec.fill and b == sec.bars - 1
                for piece, steps in pattern.items():
                    for s, ch in enumerate(steps):
                        if ch == "." or (last and piece == "hat" and s >= 12):
                            continue
                        start = bar_t + s * sixteenth + (swing if s % 2 else 0.0) + jitter(4)
                        score.hits.append((start, piece, _VEL[ch] * e * rng.uniform(0.9, 1.05)))
                if last:
                    for s, v in ((13, 0.3), (14, 0.42), (15, 0.55)):
                        score.hits.append((bar_t + s * sixteenth + (swing if s % 2 else 0.0), "snare", v * e))
                    score.hits.append((bar_t + 14 * sixteenth, "ohat", 0.5 * e))

        if sec.lead != "none":
            for slot, dur, m, vel in phrases[sec.lead]:
                if slot // 8 >= sec.bars:
                    break
                start = t0 + slot * (beat / 2) + (0.012 if slot % 2 else 0.0) + jitter(8)
                score.notes["lead"].append(Note(start, dur * beat / 2 * 0.92, m, vel * sec.energy, 0.1))
            if sec.bars > 8:  # secciones largas: se repite la primera mitad de la frase
                for slot, dur, m, vel in phrases[sec.lead]:
                    if 8 + slot // 8 >= sec.bars:
                        break
                    start = t0 + 8 * bar + slot * (beat / 2) + (0.012 if slot % 2 else 0.0) + jitter(8)
                    score.notes["lead"].append(Note(start, dur * beat / 2 * 0.92, m, vel * sec.energy * 0.95, 0.1))
        t0 += sec.bars * bar
    return score


def compose_jingle(spec: dict) -> Score:
    """Jingle corto: acorde de pad + EP y arpegio de campana ascendente."""
    key_pc = NOTE_PC[spec["key"]]
    beat = 60.0 / spec["bpm"]
    chord = Chord(key_pc, *spec["chord"])
    score = Score(loop_s=spec["length_s"])
    score.notes = {"pad": [], "ep": [], "lead": [], "bass": [], "counter": []}
    for m in voice(chord.tones(drop_root=True), 55, 74):
        score.notes["pad"].append(Note(0.0, spec["length_s"] * 0.6, m, 0.6))
    for i, m in enumerate(spec["arpeggio"]):
        start = i * beat * spec.get("step_beats", 0.25)
        score.notes["lead"].append(Note(start, beat * 0.5, m, 0.7 + 0.05 * i, 0.15 * (-1) ** i))
    last = len(spec["arpeggio"]) * beat * spec.get("step_beats", 0.25)
    for i, m in enumerate(voice(chord.tones(drop_root=True), 57, 76)):
        score.notes["ep"].append(Note(last + i * 0.012, beat * 2, m, 0.55))
    score.notes["bass"].append(Note(last, beat * 2, 36 + (chord.root_pc - 36) % 12, 0.7))
    score.hits.append((last, "kick", 0.6))
    score.hits.append((last, "ohat", 0.35))
    return score
