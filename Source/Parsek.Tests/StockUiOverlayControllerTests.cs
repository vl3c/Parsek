using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class StockUiOverlayControllerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorGameStateStoreSuppress;
        private readonly bool priorRecordingStoreSuppress;
        private readonly ParsekSettings priorSettingsOverride;

        public StockUiOverlayControllerTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorGameStateStoreSuppress = GameStateStore.SuppressLogging;
            priorRecordingStoreSuppress = RecordingStore.SuppressLogging;
            priorSettingsOverride = ParsekSettings.CurrentOverrideForTesting;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;

            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings
            {
                showCommittedFutureOverlays = true,
                blockCommittedActions = true
            };
        }

        public void Dispose()
        {
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekSettings.CurrentOverrideForTesting = priorSettingsOverride;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            GameStateStore.SuppressLogging = priorGameStateStoreSuppress;
            RecordingStore.SuppressLogging = priorRecordingStoreSuppress;
        }

        /// <summary>
        /// Zero entries. Fails if the helper NREs on cold start or fabricates sandbox marks.
        /// </summary>
        [Fact]
        public void BuildTechMarks_EmptyStore_EmptyDict()
        {
            var marks = StockUiOverlayController.BuildTechMarks();

            Assert.Empty(marks);
        }

        /// <summary>
        /// Single unreplayed event (any UT, including past-relative-to-liveUT) with a recordingId carries through to the mark. Fails if the helper accidentally introduces a UT cutoff (the bug a reviewer caught).
        /// </summary>
        [Fact]
        public void BuildTechMarks_UnreplayedEvent_PopulatedWithUtAndRecordingId()
        {
            AddCommittedRecording("rec_visible", "Mun Lander v3");
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.TechResearched, "advRocketry", ut: 42.0, recordingId: "rec_visible"),
                recordingId: "rec_visible");

            var marks = StockUiOverlayController.BuildTechMarks();

            var mark = Assert.Single(marks).Value;
            Assert.Equal("advRocketry", mark.TechId);
            Assert.Equal(42.0, mark.UT);
            Assert.Equal("rec_visible", mark.RecordingId);
            Assert.Contains("Committed at UT 42", mark.Tooltip);
            Assert.Contains("recording 'Mun Lander v3'", mark.Tooltip);
        }

        /// <summary>
        /// Event is at or before LastReplayedEventIndex for its milestone. Excluded. Fails if the cursor is off-by-one.
        /// </summary>
        [Fact]
        public void BuildTechMarks_AlreadyReplayedEvent_NotIncluded()
        {
            AddMilestone(
                lastReplayedIndex: 0,
                Event(GameStateEventType.TechResearched, "basicScience"));

            var marks = StockUiOverlayController.BuildTechMarks();

            Assert.Empty(marks);
        }

        /// <summary>
        /// Event in a milestone hidden by IsEventVisibleToCurrentTimeline. Excluded. Fails if the helper bypasses the visibility filter.
        /// </summary>
        [Fact]
        public void BuildTechMarks_HiddenByTimelineFilter_NotIncluded()
        {
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.TechResearched, "hiddenTech", recordingId: "rec_hidden"),
                recordingId: "rec_hidden");

            var marks = StockUiOverlayController.BuildTechMarks();

            Assert.Empty(marks);
        }

        /// <summary>
        /// Reflection assertion guards directly against reintroducing a liveUt cutoff parameter; a behavioral test cannot fail if the helper has no UT input to filter on.
        /// </summary>
        [Fact]
        public void BuildTechMarks_SignatureHasNoLiveUtParameter()
        {
            var methods = typeof(StockUiOverlayController).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Where(m => m.Name == "BuildTechMarks")
                .ToList();

            Assert.Contains(methods, m => m.GetParameters().Length == 0);
            Assert.DoesNotContain(methods, m => m.GetParameters().Any(p =>
                p.ParameterType == typeof(double)
                || string.Equals(p.Name, "liveUt", StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// When both predicates fire, FutureHired wins (it carries a UT; Reserved does not). Fails if reservation-only state masks committed future hires.
        /// </summary>
        [Fact]
        public void BuildApplicantMarks_PrefersFutureHiredOverReserved()
        {
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.CrewHired, "Valentina Kerman", ut: 123.0));

            var marks = StockUiOverlayController.BuildApplicantMarks(
                new[] { "Valentina Kerman" },
                _ => KerbalReservationKind.ReservedActive);

            var mark = Assert.Single(marks).Value;
            Assert.Equal(ApplicantOverlayKind.FutureHired, mark.Kind);
            Assert.Equal(123.0, mark.UT);
        }

        /// <summary>
        /// ReservedActive tooltip should identify the Parsek slot owner per §5.2. Fails if the overlay falls back to an ambiguous reservation message when slot ownership is available.
        /// </summary>
        [Fact]
        public void BuildApplicantMarks_ReservedActiveTooltipIncludesSlotOwner()
        {
            var marks = StockUiOverlayController.BuildApplicantMarks(
                new[] { "Stand-In Kerman" },
                _ => KerbalReservationKind.ReservedActive,
                _ => "Jebediah Kerman");

            var mark = Assert.Single(marks).Value;
            Assert.Equal(ApplicantOverlayKind.ReservedActive, mark.Kind);
            Assert.Equal("Reserved by Parsek for slot 'Jebediah Kerman'", mark.Tooltip);
        }

        /// <summary>
        /// Current ContractSystem.Instance.Contracts contains the same Guid in Active state; the controller suppresses the overlay and logs. Fails if active contracts still get a redundant badge.
        /// </summary>
        [Fact]
        public void BuildContractMarks_AlreadyActive_SuppressedWithLog()
        {
            string key = Guid.NewGuid().ToString();
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.ContractAccepted, key, ut: 200.0));
            var marks = StockUiOverlayController.BuildContractMarks();

            var filtered = StockUiOverlayController.SuppressAlreadyActiveContractMarks(
                marks,
                new HashSet<string>(new[] { key }, StringComparer.Ordinal));

            Assert.Empty(filtered);
            Assert.Contains(logLines, line =>
                line.Contains("[VERBOSE][StockUiOverlay]") &&
                line.Contains("BuildContractMarks: suppressed already-active committed contract count=1"));
        }

        /// <summary>
        /// Log-assertion test. Fails if the already-active suppression path stops emitting the Verbose count required for diagnosis.
        /// </summary>
        [Fact]
        public void BuildContractMarks_LogsSuppressionCountAtVerbose()
        {
            string keyA = Guid.NewGuid().ToString();
            string keyB = Guid.NewGuid().ToString();
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.ContractAccepted, keyA),
                Event(GameStateEventType.ContractAccepted, keyB));
            var marks = StockUiOverlayController.BuildContractMarks();

            StockUiOverlayController.SuppressAlreadyActiveContractMarks(
                marks,
                new HashSet<string>(new[] { keyA, keyB }, StringComparer.Ordinal));

            Assert.Contains(logLines, line =>
                line.Contains("[VERBOSE][StockUiOverlay]") &&
                line.Contains("BuildContractMarks: suppressed already-active committed contract count=2"));
        }

        /// <summary>
        /// E1: Sandbox mode has no committed timeline. Fails if an empty committed store produces visual candidates.
        /// </summary>
        [Fact]
        public void E1_SandboxMode_NoCommittedMarks()
        {
            Assert.Empty(StockUiOverlayController.BuildTechMarks());
            Assert.Empty(StockUiOverlayController.BuildContractMarks());
            Assert.Empty(StockUiOverlayController.BuildApplicantMarks(
                new[] { "Jebediah Kerman" },
                _ => KerbalReservationKind.NotManaged));
        }

        /// <summary>
        /// E2: Science mode mirrors Career for tech and kerbal hire, while contracts no-op with an empty ContractSystem surface. Fails if missing contract surface data fabricates marks after suppression.
        /// </summary>
        [Fact]
        public void E2_ScienceMode_EmptyContractSurfaceSuppressesContractMarks()
        {
            string key = Guid.NewGuid().ToString();
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.TechResearched, "scienceTech"),
                Event(GameStateEventType.CrewHired, "Scientist Kerman"),
                Event(GameStateEventType.ContractAccepted, key));

            var contractMarks = StockUiOverlayController.FilterContractMarksToVisibleContracts(
                StockUiOverlayController.BuildContractMarks(),
                new HashSet<string>(StringComparer.Ordinal));

            Assert.Contains("scienceTech", StockUiOverlayController.BuildTechMarks().Keys);
            Assert.Contains(
                "Scientist Kerman",
                StockUiOverlayController.BuildApplicantMarks(
                    new[] { "Scientist Kerman" },
                    _ => KerbalReservationKind.NotManaged).Keys);
            Assert.Empty(contractMarks);
        }

        /// <summary>
        /// E3: Early-load seeding can produce an empty first pass. Fails if a later helper rebuild cannot see newly-added unreplayed events.
        /// </summary>
        [Fact]
        public void E3_EmptyInitialMarks_RebuildAfterTimelineDataIncludesNewEvent()
        {
            Assert.Empty(StockUiOverlayController.BuildTechMarks());

            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.TechResearched, "rebuildTech"));

            Assert.Contains("rebuildTech", StockUiOverlayController.BuildTechMarks().Keys);
        }

        /// <summary>
        /// E4: Once replay advances past the source event, the next rebuild strips the candidate. Fails if the mark builder ignores LastReplayedEventIndex.
        /// </summary>
        [Fact]
        public void E4_ReplayedEventDisappearsOnNextBuild()
        {
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.TechResearched, "replayedTech"));
            Assert.Contains("replayedTech", StockUiOverlayController.BuildTechMarks().Keys);

            MilestoneStore.ResetForTesting();
            AddMilestone(
                lastReplayedIndex: 0,
                Event(GameStateEventType.TechResearched, "replayedTech"));

            Assert.Empty(StockUiOverlayController.BuildTechMarks());
        }

        /// <summary>
        /// E7: Two committed-future events for the same key use the earliest UT and note (+N more committed). Fails if duplicate events overwrite with a later time or hide the duplicate count.
        /// </summary>
        [Fact]
        public void E7_DuplicateCommittedEvents_UsesEarliestUtAndNotesAdditionalCount()
        {
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.TechResearched, "dupeTech", ut: 200.0),
                Event(GameStateEventType.TechResearched, "dupeTech", ut: 100.0));

            var mark = StockUiOverlayController.BuildTechMarks()["dupeTech"];

            Assert.Equal(100.0, mark.UT);
            Assert.Equal(1, mark.AdditionalCommittedCount);
            Assert.Contains("(+1 more committed)", mark.Tooltip);
        }

        /// <summary>
        /// E8: Deleted recording reference degrades to a UT-only tooltip with one Verbose log. Fails if a missing recording causes a crash or stale recording name.
        /// </summary>
        [Fact]
        public void E8_DeletedRecording_TooltipFallsBackToUtOnlyAndLogs()
        {
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.TechResearched, "missingRecTech", ut: 12345.0),
                recordingId: "missing-rec");

            var mark = StockUiOverlayController.BuildTechMarks()["missingRecTech"];

            Assert.Equal("Committed at UT 12345", mark.Tooltip);
            Assert.Contains(logLines, line =>
                line.Contains("[VERBOSE][StockUiOverlay]") &&
                line.Contains("recording 'missing-rec' not found for overlay tooltip"));
        }

        /// <summary>
        /// E9: A committed-future accepted contract missing from ContractSystem.Instance.Contracts is suppressed with one Verbose log. Fails if missing offers still decorate.
        /// </summary>
        [Fact]
        public void E9_MissingOfferedContract_SuppressesOverlayAndLogs()
        {
            string key = Guid.NewGuid().ToString();
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.ContractAccepted, key));

            var visible = StockUiOverlayController.FilterContractMarksToVisibleContracts(
                StockUiOverlayController.BuildContractMarks(),
                new HashSet<string>(StringComparer.Ordinal));

            Assert.Empty(visible);
            Assert.Contains(logLines, line =>
                line.Contains("[VERBOSE][StockUiOverlay]") &&
                line.Contains("BuildContractMarks: suppressed missing offered contract count=1"));
        }

        /// <summary>
        /// E10: Overlay setting off skips decoration while click-blocks remain separate. Fails if the master overlay toggle is ignored.
        /// </summary>
        [Fact]
        public void E10_OverlaySettingDisabled_ShouldApplyOverlaysFalse()
        {
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings
            {
                showCommittedFutureOverlays = false,
                blockCommittedActions = true
            };

            Assert.False(StockUiOverlayController.ShouldApplyOverlays());
        }

        /// <summary>
        /// E15: FutureRetired is sourced from CrewRemoved. Fails if retire overlays read CrewHired or the converted ledger instead of MilestoneStore's raw CrewRemoved event.
        /// </summary>
        [Fact]
        public void E15_FutureRetiredApplicant_UsesCrewRemovedKind()
        {
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.CrewRemoved, "Retiree Kerman", ut: 300.0));

            var mark = StockUiOverlayController.BuildApplicantMarks(
                new[] { "Retiree Kerman" },
                _ => KerbalReservationKind.NotManaged)["Retiree Kerman"];

            Assert.Equal(ApplicantOverlayKind.FutureRetired, mark.Kind);
            Assert.Equal(300.0, mark.UT);
        }

        /// <summary>
        /// E17: FutureHired is suppressed when the same name is already in Crew or Tourist. Fails if a redundant already-live hire badge remains visible.
        /// </summary>
        [Fact]
        public void E17_FutureHiredAlreadyLive_SuppressedFromApplicantMarks()
        {
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.CrewHired, "Live Kerman"));
            var marks = StockUiOverlayController.BuildApplicantMarks(
                new[] { "Live Kerman" },
                _ => KerbalReservationKind.NotManaged);

            var filtered = StockUiOverlayController.SuppressFutureHiredApplicantsAlreadyInLiveRoster(
                marks,
                new HashSet<string>(new[] { "Live Kerman" }, StringComparer.Ordinal));

            Assert.Empty(filtered);
            Assert.Contains(logLines, line =>
                line.Contains("[VERBOSE][StockUiOverlay]") &&
                line.Contains("BuildApplicantMarks: suppressed already-live future hire name=Live Kerman"));
        }

        /// <summary>
        /// E18: Tech overlay candidates must equal the click-block predicate set. Fails if the overlay adds a candidate-side filter not present in GetCommittedTechIds().
        /// </summary>
        [Fact]
        public void E18_BuildTechMarks_CandidateSetMatchesMilestoneHelper()
        {
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.TechResearched, "techA"),
                Event(GameStateEventType.TechResearched, "techB"));

            AssertSameSet(
                MilestoneStore.GetCommittedTechIds(),
                StockUiOverlayController.BuildTechMarks_Candidates().Keys);
        }

        /// <summary>
        /// E18: Contract overlay candidates must equal the click-block predicate set before visible/Active UI-surface suppression. Fails if the overlay candidate layer drifts from GetCommittedContractAcceptIds().
        /// </summary>
        [Fact]
        public void E18_BuildContractMarks_CandidateSetMatchesMilestoneHelper()
        {
            string keyA = Guid.NewGuid().ToString();
            string keyB = Guid.NewGuid().ToString();
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.ContractAccepted, keyA),
                Event(GameStateEventType.ContractAccepted, keyB));

            AssertSameSet(
                MilestoneStore.GetCommittedContractAcceptIds(),
                StockUiOverlayController.BuildContractMarks_Candidates().Keys);
        }

        /// <summary>
        /// E18: FutureHired applicant candidates must equal the click-block predicate set before already-live suppression. Fails if overlay-only applicant kinds leak into the hire click-block parity check.
        /// </summary>
        [Fact]
        public void E18_BuildApplicantMarks_FutureHireCandidateSetMatchesMilestoneHelper()
        {
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.CrewHired, "Future A"),
                Event(GameStateEventType.CrewHired, "Future B"),
                Event(GameStateEventType.CrewRemoved, "Retired Only"));

            var helperNames = MilestoneStore.GetCommittedKerbalHireNames();
            var candidateNames = helperNames
                .Concat(new[] { "Retired Only", "Reserved Only" })
                .ToArray();
            var marks = StockUiOverlayController.BuildApplicantMarks_Candidates(
                candidateNames,
                name => name == "Reserved Only"
                    ? KerbalReservationKind.ReservedActive
                    : KerbalReservationKind.NotManaged);
            var futureHireNames = new HashSet<string>(
                marks.Where(kvp => kvp.Value.Kind == ApplicantOverlayKind.FutureHired).Select(kvp => kvp.Key),
                StringComparer.Ordinal);

            AssertSameSet(helperNames, futureHireNames);
        }

        private static GameStateEvent Event(
            GameStateEventType type,
            string key,
            double ut = 100.0,
            string recordingId = null)
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = type,
                key = key,
                recordingId = recordingId
            };
        }

        private static void AddMilestone(
            int lastReplayedIndex,
            GameStateEvent event0,
            string recordingId = null,
            bool committed = true)
        {
            AddMilestone(lastReplayedIndex, new[] { event0 }, recordingId, committed);
        }

        private static void AddMilestone(
            int lastReplayedIndex,
            GameStateEvent event0,
            GameStateEvent event1,
            string recordingId = null,
            bool committed = true)
        {
            AddMilestone(lastReplayedIndex, new[] { event0, event1 }, recordingId, committed);
        }

        private static void AddMilestone(
            int lastReplayedIndex,
            GameStateEvent event0,
            GameStateEvent event1,
            GameStateEvent event2,
            string recordingId = null,
            bool committed = true)
        {
            AddMilestone(lastReplayedIndex, new[] { event0, event1, event2 }, recordingId, committed);
        }

        private static void AddMilestone(
            int lastReplayedIndex,
            GameStateEvent[] events,
            string recordingId = null,
            bool committed = true)
        {
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = Guid.NewGuid().ToString("N"),
                RecordingId = recordingId,
                Committed = committed,
                LastReplayedEventIndex = lastReplayedIndex,
                Events = new List<GameStateEvent>(events)
            });
        }

        private static void AddCommittedRecording(string recordingId, string vesselName)
        {
            RecordingStore.AddCommittedInternal(new Recording
            {
                RecordingId = recordingId,
                VesselName = vesselName,
                MergeState = MergeState.Immutable
            });
        }

        private static void AssertSameSet(IEnumerable<string> expected, IEnumerable<string> actual)
        {
            Assert.Equal(
                expected.OrderBy(value => value, StringComparer.Ordinal),
                actual.OrderBy(value => value, StringComparer.Ordinal));
        }
    }
}
