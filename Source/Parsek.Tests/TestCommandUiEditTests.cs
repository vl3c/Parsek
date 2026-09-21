using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for <c>UiAction op=edit</c> (<see cref="TestCommandUiEdit"/>): the
    /// per-window field table, the two requiredness rules, the opt-in commit and the
    /// payload.
    ///
    /// <para>The property that carries the most weight is the COMMIT DEFAULT. It is FALSE,
    /// which is the opposite of how the seam's other write ops default, and the reason is
    /// that none of these commits is "write a name": a group or mission-title commit runs
    /// <c>MissionGroupLink.RenameMissionGroup</c>, which renames a root group plus its auto
    /// <c>/ Debris</c> and <c>/ Crew</c> subgroups plus <c>Mission.Name</c> atomically and
    /// rejects BOTH halves on a collision, and a recording commit silently drops a row that
    /// has left the committed list. A lane that only wanted the mid-edit picture must not be
    /// able to rename the fixture's history by omitting an arg.</para>
    /// </summary>
    public class TestCommandUiEditTests
    {
        [Theory]
        [InlineData("recordingname")]
        [InlineData("groupname")]
        [InlineData("missiontitle")]
        public void EveryDriveableEditorIsOnTheMissionsWindow(string field)
        {
            Assert.True(TestCommandUiEdit.TryResolveField(
                TestCommandUiAction.MissionsWindow, field, out UiEditFieldSpec spec,
                out string reject));
            Assert.Null(reject);
            Assert.Equal(TestCommandUiAction.MissionsWindow, spec.Window);
            Assert.Equal(field, spec.Field);
            // The reject message has to say what the key means, and the three are not
            // interchangeable (a RecordingId, a group NAME, a Mission id).
            Assert.False(string.IsNullOrEmpty(spec.KeyMeaning));
        }

        [Fact]
        public void TheThreeKeyMeaningsAreDistinct()
        {
            var meanings = new List<string>();
            foreach (string field in new[] { "recordingname", "groupname", "missiontitle" })
            {
                TestCommandUiEdit.TryResolveField(
                    TestCommandUiAction.MissionsWindow, field, out UiEditFieldSpec spec,
                    out string _);
                meanings.Add(spec.KeyMeaning);
            }
            Assert.Equal(meanings.Count, meanings.Distinct().Count());
        }

        [Fact]
        public void OnlyTheMissionsWindowOwnsAnEditor()
        {
            Assert.True(TestCommandUiEdit.WindowHasEditors(
                TestCommandUiAction.MissionsWindow));
            foreach (UiWindowSpec spec in TestCommandUiAction.Windows)
            {
                if (spec.Name == TestCommandUiAction.MissionsWindow) continue;
                Assert.False(TestCommandUiEdit.WindowHasEditors(spec.Name), spec.Name);
            }
            Assert.Equal(TestCommandUiAction.MissionsWindow,
                         TestCommandUiEdit.EditableWindowNames);
        }

        [Fact]
        public void TheFieldNameListIsWhatTheRejectMessageCarries()
        {
            string[] names = TestCommandUiEdit.FieldNamesFor(
                TestCommandUiAction.MissionsWindow).Split(',');
            Assert.Equal(
                new[] { "recordingname", "groupname", "missiontitle" }, names);
            Assert.Equal(string.Empty,
                TestCommandUiEdit.FieldNamesFor(TestCommandUiAction.TimelineWindow));
        }

        [Fact]
        public void AWindowWithNoEditorIsItsOwnReject()
        {
            Assert.False(TestCommandUiEdit.TryResolveField(
                TestCommandUiAction.LogisticsWindow, "recordingname",
                out UiEditFieldSpec _, out string reject));
            // Distinct from field-invalid on purpose: "this window has no editors" and
            // "this window has editors but not that one" send an author to different fixes.
            Assert.Equal(TestCommandUiEdit.EditUnsupportedWindowReason, reject);
        }

        [Fact]
        public void AMissingFieldIsDistinctFromAnUnknownOne()
        {
            Assert.False(TestCommandUiEdit.TryResolveField(
                TestCommandUiAction.MissionsWindow, null, out UiEditFieldSpec _,
                out string missing));
            Assert.Equal(TestCommandUiEdit.EditFieldArgMissingReason, missing);

            Assert.False(TestCommandUiEdit.TryResolveField(
                TestCommandUiAction.MissionsWindow, "", out UiEditFieldSpec _,
                out string empty));
            Assert.Equal(TestCommandUiEdit.EditFieldArgMissingReason, empty);

            Assert.False(TestCommandUiEdit.TryResolveField(
                TestCommandUiAction.MissionsWindow, "nosuchfield", out UiEditFieldSpec _,
                out string unknown));
            Assert.Equal(TestCommandUiEdit.EditFieldInvalidReason, unknown);
        }

        [Fact]
        public void TheFieldParseIsCaseSensitive()
        {
            Assert.False(TestCommandUiEdit.TryResolveField(
                TestCommandUiAction.MissionsWindow, "GroupName", out UiEditFieldSpec _,
                out string reject));
            Assert.Equal(TestCommandUiEdit.EditFieldInvalidReason, reject);
        }

        [Fact]
        public void AnAbsentCommitMeansFalse()
        {
            Assert.True(TestCommandUiEdit.TryParseCommit(null, out bool commit,
                                                         out string reject));
            Assert.False(commit);
            Assert.Null(reject);
        }

        [Fact]
        public void TheCommitParseTakesTheOneBoolVocabulary()
        {
            Assert.True(TestCommandUiEdit.TryParseCommit("true", out bool yes,
                                                         out string yesReject));
            Assert.True(yes);
            Assert.Null(yesReject);
            Assert.True(TestCommandUiEdit.TryParseCommit("false", out bool no,
                                                         out string noReject));
            Assert.False(no);
            Assert.Null(noReject);
            foreach (string bad in new[] { "True", "yes", "1", "" })
            {
                Assert.False(TestCommandUiEdit.TryParseCommit(bad, out bool _,
                                                              out string reject), bad);
                Assert.Equal(TestCommandUiEdit.EditCommitArgInvalidReason, reject);
            }
        }

        [Fact]
        public void AnAbsentDraftIsTheEmptyStringAndNotAStaleBuffer()
        {
            // Nothing is in the buffer before the arm - the arm is what creates it - so the
            // only other reading would be a draft left over from a previous edit, which is
            // a state no click produces.
            Assert.Equal(string.Empty, TestCommandUiEdit.ResolveDraft(null));
            Assert.Equal(string.Empty, TestCommandUiEdit.ResolveDraft(""));
            Assert.Equal("Renamed", TestCommandUiEdit.ResolveDraft("Renamed"));
        }

        [Fact]
        public void ThePayloadReportsTheArmAndTheCommitSeparately()
        {
            List<KeyValuePair<string, string>> payload =
                TestCommandUiEdit.BuildEditPayload(
                    TestCommandUiAction.MissionsWindow, "groupname", "X / Debris",
                    "Renamed", committed: false, armed: true);
            Assert.Equal(
                new[] { "op", "window", "field", "key", "draft", "committed", "armed" },
                payload.Select(kv => kv.Key).ToArray());
            Assert.Equal(TestCommandUiAction.EditOpToken, Value(payload, "op"));
            Assert.Equal("groupname", Value(payload, "field"));
            Assert.Equal("X / Debris", Value(payload, "key"));
            Assert.Equal("Renamed", Value(payload, "draft"));
            Assert.Equal("false", Value(payload, "committed"));
            Assert.Equal("true", Value(payload, "armed"));
        }

        [Fact]
        public void ACommitStepReportsArmedFalseByDesign()
        {
            // The commit body clears the row-key sentinel, so `armed=false` beside
            // `committed=true` is the success shape - which is why the two are reported
            // separately rather than one being inferred from the other.
            List<KeyValuePair<string, string>> payload =
                TestCommandUiEdit.BuildEditPayload(
                    TestCommandUiAction.MissionsWindow, "missiontitle", "m17", "Renamed",
                    committed: true, armed: false);
            Assert.Equal("true", Value(payload, "committed"));
            Assert.Equal("false", Value(payload, "armed"));
        }

        [Fact]
        public void ThePayloadNeverCarriesANullValue()
        {
            List<KeyValuePair<string, string>> payload =
                TestCommandUiEdit.BuildEditPayload(null, null, null, null, false, false);
            Assert.All(payload, kv => Assert.NotNull(kv.Value));
        }

        [Fact]
        public void TheEditOpIsRegisteredWindowScopedAndTwoPhase()
        {
            Assert.True(TestCommandUiAction.TryParseOp(
                TestCommandUiAction.EditOpToken, out UiActionOp op, out string reject));
            Assert.Null(reject);
            Assert.Equal(UiActionOp.Edit, op);
            Assert.Equal(TestCommandUiAction.EditOpToken, TestCommandUiAction.OpToken(op));
            Assert.True(TestCommandUiAction.OpNeedsWindow(op));
            // Two-phase: the drawn frame is what turns the sentinel into a drawn text
            // field, which is the whole state this op exists to photograph.
            Assert.True(TestCommandUiAction.OpIsTwoPhase(op));
            Assert.False(TestCommandUiAction.SettleChecksHostShowUi(op));
            Assert.Contains(TestCommandUiAction.EditOpToken,
                TestCommandUiAction.ValidOpNames.Split(','));
        }

        [Fact]
        public void TheThreeArgKeysDoNotCollideWithAnotherOpsKeys()
        {
            // `draft` and not `text`: op=find owns `text=`, and the harness's arg table
            // admits one owner op per key, so a `text=` here would be flagged pre-launch as
            // an arg only op=find reads.
            Assert.Equal("draft", TestCommandUiEdit.DraftArg);
            Assert.Equal("field", TestCommandUiEdit.FieldArg);
            Assert.Equal("commit", TestCommandUiEdit.CommitArg);
            Assert.NotEqual(TestCommandUiEdit.DraftArg, "text");
            // The row key REUSES the one `key=` arg, whose four owner ops each parse it
            // their own way.
            Assert.Equal("key", TestCommandUiState.KeyArg);
        }

        private static string Value(List<KeyValuePair<string, string>> payload, string key)
            => payload.First(kv => kv.Key == key).Value;
    }
}
