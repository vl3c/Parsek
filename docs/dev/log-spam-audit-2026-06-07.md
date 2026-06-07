# Log spam audit - 2026-06-07 career playtest

Source snapshot: `logs/2026-06-07_1638_career-playtest/KSP.log` (76 MB).

The playtest was mostly pure-stock career play. The player performed NO re-fly and
had NO supply routes, yet the log was 95% Parsek output and dominated by a handful of
per-frame / per-recording diagnostics that were not throttled (or were throttled by
time only, which still re-emits a stable value forever).

## Census (whole file)

| metric | value |
| --- | --- |
| total lines | 363,790 |
| `[Parsek]` lines | 344,815 (94.8%) |
| `[VERBOSE]` | 327,667 |
| `[INFO]` | 16,707 |
| `[WARN]` | 437 |
| `[ERROR]` | 4 |

Families below are normalized (IDs / numbers / quoted strings collapsed) by
`logs/2026-06-07_1638_career-playtest/_spam_analyze.py`.

## Ranked families (normalized) and disposition

| count | subsystem | per-frame? | convention before | disposition |
| --- | --- | --- | --- | --- |
| 185,303 | ReFlySettle `FloatingOrigin.setOffset` | yes (per physics frame) | un-throttled `Verbose` on the non-settle branch | FIXED: heartbeat `VerboseRateLimited` (30s shared key); INFO settle-window branch unchanged |
| 48,109 | GhostMap `Overlap gate decision` | yes (per committed recording per frame) | `VerboseRateLimited` per-index, 3s (time-only, re-emits stable verdict forever) | FIXED: `VerboseOnChange` (RecordingId identity + boolean-facet key) |
| 9,025 | RecordingStore `TryProbeTrajectorySidecar` (success) | no (per sidecar load) | `Verbose` one-shot, but the loud overload fires on every load | FOLLOW-UP: repeated-load driven; see below |
| 7,706 + 1,461 | RecordingStore trajectory deserialize (section-authoritative / flat fallback) | no (per sidecar load) | `Verbose` one-shot | FOLLOW-UP: repeated-load driven |
| 4,512 | RecordingTree `SaveRecordingResourceAndState` | no (per save, per recording) | `Verbose` one-shot per saved recording | FOLLOW-UP: batch-count-then-summarize candidate |
| 3,184 | RouteGhost `SelectGhostDrivingBackingMissions` | yes (per frame) | `VerboseRateLimited` 2s (all-zeros, no routes) | FIXED: `VerboseOnChange` (counts key) |
| 1,999 | RecordingStore `SerializeResourceManifest` | no (per save) | `Verbose` one-shot | FOLLOW-UP: batch candidate |
| 1,818 | Milestones `Credited milestone 'FirstLaunch'` | no (per ledger recompute) | `Verbose` re-logged each recompute (total stays constant) | FOLLOW-UP: ledger-recompute re-walk |
| 1,736 + 451 | Spawner `Spawn suppressed` | yes (per frame) | `VerboseRateLimited` with `suppressed=N` | CORRECT (already throttled) |
| ~1,554 | Anchor `Recording anchor candidates` / skipped | yes (per frame) | `Verbose`, two lines per evaluation | FOLLOW-UP: pair is a per-frame zero-result emit |
| 1,086 | GameStateStore `Saved baseline` | no (per baseline save) | `Verbose` one-shot | CORRECT (genuine one-shot) |
| ~1,044 + 294 | RecordingTree `SaveBranchPoint` | no (per decouple/joint-break event) | `Verbose` one-shot per event | CORRECT (bounded by real events) |
| 979 | MapRender `shadow frame` | yes (per frame) | `Verbose` summary, ghosts=0 most frames | FOLLOW-UP: skip-when-empty or change-detect |
| ~933 | GhostMap `lifecycle-summary` | yes (per tick) | `VerboseRateLimited` 5s shared key | CORRECT (already a throttled summary) |
| ~10,460 total | ScienceModule (Earning / Reset / ComputeTotalSpendings) | no (per ledger recompute) | `Verbose` per subject per recompute | FOLLOW-UP: ledger-recompute re-walk |
| ~6,243 total | LedgerOrchestrator (UpdateSlotLimits / Rebuild...) | no (per recompute) | `Verbose` per recompute | FOLLOW-UP: ledger-recompute re-walk |
| ~5,961 total | Funds (FundsSpending / FundsEarning) | no (per recompute) | `Verbose` per ledger row per recompute | FOLLOW-UP: ledger-recompute re-walk |
| ~291 each x ~12 modules | `Reset: cleared 0 ...` (Milestones, Contracts, Strategies, Route, Reputation, Facilities, ...) | no (per recompute) | `Verbose` one line per module per recompute | FOLLOW-UP: a no-op recompute emits ~12 zero-lines |

