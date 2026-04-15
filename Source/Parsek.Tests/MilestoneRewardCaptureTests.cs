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
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateRecorder.PendingMilestoneEventByNode.Clear();
        }

        public void Dispose()
        {
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
