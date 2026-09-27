using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// REWIND-STRIPS-RESUMED-COMMITTED-TIP: a plain rewind of a LATER tree must keep an
    /// EARLIER tree's spawned vessel when no recording that replays after the rewind will
    /// spawn it again (the replay-scope gate keeps the earlier tree historical, so a stripped
    /// vessel was lost for good). Evidence shape: EVA-6 runs 2026-09-26_2058 / _2106 (Kerbal X
    /// capsule pid 2708531065, chain [35..212][212..854][854..1276.6], spawn fields left on the
    /// chain head by the load-time optimizer split; Jeb's tree rewound to 1279.28, landing at
    /// 1264.28).
    /// </summary>
    [Collection("Sequential")]
    public class RewindHistorySpawnScopeTests : IDisposable
    {
        private const uint CapsulePid = 2708531065u;
        private const string CapsuleGuid = "5493223fe49b42b181998849a9a2aefa";
        private const uint JebPid = 1310464284u;
        private const string JebGuid = "d7c7968d91e6439a826a490a48666942";
        private const uint SeismometerPid = 1448769876u;
        private const uint DebrisPid = 3021808026u;
        private const uint AsteroidPid = 2815888106u;
        private const double RewindTargetUT = 1279.28;
        private const double AdjustedUT = RewindTargetUT - RecordingStore.RewindToLaunchLeadTimeSeconds;

        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public RewindHistorySpawnScopeTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            PlaybackScopeTracker.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            tempDir = Path.Combine(Path.GetTempPath(), "parsek_rhss_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            PlaybackScopeTracker.ResetForTesting();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        private static Recording Rec(
            string id, string name, string treeId, double startUT, double endUT,
            uint vesselPid, string guid = null, string chainId = null, int chainIndex = -1)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = name,
                TreeId = treeId,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                VesselPersistentId = vesselPid,
                RecordedVesselGuid = guid,
                ChainId = chainId,
                ChainIndex = chainIndex,
            };
        }

        private static RecordingTree Tree(string id, string name, params Recording[] recs)
        {
            var tree = new RecordingTree { Id = id, TreeName = name, RootRecordingId = recs[0].RecordingId };
            foreach (var r in recs)
                tree.AddOrReplaceRecording(r);
            return tree;
        }

        private static void Commit(RecordingTree tree)
        {
            RecordingStore.AddCommittedTreeForTesting(tree);
            foreach (var r in tree.Recordings.Values)
                RecordingStore.AddRecordingWithTreeForTesting(r);
        }

        /// <summary>The EVA-6 committed state at the moment the later tree is rewound.</summary>
        private (Recording head, Recording mid, Recording tip, Recording jeb, Recording seismo) BuildEva6Shape()
        {
            // Earlier tree: the Kerbal X chain after the load-time optimizer split. The spawn
            // fields stayed on the chain head; the tip carries the Landed terminal.
            var head = Rec("28b6e543", "Kerbal X", "treeA", 35, 212.2, CapsulePid, CapsuleGuid, "chainKX", 0);
            head.SpawnedVesselPersistentId = CapsulePid;
            head.VesselSpawned = true;
            var mid = Rec("7e905363", "Kerbal X", "treeA", 212.2, 853.7, CapsulePid, CapsuleGuid, "chainKX", 1);
            var tip = Rec("c108e0a2", "Kerbal X", "treeA", 853.7, 1276.64, CapsulePid, CapsuleGuid, "chainKX", 2);
            tip.TerminalStateValue = TerminalState.Landed;
            Commit(Tree("treeA", "Kerbal X", head, mid, tip));

            // Later tree: Jeb's EVA recording (the rewind owner) and the seismometer he placed,
            // both adoption-stamped by the in-flight commit.
            var jeb = Rec("5092f4cb", "Jebediah Kerman", "treeB", RewindTargetUT, 1281.9, JebPid, JebGuid);
            jeb.SpawnedVesselPersistentId = JebPid;
            jeb.VesselSpawned = true;
            var seismo = Rec("c554cba5", "Grand Slam Passive Seismometer", "treeB", 1280.26, 1282.16, SeismometerPid);
            seismo.SpawnedVesselPersistentId = SeismometerPid;
            seismo.VesselSpawned = true;
            Commit(Tree("treeB", "Jebediah Kerman", jeb, seismo));
            return (head, mid, tip, jeb, seismo);
        }

        private string WriteRewindQuicksave()
        {
            // The rewind save captured at Jeb's EVA start: capsule, Jeb, four debris, asteroid.
            string content =
                "FLIGHTSTATE\n{\n  UT = " + RewindTargetUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "\n" +
                "  VESSEL\n  {\n    name = #autoLOC_501232\n    persistentId = " + CapsulePid + "\n  }\n" +
                "  VESSEL\n  {\n    name = Jebediah Kerman\n    persistentId = " + JebPid + "\n  }\n" +
                "  VESSEL\n  {\n    name = Kerbal X Debris\n    persistentId = " + DebrisPid + "\n  }\n" +
                "  VESSEL\n  {\n    name = Ast. MII-526\n    persistentId = " + AsteroidPid + "\n  }\n}\n";
            string path = Path.Combine(tempDir, "parsek_rw_test.sfs");
            File.WriteAllText(path, content);
            return path;
        }

        // ---- The repro: the whole plain-rewind strip chain over the EVA-6 shape ----

        [Fact]
        public void Eva6Shape_RewindOfLaterTree_KeepsEarlierTreesSpawnedCapsule()
        {
            var shape = BuildEva6Shape();
            string sfs = WriteRewindQuicksave();

            // Pre-load: the production resolver scopes the PID strip at the save's adjusted UT.
            RecordingStore.PreProcessRewindSave(
                sfs, new HashSet<string> { "Jebediah Kerman" },
                RecordingStore.ResolveRewindStripSpawnedPids,
                RecordingStore.RewindToLaunchLeadTimeSeconds);

            ConfigNode root = ConfigNode.Load(sfs);
            var names = new List<string>();
            foreach (var v in root.GetNode("FLIGHTSTATE").GetNodes("VESSEL"))
                names.Add(v.GetValue("persistentId"));
            Assert.Contains(CapsulePid.ToString(), names);
            Assert.DoesNotContain(JebPid.ToString(), names);
            Assert.Contains(CapsulePid, RewindContext.RewindQuicksaveVesselPids);

            // OnLoad: the launch-identity strip sees the capsule as pre-existing in the rewind
            // quicksave (on main it was missing from the whitelist and got stripped as a
            // same-launch recording match, and nothing spawned it again).
            var all = RecordingStore.CollectAllCommittedRecordings();
            bool strip = ParsekScenario.ShouldStripVesselForRecordings(
                CapsulePid, CapsuleGuid, Vessel.Situations.LANDED, all,
                matchSource: true, skipPrelaunch: false,
                requireWhitelist: false, preExistingWhitelist: RewindContext.RewindQuicksaveVesselPids,
                out _, out string reason);
            Assert.False(strip);
            Assert.StartsWith("pre-existing", reason);
            Assert.False(ParsekScenario.ShouldStripFuturePrelaunch(
                Vessel.Situations.LANDED, CapsulePid, RewindContext.RewindQuicksaveVesselPids));

            // The later tree's own vessels are still stripped (they replay and re-spawn).
            Assert.True(ParsekScenario.ShouldStripVesselForRecordings(
                SeismometerPid, null, Vessel.Situations.LANDED, all,
                matchSource: true, skipPrelaunch: false,
                requireWhitelist: false, preExistingWhitelist: RewindContext.RewindQuicksaveVesselPids,
                out _, out _));

            // Playback reset keeps the capsule linked to its committed recording.
            RecordingStore.ResetAllPlaybackState(RewindContext.RewindHistoricalSpawnKeepRecordingIds);
            Assert.Equal(CapsulePid, shape.head.SpawnedVesselPersistentId);
            Assert.True(shape.head.VesselSpawned);
            Assert.Equal(0u, shape.jeb.SpawnedVesselPersistentId);
            Assert.False(shape.jeb.VesselSpawned);
            Assert.Equal(0u, shape.seismo.SpawnedVesselPersistentId);

            Assert.Contains(logLines, l => l.Contains("[Rewind]") && l.Contains("Rewind strip scope:")
                && l.Contains("keptHistoryPids=1"));
            Assert.Contains(logLines, l => l.Contains("[Rewind]") && l.Contains("keep spawned pid=" + CapsulePid));
            Assert.Contains(logLines, l => l.Contains("[Rewind]") && l.Contains("keptHistorySpawnState=1"));
        }

        [Fact]
        public void Eva6Shape_ScopeSplitsHistoryFromReplayingTree()
        {
            var shape = BuildEva6Shape();
            var scope = RecordingStore.ResolveRewindSpawnStripScope(
                RecordingStore.CollectAllCommittedRecordings(), AdjustedUT);

            Assert.Contains(CapsulePid, scope.KeptPids);
            Assert.DoesNotContain(CapsulePid, scope.StripPids);
            Assert.Contains(shape.head.RecordingId, scope.KeptHolderRecordingIds);
            Assert.Contains(JebPid, scope.StripPids);
            Assert.Contains(SeismometerPid, scope.StripPids);
            Assert.DoesNotContain(shape.jeb.RecordingId, scope.KeptHolderRecordingIds);
        }

        // ---- Mirror direction: every case where a replaying recording re-produces the vessel ----

        [Fact]
        public void HolderInReplayScope_Latched_StillStripped()
        {
            // A recording already replayed this session (latched in scope) re-spawns at the
            // Space Center once past its end: its vessel is stripped as before.
            var shape = BuildEva6Shape();
            PlaybackScopeTracker.NotePlayhead(shape.head.RecordingId, 10.0, 35.0);

            var scope = RecordingStore.ResolveRewindSpawnStripScope(
                RecordingStore.CollectAllCommittedRecordings(), AdjustedUT);

            Assert.Contains(CapsulePid, scope.StripPids);
            Assert.DoesNotContain(CapsulePid, scope.KeptPids);
            Assert.Empty(scope.KeptHolderRecordingIds);
        }

        [Fact]
        public void ChainTipReplays_HeadHoldsPid_Stripped()
        {
            // The rewind lands between the chain head and the tip: the tip replays and owns the
            // terminal spawn, so the vessel the (historical) head holds must be stripped.
            var shape = BuildEva6Shape();
            var scope = RecordingStore.ResolveRewindSpawnStripScope(
                RecordingStore.CollectAllCommittedRecordings(), 800.0);

            Assert.Contains(CapsulePid, scope.StripPids);
            Assert.True(RecordingStore.IsSpawnedVesselReproducedByRewindReplay(
                shape.head, RecordingStore.CollectAllCommittedRecordings(), 800.0, out string via));
            Assert.StartsWith("chain-member-replays:c108e0a2", via);
        }

        [Fact]
        public void SupersedingContinuationReplays_Stripped()
        {
            var holder = Rec("A1", "Rover", "treeA", 10, 100, 500u, "aaaa0000aaaa0000aaaa0000aaaa0000");
            holder.SpawnedVesselPersistentId = 777u;
            holder.TerminalSpawnSupersededByRecordingId = "C1";
            var continuation = Rec("C1", "Rover Mk2", "treeC", 1300, 1400, 999u);
            var all = new List<Recording> { holder, continuation };

            Assert.True(RecordingStore.IsSpawnedVesselReproducedByRewindReplay(
                holder, all, AdjustedUT, out string via));
            Assert.StartsWith("superseding-continuation-replays:C1", via);
        }

        [Fact]
        public void ReplayingRecordingFliesUniqueSpawnPid_Stripped()
        {
            // A genuine Parsek spawn carries a KSP-unique pid; a replaying recording that flies
            // that vessel re-produces it (pid-only match, collision-free).
            var holder = Rec("A1", "Lander", "treeA", 10, 100, 500u, "aaaa0000aaaa0000aaaa0000aaaa0000");
            holder.SpawnedVesselPersistentId = 777u;
            var flier = Rec("B1", "Lander", "treeB", 1300, 1400, 777u, "bbbb0000bbbb0000bbbb0000bbbb0000");
            var all = new List<Recording> { holder, flier };

            Assert.True(RecordingStore.IsSpawnedVesselReproducedByRewindReplay(
                holder, all, AdjustedUT, out string via));
            Assert.StartsWith("same-vessel-recording-replays:B1", via);
        }

        [Fact]
        public void AdoptedHolder_ReplayingRelaunchOfSameCraft_DifferentGuid_Kept()
        {
            // Craft-baked pid collision: a later relaunch of the same craft reuses the adopted
            // holder's pid but is a different launch (guids differ), so it does not re-produce
            // the earlier launch's vessel.
            var holder = Rec("A1", "Kerbal X", "treeA", 10, 100, 500u, "aaaa0000aaaa0000aaaa0000aaaa0000");
            holder.SpawnedVesselPersistentId = 500u;
            var relaunch = Rec("B1", "Kerbal X", "treeB", 1300, 1400, 500u, "bbbb0000bbbb0000bbbb0000bbbb0000");
            var all = new List<Recording> { holder, relaunch };

            Assert.False(RecordingStore.IsSpawnedVesselReproducedByRewindReplay(
                holder, all, AdjustedUT, out _));
            var scope = RecordingStore.ResolveRewindSpawnStripScope(all, AdjustedUT);
            Assert.Contains(500u, scope.KeptPids);
        }

        [Fact]
        public void AdoptedHolder_ReplayingSameLaunch_Stripped()
        {
            var holder = Rec("A1", "Kerbal X", "treeA", 10, 100, 500u, "aaaa0000aaaa0000aaaa0000aaaa0000");
            holder.SpawnedVesselPersistentId = 500u;
            var sameLaunch = Rec("B1", "Kerbal X", "treeB", 1300, 1400, 500u, "aaaa0000aaaa0000aaaa0000aaaa0000");
            var all = new List<Recording> { holder, sameLaunch };

            Assert.True(RecordingStore.IsSpawnedVesselReproducedByRewindReplay(
                holder, all, AdjustedUT, out _));
        }

        [Fact]
        public void SharedPid_OneHolderReplays_StrippedAndNotKept()
        {
            var historical = Rec("A1", "Probe", "treeA", 10, 100, 1u);
            historical.SpawnedVesselPersistentId = 4242u;
            var replaying = Rec("B1", "Probe", "treeB", 1300, 1400, 2u);
            replaying.SpawnedVesselPersistentId = 4242u;
            var scope = RecordingStore.ResolveRewindSpawnStripScope(
                new List<Recording> { historical, replaying }, AdjustedUT);

            Assert.Contains(4242u, scope.StripPids);
            Assert.Empty(scope.KeptPids);
            Assert.Empty(scope.KeptHolderRecordingIds);
        }

        [Fact]
        public void UnknownAdjustedUT_StripsEverything()
        {
            BuildEva6Shape();
            var scope = RecordingStore.ResolveRewindSpawnStripScope(
                RecordingStore.CollectAllCommittedRecordings(), double.NaN);

            Assert.Contains(CapsulePid, scope.StripPids);
            Assert.Empty(scope.KeptPids);
        }

        [Fact]
        public void PreProcess_MissingUT_ResolverGetsNaN_StripsEverySpawnedPid()
        {
            BuildEva6Shape();
            string path = Path.Combine(tempDir, "nout.sfs");
            File.WriteAllText(path,
                "FLIGHTSTATE\n{\n  VESSEL\n  {\n    name = #autoLOC_501232\n    persistentId = " + CapsulePid + "\n  }\n}\n");
            double seen = 0;
            RecordingStore.PreProcessRewindSave(path, new HashSet<string> { "Jebediah Kerman" },
                ut => { seen = ut; return RecordingStore.ResolveRewindStripSpawnedPids(ut); },
                RecordingStore.RewindToLaunchLeadTimeSeconds);

            Assert.True(double.IsNaN(seen));
            Assert.Empty(ConfigNode.Load(path).GetNode("FLIGHTSTATE").GetNodes("VESSEL"));
            Assert.Null(RewindContext.RewindHistoricalSpawnKeepRecordingIds);
        }

        [Fact]
        public void ResetAllPlaybackState_NoKeepSet_ResetsEverySpawnState()
        {
            var shape = BuildEva6Shape();
            RecordingStore.ResetAllPlaybackState();

            Assert.Equal(0u, shape.head.SpawnedVesselPersistentId);
            Assert.False(shape.head.VesselSpawned);
            Assert.Equal(0u, shape.jeb.SpawnedVesselPersistentId);
        }

        [Fact]
        public void ReconcileAfterStrip_KeptHolderWhoseVesselVanished_IsReset()
        {
            // The kept spawn state is still subject to the guid-aware post-strip reconcile.
            var shape = BuildEva6Shape();
            RecordingStore.ResetAllPlaybackState(new HashSet<string> { shape.head.RecordingId });
            Assert.Equal(CapsulePid, shape.head.SpawnedVesselPersistentId);

            int reset = ParsekScenario.ReconcileSpawnStateAfterStrip(
                new List<(uint, string)> { (DebrisPid, null) },
                new List<Recording> { shape.head });
            Assert.Equal(1, reset);
            Assert.Equal(0u, shape.head.SpawnedVesselPersistentId);

            shape.head.SpawnedVesselPersistentId = CapsulePid;
            shape.head.VesselSpawned = true;
            reset = ParsekScenario.ReconcileSpawnStateAfterStrip(
                new List<(uint, string)> { (CapsulePid, CapsuleGuid) },
                new List<Recording> { shape.head });
            Assert.Equal(0, reset);
            Assert.Equal(CapsulePid, shape.head.SpawnedVesselPersistentId);
        }
    }
}
