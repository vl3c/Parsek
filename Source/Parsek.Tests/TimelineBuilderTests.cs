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
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
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
                0);

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
                0);

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
                0);

            Assert.Empty(result);
        }

        // ================================================================
        // 4. PlaybackDisabled — no VesselSpawn
        // ================================================================

        [Fact]
        public void PlaybackDisabled_NoVesselSpawn()
        {
            var rec = MakeRecording("Ghost Ship", 100, 200, playbackEnabled: false);
            var result = TimelineBuilder.Build(
                new List<Recording> { rec },
                new List<GameAction>(),
                new List<Milestone>(),
                0);

            // Only RecordingStart — no VesselSpawn since playback disabled
            Assert.Single(result);
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
                0);

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
                0);

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
                0);

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
                0);

            Assert.Single(result);
            Assert.Equal("Starting reputation: 25", result[0].DisplayText);
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
                0);

            Assert.Single(result);
            Assert.Equal(SignificanceTier.T2, result[0].Tier);
            Assert.False(result[0].IsEffective);
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
                0);

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

            Assert.Equal(11, t1Types.Length);
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
                0);

            // Only the chain root (index 0) gets a Launch entry.
            // Chain children (index > 0) are optimizer splits, not player-visible launches.
            var starts = result.Where(e => e.Type == TimelineEntryType.RecordingStart).ToList();
            Assert.Equal(1, starts.Count);

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
                0);

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
                0);

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
                0);

            Assert.Single(result);
            Assert.Equal(TimelineEntryType.LegacyEvent, result[0].Type);
            Assert.Equal(SignificanceTier.T2, result[0].Tier);
            Assert.Equal(TimelineSource.Legacy, result[0].Source);
        }

        // ================================================================
        // 17. Legacy events — wrong epoch filtered
        // ================================================================

        [Fact]
        public void LegacyEvents_WrongEpochFiltered()
        {
            var milestone = new Milestone
            {
                Committed = true,
                Epoch = 5,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 150,
                        eventType = GameStateEventType.ContractCompleted,
                        key = "old-contract"
                    }
                }
            };

            var result = TimelineBuilder.Build(
                new List<Recording>(),
                new List<GameAction>(),
                new List<Milestone> { milestone },
                3); // currentEpoch = 3, milestone epoch = 5

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
                0);

            Assert.Empty(result);
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
                0);

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
                0);

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
                0);

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
                1); // epoch matches milestone

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
                0);

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
                0);

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
                TimelineEntryType.LegacyEvent
            };
            foreach (var type in events)
                Assert.False(TimelineEntryDisplay.IsPlayerAction(type), $"{type} should NOT be a player action");
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
    }
}
