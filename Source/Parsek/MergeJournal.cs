using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Singleton journal for crash-recoverable staged-commit merges (design
    /// doc sections 5.8 + 6.6). Written in-memory at merge step 1 and
    /// persisted at each stable crash-recovery barrier; finally cleared before
    /// the last durable save so the terminal merged state lands on disk with
    /// no live journal. Its presence on load triggers the finisher in
    /// section 6.9 step 2.
    ///
    /// <para>Phase 1 only defines the data shape; behavior (the finisher and
    /// the staged-commit writer) arrives in later phases. The
    /// <see cref="Phases"/> constants are declared here so the merge writer
    /// in Phase 10 compiles against the same strings this loader recognizes
    /// — otherwise a typo in either place would silently break recovery.</para>
    /// </summary>
    public class MergeJournal
    {
        /// <summary>
        /// Per-run marker id; addition to design §5.8. Useful for the orchestrator
        /// to distinguish repeated staged-commit attempts in logs even when the
        /// SessionId is reused. ADDITION to design, not a rename.
        /// </summary>
        public string JournalId;

        public string SessionId;

        /// <summary>
        /// RecordingTree the staged merge belongs to. Added so purge/cleanup
        /// paths can scope a live journal without inferring through the
        /// marker, which may already be cleared in post-Durable1 phases.
        /// </summary>
        public string TreeId;

        /// <summary>
        /// Phase string. v1 only writes <see cref="Phases.Begin"/> while a merge is
        /// in staged commit (see design doc section 6.6 failure-recovery matrix).
        /// Additional phase constants are declared for forward-compat so §10 writers
        /// and §6.9 finishers reference the same strings.
        /// </summary>
        public string Phase = Phases.Begin;

        /// <summary>Planetarium UT at which the staged-commit began (design §5.8).</summary>
        public double StartedUT;

        /// <summary>Wall-clock timestamp at which the staged-commit began (ISO 8601 UTC; design §5.8).</summary>
        public string StartedRealTime;

        /// <summary>
        /// Phase vocabulary (design doc sections 5.8 + 6.6). Phase 10 of the
        /// rewind-to-staging rollout extends the single <see cref="Begin"/>
        /// marker with the intermediate staged-commit checkpoints written by
        /// <c>MergeJournalOrchestrator.RunMerge</c>. The on-load finisher
        /// branches on these strings to decide whether the interrupted merge
        /// should roll back (Phase less-than-or-equal <see cref="Finalize"/>)
        /// or be driven to completion (Phase greater-than-or-equal
        /// <see cref="Durable1Done"/>).
        /// </summary>
        public static class Phases
        {
            public const string Begin = "Begin";
            public const string Supersede = "Supersede";
            public const string Tombstone = "Tombstone";
            public const string Finalize = "Finalize";
            public const string Durable1Done = "Durable1Done";
            public const string RpReap = "RpReap";
            public const string MarkerCleared = "MarkerCleared";
            public const string Durable2Done = "Durable2Done";
            public const string Complete = "Complete";
        }

        /// <summary>
        /// Returns true if <paramref name="phase"/> represents a checkpoint at
        /// or before the first durable save — i.e. on-disk state is still the
        /// pre-merge snapshot and any recovery must roll the in-memory merge
        /// back (design §6.6 failure-recovery matrix).
        /// </summary>
        public static bool IsPreDurablePhase(string phase)
        {
            return phase == Phases.Begin
                || phase == Phases.Supersede
                || phase == Phases.Tombstone
                || phase == Phases.Finalize;
        }

        /// <summary>
        /// Returns true if <paramref name="phase"/> represents a checkpoint
        /// after Durable Save #1 — the first durable save wrote the merged
        /// state to disk, so recovery must complete the remaining steps
        /// instead of rolling back.
        /// </summary>
        public static bool IsPostDurablePhase(string phase)
        {
            return phase == Phases.Durable1Done
                || phase == Phases.RpReap
                || phase == Phases.MarkerCleared
                || phase == Phases.Durable2Done
                || phase == Phases.Complete;
        }

        internal const string NodeName = "MERGE_JOURNAL";

        /// <summary>Saves into a dedicated child node on the parent.</summary>
        public void SaveInto(ConfigNode parent)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            var ic = CultureInfo.InvariantCulture;
            ConfigNode node = parent.AddNode(NodeName);
            node.AddValue("journalId", JournalId ?? "");
            node.AddValue("sessionId", SessionId ?? "");
            node.AddValue("treeId", TreeId ?? "");
            node.AddValue("phase", string.IsNullOrEmpty(Phase) ? Phases.Begin : Phase);
            node.AddValue("startedUT", StartedUT.ToString("R", ic));
            if (!string.IsNullOrEmpty(StartedRealTime))
                node.AddValue("startedRealTime", StartedRealTime);
        }

        public static MergeJournal LoadFrom(ConfigNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            var ic = CultureInfo.InvariantCulture;
            var j = new MergeJournal();

            string jid = node.GetValue("journalId");
            j.JournalId = string.IsNullOrEmpty(jid) ? null : jid;

            string sid = node.GetValue("sessionId");
            j.SessionId = string.IsNullOrEmpty(sid) ? null : sid;

            string treeId = node.GetValue("treeId");
            j.TreeId = string.IsNullOrEmpty(treeId) ? null : treeId;

            string phase = node.GetValue("phase");
            j.Phase = string.IsNullOrEmpty(phase) ? Phases.Begin : phase;

            string utStr = node.GetValue("startedUT");
            double ut;
            if (!string.IsNullOrEmpty(utStr) && double.TryParse(utStr, NumberStyles.Float, ic, out ut))
                j.StartedUT = ut;

            j.StartedRealTime = node.GetValue("startedRealTime");

            return j;
        }
    }
}
