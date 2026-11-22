using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek.TestCommands;
using Parsek.UI.Gallery;
using UnityEngine;
using Xunit;
using Xunit.Abstractions;

namespace Parsek.Tests
{
    /// <summary>
    /// The Mission Profile window's pure halves (docs/dev/design-mission-profile.md sections
    /// 2.2 to 2.5, 6, 7 and 11.3): title, first-open rect and floor, collapse visibility, the
    /// rebuild signature, the effective id set, the career sum, the orange Spent rule, the
    /// deleted-tree close, the two gallery states over the real builder, the seam's target
    /// parse, and the source wiring of the Profile button and the window.
    /// </summary>
    [Collection("Sequential")]
    public class MissionProfileWindowTests : IDisposable
    {
        private readonly ITestOutputHelper output;

        public MissionProfileWindowTests(ITestOutputHelper output)
        {
            this.output = output;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            MissionStructureBuilder.SuppressLogging = true;
            MissionCompositionBuilder.SuppressLogging = true;
            MissionStructureListBuilder.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            MissionStructureBuilder.SuppressLogging = false;
            MissionCompositionBuilder.SuppressLogging = false;
            MissionStructureListBuilder.SuppressLogging = false;
        }

        // ------------------------------------------------------------------ title, rect

        [Fact]
        public void Title_NamesTheMission_OrIsTheBarePrefix()
        {
            Assert.Equal("Parsek - Profile: Kerbal X", MissionProfileWindowUI.BuildWindowTitle("Kerbal X"));
            Assert.Equal("Parsek - Profile", MissionProfileWindowUI.BuildWindowTitle(null));
            Assert.Equal("Parsek - Profile", MissionProfileWindowUI.BuildWindowTitle(""));
        }

        // catches: the Profile opening exactly over the Log (design 2.2: 80 px down / right of
        // the Log's first-open rect), or at another size.
        [Fact]
        public void DefaultRect_CascadesEightyPixelsFromTheLog_AtTheProfileSize()
        {
            var main = new Rect(10, 20, 300, 400);
            Rect log = StructureListWindowUI.DefaultWindowRect(main, false);
            Rect profile = MissionProfileWindowUI.DefaultWindowRect(main);
            Assert.Equal(log.x + 80f, profile.x);
            Assert.Equal(log.y + 80f, profile.y);
            Assert.Equal(960f, profile.width);
            Assert.Equal(420f, profile.height);
            Assert.Equal(480f, MissionProfileWindowUI.MinWindowWidth);
            Assert.Equal(160f, MissionProfileWindowUI.MinWindowHeight);
        }

        // ------------------------------------------------------------------ collapse

        private static MissionProfileRow R(string key, string parent)
            => new MissionProfileRow { Key = key, ParentKey = parent, HasCaret = true };

        // catches: a collapsed row hiding itself (its caret is how it reopens), a grandchild
        // left visible under a collapsed grandparent, or a sibling hidden.
        [Fact]
        public void VisibleRows_HideEveryDescendantOfACollapsedRow_ButNotTheRowItself()
        {
            var rows = new List<MissionProfileRow>
            {
                R("summary", null), R("line", null), R("phase1", "line"), R("ev1", "phase1"),
                R("phase2", "line"), R("ev2", "phase2"),
            };
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, MissionProfileWindowUI.VisibleRowIndices(rows, k => false));
            Assert.Equal(new[] { 0, 1, 2, 4, 5 },
                MissionProfileWindowUI.VisibleRowIndices(rows, k => k == "phase1"));
            Assert.Equal(new[] { 0, 1 }, MissionProfileWindowUI.VisibleRowIndices(rows, k => k == "line"));
            // A parent key that names no row (a malformed model) hides nothing.
            rows.Add(R("orphan", "missing"));
            Assert.Contains(6, MissionProfileWindowUI.VisibleRowIndices(rows, k => k == "missing"));
            // Null tolerated.
            Assert.Empty(MissionProfileWindowUI.VisibleRowIndices(null, k => false));
        }

        // catches: a cyclic ParentKey chain spinning the draw forever.
        [Fact]
        public void VisibleRows_ACycleTerminates()
        {
            var rows = new List<MissionProfileRow> { R("a", "b"), R("b", "a") };
            Assert.Equal(new[] { 0, 1 }, MissionProfileWindowUI.VisibleRowIndices(rows, k => false));
        }

