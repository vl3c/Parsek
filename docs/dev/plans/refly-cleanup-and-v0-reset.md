# Re-Fly Phase D wrap-up + recording format v0 reset

Date: 2026-05-05
Status: plan v5.6, Branch A delivered; Branch B/C not started.
Supersedes (in scope): the deferred [refly-postmerge-relative-to-absolute.md](refly-postmerge-relative-to-absolute.md) — recording-id chain replaces the post-merge promotion motivation.
Related: [ghost-anchor-recording-chain-plan.md](ghost-anchor-recording-chain-plan.md), [pr708-playtest-followup-plan.md](pr708-playtest-followup-plan.md).

This plan wraps up the Re-Fly Phase D enumeration on top of PR #708 / PR #751, then takes the opportunity (no users, still in private development) to drop every legacy-recording / legacy-format reader path and reset the recording format to v0.

## Review history folded into this draft

- v1 review by clean-context Opus internal pass (P0 D.0 thinness; P0 missing serialization surfaces; P1 deletion-target gaps; P1 missing rollback / `.sfs` audit / CHANGELOG public-history note; smaller nits).
- v1 review by external reviewer (P1 version discriminator: pre-reset `RecordingFormatVersion = 0` already exists as default fallback; P1 unsupported sidecars still enter committed storage; P2 write/read gates currently route on `>= 2` and `>= 1` and need explicit flip; P3 docs checklist contradicts repo rules and omits AGENTS.md).
- v2 review by external reviewer (P1 strict generation comparison instead of `>=`; P1 schema refusal also at `TryRestoreActiveTreeNode`; P2 text sidecar magic / load-path resolution; P2 wider version-literal grep across the test suite; stale-note: #751 status).
- v3 implementation-evidence pass (2026-05-05): three parallel investigators verified every named symbol and line number in D.1, D.1.5, D.4, D.5, D.6, D.7, §3.1, §3.2, §3.3, §3.10. **Three plan claims corrected:** (a) `RecordingTreeRecordCodec` does not have a `delta*` legacy seam; (b) D.4 helper names `TryResolveRelativeAnchorPose` and `PositionGhostRelativeAt` do not exist as standalone (already refactored); (c) `RecordingStore.PendingActiveTreeResume` does not exist (active-tree handoff uses `PendingTree` + `PendingTreeState` enum).
- v3 review by external reviewer + clean Opus pass (2026-05-05 late evening): six fixes folded into v4. **Two factual corrections (still valid in v5):** (d) `CurrentBinaryVersion = 11` (not 10 as v3 claimed); reader accepts v2-v11 inclusive. (e) PR #751 IS merged at `f530c9ad`; v3 banner spoke in future tense. **Three substantive gaps closed (still valid in v5):** (g) §3.3 enumerated nine missed gate sites. (h) D.2 narrowed from "delete `RelativeAnchorResolution`" to specific named methods/branches. (i) §3.2 schema-compat predicate clarified.
- v4 over-correction on `InPlaceContinuation` (2026-05-05 latest): v4 also claimed `ReFlySessionMarker.InPlaceContinuation` bool field "does not exist on merged main" and rewrote §3.7 / §3.8 around id-equality detection. **That claim was wrong and v5 reverts it.** The field DOES exist at [ReFlySessionMarker.cs:83](Parsek/Source/Parsek/ReFlySessionMarker.cs:83); `SaveInto` writes `inPlaceContinuation` at `:135-136`; `LoadFrom` reads it at `:198-199`; the static helper `IsInPlaceContinuation(marker)` at `:110-116` requires the flag to be true. All in-place detection sites (MarkerValidator, ParsekFlight, RenderSessionState) route through that helper. v4's verification grep was running against the plan worktree's pre-#751 checkout (`24d83205`); the worktree was only merged with `f530c9ad` in v5's commit `e78a4755`. v3's original §3.7 / §3.8 narrative (legacy items dropped, marker schema gains the flag) is restored in v5.
- v5 fixes from external review pass after merge (P1: restore `InPlaceContinuation` to schema; P2: remove stale `RelativeAnchorResolution.cs` legacy v5 row from §3.6 deletion table — that legacy path lives in the trajectory codec, not the resolver).
- v5.1 cleanup: §4 rebase note + rollback paragraph still carried v4's id-equality and `RelativeAnchorResolution legacy v5` references; both updated to match v5's flag-based marker contract and the actual codec-side legacy-reader surfaces.
- v5.2 fixes from external review v5.1 — five items: (1) ahead/behind count was 16; actual was 6/27 after PR #721 merged; (2) D.4 missed `GhostPlaybackEngine.DescribeAppearanceLiveAnchorContext` + `IGhostPositioner.TryGetLiveAnchorWorldPosition(uint anchorVesselId, ...)` at [GhostPlaybackEngine.cs:5169](Parsek/Source/Parsek/GhostPlaybackEngine.cs:5169) and [IGhostPositioner.cs:99](Parsek/Source/Parsek/IGhostPositioner.cs:99) — both added as explicit deletion targets; (3) source-wide `anchorVesselId` removal gate (not just named files) — `GhostRenderTrace.cs:643` flagged as missed production surface, gate widened to `Source/Parsek/**`; (4) §3.2 schema-refusal extended with explicit empty-tree / broken-reference handling (drop empty tree, drop tree on rejected `RootRecordingId`, clear `ActiveRecordingId`, drop `BranchPoint`/`SupersedeRelation` rows that reference rejected recordings); active-tree refusal now also clears `ParsekScenario.pendingActiveTreeResumeRewindSave` ([:4674](Parsek/Source/Parsek/ParsekScenario.cs:4674) declaration, assigned at `:3145`); (5) stale doc nits — status said "plan v2" (now v5.2); D.5 said `Source/Parsek.Tests/RuntimeTests.cs` (correct path is `Source/Parsek/InGameTests/RuntimeTests.cs`).
- v5.3 fixes from implementation-start review — plan branch merged current `origin/main` at `551746e1`, so the v5.2 6-ahead/27-behind note became stale; D.4's grep gate was split so Branch A bans live-PID use only in non-loop Relative playback/render/map/KSC helper bodies, while Branch B owns the eventual source-wide `section.anchorVesselId` removal after the field is deleted. Active-tree schema-refusal now also calls out `ClearPendingQuickloadResumeContext()`, and BranchPoint cleanup uses the actual plural `ParentRecordingIds` / `ChildRecordingIds` fields.
- v5.4 fixes from Branch A approval review — D.0 confirmation boxes are signed explicitly, including Watch **Option A**; Branch A's non-loop live-PID guard no longer claims to run v0 watch/Re-Fly fixtures before Branch B creates them; the Branch A base note no longer hardcodes an ahead-count that goes stale after fetch/merge churn.
- v5.5 follow-up — removed the remaining D.4/final-acceptance wording that still required v0 watch/Re-Fly scenario coverage before Branch B. Branch A owns the audit script plus guard reset/count semantics; Branch B owns regenerated v0 scenario playback coverage.
- v5.6 post-Branch-A review — recorded the delivered Branch A commit mapping, corrected the implemented `NonLoopLivePidGuard` API/test surface, replaced stale D.1.5 uncertainty recipes with the actual triage result, and documented the deliberate camera pre-cull reapply deviation. Branch B/C reset content remains future work.

---

## Branch status after PR #755

- **Branch A (`refly-phase-d`) delivered:** D.1-D.7 landed in PR #755 across ten commits on top of `origin/main`: `bfca3c30`, `397b8af1`, `00a04236`, `daf997ba`, `9c2d78bc`, `a0e1e87b`, `ca2c4d19`, `d1f253b2`, `f9c49a62`, and `65f42b46`. The commit cadence was coarser than the original "one commit per phase" preference: D.1.5 was absorbed into D.1, and D.4/D.5/D.7 landed together with follow-up review/documentation commits.
- **Branch A validation:** clean build, broad non-injection xUnit (`10699/10699`), `scripts/grep-audit-non-loop-live-pid.ps1`, `scripts/grep-audit-ers-els.ps1`, and a final GPT-5.5 xhigh clean review. Full `InjectAllRecordings` and in-game smoke remain manual/runtime checks because the local KSP instance held `KSP.log`.
- **Branch B not started:** recording format v0 reset still owns `TrackSection.anchorVesselId` field deletion, `RecordingSchemaGeneration`, legacy-reader refusal, fixture regeneration, and v0 scenario playback coverage.
- **Branch C not started:** `absoluteFrames` shadow removal stays gated on Branch B plus one playtest cycle.

## 1. D.0 product-behaviour confirmation

The D.0 gate from `ghost-anchor-recording-chain-plan.md` section 5 is "a required milestone, not a comment." This section records the three product decisions that were required before any Phase D deletion could land. v1 collapsed them under one user quote; v2 splits them.

User playtest sign-off context: "I've been testing the feature extensively and any of the things that I wanted to be changed have already been addressed." (2026-05-05.)

PR #708 + stabilization tail's *current* behaviour is not yet the post-Phase-D behaviour: the frozen body-fixed display alignment helper and the live-anchor display path still exist. Each bullet below distinguishes what the current state is, what Phase D will change it to, and asks for an explicit confirmation.

### D.0.1 — Ghosts render only from recorded data during active Re-Fly

- **Current (PR #708):** Active-Re-Fly ghosts render from recorded data through the chain resolver, plus a **frozen body-fixed display alignment** captured once per session/recording. Live-vessel motion does not drive ghost position after capture.
- **Post-Phase-D:** Frozen display alignment is removed. Ghosts render at their original recorded coordinates with no live-vessel-derived offset of any kind. `TryGetReFlyTreeAnchorOffset` and `reFlyTreeOffset` are gone.
- **Confirmation:** [x] User confirms the post-Phase-D behaviour (no display alignment, divergent re-fly visibly separates the player's live vessel from the ghost reference frame).

### D.0.2 — Watch camera policy during active Re-Fly

The upstream plan offered two options: (A) keep Watch anchored to the live Re-Fly target during active Re-Fly, or (B) allow Watch to follow recorded ghosts away from the player.

- **Current (PR #708):** Watch follows the live Re-Fly target; CameraPreCull reapply is restricted to active-Re-Fly entries; pure Watch ghosts use the historical linear bracket path.
- **Post-Phase-D options:**
  - **Option A** — Watch stays on the live Re-Fly target during active Re-Fly. Same as current PR #708 behaviour. Lowest player-visible change.
  - **Option B** — Watch can follow recorded ghosts away from the live vessel. Aligns with the "no live vessel in ghost math" principle but is a UX shift.
- **Confirmation:** [x] User selected **Option A** — Watch stays on the live Re-Fly target during active Re-Fly.

### D.0.3 — Distance / soft-cap / zone behaviour

- **Current (PR #708):** Distance / soft-cap / zone decisions consult ghost positions that include the frozen Re-Fly display offset.
- **Post-Phase-D:** Decisions consult recorded-coordinate ghost positions only. A divergent re-fly that flies the live vessel away from its tree may legitimately soft-cap or despawn ghosts that were near the player under the old offset.
- **Confirmation:** [x] User confirms recorded-coordinate distance behaviour. A divergent re-fly may visibly lose its ghosts. This is the explicit user-stated intent ("render the ghosts relative to the initial recording trajectory and not to the real vessel"); v2 calls it out so it can be re-acknowledged with full UX consequences in mind.

D.0 is satisfied for Branch A: all three confirmations are signed, including Watch **Option A**. If any D.0 choice changes, pause D.1-D.7 implementation and re-approve the affected behaviour before continuing.

---

## 2. Phase D enumeration (deletion arc)

Branch A has landed. The phase notes below are retained as the implementation rationale and delivered/verified targets; Branch B/C sections remain future work.

### D.1 — Per-tree Re-Fly anchor lock

`ParsekFlight.cs`. Delete:

- `TryGetReFlyTreeAnchorOffset`, `TryGetReFlyTreeAnchorOffsetUncached`, the memo struct.
- `GhostPosEntry.reFlyTreeOffset` field and every write of it.
- All LateUpdate `e.reFlyTreeOffset` reads.
- Every executable shift site: positioner, single-point hold, orbit-driven, surface, distance resolver, checkpoint, loop-relative (loop calls move behind the explicitly named loop helper from D.4).

Replacement: chain resolver call when section is non-loop Relative; nothing for Absolute / OrbitalCheckpoint / Surface (those are at recorded coords by design).

CI gate: zero hits for `TryGetReFlyTreeAnchorOffset`, `TryGetReFlyTreeAnchorOffsetUncached`, `reFlyTreeOffset` in `Source/Parsek/ParsekFlight.cs`.

**Verified site inventory (2026-05-05 investigation pass; pre-#751 main):**

| Identifier | Hit count in `ParsekFlight.cs` | Notable sites |
|---|---|---|
| `TryGetReFlyTreeAnchorOffset` | 27 | Method definition `:20194` (UT-based, wraps uncached) |
| `TryGetReFlyTreeAnchorOffsetUncached` | 8 | Method definition `:20270` (private, `allowCapture` param) |
| `reFlyTreeOffset` | 58 | Field declaration `:474` (`Vector3d`); LateUpdate reads `:1326` positioner, `:1339` single-point hold, `:1364` orbit-driven, `:1458` checkpoint, `:1531` surface, `:1597` distance resolver, `:1641` loop-relative, `:1704` CheckpointLoop |
| `GhostPosEntry` | 13 | Struct definition `:383`; instantiations spread across positioner paths |

**Replacement strategy by site:** Relative shift sites (e.g. `:17267-17269`) call `RelativeAnchorResolver` for the new anchor pose; OrbitalCheckpoint, Surface, and Loop sites delete the offset addition entirely (those frames are at recorded coordinates by design). The deletion is mechanical surgery across ~40 distinct edits, not a single-point cutover — same shape `ghost-anchor-recording-chain-plan.md` §10 warns about.

Expect line drift between this snapshot and Branch A's actual edit pass; re-grep at implementation time and confirm the post-D.1 zero-hit gate against the freshly-discovered set, not this table.

#### D.1.5 — PR #708 stabilization machinery triage

The frozen display alignment came with a layered set of stabilization hooks that were introduced *because of* the per-frame live offset (see [pr708-playtest-followup-plan.md:204-218](pr708-playtest-followup-plan.md)). Once D.1 deletes the alignment, each hook is one of: (a) dead code to remove, (b) still-load-bearing for normal Watch and renamed accordingly, (c) potentially harmful and must be deleted alongside D.1. Triage every hook as part of the D.1 commit (or as an immediately-following D.1.5 commit) so we don't ship Branch A with dead state.

**Delivered D.1.5 triage (Branch A):** the old uncertainty rows are resolved by implementation. Greps in `refly-phase-d` return zero hits for `refly-display-offset-active`, `refly-display-offset-linearized`, `reFlyTrendHit`, `ReFlyPointTrendMaxCorrectionMeters`, `reFlyGhostPartPinHit`, `live-root-relative`, and `ReFlyRenderInterpolationLiveRootFrameMaxDistanceMeters`.

| Hook | Branch A result |
|---|---|
| Co-bubble suppression / anchor-correction epsilon suppression | No display-offset-specific gate survived; the historical `refly-display-offset-active` strings are gone. |
| Hermite / spline Re-Fly-specific reason strings | Re-Fly display-offset reasons are gone; retained interpolation paths are normal playback machinery. |
| Render-interp duplicate-UT smoother | No `RenderInterp` / display-offset-specific smoother exists in Branch A. |
| Recorded-path trend gate | `reFlyTrendHit` and `ReFlyPointTrendMaxCorrectionMeters` are gone. |
| Selected-root-part pin | `reFlyGhostPartPinHit` and `ReFlyDisplayAlignment` are gone. |
| Live-root-relative interpolation frame | `live-root-relative` and `ReFlyRenderInterpolationLiveRootFrameMaxDistanceMeters` are gone. |
| Camera pre-cull reapply | **Deliberate deviation from the original recommendation:** the generic `TraceGhostPositionReapply` / `LogSkippedNonReFlyCameraPreCull` scaffolding stays as camera-pre-cull observability and transform synchronization. The Re-Fly display-alignment machinery it used to support is gone, so this is no longer a live-offset path. |

Branch A guard implementation:

- **Location/API:** `Source/Parsek/Rendering/NonLoopLivePidGuard.cs` contains private static `lookupAttempts`, `[Conditional("DEBUG")] NonLoopRelativeLivePidLookupAttempted(string context)`, `LivePidLookupAttemptsForTesting`, and `ResetForTesting()`.
- **Increment site:** Branch A deletes the non-loop live-PID lookup paths outright. The sole remaining guard call is the defensive `LateUpdate.Relative` fence that records an attempt if a non-loop entry somehow still carries a non-zero `anchorVesselId`.
- **Branch A assertion:** `Source/Parsek.Tests/GrepAuditNonLoopLivePidTests.cs` combines the grep-audit wrapper with reset/count coverage for the guard.
- **Branch B scenario assertion:** once Branch B regenerates v0 fixtures, add scenario-level watch/Re-Fly playback coverage that asserts `NonLoopLivePidGuard.LivePidLookupAttemptsForTesting == 0` after playback completes.
- **CI integration:** Branch A runs through the standard `dotnet test` acceptance gate plus `scripts/grep-audit-non-loop-live-pid.ps1`; Branch B adds the v0 scenario-level fixture gate.

In addition, D.4's structural CI grep (P1 from v3 review pass) preserves the upstream plan's named-helper allowlist contract:

- **Non-loop helper allowlist** (must NOT contain `anchorVesselId` or `FindVesselByPid` after D.4): `GhostPlaybackEngine.TryGetRelativeSectionAtUT`, `GhostPlaybackEngine.TryPositionRelativeSectionAtPlaybackUT`, `IGhostPositioner.InterpolateAndPositionRelative`, `ParsekFlight.InterpolateAndPositionRecordedRelative` (already at `:16714`), `ParsekFlight.TryResolveRecordedPlaybackWorldPosition` (split target from `TryResolvePlaybackWorldPosition`), `ParsekFlight.TryResolveRecordedRelativeAnchorPose` (split target if a separate helper emerges), `ParsekFlight.PositionGhostRecordedRelativeAt` (split target), and the recorded branch of `LateUpdate` Relative re-position.
- **Loop-only helper allowlist** (may contain live-PID, by design): `ParsekFlight.PositionLoopGhost`, `ParsekFlight.TryResolveLoopLiveAnchorPose`, `ProductionAnchorWorldFrameResolver.TryResolveLoopAnchorWorldPos`, `ProductionAnchorWorldFrameResolver.TryFindVesselByPid` (the helper survives only as a loop dependency).
- **Branch A grep gate:** `scripts/grep-audit-non-loop-live-pid.ps1` (new; sibling to existing `grep-audit-ers-els.ps1`) runs in CI via a new test class `Source/Parsek.Tests/GrepAuditNonLoopLivePidTests.cs`. This gate is **call-path scoped, not source-wide**: it parses/greps the known non-loop Relative playback/render/map/KSC helper bodies and fails if any non-allowlisted helper body contains `section.anchorVesselId`, `target.Section.anchorVesselId`, `AnchorPid`-driven live lookup, `TryGetLiveAnchorWorldPosition`, or `FindVesselByPid`. The named-file lists in D.4/D.6/D.7 are the expected surface; the scoped gate catches anything the implementer missed (e.g. `GhostRenderTrace.cs:643`, the `DescribeAppearanceLiveAnchorContext` chain in `GhostPlaybackEngine.cs:5091-5219`, and `GhostMapPresence` sites beyond what D.7 enumerated) without banning unrelated PID lookups in spawn confirmation, recording, finalization, observability, or loop-only code. This complements (not replaces) the runtime counter.
- **Branch B source-wide anchor-field gate:** after Branch B deletes `TrackSection.anchorVesselId`, a separate grep gate must return zero production hits for `section.anchorVesselId` / `target.Section.anchorVesselId` under `Source/Parsek/**` except negative tests and explicit text/binary legacy-refusal fixtures. Branch B, not D.4, owns recorder write-side reads in `BackgroundRecorder.cs` / `FlightRecorder.cs`, codec reads in `TrajectorySidecarBinary.cs` / `TrajectoryTextSidecarCodec.cs`, and other field-removal fallout.

### D.2 — no-live-anchor / stale-anchor branches

**Narrow deletion targets, not the whole `RelativeAnchorResolution` class** (P2 from review v3): the class has load-bearing helpers that protect retired Relative ghosts and decide post-position pipeline skips. `RelativeAnchorResolution` stays. Specific deletion targets:

- `ParsekFlight.TryUseAbsoluteShadowForActiveReFlyRelativeSection` — the `no-live-anchor` and `stale-anchor` fallback **branches** inside it. The method itself may stay if its other branches survive Phase C; verify at implementation time.
- `RelativeAnchorResolution.ShouldBypassLiveAnchorForActiveReFly` (currently at [:101](Parsek/Source/Parsek/RelativeAnchorResolution.cs:101)) — entire method is the bypass-on-active-Re-Fly branch that becomes unreachable once Phase C eliminates live anchoring for non-loop sections. Delete.
- `RelativeAnchorResolution.IsStaleLiveAnchor` (currently at [:186](Parsek/Source/Parsek/RelativeAnchorResolution.cs:186)) — staleness detector for the live-anchor path; non-loop playback has no live anchor to be stale. Delete.
- `RelativeAnchorResolution.SelectAnchorFrameSource` (currently at [:132](Parsek/Source/Parsek/RelativeAnchorResolution.cs:132)) — investigate at implementation: if all branches are now resolved by `RelativeAnchorResolver`, delete; if loop playback still consumes it, narrow to loop-only.

**Stays in `RelativeAnchorResolution`:**

- `Decide(uint anchorPid, ...)` ([:63](Parsek/Source/Parsek/RelativeAnchorResolution.cs:63)) — uses `Func<uint, bool>` resolver indirection; loop-only callers retain it.
- `DedupeKey` ([:205](Parsek/Source/Parsek/RelativeAnchorResolution.cs:205)) — pure utility; stays.
- `FormatRetiredMessage` ([:222](Parsek/Source/Parsek/RelativeAnchorResolution.cs:222)) — diagnostic formatter; stays.
- `ShouldSkipPostPositionPipeline(bool anchorRetiredThisFrame)` ([:258](Parsek/Source/Parsek/RelativeAnchorResolution.cs:258)) — protects retired Relative ghosts from being reactivated; load-bearing. Stays.

CI gate after D.2: `RelativeAnchorResolution.cs` should have ~5 fewer methods (the deleted ones + any helpers exclusive to them). The grep gate is per-method-name, not per-file.

### D.3 — Forward-bridge fallback

`TryFindAbsoluteShadowForwardBridgeFrame` and callers. Sparse Relative sections resolve via the chain at the section's own UT range.

### D.4 — Drop the live-PID contract from non-loop Relative playback

**Phase C status (2026-05-05 investigation):** the engine/positioner contract is **already** target-shaped. Phase C landed:

- `GhostPlaybackEngine.TryGetRelativeSectionAtUT` at [GhostPlaybackEngine.cs:2355](Parsek/Source/Parsek/GhostPlaybackEngine.cs:2355) — emits `out RelativeSectionPlaybackTarget`, no `uint anchorVesselId` in the signature.
- `GhostPlaybackEngine.TryPositionRelativeSectionAtPlaybackUT` at [:2397](Parsek/Source/Parsek/GhostPlaybackEngine.cs:2397) — carries the target through.
- `IGhostPositioner.InterpolateAndPositionRelative` at [IGhostPositioner.cs:60](Parsek/Source/Parsek/IGhostPositioner.cs:60) — takes `RelativeSectionPlaybackTarget`.
- `RelativeSectionPlaybackTarget` struct at [IGhostPositioner.cs:5](Parsek/Source/Parsek/IGhostPositioner.cs:5) — fields `RecordingId`, `SectionIndex`, `Section`, `AnchorRecordingId`.
- `RelativeAnchorResolver.TryResolveAnchorPose` at [RelativeAnchorResolver.cs:80](Parsek/Source/Parsek/RelativeAnchorResolver.cs:80) — already production code.

D.4 is therefore not "introduce the contract" but **"finish the cutover by deleting the remaining live-PID surfaces inside ParsekFlight"** plus removing `anchorVesselId` from the format (§3.6). The split-helper plan from earlier drafts was partly stale: `TryResolveRelativeAnchorPose` and `PositionGhostRelativeAt` do not exist as standalone helpers in the current code (they were absorbed into the Phase C refactor). The helpers that do exist:

| Helper | File:line | Loop branch | Non-loop branch | D.4 disposition |
|---|---|---|---|---|
| `InterpolateAndPositionRelative` | `ParsekFlight.cs:16672` | none | yes (already non-loop only) | Stays; signature is the v11 target shape |
| `InterpolateAndPositionRecordedRelative` | `ParsekFlight.cs:16714` | none | yes | Stays; already the recorded-relative-only path |
| `TryResolvePlaybackWorldPosition` | `ParsekFlight.cs:~20761` | yes (checks `LoopAnchorVesselId`) | yes (checks `activeReFlyPid`) | Split into recorded vs loop variants per D.4 |
| `LateUpdate` Relative re-position | `ParsekFlight.cs:~24052+` | yes (`LoopAnchorVesselId != 0`) | yes (`activeReFlyPid != 0`) | Recorded branch stores resolved pose; loop branch keeps live-anchor lookup |

**Verified `FindVesselByPid` / live-anchor-PID deletion targets** (only the non-loop Relative-playback sites; spawn / bookkeeping / loop sites stay):

| File:line | Context | Disposition |
|---|---|---|
| `ParsekFlight.cs:1570` | `TryResolveRecordedRelativeAnchorPose` block reading `section.anchorVesselId` | Delete the live-PID branch; resolver-only |
| `ParsekFlight.cs:16533` (and surrounding `:16540, 16546, 16549, 16559`) | RELATIVE-frame anchor lookup with live-PID fallback + the formatted log strings | Delete the live-PID fallback and its log scaffolding |
| `IGhostPositioner.cs:99` | `bool TryGetLiveAnchorWorldPosition(uint anchorVesselId, out Vector3d worldPosition)` interface method | **Delete from interface.** This is the explicit live-anchor surface that D.4's grep gate would otherwise trip on. Implementations in `ParsekFlight` removed alongside. |
| `GhostPlaybackEngine.cs:5169` | `internal static string DescribeAppearanceLiveAnchorContext(... section, ...)` — called from appearance trace at `:5091`, dispatches to `TryGetLiveAnchorWorldPosition` at `:5189`, formats `legacyAnchorPid` strings at `:5140, :5191, :5219` | **Delete the helper entirely.** It is a diagnostic-only formatter; once the resolver is the only path, the appearance log line for non-loop Relative reduces to `anchorRec=<id>`. The legacy-pid format strings have no replacement. |
| `GhostRenderTrace.cs:643` | `context.AnchorVesselId = section.anchorVesselId;` in render-trace context build | **Delete the assignment** (and the `AnchorVesselId` field on the trace context if it has no other consumers — verify at implementation). Trace consumers switch to `section.anchorRecordingId`. |

15+ other `FindVesselByPid` calls in `ParsekFlight.cs` are legitimate (spawn confirmation, vessel-decay classification, debris snapshot, scene-exit finalization, loop-only playback, marker arrival detection) — they stay.

**Acceptance bullet to preserve from PR #708 stabilization:** same-chain successor continuation for Relative anchor resolution (per `pr708-playtest-followup-plan.md` section 4.1, already implemented and live). The helper split must not regress the "first chronological covering segment wins" behaviour.

After D.4, `FindVesselByPid` calls survive only inside named loop helpers (`LoopAnchorVesselId`-rooted) and out-of-scope subsystems. Branch A's CI gate is the scoped `scripts/grep-audit-non-loop-live-pid.ps1` test plus assertions that `NonLoopLivePidGuard` resets/counts correctly. Branch B adds the v0 watch/Re-Fly scenario playback suite and pins `NonLoopLivePidGuard.LivePidLookupAttemptsForTesting == 0` there after those fixtures exist.

### D.5 — Spawn-frame defer

Delete `RefreshReFlyAnchorActivationGate`, `externalActivationDeferred`, the spawn-frame-defer plumbing in `GhostPlaybackEngine.ActivateGhostVisualsIfNeeded`. Ghost activation is immediate once the resolver returns (chain is in-memory recorded data).

**Verified site inventory (2026-05-05 investigation pass):**

| Symbol | File:line | Notes |
|---|---|---|
| `RefreshReFlyAnchorActivationGate` definition | `ParsekFlight.cs:21847` | Internal method; takes `(recordingId, state, currentUT)`; sets/clears `externalActivationDeferred` |
| `RefreshReFlyAnchorActivationGate` callers | `ParsekFlight.cs:16590, 16653, 16669, 16720, 16772, 17071, 17114, 17142` | 8 sites, one per positioning path (single-point, orbit, surface, checkpoint, loop, relative, distance, etc.) |
| `externalActivationDeferred` field | `GhostPlaybackState.cs:66` | `public bool` |
| `externalActivationDeferred` reads | `GhostPlaybackState.cs:140` (reset), `GhostPlaybackEngine.cs:4457`, `:4537`, `ParsekFlight.cs:16466` | 4 read sites |
| `externalActivationDeferred` writes | `ParsekFlight.cs:21913` (`LowerExternalActivationGate`), `:21937` (`RaiseExternalActivationGate`) | 2 write sites |
| `ActivateGhostVisualsIfNeeded` | `GhostPlaybackEngine.cs:4445` | Private static; checks `state.externalActivationDeferred` at `:4457` and bails when deferred |
| `ActivateGhostVisualsIfNeeded` callers | `GhostPlaybackEngine.cs:1189, 1709, 1921, 2093, 2182, 4274` | 6 sites in the engine frame loop |

**Test cleanup:** delete or rewrite tests that exercise the defer. Confirmed references at:
- `Source/Parsek/InGameTests/RuntimeTests.cs:12455, 12663, 12676, 12702, 12704` — direct gate-and-defer exercises (deletion candidates). Note: file is in `Source/Parsek/InGameTests/`, not `Source/Parsek.Tests/`; line numbers will drift after rebase against PR #721's +1070-line addition.
- `Source/Parsek.Tests/RelativeAnchorResolutionTests.cs:1274, 1287` — comments referencing `ActivateGhostVisualsIfNeeded` (likely just doc-comments; verify).
- `Source/Parsek.Tests/ReFlyTreeAnchorLockTests.cs:440` — comment referencing the gate (deletion candidate alongside the rest of `ReFlyTreeAnchorLockTests` — entire file probably obsolete after D.1).

### D.6 — AnchorPropagator / ProductionAnchorWorldFrameResolver

**Phase C status (2026-05-05 investigation):** `ProductionAnchorWorldFrameResolver.TryResolveRelativeBoundaryWorldPos` ([:26-121](Parsek/Source/Parsek/Rendering/ProductionAnchorWorldFrameResolver.cs)) is **already on the v11 anchor-recording-id path** — it reads `relSection.frames` and goes through `TryBuildRelativeAnchorResolverContext`, falling back to `TryResolveRelativeBoundaryShadowWorldPos` on failure. **No live-PID read remains in the non-loop path.** D.6's "non-loop fence" is therefore a **confirmation pass**, not a deletion: re-read the method body, confirm no `anchorVesselId` / `FindVesselByPid` slipped back in, add a CI grep gate that fails the build if either symbol appears anywhere in `TryResolveRelativeBoundaryWorldPos`.

Remaining D.6 work:

- `TryResolveLoopAnchorWorldPos` at [:160-178](Parsek/Source/Parsek/Rendering/ProductionAnchorWorldFrameResolver.cs:160) keeps the live-PID branch (line `:167`: `Vessel anchor = TryFindVesselByPid(rec.LoopAnchorVesselId);`). This stays; loop playback is the explicit carve-out. Tests pin that no non-loop helper reaches it.
- `TryFindVesselByPid` helper at [:517-535](Parsek/Source/Parsek/Rendering/ProductionAnchorWorldFrameResolver.cs:517) survives, but only `TryResolveLoopAnchorWorldPos` calls it in production. Add a `[Conditional("DEBUG")]` counter on `TryFindVesselByPid` that increments on entry; assert in non-loop tests that the counter stays at 0.
- `Source/Parsek/Rendering/AnchorPropagator.cs`: **investigation confirms** AnchorPropagator's production methods (`Run`, `Propagate`, `ResetForTesting`) read no `anchorVesselId` and call no `FindVesselByPid`; all world-frame resolution is delegated to the injected `IAnchorWorldFrameResolver`. AnchorPropagator is **tests-only change confirmed**: relative-boundary tests must cover v0 `anchorRecordingId`, loop-rooted invalid anchor, cross-tree invalid anchor, and missing anchor. No production edit needed.

### D.7 — Map / KSC fence

Map and KSC ghost views are playback surfaces, not metadata readers.

**Verified site inventory (2026-05-05 investigation pass; pre-#751 main):**

`GhostMapPresence.cs` — upstream plan line numbers all confirmed accurate (±2 lines):

| File:line | Symbol / read | Disposition |
|---|---|---|
| `:4231` | `ResolveAnchorInScene` definition; body returns `FlightRecorder.FindVesselByPid(anchorPid) != null` | **Deletion target** — replace with recording-id resolvability check |
| `:4251` | `TryResolveStateVectorMapPointPure` definition; uses `ResolveAnchorInScene` callback | Wrapper, edits follow `ResolveAnchorInScene` |
| `:4290` | `uint anchorPid = currentSection.Value.anchorVesselId;` | **Deletion target** — switch to `anchorRecordingId` resolution |
| `:1099-1131` | Snapshot helper reads `section.anchorVesselId`; carries it into update path | Audit + replace with `anchorRecordingId` |
| `:5117` | Anchor update helper: `Vessel anchor = FlightRecorder.FindVesselByPid(anchorPid)` | Audit + replace |
| `:5370`, `:5631` | Map state update sites; `FlightRecorder.FindVesselByPid(resolution.AnchorPid)` | Audit + replace |
| `:506` (comment) | Notes integration with `ReFlySessionMarker.IsInPlaceContinuation` | PR #751 already centralized one path here; non-loop deletion targets above are independent |
| `:2701` | `Vessel spawned = FlightRecorder.FindVesselByPid(spawnedPid)` | **Stays** — spawn confirmation, not anchor lookup |

`ParsekKSC.cs` — upstream plan line for `TryLookupKscAnchorFrame` drifted +37 lines:

| File:line | Symbol / read | Disposition |
|---|---|---|
| `:1393, 1462` | Callers of `TryResolveKscRelativePose` | Update to pass `RelativeSectionPlaybackTarget` / `anchorRecordingId` |
| `:1524` | `TryResolveKscRelativePose` definition (private; signature accepts `uint anchorVesselId, KscAnchorLookup anchorLookup`) | **Deletion target** — change signature to accept `RelativeSectionPlaybackTarget` / `anchorRecordingId`, call `RelativeAnchorResolver`. (v3 listed this at `:1487` separately and `:1524` for the body — same line range; the upstream plan's `:1487` was already drifted +37 lines pre-#751.) |
| `:1619-1633` | `TryLookupKscAnchorFrame`; `:1625` reads `Vessel anchor = FlightRecorder.FindVesselByPid(anchorVesselId)` then `anchor.GetWorldPos3D()` | **Deletion target** — entire helper goes; map/KSC paths route through resolver |
| `:1150-1155` | Callers passing `TryLookupKscAnchorFrame` as the `KscAnchorLookup` delegate | Update to pass a recording-id-aware delegate or remove indirection |

**Implementation rule:** before D.7 edits, fresh-grep `section.anchorVesselId`, `AnchorPid`, and `KscAnchorLookup` in `GhostMapPresence.cs` and `ParsekKSC.cs`. PR #751's shifts are tracked via the `ReFlySessionMarker.IsInPlaceContinuation` centralization at GhostMapPresence `:841` and `:5172`-area; expect ±5-line drift across the listed sites after merge.

Structural CI gate: no non-loop Map/KSC Relative helper uses `section.anchorVesselId` as a live lookup key. The `NonLoopRelativeLivePidLookupAttempted` counter (D.1.5) increments if any does.

### Phase D acceptance

- All `[PlaybackTrace]` lines during re-fly show ghost positions independent of live vessel motion.
- A divergent re-fly with extreme player path keeps ghosts at original recorded positions.
- Branch A: `scripts/grep-audit-non-loop-live-pid.ps1` and `NonLoopLivePidGuard` reset/count tests pass.
- Branch B: regenerated v0 watch/Re-Fly scenarios pin `NonLoopLivePidGuard.LivePidLookupAttemptsForTesting == 0` after playback.
- `dotnet test` green, no `[ERROR]` in `KSP.log` during the regression scenarios.
- One in-game smoke per phase (separation watch, divergent re-fly, map view, KSC ghost view).

---

## 3. Recording format reset to v0 (Phase E, expanded)

**Recommendation: reset the recording format to v0 with a binary-magic-prefix change AND a new `SchemaGeneration` discriminator field, so old playtest saves cannot be silently re-loaded under the assumption "v0 means the new shape."** Drop the v4–v11 reader code path entirely.

### 3.0 Version surfaces touched by the reset

Eight independent serialized-version axes plus two implicit-schema surfaces, all reset together so the discriminator (§3.1) covers every save artifact:

**A. Trajectory data** (`.prec` binary + `.prec.txt` text). Currently 8 named feature constants v4–v11 in [RecordingStore.cs:57-64](Parsek/Source/Parsek/RecordingStore.cs:57); two binary floor constants `LegacyBinaryVersion = 2` / `SparsePointBinaryVersion = 3` in [TrajectorySidecarBinary.cs:31-38](Parsek/Source/Parsek/TrajectorySidecarBinary.cs:31); text-codec gating in `TrajectoryTextSidecarCodec.cs`.

**B. Recording-tree topology** (`.sfs`-embedded scenario data via `RecordingTreeRecordCodec`). [RecordingTree.cs:10](Parsek/Source/Parsek/RecordingTree.cs:10) `CurrentTreeFormatVersion = 1`.

**C. Vessel/ghost snapshots** (`_vessel.craft`, `_ghost.craft`, `.pcrf` ghost geometry sidecars). [SnapshotSidecarCodec.cs:34](Parsek/Source/Parsek/SnapshotSidecarCodec.cs:34) `CurrentFormatVersion = 1`.

**D. Pannotations cache** (`.pann` smoothing/annotations sidecar) — three independent fields:
- `PannotationsBinaryVersion = 1` ([PannotationsSidecarBinary.cs:138](Parsek/Source/Parsek/PannotationsSidecarBinary.cs:138)) — file binary header
- `AlgorithmStampVersion = 12` ([:275](Parsek/Source/Parsek/PannotationsSidecarBinary.cs:275)) — cache invalidation stamp (bumped to 12 to discard unsafe v11 caches)
- `CanonicalEncoderVersion = 1` ([:276](Parsek/Source/Parsek/PannotationsSidecarBinary.cs:276)) — encoder version

**E. Career ledger** (`ledger.pgld`). [Ledger.cs:16](Parsek/Source/Parsek/GameActions/Ledger.cs:16) `CurrentLedgerVersion = 1`.

**F. ReFlySessionMarker** (implicit, no version constant) — schema is field-presence-defined; PR #751 adds `InPlaceContinuation`. v0 declares the post-#751 marker shape authoritative; older shapes rejected at the marker validator (§3.8).

**G. Other ScenarioModule-embedded `.sfs` data** (implicit, audited in §3.10) — `RewindPoints`, `BranchPoints`, `Tombstones`, `SupersedeRelations`, `MergeJournal`, recordings list, pending-tree state, group hierarchy, kerbal reservations.

**H. Mod-level versioning, NOT part of the reset:**
- `Parsek.version` file — KSP-compat metadata; left alone.
- `AssemblyInfo.cs` — mod assembly version, bumped from 0.9.x to 0.10.0 as a normal release-cadence change (see §6).

The next subsections (3.1–3.11) handle these surfaces; the deletion table in §3.6 lists every constant by file and line.

### 3.1 The discriminator problem (P1)

A naive "rename current to v0" is unsafe because pre-reset code already writes `RecordingFormatVersion = 0` as a default fallback in at least six load paths:

- `ParsekScenario.cs:5608`, `:5612` (legacy splice paths).
- `RecordingTreeRecordCodec.cs:463`, `:467` (codec default-stamp).
- `RecordingTree.cs:185` (tree default).
- `SnapshotSidecarCodec.cs:115` (snapshot default).

After resetting `CurrentRecordingFormatVersion = 0`, these legacy default-0 records would be indistinguishable from new reset-0 records by version alone. Existing playtest saves would not reliably become unloadable.

**Fix: two layers of discriminator, both required:**

1. **Binary magic prefix change in `TrajectorySidecarBinary`.** The first 4 bytes of the `.prec` file change to a new magic that no pre-reset writer ever produced. The current magic is `Magic = "PRKB"` at [TrajectorySidecarBinary.cs:30](Parsek/Source/Parsek/TrajectorySidecarBinary.cs:30) (ASCII `0x50 0x52 0x4B 0x42`); replace with a new 4-byte constant for the post-reset shape (suggest `PSK0`, byte sequence `0x50 0x53 0x4B 0x30`). Old `.prec` files have the old magic; new readers reject any non-matching prefix immediately, before any version field is consulted.

   **Other sidecar magics that need parallel changes** (verified 2026-05-05): `PannotationsSidecarBinary.Magic = "PANN"` and `CanonicalMagic = "PANC"` at [PannotationsSidecarBinary.cs:93-94](Parsek/Source/Parsek/PannotationsSidecarBinary.cs:93); `SnapshotSidecarCodec.Magic = "PRKS"` at [SnapshotSidecarCodec.cs:32](Parsek/Source/Parsek/SnapshotSidecarCodec.cs:32). Each gets a fresh post-reset 4-byte tag. `RecordingManifestCodec` and `Ledger` have **no** magic — they are ConfigNode-based and rely on the schema-generation field alone.

   The current `TrajectorySidecarBinary.CurrentBinaryVersion = RecordingAnchorChainBinaryVersion` resolves to **`11`** (verified at [:62-63](Parsek/Source/Parsek/TrajectorySidecarBinary.cs:62) — `RecordingAnchorChainBinaryVersion = RecordingStore.RecordingAnchorChainFormatVersion = 11`; v3's claim of "10" was investigator error). The write-version ladder at [:155-173](Parsek/Source/Parsek/TrajectorySidecarBinary.cs:155) is a nested ternary across 9 named feature constants; collapses to a single constant after the reset. `IsSupportedBinaryVersion` at [:381-392](Parsek/Source/Parsek/TrajectorySidecarBinary.cs:381) currently accepts **v2-v11 inclusive** (10 separately-named feature constants explicitly listed); collapses to a single supported version (0). `GetBinaryEncoding` at [:395-399](Parsek/Source/Parsek/TrajectorySidecarBinary.cs:395) currently switches on `>= 3`; collapses to a single encoding.
2. **`RecordingSchemaGeneration : int` field stamped at write time** (binary codec, `.sfs` recording metadata via `RecordingTreeRecordCodec`, and any `.pann` / `.craft` / `.pcrf` / ledger sidecars whose loader survives this reset). A new constant `CurrentSchemaGeneration = 1` is the post-reset stamp; the post-reset writer always stamps that value; the post-reset reader requires **strict equality** with `CurrentSchemaGeneration`. Three reject reasons distinguished in the warn log: `generation-missing` (pre-reset save), `generation-older` (`< CurrentSchemaGeneration`, e.g. an older v0 save after a future bump 1 -> 2), `generation-newer` (`> CurrentSchemaGeneration`, an older binary loading a save written by a future generation). Strict equality is required because future resets bump only the generation, not the format version, so a `>=` check would let an older v0 reader silently accept a future generation = 2 save and recreate the same silent-misload class the discriminator is meant to prevent.

The two layers exist because (a) some serialization paths are binary-only (the magic prefix is sufficient there, generation is a defense-in-depth secondary check) and (b) some paths are text-only or `.sfs`-embedded (the generation field is the only check available). An old file fails on whichever check fires first.

**Text sidecar disposition (P2 from review v2):** the `.prec.txt` text codec was listed as a versioned surface in §3.0, but the binary-magic check cannot apply to text. Branch B explicitly **removes the legacy `.prec.txt` load path** and keeps text emission only as a debug-mirror write (toggled by an existing diagnostics setting). The text codec's `formatVersion >= 1` section-authoritative gate (§3.3) is deleted along with the load path. Tests that round-trip text sidecars are deleted or rewritten to round-trip binary. The text-debug-mirror writer stamps the generation field for human readability but is never read back into a `Recording`. This eliminates the "text path stamps generation but cannot satisfy magic" inconsistency.

**Test required:** explicit fixture + tests that a pre-reset save is refused by the post-reset loader, with the expected warn and no `RecordingStore.AddCommittedInternal` call. Three cases: legacy v11 binary file with old magic (`magic-mismatch`), legacy default-0 record with no generation field (`generation-missing`), synthetic future-generation save with `RecordingSchemaGeneration = 2` (`generation-newer`). The third case proves the strict equality holds.

### 3.2 Schema refusal at every load entry point (P1)

The plan v1 said "refused recordings stay out of `RecordingStore.CommittedRecordings`" but didn't spell out the mechanism, and v2 covered only the committed-tree path. Two parallel load paths hydrate recordings on OnLoad: `LoadRecordingTrees` (committed trees) and `TryRestoreActiveTreeNode` (active `isActive=true` trees that come back from a quicksave). Both must apply the same compatibility filter or a pre-reset quicksave with an active tree slips through.

**Shared compatibility predicate:**

The predicate cannot run against the bare `Recording` object as it stands today — `Recording` only carries `RecordingFormatVersion`. The magic prefix and `RecordingSchemaGeneration` are read by `RecordingSidecarStore.LoadRecordingFiles` from a local `TrajectorySidecarProbe` and discarded before `LoadRecordingTrees` / `TryRestoreActiveTreeNode` see the result. Branch B fixes this by returning a richer load result. Two equivalent design options:

- **Option A (recommended):** Promote the probe data to persisted fields on `Recording` so the predicate can read directly. Add `internal int RecordingSchemaGenerationLoaded`, `internal byte[] LoadedMagic` (or `string LoadedMagicTag`), and `internal bool LoadResultSchemaCompatible` (set during `LoadRecordingFiles`). Predicate then reads `rec.LoadResultSchemaCompatible` directly; no separate evaluation in the load callers.
- **Option B:** Change `LoadRecordingFiles` to return a `LoadRecordingResult` struct: `{ bool Ok, int SchemaGeneration, string MagicTag, int FormatVersion, string FailureReason }`. Callers (`LoadRecordingTrees`, `TryRestoreActiveTreeNode`) consume the result and pass it to `IsSchemaCompatible(LoadRecordingResult)` before any `AddCommittedInternal` / pending-tree stash.

Either option captures the magic + generation + format version at the only point that has access to them (sidecar probe). The compat predicate is then:

- `IsSchemaCompatible(rec)` (Option A) or `IsSchemaCompatible(result)` (Option B) returns true iff binary magic matches **and** `RecordingSchemaGeneration == CurrentSchemaGeneration` **and** `RecordingFormatVersion == CurrentRecordingFormatVersion`.
- The strict generation equality is the §3.1 fix; older and newer generations both fail this check with distinct log reasons.

Pick Option A or Option B in commit 2 of Branch B (the one that introduces the discriminator without flipping values yet); the value flip (commit 3) lands the predicate against whichever option commit 2 chose.

**Filter at `LoadRecordingTrees`:**

- Before `AddCommittedInternal`: if `IsSchemaCompatible(rec) == false`, log one warn line `[Recording][WARN] file=<id> reason=<magic-mismatch|generation-missing|generation-older|generation-newer|format-version-mismatch> generation=<n> magic=<...> formatVersion=<n> action=skip`, increment a per-load-pass `skippedSchemaMismatch` counter, and continue.
- Tree-side cleanup: drop the rejected `recordingId` from `RecordingTree.Recordings` (and any `BackgroundMap` entry) so the tree's own structure does not retain stale ids that no `Recording` object backs.
- **Empty-tree handling (P2 from review v5.1):** after the per-recording drop pass, walk each tree and apply this repair-or-reject rule:
  - If `tree.Recordings.Count == 0` after drops, **drop the entire tree** from `committedTrees`. Log `[Recording][WARN] tree=<treeId> reason=all-recordings-rejected action=drop-empty-tree`. Do not retain an empty tree as scaffolding.
  - If `tree.RootRecordingId` points at a rejected recording, **reject the whole tree** (same drop) — there is no safe repair (root is the topology anchor; reparenting would require schema-aware migration). Log `reason=root-recording-rejected`.
  - If `tree.ActiveRecordingId` points at a rejected recording, **clear the field** (`tree.ActiveRecordingId = null`); the tree may still load with no active recording, and the merge journal / load-time sweep will repair via the normal "no active recording" path. Log `reason=active-recording-rejected action=clear-active`.
  - If a `BranchPoint.ParentRecordingIds` or `BranchPoint.ChildRecordingIds` entry references a rejected recording, **drop that branch point**. Log `reason=branch-point-references-rejected count=<n>`.
  - If a `RecordingSupersedeRelation` row references a rejected recording, **drop the relation**. Log `reason=supersede-references-rejected count=<n>`.
- Background map: call `tree.RebuildBackgroundMap()` after the filter pass for every tree that had drops.
- Co-bubble candidates: rejected recordings cannot be co-bubble eligible because they are not in `CommittedRecordings`; no extra cleanup needed beyond the tree drop.
- Sidecar files: **left on disk untouched.** The user can manually remove `<id>.prec`, `<id>_vessel.craft`, `<id>_ghost.craft`, `<id>.pcrf` if desired. Do not auto-delete; the loader cannot tell whether the user wants to recover the data via an out-of-band tool.
- Manifest: if the manifest still lists the rejected `recordingId`, mark the manifest dirty so the next save rewrites it without the orphan reference.

**Filter at `TryRestoreActiveTreeNode` (P1 from review v2):**

`TryRestoreActiveTreeNode` (verified at [ParsekScenario.cs:2957](Parsek/Source/Parsek/ParsekScenario.cs:2957)) is the second load path: when an OnLoad save carries an `isActive=true` tree, this method hydrates its recordings via `RecordingTree.Load(treeNode[t])` then `RecordingSidecarStore.LoadRecordingFiles(rec, treeLocalLoadSet)` (the relevant section is [:2976-2996](Parsek/Source/Parsek/ParsekScenario.cs:2976)). It then builds the active union, may salvage from `PendingTree` state, and stashes the loaded tree as pending. Without an explicit refusal here, a pre-reset quicksave bypasses §3.2's committed-tree filter. The fix:

- Run the same `IsSchemaCompatible` predicate against every recording in the loaded active tree **before** the active-union build, salvage from pending, or pending-tree stash. The check happens immediately after sidecar hydration produces the `Recording` objects, before any state mutates `RecordingStore` or `PendingTree`.
- If any recording in the active tree fails the check, treat the whole active tree as unsupported: log `[Recording][WARN] activeTree=<treeId> reason=schema-mismatch action=refuse-active-tree`, list the failing recording ids and reasons, do not build the active union, do not salvage, do not stash as pending, and do not call `AddCommittedInternal`.
- Clear pending tree state: there is **no** `PendingActiveTreeResume` field (investigation pass 2026-05-05 confirms), but there **is** a static `ParsekScenario.pendingActiveTreeResumeRewindSave` (string, declared at [ParsekScenario.cs:4674](Parsek/Source/Parsek/ParsekScenario.cs:4674)) that is assigned at `:3145` from `treeNodes[t].GetValue("resumeRewindSave")` during active-tree load and consumed by quickload-resume to point the player back at the right RP save. **Active-tree refusal must clear this field too**, alongside `RecordingStore.PendingTree` and `PendingTreeState`. The existing clear sites at `:2699, :2962, :2986` show the correct pattern (`pendingActiveTreeResumeRewindSave = null`); the refusal path adds one more clear before bailing. Also call `ClearPendingQuickloadResumeContext()` on the refusal path so a stale `pendingQuickloadResumeContext` cannot survive from a previous load and match a later tree by id. Without these clears, a refused active tree could still trigger a quickload-resume to a stale RP save in the next OnLoad cycle.
- Sidecar files for the rejected active tree are left on disk per the same UX rule as committed-tree drops. The save's other ScenarioModule data (RP files, ledger, kerbals, marker) is then individually subject to the §3.10 audit's predicates.
- This filter must run before the journal finisher / load-time sweep — the journal finisher operates on the live in-memory state populated by the prior `LoadRecordingTrees` pass and runs against an already-filtered committed-tree view; the active-tree predicate must run earlier in the OnLoad order so the journal does not see ghost references to the refused tree.

**Verified supporting symbols:**

| Symbol | File:line | Notes |
|---|---|---|
| `LoadRecordingFiles(Recording rec)` | `RecordingStore.cs:4039-4041` | Delegates to `RecordingSidecarStore.LoadRecordingFiles(rec)` |
| `LoadRecordingFiles(Recording rec, IReadOnlyDictionary<string, Recording> treeLocalLoadSet)` | `RecordingStore.cs:4050-4055` | Tree-context overload used by `TryRestoreActiveTreeNode` |
| `AddCommittedInternal(Recording rec)` | `RecordingStore.cs:519-523` | Adds to `committedRecordings` list, bumps `StateVersion` |
| `ValidateRecordingId` | `RecordingPaths.cs:233-262` | Unchanged; rejects path traversal and invalid filename chars |
| `RecordingTree.RebuildBackgroundMap` | (verified to exist; signature returns void) | Called after the filter pass for every tree that had drops |

### 3.3 Write/read gate audit (P2)

Setting `CurrentRecordingFormatVersion = 0` alone routes new recordings through legacy code paths because current dispatch is gated on the old version ladder. v2 adds an explicit Branch B audit task before any value-flip:

- **Binary write gate.** `TrajectorySidecarBinary.Write` currently goes binary only when `RecordingFormatVersion >= 2`. Post-reset, v0 is the only supported version, so the `>= 2` gate either becomes unconditional (always binary) or is deleted along with the legacy text fallback. Decide which during the audit; recommendation: always-binary for trajectory data, since the legacy text path was a debug convenience.
- **Section-authoritative text gate.** `TrajectoryTextSidecarCodec` section-authoritative read/write paths are gated on `formatVersion >= 1`. Post-reset, the **legacy text load path is removed entirely** per §3.1; the section-authoritative gate, the non-section-authoritative path, and `TrajectoryTextSidecarCodec.LoadXxx` callers all go. The text codec survives only as a debug-mirror writer (toggled by an existing diagnostics setting) and emits the v0 shape with the generation field stamped for human readers.
- **Text probe support.** Any `IsSectionAuthoritativeFormat` / `IsModernFormat` / `IsLegacyFormat` probe collapses to a constant; remove the probe. Tests that exercise probe behaviour collapse to "v0 is the only shape."
- **Annotation/snapshot codecs.** Similar audit for `PannotationsSidecarBinary`, `SnapshotSidecarCodec`. Each has its own version-gated branches; document them all in the Branch B commit message and either delete or unconditionalize each branch.

**Verified gate inventory (2026-05-05 investigation pass):**

| File | Line | Gate | Snippet context |
|---|---|---|---|
| `TrajectorySidecarBinary.cs` | `:155, 157, 159, 161, 163, 165, 167, 169, 171` | Write-version ladder `>= RecordingAnchorChainBinaryVersion` ... `>= SparsePointBinaryVersion` | Nested ternary, all collapse to single constant after reset |
| `TrajectorySidecarBinary.cs` | `:252` | `probe.FormatVersion >= 1` | Conditional flat-fallback healing for v1+; collapses |
| `TrajectorySidecarBinary.cs` | `:408, 424` | `binaryVersion >= SparsePointBinaryVersion` | Sparse vs legacy dense path on read; legacy path deletes |
| `TrajectorySidecarBinary.cs` | `:456, 462, 492, 499` | `binaryVersion >= TerrainGroundClearanceBinaryVersion` (`:456, 492`); `binaryVersion >= StructuralEventFlagBinaryVersion` (`:462, 499`) | Per-point optional doubles/bytes; all become unconditional in v0 |
| `TrajectorySidecarBinary.cs` | `:534, 558` | `binaryVersion >= PredictedOrbitSegmentBinaryVersion` | `isPredicted` flag byte; becomes unconditional |
| `TrajectoryTextSidecarCodec.cs` | `:423, 1194, 1263, 1305, 1319` | `formatVersion >= 1` (5 sites) | All five tied to legacy text load path; **deleted entirely** in Branch B per §3.1 (text becomes debug-mirror writer only) |
| `RecordingStore.cs` | `:4074, 4093` | `rec.RecordingFormatVersion >= 2` (`:4074`); `rec.RecordingFormatVersion >= PredictedOrbitSegmentFormatVersion` (`:4093`) | Audit pass; collapse or unconditionalize |
| `RecordingSidecarStore.cs` | `:357, 382` | `rec.RecordingFormatVersion >= LaunchToLaunchLoopIntervalFormatVersion` (both) | Audit pass; collapse |
| `RelativeAnchorResolver.cs` | `:226, 557` | `recording.RecordingFormatVersion >= RelativeLocalFrameFormatVersion` (`:226`); `recording.RecordingFormatVersion >= RecordingAnchorChainFormatVersion` (`:557`) | Audit pass; both become unconditional in v0 since the v0 schema is the modern shape by definition |
| `FlightRecorder.cs` | `:8418` | `activeRec.RecordingFormatVersion >= targetFormatVersion` | Audit pass; remove the parameterised gate (only one supported version) |
| `ParsekFlight.cs` | `:18549` | `rec.RecordingFormatVersion >= RecordingStore.RecordingAnchorChainFormatVersion` | Audit pass; collapse |
| `GhostPlaybackEngine.cs` | `:2378` | `traj.RecordingFormatVersion >= RecordingStore.RecordingAnchorChainFormatVersion` | Audit pass; collapse |
| `PannotationsSidecarBinary.cs` | `:403, 980-982` | `IsSupportedBinaryVersion(binaryVersion)` accepts only `v == PannotationsBinaryVersion` | Already a single-version gate; flips to `== 0` |
| `SnapshotSidecarCodec.cs` | `:34` | `CurrentFormatVersion = 1` | Single version; no observed legacy gates in header read; resets to 0 |
| `Ledger.cs` | `:16` | `CurrentLedgerVersion = 1` | No per-action version gates; resets to 0 |

**Stale claim corrected — `RecordingTreeRecordCodec` has no `delta*`/`preTree*`/`resourcesApplied` legacy load-only seam.** v1 / v2 of this plan claimed such a seam existed and needed deletion. **Investigation 2026-05-05 confirms the seam is not present in the current code** — `RecordingTree` fields are loaded transparently by ConfigNode without per-field version checks. The claim originated in `.claude/CLAUDE.md`'s note that "Phase F removed the public tree resource delta fields; legacy `delta*` / `preTree*` / `resourcesApplied` keys are load-only via a transient residual seam, and `TreeFormatVersion` gates the new save shape" — that note is stale; the seam was already removed before this plan was written. Drop this row from the Branch B work item list.

The audit is an explicit task, not a side effect. Branch B's commit message lists every gate that was inspected and what was done with it.

### 3.4 Why reset numbering instead of bumping to v12

- The plan doc explicitly calls v11 "private-development breaking format" and v7-v10 recordings "disposable". Once we delete the v4-v11 readers, the numbering is vestigial — it advertises a migration history we no longer support.
- There is precedent: PR #114 reset the recording format 7 -> 0 for the same reason, with no legacy migration.
- v0 is an honest signal: "this is the new contract, no legacy compatibility promised, regenerate fixtures."
- The mechanical cost is low *with the discriminator from §3.1*: one `CurrentRecordingFormatVersion = 0` constant, the eleven named feature constants collapse to inline gating that no longer exists, plus the binary magic + generation field.
- Future resets from 0 -> 1 (or further) bump the generation, not the version number, until the mod actually has users.

### 3.5 Cost / tradeoff

- All existing playtest saves under `Kerbal Space Program/saves/` become unloadable. Acceptable — user has confirmed no career save needs preservation. UX on load: a one-time warn log per unsupported recording, a recordings-table empty state, and orphan sidecars left on disk. No partial-load recovery is attempted.
- Bug reports referencing "v8 saves" lose their grep handle. Acceptable — those reports are already closed.
- Specific test/fixture regen surfaces are listed in §3.5.1.

### 3.5.1 Test fixture, injector, and showcase recording regen

Branch B touches several test-side files; some are search/replace, some need fixture re-baking, some collapse to a v0-only shape. None of these are production code, but they are all gates on the headless test suite.

**Test generators (rebuild what they emit, not the API shape):**
- `Source/Parsek.Tests/Generators/RecordingBuilder.cs` — generic recording builder used across xUnit. Defaulted `RecordingFormatVersion` becomes 0 with `RecordingSchemaGeneration = 1`. Builders that emit `anchorVesselId`-only Relative sections need to author `anchorRecordingId` (or remove the Relative-section helper entirely if Phase A/B already covered).
- `Source/Parsek.Tests/Generators/ScenarioWriter.cs` — drives `--filter InjectAllRecordings`. The 8 synthetic recordings injected into the test save get re-baked at v0 with the new discriminator stamped. The existing purge guard (refuses to wipe `Recordings/` while `KSP.log` is locked) stays.
- `Source/Parsek.Tests/Generators/VesselSnapshotBuilder.cs` (per CLAUDE.md project layout) — snapshot version flips to 0.

**Showcase recording builders in `SyntheticRecordingTests.cs`:**
- "Part Showcase - Light" (light on/blink/off cycle, [SyntheticRecordingTests.cs:1577](Parsek/Source/Parsek.Tests/SyntheticRecordingTests.cs:1577)).
- "Part Showcase - RCS".
- AnimateHeat 3-state cycle ([:2397](Parsek/Source/Parsek.Tests/SyntheticRecordingTests.cs:2397)).
- Generalized static-trajectory looping showcase helper ([:1073](Parsek/Source/Parsek.Tests/SyntheticRecordingTests.cs:1073)).
- The PID-100000-or-bust event-PID invariant for single-part showcase ghosts ([:4842](Parsek/Source/Parsek.Tests/SyntheticRecordingTests.cs:4842)) is independent of format version, but the surrounding round-trip tests need v0 stamps.
- The InjectAllRecordings re-run scenario ([:5043](Parsek/Source/Parsek.Tests/SyntheticRecordingTests.cs:5043)), KSP.log lock refusal cases ([:5150](Parsek/Source/Parsek.Tests/SyntheticRecordingTests.cs:5150), [:5201](Parsek/Source/Parsek.Tests/SyntheticRecordingTests.cs:5201)) — keep, retest under v0.

These are *static-trajectory looping* showcase recordings, so they are exclusively Absolute / loop-anchored. They do not exercise the chain resolver and are unaffected by Phase D rendering changes; the only changes are version stamps and discriminator fields.

**Test files that are themselves obsolete or redundant:**
- `Source/Parsek.Tests/RecordingBuilderV6Tests.cs` — V6 in the name is a stale anchor; rename or delete after Branch B (no v6 distinction exists post-reset).
- `Source/Parsek.Tests/LegacyTreeMigrationTests.cs` — entire file is migration-tested; delete once Branch B refuses pre-v0 trees outright.
- `Source/Parsek.Tests/FormatVersionTests.cs` — collapse to assert a single supported format (v0 with `RecordingSchemaGeneration = 1`); existing parameterized cross-version tests become obsolete.

**Hardcoded version literals across the test suite (P2 from review v2):**

The version-literal grep is much wider than the InGameTests subset. Branch B opens with a mechanical pre-pass that catalogues every pinned-version test and assigns it one of three dispositions:

1. **Flip to v0** — round-trip / behaviour tests whose intent is "current format works"; replace literals with the new constants and stamp `CurrentSchemaGeneration`.
2. **Delete** — legacy-acceptance tests whose intent is "we accept old formats"; delete entirely (their contract is the opposite of the post-reset rule).
3. **Rewrite** — tests whose intent is "we reject old formats" or "we round-trip the cross-version migration path"; rewrite as refusal tests against the discriminator (`magic-mismatch`, `generation-missing`, `generation-older`, `generation-newer`, `format-version-mismatch`).

Initial inventory from grep across `Source/Parsek.Tests/` and `Source/Parsek/InGameTests/` (31 files; expect to grow):

| File | Intent | Disposition |
|---|---|---|
| `Source/Parsek/InGameTests/RuntimeTests.cs` | Mixed; PeerSource v8, RecordingFormat v7/v8 fixtures | Flip to v0 |
| `RecordingStorageRoundTripTests.cs` | Cross-version round-trip v0–v3 | Delete cross-version cases; keep one v0 round-trip |
| `TrajectorySidecarBinaryTests.cs` | Inline binary version fixtures v2–v11 | Delete pre-v0 cases; collapse to v0-only |
| `FormatVersionTests.cs` | Cross-version compatibility | Rewrite as refusal tests against the discriminator |
| `LegacyTreeMigrationTests.cs` | Migration-only | Delete entire file |
| `RecordingBuilderV6Tests.cs` | V6 builder helpers | Delete (V6 anchor stale) |
| `RecordingOptimizerTests.cs`, `RecordingStoreTests.cs`, `RecordingsManagerTests.cs`, `TimelineBuilderTests.cs`, `TreeCommitTests.cs` | Round-trip + optimizer behaviour | Flip to v0 |
| `Rendering/SmoothingPipelineTests.cs`, `Rendering/SmoothingPipelineLoggingTests.cs`, `Rendering/CoBubbleSidecarRoundTripTests.cs`, `Rendering/CoBubbleBlenderTests.cs`, `Rendering/OutlierFlagsSidecarRoundTripTests.cs`, `Rendering/OutlierFlagsTests.cs` | Rendering with v7/v8 fixtures | Flip to v0; some sidecar-roundtrip tests may need full re-bake |
| `GhostMapSoiGapStateVectorTests.cs` | v6 fixtures | Flip to v0 |
| `MapMarkerRendererTests.cs`, `LoopAnchorTests.cs`, `PlaybackTrajectoryTests.cs`, `RuntimePolicyTests.cs`, `EnvironmentDetectorTests.cs`, `ComputeStatsTests.cs`, `ParsekTimeFormatTests.cs`, `AltitudeSplitExtendedTests.cs`, `ExplosionFxTests.cs` | Various behaviour tests with version literals | Flip to v0 |
| `BugFixTests.cs`, `Bug414SpawnThrottleTests.cs`, `Bug458BinaryFlatFallbackHealTests.cs`, `Bug585FollowupSaveSkipTests.cs` | Per-bug regression fixtures | Inspect each: flip if behaviour-test, delete if legacy-acceptance |
| `MergeDialogTests.cs` | Multi-version round-trip | Flip to v0; some cases may go obsolete with PR #751 |
| `Fixtures/DefaultCareer/persistent.sfs`, `Fixtures/DefaultCareer/Parsek/Saves/parsek_rw_0a74d6.sfs` | Checked-in `.sfs` fixtures | Re-bake under v0 |
| `ReFlyTreeAnchorLockTests.cs` | Tests for the anchor lock that D.1 deletes | **Delete entire file** after D.1; tests have no surviving production target |
| `Source/Parsek/InGameTests/RuntimeTests.cs` | In-game tests; some exercise display offset / live-PID paths | Audit per-test: (a) tests for `RefreshReFlyAnchorActivationGate` / `externalActivationDeferred` (e.g. `:12455`, `:12663`, `:12676`, `:12702`, `:12704`) **delete** alongside D.5; (b) tests for non-loop Relative live-PID lookup **delete** alongside D.4; (c) v7/v8 fixture tests **flip to v0**; (d) tests for chain resolver / map view / KSC view **rewrite** to assert recorded-coordinate behaviour after Phase D |

**Branch B grep gate:** before commit 3 (the value flip), an explicit grep across `Source/Parsek.Tests/` and `Source/Parsek/InGameTests/` for `RecordingFormatVersion\s*=\s*\d+`, `formatVersion\s*=\s*\d+`, `binaryVersion\s*=\s*\d+`, `PeerSourceFormatVersion\s*=\s*\d+`, and `\bversion\s*=\s*\d+` runs and produces a hit list. Every hit must be classified as flip / delete / rewrite in the commit message. After commit 3, the same grep with version literals other than 0 must return zero hits outside negative-test cases (the rewritten refusal tests intentionally feed in `7`, `2`, etc. to exercise the rejection paths).

**Branch B acceptance gate for this section:**
- `dotnet test` (full headless, excluding `InjectAllRecordings`) green against the regenerated fixtures.
- `dotnet test --filter InjectAllRecordings` green against re-baked synthetic recordings.
- A KSP playtest run that injects the 8 synthetic recordings and verifies the showcase ghosts (lights, RCS, AnimateHeat) play their loops correctly. This is the runtime smoke that proves the showcase recording shape survived the reset.

### 3.6 What gets deleted in the reset commit

Eight independent version axes are touched, not the two-or-three v1 implied:

| Surface | Deletes / changes |
|---|---|
| `RecordingStore.cs:57-64` | All eight named feature constants (`LaunchToLaunchLoopIntervalFormatVersion` ... `RecordingAnchorChainFormatVersion`); `CurrentRecordingFormatVersion = 0`; new `RecordingSchemaGeneration = 1` constant. |
| `RecordingStore.cs:135-164` | `LegacyMergeStateMigrationCount`, `EmitLegacyMergeStateMigrationLogOnce`, `BumpLegacyMergeStateMigrationCounterForTesting`, `ResetLegacyMergeStateMigrationForTesting`. The committed-bool -> tri-state migration is gone. |
| `RecordingStore.cs:78` | `LegacyGloopsGroupName` rename migration. |
| `RecordingStore.cs:194-202` | `LegacyPrefix` log compatibility. |
| `TrajectorySidecarBinary.cs` | `LegacyBinaryVersion = 2`, `SparsePointBinaryVersion = 3`, the named ladder up to v11; replace with new binary magic prefix (§3.1). `IsSupportedBinaryVersion` and `GetBinaryEncoding` collapse to a single supported version. |
| `TrajectoryTextSidecarCodec.cs` | All `formatVersion >= N` gating; `TRACK_SECTION` shape becomes the v0 shape. `anchorVesselId` value key removed. |
| `RecordingTree.cs:10` | `CurrentTreeFormatVersion` resets to 0; legacy `TreeFormatVersion = 0` default at `:185` keeps the same numeric value but the schema-generation field disambiguates (post-reset `Generation = 1`, pre-reset `Generation = 0`). |
| `RecordingTreeRecordCodec.cs` | Stamp `RecordingSchemaGeneration` on every recording-record write. (The `delta*` / `preTree*` / `resourcesApplied` legacy seam claimed in earlier drafts of this plan does **not** exist in the current code per the 2026-05-05 investigation pass; that row removed.) |
| `SnapshotSidecarCodec.cs:34` | `CurrentFormatVersion` resets to 0 with generation discriminator on writes. |
| `PannotationsSidecarBinary.cs:138` | `PannotationsBinaryVersion = 1` resets to 0. |
| `PannotationsSidecarBinary.cs:275` | `AlgorithmStampVersion = 12` resets to 0. v12 caches discarded on first load (already the existing pattern). |
| `PannotationsSidecarBinary.cs:276` | `CanonicalEncoderVersion = 1` resets to 0. |
| `Ledger.cs:16` | `CurrentLedgerVersion = 1` resets to 0 with generation discriminator; the `RecordingFormatVersion` audit pattern applies here too. |
| `Recording.cs` | Pre-Phase-F transient residual fields. |
| `TrackSection.cs:56` | `anchorVesselId` field. The "lifecycle endpoint at v12" target collapses to "removed at v0 reset commit, which chronologically follows v11." |
| `BackgroundRecorder.cs`, `FlightRecorder.cs`, `SessionMerger.cs`, `RecordingOptimizer.cs` | Every read of `section.anchorVesselId`. `AnchorIdentityKey(section)` simplifies (recording id is the only identity). |
| Loader | Discriminator gate per §3.1; LoadRecordingTrees filter per §3.2. |

**v5 correction (P2 from review v4):** v4 listed `RelativeAnchorResolution.cs` as holding "Legacy v5-and-older `ReferenceFrame.Relative` reader path." That row contradicted §D.2 (which correctly says `RelativeAnchorResolution.cs` stays — only specific live-anchor helpers go) and was factually wrong: the legacy v5 Relative reader path lives in the **trajectory codec** (`TrajectoryTextSidecarCodec.cs` + `TrajectorySidecarBinary.cs`), gated by `RelativeLocalFrameFormatVersion = 6`. Those gates are already in the §3.3 inventory and the §3.6 row for the codec files. The stale `RelativeAnchorResolution.cs` row is removed. The class itself is treated per §D.2.

### 3.7 Legacy tolerance — confirmed already dropped by post-#751 main (verified)

**Verification post-merge (v5, 2026-05-05 after `e78a4755`):** PR #751 was merged at `f530c9ad`, and the merge **does** include the legacy-drop commits (`cc0981eb` lineage). The plan worktree now sits on top of merged main; grep against the worktree confirms:

- `Recording.LegacyPreReFlyOriginal*` — **gone** from production code (only test references remain at `Source/Parsek.Tests/ReFlyTreeAnchorLockTests.cs:285+`).
- `RecordingTreeRecordCodec` legacy `PRE_REFLY_ORIGINAL` write path — **gone**. The codec now has only a comment at `:315` ("PRE_REFLY_ORIGINAL is no longer written"). A read-side silent-drop tolerance still exists (pinned by `RecordingTreeRecordCodec_RoundTrip_DropsLegacyPreReFlyOriginalNode` at `ReFlyTreeAnchorLockTests.cs:285`); Branch B's loader-refusal makes this read-tolerance unreachable and Branch B should delete it alongside the test.
- `MergeDialog.RestoreOriginFromLegacyPreReFlyOriginalSnapshot`, `TrimInPlaceAttemptBackToOriginRewindPoint`, `PurgeInPlaceAttemptEvents`, `legacyInPlaceSession` gate, `legacyOriginalRestored` gate, `inPlaceTrimmed` gate — **all gone** (grep returns no production hits).
- `MarkerValidator.legacyReusedCommittedActive` relax-set — **gone**. MarkerValidator now routes through `ReFlySessionMarker.IsInPlaceContinuation(marker)` (the flag-checking helper) at `:160` and `:198`; no id-equality fallback.
- `ReFlySessionMarker.IsInPlaceContinuation` legacy id-equality fallback — **gone**. The static helper at `:110-116` strictly requires the `InPlaceContinuation` flag; the OLD id-equality fallback was retired in the merge.
- `InPlaceChainContinuityToleranceSeconds` constant — **gone**.

**Branch B remaining work in this area:**
- Delete the `RecordingTreeRecordCodec` PRE_REFLY_ORIGINAL **read-side silent-drop branch** (the comment-only write side is already done; the read tolerance is the last legacy tolerance for Branch B to clear).
- Delete the test `ReFlyTreeAnchorLockTests.RecordingTreeRecordCodec_RoundTrip_DropsLegacyPreReFlyOriginalNode` (already in §3.5.1's full-file deletion of `ReFlyTreeAnchorLockTests.cs`).
- Confirmatory grep for the items above; if grep is clean (which v5 has just verified), close §3.7 with a one-line "no-op after #751 merge" note.

### 3.8 ReFlySessionMarker schema (implicit)

`ReFlySessionMarker` is part of `.sfs` ScenarioModule data. It has no explicit version constant. The merged-main field set (verified 2026-05-05 against `f530c9ad` post-`e78a4755` merge, [ReFlySessionMarker.cs:32-98](Parsek/Source/Parsek/ReFlySessionMarker.cs:32)):

- `SessionId` (string, GUID per invocation; `:35`)
- `TreeId` (string; `:38`)
- `ActiveReFlyRecordingId` (string; `:43`)
- `OriginChildRecordingId` (string; `:46`)
- `SupersedeTargetId` (string, nullable in legacy markers; `:54`)
- `RewindPointId` (string; `:57`)
- `SelectedRootPartPersistentId` (uint, optional; `:64`)
- `InvokedUT` (double; `:67`)
- `InvokedRealTime` (string, ISO 8601; `:70`)
- **`InPlaceContinuation` (bool; `:83`)** — set by `RewindInvoker.AtomicMarkerWrite` when forking the attempt off the same physical vessel as origin (issue #734). All in-place gating across the codebase routes through `ReFlySessionMarker.IsInPlaceContinuation(marker)` static helper at `:110-116`, which strictly requires the flag.
- `PreSessionBranchPointIds` (List<string>, optional with sentinel for absent-vs-empty round-trip; `:98`)

**v5 correction:** v4 claimed this field "does not exist on merged main" and rewrote the §3.7/§3.8 narrative around id-equality detection. That was wrong — the verification grep ran against the plan worktree's pre-#751 checkout. Post-merge verification (`grep -n "InPlaceContinuation\|inPlaceContinuation" Source/Parsek/ReFlySessionMarker.cs`) confirms field at `:83`, write at `:135-136`, read at `:198-199`, helper at `:110-116`. v3's original schema claim was correct.

Implementation: stamp `RecordingSchemaGeneration` on `ReFlySessionMarker.SaveInto` and require the stamp on `LoadFrom`. Reject markers without the stamp. Mark the rejected marker's session as cleared so the validator does not attempt to use stale state. Field-set changes (additions/removals like the `InPlaceContinuation` introduction) bump the generation, not a per-marker version number.

### 3.9 absoluteFrames disposition

**v0 schema includes `absoluteFrames` as a debug backstop, retained through Branch C playtest acceptance.** The shadow stays in the v0 binary/text shape; the chain resolver does not consult it; callers may fall back to it on resolver-miss until Branch C explicitly drops it. This is the only "kept legacy data" item in v0.

### 3.10 .sfs schema audit

ConfigNode-format `.sfs` save files have their own implicit schema (KSP-format quicksave, RP files, scenario module format). Branch B includes a one-pass audit.

**Verified scope (2026-05-05 investigation pass):** none of the audited files carry an explicit version stamp today. Every surface is implicit / field-presence-defined. The audit's job is therefore not "harmonize existing version stamps" but "**stamp `RecordingSchemaGeneration` on every ScenarioModule write that needs to round-trip across the v0 reset**" — and reject saves on read where the stamp is missing.

| Surface | File | Verified state |
|---|---|---|
| `ReFlySessionMarker` | `Source/Parsek/ReFlySessionMarker.cs` | Implicit. Fields: `sessionId`, `treeId`, `activeReFlyRecordingId`, `originChildRecordingId`, `supersedeTargetId`, `rewindPointId`, `selectedRootPartPersistentId`, `invokedUT`, `invokedRealTime`, `preSessionBranchPointIdsPresent` + array, plus PR #751 `inPlaceContinuation`. **No version stamp.** Stamp the generation field on `SaveInto`; reject on `Load` if the stamp is missing or unequal to `CurrentSchemaGeneration`. |
| `MergeJournal` (via `MergeJournalOrchestrator`) | `Source/Parsek/MergeJournalOrchestrator.cs` | Implicit. Persisted via `MergeJournal.OnSave()`; no version field observed. Stamp generation; refuse on mismatch. |
| `LoadTimeSweep` | `Source/Parsek/LoadTimeSweep.cs` | Transient OnLoad pass; no persistence observed. No audit needed. |
| `SupersedeCommit` | `Source/Parsek/SupersedeCommit.cs` | Builds `RecordingSupersedeRelation` rows persisted via `RecordingTree`; no separate version stamp. Generation rides on the parent tree write. |
| `ParsekScenario.OnSave/OnLoad` | `Source/Parsek/ParsekScenario.cs` | No explicit root-level version field — recordings carry their own `recordingFormatVersion` (read at `:5601`). Stamp the generation at the ScenarioModule root so the OnLoad refuses a pre-reset scenario before per-recording hydration runs. |
| `CrewReservationManager` | `Source/Parsek/CrewReservationManager.cs` | Implicit; reservation children `RESERVATION` without schema versioning. Stamp generation at the reservation-list root. |
| `GroupHierarchyStore` | `Source/Parsek/GroupHierarchyStore.cs` | Implicit; ConfigNode tree without versioning. Stamp generation at the root. |
| `RecordingGroupStore` | `Source/Parsek/RecordingGroupStore.cs` | Implicit; group membership without versioning. Stamp generation at the root. |
| `RewindInvoker` (RP `.sfs` writes) | `Source/Parsek/RewindInvoker.cs` | RP files are KSP-format quicksaves; the Parsek-authored portion is metadata only. Stamp generation in the metadata block; an old RP without it is refused. |

**Default-on-missing-field behaviour:** every load path that consults `RecordingSchemaGeneration` defaults to `0` on a missing field (signaling pre-reset) and refuses the parent surface. There is no "default to current and stamp on next write" silent migration — that contradicts §3.1's strict equality.

The audit is a Branch B sub-task with its own commit; it is not the same edit as the trajectory codec changes.

### 3.10.1 .pann cache discard UX (P3 from v3 review)

Resetting `PannotationsBinaryVersion`, `AlgorithmStampVersion`, and `CanonicalEncoderVersion` to 0 means every `.pann` file on disk in the user's save folder is unrecognised on first load. That is the **intended** behaviour per `.claude/CLAUDE.md` HR-10 ("`.pann` is regenerable; discard-and-recompute on probe failure"), but it is a player-visible event: a small first-load warm-up cost as `.pann` files are recomputed.

UX bullet for Branch B: log `[Pannotations][INFO] cache=invalid generation-old action=recompute count=<n>` once per session at first detection; the recompute itself runs in the background per existing HR-10 path. No player intervention needed; no recovery tool offered.

### 3.11 Loader behaviour after reset

- v0 sidecar with new magic prefix and `RecordingSchemaGeneration == CurrentSchemaGeneration` (currently 1): load normally.
- Anything else: refuse with the warn (§3.2); the recording stays out of `RecordingStore.CommittedRecordings`, the tree is pruned, the background map is rebuilt. Active-tree refusals at `TryRestoreActiveTreeNode` (§3.2) reset pending active-tree resume state. The save file itself is left on disk untouched.
- No "auto-migrate", no "shadow promotion", no "infer from PID".
- One-time per session: emit `[Recording][INFO] format=v0 generation=1 magic=PSK0` at first successful load so playtest logs greppably show which format was active.

---

## 4. Order of work

Three branches, smallest blast radius first:

### Branch A — `refly-phase-d` (the Re-Fly wrap)

- Branch A is delivered in PR #755. Original preference was one commit per phase, but implementation landed as broader deletion commits plus review/documentation follow-ups; use the "Branch status after PR #755" block for the authoritative commit mapping.
- Acceptance per phase from section 2 above is satisfied for code/grep/xUnit gates; runtime smoke and full `InjectAllRecordings` remain manual checks.
- Phase E "legacy cutoff for Relative sections" rolls into D.4 / D.6 / D.7 organically: pre-v11 Relative sections without `anchorRecordingId` already fail closed there.
- Land before the format reset so the chain resolver is the only path before we delete the older-format reader.
- **Base note (updated v5.6 after Branch A delivery):** PR #751 was merged at `f530c9ad` on 2026-05-05; PR #721 (game-state UI overlays) and follow-ups merged after that. The plan worktree merged current `origin/main` at `551746e1`, and Branch A was implemented on `refly-phase-d` after that merge. Do not rely on a hardcoded ahead count; re-check with `git rev-list --count origin/main..HEAD`, `git rev-list --count --no-merges origin/main..HEAD`, and `git rev-list --count HEAD..origin/main` before giving rebase guidance. Branch B should start from current `origin/main` or from a fresh worktree after Branch A merges, then re-run the greps below before editing. Concrete `Source/Parsek/` conflict surface from PR #751 alone (verified via `git --no-pager show --stat f530c9ad`):
  - **`Source/Parsek/ParsekFlight.cs` (+195 lines)** — most consequential. Branch A's entire deletion arc lives in this file. D.1 hit counts (27/8/58/13) and 8 LateUpdate sites listed in v3 will drift; **re-grep mandatory at rebase**.
  - **`Source/Parsek/RewindInvoker.cs` (+378 lines)** — large churn. D.5's spawn-frame defer touch points may shift.
  - **`Source/Parsek/MergeDialog.cs`** — D.4's helper-split scope likely shrinks because in-place machinery moved. Re-confirm at rebase.
  - **`Source/Parsek/MarkerValidator.cs`** — post-merge canonical detector is `ReFlySessionMarker.IsInPlaceContinuation(marker)` (the flag-checking helper) at `:160` and `:198`. v3's `InPlaceContinuation` flag claim was correct; v4's id-equality reading at `:165-168` was against the pre-merge worktree state and is reverted in v5. Branch A and Branch B both consume the flag-based contract.
  - **`Source/Parsek/LoadTimeSweep.cs`** — sweep logic touched. §3.10 audit must re-read this file post-merge.
  - **`Source/Parsek/SupersedeCommit.cs`** — supersede logic touched. §3.10 audit applies.
  - **`Source/Parsek/RevertInterceptor.cs`** — touched (was not in v3's surface list at all).
  - **`Source/Parsek/ParsekScenario.cs`** (+16 lines) — TryRestoreActiveTreeNode area may have shifted; §3.2's claim that the predicate insertion point is at `:2976-2996` needs re-verification.
  - **`Source/Parsek/GhostMapPresence.cs`** — PR #751 routes one path through `ReFlySessionMarker.IsInPlaceContinuation`; D.7's deletion targets are independent but ±5-line drift expected.
  - **`Source/Parsek/Rendering/RenderSessionState.cs`** — `IsInPlaceContinuationMarker` at `:1163` delegates to `ReFlySessionMarker.IsInPlaceContinuation(marker)` (the flag helper) at `:1169`; D.6 unaffected.
  - **`Source/Parsek/Recording.cs`, `Source/Parsek/RecordingTreeRecordCodec.cs`, `Source/Parsek/ReFlySessionMarker.cs`** — touched by PR #751. Branch B (not A) consumes these; §3.7's audit must re-read post-merge to catalogue what actually survived `cc0981eb` (or `cc0981eb`-equivalent commits in the merge).
  - **Test files churned**: `MergeDialogResourcesAppliedTests.cs` (+1033/-1033 net), `Bug585InPlaceContinuationRestoreTests.cs` (+561), `SupersedeCommitTests.cs` (-1540), `Bug618ReFlyMergeParentChainTipTests.cs` (+150 churn), `LoadTimeSweepTests.cs`, `AtomicMarkerWriteTests.cs` (+393), `ReFlyRevertDialogTests.cs`, `ReFlySessionMarkerRoundTripTests.cs` (+163), `ReFlyTreeAnchorLockTests.cs`, `RelativeAnchorResolutionTests.cs`. Branch B's §3.5.1 fixture-regen and grep gate must rebase against the post-#751 shape and rerun the version-literal grep.

  **Additional post-#751 conflict surface (PR #721 + follow-ups, 21 commits):** `Source/Parsek/InGameTests/RuntimeTests.cs` gains **+1070 lines** (Astronaut + Mission Control despawn-leak in-game tests, overlay coverage). §3.5.1's RuntimeTests inventory and the v0-reset version-literal grep must rebase against the new shape; the four-disposition class table (delete D.4/D.5 tests, flip v7/v8 fixtures, rewrite chain-resolver tests) still applies but expects a much larger test surface. UI/overlay code in `ParsekUI.cs`, `ParsekKSC.cs`, and several new overlay-related files also touched; verify post-rebase that no overlay code newly reads `section.anchorVesselId`.

  **Rebase recipe:** in the plan worktree, `git fetch origin && git merge origin/main` (this plan branch already used a merge for #751; keep that pattern). The plan doc itself rebases cleanly. The line-table inaccuracy only manifests when Branch A starts editing — that worktree gets created fresh from post-rebase main and re-runs the verification greps before any deletion.

### Branch B — `format-v0-reset`

- Commit 1: write/read gate audit (§3.3) — document and decide each gate without flipping any value yet.
- Commit 2: introduce binary magic prefix and `RecordingSchemaGeneration = 1` field, stamped on writes only; readers still accept legacy.
- Commit 3: flip `CurrentRecordingFormatVersion = 0`, reset every other version constant per §3.6, delete legacy readers, delete migration helpers, delete `anchorVesselId` field, delete post-#751 legacy tolerance items per §3.7, regenerate test fixtures, update in-game test hardcoded versions.
- Commit 4: `.sfs` schema audit pass per §3.10.
- Acceptance: `dotnet test` (full headless) green; `dotnet test --filter InjectAllRecordings` green against re-baked fixtures; in-game smoke (Watch + active Re-Fly + map + KSC) on a fresh v0 save; loader-refusal test pass against a checked-in pre-reset legacy fixture.
- **Rebase note:** Branch B rebases on top of post-#751 main and post-Branch-A main. Test files most likely to need re-baking after the rebase: `MergeDialogResourcesAppliedTests.cs`, `Bug585InPlaceContinuationRestoreTests.cs`, `SupersedeCommitTests.cs`.
- Bumps mod version to v0.10.0.

### Branch A → Branch B playtest gate (P2 from review v3)

Between Branch A landing and Branch B starting, run a **legacy v11 save validation** alongside the post-Phase-D regression smoke. Phase D ships before format reset, so the implementer needs to know whether to validate Phase D against a v11 fixture or only post-reset:

- **Required:** load an existing v11 save (one of the playtest saves under `Kerbal Space Program/saves/`), enter flight on a multi-stage launch, run a basic Watch + Re-Fly cycle, confirm no `[ERROR]` lines in `KSP.log`. This proves Phase D didn't break legacy v11 reading even though Branch B's loader-refusal will refuse v11 in the next phase.
- **Required:** the in-game smoke per D.1-D.7 acceptance (separation watch, divergent re-fly, map view, KSC ghost view) on a v11 save.
- **Not required:** v0 fixture testing — that's Branch B's gate.

Without this gate, a Phase D regression that only manifests against legacy data could ship hidden under "Branch B will refuse it anyway" reasoning. The point of the gate is to catch regressions before the legacy reader is removed.

### Branch C (optional, after one v0 playtest cycle) — `drop-absolute-shadow`

- Remove `absoluteFrames` shadow data and write path; remove the shadow-fallback branches in `ParsekFlight` and `ProductionAnchorWorldFrameResolver`.
- Defensible only after Phase D + format reset have run through Watch + active Re-Fly + map/KSC for at least one playtest. Until then `absoluteFrames` is the documented debug backstop (§3.9).
- Keep this separate so a single revert restores the backstop if the chain resolver hits an unexpected edge.

### Rollback shape

If Branch B lands and a v0-reset playtest fails:

- **Tag before merging Branch B:** `pre-v0-reset` tag on the parent commit. Public/local recovery is "git reset --hard pre-v0-reset" at worst, or revert the merge commit at best.
- **Single-revert commit cannot fully restore** because Branch B deletes legacy readers: `RecordingStore.LegacyMergeStateMigrationCount` (committed-bool tri-state migration), `RecordingStore.LegacyGloopsGroupName` (group rename migration), `RecordingStore.LegacyPrefix` (log compatibility), the v4-v11 binary write/read ladder in `TrajectorySidecarBinary.cs` (legacy v5-and-older Relative reader path lives here, gated by `RelativeLocalFrameFormatVersion = 6`), the `formatVersion >= 1` text codec gates in `TrajectoryTextSidecarCodec.cs`, the `>= N` gates in `RecordingStore.cs:4074, 4093` / `RecordingSidecarStore.cs:357, 382` / `RelativeAnchorResolver.cs:226, 557` / `FlightRecorder.cs:8418` / `ParsekFlight.cs:18549` / `GhostPlaybackEngine.cs:2378`, and the `RecordingTreeRecordCodec` `PRE_REFLY_ORIGINAL` legacy read tolerance (§3.7). A revert of the Branch B merge is the right shape, not a forward-fix on top of v0.
- Document the tag name and revert recipe in the Branch B PR description.

### Out of scope for this arc

- Phase F promote-to-absolute. Permanently deferred per `ghost-anchor-recording-chain-plan.md` section 9.3.
- Loop-anchored recording rearchitecture. Loops keep `LoopAnchorVesselId` live-vessel anchoring; switching that to recording-id is a separate plan and a separate user decision.
- Sibling-worktree pruning (mentioned at the end of v1 as ~85 worktrees; the count is unverified and is a working-tree hygiene task, not a code-cleanup task — handle separately).

---

## 5. Documentation updates required by these branches

For each branch, the same-commit doc check applies. v2 resolves the v1 contradiction (v1 said `.claude/CLAUDE.md` was out of scope while also requiring its update). Branch B updates the following docs in the same commit set:

- `CHANGELOG.md` — one entry under v0.10.0 describing the user-visible behaviour change. Include a public-history note acknowledging that the version number drops from v0.9.x mod-version-wise to v0.10.0 while the recording format renumbers from v11 to v0; explain the renumbering is a private-development reset and that public consumers should re-record. The CHANGELOG is the only place GitHub watchers see the rationale.
- `docs/dev/todo-and-known-bugs.md` — close out "PR708 post-merge Phase D continuation"; close out the `[PlaybackTrace]` separation jitter observability item once the v0 fixture is captured.
- `.claude/CLAUDE.md` — update the "Recording storage" gotcha block with the v0 reference frame contract; remove the v6/v7/v10 enum constants section. Update the format-version table.
- `AGENTS.md` — has its own "Recording storage (format v3)" gotcha block (per repo convention noted by the external reviewer). Update to v0 with the discriminator contract. The v3 reference is stale even today; this is the right moment to rewrite it.
- `MEMORY.md` — the `project_format_v0_reset.md` memory pointer becomes load-bearing again; reference it from the new entries. Add a memory entry pointing to this plan (`project_post_v0_reset_arc.md`).

The `refly-postmerge-relative-to-absolute.md` plan can be marked superseded. The `pr708-playtest-followup-plan.md` plan's status block is updated to "closed" once Branch A lands.

---

## 6. Decisions for the user

Before Branch A starts, three confirmations needed (D.0.1–D.0.3 in section 1). Plus three plan-level decisions:

1. **Confirm v0 reset with discriminator** (binary magic prefix + `RecordingSchemaGeneration` field) instead of a plain v11 -> v12 bump. Recommendation: yes, with the discriminator to prevent silent reuse of legacy default-0 records.
2. **Confirm `absoluteFrames` shadow disposition**: keep through Branch C playtest cycle, then drop. Recommendation: yes (current plan §3.9).
3. **Confirm version bump to v0.10.0**. Recommendation: yes — the format reset is exactly the kind of change minor-version bumps signal.

Worktree workflow: per the `.claude/CLAUDE.md` HARD RULE, each branch (A, B, C) gets its own dedicated sibling worktree. Not a question; stating the rule applies.
