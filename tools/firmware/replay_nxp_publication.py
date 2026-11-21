"""Test compiled Xbox frame packing, arbitration and atomic publication offline."""
import argparse
import hashlib
import itertools
import json
from pathlib import Path
import struct
import sys

from unicorn import UC_HOOK_CODE, UC_HOOK_MEM_WRITE
from unicorn.arm_const import UC_ARM_REG_PRIMASK, UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2

from emulate_decoder import DECODER, decode
from replay_nxp_dma_preemption import prepare, acknowledge
from simulate_audio_haptics import (
    CTIMER2, CTIMER3, FRAME_ENCODER, GAME_NOTICE, LEVELS, MAIN_DISPATCH_CALL,
    PUBLICATION, RETURN, Replay, STOCK_SHA256, TAP_BUFFER, USB_RING,
    check_continuous_dma, read_sensa_pwm, redirect_simulated_branch, tone, verify, xbox_frame,
)


GAME_IRQ, GAME_PACKET, SHIM = 17, 0x20024400, 0x6000
FLAG, STOCK_FRAME, CLOCK, MODE = 0x20000B2C, 0x20000B34, 0x200139F4, 0x20001057


def expected_frame(levels):
    return xbox_frame([([(a, a, p) for a, p in zip(levels[i:i + 2], (17, 55)) if a], [])
                       for i in (0, 2)] + [([], []), ([], [])])


def encoder_contracts(image, dsp):
    replay = Replay(image, dsp)
    cpu, output, strengths = replay.cpu, 0x20025000, 0x20025100
    cases = list(itertools.product((0, 63), repeat=4))
    cases += [(a, b, 0, 0) for a in range(64) for b in range(64)]
    cases += [(a, 0, b, 63) for a in range(64) for b in range(64)]
    seed = 0x13579BDF
    for _ in range(256):
        row = []
        for _ in range(4):
            seed = (1664525 * seed + 1013904223) & 0xFFFFFFFF
            row.append(seed >> 26)
        cases.append(tuple(row))
    digest = hashlib.sha256()
    decoded = 0
    for index, levels in enumerate(cases):
        cpu.mem_write(output - 4, b"\xA5" * 72)
        cpu.mem_write(strengths, bytes(levels))
        replay.call(FRAME_ENCODER, RETURN, output, strengths)
        size = cpu.reg_read(UC_ARM_REG_R0)
        reference = expected_frame(levels)
        frame = bytes(cpu.mem_read(output, 64))
        verify(size == len(reference) and frame == reference.ljust(64, b"\0"), "Compiled encoder differs from bitwise oracle")
        verify(cpu.mem_read(output - 4, 4) == b"\xA5" * 4 and cpu.mem_read(output + 64, 4) == b"\xA5" * 4,
               "Encoder crossed output guards")
        verify(cpu.mem_read(strengths, 4) == bytes(levels), "Encoder changed its input")
        digest.update(bytes(levels) + frame)
        if index < 16 or index % 512 == 0:
            bodies = decode(image, DECODER['xbox'], reference)
            for side in (0, 1):
                active = [(a, p) for a, p in zip(levels[side * 2:side * 2 + 2], (17, 55)) if a]
                for band, (a, p) in enumerate(active):
                    start = 17 + 16 * band
                    hz = 79 if p == 17 else 190
                    verify(bodies[side][start:start + 10] == (3, 0, hz, a * 520, 1, hz, a * 520, 60, hz, a * 520),
                           "Stock decoder rejected compiled band time/pitch/strength")
                verify(bodies[side][1] == 0, "Encoder invented a transient")
            verify(all(not any(body[1:]) for body in bodies[2:]), "Encoder activated a trigger part")
            decoded += 1
    for channel, invalid in itertools.product(range(4), (64, 127, 255)):
        levels = bytearray((63, 63, 63, 63)); levels[channel] = invalid
        cpu.mem_write(strengths, bytes(levels))
        cpu.mem_write(output, b"\xA5" * 64)
        replay.call(FRAME_ENCODER, RETURN, output, strengths)
        verify(cpu.reg_read(UC_ARM_REG_R0) == 0 and cpu.mem_read(output, 64) == bytes(64), "Invalid strength silently truncated")
    return {"byte_oracle_cases": len(cases), "stock_decoder_cases": decoded, "invalid_strength_cases": 12,
            "case_output_sha256": digest.hexdigest(), "output_guards_and_zero_tail": True}


