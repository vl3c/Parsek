using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The one-shot message a Re-Fly merge shows when it took the replaced flight's losses
    /// off the career (owner ruling 2026-11-06, todo GUI-EXPOSURE-4): which kerbals' deaths,
    /// how many contract failures and how many reputation penalties the merge retired. It is
    /// a message for an EVENT that changed something, so it is shown only when at least one
    /// of those was retired; a merge that retired only crew bookkeeping or zero-value
    /// duplicates says nothing.
    /// </summary>
    internal static partial class SupersedeCommit
    {
        internal const string ReFlyUndoneMessagePrefix = "Re-Fly merged - the old flight's losses are gone: ";

        /// <summary>
        /// Pure: builds the message from the rows this merge retired from the REPLACED flight
        /// (never the re-fly attempt's own rows), or null when none of them is a death, a
        /// contract failure or a reputation penalty. A death's bundled reputation penalty
        /// (<see cref="ReputationPenaltySource.KerbalDeath"/>) is part of the death and is not
        /// counted again; a contract failure's own penalty fields ride on its row.
        /// </summary>
        internal static string BuildReFlyUndoneMessage(IReadOnlyList<GameAction> retiredRows)
        {
            if (retiredRows == null || retiredRows.Count == 0)
                return null;

            var deadKerbals = new List<string>();
            var seenKerbals = new HashSet<string>(StringComparer.Ordinal);
            int contractFailures = 0;
            int repPenalties = 0;
            for (int i = 0; i < retiredRows.Count; i++)
            {
                GameAction a = retiredRows[i];
                if (a == null) continue;
                switch (a.Type)
                {
                    case GameActionType.KerbalAssignment:
                        if (a.KerbalEndStateField == KerbalEndState.Dead
                            && !string.IsNullOrEmpty(a.KerbalName)
                            && seenKerbals.Add(a.KerbalName))
                            deadKerbals.Add(a.KerbalName);
                        break;
                    case GameActionType.ContractFail:
                        contractFailures++;
                        break;
                    case GameActionType.ReputationPenalty:
                        if (a.RepPenaltySource != ReputationPenaltySource.KerbalDeath
                            && a.NominalPenalty > 0f)
                            repPenalties++;
                        break;
                }
            }

            var parts = new List<string>();
            if (deadKerbals.Count == 1)
                parts.Add(deadKerbals[0] + "'s death");
            else if (deadKerbals.Count > 1)
                parts.Add("the deaths of " + JoinNames(deadKerbals));
            if (contractFailures > 0)
                parts.Add(contractFailures.ToString(CultureInfo.InvariantCulture)
                    + (contractFailures == 1 ? " contract failure" : " contract failures"));
            if (repPenalties > 0)
                parts.Add(repPenalties.ToString(CultureInfo.InvariantCulture)
                    + (repPenalties == 1 ? " reputation penalty" : " reputation penalties"));
            if (parts.Count == 0)
                return null;
            return ReFlyUndoneMessagePrefix + string.Join(", ", parts);
        }

        private static string JoinNames(List<string> names)
        {
            if (names.Count == 1) return names[0];
            return string.Join(", ", names.GetRange(0, names.Count - 1)) + " and " + names[names.Count - 1];
        }

        /// <summary>
        /// Shows <see cref="BuildReFlyUndoneMessage"/> once and logs it (or logs why nothing
        /// was shown). Called by <see cref="CommitTombstones"/> with the replaced flight's rows
        /// it just retired; a re-run of an interrupted merge finds those rows already
        /// tombstoned, passes none, and so shows nothing a second time.
        /// </summary>
        private static void AnnounceReFlyUndone(IReadOnlyList<GameAction> retiredRows, string retiringRecordingId)
        {
            string message = BuildReFlyUndoneMessage(retiredRows);
            if (message == null)
            {
                ParsekLog.Verbose(Tag,
                    $"Re-Fly undone message: none (retiredRows={(retiredRows?.Count ?? 0).ToString(CultureInfo.InvariantCulture)}, " +
                    $"no death / contract failure / reputation penalty) retiring={retiringRecordingId ?? "<none>"}");
                return;
            }
            ParsekLog.Info(Tag, $"Re-Fly undone message shown: \"{message}\" retiring={retiringRecordingId ?? "<none>"}");
            ParsekLog.ScreenMessage(message, 6f);
        }
    }
}
