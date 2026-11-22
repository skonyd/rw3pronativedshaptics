"""Replay the original FLEXCOMM9 RGB serializer, DMA descriptors and callbacks."""
import argparse
import hashlib
import json
from pathlib import Path
import struct

from unicorn import UC_HOOK_CODE, UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE, UC_MEM_READ, UC_MEM_WRITE
from unicorn.arm_const import (UC_ARM_REG_IPSR, UC_ARM_REG_LR, UC_ARM_REG_PC,
                              UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2)

from nxp_dma_status import DMA
from nxp_spi_fifo import SPI9, SpiMasterFifo
from replay_nxp_loop import PERSISTENT, check_isolated_trace
from replay_nxp_loop_i2c import I2CLoopLab
from replay_nxp_exceptions import fixture, read_mmio, write_mmio
from replay_nxp_clock import check_motor_trace
from simulate_audio_haptics import (RETURN, STOCK_SHA256, TAP_BUFFER, check_continuous_dma,
                                   read_sensa_pwm, tone, verify)


TX, RX = 23, 22
CONTEXT, READY, BUFFER, RECEIVE = 0x2000977C, 0x200044BD, 0x20009C5C, 0x2000979C


class RgbSpiLab(I2CLoopLab):
    def __init__(self, image, dsp, *, candidate=True, connection=0, gate=0, initialize=True,
                 extra_pin=0, other_pin=0):
        super().__init__(image, dsp, candidate=candidate, connection=connection, gate=gate)
        verify(extra_pin in (0, 1) and other_pin in (0, 1), 'Extra GPIO pins need explicit binary inputs')
        self.gpio.pins.update({113: extra_pin, 23: other_pin})
        self.spi = SpiMasterFifo(self.cpu)
        self.dma_enabled, = struct.unpack('<I', self.cpu.mem_read(DMA + 0x20, 4))
        self.armed, self.shifted = set(), set()
        self.dma_resets = 0
        self.callbacks = 0
        self.callback_status, self.driver_status = [], []
        self.cpu.hook_add(UC_HOOK_MEM_READ, self.read_dma, begin=DMA + 0x20, end=DMA + 0x23)
        self.cpu.hook_add(UC_HOOK_MEM_READ, self.read_dma, begin=DMA + 0x30, end=DMA + 0x33)
        self.cpu.hook_add(UC_HOOK_MEM_WRITE, self.write_dma, begin=DMA, end=DMA + 4095)
        self.cpu.hook_add(UC_HOOK_MEM_WRITE, self.reset_dma, begin=0x40000120, end=0x40000123)
        self.cpu.hook_add(UC_HOOK_CODE, self.application_callback, begin=0x20A18, end=0x20A18)
        for pc in (0x20A40, 0x20BFC):
            self.cpu.hook_add(UC_HOOK_CODE, self.driver_callback, begin=pc, end=pc)
        if initialize:
            # The older audio boundary fixture did not execute global DMA_Init.
            # Execute it here, including the register reset, then rebuild the
            # existing stock I2S handles instead of retaining pre-reset state.
            self.replay.call(0x137C4, RETURN)
            self.replay.call(0x13D6C, RETURN)
            # Original clock, pin-mux, SPI and DMA-handle setup, including its
            # first 110-byte transfer. This is not a successful whole boot.
            self.replay.call(0x140A0, RETURN)
            verify(self.spi.registers[0x400] == 5 and self.spi.registers[0x424] == 4 and
                   self.spi.selection == 2 and self.cpu.mem_read(0x2000905A, 2) == b'\x07\0',
                   'Original SPI initialization did not configure eight-bit master transfers')

    def reset_dma(self, cpu, access, address, size, value, data):
        if value & 0x100000:
            verify(size == 4 and not self.armed and not any(self.dma.flags.values()) and
                   not self.irq.pending & 2, 'DMA reset during an active/pending transfer is not modeled')
            registers = [0, 8, 0x20, 0x28, 0x30, 0x38, 0x40, 0x48, 0x50, 0x58, 0x60, 0x68, 0x70, 0x78]
            registers += [0x400 + 16 * ch + field for ch in range(30) for field in (0, 4, 8)]
            for offset in registers: cpu.mem_write(DMA + offset, bytes(4))
            self.dma.enabled, self.dma_enabled = 0, 0
            self.dma_resets += 1

    def application_callback(self, cpu, pc, size, data):
        self.callbacks += 1
        self.callback_status.append(cpu.reg_read(UC_ARM_REG_R2))

    def driver_callback(self, cpu, pc, size, data):
        self.driver_status.append((RX if pc == 0x20A40 else TX, cpu.reg_read(UC_ARM_REG_R2)))

    def read_dma(self, cpu, access, address, size, value, data):
        verify(address in (DMA + 0x20, DMA + 0x30) and size == 4, 'Unmodeled DMA enable/active read')
        result = self.dma_enabled if address == DMA + 0x20 else sum(1 << ch for ch in self.armed - self.shifted)
        cpu.mem_write(address, struct.pack('<I', result))

    def write_dma(self, cpu, access, address, size, value, data):
        if address == DMA + 0x20:
            verify(size == 4 and value >> 30 == 0, 'Unmodeled DMA enable write')
            self.dma_enabled |= value
        for channel in (RX, TX):
            if address == DMA + 0x408 + 16 * channel:
                verify(size == 4, 'Unmodeled SPI DMA transfer width')
                if value & 5 == 5:
                    verify(channel not in self.armed, 'SPI DMA restarted before completion')
                    self.armed.add(channel)

    def check_mmio(self, cpu, access, address, size, value, data):
        if self.in_foreground and not cpu.reg_read(UC_ARM_REG_IPSR):
            if SPI9 <= address < SPI9 + 4096:
                SpiMasterFifo.validate(access, address, size)
                return
            if DMA <= address < DMA + 4096:
                allowed = {0x20, 0x30, 0x48, 0x50, 0x58, 0x60, 0x560, 0x568, 0x570, 0x578}
                verify(size == 4 and address - DMA in allowed, 'Unmodeled foreground SPI DMA access')
                return
        super().check_mmio(cpu, access, address, size, value, data)

    def words(self, address):
        return struct.unpack('<4I', self.cpu.mem_read(address, 16))

    def plan(self, channel):
        table, = struct.unpack('<I', self.cpu.mem_read(DMA + 8, 4))
        verify(table == 0x2000B000, 'Unexpected original DMA descriptor table')
        pointer, seen, result = table + 16 * channel, set(), []
        while pointer:
            verify(pointer % 16 == 0 and pointer not in seen and len(seen) < 4 and
                   0x20000000 <= pointer <= 0x20013D18, 'Invalid/cyclic SPI DMA descriptor')
            seen.add(pointer)
            config, source, destination, link = self.words(pointer)
            verify(config & ~0x03FFF33F == 0, 'Reserved SPI DMA descriptor bits')
            width = 1 << (config >> 8 & 3)
            count = (config >> 16 & 1023) + 1
            verify(config & 1 and width in (1, 4), 'Invalid SPI DMA width/valid bit')
            verify(config >> 12 & 3 in ((0, 1) if channel == TX and count == 1 else (1,) if channel == TX else (0,)) and
                   config >> 14 & 3 == (0 if channel == TX else 1), 'Unmodeled SPI DMA increments')
            verify(destination == SPI9 + 0xE20 if channel == TX else source == SPI9 + 0xE30,
                   'SPI DMA descriptor points at the wrong FIFO')
            verify(count <= 1200 and (width == 1 or count == 1), 'Unbounded SPI DMA transfer')
            memory = source if channel == TX else destination
            start = memory - (count - 1) * width
            verify(0x20000000 <= start and memory + width <= 0x20013D28, 'SPI DMA crosses stock RAM')
            result.append((config, start, count, width))
            verify(bool(config & 2) == bool(link), 'SPI DMA reload/link mismatch')
            pointer = link
        return result

    def exchange(self, miso):
        verify(isinstance(miso, bytes), 'SPI exchange needs an explicit byte buffer')
        verify(self.armed == {RX, TX} and not self.shifted, 'SPI DMA pair is not newly armed')
        verify(self.dma_enabled & (1 << RX | 1 << TX) == (1 << RX | 1 << TX), 'SPI DMA channels are disabled')
        verify(self.spi.registers[0xE00] & 0x3000 == 0x3000, 'SPI FIFO DMA requests are disabled')
        verify(self.cpu.mem_read(DMA, 4) == b'\x01\0\0\0', 'DMA controller is disabled')
        verify(not self.spi.tx and not self.spi.rx, 'SPI exchange starts with undrained FIFOs')
        tx, rx = self.plan(TX), self.plan(RX)
        total = sum(row[2] for row in tx)
        verify(len(rx) == 1 and rx[0][3] == 1 and rx[0][2] == total and 0 < total <= 1200,
               'SPI TX/RX descriptor lengths differ')
        tail_source = BUFFER if len(tx) == 1 and tx[0][2:] == (1, 1) else 0x2000B3B8
        verify(rx[0][1] == RECEIVE and tx[-1][1] == tail_source and
               (len(tx) == 1 or tx[0][1] == BUFFER), 'Unmodeled RGB DMA buffer location')
        verify(len(miso) == total, 'Explicit SPI response length differs from wire clocks')
        verify(all(not cfg & 0x30 for cfg, *_ in tx[:-1]) and tx[-1][0] & 0x10 and rx[0][0] & 0x10,
               'SPI DMA completion flag is not on the terminal descriptor')
        sent, index = bytearray(), 0
        for config, source, count, width in tx:
            for offset in range(count):
                value = int.from_bytes(self.cpu.mem_read(source + offset * width, width), 'little')
                self.spi.push(value, width)
                sent.append(self.spi.clock(miso[index]))
                self.cpu.mem_write(rx[0][1] + index, bytes([self.spi.pop()]))
                index += 1
        self.shifted = {RX, TX}
        words = [word for word, _ in self.spi.wire[-total:]]
        verify(words[-1] & 0x100000 and all(not word & 0x100000 for word in words[:-1]),
               'SPI end-of-transfer did not occur on exactly the last byte')
        for channel in (RX, TX):
            address = DMA + 0x408 + 16 * channel
            value, = struct.unpack('<I', self.cpu.mem_read(address, 4))
            self.cpu.mem_write(address, struct.pack('<I', value & ~5))
        return bytes(sent)

    def complete(self, channel):
        verify(channel in self.shifted and channel in self.armed, 'Duplicate/unshifted SPI DMA completion')
        self.shifted.remove(channel); self.armed.remove(channel)
        self.dma.signal(a=1 << channel)
        verify(self.irq.service() == 1 and not self.dma.asserted(), 'Original SPI DMA IRQ did not acknowledge its completion')

    def drain(self, order=(RX, TX)):
        size = sum(row[2] for row in self.plan(TX))
        response = bytes((i * 29 + 7) & 255 for i in range(size))
        before = self.callbacks
        sent = self.exchange(response)
        verify(self.cpu.mem_read(RECEIVE, size) == response, 'SPI receive did not preserve explicit supplied bytes')
        self.complete(order[0])
        verify(self.callbacks == before and self.cpu.mem_read(CONTEXT + 24, 4) == b'\x01\0\0\0',
               'One DMA completion prematurely completed the RGB transaction')
        self.complete(order[1])
        verify(self.callbacks == before + 1 and self.cpu.mem_read(CONTEXT, 2) == bytes(2) and
               self.cpu.mem_read(CONTEXT + 24, 4) == bytes(4) and self.cpu.mem_read(BUFFER, 1200) == bytes(1200),
               'Original paired DMA callback did not retire and clear the RGB buffer')
        return sent