## What was fixed in this pass

Three changes, all logging-only (no runtime-behavior change):

1. `ReFlySettleStabilityTracker.RecordFloatingOriginShift` (185k lines, ~56% of all
   verbose). `FloatingOrigin.setOffset` fires on nearly every physics frame the world
   re-centres. The shift STATE is still recorded unconditionally (it feeds
   `LastFloatingOriginShiftFrame`, read by the GhostRenderTrace large-delta detector);
   only the diagnostic LINE changed. The settle-window branch still emits at INFO
   unconditionally (every shift matters during an actual re-fly settle); the dominant
   non-settle branch is now a 30s shared-key `VerboseRateLimited` heartbeat with a
   `suppressed=N` tail. The message is also built lazily so suppressed frames pay no
   string-format cost.

2. `GhostMapPresence.LogOverlapGateDecision` (48k lines, ~15%). The verdict is stable
   ("not-driven") almost always, so the old per-index 3s time limit still re-emitted
   the same line every 3s for every committed recording for the whole session.
   Switched to `VerboseOnChange` keyed on the stable `RecordingId` with a boolean
   decision-facet state key (cadence / span / cycle floats deliberately excluded so a
   driven recording still coalesces). It now logs only when the verdict actually flips.

3. `RouteGhostDriverSelector.SelectGhostDrivingBackingMissions` (3.2k lines). Already
   2s-rate-limited but all-zeros (no routes existed). Converted to `VerboseOnChange`
   on the counts tuple, so a stable roster collapses to one line.

Combined, these three families were ~236,600 lines (~72% of all verbose output and
~65% of the entire log). After the fix the floating-origin trace is a sparse heartbeat,
and the other two emit only on state change.

Locked in by unit tests:
- `FloatingOriginSetOffsetPatchTests.RecordFloatingOriginShift_NoSettleActivity_IsRateLimited`
- `OverlapPerInstanceTests.LogOverlapGateDecision_StableVerdict_CoalescesAndReEmitsOnFlip`
- `RouteGhostDriverSelectorTests.Summary_StableCounts_CoalescesAndReEmitsOnChange`

## Recommended follow-ups (NOT done here)

These were left out of this pass because they are either runtime-frequency issues
(not logging-convention violations) or carry a higher risk of dropping genuine
one-shot diagnostics, and the task was scoped to logging hygiene only.

- Trajectory sidecar re-reads (~18k lines): `TryProbeTrajectorySidecar` /
  trajectory-deserialize are correct one-shot `Verbose` load logs, but something
  reloads sidecars thousands of times per session. The hygiene fix is to pass
  `quietOnSuccess: true` at the hot load call sites (`RecordingSidecarStore` lines
  ~210 / ~747), but the real win is understanding the re-read pattern (a runtime
  concern, possibly a perf smell worth a separate look).
- Ledger-recompute re-walks (Funds / ScienceModule / Milestones / LedgerOrchestrator /
  the per-module `Reset: cleared 0 ...` lines, ~25k lines combined): each full ledger
  recalculation re-logs every action / subject / module reset, even when nothing
  changed and counts are zero. Batch-count-then-summarize per recompute (or skip the
  zero-delta reset lines) would cut this sharply, but it touches the GameActions
  modules and should be its own change with its own tests.
- Per-save per-recording lines (`SaveRecordingResourceAndState`,
  `SerializeResourceManifest`): batch-count-then-summarize per save.
- `MapRender shadow frame` and `Anchor candidates`: skip-when-empty or change-detect
  the all-zero per-frame emits.
