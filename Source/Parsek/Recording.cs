using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Recommended merge action based on vessel state after recording.
    /// </summary>
    public enum MergeDefault
    {
        GhostOnly,  // Vessel destroyed or snapshot missing — merge recording only
        Persist      // Vessel intact with snapshot — respawn where it ended up
    }

    public enum LoopTimeUnit { Sec, Min, Hour, Auto }

    public class Recording
    {
        public string RecordingId = Guid.NewGuid().ToString("N");
        public int RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
        public int GhostGeometryVersion = 1; // Legacy field, kept for deserialization backward compat
        public List<TrajectoryPoint> Points = new List<TrajectoryPoint>();
        public List<OrbitSegment> OrbitSegments = new List<OrbitSegment>();
        public List<PartEvent> PartEvents = new List<PartEvent>();
        public List<FlagEvent> FlagEvents = new List<FlagEvent>();
        public List<SegmentEvent> SegmentEvents = new List<SegmentEvent>();
        public List<TrackSection> TrackSections = new List<TrackSection>();

        // Controller parts at segment start (for identity tracking)
        public List<ControllerInfo> Controllers;  // null = not set (legacy recording)

        // True if vessel has no controller parts (debris). Minimal recording only.
        public bool IsDebris;
        public bool LoopPlayback;
        public double LoopIntervalSeconds = 10.0;
        public LoopTimeUnit LoopTimeUnit = LoopTimeUnit.Sec;
        public uint LoopAnchorVesselId;  // Anchor vessel for relative loop playback (0 = no anchor, use absolute positioning)
        public string LoopAnchorBodyName;  // Body the anchor was on when loop was configured (null = not set)

        // UI grouping tags (e.g. "Synthetic", "Part Showcase") — multi-group membership
        public List<string> RecordingGroups;

        // Atmosphere segment metadata
        public string SegmentPhase;      // "atmo" or "exo" (null = untagged/legacy)
        public string SegmentBodyName;   // body name at split point (e.g., "Kerbin", "Duna")
        public bool PlaybackEnabled = true;  // false = skip ghost during playback
        public bool Hidden;                  // true = hidden from recordings list (unless Show Hidden is on)

        // EVA child recording linkage
        public string ParentRecordingId;
        public string EvaCrewName;

        // Chain linkage (multi-segment recording chains)
        public string ChainId;       // null = standalone; shared GUID for chain members
        public int ChainIndex = -1;  // -1 = not chained; 0-based position within chain
        public int ChainBranch;      // 0 = primary path; >0 = parallel continuation (ghost-only, no spawn)
        public string VesselName = "";
        public string GhostGeometryRelativePath;
        public bool GhostGeometryAvailable;
        public string GhostGeometryCaptureError;
        public string GhostGeometryCaptureStrategy; // Legacy field, kept for deserialization
        public string GhostGeometryProbeStatus;    // Legacy field, kept for deserialization

        // --- Tree linkage (null for legacy/standalone recordings) ---
        public string TreeId;                          // null = standalone (pre-tree recording)
        public uint VesselPersistentId;                // 0 = not set

        // --- Terminal state ---
        public TerminalState? TerminalStateValue;      // null = not yet terminated (still recording or legacy)

        // Terminal orbit (for Orbiting/SubOrbital terminal state)
        // Stored as Keplerian elements to avoid runtime Orbit object dependency in tests.
        public double TerminalOrbitInclination;
        public double TerminalOrbitEccentricity;
        public double TerminalOrbitSemiMajorAxis;
        public double TerminalOrbitLAN;
        public double TerminalOrbitArgumentOfPeriapsis;
        public double TerminalOrbitMeanAnomalyAtEpoch;
        public double TerminalOrbitEpoch;
        public string TerminalOrbitBody;

        // Terminal surface position (for Landed/Splashed terminal state)
        public SurfacePosition? TerminalPosition;      // null if not landed/splashed

        // Terrain height at recording end (for terrain correction on spawn)
        // NaN = not set (pre-v7 recording or non-surface terminal state)
        public double TerrainHeightAtEnd = double.NaN;

        // Antenna specifications for CommNet ghost relay registration (Phase 6f)
        // Extracted from ModuleDataTransmitter modules in vessel snapshot at commit time.
        // null = not extracted (legacy recording or no antennas).
        internal List<AntennaSpec> AntennaSpecs;

        // Background recording: surface position for landed/splashed vessels
        public SurfacePosition? SurfacePos;            // null if not a background landed vessel

        // Branch linkage
        public string ParentBranchPointId;             // null for root recording
        public string ChildBranchPointId;              // null for leaf recordings

        // Explicit UT range for recordings that may have no trajectory points
        // (background-only recordings). When Points.Count > 0, these are ignored
        // in favor of Points[0].ut / Points[last].ut.
        // Default is double.NaN (not set). 0.0 is a valid KSP UT.
        public double ExplicitStartUT = double.NaN;
        public double ExplicitEndUT = double.NaN;

        // Cached recording statistics (transient, recomputed on demand).
        // Tracks point count at cache time so continuation (which appends
        // points after commit) automatically invalidates the cache.
        internal RecordingStats? CachedStats;
        internal int CachedStatsPointCount;

        // Pre-launch resource snapshot (captured before recording starts)
        public double PreLaunchFunds;
        public double PreLaunchScience;
        public float PreLaunchReputation;

        // Rewind save (quicksave captured at recording start, stored in Parsek/Saves/)
        public string RewindSaveFileName;
        public double RewindReservedFunds;
        public double RewindReservedScience;
        public float RewindReservedRep;

        // Tracks which point's resource deltas have been applied during playback.
        // -1 means no resources applied yet (start from point 0's delta).
        public int LastAppliedResourceIndex = -1;

        // Vessel persistence fields (transient — only needed between revert and merge dialog)
        public ConfigNode VesselSnapshot;       // ProtoVessel as ConfigNode (null if destroyed)
        public ConfigNode GhostVisualSnapshot;  // Snapshot used for ghost visuals (prefer recording-start state)
        public double DistanceFromLaunch;       // Meters from launch position
        public bool VesselDestroyed;            // Vessel was destroyed before revert
        public string VesselSituation;          // "Orbiting Kerbin", "Landed on Mun", etc.
        public double MaxDistanceFromLaunch;     // Peak distance reached during recording
        public bool VesselSpawned;              // True after deferred RespawnVessel has fired
        public bool ForceSpawnNewVessel;        // Skip PID dedup — vessel exists at reverted position, not recording end (transient, not serialized)

        public uint SpawnedVesselPersistentId;  // persistentId of spawned vessel (0 = not yet spawned)
        public int SpawnAttempts;               // Number of failed spawn attempts (give up after 3)
        public int CollisionBlockCount;         // Consecutive collision-blocked frames (give up after MaxCollisionBlocks)
        public int SceneExitSituation = -1;     // Vessel.Situations at scene exit (-1 = still in flight/unknown)

        public double StartUT => Points.Count > 0 ? Points[0].ut :
                                 !double.IsNaN(ExplicitStartUT) ? ExplicitStartUT : 0.0;
        public double EndUT => Points.Count > 0 ? Points[Points.Count - 1].ut :
                               !double.IsNaN(ExplicitEndUT) ? ExplicitEndUT : 0.0;

        /// <summary>
        /// Copies persistence/capture artifacts from a stop-time captured recording.
        /// Intentionally does NOT copy Points/OrbitSegments/VesselName, which are
        /// set by StashPending from the current recorder buffers.
        /// </summary>
        public void ApplyPersistenceArtifactsFrom(Recording source)
        {
            if (source == null) return;

            VesselSnapshot = source.VesselSnapshot != null
                ? source.VesselSnapshot.CreateCopy()
                : null;
            GhostVisualSnapshot = source.GhostVisualSnapshot != null
                ? source.GhostVisualSnapshot.CreateCopy()
                : null;
            RecordingId = source.RecordingId;
            DistanceFromLaunch = source.DistanceFromLaunch;
            VesselDestroyed = source.VesselDestroyed;
            VesselSituation = source.VesselSituation;
            MaxDistanceFromLaunch = source.MaxDistanceFromLaunch;
            RecordingFormatVersion = source.RecordingFormatVersion;
            ParentRecordingId = source.ParentRecordingId;
            EvaCrewName = source.EvaCrewName;
            ChainId = source.ChainId;
            ChainIndex = source.ChainIndex;
            ChainBranch = source.ChainBranch;
            LoopPlayback = source.LoopPlayback;
            LoopIntervalSeconds = source.LoopIntervalSeconds;
            LoopTimeUnit = source.LoopTimeUnit;
            LoopAnchorVesselId = source.LoopAnchorVesselId;
            LoopAnchorBodyName = source.LoopAnchorBodyName;
            PreLaunchFunds = source.PreLaunchFunds;
            PreLaunchScience = source.PreLaunchScience;
            PreLaunchReputation = source.PreLaunchReputation;
            RewindSaveFileName = source.RewindSaveFileName;
            RewindReservedFunds = source.RewindReservedFunds;
            RewindReservedScience = source.RewindReservedScience;
            RewindReservedRep = source.RewindReservedRep;
            SegmentPhase = source.SegmentPhase;
            SegmentBodyName = source.SegmentBodyName;
            PlaybackEnabled = source.PlaybackEnabled;
            Hidden = source.Hidden;
            TreeId = source.TreeId;
            VesselPersistentId = source.VesselPersistentId;
            TerminalStateValue = source.TerminalStateValue;
            TerminalOrbitInclination = source.TerminalOrbitInclination;
            TerminalOrbitEccentricity = source.TerminalOrbitEccentricity;
            TerminalOrbitSemiMajorAxis = source.TerminalOrbitSemiMajorAxis;
            TerminalOrbitLAN = source.TerminalOrbitLAN;
            TerminalOrbitArgumentOfPeriapsis = source.TerminalOrbitArgumentOfPeriapsis;
            TerminalOrbitMeanAnomalyAtEpoch = source.TerminalOrbitMeanAnomalyAtEpoch;
            TerminalOrbitEpoch = source.TerminalOrbitEpoch;
            TerminalOrbitBody = source.TerminalOrbitBody;
            TerminalPosition = source.TerminalPosition;
            TerrainHeightAtEnd = source.TerrainHeightAtEnd;
            SurfacePos = source.SurfacePos;
            ParentBranchPointId = source.ParentBranchPointId;
            ChildBranchPointId = source.ChildBranchPointId;
            ExplicitStartUT = source.ExplicitStartUT;
            ExplicitEndUT = source.ExplicitEndUT;
            RecordingGroups = source.RecordingGroups != null
                ? new List<string>(source.RecordingGroups) : null;
            AntennaSpecs = source.AntennaSpecs != null
                ? new List<AntennaSpec>(source.AntennaSpecs) : null;

            // Copy segment events and tracks if source has them
            if (source.SegmentEvents != null && source.SegmentEvents.Count > 0)
                SegmentEvents = new List<SegmentEvent>(source.SegmentEvents);
            if (source.TrackSections != null && source.TrackSections.Count > 0)
                TrackSections = new List<TrackSection>(source.TrackSections);
            if (source.Controllers != null)
                Controllers = new List<ControllerInfo>(source.Controllers);
            IsDebris = source.IsDebris;
        }

        /// <summary>
        /// Resolves KSP localization keys (e.g., "#autoLOC_501220") to human-readable text
        /// via KSP.Localization.Localizer. Returns the input unchanged if it is not a
        /// localization key or if the Localizer is unavailable (e.g., unit tests).
        /// </summary>
        internal static string ResolveLocalizedName(string name)
        {
            if (string.IsNullOrEmpty(name) || name[0] != '#')
                return name;
            try
            {
                string resolved = KSP.Localization.Localizer.Format(name);
                if (!string.IsNullOrEmpty(resolved) && resolved != name)
                {
                    ParsekLog.Info("Recording", $"Resolved localized name: '{name}' -> '{resolved}'");
                    return resolved;
                }
            }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("Recording", $"Localizer unavailable for '{name}': {ex.GetType().Name}");
            }
            return name;
        }
    }
}
