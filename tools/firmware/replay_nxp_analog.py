"""Run the stock NXP startup and foreground ADC readers with modeled samples."""
import argparse
import hashlib
import json
from pathlib import Path
import struct

from unicorn import UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE

from simulate_audio_haptics import RETURN, STOCK_SHA256, Replay, verify


ADC = 0x400A0000
ADC_SEQUENCE_A = ADC + 8
ADC_START = 1 << 26
ADC_CHANNEL_DATA = ADC + 0x20
ADC_VALID = 1 << 31
STARTUP_SCAN, STARTUP_VALUES = 0x1E628, 0x200010D4
FOREGROUND_SCAN, FOREGROUND_AVERAGED = 0x197F8, 0x198AA
INPUT_STATE = 0x20000B74
SCAN_DUE, ADC_ENABLED = INPUT_STATE + 0x152, INPUT_STATE + 0x235
AVERAGED_VALUES, AVERAGE_READY = INPUT_STATE + 0x120, INPUT_STATE + 0x151
TRIGGER_VALUES = INPUT_STATE + 0x28
TRIGGER_CALIBRATION = (INPUT_STATE + 0x1F8, INPUT_STATE + 0x218)
TRIGGER_REPORT_MAXIMUM = 0x200056E4
GIP_INPUT_TRANSLATE, GIP_INPUT_BUILD = 0xC19C, 0x20F2C
INPUT_CHANGED, GIP_INPUT_READY, GIP_INPUT_BUSY = 0x20008AEF, 0x200134CC, 0x200134CA
GIP_INPUT_HEADER, GIP_INPUT_BUFFER, GIP_INPUT_PENDING = 0x200000F5, 0x20005514, 0x20005594
GIP_INPUT_LENGTH = 36
SCAN_ORDER = (5, 9, 3, 4, 6, 7)
INVERTED_CHANNELS = (3, 9)


def fixture_values(rounds):
    # Distinct baselines, a slow ramp, and two extreme startup outliers.
    return [{channel: 10 if index == 1 else 4080 if index == 2
             else 310 + channel * 215 + index * (channel + 2) for channel in SCAN_ORDER}
            for index in range(rounds)]


class AdcModel:
    def __init__(self, cpu, scans, invalid_first=None):
        self.scans = scans
        self.invalid_first = invalid_first
        self.reads = []
        self.round = -1
        self.consumed = set()
        self.retired_invalid = False
        for base in (0x40000000, 0x40001000, 0x4000C000, ADC):
            cpu.mem_map(base, 0x1000)
        cpu.hook_add(UC_HOOK_MEM_WRITE, self.start, begin=ADC_SEQUENCE_A, end=ADC_SEQUENCE_A)
        cpu.hook_add(UC_HOOK_MEM_READ, self.read,
                     begin=ADC_CHANNEL_DATA, end=ADC_CHANNEL_DATA + 11 * 4)

    def start(self, cpu, access, address, size, value, data):
        if value & ADC_START:
            self.round += 1
            self.consumed.clear()
            verify(self.round < len(self.scans), "Firmware requested too many ADC scans")

    def read(self, cpu, access, address, size, value, data):
        channel = (address - ADC_CHANNEL_DATA) // 4
        verify(self.round >= 0 and channel in self.scans[self.round], f"Unexpected ADC channel read: {channel}")
        sample = self.scans[self.round][channel]
        delayed = (self.round, channel) == self.invalid_first and not self.retired_invalid
        if delayed:
            self.retired_invalid = True
        valid = not delayed and channel not in self.consumed
        if valid:
            self.consumed.add(channel)
        word = (channel << 26) | (sample << 4) | (ADC_VALID if valid else 0)
        cpu.mem_write(address, struct.pack("<I", word))
        self.reads.append((self.round, channel, valid))


def expected_samples(scans, channel):
    values = [scan[channel] for scan in scans]
    return [4096 - value for value in values] if channel in INVERTED_CHANNELS else values


def read_values(cpu, address):
    return list(struct.unpack("<6I", cpu.mem_read(address, 24)))


