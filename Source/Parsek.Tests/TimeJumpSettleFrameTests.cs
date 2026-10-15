using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The map probe's time-jump settle window (todo V16M-ICON-OFF-ORBIT-AT-TIMEJUMP-LANDING).
    /// A jump sets the clock mid-frame while body rotation and <c>Planetarium.Zup</c> catch up on
    /// the next physics tick, so the icon-off-orbit check skips the jump frame and one frame after.
    /// </summary>
    [Collection("Sequential")]
    public class TimeJumpSettleFrameTests : IDisposable
    {
        public void Dispose()
        {
            MapRenderTrace.TimeJumpFrameOverrideForTesting = null;
        }

        [Theory]
        [InlineData(100, 100, true)]   // the jump frame itself
        [InlineData(101, 100, true)]   // the one frame of slack
        [InlineData(102, 100, false)]  // settled: a real off-orbit icon reports here
        [InlineData(99, 100, false)]   // before the jump
        [InlineData(100, int.MinValue, false)] // no jump yet
        public void IsTimeJumpSettleFrame_CoversTheJumpFrameAndOneMore(
            int currentFrame, int jumpFrame, bool expected)
        {
            Assert.Equal(expected, MapRenderTrace.IsTimeJumpSettleFrame(currentFrame, jumpFrame));
        }

        [Fact]
        public void LastTimeJumpFrame_HonoursTheTestOverride()
        {
            MapRenderTrace.TimeJumpFrameOverrideForTesting = () => 4242;
            Assert.Equal(4242, MapRenderTrace.LastTimeJumpFrame());
        }

        [Fact]
        public void BothJumpPaths_StampTheClockSetFrameRightAfterSettingTheClock()
        {
            string source = SourceScanText.StripCSharpComments(ReadParsekSource("TimeJumpManager.cs"));
            const string arm = "ArmTimeJumpLaunchAutoRecordSuppression(";

            string helper = MethodBody(source, "private static void ArmTimeJumpLaunchAutoRecordSuppression(");
            Assert.Contains("LastClockSetFrame = Time.frameCount;", helper);

            foreach (string signature in new[]
            {
                "internal static void ExecuteJump(",
                "internal static void ExecuteForwardJump(",
            })
            {
                string body = MethodBody(source, signature);
                int setUt = body.IndexOf("Planetarium.SetUniversalTime(targetUT);", StringComparison.Ordinal);
                int stamp = body.IndexOf(arm, StringComparison.Ordinal);
                Assert.True(setUt >= 0, signature + " no longer sets the clock");
                Assert.True(stamp > setUt, signature + " must stamp the clock-set frame after setting the clock");
                Assert.True(stamp - setUt < 200, signature + " must stamp the clock-set frame right after setting the clock");
            }
        }

        [Fact]
        public void Probe_GatesIconOffOrbitOnTheSettleWindow()
        {
            string source = SourceScanText.StripCSharpComments(ReadParsekSource("MapRenderProbe.cs"));
            int settle = source.IndexOf(
                "MapRenderTrace.IsTimeJumpSettleFrame(frame, MapRenderTrace.LastTimeJumpFrame())",
                StringComparison.Ordinal);
            int gate = source.IndexOf("if (offOrbitAngle && !timeJumpSettling", StringComparison.Ordinal);
            int emit = source.IndexOf("\"icon-off-orbit\",", StringComparison.Ordinal);
            Assert.True(settle >= 0, "the probe no longer reads the settle window");
            Assert.True(gate > settle, "the off-orbit raise is no longer gated on the settle window");
            Assert.True(emit > gate, "the off-orbit raise moved out from under the settle gate");
        }

        private static string MethodBody(string source, string signature)
        {
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