def encoded_rgb(data):
    # Independent bit-string oracle; do not reuse the stock shift/OR algorithm.
    stream = ''.join('110' if byte & (1 << bit) else '100' for byte in data for bit in range(7, -1, -1))
    return b'\0' + (int(stream, 2).to_bytes(3 * len(data), 'big') if data else b'')


def codec_cases(image, dsp):
    lab = RgbSpiLab(image, dsp)
    initial = lab.drain()
    verify(initial == bytes(110), 'Original initial RGB transfer did not contain its empty buffer')
    cases = [bytes([value if i == side else 0 for i in range(3)]) for side in range(3) for value in range(256)]
    cases += [bytes((i * 43 + 19) & 255 for i in range(n)) for n in (0, 1, 2, 4, 5, 6, 12, 63, 96, 299, 300, 301)]
    digest, wire_bytes = hashlib.sha256(), 0
    for index, data in enumerate(cases):
        lab.cpu.mem_write(0x20025000, data or b'\xA5')
        guard = bytes(lab.cpu.mem_read(0x2000A10C, 16))
        lab.replay.call(0x2B104, RETURN, 0x20025000, len(data))
        count = min(len(data), 300)
        # Stock processes complete RGB triplets, with trailing incomplete
        # bytes left as zero in the already-cleared output buffer.
        expected = encoded_rgb(data[:count - count % 3]) + bytes(3 * (count % 3))
        actual = lab.drain((TX, RX) if index & 1 else (RX, TX))
        verify(actual == expected, 'Stock RGB serializer differs from independent bit oracle')
        verify(lab.cpu.mem_read(0x2000A10C, 16) == guard and
               lab.cpu.mem_read(0x20025000, len(data)) == data, 'RGB frame crossed its output/source guards')
        digest.update(struct.pack('<I', len(data)) + data + actual); wire_bytes += len(actual)
    return {'cases': len(cases), 'all_256_values_per_rgb_component': True, 'initial_bytes': len(initial),
            'wire_bytes': wire_bytes, 'case_wire_sha256': digest.hexdigest(), 'complete_rgb_triplets_only': True,
            'input_count_clamped_to_300': True, 'both_dma_completion_orders': True,
            'callbacks': lab.callbacks, 'checked_irq_returns': lab.irq.checked_returns}


