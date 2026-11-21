"""Replay stock board-startup I2C transactions with explicit synthetic peers."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import struct

from unicorn import UC_HOOK_CODE, UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE, UC_PROT_EXEC, UC_PROT_READ
from unicorn.arm_const import UC_ARM_REG_LR, UC_ARM_REG_PC, UC_ARM_REG_PRIMASK, UC_ARM_REG_R0, UC_ARM_REG_SP

from simulate_audio_haptics import (
    BOOT_HANDOFF, CANDIDATE_BOOT, CTIMER2, CTIMER3, MAIN_DISPATCH_CALL, FOREGROUND,
    LEVELS, TAP_BUFFER, Replay, RETURN, STATE, STOCK_SHA256, install_simulated_tx_tap,
    model_async_syscon_reset, model_syscon_reset, redirect_simulated_branch, verify,
)


CONFIG_DONE = 0x2DC5C
SECOND_READ_RETURN = 0xBD82
BUS_SPECS = ((0x40089800, 17), (0x40099800, 40))
WAIT_FLAGS = {0x2B428: 0x200093F1, 0x2B460: 0x200093F1,
              0x1336C: 0x200093F1, 0x2A610: 0x200093F0, 0x2A690: 0x200093F0}
EXPECTED_CONFIG = {0: 4, 1: 14, 2: 13, 3: 65, 4: 112, 5: 132, 6: 32, 7: 203, 12: 133}


class InterruptEnableModel:
    """Model NVIC enable aliases only; vector calls are explicitly scheduled."""
    def __init__(self, cpu):
        self.enabled = 0
        cpu.hook_add(UC_HOOK_MEM_WRITE, self.write, begin=0xE000E100, end=0xE000E19F)
        cpu.hook_add(UC_HOOK_MEM_READ, self.read, begin=0xE000E100, end=0xE000E19F)

    def write(self, cpu, access, address, size, value, data):
        offset = address - 0xE000E100
        if offset < 32 or 128 <= offset < 160:
            verify(size == 4 and offset % 4 == 0, "Unexpected NVIC enable access")
            mask = value << ((offset & 31) * 8)
            self.enabled = self.enabled & ~mask if offset >= 128 else self.enabled | mask

    def read(self, cpu, access, address, size, value, data):
        offset = address - 0xE000E100
        if offset < 32 or 128 <= offset < 160:
            verify(size == 4 and offset % 4 == 0, "Unexpected NVIC enable read")
            cpu.mem_write(address, ((self.enabled >> ((offset & 31) * 8)) & 0xFFFFFFFF).to_bytes(size, "little"))

    def allows(self, cpu, irq):
        return bool(self.enabled & (1 << irq)) and cpu.reg_read(UC_ARM_REG_PRIMASK) == 0


class I2CMasterModel:
    """START/CONTINUE/STOP register peer, not an electrical or timed bus."""
    def __init__(self, cpu, base, irq, devices):
        self.base, self.irq, self.devices = base, irq, devices
        self.status, self.enabled, self.configuration, self.data = 1, 0, 0, 0
        self.address, self.receiving, self.tx_count = None, False, 0
        self.pointer, self.registers = {}, {}
        self.commands, self.transmitted, self.received = [], [], []
        self.unknown_addresses = []
        cpu.hook_add(UC_HOOK_MEM_READ, self.read, begin=base, end=base + 0x2B)
        cpu.hook_add(UC_HOOK_MEM_WRITE, self.write, begin=base, end=base + 0x2B)

    def read(self, cpu, access, address, size, value, data):
        offset = address - self.base
        if offset == 4:
            cpu.mem_write(address, struct.pack("<I", self.status))
        elif offset == 0x28 and self.receiving:
            register = self.pointer.get(self.address, 0)
            peer = self.devices[self.address]
            verify(register in peer or (self.address, register) in self.registers,
                   f"Unspecified synthetic I2C response at {self.address:#x}/{register:#x}")
            response = self.registers.get((self.address, register), peer.get(register, 0))
            cpu.mem_write(address, struct.pack("<I", response))
            self.received.append((self.address, register, response))

    def write(self, cpu, access, address, size, value, data):
        offset = address - self.base
        if offset == 0:
            self.configuration = value
        elif offset == 8:
            self.enabled |= value
        elif offset == 12:
            self.enabled &= ~value
        elif offset == 0x28:
            self.data = value & 255
        elif offset == 0x20:
            verify(size == 4 and value in (1, 2, 4), "Unmodeled I2C master command")
            self.commands.append((value, self.data))
            if value == 2:
                self.address, self.receiving = self.data >> 1, bool(self.data & 1)
                self.tx_count = 0
                if self.address not in self.devices:
                    self.unknown_addresses.append(self.address)
                    self.status = 7  # MSTPENDING + address NACK state.
                else:
                    self.status = 3 if self.receiving else 5
            elif value == 4:
                self.status = 1
            elif self.receiving:
                self.pointer[self.address] += 1
                self.status = 3
            else:
                if self.tx_count == 0:
                    self.pointer[self.address] = self.data
                else:
                    register = self.pointer[self.address]
                    self.registers[self.address, register] = self.data
                    self.pointer[self.address] = register + 1
                self.transmitted.append((self.address, self.data))
                self.tx_count += 1
                self.status = 5

    def pending(self):
        return bool(self.configuration & self.enabled & self.status & 1)


def prepare(image, dsp, candidate, identity, second_bytes):
    replay = Replay(image, dsp)
    cpu = replay.cpu
    model_syscon_reset(cpu)
    for page in (0xE000E000, 0x40001000, 0x40003000, 0x40004000, 0x40005000, 0x4000B000,
                 0x4000C000, 0x4000D000, 0x40014000, 0x40020000, CTIMER2, 0x40040000,
                 CTIMER3, 0x40082000, 0x40089000, 0x4008C000, 0x4008E000, 0x40097000,
                 0x40098000, 0x40099000, 0x400A0000):
        cpu.mem_map(page, 4096)
    model_async_syscon_reset(cpu)
    if candidate:
        cpu.mem_write(BOOT_HANDOFF, struct.pack("<I", CANDIDATE_BOOT | 1))
        install_simulated_tx_tap(replay)
        redirect_simulated_branch(cpu, MAIN_DISPATCH_CALL, FOREGROUND, "fbf79dff", link=True)
    cpu.mem_protect(0x8000, 0x30000, UC_PROT_READ | UC_PROT_EXEC)
    cpu.mem_write(0x20000000, b"\xA5" * 0x28000)
    cpu.mem_write(0x20027FF8, bytes(8))
    for address, value in ((0x40020054, 0x220), (0x40000584, 1), (0x40000520, 1)):
        cpu.mem_write(address, struct.pack("<I", value))
    nvic = InterruptEnableModel(cpu)
    devices = ({0x6B: {0x0B: identity}}, {0x20: dict(enumerate(second_bytes, 0x82))})
    buses = [I2CMasterModel(cpu, *spec, peers) for spec, peers in zip(BUS_SPECS, devices)]
    return cpu, nvic, buses


def run_startup(image, dsp, *, candidate=False, identity=0x53, second_bytes=b"\x12\x34\x56\x78",
                until_second=False, suppress_bus_irq=False, nack_identity=False, defer_bus_irqs=0):
    cpu, nvic, buses = prepare(image, dsp, candidate, identity, second_bytes)
    if nack_identity:
        buses[0].devices.clear()
    waits, writes, system_writes, irq_counts = [], [], [], Counter()
    stopped = None

    def stop_at_wait(machine, pc, size, data):
        nonlocal stopped
        timeout = 0x2000909C if pc in (0x2A610, 0x2A690) else 0x20000F64
        if machine.mem_read(WAIT_FLAGS[pc], 1)[0] and machine.mem_read(timeout, 1)[0]:
            stopped = pc
            machine.emu_stop()

    for pc in WAIT_FLAGS:
        cpu.hook_add(UC_HOOK_CODE, stop_at_wait, begin=pc, end=pc)
    cpu.hook_add(UC_HOOK_MEM_WRITE, lambda _, access, address, size, value, data:
                 writes.append((address, size, value)), begin=0x40000000, end=0x400FFFFF)
    cpu.hook_add(UC_HOOK_MEM_WRITE, lambda _, access, address, size, value, data:
                 system_writes.append((address, size, value)), begin=0xE000E000, end=0xE000EFFF)
    stack, entry = struct.unpack_from("<II", image)
    cpu.reg_write(UC_ARM_REG_SP, stack)
    target = SECOND_READ_RETURN if until_second else CONFIG_DONE
    for _ in range(300):
        stopped = None
        cpu.emu_start(entry, target, count=20_000_000)
        pc = cpu.reg_read(UC_ARM_REG_PC)
        if pc == target:
            break
        verify(stopped == pc, f"Unmodeled startup boundary at 0x{pc:X}")
        waits.append(pc)
        context = cpu.context_save()
        interrupt_sp = (cpu.reg_read(UC_ARM_REG_SP) - 32) & ~7
        withhold = suppress_bus_irq or irq_counts[9] < defer_bus_irqs
        pending = [] if withhold else [b.irq for b in buses if b.pending() and nvic.allows(cpu, b.irq)]
        irq = pending[0] if pending else 9
        verify(nvic.allows(cpu, irq), f"Startup requested disabled interrupt {irq}")
        vector, = struct.unpack("<I", cpu.mem_read(0x8000 + (16 + irq) * 4, 4))
        cpu.reg_write(UC_ARM_REG_SP, interrupt_sp)
        cpu.reg_write(UC_ARM_REG_LR, RETURN | 1)
        cpu.emu_start(vector, RETURN, count=20_000)
        verify(cpu.reg_read(UC_ARM_REG_PC) == RETURN, f"Startup vector {irq} did not return")
        irq_counts[irq] += 1
        cpu.context_restore(context)
        cpu.ctl_flush_tb()
        entry = pc | 1
    verify(cpu.reg_read(UC_ARM_REG_PC) == target,
           f"Startup event budget exhausted at {cpu.reg_read(UC_ARM_REG_PC):#x}: {dict(irq_counts)}, "
           f"bus states {[(b.status, b.enabled, b.configuration, len(b.commands)) for b in buses]}")
    configuration = cpu.mem_read(0x20000F2D, 1)[0]
    observed = {register: value for (address, register), value in buses[0].registers.items() if address == 0x6B}
    expected = not (suppress_bus_irq or nack_identity) and identity & 0xF8 == 0x50
    verify(configuration == int(expected), "Unexpected stock first-device configured flag")
    verify(observed == (EXPECTED_CONFIG if expected else {}), "Stock startup register configuration changed")
    if until_second:
        verify(cpu.reg_read(UC_ARM_REG_R0) == 4 and cpu.mem_read(0x2000A3FF, 4) == second_bytes,
               "Second stock I2C read did not preserve the supplied four bytes")
    for address, length in ((STATE, 32), (LEVELS, 4), (TAP_BUFFER, 204)):
        verify(cpu.mem_read(address, length) == (b"\0" if candidate else b"\xA5") * length,
               "Startup modified private candidate guards or failed to initialize them")
    report = {"checkpoint": hex(target), "synthetic_identity": identity,
              "first_device_configured": bool(configuration), "register_writes": observed,
              "interrupt_counts": dict(irq_counts), "wait_events": len(waits),
              "second_reply_hex": second_bytes.hex() if until_second else None,
              "stock_mmio_writes": len(writes),
              "system_control_writes": len(system_writes),
              "unknown_addresses": [b.unknown_addresses for b in buses],
              "received_bytes": [[list(item) for item in b.received] for b in buses]}
    # Compare all initialized stock RAM and peripheral writes, excluding the
    # candidate's separately allocated private simulator state.
    state = (bytes(cpu.mem_read(0x20000000, 0x13D28)), cpu.reg_read(UC_ARM_REG_SP),
             tuple(writes), tuple(system_writes), tuple(waits), nvic.enabled, cpu.reg_read(UC_ARM_REG_PRIMASK))
    return report, state


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("stock", type=Path)
    parser.add_argument("dsp", type=Path)
    args = parser.parse_args()
    image, dsp = args.stock.read_bytes(), args.dsp.read_bytes()
    verify(hashlib.sha256(image).hexdigest() == STOCK_SHA256, "Expected unmodified Dev2FW 2.04.00")
    verify(len(dsp) > CANDIDATE_BOOT - 0x100000, "Rebuild the DSP module including its reset routine")
    report = {}
    for name, options in (("configured", {}), ("identity_mask_low", {"identity": 0x50}),
                          ("identity_mask_high", {"identity": 0x57}),
                          ("identity_below_mask", {"identity": 0x4F}),
                          ("identity_above_mask", {"identity": 0x58}),
                          ("wrong_identity", {"identity": 0}),
                          ("address_nack", {"nack_identity": True}),
                          ("missing_interrupt", {"suppress_bus_irq": True}),
                          ("delayed_interrupt", {"defer_bus_irqs": 8}),
                          ("timeout_then_recovery", {"defer_bus_irqs": 80}),
                          ("second_peer_read", {"until_second": True}),
                          ("second_peer_byte_edges", {"until_second": True, "second_bytes": b"\0\xFF\x80\x7F"})):
        stock, stock_state = run_startup(image, dsp, **options)
        candidate, candidate_state = run_startup(image, dsp, candidate=True, **options)
        verify(stock == candidate and stock_state == candidate_state, f"Candidate changed stock startup in {name}")
        report[name] = stock
    for incomplete in (b"", b"\x12\x34\x56"):
        try:
            run_startup(image, dsp, until_second=True, second_bytes=incomplete)
        except RuntimeError as error:
            verify(str(error).startswith("Unspecified synthetic I2C response"),
                   f"Unexpected missing-peer failure: {error}")
        else:
            raise RuntimeError("Missing synthetic peripheral register silently became a successful response")
    print(json.dumps({"scope": "offline startup with synthetic register peers and manually scheduled vectors",
                      "stock_candidate_match": True, "missing_peer_register_rejected": True,
                      "incomplete_peer_reply_cases": 2,
                      "stock_ram_compared_bytes": 0x13D28,
                      "input_sha256": {"stock": hashlib.sha256(image).hexdigest(), "dsp": hashlib.sha256(dsp).hexdigest()},
                      "cases": report}, indent=2))


if __name__ == "__main__":
    main()
