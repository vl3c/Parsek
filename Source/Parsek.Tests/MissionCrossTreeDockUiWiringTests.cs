using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    // M-MIS-8 UI wiring gates (the handlers are IMGUI, not xUnit drivable - the source-text
    // idiom per MissionSelectionGenerationStampWiringTests): the partner-journey affordance
    // toggle must mutate Mission.IncludedForeignDockLinkIds, and the Missions-window loop
    // toggle must pass the committed trees into SetLoopEnabled so a cross-tree-linked
    // mission clears looping missions on its linked foreign tree(s).
    public class MissionCrossTreeDockUiWiringTests
    {
        private static string ReadMissionsWindowSource()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot, "Source", "Parsek", "UI", "MissionsWindowUI.cs");
            Assert.True(File.Exists(path), $"MissionsWindowUI.cs not found at {path}");
            return File.ReadAllText(path);
        }

        [Fact]
        public void LinkToggle_MutatesIncludedForeignDockLinkIds()
        {
            string src = ReadMissionsWindowSource();

            int add = src.IndexOf(
                "mission.IncludedForeignDockLinkIds.Add(link.LinkId);", StringComparison.Ordinal);
            Assert.True(add >= 0, "partner-journey include mutation site not found");

            int remove = src.IndexOf(
                "mission.IncludedForeignDockLinkIds.Remove(link.LinkId);", add, StringComparison.Ordinal);
            Assert.True(remove >= 0 && remove - add < 200,
                "partner-journey exclude mutation not adjacent to the include (same toggle block)");
        }

        [Fact]
        public void LinkToggle_ClearsConflictingLoops_WhenIncludedOnLoopingMission()
        {
            string src = ReadMissionsWindowSource();

            int add = src.IndexOf(
                "mission.IncludedForeignDockLinkIds.Add(link.LinkId);", StringComparison.Ordinal);
            Assert.True(add >= 0, "partner-journey include mutation site not found");
            int clear = src.IndexOf(
                "MissionStore.ClearLoopsConflictingWith(mission,", add, StringComparison.Ordinal);
            Assert.True(clear >= 0 && clear - add < 900,
                "including a link on a looping mission must clear conflicting loops " +
                "(spanned-set rule) in the same toggle block");
        }

        [Fact]
        public void LoopToggle_PassesCommittedTreesForSpannedSetClearing()
        {
            string src = ReadMissionsWindowSource();

            int call = src.IndexOf(
                "MissionStore.SetLoopEnabled(mission, loopNow, Planetarium.GetUniversalTime(),",
                StringComparison.Ordinal);
            Assert.True(call >= 0, "SetLoopEnabled call with trees overload not found");
            int trees = src.IndexOf("RecordingStore.CommittedTrees", call, StringComparison.Ordinal);
            Assert.True(trees >= 0 && trees - call < 200,
                "SetLoopEnabled is not passing RecordingStore.CommittedTrees");
        }
    }
}
