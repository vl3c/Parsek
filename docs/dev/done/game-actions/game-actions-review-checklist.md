# Game Actions & Resource System — Review Checklist

Feature-specific checks for reviewing each task. Apply the standard post-change checklist from CLAUDE.md first, then these additional checks.

## Ledger Correctness

- [ ] Immutable values (`scienceAwarded`, `fundsAwarded`, `nominalRep`, etc.) are never modified after creation
- [ ] Derived values (`effectiveScience`, `effectiveRep`, `affordable`, `effective`) are never stored — always recomputed
- [ ] Recalculation walk starts from UT=0 and processes ALL actions — no cached state survives between recalculations
- [ ] Sort order is correct: primary UT ascending, secondary earnings before spendings, tertiary sequence index
- [ ] First-tier modules (Science, Milestones, Contracts, Kerbals) resolve before second-tier (Funds, Reputation)
- [ ] Strategy transforms apply between first-tier and second-tier resolution

## Reservation System

- [ ] UT=0 reservation start for kerbals, contracts, strategies — never from the action's UT
- [ ] Spending reservation for science and funds: ALL committed spendings (entire timeline) reserved, not just spendings up to current UT
- [ ] `available(ut) = earnings up to ut - ALL spendings on timeline` — verify the formula
- [ ] Reservation blocks new spendings before allowing them (not after-the-fact detection)
- [ ] No-delete invariant holds: adding a recording never removes existing earnings or invalidates existing spendings

## Once-Ever Flags

- [ ] Milestones: chronologically first recording gets credit, later duplicates get `effective=false`
- [ ] Contract completions: same once-ever pattern
- [ ] Credit can shift on retroactive commit, but total credited count stays the same

## KSP State Patching

- [ ] Patching is separate from recalculation (pure computation vs impure mutation)
- [ ] Fund display uses `availableFunds` (after reservation), not `runningBalance`
- [ ] Science balance uses `availableScience`, not raw running science
- [ ] Patching targets the correct KSP singletons (`ResearchAndDevelopment.Instance`, `Funding.Instance`, `Reputation.Instance`, etc.)
- [ ] Patching happens on: commit, rewind (quickload), warp exit, KSP load

## Persistence

- [ ] Ledger file uses safe-write pattern (.tmp + rename)
- [ ] Float/double serialization uses `ToString("R", InvariantCulture)` — locale-safe
- [ ] KSP load reconciliation prunes orphaned earning actions and future spending actions
- [ ] Ledger file format is self-contained for recalculation (no sidecar reads needed)

## Module Isolation

- [ ] Modules do not call each other — data flow is one-directional (first-tier → second-tier)
- [ ] Each module owns its portion of the walk — no cross-module field access
- [ ] The only coupling between recording system and game actions is `recordingId`

## Agent Constraints

ALLOWED:
- Creating new files for new types and modules
- Adding fields to `Recording` class for commit-time data extraction
- Adding new event subscriptions in `FlightRecorder` for science/contract/milestone capture
- Adding new methods to `ParsekScenario` for ledger lifecycle management
- Extending test generators with game action builders

NOT ALLOWED:
- Modifying ghost playback, trajectory recording, or DAG structure code
- Changing serialization format of existing recording types
- Adding game action logic to `GhostPlaybackEngine` or `GhostVisualBuilder`
- Breaking the `recordingId`-only coupling between systems

**Conflict resolution:** If a change to the recording commit path is needed and it affects chain/DAG logic, escalate to orchestrator rather than modifying both systems in one task.
