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

        public Dictionary<string, RecordingStore.Recording> Recordings
            = new Dictionary<string, RecordingStore.Recording>();
        public List<BranchPoint> BranchPoints = new List<BranchPoint>();

        // Tree-level resource tracking
        public double PreTreeFunds;
        public double PreTreeScience;
        public float PreTreeReputation;
        public double DeltaFunds;
        public double DeltaScience;
        public float DeltaReputation;

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

            // Load recordings
            ConfigNode[] recNodes = treeNode.GetNodes("RECORDING");
            for (int i = 0; i < recNodes.Length; i++)
            {
                var rec = new RecordingStore.Recording();
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
                    BackgroundMap[rec.VesselPersistentId] = rec.RecordingId;
                }
            }
        }

        // --- Recording serialization helpers ---

        internal static void SaveRecordingInto(ConfigNode recNode, RecordingStore.Recording rec)
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

            // Existing recording metadata
            recNode.AddValue("recordingFormatVersion", rec.RecordingFormatVersion);
            recNode.AddValue("ghostGeometryVersion", rec.GhostGeometryVersion);
            recNode.AddValue("loopPlayback", rec.LoopPlayback);
            recNode.AddValue("loopPauseSeconds", rec.LoopPauseSeconds.ToString("R", ic));
            if (!rec.PlaybackEnabled)
                recNode.AddValue("playbackEnabled", rec.PlaybackEnabled.ToString());

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

            // Ghost geometry metadata
            recNode.AddValue("ghostGeometryStrategy", rec.GhostGeometryCaptureStrategy ?? "stub_v1");
            recNode.AddValue("ghostGeometryProbeStatus", rec.GhostGeometryProbeStatus ?? "unknown");
            if (!string.IsNullOrEmpty(rec.GhostGeometryRelativePath))
                recNode.AddValue("ghostGeometryPath", rec.GhostGeometryRelativePath);
            recNode.AddValue("ghostGeometryAvailable", rec.GhostGeometryAvailable);
            if (!string.IsNullOrEmpty(rec.GhostGeometryCaptureError))
                recNode.AddValue("ghostGeometryError", rec.GhostGeometryCaptureError);
        }

        internal static void LoadRecordingFrom(ConfigNode recNode, RecordingStore.Recording rec)
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

            string loopPauseStr = recNode.GetValue("loopPauseSeconds");
            if (loopPauseStr != null)
            {
                double loopPauseSeconds;
                if (double.TryParse(loopPauseStr, inv, ic, out loopPauseSeconds))
                    rec.LoopPauseSeconds = loopPauseSeconds;
            }

            string playbackEnabledStr = recNode.GetValue("playbackEnabled");
            if (playbackEnabledStr != null)
            {
                bool playbackEnabled;
                if (bool.TryParse(playbackEnabledStr, out playbackEnabled))
                    rec.PlaybackEnabled = playbackEnabled;
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
        }

        // --- BranchPoint serialization helpers ---

        internal static void SaveBranchPointInto(ConfigNode bpNode, BranchPoint bp)
        {
            var ic = CultureInfo.InvariantCulture;

            bpNode.AddValue("id", bp.id ?? "");
            bpNode.AddValue("ut", bp.ut.ToString("R", ic));
            bpNode.AddValue("type", ((int)bp.type).ToString(ic));

            for (int i = 0; i < bp.parentRecordingIds.Count; i++)
                bpNode.AddValue("parentId", bp.parentRecordingIds[i] ?? "");

            for (int i = 0; i < bp.childRecordingIds.Count; i++)
                bpNode.AddValue("childId", bp.childRecordingIds[i] ?? "");
        }

        internal static BranchPoint LoadBranchPointFrom(ConfigNode bpNode)
        {
            var inv = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            var bp = new BranchPoint();
            bp.id = bpNode.GetValue("id") ?? "";
            double.TryParse(bpNode.GetValue("ut"), inv, ic, out bp.ut);

            int typeInt;
            if (int.TryParse(bpNode.GetValue("type"), NumberStyles.Integer, ic, out typeInt)
                && Enum.IsDefined(typeof(BranchPointType), typeInt))
                bp.type = (BranchPointType)typeInt;

            string[] parentIds = bpNode.GetValues("parentId");
            bp.parentRecordingIds = new List<string>(parentIds);

            string[] childIds = bpNode.GetValues("childId");
            bp.childRecordingIds = new List<string>(childIds);

            return bp;
        }
    }
}
