"""Pitch tracking for the melody-mode probe: webcam-mic WAV + scene log -> how well the palm actuator follows pitch
glides, arpeggios and 3-band chords. Offline (numpy only).

    python -I analyze-melody-probe.py <dir with melody-probe.wav and melody-probe.json>
"""
import json, math, pathlib, struct, sys
import numpy as np


def read_wav(path):
    b = pathlib.Path(path).read_bytes()
    pos, fmt, data = 12, None, None
    while pos + 8 <= len(b):
        cid, size = b[pos:pos + 4], struct.unpack_from('<I', b, pos + 4)[0]
        body = b[pos + 8:pos + 8 + size]
        if cid == b'fmt ':
            tag, ch, rate, _, _, bits = struct.unpack_from('<HHIIHH', body)
            if tag == 0xFFFE:
                tag = struct.unpack_from('<H', body, 24)[0]
            fmt = (tag, ch, rate, bits)
        elif cid == b'data':
            data = body
        pos += 8 + size + (size & 1)
    tag, ch, rate, bits = fmt
    x = np.frombuffer(data, dtype=np.float32 if tag == 3 else np.int16).astype(np.float64)
    if tag != 3:
        x /= 32768
    return x.reshape(-1, ch).mean(axis=1), rate


def P(hz):
    return int(min(127, max(0, round((hz - 30) * 127 / 370))))


def code_hz(hz):
    p = P(hz)
    return 30 + p * 370 / 127


def glide(t):
    return 60 * (300 / 60) ** min(max(t / 2.5, 0), 1)


NOTES = [130.8, 164.8, 196.0, 261.6, 196.0, 164.8, 130.8, 98.0]


def arp(t, note):
    return NOTES[min(len(NOTES) - 1, int(t / note))]


def spectrum_track(x, rate, t0, t1, win=2048, hop=0.01, lo=40, hi=450):
    w = np.hanning(win)
    times, peaks, levels = [], [], []
    nfft = 16384
    freqs = np.fft.rfftfreq(nfft, 1 / rate)
    band = (freqs >= lo) & (freqs <= hi)
    t = t0
    while t + win / rate <= t1:
        i = int(t * rate)
        seg = x[i:i + win] * w
        mag = np.abs(np.fft.rfft(seg, nfft))
        m = mag[band]
        k = int(np.argmax(m))
        times.append(t + win / rate / 2 - t0)
        peaks.append(freqs[band][k])
        levels.append(20 * math.log10(m[k] + 1e-12))
        t += hop
    return np.array(times), np.array(peaks), np.array(levels)


def band_level(x, rate, t0, t1, f, width=6):
    i0, i1 = int(t0 * rate), int(t1 * rate)
    seg = x[i0:i1] * np.hanning(i1 - i0)
    mag = np.abs(np.fft.rfft(seg))
    freqs = np.fft.rfftfreq(len(seg), 1 / rate)
    sel = (freqs > f - width) & (freqs < f + width)
    return 20 * math.log10(mag[sel].max() + 1e-12)


def semis(a, b):
    return 12 * np.log2(a / b)


def main():
    d = pathlib.Path(sys.argv[1])
    x, rate = read_wav(d / 'melody-probe.wav')
    scenes = json.loads((d / 'melody-probe.json').read_text(encoding='utf-8'))
    report = {}
    # room noise floor at the peak band: the silence just before the first scene
    first = scenes[0]['Start']
    _, _, noise = spectrum_track(x, rate, max(0, first - 0.6), first - 0.05)
    floor = float(np.median(noise)) if len(noise) else None
    report['noisePeakDb'] = round(floor, 1) if floor is not None else None
    for sc in scenes:
        name, t0, t1 = sc['Name'], sc['Start'], sc['End']
        tt, pk, lv = spectrum_track(x, rate, t0, t1)
        r = {'frames': sc['Sent'], 'framesPerSecond': round(sc['Sent'] / (t1 - t0), 1), 'levelDb': round(float(np.median(lv)), 1)}
        if name.startswith('glide') or name.startswith('arp'):
            if name.startswith('glide'):
                f = lambda t: code_hz(glide(t))
            else:
                note = 0.15 if 'arp150' in name else 0.06
                f = (lambda t, n=note: code_hz(arp(t, n))) if 'arp150' in name else (lambda t, n=note: code_hz(arp(t % 0.48, n)))
            best = None
            for lag in np.arange(0, 0.2, 0.005):      # USB + actuator delay
                tgt = np.array([f(max(0.0, t - lag)) for t in tt])
                err = np.abs(semis(pk, tgt))
                score = float(np.mean(err < 1.0))
                if best is None or score > best[1]:
                    best = (lag, score, float(np.median(err)))
            r.update({'delayMs': round(best[0] * 1000), 'withinOneSemitone': round(best[1], 3), 'medianErrorSemitones': round(best[2], 2)})
            # smoothness: distinct detected pitch steps per second (pitch codes are 2.9 Hz apart)
            r['distinctPitchesPerSecond'] = round(len(set(np.round(pk / 3))) / (t1 - t0), 1)
        if name.startswith('chord'):
            r['peaksDb'] = {str(f): round(band_level(x, rate, t0 + 0.3, t1 - 0.1, f), 1) for f in (80, 160, 240)}
        if name.startswith('single') or name.startswith('high') or name.startswith('steady'):
            target = {'single_160': 160, 'single_240': 240, 'high_350_33ms': 350, 'steady100_33ms': 100}[name]
            r['targetPeakDb'] = round(band_level(x, rate, t0 + 0.3, t1 - 0.1, code_hz(target)), 1)
            r['detectedHzMedian'] = round(float(np.median(pk)), 1)
        report[name] = r
    out = json.dumps(report, indent=1)
    print(out)
    (d / 'melody-probe-analysis.json').write_text(out, encoding='utf-8')


if __name__ == '__main__':
    main()
