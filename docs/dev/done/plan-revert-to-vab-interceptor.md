# Plan: Revert-to-VAB/SPH interceptor for active re-fly sessions

Branch: `feat/revert-to-vab-interceptor` (to be created off `feat/rewind-staging`)
Worktree: `Parsek-revert-to-vab-interceptor`
Date: 2026-04-18

## Problem

v0.9's `RevertInterceptor` (`Source/Parsek/RevertInterceptor.cs:28`) only
patches `FlightDriver.RevertToLaunch`. The stock in-flight menu also exposes
**Revert to VAB** (or **Revert to SPH** for spaceplanes), which KSP routes
through a different method entirely — `FlightDriver.RevertToPrelaunch`.
While a re-fly session is active (`ParsekScenario.Instance.ActiveReFlySessionMarker != null`),
pressing Revert-to-Launch correctly spawns the 3-option `ReFlyRevertDialog`
(`Source/Parsek/ReFlyRevertDialog.cs:53`), but pressing Revert-to-VAB
bypasses the interceptor completely: the scene swaps to the editor, the
`ReFlySessionMarker` survives untouched, and on the next flight load
`LoadTimeSweep.Run` (§6.16) invalidates the marker on its first failed
durable-field check and logs `[ReFlySession] Warn: Marker invalid field=...;
clearing`. Functionally the session cleans up, but the player silently loses
their chance to pick Retry or to explicitly discard the re-fly tree. The
observable user harm is twofold: the retired sibling's Unfinished Flight
entry stays because the tree is never purged, *and* the player has no
opportunity to pick "Retry" and re-enter the RP without taking a full detour
through the editor.

## Scope

**In scope:**

- Add a Harmony prefix to `FlightDriver.RevertToPrelaunch` (analogous to the
  existing `RevertToLaunch` prefix) that dispatches to the same
  `ReFlyRevertDialog` when a re-fly session is active.
- Decide what each of the three dialog options means in a VAB/SPH-target
  revert context and wire the callback handlers accordingly.
- Update §6.14 of the design doc and the user guide's "Revert during re-fly"
  bullet.
- Tests at the same granularity as the existing `ReFlyRevertDialogTests`
  (`Source/Parsek.Tests/ReFlyRevertDialogTests.cs`).

**Out of scope:**

- Changing the behaviour of Revert-to-Launch in any way.
- Changing `LoadTimeSweep` or `MarkerValidator` — they stay the
  defence-in-depth layer for any code path that bypasses the interceptor
  in future (mods, KSP updates).
- Adding a fourth button to the dialog, or a new always-on "Retry from RP
  instead" redirect affordance.
- Deleting or altering `RevertDetector`'s `OnRevertToPrelaunchFlightState`
  subscription (the event fires from inside `RevertToPrelaunch` itself; if
  the interceptor blocks the body, the event never fires, so no extra work
  is needed — see §"Interaction with `RevertDetector`" below).
- Any changes to the flight-results revert buttons (those already go
  through `FlightDriver.RevertToLaunch` / `RevertToPrelaunch` via the
  stock paths and will be picked up automatically).

## Design options considered

### (a) Second prefix, same dialog, "Retry stays at launchpad" semantics — RECOMMENDED

Add a second `[HarmonyPatch]` on `FlightDriver.RevertToPrelaunch` that
reuses the existing `ReFlyRevertDialog.Show` verbatim:

- **Retry from Rewind Point** — exactly the same as the launch-revert
  flow: clear the marker, re-invoke `RewindInvoker.StartInvoke(rp, slot)`.
  The scene target of "Retry" is already the rewind point (which is
  almost always a flight-scene quicksave of the moment just after the
  split), NOT the editor. So the semantics of Retry do not change based
  on whether the player clicked "Revert to Launch" or "Revert to VAB" —
  they want to try the re-fly again from the RP.
