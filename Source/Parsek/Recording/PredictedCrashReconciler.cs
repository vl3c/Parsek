using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// What <see cref="PredictedCrashReconciler.Decide"/> concluded for one committed recording
    /// that ends in a crash Parsek predicted (<see cref="Recording.TerminalPredicted"/>).
    /// </summary>
    internal enum PredictedCrashReconcileOutcome
    {
        // Not a predicted Destroyed ending: nothing to reconcile.
        NotPredictedCrash,
        // The recording carries no launch guid; a bare pid never identifies the vessel.
        NoLaunchGuid,
        // A live recording session owns the tree (the flight's active tree, the pending tree, a
        // Re-Fly session): its own commit decides the ending.
        OwnedElsewhere,
        // The scene has not run long enough for stock's deletion check to have had its chance.
        SceneNotSettled,
        // The clock is not yet past the predicted crash (plus the margin).
        NotDueYet,
        // Stock removed the vessel: the prediction stands, nothing changes.
        VesselGone,
        // The player flies the vessel: the takeover path (owner ruling Q1b) owns it.
        VesselIsActive,
        // The vessel is loaded and unpacked: stock physics is deciding right now.
        StockStillSimulating,
        // The vessel's trajectory still reaches the line where stock deletes it.
        StockMayStillRemove,
        // Contradicted on this pass; acted on once a later pass sees it again.
        AwaitingConfirmation,
        // Stock kept the vessel past the predicted crash: the prediction was wrong.
        Contradicted,
    }

    /// <summary>The recording half of a reconcile decision (no live reads).</summary>
    internal struct PredictedCrashCandidateFacts
    {
        public bool IsPredictedCrash;
        public bool HasLaunchGuid;
        public bool OwnedElsewhere;
        public double PredictedCrashUT;
    }

    /// <summary>
    /// The live vessel half of a reconcile decision: what stock holds for the recording's launch
    /// guid. <see cref="StockRemovalAltitude"/> is the altitude under which stock deletes a packed
    /// vessel (the body's 1 kPa line, <see cref="OrbitClearance.ResolveDeletionAltitude"/>; 0 for
    /// an airless body, where an unloaded vessel is deleted below the datum).
    /// </summary>
    internal struct PredictedCrashLiveFacts
    {
        public bool Found;
        public bool IsActiveVessel;
        public bool LandedOrSplashed;
        public bool InPhysics;
        public double Eccentricity;
        public double PeriapsisAltitude;
        public bool Descending;
        public double StockRemovalAltitude;
    }

    /// <summary>
    /// One pass of <see cref="PredictedCrashReconciler.EvaluatePass"/>: the contradicted
    /// recordings to act on, the per-outcome counts and when the next pass is due.
    /// </summary>
    internal sealed class PredictedCrashReconcilePassResult
    {
        public readonly List<Recording> Contradicted = new List<Recording>();
        public readonly List<Recording> Gone = new List<Recording>();
        public readonly Dictionary<PredictedCrashReconcileOutcome, int> Counts =
            new Dictionary<PredictedCrashReconcileOutcome, int>();
        public int Candidates;
        // The earliest predicted crash UT + margin among candidates not yet due.
        public double NextDueUT = double.PositiveInfinity;
        // A candidate stock may still remove, or one awaiting confirmation: look again soon.
        public bool NeedsRecheck;

        internal int Count(PredictedCrashReconcileOutcome outcome)
        {
            return Counts.TryGetValue(outcome, out int n) ? n : 0;
        }
    }

    /// <summary>What <see cref="PredictedCrashReconciler.ApplyContradiction"/> did.</summary>
    internal struct PredictedCrashContradictionResult
    {
        public bool Applied;
        public string Reason;
        public PredictedEndingDropResult Drop;
        public TerminalState? NewTerminal;
    }

    /// <summary>
    /// Owner rulings P-STOCK / P-LEAVE / Q1b: Parsek commits a PREDICTED crash for a vessel KSP
    /// may still hold, and stock decides what happens to it in the background
    /// (<c>Vessel.CheckKill</c>: a packed, non-active, not landed vessel is deleted above 1 kPa
    /// outside the active vessel's pack range or below the terrain, crew killed). The
    /// reconciler only ever moves Parsek toward stock:
    /// <list type="bullet">
    /// <item>Contradicted: in a scene where stock's check runs, the vessel with the recording's
    /// launch guid still exists past the predicted crash, settled, not still falling toward
    /// stock's deletion line, on two passes. The prediction was wrong: it is dropped as a
    /// takeover drops it (<see cref="PredictedEndingTakeover.DropPredictedEndingForTakeover"/>),
    /// the ending becomes what the live vessel is, and the change reaches committed history
    /// through the tree commit's same-id replace, which retires the predicted death's crew rows
    /// (<see cref="LedgerOrchestrator.RetireTakenOverPredictedCrashCrewRows"/>) before the
    /// ledger files the live ending's.</item>
    /// <item>Confirmed: stock removed the vessel (and stock's removal killed the crew, which the
    /// predicted Destroyed already says, owner ruling Q6c): nothing changes.</item>
    /// </list>
    /// <para>The decision is pure; the caller supplies the live facts (scene host in
    /// Controllers).</para>
    /// </summary>
    internal static class PredictedCrashReconciler
    {
        internal const string LogTag = "PredictedCrashReconcile";

        /// <summary>Game seconds past the predicted crash before a live vessel contradicts it.</summary>
        internal const double CrashMarginSeconds = 10.0;

        /// <summary>
        /// Real seconds between the two passes that must both see the contradiction. Stock's
        /// deletion check runs every physics step, so a vessel still there a few dozen steps
        /// later (at any warp) is one stock kept.
        /// </summary>
        internal const double ConfirmRealSeconds = 2.0;

        /// <summary>
        /// Pure: the reconcile decision for one recording. See
        /// <see cref="PredictedCrashReconcileOutcome"/> for the rows, checked in declaration order.
        /// </summary>
        internal static PredictedCrashReconcileOutcome Decide(
            PredictedCrashCandidateFacts candidate,
            PredictedCrashLiveFacts live,
            double nowUT,
            bool sceneSettled,
            bool observedBefore,
            double secondsSinceFirstObservation)
        {
            if (!candidate.IsPredictedCrash)
                return PredictedCrashReconcileOutcome.NotPredictedCrash;
            if (!candidate.HasLaunchGuid)
                return PredictedCrashReconcileOutcome.NoLaunchGuid;
            if (candidate.OwnedElsewhere)
                return PredictedCrashReconcileOutcome.OwnedElsewhere;
            if (!sceneSettled)
                return PredictedCrashReconcileOutcome.SceneNotSettled;
            if (!IsFinite(candidate.PredictedCrashUT) || !IsFinite(nowUT)
                || nowUT <= candidate.PredictedCrashUT + CrashMarginSeconds)
                return PredictedCrashReconcileOutcome.NotDueYet;
            if (!live.Found)
                return PredictedCrashReconcileOutcome.VesselGone;
            if (live.IsActiveVessel)
                return PredictedCrashReconcileOutcome.VesselIsActive;
            if (!live.LandedOrSplashed)
            {
                if (live.InPhysics)
                    return PredictedCrashReconcileOutcome.StockStillSimulating;
                if (TrajectoryReachesStockRemoval(
                        live.Eccentricity, live.PeriapsisAltitude, live.Descending, live.StockRemovalAltitude))
                    return PredictedCrashReconcileOutcome.StockMayStillRemove;
            }
            if (!observedBefore || !(secondsSinceFirstObservation >= ConfirmRealSeconds))
                return PredictedCrashReconcileOutcome.AwaitingConfirmation;
            return PredictedCrashReconcileOutcome.Contradicted;
        }

        /// <summary>
        /// Pure: does a vessel on this orbit still reach the altitude under which stock deletes
        /// it? A low point at or under the line does on a bound orbit (it comes round again) and
        /// on an unbound one before its periapsis; an unreadable orbit counts as reaching it, so
        /// the prediction stays until the evidence is clear.
        /// </summary>
        internal static bool TrajectoryReachesStockRemoval(
            double eccentricity, double periapsisAltitude, bool descending, double stockRemovalAltitude)
        {
            return PredictedTouchdownStockFate.TrajectoryReachesStockRemoval(
                eccentricity, periapsisAltitude, descending, stockRemovalAltitude);
        }

        /// <summary>
        /// Pure: the recording half of the decision. <paramref name="isOwnedElsewhere"/> says
        /// whether a live session owns the recording's tree (null: nothing does).
        /// </summary>
        internal static PredictedCrashCandidateFacts BuildCandidate(
            Recording rec, Func<Recording, bool> isOwnedElsewhere)
        {
            var facts = new PredictedCrashCandidateFacts { PredictedCrashUT = double.NaN };
            if (rec == null)
                return facts;
            facts.IsPredictedCrash = rec.TerminalStateValue == TerminalState.Destroyed && rec.TerminalPredicted
                && !PredictedEndingTakeover.IsSurvivingBreakupRemnant(rec);
            if (!facts.IsPredictedCrash)
                return facts;
            facts.HasLaunchGuid = VesselLaunchIdentity.NormalizeGuid(rec.RecordedVesselGuid) != null
                && !string.Equals(VesselLaunchIdentity.NormalizeGuid(rec.RecordedVesselGuid),
                    Guid.Empty.ToString("N", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
            facts.OwnedElsewhere = isOwnedElsewhere != null && isOwnedElsewhere(rec);
            facts.PredictedCrashUT = rec.EndUT;
            return facts;
        }

        /// <summary>
        /// Pure over its inputs: one reconcile pass over <paramref name="recordings"/> (the
        /// effective committed set). <paramref name="liveLookup"/> answers the live facts for a
        /// recording's launch guid and is called only for a candidate that is due.
        /// <paramref name="firstObserved"/> holds, per recording id, the real time a pass first
        /// saw the contradiction; this pass adds new sightings and drops recordings no longer
        /// contradicted. Every recording id in <paramref name="skipIds"/> (one that failed to
        /// apply this scene) is left alone.
        /// </summary>
        internal static PredictedCrashReconcilePassResult EvaluatePass(
            IReadOnlyList<Recording> recordings,
            Func<Recording, bool> isOwnedElsewhere,
            Func<Recording, PredictedCrashLiveFacts> liveLookup,
            double nowUT,
            bool sceneSettled,
            double realtimeNow,
            Dictionary<string, double> firstObserved,
            ICollection<string> skipIds = null)
        {
            var result = new PredictedCrashReconcilePassResult();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (recordings != null)
            {
                for (int i = 0; i < recordings.Count; i++)
                {
                    Recording rec = recordings[i];
                    if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                        continue;
                    if (skipIds != null && skipIds.Contains(rec.RecordingId))
                        continue;
                    PredictedCrashCandidateFacts candidate = BuildCandidate(rec, isOwnedElsewhere);
                    if (!candidate.IsPredictedCrash)
                        continue;
                    result.Candidates++;

                    bool due = sceneSettled && IsFinite(candidate.PredictedCrashUT) && IsFinite(nowUT)
                        && nowUT > candidate.PredictedCrashUT + CrashMarginSeconds;
                    PredictedCrashLiveFacts live = due && candidate.HasLaunchGuid && !candidate.OwnedElsewhere
                        && liveLookup != null
                        ? liveLookup(rec)
                        : default(PredictedCrashLiveFacts);
                    bool observed = firstObserved != null
                        && firstObserved.TryGetValue(rec.RecordingId, out double firstSeen);
                    double since = observed ? realtimeNow - firstObserved[rec.RecordingId] : double.NaN;

                    PredictedCrashReconcileOutcome outcome = Decide(
                        candidate, live, nowUT, sceneSettled, observed, since);
                    result.Counts[outcome] = result.Count(outcome) + 1;

                    switch (outcome)
                    {
                        case PredictedCrashReconcileOutcome.NotDueYet:
                            if (IsFinite(candidate.PredictedCrashUT))
                                result.NextDueUT = Math.Min(
                                    result.NextDueUT, candidate.PredictedCrashUT + CrashMarginSeconds);
                            break;
                        case PredictedCrashReconcileOutcome.StockStillSimulating:
                        case PredictedCrashReconcileOutcome.StockMayStillRemove:
                            result.NeedsRecheck = true;
                            break;
                        case PredictedCrashReconcileOutcome.AwaitingConfirmation:
                            result.NeedsRecheck = true;
                            seen.Add(rec.RecordingId);
                            if (firstObserved != null && !observed)
                                firstObserved[rec.RecordingId] = realtimeNow;
                            break;
                        case PredictedCrashReconcileOutcome.Contradicted:
                            seen.Add(rec.RecordingId);
                            result.Contradicted.Add(rec);
                            break;
                        case PredictedCrashReconcileOutcome.VesselGone:
                            result.Gone.Add(rec);
                            break;
                    }
                }
            }

            if (firstObserved != null && firstObserved.Count > 0)
            {
                var stale = new List<string>();
                foreach (string id in firstObserved.Keys)
                    if (!seen.Contains(id))
                        stale.Add(id);
                for (int i = 0; i < stale.Count; i++)
                    firstObserved.Remove(stale[i]);
            }
            return result;
        }

        /// <summary>
        /// The committed-history half of a contradiction: deep-clones
        /// <paramref name="committedTree"/>, drops the predicted ending of
        /// <paramref name="recordingId"/> on the clone as a takeover at
        /// <paramref name="nowUT"/> does (coast rule included), stamps the ending the live vessel
        /// has (<paramref name="stampLiveEnding"/>; it may leave the terminal empty), commits the
        /// clone over the committed tree (same-id replace, which retires the predicted death's crew
        /// rows) and files the tree's ledger rows. The committed tree is untouched when anything
        /// before the commit refuses. Logs one Info line when it applied.
        /// </summary>
        internal static PredictedCrashContradictionResult ApplyContradiction(
            RecordingTree committedTree,
            string recordingId,
            double nowUT,
            TakeoverCoastEvidence? coastEvidence,
            Action<Recording> stampLiveEnding,
            string context)
        {
            var result = new PredictedCrashContradictionResult();
            if (committedTree?.Recordings == null || string.IsNullOrEmpty(recordingId)
                || !committedTree.Recordings.TryGetValue(recordingId, out Recording committedRec)
                || committedRec == null)
            {
                result.Reason = "recording-not-in-tree";
                return LogRefused(result, committedTree, recordingId, context);
            }
            bool predictedTouchdown = PredictedTouchdownStockFate.IsPredictedTouchdown(committedRec);
            if (!predictedTouchdown
                && (committedRec.TerminalStateValue != TerminalState.Destroyed || !committedRec.TerminalPredicted))
            {
                result.Reason = "not-a-predicted-crash";
                return LogRefused(result, committedTree, recordingId, context);
            }

            RecordingTree clone = RecordingTree.DeepClone(committedTree);
            if (clone?.Recordings == null
                || !clone.Recordings.TryGetValue(recordingId, out Recording rec) || rec == null)
            {
                result.Reason = "clone-failed";
                return LogRefused(result, committedTree, recordingId, context);
            }

            // A touchdown stock contradicted (the craft still in the air past it) never keeps
            // the predicted descent the way a takeover after the touchdown does.
            result.Drop = PredictedEndingTakeover.DropPredictedEndingForTakeover(
                rec, nowUT, context, coastEvidence, predictionContradicted: predictedTouchdown);
            if (!result.Drop.Dropped)
            {
                result.Reason = "drop-refused:" + (result.Drop.Reason ?? "(none)");
                return LogRefused(result, committedTree, recordingId, context);
            }

            stampLiveEnding?.Invoke(rec);
            RecordingEndpointResolver.RefreshEndpointDecision(rec, context, logDecision: false);
            result.NewTerminal = rec.TerminalStateValue;

            if (!TreeCommitService.CommitTree(clone))
            {
                result.Reason = "commit-refused";
                return LogRefused(result, committedTree, recordingId, context);
            }
            RecordingTree committed = TreeCommitService.ResolveCommittedTreeAfterCommit(clone, context);
            LedgerOrchestrator.NotifyLedgerTreeCommitted(committed);

            result.Applied = true;
            result.Reason = "applied";
            ParsekLog.Info(LogTag,
                string.Format(CultureInfo.InvariantCulture,
                    "Predicted {9} contradicted by stock: context={0} tree={1} rec={2} vessel='{3}' " +
                    "predictedCrashUT={4} nowUT={5} cutUT={6} coast={7} newTerminal={8} - " +
                    "stock kept the vessel, the prediction is dropped and the ending follows the live vessel",
                    context ?? "(none)",
                    committedTree.Id ?? "(null)",
                    recordingId,
                    rec.VesselName ?? "",
                    FormatUT(result.Drop.PreviousExplicitEndUT),
                    FormatUT(nowUT),
                    FormatUT(result.Drop.CutUT),
                    result.Drop.CoastKeptToTakeover ? "kept-to-takeover" : "cut-at-last-real",
                    result.NewTerminal.HasValue ? result.NewTerminal.Value.ToString() : "null",
                    predictedTouchdown ? "touchdown" : "crash"));
            return result;
        }

        /// <summary>
        /// The committed-history half of a predicted touchdown (or a predicted final orbit, owner
        /// ruling Q8) stock deleted (owner rulings P-STOCK / Q4d,
        /// <see cref="PredictedTouchdownOutcome.StockDeleted"/>): deep-clones
        /// <paramref name="committedTree"/>, turns the recording on the clone into stock's
        /// verdict (<see cref="PredictedTouchdownStockFate.ConvertToStockDeleted"/>: Destroyed,
        /// confirmed, crew dead, a quiet end after the chute descent), commits the clone over
        /// the committed tree (same-id replace, which retires the touchdown's Aboard crew rows,
        /// <see cref="LedgerOrchestrator.ShouldRefreshCrewRowsOnReplace"/>) and files the tree's
        /// ledger rows (the Dead ones). The separation stays open for a Re-Fly as for any crash.
        /// The committed tree is untouched when anything before the commit refuses.
        /// </summary>
        internal static PredictedCrashContradictionResult ApplyStockDeletion(
            RecordingTree committedTree,
            string recordingId,
            double nowUT,
            string context)
        {
            var result = new PredictedCrashContradictionResult();
            if (committedTree?.Recordings == null || string.IsNullOrEmpty(recordingId)
                || !committedTree.Recordings.TryGetValue(recordingId, out Recording committedRec)
                || committedRec == null)
            {
                result.Reason = "recording-not-in-tree";
                return LogDeletionRefused(result, committedTree, recordingId, context);
            }
            if (!PredictedTouchdownStockFate.IsStockFatePrediction(committedRec))
            {
                result.Reason = "not-a-predicted-touchdown";
                return LogDeletionRefused(result, committedTree, recordingId, context);
            }

            RecordingTree clone = RecordingTree.DeepClone(committedTree);
            if (clone?.Recordings == null
                || !clone.Recordings.TryGetValue(recordingId, out Recording rec) || rec == null)
            {
                result.Reason = "clone-failed";
                return LogDeletionRefused(result, committedTree, recordingId, context);
            }

            double touchdownUT = rec.EndUT;
            TerminalState? previous = rec.TerminalStateValue;
            string kind = PredictedTouchdownStockFate.KindLabel(rec);
            if (!PredictedTouchdownStockFate.ConvertToStockDeleted(rec, context))
            {
                result.Reason = "convert-refused";
                return LogDeletionRefused(result, committedTree, recordingId, context);
            }
            result.NewTerminal = rec.TerminalStateValue;

            if (!TreeCommitService.CommitTree(clone))
            {
                result.Reason = "commit-refused";
                return LogDeletionRefused(result, committedTree, recordingId, context);
            }
            RecordingTree committed = TreeCommitService.ResolveCommittedTreeAfterCommit(clone, context);
            LedgerOrchestrator.NotifyLedgerTreeCommitted(committed);

            result.Applied = true;
            result.Reason = "applied";
            ParsekLog.Info(PredictedTouchdownStockFate.LogTag,
                string.Format(CultureInfo.InvariantCulture,
                    "Predicted {10} overruled by stock: context={0} tree={1} rec={2} vessel='{3}' " +
                    "previousTerminal={4} touchdownUT={5} nowUT={6} stockHeldUT={7} newTerminal={8} " +
                    "quietEnd={9} - stock removed the vessel it held with no recovery (crew killed); the " +
                    "recording ends Destroyed as stock decided, its ghost ends quietly, nothing spawns",
                    context ?? "(none)",
                    committedTree.Id ?? "(null)",
                    recordingId,
                    rec.VesselName ?? "",
                    previous.HasValue ? previous.Value.ToString() : "null",
                    FormatUT(touchdownUT),
                    FormatUT(nowUT),
                    FormatUT(rec.StockHeldVesselUT),
                    result.NewTerminal.HasValue ? result.NewTerminal.Value.ToString() : "null",
                    rec.QuietDestroyedEnd ? "true" : "false",
                    kind));
            return result;
        }

        private static PredictedCrashContradictionResult LogDeletionRefused(
            PredictedCrashContradictionResult result, RecordingTree tree, string recordingId, string context)
        {
            ParsekLog.Warn(PredictedTouchdownStockFate.LogTag,
                string.Format(CultureInfo.InvariantCulture,
                    "Predicted touchdown stock deletion not applied: context={0} tree={1} rec={2} reason={3}",
                    context ?? "(none)",
                    tree?.Id ?? "(null)",
                    recordingId ?? "(null)",
                    result.Reason ?? "(none)"));
            return result;
        }

        private static PredictedCrashContradictionResult LogRefused(
            PredictedCrashContradictionResult result, RecordingTree tree, string recordingId, string context)
        {
            ParsekLog.Warn(LogTag,
                string.Format(CultureInfo.InvariantCulture,
                    "Predicted crash contradiction not applied: context={0} tree={1} rec={2} reason={3}",
                    context ?? "(none)",
                    tree?.Id ?? "(null)",
                    recordingId ?? "(null)",
                    result.Reason ?? "(none)"));
            return result;
        }

        /// <summary>One summary line for a pass (Info when it acted, Verbose otherwise).</summary>
        internal static string FormatPassSummary(
            PredictedCrashReconcilePassResult pass, string scene, double nowUT, int applied, int failed)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Pass: scene={0} nowUT={1} candidates={2} contradicted={3} applied={4} failed={5} " +
                "awaitingConfirmation={6} stockMayStillRemove={7} stockStillSimulating={8} " +
                "vesselGone={9} vesselIsActive={10} notDueYet={11} ownedElsewhere={12} noLaunchGuid={13} " +
                "nextDueUT={14} recheck={15}",
                scene ?? "(none)",
                FormatUT(nowUT),
                pass.Candidates,
                pass.Contradicted.Count,
                applied,
                failed,
                pass.Count(PredictedCrashReconcileOutcome.AwaitingConfirmation),
                pass.Count(PredictedCrashReconcileOutcome.StockMayStillRemove),
                pass.Count(PredictedCrashReconcileOutcome.StockStillSimulating),
                pass.Count(PredictedCrashReconcileOutcome.VesselGone),
                pass.Count(PredictedCrashReconcileOutcome.VesselIsActive),
                pass.Count(PredictedCrashReconcileOutcome.NotDueYet),
                pass.Count(PredictedCrashReconcileOutcome.OwnedElsewhere),
                pass.Count(PredictedCrashReconcileOutcome.NoLaunchGuid),
                FormatUT(pass.NextDueUT),
                pass.NeedsRecheck ? "true" : "false");
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
