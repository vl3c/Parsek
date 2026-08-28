using System;
using System.Collections.Generic;
using Parsek.TestCommands;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// WATCH-ENTRY-REFUSED-INSIDE-QUOTED-RANGE, the coordinated decision TAKEN: watch auto-select
    /// accepts a same-body, in-range ghost whose CACHED body reading is stale.
    ///
    /// <para>The sibling file <see cref="WatchModeTargetLossTests"/> pins the MECHANISM (the
    /// spawn seed a render-zone-hidden ghost holds indefinitely, and the engine-level cache read
    /// that reports it). This file pins the DECISION built on top of it: the body term resolves
    /// from the recording's own trajectory when the cache is not current, a genuinely cross-body
    /// ghost still refuses (design E5), an unresolvable trajectory falls back to exactly the
    /// pre-change answer, and the loop-phase reset stands down when the ghost's current phase is
    /// itself the watchable thing.</para>
    ///
    /// <para>Distances and body names are V7M-minmus-player-loop's measured values: a 98,463.595 m
    /// Minmus park, an observer 144,356 m away, and a ghost seeded <c>Kerbin</c> at spawn.</para>
    /// </summary>
    [Collection("Sequential")]
    public class WatchEntryAcceptanceTests : IDisposable
    {
        /// <summary>V7Mc run `_1607`'s cycle-1 park reading - the refusal this change fixes.</summary>
        private const double V7mParkSeparationMeters = 144356.0;

        private readonly List<string> logLines = new List<string>();

        public WatchEntryAcceptanceTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        // ---------------------------------------------------------------
        // Seam 1 - the body term answers from trajectory, not cache
        // ---------------------------------------------------------------

        /// <summary>
        /// THE SHAPE THAT USED TO REFUSE. Ghost seeded <c>Kerbin</c> at spawn, never re-positioned
        /// because the render zone hid it, observer at Minmus 144,356 m away - and the ghost's own
        /// replay was at Minmus the whole time. The trajectory says so, so the term accepts, and
        /// the range term was always going to pass (144 km &lt; the 300 km entry cutoff).
        /// </summary>
        [Fact]
        public void StaleCacheSameBodyInRange_IsAccepted()
        {
            WatchModeController.WatchBodyDecision decision =
                WatchModeController.ResolveWatchSameBodyDecision(
                    hasState: true,
                    cachedBodyName: "Kerbin",
                    zone: RenderingZone.Beyond,
                    trajectoryResolved: true,
                    trajectoryBodyName: "Minmus",
                    activeBodyName: "Minmus");

            Assert.True(decision.OnSameBody);
            Assert.Equal(
                WatchModeController.WatchBodyEvidence.TrajectoryResolved, decision.Evidence);
            Assert.Equal("Minmus", decision.GhostBodyName);

            // The other two conjuncts at the measured V7M values, so the acceptance is the
            // conjunction's and not just this term's.
            Assert.True(WatchModeController.IsWithinWatchEntryRange(V7mParkSeparationMeters));
            var candidates = new List<TestCommandEnterWatchMode.WatchCandidate>
            {
                new TestCommandEnterWatchMode.WatchCandidate
                {
                    Index = 0, InScope = true,
                    HasActiveGhost = true,
                    OnSameBody = decision.OnSameBody,
                    WithinVisualRange = WatchModeController.IsWithinWatchEntryRange(
                        V7mParkSeparationMeters),
                },
            };
            Assert.Equal(0, TestCommandEnterWatchMode.ResolveAutoWatchIndex(candidates));
        }

        /// <summary>
        /// The refusal the design still WANTS (E5): FloatingOrigin is centred on the active
        /// vessel, so a ghost genuinely at another body has no usable camera frame. The stale
        /// cache happens to agree here - the point is that the TRAJECTORY is what says no, so the
        /// refusal survives even when the cache is the thing that is wrong.
        /// </summary>
        [Fact]
        public void GenuinelyCrossBody_IsStillRefused()
        {
            WatchModeController.WatchBodyDecision refused =
                WatchModeController.ResolveWatchSameBodyDecision(
                    hasState: true,
                    cachedBodyName: "Kerbin",
                    zone: RenderingZone.Beyond,
                    trajectoryResolved: true,
                    trajectoryBodyName: "Kerbin",
                    activeBodyName: "Minmus");

            Assert.False(refused.OnSameBody);
            Assert.Equal(
                WatchModeController.WatchBodyEvidence.TrajectoryResolved, refused.Evidence);

            // ...and the mirror: a cache that wrongly says SAME body cannot rescue a ghost the
            // trajectory places elsewhere. The stale reading is never the deciding evidence in
            // EITHER direction.
            WatchModeController.WatchBodyDecision staleCacheAgrees =
                WatchModeController.ResolveWatchSameBodyDecision(
                    hasState: true,
                    cachedBodyName: "Minmus",
                    zone: RenderingZone.Beyond,
                    trajectoryResolved: true,
                    trajectoryBodyName: "Kerbin",
                    activeBodyName: "Minmus");

            Assert.False(staleCacheAgrees.OnSameBody);
            Assert.Equal(
                WatchModeController.WatchBodyEvidence.TrajectoryResolved,
                staleCacheAgrees.Evidence);
        }

        /// <summary>
        /// PINS TODAY'S BEHAVIOUR for recordings nothing can resolve. When the trajectory cannot
        /// answer at the playback UT the cache decides, which reproduces the pre-change
        /// <c>GhostPlaybackEngine.IsGhostOnBody</c> answer exactly - both ways round.
        /// </summary>
        [Fact]
        public void TrajectoryResolutionFailure_FallsBackToTheCache()
        {
            WatchModeController.WatchBodyDecision staleSaysNo =
                WatchModeController.ResolveWatchSameBodyDecision(
                    hasState: true,
                    cachedBodyName: "Kerbin",
                    zone: RenderingZone.Beyond,
                    trajectoryResolved: false,
                    trajectoryBodyName: null,
                    activeBodyName: "Minmus");
            Assert.False(staleSaysNo.OnSameBody);
            Assert.Equal(
                WatchModeController.WatchBodyEvidence.CacheFallback, staleSaysNo.Evidence);

            WatchModeController.WatchBodyDecision staleSaysYes =
                WatchModeController.ResolveWatchSameBodyDecision(
                    hasState: true,
                    cachedBodyName: "Minmus",
                    zone: RenderingZone.Beyond,
                    trajectoryResolved: false,
                    trajectoryBodyName: null,
                    activeBodyName: "Minmus");
            Assert.True(staleSaysYes.OnSameBody);
            Assert.Equal(
                WatchModeController.WatchBodyEvidence.CacheFallback, staleSaysYes.Evidence);

            // A resolver that returns true with no body name is not an answer either.
            WatchModeController.WatchBodyDecision resolvedButNameless =
                WatchModeController.ResolveWatchSameBodyDecision(
                    hasState: true,
                    cachedBodyName: "Minmus",
                    zone: RenderingZone.Beyond,
                    trajectoryResolved: true,
                    trajectoryBodyName: null,
                    activeBodyName: "Minmus");
            Assert.True(resolvedButNameless.OnSameBody);
            Assert.Equal(
                WatchModeController.WatchBodyEvidence.CacheFallback, resolvedButNameless.Evidence);

            // Never-seeded ghost, unresolvable trajectory: still no, exactly as before.
            WatchModeController.WatchBodyDecision neverSeeded =
                WatchModeController.ResolveWatchSameBodyDecision(
                    hasState: true,
                    cachedBodyName: null,
                    zone: RenderingZone.Beyond,
                    trajectoryResolved: false,
                    trajectoryBodyName: null,
                    activeBodyName: "Minmus");
            Assert.False(neverSeeded.OnSameBody);
            Assert.Equal(
                WatchModeController.WatchBodyEvidence.CacheFallback, neverSeeded.Evidence);
        }

        /// <summary>
        /// A ghost that IS being positioned answers from its cache, which is the freshest truth
        /// there is - the trajectory resolution is a fallback for the un-positioned, not a
        /// replacement for the positioner.
        /// </summary>
        [Fact]
        public void CurrentReading_DecidesAheadOfTheTrajectory()
        {
            foreach (RenderingZone zone in new[] { RenderingZone.Physics, RenderingZone.Visual })
            {
                WatchModeController.WatchBodyDecision decision =
                    WatchModeController.ResolveWatchSameBodyDecision(
                        hasState: true,
                        cachedBodyName: "Minmus",
                        zone: zone,
                        trajectoryResolved: true,
                        trajectoryBodyName: "Kerbin",
                        activeBodyName: "Minmus");
                Assert.True(decision.OnSameBody);
                Assert.Equal(
                    WatchModeController.WatchBodyEvidence.CacheCurrent, decision.Evidence);
            }
        }

        /// <summary>
        /// No ghost state is not a body answer, and a null active body cannot match anything -
        /// both were false before this change and stay false.
        /// </summary>
        [Fact]
        public void NoStateAndNoActiveBody_BothRefuse()
        {
            WatchModeController.WatchBodyDecision noState =
                WatchModeController.ResolveWatchSameBodyDecision(
                    hasState: false,
                    cachedBodyName: "Minmus",
                    zone: RenderingZone.Visual,
                    trajectoryResolved: true,
                    trajectoryBodyName: "Minmus",
                    activeBodyName: "Minmus");
            Assert.False(noState.OnSameBody);
            Assert.Equal(WatchModeController.WatchBodyEvidence.NoState, noState.Evidence);
            Assert.Null(noState.GhostBodyName);

            foreach (string activeBody in new[] { null, string.Empty })
            {
                WatchModeController.WatchBodyDecision noActive =
                    WatchModeController.ResolveWatchSameBodyDecision(
                        hasState: true,
                        cachedBodyName: "Minmus",
                        zone: RenderingZone.Beyond,
                        trajectoryResolved: true,
                        trajectoryBodyName: "Minmus",
                        activeBodyName: activeBody);
                Assert.False(noActive.OnSameBody);
            }
        }

        /// <summary>
        /// The decision line names the evidence that decided. Which reading answered is the whole
        /// finding - two documentation passes and a wrong mechanism claim were spent on
        /// attributing a refusal by guesswork - so the log says it outright.
        /// </summary>
        [Fact]
        public void DecisionLine_NamesTheEvidenceThatDecided()
        {
            WatchModeController.ResolveAndLogWatchSameBodyDecision(
                index: 1,
                recordingId: "775188af",
                hasState: true,
                cachedBodyName: "Kerbin",
                zone: RenderingZone.Beyond,
                trajectoryResolved: true,
                trajectoryBodyName: "Minmus",
                activeBodyName: "Minmus");

            Assert.Contains(logLines, l =>
                l.Contains("[CameraFollow]")
                && l.Contains("Watch same-body term:")
                && l.Contains("rec=#1")
                && l.Contains("id=775188af")
                && l.Contains("sameBody=T")
                && l.Contains("evidence=trajectory-resolved")
                && l.Contains("ghostBody=Minmus")
                && l.Contains("activeBody=Minmus")
                && l.Contains("cached=Kerbin")
                && l.Contains("zone=Beyond"));

            // Change-keyed, not per-frame: the same answer repeated does not re-emit.
            int afterFirst = logLines.Count;
            for (int i = 0; i < 5; i++)
            {
                WatchModeController.ResolveAndLogWatchSameBodyDecision(
                    index: 1, recordingId: "775188af", hasState: true,
                    cachedBodyName: "Kerbin", zone: RenderingZone.Beyond,
                    trajectoryResolved: true, trajectoryBodyName: "Minmus",
                    activeBodyName: "Minmus");
            }
            Assert.Equal(afterFirst, logLines.Count);

            // ...but a flip in the evidence or the answer does.
            logLines.Clear();
            WatchModeController.ResolveAndLogWatchSameBodyDecision(
                index: 1, recordingId: "775188af", hasState: true,
                cachedBodyName: "Kerbin", zone: RenderingZone.Beyond,
                trajectoryResolved: false, trajectoryBodyName: null,
                activeBodyName: "Minmus");
            Assert.Contains(logLines, l =>
                l.Contains("evidence=cache-fallback") && l.Contains("sameBody=F"));
        }

        [Fact]
        public void EvidenceTokens_AreTheGrepStableStrings()
        {
            Assert.Equal("no-state", WatchModeController.DescribeWatchBodyEvidence(
                WatchModeController.WatchBodyEvidence.NoState));
            Assert.Equal("cache-current", WatchModeController.DescribeWatchBodyEvidence(
                WatchModeController.WatchBodyEvidence.CacheCurrent));
            Assert.Equal("trajectory-resolved", WatchModeController.DescribeWatchBodyEvidence(
                WatchModeController.WatchBodyEvidence.TrajectoryResolved));
            Assert.Equal("cache-fallback", WatchModeController.DescribeWatchBodyEvidence(
                WatchModeController.WatchBodyEvidence.CacheFallback));
        }

        /// <summary>
        /// The resolver the live term calls, driven headlessly on the V7M shape: a Minmus park the
        /// ghost is flying at the sampled UT, resolved WITHOUT positioning the ghost. This is the
        /// evidence the term now decides on, so it is worth proving it answers at all rather than
        /// only mocking its result.
        /// </summary>
        [Fact]
        public void TrajectoryResolver_AnswersMinmusForAParkedGhostWithoutPositioningIt()
        {
            var traj = new MockTrajectory
            {
                VesselName = "Kerbal X",
                RecordingId = "775188af",
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 267000, bodyName = "Minmus", altitude = 98463.595,
                        velocity = Vector3.zero, rotation = Quaternion.identity,
                    },
                    new TrajectoryPoint
                    {
                        ut = 268000, bodyName = "Minmus", altitude = 98463.595,
                        velocity = Vector3.zero, rotation = Quaternion.identity,
                    },
                },
            };

            bool resolved = GhostPlaybackEngine.TryResolvePendingPlaybackInterpolation(
                traj, playbackUT: 267563.7, out InterpolationResult result);

            Assert.True(resolved);
            Assert.Equal("Minmus", result.bodyName);

            // Fed through the term with the stale Kerbin seed, that is an acceptance.
            WatchModeController.WatchBodyDecision decision =
                WatchModeController.ResolveWatchSameBodyDecision(
                    hasState: true, cachedBodyName: "Kerbin", zone: RenderingZone.Beyond,
                    trajectoryResolved: resolved, trajectoryBodyName: result.bodyName,
                    activeBodyName: "Minmus");
            Assert.True(decision.OnSameBody);
        }

        // ---------------------------------------------------------------
        // Seam 2 - the loop-phase reset stands down for a watchable current phase
        // ---------------------------------------------------------------

        /// <summary>
        /// THE TRAP SEAM 1 OPENS. A non-overlap looping ghost at <c>zone=Beyond</c> gets its loop
        /// phase reset to <c>EffectiveLoopStartUT</c> on entry - designed for an observer near the
        /// loop start. For an observer at an arrival park the loop start is another body ~46,000 km
        /// away: the reset would teleport the camera cross-body and the 305 km exit debounce would
        /// auto-exit within frames, worse than refusing and with a loop-phase reset left behind.
        /// </summary>
        [Fact]
        public void ResetLoopPhase_SkippedWhenTheCurrentPhaseIsItselfWatchable()
        {
            Assert.False(WatchModeController.ShouldResetLoopPhaseForWatch(
                zoneBeyond: true,
                shouldLoopPlayback: true,
                usesOverlapLooping: false,
                currentPhaseOnSameBody: true,
                currentPhaseWithinEntryRange: true));
        }

        /// <summary>
        /// The same shape stated the other way round, which is how V7M presents: the LOOP START is
        /// cross-body (Kerbin pad) while the current phase is the Minmus park alongside the
        /// observer. Entry proceeds on the current phase; nothing is reset.
        /// </summary>
        [Fact]
        public void ResetLoopPhase_SkippedWhenTheLoopStartIsCrossBodyButThePhaseIsNot()
        {
            bool currentPhaseOnSameBody = WatchModeController.ResolveWatchSameBodyDecision(
                hasState: true, cachedBodyName: "Kerbin", zone: RenderingZone.Beyond,
                trajectoryResolved: true, trajectoryBodyName: "Minmus",
                activeBodyName: "Minmus").OnSameBody;

            Assert.True(currentPhaseOnSameBody);
            Assert.False(WatchModeController.ShouldResetLoopPhaseForWatch(
                zoneBeyond: true,
                shouldLoopPlayback: true,
                usesOverlapLooping: false,
                currentPhaseOnSameBody: currentPhaseOnSameBody,
                currentPhaseWithinEntryRange: WatchModeController.IsWithinWatchEntryRange(
                    V7mParkSeparationMeters)));
        }

        /// <summary>
        /// THE RESET'S OWN CASE, PRESERVED. The observer is near the loop start and the ghost is
        /// mid-flight elsewhere - either at another body, or same-body but far outside the entry
        /// cutoff. Restarting the ghost at the pad next to the player is exactly what they asked
        /// for, so the reset still fires.
        /// </summary>
        [Theory]
        // cross-body current phase
        [InlineData(false, false)]
        [InlineData(false, true)]
        // same body, but the current phase is beyond the 300 km entry cutoff
        [InlineData(true, false)]
        public void ResetLoopPhase_PreservedWhenTheCurrentPhaseIsNotWatchable(
            bool currentPhaseOnSameBody, bool currentPhaseWithinEntryRange)
        {
            Assert.True(WatchModeController.ShouldResetLoopPhaseForWatch(
                zoneBeyond: true,
                shouldLoopPlayback: true,
                usesOverlapLooping: false,
                currentPhaseOnSameBody: currentPhaseOnSameBody,
                currentPhaseWithinEntryRange: currentPhaseWithinEntryRange));
        }

        /// <summary>
        /// The three pre-existing gate terms are untouched. Overlap looping in particular already
        /// skipped the reset (each cycle ghost carries its own phase) and must keep doing so
        /// regardless of what the new terms say.
        /// </summary>
        [Fact]
        public void ResetLoopPhase_PreExistingTermsAreUnchanged()
        {
            // In-zone ghost: never reset (the ghost is already rendered where it is).
            Assert.False(WatchModeController.ShouldResetLoopPhaseForWatch(
                zoneBeyond: false, shouldLoopPlayback: true, usesOverlapLooping: false,
                currentPhaseOnSameBody: false, currentPhaseWithinEntryRange: false));

            // Non-looping recording: there is no loop phase to reset.
            Assert.False(WatchModeController.ShouldResetLoopPhaseForWatch(
                zoneBeyond: true, shouldLoopPlayback: false, usesOverlapLooping: false,
                currentPhaseOnSameBody: false, currentPhaseWithinEntryRange: false));

            // Overlap looping: skipped before this change and skipped after, on every combination
            // of the two new terms.
            foreach (bool sameBody in new[] { false, true })
            {
                foreach (bool inRange in new[] { false, true })
                {
                    Assert.False(WatchModeController.ShouldResetLoopPhaseForWatch(
                        zoneBeyond: true, shouldLoopPlayback: true, usesOverlapLooping: true,
                        currentPhaseOnSameBody: sameBody, currentPhaseWithinEntryRange: inRange));
                }
            }
        }
    }
}
