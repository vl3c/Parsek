using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 14 of Rewind-to-Staging (design §7.28): guards the disk-usage
    /// snapshot helper that backs the Settings window's "Rewind point disk
    /// usage" line. Verifies byte-sum accuracy across multiple files,
    /// defensive handling of a missing directory, and the 10-second
    /// result cache.
    /// </summary>
    [Collection("Sequential")]
    public class DiskUsageDiagnosticsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private readonly string tempRoot;

        public DiskUsageDiagnosticsTests()
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
            RewindPointDiskUsage.ResetForTesting();

            // Per-test temp dir so fixtures do not stomp each other under the
            // shared [Collection("Sequential")] scheduler.
            tempRoot = Path.Combine(Path.GetTempPath(),
                "ParsekDiskUsageTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RewindPointDiskUsage.ResetForTesting();

            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup — nothing else depends on teardown here.
            }
        }

        private string WriteFile(string name, int byteLength)
        {
            string path = Path.Combine(tempRoot, name);
            File.WriteAllBytes(path, new byte[byteLength]);
            return path;
        }

        private static Recording Rec(
            string id,
            MergeState state,
            TerminalState? terminal,
            string parentBranchPointId,
            bool isDebris = false,
            string evaCrewName = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                MergeState = state,
                TerminalStateValue = terminal,
                ParentBranchPointId = parentBranchPointId,
                IsDebris = isDebris,
                EvaCrewName = evaCrewName
            };
        }

        private static ChildSlot Slot(
            int index,
            string originRecordingId,
            bool sealedSlot = false,
            bool stashedSlot = false)
        {
            return new ChildSlot
            {
                SlotIndex = index,
                OriginChildRecordingId = originRecordingId,
                Controllable = true,
                Sealed = sealedSlot,
                SealedRealTime = sealedSlot ? "2026-04-29T12:00:00.0000000Z" : null,
                Stashed = stashedSlot,
                StashedRealTime = stashedSlot ? "2026-04-29T12:01:00.0000000Z" : null
            };
        }

        private static RewindPoint Rp(
            string id,
            string branchPointId,
            int focusSlotIndex,
            params ChildSlot[] slots)
        {
            return new RewindPoint
            {
                RewindPointId = id,
                BranchPointId = branchPointId,
                UT = 0.0,
                QuicksaveFilename = id + ".sfs",
                SessionProvisional = false,
                FocusSlotIndex = focusSlotIndex,
                ChildSlots = new List<ChildSlot>(slots ?? Array.Empty<ChildSlot>())
            };
        }

        private static ParsekScenario InstallScenario(params RewindPoint[] rps)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(rps ?? Array.Empty<RewindPoint>()),
                ActiveReFlySessionMarker = null,
                ActiveMergeJournal = null
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        [Fact]
        public void DiskUsage_MultipleFiles_SumsBytes()
        {
            // Regression: the helper must sum every file in the directory,
            // not just the first; byte counts must match FileInfo.Length so
            // the UI string lines up with the actual disk footprint.
            WriteFile("rp_a.sfs", 100);
            WriteFile("rp_b.sfs", 250);
            WriteFile("rp_c.sfs", 650);

            var snap = RewindPointDiskUsage.Compute(tempRoot, nowSeconds: 0.0);

            Assert.Equal(1000L, snap.TotalBytes);
            Assert.Equal(3, snap.FileCount);
            Assert.Equal(tempRoot, snap.DirectoryPath);
        }

        [Fact]
        public void DiskUsage_NoDirectory_ReturnsZero()
        {
            // Regression: the helper must treat a missing directory (common
            // pre-game-load / no RP captured yet) as zero bytes, not throw.
            string bogus = Path.Combine(tempRoot, "does_not_exist");
            var snap = RewindPointDiskUsage.Compute(bogus, nowSeconds: 0.0);
            Assert.Equal(0L, snap.TotalBytes);
            Assert.Equal(0, snap.FileCount);
            Assert.Equal(bogus, snap.DirectoryPath);

            // Null path: same fallback, no throw.
            var snapNull = RewindPointDiskUsage.Compute(null, nowSeconds: 0.0);
            Assert.Equal(0L, snapNull.TotalBytes);
            Assert.Equal(0, snapNull.FileCount);
        }

        [Fact]
        public void DiskUsage_CachedFor10s()
        {
            // Regression: the cache TTL guards against per-frame disk thrash.
            // A file appearing between two GetSnapshot calls inside the 10s
            // window must NOT be reflected until the cache expires.
            double fakeNow = 1000.0;
            RewindPointDiskUsage.ClockSourceForTesting = () => fakeNow;

            WriteFile("rp_a.sfs", 100);
            var first = RewindPointDiskUsage.GetSnapshot(tempRoot);
            Assert.Equal(100L, first.TotalBytes);
            Assert.Equal(1, first.FileCount);

            // Add another file; advance the clock by less than 10s.
            WriteFile("rp_b.sfs", 200);
            fakeNow = 1005.0; // +5s, under the 10s TTL

            var cached = RewindPointDiskUsage.GetSnapshot(tempRoot);
            Assert.Equal(100L, cached.TotalBytes);
            Assert.Equal(1, cached.FileCount);

            // Push past the 10s TTL; now the new file must appear.
            fakeNow = 1010.5; // +10.5s
            var fresh = RewindPointDiskUsage.GetSnapshot(tempRoot);
            Assert.Equal(300L, fresh.TotalBytes);
            Assert.Equal(2, fresh.FileCount);
        }

        [Fact]
        public void DiskUsage_LiveBreakdownCountsCrashedStableAndSealedPendingRps()
        {
            // Regression: the diagnostics line needs to explain why RP files
            // still exist, not just show bytes. Buckets are per-RP and can
            // overlap when one RP has both sealed and still-open slots.
            var crash = Rec("rec_crash", MergeState.CommittedProvisional,
                TerminalState.Destroyed, "bp_crash");
            var focus = Rec("rec_focus", MergeState.Immutable,
                TerminalState.Landed, "bp_stable");
            var probe = Rec("rec_probe", MergeState.CommittedProvisional,
                TerminalState.Orbiting, "bp_stable");
            var sealedCrash = Rec("rec_sealed", MergeState.CommittedProvisional,
                TerminalState.Destroyed, "bp_sealed");

            RecordingStore.AddRecordingWithTreeForTesting(crash, "tree_crash");
            RecordingStore.AddRecordingWithTreeForTesting(focus, "tree_stable");
            RecordingStore.AddRecordingWithTreeForTesting(probe, "tree_stable");
            RecordingStore.AddRecordingWithTreeForTesting(sealedCrash, "tree_sealed");

            var scenario = InstallScenario(
                Rp("rp_crash", "bp_crash", 0, Slot(0, "rec_crash")),
                Rp("rp_stable", "bp_stable", 0,
                    Slot(0, "rec_focus"),
                    Slot(1, "rec_probe")),
                Rp("rp_sealed", "bp_sealed", 0,
                    Slot(0, "rec_sealed", sealedSlot: true)));

            var snap = RewindPointDiskUsage.Compute(tempRoot, nowSeconds: 0.0, scenario);

            Assert.Equal(3, snap.Live.RewindPointCount);
            Assert.Equal(1, snap.Live.CrashedOpenCount);
            Assert.Equal(1, snap.Live.StableOpenCount);
            Assert.Equal(1, snap.Live.SealedPendingCount);
        }

        [Fact]
        public void DiskUsage_FormatLineIncludesLiveBreakdown()
        {
            var snap = new RewindPointDiskUsage.Snapshot
            {
                TotalBytes = 1024L,
                FileCount = 3,
                Live = new RewindPointDiskUsage.LiveBreakdown
                {
                    RewindPointCount = 4,
                    CrashedOpenCount = 1,
                    StableOpenCount = 2,
                    SealedPendingCount = 1
                }
            };

            string line = RewindPointDiskUsage.FormatLine(snap);

            Assert.Contains("Rewind point disk usage: 1.0 KB", line);
            Assert.Contains("3 files", line);
            Assert.Contains("live=4", line);
            Assert.Contains("crashed=1", line);
            Assert.Contains("stable=2", line);
            Assert.Contains("sealed-pending=1", line);
        }

        [Fact]
        public void DiskUsage_CacheInvalidatesWhenScenarioStateChanges()
        {
            // Regression: the 10s disk cache must not freeze the live RP
            // breakdown after a Seal/Stash/merge path bumps scenario state.
            double fakeNow = 2000.0;
            RewindPointDiskUsage.ClockSourceForTesting = () => fakeNow;
            WriteFile("rp_a.sfs", 100);
            var crash = Rec("rec_crash", MergeState.CommittedProvisional,
                TerminalState.Destroyed, "bp_crash");
            RecordingStore.AddRecordingWithTreeForTesting(crash, "tree_crash");

            var scenario = InstallScenario();
            var first = RewindPointDiskUsage.GetSnapshot(tempRoot, scenario);
            Assert.Equal(0, first.Live.RewindPointCount);

            scenario.RewindPoints.Add(Rp("rp_crash", "bp_crash", 0, Slot(0, "rec_crash")));
            scenario.BumpSupersedeStateVersion();
            fakeNow = 2001.0;

            var changed = RewindPointDiskUsage.GetSnapshot(tempRoot, scenario);

            Assert.Equal(100L, changed.TotalBytes);
            Assert.Equal(1, changed.FileCount);
            Assert.Equal(1, changed.Live.RewindPointCount);
            Assert.Equal(1, changed.Live.CrashedOpenCount);
        }
    }
}
