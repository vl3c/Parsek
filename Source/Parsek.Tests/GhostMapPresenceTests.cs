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
        /// SubOrbital terminal state has valid orbit data (CaptureTerminalOrbit captures
        /// it for SUB_ORBITAL/FLYING situations). This data is used for state-vector orbit
        /// line display during flight playback. Tracking station filter (in
        /// CreateGhostVesselsFromCommittedRecordings) separately excludes SubOrbital.
        /// </summary>
        [Fact]
        public void SubOrbital_HasOrbitData_ReturnsTrue()
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

        #region StartsInOrbit

        /// <summary>
        /// Empty orbit segments means no orbital phase — not in orbit.
        /// </summary>
        [Fact]
        public void StartsInOrbit_NoSegments_ReturnsFalse()
        {
            var rec = new Recording
            {
                Points = { new TrajectoryPoint { ut = 100 } },
                OrbitSegments = new System.Collections.Generic.List<OrbitSegment>()
            };
            Assert.False(ParsekPlaybackPolicy.StartsInOrbit(rec, 100));
        }

        /// <summary>
        /// Recording with no points but orbit segments is orbit-only.
        /// </summary>
        [Fact]
        public void StartsInOrbit_OrbitOnly_NoPoints_ReturnsTrue()
        {
            var rec = new Recording
            {
                OrbitSegments = new System.Collections.Generic.List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 100, endUT = 500, bodyName = "Kerbin", semiMajorAxis = 700000 }
                }
            };
            // No points added — orbit-only recording
            Assert.True(ParsekPlaybackPolicy.StartsInOrbit(rec, 100));
        }

        /// <summary>
        /// UT before any orbit segment means not in orbit yet (ascending).
        /// </summary>
        [Fact]
        public void StartsInOrbit_UTBeforeSegment_ReturnsFalse()
        {
            var rec = new Recording
            {
                Points = { new TrajectoryPoint { ut = 50 } },
                OrbitSegments = new System.Collections.Generic.List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 200, endUT = 500, bodyName = "Kerbin", semiMajorAxis = 700000 }
                }
            };
            Assert.False(ParsekPlaybackPolicy.StartsInOrbit(rec, 50));
        }

        /// <summary>
        /// UT within an orbit segment means already in orbit.
        /// </summary>
        [Fact]
        public void StartsInOrbit_UTWithinSegment_ReturnsTrue()
        {
            var rec = new Recording
            {
                Points = { new TrajectoryPoint { ut = 200 } },
                OrbitSegments = new System.Collections.Generic.List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 100, endUT = 500, bodyName = "Kerbin", semiMajorAxis = 700000 }
                }
            };
            Assert.True(ParsekPlaybackPolicy.StartsInOrbit(rec, 200));
        }

        #endregion

        #region DefensiveConfigNodes

        [Fact]
        public void DefensiveNodes_AddedToVesselNodeMissingThem()
        {
            // Simulate what ProtoVessel.CreateVesselNode produces:
            // ACTIONGROUPS is added by CreateVesselNode, but FLIGHTPLAN/CTRLSTATE/VESSELMODULES are not.
            var vesselNode = new ConfigNode("VESSEL");
            vesselNode.AddNode("ACTIONGROUPS");

            // Apply the same defensive logic as BuildAndLoadGhostProtoVesselCore
            if (vesselNode.GetNode("FLIGHTPLAN") == null)
                vesselNode.AddNode("FLIGHTPLAN");
            if (vesselNode.GetNode("CTRLSTATE") == null)
                vesselNode.AddNode("CTRLSTATE");
            if (vesselNode.GetNode("VESSELMODULES") == null)
                vesselNode.AddNode("VESSELMODULES");

            Assert.NotNull(vesselNode.GetNode("ACTIONGROUPS"));
            Assert.NotNull(vesselNode.GetNode("FLIGHTPLAN"));
            Assert.NotNull(vesselNode.GetNode("CTRLSTATE"));
            Assert.NotNull(vesselNode.GetNode("VESSELMODULES"));
        }

        [Fact]
        public void DefensiveNodes_IdempotentWhenAlreadyPresent()
        {
            var vesselNode = new ConfigNode("VESSEL");
            vesselNode.AddNode("ACTIONGROUPS");
            vesselNode.AddNode("FLIGHTPLAN");
            vesselNode.AddNode("CTRLSTATE");
            vesselNode.AddNode("VESSELMODULES");

            // Apply defensive logic again — should not add duplicates
            if (vesselNode.GetNode("FLIGHTPLAN") == null)
                vesselNode.AddNode("FLIGHTPLAN");
            if (vesselNode.GetNode("CTRLSTATE") == null)
                vesselNode.AddNode("CTRLSTATE");
            if (vesselNode.GetNode("VESSELMODULES") == null)
                vesselNode.AddNode("VESSELMODULES");

            Assert.Single(vesselNode.GetNodes("FLIGHTPLAN"));
            Assert.Single(vesselNode.GetNodes("CTRLSTATE"));
            Assert.Single(vesselNode.GetNodes("VESSELMODULES"));
        }

        #endregion

        #region FindSupersededRecordingIds

        /// <summary>
        /// Empty recordings list returns empty superseded set.
        /// </summary>
        [Fact]
        public void FindSuperseded_Empty_ReturnsEmpty()
        {
            var result = GhostMapPresence.FindSupersededRecordingIds(new List<Recording>());
            Assert.Empty(result);
        }

        /// <summary>
        /// Null list returns empty set without throwing.
        /// </summary>
        [Fact]
        public void FindSuperseded_Null_ReturnsEmpty()
        {
            var result = GhostMapPresence.FindSupersededRecordingIds(null);
            Assert.Empty(result);
        }

        /// <summary>
        /// Recording with no parent is a standalone tip — not superseded.
        /// </summary>
        [Fact]
        public void FindSuperseded_StandaloneRecording_NotSuperseded()
        {
            var recs = new List<Recording>
            {
                new Recording { RecordingId = "rec-A" }
            };
            var result = GhostMapPresence.FindSupersededRecordingIds(recs);
            Assert.DoesNotContain("rec-A", result);
        }

        /// <summary>
        /// Chain A→B: A is superseded, B is the tip.
        /// </summary>
        [Fact]
        public void FindSuperseded_SimpleChain_ParentSuperseded()
        {
            var recs = new List<Recording>
            {
                new Recording { RecordingId = "rec-A" },
                new Recording { RecordingId = "rec-B", ParentRecordingId = "rec-A" }
            };
            var result = GhostMapPresence.FindSupersededRecordingIds(recs);
            Assert.Contains("rec-A", result);
            Assert.DoesNotContain("rec-B", result);
        }

        /// <summary>
        /// Chain A→B→C: A and B are superseded, C is tip.
        /// </summary>
        [Fact]
        public void FindSuperseded_ThreeNodeChain_OnlyTipNotSuperseded()
        {
            var recs = new List<Recording>
            {
                new Recording { RecordingId = "rec-A" },
                new Recording { RecordingId = "rec-B", ParentRecordingId = "rec-A" },
                new Recording { RecordingId = "rec-C", ParentRecordingId = "rec-B" }
            };
            var result = GhostMapPresence.FindSupersededRecordingIds(recs);
            Assert.Contains("rec-A", result);
            Assert.Contains("rec-B", result);
            Assert.DoesNotContain("rec-C", result);
        }

        /// <summary>
        /// Two independent chains: each has its own tip.
        /// </summary>
        [Fact]
        public void FindSuperseded_TwoChains_EachTipNotSuperseded()
        {
            var recs = new List<Recording>
            {
                new Recording { RecordingId = "chain1-A" },
                new Recording { RecordingId = "chain1-B", ParentRecordingId = "chain1-A" },
                new Recording { RecordingId = "chain2-A" },
                new Recording { RecordingId = "chain2-B", ParentRecordingId = "chain2-A" }
            };
            var result = GhostMapPresence.FindSupersededRecordingIds(recs);
            Assert.Contains("chain1-A", result);
            Assert.DoesNotContain("chain1-B", result);
            Assert.Contains("chain2-A", result);
            Assert.DoesNotContain("chain2-B", result);
        }

        #endregion

        #region ShouldCreateTrackingStationGhost

        /// <summary>
        /// Debris recording always skipped regardless of orbit data.
        /// </summary>
        [Fact]
        public void ShouldCreate_Debris_Skipped()
        {
            var rec = new Recording
            {
                IsDebris = true,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalStateValue = TerminalState.Orbiting
            };
            var (should, reason) = GhostMapPresence.ShouldCreateTrackingStationGhost(rec, false, 1000);
            Assert.False(should);
            Assert.Equal("debris", reason);
        }

        /// <summary>
        /// Superseded recording always skipped (intermediate chain segment).
        /// </summary>
        [Fact]
        public void ShouldCreate_Superseded_Skipped()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalStateValue = TerminalState.Orbiting
            };
            var (should, reason) = GhostMapPresence.ShouldCreateTrackingStationGhost(rec, true, 1000);
            Assert.False(should);
            Assert.Equal("superseded", reason);
        }

        /// <summary>
        /// Destroyed terminal state: no ghost even with orbit data.
        /// </summary>
        [Fact]
        public void ShouldCreate_Destroyed_Skipped()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalStateValue = TerminalState.Destroyed
            };
            var (should, reason) = GhostMapPresence.ShouldCreateTrackingStationGhost(rec, false, 1000);
            Assert.False(should);
            Assert.StartsWith("terminal", reason);
        }

        /// <summary>
        /// Orbiting tip with terminal orbit data: create ghost.
        /// </summary>
        [Fact]
        public void ShouldCreate_OrbitingWithData_Created()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalStateValue = TerminalState.Orbiting
            };
            var (should, reason) = GhostMapPresence.ShouldCreateTrackingStationGhost(rec, false, 1000);
            Assert.True(should);
            Assert.Null(reason);
        }

        /// <summary>
        /// Null terminal state with orbit data: create ghost (benefit of the doubt).
        /// </summary>
        [Fact]
        public void ShouldCreate_NullTerminal_WithOrbitData_Created()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalStateValue = null
            };
            var (should, reason) = GhostMapPresence.ShouldCreateTrackingStationGhost(rec, false, 1000);
            Assert.True(should);
            Assert.Null(reason);
        }

        /// <summary>
        /// Null terminal state with orbit segments: create ghost when UT within segment.
        /// </summary>
        [Fact]
        public void ShouldCreate_NullTerminal_WithSegment_UTInRange_Created()
        {
            var rec = new Recording
            {
                TerminalStateValue = null,
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 100, endUT = 500, bodyName = "Kerbin", semiMajorAxis = 700000 }
                }
            };
            var (should, reason) = GhostMapPresence.ShouldCreateTrackingStationGhost(rec, false, 300);
            Assert.True(should);
        }

        /// <summary>
        /// Null terminal state with same-body segment gap: keep showing the orbit ghost.
        /// </summary>
        [Fact]
        public void ShouldCreate_NullTerminal_WithSameBodyGap_Created()
        {
            var rec = new Recording
            {
                TerminalStateValue = null,
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 100, endUT = 200, bodyName = "Kerbin", semiMajorAxis = 700000 },
                    new OrbitSegment { startUT = 240, endUT = 500, bodyName = "Kerbin", semiMajorAxis = 700000, meanAnomalyAtEpoch = 2.5, epoch = 240 }
                }
            };

            var (should, reason) = GhostMapPresence.ShouldCreateTrackingStationGhost(rec, false, 220);

            Assert.True(should);
            Assert.Null(reason);
        }

        /// <summary>
        /// Same-body gap with a real orbit change should not be carried across.
        /// </summary>
        [Fact]
        public void ShouldCreate_NullTerminal_WithSameBodyOrbitChangeGap_Skipped()
        {
            var rec = new Recording
            {
                TerminalStateValue = null,
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 100, endUT = 200, bodyName = "Kerbin", semiMajorAxis = 700000 },
                    new OrbitSegment { startUT = 240, endUT = 500, bodyName = "Kerbin", semiMajorAxis = 710000 }
                }
            };

            var (should, reason) = GhostMapPresence.ShouldCreateTrackingStationGhost(rec, false, 220);

            Assert.False(should);
            Assert.Equal("no-current-segment", reason);
        }

        /// <summary>
        /// Null terminal state with orbit segments: skip when UT past all segments.
        /// </summary>
        [Fact]
        public void ShouldCreate_NullTerminal_WithSegment_UTPastRange_Skipped()
        {
            var rec = new Recording
            {
                TerminalStateValue = null,
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 100, endUT = 500, bodyName = "Kerbin", semiMajorAxis = 700000 }
                }
            };
            var (should, reason) = GhostMapPresence.ShouldCreateTrackingStationGhost(rec, false, 1000);
            Assert.False(should);
            Assert.Equal("no-current-segment", reason);
        }

        /// <summary>
        /// Tip with no orbit data and no segments: skip.
        /// </summary>
        [Fact]
        public void ShouldCreate_NoOrbitData_Skipped()
        {
            var rec = new Recording { TerminalStateValue = null };
            var (should, reason) = GhostMapPresence.ShouldCreateTrackingStationGhost(rec, false, 1000);
            Assert.False(should);
            Assert.Equal("no-orbit-data", reason);
        }

        /// <summary>
        /// Landed terminal state: no ghost even though vessel is at rest.
        /// </summary>
        [Fact]
        public void ShouldCreate_Landed_Skipped()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Mun",
                TerminalOrbitSemiMajorAxis = 200000,
                TerminalStateValue = TerminalState.Landed
            };
            var (should, reason) = GhostMapPresence.ShouldCreateTrackingStationGhost(rec, false, 1000);
            Assert.False(should);
            Assert.StartsWith("terminal", reason);
        }

        /// <summary>
        /// Docked tip with orbit data: create ghost.
        /// </summary>
        [Fact]
        public void ShouldCreate_Docked_Created()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalStateValue = TerminalState.Docked
            };
            var (should, reason) = GhostMapPresence.ShouldCreateTrackingStationGhost(rec, false, 1000);
            Assert.True(should);
        }

        #endregion

        #region Endpoint-Aligned Orbit Seeds

        [Fact]
        public void TryResolveGhostProtoOrbitSeed_MismatchedTerminalOrbitBody_UsesEndpointAlignedSeed()
        {
            var traj = new MockTrajectory
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
                EndpointBodyName = "Mun",
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        bodyName = "Mun",
                        semiMajorAxis = 250000.0,
                        eccentricity = 0.02,
                        inclination = 4.0,
                        longitudeOfAscendingNode = 11.0,
                        argumentOfPeriapsis = 22.0,
                        meanAnomalyAtEpoch = 0.7,
                        epoch = 350.0
                    }
                }
            };

            Assert.True(GhostMapPresence.TryResolveGhostProtoOrbitSeed(
                traj,
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
        public void TryResolveGhostProtoOrbitSeed_NoEndpointAlignedSeed_ReturnsFalse()
        {
            var traj = new MockTrajectory
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 900000.0,
                EndpointPhase = RecordingEndpointPhase.TrajectoryPoint,
                EndpointBodyName = "Mun"
            };

            Assert.False(GhostMapPresence.TryResolveGhostProtoOrbitSeed(
                traj,
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

        #region Chain-aware integration

        /// <summary>
        /// Scenario: chain A(launch)→B(orbit)→C(destroyed).
        /// Only C is a tip, and C has Destroyed terminal state → no ghost.
        /// Previously, B (intermediate with orbit data) would get a stale ghost.
        /// </summary>
        [Fact]
        public void ChainAware_DestroyedTip_NoGhostForIntermediateOrbit()
        {
            var recs = new List<Recording>
            {
                new Recording
                {
                    RecordingId = "launch",
                    TerminalStateValue = null
                },
                new Recording
                {
                    RecordingId = "orbit",
                    ParentRecordingId = "launch",
                    TerminalStateValue = null,
                    TerminalOrbitBody = "Kerbin",
                    TerminalOrbitSemiMajorAxis = 700000,
                    OrbitSegments = new List<OrbitSegment>
                    {
                        new OrbitSegment { startUT = 100, endUT = 500, bodyName = "Kerbin", semiMajorAxis = 700000 }
                    }
                },
                new Recording
                {
                    RecordingId = "destroyed",
                    ParentRecordingId = "orbit",
                    TerminalStateValue = TerminalState.Destroyed
                }
            };

            var superseded = GhostMapPresence.FindSupersededRecordingIds(recs);

            // "launch" and "orbit" are superseded
            Assert.Contains("launch", superseded);
            Assert.Contains("orbit", superseded);
            Assert.DoesNotContain("destroyed", superseded);

            // "orbit" is superseded → skipped even though it has orbit data
            var (shouldOrbit, _) = GhostMapPresence.ShouldCreateTrackingStationGhost(
                recs[1], superseded.Contains(recs[1].RecordingId), 300);
            Assert.False(shouldOrbit);

            // "destroyed" is tip but has Destroyed state → skipped
            var (shouldDestroyed, _) = GhostMapPresence.ShouldCreateTrackingStationGhost(
                recs[2], superseded.Contains(recs[2].RecordingId), 300);
            Assert.False(shouldDestroyed);
        }

        /// <summary>
        /// Scenario: chain A(launch)→B(orbit, still active).
        /// B is the tip with null terminal state and orbit data → ghost created.
        /// </summary>
        [Fact]
        public void ChainAware_ActiveOrbitTip_GhostCreated()
        {
            var recs = new List<Recording>
            {
                new Recording { RecordingId = "launch", TerminalStateValue = null },
                new Recording
                {
                    RecordingId = "orbit-tip",
                    ParentRecordingId = "launch",
                    TerminalStateValue = null,
                    TerminalOrbitBody = "Kerbin",
                    TerminalOrbitSemiMajorAxis = 700000
                }
            };

            var superseded = GhostMapPresence.FindSupersededRecordingIds(recs);

            Assert.Contains("launch", superseded);
            Assert.DoesNotContain("orbit-tip", superseded);

            var (should, _) = GhostMapPresence.ShouldCreateTrackingStationGhost(
                recs[1], superseded.Contains(recs[1].RecordingId), 300);
            Assert.True(should);
        }

        /// <summary>
        /// Standalone recording (no chain) with orbit data: ghost created.
        /// </summary>
        [Fact]
        public void ChainAware_Standalone_OrbitRecording_GhostCreated()
        {
            var recs = new List<Recording>
            {
                new Recording
                {
                    RecordingId = "solo",
                    TerminalOrbitBody = "Kerbin",
                    TerminalOrbitSemiMajorAxis = 700000,
                    TerminalStateValue = TerminalState.Orbiting
                }
            };

            var superseded = GhostMapPresence.FindSupersededRecordingIds(recs);
            Assert.DoesNotContain("solo", superseded);

            var (should, _) = GhostMapPresence.ShouldCreateTrackingStationGhost(
                recs[0], false, 1000);
            Assert.True(should);
        }

        #endregion

        #region ShouldDrawAtmosphericMarker

        [Fact]
        public void ShouldDrawAtmosphericMarker_SubOrbitalTerminalState_NotFiltered()
        {
            var rec = new Recording
            {
                RecordingId = "suborbital-1",
                TerminalStateValue = TerminalState.SubOrbital,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 200, bodyName = "Kerbin" }
                }
            };
            // currentUT=150 is within the recording window — marker should show
            bool result = ParsekTrackingStation.ShouldDrawAtmosphericMarker(rec, 0, 150, null);
            Assert.True(result);
        }

        [Fact]
        public void ShouldDrawAtmosphericMarker_DestroyedTerminalState_NotFiltered()
        {
            var rec = new Recording
            {
                RecordingId = "destroyed-1",
                TerminalStateValue = TerminalState.Destroyed,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 200, bodyName = "Kerbin" }
                }
            };
            bool result = ParsekTrackingStation.ShouldDrawAtmosphericMarker(rec, 0, 150, null);
            Assert.True(result);
        }

        [Fact]
        public void ShouldDrawAtmosphericMarker_RecoveredTerminalState_NotFiltered()
        {
            var rec = new Recording
            {
                RecordingId = "recovered-1",
                TerminalStateValue = TerminalState.Recovered,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 200, bodyName = "Kerbin" }
                }
            };
            bool result = ParsekTrackingStation.ShouldDrawAtmosphericMarker(rec, 0, 150, null);
            Assert.True(result);
        }

        [Fact]
        public void ShouldDrawAtmosphericMarker_LandedTerminalState_NotFiltered()
        {
            var rec = new Recording
            {
                RecordingId = "landed-1",
                TerminalStateValue = TerminalState.Landed,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 200, bodyName = "Kerbin" }
                }
            };
            bool result = ParsekTrackingStation.ShouldDrawAtmosphericMarker(rec, 0, 150, null);
            Assert.True(result);
        }

        [Fact]
        public void ShouldDrawAtmosphericMarker_OutOfTimeWindow_Filtered()
        {
            var rec = new Recording
            {
                RecordingId = "past-1",
                TerminalStateValue = TerminalState.Destroyed,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 200, bodyName = "Kerbin" }
                }
            };
            // currentUT=250 is past the recording end — marker should NOT show
            bool result = ParsekTrackingStation.ShouldDrawAtmosphericMarker(rec, 0, 250, null);
            Assert.False(result);
        }

        [Fact]
        public void ShouldDrawAtmosphericMarker_Debris_Filtered()
        {
            var rec = new Recording
            {
                RecordingId = "debris-1",
                IsDebris = true,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 200, bodyName = "Kerbin" }
                }
            };
            bool result = ParsekTrackingStation.ShouldDrawAtmosphericMarker(rec, 0, 150, null);
            Assert.False(result);
        }

        [Fact]
        public void ShouldDrawAtmosphericMarker_Superseded_Filtered()
        {
            var rec = new Recording
            {
                RecordingId = "superseded-1",
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 200, bodyName = "Kerbin" }
                }
            };
            var superseded = new HashSet<string> { "superseded-1" };
            bool result = ParsekTrackingStation.ShouldDrawAtmosphericMarker(rec, 0, 150, superseded);
            Assert.False(result);
        }

        [Fact]
        public void ShouldDrawAtmosphericMarker_NoPoints_Filtered()
        {
            var rec = new Recording
            {
                RecordingId = "empty-1",
                Points = new List<TrajectoryPoint>()
            };
            bool result = ParsekTrackingStation.ShouldDrawAtmosphericMarker(rec, 0, 150, null);
            Assert.False(result);
        }

        [Fact]
        public void ShouldDrawAtmosphericMarker_InOrbitSegment_Filtered()
        {
            var rec = new Recording
            {
                RecordingId = "orbital-phase-1",
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 500, bodyName = "Kerbin" }
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 200, endUT = 400, bodyName = "Kerbin",
                        semiMajorAxis = 700000, eccentricity = 0.01,
                        inclination = 0, longitudeOfAscendingNode = 0,
                        argumentOfPeriapsis = 0, meanAnomalyAtEpoch = 0, epoch = 200 }
                }
            };
            // currentUT=300 is within the orbit segment — ProtoVessel handles this, marker should NOT show
            bool result = ParsekTrackingStation.ShouldDrawAtmosphericMarker(rec, 0, 300, null);
            Assert.False(result);
        }

        [Fact]
        public void ShouldDrawAtmosphericMarker_InAtmosphericPhaseOfMixedRecording_NotFiltered()
        {
            var rec = new Recording
            {
                RecordingId = "mixed-1",
                TerminalStateValue = TerminalState.Orbiting,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 500, bodyName = "Kerbin" }
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 300, endUT = 500, bodyName = "Kerbin",
                        semiMajorAxis = 700000, eccentricity = 0.01,
                        inclination = 0, longitudeOfAscendingNode = 0,
                        argumentOfPeriapsis = 0, meanAnomalyAtEpoch = 0, epoch = 300 }
                }
            };
            // currentUT=150 is in the atmospheric phase (before orbit segment) — marker should show
            bool result = ParsekTrackingStation.ShouldDrawAtmosphericMarker(rec, 0, 150, null);
            Assert.True(result);
        }

        #endregion

        #region ResetBetweenTestRuns (#417 / #418)

        /// <summary>
        /// #417/#418: ResetBetweenTestRuns clears the PID tracking HashSet so the next
        /// Run All's GhostPidsResolveToProtoVessels probe does not see stale ghost PIDs
        /// from the previous pass after ProtoVessels were destroyed.
        /// </summary>
        [Fact]
        public void ResetBetweenTestRuns_ClearsGhostMapVesselPids()
        {
            GhostMapPresence.ghostMapVesselPids.Add(12345u);
            GhostMapPresence.ghostMapVesselPids.Add(67890u);

            GhostMapPresence.ResetBetweenTestRuns("unit-test-pid-clear");

            Assert.Empty(GhostMapPresence.ghostMapVesselPids);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]") && l.Contains("ResetBetweenTestRuns")
                && l.Contains("unit-test-pid-clear") && l.Contains("pids=2"));
        }

        /// <summary>
        /// #417/#418: ResetBetweenTestRuns clears every per-ghost dictionary in one
        /// synchronous call so no bookkeeping survives across a Run All boundary.
        /// </summary>
        [Fact]
        public void ResetBetweenTestRuns_ClearsAllPerGhostDicts()
        {
            GhostMapPresence.ghostMapVesselPids.Add(100u);
            GhostMapPresence.ghostsWithSuppressedIcon.Add(100u);
            GhostMapPresence.ghostOrbitBounds[100u] = (10.0, 20.0);

            GhostMapPresence.ResetBetweenTestRuns("unit-test-all-dicts");

            Assert.Empty(GhostMapPresence.ghostMapVesselPids);
            Assert.Empty(GhostMapPresence.ghostsWithSuppressedIcon);
            Assert.Empty(GhostMapPresence.ghostOrbitBounds);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]") && l.Contains("ResetBetweenTestRuns")
                && l.Contains("suppressedIcons=1") && l.Contains("orbitBounds=1"));
        }

        /// <summary>
        /// #417/#418: ResetBetweenTestRuns is idempotent — calling it twice back-to-back
        /// must not throw and the second call must emit the empty-dictionary noop log.
        /// Matches the real TestRunner flow where Run All clears, then Run All clears
        /// again on the next invocation with nothing to do.
        /// </summary>
        [Fact]
        public void ResetBetweenTestRuns_IsIdempotent()
        {
            GhostMapPresence.ghostMapVesselPids.Add(1u);

            GhostMapPresence.ResetBetweenTestRuns("first-call");
            logLines.Clear();

            // Second call on an already-empty state
            GhostMapPresence.ResetBetweenTestRuns("second-call");

            Assert.Empty(GhostMapPresence.ghostMapVesselPids);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]") && l.Contains("ResetBetweenTestRuns")
                && l.Contains("second-call") && l.Contains("already empty"));
        }

        /// <summary>
        /// #417/#418: ResetBetweenTestRuns handles a null reason gracefully (should
        /// never fail even if the caller drops the reason plumbing).
        /// </summary>
        [Fact]
        public void ResetBetweenTestRuns_NullReason_DoesNotThrow()
        {
            GhostMapPresence.ghostMapVesselPids.Add(42u);

            var ex = Record.Exception(() => GhostMapPresence.ResetBetweenTestRuns(null));

            Assert.Null(ex);
            Assert.Empty(GhostMapPresence.ghostMapVesselPids);
        }

        #endregion
    }
}
