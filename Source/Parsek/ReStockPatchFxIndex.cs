using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Index of the EFFECTS definitions ReStock authors for stock parts, parsed from
    /// ReStock's ModuleManager patch FILES on disk (<c>GameData/ReStock/Patches/**/*.cfg</c>).
    /// MM strips patch nodes from GameDatabase post-patch, so the only way to recover what
    /// the install looked like BEFORE a Waterfall config pack deleted the EFFECTS is to
    /// re-read the patch files; they are plain ConfigNode-parseable cfg text.
    /// Patch node names are decorated MM names (e.g.
    /// <c>@PART[liquidEngine2_v2]:HAS[~RestockIgnore[*]]:FOR[000_ReStock]</c>), so this is
    /// deliberately NOT the <c>GetNodes("PART")</c> strategy of
    /// <see cref="PristinePartFxResolver"/>: all child nodes are enumerated and matched by
    /// name prefix, and the part target is the token between the FIRST '[' and FIRST ']'.
    /// Directory absent = ReStock not installed = permanently empty index (one log line,
    /// zero per-part work), which is the stock-install no-op guarantee.
    /// </summary>
    internal static class ReStockPatchFxIndex
    {
        /// <summary>FX-relevant data extracted from one ReStock @PART patch node.</summary>
        internal sealed class ReStockPartFxEntry
        {
            public string SourceFile;
            /// <summary>
            /// The fresh EFFECTS node ReStock writes after its <c>!EFFECTS {}</c> delete;
            /// null when the patch node authored none (e.g. depthmask-only patches).
            /// Matched by EXACT node name "EFFECTS" so the '!EFFECTS' deletion node never
            /// counts as authored content.
            /// </summary>
            public ConfigNode EffectsNode;
            /// <summary>
            /// Per-ordinal effect group names of the patch's MODULE[ModuleEngines* nodes,
            /// in file order. May be empty when ReStock left the engine modules untouched
            /// (e.g. RAPIER keeps the stock module + stock group names).
            /// </summary>
            public List<HashSet<string>> EngineModuleEffectNames = new List<HashSet<string>>();
            /// <summary>Per-ordinal runningEffectName of the patch's MODULE[ModuleRCS* nodes.</summary>
            public List<string> RcsRunningEffectNames = new List<string>();
        }

        /// <summary>Batch counters for one index build (testable via TryExtractFromPatchFileRoot).</summary>
        internal sealed class ExtractStats
        {
            public int PartPatchNodes;
            public int WildcardSkips;
            public int MalformedTargets;
            public int EffectsBearing;
            public int DuplicateEffectsConflicts;
        }

        private const string PatchesRelativePath = "GameData/ReStock/Patches";

        private static Dictionary<string, ReStockPartFxEntry> indexByPartName;

        /// <summary>Test seam: maps a patch file path to a parsed root node.</summary>
        internal static System.Func<string, ConfigNode> LoadFileRootOverrideForTesting;
        /// <summary>Test seam: replaces the GameData/ReStock/Patches directory enumeration.</summary>
        internal static System.Func<string[]> EnumeratePatchFilesOverrideForTesting;

        private static readonly string[] EngineEffectNameKeys =
            { "runningEffectName", "powerEffectName", "spoolEffectName", "directThrottleEffectName" };

        /// <summary>
        /// Returns the ReStock patch FX entry for a runtime part name (dot-form).
        /// Builds the index on first call; an absent ReStock install yields a permanently
        /// empty index.
        /// </summary>
        internal static bool TryGetForPart(string runtimePartName, out ReStockPartFxEntry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(runtimePartName))
                return false;

            EnsureBuilt();
            return indexByPartName.TryGetValue(runtimePartName, out entry);
        }

        /// <summary>
        /// True when ReStock authored a fresh EFFECTS node for this part, i.e. the
        /// install's correct look for it is ReStock's. Used both to prefer ReStock
        /// recovery over the pristine stock definitions and to stand down the hardcoded
        /// stock per-part FX tunings.
        /// </summary>
        internal static bool HasAuthoredEffectsFor(string runtimePartName)
        {
            return TryGetForPart(runtimePartName, out ReStockPartFxEntry entry) &&
                entry.EffectsNode != null;
        }

        private static void EnsureBuilt()
        {
            if (indexByPartName != null)
                return;

            indexByPartName = new Dictionary<string, ReStockPartFxEntry>(System.StringComparer.Ordinal);

            string[] files = EnumeratePatchFiles();
            if (files == null || files.Length == 0)
            {
                ParsekLog.Info("ReStockCompat",
                    "ReStock patch directory absent or empty; index permanently empty (ReStock not installed)");
                return;
            }

            var stats = new ExtractStats();
            int parsedFiles = 0;
            int loadFailures = 0;
            for (int i = 0; i < files.Length; i++)
            {
                ConfigNode fileRoot = LoadFileRoot(files[i]);
                if (fileRoot == null)
                {
                    loadFailures++;
                    continue;
                }
                parsedFiles++;
                TryExtractFromPatchFileRoot(fileRoot, files[i], indexByPartName, stats);
            }

            ParsekLog.Info("ReStockCompat",
                $"ReStock patch FX index built: files={files.Length} parsed={parsedFiles} " +
                $"loadFailures={loadFailures} partPatchNodes={stats.PartPatchNodes} " +
                $"partsIndexed={indexByPartName.Count} effectsBearing={stats.EffectsBearing} " +
                $"wildcardSkips={stats.WildcardSkips} malformedTargets={stats.MalformedTargets} " +
                $"duplicateEffectsConflicts={stats.DuplicateEffectsConflicts}");
        }

        private static string[] EnumeratePatchFiles()
        {
            if (EnumeratePatchFilesOverrideForTesting != null)
                return EnumeratePatchFilesOverrideForTesting();

            try
            {
                string root = System.IO.Path.Combine(
                    KSPUtil.ApplicationRootPath ?? string.Empty, PatchesRelativePath);
                if (!System.IO.Directory.Exists(root))
                    return null;
                return System.IO.Directory.GetFiles(root, "*.cfg", System.IO.SearchOption.AllDirectories);
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn("ReStockCompat",
                    $"ReStock patch directory enumeration threw: {ex.Message}; index stays empty");
                return null;
            }
        }

        private static ConfigNode LoadFileRoot(string path)
        {
            try
            {
                if (LoadFileRootOverrideForTesting != null)
                    return LoadFileRootOverrideForTesting(path);

                ConfigNode root = ConfigNode.Load(path);
                if (root == null)
                {
                    ParsekLog.Warn("ReStockCompat", $"ReStock patch file failed to parse: '{path}'");
                }
                return root;
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn("ReStockCompat",
                    $"ReStock patch file load threw ('{path}'): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Pure extraction: walks all top-level nodes of one parsed patch file, indexes
        /// every @PART/+PART target by runtime (dot-form) name. Merge rule when one part
        /// is targeted by multiple patch nodes/files: the first EFFECTS-bearing node wins;
        /// EFFECTS-less nodes never overwrite an EFFECTS-bearing entry; a second
        /// EFFECTS-bearing node is counted and warn-logged (ReStock authors at most one).
        /// </summary>
        internal static void TryExtractFromPatchFileRoot(
            ConfigNode fileRoot, string sourceFile,
            Dictionary<string, ReStockPartFxEntry> index, ExtractStats stats)
        {
            if (fileRoot == null || index == null || stats == null)
                return;

            ConfigNode[] children = fileRoot.GetNodes();
            for (int i = 0; i < children.Length; i++)
            {
                string nodeName = children[i].name ?? string.Empty;
                if (!nodeName.StartsWith("@PART[", System.StringComparison.Ordinal) &&
                    !nodeName.StartsWith("+PART[", System.StringComparison.Ordinal))
                {
                    continue;
                }

                stats.PartPatchNodes++;
                if (!TryExtractPartTarget(nodeName, out string target))
                {
                    stats.MalformedTargets++;
                    continue;
                }

                // Wildcard check runs ONLY on the extracted target token. Every real
                // ReStock patch carries ':HAS[~RestockIgnore[*]]' in the SUFFIX; matching
                // the full decorated name would skip 100% of patches.
                if (target.IndexOf('*') >= 0 || target.IndexOf('?') >= 0)
                {
                    stats.WildcardSkips++;
                    continue;
                }

                string runtimeName = target.Replace('_', '.');
                ReStockPartFxEntry entry = BuildEntryFromPartPatchNode(children[i], sourceFile);

                if (index.TryGetValue(runtimeName, out ReStockPartFxEntry existing))
                {
                    if (existing.EffectsNode != null)
                    {
                        if (entry.EffectsNode != null)
                        {
                            stats.DuplicateEffectsConflicts++;
                            ParsekLog.Warn("ReStockCompat",
                                $"part '{runtimeName}' has a second EFFECTS-bearing ReStock patch node " +
                                $"in '{sourceFile}' (keeping the first from '{existing.SourceFile}')");
                        }
                        continue;
                    }
                    if (entry.EffectsNode == null)
                        continue;
                }

                index[runtimeName] = entry;
                if (entry.EffectsNode != null)
                    stats.EffectsBearing++;
            }
        }

        /// <summary>
        /// Extracts the part target from a decorated MM patch node name: the token between
        /// the FIRST '[' and the FIRST ']' (':HAS[...]:FOR[...]' suffixes follow the
        /// closing bracket and are ignored). Returns false on malformed names.
        /// </summary>
        internal static bool TryExtractPartTarget(string nodeName, out string target)
        {
            target = null;
            if (string.IsNullOrEmpty(nodeName))
                return false;

            int open = nodeName.IndexOf('[');
            int close = nodeName.IndexOf(']');
            if (open < 0 || close <= open + 1)
                return false;

            target = nodeName.Substring(open + 1, close - open - 1).Trim();
            return target.Length > 0;
        }

        private static ReStockPartFxEntry BuildEntryFromPartPatchNode(ConfigNode partPatchNode, string sourceFile)
        {
            var entry = new ReStockPartFxEntry { SourceFile = sourceFile };

            ConfigNode[] children = partPatchNode.GetNodes();
            for (int i = 0; i < children.Length; i++)
            {
                string childName = children[i].name ?? string.Empty;

                // EXACT name match: ReStock writes the fresh node as plain 'EFFECTS';
                // the preceding '!EFFECTS' deletion node must not count.
                if (entry.EffectsNode == null &&
                    string.Equals(childName, "EFFECTS", System.StringComparison.Ordinal))
                {
                    entry.EffectsNode = children[i];
                    continue;
                }

                // Covers @MODULE[ModuleEngines], @MODULE[ModuleEngines*], @MODULE[ModuleEnginesFX].
                if (childName.IndexOf("MODULE[ModuleEngines", System.StringComparison.Ordinal) >= 0)
                {
                    entry.EngineModuleEffectNames.Add(ReadPatchEngineEffectNames(children[i]));
                }
                else if (childName.IndexOf("MODULE[ModuleRCS", System.StringComparison.Ordinal) >= 0)
                {
                    string running = ReadPatchValue(children[i], "runningEffectName");
                    entry.RcsRunningEffectNames.Add(string.IsNullOrEmpty(running) ? "running" : running);
                }
            }

            return entry;
        }

        /// <summary>
        /// Reads the named effect groups of one patch MODULE node. Value names carry MM
        /// assignment prefixes ('%runningEffectName', '@runningEffectName'); deletion
        /// forms ('!powerEffectName', '-powerEffectName') are skipped, not group names.
        /// </summary>
        internal static HashSet<string> ReadPatchEngineEffectNames(ConfigNode moduleNode)
        {
            var result = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            if (moduleNode == null)
                return result;

            for (int k = 0; k < EngineEffectNameKeys.Length; k++)
            {
                string value = ReadPatchValue(moduleNode, EngineEffectNameKeys[k]);
                if (!string.IsNullOrEmpty(value))
                    result.Add(value);
            }
            return result;
        }

        /// <summary>
        /// Reads one value from a patch node, tolerating MM assignment prefixes on the
        /// value NAME: plain, '@' (edit), '%' (set-or-create), '&' (create-if-missing)
        /// are accepted; '!' and '-' (delete) and '#'/'|' (copy/path forms) are not
        /// assignments and are skipped. Returns the first match.
        /// </summary>
        internal static string ReadPatchValue(ConfigNode node, string plainName)
        {
            if (node == null || string.IsNullOrEmpty(plainName))
                return null;

            for (int v = 0; v < node.values.Count; v++)
            {
                string rawName = node.values[v].name;
                if (string.IsNullOrEmpty(rawName))
                    continue;

                char first = rawName[0];
                string stripped;
                if (first == '@' || first == '%' || first == '&')
                    stripped = rawName.Substring(1);
                else if (first == '!' || first == '-' || first == '#' || first == '|')
                    continue;
                else
                    stripped = rawName;

                // MM value names may carry trailing indexers/operators (',0', ',*', ' +'),
                // none observed in ReStock effect-name keys; exact match keeps this strict.
                if (string.Equals(stripped, plainName, System.StringComparison.Ordinal))
                    return node.values[v].value;
            }
            return null;
        }

        /// <summary>
        /// Resolves the EFFECTS group-name filter for one engine-module ordinal of a
        /// ReStock-authored EFFECTS scan. Source chain: patch-authored per-ordinal names
        /// (ReStock set them, authoritative) -> pristine per-ordinal names (ReStock left
        /// the module untouched, so the stock cfg names match its fresh groups; verified
        /// for RAPIER) -> all-groups for single-engine-module parts -> skip for
        /// multi-module parts with no per-ordinal source (scanning all groups would bleed
        /// other modes' FX onto this module, mirroring the pristine ordinal-miss rule).
        /// </summary>
        internal static bool TryResolveEngineGroupFilter(
            ReStockPartFxEntry entry,
            PristinePartFxResolver.PristinePartFxData pristine,
            int moduleIndex,
            int liveEngineModuleCount,
            out HashSet<string> groupNames,
            out string source)
        {
            groupNames = null;
            source = "skip";
            if (entry == null)
                return false;

            if (moduleIndex < entry.EngineModuleEffectNames.Count &&
                entry.EngineModuleEffectNames[moduleIndex].Count > 0)
            {
                groupNames = entry.EngineModuleEffectNames[moduleIndex];
                source = "patch";
                return true;
            }

            if (pristine != null && pristine.Found &&
                moduleIndex < pristine.EngineModuleEffectNames.Count &&
                pristine.EngineModuleEffectNames[moduleIndex].Count > 0)
            {
                groupNames = pristine.EngineModuleEffectNames[moduleIndex];
                source = "pristine";
                return true;
            }

            if (liveEngineModuleCount <= 1)
            {
                groupNames = null;
                source = "all";
                return true;
            }

            return false;
        }

        /// <summary>
        /// Resolves the RCS running group name for one RCS-module ordinal: patch-authored
        /// name -> pristine per-ordinal name -> the stock default 'running'.
        /// </summary>
        internal static string ResolveRcsRunningGroupName(
            ReStockPartFxEntry entry,
            PristinePartFxResolver.PristinePartFxData pristine,
            int moduleIndex)
        {
            if (entry != null && moduleIndex < entry.RcsRunningEffectNames.Count &&
                !string.IsNullOrEmpty(entry.RcsRunningEffectNames[moduleIndex]))
            {
                return entry.RcsRunningEffectNames[moduleIndex];
            }

            if (pristine != null && pristine.Found &&
                moduleIndex < pristine.RcsRunningEffectNames.Count &&
                !string.IsNullOrEmpty(pristine.RcsRunningEffectNames[moduleIndex]))
            {
                return pristine.RcsRunningEffectNames[moduleIndex];
            }

            return "running";
        }

        /// <summary>
        /// All indexed entries keyed by runtime part name (builds the index on first
        /// call). Used by the in-game resolve-exactly sweep.
        /// </summary>
        internal static IEnumerable<KeyValuePair<string, ReStockPartFxEntry>> Entries()
        {
            EnsureBuilt();
            return indexByPartName;
        }

        /// <summary>
        /// True when ReStock is installed (the patch index has entries). ReStock
        /// deletes the legacy fx_* keys its covered engines carried, which empties
        /// the legacy FX prefab donor cache install-wide (jets and ReStock-authored
        /// smoke trails reference fx_smokeTrail_* with no surviving donor part), so
        /// the builtin Effects/ resolution must also engage without Waterfall.
        /// </summary>
        internal static bool IsReStockInstalled()
        {
            EnsureBuilt();
            return indexByPartName.Count > 0;
        }

        internal static void ResetForTesting()
        {
            indexByPartName = null;
            LoadFileRootOverrideForTesting = null;
            EnumeratePatchFilesOverrideForTesting = null;
        }
    }
}