def loop_cases(image, dsp):
    report = {}
    for connection, gate in ((0, 0), (0, 1), (1, 0), (1, 1)):
        variants, details = [], []
        for candidate in (False, True):
            lab = RgbSpiLab(image, dsp, candidate=candidate, connection=connection, gate=gate)
            lab.drain()
            for index in range(8):
                lab.advance_us(1000); lab.wdt.advance(800); lab.run()
                if lab.armed: lab.drain((TX, RX) if index & 1 else (RX, TX))
            verify(lab.iterations == lab.calls[0x2B538] == 8 and not lab.armed and not lab.shifted and
                   not lab.irq.active and not lab.irq.frames, 'Foreground RGB fixture did not drain at its loop boundary')
            details.append({'iterations': lab.iterations, 'rgb_callbacks': lab.callbacks,
                            'spi_wire_bytes': len(lab.spi.wire), 'checked_irq_returns': lab.irq.checked_returns})
            variants.append((bytes(lab.cpu.mem_read(0x20000000, 0x13D28)),
                             tuple(lab.cpu.reg_read(r) for r in PERSISTENT), read_sensa_pwm(lab.cpu, lab.registers),
                             lab.bus.commands, lab.spi.wire, lab.wdt.feeds))
        verify(variants[0] == variants[1] and details[0] == details[1], 'Inactive candidate changed RGB/foreground state')
        verify(details[0]['rgb_callbacks'] == (2 if connection == 0 else 1), 'Unexpected original RGB loop scheduling')
        report[f'{connection},{gate}'] = {**details[0], 'stock_ram_compared_bytes': 0x13D28,
                                         'stock_candidate_ram_registers_pwm_spi_i2c_match': True}
    return report


