using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Parsek
{
    // The rewind point quicksave: its save-root copy, the selected-slot scrub, sidecar epoch refresh, slot pid sets and affinity, and the forced throttle close.
    internal static partial class RewindInvoker
    {
        internal static string ResolveAbsoluteQuicksavePath(RewindPoint rp)
        {
            if (rp == null || string.IsNullOrEmpty(rp.QuicksaveFilename))
                return null;
            if (ResolveAbsoluteQuicksavePathOverrideForTesting != null)
                return ResolveAbsoluteQuicksavePathOverrideForTesting(rp);
            return RecordingPaths.ResolveSaveScopedPath(rp.QuicksaveFilename);
        }

        /// <summary>
        /// Copies the RP's quicksave from
        /// <c>saves/&lt;save&gt;/Parsek/RewindPoints/&lt;rpId&gt;.sfs</c> to
        /// <c>saves/&lt;save&gt;/Parsek_Rewind_&lt;sessionId&gt;.sfs</c> (save
        /// root), because KSP's <c>GamePersistence.LoadGame(fileName, folder, ...)</c>
        /// does not accept subdirectory paths.
        /// <para>
        /// Returns the absolute temp path (for post-load deletion) and the
        /// base filename without the <c>.sfs</c> extension (to pass to
        /// <c>LoadGame</c>). Throws on I/O failure.
        /// </para>
        /// </summary>
        internal static void CopyQuicksaveToSaveRoot(
            RewindPoint rp, string sessionId,
            out string tempAbsolutePath, out string tempLoadName)
        {
            tempAbsolutePath = null;
            tempLoadName = null;

            string sourceAbs = ResolveAbsoluteQuicksavePath(rp);
            if (string.IsNullOrEmpty(sourceAbs) || !File.Exists(sourceAbs))
            {
                ParsekLog.Error(InvokeTag,
                    $"CopyQuicksaveToSaveRoot: source missing " +
                    $"rp={rp?.RewindPointId} source='{sourceAbs ?? "<null>"}'");
                return;
            }

            string root = KSPUtil.ApplicationRootPath ?? "";
            string saveFolder = HighLogic.SaveFolder ?? "";
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(saveFolder))
            {
                ParsekLog.Error(InvokeTag,
                    $"CopyQuicksaveToSaveRoot: missing KSP paths " +
                    $"rootSet={!string.IsNullOrEmpty(root)} saveSet={!string.IsNullOrEmpty(saveFolder)}");
                return;
            }

            string saveRoot = Path.Combine(root, "saves", saveFolder);
            string tempName = "Parsek_Rewind_" + sessionId;
            string destAbs = Path.Combine(saveRoot, tempName + ".sfs");

            File.Copy(sourceAbs, destAbs, overwrite: true);
            tempAbsolutePath = destAbs;
            tempLoadName = tempName;
            ParsekLog.Verbose(InvokeTag,
                $"CopyQuicksaveToSaveRoot: '{sourceAbs}' -> '{destAbs}' loadName='{tempName}'");
        }

        /// <summary>
        /// Production Re-Fly staging path: copy the RP quicksave to the save root,
        /// then scrub that temp copy down to the selected slot before KSP loads it.
        /// The RP source save and persistent save are not modified.
        /// </summary>
        internal static void CopyQuicksaveToSaveRoot(
            RewindPoint rp, ChildSlot selected, string sessionId,
            out string tempAbsolutePath, out string tempLoadName)
        {
            CopyQuicksaveToSaveRoot(rp, sessionId, out tempAbsolutePath, out tempLoadName);
            if (!string.IsNullOrEmpty(tempAbsolutePath) && selected != null)
            {
                RequireSelectedSlotScrubApplied(tempAbsolutePath, rp, selected.SlotIndex);
            }
        }

        internal static ReFlySaveScrubResult RequireSelectedSlotScrubApplied(
            string tempAbsolutePath, RewindPoint rp, int selectedSlotIndex)
        {
            ReFlySaveScrubResult scrubResult = ScrubQuicksaveToSelectedSlotForReFly(
                tempAbsolutePath, rp, selectedSlotIndex);
            if (!scrubResult.Applied)
            {
                throw new InvalidOperationException(
                    "Re-Fly temp save scrub failed; refusing to load unscrubbed quicksave");
            }
            return scrubResult;
        }

        internal struct ReFlySaveScrubResult
        {
            public bool Applied;
            public int VesselCountBefore;
            public int VesselsKept;

            /// <summary>
            /// Subset of <see cref="VesselsKept"/> that is not part of this RP's
            /// slot set at all: pre-existing stations, satellites, rovers,
            /// <c>SpaceObject</c> asteroids/comets and planted flags. Preserved
            /// per design doc §6.4 step 4 ("Else: leave alone").
            /// </summary>
            public int VesselsPreserved;

            public int VesselsRemoved;
            public int SelectedActiveIndex;
            public int ThrottleResets;
            public int SidecarEpochsRefreshed;
            public int SidecarEpochRefreshSkipped;
        }

        /// <summary>
        /// Rewrites the temp re-fly save so KSP only instantiates the selected
        /// slot's real vessel. This is intentionally scoped to the root-level
        /// temp copy: persistent.sfs and the RP source save are not modified.
        /// </summary>
        internal static ReFlySaveScrubResult ScrubQuicksaveToSelectedSlotForReFly(
            string sfsPath, RewindPoint rp, int selectedSlotIndex)
        {
            var result = new ReFlySaveScrubResult { SelectedActiveIndex = -1 };
            if (string.IsNullOrEmpty(sfsPath) || rp == null || selectedSlotIndex < 0)
                return result;

            ConfigNode root = ConfigNode.Load(sfsPath);
            if (root == null)
            {
                ParsekLog.Warn(InvokeTag,
                    $"Re-Fly save scrub skipped: ConfigNode.Load returned null path='{sfsPath}'");
                return result;
            }

            ConfigNode gameNode = root.HasNode("GAME") ? root.GetNode("GAME") : root;
            ConfigNode flightState = gameNode?.GetNode("FLIGHTSTATE");
            if (flightState == null)
            {
                ParsekLog.Warn(InvokeTag,
                    $"Re-Fly save scrub skipped: no FLIGHTSTATE path='{sfsPath}'");
                return result;
            }

            // ONE classification source, shared with the in-game world-preservation
            // guard (see BuildSlotPidSets): the sets are built once here and the
            // per-vessel test below is ClassifySlotAffinity.
            ReFlySlotPidSets slotPids = BuildSlotPidSets(rp, selectedSlotIndex);
            if (slotPids.SelectedSlotIsUnmapped)
            {
                ParsekLog.Warn(InvokeTag,
                    $"Re-Fly save scrub skipped: selected slot has no pid mapping " +
                    $"rp={rp.RewindPointId} slot={selectedSlotIndex}");
                return result;
            }

            // Removal is scoped to this RP's OTHER slots. A vessel that is in no
            // slot map at all is unrelated to the re-fly (pre-existing station,
            // satellite, rover, SpaceObject asteroid/comet, planted flag) and is
            // left alone, per design doc §6.4 step 4. PostLoadStripper.Strip is
            // the authoritative matcher after load and reports these as
            // LeftAlone; the narrow #587 debris kill
            // (StripPreExistingDebrisForInPlaceContinuation) still runs and is
            // the mechanism that removes prior-career debris which would trip
            // stock patched conics. Widening this predicate must NOT be
            // "re-narrowed" here to fix a phantom-encounter regression - that
            // belongs in the #587 name-matched pass.

            ConfigNode[] vesselNodes = flightState.GetNodes("VESSEL");
            result.VesselCountBefore = vesselNodes.Length;
            var remove = new List<ConfigNode>();
            var preservedNames = new List<string>();
            int keptIndex = 0;
            for (int i = 0; i < vesselNodes.Length; i++)
            {
                ConfigNode vessel = vesselNodes[i];
                ReFlySlotAffinity affinity = ClassifySlotAffinity(vessel, slotPids);

                // No ghost-node carve-out is needed here, and adding one is a
                // trap: ParsekScenario.OnSave strips ghost map ProtoVessels out
                // of HighLogic.CurrentGame.flightState (ParsekScenario.cs:1091-1097
                // -> GhostMapPresence.StripFromSave) on EVERY save, and KSP writes
                // the SCENARIO nodes before FLIGHTSTATE, so ghosts never reach a
                // quicksave in the first place. A name-prefix guard here would
                // only delete a player craft actually named "Ghost: ..." - and
                // because it would run before the selected-slot test, if that
                // craft WERE the re-fly target the whole Re-Fly would refuse.
                if (affinity == ReFlySlotAffinity.OtherSlot)
                {
                    remove.Add(vessel);
                    continue;
                }

                if (affinity == ReFlySlotAffinity.SelectedSlot)
                {
                    // Throttle-zeroing is scoped to the re-flown slot only: an
                    // unrelated vessel's control state is its own business and
                    // must survive the scrub untouched.
                    result.ThrottleResets += ForceReFlyVesselThrottleClosed(vessel);
                    if (result.SelectedActiveIndex < 0)
                        result.SelectedActiveIndex = keptIndex;
                }
                else
                {
                    result.VesselsPreserved++;
                    if (preservedNames.Count < MaxPreservedNamesLogged)
                    {
                        preservedNames.Add(vessel.GetValue("name")
                            ?? $"pid={ParseUInt(vessel.GetValue("persistentId"))}");
                    }
                }

                keptIndex++;
                result.VesselsKept++;
            }

            if (result.VesselsPreserved > 0)
            {
                string overflow = result.VesselsPreserved > preservedNames.Count
                    ? $" (+{result.VesselsPreserved - preservedNames.Count} more)"
                    : "";
                ParsekLog.Verbose(InvokeTag,
                    $"Re-Fly save scrub preserved {result.VesselsPreserved} unrelated vessel(s) " +
                    $"[{string.Join(", ", preservedNames.ToArray())}]{overflow}");
            }

            // Guard on the SELECTED slot specifically, not on "anything kept":
            // now that unrelated vessels survive, VesselsKept > 0 no longer
            // implies the re-fly target was found, and repointing activeVessel
            // at an unrelated survivor would be worse than not scrubbing.
            if (result.SelectedActiveIndex < 0)
            {
                ParsekLog.Warn(InvokeTag,
                    $"Re-Fly save scrub skipped: selected slot vessel not found " +
                    $"rp={rp.RewindPointId} slot={selectedSlotIndex} vessels={vesselNodes.Length}");
                return result;
            }

            for (int i = 0; i < remove.Count; i++)
                flightState.RemoveNode(remove[i]);

            result.VesselsRemoved = remove.Count;
            SetOrAddValue(flightState, "activeVessel",
                Math.Max(0, result.SelectedActiveIndex).ToString(CultureInfo.InvariantCulture));
            int sidecarSkipped;
            result.SidecarEpochsRefreshed =
                RefreshRecordingSidecarEpochsForTempSave(root, sfsPath, out sidecarSkipped);
            result.SidecarEpochRefreshSkipped = sidecarSkipped;
            root.Save(sfsPath);
            result.Applied = true;

            ParsekLog.Info(InvokeTag,
                $"Re-Fly save scrub applied: rp={rp.RewindPointId} slot={selectedSlotIndex} " +
                $"vesselsBefore={result.VesselCountBefore} kept={result.VesselsKept} " +
                $"preserved={result.VesselsPreserved} " +
                $"removed={result.VesselsRemoved} activeVessel={result.SelectedActiveIndex} " +
                $"throttleResets={result.ThrottleResets} " +
                $"sidecarEpochsRefreshed={result.SidecarEpochsRefreshed} " +
                $"sidecarEpochRefreshSkipped={result.SidecarEpochRefreshSkipped} " +
                $"path='{sfsPath}'");
            return result;
        }

        internal static int RefreshRecordingSidecarEpochsForTempSave(
            ConfigNode root, string sfsPath, out int skipped)
        {
            skipped = 0;
            if (root == null || string.IsNullOrEmpty(sfsPath))
                return 0;

            string saveRoot = Path.GetDirectoryName(sfsPath);
            if (string.IsNullOrEmpty(saveRoot))
                return 0;

            int refreshed = 0;
            RefreshRecordingSidecarEpochsInNode(root, saveRoot, ref refreshed, ref skipped);
            return refreshed;
        }

        private static void RefreshRecordingSidecarEpochsInNode(
            ConfigNode node, string saveRoot, ref int refreshed, ref int skipped)
        {
            if (node == null) return;

            if (string.Equals(node.name, "RECORDING", StringComparison.Ordinal))
            {
                RefreshSingleRecordingSidecarEpoch(node, saveRoot, ref refreshed, ref skipped);
            }

            foreach (ConfigNode child in node.GetNodes())
            {
                RefreshRecordingSidecarEpochsInNode(child, saveRoot, ref refreshed, ref skipped);
            }
        }

        private static void RefreshSingleRecordingSidecarEpoch(
            ConfigNode recordingNode, string saveRoot, ref int refreshed, ref int skipped)
        {
            string recordingId = recordingNode.GetValue("recordingId");
            if (!RecordingPaths.ValidateRecordingId(recordingId))
            {
                skipped++;
                return;
            }

            string precPath = Path.Combine(saveRoot, RecordingPaths.BuildTrajectoryRelativePath(recordingId));
            TrajectorySidecarProbe probe;
            if (!RecordingSidecarStore.TryProbeTrajectorySidecar(precPath, out probe) || !probe.Supported)
            {
                skipped++;
                return;
            }

            if (!string.IsNullOrEmpty(probe.RecordingId)
                && !string.Equals(probe.RecordingId, recordingId, StringComparison.Ordinal))
            {
                skipped++;
                ParsekLog.Warn(InvokeTag,
                    $"Re-Fly sidecar epoch refresh skipped: recording id mismatch node={recordingId} " +
                    $"sidecar={probe.RecordingId} path='{precPath}'");
                return;
            }

            string oldValue = recordingNode.GetValue("sidecarEpoch");
            int currentEpoch;
            bool hasCurrentEpoch = int.TryParse(oldValue, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out currentEpoch);
            if (hasCurrentEpoch && probe.SidecarEpoch < currentEpoch)
            {
                skipped++;
                ParsekLog.Warn(InvokeTag,
                    $"Re-Fly sidecar epoch refresh skipped: sidecar older than temp save " +
                    $"rec={recordingId} sfs={currentEpoch} sidecar={probe.SidecarEpoch} path='{precPath}'");
                return;
            }

            int targetEpoch = hasCurrentEpoch
                ? Math.Max(currentEpoch, probe.SidecarEpoch)
                : probe.SidecarEpoch;
            string newValue = targetEpoch.ToString(CultureInfo.InvariantCulture);

            // The node now points at the current heads by epoch; its content hashes name the
            // rewind point's older bytes. Strip them so any load of this temp save reads the
            // heads under the epoch rule instead of resolving the older versions
            // (design-load-restores-the-save.md 4.5, VERIFY V5).
            int hashesStripped = StripSidecarHashes(recordingNode);
            if (hashesStripped > 0)
            {
                ParsekLog.Verbose(InvokeTag,
                    $"Re-Fly temp save sidecar hashes stripped: rec={recordingId} count={hashesStripped}");
            }
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                SetOrAddValue(recordingNode, "sidecarEpoch", newValue);
                refreshed++;
                ParsekLog.Verbose(InvokeTag,
                    $"Re-Fly sidecar epoch refreshed: rec={recordingId} old={oldValue ?? "<missing>"} " +
                    $"new={newValue} path='{precPath}'");
            }
        }

        internal static int StripSidecarHashes(ConfigNode recordingNode)
        {
            if (recordingNode == null) return 0;
            int stripped = 0;
            foreach (string key in new[] { "precHash", "vesselHash", "ghostHash" })
            {
                if (!recordingNode.HasValue(key)) continue;
                recordingNode.RemoveValues(key);
                stripped++;
            }
            return stripped;
        }

        /// <summary>
        /// Which of a rewind point's slots a save <c>VESSEL</c> node belongs to.
        /// <see cref="Unrelated"/> is the preserved population (design §6.4 step 4:
        /// pre-existing station / satellite / rover / <c>SpaceObject</c> / flag).
        /// </summary>
        internal enum ReFlySlotAffinity
        {
            Unrelated = 0,
            SelectedSlot = 1,
            OtherSlot = 2,
        }

        /// <summary>
        /// The four pid sets the selected-slot classification needs, built ONCE per
        /// scrub pass so the per-vessel test stays O(1).
        /// </summary>
        internal struct ReFlySlotPidSets
        {
            public HashSet<uint> SelectedVesselPids;
            public HashSet<uint> SelectedRootPartPids;
            public HashSet<uint> OtherSlotVesselPids;
            public HashSet<uint> OtherSlotRootPartPids;

            /// <summary>
            /// True when this RP maps NO pid to the selected slot on either key, i.e.
            /// nothing can be matched and the scrub must decline rather than guess.
            /// </summary>
            public bool SelectedSlotIsUnmapped =>
                (SelectedVesselPids == null || SelectedVesselPids.Count == 0)
                && (SelectedRootPartPids == null || SelectedRootPartPids.Count == 0);
        }

        /// <summary>
        /// Builds the <see cref="ReFlySlotPidSets"/> for one selected slot of
        /// <paramref name="rp"/>.
        /// <para>
        /// SINGLE SOURCE for "which slot does this vessel belong to". The pre-load
        /// scrub (<see cref="ScrubQuicksaveToSelectedSlotForReFly"/>) and the in-game
        /// <c>ReFlyWorldPreservation</c> guard both classify through this pair, so a
        /// guard asserting the preserved world cannot drift from the predicate that
        /// produced it.
        /// </para>
        /// </summary>
        internal static ReFlySlotPidSets BuildSlotPidSets(RewindPoint rp, int selectedSlotIndex)
        {
            return new ReFlySlotPidSets
            {
                SelectedVesselPids = BuildSelectedSlotPidSet(rp?.PidSlotMap, selectedSlotIndex),
                SelectedRootPartPids = BuildSelectedSlotPidSet(rp?.RootPartPidMap, selectedSlotIndex),
                OtherSlotVesselPids = BuildNonSelectedSlotPidSet(rp?.PidSlotMap, selectedSlotIndex),
                OtherSlotRootPartPids = BuildNonSelectedSlotPidSet(rp?.RootPartPidMap, selectedSlotIndex),
            };
        }

        /// <summary>
        /// Classifies one save <c>VESSEL</c> node against <paramref name="sets"/>.
        /// Selected wins over other-slot (a pid in both maps is the re-fly target,
        /// never a sibling to remove), and a vessel matching neither map is
        /// <see cref="ReFlySlotAffinity.Unrelated"/> - the preserved population.
        /// Pure: reads only <c>persistentId</c> and the root <c>PART</c>'s
        /// <c>persistentId</c>, never <c>type</c> or <c>name</c>.
        /// </summary>
        internal static ReFlySlotAffinity ClassifySlotAffinity(
            ConfigNode vessel, ReFlySlotPidSets sets)
        {
            if (vessel == null) return ReFlySlotAffinity.Unrelated;

            uint vesselPid = ParseUInt(vessel.GetValue("persistentId"));
            uint rootPartPid = GetRootPartPersistentId(vessel);

            bool isSelectedSlot =
                (vesselPid != 0u && sets.SelectedVesselPids != null
                    && sets.SelectedVesselPids.Contains(vesselPid))
                || (rootPartPid != 0u && sets.SelectedRootPartPids != null
                    && sets.SelectedRootPartPids.Contains(rootPartPid));
            if (isSelectedSlot) return ReFlySlotAffinity.SelectedSlot;

            bool isOtherSlot =
                (vesselPid != 0u && sets.OtherSlotVesselPids != null
                    && sets.OtherSlotVesselPids.Contains(vesselPid))
                || (rootPartPid != 0u && sets.OtherSlotRootPartPids != null
                    && sets.OtherSlotRootPartPids.Contains(rootPartPid));
            return isOtherSlot ? ReFlySlotAffinity.OtherSlot : ReFlySlotAffinity.Unrelated;
        }

        private static HashSet<uint> BuildSelectedSlotPidSet(
            Dictionary<uint, int> map, int selectedSlotIndex)
        {
            var result = new HashSet<uint>();
            if (map == null) return result;
            foreach (var kv in map)
            {
                if (kv.Key != 0u && kv.Value == selectedSlotIndex)
                    result.Add(kv.Key);
            }
            return result;
        }

        /// <summary>
        /// Pids this RP maps to a slot OTHER than the selected one. Together with
        /// serialized ghost nodes these are the only vessels the temp-save scrub
        /// removes; anything absent from the slot maps entirely is unrelated to
        /// the re-fly and is preserved.
        /// </summary>
        private static HashSet<uint> BuildNonSelectedSlotPidSet(
            Dictionary<uint, int> map, int selectedSlotIndex)
        {
            var result = new HashSet<uint>();
            if (map == null) return result;
            foreach (var kv in map)
            {
                if (kv.Key != 0u && kv.Value != selectedSlotIndex)
                    result.Add(kv.Key);
            }
            return result;
        }

        internal static bool TryResolveSelectedSlotRootPartPersistentId(
            RewindPoint rp, int selectedSlotIndex, out uint rootPartPersistentId)
        {
            rootPartPersistentId = 0u;
            Dictionary<uint, int> map = rp?.RootPartPidMap;
            if (map == null) return false;
            foreach (var kv in map)
            {
                if (kv.Key == 0u || kv.Value != selectedSlotIndex)
                    continue;

                rootPartPersistentId = kv.Key;
                return true;
            }
            return false;
        }

        private static uint GetRootPartPersistentId(ConfigNode vessel)
        {
            if (vessel == null) return 0u;
            int rootIndex;
            if (!int.TryParse(vessel.GetValue("root"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out rootIndex))
            {
                rootIndex = 0;
            }

            ConfigNode[] parts = vessel.GetNodes("PART");
            if (parts == null || parts.Length == 0) return 0u;
            if (rootIndex < 0 || rootIndex >= parts.Length) rootIndex = 0;
            return ParseUInt(parts[rootIndex].GetValue("persistentId"));
        }

        private static uint ParseUInt(string value)
        {
            uint parsed;
            return uint.TryParse(value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : 0u;
        }

        private static int ForceReFlyVesselThrottleClosed(ConfigNode vessel)
        {
            if (vessel == null) return 0;

            int resetCount = 0;
            // The temp save may contain uncontrolled debris with no CTRLSTATE;
            // adding only throttle fields is harmless and prevents inherited player input.
            ConfigNode ctrlState = vessel.GetNode("CTRLSTATE") ?? vessel.AddNode("CTRLSTATE");
            if (SetOrAddValueAndReportChange(ctrlState, "mainThrottle", "0"))
                resetCount++;
            if (SetOrAddValueAndReportChange(ctrlState, "wheelThrottle", "0"))
                resetCount++;

            // Engine module fields are normalized only when present. Module-less
            // debris has no engine throttle state to close.
            ConfigNode[] parts = vessel.GetNodes("PART");
            for (int partIndex = 0; partIndex < parts.Length; partIndex++)
            {
                ConfigNode[] modules = parts[partIndex].GetNodes("MODULE");
                for (int moduleIndex = 0; moduleIndex < modules.Length; moduleIndex++)
                {
                    ConfigNode module = modules[moduleIndex];
                    string moduleName = module.GetValue("name");
                    if (!IsEngineModuleName(moduleName))
                        continue;

                    if (SetExistingValueAndReportChange(module, "currentThrottle", "0"))
                        resetCount++;
                    if (SetExistingValueAndReportChange(module, "independentThrottlePercentage", "0"))
                        resetCount++;
                }
            }

            return resetCount;
        }

        private static bool IsEngineModuleName(string moduleName)
        {
            return string.Equals(moduleName, "ModuleEngines", StringComparison.Ordinal)
                || string.Equals(moduleName, "ModuleEnginesFX", StringComparison.Ordinal);
        }

        private static bool SetOrAddValueAndReportChange(ConfigNode node, string name, string value)
        {
            if (node == null || string.IsNullOrEmpty(name)) return false;
            string oldValue = node.GetValue(name);
            SetOrAddValue(node, name, value);
            return !string.Equals(oldValue, value, StringComparison.Ordinal);
        }

        private static bool SetExistingValueAndReportChange(ConfigNode node, string name, string value)
        {
            if (node == null || string.IsNullOrEmpty(name) || !node.HasValue(name)) return false;
            string oldValue = node.GetValue(name);
            node.SetValue(name, value);
            return !string.Equals(oldValue, value, StringComparison.Ordinal);
        }

        private static void SetOrAddValue(ConfigNode node, string name, string value)
        {
            if (node == null || string.IsNullOrEmpty(name)) return;
            if (node.HasValue(name))
                node.SetValue(name, value);
            else
                node.AddValue(name, value);
        }
    }
}
