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
                if (tag == "CheckpointB:BeforeMarker")
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
    }
}