class Lab:
    def __init__(self, image, dsp, *, notice=True):
        self.replay, self.registers, self.irq, self.dma = prepare(image, dsp, candidate=True)
        self.cpu = self.replay.cpu
        if notice:
            redirect_simulated_branch(self.cpu, 0x30F36, GAME_NOTICE, '01203870', link=True)
        # Original fixture IRQ shim: preserve exception LR, call the real class
        # 0x16 handler with a synthetic command, return through EXC_RETURN.
        code = bytes.fromhex('10b50448014608310022034b984710bd')
        self.cpu.mem_write(SHIM, code + bytes(4) + struct.pack('<II', GAME_PACKET, 0x30BA9))
        self.cpu.mem_write(0x8000 + 4 * (GAME_IRQ + 16), struct.pack('<I', SHIM | 1))
        self.irq.enabled |= 1 << GAME_IRQ
        self.irq.priorities[GAME_IRQ] = 0x20  # explicit fixture priority
        self.cpu.mem_write(PUBLICATION, struct.pack('<I', 1))
        self.masked_current = self.masked_peak = 0
        self.observed_decodes = []
        self.game_copy_before, self.accepted_game_buffers = None, []
        self.cpu.hook_add(UC_HOOK_CODE, self.observe)

    def observe(self, cpu, pc, size, data):
        if cpu.reg_read(UC_ARM_REG_PRIMASK):
            self.masked_current += 1
            self.masked_peak = max(self.masked_peak, self.masked_current)
        else:
            self.masked_current = 0
        if pc == 0x30F32:
            verify(cpu.reg_read(UC_ARM_REG_R0) == STOCK_FRAME, 'Original game copy destination changed')
            count = cpu.reg_read(UC_ARM_REG_R2)
            source = bytes(cpu.mem_read(cpu.reg_read(UC_ARM_REG_R1), count))
            before = bytes(cpu.mem_read(STOCK_FRAME, 64))
            self.game_copy_before = source[:64] + before[min(count, 64):]
        if pc == 0x30F36:
            frame = bytes(cpu.mem_read(STOCK_FRAME, 64))
            verify(frame == self.game_copy_before, 'Original game copy changed its prefix or retained tail')
            self.accepted_game_buffers.append(frame)
        if pc == DECODER['xbox']:
            verify(cpu.reg_read(UC_ARM_REG_PRIMASK) == 1, "Stock decoder ran outside publication lock")
            frame = bytes(cpu.mem_read(STOCK_FRAME, 64))
            if not self.values()[2]:
                verify(frame == expected_frame(bytes(cpu.mem_read(LEVELS, 4))).ljust(64, b'\0'),
                       'Runtime compiled audio packet differs from the independent oracle')
            elif self.accepted_game_buffers:
                verify(frame == self.accepted_game_buffers[-1], 'Game packet changed after original publication')
            self.observed_decodes.append(frame)

    def values(self):
        return struct.unpack('<16I', self.cpu.mem_read(PUBLICATION, 64))

    def set_word(self, index, value):
        self.cpu.mem_write(PUBLICATION + index * 4, struct.pack('<I', value))

    def run(self, tick=100):
        self.cpu.mem_write(CLOCK, struct.pack('<I', tick & 0xFFFFFFFF))
        self.replay.call(MAIN_DISPATCH_CALL, MAIN_DISPATCH_CALL + 4, instruction_limit=150000)

    def feed(self, block=0, parts=((83,), ()), source=USB_RING):
        self.cpu.mem_write(TAP_BUFFER, tone(block, parts) + struct.pack('<3I', 1, 1, source))

    def packet(self, frame=None):
        if frame is None:
            frame = xbox_frame([([], []), ([], [(35, 24, 0)]), ([], []), ([], [])])
        packet = bytearray(96)
        packet[1], packet[5], packet[6], packet[7], packet[8] = 0x55, len(frame) + 1, 0x16, 0x0E, 1
        packet[9:9 + len(frame)] = frame
        self.cpu.mem_write(GAME_PACKET, bytes(packet))
        return frame.ljust(64, b'\0')

    def send(self, tick=100):
        self.cpu.mem_write(CLOCK, struct.pack('<I', tick))
        self.irq.pend(GAME_IRQ)
        verify(self.irq.service() == 1, "Game command fixture IRQ did not return")


