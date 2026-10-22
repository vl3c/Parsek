using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek.Patches;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure half of the <c>CycleVessel</c> seam verb (GC-4: stock's [ / ] cycle must skip a
    /// loaded ghost map marker) and the seam's hook into the cycle Prefix.
    /// </summary>
    public class TestCommandCycleVesselTests
    {
        [Theory]
        [InlineData(null, null, "cyclevessel-direction-arg-missing")]
        [InlineData(" ", null, "cyclevessel-direction-arg-missing")]
        [InlineData("Next", null, "cyclevessel-direction-arg-invalid")]
        [InlineData("up", null, "cyclevessel-direction-arg-invalid")]
        [InlineData("next", "yes", "cyclevessel-forceghost-arg-invalid")]
        [InlineData("prev", "", "cyclevessel-forceghost-arg-invalid")]
        public void ParseArgs_Refuses(string direction, string forceGhost, string expected)
        {
            Assert.Equal(expected, TestCommandCycleVessel.ParseArgs(direction, forceGhost, out _, out _));
            Assert.Contains(expected, TestCommandCycleVessel.Reasons);
        }

        [Theory]
        [InlineData("next", null, false, false)]
        [InlineData("prev", null, true, false)]
        [InlineData("prev", "true", true, true)]
        [InlineData("next", "TRUE", false, true)]
        [InlineData("next", "false", false, false)]
        public void ParseArgs_Accepts(string direction, string forceGhost, bool previous, bool force)
        {
            Assert.Null(TestCommandCycleVessel.ParseArgs(direction, forceGhost, out bool p, out bool f));
            Assert.Equal(previous, p);
            Assert.Equal(force, f);
        }

        [Fact]
        public void Reasons_AreTheDeclaredConstants_InGateOrder()
        {
            Assert.Equal(new[]
            {
                TestCommandCycleVessel.DirectionArgMissingReason,
                TestCommandCycleVessel.DirectionArgInvalidReason,
                TestCommandCycleVessel.ForceGhostArgInvalidReason,
                TestCommandCycleVessel.NoActiveVesselReason,
            }, TestCommandCycleVessel.Reasons);
        }

        [Fact]
        public void Outcomes_NameEveryCycleOutcome_InDeclarationOrder_ThenInert()
        {
            var members = (VesselCycleReFlySwitchPatch.CycleOutcome[])Enum.GetValues(
                typeof(VesselCycleReFlySwitchPatch.CycleOutcome));
            Assert.Equal(members.Length + 1, TestCommandCycleVessel.Outcomes.Length);
            for (int i = 0; i < members.Length; i++)
            {
                Assert.Equal(i, (int)members[i]);
                Assert.Equal(TestCommandCycleVessel.Outcomes[i], TestCommandCycleVessel.OutcomeToken(members[i]));
            }
            Assert.Equal("inert", TestCommandCycleVessel.Outcomes.Last());
            Assert.Equal("switched",
                TestCommandCycleVessel.OutcomeToken(VesselCycleReFlySwitchPatch.CycleOutcome.Switched));
            Assert.Equal("only-ghosts",
                TestCommandCycleVessel.OutcomeToken(VesselCycleReFlySwitchPatch.CycleOutcome.OnlyGhosts));
            Assert.Equal("unknown",
                TestCommandCycleVessel.OutcomeToken((VesselCycleReFlySwitchPatch.CycleOutcome)99));
        }

        [Fact]
        public void FirstGhostCandidate_IsTheFirstFlagged_InStockOrder()
        {
            var pids = new List<uint> { 7u, 4242u, 8u, 4343u };
            var ghosts = new List<bool> { false, true, false, true };
            Assert.Equal(4242u, TestCommandCycleVessel.FirstGhostCandidate(pids, ghosts));
            Assert.Equal(0u, TestCommandCycleVessel.FirstGhostCandidate(pids, new List<bool> { false, false, false, false }));
            Assert.Equal(0u, TestCommandCycleVessel.FirstGhostCandidate(new List<uint>(), new List<bool>()));
            Assert.Equal(0u, TestCommandCycleVessel.FirstGhostCandidate(null, null));
        }

        [Fact]
        public void DecideForceGhostResult_ReadsTheActiveVessel_NotOnlyTheAnswer()
        {
            // The backstop held: false and the active vessel stayed.
            Assert.Equal("refused", TestCommandCycleVessel.DecideForceGhostResult(false, 10u, 10u, 4242u));
            // The hole: the ghost became active, whatever the call answered.
            Assert.Equal("switched", TestCommandCycleVessel.DecideForceGhostResult(true, 10u, 4242u, 4242u));
            Assert.Equal("switched", TestCommandCycleVessel.DecideForceGhostResult(false, 10u, 4242u, 4242u));
            // Anything else is not the refusal the lane pins.
            Assert.Equal("unexpected", TestCommandCycleVessel.DecideForceGhostResult(true, 10u, 10u, 4242u));
            Assert.Equal("unexpected", TestCommandCycleVessel.DecideForceGhostResult(false, 10u, 11u, 4242u));
        }

        [Fact]
        public void StockPickIndex_MirrorsStocksFinalStep()
        {
            // First unpacked candidate; a packed one is taken only when the game is clear to save.
            Assert.Equal(1, VesselCycleReFlySwitchPatch.StockPickIndex(new List<bool> { true, false }, false));
            Assert.Equal(0, VesselCycleReFlySwitchPatch.StockPickIndex(new List<bool> { true, false }, true));
            // None qualifies: stock's SetActiveVessel(candidates[0]) fallback.
            Assert.Equal(0, VesselCycleReFlySwitchPatch.StockPickIndex(new List<bool> { true, true }, false));
            Assert.Equal(-1, VesselCycleReFlySwitchPatch.StockPickIndex(new List<bool>(), true));
            Assert.Equal(-1, VesselCycleReFlySwitchPatch.StockPickIndex(null, true));
        }

        [Fact]
        public void Payload_KeyOrder_AndValues()
        {
            var report = new VesselCycleReFlySwitchPatch.CycleReport
            {
                Outcome = VesselCycleReFlySwitchPatch.CycleOutcome.Switched,
                GhostsDropped = 1,
                PickedPid = 2614652043u,
                StockWouldPickPid = 4242u,
                StockWouldPickGhost = true,
            };
            report.CandidatePids.AddRange(new[] { 4242u, 2614652043u });
            report.CandidateIsGhost.AddRange(new[] { true, false });

            var payload = TestCommandCycleVessel.BuildOkPayload(true, "switched", null,
                3620499050u, 2614652043u, report, "skipped", 0u);
            Assert.Equal(new[]
            {
                "direction", "outcome", "detail", "before", "after", "switched", "candidates",
                "candidatePids", "ghostPids", "ghostsDropped", "refusedDropped", "leftToStock",
                "picked", "stockWouldPick", "stockWouldPickGhost", "forceGhost", "forceGhostPid",
            }, payload.Select(kv => kv.Key).ToArray());

            string line = TestCommandCycleVessel.FormatResultLine(payload);
            Assert.Equal("cyclevessel result direction=prev outcome=switched detail=none before=3620499050 "
                + "after=2614652043 switched=true candidates=2 candidatePids=4242,2614652043 ghostPids=4242 "
                + "ghostsDropped=1 refusedDropped=0 leftToStock=false picked=2614652043 stockWouldPick=4242 "
                + "stockWouldPickGhost=true forceGhost=skipped forceGhostPid=none", line);
        }

        [Theory]
        [InlineData("left-to-stock", "true")]
        [InlineData("stock-early-return", "true")]
        [InlineData("no-candidates", "true")]
        [InlineData("inert", "true")]
        [InlineData("only-ghosts", "false")]
        [InlineData("only-refused", "false")]
        [InlineData("switched", "false")]
        [InlineData("set-active-fallback", "false")]
        public void Payload_LeftToStock_IsThePrefixLettingStockRun(string outcome, string expected)
        {
            var payload = TestCommandCycleVessel.BuildOkPayload(false, outcome, "map-view",
                1u, 1u, null, "no-ghost", 0u);
            Assert.Equal(expected, payload.Single(kv => kv.Key == "leftToStock").Value);
            Assert.Equal("false", payload.Single(kv => kv.Key == "switched").Value);
            Assert.Equal("none", payload.Single(kv => kv.Key == "candidatePids").Value);
            Assert.Equal("none", payload.Single(kv => kv.Key == "ghostPids").Value);
        }

        private static string Source(params string[] parts)
        {
            var all = new List<string> { AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "Source", "Parsek" };
            all.AddRange(parts);
            return File.ReadAllText(Path.Combine(all.ToArray()));
        }

        [Fact]
        public void ThePrefix_RunsTheSharedBody_WithNoReport_AfterTheKeyReadAndTheGate()
        {
            string src = Source("Patches", "VesselCycleReFlySwitchPatch.cs");
            int prefix = src.IndexOf("static bool Prefix()", StringComparison.Ordinal);
            Assert.True(prefix >= 0);
            string body = src.Substring(prefix, src.IndexOf("internal static bool RunCycle(", StringComparison.Ordinal) - prefix);
            int key = body.IndexOf("GameSettings.FOCUS_PREV_VESSEL.GetKeyDown()", StringComparison.Ordinal);
            int gate = body.IndexOf("ParsekGameModeGate.CheckInert(\"VesselCycleReFlySwitchPatch.Prefix\")", StringComparison.Ordinal);
            int run = body.IndexOf("return RunCycle(prev, null);", StringComparison.Ordinal);
            Assert.True(key >= 0 && gate > key && run > gate, body);
        }

        [Fact]
        public void TheApplier_GatesThenRunsTheSameBody()
        {
            string src = Source("TestCommands", "ParsekTestCommandAddon.CycleVessel.cs");
            int gate = src.IndexOf("ParsekGameModeGate.CheckInert(", StringComparison.Ordinal);
            int run = src.IndexOf("VesselCycleReFlySwitchPatch.RunCycle(previous, report);", StringComparison.Ordinal);
            Assert.True(gate >= 0 && run > gate);
            Assert.Contains("FlightGlobals.ForceSetActiveVessel(ghost)", src);
        }
    }
}