def audio_stream(image, dsp, blocks, *, connection=0, candidate=True, lab=None):
    if lab is None: lab = RgbSpiLab(image, dsp, candidate=candidate, connection=connection)
    lab.drain()
    pwm, combined, injected_rgb, pending_rgb = [], 0, [], []

    def dispatch(status, bit):
        nonlocal combined
        lab.advance_us(1000); lab.wdt.advance(800)
        pending = sum(1 << ch for ch in lab.shifted)
        if pending:
            verify(lab.shifted == lab.armed == {RX, TX}, 'Incomplete RGB DMA batch before audio completion')
            lab.armed.clear(); lab.shifted.clear(); combined += 1
        lab.dma.signal(a=pending | (bit if status == 0x58 else 0), b=bit if status == 0x60 else 0)
        verify(lab.irq.service() == 1 and not lab.dma.asserted(), 'Combined SPI/I2S DMA IRQ did not acknowledge all sources')

    def block(tick, source, pcm):
        if tick == 0: lab.cpu.mem_write(TAP_BUFFER + 192, struct.pack('<I', int(candidate)))
        lab.run()
        verify(lab.cpu.mem_read(source, 192) == pcm, 'RGB/main-loop path changed headset PCM')
        if tick in (24, 48, 72, 96, 120): pending_rgb.append(tick)
        rgb = None
        if pending_rgb and not lab.armed:
            requested = pending_rgb.pop(0)
            rgb = bytes((requested * 3 + i * 17) & 255 for i in range(12))
            lab.cpu.mem_write(0x20025000, rgb)
            lab.replay.call(0x2B104, RETURN, 0x20025000, len(rgb))
            injected_rgb.append((requested, tick))
        if lab.armed:
            size = sum(row[2] for row in lab.plan(TX))
            sent = lab.exchange(bytes((i * 17 + tick) & 255 for i in range(size)))
            if rgb is not None: verify(sent == encoded_rgb(rgb), 'Boundary RGB control failed its bit oracle')
        pwm.append(read_sensa_pwm(lab.cpu, lab.registers))

    result = check_continuous_dma(image, dsp, blocks, replay=lab.replay, tail_ticks=64,
                                  irq_dispatch=dispatch, on_block=block)
    tail_jobs = int(bool(lab.shifted))
    for ch in tuple(lab.shifted): lab.complete(ch)
    verify(not lab.armed and not lab.shifted and not lab.irq.active, 'Combined audio/RGB run left a pending DMA source')
    verify([row[0] for row in injected_rgb] == [24, 48, 72, 96, 120] and
           all(0 <= actual - requested <= 1 for requested, actual in injected_rgb) and not pending_rgb,
           'RGB controls were lost or unexpectedly delayed')
    verify(combined + tail_jobs == lab.callbacks - 1, 'RGB completion accounting differs from shared IRQ batches')
    lab.rgb_control_events = injected_rgb
    return lab, result, pwm, combined


