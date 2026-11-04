using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Replay-theory audit R8 / R10: a reader rejects or passes through what it cannot read,
    /// and never defaults it to a value (ledger type 0, merge state Immutable, a random id).
    /// </summary>
    [Collection("Sequential")]
    public class CodecReadabilityTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CodecReadabilityTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.SuppressLogging = false;
            Ledger.ResetForTesting();
        }

        public void Dispose()
        {
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // ---------------------------------------------------------------- recordings (R10)

        private static ConfigNode SavedRecordingNode(string id, MergeState state)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Probe",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
                MergeState = state,
            };
            var node = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(node, rec);
            return node;
        }

        [Fact]
        public void UnknownMergeState_RejectsTheRecording_NeverReadsAsImmutable()
        {
            ConfigNode node = SavedRecordingNode("rec-ms", MergeState.CommittedProvisional);
            node.SetValue("mergeState", "SomeFutureState");

            var rec = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(node, rec);

            Assert.Equal(-1, rec.RecordingFormatVersion);
            Assert.Equal("rec-ms", rec.RecordingId);
            Assert.Contains(logLines, l => l.Contains("[Codec]")
                && l.Contains("rejecting recording rec-ms") && l.Contains("reason=merge-state-unknown"));
        }

        [Fact]
        public void UndefinedNumericMergeState_IsRejected()
        {
            // Enum.TryParse accepts any integer; only a defined member is readable.
            ConfigNode node = SavedRecordingNode("rec-num", MergeState.CommittedProvisional);
            node.SetValue("mergeState", "7");

            Assert.Equal("merge-state-unknown", RecordingTreeRecordCodec.ClassifyUnreadableRecordingNode(node));
        }

        [Theory]
        [InlineData(MergeState.NotCommitted)]
        [InlineData(MergeState.CommittedProvisional)]
        [InlineData(MergeState.Immutable)]
        public void DefinedMergeStates_RoundTrip(MergeState state)
        {
            ConfigNode node = SavedRecordingNode("rec-ok", state);
            Assert.Null(RecordingTreeRecordCodec.ClassifyUnreadableRecordingNode(node));

            var rec = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(node, rec);

            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, rec.RecordingFormatVersion);
            Assert.Equal(state, rec.MergeState);
        }

        [Fact]
        public void MissingRecordingId_RejectsTheRecording_NoRandomId()
        {
            ConfigNode node = SavedRecordingNode("rec-gone", MergeState.Immutable);
            node.RemoveValue("recordingId");

            var rec = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(node, rec);

            Assert.Equal(-1, rec.RecordingFormatVersion);
            Assert.Null(rec.RecordingId);
            Assert.Contains(logLines, l => l.Contains("rejecting recording <no-id>")
                && l.Contains("reason=recording-id-missing"));
        }

        [Fact]
        public void TreeWhoseOnlyRecordingIsUnreadable_IsClassifiedAsRejected()
        {
            // The inert-tree holder must see the codec's rejection, or a tree whose every
            // member is unreadable would be dropped instead of kept inert.
            var tree = new ConfigNode("RECORDING_TREE");
            tree.AddValue("id", "tree-x");
            tree.AddValue("recordingSchemaGeneration",
                RecordingStore.CurrentRecordingSchemaGeneration.ToString(CultureInfo.InvariantCulture));
            ConfigNode recNode = SavedRecordingNode("rec-t", MergeState.Immutable);
            recNode.SetValue("mergeState", "Bogus", true);
            tree.AddNode(recNode);

            Assert.Equal("recordings-merge-state-unknown",
                InertRecordingTrees.ClassifySchemaRejection(tree, null));
        }

        [Fact]
        public void CommittedTreeWithOneUnreadableRecording_IsSetAsideWhole()
        {
            // A committed tree is never pruned (T6): one unreadable member beside a readable
            // one keeps the whole tree inert, while the active / pending prune is unchanged.
            var tree = new ConfigNode("RECORDING_TREE");
            tree.AddValue("id", "tree-y");
            tree.AddValue("recordingSchemaGeneration",
                RecordingStore.CurrentRecordingSchemaGeneration.ToString(CultureInfo.InvariantCulture));
            tree.AddNode(SavedRecordingNode("rec-ok", MergeState.Immutable));
            ConfigNode bad = SavedRecordingNode("rec-bad", MergeState.Immutable);
            bad.SetValue("mergeState", "Bogus", true);
            tree.AddNode(bad);

            Assert.Null(InertRecordingTrees.ClassifySchemaRejection(tree, null));
            Assert.Equal("some-recordings-merge-state-unknown",
                InertRecordingTrees.ClassifyCommittedTreeSchemaRejection(tree, null));
        }

        // ---------------------------------------------------------------- ledger (R8)

        private static ConfigNode LedgerNode(params ConfigNode[] rows)
        {
            ConfigNode root = Ledger.BuildSaveNode(); // version + generation stamps, plus whatever the ledger holds
            for (int i = 0; i < rows.Length; i++)
                root.AddNode(rows[i]);
            return root;
        }

        private static ConfigNode Row(string ut, string type)
        {
            var row = new ConfigNode("GAME_ACTION");
            row.AddValue("ut", ut);
            if (type != null)
                row.AddValue("type", type);
            return row;
        }

        [Fact]
        public void UnknownActionType_IsKeptVerbatim_NotReadAsScienceEarning()
        {
            var good = new GameAction { UT = 10, Type = GameActionType.FundsSpending, FundsSpent = 50f, Sequence = 1 };
            var parent = new ConfigNode("P");
            good.SerializeInto(parent);
            ConfigNode future = Row("20", "9999");
            future.AddValue("futureField", "abc");

            Assert.True(Ledger.LoadFromNode(LedgerNode(parent.GetNode("GAME_ACTION"), future)));

            Assert.Single(Ledger.Actions);
            Assert.Equal(GameActionType.FundsSpending, Ledger.Actions[0].Type);
            Assert.Equal(1, Ledger.UnreadableActionCount);
            Assert.Contains(logLines, l => l.Contains("[Ledger]")
                && l.Contains("Kept unreadable action at index 1 verbatim") && l.Contains("reason=type-unknown"));

            // Saved back exactly as written, beside the readable row.
            ConfigNode saved = Ledger.BuildSaveNode();
            ConfigNode[] rows = saved.GetNodes("GAME_ACTION");
            Assert.Equal(2, rows.Length);
            Assert.Contains(rows, r => r.GetValue("type") == "9999" && r.GetValue("futureField") == "abc"
                && r.GetValue("ut") == "20");
        }

        [Theory]
        [InlineData(null, "type-missing")]
        [InlineData("science", "type-unparseable")]
        [InlineData("9999", "type-unknown")]
        public void ClassifyUnreadableActionNode_NamesTheReason(string type, string expected)
        {
            Assert.Equal(expected, GameAction.ClassifyUnreadableActionNode(Row("1", type), out _));
        }

        [Fact]
        public void ClassifyUnreadableActionNode_DefinedTypeIsReadable()
        {
            string id = ((int)GameActionType.FundsEarning).ToString(CultureInfo.InvariantCulture);
            Assert.Null(GameAction.ClassifyUnreadableActionNode(Row("1", id), out int typeId));
            Assert.Equal((int)GameActionType.FundsEarning, typeId);
        }

        [Fact]
        public void UnreadableRows_SurviveClear_AndAreReplacedByTheNextLoad()
        {
            ConfigNode emptyLedger = LedgerNode(); // built while the ledger is empty
            Assert.True(Ledger.LoadFromNode(LedgerNode(Row("5", "9999"))));
            Assert.Equal(1, Ledger.UnreadableActionCount);

            // Clear() restores a captured action list of the same save (the rewind bundle).
            Ledger.Clear();
            Assert.Equal(1, Ledger.UnreadableActionCount);

            Assert.True(Ledger.LoadFromNode(emptyLedger));
            Assert.Equal(0, Ledger.UnreadableActionCount);
        }
    }
}
