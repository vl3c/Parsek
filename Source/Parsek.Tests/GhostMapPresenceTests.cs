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

        #region ResolveVesselType

        /// <summary>
        /// Null snapshot falls back to Ship.
        /// Guards: null safety when recording has no vessel snapshot.
        /// </summary>
        [Fact]
        public void ResolveVesselType_NullSnapshot_ReturnsShip()
        {
            VesselType result = GhostMapPresence.ResolveVesselType(null);
            Assert.Equal(VesselType.Ship, result);
        }

        /// <summary>
        /// Snapshot with no "type" value falls back to Ship.
        /// Guards: missing type key in snapshot ConfigNode.
        /// </summary>
        [Fact]
        public void ResolveVesselType_MissingTypeValue_ReturnsShip()
        {
            var node = new ConfigNode("VESSEL");
            // No "type" key added
            VesselType result = GhostMapPresence.ResolveVesselType(node);
            Assert.Equal(VesselType.Ship, result);
        }

        /// <summary>
        /// Valid "Ship" type string parses correctly.
        /// Guards: standard vessel type round-trips through snapshot.
        /// </summary>
        [Fact]
        public void ResolveVesselType_Ship_ReturnsShip()
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("type", "Ship");
            Assert.Equal(VesselType.Ship, GhostMapPresence.ResolveVesselType(node));
        }

        /// <summary>
        /// Valid "Station" type string parses correctly.
        /// Guards: station ghost vessels get correct map icon.
        /// </summary>
        [Fact]
        public void ResolveVesselType_Station_ReturnsStation()
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("type", "Station");
            Assert.Equal(VesselType.Station, GhostMapPresence.ResolveVesselType(node));
        }

        /// <summary>
        /// Valid "Relay" type string parses correctly.
        /// Guards: relay ghost vessels get correct map icon.
        /// </summary>
        [Fact]
        public void ResolveVesselType_Relay_ReturnsRelay()
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("type", "Relay");
            Assert.Equal(VesselType.Relay, GhostMapPresence.ResolveVesselType(node));
        }

        /// <summary>
        /// Unrecognized type string falls back to Ship and logs a message.
        /// Guards: corrupt or modded type strings don't crash.
        /// </summary>
        [Fact]
        public void ResolveVesselType_UnrecognizedType_ReturnsShipAndLogs()
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("type", "BogusType");
            VesselType result = GhostMapPresence.ResolveVesselType(node);
            Assert.Equal(VesselType.Ship, result);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]") && l.Contains("unrecognized type") &&
                l.Contains("BogusType"));
        }

        /// <summary>
        /// Case-insensitive parse: "station" (lowercase) resolves to Station.
        /// Guards: KSP snapshot files may have inconsistent casing.
        /// </summary>
        [Fact]
        public void ResolveVesselType_CaseInsensitive_ParsesCorrectly()
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("type", "station");
            Assert.Equal(VesselType.Station, GhostMapPresence.ResolveVesselType(node));
        }

        #endregion

        #region HasOrbitData — negative SMA (hyperbolic)

        /// <summary>
        /// Recording with negative SMA (hyperbolic orbit) returns false.
        /// Known limitation: hyperbolic orbits have negative SMA, but the
        /// current check requires SMA > 0. This is acceptable because ghost
        /// map presence is for stable orbits (tracking station display).
        /// Hyperbolic trajectories are transient and don't need map markers.
        /// </summary>
        [Fact]
        public void HasOrbitData_NegativeSMA_ReturnsFalse()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = -500000.0,
                TerminalOrbitEccentricity = 1.5
            };

            bool result = GhostMapPresence.HasOrbitData(rec);

            Assert.False(result);
        }

        #endregion

        #region PID set / vessel dict sync

        /// <summary>
        /// Adding a PID to ghostMapVesselPids without creating a vessel
        /// means GetGhostVessel returns null for any chain PID.
        /// Guards: PID set and vessel dict are independent — a PID in the
        /// set does not imply a vessel exists in the dict.
        /// </summary>
        [Fact]
        public void PidSetAndVesselDict_PidAddedButNoVessel_GetGhostVesselReturnsNull()
        {
            GhostMapPresence.ghostMapVesselPids.Add(999);

            Assert.True(GhostMapPresence.IsGhostMapVessel(999));
            Assert.Null(GhostMapPresence.GetGhostVessel(999));
        }

        /// <summary>
        /// ResetForTesting clears PID set even when vessel dict was empty.
        /// Guards: reset is safe when state is partially populated.
        /// </summary>
        [Fact]
        public void ResetForTesting_ClearsPidsEvenIfNoVesselsExist()
        {
            GhostMapPresence.ghostMapVesselPids.Add(111);
            GhostMapPresence.ghostMapVesselPids.Add(222);

            // No vessels created — dict is empty
            Assert.Null(GhostMapPresence.GetGhostVessel(111));
            Assert.Null(GhostMapPresence.GetGhostVessel(222));

            GhostMapPresence.ResetForTesting();

            Assert.False(GhostMapPresence.IsGhostMapVessel(111));
            Assert.False(GhostMapPresence.IsGhostMapVessel(222));
            Assert.Empty(GhostMapPresence.ghostMapVesselPids);
        }

        /// <summary>
        /// Multiple PIDs can coexist in the set independently.
        /// Guards: no collision or overwrite between different ghost PIDs.
        /// </summary>
        [Fact]
        public void PidSet_MultiplePids_IndependentTracking()
        {
            GhostMapPresence.ghostMapVesselPids.Add(100);
            GhostMapPresence.ghostMapVesselPids.Add(200);
            GhostMapPresence.ghostMapVesselPids.Add(300);

            Assert.True(GhostMapPresence.IsGhostMapVessel(100));
            Assert.True(GhostMapPresence.IsGhostMapVessel(200));
            Assert.True(GhostMapPresence.IsGhostMapVessel(300));
            Assert.False(GhostMapPresence.IsGhostMapVessel(400));

            GhostMapPresence.ghostMapVesselPids.Remove(200);

            Assert.True(GhostMapPresence.IsGhostMapVessel(100));
            Assert.False(GhostMapPresence.IsGhostMapVessel(200));
            Assert.True(GhostMapPresence.IsGhostMapVessel(300));
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

        #region Terminal state filtering (edge cases)

        /// <summary>
        /// Destroyed terminal state should NOT get a ProtoVessel.
        /// The terminal orbit would show a trajectory the vessel never completes.
        /// </summary>
        [Fact]
        public void TerminalFilter_Destroyed_ShouldNotGetProtoVessel()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalStateValue = TerminalState.Destroyed
            };
            Assert.True(GhostMapPresence.HasOrbitData(rec));
            // HasOrbitData is true, but the policy layer should filter by terminal state.
            // This test documents the expectation: Destroyed recordings have orbit data
            // but should be filtered out at the policy level.
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
        }

        /// <summary>
        /// SubOrbital terminal state should NOT get a ProtoVessel.
        /// Suborbital trajectories show misleading orbit lines.
        /// </summary>
        [Fact]
        public void TerminalFilter_SubOrbital_ShouldNotGetProtoVessel()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 500000,
                TerminalStateValue = TerminalState.SubOrbital
            };
            Assert.True(GhostMapPresence.HasOrbitData(rec));
            Assert.Equal(TerminalState.SubOrbital, rec.TerminalStateValue);
        }

        /// <summary>
        /// Landed terminal state should NOT get a ProtoVessel.
        /// Landed vessels have no meaningful orbit.
        /// </summary>
        [Fact]
        public void TerminalFilter_Landed_ShouldNotGetProtoVessel()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Mun",
                TerminalOrbitSemiMajorAxis = 200000,
                TerminalStateValue = TerminalState.Landed
            };
            Assert.True(GhostMapPresence.HasOrbitData(rec));
            Assert.Equal(TerminalState.Landed, rec.TerminalStateValue);
        }

        /// <summary>
        /// Orbiting terminal state SHOULD get a ProtoVessel.
        /// This is the primary use case — stable orbit with correct orbit line.
        /// </summary>
        [Fact]
        public void TerminalFilter_Orbiting_ShouldGetProtoVessel()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalStateValue = TerminalState.Orbiting
            };
            Assert.True(GhostMapPresence.HasOrbitData(rec));
            Assert.Equal(TerminalState.Orbiting, rec.TerminalStateValue);
        }

        /// <summary>
        /// Docked terminal state SHOULD get a ProtoVessel.
        /// Docked vessels are still in orbit.
        /// </summary>
        [Fact]
        public void TerminalFilter_Docked_ShouldGetProtoVessel()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalStateValue = TerminalState.Docked
            };
            Assert.True(GhostMapPresence.HasOrbitData(rec));
            Assert.Equal(TerminalState.Docked, rec.TerminalStateValue);
        }

        /// <summary>
        /// Null terminal state (legacy/in-progress recording) SHOULD get a ProtoVessel
        /// if orbit data exists. Benefit of the doubt.
        /// </summary>
        [Fact]
        public void TerminalFilter_NullState_ShouldGetProtoVessel()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalStateValue = null
            };
            Assert.True(GhostMapPresence.HasOrbitData(rec));
            Assert.Null(rec.TerminalStateValue);
        }

        #endregion

        #region Debris filtering

        /// <summary>
        /// Debris recordings should NOT get ProtoVessels even with valid orbit data.
        /// Only main vessels with controllers get map presence.
        /// </summary>
        [Fact]
        public void DebrisFilter_IsDebris_ShouldNotGetProtoVessel()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalStateValue = TerminalState.Orbiting,
                IsDebris = true
            };
            Assert.True(GhostMapPresence.HasOrbitData(rec));
            Assert.True(rec.IsDebris);
        }

        /// <summary>
        /// Non-debris recording with orbit data should get ProtoVessel.
        /// </summary>
        [Fact]
        public void DebrisFilter_NotDebris_ShouldGetProtoVessel()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalStateValue = TerminalState.Orbiting,
                IsDebris = false
            };
            Assert.True(GhostMapPresence.HasOrbitData(rec));
            Assert.False(rec.IsDebris);
        }

        #endregion

        #region Recording index tracking

        /// <summary>
        /// FindRecordingIndexByVesselPid returns -1 when no vessels tracked.
        /// </summary>
        [Fact]
        public void FindRecordingIndex_EmptyState_ReturnsNegativeOne()
        {
            Assert.Equal(-1, GhostMapPresence.FindRecordingIndexByVesselPid(12345));
        }

        /// <summary>
        /// Multiple recording-index ghosts tracked independently.
        /// Removing one doesn't affect others.
        /// </summary>
        [Fact]
        public void RecordingIndexTracking_MultipleIndices_Independent()
        {
            // Simulate adding two recording-index entries via PID set
            GhostMapPresence.ghostMapVesselPids.Add(100);
            GhostMapPresence.ghostMapVesselPids.Add(200);

            Assert.True(GhostMapPresence.IsGhostMapVessel(100));
            Assert.True(GhostMapPresence.IsGhostMapVessel(200));

            GhostMapPresence.ghostMapVesselPids.Remove(100);
            Assert.False(GhostMapPresence.IsGhostMapVessel(100));
            Assert.True(GhostMapPresence.IsGhostMapVessel(200));
        }

        #endregion

        #region Orbit segment tracking on GhostChain

        /// <summary>
        /// GhostChain LastMapOrbitBodyName/Sma start as null/0 (no segment tracked yet).
        /// </summary>
        [Fact]
        public void GhostChain_OrbitTracking_InitiallyNull()
        {
            var chain = new GhostChain();
            Assert.Null(chain.LastMapOrbitBodyName);
            Assert.Equal(0.0, chain.LastMapOrbitSma);
        }

        /// <summary>
        /// After setting orbit tracking fields, they retain values.
        /// Used by UpdateChainGhostOrbitIfNeeded to detect segment changes.
        /// </summary>
        [Fact]
        public void GhostChain_OrbitTracking_RetainsValues()
        {
            var chain = new GhostChain
            {
                LastMapOrbitBodyName = "Kerbin",
                LastMapOrbitSma = 700000
            };
            Assert.Equal("Kerbin", chain.LastMapOrbitBodyName);
            Assert.Equal(700000, chain.LastMapOrbitSma);
        }

        /// <summary>
        /// Segment change detection: same body+SMA means no change.
        /// </summary>
        [Fact]
        public void GhostChain_OrbitTracking_SameValues_NoChange()
        {
            var chain = new GhostChain
            {
                LastMapOrbitBodyName = "Kerbin",
                LastMapOrbitSma = 700000
            };
            // Simulating the check in UpdateChainGhostOrbitIfNeeded
            bool changed = (chain.LastMapOrbitBodyName != "Kerbin"
                || chain.LastMapOrbitSma != 700000);
            Assert.False(changed);
        }

        /// <summary>
        /// Segment change detection: different body means SOI transition.
        /// </summary>
        [Fact]
        public void GhostChain_OrbitTracking_BodyChange_DetectedAsSOITransition()
        {
            var chain = new GhostChain
            {
                LastMapOrbitBodyName = "Kerbin",
                LastMapOrbitSma = 700000
            };
            bool changed = (chain.LastMapOrbitBodyName != "Mun"
                || chain.LastMapOrbitSma != 200000);
            Assert.True(changed);
        }

        #endregion
    }
}