- **Full Revert (Discard Re-fly)** — purge the tree via
  `TreeDiscardPurge.PurgeTree`, then re-trigger
  `FlightDriver.RevertToPrelaunch` (not `RevertToLaunch`). With the
  marker cleared the new prefix returns true, the stock body runs, and
  the scene swaps to the VAB/SPH as the player originally requested.
  This preserves the click intent: "I want to go to the editor."
- **Continue Flying** — dismiss, resume flight, no state change.

Pros:
- One new prefix, zero new UI. Copy stays identical to the Revert-to-Launch
  dialog; the same dialog title / body / buttons work verbatim because
  none of the body text calls out "launchpad" specifically (current copy
  talks about "this attempt", "this re-fly", "rewind point quicksave").
- Dialog lock flow + callback wiring + test seams already exist.
- Full Revert preserves the player's click intent (they wanted the
  editor; they get the editor after the tree is purged).
- Retry's intent ("give me another shot at this split") works identically
  regardless of which Revert button was clicked.

Cons:
- Dialog body currently reads "… and re-load the rewind-point quicksave.
  The Unfinished Flight entry stays available so you can try again." —
  this is still accurate for a VAB-click context, but the player might
  read "Full Revert … and career state stays where it is now" and be
  momentarily surprised that Full Revert drops them in the VAB instead
  of the launchpad. Mitigation: a minor body copy tweak — the
  "What Full Revert does" bullet should say "lets the stock Revert
  continue" instead of naming a specific destination. See
  §"Implementation plan" for exact wording.
- Retry after clicking "Revert to VAB" lands the player back in FLIGHT.
  That's arguably not what they clicked — but it IS what they chose in
  the dialog, and the dialog is explicit about it.

### (b) Separate VAB-context dialog with redirected Retry semantics

Introduce a second dialog variant (`ReFlyRevertDialog.ShowForPrelaunch` or
a `DialogContext` enum) for VAB/SPH reverts. Retry in the VAB context
would either (i) disappear / be replaced with a "Retry from Rewind Point
(Launch instead)" button, or (ii) redirect through the launchpad-revert
path (RP restoration but via Launch semantics), matching the reasoning
that a player who clicked Revert-to-VAB may have been trying to abandon
the re-fly rather than retry it.

Pros:
- Arguably more "clicky" — respects the specific button the player
  pressed. A VAB-click might be a stronger discard intent than a
  Launch-click.

Cons:
- Splits the 3-option copy into two variants. More code, more tests,
  more copy to maintain.
- Retry fundamentally means "re-enter the split with a clean
  provisional" — and that target is defined by the RP, not by which
  button the player clicked. Redirecting Retry based on click context
  muddles its meaning: now Retry from a VAB click lands the player on
  the launchpad pre-RP-restore, which is a state the RP contract
  explicitly does not guarantee. The RP quicksave was taken at the
  split moment in FLIGHT; restoring it via the launchpad path would
  require extra scene-transition plumbing.
- The user research for this is weak: we don't have a report that
  VAB-clicking players expected Retry to vanish. Without a signal, (a)
  is the conservative default.

### (c) Do nothing — let stock Revert-to-VAB proceed, rely on `LoadTimeSweep`

Stock revert runs. Scene swaps to editor. On the next flight load,
`MarkerValidator.Validate` fails (the provisional recording was left in
`CommittedRecordings` without being committed-out, but some of the
six durable fields will drift — most likely `InvokedUT` stays in the
future relative to the new load's UT, OR the RP has been reaped, OR the
provisional ended up with an unexpected MergeState after the scene
unload). The sweep clears the marker + discards the provisional +
prunes session-provisional RPs. Career-state / tree state is unchanged.

Pros:
- Zero code.
- The `LoadTimeSweep` layer is already doing this work as a safety net
  for other edge cases (F5 mid-session, crash-quit, etc. per §7.12 and
  §7.11), so this path is already exercised.

Cons:
- Silent cleanup. Player loses the explicit choice — no dialog, no
  question. Retry is not available.