        // catches: a caret click not flipping, not logging, or a gallery open leaving the
        // seam's row count / target mode stale.
        [Fact]
        public void GalleryOpen_ThenCaretToggle_FlipsAndLogs()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = l => lines.Add(l);
            ParsekLog.VerboseOverrideForTesting = true;
            var window = new MissionProfileWindowUI(null);
            Assert.Equal("None", window.TargetModeForTesting);
            MissionProfile p = GuiMockProfileStates.BuildMunReturn();
            window.OpenWithGalleryProfile("Mun Return", p);
            Assert.True(window.IsOpen);
            Assert.Equal(p.Rows.Count, window.StepCountForTesting);
            Assert.Equal("Mission", window.TargetModeForTesting);
            string lineKey = p.Rows.First(r => r.Kind == MissionProfileRowKind.Line).Key;
            Assert.True(window.ToggleCollapsed(lineKey));
            Assert.Contains(lines, l => l.Contains("[UI]") && l.Contains("Profile window caret:")
                && l.Contains("collapsed=True") && l.Contains("visibleRows=5/" + p.Rows.Count));
            Assert.False(window.ToggleCollapsed(lineKey));
            Assert.Contains(lines, l => l.Contains("collapsed=False"));
        }

        [Fact]
        public void CollapseKey_IsScopedByMission()
        {
            Assert.NotEqual(MissionProfileWindowUI.CollapseKey("m1", "line"),
                MissionProfileWindowUI.CollapseKey("m2", "line"));
            Assert.StartsWith(MissionProfileWindowUI.CollapseKey("m1", ""),
                MissionProfileWindowUI.CollapseKey("m1", "phase"), StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ signature

        // catches: a rebuild missed when any input the profile reads moves (design 6), or a
        // rebuild every frame because the include set's order changed.
        [Fact]
        public void ChangeSignature_MovesWithEveryInput_AndIgnoresIncludeOrder()
        {
            var keys = new List<string> { "a", "b" };
            int baseSig = MissionProfileWindowUI.ComputeChangeSignature(1, 1, true, "M", keys, 1);
            Assert.Equal(baseSig, MissionProfileWindowUI.ComputeChangeSignature(1, 1, true, "M",
                new List<string> { "b", "a" }, 1));
            Assert.NotEqual(baseSig, MissionProfileWindowUI.ComputeChangeSignature(2, 1, true, "M", keys, 1));
            Assert.NotEqual(baseSig, MissionProfileWindowUI.ComputeChangeSignature(1, 2, true, "M", keys, 1));
            Assert.NotEqual(baseSig, MissionProfileWindowUI.ComputeChangeSignature(1, 1, false, "M", keys, 1));
            Assert.NotEqual(baseSig, MissionProfileWindowUI.ComputeChangeSignature(1, 1, true, "N", keys, 1));
            Assert.NotEqual(baseSig, MissionProfileWindowUI.ComputeChangeSignature(1, 1, true, "M",
                new List<string> { "a" }, 1));
            Assert.NotEqual(baseSig, MissionProfileWindowUI.ComputeChangeSignature(1, 1, true, "M", keys, 2));
        }

        [Fact]
        public void DeletedTree_ClosesTheWindow_AGalleryMockNever()
        {
            Assert.True(MissionProfileWindowUI.ShouldCloseForDeletedTree("t1", false));
            Assert.False(MissionProfileWindowUI.ShouldCloseForDeletedTree("t1", true));
            Assert.False(MissionProfileWindowUI.ShouldCloseForDeletedTree(null, false));
        }

        // ------------------------------------------------------------------ inputs

        // catches: the effective id set dropping a recording nothing supersedes, keeping a
        // superseded one, or keeping a NotCommitted provisional (design 5).
        [Fact]
        public void EffectiveIds_DropSupersededAndNotCommitted()
        {
            var tree = new RecordingTree { Id = "t" };
            tree.Recordings["old"] = new Recording { RecordingId = "old" };
            tree.Recordings["fork"] = new Recording { RecordingId = "fork" };
            tree.Recordings["prov"] = new Recording { RecordingId = "prov", MergeState = MergeState.NotCommitted };
            var supersedes = new List<RecordingSupersedeRelation>
            {
                new RecordingSupersedeRelation { RelationId = "r", OldRecordingId = "old", NewRecordingId = "fork" },
            };
            HashSet<string> ids = MissionProfileWindowUI.EffectiveRecordingIdsOf(tree, supersedes);
            Assert.Equal(new[] { "fork" }, ids.OrderBy(x => x).ToArray());
            Assert.Equal(new[] { "fork", "old" },
                MissionProfileWindowUI.EffectiveRecordingIdsOf(tree, null).OrderBy(x => x).ToArray());
            Assert.Empty(MissionProfileWindowUI.EffectiveRecordingIdsOf(null, supersedes));
        }

        // catches: a career result of another mission counted, a spending added as an
        // earning, or Known set with nothing matched.
        [Fact]
        public void Career_SumsOnlyTheMissionsTaggedRows_WithSigns()
        {
            var actions = new List<GameAction>
            {
                new GameAction { Type = GameActionType.ScienceEarning, RecordingId = "a", ScienceAwarded = 12.5f },
                new GameAction { Type = GameActionType.FundsEarning, RecordingId = "a", FundsAwarded = 1000f },
                new GameAction { Type = GameActionType.FundsSpending, RecordingId = "b", FundsSpent = 300f },
                new GameAction { Type = GameActionType.ReputationEarning, RecordingId = "a", NominalRep = 5f },
                new GameAction { Type = GameActionType.ReputationPenalty, RecordingId = "a", NominalPenalty = 2f },
                new GameAction { Type = GameActionType.ScienceEarning, RecordingId = "other", ScienceAwarded = 99f },
                new GameAction { Type = GameActionType.KerbalHire, RecordingId = "a" },
            };
            MissionProfileCareerTotals t = MissionProfileWindowUI.SumCareer(actions, new HashSet<string> { "a", "b" });
            Assert.True(t.Known);
            Assert.Equal(12.5, t.Science, 3);
            Assert.Equal(700, t.Funds, 3);
            Assert.Equal(3, t.Reputation, 3);
            Assert.False(MissionProfileWindowUI.SumCareer(actions, new HashSet<string> { "none" }).Known);
            Assert.False(MissionProfileWindowUI.SumCareer(null, new HashSet<string> { "a" }).Known);
        }

        // catches: an empty or "-" Spent cell drawn orange, or a figure left white (design 2.3).
        [Fact]
        public void SpentIsOrange_OnlyWithAFigure()
        {
            Assert.True(MissionProfileWindowUI.DrawsSpentInOrange(new MissionProfileRow { HasSpent = true, Spent = "~862" }));
            Assert.True(MissionProfileWindowUI.DrawsSpentInOrange(new MissionProfileRow { HasSpent = true, Spent = "862" }));
            Assert.False(MissionProfileWindowUI.DrawsSpentInOrange(new MissionProfileRow { HasSpent = false, Spent = "-" }));
            Assert.False(MissionProfileWindowUI.DrawsSpentInOrange(new MissionProfileRow { HasSpent = true, Spent = "" }));
            Assert.False(MissionProfileWindowUI.DrawsSpentInOrange(null));
        }

        // ------------------------------------------------------------------ gallery states

        // catches: the Mun return state no longer reading as a Mun return through the real
        // analyzer (a builder change would silently photograph something else), or reading
        // with no orange Spent.
        [Fact]
        public void Gallery_MunReturn_ReadsLaunchToRecovery_ThroughTheRealBuilder()
        {
            MissionProfile p = GuiMockProfileStates.BuildMunReturn();
            foreach (MissionProfileRow r in p.Rows) output.WriteLine(MissionProfileFixtureTests.Row(r));
            Assert.False(p.IsEmpty);
            List<ProfilePhaseKind> kinds = p.Rows.Where(r => r.Kind == MissionProfileRowKind.Phase && r.PhaseKind.HasValue)
                .Select(r => r.PhaseKind.Value).ToList();
            Assert.Contains(ProfilePhaseKind.Ascent, kinds);
            Assert.Contains(ProfilePhaseKind.Transfer, kinds);
            Assert.Contains(ProfilePhaseKind.Reentry, kinds);
            Assert.True(kinds.Count(k => k == ProfilePhaseKind.Orbit) >= 2, string.Join(",", kinds));
            Assert.Contains(p.Rows, MissionProfileWindowUI.DrawsSpentInOrange);
            Assert.Equal(MissionProfileRowKind.Summary, p.Rows[0].Kind);
            Assert.Equal(0, p.Unclassified);
        }

        [Fact]
        public void Gallery_Empty_IsTheOneEmptyRow()
        {
            MissionProfile p = GuiMockProfileStates.BuildEmpty();
            Assert.True(p.IsEmpty);
            MissionProfileRow row = Assert.Single(p.Rows);
            Assert.Equal(MissionProfileRowKind.Empty, row.Kind);
            Assert.Equal("This mission has no recorded flight.", row.Element);
        }

        [Fact]
        public void Gallery_StatesAreFiledUnderTheProfileWindow()
        {
            List<GuiMockState> states = GuiMockCatalogue.ForWindow(GuiMockSession.ProfileWindow);
            Assert.Equal(new[] { "profile.mission.mun-return", "profile.mission.empty" },
                states.Select(s => s.Id).ToArray());
            Assert.Equal(TestCommandUiAction.ProfileWindow, GuiMockSession.ProfileWindow);
            foreach (GuiMockState s in states)
                Assert.NotEmpty(GuiMockWitness.Covered(s.Build(), s.Tab, s.Covers));
        }

        // ------------------------------------------------------------------ seam

        // catches: op=target refusing the profile window, or taking a route there.
        [Fact]
        public void SeamTarget_TheProfileTakesAMission_NotARoute()
        {
            Assert.True(TestCommandUiState.TryParseTarget(TestCommandUiAction.ProfileWindow, "Kerbal X", null,
                out UiTargetKind kind, out string value, out string reject));
            Assert.Equal(UiTargetKind.Mission, kind);
            Assert.Equal("Kerbal X", value);
            Assert.Null(reject);
            Assert.False(TestCommandUiState.TryParseTarget(TestCommandUiAction.ProfileWindow, null, "r1",
                out _, out _, out reject));
            Assert.Equal(TestCommandUiState.TargetRouteRetiredReason, reject);
            Assert.False(TestCommandUiState.TryParseTarget(TestCommandUiAction.ProfileWindow, null, null,
                out _, out _, out reject));
            Assert.Equal(TestCommandUiState.TargetArgMissingReason, reject);
            Assert.Contains(TestCommandUiAction.Windows, w => w.Name == "profile" && w.InFlight && w.InSpaceCenter);
        }

        // ------------------------------------------------------------------ source wiring

        private static string Src(string rel)
            => SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(Path.Combine(
                RepoRoot(), "Source", "Parsek", rel.Replace('/', Path.DirectorySeparatorChar))));

        private static string RepoRoot()
            => Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));

        // catches: the Profile button opening the Log (or nothing), the window built but
        // never drawn or never released, or the seam's target arm opening another window.
        [Fact]
        public void Wiring_ButtonOpensTheProfile_WindowIsDrawnReleasedAndTargeted()
        {
            string rows = Src("UI/MissionsWindowUI.MissionRows.cs");
            int profile = rows.IndexOf("DrawMissionProfileButton(mission);", StringComparison.Ordinal);
            int log = rows.IndexOf("DrawMissionLogButton(mission);", StringComparison.Ordinal);
            Assert.True(profile >= 0 && log > profile, "Profile must be drawn before (above) Log");
            Assert.Contains("parentUI.OpenProfileWindowForMission(mission.TreeId, mission.Id, mission.Name);", rows);

            string ui = Src("UI/ParsekUI.cs");
            Assert.Contains("missionProfileUI.OpenForMission(treeId, missionId, title);", ui);
            Assert.Contains("missionProfileUI.DrawIfOpen(mainWindowRect);", ui);
            Assert.Contains("missionProfileUI.ReleaseInputLock();", ui);

            string window = Src("UI/MissionProfileWindowUI.cs");
            Assert.Contains("MissionProfileBuilder.BuildProfile(", window);
            Assert.Contains("frame.Draw(parentUI,", window);
            Assert.Contains("ParsekUiScale.Window(", Src("UI/LogTableWindowFrame.cs"));
            Assert.Contains("frame.Draw(parentUI,", Src("UI/StructureListWindowUI.cs"));

            string seam = Src("TestCommands/ParsekTestCommandAddon.UiState.cs");
            Assert.Contains("GetMissionProfileUI()", seam);
        }
    }
}
