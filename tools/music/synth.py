"""Instrumentos sintetizados sin filtros IIR: tablas de onda, FM y ruido con forma espectral (FFT).

Cada instrumento devuelve un array estéreo (2, n) con la cola de liberación incluida.
"""
import numpy as np

SR = 44100
TWO_PI = 2.0 * np.pi
TABLE_SIZE = 2048
_TABLE_X = np.arange(TABLE_SIZE + 1)


def midi_to_hz(m: float) -> float:
    return 440.0 * 2.0 ** ((m - 69.0) / 12.0)


def pan_gains(pan: float):
    """Paneo de potencia constante: -1 izquierda, 0 centro, 1 derecha."""
    angle = (pan + 1.0) * np.pi / 4.0
    return np.cos(angle), np.sin(angle)


def to_stereo(mono: np.ndarray, pan: float = 0.0) -> np.ndarray:
    gl, gr = pan_gains(pan)
    return np.vstack((mono * gl, mono * gr))


def envelope(n: int, note_len: float, attack: float, decay: float, sustain: float, release: float):
    t = np.arange(n) / SR
    attack = max(attack, 1e-4)
    env = np.where(t < attack, t / attack, sustain + (1.0 - sustain) * np.exp(-(t - attack) / decay))
    t_rel = max(note_len, attack)
    at_rel = sustain + (1.0 - sustain) * np.exp(-(t_rel - attack) / decay)
    return np.where(t < t_rel, env, at_rel * np.exp(-(t - t_rel) / release))


