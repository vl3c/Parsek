using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class TerminalEventTests : IDisposable
    {
        public TerminalEventTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        #region ApplyTerminalDestruction

        [Fact]
        public void ApplyTerminalDestruction_SetsDestroyedAndExplicitEndUT()
        {
            var pending = new ParsekFlight.PendingDestruction
            {
                vesselPid = 100,
                recordingId = "rec_1",
                capturedUT = 5000.0,
                hasOrbit = false,
                hasSurface = false
            };

            var rec = new Recording
            {
                RecordingId = "rec_1"
            };

            ParsekFlight.ApplyTerminalDestruction(pending, rec);

            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.Equal(5000.0, rec.ExplicitEndUT);
        }

        [Fact]
        public void ApplyTerminalDestruction_WithOrbitalData_PopulatesAllTerminalOrbitFields()
        {
            var pending = new ParsekFlight.PendingDestruction
            {
                vesselPid = 200,
                recordingId = "rec_2",
                capturedUT = 6000.0,
                hasOrbit = true,
                inclination = 28.5,
                eccentricity = 0.01,
                semiMajorAxis = 700000.0,
                lan = 45.0,
                argumentOfPeriapsis = 90.0,
                meanAnomalyAtEpoch = 1.5,
                epoch = 5999.0,
                bodyName = "Kerbin",
                hasSurface = false
            };

            var rec = new Recording
            {
                RecordingId = "rec_2"
            };

            ParsekFlight.ApplyTerminalDestruction(pending, rec);

            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.Equal(6000.0, rec.ExplicitEndUT);
            Assert.Equal(28.5, rec.TerminalOrbitInclination);
            Assert.Equal(0.01, rec.TerminalOrbitEccentricity);
            Assert.Equal(700000.0, rec.TerminalOrbitSemiMajorAxis);
            Assert.Equal(45.0, rec.TerminalOrbitLAN);
            Assert.Equal(90.0, rec.TerminalOrbitArgumentOfPeriapsis);
            Assert.Equal(1.5, rec.TerminalOrbitMeanAnomalyAtEpoch);
            Assert.Equal(5999.0, rec.TerminalOrbitEpoch);
            Assert.Equal("Kerbin", rec.TerminalOrbitBody);
        }

        [Fact]
        public void ApplyTerminalDestruction_WithSurfaceData_PopulatesTerminalPosition()
        {
            var surfPos = new SurfacePosition
            {
                body = "Mun",
                latitude = 12.5,
                longitude = -45.3,
                altitude = 100.0,
                situation = SurfaceSituation.Landed
            };

            var pending = new ParsekFlight.PendingDestruction
            {
                vesselPid = 300,
                recordingId = "rec_3",
                capturedUT = 7000.0,
                hasOrbit = false,
                hasSurface = true,
                surfacePosition = surfPos
            };

            var rec = new Recording
            {
                RecordingId = "rec_3"
            };

            ParsekFlight.ApplyTerminalDestruction(pending, rec);

            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.Equal(7000.0, rec.ExplicitEndUT);
            Assert.True(rec.TerminalPosition.HasValue);
            Assert.Equal("Mun", rec.TerminalPosition.Value.body);
            Assert.Equal(12.5, rec.TerminalPosition.Value.latitude);
            Assert.Equal(-45.3, rec.TerminalPosition.Value.longitude);
            Assert.Equal(100.0, rec.TerminalPosition.Value.altitude);
            Assert.Equal(SurfaceSituation.Landed, rec.TerminalPosition.Value.situation);
        }

        [Fact]
        public void ApplyTerminalDestruction_WithNeitherData_LeavesDefaults()
        {
            var pending = new ParsekFlight.PendingDestruction
            {
                vesselPid = 400,
                recordingId = "rec_4",
                capturedUT = 8000.0,
                hasOrbit = false,
                hasSurface = false
            };

            var rec = new Recording
            {
                RecordingId = "rec_4"
            };

            ParsekFlight.ApplyTerminalDestruction(pending, rec);

            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.Equal(8000.0, rec.ExplicitEndUT);
            // Orbit fields should remain at default (0)
            Assert.Equal(0.0, rec.TerminalOrbitInclination);
            Assert.Equal(0.0, rec.TerminalOrbitEccentricity);
            Assert.Equal(0.0, rec.TerminalOrbitSemiMajorAxis);
            Assert.Null(rec.TerminalOrbitBody);
            // Surface position should remain null
            Assert.Null(rec.TerminalPosition);
        }

        #endregion

        #region ShouldDeferDestructionCheck

        [Fact]
        public void ShouldDeferDestructionCheck_NoTree_ReturnsFalse()
        {
            var docking = new HashSet<uint>();
            var bgMap = new Dictionary<uint, string> { { 100, "rec_1" } };

            Assert.False(ParsekFlight.ShouldDeferDestructionCheck(100, false, docking, bgMap));
        }

        [Fact]
        public void ShouldDeferDestructionCheck_InDockingInProgress_ReturnsFalse()
        {
            var docking = new HashSet<uint> { 100 };
            var bgMap = new Dictionary<uint, string> { { 100, "rec_1" } };

            Assert.False(ParsekFlight.ShouldDeferDestructionCheck(100, true, docking, bgMap));
        }

        [Fact]
        public void ShouldDeferDestructionCheck_NotInBackgroundMap_ReturnsFalse()
        {
            var docking = new HashSet<uint>();
            var bgMap = new Dictionary<uint, string> { { 200, "rec_2" } };

            Assert.False(ParsekFlight.ShouldDeferDestructionCheck(100, true, docking, bgMap));
        }

        [Fact]
        public void ShouldDeferDestructionCheck_ValidTreeVessel_ReturnsTrue()
        {
            var docking = new HashSet<uint>();
            var bgMap = new Dictionary<uint, string> { { 100, "rec_1" } };

            Assert.True(ParsekFlight.ShouldDeferDestructionCheck(100, true, docking, bgMap));
        }

        #endregion

        #region IsTrulyDestroyed

        [Fact]
        public void IsTrulyDestroyed_VesselStillExists_ReturnsFalse()
        {
            var docking = new HashSet<uint>();

            Assert.False(ParsekFlight.IsTrulyDestroyed(100, docking, vesselStillExists: true));
        }

        [Fact]
        public void IsTrulyDestroyed_VesselGone_ReturnsTrue()
        {
            var docking = new HashSet<uint>();

            Assert.True(ParsekFlight.IsTrulyDestroyed(100, docking, vesselStillExists: false));
        }

        [Fact]
        public void IsTrulyDestroyed_VesselGoneButDockingInProgress_ReturnsFalse()
        {
            var docking = new HashSet<uint> { 100 };

            Assert.False(ParsekFlight.IsTrulyDestroyed(100, docking, vesselStillExists: false));
        }

        #endregion

        #region RebuildBackgroundMap excludes Destroyed recordings

        [Fact]
        public void RebuildBackgroundMap_ExcludesDestroyedRecordings()
        {
            var tree = new RecordingTree
            {
                Id = "tree_destroy",
                TreeName = "Destroy Test",
                RootRecordingId = "root",
                ActiveRecordingId = "active"
            };

            // Destroyed recording (should be excluded from BackgroundMap)
            tree.Recordings["destroyed_rec"] = new Recording
            {
                RecordingId = "destroyed_rec",
                VesselPersistentId = 100,
                TerminalStateValue = TerminalState.Destroyed,
                TreeId = tree.Id
            };

            // Active recording (excluded because it's the active recording)
            tree.Recordings["active"] = new Recording
            {
                RecordingId = "active",
                VesselPersistentId = 200,
                TreeId = tree.Id
            };

            // Normal background recording (should be included)
            tree.Recordings["bg_rec"] = new Recording
            {
                RecordingId = "bg_rec",
                VesselPersistentId = 300,
                TreeId = tree.Id
            };

            tree.RebuildBackgroundMap();

            Assert.False(tree.BackgroundMap.ContainsKey(100)); // Destroyed -> excluded
            Assert.False(tree.BackgroundMap.ContainsKey(200)); // Active -> excluded
            Assert.True(tree.BackgroundMap.ContainsKey(300));  // Normal background -> included
            Assert.Equal("bg_rec", tree.BackgroundMap[300]);
        }

        #endregion

        #region ApplyTerminalData edge cases

        [Fact]
        public void ApplyTerminalData_OrbitAndSurface_BothApplied()
        {
            // Edge case: both hasOrbit and hasSurface are true (should not normally happen,
            // but the method should handle it gracefully by applying both)
            var surfPos = new SurfacePosition
            {
                body = "Kerbin",
                latitude = 0.1,
                longitude = -74.5,
                altitude = 10.0,
                situation = SurfaceSituation.Landed
            };

            var data = new ParsekFlight.PendingDestruction
            {
                hasOrbit = true,
                inclination = 10.0,
                eccentricity = 0.5,
                semiMajorAxis = 800000.0,
                lan = 30.0,
                argumentOfPeriapsis = 60.0,
                meanAnomalyAtEpoch = 2.0,
                epoch = 9000.0,
                bodyName = "Kerbin",
                hasSurface = true,
                surfacePosition = surfPos
            };

            var rec = new Recording { RecordingId = "rec_edge" };

            ParsekFlight.ApplyTerminalData(data, rec);

            // Both orbit and surface should be populated
            Assert.Equal(10.0, rec.TerminalOrbitInclination);
            Assert.Equal("Kerbin", rec.TerminalOrbitBody);
            Assert.True(rec.TerminalPosition.HasValue);
            Assert.Equal("Kerbin", rec.TerminalPosition.Value.body);
        }

        [Fact]
        public void ApplyTerminalDestruction_OverwritesExistingData()
        {
            // Verify that calling ApplyTerminalDestruction overwrites the recording's
            // terminal fields (this is the expected behavior — the method is the authority)
            var rec = new Recording
            {
                RecordingId = "rec_overwrite",
                TerminalOrbitInclination = 99.9 // pre-existing value
            };

            var pending = new ParsekFlight.PendingDestruction
            {
                vesselPid = 500,
                recordingId = "rec_overwrite",
                capturedUT = 10000.0,
                hasOrbit = true,
                inclination = 28.5,
                eccentricity = 0.0,
                semiMajorAxis = 600000.0,
                lan = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = 0.0,
                bodyName = "Kerbin",
                hasSurface = false
            };

            ParsekFlight.ApplyTerminalDestruction(pending, rec);

            Assert.Equal(28.5, rec.TerminalOrbitInclination); // overwritten
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
        }

        #endregion

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }
    }
}
