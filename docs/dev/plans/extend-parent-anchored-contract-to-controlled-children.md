# Extend the parent-anchored contract to controlled-decoupled children

**Status:** plan, not yet implemented.

**Scope:** narrow recorder-side fix that closes the CoBubble debris-anchor snap bug class for controlled-decoupled child vessels (probes, landers, capsules that come off a parent through a decoupler). The fix gives those recordings a parent-anchored Relative surface (`bodyFixedFrames` primary, `frames` secondary) anchored on the focused parent at split time, so playback resolves the child through the existing parent-anchored body-fixed primary path instead of needing CoBubble peer-blend with an opportunistic sibling.

**Out of scope:**
- Re-Fly provisionals (separate Open bug "Re-fly provisional Relative section anchored to fast-separating sibling", separate fix population).
- Renaming `Recording.DebrisParentRecordingId` to `ParentAnchorRecordingId` and renaming the helper / sidecar key. The field is left misnamed in this plan; the sibling superset plan `docs/dev/plans/extend-parent-anchored-contract.md` (on branch `plan-extend-parent-anchored-contract`) carries the rename. See section 3 for coordination.
- Rotation Slerp wraparound (separate bug, independent root cause).
- Cross-tree CoBubble formations that pair a controlled vessel with an unrelated tree's debris. PR #874's Rule 6 stays in place as the safety net for that class.

**Evidence:** `logs/2026-05-16_2010_pr872-kerbalx-ghost-switch/KSP.log`. Re-Fly of Kerbal X upper stage; lower-stage probe `b11ef3d4` (type=Probe, hasController=True) visibly snapped at UT 34.74 onto sibling radial-booster debris `fa429137`'s trajectory and crossfaded back at UT 38.76 when that debris crashed.

---

## 1. Goal

A controlled-decoupled child today records as plain Absolute by `BackgroundRecorder`: no `DebrisParentRecordingId`, no Relative-to-parent surface. Its only formation-coherence mechanism during playback is `CoBubblePrimarySelector`'s peer-blend, which pairs it opportunistically with whichever Absolute neighbor wins the primary selector. PR #874 added Rule 6 ("non-debris-over-debris") to stop sibling debris from winning that race against a controlled peer, but Rule 6 is a tiebreaker on top of an already-wrong premise: the controlled child should not need a CoBubble peer at all when its own physical-identity parent is the natural and stable formation anchor.

This plan extends `Recording.DebrisParentRecordingId` and the dual-surface recording contract (`TrackSection.frames` + `TrackSection.bodyFixedFrames`) to controlled-decoupled children. `Recording.IsDebris` stays `false`. After the fix:

- Coalescer-time controlled-child creation stamps `DebrisParentRecordingId` to the focused parent recording id at split.
- BackgroundRecorder's parent-anchored emission path (already generic with respect to `IsDebris` at the gate level - `BackgroundRecorder.cs:4830` checks only `DebrisParentRecordingId != null`) fires automatically for the new population while the child is within 500m of the parent (with 550m hysteresis exit).
- Playback's parent-anchored body-fixed primary path resolves the child through the existing recorded `bodyFixedFrames` surface rather than needing a CoBubble peer-blend window.
- After the parent-anchored proximity window closes, the recorder closes the Relative section and opens a new Absolute section. The controlled child keeps recording indefinitely. Playback walks `TrackSections` per-section by `referenceFrame` and the post-window Absolute tail plays through the standard Absolute path.

The CoBubble selector Rule 6 from PR #874 stays as a safety net for cross-tree co-bubble formations the new contract does not cover.

## 2. Background

### 2.1 Symptom (from the PR #872 KSP.log)

- Upper stage `92a16d7d` (controlled, Re-Fly focus) records normally.
- Lower-stage probe `b11ef3d4` (controlled, decoupled child of `92a16d7d` at UT 24.26) records as plain Absolute. It carries no `DebrisParentRecordingId` and no Relative section against its parent.
- Sibling radial-booster debris `fa429137` (uncontrolled, decoupled at UT 20.66) records as plain Absolute with a debris-anchor against the upper stage.
- During Re-Fly watch mode, `CoBubblePrimarySelector.SelectPrimaryForPair` evaluates the `(b11ef3d4, fa429137)` pair. Pre-PR #874, Rule 3 (earlier `StartUT`) made `fa429137` (StartUT 20.66) primary over `b11ef3d4` (StartUT 24.26). The probe rode through the debris's anchored playback, then crossfaded back to peer-standalone when the debris crashed at UT 38.76 - visible as a mid-flight trajectory snap and a return snap.
- PR #874's Rule 6 demotes the debris before Rule 3 evaluates, so this specific failure path no longer fires. But the underlying recording shape (controlled child as plain Absolute, no parent-anchor surface) leaves every other CoBubble pairing path with the same race condition; any future co-bubble pair (controlled, controlled) still routes through the opportunistic peer-blend window selector, which can lose against unrelated peers in formations the Rule 6 guard does not address.

### 2.2 Today's data flow at split time (focused-vessel breakup)

`ParsekFlight.ProcessBreakupEvent` (around `ParsekFlight.cs:6320-6498`) processes the BREAKUP BranchPoint emitted by `CrashCoalescer.Tick`. The relevant subset:

| Step | Site | Today's behavior |
|---|---|---|
| Controlled child create | `ParsekFlight.cs:6363-6421` | Iterates `crashCoalescer.LastEmittedControlledChildPids`. For each, calls `CreateBreakupChildRecording(activeTree, breakupBp, pid, childVessel, isDebris: false, "Unknown", ctrlSnap, initialPoint, parentGeneration, parentRecordingId: activeRec.RecordingId)` at `:6399-6401`. |
| Debris child create | `ParsekFlight.cs:6437-6489` | Iterates `crashCoalescer.LastEmittedDebrisPids`. For each, calls `CreateBreakupChildRecording(..., isDebris: true, "Debris", preSnap, breakupChildPoint, parentGeneration, parentRecordingId: activeRec.RecordingId)` at `:6455-6457`. Also calls `backgroundRecorder.QueueDebrisSeedParentAnchorPoint(pid, activeRecId, focusedParentSeedAnchorPoint.Value)` at `:6470-6476` for the structural-event parent-anchor seed. |
| Factory | `ParsekFlight.cs:5965-6067` | `CreateBreakupChildRecording` builds the new `Recording` with `IsDebris = isDebris`, captures `Controllers = ControllerInfo.CaptureFromVessel(vessel)` (live identity at split time), then calls `Recording.ApplyDebrisAnchorContract(childRec, parentRecordingId)` at `:5996`. |
| Contract helper | `Recording.cs:1027-1044` | `ApplyDebrisAnchorContract(child, parent)` has two overloads. Both early-return on `if (!child.IsDebris) return;`. Controlled children fall through this gate and their `DebrisParentRecordingId` stays null. |

