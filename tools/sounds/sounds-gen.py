# All melodic sounds use C-major pentatonic (C D E G A) so the set is consonant and feels like one family;
# modules differ by timbre and rhythm first, pitch second (earcon practice).
# Synthesises iPop's notification sound set (no samples, so no licences) and writes MP3s.
# Usage: pip install lameenc; python tools/sounds/sounds-gen.py src/IPop.Web/wwwroot/sounds
import math, random, sys
from pathlib import Path
import lameenc

SR = 44100
OUT = Path(sys.argv[1])
random.seed(7)


def silence(sec):
    return [0.0] * int(SR * sec)


def mix(dst, src, at, gain=1.0):
    i0 = int(at * SR)
    if len(dst) < i0 + len(src):
        dst.extend([0.0] * (i0 + len(src) - len(dst)))
    for i, v in enumerate(src):
        dst[i0 + i] += v * gain
    return dst


NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B']


def hz(name):  # "C6" -> Hz
    midi = 12 * (int(name[-1]) + 1) + NAMES.index(name[:-1])
    return 440 * 2 ** ((midi - 69) / 12)


# ── instruments (each returns a mono list) ──
def marimba(f, dur=0.6, bright=1.0):
    out = []
    for i in range(int(SR * dur)):
        t = i / SR
        out.append(min(1, t / 0.002) * (math.sin(2 * math.pi * f * t) * math.exp(-t * 7)
                   + bright * 0.35 * math.sin(2 * math.pi * f * 3.98 * t) * math.exp(-t * 30)
                   + bright * 0.12 * math.sin(2 * math.pi * f * 9.2 * t) * math.exp(-t * 60)))
    return out


def bell(f, dur=1.4, ratio=3.5, index=2.2, decay=3.2):
    out = []
    for i in range(int(SR * dur)):
        t = i / SR
        env = min(1, t / 0.003) * math.exp(-t * decay)
        out.append(env * math.sin(2 * math.pi * f * t + index * math.exp(-t * 4) * math.sin(2 * math.pi * f * ratio * t)))
    return out


def glass(f, dur=1.0, decay=4.5):
    out = []
    for i in range(int(SR * dur)):
        t = i / SR
        env = min(1, t / 0.004) * math.exp(-t * decay)
        out.append(env * (math.sin(2 * math.pi * f * t)
                          + 0.3 * math.sin(2 * math.pi * f * 2.76 * t) * math.exp(-t * 6)
                          + 0.12 * math.sin(2 * math.pi * f * 5.4 * t) * math.exp(-t * 12)))
    return out


def pop(f0, f1, dur=0.12):
    out, ph = [], 0.0
    for i in range(int(SR * dur)):
        t = i / SR
        ph += 2 * math.pi * (f0 + (f1 - f0) * (1 - math.exp(-t * 40))) / SR
        out.append(math.sin(ph) * min(1, t / 0.002) * math.exp(-t * 28))
    return out


def pluck(f, dur=0.7, damp=0.996):
    p = max(2, int(SR / f))
    buf = [random.uniform(-1, 1) for _ in range(p)]
    out = []
    for i in range(int(SR * dur)):
        v, nxt = buf[i % p], buf[(i + 1) % p]
        buf[i % p] = damp * 0.5 * (v + nxt)
        out.append(v)
    return out


def soft_brass(f, dur=0.5):
    out = []
    for i in range(int(SR * dur)):
        t = i / SR
        env = min(1, t / 0.03) * (1 if t < dur - 0.15 else max(0, (dur - t) / 0.15))
        out.append(env * sum(math.sin(2 * math.pi * f * k * t) / k * math.exp(-k * 0.35) for k in range(1, 7)))
    return out


def sine(f, dur, attack=0.01, release=0.05):
    n = int(SR * dur)
    return [math.sin(2 * math.pi * f * i / SR) * min(1, (i / SR) / attack, (dur - i / SR) / release) for i in range(n)]


def rustle(dur=0.12):
    out, lp = [], 0.0
    for i in range(int(SR * dur)):
        lp += 0.35 * (random.uniform(-1, 1) - lp)
        out.append(lp * math.sin(math.pi * (i / SR) / dur) ** 2)
    return out


def reverb(x, wet=0.18):
    # Small Schroeder room: 4 combs + 2 allpasses.
    y = list(x) + [0.0] * int(SR * 0.6)
    acc = [0.0] * len(y)
    for d, g in ((1557, .80), (1617, .79), (1491, .78), (1422, .77)):
        buf = [0.0] * d
        for i, v in enumerate(y):
            o = buf[i % d]
            buf[i % d] = v + o * g
            acc[i] += o
    for d, g in ((225, .5), (556, .5)):
        buf, nxt = [0.0] * d, []
        for i, v in enumerate(acc):
            b = buf[i % d]
            buf[i % d] = v + b * g
            nxt.append(b - v)
        acc = nxt
    out = [a + wet * 0.25 * b for a, b in zip(y, acc)]
    while len(out) > 1 and abs(out[-1]) < 1e-4:
        out.pop()
    return out


