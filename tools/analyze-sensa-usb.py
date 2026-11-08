"""Offline, strict USBPcap reader. Never opens or writes a USB device."""
import argparse, collections, datetime, hashlib, json, pathlib, statistics, struct

def decode_frame(frame):
    bits=''.join(f'{b:08b}' for b in frame)
    at=0
    def get(n):
        nonlocal at
        if at+n>len(bits): raise ValueError('truncated frame')
        v=int(bits[at:at+n],2);at+=n;return v
    header=get(7)
    if header not in (60,80): raise ValueError(f'unknown header {header}')
    hands=[]
    for side in range(2):
        bands=[]
        for _ in range(3):
            present=get(1)
            # Observed compact A1 variant omits point payload for zero flags.
            points=[{'a':get(6),'p':get(7)} for _ in range(4)] if present else []
            bands.append(points)
        # Archived 1225-frame HAR corpus: 21 payload bits; the previously assumed extra
        # three bits belong to the following hand. Raw value only; semantics remain uncalibrated.
        transient=get(21) if header==60 and get(1) else None
        hands.append({'bands':bands,'transient':transient})
    if len(bits)-at>7 or any(x!='0' for x in bits[at:]): raise ValueError('unconsumed/nonzero trailing bits')
    return {'header':header,'hands':hands}

def read(path):
    data=path.read_bytes()
    if len(data)<24 or data[:4]!=bytes.fromhex('d4c3b2a1') or struct.unpack_from('<I',data,20)[0]!=249:
        raise ValueError('Expected little-endian USBPcap PCAP (linktype 249)')
    pos=24;records=0;addresses=collections.Counter();frames=[];errors=[];descriptors=[];pending={}
    while pos<len(data):
        if pos+16>len(data): raise ValueError('truncated PCAP record header')
        sec,usec,size,original=struct.unpack_from('<IIII',data,pos);pos+=16
        if pos+size>len(data): raise ValueError('truncated PCAP record data')
        packet=data[pos:pos+size];pos+=size;records+=1
        if len(packet)<27: raise ValueError('short USBPcap header')
        hlen=struct.unpack_from('<H',packet)[0];address=struct.unpack_from('<H',packet,19)[0];addresses[address]+=1
        now=sec+usec/1e6;irp=packet[2:10]
        if packet[16]&1 and irp in pending:
            completed=pending.pop(irp)
            completed['usbCompletionMs']=(now-completed['time'])*1000
            completed['usbCompletionStatus']=struct.unpack_from('<I',packet,10)[0]
        payload=packet[hlen:]
        if bytes.fromhex('32154c0a') in payload:descriptors.append(records)
        if payload[:8]!=bytes.fromhex('2109040301005b00'): continue
        report=payload[8:]
        if len(report)!=91 or report[7:10]!=bytes.fromhex('160e01'):continue
        crc=0
        for value in report[3:89]:crc^=value  # packet-relative 2..87, excluding report ID and transaction
        if crc!=report[89]:raise ValueError(f'checksum mismatch at record {records}')
        frame=report[10:9+report[6]]
        item={'time':now,'record':records,'address':address,'reportHex':report.hex(),'frameHex':frame.hex()}
        try:item['decoded']=decode_frame(frame)
        except ValueError as e:errors.append({'record':records,'error':str(e),'frameHex':frame.hex()})
        frames.append(item)
        pending[irp]=item
    rtts=sorted(x['usbCompletionMs'] for x in frames if 'usbCompletionMs' in x)
    timing={'completedTransfers':len(rtts),'missingCompletionRecords':len(frames)-len(rtts),
            'nonzeroCompletionStatuses':sum(x.get('usbCompletionStatus',0)!=0 for x in frames),
            'roundTripMsMinP50P95Max':[rtts[int((len(rtts)-1)*q)] for q in [0,.5,.95,1]] if rtts else [],
            'limitation':'USB host submit/completion round trip; not physical motor latency.'}
    return {'sha256':hashlib.sha256(data).hexdigest(),'records':records,'addresses':dict(addresses),
            'wolverineDescriptorRecords':descriptors,'frameCount':len(frames),
            'variants':dict(collections.Counter(f'{len(bytes.fromhex(x["frameHex"]))}:{x["frameHex"][:2]}' for x in frames)),
            'decodeErrors':errors,'usbTiming':timing,'frames':frames}

def main():
    cli=argparse.ArgumentParser();cli.add_argument('pcap',type=pathlib.Path);cli.add_argument('out',type=pathlib.Path);cli.add_argument('--timeline',type=pathlib.Path)
    args=cli.parse_args();result=read(args.pcap)
    if args.timeline:
        cases=[]
        for path in sorted(args.timeline.glob('[0-9][0-9][0-9][0-9]-*.json')):
            case=json.loads(path.read_text(encoding='utf-8-sig'))
            start=datetime.datetime.fromisoformat(case['StartUtc']).timestamp();end=datetime.datetime.fromisoformat(case['EndUtc']).timestamp()
            selected=[x for x in result['frames'] if start<=x['time']<end]
            steady=[x for x in selected if start+.45<=x['time']<start+(case['Scene']['DriveMs']+200)/1000]
            def peak(frame):
                if 'decoded' not in frame:return None
                return max((p['a'] for hand in frame['decoded']['hands'] for band in hand['bands'] for p in band),default=0)
            active=[x for x in selected if peak(x) is not None and peak(x)>0]
            nominal_end=(case['LeadMs']+case['Scene']['DriveMs'])/1000
            last_active=active[-1]['time']-start if active else None
            zeros_after_last=[x for x in selected if active and x['time']>active[-1]['time'] and peak(x)==0]
            means=[]
            for side in range(2):
                points=[p for x in steady if 'decoded' in x for band in x['decoded']['hands'][side]['bands'] for p in band]
                active=[p for p in points if p['a']>0]
                means.append({'meanA':statistics.mean(p['a'] for p in points) if points else None,
                    'maxA':max((p['a'] for p in points),default=None),'activePitches':dict(collections.Counter(p['p'] for p in active))})
            cases.append({'source':case,'frames':len(selected),'steadyFrames':len(steady),'hands':means,
                'unknownDecodedFrames':sum('decoded' not in x for x in selected),
                'lastNonzeroCommandAfterNominalDriveEndMs':(last_active-nominal_end)*1000 if last_active is not None else None,
                'explicitZeroFramesAfterLastNonzero':len(zeros_after_last),
                'lastDecodedFramePeakCode':peak(selected[-1]) if selected else None,
                'tailLimitation':'Source Play-call clock and nominal PCM drive end. No physical stop proof; absent frames are not a zero command.'})
        result['cases']=cases
    args.out.write_text(json.dumps(result,indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in result.items() if k not in ('frames','cases','decodeErrors')}))
    print('decode errors',len(result['decodeErrors']))
    for case in result.get('cases',[]):print(case['source']['Scene']['Id'],case['frames'],case['hands'])
    if result['decodeErrors']:raise SystemExit(2)

if __name__=='__main__':main()
