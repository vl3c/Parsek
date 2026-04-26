using System;
using System.Collections.Generic;

namespace Parsek
{
    public enum LoopTimeUnit { Sec, Min, Hour, Auto }
    public enum GhostSnapshotMode { Unspecified = 0, Separate = 1, AliasVessel = 2 }

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

        // True if this recording was created via the Gloops Flight Recorder (manual ghost-only).
        // Ghost-only recordings never spawn a real vessel at playback end.
        public bool IsGhostOnly;

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
        /// <summary>
        /// Launch-to-launch period in seconds (#381). Must be &gt;= LoopTiming.MinCycleDuration.
        /// When less than the recording duration, successive launches overlap (multi-ghost
        /// overlap path). When greater, there is a pause window between cycles.
        /// </summary>
        public double LoopIntervalSeconds = LoopTiming.UntouchedLoopIntervalSentinel;
        public LoopTimeUnit LoopTimeUnit = LoopTimeUnit.Sec;
        public double LoopStartUT = double.NaN;  // NaN = use StartUT (loop entire recording)
        public double LoopEndUT = double.NaN;    // NaN = use EndUT (loop entire recording)
        public uint LoopAnchorVesselId;  // Anchor vessel for relative loop playback (0 = no anchor, use absolute positioning)
        public string LoopAnchorBodyName;  // Body the anchor was on when loop was configured (null = not set)

        // UI grouping tags (e.g. "Synthetic", "Part Showcase") — multi-group membership
        public List<string> RecordingGroups;
        public string AutoAssignedStandaloneGroupName;

        // Dirty flag: true when sidecar file data (trajectory, events, sections) has changed
        // since last SaveRecordingFiles call. Checked in OnSave to skip unchanged recordings.
        [NonSerialized] public bool FilesDirty;

        // Sidecar epoch: monotonically increasing counter stamped into both .sfs metadata
        // and .prec sidecar file on each write. On load, if the .prec epoch doesn't match
        // the .sfs epoch, the sidecar is stale (written by a different save point) and
        // trajectory data is skipped. See bug #270.
        // Intentionally NOT [NonSerialized] — persists via ConfigNode in RecordingTree
        // Save/LoadRecordingInto so the epoch survives scene reloads.
        internal int SidecarEpoch;

        // Runtime-only hydration state: LoadRecordingFiles can fail because the current save
        // point does not have a compatible sidecar for this recording. These flags are used to
        // avoid destructive follow-on behavior (for example pruning an empty leaf that only
        // looks empty because sidecar hydration was rejected).
        [NonSerialized] internal bool SidecarLoadFailed;
        [NonSerialized] internal string SidecarLoadFailureReason;

        // Bug #572 follow-up (PR after #572). Transient marker set by
        // ParsekScenario.RestoreCommittedSidecarPayloadIntoActiveTreeRecording when
        // an active-tree record is repaired from the matching committed tree
        // because its sidecar hydration failed. The immediately-following
        // FinalizeTreeRecordings on scene exit must NOT infer Landed/Splashed
        // from this recording's last trajectory point — the trajectory came
        // from the same committed copy that already lacked a terminal state,
        // so the "vessel was alive when unloaded" assumption that justifies
        // surface inference does not apply. The Re-Fly strip path makes the
        // missing live vessel a deliberate kill, not an unload. See
        // ParsekFlight.FinalizeIndividualRecording / EnsureActiveRecordingTerminalState
        // and docs/dev/plans/refly-finalize-stripped-vessel-landed-fix.md for
        // rationale. NotSerialized so a fresh session always reads false.
        [NonSerialized] internal bool RestoredFromCommittedTreeThisFrame;

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
        [NonSerialized] internal string PreReFlyAnchorSessionId;
        [NonSerialized] internal List<TrajectoryPoint> PreReFlyAnchorPoints;
        [NonSerialized] internal List<OrbitSegment> PreReFlyAnchorOrbitSegments;
        [NonSerialized] internal List<TrackSection> PreReFlyAnchorTrackSections;

