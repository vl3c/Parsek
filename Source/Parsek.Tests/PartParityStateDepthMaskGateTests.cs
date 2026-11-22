using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The depth-mask repair (<c>PartParityDepthMaskRepair</c>, PR #2760) must run on a state
    /// case too, before its real renderers are read: <c>PartParityCaseCapture.Prepare</c> hands
    /// a state case to <c>PartParityStateDriver.Prepare</c> and returns, so the repair call has
    /// to sit in that branch ahead of the hand-off, and the stowed path keeps its own call.
    /// Read from the comment-stripped source (the live repair needs KSP).
    /// </summary>
    public class PartParityStateDepthMaskGateTests
    {
        [Fact]
        public void TheStateBranch_RepairsDepthMasksBeforeTheDriverReadsTheRealRenderers()
        {
            string path = Path.Combine(SyntheticRecordingTests.ResolveProjectRoot(),
                "Source", "Parsek", "TestCommands", "PartParityCaseCapture.cs");
            string text = PartParityStateRulesTests.StripComments(File.ReadAllText(path));
            string body = PartParityStateRulesTests.MethodBody(text, "internal static void Prepare(StepContext ctx, WaveCase wc)");
            Assert.True(body != null, "PartParityCaseCapture.Prepare moved; this cell is vacuous");

            int branch = body.IndexOf("if (wc.State != null)", StringComparison.Ordinal);
            int handoff = body.IndexOf("PartParityStateDriver.Prepare(ctx, wc);", StringComparison.Ordinal);
            Assert.True(branch >= 0 && handoff > branch, "the state branch hand-off moved");
            string stateBranch = body.Substring(branch, handoff - branch);
            Assert.True(stateBranch.Contains("RepairDepthMasks(wc, wc.Real.rootPart);"),
                "the state branch hands off to the driver without repairing the real part's depth masks");

            string stowed = body.Substring(handoff);
            int repair = stowed.IndexOf("RepairDepthMasks(wc, part);", StringComparison.Ordinal);
            int records = stowed.IndexOf("wc.RealRecords = Records(", StringComparison.Ordinal);
            Assert.True(repair >= 0 && records > repair, "the stowed path no longer repairs before reading its records");
        }
    }
}
