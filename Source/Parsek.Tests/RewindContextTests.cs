using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RewindContextTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RewindContextTests()
        {
            RewindContext.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            RewindContext.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void IsRewinding_DefaultFalse()
        {
            Assert.False(RewindContext.IsRewinding);
            Assert.Equal(0.0, RewindContext.RewindUT);
            Assert.Equal(0.0, RewindContext.RewindAdjustedUT);
            Assert.Equal(0.0, RewindContext.RewindReserved.reservedFunds);
            Assert.Equal(0.0, RewindContext.RewindReserved.reservedScience);
            Assert.Equal(0.0, RewindContext.RewindReserved.reservedReputation);
            Assert.Equal(0.0, RewindContext.RewindBaselineFunds);
            Assert.Equal(0.0, RewindContext.RewindBaselineScience);
            Assert.Equal(0f, RewindContext.RewindBaselineRep);
            Assert.Null(RewindContext.RewindQuicksaveVesselPids);
        }

        [Fact]
        public void BeginRewind_SetsAllFields()
        {
            var reserved = new BudgetSummary
            {
                reservedFunds = 5000,
                reservedScience = 25,
                reservedReputation = 3
            };

            RewindContext.BeginRewind(17000.0, reserved, 50000.0, 42.0, 7.5f);

            Assert.True(RewindContext.IsRewinding);
            Assert.Equal(17000.0, RewindContext.RewindUT);
            Assert.Equal(5000.0, RewindContext.RewindReserved.reservedFunds);
            Assert.Equal(25.0, RewindContext.RewindReserved.reservedScience);
            Assert.Equal(3.0, RewindContext.RewindReserved.reservedReputation);
            Assert.Equal(50000.0, RewindContext.RewindBaselineFunds);
            Assert.Equal(42.0, RewindContext.RewindBaselineScience);
            Assert.Equal(7.5f, RewindContext.RewindBaselineRep);
        }

        [Fact]
        public void BeginRewind_LogsAllValues()
        {
            var reserved = new BudgetSummary
            {
                reservedFunds = 100,
                reservedScience = 10,
                reservedReputation = 5
            };

            RewindContext.BeginRewind(17000.0, reserved, 25000.0, 5.0, 1.5f);

            Assert.Contains(logLines, l =>
                l.Contains("[RewindContext]") && l.Contains("BeginRewind"));
            Assert.Contains(logLines, l =>
                l.Contains("UT=17000") && l.Contains("baselineFunds=25000"));
        }

        [Fact]
        public void EndRewind_ClearsAllFields()
        {
            var reserved = new BudgetSummary
            {
                reservedFunds = 5000,
                reservedScience = 25,
                reservedReputation = 3
            };
            RewindContext.BeginRewind(17000.0, reserved, 50000.0, 42.0, 7.5f);
            RewindContext.SetAdjustedUT(16990.0);
            RewindContext.SetQuicksaveVesselPids(new HashSet<uint> { 1, 2, 3 });

            // Verify all fields are set before clearing
            Assert.True(RewindContext.IsRewinding);

            RewindContext.EndRewind();

            Assert.False(RewindContext.IsRewinding);
            Assert.Equal(0.0, RewindContext.RewindUT);
            Assert.Equal(0.0, RewindContext.RewindAdjustedUT);
            Assert.Equal(0.0, RewindContext.RewindReserved.reservedFunds);
            Assert.Equal(0.0, RewindContext.RewindReserved.reservedScience);
            Assert.Equal(0.0, RewindContext.RewindReserved.reservedReputation);
            Assert.Equal(0.0, RewindContext.RewindBaselineFunds);
            Assert.Equal(0.0, RewindContext.RewindBaselineScience);
            Assert.Equal(0f, RewindContext.RewindBaselineRep);
            Assert.Null(RewindContext.RewindQuicksaveVesselPids);
        }

        [Fact]
        public void EndRewind_LogsClearMessage()
        {
            RewindContext.BeginRewind(17000.0, default(BudgetSummary), 0, 0, 0);
            logLines.Clear();

            RewindContext.EndRewind();

            Assert.Contains(logLines, l =>
                l.Contains("[RewindContext]") && l.Contains("EndRewind") &&
                l.Contains("cleared"));
        }

        [Fact]
        public void SetAdjustedUT_SetsUT()
        {
            RewindContext.SetAdjustedUT(16990.5);
            Assert.Equal(16990.5, RewindContext.RewindAdjustedUT);
        }

        [Fact]
        public void SetAdjustedUT_Logs()
        {
            RewindContext.SetAdjustedUT(16990.5);

            Assert.Contains(logLines, l =>
                l.Contains("[RewindContext]") && l.Contains("SetAdjustedUT") &&
                l.Contains("16990"));
        }

        [Fact]
        public void SetQuicksaveVesselPids_SetsPids()
        {
            var pids = new HashSet<uint> { 42, 77, 999 };
            RewindContext.SetQuicksaveVesselPids(pids);

            Assert.Equal(pids, RewindContext.RewindQuicksaveVesselPids);
            Assert.Contains(42u, RewindContext.RewindQuicksaveVesselPids);
            Assert.Contains(77u, RewindContext.RewindQuicksaveVesselPids);
            Assert.Contains(999u, RewindContext.RewindQuicksaveVesselPids);
        }

        [Fact]
        public void SetQuicksaveVesselPids_NullClearsField()
        {
            RewindContext.SetQuicksaveVesselPids(new HashSet<uint> { 1 });
            RewindContext.SetQuicksaveVesselPids(null);

            Assert.Null(RewindContext.RewindQuicksaveVesselPids);
        }

        [Fact]
        public void SetQuicksaveVesselPids_Logs()
        {
            RewindContext.SetQuicksaveVesselPids(new HashSet<uint> { 1, 2, 3 });

            Assert.Contains(logLines, l =>
                l.Contains("[RewindContext]") && l.Contains("SetQuicksaveVesselPids") &&
                l.Contains("3 PID"));
        }

        [Fact]
        public void ResetForTesting_ClearsAllFields()
        {
            RewindContext.BeginRewind(17000.0, new BudgetSummary
            {
                reservedFunds = 100, reservedScience = 10, reservedReputation = 5
            }, 50000.0, 42.0, 7.5f);
            RewindContext.SetAdjustedUT(16990.0);
            RewindContext.SetQuicksaveVesselPids(new HashSet<uint> { 1 });

            RewindContext.ResetForTesting();

            Assert.False(RewindContext.IsRewinding);
            Assert.Equal(0.0, RewindContext.RewindUT);
            Assert.Equal(0.0, RewindContext.RewindAdjustedUT);
            Assert.Equal(0.0, RewindContext.RewindBaselineFunds);
            Assert.Equal(0.0, RewindContext.RewindBaselineScience);
            Assert.Equal(0f, RewindContext.RewindBaselineRep);
            Assert.Null(RewindContext.RewindQuicksaveVesselPids);
        }

        [Fact]
        public void DelegateProperties_RecordingStore_MatchRewindContext()
        {
            // Verify that RecordingStore delegate properties reflect RewindContext state
            RewindContext.BeginRewind(17000.0, new BudgetSummary
            {
                reservedFunds = 100, reservedScience = 10, reservedReputation = 5
            }, 25000.0, 5.0, 1.5f);

            Assert.Equal(RewindContext.IsRewinding, RecordingStore.IsRewinding);
            Assert.Equal(RewindContext.RewindUT, RecordingStore.RewindUT);
            Assert.Equal(RewindContext.RewindBaselineFunds, RecordingStore.RewindBaselineFunds);
            Assert.Equal(RewindContext.RewindBaselineScience, RecordingStore.RewindBaselineScience);
            Assert.Equal(RewindContext.RewindBaselineRep, RecordingStore.RewindBaselineRep);
        }
    }
}
