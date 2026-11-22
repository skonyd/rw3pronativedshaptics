# Stock watchdog and main-loop counter gate

Status: **prototype, offline only**, 2026-11-22. No hardware access or
firmware flashing. This extends the [stock-clock replay](STOCK_CLOCK_REPLAY.md)
using the same unchanged 6,206-byte compiled simulator module.

## Original code now exercised

The main loop at `0x2F100` first feeds the windowed watchdog through
`0x2985C`. It then checks a runtime flag and compares the MRT counter
`0x200139F4` with `0x2000B340`. An unchanged counter branches to the
background/USB path at `0x2F0E8`. A changed counter is stored and reaches
the first tick task at `0x2F136`. The replay executes this original
header, stopping at those two task boundaries. It does not substitute a
host branch decision or claim to run the rest of the loop.

Thirty-four headers exercise 17 changed and 17 unchanged cases. Sixteen
counter increments come from 128 original MRT IRQs. The header supplies
68 original feed stores, all under PRIMASK. The unrelated runtime flag
is clear in this initialized audio fixture; its active branch and the
rest of board startup remain open. Test calls still seed a stack/return
context and do not establish autonomous whole-main execution.

## Clock and stock configuration

The narrow earlier audio fixture left SYSCON `WDTOSCCTRL` at zero. That
is not its documented reset value. This new lab explicitly supplies
`0x140`: FREQSEL `0xA`, DIVSEL `0`, with the oscillator powered/ready.
Original frequency query `0xA9F4(10)` then returns nominal 800,000 Hz.
Zero oscillator configuration is rejected rather than accepted as
working watchdog protection. Other older fixture register defaults
are not silently upgraded by this separate test.

Original setup `0x29870` executes the stock SDK initializer and sets:

| Register / action | Observed in replay |
| --- | --- |
| MOD | `3`: enabled, reset mode, no protect/oscillator lock |
| TC | `400000` |
| WARNINT | `512` |
| WINDOW | `0xFFFFFF`: no early-feed restriction |
| Feed | `AA`, `55`, twice during the complete setup call |

The model uses supplied watchdog-oscillator cycles independently of
MRT interrupt delivery. It covers the fixed divide-by-four counter,
minimum TC clamp, feed activation, sticky enable/reset bits, WDTOF
write-zero-clear, WDINT write-one-clear and window faults. Access to
another watchdog register between `AA` and `55` is a feed fault.
Protect/lock, interrupt-only mode, live warning reconfiguration and
undefined register accesses fail explicitly.

The logical counter includes its zero slot: the tested expiry is
1,600,004 supplied WDCLK cycles after a load, approximately two seconds
at the nominal clock. This follows the inclusive interval convention;
it is not a silicon-level zero-edge or synchronization measurement.
The manual separately describes reaching zero and interval lengths.
APB/WDCLK propagation, TV read synchronization, feed phase and reset
latency remain unmodeled. The documented oscillator tolerance also
prevents treating the nominal time as a precise wall-clock guarantee.

## Safety cases and limits

| Case | Result in the bounded replay |
| --- | --- |
| No valid feed after WDEN, including bad writes | Counter never starts; enable alone provides no protection |
| Bad feed / intervening watchdog read / early window feed | Reset boundary requested |
| All NVIC delivery disabled, PRIMASK, or BASEPRI blocking the modeled sources | Watchdog expires while MRT counter RAM stays zero; 16,000 MRT expirations coalesce into one flag |
| Reset boundary | Execution stops; later CPU resume is rejected too. RAM, audio ownership and PWM are **not** falsely restored |
| Warning IRQ explicitly enabled, foreground stopped | Original IRQ0 at `0x29674` feeds WWDT itself; three warning events avoid reset while audio ownership remains set |
| Warning arrives during the compiled protected packet copy | IRQ defers under PRIMASK, then original handler returns with checked registers and the candidate publishes one frame |
| Original second-feed immediate corrupted in Unicorn only | Main-loop header detects a feed fault/reset; no device image is modified |

The warning-enabled test is important: the existence of a hardware
watchdog does not by itself prove a stalled foreground will be reset.
The full board's IRQ0 enable/priority path has not been closed. The test
supplies enabled and blocked conditions separately. In the enabled
condition only watchdog-clock events are supplied; it is not a full
simultaneous peripheral schedule.

When TV equals WARNINT, WDINT cannot yet be cleared. With no modeled
instruction time, the first stock handler reloads before TV advances;
a second immediate entry clears the retained flag. The test checks this
two-return result rather than forcing it into one event. It is a logical
model artifact/limit, not a claim about real interrupt counts.

Thirteen register checks, three reset controls, nine unsupported-input
guards and two original-code/clock negative controls run alongside
the main-loop, blackout and publication cases. Python optimization
does not remove the explicit checks. The full stage also runs under
`python -O` and its JSON is compared with the ordinary run.

This does **not** implement an independent 250 ms audio-only stop. A
reset request is not proof of bootloader recovery or physical actuator
silence. Next follow the two loop branches and actual IRQ0 configuration,
model relevant clock-domain timing, and choose a verified elapsed-time
source for audio safety without assuming the warning handler provides it.

## Reproduce and primary references

```powershell
$env:PYTHONPATH='TestResults/firmware-lab/deps'
python -B tools/firmware/replay_nxp_watchdog.py <Dev2FW.bin> <audio_haptics.bin>
```

- NXP UM10912 rev 2.4, section 7.5.79 and chapter 18, especially 18.5.2,
  18.6.1 and 18.6.3–6. The [NXP-hosted rev 1.7](https://community.nxp.com/pwmxy87654/attachments/pwmxy87654/lpc/58795/1/lpc546_series.pdf)
  also confirms oscillator reset fields. The locally inspected
  [rev 2.4 manual](https://www.zlgmcu.com/data/upload/file/Utilitymcu/UM10912-LPC546xx%20User%20manual.pdf)
  has SHA256 `e876022a4110ac3eee7105cd59236f80601e2f071468f4581b583759f7e52c22`;
  only our notes/tools are committed.
- NXP [WWDT driver](https://github.com/nxp-mcuxpresso/mcux-sdk/blob/master/drivers/wwdt/fsl_wwdt.c),
  [clock driver](https://github.com/nxp-mcuxpresso/mcux-sdk/blob/master/devices/LPC54605/drivers/fsl_clock.c)
  and [LPC54605 register header](https://github.com/nxp-mcuxpresso/mcux-sdk/blob/master/devices/LPC54605/LPC54605.h).
