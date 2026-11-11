# Song roles

Status: **Draft**. Implements the "tracks are tagged with roles" part of
[ADR 0012](../design-docs/0012-humans-in-the-band.md). Code: `src/MetroDom.Core/Arranging/Roles.cs`
(`Roles`, `RoleDetector`, `RoleMapping`), used by `Arranger.Parts`/`Arrange`. Seats (a human
takes a role, the arranger mutes it) build on this and aren't done yet.

## Vocabulary

Detection only produces these: `drums`, `bass`, `guitar`, `keys`, `strings`, `brass`, `winds`,
`vocals`, `melody`, `other`. A saved mapping may use any one word (`cowbell`, `lead`), so a song
can be more specific than detection. (ADR 0012's open question, answered as: fixed list for
detection, free-form for people. Owner to confirm.)

## Where a part's role comes from (first match wins)

| Source | Rule | Shown as |
| --- | --- | --- |
| Saved mapping | `song.roles.json` next to `song.mid` (below) | `saved` |
| Channel 10 | General MIDI percussion | `channel 10` |
| Track name | Whole-word match, any case. "Bass Drum" → drums, "Bassoon" → winds, "Electric Piano" → keys, "Lead Vocal" → vocals, "Lead" → melody | `track name` |
| GM program | First Program Change on the part's channel, in its own track, else in any track. By family: pianos/organs/pads → keys, chromatic percussion and synth leads → melody, 24-31 guitar, 32-39 bass, strings/ensembles → strings, choir/voice → vocals, brass, reeds/pipes → winds, the rest → other | `GM program N` (1-based) |
| Nothing | | `nothing to go on` → `other` |

Names win over programs: a person typed the name, and many files leave every track on program 1.

## Saved mapping (`song.roles.json`)

```json
{
  "schema": "metrodom.song-roles/0",
  "parts": { "Lead Synth": "melody", "#3": "guitar", "#4:2": "bass" }
}
```

Keys pick parts by track name (any case), `#TRACK` (1-based), or `#TRACK:CHANNEL` (both 1-based,
for a track that plays on several channels). The most specific key wins. Roles are stored lower case.

## Commands

```bash
metrodom roles song.mid                                   # table: part, name, notes, role, from
metrodom roles song.mid --set "Lead Synth=melody" --set "#3=guitar"   # saves song.roles.json
metrodom roles song.mid --unset "#3"                      # back to detection
```

`arrange`, `play --arrange` and `simulate --arrange` load the mapping automatically and show each
part's role. A broken roles file is a usage error naming the file. A `--set` key that matches no
part is saved with a warning (the song may change).
