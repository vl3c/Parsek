using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Ghost ProtoVessel create / update / remove lifecycle, the stock-Fly strip, proto build
    // and orbit seeding.
    internal static partial class GhostMapPresence
    {
        internal struct GhostProtoOrbitSeedDiagnostics
        {
            public string Source;
            public string EndpointBodyName;
            public string FailureReason;
            public string FallbackReason;
            public bool TailSeedConsidered;
            public bool TailSeedAccepted;
            public string TailDeclineReason;
            public double TailUT;
            public double TailSma;
            public double TailEcc;
            public double LatestSegmentEndUT;
            public double RotationDriftSeconds;
            public string TailFrameSource;
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
                // The chain tip may have advanced since the ghost was first
                // created (write-once map staleness); keep the reverse map on
                // the recording that drives the ghost NOW.
                Vessel existingChainGhost = vesselsByChainPid[chain.OriginalVesselPid];
                if (existingChainGhost != null && traj != null)
                    RebindGhostRecordingId(existingChainGhost.persistentId, traj.RecordingId);
                return existingChainGhost;
            }

            // Intent-line: chain-pid terminal-orbit creation about to fire.
            {
                var intent = NewDecisionFields("create-chain-intent");
                intent.RecordingId = traj.RecordingId;
                intent.RecordingIndex = -1;
                intent.VesselName = traj.VesselName;
                intent.Source = "Chain";
                intent.Branch = "(n/a)";
                intent.Body = traj.TerminalOrbitBody;
                intent.TerminalBody = traj.TerminalOrbitBody;
                intent.TerminalSma = traj.TerminalOrbitSemiMajorAxis;
                intent.TerminalEcc = traj.TerminalOrbitEccentricity;
                intent.Reason = string.Format(ic, "chainPid={0}", chain.OriginalVesselPid);
                ParsekLog.Verbose(Tag, BuildGhostMapDecisionLine(intent));
            }

            string logContext = string.Format(ic, "chain pid={0}", chain.OriginalVesselPid);
            Vessel vessel = BuildAndLoadGhostProtoVessel(traj, logContext);
            if (vessel != null)
            {
                vesselsByChainPid[chain.OriginalVesselPid] = vessel;
                // Complete the pid -> recordingId reverse map for chain (terminal-orbit) ghosts
                // too, mirroring the timeline path's TrackRecordingGhostVessel write. Without this,
                // chain-tip ghost pids had no reverse entry, so recordingId-keyed consumers
                // (MapRenderTrace second-cut window/correlation, polyline-ownership, visibility
                // checks) silently dropped them. Keyed by the LIVE ghost vessel.persistentId (the
                // map world's native key), NOT chain.OriginalVesselPid. Removed in RemoveGhostVessel.
                // No else-remove branch (unlike TrackRecordingGhostVessel) is needed: we only reach
                // here on a fresh create (the vesselsByChainPid.ContainsKey early-return above blocks
                // re-entry), and the live pid is a fresh KSP-unique spawn pid, so no stale entry exists.
                if (!string.IsNullOrEmpty(traj.RecordingId))
                    vesselPidToRecordingId[vessel.persistentId] = traj.RecordingId;
                lifecycleCreatedThisTick++;

                Vector3d worldPos = vessel.GetWorldPos3D();
                string body = vessel.orbitDriver?.referenceBody?.name ?? traj.TerminalOrbitBody;

                var done = NewDecisionFields("create-chain-done");
                done.RecordingId = traj.RecordingId;
                done.RecordingIndex = -1;
                done.VesselName = traj.VesselName;
                done.Source = "Chain";
                done.Branch = "(n/a)";
                done.Body = body;
                done.WorldPos = worldPos;
                done.GhostPid = vessel.persistentId;
                done.TerminalBody = traj.TerminalOrbitBody;
                done.TerminalSma = traj.TerminalOrbitSemiMajorAxis;
                done.TerminalEcc = traj.TerminalOrbitEccentricity;
                done.Reason = string.Format(ic, "chainPid={0}", chain.OriginalVesselPid);
                ParsekLog.Info(Tag, BuildGhostMapDecisionLine(done));

                lastKnownByChainPid[chain.OriginalVesselPid] = new LastKnownGhostFrame
                {
                    RecordingId = traj.RecordingId,
                    VesselName = traj.VesselName,
                    GhostPid = vessel.persistentId,
                    Source = "Chain",
                    Branch = "(n/a)",
                    Body = body,
                    WorldPos = worldPos,
                    AnchorPid = 0u,
                    LastUT = double.NaN
                };
            }

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
            lifecycleUpdatedThisTick++;

            Vector3d worldPos = vessel.GetWorldPos3D();
            string updateKey = string.Format(ic, "chain-orbit-update-{0}", chainPid);
            var done = NewDecisionFields("update-chain-segment");
            done.RecordingIndex = -1;
            done.GhostPid = vessel.persistentId;
            done.Source = "Chain";
            done.Branch = "(n/a)";
            done.Body = segment.bodyName;
            done.WorldPos = worldPos;
            done.Segment = segment;
            done.Reason = string.Format(ic, "chainPid={0}", chainPid);
            ParsekLog.VerboseRateLimited(Tag, updateKey, BuildGhostMapDecisionLine(done), 5.0);

            // Keep last-known frame fresh for the destroy log.
            if (lastKnownByChainPid.TryGetValue(chainPid, out var prev))
            {
                prev.Body = segment.bodyName;
                prev.WorldPos = worldPos;
                prev.LastUT = segment.startUT;
                lastKnownByChainPid[chainPid] = prev;
            }
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
            bool hadLastKnown = lastKnownByChainPid.TryGetValue(chainPid, out LastKnownGhostFrame last);

            BeginGhostTeardown();
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
            finally
            {
                EndGhostTeardown();
            }

            ghostMapVesselPids.Remove(ghostPid);
            ghostsWithSuppressedIcon.Remove(ghostPid);
            ghostNoBoundsSuppressLastFrame.Remove(ghostPid);
            ghostOrbitBounds.Remove(ghostPid);
            ghostBodyFrameOrbitBounds.Remove(ghostPid);
            ghostLastAppliedOrbitBody.Remove(ghostPid);
            ghostLastAppliedOrbitElements.Remove(ghostPid);
            ghostOrbitLoopShiftedPids.Remove(ghostPid);
            ghostOrbitEpochShift.Remove(ghostPid);
            ghostIconDrivePropagation.Remove(ghostPid);
            ghostParkingConicLineHoldUntilUT.Remove(ghostPid);
            vesselPidToRecordingId.Remove(ghostPid);
            vesselsByChainPid.Remove(chainPid);
            lastKnownByChainPid.Remove(chainPid);
            lifecycleDestroyedThisTick++;

            var destroy = NewDecisionFields("destroy-chain");
            destroy.RecordingId = hadLastKnown ? last.RecordingId : null;
            destroy.RecordingIndex = -1;
            destroy.VesselName = hadLastKnown ? last.VesselName : null;
            destroy.Source = hadLastKnown ? last.Source : "Chain";
            destroy.Branch = hadLastKnown ? last.Branch : "(n/a)";
            destroy.Body = hadLastKnown ? last.Body : null;
            destroy.WorldPos = hadLastKnown ? (Vector3d?)last.WorldPos : null;
            destroy.GhostPid = ghostPid;
            destroy.AnchorPid = hadLastKnown ? last.AnchorPid : 0u;
            destroy.UT = hadLastKnown ? last.LastUT : double.NaN;
            destroy.Reason = string.Format(ic, "{0} chainPid={1} wasTarget={2}",
                reason ?? "(none)", chainPid, wasTarget);
            ParsekLog.Info(Tag, BuildGhostMapDecisionLine(destroy));

            // MapRenderTrace Tier-A: structural GhostDestroyed event, keyed by the
            // live ghost persistentId. Gated at the call site so disabled play pays
            // no formatting cost; reads only last-known fields already snapshotted
            // above (the vessel is dead, so world position comes from lastKnown).
            if (MapRenderTrace.IsEnabled)
            {
                double destroyUT = hadLastKnown ? last.LastUT : CurrentUTNow();
                MapRenderTrace.EmitStructural(
                    "GhostDestroyed",
                    MapRenderTrace.RenderSurface.ProtoIcon,
                    ghostPid.ToString(ic),
                    destroyUT,
                    destroyUT,
                    MapRenderTrace.DestroyWindowSeconds,
                    MapRenderTrace.BuildLifecycleDetails(
                        hadLastKnown ? last.VesselName : null,
                        hadLastKnown ? last.Body : null,
                        HighLogic.LoadedScene.ToString(),
                        hadLastKnown ? (Vector3d?)last.WorldPos : null,
                        // Carry the recordingId for pid<->recordingId correlation (see the index-keyed destroy).
                        string.Format(ic, "{0} chainPid={1} rec={2}", reason ?? "(none)", chainPid,
                            hadLastKnown ? (last.RecordingId ?? "<none>") : "<none>")));
            }

            return wasTarget;
        }

        /// <summary>
        /// Remove all ghost vessels (rewind or scene cleanup). Before the Die loop the
        /// planetarium camera is moved off any ghost it targets
        /// (<see cref="RetargetPlanetariumCameraOffDyingGhosts"/>), except during a scene
        /// teardown (<paramref name="sceneTeardown"/>, the addons' OnDestroy) or an application
        /// quit: the KnowledgeBase may already be half destroyed there, so a retarget would NRE
        /// with Parsek frames on the stack. The quit is covered earlier from
        /// <c>ParsekHarmony.OnApplicationQuit</c>.
        /// </summary>
        internal static void RemoveAllGhostVessels(string reason, bool sceneTeardown = false)
        {
            int chainCount = vesselsByChainPid.Count;
            int indexCount = vesselsByRecordingIndex.Count;
            int overlapInstanceCount = overlapInstanceVessels.Count;
            if (chainCount == 0 && indexCount == 0 && overlapInstanceCount == 0)
            {
                ParsekLog.VerboseRateLimited(Tag,
                    "remove-all-empty|" + (reason ?? "(none)"),
                    string.Format(ic,
                        "RemoveAllGhostVessels: no ghost vessels to remove (reason={0})",
                        reason),
                    30.0);
                return;
            }

            // Collect all vessels to destroy (chain + recording index + overlap instances)
            var vessels = new List<Vessel>(chainCount + indexCount + overlapInstanceCount);
            vessels.AddRange(vesselsByChainPid.Values);
            vessels.AddRange(vesselsByRecordingIndex.Values);
            vessels.AddRange(overlapInstanceVessels.Values);

            if (sceneTeardown || ParsekProcess.IsApplicationQuitting)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "RemoveAllGhostVessels: planetarium camera retarget skipped (reason={0} sceneTeardown={1} "
                        + "quitting={2}); the UI may already be torn down",
                        reason, sceneTeardown, ParsekProcess.IsApplicationQuitting));
            }
            else
            {
                RetargetPlanetariumCameraOffDyingGhosts(vessels, reason, sceneTeardown: false);
            }

            BeginGhostTeardown();
            removeAllGhostVesselsDepth++;
            try
            {
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
            }
            finally
            {
                if (removeAllGhostVesselsDepth > 0)
                    removeAllGhostVesselsDepth--;
                EndGhostTeardown();
            }

            ghostMapVesselPids.Clear();
            ghostsWithSuppressedIcon.Clear();
            ghostNoBoundsSuppressLastFrame.Clear();
            ghostOrbitBounds.Clear();
            ghostBodyFrameOrbitBounds.Clear();
            ghostLastAppliedOrbitBody.Clear();
            ghostLastAppliedOrbitElements.Clear();
            ghostOrbitLoopShiftedPids.Clear();
            ghostOrbitEpochShift.Clear();
            ghostIconDrivePropagation.Clear();
            vesselsByChainPid.Clear();
            vesselsByRecordingIndex.Clear();
            overlapInstanceVessels.Clear();
            boundaryOverlapSecondaryPids.Clear(); // M1
            vesselPidToRecordingIndex.Clear();
            vesselPidToRecordingId.Clear();
            ghostParkingConicLineHoldUntilUT.Clear();
            trackingStationStateVectorOrbitTrajectories.Clear();
            trackingStationStateVectorCachedIndices.Clear();
            activeReFlyDeferredStateVectorGhostSessions.Clear();
            lastKnownByRecordingIndex.Clear();
            lastKnownByChainPid.Clear();

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Removed all {0} ghost vessel(s) reason={1} (chain={2} index={3} overlapInstances={4})",
                    chainCount + indexCount + overlapInstanceCount, reason,
                    chainCount, indexCount, overlapInstanceCount));
        }

        /// <summary>
        /// PURE, Unity-free, unit-testable. Returns the index drift the player would
        /// have suffered on a Tracking Station "Fly" if the ghost map vessels were
        /// stripped from the saved persistent.sfs (current StripFromSave behaviour)
        /// but NOT from the live FlightGlobals.Vessels list — i.e. the off-by amount
        /// stock FlightDriver.StartAndFocusVessel(file, IndexOf(v)) would land wrong by.
        ///
        /// Stock SpaceTracking.FlyVessel identifies the target purely by its index in
        /// the live FlightGlobals.Vessels list, then focuses the vessel at that same
        /// index in the (ghost-free) loaded file. The drift is exactly the number of
        /// ghost pids that occupy a live index strictly LESS than the target's live
        /// index, because each such ghost shifts the target one slot earlier in the
        /// ghost-free file.
        ///
        /// Contract:
        ///   - target not present in liveVesselPids -> FlyIndexDriftTargetNotInLiveList (-1).
        ///   - target present at live index t -> count of i in [0, t) where
        ///     liveVesselPids[i] is in ghostPids.
        ///   - drift == 0: the Fly would have landed correctly even without the fix.
        ///   - drift > 0: the bug magnitude the strip prevents.
        /// </summary>
        internal static int ComputeFlyIndexDrift(
            IReadOnlyList<uint> liveVesselPids,
            ISet<uint> ghostPids,
            uint targetPid)
        {
            if (liveVesselPids == null)
                return FlyIndexDriftTargetNotInLiveList;

            int targetIndex = -1;
            for (int i = 0; i < liveVesselPids.Count; i++)
            {
                if (liveVesselPids[i] == targetPid)
                {
                    targetIndex = i;
                    break;
                }
            }

            if (targetIndex < 0)
                return FlyIndexDriftTargetNotInLiveList;

            if (ghostPids == null || ghostPids.Count == 0)
                return 0;

            int drift = 0;
            for (int i = 0; i < targetIndex; i++)
            {
                if (ghostPids.Contains(liveVesselPids[i]))
                    drift++;
            }

            return drift;
        }

        /// <summary>
        /// PURE log-line formatter for the pre-stock TS Fly ghost strip diagnostic.
        /// Returned string is the message body (subsystem tag added by the caller via
        /// ParsekLog.Info). Kept separate from the Unity-touching removal so the
        /// diagnostic numbers can be asserted in xUnit without a live FlightGlobals.
        /// </summary>
        internal static string FormatFlyStripDiagnostic(
            uint targetPid,
            int ghostCount,
            int liveVesselsBefore,
            int flyIndexDriftAvoided)
        {
            return string.Format(ic,
                "TS Fly pre-stock ghost strip: targetPid={0} ghostCount={1} " +
                "liveVesselsBefore={2} flyIndexDriftAvoided={3} - removed all ghost map " +
                "vessels before stock IndexOf/SaveGame so the live list and persistent.sfs " +
                "are ghost-consistent",
                targetPid,
                ghostCount,
                liveVesselsBefore,
                flyIndexDriftAvoided);
        }

        /// <summary>
        /// Tracking Station "Fly" root-cause fix (BUG #1). Removes all ghost map
        /// vessels from the live FlightGlobals.Vessels list BEFORE stock
        /// SpaceTracking.FlyVessel computes IndexOf(v) + SaveGame, so the live list and
        /// the persistent.sfs StripFromSave produces are both ghost-free and stock
        /// StartAndFocusVessel lands on the intended vessel the first time.
        ///
        /// Called only for a real (non-ghost) Fly target by
        /// SwitchIntentTrackingStationFlyPatch (the ghost-block path keeps ghosts
        /// alive — the player stays in the Tracking Station). Removal itself is always
        /// safe: it is what ParsekTrackingStation.OnDestroy -> RemoveAllGhostVessels
        /// does a moment later; on a real Fly the scene is transitioning out, so the
        /// icons vanishing a few frames early is invisible.
        ///
        /// Captures the live pid list and the would-be index drift via the pure
        /// ComputeFlyIndexDrift BEFORE removal so the logged number reflects the bug
        /// the strip just prevented.
        /// </summary>
        internal static void RemoveAllGhostVesselsBeforeStockFly(uint targetPid)
        {
            int ghostCount = ghostMapVesselPids.Count;

            // Build the live pid list + target drift from the still-polluted list
            // captured BEFORE RemoveAllGhostVessels, so the logged drift is the
            // off-by amount the strip avoided (post-strip the list is ghost-free).
            var livePids = new List<uint>();
            var liveVessels = FlightGlobals.Vessels;
            if (liveVessels != null)
            {
                for (int i = 0; i < liveVessels.Count; i++)
                {
                    Vessel vessel = liveVessels[i];
                    if (vessel != null)
                        livePids.Add(vessel.persistentId);
                }
            }

            if (ghostCount == 0)
            {
                // Common no-ghost Fly: nothing to strip, no index drift possible.
                // Single Verbose line so the case is observable without spam.
                ParsekLog.Verbose("SwitchIntentPatch",
                    string.Format(ic,
                        "TS Fly pre-stock ghost strip: no ghost map vessels to strip " +
                        "targetPid={0} liveVessels={1} - stock IndexOf/SaveGame already ghost-consistent",
                        targetPid,
                        livePids.Count));
                return;
            }

            int drift = ComputeFlyIndexDrift(livePids, ghostMapVesselPids, targetPid);
            if (drift == FlyIndexDriftTargetNotInLiveList)
            {
                // Defensive: another mod removed the target from the live list before
                // the strip ran. Removal is still safe and is what TS-cleanup does,
                // so proceed; the warn flags that the drift could not be measured.
                ParsekLog.Warn("SwitchIntentPatch",
                    string.Format(ic,
                        "TS Fly pre-stock ghost strip: targetPid={0} not found in live " +
                        "FlightGlobals.Vessels (ghostCount={1} liveVessels={2}) - drift " +
                        "unmeasurable; proceeding with ghost removal anyway (always safe)",
                        targetPid,
                        ghostCount,
                        livePids.Count));
            }
            else
            {
                ParsekLog.Info("SwitchIntentPatch",
                    FormatFlyStripDiagnostic(targetPid, ghostCount, livePids.Count, drift));
            }

            RemoveAllGhostVessels(TsFlyBeforeStockIndexofReason);
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

            // Phase 7 of Rewind-to-Staging (design §3.3): during an active re-fly
            // session, suppressed recordings must NOT own map presence. Destroy
            // any leftover entry from before the session started and skip.
            if (IsSuppressedByActiveSession(recordingIndex))
            {
                RemoveGhostVesselForRecording(recordingIndex, "session-suppressed subtree");
                return null;
            }

            // Already exists?
            if (vesselsByRecordingIndex.ContainsKey(recordingIndex))
                return vesselsByRecordingIndex[recordingIndex];

            // Intent-line: terminal-orbit creation about to fire.
            {
                var intent = NewDecisionFields("create-terminal-orbit-intent");
                intent.RecordingId = traj.RecordingId;
                intent.RecordingIndex = recordingIndex;
                intent.VesselName = traj.VesselName;
                intent.Source = "TerminalOrbit";
                intent.Branch = "(n/a)";
                intent.Body = traj.TerminalOrbitBody;
                intent.TerminalBody = traj.TerminalOrbitBody;
                intent.TerminalSma = traj.TerminalOrbitSemiMajorAxis;
                intent.TerminalEcc = traj.TerminalOrbitEccentricity;
                ParsekLog.Verbose(Tag, BuildGhostMapDecisionLine(intent));
            }

            string logContext = string.Format(ic, "recording index={0}", recordingIndex);
            Vessel vessel = BuildAndLoadGhostProtoVessel(traj, logContext);
            if (vessel != null)
            {
                TrackRecordingGhostVessel(recordingIndex, traj, vessel);
                lifecycleCreatedThisTick++;

                Vector3d worldPos = vessel.GetWorldPos3D();
                string body = vessel.orbitDriver?.referenceBody?.name ?? traj.TerminalOrbitBody;

                var done = NewDecisionFields("create-terminal-orbit-done");
                done.RecordingId = traj.RecordingId;
                done.RecordingIndex = recordingIndex;
                done.VesselName = traj.VesselName;
                done.Source = "TerminalOrbit";
                done.Branch = "(n/a)";
                done.Body = body;
                done.WorldPos = worldPos;
                done.GhostPid = vessel.persistentId;
                done.TerminalBody = traj.TerminalOrbitBody;
                done.TerminalSma = traj.TerminalOrbitSemiMajorAxis;
                done.TerminalEcc = traj.TerminalOrbitEccentricity;
                ParsekLog.Info(Tag, BuildGhostMapDecisionLine(done));

                StashLastKnownFrame(recordingIndex, new LastKnownGhostFrame
                {
                    RecordingId = traj.RecordingId,
                    VesselName = traj.VesselName,
                    GhostPid = vessel.persistentId,
                    Source = "TerminalOrbit",
                    Branch = "(n/a)",
                    Body = body,
                    WorldPos = worldPos,
                    AnchorPid = 0u,
                    LastUT = double.NaN
                });
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
            int recordingIndex, IPlaybackTrajectory traj, OrbitSegment segment,
            double loopEpochShiftSeconds = 0.0)
        {
            return CreateGhostVesselFromSegment(
                recordingIndex,
                traj,
                segment,
                TrackingStationGhostSource.Segment,
                loopEpochShiftSeconds);
        }

        private static Vessel CreateGhostVesselFromSegment(
            int recordingIndex,
            IPlaybackTrajectory traj,
            OrbitSegment segment,
            TrackingStationGhostSource source,
            double loopEpochShiftSeconds = 0.0)
        {
            if (traj == null) return null;
            string sourceLabel = source == TrackingStationGhostSource.EndpointTail
                ? "EndpointTail"
                : "Segment";
            string protoSource = source == TrackingStationGhostSource.EndpointTail
                ? "endpoint-tail"
                : "visible-segment";

            // Phase 7 session-suppression gate (design §3.3).
            if (IsSuppressedByActiveSession(recordingIndex))
            {
                RemoveGhostVesselForRecording(recordingIndex, "session-suppressed subtree");
                return null;
            }

            if (vesselsByRecordingIndex.ContainsKey(recordingIndex))
                return vesselsByRecordingIndex[recordingIndex];

            // Intent-line: full input shape before BuildAndLoadGhostProtoVessel.
            {
                var intent = NewDecisionFields("create-segment-intent");
                intent.RecordingId = traj.RecordingId;
                intent.RecordingIndex = recordingIndex;
                intent.VesselName = traj.VesselName;
                intent.Source = sourceLabel;
                intent.Branch = "(n/a)";
                intent.Body = segment.bodyName;
                intent.Segment = segment;
                ParsekLog.Verbose(Tag, BuildGhostMapDecisionLine(intent));
            }

            string logContext = string.Format(ic,
                "recording index={0} (from {1})",
                recordingIndex,
                protoSource);
            Vessel vessel = BuildAndLoadGhostProtoVessel(
                traj, segment, logContext, protoSource, loopEpochShiftSeconds);
            if (vessel != null)
            {
                TrackRecordingGhostVessel(recordingIndex, traj, vessel);
                ghostOrbitBounds[vessel.persistentId] = (segment.startUT, segment.endUT);
                lifecycleCreatedThisTick++;

                Vector3d worldPos = vessel.GetWorldPos3D();

                var done = NewDecisionFields("create-segment-done");
                done.RecordingId = traj.RecordingId;
                done.RecordingIndex = recordingIndex;
                done.VesselName = traj.VesselName;
                done.Source = sourceLabel;
                done.Branch = "(n/a)";
                done.Body = segment.bodyName;
                done.WorldPos = worldPos;
                done.GhostPid = vessel.persistentId;
                done.Segment = segment;
                ParsekLog.Info(Tag, BuildGhostMapDecisionLine(done));

                StashLastKnownFrame(recordingIndex, new LastKnownGhostFrame
                {
                    RecordingId = traj.RecordingId,
                    VesselName = traj.VesselName,
                    GhostPid = vessel.persistentId,
                    Source = sourceLabel,
                    Branch = "(n/a)",
                    Body = segment.bodyName,
                    WorldPos = worldPos,
                    AnchorPid = 0u,
                    LastUT = segment.startUT
                });
            }

            return vessel;
        }

        /// <summary>
        /// PURE: should a ghost removal emit the positive "orbit proto retired AT its terminal/deorbit
        /// bound" assertion, and (if so) the overshoot past that bound? Fires only for a segment-driven
        /// orbit proto (<paramref name="hadOrbitBounds"/>) retired at a TERMINAL orbit handoff - the flight
        /// <c>left-orbit-segments</c> / tracking-station <c>tracking-station-expired</c> reasons, where
        /// effUT ran off the last recorded OrbitSegment with none ahead (e.g. a parking-orbit proto yielding
        /// to the descent polyline at the deorbit). <paramref name="overshootSeconds"/> = liveUT - boundEndUT
        /// (both in the live/loop-shifted frame): ~0 positively confirms the segment drive was clamped at
        /// the bound and the proto was retired there rather than propagating past the deorbit; a large value
        /// would flag a genuine overshoot. Replaces the old "prove by ABSENCE of past-window lines + the
        /// proto being gone" inference with a single positive line. Pure / xUnit-testable.
        /// </summary>
        internal static bool ShouldAssertTerminalOrbitBoundClamp(
            string reason, bool hadOrbitBounds, double boundEndUT, double liveUT, out double overshootSeconds)
        {
            overshootSeconds = double.NaN;
            if (!hadOrbitBounds)
                return false;
            if (!string.Equals(reason, "left-orbit-segments", StringComparison.Ordinal)
                && !string.Equals(reason, "tracking-station-expired", StringComparison.Ordinal))
                return false;
            if (double.IsNaN(boundEndUT) || double.IsInfinity(boundEndUT)
                || double.IsNaN(liveUT) || double.IsInfinity(liveUT))
                return false;
            overshootSeconds = liveUT - boundEndUT;
            return true;
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

            // Capture the segment-drive orbit bound BEFORE the dictionary purge below so a terminal
            // handoff (parking-orbit proto yielding to the descent at the deorbit) can positively assert
            // the drive was clamped at the bound (see ShouldAssertTerminalOrbitBoundClamp).
            bool hadOrbitBounds = ghostOrbitBounds.TryGetValue(ghostPid, out var removedOrbitBounds);
            double removedBoundEndUT = hadOrbitBounds ? removedOrbitBounds.endUT : double.NaN;

            // Snapshot last-known frame before Die() destroys the vessel.
            bool hadLastKnown = TryGetLastKnownFrame(recordingIndex, out LastKnownGhostFrame last);

            BeginGhostTeardown();
            try { vessel.Die(); }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "RemoveGhostVesselForRecording: Die() threw for index={0}: {1}",
                        recordingIndex, ex.Message));
            }
            finally
            {
                EndGhostTeardown();
            }

            ghostMapVesselPids.Remove(ghostPid);
            ghostsWithSuppressedIcon.Remove(ghostPid);
            ghostNoBoundsSuppressLastFrame.Remove(ghostPid);
            ghostOrbitBounds.Remove(ghostPid);
            ghostBodyFrameOrbitBounds.Remove(ghostPid);
            ghostLastAppliedOrbitBody.Remove(ghostPid);
            ghostLastAppliedOrbitElements.Remove(ghostPid);
            ghostOrbitLoopShiftedPids.Remove(ghostPid);
            ghostOrbitEpochShift.Remove(ghostPid);
            ghostIconDrivePropagation.Remove(ghostPid);
            ghostParkingConicLineHoldUntilUT.Remove(ghostPid);
            vesselPidToRecordingIndex.Remove(ghostPid);
            vesselPidToRecordingId.Remove(ghostPid);
            vesselsByRecordingIndex.Remove(recordingIndex);
            activeReFlyDeferredStateVectorGhostSessions.Remove(recordingIndex);
            trackingStationStateVectorOrbitTrajectories.Remove(recordingIndex);
            trackingStationStateVectorCachedIndices.Remove(recordingIndex);
            lastKnownByRecordingIndex.Remove(recordingIndex);
            lifecycleDestroyedThisTick++;

            var destroy = NewDecisionFields("destroy");
            destroy.RecordingId = hadLastKnown ? last.RecordingId : null;
            destroy.RecordingIndex = recordingIndex;
            destroy.VesselName = hadLastKnown ? last.VesselName : null;
            destroy.Source = hadLastKnown ? last.Source : "None";
            destroy.Branch = hadLastKnown ? last.Branch : "(n/a)";
            destroy.Body = hadLastKnown ? last.Body : null;
            destroy.WorldPos = hadLastKnown ? (Vector3d?)last.WorldPos : null;
            destroy.GhostPid = ghostPid;
            destroy.AnchorPid = hadLastKnown ? last.AnchorPid : 0u;
            destroy.UT = hadLastKnown ? last.LastUT : double.NaN;
            destroy.Reason = reason ?? "(none)";
            ParsekLog.Info(Tag, BuildGhostMapDecisionLine(destroy));

            // Positive deorbit-clamp assertion: when a segment-driven orbit proto is retired at its
            // terminal orbit handoff (e.g. a parking-orbit proto yielding to the descent polyline at the
            // deorbit), log that the drive was clamped AT the last recorded orbit's end bound rather than
            // driving past-window past it. Makes "the proto did not overshoot the deorbit" a positive,
            // greppable line instead of an inference from the ABSENCE of past-window drive lines.
            double removeLiveUT = CurrentUTNow();
            if (ShouldAssertTerminalOrbitBoundClamp(
                    reason, hadOrbitBounds, removedBoundEndUT, removeLiveUT, out double boundOvershootSeconds))
            {
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Orbit proto retired AT terminal orbit bound: idx={0} pid={1} reason={2} "
                        + "boundEndUT={3:F1} liveUT={4:F1} overshoot={5:F1}s (segment drive clamped at the "
                        + "last recorded orbit's end; proto retired here, NOT driven past-window past the "
                        + "bound - e.g. a parking-orbit proto yielding to the descent at the deorbit)",
                        recordingIndex, ghostPid, reason, removedBoundEndUT, removeLiveUT,
                        boundOvershootSeconds));
            }

            // MapRenderTrace Tier-A: structural GhostDestroyed event, keyed by the
            // live ghost persistentId. Gated at the call site; reads only fields
            // already snapshotted above (the vessel is dead by this point).
            if (MapRenderTrace.IsEnabled)
            {
                double destroyUT = hadLastKnown ? last.LastUT : CurrentUTNow();
                MapRenderTrace.EmitStructural(
                    "GhostDestroyed",
                    MapRenderTrace.RenderSurface.ProtoIcon,
                    ghostPid.ToString(ic),
                    destroyUT,
                    destroyUT,
                    MapRenderTrace.DestroyWindowSeconds,
                    MapRenderTrace.BuildLifecycleDetails(
                        hadLastKnown ? last.VesselName : null,
                        hadLastKnown ? last.Body : null,
                        HighLogic.LoadedScene.ToString(),
                        hadLastKnown ? (Vector3d?)last.WorldPos : null,
                        // Carry the recordingId so a GhostDestroyed line is greppable back to its recording
                        // (and thus to the [ReaimDescent] descent-member lines and the always-on [GhostMap]
                        // destroy decision line, which already names the recordingId + the descent-member tag).
                        string.Format(ic, "{0} index={1} rec={2}", reason ?? "(none)", recordingIndex,
                            hadLastKnown ? (last.RecordingId ?? "<none>") : "<none>")));
            }
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

        internal static Vessel CreateGhostVesselFromSource(
            int recordingIndex,
            IPlaybackTrajectory traj,
            TrackingStationGhostSource source,
            OrbitSegment segment,
            TrajectoryPoint stateVectorPoint,
            double currentUT,
            double loopEpochShiftSeconds = 0.0)
        {
            return CreateGhostVesselFromSource(
                recordingIndex,
                traj,
                source,
                segment,
                stateVectorPoint,
                currentUT,
                out _,
                loopEpochShiftSeconds);
        }

        /// <summary>
        /// Overload that propagates <paramref name="retryLater"/> from
        /// <see cref="CreateGhostVesselFromStateVectors(int, IPlaybackTrajectory,
        /// TrajectoryPoint, double, out bool, bool, string)"/>. Non-state-vector
        /// branches always set it false. Callers that maintain a pending-map
        /// queue use this overload to decide whether to drop the pending entry
        /// on null return or keep it for the next tick (PR #574 review P2:
        /// retry-later semantics for transient map ghost creation misses).
        /// </summary>
        internal static Vessel CreateGhostVesselFromSource(
            int recordingIndex,
            IPlaybackTrajectory traj,
            TrackingStationGhostSource source,
            OrbitSegment segment,
            TrajectoryPoint stateVectorPoint,
            double currentUT,
            out bool retryLater,
            double loopEpochShiftSeconds = 0.0)
        {
            retryLater = false;
            // Dispatcher-line: which sub-create is about to fire? Single line
            // gives the post-hoc reader the routing decision.
            var dispatch = NewDecisionFields("create-dispatch");
            dispatch.RecordingId = traj?.RecordingId;
            dispatch.RecordingIndex = recordingIndex;
            dispatch.VesselName = traj?.VesselName;
            dispatch.Source = source.ToString();
            dispatch.Branch = "(n/a)";
            dispatch.UT = currentUT;
            switch (source)
            {
                case TrackingStationGhostSource.Segment:
                case TrackingStationGhostSource.EndpointTail:
                    dispatch.Body = segment.bodyName;
                    dispatch.Segment = segment;
                    break;
                case TrackingStationGhostSource.StateVector:
                case TrackingStationGhostSource.StateVectorSoiGap:
                    dispatch.Body = stateVectorPoint.bodyName;
                    dispatch.StateVecAlt = stateVectorPoint.altitude;
                    dispatch.StateVecSpeed = stateVectorPoint.velocity.magnitude;
                    if (source == TrackingStationGhostSource.StateVectorSoiGap)
                        dispatch.Reason = SoiGapStateVectorFallbackReason;
                    break;
                case TrackingStationGhostSource.TerminalOrbit:
                    dispatch.Body = traj?.TerminalOrbitBody;
                    dispatch.TerminalBody = traj?.TerminalOrbitBody;
                    dispatch.TerminalSma = traj?.TerminalOrbitSemiMajorAxis ?? double.NaN;
                    dispatch.TerminalEcc = traj?.TerminalOrbitEccentricity ?? double.NaN;
                    break;
            }
            ParsekLog.Verbose(Tag, BuildGhostMapDecisionLine(dispatch));

            switch (source)
            {
                case TrackingStationGhostSource.TerminalOrbit:
                    return CreateGhostVesselForRecording(recordingIndex, traj);

                case TrackingStationGhostSource.Segment:
                    Vessel segmentGhost = CreateGhostVesselFromSegment(
                        recordingIndex, traj, segment, loopEpochShiftSeconds);
                    if (segmentGhost != null)
                    {
                        UpdateGhostOrbitForRecording(
                            recordingIndex,
                            segment,
                            loopEpochShiftSeconds: loopEpochShiftSeconds);
                        ForceImmediateIconDrive(segmentGhost, "create-segment");
                    }
                    return segmentGhost;

                case TrackingStationGhostSource.EndpointTail:
                    Vessel endpointTailGhost = CreateGhostVesselFromSegment(
                        recordingIndex,
                        traj,
                        segment,
                        TrackingStationGhostSource.EndpointTail,
                        loopEpochShiftSeconds);
                    if (endpointTailGhost != null)
                    {
                        UpdateGhostOrbitForRecording(
                            recordingIndex,
                            segment,
                            TrackingStationGhostSource.EndpointTail,
                            loopEpochShiftSeconds: loopEpochShiftSeconds);
                        ForceImmediateIconDrive(endpointTailGhost, "create-endpoint-tail");
                    }
                    return endpointTailGhost;

                case TrackingStationGhostSource.StateVector:
                case TrackingStationGhostSource.StateVectorSoiGap:
                    return CreateGhostVesselFromStateVectors(
                        recordingIndex,
                        traj,
                        stateVectorPoint,
                        currentUT,
                        out retryLater,
                        allowOrbitalCheckpointStateVector: source == TrackingStationGhostSource.StateVectorSoiGap,
                        stateVectorCreateReason: source == TrackingStationGhostSource.StateVectorSoiGap
                            ? SoiGapStateVectorFallbackReason
                            : null);

                default:
                    return null;
            }
        }

        /// <summary>
        /// Update orbit for a recording-index ghost when the ghost traverses orbit segments.
        /// </summary>
        internal static void UpdateGhostOrbitForRecording(
            int recordingIndex,
            OrbitSegment segment,
            TrackingStationGhostSource source = TrackingStationGhostSource.Segment,
            double loopEpochShiftSeconds = 0.0,
            List<OrbitSegment> effectiveOrbitSegments = null)
        {
            if (!vesselsByRecordingIndex.TryGetValue(recordingIndex, out Vessel vessel))
                return;
            ApplyOrbitToVessel(vessel, segment, string.Format(ic, "recording #{0}", recordingIndex),
                loopEpochShiftSeconds);
            // Diagnostic: which segment was actually applied to the map orbit (source + sma), and whether
            // the re-aimed list was threaded. A re-aim transfer should apply the synthesized sma; a
            // recorded sma here means the re-aimed segment was bypassed (e.g. a state-vector/checkpoint
            // source winning over the re-aimed OrbitSegment).
            ParsekLog.VerboseRateLimited("ReaimSeam", "map-apply-" + recordingIndex.ToString(ic),
                string.Format(ic, "map orbit applied: rec={0} source={1} body={2} segSma={3:F0} hasEffective={4}",
                    recordingIndex, source, segment.bodyName ?? "(null)", segment.semiMajorAxis,
                    effectiveOrbitSegments != null), 2.0);
            lifecycleUpdatedThisTick++;

            // Cache body-frame bounds (consecutive same-body OrbitSegments around this
            // segment, shifted into the live frame) so GhostOrbitLinePatch can keep
            // line.active True across inter-segment burns in one body frame. Resolved
            // from the recording's raw OrbitSegments at the segment's raw start UT, then
            // shifted by the same loop epoch offset applied to ghostOrbitBounds above,
            // keeping the loop-shifted edge cases identical to per-segment arc clipping.
            var committed = RecordingStore.CommittedRecordings;
            // Re-aim: a re-aim owner's body-frame bounds must come from the SAME re-aimed segment list
            // the orbit was applied from (effectiveOrbitSegments), not the recorded segments, so the
            // orbit-line draw window matches the re-aimed arc. Null for non-re-aim => recorded segments.
            List<OrbitSegment> bodyFrameSource = effectiveOrbitSegments
                ?? (committed != null && recordingIndex >= 0 && recordingIndex < committed.Count
                    && committed[recordingIndex] != null
                    ? committed[recordingIndex].OrbitSegments
                    : null);
            if (bodyFrameSource != null
                && bodyFrameSource.Count > 0
                && TrajectoryMath.TryGetBodyFrameBoundsForMapDisplay(
                    bodyFrameSource,
                    // Probe just inside the active segment so the resolver always finds
                    // it (endUT is exclusive for non-last segments).
                    segment.startUT,
                    out double bfStart,
                    out double bfEnd,
                    out string bfBody,
                    out int bfFirstIdx,
                    out int bfLastIdx))
            {
                ghostBodyFrameOrbitBounds[vessel.persistentId] =
                    (bfStart + loopEpochShiftSeconds, bfEnd + loopEpochShiftSeconds);
                ParsekLog.VerboseOnChange(Tag,
                    string.Format(ic, "body-frame-cache|{0}", vessel.persistentId),
                    string.Format(ic, "{0}|{1:F3}-{2:F3}|shift={3:F3}",
                        bfBody ?? "(null)", bfStart, bfEnd, loopEpochShiftSeconds),
                    string.Format(ic,
                        "Cached body-frame bounds pid={0} recIndex={1} body={2} " +
                        "rawBodyFrameUT={3:F2}-{4:F2} segIndices={5}-{6} loopShift={7:F2}",
                        vessel.persistentId,
                        recordingIndex,
                        bfBody ?? "(null)",
                        bfStart,
                        bfEnd,
                        bfFirstIdx,
                        bfLastIdx,
                        loopEpochShiftSeconds));
            }
            else
            {
                // Fallback: the resolver couldn't seed (e.g., orbit-segments missing in
                // a degenerate test scenario, or an EndpointTail synthetic segment whose
                // startUT is not in the committed OrbitSegments). Use the single-segment
                // bounds so the patch still has something to gate on rather than tearing
                // the line down. Same key as the success branch so the cache write
                // transitions visibly between body-frame and single-segment fallback.
                ghostBodyFrameOrbitBounds[vessel.persistentId] =
                    (segment.startUT + loopEpochShiftSeconds, segment.endUT + loopEpochShiftSeconds);
                ParsekLog.VerboseOnChange(Tag,
                    string.Format(ic, "body-frame-cache|{0}", vessel.persistentId),
                    string.Format(ic, "fallback|{0:F3}-{1:F3}|shift={2:F3}",
                        segment.startUT, segment.endUT, loopEpochShiftSeconds),
                    string.Format(ic,
                        "Cached body-frame bounds pid={0} recIndex={1} source=single-segment-fallback " +
                        "rawSegmentUT={2:F2}-{3:F2} loopShift={4:F2}",
                        vessel.persistentId,
                        recordingIndex,
                        segment.startUT,
                        segment.endUT,
                        loopEpochShiftSeconds));
            }

            Vector3d worldPos = vessel.GetWorldPos3D();
            bool hasPrev = TryGetLastKnownFrame(recordingIndex, out var prev);
            string recId = hasPrev ? prev.RecordingId : null;
            string vesselName = prev.VesselName;
            string updateKey = string.Format(ic, "rec-orbit-update-{0}",
                recId ?? recordingIndex.ToString(ic));
            string sourceLabel = source == TrackingStationGhostSource.EndpointTail
                ? "EndpointTail"
                : "Segment";

            var done = NewDecisionFields("update-segment");
            done.RecordingId = recId;
            done.RecordingIndex = recordingIndex;
            done.VesselName = vesselName;
            done.Source = sourceLabel;
            done.Branch = "(n/a)";
            done.Body = segment.bodyName;
            done.WorldPos = worldPos;
            done.GhostPid = vessel.persistentId;
            done.Segment = segment;
            ParsekLog.VerboseRateLimited(Tag, updateKey, BuildGhostMapDecisionLine(done), 5.0);

            // The per-frame icon-pos-delta "frozen icon" stall diagnostic that used to live here is
            // now subsumed by the gated MapRenderProbe (per-pid per-frame world-position jump tracking
            // + Tier-B body-orbit on-change truth), behind the mapRenderTracing setting. See
            // docs/dev/design-map-ts-render-tracer.md.

            // Refresh last-known so destroy can read the current orbit shape.
            StashLastKnownFrame(recordingIndex, new LastKnownGhostFrame
            {
                RecordingId = recId,
                VesselName = vesselName,
                GhostPid = vessel.persistentId,
                Source = sourceLabel,
                Branch = "(n/a)",
                Body = segment.bodyName,
                WorldPos = worldPos,
                AnchorPid = 0u,
                LastUT = segment.startUT
            });
        }

        private static void ApplyOrbitToVessel(Vessel vessel, OrbitSegment segment, string logContext,
            double loopEpochShiftSeconds = 0.0)
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

            // SOI transition: compare against the body PARSEK last applied to this ghost, NOT
            // vessel.orbitDriver.referenceBody. KSP re-transitions an unloaded ghost's referenceBody
            // between our per-frame reseeds (escape hyperbola kicked out to the parent star; a
            // center-aimed transfer's endpoints fall inside the launch / target SOI), so the driver-field
            // compare trips every frame near an SOI boundary and toggles the renderer off/on each frame
            // -> the orbit line blinks at the start / end of a transfer leg. (OrbitDriver.celestialBody
            // must stay null for vessel targets; stock OrbitTargeter treats a non-null celestialBody on a
            // target driver as a body target and drops same-body ghosts.)
            ghostLastAppliedOrbitBody.TryGetValue(vessel.persistentId, out string lastAppliedBody);
            bool soiChanged = GhostOrbitBodyChanged(lastAppliedBody, body.name);
            if (soiChanged)
            {
                ParsekLog.Info(Tag,
                    string.Format(ic, "SOI change for {0} — new body={1}", logContext, body.name));
            }

            // Direct element assignment via SetOrbit — bypasses the lossy
            // state-vector roundtrip in UpdateFromOrbitAtUT (#172).
            //
            // RAW recorded epoch (no shift baked in): the loop-mapped sample clock (effUT) can
            // advance FASTER than the live Planetarium clock (e.g. under time warp, the loop maps
            // a short live span to a huge recorded span). Stock OrbitDriver propagates the icon at
            // the LIVE clock every FixedUpdate, so a shifted epoch only lands the right phase at
            // the instant the (rate-limited) reseed re-snaps the shift; between reseeds the live
            // clock is too slow and the icon stalls on a near-fixed anomaly until the next reseed
            // jumps it forward (the frozen-icon-on-short-arc sawtooth). Instead we seed the raw
            // epoch and record the loop shift in ghostOrbitEpochShift; GhostOrbitIconDrivePatch
            // drives the OrbitDriver at effUT = liveUT - shift every frame so the icon glides at
            // the effUT rate (matching the mesh path and the wide-segment case), and
            // GhostOrbitArcPatch evaluates its arc bounds at the same effUT so the line stays in
            // lockstep. The stored arc bounds stay in the LIVE frame (shifted) so the line-toggle
            // Postfix + AnyGhostHeadLeftAppliedSegment comparisons (which use the live clock) are
            // byte-identical to before; the two prefixes subtract the shift where they need the
            // recorded clock.
            //
            // NOTE: a loop-shift body-fixed LAN rotation was tried here (rotate the node by the body's
            // rotation over the shift so the inertial orbit projects to its recorded body-fixed
            // appearance, to close the parking->loiter line-vs-raise desync). It was REVERTED: it
            // rotates ONLY the map OrbitDriver's orbit (line + map icon), not the flight-scene ghost
            // mesh, the recorded trajectory points, or the other stages' rendering, so the rotated
            // line ended up inconsistent with everything else (the ghost on its old recorded path, the
            // other stage's trajectory in the wrong place, the hyperbolic escape line rotated off its
            // own icon). The transformation would have to apply to the entire ghost rendering
            // consistently to be valid; rotating one element in isolation breaks more than it fixes.
            // See docs/dev/todo-and-known-bugs.md.
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

            // Record the per-frame icon clock state BEFORE the first propagation so the
            // OrbitDriver tick that updateFromParameters() triggers reads the right shift + bounds
            // through GhostOrbitIconDrivePatch (otherwise that in-call frame would propagate at the
            // live clock against stale/absent bounds for one frame).
            ghostOrbitEpochShift[vessel.persistentId] = loopEpochShiftSeconds;

            // Store orbit segment time bounds for arc clipping (GhostOrbitArcPatch) + the
            // line-toggle Postfix, shifted into the LIVE frame for loop replay so the live-clock
            // comparisons in those consumers are unchanged. The arc-clip + icon-drive patches
            // subtract the shift (via ghostOrbitEpochShift) to recover the recorded UTs for their
            // eccentric-anomaly / propagation math against the raw-epoch orbit.
            ghostOrbitBounds[vessel.persistentId] =
                (segment.startUT + loopEpochShiftSeconds, segment.endUT + loopEpochShiftSeconds);
            // Mark/unmark this ghost as loop-shifted so TryGetVisibleOrbitBoundsForGhostVessel
            // returns these (shifted) bounds rather than re-deriving raw recorded bounds from the
            // OrbitSegments at the live UT, which would desync the arc/icon clip from the live
            // frame when the live clock falls inside the member's recorded window.
            if (loopEpochShiftSeconds != 0.0)
                ghostOrbitLoopShiftedPids.Add(vessel.persistentId);
            else
                ghostOrbitLoopShiftedPids.Remove(vessel.persistentId);

            vessel.orbitDriver.updateFromParameters();
            NormalizeGhostOrbitDriverTargetIdentity(vessel, logContext);

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

            // Remember the body we just applied so the next frame's soiChanged compares against it
            // (KSP may transiently re-transition orbitDriver.referenceBody before then).
            ghostLastAppliedOrbitBody[vessel.persistentId] = body.name;

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

            // This apply runs every frame the orbit is re-applied (per ghost), so an unconditional
            // Verbose floods the log (it was ~31% of all Parsek output in a 2-minute capture). Log ON
            // CHANGE per ghost instead, keyed on the orbit SHAPE (body + sma + ecc + renderer state):
            // every distinct orbit applied still logs, a frozen/steady orbit logs once and then reports a
            // suppressed=N count (which is clearer than thousands of identical lines for spotting a stuck
            // orbit), and SOI/body changes and renderer flips register as discrete events. No debugging
            // signal is lost; the per-frame repeat noise is. Ghost create/recreate is still logged loudly
            // at INFO ("Created ghost vessel" / "create-segment-done"). sma:F0/ecc:F4 in the key are coarse
            // enough to avoid float-jitter churn (these come from a fixed segment, so they are stable).
            ParsekLog.VerboseOnChange(Tag,
                "orbit-applied-" + vessel.persistentId.ToString(ic),
                string.Format(ic, "{0}|{1:F0}|{2:F4}|{3}|{4}",
                    body.name, segment.semiMajorAxis, drv.eccentricity,
                    vessel.orbitRenderer?.enabled, vessel.orbitRenderer?.drawMode),
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

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static void TrackRecordingGhostVessel(
            int recordingIndex,
            IPlaybackTrajectory traj,
            Vessel vessel)
        {
            if (vessel == null)
                return;

            vesselsByRecordingIndex[recordingIndex] = vessel;
            vesselPidToRecordingIndex[vessel.persistentId] = recordingIndex;

            string recordingId = traj?.RecordingId;
            if (!string.IsNullOrEmpty(recordingId))
                vesselPidToRecordingId[vessel.persistentId] = recordingId;
            else
                vesselPidToRecordingId.Remove(vessel.persistentId);
        }

        internal static void TrackRecordingGhostIdentityForTesting(
            uint ghostPid,
            int recordingIndex,
            string recordingId)
        {
            vesselPidToRecordingIndex[ghostPid] = recordingIndex;
            if (!string.IsNullOrEmpty(recordingId))
                vesselPidToRecordingId[ghostPid] = recordingId;
            else
                vesselPidToRecordingId.Remove(ghostPid);
        }

        internal static void TrackEndpointTailGhostBoundsForTesting(
            uint ghostPid,
            int recordingIndex,
            string recordingId,
            string bodyName,
            double startUT,
            double endUT)
        {
            TrackRecordingGhostIdentityForTesting(ghostPid, recordingIndex, recordingId);
            ghostOrbitBounds[ghostPid] = (startUT, endUT);
            StashLastKnownFrame(recordingIndex, new LastKnownGhostFrame
            {
                RecordingId = recordingId,
                VesselName = "(test)",
                GhostPid = ghostPid,
                Source = "EndpointTail",
                Branch = "(n/a)",
                Body = bodyName,
                WorldPos = default(Vector3d),
                AnchorPid = 0u,
                LastUT = startUT
            });
        }

        /// <summary>
        /// Find a CelestialBody by name without LINQ allocation.
        /// Returns null if FlightGlobals.Bodies is null or name not found.
        /// </summary>
        private static CelestialBody FindBodyByName(string bodyName)
        {
            if (FindBodyByNameForTesting != null)
            {
                CelestialBody testBody = FindBodyByNameForTesting(bodyName);
                if (!object.ReferenceEquals(testBody, null))
                    return testBody;
            }

            var bodies = FlightGlobals.Bodies;
            if (bodies == null) return null;
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i].name == bodyName || bodies[i].bodyName == bodyName) return bodies[i];
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
            IPlaybackTrajectory traj,
            OrbitSegment segment,
            string logContext,
            string protoSource = "visible-segment",
            double loopEpochShiftSeconds = 0.0)
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

            // Bug 3 (creation-frame icon-off-orbit): BAKE the loop epoch shift into the orbit the proto is
            // LOADED with. ProtoVessel.Load propagates the packed icon at the LIVE Planetarium clock against
            // the node's epoch; with the raw recorded epoch (loopEpochShiftSeconds=0) that lands the icon
            // shift-worth-of-mean-anomaly off the recorded phase at creation, snapping onto the line only on
            // the next physics tick (off EVERY frame at warp, where the proto is recreated each frame).
            // epoch = segment.epoch + shift makes pv.Load's live-clock propagation land on the recorded
            // phase at creation, with no settle pass. The per-frame steady-state drive
            // (StockConicTreatment.SeedAndDriveLive) re-seeds the whole orbit from the RAW segment.epoch +
            // shift every FixedUpdate, so the baked node epoch governs ONLY the single creation frame and
            // never compounds. Non-loop / terminal / state-vector ghosts pass shift 0 -> byte-identical to
            // before. The recorded OrbitSegment is NOT mutated; only this live Orbit instance carries the bake.
            Orbit orbit = new Orbit(
                segment.inclination,
                segment.eccentricity,
                segment.semiMajorAxis,
                segment.longitudeOfAscendingNode,
                segment.argumentOfPeriapsis,
                segment.meanAnomalyAtEpoch,
                segment.epoch + loopEpochShiftSeconds,
                body);

            return BuildAndLoadGhostProtoVesselCore(
                traj,
                orbit,
                body,
                logContext,
                string.IsNullOrEmpty(protoSource) ? "visible-segment" : protoSource,
                string.Format(ic,
                    "segmentBody={0} segmentUT={1:F1}-{2:F1}",
                    segment.bodyName ?? "(null)",
                    segment.startUT,
                    segment.endUT));
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
                out string orbitBodyName,
                out GhostProtoOrbitSeedDiagnostics seedDiagnostics))
            {
                ParsekLog.Error(Tag,
                    string.Format(ic,
                        "BuildAndLoadGhostProtoVessel: no endpoint-aligned orbit seed for {0} " +
                        "seedFailure={1} endpointBody={2} seedFallback={3} {4}",
                        logContext,
                        seedDiagnostics.FailureReason ?? "(none)",
                        seedDiagnostics.EndpointBodyName ?? "(none)",
                        seedDiagnostics.FallbackReason ?? "(none)",
                        FormatGhostProtoOrbitSeedDiagnostics(seedDiagnostics)));
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

            return BuildAndLoadGhostProtoVesselCore(
                traj,
                orbit,
                body,
                logContext,
                string.IsNullOrEmpty(seedDiagnostics.Source) ? "terminal-orbit" : seedDiagnostics.Source,
                string.Format(ic,
                    "seedBody={0} endpointBody={1} seedFallback={2} {3}",
                    orbitBodyName ?? "(null)",
                    seedDiagnostics.EndpointBodyName ?? "(none)",
                    seedDiagnostics.FallbackReason ?? "(none)",
                    FormatGhostProtoOrbitSeedDiagnostics(seedDiagnostics)));
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
            return TryResolveGhostProtoOrbitSeed(
                traj,
                out inclination,
                out eccentricity,
                out semiMajorAxis,
                out lan,
                out argumentOfPeriapsis,
                out meanAnomalyAtEpoch,
                out epoch,
                out bodyName,
                out _);
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
            out string bodyName,
            out GhostProtoOrbitSeedDiagnostics diagnostics)
        {
            // PREDICTED-TAIL defect B: the endpoint seed walks the recording's OWN segments, and a
            // chain HEAD has none - the scene-exit predicted tail lives on the TIP. Hand it the
            // effective tip's list so `endpoint-segment` can seed the ghost instead of the resolver
            // falling through to `state-vector-fallback`. Null for every non-chain recording.
            List<OrbitSegment> seedChainTipSegments = ResolveMapPresenceChainSegments(
                traj, ResolveLiveChainTipSegments, out string seedTipId, out _);
            if (string.IsNullOrEmpty(seedTipId)) seedChainTipSegments = null;

            if (RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed(
                traj,
                out inclination,
                out eccentricity,
                out semiMajorAxis,
                out lan,
                out argumentOfPeriapsis,
                out meanAnomalyAtEpoch,
                out epoch,
                out bodyName,
                out RecordingEndpointResolver.EndpointOrbitSeedDiagnostics endpointDiagnostics,
                seedChainTipSegments))
            {
                diagnostics = new GhostProtoOrbitSeedDiagnostics
                {
                    Source = endpointDiagnostics.Source,
                    EndpointBodyName = endpointDiagnostics.EndpointBodyName,
                    FailureReason = null,
                    FallbackReason = null,
                    TailUT = double.NaN,
                    TailSma = double.NaN,
                    TailEcc = double.NaN,
                    LatestSegmentEndUT = double.NaN,
                    RotationDriftSeconds = double.NaN
                };
                PopulateTailSeedDiagnostics(traj, double.NaN, ref diagnostics);
                return true;
            }

            bool fallbackResolved = TryResolveTerminalOrbitGhostSeed(
                traj,
                out inclination,
                out eccentricity,
                out semiMajorAxis,
                out lan,
                out argumentOfPeriapsis,
                out meanAnomalyAtEpoch,
                out epoch,
                out bodyName);

            diagnostics = new GhostProtoOrbitSeedDiagnostics
            {
                Source = fallbackResolved ? "terminal-orbit" : "none",
                EndpointBodyName = endpointDiagnostics.EndpointBodyName,
                FailureReason = fallbackResolved
                    ? null
                    : (endpointDiagnostics.FailureReason ?? TrackingStationGhostSkipUnseedableTerminalOrbit),
                FallbackReason = endpointDiagnostics.FailureReason,
                TailUT = double.NaN,
                TailSma = double.NaN,
                TailEcc = double.NaN,
                LatestSegmentEndUT = double.NaN,
                RotationDriftSeconds = double.NaN
            };

            if (fallbackResolved && string.IsNullOrEmpty(diagnostics.EndpointBodyName)
                && RecordingEndpointResolver.TryGetPreferredEndpointBodyName(traj, out string preferredEndpointBody))
            {
                diagnostics.EndpointBodyName = preferredEndpointBody;
            }

            PopulateTailSeedDiagnostics(traj, double.NaN, ref diagnostics);
            return fallbackResolved;
        }

        private static void PopulateTailSeedDiagnostics(
            IPlaybackTrajectory traj,
            double currentUT,
            ref GhostProtoOrbitSeedDiagnostics diagnostics)
        {
            diagnostics.TailSeedConsidered = false;
            diagnostics.TailSeedAccepted = false;
            diagnostics.TailDeclineReason = null;
            diagnostics.TailFrameSource = null;

            if (!RecordingEndpointResolver.TryGetPreferredEndpointBodyName(
                    traj,
                    out string preferredEndpointBody))
            {
                return;
            }

            diagnostics.EndpointBodyName = diagnostics.EndpointBodyName ?? preferredEndpointBody;
            diagnostics.TailSeedConsidered = true;
            if (OrbitSeedResolver.TailSeedResolverForTesting == null
                && !OrbitSeedResolver.TryFindLatestCoastTrajectoryFrame(
                    traj,
                    preferredEndpointBody,
                    out _,
                    out _))
            {
                diagnostics.TailDeclineReason = "no-absolute-coast-tail";
                return;
            }

            CelestialBody body = FindBodyByName(preferredEndpointBody);
            if (body == null)
            {
                diagnostics.TailDeclineReason = "body-not-found";
                return;
            }

            bool accepted = OrbitSeedResolver.TryDeriveTailOrbitSeed(
                traj,
                body,
                currentUT,
                TailSeedUse.MapPresence,
                out TailDerivedOrbitSeed seed);
            diagnostics.TailSeedAccepted = accepted;
            diagnostics.TailDeclineReason = seed.DeclineReason;
            diagnostics.TailUT = seed.TailUT;
            diagnostics.TailSma = seed.Segment.semiMajorAxis;
            diagnostics.TailEcc = seed.Segment.eccentricity;
            diagnostics.LatestSegmentEndUT = seed.LatestStoredSegmentEndUT;
            diagnostics.RotationDriftSeconds = seed.RotationDriftSeconds;
            diagnostics.TailFrameSource = seed.TailFrameSource;
        }

        private static string FormatGhostProtoOrbitSeedDiagnostics(
            GhostProtoOrbitSeedDiagnostics diagnostics)
        {
            return string.Format(ic,
                "tailConsidered={0} tailAccepted={1} tailDecline={2} tailUT={3:F2} tailSma={4:F1} tailEcc={5:F6} latestSegmentEndUT={6:F2} drift={7:F2}s tailFrame={8}",
                diagnostics.TailSeedConsidered,
                diagnostics.TailSeedAccepted,
                diagnostics.TailDeclineReason ?? "(none)",
                diagnostics.TailUT,
                diagnostics.TailSma,
                diagnostics.TailEcc,
                diagnostics.LatestSegmentEndUT,
                diagnostics.RotationDriftSeconds,
                diagnostics.TailFrameSource ?? "(none)");
        }

        private static bool TryResolveTerminalOrbitGhostSeed(
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
            inclination = 0.0;
            eccentricity = 0.0;
            semiMajorAxis = 0.0;
            lan = 0.0;
            argumentOfPeriapsis = 0.0;
            meanAnomalyAtEpoch = 0.0;
            epoch = 0.0;
            bodyName = null;

            if (traj == null
                || string.IsNullOrEmpty(traj.TerminalOrbitBody)
                || traj.TerminalOrbitSemiMajorAxis <= 0.0)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(traj.EndpointBodyName)
                && traj.EndpointPhase != RecordingEndpointPhase.Unknown
                && !string.Equals(traj.EndpointBodyName, traj.TerminalOrbitBody, StringComparison.Ordinal))
            {
                return false;
            }

            if (RecordingEndpointResolver.TryGetPreferredEndpointBodyName(traj, out string endpointBodyName)
                && !string.IsNullOrEmpty(endpointBodyName)
                && !string.Equals(endpointBodyName, traj.TerminalOrbitBody, StringComparison.Ordinal))
            {
                return false;
            }

            if (traj.TerminalStateValue.HasValue)
            {
                TerminalState terminalState = traj.TerminalStateValue.Value;
                if (terminalState != TerminalState.Orbiting
                    && terminalState != TerminalState.SubOrbital
                    && terminalState != TerminalState.Docked)
                {
                    return false;
                }
            }

            inclination = traj.TerminalOrbitInclination;
            eccentricity = traj.TerminalOrbitEccentricity;
            semiMajorAxis = traj.TerminalOrbitSemiMajorAxis;
            lan = traj.TerminalOrbitLAN;
            argumentOfPeriapsis = traj.TerminalOrbitArgumentOfPeriapsis;
            meanAnomalyAtEpoch = traj.TerminalOrbitMeanAnomalyAtEpoch;
            epoch = traj.TerminalOrbitEpoch;
            bodyName = traj.TerminalOrbitBody;
            return true;
        }

        private static Vessel BuildAndLoadGhostProtoVesselCore(
            IPlaybackTrajectory traj,
            Orbit orbit,
            CelestialBody body,
            string logContext,
            string orbitSource = null,
            string orbitSourceDetail = null)
        {
            ProtoVessel pv = null;
            try
            {

                ConfigNode vesselNode = BuildGhostProtoVesselNode(
                    traj,
                    orbit,
                    out VesselType vtype,
                    out string vesselName);

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
                    RemoveGhostProtoVessel(pv, nullSafeFlightState: false);
                    return null;
                }

                // Log creation + OrbitDriver state for diagnostics (#172)
                Vessel v = pv.vesselRef;
                // KSP promotes a single-part root with PhysicsSignificance=1 back to
                // FULL, and the thermal explode site checks temperature > maxTemp
                // independent of physical significance. Without this pass the marker's
                // sensorBarometer (maxTemp=1200) overheats and Part.explode() destroys
                // the vessel mid-Re-Fly whenever the active vessel passes through dense
                // atmosphere at orbital speed (repro at logs/2026-05-19_1847_refly-
                // booster-explosion, log line 18:44:50.128).
                HardenGhostVesselPartPhysics(v, logContext);
                NormalizeGhostOrbitDriverTargetIdentity(v, logContext);
                string driverState = "no-orbitDriver";
                if (v.orbitDriver != null)
                {
                    Orbit drv = v.orbitDriver.orbit;

                    driverState = string.Format(ic,
                        "updateMode={0} sma={1:F0} ecc={2:F6} inc={3:F4} " +
                        "argPe={4:F4} mna={5:F6} epoch={6:F1} vesselPos=({7:F1},{8:F1},{9:F1}) {10} registered={11}",
                        v.orbitDriver.updateMode,
                        drv.semiMajorAxis, drv.eccentricity, drv.inclination,
                        drv.argumentOfPeriapsis, drv.meanAnomalyAtEpoch, drv.epoch,
                        v.GetWorldPos3D().x, v.GetWorldPos3D().y, v.GetWorldPos3D().z,
                        BuildGhostOrbitDriverIdentity(v),
                        IsVesselRegistered(v));
                }

                // Ensure OrbitRenderer is enabled — in Tracking Station, pv.Load()
                // may create the renderer in a disabled state.
                bool rendererForceEnabled = false;
                if (v.orbitRenderer != null)
                {
                    v.orbitRenderer.drawMode = OrbitRendererBase.DrawMode.REDRAW_AND_RECALCULATE;
                    v.orbitRenderer.drawIcons = OrbitRendererBase.DrawIcons.ALL;
                    if (!v.orbitRenderer.enabled)
                    {
                        rendererForceEnabled = true;
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

                string mapVisibilityState = BuildGhostProtoVesselVisibilityState(
                    v.mapObject != null,
                    v.orbitRenderer != null,
                    v.orbitRenderer != null && v.orbitRenderer.enabled,
                    v.orbitRenderer != null ? v.orbitRenderer.drawIcons.ToString() : "(none)",
                    ghostsWithSuppressedIcon.Contains(v.persistentId),
                    rendererForceEnabled);

                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Created ghost vessel '{0}' ghostPid={1} type={2} body={3} sma={4:F0} for {5} " +
                        "orbitSource={6}{7} | {8} {9} scene={10}",
                        vesselName, v.persistentId,
                        vtype, body.name, orbit.semiMajorAxis, logContext,
                        string.IsNullOrEmpty(orbitSource) ? "(unspecified)" : orbitSource,
                        string.IsNullOrEmpty(orbitSourceDetail) ? string.Empty : " " + orbitSourceDetail,
                        driverState,
                        mapVisibilityState,
                        HighLogic.LoadedScene));

                // MapRenderTrace Tier-A: structural GhostCreated event keyed by the
                // live ghost persistentId (the map world's native key). Gated at the
                // call site so no UT / world-position read and no string formatting
                // happens in normal (disabled) play; EmitStructural also early-returns
                // internally, matching the GhostRenderTrace emitter contract.
                if (MapRenderTrace.IsEnabled)
                {
                    double nowUT = CurrentUTNow();
                    MapRenderTrace.EmitStructural(
                        "GhostCreated",
                        MapRenderTrace.RenderSurface.ProtoIcon,
                        v.persistentId.ToString(ic),
                        nowUT,
                        nowUT,
                        MapRenderTrace.InitialWindowSeconds,
                        MapRenderTrace.BuildLifecycleDetails(
                            vesselName,
                            body.name,
                            HighLogic.LoadedScene.ToString(),
                            v.GetWorldPos3D(),
                            // Correlation key: carry the recordingId in the reason so a GhostCreated line is
                            // greppable back to its recording (the pid<->recordingId map is otherwise only in
                            // the separate vesselPidToRecordingId writes). Ties the ghost-create TRUTH to the
                            // descent DECISION ([ReaimDescent] is recordingId/member-keyed) for one grep.
                            "rec=" + (traj != null ? traj.RecordingId : "<none>") + " " + logContext));
                }

                return v;
            }
            catch (Exception ex)
            {
                if (pv != null)
                {
                    RemoveGhostProtoVessel(pv, nullSafeFlightState: true);
                }
                ParsekLog.Error(Tag,
                    string.Format(ic,
                        "BuildAndLoadGhostProtoVessel failed for {0}: {1}",
                        logContext, ex.Message));
                return null;
            }
        }

        private static ConfigNode BuildGhostProtoVesselNode(
            IPlaybackTrajectory traj,
            Orbit orbit,
            out VesselType vtype,
            out string vesselName)
        {
            // Single antenna-free part: the ghost's CommNet node is GhostCommNetManager's, not this marker's.
            // Aero/thermal tolerances are hardened post-load in HardenGhostVesselPartPhysics:
            // the partNode is loaded into a real Part with prefab maxTemp=1200, which
            // would otherwise overheat and explode the marker vessel during low-altitude
            // playback (see BuildAndLoadGhostProtoVesselCore).
            ConfigNode partNode = ProtoVessel.CreatePartNode("sensorBarometer", 0);

            // Discovery: fully visible, infinite lifetime
            ConfigNode discovery = ProtoVessel.CreateDiscoveryNode(
                DiscoveryLevels.Owned, UntrackedObjectClass.C,
                double.PositiveInfinity, double.PositiveInfinity);

            vtype = ResolveVesselType(traj.VesselSnapshot);
            vesselName = GhostVesselNamePrefix + (traj.VesselName ?? "Unknown");

            ConfigNode vesselNode = ProtoVessel.CreateVesselNode(
                vesselName, vtype, orbit, 0,
                new ConfigNode[] { partNode }, discovery);

            // Critical settings: prevent ground positioning and KSC cleanup
            vesselNode.SetValue("vesselSpawning", "False", true);
            vesselNode.SetValue("prst", "True", true);
            vesselNode.SetValue("cln", "False", true);

            EnsureDefensiveVesselNodes(vesselNode);

            return vesselNode;
        }

        /// <summary>
        /// Adds the sub-nodes that SpaceTracking.buildVesselsList and other KSP internals
        /// assume exist on a VESSEL node. ProtoVessel.CreateVesselNode adds ACTIONGROUPS
        /// but omits these three, and a missing node can NRE inside tracking-station code
        /// paths. Idempotent: an already-present node is left alone. Split out of
        /// the ghost ProtoVessel node builder so the insertion can be driven headlessly.
        /// </summary>
        internal static void EnsureDefensiveVesselNodes(ConfigNode vesselNode)
        {
            if (vesselNode == null) return;

            if (vesselNode.GetNode("FLIGHTPLAN") == null)
                vesselNode.AddNode("FLIGHTPLAN");
            if (vesselNode.GetNode("CTRLSTATE") == null)
                vesselNode.AddNode("CTRLSTATE");
            if (vesselNode.GetNode("VESSELMODULES") == null)
                vesselNode.AddNode("VESSELMODULES");
        }

        /// <summary>
        /// Override aero/thermal/structural tolerances on every part of a freshly-loaded
        /// ghost-owned ProtoVessel (map-presence marker, replay flag, future single-part
        /// ghost vessels) so the vessel behaves as a render-only presence rather than a
        /// physical body. Without this, KSP's FlightIntegrator runs aerothermal sim on
        /// the vessel (single-part root parts are promoted to PhysicalSignificance.FULL
        /// regardless of prefab settings) and the prefab maxTemp / crashTolerance values
        /// trigger Part.explode() when something hot or fast happens nearby.
        /// </summary>
        internal static int HardenGhostVesselPartPhysics(Vessel v, string logContext)
        {
            if (v == null || v.parts == null) return 0;
            int count = 0;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;
                p.maxTemp = double.PositiveInfinity;
                p.skinMaxTemp = double.PositiveInfinity;
                p.crashTolerance = float.PositiveInfinity;
                p.gTolerance = double.PositiveInfinity;
                p.breakingForce = float.PositiveInfinity;
                p.breakingTorque = float.PositiveInfinity;
                count++;
            }
            ParsekLog.Verbose(Tag, string.Format(ic,
                "Ghost vessel parts hardened: vessel='{0}' parts={1} for {2}",
                v.vesselName ?? "(null)", count, logContext));
            return count;
        }

        /// <summary>
        /// Called when KSP loads a ghost map vessel's parts (<c>Vessel.Load</c> postfix):
        /// hardens the now-real parts and disables every collider under them, so the
        /// render-only marker can neither explode nor collide with a real vessel that
        /// shares its position inside physics range.
        /// </summary>
        internal static void MakeLoadedGhostVesselPhysicsInert(Vessel v, string logContext)
        {
            if (v == null) return;
            int parts = HardenGhostVesselPartPhysics(v, logContext);
            int colliders = 0;
            if (v.parts != null)
            {
                for (int i = 0; i < v.parts.Count; i++)
                {
                    Part p = v.parts[i];
                    if (p == null) continue;
                    Collider[] cs = p.GetComponentsInChildren<Collider>(true);
                    for (int c = 0; c < cs.Length; c++)
                    {
                        if (cs[c] == null || !cs[c].enabled) continue;
                        cs[c].enabled = false;
                        colliders++;
                    }
                }
            }
            LogGhostVesselMadeInert(v.persistentId, v.vesselName, parts, colliders, logContext);
        }

        internal static void LogGhostVesselMadeInert(
            uint pid, string vesselName, int parts, int colliders, string logContext)
        {
            ParsekLog.Verbose(Tag, string.Format(ic,
                "Ghost vessel made physics-inert on load: vessel='{0}' pid={1} parts={2} collidersDisabled={3} for {4}",
                vesselName ?? "(null)", pid, parts, colliders, logContext));
        }

        private static void RemoveGhostProtoVessel(ProtoVessel pv, bool nullSafeFlightState)
        {
            ghostMapVesselPids.Remove(pv.persistentId);
            if (nullSafeFlightState)
                HighLogic.CurrentGame?.flightState?.protoVessels?.Remove(pv);
            else
                HighLogic.CurrentGame.flightState.protoVessels.Remove(pv);
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
