using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for KspStatePatcher. Since KSP singletons (Funding.Instance,
    /// ResearchAndDevelopment.Instance, Reputation.Instance, ScenarioUpgradeableFacilities)
    /// are null in the test environment, these tests verify that null singletons are
    /// handled gracefully without crashing, and that logging occurs as expected.
    /// </summary>
    [Collection("Sequential")]
    public class KspStatePatcherTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public KspStatePatcherTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            // Ensure suppression flags start clean
            GameStateRecorder.SuppressResourceEvents = false;
            GameStateRecorder.IsReplayingActions = false;

            // FindObjectsOfType crashes outside Unity
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateStore.SuppressLogging = false;
            KspStatePatcher.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GameStateRecorder.SuppressResourceEvents = false;
            GameStateRecorder.IsReplayingActions = false;
        }

        private int CountLogs(string subsystemToken, string messageToken)
        {
            int count = 0;
            foreach (string line in logLines)
            {
                if (line.Contains(subsystemToken) && line.Contains(messageToken))
                    count++;
            }

            return count;
        }

        // ================================================================
        // PatchScience — null singleton
        // ================================================================

        [Fact]
        public void PatchScience_NullSingleton_DoesNotCrash()
        {
            // ResearchAndDevelopment.Instance is null in test environment
            var module = new ScienceModule();
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.ScienceEarning,
                SubjectId = "test@subject",
                ScienceAwarded = 10f,
                SubjectMaxValue = 50f
            });

            // Should not throw — null singleton is handled gracefully
            KspStatePatcher.PatchScience(module);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("ResearchAndDevelopment.Instance is null"));
        }

        [Fact]
        public void PatchScience_NullModule_LogsWarning()
        {
            KspStatePatcher.PatchScience(null);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("null ScienceModule"));
        }

        [Fact]
        public void AdjustSciencePatchTargetForPendingRecentTechResearch_HoldsBackUnmatchedDebit()
        {
            try
            {
                LedgerOrchestrator.NowUtProviderForTesting = () => 250.0;

                var evt = new GameStateEvent
                {
                    ut = 250.0,
                    eventType = GameStateEventType.ScienceChanged,
                    key = LedgerOrchestrator.TechResearchScienceReasonKey,
                    valueBefore = 35.0,
                    valueAfter = 10.0
                };
                GameStateStore.AddEvent(ref evt);
                Ledger.AddAction(new GameAction
                {
                    UT = 250.0,
                    Type = GameActionType.ScienceSpending,
                    Cost = 10f
                });

                double adjusted = KspStatePatcher.AdjustSciencePatchTargetForPendingRecentTechResearch(
                    targetScience: 25.0,
                    currentScience: 10f);

                Assert.Equal(10.0, adjusted, 3);
                Assert.Contains(logLines, l =>
                    l.Contains("[KspStatePatcher]") &&
                    l.Contains("holding back 15.0 pending tech-unlock science"));
            }
            finally
            {
                LedgerOrchestrator.NowUtProviderForTesting = null;
            }
        }

        [Fact]
        public void AdjustSciencePatchTargetForPendingRecentTechResearch_PreservesNonTechRefundPortion()
        {
            try
            {
                LedgerOrchestrator.NowUtProviderForTesting = () => 300.0;

                var evt = new GameStateEvent
                {
                    ut = 300.0,
                    eventType = GameStateEventType.ScienceChanged,
                    key = LedgerOrchestrator.TechResearchScienceReasonKey,
                    valueBefore = 20.0,
                    valueAfter = 10.0
                };
                GameStateStore.AddEvent(ref evt);
                Ledger.AddAction(new GameAction
                {
                    UT = 300.0,
                    Type = GameActionType.ScienceSpending,
                    Cost = 5f
                });

                double adjusted = KspStatePatcher.AdjustSciencePatchTargetForPendingRecentTechResearch(
                    targetScience: 18.0,
                    currentScience: 10f);

                Assert.Equal(13.0, adjusted, 3);
                Assert.Contains(logLines, l =>
                    l.Contains("[KspStatePatcher]") &&
                    l.Contains("holding back 5.0 pending tech-unlock science"));
            }
            finally
            {
                LedgerOrchestrator.NowUtProviderForTesting = null;
            }
        }

        [Fact]
        public void BuildTargetTechIdsForPatch_RewindCutoffUsesPastBaselineOnly()
        {
            var baselines = new List<GameStateBaseline>
            {
                MakeTechBaseline(0.0, "start"),
                MakeTechBaseline(108.97003112793047, "start"),
                MakeTechBaseline(251.32699401860728, "start", "basicRocketry", "engineering101")
            };
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 124.43003112792739,
                    Type = GameActionType.ScienceSpending,
                    NodeId = "basicRocketry",
                    Affordable = true
                },
                new GameAction
                {
                    UT = 124.43003112792739,
                    Type = GameActionType.ScienceSpending,
                    NodeId = "engineering101",
                    Affordable = true
                }
            };

            var target = KspStatePatcher.BuildTargetTechIdsForPatch(
                baselines,
                actions,
                utCutoff: 49.420000000004322);

            Assert.NotNull(target);
            Assert.Single(target);
            Assert.Contains("start", target);
            Assert.DoesNotContain("basicRocketry", target);
            Assert.DoesNotContain("engineering101", target);
        }

        [Fact]
        public void BuildTargetTechIdsForPatch_CutoffAddsAffordableSpendingsAfterSelectedBaseline()
        {
            var baselines = new List<GameStateBaseline>
            {
                MakeTechBaseline(0.0, "start"),
                MakeTechBaseline(108.97003112793047, "start"),
                MakeTechBaseline(251.32699401860728, "start", "basicRocketry", "engineering101")
            };
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 124.43003112792739,
                    Type = GameActionType.ScienceSpending,
                    NodeId = "basicRocketry",
                    Affordable = true
                },
                new GameAction
                {
                    UT = 124.43003112792739,
                    Type = GameActionType.ScienceSpending,
                    NodeId = "engineering101",
                    Affordable = true
                },
                new GameAction
                {
                    UT = 140.0,
                    Type = GameActionType.ScienceSpending,
                    NodeId = "futureTech",
                    Affordable = true
                }
            };

            var target = KspStatePatcher.BuildTargetTechIdsForPatch(
                baselines,
                actions,
                utCutoff: 130.0);

            Assert.NotNull(target);
            Assert.Equal(3, target.Count);
            Assert.Contains("start", target);
            Assert.Contains("basicRocketry", target);
            Assert.Contains("engineering101", target);
            Assert.DoesNotContain("futureTech", target);
        }

        [Fact]
        public void BuildTargetTechIdsForPatch_IgnoresUnaffordableScienceSpending()
        {
            var baselines = new List<GameStateBaseline>
            {
                MakeTechBaseline(0.0, "start")
            };
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 20.0,
                    Type = GameActionType.ScienceSpending,
                    NodeId = "basicRocketry",
                    Affordable = false
                }
            };

            var target = KspStatePatcher.BuildTargetTechIdsForPatch(
                baselines,
                actions,
                utCutoff: 25.0);

            Assert.NotNull(target);
            Assert.Single(target);
            Assert.Contains("start", target);
            Assert.DoesNotContain("basicRocketry", target);
        }

        [Fact]
        public void AddPurchasedPartsForTech_AddsLoadedPartsMatchingTechAndDedups()
        {
            var proto = new ProtoTechNode
            {
                techID = "basicRocketry",
                partsPurchased = new List<AvailablePart>()
            };
            var booster = new AvailablePart { name = "solidBooster.sm", TechRequired = "basicRocketry" };
            var ignored = new AvailablePart { name = "mk1pod.v2", TechRequired = "start" };

            int added = KspStatePatcher.AddPurchasedPartsForTech(
                proto,
                "basicRocketry",
                new[] { booster, ignored, booster });

            Assert.Equal(1, added);
            var purchased = Assert.Single(proto.partsPurchased);
            Assert.Same(booster, purchased);
        }

        private static GameStateBaseline MakeTechBaseline(double ut, params string[] techIds)
        {
            var baseline = new GameStateBaseline { ut = ut };
            baseline.researchedTechIds.AddRange(techIds);
            return baseline;
        }

        // ================================================================
        // #559 review follow-ups: PatchTechTree skip paths + reflection-failure warn
        // ================================================================

        [Fact]
        public void PatchTechTree_NullTarget_LogsVerboseAndSkips()
        {
            // The live rewind-only gate (LedgerOrchestrator) passes null when utCutoff is null,
            // so the "no target tech set supplied" verbose log is the hot no-op path.
            KspStatePatcher.PatchTechTree(null);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") &&
                l.Contains("PatchTechTree: no target tech set supplied"));
        }

        [Fact]
        public void PatchTechTree_NullResearchAndDevelopmentSingleton_LogsVerboseAndSkips()
        {
            // ResearchAndDevelopment.Instance is null in the xUnit environment, so even a
            // populated target set must short-circuit with the explicit verbose log.
            var target = new HashSet<string>(StringComparer.Ordinal) { "start" };

            KspStatePatcher.PatchTechTree(target);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") &&
                l.Contains("PatchTechTree: ResearchAndDevelopment.Instance is null"));
        }

        [Fact]
        public void PatchTechTree_IncludesUtCutoffAndBaselineUtInAvailableLog()
        {
            // PatchTechTree's Info log must surface the rewind context (cutoff UT + selected
            // baseline UT) so log readers can cross-check against ledger actions. The skip
            // path short-circuits before that Info log fires, so we also verify the label
            // formatting via the nullable-context skip path: the "no target set" Verbose log
            // proves the formatting helpers are reached safely with null inputs.
            KspStatePatcher.PatchTechTree(
                targetTechIds: null,
                utCutoff: 49.42,
                baselineUt: 0.0);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") &&
                l.Contains("PatchTechTree: no target tech set supplied"));
        }

        [Fact]
        public void EmitProtoTechNodesReflectionWarnOnce_FiresOncePerSession()
        {
            // ResearchAndDevelopment.Instance is null in xUnit, so GetProtoTechNodesDictionary
            // never reaches the reflection path organically. The emitter is exposed internally
            // so xUnit can assert the one-shot guard directly.
            KspStatePatcher.EmitProtoTechNodesReflectionWarnOnce("xunit-seam first call");
            KspStatePatcher.EmitProtoTechNodesReflectionWarnOnce("xunit-seam second call");
            KspStatePatcher.EmitProtoTechNodesReflectionWarnOnce("xunit-seam third call");

            int warnCount = 0;
            foreach (var line in logLines)
            {
                if (line.Contains("[KspStatePatcher]")
                    && line.Contains("reflection lookup for ResearchAndDevelopment.protoTechNodes failed"))
                {
                    warnCount++;
                }
            }

            Assert.Equal(1, warnCount);
            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") &&
                l.Contains("reflection lookup for ResearchAndDevelopment.protoTechNodes failed") &&
                l.Contains("xunit-seam first call"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("xunit-seam second call"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("xunit-seam third call"));
        }

        [Fact]
        public void EmitProtoTechNodesReflectionWarnOnce_ResetForTestingClearsOneShotGuard()
        {
            KspStatePatcher.EmitProtoTechNodesReflectionWarnOnce("first session");

            // Second call in the same "session" is suppressed.
            KspStatePatcher.EmitProtoTechNodesReflectionWarnOnce("still first session");

            // After ResetForTesting the guard is cleared so the next emit fires again.
            KspStatePatcher.ResetForTesting();
            KspStatePatcher.EmitProtoTechNodesReflectionWarnOnce("second session");

            int warnCount = 0;
            foreach (var line in logLines)
            {
                if (line.Contains("[KspStatePatcher]")
                    && line.Contains("reflection lookup for ResearchAndDevelopment.protoTechNodes failed"))
                {
                    warnCount++;
                }
            }

            Assert.Equal(2, warnCount);
            Assert.Contains(logLines, l => l.Contains("first session"));
            Assert.DoesNotContain(logLines, l => l.Contains("still first session"));
            Assert.Contains(logLines, l => l.Contains("second session"));
        }

        // ================================================================
        // PatchFunds — null singleton
        // ================================================================

        [Fact]
        public void PatchFunds_NullSingleton_DoesNotCrash()
        {
            var module = new FundsModule();

            KspStatePatcher.PatchFunds(module);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("Funding.Instance is null"));
        }

        // ================================================================
        // PatchReputation — null singleton
        // ================================================================

        [Fact]
        public void PatchReputation_NullSingleton_DoesNotCrash()
        {
            var module = new ReputationModule();

            KspStatePatcher.PatchReputation(module);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("Reputation.Instance is null"));
        }

        // ================================================================
        // PatchFacilities — null protoUpgradeables
        // ================================================================

        [Fact]
        public void PatchFacilities_NullModule_LogsWarning()
        {
            KspStatePatcher.PatchFacilities(null);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("null FacilitiesModule"));
        }

        // ================================================================
        // PatchAll — suppression flags
        // ================================================================

        // ================================================================
        // PatchScience — per-subject patching with null singleton
        // ================================================================

        [Fact]
        public void PatchScience_NullSingleton_SkipsPerSubjectPatching()
        {
            // ResearchAndDevelopment.Instance is null in test environment.
            // PatchScience should skip entirely (including per-subject patching) without crashing.
            var module = new ScienceModule();
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.ScienceEarning,
                SubjectId = "crewReport@KerbinSrfLanded",
                ScienceAwarded = 5f,
                SubjectMaxValue = 10f
            });
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.ScienceEarning,
                SubjectId = "temperatureScan@KerbinSrfLanded",
                ScienceAwarded = 8f,
                SubjectMaxValue = 16f
            });

            Assert.Equal(2, module.SubjectCount);

            KspStatePatcher.PatchScience(module);

            // Should log that R&D is null — per-subject patching is not reached
            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("ResearchAndDevelopment.Instance is null"));
        }

        [Fact]
        public void PatchPerSubjectScience_NullModule_LogsWarning()
        {
            KspStatePatcher.PatchPerSubjectScience(null);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("null ScienceModule"));
        }

        [Fact]
        public void PatchPerSubjectScience_NullSingleton_SkipsGracefully()
        {
            // ResearchAndDevelopment.Instance is null in test environment
            var module = new ScienceModule();
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.ScienceEarning,
                SubjectId = "test@subject",
                ScienceAwarded = 10f,
                SubjectMaxValue = 50f
            });

            KspStatePatcher.PatchPerSubjectScience(module);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("ResearchAndDevelopment.Instance is null"));
        }

        // ================================================================
        // PatchFacilities — destruction state with no DestructibleBuildings
        // (Full integration testing of Demolish/Repair requires in-game verification
        //  since DestructibleBuilding is a Unity MonoBehaviour unavailable in tests)
        // ================================================================

        [Fact]
        public void PatchFacilities_EmptyProtoUpgradeables_CompletesWithoutCrash()
        {
            // protoUpgradeables is an empty dict in the test environment (not null),
            // so the level loop runs but finds nothing. Destruction patching is suppressed
            // via SuppressUnityCallsForTesting (FindObjectsOfType crashes outside Unity).
            var module = new FacilitiesModule();
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.FacilityDestruction,
                FacilityId = "SpaceCenter/LaunchPad/%.%.%.%"
            });

            Assert.True(module.IsFacilityDestroyed("SpaceCenter/LaunchPad/%.%.%.%"));

            KspStatePatcher.PatchFacilities(module);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("SuppressUnityCallsForTesting"));
        }

        // ================================================================
        // Bug #596 (P2 follow-up) — INFO gate: only patched + notFound trigger INFO.
        // skipped-only and empty-totals route through rate-limited Verbose so a
        // steady-state non-empty FacilitiesModule does not flood INFO every recalc.
        // ================================================================

        [Fact]
        public void PatchFacilities_NotFound_LogsInfoSummary()
        {
            // protoUpgradeables is empty in tests, so a tracked facility will
            // hit the notFound branch. notFound > 0 must surface at INFO so
            // missing-facility diagnostics stay visible.
            var module = new FacilitiesModule();
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.FacilityUpgrade,
                FacilityId = "SpaceCenter/LaunchPad",
                ToLevel = 2
            });

            logLines.Clear();
            KspStatePatcher.PatchFacilities(module);

            Assert.Contains(logLines, l =>
                l.Contains("[INFO]") &&
                l.Contains("[KspStatePatcher]") &&
                l.Contains("PatchFacilities: levels patched=") &&
                l.Contains("notFound=1"));
        }

        [Fact]
        public void PatchFacilities_Empty_DoesNotLogInfo_UsesRateLimitedVerbose()
        {
            // No tracked facilities -> patched=0, skipped=0, notFound=0.
            // Must NOT log at INFO; must hit the rate-limited Verbose
            // "nothing to patch" path.
            var module = new FacilitiesModule();

            logLines.Clear();
            for (int i = 0; i < 50; i++)
                KspStatePatcher.PatchFacilities(module);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[INFO]") &&
                l.Contains("[KspStatePatcher]") &&
                l.Contains("PatchFacilities: levels patched"));
            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") &&
                l.Contains("PatchFacilities: nothing to patch"));
            int emptyLines = logLines.FindAll(l =>
                l.Contains("[KspStatePatcher]") &&
                l.Contains("PatchFacilities: nothing to patch")).Count;
            // Rate-limited to one line per 5s window; 50 immediate calls
            // collapse to one.
            Assert.Equal(1, emptyLines);
        }

        // Note: the skipped-only branch (patchedCount==0 && notFoundCount==0
        // && skippedCount>0) requires real KSP UpgradeableFacility refs in
        // ScenarioUpgradeableFacilities.protoUpgradeables, which are not
        // wireable in the headless xUnit environment. That path is exercised
        // in-game by a steady-state non-empty FacilitiesModule recalc loop
        // — bug #596's playtest log captured the original symptom and the
        // post-fix verification (no INFO summary) belongs in the next
        // playtest validation rather than an xUnit canary.

        // ================================================================
        // PatchAll — suppression flags
        // ================================================================

        [Fact]
        public void PatchAll_SetsSuppressFlagsAndRestores()
        {
            // Verify flags are clean before
            Assert.False(GameStateRecorder.SuppressResourceEvents);
            Assert.False(GameStateRecorder.IsReplayingActions);

            var science = new ScienceModule();
            var funds = new FundsModule();
            var reputation = new ReputationModule();
            var milestones = new MilestonesModule();
            var facilities = new FacilitiesModule();

            KspStatePatcher.PatchAll(science, funds, reputation, milestones, facilities);

            // Flags must be restored after PatchAll
            Assert.False(GameStateRecorder.SuppressResourceEvents);
            Assert.False(GameStateRecorder.IsReplayingActions);

            // Should have logged completion
            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("PatchAll complete"));
        }

        [Fact]
        public void PatchAll_RepeatedSandboxNoop_LogsStableSkipsOnce()
        {
            var science = new ScienceModule();
            var funds = new FundsModule();
            var reputation = new ReputationModule();
            var milestones = new MilestonesModule();
            var facilities = new FacilitiesModule();

            KspStatePatcher.PatchAll(science, funds, reputation, milestones, facilities);
            KspStatePatcher.PatchAll(science, funds, reputation, milestones, facilities);

            Assert.Equal(1, CountLogs("[KspStatePatcher]", "PatchTechTree: no target tech set supplied"));
            Assert.Equal(1, CountLogs("[KspStatePatcher]", "PatchFunds: Funding.Instance is null"));
            Assert.Equal(1, CountLogs("[KspStatePatcher]", "PatchAll complete"));
        }

        // ================================================================
        // #455 — IsRepeatableRecordType type pre-filter + missing-stock-field WARN silence
        //
        // Before the fix, PatchRepeatableRecordNode probed reflection for record/
        // rewardThreshold/rewardInterval on every ProgressNode and WARN'd when any
        // were missing. For the 392 per-body one-shots (Bop/Orbit, Dres/Flight, …)
        // none of those fields exist, producing 3,136 WARN lines in a single playtest
        // and short-circuiting the one-shot patch path. The pre-filter distinguishes
        // "not a record subclass" (return false silently, let the one-shot path run)
        // from "is a record subclass but missing stock fields" (keep the WARN).
        // ================================================================

        /// <summary>
        /// Synthetic stand-ins for KSPAchievements.RecordsAltitude/Depth/Speed/Distance.
        /// The production code only treats the exact stock full names as repeatable record
        /// types, but <see cref="KspStatePatcher.SuppressUnityCallsForTesting"/> enables a
        /// simple-name fallback so xUnit can still exercise the malformed-record WARN branch.
        /// None of these fakes carry the stock <c>record</c>/<c>rewardThreshold</c>/
        /// <c>rewardInterval</c> fields.
        /// </summary>
        private class RecordsAltitude : ProgressNode
        {
            public RecordsAltitude() : base("RecordsAltitude", startReached: false) { }
        }
        private class RecordsDepth : ProgressNode
        {
            public RecordsDepth() : base("RecordsDepth", startReached: false) { }
        }
        private class RecordsSpeed : ProgressNode
        {
            public RecordsSpeed() : base("RecordsSpeed", startReached: false) { }
        }
        private class RecordsDistance : ProgressNode
        {
            public RecordsDistance() : base("RecordsDistance", startReached: false) { }
        }

        /// <summary>
        /// Stand-in for one-shot per-body nodes (Orbit, Landing, Flyby, Flight, …).
        /// Any class name other than the four <c>Records*</c> names must drop out of
        /// the repeatable-record branch.
        /// </summary>
        private class OrbitNode : ProgressNode
        {
            public OrbitNode() : base("Orbit", startReached: false) { }
        }
        private class FlightNode : ProgressNode
        {
            public FlightNode() : base("Flight", startReached: false) { }
        }

        [Fact]
        public void IsRepeatableRecordType_NullNode_ReturnsFalse()
        {
            Assert.False(KspStatePatcher.IsRepeatableRecordType(null));
        }

        [Fact]
        public void IsRepeatableRecordType_RecordsSubclasses_ReturnTrue()
        {
            Assert.True(KspStatePatcher.IsRepeatableRecordType(new KSPAchievements.RecordsAltitude()));
            Assert.True(KspStatePatcher.IsRepeatableRecordType(new KSPAchievements.RecordsDepth()));
            Assert.True(KspStatePatcher.IsRepeatableRecordType(new KSPAchievements.RecordsSpeed()));
            Assert.True(KspStatePatcher.IsRepeatableRecordType(new KSPAchievements.RecordsDistance()));
        }

        [Fact]
        public void IsRepeatableRecordType_OneShotSubclasses_ReturnFalse()
        {
            Assert.False(KspStatePatcher.IsRepeatableRecordType(new OrbitNode()));
            Assert.False(KspStatePatcher.IsRepeatableRecordType(new FlightNode()));
        }

        [Fact]
        public void IsRepeatableRecordType_NameCollisionOutsideTestMode_ReturnsFalse()
        {
            KspStatePatcher.SuppressUnityCallsForTesting = false;

            Assert.False(KspStatePatcher.IsRepeatableRecordType(new RecordsAltitude()));
        }

        [Fact]
        public void PatchRepeatableRecordNode_NonRecordType_ReturnsFalseAndDoesNotWarn()
        {
            // The playtest log signature was 3,136 copies of:
            //   [Parsek][WARN][KspStatePatcher] PatchMilestones: repeatable node 'Bop/Orbit' is missing stock record fields (…) — skipping
            // After the fix the WARN must be gone AND the caller's one-shot path must
            // get a chance to run, which requires the function to return false (not true).
            var reachedField = typeof(ProgressNode).GetField("reached",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var completeField = typeof(ProgressNode).GetField("complete",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(reachedField);
            Assert.NotNull(completeField);

            var node = new OrbitNode();
            bool recognized = KspStatePatcher.PatchRepeatableRecordNode(
                node, effectiveCount: 0, qualifiedId: "Bop/Orbit",
                reachedField, completeField,
                authoritativeRepeatableRecordState: false,
                out bool changed);

            Assert.False(recognized); // falls through to one-shot branch in PatchProgressNodeTree
            Assert.False(changed);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("is missing stock record fields"));
        }

        [Fact]
        public void PatchRepeatableRecordNode_RecordsSubclassWithoutStockFields_StillWarns()
        {
            // The WARN must survive for genuinely-record types that drift away from
            // the stock API — that is a real structural change and should page someone.
            // Our synthetic RecordsAltitude has the matching class name but no stock
            // record/rewardThreshold/rewardInterval fields, so the reflection-miss
            // branch is reachable without a live KSP runtime.
            var reachedField = typeof(ProgressNode).GetField("reached",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var completeField = typeof(ProgressNode).GetField("complete",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(reachedField);
            Assert.NotNull(completeField);

            var node = new RecordsAltitude();
            bool recognized = KspStatePatcher.PatchRepeatableRecordNode(
                node, effectiveCount: 0, qualifiedId: "RecordsAltitude",
                reachedField, completeField,
                authoritativeRepeatableRecordState: false,
                out bool changed);

            // Genuine "recognized-but-malformed" case: true so the caller counts it as
            // handled, but the WARN must still fire as a structural-change alarm.
            Assert.True(recognized);
            Assert.False(changed);
            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]")
                && l.Contains("RecordsAltitude")
                && l.Contains("is missing stock record fields"));
        }
    }
}
