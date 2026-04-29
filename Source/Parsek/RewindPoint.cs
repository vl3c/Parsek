using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// A speculative quicksave captured at a multi-controllable split (design
    /// doc section 5.1). Created unconditionally at every multi-controllable
    /// split and reaped at session merge if no slot's effective recording
    /// qualifies as an Unfinished Flight; otherwise persists until all slots
    /// resolve.
    ///
    /// The persisted quicksave file lives at
    /// <c>saves/&lt;save&gt;/Parsek/RewindPoints/&lt;RewindPointId&gt;.sfs</c>.
    ///
    /// <para>
    /// Strip matching (design section 6.4) consults <see cref="PidSlotMap"/>
    /// first (by <c>Vessel.persistentId</c>); if absent, falls back to
    /// <see cref="RootPartPidMap"/> keyed by root part <c>Part.persistentId</c>
    /// (the stable cross-save/load identity; <c>Part.flightID</c> is
    /// session-scoped and unstable).
    /// </para>
    /// </summary>
    public class RewindPoint
    {
        /// <summary>Stable id; format <c>rp_&lt;Guid-N&gt;</c>. Must satisfy <see cref="RecordingPaths.ValidateRecordingId"/>.</summary>
        public string RewindPointId;

        /// <summary>Weak link to the <see cref="BranchPoint"/> that produced the split.</summary>
        public string BranchPointId;

        /// <summary>Planetarium UT at which the quicksave was written.</summary>
        public double UT;

        /// <summary>Filename (relative, under <c>Parsek/RewindPoints/</c>) of the .sfs quicksave.</summary>
        public string QuicksaveFilename;

        /// <summary>Per-controllable-sibling slot metadata.</summary>
        public List<ChildSlot> ChildSlots = new List<ChildSlot>();

        /// <summary>
        /// Zero-based list index into <see cref="ChildSlots"/> for the vessel
        /// that was focused when the split was captured. -1 means no focus
        /// signal was available, including legacy saves.
        /// </summary>
        public int FocusSlotIndex = -1;

        /// <summary>
        /// <c>Vessel.persistentId</c> -> <see cref="ChildSlot.SlotIndex"/>. Primary
        /// lookup at post-load strip time. Populated on the deferred frame after
        /// the quicksave write.
        /// </summary>
        public Dictionary<uint, int> PidSlotMap = new Dictionary<uint, int>();

        /// <summary>
        /// Root part <c>Part.persistentId</c> -> <see cref="ChildSlot.SlotIndex"/>.
        /// Fallback for vessels whose vessel-level persistentId was reassigned
        /// between save and load.
        /// </summary>
        public Dictionary<uint, int> RootPartPidMap = new Dictionary<uint, int>();

        /// <summary>
        /// True while the active re-fly session owns the RP. Promoted to
        /// persistent at session merge (design section 6.6 step 7); purged on
        /// session discard.
        /// </summary>
        public bool SessionProvisional = true;

        /// <summary>True when quicksave validation failed (e.g. bad file on disk).</summary>
        public bool Corrupted;

        /// <summary>Session GUID that created the RP (null when not session-provisional).</summary>
        public string CreatingSessionId;

        /// <summary>Wall-clock timestamp at RP creation (ISO 8601 UTC string; design §5.1).</summary>
        public string CreatedRealTime;

        private const string NodeName = "POINT";

        /// <summary>Appends a <c>POINT</c> child node to the given parent.</summary>
        public void SaveInto(ConfigNode parent)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            var ic = CultureInfo.InvariantCulture;
            ConfigNode node = parent.AddNode(NodeName);
            node.AddValue("rewindPointId", RewindPointId ?? "");
            node.AddValue("branchPointId", BranchPointId ?? "");
            node.AddValue("ut", UT.ToString("R", ic));
            node.AddValue("quicksaveFilename", QuicksaveFilename ?? "");
            node.AddValue("sessionProvisional", SessionProvisional.ToString());
            if (Corrupted)
                node.AddValue("corrupted", Corrupted.ToString());
            if (!string.IsNullOrEmpty(CreatingSessionId))
                node.AddValue("creatingSessionId", CreatingSessionId);
            if (!string.IsNullOrEmpty(CreatedRealTime))
                node.AddValue("createdRealTime", CreatedRealTime);
            if (FocusSlotIndex != -1)
                node.AddValue("focusSlotIndex", FocusSlotIndex.ToString(ic));

            if (ChildSlots != null)
            {
                for (int i = 0; i < ChildSlots.Count; i++)
                    ChildSlots[i]?.SaveInto(node);
            }

            if (PidSlotMap != null)
            {
                foreach (var kv in PidSlotMap)
                {
                    var entry = node.AddNode("PID_SLOT_MAP");
                    entry.AddValue("pid", kv.Key.ToString(ic));
                    entry.AddValue("slot", kv.Value.ToString(ic));
                }
            }

            if (RootPartPidMap != null)
            {
                foreach (var kv in RootPartPidMap)
                {
                    var entry = node.AddNode("ROOT_PART_PID_MAP");
                    entry.AddValue("pid", kv.Key.ToString(ic));
                    entry.AddValue("slot", kv.Value.ToString(ic));
                }
            }
        }

        /// <summary>Loads a single <c>POINT</c> ConfigNode into a new RewindPoint.</summary>
        public static RewindPoint LoadFrom(ConfigNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            var ic = CultureInfo.InvariantCulture;
            var rp = new RewindPoint();

            string rpId = node.GetValue("rewindPointId");
            rp.RewindPointId = string.IsNullOrEmpty(rpId) ? null : rpId;

            string bpId = node.GetValue("branchPointId");
            rp.BranchPointId = string.IsNullOrEmpty(bpId) ? null : bpId;

            string utStr = node.GetValue("ut");
            double ut;
            if (!string.IsNullOrEmpty(utStr) && double.TryParse(utStr, NumberStyles.Float, ic, out ut))
                rp.UT = ut;

            string qsName = node.GetValue("quicksaveFilename");
            rp.QuicksaveFilename = string.IsNullOrEmpty(qsName) ? null : qsName;

            string sessionProvStr = node.GetValue("sessionProvisional");
            bool sessionProv;
            if (!string.IsNullOrEmpty(sessionProvStr) && bool.TryParse(sessionProvStr, out sessionProv))
                rp.SessionProvisional = sessionProv;

            string corruptedStr = node.GetValue("corrupted");
            bool corrupted;
            if (!string.IsNullOrEmpty(corruptedStr) && bool.TryParse(corruptedStr, out corrupted))
                rp.Corrupted = corrupted;

            rp.CreatingSessionId = node.GetValue("creatingSessionId");
            rp.CreatedRealTime = node.GetValue("createdRealTime");

            string focusSlotIndexStr = node.GetValue("focusSlotIndex");
            int focusSlotIndex;
            if (!string.IsNullOrEmpty(focusSlotIndexStr)
                && int.TryParse(focusSlotIndexStr, NumberStyles.Integer, ic, out focusSlotIndex))
            {
                rp.FocusSlotIndex = focusSlotIndex;
            }

            ConfigNode[] slotNodes = node.GetNodes("CHILD_SLOT");
            rp.ChildSlots = new List<ChildSlot>(slotNodes.Length);
            for (int i = 0; i < slotNodes.Length; i++)
                rp.ChildSlots.Add(ChildSlot.LoadFrom(slotNodes[i]));

            rp.PidSlotMap = new Dictionary<uint, int>();
            ConfigNode[] pidNodes = node.GetNodes("PID_SLOT_MAP");
            for (int i = 0; i < pidNodes.Length; i++)
            {
                uint pid;
                int slot;
                if (uint.TryParse(pidNodes[i].GetValue("pid"), NumberStyles.Integer, ic, out pid)
                    && int.TryParse(pidNodes[i].GetValue("slot"), NumberStyles.Integer, ic, out slot))
                {
                    rp.PidSlotMap[pid] = slot;
                }
            }

            rp.RootPartPidMap = new Dictionary<uint, int>();
            ConfigNode[] rootNodes = node.GetNodes("ROOT_PART_PID_MAP");
            for (int i = 0; i < rootNodes.Length; i++)
            {
                uint pid;
                int slot;
                if (uint.TryParse(rootNodes[i].GetValue("pid"), NumberStyles.Integer, ic, out pid)
                    && int.TryParse(rootNodes[i].GetValue("slot"), NumberStyles.Integer, ic, out slot))
                {
                    rp.RootPartPidMap[pid] = slot;
                }
            }

            return rp;
        }
    }
}