        // Atmosphere segment metadata
        public string SegmentPhase;      // "atmo", "exo", or "approach" (null = untagged/legacy)
        public string SegmentBodyName;   // body name at split point (e.g., "Kerbin", "Duna")

        // Location context (Phase 10) — body, biome, situation at recording start and end
        public string StartBodyName;     // body at recording start (null = not set / legacy)
        public string StartBiome;        // biome at recording start (null = not set / unavailable)
        public string StartSituation;    // vessel situation at recording start (null = not set)
        public string EndBiome;          // biome at recording end (null = not set / unavailable)
        public string LaunchSiteName;    // stock/mod launch site name (null = not launched from a site)
        // false = hide ghost during playback. Visual-only: does not affect ledger
        // actions, vessel spawn, crew reservations, or resource budget. (bug #433)
        public bool PlaybackEnabled = true;
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
        public int TreeOrder = -1;                    // persisted insertion / creation order within the tree
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

        // Authoritative endpoint decision captured at finalize/load-heal time.
        // Phase = which persisted source drives endpoint resolution.
        // Body = body paired with that phase, so exact-boundary point/orbit cases
        // do not have to be re-inferred from timestamp epsilon checks later.
        public RecordingEndpointPhase EndpointPhase;
        public string EndpointBodyName;

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
        // True once PopulateCrewEndStates has reached a final answer for this
        // recording. This remains false for legacy/unprocessed recordings, and
        // becomes true even when the recording is confirmed to have no crew.
        public bool CrewEndStatesResolved;

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

        // Crew manifests (Phase 11) — per-trait crew count at recording start and end
        // null = no data (legacy recording or no crew)
        internal Dictionary<string, int> StartCrew;
        internal Dictionary<string, int> EndCrew;

        // PID of vessel docked to at this segment's boundary (0 = not a dock segment)
        public uint DockTargetVesselPid;

        // Background recording: surface position for landed/splashed vessels
        public SurfacePosition? SurfacePos;            // null if not a background landed vessel

        // Rewind-to-Staging (design doc section 5.5)
        // MergeState tri-state replaces the binary committed/uncommitted flag.
        // Immutable is the legacy default: every recording reachable from a committed
        // tree was sealed pre-feature. NotCommitted/CommittedProvisional become possible
        // only once the Rewind-to-Staging feature is live.
        public MergeState MergeState = MergeState.Immutable;
        // Session GUID for recordings produced during an active re-fly session. Null
        // outside sessions. Used by the load-time spare-set logic when a session crashed.
        public string CreatingSessionId;
        // Transient: set only on NotCommitted provisional re-fly recordings to signal
        // the intended supersede target. Replaced by a concrete RecordingSupersedeRelation
        // at merge time and cleared. Non-empty values on Immutable/CommittedProvisional
        // recordings are treated as cleared at load with a Warn (legacy-write safety).
        public string SupersedeTargetId;
        // Rewind-Point back-pointer for provisional re-fly recordings. Null outside sessions.
        public string ProvisionalForRpId;

        // Branch linkage
        public string ParentBranchPointId;             // null for root recording
        public string ChildBranchPointId;              // null for leaf recordings

        // Explicit UT range for the recording's outer semantic boundary
        // (background-only recordings, split/merge boundaries, etc.).
        // These values may extend the computed payload bounds, but must never shrink them:
        // StartUT only uses ExplicitStartUT when it is earlier than the actual trajectory
        // start, and EndUT only uses ExplicitEndUT when it is later than the actual
        // trajectory end. Ghost activation-start resolution depends on that ordering:
        // it probes first playable payload separately and falls back to StartUT only
        // when no playable payload exists at all.
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
        public GhostSnapshotMode GhostSnapshotMode;
        public double DistanceFromLaunch;       // Meters from launch position
        public bool VesselDestroyed;            // Vessel was destroyed before revert
        public string VesselSituation;          // "Orbiting Kerbin", "Landed on Mun", etc.
        public double MaxDistanceFromLaunch;     // Peak distance reached during recording
        public bool VesselSpawned;              // True after deferred RespawnVessel has fired