The same shape repeats on the background-vessel split path (`BackgroundRecorder.RegisterChildRecordingsFromSplit` at `BackgroundRecorder.cs:1170-1260`), where `child.IsDebris = !hasController` at `:1187` and the contract helper is called at `:1188`. The `hasController == true` branch produces a non-debris child with no `DebrisParentRecordingId`.

### 2.3 Today's playback gate (canonical site)

`GhostPlaybackEngine.TryPositionRelativeSectionAtPlaybackUT` at `GhostPlaybackEngine.cs:2911-2960` is the canonical site that decides whether the parent-anchored body-fixed primary path applies:

```csharp
bool parentAnchoredDebris = traj != null
    && traj.IsDebris
    && !string.IsNullOrWhiteSpace(traj.DebrisParentRecordingId);
```

The dual conjunct `IsDebris && DebrisParentRecordingId != null` repeats in five sibling playback gates (section 5.2). After this plan, the canonical gate widens to `!string.IsNullOrWhiteSpace(traj.DebrisParentRecordingId)` (drop the `IsDebris` conjunct), with `IsDebris`-specific behavior moved to the retirement / TTL surfaces where it semantically belongs.

### 2.4 Today's BackgroundRecorder anchor selector

`BackgroundRecorder.UpdateBackgroundAnchorDetection` at `BackgroundRecorder.cs:4816-4918` already checks ONLY `DebrisParentRecordingId != null` at `:4830` for the parent-anchored bypass:

```csharp
if (!string.IsNullOrEmpty(treeRec.DebrisParentRecordingId))
{
    ApplyDebrisAnchorContractToState(state, treeRec, bgVessel, ut);
    return;
}
```

This means **the BG-side recorder is already generic with respect to `IsDebris`**. Once a controlled child carries a non-null `DebrisParentRecordingId`, the BG-recorder will:

1. Resolve parent distance via `UpdateDebrisProximityState` (`:2039`).
2. Open a Relative `TrackSection` when within `DebrisHalfFidelityProximityRangeMeters = 500m`, exit at `DebrisRelativeSectionExitMeters = 550m` hysteresis.
3. On every BG sample tick inside the Relative section, append both `TrackSection.frames` (anchor-local metre offsets + anchor-local rotation, via `ApplyBackgroundRelativeOffset`) and `TrackSection.bodyFixedFrames` (full `TrajectoryPoint` in body-fixed world coordinates, via `ApplySurfaceClearanceToBodyFixedShadow`) at `AddFrameToActiveTrackSection` (`:6711-6713`).
4. When the proximity window exits (parent crashed, parent went out of range, anchor unresolvable for ≥1 tick), call `ExitBackgroundRelativeMode` (`:5160-5164`) and open a fresh Absolute section that continues sampling normally.

No code changes are needed inside the BG sampling loop to extend coverage to controlled children once the contract is stamped.

### 2.5 What is missing

Two write sites and a handful of read gates:

1. **Stamping site**: `Recording.ApplyDebrisAnchorContract`'s `if (!child.IsDebris) return;` gate at `Recording.cs:1030, 1042` blocks controlled children from getting `DebrisParentRecordingId` set. Both overloads must drop the gate (caller-decides via the parent id argument).
2. **Parent-anchor seed queueing**: `ParsekFlight.cs:6470-6476` queues the structural-event parent-anchor seed (`backgroundRecorder.QueueDebrisSeedParentAnchorPoint`) only inside the debris loop. Controlled children get no seed today; their first BG sample after split has to live-resolve the parent pose. This works (the BG-recorder seed path falls back to live-vessel resolution at `BackgroundRecorder.cs:5189-5207`), but the deterministic split-moment seed is strictly better - fewer first-frame artifacts, no risk of seeding off a stale post-split parent pose.
3. **Playback gates**: five playback / resolver gates use the dual conjunct `IsDebris && DebrisParentRecordingId != null` and would silently exclude controlled-child recordings from the body-fixed primary playback path. See section 5.2 for the full enumeration.
4. **Three implicit proxies**: `RelativeAnchorResolver.IsDebrisFocusRecording` (`RelativeAnchorResolver.cs:170-176`) gates only on `focus.IsDebris`; `GhostPlaybackEngine.cs:235` early-return on `!traj.IsDebris`; `EffectiveState.cs:1434` continue-on `!cand.IsDebris` in the parent-anchored children walk. Each is a proxy for "has a parent-anchor surface" and must switch to checking `DebrisParentRecordingId != null` instead. See section 5.2.

## 3. Coordination with the sibling superset plan

A broader plan covering this work as a subset lives at `docs/dev/plans/extend-parent-anchored-contract.md` on branch `plan-extend-parent-anchored-contract`. That plan:

- Covers controlled-decoupled children **and** Re-Fly provisionals (two populations, not one).
- Renames `Recording.DebrisParentRecordingId` to `ParentAnchorRecordingId` and the ConfigNode key `debrisParentRecordingId` to `parentAnchorRecordingId`.
- Renames `Recording.ApplyDebrisAnchorContract` to `Recording.ApplyParentAnchorContract`.
- Renames `BackgroundRecorder.ApplyDebrisAnchorContractToState` to `ApplyParentAnchorContractToState`.
- Renames `DebrisRelativeRecorderPolicy.cs` and `DebrisRelativePlaybackPolicy.cs` to `ParentAnchor*Policy.cs`.
- Bumps `RecordingStore.CurrentRecordingSchemaGeneration` from 1 to 2 and adds a named constant `ParentAnchorContractSchemaGeneration = 2`.
- Adds a `MaxParentAnchorChainDepth = 16` cycle guard for nested-supersede re-fly chains.
- Patches the active-vessel recorder (`FlightRecorder.UpdateAnchorDetection`) with a parent-anchor bypass - this is the load-bearing site for the Re-Fly provisional half of that plan; not strictly required for the controlled-child half (controlled children go to BG immediately after switch-away).
- Adds a bulk grep-test allowlist `scripts/parent-anchor-proxy-audit-allowlist.txt`.

**Coordination decision (TWO OPTIONS; pick one before implementation starts - flagged for user):**

- **Option A: this plan lands first, sibling plan absorbs it later.** Lower-risk per-PR scope; the controlled-child fix ships independently and the sibling plan's Re-Fly-provisional half lands on top, including the rename pass and schema-generation bump. Pros: small PR, fast cycle, no contention with the rename refactor. Cons: the field stays misnamed for one release cycle; two of the audit fix-sites in this plan (`RelativeAnchorResolver`, `EffectiveState` proxy reads) will be re-touched by the sibling rename pass.
- **Option B: defer this plan and land the sibling superset plan as one PR.** Higher cohesion: rename + format bump + both populations in one ship. Cons: bigger PR (around 30 files touched vs around 10), longer review cycle, blocks the user-visible CoBubble snap fix on the Re-Fly-provisional half being ready.

**Recommendation: Option A.** The PR #872 repro is a visible mid-flight visual seam; landing the controlled-child fix in isolation gets it in front of playtesters now. The rename + Re-Fly half can ride the sibling plan after. This plan is internally consistent with Option A; Option B would mean stopping here and switching to the sibling plan's worktree.

