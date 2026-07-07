using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // A Mission is a saved, named selection over one mission tree (layer 5 of
    // docs/dev/design-mission-abstractions.md). Multiple Missions may target the same
    // tree with different selections. The selection is stored as the set of EXCLUDED
    // through-line head ids (a through-line head is a leg RecordingId); an empty set
    // means "everything included" (the default Mission). Unchecking a through-line in
    // the UI adds its head id here, which drops it and its offshoots from the mission.
    internal sealed class Mission
    {
        public string Id;
        public string TreeId;
        public string Name;
        public readonly HashSet<string> ExcludedThroughLineHeadIds = new HashSet<string>();

        // Interval-level start/end trim selection (finer than ExcludedThroughLineHeadIds): the
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

        // Archived = removed from the Missions window list when the window's "Archive" toggle is
        // on (purely a list-management flag for long lists, mirroring a recording's Hidden flag).
        // It does not change looping or ghost playback - a still-looping archived mission keeps
        // looping; un-archive (or turn the Archive toggle off) to see it again.
        public bool Archived;

        // M-MIS-5 (D3): the interval-key SCHEMA GENERATION this selection was last authored /
        // reconciled under. 0 = pre-M-MIS-5 (dock edges did not exist, so an excluded structural
        // key covered the WHOLE structural interval including any docked stretch); 1 = current
        // (dock/board sub-intervals key as "<parentKey>@dockM" and are selected independently).
        // MissionStore.ReconcileSelections extends a generation-0 mission's excluded keys across
        // their @dock sub-siblings once (semantics-preserving) and stamps EVERY tree-resolvable
        // mission to the current generation. The field initializer covers every creation path
        // (ctor, EnsureDefaultsForTrees, UI creates); Clone copies the source's generation; ONLY
        // Load with the key absent yields 0.
        public int SelectionSchemaGeneration = CurrentSelectionSchemaGeneration;
        internal const int CurrentSelectionSchemaGeneration = 1;

        public Mission() { }

        public Mission(string id, string treeId, string name)
        {
            Id = id;
            TreeId = treeId;
            Name = name;
        }

        // Duplicates the definition (same tree + selection), not any recording data.
        public Mission Clone(string newId)
        {
            var copy = new Mission(newId, TreeId, (Name ?? "Mission") + " copy");
            foreach (string h in ExcludedThroughLineHeadIds)
                copy.ExcludedThroughLineHeadIds.Add(h);
            foreach (string k in ExcludedIntervalKeys)
                copy.ExcludedIntervalKeys.Add(k);
            foreach (string l in IncludedForeignDockLinkIds)
                copy.IncludedForeignDockLinkIds.Add(l);
            copy.LoopPlayback = LoopPlayback;
            copy.LoopIntervalSeconds = LoopIntervalSeconds;
            copy.LoopTimeUnit = LoopTimeUnit;
            copy.LoopAnchorUT = LoopAnchorUT;
            copy.Archived = Archived;
            // The clone's selection was authored under the SAME schema generation as its source
            // (a not-yet-reconciled generation-0 copy must still receive the @dock extension).
            copy.SelectionSchemaGeneration = SelectionSchemaGeneration;
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
            node.AddValue("archived", Archived);
            node.AddValue("selectionSchemaGeneration",
                SelectionSchemaGeneration.ToString(CultureInfo.InvariantCulture));
            foreach (string h in ExcludedThroughLineHeadIds)
                node.AddValue("excludedHead", h ?? "");
            foreach (string k in ExcludedIntervalKeys)
                node.AddValue("excludedInterval", k ?? "");
            // M-MIS-8: SPARSE - written only when a link is included, so every pre-existing
            // (link-free) mission's save output is byte-identical to pre-feature builds.
            foreach (string l in IncludedForeignDockLinkIds)
                if (!string.IsNullOrEmpty(l))
                    node.AddValue("foreignDockLink", l);
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
            if (bool.TryParse(node.GetValue("archived"), out bool archived))
                m.Archived = archived;

            // M-MIS-5 (D3): the ONLY path that yields generation 0 - a save written before the
            // key existed (or a malformed value). The field initializer defaults every freshly
            // constructed Mission to the current generation.
            m.SelectionSchemaGeneration =
                int.TryParse(node.GetValue("selectionSchemaGeneration"),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out int schemaGen)
                ? schemaGen
                : 0;

            string[] heads = node.GetValues("excludedHead");
            for (int i = 0; i < heads.Length; i++)
                if (!string.IsNullOrEmpty(heads[i]))
                    m.ExcludedThroughLineHeadIds.Add(heads[i]);
            string[] intervals = node.GetValues("excludedInterval");
            for (int i = 0; i < intervals.Length; i++)
                if (!string.IsNullOrEmpty(intervals[i]))
                    m.ExcludedIntervalKeys.Add(intervals[i]);
            string[] foreignLinks = node.GetValues("foreignDockLink");
            for (int i = 0; i < foreignLinks.Length; i++)
                if (!string.IsNullOrEmpty(foreignLinks[i]))
                    m.IncludedForeignDockLinkIds.Add(foreignLinks[i]);
            return m;
        }
    }
}
