using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Persistent timeline-state row hiding a committed Re-Fly fork that was
    /// rewound out of the active timeline when a prior supersede relation was
    /// rolled back.
    /// </summary>
    public sealed class RecordingRewindRetirement
    {
        public const string DefaultReason = "rewound-out-supersede-fork";

        /// <summary>
        /// Reason value applied when the upstream Pass 2 demotion explicitly
        /// retired an <see cref="MergeState.Immutable"/> canon fork because
        /// its priorTip was itself being retired in the same rewind batch.
        ///
        /// <para>This is distinct from <see cref="DefaultReason"/>: a
        /// retirement carrying this reason is intentional (the canon had no
        /// live source to be canon over after the cascade), and load-time
        /// sweeps must NOT treat it as legacy pre-fix bad state. By contrast,
        /// any pre-fix save that retired an Immutable fork without the Pass-2
        /// classifier ran with <see cref="DefaultReason"/>, so the
        /// load-time sweep can use the reason field to disambiguate the
        /// "remove + reconstruct" cleanup path from the "leave intact" intent
        /// path.</para>
        /// </summary>
        public const string DemotedCanonReason = "rewound-out-supersede-fork-demoted-canon";

        /// <summary>Stable id; format <c>rrt_&lt;Guid-N&gt;</c>.</summary>
        public string RetirementId;

        /// <summary>The committed fork recording that should no longer be active.</summary>
        public string RecordingId;

        /// <summary>The old-side recording restored by the supersede rollback.</summary>
        public string RestoredRecordingId;

        /// <summary>The dropped supersede relation id that caused this retirement.</summary>
        public string SourceSupersedeRelationId;

        /// <summary>Adjusted rewind UT used when the retirement was authored.</summary>
        public double RewindUT;

        /// <summary>Planetarium UT when the retirement was authored, when available.</summary>
        public double CreatedUT;

        /// <summary>Wall-clock timestamp (ISO 8601 UTC string).</summary>
        public string CreatedRealTime;

        /// <summary>Machine-readable reason; defaults to <see cref="DefaultReason"/>.</summary>
        public string Reason;

        /// <summary>Appends an <c>ENTRY</c> child node to the given parent.</summary>
        public void SaveInto(ConfigNode parent)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            var ic = CultureInfo.InvariantCulture;
            ConfigNode node = parent.AddNode("ENTRY");
            node.AddValue("retirementId", RetirementId ?? "");
            node.AddValue("recordingId", RecordingId ?? "");
            node.AddValue("restoredRecordingId", RestoredRecordingId ?? "");
            node.AddValue("sourceSupersedeRelationId", SourceSupersedeRelationId ?? "");
            node.AddValue("rewindUT", RewindUT.ToString("R", ic));
            node.AddValue("createdUT", CreatedUT.ToString("R", ic));
            if (!string.IsNullOrEmpty(CreatedRealTime))
                node.AddValue("createdRealTime", CreatedRealTime);
            node.AddValue("reason", string.IsNullOrEmpty(Reason) ? DefaultReason : Reason);
        }

        /// <summary>Loads a single <c>ENTRY</c> ConfigNode.</summary>
        public static RecordingRewindRetirement LoadFrom(ConfigNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            var ic = CultureInfo.InvariantCulture;
            var retirement = new RecordingRewindRetirement();

            string retirementId = node.GetValue("retirementId");
            retirement.RetirementId = string.IsNullOrEmpty(retirementId) ? null : retirementId;

            string recordingId = node.GetValue("recordingId");
            retirement.RecordingId = string.IsNullOrEmpty(recordingId) ? null : recordingId;

            string restored = node.GetValue("restoredRecordingId");
            retirement.RestoredRecordingId = string.IsNullOrEmpty(restored) ? null : restored;

            string sourceRel = node.GetValue("sourceSupersedeRelationId");
            retirement.SourceSupersedeRelationId = string.IsNullOrEmpty(sourceRel) ? null : sourceRel;

            string rewindStr = node.GetValue("rewindUT");
            if (!string.IsNullOrEmpty(rewindStr)
                && double.TryParse(rewindStr, NumberStyles.Float, ic, out double rewindUT))
                retirement.RewindUT = rewindUT;

            string createdStr = node.GetValue("createdUT");
            if (!string.IsNullOrEmpty(createdStr)
                && double.TryParse(createdStr, NumberStyles.Float, ic, out double createdUT))
                retirement.CreatedUT = createdUT;

            retirement.CreatedRealTime = node.GetValue("createdRealTime");
            string reason = node.GetValue("reason");
            retirement.Reason = string.IsNullOrEmpty(reason) ? DefaultReason : reason;

            return retirement;
        }
    }
}
