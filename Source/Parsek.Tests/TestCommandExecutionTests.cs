using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the execution-outcome decisions
    /// (<see cref="TestCommandExecution"/>): an executor throw is contained as a terminal
    /// ERROR (so the id completes and the pump advances rather than the whole seam wedging
    /// on an unhandled throw), and only the two-phase pending sentinel holds the FIFO head.
    /// </summary>
    public class TestCommandExecutionTests
    {
        [Fact]
        public void ExceptionTerminal_ProducesError_WithExceptionTypeName()
        {
            TestCommandExecution.ExceptionTerminal("InvalidOperationException",
                out string verdict, out string msg);
            Assert.Equal("ERROR", verdict);
            Assert.Contains("InvalidOperationException", msg);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ExceptionTerminal_NullOrEmptyType_StillError(string type)
        {
            TestCommandExecution.ExceptionTerminal(type, out string verdict, out string msg);
            Assert.Equal("ERROR", verdict);
            Assert.False(string.IsNullOrEmpty(msg));
        }

        [Fact]
        public void ErrorVerdict_AdvancesQueue()
        {
            // The contained-exception ERROR is a real terminal verdict -> the pump advances.
            Assert.True(TestCommandExecution.AdvancesQueue("ERROR"));
        }

        [Theory]
        [InlineData("OK")]
        [InlineData("REJECTED")]
        [InlineData("TIMEOUT")]
        [InlineData("INTERRUPTED")]
        public void TerminalVerdicts_AdvanceQueue(string verdict)
        {
            Assert.True(TestCommandExecution.AdvancesQueue(verdict));
        }

        [Fact]
        public void PendingVerdict_HoldsHead_DoesNotAdvance()
        {
            Assert.False(TestCommandExecution.AdvancesQueue(TestCommandExecution.PendingVerdict));
        }
    }
}