def control_cases(image, dsp):
    checks, peaks = [], []
    lab = Lab(image, dsp); lab.feed(); lab.run()
    verify(lab.values()[1] == 1 and lab.values()[5] == 1 and lab.values()[8] > 0, "Compiled publication positive control is silent")
    frame = expected_frame(bytes(lab.cpu.mem_read(LEVELS, 4))).ljust(64, b'\0')
    verify(lab.observed_decodes == [frame], "Stock decoder did not consume the complete compiled packet")
    for tick in (100, 100, 101, 111, 349): lab.run(tick)
    verify(lab.values()[5] == 1 and lab.values()[1] == 1, "Rapid polling or 249 ticks cleared active audio")
    lab.run(350); lab.run(351)
    verify(lab.values()[5] == 2 and lab.values()[1] == 0 and lab.values()[10] == 1, "Stale source did not stop once at 250 counter ticks")
    checks.append('rapid_poll_hold_and_250_counter_tick_stop'); peaks.append(lab.masked_peak)

    for initial in (100, 0xFFFFFFFA):
        lab = Lab(image, dsp); lab.feed(); lab.run(initial)
        for tick in range(1, 12): lab.feed(tick); lab.run(initial + tick)
        verify(lab.values()[5] == 1, "Audio cadence published too early")
        expected = expected_frame(bytes(lab.cpu.mem_read(LEVELS, 4))).ljust(64, b'\0')
        lab.run(initial + 12)
        verify(lab.values()[5] == 2 and lab.observed_decodes[-1] == expected, "Cadence lost the latest prepared packet or wrap")
        checks.append('cadence_wrap' if initial > 100 else 'cadence_latest_prepared'); peaks.append(lab.masked_peak)

    for old in (0, 1):
        lab = Lab(image, dsp); lab.feed(); lab.cpu.reg_write(UC_ARM_REG_PRIMASK, old); lab.run()
        verify(lab.cpu.reg_read(UC_ARM_REG_PRIMASK) == old, "Publisher changed the caller's PRIMASK")
        checks.append(f'preserves_primask_{old}')

    for long_wait in (False, True):
        lab = Lab(image, dsp); lab.cpu.mem_write(0x20000EC4, b'\1'); lab.feed(); lab.run()
        verify(lab.values()[5] == 0 and lab.values()[7] == 1 and lab.values()[15] > 0, "Busy decoder consumed or lost prepared frame")
        if long_wait: lab.run(350)
        lab.cpu.mem_write(0x20000EC4, b'\0'); lab.run(350 if long_wait else 100)
        verify(lab.values()[5] == (0 if long_wait else 1), "Busy decoder replayed expired PCM or lost fresh staging")
        checks.append('busy_expired_discarded' if long_wait else 'busy_prepared_retained')

    for mode in (1, 2):
        lab = Lab(image, dsp); lab.cpu.mem_write(MODE, bytes([mode])); lab.feed(); lab.run()
        verify(lab.values()[5] == 0 and lab.values()[9] == 1 and lab.cpu.mem_read(FLAG, 1) == b'\0', "Unsupported mode silently published Xbox frame")
        checks.append(f'unsupported_mode_{mode}')

    lab = Lab(image, dsp); lab.feed(); lab.run()
    lab.cpu.mem_write(0x20000EC4, b'\1'); lab.cpu.mem_write(TAP_BUFFER + 192, bytes(4)); lab.run(101)
    verify(lab.values()[5] == 1 and lab.values()[10] == 0 and lab.values()[1] == 1,
           'Busy decoder falsely acknowledged an audio stop')
    lab.cpu.mem_write(0x20000EC4, b'\0'); lab.run(102)
    verify(lab.values()[5] == 2 and lab.values()[10] == 1 and lab.values()[1] == 0,
           'Deferred stop did not publish when the decoder became ready')
    checks.append('busy_decoder_delays_stop_until_ready')

    lab = Lab(image, dsp); frame = lab.packet(); lab.send(); lab.feed(); lab.run()
    verify(lab.values()[3] == 1 and lab.values()[2] == 1 and lab.values()[5] == 0 and lab.observed_decodes == [frame], "Pending game did not own the frame")
    lab.run(500)
    verify(lab.values()[5] == 0, "Audio silently resumed over an ongoing game effect")
    lab.set_word(4, 1); lab.run(501)
    verify(lab.values()[2] == 0 and lab.cpu.mem_read(TAP_BUFFER + 196, 4) == bytes(4), "Rearm retained stale PCM/game ownership")
    lab.feed(1); lab.run(502)
    verify(lab.values()[5] == 1, "Explicit rearm did not accept fresh audio")
    checks.append('game_latch_and_explicit_fresh_rearm'); peaks.append(lab.masked_peak)

    lab = Lab(image, dsp); lab.packet(); lab.send(tick=10)
    verify(lab.values()[3] == 0 and lab.cpu.mem_read(FLAG, 1) == b'\0', "Stock rate-rejected command incorrectly claimed ownership")
    lab.feed(); lab.run(100)
    verify(lab.values()[5] == 1, "Rejected game input blocked audio")
    checks.append('stock_rate_gate_retained')

    lab = Lab(image, dsp); lab.feed(); lab.run()
    same = lab.observed_decodes[-1][:lab.values()[8]]
    lab.packet(same); lab.send(112); lab.feed(1); lab.run(112); lab.run(300)
    verify(lab.values()[2] == 1 and lab.values()[3] == 1 and lab.values()[5] == 1,
           'Identical game payload was mistaken for an audio-owned frame')
    checks.append('identical_game_payload_still_takes_ownership')
    lab.cpu.mem_write(TAP_BUFFER + 192, bytes(4)); lab.run(301)
    verify(lab.values()[5] == 1 and lab.values()[10] == 0, 'Audio disable overwrote a game-owned effect')
    checks.append('audio_disable_preserves_game_owner')
    lab = Lab(image, dsp); expected = lab.packet(); lab.send(); lab.feed()
    lab.cpu.mem_write(0x20000EC8, b'\1'); lab.run()
    verify(lab.values()[5] == 0 and lab.cpu.mem_read(FLAG, 1) == b'\1', 'Busy game packet was overwritten')
    lab.cpu.mem_write(0x20000EC8, b'\0'); lab.run(101)
    verify(lab.observed_decodes == [expected] and lab.values()[5] == 0, 'Busy game packet did not survive deferred decode')
    checks.append('game_packet_survives_busy_decoder')
    return {"checks": checks, "peak_masked_instruction_observations": max(peaks)}


