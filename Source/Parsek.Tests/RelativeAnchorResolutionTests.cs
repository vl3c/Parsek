using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="RelativeAnchorResolution"/>, the pure decision
    /// helper used by relative-frame ghost playback to decide whether a live
    /// anchor is usable, whether a Re-Fly target pid must be bypassed in
    /// favor of recorded anchor motion, or whether the ghost must be retired
    /// for the duration of the relative section.
    ///
    /// <para>
    /// Bug B (2026-04-26): a Re-Fly rewind erased the originally recorded
    /// anchor vessel (Kerbal X Probe, pid=3151978247). Post-rewind, the
    /// playback path resolved <c>FindVesselByPid</c> to null and "froze" a
    /// freshly spawned ghost in place -- but the ghost had no last position,
    /// so it rendered at world origin (0,0,0) with a bogus reported distance.
    /// The fix retires the ghost (hides it via <c>SetActive(false)</c>) and
    /// emits a one-shot per-(recording, anchor) WARN tagged
    /// <c>relative-anchor-retired</c>. The frozen-at-origin failure mode is
    /// unreachable.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class RelativeAnchorResolutionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RelativeAnchorResolutionTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        #region Decide

        [Fact]
        public void Decide_ResolverReturnsTrue_OutcomeResolved()
        {
            var outcome = RelativeAnchorResolution.Decide(
                anchorPid: 42u,
                resolver: pid => pid == 42u);

            Assert.Equal(RelativeAnchorResolution.Outcome.Resolved, outcome);
        }

        [Fact]
        public void Decide_ResolverReturnsFalse_OutcomeRetired()
        {
            // Bug B repro: recorded anchorPid=3151978247 was destroyed by a
            // Re-Fly rewind, so post-rewind the resolver returns false.
            var outcome = RelativeAnchorResolution.Decide(
                anchorPid: 3151978247u,
                resolver: _ => false);

            Assert.Equal(RelativeAnchorResolution.Outcome.Retired, outcome);
        }

        [Fact]
        public void Decide_AnchorPidZero_OutcomeRetired()
        {
            // anchorPid==0 sentinel from a corrupted track section: no
            // vessel can ever match, so the resolver is never invoked.
            bool resolverCalled = false;
            var outcome = RelativeAnchorResolution.Decide(
                anchorPid: 0u,
                resolver: _ => { resolverCalled = true; return true; });

            Assert.Equal(RelativeAnchorResolution.Outcome.Retired, outcome);
            Assert.False(resolverCalled, "resolver must short-circuit on pid=0");
        }

        [Fact]
        public void Decide_ResolverNull_OutcomeRetired()
        {
            // Defensive null handling: callers in test fixtures may not have
            // a resolver wired up.
            var outcome = RelativeAnchorResolution.Decide(
                anchorPid: 42u,
                resolver: null);

            Assert.Equal(RelativeAnchorResolution.Outcome.Retired, outcome);
        }

        #endregion

        #region ShouldBypassLiveAnchorForActiveReFly

        [Fact]
        public void ShouldBypassLiveAnchorForActiveReFly_SamePidDifferentRecording_ReturnsTrue()
        {
            // 2026-04-26_1332: Kerbal X recorded a relative section anchored
            // to the booster pid. During booster Re-Fly, that same pid mapped
            // to the live player vessel, so the upper-stage ghost became
            // locked to the Re-Fly booster instead of replaying ground-relative
            // recorded motion.
            Assert.True(RelativeAnchorResolution.ShouldBypassLiveAnchorForActiveReFly(
                anchorPid: 698412738u,
                activeReFlyPid: 698412738u,
                victimRecordingId: "upper-stage",
                activeReFlyRecordingId: "booster",
                victimIsParentOfActiveReFly: true));
        }

        [Fact]
        public void ShouldBypassLiveAnchorForActiveReFly_SamePidSiblingChain_ReturnsTrue()
        {
            // Repro for KSP.log 2026-04-26 a0d14b08 (Kerbal X upper stage):
            // a sibling chain (NOT a parent of the re-flown probe) carried
            // Relative sections anchored to the probe's PID. The earlier
            // parent-chain-only restriction left those sections decoding
            // against the player's live probe pose, producing visible
            // sub-surface jumps and km-scale localOffset deltas. Match by
            // anchorPid alone (modulo the explicit self-link guard) is
            // correct: the only legitimately-Relative-anchored case for the
            // active Re-Fly PID is the active recording itself, which the
            // string-equality clause below filters out.
            Assert.True(RelativeAnchorResolution.ShouldBypassLiveAnchorForActiveReFly(
                anchorPid: 698412738u,
                activeReFlyPid: 698412738u,
                victimRecordingId: "sibling-stage",
                activeReFlyRecordingId: "booster",
                victimIsParentOfActiveReFly: false));
        }

        [Fact]
        public void ShouldBypassLiveAnchorForActiveReFly_SamePidSameRecording_ReturnsFalse()
        {
            Assert.False(RelativeAnchorResolution.ShouldBypassLiveAnchorForActiveReFly(
                anchorPid: 698412738u,
                activeReFlyPid: 698412738u,
                victimRecordingId: "booster",
                activeReFlyRecordingId: "booster",
                victimIsParentOfActiveReFly: true));
        }

        [Fact]
        public void ShouldBypassLiveAnchorForActiveReFly_DifferentPid_ReturnsFalse()
        {
            Assert.False(RelativeAnchorResolution.ShouldBypassLiveAnchorForActiveReFly(
                anchorPid: 2708531065u,
                activeReFlyPid: 698412738u,
                victimRecordingId: "upper-stage",
                activeReFlyRecordingId: "booster",
                victimIsParentOfActiveReFly: true));
        }

        [Fact]
        public void ShouldBypassLiveAnchorForActiveReFly_MissingActivePid_ReturnsFalse()
        {
            Assert.False(RelativeAnchorResolution.ShouldBypassLiveAnchorForActiveReFly(
                anchorPid: 698412738u,
                activeReFlyPid: 0u,
                victimRecordingId: "upper-stage",
                activeReFlyRecordingId: "booster",
                victimIsParentOfActiveReFly: true));
        }

        [Fact]
        public void ShouldBypassLiveAnchorForActiveReFly_MatchingPidWithMissingIds_ReturnsFalse()
        {
            // Missing ids mean the parent-chain relationship cannot be proven.
            // Keep the live anchor path rather than broadening the Re-Fly
            // bypass to unrelated same-pid recordings.
            Assert.False(RelativeAnchorResolution.ShouldBypassLiveAnchorForActiveReFly(
                anchorPid: 698412738u,
                activeReFlyPid: 698412738u,
                victimRecordingId: null,
                activeReFlyRecordingId: "booster",
                victimIsParentOfActiveReFly: true));
        }

        #endregion

        #region IsStaleLiveAnchor

        [Fact]
        public void IsStaleLiveAnchor_DeltaBelowThreshold_ReturnsFalse()
        {
            bool stale = RelativeAnchorResolution.IsStaleLiveAnchor(
                new Vector3d(0, 0, 0),
                new Vector3d(100, 0, 0),
                thresholdMeters: 250.0,
                out double delta);

            Assert.False(stale);
            Assert.Equal(100.0, delta, 3);
        }

        [Fact]
        public void IsStaleLiveAnchor_DeltaAtThreshold_ReturnsFalse()
        {
            // Boundary semantics: equal does NOT trip the gate. Threshold is
            // strict-greater-than so a tiny float-precision drift right at
            // the limit doesn't false-positive.
            bool stale = RelativeAnchorResolution.IsStaleLiveAnchor(
                new Vector3d(0, 0, 0),
                new Vector3d(250, 0, 0),
                thresholdMeters: 250.0,
                out double delta);

            Assert.False(stale);
            Assert.Equal(250.0, delta, 3);
        }

        [Fact]
        public void IsStaleLiveAnchor_DeltaAboveThreshold_ReturnsTrue()
        {
            // The watch-jump bug case: anchor is in stable orbit ~818 km
            // from where the recording wanted it. Comfortably above 250 m.
            bool stale = RelativeAnchorResolution.IsStaleLiveAnchor(
                new Vector3d(0, 0, 0),
                new Vector3d(818000, 0, 0),
                thresholdMeters: 250.0,
                out double delta);

            Assert.True(stale);
            Assert.Equal(818000.0, delta, 1);
        }

        [Fact]
        public void IsStaleLiveAnchor_NaNDelta_ReturnsFalse()
        {
            // NaN propagates from any-coord NaN through magnitude. A
            // misbehaving anchor (uninitialized vessel, divide-by-zero
            // somewhere upstream) must NOT trip the staleness gate — that
            // would silently swap to recorded for legitimate live-anchor
            // playback. Fall through to existing Live/Recorded selector.
            bool stale = RelativeAnchorResolution.IsStaleLiveAnchor(
                new Vector3d(double.NaN, 0, 0),
                new Vector3d(0, 0, 0),
                thresholdMeters: 250.0,
                out double delta);

            Assert.False(stale);
            Assert.True(double.IsNaN(delta));
        }

        [Fact]
        public void IsStaleLiveAnchor_InfinityDelta_ReturnsFalse()
        {
            // Same defensive contract as NaN: a vessel pose that ended up at
            // infinity (e.g. expired Krakensbane state) must not be treated
            // as a staleness signal — fall through.
            bool stale = RelativeAnchorResolution.IsStaleLiveAnchor(
                new Vector3d(double.PositiveInfinity, 0, 0),
                new Vector3d(0, 0, 0),
                thresholdMeters: 250.0,
                out double delta);

            Assert.False(stale);
            Assert.True(double.IsInfinity(delta));
        }

        [Fact]
        public void IsStaleLiveAnchor_ZeroDelta_ReturnsFalse()
        {
            // Identical poses (matching live anchor case): never stale.
            bool stale = RelativeAnchorResolution.IsStaleLiveAnchor(
                new Vector3d(123, 456, 789),
                new Vector3d(123, 456, 789),
                thresholdMeters: 250.0,
                out double delta);

            Assert.False(stale);
            Assert.Equal(0.0, delta, 6);
        }

        #endregion

        #region SelectAnchorFrameSource

        [Fact]
        public void SelectAnchorFrameSource_ActiveReFlyBypassUsesRecordedEvenWhenLiveAnchorExists()
        {
            var source = RelativeAnchorResolution.SelectAnchorFrameSource(
                liveAnchorAvailable: true,
                bypassLiveAnchorForActiveReFly: true,
                recordedAnchorAvailable: true,
                recordedFallbackAvailable: false);

            Assert.Equal(RelativeAnchorResolution.AnchorFrameSource.Recorded, source);
        }

        [Fact]
        public void SelectAnchorFrameSource_ActiveReFlyBypassUsesRecordedFallbackForPartialCoverage()
        {
            var source = RelativeAnchorResolution.SelectAnchorFrameSource(
                liveAnchorAvailable: true,
                bypassLiveAnchorForActiveReFly: true,
                recordedAnchorAvailable: false,
                recordedFallbackAvailable: true);

            Assert.Equal(RelativeAnchorResolution.AnchorFrameSource.RecordedFallback, source);
        }

        [Fact]
        public void SelectAnchorFrameSource_ActiveReFlyBypassRetiresWhenNoRecordedPoseExists()
        {
            var source = RelativeAnchorResolution.SelectAnchorFrameSource(
                liveAnchorAvailable: true,
                bypassLiveAnchorForActiveReFly: true,
                recordedAnchorAvailable: false,
                recordedFallbackAvailable: false);

            Assert.Equal(RelativeAnchorResolution.AnchorFrameSource.Retired, source);
        }

        [Fact]
        public void SelectAnchorFrameSource_UnrelatedAnchorKeepsLiveAndDoesNotUseFallbackOnly()
        {
            var live = RelativeAnchorResolution.SelectAnchorFrameSource(
                liveAnchorAvailable: true,
                bypassLiveAnchorForActiveReFly: false,
                recordedAnchorAvailable: false,
                recordedFallbackAvailable: true);
            var missingLive = RelativeAnchorResolution.SelectAnchorFrameSource(
                liveAnchorAvailable: false,
                bypassLiveAnchorForActiveReFly: false,
                recordedAnchorAvailable: false,
                recordedFallbackAvailable: true);

            Assert.Equal(RelativeAnchorResolution.AnchorFrameSource.Live, live);
            Assert.Equal(RelativeAnchorResolution.AnchorFrameSource.Retired, missingLive);
        }

        #endregion

        #region RecordedAnchorPointListCoversUT

        [Fact]
        public void RecordedAnchorPointListCoversUT_TargetInside_ReturnsTrue()
        {
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 },
                new TrajectoryPoint { ut = 110.0 }
            };

            Assert.True(ParsekFlight.RecordedAnchorPointListCoversUT(points, 105.0));
        }

        [Fact]
        public void RecordedAnchorPointListCoversUT_TargetBeforeStart_ReturnsFalse()
        {
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 },
                new TrajectoryPoint { ut = 110.0 }
            };

            Assert.False(ParsekFlight.RecordedAnchorPointListCoversUT(points, 99.0));
        }

        [Fact]
        public void RecordedAnchorPointListCoversUT_TargetAfterEnd_ReturnsFalse()
        {
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 },
                new TrajectoryPoint { ut = 110.0 }
            };

            Assert.False(ParsekFlight.RecordedAnchorPointListCoversUT(points, 111.0));
        }

        [Fact]
        public void RecordedAnchorPointListCoversUT_SinglePointRequiresSameUT()
        {
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 }
            };

            Assert.True(ParsekFlight.RecordedAnchorPointListCoversUT(points, 100.0));
            Assert.False(ParsekFlight.RecordedAnchorPointListCoversUT(points, 100.1));
        }

        [Fact]
        public void RecordedAnchorPointListCoversUT_NullOrEmpty_ReturnsFalse()
        {
            Assert.False(ParsekFlight.RecordedAnchorPointListCoversUT(null, 100.0));
            Assert.False(ParsekFlight.RecordedAnchorPointListCoversUT(
                new List<TrajectoryPoint>(), 100.0));
        }

        [Fact]
        public void DistanceOutsideRecordedAnchorCoverage_ReportsEndpointFallbackGap()
        {
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 },
                new TrajectoryPoint { ut = 110.0 }
            };

            Assert.Equal(0.0, ParsekFlight.DistanceOutsideRecordedAnchorCoverage(points, 105.0));
            Assert.Equal(2.0, ParsekFlight.DistanceOutsideRecordedAnchorCoverage(points, 98.0));
            Assert.Equal(3.0, ParsekFlight.DistanceOutsideRecordedAnchorCoverage(points, 113.0));
        }

        [Fact]
        public void TryFindAbsoluteShadowBridgeFrame_UsesPriorAbsoluteSectionBoundary()
        {
            var absoluteSection = new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 90.0,
                endUT = 100.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 95.0, latitude = 1.0 },
                    new TrajectoryPoint { ut = 99.5, latitude = 2.0 },
                }
            };
            var relativeSection = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 110.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 101.0 },
                    new TrajectoryPoint { ut = 105.0 },
                },
                absoluteFrames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 101.0, latitude = 3.0 },
                    new TrajectoryPoint { ut = 105.0, latitude = 4.0 },
                }
            };
            var rec = new Recording
            {
                TrackSections = new List<TrackSection> { absoluteSection, relativeSection }
            };

            TrajectoryPoint bridge;
            Assert.True(ParsekFlight.TryFindAbsoluteShadowBridgeFrame(
                rec, relativeSection, 100.2, out bridge));
            Assert.Equal(99.5, bridge.ut);
            Assert.Equal(2.0, bridge.latitude);
        }

        [Fact]
        public void ResolveAbsoluteShadowPlaybackFrames_BridgesForwardFromAdjacentRelativeSection()
        {
            var firstRelative = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 100.5,
                anchorVesselId = 42u,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0 },
                },
                absoluteFrames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, latitude = 1.0 },
                }
            };
            var adjacentRelative = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.5,
                endUT = 110.0,
                anchorVesselId = 42u,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0 },
                    new TrajectoryPoint { ut = 103.0 },
                },
                absoluteFrames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, latitude = 1.0 },
                    new TrajectoryPoint { ut = 103.0, latitude = 3.0 },
                }
            };
            var rec = new Recording
            {
                RecordingId = "shadow-forward",
                TrackSections = new List<TrackSection> { firstRelative, adjacentRelative }
            };

            List<TrajectoryPoint> resolved =
                ParsekFlight.ResolveAbsoluteShadowPlaybackFrames(rec, firstRelative, 100.4);

            Assert.NotSame(firstRelative.absoluteFrames, resolved);
            Assert.Equal(2, resolved.Count);
            Assert.Equal(100.0, resolved[0].ut);
            Assert.Equal(103.0, resolved[1].ut);
            Assert.Equal(3.0, resolved[1].latitude);
        }

        [Fact]
        public void TryFindAbsoluteShadowForwardBridgeFrame_SkipsMismatchedAnchorVesselId()
        {
            var firstRelative = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 100.5,
                anchorVesselId = 42u,
                absoluteFrames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, latitude = 1.0 },
                }
            };
            var otherAnchorRelative = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.5,
                endUT = 110.0,
                anchorVesselId = 99u,
                absoluteFrames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 103.0, latitude = 3.0 },
                }
            };
            var rec = new Recording
            {
                RecordingId = "shadow-forward-mismatch",
                TrackSections = new List<TrackSection> { firstRelative, otherAnchorRelative }
            };

            TrajectoryPoint bridge;
            Assert.False(ParsekFlight.TryFindAbsoluteShadowForwardBridgeFrame(
                rec, firstRelative, 100.4, out bridge));
        }

        [Fact]
        public void ShouldWarnRecordedAnchorFallbackGap_OnlyWarnsForLargeFiniteGap()
        {
            Assert.False(ParsekFlight.ShouldWarnRecordedAnchorFallbackGap(5.0));
            Assert.True(ParsekFlight.ShouldWarnRecordedAnchorFallbackGap(5.01));
            Assert.False(ParsekFlight.ShouldWarnRecordedAnchorFallbackGap(double.NaN));
            Assert.False(ParsekFlight.ShouldWarnRecordedAnchorFallbackGap(double.PositiveInfinity));
        }

        [Fact]
        public void BuildRecordedAnchorFallbackGapLog_IncludesGapAndRecordingContext()
        {
            string line = ParsekFlight.BuildRecordedAnchorFallbackGapLog(
                anchorRecordingId: "854fdf77",
                victimRecordingId: "e77d90b6",
                anchorVesselId: 3314061462u,
                targetUT: 178.0,
                fallbackGapSeconds: 12.345);

            Assert.Contains("recorded-anchor-fallback-gap", line);
            Assert.Contains("anchorRec=854fdf77", line);
            Assert.Contains("victimRec=e77d90b6", line);
            Assert.Contains("anchorPid=3314061462", line);
            Assert.Contains("targetUT=178.00", line);
            Assert.Contains("gap=12.35s", line);
        }

        [Fact]
        public void PreReFlyAnchorTrajectory_CaptureCopiesMutableTrajectoryLists()
        {
            var rec = new Recording
            {
                RecordingId = "booster",
                VesselPersistentId = 2820240741u,
                VesselName = "Kerbal X Probe",
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0 },
                    new TrajectoryPoint { ut = 110.0 },
                },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        startUT = 100.0,
                        endUT = 110.0,
                        referenceFrame = ReferenceFrame.Absolute,
                        frames = new List<TrajectoryPoint>
                        {
                            new TrajectoryPoint { ut = 100.0 },
                        },
                    },
                },
            };

            rec.CapturePreReFlyAnchorTrajectory("sess_1");
            rec.Points.Clear();
            rec.TrackSections[0].frames.Clear();

            var frozen = rec.BuildPreReFlyAnchorTrajectoryRecording("sess_1");

            Assert.NotNull(frozen);
            Assert.Equal("booster", frozen.RecordingId);
            Assert.Equal(2820240741u, frozen.VesselPersistentId);
            Assert.Equal(2, frozen.Points.Count);
            Assert.Single(frozen.TrackSections);
            Assert.Single(frozen.TrackSections[0].frames);
        }

        [Fact]
        public void ShouldUsePreReFlyAnchorTrajectory_RequiresActiveInPlaceRecording()
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_1",
                ActiveReFlyRecordingId = "booster",
                OriginChildRecordingId = "booster",
            };
            var rec = new Recording
            {
                RecordingId = "booster",
                VesselPersistentId = 2820240741u,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0 },
                },
            };
            rec.CapturePreReFlyAnchorTrajectory("sess_1");

            Assert.True(ParsekFlight.ShouldUsePreReFlyAnchorTrajectory(
                rec, 2820240741u, "upper-stage", marker));
            Assert.False(ParsekFlight.ShouldUsePreReFlyAnchorTrajectory(
                rec, 2820240741u, "booster", marker));
            Assert.False(ParsekFlight.ShouldUsePreReFlyAnchorTrajectory(
                rec, 999u, "upper-stage", marker));

            marker.ActiveReFlyRecordingId = "replacement";
            Assert.False(ParsekFlight.ShouldUsePreReFlyAnchorTrajectory(
                rec, 2820240741u, "upper-stage", marker));
        }

        [Fact]
        public void ShouldSkipMutableActiveReFlyAnchorCandidate_OnlyDuringBypass()
        {
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "booster",
            };
            var active = new Recording { RecordingId = "booster" };
            var unrelated = new Recording { RecordingId = "upper-stage" };

            Assert.True(ParsekFlight.ShouldSkipMutableActiveReFlyAnchorCandidate(
                active, marker, bypassLiveAnchorForActiveReFly: true));
            Assert.False(ParsekFlight.ShouldSkipMutableActiveReFlyAnchorCandidate(
                active, marker, bypassLiveAnchorForActiveReFly: false));
            Assert.False(ParsekFlight.ShouldSkipMutableActiveReFlyAnchorCandidate(
                unrelated, marker, bypassLiveAnchorForActiveReFly: true));
        }

        #endregion

        #region ReFly Log Builders

        [Fact]
        public void BuildRelativeOffsetAppliedLog_ReportsRecordedOrLiveSource()
        {
            string recorded = ParsekFlight.BuildRelativeOffsetAppliedLog(
                RecordingStore.CurrentRecordingFormatVersion,
                dx: 1.0,
                dy: 2.0,
                dz: 3.0,
                anchorVesselId: 698412738u,
                anchorFromRecordedTrajectory: true);
            string live = ParsekFlight.BuildRelativeOffsetAppliedLog(
                RecordingStore.CurrentRecordingFormatVersion,
                dx: 1.0,
                dy: 2.0,
                dz: 3.0,
                anchorVesselId: 698412738u,
                anchorFromRecordedTrajectory: false);

            Assert.Contains("source=recorded", recorded);
            Assert.Contains("source=live", live);
            Assert.Contains("anchor=698412738", recorded);
        }

        [Fact]
        public void BuildDeadOnArrivalControlledChildSkipLog_ReportsSnapshotPresence()
        {
            string withSnapshot = ParsekFlight.BuildDeadOnArrivalControlledChildSkipLog(
                pid: 42u,
                hasPreCapturedSnapshot: true);
            string withoutSnapshot = ParsekFlight.BuildDeadOnArrivalControlledChildSkipLog(
                pid: 42u,
                hasPreCapturedSnapshot: false);

            Assert.Contains("pid=42", withSnapshot);
            Assert.Contains("preCapturedSnapshot=True", withSnapshot);
            Assert.Contains("preCapturedSnapshot=False", withoutSnapshot);
            Assert.Contains("Unknown", withSnapshot);
        }

        #endregion

        #region DedupeKey

        [Fact]
        public void DedupeKey_SameRecordingSameAnchor_ProducesSameKey()
        {
            long a = RelativeAnchorResolution.DedupeKey(9, 3151978247u);
            long b = RelativeAnchorResolution.DedupeKey(9, 3151978247u);
            Assert.Equal(a, b);
        }

        [Fact]
        public void DedupeKey_DifferentRecording_ProducesDifferentKey()
        {
            // Two recordings sharing the same anchor pid must dedupe
            // independently -- both should log on first encounter.
            long a = RelativeAnchorResolution.DedupeKey(9, 3151978247u);
            long b = RelativeAnchorResolution.DedupeKey(10, 3151978247u);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void DedupeKey_DifferentAnchor_ProducesDifferentKey()
        {
            long a = RelativeAnchorResolution.DedupeKey(9, 3151978247u);
            long b = RelativeAnchorResolution.DedupeKey(9, 2708531065u);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void DedupeKey_HandlesMaxRecordingIndexAndPid()
        {
            // Verifies no overflow / sign-extension when pid has the high
            // bit set (uint pids commonly use the 31st bit) and the index
            // approaches int.MaxValue.
            long a = RelativeAnchorResolution.DedupeKey(int.MaxValue, uint.MaxValue);
            long b = RelativeAnchorResolution.DedupeKey(0, uint.MaxValue);
            long c = RelativeAnchorResolution.DedupeKey(int.MaxValue, 0u);
            Assert.NotEqual(a, b);
            Assert.NotEqual(a, c);
            Assert.NotEqual(b, c);
        }

        #endregion

        #region FormatRetiredMessage

        [Fact]
        public void FormatRetiredMessage_IncludesGreppableKeyword()
        {
            string msg = RelativeAnchorResolution.FormatRetiredMessage(
                recordingIndex: 9,
                vesselName: "Kerbal X",
                anchorPid: 3151978247u,
                callsite: "InterpolateAndPositionRelative");

            Assert.Contains("relative-anchor-retired", msg);
        }

        [Fact]
        public void FormatRetiredMessage_IncludesIdentifyingFields()
        {
            string msg = RelativeAnchorResolution.FormatRetiredMessage(
                recordingIndex: 9,
                vesselName: "Kerbal X",
                anchorPid: 3151978247u,
                callsite: "InterpolateAndPositionRelative");

            Assert.Contains("recording=#9", msg);
            Assert.Contains("vessel=\"Kerbal X\"", msg);
            Assert.Contains("anchorPid=3151978247", msg);
            Assert.Contains("callsite=InterpolateAndPositionRelative", msg);
        }

        [Fact]
        public void FormatRetiredMessage_NullVesselName_RendersUnknownPlaceholder()
        {
            string msg = RelativeAnchorResolution.FormatRetiredMessage(
                recordingIndex: 0,
                vesselName: null,
                anchorPid: 1u,
                callsite: "PositionGhostRelativeAt");

            Assert.Contains("vessel=\"(unknown)\"", msg);
        }

        [Fact]
        public void FormatRetiredMessage_MentionsRewindRootCause()
        {
            // Players reading KSP.log should be told (gently) that a Re-Fly
            // rewind is the typical root cause -- this anchors the message
            // to the bug B narrative and helps support triage.
            string msg = RelativeAnchorResolution.FormatRetiredMessage(
                recordingIndex: 9,
                vesselName: "Kerbal X",
                anchorPid: 3151978247u,
                callsite: "InterpolateAndPositionRelative");

            Assert.Contains("Re-Fly rewind", msg);
        }

        #endregion

        #region Log-assertion: scenario coverage

        [Fact]
        public void RetiredMessage_LoggedToParsekLog_AppearsInWarnSink()
        {
            // Mirrors the production call site: emit FormatRetiredMessage
            // through ParsekLog.Warn under the [Anchor] tag, verify the
            // resulting line carries the greppable keyword and the [WARN]
            // level. RewindLoggingTests follows the same harness pattern.
            string formatted = RelativeAnchorResolution.FormatRetiredMessage(
                recordingIndex: 9,
                vesselName: "Kerbal X",
                anchorPid: 3151978247u,
                callsite: "InterpolateAndPositionRelative");
            ParsekLog.Warn("Anchor", formatted);

            Assert.Contains(logLines,
                l => l.Contains("[WARN]") && l.Contains("[Anchor]") && l.Contains("relative-anchor-retired"));
        }

        [Fact]
        public void Decide_RewindScenario_AnchorAliveStillResolves()
        {
            // Negative test: after the rewind, the recording's anchor vessel
            // is alive in the post-rewind FlightGlobals (e.g. a station that
            // pre-existed the rewind point). Decide must return Resolved so
            // playback proceeds normally and no spurious retirement WARN is
            // emitted.
            var liveVessels = new HashSet<uint> { 100u, 200u, 42u };
            var outcome = RelativeAnchorResolution.Decide(
                anchorPid: 42u,
                resolver: pid => liveVessels.Contains(pid));

            Assert.Equal(RelativeAnchorResolution.Outcome.Resolved, outcome);
        }

        [Fact]
        public void Decide_RewindScenario_AnchorErasedReturnsRetired_NoFreezePath()
        {
            // Repro for bug B: the anchor pid was destroyed in a Re-Fly
            // rewind so it's not in the live FlightGlobals. The decision is
            // Retired, which the production code translates into
            // SetActive(false). Frozen-at-origin is unreachable: the only
            // two outcomes are Resolved (positioned by anchor) or Retired
            // (hidden).
            var liveVesselsPostRewind = new HashSet<uint> { 100u, 200u };
            var outcome = RelativeAnchorResolution.Decide(
                anchorPid: 3151978247u,
                resolver: pid => liveVesselsPostRewind.Contains(pid));

            Assert.Equal(RelativeAnchorResolution.Outcome.Retired, outcome);
        }

        [Fact]
        public void OutcomeEnum_HasOnlyTwoStates()
        {
            // Defensive: any third outcome would represent a partially
            // positioned ghost (the bug B failure mode). Lock the contract
            // to exactly two values.
            var values = Enum.GetValues(typeof(RelativeAnchorResolution.Outcome));
            Assert.Equal(2, values.Length);
            Assert.Contains(RelativeAnchorResolution.Outcome.Resolved,
                (RelativeAnchorResolution.Outcome[])values);
            Assert.Contains(RelativeAnchorResolution.Outcome.Retired,
                (RelativeAnchorResolution.Outcome[])values);
        }

        #endregion

        #region ShouldSkipPostPositionPipeline (PR #594 P1 gate)

        [Fact]
        public void ShouldSkipPostPositionPipeline_FlagFalse_ReturnsFalse()
        {
            // Steady-state path: the relative positioner did not retire the
            // ghost this frame, so the engine must run the full post-position
            // pipeline (ApplyFrameVisuals, ActivateGhostVisualsIfNeeded,
            // TrackGhostAppearance). Inverting the gate here would silently
            // suppress every visible ghost.
            Assert.False(RelativeAnchorResolution.ShouldSkipPostPositionPipeline(false));
        }

        [Fact]
        public void ShouldSkipPostPositionPipeline_FlagTrue_ReturnsTrue()
        {
            // Bug #613 retire path: the relative positioner hit the
            // unresolvable-anchor branch and set state.anchorRetiredThisFrame.
            // The engine must skip the post-position pipeline so the
            // SetActive(false) call from the positioner is not undone the
            // same frame by ActivateGhostVisualsIfNeeded.
            Assert.True(RelativeAnchorResolution.ShouldSkipPostPositionPipeline(true));
        }

        [Fact]
        public void ShouldSkipPostPositionPipeline_IsPureFunction_NoSideEffects()
        {
            // Pin the predicate's purity: repeated calls with the same input
            // must return the same value, with no static-state mutation.
            Assert.False(RelativeAnchorResolution.ShouldSkipPostPositionPipeline(false));
            Assert.False(RelativeAnchorResolution.ShouldSkipPostPositionPipeline(false));
            Assert.True(RelativeAnchorResolution.ShouldSkipPostPositionPipeline(true));
            Assert.True(RelativeAnchorResolution.ShouldSkipPostPositionPipeline(true));
            Assert.False(RelativeAnchorResolution.ShouldSkipPostPositionPipeline(false));
            // No log line should have been emitted by the predicate itself --
            // logging is the production caller's responsibility, not the
            // pure-function gate's.
            Assert.DoesNotContain(logLines,
                l => l.Contains("ShouldSkipPostPositionPipeline"));
        }

        #endregion

        #region anchorRetiredThisFrame state-flag wiring

        [Fact]
        public void GhostPlaybackState_AnchorRetiredThisFrame_DefaultsToFalse()
        {
            // Fresh GhostPlaybackState must have the retire flag clear so the
            // engine's first frame for a new ghost runs the full pipeline.
            // ClearLoadedVisualReferences must also reset the flag --
            // previous-frame leakage would suppress the next visible frame.
            var state = new GhostPlaybackState();
            Assert.False(state.anchorRetiredThisFrame);

            state.anchorRetiredThisFrame = true;
            state.ClearLoadedVisualReferences();
            Assert.False(state.anchorRetiredThisFrame);
        }

        [Fact]
        public void RetireBranch_LogsOnceAndGatesPipeline_SimulatedIntegration()
        {
            // Mirrors the production retire branch contract end-to-end without
            // touching Unity:
            //
            //   1. ParsekFlight.InterpolateAndPositionRelative / PositionLoopGhost
            //      ask RelativeAnchorResolution.Decide whether the recorded
            //      anchor pid is live.
            //   2. On Outcome.Retired, the production code logs a one-shot
            //      WARN under the [Anchor] tag (deduped via DedupeKey) and
            //      sets state.anchorRetiredThisFrame = true.
            //   3. The engine then asks ShouldSkipPostPositionPipeline --
            //      which must return true so the SetActive(false) sticks
            //      through the frame.
            //
            // Failure modes this test catches:
            //   - flag set but predicate returns false (engine runs activation
            //     pipeline anyway, ghost re-appears at (0,0,0)).
            //   - flag never set (pre-fix regression: previous freeze-in-place
            //     branch would leave the ghost frozen at world origin).
            //   - missing/wrong WARN log line (regresses the existing one-shot
            //     dedupe contract, breaks player support triage).
            var liveVessels = new HashSet<uint> { 100u, 200u };
            var loggedKeys = new HashSet<long>();
            var state = new GhostPlaybackState();

            // Frame 1: positioner runs the retire branch.
            var outcome = RelativeAnchorResolution.Decide(
                anchorPid: 3151978247u,
                resolver: pid => liveVessels.Contains(pid));
            Assert.Equal(RelativeAnchorResolution.Outcome.Retired, outcome);

            long key = RelativeAnchorResolution.DedupeKey(9, 3151978247u);
            if (loggedKeys.Add(key))
            {
                ParsekLog.Warn("Anchor",
                    RelativeAnchorResolution.FormatRetiredMessage(
                        recordingIndex: 9,
                        vesselName: "Kerbal X",
                        anchorPid: 3151978247u,
                        callsite: "InterpolateAndPositionRelative"));
            }
            state.anchorRetiredThisFrame = true;

            // Engine: gate the post-position pipeline.
            Assert.True(RelativeAnchorResolution.ShouldSkipPostPositionPipeline(
                state.anchorRetiredThisFrame));

            // One-shot WARN must have fired exactly once.
            Assert.Equal(1,
                logLines.Count(l => l.Contains("[WARN]")
                    && l.Contains("[Anchor]")
                    && l.Contains("relative-anchor-retired")));

            // Frame 2: engine clears the flag at the top of the next render
            // pass, retire branch runs again on the same (recording, anchor)
            // -- WARN must NOT re-fire, but flag must be re-armed.
            state.anchorRetiredThisFrame = false;
            outcome = RelativeAnchorResolution.Decide(
                anchorPid: 3151978247u,
                resolver: pid => liveVessels.Contains(pid));
            Assert.Equal(RelativeAnchorResolution.Outcome.Retired, outcome);
            if (loggedKeys.Add(key))
                ParsekLog.Warn("Anchor", "should not be reachable on second hit");
            state.anchorRetiredThisFrame = true;
            Assert.True(RelativeAnchorResolution.ShouldSkipPostPositionPipeline(
                state.anchorRetiredThisFrame));

            // Still exactly one WARN line.
            Assert.Equal(1,
                logLines.Count(l => l.Contains("[WARN]")
                    && l.Contains("[Anchor]")
                    && l.Contains("relative-anchor-retired")));

            // Frame 3: anchor reappears -- Decide returns Resolved, the
            // retire flag is NOT set, and the gate must let the engine run
            // the full pipeline so the ghost can become visible again.
            liveVessels.Add(3151978247u);
            state.anchorRetiredThisFrame = false;
            outcome = RelativeAnchorResolution.Decide(
                anchorPid: 3151978247u,
                resolver: pid => liveVessels.Contains(pid));
            Assert.Equal(RelativeAnchorResolution.Outcome.Resolved, outcome);
            Assert.False(RelativeAnchorResolution.ShouldSkipPostPositionPipeline(
                state.anchorRetiredThisFrame));
        }

        #endregion
    }
}
