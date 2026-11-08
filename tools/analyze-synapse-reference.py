"""Aggregate-only Synapse/RW3 comparison. Does not turn acoustic similarity into haptic fidelity."""
import argparse
import csv
import json
import math
import statistics
from pathlib import Path


def db(numerator, denominator):
    return 20 * math.log10(max(numerator, 1e-12) / max(denominator, 1e-12))


def window_rms(window, start, end):
    bins = [b["BandRms"] for b in window.get("Bins", []) if start <= b["SampleTimeMs"] <= end]
    return math.sqrt(statistics.mean(v * v for v in bins)) if bins else None


def load(directory):
    directory = Path(directory)
    setup = json.loads((directory / "setup.json").read_text(encoding="utf-8-sig"))
    completion_file = directory / "completion.json"
    completion = json.loads(completion_file.read_text(encoding="utf-8-sig")) if completion_file.exists() else {"Completed": False}
    cases = []
    for file in sorted(directory.glob("[0-9]*.json")):
        item = json.loads(file.read_text(encoding="utf-8-sig"))
        scene = item["Scene"]
        active = window_rms(item["Driven"], 300, scene["DriveMs"] + 100)
        tail = window_rms(item["Driven"], scene["DriveMs"] + 600, scene["DriveMs"] + 1000)
        background = max(item["Background"]["BandRms"], item["After"]["BandRms"])
        clips = sum(item[key]["Clipped"] for key in ("Background", "Driven", "After"))
        exposure = item["Driven"]["BandRms"]
        valid = active is not None and tail is not None and clips == 0 and all(
            item[key]["Samples"] > 0 and item[key].get("TruncatedBins", 0) == 0
            for key in ("Background", "Driven", "After"))
        cases.append({"scene": scene["Id"], "pattern": scene["Pattern"], "repeat": item["Repeat"],
                      "source_hash": item["SourceSha256"], "playback_hash": item["PlaybackSha256"],
                      "active": active, "background": background, "tail": tail,
                      "exposure": exposure, "exposure_snr_db": db(exposure, background),
                      "active_snr_db": db(active, background) if active is not None else None,
                      "tail_snr_db": db(tail, background) if tail is not None else None,
                      "background_drift_db": abs(db(item["Background"]["BandRms"], item["After"]["BandRms"])),
                      "mic_clips": clips, "valid_acquisition": valid})
    groups = {}
    for item in cases:
        groups.setdefault(item["scene"], []).append(item)
    summaries = {}
    for name, values in groups.items():
        active = [v["active"] for v in values if v["active"] is not None]
        mean = statistics.mean(active) if active else 0
        exposures = [v["exposure"] for v in values]
        exposure_mean = statistics.mean(exposures)
        summaries[name] = {"repeats": len(values), "pattern": values[0]["pattern"],
                           "mean_active": mean, "min_active": min(active, default=0), "max_active": max(active, default=0),
                           "cv": statistics.stdev(active) / mean if len(active) > 1 and mean > 0 else None,
                           "median_snr_db": statistics.median(v["active_snr_db"] for v in values if v["active_snr_db"] is not None),
                           "max_tail_snr_db": max(v["tail_snr_db"] for v in values if v["tail_snr_db"] is not None),
                           "max_background_drift_db": max(v["background_drift_db"] for v in values),
                           "clear_all_repeats": len(values) >= 3 and all(v["valid_acquisition"] and v["active_snr_db"] >= 6 for v in values),
                           "mean_exposure": exposure_mean,
                           "exposure_cv": statistics.stdev(exposures) / exposure_mean if len(exposures) > 1 and exposure_mean > 0 else None,
                           "median_exposure_snr_db": statistics.median(v["exposure_snr_db"] for v in values),
                           "exposure_clear_all_repeats": len(values) >= 3 and all(v["valid_acquisition"] and v["exposure_snr_db"] >= 6 for v in values),
                           "source_hashes": sorted({v["source_hash"] for v in values}),
                           "playback_hashes": sorted({v["playback_hash"] for v in values})}
    return setup, completion, cases, summaries


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", required=True)
    parser.add_argument("--candidate")
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    setup, completion, cases, scenes = load(args.reference)
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    reference = {"setup": setup, "completion": completion, "cases": len(cases),
                 "valid_acquisitions": sum(c["valid_acquisition"] for c in cases),
                 "scenes": scenes, "physical_fidelity_verified": False}
    (out / "reference.json").write_text(json.dumps(reference, indent=2, allow_nan=False), encoding="utf-8")
    rows = []
    mismatches = []
    if args.candidate:
        candidate_setup, candidate_completion, candidate_cases, candidate_scenes = load(args.candidate)
        for key in ("SourceEndpoint", "PlaybackRate", "PlaybackVolume", "PlaybackMute", "Microphone", "MicVolume", "MicMute"):
            if setup.get(key) != candidate_setup.get(key):
                mismatches.append(key)
        if not completion.get("Completed") or not candidate_completion.get("Completed"):
            mismatches.append("incomplete_run")
        for name, ref in scenes.items():
            if name not in candidate_scenes:
                rows.append({"scene": name, "classification": "missing_candidate"})
                continue
            other = candidate_scenes[name]
            identical = ref["source_hashes"] == other["source_hashes"] and ref["playback_hashes"] == other["playback_hashes"]
            level_delta = db(other["mean_active"], ref["mean_active"])
            # Relative acoustic level only, with a disclosed 3dB margin around observed reference repeats.
            low = ref["min_active"] / 10 ** (3 / 20)
            high = ref["max_active"] * 10 ** (3 / 20)
            if mismatches or not identical:
                classification = "setup_or_source_mismatch"
            elif ref["pattern"] == "silence":
                classification = "silence_control"
            elif not ref["clear_all_repeats"]:
                classification = "reference_unresolved"
            elif not other["clear_all_repeats"]:
                classification = "candidate_unresolved"
            elif other["mean_active"] < low:
                classification = "lower_acoustic_level"
            elif other["mean_active"] > high:
                classification = "higher_acoustic_level"
            else:
                classification = "inside_descriptive_reference_level_band"
            rows.append({"scene": name, "classification": classification, "level_delta_db": level_delta,
                         "reference_cv": ref["cv"], "candidate_cv": other["cv"],
                         "reference_snr_db": ref["median_snr_db"], "candidate_snr_db": other["median_snr_db"],
                         "reference_tail_db": ref["max_tail_snr_db"], "candidate_tail_db": other["max_tail_snr_db"],
                         "exposure_level_delta_db": db(other["mean_exposure"], ref["mean_exposure"]),
                         "reference_exposure_snr_db": ref["median_exposure_snr_db"], "candidate_exposure_snr_db": other["median_exposure_snr_db"],
                         "reference_exposure_cv": ref["exposure_cv"], "candidate_exposure_cv": other["exposure_cv"],
                         "source_and_playback_identical": identical})
        counts = {key: sum(r["classification"] == key for r in rows) for key in sorted({r["classification"] for r in rows})}
        result = {"counts": counts, "setup_mismatches": mismatches, "rows": rows,
                  "physical_fidelity_verified": False,
                  "limit": "No better/worse score: louder is not more faithful. Single-mic physical side isolation and calibrated latency are unavailable. Screen is 6dB above max before/after background in all three repeats; level band is observed reference min/max plus 3dB, not a haptic acceptance tolerance."}
        (out / "comparison.json").write_text(json.dumps(result, indent=2, allow_nan=False), encoding="utf-8")
        with (out / "comparison.csv").open("w", newline="", encoding="utf-8-sig") as file:
            writer = csv.DictWriter(file, fieldnames=sorted({key for row in rows for key in row}))
            writer.writeheader()
            writer.writerows(rows)
        print(json.dumps({"counts": counts, "setup_mismatches": mismatches}))
    else:
        print(json.dumps({"cases": len(cases), "completed": completion.get("Completed"),
                          "clear_active_scenes_all_repeats": sum(s["pattern"] != "silence" and s["clear_all_repeats"] for s in scenes.values()),
                          "clear_exposure_scenes_all_repeats": sum(s["pattern"] != "silence" and s["exposure_clear_all_repeats"] for s in scenes.values()),
                          "total_scenes": len(scenes), "median_scene_cv": statistics.median(s["cv"] for s in scenes.values() if s["cv"] is not None)}))


if __name__ == "__main__":
    main()
