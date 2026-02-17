using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Static holder for recording data that survives scene changes.
    /// Static fields persist across scene loads within a KSP session.
    /// Save/load persistence is handled separately by ParsekScenario.
    /// </summary>
    public static class RecordingStore
    {
        public const int CurrentRecordingFormatVersion = 4;
        public const int CurrentGhostGeometryVersion = 1;

        // When true, suppresses Debug.Log calls (for unit testing outside Unity)
        internal static bool SuppressLogging;

        static void Log(string message)
        {
            if (!SuppressLogging)
            {
                try
                {
                    UnityEngine.Debug.Log(message);
                }
                catch (System.Security.SecurityException)
                {
                    // Unit-test runtime can throw when Unity internals are unavailable.
                }
                catch (MethodAccessException)
                {
                    // Same fallback for some non-Unity execution environments.
                }
            }
        }

        /// <summary>
        /// Recommended merge action based on vessel state after recording.
        /// </summary>
        public enum MergeDefault
        {
            Recover,    // Vessel barely moved — recover for funds
            MergeOnly,  // Vessel destroyed or snapshot missing — merge recording only
            Persist     // Vessel intact and moved — respawn where it ended up
        }

        public class Recording
        {
            public string RecordingId = Guid.NewGuid().ToString("N");
            public int RecordingFormatVersion = CurrentRecordingFormatVersion;
            public int GhostGeometryVersion = CurrentGhostGeometryVersion;
            public List<TrajectoryPoint> Points = new List<TrajectoryPoint>();
            public List<OrbitSegment> OrbitSegments = new List<OrbitSegment>();
            public List<PartEvent> PartEvents = new List<PartEvent>();
            public bool LoopPlayback;
            public double LoopPauseSeconds = 10.0;

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
            public string GhostGeometryCaptureStrategy = "stub_v1";
            public string GhostGeometryProbeStatus = "uninitialized";

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
            public bool TakenControl;               // True after player took control of ghost mid-playback
            public uint SpawnedVesselPersistentId;  // persistentId of spawned vessel (0 = not yet spawned)
            public int SpawnAttempts;               // Number of failed spawn attempts (give up after 3)

            public double StartUT => Points.Count > 0 ? Points[0].ut : 0;
            public double EndUT => Points.Count > 0 ? Points[Points.Count - 1].ut : 0;

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
                GhostGeometryRelativePath = source.GhostGeometryRelativePath;
                GhostGeometryAvailable = source.GhostGeometryAvailable;
                GhostGeometryCaptureError = source.GhostGeometryCaptureError;
                GhostGeometryCaptureStrategy = source.GhostGeometryCaptureStrategy;
                GhostGeometryProbeStatus = source.GhostGeometryProbeStatus;
                RecordingFormatVersion = source.RecordingFormatVersion;
                GhostGeometryVersion = source.GhostGeometryVersion;
                ParentRecordingId = source.ParentRecordingId;
                EvaCrewName = source.EvaCrewName;
                ChainId = source.ChainId;
                ChainIndex = source.ChainIndex;
                ChainBranch = source.ChainBranch;
                LoopPlayback = source.LoopPlayback;
                LoopPauseSeconds = source.LoopPauseSeconds;
            }
        }

        /// <summary>
        /// Determines the recommended merge action based on vessel state.
        /// </summary>
        public static MergeDefault GetRecommendedAction(
            double distance, bool destroyed, bool hasSnapshot,
            double duration = 0, double maxDistance = 0)
        {
            if (destroyed || !hasSnapshot)
            {
                if (distance < 100.0)
                    return MergeDefault.Recover;
                return MergeDefault.MergeOnly;
            }

            // Vessel intact with snapshot — did it actually go somewhere?
            if (distance < 100.0 && (duration <= 10.0 || maxDistance <= 100.0))
                return MergeDefault.Recover;

            return MergeDefault.Persist;
        }

        // Just-finished recording awaiting user decision (merge or discard)
        private static Recording pendingRecording;

        // Merged to timeline — these auto-playback during flight
        private static List<Recording> committedRecordings = new List<Recording>();

        public static bool HasPending => pendingRecording != null;
        public static Recording Pending => pendingRecording;
        public static List<Recording> CommittedRecordings => committedRecordings;

        public static void StashPending(List<TrajectoryPoint> points, string vesselName,
            List<OrbitSegment> orbitSegments = null,
            string recordingId = null,
            int? recordingFormatVersion = null,
            int? ghostGeometryVersion = null,
            List<PartEvent> partEvents = null)
        {
            if (points == null || points.Count < 2)
            {
                Log($"[Parsek] Recording too short for '{vesselName}' ({points?.Count ?? 0} points, need >= 2) — discarded");
                return;
            }

            pendingRecording = new Recording
            {
                RecordingId = string.IsNullOrEmpty(recordingId) ? Guid.NewGuid().ToString("N") : recordingId,
                RecordingFormatVersion = recordingFormatVersion ?? CurrentRecordingFormatVersion,
                GhostGeometryVersion = ghostGeometryVersion ?? CurrentGhostGeometryVersion,
                Points = new List<TrajectoryPoint>(points),
                OrbitSegments = orbitSegments != null
                    ? new List<OrbitSegment>(orbitSegments)
                    : new List<OrbitSegment>(),
                PartEvents = partEvents != null
                    ? new List<PartEvent>(partEvents)
                    : new List<PartEvent>(),
                VesselName = vesselName
            };

            Log($"[Parsek] Stashed pending recording: {points.Count} points, " +
                $"{pendingRecording.OrbitSegments.Count} orbit segments from {vesselName}");
        }

        public static void CommitPending()
        {
            if (pendingRecording == null) return;

            committedRecordings.Add(pendingRecording);
            Log($"[Parsek] Committed recording from {pendingRecording.VesselName} " +
                $"({pendingRecording.Points.Count} points). Total committed: {committedRecordings.Count}");
            pendingRecording = null;
        }

        public static void DiscardPending()
        {
            if (pendingRecording == null) return;

            DeleteRecordingFiles(pendingRecording);
            Log($"[Parsek] Discarded pending recording from {pendingRecording.VesselName}");
            pendingRecording = null;
        }

        public static void ClearCommitted()
        {
            for (int i = 0; i < committedRecordings.Count; i++)
                DeleteRecordingFiles(committedRecordings[i]);
            committedRecordings.Clear();
        }

        public static void Clear()
        {
            if (pendingRecording != null)
                DeleteRecordingFiles(pendingRecording);
            pendingRecording = null;
            ClearCommitted();
            Log("[Parsek] All recordings cleared");
        }

        /// <summary>
        /// Returns true if this recording is a mid-chain segment (not the last in its chain).
        /// Mid-chain ghosts should hold at their final position instead of being despawned.
        /// </summary>
        internal static bool IsChainMidSegment(Recording rec)
        {
            if (string.IsNullOrEmpty(rec.ChainId) || rec.ChainIndex < 0) return false;
            // Branch > 0 segments are parallel continuations (ghost-only); they despawn normally
            if (rec.ChainBranch > 0) return false;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var other = committedRecordings[i];
                if (other.ChainId == rec.ChainId && other.ChainBranch == 0 && other.ChainIndex > rec.ChainIndex)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns the EndUT of the last segment in this recording's chain.
        /// Returns rec.EndUT if the recording is not part of a chain.
        /// </summary>
        internal static double GetChainEndUT(Recording rec)
        {
            if (string.IsNullOrEmpty(rec.ChainId) || rec.ChainIndex < 0) return rec.EndUT;
            double maxEnd = rec.EndUT;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var other = committedRecordings[i];
                // Only branch 0 determines the chain's end (primary path)
                if (other.ChainId == rec.ChainId && other.ChainBranch == 0 && other.EndUT > maxEnd)
                    maxEnd = other.EndUT;
            }
            return maxEnd;
        }

        /// <summary>
        /// Returns all committed recordings with the given chainId, sorted by ChainIndex.
        /// Returns null if chainId is null/empty or no matches found.
        /// </summary>
        internal static List<Recording> GetChainRecordings(string chainId)
        {
            if (string.IsNullOrEmpty(chainId)) return null;

            List<Recording> chain = null;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                if (committedRecordings[i].ChainId == chainId)
                {
                    if (chain == null) chain = new List<Recording>();
                    chain.Add(committedRecordings[i]);
                }
            }

            if (chain != null && chain.Count > 1)
            {
                chain.Sort((a, b) =>
                {
                    int branchCmp = a.ChainBranch.CompareTo(b.ChainBranch);
                    return branchCmp != 0 ? branchCmp : a.ChainIndex.CompareTo(b.ChainIndex);
                });
            }

            return chain;
        }

        /// <summary>
        /// Removes all committed recordings with the given chainId, deleting their files.
        /// Call only when no timeline ghosts are active (e.g. from merge dialog before playback).
        /// </summary>
        internal static void RemoveChainRecordings(string chainId)
        {
            if (string.IsNullOrEmpty(chainId)) return;

            for (int i = committedRecordings.Count - 1; i >= 0; i--)
            {
                if (committedRecordings[i].ChainId == chainId)
                {
                    DeleteRecordingFiles(committedRecordings[i]);
                    Log($"[Parsek] Removed chain recording: {committedRecordings[i].VesselName} (chain={chainId}, idx={committedRecordings[i].ChainIndex})");
                    committedRecordings.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Validates chain integrity among committed recordings.
        /// Chains with gaps, duplicate indices, or non-monotonic StartUT are degraded
        /// to standalone recordings (ChainId/ChainIndex cleared).
        /// </summary>
        internal static void ValidateChains()
        {
            // Group by (ChainId, ChainBranch)
            var branches = new Dictionary<string, List<Recording>>();
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var rec = committedRecordings[i];
                if (string.IsNullOrEmpty(rec.ChainId)) continue;
                string key = rec.ChainId + ":" + rec.ChainBranch;
                List<Recording> list;
                if (!branches.TryGetValue(key, out list))
                {
                    list = new List<Recording>();
                    branches[key] = list;
                }
                list.Add(rec);
            }

            // Track which chainIds are invalid so we degrade all branches together
            var invalidChains = new HashSet<string>();

            foreach (var kvp in branches)
            {
                var list = kvp.Value;
                list.Sort((a, b) => a.ChainIndex.CompareTo(b.ChainIndex));

                string chainId = list[0].ChainId;
                int branch = list[0].ChainBranch;
                bool valid = true;

                if (branch == 0)
                {
                    // Branch 0: indices must be 0..N-1 with no gaps or duplicates
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i].ChainIndex != i)
                        {
                            valid = false;
                            Log($"[Parsek] Chain validation FAILED for chain={chainId} branch={branch}: expected index {i}, got {list[i].ChainIndex}");
                            break;
                        }
                    }
                }
                else
                {
                    // Branch > 0: indices must be contiguous and non-decreasing (don't need to start at 0)
                    for (int i = 1; i < list.Count; i++)
                    {
                        if (list[i].ChainIndex != list[i - 1].ChainIndex + 1)
                        {
                            valid = false;
                            Log($"[Parsek] Chain validation FAILED for chain={chainId} branch={branch}: non-contiguous index at {list[i].ChainIndex}");
                            break;
                        }
                    }
                }

                // Check StartUT is monotonically non-decreasing within branch
                if (valid)
                {
                    for (int i = 1; i < list.Count; i++)
                    {
                        if (list[i].StartUT < list[i - 1].StartUT)
                        {
                            valid = false;
                            Log($"[Parsek] Chain validation FAILED for chain={chainId} branch={branch}: non-monotonic StartUT at index {list[i].ChainIndex}");
                            break;
                        }
                    }
                }

                if (!valid)
                    invalidChains.Add(chainId);
            }

            // Degrade all recordings belonging to invalid chains
            if (invalidChains.Count > 0)
            {
                for (int i = 0; i < committedRecordings.Count; i++)
                {
                    var rec = committedRecordings[i];
                    if (!string.IsNullOrEmpty(rec.ChainId) && invalidChains.Contains(rec.ChainId))
                    {
                        Log($"[Parsek]   Degrading recording '{rec.VesselName}' " +
                            $"(id={rec.RecordingId}, idx={rec.ChainIndex}, branch={rec.ChainBranch}) to standalone");
                        rec.ChainId = null;
                        rec.ChainIndex = -1;
                        rec.ChainBranch = 0;
                    }
                }
                foreach (var chainId in invalidChains)
                    Log($"[Parsek] Degraded invalid chain {chainId} to standalone");
            }
        }

        /// <summary>
        /// Resets state without Unity logging. For unit tests only.
        /// </summary>
        internal static void ResetForTesting()
        {
            pendingRecording = null;
            committedRecordings.Clear();
        }

        internal static void DeleteRecordingFiles(Recording rec)
        {
            if (rec == null) return;
            if (!RecordingPaths.ValidateRecordingId(rec.RecordingId)) return;

            DeleteFileIfExists(RecordingPaths.BuildTrajectoryRelativePath(rec.RecordingId));
            DeleteFileIfExists(RecordingPaths.BuildVesselSnapshotRelativePath(rec.RecordingId));
            DeleteFileIfExists(RecordingPaths.BuildGhostSnapshotRelativePath(rec.RecordingId));
            DeleteFileIfExists(RecordingPaths.BuildGhostGeometryRelativePath(rec.RecordingId));
        }

        private static void DeleteFileIfExists(string relativePath)
        {
            try
            {
                string absolutePath = RecordingPaths.ResolveSaveScopedPath(relativePath);
                if (!string.IsNullOrEmpty(absolutePath) && File.Exists(absolutePath))
                    File.Delete(absolutePath);
            }
            catch { }
        }

        #region Trajectory Serialization

        internal static void SerializeTrajectoryInto(ConfigNode targetNode, Recording rec)
        {
            var ic = CultureInfo.InvariantCulture;
            for (int i = 0; i < rec.Points.Count; i++)
            {
                var pt = rec.Points[i];
                ConfigNode ptNode = targetNode.AddNode("POINT");
                ptNode.AddValue("ut", pt.ut.ToString("R", ic));
                ptNode.AddValue("lat", pt.latitude.ToString("R", ic));
                ptNode.AddValue("lon", pt.longitude.ToString("R", ic));
                ptNode.AddValue("alt", pt.altitude.ToString("R", ic));
                ptNode.AddValue("rotX", pt.rotation.x.ToString("R", ic));
                ptNode.AddValue("rotY", pt.rotation.y.ToString("R", ic));
                ptNode.AddValue("rotZ", pt.rotation.z.ToString("R", ic));
                ptNode.AddValue("rotW", pt.rotation.w.ToString("R", ic));
                ptNode.AddValue("body", pt.bodyName);
                ptNode.AddValue("velX", pt.velocity.x.ToString("R", ic));
                ptNode.AddValue("velY", pt.velocity.y.ToString("R", ic));
                ptNode.AddValue("velZ", pt.velocity.z.ToString("R", ic));
                ptNode.AddValue("funds", pt.funds.ToString("R", ic));
                ptNode.AddValue("science", pt.science.ToString("R", ic));
                ptNode.AddValue("rep", pt.reputation.ToString("R", ic));
            }

            for (int s = 0; s < rec.OrbitSegments.Count; s++)
            {
                var seg = rec.OrbitSegments[s];
                ConfigNode segNode = targetNode.AddNode("ORBIT_SEGMENT");
                segNode.AddValue("startUT", seg.startUT.ToString("R", ic));
                segNode.AddValue("endUT", seg.endUT.ToString("R", ic));
                segNode.AddValue("inc", seg.inclination.ToString("R", ic));
                segNode.AddValue("ecc", seg.eccentricity.ToString("R", ic));
                segNode.AddValue("sma", seg.semiMajorAxis.ToString("R", ic));
                segNode.AddValue("lan", seg.longitudeOfAscendingNode.ToString("R", ic));
                segNode.AddValue("argPe", seg.argumentOfPeriapsis.ToString("R", ic));
                segNode.AddValue("mna", seg.meanAnomalyAtEpoch.ToString("R", ic));
                segNode.AddValue("epoch", seg.epoch.ToString("R", ic));
                segNode.AddValue("body", seg.bodyName);
            }

            for (int pe = 0; pe < rec.PartEvents.Count; pe++)
            {
                var evt = rec.PartEvents[pe];
                ConfigNode evtNode = targetNode.AddNode("PART_EVENT");
                evtNode.AddValue("ut", evt.ut.ToString("R", ic));
                evtNode.AddValue("pid", evt.partPersistentId.ToString(ic));
                evtNode.AddValue("type", ((int)evt.eventType).ToString(ic));
                evtNode.AddValue("part", evt.partName ?? "");
                evtNode.AddValue("value", evt.value.ToString("R", ic));
                evtNode.AddValue("midx", evt.moduleIndex.ToString(ic));
            }
        }

        internal static void DeserializeTrajectoryFrom(ConfigNode sourceNode, Recording rec)
        {
            var inv = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            ConfigNode[] ptNodes = sourceNode.GetNodes("POINT");
            for (int i = 0; i < ptNodes.Length; i++)
            {
                var ptNode = ptNodes[i];
                var pt = new TrajectoryPoint();

                double.TryParse(ptNode.GetValue("ut"), inv, ic, out pt.ut);
                double.TryParse(ptNode.GetValue("lat"), inv, ic, out pt.latitude);
                double.TryParse(ptNode.GetValue("lon"), inv, ic, out pt.longitude);
                double.TryParse(ptNode.GetValue("alt"), inv, ic, out pt.altitude);

                float rx, ry, rz, rw;
                float.TryParse(ptNode.GetValue("rotX"), inv, ic, out rx);
                float.TryParse(ptNode.GetValue("rotY"), inv, ic, out ry);
                float.TryParse(ptNode.GetValue("rotZ"), inv, ic, out rz);
                float.TryParse(ptNode.GetValue("rotW"), inv, ic, out rw);
                pt.rotation = new Quaternion(rx, ry, rz, rw);

                pt.bodyName = ptNode.GetValue("body") ?? "Kerbin";

                float velX, velY, velZ;
                float.TryParse(ptNode.GetValue("velX"), inv, ic, out velX);
                float.TryParse(ptNode.GetValue("velY"), inv, ic, out velY);
                float.TryParse(ptNode.GetValue("velZ"), inv, ic, out velZ);
                pt.velocity = new Vector3(velX, velY, velZ);

                double funds;
                double.TryParse(ptNode.GetValue("funds"), inv, ic, out funds);
                pt.funds = funds;

                float science, rep;
                float.TryParse(ptNode.GetValue("science"), inv, ic, out science);
                float.TryParse(ptNode.GetValue("rep"), inv, ic, out rep);
                pt.science = science;
                pt.reputation = rep;

                rec.Points.Add(pt);
            }

            ConfigNode[] segNodes = sourceNode.GetNodes("ORBIT_SEGMENT");
            for (int s = 0; s < segNodes.Length; s++)
            {
                var segNode = segNodes[s];
                var seg = new OrbitSegment();

                double.TryParse(segNode.GetValue("startUT"), inv, ic, out seg.startUT);
                double.TryParse(segNode.GetValue("endUT"), inv, ic, out seg.endUT);
                double.TryParse(segNode.GetValue("inc"), inv, ic, out seg.inclination);
                double.TryParse(segNode.GetValue("ecc"), inv, ic, out seg.eccentricity);
                double.TryParse(segNode.GetValue("sma"), inv, ic, out seg.semiMajorAxis);
                double.TryParse(segNode.GetValue("lan"), inv, ic, out seg.longitudeOfAscendingNode);
                double.TryParse(segNode.GetValue("argPe"), inv, ic, out seg.argumentOfPeriapsis);
                double.TryParse(segNode.GetValue("mna"), inv, ic, out seg.meanAnomalyAtEpoch);
                double.TryParse(segNode.GetValue("epoch"), inv, ic, out seg.epoch);
                seg.bodyName = segNode.GetValue("body") ?? "Kerbin";

                rec.OrbitSegments.Add(seg);
            }

            ConfigNode[] peNodes = sourceNode.GetNodes("PART_EVENT");
            for (int pe = 0; pe < peNodes.Length; pe++)
            {
                var peNode = peNodes[pe];
                var evt = new PartEvent();

                double.TryParse(peNode.GetValue("ut"), inv, ic, out evt.ut);
                uint pid;
                if (uint.TryParse(peNode.GetValue("pid"), NumberStyles.Integer, ic, out pid))
                    evt.partPersistentId = pid;
                int typeInt;
                if (int.TryParse(peNode.GetValue("type"), NumberStyles.Integer, ic, out typeInt))
                    evt.eventType = (PartEventType)typeInt;
                evt.partName = peNode.GetValue("part") ?? "";

                float val;
                if (float.TryParse(peNode.GetValue("value"), inv, ic, out val))
                    evt.value = val;
                int midx;
                if (int.TryParse(peNode.GetValue("midx"), NumberStyles.Integer, ic, out midx))
                    evt.moduleIndex = midx;

                rec.PartEvents.Add(evt);
            }
        }

        #endregion

        #region Recording File I/O

        internal static bool SaveRecordingFiles(Recording rec)
        {
            if (rec == null || !RecordingPaths.ValidateRecordingId(rec.RecordingId))
                return false;

            try
            {
                string dir = RecordingPaths.EnsureRecordingsDirectory();
                if (dir == null) return false;

                // Save .prec trajectory file
                var precNode = new ConfigNode("PARSEK_RECORDING");
                precNode.AddValue("version", CurrentRecordingFormatVersion);
                precNode.AddValue("recordingId", rec.RecordingId);
                SerializeTrajectoryInto(precNode, rec);

                string precPath = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildTrajectoryRelativePath(rec.RecordingId));
                SafeWriteConfigNode(precNode, precPath);

                // Save _vessel.craft (always rewrite — snapshot can be mutated by spawn offset)
                string vesselPath = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildVesselSnapshotRelativePath(rec.RecordingId));
                if (rec.VesselSnapshot != null)
                {
                    SafeWriteConfigNode(rec.VesselSnapshot, vesselPath);
                }
                else if (File.Exists(vesselPath))
                {
                    File.Delete(vesselPath);
                }

                // Save _ghost.craft (write once — immutable after creation)
                if (rec.GhostVisualSnapshot != null)
                {
                    string ghostPath = RecordingPaths.ResolveSaveScopedPath(
                        RecordingPaths.BuildGhostSnapshotRelativePath(rec.RecordingId));
                    if (!File.Exists(ghostPath))
                        SafeWriteConfigNode(rec.GhostVisualSnapshot, ghostPath);
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"[Parsek] Failed to save recording files for {rec.RecordingId}: {ex.Message}");
                return false;
            }
        }

        internal static bool LoadRecordingFiles(Recording rec)
        {
            if (rec == null || !RecordingPaths.ValidateRecordingId(rec.RecordingId))
                return false;

            try
            {
                // Load .prec trajectory file
                // ConfigNode.Save writes the node's contents (values + children),
                // and ConfigNode.Load returns a node containing those contents directly.
                string precPath = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildTrajectoryRelativePath(rec.RecordingId));
                if (string.IsNullOrEmpty(precPath) || !File.Exists(precPath))
                {
                    Log($"[Parsek] Trajectory file missing for {rec.RecordingId} — recording degraded (0 points)");
                    return false;
                }

                var precNode = ConfigNode.Load(precPath);
                if (precNode == null)
                {
                    Log($"[Parsek] Invalid trajectory file for {rec.RecordingId} — failed to parse");
                    return false;
                }

                // Validate recordingId inside file matches
                string fileId = precNode.GetValue("recordingId");
                if (fileId != null && fileId != rec.RecordingId)
                {
                    Log($"[Parsek] Recording ID mismatch in {rec.RecordingId}.prec: file says '{fileId}' — skipping");
                    return false;
                }

                DeserializeTrajectoryFrom(precNode, rec);

                // Load _vessel.craft — ConfigNode.Load returns the snapshot directly
                string vesselPath = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildVesselSnapshotRelativePath(rec.RecordingId));
                if (!string.IsNullOrEmpty(vesselPath) && File.Exists(vesselPath))
                {
                    var vesselNode = ConfigNode.Load(vesselPath);
                    if (vesselNode != null)
                        rec.VesselSnapshot = vesselNode;
                }

                // Load _ghost.craft
                string ghostPath = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildGhostSnapshotRelativePath(rec.RecordingId));
                if (!string.IsNullOrEmpty(ghostPath) && File.Exists(ghostPath))
                {
                    var ghostNode = ConfigNode.Load(ghostPath);
                    if (ghostNode != null)
                        rec.GhostVisualSnapshot = ghostNode;
                }

                // Backward compat: if no ghost snapshot, fall back to vessel snapshot
                if (rec.GhostVisualSnapshot == null && rec.VesselSnapshot != null)
                    rec.GhostVisualSnapshot = rec.VesselSnapshot.CreateCopy();

                return true;
            }
            catch (Exception ex)
            {
                Log($"[Parsek] Failed to load recording files for {rec.RecordingId}: {ex.Message}");
                return false;
            }
        }

        private static void SafeWriteConfigNode(ConfigNode node, string path)
        {
            string tmpPath = path + ".tmp";
            node.Save(tmpPath);
            if (File.Exists(path))
                File.Delete(path);
            File.Move(tmpPath, path);
        }

        #endregion
    }
}
