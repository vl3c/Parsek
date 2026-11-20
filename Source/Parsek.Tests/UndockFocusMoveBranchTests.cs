using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo UNDOCK-FOCUS-MOVE-RECORDS-UNDER-WRONG-VESSEL: a crewed tug undocks from a probe-less
    /// depot that kept the root part, and stock moves focus to the tug. The depot half is
    /// debris, so DeferredUndockBranch used to resume the split recorder ("undocked vessel is
    /// debris"); ResumeAfterFalseAlarm rebinds to the ACTIVE vessel (the tug) while the tree
    /// recording keeps the depot's pid, so the tug's flight was written into the depot's
    /// recording. The fix branches instead: each half is recorded under its own vessel.
    /// </summary>
    public class UndockFocusMoveBranchTests
    {
        [Fact]
        public void FocusMovedToTheDepartingHalf_RecordedHalfIsDebris_Branches()
        {
            Assert.True(ParsekFlight.ShouldBranchUndockWhenFocusLeftTheRecordedDebrisHalf(
                recordedPid: 100u, backgroundPid: 100u, activePid: 200u, newVesselPid: 200u));
        }

        [Fact]
        public void FocusStayedOnTheRecordedVessel_DebrisLeaves_Resumes()
        {
            // The ordinary debris undock: the background is the new vessel, focus stays.
            Assert.False(ParsekFlight.ShouldBranchUndockWhenFocusLeftTheRecordedDebrisHalf(
                recordedPid: 100u, backgroundPid: 200u, activePid: 100u, newVesselPid: 200u));
        }

        [Fact]
        public void AmbiguousFocus_OrUnknownRecordedVessel_Resumes()
        {
            // Focus on neither side: the new vessel is backgrounded and the old one resumes.
            Assert.False(ParsekFlight.ShouldBranchUndockWhenFocusLeftTheRecordedDebrisHalf(
                recordedPid: 100u, backgroundPid: 200u, activePid: 300u, newVesselPid: 200u));
            Assert.False(ParsekFlight.ShouldBranchUndockWhenFocusLeftTheRecordedDebrisHalf(
                recordedPid: 0u, backgroundPid: 0u, activePid: 200u, newVesselPid: 200u));
        }

        [Fact]
        public void DeferredUndockBranch_DebrisResumeIsGuardedByTheFocusDecision()
        {
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(Path.Combine(
                ParsekSourceRoot(), "Controllers", "ParsekFlight.SplitBranching.cs")));
            int sig = prepared.IndexOf("IEnumerator DeferredUndockBranch(", StringComparison.Ordinal);
            Assert.True(sig >= 0, "DeferredUndockBranch not found");
            string body = SourceScanText.BraceMatchedBlock(prepared, prepared.IndexOf('{', sig));

            int decide = body.IndexOf("ShouldBranchUndockWhenFocusLeftTheRecordedDebrisHalf(", StringComparison.Ordinal);
            int debrisResume = body.IndexOf("ResumeSplitRecorder(pendingSplitRecorder, \"", body.IndexOf("ShouldRecordAsOwnBranch(", StringComparison.Ordinal), StringComparison.Ordinal);
            int branch = body.IndexOf("CreateSplitBranch(", StringComparison.Ordinal);
            Assert.True(decide >= 0 && debrisResume > decide && branch > debrisResume,
                "DeferredUndockBranch must read the focus decision before the debris resume, and branch after it");
        }

        private static string ParsekSourceRoot()
        {
            string repo = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            return Path.Combine(repo, "Source", "Parsek");
        }
    }
}
