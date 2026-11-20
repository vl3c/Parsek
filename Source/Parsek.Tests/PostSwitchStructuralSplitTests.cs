using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using Decision = Parsek.ParsekFlightPostSwitchAutoRecord.PostSwitchStructuralSplitDecision;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo POST-SWITCH-WATCH-SPLIT-NOT-RECORDED (owner ruling 2026-11-18, RULE 2): after a `]`
    /// switch to a vessel Parsek does not record yet, the post-switch watch is armed and no
    /// recorder is bound, so OnVesselsUndocking dropped the undock ("not recording, ignoring").
    /// The watch's later start recorded the remaining vessel only; the separated piece got no
    /// recording and no ghost. The fix captures the separation for the watch, starts the watched
    /// vessel's recording on the next watch frame and feeds the separated vessel to the crash
    /// coalescer as its own child.
    /// </summary>
    public class PostSwitchStructuralSplitTests
    {
        [Fact]
        public void UndockOfTheWatchedVessel_WhileNothingRecords_IsCaptured()
        {
            Assert.Equal(Decision.Capture,
                ParsekFlightPostSwitchAutoRecord.EvaluatePostSwitchStructuralSplit(
                    watchArmed: true, watchedPid: 100u, recorderBound: false, splitInProgress: false,
                    originalPid: 100u, newPid: 200u));
        }

        [Fact]
        public void NoWatch_IsNotCaptured()
        {
            Assert.Equal(Decision.NoWatch,
                ParsekFlightPostSwitchAutoRecord.EvaluatePostSwitchStructuralSplit(
                    watchArmed: false, watchedPid: 0u, recorderBound: false, splitInProgress: false,
                    originalPid: 100u, newPid: 200u));
        }

        [Fact]
        public void BoundRecorderOrParkedSplit_OwnsTheSeparation()
        {
            Assert.Equal(Decision.RecorderBound,
                ParsekFlightPostSwitchAutoRecord.EvaluatePostSwitchStructuralSplit(
                    watchArmed: true, watchedPid: 100u, recorderBound: true, splitInProgress: false,
                    originalPid: 100u, newPid: 200u));
            Assert.Equal(Decision.RecorderBound,
                ParsekFlightPostSwitchAutoRecord.EvaluatePostSwitchStructuralSplit(
                    watchArmed: true, watchedPid: 100u, recorderBound: false, splitInProgress: true,
                    originalPid: 100u, newPid: 200u));
        }

        [Fact]
        public void SeparationOfAnotherVessel_OrDegeneratePids_IsNotCaptured()
        {
            Assert.Equal(Decision.NotWatchedVessel,
                ParsekFlightPostSwitchAutoRecord.EvaluatePostSwitchStructuralSplit(
                    true, 100u, false, false, originalPid: 300u, newPid: 400u));
            Assert.Equal(Decision.NotWatchedVessel,
                ParsekFlightPostSwitchAutoRecord.EvaluatePostSwitchStructuralSplit(
                    true, 100u, false, false, originalPid: 100u, newPid: 100u));
            Assert.Equal(Decision.NotWatchedVessel,
                ParsekFlightPostSwitchAutoRecord.EvaluatePostSwitchStructuralSplit(
                    true, 100u, false, false, originalPid: 100u, newPid: 0u));
        }

        [Fact]
        public void NotRecordingUndock_RoutesToTheWatchBeforeIgnoring()
        {
            string body = MethodBody("ParsekFlightSplitBrancher.cs", "void OnVesselsUndocking(Vessel oldVessel, Vessel newVessel)");
            int capture = body.IndexOf("TryCapturePostSwitchStructuralSplit(", StringComparison.Ordinal);
            int notRecording = body.IndexOf("host.recorder == null || !host.recorder.IsRecording", StringComparison.Ordinal);
            Assert.True(notRecording >= 0 && capture > notRecording,
                "OnVesselsUndocking must offer a not-recording undock to the post-switch watch");
        }

        [Fact]
        public void WatchFrame_StartsTheRecordingThenFeedsTheCapturedSeparations()
        {
            string body = MethodBody("ParsekFlightPostSwitchAutoRecord.cs", "void OnPostSwitchAutoRecordPhysicsFrame(Vessel v)");
            int start = body.IndexOf("PostSwitchAutoRecordTrigger.StructuralSplit, currentUT)", StringComparison.Ordinal);
            int feed = body.IndexOf("FeedPostSwitchStructuralSplits(splits,", StringComparison.Ordinal);
            int baseline = body.IndexOf("if (!state.BaselineCaptured)", StringComparison.Ordinal);
            Assert.True(start >= 0 && feed > start && baseline > feed,
                "a captured separation must start the recording (before the baseline / settle gates) and then be fed");
        }

        // ---- tracked background member: the background recorder owns the split ----

        [Fact]
        public void TrackedBackgroundMember_IsLeftToTheBackgroundRecorder()
        {
            // A watch armed via ArmTrackedBackgroundMember: the BackgroundRecorder's own split
            // check records the child; a capture would record it twice.
            Assert.Equal(Decision.BackgroundRecorderOwns,
                ParsekFlightPostSwitchAutoRecord.EvaluatePostSwitchStructuralSplit(
                    watchArmed: true, watchedPid: 100u, recorderBound: false, splitInProgress: false,
                    originalPid: 100u, newPid: 200u, watchedTrackedInActiveTree: true));
        }

        [Fact]
        public void Capture_ReadsTheTrackedFlagAndTheBackgroundMap()
        {
            string body = MethodBody("ParsekFlightPostSwitchAutoRecord.cs", "bool TryCapturePostSwitchStructuralSplit(");
            Assert.Contains("state.TrackedInActiveTree", body);
            Assert.Contains("BackgroundMap.ContainsKey(state.VesselPid)", body);
        }

        // ---- focus moves to the separated vessel: the captures follow the re-arm ----

        private static ParsekFlight.PostSwitchStructuralSplit Capture(uint watched, uint child)
        {
            return new ParsekFlight.PostSwitchStructuralSplit
            {
                ChildPid = child, HasController = true, Cause = "UNDOCK", UT = 50.0,
                ChildRootPartPid = child + 1u, Source = "OnVesselsUndocking",
                OriginalPid = watched, OriginalHasController = false, OriginalRootPartPid = watched + 1u,
            };
        }

        [Fact]
        public void Rearm_OnTheSeparatedVessel_TurnsTheCaptureAround()
        {
            // Undock, then stock moves focus to the departing tug (200): the watch re-arms on
            // it, and the depot (100) becomes the separated vessel of the tug's recording.
            var carried = ParsekFlightPostSwitchAutoRecord.CarryStructuralSplitsAcrossRearm(
                new List<ParsekFlight.PostSwitchStructuralSplit> { Capture(100u, 200u) }, 100u, 200u);

            Assert.Single(carried);
            Assert.Equal(100u, carried[0].ChildPid);
            Assert.False(carried[0].HasController);
            Assert.Equal(101u, carried[0].ChildRootPartPid);
            Assert.Equal(200u, carried[0].OriginalPid);
            Assert.Equal("UNDOCK", carried[0].Cause);
            Assert.Equal(50.0, carried[0].UT);
        }

        [Fact]
        public void Rearm_OnTheSeparatedVessel_KeepsTheOtherSeparations()
        {
            var carried = ParsekFlightPostSwitchAutoRecord.CarryStructuralSplitsAcrossRearm(
                new List<ParsekFlight.PostSwitchStructuralSplit> { Capture(100u, 200u), Capture(100u, 300u) },
                100u, 200u);

            Assert.Equal(new[] { 100u, 300u }, new[] { carried[0].ChildPid, carried[1].ChildPid });
        }

        [Fact]
        public void Rearm_OnTheSameVessel_KeepsEverything_OnAnUnrelatedVessel_KeepsNothing()
        {
            var captures = new List<ParsekFlight.PostSwitchStructuralSplit> { Capture(100u, 200u) };
            Assert.Equal(200u, Assert.Single(
                ParsekFlightPostSwitchAutoRecord.CarryStructuralSplitsAcrossRearm(captures, 100u, 100u)).ChildPid);
            Assert.Empty(ParsekFlightPostSwitchAutoRecord.CarryStructuralSplitsAcrossRearm(captures, 100u, 999u));
        }

        [Fact]
        public void Arm_CarriesTheCapturesAndWarnsWhenItReplacesAWatchHoldingThem()
        {
            string body = MethodBody("ParsekFlightPostSwitchAutoRecord.cs", "void ArmPostSwitchAutoRecord(");
            int carry = body.IndexOf("CarryStructuralSplitsAcrossRearm(", StringComparison.Ordinal);
            int warn = body.IndexOf("ParsekLog.Warn(", StringComparison.Ordinal);
            int replace = body.IndexOf("host.postSwitchAutoRecord = new", StringComparison.Ordinal);
            int add = body.IndexOf("PendingStructuralSplits.AddRange(carried)", StringComparison.Ordinal);
            Assert.True(carry >= 0 && warn > carry && replace > warn && add > replace,
                "ArmPostSwitchAutoRecord must carry the captures and warn before it replaces the watch");
        }

        private static string MethodBody(string file, string signature)
        {
            string repo = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(
                Path.Combine(repo, "Source", "Parsek", "Controllers", file)));
            int sig = prepared.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(sig >= 0, "signature not found: " + signature);
            return SourceScanText.BraceMatchedBlock(prepared, prepared.IndexOf('{', sig));
        }
    }
}