        public uint SpawnedVesselPersistentId;  // persistentId of spawned vessel (0 = not yet spawned)
        public string TerminalSpawnSupersededByRecordingId; // Later continuation owns the real terminal spawn
        public int SpawnAttempts;               // Number of failed spawn attempts (give up after 3)
        public int CollisionBlockCount;         // Consecutive collision-blocked frames (give up after MaxCollisionBlocks)
        public bool SpawnAbandoned;              // True after collision/death limit reached — prevents vessel-gone check from resetting (transient)
        public bool WalkbackExhausted;           // True after TryWalkbackForEndOfRecordingSpawn scanned entire trajectory with no clear sub-step — distinct from SpawnAbandoned for diagnostics (transient, #264)
        public bool DuplicateBlockerRecovered;   // True after a same-name blocker was recovered once — prevents recovery loops (transient, #112)
        public int SpawnDeathCount;              // Spawn-then-die cycles: vessel spawned but immediately destroyed (transient)
        public int SceneExitSituation = -1;     // Vessel.Situations at scene exit (-1 = still in flight/unknown)
        public bool SpawnSuppressedByRewind;     // True only for the active/source recording protected by plain Rewind-to-Launch strip cleanup (#573). Persisted with scoped metadata below so legacy broad markers can be ignored/cleared (#589).
        public string SpawnSuppressedByRewindReason;
        public double SpawnSuppressedByRewindUT = double.NaN;

        public double StartUT
        {
            get
            {
                if (TryGetActualTrajectoryBounds(out double startUT, out _))
                {
                    if (!double.IsNaN(ExplicitStartUT) && ExplicitStartUT < startUT)
                        return ExplicitStartUT;
                    return startUT;
                }

                return !double.IsNaN(ExplicitStartUT) ? ExplicitStartUT : 0.0;
            }
        }

        public double EndUT
        {
            get
            {
                if (TryGetActualTrajectoryBounds(out _, out double endUT))
                {
                    if (!double.IsNaN(ExplicitEndUT) && ExplicitEndUT > endUT)
                        return ExplicitEndUT;
                    return endUT;
                }

                return !double.IsNaN(ExplicitEndUT) ? ExplicitEndUT : 0.0;
            }
        }

        internal bool TryGetGhostActivationStartUT(out double startUT)
        {
            if (PlaybackTrajectoryBoundsResolver.TryGetGhostPlayablePayloadBounds(this, out startUT, out _))
                return true;

            if (!double.IsNaN(ExplicitStartUT))
            {
                startUT = ExplicitStartUT;
                return true;
            }

            startUT = 0.0;
            return false;
        }

        private bool TryGetActualTrajectoryBounds(out double startUT, out double endUT)
        {
            startUT = 0.0;
            endUT = 0.0;
            bool found = false;

            if (Points != null && Points.Count > 0)
            {
                startUT = Points[0].ut;
                endUT = Points[Points.Count - 1].ut;
                found = true;
            }

            if (OrbitSegments != null && OrbitSegments.Count > 0)
            {
                double orbitStartUT = OrbitSegments[0].startUT;
                double orbitEndUT = OrbitSegments[OrbitSegments.Count - 1].endUT;
                if (!found || orbitStartUT < startUT)
                    startUT = orbitStartUT;
                if (!found || orbitEndUT > endUT)
                    endUT = orbitEndUT;
                found = true;
            }

            if (TryGetPlayableTrackSectionBounds(out double sectionStartUT, out double sectionEndUT))
            {
                if (!found || sectionStartUT < startUT)
                    startUT = sectionStartUT;
                if (!found || sectionEndUT > endUT)
                    endUT = sectionEndUT;
                found = true;
            }

            return found;
        }

