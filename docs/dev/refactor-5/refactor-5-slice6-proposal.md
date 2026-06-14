# Refactor-5 Slice 6 Proposal — Runtime / IMGUI / Harmony Extractions (Deferred)

**Date:** 2026-06-14. **Status:** Proposal (not implemented). **DEFERRED** until a
work block explicitly budgets in-game validation.
**Roadmap:** `docs/dev/refactor-5/refactor-5-slices.md` (shared rules + validation gate).

These are still **behavior-preserving** same-file extractions, but the target code
is Unity / IMGUI / Harmony / reflection-coupled, so there is **no headless xUnit net
for the extracted body** — the pure helpers they call are unit-tested, but the
placement must be validated **in-game**. Do not land any of these without the
relevant in-game scene check. Each carries an extra IMGUI hazard: extractions must
not change the **control count between Layout and Repaint passes** (an IMGUI
extraction that branches control count desyncs the layout).

## IMGUI window extractions

### 6.1 `UI/LogisticsWindowUI.DrawRouteRow` (@679, ~167)

Extract the self-contained Status / Badge / Actions cell blocks into three
`private void` helpers consuming already-computed `leg`/`sendOnceArmed` locals — no
order or control-count change. Secondary: split `DrawIntervalCell` (@861) edit-vs-
display TextField branch; fold the two **detail** steppers (`DrawCadenceStepper`
@1621, `DrawPriorityStepper` @1662 — byte-identical chrome) into `DrawDetailStepper`
(the interval-cell stepper uses a different `-` width, 20f vs 24f, so it is **not**
mergeable). **Validate in-game:** open the Supply Routes window (Active/Paused/
Candidates), confirm no layout shift, badges/steppers behave identically. Pure
helpers (`StatusReason`/`FormatCycleCount`/…) keep their existing unit tests.

### 6.2 `UI/SettingsWindowUI` — `ApplyDefaults`

Extract the Defaults-button field-assignment + `Record*` persistence block (@182–215)
into `ApplyDefaults(ParsekSettings s)`. The 4-diagnostics-toggle dedup is optional
and lower value (couples a delegate to the persistence call). **Validate in-game:**
open Settings, click Defaults, confirm every field + persisted value matches today.

### 6.3 `UI/GroupPickerUI.ApplyGroupPopupChanges` (@356, ~122)

The chain / multi-rec / single-rec branches repeat the "for each `delta.Added`: gate
via `CanAddToUserGroup`, on reject toast+log+collect, else add; then `delta.Removed`
→ remove" loop → extract `ApplyAddsAndRemoves(...)` per recording/index. Gating
predicates already pure + tested. **Validate in-game:** assign/unassign a recording
and a chain across groups, confirm rejects still toast.

### 6.4 `UI/TestRunnerUI` — section extraction

`DrawTestRunnerWindow` (@285, ~110) → `DrawControlsBar` / `DrawSummarySection` /
`DrawBottomBar`; `DrawTestCategoryList` (@148, ~102) → `DrawCategoryHeader` /
`DrawTestItem`. Care with `GUI.enabled`/`GUI.color` state coupling. **Validate
in-game:** open the test runner (Ctrl+Shift+T), confirm layout + run controls.

### 6.5 `StockUiOverlayController` — disabled-overlay gate dedup

`DecorateRnD` / `DecorateAstronaut` / `DecorateMissionControl` share a 3×-identical
disabled-gate + log block (only the scene word differs) → `OverlaysEnabledOrLogSkip(
string screenName)`. Optional: hoist the inline badge `new Color(…)` tints to named
`static readonly Color`. **Validate in-game:** open R&D / Astronaut Complex / Mission
Control, confirm badges render and the disabled path logs identically. (This file
has ~4 static fields and no `ResetForTesting`, but they're Unity-instance/event
state — note, not an action item.)

## Harmony patch bodies (highest care)

### 6.6 `Patches/GhostOrbitLinePatch.Postfix` (@640, ~396)

~8 terminal branches each repeat `line.active=…; __instance.drawIcons=…;
ghostsWithSuppressedIcon.Add/Remove(pid); LogOrbitLineDecision(pid, reason, …);
return;` → `ApplyLineDecision(__instance, pid, active, icons, suppress, reason)`.
The helper **must take Add-vs-Remove and the drawIcons value as parameters** — they
differ per branch (checklist item 10). Leave `GhostOrbitIconDrivePatch.Prefix`
(@125) inline (verbatim-stock replication; NaN-guard/`___updateUT` write ordering is
load-bearing). **Validate in-game:** map view — ghost above/below atmosphere,
burn-seam, transfer descent; confirm no icon/line blink or double-marker regression.

### 6.7 `Patches/MapFocusObjectOnSelectPatch.Prefix` (@119, ~362)

Extract the two contiguous sub-blocks: the Case-C "separate committed target"
classification (~265–297 → `TryClassifySeparateCommittedTarget`) and the
`OpenDialog` arm's two no-op-auto-discard blocks (~328–384). The pure
`DecidePreSwitchDialogAction` is already extracted. **Do NOT** dedup the 4 dialog
button-handlers (session vs no-session bookkeeping diverges — that's a Pass 2 owner,
separate). The marker-arm ordering relative to `SetActiveVessel`'s synchronous
`onVesselChange` is load-bearing. **Validate in-game:** Map Switch-To to a loaded
and an unloaded vessel; with and without an armed session.

### 6.8 `SceneExitInterceptor.Prefix` (@637, ~206)

Extract only the safest single contiguous block — the no-active-tree session-tree
dialog (~723–795 → `TryShowNoActiveTreeDialog`). **Do NOT** split the whole decision
matrix (phase order is behavior-critical; token-consume before filter, auto-discard
before HasActiveTree routing). **Validate in-game:** Esc → Space Center with an
active tree, with a pending dialog, and with an idle tree.

## Reflection / KSP-solver coupled (no headless net for the moved body)

### 6.9 `PatchedConicSnapshot.SnapshotPatchedConicChain` (@97, ~185)

Extract the patch-capture loop into `TryCapturePatches(...)` (validate → patch-limit
reflection set → capture loop → finally-restore). Reflection field cached one-shot —
keep that ordering. **Validate:** existing snapshot tests + an in-game patched-conic
coast (multi-SOI transfer) smoke.

### 6.10 `OrbitSeedResolver.TryDeriveTailOrbitSeed` (@57, ~155)

Extract the build-orbit phase (`TryBuildTailOrbitSegment`), incl. the MapPresence-
history branch. Reflection for the rotation field — preserve. Has `*ForTesting`
overrides. **Validate:** the resolver's unit tests + an in-game orbit-tail smoke.

## New small owner types (Pass 2, IMGUI)

### 6.11 `MissionLogSuppressionScope` (IDisposable)

The three `SuppressLogging` save/restore `try/finally` blocks in `MissionsWindowUI`
(@444, @506, @1242) — toggling `MissionStructureBuilder` / `MissionPeriodicity` /
`MissionLoopUnitBuilder` `.SuppressLogging` — fold into one IDisposable struct
mirroring `SuppressionGuard.cs`. Pure-testable guard, runtime call sites.
**Validate in-game:** open the Missions tab, confirm logs are still suppressed
during the draw and restored after.

## Notes

- Every item here keeps the public entry-point signature unchanged.
- The IMGUI control-count-between-passes invariant is the most common way a "pure"
  IMGUI extraction silently regresses — the clean-context review must check it
  explicitly for 6.1–6.5.
