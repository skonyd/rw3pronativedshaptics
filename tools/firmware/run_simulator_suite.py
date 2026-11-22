"""Run the current offline Wolverine firmware replays with one input set."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

from usb_audio_trace import inspect


def run_stage(name, script, arguments, output):
    print(f"Running {name}", file=sys.stderr, flush=True)
    command = [sys.executable, "-B", str(Path(__file__).with_name(script)), *map(str, arguments)]
    result = subprocess.run(command, capture_output=True, encoding="utf-8", check=False)
    try:
        report = json.loads(result.stdout) if result.returncode == 0 else None
    except json.JSONDecodeError:
        report = None
    if not report or not isinstance(report, (dict, list)):
        log = output / f"{name}.log"
        log.write_text(result.stderr + result.stdout, encoding="utf-8")
        return {"passed": False, "returncode": result.returncode, "log": log.name}
    path = output / f"{name}.json"
    path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return {"passed": True, "report": path.name}


def write_pcm_trace(capture, usb_address, path):
    report, blocks = inspect(capture, usb_address)
    if not 0 < len(blocks) <= 15000 or set(report["gip_audio_payload_sizes"]) != {192}:
        raise ValueError(f"Expected contiguous 192-byte GIP audio in {capture} at USB address {usb_address}")
    if any(current[1] != previous[1] % 255 + 1 for previous, current in zip(blocks, blocks[1:])):
        raise ValueError(f"GIP audio sequence gap in {capture} at USB address {usb_address}")
    path.write_text(json.dumps([{"urb_timestamp_us": timestamp, "sequence": sequence, "payload_hex": data.hex()}
                               for timestamp, sequence, data in blocks]), encoding="utf-8")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("images", type=Path, help="Directory containing locally extracted Dev2FW, Dev1FW and Flash2FW .bin files")
    parser.add_argument("dsp", type=Path, help="Compiled Cortex-M4 simulator module from build_audio_replay.ps1")
    parser.add_argument("wired_capture", type=Path, help="Wired USBPcap capture with verified stereo PCM16 at 48 kHz")
    parser.add_argument("dongle_capture", type=Path, help="HyperSpeed USBPcap capture in the same format")
    parser.add_argument("--wired-usb-address", type=int, default=1)
    parser.add_argument("--dongle-usb-address", type=int, default=9)
    parser.add_argument("--active-stereo-excerpt", type=int, metavar="START",
                        help="Replay 128 active stereo slots at this index of both captures into the NXP PWM model")
    parser.add_argument("--output", type=Path, default=Path("TestResults/firmware-lab/suite"))
    args = parser.parse_args()
    if not all(1 <= address <= 127 for address in (args.wired_usb_address, args.dongle_usb_address)):
        parser.error("USB addresses must be in 1–127")
    if args.active_stereo_excerpt is not None and args.active_stereo_excerpt < 0:
        parser.error("active stereo excerpt start must be nonnegative")

    nxp, dongle, radio = (args.images / f"{stem}.bin" for stem in ("Dev2FW", "Dev1FW", "Flash2FW"))
    inputs = {"nxp_image": nxp, "dongle_image": dongle, "controller_radio_image": radio, "dsp": args.dsp,
              "wired_capture": args.wired_capture, "dongle_capture": args.dongle_capture}
    for path in inputs.values():
        if not path.is_file() or path.stat().st_size == 0:
            parser.error(f"Missing or empty replay input: {path}")

    output = args.output
    output.mkdir(parents=True, exist_ok=True)
    summary_path = output / "summary.json"
    wired_pcm = output / "wired-pcm.json"
    dongle_pcm = output / "dongle-pcm.json"
    config = ("--usb-address", args.dongle_usb_address)
    capture = ("--dongle-usb-trace", args.dongle_capture, *config)
    excerpt = () if args.active_stereo_excerpt is None else ("--active-stereo-excerpt", args.active_stereo_excerpt)
    publication_captures = () if args.active_stereo_excerpt is None else (
        "--wired-pcm-trace", wired_pcm, "--dongle-pcm-trace", dongle_pcm,
        "--captured-excerpt-start", args.active_stereo_excerpt)
    stages = (
        ("telink-startup", "replay_telink_startup.py", (dongle, radio, *capture)),
        ("dongle-usb", "replay_telink_usb.py", (dongle, args.dongle_capture, *config)),
        ("telink-radio-audio", "replay_telink_radio.py", (radio,)),
        ("telink-dongle-to-radio", "replay_telink_link.py", (dongle, radio, *capture)),
        ("telink-duplex-retry", "replay_telink_duplex.py", (dongle, radio)),
        ("voice-usb-capture", "usb_voice_trace.py", (args.dongle_capture, args.dongle_usb_address)),
        ("telink-voice-uplink", "replay_telink_uplink.py", (radio, dongle, *capture)),
        ("controller-spi", "replay_controller_spi.py", (radio, nxp)),
        ("controller-i2s", "replay_controller_i2s.py", (radio, nxp)),
        ("nxp-analog", "replay_nxp_analog.py", (nxp,)),
        ("nxp-startup-i2c", "replay_nxp_startup.py", (nxp, args.dsp)),
        ("nxp-exceptions", "replay_nxp_exceptions.py", (nxp, args.dsp)),
        ("nxp-fpu-exceptions", "replay_nxp_fpu.py", (nxp, args.dsp)),
        ("nxp-dma-preemption", "replay_nxp_dma_preemption.py", (nxp, args.dsp)),
        ("nxp-publication", "replay_nxp_publication.py", (nxp, args.dsp, *publication_captures)),
        ("nxp-clock", "replay_nxp_clock.py", (nxp, args.dsp, *publication_captures)),
        ("nxp-watchdog", "replay_nxp_watchdog.py", (nxp, args.dsp)),
        ("nxp-foreground-loop", "replay_nxp_loop.py", (nxp, args.dsp, *publication_captures)),
        ("nxp-wired-dsp", "simulate_audio_haptics.py", (nxp, args.dsp, "--verified-pcm-trace", wired_pcm, *excerpt)),
        ("nxp-dongle-dsp", "simulate_audio_haptics.py", (nxp, args.dsp, "--verified-pcm-trace", dongle_pcm, *excerpt)),
    )
    summary = {"scope": "offline stock and candidate replay with modeled peripherals",
               "passed": False, "complete": False, "active_stereo_excerpt": args.active_stereo_excerpt,
               "input_sha256": {name: hashlib.sha256(path.read_bytes()).hexdigest() for name, path in inputs.items()},
               "stages": {name: {"passed": None} for name, _, _ in stages}}

    def save_summary():
        temporary = summary_path.with_suffix(".tmp")
        temporary.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
        temporary.replace(summary_path)

    save_summary()
    try:
        summary["captures"] = {"wired": write_pcm_trace(args.wired_capture, args.wired_usb_address, wired_pcm),
                               "dongle": write_pcm_trace(args.dongle_capture, args.dongle_usb_address, dongle_pcm)}
    except ValueError as error:
        summary["input_error"] = str(error)
        save_summary()
        parser.error(str(error))
    save_summary()
    for name, script, arguments in stages:
        summary["stages"][name] = run_stage(name, script, arguments, output)
        save_summary()
    summary["passed"] = all(stage["passed"] for stage in summary["stages"].values())
    summary["complete"] = True
    save_summary()
    print(summary_path)
    return 0 if summary["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
