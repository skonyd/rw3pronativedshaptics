"""Summarize all physical old/new cases without turning loudness into fidelity."""
import json, math
from pathlib import Path
from statistics import mean, stdev

root=Path("TestResults/physical-comparison-20261108/microphone")
def db(a,b):return 20*math.log10(a/b) if a>0 and b>0 else None
def section(w,a,b):
    values=[p["BandRms"] for p in w["Bins"] if a<p["SampleTimeMs"]<=b]
    return math.sqrt(mean(x*x for x in values))
rows=[]
for path in sorted(root.glob("case-*.json")):
    d=json.loads(path.read_text(encoding="utf-8-sig"))
    floor=max(d["Background"]["BandRms"],d["EngineIdle"]["BandRms"])
    drive=section(d["Driven"],400,1600);tail=section(d["Driven"],2300,3000)
    bands=[]
    for band in d["Driven"]["ToneBands"]:
        hz=band["CenterHz"]
        baseline=max(next(x["AmplitudeRms"] for x in d[w]["ToneBands"] if x["CenterHz"]==hz) for w in ["Background","EngineIdle"])
        bands.append(dict(Hz=hz,Amplitude=band["AmplitudeRms"],OverBackgroundDb=db(band["AmplitudeRms"],baseline)))
    rows.append(dict(File=path.name,Scene=d["Scene"],Repeat=d["Repeat"],Multiband=d["Multiband"],SourceHash=d["SourceHash"],
        SourceUnchanged=d["SourceUnchanged"],WorkerStopped=d["WorkerStopped"],DriveRms=drive,DriveOverBackgroundDb=db(drive,floor),
        LateTailOverBackgroundDb=db(tail,floor),OffOverBackgroundDb=db(d["EngineOff"]["BandRms"],floor),Bands=bands,
        MicClips=sum(d[w]["Clipped"] for w in ["Background","EngineIdle","Driven","EngineOff"]),HeadroomReductions=d["HeadroomReductions"]))
groups=[]
for scene in sorted({r["Scene"] for r in rows}):
    for multi in [False,True]:
        selected=[r for r in rows if r["Scene"]==scene and r["Multiband"]==multi]
        if not selected:continue
        values=[r["DriveRms"] for r in selected]
        groups.append(dict(Scene=scene,Multiband=multi,N=len(selected),MeanRms=mean(values),CV=stdev(values)/mean(values) if len(values)>1 else None,
            MinimumDriveOverBackgroundDb=min(r["DriveOverBackgroundDb"] for r in selected),
            MaximumLateTailDb=max(r["LateTailOverBackgroundDb"] for r in selected),
            Bands=[dict(Hz=hz,MeanAmplitude=mean(next(b["Amplitude"] for b in r["Bands"] if b["Hz"]==hz) for r in selected),
                MinimumOverBackgroundDb=min(next(b["OverBackgroundDb"] for b in r["Bands"] if b["Hz"]==hz) for r in selected)) for hz in [40,80,120,135,190,300]]))
summary=dict(Cases=len(rows),Complete=len(rows)==60,PhysicalFidelityVerified=False,Groups=groups,Measurements=rows,
    Limits="All cases retained, no loudness normalization/outlier removal/EQ. Steady mic sample-clock(400,1600]ms; late(2300,3000]ms, not synchronized absolute latency. Nominal100ms Hann +/-5Hz probe bands can include mechanical harmonics and room/processing effects; not calibrated actuator spectrum. CV=sample SD/mean over repeated raw steady RMS, not similarity to Sony. More amplitude is not automatically better. Same immutable source,100% source gain and100% output for both routes. Human blind preference remains separate.")
(root/"summary.json").write_text(json.dumps(summary,indent=2),encoding="utf-8")
print(json.dumps(dict(Cases=len(rows),Complete=len(rows)==60,MicClips=sum(r["MicClips"] for r in rows),
    AllSourcesUnchanged=all(r["SourceUnchanged"] for r in rows),AllWorkersStopped=all(r["WorkerStopped"] for r in rows),
    Groups=[{k:v for k,v in g.items() if k!="Bands"} for g in groups]),indent=2))
