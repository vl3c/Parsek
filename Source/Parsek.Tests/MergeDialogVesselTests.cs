using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for MergeDialog.CanPersistVessel and BuildDefaultVesselDecisions.
    /// </summary>
    [Collection("Sequential")]
    public class MergeDialogVesselTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MergeDialogVesselTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
        }

        // ============================================================
        // CanPersistVessel
        // ============================================================

        [Fact]
        public void CanPersistVessel_Orbiting_ReturnsTrue()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            Assert.True(MergeDialog.CanPersistVessel(rec));
        }

        [Fact]
        public void CanPersistVessel_Landed_ReturnsTrue()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Landed,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            Assert.True(MergeDialog.CanPersistVessel(rec));
        }

        [Fact]
        public void CanPersistVessel_Destroyed_ReturnsFalse()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Destroyed,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            Assert.False(MergeDialog.CanPersistVessel(rec));
        }

        [Fact]
        public void CanPersistVessel_Recovered_ReturnsFalse()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Recovered,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            Assert.False(MergeDialog.CanPersistVessel(rec));
        }

        [Fact]
        public void CanPersistVessel_Docked_ReturnsFalse()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Docked,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            Assert.False(MergeDialog.CanPersistVessel(rec));
        }

        [Fact]
        public void CanPersistVessel_NullTerminalState_ReturnsTrue()
        {
            var rec = new Recording
            {
                TerminalStateValue = null,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            Assert.True(MergeDialog.CanPersistVessel(rec));
        }

        [Fact]
        public void CanPersistVessel_NoVesselSnapshot_ReturnsFalse()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = null
            };
            Assert.False(MergeDialog.CanPersistVessel(rec));
        }

        [Fact]
        public void CanPersistVessel_NullRecording_ReturnsFalse()
        {
            Assert.False(MergeDialog.CanPersistVessel(null));
        }

        [Fact]
        public void CanPersistVessel_Boarded_ReturnsFalse()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Boarded,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            Assert.False(MergeDialog.CanPersistVessel(rec));
        }

        [Fact]
        public void CanPersistVessel_Splashed_ReturnsTrue()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Splashed,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            Assert.True(MergeDialog.CanPersistVessel(rec));
        }

        [Fact]
        public void CanPersistVessel_SubOrbital_ReturnsFalse()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.SubOrbital,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            Assert.False(MergeDialog.CanPersistVessel(rec));
        }

        [Fact]
        public void CanPersistVessel_Debris_ReturnsFalse()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = new ConfigNode("VESSEL"),
                IsDebris = true
            };
            Assert.False(MergeDialog.CanPersistVessel(rec));
        }

        [Fact]
        public void CanPersistVessel_ActiveNonLeafEffectiveLeafInPendingTree_ReturnsTrue()
        {
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "Kerbal X",
                RootRecordingId = "active",
                ActiveRecordingId = "active"
            };

            var active = new Recording
            {
                RecordingId = "active",
                TreeId = "tree-1",
                VesselName = "Kerbal X",
                VesselPersistentId = 100,
                ChildBranchPointId = "bp-1",
                TerminalStateValue = TerminalState.Landed,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            var debris = new Recording
            {
                RecordingId = "debris",
                TreeId = "tree-1",
                VesselName = "Kerbal X Debris",
                VesselPersistentId = 200,
                ParentBranchPointId = "bp-1",
                TerminalStateValue = TerminalState.Destroyed,
                VesselSnapshot = new ConfigNode("VESSEL"),
                IsDebris = true
            };
            var bp = new BranchPoint
            {
                Id = "bp-1",
                Type = BranchPointType.Breakup,
                UT = 10.0
            };
            bp.ParentRecordingIds.Add("active");
            bp.ChildRecordingIds.Add("debris");

            tree.Recordings["active"] = active;
            tree.Recordings["debris"] = debris;
            tree.BranchPoints.Add(bp);

            Assert.True(MergeDialog.CanPersistVessel(active, tree));
        }

        [Fact]
        public void CanPersistVessel_ActiveNonLeafWithSamePidContinuationInPendingTree_ReturnsFalse()
        {
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "Kerbal X",
                RootRecordingId = "active",
                ActiveRecordingId = "active"
            };

            var active = new Recording
            {
                RecordingId = "active",
                TreeId = "tree-1",
                VesselName = "Kerbal X",
                VesselPersistentId = 100,
                ChildBranchPointId = "bp-1",
                TerminalStateValue = TerminalState.Landed,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            var continuation = new Recording
            {
                RecordingId = "continuation",
                TreeId = "tree-1",
                VesselName = "Kerbal X",
                VesselPersistentId = 100,
                ParentBranchPointId = "bp-1",
                TerminalStateValue = TerminalState.Landed,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            var bp = new BranchPoint
            {
                Id = "bp-1",
                Type = BranchPointType.JointBreak,
                UT = 10.0
            };
            bp.ParentRecordingIds.Add("active");
            bp.ChildRecordingIds.Add("continuation");

            tree.Recordings["active"] = active;
            tree.Recordings["continuation"] = continuation;
            tree.BranchPoints.Add(bp);

            Assert.False(MergeDialog.CanPersistVessel(active, tree));
        }

        // ============================================================
        // BuildDefaultVesselDecisions
        // ============================================================

        [Fact]
        public void BuildDefaultVesselDecisions_TwoSurvivingOneDestroyed()
        {
            var tree = new RecordingTree { TreeName = "TestTree" };
            tree.Recordings["r1"] = new Recording
            {
                RecordingId = "r1",
                VesselName = "Capsule",
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            tree.Recordings["r2"] = new Recording
            {
                RecordingId = "r2",
                VesselName = "Booster",
                TerminalStateValue = TerminalState.Destroyed,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            tree.Recordings["r3"] = new Recording
            {
                RecordingId = "r3",
                VesselName = "Payload",
                TerminalStateValue = TerminalState.Landed,
                VesselSnapshot = new ConfigNode("VESSEL")
            };

            var decisions = MergeDialog.BuildDefaultVesselDecisions(tree);

            Assert.Equal(3, decisions.Count);
            Assert.True(decisions["r1"]);   // Capsule orbiting: persist
            Assert.False(decisions["r2"]);  // Booster destroyed: ghost-only
            Assert.True(decisions["r3"]);   // Payload landed: persist

            // Verify logging
            Assert.Contains(logLines, l => l.Contains("[MergeDialog]") && l.Contains("canPersist=True") && l.Contains("Capsule"));
            Assert.Contains(logLines, l => l.Contains("[MergeDialog]") && l.Contains("canPersist=False") && l.Contains("Booster"));
            Assert.Contains(logLines, l => l.Contains("[MergeDialog]") && l.Contains("canPersist=True") && l.Contains("Payload"));
        }

        [Fact]
        public void BuildDefaultVesselDecisions_EmptyTree_ReturnsEmptyDict()
        {
            var tree = new RecordingTree { TreeName = "Empty" };
            var decisions = MergeDialog.BuildDefaultVesselDecisions(tree);
            Assert.Empty(decisions);
        }

        [Fact]
        public void BuildDefaultVesselDecisions_NullTree_ReturnsEmptyDict()
        {
            var decisions = MergeDialog.BuildDefaultVesselDecisions(null);
            Assert.Empty(decisions);
        }

        [Fact]
        public void BuildDefaultVesselDecisions_AllDestroyed_AllGhostOnly()
        {
            var tree = new RecordingTree { TreeName = "Doomed" };
            tree.Recordings["r1"] = new Recording
            {
                RecordingId = "r1",
                VesselName = "Ship1",
                TerminalStateValue = TerminalState.Destroyed,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            tree.Recordings["r2"] = new Recording
            {
                RecordingId = "r2",
                VesselName = "Ship2",
                TerminalStateValue = TerminalState.Destroyed,
                VesselSnapshot = new ConfigNode("VESSEL")
            };

            var decisions = MergeDialog.BuildDefaultVesselDecisions(tree);

            Assert.Equal(2, decisions.Count);
            Assert.False(decisions["r1"]);
            Assert.False(decisions["r2"]);
        }

        [Fact]
        public void BuildDefaultVesselDecisions_DebrisAndSubOrbital_DefaultGhostOnly()
        {
            var tree = new RecordingTree { TreeName = "CrashDebris" };
            tree.Recordings["debris"] = new Recording
            {
                RecordingId = "debris",
                VesselName = "Booster Debris",
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = new ConfigNode("VESSEL"),
                IsDebris = true
            };
            tree.Recordings["suborbital"] = new Recording
            {
                RecordingId = "suborbital",
                VesselName = "Capsule",
                TerminalStateValue = TerminalState.SubOrbital,
                VesselSnapshot = new ConfigNode("VESSEL")
            };

            var decisions = MergeDialog.BuildDefaultVesselDecisions(tree);

            Assert.Equal(2, decisions.Count);
            Assert.False(decisions["debris"]);
            Assert.False(decisions["suborbital"]);
        }

        [Fact]
        public void BuildDefaultVesselDecisions_SkipsNonLeafRecordings()
        {
            // Non-leaf recordings have ChildBranchPointId != null and are not
            // returned by GetAllLeaves, so they should not appear in decisions.
            var tree = new RecordingTree { TreeName = "Branched" };
            tree.Recordings["root"] = new Recording
            {
                RecordingId = "root",
                VesselName = "Root",
                ChildBranchPointId = "bp1",  // Not a leaf
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            tree.Recordings["leaf"] = new Recording
            {
                RecordingId = "leaf",
                VesselName = "Leaf",
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = new ConfigNode("VESSEL")
            };

            var decisions = MergeDialog.BuildDefaultVesselDecisions(tree);

            Assert.Single(decisions);
            Assert.True(decisions.ContainsKey("leaf"));
            Assert.False(decisions.ContainsKey("root"));
        }

        [Fact]
        public void BuildDefaultVesselDecisions_ActiveNonLeafEffectiveLeaf_DefaultsToPersist()
        {
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "Kerbal X",
                RootRecordingId = "active",
                ActiveRecordingId = "active"
            };

            var active = new Recording
            {
                RecordingId = "active",
                TreeId = "tree-1",
                VesselName = "Kerbal X",
                VesselPersistentId = 100,
                ChildBranchPointId = "bp-1",
                TerminalStateValue = TerminalState.Landed,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            var debris = new Recording
            {
                RecordingId = "debris",
                TreeId = "tree-1",
                VesselName = "Kerbal X Debris",
                VesselPersistentId = 200,
                ParentBranchPointId = "bp-1",
                TerminalStateValue = TerminalState.Destroyed,
                VesselSnapshot = new ConfigNode("VESSEL"),
                IsDebris = true
            };
            var bp = new BranchPoint
            {
                Id = "bp-1",
                Type = BranchPointType.Breakup,
                UT = 10.0
            };
            bp.ParentRecordingIds.Add("active");
            bp.ChildRecordingIds.Add("debris");

            tree.Recordings["active"] = active;
            tree.Recordings["debris"] = debris;
            tree.BranchPoints.Add(bp);

            var decisions = MergeDialog.BuildDefaultVesselDecisions(tree);

            Assert.Equal(2, decisions.Count);
            Assert.True(decisions["active"]);
            Assert.False(decisions["debris"]);
            Assert.Contains(logLines, l =>
                l.Contains("active-nonleaf='active'") &&
                l.Contains("canPersist=True"));
        }

        // MarkForceSpawnOnTreeRecordings tests removed — spawn dedup bypass is now
        // stateless via RecordingStore.SceneEntryActiveVesselPid (#226).

        // ============================================================
        // BuildDefaultVesselDecisions: Re-Fly suppressed-subtree pass
        // (refly-suppressed-non-leaf bug fix)
        // ============================================================

        /// <summary>
        /// Bug fix (refly-suppressed-non-leaf): a parent recording inside the
        /// session-suppressed subtree that has child branch points (so it is
        /// NOT a leaf) used to retain its <c>VesselSnapshot</c> through the
        /// merge dialog because <c>BuildDefaultVesselDecisions</c> only walked
        /// <c>GetAllLeaves()</c>. Later <c>GhostPlaybackLogic.ShouldSpawnAtRecordingEnd</c>
        /// then spawned the parent as a real clickable vessel alongside its
        /// playback ghost. With the fix, every non-leaf id in the suppression
        /// closure is force-marked ghost-only — except the active Re-Fly
        /// target itself, which must stay spawnable.
        /// </summary>
        [Fact]
        public void BuildDefaultVesselDecisions_SuppressedNonLeafForcedGhostOnly()
        {
            // Tree shape:
            //   parent (non-leaf, has child bp)  --bp1-->  child (leaf, suppressed)
            //   active (the live Re-Fly target; in-place continuation, also in closure)
            // The suppression closure contains parent + child + active. Only
            // active must remain spawnable; parent and child must be flipped
            // to ghost-only.
            var tree = new RecordingTree { TreeName = "Branched" };
            tree.Recordings["parent"] = new Recording
            {
                RecordingId = "parent",
                VesselName = "Kerbal X Upper",
                ChildBranchPointId = "bp1",  // Not a leaf
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            tree.Recordings["child"] = new Recording
            {
                RecordingId = "child",
                VesselName = "Kerbal X Probe",
                ParentBranchPointId = "bp1",
                TerminalStateValue = TerminalState.Destroyed,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            tree.Recordings["active"] = new Recording
            {
                RecordingId = "active",
                VesselName = "Kerbal X Booster",
                TerminalStateValue = TerminalState.Landed,
                VesselSnapshot = new ConfigNode("VESSEL")
            };

            var suppressed = new HashSet<string>(System.StringComparer.Ordinal)
            {
                "parent",
                "child",
                "active",
            };

            var decisions = MergeDialog.BuildDefaultVesselDecisions(
                tree, suppressed, activeReFlyTargetId: "active");

            // active stays as a regular leaf decision (spawnable, since
            // terminal=Landed) and is NOT flipped to ghost-only by the
            // suppression pass.
            Assert.True(decisions.ContainsKey("active"));
            Assert.True(decisions["active"]);
            // child was a leaf and is in suppression -> ghost-only.
            Assert.True(decisions.ContainsKey("child"));
            Assert.False(decisions["child"]);
            // parent was a non-leaf, never visited by GetAllLeaves(), and
            // must now be forced ghost-only by the new suppressed-subtree pass.
            Assert.True(decisions.ContainsKey("parent"));
            Assert.False(decisions["parent"]);

            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("forcing ghost-only on suppressed")
                && l.Contains("id='parent'"));
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("keeping active Re-Fly target spawnable")
                && l.Contains("id='active'"));
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("suppressed-subtree pass complete")
                && l.Contains("forcedGhostOnly=1")
                && l.Contains("skippedActiveTarget=1"));
        }

        /// <summary>
        /// When no Re-Fly session is active (or the closure is empty), the
        /// suppression pass is a no-op and the leaf-only behaviour is
        /// unchanged.
        /// </summary>
        [Fact]
        public void BuildDefaultVesselDecisions_NullSuppression_LeafBehaviourUnchanged()
        {
            var tree = new RecordingTree { TreeName = "Plain" };
            tree.Recordings["parent"] = new Recording
            {
                RecordingId = "parent",
                VesselName = "Parent",
                ChildBranchPointId = "bp1",  // Not a leaf
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            tree.Recordings["child"] = new Recording
            {
                RecordingId = "child",
                VesselName = "Child",
                ParentBranchPointId = "bp1",
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = new ConfigNode("VESSEL")
            };

            var decisions = MergeDialog.BuildDefaultVesselDecisions(
                tree, suppressedRecordingIds: null, activeReFlyTargetId: null);

            // Only the leaf 'child' shows up.
            Assert.Single(decisions);
            Assert.True(decisions.ContainsKey("child"));
            Assert.False(decisions.ContainsKey("parent"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[MergeDialog]") && l.Contains("forcing ghost-only on suppressed"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[MergeDialog]") && l.Contains("suppressed-subtree pass complete"));
        }

        /// <summary>
        /// Closure ids that do not exist in the pending tree are counted but
        /// produce no decision changes — the suppressed-subtree closure is
        /// computed against committed recordings and may name records the
        /// pending tree never knew about.
        /// </summary>
        [Fact]
        public void BuildDefaultVesselDecisions_SuppressedIdNotInTree_CountedAndIgnored()
        {
            var tree = new RecordingTree { TreeName = "OnlyOne" };
            tree.Recordings["leaf"] = new Recording
            {
                RecordingId = "leaf",
                VesselName = "OnlyLeaf",
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            var suppressed = new HashSet<string>(System.StringComparer.Ordinal)
            {
                "ghost-id-not-in-tree",
            };

            var decisions = MergeDialog.BuildDefaultVesselDecisions(
                tree, suppressed, activeReFlyTargetId: null);

            Assert.Single(decisions);
            Assert.True(decisions["leaf"]);
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("suppressed-subtree pass complete")
                && l.Contains("notInTree=1")
                && l.Contains("forcedGhostOnly=0"));
        }

        /// <summary>
        /// Active Re-Fly target sitting in the suppression closure must keep
        /// its leaf-derived spawnable decision even when its terminal state
        /// would normally make it ghost-only — but only if it stays
        /// genuinely spawnable per <c>CanPersistVessel</c>. Conversely, when
        /// the closure does not include it, the suppression pass leaves its
        /// existing decision alone. Verify the explicit "keeping active
        /// Re-Fly target spawnable" log line fires only when the active id
        /// is in the closure AND is the named target.
        /// </summary>
        [Fact]
        public void BuildDefaultVesselDecisions_ActiveTargetNotInClosure_NoSkipLog()
        {
            var tree = new RecordingTree { TreeName = "Multi" };
            tree.Recordings["parent"] = new Recording
            {
                RecordingId = "parent",
                VesselName = "Parent",
                ChildBranchPointId = "bp1",
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            tree.Recordings["leaf"] = new Recording
            {
                RecordingId = "leaf",
                VesselName = "Leaf",
                ParentBranchPointId = "bp1",
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            tree.Recordings["other"] = new Recording
            {
                RecordingId = "other",
                VesselName = "OtherTree",
                TerminalStateValue = TerminalState.Landed,
                VesselSnapshot = new ConfigNode("VESSEL")
            };

            // Suppression names parent + leaf; activeReFlyTargetId="other"
            // is NOT in the closure (still in the same tree, but excluded
            // from suppression — e.g. an unrelated leaf). The skip-active
            // log line must NOT fire because the active id never enters
            // the suppression loop.
            var suppressed = new HashSet<string>(System.StringComparer.Ordinal)
            {
                "parent",
                "leaf",
            };

            var decisions = MergeDialog.BuildDefaultVesselDecisions(
                tree, suppressed, activeReFlyTargetId: "other");

            Assert.False(decisions["parent"]);
            Assert.False(decisions["leaf"]);
            Assert.True(decisions["other"]);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("keeping active Re-Fly target spawnable"));
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("suppressed-subtree pass complete")
                && l.Contains("skippedActiveTarget=0"));
        }
    }
}
