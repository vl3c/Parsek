# Research: Extending Unfinished Flights to Stable, Unconcluded Leaves

*Investigation doc, started 2026-04-27, R3 same day. Per the dev workflow, this lives in step 1-2 territory: vision + scenario simulation. R3 closes all open clarifications; the next step is to promote this to a formal design doc.*

*Reads against: `parsek-rewind-to-separation-design.md` (the v0.9 source of truth), `parsek-recording-finalization-design.md`, `parsek-flight-recorder-design.md`, `parsek-timeline-design.md`. Code spot-checks against `EffectiveState.cs`, `TerminalKindClassifier.cs`, `RecordingStore.cs`, `RewindPointReaper.cs`, `BranchPoint.cs`, `Recording.cs`, `RecordingOptimizer.cs`.*

---

## 0. Revision history

- **R1.** Proposed a separate "Parked Flights" virtual UI group alongside Unfinished Flights, with a meaningful-action filter (A1 body change / A2 mid-chain surface / A4 orbit shift) on stable leaves.
- **R2.** Per user feedback: one Unfinished Flights group, broadened to include "stable leaves not finished on purpose." Multi-recording chain handling. Voluntary-vs-involuntary detection via A1+A2 in v1, A4 deferred to v1.1. Eccentric-orbit optimizer concern flagged as separate investigation.
- **R3 (this version).** All R2 clarifications closed. The A1/A2/A4 voluntary-action heuristics are dropped entirely. The classifier is now purely terminal-state-based with per-row Seal override. UI: the Rewind column splits into Fly + Seal buttons. Filing the Park-from-not-UF affordance (the rover-drive override) as v2 future work to keep v1 scope tight.

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
  index 0  exo (Kerbin orbit phase)              <- HEAD, ParentBranchPointId = undock
  index 1  exo (Mun SOI cruise)                  <- body change at SOI entry
  index 2  surface (Mun landed)                  <- env class change
  index 3  exo (back in orbit after takeoff)     <- env class change
  index 4  exo (Kerbin SOI return)               <- TIP, ChildBranchPointId = null
```

Leaf detection MUST walk to the chain TIP and check that TIP's `ChildBranchPointId == null`. `EffectiveState.ResolveChainTerminalRecording` already does this walk for the v0.9 predicate. Same helper feeds the broadened predicate.

**Important corollary:** the HEAD's own `ChildBranchPointId` is non-null (it points to the next chain segment). A naive `rec.ChildBranchPointId == null` check would mis-classify every chain HEAD. The chain-aware walk fixes this.

### 2.1 Optimizer concerns (separate investigation)

Two related concerns the user flagged, both filed as separate investigation tasks (chips):

1. **Eccentric-orbit chain bloat.** An on-rails BG vessel with periapsis inside atmosphere may emit `Atmospheric` and `ExoBallistic` samples on alternating orbits, causing the optimizer to split each pair. Chain length grows unboundedly. The fix is in the optimizer's split rule.
2. **Meaningful-split-only redesign.** Broader principle: the optimizer should only split at env-class boundaries that correspond to *real* gameplay events (launch, re-entry, landing, take-off, destruction), not passive geometric crossings. Catalogue every (from, to) env-class transition pair and find a discriminator (focus history, thrust at crossing, on/off-rails state, nearby part events) that separates meaningful from passive.

For the leaf-extension feature, both concerns affect chain-walk performance but not correctness. `ResolveChainTerminalRecording` finds the same TIP regardless of chain length. Ship this feature on top of whichever optimizer behaviour is current; expect the optimizer fix to land independently.

---

## 3. The default classifier (R3 -- final shape)

R1's "Parked Flights" group is dropped (one group, called Unfinished Flights). R2's voluntary-action heuristics (A1/A2/A4) are dropped (terminal-state-based default + manual override is simpler and matches the user's framing). What remains:

```
IsUnfinishedFlight(rec) :=
    // Structural leaf gate -- unchanged from v0.9
    rec is in ERS
    AND rec.MergeState == CommittedProvisional       // slot still open
    AND ResolveChainTerminalRecording(rec).ChildBranchPointId == null
    AND parent (or active-parent-child) BranchPoint has a live RP with a slot for rec

    // Controllable-subject gate -- new in this feature
    AND chain HEAD vessel had a working ControllerInfo at start
        (ProbeCore, CrewedPod, KerbalEVA, ExternalSeat with crew)
        // Pure debris (no controller from the moment of split) is excluded.
        // A vessel whose only crew evac'd / died after the split is also
        // out of scope -- it's not re-flyable in any meaningful sense.

    // Outcome gate -- new in this feature
    AND TerminalOutcomeQualifies(chainTip)

