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
        /// O(1) check used by all guard code throughout the codebase.
        /// Returns true if the given persistentId belongs to a ghost map ProtoVessel.
        /// </summary>
        internal static bool IsGhostMapVessel(uint persistentId)
        {
            return ghostMapVesselPids.Contains(persistentId);
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
                ParsekLog.Warn(Tag,
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
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "UpdateGhostOrbitFromStateVectors: no OrbitDriver for recording #{0}",
                        recordingIndex));
                return;
            }

            CelestialBody body = FindBodyByName(point.bodyName);
            if (body == null)
            {
                ParsekLog.Warn(Tag,
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
                ParsekLog.Warn(Tag,
                    string.Format(ic, "ApplyOrbitToVessel: no OrbitDriver for {0}", logContext));
                return;
            }

            CelestialBody body = FindBodyByName(segment.bodyName);
            if (body == null)
            {
                ParsekLog.Warn(Tag,
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
        /// Create ghost ProtoVessels for all committed recordings with stable orbital data.
        /// Used in non-flight scenes (tracking station) where the playback engine is not running.
        /// Skips debris, non-orbital terminal states, and recordings that already have ghost vessels.
        /// </summary>
        internal static int CreateGhostVesselsFromCommittedRecordings()
        {
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || committed.Count == 0) return 0;

            int created = 0;
            double currentUT = Planetarium.GetUniversalTime();
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec.IsDebris) continue;

                // Skip recordings with non-orbital terminal states (Landed, Destroyed, etc.)
                // Allow: Orbiting, Docked, and null (intermediate chain segments / unfinished).
                var terminal = rec.TerminalStateValue;
                if (terminal.HasValue
                    && terminal.Value != TerminalState.Orbiting
                    && terminal.Value != TerminalState.Docked)
                    continue;

                // Skip recordings where currentUT is outside all orbit segments —
                // either before the vessel reached orbit or after it left orbit.
                // For null-terminal recordings this is the only guard against stale orbits.
                if (rec.OrbitSegments != null && rec.OrbitSegments.Count > 0)
                {
                    var firstSeg = rec.OrbitSegments[0];
                    var lastSeg = rec.OrbitSegments[rec.OrbitSegments.Count - 1];
                    if (currentUT < firstSeg.startUT || currentUT > lastSeg.endUT)
                        continue;
                }
                // No orbit segments and no terminal orbit → nothing to show
                else if (!HasOrbitData(rec))
                {
                    continue;
                }

                // Use terminal orbit data if available; otherwise fall back to
                // the last orbit segment (intermediate chain segments and recordings
                // without terminal orbit fields still have orbit segment data).
                Vessel v = null;
                if (HasOrbitData(rec))
                {
                    v = CreateGhostVesselForRecording(i, rec);
                }
                else if (rec.OrbitSegments != null && rec.OrbitSegments.Count > 0)
                {
                    var lastSeg = rec.OrbitSegments[rec.OrbitSegments.Count - 1];
                    v = CreateGhostVesselFromSegment(i, rec, lastSeg);
                }

                if (v != null) created++;
            }

            if (created > 0)
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Created {0} ghost ProtoVessel(s) from {1} committed recordings",
                        created, committed.Count));

            return created;
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
        /// Reset all state for testing (avoids Debug.Log crash outside Unity).
        /// </summary>
        internal static void ResetForTesting()
        {
            ghostMapVesselPids.Clear();
            ghostsWithSuppressedIcon.Clear();
            vesselsByChainPid.Clear();
            vesselsByRecordingIndex.Clear();
            vesselPidToRecordingIndex.Clear();
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
                ParsekLog.Warn(Tag,
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
            CelestialBody body = FindBodyByName(traj.TerminalOrbitBody);
            if (body == null)
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "BuildAndLoadGhostProtoVessel: body '{0}' not found for {1}",
                        traj.TerminalOrbitBody, logContext));
                return null;
            }

            Orbit orbit = new Orbit(
                traj.TerminalOrbitInclination,
                traj.TerminalOrbitEccentricity,
                traj.TerminalOrbitSemiMajorAxis,
                traj.TerminalOrbitLAN,
                traj.TerminalOrbitArgumentOfPeriapsis,
                traj.TerminalOrbitMeanAnomalyAtEpoch,
                traj.TerminalOrbitEpoch,
                body);

            return BuildAndLoadGhostProtoVesselCore(traj, orbit, body, logContext);
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
                    ParsekLog.Warn(Tag,
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