        private bool TryGetPlayableTrackSectionBounds(out double startUT, out double endUT)
        {
            startUT = 0.0;
            endUT = 0.0;
            if (TrackSections == null || TrackSections.Count == 0)
                return false;

            int firstPlayable = -1;
            for (int i = 0; i < TrackSections.Count; i++)
            {
                if (PlaybackTrajectoryBoundsResolver.HasPlayablePayload(TrackSections[i]))
                {
                    firstPlayable = i;
                    break;
                }
            }

            if (firstPlayable < 0)
                return false;

            int lastPlayable = -1;
            for (int i = TrackSections.Count - 1; i >= firstPlayable; i--)
            {
                if (PlaybackTrajectoryBoundsResolver.HasPlayablePayload(TrackSections[i]))
                {
                    lastPlayable = i;
                    break;
                }
            }

            if (lastPlayable < 0)
                return false;

            startUT = TrackSections[firstPlayable].startUT;
            endUT = TrackSections[lastPlayable].endUT;
            return true;
        }

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
        // In always-tree mode (post-T56), this is expected to be true for all committed recordings.
        internal bool IsTreeRecording => TreeId != null;

        /// <summary>True if this recording belongs to a chain (has ChainId and valid ChainIndex).</summary>
        internal bool IsChainRecording => !string.IsNullOrEmpty(ChainId);

        // Phase F: ManagesOwnResources removed. The standalone resource applier
        // (per-recording lump-sum replay via ApplyResourceDeltas) and the tree-level
        // applier (ApplyTreeLumpSum) are both gone — the ledger drives funds/science/
        // reputation directly. ResourceBudget now sums every recording uniformly,
        // and there are no remaining callers gating on standalone-vs-tree.

        /// <summary>
        /// Copies persistence/capture artifacts from a stop-time captured recording.
        /// Intentionally does NOT copy Points/OrbitSegments/VesselName, which are
        /// set by CreateRecordingFromFlightData from the current recorder buffers.
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
            GhostSnapshotMode = source.GhostSnapshotMode;
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
            TreeOrder = source.TreeOrder;
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
            EndpointPhase = source.EndpointPhase;
            EndpointBodyName = source.EndpointBodyName;
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
            StartCrew = source.StartCrew;
            EndCrew = source.EndCrew;
            DockTargetVesselPid = source.DockTargetVesselPid;
            CrewEndStatesResolved = source.CrewEndStatesResolved;
            TerminalSpawnSupersededByRecordingId = source.TerminalSpawnSupersededByRecordingId;

            // Copy segment events and tracks if source has them
            if (source.SegmentEvents != null && source.SegmentEvents.Count > 0)
                SegmentEvents = new List<SegmentEvent>(source.SegmentEvents);
            if (source.TrackSections != null && source.TrackSections.Count > 0)
                TrackSections = DeepCopyTrackSections(source.TrackSections);
            if (source.Controllers != null)
                Controllers = new List<ControllerInfo>(source.Controllers);
            IsDebris = source.IsDebris;
            IsGhostOnly = source.IsGhostOnly;
            // Generation is transient, but copied so the cascade-depth state is
            // preserved across recording creation/commit boundaries within a tree session.
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