def audio_cases(image, dsp):
    stereo = [tone(i, ((80, 190), (110, 230))) for i in range(96)]
    _, _, idle, _ = audio_stream(image, dsp, stereo, candidate=False)
    report = {}
    for name, connection, other_pin, blocks, side in (
            ('rgb_and_stereo', 0, 0, stereo, None),
            ('rgb_and_left', 0, 0, [tone(i, ((80, 190), ())) for i in range(96)], 0),
            ('rgb_and_right', 0, 0, [tone(i, ((), (110, 230))) for i in range(96)], 1),
            ('rgb_other_pin_high', 0, 1, stereo, None),
            ('i2c_gate_and_stereo', 1, 0, stereo, None)):
        instance = RgbSpiLab(image, dsp, connection=connection, other_pin=other_pin)
        lab, result, pwm, combined = audio_stream(image, dsp, blocks, connection=connection, lab=instance)
        if side is None: check_motor_trace(pwm, idle)
        else: check_isolated_trace(pwm, idle, side)
        verify(lab.values()[5] == len(lab.observed_decodes) == 9 and lab.values()[10] == 1,
               'Combined audio path did not publish nine independently checked frames including one stop')
        verify(lab.calls[0x2B538] == lab.iterations == 159 and combined >= 5,
               'Combined audio/RGB dispatch schedule changed')
        report[name] = {**result, 'iterations': lab.iterations, 'combined_spi_i2s_irq_batches': combined,
                        'runtime_frame_checks': len(lab.observed_decodes), 'rgb_callbacks': lab.callbacks,
                        'checked_irq_returns': lab.irq.checked_returns, 'drained_to_stock_idle': True,
                        'explicit_original_rgb_request_and_call_ticks': lab.rgb_control_events,
                        'supplied_gpio': {'P3_17': 0, 'P0_23': other_pin},
                        'opposite_channel_and_triggers_checked': side is not None}
    return report


