"""Definición de las pistas de la banda sonora de URBE (v1).

Cambiar una pista = cambiar sus datos aquí (tempo, acordes, instrumentos, semilla) y volver a renderizar.
Acordes: (número romano relativo a la tónica, calidad de theory.CHORDS).
"""

TRACKS = [
    {
        "id": "01", "slug": "plano_inicial", "title": "Plano Inicial", "use": "Menú principal",
        "key": "D", "mode": "major", "bpm": 76, "bars": 40, "seed": 1101,
        "progressions": {
            "A": [("I", "maj9"), ("vi", "m9"), ("IV", "maj7"), ("V", "9sus4")],
            "B": [("ii", "m9"), ("V", "13"), ("I", "maj9"), ("vi", "m7")],
        },
        "lead": "bell", "lead_delay_beats": 0.75, "density": 0.45,
        "drums": "soft", "drums_b": "soft", "swing": 0.22,
    },
    {
        "id": "02", "slug": "primeras_calles", "title": "Primeras Calles", "use": "Mañana / inicio de partida",
        "key": "G", "mode": "major", "bpm": 84, "bars": 44, "seed": 2202,
        "progressions": {
            "A": [("IV", "maj7"), ("V", "9"), ("iii", "m7"), ("vi", "m9")],
            "B": [("ii", "m9"), ("V", "9sus4"), ("I", "add9"), ("I", "maj7")],
        },
        "lead": "pluck", "density": 0.55,
        "drums": "boom", "drums_b": "boom", "swing": 0.2,
    },
    {
        "id": "03", "slug": "hora_punta", "title": "Hora Punta", "use": "Día con mucha actividad",
        "key": "A", "mode": "minor", "bpm": 92, "bars": 48, "seed": 3303,
        "progressions": {
            "A": [("i", "m9"), ("bVI", "maj7"), ("bVII", "9"), ("v", "m7")],
            "B": [("iv", "m9"), ("bVII", "13"), ("bIII", "maj7"), ("bVI", "maj9")],
        },
        "lead": "pluck", "density": 0.62,
        "drums": "boom", "drums_b": "busy", "swing": 0.16,
    },
    {
        "id": "04", "slug": "barrio_al_sol", "title": "Barrio al Sol", "use": "Tarde",
        "key": "F", "mode": "major", "bpm": 80, "bars": 40, "seed": 4404,
        "progressions": {
            "A": [("IV", "maj7"), ("iii", "m7"), ("ii", "m9"), ("I", "maj9")],
            "B": [("vi", "m9"), ("ii", "m7"), ("V", "13"), ("I", "6/9")],
        },
        "lead": "soft_lead", "density": 0.5,
        "drums": "boom", "drums_b": "boom", "swing": 0.26, "vinyl": 1.6,
    },
    {
        "id": "05", "slug": "luces_de_neon", "title": "Luces de Neón", "use": "Noche",
        "key": "E", "mode": "dorian", "bpm": 70, "bars": 40, "seed": 5505,
        "progressions": {
            "A": [("i", "m9"), ("IV", "9"), ("i", "m11"), ("IV", "13")],
            "B": [("bIII", "maj9"), ("bVII", "maj7"), ("ii", "m7"), ("IV", "9")],
        },
        "lead": "bell", "lead_delay_beats": 0.75, "density": 0.4, "lead_range": (64, 81),
        "drums": "half", "drums_b": "half", "swing": 0.2,
    },
    {
        "id": "06", "slug": "skyline", "title": "Skyline", "use": "Ciudad grande / hitos altos",
        "key": "Bb", "mode": "major", "bpm": 88, "bars": 44, "seed": 6606,
        "progressions": {
            "A": [("I", "maj9"), ("iii", "m7"), ("vi", "m9"), ("IV", "maj9")],
            "B": [("ii", "m9"), ("iii", "m7"), ("IV", "maj7"), ("V", "9sus4")],
        },
        "lead": "pluck", "lead_double": "soft_lead", "density": 0.58,
        "drums": "boom", "drums_b": "busy", "swing": 0.18,
    },
]

JINGLES = [
    {
        "id": "J1", "slug": "hito_alcanzado", "title": "Hito alcanzado", "use": "Jingle al desbloquear un hito",
        "key": "D", "bpm": 120, "length_s": 5.0, "seed": 9101, "chord": ("I", "maj9"),
        "arpeggio": [74, 78, 81, 85, 88, 90], "step_beats": 0.25, "lead": "bell",
    },
    {
        "id": "J2", "slug": "logro", "title": "Logro", "use": "Jingle de logro",
        "key": "G", "bpm": 132, "length_s": 3.2, "seed": 9202, "chord": ("I", "add9"),
        "arpeggio": [79, 83, 86], "step_beats": 0.5, "lead": "bell",
    },
]

# Ganancia lineal por instrumento y envío a reverb (se pueden sobrescribir por pista con "levels").
LEVELS = {
    "pad": (0.18, 0.45), "bass": (0.28, 0.0), "ep": (0.21, 0.30),
    "lead": (0.38, 0.35), "counter": (0.17, 0.50),
    "kick": (0.48, 0.05), "snare": (0.34, 0.22), "rim": (0.26, 0.2), "hat": (0.30, 0.08),
    "ohat": (0.20, 0.12), "shaker": (0.14, 0.1), "vinyl": (0.022, 0.0),
}

DRUM_PAN = {"kick": 0.0, "snare": 0.05, "rim": 0.12, "hat": 0.25, "ohat": 0.25, "shaker": -0.35}

# Capas adaptativas: qué instrumentos van en cada capa (GDD §14.1).
LAYERS = {
    "base": ("pad", "bass", "vinyl"),
    "melodia": ("ep", "lead", "counter"),
    "ritmo": ("kick", "snare", "rim", "hat", "ohat", "shaker"),
}