- The retired sibling's Unfinished Flight entry stays because the tree
  is never purged. Player sees the Unfinished Flight row in the
  Recordings Manager, clicks Rewind, and… presumably starts another
  re-fly session with a fresh provisional. That's actually workable,
  but it's not what we promised in §6.14 of the design doc. The design
  doc unambiguously promises a three-option dialog on "pressing stock
  Revert-to-Launch" — it doesn't mention Revert-to-VAB, so we're not
  *lying* today, but the user's expectation is clearly that "any
  revert during a re-fly" offers the three-option menu.
- Warn-level log on every load ("`Marker invalid field=...; clearing`")
  makes the load log noisier for a user-triggered operation.

### (d) Patch the stock menu click path instead of `FlightDriver.*`

Intercept the button click in KSP's in-flight menu UI (the
`FlightUIMenu`-ish control that exposes the Revert sub-menu), showing
the dialog before either `FlightDriver` method is called. Rejected: the
KSP menu surface for this is private UI code, not a stable API; there's
also the flight-results "Revert" buttons which go through
`FlightDriver.*` directly. Intercepting at `FlightDriver` catches all
paths (Esc menu, flight-results dialog, any future KSP UI rearrangement,
third-party mods that call `RevertToPrelaunch` programmatically).

### Recommendation

**Ship (a).** It's the minimum viable fix, reuses all the existing
dialog infrastructure, preserves Retry's RP-anchored semantics, and
honours the player's "go to editor" intent on Full Revert by driving
`RevertToPrelaunch` (not `RevertToLaunch`) after the tree purge.

Body-copy is tweaked in exactly one line to drop the "clear the rewind
point … for this split" phrasing of the Launch-click context so it reads
correctly for either destination. Everything else (button labels, title,
test seams, input lock) stays as it is today.

## Implementation plan (assuming option (a))

### Files to modify

- `Source/Parsek/RevertInterceptor.cs`
  - The class is currently decorated with a single
    `[HarmonyPatch(typeof(FlightDriver), nameof(FlightDriver.RevertToLaunch))]`
    attribute at line 28. Harmony supports multiple target methods via
    the `[HarmonyPatch(...)]` + `[HarmonyPatchAll]` pattern, *or* two
    separate patch classes. For clarity and to keep the handler logic in
    one file, extract the prefix into a helper and create two thin
    patch classes:

    ```csharp
    [HarmonyPatch(typeof(FlightDriver), nameof(FlightDriver.RevertToLaunch))]
    internal static class RevertToLaunchInterceptor
    {
        [HarmonyPrefix]
        internal static bool Prefix() =>
            RevertInterceptor.Prefix(RevertTarget.Launch);
    }

    [HarmonyPatch(typeof(FlightDriver), nameof(FlightDriver.RevertToPrelaunch))]
    internal static class RevertToPrelaunchInterceptor
    {
        [HarmonyPrefix]
        internal static bool Prefix() =>
            RevertInterceptor.Prefix(RevertTarget.Prelaunch);
    }
    ```

    `RevertInterceptor` itself no longer carries the
    `[HarmonyPatch(...)]` attribute directly; it becomes the shared-state
    container + dispatcher. This structure mirrors how KSP mods commonly
    pattern Harmony patches: one patch class per target method, delegating
    to a shared implementation.
  - Add a `RevertTarget` enum (`Launch` / `Prelaunch`).
  - `Prefix` signature becomes `internal static bool Prefix(RevertTarget target)`.
    The gate logic (`ShouldBlock`) is identical. The dispatch to
    `ReFlyRevertDialog.Show` passes `target` through to a new overload of
    `Show` (see below) so the dialog can tweak its body copy; the retry
    / full-revert / cancel handlers also take `target` so
    `FullRevertHandler` knows which stock method to call after the purge.
