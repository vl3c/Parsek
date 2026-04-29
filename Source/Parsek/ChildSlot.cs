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

        /// <summary>
        /// True when the player explicitly closed this slot from the
        /// Unfinished Flights row. Sealed slots no longer qualify for
        /// Unfinished Flights and count as closed for RP reap eligibility
        /// once their effective recording has committed.
        /// </summary>
        public bool Sealed;

        /// <summary>
        /// ISO-8601 UTC timestamp for the Seal action. Diagnostic only;
        /// null when <see cref="Sealed"/> is false.
        /// </summary>
        public string SealedRealTime;

        /// <summary>
        /// True when the player explicitly stashed a default-excluded stable
        /// slot into Unfinished Flights. Stashed slots stay re-flyable until
        /// the player Seals them; structural close-outs such as downstream
        /// branch points and boarded EVAs still use the classifier's normal
        /// closed-path rules.
        /// </summary>
        public bool Stashed;

        /// <summary>
        /// ISO-8601 UTC timestamp for the Stash action. Diagnostic only;
        /// null when <see cref="Stashed"/> is false.
        /// </summary>
        public string StashedRealTime;

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
            if (Sealed)
                node.AddValue("sealed", Sealed.ToString());
            if (!string.IsNullOrEmpty(SealedRealTime))
                node.AddValue("sealedRealTime", SealedRealTime);
            if (Stashed)
                node.AddValue("stashed", Stashed.ToString());
            if (!string.IsNullOrEmpty(StashedRealTime))
                node.AddValue("stashedRealTime", StashedRealTime);
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

            string sealedStr = node.GetValue("sealed");
            bool sealedValue;
            if (!string.IsNullOrEmpty(sealedStr) && bool.TryParse(sealedStr, out sealedValue))
                slot.Sealed = sealedValue;

            slot.SealedRealTime = node.GetValue("sealedRealTime");

            string stashedStr = node.GetValue("stashed");
            bool stashedValue;
            if (!string.IsNullOrEmpty(stashedStr) && bool.TryParse(stashedStr, out stashedValue))
                slot.Stashed = stashedValue;

            slot.StashedRealTime = node.GetValue("stashedRealTime");

            return slot;
        }

        /// <summary>
        /// Walks forward through <paramref name="supersedes"/> starting at
        /// <see cref="OriginChildRecordingId"/>. Delegates to
        /// <see cref="EffectiveState.EffectiveRecordingId"/> so the walk logic
        /// lives in one place (design section 5.2 / Phase 2 consolidation).
        /// </summary>
        public string EffectiveRecordingId(IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            return EffectiveState.EffectiveRecordingId(OriginChildRecordingId, supersedes);
        }
    }
}
