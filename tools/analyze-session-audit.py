"""Keep all repeat cases and describe variation; never certify physical equivalence."""
import json
import math
from pathlib import Path
from statistics import mean, stdev

root = Path("TestResults/scenario-audit-20261108")
rows = []
for dataset in ["session", "final-cold-check", "unity-hardware"]:
    for path in sorted((root / dataset).glob("audit-*.json")):
        d = json.loads(path.read_text(encoding="utf-8-sig"))
        bins = [b["BandRms"] for b in d["DrivenAndTail"]["Bins"] if 350 < b["SampleTimeMs"] <= 1100]
        rms = math.sqrt(mean(v*v for v in bins))
        floor = max(d["Background"]["BandRms"], d["EngineIdle"]["BandRms"])
        rows.append(dict(Dataset=dataset, File=path.name, Scene=d["Scene"]["Name"],
            Level=d["Scene"]["Amplitude"], Route=d["Route"], Repeat=d["Repeat"],
            EngineKept=d["EngineKeptAlive"], MicKept=d["MicrophoneKeptOpen"],
            SteadyRms=rms, AboveBackgroundDb=20*math.log10(rms/floor),
            SourceHash=d["OriginalPcmSha256"], AudioInputGain=d.get("AudioInputGain", 2.2),
            AudioClips=d["ClippedAudioSamples"], OutputSaturatedPoints=d["SoftwareFrames"]["SaturatedEnvelopePoints"],
            MicClips=sum(d[k]["Clipped"] for k in ["Background", "EngineIdle", "DrivenAndTail", "EngineOffAfter"]),
            AudioFailure=d["AudioFailure"]))
groups = []
keys = sorted({(r["Dataset"], r["EngineKept"], r["MicKept"], r["Level"], r["Route"]) for r in rows})
for dataset, engine, mic, level, route in keys:
    selected = [r for r in rows if (r["Dataset"], r["EngineKept"], r["MicKept"], r["Level"], r["Route"]) == (dataset, engine, mic, level, route)]
    values = [r["SteadyRms"] for r in selected]
    groups.append(dict(Dataset=dataset, EngineKept=engine, MicKept=mic, Level=level, Route=route,
        N=len(values), Mean=mean(values), CV=stdev(values)/mean(values),
        MinimumAboveBackgroundDb=min(r["AboveBackgroundDb"] for r in selected),
        AudioClips=sum(r["AudioClips"] for r in selected),
        OutputSaturatedPoints=sum(r["OutputSaturatedPoints"] for r in selected)))
summary = dict(PhysicalFidelityVerified=False, CompletedCases=len(rows), Groups=groups, Cases=rows,
    Limits="All completed cases retained; no outliers removed. Steady microphone sample-clock bins (350,1100]ms. Raw RMS, no background normalization. CV=sample SD/mean, not error against DualSense. Fixed group order confounds warm-up/time with session reuse; final cold check repeats the initial condition. Single 80Hz left scene is not general frequency/stereo acceptance. Original gain 2.2 equalized in session/final; unity-hardware uses gain 1 and no source pre-attenuation. Output saturation is parametric command ceiling, distinct from input clipping.")
(root / "repeat-summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
print(json.dumps(dict(CompletedCases=len(rows), Groups=groups), indent=2))