def game_hook_contract(image, dsp):
    reports = []
    for name in ('accepted', 'rate_rejected', 'bad_marker', 'oversized'):
        snapshots = []
        for hooked in (False, True):
            lab = Lab(image, dsp, notice=hooked); lab.packet()
            if name == 'bad_marker': lab.cpu.mem_write(GAME_PACKET + 8, b'\0')
            if name == 'oversized': lab.cpu.mem_write(GAME_PACKET + 5, b'\x42')
            lab.send(10 if name == 'rate_rejected' else 100)
            snapshots.append((bytes(lab.cpu.mem_read(0x20000000, 81192)), bytes(lab.cpu.mem_read(GAME_PACKET, 96))))
            if hooked:
                verify(lab.values()[3] == int(name == 'accepted'), 'Notice counted a rejected command')
                verify(lab.irq.checked_returns == 1, 'Command shim corrupted exception return')
        verify(snapshots[0] == snapshots[1], 'Notice changed original command behavior/stock RAM')
        reports.append({'case': name, 'stock_ram_and_response_match': True})
    return reports


def interleavings(image, dsp):
    reports = []
    for phase in ('encoder', 'copy', 'flag', 'decode'):
        lab = Lab(image, dsp); expected_game = lab.packet(); lab.feed()
        fired = []

        def request():
            if not fired:
                fired.append(True)
                lab.irq.pend(GAME_IRQ)
                lab.cpu.mem_write(CTIMER2, struct.pack('<I', 1)); lab.irq.pend(36)
                lab.cpu.mem_write(CTIMER3, struct.pack('<I', 1)); lab.irq.pend(13)

        def code(cpu, pc, size, data):
            if (phase == 'encoder' and pc == FRAME_ENCODER) or (phase == 'decode' and pc == DECODER['xbox']): request()

        def write(cpu, access, address, size, value, data):
            if phase == 'copy' and address == STOCK_FRAME + 8:
                verify(cpu.reg_read(UC_ARM_REG_PRIMASK) == 1, 'Partial copy was not protected')
                request()
            if phase == 'flag' and address == FLAG and not lab.irq.active: request()

        lab.cpu.hook_add(UC_HOOK_CODE, code)
        lab.cpu.hook_add(UC_HOOK_MEM_WRITE, write, begin=FLAG, end=STOCK_FRAME + 63)
        lab.run()
        verify(fired and lab.values()[3] == 1 and lab.values()[2] == 1, 'Injected game command was lost')
        published = lab.values()[5]
        verify(published == (0 if phase == 'encoder' else 1), 'Incorrect publication ordering at game arrival')
        if lab.cpu.mem_read(FLAG, 1) != b'\0': lab.run(101)
        verify(lab.observed_decodes[-1] == lab.accepted_game_buffers[-1], 'Stock decoder read a torn or overwritten game packet')
        verify(lab.irq.checked_returns >= 3, 'Injected game and motor IRQs did not complete')
        reports.append({'phase': phase, 'audio_frames_before_game': published,
                        'checked_returns': lab.irq.checked_returns, 'complete_decodes': len(lab.observed_decodes),
                        'masked_instruction_observations': lab.masked_peak})

    for action in ('disable', 'invalid_strength', 'mode_change', 'stale_before_commit'):
        lab = Lab(image, dsp); lab.feed(); lab.run(); lab.feed(1)
        fired = []

        def fault(cpu, pc, size, data):
            if pc != FRAME_ENCODER or fired: return
            fired.append(True)
            if action == 'disable': cpu.mem_write(TAP_BUFFER + 192, bytes(4))
            elif action == 'mode_change': cpu.mem_write(MODE, b'\1')
            elif action == 'stale_before_commit': cpu.mem_write(CLOCK, struct.pack('<I', 400))
            else: cpu.mem_write(LEVELS, b'\x40')

        lab.cpu.hook_add(UC_HOOK_CODE, fault); lab.run(112)
        verify(fired, 'Control-race fixture did not run')
        if action == 'mode_change': verify(lab.values()[5] == 1 and lab.values()[9] == 1, 'Mode race published wrong-format packet')
        else:
            verify(lab.values()[5] == 2 and lab.values()[10] == 1 and lab.values()[1] == 0,
                   f'Control/source race did not publish a stop frame ({action})')
            if action == 'invalid_strength': verify(lab.values()[11] == 1, 'Invalid encode was not diagnosed')
        reports.append({'phase': action, 'diagnosed': True})
    return reports


