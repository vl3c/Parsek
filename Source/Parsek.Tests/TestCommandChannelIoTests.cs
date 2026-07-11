using System.Text;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the channel-I/O decisions (<see cref="TestCommandChannelIo"/>):
    /// the whole-line byte-offset advance rule and the bounded append-retry / backoff
    /// policy. The raw FileStream work lives in the addon and is exercised in-game; these
    /// are the parts of it that must be provably correct without Unity - a wrong offset
    /// advance would re-read or drop a command line, and a wrong retry bound would either
    /// spin forever or drop an ack.
    /// </summary>
    public class TestCommandChannelIoTests
    {
        // ----- Offset advance -----

        [Fact]
        public void WholeLineByteCount_AdvancesPastLastNewline_LeavingTornFragment()
        {
            byte[] buffer = Encoding.UTF8.GetBytes("id=1 cmd=A\nid=2 cmd=B\nid=3 cmd");
            int whole = TestCommandChannelIo.WholeLineByteCount(buffer, buffer.Length);
            // One past the SECOND '\n'; the "id=3 cmd" fragment is not consumed.
            Assert.Equal("id=1 cmd=A\nid=2 cmd=B\n".Length, whole);
        }

        [Fact]
        public void WholeLineByteCount_NoNewline_ConsumesNothing()
        {
            byte[] buffer = Encoding.UTF8.GetBytes("id=1 cmd=A");
            Assert.Equal(0, TestCommandChannelIo.WholeLineByteCount(buffer, buffer.Length));
        }

        [Fact]
        public void WholeLineByteCount_TrailingNewline_ConsumesAll()
        {
            byte[] buffer = Encoding.UTF8.GetBytes("id=1 cmd=A\n");
            Assert.Equal(buffer.Length, TestCommandChannelIo.WholeLineByteCount(buffer, buffer.Length));
        }

        [Fact]
        public void WholeLineByteCount_CountBoundsScan_IgnoresBytesBeyondCount()
        {
            // A newline lies beyond the reported read count; it must not be consumed.
            byte[] buffer = Encoding.UTF8.GetBytes("id=1\nXXXX");
            int reportedRead = 4; // "id=1" only; the '\n' at index 4 is beyond the read
            Assert.Equal(0, TestCommandChannelIo.WholeLineByteCount(buffer, reportedRead));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void WholeLineByteCount_NonPositiveCount_IsZero(int count)
        {
            byte[] buffer = Encoding.UTF8.GetBytes("id=1 cmd=A\n");
            Assert.Equal(0, TestCommandChannelIo.WholeLineByteCount(buffer, count));
        }

        [Fact]
        public void WholeLineByteCount_NullBuffer_IsZero()
        {
            Assert.Equal(0, TestCommandChannelIo.WholeLineByteCount(null, 5));
        }

        [Fact]
        public void WholeLineByteCount_Crlf_ConsumesThroughLf()
        {
            byte[] buffer = Encoding.UTF8.GetBytes("id=1 cmd=A\r\nid=2");
            // Advance past the '\n' (CRLF included); "id=2" fragment left behind.
            Assert.Equal("id=1 cmd=A\r\n".Length, TestCommandChannelIo.WholeLineByteCount(buffer, buffer.Length));
        }

        // ----- Retry / backoff -----

        [Fact]
        public void ShouldRetryAppend_RetriesUntilLastAttempt()
        {
            int max = TestCommandChannelIo.MaxAppendAttempts;
            for (int attempt = 1; attempt < max; attempt++)
                Assert.True(TestCommandChannelIo.ShouldRetryAppend(attempt, max));
            // The final failure gives up (exhaustion leaves the id at EXECUTED).
            Assert.False(TestCommandChannelIo.ShouldRetryAppend(max, max));
        }

        [Fact]
        public void BackoffMillis_ScalesLinearly_AndFloorsAtOne()
        {
            Assert.Equal(TestCommandChannelIo.BaseBackoffMillis * 1, TestCommandChannelIo.BackoffMillis(1));
            Assert.Equal(TestCommandChannelIo.BaseBackoffMillis * 3, TestCommandChannelIo.BackoffMillis(3));
            // A non-positive attempt is treated as the first attempt (never a zero delay).
            Assert.Equal(TestCommandChannelIo.BaseBackoffMillis, TestCommandChannelIo.BackoffMillis(0));
        }

        // ----- File-name constants (the fixed KSP-root channel) -----

        [Fact]
        public void ChannelFileNames_AreTheFixedContract()
        {
            Assert.Equal("parsek-test-commands.txt", TestCommandChannelIo.CommandFileName);
            Assert.Equal("parsek-test-responses.txt", TestCommandChannelIo.ResponseFileName);
            Assert.Equal("parsek-test-commands.journal", TestCommandChannelIo.JournalFileName);
            Assert.Equal("parsek-test-commands.lock", TestCommandChannelIo.LockFileName);
        }
    }
}
