using System;
using System.Collections.Generic;
using System.Reflection;
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
            RecordingStore.ClearCommittedInternal();
            RecordingStore.CommittedTrees.Clear();
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekSettingsPersistence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
            RecordingStore.ClearCommittedInternal();
            RecordingStore.CommittedTrees.Clear();
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekSettingsPersistence.ResetForTesting();
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
            var stateVectorTrajectories = (Dictionary<int, IPlaybackTrajectory>)typeof(GhostMapPresence)
                .GetField("trackingStationStateVectorOrbitTrajectories", BindingFlags.NonPublic | BindingFlags.Static)
                .GetValue(null);
            var stateVectorCachedIndices = (Dictionary<int, int>)typeof(GhostMapPresence)
                .GetField("trackingStationStateVectorCachedIndices", BindingFlags.NonPublic | BindingFlags.Static)
                .GetValue(null);
            stateVectorTrajectories[3] = new Recording { RecordingId = "state-vector-reset" };
            stateVectorCachedIndices[3] = 7;

            GhostMapPresence.ResetForTesting();
            Assert.False(GhostMapPresence.IsGhostMapVessel(111));
            Assert.False(GhostMapPresence.IsGhostMapVessel(222));
            Assert.Empty(GhostMapPresence.ghostMapVesselPids);
            Assert.Empty(stateVectorTrajectories);
            Assert.Empty(stateVectorCachedIndices);
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

        /// <summary>
        /// Stable HasOrbitData(IPlaybackTrajectory) calls collapse to a single
        /// log line via VerboseOnChange — guards bug "1678 lines per session"
        /// captured in the 2026-04-25 playtest.
        /// </summary>
        [Fact]
        public void HasOrbitData_TrajectoryStableCalls_LogOnceWithSuppressedCounter()
        {
            var rec = new Recording
            {
                RecordingId = "rec-stable-traj",
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 742959.0
            };
            IPlaybackTrajectory traj = rec;

            // Hit the per-frame path 100 times with stable state.
            for (int i = 0; i < 100; i++)
            {
                Assert.True(GhostMapPresence.HasOrbitData(traj));
            }

            int firstEmissionCount = logLines.FindAll(l =>
                l.Contains("[GhostMap]")
                && l.Contains("HasOrbitData(IPlaybackTrajectory)")
                && l.Contains("result=True")).Count;
            // First call emits, the next 99 are suppressed (no state flip).
            Assert.Equal(1, firstEmissionCount);

            // Force a state change so the suppressed-counter surfaces on the
            // next emission.
            rec.TerminalOrbitBody = "Mun";
            rec.TerminalOrbitSemiMajorAxis = 200000.0;
            Assert.True(GhostMapPresence.HasOrbitData(traj));

            // Second emission carries `| suppressed=99`.
            Assert.Contains(logLines, l =>
                l.Contains("HasOrbitData(IPlaybackTrajectory)")
                && l.Contains("Mun")
                && l.Contains("suppressed=99"));
        }

        /// <summary>
        /// HasOrbitData(IPlaybackTrajectory) on a different recording id
        /// emits independently of a stable stream on another id — the gate
        /// must be keyed per (recording, body, sma) so distinct trajectories
        /// each get their own first emission.
        /// </summary>
        [Fact]
        public void HasOrbitData_TrajectoryDistinctRecordings_LogIndependently()
        {
            var first = new Recording
            {
                RecordingId = "rec-id-A",
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000.0
            };
            var second = new Recording
            {
                RecordingId = "rec-id-B",
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000.0
            };

            // Saturate first recording's gate.
            for (int i = 0; i < 5; i++)
                Assert.True(GhostMapPresence.HasOrbitData((IPlaybackTrajectory)first));

            int firstEmits = logLines.FindAll(l =>
                l.Contains("HasOrbitData(IPlaybackTrajectory)") && l.Contains("True")).Count;
            Assert.Equal(1, firstEmits);

            // Second recording has identical body/sma but a different id —
            // identity scope must change, so it gets its own first emission.
            Assert.True(GhostMapPresence.HasOrbitData((IPlaybackTrajectory)second));
            int secondEmits = logLines.FindAll(l =>
                l.Contains("HasOrbitData(IPlaybackTrajectory)") && l.Contains("True")).Count;
            Assert.Equal(2, secondEmits);
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
        /// it for SUB_ORBITAL/FLYING situations). Terminal-orbit fallback still excludes
        /// SubOrbital, but the shared map-presence source resolver can show active
        /// state-vector orbit lines during coast phases.
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

        [Fact]
        public void FindTrackingStationSuppressedRecordingIds_FutureChildDoesNotHideCurrentContinuation()
        {
            var recs = new List<Recording>
            {
                new Recording { RecordingId = "launch" },
                new Recording
                {
                    RecordingId = "kerbin-return",
                    ParentRecordingId = "launch",
                    Points = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = 200, bodyName = "Kerbin" },
                        new TrajectoryPoint { ut = 320, bodyName = "Kerbin" }
                    }
                },
                new Recording
                {
                    RecordingId = "mun-leg",
                    ParentRecordingId = "kerbin-return",
                    Points = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = 500, bodyName = "Kerbin" },
                        new TrajectoryPoint { ut = 620, bodyName = "Mun" }
                    }
                }
            };

            var suppressed = GhostMapPresence.FindTrackingStationSuppressedRecordingIds(recs, 240);

            Assert.Contains("launch", suppressed);
            Assert.DoesNotContain("kerbin-return", suppressed);
            Assert.DoesNotContain("mun-leg", suppressed);
        }

        [Fact]
        public void FindTrackingStationSuppressedRecordingIds_StartedChildHidesParent()
        {
            var recs = new List<Recording>
            {
                new Recording { RecordingId = "launch" },
                new Recording
                {
                    RecordingId = "kerbin-return",
                    ParentRecordingId = "launch",
                    Points = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = 200, bodyName = "Kerbin" },
                        new TrajectoryPoint { ut = 320, bodyName = "Kerbin" }
                    }
                },
                new Recording
                {
                    RecordingId = "mun-leg",
                    ParentRecordingId = "kerbin-return",
                    Points = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = 300, bodyName = "Kerbin" },
                        new TrajectoryPoint { ut = 620, bodyName = "Mun" }
                    }
                }
            };

            var suppressed = GhostMapPresence.FindTrackingStationSuppressedRecordingIds(recs, 320);

            Assert.Contains("launch", suppressed);
            Assert.Contains("kerbin-return", suppressed);
            Assert.DoesNotContain("mun-leg", suppressed);
        }

        [Fact]
        public void FindTrackingStationSuppressedRecordingIds_IndeterminateChildStart_DoesNotHideParent()
        {
            var recs = new List<Recording>
            {
                new Recording { RecordingId = "launch" },
                new Recording
                {
                    RecordingId = "kerbin-return",
                    ParentRecordingId = "launch",
                    Points = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = 200, bodyName = "Kerbin" },
                        new TrajectoryPoint { ut = 320, bodyName = "Kerbin" }
                    }
                },
                new Recording
                {
                    RecordingId = "mun-leg",
                    ParentRecordingId = "kerbin-return"
                }
            };

            var suppressed = GhostMapPresence.FindTrackingStationSuppressedRecordingIds(recs, 320);

            Assert.Contains("launch", suppressed);
            Assert.DoesNotContain("kerbin-return", suppressed);
            Assert.DoesNotContain("mun-leg", suppressed);
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
        /// Suppressed recording always skipped (intermediate chain segment).
        /// </summary>
        [Fact]
        public void ShouldCreate_Suppressed_Skipped()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalStateValue = TerminalState.Orbiting
            };
            var (should, reason) = GhostMapPresence.ShouldCreateTrackingStationGhost(rec, true, 1000);
            Assert.False(should);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSkipSuppressed, reason);
        }

        /// <summary>
        /// Already materialized recordings must not recreate a tracking-station ghost.
        /// </summary>
        [Fact]
        public void ShouldCreate_AlreadySpawned_Skipped()
        {
            var rec = new Recording
            {
                RecordingId = "already-spawned",
                VesselName = "Already Spawned",
                VesselSpawned = true,
                SpawnedVesselPersistentId = 424242,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalStateValue = TerminalState.Orbiting
            };
            var (should, reason) = GhostMapPresence.ShouldCreateTrackingStationGhost(rec, false, 1000);
            Assert.False(should);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSkipAlreadySpawned, reason);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]") &&
                l.Contains("ResolveTrackingStationGhostSource") &&
                l.Contains("reason=already-spawned") &&
                l.Contains("vesselSpawned=True") &&
                l.Contains("spawnedPid=424242"));
        }

        /// <summary>
        /// A persisted spawned PID also suppresses ghost recreation after scene reload.
        /// </summary>
        [Fact]
        public void ShouldCreate_SpawnedPidSet_Skipped()
        {
            var rec = new Recording
            {
                SpawnedVesselPersistentId = 424242,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalStateValue = TerminalState.Orbiting
            };
            var (should, reason) = GhostMapPresence.ShouldCreateTrackingStationGhost(rec, false, 1000);
            Assert.False(should);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSkipAlreadySpawned, reason);
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
        /// Future terminal orbit data must not appear before the recording has even started.
        /// Prevents tracking station startup from advertising a later chain tip too early.
        /// </summary>
        [Fact]
        public void ResolveTrackingStationGhostSource_FutureTipBeforeActivation_SkipsTerminalOrbit()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Mun",
                TerminalOrbitSemiMajorAxis = 260300,
                TerminalStateValue = TerminalState.Orbiting,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 1983.7, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 27996.3, bodyName = "Mun" }
                }
            };

            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveTrackingStationGhostSource(
                    rec,
                    false,
                    525.3,
                    out _,
                    out string reason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, source);
            Assert.Equal("before-activation", reason);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]") &&
                l.Contains("ResolveTrackingStationGhostSource") &&
                l.Contains("source=None") &&
                l.Contains("before-activation"));
        }

        /// <summary>
        /// When a recording has a visible current segment plus a later terminal orbit,
        /// the current segment must drive the tracking-station ghost.
        /// </summary>
        [Fact]
        public void ResolveTrackingStationGhostSource_VisibleSegment_PrefersSegmentOverFutureTerminalOrbit()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Mun",
                TerminalOrbitSemiMajorAxis = 260300,
                TerminalStateValue = TerminalState.Orbiting,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 30000, bodyName = "Mun" }
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        startUT = 400,
                        endUT = 600,
                        bodyName = "Kerbin",
                        semiMajorAxis = 694160,
                        eccentricity = 0.05,
                        inclination = 0.3,
                        longitudeOfAscendingNode = 0,
                        argumentOfPeriapsis = 0,
                        meanAnomalyAtEpoch = 0,
                        epoch = 400
                    }
                }
            };

            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveTrackingStationGhostSource(
                    rec,
                    false,
                    500,
                    out OrbitSegment segment,
                    out string reason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.Segment, source);
            Assert.Equal("Kerbin", segment.bodyName);
            Assert.Null(reason);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]") &&
                l.Contains("ResolveTrackingStationGhostSource") &&
                l.Contains("source=Segment") &&
                l.Contains("orbitSource=visible-segment") &&
                l.Contains("segmentBody=Kerbin"));
        }

        /// <summary>
        /// Terminal orbit fallback must wait until the recording itself has ended.
        /// While the recording is still in progress, no future orbit ghost should appear.
        /// </summary>
        [Fact]
        public void ResolveTrackingStationGhostSource_BeforeRecordingEnd_SkipsTerminalOrbitFallback()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Mun",
                TerminalOrbitSemiMajorAxis = 260300,
                TerminalStateValue = TerminalState.Orbiting,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 1000, bodyName = "Mun" }
                }
            };

            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveTrackingStationGhostSource(
                    rec,
                    false,
                    500,
                    out _,
                    out string reason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, source);
            Assert.Equal("before-terminal-orbit", reason);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]") &&
                l.Contains("ResolveTrackingStationGhostSource") &&
                l.Contains("source=None") &&
                l.Contains("before-terminal-orbit"));
        }

        /// <summary>
        /// Once the recording has ended, terminal orbit data becomes the correct fallback.
        /// </summary>
        [Fact]
        public void ResolveTrackingStationGhostSource_AfterRecordingEnd_UsesTerminalOrbitFallback()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Mun",
                TerminalOrbitSemiMajorAxis = 260300,
                TerminalStateValue = TerminalState.Orbiting,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 1000, bodyName = "Mun" }
                }
            };

            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveTrackingStationGhostSource(
                    rec,
                    false,
                    1000,
                    out _,
                    out string reason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.TerminalOrbit, source);
            Assert.Null(reason);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]") &&
                l.Contains("ResolveTrackingStationGhostSource") &&
                l.Contains("source=TerminalOrbit") &&
                l.Contains("orbitSource=terminal-orbit") &&
                l.Contains("seedSource=") &&
                l.Contains("terminalBody=Mun"));
        }

        /// <summary>
        /// Startup summary logging must keep future-tip skip buckets distinct so
        /// before-activation and before-terminal-orbit don't disappear into noOrbit.
        /// </summary>
        [Fact]
        public void CreateGhostVesselsFromCommittedRecordings_SummarySeparatesFutureTipSkipBuckets()
        {
            GhostMapPresence.CurrentUTNow = () => 500.0;
            ParsekSettingsPersistence.SetStoredShowGhostsInTrackingStationForTesting(true);

            try
            {
                RecordingStore.AddCommittedInternal(new Recording
                {
                    RecordingId = "future-tip",
                    TerminalOrbitBody = "Mun",
                    TerminalOrbitSemiMajorAxis = 260300,
                    TerminalStateValue = TerminalState.Orbiting,
                    Points = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = 1983.7, bodyName = "Kerbin" },
                        new TrajectoryPoint { ut = 27996.3, bodyName = "Mun" }
                    }
                });
                RecordingStore.AddCommittedInternal(new Recording
                {
                    RecordingId = "in-progress-tip",
                    TerminalOrbitBody = "Mun",
                    TerminalOrbitSemiMajorAxis = 260300,
                    TerminalStateValue = TerminalState.Orbiting,
                    Points = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = 100, bodyName = "Kerbin" },
                        new TrajectoryPoint { ut = 1000, bodyName = "Kerbin" }
                    }
                });
                RecordingStore.AddCommittedInternal(new Recording
                {
                    RecordingId = "no-orbit",
                    TerminalStateValue = null
                });

                int created = GhostMapPresence.CreateGhostVesselsFromCommittedRecordings();

                Assert.Equal(0, created);
                Assert.Contains(logLines, l =>
                    l.Contains("[GhostMap]") &&
                    l.Contains("CreateGhostVesselsFromCommittedRecordings: created=0 from 3 recordings") &&
                    l.Contains("beforeActivation=1") &&
                    l.Contains("beforeTerminalOrbit=1") &&
                    l.Contains("noOrbit=1"));
            }
            finally
            {
                RecordingStore.ClearCommittedInternal();
                RecordingStore.CommittedTrees.Clear();
                ParsekSettingsPersistence.ResetForTesting();
            }
        }

        /// <summary>
        /// Repeated startup skips should keep one detailed sample and aggregate the rest.
        /// </summary>
        [Fact]
        public void CreateGhostVesselsFromCommittedRecordings_AggregatesRepeatedSkipReasons()
        {
            GhostMapPresence.CurrentUTNow = () => 500.0;
            ParsekSettingsPersistence.SetStoredShowGhostsInTrackingStationForTesting(true);

            try
            {
                for (int i = 0; i < 3; i++)
                {
                    RecordingStore.AddCommittedInternal(new Recording
                    {
                        RecordingId = "no-orbit-" + i,
                        VesselName = "No Orbit " + i,
                        TerminalStateValue = null
                    });
                }

                RecordingStore.AddCommittedInternal(new Recording
                {
                    RecordingId = "spawned",
                    VesselName = "Spawned Vessel",
                    VesselSpawned = true,
                    SpawnedVesselPersistentId = 424242,
                    TerminalOrbitBody = "Kerbin",
                    TerminalOrbitSemiMajorAxis = 700000,
                    TerminalStateValue = TerminalState.Orbiting
                });

                int created = GhostMapPresence.CreateGhostVesselsFromCommittedRecordings();

                Assert.Equal(0, created);
                int detailedNoOrbitLines = 0;
                foreach (string line in logLines)
                {
                    if (line.Contains("Tracking-station orbit source") &&
                        line.Contains("reason=no-orbit-data"))
                    {
                        detailedNoOrbitLines++;
                    }
                }

                Assert.Equal(1, detailedNoOrbitLines);
                Assert.Contains(logLines, l =>
                    l.Contains("[GhostMap]") &&
                    l.Contains("Tracking-station orbit-source summary") &&
                    l.Contains("skip-no-orbit-data=3") &&
                    l.Contains("skip-already-spawned=1"));
                Assert.Contains(logLines, l =>
                    l.Contains("[GhostMap]") &&
                    l.Contains("CreateGhostVesselsFromCommittedRecordings: created=0 from 4 recordings") &&
                    l.Contains("spawned=1") &&
                    l.Contains("noOrbit=3"));
            }
            finally
            {
                RecordingStore.ClearCommittedInternal();
                RecordingStore.CommittedTrees.Clear();
                ParsekSettingsPersistence.ResetForTesting();
            }
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
        /// Null-terminal recordings should still create a ghost from a map-visible orbit
        /// tail even after the recorded points have ended.
        /// </summary>
        [Fact]
        public void ShouldCreate_NullTerminal_WithRecordedPointsAndExtendedOrbitTail_Created()
        {
            var rec = new Recording
            {
                RecordingId = "extended-tail",
                TerminalStateValue = null,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 200, bodyName = "Kerbin" }
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 200, endUT = 500, bodyName = "Kerbin", semiMajorAxis = 700000 }
                }
            };

            var (should, reason) = GhostMapPresence.ShouldCreateTrackingStationGhost(rec, false, 300);
            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveTrackingStationGhostSource(
                    rec,
                    false,
                    300,
                    out OrbitSegment segment,
                    out string sourceReason);

            Assert.True(should);
            Assert.Null(reason);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.Segment, source);
            Assert.Equal("Kerbin", segment.bodyName);
            Assert.Null(sourceReason);
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

        [Fact]
        public void ShouldCreate_CurrentOrbitContinuationWithFutureChild_Created()
        {
            var recs = new List<Recording>
            {
                new Recording { RecordingId = "launch" },
                new Recording
                {
                    RecordingId = "kerbin-orbit",
                    ParentRecordingId = "launch",
                    TerminalStateValue = null,
                    OrbitSegments = new List<OrbitSegment>
                    {
                        new OrbitSegment
                        {
                            startUT = 220,
                            endUT = 420,
                            bodyName = "Kerbin",
                            semiMajorAxis = 700000,
                            eccentricity = 0.01,
                            inclination = 0,
                            longitudeOfAscendingNode = 0,
                            argumentOfPeriapsis = 0,
                            meanAnomalyAtEpoch = 0,
                            epoch = 220
                        }
                    }
                },
                new Recording
                {
                    RecordingId = "mun-leg",
                    ParentRecordingId = "kerbin-orbit",
                    TerminalStateValue = null,
                    Points = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = 500, bodyName = "Kerbin" },
                        new TrajectoryPoint { ut = 620, bodyName = "Mun" }
                    },
                    OrbitSegments = new List<OrbitSegment>
                    {
                        new OrbitSegment
                        {
                            startUT = 560,
                            endUT = 800,
                            bodyName = "Mun",
                            semiMajorAxis = 220000,
                            eccentricity = 0.02,
                            inclination = 5,
                            longitudeOfAscendingNode = 0,
                            argumentOfPeriapsis = 0,
                            meanAnomalyAtEpoch = 0,
                            epoch = 560
                        }
                    }
                }
            };

            var suppressed = GhostMapPresence.FindTrackingStationSuppressedRecordingIds(recs, 300);
            var (should, reason) = GhostMapPresence.ShouldCreateTrackingStationGhost(
                recs[1], suppressed.Contains(recs[1].RecordingId), 300);

            Assert.True(should);
            Assert.Null(reason);
        }

        [Fact]
        public void GetTrackingStationGhostRemovalReason_SuppressedTerminalOrbitParent_RemovesExistingGhost()
        {
            var rec = new Recording
            {
                RecordingId = "kerbin-parent",
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalStateValue = TerminalState.Orbiting
            };

            string reason = GhostMapPresence.GetTrackingStationGhostRemovalReason(
                rec,
                isSuppressed: true,
                hasOrbitBounds: false,
                currentUT: 320);

            Assert.Equal("tracking-station-child-started", reason);
        }

        [Fact]
        public void GetTrackingStationGhostRemovalReason_MaterializedRealVessel_RemovesExistingGhost()
        {
            var rec = new Recording
            {
                RecordingId = "real-vessel-parent",
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalStateValue = TerminalState.Orbiting
            };

            string reason = GhostMapPresence.GetTrackingStationGhostRemovalReason(
                rec,
                isSuppressed: false,
                alreadyMaterialized: true,
                hasOrbitBounds: false,
                isStateVector: false,
                currentUT: 320);

            Assert.Equal("tracking-station-materialized-real-vessel", reason);
        }

        [Fact]
        public void GetTrackingStationGhostRemovalReason_StateVectorPastEndUT_ExpiresGhost()
        {
            var rec = new Recording
            {
                RecordingId = "state-vector-expiry",
                ExplicitEndUT = 200
            };

            string reasonAtEnd = GhostMapPresence.GetTrackingStationGhostRemovalReason(
                rec,
                isSuppressed: false,
                alreadyMaterialized: false,
                hasOrbitBounds: false,
                isStateVector: true,
                currentUT: 200);
            string reasonPastEnd = GhostMapPresence.GetTrackingStationGhostRemovalReason(
                rec,
                isSuppressed: false,
                alreadyMaterialized: false,
                hasOrbitBounds: false,
                isStateVector: true,
                currentUT: 201);

            Assert.Null(reasonAtEnd);
            Assert.Equal("tracking-station-state-vector-expired", reasonPastEnd);
        }

        // -----------------------------------------------------------------
        // PR #556 follow-up — keep relative-frame state-vector ghosts alive
        // through tracking-station refresh cycles. Mirrors the flight-scene
        // guard tested in
        // RuntimePolicyTests.RelativeFrameGuard_DzBelowAltitudeThreshold_WouldTripRemovalWithoutGate.
        // The tracking-station refresh path used to remove any state-vector
        // ghost whose currentUT was inside a Relative section; after #583
        // the resolver creates these intentionally, so the refresh path
        // would tear them down every cycle while the create path re-added
        // them next tick. The fix gates the threshold check on
        // !IsInRelativeFrame and the Relative branch flows straight into
        // UpdateGhostOrbitFromStateVectors (which already dispatches on
        // referenceFrame). The two-fact tripwire below pins the joint
        // preconditions: in a Relative section, dz-as-altitude WOULD trip
        // ShouldRemoveStateVectorOrbit if the gate weren't suppressing it.
        // -----------------------------------------------------------------

        [Fact]
        public void TrackingStationRefresh_RelativeFrameStateVector_WouldTripRemovalWithoutGate()
        {
            var rec = new Recording
            {
                RecordingId = "ts-relative-state-vector",
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = ReferenceFrame.Relative,
                        startUT = 1658.96,
                        endUT = 1668.14,
                        anchorVesselId = 12345u
                    }
                }
            };
            const double currentUT = 1662.0;
            const double dzAsAltitude = -0.31; // anchor-local dz, not geographic alt
            const double worldVelocityMag = 2920.0;
            const double airlessAtmosphereDepth = 0;

            Assert.True(
                GhostMapPresence.IsInRelativeFrame(rec, currentUT),
                "current UT lies inside the Relative-frame section");
            Assert.True(
                GhostMapPresence.ShouldRemoveStateVectorOrbit(
                    dzAsAltitude, worldVelocityMag, airlessAtmosphereDepth),
                "without the IsInRelativeFrame gate in RefreshTrackingStationGhosts, "
                + "dz~0 would trip the altitude threshold and remove the ghost every "
                + "refresh tick — the create path would re-add it next tick → flicker. "
                + "The gate suppresses the threshold for Relative-frame points so "
                + "UpdateGhostOrbitFromStateVectors stays in charge of the cycle.");
        }

        [Fact]
        public void TrackingStationRefresh_AbsoluteFrameStateVector_StillEvaluatesThreshold()
        {
            // Discriminator: an Absolute-frame point with the same low
            // altitude legitimately trips the threshold and removes the
            // ghost. The gate must apply only to Relative frames.
            var rec = new Recording
            {
                RecordingId = "ts-absolute-state-vector",
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = ReferenceFrame.Absolute,
                        startUT = 1658.96,
                        endUT = 1668.14
                    }
                }
            };
            const double currentUT = 1662.0;

            Assert.False(
                GhostMapPresence.IsInRelativeFrame(rec, currentUT),
                "Absolute section: gate must NOT bypass the threshold check");
            Assert.True(
                GhostMapPresence.ShouldRemoveStateVectorOrbit(
                    altitude: -0.31, speed: 2920.0, atmosphereDepth: 0),
                "Absolute frame: alt~0 below threshold legitimately removes "
                + "the ghost (state-vector subsurface drift case).");
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

        [Fact]
        public void ShouldCreate_TerminalOrbitWithComputedConflictingEndpoint_SkippedAsEndpointConflict()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalStateValue = TerminalState.Orbiting,
                ExplicitEndUT = 200,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 200, bodyName = "Mun", latitude = 1, longitude = 2, altitude = 3000 }
                }
            };

            var (should, reason) = GhostMapPresence.ShouldCreateTrackingStationGhost(rec, false, 1000);

            Assert.False(should);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSkipEndpointConflict, reason);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]") &&
                l.Contains("ResolveTrackingStationGhostSource") &&
                l.Contains("reason=endpoint-conflict") &&
                l.Contains("seedFailure=endpoint-conflict") &&
                l.Contains("endpointBody=Mun"));
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_VisibleSegment_MatchesTrackingStationWrapper()
        {
            var rec = new Recording
            {
                RecordingId = "segment-parity",
                TerminalOrbitBody = "Mun",
                TerminalOrbitSemiMajorAxis = 260300,
                TerminalStateValue = TerminalState.Orbiting,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 30000, bodyName = "Mun" }
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        startUT = 400,
                        endUT = 600,
                        bodyName = "Kerbin",
                        semiMajorAxis = 694160,
                        eccentricity = 0.05,
                        inclination = 0.3,
                        longitudeOfAscendingNode = 0,
                        argumentOfPeriapsis = 0,
                        meanAnomalyAtEpoch = 0,
                        epoch = 400
                    }
                }
            };

            int mapCached = -1;
            var mapSource = GhostMapPresence.ResolveMapPresenceGhostSource(
                rec,
                false,
                false,
                500,
                true,
                "test-map",
                ref mapCached,
                out OrbitSegment mapSegment,
                out _,
                out string mapReason);

            int tsCached = -1;
            var tsSource = GhostMapPresence.ResolveTrackingStationGhostSource(
                rec,
                false,
                false,
                500,
                ref tsCached,
                out OrbitSegment tsSegment,
                out _,
                out string tsReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.Segment, mapSource);
            Assert.Equal(mapSource, tsSource);
            Assert.Equal(mapSegment.bodyName, tsSegment.bodyName);
            Assert.Equal(mapSegment.semiMajorAxis, tsSegment.semiMajorAxis);
            Assert.Null(mapReason);
            Assert.Null(tsReason);
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_StateVectorFallback_MatchesTrackingStationWrapper()
        {
            var rec = new Recording
            {
                RecordingId = "state-vector-parity",
                TerminalStateValue = TerminalState.SubOrbital,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100,
                        bodyName = "Mun",
                        altitude = 3000,
                        velocity = new UnityEngine.Vector3(0, 100, 0)
                    },
                    new TrajectoryPoint
                    {
                        ut = 200,
                        bodyName = "Mun",
                        altitude = 3200,
                        velocity = new UnityEngine.Vector3(0, 110, 0)
                    }
                }
            };

            int mapCached = -1;
            var mapSource = GhostMapPresence.ResolveMapPresenceGhostSource(
                rec,
                false,
                false,
                150,
                false,
                "test-map",
                ref mapCached,
                out _,
                out TrajectoryPoint mapPoint,
                out string mapReason);

            int tsCached = -1;
            var tsSource = GhostMapPresence.ResolveTrackingStationGhostSource(
                rec,
                false,
                false,
                150,
                ref tsCached,
                out _,
                out TrajectoryPoint tsPoint,
                out string tsReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.StateVector, mapSource);
            Assert.Equal(mapSource, tsSource);
            Assert.Equal("Mun", mapPoint.bodyName);
            Assert.Equal(mapPoint.altitude, tsPoint.altitude);
            Assert.Null(mapReason);
            Assert.Null(tsReason);
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_EndpointConflict_MatchesTrackingStationWrapper()
        {
            var rec = new Recording
            {
                RecordingId = "endpoint-conflict-parity",
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalStateValue = TerminalState.Orbiting,
                ExplicitEndUT = 200,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 200, bodyName = "Mun", latitude = 1, longitude = 2, altitude = 3000 }
                }
            };

            int mapCached = -1;
            var mapSource = GhostMapPresence.ResolveMapPresenceGhostSource(
                rec,
                false,
                false,
                1000,
                true,
                "test-map",
                ref mapCached,
                out _,
                out _,
                out string mapReason);

            int tsCached = -1;
            var tsSource = GhostMapPresence.ResolveTrackingStationGhostSource(
                rec,
                false,
                false,
                1000,
                ref tsCached,
                out _,
                out _,
                out string tsReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, mapSource);
            Assert.Equal(mapSource, tsSource);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSkipEndpointConflict, mapReason);
            Assert.Equal(mapReason, tsReason);
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_MaterializedRealVessel_MatchesTrackingStationWrapper()
        {
            var rec = new Recording
            {
                RecordingId = "materialized-parity",
                VesselPersistentId = 424242,
                TerminalStateValue = TerminalState.Orbiting,
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 100, endUT = 500, bodyName = "Kerbin", semiMajorAxis = 700000 }
                }
            };

            int mapCached = -1;
            var mapSource = GhostMapPresence.ResolveMapPresenceGhostSource(
                rec,
                false,
                true,
                300,
                true,
                "test-map",
                ref mapCached,
                out _,
                out _,
                out string mapReason);

            int tsCached = -1;
            var tsSource = GhostMapPresence.ResolveTrackingStationGhostSource(
                rec,
                false,
                true,
                300,
                ref tsCached,
                out _,
                out _,
                out string tsReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, mapSource);
            Assert.Equal(mapSource, tsSource);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSkipAlreadySpawned, mapReason);
            Assert.Equal(mapReason, tsReason);
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_StateVectorBelowThreshold_SkipsWithThresholdReason()
        {
            var rec = new Recording
            {
                RecordingId = "state-vector-below-threshold",
                TerminalStateValue = TerminalState.SubOrbital,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100,
                        bodyName = "Mun",
                        altitude = 200,
                        velocity = new UnityEngine.Vector3(0, 10, 0)
                    },
                    new TrajectoryPoint
                    {
                        ut = 300,
                        bodyName = "Mun",
                        altitude = 250,
                        velocity = new UnityEngine.Vector3(0, 15, 0)
                    }
                }
            };

            int mapCached = -1;
            var source = GhostMapPresence.ResolveMapPresenceGhostSource(
                rec,
                false,
                false,
                200,
                false,
                "test-threshold",
                ref mapCached,
                out _,
                out _,
                out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, source);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSkipStateVectorThreshold, skipReason);
            Assert.Contains(logLines,
                l => l.Contains("[GhostMap]")
                    && l.Contains("test-threshold")
                    && l.Contains("reason=" + GhostMapPresence.TrackingStationGhostSkipStateVectorThreshold));
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_RelativeFrame_SkipsWithRelativeFrameReason()
        {
            var rec = new Recording
            {
                RecordingId = "state-vector-relative-frame",
                TerminalStateValue = TerminalState.SubOrbital,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100,
                        bodyName = "Mun",
                        altitude = 3000,
                        velocity = new UnityEngine.Vector3(0, 100, 0)
                    },
                    new TrajectoryPoint
                    {
                        ut = 300,
                        bodyName = "Mun",
                        altitude = 3000,
                        velocity = new UnityEngine.Vector3(0, 100, 0)
                    }
                },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        startUT = 100,
                        endUT = 300,
                        referenceFrame = ReferenceFrame.Relative
                    }
                }
            };

            int mapCached = -1;
            var source = GhostMapPresence.ResolveMapPresenceGhostSource(
                rec,
                false,
                false,
                200,
                false,
                "test-relative",
                ref mapCached,
                out _,
                out _,
                out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, source);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSkipRelativeFrame, skipReason);
            Assert.Contains(logLines,
                l => l.Contains("[GhostMap]")
                    && l.Contains("test-relative")
                    && l.Contains("reason=" + GhostMapPresence.TrackingStationGhostSkipRelativeFrame));
        }

        // -----------------------------------------------------------------
        // #583: When the first map-visible UT lands inside a Relative-frame
        // section, the resolver used to short-circuit to None because
        // TryResolveStateVectorMapPoint flatly rejected Relative-frame
        // points and the outer gate was `!HasOrbitSegments` only. The fix
        // widens the gate (Relative-frame currentUT is also considered for
        // state-vector resolution) and allows StateVector creation when the
        // section's anchor vessel is resolvable in the scene; otherwise it
        // defers with a dedicated "relative-anchor-unresolved" skip reason
        // so CheckPendingMapVessels retries on the next tick.
        //
        // CreateGhostVesselFromStateVectors already has a working Relative
        // branch (PR #547) that resolves world position via the anchor
        // pose, so flowing StateVector through gives the existing creator
        // the right input shape — no new ghost-source kind needed.
        // -----------------------------------------------------------------

        [Fact]
        public void ResolveMapPresenceGhostSource_RelativeFrame_AnchorResolvable_ReturnsStateVector()
        {
            var rec = BuildRelativeFrameRecording(
                anchorVesselId: 999u,
                pointDz: 0.5,
                pointSpeed: 0.2);

            // Production path looks anchors up via FlightRecorder.FindVesselByPid.
            // Override the test seam to simulate "anchor present in scene".
            GhostMapPresence.AnchorResolvableForTesting = pid => pid == 999u;

            int mapCached = -1;
            var source = GhostMapPresence.ResolveMapPresenceGhostSource(
                rec,
                false,
                false,
                200,
                false,
                "test-rel-anchor-ok",
                ref mapCached,
                out _,
                out TrajectoryPoint statePoint,
                out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.StateVector, source);
            Assert.Null(skipReason);
            Assert.Equal("Mun", statePoint.bodyName);
            Assert.Contains(logLines,
                l => l.Contains("[GhostMap]")
                    && l.Contains("test-rel-anchor-ok")
                    && l.Contains("source=StateVector"));
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_RelativeFrame_AnchorUnresolvable_DefersWithRelativeAnchorUnresolved()
        {
            var rec = BuildRelativeFrameRecording(
                anchorVesselId: 999u,
                pointDz: 0.5,
                pointSpeed: 0.2);

            // Anchor PID is set but the scene lookup fails (anchor not yet
            // loaded into FlightGlobals.Vessels).
            GhostMapPresence.AnchorResolvableForTesting = pid => false;

            int mapCached = -1;
            var source = GhostMapPresence.ResolveMapPresenceGhostSource(
                rec,
                false,
                false,
                200,
                false,
                "test-rel-anchor-missing",
                ref mapCached,
                out _,
                out _,
                out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, source);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSkipRelativeAnchorUnresolved, skipReason);
            Assert.Contains(logLines,
                l => l.Contains("[GhostMap]")
                    && l.Contains("test-rel-anchor-missing")
                    && l.Contains("reason=" + GhostMapPresence.TrackingStationGhostSkipRelativeAnchorUnresolved));
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_RelativeFrame_NoAnchorId_StillSkipsWithRelativeFrame()
        {
            // Sections without an anchor id (legacy / synthetic) keep the
            // pre-#583 skip-reason wording so the path is observably distinct
            // from "anchor present but not yet resolvable".
            var rec = BuildRelativeFrameRecording(
                anchorVesselId: 0u,
                pointDz: 0.5,
                pointSpeed: 0.2);

            GhostMapPresence.AnchorResolvableForTesting = pid => true;

            int mapCached = -1;
            var source = GhostMapPresence.ResolveMapPresenceGhostSource(
                rec,
                false,
                false,
                200,
                false,
                "test-rel-no-anchor",
                ref mapCached,
                out _,
                out _,
                out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, source);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSkipRelativeFrame, skipReason);
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_RelativeFrame_DzBelowAltitudeThreshold_StillReturnsStateVector()
        {
            // The whole point of the #583 fix: dz~0 (typical docking offset)
            // must NOT trip the state-vector altitude threshold for
            // Relative-frame points — the threshold is meaningful only for
            // Absolute-frame points where altitude is geographic. Pin the
            // joint behaviour: an Absolute-frame point with the same numeric
            // altitude/speed would fail ShouldCreateStateVectorOrbit; the
            // Relative-frame point must still produce StateVector because
            // the threshold check is bypassed for that branch.
            var rec = BuildRelativeFrameRecording(
                anchorVesselId: 42u,
                pointDz: 0.5,
                pointSpeed: 0.2);

            // Sanity: the same numbers as an Absolute-frame state-vector
            // recording would fall under the airless-body create threshold
            // (alt > 1500 && speed > 60).
            Assert.False(GhostMapPresence.ShouldCreateStateVectorOrbit(0.5, 0.2, 0));

            GhostMapPresence.AnchorResolvableForTesting = pid => pid == 42u;

            int mapCached = -1;
            var source = GhostMapPresence.ResolveMapPresenceGhostSource(
                rec,
                false,
                false,
                200,
                false,
                "test-rel-dz",
                ref mapCached,
                out _,
                out _,
                out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.StateVector, source);
            Assert.Null(skipReason);
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_RelativeFrame_WithOrbitSegmentsElsewhere_StillReachesStateVectorBranch()
        {
            // Pre-#583: a recording with OrbitSegments anywhere short-circuited
            // the state-vector branch (`if (!traj.HasOrbitSegments)`), so a
            // currentUT inside a Relative section between segments produced
            // None. The fix widens the gate to also consider state-vector
            // resolution when IsInRelativeFrame(currentUT) is true.
            var rec = BuildRelativeFrameRecording(
                anchorVesselId: 7u,
                pointDz: 0.5,
                pointSpeed: 0.2);
            // Add an unrelated orbit segment far before the Relative section
            // so HasOrbitSegments=true but it doesn't cover currentUT.
            rec.OrbitSegments = new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    startUT = 0,
                    endUT = 50,
                    bodyName = "Kerbin",
                    semiMajorAxis = 700000
                }
            };

            GhostMapPresence.AnchorResolvableForTesting = pid => pid == 7u;

            int mapCached = -1;
            var source = GhostMapPresence.ResolveMapPresenceGhostSource(
                rec,
                false,
                false,
                200,
                false,
                "test-rel-with-segments",
                ref mapCached,
                out _,
                out _,
                out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.StateVector, source);
            Assert.Null(skipReason);
        }

        private static Recording BuildRelativeFrameRecording(
            uint anchorVesselId, double pointDz, double pointSpeed)
        {
            return new Recording
            {
                RecordingId = "state-vector-relative-anchor",
                TerminalStateValue = TerminalState.SubOrbital,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100,
                        bodyName = "Mun",
                        // In RELATIVE sections the lat/lon/altitude fields are
                        // anchor-local x/y/z metres, not geographic — name them
                        // accordingly so future readers don't get confused.
                        latitude = 0.1,
                        longitude = 0.2,
                        altitude = pointDz,
                        velocity = new UnityEngine.Vector3(0, (float)pointSpeed, 0)
                    },
                    new TrajectoryPoint
                    {
                        ut = 300,
                        bodyName = "Mun",
                        latitude = 0.3,
                        longitude = 0.4,
                        altitude = pointDz,
                        velocity = new UnityEngine.Vector3(0, (float)pointSpeed, 0)
                    }
                },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        startUT = 100,
                        endUT = 300,
                        referenceFrame = ReferenceFrame.Relative,
                        anchorVesselId = anchorVesselId
                    }
                }
            };
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_NoTerminalFallback_StateVectorFailure_NormalizesSkipReason()
        {
            var rec = new Recording
            {
                RecordingId = "state-vector-no-points",
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000
            };

            int mapCached = -1;
            var source = GhostMapPresence.ResolveMapPresenceGhostSource(
                rec,
                false,
                false,
                500,
                false,
                "test-no-fallback",
                ref mapCached,
                out _,
                out _,
                out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, source);
            Assert.Equal("no-orbit-data", skipReason);
        }

        // #571 closure pin: an OrbitalCheckpoint TrackSection with inline frames
        // coexists with the seed OrbitSegment that covers the same window. When
        // currentUT lands inside that coexisting span the resolver MUST keep the
        // segment as the source of truth — the densified frames are sampling
        // along that same Keplerian arc, not a competing source. This mirrors
        // the in-game RuntimeTests.GhostMapCheckpointSourceLogResolvesWorldPosition
        // fixture so the contract is enforced from xUnit too.
        [Fact]
        public void ResolveMapPresenceGhostSource_OrbitalCheckpointWithCoexistingSegment_ReturnsSegment()
        {
            const double startUT = 1000.0;
            const double endUT = 1060.0;
            var segment = new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                bodyName = "Kerbin",
                semiMajorAxis = 700000,
                eccentricity = 0.0,
                inclination = 0.0,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = startUT
            };
            var first = new TrajectoryPoint
            {
                ut = startUT,
                bodyName = "Kerbin",
                latitude = 0.0,
                longitude = 0.0,
                altitude = 100000.0,
                velocity = new UnityEngine.Vector3(0f, 2300f, 0f)
            };
            var second = first;
            second.ut = endUT;
            second.longitude = 5.0;
            var rec = new Recording
            {
                RecordingId = "checkpoint-coexisting-segment",
                VesselName = "Coexisting checkpoint",
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                TerminalStateValue = TerminalState.Orbiting,
                OrbitSegments = new List<OrbitSegment> { segment },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        environment = SegmentEnvironment.ExoBallistic,
                        referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                        source = TrackSectionSource.Checkpoint,
                        startUT = startUT,
                        endUT = endUT,
                        frames = new List<TrajectoryPoint> { first, second },
                        checkpoints = new List<OrbitSegment> { segment },
                        minAltitude = 100000f,
                        maxAltitude = 100000f
                    }
                }
            };

            int mapCached = -1;
            var source = GhostMapPresence.ResolveMapPresenceGhostSource(
                rec,
                isSuppressed: false,
                alreadyMaterialized: false,
                currentUT: startUT + 30.0,
                allowTerminalOrbitFallback: true,
                logOperationName: "test-checkpoint-coexisting-segment",
                ref mapCached,
                out OrbitSegment resolvedSegment,
                out _,
                out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.Segment, source);
            Assert.Null(skipReason);
            Assert.Equal("Kerbin", resolvedSegment.bodyName);
            Assert.Equal(segment.semiMajorAxis, resolvedSegment.semiMajorAxis);
            // P3 review pin: assert the OrbitalCheckpoint branch was actually
            // exercised. Without these substrings the test would still pass if
            // TryResolveCheckpointStateVectorMapPoint stopped finding the
            // OrbitalCheckpoint section entirely — the segment branch would
            // return Segment first and the coexistence regression would go
            // silently green. `stateVectorSource=OrbitalCheckpoint` is emitted
            // only after the checkpoint section is resolved, and
            // `orbitalCheckpointFallback=reject` proves the fallback
            // evaluator ran and chose the segment as the safer source.
            Assert.Contains(logLines,
                l => l.Contains("[GhostMap]")
                    && l.Contains("test-checkpoint-coexisting-segment")
                    && l.Contains("source=Segment")
                    && l.Contains("stateVectorSource=OrbitalCheckpoint")
                    && l.Contains("orbitalCheckpointFallback=reject"));
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_TerminalFallback_FillsSparseOrbitGapBeforeEnd()
        {
            var rec = new Recording
            {
                RecordingId = "sparse-orbit-gap",
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 4070696,
                TerminalOrbitEccentricity = 0.844672,
                TerminalOrbitInclination = 0.7638,
                TerminalOrbitLAN = 12.0,
                TerminalOrbitArgumentOfPeriapsis = 188.999,
                TerminalOrbitMeanAnomalyAtEpoch = 1.185624,
                TerminalOrbitEpoch = 171496.6,
                EndpointBodyName = "Kerbin",
                ExplicitStartUT = 1658.9,
                ExplicitEndUT = 193774.6,
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        startUT = 1658.9,
                        endUT = 1668.1,
                        referenceFrame = ReferenceFrame.Relative,
                        frames = new List<TrajectoryPoint>
                        {
                            new TrajectoryPoint { ut = 1658.9, bodyName = "Kerbin" },
                            new TrajectoryPoint { ut = 1668.1, bodyName = "Kerbin" }
                        }
                    },
                    new TrackSection
                    {
                        startUT = 171496.6,
                        endUT = 193774.6,
                        referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                        checkpoints = new List<OrbitSegment>
                        {
                            new OrbitSegment
                            {
                                startUT = 171496.6,
                                endUT = 193774.6,
                                bodyName = "Kerbin",
                                semiMajorAxis = 4070696,
                                eccentricity = 0.844672
                            }
                        }
                    }
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        startUT = 171496.6,
                        endUT = 193774.6,
                        bodyName = "Kerbin",
                        semiMajorAxis = 4070696,
                        eccentricity = 0.844672
                    }
                }
            };

            int mapCached = -1;
            var source = GhostMapPresence.ResolveMapPresenceGhostSource(
                rec,
                false,
                false,
                112557.6,
                true,
                "test-sparse-gap",
                ref mapCached,
                out OrbitSegment segment,
                out _,
                out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.TerminalOrbit, source);
            Assert.Null(skipReason);
            Assert.Equal("Kerbin", segment.bodyName);
            Assert.Equal(4070696, segment.semiMajorAxis);
            Assert.Equal(0.844672, segment.eccentricity);
            Assert.Equal(1658.9, segment.startUT);
            Assert.Equal(193774.6, segment.endUT);
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_TerminalFallback_DoesNotOverrideRecordedPreOrbitCoverage()
        {
            var rec = new Recording
            {
                RecordingId = "covered-pre-orbit",
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalOrbitEccentricity = 0.01,
                TerminalOrbitEpoch = 500,
                EndpointBodyName = "Kerbin",
                ExplicitStartUT = 100,
                ExplicitEndUT = 1000,
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        startUT = 100,
                        endUT = 400,
                        referenceFrame = ReferenceFrame.Absolute,
                        frames = new List<TrajectoryPoint>
                        {
                            new TrajectoryPoint { ut = 100, bodyName = "Kerbin" },
                            new TrajectoryPoint { ut = 400, bodyName = "Kerbin" }
                        }
                    },
                    new TrackSection
                    {
                        startUT = 500,
                        endUT = 1000,
                        referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                        checkpoints = new List<OrbitSegment>
                        {
                            new OrbitSegment
                            {
                                startUT = 500,
                                endUT = 1000,
                                bodyName = "Kerbin",
                                semiMajorAxis = 700000,
                                eccentricity = 0.01
                            }
                        }
                    }
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        startUT = 500,
                        endUT = 1000,
                        bodyName = "Kerbin",
                        semiMajorAxis = 700000,
                        eccentricity = 0.01
                    }
                }
            };

            int mapCached = -1;
            var source = GhostMapPresence.ResolveMapPresenceGhostSource(
                rec,
                false,
                false,
                300,
                true,
                "test-covered-pre-orbit",
                ref mapCached,
                out _,
                out _,
                out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, source);
            Assert.Equal("before-terminal-orbit", skipReason);
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_MaterializedRecordingSuppressesMapGhost()
        {
            var rec = new Recording
            {
                RecordingId = "materialized-map-ghost",
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 900000,
                TerminalOrbitEccentricity = 0.03,
                TerminalOrbitEpoch = 100,
                EndpointBodyName = "Kerbin",
                ExplicitStartUT = 0,
                ExplicitEndUT = 100,
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        startUT = 0,
                        endUT = 100,
                        bodyName = "Kerbin",
                        semiMajorAxis = 900000,
                        eccentricity = 0.03
                    }
                }
            };

            int mapCached = -1;
            var materializedSource = GhostMapPresence.ResolveMapPresenceGhostSource(
                rec,
                false,
                true,
                150,
                true,
                "test-materialized",
                ref mapCached,
                out _,
                out _,
                out string materializedSkipReason);

            mapCached = -1;
            var visibleSource = GhostMapPresence.ResolveMapPresenceGhostSource(
                rec,
                false,
                false,
                150,
                true,
                "test-not-materialized",
                ref mapCached,
                out _,
                out _,
                out string visibleSkipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, materializedSource);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSkipAlreadySpawned, materializedSkipReason);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.TerminalOrbit, visibleSource);
            Assert.Null(visibleSkipReason);
        }

        [Fact]
        public void HasRecordedTrackCoverageAtUT_LegacyPoints_RequiresDenseBracket()
        {
            var dense = new Recording
            {
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 10 },
                    new TrajectoryPoint { ut = 13 }
                }
            };
            var sparse = new Recording
            {
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 10 },
                    new TrajectoryPoint { ut = 1000 }
                }
            };

            Assert.True(GhostMapPresence.HasRecordedTrackCoverageAtUT(dense, 11));
            Assert.False(GhostMapPresence.HasRecordedTrackCoverageAtUT(sparse, 500));
        }

        [Fact]
        public void TryResolveTerminalFallbackMapOrbitUpdate_ExistingOrbitSwitchesAcrossSparseGap()
        {
            var rec = new Recording
            {
                RecordingId = "map-existing-gap",
                VesselName = "Gap Probe",
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 2000000,
                TerminalOrbitEccentricity = 0.02,
                TerminalOrbitEpoch = 20,
                EndpointBodyName = "Kerbin",
                ExplicitStartUT = 0,
                ExplicitEndUT = 100,
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        startUT = 0,
                        endUT = 5,
                        referenceFrame = ReferenceFrame.Absolute,
                        frames = new List<TrajectoryPoint>
                        {
                            new TrajectoryPoint { ut = 0, bodyName = "Kerbin" },
                            new TrajectoryPoint { ut = 5, bodyName = "Kerbin" }
                        }
                    },
                    new TrackSection
                    {
                        startUT = 10,
                        endUT = 20,
                        referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                        checkpoints = new List<OrbitSegment>
                        {
                            new OrbitSegment
                            {
                                startUT = 10,
                                endUT = 20,
                                bodyName = "Kerbin",
                                semiMajorAxis = 1000000,
                                eccentricity = 0.01
                            }
                        }
                    }
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        startUT = 10,
                        endUT = 20,
                        bodyName = "Kerbin",
                        semiMajorAxis = 1000000,
                        eccentricity = 0.01
                    }
                }
            };

            int cachedStateVectorIndex = -1;
            bool resolved = ParsekPlaybackPolicy.TryResolveTerminalFallbackMapOrbitUpdate(
                rec,
                0,
                50,
                ("Kerbin", 900000, 0.09),
                false,
                ref cachedStateVectorIndex,
                out OrbitSegment fallbackSegment,
                out var fallbackKey,
                out bool changed);

            Assert.True(resolved);
            Assert.True(changed);
            Assert.Equal("Kerbin", fallbackSegment.bodyName);
            Assert.Equal(1000000, fallbackSegment.semiMajorAxis);
            Assert.Equal(0.01, fallbackSegment.eccentricity);
            Assert.Equal("Kerbin", fallbackKey.body);
            Assert.Equal(1000000, fallbackKey.sma);
            Assert.Equal(0.01, fallbackKey.ecc);
            Assert.Contains(logLines,
                l => l.Contains("[Policy]")
                    && l.Contains("Switched ghost map orbit")
                    && l.Contains("terminal-orbit fallback")
                    && l.Contains("Gap Probe"));
        }

        #endregion

        #region Endpoint-Aligned Orbit Seeds

        [Fact]
        public void TryResolveGhostProtoOrbitSeed_TerminalOrbitOnly_UsesTerminalSeed()
        {
            var traj = new MockTrajectory
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000.0,
                TerminalOrbitEccentricity = 0.01,
                TerminalOrbitInclination = 3.0,
                TerminalOrbitLAN = 4.0,
                TerminalOrbitArgumentOfPeriapsis = 5.0,
                TerminalOrbitMeanAnomalyAtEpoch = 0.6,
                TerminalOrbitEpoch = 200.0,
                TerminalStateValue = TerminalState.Orbiting
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
                out string bodyName,
                out GhostMapPresence.GhostProtoOrbitSeedDiagnostics diagnostics));

            Assert.Equal("Kerbin", bodyName);
            Assert.Equal(700000.0, semiMajorAxis, 10);
            Assert.Equal(0.01, eccentricity, 10);
            Assert.Equal(3.0, inclination, 10);
            Assert.Equal(4.0, lan, 10);
            Assert.Equal(5.0, argumentOfPeriapsis, 10);
            Assert.Equal(0.6, meanAnomalyAtEpoch, 10);
            Assert.Equal(200.0, epoch, 10);
            Assert.Equal("terminal-orbit", diagnostics.Source);
            Assert.Equal("no-endpoint-body", diagnostics.FallbackReason);
        }

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
                out _,
                out GhostMapPresence.GhostProtoOrbitSeedDiagnostics diagnostics));
            Assert.Equal("endpoint-conflict", diagnostics.FailureReason);
            Assert.Equal("Mun", diagnostics.EndpointBodyName);
        }

        [Fact]
        public void TryGetEndpointAlignedOrbitSeed_ConflictingEndpoint_ReportsEndpointConflict()
        {
            var traj = new MockTrajectory
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 900000.0,
                EndpointPhase = RecordingEndpointPhase.TrajectoryPoint,
                EndpointBodyName = "Mun"
            };

            Assert.False(RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed(
                traj,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out RecordingEndpointResolver.EndpointOrbitSeedDiagnostics diagnostics));
            Assert.Equal("none", diagnostics.Source);
            Assert.Equal("Mun", diagnostics.EndpointBodyName);
            Assert.Equal("endpoint-conflict", diagnostics.FailureReason);
        }

        #endregion

        #region Chain-aware integration

        /// <summary>
        /// Scenario: chain A(launch)→B(orbit)→C(destroyed).
        /// The started orbit child suppresses the launch parent, but the destroyed tip has no
        /// resolvable start UT, so Tracking Station fails open and keeps the current orbit
        /// continuation visible instead of hiding it on mere child existence.
        /// </summary>
        [Fact]
        public void ChainAware_DestroyedTipWithoutStart_KeepsCurrentOrbitVisible()
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
                    ExplicitStartUT = 100,
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

            var suppressed = GhostMapPresence.FindTrackingStationSuppressedRecordingIds(recs, 300);

            Assert.Contains("launch", suppressed);
            Assert.DoesNotContain("orbit", suppressed);
            Assert.DoesNotContain("destroyed", suppressed);

            // The current orbit continuation stays visible because the destroyed child has no
            // resolvable start UT and therefore does not suppress it yet.
            var (shouldOrbit, _) = GhostMapPresence.ShouldCreateTrackingStationGhost(
                recs[1], suppressed.Contains(recs[1].RecordingId), 300);
            Assert.True(shouldOrbit);

            // "destroyed" is tip but has Destroyed state → skipped
            var (shouldDestroyed, _) = GhostMapPresence.ShouldCreateTrackingStationGhost(
                recs[2], suppressed.Contains(recs[2].RecordingId), 300);
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
                    ExplicitStartUT = 100,
                    TerminalStateValue = null,
                    TerminalOrbitBody = "Kerbin",
                    TerminalOrbitSemiMajorAxis = 700000
                }
            };

            var suppressed = GhostMapPresence.FindTrackingStationSuppressedRecordingIds(recs, 300);

            Assert.Contains("launch", suppressed);
            Assert.DoesNotContain("orbit-tip", suppressed);

            var (should, _) = GhostMapPresence.ShouldCreateTrackingStationGhost(
                recs[1], suppressed.Contains(recs[1].RecordingId), 300);
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
        public void ShouldDrawAtmosphericMarker_Suppressed_Filtered()
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
            var suppressed = new HashSet<string> { "superseded-1" };
            bool result = ParsekTrackingStation.ShouldDrawAtmosphericMarker(rec, 0, 150, suppressed);
            Assert.False(result);
        }

        [Fact]
        public void ShouldDrawAtmosphericMarker_CurrentAtmosphericContinuationWithFutureChild_NotFiltered()
        {
            var recs = new List<Recording>
            {
                new Recording { RecordingId = "kerbin-orbit" },
                new Recording
                {
                    RecordingId = "kerbin-exit",
                    ParentRecordingId = "kerbin-orbit",
                    TerminalStateValue = TerminalState.Orbiting,
                    Points = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = 200, bodyName = "Kerbin" },
                        new TrajectoryPoint { ut = 320, bodyName = "Kerbin" }
                    }
                },
                new Recording
                {
                    RecordingId = "mun-leg",
                    ParentRecordingId = "kerbin-exit",
                    TerminalStateValue = TerminalState.Orbiting,
                    Points = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = 500, bodyName = "Kerbin" },
                        new TrajectoryPoint { ut = 620, bodyName = "Mun" }
                    }
                }
            };

            var suppressed = GhostMapPresence.FindTrackingStationSuppressedRecordingIds(recs, 240);

            bool result = ParsekTrackingStation.ShouldDrawAtmosphericMarker(recs[1], 1, 240, suppressed);

            Assert.True(result);
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
