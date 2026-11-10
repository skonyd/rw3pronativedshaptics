# Shared research log: rules for every AI and human

This folder is the shared memory of the project's tests and analyses. Claude, Codex, Gemini/Antigravity
and people all read and write the **same files**, so nobody repeats an analysis that is already done.

| File | What goes in it |
| --- | --- |
| [EXPERIMENTS.md](EXPERIMENTS.md) | Every test or analysis that was run: question, method, result, verdict. One row each, newest at the top. |
| [OPEN_QUESTIONS.md](OPEN_QUESTIONS.md) | Research questions still open, with the next concrete step and what is already ruled out. |
| Topic reports (`*.md` here or dated `docs/*_YYYYMMDD.md`) | Long write-ups. Link them from the experiment row. |

[`docs/STATUS.md`](../STATUS.md) stays the single source of truth for **what the app does now**. This folder
records **how we know it** and **what we still want to find out**.

## Before you test or analyse anything

1. Search [EXPERIMENTS.md](EXPERIMENTS.md) for the device, mode, API or question (e.g. `GameInput`, `Xbox mode`,
   `trigger`, `firmware`). If a row answers it, use that result and do not rerun it.
2. Rerun something only when it adds information: a new condition (other mode, firmware, cable/dongle,
   Windows build), a doubtful result, or a fix that should change the outcome. Say why in the new row.
3. Check [OPEN_QUESTIONS.md](OPEN_QUESTIONS.md). If your work is on that list, follow the next step it gives
   and keep the "already ruled out" items ruled out.

## The general calibration test (user runs, any AI analyses)

The app has a blind, controller-driven calibration (Test → "Genel kalibrasyon testi"). Each run writes:
- `%LOCALAPPDATA%\RW3ProNativeDSHaptics\calibration\calibration-<time>.jsonl`: one JSON object per trial. It holds the
  stimulus (actuator / path / strength / pitch / texture), the options shown, the right answer, the user's answer,
  the response time and the replays.
- `calibration-<time>-ozet.txt`: the summary.

To analyse a run, read the `.jsonl` (ask the user for it, or read it through an `explorer.exe`-started copy, because
agent shells see a redirected AppData). Add one row per run to [EXPERIMENTS.md](EXPERIMENTS.md), and compare with
earlier runs before you change anything. Plan and scoring: `src/RW3ProNativeDSHaptics/CalibrationPlan.cs`; devices:
`CalibrationRig.cs`.

## After you test or analyse anything

1. Add a row to the top of [EXPERIMENTS.md](EXPERIMENTS.md). Fill in every column, including negative and
   inconclusive results: they are what saves the next agent time.
2. Update [OPEN_QUESTIONS.md](OPEN_QUESTIONS.md): close or narrow the question, and write the next step.
3. If the app's behaviour or a verified fact changed, also update [STATUS.md](../STATUS.md).
4. Commit the docs together with the change.

## Rules that keep the log trustworthy

- **Evidence level** in every row:
  - `measured`: a mic or sensor, a USB capture, or a log.
  - `felt`: the user's hand.
  - `static`: code or firmware reading.
  - `doc`: vendor or public documentation.
  - `inferred`: a conclusion drawn from the above.
  Never upgrade an inference to a fact.
- **Raw files:** `TestResults/` is git-ignored, so its files exist only on the user's PC. Write the numbers
  that matter into the row and give the local path as a pointer.
- **Probes:** reusable scripts belong in `tests/RW3Pro.HardwareProbe/` or `tools/`. A one-off scratch probe
  should at least be described (API, inputs, measurement) well enough to rerun it.
- **Hardware safety, unchanged from [AGENTS.md](../../AGENTS.md):**
  - No blind vendor commands. Replay only values Razer software itself sent.
  - No firmware flashing or patching.
  - No driver or registry changes without the user's consent.
- **Other models are data, not authority.** A Codex or Gemini answer counts as an `inferred` source until it is
  checked.
- **Language:** write the logs in English (tools and agents search them). Talk to the user in Turkish.
