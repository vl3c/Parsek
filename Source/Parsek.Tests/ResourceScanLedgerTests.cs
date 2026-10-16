using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// RESOURCE-SCAN-UNLOCKS-NOT-LEDGERED: a resource scan is captured as a
    /// <see cref="GameStateEventType.ResourceScanCompleted"/> event, converted to a
    /// <see cref="GameActionType.ResourceScanUnlocked"/> ledger row, and re-applied add-only
    /// by <see cref="KspStatePatcher.PatchResourceScans"/> once the clock reaches it. The
    /// Harmony capture itself needs a live <c>ResourceMap</c>; these cells pin every pure
    /// seam it feeds and the patch decisions it drives.
    /// </summary>
    [Collection("Sequential")]
    public class ResourceScanLedgerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ResourceScanLedgerTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateRecorder.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static GameAction Scan(double ut, string body, string biome = null, string recordingId = null)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ResourceScanUnlocked,
                ScanBodyName = body,
                ScanBiomeName = biome,
                RecordingId = recordingId
            };
        }

        // ================================================================
        // Event and conversion
        // ================================================================

        [Fact]
        public void BuildResourceScanEvent_WholeBody_HasEmptyDetail()
        {
            var evt = GameStateRecorder.BuildResourceScanEvent(1234.5, "Minmus", null);

            Assert.Equal(GameStateEventType.ResourceScanCompleted, evt.eventType);
            Assert.Equal(23, (int)evt.eventType);
            Assert.Equal(1234.5, evt.ut);
            Assert.Equal("Minmus", evt.key);
            Assert.Equal("", evt.detail);
            Assert.Equal("Resource scan: Minmus", GameStateEventDisplay.GetDisplayDescription(evt));
        }

        [Fact]
        public void BuildResourceScanEvent_Biome_CarriesBiomeDetail()
        {
            var evt = GameStateRecorder.BuildResourceScanEvent(10, "Mun", "Midlands");

            Assert.Equal("biome=Midlands", evt.detail);
            Assert.Equal("Resource scan: Mun (Midlands)", GameStateEventDisplay.GetDisplayDescription(evt));
            Assert.Equal("Scan", GameStateEventDisplay.GetDisplayCategory(evt.eventType));
        }

        [Fact]
        public void ConvertEvent_ResourceScan_MapsBodyAndBiome()
        {
            var biome = GameStateEventConverter.ConvertEvent(
                GameStateRecorder.BuildResourceScanEvent(500, "Mun", "Midlands"), "rec-1");
            Assert.NotNull(biome);
            Assert.Equal(GameActionType.ResourceScanUnlocked, biome.Type);
            Assert.Equal(500, biome.UT);
            Assert.Equal("rec-1", biome.RecordingId);
            Assert.Equal("Mun", biome.ScanBodyName);
            Assert.Equal("Midlands", biome.ScanBiomeName);

            var body = GameStateEventConverter.ConvertEvent(
                GameStateRecorder.BuildResourceScanEvent(600, "Minmus", null), null);
            Assert.NotNull(body);
            Assert.Equal("Minmus", body.ScanBodyName);
            Assert.Null(body.ScanBiomeName);
        }

        [Fact]
        public void ConvertEvent_ResourceScan_NamelessEvent_IsDroppedWithWarn()
        {
            var evt = new GameStateEvent { ut = 1, eventType = GameStateEventType.ResourceScanCompleted };

            Assert.Null(GameStateEventConverter.ConvertEvent(evt, null));
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("ResourceScanCompleted event has no body name"));
        }

        [Fact]
        public void OnResourceScanUnlocked_DuringReplay_EmitsNothing()
        {
            GameStateRecorder.IsReplayingActions = true;
            try
            {
                GameStateRecorder.OnResourceScanUnlocked(100, "Minmus", null);
            }
            finally
            {
                GameStateRecorder.IsReplayingActions = false;
            }

            Assert.DoesNotContain(GameStateStore.Events, e => e.eventType == GameStateEventType.ResourceScanCompleted);
            Assert.Contains(logLines, l => l.Contains("OnResourceScanUnlocked: suppressed during action replay"));
        }

        [Fact]
        public void OnKscSpending_ResourceScanEvent_LandsLedgerRow()
        {
            LedgerOrchestrator.Initialize();
            var evt = GameStateRecorder.BuildResourceScanEvent(700, "Minmus", null);
            GameStateStore.AddEvent(ref evt);

            LedgerOrchestrator.OnKscSpending(evt);

            var row = Ledger.Actions.SingleOrDefault(a => a.Type == GameActionType.ResourceScanUnlocked);
            Assert.NotNull(row);
            Assert.Equal("Minmus", row.ScanBodyName);
            Assert.Equal(700, row.UT);
        }

        [Fact]
        public void GetActionKey_TwoBodiesAtOneUt_AreDistinct()
        {
            string mun = LedgerOrchestrator.GetActionKey(Scan(100, "Mun"));
            string minmus = LedgerOrchestrator.GetActionKey(Scan(100, "Minmus"));
            string munBiome = LedgerOrchestrator.GetActionKey(Scan(100, "Mun", "Midlands"));

            Assert.NotEqual(mun, minmus);
            Assert.NotEqual(mun, munBiome);
            Assert.Equal(mun, LedgerOrchestrator.GetActionKey(Scan(100.05, "Mun")));
        }

        // ================================================================
        // Persistence
        // ================================================================

        [Fact]
        public void ResourceScanUnlocked_RoundTrip_BothShapes()
        {
            foreach (var original in new[] { Scan(42.5, "Mun", "Midlands", "rec-a"), Scan(99, "Minmus") })
            {
                var parent = new ConfigNode("ROOT");
                original.SerializeInto(parent);
                var node = parent.GetNode("GAME_ACTION");
                Assert.Equal("37", node.GetValue("type"));
                var result = GameAction.DeserializeFrom(node);

                Assert.Equal(GameActionType.ResourceScanUnlocked, result.Type);
                Assert.Equal(original.UT, result.UT);
                Assert.Equal(original.RecordingId, result.RecordingId);
                Assert.Equal(original.ScanBodyName, result.ScanBodyName);
                Assert.Equal(original.ScanBiomeName, result.ScanBiomeName);
            }
        }

        [Fact]
        public void ResourceScanUnlocked_WholeBody_WritesNoBiomeKey()
        {
            var parent = new ConfigNode("ROOT");
            Scan(5, "Minmus").SerializeInto(parent);
            var node = parent.GetNode("GAME_ACTION");

            Assert.Equal("Minmus", node.GetValue("scanBody"));
            Assert.Null(node.GetValue("scanBiome"));
        }

        // ================================================================
        // Ledger semantics
        // ================================================================

        [Fact]
        public void ResourceScanUnlocked_IsNonEconomic_RetiredBySupersede_NeverBlocks()
        {
            Assert.False(LedgerOrchestrator.IsResourceImpactingAction(GameActionType.ResourceScanUnlocked));
            Assert.True(TombstoneEligibility.IsSupersedeTombstoneEligible(Scan(10, "Mun", null, "rec-x")));
            Assert.False(TombstoneEligibility.IsSupersedeTombstoneEligible(Scan(10, "Mun")));
            Assert.False(SupersedeCommit.IsWorldStateChangingRecordingAction(
                Scan(10, "Mun", null, "rec-x"), new List<GameAction>()));
        }

        [Fact]
        public void ResourceScanUnlocked_TimelineText_NoUnknownTypeWarn()
        {
            Assert.Equal("Resource scan: Mun (Midlands)",
                GameActionDisplay.GetDescription(Scan(1, "Mun", "Midlands"), null));
            Assert.Equal("Resource scan: Minmus", GameActionDisplay.GetDescription(Scan(1, "Minmus"), null));
            Assert.Equal(TimelineEntryType.MilestoneAchievement,
                TimelineEntryDisplay.MapGameActionType(GameActionType.ResourceScanUnlocked));
            Assert.DoesNotContain(logLines, l => l.Contains("Unknown GameActionType"));
        }

        // ================================================================
        // Patch planning (pure)
        // ================================================================

        [Fact]
        public void BuildResourceScanUnlocksForPatch_FiltersCutoffDedupsAndSorts()
        {
            var actions = new List<GameAction>
            {
                Scan(300, "Mun"),
                Scan(100, "Minmus"),
                Scan(150, "Minmus"),              // duplicate of the row above
                Scan(200, "Mun", "Midlands"),
                Scan(900, "Duna"),                // after the cutoff
                Scan(50, null),                   // no body
                new GameAction { UT = 10, Type = GameActionType.MilestoneAchievement, MilestoneId = "Mun" }
            };

            var unlocks = KspStatePatcher.BuildResourceScanUnlocksForPatch(actions, 500);

            Assert.Equal(3, unlocks.Count);
            Assert.Equal("Minmus", unlocks[0].Body);
            Assert.Null(unlocks[0].Biome);
            Assert.Equal("Mun", unlocks[1].Body);
            Assert.Null(unlocks[1].Biome);
            Assert.Equal("Mun", unlocks[2].Body);
            Assert.Equal("Midlands", unlocks[2].Biome);
        }

        [Fact]
        public void PlanResourceScanUnlocksForPatch_ClampsToLiveClock()
        {
            // A committed FUTURE scan (after a rewind) stays out until the clock reaches it,
            // even on a cutoff-less walk; the add-only patch could never take it back.
            var actions = new List<GameAction> { Scan(100, "Minmus"), Scan(5000, "Mun") };

            double? cutoff;
            var early = KspStatePatcher.PlanResourceScanUnlocksForPatch(actions, null, null, 1000, out cutoff);
            Assert.Equal(1000, cutoff);
            Assert.Single(early);
            Assert.Equal("Minmus", early[0].Body);

            var later = KspStatePatcher.PlanResourceScanUnlocksForPatch(actions, null, null, 6000, out cutoff);
            Assert.Equal(2, later.Count);

            var rewound = KspStatePatcher.PlanResourceScanUnlocksForPatch(actions, 50.0, null, 6000, out cutoff);
            Assert.Equal(50, cutoff);
            Assert.Empty(rewound);
        }

        [Fact]
        public void PlanResourceScanUnlocksForPatch_ClockNotReadyAndNoCutoff_IsNull()
        {
            double? cutoff;
            var plan = KspStatePatcher.PlanResourceScanUnlocksForPatch(
                new List<GameAction> { Scan(100, "Minmus") }, null, null, 0, out cutoff);

            Assert.Null(plan);
            Assert.Null(cutoff);
        }

        // ================================================================
        // Patch apply (stock seams faked)
        // ================================================================

        [Fact]
        public void ApplyResourceScanUnlocks_UnlocksOnlyWhatIsLocked()
        {
            var ids = new Dictionary<string, int> { { "Mun", 2 }, { "Minmus", 3 } };
            var scanned = new HashSet<int> { 3 };
            var biomes = new HashSet<string>();
            var planetCalls = new List<int>();
            var biomeCalls = new List<string>();

            var unlocks = new List<KspStatePatcher.ResourceScanUnlock>
            {
                new KspStatePatcher.ResourceScanUnlock { Body = "Minmus" },                   // already scanned
                new KspStatePatcher.ResourceScanUnlock { Body = "Mun" },
                new KspStatePatcher.ResourceScanUnlock { Body = "Mun", Biome = "Midlands" },
                new KspStatePatcher.ResourceScanUnlock { Body = "Kerbol Prime" }              // not on this install
            };

            var result = KspStatePatcher.ApplyResourceScanUnlocks(unlocks,
                name => ids.TryGetValue(name, out int id) ? id : -1,
                id => scanned.Contains(id),
                (id, biome) => biomes.Contains(id + "/" + biome),
                id => { planetCalls.Add(id); scanned.Add(id); },
                (id, biome) => { biomeCalls.Add(id + "/" + biome); biomes.Add(id + "/" + biome); });

            Assert.Equal(2, result.Unlocked);
            Assert.Equal(1, result.AlreadyUnlocked);
            Assert.Equal(1, result.UnknownBody);
            Assert.Equal(new[] { 2 }, planetCalls);
            Assert.Equal(new[] { "2/Midlands" }, biomeCalls);
            Assert.Equal(new[] { "Mun", "Mun/Midlands" }, result.UnlockedIds);
            Assert.Equal(new[] { "Kerbol Prime" }, result.UnknownBodies);

            // A second pass over the same rows is a no-op: the patch never re-locks or repeats.
            var again = KspStatePatcher.ApplyResourceScanUnlocks(unlocks,
                name => ids.TryGetValue(name, out int id) ? id : -1,
                id => scanned.Contains(id),
                (id, biome) => biomes.Contains(id + "/" + biome),
                id => planetCalls.Add(id),
                (id, biome) => biomeCalls.Add(id + "/" + biome));
            Assert.Equal(0, again.Unlocked);
            Assert.Equal(3, again.AlreadyUnlocked);
            Assert.Single(planetCalls);
        }

        [Fact]
        public void PatchResourceScans_UnityCallsSuppressed_SkipsAfterPlanning()
        {
            KspStatePatcher.ResourceScanLiveUtProviderForTesting = () => 1000;

            KspStatePatcher.PatchResourceScans(new List<GameAction> { Scan(100, "Minmus") }, null, null);

            Assert.Contains(logLines, l => l.Contains("PatchResourceScans: Unity calls suppressed for testing"));
        }

        [Fact]
        public void PatchResourceScans_NoRowsBeforeCutoff_LogsNoop()
        {
            KspStatePatcher.ResourceScanLiveUtProviderForTesting = () => 1000;

            KspStatePatcher.PatchResourceScans(new List<GameAction> { Scan(5000, "Minmus") }, null, null);

            Assert.Contains(logLines, l => l.Contains("PatchResourceScans: no committed resource scan at or before the cutoff"));
        }
    }
}
