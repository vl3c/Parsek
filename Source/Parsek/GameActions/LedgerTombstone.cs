using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Append-only ledger tombstone retiring a <see cref="GameAction"/> from
    /// Effective Ledger Set (ELS) computations (design doc section 5.6 + 6.6).
    /// In v1 the tombstone-eligible type list is narrow (kerbal deaths +
    /// death-scoped reputation bundles); all other action types remain in ELS
    /// even when their owning recording is superseded.
    ///
    /// Multiple tombstones for the same <see cref="ActionId"/> are tolerated —
    /// the ELS filter is "at least one tombstone exists".
    /// </summary>
    public class LedgerTombstone
    {
        /// <summary>Stable id; format <c>tomb_&lt;Guid-N&gt;</c>.</summary>
        public string TombstoneId;

        /// <summary>The superseded action's stable <see cref="GameAction.ActionId"/>.</summary>
        public string ActionId;

        /// <summary>Planetarium UT at which the merge that produced this tombstone occurred.</summary>
        public double UT;

        /// <summary>Short reason tag; defaults to <c>"supersede"</c>.</summary>
        public string Reason = "supersede";

        /// <summary>Wall-clock timestamp (ISO 8601 UTC string).</summary>
        public string CreatedRealTime;

        /// <summary>Appends an <c>ENTRY</c> child node to the given parent.</summary>
        public void SaveInto(ConfigNode parent)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            var ic = CultureInfo.InvariantCulture;
            ConfigNode node = parent.AddNode("ENTRY");
            node.AddValue("tombstoneId", TombstoneId ?? "");
            node.AddValue("actionId", ActionId ?? "");
            node.AddValue("ut", UT.ToString("R", ic));
            node.AddValue("reason", string.IsNullOrEmpty(Reason) ? "supersede" : Reason);
            if (!string.IsNullOrEmpty(CreatedRealTime))
                node.AddValue("createdRealTime", CreatedRealTime);
        }

        /// <summary>Loads a single <c>ENTRY</c> ConfigNode.</summary>
        public static LedgerTombstone LoadFrom(ConfigNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            var ic = CultureInfo.InvariantCulture;
            var t = new LedgerTombstone();

            string tombId = node.GetValue("tombstoneId");
            t.TombstoneId = string.IsNullOrEmpty(tombId) ? null : tombId;

            string actId = node.GetValue("actionId");
            t.ActionId = string.IsNullOrEmpty(actId) ? null : actId;

            string utStr = node.GetValue("ut");
            double ut;
            if (!string.IsNullOrEmpty(utStr) && double.TryParse(utStr, NumberStyles.Float, ic, out ut))
                t.UT = ut;

            string reason = node.GetValue("reason");
            t.Reason = string.IsNullOrEmpty(reason) ? "supersede" : reason;

            t.CreatedRealTime = node.GetValue("createdRealTime");
            return t;
        }
    }
}
