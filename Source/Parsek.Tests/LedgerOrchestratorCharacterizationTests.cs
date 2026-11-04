using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization tests for small decision helpers of <see cref="LedgerOrchestrator"/>
    /// that had no direct test: the funds-budget and reputation-nominal classifiers, the
    /// science reconcile window / scope helpers, the KSC sequence seed, the seed-capture
    /// stamp, the strategy-conversion untag on discard and the contract reinstate cutoffs.
    /// Each test pins what the code does today across its branches.
    /// </summary>
    [Collection("Sequential")]
    public class LedgerOrchestratorCharacterizationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public LedgerOrchestratorCharacterizationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            LedgerOrchestrator.ResetForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            EffectiveState.ResetCachesForTesting();
            Ledger.ResetForTesting();
            GameStateStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
        }

        // ---------- ActionTouchesFundsBudget ----------------------------------

        [Fact]
        public void ActionTouchesFundsBudget_NullAction_False()
        {
            Assert.False(LedgerOrchestrator.ActionTouchesFundsBudget(null));
        }

        [Fact]
        public void ActionTouchesFundsBudget_EachFundsArm_TrueOnlyWhenItsAmountIsNonZero()
        {
            // Each arm reads exactly one amount field: zero -> false, non-zero -> true.
            var cases = new List<(GameAction zero, GameAction nonZero)>
            {
                (new GameAction { Type = GameActionType.FundsEarning },
                 new GameAction { Type = GameActionType.FundsEarning, FundsAwarded = 5f }),
                (new GameAction { Type = GameActionType.FundsSpending },
                 new GameAction { Type = GameActionType.FundsSpending, FundsSpent = 5f }),
                (new GameAction { Type = GameActionType.MilestoneAchievement },
                 new GameAction { Type = GameActionType.MilestoneAchievement, MilestoneFundsAwarded = 5f }),
                (new GameAction { Type = GameActionType.ContractAccept },
                 new GameAction { Type = GameActionType.ContractAccept, AdvanceFunds = 5f }),
                (new GameAction { Type = GameActionType.ContractFail },
                 new GameAction { Type = GameActionType.ContractFail, FundsPenalty = 5f }),
                (new GameAction { Type = GameActionType.ContractCancel },
                 new GameAction { Type = GameActionType.ContractCancel, FundsPenalty = 5f }),
                (new GameAction { Type = GameActionType.FacilityUpgrade },
                 new GameAction { Type = GameActionType.FacilityUpgrade, FacilityCost = 5f }),
                (new GameAction { Type = GameActionType.FacilityRepair },
                 new GameAction { Type = GameActionType.FacilityRepair, FacilityCost = 5f }),
                (new GameAction { Type = GameActionType.KerbalHire },
                 new GameAction { Type = GameActionType.KerbalHire, HireCost = 5f }),
                (new GameAction { Type = GameActionType.StrategyActivate },
                 new GameAction { Type = GameActionType.StrategyActivate, SetupCost = 5f }),
            };
            foreach (var c in cases)
            {
                Assert.False(LedgerOrchestrator.ActionTouchesFundsBudget(c.zero), c.zero.Type.ToString());
                Assert.True(LedgerOrchestrator.ActionTouchesFundsBudget(c.nonZero), c.nonZero.Type.ToString());
            }
        }

        [Fact]
        public void ActionTouchesFundsBudget_ContractComplete_EitherRewardFieldCounts()
        {
            Assert.False(LedgerOrchestrator.ActionTouchesFundsBudget(
                new GameAction { Type = GameActionType.ContractComplete }));
            Assert.True(LedgerOrchestrator.ActionTouchesFundsBudget(
                new GameAction { Type = GameActionType.ContractComplete, FundsReward = 1f }));
            Assert.True(LedgerOrchestrator.ActionTouchesFundsBudget(
                new GameAction { Type = GameActionType.ContractParameterComplete, TransformedFundsReward = -1f }));
        }

        [Fact]
        public void ActionTouchesFundsBudget_StockPoolAdjustment_OnlyFundsPoolWithAmount()
        {
            Assert.True(LedgerOrchestrator.ActionTouchesFundsBudget(new GameAction
            {
                Type = GameActionType.StockPoolAdjustment,
                AdjustmentPool = StockPool.Funds,
                AdjustmentAmount = -3.0
            }));
            Assert.False(LedgerOrchestrator.ActionTouchesFundsBudget(new GameAction
            {
                Type = GameActionType.StockPoolAdjustment,
                AdjustmentPool = StockPool.Funds,
                AdjustmentAmount = 0.0
            }));
            Assert.False(LedgerOrchestrator.ActionTouchesFundsBudget(new GameAction
            {
                Type = GameActionType.StockPoolAdjustment,
                AdjustmentPool = StockPool.Science,
                AdjustmentAmount = 10.0
            }));
        }

        [Fact]
        public void ActionTouchesFundsBudget_NonFundsType_False()
        {
            Assert.False(LedgerOrchestrator.ActionTouchesFundsBudget(new GameAction
            {
                Type = GameActionType.ReputationEarning,
                NominalRep = 10f,
                FundsAwarded = 10f
            }));
        }

        // ---------- ResolveInsideSeedRepNominal --------------------------------

        [Fact]
        public void ResolveInsideSeedRepNominal_Null_ZeroNotPreCurved()
        {
            Assert.Equal(0f, LedgerOrchestrator.ResolveInsideSeedRepNominal(null, out bool preCurved));
            Assert.False(preCurved);
        }

        [Fact]
        public void ResolveInsideSeedRepNominal_EarningIsNominalTimesShare()
        {
            var a = new GameAction
            {
                Type = GameActionType.ReputationEarning,
                NominalRep = 10f,
                ConversionShare = 0.5f
            };
            Assert.Equal(5f, LedgerOrchestrator.ResolveInsideSeedRepNominal(a, out bool preCurved));
            Assert.False(preCurved);
        }

        [Theory]
        [InlineData(ReputationPenaltySource.Strategy)]
        [InlineData(ReputationPenaltySource.KerbalDeath)]
        [InlineData(ReputationPenaltySource.ContractDecline)]
        public void ResolveInsideSeedRepNominal_PreCurvedPenaltySources_IgnoreShare(
            ReputationPenaltySource source)
        {
            var a = new GameAction
            {
                Type = GameActionType.ReputationPenalty,
                RepPenaltySource = source,
                NominalPenalty = 8f,
                ConversionShare = 0.25f
            };
            Assert.Equal(-8f, LedgerOrchestrator.ResolveInsideSeedRepNominal(a, out bool preCurved));
            Assert.True(preCurved);
        }

        [Fact]
        public void ResolveInsideSeedRepNominal_OtherPenaltySource_ScaledByShareNotPreCurved()
        {
            var a = new GameAction
            {
                Type = GameActionType.ReputationPenalty,
                RepPenaltySource = ReputationPenaltySource.Other,
                NominalPenalty = 8f,
                ConversionShare = 0.25f
            };
            Assert.Equal(-2f, LedgerOrchestrator.ResolveInsideSeedRepNominal(a, out bool preCurved));
            Assert.False(preCurved);
        }

        [Fact]
        public void ResolveInsideSeedRepNominal_EffectiveGatedArms_ZeroWhenNotEffective()
        {
            var milestone = new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                MilestoneRepAwarded = 3f
            };
            var complete = new GameAction
            {
                Type = GameActionType.ContractComplete,
                TransformedRepReward = 4f
            };
            var fail = new GameAction
            {
                Type = GameActionType.ContractFail,
                RepPenalty = 6f
            };
            Assert.Equal(3f, LedgerOrchestrator.ResolveInsideSeedRepNominal(milestone, out _));
            Assert.Equal(4f, LedgerOrchestrator.ResolveInsideSeedRepNominal(complete, out _));
            Assert.Equal(-6f, LedgerOrchestrator.ResolveInsideSeedRepNominal(fail, out _));

            milestone.Effective = false;
            complete.Effective = false;
            fail.Effective = false;
            Assert.Equal(0f, LedgerOrchestrator.ResolveInsideSeedRepNominal(milestone, out _));
            Assert.Equal(0f, LedgerOrchestrator.ResolveInsideSeedRepNominal(complete, out _));
            Assert.Equal(0f, LedgerOrchestrator.ResolveInsideSeedRepNominal(fail, out _));
        }

        [Fact]
        public void ResolveInsideSeedRepNominal_StrategyActivateAndUnrelatedType()
        {
            Assert.Equal(-7f, LedgerOrchestrator.ResolveInsideSeedRepNominal(new GameAction
            {
                Type = GameActionType.StrategyActivate,
                SetupReputationCost = 7f
            }, out bool preCurved));
            Assert.False(preCurved);
            Assert.Equal(0f, LedgerOrchestrator.ResolveInsideSeedRepNominal(new GameAction
            {
                Type = GameActionType.FundsEarning,
                FundsAwarded = 100f
            }, out _));
        }

        // ---------- TryGetPersistedScienceActionWindow -------------------------

        [Fact]
        public void TryGetPersistedScienceActionWindow_NullOrNaN_False()
        {
            Assert.False(LedgerOrchestrator.TryGetPersistedScienceActionWindow(
                null, out _, out _, out _));
            // EndUT defaults to NaN.
            Assert.False(LedgerOrchestrator.TryGetPersistedScienceActionWindow(
                new GameAction { StartUT = 10f }, out _, out _, out _));
        }

        [Fact]
        public void TryGetPersistedScienceActionWindow_OpenSpan_ReturnedAsIs()
        {
            bool ok = LedgerOrchestrator.TryGetPersistedScienceActionWindow(
                new GameAction { StartUT = 10f, EndUT = 20f, UT = 30.0 },
                out double start, out double end, out bool collapsed);
            Assert.True(ok);
            Assert.Equal(10.0, start);
            Assert.Equal(20.0, end);
            Assert.False(collapsed);
        }

        [Fact]
        public void TryGetPersistedScienceActionWindow_CollapsedSpan_WidensToActionUT()
        {
            bool ok = LedgerOrchestrator.TryGetPersistedScienceActionWindow(
                new GameAction { StartUT = 100f, EndUT = 100f, UT = 200.0 },
                out double start, out double end, out bool collapsed);
            Assert.True(ok);
            Assert.True(collapsed);
            // Start is the collapsed float less its one-ULP half width; end is the action UT.
            Assert.True(start < 100.0 && start > 99.99, "start=" + start);
            Assert.Equal(200.0, end);
        }

        [Fact]
        public void TryGetPersistedScienceActionWindow_CollapsedSpanAfterActionUT_StartsAtActionUT()
        {
            bool ok = LedgerOrchestrator.TryGetPersistedScienceActionWindow(
                new GameAction { StartUT = 100f, EndUT = 100f, UT = 50.0 },
                out double start, out double end, out bool collapsed);
            Assert.True(ok);
            Assert.True(collapsed);
            Assert.Equal(50.0, start);
            Assert.Equal(50.0, end);
        }

        [Fact]
        public void TryGetPersistedScienceActionWindow_ZeroOrInvertedSpan_False()
        {
            Assert.False(LedgerOrchestrator.TryGetPersistedScienceActionWindow(
                new GameAction { StartUT = 0f, EndUT = 0f, UT = 50.0 }, out _, out _, out bool c1));
            Assert.False(c1);
            Assert.False(LedgerOrchestrator.TryGetPersistedScienceActionWindow(
                new GameAction { StartUT = 20f, EndUT = 10f, UT = 50.0 }, out _, out _, out _));
        }

        // ---------- DoesScienceEventMatchActionScope ---------------------------

        [Fact]
        public void DoesScienceEventMatchActionScope_UntaggedAction_MatchesAnything()
        {
            var evt = new GameStateEvent { ut = 999.0, recordingId = "rec_other" };
            Assert.True(LedgerOrchestrator.DoesScienceEventMatchActionScope(
                evt, new GameAction(), 0.0, 1.0, out bool viaWindow));
            Assert.False(viaWindow);
            Assert.True(LedgerOrchestrator.DoesScienceEventMatchActionScope(
                evt, null, 0.0, 1.0, out _));
        }

        [Fact]
        public void DoesScienceEventMatchActionScope_TagRules()
        {
            var action = new GameAction { RecordingId = "rec_a" };

            Assert.True(LedgerOrchestrator.DoesScienceEventMatchActionScope(
                new GameStateEvent { ut = 5.0, recordingId = "rec_a" }, action, 10.0, 20.0,
                out bool sameTagWindow));
            Assert.False(sameTagWindow);

            Assert.False(LedgerOrchestrator.DoesScienceEventMatchActionScope(
                new GameStateEvent { ut = 15.0, recordingId = "rec_b" }, action, 10.0, 20.0,
                out bool otherTagWindow));
            Assert.False(otherTagWindow);
        }

        [Fact]
        public void DoesScienceEventMatchActionScope_UntaggedEvent_MatchesInsideWindowInclusive()
        {
            var action = new GameAction { RecordingId = "rec_a" };
            Assert.True(LedgerOrchestrator.DoesScienceEventMatchActionScope(
                new GameStateEvent { ut = 10.0 }, action, 10.0, 20.0, out bool atStart));
            Assert.True(atStart);
            Assert.True(LedgerOrchestrator.DoesScienceEventMatchActionScope(
                new GameStateEvent { ut = 20.0, recordingId = "" }, action, 10.0, 20.0, out bool atEnd));
            Assert.True(atEnd);
            Assert.False(LedgerOrchestrator.DoesScienceEventMatchActionScope(
                new GameStateEvent { ut = 9.99 }, action, 10.0, 20.0, out bool before));
            Assert.False(before);
            Assert.False(LedgerOrchestrator.DoesScienceEventMatchActionScope(
                new GameStateEvent { ut = 20.01 }, action, 10.0, 20.0, out _));
        }

        // ---------- FormatScienceEventForReconcileDump -------------------------

        [Fact]
        public void FormatScienceEventForReconcileDump_FormatsInvariantAndLabels()
        {
            var evt = new GameStateEvent
            {
                ut = 123.456,
                key = "crewReport@KerbinSrfLanded",
                valueBefore = 10.0,
                valueAfter = 12.5,
                recordingId = "rec_a"
            };
            Assert.Equal(
                "ut=123.5 key='crewReport@KerbinSrfLanded' delta=2.5 tag='rec_a' [match, untagged-window]",
                LedgerOrchestrator.FormatScienceEventForReconcileDump(evt, true, true));
            Assert.Equal(
                "ut=0.0 key='' delta=0.0 tag='' [skip]",
                LedgerOrchestrator.FormatScienceEventForReconcileDump(new GameStateEvent(), false, false));
        }

        // ---------- HasKerbalDeathRepPenaltyRowForRecording ---------------------

        private static GameAction DeathRow(string recId, float magnitude) => new GameAction
        {
            Type = GameActionType.ReputationPenalty,
            RepPenaltySource = ReputationPenaltySource.KerbalDeath,
            RecordingId = recId,
            NominalPenalty = magnitude
        };

        [Fact]
        public void HasKerbalDeathRepPenaltyRowForRecording_NullOrEmptyInputs_False()
        {
            Assert.False(LedgerOrchestrator.HasKerbalDeathRepPenaltyRowForRecording(null, "rec_a", 10));
            Assert.False(LedgerOrchestrator.HasKerbalDeathRepPenaltyRowForRecording(
                new List<GameAction> { DeathRow("", 10f) }, "", 10));
        }

        [Fact]
        public void HasKerbalDeathRepPenaltyRowForRecording_MatchesIdSourceAndMagnitudeWithinTolerance()
        {
            var rows = new List<GameAction>
            {
                null,
                new GameAction
                {
                    Type = GameActionType.ReputationPenalty,
                    RepPenaltySource = ReputationPenaltySource.Other,
                    RecordingId = "rec_a",
                    NominalPenalty = 10f
                },
                DeathRow("rec_b", 10f),
                DeathRow("rec_a", 10f)
            };
            Assert.True(LedgerOrchestrator.HasKerbalDeathRepPenaltyRowForRecording(rows, "rec_a", 10.05));
            Assert.False(LedgerOrchestrator.HasKerbalDeathRepPenaltyRowForRecording(rows, "rec_a", 10.5));
            Assert.False(LedgerOrchestrator.HasKerbalDeathRepPenaltyRowForRecording(rows, "rec_c", 10.0));
        }

        // ---------- SeedKscSequenceCounterFromLoadedLedger ----------------------

        [Fact]
        public void SeedKscSequenceCounterFromLoadedLedger_RaisesToMaxAndLogs()
        {
            Assert.Equal(0, LedgerOrchestrator.KscSequenceCounterForTesting);
            LedgerOrchestrator.SeedKscSequenceCounterFromLoadedLedger(new List<GameAction>
            {
                new GameAction { Sequence = 3 },
                null,
                new GameAction { Sequence = 7 },
                new GameAction { Sequence = 5 }
            });
            Assert.Equal(7, LedgerOrchestrator.KscSequenceCounterForTesting);
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("KSC sequence counter seeded from the loaded ledger: 0 -> 7"));
        }

        [Fact]
        public void SeedKscSequenceCounterFromLoadedLedger_NeverLowersAndToleratesNull()
        {
            LedgerOrchestrator.SeedKscSequenceCounterFromLoadedLedger(
                new List<GameAction> { new GameAction { Sequence = 9 } });
            logLines.Clear();

            LedgerOrchestrator.SeedKscSequenceCounterFromLoadedLedger(
                new List<GameAction> { new GameAction { Sequence = 4 } });
            LedgerOrchestrator.SeedKscSequenceCounterFromLoadedLedger(
                new List<GameAction> { new GameAction { Sequence = 9 } });
            LedgerOrchestrator.SeedKscSequenceCounterFromLoadedLedger(null);

            Assert.Equal(9, LedgerOrchestrator.KscSequenceCounterForTesting);
            Assert.DoesNotContain(logLines, l => l.Contains("KSC sequence counter seeded"));
        }

        // ---------- StampRowsRecordedBeforeSeedCapture --------------------------

        private static GameAction RepSeed(double captureUT, params string[] pending) => new GameAction
        {
            Type = GameActionType.ReputationInitial,
            SeedCaptureUT = captureUT,
            SeedPendingRecordingIds = new List<string>(pending)
        };

        [Fact]
        public void StampRowsRecordedBeforeSeedCapture_NoSeedOrNoPending_Zero()
        {
            var actions = new List<GameAction>
            {
                new GameAction { Type = GameActionType.ReputationEarning, UT = 10.0 }
            };
            Assert.Equal(0, LedgerOrchestrator.StampRowsRecordedBeforeSeedCapture(
                actions, "rec_a", new List<GameAction>(), "ctx"));
            Assert.Equal(0, LedgerOrchestrator.StampRowsRecordedBeforeSeedCapture(
                actions, "rec_a", new List<GameAction>
                {
                    new GameAction { Type = GameActionType.ReputationInitial, SeedCaptureUT = 100.0 }
                }, "ctx"));
            Assert.Equal(0, LedgerOrchestrator.StampRowsRecordedBeforeSeedCapture(
                new List<GameAction>(), "rec_a", new List<GameAction> { RepSeed(100.0, "rec_a") }, "ctx"));
            Assert.False(actions[0].InsideReputationSeed);
        }

        [Fact]
        public void StampRowsRecordedBeforeSeedCapture_StampsOnlyEarlierRepRowsAndLogs()
        {
            var early = new GameAction { Type = GameActionType.ReputationEarning, UT = 50.0 };
            var late = new GameAction { Type = GameActionType.ReputationPenalty, UT = 150.0 };
            var notRep = new GameAction { Type = GameActionType.FundsEarning, UT = 40.0 };
            var already = new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = 30.0,
                InsideReputationSeed = true
            };
            var actions = new List<GameAction> { early, late, notRep, already, null };

            int stamped = LedgerOrchestrator.StampRowsRecordedBeforeSeedCapture(
                actions, "rec_a", new List<GameAction> { RepSeed(100.0, "rec_a") }, "unit-ctx");

            Assert.Equal(1, stamped);
            Assert.True(early.InsideReputationSeed);
            Assert.False(late.InsideReputationSeed);
            Assert.False(notRep.InsideReputationSeed);
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("Reputation seed stamp: 1 row(s) stamped insideRepSeed=True for unit-ctx")
                && l.Contains("at UT 100 ")
                && l.Contains("(1 later row(s) stay outside)"));
        }

        [Fact]
        public void StampRowsRecordedBeforeSeedCapture_RecordingNotPending_NoStampNoLog()
        {
            var early = new GameAction { Type = GameActionType.ReputationEarning, UT = 50.0 };
            int stamped = LedgerOrchestrator.StampRowsRecordedBeforeSeedCapture(
                new List<GameAction> { early }, "rec_other",
                new List<GameAction> { RepSeed(100.0, "rec_a") }, null);
            Assert.Equal(0, stamped);
            Assert.False(early.InsideReputationSeed);
            Assert.DoesNotContain(logLines, l => l.Contains("Reputation seed stamp:"));
        }

        // ---------- ClearStrategyConversionTagsForDiscard -----------------------

        [Fact]
        public void ClearStrategyConversionTagsForDiscard_EmptyInput_Zero()
        {
            Assert.Equal(0, LedgerOrchestrator.ClearStrategyConversionTagsForDiscard(null, "r"));
            Assert.Equal(0, LedgerOrchestrator.ClearStrategyConversionTagsForDiscard(new List<string>(), "r"));
        }

        [Fact]
        public void ClearStrategyConversionTagsForDiscard_UntagsOnlyUncommittedConversionRows()
        {
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                RecordingId = "rec_committed",
                VesselName = "Committed"
            });

            var discardedCredit = new GameAction
            {
                Type = GameActionType.StrategyScienceCredit,
                RecordingId = "rec_discarded"
            };
            var committedCredit = new GameAction
            {
                Type = GameActionType.StrategyScienceCredit,
                RecordingId = "rec_committed"
            };
            var discardedNonConversion = new GameAction
            {
                Type = GameActionType.FundsEarning,
                FundsSource = FundsEarningSource.Recovery,
                RecordingId = "rec_discarded"
            };
            var otherCredit = new GameAction
            {
                Type = GameActionType.StrategyScienceCredit,
                RecordingId = "rec_kept"
            };
            Ledger.AddAction(discardedCredit);
            Ledger.AddAction(committedCredit);
            Ledger.AddAction(discardedNonConversion);
            Ledger.AddAction(otherCredit);

            int cleared = LedgerOrchestrator.ClearStrategyConversionTagsForDiscard(
                new List<string> { "rec_discarded", "rec_committed" }, "unit-discard");

            Assert.Equal(1, cleared);
            Assert.Null(discardedCredit.RecordingId);
            Assert.Equal("rec_committed", committedCredit.RecordingId);
            Assert.Equal("rec_discarded", discardedNonConversion.RecordingId);
            Assert.Equal("rec_kept", otherCredit.RecordingId);
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("ClearStrategyConversionTagsForDiscard (unit-discard): untagged 1 strategy conversion row(s)"));
        }

        // ---------- BuildContractReinstateCutoffsForPatch -----------------------

        private static ParsekScenario InstallScenarioWithTombstones(params LedgerTombstone[] tombstones)
        {
            var scenario = new ParsekScenario
            {
                LedgerTombstones = new List<LedgerTombstone>(tombstones)
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            return scenario;
        }

        [Fact]
        public void BuildContractReinstateCutoffsForPatch_NoScenarioOrNoTombstones_Null()
        {
            Assert.Null(LedgerOrchestrator.BuildContractReinstateCutoffsForPatch());
            InstallScenarioWithTombstones();
            Assert.Null(LedgerOrchestrator.BuildContractReinstateCutoffsForPatch());
        }

        [Fact]
        public void BuildContractReinstateCutoffsForPatch_UsesRetiringStartUtElseTombstoneUt_MinPerContract()
        {
            var contractA = Guid.NewGuid();
            var contractB = Guid.NewGuid();
            var acceptA = new GameAction { Type = GameActionType.ContractAccept, ContractId = contractA.ToString() };
            var completeA = new GameAction { Type = GameActionType.ContractComplete, ContractId = contractA.ToString() };
            var failB = new GameAction { Type = GameActionType.ContractFail, ContractId = contractB.ToString() };
            Ledger.AddAction(acceptA);
            Ledger.AddAction(completeA);
            Ledger.AddAction(failB);

            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                RecordingId = "rec_early",
                VesselName = "Early",
                ExplicitStartUT = 300.0
            });
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                RecordingId = "rec_late",
                VesselName = "Late",
                ExplicitStartUT = 800.0
            });

            InstallScenarioWithTombstones(
                new LedgerTombstone { ActionId = completeA.ActionId, RetiringRecordingId = "rec_late", UT = 900.0 },
                new LedgerTombstone { ActionId = acceptA.ActionId, RetiringRecordingId = "rec_early", UT = 950.0 },
                // Retiring recording gone: falls back to the tombstone's own UT.
                new LedgerTombstone { ActionId = failB.ActionId, RetiringRecordingId = "rec_missing", UT = 1234.0 });

            var cutoffs = LedgerOrchestrator.BuildContractReinstateCutoffsForPatch();

            Assert.NotNull(cutoffs);
            Assert.Equal(2, cutoffs.Count);
            Assert.Equal(300.0, cutoffs[contractA]);
            Assert.Equal(1234.0, cutoffs[contractB]);
        }

        [Fact]
        public void BuildContractReinstateCutoffsForPatch_NonContractOrUnparseableRows_Null()
        {
            var milestone = new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                ContractId = Guid.NewGuid().ToString()
            };
            var badGuid = new GameAction { Type = GameActionType.ContractComplete, ContractId = "not-a-guid" };
            Ledger.AddAction(milestone);
            Ledger.AddAction(badGuid);
            InstallScenarioWithTombstones(
                new LedgerTombstone { ActionId = milestone.ActionId, RetiringRecordingId = "rec_x", UT = 10.0 },
                new LedgerTombstone { ActionId = badGuid.ActionId, RetiringRecordingId = "rec_x", UT = 10.0 });

            Assert.Null(LedgerOrchestrator.BuildContractReinstateCutoffsForPatch());
        }

        [Fact]
        public void BuildContractReinstateCutoffsForPatch_TombstoneWithoutRetiringId_Skipped()
        {
            var contract = Guid.NewGuid();
            var complete = new GameAction { Type = GameActionType.ContractComplete, ContractId = contract.ToString() };
            Ledger.AddAction(complete);
            InstallScenarioWithTombstones(
                new LedgerTombstone { ActionId = complete.ActionId, RetiringRecordingId = null, UT = 10.0 },
                null);

            Assert.Null(LedgerOrchestrator.BuildContractReinstateCutoffsForPatch());
        }
    }
}
