// [ERS-exempt] The retire set is built from the raw ledger (ELS is the ledger
// minus tombstones, so reading it would hide the rows a merge must retire); see
// scripts/ers-els-audit-allowlist.txt.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Design section 2.12 (Re-Fly ledger safety): the retire set of a slot and the
    /// Rule 1 value gate. One builder (<see cref="BuildRetireSet"/>) computes the rows
    /// a merge would tombstone; <see cref="CommitTombstones"/> writes exactly those
    /// rows, and <see cref="RetireSetHoldsSealingRow(Recording, RewindPoint, ChildSlot, out string)"/>
    /// refuses a Re-Fly whose retire set holds any row that is not a loss row, so a
    /// merge can only ever retire value the player lost, never value the player
    /// gained.
    /// </summary>
    internal static partial class SupersedeCommit
    {
        /// <summary>
        /// Result of <see cref="BuildRetireSet"/>: the retire set in the order the
        /// merge tombstones it, plus the scope / eligibility counters
        /// <see cref="CommitTombstones"/> reports.
        /// </summary>
        internal sealed class RetireSetScan
        {
            internal readonly List<GameAction> Rows = new List<GameAction>();
            internal int PreRewindKept;
            internal int DeathIntervalsInScopeByEndUT;
            internal int DeathPenaltiesScreenedByDeath;
            internal int AlreadyTombstoned;
            internal int SeedExcluded;
            internal int RolloutExcluded;
            internal int OtherExcluded;
            internal int DeployedScienceExcluded;
        }

        /// <summary>
        /// Pure retire-set builder (design section 2.12 Terms, section 6.13): the rows
        /// of <paramref name="actions"/> tagged to a recording in
        /// <paramref name="closureIds"/> that the pre-rewind attribution guard does not
        /// keep as earned before <paramref name="rewindCutoffUT"/>, that carry an
        /// ActionId not already in <paramref name="alreadyTombstonedActionIds"/>, and
        /// that pass <see cref="TombstoneEligibility.IsSupersedeTombstoneEligible"/>.
        /// Rows are grouped by recording in first-seen order (the order the merge has
        /// always tombstoned them in). Breaking Ground deployed-experiment science is
        /// never in the set: the credit belongs to the ground station, not the flight
        /// (operator ruling 2026-09-26), so a legacy row an older build tagged to a
        /// flight is neither retired by a merge nor sealing.
        /// <paramref name="logDecisions"/> emits the per-row keep / skip diagnostics
        /// the merge logs; the gate passes false (it reads the set on every cache
        /// miss and logs one summary line of its own).
        /// </summary>
        internal static RetireSetScan BuildRetireSet(
            IReadOnlyCollection<string> closureIds,
            double rewindCutoffUT,
            IReadOnlyList<GameAction> actions,
            IEnumerable<string> alreadyTombstonedActionIds,
            bool logDecisions)
        {
            var scan = new RetireSetScan();
            if (closureIds == null || closureIds.Count == 0 || actions == null)
                return scan;

            var tombstoned = alreadyTombstonedActionIds != null
                ? new HashSet<string>(alreadyTombstonedActionIds, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            // TOMBSTONE-SCOPE-HAS-NO-UT-GUARD: subtree containment is an ATTRIBUTION
            // key, not a timeline key. A payout earned before the rewind point belongs
            // to the half of the flight this merge KEEPS, even when the recording it is
            // tagged to lands in the superseded subtree (the origin child brackets its
            // own pre-rewind UTs, and the splitter only carves HEAD out when the origin
            // genuinely spans the rewind UT).
            var subtreeSet = closureIds as HashSet<string>
                ?? new HashSet<string>(closureIds, StringComparer.Ordinal);
            var sliceByRecording = new Dictionary<string, List<GameAction>>(StringComparer.Ordinal);
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a == null) continue;
                if (!TombstoneAttributionHelper.InSupersedeScope(a, subtreeSet))
                    continue;
                double deathFloatStep;
                if (logDecisions
                    && TombstoneAttributionHelper.IsDeathEndUTWithinFloatStepOfCutoff(
                        a, rewindCutoffUT, out deathFloatStep))
                {
                    // TOMBSTONE-ENDUT-SCREEN-LOW-LIMITS (1), accepted 2026-09-23: the float
                    // EndUT cannot place this death on either side of the cutoff, so the
                    // screen below may be deciding on rounding. Warn, not Info: it is a
                    // real ambiguity in a career-state outcome.
                    ParsekLog.Warn(LedgerSwapTag,
                        $"PreRewindTombstoneGuard: death endUT within one float step of the cutoff " +
                        $"action={a.ActionId ?? "<no-id>"} kerbal={a.KerbalName ?? "<none>"} " +
                        $"rec={a.RecordingId} " +
                        $"endUT={((double)a.EndUT).ToString("R", CultureInfo.InvariantCulture)} " +
                        $"cutoffUT={rewindCutoffUT.ToString("R", CultureInfo.InvariantCulture)} " +
                        $"floatStep={deathFloatStep.ToString("R", CultureInfo.InvariantCulture)} " +
                        $"-> {(TombstoneAttributionHelper.IsPreRewindAttributedAction(a, rewindCutoffUT, actions) ? "kept" : "in scope")} " +
                        "(the screen may be deciding on float rounding)");
                }
                bool preRewind = TombstoneAttributionHelper.IsPreRewindAttributedAction(a, rewindCutoffUT, actions);
                if (!double.IsNaN(rewindCutoffUT)
                    && TombstoneAttributionHelper.IsKerbalDeathRepPenalty(a)
                    && !double.IsNaN(a.UT)
                    && preRewind != (a.UT < rewindCutoffUT))
                {
                    // DEATH-REP-PENALTY-CAN-LAND-ACROSS-A-SPLIT-CUT: the paired death
                    // decided the side of the cutoff, not the penalty's own UT.
                    scan.DeathPenaltiesScreenedByDeath++;
                    if (logDecisions && scan.DeathPenaltiesScreenedByDeath <= PreRewindKeepLogCap)
                    {
                        ParsekLog.Verbose(LedgerSwapTag,
                            $"PreRewindTombstoneGuard: KerbalDeath rep penalty screened by its paired death " +
                            $"action={a.ActionId ?? "<no-id>"} rec={a.RecordingId} " +
                            $"ut={a.UT.ToString("R", CultureInfo.InvariantCulture)} " +
                            $"attributionUT={TombstoneAttributionHelper.ComputeAttributionUT(a, actions).ToString("R", CultureInfo.InvariantCulture)} " +
                            $"cutoffUT={rewindCutoffUT.ToString("R", CultureInfo.InvariantCulture)} " +
                            $"-> {(preRewind ? "kept" : "in scope")}");
                    }
                }
                if (preRewind)
                {
                    scan.PreRewindKept++;
                    // Per-row identity is logged because WHICH payout the guard saved is
                    // the forensic question when a career reconciles differently than
                    // expected; the cap honours the batch convention and the summary line
                    // the caller writes is always complete.
                    if (logDecisions)
                    {
                        if (scan.PreRewindKept <= PreRewindKeepLogCap)
                        {
                            ParsekLog.Verbose(LedgerSwapTag,
                                $"PreRewindTombstoneGuard: keep action={a.ActionId ?? "<no-id>"} " +
                                $"type={a.Type} rec={a.RecordingId} " +
                                $"ut={a.UT.ToString("R", CultureInfo.InvariantCulture)} " +
                                $"attributionUT={TombstoneAttributionHelper.ComputeAttributionUT(a, actions).ToString("R", CultureInfo.InvariantCulture)} " +
                                $"cutoffUT={rewindCutoffUT.ToString("R", CultureInfo.InvariantCulture)}");
                        }
                        else if (scan.PreRewindKept == PreRewindKeepLogCap + 1)
                        {
                            ParsekLog.Verbose(LedgerSwapTag,
                                $"PreRewindTombstoneGuard: further per-row keeps suppressed " +
                                $"(cap={PreRewindKeepLogCap.ToString(CultureInfo.InvariantCulture)}); " +
                                $"see the summary line for the total");
                        }
                    }
                    continue;
                }
                if (!double.IsNaN(rewindCutoffUT)
                    && TombstoneAttributionHelper.IsDeathEncodingInterval(a)
                    && a.UT < rewindCutoffUT)
                {
                    // TOMBSTONE-GUARD-SCREENS-AN-INTERVAL-ACTION-BY-ITS-START: boarded
                    // before the cutoff, died at or after it; in scope by EndUT.
                    scan.DeathIntervalsInScopeByEndUT++;
                    if (logDecisions && scan.DeathIntervalsInScopeByEndUT <= PreRewindKeepLogCap)
                    {
                        ParsekLog.Verbose(LedgerSwapTag,
                            $"PreRewindTombstoneGuard: death interval screened by endUT " +
                            $"action={a.ActionId ?? "<no-id>"} kerbal={a.KerbalName ?? "<none>"} " +
                            $"rec={a.RecordingId} " +
                            $"startUT={a.UT.ToString("R", CultureInfo.InvariantCulture)} " +
                            $"endUT={((double)a.EndUT).ToString("R", CultureInfo.InvariantCulture)} " +
                            $"cutoffUT={rewindCutoffUT.ToString("R", CultureInfo.InvariantCulture)} " +
                            $"-> in scope");
                    }
                }
                List<GameAction> slice;
                if (!sliceByRecording.TryGetValue(a.RecordingId, out slice))
                {
                    slice = new List<GameAction>();
                    sliceByRecording[a.RecordingId] = slice;
                }
                slice.Add(a);
            }

            foreach (var kv in sliceByRecording)
            {
                var slice = kv.Value;
                for (int i = 0; i < slice.Count; i++)
                {
                    var a = slice[i];
                    // Every action carries an ActionId post-Phase-1 (construction or the
                    // legacy hash on load); a row without one cannot be tombstoned.
                    if (string.IsNullOrEmpty(a.ActionId))
                        continue;
                    if (tombstoned.Contains(a.ActionId))
                    {
                        scan.AlreadyTombstoned++;
                        if (logDecisions)
                        {
                            ParsekLog.Verbose(LedgerSwapTag,
                                $"Skip: action '{a.ActionId}' already tombstoned (idempotent re-entry)");
                        }
                        continue;
                    }

                    if (!TombstoneEligibility.IsSupersedeTombstoneEligible(a))
                    {
                        CountExcludedByType(a,
                            ref scan.SeedExcluded,
                            ref scan.RolloutExcluded,
                            ref scan.OtherExcluded);
                        continue;
                    }

                    if (IsDeployedStationScienceCredit(a))
                    {
                        scan.DeployedScienceExcluded++;
                        scan.OtherExcluded++;
                        if (logDecisions)
                        {
                            ParsekLog.Verbose(LedgerSwapTag,
                                $"Keep: action '{a.ActionId}' is deployed-experiment science " +
                                $"subject='{a.SubjectId}' rec={a.RecordingId} (a ground station's credit, " +
                                "never retired by a merge)");
                        }
                        continue;
                    }

                    scan.Rows.Add(a);
                    tombstoned.Add(a.ActionId);
                }
            }

            return scan;
        }

        /// <summary>
        /// True for a Breaking Ground deployed-experiment science row. The capture
        /// writes these untagged (operator ruling 2026-09-26: the credit belongs to the
        /// ground station, not to whichever vessel was flown); this keeps a row an
        /// older build tagged to a flight out of the retire set.
        /// </summary>
        internal static bool IsDeployedStationScienceCredit(GameAction action)
        {
            return action != null
                && action.Type == GameActionType.ScienceEarning
                && GameStateRecorder.IsDeployedScienceSubjectId(action.SubjectId);
        }

        /// <summary>
        /// Design section 2.12 Terms: a LOSS row only takes value from the player and is
        /// re-recorded by the replacement flight from its own events. The complete list:
        /// <c>KerbalAssignment</c> ending <c>Dead</c> / <c>Aboard</c> / <c>Unknown</c>,
        /// <c>ReputationPenalty</c> from <c>KerbalDeath</c> / <c>Other</c>, and
        /// <c>FacilityDestruction</c>. Every other row in a retire set is a SEALING row,
        /// including any type or source added later until it is reviewed into this list
        /// (the default arms return false on purpose).
        /// </summary>
        internal static bool IsLossRow(GameAction action)
        {
            if (action == null) return false;
            switch (action.Type)
            {
                case GameActionType.KerbalAssignment:
                    switch (action.KerbalEndStateField)
                    {
                        case KerbalEndState.Dead:
                        case KerbalEndState.Aboard:
                        case KerbalEndState.Unknown:
                            return true;
                        default:
                            return false;
                    }
                case GameActionType.ReputationPenalty:
                    switch (action.RepPenaltySource)
                    {
                        case ReputationPenaltySource.KerbalDeath:
                        case ReputationPenaltySource.Other:
                            return true;
                        default:
                            return false;
                    }
                case GameActionType.FacilityDestruction:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The rewind cutoff of <see cref="ComputeTombstoneRewindCutoffUT(ReFlySessionMarker)"/>
        /// read from the rewind point itself, for the gate that has a slot but no
        /// marker. The marker's <c>RewindPointUT</c> is stamped from <c>rp.UT</c> at
        /// invoke, so both give the same value for the same slot.
        /// </summary>
        internal static double ComputeTombstoneRewindCutoffUT(RewindPoint rp)
        {
            if (rp == null) return double.NaN;
            if (!double.IsNaN(rp.UT) && !double.IsInfinity(rp.UT) && rp.UT > 0.0)
                return rp.UT;
            return double.NaN;
        }

        /// <summary>
        /// The sealing row a refusal names: the first one that is not a zero-award
        /// milestone, else the first. Every row in the list seals equally; this only
        /// picks the one the player recognises (a first-reached milestone with no
        /// award sorts ahead of a lab earning at the same UT).
        /// </summary>
        internal static GameAction PickNamedSealingRow(IReadOnlyList<GameAction> sealing)
        {
            if (sealing == null || sealing.Count == 0) return null;
            for (int i = 0; i < sealing.Count; i++)
            {
                if (sealing[i] != null && !IsZeroAwardMilestone(sealing[i]))
                    return sealing[i];
            }
            return sealing[0];
        }

        internal static bool IsZeroAwardMilestone(GameAction action)
        {
            return action != null
                && action.Type == GameActionType.MilestoneAchievement
                && action.MilestoneFundsAwarded == 0f
                && action.MilestoneRepAwarded == 0f
                && action.MilestoneScienceAwarded == 0f;
        }

        /// <summary>Summary of a sealing row: <c>Type:ActionId</c>.</summary>
        internal static string FormatRetireSetRowSummary(GameAction action)
        {
            if (action == null) return "<null>";
            return action.Type + ":" + (action.ActionId ?? "<no-action-id>");
        }

        /// <summary>
        /// The recording the retire set is rooted at: <paramref name="subject"/> itself
        /// when it is a Re-Fly attempt not yet merged (NotCommitted: Site B-1 and the
        /// merge-dialog preview, where the attempt becomes the slot's tip), otherwise
        /// the slot's current effective tip (the same id <c>RewindInvoker</c> hands the
        /// merge as its supersede target), falling back to the subject when the tip
        /// does not resolve to a committed recording (a tree mid-commit).
        /// </summary>
        internal static Recording ResolveRetireSetTip(Recording subject, ChildSlot slot)
        {
            if (subject != null && subject.MergeState == MergeState.NotCommitted)
                return subject;
            if (slot != null)
            {
                var scenario = ParsekScenario.Instance;
                IReadOnlyList<RecordingSupersedeRelation> supersedes =
                    !object.ReferenceEquals(null, scenario) ? scenario.RecordingSupersedes : null;
                string tipId = slot.EffectiveRecordingId(supersedes);
                if (!string.IsNullOrEmpty(tipId))
                {
                    if (subject != null
                        && string.Equals(subject.RecordingId, tipId, StringComparison.Ordinal))
                        return subject;
                    Recording tip = EffectiveState.FindCommittedRecordingByIdRaw(tipId);
                    if (tip != null)
                        return tip;
                }
            }
            return subject;
        }

        /// <summary>
        /// The merge closure a merge into this slot would tombstone over if it ran now
        /// (design section 6.3 / 2.12): <see cref="EffectiveState.BuildSubtreeClosureUncached"/>
        /// rooted at <paramref name="tip"/> under a slot-scoped marker whose rewind
        /// cutoff is the rewind point's UT (the real marker stamps the same value, and
        /// <c>InvokedUT</c> lands on it when the quicksave loads). The live session's
        /// identity is carried so its own recordings are fenced exactly as the real
        /// merge fences them, unless <paramref name="tip"/> IS the live attempt. The
        /// tip's own chain members are added regardless of merge state: the attempt's
        /// optimizer halves are NotCommitted until the merge flips them, yet a later
        /// merge into the slot would read them as committed chain siblings. Last, the
        /// pre-rewind carve-outs AppendRelations keeps out of the tombstone subtree
        /// (pre-rewind debris, the pre-rewind chain head) are removed through the same
        /// <see cref="PreRewindCarveOutFilter"/>, so the gate never reads a row the merge
        /// would not retire.
        /// </summary>
        internal static HashSet<string> ComputeRetireClosure(
            Recording tip,
            string originRecordingId,
            string rewindPointId,
            double rewindCutoffUT)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (tip == null || string.IsNullOrEmpty(tip.RecordingId))
                return ids;

            var scenario = ParsekScenario.Instance;
            ReFlySessionMarker live = !object.ReferenceEquals(null, scenario)
                ? scenario.ActiveReFlySessionMarker
                : null;
            // A committed slot's retire set fences the live attempt out (it is not in
            // L, design 2.12 Invariant LS). An attempt's own retire set (the tip is the
            // NotCommitted attempt) fences nothing: naming the tip as the active
            // recording turns the session-owned walk off, while the session id still
            // lets the walk skip the attempt's NotCommitted halves quietly (the chain
            // union below adds them back).
            bool attemptTip = tip.MergeState == MergeState.NotCommitted;
            var slotMarker = new ReFlySessionMarker
            {
                SessionId = attemptTip
                    ? (live?.SessionId ?? tip.CreatingSessionId)
                    : live?.SessionId,
                ActiveReFlyRecordingId = attemptTip
                    ? tip.RecordingId
                    : live?.ActiveReFlyRecordingId,
                TreeId = tip.TreeId,
                OriginChildRecordingId = string.IsNullOrEmpty(originRecordingId)
                    ? tip.RecordingId
                    : originRecordingId,
                SupersedeTargetId = tip.RecordingId,
                RewindPointId = rewindPointId,
                InvokedUT = rewindCutoffUT,
                RewindPointUT = rewindCutoffUT,
            };

            HashSet<string> closure = EffectiveState.BuildSubtreeClosureUncached(
                slotMarker, tip.RecordingId);
            if (closure != null)
                ids.UnionWith(closure);
            ids.Add(tip.RecordingId);
            AddSpanningStretchAncestorClosure(ids, tip, slotMarker);

            AddMatchingChainRecordingIds(ids, tip, RecordingStore.CommittedRecordings);
            var trees = RecordingStore.CommittedTrees;
            for (int i = 0; i < trees.Count; i++)
            {
                var tree = trees[i];
                if (tree?.Recordings == null) continue;
                AddMatchingChainRecordingIds(ids, tip, tree.Recordings.Values);
            }

            int carvedOut;
            List<string> kept = RemovePreRewindCarveOuts(ids, slotMarker, out carvedOut);
            if (carvedOut == 0)
                return ids;
            return new HashSet<string>(kept, StringComparer.Ordinal);
        }

        private static IReadOnlyList<GameAction> pendingCommitRows;

        /// <summary>
        /// Scopes <see cref="RetireSetHoldsSealingRow"/> to also read
        /// <paramref name="rows"/>, the rows a tree commit files right after it promotes
        /// its Unfinished Flights (<c>RecordingStore.ApplyRewindProvisionalMergeStates</c>
        /// runs inside <c>CommitTree</c>, before <c>NotifyLedgerTreeCommitted</c>). Without
        /// it Site A would promote a slot whose own new rows seal it (RF-9). Dispose
        /// restores the previous scope.
        /// </summary>
        internal static IDisposable UsePendingCommitRows(IReadOnlyList<GameAction> rows)
        {
            var scope = new PendingCommitRowsScope(pendingCommitRows);
            pendingCommitRows = rows;
            return scope;
        }

        private sealed class PendingCommitRowsScope : IDisposable
        {
            private readonly IReadOnlyList<GameAction> previous;
            private bool disposed;

            internal PendingCommitRowsScope(IReadOnlyList<GameAction> previous)
            {
                this.previous = previous;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                pendingCommitRows = previous;
            }
        }

        /// <summary>
        /// When the merge's splitter would cut an earlier segment of the walked vessel
        /// rather than <paramref name="root"/> (<see cref="RecordingTreeSplitter.FindSpanningOwnVesselStretchAncestor"/>:
        /// the tree-branching parent that flew through the separation, then put a crew
        /// member out), the merge's closure grows from that segment's post-rewind TIP.
        /// Adds the forward closure of the unsplit segment; <see cref="BuildRetireSet"/>'s
        /// rewind cutoff keeps only its rows after the split, the ones the splitter moves
        /// onto the TIP and the merge retires (RF-20: a spacewalk milestone on the parent).
        /// </summary>
        private static void AddSpanningStretchAncestorClosure(
            HashSet<string> ids, Recording root, ReFlySessionMarker marker)
        {
            if (ids == null || root == null || marker == null) return;
            double rewindUT = marker.RewindPointUT;
            if (double.IsNaN(rewindUT) || double.IsInfinity(rewindUT) || rewindUT <= 0.0) return;
            Recording spanning = RecordingTreeSplitter.FindSpanningOwnVesselStretchAncestor(
                root, marker, rewindUT, EffectiveState.PidPeerStartUtEpsilonSeconds);
            if (spanning == null || string.IsNullOrEmpty(spanning.RecordingId)) return;
            HashSet<string> extra = EffectiveState.BuildSubtreeClosureUncached(
                marker, spanning.RecordingId);
            if (extra != null)
                ids.UnionWith(extra);
            ids.Add(spanning.RecordingId);
        }

        /// <summary>
        /// Every sealing row in the retire set rooted at <paramref name="tip"/>
        /// (design section 2.12 Rule 1), in tombstone order. Uncached.
        /// <paramref name="extraActions"/> are rows not yet filed to
        /// <see cref="Ledger.Actions"/> (a Re-Fly attempt's rows before its tree
        /// commits); they are read as if they were already in the ledger.
        /// </summary>
        internal static List<GameAction> CollectRetireSetSealingRows(
            Recording tip,
            string originRecordingId,
            string rewindPointId,
            double rewindCutoffUT,
            IReadOnlyList<GameAction> extraActions,
            out int retireSetCount,
            out int closureCount)
        {
            var sealing = new List<GameAction>();
            retireSetCount = 0;
            closureCount = 0;
            if (tip == null)
                return sealing;

            HashSet<string> closure = ComputeRetireClosure(
                tip, originRecordingId, rewindPointId, rewindCutoffUT);
            closureCount = closure.Count;

            IReadOnlyList<GameAction> actions = Ledger.Actions;
            if (extraActions != null && extraActions.Count > 0)
            {
                var combined = new List<GameAction>(actions.Count + extraActions.Count);
                combined.AddRange(actions);
                combined.AddRange(extraActions);
                actions = combined;
            }

            RetireSetScan scan = BuildRetireSet(
                closure, rewindCutoffUT, actions, CollectTombstonedActionIds(), logDecisions: false);
            retireSetCount = scan.Rows.Count;
            for (int i = 0; i < scan.Rows.Count; i++)
            {
                if (!IsLossRow(scan.Rows[i]))
                    sealing.Add(scan.Rows[i]);
            }
            return sealing;
        }

        /// <summary>
        /// The retire set a Re-Fly merge would tombstone if it ran now (design section
        /// 2.12, Rule 5's input): the closure <see cref="AppendRelations(ReFlySessionMarker, Recording, ParsekScenario)"/>
        /// computes from the marker (rooted at its supersede target, else its origin child),
        /// minus its pre-rewind carve-outs (<see cref="PreRewindCarveOutFilter"/>, as
        /// AppendRelations drops them) and <paramref name="attemptRecordingIds"/>, through
        /// <see cref="BuildRetireSet"/> over the raw ledger and the existing tombstones,
        /// exactly as <see cref="CommitTombstones"/> builds it. Uncached, logs nothing.
        /// <paramref name="extraActions"/> are the rows the tree commit files before the
        /// tombstones are built (the build step's preview); they are read as if they were
        /// already in the ledger, so a replaced-flight row the ledger never filed is retired
        /// here as the merge retires it.
        /// </summary>
        internal static List<GameAction> BuildMergeRetireSet(
            ReFlySessionMarker marker,
            ICollection<string> attemptRecordingIds,
            out int closureCount,
            IReadOnlyList<GameAction> extraActions = null)
        {
            closureCount = 0;
            if (marker == null)
                return new List<GameAction>();
            string closureRoot = marker.SupersedeTargetId ?? marker.OriginChildRecordingId;
            var subtree = new HashSet<string>(StringComparer.Ordinal);
            IReadOnlyCollection<string> rootClosure =
                EffectiveState.ComputeSubtreeClosureInternal(marker, closureRoot);
            if (rootClosure != null)
                subtree.UnionWith(rootClosure);
            AddSpanningStretchAncestorClosure(
                subtree, EffectiveState.FindCommittedRecordingByIdRaw(closureRoot), marker);
            int carvedOut;
            List<string> kept = RemovePreRewindCarveOuts(subtree, marker, out carvedOut);
            var closure = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < kept.Count; i++)
            {
                if (attemptRecordingIds != null && attemptRecordingIds.Contains(kept[i])) continue;
                closure.Add(kept[i]);
            }
            closureCount = closure.Count;
            IReadOnlyList<GameAction> actions = Ledger.Actions;
            if (extraActions != null && extraActions.Count > 0)
            {
                var combined = new List<GameAction>(actions.Count + extraActions.Count);
                combined.AddRange(actions);
                combined.AddRange(extraActions);
                actions = combined;
            }
            RetireSetScan scan = BuildRetireSet(
                closure, ComputeTombstoneRewindCutoffUT(marker), actions,
                CollectTombstonedActionIds(), logDecisions: false);
            return scan.Rows;
        }

        /// <summary>
        /// Design section 2.12 Rule 5 models the merge as it will run: true when the merge of
        /// <paramref name="tree"/> would retire nothing on the replaced side, so the dominance
        /// check must keep the retire set in <c>L'</c>. That is the case when the attempt's
        /// recording is missing (the merge concludes without a supersede) or when
        /// <see cref="ValidateReFlySessionSupersedeSource(Recording, RecordingTree, ReFlySessionMarker, out string, out SlotVesselWalk, out BranchPoint)"/>,
        /// the predicate AppendRelations refuses on (<c>outcome=refused-unflown-provisional</c>),
        /// rejects it, read on the dialog's pending tree. <paramref name="reason"/> names which.
        /// <para><paramref name="terminalPendingFinalize"/> is true when <paramref name="tree"/>
        /// is the live tree the leaving-flight dialog reads before the scene exit finalizes it:
        /// finalize stamps the active leaf's terminal state, so a provisional refused only for
        /// its missing terminal is modeled as retiring. The payload refusals still stand, and the
        /// click re-runs the check on the finalized tree, so this never lets an unflown attempt
        /// through.</para>
        /// </summary>
        internal static bool MergeWouldRetireNothing(
            ReFlySessionMarker marker, RecordingTree tree, out string reason,
            bool terminalPendingFinalize = false)
        {
            reason = null;
            if (marker == null)
            {
                reason = "no-marker";
                return true;
            }
            string id = marker.ActiveReFlyRecordingId;
            Recording provisional = null;
            if (!string.IsNullOrEmpty(id))
            {
                if (tree?.Recordings != null)
                    tree.Recordings.TryGetValue(id, out provisional);
                if (provisional == null)
                    provisional = EffectiveState.FindCommittedRecordingByIdRaw(id);
            }
            if (provisional == null)
            {
                reason = "provisional-missing";
                return true;
            }
            string invalid;
            if (!ValidateReFlySessionSupersedeSource(
                    provisional, tree, marker, out invalid, out _, out _))
            {
                if (terminalPendingFinalize
                    && string.Equals(invalid, "null TerminalState", StringComparison.Ordinal))
                {
                    reason = "terminal-pending-finalize";
                    return false;
                }
                reason = "refused-unflown-provisional:" + (invalid ?? "<none>");
                return true;
            }
            return false;
        }

        /// <summary>
        /// The recordings of a Re-Fly attempt among the committed recordings (after its tree
        /// commits): stamped with the session (<see cref="MergeDialog.IsCreatedByReFlySession"/>),
        /// plus the marker's active re-fly recording.
        /// </summary>
        internal static HashSet<string> CollectCommittedReFlyAttemptRecordingIds(ReFlySessionMarker marker)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (marker == null)
                return ids;
            if (!string.IsNullOrEmpty(marker.ActiveReFlyRecordingId))
                ids.Add(marker.ActiveReFlyRecordingId);
            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec != null && !string.IsNullOrEmpty(rec.RecordingId)
                    && MergeDialog.IsCreatedByReFlySession(rec, marker))
                    ids.Add(rec.RecordingId);
            }
            return ids;
        }

        /// <summary>
        /// Design section 2.12 Rule 2, Variant B's second half: the attempt's own
        /// <see cref="GameActionType.ReputationPenalty"/> loss rows (sources KerbalDeath /
        /// Other) that are not tombstoned yet, in ledger order. The merge tombstones them
        /// with the attempt as the retiring recording.
        /// </summary>
        internal static List<GameAction> CollectReFlyAttemptReputationLossRows(
            ICollection<string> attemptRecordingIds)
        {
            var rows = new List<GameAction>();
            if (attemptRecordingIds == null || attemptRecordingIds.Count == 0)
                return rows;
            var tombstoned = CollectTombstonedActionIds();
            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a == null || string.IsNullOrEmpty(a.RecordingId)) continue;
                if (!attemptRecordingIds.Contains(a.RecordingId)) continue;
                if (!IsReputationLossRow(a)) continue;
                if (!string.IsNullOrEmpty(a.ActionId) && tombstoned.Contains(a.ActionId)) continue;
                rows.Add(a);
            }
            return rows;
        }

        /// <summary>A <see cref="GameActionType.ReputationPenalty"/> row that is a loss row
        /// (design section 2.12 Terms): source KerbalDeath or Other.</summary>
        internal static bool IsReputationLossRow(GameAction action)
        {
            return action != null
                && action.Type == GameActionType.ReputationPenalty
                && IsLossRow(action);
        }

        private static HashSet<string> CollectTombstonedActionIds()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario) || scenario.LedgerTombstones == null)
                return ids;
            for (int i = 0; i < scenario.LedgerTombstones.Count; i++)
            {
                var t = scenario.LedgerTombstones[i];
                if (t != null && !string.IsNullOrEmpty(t.ActionId))
                    ids.Add(t.ActionId);
            }
            return ids;
        }

        /// <summary>
        /// Design section 2.12 Rule 1, the value gate: true when the retire set of the
        /// slot (rooted at <see cref="ResolveRetireSetTip"/>) holds a sealing row;
        /// <paramref name="summary"/> is the first one as <c>Type:ActionId</c>. Read by
        /// Site A (every <see cref="UnfinishedFlightClassifier"/> arm), the Stash
        /// resolver, Site B-1, the merge-dialog auto-seal preview,
        /// <see cref="RewindInvoker.CanInvoke(RewindPoint, ChildSlot, out string)"/> and
        /// the load-time value-seal sweep. Cached per (rewind point, slot, tip, live
        /// session) and invalidated by every ledger, store, supersede and tombstone
        /// version bump, so a ledger change after the row was drawn is seen at once.
        /// </summary>
        internal static bool RetireSetHoldsSealingRow(
            Recording subject,
            RewindPoint rp,
            ChildSlot slot,
            out string summary)
        {
            summary = null;
            Recording tip = ResolveRetireSetTip(subject, slot);
            if (tip == null || string.IsNullOrEmpty(tip.RecordingId))
                return false;

            var scenario = ParsekScenario.Instance;
            ReFlySessionMarker live = !object.ReferenceEquals(null, scenario)
                ? scenario.ActiveReFlySessionMarker
                : null;
            string cacheKey = "retire"
                + WorldActionCacheKeySeparator + (rp?.RewindPointId ?? string.Empty)
                + WorldActionCacheKeySeparator + (slot != null
                    ? slot.SlotIndex.ToString(CultureInfo.InvariantCulture)
                    : string.Empty)
                + WorldActionCacheKeySeparator + (slot?.OriginChildRecordingId ?? string.Empty)
                + WorldActionCacheKeySeparator + tip.RecordingId
                + WorldActionCacheKeySeparator + tip.MergeState.ToString()
                + WorldActionCacheKeySeparator + (live?.SessionId ?? string.Empty)
                + WorldActionCacheKeySeparator + (live?.ActiveReFlyRecordingId ?? string.Empty);

            // Rows a tree commit is about to file read as already filed, uncached (the
            // ledger has not moved, so a cached verdict would not see them).
            IReadOnlyList<GameAction> pendingRows = pendingCommitRows;
            bool readPending = pendingRows != null && pendingRows.Count > 0;

            WorldActionSafetyCacheEntry cached;
            if (!readPending && TryGetCachedWorldActionSafetyVerdict(cacheKey, scenario, out cached))
            {
                summary = cached.ActionSummary;
                return cached.HasAction;
            }

            double cutoff = ComputeTombstoneRewindCutoffUT(rp);
            int retireSetCount;
            int closureCount;
            List<GameAction> sealing = CollectRetireSetSealingRows(
                tip, slot?.OriginChildRecordingId, rp?.RewindPointId, cutoff,
                extraActions: readPending ? pendingRows : null, out retireSetCount, out closureCount);

            var entry = new WorldActionSafetyCacheEntry
            {
                HasAction = sealing.Count > 0,
                ActionSummary = sealing.Count > 0 ? FormatRetireSetRowSummary(PickNamedSealingRow(sealing)) : null,
            };
            if (!readPending)
                CacheWorldActionSafetyVerdict(cacheKey, scenario, entry);

            var ic = CultureInfo.InvariantCulture;
            string identity = "retire-set|" + (rp?.RewindPointId ?? "<no-rp>") + "|"
                + (slot != null ? slot.SlotIndex.ToString(ic) : "-") + "|" + tip.RecordingId;
            string state = (entry.HasAction ? "sealed|" + entry.ActionSummary : "open")
                + "|" + retireSetCount.ToString(ic) + "|" + sealing.Count.ToString(ic);
            ParsekLog.VerboseOnChange(Tag, identity, state,
                $"RetireSet gate (section 2.12 Rule 1): rp={rp?.RewindPointId ?? "<no-rp>"} " +
                $"slot={(slot != null ? slot.SlotIndex.ToString(ic) : "<none>")} " +
                $"subject={subject?.RecordingId ?? "<none>"} tip={tip.RecordingId} " +
                $"closure={closureCount.ToString(ic)} retireSet={retireSetCount.ToString(ic)} " +
                $"lossRows={(retireSetCount - sealing.Count).ToString(ic)} " +
                $"sealingRows={sealing.Count.ToString(ic)} " +
                $"cutoffUT={(double.IsNaN(cutoff) ? "<none>" : cutoff.ToString("R", ic))} " +
                $"-> {(entry.HasAction ? "sealed by " + entry.ActionSummary : "nothing to give back")}");

            summary = entry.ActionSummary;
            return entry.HasAction;
        }

        /// <summary>
        /// The value gate for a live Re-Fly attempt (Site B-1's shape, read by the
        /// merge-dialog preview): the retire set rooted at the attempt's recording
        /// <paramref name="provisional"/>, cut at the session's rewind point.
        /// <paramref name="attemptRows"/> are the attempt's rows not yet filed to the
        /// ledger (empty until the merge-time row build exists). Returns every sealing
        /// row; uncached.
        /// </summary>
        internal static List<GameAction> CollectReFlyAttemptSealingRows(
            ReFlySessionMarker marker,
            Recording provisional,
            IReadOnlyList<GameAction> attemptRows)
        {
            if (marker == null || provisional == null)
                return new List<GameAction>();
            double cutoff = ComputeTombstoneRewindCutoffUT(marker);
            int retireSetCount;
            int closureCount;
            List<GameAction> sealing = CollectRetireSetSealingRows(
                provisional, marker.OriginChildRecordingId, marker.RewindPointId, cutoff,
                attemptRows, out retireSetCount, out closureCount);
            var ic = CultureInfo.InvariantCulture;
            ParsekLog.VerboseOnChange(Tag,
                "retire-set-attempt|" + (marker.SessionId ?? "<no-sess>") + "|" + (provisional.RecordingId ?? "<no-id>"),
                retireSetCount.ToString(ic) + "|" + sealing.Count.ToString(ic)
                    + "|" + (attemptRows?.Count ?? 0).ToString(ic),
                $"RetireSet attempt read (section 2.12 Rule 1): sess={marker.SessionId ?? "<no-sess>"} " +
                $"rec={provisional.RecordingId ?? "<no-id>"} closure={closureCount.ToString(ic)} " +
                $"extraRows={(attemptRows?.Count ?? 0).ToString(ic)} " +
                $"retireSet={retireSetCount.ToString(ic)} sealingRows={sealing.Count.ToString(ic)}" +
                (sealing.Count > 0 ? " named=" + FormatRetireSetRowSummary(PickNamedSealingRow(sealing)) : string.Empty));
            return sealing;
        }
    }
}
