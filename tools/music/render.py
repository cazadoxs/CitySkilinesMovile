"""Renderiza la banda sonora original de URBE (GDD §14.1).

Uso (desde la raíz del repo):
  python3 tools/music/render.py --all                  # mezclas OGG en audio/music/ + tracks.json
  python3 tools/music/render.py --track 03             # una pista o jingle por id
  python3 tools/music/render.py --all --stems          # además, capas base / melodia / ritmo
  python3 tools/music/render.py --all --mp3 DIR        # copias MP3 para escuchar fuera del juego
  python3 tools/music/render.py --check                # verifica los OGG ya renderizados
  python3 tools/music/render.py --check --spectrogram DIR
"""
import argparse
import hashlib
import json
import subprocess
import sys
import tempfile
import time
import wave
from pathlib import Path

import numpy as np

from compose import compose, compose_jingle
from mix import (autopan, fft_eq, loop_wrap, make_ir, peak_db, ping_pong, place, reverb, rms_db,
                 soft_limit, wow_flutter)
from synth import MELODIC, SR, drum_kit, midi_to_hz, pan_gains, vinyl
from tracks import DRUM_PAN, JINGLES, LAYERS, LEVELS, TRACKS

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "audio" / "music"
TAIL_S = 6.0
TARGET_RMS_DB = -18.0
CEILING_DB = -1.0
# Ecualización final: menos barro en graves-medios y más presencia para que la melodía se entienda.
MASTER_EQ = ((300.0, -2.5, 1.2), (3000.0, 3.5, 1.4))


def repan(sig: np.ndarray, pan: float) -> np.ndarray:
    if not pan:
        return sig
    gl, gr = pan_gains(pan)
    c = np.cos(np.pi / 4)
    return np.vstack((sig[0] * gl / c, sig[1] * gr / c))


def render_layers(spec: dict, score, jingle: bool = False) -> dict:
    rng = np.random.default_rng([spec["seed"], 7])
    levels = {**LEVELS, **spec.get("levels", {})}
    layer_of = {inst: layer for layer, insts in LAYERS.items() for inst in insts}
    loop_n = int(round(score.loop_s * SR))
    n = loop_n + int(TAIL_S * SR)
    dry = {name: np.zeros((2, n)) for name in LAYERS}
    send = {name: np.zeros((2, n)) for name in LAYERS}
    voices = {"pad": ["pad"], "bass": ["bass"], "ep": ["epiano"], "counter": ["bell"],
              "lead": [spec["lead"]] + ([spec["lead_double"]] if spec.get("lead_double") else [])}

    for part, notes in score.notes.items():
        if not notes:
            continue
        gain, rev = levels[part]
        buf = np.zeros((2, n))
        for v_i, inst in enumerate(voices[part]):
            fn, v_gain = MELODIC[inst], (1.0 if v_i == 0 else 0.45)
            for note in notes:
                sig = fn(midi_to_hz(note.midi), note.dur, note.vel, rng)
                place(buf, repan(sig, note.pan - 0.2 * v_i), int(note.start * SR), v_gain)
        if part == "lead" and spec.get("lead_delay_beats"):
            buf = ping_pong(buf, spec["lead_delay_beats"] * 60.0 / spec["bpm"])
        if part == "ep" and not jingle:
            buf = autopan(buf, score.loop_s)
        layer = layer_of[part]
        dry[layer] += buf * gain
        send[layer] += buf * gain * rev

    kit = drum_kit(rng)
    for t, piece, vel in score.hits:
        gain, rev = levels[piece]
        mono = kit[piece] * vel
        gl, gr = pan_gains(DRUM_PAN[piece])
        sig = np.vstack((mono * gl, mono * gr))
        start = max(0, int(t * SR))
        place(dry["ritmo"], sig, start, gain)
        place(send["ritmo"], sig, start, gain * rev)

    if not jingle:
        dry["base"] += vinyl(n, rng) * levels["vinyl"][0] * spec.get("vinyl", 1.0)

    ir = make_ir(np.random.default_rng([spec["seed"], 9]))
    layers = {}
    for name in LAYERS:
        x = dry[name] + reverb(send[name], ir)
        if not jingle:
            x = wow_flutter(x, score.loop_s)
        x = fft_eq(x, 40.0, 16000.0, bells=MASTER_EQ)
        layers[name] = loop_wrap(x, loop_n) if not jingle else x[:, : int((score.loop_s + 1.8) * SR)]
    return layers


