# Glossary

- **Actuator**: the thing that moves: solenoid, servo, stepper, motor.
- **Arranger**: Conductor component mapping song tracks/channels onto available instruments.
- **Click track**: a fixed metronome performers lock to. MetroDom avoids requiring one.
- **Conductor**: the headless service that runs the band (today: the `metrodom` CLI).
- **Control API**: the single HTTP/WebSocket/OSC interface all apps use.
- **Control surface**: anything that sends intents to the Conductor: app, keyboard, pad.
- **Instrument**: one physical instrument + microcontroller + firmware, presented as a MIDI device.
- **Instrument profile**: self-description of an instrument's notes, latency and limits.
- **Lookahead**: sending a note early by the instrument's latency so it sounds on time.
- **Panic**: immediate all-off of every actuator.
- **Policy profile**: config defining a deployment mode's roles, queue rules and limits.
- **SMF**: Standard MIDI File (`.mid`).
- **Watchdog**: firmware timer that switches actuators off if the host goes silent.
