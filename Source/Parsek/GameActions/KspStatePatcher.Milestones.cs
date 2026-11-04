using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Contracts;

namespace Parsek
{
    // Milestone and progress-tree patch, including repeatable record nodes.
    internal static partial class KspStatePatcher
    {
        /// <summary>
        /// Patches KSP's ProgressNode achievement tree to match the module's credited milestones.
        /// Iterates the tree recursively, setting reached/complete flags via reflection for
        /// normal once-ever nodes and rebuilding full stock interval state for repeatable
        /// Records* nodes.
        ///
        /// Body-specific nodes (under CelestialBodySubtree) are matched using path-qualified
        /// IDs ("Mun/Landing"), while top-level nodes use bare IDs ("FirstLaunch").
        /// A legacy bare body-specific ID ("Orbit" instead of "Kerbin/Orbit") matches only
        /// under the home body (<see cref="IsLegacyBareIdSubtree"/>).
        ///
        /// Repeatable Records* nodes have two patch modes: normal same-branch recalculations
        /// preserve a live in-tier best value when it still fits the currently paid reward
        /// band, while authoritative rewind-style patching rebuilds strictly from ledger-
        /// backed hit counts so discarded future progress cannot leak through.
        ///
        /// Progress that predates the ledger (a career Parsek was installed into) reaches
        /// the module through the save's <see cref="PreLedgerProgressSeed"/>: seeded one-shot
        /// ids are credited and seeded record thresholds add to the hit count. Until that
        /// seed is captured the whole patch is skipped, because the credited set cannot yet
        /// tell pre-ledger progress from progress the ledger rewound away.
        ///
        /// Note: After clearing a node, KSP's ProgressTracking.Update() may immediately
        /// re-trigger it if conditions are still met (e.g., a vessel is in orbit). This is
        /// correct behavior — milestones whose conditions are still satisfied SHOULD remain
        /// achieved. The IsReplayingActions flag suppresses GameStateRecorder from capturing
        /// re-triggered milestones as new events.
        /// </summary>
        internal static void PatchMilestones(MilestonesModule milestones,
            bool authoritativeRepeatableRecordState = false)
        {
            if (milestones == null)
            {
                ParsekLog.Warn(Tag, "PatchMilestones: null module — skipping");
                return;
            }

            // The credited set only knows ledger rows. Until the save's pre-ledger progress
            // seed is captured, a node achieved before Parsek was installed reads as
            // un-credited, and patching would clear it before the capture can record it.
            if (!milestones.HasProgressSeed)
            {
                VerboseStablePatchState("patch-skip|milestones|progress-seed", "seed-not-captured",
                    "PatchMilestones: pre-ledger progress seed not captured yet - skipping so " +
                    "progress the ledger has not recorded is not cleared");
                return;
            }

            if (ProgressTracking.Instance == null)
            {
                VerboseStablePatchState("patch-skip|milestones|progress-tracking", "progress-null",
                    "PatchMilestones: ProgressTracking.Instance is null — skipping");
                return;
            }

            var tree = ProgressTracking.Instance.achievementTree;
            if (tree == null)
            {
                ParsekLog.Warn(Tag, "PatchMilestones: achievementTree is null — skipping");
                return;
            }

            // Cache reflection FieldInfo/PropertyInfo once for the private/protected fields
            var reachedField = typeof(ProgressNode).GetField("reached",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var completeField = typeof(ProgressNode).GetField("complete",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var mannedProp = typeof(ProgressNode).GetProperty("IsCompleteManned",
                BindingFlags.Public | BindingFlags.Instance);
            var unmannedProp = typeof(ProgressNode).GetProperty("IsCompleteUnmanned",
                BindingFlags.Public | BindingFlags.Instance);

            if (reachedField == null || completeField == null)
            {
                ParsekLog.Warn(Tag,
                    "PatchMilestones: could not find reached/complete fields via reflection " +
                    $"(reached={reachedField != null}, complete={completeField != null}) — skipping");
                return;
            }

            if (mannedProp == null || unmannedProp == null)
            {
                ParsekLog.Warn(Tag,
                    "PatchMilestones: could not find IsCompleteManned/IsCompleteUnmanned properties " +
                    $"(manned={mannedProp != null}, unmanned={unmannedProp != null}) — will skip manned/unmanned flags");
            }

            int credited = 0, unreached = 0, skipped = 0;
            int seedKept = 0, seedRecordBands = 0;

            // LedgerTrace Tier-B coverage for milestones (gap 6) is intentionally
            // DEFERRED from v1: the Prompt-4 Tier-B enumeration is "subject / node /
            // facility / contract id" and omits milestones, and there is no #1098
            // changed-set for the per-node reached/complete flips below. Add a
            // milestone changed-set (and a presence read-back) when Tier-B is extended
            // to milestones; until then milestone changes are not traced.
            PatchProgressNodeTree(tree, milestones, reachedField, completeField,
                mannedProp, unmannedProp,
                "", authoritativeRepeatableRecordState,
                ref credited, ref unreached, ref skipped,
                ref seedKept, ref seedRecordBands);

            ParsekLog.Info(Tag,
                $"PatchMilestones: credited={credited.ToString(IC)}, " +
                $"unreached={unreached.ToString(IC)}, " +
                $"skipped={skipped.ToString(IC)}, " +
                $"moduleCredited={milestones.GetCreditedCount().ToString(IC)}");

            if (milestones.PreLedgerSeedOneShotCount > 0 || milestones.PreLedgerSeedRecordCount > 0)
            {
                string seedState = string.Format(IC, "{0}|{1}|{2}|{3}",
                    seedKept, seedRecordBands,
                    milestones.PreLedgerSeedOneShotCount, milestones.PreLedgerSeedRecordCount);
                VerboseStablePatchState("patch-milestones-pre-ledger-seed", seedState,
                    string.Format(IC,
                        "PatchMilestones: pre-ledger progress seed kept {0} node(s) and {1} record band(s) " +
                        "(seed holds {2} node(s), {3} record(s))",
                        seedKept, seedRecordBands,
                        milestones.PreLedgerSeedOneShotCount, milestones.PreLedgerSeedRecordCount));
            }
        }

        /// <summary>
        /// The one subtree a legacy bare body-child milestone id ("Orbit", "Landing") may
        /// name. Such rows carry no body, and every body's subtree has a child of that name,
        /// so matching them under any body marked every body's node complete (one Kerbin
        /// orbit row completed Mun/Orbit, Minmus/Orbit, ... and stock then never paid them).
        /// The capture writes "Body/Id" for every body-specific node, so a bare row is
        /// legacy data; legacy rows were overwhelmingly home-body ones, and no wider
        /// compatibility is kept.
        /// </summary>
        internal const string LegacyBareIdBodyName = "Kerbin";

        internal static bool IsLegacyBareIdSubtree(string pathPrefix)
        {
            return string.Equals(pathPrefix, LegacyBareIdBodyName, StringComparison.Ordinal);
        }

        /// <summary>
        /// Recursively patches a ProgressTree's nodes to match credited milestone state.
        /// For each node, computes a qualified ID (body nodes get "BodyName/NodeId"),
        /// checks whether it should be achieved, and sets/clears flags via reflection.
        /// Repeatable Records* nodes are handled specially: their effective hit count drives
        /// reached/complete plus the stock numeric record interval state.
        ///
        /// Matching logic: tries qualified path first ("Mun/Landing"), then falls back to
        /// bare node.Id ("Landing") for a legacy bare row, under the home body's subtree
        /// only (<see cref="IsLegacyBareIdSubtree"/>).
        ///
        /// Internal static for testability — the reflection FieldInfos are passed in.
        /// </summary>
        internal static void PatchProgressNodeTree(
            ProgressTree tree, MilestonesModule milestones,
            FieldInfo reachedField, FieldInfo completeField,
            PropertyInfo mannedProp, PropertyInfo unmannedProp,
            string pathPrefix,
            bool authoritativeRepeatableRecordState,
            ref int credited, ref int unreached, ref int skipped,
            ref int seedKept, ref int seedRecordBands)
        {
            for (int i = 0; i < tree.Count; i++)
            {
                var node = tree[i];
                if (node == null) continue;

                string nodeId = node.Id ?? "";
                string qualifiedId = string.IsNullOrEmpty(pathPrefix)
                    ? nodeId
                    : pathPrefix + "/" + nodeId;

                int effectiveCount = milestones.GetEffectiveMilestoneCount(qualifiedId);
                if (PatchRepeatableRecordNode(
                    node, effectiveCount, qualifiedId, reachedField, completeField,
                    authoritativeRepeatableRecordState, out bool repeatableChanged))
                {
                    if (milestones.GetPreLedgerRecordPaidCount(qualifiedId) > 0)
                        seedRecordBands++;
                    if (repeatableChanged)
                    {
                        if (effectiveCount > 0)
                            credited++;
                        else
                            unreached++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
                else
                {
                    // Check if this milestone should be achieved.
                    // Try qualified path first, then bare Id for backward compat on top-level nodes.
                    bool shouldBeAchieved = milestones.IsMilestoneCredited(qualifiedId);
                    if (!shouldBeAchieved && string.IsNullOrEmpty(pathPrefix))
                    {
                        // Top-level node — bare Id is already the qualified path, no fallback needed
                    }
                    else if (!shouldBeAchieved && IsLegacyBareIdSubtree(pathPrefix))
                    {
                        // Home-body subtree child: a legacy bare row ("Orbit") names it.
                        if (milestones.IsMilestoneCredited(nodeId))
                        {
                            shouldBeAchieved = true;
                            // Bug #594: this diagnostic fires every recalc walk for the
                            // same (nodeId, qualifiedId) pair — once a fallback match
                            // exists for an old recording, every walk re-emits the
                            // same line. Rate-limit per pair so the diagnostic still
                            // surfaces when a new fallback path appears, without
                            // flooding the log on steady-state walks.
                            string fallbackKey = string.Format(IC,
                                "patch-milestones-fallback-{0}-{1}",
                                nodeId, qualifiedId);
                            ParsekLog.VerboseRateLimited(Tag, fallbackKey,
                                $"PatchMilestones: bare-Id fallback match for '{nodeId}' " +
                                $"(qualified='{qualifiedId}' not found — old recording?)");
                        }
                    }

                    bool isCurrentlyAchieved = node.IsComplete;
                    if (shouldBeAchieved && milestones.IsPreLedgerSeeded(qualifiedId))
                        seedKept++;

                    if (shouldBeAchieved && !isCurrentlyAchieved)
                    {
                        // Set achieved via reflection (avoid Complete() which fires GameEvents)
                        reachedField.SetValue(node, true);
                        completeField.SetValue(node, true);
                        credited++;

                        ParsekLog.Verbose(Tag,
                            $"PatchMilestones: set achieved '{qualifiedId}'");
                    }
                    else if (!shouldBeAchieved && isCurrentlyAchieved)
                    {
                        // Un-achieve via reflection (no public setters available)
                        reachedField.SetValue(node, false);
                        completeField.SetValue(node, false);
                        if (mannedProp != null) mannedProp.SetValue(node, false, null);
                        if (unmannedProp != null) unmannedProp.SetValue(node, false, null);
                        unreached++;

                        ParsekLog.Verbose(Tag,
                            $"PatchMilestones: cleared achieved '{qualifiedId}'");
                    }
                    else
                    {
                        skipped++;
                    }
                }

                // Recurse into subtree children
                if (node.Subtree != null && node.Subtree.Count > 0)
                {
                    PatchProgressNodeTree(node.Subtree, milestones,
                        reachedField, completeField, mannedProp, unmannedProp,
                        qualifiedId, authoritativeRepeatableRecordState,
                        ref credited, ref unreached, ref skipped,
                        ref seedKept, ref seedRecordBands);
                }
            }
        }

        internal struct RepeatableRecordState
        {
            public bool Reached;
            public bool Complete;
            public double Record;
            public double RewardThreshold;
            public int RewardInterval;
        }

        /// <summary>
        /// Computes the stock save-state shape for KSP's repeatable record nodes from the
        /// number of effective ledger hits that survived recalculation. Used by
        /// authoritative rewind-style patching where only ledger-backed paid thresholds
        /// may survive.
        /// </summary>
        internal static bool TryComputeRepeatableRecordState(
            ProgressNode node, int effectiveCount, out RepeatableRecordState state)
        {
            return TryComputeRepeatableRecordState(
                node, effectiveCount, currentRecord: double.NaN, out state);
        }

        /// <summary>
        /// Computes the stock save-state shape for KSP's repeatable record nodes from the
        /// number of effective ledger hits that survived recalculation, while preserving the
        /// live best-value progress when it still fits inside that reward band, including
        /// the initial unpaid band before the first ledger-backed hit exists.
        /// </summary>
        internal static bool TryComputeRepeatableRecordState(
            ProgressNode node, int effectiveCount, double currentRecord,
            out RepeatableRecordState state)
        {
            state = default;
            if (!TryGetRepeatableRecordDefinition(node, out double maxRecord, out double roundValue))
                return false;

            double boundedCurrentRecord = NormalizeRepeatableRecordValue(currentRecord, maxRecord);
            if (effectiveCount <= 0)
            {
                int initialInterval = 1;
                double initialThreshold = FinePrint.Utilities.ProgressUtilities.FindNextRecord(
                    0.0, maxRecord, roundValue, ref initialInterval);

                if (boundedCurrentRecord > RecordStateEpsilon &&
                    !double.IsInfinity(initialThreshold) &&
                    !double.IsNaN(initialThreshold) &&
                    initialThreshold != double.MaxValue &&
                    boundedCurrentRecord < initialThreshold - RecordStateEpsilon)
                {
                    state.Reached = true;
                    state.Complete = false;
                    state.Record = boundedCurrentRecord;
                    state.RewardThreshold = initialThreshold;
                    state.RewardInterval = initialInterval;
                    return true;
                }

                state.Reached = false;
                state.Complete = false;
                state.Record = 0.0;
                state.RewardThreshold = 0.0;
                state.RewardInterval = 1;
                return true;
            }

            double paidRecord = 0.0;
            int clampedEffectiveCount = Math.Max(0, effectiveCount);
            for (int i = 0; i < clampedEffectiveCount; i++)
            {
                int interval = 1;
                double nextRecord = FinePrint.Utilities.ProgressUtilities.FindNextRecord(
                    paidRecord, maxRecord, roundValue, ref interval);
                if (double.IsInfinity(nextRecord) || double.IsNaN(nextRecord) ||
                    nextRecord == double.MaxValue)
                {
                    state.Reached = true;
                    state.Complete = true;
                    state.Record = maxRecord;
                    state.RewardThreshold = 0.0;
                    state.RewardInterval = 1;
                    return true;
                }

                paidRecord = nextRecord;
            }

            int nextInterval = 1;
            double nextThreshold = FinePrint.Utilities.ProgressUtilities.FindNextRecord(
                paidRecord, maxRecord, roundValue, ref nextInterval);
            bool isComplete = nextThreshold == double.MaxValue ||
                paidRecord >= maxRecord - RecordStateEpsilon;

            double record = paidRecord;
            if (isComplete)
            {
                record = maxRecord;
            }
            else if (boundedCurrentRecord > paidRecord + RecordStateEpsilon &&
                boundedCurrentRecord < nextThreshold - RecordStateEpsilon)
            {
                // Preserve the live best value as long as it still fits within the reward
                // band implied by the effective hit count. If it spills into a later band,
                // the extra rewards did not survive recalculation, so we fall back to the
                // last paid threshold instead of inventing an in-between value.
                record = boundedCurrentRecord;
            }

            state.Reached = record > RecordStateEpsilon;
            state.Complete = isComplete;
            state.Record = record;
            state.RewardThreshold = isComplete ? 0.0 : nextThreshold;
            state.RewardInterval = isComplete ? 1 : nextInterval;
            return true;
        }

        // Upper bound on the reward bands the inverse below enumerates. Stock has
        // ContractDefs.Progression.RecordSplit thresholds (5 by default); the walk stops at
        // the first complete band, so this only guards a pathological config.
        private const int MaxRepeatableRecordBands = 256;

        /// <summary>
        /// Inverse of <see cref="TryComputeRepeatableRecordState(ProgressNode, int, out RepeatableRecordState)"/>:
        /// how many reward thresholds the LIVE state of a repeatable record node says stock
        /// has already paid. A complete node has paid every threshold (the smallest count
        /// whose computed state is complete). Otherwise the count is the k whose computed
        /// next threshold equals the live <c>rewardThreshold</c> (k = 0 matches the first
        /// threshold), and when no k matches (an unset threshold, as Parsek's own unreached
        /// patch leaves it) the band the live best value sits in: stock re-derives the next
        /// threshold from <c>record</c> on load, so the record alone implies the same count.
        /// Returns false when the node is not a recognized record node.
        /// </summary>
        internal static bool TryComputeRepeatableRecordImpliedPaidCount(
            ProgressNode node, bool liveComplete, double liveRecord, double liveRewardThreshold,
            out int paidCount)
        {
            paidCount = 0;
            if (!TryGetRepeatableRecordDefinition(node, out double maxRecord, out double roundValue))
                return false;

            RepeatableRecordState state;
            if (liveComplete)
            {
                for (int k = 1; k <= MaxRepeatableRecordBands; k++)
                {
                    if (!TryComputeRepeatableRecordState(node, k, out state))
                        return false;
                    if (state.Complete)
                    {
                        paidCount = k;
                        return true;
                    }
                }
                return false;
            }

            if (!double.IsNaN(liveRewardThreshold) && liveRewardThreshold > RecordStateEpsilon)
            {
                int initialInterval = 1;
                double initialThreshold = FinePrint.Utilities.ProgressUtilities.FindNextRecord(
                    0.0, maxRecord, roundValue, ref initialInterval);
                for (int k = 0; k <= MaxRepeatableRecordBands; k++)
                {
                    double nextThreshold;
                    if (k == 0)
                    {
                        nextThreshold = initialThreshold;
                    }
                    else
                    {
                        if (!TryComputeRepeatableRecordState(node, k, out state) || state.Complete)
                            break;
                        nextThreshold = state.RewardThreshold;
                    }

                    if (Math.Abs(nextThreshold - liveRewardThreshold) <= RecordStateEpsilon)
                    {
                        paidCount = k;
                        return true;
                    }
                    if (nextThreshold > liveRewardThreshold + RecordStateEpsilon)
                        break;
                }
            }

            double boundedRecord = NormalizeRepeatableRecordValue(liveRecord, maxRecord);
            int best = 0;
            for (int k = 1; k <= MaxRepeatableRecordBands; k++)
            {
                if (!TryComputeRepeatableRecordState(node, k, out state))
                    return false;
                if (state.Complete)
                {
                    if (boundedRecord >= maxRecord - RecordStateEpsilon)
                        best = k;
                    break;
                }
                if (state.Record > boundedRecord + RecordStateEpsilon)
                    break;
                best = k;
            }

            paidCount = best;
            return true;
        }

        /// <summary>
        /// Reads the live <c>record</c> / <c>rewardThreshold</c> of a repeatable record node
        /// and resolves its implied paid threshold count. False when the node is not a
        /// record node or is missing the stock record fields.
        /// </summary>
        internal static bool TryReadRepeatableRecordImpliedPaidCount(ProgressNode node, out int paidCount)
        {
            paidCount = 0;
            if (!IsRepeatableRecordType(node))
                return false;

            Type nodeType = node.GetType();
            FieldInfo recordField = nodeType.GetField("record",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo rewardThresholdField = nodeType.GetField("rewardThreshold",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (recordField == null || rewardThresholdField == null)
                return false;

            double liveRecord = NormalizeRepeatableRecordValue(recordField.GetValue(node), double.MaxValue);
            object thresholdValue = rewardThresholdField.GetValue(node);
            double liveRewardThreshold = thresholdValue is double d ? d : 0.0;
            return TryComputeRepeatableRecordImpliedPaidCount(
                node, node.IsComplete, liveRecord, liveRewardThreshold, out paidCount);
        }

        /// <summary>
        /// Flattens the live progress tree into the observations the pre-ledger progress
        /// seed capture decides from. Qualification mirrors <see cref="PatchProgressNodeTree"/>
        /// exactly (bare id at the top level, "Parent/NodeId" below it, the node's own id as
        /// the legacy bare fallback for home-body subtree nodes), so a seeded id is the id the patch
        /// looks up.
        /// </summary>
        internal static List<PreLedgerProgressSeed.NodeObservation> ObserveProgressTreeForSeed(
            ProgressTree tree)
        {
            var result = new List<PreLedgerProgressSeed.NodeObservation>();
            ObserveProgressTreeForSeedCore(tree, "", result);
            return result;
        }

        private static void ObserveProgressTreeForSeedCore(
            ProgressTree tree, string pathPrefix, List<PreLedgerProgressSeed.NodeObservation> result)
        {
            if (tree == null)
                return;

            for (int i = 0; i < tree.Count; i++)
            {
                var node = tree[i];
                if (node == null) continue;

                string nodeId = node.Id ?? "";
                string qualifiedId = string.IsNullOrEmpty(pathPrefix)
                    ? nodeId
                    : pathPrefix + "/" + nodeId;

                var observation = new PreLedgerProgressSeed.NodeObservation
                {
                    QualifiedId = qualifiedId,
                    BareId = IsLegacyBareIdSubtree(pathPrefix) ? nodeId : null,
                    IsComplete = node.IsComplete
                };
                if (IsRepeatableRecordType(node))
                {
                    observation.IsRepeatableRecord = true;
                    observation.ImpliedPaidCountKnown =
                        TryReadRepeatableRecordImpliedPaidCount(node, out int paid);
                    observation.ImpliedPaidCount = paid;
                }
                result.Add(observation);

                if (node.Subtree != null && node.Subtree.Count > 0)
                    ObserveProgressTreeForSeedCore(node.Subtree, qualifiedId, result);
            }
        }

        /// <summary>
        /// Rebuilds live state for KSP's repeatable record nodes
        /// (RecordsAltitude/Depth/Speed/Distance) from the effective count that survived the
        /// ledger walk. Returns true when the node was recognized and patched.
        /// </summary>
        internal static bool PatchRepeatableRecordNode(
            ProgressNode node, int effectiveCount, string qualifiedId,
            bool authoritativeRepeatableRecordState = false)
        {
            FieldInfo reachedField = typeof(ProgressNode).GetField("reached",
                BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo completeField = typeof(ProgressNode).GetField("complete",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (reachedField == null || completeField == null)
            {
                ParsekLog.Warn(Tag,
                    "PatchMilestones: could not find reached/complete fields for repeatable record patching");
                return false;
            }

            return PatchRepeatableRecordNode(node, effectiveCount, qualifiedId,
                reachedField, completeField, authoritativeRepeatableRecordState, out _);
        }

        /// <summary>
        /// The add-only live band mirror (design 7.9, section 3.15 step 4): raises a repeatable
        /// record node's band to <paramref name="committedCount"/> thresholds when that is above
        /// the count its live state says stock already paid
        /// (<see cref="TryReadRepeatableRecordImpliedPaidCount"/>), so stock does not pay a
        /// re-climb through thresholds the committed timeline already credits at the clock.
        /// The band is rebuilt the way the normal patch does it (the live best value kept when
        /// it still fits the raised band), with no award, no <c>Complete()</c> and no event.
        /// Never lowers a band: at or below the live paid count nothing is touched. Returns
        /// true when the node is a record node whose live paid count could be read;
        /// <paramref name="raised"/> says whether the band moved.
        /// </summary>
        internal static bool TryRaiseRepeatableRecordBand(
            ProgressNode node, int committedCount, string qualifiedId,
            FieldInfo reachedField, FieldInfo completeField,
            out int livePaidCount, out bool raised)
        {
            raised = false;
            livePaidCount = 0;
            if (!IsRepeatableRecordType(node))
                return false;
            if (!TryReadRepeatableRecordImpliedPaidCount(node, out livePaidCount))
                return false;
            if (committedCount <= livePaidCount)
                return true;

            if (!PatchRepeatableRecordNode(node, committedCount, qualifiedId, reachedField, completeField,
                authoritativeRepeatableRecordState: false, out bool changed))
            {
                return false;
            }
            raised = changed;
            return true;
        }

        /// <summary>
        /// Returns true when the node is one of KSP's four repeatable world-record subclasses
        /// (<c>KSPAchievements.RecordsAltitude</c>, <c>KSPAchievements.RecordsDepth</c>,
        /// <c>KSPAchievements.RecordsSpeed</c>, <c>KSPAchievements.RecordsDistance</c>).
        /// Uses string-based full-name comparison so production matching stays scoped to the
        /// stock repeatable types without a compile-time reference to <c>KSPAchievements</c>.
        /// When <see cref="SuppressUnityCallsForTesting"/> is enabled, a simple-name fallback
        /// accepts xUnit stand-ins with matching type names.
        ///
        /// One-shot nodes (<c>Orbit</c>, <c>Landing</c>, <c>Flyby</c>, <c>Flight</c>, etc.) return
        /// false here — they are patched through the caller's one-shot achieve/un-achieve branch,
        /// not through the repeatable-record pipeline.
        /// </summary>
        internal static bool IsRepeatableRecordType(ProgressNode node)
        {
            if (node == null) return false;
            string fullName = node.GetType().FullName;
            if (fullName == "KSPAchievements.RecordsAltitude"
                || fullName == "KSPAchievements.RecordsDepth"
                || fullName == "KSPAchievements.RecordsSpeed"
                || fullName == "KSPAchievements.RecordsDistance")
            {
                return true;
            }

            if (!SuppressUnityCallsForTesting)
                return false;

            string name = node.GetType().Name;
            return name == "RecordsAltitude"
                || name == "RecordsDepth"
                || name == "RecordsSpeed"
                || name == "RecordsDistance";
        }

        /// <summary>
        /// Rebuilds live state for KSP's repeatable record nodes
        /// (RecordsAltitude/Depth/Speed/Distance) from the effective count that survived the
        /// ledger walk. Returns true when the node was recognized; <paramref name="changed"/>
        /// indicates whether any live state actually changed.
        ///
        /// Nodes whose type is not one of the four stock repeatable-record subclasses return
        /// false with no log, leaving them to the caller's one-shot patch path. This keeps
        /// the 392+ per-body sub-achievements (<c>Bop/Orbit</c>, <c>Dres/Flight</c>, …) out of
        /// the record-field reflection branch and its missing-field WARN. See bug #455.
        /// </summary>
        internal static bool PatchRepeatableRecordNode(
            ProgressNode node, int effectiveCount, string qualifiedId,
            FieldInfo reachedField, FieldInfo completeField,
            bool authoritativeRepeatableRecordState, out bool changed)
        {
            changed = false;

            // Pre-filter by type so one-shot nodes (Orbit/Landing/Flyby/Flight/…) fall through
            // to the caller's one-shot branch. Returning false here (vs. true) is load-bearing:
            // `true` would short-circuit PatchProgressNodeTree and leave those nodes unpatched.
            if (!IsRepeatableRecordType(node))
                return false;

            Type nodeType = node.GetType();
            FieldInfo recordField = nodeType.GetField("record",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo rewardThresholdField = nodeType.GetField("rewardThreshold",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo rewardIntervalField = nodeType.GetField("rewardInterval",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo recordHolderField = nodeType.GetField("recordHolder",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (recordField == null || rewardThresholdField == null || rewardIntervalField == null)
            {
                // A node whose type IS one of the four record subclasses but is missing the
                // stock record fields — that's a genuine KSP-API structural change and should
                // page someone. Keep the WARN.
                ParsekLog.Warn(Tag,
                    $"PatchMilestones: repeatable node '{qualifiedId}' is missing stock record fields " +
                    $"(record={recordField != null}, rewardThreshold={rewardThresholdField != null}, " +
                    $"rewardInterval={rewardIntervalField != null}) — skipping");
                return true;
            }

            RepeatableRecordState targetState;
            if (authoritativeRepeatableRecordState)
            {
                if (!TryComputeRepeatableRecordState(node, effectiveCount, out targetState))
                    return false;
            }
            else
            {
                double liveRecord = NormalizeRepeatableRecordValue(recordField.GetValue(node), double.MaxValue);
                if (!TryComputeRepeatableRecordState(
                    node, effectiveCount, liveRecord, out targetState))
                {
                    return false;
                }
            }

            bool currentReached = node.IsReached;
            if (currentReached != targetState.Reached)
            {
                reachedField.SetValue(node, targetState.Reached);
                changed = true;
            }

            bool currentComplete = node.IsComplete;
            if (currentComplete != targetState.Complete)
            {
                completeField.SetValue(node, targetState.Complete);
                changed = true;
            }

            double currentRecordValue = (double)recordField.GetValue(node);
            if (Math.Abs(currentRecordValue - targetState.Record) > RecordStateEpsilon)
            {
                recordField.SetValue(node, targetState.Record);
                changed = true;
            }

            double currentRewardThreshold = (double)rewardThresholdField.GetValue(node);
            if (Math.Abs(currentRewardThreshold - targetState.RewardThreshold) > RecordStateEpsilon)
            {
                rewardThresholdField.SetValue(node, targetState.RewardThreshold);
                changed = true;
            }

            int currentRewardInterval = (int)rewardIntervalField.GetValue(node);
            if (currentRewardInterval != targetState.RewardInterval)
            {
                rewardIntervalField.SetValue(node, targetState.RewardInterval);
                changed = true;
            }

            if (recordHolderField != null && recordHolderField.GetValue(node) != null)
            {
                recordHolderField.SetValue(node, null);
                changed = true;
            }

            Action<Vessel> targetIterator = targetState.Complete
                ? null
                : CreateRepeatableRecordIterator(node);
            if (!targetState.Complete && targetIterator == null)
            {
                ParsekLog.Warn(Tag,
                    $"PatchMilestones: repeatable node '{qualifiedId}' has no iterateVessels method — future record tracking may stall");
                targetIterator = node.OnIterateVessels;
            }
            if (!object.Equals(node.OnIterateVessels, targetIterator))
            {
                node.OnIterateVessels = targetIterator;
                changed = true;
            }

            if (changed)
            {
                ParsekLog.Verbose(Tag,
                    $"PatchMilestones: synced repeatable record '{qualifiedId}' " +
                    $"hits={effectiveCount.ToString(IC)} reached={targetState.Reached.ToString(IC)} " +
                    $"complete={targetState.Complete.ToString(IC)} " +
                    $"record={targetState.Record.ToString("F1", IC)} " +
                    $"nextThreshold={targetState.RewardThreshold.ToString("F1", IC)} " +
                    $"nextInterval={targetState.RewardInterval.ToString(IC)}");
            }

            return true;
        }

        private static double NormalizeRepeatableRecordValue(object value, double maxRecord)
        {
            if (value == null)
                return 0.0;

            double numericValue;
            if (value is double doubleValue)
                numericValue = doubleValue;
            else if (value is float floatValue)
                numericValue = floatValue;
            else if (value is int intValue)
                numericValue = intValue;
            else if (value is long longValue)
                numericValue = longValue;
            else
                return 0.0;

            if (double.IsNaN(numericValue) || double.IsInfinity(numericValue))
                return 0.0;
            if (numericValue <= 0.0)
                return 0.0;
            return numericValue > maxRecord ? maxRecord : numericValue;
        }

        private static bool TryGetRepeatableRecordDefinition(
            ProgressNode node, out double maxRecord, out double roundValue)
        {
            maxRecord = 0.0;
            roundValue = 0.0;
            if (node == null) return false;

            string maxPropertyName;
            double fallbackMaxRecord;
            switch (node.GetType().FullName)
            {
                case "KSPAchievements.RecordsAltitude":
                    maxPropertyName = "maxAltitude";
                    fallbackMaxRecord = 70000.0;
                    roundValue = 500.0;
                    break;
                case "KSPAchievements.RecordsDepth":
                    maxPropertyName = "maxDepth";
                    fallbackMaxRecord = 750.0;
                    roundValue = 10.0;
                    break;
                case "KSPAchievements.RecordsSpeed":
                    maxPropertyName = "maxSpeed";
                    fallbackMaxRecord = 2500.0;
                    roundValue = 5.0;
                    break;
                case "KSPAchievements.RecordsDistance":
                    maxPropertyName = "maxDistance";
                    fallbackMaxRecord = 100000.0;
                    roundValue = 1000.0;
                    break;
                default:
                    return false;
            }

            if (SuppressUnityCallsForTesting)
            {
                maxRecord = fallbackMaxRecord;
                return true;
            }

            PropertyInfo maxProperty = node.GetType().GetProperty(maxPropertyName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (maxProperty == null)
            {
                ParsekLog.Warn(Tag,
                    $"PatchMilestones: repeatable node '{node.Id ?? "<null>"}' missing '{maxPropertyName}' property");
                return false;
            }

            object maxValue;
            try
            {
                maxValue = maxProperty.GetValue(node, null);
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"PatchMilestones: repeatable node '{node.Id ?? "<null>"}' max lookup via '{maxPropertyName}' threw '{ex.Message}' — using fallback {fallbackMaxRecord.ToString("F1", IC)}");
                maxRecord = fallbackMaxRecord;
                return true;
            }

            if (!(maxValue is double))
            {
                ParsekLog.Warn(Tag,
                    $"PatchMilestones: repeatable node '{node.Id ?? "<null>"}' returned non-double max record");
                return false;
            }

            maxRecord = (double)maxValue;
            return true;
        }

        private static Action<Vessel> CreateRepeatableRecordIterator(ProgressNode node)
        {
            if (node == null) return null;

            MethodInfo iterateMethod = node.GetType().GetMethod("iterateVessels",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (iterateMethod == null)
                return null;

            return Delegate.CreateDelegate(typeof(Action<Vessel>), node, iterateMethod,
                throwOnBindFailure: false) as Action<Vessel>;
        }
    }
}
