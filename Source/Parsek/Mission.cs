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

        // Mission-level loop configuration (Phase B). Persisted but INERT for now: the
        // looping playback that consumes these is wired in a later phase. Single-selection
        // is enforced through MissionStore.SetLoopEnabled (at most one Mission loops at a
        // time). LoopIntervalSeconds is the launch-to-launch period in seconds; the
        // default is the same "untouched" sentinel the per-recording codec uses. Unlike
        // the per-recording codec (which drops the unit), the Mission persists its display
        // unit explicitly so it reads back as set.
        public bool LoopPlayback;
        public double LoopIntervalSeconds = LoopTiming.UntouchedLoopIntervalSentinel;
        public LoopTimeUnit LoopTimeUnit = LoopTimeUnit.Sec;

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
            copy.LoopPlayback = LoopPlayback;
            copy.LoopIntervalSeconds = LoopIntervalSeconds;
            copy.LoopTimeUnit = LoopTimeUnit;
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
            foreach (string h in ExcludedThroughLineHeadIds)
                node.AddValue("excludedHead", h ?? "");
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

            string[] heads = node.GetValues("excludedHead");
            for (int i = 0; i < heads.Length; i++)
                if (!string.IsNullOrEmpty(heads[i]))
                    m.ExcludedThroughLineHeadIds.Add(heads[i]);
            return m;
        }
    }
}