        internal static Recording DeepClone(Recording source)
        {
            if (source == null) return null;

            var clone = new Recording();
            clone.ApplyPersistenceArtifactsFrom(source);
            clone.CopyStartLocationFrom(source);
            clone.VesselName = source.VesselName;

            clone.Points = source.Points != null
                ? new List<TrajectoryPoint>(source.Points)
                : new List<TrajectoryPoint>();
            clone.OrbitSegments = source.OrbitSegments != null
                ? new List<OrbitSegment>(source.OrbitSegments)
                : new List<OrbitSegment>();
            clone.PartEvents = source.PartEvents != null
                ? new List<PartEvent>(source.PartEvents)
                : new List<PartEvent>();
            clone.FlagEvents = source.FlagEvents != null
                ? new List<FlagEvent>(source.FlagEvents)
                : new List<FlagEvent>();
            clone.SegmentEvents = source.SegmentEvents != null
                ? new List<SegmentEvent>(source.SegmentEvents)
                : new List<SegmentEvent>();
            clone.TrackSections = source.TrackSections != null
                ? DeepCopyTrackSections(source.TrackSections)
                : new List<TrackSection>();
            clone.Controllers = source.Controllers != null
                ? new List<ControllerInfo>(source.Controllers)
                : null;
            clone.CrewEndStates = source.CrewEndStates != null
                ? new Dictionary<string, KerbalEndState>(source.CrewEndStates)
                : null;
            clone.CrewEndStatesResolved = source.CrewEndStatesResolved;
            clone.StartResources = source.StartResources != null
                ? new Dictionary<string, ResourceAmount>(source.StartResources)
                : null;
            clone.EndResources = source.EndResources != null
                ? new Dictionary<string, ResourceAmount>(source.EndResources)
                : null;
            clone.StartInventory = source.StartInventory != null
                ? new Dictionary<string, InventoryItem>(source.StartInventory)
                : null;
            clone.EndInventory = source.EndInventory != null
                ? new Dictionary<string, InventoryItem>(source.EndInventory)
                : null;
            clone.StartCrew = source.StartCrew != null
                ? new Dictionary<string, int>(source.StartCrew)
                : null;
            clone.EndCrew = source.EndCrew != null
                ? new Dictionary<string, int>(source.EndCrew)
                : null;
            clone.FilesDirty = source.FilesDirty;
            clone.SidecarEpoch = source.SidecarEpoch;
            clone.SidecarLoadFailed = source.SidecarLoadFailed;
            clone.SidecarLoadFailureReason = source.SidecarLoadFailureReason;
            clone.LoopSyncParentIdx = source.LoopSyncParentIdx;
            clone.CachedStats = source.CachedStats;
            clone.CachedStatsPointCount = source.CachedStatsPointCount;
            clone.LastAppliedResourceIndex = source.LastAppliedResourceIndex;
            clone.ContinuationBoundaryIndex = source.ContinuationBoundaryIndex;
            clone.PreContinuationVesselSnapshot = source.PreContinuationVesselSnapshot != null
                ? source.PreContinuationVesselSnapshot.CreateCopy()
                : null;
            clone.PreContinuationGhostSnapshot = source.PreContinuationGhostSnapshot != null
                ? source.PreContinuationGhostSnapshot.CreateCopy()
                : null;
            clone.VesselSpawned = source.VesselSpawned;
            clone.SpawnedVesselPersistentId = source.SpawnedVesselPersistentId;
            clone.TerminalSpawnSupersededByRecordingId = source.TerminalSpawnSupersededByRecordingId;
            clone.SpawnAttempts = source.SpawnAttempts;
            clone.CollisionBlockCount = source.CollisionBlockCount;
            clone.SpawnAbandoned = source.SpawnAbandoned;
            clone.WalkbackExhausted = source.WalkbackExhausted;
            clone.DuplicateBlockerRecovered = source.DuplicateBlockerRecovered;
            clone.SpawnDeathCount = source.SpawnDeathCount;
            clone.SceneExitSituation = source.SceneExitSituation;
            clone.SpawnSuppressedByRewind = source.SpawnSuppressedByRewind;
            clone.SpawnSuppressedByRewindReason = source.SpawnSuppressedByRewindReason;
            clone.SpawnSuppressedByRewindUT = source.SpawnSuppressedByRewindUT;

            return clone;
        }