def finalize(layers: dict, jingle: bool = False):
    """Normaliza sonoridad, satura suavemente y limita la mezcla; las capas reciben la misma ganancia."""
    mix = sum(layers.values())
    g = 10 ** ((TARGET_RMS_DB - rms_db(mix)) / 20)
    layers = {k: v * g for k, v in layers.items()}
    mix = np.tanh(1.1 * mix * g) / 1.1
    if jingle:
        fade = np.minimum(1.0, np.linspace(6.0, 0.0, mix.shape[1]))
        mix *= fade
        layers = {k: v * fade for k, v in layers.items()}
    mix = soft_limit(mix, CEILING_DB)
    stem_peak = max(np.max(np.abs(v)) for v in layers.values())
    ceiling = 10 ** (CEILING_DB / 20)
    if stem_peak > ceiling:
        layers = {k: v * ceiling / stem_peak for k, v in layers.items()}
    return mix, layers


def write_wav(path: Path, x: np.ndarray) -> str:
    pcm = (np.clip(x, -1.0, 1.0) * 32767.0).round().astype("<i2").T.tobytes()
    with wave.open(str(path), "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm)
    return hashlib.sha256(pcm).hexdigest()


def encode(wav: Path, out: Path, title: str, quality: str = "4", mp3: bool = False):
    codec = ["-c:a", "libmp3lame", "-b:a", "160k"] if mp3 else ["-c:a", "libvorbis", "-q:a", quality]
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(wav), *codec,
                    "-metadata", f"title={title}", "-metadata", "artist=URBE OST",
                    "-metadata", "album=URBE — Banda sonora original v1", str(out)], check=True)


def render_one(spec: dict, stems: bool, mp3_dir):
    jingle = spec["id"].startswith("J")
    t0 = time.time()
    score = compose_jingle(spec) if jingle else compose(spec)
    mix, layers = finalize(render_layers(spec, score, jingle), jingle)
    name = f"{spec['id']}_{spec['slug']}"
    OUT.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        wav = Path(tmp) / f"{name}.wav"
        digest = write_wav(wav, mix)
        encode(wav, OUT / f"{name}.ogg", spec["title"])
        if mp3_dir:
            Path(mp3_dir).mkdir(parents=True, exist_ok=True)
            encode(wav, Path(mp3_dir) / f"{name}.mp3", spec["title"], mp3=True)
        if stems:
            (OUT / "stems").mkdir(exist_ok=True)
            for layer, x in layers.items():
                swav = Path(tmp) / f"{name}_{layer}.wav"
                write_wav(swav, x)
                encode(swav, OUT / "stems" / f"{name}_{layer}.ogg", f"{spec['title']} ({layer})", quality="3")
    entry = {
        "id": spec["id"], "file": f"{name}.ogg", "title": spec["title"], "use": spec["use"],
        "bpm": spec["bpm"], "key": f"{spec['key']} {spec.get('mode', 'major')}", "seed": spec["seed"],
        "duration_s": round(mix.shape[1] / SR, 2), "loop": not jingle,
        "layers": list(LAYERS) if not jingle else [], "pcm_sha256": digest,
    }
    print(f"{name}: {entry['duration_s']} s · RMS {rms_db(mix):.1f} dBFS · pico {peak_db(mix):.1f} dBFS"
          f" · {time.time() - t0:.0f} s de render")
    return entry


def decode(path: Path) -> np.ndarray:
    raw = subprocess.run(["ffmpeg", "-loglevel", "error", "-i", str(path), "-f", "s16le", "-ac", "2",
                          "-ar", str(SR), "-"], check=True, capture_output=True).stdout
    return np.frombuffer(raw, dtype="<i2").reshape(-1, 2).T.astype(float) / 32768.0


