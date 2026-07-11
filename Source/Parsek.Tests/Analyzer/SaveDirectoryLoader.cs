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
            };

            var recordings = new List<Recording>();
            var trees = new List<RecordingTree>();
            var tombstones = new List<LedgerTombstone>();
            var supersedes = new List<RecordingSupersedeRelation>();
            var loadFaults = new List<LoadFault>();

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
                    }
                }
            }
            finally
            {
                RecordingStore.SuppressLogging = prevSuppress;
            }

            model.Recordings = recordings;
            model.Trees = trees;
            model.Tombstones = tombstones;
            model.SupersedeRelations = supersedes;
            model.LoadFaults = loadFaults;
            return model;
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
    }
}