def continuous(image, dsp, blocks=None):
    if blocks is None:
        blocks = [tone(i, ((80, 190), (110, 230))) for i in range(96)]
    lab = Lab(image, dsp)
    pwm = []

    def block(tick, source, pcm):
        if tick == 0: lab.cpu.mem_write(TAP_BUFFER + 192, struct.pack('<I', 1))
        lab.run(1000 + tick)
        for irq, timer in ((36, CTIMER2),) * 4 + ((13, CTIMER3),):
            lab.cpu.mem_write(timer, struct.pack('<I', 1)); lab.irq.pend(irq)
            verify(lab.irq.service() == 1, 'Motor IRQ did not complete')
            pwm.append(read_sensa_pwm(lab.cpu, lab.registers))
        verify(lab.cpu.mem_read(source, 192) == pcm, 'Compiled publisher changed audio PCM')

    result = check_continuous_dma(image, dsp, blocks, replay=lab.replay, on_block=block, tail_ticks=64,
                                  irq_dispatch=lambda status, bit: acknowledge(lab.replay, lab.irq, lab.dma, status, bit, True))
    verify(lab.values()[5] > 1 and lab.values()[10] == 1 and lab.values()[1] == 0, 'Compiled continuous path did not play and stop')
    verify(any(row[0] != 120 for row in pwm) and any(row[1] != 120 for row in pwm), 'Compiled palm positive control is silent')
    verify(all(row[2:] == (0, 0) for row in pwm), 'Compiled path activated a trigger output')
    return {**result, 'compiled_frames': lab.values()[5], 'stop_frames': lab.values()[10],
            'checked_returns': lab.irq.checked_returns, 'pwm_snapshots': len(pwm),
            'peak_masked_instruction_observations': lab.masked_peak,
            'host_constructed_candidate_frames': 0}


