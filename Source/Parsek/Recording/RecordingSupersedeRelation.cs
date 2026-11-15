using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Append-only supersede relation linking an old recording to a new one
    /// (design doc section 5.3). Written at session-merge time; normally
    /// removed by whole-tree discard. Load-time cleanup also removes fully
    /// orphaned rows where neither endpoint exists, because they cannot affect
    /// effective recording resolution.
    ///
    /// Persisted as <c>ENTRY</c> children of the scenario's
    /// <c>RECORDING_SUPERSEDES</c> node. One-sided orphan relations are left in
    /// place with a Warn log; the forward walk in
    /// <see cref="ChildSlot.EffectiveRecordingId"/> handles them as
    /// terminators.
    /// </summary>
    public class RecordingSupersedeRelation
    {
        /// <summary>Stable id; format <c>rsr_&lt;Guid-N&gt;</c>.</summary>
        public string RelationId;

        /// <summary>Superseded recording (stays in the store, hidden from ERS).</summary>
        public string OldRecordingId;

        /// <summary>Superseding recording (the re-fly's provisional promoted on merge).</summary>
        public string NewRecordingId;

        /// <summary>Planetarium UT at which the merge occurred.</summary>
        public double UT;

        /// <summary>
        /// Wall-clock timestamp as ISO 8601 UTC string. Stored as string so tests
        /// and save files round-trip without DateTime locale hazards.
        /// </summary>
        public string CreatedRealTime;

        /// <summary>Appends an <c>ENTRY</c> child node to the given parent.</summary>
        public void SaveInto(ConfigNode parent)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            var ic = CultureInfo.InvariantCulture;
            ConfigNode node = parent.AddNode("ENTRY");
            node.AddValue("relationId", RelationId ?? "");
            node.AddValue("oldRecordingId", OldRecordingId ?? "");
            node.AddValue("newRecordingId", NewRecordingId ?? "");
            node.AddValue("ut", UT.ToString("R", ic));
            if (!string.IsNullOrEmpty(CreatedRealTime))
                node.AddValue("createdRealTime", CreatedRealTime);
        }

        /// <summary>Loads a single <c>ENTRY</c> ConfigNode.</summary>
        public static RecordingSupersedeRelation LoadFrom(ConfigNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            var ic = CultureInfo.InvariantCulture;
            var rel = new RecordingSupersedeRelation();
            string relId = node.GetValue("relationId");
            rel.RelationId = string.IsNullOrEmpty(relId) ? null : relId;
            string oldId = node.GetValue("oldRecordingId");
            rel.OldRecordingId = string.IsNullOrEmpty(oldId) ? null : oldId;
            string newId = node.GetValue("newRecordingId");
            rel.NewRecordingId = string.IsNullOrEmpty(newId) ? null : newId;

            string utStr = node.GetValue("ut");
            double ut;
            if (!string.IsNullOrEmpty(utStr) && double.TryParse(utStr, NumberStyles.Float, ic, out ut))
                rel.UT = ut;

            rel.CreatedRealTime = node.GetValue("createdRealTime");
            return rel;
        }
    }
}
