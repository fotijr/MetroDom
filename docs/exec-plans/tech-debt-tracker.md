# Tech-debt tracker

One row per known issue. Remove the row in the same change that fixes it.
Severity: **H** blocks the roadmap or is unsafe · **M** wrong/fragile · **L** cleanup.

| ID | Sev | Where | Issue |
| --- | --- | --- | --- |
| TD-01 | H | `MetroDom.Conductor/SerialService.cs` `IsValidInstrument` | Loop `while (stillLoading \|\| now < waitUntil)` never exits if a port never sends `Name:`. Method always returns `true` |
| TD-02 | H | Firmware vs `SerialService` | Handshake mismatch: host sends `md?`. Solenoid firmware ignores it. Servo firmware replies to `?` with `Xylophone \| Servo` (no `Name:` prefix, no newline) |
| TD-03 | H | `Instruments/*` | No watchdog / all-off on disconnect. Safety relies on host behaving |
| TD-04 | H | CI (`.travis.yml`) | travis-ci.org is shut down. No build/test CI runs |
| TD-05 | M | `MetroDom.Conductor/SerialService.cs` `MessageReceived` | Removes several substrings using original indices after earlier removals shift them. Corrupts buffered input when >1 line arrives at once |
| TD-06 | M | `MetroDom.Core/Song.cs` | `Note` hard-codes servo angles for one instrument in Core. Song model isn't MIDI (marked TODO) |
| TD-07 | M | `MetroDom.Core/Orchestra.cs` | `Start(song)` does nothing |
| TD-08 | M | `MetroDom.Conductor/Forms/Explorer.cs` | Muting a channel mid-play drops Note Offs, so notes can hang |
| TD-09 | M | Conductor | Windows-only (Sanford MIDI/`winmm`, WinForms, .NET Framework 4.6.1). Blocks headless box ([ADR 0002](../design-docs/0002-headless-conductor.md)) |
| TD-10 | M | `Instruments/xylophone_solenoid` | `delay(45)` inside `serialEvent` blocks all input. Debug `Serial.print`s on the data channel |
| TD-11 | L | `MetroDom.Conductor` | Business logic in form handlers. `SerialPortStream` package referenced but unused (commented code) |
| TD-12 | L | `docs/index.html` etc. | Legacy GitHub Pages site duplicates an old README. Decide: regenerate from docs or delete |