def captured_window(path, start):
    records = json.loads(path.read_text(encoding='utf-8'))
    verify(isinstance(records, list) and start >= 0 and start + 96 <= len(records) <= 15000,
           'Missing bounded active captured-PCM excerpt')
    selected = records[start:start + 96]
    blocks = [bytes.fromhex(row['payload_hex']) for row in selected]
    verify(all(len(block) == 192 for block in blocks), 'Captured PCM slot is not stereo PCM16/48k')
    verify(all(1 <= row['sequence'] <= 255 for row in selected) and
           all(b['sequence'] == a['sequence'] % 255 + 1 for a, b in zip(selected, selected[1:])),
           'Captured PCM excerpt has a sequence gap')
    return blocks, {'source_trace_sha256': hashlib.sha256(path.read_bytes()).hexdigest(), 'start_slot': start,
                    'payload_sha256': hashlib.sha256(b''.join(blocks)).hexdigest(),
                    'scope': 'Captured PCM payload bytes; GIP sequence and schedule regenerated by the fixture'}


def corruption_guards(image, dsp):
    rejected = []
    for source in ('audio', 'game'):
        lab = Lab(image, dsp); fired = []

        def damage(cpu, *args):
            if fired: return
            fired.append(True)
            byte = cpu.mem_read(STOCK_FRAME, 1)[0]
            cpu.mem_write(STOCK_FRAME, bytes([byte ^ 1]))

        if source == 'audio':
            lab.feed()
            lab.cpu.hook_add(UC_HOOK_MEM_WRITE, damage, begin=FLAG, end=FLAG)
        else:
            lab.packet()
            lab.cpu.hook_add(UC_HOOK_CODE, damage, begin=0x30F36, end=0x30F36)
            lab.send()
        try:
            lab.run()
        except RuntimeError as error:
            expected = 'Runtime compiled audio packet differs' if source == 'audio' else 'Game packet changed after original publication'
            verify(str(error).startswith(expected), f'Unexpected corruption failure: {error}')
        else:
            raise RuntimeError('Corrupt publication was falsely accepted')
        verify(fired, 'Corruption guard did not inject its fault')
        rejected.append(source + '_publication_corruption')
    return rejected


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('stock', type=Path); parser.add_argument('dsp', type=Path)
    parser.add_argument('--wired-pcm-trace', type=Path)
    parser.add_argument('--dongle-pcm-trace', type=Path)
    parser.add_argument('--captured-excerpt-start', type=int, default=3000)
    args = parser.parse_args(); image, dsp = args.stock.read_bytes(), args.dsp.read_bytes()
    if bool(args.wired_pcm_trace) != bool(args.dongle_pcm_trace): parser.error('Supply both captured PCM traces')
    verify(hashlib.sha256(image).hexdigest() == STOCK_SHA256, 'Expected pinned Dev2FW 2.04.00')
    verify(len(dsp) > GAME_NOTICE - 0x100000, 'Rebuild the module with compiled publication')
    print('Compiled encoder contracts', file=sys.stderr, flush=True)
    encoder = encoder_contracts(image, dsp)
    print('Ownership, controls and IRQ interleavings', file=sys.stderr, flush=True)
    controls = control_cases(image, dsp); races = interleavings(image, dsp)
    command_contracts = game_hook_contract(image, dsp)
    guards = corruption_guards(image, dsp)
    print('Compiled DMA-to-PWM path', file=sys.stderr, flush=True)
    streams = {'synthetic_stereo': continuous(image, dsp)}
    for name, path in (('wired_capture', args.wired_pcm_trace), ('dongle_capture', args.dongle_pcm_trace)):
        if path is not None:
            print('Compiled path: ' + name, file=sys.stderr, flush=True)
            blocks, provenance = captured_window(path, args.captured_excerpt_start)
            streams[name] = {**continuous(image, dsp, blocks), **provenance}
    print(json.dumps({'scope': 'offline compiled Xbox publication with modeled events and original command/decoder code',
                      'encoder': encoder, 'controls': controls, 'interleavings': races, 'continuous': streams,
                      'original_game_command_hook_contracts': command_contracts,
                      'negative_controls': guards,
                      'game_input_provenance': 'Synthetic command packet via a fixture IRQ shim calling original class 0x16; not a real game/USB IRQ',
                      'whole_device_equivalence_proven': False, 'flashable_candidate_ready': False,
                      'input_sha256': {'stock': hashlib.sha256(image).hexdigest(), 'dsp': hashlib.sha256(dsp).hexdigest()}}, indent=2))


if __name__ == '__main__': main()
