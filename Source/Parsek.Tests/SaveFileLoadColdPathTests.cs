using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Behaviour cells for the save-file load's cold path (design-load-restores-the-save.md 4.1):
    /// the saveStamp canary, the cold branch's clock and the flight scene exit of a save-file
    /// load. The classification truth tables live in <see cref="LoadReconcilePolicyTests"/>; the
    /// OnLoad / OnSave wiring in <see cref="LoadReconcileWiringGateTests"/>.
    /// </summary>
    [Collection("Sequential")]
    public class SaveFileLoadColdPathTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string priorStamp;

        public SaveFileLoadColdPathTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            priorStamp = ParsekScenario.LastWrittenSaveStampForTesting;
            ParsekScenario.LastWrittenSaveStampForTesting = null;
        }

        public void Dispose()
        {
            ParsekScenario.LastWrittenSaveStampForTesting = priorStamp;
            ParsekLog.ResetTestOverrides();
        }

        // ---- the saveStamp canary ----

        [Fact]
        public void WriteSaveStamp_WritesAFreshStampAndRemembersIt()
        {
            var node = new ConfigNode("SCENARIO");
            string first = ParsekScenario.WriteSaveStamp(node);
            Assert.False(string.IsNullOrEmpty(first));
            Assert.Equal(first, node.GetValue(LoadReconcilePolicy.SaveStampKey));
            Assert.Equal(first, ParsekScenario.LastWrittenSaveStampForTesting);

            string second = ParsekScenario.WriteSaveStamp(node);
            Assert.NotEqual(first, second);
            Assert.Single(node.GetValues(LoadReconcilePolicy.SaveStampKey));
            Assert.Equal(second, node.GetValue(LoadReconcilePolicy.SaveStampKey));
            Assert.Equal(second, ParsekScenario.LastWrittenSaveStampForTesting);
        }

        [Fact]
        public void Canary_SilentWhenTheLoadReadsTheLastSaveWritten()
        {
            var node = new ConfigNode("SCENARIO");
            ParsekScenario.WriteSaveStamp(node);
            Assert.False(ParsekScenario.CheckLoadedSaveStamp(node, LoadKind.InSessionOther));
            Assert.DoesNotContain(logLines, l => l.Contains("[WARN]") && l.Contains("[LoadPolicy]"));
            Assert.Contains(logLines, l => l.Contains("[LoadPolicy]")
                && l.Contains("saveStamp check: refined=InSessionOther matches the last save written"));
        }

        [Fact]
        public void Canary_WarnsOnceOnAForeignStamp_AndDoesNotReclassify()
        {
            var older = new ConfigNode("SCENARIO");
            string olderStamp = ParsekScenario.WriteSaveStamp(older);
            string lastStamp = ParsekScenario.WriteSaveStamp(new ConfigNode("SCENARIO"));

            Assert.True(ParsekScenario.CheckLoadedSaveStamp(older, LoadKind.QuickloadFlight));
            Assert.Single(logLines.FindAll(l => l.Contains("[WARN][LoadPolicy]")
                && l.Contains("In-session load read a save this session did not just write: stamp=" + olderStamp
                    + " lastWritten=" + lastStamp + " refined=QuickloadFlight")));
            // The canary reads, it never writes: the last stamp is unchanged.
            Assert.Equal(lastStamp, ParsekScenario.LastWrittenSaveStampForTesting);
        }

        [Fact]
        public void Canary_QuietForAnAnnouncedInSessionLoad_OnceOnly()
        {
            var older = new ConfigNode("SCENARIO");
            ParsekScenario.WriteSaveStamp(older);
            ParsekScenario.WriteSaveStamp(new ConfigNode("SCENARIO"));

            ParsekScenario.AnnounceInSessionSaveLoad("harness-loadgame");
            Assert.False(ParsekScenario.CheckLoadedSaveStamp(older, LoadKind.InSessionOther));
            Assert.DoesNotContain(logLines, l => l.Contains("[WARN][LoadPolicy]"));
            Assert.Contains(logLines, l => l.Contains("read an older save on purpose (source=harness-loadgame)"));

            // One-shot: the next unannounced foreign read warns again.
            Assert.True(ParsekScenario.CheckLoadedSaveStamp(older, LoadKind.InSessionOther));
            Assert.Contains(logLines, l => l.Contains("[WARN][LoadPolicy]"));
        }

        [Fact]
        public void Canary_SkipsAStockRevertAndEveryNonInSessionKind()
        {
            var older = new ConfigNode("SCENARIO");
            ParsekScenario.WriteSaveStamp(older);
            ParsekScenario.WriteSaveStamp(new ConfigNode("SCENARIO"));

            foreach (LoadKind kind in new[]
                { LoadKind.Cold, LoadKind.PlainRewind, LoadKind.ReFlyStart, LoadKind.DiscardReFly, LoadKind.StockRevert })
            {
                Assert.False(ParsekScenario.CheckLoadedSaveStamp(older, kind), kind.ToString());
            }
            Assert.DoesNotContain(logLines, l => l.Contains("[WARN][LoadPolicy]"));
        }

        [Fact]
        public void Canary_SilentForASaveWithoutAStamp_OrBeforeAnySave()
        {
            // A save written by an older build carries no stamp.
            ParsekScenario.WriteSaveStamp(new ConfigNode("SCENARIO"));
            Assert.False(ParsekScenario.CheckLoadedSaveStamp(new ConfigNode("SCENARIO"), LoadKind.InSessionOther));

            // No OnSave yet in this process.
            ParsekScenario.LastWrittenSaveStampForTesting = null;
            var stamped = new ConfigNode("SCENARIO");
            stamped.AddValue(LoadReconcilePolicy.SaveStampKey, "abc");
            Assert.False(ParsekScenario.CheckLoadedSaveStamp(stamped, LoadKind.InSessionOther));
            Assert.DoesNotContain(logLines, l => l.Contains("[WARN][LoadPolicy]"));
        }

        // ---- the clock (design 4.1) ----

        [Theory]
        // A Space Center / Tracking Station OnLoad still reads the clock from before the load
        // (QL-1 measured 1000.10 against the quicksave's 409.9): the save's own UT wins.
        [InlineData(409.9, 1000.1, 409.9)]
        // In flight the two agree.
        [InlineData(2000.0, 2000.0, 2000.0)]
        // A save of a career that has not started its clock yet.
        [InlineData(0.0, 1000.1, 0.0)]
        // The boot load: the clock is not ready; the not-ready rules stand (keep every row,
        // full replay, the deferred seed walks once the clock is set).
        [InlineData(409.9, 0.0, 0.0)]
        [InlineData(double.NaN, 0.0, 0.0)]
        // No flight state readable: the Planetarium value, as before.
        [InlineData(double.NaN, 1000.1, 1000.1)]
        [InlineData(-5.0, 1000.1, 1000.1)]
        [InlineData(double.PositiveInfinity, 1000.1, 1000.1)]
        public void ColdLoadClock_IsTheLoadedSavesUT(double saveUT, double planetariumUT, double expected)
        {
            Assert.Equal(expected, ParsekScenario.ResolveColdLoadClockUT(saveUT, planetariumUT));
        }

        [Fact]
        public void ColdLoadClock_NotReadyPlanetariumNaN_StaysNaN()
        {
            Assert.True(double.IsNaN(ParsekScenario.ResolveColdLoadClockUT(409.9, double.NaN)));
        }

        // ---- the flight scene exit of a save-file load ----

        [Theory]
        [InlineData(GameScenes.SPACECENTER, true, false, true)]
        [InlineData(GameScenes.TRACKSTATION, true, false, true)]
        [InlineData(GameScenes.EDITOR, true, false, true)]
        // An F9 into a flight save already stashes (the FLIGHT branch).
        [InlineData(GameScenes.FLIGHT, true, false, false)]
        // A revert keeps its own scene-exit handling.
        [InlineData(GameScenes.EDITOR, true, true, false)]
        [InlineData(GameScenes.SPACECENTER, true, true, false)]
        // An ordinary scene change: commit as today.
        [InlineData(GameScenes.SPACECENTER, false, false, false)]
        [InlineData(GameScenes.TRACKSTATION, false, true, false)]
        public void SceneExit_SaveFileLoadOutOfFlight_StashesInsteadOfCommitting(
            GameScenes destination, bool intentArmed, bool revertPending, bool expected)
        {
            Assert.Equal(expected,
                ParsekFlight.ShouldStashLiveTreeForSaveFileLoad(destination, intentArmed, revertPending));
        }
    }
}