- `Source/Parsek/ReFlyRevertDialog.cs`
  - Add an overload (or a new parameter) for `Show` that accepts a
    target descriptor. Internally this picks which "Full Revert" copy
    line to show. Recommended: change the existing copy from
    > "Full Revert (Discard Re-fly): throw away the current re-fly
    > attempt and clear the rewind point + supersede / tombstone state
    > for this split. The committed recordings (original launch, any
    > prior siblings) stay in the timeline. Career state stays where
    > it is now."

    to:

    > "Full Revert (Discard Re-fly): throw away the current re-fly
    > attempt and clear the rewind point + supersede / tombstone state
    > for this split, then let the stock Revert continue. The committed
    > recordings (original launch, any prior siblings) stay in the
    > timeline. Career state stays where it is now."

    The phrase "let the stock Revert continue" is accurate whether the
    stock Revert means Launch or VAB/SPH; it doesn't need to vary by
    target. Keeps the dialog copy single-source.
  - Dialog title could remain "Revert during re-fly" (no need to say
    "Revert to Launch during re-fly"). Title is already generic.
- `Source/Parsek/RevertInterceptor.cs` — `FullRevertHandler`
  - Currently calls `FlightDriver.RevertToLaunch()` at line 237. Take a
    `RevertTarget target` parameter; route to `FlightDriver.RevertToLaunch()`
    when `target == Launch` and `FlightDriver.RevertToPrelaunch()` when
    `target == Prelaunch`. The `StockRevertInvokerForTesting` seam
    becomes a `Dictionary<RevertTarget, Action>` or — simpler — gets a
    second seam (`StockPrelaunchRevertInvokerForTesting`).
  - The catch block's log message updates accordingly:
    `FlightDriver.RevertToLaunch threw` -> `FlightDriver.Revert{target} threw`.
- `Source/Parsek/RevertInterceptor.cs` — `RetryHandler`, `CancelHandler`
  - No semantic change. They do not care about the target (Retry always
    means "re-invoke StartInvoke(rp, slot)" regardless of the click;
    Cancel always means "dismiss, keep flying"). However, both should
    take the `RevertTarget target` parameter for logging symmetry so
    the `[ReFlySession] Info: End reason=retry sess=<s> rp=<r>
    slot=<i>` log gains a `target=Prelaunch` (or `target=Launch`) key.
- `Source/Parsek/RevertInterceptor.cs` — logging
  - All four log sites in `Prefix`, `RetryHandler`, `FullRevertHandler`,
    `CancelHandler` gain a `target=<Launch|Prelaunch>` key. Verbose-rate-
    limited not appropriate here (these are one-shot per click). Use
    existing `ParsekLog.Info` calls, extended.
- `Source/Parsek.Tests/ReFlyRevertDialogTests.cs`
  - Add the Prelaunch-context tests (see §Tests below). The file's
    `MakeMarker` helper and scenario-setup plumbing stay as-is.
- `docs/parsek-rewind-to-separation-design.md`
  - §6.14 paragraph — widen the opening sentence to say "intercepts both
    `FlightDriver.RevertToLaunch` AND `FlightDriver.RevertToPrelaunch`"
    and note the Full Revert path drives whichever method the player
    clicked. See §Documentation updates below.
  - §A.4 scenario walkthrough — add a one-sentence "This also applies
    to Revert-to-VAB / Revert-to-SPH." line.
- `docs/user-guide.md`
  - The "Revert during re-fly" bullet currently reads
    > "pressing stock Revert-to-Launch while a session is active …"
    Change to "pressing stock Revert-to-Launch or Revert-to-VAB / -SPH".
- `CHANGELOG.md`
  - Under the current unreleased section (or 0.9.1 if that's been cut
    by the time this ships), one line:
    > "Revert-to-VAB / Revert-to-SPH during an active re-fly session
    > now shows the same 3-option dialog as Revert-to-Launch; Full
    > Revert returns the player to the editor as originally clicked."

### Harmony-patch specifics

