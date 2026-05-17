# Switch / Fly Auto-Record Testing Checklist

Player-driven validation for the segment-scoped switch/Fly auto-record feature
(plan `docs/dev/plans/segment-scoped-switch-fly-autorecord.md`). These scenarios
need real UI button clicks and full scene transitions, which the in-game test
framework (`Ctrl+Shift+T`) cannot drive. Run each test in a fresh KSP session
with a save that already contains at least two previously spawned, committed
vessels (e.g. a probe in orbit + a lander on Mun) and `Parsek.cfg` settings at
defaults. The feature is always-on subject to its intrinsic gates
(FocusMode.OwnedVessel for Map, CanSwitchVesselsFar, non-null vessel,
ghost-vessel guards).

After each test, grep `KSP.log` for `[SwitchSegment]` and `[SwitchIntent]` to
confirm the expected log lines fired.

## 1. Tracking Station Fly into a previously spawned committed vessel
1. From the SPACECENTER, open the Tracking Station.
2. Select a previously committed vessel from the list (the one you flew last
   session, ideally — must NOT be the current active vessel).
3. Click the "Fly" button.
4. Verify the scene transitions to FLIGHT with that vessel active.
5. Open the Parsek toolbar window.
6. Pass criteria:
   - Recordings table shows a new in-progress recording for that vessel (NEW
     recording id, not a resume of an existing one).
   - `KSP.log` contains `[SwitchIntentPatch] TS Fly intent armed:` followed
     within 10 seconds by `[SwitchSegment] armed:` with `entryReason=TrackingStationFly`.
   - The first-modification watcher does NOT fire (no `Auto-record started
     (post-switch ...)` line for this switch).

## 2. KSC Vessel Marker Fly into a previously spawned committed vessel
1. From SPACECENTER, click on a nearby-vessel marker on the ground.
2. In the popup, click the "Fly" button.
3. Same pass criteria as #1, with:
   - `[SwitchIntentPatch] KSC Fly intent armed:` (or equivalent log tag for the
     KSC marker arm site)
   - `[SwitchSegment] armed:` with `entryReason=KscMarkerFly`.

## 3. Map view "Switch To" on an owned vessel
1. From FLIGHT, enter Map view (`M` key by default).
2. Click on a nearby owned-vessel icon to bring up its context menu.
3. Click the "Switch To" entry.
4. Verify Map view exits and the new vessel is active.
5. Pass criteria:
   - `[SwitchIntentPatch] Map Switch-To intent armed:` then
     `[SwitchSegment] armed:` with `entryReason=MapSwitchTo` — both in the same
     few frames.
   - New segment recording in the Parsek table for the focused vessel.
   - First-modification watcher disarmed for this switch.

## 4. Map view "Switch To" on an UNOWNED vessel (negative; routes to TS)
1. From FLIGHT Map view, click on a debris / asteroid / unowned vessel icon.
2. Click "Switch To" — this should route to the Tracking Station.
3. In TS, click "Fly".
4. Pass criteria:
   - The Map-side click does NOT immediate-start a segment (no `Map Switch-To
     intent armed:` line for the unowned branch).
   - The subsequent TS Fly click is what arms the intent — verify the
     `TS Fly intent armed:` and `[SwitchSegment] armed: entryReason=TrackingStationFly`
     lines appear after the TS Fly click, not after the original Map click.

## 5. F5 / F9 round-trip during an active switch segment
1. Perform test #1 (TS Fly into a committed vessel).
2. Wait until at least 5 seconds of trajectory have been recorded.
3. Press F5 to quicksave.
4. Continue flying for another 5 seconds.
5. Press F9 to quickload back to the F5 point.
6. Pass criteria:
   - The segment recording is restored intact (same recording id, trajectory
     truncated to the F5 UT).
   - `[SwitchSegment] session loaded: sessionId=...` appears on the F9 load.
   - The Parsek UI shows the segment as still active (RECORDING status).

## 6. Two rapid Switch-To clicks on the SAME target (duplicate guard)
1. From FLIGHT Map view, click "Switch To" on vessel A.
2. As soon as the scene returns, immediately Switch-To on vessel A AGAIN
   (before the first segment has time to record meaningfully).
3. Pass criteria:
   - Only ONE segment recording is created for vessel A.
   - The second intent clears with reason `duplicate-intent-same-target`.
   - The pre-switch Merge / Discard dialog does NOT appear (the same-target
     consume-helper branch handles it, no dialog needed).

## 7. Rapid Switch-To from A to B to C (different targets)
1. From FLIGHT, Switch-To vessel A via Map view. Fly long enough for the
   segment to start recording (5+ seconds).
