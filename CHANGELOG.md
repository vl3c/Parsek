# Changelog

All notable changes to Parsek are documented here.

---

## 0.8.2

_Unreleased._

---

## 0.8.1

### Recording System Optimization

- Binary/lossless recording sidecars are cutting recording payload size by 85.17% across 12 comparable packages (1.34 MB authoritative '.prec' / '_vessel.craft' / '_ghost.craft' files versus 9.03 MB readable text mirrors, 7.69 MB saved); April 13, 2026 log-bundle measurements also showed the latest bundle dropping from 745,079 B to 102,845 B (86.20% smaller).
- Phase 11.5 snapshot-storage follow-up now writes `_vessel.craft` / `_ghost.craft` sidecars as lossless header-dispatched `Deflate` envelopes at the highest built-in .NET compression level, while still loading legacy text snapshot files, preserving the existing alias/separate fallback rules, only falling back to vessel visuals when the ghost sidecar is actually missing, and using staged sidecar writes so caught partial-write failures roll back cleanly.
- Recording storage now has a default-on diagnostics flag that writes readable `.txt` mirrors next to the authoritative `.prec`, `_vessel.craft`, and `_ghost.craft` sidecars for comparison/debugging, deletes stale mirrors when the flag is turned off, and includes mirror bytes in the storage diagnostics breakdown without making mirror failures fatal to real saves.

### Improvements

- Phase 11.5 ghost LOD is now live in Flight: shared distance thresholds, unwatched reduced tier at `2.3-50 km`, hidden-mesh tier at `50-120 km`, and live diagnostics counts for `full / reduced / hidden / watched override`.
- Hidden-tier ghosts now unload built mesh/resources while keeping their logical playback shell alive, prewarm shortly before visible-tier re-entry or imminent structural part events, and rebuild from snapshot state without replaying transient puff/audio effects.
- Ghost performance tuning is now backend-owned. The old ghost soft-cap settings and the soft-cap subsystem were removed instead of leaving user-facing knobs that conflicted with the new distance policy.

### Bug Fixes

- `#360` Fresh ghost watch entry now lands on a canonical behind-the-ghost framing (`pitch 12°, heading 0°, 50 m`) instead of copying the active vessel's camera angles, so pad-side watch entries keep the default KSC-side view and the `V` camera-mode toggle no longer drifts the camera around the ghost on repeated presses (`PR #293`).
- `#361` Flat-fallback committed recording loads now heal malformed sidecars whose top-level point/orbit lists start by duplicating the exact `TrackSection` payload and only later resume with a valid tail, so old committed debris/background recordings no longer fail monotonicity validation just because the stale flat fallback survived on disk.
- `#359` Background `TrackSection` flush/merge paths now dedupe real multi-point overlaps instead of only shaving a single boundary frame, and finalize/revert now prune one-point destroyed debris stubs before they can poison commit metrics, so merged recordings stay structurally monotonic and the related recording-integrity / stop-metrics failures are gone (`PR #285`).
- `#358` Trees committed after an in-flight `F5`/`F9` quickload-resume now keep the root rewind save, reserved resource budget, and pre-launch baseline across quickload save, vessel-switch/background, split/promotion, and finalize paths, so destroyed-end merged recordings keep a working `Rewind` button instead of silently losing `R` (`PR #285`).
- `#357` Deferred orbital end-of-playback spawns now recover missed active-vessel handoffs when KSP switches to the spawned vessel without Parsek seeing the normal vessel-switch path, so the live recorder/tree no longer stays attached to the previous vessel through the post-spawn frame sequence.
- `#356` Optimizer boring-tail trims now require the tail after `lastInterestingUT + buffer` to already be the exact final spawn state, so orbiting/docked/suborbital and landed/splashed recordings are only shortened when nothing changes again before the real spawn.
- `#355` Flight anchor-camera ghost playback now restores deferred engine/RCS runtime FX state on the first visible frame after deferred activation, so launch ghosts no longer appear to have their engines off until `Watch Ghost` or KSC playback reapplies the same runtime state (`PR #281`).
- Fresh first-appearance ghost engine audio now waits until the ghost hierarchy is actually active before calling Unity `AudioSource.Play()`, so the retained engine sources no longer emit `Can not play a disabled audio source` warnings on the same deferred first frame that `#355` restored runtime FX/audio state (`PR #282`).
- `#354` Active breakup-continuous tree recordings that end in a stable spawned state now get a fresh terminal snapshot during tree finalization, so effective-leaf orbital end-of-playback spawns no longer reuse the old post-breakup `_vessel.craft` node after the orbit itself has already been corrected.
- `#353` High-warp orbital end-of-playback spawns now trim stable orbital boring tails against real activity instead of stale zero-throttle engine seed artifacts, then propagate stored terminal orbits to the current spawn UT and scrub stale packed-vessel/part atmospheric metadata before `ProtoVessel.Load()`, so stable-orbit recordings resolve around the normal 10-second boring-state buffer and deferred orbital spawns no longer mix an old endpoint state with a later planet rotation on the way into KSP's on-rails `SUB_ORBITAL`/`101.3 kPa` pressure kill path.
- `#352` Pending-tree merge dialogs now evaluate active non-leaf vessels against the current tree structure instead of only committed trees, so breakup-continuous landings and splashdowns default to persist exactly when runtime playback would spawn them.
- Mission-generated tree groups now keep their disband protection even when reparented under custom groups, the Recordings Manager hides the `X` button for those auto-generated `Mission` / `... / Debris` / `... / Crew` groups, and direct disband requests are blocked as a safety net, so tree-owned groups cannot be deleted accidentally (`PR #269`).
- Breakup child ghosts now always build visuals from the crash coalescer's split-time snapshot instead of a later live vessel snapshot, and debris ghosts no longer apply an extra snapshot center-of-mass offset on top of the recorded trajectory point, so freshly separated boosters and debris no longer appear visually ahead of their actual breakup position (`PR #271`).
- Map-view ghost watch actions now use the same availability rules as the main UI, support stop/refresh when re-clicking the watched ghost, and surface explicit "future ghost" / "different SOI" feedback instead of silently failing, so icon-menu and double-click watch interactions stay predictable (`PR #272`).
- Ghost map orbit lines now stay alive through brief same-body same-SOI gaps in orbit-segment coverage, merging equivalent windows while still breaking at real body changes, so continuous orbital journeys no longer flicker or lose their tracking-station/map-view path between adjacent segments (`PR #273`).
- Entering map view while already in watch mode now restores the map camera focus to the watched ghost once its map object and orbit renderer are ready, so watch-mode map transitions no longer strand the camera on the previous target or empty space (`PR #274`).
- Deferred split detection now falls back to decouple-created vessels when KSP never delivers a matching joint-break callback, but only for splits created from the recorded vessel itself, so delayed booster staging is still recorded without sweeping unrelated live debris into the current split (`PR #275`).
- `#350` Boarded-EVA playback now preserves post-board flat trajectory tails when merged `TrackSections` are only a stale prefix, and single-point boarded leaf recordings now count as renderable playback/spawn data, so the last kerbal can finish the visible re-entry path and the final capsule with the re-boarded kerbal now spawns at recording end.
- `#349` Kerbal retirement tracking now preserves raw snapshot crew names alongside the repaired logical owner names, so historical stand-ins still retire/delete correctly after `KerbalAssignment` repair rewrites their ledger rows back to the slot owner.
- `#348` The displaced-stand-in roster fix now preserves retired stand-ins as a special case, so missing retired roster entries are still recreated while truly unused displaced stand-ins stay deleted.
- `#347` Historical stand-in repair now falls back to persisted `KERBAL_SLOTS` as well as the live `CREW_REPLACEMENTS` bridge, so old ledgers with stale stand-in names still heal after the current replacement map has already been cleared.
- `#346` Ghost-only handoff fallback is now limited to unresolved or truly resolved chain segments instead of every snapshot-less chain recording, so auto-committed stable chain tips with `Orbiting`/`Landed`/`Splashed`/`Docked` terminals no longer get incorrectly collapsed to finite `Recovered` reservations.
- `#345` Save-load kerbal-action migration now rewrites stale per-recording `KerbalAssignment` rows instead of only filling missing ones, so legacy ledgers with old stand-in names or pre-fix `Unknown` end states are repaired in place on the next load.
- Load-time kerbal repair diagnostics now emit one concise summary per actual repair on both cold-start and in-session scene loads, covering slot-source fallback, chain-extension repairs, assignment remaps/end-state rewrites/tourist skips, and roster-side retired-stand-in recreation or unused-stand-in deletion while suppressing false positives from reorder-only rewrites and steady-state retired history.
- `#344` Ghost-only chain segments now resolve fallback kerbal end states as finite handoffs instead of open-ended `Unknown` reservations, so mid-chain vessel/EVA commits still reserve crew but no longer poison the reservation graph with indefinite placeholders.
- `#343` `ApplyToRoster()` no longer recreates displaced, unreserved stand-ins just because their chain metadata is still persisted, so deleted stand-ins stay deleted across later recalculation walks instead of churn-appearing and being deleted again every pass.
- `#342` Tourist passengers are now excluded from new `KerbalAssignment` generation, and the kerbals module ignores any legacy tourist assignment actions that already exist in the ledger, keeping tourist contracts out of the managed reservation/stand-in system.
- `#341` EVA-only recordings now populate crew end states during kerbal-action migration and the save-load safety net even when they only carry `EvaCrewName`, so legacy EVA recordings no longer degrade to `Unknown` reservations just because `VesselSnapshot` is null.
- `#340` Permanent-loss slot state is now recomputed on every kerbals walk, and permanently gone owners no longer keep stale stand-ins as active occupants after a prior temporary chain or after rewinding away the death.
- `#339` Replacement-chain reclaim now stops at the first free occupant after the reserved prefix, so deeper free stand-ins are treated as displaced metadata and retire/delete correctly instead of continuing to masquerade as the active slot occupant.
- `#338` Initial save load now initializes the kerbals module before reading `KERBAL_SLOTS`, so persisted replacement-chain state is restored on a cold start instead of being skipped until a later recalculation rebuilds partial slot data from reservations alone.
- `#337` KerbalAssignment creation now reverse-maps stand-in names through `CrewReplacements` before looking up `CrewEndStates`, so later recordings reserve the original slot owner instead of emitting open-ended reservations for the temporary stand-in.
- Recording timing bounds now combine actual trajectory coverage (points, orbit segments, playable track sections) with explicit outer bounds, so watch handoff and playback activation no longer get stuck when a section-authoritative continuation starts before its first flat point while still preserving live background and terminal end times.
- Optimizer boring-tail trims now cut nested section-authoritative frame/checkpoint payloads as well as the flat mirrors, so trimmed tails no longer grow back after save/load, and relative-section splits now resync their flat playback caches the same way the storage format does.
- Optimizer merges now carry the later segment's branch/end-state metadata and remove absorbed recordings from tree ownership cleanly, so merged chains keep branch continuation, terminal state, and tree/background bookkeeping consistent.
- Splashed terminal spawns now floor any slightly negative endpoint altitude back to sea level before spawn, so EVA and breakup-continuous splashdowns no longer materialize a few centimeters underwater (`#313`).
- Section-authoritative recording merges/splits now resync derived flat trajectory lists when the section payload can rebuild them losslessly, and recordings-window stats now use section altitude metadata plus relative-offset distance handling instead of treating relative frames as absolute surface coordinates (`#318`).
- Active-tree restore now keeps a matching in-memory pending tree when the saved active tree hits stale-sidecar epoch failures, and hydration-failed recordings are no longer pruned as disposable zero-point leaves during finalize (`#314`).
- Historical vessel PID reuse across committed trees no longer fails the in-game TreeIntegrity suite: the ambiguous tree-local PID cache is now explicitly `RecordedVesselPids`, cross-tree uniqueness is no longer asserted for archived history, and chain trajectory lookup still falls back globally when a claiming tree has no pre-claim path for the vessel (`#315`).
- Active-tree restore still keeps the full matching pending tree for stale-sidecar epoch failures, and other matched hydration failures now salvage only the failed recordings from the pending tree into the loaded disk tree, marking them dirty so the next save heals the sidecars without jumping the whole restore to the future timeline.
- Snapshot-only hydration failures now heal from the matching pending tree without replacing the loaded disk trajectory, so a bad `_vessel.craft` / `_ghost.craft` sidecar cannot make quickload restore a future-timeline track just to recover snapshot state.
- Separate ghost snapshot sidecars now rewrite on later saves instead of behaving like write-once files, so `_ghost.craft` stays aligned with `ghostSnapshotMode=Separate` after snapshot changes.
- Snapshot-side staged writes now clean transient `.stage.*` / `.bak.*` / `.tmp` artifacts conservatively, and orphan cleanup recognizes any leftovers from interrupted writes instead of leaving them behind forever.
- Mixed background recordings no longer let incomplete `TrackSections` suppress top-level trajectory on disk. Current-format sidecars now fall back conservatively when loaded background sections have not yet captured their checkpoint payload, preserving secondary-vessel and debris orbit continuation across save/load.
- Background `TrackSection` flushes now append their concrete frames back into the live flat `Points`/`OrbitSegments` lists with boundary dedupe, so background playback/UI paths do not need to wait for a save/load round-trip before secondary-vessel trajectory becomes usable.
- Pad-drop launch failures now use a recording-aware pad-local heuristic instead of raw 3D max distance alone, so topple / fall-over launches that never meaningfully leave the pad auto-discard instead of surfacing merge UI (`#324`).
- `#320` Tree-destruction merge confirmation now finalizes immediately in `FLIGHT` when the only remaining blockers are debris, so the merge dialog appears before the stock crash report again without waiting for a revert/fallback owner. Debris and `SubOrbital` leaves also no longer count as spawnable in the merge UI, keeping the default choices aligned with real playback spawn policy.
- `#335` Tree-destruction merge dialogs now wait for deferred joint-break and crash-coalescer resolution before finalizing the tree, so false-alarm crash branches no longer surface empty / `0s` merge dialogs and real breakup children are still attached before the merge UI is built.
- Watched ghosts now use exact watched-state identity (`recording + overlap cycle`) for full-fidelity protection. Overlap copies of the same looping recording no longer inherit watched-only exemptions or diagnostics counts.
- Watch cutoff / zone state for hidden looped ghosts now follows their logical playback position instead of a stale hidden transform, so watch eligibility and auto-exit stay correct while a loop is off-screen.
- Breakup child recordings now preserve split-time ghost visuals even when the child vessel mutates during the coalescing window, and watch transfers immediately bind/log the new watched cycle/target so debris and secondary-vessel handoff failures are diagnosable from `KSP.log`.
- Breakup/crash watch recovery now only auto-follows same-PID continuation. Different-PID breakup fragments no longer steal watch handoff, so crash-end watch now falls back to the normal hold/exit path instead of transferring the camera to debris or another fragment (`#321`).
- Compound-part ghost playback now tracks linked target persistent IDs through spawn-time build and runtime part-event updates, so one-ended detached struts/fuel lines no longer remain visible after separation, destroy, or inventory-removal paths leave their opposite endpoint logically gone (`#322`).
- Watch-mode observability now logs structured camera-focus summaries and W-button eligibility context, so playback focus and watch-button enable/disable state can be correlated from the same log bundle.
- `#317` Horizon-locked watch mode now follows surface-relative prograde during atmospheric playback instead of raw playback velocity, preserving the previous playback/inertial heading outside atmosphere and on airless bodies while adding explicit horizon-basis diagnostics to `KSP.log`.
- `#319` Group watch buttons now resolve the live continuation target across watch auto-follow handoffs instead of evaluating only the original group-main segment after its ghost despawns, so post-transfer group `W` no longer flips to misleading `disabled (no ghost)` while the watched descendant is still active.
- Watching a ghost now keeps its loop-synced breakup debris visible even beyond the normal distance-hiding tier, so long-range booster/debris playback no longer disappears just because the camera stayed on the parent vessel.
- Watched-debris protection now follows same-tree ancestry through branch points as well as loop-sync parents, so secondary breakup fragments inherit the watched ghost's visibility protection even when they are no longer directly loop-synced.
- Playback-driven automatic watch exits now retain same-lineage debris visibility through the last pending descendant debris playback across hold-expiry, high-warp hide, cutoff, policy, and target-loss exits, and failed replacement watch starts no longer clear that retained protection before a new watch session is actually established.
- Zero-throttle breakup debris now emits `EngineShutdown` sentinels instead of looking like a zero-event orphan-engine recording, so replay no longer auto-starts max-throttle booster FX/audio for staged-off debris.
- `#323` In-session `ParsekScenario.OnLoad` no longer reloads stale `GROUP_HIERARCHY` data over the live session state, so root-level `... / Debris` groups keep their intended parentage (or lack of parentage) after commit/playback instead of being silently re-parented by an older save snapshot.
- The old warp-only orbital exemption no longer punches through the new `50-120 km` hidden-mesh tier. Orbital ghosts still get the legacy exemption only in the true `Beyond` zone.
- Entering watch mode now uses the tracked playback distance first, avoiding false "in range" decisions from a hidden ghost's stale transform.
- Flight ghost LOD in watch mode now measures from the live flight camera in flight view and falls back to the active vessel only when no usable scene camera exists or when map view is active, so nearby EVA/parachute secondaries no longer get reduced or hidden just because the active vessel is far away (`PR #260`).
- Automatic watch auto-follow now primes the destination ghost's horizon basis before applying camera compensation, so continuation handoffs no longer spend their first `HorizonLocked` frame using stale orientation state (`PR #255`).
- `#351` Landed/splashed ghosts now reuse distance-aware terrain clearance even on past-end / loop-hold positioning paths, so long-range watched terminal ghosts no longer clip into terrain on their held final pose (`PR #262`).
- `#325` Branched quickload watch handoff now derives pending continuation timing from the child's real ghost-activation UT when available and extends the watched-parent hold window with warp-aware pending-activation timing plus a short post-activation grace period, so delayed same-vessel continuations no longer drop watch just because resumed payload starts later than the branch boundary.
- `#326` EVA branch recordings no longer seed bogus atmospheric start fragments when a landed or splashed kerbal is backgrounded before KSP finishes the vessel switch. The branch path now carries a one-shot surface override through delayed child initialization, and atmospheric-body EVA classification now keeps ground-adjacent or sea-level bobbing kerbals in surface segments instead of producing stray `atmo` optimizer splits.
- `#328` Continuous same-body kerbal EVA recordings no longer split across optimizer `atmo`/`surface` boundaries. The optimizer now keeps continuous EVA atmosphere/surface sections together, repairs older split-at-load pairs by trimming overlapping section payload before flat-point rebuild, and suppresses misleading mixed phase labels so vehicle-exit-through-touchdown EVA stays a single recording.
- Atmospheric-body EVA touchdown follow-ups are now consistent end-to-end: the Recordings table no longer colors suppressed mixed-EVA `Kerbin` rows as if they were exo/orbit segments, loaded EVA touchdowns that pack directly into a landed no-payload on-rails state now persist a surface boundary section so the optimizer keeps the landing as one recording, and trajectory sidecars now stay on the flat fallback path whenever `TrackSections` cannot exactly rebuild the stored flat `Points`/`OrbitSegments` (`PR #266`).
- Tree commits now create `Mission / Crew` subgroups for EVA branches and only re-home stale standalone EVA groups when they still carry Parsek's auto-assigned marker, while grouped mission rows in the Recordings table now nest by tree-local vessel lineage instead of only `ChainId`; a `Kerbal X` mission no longer leaves some same-vessel recordings flat at the mission root while later siblings appear in a separate subgroup (`PR #265`).
- `#220` Crew end-state inference now persists a separate resolved-no-crew state, so 0-point intermediate/probe recordings do not rerun `PopulateCrewEndStates` on every recalculation pass while genuinely missing start-snapshot cases still stay unresolved for later recovery.
- Destroyed debris playback now triggers whole-vessel explosion FX from the earliest eligible recorded destroy event instead of waiting for `EndUT`, so debris that visibly hits the ground no longer hangs before the final blast in either Flight or KSC playback (`#329`).
- Mid-flight active-tree saves now checkpoint the recorder's currently-open `TrackSection` before serializing and immediately reopen a continuation section afterward, so quickload no longer drops the live sparse trajectory chunk and post-separation main-stage playback does not freeze or drift across the missing save/load interval (`#327`).
- `#331` Debris-only breakup false alarms now reopen the active recorder's continuation `TrackSection` from the latest closed-or-discarded section context, restore packed `OrbitalCheckpoint` on-rails state, and seed the boundary frame when appropriate so section-authoritative main-stage playback no longer truncates or drift-resume at the first false-alarm separation boundary.
- `#336` Breakup/background child recordings now seed split-time trajectory only from genuinely captured split poses, keep destroyed coalescer-window children from losing that seed, and resolve seeded-orbit spawn/loop/chain endpoints from the true orbit end instead of snapping back to the separation frame.
- `PR #280` Breakup child recordings now capture exact decouple-time split seeds and root-part surface poses instead of stamping the later deferred split-check frame, and fresh ghost first frames clamp back to the activation start if visibility wakes slightly late, so radial boosters and other breakup debris no longer appear slightly ahead of the true separation point on first appearance.
- Manual watch-button retargets now preserve the live watch camera orbit and compensate for target-frame changes (`cameraPivot`/`horizonProxy`) when switching between ghosts, so hopping between vessel/EVA watch targets no longer resets to the default `50 m` under/overhead entry angle (`PR #267`).
- The in-game test runner window now uses a more compact layout without the visible blank rows between tests, marks destructive scene-transition checks as `[single]`, and skips those single-run-only quickload canaries during `Run All` / category batches instead of driving them through a normal FLIGHT test run.
- `#332` Running the in-game FLIGHT suite no longer batches the destructive quickload scene-transition canaries that were leaving KSP in a broken `flightReady=false / activeVessel=null` state after stock quickload blew up, and the synthetic live `ParsekScenario.OnSave`/`OnLoad` round-trip tests were removed from the in-game suite entirely. The settings window still keeps a stable tooltip layout every frame, and the quickload canaries remain available as explicit single-run diagnostics.

