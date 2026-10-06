using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// RECORD-COALESCED-ROW-COUNTS-ONE-HIT: a world-record row stands for every stock reward
    /// threshold the recorder folded into it, and the record-node patch rebuilds the node's
    /// band from the SUM of those counts. Measured on PWR-3 (2026-10-03_1335): one ascent paid
    /// five RecordsAltitude thresholds into one coalesced row plus a zero-reward completion
    /// row, the walk counted 2, the patch set record=2000 complete=False, and stock re-paid
    /// 7000 / 22000 / 70000 within the same second.
    /// </summary>
    [Collection("Sequential")]
    public class RecordThresholdCountTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RecordThresholdCountTests()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(true, true, true);
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateRecorder.PendingMilestoneEventById.Clear();
            GameStateRecorder.IsReplayingActions = false;
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            GameStateRecorder.PendingMilestoneEventById.Clear();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private class FakeProgressNode : ProgressNode
        {
            public FakeProgressNode(string id) : base(id, startReached: false) { }
        }

        private static List<GameStateEvent> MilestoneEvents(string key)
        {
            return GameStateStore.Events
                .Where(e => e.eventType == GameStateEventType.MilestoneAchieved && e.key == key)
                .ToList();
        }

        private static GameAction RecordRow(string id, double ut, string recordingId, int thresholds)
        {
            return new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = ut,
                RecordingId = recordingId,
                MilestoneId = id,
                MilestoneFundsAwarded = 4800f * thresholds,
                MilestoneRecordThresholds = thresholds
            };
        }

        private static int BandsToComplete(ProgressNode node)
        {
            for (int k = 1; k <= 64; k++)
            {
                Assert.True(KspStatePatcher.TryComputeRepeatableRecordState(node, k, out var state));
                if (state.Complete)
                    return k;
            }
            throw new InvalidOperationException("record node never completes");
        }

        private static void SetField<T>(object instance, string name, T value)
        {
            FieldInfo field = instance.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            field.SetValue(instance, value);
        }

        private static T GetField<T>(object instance, string name)
        {
            FieldInfo field = instance.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            return (T)field.GetValue(instance);
        }

        private static void SetFlags(ProgressNode node, bool reached, bool complete)
        {
            typeof(ProgressNode).GetField("reached", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(node, reached);
            typeof(ProgressNode).GetField("complete", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(node, complete);
        }

        // ================================================================
        // Recorder: the coalesced row counts its thresholds
        // ================================================================

        [Fact]
        public void Coalescer_CountsEveryFoldedThreshold_OnTheEventAndTheLedgerRow()
        {
            // Direct path (no live recorder): the seed event is forwarded to the ledger and
            // every later break folds into both the event and that row.
            LedgerOrchestrator.Initialize();
            var node = new FakeProgressNode("RecordsAltitude");
            GameStateRecorder.EmitStandaloneProgressReward(node, 4800.0, 0f, 0.0, ut: 44.5);
            GameStateRecorder.EmitStandaloneProgressReward(node, 4800.0, 1f, 0.0, ut: 60.0);
            GameStateRecorder.EmitStandaloneProgressReward(node, 4800.0, 1f, 1.0, ut: 90.0);

            var events = MilestoneEvents("RecordsAltitude");
            Assert.Single(events);
            Assert.Equal(3, GameStateEventConverter.ParseMilestoneRecordThresholds(events[0].detail));
            Assert.Contains("thresholds=3", events[0].detail);

            var rows = Ledger.Actions.Where(a =>
                a.Type == GameActionType.MilestoneAchievement && a.MilestoneId == "RecordsAltitude").ToList();
            Assert.Single(rows);
            Assert.Equal(3, rows[0].MilestoneRecordThresholds);
            Assert.Equal(14400f, rows[0].MilestoneFundsAwarded);
            // The line is rate-limited per scope; the first fold always prints.
            Assert.Contains(logLines, l =>
                l.Contains("[GameStateRecorder]") &&
                l.Contains("Coalesced world-record milestone 'RecordsAltitude'") &&
                l.Contains("thresholds=2"));
        }

        [Fact]
        public void Coalescer_DoesNotFoldIntoAnEarlierCommittedRowOfTheSameScope()
        {
            // PWR-3 2026-10-03_1335: after the in-flight commit the recording kept its id, so
            // a new award event (UT 480, not forwarded) shared its scope with the committed
            // rows (UT 44.5 / 212.2). The fold must update only the seed event's own row;
            // adding to an older row books the award twice once the new event commits.
            LedgerOrchestrator.Initialize();
            var committed = new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = 212.2,
                MilestoneId = "RecordsAltitude",
                MilestoneFundsAwarded = 24000f,
                MilestoneRecordThresholds = 5
            };
            Ledger.AddAction(committed);

            var pending = new GameStateEvent
            {
                ut = 480.0,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "RecordsAltitude",
                detail = GameStateRecorder.BuildMilestoneDetail(4800.0, 1f, 0.0)
            };
            GameStateStore.AddEvent(ref pending);

            GameStateRecorder.EmitStandaloneProgressReward(
                new FakeProgressNode("RecordsAltitude"), 4800.0, 1f, 1.0, ut: 480.0);

            Assert.Equal(24000f, committed.MilestoneFundsAwarded);
            Assert.Equal(5, committed.MilestoneRecordThresholds);
            var evt = MilestoneEvents("RecordsAltitude").Single();
            Assert.Contains("funds=9600", evt.detail);
            Assert.Equal(2, GameStateEventConverter.ParseMilestoneRecordThresholds(evt.detail));
        }

        [Fact]
        public void RecordCompletion_CarriesNoThreshold_AndIsNotLeftPendingForALaterAward()
        {
            // Stock completes a record node AFTER its award loop, so no AwardProgress follows
            // the completion. Leaving it pending let the next award (after a reset) overwrite
            // the old completion row's reward instead of being recorded on its own.
            GameStateRecorder.RegisterPendingMilestoneEvent("RecordsAltitude", ut: 212.2);

            var events = MilestoneEvents("RecordsAltitude");
            Assert.Single(events);
            Assert.Equal(0, GameStateEventConverter.ParseMilestoneRecordThresholds(events[0].detail));
            Assert.False(GameStateRecorder.PendingMilestoneEventById.ContainsKey("RecordsAltitude"));
            Assert.Contains(logLines, l =>
                l.Contains("[GameStateRecorder]") &&
                l.Contains("RecordsAltitude") &&
                l.Contains("world-record completion"));

            // A later award routes to the standalone path and folds in with its threshold.
            Parsek.Patches.ProgressRewardPatch.RoutePostfix(
                new FakeProgressNode("RecordsAltitude"), funds: 4800f, science: 0f, reputation: 1f, ut: 480.0);

            events = MilestoneEvents("RecordsAltitude");
            Assert.Single(events);
            Assert.Equal(1, GameStateEventConverter.ParseMilestoneRecordThresholds(events[0].detail));
            Assert.Contains("funds=4800", events[0].detail);
        }

        [Fact]
        public void OneShotCompletion_StillWaitsForItsAward()
        {
            GameStateRecorder.RegisterPendingMilestoneEvent("Mun/Landing", ut: 300.0);

            Assert.True(GameStateRecorder.PendingMilestoneEventById.ContainsKey("Mun/Landing"));
            var evt = MilestoneEvents("Mun/Landing").Single();
            Assert.DoesNotContain("thresholds", evt.detail);
        }

        // ================================================================
        // Detail format and converter
        // ================================================================

        [Fact]
        public void BuildMilestoneDetail_WritesTheThresholdKeyOnlyWhenItIsNotOne()
        {
            var de = new CultureInfo("de-DE");
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = de;
                Assert.Equal("funds=4800.5;rep=1;sci=0",
                    GameStateRecorder.BuildMilestoneDetail(4800.5, 1f, 0.0, recordThresholds: 1));
                Assert.Equal("funds=9600.5;rep=2;sci=1;thresholds=12",
                    GameStateRecorder.BuildMilestoneDetail(9600.5, 2f, 1.0, recordThresholds: 12));
                Assert.Equal("funds=0;rep=0;sci=0;thresholds=0",
                    GameStateRecorder.BuildMilestoneDetail(0.0, 0f, 0.0, recordThresholds: 0));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Theory]
        [InlineData("funds=4800;rep=1;sci=0", 1)]
        [InlineData("funds=4800;rep=1;sci=0;thresholds=5", 5)]
        [InlineData("funds=0;rep=0;sci=0;thresholds=0", 0)]
        [InlineData("funds=0;rep=0;sci=0;thresholds=-2", 1)]
        [InlineData("funds=0;rep=0;sci=0;thresholds=many", 1)]
        [InlineData("", 1)]
        [InlineData(null, 1)]
        public void Converter_ReadsTheThresholdCount_AbsentOrMalformedIsOne(string detail, int expected)
        {
            Assert.Equal(expected, GameStateEventConverter.ParseMilestoneRecordThresholds(detail));
            var action = GameStateEventConverter.ConvertMilestoneAchieved(new GameStateEvent
            {
                ut = 10.0,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "RecordsSpeed",
                detail = detail
            }, "rec");
            Assert.Equal(expected, action.MilestoneRecordThresholds);
        }

        // ================================================================
        // Ledger serialization
        // ================================================================

        private static ConfigNode Serialize(GameAction action)
        {
            var parent = new ConfigNode("ROOT");
            action.SerializeInto(parent);
            var node = parent.GetNode("GAME_ACTION");
            Assert.NotNull(node);
            return node;
        }

        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        [InlineData(5)]
        public void Serialization_RoundTripsANonDefaultThresholdCount(int thresholds)
        {
            var node = Serialize(RecordRow("RecordsAltitude", 44.5, "rec", thresholds));

            Assert.Equal(thresholds.ToString(CultureInfo.InvariantCulture),
                node.GetValue(GameAction.MilestoneRecordThresholdsKey));
            Assert.Equal(thresholds, GameAction.DeserializeFrom(node).MilestoneRecordThresholds);
        }

        [Fact]
        public void Serialization_OneThresholdWritesNoKey_AndALegacyRowReadsOne()
        {
            var node = Serialize(RecordRow("RecordsAltitude", 44.5, "rec", 1));
            Assert.Null(node.GetValue(GameAction.MilestoneRecordThresholdsKey));
            Assert.Equal(1, GameAction.DeserializeFrom(node).MilestoneRecordThresholds);

            var oneShot = Serialize(new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = 10.0,
                MilestoneId = "FirstLaunch",
                MilestoneFundsAwarded = 1000f
            });
            Assert.Null(oneShot.GetValue(GameAction.MilestoneRecordThresholdsKey));
            Assert.Equal(1, GameAction.DeserializeFrom(oneShot).MilestoneRecordThresholds);
        }

        [Fact]
        public void Serialization_MalformedThresholdCountReadsOneAndSaysSo()
        {
            var node = Serialize(RecordRow("RecordsAltitude", 44.5, "rec", 1));
            node.AddValue(GameAction.MilestoneRecordThresholdsKey, "-4");

            Assert.Equal(1, GameAction.DeserializeFrom(node).MilestoneRecordThresholds);
            Assert.Contains(logLines, l =>
                l.Contains(GameAction.MilestoneRecordThresholdsKey) && l.Contains("-4"));
        }

        // ================================================================
        // MilestonesModule: the effective count is the threshold sum
        // ================================================================

        [Fact]
        public void Module_SumsRepresentedThresholds_ForRecordIds()
        {
            var module = new MilestonesModule();
            module.Reset();
            module.ProcessAction(RecordRow("RecordsAltitude", 44.5, "recA", 5));
            module.ProcessAction(RecordRow("RecordsAltitude", 212.2, "recA", 0));
            module.ProcessAction(RecordRow("RecordsSpeed", 37.8, "recA", 4));

            Assert.Equal(5, module.GetEffectiveMilestoneCount("RecordsAltitude"));
            Assert.Equal(4, module.GetEffectiveMilestoneCount("RecordsSpeed"));
            Assert.True(module.IsMilestoneCredited("RecordsAltitude"));
        }

        [Fact]
        public void Module_LegacyRowsWithoutACountStillCountOneEach()
        {
            var module = new MilestonesModule();
            module.Reset();
            var legacyAward = new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = 44.5,
                MilestoneId = "RecordsAltitude",
                MilestoneFundsAwarded = 24000f
            };
            var legacyCompletion = new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = 212.2,
                MilestoneId = "RecordsAltitude"
            };
            module.ProcessAction(legacyAward);
            module.ProcessAction(legacyCompletion);

            Assert.Equal(2, module.GetEffectiveMilestoneCount("RecordsAltitude"));
        }

        [Fact]
        public void Module_OneShotIdsIgnoreTheField()
        {
            var module = new MilestonesModule();
            module.Reset();
            var first = new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = 10.0,
                MilestoneId = "FirstLaunch",
                MilestoneRecordThresholds = 7
            };
            module.ProcessAction(first);

            Assert.Equal(1, module.GetEffectiveMilestoneCount("FirstLaunch"));
        }

        [Fact]
        public void Module_SeededRecordCountAddsTheRowsThresholds()
        {
            var module = new MilestonesModule();
            module.SetPreLedgerProgressSeed(PreLedgerProgressSeed.CreateCaptured(
                null, new[] { new KeyValuePair<string, int>("RecordsDistance", 2) }));
            module.Reset();
            module.ProcessAction(RecordRow("RecordsDistance", 56.4, "recA", 3));

            Assert.Equal(5, module.GetEffectiveMilestoneCount("RecordsDistance"));
        }

        // ================================================================
        // End to end: one ascent keeps the band it paid
        // ================================================================

        [Fact]
        public void Ascent_PastEveryThreshold_PatchKeepsTheRecordComplete()
        {
            var stockNode = new KSPAchievements.RecordsAltitude();
            int bands = BandsToComplete(stockNode);
            Assert.True(bands >= 2, "the case needs a multi-threshold record");

            // One recording crosses every threshold (one coalesced event), then stock
            // completes the node (the completion event).
            var fake = new FakeProgressNode("RecordsAltitude");
            for (int i = 0; i < bands; i++)
                GameStateRecorder.EmitStandaloneProgressReward(fake, 4800.0, 1f, 0.0, ut: 44.5 + i * 30.0);
            GameStateRecorder.RegisterPendingMilestoneEvent("RecordsAltitude", ut: 212.2);

            var actions = MilestoneEvents("RecordsAltitude")
                .Select(e => GameStateEventConverter.ConvertMilestoneAchieved(e, "rec-ascent"))
                .ToList();
            Assert.Equal(2, actions.Count);

            var module = new MilestonesModule();
            RecalculationEngine.RegisterModule(module, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.Recalculate(actions);
            int effective = module.GetEffectiveMilestoneCount("RecordsAltitude");
            Assert.Equal(bands, effective);

            // The live node after the ascent: complete at the maximum, iterator released.
            Assert.True(KspStatePatcher.TryComputeRepeatableRecordState(stockNode, bands, out var max));
            SetFlags(stockNode, reached: true, complete: true);
            SetField(stockNode, "record", max.Record);
            SetField(stockNode, "rewardThreshold", 0.0);
            SetField(stockNode, "rewardInterval", 1);
            stockNode.OnIterateVessels = null;

            Assert.True(KspStatePatcher.PatchRepeatableRecordNode(
                stockNode, effective, qualifiedId: "RecordsAltitude"));

            Assert.True(stockNode.IsComplete);
            Assert.Equal(max.Record, GetField<double>(stockNode, "record"), 6);
            Assert.Null(stockNode.OnIterateVessels);

            // The row count alone (the old reading) would have reopened the node at a lower
            // band, which is what made stock pay the higher thresholds again.
            Assert.True(KspStatePatcher.TryComputeRepeatableRecordState(
                stockNode, actions.Count, max.Record, out var byRowCount));
            Assert.False(byRowCount.Complete);
        }

        [Fact]
        public void Ascent_ThatSplitsAcrossRecordings_SumsEveryScope()
        {
            var actions = new List<GameAction>
            {
                RecordRow("RecordsAltitude", 44.5, "recRoot", 2),
                RecordRow("RecordsAltitude", 130.0, "recUpper", 3),
                RecordRow("RecordsAltitude", 212.2, "recUpper", 0)
            };
            var module = new MilestonesModule();
            RecalculationEngine.RegisterModule(module, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.Recalculate(actions);

            Assert.Equal(5, module.GetEffectiveMilestoneCount("RecordsAltitude"));
        }

        // ================================================================
        // Pre-ledger progress seed: owned = represented thresholds
        // ================================================================

        private static PreLedgerProgressSeed.NodeObservation RecordObservation(string id, int impliedPaid)
        {
            return new PreLedgerProgressSeed.NodeObservation
            {
                QualifiedId = id,
                IsRepeatableRecord = true,
                ImpliedPaidCountKnown = true,
                ImpliedPaidCount = impliedPaid
            };
        }

        private static GameStateEvent RecordEvent(string id, string recordingId, string detail)
        {
            return new GameStateEvent
            {
                ut = 500.0,
                eventType = GameStateEventType.MilestoneAchieved,
                key = id,
                detail = detail,
                recordingId = recordingId ?? ""
            };
        }

        [Fact]
        public void Seed_SubtractsTheThresholdsLedgerRowsRepresent()
        {
            // A save whose one ascent the ledger already owns in full: five thresholds in the
            // coalesced row, none in the completion row. Counting rows would seed three
            // phantom pre-ledger thresholds on top.
            PreLedgerProgressSeed.CaptureStats stats;
            var seed = PreLedgerProgressSeed.Capture(
                new[] { RecordObservation("RecordsAltitude", 5) },
                new[]
                {
                    RecordRow("RecordsAltitude", 44.5, "recA", 5),
                    RecordRow("RecordsAltitude", 212.2, "recA", 0)
                },
                new GameStateEvent[0],
                out stats);

            Assert.Equal(0, seed.GetRecordPaidCount("RecordsAltitude"));
            Assert.Equal(1, stats.RecordsOwnedByLedger);
        }

        [Fact]
        public void Seed_SubtractsThePendingEventsThresholds()
        {
            // A flight still being recorded owns four thresholds (one coalesced event) and
            // the completion event; the fifth predates the ledger.
            PreLedgerProgressSeed.CaptureStats stats;
            var seed = PreLedgerProgressSeed.Capture(
                new[] { RecordObservation("RecordsAltitude", 5) },
                new GameAction[0],
                new[]
                {
                    RecordEvent("RecordsAltitude", "recB", "funds=19200;rep=2;sci=0;thresholds=4"),
                    RecordEvent("RecordsAltitude", "recB", "funds=0;rep=0;sci=0;thresholds=0")
                },
                out stats);

            Assert.Equal(1, seed.GetRecordPaidCount("RecordsAltitude"));
            Assert.Equal(1, stats.RecordThresholdsSeeded);
        }
    }
}
