using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;

namespace Parsek
{
    /// <summary>
    /// Manages lightweight ProtoVessel-based map presence for ghost vessels.
    /// Creates tracking station entries, orbit lines, and navigation targeting
    /// for ghost chains with orbital data. Ghost ProtoVessels are transient —
    /// created on chain init, destroyed on chain resolve, stripped from saves.
    ///
    /// The canonical ghost identification check is IsGhostMapVessel(persistentId).
    /// Every FlightGlobals.Vessels iteration and vessel GameEvent handler in Parsek
    /// must check this before processing a vessel.
    /// </summary>
    internal static class GhostMapPresence
    {
        private const string Tag = "GhostMap";
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;
        internal const string TrackingStationGhostSkipSuppressed = "suppressed";
        internal const string TrackingStationGhostSkipAlreadySpawned = "already-spawned";
        internal const string TrackingStationSpawnSkipRewindPending = "rewind-ut-adjustment-pending";
        internal const string TrackingStationSpawnSkipBeforeEnd = "before-recording-end";
        internal const string TrackingStationSpawnSkipIntermediateChainSegment = "intermediate-chain-segment";

        /// <summary>
        /// PID tracking set — the canonical ghost vessel identification.
        /// Every guard in the codebase checks this for O(1) exclusion.
        /// </summary>
        internal static readonly HashSet<uint> ghostMapVesselPids = new HashSet<uint>();

        /// <summary>
        /// Ghost ProtoVessels whose native icon is currently suppressed by
        /// GhostOrbitLinePatch (below atmosphere). DrawMapMarkers checks this
        /// to draw our custom icon at the ghost mesh position instead.
        /// </summary>
        internal static readonly HashSet<uint> ghostsWithSuppressedIcon = new HashSet<uint>();

        /// <summary>
        /// Map from chain PID (OriginalVesselPid) to the ghost Vessel object.
        /// Used for orbit updates, cleanup, and target transfer.
        /// </summary>
        private static readonly Dictionary<uint, Vessel> vesselsByChainPid = new Dictionary<uint, Vessel>();

        /// <summary>
        /// Map from recording index (engine ghost key) to the ghost Vessel object.
        /// Used for timeline playback ghosts that are not part of a ghost chain.
        /// </summary>
        private static readonly Dictionary<int, Vessel> vesselsByRecordingIndex = new Dictionary<int, Vessel>();

        /// <summary>
        /// Reverse lookup: ghost vessel PID → recording index.
        /// Kept in sync with vesselsByRecordingIndex to make FindRecordingIndexByVesselPid O(1).
        /// </summary>
        private static readonly Dictionary<uint, int> vesselPidToRecordingIndex = new Dictionary<uint, int>();

        /// <summary>
        /// Orbit segment time bounds per ghost vessel PID. Used by GhostOrbitArcPatch
        /// to clip the orbit line to only the visible arc (between segment startUT and endUT).
        /// Only populated for segment-based ghosts — terminal-orbit ghosts render the full ellipse.
        /// </summary>
        internal static readonly Dictionary<uint, (double startUT, double endUT)> ghostOrbitBounds
            = new Dictionary<uint, (double, double)>();

        /// <summary>
        /// O(1) check used by all guard code throughout the codebase.
        /// Returns true if the given persistentId belongs to a ghost map ProtoVessel.
        /// </summary>
        internal static bool IsGhostMapVessel(uint persistentId)
        {
            return ghostMapVesselPids.Contains(persistentId);
        }

        /// <summary>
        /// O(1) check: is this ghost's native icon currently suppressed (below atmosphere)?
        /// When true, DrawMapMarkers draws our custom icon at the ghost mesh position instead.
        /// </summary>
        internal static bool IsIconSuppressed(uint persistentId)
        {
            return ghostsWithSuppressedIcon.Contains(persistentId);
        }

        // ------------------------------------------------------------------
        // Pure data layer (unchanged from original)
        // ------------------------------------------------------------------

        /// <summary>
        /// Pure: does this recording have orbital data suitable for map presence?
        /// True if terminal orbit body is set and SMA > 0.
        /// </summary>
        internal static bool HasOrbitData(Recording rec)
        {
            if (rec == null)
            {
                ParsekLog.Verbose(Tag, "HasOrbitData(Recording): null recording — returning false");
                return false;
            }

            bool hasOrbit = !string.IsNullOrEmpty(rec.TerminalOrbitBody)
                && rec.TerminalOrbitSemiMajorAxis > 0;

            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "HasOrbitData(Recording): rec={0} body={1} sma={2} result={3}",
                    rec.RecordingId ?? "(null)",
                    rec.TerminalOrbitBody ?? "(null)",
                    rec.TerminalOrbitSemiMajorAxis,
                    hasOrbit));

