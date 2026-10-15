using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Source-order gate for the post-jump celestial-body refresh (todo
    /// V16M-ICON-OFF-ORBIT-AT-TIMEJUMP-LANDING). <c>Planetarium.SetUniversalTime</c> only sets the
    /// clock, so both jump paths run stock's <c>UpdateCBs</c> before the frame goes on. The
    /// epoch-shift path must do it AFTER the epoch shift and the chain-tip spawns, which convert
    /// pre-jump state vectors through the pre-jump <c>Planetarium.Zup</c>; refreshing first would
    /// rotate every shifted orbit by the main body's spin over the jump.
    /// </summary>
    public class TimeJumpBodyRefreshWiringTests
    {
        private const string RefreshCall = "RefreshCelestialBodiesAfterJump(targetUT);";

        [Fact]
        public void EpochShiftJump_RefreshesBodiesAfterTheShiftAndTheTipSpawns()
        {
            string body = MethodBody("internal static void ExecuteJump(");

            int setUt = IndexOnce(body, "Planetarium.SetUniversalTime(targetUT);");
            int shift = IndexOnce(body, "ApplyEpochShifts(capturedStates, targetUT);");
            int tips = IndexOnce(body, "SpawnCrossedChainTips(");
            int refresh = IndexOnce(body, RefreshCall);
            int ledger = IndexOnce(body, "RecalculateLedgerAfterTimeJump(\"epoch-shift\", targetUT);");

            Assert.True(setUt < shift, "the epoch shift must follow the clock set");
            Assert.True(shift < refresh, "the body refresh must follow the epoch shift");
            Assert.True(tips < refresh, "the body refresh must follow the chain-tip spawns");
            Assert.True(refresh < ledger, "the body refresh must precede the ledger recalculation");
        }

        [Fact]
        public void ForwardJump_RefreshesBodiesAfterTakingVesselsOffRails()
        {
            string body = MethodBody("internal static void ExecuteForwardJump(");

            int setUt = IndexOnce(body, "Planetarium.SetUniversalTime(targetUT);");
            int offRails = IndexOnce(body, "TakeVesselsOffRails(onRailsVessels);");
            int refresh = IndexOnce(body, RefreshCall);

            Assert.True(setUt < offRails);
            Assert.True(offRails < refresh, "the body refresh must follow taking vessels off rails");
        }

        [Fact]
        public void Refresh_UsesStockUpdateCBs()
        {
            string body = MethodBody("private static void RefreshCelestialBodiesAfterJump(");
            IndexOnce(body, "Planetarium.fetch.UpdateCBs();");
        }

        private static int IndexOnce(string haystack, string needle)
        {
            int first = haystack.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(first >= 0, "missing: " + needle);
            int second = haystack.IndexOf(needle, first + needle.Length, StringComparison.Ordinal);
            Assert.True(second < 0, "more than one occurrence of: " + needle);
            return first;
        }

        private static string MethodBody(string signature)
        {
            string source = SourceScanText.StripCSharpComments(ReadParsekSource("TimeJumpManager.cs"));
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, "signature not found: " + signature);
            int open = source.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return source.Substring(open, i - open + 1);
                }
            }
            throw new InvalidOperationException("unbalanced braces after " + signature);
        }

        private static string ReadParsekSource(string relPath)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(
                root, "Source", "Parsek", relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return File.ReadAllText(path);
        }
    }
}
