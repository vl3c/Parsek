using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// M2 harvest-provenance tests - the in-game layer of the harvest-capture
    /// work (plan `plan-logistics-m2-resource-generality.md` section 5).
    /// xUnit covers the pure window lifecycle (`RouteHarvestCaptureTests`),
    /// the gain-check math (`RouteHarvestAnalysisTests`), and the codec /
    /// hasher layers; these tests pin the parts only live KSP can exercise:
    /// the recorder's per-frame harvest poll over REAL
    /// <see cref="BaseConverter"/> modules (threshold-crossing window
    /// open/close on programmatic StartResourceConverter /
    /// StopResourceConverter), the D5 load-time catch-up attribution race,
    /// the D4 rails-entry/exit warp re-baseline, and the analysis verdict on
    /// the injected synthetic drill-run tree.
    ///
    /// <para><b>Re-entry discipline (mirrors
    /// <see cref="LogisticsOriginDebitRuntimeTests"/>).</b> Every capture
    /// test consumes <see cref="LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack"/>
    /// BEFORE any mutation. These tests arm NO orchestrator seams and store
    /// NO synthetic routes, so the background 1 Hz scenario tick has nothing
    /// of ours to re-enter across the recorded-frame yields; each assert
    /// block itself runs yield-free on the main thread. Everything mutated
    /// (converter activation, the recording + its tree, time warp) is
    /// restored in <c>finally</c>, and the isolated-batch baseline restore
    /// quickloads the pre-test save afterwards.</para>
    ///
    /// <para><b>ALL-TESTS-AUTO self-setup.</b> Flight auto-records, so an
    /// active recording / tree is the normal state of an ordinary session; the
    /// three capture tests used to skip on it and so never ran. They now
    /// self-discard that ephemeral session instead
    /// (<see cref="DiscardSessionRecordingForSelfSetup"/>). The discard runs
    /// only once each test's craft-capability check has passed (a converter
    /// present, or a drill for the D5 gate) and BEFORE the log observer is
    /// installed: a stock craft with no such part skips without touching the
    /// player's recording, and the live recorder's teardown never pollutes the
    /// captured log stream the assertions read. The discard is the one mutation
    /// NOT undone in <c>finally</c>; the isolated tier's post-test baseline
    /// quickload restores the pre-test world instead. That safety net is
    /// load-bearing, so the runner refuses to execute a restore-backed test
    /// when its baseline could not be captured (see
    /// <c>InGameTestRunner.RunSingle</c> /
    /// <c>PrepareBatchFlightRestoreExecution</c>) - the test skips rather than
    /// discarding with nothing to bring the recording back.</para>
    ///
    /// <para><b>These are NOT run-on-any-vessel gates.</b> The converter-toggle
    /// and warp tests need a <see cref="BaseConverter"/> on the active vessel (a
    /// stock fuel cell suffices); the D5 catch-up test needs an activated
    /// <see cref="BaseDrill"/> (a drill rig landed on ore); the analysis test
    /// needs the injected synthetic tree <c>tree-drill-harvest-m2</c>. On a
    /// plain stock craft they skip loudly with the missing prerequisite.</para>
    /// </summary>
    public sealed class LogisticsHarvestRuntimeTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// The pure in-memory tree-teardown surface on ParsekFlight (force-stop
        /// the recorder, drop the background recorder without persisting, null
        /// the active tree). Non-public; resolved once via the same reflection
        /// the RuntimeTests cleanups use. Deliberately NOT
        /// <c>AutoDiscardIdleActiveTree</c>, whose ledger recalc + "idle on pad"
        /// toast are wrong for both the ephemeral auto-record session (setup)
        /// and the test's own synthetic tree (teardown). Null only if the method
        /// is ever renamed - callers Skip (setup) or warn-log (teardown).
        /// </summary>
        private static readonly System.Reflection.MethodInfo DiscardSuppressedSceneExitMethod =
            typeof(ParsekFlight).GetMethod(
                "DiscardActiveTreeForSuppressedSceneExit",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        private const double ResourceTolerance = 0.01;
        private const float RecordingStartTimeoutSeconds = 5f;
        private const float LogWaitTimeoutSeconds = 10f;
        private const float RailsTransitionTimeoutSeconds = 10f;

        /// <summary>
        /// Tree id of the synthetic drill-run recording injected by
        /// <c>dotnet test --filter InjectAllRecordings</c>
        /// (SyntheticRecordingTests.DrillHarvestRouteTree). Keep in sync with
        /// the generator - the id cannot be shared across assemblies.
        /// </summary>
        private const string SyntheticDrillTreeId = "tree-drill-harvest-m2";

        private const string IsolatedOnlyBatchSkipReason =
            "Isolated-run only - starts/stops a live recording on the active vessel, toggles its " +
            "converters, and drives time warp under live KSP statics; excluded from ordinary " +
            "Run All / Run category. Use Run All + Isolated or the row play button in a " +
            "disposable FLIGHT session.";

        // ==================================================================
        // 1. Converter toggle opens and closes a harvest window
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "Programmatically toggling a BaseConverter (a stock fuel cell suffices; gains are zero but the window mechanics are pinned) during a live recording opens a harvest window on the activity threshold crossing (trigger=toggle) and closes it on the reverse crossing; the stopped recording carries the closed window with the at-start/at-stop flags both false")]
        public IEnumerator HarvestCapture_ConverterToggle_OpensAndClosesWindow()
        {
            // Post-restore unpack wait (yields BEFORE any mutation).
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            // PRECONDITIONS --------------------------------------------------
            ParsekFlight flight = RequireFlightWithUnpackedVessel(out Vessel vessel);
            List<BaseConverter> converters = FindConverters(vessel);
            if (converters.Count == 0)
                InGameAssert.Skip(
                    $"Active vessel '{vessel.vesselName}' carries no BaseConverter-derived module " +
                    "(harvester / converter / drill); a stock fuel cell suffices - add one to the test craft");

            bool[] originalActivation = SnapshotActivation(converters);
            var captured = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;

            try
            {
                // Self-discard the auto-record session now that the craft is
                // confirmed to carry a converter (the capability skip above is
                // non-destructive on a stock craft). Do it BEFORE installing the
                // observer so the live player recorder's teardown never pollutes
                // the captured stream the assertions read; finally restores it.
                DiscardSessionRecordingForSelfSetup(flight);
                ParsekLog.TestObserverForTesting = line => { captured.Add(line); priorObserver?.Invoke(line); };

                // Arrange: all converters idle so the recording start opens
                // no at-start window and the first transition is OUR toggle.
                DeactivateAll(converters);

                flight.StartRecording(suppressStartScreenMessage: true);
                yield return WaitUntil(() => flight.IsRecording,
                    RecordingStartTimeoutSeconds, "recording start");
                InGameAssert.IsTrue(flight.IsRecording,
                    $"StartRecording did not start within {RecordingStartTimeoutSeconds.ToString("R", IC)}s");
                InGameAssert.IsFalse(
                    captured.Exists(l => IsWindowOpenedLine(l) && l.Contains("trigger=recording-start")),
                    "No at-start window may open with every converter idle at recording start");

                // ACT 1 - threshold crossing: idle -> active.
                BaseConverter converter = converters[0];
                converter.StartResourceConverter();
                if (!converter.IsActivated)
                    InGameAssert.Skip(
                        $"Converter '{converter.ConverterName ?? converter.GetType().Name}' refused activation " +
                        "(e.g. a drill without ground contact); use a craft whose converter can activate - " +
                        "a stock fuel cell suffices");
                yield return WaitUntil(() => captured.Exists(IsWindowOpenedLine),
                    LogWaitTimeoutSeconds, "harvest window open on toggle");

                // ASSERT 1 (yield-free block).
                InGameAssert.IsTrue(
                    captured.Exists(l => IsWindowOpenedLine(l) && l.Contains("trigger=toggle") && l.Contains("atStart=0")),
                    "Expected 'Harvest window opened' with trigger=toggle atStart=0 after StartResourceConverter");

                // ACT 2 - reverse crossing: active -> idle.
                converter.StopResourceConverter();
                yield return WaitUntil(() => captured.Exists(IsWindowClosedLine),
                    LogWaitTimeoutSeconds, "harvest window close on toggle");

                // ASSERT 2 (yield-free block).
                InGameAssert.IsTrue(
                    captured.Exists(l => IsWindowClosedLine(l) && l.Contains("trigger=toggle") && l.Contains("atStop=0")),
                    "Expected 'Harvest window closed' with trigger=toggle atStop=0 after StopResourceConverter");

                // ACT 3 - stop: the recorder-side window list forwards onto
                // the tree recording (plan D3 rule 2 / D14).
                RecordingTree tree = flight.ActiveTreeForSerialization;
                InGameAssert.IsNotNull(tree, "Active tree should exist while recording");
                string recId = tree.ActiveRecordingId;
                flight.StopRecording();

                // ASSERT 3 (yield-free block).
                Recording stopped = null;
                InGameAssert.IsTrue(
                    !string.IsNullOrEmpty(recId) && tree.Recordings.TryGetValue(recId, out stopped) && stopped != null,
                    $"Stopped recording '{recId ?? "<null>"}' not found in the test tree");
                List<RouteHarvestWindow> windows = stopped.RouteHarvestWindows;
                InGameAssert.IsNotNull(windows,
                    "Stopped recording must carry the forwarded harvest window list");
                InGameAssert.AreEqual(1, windows.Count,
                    $"Exactly one window expected for one on/off toggle, got {windows.Count.ToString(IC)}");
                RouteHarvestWindow window = windows[0];
                InGameAssert.IsFalse(window.IsOpen, "The toggled-off window must be closed (EndUT set)");
                InGameAssert.IsFalse(window.OpenedAtRecordingStart,
                    "A mid-recording toggle window must not carry OpenedAtRecordingStart");
                InGameAssert.IsFalse(window.ClosedAtRecordingStop,
                    "A window closed by the toggle (not the stop) must not carry ClosedAtRecordingStop");
                InGameAssert.IsTrue(window.ActiveConverters != null && window.ActiveConverters.Count > 0,
                    "The window must record the open-time active converter ids");

                ParsekLog.Info("TestRunner",
                    $"HarvestCapture_ConverterToggle: PASS window={window.WindowId} " +
                    $"startUT={window.StartUT.ToString("F2", IC)} endUT={window.EndUT.ToString("F2", IC)} " +
                    $"converters=[{string.Join(",", window.ActiveConverters)}]");
            }
            finally
            {
                ParsekLog.TestObserverForTesting = priorObserver;
                CleanupRecordingAndTree("HarvestCapture_ConverterToggle");
                RestoreActivation(converters, originalActivation);
            }
        }

        // ==================================================================
        // 2. Load-time catch-up burst attribution (the D5 investigation)
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "D5 investigation: with a drill already active at recording start (after a provocation warp so the stock lastUpdateTime catch-up burst races the start snapshot), the window opens AT start and every positive full-run resource gain is covered by witnessed window deltas - the burst lands inside the open-at-start window or before the start baseline, never unaccounted. Requires a drill-equipped save (BaseDrill-derived module); skips otherwise")]
        public IEnumerator HarvestCapture_CatchUpOnLoad_AttributesInsideWindowOrBridges()
        {
            // Post-restore unpack wait (yields BEFORE any mutation).
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            // PRECONDITIONS --------------------------------------------------
            ParsekFlight flight = RequireFlightWithUnpackedVessel(out Vessel vessel);
            List<BaseConverter> converters = FindConverters(vessel);
            BaseConverter drill = converters.Find(c => c is BaseDrill);
            if (drill == null)
                InGameAssert.Skip(
                    $"Active vessel '{vessel.vesselName}' carries no BaseDrill-derived module " +
                    "(stock surface drills / asteroid+comet drills); the catch-up investigation needs " +
                    "real harvest production - load a drill-equipped save to run this test");

            bool[] originalActivation = SnapshotActivation(converters);
            var captured = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;
            int warpIndexBefore = TimeWarp.CurrentRateIndex;

            try
            {
                // Self-discard the auto-record session now that the vessel is
                // confirmed to carry a drill (the capability skip above is
                // non-destructive on a stock craft). Do it BEFORE the observer
                // AND before activating the drill, so the live player recorder's
                // teardown and the pre-record drill toggle never pollute the
                // captured stream; finally restores the world.
                DiscardSessionRecordingForSelfSetup(flight);
                ParsekLog.TestObserverForTesting = line => { captured.Add(line); priorObserver?.Invoke(line); };

                // 1. Activate the drill BEFORE recording (already-active-at-
                //    start premise, plan D5 part i).
                if (!drill.IsActivated)
                    drill.StartResourceConverter();
                yield return null;
                if (!drill.IsActivated)
                    InGameAssert.Skip(
                        $"Drill '{drill.ConverterName ?? drill.GetType().Name}' refused activation " +
                        "(no ground contact / no harvestable resource); land the drill rig on ore terrain " +
                        "to run this test");

                // 2. Provoke a stock catch-up burst: brief rails warp so the
                //    converter's lastUpdateTime lags, then exit - the unpack
                //    catch-up fires within the first frames, exactly the race
                //    the D5 bridging rule covers. Best-effort: a refused warp
                //    degrades the provocation, not the mechanics assertions.
                bool warped = false;
                if (TimeWarp.fetch != null)
                {
                    TimeWarp.SetRate(2, true);
                    yield return WaitUntil(() => vessel.packed,
                        RailsTransitionTimeoutSeconds, "rails entry (catch-up provocation)");
                    warped = vessel.packed;
                    if (warped)
                    {
                        float holdUntil = Time.realtimeSinceStartup + 2f;
                        while (Time.realtimeSinceStartup < holdUntil)
                            yield return null;
                    }
                    TimeWarp.SetRate(0, true);
                    yield return WaitUntil(() => vessel.loaded && !vessel.packed,
                        RailsTransitionTimeoutSeconds, "rails exit (catch-up provocation)");
                }
                if (!(vessel.loaded && !vessel.packed))
                    InGameAssert.Skip("Vessel never unpacked after the provocation warp; cannot race the start snapshot");
                ParsekLog.Verbose("TestRunner",
                    $"HarvestCapture_CatchUp: provocation warp {(warped ? "applied" : "refused (degraded run)")}");

                // 3. Record immediately: the start snapshot races the burst.
                flight.StartRecording(suppressStartScreenMessage: true);
                yield return WaitUntil(() => flight.IsRecording,
                    RecordingStartTimeoutSeconds, "recording start");
                InGameAssert.IsTrue(flight.IsRecording,
                    $"StartRecording did not start within {RecordingStartTimeoutSeconds.ToString("R", IC)}s");
                yield return WaitUntil(() => captured.Exists(IsWindowOpenedLine),
                    RecordingStartTimeoutSeconds, "at-start harvest window");
                InGameAssert.IsTrue(
                    captured.Exists(l => IsWindowOpenedLine(l) && l.Contains("atStart=1") && l.Contains("trigger=recording-start")),
                    "An already-active drill must open a harvest window AT recording start (plan D5 part i)");

                // 4. Let a few seconds of live production land inside the window.
                float produceUntil = Time.realtimeSinceStartup + 3f;
                while (Time.realtimeSinceStartup < produceUntil)
                    yield return null;

                // 5. Stop and read the forwarded capture off the tree recording.
                RecordingTree tree = flight.ActiveTreeForSerialization;
                InGameAssert.IsNotNull(tree, "Active tree should exist while recording");
                string recId = tree.ActiveRecordingId;
                flight.StopRecording();

                Recording stopped = null;
                InGameAssert.IsTrue(
                    !string.IsNullOrEmpty(recId) && tree.Recordings.TryGetValue(recId, out stopped) && stopped != null,
                    $"Stopped recording '{recId ?? "<null>"}' not found in the test tree");
                RouteRunCargoManifest manifest = stopped.RouteRunManifest;
                InGameAssert.IsNotNull(manifest, "Stopped recording must carry a run cargo manifest");
                InGameAssert.IsTrue(manifest.IsComplete,
                    "An active stop must complete the run manifest (both halves)");
                List<RouteHarvestWindow> windows = stopped.RouteHarvestWindows;
                InGameAssert.IsTrue(windows != null && windows.Count >= 1,
                    "Stopped recording must carry at least the open-at-start harvest window");
                InGameAssert.IsTrue(windows[0].OpenedAtRecordingStart,
                    "The first window must carry OpenedAtRecordingStart");
                InGameAssert.IsTrue(windows[windows.Count - 1].ClosedAtRecordingStop,
                    "The still-open window must be closed by the stop (ClosedAtRecordingStop)");

                // 6. THE D5 ASSERTION: every positive full-run gain is covered
                //    by witnessed window deltas - whichever side of the start
                //    snapshot the catch-up burst landed on, it is never an
                //    unaccounted gain.
                Dictionary<string, double> gains = ResourceManifest.ComputeResourceDelta(
                    manifest.StartTransportResources, manifest.EndTransportResources);
                int gainedResources = 0;
                if (gains != null)
                {
                    foreach (KeyValuePair<string, double> kvp in gains)
                    {
                        if (kvp.Value <= ResourceTolerance) continue;
                        gainedResources++;
                        double harvested = SumWitnessedHarvest(windows, kvp.Key);
                        InGameAssert.IsTrue(kvp.Value <= harvested + ResourceTolerance,
                            $"Untracked gain: {kvp.Key} gained {kvp.Value.ToString("R", IC)} but only " +
                            $"{harvested.ToString("R", IC)} was witnessed in harvest windows - the catch-up " +
                            "burst escaped the open-at-start window (plan D5 trap)");
                    }
                }

                ParsekLog.Info("TestRunner",
                    $"HarvestCapture_CatchUp: PASS warped={warped} windows={windows.Count.ToString(IC)} " +
                    $"gainedResources={gainedResources.ToString(IC)} (all covered within " +
                    $"{ResourceTolerance.ToString("R", IC)})");
            }
            finally
            {
                ParsekLog.TestObserverForTesting = priorObserver;
                TryRestoreWarpIndex(warpIndexBefore, "HarvestCapture_CatchUp");
                CleanupRecordingAndTree("HarvestCapture_CatchUp");
                RestoreActivation(converters, originalActivation);
            }
        }

        // ==================================================================
        // 3. Warp toggle re-baselines at the rails transitions (D4 warp rule)
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "D4 warp rule (plan round-2 correction 7): a window open at rails entry stays open across the warp (verbose breadcrumb), a converter toggled OFF during the warp closes its window on the FIRST post-rails poll with trigger=rails-exit (never while packed), so warp-period production stays attributed inside a witnessed window")]
        public IEnumerator HarvestCapture_WarpToggle_RebaselinesAtRailsTransitions()
        {
            // Post-restore unpack wait (yields BEFORE any mutation).
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            // PRECONDITIONS --------------------------------------------------
            ParsekFlight flight = RequireFlightWithUnpackedVessel(out Vessel vessel);
            if (TimeWarp.fetch == null)
                InGameAssert.Skip("TimeWarp.fetch is null; cannot drive rails transitions");
            List<BaseConverter> converters = FindConverters(vessel);
            if (converters.Count == 0)
                InGameAssert.Skip(
                    $"Active vessel '{vessel.vesselName}' carries no BaseConverter-derived module; " +
                    "a stock fuel cell suffices - add one to the test craft");

            bool[] originalActivation = SnapshotActivation(converters);
            var captured = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;
            var priorVerbose = ParsekLog.VerboseOverrideForTesting;
            int warpIndexBefore = TimeWarp.CurrentRateIndex;

            try
            {
                // Self-discard the auto-record session now that the craft is
                // confirmed to carry a converter (the capability skips above are
                // non-destructive on a stock craft). Do it BEFORE the observer
                // so the live player recorder's teardown never pollutes the
                // captured stream; finally restores the world.
                DiscardSessionRecordingForSelfSetup(flight);

                // The rails-entry stays-open breadcrumb is VerboseRateLimited.
                ParsekLog.VerboseOverrideForTesting = true;
                ParsekLog.TestObserverForTesting = line => { captured.Add(line); priorObserver?.Invoke(line); };

                DeactivateAll(converters);

                flight.StartRecording(suppressStartScreenMessage: true);
                yield return WaitUntil(() => flight.IsRecording,
                    RecordingStartTimeoutSeconds, "recording start");
                InGameAssert.IsTrue(flight.IsRecording,
                    $"StartRecording did not start within {RecordingStartTimeoutSeconds.ToString("R", IC)}s");

                // Open a window via the toggle crossing.
                BaseConverter converter = converters[0];
                converter.StartResourceConverter();
                if (!converter.IsActivated)
                    InGameAssert.Skip(
                        $"Converter '{converter.ConverterName ?? converter.GetType().Name}' refused activation; " +
                        "use a craft whose converter can activate - a stock fuel cell suffices");
                yield return WaitUntil(() => captured.Exists(IsWindowOpenedLine),
                    LogWaitTimeoutSeconds, "harvest window open before warp");
                InGameAssert.IsTrue(captured.Exists(l => IsWindowOpenedLine(l) && l.Contains("trigger=toggle")),
                    "Expected the pre-warp toggle to open a harvest window (trigger=toggle)");

                // RAILS ENTRY with the window open.
                int packedFromIndex = captured.Count;
                TimeWarp.SetRate(2, true);
                yield return WaitUntil(() => vessel.packed,
                    RailsTransitionTimeoutSeconds, "rails entry");
                if (!vessel.packed)
                    InGameAssert.Skip(
                        $"Rails warp was refused in this situation ({vessel.situation}); " +
                        "run from a landed or stable-orbit vessel to exercise the rails re-baseline");
                yield return WaitUntil(
                    () => captured.Exists(l => l.Contains("Harvest window stays open across rails entry")),
                    RecordingStartTimeoutSeconds, "rails-entry stays-open breadcrumb");
                InGameAssert.IsTrue(
                    captured.Exists(l => l.Contains("Harvest window stays open across rails entry")),
                    "Rails entry with an open window must keep it open and log the breadcrumb (plan D4 warp rule)");

                // Toggle OFF while ON RAILS - the poll is rails-gated, so the
                // close must wait for the exit boundary. Whether
                // StopResourceConverter flips IsActivated while packed is
                // STOCK behavior (BaseConverter lifecycle), not Parsek code
                // under test - if this craft/situation refuses the packed
                // deactivation, skip with the named reason instead of failing.
                converter.StopResourceConverter();
                if (converter.IsActivated)
                    InGameAssert.Skip(
                        $"Converter '{converter.ConverterName ?? converter.GetType().Name}' did not " +
                        "deactivate while packed (stock BaseConverter behavior dependency); " +
                        "cannot exercise the warp-toggle close path in this situation");
                int beforeExitCount = captured.Count;

                // RAILS EXIT.
                TimeWarp.SetRate(0, true);
                yield return WaitUntil(() => vessel.loaded && !vessel.packed,
                    RailsTransitionTimeoutSeconds, "rails exit");
                InGameAssert.IsTrue(vessel.loaded && !vessel.packed,
                    "Vessel must unpack after TimeWarp.SetRate(0)");
                yield return WaitUntil(() => captured.Exists(IsWindowClosedLine),
                    LogWaitTimeoutSeconds, "harvest window close after rails exit");

                // ASSERT (yield-free block): closed at the exit boundary,
                // never while packed.
                bool closedWhilePacked = false;
                for (int i = packedFromIndex; i < beforeExitCount && i < captured.Count; i++)
                    if (IsWindowClosedLine(captured[i]))
                        closedWhilePacked = true;
                InGameAssert.IsFalse(closedWhilePacked,
                    "No window close may fire while on rails - the harvest poll is rails-gated");
                InGameAssert.IsTrue(
                    captured.Exists(l => IsWindowClosedLine(l) && l.Contains("trigger=rails-exit")),
                    "A converter toggled OFF during warp must close its window on the first post-rails poll " +
                    "with trigger=rails-exit (plan D4 warp rule / round-2 correction 7)");

                // Stop + verify the forwarded window shape.
                RecordingTree tree = flight.ActiveTreeForSerialization;
                InGameAssert.IsNotNull(tree, "Active tree should exist while recording");
                string recId = tree.ActiveRecordingId;
                flight.StopRecording();

                Recording stopped = null;
                InGameAssert.IsTrue(
                    !string.IsNullOrEmpty(recId) && tree.Recordings.TryGetValue(recId, out stopped) && stopped != null,
                    $"Stopped recording '{recId ?? "<null>"}' not found in the test tree");
                List<RouteHarvestWindow> windows = stopped.RouteHarvestWindows;
                InGameAssert.IsTrue(windows != null && windows.Count == 1,
                    $"Exactly one window expected (opened pre-warp, closed at rails exit), got " +
                    $"{(windows == null ? "null" : windows.Count.ToString(IC))}");
                InGameAssert.IsFalse(windows[0].IsOpen, "The rails-exit-closed window must carry an EndUT");
                InGameAssert.IsFalse(windows[0].ClosedAtRecordingStop,
                    "The window was closed by the rails-exit poll, not the stop");

                ParsekLog.Info("TestRunner",
                    $"HarvestCapture_WarpToggle: PASS window={windows[0].WindowId} " +
                    $"startUT={windows[0].StartUT.ToString("F2", IC)} endUT={windows[0].EndUT.ToString("F2", IC)}");
            }
            finally
            {
                ParsekLog.TestObserverForTesting = priorObserver;
                ParsekLog.VerboseOverrideForTesting = priorVerbose;
                TryRestoreWarpIndex(warpIndexBefore, "HarvestCapture_WarpToggle");
                CleanupRecordingAndTree("HarvestCapture_WarpToggle");
                RestoreActivation(converters, originalActivation);
            }
        }

        // ==================================================================
        // 4. Synthetic drill-run tree analyzes Eligible (no live converters)
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "The injected synthetic drill-run tree (InjectAllRecordings: run manifests + two Minmus harvest windows + one ore delivery window) analyzes Eligible as a harvest-origin route: 120 Ore witnessed across both windows fully covers the run gain, the delivery manifest carries 100 Ore, and the first window's open location feeds the harvest-origin endpoint. Read-only; no live converters needed")]
        public void HarvestRoute_AnalyzesEligible_FromSyntheticRecording()
        {
            // PRECONDITIONS --------------------------------------------------
            RecordingTree tree = null;
            var trees = RecordingStore.CommittedTrees;
            if (trees != null)
            {
                for (int i = 0; i < trees.Count; i++)
                {
                    if (trees[i] != null && string.Equals(trees[i].Id, SyntheticDrillTreeId, StringComparison.Ordinal))
                    {
                        tree = trees[i];
                        break;
                    }
                }
            }
            if (tree == null)
                InGameAssert.Skip(
                    $"Synthetic drill tree '{SyntheticDrillTreeId}' is not in this save; run " +
                    "'dotnet test --filter InjectAllRecordings' (KSP closed) and load the injected save " +
                    "to run this test");

            // ACT - the production analysis entry point, read-only.
            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            // ASSERT ---------------------------------------------------------
            InGameAssert.IsNotNull(result, "AnalyzeTree returned null");
            InGameAssert.IsTrue(result.IsEligible,
                $"Synthetic drill run must analyze Eligible, got {result.Status} " +
                $"(detail={result.RejectDetail ?? "<none>"})");
            InGameAssert.IsTrue(result.IsHarvestOrigin,
                "An undocked-start fully-harvest-covered run must classify as a harvest origin (plan D7)");
            InGameAssert.IsNotNull(result.HarvestedManifest, "Eligible harvest run must carry a harvested manifest");
            InGameAssert.IsTrue(result.HarvestedManifest.ContainsKey("Ore"),
                "Harvested manifest must contain Ore");
            InGameAssert.ApproxEqual(120.0, result.HarvestedManifest["Ore"], ResourceTolerance,
                "Harvested Ore must sum both windows (80 + 40 = 120)");
            InGameAssert.IsNotNull(result.ResourceDeliveryManifest, "Eligible run must carry a delivery manifest");
            InGameAssert.IsTrue(result.ResourceDeliveryManifest.ContainsKey("Ore"),
                "Delivery manifest must contain Ore");
            InGameAssert.ApproxEqual(100.0, result.ResourceDeliveryManifest["Ore"], ResourceTolerance,
                "Delivered Ore must be min(endpoint gain, transport loss) = 100");
            InGameAssert.IsNotNull(result.FirstHarvestWindow,
                "Harvest-origin result must surface the FIRST harvest window for the origin endpoint");
            InGameAssert.AreEqual("Minmus", result.FirstHarvestWindow.BodyName,
                "The harvest-origin endpoint location comes from the first window's open location");

            ParsekLog.Info("TestRunner",
                $"HarvestRoute_AnalyzesEligible: PASS tree={SyntheticDrillTreeId} status={result.Status} " +
                $"harvestedOre={result.HarvestedManifest["Ore"].ToString("R", IC)} " +
                $"deliveredOre={result.ResourceDeliveryManifest["Ore"].ToString("R", IC)} " +
                $"originBody={result.FirstHarvestWindow.BodyName}");
        }

        // ==================================================================
        // Shared helpers
        // ==================================================================

        /// <summary>
        /// Common capture-test preconditions: a live ParsekFlight and a loaded
        /// + unpacked active vessel. Skips with a named reason when either is
        /// missing. Does NOT touch the live recording / tree - the caller does
        /// the self-discard (<see cref="DiscardSessionRecordingForSelfSetup"/>)
        /// only once it has confirmed the test can actually run, so a skip here
        /// or at a craft-capability check never destroys the player's recording.
        /// </summary>
        private static ParsekFlight RequireFlightWithUnpackedVessel(out Vessel vessel)
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (flight == null)
                InGameAssert.Skip("ParsekFlight.Instance is null; FLIGHT scene controller required");
            vessel = FlightGlobals.ActiveVessel;
            if (vessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live vessel to record");
            if (!(vessel.loaded && !vessel.packed))
                InGameAssert.Skip(
                    $"Active vessel '{vessel.vesselName}' is not loaded+unpacked " +
                    $"(loaded={vessel.loaded}, packed={vessel.packed}); the harvest poll only runs off-rails");
            return flight;
        }

        /// <summary>
        /// ALL-TESTS-AUTO self-setup: flight auto-records, so an active session
        /// recording / tree is the NORMAL state of any ordinary session, not an
        /// operator error. The tests used to skip on it and so never ran; each
        /// test now calls this as the first statement of its recording body -
        /// after the craft-capability check that would skip a stock craft, and
        /// before the log observer - so the discard fires only when the test
        /// will genuinely record and never pollutes the captured stream.
        ///
        /// <para>Discard goes through <see cref="StopAndDiscardActiveSession"/>
        /// (the pure in-memory teardown), NOT <c>AutoDiscardIdleActiveTree</c>
        /// (which re-homes irreversible live gameplay onto the ledger, runs a
        /// recalc, and toasts the player). This is the one mutation NOT undone
        /// in <c>finally</c>; the isolated tier's post-test baseline quickload
        /// restores the pre-test world - and the runner refuses to run a
        /// restore-backed test whose baseline could not be captured, so the
        /// discard never happens with nothing to restore.</para>
        /// </summary>
        private static void DiscardSessionRecordingForSelfSetup(ParsekFlight flight)
        {
            bool wasRecording = flight.IsRecording;
            RecordingTree sessionTree = flight.ActiveTreeForSerialization;
            if (!wasRecording && sessionTree == null)
                return;

            string sessionTreeId = sessionTree?.Id ?? "<none>";
            try
            {
                StopAndDiscardActiveSession(flight,
                    "LogisticsHarvest gate setup: discard the ephemeral auto-record session tree");
            }
            catch (InvalidOperationException ex)
            {
                InGameAssert.Skip(ex.Message);
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                InGameAssert.Fail(
                    "setup: DiscardActiveTreeForSuppressedSceneExit threw " +
                    $"{ex.InnerException?.GetType().Name ?? ex.GetType().Name}: " +
                    $"{ex.InnerException?.Message ?? ex.Message}");
            }

            ParsekLog.Info("TestRunner",
                "LogisticsHarvest setup: stopped/discarded the active auto-record session so the gate can run " +
                $"(wasRecording={wasRecording} tree='{sessionTreeId}')");
            InGameAssert.IsFalse(flight.IsRecording,
                "setup: the session recording must be stopped before the gate starts its own");
            InGameAssert.IsTrue(flight.ActiveTreeForSerialization == null,
                "setup: the session tree must be discarded before the gate creates its own");
        }

        /// <summary>
        /// Stop the recorder and discard the current active tree through the
        /// pure in-memory teardown <c>ParsekFlight.DiscardActiveTreeForSuppressedSceneExit</c>
        /// (the reflection surface <c>RuntimeTests.DiscardActiveTreeForRuntimeTest</c>
        /// also uses). Shared by setup (the auto-record session) and teardown
        /// (the test's own tree). The suppressed path dereferences
        /// <c>activeTree</c> unconditionally, so a bare recorder with no tree is
        /// handled by a plain <c>StopRecording</c>. Throws
        /// <see cref="InvalidOperationException"/> if the reflection surface is
        /// missing, or <see cref="System.Reflection.TargetInvocationException"/>
        /// if the underlying discard throws; callers translate that to a Skip /
        /// Fail (setup) or a warn-log (teardown).
        /// </summary>
        private static void StopAndDiscardActiveSession(ParsekFlight flight, string reason)
        {
            if (flight == null)
                return;
            if (flight.ActiveTreeForSerialization == null)
            {
                if (flight.IsRecording)
                    flight.StopRecording();
                return;
            }
            if (DiscardSuppressedSceneExitMethod == null)
                throw new InvalidOperationException(
                    "ParsekFlight.DiscardActiveTreeForSuppressedSceneExit reflection surface unavailable");
            DiscardSuppressedSceneExitMethod.Invoke(flight, new object[]
            {
                HighLogic.LoadedScene,
                Planetarium.GetUniversalTime(),
                reason,
                false
            });
        }

        private static List<BaseConverter> FindConverters(Vessel vessel)
        {
            var converters = new List<BaseConverter>();
            if (vessel == null || vessel.parts == null)
                return converters;
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p == null || p.Modules == null) continue;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    if (p.Modules[m] is BaseConverter converter)
                        converters.Add(converter);
                }
            }
            return converters;
        }

        private static bool[] SnapshotActivation(List<BaseConverter> converters)
        {
            var snapshot = new bool[converters.Count];
            for (int i = 0; i < converters.Count; i++)
                snapshot[i] = converters[i] != null && converters[i].IsActivated;
            return snapshot;
        }

        private static void DeactivateAll(List<BaseConverter> converters)
        {
            int stopped = 0;
            for (int i = 0; i < converters.Count; i++)
            {
                BaseConverter c = converters[i];
                if (c == null || !c.IsActivated) continue;
                c.StopResourceConverter();
                stopped++;
            }
            if (stopped > 0)
                ParsekLog.Verbose("TestRunner",
                    $"LogisticsHarvest arrange: deactivated {stopped.ToString(IC)} converter(s) pre-recording");
        }

        private static void RestoreActivation(List<BaseConverter> converters, bool[] original)
        {
            if (converters == null || original == null) return;
            for (int i = 0; i < converters.Count && i < original.Length; i++)
            {
                BaseConverter c = converters[i];
                if (c == null) continue;
                try
                {
                    if (original[i] && !c.IsActivated)
                        c.StartResourceConverter();
                    else if (!original[i] && c.IsActivated)
                        c.StopResourceConverter();
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("TestRunner",
                        $"LogisticsHarvest cleanup: failed to restore converter activation " +
                        $"({ex.GetType().Name}: {ex.Message})");
                }
            }
            ParsekLog.Verbose("TestRunner", "LogisticsHarvest cleanup: converter activation states restored");
        }

        /// <summary>
        /// Best-effort teardown of the recording + tree the test created: stop
        /// the recorder if still live, then discard the active tree. Uses the
        /// same pure <see cref="StopAndDiscardActiveSession"/> path as setup -
        /// NOT <c>AutoDiscardIdleActiveTree</c>, whose ledger recalc + "idle on
        /// pad" toast are wrong for the test's synthetic tree (and would be
        /// reverted by the baseline quickload anyway). A pre-existing session
        /// tree, if any, was already discarded at setup, so the only tree
        /// standing here is the test's own.
        /// </summary>
        private static void CleanupRecordingAndTree(string label)
        {
            try
            {
                ParsekFlight flight = ParsekFlight.Instance;
                if (flight == null) return;
                StopAndDiscardActiveSession(flight, $"{label} test cleanup");
                ParsekLog.Verbose("TestRunner", $"{label} cleanup: recording stopped + test tree discarded");
            }
            catch (Exception ex)
            {
                Exception inner = (ex as System.Reflection.TargetInvocationException)?.InnerException ?? ex;
                ParsekLog.Warn("TestRunner",
                    $"{label} cleanup: recording/tree teardown failed ({inner.GetType().Name}: {inner.Message})");
            }
        }

        private static void TryRestoreWarpIndex(int warpIndexBefore, string label)
        {
            try
            {
                if (TimeWarp.fetch != null && TimeWarp.CurrentRateIndex != warpIndexBefore)
                {
                    TimeWarp.SetRate(warpIndexBefore, true);
                    ParsekLog.Verbose("TestRunner",
                        $"{label} cleanup: restored time warp index {warpIndexBefore.ToString(IC)}");
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("TestRunner",
                    $"{label} cleanup: failed to restore time warp ({ex.GetType().Name}: {ex.Message})");
            }
        }

        private static double SumWitnessedHarvest(List<RouteHarvestWindow> windows, string resourceName)
        {
            double total = 0.0;
            if (windows == null) return total;
            for (int i = 0; i < windows.Count; i++)
            {
                Dictionary<string, double> harvested =
                    RouteHarvestCapture.ComputeWindowHarvestedManifest(windows[i]);
                if (harvested != null && harvested.TryGetValue(resourceName, out double amount))
                    total += amount;
            }
            return total;
        }

        private static bool IsWindowOpenedLine(string line)
        {
            return line.Contains("[Recorder]") && line.Contains("Harvest window opened");
        }

        private static bool IsWindowClosedLine(string line)
        {
            return line.Contains("[Recorder]") && line.Contains("Harvest window closed");
        }

        /// <summary>
        /// Yields until <paramref name="condition"/> is true or the bounded
        /// wait times out (the caller's next assert reports the failure with
        /// its own message; the timeout itself is breadcrumbed).
        /// </summary>
        private static IEnumerator WaitUntil(Func<bool> condition, float timeoutSeconds, string what)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (condition())
                    yield break;
                yield return null;
            }
            ParsekLog.Verbose("TestRunner",
                $"LogisticsHarvest wait timed out after {timeoutSeconds.ToString("R", IC)}s: {what}");
        }
    }
}
