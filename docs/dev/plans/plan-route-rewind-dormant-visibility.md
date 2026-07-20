# Plan: Route rewind-visibility extension (dormant routes)

Status: IMPLEMENTED (2026-07-19; revised after clean-context plan review, then
built on this branch). Branch `logistics-route-dormant`, stacked on
`logistics-route-timeline` (PR #1327, which adds `Route.CreatedUT`).

## 1. Problem (corrected by review)

`RouteStore.LoadRoutesFrom` runs ONLY on the cold-start branch of
`ParsekScenario.OnLoad` (`initialLoadDone == false`). Every in-session load,
including the Rewind-to-Separation RP quicksave load, takes the preservation
branch and leaves the in-memory RouteStore untouched. Two consequences:

- **Visibility bug (live today):** a route created AFTER the rewind point
  SURVIVES the rewind wholesale (definition, counters, status) and keeps
  dispatching before its own creation point. The ratified compat report's
  axis-A claim "definition/counters revert via .sfs; absent if created after
  RP" (risk #9 "sound") cited a cold-start-only call site and is wrong for
  in-session rewinds; a correction addendum is part of this work.
- **Stale-cursor bug (live today, review finding 2):** routes created BEFORE
  the rewind point keep their abandoned-future cycle state
  (`LastObservedLoopCycleIndex`, pending delivery, holds, credit markers), so
  the loop-clock crossing detector silently swallows re-flown cycles until UT
  re-passes the abandoned future's last observation - partially defeating the
  Rec-1 re-delivery fix (its rows were retired but never re-emit).

Desired behavior: after a rewind below a route's creation point the route is
not displayed and cannot fire; when the re-flown timeline passes its original
`CreatedUT` again it re-materializes, provided its source recordings still
exist. Pre-cutoff routes resume firing correctly on the re-flown timeline.

## 2. Design

### 2.1 Data: a dormant-route list beside the committed list

- `RouteStore` gains `dormantRoutes` (`List<Route>`) + read-only view.
  Invisible to every existing consumer: `CommittedRoutes` unchanged; the
  orchestrator tick, `RouteTreeGuard`, candidate finder, and the Logistics
  window read committed only. A dormant route cannot fire, bind its tree,
  or render.
- Persisted as a `DORMANT_ROUTES` sibling of `ROUTES` in `SaveRoutesTo` /
  `LoadRoutesFrom`, reusing `RouteCodec` per entry. Sparse (empty list writes
  no node); `SaveRoutesTo` strips a stale `DORMANT_ROUTES` node alongside its
  existing strip list; `LoadRoutesFrom`'s wholesale-replace preamble clears
  the dormant list; loaded dormant entries whose id collides with a committed
  route are dropped (Warn, committed wins). `ResetForTesting` clears it.
- Because OnSave persists both nodes, a FUTURE RP quicksave may embed
  DORMANT_ROUTES; harmless - the rewind seam (2.2) replaces both lists from
  the bundle, and the cold-load path reads whatever the save carries.

### 2.2 Rewind seam: move-and-replace inside Restore(cutoff)

`ReconciliationBundle` follows its documented replace-not-merge contract for
routes, exactly like recordings:

- `Capture()` snapshots both lists (`Routes`, `DormantRoutes`, shallow).
- `Restore(cutoffUT)` (the Rec-1 overload) calls a pure helper
  `RouteRewindClassifier.Classify(capturedCommitted, capturedDormant,
  cutoffUT)` returning `(committed, dormant)`:
  - captured committed with `CreatedUT > cutoffUT` move to dormant;
  - captured committed with `CreatedUT <= cutoffUT` OR `CreatedUT < 0`
    (unknown/legacy - never classified dormant) stay committed;
  - captured dormant entries are merged (still future), deduped by id
    against committed and among themselves (first wins).
- The results are INSTALLED into RouteStore via a new
  `RouteStore.InstallRoutesAtRewind(committed, dormant)` (clear + fill both
  lists, batch-logged).
- **Dormanting hygiene (per route moved):** `DropRouteEscrow(id)`; two-sided
  link severing - the committed partner's `LinkedRouteId` back-ref is cleared
  and its `LastConsumedPartnerCycle` reset (RemoveRoute-style hygiene), while
  the DORMANT entry keeps its `LinkedRouteId` value as a former-partner hint
  (documented; re-link decision happens at materialize).
- **Pre-cutoff route reconcile (fixes the stale-cursor bug):** every KEPT
  committed route gets its forward-looking cycle state reconciled by a pure
  helper `ResetCycleStateForRewind(route, cutoffUT)`: loop cursors reset
  (`LastObservedLoopCycleIndex = -1`, `WindowAnchorCycleIndex = -1`,
  stop-fire state), pending delivery / eligibility / current-cycle fields
  cleared when they reference UTs beyond the cutoff (an InTransit status
  whose `CurrentCycleStartUT > cutoffUT` returns to Active), holds / partial
  reports stamped after the cutoff cleared, `PendingRecoveryCreditCycleId`
  cleared when its dispatch UT is beyond the cutoff.
- **Kept-route status fidelity (follow-up slice, branch
  `route-rewind-status-fidelity`):** after the reconcile, the seam derives
  each kept route's timeline-correct pause state from the KEPT
  `RoutePaused`/`RouteResumed` rows (`DeriveTimelineStatus`: latest kept
  PLAYER-DRIVEN marker wins, UT then Sequence; AUTO lifecycle rows whose
  reason starts with `AutoPause:`/`AutoResume:` (RevalidateSources
  source-validity flips, dormant-UI slice) are skipped from the scan -
  they describe source validity, which re-derives from ERS after every
  rewind, so replaying them as player intent would pause routes the
  player never paused and poison the PreMissingStatus recovery baseline;
  a null/empty reason stays player intent for legacy rows; a derived
  Active additionally requires at least one kept player `RoutePaused`
  row - a resume must resume something recorded, so a marker-less
  pre-feature pause is never un-paused by an older kept resume; `ApplyDerivedTimelineStatus`: a derived pause flips
  only the ghost-driving/wait statuses, a derived resume only un-pauses an
  explicitly `Paused` route, `InTransit` / no-marker routes are untouched,
  and a validity status (MissingSourceRecording / SourceChanged /
  EndpointLost) keeps its live status while the verdict is written to
  `PreMissingStatus` so recovery restores the timeline-correct state),
  unconditionally clears `PauseAfterCurrentCycle` + `SendOnceArmed` (armed
  one-shots carry no timestamp, so they never survive time travel), and
  reconstructs `CompletedCycles`/`SkippedCycles` from the kept
  dispatch/delivery rows (`ReconstructCycleCounters`, evaluated AFTER
  `ResetCycleStateForRewind`: with no kept dispatch rows both reset to 0;
  with a KEPT pre-cutoff in-flight cycle (`CurrentCycleStartUT` survives
  the reset) completed = distinct delivered kept cycle ids EXCLUDING the
  in-flight cycle's own id and skipped = `maxOrdinal - completed` clamped
  >= 0, so the sum lands ON the in-flight cycle's ordinal and a straddling
  multi-stop cycle keeps its id (its already-delivered windows dedup
  against the kept rows instead of double-delivering); otherwise
  completed = distinct delivered kept cycle ids and skipped =
  `(maxKeptDispatchOrdinal + 1) - completed` clamped >= 0, so a
  dispatched-but-undelivered cycle counts as skipped and the uniqueness
  invariant `Completed + Skipped > maxOrdinal` holds). The original
  "counters deliberately NOT recomputed" residual is thereby closed.
- The rollback / route-blind `Restore()` overload leaves route state
  untouched (mirrors Rec-1's +Infinity contract).
- **Both rewind exits reconcile (go-back fix, branch
  `fix-goback-route-reconcile`; preservation-branch audit 2026-07-19):** the
  whole classify + dormanting hygiene + kept-route reconcile + install block
  above is the shared helper `RouteRewindClassifier.ReconcileStoreAtRewind`,
  called by BOTH in-session rewind exits: the Re-Fly seam
  (`ReconciliationBundle.Restore(cutoff)`, behavior-identical to the
  pre-extraction inline block) and the plain go-back rewind
  (`ParsekScenario.HandleRewindOnLoad`), which previously contained zero
  route handling (kept routes carried abandoned-future loop cursors; routes
  created after the rewind target stayed committed, visible, and firing
  before their own creation point). The go-back exit first retires the
  abandoned-future free-standing route rows IN PLACE via
  `Ledger.RetireFutureRouteActionsAtRewind(cutoff)` - Rec-1 retire parity;
  the go-back path never runs the revert branch's
  `Ledger.PruneOrphanActionsAfterUT`, so the rows would otherwise survive -
  then runs the shared reconcile over the kept rows, all BEFORE the career
  cutoff walk and while `RewindContext.RewindAdjustedUT` (the UT the loaded
  save reverted the world to, the go-back's Rec-1-contract cutoff) is still
  populated. The block emits no ledger actions (OnLoad-safe). Gated by
  xUnit parity + source-text hookup tests
  (`RouteGoBackRewindReconcileTests`).
- The rare forced-cold crash-reconcile path (LoadRoutesFrom before
  ConsumePostLoad) is coherent: Restore replaces whatever was loaded.

### 2.3 Re-materialization: a tick-driven creation-point crossing

- `RouteOrchestrator.Tick(currentUT, env)` calls
  `RouteStore.MaterializeDueDormantRoutes(currentUT)` at the VERY TOP, before
  the committed-count early return (review finding 4), so a save whose only
  routes are dormant still materializes. Early-returns when the dormant list
  is empty (no per-tick cost / log).
- Due when `currentUT >= CreatedUT`.
- Per due route:
  1. **Tree-occupancy guard:** occupancy = non-empty intersection of the
     dormant route's tree-id set (`SourceRefs[].TreeId` union
     `BackingMissionTreeId`; null/empty ids never match) with any committed
     route's set. Occupied -> DROP the dormant entry (Info): the player
     re-created a route over that tree during the re-fly; live intent wins
     (this also resolves the candidate-re-offer duplicate path - the finder
     ignores dormant routes, so the tree legitimately re-offers meanwhile).
  2. **Reset-to-fresh:** definition preserved (Id, Name, sources, stops,
     manifests, cadence, priority, `CreatedUT`, origin flags, backing-mission
     fields); runtime cycle state reset as a fresh creation: `Status =
     Paused`, counters 0, cursors -1, `LoopAnchorUT = -1`, cleared
     current-cycle / pending / eligibility / hold / partial / send-once /
     pause-after-cycle / recovery-credit fields.
  3. **Link re-establishment:** if the former-partner hint names a route that
     is committed or co-materializing this pass, re-link via
     `RouteStore.LinkRoutes` (which resets both cursors and enforces
     mutuality); otherwise null the hint.
  4. **Add + guards + revalidate:** `AddRoute` (respects preset `CreatedUT`),
     `RouteTreeGuard.ForceClearManualLoopForRoute` (mirror of the create
     paths - a manual loop acquired during the re-fly must not co-drive the
     tree; review finding 5), then `RevalidateSources("dormant-materialize")`
     so superseded sources surface as `MissingSourceRecording` (visible
     broken beats invisible leak; a superseded tree never comes back).
  5. **Notify:** one screen message ("Supply route '<name>' available again")
     + Info log; batch summary for multiple.

### 2.4 Accepted limitations (documented, not built)

- ~~No dormant-section UI: dormant routes are invisible until they
  materialize, therefore also UNDELETABLE until then; and a player who
  re-creates then deletes a route on the twin's tree before its CreatedUT
  will see the old route materialize afterward (resurrection surprise).
  Accepted for this slice; a collapsed "Dormant" disclosure is the follow-up
  if playtesting wants it.~~ LIFTED 2026-07-19 (branch
  `logistics-dormant-ui`): the Logistics window now draws a collapsed
  "Dormant Routes (N)" disclosure (name + "appears at date" via
  `KSPUtil.PrintDateCompact` + Delete through the confirm-dialog flow into
  `RouteStore.RemoveDormantRoute`); visible + deletable dormant routes also
  defuse the resurrection surprise. The section stays read-only otherwise
  (no activate / edit before materialization).
- Legacy routes without `CreatedUT` never go dormant (survive rewind
  committed, today's behavior).
- ~~Counter inflation on pre-cutoff routes after rewind (2.2) - cosmetic,
  follow-up todo.~~ CLOSED by the `route-rewind-status-fidelity` follow-up
  slice (see 2.2: counters reconstructed from kept ledger rows).
- The ratified "set-on-and-it-works" model is unchanged: only the
  definition's existence window becomes timeline-faithful.

## 3. Implementation slices

1. **RouteStore dormant core** - list + views + Add/Remove + save/load +
   strip + reset + collision drop + logging.
2. **Pure classifiers** - `RouteRewindClassifier.Classify`,
   `ResetCycleStateForRewind`, `TreeIdSetsIntersect` occupancy predicate,
   `IsDormantRouteDue`, reset-to-fresh helper. All `internal static`,
   xUnit-first.
3. **Rewind seam** - bundle fields, Capture, Restore(cutoff) install +
   hygiene + pre-cutoff reconcile.
4. **Materialization** - `MaterializeDueDormantRoutes` + Tick wiring (top of
   Tick) + link re-establishment + manual-loop clear + revalidate + notify.
5. **Docs** - design doc 6.6 adjunct, compat-report correction addendum
   (axis A / risk #9), todo flips (+ counter-inflation follow-up), CHANGELOG.

## 4. Tests

- Codec: DORMANT_ROUTES round-trip, sparse omission, stale-node strip,
  committed-collision drop, LoadRoutesFrom clears dormant.
- Classify: after/before/at cutoff; unknown CreatedUT stays committed;
  stacked-rewind merge keeps earlier dormants; id dedupe.
- ResetCycleStateForRewind: cursor reset; InTransit-past-cutoff returns
  Active; pre-cutoff pending state preserved when it references UTs at or
  before the cutoff; credit marker cleared only when beyond cutoff; counters
  untouched.
- Occupancy: intersection semantics; null/empty tree ids never match.
- Materialize: due boundary (inclusive); occupied drop; reset-to-fresh field
  matrix; link re-establish (partner committed / co-materializing / gone);
  manual-loop clear invoked; RevalidateSources flips missing sources; empty
  list no-op; batch summary log.
- Rewind integration: capture committed {A createdUT=100 with future cursor
  state, B createdUT=900} + dormant {C createdUT=950}, Restore(cutoff=500):
  committed = {A reconciled}, dormant = {B, C}; then
  MaterializeDueDormantRoutes(960) with C's tree occupied: B materialized
  Paused, C dropped.
- Tick wiring: dormant-only store still materializes (count guard ordering).

## 5. Review resolutions (R1-R4)

- R1: sever two-sidedly at dormant time with RemoveRoute-style hygiene, keep
  the former-partner hint on the dormant entry, re-link via `LinkRoutes` at
  materialize when both present (stale `LastConsumedPartnerCycle` stall
  otherwise).
- R2: materialize-then-revalidate (visible `MissingSourceRecording` beats an
  invisible permanent dormant leak). Confirmed.
- R3: materialize Paused. Confirmed (matches TryActivate reset discipline;
  player resume is a recorded `RouteResumed` event).
- R4: no `.sfs` rebuild occurs on in-session loads; the bundle replaces the
  in-memory lists (same contract as Recordings). Restated.
