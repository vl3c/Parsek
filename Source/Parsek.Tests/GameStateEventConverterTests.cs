using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GameStateEventConverterTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GameStateEventConverterTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
        }

        public void Dispose()
        {
            GameStateStore.ResetForTesting();
            GameStateStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Helper — build GameStateEvent
        // ================================================================

        private static GameStateEvent MakeEvent(GameStateEventType type, double ut,
            string key = "", string detail = "", double valBefore = 0, double valAfter = 0,
            string recordingId = "")
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = type,
                key = key,
                detail = detail,
                valueBefore = valBefore,
                valueAfter = valAfter,
                recordingId = recordingId
            };
        }

        // ================================================================
        // TechResearched -> ScienceSpending
        // ================================================================

        [Fact]
        public void ConvertEvent_TechResearched_ReturnsScienceSpending()
        {
            var evt = MakeEvent(GameStateEventType.TechResearched, 1000.0,
                key: "survivability", detail: "cost=45;parts=mk1pod");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec1");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.ScienceSpending, action.Type);
            Assert.Equal("survivability", action.NodeId);
            Assert.Equal(45f, action.Cost);
            Assert.Equal(1000.0, action.UT);
            Assert.Equal("rec1", action.RecordingId);
        }

        [Fact]
        public void ConvertEvent_TechResearched_NoCost_DefaultsToZero()
        {
            var evt = MakeEvent(GameStateEventType.TechResearched, 500.0,
                key: "basicRocketry", detail: "parts=fuelTank");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec2");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.ScienceSpending, action.Type);
            Assert.Equal("basicRocketry", action.NodeId);
            Assert.Equal(0f, action.Cost);
        }

        // ================================================================
        // PartPurchased -> FundsSpending
        // ================================================================

        [Fact]
        public void ConvertEvent_PartPurchased_ReturnsFundsSpending()
        {
            var evt = MakeEvent(GameStateEventType.PartPurchased, 2000.0,
                key: "mk1pod", detail: "cost=600");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec3");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.FundsSpending, action.Type);
            Assert.Equal(600f, action.FundsSpent);
            Assert.Equal(FundsSpendingSource.Other, action.FundsSpendingSource);
            Assert.Equal("rec3", action.RecordingId);
        }

        // ---------- #451: charged cost is authoritative; entryCost is fallback-only ----------

        [Fact]
        public void ConvertEvent_PartPurchased_UsesCostToken_WhenEntryCostAlsoPresent()
        {
            // Post-#451 recorder output may include the raw stock entry price for
            // diagnostics, but the ledger must honor the actual charged amount from
            // `cost=`. This is the stock-default bypass=true shape.
            var evt = MakeEvent(GameStateEventType.PartPurchased, 2100.0,
                key: "solidBooster.v2", detail: "cost=0;entryCost=800");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec451-1");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.FundsSpending, action.Type);
            Assert.Equal(0f, action.FundsSpent);
            Assert.Equal(FundsSpendingSource.Other, action.FundsSpendingSource);
        }

        [Fact]
        public void ConvertEvent_PartPurchased_CostOnly_StillParses()
        {
            // Save-format read-compat: pre-#451 events only have `cost=<value>` with no
            // `entryCost=` token. That remains the authoritative amount.
            var evt = MakeEvent(GameStateEventType.PartPurchased, 2200.0,
                key: "mk1pod", detail: "cost=600");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec451-2");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.FundsSpending, action.Type);
            Assert.Equal(600f, action.FundsSpent);
        }

        [Fact]
        public void ConvertEvent_PartPurchased_EntryCostOnly_FallsBackWhenCostMissing()
        {
            // Defensive fallback for malformed/future detail that omitted `cost=`.
            var evt = MakeEvent(GameStateEventType.PartPurchased, 2300.0,
                key: "liquidEngine", detail: "entryCost=1200");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec451-3");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.FundsSpending, action.Type);
            Assert.Equal(1200f, action.FundsSpent);
        }

        // ================================================================
        // FacilityUpgraded -> FacilityUpgrade
        // ================================================================

        [Fact]
        public void ConvertEvent_FacilityUpgraded_ReturnsFacilityUpgrade()
        {
            // Normalized level 0.5 = level 1 (out of 2), target = round(0.5*2) = 1
            var evt = MakeEvent(GameStateEventType.FacilityUpgraded, 3000.0,
                key: "LaunchPad", detail: "cost=75000",
                valBefore: 0.0, valAfter: 0.5);
            var action = GameStateEventConverter.ConvertEvent(evt, "rec4");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.FacilityUpgrade, action.Type);
            Assert.Equal("LaunchPad", action.FacilityId);
            Assert.Equal(1, action.ToLevel);
            Assert.Equal(75000f, action.FacilityCost);
        }

        [Fact]
        public void ConvertEvent_FacilityUpgraded_FullUpgrade_Level2()
        {
            // Normalized level 1.0 = level 2 (fully upgraded)
            var evt = MakeEvent(GameStateEventType.FacilityUpgraded, 4000.0,
                key: "VehicleAssemblyBuilding", valBefore: 0.5, valAfter: 1.0);
            var action = GameStateEventConverter.ConvertEvent(evt, "rec5");

            Assert.NotNull(action);
            Assert.Equal(2, action.ToLevel);
        }

        // ================================================================
        // BuildingDestroyed -> FacilityDestruction
        // ================================================================

        [Fact]
        public void ConvertEvent_BuildingDestroyed_ReturnsFacilityDestruction()
        {
            var evt = MakeEvent(GameStateEventType.BuildingDestroyed, 5000.0,
                key: "LaunchPad");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec6");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.FacilityDestruction, action.Type);
            Assert.Equal("LaunchPad", action.FacilityId);
        }

        // ================================================================
        // BuildingRepaired -> FacilityRepair
        // ================================================================

        [Fact]
        public void ConvertEvent_BuildingRepaired_ReturnsFacilityRepair()
        {
            var evt = MakeEvent(GameStateEventType.BuildingRepaired, 6000.0,
                key: "Runway");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec7");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.FacilityRepair, action.Type);
            Assert.Equal("Runway", action.FacilityId);
        }

        // ================================================================
        // CrewHired -> KerbalHire
        // ================================================================

        [Fact]
        public void ConvertEvent_CrewHired_ReturnsKerbalHire()
        {
            var evt = MakeEvent(GameStateEventType.CrewHired, 7000.0,
                key: "Jebediah Kerman", detail: "trait=Pilot");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec8");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.KerbalHire, action.Type);
            Assert.Equal("Jebediah Kerman", action.KerbalName);
            Assert.Equal("Pilot", action.KerbalRole);
        }

        // ================================================================
        // ContractAccepted -> ContractAccept
        // ================================================================

        [Fact]
        public void ConvertEvent_ContractAccepted_ReturnsContractAccept()
        {
            var evt = MakeEvent(GameStateEventType.ContractAccepted, 8000.0,
                key: "guid-1234", detail: "Explore the Mun");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec9");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.ContractAccept, action.Type);
            Assert.Equal("guid-1234", action.ContractId);
            Assert.Equal("Explore the Mun", action.ContractTitle);
        }

        // ================================================================
        // ContractCompleted -> ContractComplete
        // ================================================================

        [Fact]
        public void ConvertEvent_ContractCompleted_ReturnsContractComplete()
        {
            var evt = MakeEvent(GameStateEventType.ContractCompleted, 9000.0,
                key: "guid-5678", detail: "Orbit Kerbin");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec10");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.ContractComplete, action.Type);
            Assert.Equal("guid-5678", action.ContractId);
        }

        // ================================================================
        // ContractFailed -> ContractFail
        // ================================================================

        [Fact]
        public void ConvertEvent_ContractFailed_ReturnsContractFail()
        {
            var evt = MakeEvent(GameStateEventType.ContractFailed, 10000.0,
                key: "guid-9999", detail: "Failed Mission");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec11");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.ContractFail, action.Type);
            Assert.Equal("guid-9999", action.ContractId);
        }

        // ================================================================
        // ContractCancelled -> ContractCancel
        // ================================================================

        [Fact]
        public void ConvertEvent_ContractCancelled_ReturnsContractCancel()
        {
            var evt = MakeEvent(GameStateEventType.ContractCancelled, 11000.0,
                key: "guid-0001");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec12");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.ContractCancel, action.Type);
            Assert.Equal("guid-0001", action.ContractId);
        }

        // ================================================================
        // Skipped event types -> null
        // ================================================================

        [Theory]
        [InlineData(GameStateEventType.FundsChanged)]
        [InlineData(GameStateEventType.ScienceChanged)]
        [InlineData(GameStateEventType.ReputationChanged)]
        [InlineData(GameStateEventType.CrewStatusChanged)]
        [InlineData(GameStateEventType.CrewRemoved)]
        [InlineData(GameStateEventType.ContractOffered)]
        [InlineData(GameStateEventType.ContractDeclined)]
        [InlineData(GameStateEventType.FacilityDowngraded)]
        public void ConvertEvent_SkippedTypes_ReturnsNull(GameStateEventType type)
        {
            var evt = MakeEvent(type, 100.0, key: "test");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec");
            Assert.Null(action);
        }

        // ================================================================
        // ConvertEvents — batch with UT range filtering
        // ================================================================

        [Fact]
        public void ConvertEvents_FiltersByUtRange()
        {
            var events = new List<GameStateEvent>
            {
                MakeEvent(GameStateEventType.TechResearched, 50.0, key: "early", detail: "cost=10", recordingId: "rec"),
                MakeEvent(GameStateEventType.TechResearched, 100.0, key: "inRange", detail: "cost=20", recordingId: "rec"),
                MakeEvent(GameStateEventType.TechResearched, 200.0, key: "late", detail: "cost=30", recordingId: "rec"),
            };

            var actions = GameStateEventConverter.ConvertEvents(events, "rec", 100.0, 150.0);

            Assert.Single(actions);
            Assert.Equal("inRange", actions[0].NodeId);
        }

        [Fact]
        public void ConvertEvents_WhenRecordingIdSupplied_IgnoresOtherRecordingTags()
        {
            var events = new List<GameStateEvent>
            {
                MakeEvent(
                    GameStateEventType.MilestoneAchieved,
                    100.0,
                    key: "RecordsSpeed",
                    detail: "funds=2880",
                    recordingId: "rec-parent"),
                MakeEvent(
                    GameStateEventType.MilestoneAchieved,
                    100.0,
                    key: "RecordsSpeed",
                    detail: "funds=2880",
                    recordingId: "rec-child"),
                MakeEvent(
                    GameStateEventType.MilestoneAchieved,
                    100.0,
                    key: "RecordsSpeed",
                    detail: "funds=2880")
            };

            var actions = GameStateEventConverter.ConvertEvents(events, "rec-parent", 0.0, 200.0);

            var action = Assert.Single(actions);
            Assert.Equal(GameActionType.MilestoneAchievement, action.Type);
            Assert.Equal("RecordsSpeed", action.MilestoneId);
            Assert.Equal("rec-parent", action.RecordingId);
        }

        [Fact]
        public void ConvertEvents_SkipsNonConvertibleTypes()
        {
            var events = new List<GameStateEvent>
            {
                MakeEvent(GameStateEventType.TechResearched, 100.0, key: "tech1", detail: "cost=5", recordingId: "rec"),
                MakeEvent(GameStateEventType.FundsChanged, 100.0, key: "None", recordingId: "rec"),
                MakeEvent(GameStateEventType.ScienceChanged, 100.0, key: "None", recordingId: "rec"),
            };

            var actions = GameStateEventConverter.ConvertEvents(events, "rec", 0.0, 200.0);

            Assert.Single(actions);
            Assert.Equal(GameActionType.ScienceSpending, actions[0].Type);
        }

        [Fact]
        public void ConvertEvents_EmptyList_ReturnsEmpty()
        {
            var actions = GameStateEventConverter.ConvertEvents(
                new List<GameStateEvent>(), "rec", 0.0, 100.0);
            Assert.Empty(actions);
        }

        [Fact]
        public void ConvertEvents_NullList_ReturnsEmpty()
        {
            var actions = GameStateEventConverter.ConvertEvents(null, "rec", 0.0, 100.0);
            Assert.Empty(actions);
        }

        // ================================================================
        // ConvertEvents — logging
        // ================================================================

        [Fact]
        public void ConvertEvents_LogsSummary()
        {
            var events = new List<GameStateEvent>
            {
                MakeEvent(GameStateEventType.TechResearched, 100.0, key: "tech1", detail: "cost=5", recordingId: "recX"),
                MakeEvent(GameStateEventType.FundsChanged, 100.0, key: "None", recordingId: "recX"),
            };

            GameStateEventConverter.ConvertEvents(events, "recX", 0.0, 200.0);

            Assert.Contains(logLines, l =>
                l.Contains("[GameStateEventConverter]") && l.Contains("converted=1") && l.Contains("skipped=1"));
        }

        // ================================================================
        // ConvertScienceSubjects
        // ================================================================

        [Fact]
        public void ConvertScienceSubjects_ValidSubjects_ReturnsScienceEarnings()
        {
            var subjects = new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "crewReport@KerbinSrfLanded", science = 5.0f, recordingId = "rec" },
                new PendingScienceSubject { subjectId = "evaReport@MunSrfLandedMidlands", science = 8.0f, recordingId = "rec" },
            };

            var actions = GameStateEventConverter.ConvertScienceSubjects(subjects, "rec", 900.0, 1000.0);

            Assert.Equal(2, actions.Count);
            Assert.Equal(GameActionType.ScienceEarning, actions[0].Type);
            Assert.Equal("crewReport@KerbinSrfLanded", actions[0].SubjectId);
            Assert.Equal(5.0f, actions[0].ScienceAwarded);
            Assert.Equal(1000.0, actions[0].UT);
            Assert.Equal("rec", actions[0].RecordingId);
            Assert.Equal(900.0f, actions[0].StartUT);
            Assert.Equal(1000.0f, actions[0].EndUT);
        }

        [Fact]
        public void ConvertScienceSubjects_TaggedCaptureInsideRecordingWindow_PropagatesCaptureWindowAndScienceMethod()
        {
            var subjects = new List<PendingScienceSubject>
            {
                new PendingScienceSubject
                {
                    subjectId = "mysteryGoo@KerbinSrfLandedLaunchPad",
                    science = 1.5f,
                    captureUT = 117.8,
                    reasonKey = "ScienceTransmission",
                    recordingId = "rec"
                },
                new PendingScienceSubject
                {
                    subjectId = "temperatureScan@KerbinSrfLandedLaunchPad",
                    science = 1.2f,
                    captureUT = 194.6,
                    reasonKey = "VesselRecovery",
                    recordingId = "rec"
                }
            };

            var actions = GameStateEventConverter.ConvertScienceSubjects(subjects, "rec", 100.3, 248.8);

            Assert.Equal(2, actions.Count);
            Assert.Equal(117.8f, actions[0].StartUT);
            Assert.Equal(ScienceMethod.Transmitted, actions[0].Method);
            Assert.Equal(194.6f, actions[1].StartUT);
            Assert.Equal(ScienceMethod.Recovered, actions[1].Method);
            Assert.Equal(248.8f, actions[1].EndUT);
        }

        [Fact]
        public void ConvertScienceSubjects_TaggedCaptureBeforeRecordingStart_IsSkipped()
        {
            var subjects = new List<PendingScienceSubject>
            {
                new PendingScienceSubject
                {
                    subjectId = "goo@LaunchPad",
                    science = 1.5f,
                    captureUT = 88.7,
                    reasonKey = "ScienceTransmission",
                    recordingId = "rec"
                }
            };

            var actions = GameStateEventConverter.ConvertScienceSubjects(subjects, "rec", 100.3, 248.8);

            Assert.Empty(actions);
        }

        [Fact]
        public void ConvertScienceSubjects_TaggedCaptureNaN_IsSkipped()
        {
            var subjects = new List<PendingScienceSubject>
            {
                new PendingScienceSubject
                {
                    subjectId = "goo@LaunchPad",
                    science = 1.5f,
                    captureUT = double.NaN,
                    reasonKey = "ScienceTransmission",
                    recordingId = "rec"
                }
            };

            var actions = GameStateEventConverter.ConvertScienceSubjects(subjects, "rec", 100.3, 248.8);

            Assert.Empty(actions);
        }

        [Fact]
        public void ConvertScienceSubjects_TaggedCaptureInfinity_IsSkipped()
        {
            var subjects = new List<PendingScienceSubject>
            {
                new PendingScienceSubject
                {
                    subjectId = "goo@LaunchPad",
                    science = 1.5f,
                    captureUT = double.PositiveInfinity,
                    reasonKey = "ScienceTransmission",
                    recordingId = "rec"
                }
            };

            var actions = GameStateEventConverter.ConvertScienceSubjects(subjects, "rec", 100.3, 248.8);

            Assert.Empty(actions);
        }

        [Fact]
        public void ConvertScienceSubjects_TaggedCaptureNegative_IsSkipped()
        {
            var subjects = new List<PendingScienceSubject>
            {
                new PendingScienceSubject
                {
                    subjectId = "goo@LaunchPad",
                    science = 1.5f,
                    captureUT = -1.0,
                    reasonKey = "ScienceTransmission",
                    recordingId = "rec"
                }
            };

            var actions = GameStateEventConverter.ConvertScienceSubjects(subjects, "rec", 100.3, 248.8);

            Assert.Empty(actions);
        }

        [Fact]
        public void ConvertScienceSubjects_CrossRecordingCapture_IsSkipped()
        {
            var subjects = new List<PendingScienceSubject>
            {
                new PendingScienceSubject
                {
                    subjectId = "goo@LaunchPad",
                    science = 1.5f,
                    captureUT = 117.8,
                    reasonKey = "VesselRecovery",
                    recordingId = "rec-other"
                }
            };

            var actions = GameStateEventConverter.ConvertScienceSubjects(subjects, "rec", 100.3, 248.8);

            Assert.Empty(actions);
        }

        [Fact]
        public void ConvertScienceSubjects_UntaggedCaptureInsideRecordingWindow_UsesCaptureUt()
        {
            var subjects = new List<PendingScienceSubject>
            {
                new PendingScienceSubject
                {
                    subjectId = "goo@LaunchPad",
                    science = 1.5f,
                    captureUT = 117.8,
                    reasonKey = "VesselRecovery",
                    recordingId = ""
                }
            };

            var actions = GameStateEventConverter.ConvertScienceSubjects(subjects, "rec", 100.3, 248.8);

            Assert.Single(actions);
            Assert.Equal(117.8f, actions[0].StartUT);
            Assert.Equal(ScienceMethod.Recovered, actions[0].Method);
        }

        [Fact]
        public void ConvertScienceSubjects_UntaggedCaptureBeforeRecordingStart_IsSkipped()
        {
            var subjects = new List<PendingScienceSubject>
            {
                new PendingScienceSubject
                {
                    subjectId = "goo@LaunchPad",
                    science = 1.5f,
                    captureUT = 88.7,
                    reasonKey = "ScienceTransmission",
                    recordingId = ""
                }
            };

            var actions = GameStateEventConverter.ConvertScienceSubjects(subjects, "rec", 100.3, 248.8);

            Assert.Empty(actions);
        }

        [Fact]
        public void ConvertScienceSubjects_SkipsEmptySubjectId()
        {
            var subjects = new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "", science = 5.0f, recordingId = "rec" },
                new PendingScienceSubject { subjectId = "valid@subject", science = 3.0f, recordingId = "rec" },
            };

            var actions = GameStateEventConverter.ConvertScienceSubjects(subjects, "rec", 50.0, 100.0);

            Assert.Single(actions);
            Assert.Equal("valid@subject", actions[0].SubjectId);
        }

        [Fact]
        public void ConvertScienceSubjects_SkipsNonPositiveScience()
        {
            var subjects = new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "zero@subject", science = 0.0f, recordingId = "rec" },
                new PendingScienceSubject { subjectId = "negative@subject", science = -1.0f, recordingId = "rec" },
            };

            var actions = GameStateEventConverter.ConvertScienceSubjects(subjects, "rec", 50.0, 100.0);

            Assert.Empty(actions);
        }

        [Fact]
        public void ConvertScienceSubjects_NullList_ReturnsEmpty()
        {
            var actions = GameStateEventConverter.ConvertScienceSubjects(null, "rec", 50.0, 100.0);
            Assert.Empty(actions);
        }

        // ================================================================
        // ExtractDetail round-trip — verifies the detail parsing works
        // through the converter for cost fields
        // ================================================================

        [Fact]
        public void ConvertEvent_TechResearched_MultipleSemicolonFields()
        {
            var evt = MakeEvent(GameStateEventType.TechResearched, 100.0,
                key: "node1", detail: "cost=90;parts=fuelTankSmallFlat,fuelTank");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec");

            Assert.Equal(90f, action.Cost);
        }

        // ================================================================
        // Sequence number assignment
        // ================================================================

        [Fact]
        public void ConvertEvents_AssignsIncrementingSequenceNumbers()
        {
            var events = new List<GameStateEvent>
            {
                MakeEvent(GameStateEventType.TechResearched, 100.0, key: "tech1", detail: "cost=5", recordingId: "rec"),
                MakeEvent(GameStateEventType.TechResearched, 100.0, key: "tech2", detail: "cost=10", recordingId: "rec"),
                MakeEvent(GameStateEventType.CrewHired, 100.0, key: "Jeb", detail: "trait=Pilot", recordingId: "rec"),
            };

            var actions = GameStateEventConverter.ConvertEvents(events, "rec", 0.0, 200.0);

            Assert.Equal(3, actions.Count);
            Assert.Equal(1, actions[0].Sequence);
            Assert.Equal(2, actions[1].Sequence);
            Assert.Equal(3, actions[2].Sequence);
        }

        [Fact]
        public void ConvertEvents_SequenceSkipsNonConvertibleEvents()
        {
            var events = new List<GameStateEvent>
            {
                MakeEvent(GameStateEventType.TechResearched, 100.0, key: "tech1", detail: "cost=5", recordingId: "rec"),
                MakeEvent(GameStateEventType.FundsChanged, 100.0, key: "None", recordingId: "rec"),
                MakeEvent(GameStateEventType.TechResearched, 100.0, key: "tech2", detail: "cost=10", recordingId: "rec"),
            };

            var actions = GameStateEventConverter.ConvertEvents(events, "rec", 0.0, 200.0);

            Assert.Equal(2, actions.Count);
            Assert.Equal(1, actions[0].Sequence);
            Assert.Equal(2, actions[1].Sequence);
        }

        [Fact]
        public void ConvertEvents_SequenceSkipsOutOfRangeEvents()
        {
            var events = new List<GameStateEvent>
            {
                MakeEvent(GameStateEventType.TechResearched, 50.0, key: "early", detail: "cost=5", recordingId: "rec"),
                MakeEvent(GameStateEventType.TechResearched, 100.0, key: "first", detail: "cost=10", recordingId: "rec"),
                MakeEvent(GameStateEventType.TechResearched, 150.0, key: "second", detail: "cost=15", recordingId: "rec"),
            };

            var actions = GameStateEventConverter.ConvertEvents(events, "rec", 100.0, 200.0);

            Assert.Equal(2, actions.Count);
            Assert.Equal(1, actions[0].Sequence);
            Assert.Equal(2, actions[1].Sequence);
        }

        [Fact]
        public void ConvertScienceSubjects_AssignsIncrementingSequenceNumbers()
        {
            var subjects = new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "crewReport@KerbinSrfLanded", science = 5.0f, recordingId = "rec" },
                new PendingScienceSubject { subjectId = "evaReport@MunSrfLanded", science = 8.0f, recordingId = "rec" },
                new PendingScienceSubject { subjectId = "temperatureScan@KerbinFlyingLow", science = 3.0f, recordingId = "rec" },
            };

            var actions = GameStateEventConverter.ConvertScienceSubjects(subjects, "rec", 900.0, 1000.0);

            Assert.Equal(3, actions.Count);
            Assert.Equal(1, actions[0].Sequence);
            Assert.Equal(2, actions[1].Sequence);
            Assert.Equal(3, actions[2].Sequence);
        }

        [Fact]
        public void ConvertScienceSubjects_SequenceSkipsInvalidSubjects()
        {
            var subjects = new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "valid1@subject", science = 5.0f, recordingId = "rec" },
                new PendingScienceSubject { subjectId = "", science = 3.0f, recordingId = "rec" },
                new PendingScienceSubject { subjectId = "zero@subject", science = 0.0f, recordingId = "rec" },
                new PendingScienceSubject { subjectId = "valid2@subject", science = 8.0f, recordingId = "rec" },
            };

            var actions = GameStateEventConverter.ConvertScienceSubjects(subjects, "rec", 900.0, 1000.0);

            Assert.Equal(2, actions.Count);
            Assert.Equal(1, actions[0].Sequence);
            Assert.Equal(2, actions[1].Sequence);
        }

        [Fact]
        public void ConvertScienceSubjects_PropagatesSubjectMaxValue()
        {
            var subjects = new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "crewReport@KerbinSrfLanded", science = 5.0f, subjectMaxValue = 10.0f, recordingId = "rec" },
                new PendingScienceSubject { subjectId = "evaReport@MunSrfLanded", science = 8.0f, subjectMaxValue = 24.0f, recordingId = "rec" },
            };

            var actions = GameStateEventConverter.ConvertScienceSubjects(subjects, "rec", 900.0, 1000.0);

            Assert.Equal(2, actions.Count);
            Assert.Equal(10.0f, actions[0].SubjectMaxValue);
            Assert.Equal(24.0f, actions[1].SubjectMaxValue);
        }

        // ================================================================
        // ContractAccepted — structured format (new)
        // ================================================================

        [Fact]
        public void ConvertContractAccepted_NewFormat_ExtractsDeadlineAndPenalties()
        {
            var evt = MakeEvent(GameStateEventType.ContractAccepted, 8000.0,
                key: "guid-1234",
                detail: "title=Orbit the Mun;deadline=50000;type=ExploreBody;failFunds=12000;failRep=5");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec9");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.ContractAccept, action.Type);
            Assert.Equal("guid-1234", action.ContractId);
            Assert.Equal("Orbit the Mun", action.ContractTitle);
            Assert.Equal("ExploreBody", action.ContractType);
            Assert.Equal(50000f, action.DeadlineUT);
            Assert.Equal(12000f, action.FundsPenalty);
            Assert.Equal(5f, action.RepPenalty);
        }

        [Fact]
        public void ConvertContractAccepted_NewFormat_DeadlineNaN()
        {
            var evt = MakeEvent(GameStateEventType.ContractAccepted, 8000.0,
                key: "guid-5678",
                detail: "title=Test Part;deadline=NaN;failFunds=3000;failRep=1");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec10");

            Assert.NotNull(action);
            Assert.Equal("Test Part", action.ContractTitle);
            Assert.True(float.IsNaN(action.DeadlineUT));
            Assert.Equal(3000f, action.FundsPenalty);
            Assert.Equal(1f, action.RepPenalty);
        }

        [Fact]
        public void ConvertContractAccepted_OldFormat_TreatsDetailAsTitle()
        {
            // Legacy format: plain title, no semicolons
            var evt = MakeEvent(GameStateEventType.ContractAccepted, 8000.0,
                key: "guid-9999", detail: "Explore the Mun");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec11");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.ContractAccept, action.Type);
            Assert.Equal("guid-9999", action.ContractId);
            Assert.Equal("Explore the Mun", action.ContractTitle);
            Assert.True(float.IsNaN(action.DeadlineUT));
            Assert.Equal(0f, action.FundsPenalty);
            Assert.Equal(0f, action.RepPenalty);
        }

        [Fact]
        public void ConvertContractAccepted_NewFormat_LogsStructuredParsing()
        {
            var evt = MakeEvent(GameStateEventType.ContractAccepted, 8000.0,
                key: "guid-1234",
                detail: "title=Test;deadline=50000;type=PartTest;failFunds=1000;failRep=2");
            GameStateEventConverter.ConvertEvent(evt, "rec");

            Assert.Contains(logLines, l =>
                l.Contains("[GameStateEventConverter]") &&
                l.Contains("structured format") &&
                l.Contains("guid-1234") &&
                l.Contains("type='PartTest'") &&
                l.Contains("typeSource=detail"));
        }

        [Fact]
        public void ConvertContractAccepted_OldFormat_LogsLegacy()
        {
            var evt = MakeEvent(GameStateEventType.ContractAccepted, 8000.0,
                key: "guid-old", detail: "Some Old Title");
            GameStateEventConverter.ConvertEvent(evt, "rec");

            Assert.Contains(logLines, l =>
                l.Contains("[GameStateEventConverter]") &&
                l.Contains("legacy format") &&
                l.Contains("guid-old"));
        }

        [Fact]
        public void ConvertContractAccepted_V3Format_PopulatesAdvanceFunds()
        {
            // Regression for codex review [P1] on PR #307: contract advance was dropped
            // at capture time — ConvertContractAccepted had no funds= field to read, so
            // FundsModule never credited the advance and funds under-counted on every
            // accept.
            var evt = MakeEvent(GameStateEventType.ContractAccepted, 8000.0,
                key: "guid-adv",
                detail: "title=Suborbital Flight;deadline=50000;funds=8450.5;failFunds=4000;failRep=3");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec-adv");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.ContractAccept, action.Type);
            Assert.Equal("Suborbital Flight", action.ContractTitle);
            Assert.Equal(8450.5f, action.AdvanceFunds);
            Assert.Equal(4000f, action.FundsPenalty);
            Assert.Equal(3f, action.RepPenalty);
        }

        [Fact]
        public void ConvertContractAccepted_V2FormatWithoutFundsKey_DefaultsAdvanceToZero()
        {
            // Backward compat: old detail strings did not have funds=. Must still parse
            // the rest of the fields and leave AdvanceFunds at 0 (not throw, not NaN).
            var evt = MakeEvent(GameStateEventType.ContractAccepted, 8000.0,
                key: "guid-v2",
                detail: "title=Old Format;deadline=NaN;failFunds=1000;failRep=1");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec-v2");

            Assert.NotNull(action);
            Assert.Equal("Old Format", action.ContractTitle);
            Assert.Equal(0f, action.AdvanceFunds);
            Assert.Equal(1000f, action.FundsPenalty);
        }

        [Fact]
        public void ConvertContractAccepted_V3FormatZeroAdvance_StaysZero()
        {
            // Some contracts (tutorials, strategies) have zero advance — must round-trip cleanly.
            var evt = MakeEvent(GameStateEventType.ContractAccepted, 8000.0,
                key: "guid-zero",
                detail: "title=Free;deadline=NaN;funds=0;failFunds=0;failRep=0");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec");

            Assert.NotNull(action);
            Assert.Equal(0f, action.AdvanceFunds);
        }

        [Fact]
        public void ConvertContractAccepted_WhenDetailOmitsType_BackfillsFromSnapshot()
        {
            var snapshot = new ConfigNode("CONTRACT");
            snapshot.AddValue("type", "PartTest");
            GameStateStore.AddContractSnapshot("guid-snap", snapshot);

            var evt = MakeEvent(GameStateEventType.ContractAccepted, 8000.0,
                key: "guid-snap",
                detail: "title=Test Part;deadline=NaN;funds=0;failFunds=3000;failRep=1");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec-snap");

            Assert.NotNull(action);
            Assert.Equal("Test Part", action.ContractTitle);
            Assert.Equal("PartTest", action.ContractType);
            Assert.Contains(logLines, l =>
                l.Contains("[GameStateEventConverter]") &&
                l.Contains("guid-snap") &&
                l.Contains("typeSource=snapshot"));
        }

        // ================================================================
        // MilestoneAchieved -> MilestoneAchievement
        // ================================================================

        [Fact]
        public void ConvertEvent_MilestoneAchieved_ReturnsMilestoneAchievement()
        {
            var evt = MakeEvent(GameStateEventType.MilestoneAchieved, 2000.0,
                key: "FirstOrbitKerbin", detail: "funds=5000;rep=10");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec1");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.MilestoneAchievement, action.Type);
            Assert.Equal("FirstOrbitKerbin", action.MilestoneId);
            Assert.Equal(5000f, action.MilestoneFundsAwarded);
            Assert.Equal(10f, action.MilestoneRepAwarded);
            Assert.Equal(2000.0, action.UT);
            Assert.Equal("rec1", action.RecordingId);
        }

        [Fact]
        public void ConvertEvent_MilestoneAchieved_NoRewards_DefaultsToZero()
        {
            var evt = MakeEvent(GameStateEventType.MilestoneAchieved, 3000.0,
                key: "ReachSpace", detail: "");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec2");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.MilestoneAchievement, action.Type);
            Assert.Equal("ReachSpace", action.MilestoneId);
            Assert.Equal(0f, action.MilestoneFundsAwarded);
            Assert.Equal(0f, action.MilestoneRepAwarded);
        }

        [Fact]
        public void ConvertEvent_MilestoneAchieved_PartialRewards()
        {
            var evt = MakeEvent(GameStateEventType.MilestoneAchieved, 4000.0,
                key: "FirstLaunch", detail: "funds=1000");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec3");

            Assert.NotNull(action);
            Assert.Equal(1000f, action.MilestoneFundsAwarded);
            Assert.Equal(0f, action.MilestoneRepAwarded);
            Assert.Equal(0f, action.MilestoneScienceAwarded);
        }

        [Fact]
        public void ConvertEvent_MilestoneAchieved_WithScience_PopulatesScienceAwarded()
        {
            // Regression for codex review [P2] on PR #307: milestone sci= in detail was
            // silently dropped — ConvertMilestoneAchieved read only funds/rep.
            var evt = MakeEvent(GameStateEventType.MilestoneAchieved, 5000.0,
                key: "Kerbin/Science",
                detail: "funds=0;rep=0;sci=2");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec");

            Assert.NotNull(action);
            Assert.Equal("Kerbin/Science", action.MilestoneId);
            Assert.Equal(2f, action.MilestoneScienceAwarded);
        }

        [Fact]
        public void ConvertEvent_MilestoneAchieved_AllThreeRewards()
        {
            var evt = MakeEvent(GameStateEventType.MilestoneAchieved, 6000.0,
                key: "Kerbin/Landing",
                detail: "funds=3000;rep=5;sci=1.5");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec");

            Assert.Equal(3000f, action.MilestoneFundsAwarded);
            Assert.Equal(5f, action.MilestoneRepAwarded);
            Assert.Equal(1.5f, action.MilestoneScienceAwarded);
        }

        [Fact]
        public void ConvertEvent_MilestoneAchieved_BackwardCompatNoSciKey()
        {
            // Pre-fix detail strings had no sci= key. Must still parse funds/rep and
            // leave MilestoneScienceAwarded at 0.
            var evt = MakeEvent(GameStateEventType.MilestoneAchieved, 7000.0,
                key: "Legacy",
                detail: "funds=2000;rep=3");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec");

            Assert.Equal(2000f, action.MilestoneFundsAwarded);
            Assert.Equal(3f, action.MilestoneRepAwarded);
            Assert.Equal(0f, action.MilestoneScienceAwarded);
        }

        [Fact]
        public void ConvertEvents_IncludesMilestoneAchieved()
        {
            var events = new List<GameStateEvent>
            {
                MakeEvent(GameStateEventType.MilestoneAchieved, 100.0,
                    key: "FirstLaunch", detail: "", recordingId: "rec"),
                MakeEvent(GameStateEventType.TechResearched, 150.0,
                    key: "basicRocketry", detail: "cost=5", recordingId: "rec"),
            };

            var actions = GameStateEventConverter.ConvertEvents(events, "rec", 0.0, 200.0);

            Assert.Equal(2, actions.Count);
            Assert.Equal(GameActionType.MilestoneAchievement, actions[0].Type);
            Assert.Equal(GameActionType.ScienceSpending, actions[1].Type);
        }

        // ================================================================
        // KerbalRescued -> KerbalRescue
        // ================================================================

        [Fact]
        public void ConvertKerbalRescued_ExtractsNameAndTrait()
        {
            var evt = MakeEvent(GameStateEventType.KerbalRescued, 12000.0,
                key: "Valentina Kerman", detail: "trait=Pilot");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec13");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.KerbalRescue, action.Type);
            Assert.Equal("Valentina Kerman", action.KerbalName);
            Assert.Equal("Pilot", action.KerbalRole);
            Assert.Equal(12000.0, action.UT);
            Assert.Equal("rec13", action.RecordingId);
        }

        [Fact]
        public void ConvertKerbalRescued_MissingTrait_DefaultsEmpty()
        {
            var evt = MakeEvent(GameStateEventType.KerbalRescued, 13000.0,
                key: "Bob Kerman", detail: "");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec14");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.KerbalRescue, action.Type);
            Assert.Equal("Bob Kerman", action.KerbalName);
            Assert.Null(action.KerbalRole);
        }

        // ================================================================
        // #438 gap #5 (converter-side half): world-first / progress reward
        // enriched detail produced by ProgressRewardPatch must convert into a
        // MilestoneAchievement action with all three *Awarded fields populated.
        // The detail shape is the one GameStateRecorder.BuildMilestoneDetail
        // emits: "funds=<val>;rep=<val>;sci=<val>". Key carries the qualified
        // milestone id (e.g. "RecordsSpeed/Kerbin") produced by
        // GameStateRecorder.QualifyMilestoneId.
        // ================================================================

        [Fact]
        public void ConvertMilestoneAchieved_WorldFirstProgressReward_PopulatesAwardedFields()
        {
            // Shape matches GameStateRecorder.EmitStandaloneProgressReward +
            // BuildMilestoneDetail output for a RecordsSpeed world-first over Kerbin.
            var evt = MakeEvent(GameStateEventType.MilestoneAchieved, 100.0,
                key: "RecordsSpeed/Kerbin",
                detail: "funds=4800;rep=2;sci=0");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec-wf");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.MilestoneAchievement, action.Type);
            Assert.Equal("RecordsSpeed/Kerbin", action.MilestoneId);
            Assert.Equal(4800f, action.MilestoneFundsAwarded);
            Assert.Equal(2f, action.MilestoneRepAwarded);
            Assert.Equal(0f, action.MilestoneScienceAwarded);
            Assert.Equal(100.0, action.UT);
            Assert.Equal("rec-wf", action.RecordingId);
        }
    }
}
