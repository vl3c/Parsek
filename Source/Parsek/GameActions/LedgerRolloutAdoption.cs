using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Helper for VesselRollout ledger actions and later recording adoption.
    /// Kept behind LedgerOrchestrator wrappers so the public test/call surface stays stable.
    /// </summary>
    internal static class LedgerRolloutAdoption
    {
        private const string Tag = "LedgerOrchestrator";

        /// <summary>
        /// UT window (game seconds, either side of the candidate UT) within which a
        /// matching unadopted VesselRollout action is treated as the same logical
        /// rollout. The deciding identity is the launch guid (see
        /// <see cref="ClassifyRolloutDuplicate"/>); the window only bounds the scan.
        /// </summary>
        internal const double RolloutDuplicateWindowSeconds = 60.0;

        /// <summary>
        /// Why <see cref="ClassifyRolloutDuplicate"/> did or did not call two rollout
        /// contexts the same logical rollout.
        /// </summary>
        internal enum RolloutDuplicateVerdict
        {
            // Both launch guids known and equal.
            Duplicate,
            // A launch guid unknown on a side; the #710 pid (else name) + site match held.
            DuplicateGuidUnknownFallback,
            LegacyBareKey,
            GuidDiffers,
            SiteDiffers,
            PidDiffers,
            NoMatch
        }

        internal static bool IsDuplicateVerdict(RolloutDuplicateVerdict verdict)
        {
            return verdict == RolloutDuplicateVerdict.Duplicate
                || verdict == RolloutDuplicateVerdict.DuplicateGuidUnknownFallback;
        }

        /// <summary>
        /// Cost-equality epsilon for rollout duplicate detection. KSP rolls the same
        /// vessel out for the same cost down to the float — the production repro had
        /// both rollouts at <c>8320.001</c> exactly. A 1-credit tolerance survives any
        /// future float rounding without colliding with legitimate cost differences.
        /// </summary>
        internal const float RolloutDuplicateCostEpsilon = 1.0f;

        internal struct RolloutAdoptionContext
        {
            public uint VesselPersistentId;
            public string VesselName;
            public string LaunchSiteName;
            // Launch-unique KSP Vessel.id in canonical "N" form; null when unknown.
            public string VesselGuid;
            public bool IsLegacyBareKey;
        }

        internal static void RecordVesselRolloutSpending(
            double ut,
            double cost,
            RolloutAdoptionContext context,
            Func<int> allocateKscSequence,
            Action<GameAction, double> reconcileKscAction,
            Action recalculateAndPatch)
        {
            if (cost <= 0)
            {
                ParsekLog.Verbose(Tag,
                    $"OnVesselRolloutSpending: non-positive cost={cost:F1} at UT={ut:F1}, skipping");
                return;
            }

            // Revert double-rollout guard (#710), keyed on the launch guid
            // (KSP Vessel.id, see ClassifyRolloutDuplicate). The persistentId is
            // craft-baked and reused by every launch of the craft, so pid + site +
            // cost inside the window also matches a genuine relaunch of the same
            // craft (an unrecorded launch, a recovery, a second launch): when both
            // rows carry a known launch guid and the guids differ, the new rollout
            // is a second, real charge (CS-3b, RELAUNCH-ROLLOUT-DEDUPED-AS-REVERT).
            // Equal guids are one launch emitted twice. When either guid is unknown
            // the #710 pid / name + site match decides, as before.
            //
            // Cluster invariant: the surviving row's UT is always the minimum of
            // the cluster's UTs, regardless of write order or adoption state. The
            // load-time repair pass at <see cref="RepairDuplicateRolloutActions"/>
            // upholds the same invariant. When the new write has the strictly
            // smaller UT, the surviving row's UT/DedupKey/Sequence are mutated in
            // place so <see cref="TryAdoptRolloutAction"/>'s 0.5 s post-startUT cap
            // still finds it.
            GameAction guidNearMiss;
            RolloutDuplicateVerdict nearMissVerdict;
            RolloutDuplicateVerdict matchVerdict;
            if (TryFindDuplicateRolloutAction(
                    ut, cost, context, Ledger.Actions, out GameAction existing, out matchVerdict,
                    out guidNearMiss, out nearMissVerdict))
            {
                string existingGuid = ParseRolloutAdoptionContext(existing.DedupKey).VesselGuid;
                if (ut < existing.UT)
                {
                    double oldUT = existing.UT;
                    string oldDedupKey = existing.DedupKey;
                    int oldSequence = existing.Sequence;

                    // Keep whichever side's launch guid is known on the survivor.
                    var survivorContext = context;
                    if (survivorContext.VesselGuid == null)
                        survivorContext.VesselGuid = existingGuid;
                    existing.UT = ut;
                    existing.DedupKey = BuildRolloutDedupKey(ut, survivorContext);
                    existing.Sequence = allocateKscSequence();

                    Ledger.BumpStateVersion();

                    ParsekLog.Info(Tag,
                        $"OnVesselRolloutSpending: skipping duplicate rollout — swapped surviving row to relaunch UT " +
                        $"(oldUT={oldUT.ToString("R", CultureInfo.InvariantCulture)}, " +
                        $"newUT={ut.ToString("R", CultureInfo.InvariantCulture)}, " +
                        $"deltaUT={(ut - oldUT).ToString("F3", CultureInfo.InvariantCulture)}s, " +
                        $"verdict={matchVerdict}, existingGuid={existingGuid ?? "(none)"}, newGuid={context.VesselGuid ?? "(none)"}, " +
                        $"cost={cost:F1}, context={FormatRolloutAdoptionContext(context)}, " +
                        $"oldDedupKey={oldDedupKey ?? "(null)"}, " +
                        $"newDedupKey={existing.DedupKey}, " +
                        $"oldSequence={oldSequence}, newSequence={existing.Sequence})");
                }
                else
                {
                    ParsekLog.Info(Tag,
                        $"OnVesselRolloutSpending: skipping duplicate rollout — kept existing row " +
                        $"(existingUT={existing.UT.ToString("R", CultureInfo.InvariantCulture)}, " +
                        $"newUT={ut.ToString("R", CultureInfo.InvariantCulture)}, " +
                        $"deltaUT={(ut - existing.UT).ToString("F3", CultureInfo.InvariantCulture)}s, " +
                        $"verdict={matchVerdict}, existingGuid={existingGuid ?? "(none)"}, newGuid={context.VesselGuid ?? "(none)"}, " +
                        $"cost={cost:F1}, context={FormatRolloutAdoptionContext(context)}, " +
                        $"existingDedupKey={existing.DedupKey ?? "(null)"})");
                }
                return;
            }

            if (guidNearMiss != null)
            {
                ParsekLog.Info(Tag,
                    $"OnVesselRolloutSpending: recording rollout as a new launch - an unadopted rollout of the same " +
                    $"cost and site is inside the {RolloutDuplicateWindowSeconds.ToString("F0", CultureInfo.InvariantCulture)}s window " +
                    $"but its launch guid differs (verdict={nearMissVerdict}, " +
                    $"existingGuid={ParseRolloutAdoptionContext(guidNearMiss.DedupKey).VesselGuid ?? "(none)"}, " +
                    $"newGuid={context.VesselGuid ?? "(none)"}, " +
                    $"existingUT={guidNearMiss.UT.ToString("R", CultureInfo.InvariantCulture)}, " +
                    $"newUT={ut.ToString("R", CultureInfo.InvariantCulture)}, cost={cost:F1})");
            }

            int sequence = allocateKscSequence();
            var action = new GameAction
            {
                UT = ut,
                Type = GameActionType.FundsSpending,
                RecordingId = null,
                FundsSpent = (float)cost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                DedupKey = BuildRolloutDedupKey(ut, context),
                Sequence = sequence
            };

            Ledger.AddAction(action, LedgerRowWriter.StockApplied);

            ParsekLog.Info(Tag,
                $"VesselRollout spending recorded: cost={cost:F0}, UT={ut:F1}, dedupKey={action.DedupKey}, " +
                $"context={FormatRolloutAdoptionContext(context)}");

            reconcileKscAction(action, ut);
            recalculateAndPatch();
        }

        /// <summary>
        /// Scans <paramref name="actions"/> for an unadopted (recording-less) rollout
        /// action that the candidate (<paramref name="ut"/>, <paramref name="cost"/>,
        /// <paramref name="context"/>) is logically the same as. Internal for direct
        /// testability + reuse from the load-time repair pass.
        /// </summary>
        internal static bool TryFindDuplicateRolloutAction(
            double ut,
            double cost,
            RolloutAdoptionContext context,
            System.Collections.Generic.IReadOnlyList<GameAction> actions,
            out GameAction match)
        {
            GameAction ignoredNearMiss;
            RolloutDuplicateVerdict ignoredVerdict;
            RolloutDuplicateVerdict ignoredMatchVerdict;
            return TryFindDuplicateRolloutAction(
                ut, cost, context, actions, out match, out ignoredMatchVerdict,
                out ignoredNearMiss, out ignoredVerdict);
        }

        /// <summary>
        /// As above, also reporting the verdict that matched and the newest in-window,
        /// same-cost row that only differing known launch guids kept apart
        /// (<see cref="RolloutDuplicateVerdict.GuidDiffers"/>), so the caller can log why
        /// a same-craft relaunch was written as its own rollout.
        /// </summary>
        internal static bool TryFindDuplicateRolloutAction(
            double ut,
            double cost,
            RolloutAdoptionContext context,
            System.Collections.Generic.IReadOnlyList<GameAction> actions,
            out GameAction match,
            out RolloutDuplicateVerdict matchVerdict,
            out GameAction guidNearMiss,
            out RolloutDuplicateVerdict nearMissVerdict)
        {
            match = null;
            matchVerdict = RolloutDuplicateVerdict.NoMatch;
            guidNearMiss = null;
            nearMissVerdict = RolloutDuplicateVerdict.Duplicate;
            if (actions == null) return false;
            if (!CanMatchRolloutAdoptionContext(context)) return false;

            for (int i = actions.Count - 1; i >= 0; i--)
            {
                var a = actions[i];
                if (a == null) continue;
                if (a.Type != GameActionType.FundsSpending) continue;
                if (a.FundsSpendingSource != FundsSpendingSource.VesselBuild) continue;
                if (!string.IsNullOrEmpty(a.RecordingId)) continue;
                if (string.IsNullOrEmpty(a.DedupKey)) continue;
                if (!a.DedupKey.StartsWith(LedgerOrchestrator.RolloutDedupPrefix, StringComparison.Ordinal)) continue;
                if (Math.Abs(a.UT - ut) > RolloutDuplicateWindowSeconds) continue;
                if (Math.Abs(a.FundsSpent - (float)cost) > RolloutDuplicateCostEpsilon) continue;

                var existingContext = ParseRolloutAdoptionContext(a.DedupKey);
                var verdict = ClassifyRolloutDuplicate(existingContext, context);
                if (!IsDuplicateVerdict(verdict))
                {
                    if (guidNearMiss == null && verdict == RolloutDuplicateVerdict.GuidDiffers)
                    {
                        guidNearMiss = a;
                        nearMissVerdict = verdict;
                    }
                    continue;
                }

                match = a;
                matchVerdict = verdict;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Pure dedup-context comparison for rollout duplicates, shared by the write
        /// gate and the load-time repair pass so the two always agree.
        /// <list type="bullet">
        ///   <item><description>Differing known launch sites are never one rollout
        ///   (a same-craft launch from another site is a separate charge).</description></item>
        ///   <item><description>Both launch guids known: the guid decides. It is
        ///   launch-unique (stock assigns a fresh <c>Vessel.id</c> per launch), while
        ///   the persistentId and the vessel name are reused by every launch of a
        ///   craft, so equal guids are one rollout and differing guids are a genuine
        ///   relaunch (RELAUNCH-ROLLOUT-DEDUPED-AS-REVERT).</description></item>
        ///   <item><description>A guid unknown on either side (a row written before
        ///   the key carried one, or a context that could not read the live vessel):
        ///   the #710 match, same persistentId when both are known, else same vessel
        ///   name and site.</description></item>
        /// </list>
        /// Unlike <see cref="RolloutAdoptionContextsMatch"/>, a legacy bare key never
        /// matches.
        /// </summary>
        internal static RolloutDuplicateVerdict ClassifyRolloutDuplicate(
            RolloutAdoptionContext existing,
            RolloutAdoptionContext candidate)
        {
            if (existing.IsLegacyBareKey || candidate.IsLegacyBareKey)
                return RolloutDuplicateVerdict.LegacyBareKey;

            if (!string.IsNullOrEmpty(existing.LaunchSiteName)
                && !string.IsNullOrEmpty(candidate.LaunchSiteName)
                && !existing.LaunchSiteName.Equals(candidate.LaunchSiteName, StringComparison.OrdinalIgnoreCase))
                return RolloutDuplicateVerdict.SiteDiffers;

            bool pidsKnown = existing.VesselPersistentId != 0 && candidate.VesselPersistentId != 0;
            string existingGuid = VesselLaunchIdentity.NormalizeGuid(existing.VesselGuid);
            string candidateGuid = VesselLaunchIdentity.NormalizeGuid(candidate.VesselGuid);
            if (existingGuid != null && candidateGuid != null)
            {
                if (!string.Equals(existingGuid, candidateGuid, StringComparison.OrdinalIgnoreCase))
                    return RolloutDuplicateVerdict.GuidDiffers;
                if (pidsKnown && existing.VesselPersistentId != candidate.VesselPersistentId)
                    return RolloutDuplicateVerdict.PidDiffers;
                return RolloutDuplicateVerdict.Duplicate;
            }

            if (pidsKnown)
            {
                return existing.VesselPersistentId == candidate.VesselPersistentId
                    ? RolloutDuplicateVerdict.DuplicateGuidUnknownFallback
                    : RolloutDuplicateVerdict.PidDiffers;
            }

            bool nameAndSiteMatch = !string.IsNullOrEmpty(existing.VesselName)
                && !string.IsNullOrEmpty(candidate.VesselName)
                && !string.IsNullOrEmpty(existing.LaunchSiteName)
                && !string.IsNullOrEmpty(candidate.LaunchSiteName)
                && existing.VesselName.Equals(candidate.VesselName, StringComparison.OrdinalIgnoreCase)
                && existing.LaunchSiteName.Equals(candidate.LaunchSiteName, StringComparison.OrdinalIgnoreCase);
            return nameAndSiteMatch
                ? RolloutDuplicateVerdict.DuplicateGuidUnknownFallback
                : RolloutDuplicateVerdict.NoMatch;
        }

        private static bool RolloutDuplicateMatches(
            RolloutAdoptionContext existing,
            RolloutAdoptionContext candidate)
        {
            return IsDuplicateVerdict(ClassifyRolloutDuplicate(existing, candidate));
        }

        /// <summary>
        /// Load-time repair pass for already-corrupted saves: scans the in-memory
        /// ledger for clusters of VesselRollout actions that share the same
        /// logical rollout (<see cref="ClassifyRolloutDuplicate"/>: equal launch
        /// guids, or the #710 pid / name + site match when a guid is unknown;
        /// never differing known guids or sites; near-equal cost, UT within
        /// <see cref="RolloutDuplicateWindowSeconds"/>) and collapses each
        /// cluster down to a single survivor. Idempotent — a second invocation
        /// on a healthy ledger is a no-op. Returns the number of actions removed
        /// so callers can suppress chatty logs when nothing changed. The single
        /// INFO line summarising each dedup is emitted only when an action was
        /// removed.
        ///
        /// <para>Cluster invariant: the surviving row's UT is always the
        /// minimum of the cluster's UTs, regardless of write order or adoption
        /// state — same invariant as the write-time gate at
        /// <see cref="TryFindDuplicateRolloutAction"/>.</para>
        ///
        /// <para>Adoption-state matrix for a matching pair (anchor, candidate):
        /// <list type="bullet">
        ///   <item><description><b>Both unadopted</b> — keep the anchor; if
        ///   the candidate's UT is strictly smaller, first swap the anchor's
        ///   payload (UT/DedupKey/Sequence/ActionId) onto the candidate's data
        ///   so the survivor still reflects the relaunch timeline that
        ///   actually happened (mirrors the production case where the
        ///   revert-relaunch row landed with a smaller UT due to a clock
        ///   rollback).</description></item>
        ///   <item><description><b>Anchor adopted, candidate unadopted</b> —
        ///   the adopted row is the recording's authoritative cost line; drop
        ///   the unadopted candidate. If the candidate has the earlier UT,
        ///   move the adopted row's UT/Sequence to that relaunch timestamp,
        ///   but do not rewrite the adopted row's ActionId (nothing references
        ///   the unadopted ActionId — adoption never claimed it, no tombstone
        ///   exists for FundsSpending(VesselBuild), and FundsModule's
        ///   per-ActionId rate-limit cache is recomputed from scratch on the
        ///   next recalc).</description></item>
        ///   <item><description><b>Anchor unadopted, candidate adopted</b> —
        ///   same rule applied symmetrically: keep the adopted candidate as
        ///   the cluster survivor, move its UT/Sequence to the unadopted
        ///   anchor's earlier timestamp when needed, and drop the unadopted
        ///   anchor. Done by marking the anchor index for removal and breaking
        ///   out of the inner scan since the anchor is dead.</description></item>
        ///   <item><description><b>Both adopted</b> — rare; would mean two
        ///   recordings independently adopted rollouts for the same logical
        ///   vessel within the dedup window. Keep both rows untouched and
        ///   emit a WARN — collapsing here would silently steal a
        ///   recording's authoritative cost line and the safer behaviour is
        ///   to surface the anomaly for investigation.</description></item>
        /// </list></para>
        ///
        /// <para>Driven by callers that own the Ledger mutator surface
        /// (<see cref="LedgerOrchestrator.OnKspLoad"/>) — the
        /// <paramref name="removeAt"/> callback abstracts the removal and the
        /// <paramref name="resolveAdoptedContext"/> callback resolves the
        /// rollout context for an adopted row from its
        /// <see cref="GameAction.RecordingId"/> (adopted rows have no DedupKey
        /// to parse, so the context lives on the recording itself). Tests can
        /// assert against an in-memory list with a stub resolver.</para>
        /// </summary>
        internal static int RepairDuplicateRolloutActions(
            System.Collections.Generic.IList<GameAction> actions,
            Action<int> removeAt,
            Func<string, RolloutAdoptionContext> resolveAdoptedContext = null)
        {
            if (actions == null || removeAt == null) return 0;

            int removed = 0;
            var toRemove = new System.Collections.Generic.List<int>();
            var alreadyMarked = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < actions.Count; i++)
            {
                if (alreadyMarked.Contains(i)) continue;
                var anchor = actions[i];
                if (!IsRolloutAction(anchor)) continue;

                bool anchorAdopted = !string.IsNullOrEmpty(anchor.RecordingId);
                RolloutAdoptionContext anchorContext;
                if (!TryResolveRolloutContextForRow(anchor, resolveAdoptedContext, out anchorContext))
                    continue;

                for (int j = i + 1; j < actions.Count; j++)
                {
                    if (alreadyMarked.Contains(j)) continue;
                    var candidate = actions[j];
                    if (!IsRolloutAction(candidate)) continue;
                    if (Math.Abs(candidate.UT - anchor.UT) > RolloutDuplicateWindowSeconds) continue;
                    if (Math.Abs(candidate.FundsSpent - anchor.FundsSpent) > RolloutDuplicateCostEpsilon) continue;

                    bool candidateAdopted = !string.IsNullOrEmpty(candidate.RecordingId);
                    RolloutAdoptionContext candidateContext;
                    if (!TryResolveRolloutContextForRow(candidate, resolveAdoptedContext, out candidateContext))
                        continue;

                    if (!RolloutDuplicateMatches(anchorContext, candidateContext)) continue;

                    if (anchorAdopted && candidateAdopted)
                    {
                        // Two recordings claim the same logical rollout within
                        // the dedup window — this should not happen on a
                        // healthy timeline. Keep both rows; surface the
                        // anomaly for investigation rather than silently
                        // hijacking either recording's cost line.
                        ParsekLog.Warn(Tag,
                            $"RepairDuplicateRolloutActions: two adopted rollouts match within window — keeping both " +
                            $"(anchorRecordingId={anchor.RecordingId}, candidateRecordingId={candidate.RecordingId}, " +
                            $"anchorUT={anchor.UT.ToString("R", CultureInfo.InvariantCulture)}, " +
                            $"candidateUT={candidate.UT.ToString("R", CultureInfo.InvariantCulture)}, " +
                            $"cost={candidate.FundsSpent:F1}, context={FormatRolloutAdoptionContext(anchorContext)})");
                        continue;
                    }

                    if (anchorAdopted && !candidateAdopted)
                    {
                        // Anchor (adopted) survives; drop the unadopted candidate.
                        // Preserve the cluster minimum-UT invariant, but do
                        // NOT rewrite ActionId because the adopted row's
                        // ActionId is the recording's authoritative reference
                        // and the unadopted ActionId has no consumers.
                        double anchorOldUT = anchor.UT;
                        int anchorOldSequence = anchor.Sequence;
                        bool adoptedTimestampMoved = false;
                        if (candidate.UT < anchor.UT)
                        {
                            anchor.UT = candidate.UT;
                            anchor.Sequence = candidate.Sequence;
                            adoptedTimestampMoved = true;
                        }

                        ParsekLog.Info(Tag,
                            $"RepairDuplicateRolloutActions: removing duplicate rollout — adopted anchor wins " +
                            $"(removedUT={candidate.UT.ToString("R", CultureInfo.InvariantCulture)}, " +
                            $"keptUT={anchor.UT.ToString("R", CultureInfo.InvariantCulture)}, " +
                            $"oldKeptUT={anchorOldUT.ToString("R", CultureInfo.InvariantCulture)}, " +
                            $"keptRecordingId={anchor.RecordingId}, " +
                            $"cost={candidate.FundsSpent:F1}, " +
                            $"context={FormatRolloutAdoptionContext(anchorContext)}, " +
                            $"removedGuid={candidateContext.VesselGuid ?? "(none)"}, " +
                            $"removedDedupKey={candidate.DedupKey ?? "(null)"}, " +
                            $"adoptedTimestampMoved={adoptedTimestampMoved}, " +
                            $"oldSequence={anchorOldSequence}, newSequence={anchor.Sequence})");

                        alreadyMarked.Add(j);
                        toRemove.Add(j);
                        removed++;
                        continue;
                    }

                    if (!anchorAdopted && candidateAdopted)
                    {
                        // Candidate (adopted) survives; drop the unadopted anchor.
                        // The anchor is dead — break out of the inner scan; the
                        // outer loop will continue at i+1 and skip the now-marked
                        // index. Symmetric to the previous branch: preserve
                        // the cluster minimum-UT invariant, but never rewrite
                        // the candidate's ActionId.
                        double candidateOldUT = candidate.UT;
                        int candidateOldSequence = candidate.Sequence;
                        bool adoptedTimestampMoved = false;
                        if (anchor.UT < candidate.UT)
                        {
                            candidate.UT = anchor.UT;
                            candidate.Sequence = anchor.Sequence;
                            adoptedTimestampMoved = true;
                        }

                        ParsekLog.Info(Tag,
                            $"RepairDuplicateRolloutActions: removing duplicate rollout — adopted candidate wins " +
                            $"(removedUT={anchor.UT.ToString("R", CultureInfo.InvariantCulture)}, " +
                            $"keptUT={candidate.UT.ToString("R", CultureInfo.InvariantCulture)}, " +
                            $"oldKeptUT={candidateOldUT.ToString("R", CultureInfo.InvariantCulture)}, " +
                            $"keptRecordingId={candidate.RecordingId}, " +
                            $"cost={anchor.FundsSpent:F1}, " +
                            $"context={FormatRolloutAdoptionContext(candidateContext)}, " +
                            $"removedGuid={anchorContext.VesselGuid ?? "(none)"}, " +
                            $"removedDedupKey={anchor.DedupKey ?? "(null)"}, " +
                            $"adoptedTimestampMoved={adoptedTimestampMoved}, " +
                            $"oldSequence={candidateOldSequence}, newSequence={candidate.Sequence})");

                        alreadyMarked.Add(i);
                        toRemove.Add(i);
                        removed++;
                        break;
                    }

                    // Both unadopted: existing earliest-UT preference. When the
                    // candidate's UT is strictly less than the anchor's, swap
                    // the anchor's payload onto the candidate's data first so
                    // the surviving row reflects the relaunch timeline that
                    // actually happened. Swapping (UT, DedupKey, Sequence,
                    // ActionId) keeps the anchor's index stable so
                    // previously-marked siblings stay reaped. Because both
                    // rows are unadopted, the discarded ActionId has no
                    // consumers — there is no tombstone for VesselRollout
                    // and adoption never claimed it.
                    double removedUT = candidate.UT;
                    string removedDedupKey = candidate.DedupKey;
                    if (candidate.UT < anchor.UT)
                    {
                        removedUT = anchor.UT;
                        removedDedupKey = anchor.DedupKey;
                        anchor.UT = candidate.UT;
                        // Keep whichever side's launch guid is known on the survivor.
                        var survivorContext = candidateContext;
                        if (survivorContext.VesselGuid == null)
                            survivorContext.VesselGuid = anchorContext.VesselGuid;
                        anchor.DedupKey = survivorContext.VesselGuid == candidateContext.VesselGuid
                            ? candidate.DedupKey
                            : BuildRolloutDedupKey(candidate.UT, survivorContext);
                        anchor.Sequence = candidate.Sequence;
                        anchor.ActionId = candidate.ActionId;
                    }

                    ParsekLog.Info(Tag,
                        $"RepairDuplicateRolloutActions: removing duplicate rollout " +
                        $"(removedUT={removedUT.ToString("R", CultureInfo.InvariantCulture)}, " +
                        $"keptUT={anchor.UT.ToString("R", CultureInfo.InvariantCulture)}, " +
                        $"cost={candidate.FundsSpent:F1}, " +
                        $"context={FormatRolloutAdoptionContext(anchorContext)}, " +
                        $"candidateGuid={candidateContext.VesselGuid ?? "(none)"}, " +
                        $"removedDedupKey={removedDedupKey ?? "(null)"})");

                    alreadyMarked.Add(j);
                    toRemove.Add(j);
                    removed++;
                }
            }

            // Remove highest index first so removeAt() does not invalidate
            // earlier indices in the same batch.
            toRemove.Sort();
            for (int k = toRemove.Count - 1; k >= 0; k--)
                removeAt(toRemove[k]);

            return removed;
        }

        /// <summary>
        /// Resolves the rollout context for an action at scan time. Adopted
        /// rows have <see cref="GameAction.DedupKey"/> = null (cleared during
        /// adoption) so their context must come from the source recording via
        /// the <paramref name="resolveAdoptedContext"/> callback. Unadopted
        /// rows carry the structured key inline.
        /// </summary>
        private static bool TryResolveRolloutContextForRow(
            GameAction action,
            Func<string, RolloutAdoptionContext> resolveAdoptedContext,
            out RolloutAdoptionContext context)
        {
            context = default(RolloutAdoptionContext);
            if (action == null) return false;

            if (string.IsNullOrEmpty(action.RecordingId))
            {
                if (string.IsNullOrEmpty(action.DedupKey)) return false;
                context = ParseRolloutAdoptionContext(action.DedupKey);
                if (context.IsLegacyBareKey) return false;
                return CanMatchRolloutAdoptionContext(context);
            }

            // Adopted row: derive the context from the source recording.
            if (resolveAdoptedContext == null) return false;
            context = resolveAdoptedContext(action.RecordingId);
            return CanMatchRolloutAdoptionContext(context);
        }

        /// <summary>
        /// True for any <c>FundsSpending(VesselBuild)</c> action whose dedup
        /// metadata is well-formed enough to participate in duplicate
        /// detection. Adopted rows have no DedupKey so the prefix check is
        /// skipped for them — the context resolver will source their
        /// rollout-adoption context from the recording instead.
        /// </summary>
        private static bool IsRolloutAction(GameAction a)
        {
            if (a == null) return false;
            if (a.Type != GameActionType.FundsSpending) return false;
            if (a.FundsSpendingSource != FundsSpendingSource.VesselBuild) return false;
            // Unadopted rows must carry a structured rollout-prefixed key.
            if (string.IsNullOrEmpty(a.RecordingId))
            {
                if (string.IsNullOrEmpty(a.DedupKey)) return false;
                if (!a.DedupKey.StartsWith(LedgerOrchestrator.RolloutDedupPrefix, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        internal static GameAction TryAdoptRolloutAction(
            string recordingId,
            double startUT,
            Recording rec,
            System.Collections.Generic.IReadOnlyList<GameAction> actions)
        {
            RolloutAdoptionContext recordingContext;
            GameAction candidate = FindRolloutAdoptionCandidate(
                recordingId, startUT, rec, actions, out recordingContext);
            if (candidate == null)
                return null;
            ApplyRolloutAdoption(candidate, recordingId, startUT, recordingContext);
            return candidate;
        }

        /// <summary>
        /// The read half of <see cref="TryAdoptRolloutAction(string, double, Recording, System.Collections.Generic.IReadOnlyList{GameAction})"/>:
        /// the unclaimed rollout row the recording would adopt, left unchanged. The commit
        /// row build (design section 2.12 Rule 5) reads this so the merge dialog's
        /// dominance check can build a tree's rows before the tree commits.
        /// </summary>
        internal static GameAction FindRolloutAdoptionCandidate(
            string recordingId,
            double startUT,
            Recording rec,
            System.Collections.Generic.IReadOnlyList<GameAction> actions,
            out RolloutAdoptionContext recordingContext)
        {
            recordingContext = default(RolloutAdoptionContext);
            if (string.IsNullOrEmpty(recordingId)) return null;
            if (rec == null)
            {
                ParsekLog.Verbose(Tag,
                    $"TryAdoptRolloutAction: recording '{recordingId}' not found, cannot match rollout context");
                return null;
            }

            recordingContext = CreateRolloutAdoptionContext(rec);
            if (!CanMatchRolloutAdoptionContext(recordingContext))
            {
                ParsekLog.Verbose(Tag,
                    $"TryAdoptRolloutAction: recording '{recordingId}' missing rollout context " +
                    $"({FormatRolloutAdoptionContext(recordingContext)}), skipping adoption");
                return null;
            }

            if (actions == null)
                return null;

            for (int i = actions.Count - 1; i >= 0; i--)
            {
                var a = actions[i];
                if (a == null) continue;
                if (a.Type != GameActionType.FundsSpending) continue;
                if (a.FundsSpendingSource != FundsSpendingSource.VesselBuild) continue;
                if (!string.IsNullOrEmpty(a.RecordingId)) continue;
                if (string.IsNullOrEmpty(a.DedupKey)) continue;
                if (!a.DedupKey.StartsWith(LedgerOrchestrator.RolloutDedupPrefix, StringComparison.Ordinal)) continue;
                if (a.UT > startUT + 0.5) continue;
                if (startUT - a.UT > LedgerOrchestrator.RolloutAdoptionWindowSeconds) continue;
                if (!RolloutAdoptionContextsMatch(ParseRolloutAdoptionContext(a.DedupKey), recordingContext))
                    continue;
                return a;
            }

            ParsekLog.Verbose(Tag,
                $"TryAdoptRolloutAction: no unclaimed rollout action within {LedgerOrchestrator.RolloutAdoptionWindowSeconds:F0}s " +
                $"before startUT={startUT:F1} for recording '{recordingId}' " +
                $"with context {FormatRolloutAdoptionContext(recordingContext)}");
            return null;
        }

        /// <summary>
        /// The write half: tags <paramref name="a"/> (a row
        /// <see cref="FindRolloutAdoptionCandidate"/> returned) to the recording.
        /// </summary>
        internal static void ApplyRolloutAdoption(
            GameAction a,
            string recordingId,
            double startUT,
            RolloutAdoptionContext recordingContext)
        {
            if (a == null) return;
            string oldDedup = a.DedupKey;
            a.RecordingId = recordingId;
            a.DedupKey = null;

            // MUST bump, for the same reason the duplicate-swap above does and for
            // the same reason RecordingTreeSplitter's Step9 retag bumps explicitly.
            // Adoption changes which RECORDING an existing row belongs to, and the
            // consumers that answer per-recording questions cache against
            // Ledger.StateVersion: SupersedeCommit's world-action safety cache keys
            // on it (worldActionSafetyCacheLedgerVersion), so an unbumped adoption
            // leaves the safety gate answering "no world action" for a recording
            // that has just acquired one; EffectiveState.ComputeELS caches on the
            // same version. Same stale-cache class as
            // LEDGER-TRUNCATE-LEAVES-A-STALE-ELS-CACHE, which the L3 reading run
            // 2026-08-18_2136 measured live.
            Ledger.BumpStateVersion();

            ParsekLog.Info(Tag,
                $"TryAdoptRolloutAction: recording '{recordingId}' adopted rollout action " +
                $"(UT={a.UT:F1}, cost={a.FundsSpent:F0}, oldDedupKey={oldDedup}, " +
                $"startUT={startUT:F1}, lag={startUT - a.UT:F1}s, " +
                $"context={FormatRolloutAdoptionContext(recordingContext)}, " +
                $"stateVersion={Ledger.StateVersion.ToString(CultureInfo.InvariantCulture)})");
        }

        internal static bool CanRecordingAdoptRolloutAction(Recording rec)
        {
            if (rec == null || string.IsNullOrEmpty(rec.StartSituation))
                return false;

            return rec.StartSituation.Equals("Prelaunch", StringComparison.OrdinalIgnoreCase)
                || rec.StartSituation.Equals("PRELAUNCH", StringComparison.OrdinalIgnoreCase);
        }

        internal static RolloutAdoptionContext ResolveCurrentRolloutAdoptionContext()
        {
            try
            {
                RolloutAdoptionContext live;
                if (TryReadActiveVesselRolloutContextCore(out live))
                    return live;
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"ResolveCurrentRolloutAdoptionContext: ActiveVessel lookup failed: {ex.Message}");
            }

            return CreateRolloutAdoptionContext(
                0u,
                null,
                TryResolveLaunchSiteNameFromFlightDriver());
        }

        // FlightGlobals stays behind a NoInlining core so a headless host fails
        // inside the caller's try, not at the caller's JIT (mono).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool TryReadActiveVesselRolloutContextCore(out RolloutAdoptionContext context)
        {
            context = default(RolloutAdoptionContext);
            Vessel activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null)
                return false;

            string launchSiteName = FlightRecorder.ResolveLaunchSiteName(activeVessel, false);
            if (string.IsNullOrEmpty(launchSiteName))
                launchSiteName = TryResolveLaunchSiteNameFromFlightDriver();

            context = CreateRolloutAdoptionContext(
                activeVessel.persistentId,
                Recording.ResolveLocalizedName(activeVessel.vesselName),
                launchSiteName,
                FormatLaunchGuid(activeVessel.id));
            return true;
        }

        /// <summary>Canonical "N" form of a vessel launch guid; null for <see cref="Guid.Empty"/>.</summary>
        internal static string FormatLaunchGuid(Guid id)
        {
            return id == Guid.Empty ? null : id.ToString("N", CultureInfo.InvariantCulture);
        }

        private static string TryResolveLaunchSiteNameFromFlightDriver()
        {
            try
            {
                return FlightRecorder.HumanizeLaunchSiteName(FlightDriver.LaunchSiteName);
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag, $"TryResolveLaunchSiteNameFromFlightDriver failed: {ex.Message}");
                return null;
            }
        }

        internal static RolloutAdoptionContext CreateRolloutAdoptionContext(Recording rec)
        {
            if (rec == null)
                return default(RolloutAdoptionContext);

            return CreateRolloutAdoptionContext(
                rec.VesselPersistentId,
                rec.VesselName,
                rec.LaunchSiteName,
                rec.RecordedVesselGuid);
        }

        internal static RolloutAdoptionContext CreateRolloutAdoptionContext(
            uint vesselPersistentId,
            string vesselName,
            string launchSiteName,
            string vesselGuid = null)
        {
            return new RolloutAdoptionContext
            {
                VesselPersistentId = vesselPersistentId,
                VesselName = NormalizeRolloutContextText(vesselName),
                LaunchSiteName = NormalizeRolloutContextText(launchSiteName),
                VesselGuid = VesselLaunchIdentity.NormalizeGuid(vesselGuid)
            };
        }

        private static string NormalizeRolloutContextText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return value.Trim();
        }

        private static bool CanMatchRolloutAdoptionContext(RolloutAdoptionContext context)
        {
            if (context.VesselPersistentId != 0)
                return true;

            return !string.IsNullOrEmpty(context.VesselName)
                && !string.IsNullOrEmpty(context.LaunchSiteName);
        }

        private static bool RolloutAdoptionContextsMatch(
            RolloutAdoptionContext actionContext,
            RolloutAdoptionContext recordingContext)
        {
            if (actionContext.IsLegacyBareKey)
                return true;

            // A rollout row of another launch of the same craft shares the baked
            // persistentId; both launch guids known and different means the
            // recording did not fly this rollout. An unknown guid on either side
            // keeps the pid / name match below.
            if (VesselLaunchIdentity.GuidsConclusivelyDiffer(
                    actionContext.VesselGuid, recordingContext.VesselGuid))
                return false;

            if (actionContext.VesselPersistentId != 0 && recordingContext.VesselPersistentId != 0)
                return actionContext.VesselPersistentId == recordingContext.VesselPersistentId;

            return !string.IsNullOrEmpty(actionContext.VesselName)
                && !string.IsNullOrEmpty(recordingContext.VesselName)
                && !string.IsNullOrEmpty(actionContext.LaunchSiteName)
                && !string.IsNullOrEmpty(recordingContext.LaunchSiteName)
                && actionContext.VesselName.Equals(recordingContext.VesselName, StringComparison.OrdinalIgnoreCase)
                && actionContext.LaunchSiteName.Equals(recordingContext.LaunchSiteName, StringComparison.OrdinalIgnoreCase);
        }

        internal static string BuildRolloutDedupKey(double ut, RolloutAdoptionContext context)
        {
            string key = LedgerOrchestrator.RolloutDedupPrefix
                + ut.ToString("R", CultureInfo.InvariantCulture)
                + "|pid=" + context.VesselPersistentId.ToString(CultureInfo.InvariantCulture)
                + "|site=" + Uri.EscapeDataString(context.LaunchSiteName ?? string.Empty)
                + "|vessel=" + Uri.EscapeDataString(context.VesselName ?? string.Empty);
            string guid = VesselLaunchIdentity.NormalizeGuid(context.VesselGuid);
            if (guid != null)
                key += "|guid=" + guid;
            return key;
        }

        internal static RolloutAdoptionContext ParseRolloutAdoptionContext(string dedupKey)
        {
            if (string.IsNullOrEmpty(dedupKey)
                || !dedupKey.StartsWith(LedgerOrchestrator.RolloutDedupPrefix, StringComparison.Ordinal))
                return default(RolloutAdoptionContext);

            var context = default(RolloutAdoptionContext);
            string[] parts = dedupKey.Split('|');
            if (parts.Length == 1)
            {
                context.IsLegacyBareKey = true;
                return context;
            }

            for (int i = 1; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.StartsWith("pid=", StringComparison.Ordinal))
                {
                    uint.TryParse(
                        part.Substring(4),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out context.VesselPersistentId);
                }
                else if (part.StartsWith("site=", StringComparison.Ordinal))
                {
                    context.LaunchSiteName = NormalizeRolloutContextText(
                        Uri.UnescapeDataString(part.Substring(5)));
                }
                else if (part.StartsWith("vessel=", StringComparison.Ordinal))
                {
                    context.VesselName = NormalizeRolloutContextText(
                        Uri.UnescapeDataString(part.Substring(7)));
                }
                else if (part.StartsWith("guid=", StringComparison.Ordinal))
                {
                    context.VesselGuid = VesselLaunchIdentity.NormalizeGuid(part.Substring(5));
                }
            }

            return context;
        }

        private static string FormatRolloutAdoptionContext(RolloutAdoptionContext context)
        {
            return $"pid={context.VesselPersistentId}, vessel='{context.VesselName ?? "(null)"}', " +
                $"site='{context.LaunchSiteName ?? "(null)"}', guid={context.VesselGuid ?? "(none)"}";
        }
    }
}
