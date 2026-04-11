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

    public class Recording : IPlaybackTrajectory
    {
        public string RecordingId = Guid.NewGuid().ToString("N");
        public int RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
        public List<TrajectoryPoint> Points = new List<TrajectoryPoint>();
        public List<OrbitSegment> OrbitSegments = new List<OrbitSegment>();
        public bool HasOrbitSegments => OrbitSegments != null && OrbitSegments.Count > 0;
        public List<PartEvent> PartEvents = new List<PartEvent>();
        public List<FlagEvent> FlagEvents = new List<FlagEvent>();
        public List<SegmentEvent> SegmentEvents = new List<SegmentEvent>();
        public List<TrackSection> TrackSections = new List<TrackSection>();

        // Controller parts at segment start (for identity tracking)
        public List<ControllerInfo> Controllers;  // null = not set (no controller data)

        // True if vessel has no controller parts (debris). Minimal recording only.
        public bool IsDebris;

        // Cascade depth from primary recording. 0 = primary recording (active vessel),
        // 1 = primary debris (boosters/fairings decoupled by gen-0 vessel). Background
        // splits whose parent is at Generation >= MaxRecordingGeneration are skipped
        // entirely — fragments-of-fragments stay alive in KSP but Parsek does not
        // track them. See bug #284 and BackgroundRecorder.HandleBackgroundVesselSplit.
        // Persisted in .sfs (RecordingTree.SaveRecordingInto/LoadRecordingFrom) so the
        // cascade cap remains correct after F5/F9 — without persistence, a gen-1 booster
        // would reload as gen-0 and a subsequent breakup would slip past the cap.
        public int Generation;

        // Loop sync: debris follows parent recording's loop clock (-1 = independent).
        // Index into CommittedRecordings / engine trajectories list. Recomputed on every
        // RunOptimizationPass call (after every commit and at load). The list is stable
        // between passes because commits always trigger a full recompute. Not serialized.
        public int LoopSyncParentIdx { get; set; } = -1;

        public bool LoopPlayback;
        public double LoopIntervalSeconds = 10.0;
        public LoopTimeUnit LoopTimeUnit = LoopTimeUnit.Sec;
        public double LoopStartUT = double.NaN;  // NaN = use StartUT (loop entire recording)
        public double LoopEndUT = double.NaN;    // NaN = use EndUT (loop entire recording)
        public uint LoopAnchorVesselId;  // Anchor vessel for relative loop playback (0 = no anchor, use absolute positioning)
        public string LoopAnchorBodyName;  // Body the anchor was on when loop was configured (null = not set)

        // UI grouping tags (e.g. "Synthetic", "Part Showcase") — multi-group membership
        public List<string> RecordingGroups;

        // Dirty flag: true when sidecar file data (trajectory, events, sections) has changed
        // since last SaveRecordingFiles call. Checked in OnSave to skip unchanged recordings.
        [NonSerialized] public bool FilesDirty;

        /// <summary>
        /// Marks this recording as needing its <c>.prec</c> sidecar file rewritten
        /// on the next <c>OnSave</c>. MUST be called after any mutation to
        /// <see cref="Points"/>, <see cref="PartEvents"/>, <see cref="OrbitSegments"/>,
        /// <see cref="FlagEvents"/>, or <see cref="TrackSections"/> — the sidecar is
        /// only written when <see cref="FilesDirty"/> is true, so without this call
        /// the data lives only in memory and is lost on scene reload
        /// (<see cref="TryRestoreActiveTreeNode"/> reads the stale file and
        /// produces a 0-point recording). See <c>docs/dev/todo-and-known-bugs.md</c>
        /// #273 for the 2026-04-09 data-loss bug this invariant closes.
        /// </summary>
        internal void MarkFilesDirty()
        {
            FilesDirty = true;
        }

        // Continuation rollback (bug #95): transient boundary for rolling back continuation
        // data on revert. Set when continuation starts, cleared on normal stop (bake) or
        // revert (rollback). -1 = no active continuation.
        [NonSerialized] internal int ContinuationBoundaryIndex = -1;
        [NonSerialized] internal ConfigNode PreContinuationVesselSnapshot;
        [NonSerialized] internal ConfigNode PreContinuationGhostSnapshot;

        // Atmosphere segment metadata
        public string SegmentPhase;      // "atmo", "exo", or "approach" (null = untagged/legacy)
        public string SegmentBodyName;   // body name at split point (e.g., "Kerbin", "Duna")

        // Location context (Phase 10) — body, biome, situation at recording start and end
        public string StartBodyName;     // body at recording start (null = not set / legacy)
        public string StartBiome;        // biome at recording start (null = not set / unavailable)
        public string StartSituation;    // vessel situation at recording start (null = not set)
        public string EndBiome;          // biome at recording end (null = not set / unavailable)
        public string LaunchSiteName;    // stock/mod launch site name (null = not launched from a site)
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
        // NaN = not set (non-surface terminal state)
        public double TerrainHeightAtEnd = double.NaN;

        // Antenna specifications for CommNet ghost relay registration (Phase 6f)
        // Extracted from ModuleDataTransmitter modules in vessel snapshot at commit time.
        // null = not extracted (legacy recording or no antennas).
        internal List<AntennaSpec> AntennaSpecs;

        // Per-crew end state (inferred at commit time from terminal state + snapshot)
        // null = not yet populated (legacy recording or pre-commit).
        public Dictionary<string, KerbalEndState> CrewEndStates;

        // Resource manifests (Phase 11) — per-resource amount/capacity at recording start and end
        // null = no data (legacy recording or not yet captured)
        internal Dictionary<string, ResourceAmount> StartResources;
        internal Dictionary<string, ResourceAmount> EndResources;

        // Inventory manifests (Phase 11) — per-item count/slots at recording start and end
        // null = no data (legacy recording or no inventory items)
        internal Dictionary<string, InventoryItem> StartInventory;
        internal Dictionary<string, InventoryItem> EndInventory;
        public int StartInventorySlots;  // total inventory slot capacity at start (0 = no data / no inventory)
        public int EndInventorySlots;    // total inventory slot capacity at end

        // PID of vessel docked to at this segment's boundary (0 = not a dock segment)
        public uint DockTargetVesselPid;

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

        public uint SpawnedVesselPersistentId;  // persistentId of spawned vessel (0 = not yet spawned)
        public int SpawnAttempts;               // Number of failed spawn attempts (give up after 3)
        public int CollisionBlockCount;         // Consecutive collision-blocked frames (give up after MaxCollisionBlocks)
        public bool SpawnAbandoned;              // True after collision/death limit reached — prevents vessel-gone check from resetting (transient)
        public bool WalkbackExhausted;           // True after TryWalkbackForEndOfRecordingSpawn scanned entire trajectory with no clear sub-step — distinct from SpawnAbandoned for diagnostics (transient, #264)
        public bool DuplicateBlockerRecovered;   // True after a same-name blocker was recovered once — prevents recovery loops (transient, #112)
        public int SpawnDeathCount;              // Spawn-then-die cycles: vessel spawned but immediately destroyed (transient)
        public int SceneExitSituation = -1;     // Vessel.Situations at scene exit (-1 = still in flight/unknown)

        public double StartUT => Points.Count > 0 ? Points[0].ut :
                                 !double.IsNaN(ExplicitStartUT) ? ExplicitStartUT : 0.0;
        public double EndUT => Points.Count > 0 ? Points[Points.Count - 1].ut :
                               !double.IsNaN(ExplicitEndUT) ? ExplicitEndUT : 0.0;

        /// <summary>
        /// Compact, grep-friendly identity string for diagnostic logs:
        /// <c>rec[abc12345|KerbalX|tree|0]</c>. Format is
        /// <c>rec[&lt;id8&gt;|&lt;vesselName&gt;|&lt;mode&gt;|&lt;chainIdx&gt;]</c> where
        /// <c>id8</c> is the first 8 chars of the recording id (or <c>-</c>),
        /// <c>mode</c> is <c>tree</c> if the recording belongs to a tree else <c>sa</c>,
        /// and <c>chainIdx</c> is the chain index or <c>-</c> for unchained recordings.
        /// Used inline in free-text log lines that reference a single recording so
        /// the reader gets full identity context without cross-referencing other lines.
        /// </summary>
        internal string DebugName
        {
            get
            {
                string idShort;
                if (string.IsNullOrEmpty(RecordingId))
                    idShort = "-";
                else if (RecordingId.Length <= 8)
                    idShort = RecordingId;
                else
                    idShort = RecordingId.Substring(0, 8);

                string mode = IsTreeRecording ? "tree" : "sa";
                string chain = ChainIndex >= 0
                    ? ChainIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "-";
                string vessel = string.IsNullOrEmpty(VesselName) ? "-" : VesselName;
                return "rec[" + idShort + "|" + vessel + "|" + mode + "|" + chain + "]";
            }
        }

        /// <summary>True if this recording belongs to a RecordingTree.</summary>
        internal bool IsTreeRecording => TreeId != null;

        /// <summary>True if this recording belongs to a chain (has ChainId and valid ChainIndex).</summary>
        internal bool IsChainRecording => !string.IsNullOrEmpty(ChainId);

        /// <summary>
        /// True if this recording's resources are tracked individually (per-recording deltas).
        /// False for tree recordings, whose resources are tracked at tree level.
        /// </summary>
        internal bool ManagesOwnResources => !IsTreeRecording;

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
            LoopStartUT = source.LoopStartUT;
            LoopEndUT = source.LoopEndUT;
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
            EndBiome = source.EndBiome;
            // Note: Start location fields intentionally NOT copied here.
            // This method copies end-state from a previous segment onto a new one at chain
            // boundaries. Start fields are captured fresh per segment in StartRecording.
            // Use CopyStartLocationFrom for explicit start-field propagation.
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
            StartResources = source.StartResources;
            EndResources = source.EndResources;
            StartInventory = source.StartInventory;
            EndInventory = source.EndInventory;
            StartInventorySlots = source.StartInventorySlots;
            EndInventorySlots = source.EndInventorySlots;
            DockTargetVesselPid = source.DockTargetVesselPid;

            // Copy segment events and tracks if source has them
            if (source.SegmentEvents != null && source.SegmentEvents.Count > 0)
                SegmentEvents = new List<SegmentEvent>(source.SegmentEvents);
            if (source.TrackSections != null && source.TrackSections.Count > 0)
                TrackSections = DeepCopyTrackSections(source.TrackSections);
            if (source.Controllers != null)
                Controllers = new List<ControllerInfo>(source.Controllers);
            IsDebris = source.IsDebris;
            // Generation is transient, but copied so the cascade-depth state is
            // preserved across StashPending/commit boundaries within a tree session.
            // Loaded recordings reset to 0 since the field is [NonSerialized].
            Generation = source.Generation;
        }

        /// <summary>
        /// Copies start location fields from a source recording.
        /// Used at commit paths where ApplyPersistenceArtifactsFrom intentionally
        /// excludes start fields (they're end-state artifacts, not start-state).
        /// </summary>
        public void CopyStartLocationFrom(Recording source)
        {
            if (source == null) return;
            StartBodyName = source.StartBodyName;
            StartBiome = source.StartBiome;
            StartSituation = source.StartSituation;
            LaunchSiteName = source.LaunchSiteName;
        }

        /// <summary>
        /// Deep copies a list of TrackSection structs, creating new list instances for
        /// the mutable frames and checkpoints fields. Prevents shared references between
        /// original and copy (Bug #81).
        /// </summary>
        internal static List<TrackSection> DeepCopyTrackSections(List<TrackSection> source)
        {
            var result = new List<TrackSection>(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                var s = source[i];
                var copy = s;
                copy.frames = s.frames != null ? new List<TrajectoryPoint>(s.frames) : null;
                copy.checkpoints = s.checkpoints != null ? new List<OrbitSegment>(s.checkpoints) : null;
                result.Add(copy);
            }
            return result;
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

        #region IPlaybackTrajectory explicit implementation

        // Fields don't satisfy interface properties in C#, so we forward explicitly.
        // StartUT and EndUT are already expression-bodied properties — they satisfy the interface implicitly.
        List<TrajectoryPoint> IPlaybackTrajectory.Points => Points;
        List<OrbitSegment> IPlaybackTrajectory.OrbitSegments => OrbitSegments;
        List<TrackSection> IPlaybackTrajectory.TrackSections => TrackSections;
        int IPlaybackTrajectory.RecordingFormatVersion => RecordingFormatVersion;
        List<PartEvent> IPlaybackTrajectory.PartEvents => PartEvents;
        List<FlagEvent> IPlaybackTrajectory.FlagEvents => FlagEvents;
        ConfigNode IPlaybackTrajectory.GhostVisualSnapshot => GhostVisualSnapshot;
        ConfigNode IPlaybackTrajectory.VesselSnapshot => VesselSnapshot;
        string IPlaybackTrajectory.VesselName => VesselName;
        bool IPlaybackTrajectory.LoopPlayback => LoopPlayback;
        double IPlaybackTrajectory.LoopIntervalSeconds => LoopIntervalSeconds;
        LoopTimeUnit IPlaybackTrajectory.LoopTimeUnit => LoopTimeUnit;
        uint IPlaybackTrajectory.LoopAnchorVesselId => LoopAnchorVesselId;
        double IPlaybackTrajectory.LoopStartUT => LoopStartUT;
        double IPlaybackTrajectory.LoopEndUT => LoopEndUT;
        TerminalState? IPlaybackTrajectory.TerminalStateValue => TerminalStateValue;
        SurfacePosition? IPlaybackTrajectory.SurfacePos => SurfacePos;
        double IPlaybackTrajectory.TerrainHeightAtEnd => TerrainHeightAtEnd;
        bool IPlaybackTrajectory.PlaybackEnabled => PlaybackEnabled;
        bool IPlaybackTrajectory.IsDebris => IsDebris;
        string IPlaybackTrajectory.TerminalOrbitBody => TerminalOrbitBody;
        double IPlaybackTrajectory.TerminalOrbitSemiMajorAxis => TerminalOrbitSemiMajorAxis;
        double IPlaybackTrajectory.TerminalOrbitEccentricity => TerminalOrbitEccentricity;
        double IPlaybackTrajectory.TerminalOrbitInclination => TerminalOrbitInclination;
        double IPlaybackTrajectory.TerminalOrbitLAN => TerminalOrbitLAN;
        double IPlaybackTrajectory.TerminalOrbitArgumentOfPeriapsis => TerminalOrbitArgumentOfPeriapsis;
        double IPlaybackTrajectory.TerminalOrbitMeanAnomalyAtEpoch => TerminalOrbitMeanAnomalyAtEpoch;
        double IPlaybackTrajectory.TerminalOrbitEpoch => TerminalOrbitEpoch;

        #endregion
    }
}
