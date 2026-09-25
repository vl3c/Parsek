# Explaining paradox-prevention blocks on the stock KSP screens

**Date:** 2026-09-25. **Status:** analysis for owner decisions. Nothing here is implemented.

**This document is the single reference for:**
- where Parsek explains a reservation or block to the player
- whether each block is actually enforced
- what text explains it
- which Parsek window state moves out as a result

The follow-up todo entries point here: #640, #430 and `STOCK-UI-RESERVATION-OVERLAYS-2026-09-25`.

**Inputs:**
- A code read of the overlay layer, the Harmony patches, the ledger modules, `MilestoneStore` and `KerbalsModule`.
- A KSP 1.12.5 `Assembly-CSharp.dll` decompile (`vl3c/ksp-refs`, ilspycmd) for every stock hook named here.
- The reference mods RP-1, Contract Configurator (CC), Strategia, KSPCommunityFixes (KSPCF) and Crew R&R.

**Prior art:**
- `docs/dev/done/plans/game-state-ui-overlays.md` (PR #721, the v1 overlay layer)
- todo #640 (overlay v2)
- todo #430 (the "why is this blocked" explainer)
- `docs/dev/design-gui-inventory.md`
- `docs/dev/research/gui-feature-exposure-2026-09-11.md`

**Legend.** Claims are confirmed from source unless marked *(inferred)*. Every inferred claim is also listed in section 12.

---

## Summary

1. **An overlay layer already exists** (PR #721), on R&D, the Astronaut Complex and Mission Control. It says *what* and *when* in raw UT, never *why*. It uses a custom-drawn badge, loses its Mission Control badges on a tab switch, and is switched off by Contract Configurator (section 3).
2. **Every stock screen has a native way to say "disabled, and here is why".** The plan is to use those mechanisms instead of Parsek-drawn badges (section 5). Examples:
   - `Strategy.CanBeActivated(out reason)`
   - `Contract.CanBeDeclined` / `CanBeCancelled`
   - `CrewListItem.SetButtonEnabled(false, reason)`
   - the node and part tooltips
3. **Several blocks are missing.** Strategies, contract slots, Decline, Cancel and part purchases can each break or double-charge committed history today (section 4). Under the pairing rule, an overlay can only ship together with its block.
4. **"Rewind before that flight" frees nothing.** Rewinding is what creates the committed future. The honest "way out" is *when the item frees up*. The exceptions are recovering an Aboard vessel, stopping a loop and a Re-Fly of the flight that holds it (section 6).
5. **Decline and Cancel, recommended:**
   - Block Decline on an offer the committed future accepts.
   - Block Cancel only when a committed row later completes, fails or cancels that contract (section 7).
6. **The Career window becomes redundant** once Mission Control and Administration carry the annotations and stock strategy state is patched from the ledger. The Kerbals window can return to Advanced once the VAB crew dialog shows reserved kerbals (section 8).
7. **The "No new player-facing UI surfaces" rule needs one bounded exception** for annotating stock controls (section 9).

---

## 1. The problem and the owner's rulings (interview, 2026-09-25)

Each surface covers a different kind of state:
- The Timeline window shows PAST states of the career systems, and that is fine.
- The stock screens show CURRENT state, and that is also fine.

The unsolved problem is explaining WHY an action on a stock screen is blocked, when the cause is a reservation made on behalf of a future state. Parsek's timeline is append-only, and the blocks exist to prevent paradoxes. Today that explanation is either in the wrong place, a Parsek table the player must cross-reference, or it arrives as a surprise popup after the click.

| Question | Owner ruling |
|---|---|
| Problem | "Wrong place" and "surprise blocks". The core is explaining the paradox rule, not listing state |
| Overlay depth | Mark plus why. The why states the fact, the append-only rule and the way out. No jump links |
| VAB/SPH crew dialog | Show reserved kerbals marked (greyed, with a reason) instead of hiding them |
| Scope | Also audit whether the blocks themselves are complete |
| UI rule | Propose an explicit amendment |
| What stays in Parsek windows | Undecided at the interview. This document recommends (section 8) |
| Existing overlay layer | The owner had forgotten it; the analysis starts from it |

## 2. Principle: which surface owns which question

| Question the player asks | Owner surface |
|---|---|
| What happened, and when? | Timeline window (unchanged) |
| What do I have right now? | Stock screens, already patched to the ledger by `KspStatePatcher` (strategies are the exception, section 4 row S1) |
| Can I do this, and if not, why not? | **The stock control the player is about to click** |
| What is scheduled ahead of me? | Timeline window, as dated future rows (unchanged) |

The third row is what this document places. The answer always sits on the control itself: its disabled state, its existing stock tooltip, or stock's own reason field.

**The pairing rule** (the PR #721 invariant, carried forward): for every clickable kind, the mark and the click-block read **the same predicate helper**.
- A mark with no block misleads the player.
- A block with no mark is the surprise block the owner wants removed.

## 3. What exists today, and what is wrong with it

`StockUiOverlayController.cs` (`[KSPAddon(SpaceCentre)]`) attaches an 18x18 `OverlayBadge` to stock rows. The badge is a uGUI `RawImage` showing the stock alarm icon, and its tooltip box is drawn in IMGUI. Separate Harmony prefixes refuse actions with `CommittedActionDialog.ShowBlocked`, a popup titled "Action Blocked" with an OK button.

| Stock screen | Mark today | Tooltip today | Block today |
|---|---|---|---|
| R&D tech tree | gold badge on a node a committed future researches | `Committed at UT 183420 - recording 'Mun Lander 3' (+1 more committed)` | `TechResearchSpendPatch` / `TechResearchPatch`: "already committed on your timeline at UT n". Also refuses when projected science is short |
| Astronaut Complex | badges: future-hired, future-retired, reserved, lost, retired stand-in | `Reserved - held by a committed flight (Parsek)`, `Lost on a committed flight (Parsek)`, `Will be hired at UT n` | `KerbalHirePatch`, `KerbalDismissalPatch` |
| Mission Control | blue badge on an Offered contract a committed future accepts | `Will be accepted at UT n` | `ContractAcceptPatch` (+ `MissionControl.OnClickAccept` pre-block) |
| Funds / Science widgets | none; the widget hover shows a tooltip | `Total / Reserved / Short by` (`CurrencyReservationOverlay.cs`; SPACECENTER and FLIGHT only) | science enforced; funds advisory (P15 / D7) |
| KSC facility menu | none | none | `FacilityUpgradePatch` / `FacilityUpgradeSpendPatch` (the dialog prints the raw facility id) |
| VAB/SPH crew dialog | reserved kerbals **silently hidden** (`CrewDialogFilterPatch`) and swapped (`CrewAutoAssignPatch`) | none | the hiding is the block |
| Administration | none | none | **none** |
| Part list / purchase | none | none | **none** |

Defects:

1. **No why.** No text states the append-only rule or the way out. The text also uses raw UT where every Parsek window uses `KSPUtil.PrintDateCompact`, and contains em dashes, against the house style.
2. **The badge is custom-drawn and hover-only.** Its IMGUI tooltip box is a Parsek-drawn surface on a stock screen, and it uses none of the stock "disabled with reason" mechanisms.
3. **Mission Control loses its badges on a tab switch.** `MissionControl.RebuildContractList()` destroys every row on each tab change, filter change, accept, decline and `onContractsListChanged`. The controller decorates only at spawn and on `OnTimelineDataChanged`. The v1 plan accepted this as edge case E6.
4. **Contract Configurator switches the Mission Control path off.**
   - CC builds its own rows (`PrfbMissionListItem`, with a `ContractContainer` in `Data`), so the row lookup returns null and the overlay disables itself.
   - CC also replaces the Accept listener, bypassing the `OnClickAccept` pre-block, so only the `Contract.Accept` backstop refuses.
   - Read from CC source; not reproduced in game.
5. **An Astronaut Complex opened from the VAB/SPH is undecorated.** `CrewAssignmentDialog.onOpenACProceed` fires `onGUIAstronautComplexSpawn` in the EDITOR scene, and the addon runs in SpaceCentre only.
6. **Marks and blocks go stale.**
   - The committed-future slice is `MilestoneStore`'s unreplayed range (`LastReplayedEventIndex + 1 ..`). Only a rewind or revert load resets it (`MilestoneStore.cs:464-467`), and nothing advances it as the clock passes an event. Its only writes are creation (`:144`), restore-from-save (`:456`), reset (`:467`) and removal decrements.
   - Tech and contracts: this is mostly cosmetic, because the item changes state anyway.
   - Facilities: `FacilityUpgradePatch` blocks by facility id at ANY level, so a later 2->3 upgrade probably stays refused after the committed 1->2 has replayed *(inferred)*.
7. **Invisible to the GUI census.** uGUI plus hover-only means neither the GuiTree recorder nor a screenshot captures the layer (todo `GUI-INVENTORY-STOCKUIOVERLAY-BADGES-ARE-INVISIBLE-TO-BOTH-CENSUS-INSTRUMENTS`). Moving to stock mechanisms does not fix that by itself.

## 4. Block audit: is every reservation actually enforced?

Verdicts:
- **BLOCKED:** refused today.
- **SAFE:** the ledger walk reconciles the conflict.
- **HOLE:** committed history can be broken or double-charged.
- **ADVISORY:** shown but not enforced.

| Id | Player action now, against a committed future | Verdict | Mechanism / evidence |
|---|---|---|---|
| T1 | Research the same tech node | BLOCKED | `TechResearchPatch` |
| T2 | Research a different prerequisite path | SAFE | `PatchTechTree` unlocks the committed node at its UT regardless |
| S1 | Activate a strategy the future activates; deactivate one it relies on; fill slots it needs | **HOLE** | See "S1" below |
| C1 | Accept the same contract | BLOCKED | `ContractAcceptPatch` |
| C2 | Accept other contracts until the slots the future needs are full | **HOLE** | See "C2" below |
| C3 | Decline an offer the future accepts | **HOLE** | Silently overridden; section 7.2 |
| C4 | Cancel a contract the future completes, fails or cancels | **HOLE** | Completion zeroed with a possible cascade; penalties charged twice; section 7.3 |
| C5 | Complete now (another flight) a contract the future completes | SAFE | Earliest wins; the later completion is `Effective=false` (`ContractsModule.cs:396-429`) |
| F1 | Upgrade a facility the future upgrades | BLOCKED, probably over-blocked | By facility id at any level, never lifted (section 3 item 6) |
| F2 | Downgrade | SAFE | `FacilityDowngraded` is not a ledger action; `FacilityStatePatcher` re-writes the ledger level |
| F3 | Repair a facility the future repairs | state SAFE, **funds HOLE** | Every repair row charges `FacilityCost` (`FundsModule.cs:165`) without checking the building was destroyed |
| P1 | Purchase a part (entry cost) the future purchases | **HOLE** *(inferred)* | No block; a purchase is a `FundsSpending` row keyed by part name. Stock purchased state is rebuilt only in bypass-entry-purchase mode (`KspStatePatcher.cs:920-935`), so buying now plus the committed row charges twice |
| K1 | Assign / hire / dismiss a reserved or committed kerbal via VAB, SPH or Astronaut Complex | BLOCKED | Filter + swap + hire/dismiss patches |
| K2 | EVA / crew-transfer / rescue a reserved kerbal already aboard a live non-active vessel | **HOLE** (damage limited) | No `onCrewTransferred` / `onCrewOnEva` guard, and no swap on a `[` / `]` vessel switch. The spawn-time crew dedup (`VesselSpawner.cs:2972-3060`) empties the duplicate seat, so the recorded flight loses the kerbal instead of the world holding two |
| Sc1 | Collect science on a subject the future credits | SAFE | See "Sc1" below |
| M1 | Achieve a milestone the future achieves | SAFE | Earliest wins (`MilestonesModule.cs:54-114`); records stay effective by design |
| $1 | Spend funds the future needs | ADVISORY | The walk can go negative; the stock bar shows the projected minimum, so stock's own affordability checks gate KSC purchases; `CanAffordFundsSpending` has no caller (P15 / D7) |

**S1: strategies.**
- `StrategyLifecyclePatch` is capture-only postfixes.
- A second activation of the same id overwrites with a Warn (`StrategiesModule.cs:103-107`) and charges the setup cost again (`FundsModule.cs:171`).
- `StrategiesModule.GetAvailableSlots` has no caller.
- No production code writes stock `StrategySystem`, so after a rewind a committed activation is charged but the stock strategy never switches on.
- `docs/parsek-game-actions-and-resources-recorder-design.md:322/340` claims a "UT=0 reservation ... blocks new strategy activations entirely", which no code does.

**C2: contract slots.**
- `ContractsModule.GetAvailableSlots` has no caller.
- `KspStatePatcher.PatchContracts` restores committed accepts with no slot check (`ContractsModule.cs:565`: "can be negative if over-subscribed").

**Sc1: science subjects.**
- The ledger's cap walk sets `EffectiveScience = min(awarded, max - credited)`, earliest first (`ScienceModule.cs:242-256`).
- `ScienceSubjectPatch` reads the cutoff walk and does not see future credits. It is not the safety mechanism.

**What this means for overlays:**
- **Blocks that must ship with their overlays:** S1, C2, C3, C4, and P1 if a part overlay is wanted.
- **Ledger or flight defects with no control to mark:** F3 and K2, plus the unconditional double penalty behind C4 (section 7.5). File these as bugs.
- **Cheapest alternative, decision D5:** where the walk can make a duplicate harmless (earliest wins, no double charge), no block and no explanation is needed. Candidates are P1, F3 and the S1 setup cost.

## 5. Where each annotation goes, screen by screen

Common technique:
- Postfix the method that BUILDS or REFRESHES each stock row or button, never "decorate once at spawn": stock rebuilds rows and resets button state constantly.
- Text goes into the stock tooltip or reason field.
- Prefer postfixes: RP-1 replaces several of these methods with false-returning prefixes, and CC replaces listeners.
- Predicates are cached and invalidated on `LedgerOrchestrator.OnTimelineDataChanged`. `MilestoneStore.GetCommitted*` allocates and walks every committed event, and `CanAffordScienceSpending` runs a full recalc, so neither may run per row or per frame.

| Screen | Mark | Why | Block at the control | Reference | Difficulty |
|---|---|---|---|---|---|
| **R&D tech tree** | postfix `RDNode.UpdateGraphics`, tint via `graphics.SetIconColor` (runs after `SetButtonState`, which resets the colour) | postfix private `RDNode.GetTooltipCaption`, append to the stock node tooltip | postfix `RDController.UpdatePanel`: `actionButton.Enable(false)` + reason (it re-enables Research whenever a node is shown) | RP-1 `Harmony/RDNode.cs`, `RDController.cs`. `RDController.nodes` is public, so the current reflection is unneeded | easy |
| **Administration** | stock greys the row (`#bdbdbd`, state "na") | stock prints the reason in orange atop the description | ONE postfix on `Strategies.Strategy.CanBeActivated(out string reason)`, mirrored on `CanBeDeactivated`. It also refuses `Activate()`, which checks `CanBeActivated`. Bypass while `GameStateRecorder.IsReplayingActions` is set | Strategia `CanActivate(ref reason)`; RP-1 `Harmony/Administration.cs` | easy; needs the S1 predicate |
| **Mission Control: Available** | postfix `MissionControl.AddItem`, decorate the row just built (fixes the tab-switch loss). For CC: a cheap row-count change check while open, resolving `Data` as `MissionSelection`, then `Contract`, then any object with a `Contract`-typed `contract` field | postfix `UpdateInfoPanelContract`, append to `contractText` (as KSPCF `ShowContractFinishDates` does) | Accept: postfix `RefreshUIControls`, `btnAccept.interactable = false`; slot reason (C2). Decline: section 7.4 | CC `MissionControlUI.cs`; RP-1 `Harmony/MissionControl.cs` | moderate |
| **Mission Control: Active** | the same `AddItem` postfix: `Completes / Fails / Cancelled on <date> on your committed timeline` | the same detail-panel postfix | Cancel: section 7.4 | - | moderate |
| **Astronaut Complex** | postfix `AddItem_Applicants` / `_Available` / `_Assigned` / `_Kia`: `CrewListItem.SetLabel("Reserved until Y2 D114")` | postfix `CrewListItem.SetTooltip`, append to `TooltipController_CrewAC.descriptionString` | `SetButtonEnabled(false, title, caption)` (stock "locked with reason"); re-apply in a postfix on `UpdateCrewCounts`, which re-unlocks applicants. Also covers the EDITOR-opened complex (defect 5) | RP-1 `Harmony/AstronautComplex.cs`, `CrewListItem.cs`; Crew R&R `SetLabel`. Hazard: Enhanced Astronaut Complex clones rows | easy-moderate |
| **VAB/SPH crew dialog** (owner: show marked) | replace `CrewDialogFilterPatch`'s hiding with a postfix on the `AddAvailItem(pcm, out CrewListItem, ...)` overload, applying stock's `crew.inactive` look: `disabledCrewListSprite`, greyed name, `UIDragPanel.dragEnabled = false`, `MouseoverEnabled = false` | `SetButtonEnabled(false, "Reserved", "<why>")` | keep `CrewAutoAssignPatch`'s swap, so a reserved kerbal never lands in a seat | RP-1 `Harmony/BaseCrewAssignmentDialog.cs`. Do NOT copy Crew R&R's `rosterStatus = 9001` trick | moderate |
| **KSC facility menu** | - | attach a stock `TooltipController_Text` with `RequireInteractable = false` | postfix protected `KSCFacilityContextMenu.OnFacilityValuesModified` (re-runs on structure events): `UpgradeButton.interactable = false` (private; `AccessTools.FieldRefAccess`). `onFacilityContextMenuSpawn` fires before the buttons fill, so do not decorate there. Fix the raw facility id via `FacilityDisplayNames` | RP-1 `Harmony/KSCFacilityContextMenu.cs` | easy |
| **Part list / purchase** (only with a P1 block) | skip per-icon badges: `EditorPartIcon` is rebuilt on every refresh and KSPCF transpiles those paths | postfix `PartListTooltip.Setup` (both overloads): reason in `textGreyoutMessage` | disable `buttonPurchase`. NOT `EditorPartList.GreyoutFilters`, which makes the part unusable rather than unpurchasable. The R&D part list has the same need | RP-1 `Harmony/PartListTooltip.cs` | moderate |
| **Funds / science widgets** | - | keep the `CurrencyReservationOverlay` tooltip; extend its scene gate to the EDITOR, where funds are spent (todo `RESERVATION-OVERLAY-GAPS` (a)) | funds gate per P15 / D7 | - | easy |

Not worth it:
- **Tracking Station vessel list:** `TrackingStationWidget.Update()` rewrites its text every frame, and no reservation decision is taken there.
- **Science subjects:** SAFE via the cap walk (Sc1); nothing to block, so nothing to mark.
- **Game modes:** Science mode has no contracts or strategies, so only R&D, Astronaut Complex, VAB and facility annotations apply. Sandbox has no committed career state.

## 6. The "why" text: fact + rule + way out

**Finding: for most blocks, the obvious way out does not exist.**
- **Rewind (R) frees nothing.** It reloads the launch quicksave and re-applies every committed action as the timeline replays (`docs/user-guide.md`, "Rewind / Fast-Forward"). Rewinding is what CREATES the committed future.
- **Re-Fly (Rewind-to-Separation)** frees only recording-scoped rows of the superseded subtree. Null-scoped KSC rows (tech, facility, strategy, part, hire, KSC-side accepts) are never tombstoned (`TombstoneEligibility.cs:55-120`). It is also offered only at split Rewind Points.
- **Deleting a committed recording has no player UI**, apart from the ghost-only "X". Mission Delete removes cloned missions only.
- **Todo #430's planned "Revert to launch" shortcut** in the blocked dialog would unblock nothing. Re-scope it to the explanation alone.

**Genuine way-outs that do exist, all for kerbals:**
- An Aboard or Unknown hold ends when the kerbal is recovered from a real vessel continuing that flight (`KerbalsModule.cs:814-825`, `ResolveRecoveryClosureUT`).
- A Recovered hold ends at the flight's recovery UT.
- A hold made open-ended by a looping chain ends only if the loop stops, because a chain with a looping segment keeps `+inf` (`KerbalsModule.cs:804-812`). The loop toggle is an Advanced-only control (`design-ui-basic-advanced.md` section 4.5). The claim that turning the loop off releases the hold is *(inferred)*.

The honest third part is therefore **when it frees up**, plus the kerbal-specific actions above:

| Kind | Proposed text |
|---|---|
| Tech | `Researched on Y2 D114 by the committed flight 'Mun Lander 3'.` `Parsek's timeline is fixed once committed, so this cannot happen earlier or twice.` `It unlocks on that date.` |
| Contract accept / decline | `Accepted on Y2 D114 by the committed flight 'Mun Lander 3'.` + rule + `It becomes active on that date.` |
| Contract cancel | `Completed on Y1 D40 by the committed flight 'Mun Lander 3'.` + rule + `It completes and frees its slot on that date.` (or `Fails ...` / `Cancelled ...`) |
| Contract slots | `A committed flight accepts a contract on Y2 D114 and needs this slot.` + rule + `A slot frees when one of your active contracts ends.` |
| Strategy | `Activated on Y2 D114 on your committed timeline.` + rule + `It becomes active on that date.` |
| Facility upgrade | `Upgraded to level 2 on Y2 D114 on your committed timeline.` + rule + `The upgrade happens on that date.` |
| Kerbal hire | `Hired on Y2 D114 on your committed timeline.` + rule + `They join the roster on that date.` |
| Kerbal on a flight | `Flies 'Mun Lander 3' on your committed timeline.` `A kerbal on a committed flight cannot be used or risked before it ends.` `Free after Y2 D130.` / `Free once 'Mun Lander 3' is recovered.` / `Held while 'Mun Lander 3' loops.` |
| Kerbal lost | `Lost on the committed flight 'Mun Lander 3'.` `That flight is fixed history.` (no way out) |

Wording rules:
- Use the Kerbals window's crew vocabulary (Kerbals design ruling 19, "one crew vocabulary").
- Dates go through `KSPUtil.PrintDateCompact`.
- Plain ASCII, no em dashes.
- ONE pure `ReservationExplanation` builder per kind feeds the tooltip, the reason field and the backstop `CommittedActionDialog`, so a hover and a refused click say the same thing.
- Add `Re-Fly that flight to change it.` only where a Re-Fly genuinely releases the item.
- Parsek's text is English; stock reason fields are localized. This matches every other Parsek string today.

## 7. Decision analysis: Decline and Cancel on contracts the committed future relies on

### 7.1 Facts

**Stock KSP, from the 1.12.5 decompile:**
- `MissionControl.OnClickDecline` calls `Contract.Decline()`, and `OnClickCancel` calls `Contract.Cancel()`. Neither asks for confirmation.
- `Decline()` costs `Career.RepLossDeclined` reputation, a difficulty setting that can be zero.
- `Cancel()` runs `PenalizeCancellation()`: funds and reputation interpolated from the advance to the full failure penalty, by the fraction of time elapsed towards the deadline. Cancelling early is cheaper than failing.
- `Contract.CanBeDeclined()` / `CanBeCancelled()` are virtual and return true. `MissionControl` sets `btnDecline` / `btnCancel.interactable` from them, so this is a native disable hook.
- Contract Configurator overrides both (`ConfiguredContract`, from its `declinable` / `cancellable` config) and replaces both listeners with handlers that call `Contract.Decline()` / `Cancel()`.

**Parsek:**
- `ContractDeclined` is not a ledger action (`GameStateEventConverter.cs:331`). A committed later accept is restored anyway by `KspStatePatcher.PatchContracts`.
- `Cancel()` becomes a `ContractCancel` row now. A later committed `ContractComplete` then becomes `Effective=false` and loses its funds, reputation and science (`ContractsModule.cs:414-420`).
- Fail and cancel penalties are charged **unconditionally**, whatever the Effective flag (`FundsModule.cs:454-470`), so a later committed fail or cancel is charged again.
- Deadline expiry is not a committed row; the walk derives it (`ContractsModule.cs:470-497`).
- An unaffordable committed tech unlock is refused and marked `UnaffordableRunningScience`, with a Warn reading "possible bug or data corruption" (`ScienceModule.cs:294-321`). Contract completions can award science.

### 7.2 Decline an Offered contract that a committed flight accepts later

| Option | Effect | Verdict |
|---|---|---|
| A. Allow silently (today) | The offer vanishes, reputation may drop, and at the committed UT the contract reappears as Active unexplained. The row is marked while Decline stays live, breaking the pairing rule | reject |
| B. Allow, annotate | The same no-op plus penalty, now explained | reject |
| **C. Block** | Decline greyed, reason in the detail panel, backstop refusal. The offer stays listed until stock expires it or the committed UT arrives; either way it becomes active then | **adopt** |
| D. Honour the decline | Tombstones a committed action, leaves the flight's completion with no accept, contradicts append-only | reject |

Option C reuses the Accept block's predicate (`GetCommittedContractAcceptIds`) on the same row. The stale slice cannot bite here: after the committed UT the contract is Active, so Decline is unreachable.

### 7.3 Cancel an Active contract

| Case | What the committed timeline does later | Cancelling now, today |
|---|---|---|
| X1 | completes it | Cancel penalty now; the completion's funds, reputation and science are zeroed; the ghost still completes it on screen. Downstream committed spending may become unaffordable, and a committed tech unlock is refused. **A paradox cascade into committed history** |
| X2 | fails it (recorded) | Cancel penalty now **plus** the committed fail penalty: **charged twice** |
| X3 | cancels it (recorded) | Two cancel penalties |
| X4 | nothing, or only the derived deadline expiry | No committed row is affected. Cancelling early for the cheaper penalty is legitimate stock play |

| Option | Result | Verdict |
|---|---|---|
| A. Allow silently (today) | X1 cascades; X2 and X3 double-charge | reject |
| B. Allow, annotate | The same outcomes, now explained; breaks the pairing rule | reject |
| **C. Block X1-X3, allow X4** | No cascade, no double charge. Cost: the slot cannot be freed before the committed UT, but committed history already occupies it until then | **adopt** |
| D. Block X1 only | X2 and X3 still double-charge; a harder rule to state | reject |
| E. Confirmation popup | A new popup type (forbidden by the UI rule), and it cannot state the cascade without a full recalc per click | reject |

**Rule:** Cancel is refused when the committed timeline has a later explicit completion, failure or cancellation row for the contract. Derived deadline expiry does not count. Contracts the committed future leaves open stay cancellable.

### 7.4 Implementation

The same layering as the Accept block:
- **Predicate.**
  - Decline uses the accept helper.
  - Cancel uses a new pure helper over the effective ledger: the contract's committed resolution after now (type + UT + recording), cached, keyed by UT, and so free of the stale slice.
  - The Active-row annotation reads the SAME helper.
- **Button state.** Postfix `Contract.CanBeDeclined` / `CanBeCancelled`, plus CC's `ConfiguredContract` overrides when CC is loaded, because a patch on the base method does not cover an override. The reason goes in via the `UpdateInfoPanelContract` postfix.
- **Backstop.** Prefixes on the non-virtual `Contract.Decline()` / `Cancel()`, which CC's handlers call too, refusing with the section 6 text. Bypass while `IsReplayingActions` is set. `PatchContracts` writes state directly and calls neither (`KspStatePatcher.cs:2501`).
- **Tests.**
  - E18-style pairing cells: decline vs the accept mark, cancel vs the Active-row annotation.
  - A pure cell for each of X1-X4.
  - A CC-override target-resolution cell.

### 7.5 Not covered by this decision

- **A world-driven failure now:** a present-day flight loses the vessel a committed completion relied on, and stock fires `Contract.Fail`. This cannot be blocked. It takes the X1 path. Record it as the one known path where present play overrides a committed contract outcome, and point the "possible bug or data corruption" Warn at this cause.
- **The unconditional double penalty** (X2, X3, and the world-driven path) is a ledger defect in its own right. A fail or cancel on an already-resolved contract should not charge again.

## 8. What leaves the Parsek windows

### 8.1 The Career window becomes redundant

The window is `UI/CareerStateWindowUI.cs`, PR #1796, inventory section 3.5.
- It is read-only, Advanced-only, and shown in Career games in the KSC and FLIGHT scenes.
- It has two tabs, Contracts and Strategies. Each tab shows:
  - `Active now: N of M slots`
  - the active rows
  - a `Pending in timeline (n) - N of M slots at timeline end` fold
  - a `Timeline end` column
- Name cells link into the Timeline's Career view.

| Career window element | Stock screen today | With the section 5 annotations | Timeline Career view |
|---|---|---|---|
| `Active now: 2 of 3 slots` (contracts) | **yes**: `MissionControl.textMCStats` (`#autoLOC_468173`), orange when full | the Accept reason names a held slot (C2) | no |
| Active contract rows | **yes**: Active tab | - | accept rows |
| `Accepted` date | no | no | **yes** |
| `Deadline` + `(in 12d)` | **yes**: detail panel (`PrintDate` / `PrintDateDeltaCompact` on `DateDeadline`) | - | no |
| `Timeline end` on an active contract | no | **new**: the Active-row annotation (section 5) | **yes**: dated rows, future dimmed |
| Pending fold: contracts the future accepts | only while still Offered | the accept mark + Accept / Decline blocks | **yes**, including contracts no longer offered |
| `N of M slots at timeline end` | no | not needed: blocking depends on peak concurrent slots before each committed accept (the C2 reason), not the end count | no |
| `Active now: N of M` (strategies) | **yes**: `Administration.activeStratCount` (`#autoLOC_439627`) | the `CanBeActivated` reason names a held slot | no |
| `Activated` date | no (stock keeps `Strategy.DateActivated` but does not show it) | no | **yes** |
| `Flow` | **yes**: the Administration description | - | no |
| `Timeline end` on an active strategy | no | the `CanBeDeactivated` reason + description text | **yes** |
| Pending fold: strategies the future activates | no | the `CanBeActivated` reason | **yes** |
| Mode banner `(timeline ends <date>)` | no | - | **yes**: rows after the "now" divider |
| Available in FLIGHT | stock screens are KSC-only | - | **yes**; no contract or strategy decision is taken in flight |

- **Genuinely lost:** the one-screen roll-up of "what is active and what the committed future does to it". That is a planning convenience for Advanced players only; Basic players never had the window.
- **What the window was covering up:**
  - **Strategies:** the tab reads the ledger (`ComputeELS()`), but stock `StrategySystem` is never patched from it (S1). Retiring the window without a `KspStatePatcher` strategy patch would hide that divergence, not fix it.
  - **Mission Control:** Decline and Cancel are unblocked (C3, C4); only the `Timeline end` column hinted at the consequence.

**Conditions for retiring the window:**
1. **Mission Control:**
   - Available rows: mark, reason, and Accept + Decline blocked.
   - Active rows: annotated, with Cancel blocked per section 7.3.
   - The C2 slot reason on Accept.
   - All of it survives tab switches and CC.
2. **Administration:**
   - `CanBeActivated` / `CanBeDeactivated` reasons.
   - Stock strategy state patched from the ledger.
3. **The Timeline's Career view stays as it is.**
4. **One retirement PR** after 1 and 2. It removes:
   - the window and its launcher
   - the `career` census vocabulary (`op=tab window=career`, `pending:` keys)
   - the GUI-1 / GUI-5 / GUI-8 / GUI-14 / GUI-15 captures
   - `UI/Gallery/GuiMockCareerStates.cs`
   - the gate key and the inventory and Basic/Advanced rows

   Mind the `CommittedBatchTallySourceSyncTests` / census source-sync cells that read those vocabularies.

Retiring the window before conditions 1 and 2 ship would leave Advanced players with no in-place explanation, which is the owner's "wrong place" problem.

### 8.2 The other windows

| Window / tab | Recommendation |
|---|---|
| Timeline > Career view | Keep everything: dated past and future rows are its job |
| Kerbals > Roster | Keep as the full roster reference. Once the VAB crew dialog shows reserved kerbals marked, the 2026-09-22 reason for showing this window in Basic ("the only surface explaining why a reserved kerbal is missing") is gone, so consider returning it to Advanced (D3) |
| Kerbals > Outcomes | Keep (history) |

## 9. Proposed amendment to the "No new player-facing UI surfaces" rule

Current text (`.claude/CLAUDE.md`, Hard rules):
> No new player-facing UI surfaces (windows, popups, badge counters, persistent "issues" panels). When information seems to
> need surfacing, the only options are extra wording in an EXISTING hover tooltip ..., a one-shot `ParsekLog.ScreenMessage`
> for an EVENT ..., or nothing ...

Proposed addition:
> **Exception: stock-control annotation.** Parsek may annotate a STOCK KSP control the player can act on, to explain a Parsek
> block on that exact control. It may use only stock's own mechanisms:
> - the control's disabled or greyed state
> - text appended to that control's existing stock tooltip or description
> - stock's own reason field (`CanBeActivated` reason, `CrewListItem.SetButtonEnabled` caption, `SetLabel` status line)
> - a tint of the control's existing icon
>
> Every annotation is paired with a click-block that reads the same predicate. There are no Parsek-drawn boxes, badges,
> counters or panels on stock screens.

**Consequences:**
- `OverlayBadge`, a custom icon with a Parsek-drawn IMGUI box, does not meet the amended rule. Migrate it rather than grandfather it.
- The Active-row contract annotation and the Astronaut Complex status labels for lost or retired stand-ins are informational kinds that no stock button acts on. They are allowed because they annotate an existing stock row's own status text. The pairing rule applies to clickable kinds, as in the v1 plan.
- The rule text itself is changed only in `.claude/CLAUDE.md` + `AGENTS.md` (byte-identical) and only on the owner's ruling (D1).

## 10. Sequencing

Each step is one PR; each pairs a mark with its block.

1. **Wording and predicates, no new screens.**
   - The `ReservationExplanation` builder (section 6) feeds the dialog and the existing badges.
   - Replace the stale `MilestoneStore` slice with a UT-keyed committed-future index over the effective ledger, read by BOTH the marks and the blocks.
   - Verify and fix the facility over-block (F1).
2. **Migrate R&D, the Astronaut Complex and Mission Control to stock mechanisms.** This fixes defects 2-5.
3. **Mission Control Decline / Cancel blocks + Active-row annotations** (section 7). Closes C3 and C4.
4. **Contract slots:** a committed-slot predicate on Accept (C2).
5. **Strategies:**
   - the S1 predicate + `CanBeActivated` / `CanBeDeactivated`
   - a `KspStatePatcher` strategy-state patch
   - correct the design-doc claim
6. **Retire the Career window** (section 8.1).
7. **VAB/SPH crew dialog: show marked** (section 5). Then re-rule the Kerbals window's Basic visibility (D3).
8. **KSC facility menu** (section 5). Currency tooltip in the EDITOR.
9. **Only if wanted:** a P1 part-purchase block + `PartListTooltip`, or the ledger-side dedupe (D4 / D5).
10. **Bugs, independent of the UI:**
    - the unconditional contract penalty double charge (7.5)
    - the facility repair double charge (F3)
    - the EVA / transfer reservation path (K2)
11. **Tooling:** a uGUI capture path for the GuiTree recorder, plus pointer parking for tooltips, so the census can see stock-screen annotations.

## 11. Decision register

| Id | Decision | Status |
|---|---|---|
| R1 | The problem is explaining paradox blocks at the stock control; the Timeline keeps history | owner-ruled 2026-09-25 |
| R2 | Explanation depth: mark + fact + rule + way out; no jump links | owner-ruled |
| R3 | VAB/SPH crew dialog shows reserved kerbals marked, not hidden | owner-ruled |
| R4 | Audit block completeness alongside overlay placement | owner-ruled |
| D1 | Adopt the section 9 rule amendment; migrate `OverlayBadge` | **open**; recommended |
| D2 | Way out: "committed is permanent, the text says when it frees" vs building a real un-commit for KSC-origin actions | **open**; recommended: the former |
| D3 | Kerbals window back to Advanced once the crew dialog shows reserved kerbals | **open**; recommended |
| D4 | Part purchases: block, or dedupe in the ledger | **open**; recommended: ledger dedupe (no UI needed) |
| D5 | Prefer making duplicates harmless in the walk over blocking wherever possible (P1, F3, the S1 setup cost) | **open**; recommended |
| D6 | Retire the Career window after the section 8.1 conditions | **open**; recommended |
| D7 | Block Decline on committed accepts; block Cancel when a committed row later resolves the contract | **open**; recommended (section 7) |

## 12. Claims still to verify

| Claim | Where | How to verify |
|---|---|---|
| F1 facility over-block after the committed upgrade replays | section 3 item 6, section 4 | Rewind before a committed 1->2 upgrade, let it replay, try 2->3 |
| P1 part purchase double charge | section 4 | Rewind before a committed purchase, buy the part, read the ledger funds rows |
| CC switches off the Mission Control overlay and bypasses the Accept pre-block | section 3 item 4 | Open Mission Control with CC installed and a committed accept |
| Turning a loop off releases a looping chain's kerbal hold | section 6 | Unit cell over `KerbalsModule` with `LoopPlayback` toggled |
| X1 cascade reaches a refused committed tech unlock | section 7.3 | A synthetic ledger: completion reward funds a later tech spend, then cancel before the completion |

## Sources

- **Parsek, overlay layer:**
  - `StockUiOverlayController.cs`
  - `OverlayBadge.cs`
  - `CurrencyReservationOverlay.cs`
  - `CommittedActionDialog.cs`
- **Parsek, Harmony patches:** `Patches/`
  - `TechResearchPatch`
  - `ContractAcceptPatch`
  - `KerbalHirePatch`
  - `KerbalDismissalPatch`
  - `FacilityUpgradePatch`
  - `CrewDialogFilterPatch`
  - `CrewAutoAssignPatch`
  - `StrategyLifecyclePatch`
  - `FacilityRepairCapturePatches`
- **Parsek, stores and ledger:**
  - `MilestoneStore.cs`
  - `KerbalsModule.cs`
  - `TombstoneEligibility.cs`
  - `GameActions/`: `StrategiesModule`, `ContractsModule`, `FundsModule`, `ScienceModule`, `MilestonesModule`, `KspStatePatcher`, `GameStateEventConverter`
- **Parsek UI:** `UI/CareerStateWindowUI.cs`, `UI/KerbalsPresentation.cs`
- **Docs:**
  - `docs/dev/done/plans/game-state-ui-overlays.md`
  - `docs/dev/todo-and-known-bugs.md`: #640, #430, `RESERVATION-OVERLAY-GAPS`, the census badge gap, `GUI-P15-D7`
  - `docs/dev/design-gui-inventory.md`
  - `docs/dev/design-ui-basic-advanced.md`
  - `docs/dev/design-gui-kerbals-window.md`
  - `docs/parsek-game-actions-and-resources-recorder-design.md`
  - `docs/user-guide.md`
- **KSP 1.12.5 decompile:**
  - `RDController`, `RDNode`, `RDNodePrefab`
  - `MissionControl`, `MCListItem`
  - `Contracts.Contract`
  - `Administration`, `Strategies.Strategy`
  - `AstronautComplex`, `CrewListItem`, `BaseCrewAssignmentDialog`
  - `KSCFacilityContextMenu`
  - `PartListTooltip`, `EditorPartList`
  - `TrackingStationWidget`
- **Reference mods:**
  - RP-1 `Source/RP0/Harmony/` (RDNode, RDController, MissionControl, Administration, AstronautComplex, CrewListItem, BaseCrewAssignmentDialog, KSCFacilityContextMenu, PartListTooltip, EditorPartIcon)
  - CC `MissionControlUI.cs`, `ConfiguredContract.cs`
  - Strategia `StrategiaStrategy.cs`
  - KSPCF `QoL/ShowContractFinishDates.cs`
  - Crew R&R `Interface/SpaceCenterModule.cs`
