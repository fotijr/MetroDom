# Core beliefs

These rules outrank convenience. If a change conflicts with one, stop and write an ADR.

1. **Acoustic, physical, live.** Every note is a physical event. The software's job is to make
   robots play like musicians, not to sound like a recording.
2. **Humans own time.** No mandatory click track. Tempo is a live, human-controlled parameter
   (tap, MIDI clock in, follow a player). A fixed tempo is just one option.
3. **Open standards first.** MIDI 1.0 (and MIDI 2.0 where it helps), Standard MIDI Files, SysEx,
   RTP-MIDI / Network MIDI 2.0, OSC, JSON over HTTP/WebSocket. Invent a format only when no
   standard fits, and document it in `docs/references/`.
4. **Reuse before build.** Use existing MIDI libraries, class-compliant USB stacks, and OS MIDI
   services. Don't hand-roll what a maintained library already does.
5. **Instruments are dumb and safe.** Firmware maps MIDI to motion and protects its own hardware.
   It never trusts the host to turn things off.
6. **One brain.** Only the Conductor knows about multiple instruments, songs, users, or policy.
7. **One front door.** Every app uses the same Control API. Roles and policy, not separate code
   paths, decide what each app may do.
8. **Local-first.** The band plays with no internet. The cloud adds things but is never required.
9. **The owner can always stop it.** A panic/stop path exists from every owner surface and on the
   hardware itself. Nothing (queue, crowd, network) can block it.
10. **Repo is the source of truth.** Decisions, plans, protocols and known debt live in `docs/`.
    Knowledge in someone's head or a chat log doesn't exist for the next agent.
11. **No versioning until production.** Firmware versions (0.1), format bytes and schema numbers
    stay where they are while we refactor, even on dev hardware. Breaking changes land in place,
    with fixtures and docs updated in the same change and no code kept for old layouts. Versioning
    starts when the owner says MetroDom is in production (polished hardware playing gigs).
