"""Strict synthetic byte-pin inputs for the stock LPC546xx foreground scanner.

Only explicitly defined B[port][pin] inputs are supported. GPIO outputs,
interrupts, pin mux, electrical behavior and other aliases remain unmodeled.
"""
from collections import Counter

from unicorn import UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE

from simulate_audio_haptics import verify


GPIO = 0x4008C000


class GpioByteInputs:
    def __init__(self, cpu, pins):
        verify(all(type(pin) is int and 0 <= pin < 192 and type(value) is int and value in (0, 1)
                   for pin, value in pins.items()), 'Invalid explicit GPIO input fixture')
        self.pins, self.reads = dict(pins), Counter()
        if not any(first <= GPIO <= last for first, last, _ in cpu.mem_regions()):
            cpu.mem_map(GPIO, 4096)
        cpu.hook_add(UC_HOOK_MEM_READ, self.read, begin=GPIO, end=GPIO + 0x2FFF)
        cpu.hook_add(UC_HOOK_MEM_WRITE, self.write, begin=GPIO, end=GPIO + 0x2FFF)

    def read(self, cpu, access, address, size, value, data):
        pin = address - GPIO
        verify(size == 1 and pin in self.pins, f'Unmodeled GPIO input read: 0x{address:X}, width {size}')
        value = self.pins[pin]
        verify(type(value) is int and value in (0, 1), 'GPIO input changed to an invalid level')
        self.reads[pin] += 1
        cpu.mem_write(address, bytes([value]))

    def write(self, cpu, access, address, size, value, data):
        raise RuntimeError(f'GPIO output/configuration is outside the byte-input fixture: 0x{address:X}')
