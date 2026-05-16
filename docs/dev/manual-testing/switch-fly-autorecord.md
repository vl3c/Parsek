# Switch / Fly Auto-Record Testing Checklist

Player-driven validation for the segment-scoped switch/Fly auto-record feature
(plan `docs/dev/plans/segment-scoped-switch-fly-autorecord.md`). These scenarios
need real UI button clicks and full scene transitions, which the in-game test
framework (`Ctrl+Shift+T`) cannot drive. Run each test in a fresh KSP session
with a save that already contains at least two previously spawned, committed
vessels (e.g. a probe in orbit + a lander on Mun) and `Parsek.cfg` settings at
defaults (all three switch/Fly sources ON).

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

## 7. Rapid Switch-To from A to B to C (different targets)
1. From FLIGHT, Switch-To vessel A via Map view.
2. As soon as the scene returns, Switch-To vessel B.
3. As soon as the scene returns again, Switch-To vessel C.
4. Pass criteria:
   - Three distinct segment recordings exist (one each for A, B, C).
   - Each `[SwitchSegment] armed:` line carries a different `sessionId`.

## 8. Scene-exit Discard scoped to the switch segment
1. Perform test #1 (TS Fly into a committed vessel) and fly for 20+ seconds.
2. Press Esc and click "Revert to Tracking Station" (or otherwise trigger a
   scene exit).
3. In the Merge / Discard dialog that appears:
   - Verify the dialog title and body include the verb "Fly" (e.g. "Keep your
     new flight on 'VesselName'?").
   - Click **Discard**.
4. Pass criteria:
   - The Parsek recordings table returns to the EXACT pre-switch committed
     recording count (no new segment recording remaining, but every previously
     committed recording still present).
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

## 10. Map Switch-To verb in dialog copy
1. Perform test #3 (Map Switch-To), fly for 20+ seconds.
2. Trigger scene exit.
3. Pass criteria:
   - The dialog body uses the verb "switch into" (e.g. "Keep your switch into
     'VesselName'?") — NOT "new flight on".

## 11. Second whole-pending dialog after scoped Discard
1. Set up a pending state with multiple changes: spawn a fresh vessel, fly it
   briefly to record, then without finalizing, Map Switch-To a committed
   vessel and fly the new segment.
2. Trigger scene exit.
3. In the FIRST scoped dialog, click **Discard**.
4. Pass criteria:
   - A SECOND dialog appears for the pre-existing pending changes, with
     **Merge to Timeline / Discard / Cancel** buttons.
   - Choosing **Cancel** at this point leaves the pre-existing pending state
     in place AND the segment-scoped Discard already took effect.

## 12. Per-source setting toggle disables that source only
1. In Settings → Recording, disable "Auto-record on Tracking Station Fly".
2. Leave "Auto-record on Map Switch-To" enabled.
3. From TS, Fly into a committed vessel.
4. Pass criteria:
   - NO segment is started (`[SwitchSegment]` arm log line absent).
   - `[SwitchIntentPatch] TS Fly intent not armed: setting-off` appears in
     the log.
   - The first-modification watcher takes over (existing fallback behavior).
5. Now from FLIGHT Map view, Switch-To another committed vessel.
6. Pass criteria:
   - Segment IS started (Map Switch-To setting was left on).

---

## Reporting issues found

If a test fails, run `python scripts/collect-logs.py switch-fly-test-N` to
snapshot logs/saves/test-results into `../logs/<timestamp>_switch-fly-test-N/`
and file an issue with:
- Which test number failed.
- Which step diverged from the expected outcome.
- The full grep of `[SwitchSegment]` and `[SwitchIntent]` from KSP.log.
