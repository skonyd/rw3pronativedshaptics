"""Execute the two pinned Telink B91 startup loaders offline."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import zlib

from unicorn import Uc, UC_ARCH_RISCV, UC_HOOK_CODE, UC_HOOK_MEM_WRITE, UC_MODE_RISCV32, UC_PROT_ALL
from unicorn.riscv_const import (
    UC_RISCV_REG_A0, UC_RISCV_REG_A1, UC_RISCV_REG_A2, UC_RISCV_REG_A3, UC_RISCV_REG_GP,
    UC_RISCV_REG_PC, UC_RISCV_REG_SP, UC_RISCV_REG_T0, UC_RISCV_REG_X0,
)

from usb_audio_trace import inspect


XIP = 0x20009000
PAYLOAD_OFFSET = 0x1000
# (file offset or None for zero-fill, destination start, exclusive end).
# These ranges are independently checked against writes by the stock loops.
LAYOUTS = {
    "dongle": (0x2F55E8C3, 0x168, 0x20644, 0xC0000, (
        (0x1178, 0, 0x184), (0x1300, 0x2A8, 0x7A0),
        (0x17F8, 0x800, 0x29724), (0x2A720, 0x80000, 0x87F74),
        (None, 0x87F78, 0x9FFC0), (None, 0x188, 0x2A8))),
    "radio": (0x34244D8B, 0x12C, 0x9CDC, 0xA0000, (
        (0x1140, 0, 0xEC), (0x1230, 0x200, 0x508), (0x1538, 0x508, 0x1345C),
        (0x249D8, 0x80000, 0x853F8), (None, 0x853F8, 0x95AC8))),
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def field(value, offset, width):
    return (value >> offset) & ((1 << width) - 1)


def execute_addigp(cpu, opcode):
    require(opcode & 0x307F == 0x100B, "Expected an Andes ADDIGP instruction")
    offset = (field(opcode, 14, 1) | field(opcode, 21, 10) << 1 | field(opcode, 20, 1) << 11 |
              field(opcode, 17, 3) << 12 | field(opcode, 15, 2) << 15) - (field(opcode, 31, 1) << 17)
    cpu.reg_write(UC_RISCV_REG_X0 + field(opcode, 7, 5),
                  (cpu.reg_read(UC_RISCV_REG_GP) + offset) & 0xFFFFFFFF)


def replay_startup(image, role):
    crc, jump_offset, app_entry, stack, regions = LAYOUTS[role]
    payload = image[PAYLOAD_OFFSET:]
    require(len(image) > PAYLOAD_OFFSET, f"{role}: missing payload")
    entry, size, checksum, mirror_a, mirror_b = struct.unpack_from("<5I", image)
    require(entry == mirror_a == mirror_b == XIP and size == len(payload) and
            checksum == crc == zlib.crc32(payload) ^ 0xFFFFFFFF, f"{role}: unexpected B91 header or CRC")
    cpu = Uc(UC_ARCH_RISCV, UC_MODE_RISCV32)
    for address, length in ((0, 0x30000), (0x80000, 0x40000), (XIP, 0x40000),
                            (0x80100000, 0x1000), (0xE4000000, 0x1000)):
        cpu.mem_map(address, length, UC_PROT_ALL)
    cpu.mem_write(XIP, payload)
    for address, length in ((0, 0x30000), (0x80000, 0x40000)):
        cpu.mem_write(address, b"\xA5" * length)
    writes = []
    cpu.hook_add(UC_HOOK_MEM_WRITE, lambda _, access, address, size, value, data:
                 writes.append((address, size)), begin=0, end=0xBFFFF)

    jump = entry + jump_offset
    vendor_csrs = {}
    extensions = []

    def vendor_instruction(machine, address, width, data):
        if width != 4:
            return
        opcode, = struct.unpack("<I", machine.mem_read(address, width))
        if (opcode & 0x307F == 0x100B or
                opcode & 0x7F == 0x73 and opcode >> 20 in (0x800, 0x7D0, 0x7CA)):
            machine.emu_stop()

    cpu.hook_add(UC_HOOK_CODE, vendor_instruction, begin=entry, end=jump)
    pc = entry
    for _ in range(16):
        cpu.emu_start(pc, jump, count=500_000)
        pc = cpu.reg_read(UC_RISCV_REG_PC)
        if pc == jump:
            break
        opcode, = struct.unpack("<I", cpu.mem_read(pc, 4))
        dest = field(opcode, 7, 5)
        csr = opcode >> 20
        if opcode & 0x307F == 0x100B:
            execute_addigp(cpu, opcode)
            extensions.append({"pc": hex(pc), "instruction": "nds.addigp"})
        elif opcode & 0x7F == 0x73 and csr in (0x800, 0x7D0, 0x7CA):
            # Record these platform CSRs without attempting their CPU side effects.
            previous = vendor_csrs.get(csr, 0)
            source = field(opcode, 15, 5)
            operation = field(opcode, 12, 3)
            if dest:
                cpu.reg_write(UC_RISCV_REG_X0 + dest, previous)
            if operation == 1:
                vendor_csrs[csr] = cpu.reg_read(UC_RISCV_REG_X0 + source)
            elif operation == 6:
                vendor_csrs[csr] = previous | source
            else:
                require(operation == 2 and source == 0, f"Unsupported CSR operation at 0x{pc:X}")
            extensions.append({"pc": hex(pc), "instruction": "vendor CSR", "csr": hex(csr)})
        else:
            raise ValueError(f"{role}: startup stalled at 0x{pc:X}")
        pc += 4
    require(pc == jump and cpu.reg_read(UC_RISCV_REG_T0) == app_entry, f"{role}: missing app handoff")
    cpu.emu_start(jump, app_entry, count=8)
    require(cpu.reg_read(UC_RISCV_REG_PC) == app_entry and cpu.reg_read(UC_RISCV_REG_SP) == stack
            and cpu.reg_read(UC_RISCV_REG_GP) == 0x80800, f"{role}: unexpected app entry registers")
    expected_writes = [(address, 4) for _, start, end in regions for address in range(start, end, 4)]
    require(writes == expected_writes, f"{role}: unexpected relocation writes")
    for offset, start, end in regions:
        actual = bytes(cpu.mem_read(start, end - start))
        expected = bytes(end - start) if offset is None else image[offset:offset + end - start]
        require(actual == expected, f"{role}: relocation bytes differ at 0x{start:X}")

    report = {"role": role, "sha256": hashlib.sha256(image).hexdigest(), "app_entry": hex(app_entry),
              "stack": hex(stack), "global_pointer": "0x80800", "vendor_steps": extensions,
              "vendor_csrs": {hex(key): hex(value) for key, value in vendor_csrs.items()},
              "relocations": [{"file_offset": None if offset is None else hex(offset),
                               "start": hex(start), "bytes": end - start} for offset, start, end in regions]}
    if role == "dongle":
        report["xbox_usb"] = xbox_descriptors(cpu)
    return report


def xbox_descriptors(cpu):
    dev_at, cfg_at = 0x864C8, 0x80298
    # The branch arm for the 64-byte configuration prepares these
    # pointers immediately before calling its USB configuration helper.
    cpu.emu_start(0x1F1C0, 0x1F1C8, count=8)
    require(cpu.reg_read(UC_RISCV_REG_PC) == 0x1F1C8, "Missing Xbox USB descriptor setup")
    for address in (0x1F1C8, 0x1F1CC):
        execute_addigp(cpu, struct.unpack("<I", cpu.mem_read(address, 4))[0])
    registers = (UC_RISCV_REG_A0, UC_RISCV_REG_A1, UC_RISCV_REG_A2, UC_RISCV_REG_A3)
    require(tuple(cpu.reg_read(register) for register in registers) == (dev_at, cfg_at, 64, 0),
            "Unexpected Xbox USB descriptor call arguments")
    dev = bytes(cpu.mem_read(dev_at, 18))
    require(dev[:4] == bytes.fromhex("12010002"), "Missing relocated USB device descriptor")
    vid, pid = struct.unpack_from("<HH", dev, 8)
    require((vid, pid) == (0x1532, 0x0A3F), "Unexpected relocated product identity")
    cfg = bytes(cpu.mem_read(cfg_at, 64))
    require(len(cfg) == int.from_bytes(cfg[2:4], "little") and cfg[:2] == b"\x09\x02",
            "Missing relocated Xbox USB configuration")
    endpoints = []
    pos = 0
    while pos < len(cfg):
        length, kind = cfg[pos:pos + 2]
        require(2 <= length <= len(cfg) - pos, "Invalid USB descriptor length")
        if kind == 5:
            require(length >= 7, "Truncated endpoint descriptor")
            address, attributes, mtu = struct.unpack_from("<BBH", cfg, pos + 2)
            endpoints.append((address, attributes & 3, mtu))
        pos += length
    require(endpoints == [(5, 3, 64), (0x84, 3, 64), (6, 1, 228), (0x87, 1, 64)],
            "Unexpected Xbox USB endpoints")
    return {"setup_site": "0x1f1c0", "device_address_in_dlm": hex(dev_at), "configuration_address_in_dlm": hex(cfg_at),
            "vid_pid": f"{vid:04x}:{pid:04x}",
            "endpoints": [{"address": hex(address), "usb_transfer_type": kind, "max_packet_bytes": mtu}
                          for address, kind, mtu in endpoints]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dongle_fw", type=Path)
    parser.add_argument("radio_fw", type=Path)
    parser.add_argument("--dongle-usb-trace", type=Path)
    parser.add_argument("--usb-address", type=int, default=9)
    args = parser.parse_args()
    report = [replay_startup(path.read_bytes(), role)
              for role, path in (("dongle", args.dongle_fw), ("radio", args.radio_fw))]
    if args.dongle_usb_trace:
        capture, blocks = inspect(args.dongle_usb_trace, args.usb_address)
        iso_out = {endpoint for endpoint in capture["endpoints"]
                   if endpoint.endswith("/0") and int(endpoint.split("/")[0], 16) < 0x80}
        require(blocks and set(capture["gip_audio_payload_sizes"]) == {192}
                and iso_out == {"0x06/0"} and "0x87/0" in capture["endpoints"],
                "Capture does not match the audio endpoints in the relocated descriptor")
        require(all(right[1] == left[1] % 255 + 1 for left, right in zip(blocks, blocks[1:])),
                "USB GIP audio sequence gap")
        report[0]["host_endpoint_comparison"] = capture
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