            return hasOrbit;
        }

        /// <summary>
        /// Pure: does this trajectory have orbital data suitable for map presence?
        /// Overload accepting IPlaybackTrajectory for engine-side use.
        /// </summary>
        internal static bool HasOrbitData(IPlaybackTrajectory traj)
        {
            if (traj == null)
            {
                ParsekLog.Verbose(Tag, "HasOrbitData(IPlaybackTrajectory): null trajectory — returning false");
                return false;
            }

            bool hasOrbit = !string.IsNullOrEmpty(traj.TerminalOrbitBody)
                && traj.TerminalOrbitSemiMajorAxis > 0;

            if (hasOrbit)
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "HasOrbitData(IPlaybackTrajectory): body={0} sma={1} result=True",
                        traj.TerminalOrbitBody,
                        traj.TerminalOrbitSemiMajorAxis));

            return hasOrbit;
        }

        /// <summary>
        /// Pure: compute display info for tracking station / map view.
        /// Returns vessel name, status string, and spawn UT for the chain.
        /// </summary>
        internal static (string name, string status, double spawnUT)
            ComputeGhostDisplayInfo(GhostChain chain, string vesselName)
        {
            string safeName = vesselName ?? "(unnamed)";

            if (chain == null)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "ComputeGhostDisplayInfo: null chain for vessel '{0}' — returning defaults",
                        safeName));
                return (safeName, "Ghost — no chain data", 0);
            }

            if (chain.IsTerminated)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "ComputeGhostDisplayInfo: terminated chain for vessel '{0}' pid={1}",
                        safeName, chain.OriginalVesselPid));
                return (safeName, "Ghost — terminated", chain.SpawnUT);
            }

            if (chain.SpawnBlocked)
            {
                string blockedStatus = string.Format(ic,
                    "Ghost — spawn blocked (since UT={0:F1})",
                    chain.BlockedSinceUT);
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "ComputeGhostDisplayInfo: spawn blocked for vessel '{0}' pid={1} since UT={2:F1}",
                        safeName, chain.OriginalVesselPid, chain.BlockedSinceUT));
                return (safeName, blockedStatus, chain.SpawnUT);
            }

            string activeStatus = string.Format(ic,
                "Ghost — spawns at UT={0:F1}",
                chain.SpawnUT);

            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "ComputeGhostDisplayInfo: active chain for vessel '{0}' pid={1} spawnUT={2:F1} tip={3}",
                    safeName, chain.OriginalVesselPid, chain.SpawnUT,
                    chain.TipRecordingId ?? "(null)"));

            return (safeName, activeStatus, chain.SpawnUT);
        }

        // ------------------------------------------------------------------
        // ProtoVessel lifecycle
        // ------------------------------------------------------------------

        /// <summary>
        /// Create a ghost ProtoVessel for a chain with orbital data.
        /// Gives the ghost tracking station entry, orbit line, and targeting.
        /// Returns the Vessel, or null if no orbit data or creation failed.
        /// </summary>
        internal static Vessel CreateGhostVessel(
            GhostChain chain, IPlaybackTrajectory traj)
        {
            if (chain == null || traj == null)
            {
                ParsekLog.Warn(Tag, "CreateGhostVessel: null chain or trajectory");
                return null;
            }

            if (!HasOrbitData(traj))
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "CreateGhostVessel: no orbit data for chain pid={0} — skipping",
                        chain.OriginalVesselPid));
                return null;
            }

            // Already have a ghost vessel for this chain?
            if (vesselsByChainPid.ContainsKey(chain.OriginalVesselPid))
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "CreateGhostVessel: ghost already exists for chain pid={0}",
                        chain.OriginalVesselPid));
                return vesselsByChainPid[chain.OriginalVesselPid];
            }

            string logContext = string.Format(ic, "chain pid={0}", chain.OriginalVesselPid);
            Vessel vessel = BuildAndLoadGhostProtoVessel(traj, logContext);
            if (vessel != null)
                vesselsByChainPid[chain.OriginalVesselPid] = vessel;

            return vessel;
        }

        /// <summary>
        /// Update orbit when ghost traverses an OrbitSegment boundary.
        /// Changes the ProtoVessel's orbit and reference body if needed.
        /// </summary>
        internal static void UpdateGhostOrbit(uint chainPid, OrbitSegment segment)
        {
            if (!vesselsByChainPid.TryGetValue(chainPid, out Vessel vessel))
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic, "UpdateGhostOrbit: no ghost vessel for chain pid={0}", chainPid));
                return;
            }
            ApplyOrbitToVessel(vessel, segment, string.Format(ic, "chain pid={0}", chainPid));
        }

        /// <summary>
        /// Remove a single ghost vessel. Captures target state before Die().
        /// Returns true if the ghost was the current navigation target (caller
        /// should set the newly spawned real vessel as target).
        /// </summary>
        internal static bool RemoveGhostVessel(uint chainPid, string reason)
        {
            if (!vesselsByChainPid.TryGetValue(chainPid, out Vessel vessel))
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "RemoveGhostVessel: no ghost vessel for chain pid={0} reason={1}",
                        chainPid, reason));
                return false;
            }

            // Capture target state BEFORE Die() clears it
            bool wasTarget = FlightGlobals.fetch != null
                && FlightGlobals.fetch.VesselTarget != null
                && FlightGlobals.fetch.VesselTarget.GetVessel() == vessel;

            uint ghostPid = vessel.persistentId;

            try
            {
                vessel.Die();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "RemoveGhostVessel: Die() threw for chain pid={0}: {1}",
                        chainPid, ex.Message));
            }

            ghostMapVesselPids.Remove(ghostPid);
            ghostOrbitBounds.Remove(ghostPid);
            vesselsByChainPid.Remove(chainPid);

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Removed ghost vessel chainPid={0} ghostPid={1} reason={2} wasTarget={3}",
                    chainPid, ghostPid, reason, wasTarget));

            return wasTarget;
        }

        /// <summary>
        /// Remove all ghost vessels (rewind or scene cleanup).
        /// </summary>
        internal static void RemoveAllGhostVessels(string reason)
        {
            int chainCount = vesselsByChainPid.Count;
            int indexCount = vesselsByRecordingIndex.Count;
            if (chainCount == 0 && indexCount == 0)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "RemoveAllGhostVessels: no ghost vessels to remove (reason={0})",
                        reason));
                return;
            }

            // Collect all vessels to destroy (chain + recording index)
            var vessels = new List<Vessel>(chainCount + indexCount);
            vessels.AddRange(vesselsByChainPid.Values);
            vessels.AddRange(vesselsByRecordingIndex.Values);

            foreach (var vessel in vessels)
            {
                try
                {
                    vessel.Die();
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag,
                        string.Format(ic,
                            "RemoveAllGhostVessels: Die() threw for '{0}': {1}",
                            vessel.vesselName, ex.Message));
                }
            }

            ghostMapVesselPids.Clear();
            ghostsWithSuppressedIcon.Clear();
            ghostOrbitBounds.Clear();
            vesselsByChainPid.Clear();
            vesselsByRecordingIndex.Clear();
            vesselPidToRecordingIndex.Clear();

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Removed all {0} ghost vessel(s) reason={1} (chain={2} index={3})",
                    chainCount + indexCount, reason, chainCount, indexCount));
        }

        // ------------------------------------------------------------------
        // Recording-index-based ghost map (for timeline playback ghosts)
        // ------------------------------------------------------------------

        /// <summary>
        /// Create a ghost map ProtoVessel for a timeline playback ghost.
        /// Called when the engine spawns a ghost (OnGhostCreated).
        /// </summary>
        internal static Vessel CreateGhostVesselForRecording(int recordingIndex, IPlaybackTrajectory traj)
        {
            if (traj == null || !HasOrbitData(traj))
                return null;

            // Already exists?
            if (vesselsByRecordingIndex.ContainsKey(recordingIndex))
                return vesselsByRecordingIndex[recordingIndex];

            string logContext = string.Format(ic, "recording index={0}", recordingIndex);
            Vessel vessel = BuildAndLoadGhostProtoVessel(traj, logContext);
            if (vessel != null)
            {
                vesselsByRecordingIndex[recordingIndex] = vessel;
                vesselPidToRecordingIndex[vessel.persistentId] = recordingIndex;
            }

            return vessel;
        }

        /// <summary>
        /// Create a ghost map ProtoVessel for a recording that has orbit segments but
        /// no terminal orbit data (intermediate chain segments). Uses the provided
        /// OrbitSegment for the initial orbit. Called from CheckPendingMapVessels when
        /// the ghost enters its first orbital segment.
        /// </summary>
        internal static Vessel CreateGhostVesselFromSegment(
            int recordingIndex, IPlaybackTrajectory traj, OrbitSegment segment)
        {
            if (traj == null) return null;

            if (vesselsByRecordingIndex.ContainsKey(recordingIndex))
                return vesselsByRecordingIndex[recordingIndex];

            string logContext = string.Format(ic, "recording index={0} (from segment)", recordingIndex);
            Vessel vessel = BuildAndLoadGhostProtoVessel(traj, segment, logContext);
            if (vessel != null)
            {
                vesselsByRecordingIndex[recordingIndex] = vessel;
                vesselPidToRecordingIndex[vessel.persistentId] = recordingIndex;
                ghostOrbitBounds[vessel.persistentId] = (segment.startUT, segment.endUT);
            }

            return vessel;
        }

        /// <summary>
        /// Remove a ghost map ProtoVessel for a timeline playback ghost.
        /// Called when the engine destroys a ghost (OnGhostDestroyed).
        /// </summary>
        internal static void RemoveGhostVesselForRecording(int recordingIndex, string reason)
        {
            if (!vesselsByRecordingIndex.TryGetValue(recordingIndex, out Vessel vessel))
                return;

            uint ghostPid = vessel.persistentId;

            try { vessel.Die(); }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "RemoveGhostVesselForRecording: Die() threw for index={0}: {1}",
                        recordingIndex, ex.Message));
            }

            ghostMapVesselPids.Remove(ghostPid);
            ghostOrbitBounds.Remove(ghostPid);
            vesselPidToRecordingIndex.Remove(ghostPid);
            vesselsByRecordingIndex.Remove(recordingIndex);

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Removed ghost map vessel for recording #{0} ghostPid={1} reason={2}",
                    recordingIndex, ghostPid, reason));
        }

        /// <summary>
        /// Remove all ghost map presence for a given recording index: both the
        /// recording-index-based ProtoVessel AND any chain-based ProtoVessel for
        /// the same vessel PID. Centralizes the dual-dict cleanup so callers
        /// don't need to reach into RecordingStore to find the chain PID.
        /// </summary>
        internal static void RemoveAllGhostPresenceForIndex(int recordingIndex, uint vesselPersistentId, string reason)
        {
            // 1. Remove recording-index-based ghost (if any)
            RemoveGhostVesselForRecording(recordingIndex, reason);

            // 2. Remove chain-based ghost (if any) — keyed by vessel PID
            if (vesselPersistentId != 0 && vesselsByChainPid.ContainsKey(vesselPersistentId))
                RemoveGhostVessel(vesselPersistentId, reason);
        }

        /// <summary>
        /// Cached tracking-station-suppressed recording IDs from the last lifecycle tick.
        /// Reused by ParsekTrackingStation.OnGUI to avoid recomputing.
        /// </summary>
        internal static HashSet<string> CachedTrackingStationSuppressedIds { get; private set; }
            = new HashSet<string>();

        /// <summary>
        /// Per-frame lifecycle for tracking station ghost ProtoVessels.
        /// Creates ghosts when UT enters a map-visible orbit range (handles time warp),
        /// carries them across brief same-body gaps, and removes them when the visible
        /// orbit range is truly exhausted.
        /// Called periodically from ParsekTrackingStation.Update.
        /// In the flight scene, this lifecycle is handled by ParsekPlaybackPolicy.CheckPendingMapVessels;
        /// in the tracking station, this method provides the equivalent.
        /// </summary>
        internal static void UpdateTrackingStationGhostLifecycle()
        {
            double currentUT = Planetarium.GetUniversalTime();
            var committed = RecordingStore.CommittedRecordings;
            bool hasCommittedRecordings = committed != null && committed.Count > 0;

            // Real-vessel materialization is part of lifecycle correctness, not ghost
            // visibility. Keep the handoff active even when the TS ghost toggle is off.
            if (hasCommittedRecordings)
                TryRunTrackingStationSpawnHandoffs(committed, currentUT);

            // #388: respect the user's tracking-station ghost visibility toggle.
            // ParsekTrackingStation.Update already handles the off-flip transition
            // (calls RemoveAllGhostVessels); here we simply skip creation so the
            // list stays empty while the flag is off. An empty-committed cache is
            // still published so ParsekTrackingStation.OnGUI guards still work.
            // Reads through the persistence store so the early-scene-load case
            // (ParsekSettings.Current still null) gets the correct user choice
            // from settings.cfg, not the pre-#388 default.
            if (!ParsekSettingsPersistence.EffectiveShowGhostsInTrackingStation())
            {
                CachedTrackingStationSuppressedIds = new HashSet<string>();
                ParsekLog.VerboseRateLimited(Tag,
                    "ts-lifecycle-disabled",
                    "UpdateTrackingStationGhostLifecycle: showGhostsInTrackingStation=false — skip",
                    2.0);
                return;
            }

            var suppressed = hasCommittedRecordings
                ? FindTrackingStationSuppressedRecordingIds(committed, currentUT)
                : new HashSet<string>();
            CachedTrackingStationSuppressedIds = suppressed;

            RefreshTrackingStationGhosts(committed, suppressed, currentUT);

            if (!hasCommittedRecordings)
            {
                return;
            }

            // --- Phase 2: create ghosts for recordings that just entered visible orbit range ---
            for (int i = 0; i < committed.Count; i++)
            {
                // Skip recordings that already have a ghost
                if (vesselsByRecordingIndex.ContainsKey(i)) continue;

                var rec = committed[i];
                bool isSuppressed = suppressed.Contains(rec.RecordingId);
                var (shouldCreate, _) = ShouldCreateTrackingStationGhost(rec, isSuppressed, currentUT);
                if (!shouldCreate) continue;

                Vessel v = null;
                if (HasOrbitData(rec))
                {
                    v = CreateGhostVesselForRecording(i, rec);
                }
                else if (rec.HasOrbitSegments)
                {
                    OrbitSegment? seg = TrajectoryMath.FindOrbitSegmentForMapDisplay(rec.OrbitSegments, currentUT);
                    if (seg.HasValue)
                        v = CreateGhostVesselFromSegment(i, rec, seg.Value);
                }

                if (v != null)
                {
                    // Ensure orbit renderer exists (MapView.fetch should be available by now)
                    EnsureGhostOrbitRenderers();
                    ParsekLog.Info(Tag,
                        string.Format(ic,
                            "Deferred ghost creation for #{0} \"{1}\" — UT {2:F1} entered visible orbit range",
                            i, rec.VesselName ?? "(null)", currentUT));
                }
            }
        }

        private static void RefreshTrackingStationGhosts(
            IReadOnlyList<Recording> committed,
            HashSet<string> suppressed,
            double currentUT)
        {
            if (vesselsByRecordingIndex.Count == 0)
                return;

            List<(int idx, string reason)> toRemove = null;
            foreach (var kvp in vesselsByRecordingIndex)
            {
                int idx = kvp.Key;
                if (committed == null)
                {
                    if (toRemove == null) toRemove = new List<(int, string)>();
                    toRemove.Add((idx, "tracking-station-expired"));
                    continue;
                }

                if (idx < 0 || idx >= committed.Count)
                {
                    if (toRemove == null) toRemove = new List<(int, string)>();
                    toRemove.Add((idx, "tracking-station-expired"));
                    continue;
                }

                var rec = committed[idx];
                bool isSuppressed = rec != null
                    && !string.IsNullOrEmpty(rec.RecordingId)
                    && suppressed != null
                    && suppressed.Contains(rec.RecordingId);
                uint pid = kvp.Value.persistentId;
                bool hasOrbitBounds = ghostOrbitBounds.TryGetValue(pid, out var bounds);

                string removeReason = GetTrackingStationGhostRemovalReason(
                    rec, isSuppressed, hasOrbitBounds, currentUT);
                if (removeReason != null)
                {
                    if (toRemove == null) toRemove = new List<(int, string)>();
                    toRemove.Add((idx, removeReason));
                    continue;
                }

                if (!hasOrbitBounds)
                    continue;

                OrbitSegment? seg = TrajectoryMath.FindOrbitSegmentForMapDisplay(rec.OrbitSegments, currentUT);
                if (bounds.startUT != seg.Value.startUT || bounds.endUT != seg.Value.endUT)
                    UpdateGhostOrbitForRecording(idx, seg.Value);
            }

            if (toRemove == null)
                return;

            for (int i = 0; i < toRemove.Count; i++)
            {
                int idx = toRemove[i].idx;
                string reason = toRemove[i].reason;
                RemoveGhostVesselForRecording(idx, reason);
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Removed tracking-station ghost #{0} — UT {1:F1} reason={2}",
                        idx, currentUT, reason));
            }
        }

        private static void TryRunTrackingStationSpawnHandoffs(
            IReadOnlyList<Recording> committed,
            double currentUT)
        {
            if (committed == null || committed.Count == 0)
                return;

            var chains = GhostChainWalker.ComputeAllGhostChains(RecordingStore.CommittedTrees, currentUT);
            List<int> eligibleIndices = null;
            for (int i = 0; i < committed.Count; i++)
            {
                var (needsSpawn, _) = ShouldSpawnAtTrackingStationEnd(committed[i], currentUT, chains);
                if (!needsSpawn)
                    continue;

                if (eligibleIndices == null)
                    eligibleIndices = new List<int>();
                eligibleIndices.Add(i);
            }

            if (eligibleIndices == null || eligibleIndices.Count == 0)
                return;

            GhostPlaybackLogic.InvalidateVesselCache();
            uint sceneEntryActiveVesselPid = RecordingStore.SceneEntryActiveVesselPid;

            for (int i = 0; i < eligibleIndices.Count; i++)
            {
                int index = eligibleIndices[i];
                Recording rec = committed[index];
                bool realVesselExists = rec.VesselPersistentId != 0
                    && GhostPlaybackLogic.RealVesselExists(rec.VesselPersistentId);
                bool bypassDedup = ShouldBypassTrackingStationRealVesselDedup(rec, sceneEntryActiveVesselPid);

                if (realVesselExists && !bypassDedup)
                {
                    rec.VesselSpawned = true;
                    rec.SpawnedVesselPersistentId = rec.VesselPersistentId;
                    RemoveAllGhostPresenceForIndex(
                        index,
                        rec.VesselPersistentId,
                        "tracking-station-existing-real-vessel");
                    ParsekLog.Info(Tag,
                        string.Format(ic,
                            "Tracking-station handoff skipped duplicate spawn for #{0} \"{1}\" — real vessel pid={2} already exists",
                            index,
                            rec.VesselName ?? "(null)",
                            rec.VesselPersistentId));
                    continue;
                }

                bool preserveIdentity = ShouldPreserveIdentityForTrackingStationSpawn(
                    chains,
                    rec,
                    realVesselExists);
                VesselSpawner.SpawnOrRecoverIfTooClose(rec, index, preserveIdentity);
                if (!rec.VesselSpawned)
                    continue;

                RemoveAllGhostPresenceForIndex(
                    index,
                    rec.VesselPersistentId,
                    "tracking-station-spawn-handoff");

                if (rec.SpawnedVesselPersistentId != 0)
                {
                    GhostPlaybackLogic.InvalidateVesselCache();
                    ParsekLog.Info(Tag,
                        string.Format(ic,
                            "Tracking-station handoff spawned #{0} \"{1}\" pid={2} preserveIdentity={3}",
                            index,
                            rec.VesselName ?? "(null)",
                            rec.SpawnedVesselPersistentId,
                            preserveIdentity));
                }
                else if (rec.SpawnAbandoned)
                {
                    ParsekLog.Info(Tag,
                        string.Format(ic,
                            "Tracking-station handoff resolved #{0} \"{1}\" without spawning a vessel (abandoned)",
                            index,
                            rec.VesselName ?? "(null)"));
                }
            }
        }

        /// <summary>
        /// Update orbit for a recording-index ghost when the ghost traverses orbit segments.
        /// </summary>
        internal static void UpdateGhostOrbitForRecording(int recordingIndex, OrbitSegment segment)
        {
            if (!vesselsByRecordingIndex.TryGetValue(recordingIndex, out Vessel vessel))
                return;
            ApplyOrbitToVessel(vessel, segment, string.Format(ic, "recording #{0}", recordingIndex));
        }

        /// <summary>
        /// Create a ghost map ProtoVessel from interpolated trajectory state vectors.
        /// Used for physics-only suborbital recordings that have no orbit segments.
        /// Constructs a Keplerian orbit from position + velocity at the given UT.
        /// </summary>
        internal static Vessel CreateGhostVesselFromStateVectors(
            int recordingIndex, IPlaybackTrajectory traj,
            TrajectoryPoint point, double ut)
        {
            if (traj == null) return null;

            if (vesselsByRecordingIndex.ContainsKey(recordingIndex))
                return vesselsByRecordingIndex[recordingIndex];

            CelestialBody body = FindBodyByName(point.bodyName);
            if (body == null)
            {
                ParsekLog.Error(Tag,
                    string.Format(ic,
                        "CreateGhostVesselFromStateVectors: body '{0}' not found for recording #{1}",
                        point.bodyName, recordingIndex));
                return null;
            }

            Vector3d worldPos = body.GetWorldSurfacePosition(point.latitude, point.longitude, point.altitude);
            Vector3d vel = new Vector3d(point.velocity.x, point.velocity.y, point.velocity.z);

            Orbit orbit = new Orbit();
            orbit.UpdateFromStateVectors(worldPos, vel, body, ut);

            string logContext = string.Format(ic,
                "recording #{0} (state vectors alt={1:F0} spd={2:F1})",
                recordingIndex, point.altitude, point.velocity.magnitude);
            Vessel vessel = BuildAndLoadGhostProtoVesselCore(traj, orbit, body, logContext);
            if (vessel != null)
            {
                vesselsByRecordingIndex[recordingIndex] = vessel;
                vesselPidToRecordingIndex[vessel.persistentId] = recordingIndex;
            }

            return vessel;
        }

        /// <summary>
        /// Update a ghost map ProtoVessel's orbit from interpolated trajectory state vectors.
        /// Used for per-frame orbit updates of physics-only suborbital ghosts.
        /// Handles SOI transitions (body change + orbit renderer rebuild).
        /// </summary>
        internal static void UpdateGhostOrbitFromStateVectors(
            int recordingIndex, TrajectoryPoint point, double ut)
        {
            if (!vesselsByRecordingIndex.TryGetValue(recordingIndex, out Vessel vessel))
                return;

            if (vessel.orbitDriver == null)
            {
                ParsekLog.Error(Tag,
                    string.Format(ic,
                        "UpdateGhostOrbitFromStateVectors: no OrbitDriver for recording #{0}",
                        recordingIndex));
                return;
            }

            CelestialBody body = FindBodyByName(point.bodyName);
            if (body == null)
            {
                ParsekLog.Error(Tag,
                    string.Format(ic,
                        "UpdateGhostOrbitFromStateVectors: body '{0}' not found for recording #{1}",
                        point.bodyName, recordingIndex));
                return;
            }

            // SOI transition handling (same pattern as ApplyOrbitToVessel)
            bool soiChanged = vessel.orbitDriver.celestialBody != body;
            if (soiChanged)
            {
                vessel.orbitDriver.celestialBody = body;
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "SOI change for state-vector ghost #{0} — new body={1}",
                        recordingIndex, body.name));
            }

            Vector3d worldPos = body.GetWorldSurfacePosition(point.latitude, point.longitude, point.altitude);
            Vector3d vel = new Vector3d(point.velocity.x, point.velocity.y, point.velocity.z);

            vessel.orbitDriver.orbit.UpdateFromStateVectors(worldPos, vel, body, ut);
            vessel.orbitDriver.updateFromParameters();

            if (soiChanged && vessel.orbitRenderer != null)
            {
                vessel.orbitRenderer.drawMode = OrbitRendererBase.DrawMode.REDRAW_AND_RECALCULATE;
                vessel.orbitRenderer.enabled = false;
                vessel.orbitRenderer.enabled = true;
            }
        }

        /// <summary>
        /// Shared: apply an OrbitSegment's Keplerian elements to a ghost vessel's OrbitDriver.
        /// Handles body resolution, orbit construction, SOI transitions, and logging.
        /// </summary>
        private static void ApplyOrbitToVessel(Vessel vessel, OrbitSegment segment, string logContext)
        {
            if (vessel.orbitDriver == null)
            {
                ParsekLog.Error(Tag,
                    string.Format(ic, "ApplyOrbitToVessel: no OrbitDriver for {0}", logContext));
                return;
            }

            CelestialBody body = FindBodyByName(segment.bodyName);
            if (body == null)
            {
                ParsekLog.Error(Tag,
                    string.Format(ic, "ApplyOrbitToVessel: body '{0}' not found for {1}",
                        segment.bodyName, logContext));
                return;
            }

            // SOI transition: update celestialBody BEFORE SetOrbit so that
            // orbitDriver and orbit.referenceBody are consistent when
            // updateFromParameters recalculates the orbit line (#189).
            bool soiChanged = vessel.orbitDriver.celestialBody != body;
            if (soiChanged)
            {
                vessel.orbitDriver.celestialBody = body;
                ParsekLog.Info(Tag,
                    string.Format(ic, "SOI change for {0} — new body={1}", logContext, body.name));
            }

            // Direct element assignment via SetOrbit — bypasses the lossy
            // state-vector roundtrip in UpdateFromOrbitAtUT (#172).
            Orbit orb = vessel.orbitDriver.orbit;
            orb.SetOrbit(
                segment.inclination,
                segment.eccentricity,
                segment.semiMajorAxis,
                segment.longitudeOfAscendingNode,
                segment.argumentOfPeriapsis,
                segment.meanAnomalyAtEpoch,
                segment.epoch,
                body);

            vessel.orbitDriver.updateFromParameters();

            // Store orbit segment time bounds for arc clipping (GhostOrbitArcPatch)
            ghostOrbitBounds[vessel.persistentId] = (segment.startUT, segment.endUT);

            // After SOI change, force the orbit renderer to recalculate for the new body.
            // Without this, the orbit line stays clipped to the old body's SOI radius.
            // DrawOrbit is protected, so toggle the renderer off/on to force a full rebuild.
            if (soiChanged && vessel.orbitRenderer != null)
            {
                vessel.orbitRenderer.drawMode = OrbitRendererBase.DrawMode.REDRAW_AND_RECALCULATE;
                vessel.orbitRenderer.enabled = false;
                vessel.orbitRenderer.enabled = true;
                ParsekLog.Verbose(Tag,
                    string.Format(ic, "Forced orbit renderer redraw for {0} after SOI change", logContext));
            }

            // Diagnostic logging: orbit elements + hyperbola extent
            Orbit drv = vessel.orbitDriver.orbit;
            double periapsis = drv.PeR;
            double semiMinorAxis = drv.semiMinorAxis;
            // For hyperbolic: max eccentric anomaly = acos(-1/e)
            double maxE = drv.eccentricity >= 1.0
                ? System.Math.Acos(-1.0 / drv.eccentricity) : System.Math.PI;
            // Position at max eccentric anomaly = furthest point
            Vector3d farPos = drv.eccentricity >= 1.0
                ? drv.getPositionFromEccAnomaly(maxE * 0.99) : Vector3d.zero; // 0.99 to avoid singularity
            double farDist = farPos.magnitude;

            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "Orbit updated for {0} body={1} sma={2:F0} ecc={3:F6} " +
                    "periapsis={4:F0} semiMinor={5:F0} maxE={6:F2}rad farDist={7:F0}m " +
                    "rendererEnabled={8} rendererDrawMode={9}",
                    logContext, body.name, segment.semiMajorAxis,
                    drv.eccentricity, periapsis, semiMinorAxis, maxE, farDist,
                    vessel.orbitRenderer?.enabled, vessel.orbitRenderer?.drawMode));
        }

        /// <summary>
        /// Strip ghost ProtoVessels from flightState before save.
        /// Ghost vessels are transient — reconstructed from recording data on load.
        /// Called from ParsekScenario.OnSave.
        /// </summary>
        internal static int StripFromSave(FlightState flightState)
        {
            if (flightState == null || ghostMapVesselPids.Count == 0)
                return 0;

            int stripped = flightState.protoVessels.RemoveAll(
                pv => ghostMapVesselPids.Contains(pv.persistentId));

            if (stripped > 0)
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Stripped {0} ghost ProtoVessel(s) from save", stripped));

            return stripped;
        }

        /// <summary>
        /// Pure: find recording IDs that are superseded by a later recording in the same chain.
        /// A recording is superseded if another recording's ParentRecordingId points to it.
        /// Chain-tip recordings are NOT in the returned set.
        /// </summary>
        internal static HashSet<string> FindSupersededRecordingIds(IReadOnlyList<Recording> recordings)
        {
            var superseded = new HashSet<string>();
            if (recordings == null) return superseded;
            for (int i = 0; i < recordings.Count; i++)
            {
                string parentId = recordings[i].ParentRecordingId;
                if (!string.IsNullOrEmpty(parentId))
                    superseded.Add(parentId);
            }
            return superseded;
        }

        internal static (bool needsSpawn, string reason) ShouldSpawnAtTrackingStationEnd(
            Recording rec,
            double currentUT)
        {
            return ShouldSpawnAtTrackingStationEnd(rec, currentUT, chains: null);
        }

        internal static (bool needsSpawn, string reason) ShouldSpawnAtTrackingStationEnd(
            Recording rec,
            double currentUT,
            Dictionary<uint, GhostChain> chains)
        {
            if (rec == null)
                return (false, "null");

            if (RecordingStore.RewindUTAdjustmentPending)
                return (false, TrackingStationSpawnSkipRewindPending);

            if (currentUT < rec.EndUT)
                return (false, TrackingStationSpawnSkipBeforeEnd);

            bool isChainLooping = !string.IsNullOrEmpty(rec.ChainId)
                && RecordingStore.IsChainLooping(rec.ChainId);
            var spawnResult = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false, isChainLooping);
            if (!spawnResult.needsSpawn)
                return spawnResult;

            if (RecordingStore.IsChainMidSegment(rec))
                return (false, TrackingStationSpawnSkipIntermediateChainSegment);
            var chainSuppressed = GhostPlaybackLogic.ShouldSuppressSpawnForChain(chains, rec);
            if (chainSuppressed.suppressed)
                return (false, chainSuppressed.reason);

            return spawnResult;
        }

        internal static bool ShouldBypassTrackingStationRealVesselDedup(
            Recording rec,
            uint sceneEntryActiveVesselPid)
        {
            return rec != null
                && rec.VesselPersistentId != 0
                && rec.VesselPersistentId == sceneEntryActiveVesselPid;
        }

        internal static bool ShouldPreserveIdentityForTrackingStationSpawn(
            Dictionary<uint, GhostChain> chains,
            Recording rec,
            bool realVesselExists)
        {
            if (realVesselExists || rec == null || rec.VesselPersistentId == 0)
                return false;

            GhostChain chain = GhostChainWalker.FindChainForVessel(chains, rec.VesselPersistentId);
            return chain != null
                && !chain.IsTerminated
                && chain.TipRecordingId == rec.RecordingId;
        }

        /// <summary>
        /// Tracking Station visibility suppression is time-aware: a recording is hidden only
        /// after one of its child recordings has actually started by the current UT. This keeps
        /// the current atmospheric continuation visible even when a later future leg already
        /// exists in the committed chain.
        /// </summary>
        internal static HashSet<string> FindTrackingStationSuppressedRecordingIds(
            IReadOnlyList<Recording> recordings, double currentUT)
        {
            var suppressed = new HashSet<string>();
            if (recordings == null)
                return suppressed;

            for (int i = 0; i < recordings.Count; i++)
            {
                Recording child = recordings[i];
                string parentId = child?.ParentRecordingId;
                if (string.IsNullOrEmpty(parentId))
                    continue;

                if (HasTrackingStationChildStarted(child, currentUT))
                    suppressed.Add(parentId);
            }

            return suppressed;
        }

        /// <summary>
        /// Pure: should a tracking station ghost ProtoVessel be created for this recording?
        /// Only recordings that are not currently hidden by an already-started child and that
        /// have orbital presence at the current UT qualify.
        /// Returns a reason string for logging when the recording is skipped.
        /// </summary>
        internal static (bool shouldCreate, string skipReason) ShouldCreateTrackingStationGhost(
            Recording rec, bool isSuppressed, double currentUT)
        {
            if (rec == null) return (false, "null");
            if (rec.IsDebris) return (false, "debris");
            if (isSuppressed) return (false, TrackingStationGhostSkipSuppressed);
            if (rec.VesselSpawned || rec.SpawnedVesselPersistentId != 0)
                return (false, TrackingStationGhostSkipAlreadySpawned);

            // Only recordings that are currently active for Tracking Station visibility and
            // have orbital presence get ghosts.
            // Orbiting/Docked = in orbit. Null = still active or unfinished (show orbit if available).
            // All other states (Landed, Destroyed, SubOrbital, Recovered, Boarded) = no orbit ghost.
            var terminal = rec.TerminalStateValue;
            if (terminal.HasValue
                && terminal.Value != TerminalState.Orbiting
                && terminal.Value != TerminalState.Docked)
                return (false, "terminal-" + terminal.Value);

            // Terminal orbit data (stable orbit) → always show
            if (HasOrbitData(rec))
                return (true, null);

            // Orbit segments: find the one matching currentUT
            if (rec.HasOrbitSegments)
            {
                OrbitSegment? seg = TrajectoryMath.FindOrbitSegmentForMapDisplay(rec.OrbitSegments, currentUT);
                if (seg.HasValue)
                    return (true, null);
                return (false, "no-current-segment");
            }

            return (false, "no-orbit-data");
        }

        /// <summary>
        /// Create ghost ProtoVessels for committed recordings suitable for tracking station display.
        /// Chain-aware: only creates ghosts for recordings that are not currently hidden by an
        /// already-started child recording. Skips debris, non-orbital terminal states, and
        /// recordings without orbital data at the current UT.
        /// </summary>
        internal static int CreateGhostVesselsFromCommittedRecordings()
        {
            // #388: respect the user's tracking-station ghost visibility toggle.
            // This path is called from SpaceTracking.Awake prefix where
            // ParsekSettings.Current may still be null — read through the
            // persistence store so the user's recorded preference (from
            // settings.cfg) wins over the pre-#388 default.
            if (!ParsekSettingsPersistence.EffectiveShowGhostsInTrackingStation())
            {
                CachedTrackingStationSuppressedIds = new HashSet<string>();
                int commCount = RecordingStore.CommittedRecordings?.Count ?? 0;
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "CreateGhostVesselsFromCommittedRecordings: " +
                        "showGhostsInTrackingStation=false — skipping {0} committed recording(s)",
                        commCount));
                return 0;
            }

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || committed.Count == 0)
            {
                CachedTrackingStationSuppressedIds = new HashSet<string>();
                return 0;
            }

            // Step 1: identify recordings currently hidden by a started child.
            double currentUT = Planetarium.GetUniversalTime();
            var suppressed = FindTrackingStationSuppressedRecordingIds(committed, currentUT);
            CachedTrackingStationSuppressedIds = suppressed;

            int created = 0, skippedDebris = 0, skippedSuppressed = 0, skippedSpawned = 0;
            int skippedTerminal = 0, skippedNoOrbit = 0;

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                bool isSuppressed = suppressed.Contains(rec.RecordingId);

                var (shouldCreate, skipReason) = ShouldCreateTrackingStationGhost(rec, isSuppressed, currentUT);
                if (!shouldCreate)
                {
                    if (skipReason == "debris") skippedDebris++;
                    else if (skipReason == TrackingStationGhostSkipSuppressed) skippedSuppressed++;
                    else if (skipReason == TrackingStationGhostSkipAlreadySpawned) skippedSpawned++;
                    else if (skipReason != null && skipReason.StartsWith("terminal")) skippedTerminal++;
                    else skippedNoOrbit++;
                    continue;
                }

                // Use terminal orbit data if available; otherwise use the current orbit segment.
                Vessel v = null;
                if (HasOrbitData(rec))
                {
                    v = CreateGhostVesselForRecording(i, rec);
                }
                else if (rec.HasOrbitSegments)
                {
                    OrbitSegment? seg = TrajectoryMath.FindOrbitSegmentForMapDisplay(rec.OrbitSegments, currentUT);
                    if (seg.HasValue)
                        v = CreateGhostVesselFromSegment(i, rec, seg.Value);
                }

                if (v != null) created++;
            }

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "CreateGhostVesselsFromCommittedRecordings: created={0} from {1} recordings " +
                    "(skipped: debris={2} suppressed={3} spawned={4} terminal={5} noOrbit={6})",
                    created, committed.Count,
                    skippedDebris, skippedSuppressed, skippedSpawned, skippedTerminal, skippedNoOrbit));

            return created;
        }

        /// <summary>
        /// Decide whether an already-materialized Tracking Station ghost should retire on this
        /// lifecycle tick, and if so return the removal reason string to log/store.
        /// </summary>
        internal static string GetTrackingStationGhostRemovalReason(
            Recording rec, bool isSuppressed, bool hasOrbitBounds, double currentUT)
        {
            if (rec == null)
                return "tracking-station-expired";

            if (isSuppressed)
                return "tracking-station-child-started";

            if (!hasOrbitBounds)
                return null;

            OrbitSegment? seg = TrajectoryMath.FindOrbitSegmentForMapDisplay(rec.OrbitSegments, currentUT);
            return seg.HasValue ? null : "tracking-station-expired";
        }

        private static bool HasTrackingStationChildStarted(Recording child, double currentUT)
        {
            return child != null
                && child.TryGetGhostActivationStartUT(out double startUT)
                && currentUT >= startUT;
        }

        /// <summary>
        /// Ensure all tracked ghost vessels have mapObject and orbitRenderer.
        /// During SpaceTracking.Awake prefix, MapView.fetch may not be initialized yet
        /// (Unity doesn't guarantee Awake ordering), causing Vessel.AddOrbitRenderer()
        /// to silently skip creation. This method calls AddOrbitRenderer via Traverse
        /// on ghosts missing their renderer. Must be called after all Awake methods
        /// complete (e.g., from a buildVesselsList Prefix or from Start). (#195)
        /// </summary>
        internal static int EnsureGhostOrbitRenderers()
        {
            if (MapView.fetch == null)
            {
                ParsekLog.Warn(Tag, "EnsureGhostOrbitRenderers: MapView.fetch is null — cannot create orbit renderers");
                return 0;
            }

            // Collect unique ghost vessels from both dictionaries
            var ghosts = new HashSet<Vessel>();
            foreach (var v in vesselsByChainPid.Values)
                if (v != null) ghosts.Add(v);
            foreach (var v in vesselsByRecordingIndex.Values)
                if (v != null) ghosts.Add(v);

            int fixedCount = 0;
            foreach (var v in ghosts)
            {
                bool needsMapObj = v.mapObject == null;
                bool needsRenderer = v.orbitRenderer == null;

                if (!needsMapObj && !needsRenderer)
                    continue;

                // Call the private AddOrbitRenderer — it creates both mapObject and
                // orbitRenderer if missing. Now that MapView.fetch is available, the
                // guard inside AddOrbitRenderer passes. The method is idempotent.
                Traverse.Create(v).Method("AddOrbitRenderer").GetValue();

                if (v.orbitRenderer == null && needsRenderer)
                {
                    ParsekLog.Warn(Tag, string.Format(ic,
                        "EnsureGhostOrbitRenderers: AddOrbitRenderer via Traverse had no effect on '{0}' pid={1} — " +
                        "method may have been renamed in a KSP update",
                        v.vesselName, v.persistentId));
                }

                // Configure rendering (same as post-Load block)
                if (v.orbitRenderer != null)
                {
                    v.orbitRenderer.drawMode = OrbitRendererBase.DrawMode.REDRAW_AND_RECALCULATE;
                    v.orbitRenderer.drawIcons = OrbitRendererBase.DrawIcons.ALL;
                    if (!v.orbitRenderer.enabled)
                        v.orbitRenderer.enabled = true;
                }

                fixedCount++;
                ParsekLog.Info(Tag, string.Format(ic,
                    "EnsureGhostOrbitRenderers: fixed ghost '{0}' pid={1} (mapObj was null={2}, renderer was null={3}, " +
                    "now mapObj={4} renderer={5})",
                    v.vesselName, v.persistentId, needsMapObj, needsRenderer,
                    v.mapObject != null, v.orbitRenderer != null));
            }

            if (fixedCount > 0)
                ParsekLog.Info(Tag, string.Format(ic,
                    "EnsureGhostOrbitRenderers: fixed {0} of {1} ghost vessel(s)", fixedCount, ghosts.Count));
            else if (ghosts.Count > 0)
                ParsekLog.Verbose(Tag, string.Format(ic,
                    "EnsureGhostOrbitRenderers: all {0} ghost vessel(s) already have orbit renderers", ghosts.Count));

            return fixedCount;
        }

        /// <summary>
        /// Get the ghost Vessel for a chain PID, or null if none exists.
        /// Used for target transfer when chain resolves.
        /// </summary>
        internal static Vessel GetGhostVessel(uint chainPid)
        {
            vesselsByChainPid.TryGetValue(chainPid, out Vessel vessel);
            return vessel;
        }

        /// <summary>
        /// Find the recording index for a ghost map vessel by its PID.
        /// O(1) via reverse lookup dictionary. Returns -1 if not found.
        /// </summary>
        internal static int FindRecordingIndexByVesselPid(uint vesselPid)
        {
            if (vesselPidToRecordingIndex.TryGetValue(vesselPid, out int index))
                return index;
            return -1;
        }

        /// <summary>
        /// Returns true if a ghost map ProtoVessel exists for the given recording index.
        /// Used by ParsekUI to suppress the green dot marker when the native KSP icon is active.
        /// </summary>
        internal static bool HasGhostVesselForRecording(int recordingIndex)
        {
            return vesselsByRecordingIndex.ContainsKey(recordingIndex);
        }

        /// <summary>
        /// Returns the persistentId of the ghost map vessel for a recording index, or 0 if none.
        /// Used to check ghostsWithSuppressedIcon for the below-atmosphere icon handoff.
        /// </summary>
        internal static uint GetGhostVesselPidForRecording(int recordingIndex)
        {
            if (vesselsByRecordingIndex.TryGetValue(recordingIndex, out Vessel v))
                return v.persistentId;
            return 0;
        }

        /// <summary>
        /// Resolve the visible orbit time window for a ghost vessel at the current UT.
        /// Recording-index ghosts use dynamic same-body gap carry; other segment-based
        /// ghosts fall back to their stored bounds.
        /// </summary>
        internal static bool TryGetVisibleOrbitBoundsForGhostVessel(
            uint vesselPid, double currentUT, out double startUT, out double endUT)
        {
            startUT = 0;
            endUT = 0;

            int recordingIndex = FindRecordingIndexByVesselPid(vesselPid);
            var committed = RecordingStore.CommittedRecordings;
            if (recordingIndex >= 0
                && committed != null
                && recordingIndex < committed.Count
                && committed[recordingIndex].HasOrbitSegments
                && TrajectoryMath.TryGetOrbitWindowForMapDisplay(
                    committed[recordingIndex].OrbitSegments, currentUT,
                    out OrbitSegment segment,
                    out startUT,
                    out endUT,
                    out int firstVisibleIndex,
                    out int lastVisibleIndex,
                    out bool carriedAcrossGap))
            {
                ParsekLog.VerboseRateLimited(Tag,
                    string.Format(ic,
                        "visible-window-{0}-{1:F3}-{2:F3}-{3:F3}-{4:F3}-{5}",
                        vesselPid,
                        segment.startUT,
                        segment.endUT,
                        startUT,
                        endUT,
                        carriedAcrossGap ? "gap" : "segment"),
                    string.Format(ic,
                        "Map-visible orbit window pid={0} recIndex={1} ut={2:F2} body={3} " +
                        "segmentUT={4:F2}-{5:F2} windowUT={6:F2}-{7:F2} windowIndices={8}-{9} gapCarry={10}",
                        vesselPid,
                        recordingIndex,
                        currentUT,
                        segment.bodyName,
                        segment.startUT,
                        segment.endUT,
                        startUT,
                        endUT,
                        firstVisibleIndex,
                        lastVisibleIndex,
                        carriedAcrossGap),
                    1.0);

                if (PlaybackOrbitDiagnostics.TryBuildMapPredictedTailLog(
                    recordingIndex,
                    vesselPid,
                    committed[recordingIndex],
                    segment,
                    currentUT,
                    startUT,
                    endUT,
                    carriedAcrossGap,
                    out string mapRenderKey,
                    out string mapRenderMessage))
                {
                    ParsekLog.VerboseRateLimited("MapRender", mapRenderKey, mapRenderMessage, 1.0);
                }

                return true;
            }

            if (recordingIndex >= 0
                && committed != null
                && recordingIndex < committed.Count
                && committed[recordingIndex].HasOrbitSegments)
            {
                ParsekLog.VerboseRateLimited(Tag,
                    "visible-window-none-" + vesselPid,
                    string.Format(ic,
                        "Map-visible orbit window unavailable pid={0} recIndex={1} ut={2:F2} — " +
                        "no active or equivalent same-orbit segment chain",
                        vesselPid, recordingIndex, currentUT),
                    1.0);
            }

            if (ghostOrbitBounds.TryGetValue(vesselPid, out var bounds))
            {
                startUT = bounds.startUT;
                endUT = bounds.endUT;
                ParsekLog.VerboseRateLimited(Tag,
                    string.Format(ic, "visible-window-fallback-{0}-{1:F3}-{2:F3}", vesselPid, startUT, endUT),
                    string.Format(ic,
                        "Map-visible orbit window pid={0} source=stored-bounds ut={1:F2} windowUT={2:F2}-{3:F2}",
                        vesselPid, currentUT, startUT, endUT),
                    1.0);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Reset all state for testing (avoids Debug.Log crash outside Unity).
        /// </summary>
        internal static void ResetForTesting()
        {
            ghostMapVesselPids.Clear();
            ghostsWithSuppressedIcon.Clear();
            ghostOrbitBounds.Clear();
            vesselsByChainPid.Clear();
            vesselsByRecordingIndex.Clear();
            vesselPidToRecordingIndex.Clear();
        }

        /// <summary>
        /// Synchronous bookkeeping reset for the in-game test runner's between-run cleanup
        /// path (#417/#418). Clears the PID tracking HashSet, orbit bounds, and both
        /// recording-index maps in one shot without calling vessel.Die(), under the
        /// assumption that the caller has already invoked GhostPlaybackEngine.DestroyAllGhosts
        /// (or RemoveAllGhostVessels) so the Vessel-layer destruction ran first. This closes
        /// the carryover window where a ProtoVessel was already killed by an engine-driven
        /// overlap/loop end path but its PID lingered in ghostMapVesselPids, causing the
        /// GhostPidsResolveToProtoVessels test to see an orphan on the second Run All.
        ///
        /// Idempotent: safe to call when all dictionaries are already empty (emits a
        /// verbose no-op log). Does NOT call Die() on any vessel — those destructions
        /// are the caller's responsibility via RemoveAllGhostVessels or engine cleanup.
        /// </summary>
        internal static void ResetBetweenTestRuns(string reason)
        {
            int pidCount = ghostMapVesselPids.Count;
            int suppressedIconCount = ghostsWithSuppressedIcon.Count;
            int orbitBoundsCount = ghostOrbitBounds.Count;
            int chainCount = vesselsByChainPid.Count;
            int indexCount = vesselsByRecordingIndex.Count;
            int reverseCount = vesselPidToRecordingIndex.Count;

            int totalTracked = pidCount + suppressedIconCount + orbitBoundsCount
                + chainCount + indexCount + reverseCount;

            if (totalTracked == 0)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "ResetBetweenTestRuns: all dictionaries already empty (reason={0}) — noop",
                        reason ?? "(null)"));
                return;
            }

            ghostMapVesselPids.Clear();
            ghostsWithSuppressedIcon.Clear();
            ghostOrbitBounds.Clear();
            vesselsByChainPid.Clear();
            vesselsByRecordingIndex.Clear();
            vesselPidToRecordingIndex.Clear();

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "ResetBetweenTestRuns: cleared bookkeeping reason={0} " +
                    "pids={1} suppressedIcons={2} orbitBounds={3} chainVessels={4} " +
                    "indexVessels={5} reverseLookup={6}",
                    reason ?? "(null)",
                    pidCount, suppressedIconCount, orbitBoundsCount,
                    chainCount, indexCount, reverseCount));
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Find a CelestialBody by name without LINQ allocation.
        /// Returns null if FlightGlobals.Bodies is null or name not found.
        /// </summary>
        private static CelestialBody FindBodyByName(string bodyName)
        {
            var bodies = FlightGlobals.Bodies;
            if (bodies == null) return null;
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i].name == bodyName) return bodies[i];
            return null;
        }

        /// <summary>
        /// Shared ProtoVessel creation: resolves body, builds orbit + vessel node,
        /// creates ProtoVessel, pre-registers PID, loads into flightState.
        /// Returns the Vessel or null on failure. Handles full cleanup on error.
        /// </summary>
        /// <summary>
        /// Overload that creates a ProtoVessel using an OrbitSegment instead of terminal orbit data.
        /// Used for intermediate chain segments that have orbit segments but no terminal orbit.
        /// </summary>
        private static Vessel BuildAndLoadGhostProtoVessel(
            IPlaybackTrajectory traj, OrbitSegment segment, string logContext)
        {
            CelestialBody body = FindBodyByName(segment.bodyName);
            if (body == null)
            {
                ParsekLog.Error(Tag,
                    string.Format(ic,
                        "BuildAndLoadGhostProtoVessel(segment): body '{0}' not found for {1}",
                        segment.bodyName, logContext));
                return null;
            }

            Orbit orbit = new Orbit(
                segment.inclination,
                segment.eccentricity,
                segment.semiMajorAxis,
                segment.longitudeOfAscendingNode,
                segment.argumentOfPeriapsis,
                segment.meanAnomalyAtEpoch,
                segment.epoch,
                body);

            return BuildAndLoadGhostProtoVesselCore(traj, orbit, body, logContext);
        }

        private static Vessel BuildAndLoadGhostProtoVessel(IPlaybackTrajectory traj, string logContext)
        {
            if (!TryResolveGhostProtoOrbitSeed(
                traj,
                out double inclination,
                out double eccentricity,
                out double semiMajorAxis,
                out double lan,
                out double argumentOfPeriapsis,
                out double meanAnomalyAtEpoch,
                out double epoch,
                out string orbitBodyName))
            {
                ParsekLog.Error(Tag,
                    string.Format(ic,
                        "BuildAndLoadGhostProtoVessel: no endpoint-aligned orbit seed for {0}",
                        logContext));
                return null;
            }

            CelestialBody body = FindBodyByName(orbitBodyName);
            if (body == null)
            {
                ParsekLog.Error(Tag,
                    string.Format(ic,
                        "BuildAndLoadGhostProtoVessel: body '{0}' not found for {1}",
                        orbitBodyName, logContext));
                return null;
            }

            Orbit orbit = new Orbit(
                inclination,
                eccentricity,
                semiMajorAxis,
                lan,
                argumentOfPeriapsis,
                meanAnomalyAtEpoch,
                epoch,
                body);

            return BuildAndLoadGhostProtoVesselCore(traj, orbit, body, logContext);
        }

        internal static bool TryResolveGhostProtoOrbitSeed(
            IPlaybackTrajectory traj,
            out double inclination,
            out double eccentricity,
            out double semiMajorAxis,
            out double lan,
            out double argumentOfPeriapsis,
            out double meanAnomalyAtEpoch,
            out double epoch,
            out string bodyName)
        {
            return RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed(
                traj,
                out inclination,
                out eccentricity,
                out semiMajorAxis,
                out lan,
                out argumentOfPeriapsis,
                out meanAnomalyAtEpoch,
                out epoch,
                out bodyName);
        }

        private static Vessel BuildAndLoadGhostProtoVesselCore(
            IPlaybackTrajectory traj, Orbit orbit, CelestialBody body, string logContext)
        {
            ProtoVessel pv = null;
            try
            {

                // Single antenna-free part (avoids CommNet conflict with GhostCommNetRelay)
                ConfigNode partNode = ProtoVessel.CreatePartNode("sensorBarometer", 0);

                // Discovery: fully visible, infinite lifetime
                ConfigNode discovery = ProtoVessel.CreateDiscoveryNode(
                    DiscoveryLevels.Owned, UntrackedObjectClass.C,
                    double.PositiveInfinity, double.PositiveInfinity);

                VesselType vtype = ResolveVesselType(traj.VesselSnapshot);
                string vesselName = "Ghost: " + (traj.VesselName ?? "Unknown");

                ConfigNode vesselNode = ProtoVessel.CreateVesselNode(
                    vesselName, vtype, orbit, 0,
                    new ConfigNode[] { partNode }, discovery);

                // Critical settings: prevent ground positioning and KSC cleanup
                vesselNode.SetValue("vesselSpawning", "False", true);
                vesselNode.SetValue("prst", "True", true);
                vesselNode.SetValue("cln", "False", true);

                // Defensive: ensure sub-nodes that SpaceTracking.buildVesselsList and other
                // KSP internals assume exist. CreateVesselNode adds ACTIONGROUPS but omits
                // these three. Missing nodes can cause NREs in tracking station code paths.
                if (vesselNode.GetNode("FLIGHTPLAN") == null)
                    vesselNode.AddNode("FLIGHTPLAN");
                if (vesselNode.GetNode("CTRLSTATE") == null)
                    vesselNode.AddNode("CTRLSTATE");
                if (vesselNode.GetNode("VESSELMODULES") == null)
                    vesselNode.AddNode("VESSELMODULES");

                pv = new ProtoVessel(vesselNode, HighLogic.CurrentGame);

                // PRE-REGISTER PID before Load — pv.Load fires onVesselCreate and guards
                // must see this PID as a ghost vessel during the event cascade.
                ghostMapVesselPids.Add(pv.persistentId);

                HighLogic.CurrentGame.flightState.protoVessels.Add(pv);

                // Load — creates Vessel GO, OrbitDriver, MapObject, OrbitRenderer,
                // registers in FlightGlobals, fires GameEvents.onVesselCreate
                pv.Load(HighLogic.CurrentGame.flightState);

                if (pv.vesselRef == null)
                {
                    ParsekLog.Error(Tag,
                        string.Format(ic,
                            "BuildAndLoadGhostProtoVessel: vesselRef is null after Load for {0}",
                            logContext));
                    ghostMapVesselPids.Remove(pv.persistentId);
                    HighLogic.CurrentGame.flightState.protoVessels.Remove(pv);
                    return null;
                }

                // Log creation + OrbitDriver state for diagnostics (#172)
                Vessel v = pv.vesselRef;
                string driverState = "no-orbitDriver";
                if (v.orbitDriver != null)
                {
                    Orbit drv = v.orbitDriver.orbit;

                    driverState = string.Format(ic,
                        "updateMode={0} sma={1:F0} ecc={2:F6} inc={3:F4} " +
                        "argPe={4:F4} mna={5:F6} epoch={6:F1} vesselPos=({7:F1},{8:F1},{9:F1})",
                        v.orbitDriver.updateMode,
                        drv.semiMajorAxis, drv.eccentricity, drv.inclination,
                        drv.argumentOfPeriapsis, drv.meanAnomalyAtEpoch, drv.epoch,
                        v.GetWorldPos3D().x, v.GetWorldPos3D().y, v.GetWorldPos3D().z);
                }

                // Ensure OrbitRenderer is enabled — in Tracking Station, pv.Load()
                // may create the renderer in a disabled state.
                if (v.orbitRenderer != null)
                {
                    v.orbitRenderer.drawMode = OrbitRendererBase.DrawMode.REDRAW_AND_RECALCULATE;
                    v.orbitRenderer.drawIcons = OrbitRendererBase.DrawIcons.ALL;
                    if (!v.orbitRenderer.enabled)
                    {
                        v.orbitRenderer.enabled = true;
                        ParsekLog.Verbose(Tag, string.Format(ic,
                            "Force-enabled OrbitRenderer for ghost '{0}'", vesselName));
                    }
                }

                // Force correct VesselType — KSP may override for single-part vessels
                if (v.vesselType != vtype)
                {
                    ParsekLog.Verbose(Tag, string.Format(ic,
                        "Ghost vessel type overridden by KSP: {0} → {1}, restoring to {2}",
                        vtype, v.vesselType, vtype));
                    v.vesselType = vtype;
                }

                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Created ghost vessel '{0}' ghostPid={1} type={2} body={3} sma={4:F0} for {5} | {6} " +
                        "mapObj={7} orbitRenderer={8} scene={9}",
                        vesselName, v.persistentId,
                        vtype, body.name, traj.TerminalOrbitSemiMajorAxis, logContext, driverState,
                        v.mapObject != null, v.orbitRenderer != null,
                        HighLogic.LoadedScene));

                return v;
            }
            catch (Exception ex)
            {
                if (pv != null)
                {
                    ghostMapVesselPids.Remove(pv.persistentId);
                    HighLogic.CurrentGame?.flightState?.protoVessels?.Remove(pv);
                }
                ParsekLog.Error(Tag,
                    string.Format(ic,
                        "BuildAndLoadGhostProtoVessel failed for {0}: {1}",
                        logContext, ex.Message));
                return null;
            }
        }

        /// <summary>
        /// Read VesselType from vessel snapshot ConfigNode.
        /// Falls back to VesselType.Ship if snapshot is null or type is missing.
        /// </summary>
        internal static VesselType ResolveVesselType(ConfigNode vesselSnapshot)
        {
            if (vesselSnapshot == null) return VesselType.Ship;

            string typeStr = vesselSnapshot.GetValue("type");
            if (string.IsNullOrEmpty(typeStr)) return VesselType.Ship;

            if (Enum.TryParse(typeStr, true, out VesselType vtype))
                return vtype;

            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "ResolveVesselType: unrecognized type '{0}' — defaulting to Ship",
                    typeStr));
            return VesselType.Ship;
        }
    }
}
