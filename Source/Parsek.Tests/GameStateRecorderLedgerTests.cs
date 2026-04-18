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
                detail = "title=Explore Mun;deadline=NaN;failFunds=5000;failRep=10"
            };

            LedgerOrchestrator.OnKscSpending(evt);

            Assert.Contains(Ledger.Actions, a =>
                a.Type == GameActionType.ContractAccept && a.ContractId == "contract-guid-1");
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("KSC spending recorded") && l.Contains("ContractAccept"));
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
        public void OnVesselRecoveryFunds_NoPairedFundsEvent_LogsWarnAndDoesNotAdd()
        {
            // Defensive guard: if onVesselRecovered fires but no FundsChanged(VesselRecovery)
            // event sits within the epsilon window (e.g. corrupt event flow, mod conflict),
            // the routing path WARNs and skips rather than fabricating a zero earning.
            int before = Ledger.Actions.Count;
            LedgerOrchestrator.OnVesselRecoveryFunds(7000.0, "Mystery Probe", fromTrackingStation: true);

            Assert.Equal(before, Ledger.Actions.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("no paired FundsChanged(VesselRecovery) event") &&
                l.Contains("Mystery Probe"));
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
    }
}
