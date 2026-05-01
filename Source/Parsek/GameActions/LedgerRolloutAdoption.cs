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
        /// rollout. KSP's revert-to-launch flow reloads the launch quicksave but the
        /// re-launch fires <c>TransactionReasons.VesselRollout</c> at a near-identical
        /// UT (the launch quicksave's UT plus a few frames of physics tick), so the
        /// two emissions are within ~1s of each other in practice. Sized at 60s to
        /// also catch the larger gap that <c>RolloutAdoptionWindowSeconds=1800</c>
        /// allows between rollout and recording start while remaining well below any
        /// realistic gap between two genuinely independent launches of distinct
        /// vessels (different PIDs would not match anyway — see
        /// <see cref="RolloutDuplicateMatches"/>).
        /// </summary>
        internal const double RolloutDuplicateWindowSeconds = 60.0;

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

            // Revert double-rollout guard (#710): KSP fires VesselRollout on every
            // launch, including immediately after a Revert-to-Launch from a flight
            // that had already fired one. The two emissions land at near-identical
            // UTs (~40 ms apart in the production repro because the revert clock
            // rolled back) but the existing UT-embedded dedup key cannot collapse
            // them. Treat any unadopted matching rollout for the same
            // (pid|site|vessel|cost) within RolloutDuplicateWindowSeconds as the
            // same logical rollout, even when the new write would have the strictly
            // smaller UT. KSP refunded the original deduction during the revert and
            // KspStatePatcher re-applied it from the surviving ledger row, so no
            // funds were silently dropped — the second write would be the
            // double-charge.
            //
            // Cluster invariant: the surviving row's UT is always the minimum of
            // the cluster's UTs, regardless of write order or adoption state. The
            // load-time repair pass at <see cref="RepairDuplicateRolloutActions"/>
            // upholds the same invariant. When the new write has the strictly
            // smaller UT (revert-relaunch with a clock rollback >0 ms), the
            // surviving row's UT/DedupKey/Sequence are mutated in place to match
            // the relaunch timeline. Without that mutation, downstream consumers
            // — most importantly <see cref="TryAdoptRolloutAction"/>'s 0.5 s
            // post-startUT cap — would skip the surviving row because its UT is
            // still in the original (now-rolled-back) timeline.
            if (TryFindDuplicateRolloutAction(ut, cost, context, Ledger.Actions, out GameAction existing))
            {
                if (ut < existing.UT)
                {
                    double oldUT = existing.UT;
                    string oldDedupKey = existing.DedupKey;
                    int oldSequence = existing.Sequence;

                    existing.UT = ut;
                    existing.DedupKey = BuildRolloutDedupKey(ut, context);
                    existing.Sequence = allocateKscSequence();

                    Ledger.BumpStateVersion();

                    ParsekLog.Info(Tag,
                        $"OnVesselRolloutSpending: skipping duplicate rollout — swapped surviving row to relaunch UT " +
                        $"(oldUT={oldUT.ToString("R", CultureInfo.InvariantCulture)}, " +
                        $"newUT={ut.ToString("R", CultureInfo.InvariantCulture)}, " +
                        $"deltaUT={(ut - oldUT).ToString("F3", CultureInfo.InvariantCulture)}s, " +
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
                        $"cost={cost:F1}, context={FormatRolloutAdoptionContext(context)}, " +
                        $"existingDedupKey={existing.DedupKey ?? "(null)"})");
                }
                return;
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

            Ledger.AddAction(action);

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
            match = null;
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
                if (!RolloutDuplicateMatches(existingContext, context)) continue;

                match = a;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Pure dedup-context comparison for rollout duplicates. Differs from
        /// <see cref="RolloutAdoptionContextsMatch"/> by ignoring the
        /// <c>IsLegacyBareKey</c> escape hatch — duplicate detection requires the
        /// stored key to retain its structured fields so PID-bearing keys cannot
        /// silently latch onto a same-UT bare-key entry from a much older save.
        /// </summary>
        private static bool RolloutDuplicateMatches(
            RolloutAdoptionContext existing,
            RolloutAdoptionContext candidate)
        {
            if (existing.IsLegacyBareKey)
                return false;

            // Strongest match: same KSP persistentId. KSP preserves the active
            // vessel's persistentId across Revert-to-Launch (the revert reloads the
            // launch quicksave, which serialized that PID), so the original rollout
            // and the revert-relaunch rollout share a PID. Two genuinely independent
            // vessel launches always have distinct PIDs.
            if (existing.VesselPersistentId != 0 && candidate.VesselPersistentId != 0)
                return existing.VesselPersistentId == candidate.VesselPersistentId;

            // Fallback when one side is missing the PID (legacy or pre-resolution
            // state): require name + site to both be present and equal. Safer than
            // the adoption matcher's ordinal-ignore-case match for safety -- for
            // duplicate detection we want to err on "do not dedup" rather than
            // "silently dedup unrelated launches."
            return !string.IsNullOrEmpty(existing.VesselName)
                && !string.IsNullOrEmpty(candidate.VesselName)
                && !string.IsNullOrEmpty(existing.LaunchSiteName)
                && !string.IsNullOrEmpty(candidate.LaunchSiteName)
                && existing.VesselName.Equals(candidate.VesselName, StringComparison.OrdinalIgnoreCase)
                && existing.LaunchSiteName.Equals(candidate.LaunchSiteName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Load-time repair pass for already-corrupted saves: scans the in-memory
        /// ledger for clusters of VesselRollout actions that share the same
        /// logical rollout context (PID-or-name+site, near-equal cost, UT within
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
        ///   the unadopted candidate without rewriting anchor's ActionId
        ///   (nothing references the unadopted ActionId — adoption never
        ///   claimed it, no tombstone exists for FundsSpending(VesselBuild),
        ///   and FundsModule's per-ActionId rate-limit cache is recomputed
        ///   from scratch on the next recalc).</description></item>
        ///   <item><description><b>Anchor unadopted, candidate adopted</b> —
        ///   same rule applied symmetrically: keep the adopted candidate as
        ///   the cluster survivor and drop the unadopted anchor. Done by
        ///   marking the anchor index for removal and breaking out of the
        ///   inner scan since the anchor is dead.</description></item>
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
                        // ActionId is NOT rewritten because the adopted row's
                        // ActionId is the recording's authoritative reference
                        // and the unadopted ActionId has no consumers.
                        ParsekLog.Info(Tag,
                            $"RepairDuplicateRolloutActions: removing duplicate rollout — adopted anchor wins " +
                            $"(removedUT={candidate.UT.ToString("R", CultureInfo.InvariantCulture)}, " +
                            $"keptUT={anchor.UT.ToString("R", CultureInfo.InvariantCulture)}, " +
                            $"keptRecordingId={anchor.RecordingId}, " +
                            $"cost={candidate.FundsSpent:F1}, " +
                            $"context={FormatRolloutAdoptionContext(anchorContext)}, " +
                            $"removedDedupKey={candidate.DedupKey ?? "(null)"})");

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
                        // index. Symmetric to the previous branch: we never
                        // rewrite the candidate's ActionId.
                        ParsekLog.Info(Tag,
                            $"RepairDuplicateRolloutActions: removing duplicate rollout — adopted candidate wins " +
                            $"(removedUT={anchor.UT.ToString("R", CultureInfo.InvariantCulture)}, " +
                            $"keptUT={candidate.UT.ToString("R", CultureInfo.InvariantCulture)}, " +
                            $"keptRecordingId={candidate.RecordingId}, " +
                            $"cost={anchor.FundsSpent:F1}, " +
                            $"context={FormatRolloutAdoptionContext(candidateContext)}, " +
                            $"removedDedupKey={anchor.DedupKey ?? "(null)"})");

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
                        anchor.DedupKey = candidate.DedupKey;
                        anchor.Sequence = candidate.Sequence;
                        anchor.ActionId = candidate.ActionId;
                    }

                    ParsekLog.Info(Tag,
                        $"RepairDuplicateRolloutActions: removing duplicate rollout " +
                        $"(removedUT={removedUT.ToString("R", CultureInfo.InvariantCulture)}, " +
                        $"keptUT={anchor.UT.ToString("R", CultureInfo.InvariantCulture)}, " +
                        $"cost={candidate.FundsSpent:F1}, " +
                        $"context={FormatRolloutAdoptionContext(anchorContext)}, " +
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
            if (string.IsNullOrEmpty(recordingId)) return null;
            if (rec == null)
            {
                ParsekLog.Verbose(Tag,
                    $"TryAdoptRolloutAction: recording '{recordingId}' not found, cannot match rollout context");
                return null;
            }

            RolloutAdoptionContext recordingContext = CreateRolloutAdoptionContext(rec);
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

                string oldDedup = a.DedupKey;
                a.RecordingId = recordingId;
                a.DedupKey = null;
                ParsekLog.Info(Tag,
                    $"TryAdoptRolloutAction: recording '{recordingId}' adopted rollout action " +
                    $"(UT={a.UT:F1}, cost={a.FundsSpent:F0}, oldDedupKey={oldDedup}, " +
                    $"startUT={startUT:F1}, lag={startUT - a.UT:F1}s, " +
                    $"context={FormatRolloutAdoptionContext(recordingContext)})");
                return a;
            }

            ParsekLog.Verbose(Tag,
                $"TryAdoptRolloutAction: no unclaimed rollout action within {LedgerOrchestrator.RolloutAdoptionWindowSeconds:F0}s " +
                $"before startUT={startUT:F1} for recording '{recordingId}' " +
                $"with context {FormatRolloutAdoptionContext(recordingContext)}");
            return null;
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
                Vessel activeVessel = FlightGlobals.ActiveVessel;
                if (activeVessel != null)
                {
                    string launchSiteName = FlightRecorder.ResolveLaunchSiteName(activeVessel, false);
                    if (string.IsNullOrEmpty(launchSiteName))
                        launchSiteName = TryResolveLaunchSiteNameFromFlightDriver();

                    return CreateRolloutAdoptionContext(
                        activeVessel.persistentId,
                        Recording.ResolveLocalizedName(activeVessel.vesselName),
                        launchSiteName);
                }
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
                rec.LaunchSiteName);
        }

        internal static RolloutAdoptionContext CreateRolloutAdoptionContext(
            uint vesselPersistentId,
            string vesselName,
            string launchSiteName)
        {
            return new RolloutAdoptionContext
            {
                VesselPersistentId = vesselPersistentId,
                VesselName = NormalizeRolloutContextText(vesselName),
                LaunchSiteName = NormalizeRolloutContextText(launchSiteName)
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

            if (actionContext.VesselPersistentId != 0 && recordingContext.VesselPersistentId != 0)
                return actionContext.VesselPersistentId == recordingContext.VesselPersistentId;

            return !string.IsNullOrEmpty(actionContext.VesselName)
                && !string.IsNullOrEmpty(recordingContext.VesselName)
                && !string.IsNullOrEmpty(actionContext.LaunchSiteName)
                && !string.IsNullOrEmpty(recordingContext.LaunchSiteName)
                && actionContext.VesselName.Equals(recordingContext.VesselName, StringComparison.OrdinalIgnoreCase)
                && actionContext.LaunchSiteName.Equals(recordingContext.LaunchSiteName, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildRolloutDedupKey(double ut, RolloutAdoptionContext context)
        {
            return LedgerOrchestrator.RolloutDedupPrefix
                + ut.ToString("R", CultureInfo.InvariantCulture)
                + "|pid=" + context.VesselPersistentId.ToString(CultureInfo.InvariantCulture)
                + "|site=" + Uri.EscapeDataString(context.LaunchSiteName ?? string.Empty)
                + "|vessel=" + Uri.EscapeDataString(context.VesselName ?? string.Empty);
        }

        private static RolloutAdoptionContext ParseRolloutAdoptionContext(string dedupKey)
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
            }

            return context;
        }

        private static string FormatRolloutAdoptionContext(RolloutAdoptionContext context)
        {
            return $"pid={context.VesselPersistentId}, vessel='{context.VesselName ?? "(null)"}', " +
                $"site='{context.LaunchSiteName ?? "(null)"}'";
        }
    }
}
