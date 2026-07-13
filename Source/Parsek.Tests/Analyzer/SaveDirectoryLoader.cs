using System;
using System.Collections.Generic;
using System.IO;

namespace Parsek.Tests.Analyzer
{
    // The M-A1 loader: turns a save directory into a pure AnalyzerModel
    // (design doc "The loader"). ALL file I/O lives here; a file that fails to
    // parse becomes a LoadFault, never a crash. The invariant core downstream is
    // pure over the produced model.
    //
    // Phase 1.1 loads the persistent.sfs ParsekScenario node: recording trees +
    // recordings, ledger tombstones, and supersede relations. Phase 1.2 adds the
    // sidecar hydration + probe capture; Phase 1.3 adds the ledger + career parse.
    internal static class SaveDirectoryLoader
    {
        internal static AnalyzerModel Load(string saveDir, Func<string, CelestialBody> bodyResolver)
        {
            var model = new AnalyzerModel
            {
                SaveName = ResolveSaveName(saveDir),
                BodyResolver = bodyResolver,
                // File-scoped rules (INV7b / INV9) probe sidecars the loader does not
                // pre-materialize; they read this and no-op when it is null.
                SaveDirectory = saveDir,
            };

            var recordings = new List<Recording>();
            var trees = new List<RecordingTree>();
            var tombstones = new List<LedgerTombstone>();
            var supersedes = new List<RecordingSupersedeRelation>();
            var loadFaults = new List<LoadFault>();
            var sidecarSchema = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            var ledger = new List<GameAction>();
            CareerSaveSnapshot careerSave = null;

            // Quiet the production sidecar/tree logging during load; the analyzer
            // is not a KSP session and should not spam KSP-style lines.
            bool prevSuppress = RecordingStore.SuppressLogging;
            RecordingStore.SuppressLogging = true;
            try
            {
                string sfsPath = string.IsNullOrEmpty(saveDir)
                    ? null
                    : Path.Combine(saveDir, "persistent.sfs");

                ConfigNode root = TryLoadSfs(sfsPath, loadFaults);
                if (root != null)
                {
                    ConfigNode gameNode = DescendToGameNode(root);
                    ConfigNode scenario = FindParsekScenario(gameNode);
                    if (scenario != null)
                    {
                        LoadTrees(scenario, sfsPath, trees, recordings, loadFaults);
                        LoadStagingEntries(scenario, "RECORDING_SUPERSEDES",
                            supersedes, RecordingSupersedeRelation.LoadFrom);
                        LoadStagingEntries(scenario, "LEDGER_TOMBSTONES",
                            tombstones, LedgerTombstone.LoadFrom);
                        LoadSidecars(saveDir, recordings, sidecarSchema, loadFaults);
                    }

                    // Career parse (task 1.3): only kept when the save is career
                    // (Funding present). Non-career -> null, no fault (INV8 owns any
                    // career-availability finding).
                    careerSave = ParseCareer(root);
                }

                // RAW ledger (correction C1): parsed directly from ledger.pgld,
                // NEVER through Ledger.LoadFromFile, which mutates the process static
                // Ledger.actions. Unfiltered: INV8 computes the ELS filter itself.
                LoadLedger(saveDir, ledger, loadFaults);
            }
            finally
            {
                RecordingStore.SuppressLogging = prevSuppress;
            }

            model.FixtureStamp = ParseFixtureStamp(saveDir);
            model.Recordings = recordings;
            model.Trees = trees;
            model.Tombstones = tombstones;
            model.SupersedeRelations = supersedes;
            model.SidecarSchema = sidecarSchema;
            model.Ledger = ledger;
            model.CareerSave = careerSave;
            model.LoadFaults = loadFaults;

            LogLoadSummary(model, saveDir, loadFaults);
            return model;
        }

