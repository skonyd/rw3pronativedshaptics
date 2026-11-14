"""Steady-tone probe analysis: per tone, detected peak frequency, its level and the level at the target pitch code.
    python -I analyze-tone-probe.py <dir> <wav name> <json name>"""
import importlib.util, json, pathlib, sys
import numpy as np
spec = importlib.util.spec_from_file_location('a', pathlib.Path(__file__).with_name('analyze-melody-probe.py'))
a = importlib.util.module_from_spec(spec); spec.loader.exec_module(a)
d = pathlib.Path(sys.argv[1])
x, rate = a.read_wav(d / sys.argv[2]); log = json.loads((d / sys.argv[3]).read_text(encoding='utf-8'))
first = log[0]['Start']
_, _, noise = a.spectrum_track(x, rate, max(0, first - 0.8), first - 0.1, lo=40, hi=450)
out = {'noisePeakDb': round(float(np.median(noise)), 1), 'tones': []}
for s in log:
    tt, pk, lv = a.spectrum_track(x, rate, s['Start'] + 0.3, s['End'] - 0.1, lo=40, hi=450)
    target = a.code_hz(s['Hz'])
    out['tones'].append({'hz': s['Hz'], 'detectedHz': round(float(np.median(pk)), 1), 'peakDb': round(float(np.median(lv)), 1),
                         'atTargetDb': round(a.band_level(x, rate, s['Start'] + 0.3, s['End'] - 0.1, target), 1)})
print(json.dumps(out, indent=1)); (d / 'analysis.json').write_text(json.dumps(out, indent=1), encoding='utf-8')
