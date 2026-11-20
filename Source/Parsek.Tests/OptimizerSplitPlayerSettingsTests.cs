using System;
using System.Collections.Generic;
using Xunit;
using static Parsek.Tests.OptimizerSplitShapes;

namespace Parsek.Tests
{
    /// <summary>
    /// SPLIT-DROPS-PLAYER-ROW-SETTINGS (review H6, owner RULE 1 "always keep them"): an
    /// optimizer split keeps the player's hide / disable / loop / seal / group settings on
    /// both halves, never splits or trims a recording with a loop range, and the tail trim
    /// leaves a looped recording alone.
    /// </summary>
    [Collection("Sequential")]
    public class OptimizerSplitPlayerSettingsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public OptimizerSplitPlayerSettingsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = false;
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // An ascent that leaves the atmosphere for good: one persisted Atmospheric -> Exo cut.
        private static Recording Ascent(string id)
            => Make(id, 100, Atmo(200), Exo(600));

        [Fact]
        public void Split_KeepsHiddenDisabledLoopAndGroupsOnBothHalves()
        {
            var rec = Ascent("settings");
            rec.Hidden = true;
            rec.PlaybackEnabled = false;
            rec.LoopPlayback = true;
            rec.LoopIntervalSeconds = 900;
            rec.LoopTimeUnit = LoopTimeUnit.Min;
            rec.LoopAnchorVesselId = 77;
            rec.LoopAnchorBodyName = "Kerbin";
            rec.MergeState = MergeState.CommittedProvisional;
            rec.RecordingGroups = new List<string> { "Fleet" };
            CommitAlone(rec);

            CommittedListOptimizer.RunOptimizationPass();

            var chain = ChainOf(rec.TreeId);
            Assert.Equal(2, chain.Count);
            foreach (var half in chain)
            {
                Assert.True(half.Hidden, half.RecordingId);
                Assert.False(half.PlaybackEnabled, half.RecordingId);
                Assert.True(half.LoopPlayback, half.RecordingId);
                Assert.Equal(900, half.LoopIntervalSeconds);
                Assert.Equal(LoopTimeUnit.Min, half.LoopTimeUnit);
                Assert.Equal(77u, half.LoopAnchorVesselId);
                Assert.Equal("Kerbin", half.LoopAnchorBodyName);
                Assert.Equal(MergeState.CommittedProvisional, half.MergeState);
                Assert.Equal(new List<string> { "Fleet" }, half.RecordingGroups);
            }
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("player row settings kept on both halves"));
        }

        [Fact]
        public void LoopRange_RecordingIsNeitherSplitNorTrimmed()
        {
            var rec = Ascent("ranged");
            rec.LoopPlayback = true;
            rec.LoopStartUT = 150;
            rec.LoopEndUT = 500;
            CommitAlone(rec);
            double endBefore = rec.EndUT;

            CommittedListOptimizer.RunOptimizationPass();

            Assert.Single(ChainOf(rec.TreeId));
            Assert.Equal(endBefore, rec.EndUT);
            Assert.Equal(150, rec.LoopStartUT);
            Assert.Equal(500, rec.LoopEndUT);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("recording(s) whole: the player set a loop range"));
        }

        // SPLIT-LEAVES-END-FIELDS-ON-HEAD (review L6): the end situation, the dock / transfer
        // partner and the route windows not closed before the cut go to the second half.
        [Fact]
        public void Split_MovesEndOfFlightFieldsToTheSecondHalf()
        {
            var rec = Ascent("end-fields"); // cut at UT 300
            rec.VesselSituation = "Orbiting Kerbin";
            rec.DockTargetVesselPid = 55;
            rec.TransferTargetVesselPid = 66;
            rec.TransferKind = RouteConnectionKind.DockingPort;
            rec.RouteConnectionWindows = new List<RouteConnectionWindow>
            {
                new RouteConnectionWindow { WindowId = "early", DockUT = 120, UndockUT = 150 },
                new RouteConnectionWindow { WindowId = "late", DockUT = 500, UndockUT = 600 },
                new RouteConnectionWindow { WindowId = "open", DockUT = 650 },
            };
            CommitAlone(rec);

            CommittedListOptimizer.RunOptimizationPass();

            var chain = ChainOf(rec.TreeId);
            Assert.Equal(2, chain.Count);
            var head = chain[0];
            var tail = chain[1];
            Assert.Null(head.VesselSituation);
            Assert.Equal(0u, head.DockTargetVesselPid);
            Assert.Equal(0u, head.TransferTargetVesselPid);
            Assert.Equal(RouteConnectionKind.None, head.TransferKind);
            Assert.Equal("Orbiting Kerbin", tail.VesselSituation);
            Assert.Equal(55u, tail.DockTargetVesselPid);
            Assert.Equal(66u, tail.TransferTargetVesselPid);
            Assert.Equal(RouteConnectionKind.DockingPort, tail.TransferKind);
            Assert.Single(head.RouteConnectionWindows);
            Assert.Equal("early", head.RouteConnectionWindows[0].WindowId);
            Assert.Equal(2, tail.RouteConnectionWindows.Count);
            Assert.Contains(logLines, l => l.Contains("end-of-flight fields moved onto the second half")
                && l.Contains("windowsMoved=2 windowsKept=1"));
        }

        [Fact]
        public void TailTrim_SkipsLoopedRecording()
        {
            // A long boring orbital tail that the trim would otherwise cut.
            var rec = Make("looped-tail", 100, Exo(3000));
            rec.TerminalStateValue = TerminalState.Orbiting;
            rec.LoopPlayback = true;
            string category;
            bool trimmed = RecordingOptimizer.TrimBoringTailInternal(
                rec, new List<Recording> { rec }, RecordingOptimizer.DefaultTailBufferSeconds,
                logSkipReason: true, skipCategory: out category);

            Assert.False(trimmed);
            Assert.Equal("loop-settings", category);
            Assert.Contains(logLines, l => l.Contains("TrimBoringTail: skipped (loop-settings)")
                && l.Contains("id='looped-tail'"));
        }
    }
}
