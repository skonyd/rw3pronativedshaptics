# Synapse audio-profile USB study — 2026-11-08

## Permission/setup milestone

The user could not elevate Codex itself. A separate Windows-elevated, two-hour-limited
`tools/usb-profile-capture-worker.ps1` was successfully authorized through UAC. It accepts
only USBPcap inventory, single-address capture, stop and exit requests. It uses the already
installed USBPcap; no driver, service, firmware, privacy or security setting was changed.
The main Codex process remains unelevated. Tool sandbox escalation alone is not a Windows
administrator token.

Current enumeration identified the wired PC Wolverine `1532:0A4C` on `USBPcap6`, address11.
Do not reuse this address after reconnection without fresh enumeration. The preparation
capture contains2056 records, all address11, including the matching device descriptor and
1025 Sensa SET_REPORT transfers. This establishes capture access, not physical equivalence.

## UI observations and retained settings

Actual Chroma UI inspection showed:

| Preset | Audio cue ranges / displayed gain |
|---|---|
| Controlled |30–130Hz /70%|
| Balanced |30–150Hz /70%|
| Dynamic |30–100Hz /30%;100–200Hz /10%|

These settings alone do not specify Synapse's algorithm. Original Custom was based on
Controlled, one30–130Hz/70% cue. Backup:
`TestResults/audio-profile-integration-20261108/synapse-custom-backup.json`.
**Restoration remains required:** inspection copied Dynamic into Custom; active preset is
currently Controlled for the USB study. Restore original Custom at the end, after stopping
the active source/capture. Do not change profiles during an active series.

## Executed microphone-free pilot

`UsbAudioStudyProbe` opens only the default render endpoint, never a microphone, Razer
runtime, virtual controller, HidHide or RW3 motor writer. Deterministic synthetic front audio
is played; Synapse alone converts it to controller commands. This is not native/game-authored
DualSense PCM. Original48k float source hashes and explicitly resampled endpoint playback
hashes, endpoint/mute/volume and UTC scene bounds are recorded. Every scene includes lead,
drive and an additional two seconds of digital silence in the same playback stream.

Controlled pilot completed6/6 cases (silence,80/190Hz, left/right, short hits). Its PCAP has
428 records, all address11, and211 Sensa frames; all211 pass packet checksum validation and
the strict compact-frame structural decoder. Left/right numeric amplitude isolation appears
in the respective unilateral scenes. No calibrated physical measurement was performed.

The current Synapse Audio-to-Haptics stream uses compact `A1` frames (15 bytes for this
single-cue pilot;28 bytes for the preceding two-cue preparation recording), distinct from the
older41-byte `79` HAR frames. Observed `A1` structure: seven header bits, three presence bits
per hand,52 bits of points only for present bands; zero byte-padding. Each present payload
has four6-bit amplitude/7-bit pitch-code pairs. **Pitch-code-to-Hz semantics and other A1
variants are not established.** Do not apply the old79 frequency calibration automatically,
or claim that a successful structural decode proves waveform interpretation/reproduction.
`analyze-sensa-usb.py` retains raw reports and rejects unknown/truncated/nonzero-tail variants.
Checksum uses Razer-packet-relative bytes2..87, equivalently report-relative3..88; transaction
byte is excluded. This clarifies ambiguous report/packet offsets in older notes.

## Running wider series / pending work

The initial wider preparation run was deliberately stopped and retained as partial while
correcting the source-run time budget (306 × approximately4.2s exceeds20min). It must not
be counted as a complete full series. Corrected source timeout is30min.

Controlled full-v2:102 synthetic scenes ×3 repeats, deterministic shuffled order. Artifacts:
`TestResults/usb-profile-study-20261108/controlled-full-v2/` and
`admin/controlled-full-v2.pcap`. **Started, completion not yet verified at this snapshot.**
`complete-usb-study.ps1` waits for its completion file with a32min bound, stops only the
matching named capture, then runs offline decoding and records finalization. It never
changes Synapse profiles. The source completion and finalization files must both be checked;
process launch is not a successful-study result.

Pending: finish/validate Controlled; record Balanced, Dynamic and Custom with unchanged
source/endpoint/intensity and negative controls; establish compact pitch semantics, onset,
release, gains, multi-tone behavior and repeated variability; test bounded byte-identical
reference replay separately from a general audio converter. Then add selectable/custom
profiles while preserving existing Standard/Hybrid/Multiband and Audio/Xbox trigger ownership.
## Completed series and local presets

Completion records confirm 306 cases each for Controlled and Balanced, plus 123 complex
cases each for Controlled, Balanced and Dynamic. The Custom experiment stopped after seven
cases. Dynamic finalization confirms capture stop and successful offline analysis.

RW3 now offers Existing, Controlled, Balanced and Dynamic below the Audio mode card.
Existing bypasses the new filter and is the default for older settings. The three presets
apply the observed cue ranges and displayed gains from the table above as local stereo
high-pass/low-pass cascades at 48 kHz. Dynamic sums its two weighted bands. Filtering runs
on the audio delivery thread before the PC Sensa or XInput body mapping. The normal mode
and Native DualSense source are separate. Standard/Hybrid/Multiband remain output methods.
The Audio demo uses the selected profile on either Wolverine mode.

Contracts cover 145 Hz Controlled/Balanced differentiation, Dynamic's second band and
out-of-band rejection, stereo isolation, nonfinite samples and clearing filter history.
The ranges/gains come from the observed cues. The profile filters do not implement the
captured A1 pitch semantics or Synapse's onset/release behavior. The same-source physical
comparison is pending. Custom editing is deferred by the user.
