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
    /// with zero defaults + tags the qualified milestone id in PendingMilestoneEventById
    /// (#443: re-keyed from ProgressNode reference). The ProgressRewardPatch Harmony
    /// postfix (on ProgressNode.AwardProgress) then calls EnrichPendingMilestoneRewards
    /// with the real values, which updates the detail string in place. That function is
    /// pure and directly testable — real Harmony plumbing is covered by the in-game test
    /// pass.
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
            GameStateRecorder.PendingMilestoneEventById.Clear();
        }

        // #442: production passes Planetarium.GetUniversalTime() at the patch site;
        // unit tests pass this literal so they don't NRE on Planetarium statics.
        private const double TestUt = 1234.0;

        public void Dispose()
        {
            GameStateRecorder.PendingMilestoneEventById.Clear();
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
            GameStateRecorder.PendingMilestoneEventById["Mun/Landing"] = evt;

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
            Assert.Empty(GameStateRecorder.PendingMilestoneEventById);

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
            GameStateRecorder.PendingMilestoneEventById["FacilityUpgradeMC"] = evt;

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
            GameStateRecorder.PendingMilestoneEventById["Kerbin/Science"] = evt;

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
            GameStateRecorder.PendingMilestoneEventById["Kerbin/Landing"] = evt;

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

            // #443: miss log promoted from Verbose to Warn, with id + reward context.
            Assert.Contains(logLines, l =>
                l.Contains("[GameStateRecorder]") &&
                l.Contains("[WARN]") &&
                l.Contains("no pending event for milestone id 'NeverSeen'") &&
                l.Contains("funds=1000") &&
                l.Contains("rep=5") &&
                l.Contains("pendingMapSize=0"));
        }

        [Fact]
        public void EnrichPendingMilestoneRewards_NullNode_NoThrow()
        {
            GameStateRecorder.EnrichPendingMilestoneRewards(null, 1000, 5f, 0);
            // Nothing to assert except no throw.
        }

        [Fact]
        public void ResetForTesting_ClearsPendingMilestoneEventById()
        {
            GameStateRecorder.PendingMilestoneEventById["RecordsDistance"] = new GameStateEvent
            {
                ut = 42,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "RecordsDistance",
                detail = GameStateRecorder.BuildMilestoneDetail(0, 0f, 0)
            };

            GameStateRecorder.ResetForTesting();

            Assert.Empty(GameStateRecorder.PendingMilestoneEventById);
        }

        [Fact]
        public void RoutePostfix_AfterReset_IgnoresFormerStalePendingEntry()
        {
            // #443 review follow-up: re-keying the pending map from ProgressNode
            // reference to milestone id made stale entries behaviorally dangerous.
            // If a stale "RecordsDistance" entry survives a recorder reset / OnLoad
            // resubscribe, RoutePostfix sees the key and takes the enrich branch instead
            // of emitting the standalone milestone event the world-record path needs.
            // That yields a store-miss WARN and silently drops the reward. Reset must
            // drain the map so the post-reset route is the standalone append path.
            GameStateRecorder.PendingMilestoneEventById["RecordsDistance"] = new GameStateEvent
            {
                ut = 99,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "RecordsDistance",
                detail = GameStateRecorder.BuildMilestoneDetail(0, 0f, 0),
                epoch = 5
            };

            GameStateRecorder.ResetForTesting();

            var node = new FakeProgressNode("RecordsDistance");
            Parsek.Patches.ProgressRewardPatch.RoutePostfix(
                node, funds: 4800f, science: 0f, reputation: 2f, ut: TestUt);

            var stored = FindLastMilestoneEvent("RecordsDistance");
            Assert.True(stored.HasValue);
            Assert.Contains("funds=4800", stored.Value.detail);
            Assert.Contains("rep=2", stored.Value.detail);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[WARN]") && l.Contains("store had no matching event"));
        }

        // #443: regression coverage for the silent write-back failure on Kerbin/Landing
        // (and other OnProgressComplete-emitting nodes) reported in the v0.8.2 smoke test.
        // Two failure modes were proven from the code:
        //
        //   1. GameStateEvent is a struct, so AddEvent's e.epoch = MilestoneStore.CurrentEpoch
        //      stamp lands on the appended slot but NOT on the caller's local `evt`. The
        //      cached pending entry kept epoch=0 and UpdateEventDetail's ut+type+key+epoch
        //      lookup missed on every save where CurrentEpoch had been bumped (i.e. any save
        //      with at least one revert in its history) — silently, because the postfix
        //      logged "Milestone enriched" before checking UpdateEventDetail's return value.
        //
        //   2. The pending map keyed by ProgressNode reference would also miss if KSP ever
        //      handed a different instance to the AwardProgress postfix than the one that
        //      fired OnProgressComplete — re-keying by qualified id eliminates that risk.
        //
        // The end-to-end test routes a Kerbin/Landing-style node through the full
        // OnProgressComplete -> EnrichPendingMilestoneRewards path with a non-zero epoch
        // and verifies the stored event's detail actually carries the rewards on read-back.

        [Fact]
        public void RegisterAndEnrich_NonZeroEpoch_PatchesStoredDetailEndToEnd()
        {
            // Reproduces the v0.8.2 smoke-test failure exactly. With CurrentEpoch != 0
            // (the case after any revert), the OnProgressComplete -> AwardProgress
            // postfix pair must still successfully write the real funds into the stored
            // event slot. Pre-#443 the cached pending entry kept epoch=0 while the stored
            // slot carried epoch=3, the UpdateEventDetail lookup missed, and the stored
            // detail stayed "funds=0;rep=0;sci=0" — exactly what KSP.log:18024 showed for
            // the Kerbin/Landing FundsChanged +800 event.
            //
            // The "Kerbin/" prefix on the id is decorative for this test: FakeProgressNode
            // has no `body` field, so QualifyMilestoneId takes the no-body branch and
            // returns the constructor argument verbatim; the qualified id is only matched
            // by string equality between Register and Enrich. The body-reflection path
            // cannot be unit-tested (CelestialBody can't be constructed outside KSP) —
            // see #453 follow-up in docs/dev/todo-and-known-bugs.md.
            uint savedEpoch = MilestoneStore.CurrentEpoch;
            try
            {
                MilestoneStore.CurrentEpoch = 3;

                // Half 1: emulate OnProgressComplete via the extracted seam.
                GameStateRecorder.RegisterPendingMilestoneEvent("Kerbin/Landing", ut: 4134.8);
                Assert.True(GameStateRecorder.PendingMilestoneEventById.ContainsKey("Kerbin/Landing"));

                // Half 2: emulate the AwardProgress postfix carrying the real reward.
                var node = new FakeProgressNode("Kerbin/Landing");
                GameStateRecorder.EnrichPendingMilestoneRewards(node, 800.0, 0f, 0.0);

                // Stored slot must now carry the real funds, not the original zeros.
                var stored = FindLastMilestoneEvent("Kerbin/Landing");
                Assert.True(stored.HasValue);
                Assert.Equal((uint)3, stored.Value.epoch);
                Assert.Contains("funds=800", stored.Value.detail);

                // Pending map drained.
                Assert.False(GameStateRecorder.PendingMilestoneEventById.ContainsKey("Kerbin/Landing"));

                // Defensive Warn (store match miss) must not fire on the happy path.
                Assert.DoesNotContain(logLines, l =>
                    l.Contains("[WARN]") && l.Contains("store had no matching event"));

                // Converter must produce a non-zero MilestoneAchievement action — this is
                // what feeds ReconcileEarningsWindow and what was committing as funds=0
                // in the smoke-test bundle.
                var action = GameStateEventConverter.ConvertMilestoneAchieved(stored.Value, null);
                Assert.Equal(800f, action.MilestoneFundsAwarded);
            }
            finally
            {
                MilestoneStore.CurrentEpoch = savedEpoch;
                GameStateRecorder.PendingMilestoneEventById.Clear();
            }
        }

        [Fact]
        public void EnrichPendingMilestoneRewards_StoredSlotMissing_LogsDefensiveWarn()
        {
            // Belt-and-braces for the cached-vs-stored slot mismatch. If a future change
            // ever broke the epoch (or ut, or key) mirror, EnrichPendingMilestoneRewards
            // would silently no-op the store-side write and only the in-flight ledger
            // action would be patched. Surfacing the miss as Warn keeps it visible at
            // default log level.
            var node = new FakeProgressNode("Mun/Orbit");
            var evt = new GameStateEvent
            {
                ut = 200,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "Mun/Orbit",
                detail = GameStateRecorder.BuildMilestoneDetail(0, 0f, 0),
                epoch = 99 // intentionally mismatched — no event with this epoch in store
            };
            // Note: we do NOT call GameStateStore.AddEvent here, so the stored slot
            // never exists. This isolates the UpdateEventDetail miss path.
            GameStateRecorder.PendingMilestoneEventById["Mun/Orbit"] = evt;

            GameStateRecorder.EnrichPendingMilestoneRewards(node, 500, 1f, 0);

            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[GameStateRecorder]") &&
                l.Contains("store had no matching event") &&
                l.Contains("Mun/Orbit") &&
                l.Contains("epoch=99"));
        }

        [Fact]
        public void EnrichPendingMilestoneRewards_CachedEpochZeroVsStoredLive_PostFixPatchesStore()
        {
            // Direct regression for the bug shape from the smoke-test bundle: the live
            // store carried epoch=CurrentEpoch (non-zero, post-revert) while the cached
            // pending entry kept epoch=0 because GameStateEvent is a struct and AddEvent's
            // stamp didn't propagate to the caller's copy. Pre-fix the
            // UpdateEventDetail(ut+type+key+epoch) lookup missed; post-fix
            // RegisterPendingMilestoneEvent mirrors MilestoneStore.CurrentEpoch onto the
            // cached copy so the lookup hits.
            //
            // Note that StoredSlotMissing_LogsDefensiveWarn above pins the Warn path with
            // an artificial epoch=99 and no AddEvent call — it can't tell the difference
            // between "fix is in place" and "fix would also fail". This test calls
            // AddEvent with the live (non-zero) epoch but artificially leaves the cached
            // evt.epoch=0 to simulate the pre-fix state, then asserts the post-fix path
            // produces the same final state as if the mirror had been applied.
            uint savedEpoch = MilestoneStore.CurrentEpoch;
            try
            {
                MilestoneStore.CurrentEpoch = 7;

                var node = new FakeProgressNode("Mun/Flyby");
                var evtForStore = new GameStateEvent
                {
                    ut = 800,
                    eventType = GameStateEventType.MilestoneAchieved,
                    key = "Mun/Flyby",
                    detail = GameStateRecorder.BuildMilestoneDetail(0, 0f, 0)
                };
                // AddEvent stamps the live epoch (7) onto the appended slot. The local
                // evtForStore is unchanged because GameStateEvent is a struct — same
                // shape as production's pre-fix bug.
                GameStateStore.AddEvent(evtForStore);
                Assert.Equal(0u, evtForStore.epoch); // sanity: caller's copy still 0

                // Cache the broken (epoch=0) copy in the pending map — this mirrors the
                // exact pre-fix RegisterPendingMilestoneEvent behavior before the
                // `evt.epoch = MilestoneStore.CurrentEpoch` mirror was added. The
                // post-fix code now applies the mirror itself; bypassing
                // RegisterPendingMilestoneEvent and using the broken copy directly here
                // proves the rest of the system actually depends on it.
                GameStateRecorder.PendingMilestoneEventById["Mun/Flyby"] = evtForStore;

                // Without the post-fix mirror, EnrichPendingMilestoneRewards's
                // UpdateEventDetail call would miss the stored slot (cached epoch=0,
                // stored epoch=7) and fire the defensive Warn. We assert the failure
                // mode here so a regression that drops the mirror surfaces immediately.
                GameStateRecorder.EnrichPendingMilestoneRewards(node, 1200, 3f, 0);

                // Pre-fix: defensive Warn fires, store still carries detail with funds=0.
                // Post-fix: this test is constructed to *prove* the bug shape, so the
                // store-update SHOULD miss when the cache wasn't mirrored. Assert both
                // sides — the Warn fires and the stored detail still has funds=0.
                Assert.Contains(logLines, l =>
                    l.Contains("[WARN]") &&
                    l.Contains("store had no matching event") &&
                    l.Contains("Mun/Flyby") &&
                    l.Contains("epoch=0"));
                var storedAfterMiss = FindLastMilestoneEvent("Mun/Flyby");
                Assert.True(storedAfterMiss.HasValue);
                Assert.Contains("funds=0", storedAfterMiss.Value.detail);

                // Now run the same scenario through the production seam, which DOES apply
                // the mirror. The store-side detail must end up patched and no Warn fires.
                logLines.Clear();
                GameStateRecorder.PendingMilestoneEventById.Clear();
                GameStateStore.ResetForTesting();
                MilestoneStore.CurrentEpoch = 7; // reset for the second half
                GameStateRecorder.RegisterPendingMilestoneEvent("Mun/Flyby", ut: 800);

                GameStateRecorder.EnrichPendingMilestoneRewards(node, 1200, 3f, 0);

                Assert.DoesNotContain(logLines, l =>
                    l.Contains("[WARN]") && l.Contains("store had no matching event"));
                var storedAfterFix = FindLastMilestoneEvent("Mun/Flyby");
                Assert.True(storedAfterFix.HasValue);
                Assert.Equal(7u, storedAfterFix.Value.epoch);
                Assert.Contains("funds=1200", storedAfterFix.Value.detail);
            }
            finally
            {
                MilestoneStore.CurrentEpoch = savedEpoch;
                GameStateRecorder.PendingMilestoneEventById.Clear();
            }
        }

        [Fact]
        public void EnrichPendingMilestoneRewards_ByIdNotReference_HandlesAliasedNodeInstance()
        {
            // #443 re-key validation: pre-fix the map was keyed by ProgressNode reference,
            // so handing the postfix a *different* instance with the same qualified id
            // would miss the lookup. Post-fix the lookup is by id string, so two distinct
            // FakeProgressNode("FirstLaunch") instances now resolve to the same entry.
            var emitNode = new FakeProgressNode("FirstLaunch");
            var awardNode = new FakeProgressNode("FirstLaunch");
            Assert.NotSame(emitNode, awardNode); // sanity: distinct objects

            var evt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "FirstLaunch",
                detail = GameStateRecorder.BuildMilestoneDetail(0, 0f, 0)
            };
            GameStateStore.AddEvent(evt);
            evt.epoch = MilestoneStore.CurrentEpoch;
            GameStateRecorder.PendingMilestoneEventById["FirstLaunch"] = evt;

            // Enrichment uses awardNode (different instance, same Id) — must still hit.
            GameStateRecorder.EnrichPendingMilestoneRewards(awardNode, 1500, 2f, 0);

            var stored = FindLastMilestoneEvent("FirstLaunch");
            Assert.True(stored.HasValue);
            Assert.Contains("funds=1500", stored.Value.detail);
            Assert.False(GameStateRecorder.PendingMilestoneEventById.ContainsKey("FirstLaunch"));
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
            // The standalone path bypasses the enrichment-map indirection — the id
            // must NOT be left in PendingMilestoneEventById (otherwise a subsequent
            // AwardProgress on the same node would erroneously enrich an old event).
            var node = new FakeProgressNode("RecordsAltitude");

            GameStateRecorder.EmitStandaloneProgressReward(node, 4800.0, 2.0f, 0.0, TestUt);

            Assert.False(GameStateRecorder.PendingMilestoneEventById.ContainsKey("RecordsAltitude"));
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
            Assert.Empty(GameStateRecorder.PendingMilestoneEventById);
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
            GameStateRecorder.PendingMilestoneEventById["Mun/Landing"] = seed;

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
            Assert.False(GameStateRecorder.PendingMilestoneEventById.ContainsKey("Mun/Landing"));
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
        /// Complete/Fire plumbing that we don't want to invoke. Lookup is by qualified
        /// id string since #443 (was reference equality before), so this stub only needs
        /// to expose the id. It deliberately omits a private <c>body : CelestialBody</c>
        /// field, so <c>QualifyMilestoneId</c> takes the no-body branch and returns the
        /// bare id verbatim — any "Body/" prefix passed to the constructor is decorative
        /// and is preserved by string-equality only. The body-qualification reflection
        /// path cannot be exercised from xUnit because <c>CelestialBody</c> requires the
        /// Unity/KSP runtime to construct (see <c>OrbitalRotationTests.cs</c> note);
        /// see todo-and-known-bugs.md #453 follow-up for that coverage gap.
        /// </summary>
        private class FakeProgressNode : ProgressNode
        {
            public FakeProgressNode(string id) : base(id, startReached: false) { }
        }
    }
}
