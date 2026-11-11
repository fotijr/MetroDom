# Tech-debt tracker

One row per known issue. Remove the row in the same change that fixes it.
Severity: **H** blocks the roadmap or is unsafe · **M** wrong/fragile · **L** cleanup.

| ID | Sev | Where | Issue |
| --- | --- | --- | --- |
| TD-02 | M | `Instruments/*` | Legacy ASCII protocols, no host code drives them anymore. The old host sent `md?`. Solenoid firmware ignores it. Servo firmware replies to `?` with `Xylophone \| Servo` (no `Name:` prefix, no newline). Superseded by [ADR 0007](../design-docs/0007-instrument-discovery.md). Port, don't fix |
| TD-03 | H | `Instruments/*` | No watchdog / all-off on disconnect. Safety relies on host behaving |
| TD-18 | L | `.github/workflows/build.yml` | Linux CI pinned to `ubuntu-24.04`: `ubuntu-latest` (26.04, Azure kernel 7.0) had no `linux-modules-extra-$(uname -r)` package, so the ALSA loopback test can't load `snd-seq-dummy`. Retry `ubuntu-latest` later |
| TD-04 | M | `.github/workflows/build.yml` | Linux/macOS/Windows matrix not yet run on GitHub (verified locally on macOS and in a Linux container) |
| TD-10 | M | `Instruments/xylophone_solenoid` | `delay(45)` inside `serialEvent` blocks all input. Debug `Serial.print`s on the data channel |
| TD-12 | L | `docs/index.html` etc. | Legacy GitHub Pages site duplicates an old README. Decide: regenerate from docs or delete |
| TD-15 | M | `metrodom play/route` | Not yet exercised against a real MIDI instrument. Linux ALSA send/receive is covered by a CI loopback test through the kernel's virtual Midi Through port. Still needs a USB-MIDI device and a Pi. The live status panel and interactive port picker have only been checked by unit render tests, not watched during real playback |
| TD-17 | L | `src/MetroDom.Conductor/Midi/ResilientPorts.cs` | Reconnect matches ports by name, so two identical devices with the same name can't be told apart. Fix with USB serial / SysEx Identity ([ADR 0007](../design-docs/0007-instrument-discovery.md)). Unplug is found by polling every 1 s; ALSA announce events could make it instant |
