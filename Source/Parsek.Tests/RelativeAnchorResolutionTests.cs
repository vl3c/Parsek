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

        #region Recorded relative offset spike

        [Fact]
        public void RecordedRelativeOffsetSpike_CorrectsIsolatedSample()
        {
            var frames = new List<TrajectoryPoint>
            {
                RelativeFrame(150.37670288086715, -0.080, -5.200, 0.030),
                RelativeFrame(150.43670288086710, -1.820, 38.020, -0.030),
                RelativeFrame(150.65670288086750, -0.060, -5.450, 0.030),
            };

            bool corrected = ParsekFlight.TryCorrectIsolatedRecordedRelativeOffsetSpike(
                frames, 1, out Vector3d correctedOffset, out double deviationMeters);

            Assert.True(corrected);
            Assert.InRange(correctedOffset.x, -0.09, -0.05);
            Assert.InRange(correctedOffset.y, -5.30, -5.20);
            Assert.InRange(correctedOffset.z, 0.02, 0.04);
            Assert.True(deviationMeters > 40.0);
        }

        [Fact]
        public void RecordedRelativeOffsetSpike_KeepsSteadyDrift()
        {
            var frames = new List<TrajectoryPoint>
            {
                RelativeFrame(10.0, 0.0, 0.0, 0.0),
                RelativeFrame(10.1, 0.0, 20.0, 0.0),
                RelativeFrame(10.2, 0.0, 40.0, 0.0),
            };

            bool corrected = ParsekFlight.TryCorrectIsolatedRecordedRelativeOffsetSpike(
                frames, 1, out _, out _);

            Assert.False(corrected);
        }

        [Fact]
        public void RecordedRelativeOffsetSpike_KeepsEndpointSamples()
        {
            var frames = new List<TrajectoryPoint>
            {
                RelativeFrame(10.0, 0.0, 0.0, 0.0),
                RelativeFrame(10.1, 0.0, 40.0, 0.0),
                RelativeFrame(10.2, 0.0, 0.0, 0.0),
            };

            Assert.False(ParsekFlight.TryCorrectIsolatedRecordedRelativeOffsetSpike(
                frames, 0, out _, out _));
            Assert.False(ParsekFlight.TryCorrectIsolatedRecordedRelativeOffsetSpike(
                frames, 2, out _, out _));
        }

        private static TrajectoryPoint RelativeFrame(double ut, double dx, double dy, double dz)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = dx,
                longitude = dy,
                altitude = dz,
                bodyName = "Kerbin"
            };
        }

        #endregion

        #region Pre-Re-Fly anchor trajectory

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
        public void BuildPreReFlyAnchorTrajectoryRecording_CachesUntilSnapshotChanges()
        {
            var rec = new Recording
            {
                RecordingId = "booster",
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0 },
                },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        startUT = 100.0,
                        endUT = 100.0,
                        referenceFrame = ReferenceFrame.Absolute,
                        frames = new List<TrajectoryPoint>
                        {
                            new TrajectoryPoint { ut = 100.0 },
                        },
                    },
                },
            };

            rec.CapturePreReFlyAnchorTrajectory("sess_1");
            Recording first = rec.BuildPreReFlyAnchorTrajectoryRecording("sess_1");
            Recording second = rec.BuildPreReFlyAnchorTrajectoryRecording("sess_1");

            Assert.NotNull(first);
            Assert.Same(first, second);

            rec.Points.Clear();
            rec.Points.Add(new TrajectoryPoint { ut = 200.0 });
            rec.TrackSections[0].frames[0] = new TrajectoryPoint { ut = 200.0 };
            rec.CapturePreReFlyAnchorTrajectory("sess_1");
            Recording refreshed = rec.BuildPreReFlyAnchorTrajectoryRecording("sess_1");

            Assert.NotNull(refreshed);
            Assert.NotSame(first, refreshed);
            Assert.Equal(200.0, refreshed.Points[0].ut);

            rec.ClearPreReFlyAnchorTrajectory();
            Assert.Null(rec.BuildPreReFlyAnchorTrajectoryRecording("sess_1"));
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
        public void RetireBranch_ProductionCallSites_SetTheFlagAndDedupeTheWarn_SourceGate()
        {
            // This cell used to "simulate" the retire branch: the test itself added to a
            // dedupe set, called ParsekLog.Warn and assigned anchorRetiredThisFrame, then
            // asserted its own work. None of the three failure modes it names - a branch
            // that logs every frame, one that never dedupes, one that never sets the flag -
            // could red it, because production performed none of the three. The pure
            // decisions (Decide / DedupeKey / FormatRetiredMessage /
            // ShouldSkipPostPositionPipeline) already have their own cells above; what had
            // no coverage is the WIRING at the call sites, and those live inside
            // ParsekFlight positioning methods that need a live GameObject. So this is now a
            // source gate over the three recorded/loop retire branches, read with comments
            // blanked out so a comment describing the branch cannot stand in for it.
            string src = SourceScanText.StripCSharpComments(ReadParsekFlightSource());

            const string flag = "retireSignalState.anchorRetiredThisFrame = true;";
            const string dedupe = "RelativeAnchorResolution.DedupeKey(";
            const string once = "loggedAnchorNotFound.Add(";
            const string message = "RelativeAnchorResolution.FormatRetiredMessage(";

            var dedupeSites = new List<int>();
            for (int i = src.IndexOf(dedupe, StringComparison.Ordinal); i >= 0;
                 i = src.IndexOf(dedupe, i + 1, StringComparison.Ordinal))
            {
                dedupeSites.Add(i);
            }
            Assert.Equal(3, dedupeSites.Count);

            foreach (int site in dedupeSites)
            {
                // The flag is set BEFORE the warn is even considered: the ghost stays hidden
                // for the frame whether or not this (recording, anchor) has already warned.
                string before = src.Substring(Math.Max(0, site - 400), Math.Min(400, site));
                Assert.Contains(flag, before);

                // And the warn goes through the one-shot set, carrying the shared message.
                string after = src.Substring(site, Math.Min(500, src.Length - site));
                Assert.Contains(once, after);
                Assert.Contains(message, after);
                Assert.True(after.IndexOf(once, StringComparison.Ordinal)
                    < after.IndexOf(message, StringComparison.Ordinal),
                    "the warn must be gated by the dedupe set, not logged then deduped");
            }
        }

        private static string ReadParsekFlightSource()
        {
            string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = System.IO.Path.Combine(root, "Source", "Parsek", "Controllers", "ParsekFlight.cs");
            if (!System.IO.File.Exists(path))
                path = System.IO.Path.Combine(root, "Parsek", "Controllers", "ParsekFlight.cs");
            Assert.True(System.IO.File.Exists(path), "Source file not found at " + path);
            return ParsekFlightSourceFiles.ReadAll(System.IO.Path.GetDirectoryName(path));
        }

        #endregion
    }
}
