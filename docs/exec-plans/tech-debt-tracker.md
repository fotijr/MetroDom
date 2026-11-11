# Tech-debt tracker

One row per known issue. Remove the row in the same change that fixes it.
Severity: **H** blocks the roadmap or is unsafe · **M** wrong/fragile · **L** cleanup.

| ID | Sev | Where | Issue |
| --- | --- | --- | --- |
| TD-02 | M | `Instruments/*` | Legacy ASCII protocols, no host code drives them anymore. The old host sent `md?`. Solenoid firmware ignores it. Servo firmware replies to `?` with `Xylophone \| Servo` (no `Name:` prefix, no newline). Superseded by [ADR 0007](../design-docs/0007-instrument-discovery.md). Port, don't fix |
| TD-03 | H | `Instruments/*` | No watchdog / all-off on disconnect. Safety relies on host behaving |
| TD-18 | M | `.github/workflows/build.yml` | GitHub-hosted Ubuntu runners (24.04 and 26.04, Azure kernel 7.0) have no `linux-modules-extra-$(uname -r)` package, so `snd-seq-dummy` can't be loaded and the ALSA loopback test skips with a warning. Linux ALSA send/receive is currently verified only locally (Linux container). Fix: a self-hosted runner or a Pi, or retry when the package appears |
| TD-04 | M | `.github/workflows/build.yml` | Linux/macOS/Windows matrix not yet run on GitHub (verified locally on macOS and in a Linux container) |
| TD-10 | M | `Instruments/xylophone_solenoid` | `delay(45)` inside `serialEvent` blocks all input. Debug `Serial.print`s on the data channel |
| TD-12 | L | `docs/index.html` etc. | Legacy GitHub Pages site duplicates an old README. Decide: regenerate from docs or delete |
| TD-15 | M | `metrodom play/route` | Not yet exercised against a real MIDI instrument. Linux ALSA send/receive has a loopback test through the kernel's virtual Midi Through port, but hosted CI can't load it today (TD-18). Still needs a USB-MIDI device and a Pi. The live status panel and interactive port picker have only been checked by unit render tests, not watched during real playback |
| TD-19 | M | `metrodom virtual` | ALSA virtual ports have never run (written on macOS; the round-trip test runs only where the sequencer exists, TD-18). The CoreMIDI round trip skips on hosted CI (`CI` set) because headless runners aren't known to have a MIDI server. Windows has no virtual ports: users need loopMIDI |
| TD-20 | L | `metrodom play` | Tap/nudge keys are read in the status view and covered only by unit tests of `TapTempo` and `SetSpeed`, not by driving a terminal. Tap from a MIDI pedal / MIDI Clock in isn't built. Tempo changes rescale the file's tempo map (a file tempo change mid-song still applies, multiplied) |
| TD-17 | L | `src/MetroDom.Conductor/Midi/ResilientPorts.cs` | Reconnect matches ports by name, so two identical devices with the same name can't be told apart. Fix with USB serial / SysEx Identity ([ADR 0007](../design-docs/0007-instrument-discovery.md)). Unplug is found by polling every 1 s; ALSA announce events could make it instant |