def soften(x, cutoff=6000):
    # One-pole low-pass: takes the glassy edge off the upper partials so nothing sounds shrill.
    a = 1 - math.exp(-2 * math.pi * cutoff / SR)
    out, y = [], 0.0
    for v in x:
        y += a * (v - y)
        out.append(y)
    return out


def finish(x, peak_db, name, rev=0.18):
    x = soften(x)
    x = reverb(x, rev) if rev else list(x)
    g = 10 ** (peak_db / 20) / (max(abs(v) for v in x) or 1)
    fade = int(SR * 0.02)
    pcm = bytearray()
    for i, v in enumerate(x):
        if i > len(x) - fade:
            v *= (len(x) - i) / fade
        pcm += int(max(-1, min(1, v * g)) * 32767).to_bytes(2, 'little', signed=True)
    enc = lameenc.Encoder()
    enc.set_bit_rate(96)
    enc.set_in_sample_rate(SR)
    enc.set_channels(1)
    enc.set_quality(2)
    data = enc.encode(bytes(pcm)) + enc.flush()
    (OUT / f'{name}.mp3').write_bytes(data)
    print(f'{name:16} {len(x) / SR:4.2f}s {len(data) // 1024:3} KB')


def seq(steps, inst):
    out = []
    for at, note, g in steps:
        mix(out, inst(hz(note)), at, g)
    return out


SOFT, LOUD = -9, -3

# 1 DM: two rising water drops
finish(mix(pop(700, 1100), pop(950, 1500), 0.09), SOFT, 'dm')
# 2 Group chat: two quick marimba notes a fourth apart
finish(seq([(0, 'E6', .8), (.08, 'A6', 1)], lambda f: marimba(f, .45)), SOFT, 'group')
# 3 Mention / chat emergency: bright bell arpeggio, louder
finish(seq([(0, 'C6', .7), (.07, 'E6', .8), (.14, 'G6', .9), (.21, 'C7', 1)],
           lambda f: bell(f, .9, 2.0, 0.9, 5)), LOUD, 'mention')
# 4 Mail: two glass tones rising a fifth
finish(seq([(0, 'G5', .85), (.13, 'D6', 1)], lambda f: glass(f, .9)), SOFT, 'mail')
# 5 Meeting: three bell notes descending; the reminder is the louder copy
mt = seq([(0, 'E6', 1), (.22, 'C6', .9), (.44, 'G5', .85)], lambda f: bell(f, 1.2, 1.0, 1.2, 3.5))
finish(mt, SOFT, 'meeting')
finish(mt, LOUD, 'meeting-urgent')
# 6 Project / task: quick rising marimba arpeggio
finish(seq([(0, 'C5', .7), (.06, 'E5', .8), (.12, 'G5', .9), (.18, 'C6', 1)], lambda f: marimba(f, .5)), SOFT, 'project')
# 7 Announcement: soft two-note fanfare; firm three-note variant for mandatory memo / condolence
finish(seq([(0, 'G4', .8), (.16, 'C5', 1)], lambda f: soft_brass(f, .38)), SOFT, 'announce')
finish(seq([(0, 'G4', .8), (.15, 'C5', .9), (.30, 'E5', 1)], lambda f: soft_brass(f, .55)), LOUD, 'announce-firm')
# 8 Notes shared: paper rustle + soft high pluck
finish(mix(rustle(.1), [v * 1.4 for v in pluck(hz('A5'), .6)], .05), SOFT, 'notes')
# 9 System: single soft ping
finish(glass(hz('A5'), .6, 7), SOFT - 2, 'system')

# Extra choices for the per-module picker
finish(glass(hz('C7'), .7, 6), SOFT, 'x-glass')
finish(pop(500, 900, .16), SOFT, 'x-drop')
finish(seq([(0, 'D5', .6), (.1, 'A5', .7), (.2, 'E6', .8)], lambda f: pluck(f, .8)), SOFT, 'x-harp')

# Open chat tick and calls
finish(pop(1800, 2200, .05), -18, 'tick', rev=0)
ring = seq([(t * .16, n, 1) for t, n in enumerate(['E6', 'G6', 'E6', 'C7'])]
           + [(1.1 + t * .16, n, 1) for t, n in enumerate(['E6', 'G6', 'E6', 'C7'])], lambda f: marimba(f, .6))
finish(ring + silence(.8), LOUD, 'ringtone')
finish(sine(425, 1.0, .03, .08) + silence(2.0), -12, 'ringback', rev=0)
finish(seq([(0, 'G5', 1), (.16, 'C5', .9)], lambda f: glass(f, .6, 6)), SOFT, 'hangup')