### Developer Tools

- Verbose ghost FX/audio/visual diagnostics in hot rebuild paths now use shared rate-limited keys, so repeated engine/RCS ghost rebuilds no longer flood `KSP.log` every cycle and instead roll up into periodic `suppressed=N` summaries.
- Added regression coverage for fresh watch-entry camera-state prep, pinning the atmospheric/orbital initial-mode resolution and the default entry-distance behavior behind `#360` / `PR #288`.
- Added regression coverage for malformed flat-fallback committed sidecars whose duplicated `TrackSection` prefix leaves a later monotonic tail, so load-time healing now preserves the real suffix and rewrites the sidecar cleanly on the next save (`#361`).
- Added regression coverage for quickload-resume rewind metadata propagation and background recording integrity, including root rewind-budget fallback, recorder-backed commit-path wiring, overlap-aware flat-tail preservation, destroyed-stub prune guards, and the refreshed in-game runtime log-contract assertion behind the `#358` / `#359` fallout.
- Added regression coverage for missed vessel-switch recovery after deferred orbital spawns, including the pure guard cases and the `Update()` ordering requirement that recovery run before other tree transition handlers (`#357`).
- Added regression coverage for terminal-state-aware boring-tail trim gating, including exact-match orbit/surface stability checks and the `SubOrbital` orbit-tail path so trims only happen once the spawn state has truly stopped changing (`#356`).
- Added regression coverage for deferred ghost runtime FX restore, including tracked current-power collection/clearing and first-activation restore gating so anchor-camera launch ghosts keep their engine plume/audio state on the first visible frame (`#355`).
- Added regression coverage for high-warp orbital spawn hardening and stable-orbit tail trimming: terminal-orbit spawn-state selection, top-level and per-part orbital metadata normalization, and zero-throttle engine-seed filtering in boring-tail trim so stable orbital coasts resolve on the normal 10-second buffer instead of replaying their full tail (`#353`).
- Expanded ghost-visual frame coverage so breakup child ghosts stay anchored to split-time snapshots without a duplicate debris center-of-mass offset (`PR #271`).
- Added regression coverage for map-view ghost watch actions and watched-ghost focus restore, including start/stop/refresh behavior plus future/different-SOI refusal paths (`PR #272`, `PR #274`).
- Added regression coverage for same-SOI orbit-gap display and decouple-only split detection, including equivalent-window carry rules plus recorded-parent filtering for delayed booster staging (`PR #273`, `PR #275`).
- Added regression coverage for boarded-EVA re-entry playback: board-merge stale-section tail preservation, single-point ghost renderability/state seeding, and direct stale-track fallback serialization for `#350`.
- Added regression coverage for exact split-branch timing and fresh first-frame playback gating, including deferred split branch UT resolution preferring captured decouple seeds plus the narrow activation-start clamp for newly visible breakup ghosts (`PR #280`).
- Added cold-start/load-path regression coverage for kerbal repair convergence: persisted slot restoration, EVA-only end-state repair, mixed tourist/remap/end-state migration, reorder-only assignment rewrites, chain-extension diagnostics, failed retired-stand-in recreation, and both facade-path and minimal real-roster `ApplyToRoster()` checks.
- Documented the kerbals hardening follow-up plans in `docs/dev/plans/` and refreshed `todo-and-known-bugs.md` to close the cold-start slot-migration and load-diagnostics follow-ups while narrowing the remaining real-roster `ApplyToRoster()` coverage gap.
- Added regression coverage for R/FF enablement reasons, including future/past timing, tree-branch rewind save resolution, and a UI guard that pins rewind/fast-forward independence from watch-distance state (`T60`).
- Added regression coverage for compressed snapshot sidecars: legacy/new mixed corpora, alias/separate/ghost-only fallback behavior, corrupt/unsupported/oversized-envelope rejection, snapshot-hydration failure surfacing without misleading fallback logs, snapshot-only quickload salvage that preserves disk trajectory and alias invariants, transient sidecar-artifact cleanup, and staged-write rollback/heal behavior for both first-write and stale-ghost-delete branches.
- Added regression coverage for readable sidecar mirrors: default-on mirror generation, mirror cleanup when the flag is disabled, orphan/transient mirror cleanup, diagnostics byte accounting, scenario fixture generation, and non-fatal mirror reconcile failures that must not roll back authoritative sidecars.
- Added regression coverage for exact watched-cycle protection, hidden-tier warp exemption, watched-override diagnostics counting, and the new frame-context watch-cycle field.
- Added regression coverage for hidden-tier shell-state handling so unloaded ghosts keep their logical loop identity and rebuild paths preserve playback bookkeeping.
- Added regression coverage for historical PID reuse across committed trees, `BackgroundMap` duplicate detection, overlapping rewind continuations in `GhostChainWalker`, and chain trajectory lookup that prefers chain trees without dropping the global pre-claim fallback (`#315`).
- Added regression coverage for effective group watch-target resolution across single-hop and multi-hop handoffs plus parity checks for breakup/non-breakup fallback behavior, so the group `W` button stays aligned with real watch auto-follow semantics (`#319`).
- Added regression coverage for watched-lineage debris visibility, so watch-mode protection now stays pinned to the intended same-tree same-vessel debris path instead of only the exact watched recording row.
- Added archived-topology regression coverage for issue `#316`, including missing loop-sync parents after chain splitting, retained watched-lineage protection through late debris end times, and the current non-retarget-to-debris watch-target rule.
- Added focused regression coverage for compound-part target PID parsing and logical link-visibility decisions, including missing-target, inactive-target, source-removed, and subtree-removal cases behind `#322`.
- Added regression coverage for zero-throttle engine seeding vs. orphan-engine auto-start, so staged-off debris boosters are pinned against replaying as visually full-throttle.
- Added regression coverage for pending watch activation after branched quickloads, including actual-payload-start precedence over stale explicit UT, warp-aware hold sizing, and capped post-activation grace handling (`#325`).
- Added `#323` regression coverage for in-session hierarchy preservation: unit coverage pins the load-policy gate so a root-level debris group survives a stale saved hierarchy without being re-parented.
- Added regression coverage for EVA branch surface seeding and atmospheric/splashed EVA environment classification, covering the queued background override path and the near-ground / sea-level EVA surface heuristics behind `#326`.
- Added regression coverage for continuous EVA `atmo`/`surface` optimizer behavior, including split suppression for live same-body EVA sections, repair of already-split overlapping loaded pairs, and mixed-label UI formatting (`#328`).
- Added regression coverage for mixed-EVA phase styling, no-payload loaded->on-rails touchdown boundary persistence, and text/binary sidecar fallback serialization for recorder loaded->on-rails transitions (`PR #266`).
- Added regression coverage for EVA crew subgroup commits/adoption, auto-assigned standalone-group marker persistence/clearing, and grouped-tree vessel nesting/order in the Recordings table (`PR #265`).
- Added regression coverage for resolved-no-crew end-state bookkeeping, including missing-start-snapshot behavior and save/load persistence of `crewEndStatesResolved` (`#220`).
- Added regression coverage for save-time `TrackSection` checkpointing across active, relative, and orbital-checkpoint sections, and saved a reusable `tools/inspect-recording-sidecar.ps1` inspector for decoding `.prec` sparse trajectory payloads while debugging storage/playback issues like `#327`.
- Destructive live `ParsekScenario.OnSave`/`OnLoad` round-trip tests were removed from the in-game suite, quickload wait helpers fail with explicit scene context, and destructive F5/F9 scene-transition tests are single-run only instead of part of normal FLIGHT batches (`#332`).
- Added regression coverage for false-alarm recorder resume restoring absolute, relative, and orbital-checkpoint `TrackSection` continuity, including discarded-section metadata recovery and on-rails resume state for `#331`.
- Added regression coverage for manual watch-camera transfer mode resolution in the unit suite and for transferred watch-angle compensation in the in-game runtime suite, pinning the manual watch-switch camera fix against both `50 m` reset regressions and target-basis snap regressions (`PR #267`).
- Added regression coverage for post-destruction merge resolution waiting on pending crash cleanup, so debris-only crash ordering does not regress into empty-tree / wrong-duration merge dialogs while deferred split or coalescer work is still finishing (`#335`).
- Added regression coverage for split-time breakup seeding, destroyed coalescer-window child retention, seeded single-point-orbit activation handoff, and seeded-orbit endpoint resolution across spawn/chain/playback paths (`#336`).
- Diagnostics now report live engine/RCS FX counts plus last-frame ghost spawn/destroy timings, giving a measurement-first view of FX cost without changing FX behavior.
- `scripts/inject-recordings.ps1 --run-diagnostics-tests` now runs the focused diagnostics/observability slice before showcase injection, including observability logging and in-game test runner ordering coverage.

---

## 0.8.0

### Breaking Changes

- Recordings from 0.7.x saves are no longer loaded. The standalone RECORDING format has been removed; only tree recordings (RECORDING_TREE) are supported. Start a fresh save or re-record flights.

### Improvements

