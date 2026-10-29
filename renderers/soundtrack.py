"""Original launch-film soundtrack, synthesized locally (no samples, no licences needed).

120 BPM, 4/4, so every bar is exactly 2 seconds and cuts can land on the beat. Sections are given in bars:
  python soundtrack.py out.wav --bars 16 --drops 1,7 --breaks 6 --ending 14 --key D
"""
import argparse
import numpy as np
from scipy.signal import butter, sosfilt, fftconvolve

SR = 44100
BPM = 120
BEAT = 60 / BPM
BAR = BEAT * 4
NOTES = {'C': 0, 'C#': 1, 'D': 2, 'D#': 3, 'E': 4, 'F': 5, 'F#': 6, 'G': 7, 'G#': 8, 'A': 9, 'A#': 10, 'B': 11}

def hz(midi):
    return 440.0 * 2 ** ((midi - 69) / 12)

def env(n, attack, decay_time, curve=6.0):
    t = np.arange(n) / SR
    a = np.clip(t / max(attack, 1e-4), 0, 1)
    return a * np.exp(-curve * t / max(decay_time, 1e-4))

def saw(freq, n, phase=0.0):
    t = np.arange(n) / SR
    return 2 * ((freq * t + phase) % 1.0) - 1

def lowpass(x, cutoff, order=2):
    return sosfilt(butter(order, min(cutoff, SR / 2 - 100), 'low', fs=SR, output='sos'), x)

def highpass(x, cutoff, order=2):
    return sosfilt(butter(order, cutoff, 'high', fs=SR, output='sos'), x)

def bandpass(x, low, high):
    return sosfilt(butter(2, [low, high], 'band', fs=SR, output='sos'), x)

def sweep_lowpass(x, start, end, block=512):
    """Low-pass whose cutoff glides from start to end (Hz) across the signal."""
    out = np.zeros_like(x)
    zi = None
    for i in range(0, len(x), block):
        f = start * (end / start) ** (i / max(1, len(x)))
        sos = butter(2, min(f, SR / 2 - 100), 'low', fs=SR, output='sos')
        if zi is None:
            zi = np.zeros((sos.shape[0], 2))
        out[i:i + block], zi = sosfilt(sos, x[i:i + block], zi=zi)
    return out

def place(track, sound, at):
    i = int(at * SR)
    if i >= len(track):
        return
    end = min(len(track), i + len(sound))
    track[i:end] += sound[:end - i]

def kick():
    n = int(.45 * SR)
    t = np.arange(n) / SR
    freq = 46 + 110 * np.exp(-t * 38)
    body = np.sin(2 * np.pi * np.cumsum(freq) / SR) * np.exp(-t * 7.5)
    click = highpass(np.random.randn(n), 3000) * np.exp(-t * 400) * .25
    return np.tanh((body + click) * 1.6)

def clap():
    n = int(.35 * SR)
    noise = bandpass(np.random.randn(n), 900, 5200)
    e = np.zeros(n)
    for k, d in enumerate([0, .011, .022]):
        i = int(d * SR)
        e[i:] += np.exp(-np.arange(n - i) / SR * (60 if k < 2 else 16))
    return noise * e * .55

def hat(open_=False):
    n = int((.18 if open_ else .05) * SR)
    return highpass(np.random.randn(n), 7000) * np.exp(-np.arange(n) / SR * (18 if open_ else 90)) * .22

def impact():
    n = int(2.6 * SR)
    t = np.arange(n) / SR
    boom = np.sin(2 * np.pi * (38 + 40 * np.exp(-t * 6)) * t) * np.exp(-t * 1.9)
    air = lowpass(np.random.randn(n), 2500) * np.exp(-t * 3.5) * .35
    return np.tanh((boom + air) * 1.3) * .9

def riser(seconds):
    n = int(seconds * SR)
    noise = np.random.randn(n)
    x = sweep_lowpass(noise, 300, 9000) * np.linspace(0, 1, n) ** 2
    t = np.arange(n) / SR
    tone = np.sin(2 * np.pi * (200 * (1 + 3 * (t / seconds) ** 2)) * t) * np.linspace(0, .25, n) ** 2
    return (x * .35 + tone) * .8

def supersaw(freq, n, voices=5, spread=.012):
    out = np.zeros(n)
    for v in range(voices):
        detune = 1 + spread * (v - (voices - 1) / 2) / ((voices - 1) / 2)
        out += saw(freq * detune, n, phase=np.random.rand())
    return out / voices

