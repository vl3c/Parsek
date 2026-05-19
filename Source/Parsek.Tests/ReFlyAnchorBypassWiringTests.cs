using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Source-text gates for the narrowed-gate re-fly anchor selection wired
    /// into both recorder sites (active-vessel <see cref="FlightRecorder"/>
    /// and background <see cref="BackgroundRecorder"/>).
    ///
    /// <para>History: the original 2300m physics-bubble anchor rule was
    /// extended by PR #889 with a supersede-target bypass
    /// (<c>ReFlyAnchorSelection.TryResolveReFlyProvisionalAnchor</c>) that
    /// pinned the Relative anchor to the supersede target so the
    /// nearest-search would not pick a fast-separating sibling. PR 901
    /// validated that Absolute (no Relative anchor at all) is the right
    /// contract for re-fly forks with no nearby real anchor. This PR
    /// generalizes that finding: the bypass is replaced with a filter
    /// (<c>ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional</c>) that
    /// drops every candidate whose recording is in the same
    /// <see cref="RecordingTree"/> as the provisional, so the nearest-search
    /// sees only out-of-tree real anchors (stations, bases, live vessels
    /// from other lineages). The result: re-fly fork with no nearby real
    /// anchor authors Absolute, re-fly fork mid-docking-approach authors
    /// Relative-against-real-station, loop-anchored re-fly fork authors
    /// Relative-against-live-loop-anchor.</para>
    ///
    /// <para>Driving <c>UpdateAnchorDetection</c> /
    /// <c>UpdateBackgroundAnchorDetection</c> from xUnit would require
    /// constructing a live KSP Vessel and the private
    /// <c>BackgroundVesselState</c>; the established alternative (see
    /// <c>ChainSaveLoadTests.ChainStateNotPersistedInScenario</c> and
    /// <c>memory/reference_parsek_scenario_xunit.md</c>) is a source-text
    /// gate that catches accidental wiring drift. The filter helper itself
    /// is exhaustively exercised by xUnit in
    /// <c>FilterCandidatesForReFlyProvisionalTests</c>.</para>
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
        public void BackgroundRecorder_WiresNarrowedGateFilter_BeforeNearestSearch()
        {
            string source = ReadSource("BackgroundRecorder.cs");

            int filterIdx = source.IndexOf(
                "ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional(",
                StringComparison.Ordinal);
            Assert.True(filterIdx >= 0,
                "Narrowed-gate filter call missing from BackgroundRecorder.cs");

            int findNearestIdx = source.IndexOf(
                "AnchorDetector.FindNearestRecordingAnchor(",
                StringComparison.Ordinal);
            Assert.True(findNearestIdx >= 0,
                "FindNearestRecordingAnchor call missing from BackgroundRecorder.cs");

            Assert.True(filterIdx < findNearestIdx,
                "Narrowed-gate filter must execute BEFORE FindNearestRecordingAnchor " +
                "so the nearest-search only sees out-of-tree candidates");
        }

        [Fact]
        public void BackgroundRecorder_NoLongerCallsSupersedeTargetBypass()
        {
            // The supersede-target bypass call is gone from the gate site.
            // The function itself (TryResolveReFlyProvisionalAnchor) and the
            // ApplyReFlyProvisionalAnchorToState apply helper remain in the
            // file for one release as a rollback path; this test confirms
            // they are not wired into UpdateBackgroundAnchorDetection.
            string source = ReadSource("BackgroundRecorder.cs");

            int updateAnchorIdx = source.IndexOf(
                "UpdateBackgroundAnchorDetection",
                StringComparison.Ordinal);
            Assert.True(updateAnchorIdx >= 0,
                "UpdateBackgroundAnchorDetection not found");

            // Find the next method declaration after UpdateBackgroundAnchorDetection
            // so the search is scoped to that method's body. The bypass call
            // string must not appear inside that scope.
            int nextMethodIdx = source.IndexOf(
                "\n        private ",
                updateAnchorIdx + 1,
                StringComparison.Ordinal);
            if (nextMethodIdx < 0) nextMethodIdx = source.Length;

            string methodBody = source.Substring(
                updateAnchorIdx,
                nextMethodIdx - updateAnchorIdx);

            Assert.DoesNotContain(
                "TryResolveReFlyProvisionalAnchor(",
                methodBody);
        }

        [Fact]
        public void FlightRecorder_WiresNarrowedGateFilter_BeforeNearestSearch()
        {
            string source = ReadSource("FlightRecorder.cs");

            int updateAnchorIdx = source.IndexOf(
                "private void UpdateAnchorDetection(Vessel v)",
                StringComparison.Ordinal);
            Assert.True(updateAnchorIdx >= 0,
                "UpdateAnchorDetection method not found");

            int filterIdx = source.IndexOf(
                "ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional(",
                updateAnchorIdx,
                StringComparison.Ordinal);
            Assert.True(filterIdx >= 0,
                "Narrowed-gate filter call missing from FlightRecorder.UpdateAnchorDetection");

            int findNearestIdx = source.IndexOf(
                "AnchorDetector.FindNearestRecordingAnchor(",
                updateAnchorIdx,
                StringComparison.Ordinal);
            Assert.True(findNearestIdx >= 0,
                "FindNearestRecordingAnchor call not found in UpdateAnchorDetection");

            Assert.True(filterIdx < findNearestIdx,
                "Narrowed-gate filter must execute BEFORE FindNearestRecordingAnchor " +
                "so the nearest-search only sees out-of-tree candidates");
        }

        [Fact]
        public void FlightRecorder_NoLongerCallsSupersedeTargetBypass()
        {
            // Symmetric counterpart to
            // BackgroundRecorder_NoLongerCallsSupersedeTargetBypass: the
            // bypass call is gone from UpdateAnchorDetection.
            string source = ReadSource("FlightRecorder.cs");

            int updateAnchorIdx = source.IndexOf(
                "private void UpdateAnchorDetection(Vessel v)",
                StringComparison.Ordinal);
            Assert.True(updateAnchorIdx >= 0,
                "UpdateAnchorDetection method not found");

            int nextMethodIdx = source.IndexOf(
                "\n        private ",
                updateAnchorIdx + 1,
                StringComparison.Ordinal);
            if (nextMethodIdx < 0) nextMethodIdx = source.Length;

            string methodBody = source.Substring(
                updateAnchorIdx,
                nextMethodIdx - updateAnchorIdx);

            Assert.DoesNotContain(
                "TryResolveReFlyProvisionalAnchor(",
                methodBody);
        }

        [Fact]
        public void FlightRecorder_BuildsCandidateList_BeforeForceAbsoluteGate()
        {
            // BuildRecordingAnchorCandidateList carries a load-bearing side
            // effect: ConsiderReFlyTreeSamplingProximity (called from the
            // Add{Live,External}RecordingAnchorCandidates helpers) populates
            // reFlyTreeSamplingProximityMeters, which feeds the next
            // OnPhysicsFrame's proximity-tier sampling cadence
            // (Full / Half / None at 0-250m / 250-500m / 500m+ ranges,
            // see ReFlyTree{Full,Half}FidelityProximityRangeMeters).
            //
            // The force-Absolute experimental gate (still present as a
            // rollback path) has an early-return shape: if it sat below the
            // candidate build, the gated path would skip the proximity scan
            // and the recorder would fall back to sparse sampling. PR 901
            // commit 768fd6e2 hoisted the build above the gate. This test
            // pins the hoisted ordering so the regression cannot return.
            //
            // Note: with the narrowed-gate change replacing the
            // TryResolveReFlyProvisionalAnchor bypass with the
            // FilterCandidatesForReFlyProvisional filter, the build must
            // also execute before the filter (which inputs the candidates
            // list). That ordering is structural — the filter cannot run on
            // a list that does not exist yet — and is implicitly pinned by
            // the syntactic flow.
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

            // The force-Absolute experimental gate has an early-return
            // shape; the candidate build must come before it. Anchor on the
            // property read inside the gate (not the bare setting name) so
            // a comment-only reorder above the gate cannot make this
            // assertion pass while the if-block migrates back below the
            // build.
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
        public void ReFlyAnchorApplyHelpers_StillDefined_PendingDeletionInFuturePR()
        {
            // The apply helpers (ApplyReFlyProvisionalAnchorToActiveRecording
            // / ApplyReFlyProvisionalAnchorToState) are orphans after the
            // narrowed-gate change: nothing in the recorder calls them. They
            // are retained for one release as a rollback path. This test
            // pins their continued existence so a careless cleanup PR does
            // not delete them prematurely — and conversely, makes the
            // future deletion-PR's removal explicit (delete this test
            // alongside the helpers).
            string flightSource = ReadSource("FlightRecorder.cs");
            Assert.Contains(
                "private void ApplyReFlyProvisionalAnchorToActiveRecording(",
                flightSource);

            string bgSource = ReadSource("BackgroundRecorder.cs");
            Assert.Contains(
                "private void ApplyReFlyProvisionalAnchorToState(",
                bgSource);
        }

        [Fact]
        public void ReFlyAnchorApplyHelpers_PreCheckAnchorPoseBeforeOpeningRelativeSection()
        {
            // PR #889 validation playtest discovered a per-frame thrash when
            // the supersede target's authored trajectory does not cover the
            // current playback UT (nested re-fly where the rewind point
            // predates the prior provisional's startUT). The bypass opened a
            // Relative section, SeedRelativeBoundaryPoint failed with
            // anchor-out-of-recorded-range, ForceExitRelativeToAbsolute
            // closed it, and the next frame's bypass call repeated the
            // cycle.
            //
            // The narrowed-gate change makes this code path unreachable from
            // the recorder gate sites (the bypass call is gone), but the
            // pre-check log + ordering remains a meaningful invariant for
            // the apply helpers themselves: if a future PR re-wires them
            // (e.g. via a different bypass path or a manual call site), the
            // thrash regression must not return.
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
