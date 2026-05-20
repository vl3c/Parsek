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
    /// contract for re-fly forks with no nearby real anchor. The bypass was
    /// replaced with a filter
    /// (<c>ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional</c>) that
    /// drops every candidate whose recording is in the same
    /// <see cref="RecordingTree"/> as the provisional, so the nearest-search
    /// sees only out-of-tree real anchors (stations, bases, live vessels
    /// from other lineages). The result: re-fly fork with no nearby real
    /// anchor authors Absolute, re-fly fork mid-docking-approach authors
    /// Relative-against-real-station, loop-anchored re-fly fork authors
    /// Relative-against-live-loop-anchor. The orphaned bypass function, the
    /// two recorder apply helpers, and the force-Absolute rollback toggle
    /// have since been deleted; these gates now only assert the filter is
    /// wired and the bypass call is absent.</para>
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
            // The supersede-target bypass call is gone from the gate site
            // (the bypass function and its apply helper were deleted). This
            // test confirms the call string never returns to
            // UpdateBackgroundAnchorDetection.
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
        public void FlightRecorder_BuildsCandidateList_BeforeNarrowedGateFilter()
        {
            // BuildRecordingAnchorCandidateList carries a load-bearing side
            // effect: ConsiderReFlyTreeSamplingProximity (called from the
            // Add{Live,External}RecordingAnchorCandidates helpers) populates
            // reFlyTreeSamplingProximityMeters, which feeds the next
            // OnPhysicsFrame's proximity-tier sampling cadence
            // (Full / Half / None at 0-250m / 250-500m / 500m+ ranges,
            // see ReFlyTree{Full,Half}FidelityProximityRangeMeters).
            //
            // The candidate build must execute before the narrowed-gate
            // filter so the proximity scan side effect runs on every re-fly
            // frame, even ones where the filter drops every same-tree
            // candidate. The filter also consumes the candidate list, so the
            // ordering is doubly load-bearing.
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

            int filterIdx = source.IndexOf(
                "ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional(",
                updateAnchorIdx,
                StringComparison.Ordinal);
            Assert.True(filterIdx >= 0,
                "Narrowed-gate filter call not found in UpdateAnchorDetection");
            Assert.True(candidateBuildIdx < filterIdx,
                "BuildRecordingAnchorCandidateList must execute BEFORE the narrowed-gate filter " +
                "so the filter has a candidate list and the proximity-tier sampling side effect runs.");
        }
    }
}