TerminalOutcomeQualifies(chainTip) :=
    let kerbal = chainTip is an EVA kerbal recording
    let terminal = chainTip.TerminalStateValue

    if kerbal:
        return terminal != Boarded
        // EVA kerbals: any non-Boarded terminal is unfinished (stranded on
        // surface, drifting in orbit, dead). The Boarded case isn't reached
        // here anyway -- a Board BP makes the recording non-leaf via the
        // structural gate. Listed for completeness.

    if terminal == Destroyed: return true   // Crashed -- the v0.9 case
    if terminal == Orbiting:  return true   // left in flight
    if terminal == SubOrbital: return true  // left in flight (vacuum arc; atmospheric SubOrbital is reclassified to Destroyed pre-commit by BallisticExtrapolator)

    // Landed / Splashed / Recovered / Docked: the universe gave the vessel
    // a stable conclusion. Player did not say "I'm done with it" but the
    // outcome looks done enough that the default is to NOT auto-add a row.
    // Player can manually invoke Park (v2) to add the row anyway.
    return false
```

Plus a **manual Seal override** on each Unfinished Flight row that flips the recording from `CommittedProvisional` to `Immutable`, removes it from the group, and lets the RP reaper free the quicksave when all siblings are also sealed (or otherwise Immutable).

Plus, deferred to v2: a **Park override** on rows that the default classifier would NOT include (a Landed rover the player wants re-flyable later). v1 ships without this; if playtest shows demand, add later. Per user: "without getting caught up in edge cases and heuristics."

---

## 4. The hard rule, restated

> A recording does not qualify for re-fly if it has any downstream BranchPoint after its
> chain TIP. Period. The structural-leaf gate enforces this; nothing else can override it.

In code:

```
ForbidRefly(rec) := ResolveChainTerminalRecording(rec).ChildBranchPointId != null
```

Whether a leaf shows in the UI is a separate question (the default classifier + Seal override). But re-fly is structurally forbidden the moment the recording has a downstream BP -- dock, board, undock, joint break, EVA, breakup. No exception.

---

## 5. RP retention and reaping

User confirmed (R3): **keep an RP alive while any sibling could re-fly.** Concretely: an RP becomes reap-eligible only when every child slot's effective recording is `Immutable`. The new feature changes which slots reach Immutable at commit time, not the reaper rule itself.

Today (`RecordingStore.ApplyRewindProvisionalMergeStates`, [RecordingStore.cs:734](../../../Source/Parsek/RecordingStore.cs)):

```
if rec.terminal classifies as Crashed AND parent BP has live RP with slot:
    promote rec to CommittedProvisional
otherwise:
    leave Immutable
```

After this feature:

```
if rec is structurally a leaf
   AND rec is controllable
   AND parent BP has live RP with slot
   AND TerminalOutcomeQualifies(chainTip):
       promote rec to CommittedProvisional        // shows in Unfinished Flights
otherwise:
    leave Immutable                               // RP can reap when all slots Immutable