        /// <summary>
        /// Diagnostic logging (design "Diagnostic Logging"): one per-subject summary
        /// line at Verbose plus one Warn line per LoadFault. The per-fault Warn makes
        /// the "a file that failed to parse is itself a finding" contract observable
        /// in a run log. Emitted once at the end of a load (bounded: faults are few),
        /// after RecordingStore.SuppressLogging is restored, so these analyzer-tagged
        /// lines are not suppressed alongside the production sidecar logs.
        /// </summary>
        private static void LogLoadSummary(AnalyzerModel model, string saveDir, List<LoadFault> loadFaults)
        {
            ParsekLog.Verbose("Analyzer",
                "load save='" + (model.SaveName ?? "") + "' path='" + (saveDir ?? "") + "'"
                + " trees=" + model.Trees.Count
                + " recordings=" + model.Recordings.Count
                + " loadFaults=" + loadFaults.Count);

            foreach (LoadFault f in loadFaults)
            {
                ParsekLog.Warn("Analyzer",
                    "loadFault kind=" + (f.FileKind ?? "unknown")
                    + " recording=" + (f.RecordingId ?? "<none>")
                    + " path='" + (f.FilePath ?? "") + "'"
                    + " reason=" + (f.Reason ?? "unknown"));
            }
        }

        /// <summary>
        /// Reads the fixture corpus provenance stamp from
        /// <c>&lt;saveDir&gt;/fixture-generation.txt</c> (one line:
        /// <c>generation=&lt;n&gt; provenance=&lt;synthetic|harvested&gt;</c>).
        /// Returns null for a non-fixture subject (no file), which skips the
        /// STALE-FIXTURE check entirely. Tolerant of ordering / whitespace; a
        /// malformed / unreadable file yields null (unstamped) rather than a fault.
        /// </summary>
        private static FixtureStamp? ParseFixtureStamp(string saveDir)
        {
            if (string.IsNullOrEmpty(saveDir))
                return null;
            string path = Path.Combine(saveDir, "fixture-generation.txt");
            if (!File.Exists(path))
                return null;

            string text;
            try { text = File.ReadAllText(path); }
            catch { return null; }

            int generation = 0;
            string provenance = null;
            foreach (string token in text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = token.IndexOf('=');
                if (eq <= 0)
                    continue;
                string key = token.Substring(0, eq);
                string value = token.Substring(eq + 1);
                if (string.Equals(key, "generation", StringComparison.Ordinal))
                    int.TryParse(value, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out generation);
                else if (string.Equals(key, "provenance", StringComparison.Ordinal))
                    provenance = value;
            }

            // A file with neither field parsed is treated as unstamped.
            if (generation == 0 && string.IsNullOrEmpty(provenance))
                return null;
            return new FixtureStamp(generation, provenance ?? "synthetic");
        }

        private static string ResolveSaveName(string saveDir)
        {
            if (string.IsNullOrEmpty(saveDir))
                return "";
            // Directory name, robust to a trailing separator.
            string trimmed = saveDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }

        private static ConfigNode TryLoadSfs(string sfsPath, List<LoadFault> loadFaults)
        {
            if (string.IsNullOrEmpty(sfsPath) || !File.Exists(sfsPath))
                return null; // No footprint; not a fault (a rule reports the empty save).

            // Corruption pre-check: KSP's ConfigNode.Load parses unbalanced-brace
            // content leniently and returns a partial node rather than failing, so a
            // structural brace-balance check is the deterministic corruption signal
            // (design edge case "corrupt .sfs (unbalanced braces)").
            string text;
            try
            {
                text = File.ReadAllText(sfsPath);
            }
            catch (Exception ex)
            {
                loadFaults.Add(new LoadFault(sfsPath, "sfs", ex.GetType().Name + ": " + ex.Message, null));
                return null;
            }

            if (!BracesBalanced(text))
            {
                loadFaults.Add(new LoadFault(sfsPath, "sfs", "unbalanced-braces", null));
                return null;
            }

            ConfigNode root;
            try
            {
                root = ConfigNode.Load(sfsPath);
            }
            catch (Exception ex)
            {
                loadFaults.Add(new LoadFault(sfsPath, "sfs", ex.GetType().Name + ": " + ex.Message, null));
                return null;
            }

            if (root == null)
            {
                loadFaults.Add(new LoadFault(sfsPath, "sfs", "configNode-load-returned-null", null));
                return null;
            }

            // A non-empty file that parsed to a structurally empty node is corrupt.
            if (root.values.Count == 0 && root.nodes.Count == 0 && text.Trim().Length > 0)
            {
                loadFaults.Add(new LoadFault(sfsPath, "sfs", "empty-or-unparseable-sfs", null));
                return null;
            }

            return root;
        }

