"""Teoría musical mínima para el generador de la banda sonora de URBE.

Notas en MIDI (60 = Do central). Los acordes se escriben con números romanos
relativos a la tónica de la pista y una calidad explícita, p. ej. ("ii", "m9").
"""
import numpy as np

NOTE_PC = {"C": 0, "C#": 1, "Db": 1, "D": 2, "D#": 3, "Eb": 3, "E": 4, "F": 5,
           "F#": 6, "Gb": 6, "G": 7, "G#": 8, "Ab": 8, "A": 9, "A#": 10, "Bb": 10, "B": 11}

SCALES = {
    "major": (0, 2, 4, 5, 7, 9, 11),
    "minor": (0, 2, 3, 5, 7, 8, 10),
    "dorian": (0, 2, 3, 5, 7, 9, 10),
}

# Escalas pentatónicas (más un color) para las melodías: evitan choques con los acordes extendidos.
MELODY_POOLS = {
    "major": (0, 2, 4, 7, 9),
    "minor": (0, 3, 5, 7, 10),
    "dorian": (0, 2, 3, 7, 9, 10),
}

CHORDS = {
    "maj7": (0, 4, 7, 11),
    "maj9": (0, 4, 7, 11, 14),
    "add9": (0, 4, 7, 14),
    "6/9": (0, 4, 9, 14),
    "m7": (0, 3, 7, 10),
    "m9": (0, 3, 7, 10, 14),
    "m11": (0, 3, 7, 10, 14, 17),
    "7": (0, 4, 7, 10),
    "9": (0, 4, 7, 10, 14),
    "13": (0, 4, 10, 14, 21),
    "9sus4": (0, 5, 7, 10, 14),
}

_DEGREE = {"I": 0, "II": 2, "III": 4, "IV": 5, "V": 7, "VI": 9, "VII": 11}


def roman_offset(numeral: str) -> int:
    """Semitonos desde la tónica para un número romano con alteraciones ("bVII" -> 10)."""
    acc = 0
    while numeral[0] in "b#":
        acc += -1 if numeral[0] == "b" else 1
        numeral = numeral[1:]
    return _DEGREE[numeral.upper()] + acc


class Chord:
    def __init__(self, key_pc: int, numeral: str, quality: str):
        self.root_pc = (key_pc + roman_offset(numeral)) % 12
        self.intervals = CHORDS[quality]
        self.pcs = sorted({(self.root_pc + i) % 12 for i in self.intervals})

    def tones(self, drop_root: bool = False):
        ivs = self.intervals[1:] if drop_root and len(self.intervals) >= 4 else self.intervals
        out = []
        for i in ivs:
            pc = (self.root_pc + i) % 12
            if pc not in out:
                out.append(pc)
        return out


def voice(pcs, low: int, high: int, prev=None):
    """Coloca las clases de nota dentro de [low, high] eligiendo la inversión que menos se mueve."""
    candidates = []
    for rot in range(len(pcs)):
        order = pcs[rot:] + pcs[:rot]
        for base in (low, low + 6):
            notes, cur = [], base - 1
            for pc in order:
                cur = cur + 1 + ((pc - (cur + 1)) % 12)
                notes.append(cur)
            if notes[-1] <= high:
                candidates.append(notes)
    if not candidates:
        notes, cur = [], low - 1
        for pc in pcs:
            cur = cur + 1 + ((pc - (cur + 1)) % 12)
            notes.append(cur if cur <= high else cur - 12)
        candidates = [sorted(notes)]
    if prev is None:
        mid = (low + high) / 2
        return min(candidates, key=lambda c: abs(np.mean(c) - mid))
    return min(candidates, key=lambda c: sum(min(abs(n - p) for p in prev) for n in c))


def pitches_in_range(pcs, low: int, high: int):
    return [m for m in range(low, high + 1) if m % 12 in pcs]


def nearest(candidates, target: float) -> int:
    return min(candidates, key=lambda m: (abs(m - target), m))
