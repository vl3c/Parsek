using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The flight-ready restore an outside-FLIGHT load schedules for a parked Limbo tree and
    /// then commits in the same load (residual (d) of AUTOMERGE-ON-BY-DEFAULT): the schedule
    /// is a static that outlives the load, so it must not survive the commit of the tree it
    /// was armed for, or the next FLIGHT entry defers its Fly intent to a restore that has
    /// nothing to restore.
    /// </summary>
    public partial class ParsekScenarioOnLoadStepOrderTests
    {
        private const string LimboTreeId = "tree_limbo_schedule";
        private const string LimboTreeName = "Limbo Schedule";
        private const string LimboRecordingId = "rec_limbo_schedule";

        // catches: the in-session limbo dispatch arming a Quickload restore at SPACECENTER
        // and the same load's outside-flight auto-commit leaving it armed.
        [Fact]
        public void InSessionSpaceCenterLoad_CommitsTheLimboTree_AndDropsTheScheduledRestore()
        {
            var after = new ScheduleProbe();
            var scenario = new Scenario
            {
                Kind = "InSessionOther", Scene = GameScenes.SPACECENTER,
                LastOnSaveScene = GameScenes.FLIGHT,
                Arm = () => PendingTreeSlot.StashPendingTree(
                    MakeLimboTree(withActiveRecording: true), PendingTreeState.Limbo),
                AfterLoad = after.Capture,
            };

            try
            {
                List<string> rows = RunScenario(scenario);

                Assert.DoesNotContain(rows, r => r.StartsWith("threw ", StringComparison.Ordinal));
                Assert.Contains(logLines, l => l.Contains("[Scenario]")
                    && l.Contains("OnLoad: pending-Limbo tree '" + LimboTreeName
                        + "' deferred to OnFlightReady for quickload-resume"));
                Assert.Contains(logLines, l => l.Contains("[Scenario]")
                    && l.Contains("autocommit-outside-flight (scene-exit): entry tree='" + LimboTreeName + "'"));
                Assert.False(after.HasPendingTree, "the auto-commit left the Limbo tree pending");
                Assert.Equal(ParsekScenario.ActiveTreeRestoreMode.None, after.Mode);
                Assert.Contains(logLines, l => l.Contains("[INFO][Scenario]")
                    && l.Contains("autocommit-outside-flight (scene-exit): dropped the flight-ready "
                        + "Quickload restore scheduled for the committed tree '" + LimboTreeName + "'"));
            }
            finally
            {
                ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady =
                    ParsekScenario.ActiveTreeRestoreMode.None;
            }
        }

        // catches: the cold active-tree restore arming a restore for an isActive tree loaded at
        // SPACECENTER and the cold outside-flight auto-commit leaving it armed.
        [Fact]
        public void ColdSpaceCenterLoad_CommitsTheActiveTreeNode_AndDropsTheScheduledRestore()
        {
            var after = new ScheduleProbe();
            var scenario = new Scenario
            {
                Kind = "Cold", Scene = GameScenes.SPACECENTER, PrimeWithColdLoad = false,
                SaveNode = () =>
                {
                    var node = new ConfigNode("SCENARIO");
                    ConfigNode treeNode = node.AddNode("RECORDING_TREE");
                    MakeLimboTree(withActiveRecording: true).Save(treeNode);
                    treeNode.AddValue("isActive", "True");
                    return node;
                },
                AfterLoad = after.Capture,
            };

            try
            {
                List<string> rows = RunScenario(scenario);

                Assert.DoesNotContain(rows, r => r.StartsWith("threw ", StringComparison.Ordinal));
                Assert.Contains(logLines, l => l.Contains("[Scenario]")
                    && l.Contains("OnLoad: cold-start active tree detected (state=Limbo)"));
                Assert.Contains(logLines, l => l.Contains("[Scenario]")
                    && l.Contains("autocommit-outside-flight (cold-load outside-flight): entry tree='"
                        + LimboTreeName + "'"));
                Assert.False(after.HasPendingTree, "the auto-commit left the restored tree pending");
                Assert.Equal(ParsekScenario.ActiveTreeRestoreMode.None, after.Mode);
                Assert.Contains(logLines, l => l.Contains("[INFO][Scenario]")
                    && l.Contains("autocommit-outside-flight (cold-load outside-flight): dropped the "
                        + "flight-ready Quickload restore scheduled for the committed tree '"
                        + LimboTreeName + "'"));
            }
            finally
            {
                ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady =
                    ParsekScenario.ActiveTreeRestoreMode.None;
            }
        }

        // catches: the drop keyed on "the commit ran" rather than on the slot, so a commit
        // that threw with the tree still stashed loses the restore the next FLIGHT entry needs.
        [Fact]
        public void InSessionSpaceCenterLoad_CommitThrows_KeepsTheScheduledRestore()
        {
            harmony.Patch(AutoCommitTreeGhostOnlyMethod,
                prefix: new HarmonyMethod(typeof(ParsekScenarioOnLoadStepOrderTests),
                    nameof(ThrowingCommitPrefix)));
            var after = new ScheduleProbe();
            var scenario = new Scenario
            {
                Kind = "InSessionOther", Scene = GameScenes.SPACECENTER,
                LastOnSaveScene = GameScenes.FLIGHT,
                Arm = () => PendingTreeSlot.StashPendingTree(
                    MakeLimboTree(withActiveRecording: true), PendingTreeState.Limbo),
                AfterLoad = after.Capture,
            };

            try
            {
                List<string> rows = RunScenario(scenario);

                Assert.DoesNotContain(rows, r => r.StartsWith("threw ", StringComparison.Ordinal));
                Assert.Contains(logLines, l => l.Contains("[ERROR][Scenario]")
                    && l.Contains("AutoCommitPendingTreeOutsideFlight (scene-exit) threw InvalidOperationException"));
                Assert.True(after.HasPendingTree, "the throwing commit should leave the tree stashed");
                Assert.Equal(ParsekScenario.ActiveTreeRestoreMode.Quickload, after.Mode);
                Assert.Contains(logLines, l => l.Contains("[INFO][Scenario]")
                    && l.Contains("autocommit-outside-flight (scene-exit): kept the flight-ready Quickload restore"));
                Assert.DoesNotContain(logLines, l => l.Contains("dropped the flight-ready"));
            }
            finally
            {
                ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady =
                    ParsekScenario.ActiveTreeRestoreMode.None;
            }
        }

        [Theory]
        [InlineData((int)ParsekScenario.ActiveTreeRestoreMode.None, false, false)]
        [InlineData((int)ParsekScenario.ActiveTreeRestoreMode.None, true, false)]
        [InlineData((int)ParsekScenario.ActiveTreeRestoreMode.Quickload, false, true)]
        [InlineData((int)ParsekScenario.ActiveTreeRestoreMode.Quickload, true, false)]
        [InlineData((int)ParsekScenario.ActiveTreeRestoreMode.VesselSwitch, false, true)]
        [InlineData((int)ParsekScenario.ActiveTreeRestoreMode.VesselSwitch, true, false)]
        public void ShouldDropScheduledRestoreAfterOutsideFlightCommit_TruthTable(
            int mode, bool hasPendingAfter, bool expected)
        {
            Assert.Equal(expected,
                ParsekScenario.ShouldDropScheduledRestoreAfterOutsideFlightCommit(
                    (ParsekScenario.ActiveTreeRestoreMode)mode, hasPendingAfter));
        }

        /// <summary>The first call of the auto-commit's ghost-only route, the route a headless
        /// load takes (no settings, so autoMerge reads off).</summary>
        internal static System.Reflection.MethodInfo AutoCommitTreeGhostOnlyMethod =>
            AccessTools.Method(typeof(ParsekScenario), nameof(ParsekScenario.AutoCommitTreeGhostOnly));

        public static bool ThrowingCommitPrefix()
        {
            throw new InvalidOperationException("forced commit failure");
        }

        private sealed class ScheduleProbe
        {
            internal ParsekScenario.ActiveTreeRestoreMode Mode;
            internal bool HasPendingTree;

            internal void Capture()
            {
                Mode = ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady;
                HasPendingTree = PendingTreeSlot.HasPendingTree;
            }
        }

        private static RecordingTree MakeLimboTree(bool withActiveRecording)
        {
            var tree = new RecordingTree
            {
                Id = LimboTreeId,
                TreeName = LimboTreeName,
                RootRecordingId = LimboRecordingId,
                ActiveRecordingId = withActiveRecording ? LimboRecordingId : null,
            };
            var rec = new Recording
            {
                RecordingId = LimboRecordingId,
                VesselName = LimboTreeName,
                TreeId = LimboTreeId,
                ExplicitStartUT = 50.0,
                ExplicitEndUT = 90.0,
                VesselPersistentId = 4242,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 50.0, altitude = 5000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 90.0, altitude = 80000.0 });
            tree.AddOrReplaceRecording(rec);
            return tree;
        }
    }
}
