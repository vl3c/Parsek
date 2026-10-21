using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    /// <summary>
    /// Committed recording trees the recording schema gate rejected on load (another recording
    /// schema generation, older or newer), kept INERT (todo GC3-SCHEMA-REJECTED-TREES-LOSE-EARNED-VALUE,
    /// owner ruling 2026-10-21 "Keep them inert, untouched").
    ///
    /// <para>This is a pass-through, not a migration. The holder keeps a deep copy of each rejected
    /// committed RECORDING_TREE node exactly as the save held it and writes it back verbatim on every
    /// save. Nothing reads, plays or interprets an inert tree beyond the keys collected here: its
    /// tree id, the <c>recordingId</c> of each RECORDING child (so the ledger reconcile, the load-time
    /// sweeps, the rewind-point reaper and the file collector treat those recordings as known), the
    /// RECORDING children's <c>recordingGroup</c> values (so the group hierarchy prune keeps their
    /// nesting) and the content-hash / rewind-save keys the file collector's own save scanner reads.
    /// Nothing writes to an inert node, so the matching Parsek build reads the save exactly as it
    /// wrote it.</para>
    ///
    /// <para>Scope: COMMITTED trees only. A rejected active (isActive) or pending (isPending) node is
    /// an uncommitted flight and stays dropped. Partial rejection inside a current-generation tree
    /// keeps the per-recording prune (<c>RecordingTree.PruneRejectedRecordingReferences</c>). A tree
    /// that passes the gate but whose sidecars fail to hydrate is not inert.</para>
    ///
    /// <para>Lifetime: the same as the committed trees. <c>ParsekScenario.LoadRecordingTrees</c>
    /// replaces the holder (it is where the committed trees are cleared and reloaded); the cold-load
    /// session reset clears it; every other load keeps it, as it keeps the committed trees.</para>
    /// </summary>
    internal static class InertRecordingTrees
    {
        internal const string Tag = "InertTrees";

        /// <summary>Tree ids listed in the load summary line before "and N more".</summary>
        private const int MaxListedTrees = 20;

        internal sealed class Entry
        {
            public string TreeId;
            public string TreeName;
            /// <summary>Why the gate rejected it: <c>tree-&lt;reason&gt;</c> or <c>recordings-&lt;reason&gt;</c>.</summary>
            public string Reason;
            /// <summary>The generation the node carries, as written (diagnostics only).</summary>
            public string Generation;
            /// <summary>Deep copy of the node as loaded; never mutated, copied again on every write.</summary>
            public ConfigNode Node;
            public List<string> RecordingIds = new List<string>();
            public List<string> GroupNames = new List<string>();
        }

        private static readonly List<Entry> entries = new List<Entry>();
        private static readonly HashSet<string> recordingIds = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> treeIds = new HashSet<string>(StringComparer.Ordinal);
        private static bool loadNoticeTaken;

        internal static int Count => entries.Count;

        internal static IReadOnlyList<Entry> Entries => entries;

        internal static bool ContainsRecordingId(string recordingId)
        {
            return !string.IsNullOrEmpty(recordingId) && recordingIds.Contains(recordingId);
        }

        internal static bool ContainsTreeId(string treeId)
        {
            return !string.IsNullOrEmpty(treeId) && treeIds.Contains(treeId);
        }

        // ---------------------------------------------------------------- classification

        /// <summary>
        /// Pure: why the recording schema gate rejected <paramref name="treeNode"/> as a whole, or
        /// null when it did not. Two shapes count: the tree-level gate in
        /// <see cref="RecordingTree.Load"/> (the tree's <c>recordingSchemaGeneration</c> is not the
        /// current one; reason <c>tree-generation-older|newer|missing</c>), and a tree whose
        /// recordings the per-recording gate rejected so that nothing loaded (every recording, or
        /// the root and with it the whole tree; reason <c>recordings-&lt;first reason&gt;</c>).
        /// <paramref name="loaded"/> is what <see cref="RecordingTree.Load"/> returned for the node;
        /// when null, only the node is read and "nothing loaded" means every RECORDING child failed
        /// the gate. A tree with no recordings at all, or with at least one loaded recording, is
        /// not rejected.
        /// </summary>
        internal static string ClassifySchemaRejection(ConfigNode treeNode, RecordingTree loaded)
        {
            if (treeNode == null)
                return null;

            var ic = CultureInfo.InvariantCulture;
            int treeGeneration;
            if (!int.TryParse(treeNode.GetValue("recordingSchemaGeneration"), NumberStyles.Integer, ic,
                    out treeGeneration))
                treeGeneration = 0;
            if (treeGeneration != RecordingStore.CurrentRecordingSchemaGeneration)
            {
                RecordingStore.IsRecordingSchemaCompatible(
                    RecordingStore.CurrentRecordingFormatVersion, treeGeneration, out string treeReason);
                return "tree-" + (treeReason ?? "generation-mismatch");
            }

            ConfigNode[] recNodes = treeNode.GetNodes("RECORDING");
            int rejected = 0;
            string firstReason = null;
            for (int i = 0; i < recNodes.Length; i++)
            {
                int format;
                if (!int.TryParse(recNodes[i].GetValue("recordingFormatVersion"), NumberStyles.Integer, ic,
                        out format))
                    format = 0;
                int generation;
                if (!int.TryParse(recNodes[i].GetValue("recordingSchemaGeneration"), NumberStyles.Integer, ic,
                        out generation))
                    generation = 0;
                if (!RecordingStore.IsRecordingSchemaCompatible(format, generation, out string reason))
                {
                    rejected++;
                    if (firstReason == null)
                        firstReason = reason;
                }
            }
            if (rejected == 0)
                return null;

            bool nothingLoaded = loaded != null
                ? loaded.Recordings == null || loaded.Recordings.Count == 0
                : rejected == recNodes.Length;
            return nothingLoaded ? "recordings-" + firstReason : null;
        }

        // ---------------------------------------------------------------- load

        /// <summary>
        /// Called by <c>ParsekScenario.LoadRecordingTrees</c> before it reads the save's trees: the
        /// holder is replaced, never appended to.
        /// </summary>
        internal static void BeginLoad()
        {
            int previous = entries.Count;
            Clear();
            if (previous > 0)
                ParsekLog.Verbose(Tag,
                    $"BeginLoad: dropped {previous.ToString(CultureInfo.InvariantCulture)} inert tree(s) " +
                    "of the previous load; the trees being loaded replace them");
        }

        /// <summary>
        /// Keeps a deep copy of a rejected committed tree node and collects its tree id, recording
        /// ids and group names. Returns the entry (null for a null node).
        /// </summary>
        internal static Entry SetAside(ConfigNode treeNode, string reason)
        {
            if (treeNode == null)
                return null;

            var entry = new Entry
            {
                TreeId = treeNode.GetValue("id") ?? "",
                TreeName = treeNode.GetValue("treeName") ?? "",
                Reason = reason ?? "unknown",
                Generation = treeNode.GetValue("recordingSchemaGeneration") ?? "(missing)",
                Node = treeNode.CreateCopy(),
            };

            ConfigNode[] recNodes = treeNode.GetNodes("RECORDING");
            var generations = new List<string>();
            for (int i = 0; i < recNodes.Length; i++)
            {
                string id = recNodes[i].GetValue("recordingId");
                if (!string.IsNullOrEmpty(id) && !entry.RecordingIds.Contains(id))
                    entry.RecordingIds.Add(id);
                string[] groups = recNodes[i].GetValues("recordingGroup");
                for (int g = 0; g < groups.Length; g++)
                {
                    if (!string.IsNullOrEmpty(groups[g]) && !entry.GroupNames.Contains(groups[g]))
                        entry.GroupNames.Add(groups[g]);
                }
                string generation = recNodes[i].GetValue("recordingSchemaGeneration") ?? "(missing)";
                if (!generations.Contains(generation))
                    generations.Add(generation);
            }
            // For a tree whose recordings were rejected, the recordings' generations are the news.
            if (entry.Reason.StartsWith("recordings-", StringComparison.Ordinal) && generations.Count > 0)
                entry.Generation = string.Join("/", generations.ToArray());

            entries.Add(entry);
            if (!string.IsNullOrEmpty(entry.TreeId))
                treeIds.Add(entry.TreeId);
            for (int i = 0; i < entry.RecordingIds.Count; i++)
                recordingIds.Add(entry.RecordingIds[i]);

            if (entries.Count <= MaxListedTrees)
                ParsekLog.Verbose(Tag,
                    $"Set aside inert tree id={entry.TreeId} name='{entry.TreeName}' reason={entry.Reason} " +
                    $"generation={entry.Generation} recordings={entry.RecordingIds.Count.ToString(CultureInfo.InvariantCulture)} " +
                    $"groups={entry.GroupNames.Count.ToString(CultureInfo.InvariantCulture)}: node kept verbatim, " +
                    "recording ids counted as known");
            return entry;
        }

        /// <summary>
        /// Called by <c>LoadRecordingTrees</c> for an active / pending marker node: a rejected one
        /// is an uncommitted flight and is NOT kept (its restore drops it, as before). Logs the
        /// decision so the drop is not silent. Returns the rejection reason, or null.
        /// </summary>
        internal static string NoteDroppedParkedNode(ConfigNode treeNode, string kind)
        {
            string reason = ClassifySchemaRejection(treeNode, null);
            if (reason == null)
                return null;
            ParsekLog.Warn(Tag,
                $"LoadRecordingTrees: {kind} tree node id={treeNode.GetValue("id") ?? "<none>"} " +
                $"generation={treeNode.GetValue("recordingSchemaGeneration") ?? "(missing)"} reason={reason} " +
                "is an uncommitted flight from a different Parsek version and is not kept " +
                "(only committed trees are kept inert)");
            return reason;
        }

        /// <summary>
        /// The one Info line after a load that set trees aside: count, tree ids and generations.
        /// No line when nothing was set aside.
        /// </summary>
        internal static void LogLoadSummary()
        {
            if (entries.Count == 0)
                return;
            var ic = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            int listed = Math.Min(entries.Count, MaxListedTrees);
            for (int i = 0; i < listed; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(entries[i].TreeId).Append("(generation=").Append(entries[i].Generation)
                    .Append(" reason=").Append(entries[i].Reason).Append(')');
            }
            if (entries.Count > listed)
                sb.Append(" and ").Append((entries.Count - listed).ToString(ic)).Append(" more");
            ParsekLog.Info(Tag,
                $"{entries.Count.ToString(ic)} committed tree(s) from a different recording schema generation " +
                $"kept inert (current generation={RecordingStore.CurrentRecordingSchemaGeneration.ToString(ic)}, " +
                $"recordings={recordingIds.Count.ToString(ic)}): {sb}. Their nodes are written back verbatim and " +
                "their recording ids count as known; nothing plays or changes them");
        }

        // ---------------------------------------------------------------- save

        /// <summary>
        /// Re-emits every inert node verbatim (a fresh copy) into <paramref name="scenarioNode"/>,
        /// after the committed trees. A node whose id a committed tree now carries is skipped
        /// (logged): one id never appears twice in a save. Returns the number written.
        /// </summary>
        internal static int AppendTo(ConfigNode scenarioNode, IList<RecordingTree> committedTrees)
        {
            if (scenarioNode == null || entries.Count == 0)
                return 0;

            var committedIds = new HashSet<string>(StringComparer.Ordinal);
            if (committedTrees != null)
            {
                for (int i = 0; i < committedTrees.Count; i++)
                {
                    string id = committedTrees[i]?.Id;
                    if (!string.IsNullOrEmpty(id))
                        committedIds.Add(id);
                }
            }

            int written = 0;
            int skipped = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                if (entry?.Node == null)
                    continue;
                if (!string.IsNullOrEmpty(entry.TreeId) && committedIds.Contains(entry.TreeId))
                {
                    skipped++;
                    ParsekLog.Warn(Tag,
                        $"OnSave: skipped inert tree id={entry.TreeId} (generation={entry.Generation}): a " +
                        "committed tree with the same id is written instead");
                    continue;
                }
                scenarioNode.AddNode(entry.Node.CreateCopy());
                written++;
            }

            ParsekLog.Info(Tag,
                $"OnSave: re-emitted {written.ToString(CultureInfo.InvariantCulture)} inert RECORDING_TREE " +
                $"node(s) verbatim (skipped={skipped.ToString(CultureInfo.InvariantCulture)})");
            return written;
        }

        // ---------------------------------------------------------------- known ids

        internal static void AddRecordingIdsTo(ICollection<string> target)
        {
            if (target == null)
                return;
            foreach (string id in recordingIds)
                target.Add(id);
        }

        internal static void AddTreeIdsTo(ICollection<string> target)
        {
            if (target == null)
                return;
            for (int i = 0; i < entries.Count; i++)
            {
                string id = entries[i]?.TreeId;
                if (!string.IsNullOrEmpty(id) && !target.Contains(id))
                    target.Add(id);
            }
        }

        internal static void AddGroupNamesTo(ICollection<string> target)
        {
            if (target == null)
                return;
            for (int i = 0; i < entries.Count; i++)
            {
                List<string> groups = entries[i]?.GroupNames;
                if (groups == null)
                    continue;
                for (int g = 0; g < groups.Count; g++)
                {
                    if (!target.Contains(groups[g]))
                        target.Add(groups[g]);
                }
            }
        }

        /// <summary>
        /// Adds what the inert nodes keep alive under <c>Parsek/</c> to the file collector's memory
        /// reference set, with the same keys <see cref="SaveReferenceScanner"/> reads from a save
        /// file: each RECORDING's id and content hashes, and every <c>rewindSave</c> /
        /// <c>resumeRewindSave</c> value at any depth.
        /// </summary>
        internal static void AddReferencesTo(SaveReferences refs)
        {
            if (refs == null)
                return;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i]?.Node != null)
                    AddNodeReferences(refs, entries[i].Node);
            }
        }

        private static void AddNodeReferences(SaveReferences refs, ConfigNode node)
        {
            refs.AddRewindSave(node.GetValue("rewindSave"));
            refs.AddRewindSave(node.GetValue("resumeRewindSave"));
            if (string.Equals(node.name, "RECORDING", StringComparison.Ordinal))
            {
                refs.AddRecording(
                    node.GetValue("recordingId"),
                    node.GetValue("precHash"),
                    node.GetValue("vesselHash"),
                    node.GetValue("ghostHash"));
            }
            foreach (ConfigNode child in node.GetNodes())
                AddNodeReferences(refs, child);
        }

        // ---------------------------------------------------------------- notice

        /// <summary>
        /// Pure: the one-shot screen message for a load that set <paramref name="count"/> trees
        /// aside, or null for none. ParsekLog prefixes "[Parsek] ".
        /// </summary>
        internal static string BuildLoadNotice(int count)
        {
            if (count <= 0)
                return null;
            if (count == 1)
                return "1 recorded mission was made by a different Parsek version. "
                    + "It is kept unchanged in this save but not loaded.";
            return count.ToString(CultureInfo.InvariantCulture)
                + " recorded missions were made by a different Parsek version. "
                + "They are kept unchanged in this save but not loaded.";
        }

        /// <summary>
        /// The load notice for the trees the last <c>LoadRecordingTrees</c> set aside, at most once
        /// per load (a second call returns null), or null when none.
        /// </summary>
        internal static string TakeLoadNotice()
        {
            if (loadNoticeTaken)
                return null;
            string notice = BuildLoadNotice(entries.Count);
            if (notice == null)
                return null;
            loadNoticeTaken = true;
            ParsekLog.Info(Tag,
                $"Load notice queued: {entries.Count.ToString(CultureInfo.InvariantCulture)} inert tree(s)");
            return notice;
        }

        // ---------------------------------------------------------------- reset

        /// <summary>Cold-load session reset (the save being loaded provides the inert trees).</summary>
        internal static void ResetForLoad(string reason)
        {
            int previous = entries.Count;
            Clear();
            if (previous > 0)
                ParsekLog.Verbose(Tag,
                    $"Reset ({reason ?? "<none>"}): dropped {previous.ToString(CultureInfo.InvariantCulture)} " +
                    "inert tree(s) held for the previous save");
        }

        internal static void ResetForTesting()
        {
            Clear();
        }

        private static void Clear()
        {
            entries.Clear();
            recordingIds.Clear();
            treeIds.Clear();
            loadNoticeTaken = false;
        }
    }
}
