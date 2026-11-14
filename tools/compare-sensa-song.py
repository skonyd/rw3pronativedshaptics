"""Compare two USBPcap captures of PC-mode Sensa frames (class 0x16 cmd 0x0E) for the same audio, e.g. Synapse's
"Audio to Haptics" vs RW3. Offline: reads .pcap files only, never touches a device.

Frame layout (docs/SENSA_USB_PROTOCOL.md): 7-bit header, then per hand 3 x (flag + 4 x (strength6, pitch7)),
then a transient list (flag + s6 + p7 + t7, up to 2, 0 flag ends it). Pitch p -> 30 + p*370/127 Hz.

    python -I compare-sensa-song.py A.pcap B.pcap --start-a ISO --start-b ISO --duration 224 [--out report.json]
"""
import argparse, json, math, pathlib, statistics, struct, sys
from datetime import datetime


def hz(p):
    return 30 + p * 370 / 127


def decode(frame):
    bits = ''.join(f'{b:08b}' for b in frame)
    at = 0

    def get(n):
        nonlocal at
        if at + n > len(bits):
            raise ValueError('truncated')
        v = int(bits[at:at + n], 2); at += n
        return v
    header = get(7)
    hands = []
    for _ in range(2):
        bands = []
        for _b in range(3):
            if not get(1):
                bands.append([]); continue
            bands.append([(get(6), get(7)) for _ in range(4)])
        hits = []
        while len(hits) < 2 and get(1):
            hits.append((get(6), get(7), get(7)))
        hands.append({'bands': bands, 'hits': hits})
    return header, hands


def frames(path):
    data = pathlib.Path(path).read_bytes()
    if data[:4] != bytes.fromhex('d4c3b2a1') or struct.unpack_from('<I', data, 20)[0] != 249:
        raise SystemExit(f'{path}: not a USBPcap pcap')
    pos, out = 24, []
    while pos + 16 <= len(data):
        sec, usec, size, _ = struct.unpack_from('<IIII', data, pos); pos += 16
        pkt = data[pos:pos + size]; pos += size
        if len(pkt) < 27:
            continue
        hlen = struct.unpack_from('<H', pkt)[0]
        if pkt[16] & 1:          # completion direction: skip, the submit carries the data
            continue
        pay = pkt[hlen:]
        i = pay.find(bytes.fromhex('2109040301005b00'))   # SET_REPORT feature 4, 91 bytes
        if i < 0:
            continue
        rep = pay[i + 8:i + 8 + 91]
        if len(rep) != 91 or rep[7:10] != bytes.fromhex('160e01'):
            continue
        fr = rep[10:9 + rep[6]]
        try:
            h, hands = decode(fr)
        except ValueError:
            continue
        out.append({'t': sec + usec / 1e6, 'header': h, 'hands': hands})
    return out


def features(fr):
    """Per frame and hand: peak strength (0..63), strength-weighted mean pitch (Hz), active band count, hits."""
    res = []
    for hand in fr['hands']:
        pts = [(s, p) for band in hand['bands'] for (s, p) in band]
        active = [(s, p) for s, p in pts if s > 0]
        peak = max((s for s, _ in pts), default=0)
        wpitch = sum(s * hz(p) for s, p in active) / sum(s for s, _ in active) if active else None
        bands = sum(1 for band in hand['bands'] if any(s > 0 for s, _ in band))
        res.append({'peak': peak, 'pitch': wpitch, 'bands': bands, 'hits': len(hand['hits']),
                    'pitches': sorted({round(hz(p)) for s, p in active})})
    return res


def series(frs, start, duration, step=0.05):
    """Resample the left+right peak strength on a fixed grid (last frame holds, as the controller does)."""
    n = int(duration / step)
    out, j, cur = [], 0, (0, 0)
    for k in range(n):
        t = start + k * step
        while j < len(frs) and frs[j]['t'] <= t:
            f = features(frs[j]); cur = (f[0]['peak'], f[1]['peak']); j += 1
        out.append(cur)
    return out


