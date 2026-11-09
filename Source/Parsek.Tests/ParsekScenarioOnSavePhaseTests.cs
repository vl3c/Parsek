using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the save phases extracted from <c>ParsekScenario.OnSave</c>
    /// (<c>OnSavePhase*</c>). Their order inside OnSave is pinned by
    /// <see cref="ParsekScenarioOnSaveCharacterizationTests"/>; these cells pin what each one
    /// does. Touches shared static state (ParsekLog, RecordingStore),
    /// hence Sequential.
    /// </summary>
    [Collection("Sequential")]
    public class ParsekScenarioOnSavePhaseTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ParsekScenarioOnSavePhaseTests()
        {
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.ClearCommittedTreesInternal();
        }

        public void Dispose()
        {
            RecordingStore.ClearCommittedTreesInternal();
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ---- save folder mismatch ----

        private static ParsekScenario ScenarioLoadedFor(string folder)
        {
            var scenario = new ParsekScenario();
            FieldInfo field = typeof(ParsekScenario).GetField(
                "scenarioSaveFolder", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field.SetValue(scenario, folder);
            return scenario;
        }

        // HighLogic.SaveFolder reads HighLogic.fetch, which the headless host never creates:
        // the getter answers null and the setter is a no-op. So the live folder is null here,
        // and the cells pin both sides of IsSaveFolderMismatch through the loaded folder.

        [Fact]
        public void SaveFolderMismatch_ALoadedFolderThatIsNotCurrent_WarnsWithBothNames()
        {
            var scenario = ScenarioLoadedFor("career_a");

            scenario.OnSavePhaseWarnOnSaveFolderMismatch();

            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("[Scenario]")
                && l.Contains("OnSave: save folder mismatch")
                && l.Contains("loaded for 'career_a'") && l.Contains("but current is ''")
                && l.Contains("Data may write to wrong save directory."));
        }

        [Fact]
        public void SaveFolderMismatch_NoLoadedFolder_LogsNothing()
        {
            var scenario = ScenarioLoadedFor(null);

            scenario.OnSavePhaseWarnOnSaveFolderMismatch();

            Assert.DoesNotContain(logLines, l => l.Contains("save folder mismatch"));
        }

        // ---- dirty count ----

        private static Recording Rec(string id, bool dirty)
        {
            return new Recording { RecordingId = id, VesselName = "V-" + id, FilesDirty = dirty };
        }

        private static RecordingTree TreeOf(string id, params Recording[] recs)
        {
            var tree = new RecordingTree { Id = id, TreeName = "T-" + id };
            foreach (var r in recs)
            {
                r.TreeId = id;
                tree.Recordings[r.RecordingId] = r;
            }
            return tree;
        }

        [Fact]
        public void CountDirty_AddsDirtyCommittedAndTreeRecordingsToTheRunningCount()
        {
            var recordings = new List<Recording> { Rec("a", true), Rec("b", false), Rec("c", true) };
            RecordingStore.AddCommittedTreeForTesting(TreeOf("t1", Rec("t1a", true), Rec("t1b", false)));
            RecordingStore.AddCommittedTreeForTesting(TreeOf("t2", Rec("t2a", true), Rec("t2b", true)));
            int dirtyCount = 5;

            ParsekScenario.OnSavePhaseCountDirtyRecordings(recordings, ref dirtyCount);

            Assert.Equal(5 + 2 + 3, dirtyCount);
        }

        [Fact]
        public void CountDirty_NothingDirty_LeavesTheCountAlone()
        {
            var recordings = new List<Recording> { Rec("a", false) };
            RecordingStore.AddCommittedTreeForTesting(TreeOf("t1", Rec("t1a", false)));
            int dirtyCount = 0;

            ParsekScenario.OnSavePhaseCountDirtyRecordings(recordings, ref dirtyCount);

            Assert.Equal(0, dirtyCount);
        }
    }
}