def check(spectrogram_dir=None) -> bool:
    manifest = json.loads((OUT / "tracks.json").read_text(encoding="utf-8"))
    ok, rms_values = True, []
    print(f"{'archivo':32} {'dur':>7} {'pico':>6} {'RMS':>6} {'DC':>8} {'clic':>5} {'Δunión':>7}")
    for e in manifest["tracks"]:
        x = decode(OUT / e["file"])
        dur, pk, rms = x.shape[1] / SR, peak_db(x), rms_db(x)
        dc = float(np.max(np.abs(x.mean(axis=1))))
        w = int(0.25 * SR)
        if e["loop"]:
            jump = np.max(np.abs(x[:, 0] - x[:, -1]))
            click = jump / (np.percentile(np.abs(np.diff(x, axis=1)), 99) + 1e-9)
            seam = abs(rms_db(x[:, -w:]) - rms_db(x[:, :w]))
            bar_n = int(240.0 / e["bpm"] * SR)
            bar_jumps = [abs(rms_db(x[:, b - w:b]) - rms_db(x[:, b:b + w])) for b in range(bar_n, x.shape[1] - w, bar_n)]
            seam_limit = max(np.percentile(bar_jumps, 90), 3.0) + 1.0
            rms_values.append(rms)
        else:
            click, seam, seam_limit = 0.0, 0.0, 0.0
        problems = []
        if pk > CEILING_DB + 0.3:
            problems.append("pico")
        if dc > 0.002:
            problems.append("DC")
        if click > 1.0:
            problems.append("clic en la unión")
        if e["loop"] and seam > seam_limit:
            problems.append("salto de volumen en la unión")
        if e["loop"] and not 100 <= dur <= 150:
            problems.append("duración")
        ok &= not problems
        print(f"{e['file']:32} {dur:6.1f}s {pk:5.1f} {rms:6.1f} {dc:8.5f} {click:5.2f} {seam:6.1f}dB"
              f" {'OK' if not problems else 'FALLO: ' + ', '.join(problems)}")
        if spectrogram_dir:
            Path(spectrogram_dir).mkdir(parents=True, exist_ok=True)
            subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(OUT / e["file"]), "-lavfi",
                            "showspectrumpic=s=1200x400:legend=1:scale=log:fscale=log",
                            str(Path(spectrogram_dir) / f"{Path(e['file']).stem}.png")], check=True)
    spread = max(rms_values) - min(rms_values)
    print(f"Diferencia de sonoridad entre pistas: {spread:.2f} dB {'OK' if spread <= 1.5 else 'FALLO'}")
    size = sum((OUT / e["file"]).stat().st_size for e in manifest["tracks"]) / 1e6
    print(f"Tamaño total de las mezclas: {size:.1f} MB")
    return ok and spread <= 1.5


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--all", action="store_true")
    ap.add_argument("--track", action="append", default=[])
    ap.add_argument("--stems", action="store_true")
    ap.add_argument("--mp3", metavar="DIR")
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--spectrogram", metavar="DIR")
    args = ap.parse_args()

    specs = TRACKS + JINGLES
    selected = specs if args.all else [s for s in specs if s["id"] in args.track]
    if selected:
        manifest_path = OUT / "tracks.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.exists() else {"tracks": []}
        entries = {e["id"]: e for e in manifest["tracks"]}
        order = [s["id"] for s in specs]
        for spec in selected:
            entries[spec["id"]] = render_one(spec, args.stems, args.mp3)
            manifest = {"version": 1, "sample_rate": SR, "layers": {k: list(v) for k, v in LAYERS.items()},
                        "tracks": [entries[i] for i in order if i in entries]}
            manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    if args.check:
        sys.exit(0 if check(args.spectrogram) else 1)
    if not selected and not args.check:
        ap.print_help()


if __name__ == "__main__":
    main()