def harmonic_table(amps) -> np.ndarray:
    """Un ciclo de onda con los armónicos dados (fase seno), normalizado y con el punto de cierre."""
    spec = np.zeros(TABLE_SIZE // 2 + 1, dtype=complex)
    k = np.arange(1, len(amps) + 1)
    spec[k] = -1j * np.asarray(amps, dtype=float)
    table = np.fft.irfft(spec, TABLE_SIZE)
    table /= np.max(np.abs(table)) + 1e-12
    return np.append(table, table[0])


def rolloff_amps(freq: float, brightness_hz: float, slope: float = 1.0, max_hz: float = 9000.0):
    """Armónicos 1/k^slope con caída exponencial: equivale a un filtro paso bajo suave, sin aliasing."""
    kmax = max(1, int(min(max_hz, 0.45 * SR) / freq))
    k = np.arange(1, kmax + 1)
    return (1.0 / k ** slope) * np.exp(-(k * freq) / brightness_hz)


def read_table(table: np.ndarray, phase_cycles: np.ndarray) -> np.ndarray:
    return np.interp((phase_cycles % 1.0) * TABLE_SIZE, _TABLE_X, table)


def shaped_noise(n: int, lo: float, hi: float, rng, order: int = 4) -> np.ndarray:
    """Ruido blanco filtrado por FFT con flancos tipo Butterworth (fase cero)."""
    x = rng.standard_normal(n)
    f = np.fft.rfftfreq(n, 1.0 / SR)
    with np.errstate(divide="ignore"):
        mask = 1.0 / (1.0 + (lo / np.maximum(f, 1e-6)) ** order) / (1.0 + (f / hi) ** order)
    y = np.fft.irfft(np.fft.rfft(x) * mask, n)
    return y / (np.max(np.abs(y)) + 1e-12)


# --- Instrumentos melódicos -------------------------------------------------------------

def pad(freq, note_len, vel, rng):
    """Pad cálido: 3 sierras desafinadas de banda limitada, con brillo que respira lentamente."""
    release = 1.8
    n = int((note_len + release * 5) * SR)
    t = np.arange(n) / SR
    env = envelope(n, note_len, 0.5, 1.0, 1.0, release)
    dark = harmonic_table(rolloff_amps(freq, 950.0))
    bright = harmonic_table(rolloff_amps(freq, 3200.0))
    lfo = 0.5 + 0.5 * np.sin(TWO_PI * (0.07 + 0.05 * rng.random()) * t + TWO_PI * rng.random())
    mix = 0.15 + 0.45 * lfo
    out = np.zeros((2, n))
    for cents, pan in ((-9.0, -0.75), (0.0, 0.0), (8.0, 0.75)):
        f = freq * 2.0 ** (cents / 1200.0)
        drift = 1.0 + 0.0012 * np.sin(TWO_PI * (0.15 + 0.2 * rng.random()) * t + TWO_PI * rng.random())
        ph = np.cumsum(f * drift) / SR + rng.random()
        y = read_table(dark, ph) * (1.0 - mix) + read_table(bright, ph) * mix
        gl, gr = pan_gains(pan)
        out[0] += y * gl
        out[1] += y * gr
    return out * (env * vel / 3.0)


def epiano(freq, note_len, vel, rng):
    """Piano eléctrico FM de 2 operadores con 'púa' metálica en el ataque."""
    release = 0.28
    decay = 1.7 * (261.6 / freq) ** 0.5
    n = int((note_len + release * 5) * SR)
    t = np.arange(n) / SR
    env = envelope(n, note_len, 0.002, decay, 0.0, release)
    index = (0.5 + 1.6 * vel) * np.exp(-t / 0.4) + 0.12
    tine = 0.6 * vel * np.exp(-t / 0.012)
    w = TWO_PI * freq * t + TWO_PI * rng.random()
    y = np.sin(w + index * np.sin(w) + tine * np.sin(14.0 * w))
    y += 0.1 * np.sin(2.0 * w) * np.exp(-t / 0.6)
    return to_stereo(y * env * vel)


def bass(freq, note_len, vel, rng):
    """Bajo redondo: seno con un poco de 2.º y 3.er armónico y saturación suave."""
    release = 0.08
    n = int((note_len + release * 6) * SR)
    t = np.arange(n) / SR
    env = envelope(n, note_len, 0.008, 0.6, 0.72, release)
    w = TWO_PI * freq * t
    y = np.sin(w) + 0.25 * np.sin(2.0 * w) + 0.07 * np.sin(3.0 * w)
    y = np.tanh(1.4 * y) / np.tanh(1.4)
    return to_stereo(y * env * vel)


def pluck(freq, note_len, vel, rng):
    """Pulsación tipo kalimba/guitarra suave: el brillo cae rápido tras el ataque."""
    release = 0.2
    n = int((note_len + 1.4) * SR)
    t = np.arange(n) / SR
    env = envelope(n, note_len, 0.003, 0.6, 0.0, release)
    bright = harmonic_table(rolloff_amps(freq, 5500.0, slope=1.5))
    dark = harmonic_table(rolloff_amps(freq, 900.0, slope=1.5))
    ph = freq * t + rng.random()
    x = np.exp(-t / 0.1)
    y = read_table(bright, ph) * x + read_table(dark, ph) * (1.0 - x)
    return to_stereo(y * env * vel)


def bell(freq, note_len, vel, rng):
    """Campana FM con brillo inarmónico breve en el ataque."""
    n = int((max(note_len, 0.3) + 2.6) * SR)
    t = np.arange(n) / SR
    env = np.minimum(t / 0.002, 1.0) * np.exp(-t / 1.0)
    w = TWO_PI * freq * t
    y = 0.8 * np.sin(w + 1.3 * np.exp(-t / 0.22) * np.sin(3.5 * w))
    y += 0.25 * np.sin(2.0 * w) * np.exp(-t / 0.5)
    return to_stereo(y * env * vel)


def soft_lead(freq, note_len, vel, rng):
    """Lead suave tipo flauta sintética con vibrato retardado."""
    release = 0.18
    n = int((note_len + release * 6) * SR)
    t = np.arange(n) / SR
    env = envelope(n, note_len, 0.05, 0.9, 0.85, release)
    vib = 1.0 + 0.004 * np.sin(TWO_PI * 5.2 * t) * np.clip((t - 0.25) / 0.3, 0.0, 1.0)
    table = harmonic_table([1.0, 0.28, 0.12, 0.05, 0.03])
    y = read_table(table, np.cumsum(freq * vib) / SR)
    return to_stereo(y * env * vel)


MELODIC = {"pad": pad, "epiano": epiano, "bass": bass, "pluck": pluck, "bell": bell, "soft_lead": soft_lead}


# --- Batería lo-fi (muestras generadas una vez por pista) -------------------------------

def drum_kit(rng) -> dict:
    def t_of(seconds):
        return np.arange(int(seconds * SR)) / SR

    t = t_of(0.6)
    f = 46.0 + 90.0 * np.exp(-t / 0.03)
    kick = np.sin(TWO_PI * np.cumsum(f) / SR) * np.exp(-t / 0.26)
    kick += 0.2 * shaped_noise(len(t), 800, 5000, rng) * np.exp(-t / 0.003)
    kick = np.tanh(1.5 * kick) / np.tanh(1.5)

    t = t_of(0.5)
    snare = 0.55 * np.sin(TWO_PI * 190.0 * t) * np.exp(-t / 0.05)
    snare += 0.25 * np.sin(TWO_PI * 330.0 * t) * np.exp(-t / 0.03)
    snare += 0.9 * shaped_noise(len(t), 1500, 8000, rng) * np.exp(-t / 0.13)

    t = t_of(0.2)
    rim = np.sin(TWO_PI * 1650.0 * t) * np.exp(-t / 0.01)
    rim += 0.5 * shaped_noise(len(t), 600, 3000, rng) * np.exp(-t / 0.008)

    t = t_of(0.15)
    hat = shaped_noise(len(t), 6500, 15000, rng) * np.exp(-t / 0.022)
    t = t_of(0.6)
    ohat = shaped_noise(len(t), 6000, 14000, rng) * np.exp(-t / 0.2)
    t = t_of(0.2)
    shaker = shaped_noise(len(t), 4000, 11000, rng) * np.minimum(t / 0.012, 1.0) * np.exp(-t / 0.05)

    kit = {"kick": kick, "snare": snare, "rim": rim, "hat": hat, "ohat": ohat, "shaker": shaker}
    return {k: v / (np.max(np.abs(v)) + 1e-12) for k, v in kit.items()}


def vinyl(n: int, rng) -> np.ndarray:
    """Crujido de vinilo: chasquidos dispersos + siseo muy bajo (estéreo)."""
    out = np.zeros((2, n))
    for ch in range(2):
        clicks = np.zeros(n)
        count = int(n / SR * 5.0)
        pos = rng.integers(0, n - 8, count)
        clicks[pos] = rng.exponential(0.35, count) * rng.choice([-1.0, 1.0], count)
        clicks = np.convolve(clicks, np.array([1.0, -0.6, 0.25, -0.1]), mode="same")
        hiss = 0.04 * shaped_noise(n, 1000, 8000, rng, order=2)
        out[ch] = clicks + hiss
    return out