```

Disk impact:

- 4-probe scenario (S1): all 4 probes promote to CP. RP stays alive indefinitely until the player Seals all 4 (or successfully re-flies all 4 to Immutable terminals).
- Auto-parachuting booster (S3): TerminalOutcomeQualifies returns false (Landed). Booster commits Immutable. If the upper stage is also Immutable (Orbited, the player's mission), all slots are Immutable -> RP reaps. Disk freed.
- Crashed booster (S2): unchanged from v0.9.

This is the right disk-cost shape: the player only pays for slots that actually need keeping. The Seal button becomes the player's escape hatch when they're done with a parked slot.

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

**Rewind-column split.** Per user: in the Recordings Manager, the Unfinished Flights row's Rewind cell splits into two side-by-side controls:

| Cell | Button | Effect |
|---|---|---|
| Fly | "Fly" | Routes through `RewindInvoker.StartInvoke` (existing v0.9 flow). Reloads the RP quicksave, strips siblings, activates this recording's vessel, scene reload. |
| Seal | "Seal" | Spawns the Seal confirmation dialog (see below). On accept: flip `MergeState` to `Immutable`; bump `SupersedeStateVersion`; row drops from group; reaper runs (RP deleted if all sibling slots are also closed). |

Fly is the primary action (left); Seal is secondary (right). Same column width as today's single Rewind cell -- compress button text or use icons if needed.

The crashed-row UX (today's v0.9) becomes the same split-cell layout. A crashed row's Seal action is "I accept the crash as canonical; stop offering me the re-fly." Same semantics; works identically for both default-UF flavours (Crashed and Stable-Unconcluded).

### 7.1 Seal confirmation dialog

The Seal action is **destructive and irreversible** -- once a recording is sealed, the slot closes permanently and the rewind point's quicksave can be deleted by the reaper. The player has no in-game path to un-seal a recording (a Full-Revert of the entire tree is the only way back, and that loses every other commit on the tree too).

A `PopupDialog.SpawnPopupDialog` with a `MultiOptionDialog` body. Title: "Seal Unfinished Flight?" Body copy:

```
Seal "<vessel-name>" (<terminal-state> at UT <ut>)?

This action CANNOT BE UNDONE.

After sealing:
  - This recording becomes immutable -- it can never be re-flown.
  - The "Fly" button on this row disappears.
  - The rewind point's quicksave file may be deleted (when every
    sibling of this split is also sealed or already finalized).
  - The recording remains in your timeline and continues to play
    back as a ghost on any future rewind, exactly as it does now.
    Sealing only closes the re-fly opportunity; it does not erase
    the recording.

