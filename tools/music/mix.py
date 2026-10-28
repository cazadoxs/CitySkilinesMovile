"""Efectos y mezcla: reverb por convolución FFT, delay, wow de cinta, EQ, normalización y limitador."""
import numpy as np

from synth import SR, TWO_PI, shaped_noise


def place(buf: np.ndarray, sig: np.ndarray, start: int, gain: float = 1.0):
    """Suma una señal estéreo en el búfer a partir de la muestra `start` (se recorta al final)."""
    if start >= buf.shape[1]:
        return
    if start < 0:  # una nota adelantada por la humanización empieza en 0
        sig, start = sig[:, -start:], 0
    end = min(buf.shape[1], start + sig.shape[1])
    buf[:, start:end] += sig[:, : end - start] * gain


def fft_convolve(x: np.ndarray, h: np.ndarray) -> np.ndarray:
    n = len(x) + len(h) - 1
    nfft = 1 << (n - 1).bit_length()
    y = np.fft.irfft(np.fft.rfft(x, nfft) * np.fft.rfft(h, nfft), nfft)
    return y[: len(x)]


def make_ir(rng, seconds: float = 3.0, rt_low: float = 2.3, rt_high: float = 0.9, predelay: float = 0.02):
    """Respuesta al impulso sintética estéreo: graves que duran más que los agudos + primeras reflexiones."""
    n = int(seconds * SR)
    t = np.arange(n) / SR
    ir = np.zeros((2, n))
    for ch in range(2):
        low = shaped_noise(n, 40, 2500, rng, order=2) * np.exp(-6.9 * t / rt_low)
        high = shaped_noise(n, 2500, 12000, rng, order=2) * np.exp(-6.9 * t / rt_high)
        tail = 0.8 * low + 0.35 * high
        early = np.zeros(n)
        for _ in range(6):
            early[int(rng.uniform(0.005, 0.06) * SR)] += rng.uniform(0.2, 0.5) * rng.choice([-1, 1])
        ir[ch] = early + tail * np.minimum(t / 0.03, 1.0)
    d = int(predelay * SR)
    ir = np.pad(ir, ((0, 0), (d, 0)))[:, :n]
    return ir / np.sqrt(np.sum(ir ** 2) / 2.0)


def reverb(send: np.ndarray, ir: np.ndarray) -> np.ndarray:
    return np.vstack([fft_convolve(send[ch], ir[ch]) for ch in range(2)])


def fft_eq(x: np.ndarray, lo: float, hi: float, order: int = 2, bells=()) -> np.ndarray:
    """Paso alto + paso bajo de fase cero aplicados por FFT a una señal estéreo larga.

    `bells`: campanas de ecualización (frecuencia central Hz, ganancia dB, ancho en octavas).
    """
    n = x.shape[1]
    f = np.fft.rfftfreq(n, 1.0 / SR)
    with np.errstate(divide="ignore"):
        mask = 1.0 / (1.0 + (lo / np.maximum(f, 1e-6)) ** order) / (1.0 + (f / hi) ** order)
        octaves = np.log2(np.maximum(f, 1.0))
    for fc, gain_db, width in bells:
        mask = mask * 10 ** (gain_db / 20 * np.exp(-0.5 * ((octaves - np.log2(fc)) / (width / 2)) ** 2))
    return np.vstack([np.fft.irfft(np.fft.rfft(x[ch]) * mask, n) for ch in range(2)])


def ping_pong(x: np.ndarray, delay_s: float, feedback: float = 0.35, repeats: int = 6, mix: float = 0.25):
    """Delay estéreo alternando canales; las repeticiones se oscurecen con un paso bajo."""
    mono = x.mean(axis=0)
    d = int(delay_s * SR)
    wet = np.zeros_like(x)
    for i in range(1, repeats + 1):
        shift = i * d
        if shift >= x.shape[1]:
            break
        wet[i % 2, shift:] += (feedback ** i) * mono[:-shift]
    return x + mix * fft_eq(wet, 150, 3500)


def periodic(freq: float, length_s: float) -> float:
    """Ajusta una frecuencia de LFO para que complete ciclos enteros en el bucle (sin saltos al repetir)."""
    return max(1, round(freq * length_s)) / length_s


def wow_flutter(x: np.ndarray, loop_s: float, depth_s: float = 0.0006, flutter_s: float = 0.00008):
    """Pequeña inestabilidad de cinta (retardo variable), periódica respecto al bucle."""
    n = x.shape[1]
    t = np.arange(n) / SR
    delay = depth_s * (1 + np.sin(TWO_PI * periodic(0.33, loop_s) * t)) \
        + flutter_s * (1 + np.sin(TWO_PI * periodic(5.8, loop_s) * t))
    src = np.clip(t - delay, 0, None) * SR
    idx = np.arange(n)
    return np.vstack([np.interp(src, idx, x[ch]) for ch in range(2)])


def autopan(x: np.ndarray, loop_s: float, rate: float = 3.2, depth: float = 0.15):
    t = np.arange(x.shape[1]) / SR
    m = depth * np.sin(TWO_PI * periodic(rate, loop_s) * t)
    return np.vstack((x[0] * (1 + m), x[1] * (1 - m)))


def loop_wrap(x: np.ndarray, loop_len: int) -> np.ndarray:
    """Suma la cola posterior al final del bucle sobre su principio: la pista se repite sin cortes."""
    out = x[:, :loop_len].copy()
    tail = x[:, loop_len:]
    k = min(tail.shape[1], loop_len)
    out[:, :k] += tail[:, :k]
    return out


def rms_db(x: np.ndarray) -> float:
    return 20 * np.log10(np.sqrt(np.mean(x ** 2)) + 1e-12)


def peak_db(x: np.ndarray) -> float:
    return 20 * np.log10(np.max(np.abs(x)) + 1e-12)


def soft_limit(x: np.ndarray, ceiling_db: float = -1.0, window_s: float = 0.005) -> np.ndarray:
    """Limitador simple: ganancia por bloques según el pico, suavizada para no generar chasquidos."""
    ceiling = 10 ** (ceiling_db / 20)
    n = x.shape[1]
    w = int(window_s * SR)
    blocks = -(-n // w)
    padded = np.pad(np.max(np.abs(x), axis=0), (0, blocks * w - n))
    peaks = padded.reshape(blocks, w).max(axis=1)
    peaks = np.maximum(peaks, np.concatenate(([0.0], peaks[:-1])))  # mira un bloque atrás
    peaks = np.maximum(peaks, np.concatenate((peaks[1:], [0.0])))  # y uno adelante
    gain = np.minimum(1.0, ceiling / np.maximum(peaks, 1e-9))
    kernel = np.hanning(9)
    kernel /= kernel.sum()
    gain = np.minimum(gain, np.convolve(gain, kernel, mode="same"))
    g = np.interp(np.arange(n), np.arange(blocks) * w + w / 2, gain)
    y = x * g
    return y * min(1.0, ceiling / (np.max(np.abs(y)) + 1e-12))
