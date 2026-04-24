using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class TimelineBuilderTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public TimelineBuilderTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekTimeFormat.KerbinTimeOverrideForTesting = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            ParsekTimeFormat.ResetForTesting();
        }

        private static Recording MakeRecording(string vesselName, double startUT, double endUT,
            TerminalState? terminal = null, string recordingId = null, bool playbackEnabled = true,
            bool hidden = false, string chainId = null, int chainIndex = -1, int chainBranch = 0)
        {
            var rec = new Recording();
            rec.ExplicitStartUT = startUT;
            rec.ExplicitEndUT = endUT;
            rec.VesselName = vesselName;
            rec.TerminalStateValue = terminal;
            rec.RecordingId = recordingId ?? Guid.NewGuid().ToString("N");
            rec.PlaybackEnabled = playbackEnabled;
            rec.Hidden = hidden;
            rec.ChainId = chainId;
            rec.ChainIndex = chainIndex;
            rec.ChainBranch = chainBranch;
            return rec;
        }

        // ================================================================
        // 1. Empty inputs
        // ================================================================

        [Fact]
        public void EmptyInputs_ProducesEmptyList()
        {
            var result = TimelineBuilder.Build(
                new List<Recording>(),
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);

            Assert.Empty(result);
        }

        // ================================================================
        // 2. Single recording produces Start + End + Spawn
        // ================================================================

        [Fact]
        public void SingleRecording_ProducesStartEndSpawn()
        {
            var rec = MakeRecording("Flea I", 100, 500);
            var result = TimelineBuilder.Build(
                new List<Recording> { rec },
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);

            Assert.Equal(2, result.Count);

            var start = result.First(e => e.Type == TimelineEntryType.RecordingStart);
            Assert.Equal(100, start.UT);
            Assert.Equal("Flea I", start.VesselName);
            Assert.Equal(TimelineSource.Recording, start.Source);

            var spawn = result.First(e => e.Type == TimelineEntryType.VesselSpawn);
            Assert.Equal(500, spawn.UT); // EndUT — vessel materializes after ghost playback
            Assert.Equal("Flea I", spawn.VesselName);
        }

        // ================================================================
        // 3. Hidden recording skipped
        // ================================================================

        [Fact]
        public void HiddenRecording_Skipped()
        {
            var rec = MakeRecording("Hidden Ship", 100, 200, hidden: true);
            var result = TimelineBuilder.Build(
                new List<Recording> { rec },
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);

            Assert.Empty(result);
        }

        // ================================================================
        // 4. PlaybackDisabled still emits VesselSpawn (bug #433)
        // ================================================================

        [Fact]
        public void PlaybackDisabled_StillEmitsVesselSpawn()
        {
            // The PlaybackEnabled toggle is visual-only: the vessel still spawns
            // in-world at ghost-end, so the career timeline must still show that
            // materialization. Bug #433.
            var rec = MakeRecording("Ghost Ship", 100, 200, playbackEnabled: false);
            var result = TimelineBuilder.Build(
                new List<Recording> { rec },
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);

            Assert.Equal(2, result.Count);
            Assert.Contains(result, e => e.Type == TimelineEntryType.RecordingStart);
            Assert.Contains(result, e => e.Type == TimelineEntryType.VesselSpawn);
        }

        [Fact]
        public void TerminalSpawnSuperseded_SuppressesVesselSpawnEntry()
        {
            var rec = MakeRecording("Butterfly Rover", 100, 200,
                terminal: TerminalState.Landed);
            rec.TerminalSpawnSupersededByRecordingId = "continued-butterfly";

            var result = TimelineBuilder.Build(
                new List<Recording> { rec },
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);

            Assert.Contains(result, e => e.Type == TimelineEntryType.RecordingStart);
            Assert.DoesNotContain(result, e => e.Type == TimelineEntryType.VesselSpawn);
        }

        // ================================================================
        // 5. UT sort order across sources
        // ================================================================

        [Fact]
        public void UTSortOrderAcrossSources()
        {
            var rec = MakeRecording("Rocket", 100, 500);
            var actions = new List<GameAction>
            {
                new GameAction { UT = 200, Type = GameActionType.ScienceEarning, Effective = true },
                new GameAction { UT = 300, Type = GameActionType.FundsEarning, Effective = true }
            };

            var result = TimelineBuilder.Build(
                new List<Recording> { rec },
                actions,
                new List<Milestone>(),
                _ => true);

            // Verify ascending UT order
            for (int i = 1; i < result.Count; i++)
            {
                Assert.True(result[i].UT >= result[i - 1].UT,
                    $"Entry at index {i} (UT={result[i].UT}) should be >= entry at index {i - 1} (UT={result[i - 1].UT})");
            }
        }

        // ================================================================
        // 6. GameAction types map correctly
        // ================================================================

        [Fact]
        public void GameActionTypesMapCorrectly()
        {
            var actions = new List<GameAction>
            {
                new GameAction { UT = 100, Type = GameActionType.ScienceEarning, Effective = true },
                new GameAction { UT = 200, Type = GameActionType.MilestoneAchievement, Effective = true, MilestoneId = "test" },
                new GameAction { UT = 300, Type = GameActionType.FacilityUpgrade, Effective = true, FacilityId = "LaunchPad" }
            };

            var result = TimelineBuilder.Build(
                new List<Recording>(),
                actions,
                new List<Milestone>(),
                _ => true);

            Assert.Equal(3, result.Count);
            Assert.Equal(TimelineEntryType.ScienceEarning, result[0].Type);
            Assert.Equal(TimelineEntryType.MilestoneAchievement, result[1].Type);
            Assert.Equal(TimelineEntryType.FacilityUpgrade, result[2].Type);
        }

        // ================================================================
        // 7. ScienceInitial custom text
        // ================================================================

        [Fact]
        public void ScienceInitialCustomText()
        {
            var actions = new List<GameAction>
            {
                new GameAction { UT = 50, Type = GameActionType.ScienceInitial, InitialScience = 150.5f, Effective = true }
            };

            var result = TimelineBuilder.Build(
                new List<Recording>(),
                actions,
                new List<Milestone>(),
                _ => true);

            Assert.Single(result);
            Assert.Equal("Starting science: 150.5", result[0].DisplayText);
        }

        // ================================================================
        // 8. ReputationInitial custom text
        // ================================================================

        [Fact]
        public void ReputationInitialCustomText()
        {
            var actions = new List<GameAction>
            {
                new GameAction { UT = 50, Type = GameActionType.ReputationInitial, InitialReputation = 25f, Effective = true }
            };

            var result = TimelineBuilder.Build(
                new List<Recording>(),
                actions,
                new List<Milestone>(),
                _ => true);

            Assert.Single(result);
            Assert.Equal("Starting reputation: 25", result[0].DisplayText);
        }

        [Fact]
        public void MilestoneText_IncludesAllRewardLegs()
        {
            string text = TimelineEntryDisplay.GetGameActionText(
                new GameAction
                {
                    Type = GameActionType.MilestoneAchievement,
                    MilestoneId = "Kerbin/RecordsDistance",
                    MilestoneFundsAwarded = 4800,
                    MilestoneRepAwarded = 2f,
                    MilestoneScienceAwarded = 1.5f
                },
                vesselName: null);

            Assert.Equal("Milestone: Kerbin - Records Distance +4800 funds +2 rep +1.5 sci", text);
        }

        // ================================================================
        // 9. Ineffective T1 demoted to T2
        // ================================================================

        [Fact]
        public void IneffectiveT1DemotedToT2()
        {
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 100,
                    Type = GameActionType.MilestoneAchievement,
                    Effective = false,
                    MilestoneId = "DuplicateMilestone"
                }
            };

            var result = TimelineBuilder.Build(
                new List<Recording>(),
                actions,
                new List<Milestone>(),
                _ => true);

            Assert.Single(result);
            Assert.Equal(SignificanceTier.T2, result[0].Tier);
            Assert.False(result[0].IsEffective);
        }

        [Fact]
        public void AdjacentSameMilestoneSameUt_AreCompactedIntoSingleRicherEntry()
        {
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 100,
                    Type = GameActionType.MilestoneAchievement,
                    Effective = true,
                    MilestoneId = "Kerbin/RecordsDistance",
                    MilestoneFundsAwarded = 4800
                },
                new GameAction
                {
                    UT = 100,
                    Type = GameActionType.MilestoneAchievement,
                    Effective = false,
                    MilestoneId = "Kerbin/RecordsDistance",
                    MilestoneFundsAwarded = 4800,
                    MilestoneRepAwarded = 2f,
                    MilestoneScienceAwarded = 1.5f
                }
            };

            var result = TimelineBuilder.Build(
                new List<Recording>(),
                actions,
                new List<Milestone>(),
                _ => true);

            var milestone = Assert.Single(result);
            Assert.Equal(TimelineEntryType.MilestoneAchievement, milestone.Type);
            Assert.Equal("Milestone: Kerbin - Records Distance +4800 funds +2 rep +1.5 sci", milestone.DisplayText);
            Assert.True(milestone.IsEffective);
            Assert.Equal(SignificanceTier.T1, milestone.Tier);
        }

        [Fact]
        public void SameMilestoneDifferentUt_AreNotCompacted()
        {
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 100,
                    Type = GameActionType.MilestoneAchievement,
                    Effective = true,
                    MilestoneId = "Kerbin/RecordsDistance",
                    MilestoneFundsAwarded = 4800
                },
                new GameAction
                {
                    UT = 101,
                    Type = GameActionType.MilestoneAchievement,
                    Effective = true,
                    MilestoneId = "Kerbin/RecordsDistance",
                    MilestoneFundsAwarded = 4800,
                    MilestoneRepAwarded = 2f
                }
            };

            var result = TimelineBuilder.Build(
                new List<Recording>(),
                actions,
                new List<Milestone>(),
                _ => true);

            Assert.Equal(2, result.Count);
        }

        [Fact]
        public void SameMilestoneWithinPointOneSeconds_AreCompacted()
        {
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 100.0,
                    Type = GameActionType.MilestoneAchievement,
                    Effective = true,
                    MilestoneId = "Kerbin/RecordsDistance",
                    MilestoneFundsAwarded = 4800
                },
                new GameAction
                {
                    UT = 100.05,
                    Type = GameActionType.MilestoneAchievement,
                    Effective = false,
                    MilestoneId = "Kerbin/RecordsDistance",
                    MilestoneFundsAwarded = 4800,
                    MilestoneRepAwarded = 2f
                }
            };

            var result = TimelineBuilder.Build(
                new List<Recording>(),
                actions,
                new List<Milestone>(),
                _ => true);

            var milestone = Assert.Single(result);
            Assert.Equal("Milestone: Kerbin - Records Distance +4800 funds +2 rep", milestone.DisplayText);
            Assert.True(milestone.IsEffective);
            Assert.Equal(SignificanceTier.T1, milestone.Tier);
        }

        [Fact]
        public void SameMilestoneWithinPointOneSeconds_IgnoresInterleavedNonMilestone()
        {
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 100.0,
                    Type = GameActionType.MilestoneAchievement,
                    Effective = true,
                    MilestoneId = "Kerbin/RecordsDistance",
                    MilestoneFundsAwarded = 4800
                },
                new GameAction
                {
                    UT = 100.02,
                    Type = GameActionType.ScienceEarning,
                    SubjectId = "crewReport@KerbinSrfLandedLaunchPad",
                    ScienceAwarded = 1f
                },
                new GameAction
                {
                    UT = 100.04,
                    Type = GameActionType.MilestoneAchievement,
                    Effective = false,
                    MilestoneId = "Kerbin/RecordsDistance",
                    MilestoneFundsAwarded = 4800,
                    MilestoneRepAwarded = 2f,
                    MilestoneScienceAwarded = 1.5f
                }
            };

            var result = TimelineBuilder.Build(
                new List<Recording>(),
                actions,
                new List<Milestone>(),
                _ => true);

            Assert.Equal(2, result.Count);
            Assert.Contains(result, e => e.Type == TimelineEntryType.MilestoneAchievement &&
                e.DisplayText == "Milestone: Kerbin - Records Distance +4800 funds +2 rep +1.5 sci");
            Assert.Contains(result, e => e.Type == TimelineEntryType.ScienceEarning);
        }

        [Fact]
        public void AdjacentSameMilestoneSameUt_WithConflictingRewardLegs_AreNotCompacted()
        {
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 100,
                    Type = GameActionType.MilestoneAchievement,
                    Effective = true,
                    MilestoneId = "Kerbin/RecordsDistance",
                    MilestoneFundsAwarded = 4800
                },
                new GameAction
                {
                    UT = 100,
                    Type = GameActionType.MilestoneAchievement,
                    Effective = true,
                    MilestoneId = "Kerbin/RecordsDistance",
                    MilestoneFundsAwarded = 9600
                }
            };

            var result = TimelineBuilder.Build(
                new List<Recording>(),
                actions,
                new List<Milestone>(),
                _ => true);

            Assert.Equal(2, result.Count);
            Assert.Contains(result, e => e.DisplayText == "Milestone: Kerbin - Records Distance +4800 funds");
            Assert.Contains(result, e => e.DisplayText == "Milestone: Kerbin - Records Distance +9600 funds");
        }

        // ================================================================
        // 10. Ineffective T2 stays T2
        // ================================================================

        [Fact]
        public void IneffectiveT2StaysT2()
        {
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 100,
                    Type = GameActionType.ScienceEarning,
                    Effective = false,
                    SubjectId = "test"
                }
            };

            var result = TimelineBuilder.Build(
                new List<Recording>(),
                actions,
                new List<Milestone>(),
                _ => true);

            Assert.Single(result);
            Assert.Equal(SignificanceTier.T2, result[0].Tier);
            Assert.False(result[0].IsEffective);
        }

        // ================================================================
        // 11. Tier classification — all T1 types
        // ================================================================

        [Fact]
        public void TierClassification_AllT1Types()
        {
            var t1Types = new[]
            {
                TimelineEntryType.RecordingStart,
                TimelineEntryType.VesselSpawn,
                TimelineEntryType.CrewDeath,
                TimelineEntryType.MilestoneAchievement,
                TimelineEntryType.ContractComplete,
                TimelineEntryType.ContractFail,
                TimelineEntryType.FacilityUpgrade,
                TimelineEntryType.FacilityDestruction,
                TimelineEntryType.KerbalHire,
                TimelineEntryType.FundsInitial,
                TimelineEntryType.ScienceInitial,
                TimelineEntryType.ReputationInitial
            };

            foreach (var type in t1Types)
            {
                var tier = TimelineEntryDisplay.GetTier(type);
                Assert.True(tier == SignificanceTier.T1,
                    $"Expected T1 for {type} but got {tier}");
            }

            Assert.Equal(12, t1Types.Length);
        }

        // ================================================================
        // 12. Tier classification — all T2 types
        // ================================================================

        [Fact]
        public void TierClassification_AllT2Types()
        {
            var t2Types = new[]
            {
                TimelineEntryType.ScienceEarning,
                TimelineEntryType.ScienceSpending,
                TimelineEntryType.FundsEarning,
                TimelineEntryType.FundsSpending,
                TimelineEntryType.ReputationEarning,
                TimelineEntryType.ReputationPenalty,
                TimelineEntryType.ContractAccept,
                TimelineEntryType.ContractCancel,
                TimelineEntryType.KerbalAssignment,
                TimelineEntryType.KerbalRescue,
                TimelineEntryType.KerbalStandIn,
                TimelineEntryType.FacilityRepair,
                TimelineEntryType.StrategyActivate,
                TimelineEntryType.StrategyDeactivate,
                TimelineEntryType.LegacyEvent
            };

            foreach (var type in t2Types)
            {
                var tier = TimelineEntryDisplay.GetTier(type);
                Assert.True(tier == SignificanceTier.T2,
                    $"Expected T2 for {type} but got {tier}");
            }

            Assert.Equal(15, t2Types.Length);
        }

        // ================================================================
        // 12b. FormatDuration produces correct human-readable text
        // ================================================================

        [Theory]
        [InlineData(0, "")]
        [InlineData(30, "30s")]
        [InlineData(60, "1m")]
        [InlineData(90, "1m, 30s")]
        [InlineData(3600, "1h")]
        [InlineData(21600, "1d")]           // 6h = 1 KSP day
        [InlineData(9201600, "1y")]         // 426 * 6h * 3600 = 9201600s
        [InlineData(9201600 + 21600 + 3661, "1y, 1d, 1h, 1m, 1s")]
        public void FormatDuration_ProducesCorrectText(double seconds, string expected)
        {
            Assert.Equal(expected, TimelineEntryDisplay.FormatDuration(seconds));
        }

        // ================================================================
        // 13. Ghost chain window — two members
        // ================================================================

        [Fact]
        public void ChainRecording_LaunchShowsFullChainDuration()
        {
            string chainId = "chain-abc";
            // Chain spans UT 100-400 (300 seconds = 5m)
            var rec0 = MakeRecording("Orbiter", 100, 200, chainId: chainId, chainIndex: 0, chainBranch: 0);
            var rec1 = MakeRecording("Orbiter", 200, 400, chainId: chainId, chainIndex: 1, chainBranch: 0);

            var result = TimelineBuilder.Build(
                new List<Recording> { rec0, rec1 },
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);

            // Only the chain root (index 0) gets a Launch entry.
            // Chain children (index > 0) are optimizer splits, not player-visible launches.
            var starts = result.Where(e => e.Type == TimelineEntryType.RecordingStart).ToList();
            Assert.Single(starts);

            // Root launch shows full chain duration (300s = 5m)
            Assert.Contains("MET 5m", starts[0].DisplayText);
        }

        // ================================================================
        // 14. Chain duration — branch excluded from duration calc
        // ================================================================

        [Fact]
        public void ChainDuration_BranchExcluded()
        {
            string chainId = "chain-xyz";
            // Branch 0: UT 100-200 (100s), Branch 1: UT 200-400 (ignored)
            var rec0 = MakeRecording("Main Ship", 100, 200, chainId: chainId, chainIndex: 0, chainBranch: 0);
            var rec1 = MakeRecording("Branch Ship", 200, 400, chainId: chainId, chainIndex: 1, chainBranch: 1);

            var result = TimelineBuilder.Build(
                new List<Recording> { rec0, rec1 },
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);

            // rec0 (branch 0) should show duration of branch 0 only (100s = 1m, 40s)
            var start0 = result.First(e => e.Type == TimelineEntryType.RecordingStart && e.VesselName == "Main Ship");
            Assert.Contains("MET 1m, 40s", start0.DisplayText);
        }

        // ================================================================
        // 15. Mid-chain recording — no VesselSpawn
        // ================================================================

        [Fact]
        public void MidChainRecording_NoVesselSpawn()
        {
            string chainId = "chain-mid";
            var rec0 = MakeRecording("Staged Rocket", 100, 200, chainId: chainId, chainIndex: 0, chainBranch: 0);
            var rec1 = MakeRecording("Staged Rocket", 200, 400, chainId: chainId, chainIndex: 1, chainBranch: 0);

            var result = TimelineBuilder.Build(
                new List<Recording> { rec0, rec1 },
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);

            var spawns = result.Where(e => e.Type == TimelineEntryType.VesselSpawn).ToList();

            // Index 0 is mid-chain (has successor index 1) — should NOT spawn
            // Index 1 is last in chain — SHOULD spawn at its EndUT
            Assert.Single(spawns);
            Assert.Equal(400, spawns[0].UT); // rec1's EndUT
        }

        // ================================================================
        // 16. Legacy events appear at T2
        // ================================================================

        [Fact]
        public void LegacyEvents_AppearAtT2()
        {
            var milestone = new Milestone
            {
                Committed = true,
                Epoch = 0,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 150,
                        eventType = GameStateEventType.ContractCompleted,
                        key = "test-contract"
                    }
                }
            };

            var result = TimelineBuilder.Build(
                new List<Recording>(),
                new List<GameAction>(),
                new List<Milestone> { milestone },
                _ => true);

            Assert.Single(result);
            Assert.Equal(TimelineEntryType.LegacyEvent, result[0].Type);
            Assert.Equal(SignificanceTier.T2, result[0].Tier);
            Assert.Equal(TimelineSource.Legacy, result[0].Source);
        }

        // ================================================================
        // 17. Legacy events — hidden tagged rows filtered
        // ================================================================

        [Fact]
        public void LegacyEvents_HiddenTaggedRowsFiltered()
        {
            var milestone = new Milestone
            {
                Committed = true,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 150,
                        eventType = GameStateEventType.ContractCompleted,
                        key = "old-contract",
                        recordingId = "old-rec"
                    }
                }
            };

            var result = TimelineBuilder.Build(
                new List<Recording>(),
                new List<GameAction>(),
                new List<Milestone> { milestone },
                GameStateStore.IsEventVisibleToCurrentTimeline);

            Assert.Empty(result);
        }

        // ================================================================
        // 18. Legacy events — resource events filtered
        // ================================================================

        [Fact]
        public void LegacyEvents_ResourceEventsFiltered()
        {
            var milestone = new Milestone
            {
                Committed = true,
                Epoch = 0,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 150,
                        eventType = GameStateEventType.FundsChanged,
                        key = "funds-change"
                    }
                }
            };

            var result = TimelineBuilder.Build(
                new List<Recording>(),
                new List<GameAction>(),
                new List<Milestone> { milestone },
                _ => true);

            Assert.Empty(result);
        }

        [Fact]
        public void LegacyMilestoneAndStrategyDuplicates_AreFilteredWhenMatchingGameActionsExist()
        {
            var milestone = new Milestone
            {
                Committed = true,
                Epoch = 0,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 100,
                        eventType = GameStateEventType.MilestoneAchieved,
                        key = "Kerbin/FirstLaunch"
                    },
                    new GameStateEvent
                    {
                        ut = 200,
                        eventType = GameStateEventType.StrategyActivated,
                        key = "UnpaidInterns",
                        detail = "title=Unpaid Research Program"
                    },
                    new GameStateEvent
                    {
                        ut = 300,
                        eventType = GameStateEventType.StrategyDeactivated,
                        key = "UnpaidInterns",
                        detail = "title=Unpaid Research Program"
                    },
                    new GameStateEvent
                    {
                        ut = 400,
                        eventType = GameStateEventType.ContractCompleted,
                        key = "keep-contract"
                    }
                }
            };

            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 100,
                    Type = GameActionType.MilestoneAchievement,
                    Effective = true,
                    MilestoneId = "Kerbin/FirstLaunch",
                    MilestoneFundsAwarded = 960,
                    MilestoneRepAwarded = 0.5f
                },
                new GameAction
                {
                    UT = 200,
                    Type = GameActionType.StrategyActivate,
                    Effective = true,
                    StrategyId = "UnpaidInterns",
                    Commitment = 0.15f,
                    SourceResource = StrategyResource.Funds,
                    TargetResource = StrategyResource.Reputation
                },
                new GameAction
                {
                    UT = 300,
                    Type = GameActionType.StrategyDeactivate,
                    Effective = true,
                    StrategyId = "UnpaidInterns"
                }
            };

            var result = TimelineBuilder.Build(
                new List<Recording>(),
                actions,
                new List<Milestone> { milestone },
                _ => true);

            Assert.Contains(result, e =>
                e.Source == TimelineSource.GameAction &&
                e.Type == TimelineEntryType.MilestoneAchievement &&
                e.DisplayText.Contains("Milestone: Kerbin -") &&
                e.DisplayText.Contains("First Launch"));
            Assert.Contains(result, e =>
                e.Source == TimelineSource.GameAction &&
                e.Type == TimelineEntryType.StrategyActivate &&
                e.DisplayText.Contains("Activate: Unpaid Research Program"));
            Assert.Contains(result, e =>
                e.Source == TimelineSource.GameAction &&
                e.Type == TimelineEntryType.StrategyDeactivate &&
                e.DisplayText.Contains("Deactivate: Unpaid Research Program"));

            Assert.DoesNotContain(result, e =>
                e.Source == TimelineSource.Legacy &&
                e.DisplayText.Contains("\"Kerbin/FirstLaunch\" achieved"));
            Assert.DoesNotContain(result, e =>
                e.Source == TimelineSource.Legacy &&
                e.DisplayText.Contains("activated"));
            Assert.DoesNotContain(result, e =>
                e.Source == TimelineSource.Legacy &&
                e.DisplayText.Contains("deactivated"));

            var legacyEntries = result.Where(e => e.Source == TimelineSource.Legacy).ToList();
            var legacy = Assert.Single(legacyEntries);
            Assert.Equal(400, legacy.UT);
            Assert.Contains("Contract:", legacy.DisplayText);
        }

        [Fact]
        public void LegacyMilestoneAndStrategyEvents_AreNotFilteredForIneffectiveOrNonMatchingActions()
        {
            var milestone = new Milestone
            {
                Committed = true,
                Epoch = 0,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 100,
                        eventType = GameStateEventType.MilestoneAchieved,
                        key = "Kerbin/FirstLaunch"
                    },
                    new GameStateEvent
                    {
                        ut = 200,
                        eventType = GameStateEventType.StrategyActivated,
                        key = "UnpaidInterns",
                        detail = "title=Unpaid Research Program"
                    },
                    new GameStateEvent
                    {
                        ut = 300,
                        eventType = GameStateEventType.StrategyDeactivated,
                        key = "UnpaidInterns",
                        detail = "title=Unpaid Research Program"
                    }
                }
            };

            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 100,
                    Type = GameActionType.MilestoneAchievement,
                    Effective = false,
                    MilestoneId = "Kerbin/FirstLaunch"
                },
                new GameAction
                {
                    UT = 201,
                    Type = GameActionType.StrategyActivate,
                    Effective = true,
                    StrategyId = "UnpaidInterns",
                    Commitment = 0.15f,
                    SourceResource = StrategyResource.Funds,
                    TargetResource = StrategyResource.Reputation
                },
                new GameAction
                {
                    UT = 300,
                    Type = GameActionType.StrategyDeactivate,
                    Effective = true,
                    StrategyId = "OutreachProg"
                }
            };

            var result = TimelineBuilder.Build(
                new List<Recording>(),
                actions,
                new List<Milestone> { milestone },
                _ => true);

            Assert.Contains(result, e =>
                e.Source == TimelineSource.Legacy &&
                e.UT == 100 &&
                e.DisplayText.Contains("\"Kerbin/FirstLaunch\" achieved"));
            Assert.Contains(result, e =>
                e.Source == TimelineSource.Legacy &&
                e.UT == 200 &&
                e.DisplayText.Contains("activated"));
            Assert.Contains(result, e =>
                e.Source == TimelineSource.Legacy &&
                e.UT == 300 &&
                e.DisplayText.Contains("deactivated"));
        }

        // ================================================================
        // 19. GameAction RecordingId resolves vessel name
        // ================================================================

        [Fact]
        public void GameActionRecordingId_ResolvesVesselName()
        {
            var rec = MakeRecording("Mun Lander", 100, 500, recordingId: "rec-123");
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 200,
                    Type = GameActionType.ScienceEarning,
                    RecordingId = "rec-123",
                    Effective = true
                }
            };

            var result = TimelineBuilder.Build(
                new List<Recording> { rec },
                actions,
                new List<Milestone>(),
                _ => true);

            var scienceEntry = result.First(e => e.Type == TimelineEntryType.ScienceEarning);
            Assert.Equal("Mun Lander", scienceEntry.VesselName);
            Assert.Equal("rec-123", scienceEntry.RecordingId);
        }

        // ================================================================
        // 20. RecordingEnd terminal state text
        // ================================================================

        [Fact]
        public void VesselSpawn_TerminalStateText()
        {
            var recRecovered = MakeRecording("Ship A", 100, 200, terminal: TerminalState.Recovered);
            var recDestroyed = MakeRecording("Ship B", 100, 200, terminal: TerminalState.Destroyed);
            var recNone = MakeRecording("Ship C", 100, 200, terminal: null);

            var result = TimelineBuilder.Build(
                new List<Recording> { recRecovered, recDestroyed, recNone },
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);

            // Destroyed terminals are filtered — "Spawn: Destroyed" makes no sense
            var spawns = result.Where(e => e.Type == TimelineEntryType.VesselSpawn).ToList();
            Assert.Equal(2, spawns.Count);

            var spawnA = spawns.First(e => e.VesselName == "Ship A");
            Assert.Equal("Spawn: Ship A (Recovered)", spawnA.DisplayText);

            // Ship B (Destroyed) should NOT have a spawn entry
            Assert.DoesNotContain(spawns, e => e.VesselName == "Ship B");

            var spawnC = spawns.First(e => e.VesselName == "Ship C");
            Assert.Equal("Spawn: Ship C", spawnC.DisplayText);
        }

        // ================================================================
        // 21. Build logs summary
        // ================================================================

        [Fact]
        public void BuildLogsSummary()
        {
            var rec = MakeRecording("Logger Test", 100, 200);
            TimelineBuilder.Build(
                new List<Recording> { rec },
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);

            Assert.Contains(logLines, l =>
                l.Contains("[Timeline]") && l.Contains("Build complete"));
        }

        // ================================================================
        // 22. Integration — all sources merged
        // ================================================================

        [Fact]
        public void IntegrationTest_AllSourcesMerged()
        {
            var rec1 = MakeRecording("Alpha", 100, 300);
            var rec2 = MakeRecording("Beta", 400, 600);

            var actions = new List<GameAction>
            {
                new GameAction { UT = 150, Type = GameActionType.ScienceEarning, Effective = true },
                new GameAction { UT = 250, Type = GameActionType.ContractComplete, Effective = true },
                new GameAction { UT = 450, Type = GameActionType.FacilityUpgrade, Effective = true, FacilityId = "Pad" }
            };

            var milestone = new Milestone
            {
                Committed = true,
                Epoch = 1,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 350,
                        eventType = GameStateEventType.TechResearched,
                        key = "basicRocketry"
                    }
                }
            };

            var result = TimelineBuilder.Build(
                new List<Recording> { rec1, rec2 },
                actions,
                new List<Milestone> { milestone },
                _ => true);

            // 2 recordings x 2 entries each (Start + Spawn) = 4
            // 3 game actions = 3
            // 1 legacy event = 1
            // Total = 8
            Assert.Equal(8, result.Count);

            // Verify UT ordering
            for (int i = 1; i < result.Count; i++)
            {
                Assert.True(result[i].UT >= result[i - 1].UT,
                    $"Entry at index {i} (UT={result[i].UT}, {result[i].Type}) should be >= " +
                    $"entry at index {i - 1} (UT={result[i - 1].UT}, {result[i - 1].Type})");
            }

            // Verify source diversity
            Assert.Contains(result, e => e.Source == TimelineSource.Recording);
            Assert.Contains(result, e => e.Source == TimelineSource.GameAction);
            Assert.Contains(result, e => e.Source == TimelineSource.Legacy);
        }

        // ================================================================
        // 23. All terminal states produce correct text
        // ================================================================

        [Theory]
        [InlineData(TerminalState.Orbiting, null, "Spawn: TestVessel (Orbiting)")]  // no body fallback
        [InlineData(TerminalState.Landed, null, "Spawn: TestVessel (Landed)")]
        [InlineData(TerminalState.Splashed, null, "Spawn: TestVessel (Splashed)")]
        [InlineData(TerminalState.SubOrbital, null, "Spawn: TestVessel (Sub-orbital)")]
        [InlineData(TerminalState.Destroyed, null, "Spawn: TestVessel (Destroyed)")]
        [InlineData(TerminalState.Recovered, null, "Spawn: TestVessel (Recovered)")]
        [InlineData(TerminalState.Docked, null, "Spawn: TestVessel (Docked)")]
        [InlineData(TerminalState.Boarded, null, "Spawn: TestVessel (Boarded)")]
        [InlineData(TerminalState.Landed, "Landed on Mun", "Spawn: TestVessel (Landed on Mun)")]
        [InlineData(TerminalState.Orbiting, "Orbiting Kerbin", "Spawn: TestVessel (Orbiting Kerbin)")]
        public void VesselSpawn_AllTerminalStates(TerminalState state, string situation, string expectedText)
        {
            var text = TimelineEntryDisplay.GetVesselSpawnText("TestVessel", state, situation, false, null, null, null);
            Assert.Equal(expectedText, text);
        }

        // ================================================================
        // 24. MapGameActionType unknown type logs warning
        // ================================================================

        [Fact]
        public void MapGameActionType_UnknownType_LogsWarning()
        {
            var result = TimelineEntryDisplay.MapGameActionType((GameActionType)999);

            Assert.Equal(TimelineEntryType.LegacyEvent, result);
            Assert.Contains(logLines, l =>
                l.Contains("[Timeline]") && l.Contains("Unknown GameActionType"));
        }

        // ================================================================
        // 25. Recording with StartUT == EndUT produces valid entries
        // ================================================================

        [Fact]
        public void ZeroDurationRecording_ProducesValidEntries()
        {
            // Destroyed terminals no longer produce spawn entries, so use Landed
            var rec = MakeRecording("Instant", 100, 100, terminal: TerminalState.Landed);
            var result = TimelineBuilder.Build(
                new List<Recording> { rec },
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);

            Assert.Equal(2, result.Count); // Start + Spawn
            Assert.All(result, e => Assert.Equal(100.0, e.UT));
        }

        // ================================================================
        // 26. Hidden skip count logged
        // ================================================================

        [Fact]
        public void HiddenRecordings_LogSkipCount()
        {
            var visible = MakeRecording("Visible", 100, 200);
            var hidden1 = MakeRecording("Hidden1", 100, 200, hidden: true);
            var hidden2 = MakeRecording("Hidden2", 100, 200, hidden: true);

            TimelineBuilder.Build(
                new List<Recording> { visible, hidden1, hidden2 },
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);

            Assert.Contains(logLines, l =>
                l.Contains("[Timeline]") && l.Contains("hidden=2"));
        }

        // ================================================================
        // 27. HumanizeSubjectId
        // ================================================================

        [Theory]
        [InlineData("crewReport@KerbinSrfLaunchpad", "Crew Report @ Kerbin Launchpad")]
        [InlineData("mysteryGoo@MunSrfLandedMidlands", "Mystery Goo @ Mun Landed Midlands")]
        [InlineData("temperatureScan", "Temperature Scan")]
        [InlineData("", "")]
        [InlineData(null, null)]
        public void HumanizeSubjectId_FormatsCorrectly(string input, string expected)
        {
            Assert.Equal(expected, TimelineEntryDisplay.HumanizeSubjectId(input));
        }

        // ================================================================
        // 28. IsPlayerAction classification
        // ================================================================

        [Fact]
        public void IsPlayerAction_ClassifiesActionsCorrectly()
        {
            // Player actions (deliberate KSC choices)
            var actions = new[]
            {
                TimelineEntryType.ScienceSpending,
                TimelineEntryType.FundsSpending,
                TimelineEntryType.ContractAccept,
                TimelineEntryType.ContractCancel,
                TimelineEntryType.KerbalHire,
                TimelineEntryType.FacilityUpgrade,
                TimelineEntryType.FacilityRepair,
                TimelineEntryType.StrategyActivate,
                TimelineEntryType.StrategyDeactivate,
                TimelineEntryType.FundsInitial,
                TimelineEntryType.ScienceInitial,
                TimelineEntryType.ReputationInitial
            };
            foreach (var type in actions)
                Assert.True(TimelineEntryDisplay.IsPlayerAction(type), $"{type} should be a player action");

            // Events (gameplay consequences)
            var events = new[]
            {
                TimelineEntryType.ScienceEarning,
                TimelineEntryType.FundsEarning,
                TimelineEntryType.ReputationEarning,
                TimelineEntryType.ReputationPenalty,
                TimelineEntryType.MilestoneAchievement,
                TimelineEntryType.ContractComplete,
                TimelineEntryType.ContractFail,
                TimelineEntryType.FacilityDestruction,
                TimelineEntryType.KerbalAssignment,
                TimelineEntryType.KerbalRescue,
                TimelineEntryType.KerbalStandIn,
                TimelineEntryType.RecordingStart,
                TimelineEntryType.VesselSpawn,
                TimelineEntryType.CrewDeath,
                TimelineEntryType.LegacyEvent
            };
            foreach (var type in events)
                Assert.False(TimelineEntryDisplay.IsPlayerAction(type), $"{type} should NOT be a player action");
        }

        // ================================================================
        // 30. Tree recording with EVA branch — parent suppressed, leaf spawns (#227)
        // ================================================================

        [Fact]
        public void TreeRecordingWithEvaBranch_OnlyLeafSpawns()
        {
            string branchPointId = "bp-eva-001";
            uint vesselPid = 42;

            // Root recording: launched, then EVA at UT=300. Has ChildBranchPointId.
            var root = MakeRecording("Kerbal X", 100, 300, terminal: TerminalState.Landed);
            root.VesselPersistentId = vesselPid;
            root.ChildBranchPointId = branchPointId;

            // Continuation child: same vessel PID, picks up after EVA branch
            var continuation = MakeRecording("Kerbal X", 300, 600, terminal: TerminalState.Landed);
            continuation.VesselPersistentId = vesselPid;
            continuation.ParentBranchPointId = branchPointId;

            var result = TimelineBuilder.Build(
                new List<Recording> { root, continuation },
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);

            var spawns = result.Where(e => e.Type == TimelineEntryType.VesselSpawn).ToList();

            // Only the continuation leaf should spawn (at UT=600), not the root (UT=300)
            Assert.Single(spawns);
            Assert.Equal(600, spawns[0].UT);
        }

        // ================================================================
        // 31. Tree recording with breakup-only — effective leaf spawns (#224/#227)
        // ================================================================

        [Fact]
        public void TreeRecordingBreakupOnly_EffectiveLeafSpawns()
        {
            string branchPointId = "bp-breakup-001";
            uint vesselPid = 42;
            uint debrisPid = 99;

            // Root recording: breakup at UT=300, no same-PID continuation (debris only)
            var root = MakeRecording("Kerbal X", 100, 300, terminal: TerminalState.Splashed);
            root.VesselPersistentId = vesselPid;
            root.ChildBranchPointId = branchPointId;

            // Debris child: different vessel PID
            var debris = MakeRecording("Kerbal X Debris", 300, 310, terminal: TerminalState.Destroyed);
            debris.VesselPersistentId = debrisPid;
            debris.ParentBranchPointId = branchPointId;
            debris.IsDebris = true;

            var result = TimelineBuilder.Build(
                new List<Recording> { root, debris },
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);

            var spawns = result.Where(e => e.Type == TimelineEntryType.VesselSpawn).ToList();

            // Root IS the effective leaf (no same-PID continuation) — should spawn
            Assert.Single(spawns);
            Assert.Equal(300, spawns[0].UT);
            Assert.Equal("Kerbal X", spawns[0].VesselName);
        }

        // ================================================================
        // 32. HasSamePidTreeContinuation — direct unit tests
        // ================================================================

        [Fact]
        public void HasSamePidTreeContinuation_WithContinuation_ReturnsTrue()
        {
            string bpId = "bp-001";
            uint pid = 42;

            var parent = MakeRecording("Ship", 100, 200);
            parent.VesselPersistentId = pid;
            parent.ChildBranchPointId = bpId;

            var child = MakeRecording("Ship", 200, 400);
            child.VesselPersistentId = pid;
            child.ParentBranchPointId = bpId;

            Assert.True(TimelineBuilder.HasSamePidTreeContinuation(parent, new List<Recording> { parent, child }));
        }

        [Fact]
        public void HasSamePidTreeContinuation_DifferentPid_ReturnsFalse()
        {
            string bpId = "bp-001";

            var parent = MakeRecording("Ship", 100, 200);
            parent.VesselPersistentId = 42;
            parent.ChildBranchPointId = bpId;

            var child = MakeRecording("Debris", 200, 300);
            child.VesselPersistentId = 99;
            child.ParentBranchPointId = bpId;

            Assert.False(TimelineBuilder.HasSamePidTreeContinuation(parent, new List<Recording> { parent, child }));
        }

        [Fact]
        public void HasSamePidTreeContinuation_NoChildBranchPoint_ReturnsFalse()
        {
            var rec = MakeRecording("Ship", 100, 200);
            rec.VesselPersistentId = 42;
            // No ChildBranchPointId set

            Assert.False(TimelineBuilder.HasSamePidTreeContinuation(rec, new List<Recording> { rec }));
        }

        [Fact]
        public void HasSamePidTreeContinuation_ZeroPid_ReturnsFalse()
        {
            string bpId = "bp-001";

            var parent = MakeRecording("Ship", 100, 200);
            parent.VesselPersistentId = 0; // not set
            parent.ChildBranchPointId = bpId;

            var child = MakeRecording("Ship", 200, 300);
            child.VesselPersistentId = 0;
            child.ParentBranchPointId = bpId;

            Assert.False(TimelineBuilder.HasSamePidTreeContinuation(parent, new List<Recording> { parent, child }));
        }

        // ================================================================
        // 33. Multi-branch tree: EVA + staging, only final leaf spawns (#227)
        // ================================================================

        [Fact]
        public void MultiBranchTree_OnlyFinalLeafSpawns()
        {
            string bp1 = "bp-eva-001";
            string bp2 = "bp-stage-001";
            uint vesselPid = 42;

            // Root: launches, EVA at UT=200
            var root = MakeRecording("Kerbal X", 100, 200);
            root.VesselPersistentId = vesselPid;
            root.ChildBranchPointId = bp1;

            // Continuation after EVA: same PID, stages at UT=400
            var seg1 = MakeRecording("Kerbal X", 200, 400);
            seg1.VesselPersistentId = vesselPid;
            seg1.ParentBranchPointId = bp1;
            seg1.ChildBranchPointId = bp2;

            // Final continuation after staging: same PID, lands at UT=600
            var seg2 = MakeRecording("Kerbal X", 400, 600, terminal: TerminalState.Landed);
            seg2.VesselPersistentId = vesselPid;
            seg2.ParentBranchPointId = bp2;

            var result = TimelineBuilder.Build(
                new List<Recording> { root, seg1, seg2 },
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);

            var spawns = result.Where(e => e.Type == TimelineEntryType.VesselSpawn).ToList();

            // Only the final leaf (seg2) should spawn at UT=600
            Assert.Single(spawns);
            Assert.Equal(600, spawns[0].UT);
        }

        // ================================================================
        // 29. HumanizeStrategyId
        // ================================================================

        [Theory]
        [InlineData("AggressiveNeg", "Aggressive Negotiations")]
        [InlineData("PatentsLic", "Patents Licensing")]
        [InlineData("UnknownModStrategy", "Unknown Mod Strategy")]
        [InlineData(null, "unknown")]
        public void HumanizeStrategyId_MapsCorrectly(string input, string expected)
        {
            Assert.Equal(expected, TimelineEntryDisplay.HumanizeStrategyId(input));
        }

        // ================================================================
        // 30. Crew death entries from CrewEndStates (Bug #229)
        // ================================================================

        [Fact]
        public void CrewDeath_SingleDeadKerbal_ProducesEntry()
        {
            var rec = MakeRecording("Flea I", 100, 500, terminal: TerminalState.Destroyed);
            rec.CrewEndStates = new Dictionary<string, KerbalEndState>
            {
                { "Bob Kerman", KerbalEndState.Dead }
            };

            var result = TimelineBuilder.Build(
                new List<Recording> { rec },
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);

            var deaths = result.FindAll(e => e.Type == TimelineEntryType.CrewDeath);
            Assert.Single(deaths);
            Assert.Equal(500, deaths[0].UT);
            Assert.Equal("Lost: Bob Kerman (Flea I)", deaths[0].DisplayText);
            Assert.Equal(SignificanceTier.T1, deaths[0].Tier);
            Assert.Equal(TimelineSource.Recording, deaths[0].Source);
        }

        [Fact]
        public void CrewDeath_MultipleDeadKerbal_ProducesMultipleEntries()
        {
            var rec = MakeRecording("Doomed Ship", 100, 300, terminal: TerminalState.Destroyed);
            rec.CrewEndStates = new Dictionary<string, KerbalEndState>
            {
                { "Jebediah Kerman", KerbalEndState.Dead },
                { "Bill Kerman", KerbalEndState.Dead },
                { "Bob Kerman", KerbalEndState.Aboard } // survived somehow
            };

            var result = TimelineBuilder.Build(
                new List<Recording> { rec },
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);

            var deaths = result.FindAll(e => e.Type == TimelineEntryType.CrewDeath);
            Assert.Equal(2, deaths.Count);
            Assert.All(deaths, d => Assert.Equal(300, d.UT));
        }

        [Fact]
        public void CrewDeath_NoneDeadKerbal_NoEntry()
        {
            var rec = MakeRecording("Safe Ship", 100, 500, terminal: TerminalState.Landed);
            rec.CrewEndStates = new Dictionary<string, KerbalEndState>
            {
                { "Jebediah Kerman", KerbalEndState.Aboard }
            };

            var result = TimelineBuilder.Build(
                new List<Recording> { rec },
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);

            Assert.DoesNotContain(result, e => e.Type == TimelineEntryType.CrewDeath);
        }

        [Fact]
        public void CrewDeath_NullCrewEndStates_NoEntry()
        {
            var rec = MakeRecording("Legacy Ship", 100, 500);
            rec.CrewEndStates = null;

            var result = TimelineBuilder.Build(
                new List<Recording> { rec },
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);

            Assert.DoesNotContain(result, e => e.Type == TimelineEntryType.CrewDeath);
        }

        [Fact]
        public void CrewDeath_HiddenRecording_Skipped()
        {
            var rec = MakeRecording("Hidden Death", 100, 500, hidden: true);
            rec.CrewEndStates = new Dictionary<string, KerbalEndState>
            {
                { "Bob Kerman", KerbalEndState.Dead }
            };

            var result = TimelineBuilder.Build(
                new List<Recording> { rec },
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);

            Assert.DoesNotContain(result, e => e.Type == TimelineEntryType.CrewDeath);
        }

        [Fact]
        public void CrewDeath_LogsCount()
        {
            var rec = MakeRecording("Crash Ship", 100, 200, terminal: TerminalState.Destroyed);
            rec.CrewEndStates = new Dictionary<string, KerbalEndState>
            {
                { "Jeb", KerbalEndState.Dead }
            };

            TimelineBuilder.Build(
                new List<Recording> { rec },
                new List<GameAction>(),
                new List<Milestone>(),
                _ => true);

            Assert.Contains(logLines, l =>
                l.Contains("[Timeline]") && l.Contains("crewDeath=1"));
        }

        // ================================================================
        // 31. GetCrewDeathText
        // ================================================================

        [Theory]
        [InlineData("Bob Kerman", "Flea I", "Lost: Bob Kerman (Flea I)")]
        [InlineData("Jebediah Kerman", null, "Lost: Jebediah Kerman")]
        [InlineData("Jebediah Kerman", "", "Lost: Jebediah Kerman")]
        [InlineData(null, "Ship", "Lost: unknown (Ship)")]
        public void GetCrewDeathText_FormatsCorrectly(string kerbalName, string vesselName, string expected)
        {
            Assert.Equal(expected, TimelineEntryDisplay.GetCrewDeathText(kerbalName, vesselName));
        }

        // ================================================================
        // 32. EVA crew reassignment filtering (Bug #228)
        // ================================================================

        [Fact]
        public void EvaReassignment_FilteredAtBranchTime()
        {
            // Parent recording — the vessel crew EVAs from
            var parentRec = MakeRecording("Mun Lander", 100, 500, recordingId: "parent-rec");

            // EVA recording — starts at UT 300 (the EVA time)
            var evaRec = MakeRecording("Jeb", 300, 400, recordingId: "eva-rec");
            evaRec.EvaCrewName = "Jebediah Kerman";
            evaRec.ParentRecordingId = "parent-rec";
            evaRec.ParentBranchPointId = "bp-1"; // tree child

            // KSP auto-generates crew reassignment at the EVA time on the parent
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 300,
                    Type = GameActionType.KerbalAssignment,
                    RecordingId = "parent-rec",
                    KerbalName = "Bill Kerman",
                    KerbalRole = "Engineer",
                    Effective = true
                }
            };

            var result = TimelineBuilder.Build(
                new List<Recording> { parentRec, evaRec },
                actions,
                new List<Milestone>(),
                _ => true);

            // Bill's reassignment at UT=300 should be filtered (same UT + same recordingId as EVA parent)
            Assert.DoesNotContain(result, e => e.Type == TimelineEntryType.KerbalAssignment);
        }

        [Fact]
        public void EvaReassignment_DifferentUT_NotFiltered()
        {
            var parentRec = MakeRecording("Mun Lander", 100, 500, recordingId: "parent-rec");
            var evaRec = MakeRecording("Jeb", 300, 400, recordingId: "eva-rec");
            evaRec.EvaCrewName = "Jebediah Kerman";
            evaRec.ParentRecordingId = "parent-rec";
            evaRec.ParentBranchPointId = "bp-1";

            // Crew assignment at a DIFFERENT UT — should NOT be filtered
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 200, // not the EVA time
                    Type = GameActionType.KerbalAssignment,
                    RecordingId = "parent-rec",
                    KerbalName = "Bill Kerman",
                    KerbalRole = "Engineer",
                    Effective = true
                }
            };

            var result = TimelineBuilder.Build(
                new List<Recording> { parentRec, evaRec },
                actions,
                new List<Milestone>(),
                _ => true);

            Assert.Contains(result, e => e.Type == TimelineEntryType.KerbalAssignment);
        }

        [Fact]
        public void EvaReassignment_DifferentRecording_NotFiltered()
        {
            var parentRec = MakeRecording("Mun Lander", 100, 500, recordingId: "parent-rec");
            var otherRec = MakeRecording("Other Ship", 100, 500, recordingId: "other-rec");
            var evaRec = MakeRecording("Jeb", 300, 400, recordingId: "eva-rec");
            evaRec.EvaCrewName = "Jebediah Kerman";
            evaRec.ParentRecordingId = "parent-rec";
            evaRec.ParentBranchPointId = "bp-1";

            // Same UT but different recording — should NOT be filtered
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 300,
                    Type = GameActionType.KerbalAssignment,
                    RecordingId = "other-rec", // not the parent
                    KerbalName = "Val Kerman",
                    KerbalRole = "Pilot",
                    Effective = true
                }
            };

            var result = TimelineBuilder.Build(
                new List<Recording> { parentRec, otherRec, evaRec },
                actions,
                new List<Milestone>(),
                _ => true);

            Assert.Contains(result, e => e.Type == TimelineEntryType.KerbalAssignment);
        }

        [Fact]
        public void EvaReassignment_LogsFilterCount()
        {
            var parentRec = MakeRecording("Ship", 100, 500, recordingId: "parent-rec");
            var evaRec = MakeRecording("Jeb", 300, 400, recordingId: "eva-rec");
            evaRec.EvaCrewName = "Jeb";
            evaRec.ParentRecordingId = "parent-rec";
            evaRec.ParentBranchPointId = "bp-1";

            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 300, Type = GameActionType.KerbalAssignment,
                    RecordingId = "parent-rec", KerbalName = "Bill", KerbalRole = "Engineer",
                    Effective = true
                },
                new GameAction
                {
                    UT = 300, Type = GameActionType.KerbalAssignment,
                    RecordingId = "parent-rec", KerbalName = "Bob", KerbalRole = "Scientist",
                    Effective = true
                }
            };

            TimelineBuilder.Build(
                new List<Recording> { parentRec, evaRec },
                actions,
                new List<Milestone>(),
                _ => true);

            Assert.Contains(logLines, l =>
                l.Contains("[Timeline]") && l.Contains("Filtered 2 KerbalAssignment"));
        }

        // ================================================================
        // 33. BuildEvaBranchKeys / EncodeEvaBranchKey
        // ================================================================

        [Fact]
        public void BuildEvaBranchKeys_ReturnsEmptyForNoEva()
        {
            var rec = MakeRecording("Ship", 100, 200);
            var keys = TimelineBuilder.BuildEvaBranchKeys(new List<Recording> { rec });
            Assert.Empty(keys);
        }

        [Fact]
        public void BuildEvaBranchKeys_ReturnsKeyForEvaWithParent()
        {
            var eva = MakeRecording("Jeb", 300, 400);
            eva.EvaCrewName = "Jeb";
            eva.ParentRecordingId = "parent-123";
            eva.ParentBranchPointId = "bp-1";

            var keys = TimelineBuilder.BuildEvaBranchKeys(new List<Recording> { eva });
            Assert.Single(keys);
            Assert.Contains(TimelineBuilder.EncodeEvaBranchKey("parent-123", 300), keys);
        }

        [Fact]
        public void BuildEvaBranchKeys_SkipsEvaWithoutParent()
        {
            var eva = MakeRecording("Jeb", 300, 400);
            eva.EvaCrewName = "Jeb";
            // No ParentRecordingId

            var keys = TimelineBuilder.BuildEvaBranchKeys(new List<Recording> { eva });
            Assert.Empty(keys);
        }
    }
}
