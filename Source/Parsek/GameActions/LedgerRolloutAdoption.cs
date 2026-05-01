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
            if (TryFindDuplicateRolloutAction(ut, cost, context, Ledger.Actions, out GameAction existing))
            {
                ParsekLog.Info(Tag,
                    $"OnVesselRolloutSpending: skipping duplicate rollout — existing " +
                    $"unadopted rollout matches (existingUT={existing.UT.ToString("R", CultureInfo.InvariantCulture)}, " +
                    $"newUT={ut.ToString("R", CultureInfo.InvariantCulture)}, " +
                    $"deltaUT={(ut - existing.UT).ToString("F3", CultureInfo.InvariantCulture)}s, " +
                    $"cost={cost:F1}, context={FormatRolloutAdoptionContext(context)}, " +
                    $"existingDedupKey={existing.DedupKey ?? "(null)"})");
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
        /// ledger for clusters of unadopted VesselRollout actions that share the
        /// same logical rollout context (PID-or-name+site, near-equal cost, UT
        /// within <see cref="RolloutDuplicateWindowSeconds"/>) and collapses each
        /// cluster down to its earliest member. Idempotent — a second invocation
        /// on a healthy ledger is a no-op. Returns the number of actions removed
        /// so callers can suppress chatty logs when nothing changed. The single
        /// INFO line summarising the dedup is emitted only when at least one
        /// action was removed.
        ///
        /// <para>Only operates on actions whose <see cref="GameAction.RecordingId"/>
        /// is null/empty: an adopted rollout (RecordingId set) is the recording's
        /// authoritative cost row and a sibling unadopted row pointing at the same
        /// vessel is a different bug (e.g. failed adoption) that this pass must
        /// not silently swallow. Driven by callers that own the Ledger mutator
        /// surface (<see cref="LedgerOrchestrator.OnKspLoad"/>) — the
        /// <paramref name="removeAt"/> callback abstracts the removal so this
        /// helper can stay in the rollout-adoption file and tests can assert
        /// against an in-memory list.</para>
        /// </summary>
        internal static int RepairDuplicateRolloutActions(
            System.Collections.Generic.IList<GameAction> actions,
            Action<int> removeAt)
        {
            if (actions == null || removeAt == null) return 0;

            int removed = 0;
            // Walk highest index first so removeAt() does not invalidate the
            // surviving outer index. Each "kept" action becomes the anchor; later
            // (smaller-index, earlier-UT) entries that match it stay; later
            // (larger-index, later-UT) entries that match it get reaped.
            // Implementation: for each action i (low-to-high), scan j>i for a
            // duplicate; if found, mark j for removal. Then collapse.
            var toRemove = new System.Collections.Generic.List<int>();
            var alreadyMarked = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < actions.Count; i++)
            {
                if (alreadyMarked.Contains(i)) continue;
                var anchor = actions[i];
                if (!IsUnadoptedRolloutAction(anchor)) continue;

                var anchorContext = ParseRolloutAdoptionContext(anchor.DedupKey);
                if (anchorContext.IsLegacyBareKey) continue;
                if (!CanMatchRolloutAdoptionContext(anchorContext)) continue;

                for (int j = i + 1; j < actions.Count; j++)
                {
                    if (alreadyMarked.Contains(j)) continue;
                    var candidate = actions[j];
                    if (!IsUnadoptedRolloutAction(candidate)) continue;
                    if (Math.Abs(candidate.UT - anchor.UT) > RolloutDuplicateWindowSeconds) continue;
                    if (Math.Abs(candidate.FundsSpent - anchor.FundsSpent) > RolloutDuplicateCostEpsilon) continue;

                    var candidateContext = ParseRolloutAdoptionContext(candidate.DedupKey);
                    if (!RolloutDuplicateMatches(anchorContext, candidateContext)) continue;

                    // Reap the duplicate at index j. Earliest-UT preference: when
                    // the candidate's UT is strictly less than the anchor's, swap
                    // the anchor's payload onto the candidate's data first
                    // (mirrors the production case where the revert-relaunch row
                    // landed with a smaller UT due to clock rollback). Swapping
                    // the payload (UT, DedupKey, Sequence, ActionId) keeps the
                    // anchor's index stable so previously-marked siblings stay
                    // reaped, and reflects the legitimate rollout in the user's
                    // current timeline. The pre-swap "removedUT" is captured here
                    // so the log line shows the row that physically went away
                    // even when the swap re-points the anchor at the smaller UT.
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

            // Remove highest index first.
            toRemove.Sort();
            for (int k = toRemove.Count - 1; k >= 0; k--)
                removeAt(toRemove[k]);

            return removed;
        }

        private static bool IsUnadoptedRolloutAction(GameAction a)
        {
            if (a == null) return false;
            if (a.Type != GameActionType.FundsSpending) return false;
            if (a.FundsSpendingSource != FundsSpendingSource.VesselBuild) return false;
            if (!string.IsNullOrEmpty(a.RecordingId)) return false;
            if (string.IsNullOrEmpty(a.DedupKey)) return false;
            if (!a.DedupKey.StartsWith(LedgerOrchestrator.RolloutDedupPrefix, StringComparison.Ordinal)) return false;
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
