using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Debris lifetime rules (owner rulings Q12 / Q13, 2026-11-18): a debris recording ends at
    /// its 60 s time limit, when its vessel is gone or leaves the physics bubble, or when the
    /// recording it is anchored to stops being written; time warp putting the parent on rails
    /// does not end it.
    /// </summary>
    [Collection("Sequential")]
    public class DebrisLifetimeTests : IDisposable
    {
        public DebrisLifetimeTests()
        {
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
        }

        private static BackgroundRecorder.BackgroundRecordingEndReason? Classify(
            double currentUT = 100.0,
            double expiryUT = 160.0,
            bool vesselFound = true,
            bool vesselLoaded = true,
            bool hasParentAnchor = true,
            bool parentRecordingFound = true,
            bool parentRecordingClosed = false,
            bool parentVesselFound = true,
            bool parentVesselLoaded = true,
            bool parentVesselPacked = false)
        {
            return BackgroundRecorder.ClassifyDebrisTtlEntry(
                currentUT, expiryUT, vesselFound, vesselLoaded, hasParentAnchor,
                parentRecordingFound, parentRecordingClosed, parentVesselFound,
                parentVesselLoaded, parentVesselPacked);
        }

        [Fact]
        public void ParentPackedByTimeWarp_DebrisKeepsRecording()
        {
            // DEBRIS-ENDS-WHEN-PARENT-GOES-ON-RAILS: the parent packing for time warp ended
            // every parent-anchored debris recording at once, cutting a booster's fall short.
            Assert.Null(Classify(parentVesselLoaded: true, parentVesselPacked: true));
        }

        [Fact]
        public void ParentUnloaded_DebrisKeepsRecording()
        {
            Assert.Null(Classify(parentVesselLoaded: false, parentVesselPacked: true));
        }

        [Fact]
        public void ParentVesselGone_EndsDebris()
        {
            Assert.Equal(BackgroundRecorder.BackgroundRecordingEndReason.ParentVesselDestroyed,
                Classify(parentVesselFound: false, parentVesselLoaded: false));
        }

        [Fact]
        public void TimeLimit_EndsDebrisEvenWhileParentIsOnRails()
        {
            Assert.Equal(BackgroundRecorder.BackgroundRecordingEndReason.TtlExpired,
                Classify(currentUT: 160.0, parentVesselPacked: true));
        }

        [Fact]
        public void DebrisVesselGone_EndsAsDestroyed()
        {
            Assert.Equal(BackgroundRecorder.BackgroundRecordingEndReason.VesselDestroyedOrDespawned,
                Classify(vesselFound: false, vesselLoaded: false));
        }

        [Fact]
        public void DebrisLeftPhysicsBubble_Ends()
        {
            Assert.Equal(BackgroundRecorder.BackgroundRecordingEndReason.LeftPhysicsBubble,
                Classify(vesselLoaded: false));
        }

        [Fact]
        public void ParentRecordingMissingOrClosed_Ends()
        {
            Assert.Equal(BackgroundRecorder.BackgroundRecordingEndReason.ParentRecordingMissing,
                Classify(parentRecordingFound: false, parentVesselFound: false));
            Assert.Equal(BackgroundRecorder.BackgroundRecordingEndReason.ParentRecordingClosedOrSuperseded,
                Classify(parentRecordingClosed: true, parentVesselFound: false));
        }

        [Fact]
        public void LegacyDebrisWithoutParentAnchor_KeepsDebrisOnlyChecks()
        {
            Assert.Null(Classify(hasParentAnchor: false, parentRecordingFound: false,
                parentVesselFound: false, parentVesselLoaded: false));
        }

        [Fact]
        public void NoTimeLimit_NeverEnds()
        {
            Assert.Null(Classify(expiryUT: double.NaN, vesselFound: false));
        }

        // ---------- owner ruling Q13: the lifetime follows the piece ----------

        private const uint DebrisPid = 4242u;

        /// <summary>
        /// A launched vessel (rec_root) that dropped a booster (rec_debris) at UT 100 through a
        /// JointBreak; the booster anchors to rec_root.
        /// </summary>
        private static RecordingTree MakeStagingTree(bool debris = true)
        {
            var tree = new RecordingTree
            {
                Id = "tree_q13",
                TreeName = "Q13",
                RootRecordingId = "rec_root",
                ActiveRecordingId = "rec_root",
                BranchPoints = new List<BranchPoint>(),
            };
            tree.Recordings["rec_root"] = new Recording
            {
                RecordingId = "rec_root",
                TreeId = tree.Id,
                VesselPersistentId = 1000u,
                ExplicitStartUT = 50.0,
            };
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp_stage",
                UT = 100.0,
                Type = BranchPointType.JointBreak,
                ParentRecordingIds = new List<string> { "rec_root" },
                ChildRecordingIds = new List<string> { "rec_debris" },
            });
            tree.Recordings["rec_debris"] = new Recording
            {
                RecordingId = "rec_debris",
                TreeId = tree.Id,
                VesselPersistentId = DebrisPid,
                ParentBranchPointId = "bp_stage",
                ExplicitStartUT = 100.0,
                IsDebris = debris,
                ParentAnchorRecordingId = "rec_root",
            };
            return tree;
        }

        private static void AddSwitchContinuation(RecordingTree tree, string fromRecId, string newRecId, double ut)
        {
            string bpId = "bp_switch_" + newRecId;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = bpId,
                UT = ut,
                Type = BranchPointType.VesselSwitchContinuation,
                ParentRecordingIds = new List<string> { fromRecId },
                ChildRecordingIds = new List<string> { newRecId },
            });
            tree.Recordings[fromRecId].ChildBranchPointId = bpId;
            tree.Recordings[newRecId] = new Recording
            {
                RecordingId = newRecId,
                TreeId = tree.Id,
                VesselPersistentId = DebrisPid,
                ParentBranchPointId = bpId,
                ExplicitStartUT = ut,
                IsDebris = true,
            };
        }

        [Fact]
        public void TryResolveExpiryUT_DebrisChild_IsSixtySecondsAfterTheSeparation()
        {
            var tree = MakeStagingTree();
            Assert.True(BackgroundRecorder.TryResolveDebrisExpiryUT(
                tree.Recordings["rec_debris"], tree, out double expiry, out string bpId, out string reason));
            Assert.Equal(160.0, expiry);
            Assert.Equal("bp_stage", bpId);
            Assert.Equal("separation", reason);
        }

        [Fact]
        public void TryResolveExpiryUT_SwitchContinuation_KeepsTheSeparationLifetime()
        {
            var tree = MakeStagingTree();
            AddSwitchContinuation(tree, "rec_debris", "rec_cont", 130.0);
            AddSwitchContinuation(tree, "rec_cont", "rec_cont2", 140.0);
            Assert.True(BackgroundRecorder.TryResolveDebrisExpiryUT(
                tree.Recordings["rec_cont2"], tree, out double expiry, out string bpId, out _));
            Assert.Equal(160.0, expiry);
            Assert.Equal("bp_stage", bpId);
        }

        [Fact]
        public void TryResolveExpiryUT_NotDebrisOrNoBranchPoint_HasNoLifetime()
        {
            var tree = MakeStagingTree(debris: false);
            Assert.False(BackgroundRecorder.TryResolveDebrisExpiryUT(
                tree.Recordings["rec_debris"], tree, out _, out _, out string reason));
            Assert.Equal("not-debris", reason);

            var tree2 = MakeStagingTree();
            tree2.Recordings["rec_debris"].ParentBranchPointId = "bp_missing";
            Assert.False(BackgroundRecorder.TryResolveDebrisExpiryUT(
                tree2.Recordings["rec_debris"], tree2, out _, out _, out reason));
            Assert.Equal("branch-point-missing", reason);
        }

        [Fact]
        public void TryResolveExpiryUT_ContinuationCycle_Refuses()
        {
            var tree = MakeStagingTree();
            AddSwitchContinuation(tree, "rec_debris", "rec_cont", 130.0);
            // Corrupt: the continuation point names itself as its parent.
            tree.BranchPoints[1].ParentRecordingIds[0] = "rec_cont";
            Assert.False(BackgroundRecorder.TryResolveDebrisExpiryUT(
                tree.Recordings["rec_cont"], tree, out _, out _, out string reason));
            Assert.Equal("lineage-cycle", reason);
        }

        [Fact]
        public void ActiveDebris_PromotedBooster_EndsAtItsSeparationLifetime()
        {
            // DEBRIS-LIFETIME-LOST-ON-PROMOTION: a booster the player switched to was promoted to
            // the active recorder, which had no time limit, so it recorded until it crashed.
            var tree = MakeStagingTree();
            tree.ActiveRecordingId = "rec_debris";

            Assert.False(ParsekFlight.EvaluateActiveDebrisLifetimeEnd(
                tree, recorderLive: true, handOffInProgress: false, currentUT: 159.9,
                out double expiry, out string reason));
            Assert.Equal("running", reason);
            Assert.Equal(160.0, expiry);

            Assert.True(ParsekFlight.EvaluateActiveDebrisLifetimeEnd(
                tree, recorderLive: true, handOffInProgress: false, currentUT: 160.0,
                out expiry, out reason));
            Assert.Equal("expired", reason);
        }

        [Fact]
        public void ActiveDebris_GuardsKeepTheRecording()
        {
            var tree = MakeStagingTree();
            Assert.False(ParsekFlight.EvaluateActiveDebrisLifetimeEnd(
                tree, true, false, 500.0, out _, out string reason));
            Assert.Equal("not-debris", reason); // rec_root is active

            tree.ActiveRecordingId = "rec_debris";
            Assert.False(ParsekFlight.EvaluateActiveDebrisLifetimeEnd(
                tree, recorderLive: false, handOffInProgress: false, currentUT: 500.0, out _, out reason));
            Assert.Equal("recorder-not-live", reason);
            Assert.False(ParsekFlight.EvaluateActiveDebrisLifetimeEnd(
                tree, recorderLive: true, handOffInProgress: true, currentUT: 500.0, out _, out reason));
            Assert.Equal("hand-off-in-progress", reason);

            tree.ActiveRecordingId = null;
            Assert.False(ParsekFlight.EvaluateActiveDebrisLifetimeEnd(
                tree, true, false, 500.0, out _, out reason));
            Assert.Equal("no-active-recording", reason);
        }

        [Fact]
        public void HandBackToBackground_RearmsTheSeparationLifetime()
        {
            // Promotion cancels the background deadline (D5-PROMOTED-DEBRIS-TTL-NOT-CANCELLED);
            // switching away again hands the piece back, which must re-arm the SAME deadline
            // rather than record the piece forever.
            var logLines = new List<string>();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;

            var tree = MakeStagingTree();
            tree.BackgroundMap[DebrisPid] = "rec_debris";
            var bg = new BackgroundRecorder(tree);
            bg.InjectDebrisTTLForTesting(DebrisPid, 160.0);

            bg.OnVesselRemovedFromBackground(DebrisPid);
            Assert.True(double.IsNaN(bg.GetDebrisTTLExpiryForTesting(DebrisPid)));

            tree.BackgroundMap[DebrisPid] = "rec_debris";
            bg.OnVesselBackgrounded(DebrisPid);

            Assert.Equal(160.0, bg.GetDebrisTTLExpiryForTesting(DebrisPid));
            Assert.Contains(logLines, l => l.Contains("[BgRecorder]")
                && l.Contains("Debris lifetime re-armed from the tree: pid=4242 rec=rec_debris")
                && l.Contains("expiryUT=160.0 separationBp=bp_stage context=backgrounded"));
        }

        [Fact]
        public void HandBackToBackground_NonDebris_ArmsNothing()
        {
            var tree = MakeStagingTree(debris: false);
            tree.BackgroundMap[DebrisPid] = "rec_debris";
            var bg = new BackgroundRecorder(tree);
            bg.OnVesselBackgrounded(DebrisPid);
            Assert.Equal(0, bg.DebrisTTLCount);
        }

        [Fact]
        public void EndDebrisRecordingForLifetime_EndsTheHandedBackPiece()
        {
            var logLines = new List<string>();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            var tree = MakeStagingTree();
            tree.BackgroundMap[DebrisPid] = "rec_debris";
            var bg = new BackgroundRecorder(tree);
            bg.OnVesselBackgrounded(DebrisPid);

            Assert.True(bg.EndDebrisRecordingForLifetime(DebrisPid, 160.0, 160.2, "active-recorder"));

            Assert.False(tree.BackgroundMap.ContainsKey(DebrisPid));
            Assert.Equal(0, bg.DebrisTTLCount);
            Assert.True(tree.Recordings["rec_debris"].TerminalStateValue.HasValue);
            Assert.Contains(logLines, l => l.Contains("[BgRecorder]")
                && l.Contains("Debris lifetime over for a piece handed back from the active recorder")
                && l.Contains("vesselPid=4242 expiry=160.0 currentUT=160.2 context=active-recorder"));

            Assert.False(bg.EndDebrisRecordingForLifetime(DebrisPid, 160.0, 160.3, "again"));
        }

        [Fact]
        public void CheckDebrisTTL_ReturnsThePidsItEnded()
        {
            // The flight host disarms a post-switch watch armed on a piece whose recording ended.
            var tree = MakeStagingTree();
            tree.BackgroundMap[DebrisPid] = "rec_debris";
            var bg = new BackgroundRecorder(tree);
            bg.InjectDebrisTTLForTesting(DebrisPid, 160.0);
            bg.InjectDebrisTTLForTesting(7u, double.NaN);

            List<uint> ended = bg.CheckDebrisTTL(170.0);

            Assert.Equal(new List<uint> { DebrisPid }, ended);
            Assert.Empty(new BackgroundRecorder(MakeStagingTree()).CheckDebrisTTL(170.0));
        }

        [Fact]
        public void SwitchContinuation_OfLiveDebris_StaysDebris()
        {
            var tree = MakeStagingTree();
            var result = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                tree, "rec_debris", DebrisPid, "Booster", 130.0,
                SwitchSegmentEntryReason.MapSwitchTo, Guid.NewGuid(), Guid.NewGuid(),
                "rec_cont", "bp_switch",
                ut => new TrajectoryPoint { ut = ut, bodyName = "Kerbin" });

            Assert.True(result.Created, result.FailureReason);
            Recording cont = tree.Recordings["rec_cont"];
            Assert.True(cont.IsDebris);
            Assert.Equal("rec_root", cont.ParentAnchorRecordingId);
            Assert.True(BackgroundRecorder.TryResolveDebrisExpiryUT(cont, tree, out double expiry, out _, out _));
            Assert.Equal(160.0, expiry);
        }

        [Fact]
        public void SwitchContinuation_OfDebrisPastItsLifetime_IsAPlainSegment()
        {
            var tree = MakeStagingTree();
            var result = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                tree, "rec_debris", DebrisPid, "Booster", 400.0,
                SwitchSegmentEntryReason.MapSwitchTo, Guid.NewGuid(), Guid.NewGuid(),
                "rec_cont", "bp_switch",
                ut => new TrajectoryPoint { ut = ut, bodyName = "Kerbin" });

            Assert.True(result.Created, result.FailureReason);
            Recording cont = tree.Recordings["rec_cont"];
            Assert.False(cont.IsDebris);
            Assert.Null(cont.ParentAnchorRecordingId);
        }
    }
}
