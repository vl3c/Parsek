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
        public void SweepMissingRewindPointQuicksaves_ConcludesSlotTipsAndReapsRp()
        {
            string missingPath = Path.Combine(
                Path.GetTempPath(),
                "ParsekTests",
                Guid.NewGuid().ToString("N"),
                "rp_missing.sfs");
            // An open (CommittedProvisional) slot tip; the missing-quicksave
            // sweep must conclude it (flip to Immutable) since it can never be
            // re-flown without its quicksave, then reap the RP.
            var tip = new Recording
            {
                RecordingId = "rec_missing",
                MergeState = MergeState.CommittedProvisional,
            };
            RecordingStore.AddCommittedInternal(tip);
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
            Assert.Equal(MergeState.Immutable, tip.MergeState);
            Assert.True(rp.ChildSlots[0].Stashed);
            Assert.Empty(scenario.RewindPoints);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][LoadSweep]")
                && l.Contains("Cleaned missing rewind-point quicksave")
                && l.Contains("rp=rp_missing")
                && l.Contains("bp=bp_missing")
                && l.Contains("slots=1")
                && l.Contains(missingPath));
        }

        [Fact]
        public void SweepMissingRewindPointQuicksaves_SkipsActiveMarkerRp()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "ParsekTests",
                Guid.NewGuid().ToString("N"));
            var activeTip = new Recording
            {
                RecordingId = "rec_active",
                MergeState = MergeState.CommittedProvisional,
            };
            RecordingStore.AddCommittedInternal(activeTip);
            var active = new RewindPoint
            {
                RewindPointId = "rp_active",
                BranchPointId = "bp_active",
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 0,
                        OriginChildRecordingId = "rec_active",
                        Controllable = true,
                        Stashed = true,
                    }
                }
            };
            var other = new RewindPoint
            {
                RewindPointId = "rp_other",
                BranchPointId = "bp_other",
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 0,
                        OriginChildRecordingId = "rec_other",
                        Controllable = true,
                        Stashed = true,
                    }
                }
            };
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint> { active, other },
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = "sess_active",
                    RewindPointId = "rp_active",
                },
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            RewindPointReaper.ResolveQuicksaveAbsolutePathForTesting = id =>
                Path.Combine(root, id + ".sfs");

            int swept = LoadTimeSweep.SweepMissingRewindPointQuicksaves(scenario);

            Assert.Equal(1, swept);
            Assert.Equal(MergeState.CommittedProvisional, activeTip.MergeState);
            Assert.Contains(active, scenario.RewindPoints);
            Assert.DoesNotContain(other, scenario.RewindPoints);
            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE][LoadSweep]")
                && l.Contains("skipped active marker")
                && l.Contains("rp=rp_active"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[INFO][LoadSweep]")
                && l.Contains("rp=rp_active"));
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][LoadSweep]")
                && l.Contains("Cleaned missing rewind-point quicksave")
                && l.Contains("rp=rp_other"));
        }

        [Fact]
        public void SweepMissingRewindPointQuicksaves_SkipsSessionProvisionalRpBeforeSealing()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "ParsekTests",
                Guid.NewGuid().ToString("N"));
            var provisionalTip = new Recording
            {
                RecordingId = "rec_provisional",
                MergeState = MergeState.CommittedProvisional,
            };
            RecordingStore.AddCommittedInternal(provisionalTip);
            var provisional = new RewindPoint
            {
                RewindPointId = "rp_provisional",
                BranchPointId = "bp_provisional",
                SessionProvisional = true,
                CreatingSessionId = "sess_pending",
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 0,
                        OriginChildRecordingId = "rec_provisional",
                        Controllable = true,
                        Stashed = true,
                    }
                }
            };
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint> { provisional },
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            RewindPointReaper.ResolveQuicksaveAbsolutePathForTesting = id =>
                Path.Combine(root, id + ".sfs");

            int swept = LoadTimeSweep.SweepMissingRewindPointQuicksaves(scenario);

            Assert.Equal(0, swept);
            Assert.Equal(MergeState.CommittedProvisional, provisionalTip.MergeState);
            Assert.Contains(provisional, scenario.RewindPoints);
            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE][LoadSweep]")
                && l.Contains("skipped session-provisional")
                && l.Contains("rp=rp_provisional")
                && l.Contains("sess=sess_pending"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[INFO][LoadSweep]")
                && l.Contains("rp=rp_provisional"));
        }

        [Fact]
        public void SweepMissingRewindPointQuicksaves_ReturnsOnlyCleanedMissingRps()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "ParsekTests",
                Guid.NewGuid().ToString("N"));
            string existingPath = Path.Combine(root, "rp_unrelated.sfs");
            Directory.CreateDirectory(root);
            File.WriteAllText(existingPath, "present");
            var missing = new RewindPoint
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
            var unrelatedEligible = new RewindPoint
            {
                RewindPointId = "rp_unrelated",
                BranchPointId = "bp_unrelated",
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>(),
            };
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint> { missing, unrelatedEligible },
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            RewindPointReaper.ResolveQuicksaveAbsolutePathForTesting = id =>
                Path.Combine(root, id + ".sfs");

            int swept = LoadTimeSweep.SweepMissingRewindPointQuicksaves(scenario);

            Assert.Equal(1, swept);
            Assert.Empty(scenario.RewindPoints);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][LoadSweep]")
                && l.Contains("rp=rp_missing"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[INFO][LoadSweep]")
                && l.Contains("rp=rp_unrelated"));
        }
    }
}
