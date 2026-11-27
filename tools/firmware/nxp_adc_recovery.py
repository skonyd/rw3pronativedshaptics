"""ADC startup/recovery subset with explicit status-read fixtures, not silicon time."""
import struct

from unicorn import UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE, UC_MEM_READ, UC_MEM_WRITE
from nxp_adc_sequence_b import ADC, CONTROL, DATA8, CONFIG, ENABLE, START, AdcSequenceB
from simulate_audio_haptics import verify

CTRL, INSEL, SEQA, TRM, STARTUP, CALIB = (ADC + n for n in (0, 4, 8, 0x64, 0x6C, 0x70))
POWER, POWER_SET, POWER_CLEAR = 0x40000610, 0x40000620, 0x40000630
ADC_POWER = 0x400


class RecoveringAdc(AdcSequenceB):
    """Only original 0x90E0/0x8F1C setup plus the existing channel-8 reader.

    Completion is supplied after N status reads. None means no completion.
    Sequence A can be configured; attempting an A conversion still rejects.
    ADC power loss cancels the explicit pending B conversion and clears ENA.
    """
    def __init__(self, cpu, samples, *, ready_after=0, calibration_required=True,
                 startup_enabled=True, calibration_after=(0,) * 4, dummy_after=(0,) * 4):
        verify(type(calibration_required) is bool and type(startup_enabled) is bool,
               'Explicit ADC startup/calibration flags required')
        for schedule in (calibration_after, dummy_after):
            verify(isinstance(schedule, (tuple, list)) and 0 < len(schedule) <= 16 and
                   all(x is None or type(x) is int and 0 <= x <= 16 for x in schedule),
                   'Invalid ADC calibration/dummy status-read schedule')
        self.cpu = cpu
        self.registers = {CTRL: 0, INSEL: 3, SEQA: 0, TRM: 0, STARTUP: 0, CALIB: 0}
        self.power = 0x880640  # Explicit supplied power state; not a boot result.
        self.calibration_required, self.startup_enabled = calibration_required, startup_enabled
        self.calibration_after, self.dummy_after = tuple(calibration_after), tuple(dummy_after)
        self.requests, self.status_reads, self.power_writes, self.aborted = [], [], [], []
        self.busy, self.busy_reads, self.dummy_while_calibration_required = {}, {}, 0
        super().__init__(cpu, samples, ready_after=ready_after)
        cpu.hook_add(UC_HOOK_MEM_READ, self.power_read, begin=POWER, end=POWER + 3)
        cpu.hook_add(UC_HOOK_MEM_WRITE, self.power_write, begin=POWER_SET, end=POWER_CLEAR + 3)

    @staticmethod
    def validate(access, address, size):
        reads = (CTRL, INSEL, SEQA, CONTROL, DATA8, STARTUP, CALIB)
        writes = (CTRL, INSEL, SEQA, CONTROL, TRM, STARTUP, CALIB)
        verify(size == 4 and (access == UC_MEM_READ and address in reads or
                             access == UC_MEM_WRITE and address in writes), 'Unmodeled recovery ADC register/access')

    def power_read(self, cpu, access, address, size, value, data):
        verify(address == POWER and size == 4, 'Unsupported ADC power read')
        cpu.mem_write(address, struct.pack('<I', self.power))

    def power_write(self, cpu, access, address, size, value, data):
        verify(size == 4 and (address == POWER_SET and value == ADC_POWER or
                             address == POWER_CLEAR and value in (0x80000, ADC_POWER, 0x200, 0x800000, 0x40)),
               'Unsupported recovery power write')
        self.power_writes.append((address, value))
        if address == POWER_SET:
            self.power |= value
            self.registers[STARTUP] = 0
            self.busy.clear()
            if self.pending:
                self.aborted.append(self.index)
                self.index += 1
                self.pending = False
        else:
            self.power &= ~value

    def request(self, kind):
        verify(not self.power & ADC_POWER and self.registers[STARTUP] & 1,
               'ADC initialization request without enabled powered ADC')
        # A repeated request after a timed-out attempt explicitly restarts it.
        schedule = self.calibration_after if kind == 'calibration' else self.dummy_after
        count = sum(r == kind for r in self.requests)
        verify(count < len(schedule), 'Exhausted ADC initialization completion fixture')
        self.requests.append(kind)
        self.busy[kind], self.busy_reads[kind] = schedule[count], 0

    def write(self, cpu, access, address, size, value, data):
        self.validate(access, address, size)
        if address == CONTROL:
            if value & START:
                verify(not self.power & ADC_POWER and self.registers[STARTUP] & 1 and
                       not self.calibration_required and not self.busy,
                       'ADC conversion before successful startup/calibration')
            super().write(cpu, access, address, size, value, data)
            return
        if address == STARTUP:
            verify(value in (1, 3) and not self.power & ADC_POWER, 'Unsupported/unpowered ADC startup')
            if value == 1:
                self.registers[STARTUP] |= 1 if self.startup_enabled else 0
            else:
                verify(self.registers[STARTUP] & 1, 'Dummy conversion before ADC enable')
                if self.calibration_required: self.dummy_while_calibration_required += 1
                self.request('dummy')
                self.registers[STARTUP] |= 2
        elif address == CALIB:
            verify(value == 1 and self.calibration_required, 'Unsupported/unneeded ADC calibration request')
            self.request('calibration')
        else:
            allowed = {CTRL: (0x665F,), INSEL: (0,), SEQA: (0x400402F8, 0xC00402F8), TRM: (0,)}
            verify(value in allowed[address], 'Unsupported recovery ADC configuration')
            self.registers[address] = value

    def read(self, cpu, access, address, size, value, data):
        self.validate(access, address, size)
        if address in (CONTROL, DATA8):
            super().read(cpu, access, address, size, value, data)
            return
        kind = 'calibration' if address == CALIB else 'dummy' if address == STARTUP else None
        if kind in self.busy:
            after = self.busy[kind]
            if after is not None and self.busy_reads[kind] >= after:
                if kind == 'calibration': self.calibration_required = False
                else: self.registers[STARTUP] &= ~2
                del self.busy[kind]
            self.busy_reads[kind] += 1
        if address == CALIB:
            word = (2 if self.calibration_required else 0) | (1 if 'calibration' in self.busy else 0)
        else: word = self.registers[address]
        if address in (STARTUP, CALIB): self.status_reads.append((address, word))
        cpu.mem_write(address, struct.pack('<I', word))
