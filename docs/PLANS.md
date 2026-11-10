# Plans

Plans are first-class, versioned artifacts. An agent picking up work should be able to continue
from the plan alone.

## Where things live
- `exec-plans/active/`: in-flight plans. One file per initiative: `NNNN-short-name.md`.
- [exec-plans/completed/](exec-plans/completed/README.md): finished plans, moved here (not deleted) with an outcome note.
- [exec-plans/tech-debt-tracker.md](exec-plans/tech-debt-tracker.md): known debt, one row each.

## When to write one
Any change that spans multiple sessions, multiple components, or needs an owner decision.
Small fixes don't need a plan: just a clear commit message and a tech-debt row removed.

## Plan shape
```
# NNNN: Title
Status: Active | Blocked (on X) | Done   ·   Last updated: YYYY-MM-DD
## Goal          what done looks like, measurable
## Context       links to ADRs/specs. Don't restate them
## Phases        checklists. Check items off as you go
## Decisions log dated one-liners: what was decided and why
## Open questions things that need the owner
```

## Active plans
- [0001: Foundation reboot](exec-plans/active/0001-foundation-reboot.md)
- [0002: Hardware bootstrap](exec-plans/active/0002-hardware-bootstrap.md)
