using System;
using System.Collections.Generic;
using System.Globalization;

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

            // Ensure orbit patch covers the full trajectory so the orbit renderer
            // draws the complete line (not clipped to a short time span).
            // For hyperbolic escape orbits, extend EndUT far enough to reach the SOI edge.
            // FINAL transition tells KSP this is the last patch — no SOI boundary.
            orb.StartUT = segment.epoch;
            orb.EndUT = segment.epoch + (segment.eccentricity >= 1.0 ? 1e8 : orb.period * 1.5);
            orb.patchStartTransition = Orbit.PatchTransitionType.INITIAL;
            orb.patchEndTransition = Orbit.PatchTransitionType.FINAL;

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

            // Diagnostic logging: full element set + resulting vessel position (#172)
            Orbit drv = vessel.orbitDriver.orbit;
            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "Orbit updated for {0} body={1} sma={2:F0} ecc={3:F6} inc={4:F4} " +
                    "lan={5:F4} argPe={6:F4} mna={7:F6} epoch={8:F1} " +
                    "updateMode={9} vesselPos=({10:F1},{11:F1},{12:F1}) " +
                    "drvPos=({13:F1},{14:F1},{15:F1})",
                    logContext, body.name, segment.semiMajorAxis,
                    drv.eccentricity, drv.inclination, drv.LAN,
                    drv.argumentOfPeriapsis, drv.meanAnomalyAtEpoch, drv.epoch,
                    vessel.orbitDriver.updateMode,
                    vessel.GetWorldPos3D().x, vessel.GetWorldPos3D().y, vessel.GetWorldPos3D().z,
                    vessel.orbitDriver.pos.x, vessel.orbitDriver.pos.y, vessel.orbitDriver.pos.z));
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
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec.IsDebris) continue;

                var terminal = rec.TerminalStateValue;
                if (terminal.HasValue
                    && terminal.Value != TerminalState.Orbiting
                    && terminal.Value != TerminalState.Docked)
                    continue;

                if (!HasOrbitData(rec)) continue;

                Vessel v = CreateGhostVesselForRecording(i, rec);
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
        /// Reset all state for testing (avoids Debug.Log crash outside Unity).
        /// </summary>
        internal static void ResetForTesting()
        {
            ghostMapVesselPids.Clear();
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
        private static Vessel BuildAndLoadGhostProtoVessel(IPlaybackTrajectory traj, string logContext)
        {
            ProtoVessel pv = null;
            try
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

                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Created ghost vessel '{0}' ghostPid={1} type={2} body={3} sma={4:F0} for {5} | {6}",
                        vesselName, v.persistentId,
                        vtype, body.name, traj.TerminalOrbitSemiMajorAxis, logContext, driverState));

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
