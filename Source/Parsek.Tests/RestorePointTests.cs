using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RestorePointTests
    {
        public RestorePointTests()
        {
            RestorePointStore.SuppressLogging = true;
            RestorePointStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void SerializeRoundTrip()
        {
            var rp = new RestorePoint(true)
            {
                Id = "abc123def456",
                UT = 17030.5,
                SaveFileName = "parsek_rp_abc123",
                Label = "\"Flea Rocket\" launch (1 recording)",
                RecordingCount = 1,
                Funds = 47500.0,
                Science = 12.75,
                Reputation = 8.5f,
                ReservedFundsAtSave = 15000.0,
                ReservedScienceAtSave = 3.25,
                ReservedRepAtSave = 2.0f,
                SaveFileExists = true
            };

            var parent = new ConfigNode("TEST");
            rp.SerializeInto(parent);

            ConfigNode rpNode = parent.GetNode("RESTORE_POINT");
            Assert.NotNull(rpNode);

            RestorePoint loaded = RestorePoint.DeserializeFrom(rpNode);

            Assert.Equal(rp.Id, loaded.Id);
            Assert.Equal(rp.UT, loaded.UT);
            Assert.Equal(rp.SaveFileName, loaded.SaveFileName);
            Assert.Equal(rp.Label, loaded.Label);
            Assert.Equal(rp.RecordingCount, loaded.RecordingCount);
            Assert.Equal(rp.Funds, loaded.Funds);
            Assert.Equal(rp.Science, loaded.Science);
            Assert.Equal(rp.Reputation, loaded.Reputation);
            Assert.Equal(rp.ReservedFundsAtSave, loaded.ReservedFundsAtSave);
            Assert.Equal(rp.ReservedScienceAtSave, loaded.ReservedScienceAtSave);
            Assert.Equal(rp.ReservedRepAtSave, loaded.ReservedRepAtSave);
        }

        [Fact]
        public void SerializeRoundTrip_ExtremeDoubles()
        {
            var rp = new RestorePoint(true)
            {
                Id = "extreme_test",
                UT = 1e15,
                SaveFileName = "parsek_rp_extreme",
                Label = "extreme test",
                RecordingCount = 999999,
                Funds = -1e12,
                Science = 1e-10,
                Reputation = -999.999f,
                ReservedFundsAtSave = double.MaxValue / 2,
                ReservedScienceAtSave = double.MinValue / 2,
                ReservedRepAtSave = float.MaxValue / 2,
                SaveFileExists = true
            };

            var parent = new ConfigNode("TEST");
            rp.SerializeInto(parent);

            ConfigNode rpNode = parent.GetNode("RESTORE_POINT");
            RestorePoint loaded = RestorePoint.DeserializeFrom(rpNode);

            Assert.Equal(rp.UT, loaded.UT);
            Assert.Equal(rp.Funds, loaded.Funds);
            Assert.Equal(rp.Science, loaded.Science);
            Assert.Equal(rp.Reputation, loaded.Reputation);
            Assert.Equal(rp.ReservedFundsAtSave, loaded.ReservedFundsAtSave);
            Assert.Equal(rp.ReservedScienceAtSave, loaded.ReservedScienceAtSave);
            Assert.Equal(rp.ReservedRepAtSave, loaded.ReservedRepAtSave);
            Assert.Equal(rp.RecordingCount, loaded.RecordingCount);
        }

        [Fact]
        public void LabelGeneration_Standalone()
        {
            string label = RestorePointStore.BuildLabel("Flea Rocket", 3, false);
            Assert.Equal("\"Flea Rocket\" launch (3 recordings)", label);
        }

        [Fact]
        public void LabelGeneration_Tree()
        {
            string label = RestorePointStore.BuildLabel("Mun Lander", 5, true);
            Assert.Equal("\"Mun Lander\" tree launch (5 recordings)", label);
        }

        [Fact]
        public void LabelGeneration_SingleRecording()
        {
            string label = RestorePointStore.BuildLabel("Test Vessel", 1, false);
            Assert.Equal("\"Test Vessel\" launch (1 recording)", label);
        }

        [Fact]
        public void SaveFileNameGeneration()
        {
            string name = RestorePointStore.RestorePointSaveName("abc123");
            Assert.Equal("parsek_rp_abc123", name);
        }

        [Fact]
        public void ResetForTesting_ClearsAll()
        {
            // Add a restore point
            var rp = new RestorePoint(true)
            {
                Id = "test_reset",
                UT = 100,
                SaveFileName = "parsek_rp_test",
                Label = "test",
                RecordingCount = 1,
                Funds = 50000,
                SaveFileExists = true
            };
            RestorePointStore.AddForTesting(rp);

            // Set pending launch save
            RestorePointStore.pendingLaunchSave = new PendingLaunchSave
            {
                SaveFileName = "pending_save",
                UT = 200,
                Funds = 40000
            };

            // Set go-back flags
            RestorePointStore.IsGoingBack = true;
            RestorePointStore.GoBackUT = 300;
            RestorePointStore.GoBackReserved = new ResourceBudget.BudgetSummary
            {
                reservedFunds = 10000,
                reservedScience = 5,
                reservedReputation = 2
            };

            // Verify state is set
            Assert.True(RestorePointStore.HasRestorePoints);
            Assert.True(RestorePointStore.HasPendingLaunchSave);
            Assert.True(RestorePointStore.IsGoingBack);

            // Reset
            RestorePointStore.ResetForTesting();

            // Verify all cleared
            Assert.False(RestorePointStore.HasRestorePoints);
            Assert.Empty(RestorePointStore.RestorePoints);
            Assert.False(RestorePointStore.HasPendingLaunchSave);
            Assert.Null(RestorePointStore.pendingLaunchSave);
            Assert.False(RestorePointStore.IsGoingBack);
            Assert.Equal(0, RestorePointStore.GoBackUT);
            Assert.Equal(0, RestorePointStore.GoBackReserved.reservedFunds);
            Assert.Equal(0, RestorePointStore.GoBackReserved.reservedScience);
            Assert.Equal(0, RestorePointStore.GoBackReserved.reservedReputation);
        }

        [Fact]
        public void BuildRestorePointsRelativePath_CorrectPath()
        {
            string path = RecordingPaths.BuildRestorePointsRelativePath().Replace('\\', '/');
            Assert.Equal("Parsek/GameState/restore_points.pgrp", path);
        }
    }
}
