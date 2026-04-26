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

        private const string TreeId = "tree-1";
        private const string ParentBpId = "bp-decouple-1";

        /// <summary>
        /// Builds a one-tree topology where <paramref name="activeId"/> is the
        /// child of <paramref name="parentId"/>: an Undock BranchPoint whose
        /// <c>ParentRecordingIds</c> contains <paramref name="parentId"/>, and
        /// the active recording's <c>ParentBranchPointId</c> points to that BP.
        /// This is the exact shape <see cref="GhostMapPresence.IsRecordingInParentChainOfActiveReFly"/>
        /// walks: child → ParentBranchPointId → BranchPoint → ParentRecordingIds.
        /// </summary>
        private static List<RecordingTree> TreesWithDecouple(
            string parentId,
            string activeId,
            params (string id, string vesselName, uint pid)[] extraRecs)
        {
            var tree = new RecordingTree { Id = TreeId };
            tree.Recordings[parentId] = new Recording
            {
                RecordingId = parentId,
                TreeId = TreeId,
                ChildBranchPointId = ParentBpId,
            };
            tree.Recordings[activeId] = new Recording
            {
                RecordingId = activeId,
                TreeId = TreeId,
                ParentBranchPointId = ParentBpId,
            };
            for (int i = 0; i < extraRecs.Length; i++)
            {
                var r = extraRecs[i];
                if (tree.Recordings.ContainsKey(r.id)) continue;
                tree.Recordings[r.id] = new Recording
                {
                    RecordingId = r.id,
                    TreeId = TreeId,
                    VesselName = r.vesselName,
                    VesselPersistentId = r.pid,
                };
            }
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = ParentBpId,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { parentId },
                ChildRecordingIds = new List<string> { activeId },
            });
            return new List<RecordingTree> { tree };
        }

        /// <summary>
        /// Builds a two-recording, no-relationship topology: each recording is
        /// its own root with no ParentBranchPointId / ChildBranchPointId. Used
        /// for tests that exercise gates upstream of the parent-chain walk
        /// (no marker / placeholder / wrong branch / etc.) where the trees are
        /// irrelevant — keeping the helper distinct from
        /// <see cref="TreesWithDecouple"/> documents that intent.
        /// </summary>
        private static List<RecordingTree> TreesFlat(params string[] recIds)
        {
            var tree = new RecordingTree { Id = TreeId };
            for (int i = 0; i < recIds.Length; i++)
            {
                tree.Recordings[recIds[i]] = new Recording
                {
                    RecordingId = recIds[i],
                    TreeId = TreeId,
                };
            }
            return new List<RecordingTree> { tree };
        }

        private static ReFlySessionMarker InPlaceMarker(string activeAndOriginRecId)
        {
            return new ReFlySessionMarker
            {
                SessionId = "sess_587_third_facet_test",
                TreeId = TreeId,
                ActiveReFlyRecordingId = activeAndOriginRecId,
                OriginChildRecordingId = activeAndOriginRecId,
                InvokedUT = 159.5,
            };
        }

        // -----------------------------------------------------------------
        // Positive — the user's exact scenario.
        // -----------------------------------------------------------------

        [Fact]
        public void Suppresses_WhenInPlaceMarker_RelativeBranch_AnchorIsActiveReFlyVesselPid_VictimIsParent()
        {
            // The user's exact case: the parent capsule recording is being
            // mapped during a Re-Fly of the booster, with its current section
            // in Relative frame anchored to the booster's pid (= active
            // Re-Fly target). The capsule decouples into the booster, so the
            // booster's ParentBranchPointId points to a BP whose
            // ParentRecordingIds = [capsule].
            const uint boosterPid = 2676381515u;
            var marker = InPlaceMarker("rec-booster");
            var committed = CommittedWith(
                ("rec-capsule", "Kerbal X", 2708531065u),
                ("rec-booster", "Kerbal X Probe", boosterPid));
            var trees = TreesWithDecouple(
                parentId: "rec-capsule",
                activeId: "rec-booster");

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: boosterPid,
                victimRecordingId: "rec-capsule",
                committedRecordings: committed,
                committedTrees: trees,
                out string reason);

            Assert.True(suppressed);
            Assert.StartsWith("refly-relative-anchor=active relationship=parent", reason);
        }

        // -----------------------------------------------------------------
        // PR #574 review P2: parent-chain scope. The anchor-equality predicate
        // is necessary but not sufficient — a docking-target recording or any
        // sibling recording could legitimately be Relative-anchored to the
        // active vessel for #583 / #584 reasons. Restrict suppression to the
        // user's actual case (victim is in the active recording's parent
        // chain).
        // -----------------------------------------------------------------

        [Fact]
        public void NotSuppressed_WhenAnchorIsActiveButRecordingIsNotParent_DockingTargetSibling()
        {
            // A separate recording (e.g. a station that the booster docks to
            // for rendezvous) is Relative-anchored to the booster's pid for
            // legitimate map-display reasons, but is NOT in the booster's
            // parent chain. The anchor-equality check would otherwise hide
            // its #583 / #584 ghost during Re-Fly — the parent-chain gate
            // prevents that.
            const uint boosterPid = 2676381515u;
            var marker = InPlaceMarker("rec-booster");
            var committed = CommittedWith(
                ("rec-capsule", "Kerbal X", 2708531065u),
                ("rec-booster", "Kerbal X Probe", boosterPid),
                ("rec-station", "Mun Station", 9999999u));
            // Only capsule -> booster is a parent relationship; rec-station
            // exists alongside as an unrelated sibling/peer.
            var trees = TreesWithDecouple(
                parentId: "rec-capsule",
                activeId: "rec-booster",
                extraRecs: ("rec-station", "Mun Station", 9999999u));

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: boosterPid, // anchor IS the booster (the active)
                victimRecordingId: "rec-station", // but the recording being mapped is the station
                committedRecordings: committed,
                committedTrees: trees,
                out string reason);

            Assert.False(suppressed);
            Assert.StartsWith("not-suppressed-not-parent-of-refly-target", reason);
        }

        [Fact]
        public void Suppresses_WhenVictimIsGrandparent_MultiHopParentChain()
        {
            // A multi-hop parent chain: rec-pad (root) -> rec-capsule (decoupled
            // off pad) -> rec-booster (decoupled off capsule). All three are
            // valid victims of the doubled-vessel placement during a Re-Fly
            // of rec-booster. The walk must enqueue parent BPs transitively.
            const uint boosterPid = 2676381515u;
            var marker = InPlaceMarker("rec-booster");
            var committed = CommittedWith(
                ("rec-pad", "Kerbal X Pad", 1u),
                ("rec-capsule", "Kerbal X", 2708531065u),
                ("rec-booster", "Kerbal X Probe", boosterPid));

            var tree = new RecordingTree { Id = TreeId };
            tree.Recordings["rec-pad"] = new Recording
            {
                RecordingId = "rec-pad",
                TreeId = TreeId,
                ChildBranchPointId = "bp-pad-decouple",
            };
            tree.Recordings["rec-capsule"] = new Recording
            {
                RecordingId = "rec-capsule",
                TreeId = TreeId,
                ParentBranchPointId = "bp-pad-decouple",
                ChildBranchPointId = "bp-stage-decouple",
            };
            tree.Recordings["rec-booster"] = new Recording
            {
                RecordingId = "rec-booster",
                TreeId = TreeId,
                ParentBranchPointId = "bp-stage-decouple",
            };
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-pad-decouple",
                Type = BranchPointType.Launch,
                ParentRecordingIds = new List<string> { "rec-pad" },
                ChildRecordingIds = new List<string> { "rec-capsule" },
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-stage-decouple",
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "rec-capsule" },
                ChildRecordingIds = new List<string> { "rec-booster" },
            });
            var trees = new List<RecordingTree> { tree };

            // Victim is the grandparent (rec-pad) — must still be reached by
            // the multi-hop parent walk.
            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: boosterPid,
                victimRecordingId: "rec-pad",
                committedRecordings: committed,
                committedTrees: trees,
                out string reason);

            Assert.True(suppressed);
            Assert.StartsWith("refly-relative-anchor=active relationship=parent", reason);
        }

        [Fact]
        public void NotSuppressed_WhenVictimIsActiveItself()
        {
            // The active recording itself is already covered by the
            // SessionSuppressedSubtree gate (IsSuppressedByActiveSession);
            // the parent-chain predicate is idempotent for that case and
            // returns a distinct rejection reason for grep clarity.
            const uint boosterPid = 2676381515u;
            var marker = InPlaceMarker("rec-booster");
            var committed = CommittedWith(
                ("rec-capsule", "Kerbal X", 2708531065u),
                ("rec-booster", "Kerbal X Probe", boosterPid));
            var trees = TreesWithDecouple(
                parentId: "rec-capsule",
                activeId: "rec-booster");

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: boosterPid,
                victimRecordingId: "rec-booster", // victim IS the active recording
                committedRecordings: committed,
                committedTrees: trees,
                out string reason);

            Assert.False(suppressed);
            Assert.Equal("not-suppressed-victim-is-active", reason);
        }

        [Fact]
        public void NotSuppressed_WhenCommittedTreesIsNull_BailsSafely()
        {
            // Defensive: a missing tree topology cannot be safely walked —
            // err on the side of NOT suppressing rather than silently
            // returning true on the looser anchor-equality predicate.
            const uint boosterPid = 2676381515u;
            var marker = InPlaceMarker("rec-booster");
            var committed = CommittedWith(
                ("rec-capsule", "Kerbal X", 2708531065u),
                ("rec-booster", "Kerbal X Probe", boosterPid));

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: boosterPid,
                victimRecordingId: "rec-capsule",
                committedRecordings: committed,
                committedTrees: null, // missing tree data
                out string reason);

            Assert.False(suppressed);
            Assert.StartsWith("not-suppressed-not-parent-of-refly-target", reason);
        }

        [Fact]
        public void NotSuppressed_WhenVictimIdIsNullOrEmpty()
        {
            const uint boosterPid = 2676381515u;
            var marker = InPlaceMarker("rec-booster");
            var committed = CommittedWith(
                ("rec-booster", "Kerbal X Probe", boosterPid));
            var trees = TreesWithDecouple(
                parentId: "rec-capsule",
                activeId: "rec-booster");

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: boosterPid,
                victimRecordingId: null,
                committedRecordings: committed,
                committedTrees: trees,
                out string reason);

            Assert.False(suppressed);
            Assert.Equal("not-suppressed-no-victim-id", reason);
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
            var trees = TreesFlat("rec-capsule", "rec-booster");

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker: null,
                resolutionBranch: "relative",
                resolutionAnchorPid: 2676381515u,
                victimRecordingId: "rec-capsule",
                committedRecordings: committed,
                committedTrees: trees,
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
                TreeId = TreeId,
                ActiveReFlyRecordingId = "rec-fresh-provisional",
                OriginChildRecordingId = "rec-booster",
                InvokedUT = 159.5,
            };
            var committed = CommittedWith(
                ("rec-fresh-provisional", "Kerbal X Probe", boosterPid),
                ("rec-booster", "Kerbal X Probe", boosterPid));
            var trees = TreesFlat("rec-fresh-provisional", "rec-booster");

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: boosterPid,
                victimRecordingId: "rec-booster",
                committedRecordings: committed,
                committedTrees: trees,
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
            var trees = TreesWithDecouple(
                parentId: "rec-capsule",
                activeId: "rec-booster");

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "absolute",
                resolutionAnchorPid: 0u,
                victimRecordingId: "rec-capsule",
                committedRecordings: committed,
                committedTrees: trees,
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
            var trees = TreesWithDecouple(
                parentId: "rec-capsule",
                activeId: "rec-booster",
                extraRecs: ("rec-station", "Mun Station", 9999999u));

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: 9999999u, // anchor is the station, not the booster
                victimRecordingId: "rec-capsule",
                committedRecordings: committed,
                committedTrees: trees,
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
            var trees = TreesWithDecouple(
                parentId: "rec-capsule",
                activeId: "rec-booster");

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: 0u,
                victimRecordingId: "rec-capsule",
                committedRecordings: committed,
                committedTrees: trees,
                out string reason);

            Assert.False(suppressed);
            Assert.Equal("not-suppressed-no-anchor-pid", reason);
        }

        [Fact]
        public void NotSuppressed_WhenActiveReFlyRecordingMissingFromCommittedList()
        {
            // Defensive: a stale marker whose active recording id is not in
            // the committed list (NOR in any pending tree, after #611 P1)
            // cannot be safely matched — bail out with a distinct reason
            // rather than silently turning into a tautology.
            var marker = InPlaceMarker("rec-missing-from-store");
            var committed = CommittedWith(
                ("rec-capsule", "Kerbal X", 2708531065u));
            var trees = TreesFlat("rec-capsule");

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: 2676381515u,
                victimRecordingId: "rec-capsule",
                committedRecordings: committed,
                committedTrees: trees,
                out string reason);

            Assert.False(suppressed);
            Assert.StartsWith("not-suppressed-active-rec-pid-unknown", reason);
            Assert.Contains("activeRecId=rec-missing-from-store", reason);
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
            var trees = TreesWithDecouple(
                parentId: "rec-capsule",
                activeId: "rec-booster");

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: 12345u,
                victimRecordingId: "rec-capsule",
                committedRecordings: committed,
                committedTrees: trees,
                out string reason);

            Assert.False(suppressed);
            Assert.StartsWith("not-suppressed-active-rec-pid-unknown", reason);
        }

        [Fact]
        public void NotSuppressed_WhenCommittedListIsNull_AndTreeRecsHaveNoPid()
        {
            // #611 P1 follow-up: the predicate tolerates a null
            // `committedRecordings` and falls back to the search-tree walk.
            // When the search trees also can't yield a non-zero PID for the
            // active recording, the gate bails with the unified
            // `not-suppressed-active-rec-pid-unknown` reason carrying the
            // search-tree count and committed-recordings count.
            var marker = InPlaceMarker("rec-booster");
            var trees = TreesWithDecouple(
                parentId: "rec-capsule",
                activeId: "rec-booster");

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: 2676381515u,
                victimRecordingId: "rec-capsule",
                committedRecordings: null,
                committedTrees: trees,
                out string reason);

            Assert.False(suppressed);
            Assert.StartsWith("not-suppressed-active-rec-pid-unknown", reason);
            Assert.Contains("searchTrees=1", reason);
            Assert.Contains("committedRecordings=0", reason);
        }

        [Fact]
        public void NotSuppressed_WhenMarkerFieldsEmpty()
        {
            var marker = new ReFlySessionMarker { SessionId = "sess", TreeId = TreeId };
            var committed = CommittedWith(("rec-booster", "Kerbal X Probe", 2676381515u));
            var trees = TreesWithDecouple(
                parentId: "rec-capsule",
                activeId: "rec-booster");

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: 2676381515u,
                victimRecordingId: "rec-capsule",
                committedRecordings: committed,
                committedTrees: trees,
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
            var trees = TreesWithDecouple(
                parentId: "rec-capsule",
                activeId: "rec-booster");

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "no-section",
                resolutionAnchorPid: 0u,
                victimRecordingId: "rec-capsule",
                committedRecordings: committed,
                committedTrees: trees,
                out string reason);

            Assert.False(suppressed);
            Assert.Equal("not-suppressed-not-relative-frame", reason);
        }

        [Fact]
        public void NotSuppressed_WhenBranchIsOrbitalCheckpoint()
        {
            var marker = InPlaceMarker("rec-booster");
            var committed = CommittedWith(("rec-booster", "Kerbal X Probe", 2676381515u));
            var trees = TreesWithDecouple(
                parentId: "rec-capsule",
                activeId: "rec-booster");

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "orbital-checkpoint",
                resolutionAnchorPid: 0u,
                victimRecordingId: "rec-capsule",
                committedRecordings: committed,
                committedTrees: trees,
                out string reason);

            Assert.False(suppressed);
            Assert.Equal("not-suppressed-not-relative-frame", reason);
        }

        [Fact]
        public void Suppression_DistinctReason_StableForLogParsers()
        {
            // The structured log line shape pins on the suppress-reason value;
            // tests that grep for `refly-relative-anchor=active relationship=parent`
            // would silently pass if the constant ever drifted. Assert the
            // canonical spelling here so a future refactor cannot change the
            // reason string without breaking this test.
            const uint boosterPid = 2676381515u;
            var marker = InPlaceMarker("rec-booster");
            var committed = CommittedWith(
                ("rec-capsule", "Kerbal X", 2708531065u),
                ("rec-booster", "Kerbal X Probe", boosterPid));
            var trees = TreesWithDecouple(
                parentId: "rec-capsule",
                activeId: "rec-booster");

            GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: boosterPid,
                victimRecordingId: "rec-capsule",
                committedRecordings: committed,
                committedTrees: trees,
                out string reason);

            Assert.StartsWith("refly-relative-anchor=active relationship=parent", reason);
        }

        // -----------------------------------------------------------------
        // Pure parent-chain helper: direct-call coverage so future refactors
        // of the topology walk cannot silently drift from the predicate's
        // gate logic.
        // -----------------------------------------------------------------

        [Fact]
        public void IsRecordingInParentChainOfActiveReFly_DirectParent_ReturnsTrue()
        {
            var trees = TreesWithDecouple(
                parentId: "rec-capsule",
                activeId: "rec-booster");

            Assert.True(GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                "rec-capsule",
                "rec-booster",
                trees,
                out _));
        }

        [Fact]
        public void IsRecordingInParentChainOfActiveReFly_UnrelatedSibling_ReturnsFalse()
        {
            var trees = TreesWithDecouple(
                parentId: "rec-capsule",
                activeId: "rec-booster",
                extraRecs: ("rec-station", "Mun Station", 9999999u));

            Assert.False(GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                "rec-station",
                "rec-booster",
                trees,
                out _));
        }

        [Fact]
        public void IsRecordingInParentChainOfActiveReFly_NullArgsBailSafely()
        {
            Assert.False(GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                null, "rec-booster", new List<RecordingTree>(), out _));
            Assert.False(GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                "rec-capsule", null, new List<RecordingTree>(), out _));
            Assert.False(GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                "rec-capsule", "rec-booster", null, out _));
        }

        [Fact]
        public void IsRecordingInParentChainOfActiveReFly_MissingActiveRecording_ReturnsFalse()
        {
            // The walk must locate the active recording in some tree before it
            // can seed the queue — otherwise it bails false (not suppress).
            var trees = TreesFlat("rec-capsule");
            Assert.False(GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                "rec-capsule",
                "rec-booster", // not in any tree
                trees,
                out _));
        }

        [Fact]
        public void IsRecordingInParentChainOfActiveReFly_CycleInBpTopologyBailsSafely()
        {
            // A pathological cycle (BP-A's parent is rec-1, which references
            // BP-A again as its ParentBranchPointId) must not infinite-loop;
            // the visited-sets cut the walk.
            var tree = new RecordingTree { Id = TreeId };
            tree.Recordings["rec-1"] = new Recording
            {
                RecordingId = "rec-1",
                TreeId = TreeId,
                ParentBranchPointId = "bp-cycle",
            };
            tree.Recordings["rec-2"] = new Recording
            {
                RecordingId = "rec-2",
                TreeId = TreeId,
                ParentBranchPointId = "bp-cycle",
            };
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-cycle",
                ParentRecordingIds = new List<string> { "rec-1", "rec-2" },
                ChildRecordingIds = new List<string> { "rec-1", "rec-2" },
            });
            var trees = new List<RecordingTree> { tree };

            // rec-1 IS itself a parent in the BP, so walking from rec-2 hits
            // rec-1 immediately. The cycle is harmless because visited-recs
            // bounds the walk.
            Assert.True(GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                "rec-1", "rec-2", trees, out _));
        }

        // -----------------------------------------------------------------
        // #611: production playtest hit a "doubled vessel still visible"
        // outcome despite PR #574's parent-chain gate. Diagnosis: at Re-Fly
        // load time, TryRestoreActiveTreeNode calls
        // RecordingStore.RemoveCommittedTreeById on the same tree whose
        // recordings the predicate is about to walk — the tree has just
        // been stashed into PendingTree. Searching only CommittedTrees made
        // the active-recording lookup fail silently, the BFS bailed, the
        // predicate fell through to "not-suppressed-not-parent-of-refly-
        // target", the ProtoVessel got created, the bug shipped.
        //
        // Fix: callers must compose committed + pending into the search
        // list. Helper ComposeSearchTreesForReFlySuppression encapsulates
        // the rule.
        // -----------------------------------------------------------------

        [Fact]
        public void ComposeSearchTreesForReFlySuppression_NoPending_ReturnsCommittedAsIs()
        {
            var committed = new List<RecordingTree>
            {
                new RecordingTree { Id = "tree-A" },
                new RecordingTree { Id = "tree-B" },
            };

            var result = GhostMapPresence.ComposeSearchTreesForReFlySuppression(
                committed,
                pendingTree: null);

            Assert.Equal(2, result.Count);
            Assert.Same(committed[0], result[0]);
            Assert.Same(committed[1], result[1]);
        }

        [Fact]
        public void ComposeSearchTreesForReFlySuppression_NullCommitted_ReturnsEmptyOrPendingOnly()
        {
            var pending = new RecordingTree { Id = "tree-pending" };

            var withPending = GhostMapPresence.ComposeSearchTreesForReFlySuppression(
                committedTrees: null, pendingTree: pending);

            Assert.Single(withPending);
            Assert.Same(pending, withPending[0]);

            var noPending = GhostMapPresence.ComposeSearchTreesForReFlySuppression(
                committedTrees: null, pendingTree: null);

            Assert.NotNull(noPending);
            Assert.Empty(noPending);
        }

        [Fact]
        public void ComposeSearchTreesForReFlySuppression_PendingDistinctFromCommitted_AppendsPending()
        {
            var committed = new List<RecordingTree>
            {
                new RecordingTree { Id = "tree-committed" },
            };
            var pending = new RecordingTree { Id = "tree-pending" };

            var result = GhostMapPresence.ComposeSearchTreesForReFlySuppression(
                committed, pending);

            Assert.Equal(2, result.Count);
            Assert.Same(committed[0], result[0]);
            Assert.Same(pending, result[1]);
        }

        [Fact]
        public void ComposeSearchTreesForReFlySuppression_PendingSameIdAsCommitted_KeepsPendingDropsCommitted()
        {
            // Same tree id in both committed and pending (load-time transient).
            // Pending carries the post-splice + post-refresh shape; committed
            // is the pre-load snapshot. The helper drops the committed
            // duplicate so the BFS walk only iterates the post-load truth.
            var committed = new List<RecordingTree>
            {
                new RecordingTree { Id = "tree-shared" },
            };
            var pending = new RecordingTree { Id = "tree-shared" };

            var result = GhostMapPresence.ComposeSearchTreesForReFlySuppression(
                committed, pending);

            Assert.Single(result);
            Assert.Same(pending, result[0]);
        }

        [Fact]
        public void ComposeSearchTreesForReFlySuppression_SameInputs_ReturnsStableComposedView()
        {
            var committed = new List<RecordingTree>
            {
                new RecordingTree { Id = "tree-committed" },
            };
            var pending = new RecordingTree { Id = "tree-pending" };

            var first = GhostMapPresence.ComposeSearchTreesForReFlySuppression(
                committed, pending);
            var second = GhostMapPresence.ComposeSearchTreesForReFlySuppression(
                committed, pending);

            Assert.Equal(first.Count, second.Count);
            Assert.Equal(2, second.Count);
            Assert.Same(committed[0], second[0]);
            Assert.Same(pending, second[1]);
        }

        [Fact]
        public void ComposeSearchTreesForReFlySuppression_SameListMutated_InvalidatesCachedView()
        {
            var original = new RecordingTree { Id = "tree-original" };
            var replacement = new RecordingTree { Id = "tree-replacement" };
            var committed = new List<RecordingTree> { original };
            var pending = new RecordingTree { Id = "tree-pending" };

            var first = GhostMapPresence.ComposeSearchTreesForReFlySuppression(
                committed, pending);
            committed[0] = replacement;

            var second = GhostMapPresence.ComposeSearchTreesForReFlySuppression(
                committed, pending);

            Assert.NotSame(first, second);
            Assert.Equal(2, second.Count);
            Assert.Same(replacement, second[0]);
            Assert.Same(pending, second[1]);
        }

        [Fact]
        public void Suppresses_LoadWindowShape_EmptyCommittedRecordings_ActiveInPendingTree()
        {
            // #611 P1 review follow-up: the user-visible bug is the
            // doubled ProtoVessel, and the predicate's first PID-resolution
            // lookup walked only `RecordingStore.CommittedRecordings`. At
            // Re-Fly load time `TryRestoreActiveTreeNode` calls
            // `RemoveCommittedTreeById`, which empties this tree's recordings
            // out of `CommittedRecordings` before stashing the loaded tree
            // as `PendingTree`. The PID lookup therefore returned 0 and
            // the gate bailed with `not-suppressed-active-rec-pid-unknown`
            // BEFORE the new pending-tree topology walk could run.
            //
            // This regression pins the exact production load-window shape:
            // committedRecordings = [] (empty) AND the active recording lives
            // in the composed search trees with VesselPersistentId set
            // (representing the pending tree's recordings). The predicate
            // must now resolve the active PID from the search trees and
            // proceed to suppress.
            const uint boosterPid = 2676381515u;
            var marker = InPlaceMarker("rec-booster");

            // committedRecordings is empty (RemoveCommittedTreeById has run).
            var emptyCommitted = new List<Recording>();

            // Build the tree shape that the splice has just stashed as
            // PendingTree: parent (capsule) ChildBranchPointId -> BP, active
            // (booster) ParentBranchPointId -> same BP. Set the active rec's
            // VesselPersistentId so the PID-via-tree lookup succeeds.
            var pendingTree = new RecordingTree { Id = TreeId };
            pendingTree.Recordings["rec-capsule"] = new Recording
            {
                RecordingId = "rec-capsule",
                TreeId = TreeId,
                VesselName = "Kerbal X",
                VesselPersistentId = 2708531065u,
                ChildBranchPointId = ParentBpId,
            };
            pendingTree.Recordings["rec-booster"] = new Recording
            {
                RecordingId = "rec-booster",
                TreeId = TreeId,
                VesselName = "Kerbal X Probe",
                VesselPersistentId = boosterPid,
                ParentBranchPointId = ParentBpId,
            };
            pendingTree.BranchPoints.Add(new BranchPoint
            {
                Id = ParentBpId,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "rec-capsule" },
                ChildRecordingIds = new List<string> { "rec-booster" },
            });

            // Production composes committed ++ pending; here committed is
            // empty so the search list is just [pendingTree].
            var search = GhostMapPresence.ComposeSearchTreesForReFlySuppression(
                committedTrees: new List<RecordingTree>(),
                pendingTree: pendingTree);

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: boosterPid,
                victimRecordingId: "rec-capsule",
                committedRecordings: emptyCommitted,
                committedTrees: search,
                out string reason);

            Assert.True(suppressed);
            // Success reason now exposes where the active PID came from
            // so the load-window vs steady-state distinction is auditable.
            Assert.Contains("activePidSource=search-tree:" + TreeId, reason);
            Assert.Contains("relationship=parent", reason);
            Assert.Contains("found-victim-in-parent-chain", reason);
        }

        [Fact]
        public void NotSuppressed_LoadWindowShape_ActiveMissingEverywhere_ReportsZeroCounts()
        {
            // Belt-and-suspenders: when both lookup sources fail to resolve
            // the active recording's PID, the rejection reason must carry
            // the search-tree count + committed-recordings count + active
            // recording id so a playtest log reader can immediately diagnose
            // "ah, neither pending nor committed had it" instead of having
            // to re-read the source.
            const uint boosterPid = 2676381515u;
            var marker = InPlaceMarker("rec-booster");

            // Active rec id ("rec-booster") doesn't appear anywhere.
            var emptyCommitted = new List<Recording>();
            var unrelatedTree = new RecordingTree { Id = "tree-unrelated" };
            unrelatedTree.Recordings["rec-other"] = new Recording
            {
                RecordingId = "rec-other",
                TreeId = "tree-unrelated",
                VesselPersistentId = 12345u,
            };
            var search = new List<RecordingTree> { unrelatedTree };

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: boosterPid,
                victimRecordingId: "rec-capsule",
                committedRecordings: emptyCommitted,
                committedTrees: search,
                out string reason);

            Assert.False(suppressed);
            Assert.StartsWith("not-suppressed-active-rec-pid-unknown", reason);
            Assert.Contains("searchTrees=1", reason);
            Assert.Contains("committedRecordings=0", reason);
            Assert.Contains("activeRecId=rec-booster", reason);
        }

        [Fact]
        public void IsRecordingInParentChainOfActiveReFly_ActiveInPendingTree_FoundViaSearchList()
        {
            // The exact #611 production shape: at Re-Fly load time,
            // CommittedTrees has been emptied for this tree (post
            // RemoveCommittedTreeById), and the active recording lives in
            // PendingTree. The BFS walk must see both.
            var pendingOnly = TreesWithDecouple(
                parentId: "rec-capsule",
                activeId: "rec-booster");
            // Simulate the load-time state: committed is empty, only pending.
            var search = GhostMapPresence.ComposeSearchTreesForReFlySuppression(
                committedTrees: new List<RecordingTree>(),
                pendingTree: pendingOnly[0]);

            Assert.True(GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                "rec-capsule",
                "rec-booster",
                search,
                out string trace));
            Assert.Contains("found-victim-in-parent-chain", trace);
            Assert.Contains("victim=rec-capsule", trace);
        }

        [Fact]
        public void IsRecordingInParentChainOfActiveReFly_WalkTrace_ExhaustedShape()
        {
            // The walk-trace MUST carry enough detail for a playtest log
            // reader to diagnose a "predicate didn't fire" failure mode:
            // - the active recording id (so the reader can find it in tree)
            // - the visited-BPs count + ids (so the reader can see the walk)
            // - a termination reason (exhausted vs found)
            var trees = TreesWithDecouple(
                parentId: "rec-capsule",
                activeId: "rec-booster",
                extraRecs: ("rec-station", "Mun Station", 9999999u));

            Assert.False(GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                "rec-station",
                "rec-booster",
                trees,
                out string trace));
            Assert.Contains("exhausted-without-victim", trace);
            Assert.Contains("activeId=rec-booster", trace);
            Assert.Contains("victim=rec-station", trace);
            Assert.Contains("visitedBPs=", trace);
        }

        [Fact]
        public void IsRecordingInParentChainOfActiveReFly_WalkTrace_ActiveNotFoundShape()
        {
            // When the active recording is missing from every search tree
            // (the production bug shape pre-#611), the trace must say so
            // explicitly so the playtest log reader can see "ah, the lookup
            // failed because committed was empty / pending wasn't passed".
            Assert.False(GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                "rec-capsule",
                "rec-booster",
                new List<RecordingTree>(),
                out string trace));
            Assert.Contains("active-not-found", trace);
            Assert.Contains("activeId=rec-booster", trace);
            Assert.Contains("treesSearched=0", trace);
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
            fields.Reason = "refly-relative-anchor=active relationship=parent sess=sess_demo retryLater=true";

            string line = GhostMapPresence.BuildGhostMapDecisionLine(fields);

            // Action label
            Assert.StartsWith("create-state-vector-suppressed:", line);
            // Identity fields
            Assert.Contains("rec=rec-capsule", line);
            Assert.Contains("vessel=\"Kerbal X\"", line);
            Assert.Contains("source=StateVector", line);
            Assert.Contains("branch=Relative", line);
            Assert.Contains("body=Kerbin", line);
            // Anchor + reason — relationship + retryLater are PR #574 P2 additions
            Assert.Contains("anchorPid=" + boosterPid.ToString(), line);
            Assert.Contains(
                "reason=refly-relative-anchor=active relationship=parent sess=sess_demo retryLater=true",
                line);
        }

        // -----------------------------------------------------------------
        // PR #613 review P2: the v7 absolute-shadow branch is the same
        // RELATIVE section's sibling positioning source. Suppression must
        // fire on both "relative" and "absolute-shadow"; treating only
        // "relative" leaks parent-chain v7 doubled ProtoVessels into the
        // scene during active Re-Fly.
        // -----------------------------------------------------------------

        [Fact]
        public void Suppresses_WhenBranchIsAbsoluteShadow_ParentChainVictim()
        {
            // Same scenario as the canonical positive test, but the resolver
            // returned the v7 absolute-shadow branch (the section's anchor PID
            // matched the active Re-Fly target so the wrapper substituted the
            // shadow point in lieu of multiplying anchor-local offsets by the
            // player's live pose). The suppression decision must STILL fire,
            // because the underlying section is RELATIVE — it's only the
            // positioning source that changed.
            const uint boosterPid = 2676381515u;
            var marker = InPlaceMarker("rec-booster");
            var committed = CommittedWith(
                ("rec-capsule", "Kerbal X", 2708531065u),
                ("rec-booster", "Kerbal X Probe", boosterPid));
            var trees = TreesWithDecouple(
                parentId: "rec-capsule",
                activeId: "rec-booster");

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "absolute-shadow",
                resolutionAnchorPid: boosterPid,
                victimRecordingId: "rec-capsule",
                committedRecordings: committed,
                committedTrees: trees,
                out string reason);

            Assert.True(suppressed);
            Assert.StartsWith("refly-relative-anchor=active relationship=parent", reason);
        }

        [Fact]
        public void NotSuppressed_WhenBranchIsAbsolute_RealAbsoluteSection()
        {
            // Negative pin: a true Absolute (non-shadow) section must NOT be
            // suppressed. The "absolute" string is the regular Absolute
            // branch label; only "absolute-shadow" piggy-backs on the
            // RELATIVE-section suppression rule.
            const uint boosterPid = 2676381515u;
            var marker = InPlaceMarker("rec-booster");
            var committed = CommittedWith(
                ("rec-capsule", "Kerbal X", 2708531065u),
                ("rec-booster", "Kerbal X Probe", boosterPid));
            var trees = TreesWithDecouple(
                parentId: "rec-capsule",
                activeId: "rec-booster");

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly(
                marker,
                resolutionBranch: "absolute",
                resolutionAnchorPid: boosterPid,
                victimRecordingId: "rec-capsule",
                committedRecordings: committed,
                committedTrees: trees,
                out string reason);

            Assert.False(suppressed);
            Assert.Equal("not-suppressed-not-relative-frame", reason);
        }
    }
}
