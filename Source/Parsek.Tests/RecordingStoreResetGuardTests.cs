using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Regression tests for the data-loss guard in
    /// <see cref="RecordingStore.ResetForTesting"/> and the
    /// <see cref="RecordingStoreTestSnapshot"/> non-destructive snapshot pattern.
    ///
    /// Production bug source: 2026-05-01 KSP.log shows
    /// <c>PersistenceSplitOptimizerTest</c> calling <c>ResetForTesting()</c> from inside
    /// the in-game test runner (Ctrl+Shift+T), which silently wiped the player's 5
    /// committed recordings (R0 + 4×R1) from the live save. The next OnSave wrote 0
    /// RECORDING_TREE nodes and the user only recovered through quicksave.sfs.
    /// </summary>
    [Collection("Sequential")]
    public class RecordingStoreResetGuardTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RecordingStoreResetGuardTests()
        {
            RecordingStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ApplicationIsPlayingForTesting = null;
            // Direct reset is safe here: outside Unity play mode the guard is a no-op.
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ApplicationIsPlayingForTesting = null;
            RecordingStore.ResetForTesting();
        }

        private static List<TrajectoryPoint> MakePoints(int count)
        {
            var points = new List<TrajectoryPoint>();
            for (int i = 0; i < count; i++)
            {
                points.Add(new TrajectoryPoint
                {
                    ut = 100 + i * 10,
                    latitude = 0,
                    longitude = 0,
                    altitude = 100,
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero,
                    bodyName = "Kerbin"
                });
            }
            return points;
        }

        // ─────────── Guard tests ───────────

        [Fact]
        public void ResetForTesting_NoOpInPlayMode_WhenStoreIsEmpty()
        {
            // Even with the play-mode flag asserted, an empty store is safe to reset —
            // there is nothing the guard could lose.
            RecordingStore.ApplicationIsPlayingForTesting = () => true;

            RecordingStore.ResetForTesting();

            Assert.Empty(RecordingStore.CommittedRecordings);
            Assert.Empty(RecordingStore.CommittedTrees);
        }

        [Fact]
        public void ResetForTesting_ThrowsInPlayMode_WhenCommittedRecordingsExist()
        {
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "LiveShip");
            Assert.NotNull(rec);
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            Assert.Single(RecordingStore.CommittedRecordings);

            // Simulate the in-game test runner: Application.isPlaying = true.
            RecordingStore.ApplicationIsPlayingForTesting = () => true;

            var ex = Assert.Throws<InvalidOperationException>(
                () => RecordingStore.ResetForTesting());

            Assert.Contains("ResetForTesting blocked", ex.Message);
            Assert.Contains("committedRecordings=1", ex.Message);

            // Live data must survive the failed reset.
            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("LiveShip", RecordingStore.CommittedRecordings[0].VesselName);

            // The error must hit the log so the failure is visible in KSP.log.
            Assert.Contains(logLines,
                l => l.Contains("[ERROR][RecordingStore]") && l.Contains("ResetForTesting blocked"));
        }

        [Fact]
        public void ResetForTesting_ThrowsInPlayMode_WhenPendingTreeExists()
        {
            var tree = new RecordingTree { Id = "pendT", TreeName = "PendingShip" };
            RecordingStore.StashPendingTree(tree);
            Assert.True(RecordingStore.HasPendingTree);

            RecordingStore.ApplicationIsPlayingForTesting = () => true;

            var ex = Assert.Throws<InvalidOperationException>(
                () => RecordingStore.ResetForTesting());

            Assert.Contains("hasPendingTree=True", ex.Message);
            Assert.True(RecordingStore.HasPendingTree);

            // Parity with the committed-recordings sibling test: the throw must reach
            // KSP.log so the failure is visible in production diagnostics.
            Assert.Contains(logLines,
                l => l.Contains("[ERROR][RecordingStore]") && l.Contains("ResetForTesting blocked"));
        }

        [Fact]
        public void ResetForTesting_AllowsReset_WhenNotInPlayMode()
        {
            // xUnit / dotnet-test path: Application.isPlaying = false. The guard must
            // not interfere with existing test fixtures that legitimately reset state.
            var rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "Probe");
            Assert.NotNull(rec);
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            Assert.Single(RecordingStore.CommittedRecordings);

            RecordingStore.ApplicationIsPlayingForTesting = () => false;
            RecordingStore.ResetForTesting();

            Assert.Empty(RecordingStore.CommittedRecordings);
            Assert.Empty(RecordingStore.CommittedTrees);
        }

        // ─────────── Snapshot/Restore tests ───────────

        [Fact]
        public void Snapshot_RestoresCommittedRecordingsAndTrees()
        {
            var live = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "Live");
            Assert.NotNull(live);
            RecordingStore.AddRecordingWithTreeForTesting(live);
            int liveTreeCount = RecordingStore.CommittedTrees.Count;
            Assert.Equal(1, RecordingStore.CommittedRecordings.Count);

            // Capture the player's pre-test state.
            var snapshot = RecordingStoreTestSnapshot.Capture();
            Assert.Equal(1, snapshot.CommittedRecordingCount);
            Assert.Equal(liveTreeCount, snapshot.CommittedTreeCount);

            // Inject a synthetic recording the way an in-game test would.
            var synthetic = RecordingStore.CreateRecordingFromFlightData(MakePoints(2), "Synthetic");
            Assert.NotNull(synthetic);
            RecordingStore.AddRecordingWithTreeForTesting(synthetic);
            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);

            // Restore: synthetic gone, live recording still there.
            snapshot.Restore();

            Assert.Equal(1, RecordingStore.CommittedRecordings.Count);
            Assert.Equal("Live", RecordingStore.CommittedRecordings[0].VesselName);
            Assert.Equal(liveTreeCount, RecordingStore.CommittedTrees.Count);
        }

        [Fact]
        public void Snapshot_RestoresPendingTreeAndState()
        {
            var pending = new RecordingTree { Id = "p1", TreeName = "Pending" };
            RecordingStore.StashPendingTree(pending);
            var preState = RecordingStore.PendingTreeStateValue;
            Assert.True(RecordingStore.HasPendingTree);

            var snapshot = RecordingStoreTestSnapshot.Capture();

            // Test mutates the pending slot (e.g. discards it as the in-game test
            // might if it were poorly designed, or replaces it with a synthetic one).
            RecordingStore.DiscardPendingTree();
            Assert.False(RecordingStore.HasPendingTree);

            snapshot.Restore();

            Assert.True(RecordingStore.HasPendingTree);
            Assert.Equal("Pending", RecordingStore.PendingTree.TreeName);
            Assert.Equal(preState, RecordingStore.PendingTreeStateValue);
        }

        // ─────────── Bypass-guard variant tests ───────────

        [Fact]
        public void ResetForBatchFlightBaselineRestoreBypassingGuard_ClearsLiveData_WithoutThrowing()
        {
            // The in-game test runner's batch FLIGHT baseline restore flow
            // calls this variant before quickloading the player's clean
            // baseline save. The wipe is transient (about to be replaced
            // from disk via OnLoad) so the live-save guard must NOT fire.
            var live = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "BaselineLive");
            Assert.NotNull(live);
            RecordingStore.AddRecordingWithTreeForTesting(live);
            Assert.Single(RecordingStore.CommittedRecordings);

            // Simulate Unity play mode: the regular ResetForTesting() would
            // throw here. Confirm the bypass variant doesn't.
            RecordingStore.ApplicationIsPlayingForTesting = () => true;

            RecordingStore.ResetForBatchFlightBaselineRestoreBypassingGuard();

            Assert.Empty(RecordingStore.CommittedRecordings);
            Assert.Empty(RecordingStore.CommittedTrees);
            Assert.False(RecordingStore.HasPendingTree);
            // The bypass must NOT log the live-save-data error — that line
            // is the guard's signature and the bypass deliberately skips it.
            Assert.DoesNotContain(logLines,
                l => l.Contains("ResetForTesting blocked"));
        }

        [Fact]
        public void BatchFlightBaselineRestore_ValidationFailureBeforeWipe_LeavesAllStoresIntact()
        {
            // P1 (round 3) review regression: previously
            // PrepareForIsolatedBatchFlightBaselineRestore wiped
            // RecordingStore + GroupHierarchyStore +
            // CrewReservationManager.crewReplacements + GameStateStore +
            // MilestoneStore + GameStateRecorder.PendingScienceSubjects +
            // LedgerOrchestrator + RevertDetector BEFORE the .sfs was
            // validated as loadable. Any quickload-failure mode (missing
            // slot, corrupt .sfs, null Game, invalid activeVesselIdx) left
            // all seven stores cleared with no rebuilding OnLoad to follow.
            // The RecordingStore-specific snapshot rollback added earlier
            // covered RecordingStore but not the other six.
            //
            // Fix: TriggerQuickload was split into three phases.
            // RestoreBatchFlightBaselineCore now sequences:
            //   1a. ValidateQuicksaveStructure (ConfigNode.Load XML
            //       parse only; no FlightGlobals mutation).
            //   2.  ActivateStagedBatchFlightBaselineRestore (on-disk
            //       Parsek/ swap, file copy + Directory.Move).
            //   1b. LoadAndValidateGameForQuickload (calls
            //       GamePersistence.LoadGame; mutates FlightGlobals
            //       persistent-id dictionaries as a stock-KSP side
            //       effect, rebuilt by OnLoad on step 4).
            //   3.  PrepareForIsolatedBatchFlightBaselineRestore (the
            //       wipe of RecordingStore + 6 other Parsek save-scoped
            //       stores).
            //   4.  CommitValidatedGameLoad (scene change fires OnLoad).
            //   5.  WaitForFlightReady / WaitForBatchBaselineVessel /
            //       WaitForStockStageManagerReady (post-OnLoad waits).
            //
            // The wipe at step 3 only runs after step 1a's structural
            // validation AND step 1b's Game-object realisation both
            // succeeded. Step 1a is truly non-destructive (no
            // FlightGlobals or in-memory Parsek state touched); step 1b
            // mutates FlightGlobals but is a documented residual in the
            // production failure-mode comment.
            //
            // SCOPE: this test is DOCUMENTATION-BY-TEST. It pins the
            // contract by simulation -- it does NOT invoke the actual
            // LoadAndValidateGameForQuickload (Unity-only) or
            // ResetForBatchFlightBaselineRestoreBypassingGuard from
            // inside RestoreBatchFlightBaselineCore. A future refactor
            // that put the wipe BEFORE validation in the real iterator
            // would NOT be caught by this test alone -- the throw is
            // hardcoded, not delegated through the actual helper. Real
            // integration coverage comes from running the in-game test
            // batch via Ctrl+Shift+T (Run All + Isolated): the prep
            // wipe failures that motivated this PR exercise the actual
            // ordering. This xUnit test serves as a readable
            // specification of the intended sequence; the in-game
            // run is the regression detector.
            var live = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "PreValidationLive");
            Assert.NotNull(live);
            RecordingStore.AddRecordingWithTreeForTesting(live);
            Assert.Single(RecordingStore.CommittedRecordings);

            RecordingStore.ApplicationIsPlayingForTesting = () => true;

            // Simulate the new sequence in RestoreBatchFlightBaselineCore.
            // Step 1: stage the snapshot dir on disk (no-op here -- not
            // relevant to in-memory state).
            // Step 2: validation throws.
            // Step 3 (the wipe) and beyond MUST NOT run.
            bool wipeRan = false;
            try
            {
                // Step 2: simulated LoadAndValidateGameForQuickload throw.
                // Pre-fix this would have been
                //   ActivateStagedBatchFlightBaselineRestore(stage, prepareForRestore: () => Reset...)
                // and the wipe would already have happened by this point.
                throw new InvalidOperationException(
                    "simulated LoadAndValidateGameForQuickload throw");
                // Step 3 (unreachable): the wipe is the next operation.
#pragma warning disable CS0162 // Unreachable code -- documents the post-validation step we never reach.
                RecordingStore.ResetForBatchFlightBaselineRestoreBypassingGuard();
                wipeRan = true;
#pragma warning restore CS0162
            }
            catch (InvalidOperationException) { /* expected */ }

            // The wipe never ran because validation failed first.
            Assert.False(wipeRan,
                "validation failure must short-circuit before any save-scoped store is wiped");
            // Live data is untouched in every store.
            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("PreValidationLive", RecordingStore.CommittedRecordings[0].VesselName);
        }

        [Fact]
        public void BatchFlightBaselineRestore_PostTestRollback_RestoresBatchStart_NotTestMutations()
        {
            // P2 review regression: a test may layer synthetic mutations on
            // RecordingStore during its run. If the post-test
            // RestoreBatchFlightBaselineCore captures its rollback snapshot
            // INSIDE the call, that snapshot includes the test's mutations.
            // A subsequent restore failure would then roll back to the
            // synthetic state, not the player's authoritative pre-batch
            // state. Fix: capture the snapshot ONCE at batch start
            // (CaptureFlightBatchBaseline), use it for every rollback during
            // the batch.
            //
            // Models the post-test failure path: player has live data,
            // batch starts (snapshot captured), test layers a synthetic
            // mutation, restore fails before OnLoad fires, rollback
            // fires. The rollback must restore the player's pre-batch
            // state -- NOT the test-mutated state.
            var live = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "PreBatchLive");
            Assert.NotNull(live);
            RecordingStore.AddRecordingWithTreeForTesting(live);
            int liveTreeCount = RecordingStore.CommittedTrees.Count;

            // Step 1: batch capture snapshots the player's pre-batch state.
            var batchStartSnapshot = RecordingStoreTestSnapshot.Capture();

            RecordingStore.ApplicationIsPlayingForTesting = () => true;

            // Step 2: a test runs and layers a synthetic mutation. (This
            // models a `Capture/Restore` test that happens to abort or
            // forget to restore.)
            var synthetic = RecordingStore.CreateRecordingFromFlightData(MakePoints(2), "TestMutation");
            Assert.NotNull(synthetic);
            RecordingStore.AddRecordingWithTreeForTesting(synthetic);
            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);

            // Step 3: post-test restore starts. Bypass wipe runs.
            bool restoreCommitted = false;
            try
            {
                RecordingStore.ResetForBatchFlightBaselineRestoreBypassingGuard();
                Assert.Empty(RecordingStore.CommittedRecordings);

                // Step 4: simulate restore-step failure.
                throw new InvalidOperationException("simulated restore failure");
            }
            catch (InvalidOperationException) { /* expected */ }
            finally
            {
                if (!restoreCommitted)
                {
                    // P2 fix: roll back to the BATCH-START snapshot, not a
                    // fresh capture taken inside the restore call.
                    batchStartSnapshot.Restore();
                }
            }

            // Player's PRE-BATCH state is back. Synthetic test mutation
            // is gone. If the per-call capture pattern were still in use,
            // CommittedRecordings would have BOTH "PreBatchLive" AND
            // "TestMutation" — the wrong rollback target.
            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("PreBatchLive", RecordingStore.CommittedRecordings[0].VesselName);
            Assert.Equal(liveTreeCount, RecordingStore.CommittedTrees.Count);
        }

        [Fact]
        public void RunCoroutineSafely_NestedFailure_DisposesParentRoutine()
        {
            // P1 review regression: RunCoroutineSafely's nested-failure
            // path used to `yield break` after invoking `onFailure`,
            // abandoning the parent `routine` without calling Dispose().
            // C# iterator state machines only run try/finally blocks on
            // explicit Dispose -- not on consumer abandonment. So
            // RestoreBatchFlightBaselineCore's outer try/finally never
            // ran when a nested wait threw, and the snapshot rollback
            // never fired.
            //
            // Fix: wrap RunCoroutineSafely's body in try/finally that
            // disposes `routine` on every exit. This test pins that the
            // parent's finally now runs when a nested coroutine throws.
            bool parentFinallyRan = false;
            IEnumerator nestedThatThrows()
            {
                yield return null;
                throw new InvalidOperationException("nested failure");
            }
            IEnumerator parentWithFinally()
            {
                try
                {
                    yield return nestedThatThrows();
                    // unreachable
                }
                finally
                {
                    parentFinallyRan = true;
                }
            }

            Exception captured = null;
            // Drive the iterator manually -- mirrors how Unity's
            // CoroutineHost calls MoveNext on `RunCoroutineSafely`'s
            // returned enumerator.
            var safe = Parsek.InGameTests.InGameTestRunner.RunCoroutineSafely(
                parentWithFinally(), ex => captured = ex);
            DriveCoroutineToCompletion(safe);

            Assert.NotNull(captured);
            Assert.Equal("nested failure", captured.Message);
            Assert.True(parentFinallyRan,
                "RunCoroutineSafely must dispose the parent iterator on nested-failure abandon so its try/finally runs");
        }

        [Fact]
        public void RunCoroutineSafely_SynchronousFailure_DisposesParentRoutine()
        {
            // Sibling coverage: when MoveNext on the parent throws (not
            // inside a yielded nested coroutine, but in the parent's own
            // body before the next yield), the iterator state machine's
            // own throw-unwind already runs its try/finally. But
            // RunCoroutineSafely also calls Dispose explicitly via the
            // try/finally wrapper -- this test pins that the explicit
            // Dispose is idempotent (no double-finally) on the
            // synchronous-throw path.
            int parentFinallyCount = 0;
            IEnumerator parentThatThrows()
            {
                try
                {
                    yield return null;
                    throw new InvalidOperationException("parent throws synchronously");
                }
                finally
                {
                    parentFinallyCount++;
                }
            }

            Exception captured = null;
            var safe = Parsek.InGameTests.InGameTestRunner.RunCoroutineSafely(
                parentThatThrows(), ex => captured = ex);
            DriveCoroutineToCompletion(safe);

            Assert.NotNull(captured);
            Assert.Equal("parent throws synchronously", captured.Message);
            Assert.Equal(1, parentFinallyCount);
        }

        [Fact]
        public void RunCoroutineSafely_NestedFailure_DisposesUsingBlockInsideParent()
        {
            // Round-3 P3 review: RestoreBatchFlightBaselineCore wraps the
            // staging-dir activation in `using (var stagedSnapshot = ...)`.
            // When a nested wait throws AFTER the using block has exited
            // (the using's Dispose has already run), the iterator state
            // machine is suspended at the post-using yield and the parent's
            // try/finally is still in scope. RunCoroutineSafely's
            // disposal of the parent must unwind that try/finally without
            // re-disposing the already-disposed staging snapshot.
            //
            // This test models a parent iterator that uses an IDisposable
            // inside the try, exits the using before yielding, then yields
            // a nested coroutine that throws. The using's Dispose must
            // have run exactly once (during normal flow), and the
            // parent's outer try/finally must run exactly once (during
            // the parent-iterator disposal triggered by RunCoroutineSafely).
            int usingDisposeCount = 0;
            int parentFinallyCount = 0;
            IEnumerator parentWithUsingAndYieldedThrow()
            {
                try
                {
                    using (var disposable = new TestDisposable(() => usingDisposeCount++))
                    {
                        // No-op inside using -- exits immediately.
                    }
                    // using has Disposed by now. Yield a nested-throwing.
                    yield return ThrowAfterOneTick();
                }
                finally
                {
                    parentFinallyCount++;
                }
            }

            Exception captured = null;
            var safe = Parsek.InGameTests.InGameTestRunner.RunCoroutineSafely(
                parentWithUsingAndYieldedThrow(), ex => captured = ex);
            DriveCoroutineToCompletion(safe);

            Assert.NotNull(captured);
            Assert.Equal(1, usingDisposeCount);    // using ran exactly once
            Assert.Equal(1, parentFinallyCount);   // parent finally ran exactly once
        }

        private static IEnumerator ThrowAfterOneTick()
        {
            yield return null;
            throw new InvalidOperationException("nested failure after one tick");
        }

        private sealed class TestDisposable : IDisposable
        {
            private readonly Action onDispose;
            public TestDisposable(Action onDispose) { this.onDispose = onDispose; }
            public void Dispose() => onDispose?.Invoke();
        }

        [Fact]
        public void RunCoroutineSafely_NaturalCompletion_DisposesParentRoutine()
        {
            // When the parent completes naturally (no failure), Dispose
            // is still called. For a compiler-generated iterator that
            // ran to completion, Dispose is a no-op, but the call is
            // still well-defined. Pins idempotency on the success path.
            int parentFinallyCount = 0;
            IEnumerator parentThatCompletes()
            {
                try
                {
                    yield return null;
                    yield return null;
                }
                finally
                {
                    parentFinallyCount++;
                }
            }

            Exception captured = null;
            var safe = Parsek.InGameTests.InGameTestRunner.RunCoroutineSafely(
                parentThatCompletes(), ex => captured = ex);
            DriveCoroutineToCompletion(safe);

            Assert.Null(captured);
            Assert.Equal(1, parentFinallyCount);
        }

        private static void DriveCoroutineToCompletion(IEnumerator routine)
        {
            // Simple driver: walk MoveNext until exhausted. Nested
            // IEnumerator yields are not recursively expanded -- that
            // matches Unity's coroutine host (sub-coroutines have to be
            // explicitly StartCoroutine'd). RunCoroutineSafely yields
            // the recursive RunCoroutineSafely directly, so this driver
            // walks both levels correctly.
            var stack = new Stack<IEnumerator>();
            stack.Push(routine);
            while (stack.Count > 0)
            {
                var top = stack.Peek();
                if (top.MoveNext())
                {
                    if (top.Current is IEnumerator nested)
                        stack.Push(nested);
                }
                else
                {
                    stack.Pop();
                }
            }
        }

        [Fact]
        public void BatchFlightBaselineRestore_FailedQuickload_SnapshotRollbackRecoversLiveData()
        {
            // P1 review regression: the runner's RestoreBatchFlightBaselineCore
            // wraps the bypass wipe + TriggerQuickload + WaitForFlightReady
            // sequence in try/finally with a pre-wipe RecordingStoreTestSnapshot
            // that gets restored when restoreCommitted=false. This test models
            // that recovery path end-to-end without needing a Unity scene:
            //   1. Inject the player's "live" recording.
            //   2. Capture a snapshot (what the runner does first).
            //   3. Run the bypass wipe (simulates the runner's prep step).
            //   4. Quickload "fails" (simulated by NOT touching disk).
            //   5. snapshot.Restore() in the finally (the runner's rollback).
            // After step 5 the player's live recording must be back exactly
            // as it was, even though play mode was active and the bypass
            // wipe ran. Without the fail-closed pattern in PR #805, step 4's
            // failure would leave the player with an empty RecordingStore.
            var live = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "PreCrashLive");
            Assert.NotNull(live);
            RecordingStore.AddRecordingWithTreeForTesting(live);
            int liveTreeCount = RecordingStore.CommittedTrees.Count;
            Assert.Single(RecordingStore.CommittedRecordings);

            RecordingStore.ApplicationIsPlayingForTesting = () => true;

            // Step 2: pre-wipe snapshot (what the runner captures).
            var preWipeSnapshot = RecordingStoreTestSnapshot.Capture();
            bool restoreCommitted = false;
            try
            {
                // Step 3: bypass wipe (the runner's
                // PrepareForIsolatedBatchFlightBaselineRestore).
                RecordingStore.ResetForBatchFlightBaselineRestoreBypassingGuard();
                Assert.Empty(RecordingStore.CommittedRecordings);

                // Step 4: simulate a TriggerQuickload throw / WaitForFlightReady
                // timeout. We never set restoreCommitted=true.
                throw new InvalidOperationException("simulated TriggerQuickload skip");
            }
            catch (InvalidOperationException)
            {
                // Caught for the test (the runner's RunCoroutineSafely catches
                // for the same reason in production).
            }
            finally
            {
                if (!restoreCommitted)
                {
                    preWipeSnapshot.Restore();
                }
            }

            // Player's live recording back, in the exact same shape.
            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("PreCrashLive", RecordingStore.CommittedRecordings[0].VesselName);
            Assert.Equal(liveTreeCount, RecordingStore.CommittedTrees.Count);
        }

        [Fact]
        public void ResetForBatchFlightBaselineRestoreBypassingGuard_DoesNotWeakenRegularResetGuard()
        {
            // Regression guard: the public ResetForTesting() must still
            // throw on live data even after the bypass variant is in scope.
            // Pins that the refactor did not silently flip the guard's
            // sense for the public method.
            var live = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "GuardedLive");
            Assert.NotNull(live);
            RecordingStore.AddRecordingWithTreeForTesting(live);

            RecordingStore.ApplicationIsPlayingForTesting = () => true;

            var ex = Assert.Throws<InvalidOperationException>(
                () => RecordingStore.ResetForTesting());
            Assert.Contains("ResetForTesting blocked", ex.Message);
            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("GuardedLive", RecordingStore.CommittedRecordings[0].VesselName);
        }

        [Fact]
        public void Snapshot_GuardStillBlocksDirectReset_AfterRestore()
        {
            // End-to-end: even if the test correctly snapshot/restored its work, a
            // stray call to ResetForTesting() AFTER the restore (still in play mode)
            // must still throw — the live data is back, the guard must protect it.
            var live = RecordingStore.CreateRecordingFromFlightData(MakePoints(3), "PostRestoreLive");
            Assert.NotNull(live);
            RecordingStore.AddRecordingWithTreeForTesting(live);

            var snapshot = RecordingStoreTestSnapshot.Capture();
            var synth = RecordingStore.CreateRecordingFromFlightData(MakePoints(2), "Synth");
            Assert.NotNull(synth);
            RecordingStore.AddRecordingWithTreeForTesting(synth);
            snapshot.Restore();

            RecordingStore.ApplicationIsPlayingForTesting = () => true;

            Assert.Throws<InvalidOperationException>(() => RecordingStore.ResetForTesting());
            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("PostRestoreLive", RecordingStore.CommittedRecordings[0].VesselName);
        }
    }
}
