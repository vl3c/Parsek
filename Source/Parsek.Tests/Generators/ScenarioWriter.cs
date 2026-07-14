using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Parsek.Tests.Generators
{
    public class ScenarioWriter
    {
        private readonly List<RecordingBuilder> v3Builders = new List<RecordingBuilder>();
        private readonly List<ConfigNode> trees = new List<ConfigNode>();
        private readonly List<(string original, string replacement)> crewReplacements
            = new List<(string, string)>();
        private readonly List<Milestone> milestones = new List<Milestone>();
        private readonly List<GameStateEvent> gameStateEvents = new List<GameStateEvent>();
        private readonly List<ConfigNode> rawMilestoneStates = new List<ConfigNode>();
        private readonly List<(string child, string parent)> groupHierarchyEntries
            = new List<(string, string)>();
        private readonly List<RewindPoint> rewindPoints = new List<RewindPoint>();
        private uint milestoneEpoch;
        private bool useV3Format;
        // One-shot guard: InjectRewindB9 calls InjectIntoSaveFile once per target
        // file (persistent.sfs AND the run target), but the RP quicksave sidecar is a
        // single shared artifact under Parsek/RewindPoints/ that does not depend on
        // WHICH save file was just injected. Writing it on the first InjectIntoSaveFile
        // and skipping the rest avoids re-emitting the identical sidecar per target.
        private bool rewindPointSidecarsWritten;

        /// <summary>
        /// Wraps a single RecordingBuilder into a RECORDING_TREE node and adds it
        /// to the scenario. Each recording becomes a single-recording tree.
        /// Sidecar files (.prec, _vessel.craft, _ghost.craft) are also registered
        /// for writing when WithV3Format is active.
        /// </summary>
        public ScenarioWriter AddRecordingAsTree(RecordingBuilder builder)
        {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));

            return AddRecordingsAsTree(new[] { builder });
        }

        /// <summary>
        /// Wraps multiple related RecordingBuilders into a single RECORDING_TREE node.
        /// This is used for linked chain fixtures so ParentRecordingId references stay
        /// within the same tree when injected into synthetic saves.
        /// </summary>
        public ScenarioWriter AddRecordingsAsTree(IEnumerable<RecordingBuilder> builders)
        {
            if (builders == null)
                throw new ArgumentNullException(nameof(builders));

            var builderList = new List<RecordingBuilder>();
            foreach (var builder in builders)
            {
                if (builder == null)
                    throw new ArgumentException("Recording tree builders cannot contain null entries.", nameof(builders));
                builderList.Add(builder);
            }

            if (builderList.Count == 0)
                throw new ArgumentException("Recording tree requires at least one builder.", nameof(builders));

            var recordings = new List<Recording>(builderList.Count);
            Recording root = null;

            for (int i = 0; i < builderList.Count; i++)
            {
                var rec = BuildRecording(builderList[i]);
                recordings.Add(rec);
                if (root == null && string.IsNullOrEmpty(rec.ParentRecordingId))
                    root = rec;
            }

            if (root == null)
                root = recordings[0];

            var tree = new RecordingTree
            {
                Id = "tree-" + root.RecordingId,
                TreeName = root.VesselName,
                RootRecordingId = root.RecordingId,
                ActiveRecordingId = null
            };

            for (int i = 0; i < recordings.Count; i++)
            {
                recordings[i].TreeId = tree.Id;
                tree.Recordings[recordings[i].RecordingId] = recordings[i];
            }

            AddSerializedTree(tree);
            RegisterV3Builders(builderList);
            return this;
        }

        /// <summary>
        /// Adds a pre-built RECORDING_TREE ConfigNode to be emitted in the scenario.
        /// The node should be built via RecordingTree.Save().
        /// </summary>
        public ScenarioWriter AddTree(ConfigNode treeNode)
        {
            trees.Add(treeNode);
            return this;
        }

        public ScenarioWriter WithV3Format(bool v3 = true)
        {
            useV3Format = v3;
            return this;
        }

        public ScenarioWriter AddCrewReplacement(string original, string replacement)
        {
            crewReplacements.Add((original, replacement));
            return this;
        }

        public ScenarioWriter WithMilestoneEpoch(uint epoch)
        {
            milestoneEpoch = epoch;
            return this;
        }

        public ScenarioWriter AddRawMilestoneState(ConfigNode stateNode)
        {
            rawMilestoneStates.Add(stateNode);
            return this;
        }

        public ScenarioWriter AddGroupHierarchyEntry(string child, string parent)
        {
            if (string.IsNullOrEmpty(child) || string.IsNullOrEmpty(parent))
                return this;
            groupHierarchyEntries.Add((child, parent));
            return this;
        }

        /// <summary>
        /// Registers a <see cref="RewindPoint"/> to be emitted in the scenario's
        /// <c>REWIND_POINTS</c> block (mirrors <see cref="ParsekScenario"/>'s
        /// <c>SaveStagingList("REWIND_POINTS", ...)</c> shape: a parent
        /// <c>REWIND_POINTS</c> node with one <c>POINT</c> child per RP). Used by
        /// the rewindable-tree fixture (B9) so an injected save can carry a
        /// re-fly-able RewindPoint alongside its committed tree. The RP's quicksave
        /// sidecar is written by <see cref="WriteRewindPointSaveFiles"/>.
        /// </summary>
        public ScenarioWriter AddRewindPoint(RewindPoint rp)
        {
            if (rp == null)
                throw new ArgumentNullException(nameof(rp));
            rewindPoints.Add(rp);
            return this;
        }

        /// <summary>
        /// Derives the synthetic <see cref="Recording.VesselPersistentId"/> a
        /// recording id maps to under this writer's serialization (FNV-1a, the same
        /// hash <see cref="BuildRecording"/> applies). Exposed so a rewindable-tree
        /// fixture can populate a <see cref="RewindPoint.PidSlotMap"/> that matches
        /// the pids the injected recordings actually carry.
        /// </summary>
        public static uint DeriveVesselPersistentId(string recordingId)
        {
            return StableHashToUint(recordingId ?? "");
        }

        /// <summary>
        /// Derives the root-part <c>persistentId</c> a recording id's slot carries in
        /// the RP quicksave sidecar. Distinct from <see cref="DeriveVesselPersistentId"/>
        /// (the ":root" salt) so the vessel-level and root-part pids never collide,
        /// letting the fixture populate a <see cref="RewindPoint.RootPartPidMap"/> that
        /// matches the sidecar's cloned root PART nodes and exercises the strip's
        /// root-part fallback path (<c>PostLoadStripper</c> / the pre-load
        /// <c>ScrubQuicksaveToSelectedSlotForReFly</c>).
        /// </summary>
        public static uint DeriveRootPartPersistentId(string recordingId)
        {
            return StableHashToUint((recordingId ?? "") + ":root");
        }

        internal ScenarioWriter AddMilestone(Milestone milestone)
        {
            milestones.Add(milestone);
            return this;
        }

        internal ScenarioWriter AddGameStateEvent(GameStateEvent e)
        {
            gameStateEvents.Add(e);
            return this;
        }

        public ConfigNode BuildScenarioNode()
        {
            var node = new ConfigNode("SCENARIO");
            node.AddValue("name", "ParsekScenario");
            node.AddValue("scene", "5, 6, 7, 8");

            foreach (var tree in trees)
                node.AddNode("RECORDING_TREE", tree);

            if (crewReplacements.Count > 0)
            {
                var crNode = node.AddNode("CREW_REPLACEMENTS");
                foreach (var (original, replacement) in crewReplacements)
                {
                    var entry = crNode.AddNode("ENTRY");
                    entry.AddValue("original", original);
                    entry.AddValue("replacement", replacement);
                }
            }

            if (milestones.Count > 0)
            {
                var ic = CultureInfo.InvariantCulture;
                node.AddValue("milestoneEpoch", milestoneEpoch.ToString(ic));

                foreach (var m in milestones)
                {
                    var stateNode = node.AddNode("MILESTONE_STATE");
                    stateNode.AddValue("id", m.MilestoneId ?? "");
                    stateNode.AddValue("lastReplayedIdx",
                        m.LastReplayedEventIndex.ToString(ic));
                }
            }

            foreach (var rawMs in rawMilestoneStates)
                node.AddNode("MILESTONE_STATE", rawMs);

            if (groupHierarchyEntries.Count > 0)
            {
                var hierarchyNode = node.AddNode("GROUP_HIERARCHY");
                foreach (var (child, parent) in groupHierarchyEntries)
                {
                    var entry = hierarchyNode.AddNode("ENTRY");
                    entry.AddValue("child", child);
                    entry.AddValue("parent", parent);
                }
            }

            // REWIND_POINTS block: one POINT child per RewindPoint. Matches the
            // parent-node + POINT-child shape ParsekScenario.SaveRewindStagingState
            // writes (via SaveStagingList), so ParsekScenario.LoadRewindStagingState
            // deserializes an injected fixture RP identically to a live one.
            if (rewindPoints.Count > 0)
            {
                var rpParent = node.AddNode("REWIND_POINTS");
                foreach (var rp in rewindPoints)
                    rp?.SaveInto(rpParent);
            }

            return node;
        }

        public string SerializeConfigNode(ConfigNode node, string nodeName, int baseIndent = 1)
        {
            var sb = new StringBuilder();
            WriteNode(sb, node, nodeName, baseIndent);
            return sb.ToString();
        }

        private void WriteNode(StringBuilder sb, ConfigNode node, string nodeName, int indent)
        {
            string tabs = new string('\t', indent);
            sb.AppendLine($"{tabs}{nodeName}");
            sb.AppendLine($"{tabs}{{");

            string innerTabs = new string('\t', indent + 1);

            // Values first
            foreach (ConfigNode.Value val in node.values)
                sb.AppendLine($"{innerTabs}{val.name} = {val.value}");

            // Then child nodes
            foreach (ConfigNode child in node.nodes)
                WriteNode(sb, child, child.name, indent + 1);

            sb.AppendLine($"{tabs}}}");
        }

        public string InjectIntoSave(string saveContent)
        {
            var scenarioNode = BuildScenarioNode();
            string serialized = SerializeConfigNode(scenarioNode, "SCENARIO", 1);

            // Remove existing ParsekScenario block if present
            saveContent = RemoveExistingScenario(saveContent);

            // Find FLIGHTSTATE and insert before it
            int flightstateIdx = saveContent.IndexOf("\tFLIGHTSTATE", StringComparison.Ordinal);
            if (flightstateIdx < 0)
                throw new InvalidOperationException("Could not find FLIGHTSTATE in save file");

            return saveContent.Insert(flightstateIdx, serialized);
        }

        public void InjectIntoSaveFile(string inputPath, string outputPath)
        {
            string content = File.ReadAllText(inputPath);
            string modified = InjectIntoSave(content);
            File.WriteAllText(outputPath, modified);

            // If v3 format, write sidecar files alongside the save
            if (useV3Format)
            {
                string saveDir = Path.GetDirectoryName(outputPath);
                WriteSidecarFiles(saveDir);
            }

            // Write milestone and event sidecar files
            if (milestones.Count > 0 || gameStateEvents.Count > 0)
            {
                string saveDir = Path.GetDirectoryName(outputPath);
                WriteGameStateFiles(saveDir);
            }

            // Write rewind save files for v3 recordings that have rewind saves
            if (useV3Format)
            {
                string saveDir = Path.GetDirectoryName(outputPath);
                WriteRewindSaveFiles(saveDir, inputPath);
            }

            // Write RewindPoint quicksave sidecars for any registered RPs, ONCE per
            // writer (see rewindPointSidecarsWritten). The un-injected input save is
            // the donor: WriteRewindPointSaveFiles clones its command vessel per
            // controllable slot and stamps the slot's mapped pids, so the sidecar
            // carries the vessels the RP's PidSlotMap references (the mechanics under
            // test are strip/restore/marker/merge; live-load fidelity is operator-
            // verified).
            if (rewindPoints.Count > 0 && !rewindPointSidecarsWritten)
            {
                string saveDir = Path.GetDirectoryName(outputPath);
                WriteRewindPointSaveFiles(saveDir, inputPath);
                rewindPointSidecarsWritten = true;
            }
        }

        /// <summary>
        /// Number of v3 recording builders registered with this writer —
        /// used by InjectAllRecordings to compute the exact expected sidecar
        /// file count without maintaining a brittle magic number.
        /// </summary>
        public int V3BuilderCount => v3Builders.Count;

        /// <summary>
        /// Deletes the entire Parsek/Recordings/ sidecar directory under
        /// saveDir so a subsequent WriteSidecarFiles call starts from a clean
        /// slate. Without this purge, recordings from a previous InjectAllRecordings
        /// run with different (GUID-keyed) IDs survive on disk as orphans, and
        /// KSP's load-time orphan sweep in RecordingStore later deletes them
        /// along with any of the current run's sidecars that happen to share
        /// the victim set. Call this before WriteSidecarFiles / InjectIntoSaveFile
        /// when you want the final on-disk state to match the just-written scenario.
        /// </summary>
        public void PurgeRecordingSidecars(string saveDir)
        {
            if (string.IsNullOrEmpty(saveDir))
                return;

            string recordingsDir = Path.Combine(saveDir, "Parsek", "Recordings");
            if (Directory.Exists(recordingsDir))
                Directory.Delete(recordingsDir, recursive: true);
        }

        /// <summary>
        /// Guarded preflight used by InjectAllRecordings. Refuses the inject when
        /// a running KSP instance still has KSP.log open, which would race the
        /// game and wipe or rewrite its current save-side data.
        /// </summary>
        public bool TryPurgeRecordingSidecarsForInject(
            string saveDir,
            string kspLogPath,
            out string refusalMessage)
        {
            refusalMessage = null;
            string lockedLogPath;
            if (IsExclusiveReadWriteProbeBlocked(kspLogPath, out lockedLogPath))
            {
                string recordingsDir = string.IsNullOrEmpty(saveDir)
                    ? "(purge skipped)"
                    : Path.Combine(saveDir, "Parsek", "Recordings");
                refusalMessage =
                    $"InjectAllRecordings refused to purge '{recordingsDir}' because KSP.log '{lockedLogPath}' is locked by another process. Close KSP or point KSPDIR at a different install/save before rerunning.";
                ParsekLog.Error("SyntheticInjector", refusalMessage);
                return false;
            }

            if (string.IsNullOrEmpty(saveDir))
                return true;

            PurgeRecordingSidecars(saveDir);
            return true;
        }

        internal static bool IsExclusiveReadWriteProbeBlocked(string path, out string normalizedPath)
        {
            normalizedPath = null;
            if (string.IsNullOrWhiteSpace(path))
                return false;

            normalizedPath = Path.GetFullPath(path);
            if (!File.Exists(normalizedPath))
                return false;

            try
            {
                using (File.Open(normalizedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        /// <summary>
        /// Writes recording sidecar files for all v3 recordings to the
        /// Parsek/Recordings/ subdirectory relative to the given save directory.
        /// Uses RecordingStore's test-facing save path so generated corpora
        /// stay aligned with live sidecar behavior.
        /// </summary>
        public void WriteSidecarFiles(string saveDir)
        {
            string recordingsDir = Path.Combine(saveDir, "Parsek", "Recordings");
            if (!Directory.Exists(recordingsDir))
                Directory.CreateDirectory(recordingsDir);

            foreach (var builder in v3Builders)
            {
                string id = builder.GetRecordingId();

                // Write .prec trajectory file
                var sourceTrajNode = builder.BuildTrajectoryNode();
                var recording = new Recording
                {
                    RecordingId = id,
                    RecordingFormatVersion = builder.GetFormatVersion(),
                    RecordingSchemaGeneration = builder.GetSchemaGeneration(),
                    VesselName = builder.GetVesselName(),
                    VesselSnapshot = builder.GetVesselSnapshot()?.CreateCopy(),
                    GhostVisualSnapshot = builder.GetGhostVisualSnapshot()?.CreateCopy(),
                    FilesDirty = true,
                };
                recording.GhostSnapshotMode = RecordingStore.DetermineGhostSnapshotMode(recording);
                RecordingStore.DeserializeTrajectoryFrom(sourceTrajNode, recording);
                string precPath = Path.Combine(recordingsDir, $"{id}.prec");
                string vesselPath = Path.Combine(recordingsDir, $"{id}_vessel.craft");
                string ghostPath = Path.Combine(recordingsDir, $"{id}_ghost.craft");
                if (!RecordingStore.SaveRecordingFilesToPathsForTesting(
                        recording, precPath, vesselPath, ghostPath, incrementEpoch: false))
                    throw new InvalidOperationException($"Failed to write sidecar files for {id}");
            }
        }

        /// <summary>
        /// Writes milestones.pgsm and events.pgse sidecar files to the
        /// Parsek/GameState/ subdirectory relative to the save directory.
        /// </summary>
        public void WriteGameStateFiles(string saveDir)
        {
            string gameStateDir = Path.Combine(saveDir, "Parsek", "GameState");
            if (!Directory.Exists(gameStateDir))
                Directory.CreateDirectory(gameStateDir);

            if (milestones.Count > 0)
            {
                var rootNode = new ConfigNode("PARSEK_MILESTONES");
                rootNode.AddValue("version", "1");
                foreach (var m in milestones)
                {
                    ConfigNode milestoneNode = rootNode.AddNode("MILESTONE");
                    m.SerializeInto(milestoneNode);
                }
                rootNode.Save(Path.Combine(gameStateDir, "milestones.pgsm"));
            }

            if (gameStateEvents.Count > 0)
            {
                var rootNode = new ConfigNode("PARSEK_GAME_STATE");
                rootNode.AddValue("version", "1");
                foreach (var e in gameStateEvents)
                {
                    ConfigNode eventNode = rootNode.AddNode("GAME_STATE_EVENT");
                    e.SerializeInto(eventNode);
                }
                rootNode.Save(Path.Combine(gameStateDir, "events.pgse"));
            }
        }

        /// <summary>
        /// Copies the source save as each v3 recording's rewind quicksave .sfs file
        /// in the Parsek/Saves/ subdirectory.
        /// </summary>
        public void WriteRewindSaveFiles(string saveDir, string sourceSavePath)
        {
            if (!File.Exists(sourceSavePath)) return;

            foreach (var builder in v3Builders)
            {
                string rewindName = builder.GetRewindSaveFileName();
                if (string.IsNullOrEmpty(rewindName)) continue;

                string savesDir = Path.Combine(saveDir, "Parsek", "Saves");
                if (!Directory.Exists(savesDir))
                    Directory.CreateDirectory(savesDir);

                string destPath = Path.Combine(savesDir, rewindName + ".sfs");
                File.Copy(sourceSavePath, destPath, true);
            }
        }

        /// <summary>
        /// Writes each registered <see cref="RewindPoint"/>'s quicksave sidecar to
        /// <c>saves/&lt;save&gt;/Parsek/RewindPoints/&lt;rpId&gt;.sfs</c> (the path
        /// <see cref="RewindInvoker.CanInvoke"/> probes on disk, resolved from
        /// <see cref="RewindPoint.QuicksaveFilename"/>). Skips RPs with no
        /// QuicksaveFilename.
        ///
        /// <para>
        /// The sidecar is NOT a bare copy of <paramref name="sourceSavePath"/>: a
        /// re-fly's pre-load selected-slot scrub
        /// (<c>RewindInvoker.ScrubQuicksaveToSelectedSlotForReFly</c>) and post-load
        /// <c>PostLoadStripper</c> both KEEP only vessels whose <c>persistentId</c> the
        /// RP's <see cref="RewindPoint.PidSlotMap"/> / <see cref="RewindPoint.RootPartPidMap"/>
        /// references, and STRIP the rest; a vessel-less or unmatched-pid quicksave
        /// makes the scrub keep nothing and the Activate fail. So for each RP this
        /// builds a scene state carrying exactly one controllable VESSEL per child slot,
        /// each stamped with the slot's mapped vessel + root-part pids and cloned from
        /// the host save's own command vessel when present (real parts, so the deep-parse
        /// precondition resolves them). The tree / RP / sidecar pid triangle is thus
        /// consistent. Live-load fidelity of the clones remains operator-verified.
        /// </para>
        /// </summary>
        public void WriteRewindPointSaveFiles(string saveDir, string sourceSavePath)
        {
            if (string.IsNullOrEmpty(saveDir) || !File.Exists(sourceSavePath))
                return;

            ConfigNode donorRoot = ConfigNode.Load(sourceSavePath);

            foreach (var rp in rewindPoints)
            {
                if (rp == null || string.IsNullOrEmpty(rp.QuicksaveFilename))
                    continue;

                // QuicksaveFilename is a save-relative path ("Parsek/RewindPoints/<id>.sfs").
                string destPath = Path.Combine(saveDir, rp.QuicksaveFilename);
                string destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                BuildRewindPointQuicksave(donorRoot, rp, destPath);
            }
        }

        /// <summary>
        /// Builds one RP quicksave: rewrites <paramref name="donorRoot"/>'s FLIGHTSTATE
        /// to hold exactly one controllable VESSEL per child slot in
        /// <paramref name="rp"/>, each stamped with the slot's mapped vessel- and
        /// root-part pids, then saves to <paramref name="destPath"/>. A donor command
        /// vessel (first non-asteroid VESSEL) is cloned per slot when present so its
        /// parts resolve in PartLoader; otherwise a minimal stock-pod VESSEL is
        /// synthesized. <c>activeVessel</c> is set to the focus slot's ordinal.
        /// </summary>
        internal static void BuildRewindPointQuicksave(ConfigNode donorRoot, RewindPoint rp, string destPath)
        {
            var ic = CultureInfo.InvariantCulture;

            // ConfigNode.Load on a KSP .sfs returns a wrapper whose child is GAME; be
            // robust to either shape (wrapper-with-GAME, or a bare GAME node) and
            // re-emit a single GAME wrapper on save so the sidecar is a valid save.
            ConfigNode root = (donorRoot != null && donorRoot.CountNodes > 0)
                ? donorRoot.CreateCopy()
                : new ConfigNode();
            ConfigNode game = root.GetNode("GAME") ?? root;
            ConfigNode flightState = game.GetNode("FLIGHTSTATE") ?? game.AddNode("FLIGHTSTATE");

            // Pick a donor VESSEL template BEFORE clearing (first controllable, i.e.
            // non-asteroid, vessel). When absent we synthesize a minimal stock pod.
            ConfigNode donorVessel = SelectDonorVessel(flightState);

            flightState.RemoveNodes("VESSEL");

            // Controllable slots in stable slot-index order.
            var slots = new List<ChildSlot>();
            if (rp.ChildSlots != null)
            {
                foreach (var s in rp.ChildSlots)
                    if (s != null && s.Controllable) slots.Add(s);
            }
            slots.Sort((a, b) => a.SlotIndex.CompareTo(b.SlotIndex));

            int activeOrdinal = 0;
            for (int ordinal = 0; ordinal < slots.Count; ordinal++)
            {
                ChildSlot slot = slots[ordinal];
                uint vesselPid = ReverseLookupPid(rp.PidSlotMap, slot.SlotIndex);
                uint rootPid = ReverseLookupPid(rp.RootPartPidMap, slot.SlotIndex);

                // AddNode(ConfigNode) adds by reference, so clone per slot (the
                // proven CreateCopy pattern) - two slots must be two distinct nodes.
                ConfigNode vessel = donorVessel != null
                    ? flightState.AddNode(donorVessel.CreateCopy())
                    : BuildSyntheticVessel(flightState);
                StampVesselIdentity(vessel, vesselPid, rootPid,
                    "B9 Slot " + slot.SlotIndex.ToString(ic));

                if (slot.SlotIndex == rp.FocusSlotIndex)
                    activeOrdinal = ordinal;
            }

            SetOrAddValue(flightState, "activeVessel", activeOrdinal.ToString(ic));

            // Emit a single guaranteed GAME wrapper (ConfigNode.Save writes CONTENTS,
            // not the node's own name, so wrap the GAME subtree in a fresh root).
            ConfigNode gameCopy = game.CreateCopy();
            gameCopy.name = "GAME";
            var outRoot = new ConfigNode();
            outRoot.AddNode(gameCopy);
            outRoot.Save(destPath);
        }

        // First non-asteroid VESSEL under FLIGHTSTATE, or null. Asteroids/comets
        // (type SpaceObject) are not controllable command vessels, so they are a poor
        // clone template for a re-fly slot.
        private static ConfigNode SelectDonorVessel(ConfigNode flightState)
        {
            if (flightState == null) return null;
            ConfigNode[] vessels = flightState.GetNodes("VESSEL");
            ConfigNode firstAny = null;
            for (int i = 0; i < vessels.Length; i++)
            {
                if (vessels[i] == null) continue;
                if (firstAny == null) firstAny = vessels[i];
                string type = vessels[i].GetValue("type");
                if (!string.Equals(type, "SpaceObject", StringComparison.Ordinal))
                    return vessels[i];
            }
            return firstAny;
        }

        // Minimal stock-pod VESSEL when the host save carries no command vessel to
        // clone (headless unit fixtures). The part name is a real stock part so the
        // deep-parse precondition resolves it live; live-load fidelity of this minimal
        // node is operator-verified (PENDING-OPERATOR).
        private static ConfigNode BuildSyntheticVessel(ConfigNode flightState)
        {
            ConfigNode v = flightState.AddNode("VESSEL");
            v.AddValue("type", "Ship");
            v.AddValue("sit", "LANDED");
            v.AddValue("landed", "True");
            v.AddValue("root", "0");
            ConfigNode part = v.AddNode("PART");
            part.AddValue("name", "mk1pod.v2");
            return v;
        }

        // Overwrite the clone's vessel-level identity and root-part pid so the RP's
        // PidSlotMap / RootPartPidMap resolve this vessel to its slot. A fresh guid
        // pid per clone avoids vessel-guid collisions between the per-slot clones.
        private static void StampVesselIdentity(
            ConfigNode vessel, uint vesselPid, uint rootPid, string vesselName)
        {
            if (vessel == null) return;
            var ic = CultureInfo.InvariantCulture;

            vessel.SetValue("pid", Guid.NewGuid().ToString("N"), true);
            vessel.SetValue("persistentId", vesselPid.ToString(ic), true);
            vessel.SetValue("name", vesselName, true);

            // Stamp the ROOT part's persistentId (the node the scrub/strip read via
            // vessel "root" index; default 0). Add a PART if the clone somehow has none.
            int rootIndex;
            if (!int.TryParse(vessel.GetValue("root"), NumberStyles.Integer, ic, out rootIndex))
                rootIndex = 0;
            ConfigNode[] parts = vessel.GetNodes("PART");
            ConfigNode rootPart;
            if (parts.Length == 0)
            {
                rootPart = vessel.AddNode("PART");
                rootPart.AddValue("name", "mk1pod.v2");
            }
            else
            {
                if (rootIndex < 0 || rootIndex >= parts.Length) rootIndex = 0;
                rootPart = parts[rootIndex];
            }
            rootPart.SetValue("persistentId", rootPid.ToString(ic), true);
        }

        // Reverse map lookup: the (unique) pid whose slot == slotIndex, else 0.
        private static uint ReverseLookupPid(Dictionary<uint, int> map, int slotIndex)
        {
            if (map == null) return 0u;
            foreach (var kv in map)
                if (kv.Value == slotIndex) return kv.Key;
            return 0u;
        }

        private static void SetOrAddValue(ConfigNode node, string name, string value)
        {
            if (node == null) return;
            if (node.HasValue(name)) node.SetValue(name, value);
            else node.AddValue(name, value);
        }

        private static string RemoveExistingScenario(string content)
        {
            // Brace-counting removal: find SCENARIO blocks containing
            // name = ParsekScenario, then track brace depth to find the
            // matching close brace regardless of internal formatting.
            var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var result = new List<string>(lines.Length);
            int i = 0;

            while (i < lines.Length)
            {
                string trimmed = lines[i].TrimStart('\t', ' ');

                // Look for a SCENARIO opening line at indent level 1
                if (trimmed == "SCENARIO" && i + 2 < lines.Length)
                {
                    string nextTrimmed = lines[i + 1].TrimStart('\t', ' ');
                    // Check if next line is open brace and the line after is name = ParsekScenario
                    if (nextTrimmed == "{")
                    {
                        string valueLine = lines[i + 2].TrimStart('\t', ' ');
                        if (valueLine.StartsWith("name = ParsekScenario", StringComparison.Ordinal))
                        {
                            // Found it — skip lines until matching close brace
                            i++; // skip "SCENARIO"
                            int depth = 0;
                            while (i < lines.Length)
                            {
                                string lt = lines[i].TrimStart('\t', ' ');
                                if (lt.StartsWith("{")) depth++;
                                if (lt.StartsWith("}")) depth--;
                                i++;
                                if (depth == 0) break;
                            }
                            continue;
                        }
                    }
                }

                result.Add(lines[i]);
                i++;
            }

            // Preserve original line ending style
            string sep = content.Contains("\r\n") ? "\r\n" : "\n";
            return string.Join(sep, result);
        }

        /// <summary>
        /// Generates a stable uint from a string via FNV-1a hash.
        /// Used to derive VesselPersistentId from recordingId for synthetic trees.
        /// </summary>
        private static uint StableHashToUint(string input)
        {
            uint hash = 2166136261;
            for (int i = 0; i < input.Length; i++)
            {
                hash ^= input[i];
                hash *= 16777619;
            }
            // Avoid 0 (which means "not set" for VesselPersistentId)
            return hash == 0 ? 1u : hash;
        }

        private void AddSerializedTree(RecordingTree tree)
        {
            var treeNode = new ConfigNode("RECORDING_TREE");
            tree.Save(treeNode);
            trees.Add(treeNode);
        }

        private void RegisterV3Builders(IEnumerable<RecordingBuilder> builders)
        {
            if (!useV3Format)
                return;

            foreach (var builder in builders)
                v3Builders.Add(builder);
        }

        private static Recording BuildRecording(RecordingBuilder builder)
        {
            string recordingId = builder.GetRecordingId();

            var rec = new Recording
            {
                RecordingId = recordingId,
                VesselName = builder.GetVesselName(),
                RecordingFormatVersion = builder.GetFormatVersion(),
                RecordingSchemaGeneration = builder.GetSchemaGeneration(),
                VesselPersistentId = StableHashToUint(recordingId),
                ExplicitStartUT = builder.GetStartUT(),
                ExplicitEndUT = builder.GetEndUT(),
                LoopPlayback = builder.GetLoopPlayback(),
                LoopIntervalSeconds = builder.GetLoopIntervalSeconds(),
                PlaybackEnabled = builder.GetPlaybackEnabled(),
                VesselSnapshot = builder.GetVesselSnapshot()?.CreateCopy(),
                GhostVisualSnapshot = builder.GetGhostVisualSnapshot()?.CreateCopy(),
            };
            rec.GhostSnapshotMode = RecordingStore.DetermineGhostSnapshotMode(rec);

            int? ts = builder.GetTerminalState();
            if (ts.HasValue)
                rec.TerminalStateValue = (TerminalState)ts.Value;

            double terrainH = builder.GetTerrainHeightAtEnd();
            if (!double.IsNaN(terrainH))
                rec.TerrainHeightAtEnd = terrainH;

            var groups = builder.GetRecordingGroups();
            if (groups != null && groups.Count > 0)
                rec.RecordingGroups = new List<string>(groups);

            if (!string.IsNullOrEmpty(builder.GetSegmentPhase()))
                rec.SegmentPhase = builder.GetSegmentPhase();
            if (!string.IsNullOrEmpty(builder.GetSegmentBodyName()))
                rec.SegmentBodyName = builder.GetSegmentBodyName();

            if (!string.IsNullOrEmpty(builder.GetChainId()))
                rec.ChainId = builder.GetChainId();
            if (builder.GetChainIndex() >= 0)
                rec.ChainIndex = builder.GetChainIndex();
            if (builder.GetChainBranch() > 0)
                rec.ChainBranch = builder.GetChainBranch();

            if (!string.IsNullOrEmpty(builder.GetParentRecordingId()))
                rec.ParentRecordingId = builder.GetParentRecordingId();
            if (!string.IsNullOrEmpty(builder.GetEvaCrewName()))
                rec.EvaCrewName = builder.GetEvaCrewName();

            if (!string.IsNullOrEmpty(builder.GetRewindSaveFileName()))
            {
                rec.RewindSaveFileName = builder.GetRewindSaveFileName();
                rec.RewindReservedFunds = builder.GetRewindReservedFunds();
                rec.RewindReservedScience = builder.GetRewindReservedScience();
                rec.RewindReservedRep = builder.GetRewindReservedRep();
            }

            var controllers = builder.GetControllers();
            if (controllers != null)
                rec.Controllers = new List<ControllerInfo>(controllers);
            rec.IsDebris = builder.GetIsDebris();
            // v13 debris parent-anchor contract: stamp adjacent to IsDebris
            // so cloned/merged/serialized synthetic debris recordings carry the
            // contract through every code path the recorder writes for live debris.
            // Recording.ApplyParentAnchorContract no-ops on non-debris, so this
            // assignment is safe even when the builder didn't call AsDebris().
            Recording.ApplyParentAnchorContract(rec, builder.GetParentAnchorRecordingId());

            return rec;
        }
    }
}