2. From FLIGHT Map view, click "Switch To" on vessel B.
3. Pass criteria for step 2: a "Pending switch-segment recording" dialog
   opens BEFORE the switch. The dialog has two buttons: **Merge** and
   **Discard**. There is NO Cancel button, and pressing Esc does NOT
   dismiss the dialog (the dialog re-spawns immediately). Verify the
   dialog body shows vessel A's tree name + a short duration matching
   the time spent on vessel A.
4. Click **Discard** in the dialog. The switch proceeds to vessel B; the
   vessel A segment is removed from the recordings table.
5. Repeat for B -> C using **Merge** this time.
6. Pass criteria for the whole sequence:
   - Vessel A's segment was removed (Discard branch).
   - Vessel B's segment was committed to the timeline (Merge branch).
   - Vessel C's session is active in the recordings table.
   - `[SwitchIntentPatch] pre-switch-dialog-opened` fires for each rapid
     click; `pre-switch-dialog-discard-chosen` and
     `pre-switch-dialog-merge-chosen` log the respective button outcomes.

## 8. Scene-exit Discard scoped to the switch segment
1. Perform test #1 (TS Fly into a committed vessel) and fly for 20+ seconds.
2. Press Esc and click "Revert to Tracking Station" (or otherwise trigger a
   scene exit).
3. In the Merge / Discard dialog that appears:
   - Verify the dialog body reads `"{TreeName} - {Duration}"` and the
     duration matches the time you spent in this segment (post-#876 playtest
     fix: a single shared dialog template, the duration line distinguishes a
     short switch-segment from a long-running launch tree).
   - Click **Discard**.
4. Pass criteria:
   - The Parsek recordings table returns to the EXACT pre-switch committed
     recording count (no new segment recording remaining, but every previously
     committed recording still present). Debris recordings spawned during the
     segment (e.g. from in-segment staging) are also removed.
   - Only one dialog appears — no second whole-pending-tree prompt.
   - `KSP.log` shows `[SwitchSegment]` discard summary with the segment id
     listed as removed.

## 9. Scene-exit Merge commits the segment under the committed timeline
1. Perform test #1 again, fly for 20+ seconds.
2. Trigger scene exit.
3. Click **Merge to Timeline** in the scoped dialog.
4. Pass criteria:
   - The new segment recording is committed under the parent vessel's tree
     (visible in the recordings table as a child / continuation of the
     previously committed history).
   - `[SwitchSegment] cleared: ... reason=scoped-merge-success` in the log.

## 10. Map Switch-To dialog body
1. Perform test #3 (Map Switch-To), fly for 20+ seconds.
2. Trigger scene exit.
3. Pass criteria:
   - The dialog body reads `"{TreeName} - {Duration}"` — the same template
     used by long-running launch trees. The duration line is the load-bearing
     distinguisher between a 16s segment and a 30-minute launch.

## 11. Pre-switch dialog: Merge button
1. Perform test #3 (Map Switch-To on vessel A) and fly for 15+ seconds.
2. From Map view, Switch-To on vessel B. The pre-switch
   "Pending switch-segment recording" dialog appears.
3. Click **Merge**.
4. Pass criteria:
   - The switch to vessel B proceeds; vessel A's segment is committed
     under its committed timeline as a new continuation row.
   - A new switch-segment recording starts on vessel B (verify via the
     recordings table and `[SwitchSegment] armed:` log).
   - `[SwitchIntentPatch] pre-switch-dialog-merge-chosen` and
     `pre-switch-dialog committed-prior-segment` appear in the log.

## 12. Pre-switch dialog: Discard button
1. Perform test #3 (Map Switch-To on vessel A) and fly for 15+ seconds.
2. From Map view, Switch-To on vessel B. The pre-switch dialog appears.
3. Click **Discard**.
4. Pass criteria:
   - The switch to vessel B proceeds; vessel A's switch-segment recording
     is removed (subtree-scoped discard).
   - A new switch-segment recording starts on vessel B.
   - `[SwitchIntentPatch] pre-switch-dialog-discard-chosen` and
     `pre-switch-dialog scoped-discard-success` appear in the log.

---

## Reporting issues found

If a test fails, run `python scripts/collect-logs.py switch-fly-test-N` to
snapshot logs/saves/test-results into `../logs/<timestamp>_switch-fly-test-N/`
and file an issue with:
- Which test number failed.
- Which step diverged from the expected outcome.
- The full grep of `[SwitchSegment]` and `[SwitchIntent]` from KSP.log.