def check_reads(model, rounds, invalid_first=None):
    expected = [(index, channel, True) for index in range(rounds) for channel in SCAN_ORDER]
    if invalid_first:
        index = expected.index((*invalid_first, True))
        expected.insert(index, (*invalid_first, False))
    verify(model.reads == expected and model.round == rounds - 1, "Unexpected stock scan order or retry behavior")


def replay_startup(image):
    replay = Replay(image)
    scans = fixture_values(18)
    model = AdcModel(replay.cpu, scans, invalid_first=(6, 3))
    replay.call(STARTUP_SCAN, RETURN, instruction_limit=500_000)
    check_reads(model, len(scans), model.invalid_first)
    values = read_values(replay.cpu, STARTUP_VALUES)
    expected = [sum(sorted(expected_samples(scans, channel))[1:-1]) // 16 for channel in SCAN_ORDER]
    verify(values == expected, f"Stock ADC startup expected {expected}, observed {values}")

    # The stock maximum update skips the minimum comparison in that round.
    # A strictly increasing sequence therefore leaves minimum at its seed.
    increasing = [1000 + index * 50 for index in range(18)]
    monotonic = [{channel: 4096 - value if channel in INVERTED_CHANNELS else value for channel in SCAN_ORDER}
                 for value in increasing]
    replay = Replay(image)
    model = AdcModel(replay.cpu, monotonic)
    replay.call(STARTUP_SCAN, RETURN, instruction_limit=500_000)
    check_reads(model, len(monotonic))
    observed = read_values(replay.cpu, STARTUP_VALUES)
    sentinel_average = (sum(increasing[:-1]) - 4095) // 16
    verify(observed == [sentinel_average] * len(SCAN_ORDER) and sentinel_average != sum(increasing[1:-1]) // 16,
            "Stock ADC startup minimum-sentinel behavior changed")
    return {"rounds": len(scans), "valid_reads": len(scans) * len(SCAN_ORDER), "invalid_reads_retried": 1,
            "trimmed_average": values, "monotonic_average_with_seeded_minimum": observed}


def replay_foreground(image):
    replay = Replay(image)
    cpu = replay.cpu
    scans = fixture_values(11)
    model = AdcModel(cpu, scans)
    cpu.mem_write(SCAN_DUE, b"\x01")
    replay.call(FOREGROUND_SCAN, RETURN)
    cpu.mem_write(SCAN_DUE, b"\0")
    cpu.mem_write(ADC_ENABLED, b"\x01")
    replay.call(FOREGROUND_SCAN, RETURN)
    verify(not model.reads and model.round == -1, "Stock foreground sampled without both enable and due state")

    reports = []
    for index in range(len(scans)):
        cpu.mem_write(SCAN_DUE, b"\x01")
        replay.call(FOREGROUND_SCAN, FOREGROUND_AVERAGED, instruction_limit=30_000)
        verify(cpu.mem_read(SCAN_DUE, 1) == b"\0", "Foreground did not retire its scan request")
        ready = cpu.mem_read(AVERAGE_READY, 1)[0]
        verify(ready == int(index >= 9), "Stock foreground published an incomplete moving average")
        if ready:
            expected = [sum(expected_samples(scans[index - 9:index + 1], channel)) // 10 for channel in SCAN_ORDER]
            observed = read_values(cpu, AVERAGED_VALUES)
            verify(observed == expected, "Stock foreground moving average used the wrong samples")
            reports.append(observed)
    check_reads(model, len(scans))
    return {"rounds": len(scans), "moving_average_samples": 10, "averages": reports,
            "disabled_or_not_due_reads": 0}


def replay_trigger_values(image):
    cases = (("released", 3000, 3000, (0, 0)), ("below_deadzone", 2951, 2951, (0, 0)),
             ("first_active_step", 2949, 3000, (1, 0)), ("first_side_partial", 2525, 3000, (458, 0)),
             ("second_side_partial", 3000, 2200, (0, 808)), ("both_saturated", 2000, 1700, (1023, 1023)))
    results = {}
    for name, first, second, expected in cases:
        replay = Replay(image)
        cpu = replay.cpu
        scans = [{**dict.fromkeys(SCAN_ORDER, 2048), 6: first, 7: second}] * 10
        if name == "both_saturated":
            scans += [{**dict.fromkeys(SCAN_ORDER, 2048), 6: 3000, 7: 3000}] * 10
        model = AdcModel(cpu, scans)
        cpu.mem_write(ADC_ENABLED, b"\x01")
        for address in TRIGGER_CALIBRATION:
            cpu.mem_write(address, struct.pack("<II", 3000, 1000))
        for _ in range(10):
            cpu.mem_write(SCAN_DUE, b"\x01")
            replay.call(FOREGROUND_SCAN, RETURN, instruction_limit=30_000)
        values = struct.unpack("<II", cpu.mem_read(TRIGGER_VALUES, 8))
        verify(values == expected, f"Unexpected stock analog trigger result for {name}: {values}")
        cpu.mem_write(TRIGGER_REPORT_MAXIMUM, struct.pack("<HH", 1023, 1023))
        cpu.mem_write(GIP_INPUT_HEADER, b"\x20\x00\x00")  # Initial header from the stock scatter data.
        cpu.mem_write(GIP_INPUT_READY, b"\x01")  # Negotiated USB state at the queue boundary.
        replay.call(GIP_INPUT_TRANSLATE, RETURN)
        before = bytes(cpu.mem_read(GIP_INPUT_BUFFER, GIP_INPUT_LENGTH))
        cpu.mem_write(GIP_INPUT_BUSY, b"\x01\x00")
        replay.call(GIP_INPUT_BUILD, RETURN)
        verify(cpu.mem_read(GIP_INPUT_PENDING, 1) == b"\0" and
                bytes(cpu.mem_read(GIP_INPUT_BUFFER, GIP_INPUT_LENGTH)) == before,
                "Stock report builder overwrote a busy report")
        cpu.mem_write(GIP_INPUT_BUSY, b"\x00\x00")
        replay.call(GIP_INPUT_BUILD, RETURN)
        report = {"adc_6_7": [first, second], "internal_trigger_values": list(values), "gip_queued_bytes": 0}
        pending = cpu.mem_read(GIP_INPUT_PENDING, 1)[0]
        if not any(expected):
            verify(cpu.mem_read(INPUT_CHANGED, 1) == b"\0" and pending == 0,
                    "No-change neutral fixture unexpectedly queued a GIP report")
        else:
            gip = bytes(cpu.mem_read(GIP_INPUT_BUFFER, pending))
            verify(pending == GIP_INPUT_LENGTH and gip[:4] == b"\x20\x00\x01\x20" and
                    struct.unpack_from("<HH", gip, 6) == expected,
                    f"Unexpected queued GIP trigger fields for {name}")
            report.update({"gip_queued_bytes": pending, "gip_trigger_bytes": gip[6:10].hex()})
        if name == "both_saturated":
            # Model consumption of the pending report, then feed the analog
            # release through the same firmware state and moving average.
            cpu.mem_write(INPUT_CHANGED, b"\0")
            cpu.mem_write(GIP_INPUT_PENDING, b"\0")
            for _ in range(10):
                cpu.mem_write(SCAN_DUE, b"\x01")
                replay.call(FOREGROUND_SCAN, RETURN, instruction_limit=30_000)
            replay.call(GIP_INPUT_TRANSLATE, RETURN)
            replay.call(GIP_INPUT_BUILD, RETURN)
            verify(cpu.mem_read(GIP_INPUT_PENDING, 1) == bytes([GIP_INPUT_LENGTH]) and
                    cpu.mem_read(GIP_INPUT_BUFFER + 6, 4) == bytes(4),
                    "Stock GIP report did not clear the previously active triggers")
            report["gip_release_trigger_bytes"] = "00000000"
        check_reads(model, len(scans))
        results[name] = report
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("controller_nxp_fw", type=Path)
    args = parser.parse_args()
    image = args.controller_nxp_fw.read_bytes()
    verify(hashlib.sha256(image).hexdigest() == STOCK_SHA256, "Unexpected NXP stock image")
    print(json.dumps({"adc_channel_order": SCAN_ORDER, "inverted_channels": INVERTED_CHANNELS,
                      "startup": replay_startup(image), "foreground": replay_foreground(image),
                      "trigger_scaling": replay_trigger_values(image)}, indent=2))


if __name__ == "__main__":
    main()
