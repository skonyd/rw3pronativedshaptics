"""Melody A/B analysis (synthetic song: 60 Hz bass + C5-E5-G5-C6 melody in 250 ms notes, optional noise bursts every
1.5 s at 0.70-0.85 s): per method, share of note slots where the expected folded note (262/330/392 Hz) is the loudest
of the three, its margin, the bass level, and how much a noise burst raises the 40-400 Hz level.
    python -I analyze-melody-ab.py <dir>   (melody-live.wav + melody-live.json)"""
import importlib.util, json, pathlib, sys
import numpy as np
spec = importlib.util.spec_from_file_location('a', pathlib.Path(__file__).with_name('analyze-melody-probe.py'))
a = importlib.util.module_from_spec(spec); spec.loader.exec_module(a)
d = pathlib.Path(sys.argv[1])
x, rate = a.read_wav(d / 'melody-live.wav'); log = json.loads((d / 'melody-live.json').read_text(encoding='utf-8'))
seq = [261.6, 329.6, 392.0, 261.6, 392.0, 329.6]; notes = [261.6, 329.6, 392.0]


def band_db(t0, t1, lo=40, hi=400):
    i0, i1 = int(t0 * rate), int(t1 * rate); seg = x[i0:i1] * np.hanning(i1 - i0)
    m = np.abs(np.fft.rfft(seg)); f = np.fft.rfftfreq(len(seg), 1 / rate); sel = (f > lo) & (f < hi)
    return 10 * np.log10(np.sum(m[sel] ** 2) + 1e-12)


res = {}
for s in log:
    t0, t1 = s['Start'], s['End']; best = None
    for lag in np.arange(0, 0.35, 0.01):
        correct = total = 0; margins = []; k = 0
        while True:
            a0 = t0 + lag + k * 0.25 + 0.05; a1 = a0 + 0.15
            if a1 > t1: break
            ts = (a0 - t0 - lag) % 1.5
            if not (0.55 <= ts <= 1.0):
                tgt = seq[k % 6]; lv = {n: a.band_level(x, rate, a0, a1, n, width=5) for n in notes}
                others = max(v for n, v in lv.items() if n != tgt); margins.append(lv[tgt] - others); correct += lv[tgt] > others; total += 1
            k += 1
        sc = correct / max(1, total)
        if best is None or sc > best[1]: best = (lag, sc, float(np.median(margins)), total)
    lag = best[0]; rises = []; t = 0.70
    while t0 + lag + t + 0.15 < t1:
        rises.append(band_db(t0 + lag + t, t0 + lag + t + 0.12) - band_db(t0 + lag + t - 0.3, t0 + lag + t - 0.18)); t += 1.5
    res[s['Method']] = {'notesCorrect': round(best[1], 3), 'notes': best[3], 'medianMarginDb': round(best[2], 1), 'lagMs': round(lag * 1000),
                        'bass60Db': round(a.band_level(x, rate, t0 + 0.5, t1 - 0.2, 60), 1),
                        'burstRiseDb': round(float(np.median(rises)), 1) if rises else None, 'fallbackHandFrames': s.get('MelodyFallbackFrames')}
print(json.dumps(res, indent=1)); (d / 'analysis.json').write_text(json.dumps(res, indent=1), encoding='utf-8')