- Phase 11.5 recording storage groundwork: added representative storage fixtures, golden round-trip coverage, and `v1` `.prec` sidecars that make `TrackSections` authoritative on disk instead of duplicating flat `POINT` / `ORBIT_SEGMENT` trajectory data.
- Recording sidecars now alias identical ghost snapshots to `_vessel.craft` via `ghostSnapshotMode` metadata instead of always writing a duplicate `_ghost.craft` file. Diagnostics and load paths understand alias mode and stale ghost sidecars are cleaned up on save.
- Phase 11.5 storage now writes current-format `.prec` sidecars as compact `v3` binary files with `PRKB` header dispatch, exact scalar payloads, a file-level string table, and conservative sparse defaults for stable per-point body/career fields. Legacy text `v0` / `v1` sidecars and binary `v2` sidecars still load, and the fixture/generator path covers mixed-format corpora.
- Migrated 9 log contract checks from post-hoc KSP.log analysis to in-game tests (Ctrl+Shift+T) -- catches format, resource, and recording metric issues at runtime instead of after session ends.
- Unified standalone and tree recording systems -- all recordings now use tree architecture internally (#271).
- Optimizer now splits tree recordings at environment boundaries, restoring per-phase segment display in the UI.
- Removed standalone RECORDING format entirely (T56) -- deleted pending slot, standalone merge/chain dialogs, standalone serialization, legacy migration shim. All committed recordings now require tree ownership.
- `#286` Merge dialog now explains when no flight branches produced a vessel that can continue flying.

### Bug Fixes

- Recording tree metadata now preserves the last written `ghostSnapshotMode` instead of recomputing it from live snapshots on every save, preventing `.sfs` alias/separate drift from disagreeing with the actual sidecar files on disk.
- `#307` Rewind (R button) now works for recordings committed after an in-flight vessel switch -- the rewind save is now copied to the tree root in both vessel-switch flush paths.
- `#308` Reserved kerbals no longer appear auto-assigned in the VAB/SPH crew dialog -- new Harmony patch replaces reserved crew with their stand-ins before the dialog builds.
- `#309` Rovers and ghosts recorded on the Island Airfield (or launchpad/KSC buildings) no longer spawn 19 m underground -- recording now captures the true surface height from KSP's raycast-derived `vessel.terrainAltitude` instead of PQS-only terrain, and spawn/ghost altitudes trust the recorded value with only an underground safety floor against PQS.
- `#310` Spawn collision detection now uses `Physics.OverlapBox` against real part colliders instead of a 2 m-cube blocker approximation, so large vessels (stations, planes, carriers) block spawns correctly.
- `#311` Walkback spawns on diagonally-descending trajectories are now snapped to the true surface via top-down `Physics.Raycast`, preventing mid-air spawns that fall.
- `#312` Placing multiple showcase ghosts of the same vessel type near each other no longer causes each new spawn to destroy the previous one -- the duplicate-blocker recovery now requires a PID match against the recording's own previous spawn, so siblings correctly fall through to walkback.
- `#241` Ghost parts with the base/default color variant (e.g., BlackAndWhite fuel tanks) no longer show the wrong variant texture (was Orange).
- `#297` Vessel destruction during tree recording no longer orphans continuation data as a standalone recording.
- `#304` Stock vessel recordings now show resolved names instead of raw `#autoLOC` keys in the UI, timeline, and logs.
- `#305` Standalone recordings (rovers, planes) now survive revert-to-launch and show the merge dialog instead of being silently discarded.
- `#306` Ghost engine nozzles no longer glow red permanently -- inherited prefab emissive values are cleared to black at build time. Thermal glow now driven purely by temperature thresholds (cool <40%, warm 40-80%, hot >80%), decoupled from engine/RCS throttle.
- Landed ghost terrain correction now clamps downward when terrain height decreased since recording (was only clamping upward, leaving ghosts floating above ground).
- Test runner window no longer spams IMGUI layout exceptions when opened during flight.
- `T55` FlagEvents and SegmentEvents are now preserved across tree recording splits and flushes.
- `#298b` Dead engine shutdown sentinels now work for active-vessel recordings (were only emitted for background vessel recordings).
- `T59` Rewind (R button) now works after mid-recording EVA -- rewind save filename and budget are copied to the tree root at branch time.
- `#270` Quickloading no longer loads stale sidecar trajectory data from a later save point.
- `T58` Debris ghost engines no longer show running FX at zero throttle after staging -- inherited engine state respects the child vessel's operational assessment.
- `T57` EVA recordings now spawn at end instead of being abandoned -- parent vessel is exempt from collision checks during EVA walkback.
- `#290` Five cascading bugs caused total recording loss on F5/F9 quickload: epoch drift, vessel name matching, idle-on-pad false positives, missing MaxDistanceFromLaunch, and Finalized tree overwrite by stale .sfs.
- `#290` Spawn-at-end no longer places vessels below runway/launchpad structures when the vessel was unloaded at scene exit.
- `#290` Phase column now correctly shows "surface" for landed/splashed vessels instead of "atmo"; LaunchSiteName now populates during OnSave for tree recordings.
- `#290` Debris and EVA branch recordings in the active tree no longer lose trajectory data after two cold starts (quit KSP mid-flight, relaunch, quit again).
- `#242` Ghost PREFAB_PARTICLE FX (smoke, small engine flames) no longer fires sideways -- auto-detects parent transform orientation and applies -90 X correction when needed.
- `#242` RAPIER and Panther ghost FX now shows only the active engine mode (jet or rocket) instead of both simultaneously.
- `#242c` Ghost engine and RCS FX on multi-variant parts (Poodle, Terrier, RV-105, etc.) now only fires from the selected variant's nozzles instead of all variants simultaneously.

### New Features

- Watch camera mode: press V during ghost watch to toggle between free orbit and horizon-locked (ground always at screen bottom). Auto-selects horizon-locked near planetary surfaces, free in orbit.
- Sortable "Site" column in the Recordings Manager — sorts by launch site name with UT tiebreak within the same site.
- Renamed the expanded-stats toggle button from "Stats" to "Info".
- Resource snapshots: recordings capture physical resource manifests (LF, Ox, Ore, etc.) at start and end. Hover tooltip in Recordings Manager shows resource changes per recording. Prerequisite for logistics routes.
- Inventory manifests: recordings capture stored inventory items (count, slots) at start and end. Hover tooltip shows inventory changes per recording.
- Crew manifests: recordings capture crew by trait (Pilot/Scientist/Engineer/Tourist) at start and end. Hover tooltip shows crew changes per recording.
- Dock target vessel PID captured at chain boundaries for future route endpoint identification.

---

## 0.7.2

Dev notes: technical narratives for the fixes below live in `docs/dev/todo-and-known-bugs.md` under the matching bug numbers. Commit messages have the full root-cause detail per PR.

### Bug Fixes — Name resolution & ghost positioning

- Vessel names in split/EVA/background recordings now resolve KSP localization tags (e.g., `#autoLOC_501232` → "Kerbal X") instead of displaying raw tags.
- `#282` Landed ghosts now sit at their natural recorded height above terrain instead of floating 4m up, using `Recording.TerrainHeightAtEnd` when available (NaN fallback preserves old behavior for legacy recordings).
- `#BugC` Switching to a spawned vessel no longer fills empty seats with extra kerbals — crew swap skips Parsek-spawned vessels entirely.

### New Features

- Ghost positional audio: 3D engine sounds, decoupler pops, and explosions with distance fade and atmospheric attenuation. Volume slider in Settings. Compatible with RocketSoundEnhancement.
- Start/End position columns in the expanded recordings table — launch site, EVA source vessel, and situation/biome/body for start; terminal state with location context for end.
- Per-recording storage tooltip in the Recordings Manager (file sizes + storage efficiency on hover).

### Developer Tools

- Recorder state observability: `grep "[RecState]" KSP.log` now shows every recorder lifecycle boundary as a single structured line (mode, active recording id, buffer levels, pending slots, chain state, sequence numbers). Diagnoses F5/F9 / EVA / dock / undock bugs in one grep pass.
- Diagnostics report accessible via Ctrl+Shift+T or Settings > Diagnostics — storage, memory, frame budget, save/load timing, health counters. Dumps to KSP.log.
- Automatic rate-limited `[WARN]` when playback exceeds 8ms/frame or recording exceeds 4ms/frame.
- New `ParsekLog.WarnRateLimited` API.

### Bug Fixes — Quickload-resume hardening

- `#293` F5/F9 during a multi-stage (tree) recording no longer kills the recording — quickload seamlessly resumes where the quicksave was made.
- `#294` F5/F9 during a single-vessel recording now seamlessly resumes recording from the quicksave point instead of silently stopping.
- `#267` Restore coroutine reentrancy guard: `OnVesselSwitchComplete`, `OnVesselWillDestroy`, and `FinalizeTreeOnSceneChange` now skip while the quickload-resume or vessel-switch restore coroutine is mid-yield.
- `#268` Belt-and-braces snapshot capture in `StashActiveTreeAsPendingLimbo` ensures null-snapshot leaves get a fresh vessel snapshot while the vessel is still alive, before the scene reload.

### Developer Tools

- `#269` Test runner now survives scene transitions (`Instantly + DontDestroyOnLoad`), enabling multi-scene coroutine tests. Three QuickloadResume in-game tests added: bridge canary, mid-recording resume identity, reentrancy guard verification.
- `#265` Nine in-game tests added for code paths xUnit can't reach (AudioSource dependency): ghost audio pause/unpause, terminal orbit backfill from orbit segments, and part-state seeder consistency.

### Bug Fixes & Maintenance

- `#302` Tree recordings no longer falsely auto-discarded as "idle on pad" after a scene change — max distance from launch was lost during save/load, making every recording appear to have never left the pad.
- `#303` Ghosts outside the physics bubble no longer clip underground — terrain LOD mismatch at distance caused the 0.5m clearance floor to be insufficient; now lerps from 2m at the bubble edge to 5m at 120km.
- **Fix ghost icon popup appearing at screen center instead of near cursor (#196).** Matched KSP's stock `MapContextMenu` positioning pattern: (0,0) anchors, forced layout rebuild, and `AnchorOffset` so the menu opens below the click point.
- `#283` Fixed ghost position pops at TrackSection boundaries by seeding the new section with the closing section's boundary point; also skips spurious cross-reference-frame discontinuity warnings.
- `#298` Ghost engine/RCS flames now auto-start on debris booster ghosts whose engines were running at separation. Debris recordings also get proper EngineIgnited seed events inherited from the parent vessel at decouple time.
- `#299` Terminal EngineShutdown events no longer survive into committed recordings when a standalone recording promotes to a tree during breakup.

- `#291` EVA spawn walkback now correctly detects the active vessel (parent rocket) as a blocker, so kerbals walk back to a clear position instead of spawning on top of their parent vessel.
- `#285` Background vessel splits no longer create empty parent-continuation recordings when the parent vessel is already destroyed.
- `#288` Ghost map icons now appear immediately after re-entry from orbit instead of requiring W (Watch) to be pressed first.
- `#289` End-of-mission spawn-at-end now correctly materializes splashed-down vessels and EVA kerbals after a rewind+merge.
- `#292` F9 quickload after a Tree Merge no longer silently drops recordings created during the merge.
- `#298` Rewind buttons no longer blocked by "Merge or discard pending recording first" after a tree merge — pending standalone recordings are auto-committed.
- `#299` EVA kerbal crew end states now correctly inferred (previously showed Unknown with infinite reservation because snapshot crew extraction fails for EVA vessels).
- `#300` Phantom terrain crash detection: EVA kerbals destroyed by KSP's pack-time terrain collision within 5s of a safe (Landed/Splashed) state are classified as Landed instead of Destroyed.
- `#301` Vessels alive at scene exit no longer marked Destroyed — terminal state inferred from trajectory when vessel unloaded during KSC transition.
- `#290a` Pending Limbo tree no longer discarded on revert — fixes lost debris recordings and missing proto vessels after merge-then-revert.
- `#290b` Ghost engine skirt/shroud now hidden at build time when the snapshot shows the shroud was already jettisoned.
- `#290c` Ghost map icon no longer flickers during time warp — zone warp exemption threshold lowered from >4x to >1x, and ghost warp suppression is now skipped in map view so icons stay visible at any warp speed.
- `#287` Ghost engine flames on multi-stage rockets (Kerbal X Mainsail + boosters) no longer turn off permanently after booster staging.
- `#278` EVA kerbals walking at the moment of revert/vessel-switch are now correctly classified Landed instead of Destroyed, so the merge dialog can spawn them at end of recording.
- `#277` Stand-in crew is now placed correctly when a launch crew member was on EVA at merge time.
- `#284` Background debris recording capped at primary debris only — cuts the recording count from ~25 to ~4-6 entries per Kerbal X launch.
- `#282` Landed ghost vessels and end-of-recording respawned vessels no longer clip into terrain.
- `#297` Map view now shows custom icons for all active ghost vessels, including atmospheric/landed ghosts beyond visual range.
- `#295` Vessels that sit idle on the launch pad are now auto-discarded instead of triggering a merge dialog.
- `#300` Revert-to-launch on a first flight no longer silently loses the recording — the merge dialog now appears correctly instead of the recording being mistaken for a quickload resume.
- `#280` Background debris recordings now persist their trajectory data across scene reloads.
- `#281` Decouple events on mixed-symmetry stages are no longer lost when a terminal-event collision occurs at the same physics frame.
- `#272` F5/F9 no longer destroys the entire launch tree.
- `#273` Tree recording trajectory is no longer silently lost across scene reloads.
- `#274` F9 after EVA resumes the tree instead of finalizing it.
- `#276` F5 → fly → F9 no longer commits the intermediate flight as an orphan recording.
- `#275` Watch buttons in the Recordings Manager now show explanatory tooltips for every disabled state.
- `#264` EVA kerbals spawn at their exact recorded walked position instead of on top of the parent vessel.
- `#263` Ghost boosters are no longer left visible on the ghost after a symmetry-group decouple.
- `#266` Switching to a distant vessel via the Tracking Station now preserves the in-progress mission tree instead of finalizing it on scene reload.
- Quicksave + quickload during an active recording no longer fragments the mission, produces partial ghost orbits, or scatters crew across orphan recordings.
- `#244` Ghost no longer stays icon-only for the entire Kerbin→Mun transfer.
- `#246` EVA on an airless body no longer generates multiple "approach" segments.
- `#248` EVA boarding is no longer misclassified as vessel destruction.
- `#254` Capsule spawned with wrong crew — now reverse-maps stand-ins through `CrewReplacements` before inferring end states.
- `#245`/`#247` Ghost map markers no longer appear at wrong positions for hidden ghosts.
- `#243` Watch camera now honors the distance cutoff for orbital recordings.
- `#250` End column no longer shows "-" for chain mid-segments.
- `#249` Planted flags are now visible during ghost playback when the ghost is in the Beyond zone.
- `#251` Recording phase label now updates after SOI change.
- `#255` Engine FX no longer killed during ghost playback after a false-alarm booster decouple.
- `#259` Orbital recordings now capture `TerminalOrbitBody` on every terminal-state path.
- `#258` Non-chronological trajectory points from quickload mid-recording are trimmed.
- `#257` Hyperbolic escape orbits no longer fail the `OrbitSegmentBodiesValid` in-game test.
- `#260` Removed dead `.pcrf` ghost geometry scaffolding.
- `#261` Diagnostics playback budget shows `N/A` instead of `0.0 ms` when no data available.
- `#262` Diagnostics storage scan no longer warns about recordings with null snapshots.
- `#264` follow-up: `StripEvaLadderState` no longer writes an invalid literal to the EVA FSM `state` field.
- `#256` Capped slow-motion trajectory sampling; new `Min sample interval` slider in Settings (default 0.2 s).
- `#252` Group hide checkbox now toggles all member recordings.
- Ghost orbit line now suppressed below atmosphere for segment-based ghosts.
- Rover recordings no longer include stale chain-promotion seed events.
- Ghost audio now mutes when the ESC pause menu is open.
- Ghost camera cutoff persists across rewind and KSP restarts.
- Closed `#156` (lifecycle test coverage — decision logic already extracted to pure/static methods with full coverage).
- Closed `#188` (spawned surface vessels in map view are real KSP vessels, expected).
- Deferred `#189b` (ghost escape orbit line) to Phase 11.5.
- Deferred `T43` (mod compatibility testing) to the last phase of the roadmap.
- Bump version to 0.7.2.

### In-Game Tests added

- `SpawnHealth` (3 tests) — regression coverage for `#132`, `#112`, `#149`.
- `ContinuationIntegrity` (2 tests) — regression coverage for `#95`.
- `RewindSaves` (1 test) — regression coverage for `#159`, `#166`.
- `TerminalOrbit` (2 tests) — regression coverage for `#203`, `#219`.
- `CrewReservationLive` (2 tests) — regression coverage for `#233`, `#46`.

---

## 0.7.1

### Bug Fixes

- **Fix crash breakup debris not recorded when recorder tears down before coalescer (#218).** `ShowPostDestructionMergeDialog` stopped the recorder after one frame, but the crash coalescer's 0.5s window hadn't expired yet. By the time the BREAKUP event emitted, no recorder existed to attach it to. Now waits for the coalescer to finish before proceeding, with a 5s real-time timeout for safety. Continuation recorder marked `VesselDestroyedDuringRecording` after tree promotion to prevent tree dialog guard from incorrectly aborting.
- **Fix spawn permanently blocked by duplicate vessel after rewind (#112).** After rewind, a quicksave-loaded duplicate of a spawned vessel could survive cleanup and permanently block the spawn position. Added defensive duplicate recovery in `CheckSpawnCollisions`: when a collision blocker's name matches the recording's vessel name, recover the blocker once then re-check. `DuplicateBlockerRecovered` flag prevents recovery loops. Also fixed pre-existing gap where `CollisionBlockCount`/`SpawnAbandoned` survived rewind (now reset by `ResetRecordingPlaybackFields`).
- **Fix atmospheric ghost markers not appearing in Tracking Station (#240).** `OnGUI` had a terminal state filter that skipped non-Orbiting/non-Docked recordings, blocking atmospheric trajectory markers for SubOrbital, Destroyed, Recovered, and Landed recordings even during their active flight window. The UT range check already handles temporal visibility correctly. Extracted `ShouldDrawAtmosphericMarker` as testable pure method.
- **Fix delayed proto-vessel ghost creation after merge dialog commit.** When a recording was committed via the merge/approval dialog while in the Tracking Station, proto-vessel ghosts took up to 2 seconds to appear (waiting for the lifecycle tick). Now detects committed recording count changes and forces an immediate lifecycle tick.
- **Fix deferred spawn queue split-brain (#132).** `HandlePlaybackCompleted` in `ParsekPlaybackPolicy` added deferred spawn IDs to the policy's `pendingSpawnRecordingIds`, but `FlushDeferredSpawns` in `ParsekFlight` read from its own never-populated duplicate set. Deferred spawns during warp silently never flushed. Moved `FlushDeferredSpawns` to the policy, eliminated the duplicate fields.
- **Wire up spawn-death detection (#132).** `RunSpawnDeathChecks` now runs before each engine update, detecting spawned vessels that died since last frame. Increments `SpawnDeathCount`, resets for re-spawn, or abandons after 3 cycles. Previously `SpawnDeathCount` was never incremented in the engine path.
- **Fix mid-tree spawn entries at EVA/staging boundaries (#227).** When a kerbal EVA'd or a stage separated, the timeline showed a premature "Spawn: Kerbal X" at the branch time. Two-sided fix: (1) suppress parent spawn when a same-PID continuation child exists via new `HasSamePidTreeContinuation` helper, (2) allow tree-child leaf recordings to produce spawn entries. Breakup-only recordings (no same-PID continuation) correctly still spawn.
- **Fix toolbar icon texture compression warning (#154).** Replaced 38x38 and 24x24 toolbar icons with 64x64 and 32x32 power-of-two versions. KSP can now DXT-compress the textures.
- **Fix ghost creation failing for orbital debris ("no orbit data") (#219).** `CaptureTerminalOrbit` only ran when `FindVesselByPid` returned a live vessel. Orbital debris with 30s TTL was often destroyed by finalization time, leaving terminal orbit fields empty. `PopulateTerminalOrbitFromLastSegment` now recovers orbit data from the last `OrbitSegment`.
- **Fix green sphere ghost for debris destroyed during coalescing window (#157).** Pre-capture vessel snapshots at split detection time and carry them through the CrashCoalescer. When `CreateBreakupChildRecording` runs 0.5s later and the vessel is destroyed, the pre-captured snapshot provides `GhostVisualSnapshot` instead of falling back to a green sphere.
- **Fix Settings window GUILayout exception (#217).** The ghost soft caps toggle caused an IMGUI Layout/Repaint mismatch: the early `return` in `DrawGhostSettings` skipped slider controls when caps were disabled, but a toggle click between passes changed the control count. 72 exceptions per session, window stuck at 10px. Fix: sliders always drawn, grayed out via `GUI.enabled` when caps disabled.
- **Fix W (watch) button staying enabled on debris boosters (#194).** After booster separation, one debris recording could have an active ghost with the W button enabled. Added `IsDebris` guard to watch eligibility check and "Debris is not watchable" tooltip.
- **Fix vessels and EVA kerbals spawning high in the air (#231).** Vessels with `terminal=Landed` spawned at their last trajectory point altitude (still descending), fell, and exploded. Three spawn paths lacked altitude clamping (flight scene, KSC, tree leaves). Now all paths clamp LANDED to terrain+2m clearance, override snapshot position AND rotation from the last trajectory point. Vessels spawn in their near-landing orientation instead of mid-flight descent pose.
- **Fix ghost map markers missing / wrong positions after save/load (#203).** `SaveRecordingMetadata` / `LoadRecordingMetadata` (standalone recordings) never serialized the 8 terminal orbit fields. After save/load, all standalone recordings had `TerminalOrbitBody = null`, so `HasOrbitData` returned false and no ghost map ProtoVessels could be created. Tree recordings were unaffected (separate serialization path). Now serialized in both paths.
- **Fix tree root recording showing T+ countdown instead of terminal state (#186).** Continuation sampling extends a committed recording's `EndUT` past current time, causing the status column to show "T+5m 23s" instead of "Landed". Now checks `TerminalStateValue` in all three status paths (row display, group aggregate, sort key). Group status also shows the best non-debris terminal state instead of generic "past".
- **Fix green sphere fallback for debris ghosts with no snapshot (#232).** Debris from mid-air booster collisions had no vessel snapshot, causing distracting green spheres during watch mode playback. Now skips ghost creation entirely for snapshotless debris. Non-debris keeps sphere fallback as safety net.
- **Fix continuation data persisting through revert (#95, items 3-5).** After EVA or undock, the continuation system appended trajectory points and overwrote snapshots on already-committed recordings. On revert/rewind, these mutations persisted — ghosts showed trajectory from an abandoned timeline. Fix: `ContinuationBoundaryIndex` tracks the commit-time point count; pre-continuation snapshots are backed up. On normal stop, the boundary is cleared (data baked as canonical). On revert, `RollbackContinuationData` truncates points and restores snapshots. All 8 stop sites audited: 5 bake (normal lifecycle transitions), 3 don't (vessel destroyed — revert undoes destruction).
- **Fix R (rewind) button missing on tree branch recordings (#159, #166).** Tree branch recordings (EVA kerbals, decoupled stages) had no `RewindSaveFileName` because rewind saves are only captured at launch. Added tree-aware lookup: `GetRewindRecording` resolves through the tree root so branches can rewind to the original launch point. `InitiateRewind` and `ShowRewindConfirmation` now use the owner recording's fields for correct vessel stripping, UT display, and future-recording count.
- **Fix timeline not refreshing after commits, rewinds, and KSC spending.** `LedgerOrchestrator.OnTimelineDataChanged` callback now wires to `TimelineWindowUI.InvalidateCache()`, ensuring the timeline view refreshes when data changes from commits, rewinds, time warp, KSC spending, and game load.
- **Fix FormatDuration overflow for long careers.** Changed `int` → `long` to support durations exceeding 68 years.
- **Fix timeline footer VesselSpawn count.** Footer now correctly counts all Recording-source entries instead of skipping VesselSpawn via early `continue`.

### Spawn System Hardening

- **Per-part identity regeneration on spawn (#234).** `RegenerateVesselIdentity` now regenerates per-part `persistentId` (via `FlightGlobals.GetUniquepersistentId`), `flightID` (via `ShipConstruction.GetUniqueFlightID`), `missionID`, and `launchID` — not just vessel-level GUID. Previously, spawned copies shared all part PIDs with the original vessel, causing tracking station/map view conflicts and likely contributing to #112 (spawn blocked by own copy). Uses delegate injection for unit testability.
- **G-force suppression after spawn (#235).** `IgnoreGForces(240)` now called on newly spawned vessels in both `RespawnVessel` and `SpawnAtPosition`. Without this, KSP calculates extreme g-forces from position correction after `ProtoVessel.Load()` and can destroy the vessel immediately. The existing `MaxSpawnDeathCycles = 3` guard was treating this symptom.
- **Global PID registry cleanup (#237).** Old part `persistentId` values are now removed from `FlightGlobals.PersistentUnloadedPartIds` before assigning new ones during identity regeneration. Prevents phantom entries accumulating over spawn/revert cycles in long sessions.
- **Robotics reference patching (#238).** `PatchRoboticsReferences` remaps `ModuleRoboticController` (KAL-1000) part PID references in `CONTROLLEDAXES`/`CONTROLLEDACTIONS`/`SYMPARTS` after identity regeneration. Without this, Breaking Ground DLC robotics controllers lose their servo bindings on spawned copies.
- **Post-spawn velocity zeroing (#239).** `ApplyPostSpawnStabilization` zeroes linear and angular velocity on freshly spawned surface vessels (LANDED/SPLASHED/PRELAUNCH) to prevent physics jitter. Orbital spawns are excluded via `ShouldZeroVelocityAfterSpawn` guard.
- **isBackingUp investigation (#236).** Confirmed via decompilation that `Vessel.BackupVessel()` sets `isBackingUp = true` internally. No fix needed — added documentation comment.

### Recording Optimizer

- **All-boring leaf recordings now trimmed to minimal window.** After optimizer splits produce an all-SurfaceStationary or all-ExoBallistic leaf recording (e.g., vessel sitting on Mun after approach/surface split), `TrimBoringTail` now trims to `Points[1].ut + buffer` instead of skipping. Previously, these recordings survived indefinitely because `FindLastInterestingUT` returned NaN with no reference point. The ghost now finishes in seconds instead of replaying minutes of stationary vessel.
- **Documented ChildBranchPointId split invariant.** Added comments in `RecordingStore.RunOptimizationPass` explaining why unconditional move of `ChildBranchPointId` to the second half is safe (BP is always at recording's temporal end) and why `BranchPoint.ChildRecordingIds` doesn't need updating (chain linkage provides the connection).

### Code Quality

- **Remove dead forwarding properties (#133).** Removed unused `overlapGhosts` and `loopPhaseOffsets` private forwarding properties from ParsekFlight — zero internal callers, external code accesses `engine.*` directly.
- **Centralize time conversion system (#187).** Created `ParsekTimeFormat` static class as single source of truth for calendar-aware time formatting. `FormatDuration` (compact: "2d 3h"), `FormatDurationFull` (all units: "1y, 2d, 3h"), and `FormatCountdown` ("T-2d 3h 15m 5s") all respect `GameSettings.KERBIN_TIME`. Replaced 4 duplicate `FormatDuration` implementations (RecordingsTableUI, MergeDialog, TimelineEntryDisplay, ParsekUI) and moved calendar constants from SelectiveSpawnUI. MergeDialog now correctly shows days/years for long recordings.
- **Timeline per-frame allocation cleanup.** Deduplicated vesselNameById dictionary, replaced `OrderBy().ToList()` with in-place `Sort()`, cached retired kerbals and stats text (rebuilt only on filter change), added `Dictionary<string, Recording>` for O(1) `FindRecordingById` lookup (was O(N) per row).

### Tests

- 119 new test cases. 5064 total passing.

### Location Context (Phase 10)

- **Biome captured at recording start and end.** Timeline shows "Landed at Midlands on Mun" instead of "Landed on Mun". Uses `ScienceUtil.GetExperimentBiome` — works for all stock bodies and biomes.
- **Vessel situation at recording start.** Humanized display: "Flying", "Orbiting", "Prelaunch" (not raw KSP ALL_CAPS enum names).
- **Stock launch site name captured.** Launch Pad, Runway, and Making History DLC sites (Desert Airfield, Woomerang Launch Site, Island Airfield) detected via `FlightDriver.LaunchSiteName`. Timeline shows "Launch: Vessel from Launch Pad on Kerbin".
- **Biome injected into spawn entries.** Surface situations (Landed, Splashed) get "at [biome] on [body]". Orbital situations pass through unchanged.

### Timeline Improvements

- **Tree branch recordings filtered from timeline.** Staging debris, booster separations, and decouple children no longer show as spurious "Launch:" or "Spawn:" entries. Only true launches and EVAs appear.
- **Destroyed terminal spawn entries removed.** "Spawn: Bob (Destroyed)" no longer appears — you can't spawn a destroyed vessel.
- **Spawn text shows body name for all terminal states.** Previously only orbital terminals showed the body.
- **Diagnostic logging for timeline construction.** Each included/excluded entry logged with classification flags and skip reasons.
- **Crew death entries in timeline (#229).** New `CrewDeath` timeline entry type (T1 significance). When a recording has `CrewEndStates` with `Dead` kerbals, "Lost: Bob Kerman (Vessel Name)" entries appear at recording EndUT with red-tinted color. Makes crew deaths visible in the mission narrative.
- **EVA crew reassignment noise filtered (#228).** KSP auto-reshuffles remaining crew when someone EVAs, generating spurious KerbalAssignment actions. These are now filtered by matching `(RecordingId, UT)` against EVA branch keys built from EVA recordings' parent info.

### Crew Reservation

- **Fix EVA kerbals disappearing after spawn or player EVA (#46, #233).** `RemoveReservedEvaVessels` was deleting any EVA vessel whose crew name was in the `crewReplacements` dict, including Parsek-spawned vessels (#233) and player-created EVAs (#46). Two guards added: (1) loaded EVA vessels (in the physics bubble) are always kept. (2) Vessels whose `persistentId` matches a committed recording's `SpawnedVesselPersistentId` are kept.

### Tests

- 4 new test cases (all-boring leaf trim, tree-with-branch-point optimizer integration, all-boring-too-few-points guard, end-to-end approach/surface split+trim). 4977 total passing.
- 23 new test cases (crew death timeline entries, EVA reassignment filtering, spawned EVA PID guard, BuildEvaBranchKeys, BuildSpawnedVesselPidSet). 5007 total passing.

### Documentation

- **Gloops design document rewritten.** `docs/dev/gloops-recorder-design.md` now grounded in actual codebase — maps 1:1 to existing types (IPlaybackTrajectory, GhostPlaybackEngine). Extraction plan identifies 17 files to move, 2 needing pre-extraction splitting.
- **Roadmap updated.** Phase 10 renamed to Location Context. Phase 11.5 (Recording Optimization & Observability) added. Phase 15 (Mod Compatibility) added as final phase — stock-first approach. Gloops extraction placed as Phase 13 prerequisite.
- `docs/dev/recording-optimizer-review.md` — full investigation of the recording post-processing optimizer covering multi-environment split logic, branch point re-parenting safety, and two end-to-end mission scenarios (Mun landing, Duna rover to Kerbin sample return).

---

## 0.7.0

### Timeline (Phase 9)

- **Unified timeline view replaces Game Actions window.** Chronological, read-only view of all committed career events sorted by UT. Current-UT divider separates past (full color) from future (dimmed). See `docs/parsek-timeline-design.md`.
- **Two-tier filtering.** Overview shows mission structure (launches, milestones, contracts, facilities). Detail adds all resource transactions (science, funds, reputation).
- **Three source filters.** Recordings (launches/spawns), Actions (deliberate player choices: tech, build, hire), Events (gameplay consequences: milestones, science earned, contract completions).
- **Rewind / Fast-Forward from timeline.** R button on past recordings, FF button on future recordings — same behavior as Recordings Manager.
- **GoTo cross-link.** GoTo button on recording entries opens the Recordings Manager, unhides the recording if hidden, expands parent groups, and scrolls to it.
- **Humanized display text.** Science subjects split and spaced (`crewReport@KerbinSrfLaunchpad` → `Crew Report @ Kerbin Launchpad`). Tech nodes, milestones, and strategy names humanized. Milestone IDs with `/` shown as ` - ` separator.
- **EVA-aware entries.** EVA recordings show `EVA: Jeb from Mun Lander (MET 5s)` instead of `Launch:`. Boarded kerbals show `Board: Jeb (Mun Lander)`. EVA self-assignment entries filtered out.
- **Spawn entries at EndUT with situation.** `Spawn: Vessel (Landed on Mun)` using KSP's VesselSituation. Falls back to terminal state + orbit body.
- **Mission Elapsed Time on launch entries.** `Launch: Vessel (MET 1d, 2h, 30m)` showing only non-zero components (KSP calendar: 6h days, 426d years). Chain recordings show full chain duration.
- **Resource budget summary.** Funds/science/reputation reserved vs. available at top of window. Game-mode aware (hidden in sandbox, science-only in science mode).
- **Footer statistics.** Recording count, revert count, action count, event count.
- 53 timeline-specific tests, 4870 total.

---

## 0.6.2

### Bug Fixes

- **Fix vessel not spawned at end of playback when parts break off on impact (#224).** Breakup-continuous foreground recordings (where `ProcessBreakupEvent` sets `ChildBranchPointId` without creating a same-PID continuation) are now recognized as effective leaves via `IsEffectiveLeafForVessel`. Snapshot refreshed post-breakup to reflect surviving vessel. Added diagnostic spawn-suppression-reason logging.
- **Fix spawn-in-air: altitude clamping for surface terminal states.** Breakup-continuous recordings used the last trajectory point (still descending) as spawn position. Splashed vessels spawned above sea level; KSP reclassified them to FLYING. Altitude now clamped to 0 for Splashed, terrain height for Landed.
- **Fix boring tail trim skipped for breakup-continuous leaves (#185).** `IsLeafRecording` in `RecordingOptimizer` rejected recordings with `ChildBranchPointId` without checking effective-leaf status. Ghost sat motionless on the surface for the full recording duration instead of trimming the idle tail and spawning promptly.
- **Fix camera black screen on distant vessel spawn.** `DeferredActivateVessel` called `ForceSetActiveVessel` on unloaded vessels (beyond physics range), triggering a full FLIGHT→FLIGHT scene reload. Now skips activation for unloaded vessels — camera stays on the pad.
- **Fix GhostChain.GhostStartUT set to rewindUT instead of earliest claim time (#225, T51).** `GhostStartUT` was initialized to the `rewindUT` parameter, making the in-game test `ChainTimeRangesValid` fail for all 13 chains with valid SpawnUT. Now set to `links[0].ut` (earliest chain link time).
- **Stateless spawn dedup bypass replaces fragile ForceSpawnNewVessel flag (#226).** Per-recording transient flag (lost on Recording object recreation mid-scene) replaced with single static `RecordingStore.SceneEntryActiveVesselPid`. `SpawnVesselOrChainTip` derives bypass decision from current game state at spawn time. Removed `MarkForceSpawnOnActiveVesselRecordings`, `MergeDialog.MarkForceSpawnOnTreeRecordings`, and all per-recording flag-setting code.

### Crew Reservation

- **Refactor kerbal reservation to not use rosterStatus=Assigned (T44).** Reserved kerbals now stay at their natural rosterStatus (typically Available) instead of being set to Assigned. A new `CrewDialogFilterPatch` Harmony prefix on `BaseCrewAssignmentDialog.AddAvailItem` filters reserved and retired kerbals from the VAB/SPH crew selection dialog. Eliminates the `KerbalAssignmentValidationPatch` tug-of-war (~27 KSP warnings per session) and the `AssignedCrewCountPatch` Astronaut Complex count mismatch. Both workaround patches deleted. Dead `ReserveSnapshotCrew` method removed.

### Code Quality — Refactor-3

- **Pass 1: 48 sub-methods extracted across 9 files.** Method extraction within files for long methods in GhostPlaybackEngine, FlightRecorder, GhostVisualBuilder, GhostPlaybackLogic, ParsekUI, ParsekFlight, RecordingStore, VesselSpawner, ParsekScenario. No logic changes.
- **Pass 3A: SafeWriteConfigNode deduplicated.** Four independent safe-write implementations (Ledger, RecordingStore, GameStateStore, MilestoneStore) consolidated into shared `FileIOUtils.SafeWriteConfigNode`. MilestoneStore gains error handling it previously lacked.
- **Pass 3B: SuppressionGuard struct.** 10 manual try/finally suppression-flag blocks across 4 files replaced with `IDisposable` `SuppressionGuard` struct (Crew, Resources, ResourcesAndReplay factories).
- **Pass 3C: ParsekUI window extractions.** Three self-contained windows extracted from ParsekUI (4,773 → 3,698 lines): `UI/GroupPickerUI` (373 lines), `UI/SpawnControlUI` (321 lines), `UI/ActionsWindowUI` (500 lines).
- **T45: `HasOrbitSegments` added to `IPlaybackTrajectory` interface.** 13 inline `OrbitSegments != null && .Count > 0` checks replaced across 6 files. `MockTrajectory` updated.
- **Pass 4: Remaining UI & watch-mode extractions (T46-T50).** Five more extractions completing the refactor:
  - `UI/TestRunnerUI` (276 lines) — test runner window from ParsekUI.
  - `UI/SettingsWindowUI` (353 lines) — settings window from ParsekUI.
  - `UI/RecordingsTableUI` (2,251 lines) — recordings table from ParsekUI (largest extraction, 57 fields, 30+ methods). GroupPickerUI ownership moved here.
  - `WatchModeController` (963 lines) — camera-follow / watch-mode from ParsekFlight (15 fields, 18 methods). ParsekFlight keeps forwarding methods for external callers.
  - `MilestoneStore.SuppressLogging` dead code removed (field written in 80 tests, never read).
- 4,816 tests pass throughout. Zero logic changes.

### Tests

- **40 new in-game runtime tests across 9 categories (PR #130).** Nearly doubles the runtime test suite (50 → 90). New categories: GhostLifecycle (orphan ghosts, NaN positions, overlap cap, explosion leaks, soft-cap coherence), PartEventFX (engine/RCS particle systems, parachute canopy, light components, fairing meshes, deployable transforms), GameActionsHealth (stuck suppression flags, career resource singleton bounds), GhostChains (stale recording refs, double-ghosting, time range validity, missing tip snapshots), TreeIntegrity (broken parent/child links, PID collisions across trees, EndUT coverage), SceneAndPatch (KSC/TS controller presence, ghost vessel load patch, scenario+crew round-trips), KspApiSanity (body rotation stability, UT monotonicity, PartLoader cache, Krakensbane, floating origin NaN drift), GhostMapOrbits (degenerate orbital elements), SpawnCollision (vessel bounds, distant overlap).
- **`InGameAssert.Skip()` for honest test reporting.** Tests that cannot exercise their assertions (no active ghosts, no committed trees, wrong game mode) now report as SKIPPED instead of silently passing. The test runner catches `InGameTestSkippedException` in both sync and coroutine paths, so results clearly distinguish "tested and passed" from "could not test".

---

## 0.6.1

### Game Actions & Resources

- **Fix career funds/science/reputation zeroed on save load (#222).** Loading a career save (especially after a sandbox save in the same session) would set all resources to 0. The ledger's `seedChecked` flag was never reset between saves, so no `FundsInitial`/`ScienceInitial`/`ReputationInitial` actions were created for the career save — the recalculation engine computed target=0 and `KspStatePatcher` actively zeroed KSP's correct values. Fix: added `HasSeed` guard to each resource module so patching is skipped when no seed exists, reset `seedChecked` on each save load, and added a deferred seeding coroutine that captures correct values after KSP finishes loading.

### Ghost Visuals

- **Fix invisible shrouds on ghost engines with variants (PR #124).** Engine shrouds (e.g. Poodle skirt, EP37 engine plate covers) were permanently invisible on ghosts. Three fixes: (1) Variant name resolution now reads `moduleVariantName` from the PART level in snapshots, where KSP actually persists it, not just inside the MODULE node. (2) Multi-MODEL parts (engine plates) have transform names with full GameDatabase paths; variant GAMEOBJECTS rules now match after stripping the path prefix and `(Clone)` suffix. (3) The transform-visibility fallback in `CheckJettisonState` misinterpreted variant-hidden transforms as jettisoned shrouds, emitting false `ShroudJettisoned` events at recording start that permanently hid all jettison geometry on playback. Fixed by skipping the fallback for parts with `ModulePartVariants`.

### Watch Mode

- **Fix watch camera cutting off on orbital ghosts.** The 300km camera cutoff was unconditionally exiting watch mode when a ghost exceeded the distance — even for orbital recordings that naturally travel far during ascent/orbit. Orbital recordings (those with orbit segments) are now exempt from both the watch-exit cutoff and the Watch button distance gate. Also added missing log entry on individual recording Watch button clicks.

### Recording

- **Close commit-to-save crash window (T15).** Recording data no longer lives only in RAM between commit and the next `OnSave()` cycle. New `FlushDirtyFiles()` writes `.prec`, `_vessel.craft`, and `_ghost.craft` to disk immediately on commit and again after the recording optimization pass (merge/split/trim). If the immediate write fails, `FilesDirty` stays true and `OnSave` retries as before — no behavior change in the failure path.

### Code Quality

- **ChainSegmentManager field encapsulation (T35).** Added `ApplyChainMetadataTo(Recording)`, `IsTrackingContinuation`/`IsTrackingUndockContinuation` properties, and `TryGetContinuationRecording`/`TryGetUndockContinuationRecording` accessors. ParsekFlight chain metadata copy sites and continuation access patterns now use these instead of direct field access.
- **Continuation recording index validation (T36).** `ContinuationRecordingId` and `UndockContinuationRecId` stored alongside int indices. `TryGet` accessors validate the ID matches before returning, detecting stale indices if recordings are ever removed mid-flight.
- **Breakup child recording dedup (T31).** Extracted `CreateBreakupChildRecording` static helper. 4 child-recording creation loops across `ProcessBreakupEvent` and `PromoteToTreeForBreakup` replaced with one-liner calls.

### UI

- **Simplified merge dialog.** All merge/commit confirmation dialogs (standalone, chain, tree, multi-vessel tree) now use a unified simple format: vessel/tree name and duration, with consistent "Merge to Timeline" / "Discard" buttons. Multi-vessel trees auto-apply default persist/ghost-only decisions (surviving vessels persist, destroyed are ghost-only) without per-vessel UI. Removed verbose per-vessel summaries, point counts, distances, and situation text.
- **In-game test runner.** New runtime test framework accessible via **Ctrl+Shift+T** (any scene) or Settings > Diagnostics button. Discovers and runs tests inside KSP to verify systems that xUnit structurally cannot cover (real Unity GameObjects, live KSP APIs, ghost visual construction, part name resolution, crew roster state). 50 tests across 13 categories: ghost visual builds, recording data health, body name resolution, save/load round-trips, crew reservation integrity, ghost map presence, CommNet antenna power, and Flight-scene integration. Supports sync and multi-frame coroutine tests, per-category and individual run buttons, color-coded pass/fail results with inline error messages, and auto-exports `parsek-test-results.txt` to the KSP root folder after each run.

### Showcase Recordings

- **Fix kerbal-with-flag height mismatch (T37).** Flag plant showcase kerbalEVA now uses `ShowcaseAltitudeOffset` for top-aligned height, matching other showcase parts.

### Ghost Playback

- **Fix ghost icon through planet (#212b).** Ghost vessel icon no longer follows the full Keplerian ellipse through the planet in tracking station. New `GhostOrbitIconClampPatch` prefix on `OrbitDriver.updateFromParameters` clamps the propagation UT to the visible arc — the icon freezes at the arc endpoint instead of going underground. The previous approach (drawIcons postfix on LateUpdate) silently failed because `vessel.orbitDriver` was null for ghost ProtoVessels despite `OrbitRendererBase.driver` being valid.
- **Chain-aware tracking station ghosts (#215).** Tracking station ghost creation now respects recording chains. Intermediate recordings superseded by later recordings in the same chain no longer get stale ghost ProtoVessels. Only chain-tip recordings with orbital data create ghosts.
- **Tracking station ghost lifecycle (#215).** Ghost ProtoVessels are now removed from the tracking station vessel list when game time passes their orbit segment endUT. Previously, ghosts were created once at scene init and persisted until scene exit regardless of time progression. Ghosts are also created dynamically during time warp when UT enters an orbit segment range.
- **Atmospheric ghost icons in tracking station.** Ghost vessel icons are now visible during atmospheric flight phases (launch, reentry) in the tracking station. Uses direct OnGUI rendering from trajectory data — same projection pipeline as flight-scene map markers. No ProtoVessel (avoids the known OrbitDriver state-vector roundtrip position mismatch, #172). New `MapMarkerRenderer` static helper shares icon atlas, vessel type colors, and rendering logic between flight and tracking station scenes.
- **Watch mode for distant ghosts (T39).** Watch button is no longer disabled for ghosts beyond the 120km visual rendering zone. The zone boundary is about rendering from the active vessel's camera — irrelevant for watch mode which moves the camera to the ghost. The only limit is now the user-configurable `ghostCameraCutoffKm` setting (default 300km).

### Recording

- **Debris recording filtering (T38).** Trivial crash fragments (single struts, panels, shroud pieces) are no longer recorded during breakup events. `ShouldRecordDebris` filters debris before Recording creation — vessels with fewer than 3 parts AND less than 0.5 tons are skipped entirely (no BackgroundRecorder tracking, no per-frame sampling). Spent boosters and multi-part stages pass the filter.

### Tests

- **ChainSegmentManager unit tests (T34).** 16 new tests covering `SampleContinuationVessel` guard paths (pid=0 early return, stale/negative index → stop callback), `UpdateContinuationSampling`/`UpdateUndockContinuationSampling` wrappers (no-op and stale-index propagation), `StopAllContinuations` branching (neither/one/both active, chain identity preservation), and `RefreshContinuationSnapshotCore` guards (pid=0, negative recIdx, stale recIdx). Total: 46 tests for ChainSegmentManager (up from 30).

### Architecture

- **KerbalsModule converted to IResourceModule (T42).** KerbalsModule now participates in the RecalculationEngine walk lifecycle instead of operating as a separate bridge. Added `PostWalk()` phase to IResourceModule interface. Removed 19 redundant `RecalculateAndApply()` calls. Added old-save migration for KerbalAssignment actions. Fixed latent bug where dead crew (absent from VesselSnapshot) weren't reserved for MIA respawn override.
- **Fix Astronaut Complex "Assigned" tab mismatch (#216).** KSP's `ValidateAssignments` set Parsek-reserved kerbals to Missing (not on any vessel), and `GetAssignedCrewCount` counted them despite the list being empty. Two Harmony postfixes: `KerbalAssignmentValidationPatch` restores Assigned status after validation, `AssignedCrewCountPatch` subtracts managed kerbals from the tab count.

### Documentation

- **TODO cleanup.** Marked T17 (game actions redesign), T25/D20 (playback engine extraction), T28/D2 (commit-pattern dedup), T32 (test audit), T34 (ChainSegmentManager tests), T41 (suborbital orbit line), T41b (atmosphere on-rails skip), T18/T35/T36/T37, T14, T29/T40 (closed), T31, and T13 as done.

---

## 0.6.0

### Ghost Playback

- **Suborbital orbit line (T41).** Ghost orbit lines are now visible during suborbital coasting phases. Recordings with orbit segments show the ballistic arc when the ghost enters a coast phase; pure-physics recordings (no time warp) construct the orbit from interpolated state vectors once above the atmosphere. Orbit line is atmosphere-aware: hidden below `body.atmosphereDepth` (70km on Kerbin) to avoid wild/flickering lines from atmospheric drag. On airless bodies, orbit line appears above 1500m. Hysteresis thresholds prevent flicker. Tracking station orbit lines remain restricted to stable orbital recordings.
- **Ghost orbit line suppression (Harmony).** New `GhostOrbitLinePatch` postfix on `OrbitRendererBase.LateUpdate` hides the orbit line for ghost ProtoVessels below atmosphere while keeping the native KSP map icon visible. Also hides Ap/Pe/AN/DN markers when orbit line is hidden.
- **Debris map markers hidden.** Debris ghost recordings no longer show green dot markers in map view.
- **Stock vessel type icons for ghost markers.** Ghost map markers now use KSP's actual vessel type icons (Ship, Probe, Rover, Station, Plane, etc.) from the orbit icon atlas instead of a plain green dot. Icons are color-tinted per vessel type. Falls back to a diamond shape before MapView initialization.
- **Ghost orbit arc clipping.** Ghost orbit lines in the tracking station now render only the arc between the orbit segment's `startUT` and `endUT`, instead of the full Keplerian ellipse. Suborbital trajectories no longer show orbit lines passing through the planet surface. Uses a Harmony prefix on `OrbitRendererBase.UpdateSpline` that applies the same eccentric-anomaly arc-clipping logic as KSP's own `PatchRendering`. Terminal-orbit ghosts (stable orbits) continue to show the full ellipse. Ap/Pe/AN/DN nodes are hidden for partial-arc ghosts to prevent misleading markers at out-of-arc positions.
- **Fix ghost icon through planet (#212b).** Ghost vessel icon no longer circles through the planet on the underground portion of the orbit. Replaced broken eccentric/true anomaly arc check (sign mismatch bug) with orbital-time-based check using `orbit.getObtAtUT()`. Added rate-limited logging for icon visibility decisions.
- **Chain-aware tracking station ghosts (#215).** Tracking station ghost creation now respects recording chains. Intermediate recordings superseded by later recordings in the same chain no longer get stale ghost ProtoVessels. Only chain-tip recordings with orbital data create ghosts. Fixes stale orbit display for vessels that have deorbited or been destroyed.
- **Ghost ProtoVessel pressure protection.** New `GhostCheckKillPatch` prevents KSP from destroying ghost ProtoVessels due to on-rails atmospheric pressure. Deorbit orbits pass through the atmosphere, triggering KSP's stock vessel destruction — if the map camera was focused on the ghost, this caused a NullRef cascade that broke scaled space rendering (planet disappeared, stuck exit).
- **Ghost surface clamp.** Ghost mesh clamped to body surface when Keplerian orbit goes underground (deorbit orbits with sub-surface periapsis). Prevents ghost tunneling through the planet during orbit-only recording sections.

### Format Reset

- **Recording format reset to version 0 (PR #114).** Clean break: reset `CurrentRecordingFormatVersion` from 7 to 0. Removed all legacy format migration code (v4→v5 rotation conversion, `SyncVersionFromPrecFile`, `CorrectForBodyRotation`), the `surfaceRelativeRotation` version-branching (all rotation is now unconditionally surface-relative), ghost geometry legacy fields (`GhostGeometryVersion`, `GhostGeometryCaptureStrategy`, `GhostGeometryProbeStatus`), and the `loopPauseSeconds` field-rename fallback. -500 lines. No behavioral change — all removed code paths were already dead (no users with old-format recordings exist).

### Release & Distribution

- **Parsek.version file (T1).** Added `GameData/Parsek/Parsek.version` for AVC and CKAN version detection. Auto-copied to KSP GameData on build.
- **UI version display (T2).** Version label ("v0.6.0") shown at the bottom of the main Parsek window, read from AssemblyVersion at runtime.

### Recording Optimizer

- **Fix optimizer over-splitting (PR #111).** The optimizer was splitting recordings at every ExoPropulsive↔ExoBallistic boundary (engine on/off), creating 10+ chain segments for multi-burn missions. Introduced `SplitEnvironmentClass` to coarsen split decisions: ExoPropulsive/ExoBallistic are the same class ("exo"), SurfaceMobile/SurfaceStationary are the same class ("surface"). Splits now only happen at meaningful boundaries: atmo↔exo, exo↔approach, approach↔surface.
- **Approach environment for airless bodies.** New `SegmentEnvironment.Approach` (=5) classifies vessels below approach altitude on airless bodies (Mun, Minmus). Enables the optimizer to split landing/takeoff recordings so they can be looped independently. A typical Kerbin→Mun landing now produces ~4 segments (atmo, exo, approach, surface) instead of 10+.
- **Unified tree root recording.** `PromoteToTreeForBreakup` no longer creates separate root and continuation recordings. The main vessel gets one continuous recording through all breakups. Decoupled part events handle ghost visual updates (booster detach) during playback. Eliminates the 14s root fragment that couldn't show later staging events.
- **Debris loop sync.** Debris ghosts (separated boosters, fairings) now replay in sync with the parent recording's loop cycle. New `LoopSyncParentIdx` field links debris to the parent recording whose loop clock drives their playback. Boosters visibly separate and fly away on each loop iteration.
- **Boring tail trimming.** Leaf recordings that end with a long idle period (surface stationary or orbital coasting) are automatically trimmed to ~10 seconds past the last meaningful activity. Prevents ghosts from sitting motionless for extended periods before the real vessel spawns. Only applies to leaf recordings (no child branches or chain continuations).

### Game Actions & Resources System

Full career-mode resource tracking across the rewind timeline. Science, funds, reputation, milestones, contracts, kerbals, facilities, and strategies are now recorded, reconciled on rewind, and patched back into KSP's singletons.

- **Ledger-based recalculation engine.** All game state changes are stored as `GameAction` entries in a chronological ledger. On every commit, rewind, warp exit, or load, the engine walks the full action list from UT=0 forward, recomputing derived state from scratch. Deterministic — same actions always produce the same result.
- **7 resource modules** participate in the recalculation walk:
  - **Science** — per-subject credited totals with diminishing returns, once-ever semantics, tech tree cost tracking
  - **Funds** — earnings, spendings, advance payments, affordability checks, hire costs, facility costs
  - **Reputation** — raw accumulation with KSP's diminishing-returns curve applied during the walk
  - **Milestones** — once-ever achievement tracking with path-qualified IDs for body-specific milestones
  - **Contracts** — full lifecycle (accept/complete/fail/cancel), slot management, deadline expiration with synthetic failure generation, once-ever completion semantics
  - **Facilities** — upgrade level and destruction/repair state tracking per KSC building
  - **Strategies** — activation/deactivation tracking with commitment-based contract reward diversion
- **KSP state patching.** After recalculation, `KspStatePatcher` syncs KSP singletons to match the ledger's computed state:
  - Science pool balance and per-subject credited totals (Science Archive)
  - Funds balance
  - Reputation (direct set, no double curve)
  - Facility levels via `UpgradeableFacility.SetLevel` and destruction state via `DestructibleBuilding.Demolish/Repair`
  - Milestone achievement flags via reflection on private `reached`/`complete` fields (no public reversal API)
  - Active contracts restored from ConfigNode snapshots via `Activator.CreateInstance` + `Contract.Load`
- **Contract deadline failures.** Accepted contracts with deadlines are tracked. If a deadline expires before the contract is resolved, the engine injects a synthetic `ContractFail` action at the deadline UT with the contract's failure penalties. Deadline and penalty data captured at accept time with backward-compatible structured detail format.
- **Kerbal rescue detection.** Subscribes to `GameEvents.onKerbalTypeChange` to detect Unowned-to-Crew transitions (rescue pickup). Covers all rescue scenarios: EVA boarding, docking, claw grab, crew transfer.
- **Game state event recording.** `GameStateRecorder` subscribes to 15+ KSP GameEvents (contract lifecycle, crew changes, facility upgrades, tech research, milestones, science experiments, kerbal type changes). Events stored in `GameStateStore` per recording, converted to ledger actions at commit time.
- **Milestone ID path qualification.** Body-specific milestones now captured as `"Mun/Landing"` instead of ambiguous bare `"Landing"`. Uses reflection to read the private `CelestialBody body` field on KSPAchievements subclasses.
- **Strategy commitment rates.** Strategy reward diversion uses the player's chosen commitment level (0.01-0.25) instead of a hardcoded 1.0.
- **Warp facility patching.** Facility visuals patched at warp start and full recalculation on warp exit.
- **Retired Stand-ins UI.** Game Actions window shows retired kerbals (stand-ins displaced by returning originals) with count header. Section hidden when empty.

### Kerbal Lifecycle Management

- **Crew end-state inference.** Each crew member's fate is inferred from the recording's terminal state and end-of-recording vessel snapshot (Aboard, Dead, Recovered, Unknown).
- **Reservation system.** Reserved kerbals (appearing in committed recordings) are set to Assigned status, preventing KSP from assigning them to new missions. Permanent reservations for dead crew, temporary for recovered.
- **Stand-in chain system.** When a reserved kerbal needs to fly again, a stand-in is hired with the same trait. Chains can go multiple levels deep. Stand-in names persisted in KERBAL_SLOTS ConfigNode.
- **Retirement tracking.** When a stand-in is displaced by the original returning, the stand-in becomes "retired" — kept in roster (may appear in recordings) but blocked from dismissal.
- **MIA respawn override.** KSP's respawn mechanic (Dead-to-Available after delay) is overridden on every recalculation — reserved kerbals stay Assigned regardless.

### Bug Fixes

- **Chain segment gap event loss (#204).** When the optimizer splits a recording into chain segments at TrackSection boundaries, events falling in the UT gap between consecutive segments (e.g., `RecordsAltitude` at the atmosphere/space transition) were silently lost. `NotifyLedgerTreeCommitted` now extends each chain continuation's event window backward to the predecessor's EndUT. In career mode, this prevented milestone rewards from being credited.
- **Ledger reconcile pruning KSC milestones (#205).** `Ledger.Reconcile` pruned earning-type actions with null `recordingId`, incorrectly removing legitimate KSC spending milestones like `FirstCrewToSurvive` that are not associated with any recording. Fixed condition to allow null-recordingId earnings through.
- **KerbalDismissalPatch Harmony failure (#206).** The patch targeting `KerbalRoster.Remove` failed with "Ambiguous match" because the method has multiple overloads. Switched to `TargetMethod()` with explicit parameter types `new[] { typeof(ProtoCrewMember) }`.
- **Duplicate CrewStatusChanged events (#207).** KSP's delayed `onKerbalStatusChange` callbacks fired after the crew mutation suppression window closed, producing redundant `Assigned->Missing` events on every save cycle. Added `KerbalsModule.IsManaged` check to suppress status change noise for reserved/stand-in kerbals.
- **Chain tip missing terminalState (#208).** In tree mode, the active recording is non-leaf (has debris branches) so `FinalizeIndividualRecording` skipped its terminalState. Added explicit terminalState determination for the active recording in `FinalizeTreeRecordings`, which the optimizer propagates to the chain tip via `SplitAtSection`.
- **Looping chain crew release (#209).** Fixing the terminalState alone would free crew at the tip's EndUT while the ghost still loops. Added a `loopingChains` pre-scan in `KerbalsModule.Recalculate`: Recovered crews on chains with any looping segment keep `endUT=Infinity`. Disabling the loop correctly releases them.
- **Ghost tunnels underground during atmospheric on-rails (#210, PR #116).** When a vessel went on-rails during atmospheric flight (e.g. reentry time warp), Keplerian orbit segments were recorded that ignore drag — the ghost dove through the planet. Added `ShouldSkipOrbitSegmentForAtmosphere` check: orbit segment creation is skipped when `altitude < atmosphereDepth`. Point interpolation lerps across the gap instead. Applies to FlightRecorder and BackgroundRecorder.

### Code Quality & Refactoring

- **RewindContext encapsulation (R3-3).** Extracted 8 scattered static rewind fields from `RecordingStore` into `RewindContext` static class with controlled `BeginRewind()`/`EndRewind()` mutation API.
- **Dock/undock state dedup (R3-5).** Extracted `ClearDockUndockState()` and `RestartRecordingAfterDockUndock()` from ParsekFlight, eliminating 4x duplicated cleanup and restart blocks.
- **Removed ActionReplay.** Legacy action replay system (499 lines) replaced by ledger-based recalculation.
- **Removed ResourceApplicator.** Legacy resource delta application (318 lines) replaced by KspStatePatcher.

### Bug Fixes

- **Fix #195: Ghost orbit lines not visible in tracking station.** Ghost ProtoVessels created in `SpaceTracking.Awake` prefix had null `orbitRenderer` because `MapView.fetch` wasn't set yet (Unity Awake ordering is undefined). `buildVesselsList` line 751 unconditionally accesses `vessel.orbitRenderer.onVesselIconClicked` with no try/catch — single NRE aborted the entire method including `ConstructUIList()`. Fix: added Prefix on `buildVesselsList` calling `EnsureGhostOrbitRenderers()` which uses Traverse to invoke private `AddOrbitRenderer()` on ghosts with null renderer. Also added defensive FLIGHTPLAN/CTRLSTATE/VESSELMODULES ConfigNode children to ghost ProtoVessel.
- **Fix: Ghost map missing in tracking station when TerminalOrbit fields empty.** `CreateGhostVesselsFromCommittedRecordings` only checked `HasOrbitData()` (terminal orbit fields) which returned false for all recordings. Now falls back to the last `OrbitSegment` — same pattern used by the flight scene's deferred creation path.
- **Fix: Duplicate ghost orbit lines during time warp across chain segments.** Multiple chain segments each created their own ghost map ProtoVessel during fast time warp. Added per-chain dedup via `chainMapOwner` dict — when a new chain segment creates a ghost map vessel, the previous segment's is removed.
- **Fix T41: Ghost orbit line persists after landing.** `CheckPendingMapVessels` skipped with `continue` when `FindOrbitSegment` returned null (ghost past all orbit segments). The stale orbit line remained indefinitely. Now removes the ghost map ProtoVessel when UT exits all orbit segments.

### Tests

- **4621 tests** (up from 2419 on main). 20 new test classes covering all game action modules, serialization round-trips, recalculation engine, kerbal reservation lifecycle, milestone patching, contract deadline injection, and ChainSegmentManager state management.

### Research & Documentation

- **3 API investigation reports** in `docs/dev/research/`: milestone ProgressNode tree, contract type registry, rescue docking detection.
- **Design document** updated to v0.5 (Phases 1-8 complete).
- **Recording format version 6** (v5-to-v6: SegmentEvents, TrackSections, ControllerInfo, extended BranchPoint types).

---

## 0.5.3

### Features

- **Departure-aware Real Spawn Warp.** When a nearby ghost will leave its current orbit before spawn time (e.g., parking orbit → Mun transfer), the RSW window now shows a "Departs T-Xm Xs" state column and replaces the "FF-Spawn" button with "FF-Depart" — an epoch-shifted warp to the departure moment that preserves rendezvous geometry. Orbit comparison uses SMA, eccentricity, inclination, and argument of periapsis (eccentric orbits only) with tight tolerances to detect any intentional maneuver. Handles SOI changes, surface terminal states, off-rails gaps, and return-trip scenarios.
- **T97: Altitude-based chain splits for airless bodies.** Recordings auto-split when crossing the approach altitude threshold on bodies without atmosphere (Mun, Minmus, Tylo, etc.). Uses KSP's native `timeWarpAltitudeLimits[4]` (100x warp limit) as the threshold, with `body.Radius * 0.15` as fallback. Enables selective looping of landing approaches without looping orbital coasts.
- **T97: "approach" phase tagging.** Airless body segments below the threshold are tagged `"approach"` (sky blue in UI) instead of `"space"`. All phase tagging sites updated.
- **T97: TrackSection altitude metadata.** Min/max altitude tracked per TrackSection during recording. Serialized as sparse keys, backward compatible with existing saves.
- **T97: Recording optimization pass.** Automatic housekeeping merges redundant consecutive chain segments on save load (same phase, same body, no branch points, no ghosting triggers, no user-modified settings).
- **T98: Per-phase looping for all recording modes.** Tree recordings are now split into per-phase segments after commit, matching chain mode's per-phase loop toggles. The optimizer's split pass (`FindSplitCandidatesForOptimizer`) breaks multi-environment recordings at environment boundaries without the conservative ghosting-trigger check. Each phase gets its own loop toggle in the UI. Auto loop range trims boring bookends (orbital coasts, surface idle) when loop is toggled on.
- **T98: Loop range fields.** New `LoopStartUT`/`LoopEndUT` fields on Recording narrow the loop range. Engine (`TryComputeLoopPlaybackUT`, `ShouldLoopPlayback`), save/load (both paths), and `CanAutoMerge` updated. Backward compatible (NaN defaults = existing behavior).
- **T98: Policy modularity refactor.** Migrated scattered `TreeId != null` / `ChainId != null` policy checks to `IsTreeRecording` / `IsChainRecording` / `ManagesOwnResources` query properties. Extracted `ClassifyVesselDestruction` and `ShouldSuppressBoundarySplit` as testable static methods.

### Ghost Map Presence (bug #60)

Ghost vessels now appear in KSP's tracking station, show orbit lines in map view, and can be targeted for rendezvous planning. Works for both ghost chain vessels and timeline playback ghosts.

- **ProtoVessel-based map integration** — lightweight ProtoVessel (single `sensorBarometer` part) per ghost provides automatic tracking station entry, orbit line (OrbitRenderer), clickable map icon (MapObject), and navigation targeting (ITargetable). Created on chain init or engine ghost spawn, removed on resolve/destroy/rewind/scene cleanup, stripped from saves.
- **Timeline playback + chain ghosts** — both recording-index ghosts (from the playback engine) and chain ghosts (from `VesselGhoster`) get ProtoVessels. Parallel tracking dicts with unified cleanup via `RemoveAllGhostPresenceForIndex`.
- **Deferred orbit line creation** — recordings that start pre-orbital (launch-to-orbit) don't show orbit lines during atmospheric ascent. ProtoVessel created when ghost enters first orbital segment, with the current segment's orbit (not terminal orbit).
- **Per-frame orbit segment tracking** — ghost ProtoVessel orbit updates as the ghost traverses segments (Hohmann transfers, SOI transitions). Both chain and recording-index ghosts use `ApplyOrbitToVessel` with direct element assignment via `Orbit.SetOrbit()`.
- **Terminal state filtering** — only Orbiting/Docked recordings get orbit lines. Destroyed, SubOrbital, Landed, Splashed skip (misleading orbit). Debris always skipped.
- **30 guard rails** across 10 source files — `IsGhostMapVessel(pid)` checks on all `FlightGlobals.Vessels` iteration sites and vessel GameEvent handlers.
- **6 Harmony patches** — `Vessel.GoOffRails` (prevent physics loading), `CommNetVessel.OnStart` (prevent duplicate CommNet nodes), `FlightGlobals.SetActiveVessel` (redirect to watch mode), `SpaceTracking.FlyVessel`/`OnVesselDeleteConfirm`/`OnRecoverConfirm` (block tracking station actions with screen message, release input lock via `OnDialogDismiss`).
- **Tracking station scene support** — `ParsekTrackingStation` addon creates ghost ProtoVessels from committed recordings when visiting tracking station directly.
- **Soft cap integration** — `Despawn` removes ProtoVessel; `ReduceFidelity` and `SimplifyToOrbitLine` keep it (orbit line stays visible when mesh is hidden).
- **Target transfer** — if ghost was the navigation target when chain resolves, the spawned vessel becomes the new target.
- **VesselType mirroring** — ghost uses the original vessel's type from snapshot for correct filter placement.
- **Green dot suppression** — `DrawMapMarkers` skips the old GUI overlay dot when a native KSP map icon exists for that ghost.
- **Merge dialog re-evaluation** — `MergeDialog.OnTreeCommitted` callback triggers chain re-evaluation so ghost ProtoVessels are created immediately after commit+revert.
- **46 tests** — PID tracking, HasOrbitData, ComputeGhostDisplayInfo, ResolveVesselType, terminal state filtering, debris filtering, StartsInOrbit, orbit segment tracking, log assertions.

### Group UI Enhancements

- **Group header columns in recordings window.** Groups now display Launch (earliest member StartUT), Duration (sum of member durations), and Status (closest active T- countdown) columns. Groups participate in column-based sorting alongside chains and standalone recordings instead of always rendering first. Six `internal static` helpers extracted for testability with 27 unit tests.
- **Fix #176: Group hide checkbox misaligned when expanded stats visible.** Group rows were missing spacers for the MaxAlt/MaxSpd/Dist/Pts columns, causing the trailing Hide checkbox to shift out of alignment when the Stats panel was open.

### Bug Fixes

- **Fix #175: EVA kerbal spawns at recording start position instead of endpoint.** EVA vessel snapshots are captured at EVA start (kerbal on the pod's ladder), but the kerbal walks elsewhere during the recording. On spawn, the snapshot's baked-in lat/lon/alt placed the kerbal on top of the parent vessel, grabbing its ladder and triggering KSP's "Kerbals on a ladder — cannot save" error. `ResolveSpawnPosition` now routes EVA recordings to the trajectory endpoint; `OverrideSnapshotPosition` patches the snapshot before `RespawnVessel`.
- **Fix #179: Orbital vessel destroyed by pressure on spawn.** Three-part fix: (1) `terminalOverridesUnsafe` includes `TerminalState.Orbiting`, allowing spawn eligibility. (2) KSC spawn defers orbital vessels to flight scene (Space Center `pv.Load()` crashes them through terrain). (3) Flight-scene spawn uses `SpawnAtPosition` for orbital vessels to construct correct Keplerian orbit from last trajectory point position+velocity — `RespawnVessel` used the raw ascent snapshot orbit whose periapsis was in atmosphere. Additionally, `SpawnAtPosition` now accepts an optional `terminalState` parameter: when the terminal state is Orbiting/Docked but `DetermineSituation` returns FLYING (last trajectory point captured during ascent at suborbital speed), the situation is overridden to ORBITING to prevent KSP's on-rails 101.3 kPa pressure check from destroying the vessel.
- **Fix #172: Ghost map icon position + orbit lines not rendering + icon click menu.** Three-part fix: (1) Replaced `Orbit.UpdateFromOrbitAtUT()` with `Orbit.SetOrbit()` in `ApplyOrbitToVessel` — the old path roundtripped through state vectors, introducing floating-point drift in `argumentOfPeriapsis` for near-circular orbits (confirmed 0.0m offset after fix). (2) Added `deferredCreatedEvents.Add()` to `UpdateLoopingPlayback` and `UpdateOverlapPlayback` in `GhostPlaybackEngine` — only `RenderInRangeGhost` was firing `OnGhostCreated`, so looping ghosts never got ProtoVessels and orbit lines never rendered. (3) Added `GhostIconClickPatch` (postfix on `objectNode_OnClick`) showing a popup near cursor with "Set As Target" / "Watch" options. Ghost orbit lines are visual-only (not clickable via `GhostOrbitCastPatch`) to avoid ambiguity with real vessels sharing the same orbit. Watch mode entry distance now reads user's `ghostCameraCutoffKm` setting instead of hardcoded 100km.
- **Fix #180: Clicking ghost vessel in tracking station traps user with input lock.** `GhostTrackingFlyPatch` blocked `FlyVessel` for ghost map vessels but didn't dismiss the dialog, leaving a stale input lock. Now calls `OnDialogDismiss` after blocking. Also fixed `GhostVesselSwitchPatch` overload targeting — replaced unreliable attribute-based `Type[]` with explicit `TargetMethod()` for `FlightGlobals.SetActiveVessel`.
- **Fix #171: Orbital ghost disappears during 50x time warp.** During warp >4x, ghosts with orbital segments are now exempt from zone-based mesh hiding (`ShouldExemptFromZoneHide` in `GhostPlaybackLogic`). Prevents orbital ghosts from completing playback while invisible in the Beyond zone.
- **Fix #172: Ghost destruction reason logged as "unknown".** `RetryHeldGhostSpawns` now passes per-action reason strings to `DestroyGhost`: `"held-spawn-succeeded"`, `"held-already-spawned"`, `"held-spawn-timeout"`, `"held-invalid-index"`.
- **Fix #173: Zero-point debris leaf recordings saved from same-frame destruction.** Added `PruneZeroPointLeaves` step in `FinalizeTreeRecordings` — removes leaf recordings with zero trajectory points, no orbit segments, and no surface position. Prevents `.prec` sidecar files and tree nodes for instantly-destroyed debris.
- **Fix #174: ChainWalker evaluates terminated chains every frame.** Two-level filtering: `IsTreeFullyTerminated` skips scanning trees where all leaves are Destroyed/Recovered; `EvaluateAndApplyGhostChains` excludes terminated chains from `activeGhostChains`.
- **Fix #158: Watch mode auto-follow picks debris instead of core vessel after separation.** `FindNextWatchTarget` now recursively descends through PID-matched continuations that have no active ghost (boundary seed recordings). If the PID-matched continuation exists but has no ghost anywhere in its subtree, debris fallback is suppressed — watch hold expires naturally instead of following a booster.
- **Fix #168: Spawned vessels not re-spawned after rewind/revert.** After vessel stripping on revert, `SpawnedVesselPersistentId` was restored from the save but pointed to a stripped vessel — blocking re-spawn permanently. Added `ReconcileSpawnStateAfterStrip` that checks surviving PIDs in flightState after all strip operations and resets spawn tracking for recordings whose vessel no longer exists.
- **Fix #170: Vessel spawned near launch pad collides with infrastructure, chain-explodes player's rocket.** Added 50m KSC exclusion zones around launch pad and runway start point (`IsWithinKscExclusionZone` in `SpawnCollisionDetector`) to block spawns near infrastructure. Fixed `RemoveDeadCrewFromSnapshot` to remove reserved crew who are Dead (reservation no longer overrides death). Added `ShouldBlockSpawnForDeadCrew` guard to abandon spawn when all crew are dead.
- **Fix #72: GhostCommNetRelay antenna combination formula wrong for non-combinable strongest.** Extracted `ResolveCombinationExponent` pure method. When the overall strongest antenna is non-combinable, the combination exponent now comes from the strongest *combinable* antenna, matching KSP's actual formula.
- **Fix #81: TrackSection struct shallow copy shares mutable list references.** Extracted `Recording.DeepCopyTrackSections` that creates independent `frames` and `checkpoints` lists for each copied TrackSection. Used in `ApplyPersistenceArtifactsFrom`.
- **Fix #122: Dead->Dead crew status identity transitions logged as events.** Added `IsRealStatusChange` guard in `GameStateRecorder.OnKerbalStatusChange` to filter identity transitions before recording.
- **Fix #123: #autoLOC localization keys in internal log messages.** Wrapped `v.vesselName` in `TimeJumpManager` and `other.vesselName` in `SpawnCollisionDetector` with `Recording.ResolveLocalizedName()`.
- **Fix #131: Explosion GO count can reach ~90 for overlapping reentry loops.** Added `MaxActiveExplosions = 30` cap in `TriggerExplosionIfDestroyed`. New explosions are skipped (with logging) when at cap; ghost parts are still hidden.

- **Fix #78: DetermineTerminalState maps DOCKED to Orbiting.** Changed `case 128` (DOCKED) to return `TerminalState.Docked` instead of `TerminalState.Orbiting`. Edge case for debris that docks.
- **Fix #80: TimeJumpManager.ExecuteJump no warp guard.** Added warp stop at the start of `ExecuteJump` — calls `TimeWarp.SetRate(0, true)` when `CurrentRateIndex > 0` to prevent desync from `SetUniversalTime` during warp.
- **Fix #75: GhostPlaybackLogic inconsistent negative interval handling.** Added early guard in `ComputeLoopPhaseFromUT` for `currentUT < recordingStartUT`, consistent with `TryComputeLoopPlaybackUT`. Removed redundant duplicate guard.
- **Fix #82: IsDebris, Controllers, SurfacePos not serialized for standalone recordings.** Added save/load for all three fields in `ParsekScenario.SaveStandaloneRecordings` / `LoadStandaloneRecordingsFromNodes`, matching the tree recording pattern.

- **Fix #134: CleanupOrphanedSpawnedVessels destroys freshly-spawned past vessels after rewind.** The rewind path populated `PendingCleanupNames` with all recording vessel names for `StripOrphanedSpawnedVessels`, but left them set for `CleanupOrphanedSpawnedVessels` in `OnFlightReady`, which then destroyed correctly-spawned past vessels. Fix: clear `PendingCleanupPids`/`PendingCleanupNames` immediately after the strip completes.
- **Fix #43: Update known-bugs status.** Shader fallback lookup (`FindShaderOnRenderers`) was already implemented in commit 25ccfa9 but doc status was stale.
- **Fix #95: Preserve VesselSnapshot on committed recordings.** Removed snapshot nulling from continuation vessel destroyed and EVA boarding handlers. `VesselDestroyed` flag gates spawn and is now reset by `ResetRecordingPlaybackFields` on revert/rewind. `UpdateRecordingsForTerminalEvent` skips all committed recordings. Items 3-5 (continuation sampling/refresh) deferred as tech debt.
- **Fix #96: Hold ghost until spawn succeeds.** Ghost no longer disappears when spawn is blocked or warp-deferred. `HandlePlaybackCompleted` holds the ghost at its final position via `heldGhosts` dict. `RetryHeldGhostSpawns` retries each frame, releasing on success or 5s timeout.
- **Fix #99: Spawn real vessels at KSC when ghost timelines complete.** `ParsekKSC.TrySpawnAtRecordingEnd` calls `VesselSpawner.RespawnVessel` when ghosts exit range. Chain mid-segment suppression via `IsChainMidSegment`. `OnSave` auto-unreserve guarded at SpaceCenter to prevent snapshot pre-emption.

- **Fix #48: Use actual body radius in ComputeBoundaryDiscontinuity.** Replaced hardcoded Kerbin radius (600,000m) with lookup from static dictionary of 17 stock KSP body radii. Diagnostic-only fix — logged discontinuity magnitude is now accurate on all bodies.
- **Fix #77: Use InvariantCulture for TerrainCorrector log formatting.** Replaced 8 `{val:F1}` interpolation sites with `.ToString("F1", IC)` to prevent comma-decimal output on non-English locales.
- **Fix #73: Filter vessel types in CheckWarningProximity.** Extracted `ShouldSkipVesselType` helper (Debris/EVA/Flag/SpaceObject) shared between `CheckOverlapAgainstLoadedVessels` and `CheckWarningProximity`.
- **Fix #129: Strip future PRELAUNCH vessels on rewind.** Unrecorded pad vessels from the future persisted after rewind because `StripOrphanedSpawnedVessels` only matched recorded names. Added PID-based quicksave whitelist: `PreProcessRewindSave` captures surviving vessel PIDs, `HandleRewindOnLoad` strips any PRELAUNCH vessel not in the whitelist.
- **Fix #137: Rescue reserved crew from Missing after EVA vessel removal.** `vessel.Unload()` in `RemoveReservedEvaVessels` orphaned crew → KSP set them Missing. Added `RescueReservedCrewAfterEvaRemoval` to restore Missing→Assigned for crew in `crewReplacements` dict.
- **Fix #64: Clear pending tree/recording on revert.** Merge dialog shown twice when reverting during tree destruction. `pendingTree` (static) persisted across scene transitions without cleanup. Now discarded in the OnLoad revert path.
- **Fix #71: Remove old CommNode before re-registration.** `RegisterNode` now removes existing node from CommNet before adding new one, preventing orphaned nodes.
- **Fix #79: SpawnCrossedChainTips no longer mutates caller's dict.** Returns spawned PIDs list; caller removes after call.
- **Fix #84: int→long for cycleIndex.** Prevents integer overflow in loop phase calculations for very long sessions. Updated across 10 files (state, events, logic, engine, KSC, flight).
- **Fix #101: BackgroundRecorder.SubscribePartEvents now called.** Part events (onPartDie, onPartJointBreak) are now subscribed for background vessels at both tree creation sites.
- **Fix #102: CreateSplitBranch copies FlagEvents and SegmentEvents.** Previously omitted, causing flag/segment data loss on tree split.
- **Fix #130: Cache vesselName on GhostPlaybackState.** Destroy events now show vessel name even when trajectory reference is null (loop restart).

- **Fix #139: Merge dialog not shown on revert to launch.** Bug #64 fix unconditionally discarded freshly-stashed pendings. Added `PendingStashedThisTransition` flag to distinguish fresh (keep) vs stale (discard) pendings across scene transitions.
- **Fix #140: Camera resets to active vessel on loop ghost cycle boundary.** Non-destroyed looped ghosts left FlightCamera with null target between destroy/respawn. `ExplosionHoldEnd` now creates a temporary camera bridge anchor; `RetargetToNewGhost` cleans it up.
- **Fix #141: Budget deduction drives science/funds/reputation negative.** Extracted `ClampDeduction(reserved, available)` pure method. All three resource types clamped to available balance.
- **Fix #142: Ghosts spawning into dying scene after DestroyAllGhosts.** Added `sceneChangeInProgress` flag to suppress `Update()`/`LateUpdate()` after `OnSceneChangeRequested`.
- **Fix #143: ApplyTreeResourceDeltas per-frame no-op overhead.** Added fast-path early-out when all trees already have `ResourcesApplied=true`.
- **Fix #144: Degraded trees (0 points) deduct budget.** Extracted `RecordingTree.IsDegraded`/`ComputeEndUT()`. Trees with no trajectory data skip budget application.
- **Fix #145: Ghoster WARN spam for non-existent synthetic vessels.** Pre-check vessel existence before ghosting; downgraded to VERBOSE.
- **Fix #22 (revised): Facility upgrade replay deferred instead of dropped.** Facility upgrades in Flight scene now set `deferred=true`, stopping the watermark so they are retried on next scene load (previously marked as replayed and permanently skipped).
- **Fix #146: Ghost frozen at final position after watch hold.** `watchEndHoldUntilUT` was set but never consumed — ghost held indefinitely. Added expiry check in `UpdateWatchCamera` that retries auto-follow during hold, then destroys ghost on timeout.
- **Fix #147: Watch mode auto-follow race condition.** Continuation ghost not yet spawned when `FindNextWatchTarget` runs at completion. Hold timer now retries every frame; auto-follows as soon as continuation appears.
- **Fix #148: Fast-forward doesn't transfer watch to target.** `FastForwardToRecording` now exits watch and defers entering on the FF target after engine positions ghosts.
- **Fix #149: RCS throttle event spam.** Deadband increased from 1% to 5% — reduces RCS part events by ~90% for SAS-active flights.

- **Fix #135: ParsePartPositions wrong key.** `SpawnCollisionDetector.ParsePartPositions` only checked `"pos"` but KSP vessel snapshots use `"position"`. Parsed 0/40 parts on real vessels, falling back to inaccurate 2m bounds. Now checks both keys.
- **Fix #150: Engine/RCS FX not stopped at on-rails.** `FlightRecorder.OnVesselGoOnRails` now calls `EmitTerminalEngineAndRcsEvents()` before going on-rails. Ghost engine plumes no longer persist during orbit segments.
- **Fix #151: FF watch renders broken scene.** Added 100km distance guard in `EnterWatchMode` — refuses watch when ghost is beyond rendering-safe distance from active vessel. Rate-limited `FindNextWatchTarget` logging during watch hold.
- **Fix: Enter key on camera cutoff input.** Enter key now commits the value (KeyDown was consumed by TextField before the check ran).
- **Fix #74: RELATIVE mode boundary point at on-rails.** `SamplePosition` recorded absolute lat/lon/alt into a RELATIVE TrackSection at on-rails boundaries. Moved RELATIVE clearing before boundary sampling.
- **Fix #107: Engine/SRB smoke trails vanish on ghost despawn.** Particle systems are now detached from the ghost hierarchy before destruction, allowing trails to fade naturally (8s linger).
- **Fix #125: Engine plate shrouds not visible on ghost.** Inactive-variant renderer filter preempted GAMEOBJECT rules. When explicit variant rules exist, they are now the sole authority on object inclusion.
- **Engine throttle deadband increased to 5%.** SRBs with smooth thrust curves generated excessive EngineThrottle events at 1% deadband. Matches RCS deadband (#149).
- **Fix #56: Auto-record EVA from any vessel situation.** Removed PRELAUNCH restriction — kerbals EVA'ing from landed bases, orbiting stations, etc. now auto-record.
- **Fix #57: Boarding confirmation timeout too short.** Increased from 3 frames (~60ms) to 10 frames (~200ms).
- **Fix #115/#116: Crew lost to Missing after rewind vessel strip.** New `RescueOrphanedCrew` sets orphaned Assigned crew to Available after vessel stripping, before KSP's validation marks them Missing.
- **Fix #155: Orphaned recording lost on auto-record vessel switch.** `StartRecording` now commits the orphaned recorder's data before creating a new one.
- **Fix #76: GhostExtender hyperbolic fallback negative altitude.** Added `Math.Max(0, ...)` to prevent ghost underground placement.
- **Fix #157: Green sphere ghost for ghost-only debris.** `ApplyVesselDecisions` now preserves `GhostVisualSnapshot` before nulling spawn snapshot.
- **Fix #161: EVA snapshot situation stale.** `ShouldSpawnAtRecordingEnd` overrides unsafe-situation check when terminal state is Landed/Splashed.
- **Fix #162: AutoCommitGhostOnly strips snapshot from landed EVAs.** Preserves `VesselSnapshot` for Landed/Splashed terminals.
- **Fix #163: KSC spawns vessels from the future after rewind.** `ShouldSpawnAtKscEnd` now checks `currentUT >= EndUT`.
- **Fix #165: Engine flame flash on ignition.** Seed events for engines at zero throttle (staged but idle on the pad) are now skipped entirely — no plume at playback start. The first real throttle-up event starts the plume at the correct time. Playback retains `Math.Max(0.01f)` floor for backward compatibility with older recordings.
- **Fix #169: EVA vessel spawned FLYING destroyed by on-rails pressure.** EVA snapshot captured `sit=FLYING` but terminal state was Landed. KSP's on-rails pressure check killed the vessel instantly, crew set to Dead. `CorrectUnsafeSnapshotSituation` now corrects FLYING/SUB_ORBITAL to LANDED/SPLASHED before spawning when terminal state indicates safe surface arrival.
- **Fix #164: Strip all future vessels on rewind, not just PRELAUNCH.** Flags, landed capsules, and other player-created vessels from the future now removed after rewind.
- **Fix #167: Crew swap not executed for KSC-spawned vessels.** `SwapReservedCrewInFlight` only runs in flight scene — KSC spawns via `TrySpawnAtRecordingEnd` never swapped reserved crew. Added `SwapReservedCrewInSnapshot` to replace reserved crew names directly in the snapshot ConfigNode before spawning.
- **DeferredActivateVessel timeout increased** from 10 frames to 5 seconds. Distant spawned vessels (37km+) couldn't load in 10 frames.
- **ComputeTotal logging removed.** Eliminated 52% of all Parsek log output (pure computation was logging every UI frame).
- **Status column widened** (95→120px) for longer T+ timestamps.
- **R/FF button state transition logging** for debugging enable/disable issues.
- **Fix #197: Ghost ProtoVessel missing for stable orbits reported as SUB_ORBITAL.** KSP reports `SUB_ORBITAL` for off-rails vessels in stable orbits (e.g., Mun orbit). Added `DetermineTerminalState(int, Vessel)` overload that checks `orbit.eccentricity < 1.0 && orbit.PeR > body.Radius` to override to Orbiting, enabling ghost ProtoVessel creation and orbit line rendering.
- **Fix #198: Duplicate green dot map markers during time warp.** During warp, multiple chain segments can be active simultaneously (short segments from optimizer splits). Added per-chain dedup in `DrawMapMarkers` — only the highest-index (latest) ghost per chain draws a marker.
- **Fix #199: Checkpoint log spam.** Downgraded `CheckpointAllVessels` and warp-rate-changed messages from Info to Verbose (~8,800 lines per Mun mission).
- **Fix: Recording optimizer hang on save load.** `RunOptimizationPass` called `SaveRecordingFiles` synchronously during OnLoad for every merge/split — with 400k+ trajectory points this blocked the main thread long enough for Windows to kill KSP as not responding. File saves now deferred to OnSave.
- **Fix: OnSave re-serializing unchanged recordings.** Added `FilesDirty` flag on Recording — OnSave skips `SaveRecordingFiles` for recordings whose sidecar data hasn't changed. Eliminates ~80 seconds of redundant I/O per session for large saves.
- **Fix #200: 128km trajectory gap at environment transitions.** Environment hysteresis transitions and anchor detection transitions called `CloseCurrentTrackSection` without sampling a boundary point. Added `SamplePosition(v)` before all 4 affected `CloseCurrentTrackSection` calls — matches the on-rails transition pattern.
- **Fix #202: Spawned vessel deleted when switching to it.** Vessel switching triggers FLIGHT→FLIGHT scene reload, which was misidentified as a revert. The spawned vessel was stripped, re-spawned, then immediately deleted by the orphan cleanup. Added `vesselSwitchPending` flag via `onVesselSwitching` to distinguish vessel switches from reverts.
- **Fix #201: Optimizer split creates temporal gap at section boundaries.** `SplitAtSection` now interpolates a synthetic boundary point at exactly `splitUT` when no trajectory point falls at the split time. Both halves share the boundary point, eliminating visible jumps during chain playback.
- **Fix map marker camera in flight view.** `DrawMapMarkerAt` used `PlanetariumCamera` unconditionally — correct for map view but wrong for flight view. Now uses `FlightCamera` in flight view, `PlanetariumCamera` + ScaledSpace in map view.
- **Default recordings sort: Launch time ascending.** Recordings window now defaults to chronological order (earliest launch at top) instead of index order.
- **Ghost orbit lines for intermediate chain segments.** ProtoVessels and orbit lines now appear during every coast phase (transfer orbits, parking orbits), not just the terminal orbit. `CreateGhostVesselFromSegment` builds from OrbitSegment data when terminal orbit is unavailable.
- **Fix: 10 tests failing due to FindObjectsOfType in KspStatePatcher.** `PatchDestructionState` called `UnityEngine.Object.FindObjectsOfType<DestructibleBuilding>()` which throws `SecurityException` outside Unity. `protoUpgradeables` is an empty dict (not null) in the test env, so `PatchFacilities` fell through to the Unity call. Added `SuppressUnityCallsForTesting` guard.

### Features

- **Settings window: "Ghosts" group.** Merged "Ghost Camera" and "Ghost Soft Caps" sections into a single "Ghosts" group. Added checkbox-to-label spacing for all settings toggles.
- **Fix #50: Chain block enable/loop checkboxes.** Chain headers now have aggregate enable and loop checkboxes (were empty spacers).
- **Fix #98: Merge Countdown into Status column.** Status now shows `T-Xm Xs` for future, `Active` for playing, terminal state name for past.
- **Fix #88: Commit approval dialog for landed/splashed vessels.** When leaving Flight to KSC or Tracking Station with a landed/splashed vessel, shows Keep/Discard dialog instead of auto-committing. Game exit still auto-commits.

- **Ghost camera cutoff setting.** Settings > Ghost Camera > Cutoff [300] km. Watch mode auto-exits when ghost exceeds this distance. Watch button disabled for ghosts beyond cutoff. Default 300km, configurable 10-10000km.
- **Watch mode distance overlay.** "Watching: Vessel (45.2 km)" in the notification bar shows distance from ghost to active vessel.
- **Watch mode auto-follow on stage separation.** Camera automatically follows the controller vessel through tree branch points and chain continuations.
- **T+ mission time in Countdown column.** Past/live recordings show elapsed time since launch (T+Xh Ym Zs) instead of "LIVE" or "-".
- **Debris subgroups in recordings window.** Debris recordings from stage separations are auto-grouped under a "Vessel / Debris" subgroup. Orphaned split segments adopted into the tree group on commit.

### Previously Fixed (Confirmed)

- **#43** (shader fallback), **#49** (RealVesselExists O(n)) — already fixed in prior releases.
- **#121** (Ghost SKIPPED log spam) — resolved by T25 Phase 9 engine extraction.
- **#133** — removed 6 dead forwarding methods + 2 unused properties from ParsekFlight, inlined call sites.
- **#63** — added `errorWhitelist` parameter to `ParsekLogContractChecker.ValidateLatestSession`.
- **#83** — CommNet stale nodes concern is not a bug; follows stock KSP re-registration pattern.
- **#108** — engine cutoff polling logic (`EngineIgnited && isOperational`) correctly catches flameout; remaining inconsistency needs in-game repro.
- **#113** — stock FX modules infeasible on ghost architecture; current reimplementation is correct.
- **#153** — AnimateHeat nose cone classification requires reflection hack for negligible visual effect; won't fix.
- **#156** — extracted `IsPadFailure` static method + 7 tests; remaining items need Unity runtime.

Log spam audit and cleanup. Analyzed a 28,923-line KSP.log from a 70-second KSC session with 273 recordings — Parsek was 68.4% of all output (19,771 lines). Identified and fixed the top spam sources.

### Log Cleanup

- **Removed `ParsekLog.Log()` method** — all 26 call sites (16 in EngineFxBuilder, 10 in GhostVisualBuilder) were using the subsystem-less `Log()` wrapper, producing 2,651 lines tagged as `[General]` (55% of all INFO output). Migrated to proper `Verbose("EngineFx")` / `Verbose("GhostVisual")` / `Info("GhostVisual")`. Deleted the method to prevent future untagged usage.
- **ReentryFx INFO→VERBOSE** — mesh combination and fire shell overlay messages fired per ghost build at INFO level (2,148 lines in 70s). Downgraded to Verbose.
- **KSC per-ghost spawn/destroy INFO→VERBOSE** — per-ghost spawn, enter-range, re-show, warp-hide, and no-longer-eligible messages at INFO level (1,347 lines). Downgraded to Verbose. Added batch summary in OnDestroy (`Destroyed N primary + N overlap KSC ghosts`).
- **FlightRecorder point logging rate-limited** — `Recorded point #N` logged every 10th physics frame at Verbose without rate limiting (~50 lines/sec during recording). Changed to `VerboseRateLimited` with 5s interval.
- **Mass ghost teardown batched** — KSC `DestroyKscGhost` per-ghost log (277 consecutive in one burst) changed to `VerboseRateLimited`. Overlap ghost destroy in `GhostPlaybackEngine` similarly rate-limited.
- **Per-renderer VERBOSE diagnostics removed** — individual MR[N]/SMR[N] per-renderer logs (1,041+ lines), per-renderer damaged-wheel skip logs, and per-SMR bone fallback logs removed. Per-part summary already captures the same counts.
- **Subsystem tag consolidation** — `Store`→`RecordingStore` (1 occurrence), `GhostBuild`→`GhostVisual` (5 occurrences). Reduces tag count from 63 to 61.

### Round 2 — Ghost Lifecycle Batch Logging

- **Frame batch summary** — replaced per-ghost spawn/destroy/build Verbose logs (15,489 Engine lines) with per-frame counters and one `VerboseRateLimited` summary: `Frame: spawned=N destroyed=N active=N`.
- **DestroyGhost reason parameter** — all 7+ call sites now pass a reason string (`"cycle transition"`, `"soft cap despawn"`, `"anchor unloaded"`, etc.). Per-ghost destroy log restored at 1s rate limit with full context.
- **SpawnGhost per-ghost log restored** — 1s rate-limited per-index key with build type (snapshot/sphere), part/engine/rcs counts.
- **ShouldTriggerExplosion skip logs removed** — 1,959 lines/session of pure predicate noise (caller already knows the result).
- **CrewReservation null snapshot log removed** — 515 lines of expected-path noise.
- **ReentryFx → shared rate-limit keys** — mesh combination messages now dedup across all ghosts (was per-ghost-index).
- **Overlap/explosion lifecycle → shared VRL keys** — overlap move, overlap expired, explosion created, parts hidden, loop restarted, overlap expired all changed from per-index to shared keys.
- **Zone rendering Info→VRL** — per-ghost zone transition messages downgraded from Info to VerboseRateLimited (1,008 lines).
- **Bug #135 cleanup** — fixed 12 garbled comments in ShouldSpawnAtRecordingEnd left from prior partial edit.

### Round 3 — Serialization Batch Summaries

- **Per-recording serialization logs removed** — 12 Verbose logs in RecordingStore (orbit segments, track sections, segment events, file summaries) and 2 per-recording metadata logs in ParsekScenario removed. These produced ~2,900 lines per save/load cycle.
- **4 batch summaries added** — standalone save/load and tree save/load now log one summary each with aggregate counters (points, orbit segments, part events, track sections, snapshots).
- **DeserializeSegmentEvents** — changed from always-log to Warn-only when events are skipped.

### Round 4 — Remaining Spam Sources

- **SpawnWarning FormatChainStatus** — Verbose → VerboseRateLimited shared key. Per-frame poll logging identical status (1,165 lines, 802-line burst).
- **Zone transition per-ghost** — Info → VerboseRateLimited shared key. 248-ghost bursts at scene switch collapsed to 1 line.
- **Scenario per-recording index dump** — Info → Verbose. Summary header stays at Info; per-recording detail demoted.
- **Per-recording "Loaded recording:"** — ScenarioLog (Info) → Verbose. Batch summary covers aggregates.
- **"Triggering explosion"** — Info → VerboseRateLimited per-index 10s. Looping overlap re-explosions deduplicated.

### Documentation

- Log audit report: `docs/dev/log-audit-2026-03-25.md`
- CLAUDE.md: added batch counting convention to Logging Requirements, removed obsolete `ParsekLog.Log` reference

### Design & Research

- Ghost orbits & trajectories investigation document (12 scenarios, 37 edge cases)
- KSP API decompilation reference (17 classes: ProtoVessel, OrbitRenderer, MapObject, SpaceTracking, etc.)
- KSPTrajectories mod architecture analysis (rendering, coordinate transforms, NavBall integration)

---

## 0.5.2

Second-pass structural refactoring + game action system modularization + continued decomposition. ~80 method extractions, ~105 logging additions, 103 new tests. 1 latent bug fixed, 1 latent IMGUI bugfix. Zero logic changes (except bug fixes).

### Code Refactor

- **Pass 1 — Method extraction + logging + tests** across 18 source files
  - `AddPartVisuals` reduced from 802 → 454 lines (parachute, deployable, heat phases extracted)
  - `RecordingStore` POINT/ORBIT serialization dedup (-140 lines, 4 shared helpers)
  - `ParsekScenario.OnLoad` split from 587 → ~450 lines (HandleRewindOnLoad, DiscardStalePendingState, LoadRecordingTrees)
  - `ParsekFlight.OnSceneChangeRequested` split from 205 → ~50 lines
  - `FlightRecorder` triple-dedup: FinalizeRecordingState shared across StopRecording/StopRecordingForChainBoundary/ForceStop
  - `FlightRecorder.CreateOrbitSegmentFromVessel` dedup (was duplicated in 4 sites)
  - `GhostPlaybackLogic.BuildDictByPid<T>` replaces 6 identical dict-construction blocks
  - `PartStateSeeder.EmitSeedEvents` -60 lines via local emit helper
  - `GhostChainWalker` zero-logging gaps fixed (4 methods now have full diagnostics)
  - `GhostExtender.PropagateOrbital` split from 83 → 15 lines (ComputeOrbitalPosition + CartesianToGeodetic)
- **Pass 2 — Architecture analysis** (dependency graph, static state inventory, cross-file duplication analysis)
- **Pass 3 — SOLID restructuring**
  - `EngineFxBuilder` extracted from GhostVisualBuilder (-975 lines)
  - `MaterialCleanup` MonoBehaviour extracted to own file
  - Loop constants consolidated into GhostPlaybackLogic
  - Shared ghost interpolation extracted to TrajectoryMath
  - `BudgetSummary` and `UIMode` nested types extracted to top-level
  - Dead code removed: `GetFairingShowMesh`, `GenerateFairingTrussMesh` (zero call sites)
  - `SanitizeQuaternion` unnecessary instance wrapper removed
- **T25 — Ghost Playback Engine extraction** (ParsekFlight 9900 → 8657 lines)
  - `GhostPlaybackEngine` (1553 lines) — extracted ghost lifecycle, per-frame rendering, loop/overlap playback, zone transitions, soft caps, reentry FX from ParsekFlight. Zero Recording references; accesses trajectories via `IPlaybackTrajectory` interface only. Fires lifecycle events (OnGhostCreated, OnPlaybackCompleted, OnLoopRestarted, etc.) for policy layer.
  - `ParsekPlaybackPolicy` (192 lines) — event subscriber handling spawn decisions, resource deltas, camera management, deferred spawn queue.
  - `IPlaybackTrajectory` interface — 19-property boundary exposing only trajectory/visual data from Recording. Enables future standalone ghost playback mod.
  - `IGhostPositioner` interface — 8 positioning methods implemented by ParsekFlight, delegates world-space placement to the host scene.
  - `GhostPlaybackEvents` — TrajectoryPlaybackFlags, FrameContext, lifecycle event types, CameraActionEvent for watch-mode decomposition.
  - 109 new tests (MockTrajectory, engine lifecycle, query API, interface isolation, log assertions)
- **Pass 4 — Continued dedup**
  - `SampleAnimationStates` unified core extracted from 4 near-identical methods (D15/T27, -139 lines)
  - `AnimLookup` enum + `FindAnimation` resolver parameterize 3 animation lookup strategies
  - 4 animation sample caches consolidated into 1 `animationSampleCache`
  - `CommitBoundaryAndRestart` shared tail extracted from atmosphere/SOI split handlers (D7)
- **Pass 5 — Game action system modularization** (ParsekScenario reduced by ~1020 lines)
  - `GroupHierarchyStore` extracted — UI group hierarchy + visibility (~200 lines, zero coupling to crew/resources)
  - `ResourceApplicator` extracted — resource ticking (TickStandalone, TickTrees), budget deduction, rewind baseline correction. Coroutine shells stay on ParsekScenario.
  - `CrewReservationManager` extracted — crew reservation lifecycle (Reserve/Unreserve/Swap/Clear), replacement hiring, EVA vessel cleanup. ~40 call sites updated across 7 source files.
  - `ResourceDelta` struct + `ComputeStandaloneDelta` added to ResourceBudget — pure testable delta computation
  - `SuppressActionReplay` + `SuppressBlockingPatches` merged into single `IsReplayingActions` flag
  - `ActionReplay.ParseDetailField` removed, callers use `GameStateEventDisplay.ExtractDetailField`
  - Guard logs added to all silent early-return paths in ResourceApplicator and CrewReservationManager
- **Pass 6 — GhostPlaybackEngine decomposition** (D5, D8)
  - `ApplyFrameVisuals` extracted — deduplicates part events + flag events + reentry FX + RCS toggle from 4 call sites. `skipPartEvents` parameter preserves Site 1 semantics.
  - `RenderInRangeGhost` (~84 lines) + `HandlePastEndGhost` (~47 lines) extracted from `UpdatePlayback` loop body. Loop body reduced from ~207 to ~70 lines.
- **Pass 7 — ChainSegmentManager extraction** (T26, ParsekFlight 8657 → 8098 lines)
  - `ChainSegmentManager` (686 lines) — owns 16 chain state fields + 16 methods. ~150 field accesses migrated from ParsekFlight. `ClearAll()` replaces 13-line scattered reset.
  - Phase 1: State isolation (16 fields moved, `StopContinuation`/`StopUndockContinuation` moved)
  - Phase 2: 12 methods moved (Group A: 8 continuation methods. Group B: 4 commit methods refactored with recorder-as-parameter + bool return for abort handling)
  - `CommitSegmentCore` shared pattern (T28/D2) — stash/tag/commit/advance extracted with `Action<Recording>` callback for per-method customization. All 4 commit methods delegate to core (nullable CaptureAtStop handled for boundary splits).
  - `ClearChainIdentity()` — replaces inline 4-field reset patterns in 3 locations
  - 3 orchestration methods stay on ParsekFlight (HandleDockUndockCommitRestart, HandleChainBoardingTransition, CommitBoundaryAndRestart — own StartRecording lifecycle)
- **Pass 8 — UI dedup** (T30/D18, D19)
  - `HandleResizeDrag` + `DrawResizeHandle` static helpers — 4 drag blocks + 4 handle blocks replaced with 8 one-liner calls
  - `DrawSortableHeaderCore<TCol>` generic method — unifies `DrawSortableHeader` and `DrawSpawnSortableHeader` via `ref` sort state + `Action onChanged`. `ToggleSpawnSort` removed.
- **Pass 9 — Encapsulation** (T33)
  - `GroupHierarchyStore` accessor migration — 5 new accessor methods (`AddHiddenGroup`, `RemoveHiddenGroup`, `IsGroupHidden`, `TryGetGroupParent`, `HasGroupParent`). All ~20 ParsekUI.cs direct field accesses migrated to accessors/read-only properties.
- **Performance**
  - Per-frame `List<PartEvent>` allocations eliminated — 4 transition-check methods now append to reusable buffer (T19)
  - `TimelineGhosts` dictionary cached per-frame instead of allocating on every property access (T20)
  - `ResourceBudget.ComputeTotal` cached per-frame, shared across `DrawResourceBudget` and `DrawCompactBudgetLine` (T21)
  - Chain ghost `cachedIdx` persisted on `GhostChain` — O(n) → O(1) amortized trajectory lookup (T9)
  - `RealVesselExists` HashSet cache — O(n) linear scan → O(1) per frame with manual invalidation (T10)
- **Ghost Soft Caps** (T5)
  - `ReduceFidelity` implemented — disables 75% of renderers by index for coarse LOD silhouette
  - `SimplifyToOrbitLine` improved — hides ghost mesh with `simplified` flag, frame-skip to avoid re-processing
  - Caps-resolved branch restores fidelity and re-shows simplified ghosts
- **Audits**
  - C2: namespace consistency verified — all 73 files correct (`Parsek` or `Parsek.Patches`)
  - C3: one-class-per-file verified — 5 files have multiple types but all are acceptable data-type bundles or tightly coupled enum+class pairs
  - C4: inventory doc line counts updated to final values

### Bug Fixes

- **KSC ghost heat initialization** — KSC scene ghosts now properly start heat-animated parts in cold state. Previously, the KSC private copy of `PopulateGhostInfoDictionaries` missed the cold-state initialization that the flight scene had. Fixed by deleting the private copy and calling the shared `GhostPlaybackLogic` version.
- **Group Popup drag event leak** — Group popup window resize drag was missing `Event.current.Use()` on MouseDrag, allowing drag events to fall through to underlying windows. Fixed by extracting shared `HandleResizeDrag` helper that applies `Use()` uniformly across all 4 windows (T30/D18).
- **RestoreGhostFidelity renderer over-enable** — `RestoreGhostFidelity` previously re-enabled all renderers unconditionally, overriding part-event visibility state (decoupled/destroyed parts could reappear for one frame after soft cap resolution). Now tracks which renderers were disabled by `ReduceGhostFidelity` and only re-enables those.
- **CommitSegmentCore log index off-by-one** — Post-commit log message showed the *next* segment's index instead of the committed segment's index. Now captures index before increment.
- **ParsekUI build error** — Missing `using System` for `Action` type in `DrawSortableHeaderCore<TCol>` generic method.
- **Simplified ghost re-shown by warp-down logic** — `SimplifyToOrbitLine` soft cap hid a ghost (`activeSelf=false`, `simplified=true`), but the warp-down re-show logic saw an inactive ghost in a non-Beyond zone and re-activated it, defeating the soft cap. Fixed by adding `!state.simplified` to both re-show conditions.
- **CommitVesselSwitchTermination orphaned undock continuation** — Only cleaned up vessel continuation (`ContinuationVesselPid`) but not undock continuation. Could leave an active undock continuation until next `ClearAll()`.
- **StopContinuation incomplete reset** — Did not reset `ContinuationLastVelocity`/`ContinuationLastUT`, asymmetric with `ClearAll()` and `StopUndockContinuation`.
- **Log spam cleanup** — "Terminated chain spawn suppressed" (26k lines/session) rate-limited; "GetCachedBudget" (6.8k) rate-limited; per-save serialization logs (4k) downgraded to Verbose; explosion FX spawn log (237) downgraded to Verbose; redundant "0 segment events" log (1.8k) removed. Total ~53% reduction in Parsek log output.

### Test Suite Audit (T32)

Deep audit of all 110 test files (~55k lines). 43 files changed, +170/-1182 lines:
- 8 exact duplicate test pairs deleted
- 28 always-passing/tautological tests removed (zero-assertion, property setter, inline-math-only)
- 12 tests not exercising production code deleted (hand-written logic, ConfigNode API tests, ParsekLog direct calls)
- 17 test classes given `IDisposable` for proper shared state cleanup
- 4 misleading test names fixed
- 3 unused `logLines` captures removed, dead code cleaned up, `[Collection("Sequential")]` added to WaypointSearchTests
- 3 `.NET framework behavior` tests deleted (HashSet operations in AnchorLifecycleTests)

### Test Coverage

3227 → 3374 tests (net +147: +212 new, -65 from T32 audit cleanup). New test areas:
- GroupTreeDataTests (14): recordings tree data-computation
- PostSpawnTerminalStateTests (12): spawn terminal state clearing
- InterpolatePointsTests (11): trajectory interpolation edge cases
- SerializationEdgeCaseTests (16): POINT/OrbitSegment round-trip, NaN, InvariantCulture
- ParsePartPositions + WalkbackAlongTrajectory (14): spawn collision parsing and walkback
- ReindexTests (7): ghost dict reindexing after deletion
- AppendCapturedDataTests (7): recording data append + sort
- GhostSoftCapManager Enabled=false guard (T22)
- SessionMerger frame trimming verification (T23)
- EnvironmentDetector ORBITING/ESCAPING situations (T24, 5 tests)
- ComputeStandaloneDelta (3): no-advance, multi-point, negative-index edge cases
- ResourceApplicator.TickStandalone (6): skip tree/loop/short, advance index, no-advance
- ResourceApplicator.DeductBudget (2): marking recordings and trees as applied
- CrewReservationManager serialization (4): LoadCrewReplacements log assertions, SaveCrewReplacements round-trip

### Documentation

- Refactor plan, inventory, review checklist, architecture analysis
- 21 deferred items tracked in `refactor-2-deferred.md` with Open/Done/Closed status
- Deferred items completed: D2, D5, D7, D8, D15, D18, D19, D20, D21
- TODO items completed: T5, T9, T10, T19-T27, T28, T30, T33, C1-C4
- `CLAUDE.md` updated with `ChainSegmentManager.cs` description
- Inventory doc (C4) updated with final line counts for all modified files

---

## 0.5.1

Spawn safety hardening, ghost visual improvements, booster/debris tree recording, flag planting, Fast Forward redesign, Real Spawn Control window. 20 PRs merged, 26 bugs fixed.

### Bug Fixes (Late)

- **Localization key mismatch on rewind** — stock vessels using `#autoLOC` keys (e.g., Aeris 4A stored as `#autoLOC_501176`) survived rewind vessel strip because name comparisons failed. Now resolves localization keys via `ResolveLocalizedName()` at all 4 strip/cleanup sites and all recording-creation sites (#126)
- **Collision check at wrong position** — `SpawnOrRecoverIfTooClose` checked the trajectory endpoint for collisions but `RespawnVessel` spawned at the snapshot position, allowing vessels to materialize on top of existing vessels. Now reads lat/lon/alt from the vessel snapshot for the collision check, with trajectory fallback. Also fixed in chain-tip spawn path (#127)

### Spawn Safety & Reliability

- **Bounding box collision detection** — replaced proximity-offset heuristic with oriented bounding box overlap checks against all loaded vessels (active vessel, debris, EVA, flags excluded)
- **Spawn collision retry limit** — 150-frame (~2.5s) collision block limit for non-chain spawns; walkback exhaustion flag for chain-tip spawns; spawn abandoned with WARN after limit hit (#110)
- **Spawn-die-respawn prevention** — 3-cycle death counter with permanent abandon for vessels destroyed immediately after spawn (e.g., FLYING at sea level killed by on-rails aero) (#110b)
- **Spawn abandon flag** — `SpawnAbandoned` prevents vessel-gone reset cycle from re-triggering spawn indefinitely
- **Non-leaf spawn suppression** — non-leaf tree recordings and FLYING/SUB_ORBITAL snapshot situations blocked from spawning; crew stripped from Destroyed-terminal-state spawn snapshots (#114)
- **SubOrbital terminal spawn suppression** — recordings with SubOrbital terminal state no longer attempt vessel spawn (#45)
- **Debris spawn suppression** — debris recordings (`IsDebris=true`) blocked from spawning real vessels
- **Orphaned vessel cleanup** — spawned vessels stripped from FLIGHTSTATE on revert and rewind; guards preserve already-set cleanup data on second rewind (#109)
- **ForceSpawnNewVessel on tree merge** — tree recordings correctly set ForceSpawnNewVessel during merge dialog callback, preventing PID dedup from skipping spawn after revert (#120)
- **ForceSpawnNewVessel on flight entry** — all same-PID committed recordings marked at flight entry for standalone recordings
- **Terminal state protection** — recovered/destroyed terminal state no longer corrupts committed recordings (#94)
- **Save stale data leak** — `initialLoadDone` reset on main menu transition prevents old recordings leaking into new saves with the same name (#98)

### Recording Improvements

- **Booster/debris tree recording** — `PromoteToTreeForBreakup` auto-promotes standalone recordings to trees on staging; creates root, continuation, and debris child recordings with 60s debris TTL. Continuation seeded with post-breakup points from root recording (#106 watch camera fix)
- **Controlled child recording** — `ProcessBreakupEvent` now creates child recordings for controlled children (vessels with probe cores surviving breakup), not just debris. Added to BackgroundRecorder with no TTL. Fixes RELATIVE anchor availability during playback (#61)
- **Flag planting recording/playback** — flag planting captured via `afterFlagPlanted`, stored as `FlagEvent` with position/rotation/flagUrl. Ghost flags built from stock flagPole prefab. Flags spawn as real vessels at playback end with world-space distance dedup
- **Auto-record from LANDED** — recording now triggers from LANDED state (not just PRELAUNCH) with 5-second settle timer to filter physics bounces, enabling save-loaded pad vessels and Mun takeoffs
- **Settle timer seed on vessel switch** — `lastLandedUT` seeded in `OnVesselSwitchComplete` for already-landed vessels, fixing auto-record for spawned vessels (#111)
- **Terminal engine/RCS events** — synthetic EngineShutdown, RCSStopped, and RoboticMotionStopped events emitted at recording stop for all active entries, preventing ghost plumes from persisting past recording end (#108)
- **Localization resolution** — `#autoLOC` keys resolved to human-readable names in vessel names and group headers via `Localizer.Format()` (#103)
- **Group name dedup** — multiple launches of same craft get unique group names: "Flea (2)", "Flea (3)" etc. (#104)
- **Chain boundary fix** — boundary splits skip standalone chain commits during tree mode, preventing nested groups in UI (#87)

### Ghost Visual Improvements

- **Compound part visuals** — fuel lines and struts render correctly on ghosts via PARTDATA/CModuleLinkedMesh fixup
- **Plume bubble fix** — ghost plume bubble artifacts eliminated by using KSP-native `KSPParticleEmitter.emit` via reflection instead of Unity emission module (#105)
- **Smoke trail fix** — Unity emission only disabled on FX objects that have KSPParticleEmitter; objects without it (smoke trails) keep their emission intact
- **Engine plume persistence** — `ModelMultiParticlePersistFX`/`ModelParticleFX` kept alive on ghosts for native KSP plume visuals (stripping them killed smoke trails)
- **Fairing cap** — `GenerateFairingConeMesh` generates flat disc cap when top XSECTION has non-zero radius (#85)
- **Fairing internal structure** — prefab Cap/Truss meshes permanently hidden; internal structure revealed only on `FairingJettisoned` event (#91)
- **Heat material fallback** — fallback path only clones materials that are tracked in `materialStates`, preventing red tint on non-heat parts (#86)
- **Surface ghost slide fix** — orbit segments skipped for LANDED/SPLASHED/PRELAUNCH vessels; `IsSurfaceAtUT` suppresses orbit interpolation for surface TrackSections; SMA < 90% body radius rejected (#93)
- **Terrain clamp** — ghost positions clamped above terrain in LateUpdate, preventing underground ghosts regardless of interpolation source
- **RELATIVE anchor fallback** — ghosts freeze at last known position instead of hiding when RELATIVE section anchor vessel is missing
- **Part events in Visual zone** — structural part events (fairing jettison, staging, destruction) now applied in the Visual zone (2.3-120km), not just Physics zone

### UI Improvements

- **Real Spawn Control window** — proximity-based UI showing ghosts within 500m whose recording ends in the future. Per-craft Warp button, sortable columns (Craft, Dist, Spawns at, In T-), and "Warp to Next Spawn" quick-jump button
- **Countdown column** — `T-Xd Xh Xm Xs` countdown in Recordings Manager, updates live during playback
- **Screen notification** when ghost craft enters spawn proximity range (10-second duration)
- **Toggle button** — "Real Spawn Control (N)" in main window, grayed out when no candidates nearby
- **Fast Forward redesign** — FF button performs instant UT jump forward (like time warp) instead of loading a quicksave; uses reflection for `BaseConverter.lastUpdateTime` to prevent burst resource production
- **Pinned bottom buttons** — Warp, Close, and action buttons pinned to window bottom in Actions, Recordings, and Spawn Control windows
- **Recordings window widened** — 1106 collapsed, 1324 expanded for better readability
- **Spawn abandon status** — spawn warnings show "walkback exhausted" / "spawn abandoned" status instead of silently retrying
- **Watch exit key** — changed from Backspace (conflicts with KSP Abort action group) to `[` or `]` bracket keys (#124)
- **Watch button guards** — disabled for out-of-range ghosts (tooltip: "Ghost is beyond visual range") and past recordings (#89, #90)
- **Group-level W and R/FF buttons** — recording group headers now show Watch and Rewind/FastForward buttons targeting the group's main recording (earliest non-debris descendant), no need to expand groups to access these controls (#115)
- **Watch overlay repositioned** — moved to left half of screen to avoid altimeter overlap

### Performance & Logging

- **CanRewind/CanFastForward log spam removed** — per-frame VERBOSE logs eliminated (was 578K lines/session, 94% of all output) (#117)
- **Main menu hook warning downgraded** — "Failed to register main menu hook" from WARN to VERBOSE (#118)
- **Spawn collision log demotion** — per-frame overlap log from Info to VerboseRateLimited (was ~24K lines/session)
- **GC allocation reduction** — per-frame allocations reduced in spawn UI via cached vessel names and eliminated redundant scans
- **Ghost FX audit** — systematic review of KSP-native component usage on ghosts; `KSPParticleEmitter` kept alive with `emit` control, `SmokeTrailControl` stripped (sets alpha to 0 on ghosts), `FXPrefab` stripped (pollutes FloatingOrigin), engine heat/RCS glow reimplementations retained (#113)
- **ParsekLog thread safety** — test overrides made thread-static to prevent cross-test pollution (#47)

### Bug Fixes

- **#45**: SubOrbital terminal state recordings no longer attempt vessel spawn
- **#47**: ParsekLog test overrides made thread-static
- **#85**: Fairing nosecone cap added to generated cone mesh
- **#86**: Heat material fallback only clones tracked materials, preventing red tint
- **#87**: Chain boundary commits no longer create nested groups in tree mode
- **#89**: Watch button disabled for ghosts beyond visual range
- **#90**: Watch button disabled for past (finished) recordings
- **#91**: Fairing internal structure hidden on ghost, revealed on jettison
- **#92**: Zone rendering tests updated for Visual-zone part events
- **#93**: Surface ghost slide fixed via orbit segment skip and SMA sanity check
- **#94**: Recovered/destroyed terminal state no longer corrupts committed recordings
- **#98**: Save data leak on same-name save recreation fixed via main menu reset
- **#103**: Localization keys resolved in vessel names and group headers
- **#104**: Multiple launches of same craft get unique group names
- **#105**: Ghost plume bubble artifacts fixed by using KSP-native emission
- **#106**: Watch camera booster fix via continuation point seeding
- **#108**: Synthetic shutdown events emitted at recording stop for all active engines/RCS
- **#109**: Spawned vessel cleanup preserved on second rewind
- **#110**: Spawn collision retry limited to 150 frames with abandon
- **#110b**: Spawn-die-respawn infinite loop stopped after 3 death cycles
- **#111**: Auto-record settle timer seeded on vessel switch
- **#114**: Non-leaf and FLYING/SUB_ORBITAL recordings blocked from spawning; crew stripped from destroyed-vessel snapshots
- **#119**: Watched ghosts exempt from zone distance hiding
- **#120**: Tree recordings set ForceSpawnNewVessel on merge
- **#61**: Controlled children now recorded after breakup (was "deferred to Phase 2")
- **#124**: Watch exit key changed from Backspace to brackets (Abort action group conflict)

---

## 0.5.0

Recording system redesign: multi-vessel sessions, ghost chain paradox prevention, spawn safety, time jump, and rendering zones. Recording format v5 → v7 (backward compatible).

### Recording System Redesign

- **Segment boundary rule** — only physical structural separation creates new segments. Controller changes, part destruction without splitting, and crew transfers are recorded as SegmentEvents within a continuing segment.
- **Crash coalescing** — rapid split events grouped into single BREAKUP BranchPoints via 0.5s window.
- **Environment taxonomy** — 5-state classification (Atmospheric, ExoPropulsive, ExoBallistic, SurfaceMobile, SurfaceStationary) with hysteresis (1s thrust, 3s surface speed, 0.5s surface/atmospheric bounce).
- **TrackSections** — typed trajectory chunks tagged with environment, reference frame, and data source.
- **Reference frames** — ABSOLUTE for physics, ORBITAL_CHECKPOINT for on-rails, RELATIVE for anchor-vessel proximity.

### Multi-Vessel Sessions

- **Background vessel recording** — all vessels in the physics bubble sampled at proximity-based rates (<200m: 5Hz, 200m-1km: 2Hz, 1-2.3km: 0.5Hz) with full part event capture.
- **Background vessel split detection** — creates tree BranchPoints + child recordings for all new vessels from separations. Debris children get 30s TTL.
- **Debris split detection** — `onPartDeCoupleNewVesselComplete` catches booster/debris vessels synchronously at decouple time. Debris trajectory recording planned for v0.5.1.
- **Highest-fidelity-wins merge** — overlapping Active/Background/Checkpoint TrackSections merged per vessel with snap-switch at boundaries.
- **Per-vessel merge dialog** — extended dialog shows per-vessel persist/ghost-only decisions.

### Relative Frames & Anchoring

- **Anchor detection** — nearest in-flight vessel with 2300m entry / 2500m exit hysteresis. Landed/splashed vessels excluded (not loaded during playback far from surface).
- **Relative recording** — offsets stored as dx/dy/dz from anchor vessel for pixel-perfect docking playback.
- **Relative playback** — ghost positioned at anchor's current world position + stored offset, FloatingOrigin-safe.
- **Loop phase tracking** — preserves phase across anchor vessel load/unload via pure arithmetic.

### Ghost Chain Paradox Prevention

- **Ghost chain model** — committed recordings that interact with pre-existing vessels (docking, undocking, etc.) cause those vessels to become ghosts from rewind until the chain tip resolves.
- **Chain walker algorithm** — scans all committed trees for vessel-claiming events, builds ordered chains, resolves cross-tree links.
- **Intermediate spawn suppression** — multi-link chains (bare-S → S+A → S+A+B) only spawn at the tip.
- **Ghost conversion** — real vessels despawned and replaced with ghost GameObjects during chain windows.
- **PID preservation** — chain-tip spawns preserve the original vessel's persistentId for cross-tree chain linking.
- **Ghosting trigger taxonomy** — structural events, orbital changes, and part state changes trigger ghosting; cosmetic events (lights) do not.

### Spawn Safety

- **Bounding box collision detection** — spawn blocked when overlapping with loaded vessels (active vessel, debris, EVA, flags excluded).
- **Ghost extension** — ghost continues on propagated orbit/surface past recording end while spawn is blocked.
- **Trajectory walkback** — for immovable blockers, walks backward along recorded trajectory to find a valid spawn position.
- **Terrain correction** — surface spawns adjusted for terrain height changes between recording and playback.

### Time Jump

- **Relative-state time jump** — discrete UT skip that advances the game clock while keeping the physics bubble frozen in place, preserving rendezvous geometry across ghost chain windows.
- **Epoch-shifted orbits** — orbital elements recomputed at the new UT from captured state vectors for Keplerian consistency.
- **TIME_JUMP SegmentEvent** — records the discontinuity for playback handling.

### Ghost World Presence

- **CommNet relay** — ghost vessels register as CommNet nodes with antenna specs from ModuleDataTransmitter, maintaining communication network coverage during ghost windows.
- **Ghost labels** — floating text labels showing vessel name, ghost status, and chain tip UT.
- **Map view / tracking station** — infrastructure stubs for ghost orbit lines and nav targets (full KSP integration pending).

### Rendering & Performance

- **Distance-based zones** — Physics (<2.3km, full fidelity), Visual (2.3-120km, mesh only), Beyond (120km+, no mesh).
- **Zone-aware playback** — per-ghost distance computation, zone transition detection, part events gated to Physics zone.
- **Ghost soft caps** — configurable thresholds with priority-based despawning (LoopedOldest first, FullTimeline kept longest). Disabled by default until profiled.
- **Settings UI** — three slider controls for cap thresholds with enable toggle and live apply.
- **Log spam mitigation** — rate-limited high-volume diagnostics (SoftCap, zone, heat, engine FX).

### Bug Fixes (70 tracked, 48 fixed)

- **#51**: Chain ID lost on vessel-switch auto-stop — proper segment commit and chain termination
- **#52**: CanRewind log spam (485K lines) — verbose removed from success path
- **#53**: Re-show log spam (16K lines) — deduplicated via loggedReshow HashSet
- **#54**: Watch mode beyond terrain range — 2s grace period then auto-exit
- **#55**: RELATIVE anchor on debris — vessel type filtering + surface skip
- **#9**: Zero-frame TrackSections from brief RELATIVE flickers — discarded
- Active TrackSections not flushed to tree recordings — FlushRecorderToTreeRecording, CreateSplitBranch, CreateMergeBranch now copy TrackSections
- Watch mode camera re-targeting — deferred spawn no longer switches camera to spawned vessel after watch mode ends at recording boundary
- Rewind save propagation fixed across tree/EVA/split paths
- Soft cap spawn-despawn loop — suppression set prevents re-spawn after cap despawn
- Zone hide vs warp re-show loop — check currentZone before re-showing
- False RELATIVE anchor at launchpad — skip anchor detection on surface
- Watch mode on beyond-range looped ghost — loop phase offset reset
- Background split children capture vessel snapshots for ghost playback
- See `docs/dev/todo-and-known-bugs.md` for full list

### Format Changes

- Recording format v5 → v7 (additive, backward compatible)
- v6: SegmentEvents, TrackSections, ControllerInfo, extended BranchPoint types (Launch, Breakup, Terminal)
- v7: TerrainHeightAtEnd for surface spawn terrain correction
- Old recordings (v5) play back unchanged using legacy flat Points path

### Test Coverage

2994 tests (up from 1748 in v0.4.3).

---

## 0.4.3

Code refactor + UI improvements.

### Code Refactor

- ~73 methods extracted across 38 source files
- 6 new focused files: GhostTypes.cs, Recording.cs, GhostPlaybackState.cs, GhostPlaybackLogic.cs, PartStateSeeder.cs, GhostBuildResult
- ParsekFlight.cs reduced from 8,225 to ~7,000 lines
- ParsekKSC/ParsekFlight coupling eliminated

### New Features

- Hide column replaces Delete in Recordings Manager
- Group disband replaces group delete
- Context-aware rewind button (R for past, FF for future)
- Sandbox rewind fix

### Test Coverage

1748 tests (up from 1394 in v0.4.2).

---

## 0.4.2

### New Features

- Auto-loop with per-recording unit selector (sec/min/hr/auto)
- Recording groups with multi-membership and nesting
- KSC scene ghost playback with overlap support
- Time warp visual cutoffs and deferred spawn queue

### Visual Improvements

- 3-state heat model (cold/medium/hot)
- Heat shield ablation, smoke puff + spark FX on decouple/destroy
- FXModuleAnimateRCS, ModuleColorChanger, fairing ghost visuals
- EVA kerbal facing fix

### Bug Fixes

- #28-#44: 17 fixes including damaged wheel filtering, RCS debounce, FX activation, rate-limited log spam

---

## 0.4.1

### Bug Fixes

- #26: EVA crew swap after merging from KSC
- #40: Save contamination between saves
- #41: Watch camera stuck after loop explosion with time warp

### New Features

- Explosion visual effect on impact with camera hold
- Overlapping ghost support for negative loop intervals
- Loop interval editing per-recording
- KSC toolbar UI
- Short recording auto-discard (<10s AND <30m)

---

## 0.4.0

### New Features

- Orbital rotation fidelity — ghosts hold recorded SAS orientation during orbital playback
- PersistentRotation mod support — spinning vessels reproduced during ghost playback
- Camera recenters on ghost after separation events in Watch mode

### Bug Fixes

- #17: Re-entry FX too large — replaced with mesh-surface fire particles matching stock aeroFX
- #18: Engine nozzle glow persists after shutdown
- #19: Watch button broken for looped segments
- #21-#24: Ghost build spam, facility warnings, dead geometry stubs, variant warnings

### Test Coverage

1263 tests.

---

## 0.3.1

### Bug Fixes

- Auto-migrate v4 world-space rotation to v5 surface-relative
- Remove incorrect planetary spin correction
- Fix rewind save lost on atmosphere boundary false alarm
- Fix chain orphan on vessel destruction
- Fix EVA ghost showing capsule mesh

### Improvements

- Collapsible recording groups
- Recording format v5 (surface-relative rotation)

---

## 0.3.0

Initial public release.

- Position recording with adaptive sampling
- Ghost playback with opaque vessel replicas
- Rewind to any earlier timeline point
- Multi-vessel recording (undock, EVA, dock)
- Career mode: milestones, resource budgeting, action blocking
- 28 part event types replayed on ghosts
- Orbital recording with analytical Keplerian orbits
- Recordings Manager UI

**Dependencies:** Module Manager, HarmonyKSP, ClickThroughBlocker, ToolbarControl
