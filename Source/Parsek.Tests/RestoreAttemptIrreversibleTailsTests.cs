using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// RESTORE-CLONE-DISCARD-DROPS-IRREVERSIBLE-CAREER-ACTIONS: while a committed tree is
    /// resumed as a clone, new events and science are tagged with the COMMITTED recording
    /// ids, which the discard re-home skips as committed history. A LIVE discard of the
    /// attempt must re-home the ones after the attempt's cutoff (the clone's own: KSP keeps
    /// the finished contract, the milestone, the banked science) before the tail purge
    /// removes them; a rollback discard (rewind, load) must not, because its reload reverts
    /// KSP. Cells: the helper both ways, the DiscardPendingTree path both ways, and a source
    /// gate on the rollback call sites.
    /// </summary>
    [Collection("Sequential")]
    public class RestoreAttemptIrreversibleTailsTests : IDisposable
    {
        private const string TreeId = "tree-restore-tails";
        private const string RecId = "rec-restore-tails";
        private const double CutoffUT = 200.0;

        private readonly List<string> logLines = new List<string>();
        private readonly GameScenes originalScene;

        public RestoreAttemptIrreversibleTailsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.ClearCommittedTreeRestoreAttempt("test-setup");
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            PostWalkActionReconciler.SetResourceTrackingAvailabilityForTesting(true, true, true);
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            originalScene = HighLogic.LoadedScene;
        }

        public void Dispose()
        {
            HighLogic.LoadedScene = originalScene;
            RecordingStore.ClearCommittedTreeRestoreAttempt("test-teardown");
            KspStatePatcher.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        /// <summary>
        /// Commits one recording ending at <see cref="CutoffUT"/>, then arms a restore
        /// attempt on a same-id clone of its tree (the cutoff map entry is that end UT).
        /// </summary>
        private static RecordingTree ArmAttemptOnCommittedRecording()
        {
            var committed = new Recording
            {
                RecordingId = RecId,
                TreeId = TreeId,
                VesselName = "Clone Ship",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = CutoffUT,
            };
            RecordingStore.AddCommittedInternal(committed);

            var clone = new RecordingTree
            {
                Id = TreeId,
                TreeName = "Clone Ship",
                RootRecordingId = RecId,
                ActiveRecordingId = RecId,
            };
            clone.AddOrReplaceRecording(new Recording
            {
                RecordingId = RecId,
                TreeId = TreeId,
                VesselName = "Clone Ship",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = CutoffUT,
            });
            CommittedTreeRestoreAttempt.ArmCommittedTreeRestoreAttempt(clone, "test-arm");
            return clone;
        }

        private static void AddEvent(GameStateEventType type, string key, double ut, string detail = "")
        {
            var e = new GameStateEvent
            {
                ut = ut,
                eventType = type,
                key = key,
                detail = detail,
                recordingId = RecId,
            };
            GameStateStore.AddEvent(ref e);
        }

        private static void AddScience(string subjectId, double captureUT)
        {
            GameStateRecorder.PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = subjectId,
                science = 6f,
                subjectMaxValue = 16f,
                captureUT = captureUT,
                reasonKey = "ScienceTransmission",
                recordingId = RecId,
            });
        }

        // catches: the discard re-home skipping every committed-id event, so a contract the
        // player finished in the restore attempt (KSP keeps it finished) has no ledger row
        // and the next recalc re-lists it as active / takes its reward back.
        [Fact]
        public void LiveDiscard_RehomesTheAttemptsIrreversibleTails_KeepsCommittedHistory()
        {
            ArmAttemptOnCommittedRecording();
            AddEvent(GameStateEventType.ContractCompleted, "guid-tail", 250.0);
            AddEvent(GameStateEventType.MilestoneAchieved, "RecordsAltitude", 260.0,
                GameStateRecorder.BuildMilestoneDetail(1200.0, 1.0f, 0.0));
            // At or before the cutoff: committed history, already a ledger row at commit.
            AddEvent(GameStateEventType.MilestoneAchieved, "RecordsSpeed", 150.0,
                GameStateRecorder.BuildMilestoneDetail(800.0, 1.0f, 0.0));
            AddScience("sci-tail", 270.0);
            AddScience("sci-history", 190.0);

            RecordingStore.DiscardCommittedTreeRestoreAttempt("test-live", preserveIrreversibleTails: LedgerOrchestrator.PreserveIrreversibleRestoreAttemptTailsOnDiscard);

            var contract = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.ContractComplete && a.ContractId == "guid-tail");
            Assert.NotNull(contract);
            Assert.True(string.IsNullOrEmpty(contract.RecordingId));
            Assert.Equal(250.0, contract.UT);
            var milestone = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.MilestoneAchievement && a.MilestoneId == "RecordsAltitude");
            Assert.NotNull(milestone);
            Assert.Equal(1200f, milestone.MilestoneFundsAwarded);
            Assert.DoesNotContain(Ledger.Actions, a => a.MilestoneId == "RecordsSpeed");

            var science = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.ScienceEarning && a.SubjectId == "sci-tail");
            Assert.NotNull(science);
            Assert.True(string.IsNullOrEmpty(science.RecordingId));
            Assert.DoesNotContain(GameStateRecorder.PendingScienceSubjects, s => s.subjectId == "sci-tail");
            Assert.DoesNotContain(Ledger.Actions, a => a.SubjectId == "sci-history");
            Assert.Contains(GameStateRecorder.PendingScienceSubjects, s => s.subjectId == "sci-history");

            // The tails are still purged after the re-home; the history event stays.
            Assert.DoesNotContain(GameStateStore.Events, e => e.key == "guid-tail");
            Assert.Contains(GameStateStore.Events, e => e.key == "RecordsSpeed");
            Assert.False(CommittedTreeRestoreAttempt.HasCommittedTreeRestoreAttempt);
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("PreserveIrreversibleRestoreAttemptTailsOnDiscard (test-live): re-homed 3 direct action(s)")
                && l.Contains("contract=1") && l.Contains("milestone=1") && l.Contains("science=1"));
        }

        // catches: a rollback discard (a rewind, a load) re-homing the attempt's tails. The
        // reload reverted KSP, so a re-homed row would credit a reward KSP no longer has.
        [Fact]
        public void RollbackDiscard_DoesNotRehome_StillPurges()
        {
            ArmAttemptOnCommittedRecording();
            AddEvent(GameStateEventType.ContractCompleted, "guid-tail", 250.0);
            AddScience("sci-tail", 270.0);

            RecordingStore.DiscardCommittedTreeRestoreAttempt("test-rollback", preserveIrreversibleTails: null);

            Assert.DoesNotContain(Ledger.Actions, a => a.ContractId == "guid-tail");
            Assert.DoesNotContain(Ledger.Actions, a => a.SubjectId == "sci-tail");
            Assert.DoesNotContain(GameStateStore.Events, e => e.key == "guid-tail");
            Assert.DoesNotContain(logLines, l => l.Contains("PreserveIrreversibleRestoreAttemptTailsOnDiscard"));
        }

        // catches: the pending-tree discard purging the attempt's tails BEFORE the re-home
        // could run (its committed-overlap ids are never in the re-home set).
        [Fact]
        public void DiscardPendingTree_OfTheClone_RehomesTheTails_AbandonDoesNot()
        {
            RecordingTree clone = ArmAttemptOnCommittedRecording();
            AddEvent(GameStateEventType.ContractCompleted, "guid-pending", 250.0);
            PendingTreeSlot.StashPendingTree(clone);

            TreeDiscardService.DiscardPendingTree();

            Assert.Contains(Ledger.Actions, a =>
                a.Type == GameActionType.ContractComplete && a.ContractId == "guid-pending"
                && string.IsNullOrEmpty(a.RecordingId));
            Assert.DoesNotContain(GameStateStore.Events, e => e.key == "guid-pending");
        }

        [Fact]
        public void DiscardPendingTree_AbandonPath_OfTheClone_DoesNotRehome()
        {
            RecordingTree clone = ArmAttemptOnCommittedRecording();
            AddEvent(GameStateEventType.ContractCompleted, "guid-abandon", 250.0);
            PendingTreeSlot.StashPendingTree(clone);

            TreeDiscardService.DiscardPendingTree(preserveIrreversibleLiveGameplay: false);

            Assert.DoesNotContain(Ledger.Actions, a => a.ContractId == "guid-abandon");
            Assert.DoesNotContain(GameStateStore.Events, e => e.key == "guid-abandon");
        }

        // catches: a site whose drop follows a reload (KSP already reverted with the save)
        // flipped to a live discard, which would credit what KSP no longer has; and a live
        // drop site flipped to rollback, which would drop what KSP keeps.
        // A live drop passes the ledger's re-home step, a rollback drop passes null.
        private const string LivePreserve = "LedgerOrchestrator.PreserveIrreversibleRestoreAttemptTailsOnDiscard";
        private const string RollbackPreserve = "null";

        [Theory]
        [InlineData(RewindInvokerSourceFiles.ModuleFolder + "/" + RewindInvokerSourceFiles.PostLoad, "internal static bool DiscardRestoreAttemptOfAbandonedFlight(", RollbackPreserve)]
        [InlineData("Controllers/ParsekFlight.cs", "private void ResetFlightReadyState(", RollbackPreserve)]
        [InlineData("Controllers/ParsekFlight.cs", "internal void DiscardActiveTreeForSuppressedSceneExit(", RollbackPreserve)]
        [InlineData("Controllers/ParsekFlightSwitchContinuation.cs", "internal void AutoDiscardNoOpNoSessionCommittedResume(", LivePreserve)]
        [InlineData("Patches/MapFocusObjectOnSelectPatch.cs", "private static void DiscardActiveRecordingAndSwitchTo(", LivePreserve)]
        public void DropSites_PassTheirPreserveFlag(string file, string signature, string expected)
        {
            string body = ExtractMethodBody(
                SourceScanText.StripCSharpComments(ParsekFlightSourceFiles.ReadText(LocateParsekSourceFile(file))),
                signature);
            Assert.Contains("preserveIrreversibleTails: " + expected, body);
            string other = expected == LivePreserve ? RollbackPreserve : LivePreserve;
            Assert.DoesNotContain("preserveIrreversibleTails: " + other, body);
        }

        // catches: a caller of the scoped switch-segment discard that hands it anything but the
        // ledger's two preservations (a no-op lambda, null, another method), which would drop
        // the contracts, milestones and science earned inside the discarded segment while KSP
        // keeps them. Every production call site is found by walking the comment-stripped,
        // literal-masked source of Source/Parsek, so a new caller is held to the same rule.
        private const string ScopedDiscardMethod = "TryDiscardActiveSwitchSegmentAttempt";
        private const string LiveGameplayPreserve = "LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard";

        [Fact]
        public void ScopedSwitchSegmentDiscardCallers_PassTheLedgerPreservations()
        {
            string root = Path.GetDirectoryName(LocateParsekSourceFile("Parsek.csproj"));
            var callsByFile = new Dictionary<string, int>(StringComparer.Ordinal);
            int declarations = 0;
            foreach (string path in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = path.Substring(root.Length + 1).Replace('\\', '/');
                if (rel.StartsWith("bin/", StringComparison.Ordinal)
                    || rel.StartsWith("obj/", StringComparison.Ordinal))
                    continue;
                string prepared = SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));
                foreach (int at in IdentifierOccurrences(prepared, ScopedDiscardMethod))
                {
                    int open = SkipWhitespace(prepared, at + ScopedDiscardMethod.Length);
                    Assert.True(open < prepared.Length && prepared[open] == '(',
                        rel + ": " + ScopedDiscardMethod + " used other than as a call");
                    if (!PrecededByMemberAccess(prepared, at))
                    {
                        Assert.Equal("SwitchIntent/SwitchSegmentAttempts.cs", rel);
                        declarations++;
                        continue;
                    }
                    List<string> args = TopLevelArguments(prepared, open);
                    string site = rel + " line " + (prepared.Take(at).Count(c => c == '\n') + 1);
                    Assert.True(args.Count == 3, site + ": expected 3 arguments, got " + args.Count);
                    Assert.True(LiveGameplayPreserve == args[0],
                        site + ": first argument is '" + args[0] + "'");
                    Assert.True(LivePreserve == args[1],
                        site + ": second argument is '" + args[1] + "'");
                    Assert.StartsWith("out", args[2]);
                    callsByFile[rel] = callsByFile.TryGetValue(rel, out int n) ? n + 1 : 1;
                }
            }
            Assert.Equal(1, declarations);
            Assert.Equal(
                new[]
                {
                    "Controllers/ParsekFlightSwitchContinuation.cs=1",
                    "Patches/MapFocusObjectOnSelectPatch.cs=1",
                    "Rewind/MergeDialog.ReFlyDiscard.cs=1",
                },
                callsByFile.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => kv.Key + "=" + kv.Value).ToArray());
        }

        private static IEnumerable<int> IdentifierOccurrences(string prepared, string identifier)
        {
            for (int i = prepared.IndexOf(identifier, StringComparison.Ordinal); i >= 0;
                 i = prepared.IndexOf(identifier, i + identifier.Length, StringComparison.Ordinal))
            {
                bool startOk = i == 0 || !IsIdentifierChar(prepared[i - 1]);
                int end = i + identifier.Length;
                bool endOk = end >= prepared.Length || !IsIdentifierChar(prepared[end]);
                if (startOk && endOk)
                    yield return i;
            }
        }

        private static bool IsIdentifierChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        private static int SkipWhitespace(string text, int i)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            return i;
        }

        private static bool PrecededByMemberAccess(string text, int i)
        {
            int k = i - 1;
            while (k >= 0 && char.IsWhiteSpace(text[k])) k--;
            return k >= 0 && text[k] == '.';
        }

        // The argument list that opens at openParen, split at its top-level commas, each
        // argument with its whitespace removed. Parentheses, brackets and braces nest, so a
        // lambda's own parameter list or body never splits an argument.
        private static List<string> TopLevelArguments(string prepared, int openParen)
        {
            var args = new List<string>();
            var current = new System.Text.StringBuilder();
            int depth = 0;
            for (int i = openParen; i < prepared.Length; i++)
            {
                char c = prepared[i];
                if (c == '(' || c == '[' || c == '{')
                {
                    if (depth++ == 0) continue;
                }
                else if (c == ')' || c == ']' || c == '}')
                {
                    if (--depth == 0)
                    {
                        args.Add(current.ToString());
                        return args;
                    }
                }
                else if (c == ',' && depth == 1)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    continue;
                }
                if (!char.IsWhiteSpace(c))
                    current.Append(c);
            }
            throw new InvalidOperationException("unbalanced argument list at " + openParen);
        }

        private static string ExtractMethodBody(string src, string signature)
        {
            int start = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature + " not found - update this gate.");
            int open = src.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0)
                    return src.Substring(start, i - start + 1);
            }
            throw new InvalidOperationException(signature + " has unbalanced braces");
        }

        private static string LocateParsekSourceFile(string relativePath)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                string candidate = Path.Combine(dir, "Source", "Parsek", relativePath);
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Parsek", relativePath));
        }
    }
}
