using System;
using System.Collections.Generic;

namespace Parsek
{
    // The live half of RecoveredRecordingEvidence: whether the effective ledger (ComputeELS)
    // holds a recovery row for a recording, through an index cached per ELS list instance.
    // It reads effective state, so it lives here; the pure row predicates stay in GameActions.
    public static partial class EffectiveState
    {
        // Live index over the effective ledger, keyed by the ELS list instance:
        // EffectiveState.ComputeELS returns the same cached list until the ledger or the
        // tombstones change, so the per-recording lookup is O(1) on the spawn path.
        private static readonly object indexLock = new object();
        private static IReadOnlyList<GameAction> indexedEls;
        private static HashSet<string> recoveryRowRecordingIds;
        private static HashSet<string> terminatedRowRecordingIds;

        /// <summary>
        /// Live wrapper of <see cref="RecoveredRecordingEvidence.IsRecoveredByLedgerRow"/> over
        /// <see cref="EffectiveState.ComputeELS"/> (a tombstoned recovery row, e.g. one a
        /// Re-Fly retired, no longer counts).
        /// </summary>
        internal static bool IsRecoveredByLedgerRowLive(Recording rec)
        {
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) return false;
            if (rec.TerminalStateValue == TerminalState.Recovered) return false;
            if (!ResurrectionRetirementEligibility.VesselOutlivedTerminal(rec.TerminalStateValue))
                return false;

            IReadOnlyList<GameAction> els = ComputeELS();
            lock (indexLock)
            {
                EnsureEvidenceIndex(els);
                return recoveryRowRecordingIds.Contains(rec.RecordingId);
            }
        }

        /// <summary>
        /// Live wrapper of <see cref="RecoveredRecordingEvidence.IsTerminatedByLedgerRow"/> over
        /// <see cref="EffectiveState.ComputeELS"/>: the player terminated the vessel this
        /// recording's end left in the world (a tombstoned row no longer counts).
        /// </summary>
        internal static bool IsTerminatedByLedgerRowLive(Recording rec)
        {
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) return false;
            if (!ResurrectionRetirementEligibility.VesselOutlivedTerminal(rec.TerminalStateValue))
                return false;

            IReadOnlyList<GameAction> els = ComputeELS();
            lock (indexLock)
            {
                EnsureEvidenceIndex(els);
                return terminatedRowRecordingIds.Contains(rec.RecordingId);
            }
        }

        // Caller holds indexLock. Rebuilds both id sets in one pass when the ELS list changed.
        private static void EnsureEvidenceIndex(IReadOnlyList<GameAction> els)
        {
            if (object.ReferenceEquals(els, indexedEls)
                && recoveryRowRecordingIds != null
                && terminatedRowRecordingIds != null)
                return;
            var recovered = new HashSet<string>(StringComparer.Ordinal);
            var terminated = new HashSet<string>(StringComparer.Ordinal);
            if (els != null)
            {
                for (int i = 0; i < els.Count; i++)
                {
                    var a = els[i];
                    if (a == null || string.IsNullOrEmpty(a.RecordingId)) continue;
                    if (RecoveredRecordingEvidence.IsRecoveryRow(a, a.RecordingId, double.NegativeInfinity))
                        recovered.Add(a.RecordingId);
                    else if (RecoveredRecordingEvidence.IsVesselTerminatedRow(a, a.RecordingId, double.NegativeInfinity))
                        terminated.Add(a.RecordingId);
                }
            }
            recoveryRowRecordingIds = recovered;
            terminatedRowRecordingIds = terminated;
            indexedEls = els;
        }

        internal static void ResetRecoveredIndexForTesting()
        {
            lock (indexLock)
            {
                indexedEls = null;
                recoveryRowRecordingIds = null;
                terminatedRowRecordingIds = null;
            }
        }
    }
}