        internal void CapturePreReFlyAnchorTrajectory(string sessionId)
        {
            PreReFlyAnchorSessionId = sessionId;
            PreReFlyAnchorPoints = Points != null
                ? new List<TrajectoryPoint>(Points)
                : new List<TrajectoryPoint>();
            PreReFlyAnchorOrbitSegments = OrbitSegments != null
                ? new List<OrbitSegment>(OrbitSegments)
                : new List<OrbitSegment>();
            PreReFlyAnchorTrackSections = TrackSections != null
                ? DeepCopyTrackSections(TrackSections)
                : new List<TrackSection>();
        }

        internal bool HasPreReFlyAnchorTrajectory(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return false;
            if (!string.Equals(PreReFlyAnchorSessionId, sessionId, StringComparison.Ordinal))
                return false;
            return (PreReFlyAnchorPoints != null && PreReFlyAnchorPoints.Count > 0)
                || (PreReFlyAnchorTrackSections != null && PreReFlyAnchorTrackSections.Count > 0)
                || (PreReFlyAnchorOrbitSegments != null && PreReFlyAnchorOrbitSegments.Count > 0);
        }

        internal Recording BuildPreReFlyAnchorTrajectoryRecording(string sessionId)
        {
            if (!HasPreReFlyAnchorTrajectory(sessionId))
                return null;

            return new Recording
            {
                RecordingId = RecordingId,
                RecordingFormatVersion = RecordingFormatVersion,
                Points = PreReFlyAnchorPoints != null
                    ? new List<TrajectoryPoint>(PreReFlyAnchorPoints)
                    : new List<TrajectoryPoint>(),
                OrbitSegments = PreReFlyAnchorOrbitSegments != null
                    ? new List<OrbitSegment>(PreReFlyAnchorOrbitSegments)
                    : new List<OrbitSegment>(),
                TrackSections = PreReFlyAnchorTrackSections != null
                    ? DeepCopyTrackSections(PreReFlyAnchorTrackSections)
                    : new List<TrackSection>(),
                VesselPersistentId = VesselPersistentId,
                VesselName = VesselName,
                TreeId = TreeId,
                ChainId = ChainId,
                ChainIndex = ChainIndex,
                ChainBranch = ChainBranch,
            };
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
                copy.absoluteFrames = s.absoluteFrames != null ? new List<TrajectoryPoint>(s.absoluteFrames) : null;
                copy.checkpoints = s.checkpoints != null ? new List<OrbitSegment>(s.checkpoints) : null;
                result.Add(copy);
            }
            return result;
        }

        /// <summary>
        /// Returns true if this recording is "logically loopable" — a launch, atmospheric
        /// descent, surface departure, or docking segment whose loop replay has visual value.
        /// Used by the timeline L button to decide which entries get a loop toggle.
        /// </summary>
        internal static bool IsLoopableRecording(Recording rec)
        {
            if (rec == null || rec.IsDebris)
                return false;

            // Launch from pad/runway
            if (!string.IsNullOrEmpty(rec.LaunchSiteName))
                return true;

            // Prelaunch start (e.g. pad without a named site)
            if (rec.StartSituation == "Prelaunch")
                return true;

            // Atmospheric entry/exit or high-altitude approach
            if (rec.SegmentPhase == "atmo" || rec.SegmentPhase == "approach" || rec.SegmentPhase == "surface")
                return true;

            // Actual docking segments record the other vessel's PID at the boundary.
            // A plain Docked terminal state is too broad: some recordings simply stay
            // docked in orbit and should not get the timeline loop toggle.
            if (rec.DockTargetVesselPid != 0)
                return true;

            return false;
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
        string IPlaybackTrajectory.RecordingId => RecordingId;
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
        RecordingEndpointPhase IPlaybackTrajectory.EndpointPhase => EndpointPhase;
        string IPlaybackTrajectory.EndpointBodyName => EndpointBodyName;

        #endregion
    }
}
