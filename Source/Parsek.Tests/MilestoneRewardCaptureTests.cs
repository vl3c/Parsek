using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers §D/#400 of career-earnings-bundle plan: MilestoneAchieved events must
    /// contain the real funds/rep/sci reward values, not hardcoded zeros. Before the
    /// fix, OnProgressComplete emitted detail="" and ConvertMilestoneAchieved parsed
    /// to zero for every field — so recovery budgets never reflected milestone income.
    ///
    /// The fix splits the emission into two phases: OnProgressComplete writes the event
    /// with zero defaults + tags the ProgressNode in PendingMilestoneEventByNode. The
    /// ProgressRewardPatch Harmony postfix (on ProgressNode.AwardProgress) then calls
    /// EnrichPendingMilestoneRewards with the real values, which updates the detail
    /// string in place. That function is pure and directly testable — real Harmony
    /// plumbing is covered by the in-game test pass.
    /// </summary>
    [Collection("Sequential")]
    public class MilestoneRewardCaptureTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MilestoneRewardCaptureTests()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateRecorder.PendingMilestoneEventByNode.Clear();
        }

        // #442: production passes Planetarium.GetUniversalTime() at the patch site;
        // unit tests pass this literal so they don't NRE on Planetarium statics.
        private const double TestUt = 1234.0;

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            GameStateRecorder.PendingMilestoneEventByNode.Clear();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void BuildMilestoneDetail_FormatsAsExpected()
        {
            var s = GameStateRecorder.BuildMilestoneDetail(3000.0, 5.5f, 2.25);

            Assert.Contains("funds=3000", s);
            Assert.Contains("rep=5.5", s);
            Assert.Contains("sci=2.25", s);
            Assert.Contains(";", s);
        }

        [Fact]
        public void BuildMilestoneDetail_ZeroRewards_ParsesToZero()
        {
            var s = GameStateRecorder.BuildMilestoneDetail(0, 0f, 0);
            var evt = new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "FirstLaunch",
                detail = s
            };

            var action = GameStateEventConverter.ConvertMilestoneAchieved(evt, null);

            Assert.Equal(0f, action.MilestoneFundsAwarded);
            Assert.Equal(0f, action.MilestoneRepAwarded);
        }

        [Fact]
        public void ConvertMilestoneAchieved_ParsesRealRewardValues()
        {
            var evt = new GameStateEvent
            {
                ut = 200,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "Mun/Landing",
                detail = GameStateRecorder.BuildMilestoneDetail(15000.0, 42.5f, 12.0)
            };

            var action = GameStateEventConverter.ConvertMilestoneAchieved(evt, null);

            Assert.Equal(15000f, action.MilestoneFundsAwarded);
            Assert.Equal(42.5f, action.MilestoneRepAwarded);
            Assert.Equal("Mun/Landing", action.MilestoneId);
        }

        [Fact]
        public void EnrichPendingMilestoneRewards_UpdatesStoredEventInPlace()
        {
            // Simulate the OnProgressComplete step: create a pending zero-reward
            // event and a fake progress node, stored in the map.
            var node = new FakeProgressNode("Mun/Landing");
            var evt = new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "Mun/Landing",
                detail = GameStateRecorder.BuildMilestoneDetail(0, 0f, 0)
            };
            GameStateStore.AddEvent(evt);
            GameStateRecorder.PendingMilestoneEventByNode[node] = evt;

            // Simulate the AwardProgress postfix with real values.
            GameStateRecorder.EnrichPendingMilestoneRewards(node, 15000.0, 42.5f, 12.0);

            // GameStateEvent is a struct — query the store to read the patched slot.
            GameStateEvent storedEvt = default;
            bool found = false;
            foreach (var e in GameStateStore.Events)
            {
                if (e.eventType == GameStateEventType.MilestoneAchieved && e.key == "Mun/Landing")
                {
                    storedEvt = e;
                    found = true;
                    break;
                }
            }
            Assert.True(found, "event should still be in the store");
            Assert.Contains("funds=15000", storedEvt.detail);
            Assert.Contains("rep=42.5", storedEvt.detail);
            Assert.Contains("sci=12", storedEvt.detail);

            // And the pending map should be empty.
            Assert.Empty(GameStateRecorder.PendingMilestoneEventByNode);

            // Converter should yield non-zero action.
            var action = GameStateEventConverter.ConvertMilestoneAchieved(storedEvt, null);
            Assert.Equal(15000f, action.MilestoneFundsAwarded);
            Assert.Equal(42.5f, action.MilestoneRepAwarded);
        }

        [Fact]
        public void EnrichPendingMilestoneRewards_UpdatesLedgerActionForKscMilestone()
        {
            // Simulate the KSC path: OnKscSpending has already written a zero-reward
            // MilestoneAchievement action to the ledger. EnrichPendingMilestoneRewards
            // must find it and update the field values in place.
            LedgerOrchestrator.Initialize();

            var node = new FakeProgressNode("FacilityUpgradeMC");
            var evt = new GameStateEvent
            {
                ut = 500,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "FacilityUpgradeMC",
                detail = GameStateRecorder.BuildMilestoneDetail(0, 0f, 0)
            };
            GameStateStore.AddEvent(evt);
            LedgerOrchestrator.OnKscSpending(evt); // writes zero-reward action
            GameStateRecorder.PendingMilestoneEventByNode[node] = evt;

            int before = Ledger.Actions.Count;
            GameStateRecorder.EnrichPendingMilestoneRewards(node, 7500.0, 10.0f, 0);

            // Same number of actions — we updated in place, not appended.
            Assert.Equal(before, Ledger.Actions.Count);

            var updated = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.MilestoneAchievement &&
                a.MilestoneId == "FacilityUpgradeMC");
            Assert.NotNull(updated);
            Assert.Equal(7500f, updated.MilestoneFundsAwarded);
            Assert.Equal(10.0f, updated.MilestoneRepAwarded);
            Assert.Equal(0f, updated.MilestoneScienceAwarded);
        }

        [Fact]
        public void EnrichPendingMilestoneRewards_WithScienceReward_UpdatesLedgerAction()
        {
            // Regression for codex follow-up review: the KSC-path enrichment updated
            // MilestoneFundsAwarded and MilestoneRepAwarded on the ledger action but
            // forgot MilestoneScienceAwarded. Milestones that award science (e.g.
            // Kerbin/Science) thus produced zero-sci ledger actions on the KSC path
            // even after Fix #4 wired up the schema. The earlier test with sci=0
            // could not catch the gap.
            LedgerOrchestrator.Initialize();

            var node = new FakeProgressNode("Kerbin/Science");
            var evt = new GameStateEvent
            {
                ut = 600,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "Kerbin/Science",
                detail = GameStateRecorder.BuildMilestoneDetail(0, 0f, 0)
            };
            GameStateStore.AddEvent(evt);
            LedgerOrchestrator.OnKscSpending(evt); // writes zero-reward action
            GameStateRecorder.PendingMilestoneEventByNode[node] = evt;

            GameStateRecorder.EnrichPendingMilestoneRewards(node, 0.0, 0f, 2.0);

            var updated = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.MilestoneAchievement &&
                a.MilestoneId == "Kerbin/Science");
            Assert.NotNull(updated);
            Assert.Equal(2f, updated.MilestoneScienceAwarded);
        }

        [Fact]
        public void EnrichPendingMilestoneRewards_AllThreeRewards_UpdatesAllFields()
        {
            LedgerOrchestrator.Initialize();

            var node = new FakeProgressNode("Kerbin/Landing");
            var evt = new GameStateEvent
            {
                ut = 700,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "Kerbin/Landing",
                detail = GameStateRecorder.BuildMilestoneDetail(0, 0f, 0)
            };
            GameStateStore.AddEvent(evt);
            LedgerOrchestrator.OnKscSpending(evt);
            GameStateRecorder.PendingMilestoneEventByNode[node] = evt;

            GameStateRecorder.EnrichPendingMilestoneRewards(node, 3000.0, 5f, 1.5);

            var updated = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.MilestoneAchievement &&
                a.MilestoneId == "Kerbin/Landing");
            Assert.NotNull(updated);
            Assert.Equal(3000f, updated.MilestoneFundsAwarded);
            Assert.Equal(5f, updated.MilestoneRepAwarded);
            Assert.Equal(1.5f, updated.MilestoneScienceAwarded);
        }

        [Fact]
        public void EnrichPendingMilestoneRewards_UnknownNode_LogsAndNoOp()
        {
            var node = new FakeProgressNode("NeverSeen");

            GameStateRecorder.EnrichPendingMilestoneRewards(node, 1000, 5f, 0);

            Assert.Contains(logLines, l =>
                l.Contains("[GameStateRecorder]") && l.Contains("no pending event"));
        }

        [Fact]
        public void EnrichPendingMilestoneRewards_NullNode_NoThrow()
        {
            GameStateRecorder.EnrichPendingMilestoneRewards(null, 1000, 5f, 0);
            // Nothing to assert except no throw.
        }

        // #442: World-record progress nodes (RecordsSpeed/Altitude/Distance/Depth) call
        // ProgressNode.AwardProgress directly without firing OnProgressComplete first, so
        // the two-phase emit-then-enrich path never runs and their funds/rep/sci rewards
        // are silently dropped by the converter. EmitStandaloneProgressReward closes the
        // gap by emitting a fully-populated MilestoneAchieved event when the postfix sees
        // no pending entry for the node.

        [Fact]
        public void EmitStandaloneProgressReward_AppendsMilestoneEventWithRewards()
        {
            var node = new FakeProgressNode("RecordsDistance");

            int before = CountEvents(GameStateEventType.MilestoneAchieved);
            GameStateRecorder.EmitStandaloneProgressReward(node, 4800.0, 2.0f, 0.0, TestUt);

            Assert.Equal(before + 1, CountEvents(GameStateEventType.MilestoneAchieved));
            var stored = FindLastMilestoneEvent("RecordsDistance");
            Assert.True(stored.HasValue, "standalone MilestoneAchieved should be in the store");
            Assert.Contains("funds=4800", stored.Value.detail);
            Assert.Contains("rep=2", stored.Value.detail);
            Assert.Contains("sci=0", stored.Value.detail);
        }

        [Fact]
        public void EmitStandaloneProgressReward_DoesNotPopulatePendingMap()
        {
            // The standalone path bypasses the enrichment-map indirection — the node
            // must NOT be left in PendingMilestoneEventByNode (otherwise a subsequent
            // AwardProgress on the same node would erroneously enrich an old event).
            var node = new FakeProgressNode("RecordsAltitude");

            GameStateRecorder.EmitStandaloneProgressReward(node, 4800.0, 2.0f, 0.0, TestUt);

            Assert.False(GameStateRecorder.PendingMilestoneEventByNode.ContainsKey(node));
        }

        [Fact]
        public void EmitStandaloneProgressReward_ConvertsToMilestoneActionWithRewards()
        {
            // End-to-end: the standalone-emitted event must round-trip through the
            // converter into a MilestoneAchievement GameAction with non-zero rewards.
            var node = new FakeProgressNode("RecordsSpeed");
            GameStateRecorder.EmitStandaloneProgressReward(node, 4800.0, 2.0f, 0.0, TestUt);

            var stored = FindLastMilestoneEvent("RecordsSpeed");
            Assert.True(stored.HasValue);

            var action = GameStateEventConverter.ConvertMilestoneAchieved(stored.Value, null);
            Assert.Equal("RecordsSpeed", action.MilestoneId);
            Assert.Equal(4800f, action.MilestoneFundsAwarded);
            Assert.Equal(2f, action.MilestoneRepAwarded);
            Assert.Equal(0f, action.MilestoneScienceAwarded);
        }

        [Fact]
        public void EmitStandaloneProgressReward_LogsEmissionWithFundsAmount()
        {
            var node = new FakeProgressNode("RecordsDepth");

            GameStateRecorder.EmitStandaloneProgressReward(node, 4800.0, 2.0f, 0.0, TestUt);

            Assert.Contains(logLines, l =>
                l.Contains("[GameStateRecorder]") &&
                l.Contains("standalone") &&
                l.Contains("RecordsDepth") &&
                l.Contains("funds=4800"));
        }

        [Fact]
        public void EmitStandaloneProgressReward_NullNode_NoThrowAndNoEvent()
        {
            int before = CountEvents(GameStateEventType.MilestoneAchieved);

            GameStateRecorder.EmitStandaloneProgressReward(null, 4800.0, 2.0f, 0.0, TestUt);

            Assert.Equal(before, CountEvents(GameStateEventType.MilestoneAchieved));
            Assert.Contains(logLines, l =>
                l.Contains("[GameStateRecorder]") && l.Contains("null node"));
        }

        [Fact]
        public void EmitStandaloneProgressReward_DuringActionReplay_Suppressed()
        {
            // The same suppression guard the OnProgressComplete subscriber uses: while
            // KspStatePatcher is replaying ledger actions, the synthetic AwardProgress
            // calls must not emit fresh MilestoneAchieved events back into the store.
            var node = new FakeProgressNode("RecordsDistance");
            int before = CountEvents(GameStateEventType.MilestoneAchieved);

            GameStateRecorder.IsReplayingActions = true;
            try
            {
                GameStateRecorder.EmitStandaloneProgressReward(node, 4800.0, 2.0f, 0.0, TestUt);
            }
            finally
            {
                GameStateRecorder.IsReplayingActions = false;
            }

            Assert.Equal(before, CountEvents(GameStateEventType.MilestoneAchieved));
            Assert.Contains(logLines, l =>
                l.Contains("[GameStateRecorder]") && l.Contains("suppressed during action replay"));
        }

        // #442 follow-up: pin the ProgressRewardPatch.RoutePostfix branch decision so a
        // future regression that swaps the if/else (always enrich, or always emit-
        // standalone) fails one of these two tests. Helpers are tested above; this layer
        // covers the routing itself.

        [Fact]
        public void RoutePostfix_NoPendingEntry_EmitsStandaloneMilestoneEvent()
        {
            // Empty pending map -> postfix must take the EmitStandaloneProgressReward
            // branch and append a fresh MilestoneAchieved event (the standalone signature
            // 'MilestoneAchievedStandalone' shows up in the source-tagged Emit log).
            var node = new FakeProgressNode("RecordsDistance");
            Assert.Empty(GameStateRecorder.PendingMilestoneEventByNode);
            int before = CountEvents(GameStateEventType.MilestoneAchieved);

            Parsek.Patches.ProgressRewardPatch.RoutePostfix(
                node, funds: 4800f, science: 0f, reputation: 2f, ut: TestUt);

            // Branch effect: one new MilestoneAchieved event in the store, with rewards
            // baked in (the enrich branch would not have appended anything because the
            // map was empty).
            Assert.Equal(before + 1, CountEvents(GameStateEventType.MilestoneAchieved));
            var stored = FindLastMilestoneEvent("RecordsDistance");
            Assert.True(stored.HasValue, "standalone branch should have appended a MilestoneAchieved event");
            Assert.Contains("funds=4800", stored.Value.detail);
            // Source tag from the standalone Emit call — distinguishes from the enrich path.
            Assert.Contains(logLines, l =>
                l.Contains("[GameStateRecorder]") &&
                l.Contains("source='MilestoneAchievedStandalone'"));
        }

        [Fact]
        public void RoutePostfix_PendingEntry_EnrichesInPlaceWithoutAppending()
        {
            // Pre-populated pending entry -> postfix must take the
            // EnrichPendingMilestoneRewards branch: rewards are written into the existing
            // event's detail in place, and NO new MilestoneAchieved event is appended.
            var node = new FakeProgressNode("Mun/Landing");
            var seed = new GameStateEvent
            {
                ut = TestUt,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "Mun/Landing",
                detail = GameStateRecorder.BuildMilestoneDetail(0, 0f, 0)
            };
            GameStateStore.AddEvent(seed);
            GameStateRecorder.PendingMilestoneEventByNode[node] = seed;

            int before = CountEvents(GameStateEventType.MilestoneAchieved);

            Parsek.Patches.ProgressRewardPatch.RoutePostfix(
                node, funds: 15000f, science: 12f, reputation: 42.5f, ut: TestUt);

            // Branch effect: same number of MilestoneAchieved events (no append) and the
            // existing event was updated in place. The standalone branch would have
            // appended a second event with the same key.
            Assert.Equal(before, CountEvents(GameStateEventType.MilestoneAchieved));
            var stored = FindLastMilestoneEvent("Mun/Landing");
            Assert.True(stored.HasValue);
            Assert.Contains("funds=15000", stored.Value.detail);
            Assert.Contains("rep=42.5", stored.Value.detail);
            Assert.Contains("sci=12", stored.Value.detail);
            // Pending entry consumed by the enrich branch.
            Assert.False(GameStateRecorder.PendingMilestoneEventByNode.ContainsKey(node));
            // Standalone-source tag must NOT appear — confirms the enrich branch ran.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("source='MilestoneAchievedStandalone'"));
        }

        // #442 follow-up: end-to-end smoke that the standalone-emit fix actually closes
        // the ReconcileEarningsWindow gap that motivated the bug. Synthesizes the
        // RecordsDistance-style pair (FundsChanged(Progression) + standalone
        // MilestoneAchieved -> ConvertMilestoneAchieved -> MilestoneAchievement action)
        // and asserts that ReconcileEarningsWindow stays silent.

        [Fact]
        public void ReconcileEarningsWindow_StandaloneMilestonePairsCleanly_NoMismatchWarn()
        {
            // 1. Synthesize the FundsChanged(Progression) event KSP fires when AwardProgress
            //    runs for a world-record node. RecordsDistance grants 4800 funds + 2 rep
            //    in stock career, so the converter's drop-rule swallows the +4800 funds
            //    and the +2 rep here — they have to be re-supplied through the milestone
            //    channel to balance.
            const double ut = 5000.0;
            var fundsChanged = new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.FundsChanged,
                key = "Progression",  // KSP TransactionReasons enum string
                valueBefore = 50000.0,
                valueAfter = 54800.0   // +4800
            };
            var repChanged = new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.ReputationChanged,
                key = "Progression",
                valueBefore = 5.0,
                valueAfter = 7.0       // +2
            };

            // 2. Run the standalone-emit path that the patch postfix now takes for
            //    RecordsDistance-style nodes. This appends a fully-populated
            //    MilestoneAchieved event to the store with rewards baked into detail.
            var node = new FakeProgressNode("RecordsDistance");
            GameStateRecorder.EmitStandaloneProgressReward(node, 4800.0, 2.0f, 0.0, ut);

            // 3. Convert that event the same way OnRecordingCommitted's ConvertEvents
            //    walk would. The result is a MilestoneAchievement action carrying the
            //    +4800 funds / +2 rep awards that ReconcileEarningsWindow's emitted-side
            //    sum draws from.
            var stored = FindLastMilestoneEvent("RecordsDistance");
            Assert.True(stored.HasValue);
            var milestoneAction = GameStateEventConverter.ConvertMilestoneAchieved(
                stored.Value, recordingId: null);

            // 4. Run reconciliation across the FundsChanged + ReputationChanged pair on
            //    the store side and the MilestoneAchievement action on the ledger side.
            //    Without the fix there is no MilestoneAchievement to balance the dropped
            //    Progression FundsChanged delta and the matcher fires WARN. With the fix
            //    the two sides match within tolerance and no WARN appears.
            var events = new List<GameStateEvent> { fundsChanged, repChanged };
            var newActions = new List<GameAction> { milestoneAction };

            // Drop pre-existing log noise from EmitStandaloneProgressReward so the assert
            // only judges what reconciliation itself logs.
            logLines.Clear();

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                startUT: ut - 100, endUT: ut + 100);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("Earnings reconciliation (funds)"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("Earnings reconciliation (rep)"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("Earnings reconciliation (sci)"));
        }

        [Fact]
        public void EmitStandaloneProgressReward_RepeatedRecordHits_BothStayEffective()
        {
            var node = new FakeProgressNode("RecordsDistance");
            GameStateRecorder.EmitStandaloneProgressReward(node, 4800.0, 2.0f, 0.0, ut: 100.0);
            GameStateRecorder.EmitStandaloneProgressReward(node, 3200.0, 1.0f, 0.0, ut: 200.0);

            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 0.0,
                    Type = GameActionType.FundsInitial,
                    InitialFunds = 10000f
                }
            };

            foreach (var evt in GameStateStore.Events)
            {
                if (evt.eventType == GameStateEventType.MilestoneAchieved &&
                    evt.key == "RecordsDistance")
                {
                    actions.Add(GameStateEventConverter.ConvertMilestoneAchieved(evt, null));
                }
            }

            var milestones = new MilestonesModule();
            var funds = new FundsModule();
            RecalculationEngine.RegisterModule(milestones, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);

            RecalculationEngine.Recalculate(actions);

            var recordActions = actions.Where(a =>
                a.Type == GameActionType.MilestoneAchievement &&
                a.MilestoneId == "RecordsDistance").ToList();
            Assert.Equal(2, recordActions.Count);
            Assert.All(recordActions, a => Assert.True(a.Effective));
            Assert.True(milestones.IsMilestoneCredited("RecordsDistance"));
            Assert.Equal(1, milestones.GetCreditedCount());
            Assert.Equal(18000.0, funds.GetRunningBalance(), 1);
        }

        private static int CountEvents(GameStateEventType type)
        {
            int n = 0;
            foreach (var e in GameStateStore.Events)
                if (e.eventType == type) n++;
            return n;
        }

        private static GameStateEvent? FindLastMilestoneEvent(string key)
        {
            GameStateEvent? hit = null;
            foreach (var e in GameStateStore.Events)
            {
                if (e.eventType == GameStateEventType.MilestoneAchieved && e.key == key)
                    hit = e;
            }
            return hit;
        }

        /// <summary>
        /// Minimal ProgressNode stand-in for tests — real KSP ProgressNode requires
        /// Complete/Fire plumbing that we don't want to invoke. The key is only reference
        /// equality in the pending map, so any ProgressNode subclass works.
        /// </summary>
        private class FakeProgressNode : ProgressNode
        {
            public FakeProgressNode(string id) : base(id, startReached: false) { }
        }
    }
}
