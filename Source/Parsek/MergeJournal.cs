using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Singleton journal for crash-recoverable staged-commit merges (design
    /// doc sections 5.8 + 6.6). Written in-memory at merge step 1 and durably
    /// saved at step 8; cleared at step 13 and durably saved at step 14. Its
    /// presence on load triggers the finisher in section 6.9 step 2.
    ///
    /// <para>Phase 1 only defines the data shape; behavior (the finisher and
    /// the staged-commit writer) arrives in later phases. The
    /// <see cref="Phases"/> constants are declared here so the merge writer
    /// in Phase 10 compiles against the same strings this loader recognizes
    /// — otherwise a typo in either place would silently break recovery.</para>
    /// </summary>
    public class MergeJournal
    {
        public string JournalId;
        public string SessionId;

        /// <summary>
        /// Phase string. v1 only writes <see cref="Phases.Begin"/> while a merge is
        /// in staged commit (see design doc section 6.6 failure-recovery matrix).
        /// Additional phase constants are declared for forward-compat so §10 writers
        /// and §6.9 finishers reference the same strings.
        /// </summary>
        public string Phase = Phases.Begin;

        /// <summary>Phase vocabulary (design doc sections 5.8 + 6.6).</summary>
        public static class Phases
        {
            public const string Begin = "Begin";
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
            node.AddValue("phase", string.IsNullOrEmpty(Phase) ? Phases.Begin : Phase);
            // UT and wall-clock could be added in later phases; Phase 1 only requires
            // the fields the design doc enumerates here (JournalId, SessionId, Phase).
            // Culture shim retained for future float fields.
            GC.KeepAlive(ic);
        }

        public static MergeJournal LoadFrom(ConfigNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            var j = new MergeJournal();

            string jid = node.GetValue("journalId");
            j.JournalId = string.IsNullOrEmpty(jid) ? null : jid;

            string sid = node.GetValue("sessionId");
            j.SessionId = string.IsNullOrEmpty(sid) ? null : sid;

            string phase = node.GetValue("phase");
            j.Phase = string.IsNullOrEmpty(phase) ? Phases.Begin : phase;

            return j;
        }
    }
}