- Target: `FlightDriver.RevertToPrelaunch` — zero-argument instance method
  (but Harmony dispatches without the instance for static vs instance the
  same way as the existing `RevertToLaunch` prefix, which is also a
  zero-arg signature).
- Prefix signature: `internal static bool Prefix()` returning `bool`.
  `true` = run stock body, `false` = skip stock body. Same as the
  existing prefix.
- No `__result`, no `__instance` dependencies. The prefix consults
  `ParsekScenario.Instance?.ActiveReFlySessionMarker` and nothing from
  the call site.
- Patch registration: if the existing Harmony bootstrapper uses
  `harmony.PatchAll()` on the assembly, the new class is picked up
  automatically. Verify via a log grep on startup for
  "Patched FlightDriver.RevertToPrelaunch".

### Dialog changes

- One body-copy line tweak (see §"Files to modify" above). No new
  buttons. No new callbacks.
- Dialog lock id + dialog name stay the same — a player can only trigger
  one revert at a time, and the dialog dismisses itself before the next
  click can fire.

### Callback handler changes

| Handler | Launch context | Prelaunch context |
|---|---|---|
| `RetryHandler(marker, target)` | Unchanged — clear marker, re-invoke `RewindInvoker.StartInvoke(rp, slot)`. Log includes `target=Launch`. | Identical to Launch. The RP restoration drops the player back in FLIGHT regardless of which button they clicked; Retry's semantics are "re-enter the split", not "go back to whatever scene you clicked". Log includes `target=Prelaunch`. |
| `FullRevertHandler(marker, target)` | Unchanged — purge tree, call `FlightDriver.RevertToLaunch`. | Purge tree, call `FlightDriver.RevertToPrelaunch`. With marker cleared, the new prefix returns `true` and the stock VAB/SPH swap proceeds. |
| `CancelHandler(marker, target)` | Unchanged — log only. | Identical to Launch. |

### Interaction with `RevertDetector`

`RevertDetector` (`Source/Parsek/RevertDetector.cs:25`) subscribes to both
`GameEvents.OnRevertToLaunchFlightState` and
`OnRevertToPrelaunchFlightState`. Those events fire from **inside**
`FlightDriver.RevertToLaunch` / `RevertToPrelaunch` — specifically, after
the save-state is rolled back but before `HighLogic.LoadScene`.

If the interceptor's prefix returns `false` (blocking the stock body),
the event is **never fired** — the body that raises the event doesn't
run. So `RevertDetector.PendingKind` stays `None`, and
`ParsekScenario.OnLoad`'s revert-aware branch is correctly not taken.
This is already the behaviour of the Launch-revert interceptor; the
Prelaunch-revert interceptor works identically by virtue of following
the same pattern.

When `FullRevertHandler` re-calls `FlightDriver.RevertToPrelaunch` after
the marker is cleared, the prefix returns `true` (marker is null), the
stock body runs, `OnRevertToPrelaunchFlightState` fires, and
`RevertDetector.PendingKind = Prelaunch`. That's the correct behaviour:
the load after Full Revert *is* a revert, and the existing revert-load
handling in `ParsekScenario.OnLoad` should treat it as one.

No change needed to `RevertDetector`.

### `ParsekScenario.OnDestroy` / marker cleanup

No change. The FullRevert path purges the marker through
`TreeDiscardPurge.PurgeTree` before the stock revert runs; the stock
revert's subsequent scene-unload calls `OnDestroy` with a null marker
and there is nothing to clean up. The Retry path clears the marker in
`RetryHandler` before re-invoking `StartInvoke`; scene-unload during the
re-invoke sees a cleared marker, and `StartInvoke` writes a fresh one
atomically at the post-load `Activate` step (§6.10). The Cancel path
leaves state untouched and no scene swap happens.

Defence-in-depth: if any of these paths drops the marker on the floor
(bug regression, mod conflict), `LoadTimeSweep` runs on the next
flight-scene load and the existing six-field validation catches the
broken marker.

## Tests

