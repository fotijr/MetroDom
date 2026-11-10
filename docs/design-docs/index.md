# Design docs

Architecture Decision Records (ADRs) plus the beliefs every decision must respect.

- [core-beliefs.md](core-beliefs.md): non-negotiable principles. Read first.

| # | Decision | Status |
| --- | --- | --- |
| 0001 | [MIDI is the contract at the instrument boundary](0001-midi-is-the-contract.md) | Proposed |
| 0002 | [Conductor is a headless, cross-platform service](0002-headless-conductor.md) | Proposed |
| 0003 | [One Control API for every app](0003-control-api.md) | Proposed |
| 0004 | [Latency compensation via per-instrument lookahead](0004-latency-compensation.md) | Proposed |
| 0005 | [Deployment modes are policy, not forks; local-first](0005-deployment-modes-are-policy.md) | Proposed |

## Writing a new ADR
Copy the shape of an existing one: **Status, Context, Decision, Consequences, Open questions**.
Number sequentially, add a row above, and link it from wherever it matters
(usually [ARCHITECTURE.md](../../ARCHITECTURE.md)). Status moves
Proposed → Accepted (owner sign-off) → Superseded by NNNN. Never delete an ADR. Supersede it.