The rest of this document assumes Option A unless the user picks B.

## 4. Design

### 4.1 Coalescer-time stamping

`Recording.ApplyDebrisAnchorContract` becomes caller-decides:

```csharp
internal static void ApplyDebrisAnchorContract(Recording child, Recording parent)
{
    if (child == null) return;
    if (parent == null) return;  // <- replace the `if (!child.IsDebris) return;` guard
    child.DebrisParentRecordingId = parent.RecordingId;
}

internal static void ApplyDebrisAnchorContract(Recording child, string parentRecordingId)
{
    if (child == null) return;
    if (string.IsNullOrEmpty(parentRecordingId)) return;  // <- same swap
    child.DebrisParentRecordingId = parentRecordingId;
}
```

The helper now stamps unconditionally when a parent is supplied. Callers decide applicability by passing or withholding the parent id.

Call sites:

| Site | Today | After |
|---|---|---|
| `ParsekFlight.cs:5996` (`CreateBreakupChildRecording`) | Calls helper unconditionally for both debris and controlled children. Helper internally early-returns on non-debris. | Helper now stamps both. No call-site change. |
| `BackgroundRecorder.cs:1188` (`RegisterChildRecordingsFromSplit`) | Calls helper unconditionally for both branches. Helper internally early-returns on non-debris. | Helper now stamps both. No call-site change. |
| `BackgroundRecorder.cs:1308` (`BuildBackgroundSplitBranchData`, pure-static factory) | Calls helper unconditionally. Helper internally early-returns on non-debris. | Same - no call-site change. |
| Internal contract helpers (debris ledger ownership link, supersede commit, optimizer) | All sites already either propagate the field verbatim (assignment) or read it through the dual-conjunct gate. | No change. |

The "parent" supplied at the focused-vessel breakup site is `activeRec.RecordingId` (the focused recording at the time of breakup), not the literal tree root. This is intentional: a controlled-decoupled child's natural formation anchor is whatever recording it most-recently shared physical-identity with - which is the focused parent at the breakup BP, even when that focused parent is itself a chain continuation or post-breakup mid-air segment. This matches today's debris contract; debris already anchors on `activeRec`, not on `tree.RootRecording`. The plan does not change "parent" semantics; it only widens which child populations get one.

### 4.2 Coalescer-time parent-anchor seed queueing

Today (`ParsekFlight.cs:6470-6476`):

```csharp
if (debrisVessel != null && backgroundRecorder != null)
{
    // ...
    if (focusedParentSeedAnchorPoint.HasValue)
    {
        backgroundRecorder.QueueDebrisSeedParentAnchorPoint(
            pid,
            activeRecId,
            focusedParentSeedAnchorPoint.Value);
    }
    backgroundRecorder.OnVesselBackgrounded(...);
    backgroundRecorder.SetDebrisExpiry(pid, debrisExpiryUT);
}
```

The seed call inside the debris loop has no counterpart inside the controlled-child loop (`ParsekFlight.cs:6403-6411`). Add the same call:

```csharp
if (childVessel != null && backgroundRecorder != null)
{
    activeTree.BackgroundMap[pid] = childRec.RecordingId;
    if (focusedParentSeedAnchorPoint.HasValue)
    {
        backgroundRecorder.QueueDebrisSeedParentAnchorPoint(
            pid,
            activeRecId,
            focusedParentSeedAnchorPoint.Value);
    }
    backgroundRecorder.OnVesselBackgrounded(
        pid,
        breakupEngineState,
        initialTrajectoryPoint: initialPoint);
    // NOTE: no SetDebrisExpiry - controlled children record indefinitely.
}
```

Method name `QueueDebrisSeedParentAnchorPoint` keeps its current name (rename is sibling-plan scope). Add a one-line comment at the new call site documenting that the same seed mechanism applies to controlled children with parent-anchor.

### 4.3 BackgroundRecorder behavior is unchanged at the gates

The two gates that matter are already generic:

- `BackgroundRecorder.cs:4830` - `if (!string.IsNullOrEmpty(treeRec.DebrisParentRecordingId))` - generic.
- `BackgroundRecorder.cs:5151-5152` - `ApplyDebrisAnchorContractToState` parent id read - generic.

So the per-frame Relative-section emission, body-fixed shadow append, hysteresis exit, and Absolute-section reopen all fire automatically for the new population. No code change here.

One caveat to verify in implementation: the diagnostic log line at `BackgroundRecorder.cs:1189-1194` is debris-only (`if (child.IsDebris) ParsekLog.Verbose(...)`). Widen to log both populations with a `population=debris|controlled-child` field. See section 6 logging.

### 4.4 Sample-cap policy decision (debris-only, keep)

`BackgroundRecorder.IsDebrisAwareSampleCapEligible` at `BackgroundRecorder.cs:5893-5898`:

```csharp
internal static bool IsDebrisAwareSampleCapEligible(Recording treeRec)
{
    return treeRec != null
        && treeRec.IsDebris
        && !string.IsNullOrEmpty(treeRec.DebrisParentRecordingId);
}
```

This guards two sampling-rate caps (MIN floor at `:6064`, MAX backstop at `:6097`) sized for short-lived debris orbiting a parent for tens of seconds. Two competing audit verdicts converged on opposite recommendations:

- "Admit controlled children" (so radial-breakup controlled children don't lose fidelity).
- "Reject controlled children" (so long-loitering controlled-children-near-station don't multiply sample storage 6-16x).

**Decision: KEEP the `IsDebris` conjunct.** Controlled children record indefinitely; expanding the cap admits unbounded long-loiter cost, while the current MAX backstop for non-debris is already adequate for ghost playback fidelity (the storage tuning was deliberately sized for the debris use case). The MIN-floor concern (radial-breakup controlled children at FarRange tier) is theoretical for now; if a playtester surfaces that fidelity issue in practice, file a separate follow-up. **Document the dual-conjunct rationale inline** at `BackgroundRecorder.cs:5893` so a future audit pass does not collapse it.

This decision is the same as the sibling plan's verdict. The function name `IsDebrisAwareSampleCapEligible` stays unchanged (rename to `IsDebrisProximitySampleCapEligible` is sibling-plan scope).

### 4.5 Tail-normalize policy decision (debris-only, keep, inline-document)

`DebrisRelativeRecorderPolicy.ShouldNormalizeParentAnchoredDebris` at `DebrisRelativeRecorderPolicy.cs:24-30` gates tail-normalization on `IsDebris && DebrisParentRecordingId != null && LoopAnchorVesselId == 0u`. The normalization truncates a parent-anchored Relative section's tail to its recorder-persistable authored coverage when the recording ends.

For controlled children this is wrong: the Relative section is bounded by the 500/550m proximity window; everything past that window is plain Absolute content that must NOT be tail-truncated. Today the dual-conjunct gate already excludes the controlled-child case because `IsDebris == false`. After this plan, controlled children will have non-null `DebrisParentRecordingId` but still `IsDebris == false`, so the gate continues to exclude them. **Keep the `IsDebris` conjunct; do not collapse it.** Add an inline comment documenting that this normalization is intentionally debris-only because controlled children record post-window Absolute tails.

## 5. Audits

### 5.1 `DebrisParentRecordingId` read sites (after Coalescer-time stamping change)

Findings from a per-site audit of every read of `DebrisParentRecordingId` in `Source/Parsek/` (assignment sites and pure diagnostics excluded):

| Site | Today's behavior | Verdict |
|---|---|---|
| `BackgroundRecorder.cs:1411-1461` | TTL gate inside `RegisterChildRecordingsFromSplit` is keyed by `!string.IsNullOrEmpty(childRec.DebrisParentRecordingId)`. Today this only fires on debris because today only debris carry the field. | **SAFE.** After the plan, controlled children would enter this branch and inherit debris-TTL behavior - but the surrounding loop is the `hasController == false` branch (per the file structure), so the TTL logic does not fire from the controlled-child loop in the first place. Confirm during implementation by re-reading `:1170-1260`. |
| `BackgroundRecorder.cs:3779-3990` (`InitializeLoadedState` debris-seed path) | Already gates on `DebrisParentRecordingId != null`; switches between Relative-seed and Absolute-seed by proximity. | **SAFE.** Already generic. Verify the Relative-seed branch handles the controlled-child case correctly when the live parent is far away (typical for distant decouples): expected behavior is the warn-and-fall-back-to-Absolute-seed path. |
| `BackgroundRecorder.cs:4462, 4486, 4600, 4830, 5151-5152, 5339` | Parent-anchored seed / per-tick anchor logic. | **SAFE.** All already key on `DebrisParentRecordingId != null` directly. No proxy. |
| `BackgroundRecorder.cs:5893-5898` (`IsDebrisAwareSampleCapEligible`) | Dual conjunct `IsDebris && DebrisParentRecordingId != null`. | **KEEP (debris-only).** See section 4.4. Document inline. |
| `DebrisRelativeRecorderPolicy.cs:24-30` (`ShouldNormalizeParentAnchoredDebris`) | Dual conjunct. | **KEEP (debris-only).** See section 4.5. Document inline. |
| `DebrisRelativePlaybackPolicy.cs:60` (`ShouldPlaybackParentAnchoredDebrisRelatively`) | Dual conjunct gating parent-anchored playback decision. | **NEEDS UPDATE.** This is the highest-impact playback gate. Drop the `IsDebris` conjunct to admit controlled children. The playback policy is "if the recording carries a parent-anchor surface, prefer that path"; this applies to both populations. |
| `DebrisRelativePlaybackPolicy.cs:5754-5756` (`ShouldUseLoopAnchoredDebrisChain`) | Dual conjunct gating loop-anchored chain detection. | **NEEDS UPDATE OR KEEP - DECIDE PER REVIEW.** Loop-anchored debris chains are constructed by loop-anchored ancestors (debris of a looped vessel). Today's controlled children almost never participate in loop-anchored chains. Keep `IsDebris` until a follow-up test confirms controlled children inside loop-anchored chains play correctly. Document the gate's intent. |
| `EffectiveState.cs:1434` (parent-anchored children walk) | `if (!cand.IsDebris) continue;` inside the supersede-closure walk. | **NEEDS UPDATE.** The walk's purpose is to enumerate parent-anchored children of a given parent recording. Change to `if (string.IsNullOrEmpty(cand.DebrisParentRecordingId)) continue;` (with `cand.DebrisParentRecordingId == rec.RecordingId` match retained). This is the read site mentioned in the audit-checklist row for `EffectiveState.cs:1394`; same line. |
| `SupersedeCommit.cs:520-525` (`IsPreRewindCarveOut` debris branch) and `:564` (non-debris HEAD branch) | Both keep `IsDebris` as a semantic discriminator. | **KEEP both conjuncts.** Pre-rewind debris carve-out is debris-specific by design; controlled children that happen to be pre-rewind go through the chain-head carve-out (non-debris branch) instead. Document the dual-check rationale inline. Add unit test `SupersedeCommit_ProvisionalWithControlledChildParentAnchor_NotMisCarvedOut`. |
| `GhostPlaybackEngine.cs:2920-2922` (canonical `TryPositionRelativeSectionAtPlaybackUT` gate) | Dual conjunct. | **NEEDS UPDATE.** This is the canonical playback gate. Drop the `IsDebris` conjunct. The variable name `parentAnchoredDebris` should change to `parentAnchored` in the same edit. |
| `GhostPlaybackEngine.cs:3054, 3375, 5755` (sibling gates in same engine) | Dual conjuncts in `ShouldUseLoopAnchoredDebrisChain` cross-call sites. | Follow the same decision as `DebrisRelativePlaybackPolicy.cs:5754-5756` (likely **KEEP** until validated; document inline). |
| `GhostPlaybackEngine.cs:235` (early-return on `!traj.IsDebris`) | Single conjunct, not dual. | **NEEDS UPDATE.** This site is in the parent-anchored playback dispatch chain (per the playback readout). Switch to `if (string.IsNullOrWhiteSpace(traj.DebrisParentRecordingId)) return;`. Verify by reading `:230-245` for surrounding context at implementation time. |
| `GhostPlaybackEngine.cs:4647, 5270` (`traj.IsDebris && GhostVisualBuilder.GetGhostSnapshot(traj) == null`) | Ghost-snapshot existence check; debris are visual-only. | **KEEP.** Controlled children have ghost snapshots; the `IsDebris` here gates on "this is a no-snapshot population." |
| `GhostMapPresence.cs` map-presence resolver | Already does not gate on `IsDebris` for parent-anchored playback (per readout). | **SAFE.** No change. |
| `RelativeAnchorResolver.cs:170-176` (`IsDebrisFocusRecording`) | Single `focus.IsDebris` check, no `DebrisParentRecordingId` paired. | **NEEDS UPDATE - highest-risk omission.** The predicate is semantically "this focus uses a parent-anchor surface", not "this focus is debris specifically." Change to `focus != null && !string.IsNullOrEmpty(focus.DebrisParentRecordingId)`. Rename the predicate to a name that does not mention debris in the sibling-plan rename pass; for this plan keep the function name and add an inline comment noting the rename is deferred to the sibling plan. |
| `Rendering/AnchorPropagator.cs:353` (`&& rChildClassify.IsDebris`) | Gates DockOrMerge ε propagation for child recordings. | **REVIEW - keep until playback-validated.** The audit flagged this as "probable proxy" but the body-fixed playback contract has not been verified end-to-end for controlled children in DockOrMerge events. Add an in-game test `ControlledChild_PlaybackAlignment_WithoutDockOrMergeEps` and a counterpart `_WithDockOrMergeEps`; flip the predicate only if both pass. If unflipped at PR-time, file as a follow-up. |
| `Rendering/CoBubblePrimarySelector.cs:284-285` (Rule 6) | `a.IsDebris && !b.IsDebris` → b wins. | **KEEP (safety net).** Per PR #874. Rule 6 stays as the cross-tree co-bubble safety net. |
| `RecordingOptimizer.cs:127` (`a.IsDebris != b.IsDebris -> false`) | Auto-merge guard. | **KEEP.** Debris and non-debris must never auto-merge regardless of parent-anchor. |
| `RecordingTree.cs:1296, 1305`, `RecordingGroupStore.cs:80, 159, 206, 247, 528, 554`, `RecordingsTableUI.cs:*`, `TimelineWindowUI.cs:*`, `TimelineBuilder.cs:*`, `ParsekUI.cs:1145`, `ParsekTrackingStation.cs:684`, `UnfinishedFlightClassifier.cs:101` | UI / classification / grouping / timeline / tracking-station-marker / unfinished-flight semantics. | **KEEP across the board.** Controlled children get the same UI treatment as today (non-debris row in the table, present in timeline, watch button enabled, etc.). The "Debris" UI group label is intentionally debris-only. |
| `IdentityLossClassifier.cs:35, 109` | Debris opt-out for identity-loss classification. | **KEEP.** Controlled children DO need identity-loss classification (per the v0.9.2 BG-tracked vessel mis-classified bug fix); `IsDebris == false` semantics are correct here. |
| `ParsekPlaybackPolicy.cs:982, 984, 1120` | `evt.Trajectory.IsDebris` early-return (`:982`) and `|| rec.IsDebris` gate (`:1120`). | **NEEDS UPDATE (probable proxy).** Re-read each site at implementation time. `:982` early-return is for the policy event chain; `:1120` is in spawn eligibility. Both likely need to admit controlled children. Per-site decision goes in the audit allowlist. |
| `RecordingStore.cs:4820` (`!rec.IsDebris || string.IsNullOrEmpty(rec.TreeId)`) | Loop-sync parent linking. | **NEEDS UPDATE (probable proxy).** Re-read at implementation time. If switching to parent-anchor field changes UI behavior, defer; otherwise flip. |

**Summary:**
- **NEEDS UPDATE (must change for this fix to work):** 5 sites - `DebrisRelativePlaybackPolicy.cs:60` (playback policy), `GhostPlaybackEngine.cs:2920-2922` (canonical playback gate), `GhostPlaybackEngine.cs:235` (early-return), `RelativeAnchorResolver.cs:170-176` (focus predicate), `EffectiveState.cs:1434` (children walk).
- **NEEDS UPDATE (probable proxy, validate per-site):** 3 sites - `ParsekPlaybackPolicy.cs:982-1120` (2 reads), `RecordingStore.cs:4820`.
- **REVIEW (validate via playback test before flipping):** 1 site - `AnchorPropagator.cs:353`.
- **KEEP `IsDebris` (semantic):** all other sites.

Estimated diff churn: 8-10 source-file touches, 50-100 lines of net change across logic + comments. The change is small enough to fit a single PR without a rename refactor.

**If the proxy audit at implementation time finds the scope is larger than this (e.g. a per-site decision uncovers cascading behavior changes), STOP and ASK** per the prompt's escalation gate.

### 5.2 `IsDebris` read-site audit checklist (Phase 3 work)

Mechanical scope: every read of `IsDebris` in `Source/Parsek/` (excluding tests). Verdict bins above. The full enumeration lives in this plan's section 5.1 table consolidated with the cross-cutting `IsDebris`-only sites:

- 35 SAFE sites (debris-specific lifecycle, UI grouping, serialization).
- 11 NEEDS UPDATE sites (gate on `DebrisParentRecordingId != null` or check both).
- 1 REVIEW site (AnchorPropagator DAG propagation).

The implementer re-derives this list at Phase 3 start (the source has likely drifted by tens of lines since the audit ran). The file-and-function pairs are stable.

Allowlist file: `scripts/parent-anchor-proxy-audit-allowlist.txt`. CI gate `scripts/grep-audit-parent-anchor-proxy.ps1` is sibling-plan scope (out of scope here unless the cleanup turns out to need it; the small NEEDS UPDATE count argues against the CI gate landing in this PR).

## 6. Format-version concerns - DECISION GATE FOR USER

**Question:** does this contract widening require bumping `RecordingStore.CurrentRecordingSchemaGeneration` from 1 to 2?

**Today's invariant on disk:** `DebrisParentRecordingId != null ⇒ IsDebris == true` is enforced by `Recording.ApplyDebrisAnchorContract`'s early-return at `Recording.cs:1030, 1042`.

**After this plan's change:** `DebrisParentRecordingId != null` becomes valid for both `IsDebris == true` (genuine debris) and `IsDebris == false` (controlled children). The on-disk shape's truth table widens by one row (per the sibling plan's section 4.5 table):

| `IsDebris` | `DebrisParentRecordingId` | Today | After |
|---|---|---|---|
| true | non-null | Valid (genuine debris) | Valid (unchanged) |
| true | null | Valid (rare, orphan debris) | Valid (unchanged) |
| false | non-null | **Unreachable** | **Newly valid** (controlled child) |
| false | null | Valid (ordinary recording) | Valid (unchanged) |

**Consequences if NOT bumped:**
- A player who downgrades to a pre-fix mod version while their save has new controlled-child recordings: the older mod sees `IsDebris=false` and `DebrisParentRecordingId=non-null`, takes the non-debris code path, loses the parent-anchored playback (silent rendering regression - the bug we just fixed comes back for those specific recordings). No crash, no data loss.
- A `.pann` sidecar (`PannotationsSidecarBinary.cs`) authored by the new code: its `ConfigurationHash` would invalidate cleanly on first load, so no stale-pannotations corruption risk. Confirmed by reading the sibling plan's section 4.3 verification list.
- `.prec` binary trajectory sidecar (`TrajectorySidecarBinary.cs`): the dual-surface parallel-list invariant ("`bodyFixedFrames` populated only when section is Relative") still holds. New controlled-child Relative sections write the same dual-surface shape. **No wire-format change.** Confirmed.

**Consequences if bumped:**
- All pre-fix recordings reject on load with reason `"generation-older"` via `RecordingStore.IsRecordingSchemaCompatible` at `RecordingStore.cs:150-156`. Consistent with project policy "no backwards compatibility for old recordings" (`memory/feedback_no_recording_compat.md`).
- One CHANGELOG line: "Recordings authored before v0.10.0 are no longer loaded; export-replay them in the prior version first if needed." (Identical to prior format-version-bump CHANGELOG entries.)
- The named constant `ControlledChildParentAnchorSchemaGeneration = 2` (or whichever name) lives in `RecordingStore.cs` next to `CurrentRecordingFormatVersion = 1` and `CurrentRecordingSchemaGeneration` (today `= 1`). New behavior in the recorder gates on the named constant per the project convention.
- Three test files have hardcoded literals that flip at the bump and need updating: `FormatVersionTests.cs:36, 91, 288` and `FormatRoundtripTests.cs:45` (re-derive these at Phase 6 start; sibling plan section 4.3 has the current locations).

**My recommendation: BUMP.** Rationale:
- The downgrade case is real (some players keep multiple mod versions side by side for save-compat reasons).
- "No backcompat" policy applies cleanly; this is exactly the case the policy was written for.
- Cost is small (3-4 file touches plus one CHANGELOG line).
- The sibling superset plan bumps anyway; landing this plan with a bump that the sibling plan can re-use the named constant for is a coordination win.

**STOP AND ASK the user before implementing:**
1. Approve format-version bump in this PR, OR
2. Defer the bump to the sibling plan and accept the downgrade-rendering-regression risk for this release cycle.

If the user picks "defer," the named constant addition and the schema-generation literal swap are dropped from this plan; everything else remains identical.

## 7. Playback path verification

Per the playback-readout agent's findings, the parent-anchored body-fixed primary path at `ParsekFlight.TryPositionFromBodyFixedPrimary` (`ParsekFlight.cs:19373-19685`) is already generic with respect to `IsDebris` once invoked. The blockers are upstream:

- The canonical dispatch gate at `GhostPlaybackEngine.cs:2920-2922` (the dual conjunct in `TryPositionRelativeSectionAtPlaybackUT`). Drop the `IsDebris` conjunct.
- The early-return at `GhostPlaybackEngine.cs:235`. Switch to `DebrisParentRecordingId != null` check.
- `RelativeAnchorResolver.IsDebrisFocusRecording` at `:170-176`. Switch to `DebrisParentRecordingId != null` check.

Once these three (plus the two sibling sites at `DebrisRelativePlaybackPolicy.cs:60` and `EffectiveState.cs:1434`) flip, a controlled child carrying a Relative section with both `frames` and `bodyFixedFrames` resolves through the same path that genuine debris already takes today.

**Coverage end-of-window behavior:** when the recorder closes the Relative section at the 550m hysteresis exit and opens a fresh Absolute section, playback walks `TrackSections` per-section by `referenceFrame`. The post-window Absolute section plays through the standard Absolute path (no parent-anchored machinery involved). The retirement helper `ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage` (`GhostPlaybackEngine.cs:2862-2882`) only fires when playback UT is inside a Relative section but the section's authored coverage doesn't cover that UT - for controlled children this should be rare (recorder authored both surfaces continuously while in proximity), but verify with an in-game test that the retirement does NOT fire when playback UT is in the post-window Absolute section.

**Trace seam for the in-game test assertion:** `[Parsek][VERBOSE][PlaybackTrace] ParentAnchored hit: recId=... parentRecId=... section=...` already fires from `GhostPlaybackEngine.cs:19430-19437` (per audit). The in-game test asserts this trace fires for the controlled child during re-fly, naming the parent recording id, and that no `large-delta` or `cobubble-blend-window` trace lines fire for the same recording.

## 8. CoBubble Rule 6 retention

`CoBubblePrimarySelector.cs:284-285` Rule 6 (`a.IsDebris && !b.IsDebris -> b wins`) stays as the safety net for cross-tree co-bubble formations the new contract does not cover:

- Controlled child A in tree T1 meets debris piece B from tree T2 in the same physics bubble. T1 and T2 are unrelated; neither A nor B carries the other as a parent-anchor. CoBubble peer-blend may still pair them. Rule 6 demotes B before Rule 3 fires, keeping A as primary.
- The new parent-anchored contract for A is intra-tree only (anchor = A's own parent recording in T1). Cross-tree formations still rely on the selector.

This plan does NOT touch Rule 6.

## 9. Test plan

### 9.1 xUnit fixtures (`Source/Parsek.Tests/`)

New test class `ControlledChildParentAnchorTests` in `Source/Parsek.Tests/`. Each test under `[Collection("Sequential")]` (touches shared `ParsekLog` state).

| Test | Asserts |
|---|---|
| `ApplyDebrisAnchorContract_ControlledChildWithParent_StampsField` | New `Recording { IsDebris = false }` plus a parent Recording -> `DebrisParentRecordingId == parent.RecordingId` after the helper call. |
| `ApplyDebrisAnchorContract_NullParent_NoStamp` | Helper guard fires correctly; `DebrisParentRecordingId` stays null. |
| `CreateBreakupChildRecording_ControlledBranch_StampsParentAnchor` | Build a synthetic `RecordingTree` + `BranchPoint`; invoke `CreateBreakupChildRecording(..., isDebris: false, ..., parentRecordingId: parent.RecordingId)`; assert the returned `childRec.DebrisParentRecordingId == parent.RecordingId` AND `childRec.IsDebris == false`. |
| `CreateBreakupChildRecording_DebrisBranch_UnchangedShape` | Regression: debris branch still produces `IsDebris == true && DebrisParentRecordingId == parent.RecordingId`. |
| `BackgroundRecorder_ParentAnchorBypass_FiresForControlledChild` | Synthesize a controlled-child recording with non-null `DebrisParentRecordingId` and feed it through `UpdateBackgroundAnchorDetection` (or a testable surrogate). Assert the parent-anchored branch executes and `state.currentTrackSection.referenceFrame == ReferenceFrame.Relative`. (May need an internal test seam if the BG recorder's anchor detection is currently too coupled to a live `Vessel` for unit-testing.) |
| `IsDebrisAwareSampleCapEligible_ControlledChild_ReturnsFalse` | Verifies the sample-cap policy decision in section 4.4: a controlled child does NOT get the debris-aware sample cap. |
| `ShouldNormalizeParentAnchoredDebris_ControlledChild_ReturnsFalse` | Verifies the tail-normalize policy decision in section 4.5: a controlled child is not tail-normalized. |
| `ApplyDebrisAnchorContract_AlreadyStamped_DoesNotOverwrite` | Helper is idempotent: a second call with a different parent does not change the existing value. (If today's helper does overwrite, leave it as-is and add a comment; behavior is unobservable in practice.) |

New test class `ParentAnchorPlaybackGateTests`. Asserts the five "NEEDS UPDATE" playback gates handle the controlled-child case:

| Test | Asserts |
|---|---|
| `DebrisRelativePlaybackPolicy_ShouldPlayback_ControlledChild_ReturnsTrue` | Drop `IsDebris` conjunct verified. |
| `GhostPlaybackEngine_TryPositionRelativeSection_ControlledChild_TakesParentAnchoredPath` | Canonical gate in section 2.3 drops `IsDebris`. Mock `IPlaybackTrajectory` with `IsDebris=false, DebrisParentRecordingId=non-null`, assert the parent-anchored branch is entered. |
| `RelativeAnchorResolver_IsParentAnchoredFocus_ControlledChild_ReturnsTrue` | The function still named `IsDebrisFocusRecording` until the sibling rename; assert behavior. |
| `EffectiveState_ParentAnchoredChildrenWalk_AdmitsControlledChild` | Synthesize a parent recording with one debris child and one controlled child; assert the walk visits both. |
| `GhostPlaybackEngine_EarlyReturn_NonDebrisWithParentAnchor_ProceedsToPlayback` | The early-return at `:235` no longer fires for controlled children. |

Log-assertion tests per `RewindLoggingTests.cs` pattern: `ParentAnchorContractLoggingTests` asserts the new `population=controlled-child` field appears in the log line emitted at `BackgroundRecorder.cs:1189-1194` (widened in section 4.3).

### 9.2 In-game test (`Source/Parsek/InGameTests/RuntimeTests.cs`)

New test under category `"ParentAnchorContract"`, scene `GameScenes.FLIGHT`:

`ControlledDecoupledChild_PlaysViaParentAnchoredPath_NotCoBubbleBlend`:
- Setup: build a small multi-stage vessel with a probe-cored payload (Mk1 lander, probeCoreCube), launch, ascend, decouple the upper stage while staying within 250m of the parent stage.
- Phase 1 (recording): assert the new child recording exists, `IsDebris == false`, `DebrisParentRecordingId == parent.RecordingId`, first `TrackSection` has `referenceFrame == ReferenceFrame.Relative`, the section's `bodyFixedFrames` has ≥2 samples, the section's `anchorRecordingId == parent.RecordingId`.
- Phase 2 (force background): switch focus to a third vessel so the controlled child goes background; continue recording for 5s; assert the section closes at exit hysteresis and a follow-up Absolute section opens.
- Phase 3 (re-fly + playback assertion): trigger Re-Fly on the parent stage, watch the ghost during playback, assert the canonical playback log line indicates the parent-anchored path: trace pattern `[PlaybackTrace] ParentAnchored hit: recId=<controlled-child-id> parentRecId=<parent-id>` fires; no `[CoBubble] blend-window` trace line fires for the controlled child; no `large-delta` trace fires for the controlled child.

This is the canonical acceptance assertion per the user prompt: "the path log line indicates the parent recording id, not a co-bubble blend window."

If the in-game test runner cannot stand up a Re-Fly scenario within a test (KSP state management, save corruption risk), fall back to a scripted xUnit fixture: pre-build a tree with two recordings (parent + controlled child with the new contract), invoke the playback dispatch directly, assert the same trace pattern. The xUnit version is sufficient for CI; the in-game version is the manual smoke-pass deliverable.

### 9.3 PR #872 repro acceptance (manual)

Manual playtest, documented in `docs/dev/manual-testing/extend-parent-anchored-contract-to-controlled-children.md` (new file):

1. Build Kerbal X (or equivalent multi-stage rocket with a controlled probe payload as lower-stage core).
2. Launch, ascend, stage the upper stage at the configuration from the PR #872 log (UT 24.26 decouple).
3. Trigger Re-Fly from the upper stage's recording.
4. Watch the lower-stage probe ghost during playback from UT 30 to UT 50.
5. Acceptance: the probe ghost plays smoothly with no mid-flight trajectory snap, regardless of whether sibling radial-booster debris pieces survive or crash during the playback window.

### 9.4 Full suite acceptance bar

- `dotnet test` green. Today's baseline is **12119 passing** per the user prompt; new fixtures plus the playback gate flips should net to 12130+ green with zero failures.
- `dotnet test --filter InjectAllRecordings` green. The 8 synthetic recordings still load. (No new synthetic recordings added in this plan; the sibling superset plan adds them.)

## 10. Phases

Each phase ships as a separate commit on the implementation branch `controlled-child-parent-anchored`.

### Phase 1 (this prompt): Plan

This document. Lands on `plan-controlled-child-parent-anchored` branch in worktree `Parsek-plan-controlled-child-parent-anchored`. **STOP AND ASK before any implementation work begins**, per the user prompt.

### Phase 2: Coalescer-time stamping + parent-anchor seed (one commit)

- `Recording.ApplyDebrisAnchorContract`: drop the `!child.IsDebris` early-return in both overloads.
- `ParsekFlight.ProcessBreakupEvent` controlled-child loop (`:6403-6411`): add the `QueueDebrisSeedParentAnchorPoint` call mirroring the debris branch.
- `BackgroundRecorder.RegisterChildRecordingsFromSplit` and `BuildBackgroundSplitBranchData`: no call-site change (helper now stamps unconditionally).
- Widen the diagnostic log at `BackgroundRecorder.cs:1189-1194` to log both populations.
- xUnit: `ApplyDebrisAnchorContract_ControlledChildWithParent_StampsField`, `CreateBreakupChildRecording_ControlledBranch_StampsParentAnchor`, `CreateBreakupChildRecording_DebrisBranch_UnchangedShape`.
- Logging-assertion test for the widened diagnostic line.

**Acceptance:** a focused-vessel breakup with one controlled child + one debris child produces two recordings whose `DebrisParentRecordingId == parent.RecordingId`. Debris regression test passes.

### Phase 3: `IsDebris` proxy audit + cleanup (one commit)

Touch the 5 NEEDS UPDATE sites enumerated in section 5.1 + the 3 PROBABLE PROXY sites (after per-site re-read at implementation time):

- `DebrisRelativePlaybackPolicy.cs:60`
- `GhostPlaybackEngine.cs:2920-2922` (rename local `parentAnchoredDebris` to `parentAnchored` in the same edit)
- `GhostPlaybackEngine.cs:235`
- `RelativeAnchorResolver.cs:170-176`
- `EffectiveState.cs:1434`
- `ParsekPlaybackPolicy.cs:982, 984, 1120` (3 reads, per-site verdict at implementation time)
- `RecordingStore.cs:4820` (per-site verdict)

Document each KEEP `IsDebris` site (5.1 table's KEEP rows) with a one-line inline comment naming the semantic rationale.

xUnit: `ParentAnchorPlaybackGateTests` (5 tests above).

**Acceptance:** a controlled child with non-null `DebrisParentRecordingId` reaches the parent-anchored body-fixed primary playback path during playback. PR #872 repro shows no mid-flight snap during a focused playtest.

**STOP AND ASK escalation gate:** if the per-site audit at implementation time uncovers >3 additional NEEDS UPDATE sites not in section 5.1, OR a flip touches behavior beyond playback (e.g. save/load, UI grouping, classifier), STOP and report the expanded scope.

### Phase 4: BackgroundRecorder dual-surface emission verification (one commit)

The BG recorder already fires the dual-surface emission once the contract is stamped (section 4.3); this phase is mostly observation and test coverage:

- xUnit: `BackgroundRecorder_ParentAnchorBypass_FiresForControlledChild` (the test may need a new internal test seam if the BG recorder's anchor detection is too coupled to live `Vessel` for unit-testing).
- xUnit: `IsDebrisAwareSampleCapEligible_ControlledChild_ReturnsFalse`, `ShouldNormalizeParentAnchoredDebris_ControlledChild_ReturnsFalse` (sample-cap and tail-normalize policy decisions in sections 4.4-4.5).
- Inline comments at `BackgroundRecorder.cs:5893` and `DebrisRelativeRecorderPolicy.cs:24` documenting the dual-conjunct rationale.

**Acceptance:** the controlled child opens a Relative section while within 500m of the parent, closes at 550m hysteresis, and opens a fresh Absolute section that continues sampling. Verified via xUnit + manual playtest log inspection.

### Phase 5: Playback verification + in-game test (one commit)

- New in-game test `ControlledDecoupledChild_PlaysViaParentAnchoredPath_NotCoBubbleBlend` per section 9.2.
- Manual smoke-pass document `docs/dev/manual-testing/extend-parent-anchored-contract-to-controlled-children.md` per section 9.3.
- Verify the trace seam in section 7 fires correctly during playback.

**Acceptance:** in-game test passes (or the xUnit fallback fixture passes); manual smoke shows no mid-flight snap; trace log line indicates parent-anchored path.

### Phase 6: Format-version bump (one commit, USER-GATED)

Conditional on user-approved option from section 6.

If user picks bump:
- Add `RecordingStore.ControlledChildParentAnchorSchemaGeneration = 2` named constant.
- Bump `RecordingStore.CurrentRecordingSchemaGeneration` 1 -> 2.
- Update `FormatVersionTests.cs:36, 91, 288` and `FormatRoundtripTests.cs:45` hardcoded literals.
- Add `IsRecordingSchemaCompatible_LegacyGenerationRejected` regression test.
- CHANGELOG line under v0.10.0 noting pre-bump recordings are rejected on load.

If user picks defer:
- Skip this phase. The sibling superset plan absorbs the bump.

### Phase 7: Documentation + CHANGELOG (one commit)

- `.claude/CLAUDE.md`: update the "Parent-anchored debris contract" section to note the contract now covers two populations (genuine debris and controlled-decoupled children). No rename (rename is sibling-plan scope).
- `CHANGELOG.md`: one line under v0.10.0 per `memory/feedback_changelog_style.md`. Suggested wording: "Controlled-decoupled child vessels now anchor on their tree parent during ghost playback, fixing the mid-flight trajectory snap when a sibling debris piece crashed."
- `docs/dev/todo-and-known-bugs.md`: mark the Open entry "Controlled-decoupled child vessels lack a parent-anchored recording surface" as CLOSED with a Fix paragraph and date. Leave the related "Re-fly provisional Relative section anchored to fast-separating sibling" and "Rotation Slerp wraparound" entries unchanged.

**Per-commit doc updates** (per `.claude/CLAUDE.md` "Documentation Updates - Per Commit, Not Per PR"): if the CHANGELOG or todo wording changes during Phase 3 / Phase 5, update those doc entries in the same commit that changes the approach.

## 11. Risks, decisions, and stop-and-ask gates

### 11.1 Open decisions for the user (stop and ask BEFORE Phase 2)

1. **Plan ordering (section 3): Option A or Option B?** A = this plan ships first; B = absorb into sibling superset plan.
2. **Format-version bump (section 6): bump or defer?** Bump in this PR or defer to sibling plan.

### 11.2 Risks identified

- **`AnchorPropagator.cs:353` REVIEW verdict (section 5.1).** If the controlled-child playback alignment test (`ControlledChild_PlaybackAlignment_WithoutDockOrMergeEps`) reveals that DockOrMerge ε propagation needs to extend to controlled children, the Phase 3 fix scope grows by one site. Document the outcome of the playback test in the PR description.
- **In-game test framework limitations.** Re-Fly is hard to drive from `RuntimeTests.cs` because the rewind machinery is scenario-state-coupled. The xUnit fallback fixture in section 9.2 is the safety net; if both prove flaky, the manual smoke pass in section 9.3 is the final acceptance.
- **Pannotations sidecar interaction.** The `.pann` `ConfigurationHash` should invalidate cleanly on the recorder behavior change (the hash already keys on every input that influences anchor decisions); verify by reading `PannotationsSidecarBinary.cs` `ConfigurationHash` inputs at Phase 4 start. If a stale pannotations sidecar is observed after the fix lands, file as a follow-up - not a Phase blocker.
- **Re-Fly cross-talk.** A Re-Fly provisional whose vessel happens to be a controlled-decoupled child of a prior recording (e.g. re-fly the lower stage probe specifically) would carry `DebrisParentRecordingId` set by the new contract AND `SupersedeTargetId` set by the rewind invoker. The current plan does not touch the Re-Fly path, but a sanity-check test (`ReFlyOfControlledChild_DoesNotBreakSupersedeContract`) is worth adding to Phase 3 to confirm the two edges don't conflict.

### 11.3 Stop-and-ask gates during implementation

- **Phase 3 audit scope expansion.** If the per-site re-read uncovers >3 additional NEEDS UPDATE sites, stop and report scope.
- **Phase 5 in-game test flakiness.** If the in-game test can't be made deterministic within 1 day of effort, fall back to the xUnit fixture and document why.
- **Any xUnit regression that cannot be attributed to the plan's changes.** Stop and ask before proceeding.

### 11.4 Out-of-scope follow-ups (not done in this plan)

- Field/helper/file rename `DebrisParentRecordingId` -> `ParentAnchorRecordingId`.
- Re-Fly provisional parent-anchor edge (sibling-plan scope).
- `MaxParentAnchorChainDepth = 16` cycle guard (sibling-plan scope; would be needed if Re-Fly provisional landed).
- Active-vessel `FlightRecorder.UpdateAnchorDetection` parent-anchor bypass (sibling-plan scope; not strictly needed for controlled children because they go to BG immediately).
- Bulk grep CI gate `scripts/grep-audit-parent-anchor-proxy.ps1` (sibling-plan scope; small NEEDS UPDATE count argues against it landing in this PR).
- DockOrMerge ε propagation extension to controlled children (filed as follow-up after Phase 3 playback test outcome).
- CoBubble Rule 6 extension to `(parent-anchored, non-parent-anchored)` discriminator (sibling-plan flagged this for after-contract follow-up).

## 12. Acceptance bar (final)

Per the user prompt:

- Re-running the PR #872 repro shows the Kerbal X Probe ghost playing smoothly end-to-end with no mid-flight trajectory snap, regardless of whether nearby sibling debris pieces survive or crash during the playback window.
- In-game test or scripted xUnit fixture asserts the child plays via the parent-anchored path during re-fly (the path log line indicates the parent recording id, not a co-bubble blend window).
- Full xUnit suite stays green. Today's baseline is 12119 passing; expected net is 12130+ after the new fixtures + playback gate flips.

## 13. Worktree layout for implementation

- Plan branch: `plan-controlled-child-parent-anchored` in worktree `Parsek-plan-controlled-child-parent-anchored` (this branch, this worktree).
- Implementation branch: `controlled-child-parent-anchored` in worktree `Parsek-controlled-child-parent-anchored`, branched from `origin/main` at implementation-start time.
- Once the implementation worktree is created, this plan worktree is reference-only - never edit code in it.
- The plan branch does not get pushed unless the user explicitly asks. Per the user prompt: "Before pushing the plan branch or opening any PR" is a stop-and-ask gate.