All additions land in `Source/Parsek.Tests/ReFlyRevertDialogTests.cs`.
The file already has the scenario setup, marker builder, and test-seam
plumbing; these are just new test methods.

1. `Prefix_PrelaunchTarget_NoMarker_AllowsStockRevert`
   — scenario has no marker; `RevertInterceptor.Prefix(RevertTarget.Prelaunch)`
   returns `true`; log contains `"Prefix: no active re-fly session —
   allowing stock RevertToPrelaunch"`.
2. `Prefix_PrelaunchTarget_WithMarker_BlocksAndShowsDialog`
   — scenario has an active marker; set
   `RevertInterceptor.DialogShowForTesting` to capture the call;
   `Prefix(RevertTarget.Prelaunch)` returns `false`; the dialog hook
   was invoked exactly once with the current marker; log contains
   `"blocking stock RevertToPrelaunch"` and `target=Prelaunch`.
3. `FullRevertHandler_PrelaunchTarget_CallsPrelaunchStockRevert`
   — set `RevertInterceptor.StockPrelaunchRevertInvokerForTesting` to
   a counter; `FullRevertHandler(marker, RevertTarget.Prelaunch)` runs
   the counter once, clears the marker, and the log shows
   `End reason=fullRevert` with `target=Prelaunch`.
4. `FullRevertHandler_LaunchTarget_StillCallsLaunchStockRevert`
   — regression: the existing Launch-context behaviour is unchanged.
   Counter on `StockRevertInvokerForTesting` fires; not the Prelaunch
   seam.
5. `RetryHandler_PrelaunchTarget_ReinvokesSameRp`
   — `RewindInvokeStartForTesting` captures the
   `(RewindPoint, ChildSlot)` tuple; assert it matches the marker's
   RP id and origin child id; the log line
   `End reason=retry sess=<old> rp=<rp>` now also contains
   `target=Prelaunch`.
6. `CancelHandler_PrelaunchTarget_LogsWithTarget`
   — log contains `Revert dialog cancelled` and `target=Prelaunch`.

Existing Launch-context tests do not change except for the log-line
assertions — every handler's log line now gains a `target=Launch` key.
Those assertions get a `.Contains("target=Launch")` added; the test
changes are mechanical.

In-game verification (added to the existing manual test list, not to
`InGameTests/` unless the user specifically wants automated in-game
coverage here — the pure callback paths are unit-tested and the scene
swap is stock KSP behaviour):

1. Start a re-fly session. Esc > **Revert to VAB**. Expect the
   3-option dialog with the updated copy. Click **Continue Flying**;
   expect flight resumes, no scene swap.
2. Same starting state. Esc > **Revert to VAB**. Click **Retry from
   Rewind Point**. Expect the scene reloads to the split moment
   (FLIGHT scene, not VAB). Verify `ReFlySessionMarker` on disk has a
   new `SessionId`.
3. Same starting state. Esc > **Revert to VAB**. Click **Full Revert
   (Discard Re-fly)**. Expect the scene swaps to VAB. Verify on
   next-flight load that the tree is gone and no sweep-Warn fires.
4. Regression: re-fly active, **Revert to Launch** still works
   unchanged (all three options).
5. Regression: no re-fly active, **Revert to VAB** proceeds without
   any dialog.
6. SPH variant: same tests on a spaceplane mission that uses the SPH
   (KSP routes the editor target automatically; the interceptor
   doesn't need to distinguish — it's still `RevertToPrelaunch`).

## Documentation updates

### `docs/parsek-rewind-to-separation-design.md` §6.14

Current opening sentence (line 842):

> `RevertInterceptor` is a `[HarmonyPatch(typeof(FlightDriver),
> nameof(FlightDriver.RevertToLaunch))]` prefix. When
> `ParsekScenario.Instance?.ActiveReFlySessionMarker == null`, the prefix
> returns `true` and the stock revert runs.

Replace with:

