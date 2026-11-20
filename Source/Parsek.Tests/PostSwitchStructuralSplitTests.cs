using System;
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
