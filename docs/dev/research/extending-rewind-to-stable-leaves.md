# Research: Extending Unfinished Flights to Stable, Unconcluded Leaves

*Investigation doc, started 2026-04-27, R13 on 2026-04-28. Per the dev workflow, this lives in step 1-2 territory: vision + scenario simulation. R3 closed all clarifications with the user; R4 incorporated an internal opus review pass; R5-R12 incorporated eight external review passes; R13 closes a ninth pass with one P1 (R12's `ComputeSubtreeClosure(rootRecordingId)` was root-only and would have lost PID-peer expansion gating that depends on `marker.InvokedUT`; R13 changes the helper to take both marker context AND a root override) and a P2 (the §9.2.1 step 2 pseudocode block still showed an unconditional `provisional.SupersedeTargetId = priorTip` assignment despite step 4 saying it must be guarded; R13 inlines the null-check). The next step is to promote this to a formal design doc.*

*Reads against: `parsek-rewind-to-separation-design.md` (the v0.9 source of truth), `parsek-recording-finalization-design.md`, `parsek-flight-recorder-design.md`, `parsek-timeline-design.md`. Code spot-checks against `EffectiveState.cs`, `TerminalKindClassifier.cs`, `RecordingStore.cs`, `RewindPointReaper.cs`, `BranchPoint.cs`, `Recording.cs`, `RecordingOptimizer.cs`.*

---

## 0. Revision history

- **R1.** Proposed a separate "Parked Flights" virtual UI group alongside Unfinished Flights, with a meaningful-action filter (A1 body change / A2 mid-chain surface / A4 orbit shift) on stable leaves.
- **R2.** Per user feedback: one Unfinished Flights group, broadened to include "stable leaves not finished on purpose." Multi-recording chain handling. Voluntary-vs-involuntary detection via A1+A2 in v1, A4 deferred to v1.1. Eccentric-orbit optimizer concern flagged as separate investigation.
- **R3.** All R2 clarifications closed. The A1/A2/A4 voluntary-action heuristics are dropped entirely. The classifier is now purely terminal-state-based with per-row Seal override. UI: the Rewind column splits into Fly + Seal buttons. Filing the Park-from-not-UF affordance (the rover-drive override) as v2 future work to keep v1 scope tight.
- **R4.** Internal opus review pass. Code anchors fixed across §3: `Recording.IsDebris` ([Recording.cs:25](../../../Source/Parsek/Recording.cs)) for the controllable-subject gate (replacing the earlier hand-wavy "had a working ControllerInfo" prose); `Recording.EvaCrewName` ([Recording.cs:142](../../../Source/Parsek/Recording.cs)) for the EVA exception; `BallisticExtrapolator.cs:223` + `IncompleteBallisticSceneExitFinalizer.cs:464,489` for the atmospheric-SubOrbital reclassification footnote; explicit null-terminal handling. §9.2 now also extends `SupersedeCommit.FlipMergeStateAndClearTransient` so re-fly merges that end Orbiting / SubOrbital / EVA-stranded produce `CommittedProvisional` -- without this, §S8's chain-extension claim is false. Added §S19b (inverted: upper stage is the leaf). §9.2 legacy-migration guard. §7 split-cell UI layout reframed as an unresolved design question. §10 risk #2 cleaned up.
- **R5.** External review pass landed three P1s; all three resolved by adding two persistent fields and rewriting the predicate. Specifically: (1) the R4 narrowing of `MergeState == CommittedProvisional` would have dropped legacy Immutable crashed UF rows; **R5 reverts to v0.9's `MergeState in {Immutable, CommittedProvisional}` and introduces `ChildSlot.Sealed`** as the dedicated close signal, decoupled from `MergeState`; (2) the R4 leaf gate `chainTip.ChildBranchPointId == null` regressed the v0.9 breakup-survivor active-parent path (which links to its slot RP via `ChildBranchPointId`); **R5 replaces it with a per-RP-context check that allows `chainTip.ChildBranchPointId == matchingRP.BranchPointId`**; (3) the R4 outcome gate would have promoted every routine focus-continuation upper stage to UF, blocking RP reap on every nominal launch; **R5 introduces `RewindPoint.FocusSlotIndex` and gates stable-terminal qualification on `slot.SlotIndex != FocusSlotIndex`**. Crashed terminals still qualify regardless of focus. Scenarios re-verdicted (S1, S3, S5, S19, S19b updated; new §S22 for breakup-survivor case).
- **R6.** Follow-up external review caught two internal-consistency P1s: (a) §4's `HasDownstreamBP` standalone-leaf code block still used the R4 gate, which would re-introduce the breakup-survivor regression that R5 fixed in §3; (b) §9.2 Site A pseudocode still wrote `chainTip.ChildBranchPointId != null -> reject` and called `TerminalOutcomeQualifies(chainTip)` without the slot/RP context, which would re-introduce both P1.2 and P1.3 failure modes. R6 rewrites both: §4 now defines `HasDownstreamBP(rec, matchedRP)` requiring per-RP context. §9.2 Site A and Site B both call the new `UnfinishedFlightClassifier.Qualifies(rec, slot, rp, considerSealed)` shared helper.
- **R7.** Third external review pass landed two more P1s + a P2: (a) **P1.C** -- Site B's slot resolution matched on `slot.EffectiveRecordingId(supersedes) == marker.OriginChildRecordingId`, which fails on chain extension. R7 rewrites Site B to use `TryResolveRewindPointForRecording(provisional, ...)`. (b) **P1.D** -- the R6 broad legacy guard would have blocked retroactive surfacing; R7 drops it and replaces with a narrower `RP.FocusSlotIndex == -1` short-circuit inside `TerminalOutcomeQualifies`. The retroactive-surfacing claim flips to forward-only. (c) **P2** -- §2's overview MUST statement still said leaf detection requires `ChildBranchPointId == null`, contradicting §3.
- **R8.** Fourth external review pass landed two more P1s + a P2: (a) **P1.E** -- R7's Site B explanation claimed the helper was topology-agnostic; R8 corrected it to match the actual v0.9 star-graph semantic. (b) **P1.F** -- the EVA branch returns BEFORE the FocusSlotIndex short-circuit, so legacy live-RP stranded EVA kerbals retroactively surface; R8 made this an explicit intentional carve-out and split the CHANGELOG note into vessels-forward-only / kerbals-retroactive. (c) **P2** -- §10's stale "every slot treated as non-focus" risk text rewritten to match R7's short-circuit.
- **R9.** Fifth external review pass landed one P1 + a P2: (a) **P1.G** -- the helper-based Site B fix doesn't work under v0.9's star-shaped supersede graph; R9 escalated to a prerequisite invocation change. (b) **P2** -- legacy-test bullet contradicted the EVA carve-out; rewritten.
- **R10.** Sixth external review pass landed two P1s on the R9 §9.2.1 prerequisite: (a) **P1.H** -- R9 edited the wrong write site (BuildProvisionalRecording vs AtomicMarkerWrite); R10 moved the change. (b) **P1.I** -- R9 mutated `marker.OriginChildRecordingId` and would have broken every existing consumer; R10 introduced a new `marker.SupersedeTargetId` field instead.
- **R11.** Seventh external review pass landed one P1 + a P2: (a) **P1.J** -- R10 said `AppendRelations` should bypass the closure; R11 re-rooted it instead. (b) **P2** -- "omitted when null" contract conflict with unconditional write recipe; R11 picked the unconditional contract.
- **R12.** Eighth external review pass landed one P1 + two P2s: (a) **P1.K** -- R11 framed closure re-rooting as a one-line `AppendRelations` change, but the helper is shared with runtime suppression; R12 split into a separate root-aware helper. (b) **P2.L** -- stale "v0.9 parity exactly" wording contradicted the EVA carve-out; R12 narrowed it. (c) **P2.M** -- R10 framed the in-place continuation as outside `AtomicMarkerWrite`; R12 corrected with a precise within-method recipe.
- **R13 (this version).** Ninth external review pass landed one P1 + a P2: (a) **P1.M** -- R12's `ComputeSubtreeClosure(rootRecordingId)` was a root-only signature, but the existing closure algorithm passes the marker into `EnqueuePidPeerSiblings`, which uses `marker.InvokedUT` to include only post-rewind same-PID peers and exclude prior same-vessel history. A root-only helper would either lose PID-peer expansion (re-introducing missed descendants/tombstones) or mis-gate against pre-rewind peers (superseding the wrong recordings). R13 changes the split: extract the existing helper's body into `ComputeSubtreeClosureInternal(marker, rootOverride)` so the marker context (InvokedUT, mixed-parent halt, chain-sibling expansion) is preserved exactly; the existing `ComputeSessionSuppressedSubtree(marker)` becomes a thin wrapper delegating with `marker.OriginChildRecordingId`. `AppendRelations` calls the internal helper with the SupersedeTargetId-or-origin fallback. Cache key inside the helper must include `rootOverride` so the two call shapes don't collide. Updated §12 recommendation to match. (b) **P2.N** -- the §9.2.1 step 2 pseudocode still wrote `provisional.SupersedeTargetId = priorTip` unconditionally even though step 4 explicitly says the assignment must be guarded; following step 2 verbatim would crash on the in-place branch where `provisional` is null. R13 inlines the `if (provisional != null)` guard into the step 2 snippet so the recipe is internally consistent.

---

## 1. The ask

Today, Unfinished Flights surfaces only `TerminalKind.Crashed` siblings of multi-controllable splits. The proposal is to broaden the group to include **stable leaves the player did not finish on purpose**: probes left in a parking orbit at separation, stranded EVA kerbals, sub-orbital coast that never resolved.

The user's hard rule:

> No re-fly is allowed for any recording that was merged on the timeline and is not a leaf.
> A leaf is a recording with no downstream BranchPoint (no docking, no boarding, no further
> separation). The classifier must also NOT include vessels that "reached a stable conclusion
> on their own" -- a booster that auto-parachutes to a safe landing is finished, not unfinished.

The user's framing for the algorithm:

> We need a clear policy. Auto-classify the obvious cases. Don't get caught up in edge cases
> and heuristics. Use a per-row Seal button as the override for cases where the default is
> wrong.

R3 implements that exactly: a small, simple default predicate plus a player-controlled override.

---

## 2. What "leaf" means when one vessel maps to many recordings

A single physical vessel often produces a chain of `Recording` instances linked by `ChainId` / `ChainBranch=0` / `ChainIndex`. Two mechanisms create chain segments:

1. **Optimizer env-class splits.** `RecordingOptimizer.SplitAtSection` cuts at TrackSection boundaries when env class changes (Atmospheric / Exo / SurfaceMobile-or-Stationary / Approach) or body changes (#251). See [RecordingOptimizer.cs:178-230](../../../Source/Parsek/RecordingOptimizer.cs).
2. **Continuation across focus switch / scene exit.** Active recording closes; on return, a fresh recording starts; chained by `ChainId`.

A Mun lander chain might look like:

```
ChainId=L  ChainBranch=0
  index 0  exo      (Kerbin orbit phase)         <- HEAD, ParentBranchPointId = undock
  index 1  exo      (Mun SOI cruise)             <- body change at SOI entry
  index 2  surface  (Mun landed)                 <- env class change
  index 3  exo      (back in orbit after takeoff) <- env class change
  index 4  exo      (Kerbin SOI return)
  index 5  atmo     (re-entry)                   <- env class change
  index 6  surface  (Kerbin splashdown)          <- TIP, terminal=Splashed,
                                                    ChildBranchPointId = null
```

Leaf detection MUST walk to the chain TIP via `EffectiveState.ResolveChainTerminalRecording` and apply a **per-RP-context** check on the TIP's `ChildBranchPointId`: the recording is leaf-shaped relative to a candidate RP iff the TIP has no downstream BP (`ChildBranchPointId == null`) OR the downstream BP IS the candidate RP's own `BranchPointId` (the breakup-survivor case). The exact predicate lives in §3; do not implement a standalone null-only leaf check -- doing so re-introduces the breakup-survivor regression caught by R5's external review.

**Important corollary:** the HEAD's own `ChildBranchPointId` is non-null (it points to the next chain segment). A naive `rec.ChildBranchPointId == null` check on the recording itself would mis-classify every chain HEAD. The chain-walk to TIP plus the per-RP-context check fix this.

### 2.1 Optimizer concerns (separate investigation)

Two related concerns the user flagged, both filed as separate investigation tasks (chips):

1. **Eccentric-orbit chain bloat.** An on-rails BG vessel with periapsis inside atmosphere may emit `Atmospheric` and `ExoBallistic` samples on alternating orbits, causing the optimizer to split each pair. Chain length grows unboundedly. The fix is in the optimizer's split rule.
2. **Meaningful-split-only redesign.** Broader principle: the optimizer should only split at env-class boundaries that correspond to *real* gameplay events (launch, re-entry, landing, take-off, destruction), not passive geometric crossings. Catalogue every (from, to) env-class transition pair and find a discriminator (focus history, thrust at crossing, on/off-rails state, nearby part events) that separates meaningful from passive.

For the leaf-extension feature, both concerns affect chain-walk performance but not correctness. `ResolveChainTerminalRecording` finds the same TIP regardless of chain length. Ship this feature on top of whichever optimizer behaviour is current; expect the optimizer fix to land independently.

---

## 3. The default classifier (R5 -- final shape)

R1's "Parked Flights" group is dropped (one group, called Unfinished Flights). R2's voluntary-action heuristics (A1/A2/A4) are dropped (terminal-state-based default + manual override is simpler and matches the user's framing).

R5's external review forced three structural changes to the predicate that R4 had wrong:

1. **`MergeState == CommittedProvisional` cannot be the close signal.** v0.9's `IsUnfinishedFlight` accepts both `Immutable` and `CommittedProvisional` for legacy crash rows whose origin tree was discarded mid-flow. Narrowing to CP-only would silently drop those legacy rows. R5 keeps the v0.9 check and introduces `ChildSlot.Sealed` (a new persistent bool, default false) as the dedicated close signal.
2. **`chainTip.ChildBranchPointId == null` is too strict.** The v0.9 breakup-survivor active-parent slot links to its RP's BranchPoint via `ChildBranchPointId` (see [RecordingsTableUI.cs:2809-2817](../../../Source/Parsek/UI/RecordingsTableUI.cs)). R5 rewrites the leaf check as a per-RP-context predicate that accepts either no downstream BP at all OR a downstream BP that IS the slot-defining BP.
3. **Naive Orbiting-qualifies promotes every focus-continuation upper stage to UF.** S19 / §S3 disk-impact analysis breaks: the upper stage that orbits as the player's mission becomes a CP slot, blocking RP reap. R5 introduces `RewindPoint.FocusSlotIndex` (the slot index of whichever vessel was the active focus at split time) and gates stable-terminal qualification on `slot.SlotIndex != FocusSlotIndex`. Crashed terminals continue to qualify regardless of focus, matching v0.9 active-parent-can-crash semantics.

The shape after R5:

```
IsUnfinishedFlight(rec) :=
    rec is in ERS
    AND rec.MergeState in { Immutable, CommittedProvisional }   // v0.9 contract preserved
    AND chainHead.IsDebris == false                             // controllable-subject gate
    AND exists RP, exists slot in RP.ChildSlots such that:
        // Slot resolution -- v0.9 logic, unchanged
        slot.EffectiveRecordingId(supersedes) == rec.RecordingId
        AND (rec.ParentBranchPointId == RP.BranchPointId
             OR rec.ChildBranchPointId == RP.BranchPointId)

        // Slot-close gate (P1.1 fix)
        AND slot.Sealed == false

        // Per-RP leaf gate (P1.2 fix)
        AND let chainTip = ResolveChainTerminalRecording(rec)
            (chainTip.ChildBranchPointId == null
             OR chainTip.ChildBranchPointId == RP.BranchPointId)
            // null  -> no downstream BP at all (normal-split children).
            // == RP.BranchPointId -> the chain-tip's downstream BP IS the
            // slot-defining BP, which is the breakup-survivor case where
            // the survivor recording (= the slot) terminates AT the breakup
            // BP itself rather than producing a fresh continuation.

        // Outcome gate (P1.3 fix folded in)
        AND TerminalOutcomeQualifies(chainTip, slot, RP)

TerminalOutcomeQualifies(chainTip, slot, RP) :=
    let kerbal   = !string.IsNullOrEmpty(chainTip.EvaCrewName)   // Recording.cs:142
    let terminal = chainTip.TerminalStateValue                   // Nullable<TerminalState>
    let isFocus  = (slot.SlotIndex == RP.FocusSlotIndex)         // P1.3 fix

    if !terminal.HasValue:
        return false
        // No terminal recorded means finalization didn't run cleanly. The
        // recording-finalization-design.md contract guarantees this is rare
        // post-v0.9, but we don't auto-include a recording whose terminal
        // we can't read. Logged as reason=noTerminal for diagnostics.

    if kerbal:
        return terminal.Value != Boarded
        // EVA kerbals: any non-Boarded terminal is unfinished (stranded on
        // surface, drifting in orbit, dead). Focus-status doesn't gate this --
        // a stranded EVA kerbal is unfinished whether the player flew the
        // EVA actively or never took focus. The kerbal branch returns BEFORE
        // the FocusSlotIndex == -1 short-circuit below, by design: stranded
        // EVAs surface even from legacy / no-focus-signal RPs. This is an
        // INTENTIONAL retroactive exception to the otherwise forward-only
        // migration -- a stranded kerbal is unambiguous (player wants them
        // back), unlike orbital siblings where intent is ambiguous.
        // See §9.2 + §10 EVA-exception note + §9.5 test cases.

    if terminal.Value == Destroyed:
        return true                       // Crashed -- the v0.9 case
                                          // qualifies regardless of isFocus,
                                          // matches v0.9 active-parent-crash

    // Stable in-flight terminals only qualify for non-focus slots on RPs
    // that have a defined focus signal. FocusSlotIndex == -1 covers TWO
    // cases that we treat identically here:
    //   (a) Legacy RPs written before this feature -- no signal at all.
    //   (b) New post-feature RPs where no slot was focused at split time
    //       (rare: e.g. the player was focused on an unrelated vessel
    //       outside the split, like a chase plane watching another vessel
    //       undock). RewindPointAuthor.Begin sets -1 explicitly here.
    // Both cases mean "we cannot distinguish a routine focus-continuation
    // upper stage from an unflown deploy", so we conservatively suppress
    // Orbiting/SubOrbital qualification. The Park-from-not-UF affordance
    // (deferred to v2) is the player's escape hatch for case (b).
    // See §9.2's legacy-migration paragraph.
    bool noFocusSignal = (RP.FocusSlotIndex == -1)
    if noFocusSignal:
        return false                              // Crashed / EVA already returned above

    if terminal.Value == Orbiting  && !isFocus: return true
    if terminal.Value == SubOrbital && !isFocus: return true
        // Vacuum-arc SubOrbital only. Atmospheric SubOrbital is reclassified
        // to Destroyed by BallisticExtrapolator.cs:223 +
        // IncompleteBallisticSceneExitFinalizer.cs:464,489 before commit.
        //
        // Focus-slot stable terminals (the player's mission upper stage
        // that orbited successfully, the lander the player flew down to
        // the surface) are the player's deliberate outcome, not unfinished.
        // The Park-from-not-UF affordance (deferred to v2) is the path
        // for re-flying these on demand.

    // Landed / Splashed / Recovered / Docked: stable surface or recovered
    // terminal. The universe gave the vessel a conclusion that doesn't
    // smell unfinished. Default does not include the row regardless of
    // focus. (Stranded EVAs hit the kerbal branch above before getting
    // here.)
    return false
```

Plus a **manual Seal override** on each Unfinished Flight row that sets `slot.Sealed = true`, removes the row from the group, and lets the RP reaper free the quicksave when all siblings are also sealed-or-Immutable. Crucially, **Seal does NOT touch `MergeState`** -- that's how legacy `Immutable` crash rows can stay re-flyable today, and how a sealed slot on a CP recording stays out of the UF group without conflating the two signals.

Plus, deferred to v2: a **Park override** on rows that the default classifier would NOT include (a Landed rover the player wants re-flyable later, a focus-continuation upper stage the player wants to re-fly to a different orbit). v1 ships without this; if playtest shows demand, add later.

---

## 4. The hard rule, restated

> A recording does not qualify for re-fly if it has any downstream BranchPoint after its
> chain TIP. Period. Nothing the player or the algorithm does can override this.

`chainTip.ChildBranchPointId != null` is **a necessary condition for re-fly to be forbidden, not the full predicate.** A recording can also be ineligible for re-fly because:

- Its `MergeState` is not in `{ Immutable, CommittedProvisional }` (e.g. the live re-fly provisional with `MergeState == NotCommitted`).
- It is not in ERS (filtered out by supersede or by `SessionSuppressedSubtree` during an active re-fly).
- The parent BranchPoint has no live `RewindPoint` with a slot for this recording (the RP was reaped, was never written, or is `Corrupted`).
- It is debris (`IsDebris == true`) -- not re-flyable in v1.

§3's `IsUnfinishedFlight` predicate composes all of those gates. The hard-rule statement above is the *structural* invariant that the rest of the predicate refines further.

In code, the structural-leaf negation must use the **same per-RP context** as §3's predicate -- a non-null `ChildBranchPointId` is permitted when it equals the slot-defining BP (the breakup-survivor case from §3 P1.2):

```
HasDownstreamBP(rec, matchedRP) :=
    let chainTip = ResolveChainTerminalRecording(rec)
    chainTip.ChildBranchPointId != null
        AND chainTip.ChildBranchPointId != matchedRP.BranchPointId
```

A `true` result here means re-fly is forbidden no matter what else holds. A `false` result means re-fly *might* be permitted, subject to the other gates in §3.

Outside an RP-resolution context (e.g. a generic "is this recording re-fly-able" query without a specific RP), the only safe restatement is the predicate-shaped one: re-fly is forbidden iff `IsUnfinishedFlight(rec)` is false for ALL RPs that include `rec` in a slot. There is no shorter standalone "leaf check" that captures the hard rule without losing the breakup-survivor case -- that's the lesson from R5's external review.

---

## 5. RP retention and reaping

User confirmed (R3): **keep an RP alive while any sibling could re-fly.** Concretely: an RP becomes reap-eligible only when every child slot is "closed" -- either the effective recording is `Immutable` (today's signal) OR the slot has `Sealed == true` (R5's new signal). The reaper rule extends from "every slot Immutable" to "every slot Immutable OR Sealed."

**This is the only reaper change in R5.** Previously reaped behaviour for routine missions is preserved -- the §S3 / §S19 auto-parachute booster scenarios reap as before because the focus-continuation upper stage now correctly resolves to Immutable (P1.3 fix; see §3) and the booster's Landed terminal stays Immutable (TerminalOutcomeQualifies returns false).

Today (`RecordingStore.ApplyRewindProvisionalMergeStates`, [RecordingStore.cs:734](../../../Source/Parsek/RecordingStore.cs)):

```
if rec.terminal classifies as Crashed AND parent BP has live RP with slot:
    promote rec to CommittedProvisional
otherwise:
    leave Immutable
```

After this feature:

```
for each rec:
    if rec.MergeState != Immutable: continue           // CP and NotCommitted skip
    if NOT IsUnfinishedFlight(rec, considerSealed=false):
        continue                                       // not eligible
    promote rec to CommittedProvisional                // becomes a UF row
```

The `considerSealed=false` flag bypasses the slot.Sealed check at promotion time -- a freshly committed slot is never Sealed yet, so the check is moot, but the unified entry point matters for the read-time predicate (§3) where the Sealed gate is live.

**The same predicate must also feed the re-fly merge path.** `SupersedeCommit.FlipMergeStateAndClearTransient` today maps `TerminalKindClassifier.Classify(provisional) != Crashed` -> `Immutable`. R5 extends it to: a re-fly merging into a slot that satisfies `TerminalOutcomeQualifies(chainTip, slot, RP)` commits `CommittedProvisional` so the slot stays open for another re-fly attempt (consistent with §S8 chain extension). See §9.2 for the call-site change.

Disk impact (R5 verdicts):

- **4-probe scenario (S1):** mothership is FocusSlot -> Orbiting + isFocus -> NOT UF -> Immutable. The 4 probes are non-focus slots -> Orbiting + !isFocus -> UF -> CP. RP stays alive while any of the 4 probe slots is unsealed; player Seals or successfully re-flies (Immutable terminal) to close.
- **Auto-parachuting booster (S3, S19):** booster is non-focus, terminal Landed -> TerminalOutcomeQualifies false -> Immutable. Upper stage is FocusSlot, terminal Orbiting, but isFocus -> TerminalOutcomeQualifies false -> Immutable. All slots Immutable -> RP reaps. **No regression vs v0.9.**
- **Crashed booster (S2):** unchanged from v0.9. Booster non-focus, terminal Destroyed -> qualifies regardless of isFocus -> CP. RP stays alive.
- **Inverted case (S19b):** player flew the booster (booster is FocusSlot), upper stage was BG and ends Orbiting. Upper stage non-focus + Orbiting -> qualifies -> CP. Booster (focus) Landed -> Immutable. RP stays alive while upper-stage slot is unsealed.
- **Stranded EVA (S5):** kerbal IS focus typically (player did the EVA), but the kerbal branch in TerminalOutcomeQualifies ignores isFocus -- a stranded EVA is unfinished regardless. Lander is non-focus, Orbiting -> ALSO qualifies (the lander IS in the EVA RP's slots). Both slots CP; RP stays. The lander row is over-inclusion (player likely just wants the kerbal back); player Seals the lander row.

This is the right disk-cost shape: the player only pays for slots that the predicate actually flags. Routine focus-continuation missions reap cleanly. Off-mission siblings keep the slot alive until Seal or successful re-fly.

---

## 6. Re-fly mechanics

Confirmed in R2: each re-fly uses the existing RP-rewind mechanism. Rewind to split UT, selected child is live, others ghost. Player flies, merges, supersede chain extends. The "fly in parallel" is **temporal interleaving on the committed timeline** -- each re-fly slots into history at the same UT range as the original split. After all four probes are re-flown:

```
Timeline view at UT 1000-2000 (the staging UT and a bit after):
  - Mothership return-to-Kerbin recording (original)
  - Probe 1 alternate flight   (re-fly, supersede of probe 1 BG-coast)
  - Probe 2 alternate flight   (re-fly, supersede of probe 2 BG-coast)
  - Probe 3 alternate flight   (re-fly, supersede of probe 3 BG-coast)
  - Probe 4 alternate flight   (re-fly, supersede of probe 4 BG-coast)
```

All play back as ghosts simultaneously when any later mission rewinds past UT 1000. "Parallel" is in playback, not in player wall-clock.

---

## 7. UI

**One Unfinished Flights group** (no separate Parked Flights). System group, not hideable, not a drop target. Tooltip text updated to: "Vessels and kerbals that ended up in a state where you might want to re-fly them -- crashed, abandoned in orbit, stranded on a surface."

**Two row-level actions: Fly + Seal.**

| Action | Effect |
|---|---|
| Fly | Routes through `RewindInvoker.StartInvoke` (existing v0.9 flow). Reloads the RP quicksave, strips siblings, activates this recording's vessel, scene reload. Today this is the only button in the Rewind column for an Unfinished Flight row, drawn by `DrawUnfinishedFlightRewindButton` ([RecordingsTableUI.cs:2559](../../../Source/Parsek/UI/RecordingsTableUI.cs)). |
| Seal | Spawns the Seal confirmation dialog (see §7.1). On accept: set `slot.Sealed = true` (NOT MergeState); bump `SupersedeStateVersion`; row drops from group; reaper runs (RP deleted if all sibling slots are also closed -- Immutable OR Sealed). |

The crashed-row UX (today's v0.9) gets the same two actions. A crashed row's Seal action is "I accept the crash as canonical; stop offering me the re-fly." Same semantics; works identically for both default-UF flavours (Crashed and Stable-Unconcluded).

### 7.0 Unresolved: how to surface the second action in the table

The natural-language R3 framing was "split the Rewind cell into Fly + Seal." On closer reading of [RecordingsTableUI.cs:39](../../../Source/Parsek/UI/RecordingsTableUI.cs), `ColW_Rewind = 75f` and the existing `DrawUnfinishedFlightRewindButton` draws a single Fly button at the full column width. Two side-by-side controls in 75 px would be unreadable. Three candidate layouts, none free:

- **(L1) Widen the Rewind column.** Bump `ColW_Rewind` to ~150 px; draw two `DrawBodyCenteredButton`s side-by-side; each ~70 px wide. Cascades through every row in the table (Crashed rows currently using R/FF/Fly/blank also get the new width). The header "Rewind/FF" stays; possibly relabel to "Rewind / Seal."
- **(L2) Add a separate Seal column.** New `ColW_Seal = 60f`; only drawn for rows where `IsUnfinishedFlight` is true; blank otherwise. Adds a column to *every* row visually (alignment), even though most cells are blank. Less natural-grouping but no width pressure on existing controls.
- **(L3) Row context action / kebab menu.** Tiny "..." button next to the Fly cell that pops a menu with "Seal slot" as the only entry (today). Cheapest layout-wise; least discoverable. Discoverability matters less if the v0.9 crashed-row Seal is also routed through the same menu (consistent across flavours).

R4 does not pick. Promoting this doc to a formal design doc requires a UI mock for one of the three (or a fourth nobody's thought of yet); the chosen layout drives `RecordingsTableUI` changes in the build phase.

### 7.1 Seal confirmation dialog

The Seal action is **destructive and irreversible** -- once a slot is sealed, it closes permanently and the rewind point's quicksave can be deleted by the reaper. The player has no in-game path to un-seal a slot (a Full-Revert of the entire tree is the only way back, and that loses every other commit on the tree too).

The seal flips `ChildSlot.Sealed = true` on the matching slot. It does NOT touch `Recording.MergeState` -- doing so would conflict with v0.9's contract that legacy `Immutable` crash recordings are still valid UF candidates. The slot-level Sealed signal is the dedicated close mechanism (see §3 P1.1 fix).

A `PopupDialog.SpawnPopupDialog` with a `MultiOptionDialog` body. Title: "Seal Unfinished Flight?" Body copy:

```
Seal "<vessel-name>" (<terminal-state> at UT <ut>)?

This action CANNOT BE UNDONE.

After sealing:
  - This slot is closed permanently -- the recording can never be re-flown.
  - The "Fly" button on this row disappears.
  - The rewind point's quicksave file may be deleted (when every
    sibling of this split is also sealed or already finalized).
  - The recording itself is unchanged. It remains in your timeline
    and continues to play back as a ghost on any future rewind,
    exactly as it does now. Sealing only closes the re-fly slot;
    it does not erase the recording or its trajectory.

If you might want to re-fly this later, click Cancel.
```

Buttons: `Seal Permanently` (red / destructive style, fires the seal handler) and `Cancel` (logs `[UnfinishedFlights] Info: Seal cancelled rec=<rid>` and dismisses).

The dialog takes an input lock (`DialogLockId = "ParsekUFSealDialog"`) while visible so the player cannot click other UI controls during the decision.

Logging on accept: `[UnfinishedFlights] Info: Sealed slot=<idx> rec=<rid> bp=<bpId> rp=<rpId> terminal=<state> reaperImpact=<willReap|stillBlocked>` -- the `reaperImpact` field tells a log reader whether this seal triggered the RP cleanup or whether sibling slots are still keeping the RP alive.

**No Park affordance in v1.** Rows that the default classifier excluded (rover-drove-20m, briefly-nudged-probe-still-Landed) cannot be added back to Unfinished Flights in v1. This is intentional scope-trimming per user direction. v2 may add a Park button on the corresponding row in the main Recordings table to manually opt a Landed/Splashed/Recovered leaf into the group.

---

## 8. Gameplay scenarios

Each scenario: setup -> default classifier verdict -> note. The R3 algorithm is small enough that most scenarios resolve trivially.

### S1. Four probes deployed simultaneously from Mun mothership

5-controllable split (mothership + 4 probes). RP captures 5 slots; `RewindPoint.FocusSlotIndex` records the mothership's slot.

- Mothership: terminal Orbiting + isFocus -> `TerminalOutcomeQualifies` false -> Immutable.
- Each probe: terminal Orbiting + !isFocus -> qualifies -> CP -> Unfinished Flight row.

Player can Fly any probe; Seal individually. RP reaps once all 4 probe slots are Sealed or successfully re-flown to closing (Immutable) terminals AND the mothership slot is Immutable (which it already is).

### S2. Booster recovery (crashed)

Unchanged from v0.9. Crashed -> UF.

### S3. Booster auto-parachutes successfully

2-controllable split. Upper stage is FocusSlot.

- Booster: non-focus, terminal Landed -> `TerminalOutcomeQualifies` false (Landed always returns false regardless of focus) -> Immutable.
- Upper stage: focus, terminal Orbiting -> `TerminalOutcomeQualifies` false (isFocus excludes Orbiting) -> Immutable.

All slots Immutable -> RP reaps. **R3 reverses the R2 reading**: "controller landed = reached stable state = not unfinished." **R5 confirms** the reap actually happens (R4 had this wrong: without the FocusSlotIndex gate, the upper stage would have been promoted to CP and blocked the reap on every routine launch).

### S4. EVA kerbal plants flag, reboards

EVA recording's chain TIP gets `ChildBranchPointId = boardBp.Id`. The matching RP is the original EVA RP, whose `BranchPointId = evaBp.Id`. Per the per-RP leaf gate (§3): `chainTip.ChildBranchPointId != null AND chainTip.ChildBranchPointId != evaBp.Id` -> rejected. Not UF. ForbidRefly true. ✓

### S5. EVA kerbal plants flag, abandoned (no reboard)

2-controllable split (lander + EVA kerbal). Kerbal is FocusSlot (player took focus on EVA).

- Kerbal: chain TIP terminal Landed; kerbal branch in `TerminalOutcomeQualifies` ignores isFocus; not Boarded -> qualifies -> CP -> UF row.
- Lander: non-focus, Orbiting -> qualifies -> CP -> UF row too (over-inclusion: player likely just wants the kerbal back).

Player Flies the kerbal to attempt reboard, or Seals the kerbal row to accept the loss. The lander row is independent; the player Seals it if they don't want to re-fly the lander.

### S6. Probe undocked, player flew it through Hohmann to Mun, parked

Chain TIP terminal Orbiting (around Mun). `TerminalOutcomeQualifies` true. UF. Default would include this row.

But: user's Q2/Q3 framing said "if the player did the mission with that vessel and merged, not UF." Mun-transfer-and-park IS a mission the player did with the probe. So default-UF over-includes.

In v1, the player Seals it. One-click cleanup. Acceptable per user direction. v2's Park-or-Seal symmetry could move this case from "auto-included, must Seal" to "auto-excluded, must Park," but that change would also auto-exclude S1 (4 probes) which the user wants auto-included. The v1 default favours over-inclusion + Seal; v2 might add a "did the player meaningfully fly this" heuristic to flip the default for cases like S6. Out of scope for v1.

### S6b. Probe carried to Mun by transfer stage; player undocked there; probe untouched

Chain HEAD starts in Mun orbit. Chain TIP also Mun orbit. Terminal Orbiting. UF. ✓ Matches user intent (deployed in Mun orbit and forgot).

### S7. Re-fly already happened, sealed

Slot Immutable. Predicate filters out. ✓ Same as v0.9.

### S8. Re-fly a parked leaf, end in another stable state

Re-fly merge: terminal Landed -> `TerminalKindClassifier.Classify` returns Landed -> per `SupersedeCommit.FlipMergeStateAndClearTransient`, MergeState = Immutable. Slot closes. Supersede chain `probeOrig -> probeReFly`. ✓

If re-fly ends Orbiting (player parked in a different orbit): under v0.9 today, `Classify` returns InFlight and `SupersedeCommit.FlipMergeStateAndClearTransient` maps `kind != Crashed -> Immutable`. The slot would close. **R4 changes this**: §9.2 extends `FlipMergeStateAndClearTransient` to use the same `TerminalOutcomeQualifies` predicate as `ApplyRewindProvisionalMergeStates`, so a re-fly ending Orbiting / SubOrbital / EVA-stranded commits `CommittedProvisional`. The slot stays open; the row reappears in Unfinished Flights with the new flight as the effective recording. The player can Fly it again, or Seal to close.

This means the chain-extension semantics in R3 (only Crashed extends the chain) are **broadened** to also extend through stable-unconcluded re-flies. A player who parks a probe, re-flies it, ends in a different stable orbit, and parks again has a 3-link supersede chain `probeOrig -> probeReFly1 -> probeReFly2`, all CP, slot still open. To finally close the slot, the player either re-flies to a Landed terminal (Immutable per `TerminalOutcomeQualifies` returning false) or hits Seal.

### S9. Re-fly a parked leaf, dock with another vessel

Dock BP -> structural leaf gate fails on the new flight. Not a leaf. Slot closes. Cross-tree station unaffected. ✓ (Same as v0.9.)

### S10. Player reverts a parked re-fly mid-flight

`RevertInterceptor` 3-option dialog. Discard Re-fly preserves the parked row. ✓ Unchanged from v0.9.

### S11. Crash-quit during a parked re-fly

Marker validates against on-disk session-provisional + RP. Session resumes. ✓ No new state.

### S12. Existing save with old reaped RPs

Forward-only behaviour. Existing splits whose RPs already reaped don't retroactively re-appear. CHANGELOG note required.

### S13. Re-fly delta is 50 in-game years

Confirmation dialog warns about large UT delta. UT jumps back; career state preserved by reconciliation bundle. ✓ (Mitigation deferred to v2.)

### S14. Stranded EVA kerbal counts against roster

Same as S5. UF flow handles it. ✓

### S15. Player wants the row gone

Seal button on the row. ✓ Confirmed as the primary cleanup affordance.

### S16. Eccentric orbit periapsis-grazes atmo

Chain may be N segments long depending on optimizer behaviour. `ResolveChainTerminalRecording` walks to TIP. TIP terminal Orbiting. UF. Performance scales with chain length; orthogonal to this feature; addressed by the optimizer-fix investigation.

### S17. Player switched briefly to nudge a probe

Probe still terminal Orbiting after the nudge. UF. Player can Fly to redo the probe, or Seal to accept. v1 over-includes nudged probes; player handles via Seal. ✓ Acceptable per user.

### S18 (R3-new). Rover drove 20m, player merged

Rover terminal Landed. `TerminalOutcomeQualifies` false. NOT UF. Default does NOT include the row. v1 has no Park button to add it. The player accepts the rover is "concluded" by default; if they later want to drive it more, they take over via stock KSP from the Tracking Station.

This contradicts the user's Q2 verbal example ("rover drove 20m -> UF"), but matches their preference for "simple algorithm + Seal override over heuristic edge cases." The Park-from-not-UF affordance is the v2 path for this case.

### S19 (R3-new). Two-stage with controllable booster, player just deploys parachute and lets it ride

Booster has parachutes + probe core (controllable). Player briefly switches to booster to toggle chute, returns to upper stage, booster lands safely. Upper stage continues to orbit.

Either order of focus-attribution at split time works for the predicate, because both terminals are excluded:

- Upper stage (likely FocusSlot, since player flew it to orbit): Orbiting + isFocus -> not UF -> Immutable.
- Booster (non-focus): Landed -> Landed always returns false -> Immutable.

Both Immutable -> RP reaps. ✓ Matches user's Q3 example. **R5 makes this work correctly** -- in R4 the upper stage would have flooded UF; the FocusSlotIndex gate prevents that.

### S20 (R3-new). Same as S19 but booster is uncontrollable (no probe core)

Booster has parachutes only, no controller. At split time, the controllable-subject gate at chain HEAD fails. NOT UF. Booster is debris, may not even produce a multi-controllable RP at all (`SegmentBoundaryLogic.IsMultiControllableSplit` requires count >= 2 controllables). ✓ Matches user's Q3 example.

### S19b (R4-new, R5-revised). Inverted: upper stage is the leaf, booster is the active mission

Setup. Single-stage spaceplane with a strap-on booster section. Player flies the BOOSTER section to recover it (the actual mission); the strap-on upper stage is BG-recorded and ends Orbiting around Kerbin (it never had a planned mission -- the player only cared about the booster). Booster is FocusSlot.

- Booster: focus, Landed -> not UF -> Immutable.
- Upper stage: non-focus, Orbiting -> qualifies -> CP -> UF row.

RP stays alive while upper-stage slot is unsealed. Player Flies the upper stage to do something with it (deferred mission), or Seals to close.

This case is symmetric to S1 but inverts the roles of the two-vessel split: a BG sibling that happens to orbit is the new common UF-row source. **R5 confirms** the FocusSlotIndex gate makes this work without flooding routine S19-shape missions.

### S21 (R4-new). Cross-tree dock during a stable-leaf re-fly

Setup. Player picks parked probe (S1) from Unfinished Flights, hits Fly, rewinds to split UT. Probe is live; mothership is ghost. Player flies the probe to a station that belongs to a *different tree* (different launch). Docks the probe to the station.

Behaviour. Dock BP fires; probe-re-fly recording gains a `ChildBranchPointId`. On merge, `SupersedeCommit.AppendRelations` walks `EffectiveState.ComputeSessionSuppressedSubtree(marker)` for the supersede subtree -- which is tree-scoped and halts at mixed-parent BranchPoints. The station's tree is unaffected; supersede stays inside the probe's tree. The probe-re-fly itself is no longer a leaf (Dock BP). `FlipMergeStateAndClearTransient` runs `TerminalOutcomeQualifies` on a Docked terminal -> returns false -> Immutable. Slot closes.

Same shape as v0.9 §7.7 (cross-tree dock during re-fly). Confirmed compatible with the broadened predicate; no new code path needed. v0.9's "acceptable v1 limitation: no dedicated test for cross-tree dock" still applies; this feature should add a covering test as part of its in-game test pass.

### S22 (R5-new). Breakup-survivor active parent is the leaf

Setup. Player is flying a vessel V that suffers a structural breakup mid-flight (heat, overpressure). KSP fires `Breakup` BP; the survivor continues with the SAME recording id (rather than a fresh continuation), with `ChildBranchPointId = breakBp.Id`. The breakup spawns several debris fragments (no controllers; not slot-eligible per the controllable-subject gate) and ONE controllable survivor (V itself, minus the broken parts).

The breakup RP includes V as a controllable slot output. V's slot is the survivor; its OriginChildRecordingId is V's pre-breakup recording id (which continues post-breakup). FocusSlotIndex points at V's slot (the survivor stays focused).

Now V continues flying and either lands successfully or crashes:

- **Survivor lands successfully.** V's chain TIP terminal Landed. V is FocusSlot. Landed terminals always return false from `TerminalOutcomeQualifies` -> Immutable. RP reaps. ✓
- **Survivor crashes anyway.** V's chain TIP terminal Destroyed (Crashed). Crashed always qualifies regardless of focus -> CP -> UF row. ✓ (Same as v0.9.)
- **Survivor leaves orbit unfinished.** V's chain TIP terminal Orbiting. V is FocusSlot -> Orbiting + isFocus -> not UF -> Immutable. **Player loses access to re-fly the breakup moment because the focus-gated stable terminal didn't qualify.** This is acceptable per the no-Park-in-v1 trade -- if the player wanted to re-fly the breakup, they should have crashed the post-breakup vessel or sealed Landed. v2's Park affordance is the proper fix here.

The leaf gate is the critical one for this scenario: V's chainTip has `ChildBranchPointId = breakBp.Id`, NOT null. The R4 standalone-leaf gate would have rejected V outright. R5's per-RP-context gate accepts V because `chainTip.ChildBranchPointId == matchingRP.BranchPointId` (the breakup RP's BranchPoint is the same as V's downstream BP).

---

## 9. Data-model and code touchpoints

### 9.0 New persistent fields (R5 + R10)

Three new fields on existing types. All back-compat (default values match v0.9 behaviour).

**`ChildSlot.Sealed`** ([ChildSlot.cs](../../../Source/Parsek/ChildSlot.cs)):

```csharp
/// True if the player invoked the per-row Seal action on this slot,
/// closing it permanently. Excluded from IsUnfinishedFlight; treated as
/// equivalent-to-Immutable by RewindPointReaper. Default false; legacy
/// saves load with false (existing crash UF rows continue to qualify).
public bool Sealed;

/// Wall-clock ISO-8601 UTC timestamp the Seal was applied. Diagnostic
/// only; null when Sealed is false.
public string SealedRealTime;
```

ConfigNode keys: `sealed` (omitted when false), `sealedRealTime` (omitted when null). `SaveInto`/`LoadFrom` extended in `ChildSlot.cs`.

**`RewindPoint.FocusSlotIndex`** ([RewindPoint.cs](../../../Source/Parsek/RewindPoint.cs)):

```csharp
/// Slot index (0-based, into ChildSlots) of the vessel that was the
/// active focus at split time. -1 means unknown / no focus continuation
/// in this RP (e.g. legacy RPs from before this field existed, or pure
/// background-only splits where no vessel had focus).
///
/// Set by RewindPointAuthor.Begin: at the moment the multi-controllable
/// split fires, FlightGlobals.ActiveVessel.persistentId is matched against
/// the slot's expected vessel PID; the matching slot's index is stored.
/// If no slot matches (the focused vessel was the pre-split parent that
/// is NOT itself a slot in normal-split RPs), FocusSlotIndex stays -1.
///
/// Used by IsUnfinishedFlight's TerminalOutcomeQualifies to gate
/// stable-terminal qualification: focus-continuation slots are excluded
/// from auto-UF for stable terminals (Orbiting / SubOrbital). Crashed
/// terminals qualify regardless of FocusSlotIndex.
public int FocusSlotIndex = -1;
```

ConfigNode key: `focusSlotIndex`, written when != -1. Legacy RPs load with -1; `TerminalOutcomeQualifies` interprets -1 as "no focus signal available" and short-circuits Orbiting/SubOrbital to false (forward-only migration -- see §3 algorithm and §9.2's legacy-migration paragraph). Crashed continues to qualify regardless of focus, preserving v0.9 parity for that case. **Stranded EVA also qualifies, which is NEW behaviour vs v0.9** (the EVA branch returns before the focus short-circuit; intentional retroactive carve-out -- see §3, §9.2, §10). New post-feature RPs always have a defined `FocusSlotIndex` (>= 0 for the focused slot, or explicitly -1 ONLY when no slot was focused at split time -- e.g. a pure background split where the focused vessel is not itself in any slot).

**`ReFlySessionMarker.SupersedeTargetId`** (R10/R11, [ReFlySessionMarker.cs](../../../Source/Parsek/ReFlySessionMarker.cs)):

```csharp
/// The supersede target at invocation time -- the slot's CURRENT EFFECTIVE
/// recording (slot.EffectiveRecordingId(supersedes)). For markers written
/// by post-feature invocations, this field is ALWAYS set, even on the first
/// re-fly into a slot (where the value equals OriginChildRecordingId).
/// Legacy markers (written before this field existed) load with null; the
/// AppendRelations code path coalesces "null OR equal to OriginChildRecordingId"
/// behaviour by falling back to OriginChildRecordingId in both cases (§9.2.1
/// step 3). The field is therefore safe to read unconditionally without a
/// "is the marker post-feature?" gate.
///
/// Used by SupersedeCommit.AppendRelations as the root of the subtree
/// closure walk: when set, the closure is rooted at SupersedeTargetId
/// (= prior tip on chain extension; = slot origin on first re-fly).
/// CommitTombstones receives the same closure for tombstone scoping.
/// This decouples the supersede-graph topology from the slot identity
/// (held in OriginChildRecordingId), so existing consumers that key off
/// the slot's immutable origin (RevertInterceptor.FindSlotForMarker,
/// in-place continuation, ghost suppression) continue to work unchanged.
///
/// See §9.2.1 for the marker-write site change, the AppendRelations
/// closure-root change, and §9.5 for the existing-consumer audit test.
public string SupersedeTargetId;
```

ConfigNode key: `supersedeTargetId`, **always written** when the marker is persisted (R11 P2 fix: previously framed as "omitted when null," which contradicted the unconditional marker-write recipe). Legacy markers load with null because the key was absent on disk. Validated by `MarkerValidator.Validate` only weakly: when present, must resolve in `CommittedRecordings`; when null (legacy marker), no validation.

### 9.1 Predicate

`EffectiveState.IsUnfinishedFlight` rewritten per §3:

- Keep the v0.9 `MergeState in { Immutable, CommittedProvisional }` check.
- Add the controllable-subject check at chain HEAD (`chainHead.IsDebris == false`).
- Inside the per-RP slot loop: add the `slot.Sealed == false` gate AND the per-RP-context leaf check (`chainTip.ChildBranchPointId == null OR == RP.BranchPointId`).
- Replace the terminal-Crashed-only check with `TerminalOutcomeQualifies(chainTip, slot, RP)` per §3.

Extract `TerminalOutcomeQualifies` and the controllable-subject helper into `EffectiveState` (or a new `UnfinishedFlightClassifier` static class) so both call sites in §9.2 share a single source of truth.

Logging additions (`[UnfinishedFlights] Verbose`):

- `IsUnfinishedFlight=false rec=<rid> reason=notControllable headIsDebris=true`
- `IsUnfinishedFlight=false rec=<rid> reason=slotSealed slot=<idx>`
- `IsUnfinishedFlight=false rec=<rid> reason=downstreamBp chainTipChildBp=<bpId> matchedRpBp=<bpId>`
- `IsUnfinishedFlight=false rec=<rid> reason=stableTerminalFocusSlot slot=<idx> focusSlot=<idx> terminal=<state>`
- `IsUnfinishedFlight=false rec=<rid> reason=stableTerminal terminal=<state>` (Landed/Splashed/Recovered/Docked)
- `IsUnfinishedFlight=false rec=<rid> reason=noTerminal` (null TerminalStateValue)
- `IsUnfinishedFlight=true  rec=<rid> reason=crashed terminal=Destroyed`
- `IsUnfinishedFlight=true  rec=<rid> reason=stableLeafUnconcluded slot=<idx> terminal=<state>`
- `IsUnfinishedFlight=true  rec=<rid> reason=strandedEva terminal=<state>`

### 9.2 MergeState promotion -- TWO call sites

The promotion logic must change in **two places**, not one. Missing the second one breaks chain extension for stable-leaf re-flies (see §S8 R4 update).

**Site A: original mission tree commit.** `RecordingStore.ApplyRewindProvisionalMergeStates` ([RecordingStore.cs:715-770](../../../Source/Parsek/RecordingStore.cs)) extended. The implementation must call the shared classifier with the resolved `(Recording, ChildSlot, RewindPoint)` triple; per-leaf and per-focus gates only work with that context, so a free-standing "is leaf" check would re-introduce P1.2 / P1.3:

```
for each rec in tree.Recordings:
    if rec.MergeState != Immutable: continue                    // CP / NotCommitted skip
    if rec.chainHead.IsDebris: continue                          // controllable-subject gate

    // Resolve the matching RP + slot using the v0.9 helper that already
    // walks ParentBranchPointId / ChildBranchPointId and supersedes:
    if NOT TryResolveRewindPointForRecording(rec, out rp, out slotIdx):
        continue
    var slot = rp.ChildSlots[slotIdx]

    // Hand the full triple to the shared classifier. considerSealed=false
    // because a freshly committed slot is never Sealed yet; the gate is
    // only meaningful at read time on already-CP rows.
    if NOT UnfinishedFlightClassifier.Qualifies(
            rec, slot, rp, considerSealed: false):
        continue

    rec.MergeState = CommittedProvisional
    log [UnfinishedFlights] Info: CommitTree: promoted rec=<rid>
        slot=<slotIdx> rp=<rpId> reason=<crashed|stableLeafUnconcluded|strandedEva>
```

`UnfinishedFlightClassifier.Qualifies(rec, slot, rp, considerSealed)` is the shared helper extracted from §3. Site B (`SupersedeCommit.FlipMergeStateAndClearTransient`) calls the same helper with the same triple. The shared-classifier identity test in §9.5 forces both call sites through this entry point so the predicate cannot drift.

The existing v0.9 crash-only path is subsumed: `Qualifies(...)` returns true for `terminal == Destroyed` regardless of focus, so legacy crash promotion still fires (P1.1 regression preserved).

**Legacy migration is forward-only, enforced inside `TerminalOutcomeQualifies`.** Earlier R4/R5 drafts proposed a per-recording "skip any previously-Immutable" guard at the top of the loop; the external R6 review correctly flagged that this would block the desired retroactive surfacing of pre-upgrade Orbiting non-focus siblings (the §S5.2 migration case). R7's narrower migration rule lives one layer down: when `RP.FocusSlotIndex == -1`, `TerminalOutcomeQualifies` returns false for `Orbiting` and `SubOrbital` regardless of slot identity. The -1 sentinel covers two cases identically -- legacy RPs that predate this field, AND new RPs where no slot was focused at split time. Both lack the focus signal needed to distinguish a routine focus-continuation upper stage (NOT-UF) from an unflown deploy (would-be UF), so the conservative choice is to leave both as Immutable. Crashed continues to qualify regardless of focus, preserving v0.9 parity for that case. **Stranded EVA also qualifies (intentional R8 carve-out, NOT v0.9 parity)** -- see the §10 EVA-exception note below for the rationale and CHANGELOG split. New post-feature RPs almost always have `FocusSlotIndex >= 0` (the focused vessel at the split is one of the slot members in the common case); the -1 case in new RPs is rare (player focused on a vessel outside the split).

This **changes the §S5.2 / §10 retroactive-surfacing claim**: pre-upgrade BG-recorded vessels left Orbiting do NOT auto-populate Unfinished Flights after upgrade. The motivating "4-probe deploy from a pre-upgrade save" case requires the player to fly a fresh deploy mission post-upgrade. CHANGELOG must say "this feature is forward-only for vessels; split RPs from before the upgrade do not gain new Unfinished Flights rows for orbiting siblings."

**Exception: stranded EVA kerbals DO surface retroactively.** The EVA branch in `TerminalOutcomeQualifies` returns before the `FocusSlotIndex == -1` short-circuit, so a pre-upgrade live RP with an Immutable non-boarded EVA recording newly appears as an Unfinished Flight after upgrade. This is **intentional** -- a stranded kerbal is the player's most common "I want them back" scenario, the volume is small (typical saves don't have many stranded kerbals lying around), and the intent is unambiguous unlike orbital siblings. CHANGELOG must call this out separately: "Stranded EVA kerbals from before the upgrade DO appear as Unfinished Flights; you can rewind to attempt a rescue."

The `RP.FocusSlotIndex == -1` short-circuit is the only retroactive-surfacing brake; v0.9 Crashed-only behavior is preserved exactly for legacy RPs except for the EVA carve-out.

(R6's "Two implementation options" sketch for a per-recording legacy guard -- persisted bit on `Recording` or scenario-level `RewindStableLeavesFeatureSeen` -- is **dropped in R7**. Both proposals were responses to a guard that R7 no longer needs. The `FocusSlotIndex == -1` short-circuit is a single-point check that requires no per-recording migration scan, no new persistent bit on legacy recordings, and no first-load sweep. Landed/Splashed/Recovered/Docked terminals are already excluded by `TerminalOutcomeQualifies` returning false unconditionally for those states, so there is no risk of legacy Immutable Landed siblings being retroactively surfaced regardless of focus signal.)

**Site B: re-fly merge.** `SupersedeCommit.FlipMergeStateAndClearTransient` extended. Today (paraphrased):

```
kind = TerminalKindClassifier.Classify(provisional)
newState = (kind == Crashed) ? CommittedProvisional : Immutable
provisional.MergeState = newState
```

After this feature -- same shared classifier, same triple as Site A. Slot resolution uses the same `TryResolveRewindPointForRecording` helper Site A uses, fed the **provisional** recording. The helper walks each slot's `OriginChildRecordingId` forward through `RecordingSupersedes` and returns the slot whose forward trail contains the queried recording id.

```
// Use the v0.9 resolver. After Phase 2 (Supersede / AppendRelations) of
// MergeJournalOrchestrator runs, the new supersede relation is in
// supersedes, so the walk from the matching slot's OriginChildRecordingId
// now reaches the provisional's id. The resolver returns that slot.
if NOT TryResolveRewindPointForRecording(provisional, out rp, out slotIdx):
    // Should not happen for a valid re-fly merge; defensive log + fall back
    // to the v0.9 default (Immutable for non-Crashed) so we never throw
    // mid-merge.
    log [Supersede] Warn: Site B slot lookup failed for provisional=<rid> ...
    provisional.MergeState = (Classify(provisional) == Crashed)
                                 ? CommittedProvisional : Immutable
    return

var slot = rp.ChildSlots[slotIdx]

bool qualifies = UnfinishedFlightClassifier.Qualifies(
    provisional, slot, rp, considerSealed: false)
provisional.MergeState = qualifies ? CommittedProvisional : Immutable
log [Supersede] Info: provisional=<rid> mergeState=<state> qualifies=<b>
    slot=<slotIdx> rp=<rpId> focusSlot=<rp.FocusSlotIndex>
```

**This feature requires changing v0.9 invocation to write LINEAR supersede semantics. See §9.2.1 below.** The naive helper-based lookup against the existing star-shaped graph does NOT resolve correctly on chain extension; the linear-semantics change is a prerequisite, not an optional polish.

### 9.2.1 Prerequisite: linear supersede semantics on re-fly invocation

Background. v0.9's `RewindInvoker.BuildProvisionalRecording` stamps both `marker.OriginChildRecordingId` and `provisional.SupersedeTargetId` from `selected.OriginChildRecordingId` -- the slot's **immutable original**, not the prior effective tip. `SupersedeCommit.AppendRelations` then writes `{selected.OriginChildRecordingId -> provisional}`. Multiple re-flies into the same slot produce multiple relations all sharing `old = slot.OriginChildRecordingId` -- a **star-shaped graph** rooted at the slot's origin (`{probeOrig -> probeReFly1, probeOrig -> probeReFly2, ...}`).

The current `EffectiveState.EffectiveRecordingId` walker scans `RecordingSupersedes` from the beginning and stops at the first `OldRecordingId` match per node. On a star graph, the walk from `probeOrig` finds the first relation whose old is `probeOrig` (typically `probeOrig -> probeReFly1`, the oldest), steps to `probeReFly1`, finds no relation whose old is `probeReFly1`, terminates and returns `probeReFly1`. **The walker silently misses `probeReFly2`.** A consequence: `ResolveRewindPointSlotIndexForRecording(rp, provisional, supersedes)` -- which accepts the queried recording iff it matches `slot.EffectiveRecordingId` or `slot.OriginChildRecordingId` -- returns -1 for `provisional`. `TryResolveRewindPointForRecording(provisional, ...)` returns false. Site B's helper-based slot lookup fails. The merge falls back to the v0.9 Crashed-only default, which means a stable-leaf re-fly silently seals Immutable instead of CommittedProvisional, and the row vanishes -- the same R6 P1.C failure mode in a new disguise.

This is **not just a Site B problem.** v0.9's existing Crashed chain extension documented in `parsek-rewind-to-separation-design.md` §7.43 ("Chain extends through multiple merged crashes") relies on the same walker. Either v0.9's chain extension is silently broken under current code (the second re-fly's effective lookup returns the first re-fly, hiding the second), or there is some unstated mechanism (relation insertion order, walker tie-break, or a code path I have not read) that makes star-graph resolution work in practice. R9 takes the conservative position: **don't depend on the star graph resolving correctly. Change v0.9 invocation to produce a linear chain.**

**The fix is more nuanced than R9 stated.** R9 originally proposed editing `RewindInvoker.BuildProvisionalRecording`, but two reviewer findings (R10 P1.H and P1.I) flag that:

- **P1.H -- wrong write site.** `BuildProvisionalRecording` only stamps `provisional.SupersedeTargetId`. The marker fields are assigned LATER in `AtomicMarkerWrite` from `selected.OriginChildRecordingId`. `SupersedeCommit.AppendRelations` reads from the marker (via the subtree closure), not from the provisional. Editing only `BuildProvisionalRecording` leaves the marker rooted at the immutable origin and the graph stays star-shaped. The fix has to land where the marker is actually written.
- **P1.I -- can't repurpose `marker.OriginChildRecordingId`.** Existing v0.9 consumers (`RevertInterceptor.FindSlotForMarker`, in-place continuation paths, ghost suppression keying off `ActiveReFlyRecordingId == OriginChildRecordingId`) treat that field as the slot's immutable origin. Changing its semantic to "prior effective tip" silently breaks Retry resolution and ghost suppression after a chain extension. The fix has to add a new marker field rather than mutate the existing one.

**R10 design.** Add a new marker field; touch the marker-write site, not the provisional-build site:

1. **New field on `ReFlySessionMarker`:** `string SupersedeTargetId` (default null). Stores the slot's effective recording at invocation time -- the linear-append root for `AppendRelations`. `marker.OriginChildRecordingId` keeps its existing contract (= slot's immutable origin) so every existing consumer continues to work.

2. **`AtomicMarkerWrite` change** (`RewindInvoker.cs`):
   ```
   // BEFORE (paraphrased): selected = the ChildSlot the player invoked
   marker.OriginChildRecordingId = selected.OriginChildRecordingId

   // AFTER: compute prior tip ONCE before the in-place vs fresh-provisional
   // branch. Stamp BOTH marker fields unconditionally (shared block; runs
   // on both branches). The provisional overwrite is GUARDED because the
   // in-place branch skips BuildProvisionalRecording -- see step 4.
   string priorTip = selected.EffectiveRecordingId(scenario.RecordingSupersedes)
   marker.OriginChildRecordingId = selected.OriginChildRecordingId   // unchanged
   marker.SupersedeTargetId      = priorTip                          // NEW

   if (provisional != null)                                          // null on in-place branch
       provisional.SupersedeTargetId = priorTip                      // overwrite the
                                                                     // BuildProvisionalRecording
                                                                     // value to stay consistent
   ```
   All writes happen inside the same atomic block as the existing marker assignment; no new save/yield boundaries. The marker-side stamp is what `AppendRelations` reads, so the in-place branch (where `provisional == null`) is still correctly linearized -- step 4 spells this out.

3. **`SupersedeCommit.AppendRelations` change**: re-root the closure walk at `marker.SupersedeTargetId` instead of `marker.OriginChildRecordingId`. Do NOT bypass the closure -- it is load-bearing for downstream behaviour:
   - The closure is returned to `CommitTombstones`, which uses it to scope tombstone-eligible actions to the supersede subtree (v0.9 §6.13). Bypassing the closure on chain-extension would mean kerbal-death tombstones (and bundled rep) from descendants of the prior tip never fire.
   - The closure also covers chain siblings (`RecordingOptimizer.SplitAtSection` HEAD/TIP pairs), PID-peer recordings of the slot, and the mixed-parent halt (v0.9 §4.3 / `EffectiveState.ComputeSessionSuppressedSubtree`). Bypassing it would write supersede relations for the wrong shape.

   **The fix is NOT a one-line root change in `AppendRelations`** -- the closure is computed by `EffectiveState.ComputeSessionSuppressedSubtree(marker)`, which is a **shared helper** that runtime ghost suppression also calls (per the existing-consumer audit in §9.2.1 step 5: ghost playback engine, chain walker, ghost map presence, watch mode all consume the same closure rooted at `marker.OriginChildRecordingId`). Re-rooting that helper globally would change runtime suppression behaviour too -- the audit explicitly says ghost suppression must stay origin-rooted so the player's live re-fly (whose physical visibility is keyed off the slot's identity) doesn't ghost-suppress the wrong subtree.

   **R13 fix: marker-aware helper with explicit root override.** R12's first attempt was a single-arg `ComputeSubtreeClosure(rootRecordingId)`, but the closure algorithm needs more than just a root. `ComputeSessionSuppressedSubtreeInternal` calls `EnqueuePidPeerSiblings`, which uses `marker.InvokedUT` to include only POST-rewind same-PID peers and exclude prior same-vessel history. A root-only helper would either lose PID-peer expansion (re-introducing missed descendants/tombstones) or mis-gate against pre-rewind peers (superseding the wrong recordings).

   The corrected split: extract the existing helper's body into `ComputeSubtreeClosureInternal(ReFlySessionMarker marker, string rootOverride)` -- same marker-context plumbing as today (InvokedUT, cache key, every other gate), with the **root recording** parameterized. The current helper becomes a thin wrapper:

   ```csharp
   public static IReadOnlyCollection<string> ComputeSessionSuppressedSubtree(
       ReFlySessionMarker marker)
       => ComputeSubtreeClosureInternal(marker, marker.OriginChildRecordingId);
   ```

   Two consumers, two call shapes, both pass the marker context:
   - **Runtime suppression** (ghost playback engine, chain walker, ghost map presence, watch mode): continue calling `ComputeSessionSuppressedSubtree(marker)` -- delegates to the internal helper with the slot's immutable origin. **No behavioural change.**
   - **`SupersedeCommit.AppendRelations`**: switch to calling `ComputeSubtreeClosureInternal(marker, marker.SupersedeTargetId ?? marker.OriginChildRecordingId)`. The fallback handles legacy markers (null field on disk) and first-re-fly (priorTip equals origin); chain-extension uses the new field's prior tip. **PID-peer gating still uses `marker.InvokedUT` (unchanged)** so post-rewind peers expand correctly and pre-rewind history stays excluded. `CommitTombstones` receives this closure unchanged; tombstone scope correctly tracks the chain-extension's actual descendants.

   The new helper is a refactor of the existing one (extract method + add a parameter), not a re-implementation. The cache-key inside the helper must include `rootOverride` so the two call shapes don't collide in the cache. Add a unit test asserting `ComputeSubtreeClosureInternal(marker, marker.OriginChildRecordingId)` returns the same result as today's `ComputeSessionSuppressedSubtree(marker)` for arbitrary marker/scenario combinations -- regression guard against the refactor accidentally changing PID-peer / mixed-parent / chain-sibling expansion.

4. **In-place continuation branch inside `AtomicMarkerWrite`.** R10 framed this incorrectly; R12 corrects it. The in-place continuation is NOT a separate code path outside `AtomicMarkerWrite` -- it is a branch INSIDE `AtomicMarkerWrite` that *skips* `BuildProvisionalRecording`, so on that branch `provisional` is null and the `provisional.SupersedeTargetId = priorTip` line in the recipe above does not apply. The recipe's structure must therefore be:
   - **Compute `priorTip` once, BEFORE the in-place vs. fresh-provisional branch:** `string priorTip = selected.EffectiveRecordingId(scenario.RecordingSupersedes)`. Same value for both branches.
   - **Stamp `marker.OriginChildRecordingId` and `marker.SupersedeTargetId` in the shared marker-creation block** (which runs on both branches): `marker.OriginChildRecordingId = selected.OriginChildRecordingId`; `marker.SupersedeTargetId = priorTip`. These two assignments are unconditional within `AtomicMarkerWrite`.
   - **Guard the `provisional.SupersedeTargetId` overwrite to the fresh-provisional path only:** `if (provisional != null) provisional.SupersedeTargetId = priorTip`. On the in-place placeholder path, `provisional` is null and the overwrite is skipped (the marker-side stamp is what `AppendRelations` reads, so the in-place branch is still correctly linearized).

   This makes the recipe complete and removes the misleading "code path outside AtomicMarkerWrite" framing. No external code-spelunking is required because the branching is local to one method.

5. **Existing consumer audit.** Add a §9.2.2 sub-task before promotion: walk every read site of `marker.OriginChildRecordingId` and confirm it should continue to receive the slot's immutable origin (NOT the new prior-tip value). Initial findings from the reviewer:
   - `RevertInterceptor.FindSlotForMarker`: matches against `slot.OriginChildRecordingId` -- needs the immutable origin. ✓ Unchanged.
   - In-place continuation paths: key off `ActiveReFlyRecordingId == OriginChildRecordingId` -- needs the immutable origin. ✓ Unchanged.
   - Ghost suppression: same. ✓ Unchanged.
   - `SupersedeCommit.AppendRelations`: needs the prior tip for linear chain extension -- migrates to the NEW field. ✓ Per (3).

**Doc/code discrepancy resolved.** v0.9 design doc §1.4 describes the linear chain (`SupersedeTargetId = A'.Id` for the second re-fly). The actual v0.9 code does the star (per the reviewer's analysis). The fix above brings the code's append behaviour in line with the doc, without breaking the existing marker contract.

**Migration concern.** Existing saves with star-shaped supersede graphs from prior v0.9 Crashed chain extensions: their `EffectiveRecordingId` walks already silently picked the oldest re-fly. After R10's invocation fix lands, NEW relations are linear, but OLD star relations remain. The walker continues to pick the oldest in the star portion, then walks linearly from there. A migration sweep that converts star to linear (delete duplicate-old relations, keep only the latest) is a nice-to-have but not required for this feature.

**The §9.5 regression test models the linear semantic** (post-fix), with separate tests asserting that the marker-write path stamps both fields correctly AND that every existing consumer of `marker.OriginChildRecordingId` continues to receive the slot's immutable origin.

Both Site A and Site B route through the same `UnfinishedFlightClassifier.Qualifies` entry point with the same triple shape. The shared-classifier identity test in §9.5 forces this -- otherwise the failure mode is that a stable-leaf re-fly gets sealed Immutable at merge while a tree-commit pass would have promoted it, and the row vanishes from Unfinished Flights immediately after the player merges their re-fly.

### 9.3 Reaper

`RewindPointReaper.IsReapEligible` extended one line: a slot is treated as closed when `effectiveRecording.MergeState == Immutable` OR `slot.Sealed == true`. Combined with §9.2's broader CP promotion, the rule remains "every slot closed -> reap-eligible," with the close definition expanded by one term.

Logging on reap:

- `[Rewind] Info: ReapOrphanedRPs: reaped=<R> remaining=<rem> sealedSlotsContributing=<S>` -- the new `sealedSlotsContributing` counter logs how many of the closed slots reached closure via the Seal path vs. the Immutable path. Useful for understanding player behaviour during playtest.

### 9.4 UI

`UI/RecordingsTableUI.cs` `DrawUnfinishedFlightRewindButton` ([line 2559](../../../Source/Parsek/UI/RecordingsTableUI.cs)) gains a second action -- Seal -- per one of the three layouts proposed in §7.0. The chosen layout drives whether `ColW_Rewind` widens, a new column is added, or a kebab menu is introduced.

Seal handler:

1. Confirmation dialog (`PopupDialog.SpawnPopupDialog`) per §7.1, with input lock `ParsekUFSealDialog`.
2. On accept: locate the matching slot via `TryResolveRewindPointForRecording(rec, out rp, out slotListIndex)` ([RecordingsTableUI.cs:2802](../../../Source/Parsek/UI/RecordingsTableUI.cs)); set `rp.ChildSlots[slotListIndex].Sealed = true` and `SealedRealTime = DateTime.UtcNow.ToString("o")`; `BumpSupersedeStateVersion`; the reaper's next pass will free the RP if all siblings are now Immutable-or-Sealed. **MergeState is NOT touched** -- see §3 P1.1.
3. Log `[UnfinishedFlights] Info: Sealed slot=<idx> rec=<rid> bp=<bpId> rp=<rpId> terminal=<state> reaperImpact=<willReap|stillBlocked>`.
4. On cancel: log `[UnfinishedFlights] Info: Seal cancelled rec=<rid>`.

Tooltip update on the group itself ("Vessels and kerbals that ended up in a state where you might want to re-fly them..."). The existing tooltip is set in `UnfinishedFlightsGroup` -- name the file at design-doc time so a planner doesn't have to grep.

### 9.5 Tests

Round-trip tests:

- `ChildSlot.Sealed` + `SealedRealTime` round-trip through ConfigNode save/load; legacy `CHILD_SLOT` ConfigNodes without the keys load with `Sealed = false`.
- `RewindPoint.FocusSlotIndex` round-trip; legacy `POINT` ConfigNodes load with `FocusSlotIndex = -1`.

Predicate unit tests:

- Controllable-subject gate: `IsDebris == true` -> false; `IsDebris == false` -> proceed.
- TerminalOutcomeQualifies for each `TerminalState` enum value, with both `isFocus = true` and `isFocus = false` permutations -- 8 states x 2 = 16 cases. Plus null-terminal -> false. Plus the EVA exception (kerbal branch) for each.
- Per-RP leaf gate: chainTip with `ChildBranchPointId == null` -> proceed; chainTip with `ChildBranchPointId == matchingRP.BranchPointId` (breakup-survivor) -> proceed; chainTip with `ChildBranchPointId == otherBp.Id` -> rejected.
- Slot-closed gate: `slot.Sealed == false` -> proceed; `slot.Sealed == true` -> rejected.
- Legacy `Immutable` crash row preserved: `MergeState = Immutable`, terminal Crashed, slot Sealed = false, FocusSlotIndex = -1 -> qualifies (P1.1 regression guard).

Site A / Site B promotion tests:

- `ApplyRewindProvisionalMergeStates` (Site A): stable-leaf non-focus controllable -> CP; stable-leaf focus controllable -> Immutable; stable-leaf debris -> Immutable; crashed-leaf focus -> CP (active-parent crash regression guard); crashed-leaf non-focus -> CP.
- `SupersedeCommit.FlipMergeStateAndClearTransient` (Site B): re-fly ending Orbiting + non-focus slot -> CP; re-fly ending Orbiting + focus slot -> Immutable; re-fly ending SubOrbital + non-focus -> CP; re-fly ending EVA-stranded Landed -> CP (kerbal branch ignores focus); re-fly ending Landed (vessel) -> Immutable; re-fly ending Crashed -> CP (regression).
- **R10 marker-write linearization (P1.G/H/I prerequisite):** invoke a re-fly into a slot whose effective recording is `probeReFly1` (one prior re-fly already in the supersede chain). After `AtomicMarkerWrite` runs, assert: `marker.OriginChildRecordingId == probeOrig` (UNCHANGED -- slot's immutable origin); `marker.SupersedeTargetId == probeReFly1` (NEW field, prior tip); `provisional.SupersedeTargetId == probeReFly1`. After Phase 2's `AppendRelations` runs, assert one new relation `{probeReFly1 -> provisional.RecordingId}` appended (linear, NOT a duplicate-old star relation).
- **R11 closure-root linearization (P1.J prerequisite):** invoke chain-extension on a slot whose prior tip has descendants (chain siblings via `RecordingOptimizer.SplitAtSection`, PID peers, or a mixed-parent BP). Assert that `AppendRelations` walks the closure rooted at `marker.SupersedeTargetId` (= prior tip) and writes one supersede relation per closure member, all pointing to `provisional`. Assert `CommitTombstones` receives the same closure (verify by injecting a kerbal-death `GameAction` whose `RecordingId` is in the prior-tip subtree and asserting it gets a `LedgerTombstone` after the merge). Negative variant: build the same scenario where the closure is INCORRECTLY rooted at `marker.OriginChildRecordingId` (the immutable origin) -- a tombstone-eligible action in the prior tip's subtree but NOT in the origin's subtree would not get a tombstone; assert the test fails in that mis-rooted state.
- **First-re-fly closure equivalence:** invoke a first re-fly into a slot (no prior chain extension). Assert `marker.SupersedeTargetId == marker.OriginChildRecordingId == slot.OriginChildRecordingId`. Assert the closure rooted at SupersedeTargetId is byte-identical to the closure rooted at OriginChildRecordingId. Assert AppendRelations writes the same set of relations as a v0.9 first-re-fly merge would have written. (Regression guard: the new code path reduces to the old code path when there's no chain extension.)
- **Existing consumer audit (P1.I regression guard):** for each existing read site of `marker.OriginChildRecordingId` -- `RevertInterceptor.FindSlotForMarker`, in-place continuation, ghost suppression -- assert that the value the consumer reads is still the slot's immutable origin (`probeOrig`), not the new prior-tip (`probeReFly1`). Specifically: build a chain-extension scenario, exercise Retry from RevertInterceptor, and assert the slot resolves correctly; exercise an in-place continuation path and assert it routes via the slot's origin; exercise ghost suppression and assert the supressed-subtree closure starts from the slot's origin.
- **Site B chain-extension slot resolution** (R7 P1.C / R8 P1.E / R9 P1.G regression guard): build a linear supersede chain `{probeOrig -> probeReFly1, probeReFly1 -> provisional}` (matches the post-§9.2.1 invocation semantic). At Site B's slot-resolution step (during Phase 4 Finalize, after Phase 2 AppendRelations has appended the second relation), assert that `TryResolveRewindPointForRecording(provisional, ...)` returns the slot whose `OriginChildRecordingId == probeOrig`. Negative variant: build the same scenario with a STAR graph `{probeOrig -> probeReFly1, probeOrig -> provisional}` (the pre-§9.2.1 buggy state); assert that the helper returns -1 for `provisional` -- this is the failure mode the linearization fixes, and the test's role is to make a regression in the invocation path loud.
- **In-place continuation marker-stamp coverage** (P1.H / R12 P2.M follow-on): exercise both branches of `AtomicMarkerWrite` (fresh-provisional and in-place placeholder). Assert that in BOTH cases, `marker.SupersedeTargetId == priorTip` and `marker.OriginChildRecordingId == slot.OriginChildRecordingId`. Assert that `provisional.SupersedeTargetId == priorTip` on the fresh-provisional branch and is unset (provisional is null) on the in-place branch -- and that both branches result in the same linear supersede append because `AppendRelations` reads from the marker.
- Shared-classifier identity test: assert that the Site A and Site B paths resolve the same answer on the same `(Recording, ChildSlot, RewindPoint)` triple for every terminal-state value (forcing both call sites through the shared helper -- catches predicate drift).

Seal handler tests:

- Slot.Sealed flip + SealedRealTime stamp; `SupersedeStateVersion` bumps; `MergeState` UNCHANGED.
- `RewindPointReaper.IsReapEligible`: slots Immutable + Sealed -> reap; any unsealed CP slot -> no-reap; any NotCommitted -> no-reap (regression).
- Legacy unsealed `Immutable` crash row -> Seal flips slot.Sealed without touching the recording -> reaper now treats slot as closed -> RP eventually reaps.
- Sealing one of N siblings with the others still CP -> log emits `reaperImpact=stillBlocked`; sealing the last one -> `reaperImpact=willReap`.

`RewindPointAuthor` tests:

- `FocusSlotIndex` set correctly when the active vessel matches one of the post-split slots (normal split with focus-continuation slot).
- `FocusSlotIndex == -1` when no slot matches (e.g., normal split where the active vessel is the pre-split parent that is NOT in the slot list).
- Legacy save loaded -> `FocusSlotIndex == -1` -> `TerminalOutcomeQualifies` short-circuits Orbiting/SubOrbital to false. Crashed continues to qualify (v0.9 parity). **Stranded EVA also qualifies, which is NEW behaviour** -- v0.9 only surfaced Crashed; the EVA branch returns before the focus-signal short-circuit, so legacy live-RP non-Boarded EVAs newly appear post-upgrade. Migration is forward-only for vessels (no new Orbiting/SubOrbital rows from legacy splits) but retroactive for stranded EVA kerbals (intentional carve-out, see §3 + §9.2 + §10).

Migration tests:

- Legacy save with already-reaped split RPs has empty Unfinished Flights for those splits (no retroactive surfacing).
- Legacy save with live RPs whose Landed siblings are Immutable: row count unchanged from v0.9 (no Park-from-not-UF in v1; the predicate excludes Landed).
- Legacy save with live RPs whose Orbiting non-focus siblings are Immutable: `ApplyRewindProvisionalMergeStates` does NOT re-promote them. The `FocusSlotIndex == -1` short-circuit in `TerminalOutcomeQualifies` returns false for Orbiting/SubOrbital, so legacy stable-leaf rows stay Immutable across the upgrade. **Forward-only migration.** CHANGELOG: "This feature only surfaces splits made after the upgrade. Pre-upgrade missions where you deployed probes or stages and left them parked are not retroactively converted to Unfinished Flights -- the original feature decision deliberately avoided guessing focus on legacy data."
- Legacy save with live RPs whose Crashed siblings are CP / Immutable: continues to qualify (matches v0.9). Regression guard.
- Post-upgrade fresh deploy: spawn a multi-controllable split, leave probes orbiting, commit. Verify the new RP has `FocusSlotIndex >= 0`, the probes promote to CP, and the row appears in Unfinished Flights -- the motivating case works for new RPs.
- **Legacy stranded EVA does surface (R8 P1.F regression guard):** load a synthetic save with a legacy live RP whose Immutable EVA child has terminal `Landed` and `EvaCrewName != null`. First post-upgrade `ApplyRewindProvisionalMergeStates` run promotes the EVA recording to CP and the row appears in Unfinished Flights. This is the **intentional** retroactive EVA carve-out (§3 + §9.2). Same scenario with `EvaCrewName == null` (i.e. an Immutable Landed vessel, not a kerbal): row does NOT surface (forward-only for vessels).
- BG-only split (no focused slot): synthetic RP with `FocusSlotIndex == -1` (set explicitly, not legacy), Orbiting controllable sibling, Immutable. Verify the row does NOT surface (R7 noFocusSignal short-circuit). Same RP with a Crashed sibling: row DOES surface (Crashed bypasses the short-circuit).

In-game tests:

- Deploy 4 probes (S1), fly mothership home, return to Recordings Manager, verify all 4 in Unfinished Flights AND mothership NOT in UF, Fly probe #2, land it, merge, verify slot closure + supersede chain.
- Auto-parachute booster (S3, S19), verify NOT in Unfinished Flights and RP reaps cleanly. **Critical: this test is the regression guard against R4's broken predicate.**
- Inverted scenario (S19b): fly booster, leave upper stage Orbiting, verify upper stage IS in UF and RP stays alive.
- Stranded EVA (S5): kerbal stranded + lander Orbiting -> both in UF; Fly kerbal, reboard, merge -> kerbal slot closes; lander slot still in UF; Seal lander to clean up.
- Breakup-survivor (S22): trigger a breakup, survive, land safely -> NOT in UF and RP reaps. Trigger another, survive but Orbiting -> NOT in UF (focus exclusion). Trigger a third, crash post-survival -> IS in UF (Crashed regardless of focus).
- Cross-tree dock during stable-leaf re-fly (S21): probe re-fly docks with another tree's station, merge, verify slot closes Immutable, supersede stays inside probe's tree, station's tree unchanged.
- Re-fly chain extension on a stable terminal: park probe, re-fly to a different stable orbit, merge, verify slot stays CP and UF still shows the probe with the new flight as effective; re-fly again, land it, merge, verify slot now Immutable.
- Seal then unseal-attempt: verify there's no in-game un-seal path (Full-Revert is the only undo).

### 9.6 Documentation

- Update `parsek-rewind-to-separation-design.md` §1.2 ("Out of scope: stable-end splits") and §7.31 ("intended behaviour, not a limitation") -- both are reversed by this extension.
- New `parsek-unfinished-flights-extension-design.md` (or fold into a v0.10 revision of the rewind-to-separation doc) once this research note is promoted.
- `roadmap.md` entry under v0.10+.
- CHANGELOG: "Unfinished Flights now also surfaces stable leaves left hanging -- probes deployed and forgotten in orbit, stranded EVA kerbals. Each row now has a Fly button and a Seal button (Seal marks the slot final so the rewind point can be cleaned up). Vessels that auto-parachuted to a safe landing still don't appear (they reached a stable conclusion)."

---

## 10. Risks

- **Disk usage growth.** Stable-leaf RPs persist until Sealed or fully closed. Bounded by player diligence with the Seal button + by the FocusSlotIndex gate (which prevents routine focus-continuation upper stages from inflating the count). Mitigation: Settings -> Diagnostics already shows total RP disk usage; consider splitting into Crashed vs Stable-Unconcluded counts.
- **Over-inclusion (S5 lander, S6, S17, S18).** The simple terminal-state classifier surfaces some cases where the player meaningfully flew or didn't intend to leave a vessel and it ended Orbiting / SubOrbital. The player Seals them. This is the explicitly-chosen v1 trade-off; the Seal affordance IS the design's answer. Do not re-introduce voluntary-action heuristics -- that path was rejected upstream of R3.
- **Predicate drift between Sites A and B.** The two MergeState-promotion call sites (§9.2) must use a shared classifier helper that takes `(Recording, ChildSlot, RewindPoint)`. If they diverge, a player merging a stable-leaf re-fly sees the row vanish immediately at merge (Site B sealed Immutable) even though a tree-commit pass (Site A) would have promoted it. The shared-classifier identity test in §9.5 guards against this.
- **Reversal of v0.9 §7.31 stance.** v0.9 said stable-end splits don't get a row. This feature says some non-focus stable-end splits do (Orbiting non-focus, SubOrbital non-focus, EVA-stranded). Focus-continuation stable terminals continue to NOT get a row -- this preserves v0.9's "your mission's upper stage didn't suddenly become unfinished" intuition. CHANGELOG language must be precise to avoid mid-mission surprise.
- **Focus attribution at split time.** `RewindPoint.FocusSlotIndex` depends on `RewindPointAuthor.Begin` being able to identify the focused vessel and match it to a slot. Edge cases that produce `FocusSlotIndex = -1`: (a) legacy RPs that predate the field; (b) a new split fired by KSP on a non-focused vessel (e.g. background joint break or a split where the player was focused on an unrelated vessel). Per R7's `noFocusSignal` short-circuit, both cases conservatively suppress Orbiting/SubOrbital qualification, so BG-only splits with stable-orbit siblings produce NO UF rows -- only Crashed and EVA-stranded surface from such RPs. This is the cost of forward-only migration; v2 Park-from-not-UF is the escape hatch for the player who wants to re-fly a BG-only-split orbiting sibling. Worth in-game test coverage to confirm the suppression behaves correctly (and does not accidentally hide legitimate Crashed siblings).
- **Legacy migration is forward-only for vessels, retroactive for stranded EVAs.** Pre-upgrade BG-recorded vessels left in stable orbit stay `Immutable` after upgrade -- the `FocusSlotIndex == -1` short-circuit suppresses Orbiting/SubOrbital because legacy RPs have no focus signal. But the EVA branch in `TerminalOutcomeQualifies` returns BEFORE that short-circuit, so a legacy live RP with a stranded (non-Boarded terminal) EVA kerbal newly surfaces post-upgrade. **This is intentional**: stranded kerbals are unambiguous and low-volume; orbital siblings are ambiguous and potentially high-volume. CHANGELOG must split the migration story into two notes: (1) "vessels left in past missions do NOT retroactively appear"; (2) "stranded EVA kerbals from past missions DO retroactively appear so you can attempt rescue."
- **EVA stranded edge cases.** What if the kerbal is dead (suit ran out)? `TerminalState` for a dead EVA kerbal might be Destroyed; that's already UF via the Crashed path. What if KSP unloaded the kerbal mid-EVA? The finalization-cache work in `parsek-recording-finalization-design.md` should give a reliable terminal; depends on that work being solid.
- **Optimizer chain length.** S16 / §2.1 -- chain walks unbounded in eccentric-orbit case. Performance risk on a save with many such vessels; the chip-spawned investigations should fix the optimizer side independently.

---

## 11. Clarifications -- ALL CLOSED

- **Q1 (SOI semantics).** Closed in R2: "voluntary SOI traversal disqualifies." Effectively folded into R3's terminal-state algorithm (a probe that left its SOI is in a different SOI's Orbiting state -> still UF; a probe that the player flew to Mun and parked is also Orbiting -> UF; both treated identically).
- **Q2 (rover drive / "further used" definition).** Closed in R3: NOT distinguished by an algorithm. Rover terminal Landed -> NOT UF by default. Player accepts or (in v2) Parks. v1 doesn't have Park -- player accepts.
- **Q3 (yes-to-all leaves vs. opt-in).** Closed in R3: terminal-state-based default. Crashed/Orbiting/SubOrbital/EVA-not-boarded -> auto-UF. Landed/Splashed/Recovered/Docked -> auto-not-UF. Controllable-subject gate excludes pure debris.
- **Q4 (park vs seal at merge).** Closed in R3: auto-classify by §3 algorithm; per-row Seal button is the cleanup affordance. No merge-time dialog friction.
- **Q5 (rewind-to-RP mechanism).** Closed in R2: yes, existing mechanism.
- **Q6 (EVA leaves first-class).** Closed in R2: yes, included via the EVA exception in `TerminalOutcomeQualifies`.
- **Q7 (group naming).** Closed in R2: stays "Unfinished Flights." Tooltip text updated.
- **Q-additional (v1 detection scope).** Closed in R3: no A1/A2/A4 instrumentation needed. Algorithm is purely terminal-state; over-inclusion handled by Seal button.
- **R3-add (RP retention).** Closed: keep an RP alive while any sibling could re-fly. Reaper's existing "all slots Immutable" rule already enforces this once §9.2's CP promotion fires.

---

## 12. Recommendation

R13 is ready to promote to a formal design doc, modulo one explicit unresolved item: §7.0's UI layout choice (widen column / new column / kebab menu). R10's earlier "in-place continuation enumeration" open item was closed in R12 -- the in-place branch is local to `AtomicMarkerWrite` and the recipe in §9.2.1 step 4 covers both branches without external code spelunking. The shape:

- **Prerequisite v0.9 marker-write change + closure-helper split**: `RewindInvoker.AtomicMarkerWrite` (NOT `BuildProvisionalRecording`) computes `priorTip = selected.EffectiveRecordingId(supersedes)` once before the in-place vs fresh-provisional branch; stamps `marker.OriginChildRecordingId` (slot origin, unchanged contract) and `marker.SupersedeTargetId` (NEW, prior tip) in the shared marker-creation block; the `provisional.SupersedeTargetId = priorTip` overwrite is guarded with `if (provisional != null)` (the in-place branch sets provisional null). Existing `ComputeSessionSuppressedSubtree(marker)` is refactored: body extracts into `ComputeSubtreeClosureInternal(marker, rootOverride)` (preserves all marker-context gates: InvokedUT for PID-peer, mixed-parent halt, chain-sibling expansion); the existing wrapper delegates with `marker.OriginChildRecordingId`. `SupersedeCommit.AppendRelations` calls the internal helper with `marker.SupersedeTargetId ?? marker.OriginChildRecordingId`. Runtime ghost suppression is unchanged. See §9.2.1.
- **Three new persistent fields**: `ChildSlot.Sealed` (+ diagnostic `SealedRealTime`); `RewindPoint.FocusSlotIndex` (int, -1 default); `ReFlySessionMarker.SupersedeTargetId` (string, null default). All back-compat.
- **Predicate**: keep v0.9's `MergeState in { Immutable, CommittedProvisional }`; add `IsDebris == false` controllable-subject gate; add per-RP-context leaf gate (chainTip ChildBranchPointId is null OR equals the matched RP's BranchPointId); add `slot.Sealed == false`; replace terminal-Crashed-only with `TerminalOutcomeQualifies(chainTip, slot, RP)` per §3. Shared classifier helper used by both call sites.
- **MergeState promotion at TWO sites**: `RecordingStore.ApplyRewindProvisionalMergeStates` (Site A) AND `SupersedeCommit.FlipMergeStateAndClearTransient` (Site B), both routing through the shared classifier. Reaper extended one term ("Immutable OR Sealed").
- **UI**: per-row Seal action alongside the existing Fly button. Layout TBD (§7.0). Confirmation dialog with destructive-action language (§7.1). Tooltip refresh.
- **Logging**: new `[UnfinishedFlights]` reasons covering each predicate gate; new `[Rewind]` reaper line with `sealedSlotsContributing` counter; `[UnfinishedFlights]` Seal accept/cancel logs; `[Supersede]` log emits the new `qualifies` field on Site B's flip.
- **Tests**: unit + in-game per §9.5, including: shared-classifier identity test (Site A/B drift guard); legacy-Immutable-crash regression guard (P1.1); breakup-survivor guard (P1.2); routine-launch-reap regression guard (P1.3); R9 invocation linearization guard + Site B chain-extension positive/negative test (P1.G).
- **Disk policy**: relies on player to Seal; Settings diagnostic surfaces total usage; consider Crashed/Stable-Unconcluded breakdown.

**Promotion to a formal design doc requires resolving §7.0 (UI layout) and confirming §9.2.1 (v0.9 invocation rewrite is in scope, not a separate v0.9 fix PR).** If the v0.9 invocation change is treated as a prerequisite separate-PR rather than part of this feature, the feature blocks on that PR landing first.

Promote this to `Parsek/docs/parsek-unfinished-flights-stable-leaves-design.md` (or merge into a v0.10 revision of the rewind-to-separation doc) following the design-doc template in `development-workflow.md` step 3. Resolve the §7.0 UI layout question with a UX mock during the design-doc phase. Plan + build cycle follows.

---

*End of research note R13.*
