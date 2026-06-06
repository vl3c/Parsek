using Parsek;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the M2 pure rename-commit decision
    /// <see cref="LogisticsRenamePresentation.ComputeRouteRename"/> behind the
    /// Logistics detail-panel rename field. The helper trims the typed text and
    /// reports a committable change only when the result is non-empty AND differs
    /// from the current name (the empty-name guard + the no-op guard). Unity-free,
    /// so exercised directly without IMGUI (the IMGUI deferred-commit wiring is
    /// covered by in-game validation, per the plan's section 7).
    /// </summary>
    public class LogisticsRouteRenameTests
    {
        // catches: a distinct non-empty trimmed name not being treated as a change.
        [Fact]
        public void DistinctName_IsChange_AndTrimmed()
        {
            bool changed = LogisticsRenamePresentation.ComputeRouteRename(
                "Old Route", "New Route", out string committed);
            Assert.True(changed);
            Assert.Equal("New Route", committed);
        }

        // catches: whitespace-only input overwriting the name with a blank (the
        // empty-name guard). Must report no change.
        [Theory]
        [InlineData("   ")]
        [InlineData("\t")]
        [InlineData("")]
        public void EmptyOrWhitespace_IsNotChange(string typed)
        {
            bool changed = LogisticsRenamePresentation.ComputeRouteRename(
                "Old Route", typed, out string committed);
            Assert.False(changed);
            // committed is the trimmed (empty) value; the caller does not write it.
            Assert.Equal(string.Empty, committed);
        }

        // catches: a null typed value throwing instead of being treated as empty.
        [Fact]
        public void NullTyped_IsNotChange()
        {
            bool changed = LogisticsRenamePresentation.ComputeRouteRename(
                "Old Route", null, out string committed);
            Assert.False(changed);
            Assert.Equal(string.Empty, committed);
        }

        // catches: a same-as-current value (after trimming) being re-written /
        // re-logged as a change.
        [Fact]
        public void SameAsCurrentAfterTrim_IsNotChange()
        {
            bool changed = LogisticsRenamePresentation.ComputeRouteRename(
                "Depot Run", "  Depot Run  ", out string committed);
            Assert.False(changed);
            Assert.Equal("Depot Run", committed);
        }

        // catches: leading / trailing whitespace around a real new name not being
        // stripped before the write.
        [Fact]
        public void SurroundingWhitespace_TrimmedAndIsChange()
        {
            bool changed = LogisticsRenamePresentation.ComputeRouteRename(
                "Old", "   Fresh Name   ", out string committed);
            Assert.True(changed);
            Assert.Equal("Fresh Name", committed);
        }

        // catches: renaming away from a null current name failing. A non-empty typed
        // name over a null current is a change.
        [Fact]
        public void NullCurrent_NonEmptyTyped_IsChange()
        {
            bool changed = LogisticsRenamePresentation.ComputeRouteRename(
                null, "First Name", out string committed);
            Assert.True(changed);
            Assert.Equal("First Name", committed);
        }
    }
}
