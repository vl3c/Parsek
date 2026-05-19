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
        public void FlightRecorder_WiresReFlyAnchorBypass_BeforeNearestSearch()
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

            // The bypass must precede the nearest-search call so the bypass
            // actually intercepts the nearest-search.
            //
            // Note: BuildRecordingAnchorCandidateList is intentionally
            // hoisted ABOVE the bypass (above this re-fly bypass site).
            // Its load-bearing side effect (ConsiderReFlyTreeSamplingProximity
            // populating reFlyTreeSamplingProximityMeters) gates the
            // 0-250-500 proximity-tier sampling cadence on the NEXT
            // OnPhysicsFrame. If the bypass early-returned without that
            // scan, in-tree peers within 250m would not unlock Full-tier
            // sampling and atmospheric re-fly sections would Lerp through
            // sparse defaults (3 s max) instead of the dense (~0.05 s)
            // configured-min interval. See bobbing investigation log
            // 2026-05-19_1956_forceabsolute-refly-bobbing.
            int findNearestIdx = source.IndexOf(
                "AnchorDetector.FindNearestRecordingAnchor(",
                updateAnchorIdx,
                StringComparison.Ordinal);
            Assert.True(findNearestIdx >= 0,
                "FindNearestRecordingAnchor call not found in UpdateAnchorDetection");
            Assert.True(findNearestIdx > reflyBypassIdx,
                "Re-fly bypass must execute before FindNearestRecordingAnchor so the bypass actually intercepts the nearest-search");

            // Verify the active-vessel apply helper is defined.
            Assert.Contains(
                "private void ApplyReFlyProvisionalAnchorToActiveRecording(",
                source);
        }

        [Fact]
        public void FlightRecorder_BuildsCandidateList_BeforeReFlyBypassEarlyReturn()
        {
            // Companion test to FlightRecorder_WiresReFlyAnchorBypass_BeforeNearestSearch.
            // BuildRecordingAnchorCandidateList carries a load-bearing side
            // effect: ConsiderReFlyTreeSamplingProximity (called from the
            // Add{Live,External}RecordingAnchorCandidates helpers) populates
            // reFlyTreeSamplingProximityMeters, which feeds the next
            // OnPhysicsFrame's proximity-tier sampling cadence
            // (Full / Half / None at 0-250m / 250-500m / 500m+ ranges,
            // see ReFlyTree{Full,Half}FidelityProximityRangeMeters).
            //
            // If the call sat below the re-fly bypass early-return (the
            // pre-2026-05-19 ordering), an active re-fly provisional that
            // hit the bypass would skip the proximity scan, the tier would
            // resolve to None, and the recorder would fall back to sparse
            // sampling intervals (configuredMax = 3 s). The visible
            // consequence was per-section sample counts of 2 in atmospheric
            // re-fly sections, which produced multi-meter Lerp-vs-physics
            // mismatch ("bobbing") of close-camera ghosts.
            //
            // Pin the hoisted ordering so this regression cannot return
            // silently.
            string source = ReadSource("FlightRecorder.cs");

            int updateAnchorIdx = source.IndexOf(
                "private void UpdateAnchorDetection(Vessel v)",
                StringComparison.Ordinal);
            Assert.True(updateAnchorIdx >= 0,
                "UpdateAnchorDetection method not found");

            int candidateBuildIdx = source.IndexOf(
                "BuildRecordingAnchorCandidateList(",
                updateAnchorIdx,
                StringComparison.Ordinal);
            Assert.True(candidateBuildIdx >= 0,
                "BuildRecordingAnchorCandidateList call missing from UpdateAnchorDetection");

            int reflyBypassIdx = source.IndexOf(
                "ReFlyAnchorSelection.TryResolveReFlyProvisionalAnchor(",
                updateAnchorIdx,
                StringComparison.Ordinal);
            Assert.True(reflyBypassIdx >= 0,
                "Re-fly bypass call missing from UpdateAnchorDetection");

            Assert.True(candidateBuildIdx < reflyBypassIdx,
                "BuildRecordingAnchorCandidateList must execute BEFORE the re-fly bypass " +
                "so the bypass's early-return does not skip the proximity-tier sampling " +
                "side effect (ConsiderReFlyTreeSamplingProximity populating " +
                "reFlyTreeSamplingProximityMeters).");

            // The force-Absolute experimental gate has the same early-return
            // shape as the bypass; the candidate build must come before it
            // too so the gated path doesn't lose the proximity side effect.
            // Anchor on the property read inside the gate (not the bare
            // setting name) so a comment-only reorder above the gate cannot
            // make this assertion pass while the if-block migrates back
            // below the build.
            int forceAbsoluteGateIdx = source.IndexOf(
                "ParsekSettings.Current.forceAbsoluteForReFlyProvisional",
                updateAnchorIdx,
                StringComparison.Ordinal);
            Assert.True(forceAbsoluteGateIdx >= 0,
                "force-absolute-refly gate call not found in UpdateAnchorDetection");
            Assert.True(candidateBuildIdx < forceAbsoluteGateIdx,
                "BuildRecordingAnchorCandidateList must execute BEFORE the force-Absolute gate " +
                "so the gated path keeps the proximity-tier sampling side effect.");
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
