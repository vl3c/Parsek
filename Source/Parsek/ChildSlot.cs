using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// A controllable slot captured by a <see cref="RewindPoint"/> at a
    /// multi-controllable split (design doc section 5.2).
    ///
    /// The slot carries the origin recording id that was spawned at split time;
    /// <see cref="EffectiveRecordingId"/> walks forward through
    /// <see cref="RecordingSupersedeRelation"/> entries to find the currently
    /// effective recording for this slot.
    /// </summary>
    public class ChildSlot
    {
        /// <summary>Zero-based position within the parent <see cref="RewindPoint.ChildSlots"/>.</summary>
        public int SlotIndex;

        /// <summary>Immutable: the recording originally created for this slot at split time.</summary>
        public string OriginChildRecordingId;

        /// <summary>
        /// True if the slot is considered controllable (has a command module or is an
        /// EVA kerbal). The slot is always "Controllable" in v1 because only controllable
        /// entities produce child slots; the field is reserved for future classifier churn.
        /// </summary>
        public bool Controllable = true;

        /// <summary>
        /// True if the Rewind button for this slot must be grayed out. Set when the
        /// split-time PidSlotMap lookup fails for this slot (design section 6.1 partial
        /// map failure) or when loader sanity checks fail at invocation time.
        /// </summary>
        public bool Disabled;

        /// <summary>
        /// Human-readable disable reason shown in the UI tooltip (null when
        /// <see cref="Disabled"/> is false).
        /// </summary>
        public string DisabledReason;

        private const string NodeName = "CHILD_SLOT";

        /// <summary>Appends a <c>CHILD_SLOT</c> child node to the given parent ConfigNode.</summary>
        public void SaveInto(ConfigNode parent)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            var ic = CultureInfo.InvariantCulture;
            ConfigNode node = parent.AddNode(NodeName);
            node.AddValue("slotIndex", SlotIndex.ToString(ic));
            node.AddValue("originChildRecordingId", OriginChildRecordingId ?? "");
            node.AddValue("controllable", Controllable.ToString());
            if (Disabled)
                node.AddValue("disabled", Disabled.ToString());
            if (!string.IsNullOrEmpty(DisabledReason))
                node.AddValue("disabledReason", DisabledReason);
        }

        /// <summary>Loads a single <c>CHILD_SLOT</c> ConfigNode into a new <see cref="ChildSlot"/>.</summary>
        public static ChildSlot LoadFrom(ConfigNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            var ic = CultureInfo.InvariantCulture;
            var slot = new ChildSlot();

            string slotIndexStr = node.GetValue("slotIndex");
            int slotIndex;
            if (int.TryParse(slotIndexStr, NumberStyles.Integer, ic, out slotIndex))
                slot.SlotIndex = slotIndex;

            string origin = node.GetValue("originChildRecordingId");
            slot.OriginChildRecordingId = string.IsNullOrEmpty(origin) ? null : origin;

            string controllableStr = node.GetValue("controllable");
            bool controllable;
            if (!string.IsNullOrEmpty(controllableStr) && bool.TryParse(controllableStr, out controllable))
                slot.Controllable = controllable;

            string disabledStr = node.GetValue("disabled");
            bool disabled;
            if (!string.IsNullOrEmpty(disabledStr) && bool.TryParse(disabledStr, out disabled))
                slot.Disabled = disabled;

            slot.DisabledReason = node.GetValue("disabledReason");

            return slot;
        }

        /// <summary>
        /// Walks forward through <paramref name="supersedes"/> starting at
        /// <see cref="OriginChildRecordingId"/>. For each current id, looks for a
        /// supersede relation with <c>OldRecordingId == current</c>; if found,
        /// advances to <c>NewRecordingId</c>. Returns the last id reached when no
        /// relation matches (the "orphan endpoint" in design section 5.2: if
        /// <c>NewRecordingId</c> is not the Old of another relation, the walk
        /// terminates there — that id IS the effective id).
        ///
        /// Cycle guard via visited HashSet: A -> B -> A logs Warn and returns the
        /// last-visited id. Null/empty origin returns null.
        /// </summary>
        public string EffectiveRecordingId(IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            if (string.IsNullOrEmpty(OriginChildRecordingId))
                return null;

            // No relations: chain length 0; origin is already effective.
            if (supersedes == null || supersedes.Count == 0)
                return OriginChildRecordingId;

            var visited = new HashSet<string>(StringComparer.Ordinal);
            string current = OriginChildRecordingId;
            visited.Add(current);

            while (true)
            {
                string next = null;
                for (int i = 0; i < supersedes.Count; i++)
                {
                    var rel = supersedes[i];
                    if (rel == null) continue;
                    if (string.Equals(rel.OldRecordingId, current, StringComparison.Ordinal))
                    {
                        next = rel.NewRecordingId;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(next))
                    return current; // orphan endpoint / chain terminus

                if (visited.Contains(next))
                {
                    ParsekLog.Warn("Supersede",
                        $"EffectiveRecordingId: cycle detected starting from origin={OriginChildRecordingId} " +
                        $"at current={current} next={next}; returning last-visited={current}");
                    return current;
                }

                visited.Add(next);
                current = next;
            }
        }
    }
}
