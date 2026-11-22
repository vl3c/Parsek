using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// What <see cref="PredictedEndingRecovery.DecideRecovery"/> concluded for one committed
    /// recording when the player recovered a real vessel (owner ruling Q13).
    /// </summary>
    internal enum PredictedEndingRecoveryOutcome
    {
        // Not a crash or chute touchdown Parsek predicted, and not a confirmed crash either (a
        // predicted final orbit is neither): the recovery row writers alone handle it.
        NotPredictedEnding,
        // The recording carries no launch guid; a bare pid never identifies the vessel.
        NoLaunchGuid,
        // The recovered vessel is another launch (or its guid is unknown).
        NotMatchingLaunch,
        // A crash KSP confirmed is history and stays final.
        ConfirmedEnding,
        // A live recording session owns the tree (the flight's active tree, the pending tree):
        // its own commit decides the ending.
        OwnedElsewhere,
        // A predicted chute touchdown recovered at or after its touchdown UT: the replay reached
        // the touchdown, so this is an ordinary landed recovery (the row writers handle it). A
        // predicted crash never reads this (owner ruling Q13b).
        AfterPredictedEnd,
        // Before the recording's last real sample: not the vessel of this timeline's flight.
        BeforeRecordedFlight,
        // Stock still had the vessel and the player recovered it: the recording follows.
        Recover,
    }

    /// <summary>
    /// Owner rulings Q13 / Q13b: "a player recovering a stage after its predicted crash was
    /// committed revives that crew; predicted endings only, a confirmed crash stays final", and
    /// any recovery of the launch-matched vessel overrides a predicted Destroyed, before or after
    /// its predicted UT (the vessel survived: stock opened its chutes and landed it after the
    /// drag-free crash UT, say). Parsek commits a PREDICTED crash or chute touchdown for a vessel
    /// KSP may still hold; when the player recovers that vessel (Tracking Station, KSC, the
    /// post-flight recovery), the recovery is stock's real outcome and the committed recording
    /// must follow it (owner principle P-STOCK). A predicted touchdown follows a recovery only
    /// before its touchdown UT (after it, the replay already landed it).
    /// <para>Pure over <see cref="Recording"/>: the decision and the change to one recording.
    /// The committed-history half (clone the tree, commit it over the committed one, file its
    /// ledger rows) is <c>ParsekScenario.ApplyRecoveryOfCommittedPredictions</c>, so this
    /// class stays out of the type knot.</para>
    /// </summary>
    internal static class PredictedEndingRecovery
    {
        internal const string LogTag = "PredictedEndingRecovery";

        /// <summary>
        /// Slack for "recovered before the recording's last real sample": the sample and the
        /// stock clock are read at different moments of one scene change.
        /// </summary>
        internal const double LastRealToleranceSeconds = 1.0;

        /// <summary>
        /// Pure: the decision for one committed recording. The player recovered the real
        /// vessel with launch guid <paramref name="liveGuid"/> at <paramref name="recoveryUT"/>;
        /// a recording of that launch (launch guid only, never a bare pid) that ends in a crash
        /// Parsek only PREDICTED (at any UT after its last real sample, owner ruling Q13b) or a
        /// chute touchdown Parsek only predicted (after its last real sample and before the
        /// touchdown UT), follows the recovery
        /// (<see cref="PredictedEndingRecoveryOutcome.Recover"/>). Rows checked in the
        /// declaration order of <see cref="PredictedEndingRecoveryOutcome"/>.
        /// </summary>
        internal static PredictedEndingRecoveryOutcome DecideRecovery(
            Recording rec, string liveGuid, double recoveryUT, bool ownedElsewhere)
        {
            if (rec == null || !rec.TerminalStateValue.HasValue || !IsFinite(recoveryUT))
                return PredictedEndingRecoveryOutcome.NotPredictedEnding;
            if (rec.TerminalStateValue.Value != TerminalState.Destroyed
                && !PredictedTouchdownStockFate.IsPredictedTouchdown(rec))
                return PredictedEndingRecoveryOutcome.NotPredictedEnding;
            string recGuid = PredictedTouchdownStockFate.UsableLaunchGuid(rec);
            if (recGuid == null)
                return PredictedEndingRecoveryOutcome.NoLaunchGuid;
            string live = VesselLaunchIdentity.NormalizeGuid(liveGuid);
            if (live == null || !string.Equals(recGuid, live, StringComparison.OrdinalIgnoreCase))
                return PredictedEndingRecoveryOutcome.NotMatchingLaunch;
            if (!rec.TerminalPredicted)
                return PredictedEndingRecoveryOutcome.ConfirmedEnding;
            if (ownedElsewhere)
                return PredictedEndingRecoveryOutcome.OwnedElsewhere;
            if (!IsRecoveryInsidePredictedEnding(rec, recoveryUT))
                return PredictedEndingRecoveryOutcome.AfterPredictedEnd;
            if (PredictedEndingTakeover.TryGetLastRealUT(rec, out double lastRealUT)
                && IsFinite(lastRealUT)
                && recoveryUT + LastRealToleranceSeconds < lastRealUT)
                return PredictedEndingRecoveryOutcome.BeforeRecordedFlight;
            return PredictedEndingRecoveryOutcome.Recover;
        }

        /// <summary>
        /// Pure: does a recovery at <paramref name="recoveryUT"/> still contradict the predicted
        /// ending? A predicted crash always does (owner ruling Q13b: the vessel the player
        /// recovered survived whatever the drag-free arc said); a predicted touchdown only before
        /// its touchdown UT.
        /// </summary>
        internal static bool IsRecoveryInsidePredictedEnding(Recording rec, double recoveryUT)
        {
            if (rec == null || !IsFinite(recoveryUT))
                return false;
            if (rec.TerminalStateValue == TerminalState.Destroyed && rec.TerminalPredicted)
                return true;
            return recoveryUT < rec.EndUT;
        }

        /// <summary>
        /// The recordings of <paramref name="effectiveRecordings"/> the recovery converts, in
        /// list order (decided over the whole list before anything is committed, because each
        /// commit replaces list entries). One Info line per refusal that named the recovered
        /// launch (a confirmed crash, a touchdown recovered at or after its touchdown UT, a live
        /// session's tree, a recovery before the recorded flight); nothing for other launches.
        /// </summary>
        internal static List<Recording> SelectRecoveryTargets(
            IReadOnlyList<Recording> effectiveRecordings, string liveGuid, double recoveryUT,
            Func<Recording, bool> ownedElsewhere, string context)
        {
            var targets = new List<Recording>();
            if (effectiveRecordings == null)
                return targets;
            for (int i = 0; i < effectiveRecordings.Count; i++)
            {
                Recording rec = effectiveRecordings[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                bool owned = ownedElsewhere != null && ownedElsewhere(rec);
                PredictedEndingRecoveryOutcome outcome = DecideRecovery(rec, liveGuid, recoveryUT, owned);
                if (outcome == PredictedEndingRecoveryOutcome.Recover)
                {
                    targets.Add(rec);
                    continue;
                }
                if (outcome == PredictedEndingRecoveryOutcome.ConfirmedEnding
                    || outcome == PredictedEndingRecoveryOutcome.OwnedElsewhere
                    || outcome == PredictedEndingRecoveryOutcome.AfterPredictedEnd
                    || outcome == PredictedEndingRecoveryOutcome.BeforeRecordedFlight)
                {
                    ParsekLog.Info(LogTag,
                        string.Format(CultureInfo.InvariantCulture,
                            "Recovery of a committed ending left as committed: context={0} rec={1} vessel='{2}' " +
                            "terminal={3} predicted={4} endUT={5} recoveryUT={6} reason={7}",
                            context ?? "(none)",
                            rec.RecordingId,
                            rec.VesselName ?? "",
                            rec.TerminalStateValue.HasValue ? rec.TerminalStateValue.Value.ToString() : "null",
                            rec.TerminalPredicted ? "true" : "false",
                            FormatUT(rec.EndUT),
                            FormatUT(recoveryUT),
                            outcome));
                }
            }
            return targets;
        }

        /// <summary>
        /// Pure over the recording (call it on a CLONE of the committed copy): drops the
        /// predicted ending as a takeover at the recovery does (the predicted tail goes, the
        /// crew answer the forecast decided is cleared), then ends the recording Recovered at
        /// <paramref name="recoveryUT"/> as a recovery stamps a recording not yet committed
        /// (explicit end at the recovery, vessel snapshot dropped, the Recovered stamp re-infers
        /// the crew as recovered). <paramref name="recoveredCrew"/> is the recovered vessel's
        /// crew (null: not known), carried on the stamp so a kerbal stock recovered reads
        /// Recovered whatever seat he started in. False (only the drop's refusal touched nothing)
        /// when the recording is not a predicted crash or touchdown, or a touchdown is recovered
        /// at or after its touchdown UT. A crash recovered after its predicted UT drops the whole
        /// predicted tail (the cut is the last real sample: stock's own path after it is not
        /// recorded) and ends at the recovery.
        /// </summary>
        internal static bool ConvertToRecovered(
            Recording rec, double recoveryUT, string context, out PredictedEndingDropResult drop,
            out string reason, IEnumerable<string> recoveredCrew = null)
        {
            drop = default(PredictedEndingDropResult);
            reason = null;
            if (rec == null)
            {
                reason = "no-recording";
                return false;
            }
            bool touchdown = PredictedTouchdownStockFate.IsPredictedTouchdown(rec);
            if (!touchdown
                && (rec.TerminalStateValue != TerminalState.Destroyed || !rec.TerminalPredicted))
            {
                reason = "not-a-predicted-ending";
                return false;
            }
            if (!IsRecoveryInsidePredictedEnding(rec, recoveryUT))
            {
                reason = "after-predicted-end";
                return false;
            }
            drop = PredictedEndingTakeover.DropPredictedEndingForTakeover(rec, recoveryUT, context);
            if (!drop.Dropped)
            {
                reason = "drop-refused:" + (drop.Reason ?? "(none)");
                return false;
            }
            rec.VesselDestroyed = false;
            rec.StockHeldVesselUT = double.NaN;
            rec.ExplicitEndUT = recoveryUT;
            rec.VesselSnapshot = null;
            rec.StampTerminalState(TerminalState.Recovered, context ?? LogTag, recoveredCrew: recoveredCrew);
            RecordingEndpointResolver.RefreshEndpointDecision(rec, context, logDecision: false);
            rec.MarkFilesDirty();
            return true;
        }

        /// <summary>The applied line (one per converted recording).</summary>
        internal static void LogApplied(
            string context, string treeId, Recording rec, TerminalState? previous, bool touchdown,
            double predictedEndUT, double recoveryUT, double cutUT)
        {
            ParsekLog.Info(LogTag,
                string.Format(CultureInfo.InvariantCulture,
                    "Predicted {0} recovered by the player: context={1} tree={2} rec={3} vessel='{4}' " +
                    "previousTerminal={5} predictedEndUT={6} recoveryUT={7} cutUT={8} newTerminal={9} - " +
                    "stock still had the vessel and the recovery is what happened: the recording ends " +
                    "Recovered and its crew come home",
                    touchdown ? "touchdown" : "crash",
                    context ?? "(none)",
                    treeId ?? "(null)",
                    rec?.RecordingId ?? "(null)",
                    rec?.VesselName ?? "",
                    previous.HasValue ? previous.Value.ToString() : "null",
                    FormatUT(predictedEndUT),
                    FormatUT(recoveryUT),
                    FormatUT(cutUT),
                    rec != null && rec.TerminalStateValue.HasValue ? rec.TerminalStateValue.Value.ToString() : "null"));
        }

        /// <summary>The refusal line (Warn: a recovery Q13 wanted applied was not).</summary>
        internal static void LogRefused(string context, string treeId, string recordingId, string reason)
        {
            ParsekLog.Warn(LogTag,
                string.Format(CultureInfo.InvariantCulture,
                    "Predicted ending recovery not applied: context={0} tree={1} rec={2} reason={3}",
                    context ?? "(none)",
                    treeId ?? "(null)",
                    recordingId ?? "(null)",
                    reason ?? "(none)"));
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        private static string FormatUT(double v)
        {
            return IsFinite(v) ? v.ToString("F3", CultureInfo.InvariantCulture) : "NaN";
        }
    }
}