def reverb(x, seconds=2.4, seed=1):
    rng = np.random.default_rng(seed)
    n = int(seconds * SR)
    ir = rng.standard_normal(n) * np.exp(-np.arange(n) / SR * (6.9 / seconds))
    ir = lowpass(ir, 6000)
    ir /= np.sqrt(np.sum(ir ** 2))
    return fftconvolve(x, ir)[:len(x)]

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('out')
    ap.add_argument('--bars', type=int, default=16)
    ap.add_argument('--drops', default='1,7', help='bars (0-based) where the groove hits with an impact')
    ap.add_argument('--breaks', default='6', help='bars that drop the drums and build a riser into the next bar')
    ap.add_argument('--ending', type=int, default=14, help='bar of the final hit; the rest is the tail')
    ap.add_argument('--key', default='D')
    ap.add_argument('--seed', type=int, default=7)
    args = ap.parse_args()
    np.random.seed(args.seed)
    drops = {int(b) for b in args.drops.split(',') if b}
    breaks = {int(b) for b in args.breaks.split(',') if b}
    total = int(args.bars * BAR * SR)
    root = 50 + NOTES[args.key.upper()] - 2  # D3 for key D
    # i - VI - III - VII (minor key, uplifting): chords as semitone offsets from the key root.
    progression = [[0, 3, 7], [-4, 0, 3], [3, 7, 10], [-2, 2, 5]]
    drums, bass, pads, arp, fx = (np.zeros(total) for _ in range(5))
    first_drop = min(drops) if drops else 0
    K = kick(); C = clap(); H = hat(); O = hat(True); I = impact()
    side = np.ones(total)  # sidechain envelope: ducks pads and bass under each kick
    for bar in range(args.bars):
        start = bar * BAR
        chord = progression[bar % 4]
        grooving = bar >= first_drop and bar not in breaks and bar < args.ending
        if bar in drops or bar == args.ending:
            place(fx, I, start)
        if bar + 1 in drops and bar + 1 > 0:
            place(fx, riser(BAR), start)
        # Pads every bar, swelling in on the intro and the breaks.
        n = int(BAR * SR) + int(.6 * SR)
        pad = sum(supersaw(hz(root + 12 + note), n) for note in chord) / 3
        pad += supersaw(hz(root + 24 + chord[1]), n, voices=3, spread=.008) * .4
        bright = 2400 if grooving else 900
        pad = lowpass(pad, bright) * env(n, .35 if grooving else 1.2, 4.0, 1.2)
        place(pads, pad * (.32 if grooving else .42), start)
        if bar >= args.ending:
            continue
        if grooving:
            for beat in range(4):
                place(drums, K, start + beat * BEAT)
                i = int((start + beat * BEAT) * SR)
                dip = 1 - .65 * np.exp(-np.arange(int(BEAT * SR)) / SR * 9)
                side[i:i + len(dip)] = np.minimum(side[i:i + len(dip)], dip[:max(0, min(len(dip), total - i))])
                if beat in (1, 3):
                    place(drums, C, start + beat * BEAT)
                place(drums, O if beat == 3 else H, start + beat * BEAT + BEAT / 2)
                if bar % 2 == 1:
                    place(drums, H * .5, start + beat * BEAT + BEAT * .75)
            # Sub bass: eighth notes on the chord root, the last eighth stepping to the fifth.
            for e in range(8):
                note = root - 12 + chord[0] + (7 if e == 7 else 0)
                m = int(BEAT / 2 * SR)
                t = np.arange(m) / SR
                tone = np.sin(2 * np.pi * hz(note) * t) + .25 * lowpass(saw(hz(note), m), 500)
                place(bass, tone * env(m, .005, .22, 3.5) * .55, start + e * BEAT / 2)
        # Plucked arpeggio in sixteenths, brighter as the film builds.
        if bar >= first_drop - 1:
            pattern = [0, 1, 2, 1, 2, 3, 2, 1] * 2
            notes = chord + [chord[0] + 12]
            for s16 in range(16):
                note = root + 24 + notes[pattern[s16]]
                m = int(BEAT / 4 * SR * 1.6)
                pl = lowpass(saw(hz(note), m) * .6 + np.sign(np.sin(2 * np.pi * hz(note) * np.arange(m) / SR)) * .2, 1800 + 300 * (bar % 4))
                place(arp, pl * env(m, .002, .16, 4.5) * (.14 if grooving else .08), start + s16 * BEAT / 4)
    wet = reverb(pads * .5 + arp * .8 + drums * .12, 2.6)
    left = drums * .9 + bass * side + pads * side + arp * side * .9 + fx + wet * .35
    right = drums * .9 + bass * side + pads * side * .97 + arp * side + fx + reverb(pads * .5 + arp * .8, 2.4, seed=3) * .35
    stereo = np.stack([left, right], axis=1)
    fade = int(.04 * SR)
    stereo[:fade] *= np.linspace(0, 1, fade)[:, None]
    stereo = np.tanh(stereo * 1.15)
    stereo /= np.max(np.abs(stereo)) / 0.89
    import wave
    with wave.open(args.out, 'wb') as w:
        w.setnchannels(2); w.setsampwidth(2); w.setframerate(SR)
        w.writeframes((stereo * 32767).astype('<i2').tobytes())
    print(f'wrote {args.out}: {total / SR:.1f} s')

if __name__ == '__main__':
    main()
