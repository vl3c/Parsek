using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// archived todo REPLAY-SCOPE-LOST-ON-SAVE-LOAD (owner ruling 2026-11-01: after any load every current
    /// ghost is drawn and relays, even mid-flight). The replay-scope latch is saved in the PARSEK
    /// node and a cold load installs the save's copy, so a recording replaying when the save was
    /// written keeps replaying from the loaded UT, while a recording the playhead only ever moved
    /// forward past stays historical (BUG-B) across the same save and load.
    /// </summary>
    [Collection("Sequential")]
    public class PlaybackScopeSaveLoadTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        // The CN-4 shape: a relay whose window opens at 1000, rewound to before it, saved at 3000.
        private const double RelayStartUT = 1000.0;
        private const double RewindUT = 990.0;
        private const double SaveUT = 3000.0;

        public PlaybackScopeSaveLoadTests()
        {
            PlaybackScopeTracker.ResetForTesting();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            PlaybackScopeTracker.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static ConfigNode SaveNow()
        {
            var node = new ConfigNode("SCENARIO");
            PlaybackScopeTracker.SaveInto(node);
            return node;
        }

        [Fact]
        public void SaveMidReplay_ColdLoad_KeepsTheRecordingInReplayScope()
        {
            // The rewind put the playhead before the relay's start: latched.
            PlaybackScopeTracker.NotePlayhead("relay", RewindUT, RelayStartUT);
            Assert.False(PlaybackScopeTracker.IsHistoricalNeverReplayed("relay", SaveUT, RelayStartUT));
            ConfigNode saved = SaveNow();

            // The cold load: the save-scoped reset clears memory, then the save's copy installs.
            PlaybackScopeTracker.Reset();
            Assert.True(PlaybackScopeTracker.IsHistoricalNeverReplayed("relay", SaveUT, RelayStartUT),
                "without the saved latch the reset makes a mid-replay ghost historical (the bug)");
            PlaybackScopeTracker.InstallFromSave(saved, "load Cold");

            Assert.True(PlaybackScopeTracker.IsInReplayScope("relay"));
            Assert.False(PlaybackScopeTracker.IsHistoricalNeverReplayed("relay", SaveUT, RelayStartUT));
            Assert.Contains(logLines, l => l.Contains("[PlaybackScope]")
                && l.Contains("Replay scope installed from the save (load Cold): inScope=1 seenBeforeActivation=1")
                && l.Contains("node=present"));
        }

        [Fact]
        public void ForwardPlaySave_ColdLoad_HistoricalRecordingStaysHistorical()
        {
            // BUG-B: forward play only ever moved the playhead past the recording's start.
            PlaybackScopeTracker.NotePlayhead("old", SaveUT, RelayStartUT);
            ConfigNode saved = SaveNow();
            Assert.Null(saved.GetNode(PlaybackScopeTracker.NodeName));

            PlaybackScopeTracker.Reset();
            PlaybackScopeTracker.InstallFromSave(saved, "load Cold");

            Assert.False(PlaybackScopeTracker.IsInReplayScope("old"));
            Assert.True(PlaybackScopeTracker.IsHistoricalNeverReplayed("old", SaveUT, RelayStartUT));
            Assert.Contains(logLines, l => l.Contains("[PlaybackScope]")
                && l.Contains("inScope=0 seenBeforeActivation=0") && l.Contains("node=absent"));
        }

        [Fact]
        public void ColdLoad_ReplacesMemoryWithTheSavesLatch()
        {
            // Memory holds a latch from another timeline (a later rewind the loaded save never saw).
            PlaybackScopeTracker.NotePlayhead("saved", RewindUT, RelayStartUT);
            ConfigNode saved = SaveNow();
            PlaybackScopeTracker.NotePlayhead("unsaved", RewindUT, RelayStartUT);

            PlaybackScopeTracker.InstallFromSave(saved, "load Cold");

            Assert.True(PlaybackScopeTracker.IsInReplayScope("saved"));
            Assert.False(PlaybackScopeTracker.IsInReplayScope("unsaved"));
            Assert.False(PlaybackScopeTracker.WasPlayheadSeenBeforeActivation("unsaved"));
        }

        [Fact]
        public void SeenBeforeActivation_RoundTripsSeparatelyFromReplayScope()
        {
            // Within tolerance after the start: in scope, but never seen strictly before it.
            PlaybackScopeTracker.NotePlayhead("tolerance", RelayStartUT + 1.0, RelayStartUT);
            // Strictly before: both.
            PlaybackScopeTracker.NotePlayhead("before", RewindUT, RelayStartUT);
            ConfigNode saved = SaveNow();

            PlaybackScopeTracker.Reset();
            PlaybackScopeTracker.InstallFromSave(saved, "load Cold");

            Assert.True(PlaybackScopeTracker.IsInReplayScope("tolerance"));
            Assert.False(PlaybackScopeTracker.WasPlayheadSeenBeforeActivation("tolerance"));
            Assert.True(PlaybackScopeTracker.IsInReplayScope("before"));
            Assert.True(PlaybackScopeTracker.WasPlayheadSeenBeforeActivation("before"));
        }

        [Fact]
        public void SaveInto_WritesSortedIdsAndReplacesAnOlderCopy()
        {
            var node = new ConfigNode("SCENARIO");
            ConfigNode stale = node.AddNode(PlaybackScopeTracker.NodeName);
            stale.AddValue(PlaybackScopeTracker.InScopeKey, "stale");

            PlaybackScopeTracker.NotePlayhead("b", RewindUT, RelayStartUT);
            PlaybackScopeTracker.NotePlayhead("a", RewindUT, RelayStartUT);
            PlaybackScopeTracker.SaveInto(node);

            ConfigNode[] scopes = node.GetNodes(PlaybackScopeTracker.NodeName);
            Assert.Single(scopes);
            Assert.Equal(new[] { "a", "b" }, scopes[0].GetValues(PlaybackScopeTracker.InScopeKey));
            Assert.Equal(new[] { "a", "b" }, scopes[0].GetValues(PlaybackScopeTracker.SeenBeforeActivationKey));
        }

        [Fact]
        public void SaveInto_EmptyLatch_RemovesAnOlderCopyAndWritesNone()
        {
            var node = new ConfigNode("SCENARIO");
            node.AddNode(PlaybackScopeTracker.NodeName).AddValue(PlaybackScopeTracker.InScopeKey, "stale");

            PlaybackScopeTracker.SaveInto(node);

            Assert.Null(node.GetNode(PlaybackScopeTracker.NodeName));
        }

        [Fact]
        public void UnionFromSave_KeepsMemoryAndAddsTheSavesLatch()
        {
            // A rewind lands in a save written while "saved" replayed; memory holds "memory".
            PlaybackScopeTracker.NotePlayhead("saved", RewindUT, RelayStartUT);
            ConfigNode rewindSave = SaveNow();
            PlaybackScopeTracker.Reset();
            PlaybackScopeTracker.NotePlayhead("memory", RewindUT, RelayStartUT);

            int added = PlaybackScopeTracker.UnionFromSave(rewindSave, "Rewind-to-Launch");

            Assert.Equal(1, added);
            Assert.True(PlaybackScopeTracker.IsInReplayScope("saved"));
            Assert.True(PlaybackScopeTracker.IsInReplayScope("memory"));
            Assert.Contains(logLines, l => l.Contains("[PlaybackScope]")
                && l.Contains("Replay scope merged from the save (Rewind-to-Launch): savedInScope=1 savedSeen=1 "
                    + "newInScope=1 newSeen=1 inScope=2 seenBeforeActivation=2"));
        }

        [Fact]
        public void UnionFromSave_NoNode_ChangesNothing()
        {
            PlaybackScopeTracker.NotePlayhead("memory", RewindUT, RelayStartUT);

            Assert.Equal(0, PlaybackScopeTracker.UnionFromSave(new ConfigNode("SCENARIO"), "load ReFlyStart"));
            Assert.Equal(0, PlaybackScopeTracker.UnionFromSave(null, "load ReFlyStart"));

            Assert.Equal(1, PlaybackScopeTracker.InReplayScopeCount);
        }

        [Fact]
        public void InstallFromSave_SkipsBlankIds()
        {
            var node = new ConfigNode("SCENARIO");
            ConfigNode scope = node.AddNode(PlaybackScopeTracker.NodeName);
            scope.AddValue(PlaybackScopeTracker.InScopeKey, "");
            scope.AddValue(PlaybackScopeTracker.InScopeKey, "rec");

            PlaybackScopeTracker.InstallFromSave(node, "load Cold");

            Assert.Equal(1, PlaybackScopeTracker.InReplayScopeCount);
            Assert.True(PlaybackScopeTracker.IsInReplayScope("rec"));
        }

        [Fact]
        public void RewindSavePreProcess_MergesTheSavesLatchBeforeTheStripResolverRuns()
        {
            // The rewind save was written while "saved" replayed; memory lost it to a later
            // cold load. The strip resolver decides what replays after the rewind, so it must
            // already see the save's latch.
            var root = new ConfigNode();
            ConfigNode flightState = root.AddNode("FLIGHTSTATE");
            flightState.AddValue("UT", "3000");
            ConfigNode scenario = root.AddNode("SCENARIO");
            scenario.AddValue("name", "ParsekScenario");
            scenario.AddNode(PlaybackScopeTracker.NodeName).AddValue(PlaybackScopeTracker.InScopeKey, "saved");
            string sfs = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "parsek-replay-scope-rewind-" + Guid.NewGuid().ToString("N") + ".sfs");
            root.Save(sfs);
            bool inScopeAtResolve = false;
            try
            {
                RewindLauncher.PreProcessRewindSave(
                    sfs, null,
                    ut => { inScopeAtResolve = PlaybackScopeTracker.IsInReplayScope("saved"); return null; },
                    RecordingStore.RewindToLaunchLeadTimeSeconds);
            }
            finally
            {
                try { System.IO.File.Delete(sfs); } catch { }
            }

            Assert.True(inScopeAtResolve);
            Assert.Contains(logLines, l => l.Contains("[PlaybackScope]")
                && l.Contains("Replay scope merged from the save (rewind save pre-process): savedInScope=1"));
        }

        [Fact]
        public void UnionFromGameNode_NoParsekScenario_ChangesNothing()
        {
            var game = new ConfigNode("GAME");
            game.AddNode("SCENARIO").AddValue("name", "ResearchAndDevelopment");

            Assert.Equal(0, PlaybackScopeTracker.UnionFromGameNode(game, "rewind save pre-process"));
            Assert.Equal(0, PlaybackScopeTracker.UnionFromGameNode(null, "rewind save pre-process"));
            Assert.Equal(0, PlaybackScopeTracker.InReplayScopeCount);
        }

        [Fact]
        public void Policy_ColdTakesTheSave_EveryOtherLoadKeepsMemory()
        {
            foreach (LoadKind kind in Enum.GetValues(typeof(LoadKind)))
            {
                LoadReconcileDecision d = LoadReconcilePolicy.Decide(kind, LoadStateCategory.PlaybackReplayScope);
                Assert.Null(d.KnownGapTodoId);
                Assert.Equal(kind == LoadKind.Cold ? LoadReconcileAction.Save : LoadReconcileAction.Memory, d.Action);
            }
            Assert.True(LoadReconcilePolicy.IsConsumedEarly(LoadStateCategory.PlaybackReplayScope));
            Assert.Equal(LoadReconcileAction.Memory,
                LoadReconcilePolicy.DecideEarly(EarlyLoadKind.InSession, LoadStateCategory.PlaybackReplayScope).Action);
        }
    }
}
