using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The save carries its game state (design-load-restores-the-save.md 4.3, plan 1.3): OnSave
    /// builds each external node once and adds it under the scenario node's <c>GAME_STATE</c>
    /// child; the cold branch reads each store from that node when present, else from the
    /// files. Cells: the node round trip equals the file round trip over the committed career
    /// fixtures, the files fallback, the gates on the node path, pending science, and the
    /// restore-attempt events in both branches.
    /// </summary>
    [Collection("Sequential")]
    public class GameStateSaveNodeTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> tempDirs = new List<string>();

        public GameStateSaveNodeTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ResetStores();
        }

        public void Dispose()
        {
            RecordingPaths.SaveRootOverrideForTesting = null;
            RecordingStore.ClearCommittedTreeRestoreAttempt("test-teardown");
            ResetStores();
            ParsekLog.ResetTestOverrides();
            foreach (string dir in tempDirs)
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void ResetStores()
        {
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
        }

        private string NewTempRoot()
        {
            string dir = Path.Combine(Path.GetTempPath(), "parsek-gamestate-node-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            tempDirs.Add(dir);
            return dir;
        }

        private string StageFixtureRoot(string fixture)
        {
            string source = Path.Combine(SyntheticRecordingTests.ResolveProjectRoot(),
                "Source", "Parsek.Tests", "Fixtures", fixture, "Parsek", "GameState");
            Assert.True(Directory.Exists(source), "fixture GameState dir not found at " + source);
            string root = NewTempRoot();
            string dest = Path.Combine(root, "Parsek", "GameState");
            Directory.CreateDirectory(dest);
            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
            return root;
        }

        private static void LoadFromFiles()
        {
            GameStateStore.LoadEventFile();
            GameStateStore.LoadBaselines();
            MilestoneStore.LoadMilestoneFile();
            LedgerOrchestrator.OnLoad();
        }

        /// <summary>Every store, serialized the way the writers serialize it.</summary>
        private static string Snapshot()
        {
            var sb = new StringBuilder();
            sb.AppendLine(GameStateStore.BuildEventsNode(null).Root.ToString());
            foreach (var b in GameStateStore.Baselines)
                sb.AppendLine(GameStateStore.BuildBaselineNode(b).ToString());
            sb.AppendLine(MilestoneStore.BuildMilestonesNode().ToString());
            sb.AppendLine(Ledger.BuildSaveNode().ToString());
            sb.AppendLine(GameStateRecorder.BuildPendingScienceNode(GameStateRecorder.PendingScienceSubjects).ToString());
            return sb.ToString();
        }

        /// <summary>
        /// The GAME_STATE node OnSave builds (PersistGameStateAndMilestones), carried through a
        /// written and re-read scenario node like a .sfs.
        /// </summary>
        private ConfigNode SaveScenarioNodeThroughText(Action<ConfigNode> edit = null)
        {
            var baselines = new ConfigNode(ParsekScenario.BaselinesNodeName);
            foreach (var b in GameStateStore.Baselines)
                baselines.AddNode(GameStateStore.BuildBaselineNode(b));
            var eventsBuild = GameStateStore.BuildEventsNode(
                RecordingStore.CommittedTreeRestoreAttemptTreeId,
                RecordingStore.CommittedTreeRestoreAttemptReason);
            ConfigNode gameState = ParsekScenario.BuildGameStateNode(
                Ledger.BuildSaveNode(), eventsBuild.Root, MilestoneStore.BuildMilestonesNode(), baselines,
                GameStateRecorder.BuildPendingScienceNode(GameStateRecorder.PendingScienceSubjects),
                eventsBuild.AttemptEvents);
            edit?.Invoke(gameState);

            var scenario = new ConfigNode("SCENARIO");
            scenario.AddValue("name", "ParsekScenario");
            scenario.AddNode(gameState);
            string path = Path.Combine(NewTempRoot(), "persistent-scenario.cfg");
            scenario.Save(path);
            ConfigNode loaded = ConfigNode.Load(path);
            Assert.NotNull(loaded);
            Assert.NotNull(loaded.GetNode(ParsekScenario.GameStateNodeName));
            return loaded;
        }

        // ---- round trip ----

        // catches: a node reader or writer that drops or reorders anything the file pair keeps
        // (an action, an event, a contract snapshot, a science subject, a baseline, a
        // milestone, the progress seed): the cold load from the save would then differ from
        // the cold load from the files it mirrors.
        [Theory]
        [InlineData("C2CareerPostFix")]
        [InlineData("C1Career")]
        public void NodeRoundTrip_EqualsFileRoundTrip(string fixture)
        {
            RecordingPaths.SaveRootOverrideForTesting = StageFixtureRoot(fixture);
            LoadFromFiles();
            string fromFiles = Snapshot();
            int ledgerRows = Ledger.Actions.Count;
            int events = GameStateStore.EventCount;
            int milestones = MilestoneStore.MilestoneCount;
            int baselines = GameStateStore.BaselineCount;
            Assert.True(ledgerRows > 0, fixture + " ledger is empty");
            if (fixture == "C2CareerPostFix")
            {
                Assert.True(events > 0);
                Assert.True(milestones > 0);
                Assert.Equal(2, baselines);
            }

            ConfigNode scenario = SaveScenarioNodeThroughText();

            ResetStores();
            string emptyRoot = NewTempRoot();
            RecordingPaths.SaveRootOverrideForTesting = emptyRoot;
            logLines.Clear();
            ParsekScenario.LoadGameStateFromNodeOrFiles(scenario);

            Assert.Equal(fromFiles, Snapshot());
            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains("OnLoad game state from save node: ledgerRows="
                    + ledgerRows.ToString(CultureInfo.InvariantCulture)
                    + " events=" + events.ToString(CultureInfo.InvariantCulture)
                    + " milestones=" + milestones.ToString(CultureInfo.InvariantCulture)
                    + " baselines=" + baselines.ToString(CultureInfo.InvariantCulture)
                    + " pendingScience=0"));
            Assert.DoesNotContain(logLines, l => l.Contains("lacks"));
            // The node path read no file: nothing was written into or read from the empty root.
            Assert.Empty(Directory.GetFiles(emptyRoot, "*", SearchOption.AllDirectories));
        }

        // catches: a save written by an older build (no GAME_STATE node) no longer reading the
        // external files, which is today's behavior and its only source.
        [Fact]
        public void NoNode_ReadsTheFiles()
        {
            RecordingPaths.SaveRootOverrideForTesting = StageFixtureRoot("C2CareerPostFix");
            LoadFromFiles();
            string fromFiles = Snapshot();

            ResetStores();
            logLines.Clear();
            var scenario = new ConfigNode("SCENARIO");
            ParsekScenario.LoadGameStateFromNodeOrFiles(scenario);

            Assert.Equal(fromFiles, Snapshot());
            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains("OnLoad game state from files (no GAME_STATE node)"));
        }

        // catches: a GAME_STATE node missing one child leaving that store empty instead of
        // reading its file.
        [Fact]
        public void NodeWithoutLedger_ReadsTheLedgerFile_AndWarns()
        {
            RecordingPaths.SaveRootOverrideForTesting = StageFixtureRoot("C2CareerPostFix");
            LoadFromFiles();
            string ledgerFromFile = Ledger.BuildSaveNode().ToString();
            ConfigNode scenario = SaveScenarioNodeThroughText(gs => gs.RemoveNodes(ParsekScenario.LedgerNodeName));

            ResetStores();
            logLines.Clear();
            ParsekScenario.LoadGameStateFromNodeOrFiles(scenario);

            Assert.Equal(ledgerFromFile, Ledger.BuildSaveNode().ToString());
            Assert.Contains(logLines, l => l.Contains("[WARN][Scenario]")
                && l.Contains("lacks LEDGER; read from the external file(s) instead"));
        }

        // ---- gates ----

        // catches: the node path skipping the ledger version gate the ledger file reader applies.
        [Theory]
        [InlineData("version", "99")]
        public void LedgerNode_SchemaGates_StartEmpty(string key, string value)
        {
            Ledger.AddAction(new GameAction { UT = 10.0, Type = GameActionType.FundsSpending, Sequence = 1 });
            RecordingPaths.SaveRootOverrideForTesting = NewTempRoot();
            ConfigNode scenario = SaveScenarioNodeThroughText(gs =>
            {
                ConfigNode ledger = gs.GetNode(ParsekScenario.LedgerNodeName);
                ledger.RemoveValues(key);
                if (value != null)
                    ledger.AddValue(key, value);
            });

            ResetStores();
            logLines.Clear();
            ParsekScenario.LoadGameStateFromNodeOrFiles(scenario);

            Assert.Empty(Ledger.Actions);
            Assert.Contains(logLines, l => l.Contains("[WARN][Ledger]")
                && l.Contains("Ledger node in the save's GAME_STATE has unsupported schema")
                && l.Contains("starting with empty ledger"));
        }

        // catches: the node path rejecting a ledger whose only difference is the recording
        // schema generation it was stamped with (GC3-LEDGER-GENERATION-GATE-DROPS-ROWS, owner
        // ruling 2026-10-21: load it when its format matches).
        [Theory]
        [InlineData("999")]
        [InlineData(null)]
        public void LedgerNode_OtherRecordingSchemaGeneration_Loads(string value)
        {
            Ledger.AddAction(new GameAction { UT = 10.0, Type = GameActionType.FundsSpending, Sequence = 1 });
            RecordingPaths.SaveRootOverrideForTesting = NewTempRoot();
            ConfigNode scenario = SaveScenarioNodeThroughText(gs =>
            {
                ConfigNode ledger = gs.GetNode(ParsekScenario.LedgerNodeName);
                ledger.RemoveValues("recordingSchemaGeneration");
                if (value != null)
                    ledger.AddValue("recordingSchemaGeneration", value);
            });

            ResetStores();
            logLines.Clear();
            ParsekScenario.LoadGameStateFromNodeOrFiles(scenario);

            Assert.Single(Ledger.Actions);
            Assert.Contains(logLines, l => l.Contains("[INFO][Ledger]")
                && l.Contains("recording schema generation")
                && l.Contains("loaded anyway"));
        }

        // catches: the events node path rejecting (or silently accepting) a version the file
        // reader warns about but still loads.
        [Fact]
        public void EventsNode_UnknownVersion_WarnsAndLoads_AsTheFileReaderDoes()
        {
            var e = new GameStateEvent { ut = 5.0, eventType = GameStateEventType.TechResearched, key = "k", detail = "" };
            GameStateStore.AddEvent(ref e);
            RecordingPaths.SaveRootOverrideForTesting = NewTempRoot();
            ConfigNode scenario = SaveScenarioNodeThroughText(gs =>
            {
                ConfigNode ev = gs.GetNode(GameStateStore.EventsNodeName);
                ev.SetValue("version", "2");
            });

            ResetStores();
            logLines.Clear();
            ParsekScenario.LoadGameStateFromNodeOrFiles(scenario);

            Assert.Single(GameStateStore.Events);
            Assert.Contains(logLines, l => l.Contains("Unsupported game state events version=2 (expected 1)"));
        }

        // catches: the node load leaving the once-per-folder guard open, so a later file read
        // in the same session replaces what the save restored.
        [Fact]
        public void NodeLoad_ClosesTheOncePerFolderGuard()
        {
            var e = new GameStateEvent { ut = 5.0, eventType = GameStateEventType.TechResearched, key = "from-node", detail = "" };
            GameStateStore.AddEvent(ref e);
            RecordingPaths.SaveRootOverrideForTesting = NewTempRoot();
            ConfigNode scenario = SaveScenarioNodeThroughText();

            ResetStores();
            ParsekScenario.LoadGameStateFromNodeOrFiles(scenario);
            logLines.Clear();
            Assert.True(GameStateStore.LoadEventFile());
            Assert.True(MilestoneStore.LoadMilestoneFile());

            Assert.Single(GameStateStore.Events);
            Assert.Equal("from-node", GameStateStore.Events[0].key);
            Assert.Contains(logLines, l => l.Contains("LoadEventFile: already loaded, skipping"));
            Assert.Contains(logLines, l => l.Contains("LoadMilestoneFile: already loaded, skipping"));
        }

        // ---- pending science ----

        // catches: a pending science field lost by the node codec (the ledger converts these
        // subjects into rows at commit, so a dropped multiplier or capture id changes credit).
        [Fact]
        public void PendingScience_RoundTripsEveryField()
        {
            var a = new PendingScienceSubject
            {
                subjectId = "crewReport@KerbinSrfLandedLaunchPad",
                science = 1.5f,
                subjectMaxValue = 7.25f,
                captureUT = 123.456789,
                reasonKey = "ScienceTransmission",
                recordingId = "rec-a",
                scienceGainMultiplier = 0.8f,
                captureActionId = "act_abc",
            };
            var b = new PendingScienceSubject { subjectId = "evaReport@MunSrfLanded", science = 3f };
            GameStateRecorder.PendingScienceSubjects.Add(a);
            GameStateRecorder.PendingScienceSubjects.Add(b);
            RecordingPaths.SaveRootOverrideForTesting = NewTempRoot();
            ConfigNode scenario = SaveScenarioNodeThroughText();

            ResetStores();
            logLines.Clear();
            ParsekScenario.LoadGameStateFromNodeOrFiles(scenario);

            Assert.Equal(2, GameStateRecorder.PendingScienceSubjects.Count);
            var ra = GameStateRecorder.PendingScienceSubjects[0];
            Assert.Equal(a.subjectId, ra.subjectId);
            Assert.Equal(a.science, ra.science);
            Assert.Equal(a.subjectMaxValue, ra.subjectMaxValue);
            Assert.Equal(a.captureUT, ra.captureUT);
            Assert.Equal(a.reasonKey, ra.reasonKey);
            Assert.Equal(a.recordingId, ra.recordingId);
            Assert.Equal(a.scienceGainMultiplier, ra.scienceGainMultiplier);
            Assert.Equal(a.captureActionId, ra.captureActionId);
            var rb = GameStateRecorder.PendingScienceSubjects[1];
            Assert.Equal(b.subjectId, rb.subjectId);
            Assert.Null(rb.reasonKey);
            Assert.Null(rb.recordingId);
            Assert.Null(rb.captureActionId);
            Assert.Contains(logLines, l => l.Contains("pendingScience=2"));
        }

        // ---- restore-attempt events (#866) ----

        private const string AttemptTreeId = "tree-attempt-866";
        private const string AttemptRecId = "rec-attempt-866";

        private static RecordingTree AttemptTree()
        {
            var rec = new Recording
            {
                RecordingId = AttemptRecId,
                TreeId = AttemptTreeId,
                VesselName = "Attempt",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0,
            };
            var tree = new RecordingTree { Id = AttemptTreeId, TreeName = "Attempt", RootRecordingId = AttemptRecId };
            tree.AddOrReplaceRecording(rec);
            return tree;
        }

        private static void AddAttemptEvents()
        {
            var post = new GameStateEvent
            {
                ut = 250.0, eventType = GameStateEventType.TechResearched, key = "tail-post", detail = "",
                recordingId = AttemptRecId,
            };
            GameStateStore.AddEvent(ref post);
            var pre = new GameStateEvent
            {
                ut = 150.0, eventType = GameStateEventType.TechResearched, key = "tail-pre", detail = "",
                recordingId = AttemptRecId,
            };
            GameStateStore.AddEvent(ref pre);
            var untagged = new GameStateEvent
            {
                ut = 260.0, eventType = GameStateEventType.TechResearched, key = "career", detail = "",
            };
            GameStateStore.AddEvent(ref untagged);
        }

        // catches: ATTEMPT_EVENTS written with no live attempt, or carrying anything but the
        // events the #866 filter keeps off the file.
        [Fact]
        public void AttemptEvents_WrittenOnlyForALiveAttempt_ExactlyTheFilteredEvents()
        {
            AddAttemptEvents();
            var noAttempt = GameStateStore.BuildEventsNode(RecordingStore.CommittedTreeRestoreAttemptTreeId);
            Assert.Null(noAttempt.AttemptEvents);
            Assert.Equal(3, noAttempt.Root.GetNodes("GAME_STATE_EVENT").Length);

            RecordingStore.ArmCommittedTreeRestoreAttempt(AttemptTree(), "test");
            RecordingPaths.SaveRootOverrideForTesting = NewTempRoot();
            var build = GameStateStore.BuildEventsNode(RecordingStore.CommittedTreeRestoreAttemptTreeId);

            Assert.NotNull(build.AttemptEvents);
            Assert.Equal(AttemptTreeId, build.AttemptEvents.GetValue(GameStateStore.AttemptTreeIdKey));
            Assert.Equal(new[] { "tail-post" },
                build.AttemptEvents.GetNodes("GAME_STATE_EVENT").Select(n => n.GetValue("key")).ToArray());
            Assert.Equal(new[] { "tail-pre", "career" },
                build.Root.GetNodes("GAME_STATE_EVENT").Select(n => n.GetValue("key")).ToArray());

            // The file mirror is the same filtered node.
            Assert.True(GameStateStore.WriteEventsNode(build));
            string file = RecordingPaths.ResolveSaveScopedPath(RecordingPaths.BuildGameStateEventsRelativePath());
            Assert.DoesNotContain("tail-post", File.ReadAllText(file));
        }

        [Theory]
        [InlineData(null, null, null, (int)ParsekScenario.SaveAttemptEventsDisposition.NoneInSave)]
        [InlineData("t1", null, "t1", (int)ParsekScenario.SaveAttemptEventsDisposition.DropAttemptNotArmed)]
        [InlineData("t1", "t2", "t1", (int)ParsekScenario.SaveAttemptEventsDisposition.DropAttemptNotArmed)]
        [InlineData("t1", "t1", null, (int)ParsekScenario.SaveAttemptEventsDisposition.DropActiveTreeDiffers)]
        [InlineData("t1", "t1", "t2", (int)ParsekScenario.SaveAttemptEventsDisposition.DropActiveTreeDiffers)]
        [InlineData("t1", "t1", "t1", (int)ParsekScenario.SaveAttemptEventsDisposition.Restore)]
        public void AttemptEvents_Decision(string saved, string armed, string active, int expected)
        {
            Assert.Equal((ParsekScenario.SaveAttemptEventsDisposition)expected,
                ParsekScenario.DecideSaveAttemptEventsRestore(saved, armed, active));
        }

        // catches: the save's attempt events restored to memory when the attempt is not armed
        // again (the cold restore re-arms it only for its own restored tree with a committed
        // copy, ColdLoadRestoreAttemptRearmTests), which would make them durable on the next
        // save (no filter) outside the attempt's merge.
        [Fact]
        public void AttemptEvents_AttemptNotArmedAfterLoad_Dropped()
        {
            AddAttemptEvents();
            RecordingStore.ArmCommittedTreeRestoreAttempt(AttemptTree(), "test");
            RecordingPaths.SaveRootOverrideForTesting = NewTempRoot();
            ConfigNode scenario = SaveScenarioNodeThroughText();

            ResetStores();
            RecordingStore.ClearCommittedTreeRestoreAttempt("cold-load");
            RecordingStore.StashPendingTree(AttemptTree(), PendingTreeState.Limbo);
            logLines.Clear();
            ParsekScenario.LoadGameStateFromNodeOrFiles(scenario);
            Assert.True(ParsekScenario.HasPendingSaveAttemptEventsForTesting);

            Assert.Equal(0, ParsekScenario.ApplySaveAttemptEventsAfterActiveTreeRestore());

            Assert.DoesNotContain(GameStateStore.Events, e => e.key == "tail-post");
            Assert.False(ParsekScenario.HasPendingSaveAttemptEventsForTesting);
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][Scenario]")
                && l.Contains("dropped the save's ATTEMPT_EVENTS: attemptTreeId=" + AttemptTreeId)
                && l.Contains("reason=DropAttemptNotArmed"));
        }

        // catches: the restore branch writing the attempt's events to the file or making them
        // milestone-eligible: they go back into memory only, and the live attempt keeps them
        // off the next save's file and defers the milestone flush.
        [Fact]
        public void AttemptEvents_SameAttemptRestored_MemoryOnly()
        {
            AddAttemptEvents();
            RecordingStore.ArmCommittedTreeRestoreAttempt(AttemptTree(), "test");
            RecordingPaths.SaveRootOverrideForTesting = NewTempRoot();
            ConfigNode scenario = SaveScenarioNodeThroughText();

            ResetStores();
            RecordingStore.ArmCommittedTreeRestoreAttempt(AttemptTree(), "re-armed");
            RecordingStore.StashPendingTree(AttemptTree(), PendingTreeState.Limbo);
            logLines.Clear();
            ParsekScenario.LoadGameStateFromNodeOrFiles(scenario);
            Assert.DoesNotContain(GameStateStore.Events, e => e.key == "tail-post");

            Assert.Equal(1, ParsekScenario.ApplySaveAttemptEventsAfterActiveTreeRestore());

            Assert.Contains(GameStateStore.Events, e => e.key == "tail-post");
            Assert.Contains(logLines, l => l.Contains("[INFO][Scenario]")
                && l.Contains("restored the save's ATTEMPT_EVENTS into memory only: attemptTreeId=" + AttemptTreeId)
                && l.Contains("events=1"));
            Assert.True(ParsekScenario.ShouldDeferPendingEventMilestoneFlushForSave());
            Assert.True(GameStateStore.SaveEventFile());
            string file = RecordingPaths.ResolveSaveScopedPath(RecordingPaths.BuildGameStateEventsRelativePath());
            Assert.DoesNotContain("tail-post", File.ReadAllText(file));
        }

        // ---- wiring ----

        // catches: OnSave building a store's node twice (file and save could then differ) or
        // not adding GAME_STATE, and the cold branch not reading it.
        [Fact]
        public void Wiring_OnSaveBuildsOnce_ColdBranchReadsTheNode()
        {
            string persist = PreparedMethodBody("ParsekScenario.cs", "private static void PersistGameStateAndMilestones(ConfigNode node)");
            Assert.Equal(1, Occurrences(persist, "GameStateStore.BuildEventsNode("));
            Assert.Equal(1, Occurrences(persist, "GameStateStore.WriteEventsNode(eventsBuild)"));
            Assert.Equal(1, Occurrences(persist, "MilestoneStore.BuildMilestonesNode()"));
            Assert.Equal(1, Occurrences(persist, "Ledger.BuildSaveNode()"));
            Assert.Equal(1, Occurrences(persist, "LedgerOrchestrator.OnSave(ledgerNode)"));
            Assert.Equal(1, Occurrences(persist, "BuildGameStateNode("));
            Assert.Equal(0, Occurrences(persist, "SaveEventFile("));
            Assert.Equal(0, Occurrences(persist, "SaveMilestoneFile("));
            int remove = IndexOrFail(persist, "node.RemoveNodes(GameStateNodeName)");
            int add = IndexOrFail(persist, "node.AddNode(BuildGameStateNode(");
            Assert.True(remove < add);

            string external = PreparedMethodBody("ParsekScenario.cs", "private static void LoadExternalFiles(ConfigNode node)");
            Assert.Equal(1, Occurrences(external, "LoadGameStateFromNodeOrFiles(node);"));
            Assert.Equal(0, Occurrences(external, "LoadEventFile("));

            string onLoad = PreparedMethodBody("ParsekScenario.cs", "public override void OnLoad(ConfigNode node)");
            int classify = IndexOrFail(onLoad, "LoadReconcilePolicy.ClassifyEarly(");
            int reset = IndexOrFail(onLoad, "ResetSaveScopedSessionState(");
            int crew = IndexOrFail(onLoad, "LoadCrewAndGroupState(node);");
            int external2 = IndexOrFail(onLoad, "LoadExternalFiles(node);");
            int coldRestore = IndexOrFail(onLoad, "EarlyLoadKind.Cold, HighLogic.LoadedScene == GameScenes.FLIGHT");
            int attempt = IndexOrFail(onLoad, "ApplySaveAttemptEventsAfterActiveTreeRestore();");
            int pendingRestore = onLoad.IndexOf("TryRestorePendingTreeNode(", coldRestore, StringComparison.Ordinal);
            Assert.Equal(1, Occurrences(onLoad, "ResetSaveScopedSessionState("));
            Assert.Equal(1, Occurrences(onLoad, "ApplySaveAttemptEventsAfterActiveTreeRestore();"));
            Assert.True(classify < reset && reset < crew && crew < external2,
                "the session reset runs after the classification and before LoadCrewAndGroupState");
            Assert.True(coldRestore < attempt && attempt < pendingRestore,
                "the save's attempt events follow the cold active-tree restore");
            Assert.Contains("if (earlyLoadKind == EarlyLoadKind.Cold)", Collapse(onLoad.Substring(classify, reset - classify)));
        }

        private static string PreparedMethodBody(string relPath, string signatureFragment)
        {
            string repo = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(repo, "Source", "Parsek", relPath);
            Assert.True(File.Exists(path), "Source file not found at " + path);
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));
            int sigIdx = prepared.IndexOf(signatureFragment, StringComparison.Ordinal);
            Assert.True(sigIdx >= 0, "signature not found in " + relPath + ": " + signatureFragment);
            int open = prepared.IndexOf('{', sigIdx);
            return SourceScanText.BraceMatchedBlock(prepared, open);
        }

        private static int IndexOrFail(string body, string needle)
        {
            int idx = body.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(idx >= 0, "'" + needle + "' not found");
            return idx;
        }

        private static int Occurrences(string text, string needle)
        {
            int count = 0;
            int idx = 0;
            while ((idx = text.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }

        private static string Collapse(string text) => Regex.Replace(text, @"\s+", " ");
    }
}
