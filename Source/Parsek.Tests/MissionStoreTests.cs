using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class MissionStoreTests : IDisposable
    {
        public MissionStoreTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            MissionStore.SuppressLogging = true;
            MissionStore.ResetForTesting();
        }

        public void Dispose()
        {
            MissionStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static RecordingTree Tree(string id, string name)
            => new RecordingTree { Id = id, TreeName = name };

        // A tree with a single continuous-vessel run, so MissionThroughLineBuilder
        // produces exactly one through-line whose head id == the root recording id.
        private static RecordingTree TreeWithThroughLineHead(
            string treeId, string headRecordingId)
        {
            var rec = new Recording
            {
                RecordingId = headRecordingId,
                VesselName = "V",
                ChainId = "C",
                ChainIndex = 0,
                ChainBranch = 0,
                IsDebris = false,
                ExplicitStartUT = 100,
                ExplicitEndUT = 200
            };
            var tree = new RecordingTree { Id = treeId, RootRecordingId = headRecordingId };
            tree.Recordings[headRecordingId] = rec;
            return tree;
        }

        private static Mission First() => new List<Mission>(MissionStore.Missions)[0];

        [Fact]
        public void EnsureDefaults_CreatesOnePerTree_AllIncluded_AndIsIdempotent()
        {
            var trees = new List<RecordingTree> { Tree("t1", "Kerbal X"), Tree("t2", "Mun Lander") };

            Assert.Equal(2, MissionStore.EnsureDefaultsForTrees(trees));
            Assert.Equal(2, MissionStore.Missions.Count);
            Assert.Equal(1, MissionStore.CountForTree("t1"));
            foreach (var m in MissionStore.Missions)
                Assert.Empty(m.ExcludedIntervalKeys); // default = everything included

            Assert.Equal(0, MissionStore.EnsureDefaultsForTrees(trees)); // idempotent
            Assert.Equal(2, MissionStore.Missions.Count);
        }

        private static ConfigNode MissionNode(string id, string treeId, string name, bool loop = false)
        {
            var n = new ConfigNode("MISSION");
            n.AddValue("id", id);
            n.AddValue("treeId", treeId ?? "");
            n.AddValue("name", name);
            n.AddValue("loopPlayback", loop);
            return n;
        }

        // catches: a legacy clone surviving a load. Older builds let the player clone a mission
        // onto the same tree (a clone was inserted after its source); the clone feature is gone
        // and a save that still holds one loads without it (owner ruling 2026-11-22), keeping
        // each tree's FIRST mission, looping or not, and writing the clones out on the next save.
        [Fact]
        public void Load_DropsLegacyClones_KeepsEachTreesFirstMission_AndLogs()
        {
            var logLines = new List<string>();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            MissionStore.SuppressLogging = false;

            var node = new ConfigNode("PARSEK");
            node.AddNode(MissionNode("m1", "t1", "Kerbal X"));
            node.AddNode(MissionNode("m1c", "t1", "Kerbal X copy", loop: true));
            node.AddNode(MissionNode("m2", "t2", "Mun Lander", loop: true));
            node.AddNode(MissionNode("m1c2", "t1", "Kerbal X copy 2"));

            MissionStore.Load(node);

            var ids = new List<string>();
            foreach (Mission m in MissionStore.Missions)
                ids.Add(m.Id);
            Assert.Equal(new[] { "m1", "m2" }, ids);
            Assert.False(MissionStore.FindById("m1").LoopPlayback); // the clone's loop goes with it
            Assert.True(MissionStore.FindById("m2").LoopPlayback);
            Assert.Contains(logLines, l => l.Contains("[Mission]")
                && l.Contains("DropLegacyClones: removed 2 legacy clone mission(s)")
                && l.Contains("'Kerbal X copy' (tree=t1)")
                && l.Contains("'Kerbal X copy 2' (tree=t1)"));
            Assert.Contains(logLines, l => l.Contains("Loaded 2 mission(s)"));

            // The next save writes the clones out for good.
            var saved = new ConfigNode("PARSEK");
            MissionStore.Save(saved);
            Assert.Equal(2, saved.GetNodes("MISSION").Length);
        }

        [Fact]
        public void Load_WithOneMissionPerTree_DropsNothing_AndLogsNoDrop()
        {
            var logLines = new List<string>();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            MissionStore.SuppressLogging = false;

            var node = new ConfigNode("PARSEK");
            node.AddNode(MissionNode("m1", "t1", "Kerbal X"));
            node.AddNode(MissionNode("m2", "t2", "Mun Lander"));

            MissionStore.Load(node);

            Assert.Equal(2, MissionStore.Missions.Count);
            Assert.Equal(0, MissionStore.DropLegacyClones());
            Assert.DoesNotContain(logLines, l => l.Contains("DropLegacyClones"));
        }

        // Missions with no tree id are not clones of anything: DropLegacyClones leaves them for
        // PruneOrphans, so each pass keeps its own reason in the log.
        [Fact]
        public void Load_LeavesTreelessMissionsForPruneOrphans()
        {
            var node = new ConfigNode("PARSEK");
            node.AddNode(MissionNode("m1", "t1", "Kerbal X"));
            node.AddNode(MissionNode("n1", null, "Stray A"));
            node.AddNode(MissionNode("n2", null, "Stray B"));

            MissionStore.Load(node);

            Assert.Equal(3, MissionStore.Missions.Count);
            Assert.Equal(2, MissionStore.PruneOrphans(new List<RecordingTree> { Tree("t1", "Kerbal X") }));
            Assert.Equal("m1", First().Id);
        }

        [Fact]
        public void PruneOrphans_RemovesMissionsForMissingTrees()
        {
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "X"), Tree("t2", "Y") });

            int removed = MissionStore.PruneOrphans(new List<RecordingTree> { Tree("t1", "X") });

            Assert.Equal(1, removed);
            Assert.Single(MissionStore.Missions);
            Assert.Equal(0, MissionStore.CountForTree("t2"));
        }

        [Fact]
        public void PruneOrphans_ProtectsMissionForParkedTreeId()
        {
            // Limbo-tree data-loss fix (second half): a tree parked as a quickload-resume
            // isActive node (restored into the pending slot LATER in OnLoad) is not in the
            // committed list when the mission reconcile runs. Its mission name + loop settings
            // must survive — additionalLiveTreeIds protects it from being pruned as an orphan.
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "X"), Tree("t2", "Duna Supply 1") });
            Mission parked = new List<Mission>(MissionStore.Missions).Find(m => m.TreeId == "t2");
            parked.LoopPlayback = true;
            parked.LoopIntervalSeconds = 123.0;

            // t2 is NOT committed, but it is parked (its isActive node restores later).
            int removed = MissionStore.PruneOrphans(
                new List<RecordingTree> { Tree("t1", "X") },
                additionalLiveTreeIds: new List<string> { "t2" });

            Assert.Equal(0, removed);
            Assert.Equal(2, MissionStore.Missions.Count);
            Mission survived = new List<Mission>(MissionStore.Missions).Find(m => m.TreeId == "t2");
            Assert.NotNull(survived);
            Assert.Equal("Duna Supply 1", survived.Name);    // custom name preserved
            Assert.True(survived.LoopPlayback);              // loop settings preserved
            Assert.Equal(123.0, survived.LoopIntervalSeconds);
        }

        [Fact]
        public void PruneOrphans_StillRemovesTrueOrphan_WhenParkedIdsGivenButDontMatch()
        {
            // The protection is scoped: a mission whose tree is in NEITHER the committed list
            // NOR the parked-id set is still a genuine orphan and is removed.
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "X"), Tree("t3", "Gone") });

            int removed = MissionStore.PruneOrphans(
                new List<RecordingTree> { Tree("t1", "X") },
                additionalLiveTreeIds: new List<string> { "t2" }); // protects t2, not t3

            Assert.Equal(1, removed);
            Assert.Equal(0, MissionStore.CountForTree("t3"));
        }

        [Fact]
        public void SaveLoad_RoundTripsNameTreeAndSelection()
        {
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "Kerbal X") });
            Mission m = First();
            m.ExcludedIntervalKeys.Add("legA");
            m.ExcludedIntervalKeys.Add("legB");
            m.LoopPlayback = true;
            m.LoopIntervalSeconds = 123.75;
            m.LoopTimeUnit = LoopTimeUnit.Hour;
            m.LoopAnchorUT = 54321.25;
            string savedId = m.Id;

            var node = new ConfigNode("PARSEK");
            MissionStore.Save(node);

            MissionStore.ResetForTesting();
            Assert.Empty(MissionStore.Missions);

            MissionStore.Load(node);

            Assert.Single(MissionStore.Missions);
            Mission loaded = First();
            Assert.Equal(savedId, loaded.Id);
            Assert.Equal("t1", loaded.TreeId);
            Assert.Equal(2, loaded.ExcludedIntervalKeys.Count);
            Assert.Contains("legA", loaded.ExcludedIntervalKeys);
            Assert.Contains("legB", loaded.ExcludedIntervalKeys);

            // Loop fields round-trip too, including the phase anchor.
            Assert.True(loaded.LoopPlayback);
            Assert.Equal(123.75, loaded.LoopIntervalSeconds);
            Assert.Equal(LoopTimeUnit.Hour, loaded.LoopTimeUnit);
            Assert.Equal(54321.25, loaded.LoopAnchorUT);
        }

        [Fact]
        public void SaveLoad_RoundTripsCollapsedFlag_UnderItsOwnName()
        {
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "Kerbal X") });
            First().Collapsed = true;

            var node = new ConfigNode("PARSEK");
            MissionStore.Save(node);
            ConfigNode saved = node.GetNodes("MISSION")[0];
            Assert.Equal("True", saved.GetValue("collapsed"));
            Assert.Null(saved.GetValue("archived"));

            MissionStore.ResetForTesting();
            MissionStore.Load(node);
            Assert.True(First().Collapsed);
        }

        [Fact]
        public void Load_MissingCollapseValues_DefaultToExpanded()
        {
            // Older saves (before the Archive column) carry neither key: the mission must load
            // expanded, not throw.
            var node = new ConfigNode("PARSEK");
            ConfigNode mNode = node.AddNode("MISSION");
            mNode.AddValue("id", "m1");
            mNode.AddValue("treeId", "t1");
            mNode.AddValue("name", "Kerbal X");

            MissionStore.Load(node);
            Assert.False(First().Collapsed);
        }

        [Fact]
        public void SaveLoad_RoundTripsExcludedIntervalKeys()
        {
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "Kerbal X") });
            Mission m = First();
            m.ExcludedIntervalKeys.Add("L");        // dropped launch interval
            m.ExcludedIntervalKeys.Add("L/seg1");   // dropped survivor too

            var node = new ConfigNode("PARSEK");
            MissionStore.Save(node);
            MissionStore.ResetForTesting();
            MissionStore.Load(node);

            Mission loaded = First();
            Assert.Equal(2, loaded.ExcludedIntervalKeys.Count);
            Assert.Contains("L", loaded.ExcludedIntervalKeys);
            Assert.Contains("L/seg1", loaded.ExcludedIntervalKeys);
        }

        [Fact]
        public void SaveLoad_UnsetAnchor_RoundTripsAsNaN()
        {
            // An unset anchor (NaN) must survive save/load as NaN, not silently become 0 (which would
            // anchor the span clock at UT 0 instead of falling back to spanStart in the adapter).
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "Kerbal X") });
            Mission m = First();
            Assert.True(double.IsNaN(m.LoopAnchorUT)); // default

            var node = new ConfigNode("PARSEK");
            MissionStore.Save(node);
            MissionStore.ResetForTesting();
            MissionStore.Load(node);

            Assert.True(double.IsNaN(First().LoopAnchorUT));
        }

        [Fact]
        public void SetLoopEnabled_On_KeepsLoopingMissionsOnOtherTrees()
        {
            MissionStore.EnsureDefaultsForTrees(
                new List<RecordingTree> { Tree("t1", "Kerbal X"), Tree("t2", "Mun Lander") });
            var all = new List<Mission>(MissionStore.Missions);
            Mission m1 = all.Find(m => m.TreeId == "t1");
            Mission m2 = all.Find(m => m.TreeId == "t2");

            // Loop the first mission, then loop the second: concurrent across distinct trees.
            MissionStore.SetLoopEnabled(m1, true, 1000.0);
            MissionStore.SetLoopEnabled(m2, true, 1100.0);

            Assert.True(m1.LoopPlayback);  // not cleared - different tree
            Assert.True(m2.LoopPlayback);
            Assert.Equal(1000.0, m1.LoopAnchorUT);
            Assert.Equal(1100.0, m2.LoopAnchorUT);
        }

        [Fact]
        public void SetLoopEnabled_Off_TurnsOnlyTargetOff()
        {
            MissionStore.EnsureDefaultsForTrees(
                new List<RecordingTree> { Tree("t1", "X"), Tree("t2", "Y") });
            var all = new List<Mission>(MissionStore.Missions);
            Mission a = all.Find(m => m.TreeId == "t1");
            Mission b = all.Find(m => m.TreeId == "t2");
            a.LoopPlayback = true;
            b.LoopPlayback = true;

            MissionStore.SetLoopEnabled(a, false, 1000.0);

            Assert.False(a.LoopPlayback);
            Assert.True(b.LoopPlayback); // unaffected
        }

        [Fact]
        public void SetLoopEnabled_On_StampsAnchorUT_AndReEnableReAnchors()
        {
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "X") });
            Mission a = First();
            Assert.True(double.IsNaN(a.LoopAnchorUT)); // unset before any enable

            // First enable stamps the anchor to the supplied UT.
            MissionStore.SetLoopEnabled(a, true, 1234.5);
            Assert.True(a.LoopPlayback);
            Assert.Equal(1234.5, a.LoopAnchorUT);

            // Disable then re-enable at a later UT re-anchors: the span clock will restart from the
            // recording start at the new anchor instead of resuming mid-mission.
            MissionStore.SetLoopEnabled(a, false, 2000.0);
            Assert.Equal(1234.5, a.LoopAnchorUT); // disable leaves the stale anchor in place

            MissionStore.SetLoopEnabled(a, true, 5555.5);
            Assert.True(a.LoopPlayback);
            Assert.Equal(5555.5, a.LoopAnchorUT); // re-enable overwrites it
        }

        [Fact]
        public void NormalizeOneLoopPerTree_KeepsOneLoopPerDistinctTree()
        {
            MissionStore.EnsureDefaultsForTrees(
                new List<RecordingTree> { Tree("t1", "Kerbal X"), Tree("t2", "Mun Lander") });
            var all = new List<Mission>(MissionStore.Missions);
            Mission m1 = all.Find(m => m.TreeId == "t1");
            Mission m2 = all.Find(m => m.TreeId == "t2");
            // Two looping missions on DISTINCT trees: both are valid concurrent loops.
            m1.LoopPlayback = true;
            m2.LoopPlayback = true;

            int cleared = MissionStore.NormalizeOneLoopPerTree();

            Assert.Equal(0, cleared);     // nothing to clear - one per tree already
            Assert.True(m1.LoopPlayback);
            Assert.True(m2.LoopPlayback);
        }

        [Fact]
        public void NormalizeOneLoopPerTree_NullTreeIdMissions_CollapseToOneSlot()
        {
            // A corrupt save could carry missions with no tree id. NormalizeOneLoopPerTree maps a
            // null TreeId to "" so all null-tree missions share ONE logical loop slot, while a
            // real-tree mission stays independent.
            var node = new ConfigNode("PARSEK");
            node.AddNode(MissionNode("real", "t1", "X", loop: true));
            node.AddNode(MissionNode("n1", null, "Stray A", loop: true));
            node.AddNode(MissionNode("n2", null, "Stray B", loop: true));
            MissionStore.Load(node);
            Mission real = MissionStore.FindById("real");
            Mission n1 = MissionStore.FindById("n1");
            Mission n2 = MissionStore.FindById("n2");

            int cleared = MissionStore.NormalizeOneLoopPerTree();

            // The real-tree mission is independent; the two null-tree missions collapse to one
            // logical "" slot, so exactly one of them is cleared (which one depends on list
            // order, so assert the invariant, not a specific survivor).
            Assert.Equal(1, cleared);
            Assert.True(real.LoopPlayback);                       // distinct (real) tree - kept
            Assert.True(n1.LoopPlayback ^ n2.LoopPlayback);       // exactly one null-tree survivor
        }

        // catches: the retired coarse per-through-line exclusion (todo GUI-D6) coming back. An
        // old save's "excludedHead" value must load as nothing (no field reads it), and must be
        // dropped on the next save rather than preserved verbatim as an unknown key.
        [Fact]
        public void RetiredExcludedHeadKey_IsIgnoredOnLoad_AndDroppedOnSave()
        {
            var missionNode = new ConfigNode("MISSION");
            missionNode.AddValue("id", "m1");
            missionNode.AddValue("treeId", "t1");
            missionNode.AddValue("name", "Kerbal X");
            missionNode.AddValue("excludedHead", "legA");
            missionNode.AddValue("excludedInterval", "keyB");

            Mission loaded = Mission.Load(missionNode);

            Assert.Contains("keyB", loaded.ExcludedIntervalKeys);
            Assert.Empty(loaded.UnknownValues);
            var saved = new ConfigNode("MISSION");
            loaded.Save(saved);
            Assert.Null(saved.GetValue("excludedHead"));
            Assert.Equal("keyB", saved.GetValue("excludedInterval"));
        }

        [Fact]
        public void ReconcileSelections_RemovesStaleIntervalKey_KeepsValidKey_AndWarns()
        {
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            MissionStore.SuppressLogging = false;

            const string headId = "headLeg";
            var tree = TreeWithThroughLineHead("t1", headId);
            // A genuine selectable composition key for this tree (the same set ReconcileSelections
            // validates against), so the "valid key survives" assertion does not hard-code an id.
            string validKey = FirstSelectableHeadLegId(
                MissionCompositionBuilder.Build(MissionStructureBuilder.Build(tree)));
            Assert.False(string.IsNullOrEmpty(validKey)); // sanity: the tree has a selectable interval

            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { tree });
            Mission m = First();
            m.ExcludedIntervalKeys.Add(validKey);            // valid: a current composition node
            m.ExcludedIntervalKeys.Add("bogusIntervalKey");  // stale: not a current composition key

            int removed = MissionStore.ReconcileSelections(new List<RecordingTree> { tree });

            Assert.Equal(1, removed);
            Assert.DoesNotContain("bogusIntervalKey", m.ExcludedIntervalKeys); // dropped
            Assert.Contains(validKey, m.ExcludedIntervalKeys);                 // survives
            Assert.Contains(logLines,
                l => l.Contains("[Mission]") && l.Contains("ReconcileSelections")
                  && l.Contains("stale interval key"));
        }

        // --- interval keys across a dock edge ---

        // launch -> dock -> undock tree whose composition mints the "launch@dock1" sub-interval
        // (selectable keys: launch, launch@dock1, launch/seg1, payload).
        private static RecordingTree DockTree(string treeId)
        {
            RecordingTree tree = new RecordingTree { Id = treeId, RootRecordingId = "launch" };
            var recs = new[]
            {
                MakeLeg("launch", "C", 0, 1000, 2000),
                MakeLeg("docked", "C2", 0, 2000, 3000),
                MakeLeg("survivor", "C3", 0, 3000, 4000),
                MakeLeg("payload", "C4", 0, 3000, 3500),
            };
            foreach (var r in recs) tree.Recordings[r.RecordingId] = r;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "dockbp",
                Type = BranchPointType.Dock,
                UT = 2000,
                ParentRecordingIds = new List<string> { "launch" },
                ChildRecordingIds = new List<string> { "docked" },
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "undockbp",
                Type = BranchPointType.Undock,
                UT = 3000,
                SplitCause = "UNDOCK",
                ParentRecordingIds = new List<string> { "docked" },
                ChildRecordingIds = new List<string> { "survivor", "payload" },
            });
            return tree;
        }

        private static Recording MakeLeg(
            string id, string chainId, int chainIndex, double start, double end)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "V",
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = 0,
                IsDebris = false,
                ExplicitStartUT = start,
                ExplicitEndUT = end
            };
        }

        [Fact]
        public void ReconcileSelections_PreservesMixedDockSelection()
        {
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            MissionStore.SuppressLogging = false;

            // A mission that deliberately excludes the pre-dock lead
            // interval but KEEPS the docked stretch (the M-MIS-5 headline selection):
            // reconcile must not touch it.
            RecordingTree tree = DockTree("t1");
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { tree });
            Mission m = First();
            m.ExcludedIntervalKeys.Add("launch");
            m.ExcludedIntervalKeys.Add("payload");

            int removed = MissionStore.ReconcileSelections(new List<RecordingTree> { tree });

            Assert.Equal(0, removed);
            Assert.Equal(2, m.ExcludedIntervalKeys.Count);
            Assert.DoesNotContain("launch@dock1", m.ExcludedIntervalKeys);
        }

        // First selectable composition-node HeadLegId in a depth-first walk (null if none).
        private static string FirstSelectableHeadLegId(
            System.Collections.Generic.IReadOnlyList<MissionCompositionNode> nodes)
        {
            if (nodes == null)
                return null;
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                if (n == null)
                    continue;
                if (n.IsSelectable && !string.IsNullOrEmpty(n.HeadLegId))
                    return n.HeadLegId;
                string child = FirstSelectableHeadLegId(n.Children);
                if (child != null)
                    return child;
            }
            return null;
        }
    }
}
