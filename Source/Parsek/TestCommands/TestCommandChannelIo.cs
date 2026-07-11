using System;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure channel-I/O decisions and constants for the ParsekTestCommands seam (M-A2):
    /// the four fixed KSP-root file names, the persistent-byte-offset advance rule, and
    /// the bounded append-retry / backoff policy. No UnityEngine / KSP / System.IO usage
    /// - the raw <c>FileStream</c> work lives in <see cref="ParsekTestCommandAddon"/>,
    /// and these decisions are the parts of it exercised by xUnit without Unity.
    /// </summary>
    internal static class TestCommandChannelIo
    {
        // ----- Fixed channel file names (KSP root; see the design's file-location table) -----

        internal const string CommandFileName = "parsek-test-commands.txt";
        internal const string ResponseFileName = "parsek-test-responses.txt";
        internal const string JournalFileName = "parsek-test-commands.journal";
        internal const string LockFileName = "parsek-test-commands.lock";

        // ----- Append retry policy -----

        /// <summary>Bounded append attempts. On exhaustion the addon leaves the id at
        /// EXECUTED (the response is (re)written next frame / next restart WITHOUT
        /// re-executing), so an ack is never silently dropped.</summary>
        internal const int MaxAppendAttempts = 5;

        /// <summary>Base backoff step (ms); the delay scales linearly with the attempt so
        /// a transient reader lock clears without an unbounded spin.</summary>
        internal const int BaseBackoffMillis = 20;

        // ----- Offset advance -----

        /// <summary>
        /// The number of leading bytes in <paramref name="buffer"/><c>[0..count)</c> that
        /// form COMPLETE, newline-terminated content: one past the last <c>\n</c>, or 0 if
        /// there is no <c>\n</c>. A torn trailing fragment (bytes after the last <c>\n</c>)
        /// is NOT consumed, so the persistent byte offset advances only over whole lines
        /// and the fragment is re-read intact on the next poll.
        /// </summary>
        internal static int WholeLineByteCount(byte[] buffer, int count)
        {
            if (buffer == null || count <= 0) return 0;
            int limit = Math.Min(count, buffer.Length);
            for (int i = limit - 1; i >= 0; i--)
            {
                if (buffer[i] == (byte)'\n')
                    return i + 1;
            }
            return 0;
        }

        // ----- Retry decision -----

        /// <summary>
        /// True while an append that has just failed its <paramref name="attempt"/>-th try
        /// (1-based) may retry: attempts <c>1..maxAttempts-1</c> retry; the
        /// <paramref name="maxAttempts"/>-th failure gives up this frame.
        /// </summary>
        internal static bool ShouldRetryAppend(int attempt, int maxAttempts)
        {
            return attempt < maxAttempts;
        }

        /// <summary>
        /// Bounded backoff (ms) before append retry <paramref name="attempt"/> (1-based):
        /// linear <see cref="BaseBackoffMillis"/> * attempt.
        /// </summary>
        internal static int BackoffMillis(int attempt)
        {
            if (attempt < 1) attempt = 1;
            return BaseBackoffMillis * attempt;
        }
    }
}
