using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 2 of fix-refly-relative-anchor: source-text gates that confirm
    /// the ReFlyAnchorSelection bypass is wired into both recorder sites
    /// (active-vessel and background).
    ///
    /// <para>Driving <c>UpdateAnchorDetection</c> / <c>UpdateBackgroundAnchorDetection</c>
    /// from xUnit would require constructing a live KSP Vessel and the
    /// private <c>BackgroundVesselState</c>; the established alternative
    /// (see <c>ChainSaveLoadTests.ChainStateNotPersistedInScenario</c> and
    /// <c>memory/reference_parsek_scenario_xunit.md</c>) is a source-text
    /// gate that catches accidental wiring drift. The helper itself is
    /// exhaustively exercised in <c>ReFlyAnchorSelectionTests</c>; the
    /// end-to-end behavior is validated by the
    /// <c>ReFlyAnchorContract</c> in-game test landed in Phase 4.</para>
    /// </summary>
    public class ReFlyAnchorBypassWiringTests
    {
        private static string ReadSource(string relativePath)
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot, "Source", "Parsek", relativePath);
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return File.ReadAllText(path);
        }

        [Fact]
        public void BackgroundRecorder_WiresReFlyAnchorBypass_AfterDebrisBypass()
        {
            string source = ReadSource("BackgroundRecorder.cs");

            int debrisBypassIdx = source.IndexOf(
                "ApplyDebrisAnchorContractToState(state, treeRec, bgVessel, ut)",
                StringComparison.Ordinal);
            Assert.True(debrisBypassIdx >= 0,
                "Existing debris-parent bypass call site not found");

            int reflyBypassIdx = source.IndexOf(
                "ReFlyAnchorSelection.TryResolveReFlyProvisionalAnchor(",
                StringComparison.Ordinal);
            Assert.True(reflyBypassIdx >= 0,
                "ReFlyAnchorSelection bypass call site missing from BackgroundRecorder.cs");
            Assert.True(reflyBypassIdx > debrisBypassIdx,
                "Re-fly bypass must come after the debris-parent bypass so debris recordings still take precedence");

            // Verify the BG-specific apply helper is defined.
            Assert.Contains(
                "private void ApplyReFlyProvisionalAnchorToState(",
                source);
        }

        [Fact]
        public void FlightRecorder_WiresReFlyAnchorBypass_BeforeBuildCandidateList()
        {
            string source = ReadSource("FlightRecorder.cs");

            int updateAnchorIdx = source.IndexOf(
                "private void UpdateAnchorDetection(Vessel v)",
                StringComparison.Ordinal);
            Assert.True(updateAnchorIdx >= 0,
                "UpdateAnchorDetection method not found");

            int reflyBypassIdx = source.IndexOf(
                "ReFlyAnchorSelection.TryResolveReFlyProvisionalAnchor(",
                updateAnchorIdx,
                StringComparison.Ordinal);
            Assert.True(reflyBypassIdx >= 0,
                "Re-fly bypass call missing from FlightRecorder.UpdateAnchorDetection");

            // The bypass must precede the nearest-search call.
            int candidateBuildIdx = source.IndexOf(
                "BuildRecordingAnchorCandidateList(",
                updateAnchorIdx,
                StringComparison.Ordinal);
            Assert.True(candidateBuildIdx > reflyBypassIdx,
                "Re-fly bypass must execute before BuildRecordingAnchorCandidateList so the bypass actually intercepts the nearest-search");

            // Verify the active-vessel apply helper is defined.
            Assert.Contains(
                "private void ApplyReFlyProvisionalAnchorToActiveRecording(",
                source);
        }

        [Fact]
        public void ReFlyAnchorBypass_UsesReFlyProvisionalSupersedeCandidateSource()
        {
            // The synthetic RecordingAnchorCandidate constructed by the
            // active-vessel apply helper must tag itself with the new
            // diagnostic enum value so log lines and downstream
            // affinity-ranking treat it as a distinct source.
            string flightSource = ReadSource("FlightRecorder.cs");
            Assert.Contains(
                "AnchorCandidateSource.ReFlyProvisionalSupersede",
                flightSource);

            string anchorDetectorSource = ReadSource("AnchorDetector.cs");
            Assert.Contains(
                "ReFlyProvisionalSupersede",
                anchorDetectorSource);
        }

        [Fact]
        public void ReFlyAnchorBypass_PreChecksAnchorPoseBeforeOpeningRelativeSection()
        {
            // PR #889 validation playtest discovered a per-frame thrash when
            // the supersede target's authored trajectory does not cover the
            // current playback UT (nested re-fly where the rewind point
            // predates the prior provisional's startUT). The bypass opened a
            // Relative section, SeedRelativeBoundaryPoint failed with
            // anchor-out-of-recorded-range, ForceExitRelativeToAbsolute
            // closed it, and the next frame's bypass call repeated the
            // cycle. The thrash produced thousands of zero-frame
            // TrackSections (caught by the recorder safeguard so the on-disk
            // recordings stay clean) and thousands of matching INFO log
            // lines.
            //
            // Source-text gate: confirms both recorder bypass apply helpers
            // pre-check the anchor pose with a rate-limited VerboseRateLimited
            // log (key=refly-bypass-anchor-uncovered) before any section
            // flip, anchor write, or HF sampling activation.
            string flightSource = ReadSource("FlightRecorder.cs");
            int flightApplyIdx = flightSource.IndexOf(
                "private void ApplyReFlyProvisionalAnchorToActiveRecording(",
                System.StringComparison.Ordinal);
            Assert.True(flightApplyIdx >= 0, "Active-vessel apply helper not found");
            int flightPreCheckIdx = flightSource.IndexOf(
                "refly-bypass-anchor-uncovered",
                flightApplyIdx,
                System.StringComparison.Ordinal);
            Assert.True(flightPreCheckIdx >= 0,
                "Active-vessel pre-check log (key=refly-bypass-anchor-uncovered) missing");
            // The pre-check must run BEFORE the section flip and HF sampling
            // calls, otherwise the thrash regression returns.
            int flightSamplePositionIdx = flightSource.IndexOf(
                "SamplePosition(v);",
                flightApplyIdx,
                System.StringComparison.Ordinal);
            Assert.True(flightSamplePositionIdx > flightPreCheckIdx,
                "Active-vessel pre-check must come before SamplePosition / section flip");

            string bgSource = ReadSource("BackgroundRecorder.cs");
            int bgApplyIdx = bgSource.IndexOf(
                "private void ApplyReFlyProvisionalAnchorToState(",
                System.StringComparison.Ordinal);
            Assert.True(bgApplyIdx >= 0, "BG apply helper not found");
            int bgPreCheckIdx = bgSource.IndexOf(
                "refly-bypass-anchor-uncovered",
                bgApplyIdx,
                System.StringComparison.Ordinal);
            Assert.True(bgPreCheckIdx >= 0,
                "BG pre-check log (key=refly-bypass-anchor-uncovered) missing");
            int bgStartSectionIdx = bgSource.IndexOf(
                "StartBackgroundTrackSection(state, env, ReferenceFrame.Relative",
                bgApplyIdx,
                System.StringComparison.Ordinal);
            Assert.True(bgStartSectionIdx > bgPreCheckIdx,
                "BG pre-check must come before StartBackgroundTrackSection / section flip");
        }
    }
}
