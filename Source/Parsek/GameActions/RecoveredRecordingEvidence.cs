using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// One answer to "was this recording's vessel recovered?", shared by every reader that
    /// must not treat a recovered flight as a vessel still in the world: the end-of-recording
    /// spawn (<see cref="GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(Recording, bool, RecordingTree, bool)"/>),
    /// the manual Stash classifier (<c>UnfinishedFlightClassifier</c>) and the Re-Fly
    /// resurrected-recovery retirement (<see cref="ResurrectionRetirementEligibility"/>).
    ///
    /// <para>
    /// A recording is recovered when its terminal is <see cref="TerminalState.Recovered"/>,
    /// or when its terminal left the vessel in the world
    /// (<see cref="ResurrectionRetirementEligibility.VesselOutlivedTerminal"/>) and the
    /// effective ledger holds a recovery row tagged to it: a
    /// <see cref="GameActionType.FundsEarning"/> with <see cref="FundsEarningSource.Recovery"/>,
    /// a <see cref="GameActionType.KerbalRecovered"/> crew-close row, or a
    /// <see cref="GameActionType.VesselRecovered"/> row. All three are written only from a
    /// real <c>onVesselRecovered</c> (or the commit-time pairing of a Recovered terminal),
    /// so a row means the vessel was recovered.
    /// </para>
    ///
    /// <para>
    /// The funds row needs stock's <c>FundsChanged(VesselRecovery)</c> (never raised on a
    /// sandbox save) and the crew-close row needs crew aboard, so the
    /// <see cref="GameActionType.VesselRecovered"/> row is the one written on every player
    /// recovery that maps to a committed recording, in every game mode. No reader filters
    /// by UT: a recovered recording's vessel never spawns again, also after a rewind to
    /// before the recovery. Only a tombstone (a Re-Fly supersede or resurrection) retires
    /// the evidence.
    /// </para>
    ///
    /// <para>
    /// The row half exists because the terminal alone under-reports. A recording committed
    /// before its vessel was recovered keeps its situation terminal (committed recordings
    /// are never re-stamped by a live event): a Tracking Station or KSC-marker recovery of
    /// an older flight, and every save written before the in-flight Recover started
    /// committing Recovered (<see cref="InFlightRecoveryRequest"/>).
    /// </para>
    /// </summary>
    internal static class RecoveredRecordingEvidence
    {
        /// <summary>Evidence tag: the recording's own terminal is Recovered.</summary>
        internal const string EvidenceTerminal = "terminal-recovered";

        /// <summary>Evidence tag: a recovery ledger row is tagged to the recording.</summary>
        internal const string EvidenceLedgerRow = "recovery-row";

        /// <summary>
        /// True for a <see cref="GameActionType.FundsEarning"/> recovery payout tagged to
        /// <paramref name="recordingId"/> with a UT after <paramref name="afterUT"/>.
        /// </summary>
        internal static bool IsRecoveryFundsRow(GameAction action, string recordingId, double afterUT)
        {
            if (!IsTaggedAfter(action, recordingId, afterUT)) return false;
            return action.Type == GameActionType.FundsEarning
                && action.FundsSource == FundsEarningSource.Recovery;
        }

        /// <summary>
        /// True for a <see cref="GameActionType.KerbalRecovered"/> crew-close row tagged to
        /// <paramref name="recordingId"/> with a UT after <paramref name="afterUT"/>.
        /// </summary>
        internal static bool IsCrewCloseRow(GameAction action, string recordingId, double afterUT)
        {
            if (!IsTaggedAfter(action, recordingId, afterUT)) return false;
            return action.Type == GameActionType.KerbalRecovered;
        }

        /// <summary>
        /// True for a <see cref="GameActionType.VesselRecovered"/> row tagged to
        /// <paramref name="recordingId"/> with a UT after <paramref name="afterUT"/>.
        /// </summary>
        internal static bool IsVesselRecoveredRow(GameAction action, string recordingId, double afterUT)
        {
            if (!IsTaggedAfter(action, recordingId, afterUT)) return false;
            return action.Type == GameActionType.VesselRecovered;
        }

        /// <summary>Any recovery row kind (funds payout, crew close or vessel recovered).</summary>
        internal static bool IsRecoveryRow(GameAction action, string recordingId, double afterUT)
        {
            return IsRecoveryFundsRow(action, recordingId, afterUT)
                || IsCrewCloseRow(action, recordingId, afterUT)
                || IsVesselRecoveredRow(action, recordingId, afterUT);
        }

        /// <summary>True when any row of <paramref name="ledger"/> is a recovery row for the recording.</summary>
        internal static bool HasRecoveryRow(
            string recordingId,
            IReadOnlyList<GameAction> ledger,
            double afterUT = double.NegativeInfinity)
        {
            if (string.IsNullOrEmpty(recordingId) || ledger == null) return false;
            for (int i = 0; i < ledger.Count; i++)
            {
                if (IsRecoveryRow(ledger[i], recordingId, afterUT))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Pure predicate: Recovered terminal, or an outlived terminal with a recovery row in
        /// <paramref name="ledger"/>. <paramref name="evidence"/> names which (null when false).
        /// </summary>
        internal static bool IsRecovered(
            Recording rec,
            IReadOnlyList<GameAction> ledger,
            out string evidence)
        {
            evidence = null;
            if (rec == null) return false;
            if (rec.TerminalStateValue == TerminalState.Recovered)
            {
                evidence = EvidenceTerminal;
                return true;
            }
            if (!ResurrectionRetirementEligibility.VesselOutlivedTerminal(rec.TerminalStateValue))
                return false;
            if (!HasRecoveryRow(rec.RecordingId, ledger))
                return false;
            evidence = EvidenceLedgerRow;
            return true;
        }

        /// <summary>
        /// The row half only: an outlived terminal (not Recovered) whose recording carries a
        /// recovery row in <paramref name="ledger"/>. The readers that already reject a
        /// Recovered terminal on their own call this.
        /// </summary>
        internal static bool IsRecoveredByLedgerRow(Recording rec, IReadOnlyList<GameAction> ledger)
        {
            return IsRecovered(rec, ledger, out string evidence)
                && string.Equals(evidence, EvidenceLedgerRow, StringComparison.Ordinal);
        }

        // Live index over the effective ledger, keyed by the ELS list instance:
        // EffectiveState.ComputeELS returns the same cached list until the ledger or the
        // tombstones change, so the per-recording lookup is O(1) on the spawn path.
        private static readonly object indexLock = new object();
        private static IReadOnlyList<GameAction> indexedEls;
        private static HashSet<string> recoveryRowRecordingIds;

        /// <summary>
        /// Live wrapper of <see cref="IsRecoveredByLedgerRow"/> over
        /// <see cref="EffectiveState.ComputeELS"/> (a tombstoned recovery row, e.g. one a
        /// Re-Fly retired, no longer counts).
        /// </summary>
        internal static bool IsRecoveredByLedgerRowLive(Recording rec)
        {
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) return false;
            if (rec.TerminalStateValue == TerminalState.Recovered) return false;
            if (!ResurrectionRetirementEligibility.VesselOutlivedTerminal(rec.TerminalStateValue))
                return false;

            IReadOnlyList<GameAction> els = EffectiveState.ComputeELS();
            lock (indexLock)
            {
                if (!object.ReferenceEquals(els, indexedEls) || recoveryRowRecordingIds == null)
                {
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    if (els != null)
                    {
                        for (int i = 0; i < els.Count; i++)
                        {
                            var a = els[i];
                            if (a == null || string.IsNullOrEmpty(a.RecordingId)) continue;
                            if (IsRecoveryRow(a, a.RecordingId, double.NegativeInfinity))
                                ids.Add(a.RecordingId);
                        }
                    }
                    recoveryRowRecordingIds = ids;
                    indexedEls = els;
                }
                return recoveryRowRecordingIds.Contains(rec.RecordingId);
            }
        }

        internal static void ResetForTesting()
        {
            lock (indexLock)
            {
                indexedEls = null;
                recoveryRowRecordingIds = null;
            }
        }

        private static bool IsTaggedAfter(GameAction action, string recordingId, double afterUT)
        {
            if (action == null || string.IsNullOrEmpty(recordingId)) return false;
            if (!string.Equals(action.RecordingId, recordingId, StringComparison.Ordinal))
                return false;
            return action.UT > afterUT;
        }
    }
}
