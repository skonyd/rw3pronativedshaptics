"""Compare two USBPcap captures of PC-mode Sensa frames (Razer class 0x16 cmd 0x0E) for the same audio, decoding every
frame with the controller's own firmware decoder (Dev2FW 2.04.00, PC decoder 0x2A708) in an ARM emulator, so
Synapse's compact frames (header 80) and RW3's full frames are read the same way the controller reads them.

Offline: reads .pcap files and the firmware image only. Needs unicorn (tools/firmware/emulate_decoder.py).

    python -I compare-sensa-song-emu.py Dev2FW.bin A.pcap startA_iso B.pcap startB_iso seconds [out.json]
"""
import json, math, pathlib, statistics, struct, sys
from datetime import datetime

sys.path.insert(0, str(pathlib.Path(__file__).parent / 'firmware'))
import emulate_decoder as emu   # noqa: E402


def packets(path):
    data = pathlib.Path(path).read_bytes()
    pos, out = 24, []
    while pos + 16 <= len(data):
        sec, usec, size, _ = struct.unpack_from('<IIII', data, pos); pos += 16
        pkt = data[pos:pos + size]; pos += size
        if len(pkt) < 27 or pkt[16] & 1:
            continue
        pay = pkt[struct.unpack_from('<H', pkt)[0]:]
        i = pay.find(bytes.fromhex('2109040301005b00'))
        if i < 0:
            continue
        rep = pay[i + 8:i + 99]
        if len(rep) == 91 and rep[7:10] == bytes.fromhex('160e01'):
            out.append((sec + usec / 1e6, rep[10:9 + rep[6]]))
    return out


def band(v, base):
    """(peak amplitude 0..63, pitch Hz of the loudest point) of one band; v = int16 body-part struct."""
    n = v[base]
    pts = [v[base + 1 + 3 * k: base + 4 + 3 * k] for k in range(min(n, 5))]
    pts = [p for p in pts if len(p) == 3]
    if not pts:
        return 0.0, None
    t, hz, amp = max(pts, key=lambda p: p[2])
    return amp / 520, (hz if amp > 0 else None)


def decode_all(img, frames, cache):
    out = []
    for t, fr in frames:
        key = fr.hex()
        if key not in cache:
            d = emu.decode(img, emu.DECODER['pc'], fr)
            hands = []
            for side in range(2):
                v = d[side]
                b1, b2, b3 = band(v, 17), band(v, 33), band(v, 49)
                hands.append({'b1': b1, 'b2': b2, 'b3': b3, 'hits': v[1]})
            cache[key] = {'len': len(fr), 'hdr': fr[0] >> 1, 'hands': hands}
        out.append((t, cache[key]))
    return out


def grid(dec, start, seconds, step=0.05):
    """Left/right peak strength (all bands) on a fixed grid; the controller keeps playing the last frame."""
    res, j, cur = [], 0, (0.0, 0.0)
    for k in range(int(seconds / step)):
        t = start + k * step
        while j < len(dec) and dec[j][0] <= t:
            h = dec[j][1]['hands']
            cur = tuple(max(x['b1'][0], x['b2'][0], x['b3'][0]) for x in h); j += 1
        res.append(cur)
    return res


def corr(a, b):
    n = min(len(a), len(b)); a, b = a[:n], b[:n]
    ma, mb = statistics.mean(a), statistics.mean(b)
    va = sum((x - ma) ** 2 for x in a); vb = sum((y - mb) ** 2 for y in b)
    return sum((x - ma) * (y - mb) for x, y in zip(a, b)) / math.sqrt(va * vb) if va and vb else float('nan')


def q(xs, ps=(0.1, 0.5, 0.9)):
    xs = sorted(xs)
    return [round(xs[min(len(xs) - 1, int(p * len(xs)))], 1) for p in ps] if xs else None


def summary(dec, start, seconds):
    sel = [(t, d) for t, d in dec if start <= t < start + seconds]
    gaps = [(b[0] - a[0]) * 1000 for a, b in zip(sel, sel[1:])]
    out = {'frames': len(sel), 'framesPerSecond': round(len(sel) / seconds, 1), 'gapMsP10P50P90': q(gaps),
           'frameBytes': sorted({d['len'] for _, d in sel}), 'headers': sorted({d['hdr'] for _, d in sel})}
    for side, name in ((0, 'left'), (1, 'right')):
        hs = [d['hands'][side] for _, d in sel]
        peaks = [max(h['b1'][0], h['b2'][0], h['b3'][0]) for h in hs]
        p1 = [h['b1'][1] for h in hs if h['b1'][1]]
        out[name] = {
            'activeShare': round(sum(p > 0.5 for p in peaks) / max(1, len(peaks)), 3),
            'strength63P10P50P90Max': q(peaks) + [round(max(peaks, default=0), 1)],
            'band1PitchHzP10P50P90': q(p1),
            'distinctBand1Pitches': len({round(x) for x in p1}),
            'framesWithBand2': sum(h['b2'][0] > 0 for h in hs), 'framesWithBand3': sum(h['b3'][0] > 0 for h in hs),
            'framesWithHits': sum(h['hits'] > 0 for h in hs),
        }
    lr = [(d['hands'][0], d['hands'][1]) for _, d in sel]
    out['leftEqualsRightShare'] = round(sum(a == b for a, b in lr) / max(1, len(lr)), 3)
    return out


def main():
    img = open(sys.argv[1], 'rb').read()
    a, sa, b, sb, secs = sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5], float(sys.argv[6])
    ta = datetime.fromisoformat(sa.replace('Z', '+00:00')).timestamp()
    tb = datetime.fromisoformat(sb.replace('Z', '+00:00')).timestamp()
    cache = {}
    da, db = decode_all(img, packets(a), cache), decode_all(img, packets(b), cache)
    rep = {'a': {'file': pathlib.Path(a).name, **summary(da, ta, secs)},
           'b': {'file': pathlib.Path(b).name, **summary(db, tb, secs)}}
    A, B = grid(da, ta, secs), grid(db, tb, secs)
    best = None
    for lag in range(-40, 41):   # +-2 s: start clocks and RW3's Bluetooth sync delay differ
        xa = A[max(0, lag):]; xb = B[max(0, -lag):]
        c = corr([l + r for l, r in xa], [l + r for l, r in xb])
        if best is None or c > best[1]:
            best = (lag, c)
    lag = best[0]
    xa = A[max(0, lag):]; xb = B[max(0, -lag):]
    rep['envelope'] = {'bLagsAMs': lag * 50, 'correlationSum': round(best[1], 3),
                       'correlationLeft': round(corr([p[0] for p in xa], [p[0] for p in xb]), 3),
                       'correlationRight': round(corr([p[1] for p in xa], [p[1] for p in xb]), 3),
                       'meanStrengthA': round(statistics.mean(l + r for l, r in xa) / 2, 1),
                       'meanStrengthB': round(statistics.mean(l + r for l, r in xb) / 2, 1)}
    text = json.dumps(rep, indent=1, ensure_ascii=False)
    print(text)
    if len(sys.argv) > 7:
        pathlib.Path(sys.argv[7]).write_text(text, encoding='utf-8')


if __name__ == '__main__':
    main()
