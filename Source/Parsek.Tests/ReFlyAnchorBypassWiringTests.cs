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
    }
}
