using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Pure classifier for issue #15: a Re-Fly puts a vessel back in the world that the
    /// player had already RECOVERED, but the recovery's career rewards stayed in the
    /// ledger. The craft is flying again AND its recovery funds/science are still banked —
    /// a duplication the player never earned.
    ///
    /// <para>
    /// This decides which ledger actions the resurrection retires. It does NOT remove
    /// anything: the caller writes append-only <see cref="LedgerTombstone"/> rows, the
    /// codebase's retirement mechanism (ELS = ledger minus tombstones), so the history
    /// stays intact and the retirement is idempotent under the "at least one tombstone
    /// exists" rule.
    /// </para>
    ///
    /// <para>
    /// <b>Identity is guid-based, and a pid-only match is FORBIDDEN here.</b> KSP bakes
    /// <c>persistentId</c> into the .craft and reuses it on every launch, so a bare pid
    /// match cannot tell a fresh launch of the same craft from the recorded one. Everywhere
    /// else in the codebase an unknown guid degrades to pid-only, which is the right call
    /// for a visibility or dedup decision. It is the WRONG call here, because the
    /// consequence is clawing funds back out of the player's account: this classifier
    /// requires a POSITIVE guid match on both sides and matches nothing when either side's
    /// guid is unknown.
    /// </para>
    /// </summary>
    internal static class ResurrectionRetirementEligibility
    {
        /// <summary>
        /// One resurrected recording and the actions its resurrection retires.
        /// </summary>
        internal sealed class ResurrectedRecovery
        {
            /// <summary>The committed recording whose recovery is being undone.</summary>
            public string RecordingId;

            /// <summary>The live vessel's persistentId that matched this recording.</summary>
            public uint LiveVesselPid;

            /// <summary>UT of the recovery this retirement is anchored on.</summary>
            public double AnchorUT;

            /// <summary>True when no recovery funds row existed and EndUT was used as the anchor.</summary>
            public bool UsedFallbackAnchor;

            /// <summary>
            /// What proved the recovery: <see cref="EvidenceTerminal"/> (the recording's own
            /// <see cref="TerminalState.Recovered"/> verdict) or <see cref="EvidenceLedgerRow"/>
            /// (a post-cutoff recovery row on a recording committed before the recovery fired).
            /// </summary>
            public string Evidence;

            /// <summary>ActionIds to tombstone, in ledger order.</summary>
            public List<string> RetiredActionIds = new List<string>();
        }

        /// <summary>Evidence tag: the recording itself ended <see cref="TerminalState.Recovered"/>.</summary>
        internal const string EvidenceTerminal = RecoveredRecordingEvidence.EvidenceTerminal;

        /// <summary>Evidence tag: the ledger holds a post-cutoff recovery row for the recording.</summary>
        internal const string EvidenceLedgerRow = RecoveredRecordingEvidence.EvidenceLedgerRow;

        /// <summary>
        /// True when a recording's terminal leaves the recorded vessel in the world, so a LATER
        /// recovery of it is possible: no verdict yet, or a situation verdict (Orbiting,
        /// Landed, Splashed, SubOrbital). False for Recovered (handled as its own evidence)
        /// and for every verdict that ended the vessel (Destroyed, Docked, Boarded,
        /// Disassembled).
        /// </summary>
        internal static bool VesselOutlivedTerminal(TerminalState? terminal)
        {
            if (!terminal.HasValue) return true;
            switch (terminal.Value)
            {
                case TerminalState.Orbiting:
                case TerminalState.Landed:
                case TerminalState.Splashed:
                case TerminalState.SubOrbital:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// True only when a live vessel is POSITIVELY the same launch the recording captured:
        /// pid equal AND both launch guids known AND equal. Unlike
        /// <see cref="VesselLaunchIdentity.LiveVesselIsRecordedLaunch"/> this does NOT fall
        /// back to pid-only on an unknown guid — see the class remarks for why. The
        /// implementation lives with its degrading sibling as
        /// <see cref="VesselLaunchIdentity.LiveVesselIsPositivelyRecordedLaunch"/>; this name is
        /// kept because the #15 rationale reads from here.
        /// </summary>
        internal static bool IsPositivelySameLaunch(Recording rec, uint livePid, string liveGuid)
        {
            return VesselLaunchIdentity.LiveVesselIsPositivelyRecordedLaunch(rec, livePid, liveGuid);
        }

        /// <summary>
        /// Classifies every committed recording that a surviving live vessel resurrects, and
        /// the ledger actions each resurrection retires.
        ///
        /// <para>
        /// A recording qualifies when a surviving live vessel is POSITIVELY its launch
        /// (<see cref="IsPositivelySameLaunch"/>) and the recording carries recovery evidence
        /// after <paramref name="retireCutoffUT"/> (a recovery from BEFORE the rewind point is
        /// still true in the reverted world and must be left alone). Two kinds of evidence
        /// count:
        /// </para>
        ///
        /// <para>
        /// (1) The recording's terminal verdict is <see cref="TerminalState.Recovered"/>. Only
        /// a not-yet-committed tree is ever stamped Recovered: the pending tree from
        /// <c>onVesselRecovered</c> (<c>UpdateRecordingsForTerminalEvent</c>), or the active
        /// tree at the scene-exit finalize of an in-flight Recover
        /// (<see cref="InFlightRecoveryRequest"/>, the shipping shape since 2026-10-01).
        /// </para>
        ///
        /// <para>
        /// (2) A <see cref="GameActionType.FundsEarning"/> row with
        /// <see cref="FundsEarningSource.Recovery"/>, or a
        /// <see cref="GameActionType.KerbalRecovered"/> row, on the recording with a UT after
        /// the cutoff, on a recording whose terminal left the vessel in the world
        /// (<see cref="VesselOutlivedTerminal"/>, row test shared through
        /// <see cref="RecoveredRecordingEvidence"/>). A Tracking Station or KSC-marker recovery
        /// reaches a recording committed long before, and saves written before 2026-10-01
        /// hold in-flight Recovers committed with the vessel's situation (e.g. Landed). Committed recordings are never re-stamped by a terminal event, so the
        /// recovery lives only in the ledger, as the #444 funds row
        /// (<c>LedgerOrchestrator.OnVesselRecoveryFunds</c>) and the crew-close row
        /// (<c>LedgerOrchestrator.OnRealVesselCrewRecovered</c>). Both are written only from a
        /// real <c>onVesselRecovered</c>, and the commit-time pairing in
        /// <c>CreateVesselCostActions</c> emits a recovery row only for a Recovered terminal,
        /// so a recovery row always means the vessel was recovered. The funds row's recording
        /// pick drops any candidate whose launch guid conclusively differs from the recovered
        /// vessel's, and this classifier additionally requires the survivor to be POSITIVELY
        /// the recording's launch, so the survivor is the vessel that was recovered.
        /// </para>
        ///
        /// <para>
        /// A <see cref="GameActionType.KerbalRecovered"/> row is evidence but is NOT retired.
        /// It closes the recording's open-ended Aboard hold at the recovery UT. Retired, the
        /// hold would stay open, and a later recovery of the resurrected vessel BEFORE the
        /// recording's end could not close it again (<c>CrewRecoveryReservationClose</c> picks
        /// only owners that ended at or before the recovery), so the kerbal would be held
        /// forever. Kept, the hold ends where it ended before the Re-Fly, and the kerbal
        /// aboard the resurrected vessel is protected by the live roster meanwhile.
        /// </para>
        ///
        /// <para>
        /// The retired set per recording: the recovery <see cref="GameActionType.FundsEarning"/>
        /// anchor(s) with <see cref="FundsEarningSource.Recovery"/>, plus same-recording
        /// <see cref="GameActionType.ScienceEarning"/> rows carrying
        /// <see cref="ScienceMethod.Recovered"/>, plus same-recording
        /// <see cref="GameActionType.KerbalAssignment"/> rows whose end state is
        /// <see cref="KerbalEndState.Recovered"/> (matched by end state rather than UT — the
        /// crew's disposition IS the recovery, whenever it was stamped), plus every
        /// same-recording <see cref="GameActionType.KerbalExperience"/> row (such a row exists
        /// only because a recovery archived the crew's flight log, so recording membership
        /// alone is the right match). Stock awards no reputation for a plain vessel recovery,
        /// so no reputation row is bundled.
        /// </para>
        ///
        /// <para>
        /// SCOPE FACT measured on the first production-shaped run (the in-game
        /// <c>ReFlyRecoveryBundleRuntimeTest</c>, 2026-08-20): a real recovery's
        /// <see cref="GameActionType.KerbalAssignment"/> row carries
        /// <c>endState = Aboard</c>, not <see cref="KerbalEndState.Recovered"/>, so the crew
        /// leg above does not fire on the real recovery shape - the merge path retires that
        /// row anyway via <c>TombstoneEligibility.IsSupersedeTombstoneEligible</c>, and the
        /// unit fixture keeping this leg green (<c>ResurrectionRetirementEligibilityTests</c>
        /// <c>CrewRecovered</c>) stamps a shape the real recovery ledger does not emit. Not a
        /// defect (the crew are alive in both worlds), a scope fact.
        /// </para>
        ///
        /// <para>
        /// <b>Science is keyed on <see cref="GameAction.Method"/>, and a UT window would be
        /// wrong in BOTH directions.</b> <c>GameStateEventConverter.ConvertScienceSubjects</c>
        /// stamps <c>Method = ResolveScienceMethod(subj.reasonKey)</c> on every
        /// <see cref="GameActionType.ScienceEarning"/> row it emits, mapping the recorder's
        /// <c>TransactionReasons.VesselRecovery</c> reason key to
        /// <see cref="ScienceMethod.Recovered"/>; the field round-trips through the
        /// <c>method</c> serialized value, and <c>LedgerOrchestrator</c> already reads it back
        /// to reconstruct the reason key. So the discriminator EXISTS and is exact. A UT window
        /// is not merely weaker, it is actively broken here, because that same converter stamps
        /// <c>UT = endUT</c> on EVERY science row of a recording: transmitted science the
        /// player banked mid-flight and still owns carries the same UT as the recovery, so any
        /// window that admits the recovery science admits the transmit too (OVER-retire, funds
        /// and research clawed back from the player), while a recovery whose funds anchor was
        /// sampled away from <c>endUT</c> falls outside the window and keeps its science
        /// banked (UNDER-retire). Recording membership plus <c>Method</c> has neither failure.
        /// </para>
        ///
        /// <para>
        /// The residual is a pro-player UNDER-retire: <c>reasonKey</c> is empty when the
        /// recorder's <c>ScienceChanged</c> capture does not match the
        /// <c>OnScienceReceived</c> callback, and such a row reads
        /// <see cref="ScienceMethod.Transmitted"/> and stays banked. Leaving science the
        /// player might own is the safe direction; taking it away is not.
        /// </para>
        ///
        /// <para>
        /// A recovery worth zero funds (a pod recovered at the pad, a fully-refunded craft)
        /// has no funds anchor. It still resurrects, and its crew/science rows still need
        /// retiring, so the recording's <see cref="Recording.EndUT"/> stands in as the anchor.
        /// </para>
        ///
        /// Pure: every input is a parameter; no statics, no singletons, no Unity.
        /// </summary>
        internal static List<ResurrectedRecovery> Classify(
            IReadOnlyList<(uint pid, string guid)> survivingIdentities,
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyList<GameAction> ledgerActions,
            double retireCutoffUT)
        {
            return Classify(
                survivingIdentities, committedRecordings, ledgerActions, retireCutoffUT,
                out _);
        }

        /// <summary>
        /// The classifier above, plus the number of committed recordings a survivor
        /// POSITIVELY matched that carried no post-cutoff recovery evidence
        /// (<paramref name="matchedWithoutRecovery"/>): a resurrected launch that was not
        /// recovered after the rewind point. The caller's summary line names that skip.
        /// </summary>
        internal static List<ResurrectedRecovery> Classify(
            IReadOnlyList<(uint pid, string guid)> survivingIdentities,
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyList<GameAction> ledgerActions,
            double retireCutoffUT,
            out int matchedWithoutRecovery)
        {
            matchedWithoutRecovery = 0;
            var result = new List<ResurrectedRecovery>();
            if (survivingIdentities == null || survivingIdentities.Count == 0) return result;
            if (committedRecordings == null || committedRecordings.Count == 0) return result;
            if (ledgerActions == null || ledgerActions.Count == 0) return result;
            if (double.IsNaN(retireCutoffUT)) return result;

            var claimedRecordingIds = new HashSet<string>(StringComparer.Ordinal);

            for (int r = 0; r < committedRecordings.Count; r++)
            {
                var rec = committedRecordings[r];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) continue;
                if (claimedRecordingIds.Contains(rec.RecordingId)) continue;
                bool terminalRecovered = rec.TerminalStateValue.HasValue
                    && rec.TerminalStateValue.Value == TerminalState.Recovered;

                uint matchedPid = 0;
                for (int i = 0; i < survivingIdentities.Count; i++)
                {
                    var identity = survivingIdentities[i];
                    if (IsPositivelySameLaunch(rec, identity.pid, identity.guid))
                    {
                        matchedPid = identity.pid;
                        break;
                    }
                }
                if (matchedPid == 0) continue;

                // Anchors: this recording's recovery funds rows after the cutoff.
                var anchorUTs = new List<double>();
                var retired = new List<string>();
                for (int a = 0; a < ledgerActions.Count; a++)
                {
                    var action = ledgerActions[a];
                    if (action == null || string.IsNullOrEmpty(action.ActionId)) continue;
                    if (!RecoveredRecordingEvidence.IsRecoveryFundsRow(
                            action, rec.RecordingId, retireCutoffUT))
                        continue;

                    anchorUTs.Add(action.UT);
                    retired.Add(action.ActionId);
                }

                // Crew-close rows: evidence of a post-commit recovery, never retired (see the
                // KerbalRecovered remarks on Classify).
                var crewCloseUTs = new List<double>();
                for (int a = 0; a < ledgerActions.Count; a++)
                {
                    var action = ledgerActions[a];
                    if (!RecoveredRecordingEvidence.IsCrewCloseRow(
                            action, rec.RecordingId, retireCutoffUT))
                        continue;
                    crewCloseUTs.Add(action.UT);
                }

                if (!terminalRecovered && !VesselOutlivedTerminal(rec.TerminalStateValue))
                {
                    // Destroyed / Docked / Boarded / Disassembled: the recorded vessel no
                    // longer existed to be recovered, so a recovery row on it is a
                    // misattribution. Leaving it banked is the pro-player direction.
                    matchedWithoutRecovery++;
                    continue;
                }

                if (!terminalRecovered && anchorUTs.Count == 0 && crewCloseUTs.Count == 0)
                {
                    // The survivor IS this launch, but nothing recovered it after the rewind
                    // point: a Landed / Orbiting flight whose vessel simply lives on.
                    matchedWithoutRecovery++;
                    continue;
                }

                bool usedFallbackAnchor = false;
                if (anchorUTs.Count == 0)
                {
                    if (crewCloseUTs.Count > 0)
                    {
                        // Zero-value crewed recovery after the commit: the crew-close row
                        // carries the recovery's own UT.
                        anchorUTs.AddRange(crewCloseUTs);
                    }
                    else
                    {
                        // Zero-value recovery of a Recovered-terminal recording: no funds row
                        // exists to anchor on. The recording's own end is when the recovery
                        // happened.
                        if (!(rec.EndUT > retireCutoffUT)) continue;
                        anchorUTs.Add(rec.EndUT);
                        usedFallbackAnchor = true;
                    }
                }

                // Bundled science: same recording, and RECOVERED rather than transmitted.
                // See the Method remarks on Classify for why the UT is not consulted.
                for (int a = 0; a < ledgerActions.Count; a++)
                {
                    var action = ledgerActions[a];
                    if (action == null || string.IsNullOrEmpty(action.ActionId)) continue;
                    if (!string.Equals(action.RecordingId, rec.RecordingId, StringComparison.Ordinal))
                        continue;
                    if (action.Type != GameActionType.ScienceEarning) continue;
                    if (action.Method != ScienceMethod.Recovered) continue;

                    if (!retired.Contains(action.ActionId))
                        retired.Add(action.ActionId);
                }

                // Bundled crew: same recording, end state Recovered. Matched by end state
                // rather than UT — the crew's disposition IS the recovery.
                for (int a = 0; a < ledgerActions.Count; a++)
                {
                    var action = ledgerActions[a];
                    if (action == null || string.IsNullOrEmpty(action.ActionId)) continue;
                    if (!string.Equals(action.RecordingId, rec.RecordingId, StringComparison.Ordinal))
                        continue;
                    if (action.Type != GameActionType.KerbalAssignment) continue;
                    if (action.KerbalEndStateField != KerbalEndState.Recovered) continue;

                    if (!retired.Contains(action.ActionId))
                        retired.Add(action.ActionId);
                }

                // Bundled experience: same recording, every KerbalExperience row. Such a row
                // EXISTS only because a recovery archived the crew's flight log, and a
                // recording has at most one recovery — so recording membership alone is the
                // right match and no UT window is needed. Retiring it is load-bearing: the
                // crew are back in flight in a world where that recovery has not happened, and
                // leaving the row would let the monotone roster re-assert put the recovery
                // flight's experience back onto their careers anyway.
                for (int a = 0; a < ledgerActions.Count; a++)
                {
                    var action = ledgerActions[a];
                    if (action == null || string.IsNullOrEmpty(action.ActionId)) continue;
                    if (!string.Equals(action.RecordingId, rec.RecordingId, StringComparison.Ordinal))
                        continue;
                    if (action.Type != GameActionType.KerbalExperience) continue;

                    if (!retired.Contains(action.ActionId))
                        retired.Add(action.ActionId);
                }

                if (retired.Count == 0) continue;

                double earliestAnchor = anchorUTs[0];
                for (int k = 1; k < anchorUTs.Count; k++)
                {
                    if (anchorUTs[k] < earliestAnchor)
                        earliestAnchor = anchorUTs[k];
                }

                claimedRecordingIds.Add(rec.RecordingId);
                result.Add(new ResurrectedRecovery
                {
                    RecordingId = rec.RecordingId,
                    LiveVesselPid = matchedPid,
                    AnchorUT = earliestAnchor,
                    UsedFallbackAnchor = usedFallbackAnchor,
                    Evidence = terminalRecovered ? EvidenceTerminal : EvidenceLedgerRow,
                    RetiredActionIds = retired
                });
            }

            return result;
        }
    }
}
