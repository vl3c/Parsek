using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    // M-MIS-8 store lifecycle + codec: link reconcile, cross-seam interval-key validity,
    // clone, spanned-set loop clearing, round-trip, and the byte-identity pins for
    // pre-existing (link-free) missions.
    [Collection("Sequential")]
    public class MissionStoreCrossTreeTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MissionStoreCrossTreeTests()
        {
            MissionStore.ResetForTesting();
            // MissionStoreTests leaves the static SuppressLogging flag set; the reconcile
            // warn assertion below needs logging live.
            MissionStore.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            MissionStore.ResetForTesting();
            MissionStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
        }

        private static Mission AddMission(string id, string treeId, string name)
        {
            var m = new Mission(id, treeId, name);
            // Route through Load-free direct add: MissionStore has no public Add, so use
            // Save/Load-free seeding via EnsureDefaultsForTrees is not applicable here (we
            // need specific ids). Serialize one mission node in and reload the store.
            var node = new ConfigNode("PARSEK");
            MissionStore.Save(node);
            ConfigNode mNode = node.AddNode("MISSION");
            m.Save(mNode);
            MissionStore.Load(node);
            return MissionStore.Missions.First(x => x.Id == id);
        }

        // ---- reconcile ----

        [Fact]
        public void Reconcile_DropsStaleLinkId_AndWarns()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var m = AddMission("m1", "tb", "B mission");
            m.IncludedForeignDockLinkIds.Add("no-such-bp");

            int removed = MissionStore.ReconcileSelections(new List<RecordingTree> { tb });

            Assert.Equal(1, removed);
            Assert.Empty(m.IncludedForeignDockLinkIds);
            Assert.Contains(logLines, l => l.Contains("[Mission]")
                && l.Contains("cross-tree dock link id(s)"));
        }

        [Fact]
        public void Reconcile_KeepsValidLink_AndForeignExcludedKey()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree();
            var m = AddMission("m1", "tb", "B mission");
            m.IncludedForeignDockLinkIds.Add(CrossTreeDockFixture.DockBpId);
            // A real partner-journey key: B's post-undock offshoot branch interval.
            m.ExcludedIntervalKeys.Add("B1");

            int removed = MissionStore.ReconcileSelections(new List<RecordingTree> { tb, ta });

            Assert.Equal(0, removed);
            Assert.Contains(CrossTreeDockFixture.DockBpId, m.IncludedForeignDockLinkIds);
            Assert.Contains("B1", m.ExcludedIntervalKeys);
        }

        [Fact]
        public void Reconcile_DropsForeignKey_WhenLinkGone()
        {
            // Foreign tree absent: the link id AND the foreign interval key both go stale.
            var tb = CrossTreeDockFixture.PartnerTree();
            var m = AddMission("m1", "tb", "B mission");
            m.IncludedForeignDockLinkIds.Add(CrossTreeDockFixture.DockBpId);
            m.ExcludedIntervalKeys.Add("B1");

            int removed = MissionStore.ReconcileSelections(new List<RecordingTree> { tb });

            Assert.Equal(2, removed);
            Assert.Empty(m.IncludedForeignDockLinkIds);
            Assert.Empty(m.ExcludedIntervalKeys);
        }

        [Fact]
        public void Reconcile_OwnTreeUncommitted_LeavesLinksUntouched()
        {
            var m = AddMission("m1", "tb", "B mission");
            m.IncludedForeignDockLinkIds.Add(CrossTreeDockFixture.DockBpId);

            MissionStore.ReconcileSelections(new List<RecordingTree>());

            Assert.Contains(CrossTreeDockFixture.DockBpId, m.IncludedForeignDockLinkIds);
        }

        // ---- clone ----

        [Fact]
        public void Clone_CopiesForeignDockLinks()
        {
            var m = AddMission("m1", "tb", "B mission");
            m.IncludedForeignDockLinkIds.Add(CrossTreeDockFixture.DockBpId);

            Mission copy = MissionStore.Clone(m);

            Assert.Contains(CrossTreeDockFixture.DockBpId, copy.IncludedForeignDockLinkIds);
            // Definition-only copy: mutating the clone never touches the source.
            copy.IncludedForeignDockLinkIds.Clear();
            Assert.Contains(CrossTreeDockFixture.DockBpId, m.IncludedForeignDockLinkIds);
        }

        // ---- spanned-set loop clearing ----

        [Fact]
        public void SetLoopEnabled_ClearsLoopOnLinkedForeignTree_WhenTreesGiven()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree();
            var trees = new List<RecordingTree> { tb, ta };
            var node = new ConfigNode("PARSEK");
            var mA = new Mission("ma", "ta", "A mission");
            var mB = new Mission("mb", "tb", "B mission");
            mB.IncludedForeignDockLinkIds.Add(CrossTreeDockFixture.DockBpId);
            mA.Save(node.AddNode("MISSION"));
            mB.Save(node.AddNode("MISSION"));
            MissionStore.Load(node);
            Mission a = MissionStore.Missions.First(x => x.Id == "ma");
            Mission b = MissionStore.Missions.First(x => x.Id == "mb");

            // Enabling the linked mission clears the loop on the foreign tree it spans.
            MissionStore.SetLoopEnabled(a, true, 100.0, trees);
            MissionStore.SetLoopEnabled(b, true, 200.0, trees);
            Assert.True(b.LoopPlayback);
            Assert.False(a.LoopPlayback);

            // And the reverse: enabling the foreign tree's own mission clears the linked one.
            MissionStore.SetLoopEnabled(a, true, 300.0, trees);
            Assert.True(a.LoopPlayback);
            Assert.False(b.LoopPlayback);
        }

        [Fact]
        public void SetLoopEnabled_WithoutTrees_LegacyBehaviorUnchanged()
        {
            var node = new ConfigNode("PARSEK");
            var mA = new Mission("ma", "ta", "A mission");
            var mB = new Mission("mb", "tb", "B mission");
            mB.IncludedForeignDockLinkIds.Add(CrossTreeDockFixture.DockBpId);
            mA.Save(node.AddNode("MISSION"));
            mB.Save(node.AddNode("MISSION"));
            MissionStore.Load(node);
            Mission a = MissionStore.Missions.First(x => x.Id == "ma");
            Mission b = MissionStore.Missions.First(x => x.Id == "mb");

            MissionStore.SetLoopEnabled(a, true, 100.0);
            MissionStore.SetLoopEnabled(b, true, 200.0);

            // No trees supplied: the spanned set degenerates to the own tree, so loops on
            // distinct trees coexist exactly as before the feature.
            Assert.True(a.LoopPlayback);
            Assert.True(b.LoopPlayback);
        }

        [Fact]
        public void Reconcile_ParkedTreeUncommitted_DefersLinkAndKeyDrop()
        {
            // The linked FOREIGN tree may be a parked (Limbo / restored-later) tree; while any
            // parked tree is uncommitted, the link + this mission's interval-key stale-drop
            // are deferred so the selection is never permanently lost mid-OnLoad.
            var tb = CrossTreeDockFixture.PartnerTree();
            var m = AddMission("m1", "tb", "B mission");
            m.IncludedForeignDockLinkIds.Add(CrossTreeDockFixture.DockBpId);
            m.ExcludedIntervalKeys.Add("B1");

            int removed = MissionStore.ReconcileSelections(
                new List<RecordingTree> { tb },
                additionalLiveTreeIds: new[] { "ta" });

            Assert.Equal(0, removed);
            Assert.Contains(CrossTreeDockFixture.DockBpId, m.IncludedForeignDockLinkIds);
            Assert.Contains("B1", m.ExcludedIntervalKeys);
            Assert.Contains(logLines, l => l.Contains("[Mission]")
                && l.Contains("deferred cross-tree link reconcile"));
        }

        [Fact]
        public void NormalizeOneLoopPerTree_SpannedSets_ClearsCrossTreeConflict()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree();
            var trees = new List<RecordingTree> { tb, ta };
            var node = new ConfigNode("PARSEK");
            var mA = new Mission("ma", "ta", "A mission") { LoopPlayback = true };
            var mB = new Mission("mb", "tb", "B mission") { LoopPlayback = true };
            mB.IncludedForeignDockLinkIds.Add(CrossTreeDockFixture.DockBpId);
            mA.Save(node.AddNode("MISSION"));
            mB.Save(node.AddNode("MISSION"));
            MissionStore.Load(node);

            // Without trees: legacy per-tree rule keeps both (distinct TreeIds).
            Assert.Equal(0, MissionStore.NormalizeOneLoopPerTree());

            // With trees: mb's spanned set {tb, ta} intersects ma's {ta}; ma is first in list
            // order so mb is cleared.
            int cleared = MissionStore.NormalizeOneLoopPerTree(trees);
            Assert.Equal(1, cleared);
            Assert.True(MissionStore.Missions.First(x => x.Id == "ma").LoopPlayback);
            Assert.False(MissionStore.Missions.First(x => x.Id == "mb").LoopPlayback);
        }

        [Fact]
        public void ClearLoopsConflictingWith_ClearsForeignTreeLoop_KeepsTargetUntouched()
        {
            // The Missions-window link toggle calls this when a link is included on an
            // already-looping mission: the foreign tree's loop clears, the target keeps its
            // loop state and anchor.
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree();
            var trees = new List<RecordingTree> { tb, ta };
            var node = new ConfigNode("PARSEK");
            var mA = new Mission("ma", "ta", "A mission") { LoopPlayback = true };
            var mB = new Mission("mb", "tb", "B mission") { LoopPlayback = true, LoopAnchorUT = 42.0 };
            mB.IncludedForeignDockLinkIds.Add(CrossTreeDockFixture.DockBpId);
            mA.Save(node.AddNode("MISSION"));
            mB.Save(node.AddNode("MISSION"));
            MissionStore.Load(node);
            Mission a = MissionStore.Missions.First(x => x.Id == "ma");
            Mission b = MissionStore.Missions.First(x => x.Id == "mb");

            MissionStore.ClearLoopsConflictingWith(b, trees, out int same, out int cross,
                "PartnerJourneyInclude");

            Assert.Equal(0, same);
            Assert.Equal(1, cross);
            Assert.False(a.LoopPlayback);
            Assert.True(b.LoopPlayback);
            Assert.Equal(42.0, b.LoopAnchorUT);
        }

        // ---- codec ----

        [Fact]
        public void SaveLoad_RoundTripsForeignDockLinks()
        {
            var m = new Mission("m1", "tb", "B mission");
            m.IncludedForeignDockLinkIds.Add("link-1");
            m.IncludedForeignDockLinkIds.Add("link-2");

            var node = new ConfigNode("MISSION");
            m.Save(node);
            Mission loaded = Mission.Load(node);

            Assert.Equal(2, loaded.IncludedForeignDockLinkIds.Count);
            Assert.Contains("link-1", loaded.IncludedForeignDockLinkIds);
            Assert.Contains("link-2", loaded.IncludedForeignDockLinkIds);
        }

        [Fact]
        public void Save_ForeignDockLinks_WrittenSorted()
        {
            var m = new Mission("m1", "tb", "B mission");
            m.IncludedForeignDockLinkIds.Add("z-link");
            m.IncludedForeignDockLinkIds.Add("a-link");
            m.IncludedForeignDockLinkIds.Add("m-link");

            var node = new ConfigNode("MISSION");
            m.Save(node);

            var links = new List<string>(node.GetValues("foreignDockLink"));
            Assert.Equal(new[] { "a-link", "m-link", "z-link" }, links);
        }

        [Fact]
        public void Save_LinkFreeMission_IsByteIdenticalToPreFeatureShape()
        {
            // Byte-identity pin: a mission WITHOUT links must serialize the exact pre-feature
            // key sequence - no foreignDockLink key, nothing reordered, nothing added.
            var m = new Mission("m1", "tb", "B mission")
            {
                LoopPlayback = true,
                LoopIntervalSeconds = 42.5,
                LoopTimeUnit = LoopTimeUnit.Min,
                LoopAnchorUT = 1234.5,
                Archived = true,
            };
            m.ExcludedThroughLineHeadIds.Add("headX");
            m.ExcludedIntervalKeys.Add("intervalY");

            var node = new ConfigNode("MISSION");
            m.Save(node);

            var names = new List<string>();
            var values = new List<string>();
            for (int i = 0; i < node.values.Count; i++)
            {
                names.Add(node.values[i].name);
                values.Add(node.values[i].value);
            }
            Assert.Equal(new[]
            {
                "id", "treeId", "name", "loopPlayback", "loopIntervalSeconds", "loopTimeUnit",
                "loopAnchorUT", "archived", "selectionSchemaGeneration",
                "excludedHead", "excludedInterval",
            }, names);
            Assert.Equal(new[]
            {
                "m1", "tb", "B mission", "True", "42.5", "Min",
                "1234.5", "True", "1",
                "headX", "intervalY",
            }, values);
        }

        [Fact]
        public void Save_PreFeatureNode_RoundTripsByteIdentically_ThroughStore()
        {
            // A pre-feature store snapshot must survive Load -> Save byte-identically.
            var node = new ConfigNode("PARSEK");
            var m = new Mission("m1", "tb", "B mission");
            m.ExcludedIntervalKeys.Add("intervalY");
            MissionStore.Save(node);
            m.Save(node.AddNode("MISSION"));

            MissionStore.Load(node);
            var resaved = new ConfigNode("PARSEK");
            MissionStore.Save(resaved);

            Assert.Equal(DumpNode(node), DumpNode(resaved));
        }

        private static string DumpNode(ConfigNode node)
        {
            var sb = new System.Text.StringBuilder();
            DumpInto(node, sb, 0);
            return sb.ToString();
        }

        private static void DumpInto(ConfigNode node, System.Text.StringBuilder sb, int depth)
        {
            sb.Append(' ', depth).Append(node.name ?? "").Append('\n');
            for (int i = 0; i < node.values.Count; i++)
                sb.Append(' ', depth).Append(node.values[i].name)
                  .Append('=').Append(node.values[i].value).Append('\n');
            for (int i = 0; i < node.nodes.Count; i++)
                DumpInto(node.nodes[i], sb, depth + 1);
        }
    }
}
