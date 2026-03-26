using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GhostMapPresenceTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostMapPresenceTests()
        {
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        #region HasOrbitData

        /// <summary>
        /// Recording with terminal orbit body and positive SMA has orbit data.
        /// Guards: orbital recordings are correctly identified for map view orbit lines.
        /// </summary>
        [Fact]
        public void HasOrbitData_WithTerminalOrbit_True()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000.0,
                TerminalOrbitEccentricity = 0.01
            };

            bool result = GhostMapPresence.HasOrbitData(rec);

            Assert.True(result);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]") && l.Contains("HasOrbitData") &&
                l.Contains("Kerbin") && l.Contains("True"));
        }

        /// <summary>
        /// Recording with surface terminal position but no orbit data returns false.
        /// Guards: surface-only recordings correctly excluded from orbit display.
        /// </summary>
        [Fact]
        public void HasOrbitData_SurfaceOnly_False()
        {
            var rec = new Recording
            {
                TerminalPosition = new SurfacePosition
                {
                    body = "Kerbin",
                    latitude = -0.0972,
                    longitude = -74.5575,
                    altitude = 67.0
                }
                // No TerminalOrbitBody or SMA
            };

            bool result = GhostMapPresence.HasOrbitData(rec);

            Assert.False(result);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]") && l.Contains("HasOrbitData") &&
                l.Contains("False"));
        }

        /// <summary>
        /// Null recording returns false without throwing.
        /// Guards: null safety for recordings not yet loaded.
        /// </summary>
        [Fact]
        public void HasOrbitData_NoData_False()
        {
            bool result = GhostMapPresence.HasOrbitData(null);

            Assert.False(result);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]") && l.Contains("null recording"));
        }

        /// <summary>
        /// Recording with orbit body but zero SMA returns false.
        /// Guards: incomplete orbital data correctly rejected.
        /// </summary>
        [Fact]
        public void HasOrbitData_ZeroSMA_False()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 0
            };

            bool result = GhostMapPresence.HasOrbitData(rec);

            Assert.False(result);
        }

        /// <summary>
        /// Recording with positive SMA but no orbit body returns false.
        /// Guards: body name is required for orbit data.
        /// </summary>
        [Fact]
        public void HasOrbitData_NoBody_False()
        {
            var rec = new Recording
            {
                TerminalOrbitSemiMajorAxis = 700000.0
            };

            bool result = GhostMapPresence.HasOrbitData(rec);

            Assert.False(result);
        }

        #endregion

        #region ComputeGhostDisplayInfo

        /// <summary>
        /// Active chain (not terminated, not blocked) shows spawn UT in status.
        /// Guards: primary display path for ghost vessels in tracking station.
        /// </summary>
        [Fact]
        public void ComputeGhostDisplayInfo_ActiveChain_ShowsSpawnUT()
        {
            var chain = new GhostChain
            {
                OriginalVesselPid = 42,
                SpawnUT = 17060.0,
                TipRecordingId = "rec-tip",
                IsTerminated = false,
                SpawnBlocked = false
            };

            var (name, status, spawnUT) =
                GhostMapPresence.ComputeGhostDisplayInfo(chain, "Station Alpha");

            Assert.Equal("Station Alpha", name);
            Assert.Contains("Ghost", status);
            Assert.Contains("spawns at UT=17060.0", status);
            Assert.Equal(17060.0, spawnUT);

            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]") && l.Contains("active chain") &&
                l.Contains("Station Alpha") && l.Contains("17060"));
        }

        /// <summary>
        /// Terminated chain shows "terminated" status.
        /// Guards: terminated ghosts (destroyed/recovered vessels) display correctly.
        /// </summary>
        [Fact]
        public void ComputeGhostDisplayInfo_TerminatedChain_ShowsTerminated()
        {
            var chain = new GhostChain
            {
                OriginalVesselPid = 42,
                SpawnUT = 17060.0,
                TipRecordingId = "rec-tip",
                IsTerminated = true,
                SpawnBlocked = false
            };

            var (name, status, spawnUT) =
                GhostMapPresence.ComputeGhostDisplayInfo(chain, "Debris Field");

            Assert.Equal("Debris Field", name);
            Assert.Contains("terminated", status);
            Assert.Equal(17060.0, spawnUT);

            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]") && l.Contains("terminated") &&
                l.Contains("Debris Field"));
        }

        /// <summary>
        /// Blocked chain shows "spawn blocked" status with blocked-since UT.
        /// Guards: collision-blocked ghosts display diagnostic info.
        /// </summary>
        [Fact]
        public void ComputeGhostDisplayInfo_BlockedChain_ShowsBlocked()
        {
            var chain = new GhostChain
            {
                OriginalVesselPid = 42,
                SpawnUT = 17060.0,
                TipRecordingId = "rec-tip",
                IsTerminated = false,
                SpawnBlocked = true,
                BlockedSinceUT = 17055.0
            };

            var (name, status, spawnUT) =
                GhostMapPresence.ComputeGhostDisplayInfo(chain, "Rover");

            Assert.Equal("Rover", name);
            Assert.Contains("spawn blocked", status);
            Assert.Contains("17055.0", status);
            Assert.Equal(17060.0, spawnUT);

            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]") && l.Contains("spawn blocked") &&
                l.Contains("Rover"));
        }

        /// <summary>
        /// Null chain returns safe defaults without throwing.
        /// Guards: graceful handling of missing chain data.
        /// </summary>
        [Fact]
        public void ComputeGhostDisplayInfo_NullChain_ReturnsSafeDefaults()
        {
            var (name, status, spawnUT) =
                GhostMapPresence.ComputeGhostDisplayInfo(null, "TestVessel");

            Assert.Equal("TestVessel", name);
            Assert.Contains("no chain data", status);
            Assert.Equal(0.0, spawnUT);

            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]") && l.Contains("null chain"));
        }

        /// <summary>
        /// Null vessel name is replaced with "(unnamed)".
        /// Guards: null vessel name doesn't crash display formatting.
        /// </summary>
        [Fact]
        public void ComputeGhostDisplayInfo_NullVesselName_ShowsUnnamed()
        {
            var chain = new GhostChain
            {
                OriginalVesselPid = 42,
                SpawnUT = 17060.0,
                IsTerminated = false
            };

            var (name, status, spawnUT) =
                GhostMapPresence.ComputeGhostDisplayInfo(chain, null);

            Assert.Equal("(unnamed)", name);
            Assert.Contains("Ghost", status);
        }

        #endregion

        #region IsGhostMapVessel

        /// <summary>
        /// Empty PID set returns false for any PID.
        /// Guards: no false positives when no ghost vessels exist.
        /// </summary>
        [Fact]
        public void IsGhostMapVessel_EmptySet_ReturnsFalse()
        {
            Assert.False(GhostMapPresence.IsGhostMapVessel(12345));
        }

        /// <summary>
        /// After adding a PID, IsGhostMapVessel returns true for that PID.
        /// Guards: registered ghost PIDs are correctly identified.
        /// </summary>
        [Fact]
        public void IsGhostMapVessel_AfterAdd_ReturnsTrue()
        {
            GhostMapPresence.ghostMapVesselPids.Add(12345);
            Assert.True(GhostMapPresence.IsGhostMapVessel(12345));
        }

        /// <summary>
        /// After adding then removing a PID, IsGhostMapVessel returns false.
        /// Guards: removed ghost PIDs are no longer identified as ghosts.
        /// </summary>
        [Fact]
        public void IsGhostMapVessel_AfterRemove_ReturnsFalse()
        {
            GhostMapPresence.ghostMapVesselPids.Add(12345);
            GhostMapPresence.ghostMapVesselPids.Remove(12345);
            Assert.False(GhostMapPresence.IsGhostMapVessel(12345));
        }

        /// <summary>
        /// ResetForTesting clears all PID tracking state.
        /// Guards: test isolation — no state bleeds between tests.
        /// </summary>
        [Fact]
        public void ResetForTesting_ClearsAllState()
        {
            GhostMapPresence.ghostMapVesselPids.Add(111);
            GhostMapPresence.ghostMapVesselPids.Add(222);
            GhostMapPresence.ResetForTesting();
            Assert.False(GhostMapPresence.IsGhostMapVessel(111));
            Assert.False(GhostMapPresence.IsGhostMapVessel(222));
            Assert.Empty(GhostMapPresence.ghostMapVesselPids);
        }

        #endregion

        #region HasOrbitData (IPlaybackTrajectory overload)

        /// <summary>
        /// Null IPlaybackTrajectory returns false without throwing.
        /// Guards: null safety for the interface overload.
        /// </summary>
        [Fact]
        public void HasOrbitData_NullTrajectory_ReturnsFalse()
        {
            Assert.False(GhostMapPresence.HasOrbitData((IPlaybackTrajectory)null));
        }

        /// <summary>
        /// Recording accessed via IPlaybackTrajectory interface returns true when orbit data present.
        /// Guards: interface-based lookup works (engine uses IPlaybackTrajectory, not Recording).
        /// </summary>
        [Fact]
        public void HasOrbitData_ViaInterface_ReturnsTrue()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Mun",
                TerminalOrbitSemiMajorAxis = 12000000
            };
            IPlaybackTrajectory traj = rec;
            Assert.True(GhostMapPresence.HasOrbitData(traj));
        }

        #endregion

        #region Log assertions

        /// <summary>
        /// HasOrbitData logs result with [GhostMap] tag.
        /// Guards: diagnostic logging fires for orbit data checks.
        /// </summary>
        [Fact]
        public void HasOrbitData_LogsResult()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000
            };
            GhostMapPresence.HasOrbitData(rec);
            Assert.Contains(logLines, l => l.Contains("[GhostMap]") && l.Contains("result=True"));
        }

        #endregion
    }
}
