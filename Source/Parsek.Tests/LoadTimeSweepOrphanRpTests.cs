using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class LoadTimeSweepOrphanRpTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public LoadTimeSweepOrphanRpTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RewindPointReaper.ResetTestOverrides();
            RewindPointReaper.DeleteQuicksaveForTesting = _ => true;
        }

        public void Dispose()
        {
            RewindPointReaper.ResetTestOverrides();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        [Fact]
        public void SweepMissingRewindPointQuicksaves_SealsSlotsAndReapsRp()
        {
            string missingPath = Path.Combine(
                Path.GetTempPath(),
                "ParsekTests",
                Guid.NewGuid().ToString("N"),
                "rp_missing.sfs");
            var rp = new RewindPoint
            {
                RewindPointId = "rp_missing",
                BranchPointId = "bp_missing",
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 0,
                        OriginChildRecordingId = "rec_missing",
                        Controllable = true,
                        Stashed = true,
                    }
                }
            };
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint> { rp },
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            RewindPointReaper.ResolveQuicksaveAbsolutePathForTesting = id =>
                string.Equals(id, "rp_missing", StringComparison.Ordinal)
                    ? missingPath
                    : null;

            int swept = LoadTimeSweep.SweepMissingRewindPointQuicksaves(scenario);

            Assert.Equal(1, swept);
            Assert.True(rp.ChildSlots[0].Sealed);
            Assert.True(rp.ChildSlots[0].Stashed);
            Assert.Empty(scenario.RewindPoints);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][LoadSweep]")
                && l.Contains("rp=rp_missing")
                && l.Contains("bp=bp_missing")
                && l.Contains("slots=1")
                && l.Contains(missingPath));
        }
    }
}
