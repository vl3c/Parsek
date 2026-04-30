# Parsek Architecture (Index)

High-level orientation for anyone new to the codebase. This doc deliberately stays short: it shows the component picture and points at the live, per-subsystem design docs that hold the real detail. When details rot, they rot there — not here.

Historical note: the original 0.4.3-era architecture spec (class-level pseudo-code) now lives at [`dev/done/parsek-architecture-v0.4.3.md`](dev/done/parsek-architecture-v0.4.3.md) as a snapshot of early conceptual thinking. Class names in that document do not match current code.

---

## Block diagram

```
                                Player (KSP)
                                     |
         +---------------------------+-----------------------------+
         |                           |                             |
         v                           v                             v
   ParsekFlight               ParsekKSC / ParsekTS            Stock KSP events
  (Flight + Map scene)       (KSC + Tracking Station)         (contracts, funds,
                                                               parts, facilities,
                                                               crew, milestones)
         |                           |                             |
         v                           v                             v
  +----------------+         +--------------------+       +--------------------+
  | FlightRecorder |         |  GameStateRecorder |       |  KspStatePatcher   |
  | - trajectory   |         |  - career events   |       |  - restore KSP     |
  |   sampling     |         |  - resource deltas |       |    state on load/  |
  | - part events  |         |  - milestones      |       |    revert/rewind   |
  | - commit+snap  |         +--------------------+       +--------------------+
  +----------------+                     |                           ^
         |                               v                           |
         |                       +-------------------+                |
         |                       | LedgerOrchestrator|                |
         |                       | + 8 modules:      |                |
         |                       |   Funds, Science, |----------------+
         |                       |   Reputation,     |
         |                       |   Contracts,      |
         |                       |   Strategies,     |
         |                       |   Facilities,     |
         |                       |   Milestones,     |
         |                       |   Kerbals         |
         |                       +-------------------+
         |                               |
         v                               v
  +----------------+             +-----------------+
  | RecordingStore |             |    Ledger       |
  | (sidecar files |             | (GameAction     |
  |  .prec / .craft|             |  list, source of|
  |  /.pcrf)       |             |  truth per save)|
  +----------------+             +-----------------+
         |                               |
         +-------+------------------+----+
                 |                  |
                 v                  v
        +------------------+  +------------------+
        | GhostPlayback    |  | ParsekScenario   |
        | Engine           |  | (save/load glue) |
        | - ghost vessel   |  +------------------+
        |   spawn/despawn  |
        | - trajectory &   |
        |   event replay   |
        | - loop / overlap |
        | - WatchMode      |
        +------------------+

    -------------------------- UI layer --------------------------
      ParsekUI (main window + button row)
         -> Recordings Manager, Timeline, Kerbals, Career State,
            Gloops Flight Recorder, Real Spawn Control, Settings
```

All UI windows are read-only views of the three stores above (`RecordingStore`, `Ledger`, `KerbalsModule`). Cache invalidation fans out through a single event (`LedgerOrchestrator.OnTimelineDataChanged`) after every recalculation walk.

---

## Subsystem design docs

Each of these is the authoritative source for its area. This index doesn't duplicate their content.

- **Flight recorder + ghost playback**: [`parsek-flight-recorder-design.md`](parsek-flight-recorder-design.md) — trajectory sampling, part-event capture, commit/discard, ghost spawn/despawn, loop and overlap semantics, watch mode.
- **Recording finalization reliability**: [`parsek-recording-finalization-design.md`](parsek-recording-finalization-design.md) — terminal-state and synthetic-tail contract for scene exit, crash, vessel unload/delete, background recordings, and Rewind-to-Separation dependencies.
- **Timeline (entries + resource budget)**: [`parsek-timeline-design.md`](parsek-timeline-design.md) — timeline entry model, significance tiers, source toggles, time-range filter, resource-budget footer.
- **Game actions & career resources**: [`parsek-game-actions-and-resources-recorder-design.md`](parsek-game-actions-and-resources-recorder-design.md) — how KSP career events become `GameAction` ledger entries, resource module semantics, recalculation engine, action replay.
- **Logistics / Supply Routes**: [`parsek-logistics-supply-routes-design.md`](parsek-logistics-supply-routes-design.md) — stock-first Supply Runs, Supply Routes, dock/transfer/undock validation, and future round-trip linking.
- **Rewind to Separation (v0.9)**: [`parsek-rewind-to-separation-design.md`](parsek-rewind-to-separation-design.md) — re-fly unfinished sibling missions from a past multi-controllable split. Effective-state model (ERS / ELS), append-only supersede relations, session-suppressed subtree, journaled staged merge, post-load strip, narrow v1 tombstone scope.
- **Career State window**: [`dev/plans/career-state-window.md`](dev/plans/career-state-window.md) — four-tab career-state view, current-vs-projected walk, slot math, companion Kerbals→Timeline scroll.

Completed design specs (now implementation-historical) live under [`dev/done/`](dev/done/). Recent ones worth knowing:

- `design-committed-action-replay.md` — how committed game actions replay on rewind.
- `design-going-back-in-time.md` — rewind data model and safety rules.
- `design-mission-tree.md` — tree / group structure for related recordings.
- `design-timeline.md` — implementation-level spec companion to the live `parsek-timeline-design.md`.
- `design-camera-follow-ghost.md`, `design-restore-points.md`, `design-orbital-rotation.md`, `design-reentry-fx.md`.

---

## Entry points by KSP scene

| Scene | Class | What runs |
|---|---|---|
| Flight / Map | `ParsekFlight` | Recorder, ghost playback engine, watch mode, UI, toolbar button |
| KSC | `ParsekKSC` | UI, game-state recorder, toolbar button |
| Tracking Station | `ParsekTS` | Ghost ProtoVessel + icon presence, UI |
| (save/load glue) | `ParsekScenario` | All persistence: committed recordings, game-action ledger, milestones, kerbal slots |

The `KspStatePatcher` runs during save-load and after rewind to restore stock KSP state (funds, reputation, science, facilities, contracts, crew) to match the ledger's committed position.

---

## File inventory

The canonical, up-to-date list of source files + line counts + extraction history (T25 decomposition, Phase 11.5 storage layer, etc.) is maintained in `CLAUDE.md` at the repo root. That file is updated as a matter of routine when subsystems move; this index deliberately does not duplicate it.

---

## Invariants worth remembering

- `Ledger.Actions` is the single authoritative source of career-state truth per save. Modules hold terminal-state snapshots derived from walking the ledger.
- Recording format v0 is a clean reset; no legacy migration paths exist (PR #114).
- All recordings are tree recordings; the standalone format was removed in PR #214.
- Save-file writes go through sidecar files under `saves/<save>/Parsek/Recordings/` (`.prec`, `_vessel.craft`, `_ghost.craft`, `.pcrf`). Only lightweight metadata + mutable state live in `.sfs`.
- KSP surface-frame rotation is unconditionally surface-relative since format v0 — orbital rotation is future work.
