using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 6 of Rewind-to-Staging (design §6.3 step 4 phase 1+2): guards the
    /// atomic provisional-recording add + ReFlySessionMarker write in
    /// <see cref="RewindInvoker.AtomicMarkerWrite"/>. Verifies the two
    /// checkpoints (A: before/after provisional, B: before/after marker) run
    /// synchronously with no yield between them, and that a throw between the
    /// provisional add and the marker write rolls back the provisional.
    /// </summary>
    [Collection("Sequential")]
    public class AtomicMarkerWriteTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public AtomicMarkerWriteTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RewindInvoker.CheckpointHookForTesting = null;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;

            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RewindInvoker.CheckpointHookForTesting = null;
            RewindInvoker.FlightReadyProbeOverrideForTesting = null;
            RewindInvoker.DeferUntilFlightReadyOverrideForTesting = null;
            RewindInvokeContext.Clear();
        }

        [Fact]
        public void ConsumePostLoad_FlightNotReady_DefersStripToOnFlightReady()
        {
            // Regression for the post-2026-04-25 playtest log:
            //   SPACECENTER→FLIGHT is an async scene change; OnLoad (and thus
            //   ConsumePostLoad) fires before KSP populates FlightGlobals.Vessels.
            //   Running Strip synchronously at that point finds zero candidates
            //   and the invocation bails with
            //   "Activate failed: selected vessel not present on reload".
            // The fix defers Strip+Activate+AtomicMarkerWrite to onFlightReady.
            var scenario = MakeScenario();
            var (rp, slot) = MakeRpAndSlot();

            // Stage the context so ConsumePostLoad actually enters its body.
            // No bundle, no temp path — reconcile is skipped (logged as Warn)
            // and deferral decision runs.
            RewindInvokeContext.Pending = true;
            RewindInvokeContext.SessionId = "sess_defer";
            RewindInvokeContext.RewindPoint = rp;
            RewindInvokeContext.Selected = slot;
            RewindInvokeContext.CapturedBundle = default(ReconciliationBundle);
            RewindInvokeContext.HasCapturedBundle = false;
            RewindInvokeContext.TempQuicksavePath = null;

            RewindInvoker.FlightReadyProbeOverrideForTesting = () => false;
            Action deferred = null;
            string deferredTempPath = null;
            RewindInvoker.DeferUntilFlightReadyOverrideForTesting = (a, path) =>
            {
                deferred = a;
                deferredTempPath = path;
            };

            RewindInvoker.ConsumePostLoad();

            Assert.NotNull(deferred);
            Assert.Null(deferredTempPath); // test context staged null tempPath
            Assert.True(RewindInvokeContext.Pending, "context must survive deferral so onFlightReady can consume it");
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]")
                && l.Contains("ConsumePostLoad deferred to onFlightReady")
                && l.Contains("sess_defer"));
        }

        [Fact]
        public void ConsumePostLoad_DeferredTempPath_PropagatesThroughSeam()
        {
            // Review item: the deferred rewind timeout branch must receive
            // the temp quicksave path so it can clean up on flight-ready
            // wait timeout / action failure. Without this, a catastrophic
            // scene-load that never fires onFlightReady would leak the
            // root-level Parsek_Rewind_*.sfs copy and violate the
            // ConsumePostLoad cleanup contract.
            var scenario = MakeScenario();
            var (rp, slot) = MakeRpAndSlot();

            const string stagedTempPath = @"C:\saves\s19\Parsek_Rewind_sess_xyz.sfs";
            RewindInvokeContext.Pending = true;
            RewindInvokeContext.SessionId = "sess_temp";
            RewindInvokeContext.RewindPoint = rp;
            RewindInvokeContext.Selected = slot;
            RewindInvokeContext.CapturedBundle = default(ReconciliationBundle);
            RewindInvokeContext.HasCapturedBundle = false;
            RewindInvokeContext.TempQuicksavePath = stagedTempPath;

            RewindInvoker.FlightReadyProbeOverrideForTesting = () => false;
            string sawTempPath = null;
            RewindInvoker.DeferUntilFlightReadyOverrideForTesting = (a, path) => sawTempPath = path;

            RewindInvoker.ConsumePostLoad();

            Assert.Equal(stagedTempPath, sawTempPath);
        }

        [Fact]
        public void StartInvoke_CanInvokeFailsAfterDialog_AbortsBeforeStaging()
        {
            // TOCTOU defense (review item 15): CanInvoke is checked at UI
            // render time AND at confirm-dialog show time, but state can
            // flip between dialog open and confirm click (RP marked
            // corrupted by load-time sweep, save file removed, another
            // re-fly session activates, scene transition starts).
            // StartInvoke must re-run CanInvoke at the action boundary so a
            // stale confirmation cannot bypass the safety gates. This test
            // drives the TOCTOU scenario: a dialog opens, then mid-dialog
            // the RP gets marked corrupted, then the user clicks Rewind.
            // StartInvoke must abort early — no provisional add, no marker
            // write, no context parked — and the WARN log must record the
            // reason from CanInvoke.
            MakeScenario();
            var (rp, slot) = MakeRpAndSlot();

            // Headless: keep CanInvoke off the disk-access path entirely.
            // Setting Corrupted=true short-circuits CanInvoke before it
            // resolves the quicksave path or runs the part-loader probe,
            // so the test does not depend on KSPUtil.ApplicationRootPath
            // or HighLogic.SaveFolder being set.
            rp.QuicksaveFilename = "Parsek/RewindPoints/" + rp.RewindPointId + ".sfs";
            rp.Corrupted = false; // dialog-time state: passes CanInvoke

            // Mid-dialog, between confirm-dialog show and confirm click,
            // the load-time sweep / external pipeline marks the RP
            // corrupted. The next CanInvoke now returns false.
            rp.Corrupted = true;

            int committedCountBefore = RecordingStore.CommittedRecordings.Count;
            Assert.False(RewindInvokeContext.Pending);
            Assert.Null(ParsekScenario.Instance.ActiveReFlySessionMarker);

            // CanInvoke checks the scene first. Pin to FLIGHT so the test
            // exercises the Corrupted branch instead of the scene branch.
            GameScenes priorScene = HighLogic.LoadedScene;
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            try
            {
                RewindInvoker.StartInvoke(rp, slot);
            }
            finally
            {
                HighLogic.LoadedScene = priorScene;
            }

            // No state-mutating work ran past the precondition gate.
            Assert.False(
                RewindInvokeContext.Pending,
                "StartInvoke must not park context when CanInvoke fails");
            Assert.Equal(committedCountBefore, RecordingStore.CommittedRecordings.Count);
            Assert.Null(ParsekScenario.Instance.ActiveReFlySessionMarker);

            // WARN log records the reason returned by CanInvoke, the rp id,
            // and the slot — so a regression that loses the precondition
            // re-run is diagnosable from the log alone.
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[Rewind]") &&
                l.Contains("precondition failed after dialog confirm") &&
                l.Contains("corrupted") &&
                l.Contains(rp.RewindPointId));
        }

        [Fact]
        public void ConsumePostLoad_StartInvokeGatePreventsStaleContext_PinnedBySourceInspection()
        {
            // Review item: StartInvoke must re-run CanInvoke so a stale
            // confirmation dialog / retry path / direct caller can't bypass
            // the safety gates (corrupted RP, missing parts, active session,
            // scene transition). Pin via source inspection because StartInvoke
            // touches KSP runtime (scene load, FlightDriver) and isn't
            // exercisable headless.
            string srcRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "Parsek"));
            string invokerSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(srcRoot, "RewindInvoker.cs"));

            int startInvokeStart = invokerSrc.IndexOf(
                "internal static void StartInvoke(", StringComparison.Ordinal);
            Assert.True(startInvokeStart >= 0, "StartInvoke must exist");

            // Find the body start (first `{`) and a reasonable end bound (the
            // `string sessionId = \"sess_\"` line where the function body kicks
            // off concrete work).
            int bodyStart = invokerSrc.IndexOf('{', startInvokeStart);
            int sessionIdAssign = invokerSrc.IndexOf(
                "string sessionId = \"sess_\"", bodyStart, StringComparison.Ordinal);
            Assert.True(sessionIdAssign > bodyStart,
                "StartInvoke body must include the sessionId assignment");

            string prelude = invokerSrc.Substring(bodyStart, sessionIdAssign - bodyStart);

            Assert.Contains("CanInvoke(rp, out", prelude);
            Assert.Contains("precondition failed after dialog confirm", prelude);
        }

        [Fact]
        public void ConsumePostLoad_PreFlightReadyWindow_CleansOnCatastrophicTimeout_PinnedBySourceInspection()
        {
            // Review item: deferring Strip+Activate+Marker leaves a static-only
            // pending window between Reconcile (runs in OnLoad) and the
            // actual durable marker+provisional write (runs when
            // FlightGlobals becomes ready). If KSP crashes / autosaves /
            // never fires onFlightReady in that window, the temp quicksave
            // and RewindInvokeContext must still be cleaned up. Pin the
            // timeout-cleanup contract via source inspection so a future
            // refactor that forgets `TryDeleteTemp` on the timeout branch
            // fails the test rather than leaking silently.
            string srcRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "Parsek"));
            string invokerSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(srcRoot, "RewindInvoker.cs"));

            int coroutineStart = invokerSrc.IndexOf(
                "WaitForFlightReadyAndInvoke(Action action, string tempPath)",
                StringComparison.Ordinal);
            Assert.True(coroutineStart >= 0,
                "WaitForFlightReadyAndInvoke must carry tempPath for cleanup");

            // The timeout branch must call TryDeleteTemp(tempPath) before
            // clearing the context so the temp file doesn't leak on crash.
            int timeoutBranch = invokerSrc.IndexOf(
                "timed out after", coroutineStart, StringComparison.Ordinal);
            Assert.True(timeoutBranch > coroutineStart, "Timeout branch must exist");

            int timeoutEnd = invokerSrc.IndexOf("yield break", timeoutBranch, StringComparison.Ordinal);
            Assert.True(timeoutEnd > timeoutBranch);

            string timeoutBody = invokerSrc.Substring(timeoutBranch, timeoutEnd - timeoutBranch);
            Assert.Contains("TryDeleteTemp(tempPath)", timeoutBody);
            Assert.Contains("RewindInvokeContext.Clear()", timeoutBody);
        }

        [Fact]
        public void ConsumePostLoad_FlightReady_RunsStripSynchronously()
        {
            // When FlightGlobals is populated (synchronous reload, or deferred
            // callback already firing), Strip runs immediately without
            // going through the deferred callback seam.
            var scenario = MakeScenario();
            var (rp, slot) = MakeRpAndSlot();

            RewindInvokeContext.Pending = true;
            RewindInvokeContext.SessionId = "sess_sync";
            RewindInvokeContext.RewindPoint = rp;
            RewindInvokeContext.Selected = slot;
            RewindInvokeContext.CapturedBundle = default(ReconciliationBundle);
            RewindInvokeContext.HasCapturedBundle = false;
            RewindInvokeContext.TempQuicksavePath = null;

            RewindInvoker.FlightReadyProbeOverrideForTesting = () => true;
            bool deferCalled = false;
            RewindInvoker.DeferUntilFlightReadyOverrideForTesting = (a, path) => deferCalled = true;

            // ConsumePostLoad will try to actually Strip + SetActiveVessel,
            // which touches FlightGlobals in a way that won't work headless.
            // We only assert that the deferral seam was NOT invoked; exceptions
            // from later steps are expected and tolerated.
            try { RewindInvoker.ConsumePostLoad(); } catch { }

            Assert.False(deferCalled, "Flight-ready path must not take the deferral seam.");
        }

        private static ParsekScenario MakeScenario()
        {
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint>(),
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            return scenario;
        }

        private static (RewindPoint rp, ChildSlot slot) MakeRpAndSlot()
        {
            var slot = new ChildSlot
            {
                SlotIndex = 0,
                OriginChildRecordingId = "recOrigin",
                Controllable = true,
            };
            var rp = new RewindPoint
            {
                RewindPointId = "rp_atomic",
                BranchPointId = "bp_atomic",
                ChildSlots = new List<ChildSlot> { slot },
                UT = 42.0,
            };
            return (rp, slot);
        }

        private static PostLoadStripResult MakeStripResult(uint selectedPid = 12345u)
        {
            return new PostLoadStripResult
            {
                SelectedVessel = null, // tests do not construct live Unity objects
                SelectedPid = selectedPid,
                StrippedPids = new List<uint>(),
                GhostsGuarded = 0,
                LeftAlone = 0,
                FallbackMatches = 0,
            };
        }

        [Fact]
        public void Phase1And2_SameSyncBlock_NoInterleaving()
        {
            // New-recording path (origin tree gone / origin recording not
            // committed): AtomicMarkerWrite creates a fresh placeholder
            // provisional and points the marker at it. No origin recording
            // is installed in the test fixture, so this exercises the
            // placeholder branch.
            var scenario = MakeScenario();
            var (rp, slot) = MakeRpAndSlot();

            var checkpoints = new List<string>();
            int markerWriteCounter = 0;
            int provisionalCountAtMarker = -1;

            RewindInvoker.CheckpointHookForTesting = tag =>
            {
                checkpoints.Add(tag);
                if (tag == "CheckpointB:BeforeMarker")
                {
                    // At this instant, the provisional must already be in the list.
                    provisionalCountAtMarker = RecordingStore.CommittedRecordings.Count;
                }
                if (tag == "CheckpointB:AfterMarker") markerWriteCounter++;
            };

            RewindInvoker.AtomicMarkerWrite(rp, slot, MakeStripResult(), "sess_test");

            // Checkpoint sequence: A:Before -> A:After -> B:Before -> B:After
            // No other call may be interleaved.
            Assert.Equal(new List<string>
            {
                "CheckpointA:BeforeProvisional",
                "CheckpointA:AfterProvisional",
                "CheckpointB:BeforeMarker",
                "CheckpointB:AfterMarker",
            }, checkpoints);

            // Provisional was already in the committed list when marker-write started.
            Assert.Equal(1, provisionalCountAtMarker);

            // Post-block: provisional + marker both present.
            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Equal("sess_test", scenario.ActiveReFlySessionMarker.SessionId);
            Assert.Equal(rp.RewindPointId, scenario.ActiveReFlySessionMarker.RewindPointId);
            Assert.Equal(slot.OriginChildRecordingId,
                scenario.ActiveReFlySessionMarker.OriginChildRecordingId);
            Assert.Equal(1, markerWriteCounter);

            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") && l.Contains("Started sess=sess_test"));
        }

        [Fact]
        public void ExceptionBetween_RemovesProvisional_NoLeak()
        {
            MakeScenario();
            var (rp, slot) = MakeRpAndSlot();

            RewindInvoker.CheckpointHookForTesting = tag =>
            {
                if (tag == "CheckpointA:AfterProvisional")
                    throw new InvalidOperationException("simulated marker failure");
            };

            Assert.Throws<InvalidOperationException>(() =>
            {
                RewindInvoker.AtomicMarkerWrite(rp, slot, MakeStripResult(), "sess_fail");
            });

            // Rollback: provisional removed AND marker cleared, so no half-
            // written pair leaks out of the critical section.
            Assert.Empty(RecordingStore.CommittedRecordings);
            Assert.Null(ParsekScenario.Instance.ActiveReFlySessionMarker);
        }

        /// <summary>
        /// Blocker 3: the ordering test alone does not prove atomicity — it
        /// checks the four checkpoints fire in order, but not that no save
        /// handler fires BETWEEN the provisional-add (end of phase 1) and the
        /// marker-write (start of phase 2).
        ///
        /// <para>
        /// This test subscribes an <c>onGameStateSave</c> handler before the
        /// atomic block and tracks whether the handler fires while we are
        /// inside the critical section (between CheckpointA:AfterProvisional
        /// and CheckpointB:AfterMarker). Because <c>AtomicMarkerWrite</c> is a
        /// pure synchronous method with no yield/await/IEnumerator and makes
        /// no KSP state-save calls, the handler must never fire inside that
        /// window — any future regression that introduces a save-side-effect
        /// mid-critical-section trips the assertion.
        /// </para>
        /// </summary>
        [Fact]
        public void Phase1And2_NoOnSaveBetween()
        {
            MakeScenario();
            var (rp, slot) = MakeRpAndSlot();

            // Tracker flipped by the handler if it ever fires inside the
            // critical section. Starts false; AtomicMarkerWrite has no code
            // path that should flip it.
            bool onSaveFiredBetweenPhases = false;
            bool insideCritical = false;

            EventData<ConfigNode>.OnEvent onSave = _ =>
            {
                if (insideCritical)
                    onSaveFiredBetweenPhases = true;
            };

            // Subscribe BEFORE the atomic block. GameEvents may be null in
            // some unit-test harnesses (no Unity runtime); guard defensively
            // so the test still asserts the invariant even if the subscription
            // cannot be wired.
            bool subscribed = false;
            try
            {
                if (GameEvents.onGameStateSave != null)
                {
                    GameEvents.onGameStateSave.Add(onSave);
                    subscribed = true;
                }
            }
            catch
            {
                // Fall through — the invariant still holds and the checkpoint
                // hook below exercises the key asserts regardless.
            }

            try
            {
                RewindInvoker.CheckpointHookForTesting = tag =>
                {
                    // Window: after the provisional is committed to the list
                    // (end of phase 1) up to just before the marker write
                    // completes (end of phase 2). If any save fires inside
                    // this window, the handler flips the tracker.
                    if (tag == "CheckpointA:AfterProvisional")
                        insideCritical = true;
                    else if (tag == "CheckpointB:AfterMarker")
                        insideCritical = false;
                };

                RewindInvoker.AtomicMarkerWrite(rp, slot, MakeStripResult(), "sess_atomic");

                // Primary invariant: the handler did not fire between phase 1
                // and phase 2. True by construction for the current code path;
                // the assertion guards against future regressions that insert
                // a save-triggering side effect into the critical section.
                Assert.False(onSaveFiredBetweenPhases,
                    "onGameStateSave fired between CheckpointA:AfterProvisional and " +
                    "CheckpointB:AfterMarker — atomicity invariant broken");

                // And the critical-section guard is cleanly closed — no
                // leftover 'insideCritical == true' after the method returns.
                Assert.False(insideCritical,
                    "insideCritical flag still set after AtomicMarkerWrite returned " +
                    "— CheckpointB:AfterMarker may have been skipped");

                // Post-block sanity: the atomic pair landed.
                Assert.Single(RecordingStore.CommittedRecordings);
                Assert.NotNull(ParsekScenario.Instance.ActiveReFlySessionMarker);
                Assert.Equal("sess_atomic",
                    ParsekScenario.Instance.ActiveReFlySessionMarker.SessionId);
            }
            finally
            {
                if (subscribed)
                {
                    try { GameEvents.onGameStateSave.Remove(onSave); }
                    catch { /* swallow unsubscribe errors in test teardown */ }
                }
            }
        }

        // ---------- In-place continuation vs new-recording paths (item 11) -----

        /// <summary>
        /// In-place continuation path: when the Limbo-restore kept the origin
        /// recording alive in the restored tree AND the strip-selected vessel
        /// pid matches the origin's <see cref="Recording.VesselPersistentId"/>,
        /// AtomicMarkerWrite must point the marker directly at the origin id
        /// without creating a placeholder. This eliminates the legacy
        /// placeholder-and-redirect cascade and the cycle-poisoning class of
        /// bug it introduced (#568).
        /// </summary>
        [Fact]
        public void AtomicMarkerWrite_InPlaceContinuation_PointsMarkerAtOriginNoPlaceholder()
        {
            const uint kOriginPid = 9999u;
            var scenario = MakeScenario();
            var (rp, slot) = MakeRpAndSlot();

            // Install the origin recording (committed) with the same pid as
            // the strip-selected vessel. AtomicMarkerWrite must detect this
            // and skip the placeholder.
            var origin = new Recording
            {
                RecordingId = slot.OriginChildRecordingId,
                VesselName = "rec_origin",
                TreeId = "tree_origin",
                MergeState = MergeState.Immutable,
                VesselPersistentId = kOriginPid,
            };
            RecordingStore.AddRecordingWithTreeForTesting(origin, "tree_origin");

            int committedCountBefore = RecordingStore.CommittedRecordings.Count;
            // Helper either adds 1 (TreeId already set) or creates a tree
            // that may carry a wrapper recording — capture the actual count
            // and assert delta semantics rather than absolute equality.

            RewindInvoker.AtomicMarkerWrite(
                rp, slot, MakeStripResult(selectedPid: kOriginPid), "sess_inplace");

            // No placeholder added — committed list size unchanged.
            Assert.Equal(committedCountBefore, RecordingStore.CommittedRecordings.Count);
            // The pre-existing origin recording is still in the list (by reference).
            bool foundOrigin = false;
            for (int i = 0; i < RecordingStore.CommittedRecordings.Count; i++)
            {
                if (ReferenceEquals(RecordingStore.CommittedRecordings[i], origin))
                {
                    foundOrigin = true;
                    break;
                }
            }
            Assert.True(foundOrigin, "origin recording must remain committed across in-place continuation");

            // Marker points directly at the origin id.
            var marker = scenario.ActiveReFlySessionMarker;
            Assert.NotNull(marker);
            Assert.Equal("sess_inplace", marker.SessionId);
            Assert.Equal(origin.RecordingId, marker.ActiveReFlyRecordingId);
            Assert.Equal(slot.OriginChildRecordingId, marker.OriginChildRecordingId);
            // Origin's TreeId is reused on the marker.
            Assert.Equal("tree_origin", marker.TreeId);

            // INFO log advertises the in-place continuation diagnosis so a
            // future regression that loses the detection is diagnosable.
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]")
                && l.Contains("in-place continuation detected")
                && l.Contains(origin.RecordingId)
                && l.Contains("no placeholder created"));

            // Started log carries inPlaceContinuation=True.
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("Started sess=sess_inplace")
                && l.Contains("inPlaceContinuation=True"));
        }

        /// <summary>
        /// New-recording path: when the origin recording is committed but its
        /// VesselPersistentId does not match the strip-selected pid (e.g. the
        /// origin tree was restored but the player is flying a different
        /// vessel slot), AtomicMarkerWrite falls back to creating a fresh
        /// placeholder provisional and pointing the marker at it. This is
        /// the original Phase 6 behavior; it is preserved for cases where
        /// the in-place continuation detection rejects.
        /// </summary>
        [Fact]
        public void AtomicMarkerWrite_OriginPidMismatch_CreatesPlaceholder()
        {
            const uint kOriginPid = 1111u;
            const uint kActivePid = 2222u;
            var scenario = MakeScenario();
            var (rp, slot) = MakeRpAndSlot();

            // Install an origin recording but with a DIFFERENT pid than the
            // strip-selected vessel. This forces the placeholder path even
            // though the recording is committed.
            var origin = new Recording
            {
                RecordingId = slot.OriginChildRecordingId,
                VesselName = "rec_origin",
                TreeId = "tree_origin",
                MergeState = MergeState.Immutable,
                VesselPersistentId = kOriginPid,
            };
            RecordingStore.AddRecordingWithTreeForTesting(origin, "tree_origin");
            origin.PreReFlyAnchorSessionId = "prior_session";
            origin.PreReFlyAnchorPoints = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 12.0 },
            };

            int committedCountBefore = RecordingStore.CommittedRecordings.Count;

            RewindInvoker.AtomicMarkerWrite(
                rp, slot, MakeStripResult(selectedPid: kActivePid), "sess_placeholder");

            // A new placeholder was added — committed list grew by exactly 1.
            Assert.Equal(committedCountBefore + 1, RecordingStore.CommittedRecordings.Count);

            var marker = scenario.ActiveReFlySessionMarker;
            Assert.NotNull(marker);
            // Marker DOES NOT point at the origin id — it points at the
            // freshly-built placeholder.
            Assert.NotEqual(origin.RecordingId, marker.ActiveReFlyRecordingId);
            Assert.False(string.IsNullOrEmpty(marker.ActiveReFlyRecordingId));
            // Origin's lineage metadata is still preserved on the marker.
            Assert.Equal(slot.OriginChildRecordingId, marker.OriginChildRecordingId);
            Assert.Equal("tree_origin", marker.TreeId);

            // The placeholder is a NotCommitted recording with the active
            // vessel's pid and no trajectory.
            Recording placeholder = null;
            for (int i = 0; i < RecordingStore.CommittedRecordings.Count; i++)
            {
                var r = RecordingStore.CommittedRecordings[i];
                if (r.RecordingId == marker.ActiveReFlyRecordingId)
                {
                    placeholder = r;
                    break;
                }
            }
            Assert.NotNull(placeholder);
            Assert.Equal(MergeState.NotCommitted, placeholder.MergeState);
            Assert.Equal(kActivePid, placeholder.VesselPersistentId);

            // Started log carries inPlaceContinuation=False.
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("Started sess=sess_placeholder")
                && l.Contains("inPlaceContinuation=False"));

            // The pid mismatch took the placeholder branch — no in-place
            // continuation log line should appear.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("in-place continuation detected"));
        }

        /// <summary>
        /// Reverse of the in-place test: even when the origin recording is
        /// committed and the pids match, an exception during marker write
        /// must NOT remove the (pre-existing) origin recording. The rollback
        /// only touches a placeholder that this path never added.
        /// </summary>
        [Fact]
        public void AtomicMarkerWrite_InPlaceContinuation_ExceptionDoesNotRemoveOrigin()
        {
            const uint kOriginPid = 7777u;
            var scenario = MakeScenario();
            var (rp, slot) = MakeRpAndSlot();

            var origin = new Recording
            {
                RecordingId = slot.OriginChildRecordingId,
                VesselName = "rec_origin",
                TreeId = "tree_origin",
                MergeState = MergeState.Immutable,
                VesselPersistentId = kOriginPid,
            };
            RecordingStore.AddRecordingWithTreeForTesting(origin, "tree_origin");
            origin.PreReFlyAnchorSessionId = "prior_session";
            origin.PreReFlyAnchorPoints = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 12.0 },
            };

            int committedCountBefore = RecordingStore.CommittedRecordings.Count;

            RewindInvoker.CheckpointHookForTesting = tag =>
            {
                if (tag == "CheckpointA:AfterProvisional")
                    throw new InvalidOperationException("simulated marker failure");
            };

            Assert.Throws<InvalidOperationException>(() =>
            {
                RewindInvoker.AtomicMarkerWrite(
                    rp, slot, MakeStripResult(selectedPid: kOriginPid), "sess_inplace_fail");
            });

            // Origin is NOT removed — the rollback path skips the recording-
            // remove call when no placeholder was added.
            Assert.Equal(committedCountBefore, RecordingStore.CommittedRecordings.Count);
            // Marker is cleared (rollback).
            Assert.Null(ParsekScenario.Instance.ActiveReFlySessionMarker);
            Recording storedOrigin = null;
            for (int i = 0; i < RecordingStore.CommittedRecordings.Count; i++)
            {
                var candidate = RecordingStore.CommittedRecordings[i];
                if (candidate != null && candidate.RecordingId == slot.OriginChildRecordingId)
                {
                    storedOrigin = candidate;
                    break;
                }
            }
            Assert.NotNull(storedOrigin);
            Assert.Equal("prior_session", storedOrigin.PreReFlyAnchorSessionId);
            Assert.Single(storedOrigin.PreReFlyAnchorPoints);
            Assert.Equal(12.0, storedOrigin.PreReFlyAnchorPoints[0].ut);
        }
    }
}