        private static bool BracesBalanced(string text)
        {
            if (string.IsNullOrEmpty(text))
                return true;
            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '{')
                {
                    depth++;
                }
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth < 0)
                        return false; // close before open
                }
            }
            return depth == 0;
        }

        /// <summary>
        /// Mirrors CareerSaveParser's root-vs-GAME descent: a KSP save's top node is
        /// GAME, but ConfigNode.Load can return either the GAME node itself (its
        /// FLIGHTSTATE/SCENARIO children are direct) or a wrapper.
        /// </summary>
        private static ConfigNode DescendToGameNode(ConfigNode root)
        {
            if (root.GetNode("FLIGHTSTATE") != null)
                return root;
            ConfigNode wrapped = root.GetNode("GAME");
            return wrapped ?? root;
        }

        private static ConfigNode FindParsekScenario(ConfigNode gameNode)
        {
            foreach (ConfigNode scenario in gameNode.GetNodes("SCENARIO"))
            {
                if (string.Equals(scenario.GetValue("name"), "ParsekScenario", StringComparison.Ordinal))
                    return scenario;
            }
            return null;
        }

        private static void LoadTrees(
            ConfigNode scenario,
            string sfsPath,
            List<RecordingTree> trees,
            List<Recording> recordings,
            List<LoadFault> loadFaults)
        {
            foreach (ConfigNode treeNode in scenario.GetNodes("RECORDING_TREE"))
            {
                var tree = new RecordingTree
                {
                    Id = treeNode.GetValue("id") ?? "",
                    TreeName = treeNode.GetValue("treeName") ?? "",
                    RootRecordingId = treeNode.GetValue("rootRecordingId") ?? "",
                    ActiveRecordingId = treeNode.GetValue("activeRecordingId"),
                };

                foreach (ConfigNode recNode in treeNode.GetNodes("RECORDING"))
                {
                    var rec = new Recording();
                    try
                    {
                        RecordingTreeRecordCodec.LoadRecordingFrom(recNode, rec);
                    }
                    catch (Exception ex)
                    {
                        loadFaults.Add(new LoadFault(
                            sfsPath, "tree-node",
                            ex.GetType().Name + ": " + ex.Message,
                            recNode.GetValue("recordingId")));
                        continue;
                    }

                    // Include schema-incompatible recordings (LoadRecordingFrom leaves
                    // RecordingFormatVersion == -1 but keeps the parsed id + generation)
                    // so INV5 can flag them; the analyzer must SEE what the production
                    // loader would reject, not silently drop it.
                    if (string.IsNullOrEmpty(rec.RecordingId))
                        continue;

                    tree.Recordings[rec.RecordingId] = rec;
                    rec.TreeId = tree.Id;
                    recordings.Add(rec);
                }

                trees.Add(tree);
            }
        }

        private static void LoadStagingEntries<T>(
            ConfigNode scenario,
            string containerNodeName,
            List<T> target,
            Func<ConfigNode, T> loadFrom)
        {
            foreach (ConfigNode container in scenario.GetNodes(containerNodeName))
            {
                foreach (ConfigNode entry in container.GetNodes("ENTRY"))
                    target.Add(loadFrom(entry));
            }
        }

        // --- Sidecar hydration + probe capture (task 1.2) ---

        private static void LoadSidecars(
            string saveDir,
            List<Recording> recordings,
            Dictionary<string, (int, int)> sidecarSchema,
            List<LoadFault> loadFaults)
        {
            var recordingIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (Recording rec in recordings)
            {
                if (string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                recordingIds.Add(rec.RecordingId);

                // Reject a malformed id BEFORE touching the filesystem (path traversal,
                // invalid filename chars). ValidateRecordingId is the production gate.
                if (!RecordingPaths.ValidateRecordingId(rec.RecordingId, RecordingIdValidationLogContext.Test))
                {
                    loadFaults.Add(new LoadFault(null, "trajectory", "invalid-recording-id", rec.RecordingId));
                    continue;
                }

                string precPath = Path.Combine(saveDir, RecordingPaths.BuildTrajectoryRelativePath(rec.RecordingId));
                LoadTrajectorySidecar(precPath, rec, sidecarSchema, loadFaults);
                LoadSnapshotSidecar(
                    Path.Combine(saveDir, RecordingPaths.BuildVesselSnapshotRelativePath(rec.RecordingId)),
                    rec.RecordingId, loadFaults, node => rec.VesselSnapshot = node);
                LoadSnapshotSidecar(
                    Path.Combine(saveDir, RecordingPaths.BuildGhostSnapshotRelativePath(rec.RecordingId)),
                    rec.RecordingId, loadFaults, node => rec.GhostVisualSnapshot = node);
            }

            InventoryOrphanSidecars(saveDir, recordingIds, sidecarSchema);
        }

        private static void LoadTrajectorySidecar(
            string precPath,
            Recording rec,
            Dictionary<string, (int, int)> sidecarSchema,
            List<LoadFault> loadFaults)
        {
            // A genuinely absent trajectory sidecar is not a loader fault (the
            // recording simply has none on disk); INV5 owns any presence policy.
            if (!File.Exists(precPath))
                return;

            TrajectorySidecarProbe probe;
            bool probed = RecordingStore.TryProbeTrajectorySidecar(precPath, out probe);
            if (!probed)
            {
                // Header could not be read at all (e.g. truncated before the magic).
                loadFaults.Add(new LoadFault(precPath, "trajectory",
                    string.IsNullOrEmpty(probe.FailureReason) ? "probe-failed" : probe.FailureReason,
                    rec.RecordingId));
                return;
            }

            // C2: capture the sidecar's reported (generation, formatVersion) for INV5.
            sidecarSchema[rec.RecordingId] = (probe.SchemaGeneration, probe.FormatVersion);

            if (!probe.Supported)
            {
                loadFaults.Add(new LoadFault(precPath, "trajectory",
                    string.IsNullOrEmpty(probe.FailureReason) ? "unsupported" : probe.FailureReason,
                    rec.RecordingId));
                return;
            }

            try
            {
                if (!RecordingStore.LoadTrajectorySidecarForTesting(precPath, rec))
                {
                    loadFaults.Add(new LoadFault(precPath, "trajectory",
                        string.IsNullOrEmpty(probe.FailureReason) ? "load-failed" : probe.FailureReason,
                        rec.RecordingId));
                }
            }
            catch (Exception ex)
            {
                loadFaults.Add(new LoadFault(precPath, "trajectory",
                    ex.GetType().Name + ": " + ex.Message, rec.RecordingId));
            }
        }

        private static void LoadSnapshotSidecar(
            string path,
            string recordingId,
            List<LoadFault> loadFaults,
            Action<ConfigNode> assign)
        {
            if (!File.Exists(path))
                return; // Snapshots are optional (destroyed / showcase recordings).

            try
            {
                ConfigNode node;
                if (RecordingStore.LoadSnapshotSidecarForTesting(path, out node) && node != null)
                {
                    assign(node);
                    return;
                }

                string reason = "snapshot-load-failed";
                SnapshotSidecarProbe probe;
                if (RecordingStore.TryProbeSnapshotSidecar(path, out probe)
                    && !string.IsNullOrEmpty(probe.FailureReason))
                {
                    reason = probe.FailureReason;
                }
                loadFaults.Add(new LoadFault(path, "snapshot", reason, recordingId));
            }
            catch (Exception ex)
            {
                loadFaults.Add(new LoadFault(path, "snapshot",
                    ex.GetType().Name + ": " + ex.Message, recordingId));
            }
        }

        /// <summary>
        /// Inventories orphan trajectory sidecars: a <c>.prec</c> on disk that no tree
        /// recording references. Captured into <paramref name="sidecarSchema"/> (a key
        /// with no matching recording) so INV5 can flag it later, without a dedicated
        /// model field.
        /// </summary>
        private static void InventoryOrphanSidecars(
            string saveDir,
            HashSet<string> recordingIds,
            Dictionary<string, (int, int)> sidecarSchema)
        {
            string recordingsDir = Path.Combine(saveDir, "Parsek", "Recordings");
            if (!Directory.Exists(recordingsDir))
                return;

            foreach (string precFile in Directory.GetFiles(recordingsDir, "*.prec"))
            {
                string id = Path.GetFileNameWithoutExtension(precFile);
                if (string.IsNullOrEmpty(id) || recordingIds.Contains(id) || sidecarSchema.ContainsKey(id))
                    continue;

                TrajectorySidecarProbe probe;
                sidecarSchema[id] = RecordingStore.TryProbeTrajectorySidecar(precFile, out probe)
                    ? (probe.SchemaGeneration, probe.FormatVersion)
                    : (0, 0);
            }
        }

        // --- Ledger + career parse (task 1.3) ---

        /// <summary>
        /// Parses <c>saves/&lt;save&gt;/Parsek/GameState/ledger.pgld</c> directly into
        /// a RAW, unfiltered action list (correction C1). Deliberately does NOT call
        /// <c>Ledger.LoadFromFile</c> (which mutates the process-static
        /// <c>Ledger.actions</c>), keeping the loader pure and Sequential-safe.
        /// </summary>
        private static void LoadLedger(string saveDir, List<GameAction> ledger, List<LoadFault> loadFaults)
        {
            if (string.IsNullOrEmpty(saveDir))
                return;

            string ledgerPath = Path.Combine(saveDir, RecordingPaths.BuildLedgerRelativePath());
            if (!File.Exists(ledgerPath))
                return; // No ledger file == empty ledger; not a fault.

            ConfigNode loaded;
            try
            {
                loaded = ConfigNode.Load(ledgerPath);
            }
            catch (Exception ex)
            {
                loadFaults.Add(new LoadFault(ledgerPath, "ledger", ex.GetType().Name + ": " + ex.Message, null));
                return;
            }

            if (loaded == null)
            {
                loadFaults.Add(new LoadFault(ledgerPath, "ledger", "configNode-load-returned-null", null));
                return;
            }

            foreach (ConfigNode actionNode in loaded.GetNodes("GAME_ACTION"))
            {
                try
                {
                    ledger.Add(GameAction.DeserializeFrom(actionNode));
                }
                catch (Exception ex)
                {
                    loadFaults.Add(new LoadFault(ledgerPath, "ledger", ex.GetType().Name + ": " + ex.Message, null));
                }
            }
        }

        /// <summary>
        /// Parses the career totals from the loaded save root, returning ANY snapshot
        /// the parser recognized (Parsed == true), regardless of the funds facet.
        /// Unparsable / unrecognizable shape -> null, no fault (INV8 owns any
        /// career-availability finding).
        ///
        /// Module M-B2 dropped the former <c>HasFunds</c> gate here: the analyzer's
        /// careerSave export block must be POPULATED on a career-but-non-funds save
        /// (Science / Sandbox), so the ledger-oracle verifier reads the per-facet
        /// <c>hasFunds</c>/<c>hasScience</c>/<c>hasRep</c> flags (facet-absence) rather
        /// than inferring absence from a missing block (which means an old/broken
        /// analyzer). INV8's funds-only career diff is unaffected: it RE-GATES on
        /// <c>HasFunds</c> (<c>Inv8Ledger.EvaluateCareerDiff</c>), not on snapshot
        /// null-ness, so a Science / Sandbox snapshot still skips part (b).
        /// </summary>
        private static CareerSaveSnapshot ParseCareer(ConfigNode root)
        {
            CareerSaveSnapshot snapshot = CareerSaveParser.Parse(root);
            if (snapshot != null && snapshot.Parsed)
                return snapshot;
            return null;
        }
    }
}
