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
    //
    // Every scan runs over SourceScanText.StripCommentsAndMaskLiterals output, so a call left
    // behind only as a comment (or inside a log string) no longer satisfies a pin, and each
    // proximity window is additionally anchored to the ENCLOSING METHOD BODY, so two unrelated
    // methods that happen to sit within the character budget cannot jointly satisfy one gate.
    // EveryScan_RejectsSourceWhereTheCallsSurviveOnlyAsComments pins the helper on a synthetic
    // comment-only source; the wiring of that helper into ReadMissionsWindowSource is not
    // separately gated here.
    public class MissionCrossTreeDockUiWiringTests
    {
        private static string ReadMissionsWindowSource()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot, "Source", "Parsek", "UI", "MissionsWindowUI.cs");
            Assert.True(File.Exists(path), $"MissionsWindowUI.cs not found at {path}");
            return SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));
        }

        /// <summary>
        /// True when both offsets sit inside the same enclosing method body (derived from the
        /// file's brace structure, not from a declaration regex).
        /// </summary>
        private static bool SameMethodBody(string prepared, int a, int b)
        {
            int first = SourceScanText.EnclosingMethodBodyStart(prepared, a);
            int second = SourceScanText.EnclosingMethodBodyStart(prepared, b);
            return first >= 0 && first == second;
        }

        // ---- the scans, as pure functions over prepared source (null = satisfied) ----

        internal static string ScanLinkToggleMutation(string prepared)
        {
            int add = prepared.IndexOf(
                "mission.IncludedForeignDockLinkIds.Add(link.LinkId);", StringComparison.Ordinal);
            if (add < 0) return "partner-journey include mutation site not found";

            int remove = prepared.IndexOf(
                "mission.IncludedForeignDockLinkIds.Remove(link.LinkId);", add, StringComparison.Ordinal);
            if (remove < 0 || remove - add >= 200 || !SameMethodBody(prepared, add, remove))
                return "partner-journey exclude mutation not adjacent to the include (same toggle block)";
            return null;
        }

        internal static string ScanLinkToggleLoopClear(string prepared)
        {
            int add = prepared.IndexOf(
                "mission.IncludedForeignDockLinkIds.Add(link.LinkId);", StringComparison.Ordinal);
            if (add < 0) return "partner-journey include mutation site not found";

            int clear = prepared.IndexOf(
                "MissionStore.ClearLoopsConflictingWith(mission,", add, StringComparison.Ordinal);
            if (clear < 0 || clear - add >= 1600 || !SameMethodBody(prepared, add, clear))
                return "including a link on a looping mission must clear conflicting loops "
                     + "(spanned-set rule) in the same toggle block";

            // T1.6 parity: this is the SECOND path that can take another mission's loop, so the
            // clear must be bracketed by the same snapshot + on-screen announcement the Loop
            // toggle uses - a loop moving silently here is the symptom T1.6 removed.
            int snapshot = prepared.IndexOf(
                "SnapshotOtherLoopingMissions(mission);", add, StringComparison.Ordinal);
            if (snapshot < 0 || snapshot >= clear || !SameMethodBody(prepared, add, snapshot))
                return "partner-journey loop clear is not preceded by SnapshotOtherLoopingMissions";

            int announce = prepared.IndexOf(
                "AnnounceClearedLoops(mission, wereLooping);", clear, StringComparison.Ordinal);
            if (announce < 0 || announce - clear >= 400 || !SameMethodBody(prepared, clear, announce))
                return "partner-journey loop clear is not followed by AnnounceClearedLoops";
            return null;
        }

        internal static string ScanLoopTogglePassesTrees(string prepared)
        {
            int call = prepared.IndexOf(
                "MissionStore.SetLoopEnabled(mission, loopNow, Planetarium.GetUniversalTime(),",
                StringComparison.Ordinal);
            if (call < 0) return "SetLoopEnabled call with trees overload not found";

            int trees = prepared.IndexOf(
                "RecordingStore.CommittedTrees", call, StringComparison.Ordinal);
            if (trees < 0 || trees - call >= 200 || !SameMethodBody(prepared, call, trees))
                return "SetLoopEnabled is not passing RecordingStore.CommittedTrees";
            return null;
        }

        // ---- the gates ----

        [Fact]
        public void LinkToggle_MutatesIncludedForeignDockLinkIds()
        {
            string reason = ScanLinkToggleMutation(ReadMissionsWindowSource());
            Assert.True(reason == null, reason);
        }

        [Fact]
        public void LinkToggle_ClearsConflictingLoops_WhenIncludedOnLoopingMission()
        {
            string reason = ScanLinkToggleLoopClear(ReadMissionsWindowSource());
            Assert.True(reason == null, reason);
        }

        [Fact]
        public void LoopToggle_PassesCommittedTreesForSpannedSetClearing()
        {
            string reason = ScanLoopTogglePassesTrees(ReadMissionsWindowSource());
            Assert.True(reason == null, reason);
        }

        [Fact]
        public void EveryScan_RejectsSourceWhereTheCallsSurviveOnlyAsComments()
        {
            // The decoy: every pinned call present, all of it commented out. Before the strip
            // was wired in, all three scans read this as satisfied.
            string decoy = string.Join("\n", new[]
            {
                "namespace Decoy",
                "{",
                "    internal class Ui",
                "    {",
                "        private void Draw()",
                "        {",
                "            // mission.IncludedForeignDockLinkIds.Add(link.LinkId);",
                "            // mission.IncludedForeignDockLinkIds.Remove(link.LinkId);",
                "            // List<Mission> wereLooping = SnapshotOtherLoopingMissions(mission);",
                "            // MissionStore.ClearLoopsConflictingWith(mission,",
                "            // AnnounceClearedLoops(mission, wereLooping);",
                "            /* MissionStore.SetLoopEnabled(mission, loopNow, Planetarium.GetUniversalTime(),",
                "               RecordingStore.CommittedTrees); */",
                "        }",
                "    }",
                "}",
            });

            string prepared = SourceScanText.StripCommentsAndMaskLiterals(decoy);
            Assert.NotNull(ScanLinkToggleMutation(prepared));
            Assert.NotNull(ScanLinkToggleLoopClear(prepared));
            Assert.NotNull(ScanLoopTogglePassesTrees(prepared));
        }
    }
}