If you might want to re-fly this later, click Cancel.
```

Buttons: `Seal Permanently` (red / destructive style, fires the seal handler) and `Cancel` (logs `[UnfinishedFlights] Info: Seal cancelled rec=<rid>` and dismisses).

The dialog takes an input lock (`DialogLockId = "ParsekUFSealDialog"`) while visible so the player cannot click other UI controls during the decision.

Logging on accept: `[UnfinishedFlights] Info: Sealed rec=<rid> bp=<bpId> rp=<rpId> terminal=<state> reaperImpact=<willReap|stillBlocked>` -- the `reaperImpact` field tells a log reader whether this seal triggered the RP cleanup or whether sibling slots are still keeping the RP alive.

**No Park affordance in v1.** Rows that the default classifier excluded (rover-drove-20m, briefly-nudged-probe-still-Landed) cannot be added back to Unfinished Flights in v1. This is intentional scope-trimming per user direction. v2 may add a Park button on the corresponding row in the main Recordings table to manually opt a Landed/Splashed/Recovered leaf into the group.

---

## 8. Gameplay scenarios (R3 verdicts)

Each scenario: setup -> default classifier verdict -> note. The R3 algorithm is small enough that most scenarios resolve trivially.

### S1. Four probes deployed simultaneously from Mun mothership

Probes terminal Orbiting -> `TerminalOutcomeQualifies` true -> promote to CP -> appear in Unfinished Flights. Player can Fly any probe individually; Seal individually.

### S2. Booster recovery (crashed)

Unchanged from v0.9. Crashed -> UF.

### S3. Booster auto-parachutes successfully

Terminal Landed (or Splashed) -> `TerminalOutcomeQualifies` false -> Immutable -> NOT in Unfinished Flights. RP reaps when upper stage also Immutable. **R3 reverses the R2 reading of this case** to match user direction: "controller landed = reached stable state = not unfinished."

### S4. EVA kerbal plants flag, reboards

Board BP -> structural leaf gate fails (chain TIP `ChildBranchPointId != null`). Not a leaf. Not UF. ForbidRefly true. ✓

### S5. EVA kerbal plants flag, abandoned (no reboard)

EVA recording chain TIP terminal Landed. EVA kerbal -> `TerminalOutcomeQualifies` true (kerbal not Boarded). UF. Player can Fly to attempt reboard, or Seal to accept the loss.

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

If re-fly ends Orbiting (player parked in a different orbit): `Classify` returns InFlight, which today maps to Immutable in `SupersedeCommit.FlipMergeStateAndClearTransient`. Slot closes. The new flight is "deliberate parking, sealed." If player wants it re-flyable AGAIN, they'd need a fresh undock -- which is consistent with v0.9 chain-extension semantics (only Crashed extends the chain).

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

Booster has parachutes + probe core (controllable). Player toggles chute, returns to upper stage, booster lands safely. Chain TIP terminal Landed. NOT UF (controllable + Landed). RP reaps when upper stage commits. ✓ Matches user's Q3 example.

### S20 (R3-new). Same as S19 but booster is uncontrollable (no probe core)

Booster has parachutes only, no controller. At split time, the controllable-subject gate at chain HEAD fails. NOT UF. Booster is debris, may not even produce a multi-controllable RP at all (`SegmentBoundaryLogic.IsMultiControllableSplit` requires count >= 2 controllables). ✓ Matches user's Q3 example.

---

## 9. Data-model and code touchpoints

### 9.1 Predicate

`EffectiveState.IsUnfinishedFlight` extended per §3:

- Add a controllable-subject check at chain HEAD.
- Add a `TerminalOutcomeQualifies(chainTip)` outcome check.
- The structural-leaf gate (existing) and slot-still-open gate (existing) are unchanged.

Logging additions (`[UnfinishedFlights] Verbose`):

- `IsUnfinishedFlight=false rec=<rid> reason=notControllable controller=<type|null>`
- `IsUnfinishedFlight=false rec=<rid> reason=stableTerminal terminal=<state>`
- `IsUnfinishedFlight=true rec=<rid> reason=stableLeafUnconcluded terminal=<state>`
- `IsUnfinishedFlight=true rec=<rid> reason=stranded EVA terminal=<state>`

### 9.2 MergeState promotion

`RecordingStore.ApplyRewindProvisionalMergeStates` extended:

```
if rec.MergeState != Immutable: continue
if NOT structurally a leaf: continue
if NOT controllable at HEAD: continue
if parent BP doesn't have live RP with slot for rec: continue
if NOT TerminalOutcomeQualifies(chainTip): continue
promote rec to CommittedProvisional
log [UnfinishedFlights] Info: CommitTree: promoted ...
```

The existing crash-only path is folded into the more general predicate; same call site.

### 9.3 Reaper

No change. "All slots Immutable" rule still works correctly given §9.2's broader CP promotion.

### 9.4 UI

`UI/RecordingsTableUI.cs` (or wherever the Unfinished Flights row currently draws its Rewind button): split the cell into Fly + Seal.

Seal handler:

1. Confirmation dialog (`PopupDialog.SpawnPopupDialog`).
2. On accept: `rec.MergeState = MergeState.Immutable`; clear any transient session fields; `BumpSupersedeStateVersion`; `BumpStateVersion` on the store; the reaper's next pass will free the RP if all siblings now Immutable.
3. Log `[UnfinishedFlights] Info: Sealed rec=<rid> bp=<bpId> rp=<rpId>`.

Tooltip update on the group itself.

### 9.5 Tests

- Unit tests on the new predicate gates: controllable-subject true/false, each terminal state's outcome verdict, EVA exception, structural leaf interaction.
- `ApplyRewindProvisionalMergeStates`: stable-leaf controllable -> CP; stable-leaf debris -> Immutable; crashed-leaf -> CP (regression).
- Seal handler test: state flips, version bumps, reaper subsequently reaps RP if siblings sealed.
- `RewindPointReaperTests` extended: stable-leaf CP slots prevent reap until sealed.
- Migration test: legacy save with already-reaped split RPs has empty Unfinished Flights for those splits.
- In-game test: deploy 4 probes, fly mothership home, return to Recordings Manager, verify all 4 in Unfinished Flights, Fly probe #2, land it, merge, verify slot closure + supersede.
- In-game test: deploy a probe, Seal it, verify row disappears + RP eventually reaps.
- In-game test: auto-parachute booster scenario (S19), verify NOT in Unfinished Flights and RP reaps.

### 9.6 Documentation

- Update `parsek-rewind-to-separation-design.md` §1.2 ("Out of scope: stable-end splits") and §7.31 ("intended behaviour, not a limitation") -- both are reversed by this extension.
- New `parsek-unfinished-flights-extension-design.md` (or fold into a v0.10 revision of the rewind-to-separation doc) once this research note is promoted.
- `roadmap.md` entry under v0.10+.
- CHANGELOG: "Unfinished Flights now also surfaces stable leaves left hanging -- probes deployed and forgotten in orbit, stranded EVA kerbals. Each row now has a Fly button and a Seal button (Seal marks the slot final so the rewind point can be cleaned up). Vessels that auto-parachuted to a safe landing still don't appear (they reached a stable conclusion)."

---

## 10. Risks

- **Disk usage growth.** Stable-leaf RPs persist until Sealed or fully closed. Bounded by player diligence with the Seal button. Mitigation: Settings -> Diagnostics already shows total RP disk usage; consider splitting into Crashed vs Stable-Unconcluded counts.
- **Over-inclusion in S6 / S17 / S18.** v1 ships with the simple terminal-state classifier; cases where the player did meaningfully fly a vessel that ended Orbiting/SubOrbital still appear as UF. Player Seals them. Acceptable per direction; v2 adds Park-or-not heuristics if playtest shows annoyance.
- **Reversal of v0.9 §7.31 stance.** v0.9 said stable-end splits don't get a row. R3 says some do (Orbiting, SubOrbital, EVA-stranded). Need clear CHANGELOG language to set player expectations.
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

R3 is ready to promote to a formal design doc. The shape:

- **Predicate**: structural leaf + controllable-subject + `TerminalOutcomeQualifies`. About 30 lines of code in `EffectiveState.cs`.
- **MergeState promotion**: extended `ApplyRewindProvisionalMergeStates` to use the new predicate. Reaper unchanged.
- **UI**: Rewind cell splits into Fly + Seal. Per-row Seal handler with confirmation. Tooltip text refresh.
- **Logging**: new `[UnfinishedFlights]` reasons; `[Recording]` log on Seal.
- **No new persistent state** (no Park flag, no focus-time field, no orbit-shift cache). Pure derivation from existing data + a button.
- **Tests**: unit + in-game per §9.5.
- **Disk policy**: relies on player to Seal; Settings diagnostic surfaces total usage.

Promote this to `Parsek/docs/parsek-unfinished-flights-stable-leaves-design.md` (or merge into a v0.10 revision of the rewind-to-separation doc) following the design-doc template in `development-workflow.md` step 3. Plan + build cycle follows.

---

*End of research note R3.*
