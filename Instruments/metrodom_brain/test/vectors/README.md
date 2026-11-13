# Brain parity vectors

Timed MIDI in, expected strikes out. The brain's host test (`make -C ..`) and the Conductor's
`BrainModel` (`dotnet test`) both run every `*.vectors` file here, so the software brain behind
`metrodom virtual --body` and `simulate --body` can't drift from the firmware
([plan 0006](../../../../docs/exec-plans/active/0006-new-instrument-dev-loop.md) N3).

```
body ../../bodies/m1-xylophone.hex     the body record, relative to this folder
# a comment, on its own line
0 on 72 127 struck 30                  ms, Note On (omni), velocity, then what the brain does:
0 on 72 127 struck 30 #330C00            struck PULSE_MS [flash color], busy, interval, duty,
10 on 72 127 busy                        polyphony, notmine, or none (velocity 0)
30 is-on 72 no                         is its actuator inside a pulse? (yes/no)
79 is-lit 72 yes                       are its accent LEDs lit? (yes/no)
705 panic                              CC 123: everything off
```

Each line runs at its time after the brain's loop has ticked at that time. Times only go forward.
Add a file when you change what the brain decides, and run both test suites.
