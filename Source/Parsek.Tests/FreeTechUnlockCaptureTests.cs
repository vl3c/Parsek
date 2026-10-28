using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// FREE-TECH-NODE-LOST-AFTER-REWIND: stock <c>ResearchAndDevelopment.UnlockProtoTechNode</c>
    /// (a point-of-interest tech gift, the debug menu) unlocks a node for free and fires no
    /// OnTechnologyResearched, so the node had no ledger row and the first tech patch whose
    /// baseline lacked it (every one after a Rewind-to-Launch pruned the commit baseline)
    /// re-locked it for good. These cells cover the patch's pure capture decision, the
    /// recorder's capture core (KSC forward, flight tag, replay suppression), the converter
    /// round trip, the walk (a free row charges nothing; a repeat is a no-op, not a refund)
    /// and the headline regression through the real capture door.
    /// </summary>
    [Collection("Sequential")]
    public class FreeTechUnlockCaptureTests : IDisposable
    {
        private const string GiftTech = "advRocketry";
        private const string StartTech = "start";
        private readonly List<string> logLines = new List<string>();

        public FreeTechUnlockCaptureTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(true, true, true);
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            FreeTechUnlockCapturePatch.ResetForTesting();
            RecalculationEngine.ClearModules();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            FreeTechUnlockCapturePatch.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            KspStatePatcher.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static List<GameStateEvent> TechEvents()
        {
            return GameStateStore.Events.Where(e => e.eventType == GameStateEventType.TechResearched).ToList();
        }

        private static List<GameAction> TechRows()
        {
            return Ledger.Actions.Where(a => a.Type == GameActionType.ScienceSpending).ToList();
        }

        // ================================================================
        // The patch's capture decision
        // ================================================================

        // catches: a node the career already held being filed again (CheatTechnology runs over
        // every tree node), and Parsek's own replay being captured as a player unlock.
        [Theory]
        [InlineData(GiftTech, false, true, false, null)]
        [InlineData(GiftTech, true, true, false, FreeTechUnlockCapturePatch.SkipAlreadyAvailable)]
        [InlineData(GiftTech, false, false, false, FreeTechUnlockCapturePatch.SkipNotAvailableAfter)]
        [InlineData(GiftTech, false, true, true, FreeTechUnlockCapturePatch.SkipReplaying)]
        [InlineData(null, false, true, false, FreeTechUnlockCapturePatch.SkipNoTechId)]
        [InlineData("", false, true, false, FreeTechUnlockCapturePatch.SkipNoTechId)]
        public void DecideCapture_OnlyANewlyAvailableNodeOutsideReplay(
            string techId, bool wasAvailable, bool nowAvailable, bool replaying, string expected)
        {
            Assert.Equal(expected,
                FreeTechUnlockCapturePatch.DecideCapture(techId, wasAvailable, nowAvailable, replaying));
        }

        [Fact]
        public void Report_NoSubscriber_LogsAndDrops_WithSubscriber_Delivers()
        {
            FreeTechUnlockCapturePatch.Report(new FreeTechUnlock { TechId = GiftTech });
            Assert.Contains(logLines, l => l.Contains("[FreeTechUnlockCapture]")
                && l.Contains("no subscribed recorder") && l.Contains(GiftTech));

            FreeTechUnlock seen = default(FreeTechUnlock);
            FreeTechUnlockCapturePatch.FreeUnlockCaptured += u => seen = u;
            FreeTechUnlockCapturePatch.Report(new FreeTechUnlock { TechId = GiftTech, NodeCost = 45f });
            Assert.Equal(GiftTech, seen.TechId);
            Assert.Equal(45f, seen.NodeCost);
        }

        // ================================================================
        // Detail + converter + display
        // ================================================================

        // catches: a culture-dependent nodeCost (de-DE "45,5") breaking the ';'-separated detail.
        [Fact]
        public void Detail_IsInvariant_AndCarriesTheFreeFlag()
        {
            var saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string detail = GameStateRecorder.BuildFreeTechUnlockDetail(45.5f, new[] { "a", "b" });
                Assert.Equal("cost=0;free=1;nodeCost=45.5;parts=a,b", detail);
                Assert.Equal("cost=0;free=1;nodeCost=0;parts=", GameStateRecorder.BuildFreeTechUnlockDetail(0f, null));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        // catches: the converter reading nodeCost (the node's price) as the charged cost, which
        // would bill a gift.
        [Fact]
        public void Converter_FreeEvent_IsAZeroCostScienceSpendingRow()
        {
            var evt = new GameStateEvent
            {
                ut = 5000.0,
                eventType = GameStateEventType.TechResearched,
                key = GiftTech,
                detail = GameStateRecorder.BuildFreeTechUnlockDetail(45f, new[] { "liquidEngine2" }),
            };
            var row = GameStateEventConverter.ConvertEvent(evt, "recA");
            Assert.NotNull(row);
            Assert.Equal(GameActionType.ScienceSpending, row.Type);
            Assert.Equal(GiftTech, row.NodeId);
            Assert.Equal(0f, row.Cost);
            Assert.Equal(5000.0, row.UT);
            Assert.Equal("recA", row.RecordingId);
        }

        [Fact]
        public void Display_FreeEvent_ReadsFree()
        {
            var evt = new GameStateEvent
            {
                eventType = GameStateEventType.TechResearched,
                key = GiftTech,
                detail = GameStateRecorder.BuildFreeTechUnlockDetail(45f, null),
            };
            Assert.Equal("\"" + GiftTech + "\" (free)", GameStateEventDisplay.GetDisplayDescription(evt));
        }

        // ================================================================
        // Capture core
        // ================================================================

        // catches: the defect's capture half - a free unlock at KSC reaching no ledger row.
        [Fact]
        public void Capture_Untagged_FilesOneEventAndOneZeroCostRow()
        {
            new GameStateRecorder().CaptureFreeTechUnlock(GiftTech, 45f, new[] { "liquidEngine2" }, 5000.0, 12.0);

            var events = TechEvents();
            Assert.Single(events);
            Assert.Equal(GiftTech, events[0].key);
            Assert.Equal("1", GameStateEventDisplay.ExtractDetailField(events[0].detail, "free"));
            Assert.Equal(12.0, events[0].valueBefore);
            Assert.Equal(12.0, events[0].valueAfter);

            var rows = TechRows();
            Assert.Single(rows);
            Assert.Equal(GiftTech, rows[0].NodeId);
            Assert.Equal(0f, rows[0].Cost);
            Assert.True(string.IsNullOrEmpty(rows[0].RecordingId));
            Assert.Contains(logLines, l => l.Contains("[GameStateRecorder]")
                && l.Contains("TechResearched '" + GiftTech + "' free unlock")
                && l.Contains("nodeCost=45") && l.Contains("ut=5000"));
        }

        // catches: an in-flight gift being written to the ledger at once instead of riding the
        // live recording to its commit (a discarded flight must not keep its gift).
        [Fact]
        public void Capture_TaggedToLiveRecording_StaysOffTheLedger()
        {
            GameStateRecorder.TagResolverForTesting = () => "recA";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;

            new GameStateRecorder().CaptureFreeTechUnlock(GiftTech, 45f, null, 5000.0, 0.0);

            var events = TechEvents();
            Assert.Single(events);
            Assert.Equal("recA", events[0].recordingId);
            Assert.Empty(TechRows());
        }

        [Fact]
        public void Capture_DuringReplay_IsSuppressed()
        {
            GameStateRecorder.IsReplayingActions = true;
            new GameStateRecorder().CaptureFreeTechUnlock(GiftTech, 45f, null, 5000.0, 0.0);

            Assert.Empty(TechEvents());
            Assert.Empty(TechRows());
            Assert.Contains(logLines, l => l.Contains("Suppressed free TechResearched event during action replay"));
        }

        [Fact]
        public void Capture_NoTechId_IsRejected()
        {
            new GameStateRecorder().CaptureFreeTechUnlock(null, 45f, null, 5000.0, 0.0);
            Assert.Empty(TechEvents());
            Assert.Contains(logLines, l => l.Contains("free-unlock-tech-id-missing"));
        }

        // ================================================================
        // The walk
        // ================================================================

        private static ScienceModule RegisterScience()
        {
            var science = new ScienceModule();
            RecalculationEngine.RegisterModule(science, RecalculationEngine.ModuleTier.SecondTier);
            return science;
        }

        private static GameAction TechRow(string actionId, double ut, long cseq, string nodeId, float cost)
        {
            return new GameAction
            {
                ActionId = actionId, UT = ut, CaptureSequence = cseq,
                Type = GameActionType.ScienceSpending, NodeId = nodeId, Cost = cost
            };
        }

        // catches: a free row being refused or charged, which would leave it out of the patch
        // target (Affordable is the "researched by the walk" contract) or bill a gift.
        [Fact]
        public void Walk_FreeRow_IsResearchedAndChargesNothing()
        {
            var science = RegisterScience();
            var actions = new List<GameAction>
            {
                new GameAction { UT = 0, Type = GameActionType.ScienceInitial, InitialScience = 10f },
                TechRow("free", 5000.0, 10, GiftTech, 0f),
            };

            RecalculationEngine.Recalculate(actions);

            Assert.True(actions[1].Affordable);
            Assert.True(actions[1].Effective);
            Assert.Equal(10.0, science.GetRunningScience(), 3);
        }

        // catches: a paid research of the same node earlier on the new timeline (captured
        // after the gift) being refunded or double-charged by the later free row.
        [Fact]
        public void Walk_PaidEarlierThenFreeRepeat_FreeRowIsANoOp()
        {
            var science = RegisterScience();
            var actions = new List<GameAction>
            {
                new GameAction { UT = 0, Type = GameActionType.ScienceInitial, InitialScience = 100f },
                TechRow("free", 5000.0, 10, GiftTech, 0f),
                TechRow("paid", 3000.0, 20, GiftTech, 45f),
            };

            RecalculationEngine.Recalculate(actions);

            Assert.Equal(55.0, science.GetRunningScience(), 3);
            var free = actions.Single(a => a.ActionId == "free");
            Assert.False(free.Effective);
            Assert.Equal(GameActionNotCountedReason.PurchaseAlreadyOwned, free.NotCountedReason);
            Assert.True(actions.Single(a => a.ActionId == "paid").Affordable);
        }

        // ================================================================
        // Non-rewind discard: KSP keeps the node, so the ledger must too
        // ================================================================

        private static void AddTaggedTech(string recordingId, double ut, string detail)
        {
            var e = new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.TechResearched,
                key = GiftTech,
                detail = detail,
                recordingId = recordingId,
            };
            GameStateStore.AddEvent(ref e);
        }

        // catches: a discarded flight's gift being dropped with its tagged event while KSP
        // keeps the node, so the next tech patch re-locks it.
        [Fact]
        public void Discard_FreeUnlock_IsReHomedAsAnUntaggedZeroCostRow()
        {
            GameStateRecorder.TagResolverForTesting = () => "rec-A";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            new GameStateRecorder().CaptureFreeTechUnlock(GiftTech, 45f, null, 5000.0, 0.0);
            Assert.Empty(TechRows());

            LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard(new HashSet<string> { "rec-A" }, "test");

            var rows = TechRows();
            Assert.Single(rows);
            Assert.Equal(GiftTech, rows[0].NodeId);
            Assert.Equal(0f, rows[0].Cost);
            Assert.Equal(5000.0, rows[0].UT);
            Assert.True(string.IsNullOrEmpty(rows[0].RecordingId));
            Assert.Contains(logLines, l => l.Contains("PreserveIrreversibleLiveGameplayOnDiscard (test): re-homed 1")
                && l.Contains("freeTech=1"));
        }

        // catches: a paid research tagged to a flight (research is a Space Center act, filed
        // untagged at once) being re-homed as a second, charged row.
        [Fact]
        public void Discard_PaidResearch_IsNotReHomed()
        {
            AddTaggedTech("rec-A", 5000.0, "cost=45;parts=");
            Assert.False(LedgerOrchestrator.IsIrreversibleLiveGameplayEvent(GameStateStore.Events[0]));

            LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard(new HashSet<string> { "rec-A" }, "test");

            Assert.Empty(TechRows());
        }

        // catches: a substring match on "free=1" reading the marker out of another field's value.
        [Theory]
        [InlineData("cost=45;parts=free=1")]
        [InlineData("cost=45;nonfree=1;parts=")]
        [InlineData("cost=45;free=10;parts=")]
        public void Discard_FreeMarkerOnlyInsideAnotherValue_IsNotMatched(string detail)
        {
            AddTaggedTech("rec-A", 5000.0, detail);
            Assert.False(LedgerOrchestrator.IsFreeTechUnlockEvent(GameStateStore.Events[0]));

            LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard(new HashSet<string> { "rec-A" }, "test");

            Assert.Empty(TechRows());
        }

        // catches: the committed-tree restore attempt's discard tail dropping a gift the clone
        // earned after its cutoff.
        [Fact]
        public void RestoreAttemptTailDiscard_FreeUnlockAfterCutoff_IsReHomed()
        {
            AddTaggedTech("rec-A", 5000.0, GameStateRecorder.BuildFreeTechUnlockDetail(45f, null));

            LedgerOrchestrator.PreserveIrreversibleRestoreAttemptTailsOnDiscard(
                new Dictionary<string, double> { { "rec-A", 4000.0 } }, "test");

            var rows = TechRows();
            Assert.Single(rows);
            Assert.Equal(0f, rows[0].Cost);
            Assert.True(string.IsNullOrEmpty(rows[0].RecordingId));
        }

        // ================================================================
        // Headline regression
        // ================================================================

        // catches: the defect end to end. Flight A launches at UT 100 (baseline B0, no gift), its
        // probe is gifted GiftTech at UT 5000, the commit at 6000 captures B1 holding it. A
        // Rewind-to-Launch prunes B1. Without a ledger row for the gift, every later tech patch
        // target (B0 + rows) lacks it, so PatchTechTree re-locks it for good.
        [Fact]
        public void RewindPrunesTheCommitBaseline_FreeRowKeepsTheGiftInThePatchTarget()
        {
            Ledger.AddAction(new GameAction { UT = 0, Type = GameActionType.ScienceInitial, InitialScience = 20f });

            var b0 = new GameStateBaseline { ut = 100.0 };
            b0.researchedTechIds.Add(StartTech);
            GameStateStore.AddBaseline(b0);

            // The real capture door (KSC-untagged here, so the row lands directly).
            new GameStateRecorder().CaptureFreeTechUnlock(GiftTech, 45f, null, 5000.0, 20.0);

            var b1 = new GameStateBaseline { ut = 6000.0 };
            b1.researchedTechIds.Add(StartTech);
            b1.researchedTechIds.Add(GiftTech);
            GameStateStore.AddBaseline(b1);

            GameStateStore.PruneBaselinesAfterUT(100.0, preserveEarliest: true, deleteFiles: false);
            Assert.Single(GameStateStore.Baselines);

            LedgerOrchestrator.RecalculateAndPatch(7000.0);

            var target = KspStatePatcher.BuildTargetTechIdsForPatch(GameStateStore.Baselines, Ledger.Actions, 7000.0);
            Assert.NotNull(target);
            Assert.Contains(StartTech, target);
            Assert.Contains(GiftTech, target);

            var before = KspStatePatcher.BuildTargetTechIdsForPatch(GameStateStore.Baselines, Ledger.Actions, 4000.0);
            Assert.NotNull(before);
            Assert.Contains(StartTech, before);
            Assert.DoesNotContain(GiftTech, before);

            // The committed-unlock pass (cutoff-less walk at the live clock) reaches it too.
            Assert.Contains(GiftTech, KspStatePatcher.BuildCommittedTechUnlockIdsForPatch(Ledger.Actions, 7000.0));
            Assert.DoesNotContain(GiftTech, KspStatePatcher.BuildCommittedTechUnlockIdsForPatch(Ledger.Actions, 4000.0));
        }
    }
}