def register_contracts():
    cpu, _ = fixture()
    spi = SpiMasterFifo(cpu)
    read = lambda off: read_mmio(cpu, SPI9 + off)
    def write(off, value, size=4):
        if size != 2: return write_mmio(cpu, SPI9 + off, value, size=size)
        cpu.mem_write(0x300, bytes.fromhex('01807047'))  # STRH r1,[r0]; BX LR.
        cpu.ctl_flush_tb()
        cpu.reg_write(UC_ARM_REG_R0, SPI9 + off); cpu.reg_write(UC_ARM_REG_R1, value)
        cpu.reg_write(UC_ARM_REG_LR, 0x501); cpu.emu_start(0x301, 0x500, count=8)
        verify(cpu.reg_read(UC_ARM_REG_PC) == 0x500, 'Synthetic halfword MMIO writer did not return')
    verify((read(0xFF8), read(0xE00), read(0xE04)) == (0x20, 0x10, 0x30), 'Wrong SPI capability/size/reset flags')
    write(0xFF8, 2); verify(read(0xFF8) == 0x22, 'SPI selection overwrote read-only capability')
    write(0x400, 5); write(0xE00, 3); write(0xE22, 0x70E, 2)
    verify(read(0xE00) == 0x13, 'SPI read-only FIFO size was overwritten')
    write(0xE20, 0xA6, 1); verify(read(0xE04) == 0x120, 'SPI queued TX flags differ')
    verify(spi.clock(0x5A) == 0xA6 and read(0xE04) == 0x10070, 'SPI explicit full-duplex byte/flags differ')
    verify(read(0xE30) == 0x5A and read(0xE04) == 0x30, 'SPI RX pop did not drain')
    write(0xE20, 0x071E0042); spi.clock(0x51); verify(read(0xE30) == 0x51, 'Full SPI word RX mismatch')
    write(0xE20, 0x43, 1); spi.clock(0x52); verify(read(0xE30) == 0x52, 'Inherited-control SPI RX mismatch')
    verify(all(word & 0x100000 for word, _ in spi.wire[-2:]), 'Byte write lost the last full-word control')
    write(0xE22, 0x70E, 2)
    rejected = []

    def reject(name, action):
        try: action()
        except RuntimeError: rejected.append(name)
        else: raise RuntimeError('Unsupported SPI operation accepted: ' + name)

    for name, action in (
            ('undefined_register', lambda: read(0)), ('read_writeonly_fifo', lambda: read(0xE20)),
            ('wrong_width', lambda: write(0x400, 5, 1)), ('selection_lock', lambda: write(0xFF8, 10)),
            ('slave_mode', lambda: write(0x400, 1)), ('non_8bit_frame', lambda: write(0xE22, 0xF0E, 2)),
            ('unknown_control', lambda: spi.push(0x07CE0001, 4)),
            ('missing_miso', lambda: spi.clock(None)), ('rx_underflow', spi.pop)):
        reject(name, action)
    for i in range(8): write(0xE20, i, 1)
    verify(read(0xE04) == 8 << 8, 'Eight-entry SPI TX FIFO not full')
    reject('tx_overflow', lambda: write(0xE20, 8, 1))
    write(0xE00, 0x30003)
    verify(not spi.tx and not spi.rx and read(0xE00) == 0x13, 'SPI flush did not self-clear')
    for i in range(8):
        write(0xE20, i, 1); spi.clock(i + 17)
    verify(read(0xE04) == 0x800F0, 'Eight-entry SPI RX FIFO not full')
    write(0xE20, 8, 1)
    reject('rx_overflow', lambda: spi.clock(25))
    write(0xE00, 0x30003)
    return {'checks': ['reset_flags', 'read_only_spi_capability', 'read_only_size_1', 'eight_entry_tx_and_rx',
                       'explicit_full_duplex_byte', 'rx_pop', 'full_word_control_retained', 'self_clearing_flush'], 'rejected': rejected,
            'electrical_error_behavior_modeled': False}


def descriptor_controls(image, dsp):
    rejected = []
    for name in ('no_bus_clocks', 'missing_miso', 'short_reply', 'long_reply', 'reserved_config',
                 'descriptor_cycle', 'wrong_fifo', 'rx_count_mismatch', 'wrong_tail_marker',
                 'disabled_spi', 'disabled_dma', 'disabled_fifo_dma', 'active_reset'):
        lab = RgbSpiLab(image, dsp)
        tx = 0x2000B000 + TX * 16
        rx = 0x2000B000 + RX * 16
        action = lambda: lab.exchange(bytes(110))
        if name == 'no_bus_clocks': action = lambda: lab.complete(TX)
        elif name == 'missing_miso': action = lambda: lab.exchange(None)
        elif name == 'short_reply': action = lambda: lab.exchange(bytes(109))
        elif name == 'long_reply': action = lambda: lab.exchange(bytes(111))
        elif name == 'reserved_config':
            lab.cpu.mem_write(tx, struct.pack('<I', lab.words(tx)[0] | 0x80000000))
        elif name == 'descriptor_cycle': lab.cpu.mem_write(tx + 12, struct.pack('<I', tx))
        elif name == 'wrong_fifo': lab.cpu.mem_write(tx + 8, struct.pack('<I', SPI9 + 0xE24))
        elif name == 'rx_count_mismatch': lab.cpu.mem_write(rx, struct.pack('<I', lab.words(rx)[0] - 0x10000))
        elif name == 'wrong_tail_marker':
            word, = struct.unpack('<I', lab.cpu.mem_read(0x2000B3B8, 4))
            lab.cpu.mem_write(0x2000B3B8, struct.pack('<I', word & ~0x100000))
        elif name == 'disabled_spi': lab.spi.registers[0x400] = 4
        elif name == 'disabled_dma': lab.cpu.mem_write(DMA, bytes(4))
        elif name == 'disabled_fifo_dma': lab.spi.registers[0xE00] &= ~0x3000
        elif name == 'active_reset': action = lambda: lab.replay.call(0x137C4, RETURN)
        try: action()
        except RuntimeError:
            verify(lab.callbacks == 0 and lab.cpu.mem_read(READY, 1) == b'\0', 'Rejected SPI state manufactured application success')
            rejected.append(name)
        else: raise RuntimeError('Malformed/missing SPI DMA state accepted: ' + name)
    lab = RgbSpiLab(image, dsp); lab.drain()
    try: lab.complete(TX)
    except RuntimeError: rejected.append('duplicate_completion')
    else: raise RuntimeError('Duplicate SPI completion accepted')
    verify(lab.callbacks == 1, 'Duplicate completion reissued the application callback')
    return {'rejected': rejected, 'rejected_cases_cannot_manufacture_ready': True}


