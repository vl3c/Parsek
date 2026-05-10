using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers §A/#405 of career-earnings-bundle plan: five event types that fire at
    /// KSC (ContractAccepted/Completed/Failed/Cancelled + PartPurchased) must reach
    /// the ledger in real time via LedgerOrchestrator.OnKscSpending. Before the fix
    /// their handlers only wrote to GameStateStore, and nothing converted them to
    /// GameActions until the next recording commit — which for a player staying at
    /// KSC never came.
    ///
    /// The handlers themselves are private instance methods on GameStateRecorder and
    /// take KSP-only types (Contract, AvailablePart) which can't be constructed in unit
    /// tests. We therefore test the narrow contract the handlers rely on: given a well-
    /// formed GameStateEvent, OnKscSpending lands a matching GameAction in the ledger.
    ///
    /// Also covers #444 vessel-recovery routing tests at the bottom of the file —
    /// these use <c>RecordingStore</c> as a fixture dependency (lookup of the matching
    /// committed recording for the recovery payout's RecordingId tag), so the setup
    /// adds <c>RecordingStore.ResetForTesting()</c> on top of the §A baseline.
    /// </summary>
    [Collection("Sequential")]
    public class GameStateRecorderLedgerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GameStateRecorderLedgerTests()
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

        [Fact]
        public void OnKscSpending_ContractAccepted_AddsContractAcceptAction()
        {
            var evt = new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.ContractAccepted,
                key = "contract-guid-1",
                detail = "title=Explore Mun;deadline=NaN;type=PartTest;failFunds=5000;failRep=10"
            };

            LedgerOrchestrator.OnKscSpending(evt);

            var match = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.ContractAccept && a.ContractId == "contract-guid-1");
            Assert.NotNull(match);
            Assert.Equal("PartTest", match.ContractType);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("KSC spending recorded") && l.Contains("ContractAccept"));
        }

        [Fact]
        public void OnKspLoad_MigratedFutureContractLifecycleRows_PrunedByMaxUt()
        {
            var accept = new GameStateEvent
            {
                ut = 20000.0,
                eventType = GameStateEventType.ContractAccepted,
                key = "contract-future",
                detail = "title=Future Contract;deadline=NaN;type=PartTest;funds=5000;failFunds=1000;failRep=1"
            };
            var complete = new GameStateEvent
            {
                ut = 20100.0,
                eventType = GameStateEventType.ContractCompleted,
                key = "contract-future",
                detail = "title=Future Contract;fundsReward=10000;repReward=5;sciReward=2"
            };
            GameStateStore.AddEvent(ref accept);
            GameStateStore.AddEvent(ref complete);

            LedgerOrchestrator.OnKspLoad(
                new HashSet<string> { "rec-current" },
                maxUT: 18000.0);

            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.ContractAccept
                && a.ContractId == "contract-future");
            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.ContractComplete
                && a.ContractId == "contract-future");
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("MigrateOldSaveEvents: migrated"));
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]")
                && l.Contains("Pruned contract lifecycle")
                && l.Contains("ContractAccept"));
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]")
                && l.Contains("Pruned contract lifecycle")
                && l.Contains("ContractComplete"));
        }

        [Fact]
        public void OnKspLoad_PostMigrationSyntheticFutureContractAccept_SurvivesTargetedReconcile()
        {
            var oldSaveAccept = new GameStateEvent
            {
                ut = 20000.0,
                eventType = GameStateEventType.ContractAccepted,
                key = "old-event-future",
                detail = "title=Old Future;deadline=NaN;type=PartTest;funds=5000;failFunds=1000;failRep=1"
            };
            GameStateStore.AddEvent(ref oldSaveAccept);

            LedgerOrchestrator.OnKspLoadAfterOldSaveEventReconcileForTesting = () =>
            {
                Ledger.AddAction(new GameAction
                {
                    Type = GameActionType.ContractAccept,
                    UT = 21000.0,
                    RecordingId = "rec-current",
                    ContractId = "post-migration-synthetic",
                    ContractType = "PartTest",
                    DeadlineUT = float.NaN
                });
            };

            LedgerOrchestrator.OnKspLoad(
                new HashSet<string> { "rec-current" },
                maxUT: 18000.0);

            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.ContractAccept
                && a.ContractId == "old-event-future");
            Assert.Contains(Ledger.Actions, a =>
                a.Type == GameActionType.ContractAccept
                && a.ContractId == "post-migration-synthetic");
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]")
                && l.Contains("prunedContractLifecycle=1"));
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("TryRecoverBrokenLedgerOnLoad")
                && l.Contains("skippedFuture=1"));
        }

        [Fact]
        public void OnKscSpending_ContractCompleted_AddsContractCompleteAction()
        {
            var evt = new GameStateEvent
            {
                ut = 200.0,
                eventType = GameStateEventType.ContractCompleted,
                key = "contract-guid-2",
                detail = "title=Test Flight;fundsReward=8000;repReward=5;sciReward=2"
            };

            LedgerOrchestrator.OnKscSpending(evt);

            var match = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.ContractComplete && a.ContractId == "contract-guid-2");
            Assert.NotNull(match);
            Assert.Equal(8000f, match.FundsReward);
            Assert.Equal(5f, match.RepReward);
        }

        [Fact]
        public void OnKscSpending_ContractFailed_AddsContractFailAction()
        {
            var evt = new GameStateEvent
            {
                ut = 300.0,
                eventType = GameStateEventType.ContractFailed,
                key = "contract-guid-3",
                detail = "title=Fail;fundsPenalty=2000;repPenalty=15"
            };

            LedgerOrchestrator.OnKscSpending(evt);

            var match = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.ContractFail && a.ContractId == "contract-guid-3");
            Assert.NotNull(match);
            Assert.Equal(2000f, match.FundsPenalty);
        }

        [Fact]
        public void OnKscSpending_ContractCancelled_AddsContractCancelAction()
        {
            var evt = new GameStateEvent
            {
                ut = 400.0,
                eventType = GameStateEventType.ContractCancelled,
                key = "contract-guid-4",
                detail = "title=Cancel;fundsPenalty=500;repPenalty=3"
            };

            LedgerOrchestrator.OnKscSpending(evt);

            var match = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.ContractCancel && a.ContractId == "contract-guid-4");
            Assert.NotNull(match);
            Assert.Equal(500f, match.FundsPenalty);
        }

        [Fact]
        public void OnKscSpending_PartPurchased_AddsFundsSpendingActionWithDedupKey()
        {
            var evt = new GameStateEvent
            {
                ut = 500.0,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod",
                detail = "cost=600"
            };

            LedgerOrchestrator.OnKscSpending(evt);

            var match = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.FundsSpending && a.DedupKey == "mk1pod");
            Assert.NotNull(match);
            Assert.Equal(FundsSpendingSource.Other, match.FundsSpendingSource);
            Assert.Equal(600f, match.FundsSpent);
        }

        [Fact]
        public void OnKscSpending_PartPurchased_UsesCostTokenWhenEntryCostAlsoPresent()
        {
            // #451: post-fix events may persist the raw stock entry price for diagnostics,
            // but the ledger must honor the actual charged amount from `cost=`. This
            // guards the stock-default bypass=true shape (`cost=0;entryCost=<raw>`).
            var evt = new GameStateEvent
            {
                ut = 550.0,
                eventType = GameStateEventType.PartPurchased,
                key = "solidBooster.v2",
                detail = "cost=0;entryCost=800"
            };

            LedgerOrchestrator.OnKscSpending(evt);

            var match = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.FundsSpending && a.DedupKey == "solidBooster.v2");
            Assert.NotNull(match);
            Assert.Equal(0f, match.FundsSpent);
        }

        [Fact]
        public void ComputePartPurchaseFundsSpent_BypassOn_ReturnsZero()
        {
            try
            {
                GameStateRecorder.BypassEntryPurchaseAfterResearchProviderForTesting = () => true;
                Assert.Equal(0f, GameStateRecorder.ComputePartPurchaseFundsSpent(800f));
            }
            finally
            {
                GameStateRecorder.BypassEntryPurchaseAfterResearchProviderForTesting = null;
            }
        }

        [Fact]
        public void ComputePartPurchaseFundsSpent_BypassOff_ReturnsEntryCost()
        {
            try
            {
                GameStateRecorder.BypassEntryPurchaseAfterResearchProviderForTesting = () => false;
                Assert.Equal(800f, GameStateRecorder.ComputePartPurchaseFundsSpent(800f));
            }
            finally
            {
                GameStateRecorder.BypassEntryPurchaseAfterResearchProviderForTesting = null;
            }
        }

        [Fact]
        public void OnKscSpending_TwoDifferentParts_BothLandInLedger()
        {
            // Regression guard for §F dedup key collision — two KSC part purchases
            // at almost-identical UTs must both survive after each is piped through
            // OnKscSpending (the second goes through DeduplicateAgainstLedger against
            // the first).
            var evtA = new GameStateEvent
            {
                ut = 1000.0,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod",
                detail = "cost=600"
            };
            var evtB = new GameStateEvent
            {
                ut = 1000.02,
                eventType = GameStateEventType.PartPurchased,
                key = "solidBooster",
                detail = "cost=200"
            };

            LedgerOrchestrator.OnKscSpending(evtA);
            LedgerOrchestrator.OnKscSpending(evtB);

            int mkCount = 0, boosterCount = 0;
            foreach (var a in Ledger.Actions)
            {
                if (a.Type != GameActionType.FundsSpending) continue;
                if (a.DedupKey == "mk1pod") mkCount++;
                else if (a.DedupKey == "solidBooster") boosterCount++;
            }

            Assert.Equal(1, mkCount);
            Assert.Equal(1, boosterCount);
        }

        [Fact]
        public void OnKscSpending_CrewHired_AddsKerbalHireActionWithCost()
        {
            // #416: CrewHired events must flow through as KerbalHire actions carrying
            // the stored cost. Before the fix, GameStateRecorder subscribed to
            // GameEvents.onKerbalAdded, which fired for every applicant pool generation
            // and the four starter kerbals; each got recorded as a paid hire and the
            // KerbalHire debits drained the starting funds on every new career.
            var evt = new GameStateEvent
            {
                ut = 700.0,
                eventType = GameStateEventType.CrewHired,
                key = "Jebediah Kerman",
                detail = "trait=Pilot;cost=62113"
            };

            LedgerOrchestrator.OnKscSpending(evt);

            var match = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.KerbalHire && a.KerbalName == "Jebediah Kerman");
            Assert.NotNull(match);
            Assert.Equal(62113f, match.HireCost);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("KSC spending recorded") && l.Contains("KerbalHire"));
        }

        [Fact]
        public void OnKscSpending_CrewHired_ZeroCost_LandsAsZeroCostAction()
        {
            // #416 defensive guard: if a CrewHired event arrives with cost=0 (e.g.
            // ComputeHireCost returned 0 because GameVariables wasn't ready yet), it
            // still lands as a KerbalHire, but with zero fund impact. This keeps the
            // action timeline complete while ensuring starting funds aren't wiped.
            var evt = new GameStateEvent
            {
                ut = 800.0,
                eventType = GameStateEventType.CrewHired,
                key = "Valentina Kerman",
                detail = "trait=Pilot;cost=0"
            };

            LedgerOrchestrator.OnKscSpending(evt);

            var match = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.KerbalHire && a.KerbalName == "Valentina Kerman");
            Assert.NotNull(match);
            Assert.Equal(0f, match.HireCost);
        }

        [Fact]
        public void ComputeHireCost_NullGameVariables_ReturnsZero()
        {
            // #416: defensive path — ComputeHireCost must not NRE when called outside of
            // a live KSP runtime (tests, early scene load before GameVariables.Instance
            // is set). Returning 0 keeps the ledger action in place but produces no
            // fund impact, which is safer than dropping the event.
            Assert.Null(GameVariables.Instance);
            float cost = GameStateRecorder.ComputeHireCost(activeCrewCount: 5);
            Assert.Equal(0f, cost);
        }

        [Fact]
        public void OnKscSpending_DroppedEventType_LogsNoAction()
        {
            // FundsChanged is intentionally dropped by the converter (see §I reconciliation).
            // Guard: OnKscSpending with a dropped event type logs "produced no action"
            // and does NOT add a ledger entry.
            int before = Ledger.Actions.Count;
            var evt = new GameStateEvent
            {
                ut = 600.0,
                eventType = GameStateEventType.FundsChanged,
                key = "",
                valueBefore = 10000,
                valueAfter = 9400
            };

            LedgerOrchestrator.OnKscSpending(evt);

            Assert.Equal(before, Ledger.Actions.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("produced no action"));
        }

        // -------- #444: tracking-station / KSC vessel recovery routing --------

        [Fact]
        public void OnVesselRecoveryFunds_TrackingStationRecoveryBetweenRecordings_AddsFundsEarningTaggedToRecording()
        {
            // Reproduces the smoke-test bundle scenario: recording 1 ends at ut=177,
            // player recovers the vessel from the tracking station at ut=3980 (between
            // recordings — outside any window), KSP fires FundsChanged(VesselRecovery)
            // which the converter drops. Without #444 the +4005 funds were silently
            // lost; the routing path must land a FundsEarning(Recovery) action tagged
            // with the recording's id and amount = the funds delta.
            var rec = new Recording
            {
                RecordingId = "rec-tracking-station-recovery",
                VesselName = "Test Probe",
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Orbiting
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 40000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 177.0, funds = 40000.0 });
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            // KSP emits FundsChanged(VesselRecovery) at the recovery moment, captured
            // by GameStateRecorder.OnFundsChanged into GameStateStore.
            var evt = new GameStateEvent
            {
                ut = 3980.4,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 40000.0,
                valueAfter = 44005.0
            };
            GameStateStore.AddEvent(ref evt);

            int before = Ledger.Actions.Count;
            LedgerOrchestrator.OnVesselRecoveryFunds(3980.4, "Test Probe", fromTrackingStation: true);

            var recovery = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.FundsEarning &&
                a.FundsSource == FundsEarningSource.Recovery &&
                a.RecordingId == "rec-tracking-station-recovery");
            Assert.NotNull(recovery);
            Assert.Equal(4005f, recovery.FundsAwarded);
            Assert.Equal(3980.4, recovery.UT);
            Assert.True(Ledger.Actions.Count > before);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("VesselRecovery funds patched") &&
                l.Contains("Test Probe") &&
                l.Contains("fromTrackingStation=True"));
        }

        [Fact]
        public void OnVesselRecoveryFunds_NoMatchingRecording_AddsActionWithNullRecordingId()
        {
            // Recovery of a vessel that has no Parsek recording (e.g., a stock vessel
            // launched before the player installed Parsek) still credits funds to the
            // ledger so the running balance stays in sync with KSP — just with a null
            // RecordingId tag.
            var evt = new GameStateEvent
            {
                ut = 5000.0,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 100.0,
                valueAfter = 1100.0
            };
            GameStateStore.AddEvent(ref evt);

            LedgerOrchestrator.OnVesselRecoveryFunds(5000.0, "Stock Vessel", fromTrackingStation: false);

            var match = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.FundsEarning &&
                a.FundsSource == FundsEarningSource.Recovery &&
                System.Math.Abs(a.UT - 5000.0) < 0.01);
            Assert.NotNull(match);
            Assert.Null(match.RecordingId);
            Assert.Equal(1000f, match.FundsAwarded);
        }

        [Fact]
        public void OnVesselRecoveryFunds_NoPairedFundsEvent_DefersAndDoesNotAdd()
        {
            // KSP can fire onVesselRecovered before FundsChanged(VesselRecovery) in the
            // post-flight KSC recovery path. The routing path must wait for the paired
            // resource event rather than fabricating a zero earning or logging a false WARN.
            int before = Ledger.Actions.Count;
            LedgerOrchestrator.OnVesselRecoveryFunds(7000.0, "Mystery Probe", fromTrackingStation: true);

            Assert.Equal(before, Ledger.Actions.Count);
            Assert.Equal(1, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("deferred pairing") &&
                l.Contains("Mystery Probe"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("no paired FundsChanged(VesselRecovery) event"));
        }

        [Fact]
        public void OnVesselRecoveryFunds_DebrisRecovery_DoesNotEnqueueOrWarn()
        {
            // #579: debris-only recoveries can arrive without a ledger-worthy paired
            // FundsChanged(VesselRecovery) event. They must not accumulate in the
            // pending recovery-funds queue or later flush as stale rewind entries.
            int before = Ledger.Actions.Count;
            for (int i = 0; i <= LedgerOrchestrator.PendingRecoveryFundsStaleThreshold; i++)
            {
                LedgerOrchestrator.OnVesselRecoveryFunds(
                    1432.4 + i * 0.01,
                    "Kerbal X Debris",
                    fromTrackingStation: true,
                    vesselType: VesselType.Debris);
            }

            LedgerOrchestrator.FlushStalePendingRecoveryFunds("rewind end");

            Assert.Equal(before, Ledger.Actions.Count);
            Assert.Equal(0, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("Kerbal X Debris") &&
                l.Contains("(VesselType.Debris)") &&
                l.Contains("skipping deferred recovery-funds pairing"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("pending queue exceeded threshold"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("FlushStalePendingRecoveryFunds"));
        }

        [Fact]
        public void OnVesselRecoveryFunds_DebrisWithPairedFundsEvent_AddsRecoveryAction()
        {
            // Stock recovery processing runs before onVesselRecovered. If a debris
            // recovery really did produce funds, keep pairing it; only the missing-pair
            // debris path is filtered from the deferred queue.
            var evt = new GameStateEvent
            {
                ut = 2100.0,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 1000.0,
                valueAfter = 1250.0
            };
            GameStateStore.AddEvent(ref evt);

            int before = Ledger.Actions.Count;
            LedgerOrchestrator.OnVesselRecoveryFunds(
                2100.0,
                "Kerbal X Debris",
                fromTrackingStation: true,
                vesselType: VesselType.Debris);

            Assert.Equal(0, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            Assert.True(Ledger.Actions.Count > before);
            var recovery = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.FundsEarning &&
                a.FundsSource == FundsEarningSource.Recovery &&
                System.Math.Abs(a.UT - 2100.0) < 0.01);
            Assert.NotNull(recovery);
            Assert.Equal(250f, recovery.FundsAwarded);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("skipping deferred recovery-funds pairing"));
        }

        [Fact]
        public void OnVesselRecoveryFunds_DebrisPositivePayoutContextWithoutEvent_DefersAndFlushWarns()
        {
            var identity = RecoveredVesselIdentity.FromRawName("Funded Debris");
            var payoutContext = MakeRecoveryPayoutContext(
                2200.0,
                identity,
                VesselType.Debris,
                fundsEarned: GameStateRecorder.FundsThreshold + 75.0);

            LedgerOrchestrator.OnVesselRecoveryFunds(
                2200.0,
                identity,
                fromTrackingStation: true,
                vesselType: VesselType.Debris,
                payoutContext: payoutContext);

            Assert.Equal(1, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);

            LedgerOrchestrator.FlushStalePendingRecoveryFunds("rewind end");

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("FlushStalePendingRecoveryFunds") &&
                l.Contains("Funded Debris") &&
                l.Contains("vesselType=Debris") &&
                l.Contains("expectedFunds=175.0"));
        }

        [Fact]
        public void OnVesselRecoveryFunds_SubThresholdPayoutContext_DoesNotEnqueueOrWarn()
        {
            var identity = RecoveredVesselIdentity.FromNames("#autoLOC_501224", "Jumping Flea");
            var payoutContext = MakeRecoveryPayoutContext(
                288.7,
                identity,
                VesselType.Ship,
                fundsEarned: GameStateRecorder.FundsThreshold - 1.0);

            int before = Ledger.Actions.Count;
            LedgerOrchestrator.OnVesselRecoveryFunds(
                288.7,
                identity,
                fromTrackingStation: true,
                vesselType: VesselType.Ship,
                payoutContext: payoutContext);
            LedgerOrchestrator.FlushStalePendingRecoveryFunds("rewind end");

            Assert.Equal(before, Ledger.Actions.Count);
            Assert.Equal(0, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("Jumping Flea") &&
                l.Contains("below recorder threshold"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("FlushStalePendingRecoveryFunds"));
        }

        [Fact]
        public void OnVesselRecoveryFunds_SubThresholdContext_DoesNotConsumeNeighborStampedEvent()
        {
            var goodIdentity = RecoveredVesselIdentity.FromRawName("Good Recovery");
            var goodContext = MakeRecoveryPayoutContext(288.7, goodIdentity, VesselType.Ship, 500.0);
            var evt = new GameStateEvent
            {
                ut = 288.7,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                detail = RecoveryPayoutContextStore.BuildFundsEventDetail(goodContext),
                valueBefore = 1000.0,
                valueAfter = 1500.0
            };
            GameStateStore.AddEvent(ref evt);

            var tinyIdentity = RecoveredVesselIdentity.FromRawName("Tiny Recovery");
            LedgerOrchestrator.OnVesselRecoveryFunds(
                288.7,
                tinyIdentity,
                fromTrackingStation: true,
                vesselType: VesselType.Ship,
                payoutContext: MakeRecoveryPayoutContext(
                    288.7,
                    tinyIdentity,
                    VesselType.Ship,
                    GameStateRecorder.FundsThreshold - 1.0));
            LedgerOrchestrator.OnVesselRecoveryFunds(
                288.7,
                goodIdentity,
                fromTrackingStation: true,
                vesselType: VesselType.Ship,
                payoutContext: goodContext);

            var recovery = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.FundsEarning &&
                a.FundsSource == FundsEarningSource.Recovery);
            Assert.NotNull(recovery);
            Assert.Equal(500f, recovery.FundsAwarded);
            Assert.Contains(logLines, l =>
                l.Contains("VesselRecovery funds patched") &&
                l.Contains("Good Recovery"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("VesselRecovery funds patched") &&
                l.Contains("Tiny Recovery"));
        }


        [Fact]
        public void OnVesselRecoveryFunds_ZeroPayoutContext_DoesNotEnqueue()
        {
            var identity = RecoveredVesselIdentity.FromRawName("Gerdorf Kerman");
            var payoutContext = MakeRecoveryPayoutContext(
                286.3,
                identity,
                VesselType.EVA,
                fundsEarned: 0.0);

            LedgerOrchestrator.OnVesselRecoveryFunds(
                286.3,
                identity,
                fromTrackingStation: false,
                vesselType: VesselType.EVA,
                payoutContext: payoutContext);

            Assert.Equal(0, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            Assert.Empty(Ledger.Actions.Where(a =>
                a.Type == GameActionType.FundsEarning &&
                a.FundsSource == FundsEarningSource.Recovery));
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("Gerdorf Kerman") &&
                l.Contains("stock expected zero recovery funds"));
        }

        [Fact]
        public void OnVesselRecoveryFunds_PositivePayoutContextWithoutEvent_DefersAndFlushWarns()
        {
            var identity = RecoveredVesselIdentity.FromRawName("MissingFundsProbe");
            var payoutContext = MakeRecoveryPayoutContext(
                6110.0,
                identity,
                VesselType.Ship,
                fundsEarned: GameStateRecorder.FundsThreshold + 50.0);

            LedgerOrchestrator.OnVesselRecoveryFunds(
                6110.0,
                identity,
                fromTrackingStation: true,
                vesselType: VesselType.Ship,
                payoutContext: payoutContext);
            Assert.Equal(1, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);

            LedgerOrchestrator.FlushStalePendingRecoveryFunds("rewind end");

            Assert.Equal(0, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("FlushStalePendingRecoveryFunds") &&
                l.Contains("MissingFundsProbe") &&
                l.Contains("vesselType=Ship") &&
                l.Contains("expectedFunds=150.0"));
        }

        [Fact]
        public void BuildVesselRecoveryFundsDetail_StampsProcessingContextIdentity()
        {
            var identity = RecoveredVesselIdentity.FromNames("#autoLOC_501224", "Jumping Flea");
            var context = MakeRecoveryPayoutContext(288.7, identity, VesselType.Ship, 250.0);

            string detail = RecoveryPayoutContextStore.BuildFundsEventDetail(context);
            RecoveredVesselIdentity parsed =
                RecoveryPayoutContextStore.ExtractIdentityFromFundsEventDetail(detail);

            Assert.NotNull(detail);
            Assert.Contains("vessel=", detail);
            Assert.Contains("rawVessel=", detail);
            Assert.True(parsed.Matches(identity));
            Assert.True(parsed.MatchesName("#autoLOC_501224"));
            Assert.True(parsed.MatchesName("Jumping Flea"));

            RecoveryPayoutContextStore.Remember(
                persistentId: 501225,
                rawVesselName: "Stamped Probe",
                vesselType: VesselType.Ship,
                ut: 500.0,
                recoveryFactor: 1.0f,
                hasFundsEarned: true,
                fundsEarned: 250.0,
                beforeMissionFunds: 1000.0,
                totalFunds: 1250.0);
            Assert.Contains("Stamped%20Probe", GameStateRecorder.BuildVesselRecoveryFundsDetail(500.0));
        }

        [Fact]
        public void RecoveryPayoutContext_TryFind_KeepsContextForDelayedFundsDetail()
        {
            RecoveryPayoutContextStore.Remember(
                persistentId: 501226,
                rawVesselName: "Delayed Funds Probe",
                vesselType: VesselType.Ship,
                ut: 600.0,
                recoveryFactor: 1.0f,
                hasFundsEarned: true,
                fundsEarned: 250.0,
                beforeMissionFunds: 1000.0,
                totalFunds: 1250.0);

            Assert.True(RecoveryPayoutContextStore.TryFind(
                persistentId: 501226,
                identity: RecoveredVesselIdentity.FromRawName("Delayed Funds Probe"),
                ut: 600.0,
                out RecoveryPayoutContext context));
            Assert.NotNull(context);

            string detail = GameStateRecorder.BuildVesselRecoveryFundsDetail(600.0);
            Assert.Contains("Delayed%20Funds%20Probe", detail);
        }

        [Fact]
        public void RecoveryPayoutContext_AllZeroFundsSnapshot_TreatedAsUnknown()
        {
            var identity = RecoveredVesselIdentity.FromRawName("Uninitialized Funds Probe");
            RecoveryPayoutContextStore.Remember(
                persistentId: 501230,
                rawVesselName: "Uninitialized Funds Probe",
                vesselType: VesselType.Ship,
                ut: 605.0,
                recoveryFactor: 1.0f,
                hasFundsEarned: true,
                fundsEarned: 0.0,
                beforeMissionFunds: 0.0,
                totalFunds: 0.0);

            Assert.True(RecoveryPayoutContextStore.TryFind(
                persistentId: 501230,
                identity: identity,
                ut: 605.0,
                out RecoveryPayoutContext context));
            Assert.False(context.HasFundsEarned);

            LedgerOrchestrator.OnVesselRecoveryFunds(
                605.0,
                identity,
                fromTrackingStation: true,
                vesselType: VesselType.Ship,
                payoutContext: context);

            Assert.Equal(1, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("deferred pairing") &&
                l.Contains("expectedFunds=(unknown)"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("stock expected zero recovery funds"));
        }

        [Fact]
        public void RecoveryPayoutContext_Clear_LogsDroppedContextCount()
        {
            var identity = RecoveredVesselIdentity.FromRawName("Clear Probe");
            RecoveryPayoutContextStore.Remember(
                persistentId: 501231,
                rawVesselName: "Clear Probe",
                vesselType: VesselType.Ship,
                ut: 610.0,
                recoveryFactor: 1.0f,
                hasFundsEarned: true,
                fundsEarned: 250.0,
                beforeMissionFunds: 1000.0,
                totalFunds: 1250.0);

            RecoveryPayoutContextStore.Clear("rewind end");

            Assert.False(RecoveryPayoutContextStore.TryFind(
                persistentId: 501231,
                identity: identity,
                ut: 610.0,
                out _));
            Assert.Contains(logLines, l =>
                l.Contains("[RecoveryPayoutContext]") &&
                l.Contains("Clear (rewind end)") &&
                l.Contains("dropping 1 recovery payout context"));
        }

        [Fact]
        public void BuildVesselRecoveryFundsDetail_SameUtContexts_PrefersMatchingFundsEarned()
        {
            RecoveryPayoutContextStore.Remember(
                persistentId: 501227,
                rawVesselName: "Small Recovery",
                vesselType: VesselType.Ship,
                ut: 700.0,
                recoveryFactor: 1.0f,
                hasFundsEarned: true,
                fundsEarned: 150.0,
                beforeMissionFunds: 1000.0,
                totalFunds: 1150.0);
            RecoveryPayoutContextStore.Remember(
                persistentId: 501228,
                rawVesselName: "Large Recovery",
                vesselType: VesselType.Ship,
                ut: 700.0,
                recoveryFactor: 1.0f,
                hasFundsEarned: true,
                fundsEarned: 500.0,
                beforeMissionFunds: 1150.0,
                totalFunds: 1650.0);

            string detail = GameStateRecorder.BuildVesselRecoveryFundsDetail(700.0, 150.0);

            Assert.Contains("Small%20Recovery", detail);
            Assert.DoesNotContain("Large%20Recovery", detail);
        }

        [Fact]
        public void BuildVesselRecoveryFundsDetail_UsedContext_DoesNotStampLaterEvent()
        {
            RecoveryPayoutContextStore.Remember(
                persistentId: 501229,
                rawVesselName: "Single Recovery",
                vesselType: VesselType.Ship,
                ut: 800.0,
                recoveryFactor: 1.0f,
                hasFundsEarned: true,
                fundsEarned: 200.0,
                beforeMissionFunds: 1000.0,
                totalFunds: 1200.0);

            string first = GameStateRecorder.BuildVesselRecoveryFundsDetail(800.0, 200.0);
            string second = GameStateRecorder.BuildVesselRecoveryFundsDetail(800.0, 999.0);

            Assert.Contains("Single%20Recovery", first);
            Assert.Null(second);
        }

        [Fact]
        public void RecoveryPayoutContext_TryFind_PrefersPersistentIdOverSameNameCloserUt()
        {
            var identity = RecoveredVesselIdentity.FromRawName("Same Name");
            RecoveryPayoutContextStore.Remember(
                persistentId: 101,
                rawVesselName: "Same Name",
                vesselType: VesselType.Ship,
                ut: 900.00,
                recoveryFactor: 1.0f,
                hasFundsEarned: true,
                fundsEarned: 0.0,
                beforeMissionFunds: 1000.0,
                totalFunds: 1000.0);
            RecoveryPayoutContextStore.Remember(
                persistentId: 202,
                rawVesselName: "Same Name",
                vesselType: VesselType.Ship,
                ut: 900.05,
                recoveryFactor: 1.0f,
                hasFundsEarned: true,
                fundsEarned: 500.0,
                beforeMissionFunds: 1000.0,
                totalFunds: 1500.0);

            Assert.True(RecoveryPayoutContextStore.TryFind(
                persistentId: 202,
                identity: identity,
                ut: 900.00,
                out RecoveryPayoutContext context));

            Assert.Equal((uint)202, context.PersistentId);
            Assert.Equal(500.0, context.FundsEarned);
        }

        [Fact]
        public void OnVesselRecoveryFunds_CallbackBeforeFundsEvent_PairsWhenEventIsRecorded()
        {
            LedgerOrchestrator.OnVesselRecoveryFunds(149.53, "r0", fromTrackingStation: false);
            Assert.Equal(1, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);

            var evt = new GameStateEvent
            {
                ut = 149.53,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 38990.0,
                valueAfter = 42795.0
            };
            GameStateStore.AddEvent(ref evt);
            LedgerOrchestrator.OnRecoveryFundsEventRecorded(evt);

            Assert.Equal(0, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            var match = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.FundsEarning &&
                a.FundsSource == FundsEarningSource.Recovery &&
                System.Math.Abs(a.UT - 149.53) < 0.01);
            Assert.NotNull(match);
            Assert.Equal(3805f, match.FundsAwarded);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("VesselRecovery funds patched") &&
                l.Contains("amount=3805"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("no paired FundsChanged(VesselRecovery) event"));
        }

        [Fact]
        public void OnVesselRecoveryFunds_DeferredSameNameWithinEpsilon_PairToDistinctEvents()
        {
            LedgerOrchestrator.OnVesselRecoveryFunds(3000.00, "Debris", fromTrackingStation: true);
            LedgerOrchestrator.OnVesselRecoveryFunds(3000.04, "Debris", fromTrackingStation: true);
            Assert.Equal(2, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);

            var evtA = new GameStateEvent
            {
                ut = 3000.00,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 1000.0,
                valueAfter = 1100.0
            };
            GameStateStore.AddEvent(ref evtA);
            LedgerOrchestrator.OnRecoveryFundsEventRecorded(evtA);
            Assert.Equal(1, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);

            var evtB = new GameStateEvent
            {
                ut = 3000.04,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 1100.0,
                valueAfter = 1800.0
            };
            GameStateStore.AddEvent(ref evtB);
            LedgerOrchestrator.OnRecoveryFundsEventRecorded(evtB);

            Assert.Equal(0, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            var recoveries = Ledger.Actions
                .Where(a => a.Type == GameActionType.FundsEarning && a.FundsSource == FundsEarningSource.Recovery)
                .OrderBy(a => a.UT)
                .ToList();

            Assert.Equal(2, recoveries.Count);
            Assert.Equal(100f, recoveries[0].FundsAwarded);
            Assert.Equal(700f, recoveries[1].FundsAwarded);
            Assert.Equal(800f, recoveries.Sum(r => r.FundsAwarded));
        }

        [Fact]
        public void OnVesselRecoveryFunds_EmptyVesselName_SkipsSilently()
        {
            // Defensive guard: caller (ParsekScenario) already filters empty names but
            // the entry point also handles it — no ledger entry, verbose-only log.
            int before = Ledger.Actions.Count;
            LedgerOrchestrator.OnVesselRecoveryFunds(8000.0, "", fromTrackingStation: false);
            LedgerOrchestrator.OnVesselRecoveryFunds(8000.0, null, fromTrackingStation: false);

            Assert.Equal(before, Ledger.Actions.Count);
        }

        [Fact]
        public void OnVesselRecoveryFunds_MultipleRecordingsSameName_TagsLatestByEndUt()
        {
            // Two committed recordings with the same vessel name (revert + re-fly): the
            // routing must pick the one with the larger EndUT — that's the most recently
            // flown one and the one the recovery payout belongs to.
            var oldRec = new Recording
            {
                RecordingId = "rec-old",
                VesselName = "ReusableProbe",
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Destroyed
            };
            oldRec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 40000.0 });
            oldRec.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 40000.0 });
            RecordingStore.AddRecordingWithTreeForTesting(oldRec);

            var newRec = new Recording
            {
                RecordingId = "rec-new",
                VesselName = "ReusableProbe",
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Orbiting
            };
            newRec.Points.Add(new TrajectoryPoint { ut = 1000.0, funds = 38000.0 });
            newRec.Points.Add(new TrajectoryPoint { ut = 1500.0, funds = 38000.0 });
            RecordingStore.AddRecordingWithTreeForTesting(newRec);

            var evt = new GameStateEvent
            {
                ut = 4000.0,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 38000.0,
                valueAfter = 41000.0
            };
            GameStateStore.AddEvent(ref evt);

            LedgerOrchestrator.OnVesselRecoveryFunds(4000.0, "ReusableProbe", fromTrackingStation: true);

            var match = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.FundsEarning &&
                a.FundsSource == FundsEarningSource.Recovery &&
                System.Math.Abs(a.UT - 4000.0) < 0.01);
            Assert.NotNull(match);
            Assert.Equal("rec-new", match.RecordingId);
            Assert.Equal(3000f, match.FundsAwarded);
        }

        [Fact]
        public void OnVesselRecoveryFunds_GhostOnlyRecordingMatch_NotTagged()
        {
            // Ghost-only (Gloops) recordings have zero career footprint per #432 — they
            // must NOT be the recordingId tag for a real recovery payout. The lookup
            // skips them, falling back to null when no real recording matches.
            var ghost = new Recording
            {
                RecordingId = "rec-ghost",
                VesselName = "GloopsClone",
                IsGhostOnly = true
            };
            ghost.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 0.0 });
            ghost.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 0.0 });
            RecordingStore.AddRecordingWithTreeForTesting(ghost);

            var evt = new GameStateEvent
            {
                ut = 3000.0,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 100.0,
                valueAfter = 600.0
            };
            GameStateStore.AddEvent(ref evt);

            LedgerOrchestrator.OnVesselRecoveryFunds(3000.0, "GloopsClone", fromTrackingStation: true);

            var match = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.FundsEarning &&
                a.FundsSource == FundsEarningSource.Recovery &&
                System.Math.Abs(a.UT - 3000.0) < 0.01);
            Assert.NotNull(match);
            Assert.Null(match.RecordingId);
            Assert.Equal(500f, match.FundsAwarded);
        }

        // -------- #444 review round 1 follow-ups --------

        [Fact]
        public void OnVesselRecoveryFunds_BackToBackRecoveriesWithinEpsilon_PairToDistinctEvents()
        {
            // Review items 1 + 2 together: back-to-back recoveries inside the 0.1 s resource
            // coalesce window must still produce two distinct recovery earnings. The fix has
            // two parts: GameStateStore stops coalescing FundsChanged(VesselRecovery), and
            // OnVesselRecoveryFunds consumes a stable per-event fingerprint so each callback
            // pairs to exactly one funds delta.
            var evtA = new GameStateEvent
            {
                ut = 3000.00,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 0.0,
                valueAfter = 100.0   // A = +100
            };
            GameStateStore.AddEvent(ref evtA);
            var evtB = new GameStateEvent
            {
                ut = 3000.04,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 100.0,
                valueAfter = 800.0   // B = +700
            };
            GameStateStore.AddEvent(ref evtB);

            LedgerOrchestrator.OnVesselRecoveryFunds(3000.04, "DebrisB", fromTrackingStation: true);
            LedgerOrchestrator.OnVesselRecoveryFunds(3000.00, "DebrisA", fromTrackingStation: true);

            var recoveries = Ledger.Actions
                .Where(a => a.Type == GameActionType.FundsEarning && a.FundsSource == FundsEarningSource.Recovery)
                .OrderBy(a => a.UT)
                .ToList();

            Assert.Equal(2, recoveries.Count);
            Assert.Equal(100f, recoveries[0].FundsAwarded);
            Assert.Equal(700f, recoveries[1].FundsAwarded);
            Assert.Equal(800f, recoveries.Sum(r => r.FundsAwarded));
            Assert.Equal(3000.00, recoveries[0].UT, 2);
            Assert.Equal(3000.04, recoveries[1].UT, 2);
        }

        [Fact]
        public void OnVesselRecoveryFunds_BracketingRecordingPreferredOverLatestByEndUt()
        {
            // Review item 2: spec text says RecordingId = nearest-recording-by-name, but the
            // first commit picked "max EndUT for matching name" which diverges for a long
            // mission whose EndUT extends past the recovery UT. The follow-up picker prefers
            // the recording whose [StartUT, EndUT] brackets the recovery UT.
            //
            // recA — does NOT bracket: window=[4000, 5000], later than the recovery UT.
            // recB — DOES bracket: window=[3200, 3800], contains the recovery UT 3500.
            // Old "max EndUT" picks recA (5000 > 3800). New picker picks recB (brackets).
            var recA = new Recording
            {
                RecordingId = "rec-A-long-mission",
                VesselName = "Twinned",
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Orbiting
            };
            recA.Points.Add(new TrajectoryPoint { ut = 4000.0, funds = 40000.0 });
            recA.Points.Add(new TrajectoryPoint { ut = 5000.0, funds = 40000.0 });
            RecordingStore.AddRecordingWithTreeForTesting(recA);

            var recB = new Recording
            {
                RecordingId = "rec-B-bracketing",
                VesselName = "Twinned",
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Orbiting
            };
            recB.Points.Add(new TrajectoryPoint { ut = 3200.0, funds = 38000.0 });
            recB.Points.Add(new TrajectoryPoint { ut = 3800.0, funds = 38000.0 });
            RecordingStore.AddRecordingWithTreeForTesting(recB);

            var evt = new GameStateEvent
            {
                ut = 3500.0,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 38000.0,
                valueAfter = 39500.0
            };
            GameStateStore.AddEvent(ref evt);

            LedgerOrchestrator.OnVesselRecoveryFunds(3500.0, "Twinned", fromTrackingStation: true);

            var match = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.FundsEarning &&
                a.FundsSource == FundsEarningSource.Recovery &&
                System.Math.Abs(a.UT - 3500.0) < 0.01);
            Assert.NotNull(match);
            Assert.Equal("rec-B-bracketing", match.RecordingId);
        }

        [Fact]
        public void PickRecoveryRecordingId_FallsBackToMostRecentEndedBeforeUt()
        {
            // Review item 2: when no recording brackets ut, the picker must prefer the
            // most-recent flight that ended at or before ut (semantic: the flight this
            // recovery belongs to is one that has already finished). Only fall back to the
            // global latest by EndUT when nothing has ended yet at ut (e.g., metadata drift).
            var early = new Recording
            {
                RecordingId = "rec-early",
                VesselName = "Reusable",
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Destroyed
            };
            early.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 40000.0 });
            early.Points.Add(new TrajectoryPoint { ut = 500.0, funds = 40000.0 });
            RecordingStore.AddRecordingWithTreeForTesting(early);

            var mid = new Recording
            {
                RecordingId = "rec-mid",
                VesselName = "Reusable",
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Destroyed
            };
            mid.Points.Add(new TrajectoryPoint { ut = 600.0, funds = 38000.0 });
            mid.Points.Add(new TrajectoryPoint { ut = 1500.0, funds = 38000.0 });
            RecordingStore.AddRecordingWithTreeForTesting(mid);

            // A recording that hasn't ended yet at the recovery UT (start=3000, end=5000;
            // recovery at ut=2000). It does not bracket and has not ended — should NOT win.
            var future = new Recording
            {
                RecordingId = "rec-future",
                VesselName = "Reusable",
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Orbiting
            };
            future.Points.Add(new TrajectoryPoint { ut = 3000.0, funds = 36000.0 });
            future.Points.Add(new TrajectoryPoint { ut = 5000.0, funds = 36000.0 });
            RecordingStore.AddRecordingWithTreeForTesting(future);

            string pick = LedgerOrchestrator.PickRecoveryRecordingId("Reusable", 2000.0);
            // Tier 1 empty (no recording brackets 2000), tier 2 = max EndUT with EndUT<=2000
            // → rec-mid (1500). rec-future has EndUT=5000 but EndUT>ut so it's tier 3 only.
            Assert.Equal("rec-mid", pick);
        }

        [Fact]
        public void OnVesselRecoveryFunds_SameUtDifferentReasonKey_PicksVesselRecoveryEvent()
        {
            // Review item 3a: the gate filters on key == VesselRecoveryReasonKey. If two
            // FundsChanged events fall within the pairing epsilon but carry different
            // reason keys (e.g., the player accepts a strategy that changes funds in the
            // same window a vessel is recovered), the routing must pick the VesselRecovery
            // one — not just "the most recent FundsChanged regardless of reason".
            //
            // GameStateStore still coalesces non-recovery resource events that share the
            // same eventType AND recordingId tag within 0.1 s. Use different tags so the
            // unrelated Strategies delta stays distinct from the recovery delta.
            var recoveryEvt = new GameStateEvent
            {
                ut = 7000.00,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                recordingId = "tag-recovery",
                valueBefore = 10000.0,
                valueAfter = 12000.0   // recovery: +2000
            };
            GameStateStore.AddEvent(ref recoveryEvt);
            var strategyEvt = new GameStateEvent
            {
                ut = 7000.05,   // within pairing epsilon AND within coalesce epsilon, but distinct tag
                eventType = GameStateEventType.FundsChanged,
                key = "Strategies",
                recordingId = "tag-strategy",
                valueBefore = 12000.0,
                valueAfter = 11500.0   // strategy fee: -500, more recent in store, different key
            };
            GameStateStore.AddEvent(ref strategyEvt);

            // Both events sit within VesselRecoveryEventEpsilonSeconds of ut=7000.05.
            // Reverse-search hits the Strategies event first; the key gate must reject it
            // and continue back to the VesselRecovery event.
            LedgerOrchestrator.OnVesselRecoveryFunds(7000.05, "RecoveredProbe", fromTrackingStation: true);

            var match = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.FundsEarning &&
                a.FundsSource == FundsEarningSource.Recovery);
            Assert.NotNull(match);
            // Must be the VesselRecovery delta (+2000), not the Strategies one (-500).
            Assert.Equal(2000f, match.FundsAwarded);
        }

        [Fact]
        public void OnVesselRecoveryFunds_PreSeededMatchingRecoveryAction_DedupsByEventFingerprint()
        {
            // Review item 3b: OnVesselRecoveryFunds must dedup against a recovery action that
            // was already emitted from the same FundsChanged(VesselRecovery) event, even if
            // the caller accidentally bypasses the scene gate in ParsekScenario.
            var recoveryEvent = new GameStateEvent
            {
                ut = 9000.0,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 10000.0,
                valueAfter = 11500.0
            };
            string dedupKey = LedgerOrchestrator.BuildRecoveryEventDedupKey(recoveryEvent);

            var preSeeded = new GameAction
            {
                UT = 9000.0,
                Type = GameActionType.FundsEarning,
                RecordingId = "rec-in-flight",
                FundsAwarded = 1500f,
                FundsSource = FundsEarningSource.Recovery,
                DedupKey = dedupKey,
                Sequence = 1
            };
            Ledger.AddAction(preSeeded);

            GameStateStore.AddEvent(ref recoveryEvent);

            int before = Ledger.Actions.Count(a =>
                a.Type == GameActionType.FundsEarning &&
                a.FundsSource == FundsEarningSource.Recovery);

            LedgerOrchestrator.OnVesselRecoveryFunds(9000.0, "InFlightRecovery", fromTrackingStation: false);

            int after = Ledger.Actions.Count(a =>
                a.Type == GameActionType.FundsEarning &&
                a.FundsSource == FundsEarningSource.Recovery);

            Assert.Equal(before, after);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("already exists in ledger"));
        }

        [Fact]
        public void OnKspLoad_LegacyRecoveryActionMissingDedupKey_IsRepairedAndReplayStillDedups()
        {
            var recoveryEvent = new GameStateEvent
            {
                ut = 9100.0,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 5000.0,
                valueAfter = 6500.0
            };
            string expectedDedupKey = LedgerOrchestrator.BuildRecoveryEventDedupKey(recoveryEvent);

            Ledger.AddAction(new GameAction
            {
                UT = 9100.0,
                Type = GameActionType.FundsEarning,
                RecordingId = null,
                FundsAwarded = 1500f,
                FundsSource = FundsEarningSource.Recovery,
                DedupKey = null
            });
            GameStateStore.AddEvent(ref recoveryEvent);

            LedgerOrchestrator.OnKspLoad(new HashSet<string>(), maxUT: 10000.0);

            var repaired = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsEarning &&
                a.FundsSource == FundsEarningSource.Recovery);
            Assert.Equal(expectedDedupKey, repaired.DedupKey);

            int before = Ledger.Actions.Count(a =>
                a.Type == GameActionType.FundsEarning &&
                a.FundsSource == FundsEarningSource.Recovery);

            LedgerOrchestrator.OnVesselRecoveryFunds(9100.0, "LegacyRecovery", fromTrackingStation: true);

            int after = Ledger.Actions.Count(a =>
                a.Type == GameActionType.FundsEarning &&
                a.FundsSource == FundsEarningSource.Recovery);

            Assert.Equal(before, after);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("RepairMissingRecoveryDedupKeys") &&
                l.Contains("repaired=1"));
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("already exists in ledger"));
        }

        [Fact]
        public void OnKspLoad_LegacyRecoveryActionMissingDedupKey_LargeFloatRoundedAmount_IsRepaired()
        {
            const double recoveryAmount = 1234567.89;
            float storedRecoveryAmount = (float)recoveryAmount;
            Assert.True(Math.Abs(recoveryAmount - storedRecoveryAmount) > 0.01);

            var recoveryEvent = new GameStateEvent
            {
                ut = 9200.0,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 5000.0,
                valueAfter = 5000.0 + recoveryAmount
            };
            string expectedDedupKey = LedgerOrchestrator.BuildRecoveryEventDedupKey(recoveryEvent);

            Ledger.AddAction(new GameAction
            {
                UT = 9200.0,
                Type = GameActionType.FundsEarning,
                RecordingId = null,
                FundsAwarded = storedRecoveryAmount,
                FundsSource = FundsEarningSource.Recovery,
                DedupKey = null
            });
            GameStateStore.AddEvent(ref recoveryEvent);

            LedgerOrchestrator.OnKspLoad(new HashSet<string>(), maxUT: 10000.0);

            var repaired = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsEarning &&
                a.FundsSource == FundsEarningSource.Recovery);
            Assert.Equal(expectedDedupKey, repaired.DedupKey);

            int before = Ledger.Actions.Count(a =>
                a.Type == GameActionType.FundsEarning &&
                a.FundsSource == FundsEarningSource.Recovery);

            LedgerOrchestrator.OnVesselRecoveryFunds(9200.0, "LegacyBigRecovery", fromTrackingStation: true);

            int after = Ledger.Actions.Count(a =>
                a.Type == GameActionType.FundsEarning &&
                a.FundsSource == FundsEarningSource.Recovery);

            Assert.Equal(before, after);
        }

        // --- Review follow-up: staleness eviction for pendingRecoveryFunds. Unmatched
        // recovery callbacks that never receive a paired FundsChanged(VesselRecovery)
        // event must be drained at lifecycle boundaries and fire a WARN listing every
        // unclaimed entry so missing-payout bugs stay visible.
        [Fact]
        public void FlushStalePendingRecoveryFunds_EvictsUnclaimedEntriesWithWarn()
        {
            LedgerOrchestrator.OnVesselRecoveryFunds(1000.0, "LeakyProbe", fromTrackingStation: false);
            LedgerOrchestrator.OnVesselRecoveryFunds(1050.0, "LeakyRover", fromTrackingStation: true);
            Assert.Equal(2, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);

            LedgerOrchestrator.FlushStalePendingRecoveryFunds("scene switch");

            Assert.Equal(0, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("FlushStalePendingRecoveryFunds") &&
                l.Contains("scene switch") &&
                l.Contains("evicting 2") &&
                l.Contains("LeakyProbe") &&
                l.Contains("LeakyRover"));
        }

        [Fact]
        public void FlushStalePendingRecoveryFunds_EmptyQueue_IsSilentNoOp()
        {
            // A scene switch with no pending requests should NOT emit the WARN.
            Assert.Equal(0, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            LedgerOrchestrator.FlushStalePendingRecoveryFunds("scene switch");
            Assert.DoesNotContain(logLines, l =>
                l.Contains("FlushStalePendingRecoveryFunds"));
        }

        [Fact]
        public void OnVesselRecoveryFunds_QueueOverThreshold_EmitsWarn()
        {
            // Safety-net trip when lifecycle boundaries miss: queue past the threshold
            // should emit a WARN identifying the latest deferred request.
            for (int i = 0; i < LedgerOrchestrator.PendingRecoveryFundsStaleThreshold; i++)
            {
                LedgerOrchestrator.OnVesselRecoveryFunds(
                    2000.0 + i * 0.001, "DebrisBatch" + i, fromTrackingStation: true);
            }

            // At threshold, no warn yet.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("pending queue exceeded threshold"));

            LedgerOrchestrator.OnVesselRecoveryFunds(
                2000.5, "LatestDebris", fromTrackingStation: true);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("pending queue exceeded threshold") &&
                l.Contains("LatestDebris"));
        }

        // --- Review follow-up: tighter recovery-fund pairing. When two pending
        // requests sit at the same UT with different vessel names, a FundsChanged
        // event carrying a vessel name (via evt.detail) must prefer the name match
        // instead of just picking nearest-UT.
        [Fact]
        public void OnRecoveryFundsEventRecorded_VesselNameMatch_PreferredOverNearestUT()
        {
            LedgerOrchestrator.OnVesselRecoveryFunds(3000.0, "Rocket A", fromTrackingStation: false);
            LedgerOrchestrator.OnVesselRecoveryFunds(3000.0, "Rocket B", fromTrackingStation: false);
            Assert.Equal(2, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);

            // Event tagged with "Rocket A" in detail should pair the A pending
            // request even though both sit at identical UT.
            var evt = new GameStateEvent
            {
                ut = 3000.0,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                detail = "Rocket A",
                valueBefore = 100.0,
                valueAfter = 1100.0
            };
            GameStateStore.AddEvent(ref evt);
            LedgerOrchestrator.OnRecoveryFundsEventRecorded(evt);

            // One request remains (Rocket B).
            Assert.Equal(1, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            // The pairing log identifies the vessel name used for the successful
            // pairing — proof that "Rocket A" (not "Rocket B") won this tier-1 pass.
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("VesselRecovery funds patched") &&
                l.Contains("vessel='Rocket A'"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("VesselRecovery funds patched") &&
                l.Contains("vessel='Rocket B'"));
        }

        [Fact]
        public void OnRecoveryFundsEventRecorded_StructuredRawNormalizedNameMatch_Preferred()
        {
            var identity = RecoveredVesselIdentity.FromNames("#autoLOC_501224", "Jumping Flea");
            LedgerOrchestrator.OnVesselRecoveryFunds(
                3100.0,
                identity,
                fromTrackingStation: true,
                vesselType: VesselType.Ship,
                payoutContext: MakeRecoveryPayoutContext(3100.0, identity, VesselType.Ship, 500.0));
            LedgerOrchestrator.OnVesselRecoveryFunds(
                3100.0,
                RecoveredVesselIdentity.FromRawName("Other Probe"),
                fromTrackingStation: true,
                vesselType: VesselType.Ship,
                payoutContext: MakeRecoveryPayoutContext(
                    3100.0,
                    RecoveredVesselIdentity.FromRawName("Other Probe"),
                    VesselType.Ship,
                    500.0));
            Assert.Equal(2, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);

            var evt = new GameStateEvent
            {
                ut = 3100.0,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                detail = RecoveryPayoutContextStore.BuildFundsEventDetail(
                    MakeRecoveryPayoutContext(3100.0, identity, VesselType.Ship, 500.0)),
                valueBefore = 1000.0,
                valueAfter = 1500.0
            };
            GameStateStore.AddEvent(ref evt);
            LedgerOrchestrator.OnRecoveryFundsEventRecorded(evt);

            Assert.Equal(1, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("VesselRecovery funds patched") &&
                l.Contains("vessel='Jumping Flea'"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("VesselRecovery funds patched") &&
                l.Contains("Other Probe"));
        }

        [Fact]
        public void OnRecoveryFundsEventRecorded_VesselNameMatchTie_LogsWarn()
        {
            // Two same-named same-UT pending requests: after name-match filtering they
            // still tie on distance=0. Pairing still succeeds (first in list order) but
            // the tie must be logged so the ambiguity is visible.
            LedgerOrchestrator.OnVesselRecoveryFunds(4000.0, "Twin Probe", fromTrackingStation: false);
            LedgerOrchestrator.OnVesselRecoveryFunds(4000.0, "Twin Probe", fromTrackingStation: false);

            var evt = new GameStateEvent
            {
                ut = 4000.0,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                detail = "Twin Probe",
                valueBefore = 200.0,
                valueAfter = 700.0
            };
            GameStateStore.AddEvent(ref evt);
            LedgerOrchestrator.OnRecoveryFundsEventRecorded(evt);

            Assert.Equal(1, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("multiple pending requests tied") &&
                l.Contains("name-match") &&
                l.Contains("Twin Probe"));
        }

        [Fact]
        public void OnRecoveryFundsEventRecorded_NoNameMatchFallsBackToNearestUT()
        {
            // Event with no usable name hint: the legacy nearest-UT fallback still
            // pairs the closest one, preserving the #444 contract for older events.
            LedgerOrchestrator.OnVesselRecoveryFunds(5000.0, "Alpha", fromTrackingStation: false);
            LedgerOrchestrator.OnVesselRecoveryFunds(5000.1, "Beta", fromTrackingStation: false);

            var evt = new GameStateEvent
            {
                ut = 5000.0,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 500.0,
                valueAfter = 1500.0
            };
            GameStateStore.AddEvent(ref evt);
            LedgerOrchestrator.OnRecoveryFundsEventRecorded(evt);

            Assert.Equal(1, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            // Alpha (ut=5000.0) is nearest to evt.ut=5000.0 so it wins the fallback.
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("VesselRecovery funds patched") &&
                l.Contains("vessel='Alpha'"));
        }

        [Fact]
        public void OnRecoveryFundsEventRecorded_MismatchedIdentity_DoesNotFallbackToNearestUT()
        {
            LedgerOrchestrator.OnVesselRecoveryFunds(5100.0, "Alpha", fromTrackingStation: false);
            LedgerOrchestrator.OnVesselRecoveryFunds(5100.0, "Beta", fromTrackingStation: false);

            var evt = new GameStateEvent
            {
                ut = 5100.0,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                detail = "Gamma",
                valueBefore = 500.0,
                valueAfter = 1500.0
            };
            GameStateStore.AddEvent(ref evt);
            LedgerOrchestrator.OnRecoveryFundsEventRecorded(evt);

            Assert.Equal(2, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("VesselRecovery funds patched") &&
                (l.Contains("Alpha") || l.Contains("Beta")));
        }

        private static RecoveryPayoutContext MakeRecoveryPayoutContext(
            double ut,
            RecoveredVesselIdentity identity,
            VesselType vesselType,
            double fundsEarned)
        {
            return new RecoveryPayoutContext
            {
                PersistentId = 12345,
                Identity = identity,
                VesselType = vesselType,
                Ut = ut,
                RecoveryFactor = 1.0f,
                HasFundsEarned = true,
                FundsEarned = fundsEarned,
                BeforeMissionFunds = 1000.0,
                TotalFunds = 1000.0 + fundsEarned
            };
        }
    }
}
