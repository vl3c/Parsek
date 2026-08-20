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

            /// <summary>ActionIds to tombstone, in ledger order.</summary>
            public List<string> RetiredActionIds = new List<string>();
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
        /// A recording qualifies when: a surviving live vessel is POSITIVELY its launch
        /// (<see cref="IsPositivelySameLaunch"/>), the recording's terminal verdict is
        /// <see cref="TerminalState.Recovered"/>, and the recovery evidence lies after
        /// <paramref name="retireCutoffUT"/> (a recovery from BEFORE the rewind point is
        /// still true in the reverted world and must be left alone).
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
                if (!rec.TerminalStateValue.HasValue) continue;
                if (rec.TerminalStateValue.Value != TerminalState.Recovered) continue;

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
                    if (!string.Equals(action.RecordingId, rec.RecordingId, StringComparison.Ordinal))
                        continue;
                    if (action.Type != GameActionType.FundsEarning) continue;
                    if (action.FundsSource != FundsEarningSource.Recovery) continue;
                    if (!(action.UT > retireCutoffUT)) continue;

                    anchorUTs.Add(action.UT);
                    retired.Add(action.ActionId);
                }

                bool usedFallbackAnchor = false;
                if (anchorUTs.Count == 0)
                {
                    // Zero-value recovery: no funds row exists to anchor on. The recording's
                    // own end is when the recovery happened.
                    if (!(rec.EndUT > retireCutoffUT)) continue;
                    anchorUTs.Add(rec.EndUT);
                    usedFallbackAnchor = true;
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
                    RetiredActionIds = retired
                });
            }

            return result;
        }
    }
}