> `RevertInterceptor` is a pair of Harmony prefixes — one on
> `FlightDriver.RevertToLaunch` (for Esc > Revert to Launch and the
> flight-results dialog's Launch revert button) and one on
> `FlightDriver.RevertToPrelaunch` (for Esc > Revert to VAB / SPH).
> Both prefixes share the same gate: when
> `ParsekScenario.Instance?.ActiveReFlySessionMarker == null`, the
> prefix returns `true` and the stock revert runs.

The three-bullet list at §6.14 stays; update the **Full Revert** bullet:

> **Full Revert (Discard Re-fly)** — `FullRevertHandler`:
> `TreeDiscardPurge.PurgeTree(treeId)` (clears RPs scoped to the tree,
> supersedes with either endpoint in the tree, tombstones for actions
> in tree recordings, active marker if it references the tree, active
> journal if it references the tree). Then re-drive whichever stock
> revert method the player originally clicked — `RevertToLaunch` for a
> Launch click, `RevertToPrelaunch` for a VAB/SPH click — so the
> player lands in the scene they asked for. Log
> `[ReFlySession] Info: End reason=fullRevert sess=<sid> target=<Launch|Prelaunch>`.

### §A.4 scenario walkthrough

Append a single line at the end of §A.4:

> The same dialog appears when the player picks Revert-to-VAB (or
> Revert-to-SPH for a spaceplane); Full Revert in that case drives the
> scene swap to the editor after the tree is purged.

### `docs/user-guide.md`

Line 72:

> **Revert during re-fly** — pressing stock Revert-to-Launch while a
> session is active shows a three-option dialog: Retry from Rewind
> Point, Full Revert (Discard Re-fly), Continue Flying.

becomes

> **Revert during re-fly** — pressing stock Revert-to-Launch or
> Revert-to-VAB / -SPH while a session is active shows a three-option
> dialog: Retry from Rewind Point, Full Revert (Discard Re-fly),
> Continue Flying.

### `CHANGELOG.md`

Under the current "0.9.0" section (or the next patch section if 0.9.0
has already shipped by the time this lands), one bullet line under a
`### Fixed` (or `### Changed`) subheading:

```
- Revert-to-VAB / -SPH during an active re-fly session now shows the
  same 3-option dialog as Revert-to-Launch; Full Revert returns the
  player to the editor as originally clicked.
```

### `docs/dev/todo-and-known-bugs.md`

No new entry needed — this is a feature completion, not a bug triage
item. If there's an open "Rewind polish" slot, mark it `~~done~~`.

## Commit strategy

Three commits on `feat/revert-to-vab-interceptor`, mirroring the shape
of the original Phase 12 commit series:

1. **Commit 1** — code only: split `RevertInterceptor.cs` into the
   dispatcher + two thin patch classes, add the `RevertTarget` enum,
   update `Prefix`, `FullRevertHandler`, `RetryHandler`, `CancelHandler`
   to take the target, add the Prelaunch-specific test seam. Build
   green, existing tests pass (the signature change to take `target`
   is backward-compatible via the Launch-context enum value; existing
   tests opt in via a default parameter or are updated in the same
   commit).
2. **Commit 2** — dialog copy tweak + the 6 new Prelaunch-context tests
   + the 5-ish `.Contains("target=Launch")` additions to existing
   tests. All tests green.
3. **Commit 3** — docs: design doc §6.14 + §A.4, user-guide bullet,
   CHANGELOG entry. Docs-only; no code churn.

Commit 1 + 2 can be collapsed if the reviewer prefers a single
"feature" commit; the docs commit stays separate per the
[per-commit doc discipline](../../.claude/CLAUDE.md) unless the
reviewer asks for one combined commit.

Merge path: PR into `feat/rewind-staging` (not main). That branch
already hosts the v0.9 rewind work; this fix is a natural extension.

## Risks / open questions

1. **Harmony patch registration order.** The existing
   `RevertInterceptor` uses a single `[HarmonyPatch]` attribute on the
   class. Splitting into two patch classes changes the attribute
   layout. If the repo's Harmony bootstrap does `PatchAll()` on the
   assembly, this is transparent. If it manually enumerates patch
   classes, the new `RevertToPrelaunchInterceptor` needs to be added
   to the enumeration. **Action before implementation:** grep
   `Source/Parsek/ParsekHarmony.cs` for the patcher's patch-discovery
   strategy and confirm `PatchAll()` is in use.
2. **Should Retry in a VAB-click context redirect to Launch instead?**
   Chose **no** in option (a) because Retry's semantics are
   RP-anchored, not click-anchored. But this is a judgement call and
   the user may have a different preference. If the user wants the
   (b) semantic instead, revisit before Commit 1.
3. **Flight-results dialog revert buttons.** If the player dies during
   a re-fly, KSP shows its flight-results dialog with Revert-to-Launch
   / Revert-to-VAB / Recover options before the in-flight menu ever
   opens. Those buttons call `FlightDriver.RevertToLaunch` /
   `RevertToPrelaunch` directly, so the interceptor catches them too.
   Confirm via in-game test 3 above that the dialog appears from the
   flight-results revert path, not just from Esc > Revert.
4. **KSP stock behaviour difference between Launch and Prelaunch
   reverts with respect to career state.** `RevertToLaunch` replays
   from the launchpad with clock reset; `RevertToPrelaunch` goes to
   editor. Both roll the KSP save back to the pre-launch checkpoint.
   The existing Launch-revert Full Revert flow already purges our tree
   before the stock revert runs, so career state hand-off is clean.
   The Prelaunch-revert Full Revert should behave identically because
   `TreeDiscardPurge` is scene-agnostic. No expected issue, but
   in-game test 3 is the confirmation.
5. **Mod interactions.** KerbalKonstructs, BetterBurnTime, and other
   mods sometimes patch `FlightDriver.RevertToPrelaunch` themselves.
   Harmony prefix-ordering follows the priority attribute; our prefix
   has no priority set (defaults to `Normal`). If another mod's
   prefix returns `false` before ours runs, we never see the click.
   Acceptable — if a mod blocks the revert, our dialog shouldn't
   pre-empt it. No action needed unless a specific conflict report
   surfaces.

## Out of scope (explicit)

- Changes to any ghost / playback / camera code paths.
- Any modification to `TreeDiscardPurge.PurgeTree` — it already does
  the right thing for both click contexts.
- New UI elements beyond the body-copy word change.
- Localization pass on the dialog copy (if/when the mod adds
  localization, both dialog paths pick it up via a single string key).
- A separate "I clicked Revert-to-VAB, are you sure?" confirmation
  on non-re-fly sessions (out of mission scope; stock KSP already
  shows its own "Revert?" prompt).

## Implementation checklist

1. [ ] Grep `ParsekHarmony.cs` to confirm `PatchAll()` discovery.
2. [ ] Introduce `RevertTarget` enum + split `RevertInterceptor` into
       two patch classes + a dispatcher.
3. [ ] Add `StockPrelaunchRevertInvokerForTesting` seam.
4. [ ] Update `Prefix`, `FullRevertHandler`, `RetryHandler`,
       `CancelHandler` to accept and log the target.
5. [ ] Tweak the Full Revert body copy in `ReFlyRevertDialog.cs`.
6. [ ] Add 6 new tests in `ReFlyRevertDialogTests.cs`.
7. [ ] Update existing tests' log-contains assertions to include
       `target=Launch`.
8. [ ] Build, deploy, verify DLL contains a distinctive new UTF-16
       string (e.g. `"target=Prelaunch"`).
9. [ ] Run full `dotnet test`; expect green.
10. [ ] In-game verify the 6 scenarios above.
11. [ ] Update §6.14, §A.4, user-guide bullet, CHANGELOG entry.
12. [ ] Commit on `feat/revert-to-vab-interceptor`, push, open PR into
        `feat/rewind-staging`.
