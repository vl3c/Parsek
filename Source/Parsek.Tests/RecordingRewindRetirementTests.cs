using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    public class RecordingRewindRetirementTests
    {
        [Fact]
        public void SaveIntoLoadFrom_RoundTripsFields()
        {
            var parent = new ConfigNode("RECORDING_REWIND_RETIREMENTS");
            var retirement = new RecordingRewindRetirement
            {
                RetirementId = "rrt_123",
                RecordingId = "rewound-fork",
                RestoredRecordingId = "restored-origin",
                SourceSupersedeRelationId = "rsr_123",
                RewindUT = 42.5,
                CreatedUT = 43.5,
                CreatedRealTime = "2026-05-09T00:00:00.0000000Z",
                Reason = RecordingRewindRetirement.DefaultReason
            };

            retirement.SaveInto(parent);
            var loaded = RecordingRewindRetirement.LoadFrom(parent.GetNode("ENTRY"));

            Assert.Equal("rrt_123", loaded.RetirementId);
            Assert.Equal("rewound-fork", loaded.RecordingId);
            Assert.Equal("restored-origin", loaded.RestoredRecordingId);
            Assert.Equal("rsr_123", loaded.SourceSupersedeRelationId);
            Assert.Equal(42.5, loaded.RewindUT);
            Assert.Equal(43.5, loaded.CreatedUT);
            Assert.Equal("2026-05-09T00:00:00.0000000Z", loaded.CreatedRealTime);
            Assert.Equal(RecordingRewindRetirement.DefaultReason, loaded.Reason);
        }

        [Fact]
        public void LoadFrom_MissingReason_UsesDefault()
        {
            var node = new ConfigNode("ENTRY");
            node.AddValue("recordingId", "rewound-fork");

            var loaded = RecordingRewindRetirement.LoadFrom(node);

            Assert.Equal("rewound-fork", loaded.RecordingId);
            Assert.Equal(RecordingRewindRetirement.DefaultReason, loaded.Reason);
        }

        [Fact]
        public void ScenarioSaveLoad_ReplacesRetirementParentNode()
        {
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint>(),
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = new List<RecordingRewindRetirement>
                {
                    new RecordingRewindRetirement
                    {
                        RetirementId = "rrt_fresh",
                        RecordingId = "fresh-retired",
                        RestoredRecordingId = "fresh-restored",
                        SourceSupersedeRelationId = "rsr_fresh",
                        RewindUT = 12.5,
                        CreatedUT = 13.5,
                        CreatedRealTime = "2026-05-09T00:00:00.0000000Z",
                        Reason = RecordingRewindRetirement.DefaultReason
                    }
                },
                LedgerTombstones = new List<LedgerTombstone>()
            };
            var node = new ConfigNode("SCENARIO");
            ConfigNode staleParent = node.AddNode("RECORDING_REWIND_RETIREMENTS");
            ConfigNode staleEntry = staleParent.AddNode("ENTRY");
            staleEntry.AddValue("retirementId", "rrt_stale");
            staleEntry.AddValue("recordingId", "stale-retired");

            InvokeSaveRewindStagingState(scenario, node);

            ConfigNode[] parents = node.GetNodes("RECORDING_REWIND_RETIREMENTS");
            Assert.Single(parents);
            ConfigNode[] entries = parents[0].GetNodes("ENTRY");
            Assert.Single(entries);
            Assert.Equal(
                "fresh-retired",
                RecordingRewindRetirement.LoadFrom(entries[0]).RecordingId);

            var loadedScenario = new ParsekScenario();
            InvokeLoadRewindStagingState(loadedScenario, node);

            RecordingRewindRetirement loaded = Assert.Single(loadedScenario.RecordingRewindRetirements);
            Assert.Equal("rrt_fresh", loaded.RetirementId);
            Assert.Equal("fresh-retired", loaded.RecordingId);
            Assert.Equal("fresh-restored", loaded.RestoredRecordingId);

            scenario.RecordingRewindRetirements.Clear();
            InvokeSaveRewindStagingState(scenario, node);

            Assert.Empty(node.GetNodes("RECORDING_REWIND_RETIREMENTS"));
        }

        private static void InvokeSaveRewindStagingState(ParsekScenario scenario, ConfigNode node)
        {
            MethodInfo method = typeof(ParsekScenario).GetMethod(
                "SaveRewindStagingState",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(scenario, new object[] { node });
        }

        private static void InvokeLoadRewindStagingState(ParsekScenario scenario, ConfigNode node)
        {
            MethodInfo method = typeof(ParsekScenario).GetMethod(
                "LoadRewindStagingState",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(scenario, new object[] { node });
        }
    }
}
