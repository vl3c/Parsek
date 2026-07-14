using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the <see cref="TestCommandSaveGame"/> decider (M-C1.1 follow-up):
    /// the name default ("persistent"), the can-save gate (game loaded AND save folder), and
    /// the OK payload shape (saved=&lt;name&gt;). The Unity applier (SaveGameImpl) is the
    /// thin call site; these guard the pure decisions without KSP.
    /// </summary>
    public class TestCommandSaveGameTests
    {
        [Theory]
        [InlineData(null, "persistent")]
        [InlineData("", "persistent")]
        [InlineData("persistent", "persistent")]
        [InlineData("quicksave2", "quicksave2")]
        public void ResolveName_DefaultsToPersistent(string arg, string expected)
        {
            Assert.Equal(expected, TestCommandSaveGame.ResolveName(arg));
        }

        [Theory]
        [InlineData(true, true, true)]    // game + save folder -> may save
        [InlineData(true, false, false)]  // no save folder resolved
        [InlineData(false, true, false)]  // no game loaded (e.g. MAINMENU)
        [InlineData(false, false, false)]
        public void CanSave_RequiresGameAndFolder(bool gameLoaded, bool saveFolderPresent, bool expected)
        {
            Assert.Equal(expected, TestCommandSaveGame.CanSave(gameLoaded, saveFolderPresent));
        }

        [Fact]
        public void BuildPayload_CarriesSavedName()
        {
            var payload = TestCommandSaveGame.BuildPayload("persistent");
            Assert.Single(payload);
            Assert.Equal("saved", payload[0].Key);
            Assert.Equal("persistent", payload[0].Value);
        }

        [Fact]
        public void BuildPayload_NullNameIsEmptyString()
        {
            var payload = TestCommandSaveGame.BuildPayload(null);
            Assert.Equal("saved", payload[0].Key);
            Assert.Equal(string.Empty, payload[0].Value);
        }
    }
}
