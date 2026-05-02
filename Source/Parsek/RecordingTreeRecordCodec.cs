using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    internal static class RecordingTreeRecordCodec
    {
        // --- Recording serialization helpers ---

        internal static void SaveRecordingInto(ConfigNode recNode, Recording rec)
        {
            var ic = CultureInfo.InvariantCulture;

            recNode.AddValue("recordingId", rec.RecordingId ?? "");
            recNode.AddValue("vesselName", rec.VesselName ?? "");
            if (rec.TreeId != null)
                recNode.AddValue("treeId", rec.TreeId);
            if (rec.TreeOrder >= 0)
                recNode.AddValue("treeOrder", rec.TreeOrder.ToString(ic));
            recNode.AddValue("vesselPersistentId", rec.VesselPersistentId.ToString(ic));

            if (!double.IsNaN(rec.ExplicitStartUT))
                recNode.AddValue("explicitStartUT", rec.ExplicitStartUT.ToString("R", ic));
            if (!double.IsNaN(rec.ExplicitEndUT))
                recNode.AddValue("explicitEndUT", rec.ExplicitEndUT.ToString("R", ic));

            if (rec.TerminalStateValue.HasValue)
                recNode.AddValue("terminalState", ((int)rec.TerminalStateValue.Value).ToString(ic));
            if (rec.EndpointPhase != RecordingEndpointPhase.Unknown)
                recNode.AddValue("endpointPhase", ((int)rec.EndpointPhase).ToString(ic));
            if (!string.IsNullOrEmpty(rec.EndpointBodyName))
                recNode.AddValue("endpointBodyName", rec.EndpointBodyName);

            if (rec.ParentBranchPointId != null)
                recNode.AddValue("parentBranchPointId", rec.ParentBranchPointId);
            if (rec.ChildBranchPointId != null)
                recNode.AddValue("childBranchPointId", rec.ChildBranchPointId);

            // Terminal orbit fields (only when Orbiting or SubOrbital)
            if (!string.IsNullOrEmpty(rec.TerminalOrbitBody))
            {
                recNode.AddValue("tOrbInc", rec.TerminalOrbitInclination.ToString("R", ic));
                recNode.AddValue("tOrbEcc", rec.TerminalOrbitEccentricity.ToString("R", ic));
                recNode.AddValue("tOrbSma", rec.TerminalOrbitSemiMajorAxis.ToString("R", ic));
                recNode.AddValue("tOrbLan", rec.TerminalOrbitLAN.ToString("R", ic));
                recNode.AddValue("tOrbArgPe", rec.TerminalOrbitArgumentOfPeriapsis.ToString("R", ic));
                recNode.AddValue("tOrbMna", rec.TerminalOrbitMeanAnomalyAtEpoch.ToString("R", ic));
                recNode.AddValue("tOrbEpoch", rec.TerminalOrbitEpoch.ToString("R", ic));
                recNode.AddValue("tOrbBody", rec.TerminalOrbitBody);
            }

            // Terminal position (Landed/Splashed)
            if (rec.TerminalPosition.HasValue)
            {
                ConfigNode tpNode = recNode.AddNode("TERMINAL_POSITION");
                SurfacePosition.SaveInto(tpNode, rec.TerminalPosition.Value);
            }

            // Terrain height at recording end
            if (!double.IsNaN(rec.TerrainHeightAtEnd))
                recNode.AddValue("terrainHeightAtEnd", rec.TerrainHeightAtEnd.ToString("R", ic));

            // Background surface position
            if (rec.SurfacePos.HasValue)
            {
                ConfigNode spNode = recNode.AddNode("SURFACE_POSITION");
                SurfacePosition.SaveInto(spNode, rec.SurfacePos.Value);
            }

            // Playback settings, linkage, and chain metadata
            SaveRecordingPlaybackAndLinkage(recNode, rec);

            // Resource, rewind, geometry, and mutable state
            SaveRecordingResourceAndState(recNode, rec);
        }

        /// <summary>
        /// Saves playback settings (format version, ghost snapshot mode, loop, playback enabled),
        /// EVA child linkage, and chain linkage into a RECORDING ConfigNode.
        /// </summary>
        private static void SaveRecordingPlaybackAndLinkage(ConfigNode recNode, Recording rec)
        {
            var ic = CultureInfo.InvariantCulture;

            // Existing recording metadata
            recNode.AddValue("recordingFormatVersion", rec.RecordingFormatVersion);
            // Persist the mode that matches the current sidecars on disk. Recomputing
            // from live snapshots here can drift .sfs metadata away from sidecars when
            // later saves serialize tree state without rewriting files.
            GhostSnapshotMode ghostSnapshotMode = RecordingStore.GetExpectedGhostSnapshotMode(rec);
            rec.GhostSnapshotMode = ghostSnapshotMode;
            if (ghostSnapshotMode != GhostSnapshotMode.Unspecified)
                recNode.AddValue("ghostSnapshotMode", ghostSnapshotMode.ToString());

            // Sidecar epoch (bug #270): stamped into .prec on write, validated on load
            if (rec.SidecarEpoch > 0)
                recNode.AddValue("sidecarEpoch", rec.SidecarEpoch.ToString(ic));
            recNode.AddValue("loopPlayback", rec.LoopPlayback);
            recNode.AddValue("loopIntervalSeconds", rec.LoopIntervalSeconds.ToString("R", ic));
            if (!double.IsNaN(rec.LoopStartUT))
                recNode.AddValue("loopStartUT", rec.LoopStartUT.ToString("R", ic));
            if (!double.IsNaN(rec.LoopEndUT))
                recNode.AddValue("loopEndUT", rec.LoopEndUT.ToString("R", ic));
            if (rec.LoopAnchorVesselId != 0)
                recNode.AddValue("loopAnchorPid", rec.LoopAnchorVesselId.ToString(ic));
            if (!string.IsNullOrEmpty(rec.LoopAnchorBodyName))
                recNode.AddValue("loopAnchorBodyName", rec.LoopAnchorBodyName);
            if (!rec.PlaybackEnabled)
                recNode.AddValue("playbackEnabled", rec.PlaybackEnabled.ToString());
            if (rec.Hidden)
                recNode.AddValue("hidden", rec.Hidden.ToString());

            // EVA child linkage
            if (!string.IsNullOrEmpty(rec.ParentRecordingId))
                recNode.AddValue("parentRecordingId", rec.ParentRecordingId);
            if (!string.IsNullOrEmpty(rec.EvaCrewName))
                recNode.AddValue("evaCrewName", rec.EvaCrewName);

            // Chain linkage
            if (!string.IsNullOrEmpty(rec.ChainId))
                recNode.AddValue("chainId", rec.ChainId);
            if (rec.ChainIndex >= 0)
                recNode.AddValue("chainIndex", rec.ChainIndex.ToString(ic));
            if (rec.ChainBranch > 0)
                recNode.AddValue("chainBranch", rec.ChainBranch.ToString(ic));
        }

        /// <summary>
        /// Saves atmosphere segment metadata, pre-launch resources, rewind save metadata,
        /// mutable playback state, and UI grouping tags into a RECORDING ConfigNode.
        /// </summary>
        internal static void SaveRecordingResourceAndState(ConfigNode recNode, Recording rec)
        {
            var ic = CultureInfo.InvariantCulture;

            // Atmosphere segment metadata
            if (!string.IsNullOrEmpty(rec.SegmentPhase))
                recNode.AddValue("segmentPhase", rec.SegmentPhase);
            if (!string.IsNullOrEmpty(rec.SegmentBodyName))
                recNode.AddValue("segmentBodyName", rec.SegmentBodyName);

            // Location context (Phase 10)
            if (!string.IsNullOrEmpty(rec.StartBodyName))
                recNode.AddValue("startBodyName", rec.StartBodyName);
            if (!string.IsNullOrEmpty(rec.StartBiome))
                recNode.AddValue("startBiome", rec.StartBiome);
            if (!string.IsNullOrEmpty(rec.StartSituation))
                recNode.AddValue("startSituation", rec.StartSituation);
            if (!string.IsNullOrEmpty(rec.EndBiome))
                recNode.AddValue("endBiome", rec.EndBiome);
            if (!string.IsNullOrEmpty(rec.LaunchSiteName))
                recNode.AddValue("launchSiteName", rec.LaunchSiteName);

            // Pre-launch resources
            if (rec.PreLaunchFunds != 0)
                recNode.AddValue("preLaunchFunds", rec.PreLaunchFunds.ToString("R", ic));
            if (rec.PreLaunchScience != 0)
                recNode.AddValue("preLaunchScience", rec.PreLaunchScience.ToString("R", ic));
            if (rec.PreLaunchReputation != 0)
                recNode.AddValue("preLaunchRep", rec.PreLaunchReputation.ToString("R", ic));

            // Rewind save metadata
            if (!string.IsNullOrEmpty(rec.RewindSaveFileName))
            {
                recNode.AddValue("rewindSave", rec.RewindSaveFileName);
                recNode.AddValue("rewindResFunds", rec.RewindReservedFunds.ToString("R", ic));
                recNode.AddValue("rewindResSci", rec.RewindReservedScience.ToString("R", ic));
                recNode.AddValue("rewindResRep", rec.RewindReservedRep.ToString("R", ic));
            }

            // Mutable playback state (parallels ParsekScenario.OnSave standalone fields)
            if (rec.SpawnedVesselPersistentId != 0)
                recNode.AddValue("spawnedPid", rec.SpawnedVesselPersistentId);
            if (!string.IsNullOrEmpty(rec.TerminalSpawnSupersededByRecordingId))
                recNode.AddValue("terminalSpawnSupersededBy", rec.TerminalSpawnSupersededByRecordingId);
            if (rec.VesselDestroyed)
                recNode.AddValue("vesselDestroyed", rec.VesselDestroyed.ToString());
            // #573/#589: persist scoped rewind suppression metadata. New saves only
            // write this for the active/source recording; unscoped legacy markers are
            // recognized at load/spawn time and cleared instead of blocking future
            // same-tree terminal materialization forever.
            if (rec.SpawnSuppressedByRewind)
            {
                recNode.AddValue("spawnSuppressedByRewind", rec.SpawnSuppressedByRewind.ToString());
                if (!string.IsNullOrEmpty(rec.SpawnSuppressedByRewindReason))
                    recNode.AddValue("spawnSuppressedByRewindReason", rec.SpawnSuppressedByRewindReason);
                if (!double.IsNaN(rec.SpawnSuppressedByRewindUT))
                    recNode.AddValue("spawnSuppressedByRewindUT", rec.SpawnSuppressedByRewindUT.ToString("R", ic));
            }
            recNode.AddValue("lastResIdx", rec.LastAppliedResourceIndex);
            recNode.AddValue("pointCount", rec.Points != null ? rec.Points.Count : 0);

            // UI grouping tags (multi-group membership)
            if (rec.RecordingGroups != null)
                for (int g = 0; g < rec.RecordingGroups.Count; g++)
                    recNode.AddValue("recordingGroup", rec.RecordingGroups[g]);
            if (!string.IsNullOrEmpty(rec.AutoAssignedStandaloneGroupName))
                recNode.AddValue("autoAssignedStandaloneGroup", rec.AutoAssignedStandaloneGroupName);

            // Controller info
            if (rec.Controllers != null)
            {
                for (int i = 0; i < rec.Controllers.Count; i++)
                {
                    ConfigNode ctrlNode = recNode.AddNode("CONTROLLER");
                    ctrlNode.AddValue("type", rec.Controllers[i].type ?? "");
                    ctrlNode.AddValue("part", rec.Controllers[i].partName ?? "");
                    ctrlNode.AddValue("pid", rec.Controllers[i].partPersistentId.ToString(ic));
                }
                ParsekLog.Verbose("RecordingTree",
                    $"SaveRecordingResourceAndState: saved {rec.Controllers.Count} controller(s) for recording={rec.RecordingId}");
            }
            if (rec.IsDebris)
                recNode.AddValue("isDebris", rec.IsDebris.ToString());
            if (rec.IsGhostOnly)
                recNode.AddValue("isGhostOnly", rec.IsGhostOnly.ToString());

            // Max distance from launch (#302): needed for idle-on-pad auto-discard
            // after scene reload (without this, deserialized recordings default to 0.0
            // and IsTreeIdleOnPad falsely discards the whole tree).
            if (rec.MaxDistanceFromLaunch > 0)
                recNode.AddValue("maxDist", rec.MaxDistanceFromLaunch.ToString("R", ic));

            // Cascade depth (#284). Only written when non-zero so existing
            // gen-0 recordings stay byte-identical and old saves stay clean.
            if (rec.Generation > 0)
                recNode.AddValue("generation", rec.Generation.ToString(ic));

            // Crew end states (kerbals module)
            if (rec.CrewEndStatesResolved)
                recNode.AddValue("crewEndStatesResolved", rec.CrewEndStatesResolved.ToString());
            RecordingStore.SerializeCrewEndStates(recNode, rec);

            // Resource manifests (Phase 11)
            RecordingStore.SerializeResourceManifest(recNode, rec);

            // Inventory manifests (Phase 11)
            RecordingStore.SerializeInventoryManifest(recNode, rec);
            if (rec.StartInventorySlots != 0)
                recNode.AddValue("startInvSlots", rec.StartInventorySlots.ToString(ic));
            if (rec.EndInventorySlots != 0)
                recNode.AddValue("endInvSlots", rec.EndInventorySlots.ToString(ic));

            // Crew manifests (Phase 11)
            RecordingStore.SerializeCrewManifest(recNode, rec);

            // Dock target vessel PID (Phase 11)
            if (rec.DockTargetVesselPid != 0)
                recNode.AddValue("dockTargetPid", rec.DockTargetVesselPid.ToString(ic));

            // Rewind-to-Staging (design section 5.5). Omit the default Immutable enum value
            // so legacy saves stay byte-identical; write the string form for durability across
            // enum renumbering. SupersedeTargetId is transient but written defensively so a
            // mid-session crash can be diagnosed.
            if (rec.MergeState != MergeState.Immutable)
                recNode.AddValue("mergeState", rec.MergeState.ToString());
            if (!string.IsNullOrEmpty(rec.CreatingSessionId))
                recNode.AddValue("creatingSessionId", rec.CreatingSessionId);
            if (!string.IsNullOrEmpty(rec.SupersedeTargetId))
                recNode.AddValue("supersedeTargetId", rec.SupersedeTargetId);
            if (!string.IsNullOrEmpty(rec.ProvisionalForRpId))
                recNode.AddValue("provisionalForRpId", rec.ProvisionalForRpId);

            // Pre-Re-Fly anchor trajectory snapshot (#688 follow-up). Captured
            // at session start so the per-frame anchor offset can sample the
            // ORIGINAL active recording at any UT — even after the live
            // recording is trimmed past cutoffUT and re-extended with new
            // player flight data. The snapshot fields are [NonSerialized]
            // and would otherwise vanish on F5/F9 mid-session, breaking the
            // per-frame anchor for every other ghost in the Re-Fly tree.
            // Encoded as a child PRE_REFLY_ANCHOR node containing a full
            // serialized trajectory (points/orbit segments/track sections);
            // SerializeTrajectoryInto is reused via a temp Recording so the
            // codec stays a single source of truth for trajectory layout.
            // SupersedeCommit clears the snapshot once the session ends, so
            // the .sfs bloat is bounded to active sessions only.
            //
            // [storage-exempt] CLAUDE.md's storage contract reserves bulk
            // trajectory data for sidecar `.prec` files. We deliberately
            // inline the snapshot into `.sfs` here because the data is
            // session-scoped (one recording per active Re-Fly, capped at
            // session lifetime), the lifetime is bounded by
            // ClearPreReFlyAnchorSnapshotsForSession at every session-end
            // path plus a load-time orphan sweep, and tying the snapshot
            // to the same atomic write as the marker / supersede / merge-
            // state metadata avoids a sidecar-orphan class of bug where the
            // snapshot's lifecycle drifts from the marker's. A future
            // optimisation could move the encoded payload into a sidecar
            // file keyed by session id; not blocking on it because the
            // worst-case bloat is bounded to active sessions only.
            if (!string.IsNullOrEmpty(rec.PreReFlyAnchorSessionId)
                && rec.HasPreReFlyAnchorTrajectory(rec.PreReFlyAnchorSessionId))
            {
                Recording snapshotRec = rec.BuildPreReFlyAnchorTrajectoryRecording(
                    rec.PreReFlyAnchorSessionId);
                if (snapshotRec != null)
                {
                    ConfigNode anchorNode = recNode.AddNode("PRE_REFLY_ANCHOR");
                    anchorNode.AddValue("sessionId", rec.PreReFlyAnchorSessionId);
                    TrajectoryTextSidecarCodec.SerializeTrajectoryInto(anchorNode, snapshotRec);
                    if (!RecordingStore.SuppressLogging)
                    {
                        var icL = CultureInfo.InvariantCulture;
                        ParsekLog.Verbose("RecordingTree",
                            "PRE_REFLY_ANCHOR written: rec=" + (rec.RecordingId ?? "<no-id>")
                            + " sess=" + rec.PreReFlyAnchorSessionId
                            + " points=" + (snapshotRec.Points?.Count ?? 0).ToString(icL)
                            + " orbitSegments=" + (snapshotRec.OrbitSegments?.Count ?? 0).ToString(icL)
                            + " trackSections=" + (snapshotRec.TrackSections?.Count ?? 0).ToString(icL));
                    }
                }
            }

            if (!string.IsNullOrEmpty(rec.PreReFlyOriginalSessionId)
                && rec.HasPreReFlyOriginalRecording(rec.PreReFlyOriginalSessionId))
            {
                Recording originalSnapshot = rec.BuildPreReFlyOriginalRecording(
                    rec.PreReFlyOriginalSessionId);
                if (originalSnapshot != null)
                {
                    ConfigNode originalNode = recNode.AddNode("PRE_REFLY_ORIGINAL");
                    originalNode.AddValue("sessionId", rec.PreReFlyOriginalSessionId);
                    ConfigNode originalRecordingNode = originalNode.AddNode("RECORDING");
                    SaveRecordingInto(originalRecordingNode, originalSnapshot);
                    TrajectoryTextSidecarCodec.SerializeTrajectoryInto(
                        originalRecordingNode, originalSnapshot);
                    SaveInlineSnapshot(originalNode, "VESSEL_SNAPSHOT", originalSnapshot.VesselSnapshot);
                    SaveInlineSnapshot(originalNode, "GHOST_SNAPSHOT", originalSnapshot.GhostVisualSnapshot);
                    if (!RecordingStore.SuppressLogging)
                    {
                        var icL = CultureInfo.InvariantCulture;
                        ParsekLog.Verbose("RecordingTree",
                            "PRE_REFLY_ORIGINAL written: rec=" + (rec.RecordingId ?? "<no-id>")
                            + " sess=" + rec.PreReFlyOriginalSessionId
                            + " points=" + (originalSnapshot.Points?.Count ?? 0).ToString(icL)
                            + " orbitSegments=" + (originalSnapshot.OrbitSegments?.Count ?? 0).ToString(icL)
                            + " trackSections=" + (originalSnapshot.TrackSections?.Count ?? 0).ToString(icL));
                    }
                }
            }
        }

        internal static void LoadRecordingFrom(ConfigNode recNode, Recording rec)
        {
            var inv = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            string id = recNode.GetValue("recordingId");
            if (!string.IsNullOrEmpty(id))
                rec.RecordingId = id;

            rec.VesselName = Recording.ResolveLocalizedName(recNode.GetValue("vesselName") ?? "");
            rec.TreeId = recNode.GetValue("treeId");
            int treeOrder;
            if (int.TryParse(recNode.GetValue("treeOrder"), NumberStyles.Integer, ic, out treeOrder))
                rec.TreeOrder = treeOrder;

            uint vesselPid;
            if (uint.TryParse(recNode.GetValue("vesselPersistentId"), NumberStyles.Integer, ic, out vesselPid))
                rec.VesselPersistentId = vesselPid;

            string explicitStartStr = recNode.GetValue("explicitStartUT");
            if (explicitStartStr != null)
            {
                double explicitStart;
                if (double.TryParse(explicitStartStr, inv, ic, out explicitStart))
                    rec.ExplicitStartUT = explicitStart;
            }
            string explicitEndStr = recNode.GetValue("explicitEndUT");
            if (explicitEndStr != null)
            {
                double explicitEnd;
                if (double.TryParse(explicitEndStr, inv, ic, out explicitEnd))
                    rec.ExplicitEndUT = explicitEnd;
            }

            string terminalStateStr = recNode.GetValue("terminalState");
            if (terminalStateStr != null)
            {
                int terminalInt;
                if (int.TryParse(terminalStateStr, NumberStyles.Integer, ic, out terminalInt)
                    && Enum.IsDefined(typeof(TerminalState), terminalInt))
                    rec.TerminalStateValue = (TerminalState)terminalInt;
            }

            string endpointPhaseStr = recNode.GetValue("endpointPhase");
            if (endpointPhaseStr != null)
            {
                int endpointPhaseInt;
                if (int.TryParse(endpointPhaseStr, NumberStyles.Integer, ic, out endpointPhaseInt)
                    && Enum.IsDefined(typeof(RecordingEndpointPhase), endpointPhaseInt))
                    rec.EndpointPhase = (RecordingEndpointPhase)endpointPhaseInt;
            }
            rec.EndpointBodyName = recNode.GetValue("endpointBodyName");

            rec.ParentBranchPointId = recNode.GetValue("parentBranchPointId");
            rec.ChildBranchPointId = recNode.GetValue("childBranchPointId");

            // Terminal orbit fields
            string tOrbBody = recNode.GetValue("tOrbBody");
            if (!string.IsNullOrEmpty(tOrbBody))
            {
                rec.TerminalOrbitBody = tOrbBody;
                double.TryParse(recNode.GetValue("tOrbInc"), inv, ic, out rec.TerminalOrbitInclination);
                double.TryParse(recNode.GetValue("tOrbEcc"), inv, ic, out rec.TerminalOrbitEccentricity);
                double.TryParse(recNode.GetValue("tOrbSma"), inv, ic, out rec.TerminalOrbitSemiMajorAxis);
                double.TryParse(recNode.GetValue("tOrbLan"), inv, ic, out rec.TerminalOrbitLAN);
                double.TryParse(recNode.GetValue("tOrbArgPe"), inv, ic, out rec.TerminalOrbitArgumentOfPeriapsis);
                double.TryParse(recNode.GetValue("tOrbMna"), inv, ic, out rec.TerminalOrbitMeanAnomalyAtEpoch);
                double.TryParse(recNode.GetValue("tOrbEpoch"), inv, ic, out rec.TerminalOrbitEpoch);
            }

            // Terminal position
            ConfigNode tpNode = recNode.GetNode("TERMINAL_POSITION");
            if (tpNode != null)
                rec.TerminalPosition = SurfacePosition.LoadFrom(tpNode);

            // Terrain height at recording end
            string thtStr = recNode.GetValue("terrainHeightAtEnd");
            if (thtStr != null)
            {
                double tht;
                if (double.TryParse(thtStr, inv, ic, out tht))
                    rec.TerrainHeightAtEnd = tht;
            }

            // Background surface position
            ConfigNode spNode = recNode.GetNode("SURFACE_POSITION");
            if (spNode != null)
                rec.SurfacePos = SurfacePosition.LoadFrom(spNode);

            // Playback settings, linkage, and chain metadata
            LoadRecordingPlaybackAndLinkage(recNode, rec);

            // Resource, rewind, geometry, and mutable state
            LoadRecordingResourceAndState(recNode, rec);
        }

        #region LoadRecording Extracted Helpers

        /// <summary>
        /// Loads playback settings (format version, ghost geometry version, loop,
        /// playback enabled), EVA child linkage, and chain linkage from a RECORDING
        /// ConfigNode into the given Recording.
        /// </summary>
        private static void LoadRecordingPlaybackAndLinkage(ConfigNode recNode, Recording rec)
        {
            var inv = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            // Existing recording metadata
            string formatVersionStr = recNode.GetValue("recordingFormatVersion");
            if (formatVersionStr != null)
            {
                int formatVersion;
                if (int.TryParse(formatVersionStr, NumberStyles.Integer, ic, out formatVersion))
                    rec.RecordingFormatVersion = formatVersion;
                else
                    rec.RecordingFormatVersion = 0;
            }
            else
            {
                rec.RecordingFormatVersion = 0;
            }
            rec.GhostSnapshotMode = RecordingStore.ParseGhostSnapshotMode(recNode.GetValue("ghostSnapshotMode"));

            // Sidecar epoch (bug #270)
            string sidecarEpochStr = recNode.GetValue("sidecarEpoch");
            if (sidecarEpochStr != null)
            {
                int sidecarEpoch;
                if (int.TryParse(sidecarEpochStr, NumberStyles.Integer, ic, out sidecarEpoch))
                    rec.SidecarEpoch = sidecarEpoch;
            }

            string loopPlaybackStr = recNode.GetValue("loopPlayback");
            if (loopPlaybackStr != null)
            {
                bool loopPlayback;
                if (bool.TryParse(loopPlaybackStr, out loopPlayback))
                    rec.LoopPlayback = loopPlayback;
            }

            string loopStartUTStr = recNode.GetValue("loopStartUT");
            if (loopStartUTStr != null)
            {
                double loopStartUT;
                if (double.TryParse(loopStartUTStr, inv, ic, out loopStartUT))
                    rec.LoopStartUT = loopStartUT;
            }

            string loopEndUTStr = recNode.GetValue("loopEndUT");
            if (loopEndUTStr != null)
            {
                double loopEndUT;
                if (double.TryParse(loopEndUTStr, inv, ic, out loopEndUT))
                    rec.LoopEndUT = loopEndUT;
            }

            string loopIntervalStr = recNode.GetValue("loopIntervalSeconds");
            if (loopIntervalStr != null)
            {
                double loopIntervalSeconds;
                if (double.TryParse(loopIntervalStr, inv, ic, out loopIntervalSeconds))
                {
                    if (rec.RecordingFormatVersion < RecordingStore.LaunchToLaunchLoopIntervalFormatVersion)
                    {
                        double effectiveLoopDuration;
                        double migratedLoopIntervalSeconds =
                            loopIntervalSeconds;
                        if (GhostPlaybackEngine.TryConvertLegacyGapToLoopPeriodSeconds(
                                rec, loopIntervalSeconds,
                                out migratedLoopIntervalSeconds, out effectiveLoopDuration))
                        {
                            int legacyRecordingFormatVersion = rec.RecordingFormatVersion;
                            rec.LoopIntervalSeconds = migratedLoopIntervalSeconds;
                            RecordingStore.NormalizeRecordingFormatVersionAfterLegacyLoopMigration(rec);
                            ParsekLog.Warn("Loop",
                                $"RecordingTree: migrated recording '{rec.VesselName}' from legacy " +
                                $"gap loopIntervalSeconds={loopIntervalSeconds.ToString("R", ic)} " +
                                $"to launch-to-launch period={migratedLoopIntervalSeconds.ToString("R", ic)}s " +
                                $"using effectiveLoopDuration={effectiveLoopDuration.ToString("R", ic)}s " +
                                $"for recordingFormatVersion={legacyRecordingFormatVersion} (pre-v4 loop save).");
                        }
                        else
                        {
                            rec.LoopIntervalSeconds = loopIntervalSeconds;
                            ParsekLog.Warn("Loop",
                                $"RecordingTree: loaded recording '{rec.VesselName}' with legacy " +
                                $"loopIntervalSeconds={loopIntervalSeconds.ToString("R", ic)} " +
                                $"for recordingFormatVersion={rec.RecordingFormatVersion}, but deferred migration " +
                                "because loop bounds are not hydrated yet.");
                        }
                    }
                    else
                    {
                        rec.LoopIntervalSeconds = loopIntervalSeconds;
                    }
                }
            }

            string loopAnchorPidStr = recNode.GetValue("loopAnchorPid");
            if (loopAnchorPidStr != null)
            {
                uint loopAnchorPid;
                if (uint.TryParse(loopAnchorPidStr, NumberStyles.Integer, ic, out loopAnchorPid))
                    rec.LoopAnchorVesselId = loopAnchorPid;
            }

            string loopAnchorBodyNameStr = recNode.GetValue("loopAnchorBodyName");
            if (!string.IsNullOrEmpty(loopAnchorBodyNameStr))
                rec.LoopAnchorBodyName = loopAnchorBodyNameStr;

            string playbackEnabledStr = recNode.GetValue("playbackEnabled");
            if (playbackEnabledStr != null)
            {
                bool playbackEnabled;
                if (bool.TryParse(playbackEnabledStr, out playbackEnabled))
                    rec.PlaybackEnabled = playbackEnabled;
            }
            string hiddenStr = recNode.GetValue("hidden");
            if (hiddenStr != null)
            {
                bool hidden;
                if (bool.TryParse(hiddenStr, out hidden))
                    rec.Hidden = hidden;
            }

            // EVA child linkage
            rec.ParentRecordingId = recNode.GetValue("parentRecordingId");
            rec.EvaCrewName = recNode.GetValue("evaCrewName");

            // Chain linkage
            rec.ChainId = recNode.GetValue("chainId");
            string chainIndexStr = recNode.GetValue("chainIndex");
            if (chainIndexStr != null)
            {
                int chainIndex;
                if (int.TryParse(chainIndexStr, NumberStyles.Integer, ic, out chainIndex))
                    rec.ChainIndex = chainIndex;
            }
            string chainBranchStr = recNode.GetValue("chainBranch");
            if (chainBranchStr != null)
            {
                int chainBranch;
                if (int.TryParse(chainBranchStr, NumberStyles.Integer, ic, out chainBranch))
                    rec.ChainBranch = chainBranch;
            }
        }

        /// <summary>
        /// Loads atmosphere segment metadata, pre-launch resources, rewind save metadata,
        /// ghost geometry metadata, mutable playback state, and UI grouping tags from a
        /// RECORDING ConfigNode into the given Recording.
        /// </summary>
        internal static void LoadRecordingResourceAndState(ConfigNode recNode, Recording rec)
        {
            var inv = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            // Atmosphere segment metadata
            rec.SegmentPhase = recNode.GetValue("segmentPhase");
            rec.SegmentBodyName = recNode.GetValue("segmentBodyName");

            // Location context (Phase 10)
            rec.StartBodyName = recNode.GetValue("startBodyName");
            rec.StartBiome = recNode.GetValue("startBiome");
            rec.StartSituation = recNode.GetValue("startSituation");
            rec.EndBiome = recNode.GetValue("endBiome");
            rec.LaunchSiteName = recNode.GetValue("launchSiteName");

            // Pre-launch resources
            string preLaunchFundsStr = recNode.GetValue("preLaunchFunds");
            if (preLaunchFundsStr != null)
            {
                double preLaunchFunds;
                if (double.TryParse(preLaunchFundsStr, inv, ic, out preLaunchFunds))
                    rec.PreLaunchFunds = preLaunchFunds;
            }
            string preLaunchScienceStr = recNode.GetValue("preLaunchScience");
            if (preLaunchScienceStr != null)
            {
                double preLaunchScience;
                if (double.TryParse(preLaunchScienceStr, inv, ic, out preLaunchScience))
                    rec.PreLaunchScience = preLaunchScience;
            }
            string preLaunchRepStr = recNode.GetValue("preLaunchRep");
            if (preLaunchRepStr != null)
            {
                float preLaunchRep;
                if (float.TryParse(preLaunchRepStr, inv, ic, out preLaunchRep))
                    rec.PreLaunchReputation = preLaunchRep;
            }

            // Rewind save metadata
            rec.RewindSaveFileName = recNode.GetValue("rewindSave");
            string rewindFundsStr = recNode.GetValue("rewindResFunds");
            if (rewindFundsStr != null)
            {
                double rewindFunds;
                if (double.TryParse(rewindFundsStr, inv, ic, out rewindFunds))
                    rec.RewindReservedFunds = rewindFunds;
            }
            string rewindSciStr = recNode.GetValue("rewindResSci");
            if (rewindSciStr != null)
            {
                double rewindSci;
                if (double.TryParse(rewindSciStr, inv, ic, out rewindSci))
                    rec.RewindReservedScience = rewindSci;
            }
            string rewindRepStr = recNode.GetValue("rewindResRep");
            if (rewindRepStr != null)
            {
                float rewindRep;
                if (float.TryParse(rewindRepStr, inv, ic, out rewindRep))
                    rec.RewindReservedRep = rewindRep;
            }

            // Mutable playback state
            string pidStr = recNode.GetValue("spawnedPid");
            if (pidStr != null)
            {
                uint spawnedPid;
                if (uint.TryParse(pidStr, NumberStyles.Integer, ic, out spawnedPid))
                {
                    rec.SpawnedVesselPersistentId = spawnedPid;
                    // Invariant: VesselSpawned ⇔ SpawnedVesselPersistentId != 0. The field
                    // is not serialized directly; re-derive on load. Without this, the
                    // scene-enter resume path (TryFindCommittedTreeForSpawnedVessel) fails
                    // its `!VesselSpawned` filter after any save/load, so auto-record
                    // doesn't resume when the player re-enters a vessel that already has
                    // a committed recording (fix for v0.8.3 playtest bug).
                    rec.VesselSpawned = spawnedPid != 0;
                }
            }
            string destroyedStr = recNode.GetValue("vesselDestroyed");
            if (destroyedStr != null)
            {
                bool destroyed;
                if (bool.TryParse(destroyedStr, out destroyed))
                    rec.VesselDestroyed = destroyed;
            }
            rec.TerminalSpawnSupersededByRecordingId = recNode.GetValue("terminalSpawnSupersededBy");
            // #573/#589: load scoped post-rewind suppression marker. Older saves
            // only contain the bool because the original fix marked whole trees;
            // tag those as legacy-unscoped so the spawn gate can clear them.
            string spawnSuppressedStr = recNode.GetValue("spawnSuppressedByRewind");
            if (spawnSuppressedStr != null)
            {
                bool spawnSuppressed;
                if (bool.TryParse(spawnSuppressedStr, out spawnSuppressed))
                    rec.SpawnSuppressedByRewind = spawnSuppressed;
            }
            if (rec.SpawnSuppressedByRewind)
            {
                rec.SpawnSuppressedByRewindReason = recNode.GetValue("spawnSuppressedByRewindReason");
                string spawnSuppressedUTStr = recNode.GetValue("spawnSuppressedByRewindUT");
                if (spawnSuppressedUTStr != null)
                {
                    double spawnSuppressedUT;
                    if (double.TryParse(spawnSuppressedUTStr, NumberStyles.Float, ic, out spawnSuppressedUT))
                        rec.SpawnSuppressedByRewindUT = spawnSuppressedUT;
                }

                if (string.IsNullOrEmpty(rec.SpawnSuppressedByRewindReason))
                    rec.SpawnSuppressedByRewindReason = ParsekScenario.RewindSpawnSuppressionReasonLegacyUnscoped;
            }
            string resIdxStr = recNode.GetValue("lastResIdx");
            if (resIdxStr != null)
            {
                int resIdx;
                if (int.TryParse(resIdxStr, NumberStyles.Integer, ic, out resIdx))
                    rec.LastAppliedResourceIndex = resIdx;
            }
            // pointCount is informational — Points list is loaded from sidecar file

            // UI grouping tags (multi-group membership, backward compat with single value)
            string[] recGroups = recNode.GetValues("recordingGroup");
            if (recGroups != null && recGroups.Length > 0)
            {
                bool renamedAny = false;
                for (int g = 0; g < recGroups.Length; g++)
                {
                    recGroups[g] = Recording.ResolveLocalizedName(recGroups[g]);
                    // PR #328: rename the gloops group to a shorter label. Recordings
                    // from pre-rename builds keep working — the mapping runs once,
                    // marks the file dirty, and the next save writes the new name.
                    if (recGroups[g] == RecordingStore.LegacyGloopsGroupName)
                    {
                        recGroups[g] = RecordingStore.GloopsGroupName;
                        renamedAny = true;
                    }
                }
                rec.RecordingGroups = new List<string>(recGroups);
                if (renamedAny)
                    rec.FilesDirty = true;
            }
            rec.AutoAssignedStandaloneGroupName = recNode.GetValue("autoAssignedStandaloneGroup");

            // Controller info
            ConfigNode[] ctrlNodes = recNode.GetNodes("CONTROLLER");
            if (ctrlNodes.Length > 0)
            {
                rec.Controllers = new List<ControllerInfo>(ctrlNodes.Length);
                for (int i = 0; i < ctrlNodes.Length; i++)
                {
                    var ctrl = new ControllerInfo();
                    ctrl.type = ctrlNodes[i].GetValue("type") ?? "";
                    ctrl.partName = ctrlNodes[i].GetValue("part") ?? "";
                    uint pid;
                    if (uint.TryParse(ctrlNodes[i].GetValue("pid"), NumberStyles.Integer, ic, out pid))
                        ctrl.partPersistentId = pid;
                    rec.Controllers.Add(ctrl);
                }
                ParsekLog.Verbose("RecordingTree",
                    $"LoadRecordingResourceAndState: loaded {rec.Controllers.Count} controller(s) for recording={rec.RecordingId}");
            }

            string isDebrisStr = recNode.GetValue("isDebris");
            if (isDebrisStr != null)
            {
                bool isDebris;
                if (bool.TryParse(isDebrisStr, out isDebris))
                    rec.IsDebris = isDebris;
            }

            string isGhostOnlyStr = recNode.GetValue("isGhostOnly");
            if (isGhostOnlyStr != null)
            {
                bool isGhostOnly;
                if (bool.TryParse(isGhostOnlyStr, out isGhostOnly))
                    rec.IsGhostOnly = isGhostOnly;
            }

            // Max distance from launch (#302)
            string maxDistStr = recNode.GetValue("maxDist");
            if (maxDistStr != null)
            {
                double maxDist;
                if (double.TryParse(maxDistStr, inv, ic, out maxDist))
                    rec.MaxDistanceFromLaunch = maxDist;
            }

            // Cascade depth (#284). Missing key = 0 (legacy save or gen-0 recording).
            string generationStr = recNode.GetValue("generation");
            if (generationStr != null)
            {
                int generation;
                if (int.TryParse(generationStr, NumberStyles.Integer, ic, out generation))
                    rec.Generation = generation;
            }

            // Crew end states (kerbals module)
            string crewEndStatesResolvedStr = recNode.GetValue("crewEndStatesResolved");
            if (crewEndStatesResolvedStr != null)
            {
                bool crewEndStatesResolved;
                if (bool.TryParse(crewEndStatesResolvedStr, out crewEndStatesResolved))
                    rec.CrewEndStatesResolved = crewEndStatesResolved;
            }
            RecordingStore.DeserializeCrewEndStates(recNode, rec);
            if (rec.CrewEndStates != null)
                rec.CrewEndStatesResolved = true;

            // Resource manifests (Phase 11)
            RecordingStore.DeserializeResourceManifest(recNode, rec);

            // Inventory manifests (Phase 11)
            RecordingStore.DeserializeInventoryManifest(recNode, rec);
            string startInvSlotsStr = recNode.GetValue("startInvSlots");
            if (startInvSlotsStr != null)
            {
                int startInvSlots;
                if (int.TryParse(startInvSlotsStr, NumberStyles.Integer, ic, out startInvSlots))
                    rec.StartInventorySlots = startInvSlots;
            }
            string endInvSlotsStr = recNode.GetValue("endInvSlots");
            if (endInvSlotsStr != null)
            {
                int endInvSlots;
                if (int.TryParse(endInvSlotsStr, NumberStyles.Integer, ic, out endInvSlots))
                    rec.EndInventorySlots = endInvSlots;
            }

            // Crew manifests (Phase 11)
            RecordingStore.DeserializeCrewManifest(recNode, rec);

            // Dock target vessel PID (Phase 11)
            string dockTargetPidStr = recNode.GetValue("dockTargetPid");
            if (dockTargetPidStr != null)
            {
                uint dockTargetPid;
                if (uint.TryParse(dockTargetPidStr, NumberStyles.Integer, ic, out dockTargetPid))
                    rec.DockTargetVesselPid = dockTargetPid;
            }

            // Rewind-to-Staging (design section 5.5 + 9). Legacy saves without `mergeState`
            // default to Immutable. Saves that carried the old binary `committed` bool map
            // to the tri-state via RecordingStore's batch migration helper (idempotent,
            // one-shot logged). Stray transient SupersedeTargetId on committed recordings
            // is logged Warn and treated as cleared.
            string mergeStateStr = recNode.GetValue("mergeState");
            bool mergeStateWasExplicit = false;
            if (mergeStateStr != null)
            {
                mergeStateWasExplicit = true;
                MergeState parsed;
                if (Enum.TryParse(mergeStateStr, out parsed))
                    rec.MergeState = parsed;
                else
                    ParsekLog.Warn("RecordingTree",
                        $"LoadRecordingFrom: unknown mergeState '{mergeStateStr}' for rec={rec.RecordingId} — defaulting to Immutable");
            }
            else
            {
                rec.MergeState = MergeState.Immutable;
            }

            // Legacy migration: binary `committed` bool -> MergeState tri-state. The field
            // was never shipped in a release but design section 9 defines the mapping for
            // forward safety. Counter incremented in RecordingStore for a one-shot Info log.
            if (!mergeStateWasExplicit)
            {
                string committedStr = recNode.GetValue("committed");
                if (committedStr != null)
                {
                    bool committed;
                    if (bool.TryParse(committedStr, out committed))
                    {
                        rec.MergeState = committed ? MergeState.Immutable : MergeState.NotCommitted;
                        RecordingStore.BumpLegacyMergeStateMigrationCounterForTesting();
                    }
                }
            }

            rec.CreatingSessionId = recNode.GetValue("creatingSessionId");
            rec.SupersedeTargetId = recNode.GetValue("supersedeTargetId");
            rec.ProvisionalForRpId = recNode.GetValue("provisionalForRpId");

            if (!string.IsNullOrEmpty(rec.SupersedeTargetId)
                && rec.MergeState != MergeState.NotCommitted)
            {
                ParsekLog.Warn("Recording",
                    $"Stray SupersedeTargetId on committed rec={rec.RecordingId}; treating as cleared");
                rec.SupersedeTargetId = null;
            }

            // Pre-Re-Fly anchor trajectory snapshot (#688 follow-up). The
            // matching SaveRecordingInto branch documents the lifecycle and
            // why the snapshot must survive F5/F9. Decoded into a temp
            // Recording (re-using SerializeTrajectoryInto's inverse) so the
            // shared trajectory codec stays a single source of truth.
            ConfigNode anchorNode = recNode.GetNode("PRE_REFLY_ANCHOR");
            if (anchorNode != null)
            {
                string anchorSessionId = anchorNode.GetValue("sessionId");
                if (!string.IsNullOrEmpty(anchorSessionId))
                {
                    Recording snapshotRec = new Recording
                    {
                        RecordingFormatVersion = rec.RecordingFormatVersion,
                    };
                    TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(anchorNode, snapshotRec);
                    rec.PreReFlyAnchorSessionId = anchorSessionId;
                    rec.PreReFlyAnchorPoints = snapshotRec.Points;
                    rec.PreReFlyAnchorOrbitSegments = snapshotRec.OrbitSegments;
                    rec.PreReFlyAnchorTrackSections = snapshotRec.TrackSections;
                    if (!RecordingStore.SuppressLogging)
                    {
                        var icL = CultureInfo.InvariantCulture;
                        ParsekLog.Verbose("RecordingTree",
                            "PRE_REFLY_ANCHOR loaded: rec=" + (rec.RecordingId ?? "<no-id>")
                            + " sess=" + anchorSessionId
                            + " points=" + (snapshotRec.Points?.Count ?? 0).ToString(icL)
                            + " orbitSegments=" + (snapshotRec.OrbitSegments?.Count ?? 0).ToString(icL)
                            + " trackSections=" + (snapshotRec.TrackSections?.Count ?? 0).ToString(icL));
                    }
                }
            }

            ConfigNode originalNode = recNode.GetNode("PRE_REFLY_ORIGINAL");
            if (originalNode != null)
            {
                string originalSessionId = originalNode.GetValue("sessionId");
                ConfigNode originalRecordingNode = originalNode.GetNode("RECORDING");
                if (!string.IsNullOrEmpty(originalSessionId)
                    && originalRecordingNode != null)
                {
                    Recording originalSnapshot = new Recording
                    {
                        RecordingFormatVersion = rec.RecordingFormatVersion,
                    };
                    LoadRecordingFrom(originalRecordingNode, originalSnapshot);
                    TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(
                        originalRecordingNode, originalSnapshot);
                    originalSnapshot.VesselSnapshot = LoadInlineSnapshot(
                        originalNode, "VESSEL_SNAPSHOT");
                    originalSnapshot.GhostVisualSnapshot = LoadInlineSnapshot(
                        originalNode, "GHOST_SNAPSHOT");
                    originalSnapshot.ClearPreReFlySessionSnapshots();
                    rec.PreReFlyOriginalSessionId = originalSessionId;
                    rec.PreReFlyOriginalRecording = originalSnapshot;
                    if (!RecordingStore.SuppressLogging)
                    {
                        var icL = CultureInfo.InvariantCulture;
                        ParsekLog.Verbose("RecordingTree",
                            "PRE_REFLY_ORIGINAL loaded: rec=" + (rec.RecordingId ?? "<no-id>")
                            + " sess=" + originalSessionId
                            + " points=" + (originalSnapshot.Points?.Count ?? 0).ToString(icL)
                            + " orbitSegments=" + (originalSnapshot.OrbitSegments?.Count ?? 0).ToString(icL)
                            + " trackSections=" + (originalSnapshot.TrackSections?.Count ?? 0).ToString(icL));
                    }
                }
            }
        }

        private static void SaveInlineSnapshot(ConfigNode parent, string wrapperName, ConfigNode snapshot)
        {
            if (parent == null || string.IsNullOrEmpty(wrapperName) || snapshot == null)
                return;

            ConfigNode wrapper = parent.AddNode(wrapperName);
            string nodeName = string.IsNullOrEmpty(snapshot.name) ? "SNAPSHOT" : snapshot.name;
            wrapper.AddNode(nodeName, snapshot.CreateCopy());
        }

        private static ConfigNode LoadInlineSnapshot(ConfigNode parent, string wrapperName)
        {
            if (parent == null || string.IsNullOrEmpty(wrapperName))
                return null;

            ConfigNode wrapper = parent.GetNode(wrapperName);
            if (wrapper == null || wrapper.nodes == null || wrapper.nodes.Count == 0)
                return null;

            ConfigNode snapshot = wrapper.nodes[0];
            return snapshot != null ? snapshot.CreateCopy() : null;
        }

        #endregion

    }
}
