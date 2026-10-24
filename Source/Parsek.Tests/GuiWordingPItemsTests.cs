using System.IO;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the wording fixes of the GUI exposure audit's P-items (todo GUI-P3, P9, P11, P19):
    /// each text says what the control or the refusal actually is, and names no control that
    /// the state it describes does not draw.
    /// </summary>
    public class GuiWordingPItemsTests
    {
        [Fact]
        public void P19_CandidatesEmptyState_WithNearMisses_PointsAtTheListInsteadOfTheInstructions()
        {
            string withNearMisses = LogisticsWindowUI.CandidatesEmptyStateText(3);
            string withoutNearMisses = LogisticsWindowUI.CandidatesEmptyStateText(0);

            Assert.Contains("list below", withNearMisses);
            Assert.DoesNotContain("Fly a cargo run", withNearMisses);
            Assert.Contains("Fly a cargo run", withoutNearMisses);
            Assert.DoesNotContain("list below", withoutNearMisses);
        }

        [Fact]
        public void P11_MergeAndSealFailure_NamesNoSealControl()
        {
            string text = MergeDialog.MergeAndSealFailedMessage;

            Assert.Contains("could not be sealed", text);
            Assert.DoesNotContain("Timeline", text);
            Assert.DoesNotContain("seal it from", text);
        }

        [Fact]
        public void P11_MergeAndSealFailure_BothFailurePathsUseTheSharedMessage()
        {
            string source = File.ReadAllText(Path.Combine(RepoRoot(), "Source", "Parsek", "MergeDialog.Commit.cs"));

            Assert.DoesNotContain("seal it from the Timeline window", source);
            Assert.Equal(3, CountOccurrences(source, "MergeAndSealFailedMessage"));
        }

        [Fact]
        public void P9_RenameHovers_StateTheFolderNameRules()
        {
            Assert.Contains("in use", MissionsWindowUI.MissionHeaderRenameHoverOriginal);
            Assert.Contains("= { }", MissionsWindowUI.MissionHeaderRenameHoverOriginal);
            Assert.Contains("Double-click to rename", MissionsWindowUI.MissionHeaderRenameHoverClone);

            string tableSource = File.ReadAllText(
                Path.Combine(RepoRoot(), "Source", "Parsek", "UI", "RecordingsTableUI.cs"));
            Assert.Contains("double-click to rename (a name in use, or one with = { }, is refused)", tableSource);
        }

        [Fact]
        public void P9_RuleText_MatchesTheCharactersTheStoreRefuses()
        {
            Assert.True(RecordingGroupStore.IsInvalidGroupName("a=b"));
            Assert.True(RecordingGroupStore.IsInvalidGroupName("a{b"));
            Assert.True(RecordingGroupStore.IsInvalidGroupName("a}b"));
            Assert.False(RecordingGroupStore.IsInvalidGroupName("Mun Lander 2"));
        }

        [Fact]
        public void P3_WindowedIntervalField_ReasonNamesTheStepperNotATypedInterval()
        {
            string reason = LogisticsWindowUI.WindowedIntervalFieldDisabledReason;

            Assert.Contains("launch windows", reason);
            Assert.Contains("-/+", reason);
            Assert.Contains("Nth window", reason);
        }

        private static int CountOccurrences(string text, string token)
        {
            int count = 0;
            int at = 0;
            while ((at = text.IndexOf(token, at, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += token.Length;
            }
            return count;
        }

        private static string RepoRoot()
        {
            return Path.GetFullPath(Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
        }
    }
}
