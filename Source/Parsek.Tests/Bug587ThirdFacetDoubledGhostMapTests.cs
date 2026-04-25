using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug #587 third facet (2026-04-25 playtest follow-up): the in-place
    /// continuation Re-Fly path leaves the parent of the active Re-Fly recording
    /// outside <see cref="EffectiveState.ComputeSessionSuppressedSubtree"/>'s
    /// child-ward closure. When that parent recording is mid-flight in a
    /// <see cref="ReferenceFrame.Relative"/>-anchored section whose anchor is
    /// the live active Re-Fly target's persistent id,
    /// <see cref="GhostMapPresence.CreateGhostVesselFromStateVectors"/> would
    /// synthesize a real registered <c>Vessel</c> colocated with the active
    /// vessel — the "doubled upper-stage" the user reported.
    ///
    /// The first facet (#587) and second facet (#587 follow-up) targeted the
    /// strip-side leftover (a pre-existing in-scene <c>Vessel</c> the
    /// <c>PostLoadStripper</c> missed). This third facet targets the GhostMap-
    /// side <em>creation</em> of a fresh ProtoVessel during the same Re-Fly
    /// invocation. The strip side cannot see this vessel because it is born
    /// after strip runs.
    ///
    /// All cases here drive the pure predicate
    /// <see cref="GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly"/>
    /// directly so no Unity scene is required.
    /// </summary>
    [Collection("Sequential")]
    public class Bug587ThirdFacetDoubledGhostMapTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug587ThirdFacetDoubledGhostMapTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static List<Recording> CommittedWith(params (string id, string vesselName, uint pid)[] recs)
        {
            var list = new List<Recording>();
            foreach (var r in recs)
            {
                list.Add(new Recording
                {
                    RecordingId = r.id,
                    VesselName = r.vesselName,
                    VesselPersistentId = r.pid,
                });
            }
            return list;
        }

        private static ReFlySessionMarker InPlaceMarker(string activeAndOriginRecId)
        {
            return new ReFlySessionMarker
            {
                SessionId = "sess_587_third_facet_test",
                TreeId = "tree-1",
                ActiveReFlyRecordingId = activeAndOriginRecId,
                OriginChildRecordingId = activeAndOriginRecId,
                InvokedUT = 159.5,
            };
        }

        // -----------------------------------------------------------------
        // Positive — the user's exact scenario.
        // -----------------------------------------------------------------

        [Fact]
        public void Suppresses_WhenInPlaceMarker_RelativeBranch_AnchorIsActiveReFlyVesselPid()
        {
            // The user's exact case: the parent capsule recording is being
            // mapped during a Re-Fly of the booster, with its current section
            // in Relative frame anchored to the booster's pid (= active
            // Re-Fly target).
            const uint boosterPid = 2676381515u;
            var marker = InPlaceMarker("rec-booster");
            var committed = CommittedWith(
                ("rec-capsule", "Kerbal X", 2708531065u),
                ("rec-booster", "Kerbal X Probe", boosterPid));

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: boosterPid,
                committedRecordings: committed,
                out string reason);

            Assert.True(suppressed);
            Assert.Equal("refly-relative-anchor=active", reason);
        }

        // -----------------------------------------------------------------
        // Negative — gates that must NOT over-trigger.
        // -----------------------------------------------------------------

        [Fact]
        public void NotSuppressed_WhenNoMarkerActive()
        {
            // Defense against over-broadening: outside Re-Fly, a Relative-frame
            // state-vector ghost whose anchor happens to be the active vessel
            // is a legitimate orbit-line case (e.g. ascent ghost alongside its
            // own anchor). The fix must only trigger inside Re-Fly.
            var committed = CommittedWith(
                ("rec-capsule", "Kerbal X", 2708531065u),
                ("rec-booster", "Kerbal X Probe", 2676381515u));

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker: null,
                resolutionBranch: "relative",
                resolutionAnchorPid: 2676381515u,
                committedRecordings: committed,
                out string reason);

            Assert.False(suppressed);
            Assert.Equal("not-suppressed-no-marker", reason);
        }

        [Fact]
        public void NotSuppressed_WhenMarkerIsPlaceholderPattern()
        {
            // Mirrors the #587 placeholder carve-out: provisional != origin
            // means the player's pre-rewind active vessel is still in scene
            // (no fresh restoration). The doubled-vessel placement only arises
            // in the in-place continuation pattern.
            const uint boosterPid = 2676381515u;
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_placeholder",
                TreeId = "tree-1",
                ActiveReFlyRecordingId = "rec-fresh-provisional",
                OriginChildRecordingId = "rec-booster",
                InvokedUT = 159.5,
            };
            var committed = CommittedWith(
                ("rec-fresh-provisional", "Kerbal X Probe", boosterPid),
                ("rec-booster", "Kerbal X Probe", boosterPid));

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: boosterPid,
                committedRecordings: committed,
                out string reason);

            Assert.False(suppressed);
            Assert.Equal("not-suppressed-placeholder-pattern", reason);
        }

        [Fact]
        public void NotSuppressed_WhenAbsoluteFrame()
        {
            // Absolute-frame state-vector paths use lat/lon/alt as geographic
            // surface coords; the position is not anchored to the active
            // vessel and the orbit synthesised from it is meaningful.
            var marker = InPlaceMarker("rec-booster");
            var committed = CommittedWith(
                ("rec-capsule", "Kerbal X", 2708531065u),
                ("rec-booster", "Kerbal X Probe", 2676381515u));

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "absolute",
                resolutionAnchorPid: 0u,
                committedRecordings: committed,
                out string reason);

            Assert.False(suppressed);
            Assert.Equal("not-suppressed-not-relative-frame", reason);
        }

        [Fact]
        public void NotSuppressed_WhenRelativeAnchorIsADifferentVessel()
        {
            // A Relative-frame ghost whose anchor is some OTHER vessel during
            // Re-Fly is legitimate (e.g. docking-target ghost). Only the
            // anchor-equals-active-Re-Fly-target case is the doubled-vessel
            // bug.
            var marker = InPlaceMarker("rec-booster");
            var committed = CommittedWith(
                ("rec-capsule", "Kerbal X", 2708531065u),
                ("rec-booster", "Kerbal X Probe", 2676381515u),
                ("rec-station", "Mun Station", 9999999u));

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: 9999999u, // anchor is the station, not the booster
                committedRecordings: committed,
                out string reason);

            Assert.False(suppressed);
            Assert.Equal("not-suppressed-anchor-not-active-refly", reason);
        }

        [Fact]
        public void NotSuppressed_WhenAnchorPidIsZero()
        {
            // Defensive: zero anchor pids cannot match the active vessel and
            // the helper should short-circuit with a distinct reason so
            // observability isolates this branch from the general "no match".
            var marker = InPlaceMarker("rec-booster");
            var committed = CommittedWith(
                ("rec-booster", "Kerbal X Probe", 2676381515u));

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: 0u,
                committedRecordings: committed,
                out string reason);

            Assert.False(suppressed);
            Assert.Equal("not-suppressed-no-anchor-pid", reason);
        }

        [Fact]
        public void NotSuppressed_WhenActiveReFlyRecordingMissingFromCommittedList()
        {
            // Defensive: a stale marker whose active recording id is not in
            // the committed list cannot be safely matched — bail out with a
            // distinct reason rather than silently turning into a tautology.
            var marker = InPlaceMarker("rec-missing-from-store");
            var committed = CommittedWith(
                ("rec-capsule", "Kerbal X", 2708531065u));

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: 2676381515u,
                committedRecordings: committed,
                out string reason);

            Assert.False(suppressed);
            Assert.Equal("not-suppressed-active-rec-pid-unknown", reason);
        }

        [Fact]
        public void NotSuppressed_WhenActiveReFlyRecordingHasZeroVesselPid()
        {
            // VesselPersistentId = 0 is the "not yet bound" sentinel; treat
            // it the same as missing so we never spuriously suppress on a
            // 0-vs-0 match against a Relative-section anchorVesselId that
            // (per construction) cannot be 0 anyway.
            var marker = InPlaceMarker("rec-booster");
            var committed = CommittedWith(
                ("rec-booster", "Kerbal X Probe", 0u));

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: 12345u,
                committedRecordings: committed,
                out string reason);

            Assert.False(suppressed);
            Assert.Equal("not-suppressed-active-rec-pid-unknown", reason);
        }

        [Fact]
        public void NotSuppressed_WhenCommittedListIsNull()
        {
            var marker = InPlaceMarker("rec-booster");

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: 2676381515u,
                committedRecordings: null,
                out string reason);

            Assert.False(suppressed);
            Assert.Equal("not-suppressed-no-committed-recordings", reason);
        }

        [Fact]
        public void NotSuppressed_WhenMarkerFieldsEmpty()
        {
            var marker = new ReFlySessionMarker { SessionId = "sess", TreeId = "tree-1" };
            var committed = CommittedWith(("rec-booster", "Kerbal X Probe", 2676381515u));

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: 2676381515u,
                committedRecordings: committed,
                out string reason);

            Assert.False(suppressed);
            Assert.Equal("not-suppressed-marker-fields-empty", reason);
        }

        // -----------------------------------------------------------------
        // Other-branches sanity — defensive against future refactors that
        // change branch label spellings.
        // -----------------------------------------------------------------

        [Fact]
        public void NotSuppressed_WhenBranchIsNoSection()
        {
            // Legacy/synthetic recordings with no track sections fall through
            // ResolveStateVectorWorldPositionPure's "no-section" Absolute
            // interpretation; their position is not anchor-derived and should
            // not be suppressed.
            var marker = InPlaceMarker("rec-booster");
            var committed = CommittedWith(("rec-booster", "Kerbal X Probe", 2676381515u));

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "no-section",
                resolutionAnchorPid: 0u,
                committedRecordings: committed,
                out string reason);

            Assert.False(suppressed);
            Assert.Equal("not-suppressed-not-relative-frame", reason);
        }

        [Fact]
        public void NotSuppressed_WhenBranchIsOrbitalCheckpoint()
        {
            var marker = InPlaceMarker("rec-booster");
            var committed = CommittedWith(("rec-booster", "Kerbal X Probe", 2676381515u));

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "orbital-checkpoint",
                resolutionAnchorPid: 0u,
                committedRecordings: committed,
                out string reason);

            Assert.False(suppressed);
            Assert.Equal("not-suppressed-not-relative-frame", reason);
        }

        [Fact]
        public void Suppression_DistinctReason_StableForLogParsers()
        {
            // The structured log line shape pins on the suppress-reason value;
            // tests that grep for `refly-relative-anchor=active` would silently
            // pass if the constant ever drifted to e.g. "active-refly-anchor".
            // Assert the canonical spelling here so a future refactor cannot
            // change the reason string without breaking this test.
            const uint boosterPid = 2676381515u;
            var marker = InPlaceMarker("rec-booster");
            var committed = CommittedWith(("rec-booster", "Kerbal X Probe", boosterPid));

            GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: boosterPid,
                committedRecordings: committed,
                out string reason);

            Assert.Equal("refly-relative-anchor=active", reason);
        }

        // -----------------------------------------------------------------
        // Structured log line shape: the production gate logs via
        // GhostMapPresence.BuildGhostMapDecisionLine with action
        // "create-state-vector-suppressed". Pin the line shape so a future
        // refactor cannot silently rename the action and break log parsers.
        // -----------------------------------------------------------------

        [Fact]
        public void StructuredLogLine_CreateStateVectorSuppressed_PinShape()
        {
            const uint boosterPid = 2676381515u;
            var fields = GhostMapPresence.NewDecisionFields("create-state-vector-suppressed");
            fields.RecordingId = "rec-capsule";
            fields.RecordingIndex = 0;
            fields.VesselName = "Kerbal X";
            fields.Source = "StateVector";
            fields.Branch = "Relative";
            fields.Body = "Kerbin";
            fields.AnchorPid = boosterPid;
            fields.StateVecAlt = 0.0;
            fields.StateVecSpeed = 2185.7;
            fields.UT = 159.5;
            fields.Reason = "refly-relative-anchor=active sess=sess_demo";

            string line = GhostMapPresence.BuildGhostMapDecisionLine(fields);

            // Action label
            Assert.StartsWith("create-state-vector-suppressed:", line);
            // Identity fields
            Assert.Contains("rec=rec-capsule", line);
            Assert.Contains("vessel=\"Kerbal X\"", line);
            Assert.Contains("source=StateVector", line);
            Assert.Contains("branch=Relative", line);
            Assert.Contains("body=Kerbin", line);
            // Anchor + reason
            Assert.Contains("anchorPid=" + boosterPid.ToString(), line);
            Assert.Contains("reason=refly-relative-anchor=active sess=sess_demo", line);
        }
    }
}
