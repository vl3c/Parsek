using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;
using C = Parsek.Tests.RecordingsTableClickHandlerCharacterizationTests;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the four helpers extracted from the Recordings tab's draw methods
    /// (<c>ResolveFolderDisplayBlocks</c>, <c>ApplyGroupLoopToggle</c> and
    /// <c>ApplyGroupArchiveToggle</c> from <c>DrawGroupTree</c>,
    /// <c>ApplyRecordingArchiveToggle</c> from <c>DrawRecordingRow</c>). Each scenario of
    /// <see cref="RecordingsTableClickHandlerCharacterizationTests"/> runs through the
    /// shipped helper and must land on the golden the ORIGINAL block (its verbatim oracle)
    /// pinned, and on the oracle's own digest when both run on fresh stores.
    /// </summary>
    [Collection("Sequential")]
    public class RecordingsTableClickHandlerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> screenMessages = new List<string>();

        public RecordingsTableClickHandlerTests()
        {
            C.ResetAll();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.ScreenMessageSinkForTesting = (msg, dur) =>
                screenMessages.Add(msg + "|" + dur.ToString("R", CultureInfo.InvariantCulture));
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            C.ResetAll();
        }

        private sealed class ShippedBlocks : C.IBlocks
        {
            public List<RecordingsTableUI.GroupDisplayBlock> FolderDisplayBlocks(RecordingsTableUI ui,
                string groupName, IReadOnlyList<Recording> committed,
                Dictionary<string, List<int>> grpToRecs, Dictionary<string, List<int>> chainToRecs,
                out List<int> absorbedRootMembers)
                => ui.ResolveFolderDisplayBlocks(groupName, committed, grpToRecs, chainToRecs,
                    out absorbedRootMembers);
            public void GroupLoop(string groupName, bool newLoop, bool grpRouteBound,
                IReadOnlyList<Recording> committed, HashSet<int> descendants,
                IEnumerable<int> grpLoopScope, List<int> absorbedRootMembers)
                => RecordingsTableUI.ApplyGroupLoopToggle(groupName, newLoop, grpRouteBound,
                    committed, descendants, grpLoopScope, absorbedRootMembers);
            public void GroupArchive(RecordingsTableUI ui, string groupName, HashSet<int> descendants,
                IReadOnlyList<Recording> committed, int memberCount, bool newAllHidden)
                => ui.ApplyGroupArchiveToggle(groupName, descendants, committed, memberCount, newAllHidden);
            public void RowArchive(RecordingsTableUI ui, Recording rec, bool hidden)
                => ui.ApplyRecordingArchiveToggle(rec, hidden);
        }

        private string Run(string name, C.IBlocks blocks, out string material)
        {
            C.ResetAll();
            string output = C.RunScenario(name, blocks, () =>
            {
                logLines.Clear();
                screenMessages.Clear();
            });
            material = C.Material(output, logLines, screenMessages);
            return C.Digest(material, logLines.Count);
        }

        [Theory]
        [MemberData(nameof(C.Scenarios), MemberType = typeof(C))]
        public void ShippedHelper_MatchesTheOriginalBlock(string name)
        {
            string oracle = Run(name, new C.OracleBlocks(), out string oracleMaterial);
            string shipped = Run(name, new ShippedBlocks(), out string shippedMaterial);

            Assert.True(C.Goldens[name] == shipped,
                name + ": golden " + C.Goldens[name] + " shipped " + shipped + "\n" + shippedMaterial);
            Assert.True(oracle == shipped,
                name + ": oracle " + oracle + " shipped " + shipped
                + "\n--- oracle\n" + oracleMaterial + "\n--- shipped\n" + shippedMaterial);
        }

        // ---- Readable direct checks, one behaviour each ----

        [Fact]
        public void RowArchive_RefusesToHideAnUnfinishedFlight_AndSaysSo()
        {
            var u = C.MakeUnfinished("u1", "Booster", "bp-1");
            C.Commit(u);
            C.InstallRewindPoints(u);
            var ui = C.MakeTable();

            ui.ApplyRecordingArchiveToggle(u, true);

            Assert.False(u.Hidden);
            Assert.Contains(logLines, l => l.Contains("[WARN][UnfinishedFlights]")
                && l.Contains("Hide refused for Unfinished Flight rec=u1"));
            Assert.Single(screenMessages);
            Assert.Contains("Cannot hide 'Booster'", screenMessages[0]);
            Assert.DoesNotContain(logLines, l => l.Contains("Cache invalidated"));
        }

        [Fact]
        public void RowArchive_UnhidesAnUnfinishedFlight_TheRecoveryPath()
        {
            var u = C.MakeUnfinished("u1", "Booster", "bp-1");
            u.Hidden = true;
            C.Commit(u);
            C.InstallRewindPoints(u);
            var ui = C.MakeTable();

            ui.ApplyRecordingArchiveToggle(u, false);

            Assert.False(u.Hidden);
            Assert.Empty(screenMessages);
            Assert.Contains(logLines, l => l.Contains("[INFO][UI]") && l.Contains("Recording 'Booster' hidden=False"));
            Assert.Contains(logLines, l => l.Contains("[Timeline]") && l.Contains("Cache invalidated"));
        }

        [Fact]
        public void GroupArchive_HidesEveryDescendantAndTheFolder()
        {
            var a = C.MakeRec("a", "A");
            var b = C.MakeRec("b", "B");
            var outside = C.MakeRec("c", "Outside");
            C.Commit(a, b, outside);
            var ui = C.MakeTable();

            ui.ApplyGroupArchiveToggle("G", new HashSet<int> { 0, 1 }, C.Committed, 2, true);

            Assert.True(a.Hidden);
            Assert.True(b.Hidden);
            Assert.False(outside.Hidden);
            Assert.True(GroupHierarchyStore.IsGroupHidden("G"));
            Assert.Contains(logLines, l => l.Contains("Group 'G' hide-all=True (2 recordings)"));
        }

        [Fact]
        public void GroupArchive_RefusalWritesNothing()
        {
            var n = C.MakeRec("n", "Normal");
            var u = C.MakeUnfinished("u1", "Booster", "bp-1");
            C.Commit(n, u);
            C.InstallRewindPoints(u);
            var ui = C.MakeTable();

            ui.ApplyGroupArchiveToggle("Munshots", new HashSet<int> { 0, 1 }, C.Committed, 2, true);

            Assert.False(n.Hidden);
            Assert.False(u.Hidden);
            Assert.False(GroupHierarchyStore.IsGroupHidden("Munshots"));
            Assert.Contains(logLines, l => l.Contains("Group hide-all refused for group 'Munshots': 1 of 2"));
            Assert.Single(screenMessages);
        }

        [Fact]
        public void GroupLoop_AbsorbedRootMembersGetTheAutoRange_OthersDoNot()
        {
            var root = C.MakeLoopable("r", "Root");
            var other = C.MakeLoopable("o", "Other");
            C.Commit(root, other);
            var desc = new HashSet<int> { 1 };
            var absorbed = new List<int> { 0 };
            var scope = new HashSet<int> { 0, 1 };

            RecordingsTableUI.ApplyGroupLoopToggle("Mission", true, false, C.Committed, desc, scope, absorbed);

            Assert.True(root.LoopPlayback);
            Assert.True(other.LoopPlayback);
            Assert.Equal(100, root.LoopStartUT);
            Assert.Equal(200, root.LoopEndUT);
            Assert.True(double.IsNaN(other.LoopStartUT));
        }

        [Fact]
        public void GroupLoop_RouteBoundScopeWritesNothing()
        {
            var a = C.MakeLoopable("a", "A");
            C.Commit(a);
            var desc = new HashSet<int> { 0 };

            RecordingsTableUI.ApplyGroupLoopToggle("G", true, true, C.Committed, desc, desc, null);

            Assert.False(a.LoopPlayback);
            Assert.Contains(logLines, l => l.Contains("[RouteGuard]") && l.Contains("request=True ignored"));
        }

        [Fact]
        public void FolderDisplayBlocks_NoDirectMembersIsNull()
        {
            C.Commit(C.MakeRec("a", "A"));
            var ui = C.MakeTable();

            var blocks = ui.ResolveFolderDisplayBlocks("G", C.Committed,
                new Dictionary<string, List<int>>(), new Dictionary<string, List<int>>(),
                out List<int> absorbed);

            Assert.Null(blocks);
            Assert.Null(absorbed);
        }

        [Fact]
        public void FolderDisplayBlocks_MissionFolderAbsorbsTheLaunchedVessel()
        {
            var r0 = C.MakeTreeRec("kx-0", "Kerbal X", "Kerbal X", "tree-kx", 42, 10, 20);
            C.Commit(r0, C.MakeTreeRec("kx-1", "Kerbal X", "Kerbal X", "tree-kx", 42, 20, 30),
                C.MakeTreeRec("pr-0", "Probe", "Kerbal X", "tree-kx", 77, 25, 40));
            var tree = new RecordingTree
            {
                Id = "tree-kx",
                RootRecordingId = "kx-0",
                AutoGeneratedRootGroupName = "Kerbal X",
            };
            tree.Recordings["kx-0"] = r0;
            RecordingStore.AddCommittedTreeForTesting(tree);
            var grpToRecs = new Dictionary<string, List<int>> { { "Kerbal X", new List<int> { 0, 1, 2 } } };
            var ui = C.MakeTable();

            var blocks = ui.ResolveFolderDisplayBlocks("Kerbal X", C.Committed, grpToRecs,
                new Dictionary<string, List<int>>(), out List<int> absorbed);

            Assert.Equal(new[] { 0, 1 }, absorbed);
            Assert.Equal(3, blocks.Count);
            Assert.Equal(new[] { 0 }, blocks[0].Members);
            Assert.Equal(new[] { 1 }, blocks[1].Members);
            Assert.Equal(new[] { 2 }, blocks[2].Members);
        }
    }
}
