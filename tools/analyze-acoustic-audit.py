"""Summarize aggregate-only microphone reports; never declares physical fidelity."""
import argparse
import json
import math
from pathlib import Path
from statistics import mean, stdev


def db(value, baseline):
    return 20 * math.log10(value / baseline) if value > 0 and baseline > 0 else None


def section_rms(window, start, end):
    values = [b["BandRms"] for b in window["Bins"] if start < b["SampleTimeMs"] <= end]
    return math.sqrt(mean(v * v for v in values)) if values else None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    rows, failures = [], []
    for folder in ["controls-unprocessed", "sweep-unprocessed", "repeat-unprocessed", "extra-unprocessed"]:
        for path in sorted((args.directory / folder).glob("audit-*.json")):
            report = json.loads(path.read_text(encoding="utf-8-sig"))
            if "DrivenAndTail" not in report:
                failures.append({"File": str(path), "Failure": report.get("Failure")})
                continue
            scene = report["Scene"]
            baseline = max(report["Background"]["BandRms"], report["EngineIdle"]["BandRms"])
            drive = section_rms(report["DrivenAndTail"], 350, min(200 + scene["DriveMs"] - 100, report["CommandElapsedMs"] - 100))
            late = section_rms(report["DrivenAndTail"], report["CommandElapsedMs"] + 400, math.inf)
            rows.append({"Dataset": folder, "File": str(path), "Scene": scene, "Route": report["Route"],
                "Repeat": report["Repeat"], "SourceSha256": report["OriginalPcmSha256"],
                "BackgroundRms": baseline, "DriveRms": drive, "LateTailRms": late,
                "DriveOverBackgroundDb": db(drive, baseline) if drive is not None else None,
                "LateTailOverBackgroundDb": db(late, baseline) if late is not None else None,
                "PostStopOverBackgroundDb": db(report["EngineOffAfter"]["BandRms"], baseline),
                "AudioCallbacks": report["AudioCallbacks"], "AudioInputSamples": report["AudioInputSamples"],
                "ClippedAudioSamples": report["ClippedAudioSamples"],
                "MicrophoneClippedSamples": sum(report[w]["Clipped"] for w in
                    ["Background", "EngineIdle", "DrivenAndTail", "EngineOffAfter"]),
                "EngineHz": report["EngineHz"], "AudioFailure": report["AudioFailure"]})

    pairs = []
    sources = {tuple((r["Scene"][k] for k in ["FrequencyHz", "Amplitude", "Right", "DriveMs"]))
               for r in rows if r["Dataset"] == "sweep-unprocessed"}
    for key in sorted(sources):
        group = [r for r in rows if r["Dataset"] == "sweep-unprocessed" and
                 tuple(r["Scene"][k] for k in ["FrequencyHz", "Amplitude", "Right", "DriveMs"]) == key]
        if len(group) != 2:
            continue
        ref = next(r for r in group if r["Route"] == "reference")
        audio = next(r for r in group if r["Route"] == "audio")
        adequate = all(r["DriveOverBackgroundDb"] is not None and r["DriveOverBackgroundDb"] >= 6 for r in group)
        pairs.append({"FrequencyHz": key[0], "Amplitude": key[1], "Right": key[2],
            "SourceHashesEqual": ref["SourceSha256"] == audio["SourceSha256"],
            "ReferenceOverBackgroundDb": ref["DriveOverBackgroundDb"],
            "AudioOverBackgroundDb": audio["DriveOverBackgroundDb"],
            "BothAboveBackground6Db": adequate,
            "AudioOverReferenceDb": db(audio["DriveRms"], ref["DriveRms"]) if adequate else None})

    repeats = []
    for hz, level in [(50, .5), (80, .2), (80, .5), (190, .5)]:
        for route in ["reference", "audio"]:
            values = [r["DriveRms"] for r in rows if r["Dataset"] == "repeat-unprocessed"
                      and r["Scene"]["FrequencyHz"] == hz and r["Scene"]["Amplitude"] == level and r["Route"] == route]
            if values:
                repeats.append({"Hz": hz, "Level": level, "Route": route, "N": len(values), "MeanDriveRms": mean(values),
                    "CoefficientOfVariation": stdev(values) / mean(values) if len(values) > 1 and mean(values) > 0 else None})

    summary = {"PhysicalFidelityVerified": False, "Rows": len(rows), "Failures": failures,
        "DatasetCounts": {name: sum(r["Dataset"] == name for r in rows) for name in
            ["controls-unprocessed", "sweep-unprocessed", "repeat-unprocessed", "extra-unprocessed"]},
        "Pairs": pairs, "Repeats": repeats, "Measurements": rows,
        "Limits": "Uncalibrated one-mic sound levels, not displacement/fidelity/side-isolation or absolute latency. "
                  "Reference is synthetic Native-extractor-to-Wolverine, not physical DualSense. "
                  "Drive bins 350ms through source drive end minus 100ms; late tail begins command completion +400ms. "
                  "Sample-clock bins are not synchronized with command onset. 6dB is a descriptive detectability screen, not a fidelity tolerance. "
                  "Ratios are raw microphone RMS ratios; background energy is not normalized away."}
    (args.directory / "measurement-summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    fmt = lambda v: "unresolved" if v is None else f"{v:+.2f}"
    lines = ["# Relative microphone measurements", "", summary["Limits"], "",
        "Completed cases: " + json.dumps(summary["DatasetCounts"]), "",
        "| Hz | Level | Side | Reference/background dB | Audio/background dB | Audio/reference dB |",
        "| --- | --- | --- | --- | --- | --- |"]
    for p in pairs:
        lines.append(f'| {p["FrequencyHz"]} | {p["Amplitude"]} | {"right" if p["Right"] else "left"} | '
            f'{fmt(p["ReferenceOverBackgroundDb"])} | {fmt(p["AudioOverBackgroundDb"])} | {fmt(p["AudioOverReferenceDb"])} |')
    lines += ["", "Unresolved ratios have at least one path less than 6dB above its background; no equality is inferred.",
              "", "Repeat observations:", "", "```json", json.dumps(repeats, indent=2), "```", ""]
    (args.directory / "measurement-summary.md").write_text("\n".join(lines), encoding="utf-8")
    print(json.dumps({"Rows": len(rows), "Counts": summary["DatasetCounts"], "Pairs": len(pairs),
        "PairsAboveFloor": sum(p["BothAboveBackground6Db"] for p in pairs), "Failures": len(failures)}))


if __name__ == "__main__":
    main()
