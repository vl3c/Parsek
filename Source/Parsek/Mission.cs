using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // A Mission is a saved, named selection over one mission tree (layer 5 of
    // docs/dev/design-mission-abstractions.md). Multiple Missions may target the same
    // tree with different selections. The selection is stored as the set of EXCLUDED
    // composition-interval keys (ExcludedIntervalKeys below) plus the included cross-tree
    // dock links; empty sets mean "everything included" (the default Mission).
    internal sealed class Mission
    {
        public string Id;
        public string TreeId;
        public string Name;
        // Interval-level start/end trim selection: the
        // set of EXCLUDED composition-interval keys (a MissionCompositionNode.HeadLegId). Dropping
        // a vessel's leading interval start-trims it (e.g. show the pod only after the decouple,
        // not the launch); dropping a peeled branch's interval drops that branch. Empty = nothing
        // trimmed. Consumed by MissionIntervalSelection to derive per-vessel render windows.
        // M-MIS-8: when a foreign dock link is included (IncludedForeignDockLinkIds), this set
        // also holds excluded FOREIGN partner-journey interval keys - the keys are
        // recording-GUID-rooted, hence globally unique across trees, so one set serves both
        // sides of the seam.
        public readonly HashSet<string> ExcludedIntervalKeys = new HashSet<string>();

        // M-MIS-8 (design: docs/dev/design-mission-crosstree-dock.md): the set of INCLUDED
        // cross-tree foreign dock links - each id is the claiming Dock/Board BranchPoint's GUID
        // in the FOREIGN (controller's) tree whose TargetVesselPersistentId matches a vessel of
        // this mission's tree. Including a link pulls the derived PARTNER JOURNEY (the docked
        // stretch + the partner's post-undock offshoot in the foreign tree) into this mission's
        // selection and loop unit. Everything about the link beyond its id is DERIVED live via
        // PID + launch-guid matching (MissionCrossTreeDock, walker parity). Default OFF (empty);
        // the codec writes the key only when non-empty so pre-existing missions round-trip
        // byte-identically.
        public readonly HashSet<string> IncludedForeignDockLinkIds = new HashSet<string>();

        // Mission-level loop configuration. Multiple Missions may loop concurrently, but at
        // most one per recording tree: MissionStore.SetLoopEnabled clears only looping
        // siblings that share this Mission's TreeId (same-tree variants share trunk legs, so
        // their committed indices overlap; different trees are disjoint and loop together).
        // LoopIntervalSeconds is the launch-to-launch period in seconds; the default is the
        // same "untouched" sentinel the per-recording codec uses. Unlike the per-recording
        // codec (which drops the unit), the Mission persists its display unit explicitly so
        // it reads back as set.
        public bool LoopPlayback;
        public double LoopIntervalSeconds = LoopTiming.UntouchedLoopIntervalSentinel;
        public LoopTimeUnit LoopTimeUnit = LoopTimeUnit.Sec;

        // The UT the loop was last enabled at (NaN = unset). The span clock phases relative
        // to this anchor (elapsed = currentUT - LoopAnchorUT) so re-enabling the loop restarts
        // playback from the recording's start instead of resuming mid-mission.
        public double LoopAnchorUT = double.NaN;

        // Collapsed = the Missions tab draws only this mission's two-line bar (title, values,
        // summary, buttons) and hides the rows below it (vessel, interval detail, chapter and
        // Docked partner rows). Missions-tab view state only: it does not change looping or ghost
        // playback. Saved as "collapsed".
        public bool Collapsed;

        // The interval-key schema generation written with every mission ("selectionSchemaGeneration"):
        // dock/board sub-intervals key as "<parentKey>@dockM" and are selected independently.
        // A newer value is warned once per load (MissionStore.Load) and read best effort: a
        // mission is presentation state over its tree.
        internal const int CurrentSelectionSchemaGeneration = 1;

        // Values and child nodes of the MISSION node this build does not know (a newer build's
        // keys), written back verbatim after the known keys so an older build's save loses none.
        public readonly List<KeyValuePair<string, string>> UnknownValues = new List<KeyValuePair<string, string>>();
        public readonly List<ConfigNode> UnknownNodes = new List<ConfigNode>();

        // A selectionSchemaGeneration above CurrentSelectionSchemaGeneration as loaded, written back
        // instead of the current one so the newer build still reads its own keys under its rules.
        public int? NewerSelectionSchemaGeneration;

        internal static readonly HashSet<string> KnownKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "treeId", "name", "loopPlayback", "loopIntervalSeconds", "loopTimeUnit", "loopAnchorUT",
            "collapsed", "selectionSchemaGeneration", "excludedInterval", "foreignDockLink",
            // Retired 2026-11-06 (todo GUI-D6): the coarse per-through-line exclusion nothing ever
            // wrote. Listed so an old save's value is consumed and dropped on the next save rather
            // than preserved verbatim as an unknown key; nothing reads it.
            "excludedHead",
        };

        public Mission() { }

        public Mission(string id, string treeId, string name)
        {
            Id = id;
            TreeId = treeId;
            Name = name;
        }

        // Duplicates the definition (same tree + selection), not any recording data. A faithful
        // copy including LoopPlayback; MissionStore.Clone (the only path that inserts a copy
        // into the store) disarms the copy's loop to keep one loop per tree.
        public Mission Clone(string newId)
        {
            var copy = new Mission(newId, TreeId, (Name ?? "Mission") + " copy");
            foreach (string k in ExcludedIntervalKeys)
                copy.ExcludedIntervalKeys.Add(k);
            foreach (string l in IncludedForeignDockLinkIds)
                copy.IncludedForeignDockLinkIds.Add(l);
            copy.LoopPlayback = LoopPlayback;
            copy.LoopIntervalSeconds = LoopIntervalSeconds;
            copy.LoopTimeUnit = LoopTimeUnit;
            copy.LoopAnchorUT = LoopAnchorUT;
            copy.Collapsed = Collapsed;
            return copy;
        }

        public void Save(ConfigNode node)
        {
            node.AddValue("id", Id ?? "");
            node.AddValue("treeId", TreeId ?? "");
            node.AddValue("name", Name ?? "");
            node.AddValue("loopPlayback", LoopPlayback);
            node.AddValue("loopIntervalSeconds",
                LoopIntervalSeconds.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("loopTimeUnit", LoopTimeUnit.ToString());
            node.AddValue("loopAnchorUT",
                LoopAnchorUT.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("collapsed", Collapsed);
            node.AddValue("selectionSchemaGeneration",
                (NewerSelectionSchemaGeneration ?? CurrentSelectionSchemaGeneration).ToString(CultureInfo.InvariantCulture));
            foreach (string k in ExcludedIntervalKeys)
                node.AddValue("excludedInterval", k ?? "");
            // M-MIS-8: SPARSE - written only when a link is included, so every pre-existing
            // (link-free) mission's save output is byte-identical to pre-feature builds.
            // Sorted: HashSet enumeration order is nondeterministic, and an unsorted write
            // would churn the save bytes of a LINKED mission across sessions with no logical
            // change.
            if (IncludedForeignDockLinkIds.Count > 0)
            {
                var links = new List<string>(IncludedForeignDockLinkIds);
                links.Sort(StringComparer.Ordinal);
                for (int i = 0; i < links.Count; i++)
                    if (!string.IsNullOrEmpty(links[i]))
                        node.AddValue("foreignDockLink", links[i]);
            }
            for (int i = 0; i < UnknownValues.Count; i++)
                node.AddValue(UnknownValues[i].Key, UnknownValues[i].Value);
            for (int i = 0; i < UnknownNodes.Count; i++)
                node.AddNode(UnknownNodes[i].CreateCopy());
        }

        public static Mission Load(ConfigNode node)
        {
            var m = new Mission
            {
                Id = node.GetValue("id"),
                TreeId = node.GetValue("treeId") ?? "",
                Name = Recording.ResolveLocalizedName(node.GetValue("name") ?? "")
            };
            if (string.IsNullOrEmpty(m.Id))
                m.Id = Guid.NewGuid().ToString("N");

            // Loop fields: parse each with a safe fallback that keeps the field default
            // when the value is missing or malformed (older saves, hand edits).
            if (bool.TryParse(node.GetValue("loopPlayback"), out bool loopPlayback))
                m.LoopPlayback = loopPlayback;
            if (double.TryParse(node.GetValue("loopIntervalSeconds"),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double loopInterval))
                m.LoopIntervalSeconds = loopInterval;
            if (Enum.TryParse(node.GetValue("loopTimeUnit"), out LoopTimeUnit loopUnit))
                m.LoopTimeUnit = loopUnit;
            // Keep the NaN (unset) default when the value is missing or malformed.
            if (double.TryParse(node.GetValue("loopAnchorUT"),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double loopAnchor))
                m.LoopAnchorUT = loopAnchor;
            if (bool.TryParse(node.GetValue("collapsed"), out bool collapsed))
                m.Collapsed = collapsed;

            string[] intervals = node.GetValues("excludedInterval");
            for (int i = 0; i < intervals.Length; i++)
                if (!string.IsNullOrEmpty(intervals[i]))
                    m.ExcludedIntervalKeys.Add(intervals[i]);
            string[] foreignLinks = node.GetValues("foreignDockLink");
            for (int i = 0; i < foreignLinks.Length; i++)
                if (!string.IsNullOrEmpty(foreignLinks[i]))
                    m.IncludedForeignDockLinkIds.Add(foreignLinks[i]);
            int? selectionGeneration = SaveFormat.ParseStored(node.GetValue("selectionSchemaGeneration"));
            if (SaveFormat.MissionSelection.Classify(selectionGeneration) == SaveFormatClass.Newer)
                m.NewerSelectionSchemaGeneration = selectionGeneration;
            for (int i = 0; i < node.values.Count; i++)
            {
                ConfigNode.Value v = node.values[i];
                if (!KnownKeys.Contains(v.name))
                    m.UnknownValues.Add(new KeyValuePair<string, string>(v.name, v.value));
            }
            for (int i = 0; i < node.nodes.Count; i++)
                m.UnknownNodes.Add(node.nodes[i].CreateCopy());
            return m;
        }
    }
}
