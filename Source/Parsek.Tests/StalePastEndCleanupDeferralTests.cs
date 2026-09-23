using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// D18-HELD-GHOST-DESTROYED-BY-STALE-PAST-END-CLEANUP-SAME-FRAME: the engine's stale
    /// past-end cleanup must not destroy a ghost whose completion event has not reached the
    /// policy yet, because the policy may hold that ghost for a blocked spawn (design 13.5).
    /// The engine cells drive the real frame-tail sequence (queue completion, loop stale step,
    /// event delivery, post-delivery pass) with a fake policy subscribed to the completion.
    /// </summary>
    [Collection("Sequential")]
    public class StalePastEndCleanupDeferralTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public StalePastEndCleanupDeferralTests()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
            GhostPlaybackLogic.ResetForTesting();
            ParsekScenario.SetInstanceForTesting(null);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
            GhostPlaybackLogic.ResetForTesting();
            ParsekScenario.SetInstanceForTesting(null);
        }

        private static Recording MakeRecording(string id = "rec-held-ghost")
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Logi Cargo Rig",
            };
        }

        private static TrajectoryPlaybackFlags MakeFlags(Recording rec)
        {
            return new TrajectoryPlaybackFlags
            {
                recordingId = rec.RecordingId,
                needsSpawn = true,
                chainEndUT = 100.0,
            };
        }

        private static GhostPlaybackEngine MakeEngine()
        {
            return new GhostPlaybackEngine(null)
            {
                DestroyGhostResourcesOverrideForTesting = state => { },
            };
        }

        // ---- pure decision ----

        [Fact]
        public void Decide_PendingCompletionNotHeld_Defers()
        {
            Assert.Equal(GhostPlaybackEngine.StalePastEndCleanupDecision.DeferUntilCompletionDelivered,
                GhostPlaybackEngine.DecideStalePastEndCleanup(
                    hasState: true, completionFired: true, ghostHeld: false,
                    completionPendingDelivery: true));
        }

        [Fact]
        public void Decide_DeliveredNotHeld_Destroys()
        {
            Assert.Equal(GhostPlaybackEngine.StalePastEndCleanupDecision.Destroy,
                GhostPlaybackEngine.DecideStalePastEndCleanup(
                    hasState: true, completionFired: true, ghostHeld: false,
                    completionPendingDelivery: false));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Decide_Held_Keeps(bool pending)
        {
            Assert.Equal(GhostPlaybackEngine.StalePastEndCleanupDecision.KeepHeld,
                GhostPlaybackEngine.DecideStalePastEndCleanup(
                    hasState: true, completionFired: true, ghostHeld: true,
                    completionPendingDelivery: pending));
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(false, false)]
        public void Decide_NoStateOrNotCompleted_NotApplicable(bool hasState, bool completionFired)
        {
            Assert.Equal(GhostPlaybackEngine.StalePastEndCleanupDecision.NotApplicable,
                GhostPlaybackEngine.DecideStalePastEndCleanup(
                    hasState, completionFired, ghostHeld: false, completionPendingDelivery: true));
        }

        // ---- engine frame tail with a fake policy ----

        [Fact]
        public void FrameTail_PolicyHoldsOnDelivery_GhostSurvivesTheFrame()
        {
            var rec = MakeRecording();
            var engine = MakeEngine();
            engine.ghostStates[0] = new GhostPlaybackState { vesselName = rec.VesselName };
            var held = new HashSet<int>();
            bool ghostPresentAtDelivery = false;
            engine.IsGhostHeld = idx => held.Contains(idx);
            engine.OnPlaybackCompleted += evt =>
            {
                ghostPresentAtDelivery = engine.HasGhost(evt.Index);
                held.Add(evt.Index); // spawn blocked: hold the ghost
            };

            engine.RunPastEndFrameTailForTesting(0, rec, MakeFlags(rec), 101.0, queueCompletion: true);

            Assert.True(ghostPresentAtDelivery);
            Assert.True(engine.HasGhost(0));
            Assert.DoesNotContain(logLines, l => l.Contains("stale past-end ghost (no longer held)"));
            Assert.Contains(logLines, l => l.Contains("[Engine]")
                && l.Contains("Stale past-end cleanup after completion delivery: ghost #0")
                && l.Contains("kept (held by the policy)"));

            // Next frame: completion already delivered, still held -> kept.
            engine.RunPastEndFrameTailForTesting(0, rec, MakeFlags(rec), 102.0, queueCompletion: false);
            Assert.True(engine.HasGhost(0));
        }

        [Fact]
        public void FrameTail_DeliveredAndNotHeld_GhostCleanedUpSameFrame()
        {
            var rec = MakeRecording();
            var engine = MakeEngine();
            engine.ghostStates[0] = new GhostPlaybackState { vesselName = rec.VesselName };
            bool ghostPresentAtDelivery = false;
            engine.IsGhostHeld = idx => false;
            engine.OnPlaybackCompleted += evt =>
                ghostPresentAtDelivery = engine.HasGhost(evt.Index); // policy leaves it alone

            engine.RunPastEndFrameTailForTesting(0, rec, MakeFlags(rec), 101.0, queueCompletion: true);

            Assert.True(ghostPresentAtDelivery);
            Assert.False(engine.HasGhost(0));
            Assert.Contains(logLines, l => l.Contains("[Engine]")
                && l.Contains("destroyed (stale past-end ghost (no longer held))"));
            Assert.Contains(logLines, l => l.Contains("[Engine]")
                && l.Contains("Stale past-end cleanups after completion delivery: deferred=1")
                && l.Contains("destroyed=1"));
        }

        [Fact]
        public void FrameTail_PolicyDestroysOnDelivery_PostPassSeesItGone()
        {
            var rec = MakeRecording();
            var engine = MakeEngine();
            engine.ghostStates[0] = new GhostPlaybackState { vesselName = rec.VesselName };
            engine.IsGhostHeld = idx => false;
            engine.OnPlaybackCompleted += evt =>
                engine.DestroyGhost(evt.Index, evt.Trajectory, evt.Flags, reason: "playback completed");

            engine.RunPastEndFrameTailForTesting(0, rec, MakeFlags(rec), 101.0, queueCompletion: true);

            Assert.False(engine.HasGhost(0));
            Assert.Contains(logLines, l => l.Contains("destroyed (playback completed)"));
            Assert.DoesNotContain(logLines, l => l.Contains("stale past-end ghost (no longer held)"));
            Assert.Contains(logLines, l => l.Contains("alreadyGone=1"));
        }

        [Fact]
        public void FrameTail_CompletionAlreadyDeliveredAndNotHeld_DestroyedInTheLoopStep()
        {
            // The mirror direction: a genuinely stale ghost (completion delivered on an
            // earlier frame, policy chose not to hold) is still cleaned up without deferral.
            var rec = MakeRecording();
            var engine = MakeEngine();
            engine.ghostStates[0] = new GhostPlaybackState { vesselName = rec.VesselName };
            var held = new HashSet<int> { 0 };
            engine.IsGhostHeld = idx => held.Contains(idx);
            engine.RunPastEndFrameTailForTesting(0, rec, MakeFlags(rec), 101.0, queueCompletion: true);
            Assert.True(engine.HasGhost(0));

            held.Clear(); // e.g. watch hold ended
            logLines.Clear();
            engine.RunPastEndFrameTailForTesting(0, rec, MakeFlags(rec), 102.0, queueCompletion: false);

            Assert.False(engine.HasGhost(0));
            Assert.Contains(logLines, l => l.Contains("destroyed (stale past-end ghost (no longer held))"));
            Assert.DoesNotContain(logLines, l => l.Contains("after completion delivery"));
        }

        // ---- real policy: held until the timeout, then destroyed ----

        [Fact]
        public void RealPolicyHold_KeptUntilTimeout_ThenDestroyed()
        {
            var rec = MakeRecording("rec-held-timeout");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            int index = -1;
            for (int k = 0; k < RecordingStore.CommittedRecordings.Count; k++)
                if (ReferenceEquals(RecordingStore.CommittedRecordings[k], rec)) index = k;
            Assert.True(index >= 0);

            float now = 10f;
            var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            var engine = MakeEngine();
            var policy = new ParsekPlaybackPolicy(engine, host)
            {
                CurrentRealTimeOverrideForTesting = () => now,
                CurrentUTOverrideForTesting = () => 500.0,
                TimelineInactiveIdsOverrideForTesting = committed =>
                    new Dictionary<string, TimelineInactiveReason>(),
                SpawnVesselOrChainTipOverrideForTesting = (recording, i) => { /* still blocked */ },
            };
            engine.ghostStates[index] = new GhostPlaybackState { vesselName = rec.VesselName };
            policy.heldGhosts[index] = new HeldGhostInfo
            {
                holdStartTime = now,
                recordingId = rec.RecordingId,
                vesselName = rec.VesselName,
            };

            // Completion already delivered (the hold exists): the loop step keeps the ghost.
            engine.RunPastEndFrameTailForTesting(index, rec, MakeFlags(rec), 101.0, queueCompletion: false);
            Assert.True(engine.HasGhost(index));

            now = 12f; // retry, still blocked, inside the timeout
            policy.RetryHeldGhostSpawns();
            engine.RunPastEndFrameTailForTesting(index, rec, MakeFlags(rec), 102.0, queueCompletion: false);
            Assert.True(engine.HasGhost(index));
            Assert.True(policy.heldGhosts.ContainsKey(index));

            now = 10f + ParsekPlaybackPolicy.HeldGhostTimeoutSeconds + 0.1f;
            policy.RetryHeldGhostSpawns();

            Assert.False(engine.HasGhost(index));
            Assert.False(policy.heldGhosts.ContainsKey(index));
            Assert.Contains(logLines, l => l.Contains("[Policy]") && l.Contains("Held ghost timed out"));
            Assert.Contains(logLines, l => l.Contains("destroyed (held-spawn-timeout)"));
        }

        // ---- policy log honesty ----

        [Fact]
        public void DescribeHeldGhostVisibility_OnlyClaimsVisibleWhenGhostExists()
        {
            Assert.Equal("ghost stays visible",
                ParsekPlaybackPolicy.DescribeHeldGhostVisibility(engineHasGhost: true));
            string none = ParsekPlaybackPolicy.DescribeHeldGhostVisibility(engineHasGhost: false);
            Assert.DoesNotContain("stays visible", none);
            Assert.Contains("no ghost", none);
        }
    }
}
