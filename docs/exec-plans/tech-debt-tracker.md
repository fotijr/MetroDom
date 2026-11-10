# Tech-debt tracker

One row per known issue. Remove the row in the same change that fixes it.
Severity: **H** blocks the roadmap or is unsafe · **M** wrong/fragile · **L** cleanup.

| ID | Sev | Where | Issue |
| --- | --- | --- | --- |
| TD-02 | M | `Instruments/*` | Legacy ASCII protocols, no host code drives them anymore. The old host sent `md?`. Solenoid firmware ignores it. Servo firmware replies to `?` with `Xylophone \| Servo` (no `Name:` prefix, no newline). Superseded by [ADR 0007](../design-docs/0007-instrument-discovery.md). Port, don't fix |
| TD-03 | H | `Instruments/*` | No watchdog / all-off on disconnect. Safety relies on host behaving |
| TD-04 | M | `.github/workflows/build.yml` | Linux/macOS/Windows matrix not yet run on GitHub (verified locally on macOS and in a Linux container) |
| TD-10 | M | `Instruments/xylophone_solenoid` | `delay(45)` inside `serialEvent` blocks all input. Debug `Serial.print`s on the data channel |
| TD-12 | L | `docs/index.html` etc. | Legacy GitHub Pages site duplicates an old README. Decide: regenerate from docs or delete |
| TD-14 | H | `src/MetroDom.Conductor/Midi/` | No MIDI device I/O on Linux (DryWetMIDI devices are Windows/macOS only). The target box is Linux. Needs an ALSA adapter behind `IMidiOutput` |
| TD-15 | M | `metrodom play/route` | Not yet exercised against a real MIDI device (no device on the dev machine). Test with a USB-MIDI instrument or a virtual port |
