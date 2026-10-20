using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Design section 2.12 Rule 3, "A vessel the committed future uses is held back at
    /// invoke". A Re-Fly loads its rewind point's quicksave, which brings back every vessel
    /// that was live at the rewind UT. The post-load strip removes the slot's siblings; an
    /// unrelated survivor stays. When a committed recording flies that survivor or recovers
    /// it after the rewind UT, the survivor is handed to the existing claimed-vessel hold
    /// (<c>VesselGhoster</c> plus <c>ParsekFlight.UpdateClaimedVesselHoldGhosts</c>): it is
    /// despawned, drawn as a hold ghost in its rewind-save shape until its first claim, and
    /// from then on the committed recording plays it. Its committed rows stay; nothing is
    /// tombstoned at invoke. This replaces the invoke-time retirement of such a vessel's
    /// recovery rows (the old Step 3b of <c>RewindInvoker.ConsumePostLoad</c>).
    ///
    /// <para>
    /// A vessel a committed recording CHANGES after the rewind UT (a dock, a board, a part
    /// transfer) is a ghost-chain claim and is held by the chain feed
    /// (<c>ParsekFlight.EvaluateAndApplyGhostChains</c>); this feed skips a vessel that is
    /// already held.
    /// </para>
    ///
    /// <para>
    /// Identity is POSITIVE launch identity
    /// (<see cref="VesselLaunchIdentity.LiveVesselIsPositivelyRecordedLaunch"/>): pid equal
    /// and both launch guids known and equal. Holding a vessel removes it from the world, so
    /// a relaunch of the same craft (a craft-baked pid) must never match on pid alone.
    /// </para>
    /// </summary>
    internal static class ReFlyInvokeHold
    {
        internal const string EvidenceRecovery = "recovery";
        internal const string EvidenceFlies = "flies";

        /// <summary>One survivor the committed future uses.</summary>
        internal sealed class Entry
        {
            public uint Pid;
            public string LaunchGuid;

            /// <summary>The committed recording whose future use decided the hold (the earliest claim).</summary>
            public string RecordingId;

            /// <summary>UT of the first committed use after the rewind UT: the hold ghost shows until then.</summary>
            public double FirstClaimUT;

            /// <summary><see cref="EvidenceRecovery"/> or <see cref="EvidenceFlies"/>.</summary>
            public string Evidence;

            /// <summary>
            /// Recordings of this launch with trajectory after the rewind UT: their end spawn
            /// claim on the held pid is released, so the vessel comes back at their end.
            /// </summary>
            public List<string> FlyingRecordingIds = new List<string>();
        }

        /// <summary>
        /// Pure: the survivors the committed future uses after <paramref name="rewindUT"/>.
        /// <paramref name="excludedPids"/> are never held (the re-flown vessel, the stripped
        /// vessels). A recording of the survivor's launch counts when it ends after the rewind
        /// UT (it flies the vessel then; first claim = its start, or the rewind UT when it is
        /// already playing) or carries recovery evidence after it (a <c>Recovered</c> terminal
        /// ending after the rewind UT, or a post-rewind recovery row; first claim = the
        /// earliest recovery UT). <paramref name="matchedWithoutFuture"/> counts survivors that
        /// matched a recording with neither.
        /// </summary>
        internal static List<Entry> Classify(
            IReadOnlyList<(uint pid, string guid)> survivors,
            ICollection<uint> excludedPids,
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyList<GameAction> ledgerActions,
            double rewindUT,
            out int matchedWithoutFuture)
        {
            matchedWithoutFuture = 0;
            var result = new List<Entry>();
            if (survivors == null || survivors.Count == 0) return result;
            if (committedRecordings == null || committedRecordings.Count == 0) return result;
            if (double.IsNaN(rewindUT) || double.IsInfinity(rewindUT)) return result;

            var seen = new HashSet<uint>();
            for (int s = 0; s < survivors.Count; s++)
            {
                uint pid = survivors[s].pid;
                string guid = survivors[s].guid;
                if (pid == 0u || !seen.Add(pid)) continue;
                if (excludedPids != null && excludedPids.Contains(pid)) continue;

                Entry entry = null;
                bool matched = false;
                for (int r = 0; r < committedRecordings.Count; r++)
                {
                    Recording rec = committedRecordings[r];
                    if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) continue;
                    if (!VesselLaunchIdentity.LiveVesselIsPositivelyRecordedLaunch(rec, pid, guid)) continue;
                    matched = true;

                    double claimUT;
                    string evidence;
                    if (!TryFirstFutureUse(rec, ledgerActions, rewindUT, out claimUT, out evidence))
                        continue;

                    if (entry == null)
                        entry = new Entry { Pid = pid, LaunchGuid = guid, FirstClaimUT = double.PositiveInfinity };
                    if (evidence == EvidenceFlies)
                        entry.FlyingRecordingIds.Add(rec.RecordingId);
                    if (claimUT < entry.FirstClaimUT)
                    {
                        entry.FirstClaimUT = claimUT;
                        entry.RecordingId = rec.RecordingId;
                        entry.Evidence = evidence;
                    }
                }

                if (entry != null)
                    result.Add(entry);
                else if (matched)
                    matchedWithoutFuture++;
            }
            return result;
        }

        /// <summary>
        /// Pure: the first UT after <paramref name="rewindUT"/> at which <paramref name="rec"/>
        /// uses its vessel, and why. False when it uses it no more after the rewind UT.
        /// </summary>
        internal static bool TryFirstFutureUse(
            Recording rec, IReadOnlyList<GameAction> ledgerActions, double rewindUT,
            out double claimUT, out string evidence)
        {
            claimUT = double.NaN;
            evidence = null;
            if (rec == null) return false;

            bool terminalRecovered = rec.TerminalStateValue == TerminalState.Recovered;
            if (rec.EndUT > rewindUT && !terminalRecovered)
            {
                claimUT = Math.Max(rec.StartUT, rewindUT);
                evidence = EvidenceFlies;
                return true;
            }

            double earliestRecovery = double.PositiveInfinity;
            if (terminalRecovered && rec.EndUT > rewindUT)
                earliestRecovery = Math.Max(rec.StartUT, rewindUT);
            if (ledgerActions != null
                && (terminalRecovered
                    || ResurrectionRetirementEligibility.VesselOutlivedTerminal(rec.TerminalStateValue)))
            {
                for (int a = 0; a < ledgerActions.Count; a++)
                {
                    GameAction action = ledgerActions[a];
                    if (!RecoveredRecordingEvidence.IsRecoveryRow(action, rec.RecordingId, rewindUT))
                        continue;
                    if (action.UT < earliestRecovery)
                        earliestRecovery = action.UT;
                }
            }
            if (double.IsPositiveInfinity(earliestRecovery))
                return false;
            claimUT = earliestRecovery;
            evidence = EvidenceRecovery;
            return true;
        }
    }
}