def corr(a, b):
    ma, mb = statistics.mean(a), statistics.mean(b)
    va = sum((x - ma) ** 2 for x in a); vb = sum((y - mb) ** 2 for y in b)
    return sum((x - ma) * (y - mb) for x, y in zip(a, b)) / math.sqrt(va * vb) if va and vb else float('nan')


def summary(name, frs, start, duration):
    sel = [f for f in frs if start <= f['t'] < start + duration]
    if not sel:
        return {'name': name, 'frames': 0}
    gaps = [b['t'] - a['t'] for a, b in zip(sel, sel[1:])]
    feats = [features(f) for f in sel]
    out = {'name': name, 'frames': len(sel), 'framesPerSecond': len(sel) / duration,
           'gapMsP10P50P90': [round(statistics.quantiles(gaps, n=10)[i] * 1000, 1) for i in (0, 4, 8)] if len(gaps) > 10 else None,
           'headers': sorted({f['header'] for f in sel})}
    for side, label in ((0, 'left'), (1, 'right')):
        peaks = [f[side]['peak'] for f in feats]
        pitches = [f[side]['pitch'] for f in feats if f[side]['pitch']]
        out[label] = {
            'activeShare': round(sum(p > 0 for p in peaks) / len(peaks), 3),
            'strengthMeanP50P90Max': [round(statistics.mean(peaks), 1), statistics.median(peaks),
                                       sorted(peaks)[int(len(peaks) * .9)], max(peaks)],
            'pitchHzP10P50P90': [round(x) for x in (statistics.quantiles(pitches, n=10)[i] for i in (0, 4, 8))] if len(pitches) > 10 else None,
            'bandsUsed': {k: sum(f[side]['bands'] == k for f in feats) for k in range(4)},
            'framesWithHits': sum(f[side]['hits'] > 0 for f in feats),
        }
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('a'); ap.add_argument('b')
    ap.add_argument('--start-a', required=True); ap.add_argument('--start-b', required=True)
    ap.add_argument('--duration', type=float, required=True)
    ap.add_argument('--out')
    args = ap.parse_args()
    sa = datetime.fromisoformat(args.start_a.replace('Z', '+00:00')).timestamp()
    sb = datetime.fromisoformat(args.start_b.replace('Z', '+00:00')).timestamp()
    fa, fb = frames(args.a), frames(args.b)
    report = {'a': summary(pathlib.Path(args.a).stem, fa, sa, args.duration),
              'b': summary(pathlib.Path(args.b).stem, fb, sb, args.duration)}
    if fa and fb:
        A, B = series(fa, sa, args.duration), series(fb, sb, args.duration)
        best = None
        for lag in range(-40, 41):          # +-2 s alignment search (start clocks differ slightly)
            xa = [l + r for l, r in A[max(0, lag):len(A) + min(0, lag)]]
            xb = [l + r for l, r in B[max(0, -lag):len(B) + min(0, -lag)]]
            n = min(len(xa), len(xb))
            c = corr(xa[:n], xb[:n])
            if best is None or c > best[1]:
                best = (lag, c)
        lag = best[0]
        def side(i):
            xa = [p[i] for p in A[max(0, lag):len(A) + min(0, lag)]]
            xb = [p[i] for p in B[max(0, -lag):len(B) + min(0, -lag)]]
            n = min(len(xa), len(xb)); return round(corr(xa[:n], xb[:n]), 3)
        report['envelope'] = {'bestLagMs': lag * 50, 'correlationSum': round(best[1], 3),
                              'correlationLeft': side(0), 'correlationRight': side(1)}
    text = json.dumps(report, indent=1, ensure_ascii=False)
    print(text)
    if args.out:
        pathlib.Path(args.out).write_text(text, encoding='utf-8')


if __name__ == '__main__':
    main()
