"""Connect the stock B91 RF DMA transmit and receive boundaries offline."""
import struct

from unicorn import UC_HOOK_MEM_WRITE
from unicorn.riscv_const import UC_RISCV_REG_RA, UC_RISCV_REG_SP

from replay_telink_startup import require
from replay_telink_usb import run_until


TX_ENTRY, TX_STATE, TX_SLOTS = 0x2FA4, 0x8E060, 0x8E170
TX_DMA_SOURCE, DMA_RAM_ALIAS = 0x80100448, 0xC0180000
RX_IRQ, RX_IRQ_STATUS, RX_LENGTH_LIMIT = 0x118A0, 0x80140A20, 0x80140831
RX_STATE, RX_BUFFER, RX_SLOTS = 0x874F4, 0x936CC, 0x87644
SLOT_BYTES, SLOT_COUNT = 128, 8
SLOT_STATUS_OFFSET = SLOT_BYTES - 1
AUDIO_OFFSET, AUDIO_BYTES, AIR_LENGTH = 13, 70, 77


def transmit_audio(cpu, index, payload):
    """Run stock TX selection and return the bytes its DMA source points to."""
    address = TX_SLOTS + SLOT_BYTES * index
    require(cpu.mem_read(address + AUDIO_OFFSET, AUDIO_BYTES) == payload,
            "Stock TX slot does not already contain the encoded audio")
    # Supply the negotiated link and scheduled counter, and model completion
    # of any previous transmission before requesting another stock packet.
    cpu.mem_write(TX_STATE + 8, struct.pack("<H", index << 3))
    cpu.mem_write(TX_STATE + 0x24, bytes(2))
    cpu.mem_write(TX_STATE + 0x41, b"\x20")
    cpu.mem_write(TX_STATE + 0x4C, struct.pack("<I", 1))
    cpu.reg_write(UC_RISCV_REG_RA, 0x700)
    run_until(cpu, TX_ENTRY, 0x700, vendor_budget=4096)

    dma, = struct.unpack("<I", cpu.mem_read(TX_DMA_SOURCE, 4))
    require(dma - DMA_RAM_ALIAS == address, "Stock TX selected an unexpected RF DMA source")
    frame = bytes(cpu.mem_read(dma - DMA_RAM_ALIAS, AUDIO_OFFSET + AUDIO_BYTES))
    length_info, = struct.unpack_from("<I", frame)
    wire_bytes = AIR_LENGTH + 2
    expected_dma_length = (wire_bytes + 3) // 4 | (wire_bytes % 4) << 22
    require(length_info == expected_dma_length and frame[5] == AIR_LENGTH,
            "Unexpected stock RF DMA or on-air length")
    require(frame[12] == 0x28 | index and frame[AUDIO_OFFSET:] == payload,
            "Stock RF packet has the wrong slot tag or codec bytes")
    return frame


def run_receive_irq(cpu, stack, frame, *, pending=True, crc_error=False, max_length=AIR_LENGTH, timestamp=0):
    """Deliver the exact frame bytes with modeled hardware RX metadata."""
    require(frame[5] == AIR_LENGTH and len(frame) == 6 + AIR_LENGTH, "Unexpected RF input frame")
    # RX has its own DMA prefix. The modeled CRC bytes, frequency offset and
    # RSSI are zero. Supply the timestamp and CRC status separately.
    metadata = struct.pack("<3xI3xB", timestamp, int(crc_error))
    cpu.mem_write(RX_BUFFER, bytes(4) + frame[4:] + metadata)
    cpu.mem_write(RX_STATE + 0x8C, struct.pack("<I", RX_BUFFER))
    cpu.mem_write(RX_LENGTH_LIMIT, bytes([max_length]))
    cpu.mem_write(RX_IRQ_STATUS, struct.pack("<H", int(pending)))
    cpu.reg_write(UC_RISCV_REG_SP, stack)
    cpu.reg_write(UC_RISCV_REG_RA, 0x500)
    acknowledgements = []
    queue_writes = []
    status_hook = cpu.hook_add(UC_HOOK_MEM_WRITE,
                               lambda _, access, address, size, value, data: acknowledgements.append((size, value)),
                               begin=RX_IRQ_STATUS, end=RX_IRQ_STATUS + 1)
    queue_hook = cpu.hook_add(UC_HOOK_MEM_WRITE,
                              lambda _, access, address, size, value, data: queue_writes.append(address),
                              begin=RX_SLOTS, end=RX_SLOTS + SLOT_COUNT * SLOT_BYTES - 1)
    try:
        run_until(cpu, RX_IRQ, 0x500, vendor_budget=16_384)
    finally:
        cpu.hook_del(queue_hook)
        cpu.hook_del(status_hook)
    allowed = ([(2, 1)], [(2, 1), (2, 0xFFFF)]) if pending else ([],)
    require(acknowledgements in allowed,
            f"Stock receive interrupt with pending={pending} wrote {acknowledgements} to its status register")
    cpu.mem_write(RX_IRQ_STATUS, bytes(2))
    return bool(queue_writes)


def receive_audio(cpu, stack, frame, *, timestamp=0):
    index = frame[12] & (SLOT_COUNT - 1)
    slot = RX_SLOTS + index * SLOT_BYTES
    before = bytes(cpu.mem_read(RX_SLOTS, SLOT_COUNT * SLOT_BYTES))
    require(run_receive_irq(cpu, stack, frame, timestamp=timestamp), "Stock RF receiver ignored a fresh audio packet")
    expected = bytearray(before)
    offset = index * SLOT_BYTES
    expected[offset:offset + AUDIO_BYTES] = frame[AUDIO_OFFSET:]
    expected[offset + SLOT_STATUS_OFFSET] = 1
    require(cpu.mem_read(RX_SLOTS, len(expected)) == expected,
            "Stock RF receiver wrote unexpected bytes or selected a different queue slot")
    return slot


def check_receive_rejections(cpu, stack, frame):
    """Check the actual IRQ guards with known nonzero packet data."""
    for options in ({"pending": False}, {"crc_error": True}, {"max_length": AIR_LENGTH - 1}):
        require(not run_receive_irq(cpu, stack, frame, **options),
                f"Stock receiver wrote audio while it should reject {options}")
