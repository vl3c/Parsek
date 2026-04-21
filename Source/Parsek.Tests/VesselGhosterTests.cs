using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class VesselGhosterTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly VesselGhoster ghoster;

        public VesselGhosterTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ghoster = new VesselGhoster();
        }

        public void Dispose()
        {
            ghoster.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        #region ShouldAttemptGhosting — pure decision

        [Fact]
        public void ShouldAttemptGhosting_ZeroPid_ReturnsFalse()
        {
            Assert.False(VesselGhoster.ShouldAttemptGhosting(0, true));
        }

        [Fact]
        public void ShouldAttemptGhosting_VesselMissing_ReturnsFalse()
        {
            Assert.False(VesselGhoster.ShouldAttemptGhosting(100, false));
        }

        [Fact]
        public void ShouldAttemptGhosting_ValidVessel_ReturnsTrue()
        {
            Assert.True(VesselGhoster.ShouldAttemptGhosting(100, true));
        }

        [Fact]
        public void ShouldAttemptGhosting_ZeroPidAndMissing_ReturnsFalse()
        {
            Assert.False(VesselGhoster.ShouldAttemptGhosting(0, false));
        }

        #endregion

        #region CanSpawnAtChainTip — pure decision

        [Fact]
        public void CanSpawnAtChainTip_NullChain_ReturnsFalse()
        {
            Assert.False(VesselGhoster.CanSpawnAtChainTip(null));
        }

        [Fact]
        public void CanSpawnAtChainTip_TerminatedChain_ReturnsFalse()
        {
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                TipRecordingId = "rec-1",
                IsTerminated = true
            };
            Assert.False(VesselGhoster.CanSpawnAtChainTip(chain));
        }

        [Fact]
        public void CanSpawnAtChainTip_NullTipRecordingId_ReturnsFalse()
        {
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                TipRecordingId = null,
                IsTerminated = false
            };
            Assert.False(VesselGhoster.CanSpawnAtChainTip(chain));
        }

        [Fact]
        public void CanSpawnAtChainTip_EmptyTipRecordingId_ReturnsFalse()
        {
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                TipRecordingId = "",
                IsTerminated = false
            };
            Assert.False(VesselGhoster.CanSpawnAtChainTip(chain));
        }

        [Fact]
        public void CanSpawnAtChainTip_ValidChain_ReturnsTrue()
        {
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                TipRecordingId = "rec-tip",
                IsTerminated = false
            };
            Assert.True(VesselGhoster.CanSpawnAtChainTip(chain));
        }

        #endregion

        #region IsGhosted — state tracking

        [Fact]
        public void IsGhosted_NotGhosted_ReturnsFalse()
        {
            Assert.False(ghoster.IsGhosted(100));
        }

        [Fact]
        public void IsGhosted_AfterDirectAdd_ReturnsTrue()
        {
            ghoster.AddGhostedForTesting(100, "Station", new ConfigNode("VESSEL"));
            Assert.True(ghoster.IsGhosted(100));
        }

        [Fact]
        public void IsGhosted_DifferentPid_ReturnsFalse()
        {
            ghoster.AddGhostedForTesting(100, "Station", new ConfigNode("VESSEL"));
            Assert.False(ghoster.IsGhosted(200));
        }

        #endregion

        #region GhostedCount

        [Fact]
        public void GhostedCount_Empty_ReturnsZero()
        {
            Assert.Equal(0, ghoster.GhostedCount);
        }

        [Fact]
        public void GhostedCount_AfterAdd_ReturnsCorrect()
        {
            ghoster.AddGhostedForTesting(100, "Station-A", new ConfigNode("VESSEL"));
            ghoster.AddGhostedForTesting(200, "Station-B", new ConfigNode("VESSEL"));
            Assert.Equal(2, ghoster.GhostedCount);
        }

        [Fact]
        public void GhostedCount_DuplicatePid_ReturnsOne()
        {
            ghoster.AddGhostedForTesting(100, "Station-A", new ConfigNode("VESSEL"));
            ghoster.AddGhostedForTesting(100, "Station-A-Updated", new ConfigNode("VESSEL"));
            Assert.Equal(1, ghoster.GhostedCount);
        }

        #endregion

        #region CleanupAll

        [Fact]
        public void CleanupAll_ClearsState()
        {
            ghoster.AddGhostedForTesting(100, "Station-A", new ConfigNode("VESSEL"));
            ghoster.AddGhostedForTesting(200, "Station-B", new ConfigNode("VESSEL"));
            Assert.Equal(2, ghoster.GhostedCount);

            ghoster.CleanupAll();

            Assert.Equal(0, ghoster.GhostedCount);
            Assert.False(ghoster.IsGhosted(100));
            Assert.False(ghoster.IsGhosted(200));
        }

        [Fact]
        public void CleanupAll_EmptyDict_NoThrow()
        {
            // Should not throw on empty state
            ghoster.CleanupAll();
            Assert.Equal(0, ghoster.GhostedCount);
        }

        [Fact]
        public void CleanupAll_LogsCount()
        {
            logLines.Clear();
            ghoster.AddGhostedForTesting(100, "Station", new ConfigNode("VESSEL"));
            ghoster.AddGhostedForTesting(200, "Relay", new ConfigNode("VESSEL"));

            ghoster.CleanupAll();

            Assert.Contains(logLines, l =>
                l.Contains("[Ghoster]") && l.Contains("CleanupAll") && l.Contains("2"));
        }

        [Fact]
        public void CleanupAll_EmptyDict_LogsZero()
        {
            logLines.Clear();
            ghoster.CleanupAll();

            Assert.Contains(logLines, l =>
                l.Contains("[Ghoster]") && l.Contains("CleanupAll") && l.Contains("0"));
        }

        #endregion

        #region GetGhostedInfo

        [Fact]
        public void GetGhostedInfo_NotGhosted_ReturnsNull()
        {
            Assert.Null(ghoster.GetGhostedInfo(999));
        }

        [Fact]
        public void GetGhostedInfo_Ghosted_ReturnsInfo()
        {
            var snapshot = new ConfigNode("VESSEL");
            ghoster.AddGhostedForTesting(100, "Station", snapshot);

            var info = ghoster.GetGhostedInfo(100);

            Assert.NotNull(info);
            Assert.Equal((uint)100, info.vesselPid);
            Assert.Equal("Station", info.vesselName);
            Assert.Same(snapshot, info.snapshot);
        }

        #endregion

        #region GetGhostGO

        [Fact]
        public void GetGhostGO_NotGhosted_ReturnsNull()
        {
            Assert.Null(ghoster.GetGhostGO(100));
        }

        [Fact]
        public void GetGhostGO_GhostedButNoGO_ReturnsNull()
        {
            // AddGhostedForTesting creates info with ghostGO=null (default)
            ghoster.AddGhostedForTesting(100, "Station", new ConfigNode("VESSEL"));
            Assert.Null(ghoster.GetGhostGO(100));
        }

        #endregion

        #region ResetForTesting

        [Fact]
        public void ResetForTesting_ClearsAllState()
        {
            ghoster.AddGhostedForTesting(100, "Station-A", new ConfigNode("VESSEL"));
            ghoster.AddGhostedForTesting(200, "Station-B", new ConfigNode("VESSEL"));

            ghoster.ResetForTesting();

            Assert.Equal(0, ghoster.GhostedCount);
            Assert.False(ghoster.IsGhosted(100));
            Assert.False(ghoster.IsGhosted(200));
        }

        #endregion

        #region Multiple operations — state consistency

        [Fact]
        public void AddThenCleanup_ThenAdd_StateConsistent()
        {
            ghoster.AddGhostedForTesting(100, "First", new ConfigNode("VESSEL"));
            Assert.Equal(1, ghoster.GhostedCount);

            ghoster.CleanupAll();
            Assert.Equal(0, ghoster.GhostedCount);

            ghoster.AddGhostedForTesting(200, "Second", new ConfigNode("VESSEL"));
            Assert.Equal(1, ghoster.GhostedCount);
            Assert.False(ghoster.IsGhosted(100));
            Assert.True(ghoster.IsGhosted(200));
        }

        #endregion

        #region Endpoint-Aligned Orbit Seeds

        [Fact]
        public void TryResolvePropagatedOrbitSeed_MismatchedTerminalOrbitBody_UsesEndpointAlignedSeed()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 900000.0,
                TerminalOrbitEccentricity = 0.1,
                TerminalOrbitInclination = 1.0,
                TerminalOrbitLAN = 2.0,
                TerminalOrbitArgumentOfPeriapsis = 3.0,
                TerminalOrbitMeanAnomalyAtEpoch = 0.4,
                TerminalOrbitEpoch = 100.0,
                EndpointPhase = RecordingEndpointPhase.TrajectoryPoint,
                EndpointBodyName = "Mun"
            };
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Mun",
                semiMajorAxis = 250000.0,
                eccentricity = 0.02,
                inclination = 4.0,
                longitudeOfAscendingNode = 11.0,
                argumentOfPeriapsis = 22.0,
                meanAnomalyAtEpoch = 0.7,
                epoch = 350.0
            });

            Assert.True(VesselGhoster.TryResolvePropagatedOrbitSeed(
                rec,
                out double inclination,
                out double eccentricity,
                out double semiMajorAxis,
                out double lan,
                out double argumentOfPeriapsis,
                out double meanAnomalyAtEpoch,
                out double epoch,
                out string bodyName));

            Assert.Equal("Mun", bodyName);
            Assert.Equal(250000.0, semiMajorAxis, 10);
            Assert.Equal(4.0, inclination, 10);
            Assert.Equal(0.7, meanAnomalyAtEpoch, 10);
        }

        [Fact]
        public void TryResolvePropagatedOrbitSeed_NoEndpointAlignedSeed_ReturnsFalse()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 900000.0,
                EndpointPhase = RecordingEndpointPhase.TrajectoryPoint,
                EndpointBodyName = "Mun"
            };

            Assert.False(VesselGhoster.TryResolvePropagatedOrbitSeed(
                rec,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _));
        }

        [Fact]
        public void TryResolvePropagatedOrbitSeed_SurfaceEndpoint_DoesNotReuseStaleTerminalOrbit()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Landed,
                TerminalOrbitBody = "Mun",
                TerminalOrbitSemiMajorAxis = 250000.0,
                TerminalOrbitEccentricity = 0.02,
                TerminalOrbitInclination = 4.0,
                TerminalOrbitLAN = 11.0,
                TerminalOrbitArgumentOfPeriapsis = 22.0,
                TerminalOrbitMeanAnomalyAtEpoch = 0.7,
                TerminalOrbitEpoch = 350.0,
                EndpointPhase = RecordingEndpointPhase.SurfacePosition,
                EndpointBodyName = "Mun"
            };

            Assert.False(VesselGhoster.TryResolvePropagatedOrbitSeed(
                rec,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _));
        }

        #endregion
    }
}
