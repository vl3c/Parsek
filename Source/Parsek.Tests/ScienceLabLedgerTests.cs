using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// LAB-SCIENCE-NEVER-LEDGERED: the Mobile Processing Lab pays its stored science out
    /// through a bare <c>AddScience</c> with no subject (decompiled
    /// <c>ModuleScienceLab.OnTransmissionComplete</c> / <c>RecoverScienceLabs</c>), so the
    /// subject channel never saw it and a rewind lost it. It is now captured as a
    /// <see cref="GameStateEventType.ScienceLabCredited"/> event and walked as an uncapped
    /// <see cref="GameActionType.ScienceLabEarning"/> row. The Harmony capture itself needs a
    /// live R&amp;D singleton; these cells pin every seam it feeds.
    /// </summary>
    [Collection("Sequential")]
    public class ScienceLabLedgerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ScienceLabLedgerTests()
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
            GameStateRecorder.TagResolverForTesting = null;
            GameStateRecorder.HasLiveRecorderProviderForTesting = null;
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static GameAction Lab(double ut, float credit, string recordingId = null,
            ScienceMethod method = ScienceMethod.Transmitted)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ScienceLabEarning,
                ScienceAwarded = credit,
                Method = method,
                RecordingId = recordingId
            };
        }

        // ================================================================
        // Event and conversion
        // ================================================================

        [Fact]
        public void BuildScienceLabCreditEvent_CarriesPoolAndReason()
        {
            var evt = GameStateRecorder.BuildScienceLabCreditEvent(
                1234.5, 40f, 52.5f, "ScienceTransmission", "Lab One");

            Assert.Equal(GameStateEventType.ScienceLabCredited, evt.eventType);
            Assert.Equal(24, (int)evt.eventType);
            Assert.Equal("ScienceTransmission", evt.key);
            Assert.Equal(40.0, evt.valueBefore);
            Assert.Equal(52.5, evt.valueAfter);
            Assert.Equal("vessel=Lab One", evt.detail);
            Assert.Equal("Lab science transmitted: +12.5", GameStateEventDisplay.GetDisplayDescription(evt));
            Assert.Equal("Science", GameStateEventDisplay.GetDisplayCategory(evt.eventType));
        }

        [Fact]
        public void TheLabsOwnScienceChanged_StillConvertsToNothing_TheLabEventDoes()
        {
            // The red half: before the fix the lab left only this ScienceChanged behind, and
            // it is dropped on purpose (the subject channel owns science earnings).
            var poolEvent = new GameStateEvent
            {
                ut = 500, eventType = GameStateEventType.ScienceChanged,
                key = "ScienceTransmission", valueBefore = 40, valueAfter = 52.5
            };
            Assert.Null(GameStateEventConverter.ConvertEvent(poolEvent, "rec-1"));

            var row = GameStateEventConverter.ConvertEvent(
                GameStateRecorder.BuildScienceLabCreditEvent(500, 40f, 52.5f, "ScienceTransmission", "Lab"),
                "rec-1");
            Assert.NotNull(row);
            Assert.Equal(GameActionType.ScienceLabEarning, row.Type);
            Assert.Equal(500, row.UT);
            Assert.Equal("rec-1", row.RecordingId);
            Assert.Equal(12.5f, row.ScienceAwarded);
            Assert.Equal(ScienceMethod.Transmitted, row.Method);
        }

        [Fact]
        public void Convert_RecoveryReason_IsRecoveredMethod()
        {
            var row = GameStateEventConverter.ConvertEvent(
                GameStateRecorder.BuildScienceLabCreditEvent(9, 0f, 30f, "VesselRecovery", "Lab"), null);

            Assert.Equal(ScienceMethod.Recovered, row.Method);
            Assert.Equal(30f, row.ScienceAwarded);
        }

        [Fact]
        public void Convert_NonPositiveCredit_IsDropped()
        {
            var evt = GameStateRecorder.BuildScienceLabCreditEvent(9, 30f, 30f, "ScienceTransmission", "Lab");

            Assert.Null(GameStateEventConverter.ConvertEvent(evt, null));
        }

        [Fact]
        public void OnScienceLabCredited_NoRecording_LandsAnUntaggedLedgerRow()
        {
            LedgerOrchestrator.Initialize();
            GameStateRecorder.TagResolverForTesting = () => "";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => false;

            GameStateRecorder.OnScienceLabCredited(700, 10f, 25f, "ScienceTransmission", "Lab One");

            Assert.Contains(GameStateStore.Events, e => e.eventType == GameStateEventType.ScienceLabCredited);
            var row = Ledger.Actions.SingleOrDefault(a => a.Type == GameActionType.ScienceLabEarning);
            Assert.NotNull(row);
            Assert.Equal(15f, row.ScienceAwarded);
            Assert.Null(row.RecordingId);
            Assert.Contains(logLines, l => l.Contains("[GameStateRecorder]")
                && l.Contains("Game state: ScienceLabCredited +15.00")
                && l.Contains("reason='ScienceTransmission'"));
        }

        [Fact]
        public void OnScienceLabCredited_LiveRecording_TagsTheEventAndWritesNoKscRow()
        {
            LedgerOrchestrator.Initialize();
            GameStateRecorder.TagResolverForTesting = () => "rec-live";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;

            GameStateRecorder.OnScienceLabCredited(800, 10f, 25f, "ScienceTransmission", "Lab One");

            var evt = GameStateStore.Events.Single(e => e.eventType == GameStateEventType.ScienceLabCredited);
            Assert.Equal("rec-live", evt.recordingId);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.ScienceLabEarning);
        }

        [Fact]
        public void OnScienceLabCredited_DuringReplay_EmitsNothing()
        {
            GameStateRecorder.IsReplayingActions = true;
            try
            {
                GameStateRecorder.OnScienceLabCredited(100, 0f, 20f, "ScienceTransmission", "Lab");
            }
            finally
            {
                GameStateRecorder.IsReplayingActions = false;
            }

            Assert.DoesNotContain(GameStateStore.Events, e => e.eventType == GameStateEventType.ScienceLabCredited);
        }

        [Theory]
        [InlineData(10f, 25f, 15f, true)]
        // A converter strategy took 10% inside AddScience: the pool moved 13.5 of 15.
        [InlineData(10f, 23.5f, 15f, true)]
        [InlineData(10f, 10f, 15f, false)]
        [InlineData(10f, 9f, 15f, false)]
        [InlineData(10f, 25f, 0f, false)]
        [InlineData(float.NaN, 9f, 15f, false)]
        public void CapturePatch_APoolMoveWithAGrossIsACredit(float before, float after, float gross, bool expected)
        {
            Assert.Equal(expected, Parsek.Patches.ScienceLabCreditCapture.IsCredit(before, after, gross));
        }

        [Fact]
        public void CapturePatch_SumsRecoveredStoredScienceTheWayStockDoes()
        {
            // Stock skips unparseable and non-positive values and sums the rest.
            float sum = Parsek.Patches.ScienceLabCreditCapture.SumPositiveValues(
                new[] { "12.5", "0", "-3", "junk", "2.25" });
            Assert.Equal(14.75, (double)sum, 3);
            Assert.Equal(0f, Parsek.Patches.ScienceLabCreditCapture.SumPositiveValues(null));
        }

        [Fact]
        public void StrategyTake_IsChargedOnce_GrossLabRowPlusItsOwnDebit()
        {
            // Lab transmits 100, Patents Licensing takes 10 inside AddScience. The capture
            // records the gross (event 10 -> 110, not the pool's 100); the take is the
            // strategy's own StrategyScienceDebit. The walk nets +90, what KSP applied.
            var evt = GameStateRecorder.BuildScienceLabCreditEvent(500, 10f, 110f, "ScienceTransmission", "Lab");
            var lab = GameStateEventConverter.ConvertEvent(evt, null);
            Assert.NotNull(lab);
            Assert.Equal(100.0, (double)lab.ScienceAwarded, 3);

            var module = new ScienceModule();
            module.ProcessAction(new GameAction { UT = 0, Type = GameActionType.ScienceInitial, InitialScience = 10f });
            module.ProcessAction(lab);
            module.ProcessAction(new GameAction
            {
                UT = 500, Type = GameActionType.StrategyScienceDebit, Cost = 10f,
            });
            Assert.Equal(100.0, module.GetRunningScience(), 3);
        }

        // ================================================================
        // Persistence
        // ================================================================

        [Fact]
        public void ScienceLabEarning_RoundTrips()
        {
            var original = Lab(42.5, 17.25f, "rec-a", ScienceMethod.Recovered);
            var parent = new ConfigNode("ROOT");
            original.SerializeInto(parent);
            var node = parent.GetNode("GAME_ACTION");
            Assert.Equal("38", node.GetValue("type"));

            var result = GameAction.DeserializeFrom(node);

            Assert.Equal(GameActionType.ScienceLabEarning, result.Type);
            Assert.Equal(42.5, result.UT);
            Assert.Equal("rec-a", result.RecordingId);
            Assert.Equal(17.25f, result.ScienceAwarded);
            Assert.Equal(ScienceMethod.Recovered, result.Method);
        }

        // ================================================================
        // Ledger semantics
        // ================================================================

        [Fact]
        public void ScienceModule_CreditsLabScienceUncapped()
        {
            var module = new ScienceModule();
            module.Reset();
            var first = Lab(100, 50f);
            var second = Lab(200, 50f);

            module.ProcessAction(first);
            module.ProcessAction(second);

            // No subject, so nothing caps a second payout of the same size.
            Assert.Equal(100.0, module.GetRunningScience(), 4);
            Assert.Equal(50f, first.EffectiveScience);
            Assert.Equal(100.0, module.GetAvailableScience(), 4);
            Assert.True(module.TryGetProjectionDelta(second, out double delta));
            Assert.Equal(50.0, delta, 4);
        }

        [Fact]
        public void RewindPastTheTransmission_KeepsTheLabScience()
        {
            LedgerOrchestrator.Initialize();
            var actions = new List<GameAction>
            {
                new GameAction { UT = 0, Type = GameActionType.ScienceInitial, InitialScience = 20f },
                Lab(500, 15f, "rec-lab")
            };

            // Rewound before the transmission: not yet earned.
            RecalculationEngine.Recalculate(actions, 100);
            Assert.Equal(20.0, LedgerOrchestrator.Science.GetRunningScience(), 4);

            // The clock passes it again: the committed lab science is re-applied.
            RecalculationEngine.Recalculate(actions, 600);
            Assert.Equal(35.0, LedgerOrchestrator.Science.GetRunningScience(), 4);
        }

        [Fact]
        public void ScienceLabEarning_IsAnEarning_RetiredBySupersede_NeverBlocks()
        {
            Assert.True(LedgerOrchestrator.IsResourceImpactingAction(GameActionType.ScienceLabEarning));
            Assert.True(RecalculationEngine.IsEarningType(GameActionType.ScienceLabEarning));
            Assert.True(TombstoneEligibility.IsSupersedeTombstoneEligible(Lab(10, 5f, "rec-x")));
            Assert.False(TombstoneEligibility.IsSupersedeTombstoneEligible(Lab(10, 5f)));
            Assert.False(SupersedeCommit.IsWorldStateChangingRecordingAction(
                Lab(10, 5f, "rec-x"), new List<GameAction>()));
        }

        [Fact]
        public void GetActionKey_TwoLabsAtOneUt_AreDistinct()
        {
            Assert.NotEqual(
                LedgerOrchestrator.GetActionKey(Lab(100, 5f)),
                LedgerOrchestrator.GetActionKey(Lab(100, 6f)));
            Assert.NotEqual(
                LedgerOrchestrator.GetActionKey(Lab(100, 5f)),
                LedgerOrchestrator.GetActionKey(Lab(100, 5f, null, ScienceMethod.Recovered)));
        }

        [Fact]
        public void ReconcileEarningsWindow_LabTransmission_NoMissingChannelWarn()
        {
            LedgerOrchestrator.Initialize();
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(true, true, true);
            var events = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 150, eventType = GameStateEventType.ScienceChanged,
                    key = "ScienceTransmission", valueBefore = 40, valueAfter = 52.5,
                    recordingId = "rec-lab"
                }
            };
            // Negative control: with no lab row (the pre-fix state) the reconcile WARNs.
            LedgerOrchestrator.ReconcileEarningsWindow(
                events, new List<GameAction>(), 100, 200, "rec-lab");
            Assert.Contains(logLines, l => l.Contains("Earnings reconciliation (sci)"));
            logLines.Clear();

            var row = Lab(150, 12.5f, "rec-lab");
            row.Effective = true;
            LedgerOrchestrator.ReconcileEarningsWindow(
                events, new List<GameAction> { row }, 100, 200, "rec-lab");

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (sci)"));
        }

        [Fact]
        public void ScienceLabEarning_TimelineText_NoUnknownTypeWarn()
        {
            Assert.Equal("Lab science transmitted +12.5 sci",
                GameActionDisplay.GetDescription(Lab(1, 12.5f), null));
            Assert.Equal("Lab science recovered +3 sci",
                GameActionDisplay.GetDescription(Lab(1, 3f, null, ScienceMethod.Recovered), null));
            Assert.Equal(TimelineEntryType.ScienceEarning,
                TimelineEntryDisplay.MapGameActionType(GameActionType.ScienceLabEarning));
            Assert.DoesNotContain(logLines, l => l.Contains("Unknown GameActionType"));
        }
    

        // ================================================================
        // Orbital survey bonus (the M700's subject-less science)
        // ================================================================

        [Fact]
        public void SurveyCreditEvent_NamesTheBody_AheadOfTheVessel()
        {
            var evt = GameStateRecorder.BuildScienceLabCreditEvent(
                300, 40f, 52f, "ScienceTransmission", "Scan;Sat", "Mun");

            Assert.Equal(GameStateEventType.ScienceLabCredited, evt.eventType);
            Assert.Equal("survey=Mun;vessel=Scan;Sat", evt.detail);
            Assert.Equal("Orbital survey of Mun: +12.0", GameStateEventDisplay.GetDisplayDescription(evt));

            var noVessel = GameStateRecorder.BuildScienceLabCreditEvent(
                300, 40f, 52f, "ScienceTransmission", null, "Mun");
            Assert.Equal("survey=Mun", noVessel.detail);
        }

        [Fact]
        public void SurveyCreditEvent_ConvertsToAnEarningThatNamesTheBody()
        {
            var row = GameStateEventConverter.ConvertEvent(
                GameStateRecorder.BuildScienceLabCreditEvent(300, 40f, 52f, "ScienceTransmission", "Scan Sat", "Mun"),
                "rec-scan");

            Assert.NotNull(row);
            Assert.Equal(GameActionType.ScienceLabEarning, row.Type);
            Assert.Equal("rec-scan", row.RecordingId);
            Assert.Equal(12f, row.ScienceAwarded);
            Assert.Equal(ScienceMethod.Transmitted, row.Method);
            Assert.Equal("Mun", row.ScanBodyName);
            Assert.Equal("Orbital survey of Mun +12 sci", GameActionDisplay.GetDescription(row, null));

            // A lab row names no body and keeps its wording.
            var lab = GameStateEventConverter.ConvertEvent(
                GameStateRecorder.BuildScienceLabCreditEvent(300, 40f, 52f, "ScienceTransmission", "Lab"), null);
            Assert.Null(lab.ScanBodyName);
            Assert.Equal("Lab science transmitted +12 sci", GameActionDisplay.GetDescription(lab, null));
        }

        [Fact]
        public void SurveyEarning_RoundTripsTheBody_LabRowWritesNoBodyKey()
        {
            var survey = Lab(42.5, 12f, "rec-scan");
            survey.ScanBodyName = "Minmus";
            var parent = new ConfigNode("ROOT");
            survey.SerializeInto(parent);
            var result = GameAction.DeserializeFrom(parent.GetNode("GAME_ACTION"));
            Assert.Equal("Minmus", result.ScanBodyName);
            Assert.Equal(12f, result.ScienceAwarded);

            var labParent = new ConfigNode("ROOT");
            Lab(42.5, 12f, "rec-lab").SerializeInto(labParent);
            var labNode = labParent.GetNode("GAME_ACTION");
            Assert.Null(labNode.GetValue("scanBody"));
            Assert.Null(GameAction.DeserializeFrom(labNode).ScanBodyName);
        }

        [Fact]
        public void GetActionKey_SurveyAndLabOfOneAmountAtOneUt_AreDistinct_LabKeyUnchanged()
        {
            var survey = Lab(100, 5f);
            survey.ScanBodyName = "Mun";
            Assert.NotEqual(LedgerOrchestrator.GetActionKey(Lab(100, 5f)), LedgerOrchestrator.GetActionKey(survey));
            Assert.EndsWith(":0:5", LedgerOrchestrator.GetActionKey(Lab(100, 5f)));
        }

        [Fact]
        public void OnScienceLabCredited_Survey_LiveRecording_TagsTheFlightThatRanIt()
        {
            LedgerOrchestrator.Initialize();
            GameStateRecorder.TagResolverForTesting = () => "rec-live";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;

            GameStateRecorder.OnScienceLabCredited(800, 10f, 22f, "ScienceTransmission", "Scan Sat",
                0u, "Mun");

            var evt = GameStateStore.Events.Single(e => e.eventType == GameStateEventType.ScienceLabCredited);
            Assert.Equal("rec-live", evt.recordingId);
            Assert.Equal(800, evt.ut);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.ScienceLabEarning);
            Assert.Contains(logLines, l => l.Contains("[GameStateRecorder]")
                && l.Contains("Game state: ScienceLabCredited +12.00") && l.Contains("survey='Mun'"));
        }

        [Fact]
        public void OnScienceLabCredited_Survey_NoRecording_LandsAnUntaggedRowAtTheSurveyUt()
        {
            LedgerOrchestrator.Initialize();
            GameStateRecorder.TagResolverForTesting = () => "";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => false;

            GameStateRecorder.OnScienceLabCredited(900, 10f, 22f, "ScienceTransmission", "Scan Sat",
                0u, "Mun");

            var row = Ledger.Actions.Single(a => a.Type == GameActionType.ScienceLabEarning);
            Assert.Equal(900, row.UT);
            Assert.Equal(12f, row.ScienceAwarded);
            Assert.Equal("Mun", row.ScanBodyName);
            Assert.Null(row.RecordingId);
        }

        [Fact]
        public void SurveyCreditPatch_TargetsTheStockCompletion_AndComputesTheBonusTheStockWay()
        {
            var target = HarmonyLib.AccessTools.Method(typeof(ModuleOrbitalSurveyor), "CompleteSurvey");
            Assert.NotNull(target);
            var ps = target.GetParameters();
            Assert.Equal(3, ps.Length);
            Assert.Equal(typeof(ScienceData), ps[0].ParameterType);
            Assert.Equal(typeof(Vessel), ps[1].ParameterType);
            Assert.Equal(typeof(bool), ps[2].ParameterType);

            // Stock: (float)SciBonus * mainBody.scienceValues.RecoveryValue.
            Assert.Equal(40f, Parsek.Patches.OrbitalSurveyCreditPatch.SurveyBonus(10, 4f));
            Assert.Equal(0f, Parsek.Patches.OrbitalSurveyCreditPatch.SurveyBonus(0, 4f));
        }

        [Fact]
        public void SurveyCredit_IsCreditOnlyWhenThePoolMoved()
        {
            // The early-return paths (another part's data, an aborted transmission) leave the
            // pool where it was, so the prefix's gross is never reported.
            Assert.False(Parsek.Patches.ScienceLabCreditCapture.IsCredit(40f, 40f, 12f));
            Assert.False(Parsek.Patches.ScienceLabCreditCapture.IsCredit(float.NaN, 52f, 12f));
            Assert.True(Parsek.Patches.ScienceLabCreditCapture.IsCredit(40f, 52f, 12f));
        }
    }
}
