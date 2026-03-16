using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    public class RecordingTree
    {
        // --- Serialized fields ---
        public string Id;
        public string TreeName;                     // root vessel name at launch
        public string RootRecordingId;
        public string ActiveRecordingId;            // nullable: null if all background

        public Dictionary<string, Recording> Recordings
            = new Dictionary<string, Recording>();
        public List<BranchPoint> BranchPoints = new List<BranchPoint>();

        // Tree-level resource tracking
        public double PreTreeFunds;
        public double PreTreeScience;
        public float PreTreeReputation;
        public double DeltaFunds;
        public double DeltaScience;
        public float DeltaReputation;
        public bool ResourcesApplied;

        // --- Runtime-only fields (not serialized) ---
        // Maps vessel persistentId -> recordingId for background vessel lookup.
        // Rebuilt from Recordings on load via RebuildBackgroundMap().
        public Dictionary<uint, string> BackgroundMap
            = new Dictionary<uint, string>();

        // --- Serialization ---

        public void Save(ConfigNode treeNode)
        {
            var ic = CultureInfo.InvariantCulture;

            treeNode.AddValue("id", Id ?? "");
            treeNode.AddValue("treeName", TreeName ?? "");
            treeNode.AddValue("rootRecordingId", RootRecordingId ?? "");
            if (ActiveRecordingId != null)
                treeNode.AddValue("activeRecordingId", ActiveRecordingId);
            treeNode.AddValue("preTreeFunds", PreTreeFunds.ToString("R", ic));
            treeNode.AddValue("preTreeScience", PreTreeScience.ToString("R", ic));
            treeNode.AddValue("preTreeRep", PreTreeReputation.ToString("R", ic));
            treeNode.AddValue("deltaFunds", DeltaFunds.ToString("R", ic));
            treeNode.AddValue("deltaScience", DeltaScience.ToString("R", ic));
            treeNode.AddValue("deltaRep", DeltaReputation.ToString("R", ic));
            treeNode.AddValue("resourcesApplied", ResourcesApplied.ToString());

            // Serialize each recording
            foreach (var rec in Recordings.Values)
            {
                ConfigNode recNode = treeNode.AddNode("RECORDING");
                SaveRecordingInto(recNode, rec);
            }

            // Serialize branch points
            for (int i = 0; i < BranchPoints.Count; i++)
            {
                ConfigNode bpNode = treeNode.AddNode("BRANCH_POINT");
                SaveBranchPointInto(bpNode, BranchPoints[i]);
            }

            ParsekLog.Verbose("RecordingTree",
                $"Save: tree='{TreeName}' recordings={Recordings.Count} branchPoints={BranchPoints.Count} resourcesApplied={ResourcesApplied}");
        }

        public static RecordingTree Load(ConfigNode treeNode)
        {
            var inv = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            var tree = new RecordingTree();
            tree.Id = treeNode.GetValue("id") ?? "";
            tree.TreeName = treeNode.GetValue("treeName") ?? "";
            tree.RootRecordingId = treeNode.GetValue("rootRecordingId") ?? "";

            tree.ActiveRecordingId = treeNode.GetValue("activeRecordingId");

            double.TryParse(treeNode.GetValue("preTreeFunds"), inv, ic, out tree.PreTreeFunds);
            double.TryParse(treeNode.GetValue("preTreeScience"), inv, ic, out tree.PreTreeScience);
            float preTreeRep;
            float.TryParse(treeNode.GetValue("preTreeRep"), inv, ic, out preTreeRep);
            tree.PreTreeReputation = preTreeRep;
            double.TryParse(treeNode.GetValue("deltaFunds"), inv, ic, out tree.DeltaFunds);
            double.TryParse(treeNode.GetValue("deltaScience"), inv, ic, out tree.DeltaScience);
            float deltaRep;
            float.TryParse(treeNode.GetValue("deltaRep"), inv, ic, out deltaRep);
            tree.DeltaReputation = deltaRep;

            string resourcesAppliedStr = treeNode.GetValue("resourcesApplied");
            if (resourcesAppliedStr != null)
            {
                bool resourcesApplied;
                if (bool.TryParse(resourcesAppliedStr, out resourcesApplied))
                    tree.ResourcesApplied = resourcesApplied;
            }

            // Load recordings
            ConfigNode[] recNodes = treeNode.GetNodes("RECORDING");
            for (int i = 0; i < recNodes.Length; i++)
            {
                var rec = new Recording();
                LoadRecordingFrom(recNodes[i], rec);
                tree.Recordings[rec.RecordingId] = rec;
            }

            // Load branch points
            ConfigNode[] bpNodes = treeNode.GetNodes("BRANCH_POINT");
            for (int i = 0; i < bpNodes.Length; i++)
            {
                tree.BranchPoints.Add(LoadBranchPointFrom(bpNodes[i]));
            }

            tree.RebuildBackgroundMap();

            ParsekLog.Verbose("RecordingTree",
                $"Load: id={tree.Id} tree='{tree.TreeName}' recordings={tree.Recordings.Count} " +
                $"branchPoints={tree.BranchPoints.Count} resourcesApplied={tree.ResourcesApplied} " +
                $"resourcesAppliedFieldPresent={resourcesAppliedStr != null}");
            return tree;
        }

        public void RebuildBackgroundMap()
        {
            BackgroundMap.Clear();
            foreach (var kvp in Recordings)
            {
                var rec = kvp.Value;
                if (rec.VesselPersistentId != 0
                    && rec.TerminalStateValue == null
                    && rec.ChildBranchPointId == null  // has branched → no longer a live recording
                    && rec.RecordingId != ActiveRecordingId)
                {
                    if (BackgroundMap.ContainsKey(rec.VesselPersistentId))
                        ParsekLog.Warn("RecordingTree",
                            $"RebuildBackgroundMap: duplicate PID={rec.VesselPersistentId} " +
                            $"(existing={BackgroundMap[rec.VesselPersistentId]}, replacing with={rec.RecordingId})");
                    BackgroundMap[rec.VesselPersistentId] = rec.RecordingId;
                }
            }

            ParsekLog.Verbose("RecordingTree",
                $"RebuildBackgroundMap: entries={BackgroundMap.Count} totalRecordings={Recordings.Count}");
        }

        // --- Recording serialization helpers ---

        internal static void SaveRecordingInto(ConfigNode recNode, Recording rec)
        {
            var ic = CultureInfo.InvariantCulture;

            recNode.AddValue("recordingId", rec.RecordingId ?? "");
            recNode.AddValue("vesselName", rec.VesselName ?? "");
            if (rec.TreeId != null)
                recNode.AddValue("treeId", rec.TreeId);
            recNode.AddValue("vesselPersistentId", rec.VesselPersistentId.ToString(ic));

            if (!double.IsNaN(rec.ExplicitStartUT))
                recNode.AddValue("explicitStartUT", rec.ExplicitStartUT.ToString("R", ic));
            if (!double.IsNaN(rec.ExplicitEndUT))
                recNode.AddValue("explicitEndUT", rec.ExplicitEndUT.ToString("R", ic));

            if (rec.TerminalStateValue.HasValue)
                recNode.AddValue("terminalState", ((int)rec.TerminalStateValue.Value).ToString(ic));

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
        /// Saves playback settings (format version, loop, playback enabled),
        /// EVA child linkage, and chain linkage into a RECORDING ConfigNode.
        /// </summary>
        private static void SaveRecordingPlaybackAndLinkage(ConfigNode recNode, Recording rec)
        {
            var ic = CultureInfo.InvariantCulture;

            // Existing recording metadata
            recNode.AddValue("recordingFormatVersion", rec.RecordingFormatVersion);
            recNode.AddValue("loopPlayback", rec.LoopPlayback);
            recNode.AddValue("loopIntervalSeconds", rec.LoopIntervalSeconds.ToString("R", ic));
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
        private static void SaveRecordingResourceAndState(ConfigNode recNode, Recording rec)
        {
            var ic = CultureInfo.InvariantCulture;

            // Atmosphere segment metadata
            if (!string.IsNullOrEmpty(rec.SegmentPhase))
                recNode.AddValue("segmentPhase", rec.SegmentPhase);
            if (!string.IsNullOrEmpty(rec.SegmentBodyName))
                recNode.AddValue("segmentBodyName", rec.SegmentBodyName);

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
            if (rec.VesselDestroyed)
                recNode.AddValue("vesselDestroyed", rec.VesselDestroyed.ToString());
            recNode.AddValue("lastResIdx", rec.LastAppliedResourceIndex);
            recNode.AddValue("pointCount", rec.Points != null ? rec.Points.Count : 0);

            // UI grouping tags (multi-group membership)
            if (rec.RecordingGroups != null)
                for (int g = 0; g < rec.RecordingGroups.Count; g++)
                    recNode.AddValue("recordingGroup", rec.RecordingGroups[g]);
        }

        internal static void LoadRecordingFrom(ConfigNode recNode, Recording rec)
        {
            var inv = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            string id = recNode.GetValue("recordingId");
            if (!string.IsNullOrEmpty(id))
                rec.RecordingId = id;

            rec.VesselName = recNode.GetValue("vesselName") ?? "";
            rec.TreeId = recNode.GetValue("treeId");

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

            // Background surface position
            ConfigNode spNode = recNode.GetNode("SURFACE_POSITION");
            if (spNode != null)
                rec.SurfacePos = SurfacePosition.LoadFrom(spNode);

            // Playback settings, linkage, and chain metadata
            LoadRecordingPlaybackAndLinkage(recNode, rec);

            // Resource, rewind, geometry, and mutable state
            LoadRecordingResourceAndState(recNode, rec);

            ParsekLog.Verbose("RecordingTree",
                $"LoadRecordingFrom: id={rec.RecordingId} vessel='{rec.VesselName}' " +
                $"terminal={rec.TerminalStateValue?.ToString() ?? "null"} " +
                $"chain={rec.ChainId ?? "none"} formatVersion={rec.RecordingFormatVersion}");
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
            }

            string geomVersionStr = recNode.GetValue("ghostGeometryVersion");
            if (geomVersionStr != null)
            {
                int geomVersion;
                if (int.TryParse(geomVersionStr, NumberStyles.Integer, ic, out geomVersion))
                    rec.GhostGeometryVersion = geomVersion;
            }

            string loopPlaybackStr = recNode.GetValue("loopPlayback");
            if (loopPlaybackStr != null)
            {
                bool loopPlayback;
                if (bool.TryParse(loopPlaybackStr, out loopPlayback))
                    rec.LoopPlayback = loopPlayback;
            }

            string loopIntervalStr = recNode.GetValue("loopIntervalSeconds")
                                   ?? recNode.GetValue("loopPauseSeconds"); // migration fallback
            if (loopIntervalStr != null)
            {
                double loopIntervalSeconds;
                if (double.TryParse(loopIntervalStr, inv, ic, out loopIntervalSeconds))
                    rec.LoopIntervalSeconds = loopIntervalSeconds;
            }

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
        private static void LoadRecordingResourceAndState(ConfigNode recNode, Recording rec)
        {
            var inv = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            // Atmosphere segment metadata
            rec.SegmentPhase = recNode.GetValue("segmentPhase");
            rec.SegmentBodyName = recNode.GetValue("segmentBodyName");

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

            // Ghost geometry metadata
            rec.GhostGeometryRelativePath = recNode.GetValue("ghostGeometryPath");
            string strategy = recNode.GetValue("ghostGeometryStrategy");
            if (!string.IsNullOrEmpty(strategy))
                rec.GhostGeometryCaptureStrategy = strategy;
            string probeStatus = recNode.GetValue("ghostGeometryProbeStatus");
            if (!string.IsNullOrEmpty(probeStatus))
                rec.GhostGeometryProbeStatus = probeStatus;
            string geomAvailableStr = recNode.GetValue("ghostGeometryAvailable");
            if (geomAvailableStr != null)
            {
                bool geomAvailable;
                if (bool.TryParse(geomAvailableStr, out geomAvailable))
                    rec.GhostGeometryAvailable = geomAvailable;
            }
            rec.GhostGeometryCaptureError = recNode.GetValue("ghostGeometryError");

            // Mutable playback state
            string pidStr = recNode.GetValue("spawnedPid");
            if (pidStr != null)
            {
                uint spawnedPid;
                if (uint.TryParse(pidStr, NumberStyles.Integer, ic, out spawnedPid))
                    rec.SpawnedVesselPersistentId = spawnedPid;
            }
            string destroyedStr = recNode.GetValue("vesselDestroyed");
            if (destroyedStr != null)
            {
                bool destroyed;
                if (bool.TryParse(destroyedStr, out destroyed))
                    rec.VesselDestroyed = destroyed;
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
                rec.RecordingGroups = new List<string>(recGroups);
        }

        #endregion

        // --- BranchPoint serialization helpers ---

        internal static void SaveBranchPointInto(ConfigNode bpNode, BranchPoint bp)
        {
            var ic = CultureInfo.InvariantCulture;

            bpNode.AddValue("id", bp.Id ?? "");
            bpNode.AddValue("ut", bp.UT.ToString("R", ic));
            bpNode.AddValue("type", ((int)bp.Type).ToString(ic));

            for (int i = 0; i < bp.ParentRecordingIds.Count; i++)
                bpNode.AddValue("parentId", bp.ParentRecordingIds[i] ?? "");

            for (int i = 0; i < bp.ChildRecordingIds.Count; i++)
                bpNode.AddValue("childId", bp.ChildRecordingIds[i] ?? "");
        }

        internal static BranchPoint LoadBranchPointFrom(ConfigNode bpNode)
        {
            var inv = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            var bp = new BranchPoint();
            bp.Id = bpNode.GetValue("id") ?? "";
            double.TryParse(bpNode.GetValue("ut"), inv, ic, out bp.UT);

            int typeInt;
            if (int.TryParse(bpNode.GetValue("type"), NumberStyles.Integer, ic, out typeInt)
                && Enum.IsDefined(typeof(BranchPointType), typeInt))
                bp.Type = (BranchPointType)typeInt;

            string[] parentIds = bpNode.GetValues("parentId");
            bp.ParentRecordingIds = new List<string>(parentIds);

            string[] childIds = bpNode.GetValues("childId");
            bp.ChildRecordingIds = new List<string>(childIds);

            return bp;
        }

        // --- Leaf identification ---

        /// <summary>
        /// Identifies spawnable leaf recordings: no children, not terminal
        /// (Destroyed/Recovered/Docked/Boarded), has vessel snapshot.
        /// </summary>
        public List<Recording> GetSpawnableLeaves()
        {
            var leaves = new List<Recording>();
            foreach (var rec in Recordings.Values)
            {
                if (IsSpawnableLeaf(rec))
                    leaves.Add(rec);
            }
            return leaves;
        }

        /// <summary>
        /// Identifies ALL leaf recordings (including destroyed/recovered).
        /// A leaf is any recording with ChildBranchPointId == null.
        /// </summary>
        public List<Recording> GetAllLeaves()
        {
            var leaves = new List<Recording>();
            foreach (var rec in Recordings.Values)
            {
                if (rec.ChildBranchPointId == null)
                    leaves.Add(rec);
            }
            return leaves;
        }

        /// <summary>
        /// Checks whether a recording is a spawnable leaf:
        /// 1. No children (ChildBranchPointId is null)
        /// 2. Terminal state allows spawning (not Destroyed/Recovered/Docked/Boarded)
        /// 3. Has a vessel snapshot
        /// </summary>
        internal static bool IsSpawnableLeaf(Recording rec)
        {
            if (rec.ChildBranchPointId != null)
                return false;

            if (rec.TerminalStateValue.HasValue)
            {
                var ts = rec.TerminalStateValue.Value;
                if (ts == TerminalState.Destroyed || ts == TerminalState.Recovered
                    || ts == TerminalState.Docked || ts == TerminalState.Boarded)
                    return false;
            }

            if (rec.VesselSnapshot == null)
                return false;

            return true;
        }

        /// <summary>
        /// Assigns terminal state based on vessel situation.
        /// Pure static method for testability.
        /// </summary>
        internal static TerminalState DetermineTerminalState(int situation)
        {
            // Vessel.Situations enum values: LANDED=1, SPLASHED=2, PRELAUNCH=4,
            // FLYING=8, SUB_ORBITAL=16, ORBITING=32, ESCAPING=64, DOCKED=128
            switch (situation)
            {
                case 32: // ORBITING
                    return TerminalState.Orbiting;
                case 1: // LANDED
                    return TerminalState.Landed;
                case 2: // SPLASHED
                    return TerminalState.Splashed;
                case 16: // SUB_ORBITAL
                case 8:  // FLYING
                case 64: // ESCAPING
                    return TerminalState.SubOrbital;
                case 4:  // PRELAUNCH
                    return TerminalState.Landed;
                case 128: // DOCKED — handled via explicit TerminalState.Docked in merge logic
                    return TerminalState.Orbiting;
                default:
                    ParsekLog.Warn("RecordingTree", $"DetermineTerminalState: unexpected situation={situation}, defaulting to SubOrbital");
                    return TerminalState.SubOrbital;
            }
        }

        /// <summary>
        /// Pure decision method: checks whether all leaf recordings in a tree have
        /// non-spawnable terminal states (Destroyed, Recovered, Docked, Boarded).
        /// A recording is a leaf if it has no ChildBranchPointId.
        /// Leaves with null TerminalStateValue are considered NOT terminal (still active).
        /// If activeRecordingId is non-null, the active recording is treated as alive
        /// unless activeVesselDestroyed is true.
        /// </summary>
        internal static bool AreAllLeavesTerminal(
            Dictionary<string, Recording> recordings,
            string activeRecordingId,
            bool activeVesselDestroyed)
        {
            if (recordings.Count == 0)
            {
                ParsekLog.Verbose("TreeDestruction", "AreAllLeavesTerminal: empty recordings dict — returning true");
                return true;
            }

            foreach (var kvp in recordings)
            {
                var rec = kvp.Value;

                // Skip non-leaf recordings (they branched into children)
                if (rec.ChildBranchPointId != null)
                    continue;

                bool isActiveRecording = activeRecordingId != null && rec.RecordingId == activeRecordingId;

                // Leaf: active recording still alive — tree is not fully terminal
                if (isActiveRecording && !activeVesselDestroyed)
                {
                    ParsekLog.Verbose("TreeDestruction",
                        $"AreAllLeavesTerminal: leaf '{rec.RecordingId}' ({rec.VesselName}) is active and alive — NOT terminal");
                    return false;
                }

                // Active recording with destroyed vessel: treat as terminal even if
                // TerminalStateValue hasn't been set yet (recorder knows it's dead,
                // but FinalizeTreeRecordings hasn't run to set the terminal state)
                if (isActiveRecording && activeVesselDestroyed)
                {
                    ParsekLog.Verbose("TreeDestruction",
                        $"AreAllLeavesTerminal: leaf '{rec.RecordingId}' ({rec.VesselName}) is active but vessel destroyed — terminal");
                    continue;
                }

                // Leaf: no terminal state means still recording / not finalized
                if (!rec.TerminalStateValue.HasValue)
                {
                    ParsekLog.Verbose("TreeDestruction",
                        $"AreAllLeavesTerminal: leaf '{rec.RecordingId}' ({rec.VesselName}) has no terminal state — NOT terminal");
                    return false;
                }

                var ts = rec.TerminalStateValue.Value;
                if (ts == TerminalState.Destroyed || ts == TerminalState.Recovered
                    || ts == TerminalState.Docked || ts == TerminalState.Boarded)
                {
                    // Non-spawnable terminal state — this leaf is done
                    ParsekLog.Verbose("TreeDestruction",
                        $"AreAllLeavesTerminal: leaf '{rec.RecordingId}' ({rec.VesselName}) terminal={ts} — dead");
                    continue;
                }

                // Spawnable terminal state (Orbiting, Landed, SubOrbital, Splashed) — vessel could be spawned
                ParsekLog.Verbose("TreeDestruction",
                    $"AreAllLeavesTerminal: leaf '{rec.RecordingId}' ({rec.VesselName}) terminal={ts} — spawnable, NOT terminal");
                return false;
            }

            ParsekLog.Verbose("TreeDestruction", "AreAllLeavesTerminal: all leaves are terminal — returning true");
            return true;
        }
    }
}