def stock_error_flags(image, dsp):
    cases = []
    for order in ((RX, TX), (TX, RX)):
        lab = RgbSpiLab(image, dsp)
        for index, ch in enumerate(order):
            lab.dma.signal(error=1 << ch)
            verify(lab.irq.service() == 1 and not lab.dma.asserted(), 'Original DMA error status did not acknowledge')
            verify(lab.callbacks == index, 'Original error callback sequence changed')
        verify(lab.driver_status == [(ch, 0) for ch in order] and lab.callback_status == [0] and
               lab.cpu.mem_read(READY, 1) == b'\x01' and not lab.spi.wire,
               'Stock SPI error-flag propagation differs from the observed missing-data success path')
        cases.append({'dma_order': order, 'dma_transfer_done_arguments': lab.driver_status,
                      'application_status': lab.callback_status, 'ready': True, 'fifo_clocked_bytes': 0})
    return {'scope': 'CPU callbacks after supplied DMA ERRINT flags; not a physical bus-fault simulation',
            'cases': cases, 'ready_is_not_delivery_proof': True,
            'custom_firmware_error_propagation_still_required': True}


def gpio_boundaries(image, dsp):
    cases = []
    for other in (0, 1):
        lab = RgbSpiLab(image, dsp, extra_pin=1, other_pin=other)
        try:
            audio_stream(image, dsp, [bytes(192)] * 32, lab=lab)
        except RuntimeError as error:
            verify(str(error).endswith('0x4008C028') and lab.cpu.reg_read(UC_ARM_REG_PC) == 0xD7F8 and
                   lab.iterations == 19, 'Unexpected extra-GPIO boundary')
        else: raise RuntimeError('Unmodeled GPIO power/output branch silently completed')
        cases.append({'P3_17': 1, 'P0_23': other, 'completed_iterations': 19,
                      'pc': '0xD7F8', 'unmodeled_gpio_write': '0x4008C028', 'accepted_as_full_run': False})
    return cases


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('stock', type=Path); parser.add_argument('dsp', type=Path)
    args = parser.parse_args(); image, dsp = args.stock.read_bytes(), args.dsp.read_bytes()
    verify(hashlib.sha256(image).hexdigest() == STOCK_SHA256, 'Unexpected stock image')
    print(json.dumps({'registers': register_contracts(), 'descriptor_controls': descriptor_controls(image, dsp),
                      'stock_error_flags': stock_error_flags(image, dsp), 'gpio_boundaries': gpio_boundaries(image, dsp),
                      'codec': codec_cases(image, dsp), 'foreground': loop_cases(image, dsp),
                      'audio': audio_cases(image, dsp),
                      'scope': 'bounded original RGB SPI/DMA fixture', 'whole_device_equivalence_proven': False,
                      'flashable_candidate_ready': False, 'physical_spi_or_clock_timing_verified': False,
                      'input_sha256': {'stock': hashlib.sha256(image).hexdigest(), 'dsp': hashlib.sha256(dsp).hexdigest()}}, indent=2))


if __name__ == '__main__': main()
