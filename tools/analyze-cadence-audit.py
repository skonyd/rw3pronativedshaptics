"""Descriptive one-microphone tail observations, not a physical latency measurement."""
import json,math
from pathlib import Path
from statistics import mean

root=Path("TestResults/multitone-20261108/cadence")
rows=[]
def rms(window,start,end):
    values=[b["BandRms"] for b in window["Bins"] if start<b["SampleTimeMs"]<=end]
    return math.sqrt(mean(v*v for v in values)) if values else None
def db(x,baseline):return 20*math.log10(x/baseline) if x and baseline else None
for path in sorted(root.glob("audit-*.json")):
    d=json.loads(path.read_text(encoding="utf-8-sig"));baseline=max(d["Background"]["BandRms"],d["EngineIdle"]["BandRms"])
    w=d["DrivenAndTail"]
    rows.append(dict(File=path.name,TickMs=d["RequestedEngineTickMs"],Multiband=d["ExperimentalMultiband"],Repeat=d["Repeat"],
        DriveOverBackgroundDb=db(rms(w,600,2800),baseline),Tail3200to3400Db=db(rms(w,3200,3400),baseline),
        Tail3400to3700Db=db(rms(w,3400,3700),baseline),Tail3700to4300Db=db(rms(w,3700,4300),baseline),
        LateTailDb=db(rms(w,4300,5300),baseline),OffDb=db(d["EngineOffAfter"]["BandRms"],baseline),
        MicClips=sum(d[k]["Clipped"] for k in ["Background","EngineIdle","DrivenAndTail","EngineOffAfter"]),
        EngineHz=d["EngineHz"],RenderTicks=d["RenderTicks"],SourceHash=d["OriginalPcmSha256"]))
summary=dict(PhysicalLatencyMeasured=False,Cases=rows,Limit="All12 reports retained. Same80Hz/.35/3s source; source drive expected200..3200ms on its own clock. Microphone sample-clock bins are not synchronized. Integrated acoustic ratios do not prove input-to-motor latency, provider queue age or actual33ms quantization. Processing/room coupling remain uncontrolled. Last EngineHz includes idle scheduling and is not active motor cadence. Two repeats per condition are insufficient to choose a faster production rate.")
(root/"summary.json").write_text(json.dumps(summary,indent=2),encoding="utf-8")
for r in rows:print(r["TickMs"],r["Multiband"],r["Repeat"],*[round(r[k],2) for k in ["DriveOverBackgroundDb","Tail3200to3400Db","Tail3400to3700Db","Tail3700to4300Db","LateTailDb"]])
