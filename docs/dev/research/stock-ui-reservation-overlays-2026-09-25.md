# Reservation and block state on the stock KSP screens

**Date:** 2026-09-25. **Status:** analysis for an owner decision. Nothing here is implemented yet.
**Inputs:** a code read of the overlay layer, the patches, the ledger modules and `MilestoneStore`, plus a
1.12.5 `Assembly-CSharp.dll` decompile (from `vl3c/ksp-refs`) for the stock hook points.
It also reads RP-1, Contract Configurator, Strategia, KSPCommunityFixes and Crew R&R as reference mods.
Prior art it builds on:
- `docs/dev/done/plans/game-state-ui-overlays.md` (PR #721, the v1 overlay layer)
- todo #640 (overlay v2) and todo #430 (the "why is this blocked" explainer)
- `docs/dev/design-gui-inventory.md`
- `docs/dev/research/gui-feature-exposure-2026-09-11.md`

## 0. The problem, as the owner framed it (interview, 2026-09-25)

Each surface already covers a different kind of state:
- The Timeline window shows PAST states of the career systems. That is fine as it is.
- The stock screens show CURRENT state. That is also fine.

The unsolved problem is explaining WHY an action on a stock screen is blocked. The cause is a reservation made on behalf of a future state: Parsek's timeline is append-only, and blocks prevent paradoxes. Today that explanation is in the wrong place, in a Parsek table the player has to cross-reference. Or it arrives as a surprise, a popup after the click.

The owner's answers that shape this report:
- **Problem:** "wrong place" and "surprise blocks". The core is explaining the paradox rule, not listing state.
- **Overlay depth:** mark plus why. The "why" carries three things: the fact, the append-only rule and the way out. No jump links.
- **VAB/SPH crew dialog:** show reserved kerbals marked (greyed, with a reason) instead of hiding them.
- **Scope:** also audit whether the blocks themselves are complete. Section 4 does that.
- **The "No new player-facing UI surfaces" rule:** propose an explicit amendment (section 7).
- **What stays in the Parsek windows:** undecided. This report recommends per window (section 6).
- The owner had forgotten the v1 overlay layer exists. The report therefore starts from it (section 1).

## 1. What already exists: the PR #721 overlay layer

`Source/Parsek/StockUiOverlayController.cs` is a `[KSPAddon(SpaceCentre)]` addon. It decorates three stock screens with
an 18x18 `OverlayBadge`: a uGUI `RawImage` showing the stock alarm icon, with its tooltip box drawn in IMGUI. Separate Harmony
prefixes refuse the action and pop `CommittedActionDialog.ShowBlocked` ("Action Blocked", OK button).

| Stock screen | Mark today | Tooltip text today | Block today |
|---|---|---|---|
| R&D tech tree | gold badge on a node a committed future researches | `Committed at UT 183420 - recording 'Mun Lander 3' (+1 more committed)` | `TechResearchSpendPatch` / `TechResearchPatch`: "Cannot research X. This technology is already committed on your timeline at UT n." It also refuses when projected science is short |
| Astronaut Complex | badges for future-hired, future-retired, reserved, lost and retired stand-in | `Reserved - held by a committed flight (Parsek)`, `Lost on a committed flight (Parsek)`, `Will be hired at UT n` | `KerbalHirePatch` (committed hire), `KerbalDismissalPatch` |
| Mission Control | blue badge on an Offered contract a committed future accepts | `Will be accepted at UT n` | `ContractAcceptPatch` (+ `MissionControl.OnClickAccept` pre-block) |
| Funds / Science widgets | none; hovering the widget opens a tooltip | `Total / Reserved / Short by` (`CurrencyReservationOverlay.cs`, SPACECENTER and FLIGHT only) | science enforced; funds advisory (P15 / D7) |
| KSC facility menu | none | none | `FacilityUpgradePatch` / `FacilityUpgradeSpendPatch` (dialog prints the raw facility id) |
| VAB/SPH crew dialog | reserved kerbals **silently hidden** (`CrewDialogFilterPatch`) and swapped (`CrewAutoAssignPatch`) | none | the hiding is the block |
| Administration | none | none | **none** |
| Part list / purchase | none | none | **none** |

The v1 plan's load-bearing invariant is still right and should carry forward (plan section 3, todo #640 "Review guidance"):
**for every clickable kind, the overlay candidate set equals the click-block predicate set, and both read the same
source helper.** A mark with no block misleads the player. A block with no mark is the "surprise block" the owner wants removed.

### 1.1 What is wrong with the existing layer

Each item below is confirmed from source unless marked otherwise.

1. **It says what and when, never why.** No text states the append-only rule, and no text states what the player can do. "Committed at UT 183420"
   also uses raw UT, while every Parsek window has moved to `KSPUtil.PrintDateCompact` dates. The tooltips also contain
   em dashes, against the house style.
2. **The badge is hover-only and custom-drawn.** The 18px alarm icon's IMGUI tooltip box is a Parsek-drawn surface on a stock
   screen. Every screen has a STOCK way to say "disabled, and here is why" (section 3), and the badge uses none of them.
3. **Mission Control badges disappear on a tab switch.** `MissionControl.RebuildContractList()` destroys every row on each tab
   change, archive filter change, accept, decline and `onContractsListChanged`. The controller decorates only at spawn and on
   `OnTimelineDataChanged`. The v1 plan accepted this as edge case E6. In practice the player loses the badges after one
   Available, Active, Available round trip.
4. **Contract Configurator breaks the Mission Control path.** CC builds its own rows (`PrfbMissionListItem`, with a `ContractContainer` in
   `Data`), so `ExtractMissionControlRowContract` returns null and the overlay switches itself off. CC also replaces the Accept
   button's listeners, which bypasses the `OnClickAccept` pre-block, so only the `Contract.Accept` backstop still refuses. CC is in most career
   installs. This is from CC's source (`MissionControlUI.cs`) and was not reproduced in game.
5. **An Astronaut Complex opened from the VAB/SPH is not decorated.** `CrewAssignmentDialog.onOpenACProceed` fires
   `onGUIAstronautComplexSpawn` in the EDITOR scene, and the addon is SpaceCentre-only.
6. **Badges can go stale.** The committed-future slice is `MilestoneStore`'s unreplayed range
   (`LastReplayedEventIndex + 1 ..`). Only a rewind or revert load resets it (`MilestoneStore.cs:464-467`). Nothing ever advances it again as
   the clock passes an event's UT: the only writes are creation (`:144`), restore-from-save (`:456`), reset (`:467`) and removal
   decrements. So after a rewind, a mark and its block stay on for the rest of the session, even after the event has replayed.
   - Tech and contracts: this is mostly cosmetic, because the node or contract changes state anyway.
   - Facilities: `FacilityUpgradePatch` blocks by facility id at ANY level, so this probably over-blocks. A later 2->3 upgrade stays refused after the
     committed 1->2 has replayed. This is inferred from code and needs an in-game check.
7. **The census cannot see the layer.** uGUI plus hover-only means neither the GuiTree recorder nor a screenshot captures it
   (todo `GUI-INVENTORY-STOCKUIOVERLAY-BADGES-ARE-INVISIBLE-TO-BOTH-CENSUS-INSTRUMENTS`). Any expansion is untestable by the
   harness until that tooling exists. Moving to stock tooltips (section 3) does not fix this by itself.

## 2. The principle: which surface owns which question

| Question the player asks | Owner surface |
|---|---|
| What happened, and when? (past) | Parsek Timeline window (unchanged) |
| What do I have right now? | Stock screens, already patched to the ledger by `KspStatePatcher` |
| Can I do this, and if not, why not? | **The stock control the player is about to click** |
| What is scheduled on the timeline ahead of me? | Timeline window, as dated future rows (unchanged) |

The third row is what this report places. The answer always sits on the control itself: its disabled state, its existing
tooltip, or stock's own "reason" field. It never sits in a separate Parsek panel.

## 3. Where each overlay goes, screen by screen

Common technique for all screens:
- Postfix the method that BUILDS or REFRESHES each stock row or button, never "decorate once at spawn". Stock rebuilds rows constantly and resets button state on refresh.
- Put the text in the stock tooltip or reason field, never in a Parsek-drawn box.
- Prefer postfixes: RP-1 replaces several of these methods with false-returning prefixes, and CC replaces listeners.

### 3.1 R&D tech tree (easy)
- **Mark:** postfix `RDNode.UpdateGraphics` and tint via `graphics.SetIconColor`. `SetButtonState` resets the colour to white, so the
  tint must be applied after it.
- **Why:** postfix the private `RDNode.GetTooltipCaption` and append the explanation to the stock node tooltip
  (`TooltipController_TitleAndText`).
- **At the button:** postfix `RDController.UpdatePanel` to call `actionButton.Enable(false)` and put the reason in the panel.
  `UpdatePanel` re-enables Research every time a node is shown. With this in place the player never reaches the popup; the popup stays as the backstop.
- **Reference:** RP-1 `Harmony/RDNode.cs` and `Harmony/RDController.cs`. `RDController.nodes` is public, so the current reflection is unnecessary.
- **Pairs with:** the existing tech block. No new predicate is needed.

### 3.2 Administration / strategies (easy, and the most native of all)
- **One postfix on `Strategies.Strategy.CanBeActivated(out string reason)`**, returning false with a Parsek reason. Stock then does everything:
  - greys the list row (title colour `#bdbdbd`, state "na")
  - disables Accept (`btnAcceptCancel.Enable(...)`)
  - prints the reason in orange at the top of the description (`GetStrategyDescription`)
  - refuses `Strategy.Activate()`, which checks `CanBeActivated` itself
- Mirror it on `CanBeDeactivated` for a strategy the committed future relies on. Bypass it while
  `GameStateRecorder.IsReplayingActions` is set. Strategia uses the same shape (`CanActivate(ref string reason)`).
- **This is a mark and a block in one predicate.** The invariant holds by construction.
- **Blocked on section 4:** no committed-future strategy predicate exists yet.

### 3.3 Mission Control / contracts (moderate)
- **Mark:** postfix `MissionControl.AddItem` and decorate the row it just built. This fixes the tab-switch loss.
- **Contract Configurator:** add a cheap change check while Mission Control is open, for example on the row count. Resolve `Data` in three ways: as
  `MissionSelection`, then as `Contract`, then as any object with a `Contract`-typed `contract` field.
- **Why:** postfix `UpdateInfoPanelContract` and append the explanation to `contractText`, the way KSPCF's `ShowContractFinishDates` does.
- **At the button:** postfix `RefreshUIControls` and set `btnAccept.interactable = false`.
- **Pairs with:** the existing same-guid accept block. The slot-exhaustion hole (section 4) is the second thing this screen must explain.

### 3.4 Astronaut Complex (easy to moderate)
- **Mark:** postfix `AddItem_Applicants` / `_Available` / `_Assigned` / `_Kia` and call stock's
  `CrewListItem.SetLabel("Reserved until Y2 D114")`, which replaces the status line.
- **Block:** call `SetButtonEnabled(false, reasonTitle, reasonCaption)` for a committed hire or a protected dismissal. That is stock's own
  "locked with reason" behaviour: it disables the hover buttons and appends a bold reason to the tooltip.
- **Why:** postfix `CrewListItem.SetTooltip` and append to `TooltipController_CrewAC.descriptionString`.
- **Re-apply** in a postfix on `UpdateCrewCounts`. That method re-unlocks every applicant row one frame after init and after every hire or fire.
- **Scene:** extend the controller to the EDITOR, or move the logic entirely into the patches, which are scene-agnostic.
- **References:** RP-1 `Harmony/AstronautComplex.cs` and `Harmony/CrewListItem.cs`, and Crew R&R's `SetLabel("Ready In: ...")`.
- **Compatibility hazard:** Enhanced Astronaut Complex clones rows.

### 3.5 VAB/SPH crew assignment dialog (moderate; owner chose "show marked")
- **Replace** `CrewDialogFilterPatch`'s hiding with a postfix on the `AddAvailItem(pcm, out CrewListItem, ...)` overload. The postfix applies
  stock's existing disabled-crew look, the same one stock uses for `crew.inactive`:
  - `disabledCrewListSprite`
  - greyed name
  - `UIDragPanel.dragEnabled = false`
  - `MouseoverEnabled = false`
- Add `SetButtonEnabled(false, "Reserved", "<why>")` on top.
- **Keep `CrewAutoAssignPatch`'s swap.** A reserved kerbal must still never land in a seat. The player now sees why Jeb is not in the pod.
- **Reference:** RP-1 `Harmony/BaseCrewAssignmentDialog.cs`. Do NOT copy Crew R&R's roster-status trick (it sets `rosterStatus = 9001` while the crew tab is open).
- **Consequence:** the Kerbals window loses the reason it was un-hidden in Basic mode (section 6).

### 3.6 KSC facility context menu (easy)
- **Block:** postfix the protected `KSCFacilityContextMenu.OnFacilityValuesModified`, which re-runs on every structure event. Set
  `UpgradeButton.interactable = false` (a private field, reached via `AccessTools.FieldRefAccess`).
- **Why:** attach a stock `TooltipController_Text` with `RequireInteractable = false`, so the disabled button still explains itself.
  `GameEvents.onFacilityContextMenuSpawn` fires before the buttons are filled, so do not decorate there.
- **Reference:** RP-1 `Harmony/KSCFacilityContextMenu.cs`.
- **Also fix:** the dialog's raw facility id. `FacilityDisplayNames.cs` already maps ids to names.

### 3.7 Part list and part purchase (moderate; only if section 4's part hole is closed by a block)
- **Do NOT use** `EditorPartList.GreyoutFilters`. It makes the part unusable, not unpurchasable.
- **Instead:** postfix `PartListTooltip.Setup` (both overloads), disable `buttonPurchase`, and put the reason in `textGreyoutMessage`.
  The R&D screen's part list has the same need.
- **Skip per-icon badges on `EditorPartIcon`.** Icons are rebuilt on every category or filter refresh, and KSPCF's `FasterEditorPartList` transpiles those paths.
- **Reference:** RP-1 `Harmony/PartListTooltip.cs`.

### 3.8 Funds and science widgets
- Keep the `CurrencyReservationOverlay` tooltip, and extend its scene gate to the EDITOR. That is where funds are actually spent;
  see open todo `RESERVATION-OVERLAY-GAPS` (a).
- This is also the place for the funds half of the "why", once P15 / D7 decides whether a funds gate exists.

### 3.9 Not worth it
- **Tracking Station vessel list:** `TrackingStationWidget.Update()` rewrites its text every frame, and nothing on it is a reservation decision.
- **Science subjects:** the ledger's cap walk (earliest credit first) already makes collecting now safe (section 4). There is nothing to block, so there is nothing to mark.

## 4. Block audit: is every reservation actually enforced?

The rule that falls out of the #721 invariant: **an overlay may only ship for a kind whose block ships in the same change.** So
the holes below come before, or together with, their screen's overlay.

| # | Player action now, against a committed future | Verdict | Mechanism / evidence |
|---|---|---|---|
| 1 | Activate a strategy the future activates; deactivate one it relies on; fill slots it needs | **HOLE** | See "Row 1" below |
| 2 | Purchase a part (entry cost) the future purchases | **HOLE** (inferred) | See "Row 2" below |
| 3 | Upgrade a facility the future upgrades | BLOCKED, probably over-blocked | by facility id at any level, and never lifts (section 1.1 item 6) |
| 3 | Downgrade | SAFE | `FacilityDowngraded` is not a ledger action; `FacilityStatePatcher` re-writes the ledger level |
| 3 | Repair a facility the future repairs | state SAFE, **funds HOLE** | See "Row 3" below |
| 4 | Accept the same contract (same guid) | BLOCKED | `ContractAcceptPatch` |
| 4 | Accept OTHER contracts until the slots the future needs are full | **HOLE** | See "Row 4" below |
| 4 | Decline an offer the future accepts | **HOLE** (silently overridden; see section 11.2) | `ContractDeclined` is not a ledger action; the accept is restored from its snapshot at its UT, and the decline's reputation loss stays |
| 4 | Complete a contract now (another flight) that the future completes | SAFE | earliest wins; the later completion gets `Effective=false` (`ContractsModule.cs:396-429`) |
| 4 | Cancel a contract the future completes, fails or cancels | **HOLE** (see section 11.3) | completion zeroed, with a possible committed-spend cascade; fail and cancel penalties are charged twice |
| 5 | Collect science on a subject the future credits | SAFE | See "Row 5" below |
| 6 | Spend funds the future needs | advisory | See "Row 6" below |
| 7 | Achieve a milestone the future achieves | SAFE | earliest wins (`MilestonesModule.cs:54-114`); records stay effective by design |
| 8 | Assign / dismiss / hire a reserved or committed kerbal via VAB, SPH or Astronaut Complex | BLOCKED | filter + swap + hire/dismiss patches |
| 8 | EVA / crew transfer / rescue a reserved kerbal who sits in a live non-active vessel | **HOLE** (damage limited) | See "Row 8" below |
| 9 | Research the same tech node | BLOCKED | `TechResearchPatch` |
| 9 | Research a different prerequisite path | SAFE | `PatchTechTree` unlocks the future node at its UT regardless |

**Row 1: strategies.**
- There is no block anywhere. `StrategyLifecyclePatch` only has capture postfixes on `Activate` / `Deactivate`.
- A second activation of the same id overwrites, with a Warn (`StrategiesModule.cs:103-107`), and charges setup cost again on every activate row (`FundsModule.cs:171`).
- No production code ever mutates the stock `StrategySystem`.
- `StrategiesModule.GetAvailableSlots` has no caller.
- `docs/parsek-game-actions-and-resources-recorder-design.md:322/340` claims a "UT=0 reservation ... blocks new strategy activations entirely". No code does this.

**Row 2: part purchases.**
- There is no block.
- A purchase is a `FundsSpending` row keyed by part name. Stock purchased state is only rebuilt in bypass-entry-purchase mode (`KspStatePatcher.cs:920-935`).
- So after a rewind, buying now plus the future row means two charges.

**Row 3: facility repair.**
- The building state is the last destroy or repair row by UT, which is safe.
- Every repair row charges `FacilityCost` without checking that the building was destroyed (`FundsModule.cs:165`). So a repair now plus the future repair means paying for a no-op repair.

**Row 4: contract slot exhaustion.**
- `ContractsModule.GetAvailableSlots` has no caller.
- `KspStatePatcher.PatchContracts` restores committed accepts with no slot check ("can be negative if over-subscribed", `ContractsModule.cs:565`).
- The design doc's UT=0 slot reservation is not enforced.

**Row 5: science subjects.**
- The ledger's cap walk sets `EffectiveScience = min(awarded, max - credited)`, earliest first (`ScienceModule.cs:242-256`).
- `ScienceSubjectPatch` does NOT see future credits, because it reads the cutoff walk. It is not the safety mechanism; the cap walk is.

**Row 6: funds.**
- The walk can go negative.
- The stock bar shows the projected minimum, so stock's own affordability checks are the effective gate at KSC.
- `CanAffordFundsSpending` has no caller (open decision P15 / D7).

**Row 8: kerbals via EVA / transfer / rescue.**
- No `onCrewTransferred` / `onCrewOnEva` guard exists, and the swap does not run on a `[` / `]` vessel switch.
- The spawn-time crew dedup (`VesselSpawner.cs:2972-3060`) empties the duplicate seat, so the recorded flight loses the kerbal instead of the world holding two copies.

**The holes that must close before their overlay can ship:**
- strategies (#1)
- contract slots (#4b)
- part purchase (#2), if a part overlay is wanted

Facility repair funds (#3c) and the EVA / transfer path (#8b) are ledger or flight defects with no stock-screen control to mark. File them as bugs.

## 5. The "why" text: fact + rule + way out

The owner picked a three-part explanation. The audit found a problem with the third part: **for most blocks, the obvious way
out does not exist.**

- **Rewind (R) does not free anything.** It reloads the launch quicksave and re-applies every committed action as the timeline replays
  (`docs/user-guide.md` "Rewind / Fast-Forward"). Rewinding is what CREATES the committed future.
- **Re-Fly (Rewind-to-Separation) does free items**, through tombstones, but with two limits:
  - It only covers recording-scoped rows of the superseded subtree.
  - Null-scoped KSC rows (tech, facility, strategy, part, hire and KSC-side accepts) are never tombstoned (`TombstoneEligibility.cs:55-120`). Re-Fly is also only offered at split Rewind Points.
- **Deleting a committed recording has no player UI** apart from the ghost-only "X". Mission Delete only removes cloned missions.
- **Todo #430 plans a "Revert to launch" shortcut in the blocked dialog.** It would not unblock anything, so re-scope #430 accordingly.

The honest "way out" is therefore **when it frees up**, not **how to undo it**:

| Kind | Proposed text (stock tooltip / reason field) |
|---|---|
| Tech | `Researched on Y2 D114 by the committed flight 'Mun Lander 3'.` / `Parsek's timeline is fixed once committed, so this cannot happen earlier or twice.` / `It unlocks when the clock reaches that date.` |
| Contract accept | `Accepted on Y2 D114 by the committed flight 'Mun Lander 3'.` / same rule / `It becomes active on that date.` |
| Contract slots | `A committed flight accepts a contract on Y2 D114 and needs this slot.` / same rule / `A slot frees when one of your active contracts ends.` |
| Strategy | `Activated on Y2 D114 on your committed timeline.` / same rule / `It becomes active on that date.` |
| Facility upgrade | `Upgraded to level 2 on Y2 D114 on your committed timeline.` / same rule / `The upgrade happens on that date.` |
| Kerbal hire | `Hired on Y2 D114 by your committed timeline.` / same rule / `They join the roster on that date.` |
| Kerbal reserved (flight) | `Flies 'Mun Lander 3' on your committed timeline.` / `A kerbal on a committed flight cannot be used or risked before it ends.` / `Free after Y2 D130.` (or `Free once 'Mun Lander 3' is recovered.` for an open-ended hold) |
| Kerbal lost | `Lost on the committed flight 'Mun Lander 3'.` / `That flight is fixed history.` / (no way out) |

Where a Re-Fly genuinely would release the item, the text can add `Re-Fly that flight to change it.` Deciding where that is
true needs the tombstone scope above, per kind. Wording should reuse the Kerbals window's crew vocabulary (Kerbals design
ruling 19, "one crew vocabulary"), dates via `KSPUtil.PrintDateCompact`, and no em dashes. The same strings feed the
backstop `CommittedActionDialog`, so a blocked click and a hover say the same thing.

**One owner decision falls out of this (D2 in section 9):** whether a player-side way out SHOULD exist for KSC-origin
commitments. Examples would be a Re-Fly that also tombstones the KSC actions between two flights, or an explicit
"un-commit". Today the model is strict: committed means permanent. The overlay text should say so plainly rather than hint at an
escape that is not there.

## 6. What leaves the Parsek windows

The recommendation follows the ownership table in section 2: remove present-tense "can I" state from the windows, and keep
dated history and the timeline projection.

| Window / tab | Keep | Remove or demote |
|---|---|---|
| Timeline > Career view (Contracts, Strategies, Facilities, Milestones, Tech) | all of it: dated rows, past and future | nothing |
| Career window > Contracts | see section 10: the whole window becomes redundant once the section 10.3 conditions hold | every column is covered by stock Mission Control, a Mission Control annotation or the Timeline's Career view |
| Career window > Strategies | see section 10 | every column is covered by stock Administration, an Administration annotation or the Timeline's Career view |
| Kerbals window > Roster | status plus the `Reserved until` hover, as the full roster reference | once the VAB crew dialog shows reserved kerbals marked, Basic mode loses its reason to show this window. Consider moving it back to Advanced (reverses the 2026-09-22 re-ruling; `design-ui-basic-advanced.md` section 3) |
| Kerbals window > Outcomes | all of it (history) | nothing |

Net effect:
- The Career window can be retired once the overlays are complete (section 10).
- The Kerbals window becomes a reference rather than the only explanation.
- No career fact is lost, because every removed "now" value is exactly what the stock screen already shows.

## 7. Proposed amendment to the "No new player-facing UI surfaces" rule

Current text (`.claude/CLAUDE.md`, Hard rules):
> No new player-facing UI surfaces (windows, popups, badge counters, persistent "issues" panels). When information seems to
> need surfacing, the only options are extra wording in an EXISTING hover tooltip ..., a one-shot `ParsekLog.ScreenMessage`
> for an EVENT ..., or nothing ...

Proposed addition (one bullet, appended):
> **Exception: stock-control annotation.** Parsek may annotate a STOCK KSP control the player can act on, to explain a Parsek
> block on that exact control. The annotation uses only stock's own mechanisms:
> - the control's disabled or greyed state
> - text appended to that control's existing stock tooltip or description
> - stock's own reason field (`CanBeActivated` reason, `CrewListItem.SetButtonEnabled` caption, `SetLabel` status line)
> - a tint of the control's existing icon
>
> Every annotation is paired with a click-block that reads the same predicate, the PR #721 invariant. There are no Parsek-drawn boxes, badges, counters or panels on stock screens.

Consequence: the existing `OverlayBadge` (a custom icon with a Parsek-drawn IMGUI box) does not meet the amended rule. It
should be migrated to the stock mechanisms in section 3 rather than grandfathered. The Kerbals design's "one crew vocabulary"
already requires the wording to match the Parsek windows.

## 8. Suggested sequencing

Each step is one PR and a pairing of mark and block.

1. **Wording and plumbing, no new screens.**
   - One pure `ReservationExplanation` builder per kind: fact + rule + when, with dates and no em dashes.
   - Feed it to `CommittedActionDialog` and to the existing badges.
   - Fix the stale-slice problem (item 6). A UT or replay-aware filter must be applied to both the overlay and the block, together, or the #721 invariant breaks.
   - Verify and fix the facility over-block.
2. **Migrate R&D, the Astronaut Complex and Mission Control to stock mechanisms** (sections 3.1, 3.3, 3.4). This fixes the Mission Control tab-switch loss, Contract Configurator rows and the Astronaut Complex opened from the editor.
3. **VAB/SPH crew dialog: show marked** (section 3.5). Then re-rule the Kerbals window's Basic visibility.
4. **Strategies:** a committed-future strategy predicate plus the `CanBeActivated` / `CanBeDeactivated` postfix (section 3.2). This closes hole #1.
   Correct or delete the design-doc claim.
5. **Contract slots:** a committed-slot predicate, blocking Accept with the reason (hole #4b).
5b. **Mission Control Decline / Cancel and Active-row annotations, plus the strategy state patch** (section 10.2).
   Then **retire the Career window** in one PR (section 10.3).
6. **KSC facility menu** (section 3.6). Extend the currency tooltip to the EDITOR (section 3.8).
7. **Only if wanted:** a part-purchase block plus the `PartListTooltip` annotation (hole #2).
8. **Separately, as bugs:** facility repair double charge (#3c) and the EVA / transfer reservation path (#8b).
9. **Tooling:** a uGUI capture path for the GuiTree recorder, so the census can see stock-screen annotations. Its tooltip text
   becomes photographable via pointer parking.

## 9. Open decisions for the owner

- **D1.** Adopt the rule amendment in section 7, and migrate `OverlayBadge` rather than keep it.
- **D2.** The way out: accept "committed is permanent; the text says when it happens", or design a real un-commit path
  for KSC-origin actions (section 5).
- **D3.** Kerbals window back to Advanced once the crew dialog shows reserved kerbals (section 6).
- **D4.** Whether part purchases get a block at all, or whether the double charge is fixed purely in the ledger. For example, dedupe
  purchases by part name the way milestones dedupe, which would make the action SAFE and need no overlay.
- **D5.** The same ledger-side option for strategies and facility repairs: make the duplicate action harmless in the walk
  (earliest wins, no double charge) instead of blocking it. For every kind where that is possible, it removes the need for
  both a block AND an explanation. That is the cheapest way to answer "why is this blocked": it is not blocked.

- **D6.** Retire the Career window once the section 10.3 conditions hold. The recommendation for Decline and Cancel on
  contracts the committed future relies on is in section 11: block both, with Cancel blocked only when a committed row
  resolves the contract later.

## 10. Is the Career window redundant once the overlays are done?

**Short answer: yes.** Its contracts and strategies tabs become redundant, but only after four conditions are met (section 10.3). Two of those conditions are
defects or holes that the window currently papers over, not features that the overlays would need to copy.

What the window is today: `UI/CareerStateWindowUI.cs`, PR #1796 (2026-09-24), inventory section 3.5.
- It is read-only and hidden in Basic mode.
- Its launcher appears in Career games only, in the KSC and FLIGHT scenes.
- It has two tabs, Contracts and Strategies. Each tab has:
  - a heading, `Active now: N of M slots`
  - a table of what is active now
  - a `Pending in timeline (n) - N of M slots at timeline end` fold listing what the recorded future adds
  - a `Timeline end` column saying what the recorded future does to each row
- Its only interaction is the name-cell link into the Timeline's Career view.

### 10.1 Element-by-element coverage

The "stock" column below was checked against the 1.12.5 decompile (`MissionControl`, `Administration`, `Strategies.Strategy`).

| Career window element | Stock screen today | With the section 3 annotations | Timeline Career view |
|---|---|---|---|
| Contracts heading `Active now: 2 of 3 slots` | **yes**: `MissionControl.textMCStats` prints the active count and limit (`#autoLOC_468173`), orange when full | the Accept reason names a slot held for a committed flight (hole #4b) | no |
| its hover `Slot limit from Mission Control L1` | implicit: the limit follows the building level | n/a | Facilities view dates the upgrade |
| Active contract rows (name) | **yes**: Mission Control Active tab | n/a | accept rows |
| `Accepted` date | no | no | **yes**: dated accept row |
| `Deadline` + `(in 12d)` / `(overdue 3d)` | **yes**: detail panel, `PrintDate` / `PrintDateDeltaCompact` on `DateDeadline` | n/a | no |
| `Timeline end` on an ACTIVE contract (`completes` / `FAILS` / `cancelled <date>`) | no | **new annotation needed**: Mission Control Active-tab row + detail text, `Completed on Y1 D40 by the committed flight 'X'` / `Fails on Y1 D40 ...` (todo #640's future-completed / future-failed badges) | **yes**: dated complete / fail / cancel rows (future rows dimmed) |
| Pending fold: contracts the future accepts | only if still Offered | the existing future-accept mark on Offered rows; paired with the accept block | **yes**, including contracts no longer offered (plan E9: not offered, so nothing on the stock screen to mark) |
| `N of M slots at timeline end` | no | not needed: the blocking question is peak concurrent slots before each future accept, not the end count, and it belongs on the Accept reason (#4b) | no |
| Strategies heading `Active now: N of M` | **yes**: `Administration.activeStratCount` (`#autoLOC_439627`) | the `CanBeActivated` reason names a slot held for the future | no |
| `Activated` date | no (stock keeps `Strategy.DateActivated` but does not show it) | no | **yes**: dated activate row |
| `Flow` | **yes**: the strategy's effect and commitment are in the Administration description | n/a | no |
| `Timeline end` on an active strategy (`deactivates <date>`) | no | `CanBeDeactivated` reason plus description text, `Deactivated on Y2 D114 on your committed timeline` | **yes** |
| Pending fold: strategies the future activates | no | the `CanBeActivated` reason on that strategy (section 3.2) | **yes** |
| Mode banner `(timeline ends <date>)` | no | n/a | **yes**: the future rows after the "now" divider |
| Name cell link into the Timeline | n/a | n/a | the Timeline's own category buttons |
| Available in FLIGHT | stock Mission Control and Administration are KSC-only | n/a | **yes**: the Timeline opens in flight. No contract or strategy decision is taken in flight, so nothing is lost for the "why is this blocked" question |

Every row is covered by at least one surface. **What is genuinely lost** is the one-screen roll-up: "which contracts or strategies are active now, and what does my committed future do to each". Today that takes a Mission Control visit plus the Timeline's Contracts view, and after section 10.3 it is a Mission Control visit with annotated rows. That roll-up is a planning convenience, and the window is already hidden from Basic players, so they never had it.

### 10.2 What the window was covering up

1. **The Strategies tab shows state that stock does not have.**
   - The tab reads the ledger (`EffectiveState.ComputeELS()`).
   - No production code ever writes the ledger's strategy state back into stock's `StrategySystem`.
   - After a rewind, a committed future activation is listed under the fold and charged in the ledger, but the stock strategy is never switched on (section 4, hole #1).
   - Retiring the window without fixing that would hide the divergence rather than solve it. The fix belongs in `KspStatePatcher`, so that stock Administration becomes the truth for "active now", the same way `PatchContracts` already makes Mission Control the truth for contracts.
2. **Mission Control's Decline and Cancel have no reasons and no blocks.** Stock has `btnDecline` and `btnCancel`. Neither is blocked for a contract the committed future relies on.
   - **Declining an offer the future accepts** is silently overridden: the committed accept is restored at its UT. Today the row carries the accept mark, but Decline stays live. That breaks the #721 invariant: a marked row has a clickable affordance with no block.
   - **Cancelling an active contract the future completes** makes the future completion `Effective=false` (earliest wins). The committed flight's reward is silently withdrawn.
   - Both are paradox-adjacent in exactly the owner's sense: the append-only timeline says the committed accept or completion happened. Each needs either a block with the section 5 reason, or at least the annotation. Only the Career window's `Timeline end` column hints at this today, and only for players who open it.

### 10.3 Conditions for retiring the window

1. **Mission Control:**
   - Offered rows the future accepts: mark and reason, with both Accept and Decline blocked (10.2 item 2).
   - Active rows the future completes, fails or cancels: annotated, with Cancel blocked or annotated per the owner's choice (D6).
   - A slot held for a committed accept: shown as the Accept reason (hole #4b).
   - All of this must survive tab switches and Contract Configurator (section 1.1 items 3-4).
2. **Administration:**
   - `CanBeActivated` / `CanBeDeactivated` return reasons for future activations, deactivations and held slots (section 3.2).
   - The ledger's strategy state is patched into stock `StrategySystem` (10.2 item 1).
3. **The Timeline's Career view stays as it is:** dated past and future rows for Contracts and Strategies. It is the history-and-schedule surface that absorbs the `Accepted`, `Activated`, pending and `Timeline end` information. No change is needed there.
4. **Retire in one PR, after 1 and 2 have shipped.** Remove the window, its launcher, the `career` census vocabulary (`op=tab window=career`, `pending:` keys), the GUI-1 / GUI-5 / GUI-8 / GUI-14 / GUI-15 captures, the gallery states in `UI/Gallery/GuiMockCareerStates.cs`, the `UiSurface` gate key, and the inventory and Basic/Advanced rows. Note the `CommittedBatchTallySourceSyncTests` trap for any in-game tests in its categories.

Retiring the window early, before 1 and 2, would leave Advanced players with the Timeline alone. That is still a complete record of events, but it has no "active now" roll-up and no in-place explanation, which is exactly the owner's "wrong place" problem.

## 11. Decision analysis: block Decline and Cancel, or only annotate them?

### 11.1 The facts both options have to respect

**Stock KSP, checked in the 1.12.5 decompile:**
- `MissionControl.OnClickDecline` calls `Contract.Decline()`, and `OnClickCancel` calls `Contract.Cancel()`. Neither asks for confirmation.
- `Decline()` costs `Career.RepLossDeclined` reputation, a difficulty setting that can be zero.
- `Cancel()` leads to `PenalizeCancellation()`. That charges funds and reputation interpolated from the advance to the full failure penalty, by the fraction of time elapsed towards the deadline. Cancelling early is cheaper than failing.
- Stock already has the hooks to disable both buttons: `Contract.CanBeDeclined()` and `Contract.CanBeCancelled()` are virtual and return true. `MissionControl` sets `btnDecline.interactable` / `btnCancel.interactable` from them.
- Contract Configurator overrides both hooks from contract-type config (`declinable` / `cancellable`). It also replaces both button listeners with handlers that call `Contract.Decline()` / `Contract.Cancel()` directly.

**Parsek:**
- **Decline:** `ContractDeclined` is not a ledger action (`GameStateEventConverter.cs:331`). A committed accept at a later UT is restored anyway by `KspStatePatcher.PatchContracts`.
- **Cancel:** it becomes a `ContractCancel` ledger row now. The walk then treats a later committed row as already resolved:
  - A later committed `ContractComplete` for the same contract becomes `Effective=false`, and its rewards are zeroed (`ContractsModule.cs:414-420`). Those are funds, reputation and science.
  - Fail and cancel penalties are charged **unconditionally**, whatever the Effective flag (`FundsModule.cs:454-470`; `ContractsModule` `ProcessFail` / `ProcessCancel`). A later committed `ContractFail` or `ContractCancel` is therefore charged a second time.
- **Deadline expiry is not a committed row.** The walk derives it from `DeadlineUT` (`ContractsModule.cs:470-497`).
- **An unaffordable committed tech unlock is refused.** The row is marked `UnaffordableRunningScience`, a Warn reads "possible bug or data corruption", and `KspStatePatcher`'s relock guard takes over (`ScienceModule.cs:294-321`). Science that a committed tech unlock depends on can come from a contract completion reward.

### 11.2 Decline an Offered contract that a committed flight accepts later

| Option | What happens | Verdict |
|---|---|---|
| A. Allow silently (today) | The offer vanishes, the player may lose reputation, and at the committed UT the contract reappears as Active with no explanation. The player's intent is defeated. The row carries the accept mark while Decline stays live, which breaks the #721 invariant | reject |
| B. Allow, annotate | The same no-op plus penalty, now explained. It still offers a click whose only durable effect is a reputation loss | reject |
| C. **Block** | Decline is greyed and the reason sits in the detail panel. The backstop refuses with the section 5 text. The offer stays listed until stock expires it or the committed UT arrives. Either way it becomes active then | **adopt** |
| D. Honour the decline | Remove the committed accept. That tombstones a committed action, leaves the flight's later completion with no accept, and contradicts the append-only model | reject |

Option C costs nothing: it reuses the accept block's predicate (`GetCommittedContractAcceptIds`) on the same row, so the invariant holds by construction. The stale-slice problem (section 1.1 item 6) cannot bite here, because after the committed UT the contract is Active, not Offered, and Decline is unreachable.

### 11.3 Cancel an Active contract

| Case | What the committed timeline does later | Effect of cancelling now (today) |
|---|---|---|
| C1 | completes it (a recorded completion) | The cancel penalty is charged now, and the committed completion's funds, reputation and science are zeroed. The ghost still completes it on screen, but nothing pays. Downstream committed spending that relied on the reward can become unaffordable: a committed tech unlock is refused and the tree may re-lock. **This is a paradox cascade into committed history**, the exact thing the blocks exist to prevent |
| C2 | fails it (a recorded failure, e.g. the vessel is lost) | Cancel penalty now, **plus** the committed failure penalty later, because penalties are unconditional. **The player pays twice** |
| C3 | cancels it (a recorded KSC cancel) | Two cancel penalties, the same double charge |
| C4 | nothing, or only the derived deadline expiry | No committed row is affected. This is ordinary stock play: cancelling early to take the cheaper penalty is a legitimate stock decision |

| Option | Result | Verdict |
|---|---|---|
| A. Allow silently (today) | C1 cascades, C2 and C3 double-charge. Nothing on screen says so | reject |
| B. Allow, annotate | Still cascades and double-charges; only the player's reading prevents it. It also breaks the "every mark is paired with a block" rule for a clickable kind | reject |
| C. **Block C1-C3, allow C4** | No cascade and no double charge. Cost: the player cannot free that slot before the committed UT. The committed history already occupies the slot until then, and any committed later accept was planned around it, so freeing it early buys nothing the timeline can keep | **adopt** |
| D. Block C1 only | C2 and C3 still double-charge, and the rule is harder to state ("you may cancel this one, but not that one") | reject |
| E. Confirmation popup | A new popup type, which the UI rule forbids. It also cannot state the downstream cascade honestly, because that needs a full recalc per click | reject |

**The rule to adopt:** Cancel is refused for a contract **when the committed timeline has a later explicit completion, failure or cancellation row for it**. Derived deadline expiry does not count; it is a rule of the game, not a committed action. Contracts the committed future leaves open stay cancellable.

**Way-out text (section 5 form):**
- `Completed on Y1 D40 by the committed flight 'Mun Lander 3'. Parsek's timeline is fixed once committed, so this contract cannot be cancelled before then. It completes and frees its slot on that date.`
- For a failure: `Fails on Y1 D40 on your committed timeline ... it fails and frees its slot on that date.`

### 11.4 Implementation shape

The same layering as the existing Accept block:
- **Predicate.** Declines use the existing accept helper. Cancels need a new pure helper over the effective ledger: the committed resolution of a contract after now (row type + UT + recording), cached and invalidated on `LedgerOrchestrator.OnTimelineDataChanged`. It is keyed by UT against the ledger rather than the `MilestoneStore` slice, so the stale-slice problem does not apply. The Active-row annotation (section 10.3 item 1) reads the SAME helper. That is the invariant.
- **Button state.** Postfix `Contract.CanBeDeclined` / `CanBeCancelled` to return false, and CC's `ConfiguredContract` overrides too when CC is loaded. A Harmony patch on the base method does not cover an override. Stock's `btnDecline` / `btnCancel` then grey themselves. The reason goes into the detail panel through the `UpdateInfoPanelContract` postfix.
- **Backstop.** Prefixes on `Contract.Decline()` / `Contract.Cancel()` (non-virtual, and CC's handlers call them) refuse with `CommittedActionDialog` and the same text. Bypass while `GameStateRecorder.IsReplayingActions` is set. `PatchContracts` writes contract state directly and calls neither method (`KspStatePatcher.cs:2501`).
- **Tests.** Pairwise invariant cells in the E18 style: the decline predicate against the accept mark, and the cancel predicate against the Active-row annotation. A pure cell for each of C1-C4, and a CC-override target-resolution cell.

### 11.5 What this decision does not cover

- **A present-day failure from the game world.** Example: a flight in the present crashes the vessel a committed completion relied on, and stock fires `Contract.Fail` from a parameter. It cannot be blocked; it is physics, not a button. It takes the C1 path, with the future completion zeroed and a possible cascade. Record it as the one known path where present play overrides a committed contract outcome, and make the ledger cascade visible, for example by pinning the "possible bug or data corruption" Warn wording to this cause.
- **The double charge in C2 and C3 is a ledger defect in its own right.** A fail or cancel row on a contract that is already resolved should not charge again. Fix it in `ContractsModule` / `FundsModule` independently of the block, because the world-driven path above still reaches it.

## Sources

- **Parsek source, overlay layer:**
  - `StockUiOverlayController.cs`
  - `OverlayBadge.cs`
  - `CurrencyReservationOverlay.cs`
  - `CommittedActionDialog.cs`
- **Parsek source, Harmony patches:**
  - `Patches/TechResearchPatch.cs`
  - `Patches/ContractAcceptPatch.cs`
  - `Patches/KerbalHirePatch.cs`
  - `Patches/KerbalDismissalPatch.cs`
  - `Patches/FacilityUpgradePatch.cs`
  - `Patches/CrewDialogFilterPatch.cs`
  - `Patches/CrewAutoAssignPatch.cs`
  - `Patches/StrategyLifecyclePatch.cs`
  - `Patches/FacilityRepairCapturePatches.cs`
- **Parsek source, stores and state:**
  - `MilestoneStore.cs`
  - `KerbalsModule.cs`
  - `GameActions/StrategiesModule.cs`, `ContractsModule.cs`, `FundsModule.cs`, `ScienceModule.cs`, `MilestonesModule.cs`, `KspStatePatcher.cs`
  - `TombstoneEligibility.cs`
- **Parsek UI:** `UI/CareerStateWindowUI.cs`, `UI/KerbalsPresentation.cs`
- **Docs:**
  - `docs/dev/done/plans/game-state-ui-overlays.md`
  - `docs/dev/todo-and-known-bugs.md` #640, #430, `RESERVATION-OVERLAY-GAPS`, the census badge gap, `GUI-P15-D7`
  - `docs/dev/design-gui-inventory.md`
  - `docs/dev/design-ui-basic-advanced.md`
  - `docs/dev/design-gui-kerbals-window.md`
  - `docs/parsek-game-actions-and-resources-recorder-design.md`
  - `docs/user-guide.md`
- **KSP 1.12.5 decompile:**
  - `RDController`, `RDNode`, `RDNodePrefab`
  - `MissionControl`, `MCListItem`
  - `Administration`, `Strategies.Strategy`
  - `AstronautComplex`, `CrewListItem`, `BaseCrewAssignmentDialog`
  - `KSCFacilityContextMenu`
  - `PartListTooltip`, `EditorPartList`
  - `TrackingStationWidget`
- **Reference mods:**
  - RP-1 `Source/RP0/Harmony/{RDNode,RDController,MissionControl,Administration,AstronautComplex,CrewListItem,BaseCrewAssignmentDialog,KSCFacilityContextMenu,PartListTooltip,EditorPartIcon}.cs`
  - Contract Configurator `MissionControlUI.cs`
  - Strategia `StrategiaStrategy.cs`
  - KSPCommunityFixes `QoL/ShowContractFinishDates.cs`
  - Crew R&R `Interface/SpaceCenterModule.cs`
