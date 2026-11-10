using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct cases for the three units split out of
    /// <c>LedgerOrchestrator.OnRealVesselRecovered</c>:
    /// <see cref="LedgerOrchestrator.BuildVesselRecoveredRow"/>,
    /// <see cref="LedgerOrchestrator.LogVesselRecoveredRecorded"/> and
    /// <see cref="LedgerOrchestrator.LogVesselRecoveryRowsSummary"/>.
    /// </summary>
    [Collection("Sequential")]
    public class VesselRecoveredUnitsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo savedCulture;

        public VesselRecoveredUnitsTests()
        {
            savedCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            Thread.CurrentThread.CurrentCulture = savedCulture;
        }

        [Fact]
        public void BuildRow_CarriesTheOwnerTheVesselAndTheUT_AndNothingElse()
        {
            var owner = new Recording { RecordingId = "rec-tip", VesselName = "Recorded Name" };

            var row = LedgerOrchestrator.BuildVesselRecoveredRow(412.4, owner, "Live Name", 2718281828u);

            Assert.Equal(412.4, row.UT);
            Assert.Equal(GameActionType.VesselRecovered, row.Type);
            Assert.Equal("rec-tip", row.RecordingId);
            Assert.Equal("Live Name", row.RecoveredVesselName);
            Assert.Equal(2718281828u, row.RecoveredVesselPid);
            Assert.Equal(0, row.Sequence);
            Assert.Null(row.DedupKey);
            Assert.Empty(logLines);
        }

        [Fact]
        public void BuildRow_NullVesselName_StaysNull()
        {
            var row = LedgerOrchestrator.BuildVesselRecoveredRow(
                1.0, new Recording { RecordingId = "r" }, null, 7u);

            Assert.Null(row.RecoveredVesselName);
            Assert.Equal(7u, row.RecoveredVesselPid);
        }

        [Fact]
        public void RecordedLine_NamesTerminalAndSpawnedPid()
        {
            var owner = new Recording
            {
                RecordingId = "rec-tip",
                TerminalStateValue = TerminalState.Landed,
                SpawnedVesselPersistentId = 4000000000u,
            };

            LedgerOrchestrator.LogVesselRecoveredRecorded(
                "CTR Lander", 3000000000u, "400.0", owner, CultureInfo.InvariantCulture);

            var line = Assert.Single(logLines);
            Assert.Equal(
                "[Parsek][INFO][LedgerOrchestrator] Vessel recovered recorded: vessel='CTR Lander' " +
                "pid=3000000000 recoveryUT=400.0 recordingId=rec-tip terminal=Landed spawnedPid=4000000000",
                line);
        }

        [Fact]
        public void RecordedLine_NoTerminal_ReadsNone()
        {
            LedgerOrchestrator.LogVesselRecoveredRecorded(
                "(null)", 5u, "1.0", new Recording { RecordingId = "r" }, CultureInfo.InvariantCulture);

            var line = Assert.Single(logLines);
            Assert.Contains("terminal=<none> spawnedPid=0", line);
        }

        [Fact]
        public void SummaryLine_CountsOwnersWrittenAndDeduped()
        {
            var owners = new List<Recording>
            {
                new Recording { RecordingId = "a" },
                new Recording { RecordingId = "b" },
                new Recording { RecordingId = "c" },
            };

            LedgerOrchestrator.LogVesselRecoveryRowsSummary(
                "CTR Lander", 12u, "400.1", owners, 1, 2, CultureInfo.InvariantCulture);

            var line = Assert.Single(logLines);
            Assert.Equal(
                "[Parsek][INFO][LedgerOrchestrator] Vessel recovery rows: vessel='CTR Lander' pid=12 " +
                "ut=400.1 owners=3 written=1 deduped=2",
                line);
        }
    }
}
