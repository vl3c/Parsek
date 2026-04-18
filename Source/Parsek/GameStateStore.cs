using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Parsek
{
    internal static class GameStateStore
    {
        private static List<GameStateEvent> events = new List<GameStateEvent>();
        private static List<ContractSnapshot> contractSnapshots = new List<ContractSnapshot>();
        private static List<GameStateBaseline> baselines = new List<GameStateBaseline>();
        private static Dictionary<string, float> committedScienceSubjects = new Dictionary<string, float>();
        private static Dictionary<string, float> originalScienceValues = new Dictionary<string, float>();

        private static bool initialLoadDone = false;
        private static string lastSaveFolder = null;

        internal static bool SuppressLogging = false;

        private const double ResourceCoalesceEpsilon = 0.1; // seconds

        internal static IReadOnlyList<GameStateEvent> Events => events;
        internal static IReadOnlyList<ContractSnapshot> ContractSnapshots => contractSnapshots;
        internal static IReadOnlyList<GameStateBaseline> Baselines => baselines;
        internal static int EventCount => events.Count;
        internal static int BaselineCount => baselines.Count;

        #region Event Management

        internal static void AddEvent(GameStateEvent e)
        {
            // Stamp current epoch for branch isolation
            e.epoch = MilestoneStore.CurrentEpoch;

            // Resource coalescing: if this is a resource event and the last event
            // of the same type + same recordingId tag is within the epsilon window,
            // update it instead. #431: the tag equality gate is required — without
            // it, an untagged career slot + tagged in-flight event within epsilon
            // would merge into an untagged slot and silently drop the tag, leaking
            // through the discard purge.
            if (IsResourceEvent(e.eventType) && events.Count > 0)
            {
                string incomingTag = e.recordingId ?? "";
                for (int i = events.Count - 1; i >= 0; i--)
                {
                    var existing = events[i];
                    string existingTag = existing.recordingId ?? "";
                    if (existing.eventType == e.eventType &&
                        Math.Abs(existing.ut - e.ut) <= ResourceCoalesceEpsilon &&
                        string.Equals(existingTag, incomingTag, StringComparison.Ordinal))
                    {
                        // Update the existing event's valueAfter
                        existing.valueAfter = e.valueAfter;
                        events[i] = existing;
                        ParsekLog.VerboseRateLimited("GameStateStore", "resource-coalesce",
                            $"Coalesced {e.eventType} event at ut={e.ut:F2} tag='{incomingTag}'");
                        return;
                    }
                    // Stop searching once we pass the epsilon window
                    if (e.ut - existing.ut > ResourceCoalesceEpsilon)
                        break;
                }
            }

            events.Add(e);
            ParsekLog.Verbose("GameStateStore",
                $"AddEvent: {e.eventType} key='{e.key}' epoch={e.epoch} ut={e.ut:F1} (total={events.Count})");
        }

        internal static bool IsResourceEvent(GameStateEventType type)
        {
            return type == GameStateEventType.FundsChanged ||
                   type == GameStateEventType.ScienceChanged ||
                   type == GameStateEventType.ReputationChanged;
        }

        /// <summary>
        /// Events that should be excluded from milestones and the Actions window.
        /// Resource events are summarized by the budget; CrewStatusChanged is KSP
        /// internal bookkeeping (Available↔Assigned) and not a player action;
        /// ContractOffered is a KSP ContractSystem tick artifact — the UT a contract
        /// was advertised to the player carries no planning value and pre-#398 saves
        /// still carry these events baked into historical milestones.
        /// </summary>
        internal static bool IsMilestoneFilteredEvent(GameStateEventType type)
        {
            return IsResourceEvent(type) ||
                   type == GameStateEventType.CrewStatusChanged ||
                   type == GameStateEventType.ContractOffered;
        }

        /// <summary>
        /// Counts events in the store that haven't been swept into any milestone yet.
        /// Used for the Actions button badge alongside GetPendingEventCount.
        /// </summary>
        internal static int GetUncommittedEventCount()
        {
            uint epoch = MilestoneStore.CurrentEpoch;
            double lastMilestoneEndUT = 0;
            var milestones = MilestoneStore.Milestones;
            for (int i = 0; i < milestones.Count; i++)
            {
                if (milestones[i].Epoch == epoch && milestones[i].EndUT > lastMilestoneEndUT)
                    lastMilestoneEndUT = milestones[i].EndUT;
            }

            int count = 0;
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (e.epoch != epoch) continue;
                if (e.ut <= lastMilestoneEndUT) continue;
                if (IsMilestoneFilteredEvent(e.eventType)) continue;
                count++;
            }
            return count;
        }

        internal static void AddContractSnapshot(string guid, ConfigNode contractNode)
        {
            if (string.IsNullOrEmpty(guid) || contractNode == null)
            {
                ParsekLog.Verbose("GameStateStore", $"AddContractSnapshot skipped: guid={guid ?? "null"}, node={contractNode != null}");
                return;
            }

            // Replace existing snapshot for same GUID (contract re-accepted after failure)
            for (int i = 0; i < contractSnapshots.Count; i++)
            {
                if (contractSnapshots[i].contractGuid == guid)
                {
                    contractSnapshots[i] = new ContractSnapshot
                    {
                        contractGuid = guid,
                        contractNode = contractNode
                    };
                    ParsekLog.Verbose("GameStateStore", $"Replaced existing contract snapshot for guid={guid}");
                    return;
                }
            }

            contractSnapshots.Add(new ContractSnapshot
            {
                contractGuid = guid,
                contractNode = contractNode
            });
            ParsekLog.Verbose("GameStateStore", $"Added contract snapshot for guid={guid} (total={contractSnapshots.Count})");
        }

        internal static ConfigNode GetContractSnapshot(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;

            for (int i = 0; i < contractSnapshots.Count; i++)
            {
                if (contractSnapshots[i].contractGuid == guid)
                    return contractSnapshots[i].contractNode;
            }
            return null;
        }

        /// <summary>
        /// Updates the <c>detail</c> field of the first event matching the given
        /// ut/eventType/key/epoch. Returns true if an entry was found and updated.
        /// GameStateEvent is a value type, so callers can't just mutate their copy —
        /// this helper rewrites the underlying list slot. Used by the milestone reward
        /// enrichment path (#400), which emits the event first with zero-reward detail
        /// and then, when the Harmony postfix on ProgressNode.AwardProgress has the
        /// real values, patches the stored event in place.
        /// </summary>
        internal static bool UpdateEventDetail(
            double ut, GameStateEventType eventType, string key, uint epoch, string newDetail)
        {
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (e.ut == ut && e.eventType == eventType &&
                    e.key == key && e.epoch == epoch)
                {
                    e.detail = newDetail;
                    events[i] = e;
                    ParsekLog.Verbose("GameStateStore",
                        $"Updated event detail: {eventType} key='{key}' ut={ut:F1}");
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Removes an event by matching ut, eventType, and key.
        /// Returns true if the event was found and removed.
        /// </summary>
        internal static bool RemoveEvent(GameStateEvent target)
        {
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (e.ut == target.ut && e.eventType == target.eventType &&
                    e.key == target.key && e.epoch == target.epoch)
                {
                    events.RemoveAt(i);
                    ParsekLog.Info("GameStateStore",
                        $"Removed event: {target.eventType} key='{target.key}' ut={target.ut:F1}");
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Removes events that have been consumed by milestone creation and ledger conversion.
        /// Call after both CreateMilestone and OnRecordingCommitted have completed.
        /// Events are pruned if they belong to an old epoch OR if their UT is at or below
        /// the latest committed milestone EndUT in the current epoch.
        /// </summary>
        internal static int PruneProcessedEvents()
        {
            uint currentEpoch = MilestoneStore.CurrentEpoch;
            double threshold = MilestoneStore.GetLatestCommittedEndUT();

            int pruned = 0;
            for (int i = events.Count - 1; i >= 0; i--)
            {
                var e = events[i];
                if (e.epoch != currentEpoch || e.ut <= threshold)
                {
                    events.RemoveAt(i);
                    pruned++;
                }
            }

            if (pruned > 0)
            {
                ParsekLog.Info("GameStateStore",
                    $"PruneProcessedEvents: removed {pruned} events " +
                    $"(epoch={currentEpoch}, threshold={threshold:F1}, remaining={events.Count})");
            }

            return pruned;
        }

        internal static void ClearEvents()
        {
            int eventCount = events.Count;
            int snapCount = contractSnapshots.Count;
            events.Clear();
            contractSnapshots.Clear();
            ParsekLog.Info("GameStateStore", $"Cleared {eventCount} events and {snapCount} contract snapshots");
        }

        /// <summary>
        /// #431: removes every tagged event whose <see cref="GameStateEvent.recordingId"/>
        /// is in the given id set. Walks both the live events list AND every milestone's
        /// events list (via <see cref="MilestoneStore.PurgeTaggedEvents"/>) — the flush-on-save
        /// path can move tagged events into a milestone before the player decides commit/discard,
        /// so the purge has to cover both stores. Contract snapshots whose accept event was
        /// purged are removed too. Untagged events are never touched.
        /// </summary>
        internal static int PurgeEventsForRecordings(ICollection<string> recordingIds, string reason)
        {
            if (recordingIds == null || recordingIds.Count == 0)
            {
                ParsekLog.Verbose("GameStateStore",
                    $"PurgeEventsForRecordings: no ids supplied ({reason}) — skipping");
                return 0;
            }

            var set = recordingIds as HashSet<string> ?? new HashSet<string>(recordingIds);

            // 1. Live events list
            var liveRemoved = new List<GameStateEvent>();
            for (int i = events.Count - 1; i >= 0; i--)
            {
                var e = events[i];
                if (!string.IsNullOrEmpty(e.recordingId) && set.Contains(e.recordingId))
                {
                    liveRemoved.Add(e);
                    events.RemoveAt(i);
                }
            }

            // 2. Milestone event lists — covers the F5-then-discard path.
            var milestoneRemoved = MilestoneStore.PurgeTaggedEvents(set, reason);

            // 3. Contract snapshots — only those whose ContractAccepted event was purged.
            var allPurged = new List<GameStateEvent>(liveRemoved.Count + milestoneRemoved.Count);
            allPurged.AddRange(liveRemoved);
            allPurged.AddRange(milestoneRemoved);
            int snapshotsRemoved = PurgeOrphanedContractSnapshots(allPurged);

            ParsekLog.Info("GameStateStore",
                $"PurgeEventsForRecordings ({reason}): live={liveRemoved.Count}, " +
                $"milestone={milestoneRemoved.Count}, snapshots={snapshotsRemoved}, ids={set.Count}");

            return liveRemoved.Count + milestoneRemoved.Count;
        }

        /// <summary>
        /// #431: deletes contract snapshots whose corresponding <see cref="GameStateEventType.ContractAccepted"/>
        /// event appears in the purged list. Snapshots are only created by <see cref="AddContractSnapshot"/>
        /// from <c>GameStateRecorder.OnContractAccepted</c> — so they always follow the accept event's fate.
        /// A completion/failure/cancel event being purged does NOT drop the snapshot on its own;
        /// the accept event must be among the purged set for the snapshot to go.
        /// </summary>
        internal static int PurgeOrphanedContractSnapshots(List<GameStateEvent> purgedEvents)
        {
            if (purgedEvents == null || purgedEvents.Count == 0)
                return 0;

            var guidsToRemove = new HashSet<string>();
            for (int i = 0; i < purgedEvents.Count; i++)
            {
                var e = purgedEvents[i];
                if (e.eventType == GameStateEventType.ContractAccepted && !string.IsNullOrEmpty(e.key))
                    guidsToRemove.Add(e.key);
            }

            if (guidsToRemove.Count == 0)
                return 0;

            int removed = 0;
            for (int i = contractSnapshots.Count - 1; i >= 0; i--)
            {
                if (guidsToRemove.Contains(contractSnapshots[i].contractGuid))
                {
                    contractSnapshots.RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        /// <summary>
        /// Rewrites any legacy persisted <see cref="GameStateEventType.PartPurchased"/>
        /// rows whose stored <c>cost=</c> token still means rollout <c>part.cost</c>
        /// instead of the real historical R&amp;D debit captured by saved
        /// <see cref="GameStateEventType.FundsChanged"/> history. The only no-funds
        /// fallback is the legacy stock-bypass shape: one stale part-purchase row,
        /// zero paired <c>RnDPartPurchase</c> funds events, and a loaded save whose
        /// difficulty still reports <c>BypassEntryPurchaseAfterResearch=true</c>.
        /// Ambiguous or coalesced windows are left untouched rather than guessed from
        /// current runtime state.
        /// Runs against the live in-memory list so recovery and load-time compatibility
        /// paths see corrected event data without a save-format bump.
        /// </summary>
        internal static int RepairLegacyPartPurchaseEventsForCurrentSemantics()
        {
            return RepairLegacyPartPurchaseEventsForCurrentSemantics(events);
        }

        internal static int RepairLegacyPartPurchaseEventsForCurrentSemantics(IList<GameStateEvent> sourceEvents)
        {
            if (sourceEvents == null || sourceEvents.Count == 0)
                return 0;

            IReadOnlyList<GameStateEvent> readOnlySource =
                sourceEvents as IReadOnlyList<GameStateEvent> ??
                new List<GameStateEvent>(sourceEvents);

            int repaired = 0;
            for (int i = 0; i < sourceEvents.Count; i++)
            {
                GameStateEvent rewritten;
                if (!TryRewriteLegacyPartPurchaseEvent(sourceEvents[i], readOnlySource, out rewritten))
                    continue;

                sourceEvents[i] = rewritten;
                repaired++;
            }

            return repaired;
        }

        internal static bool TryRewriteLegacyPartPurchaseEvent(
            GameStateEvent evt,
            IReadOnlyList<GameStateEvent> sourceEvents,
            out GameStateEvent rewritten)
        {
            rewritten = evt;
            if (evt.eventType != GameStateEventType.PartPurchased)
                return false;

            float storedCost;
            if (!TryGetStoredPartPurchaseCost(evt.detail, out storedCost))
                return false;

            float canonicalCost;
            if (!TryResolveLegacyPartPurchaseCharge(evt, sourceEvents, out canonicalCost))
                return false;

            double storedDelta = evt.valueBefore - evt.valueAfter;
            if (Math.Abs(storedCost - canonicalCost) <= 0.01f &&
                Math.Abs(storedDelta - canonicalCost) <= 0.01)
            {
                return false;
            }

            rewritten.detail = "cost=" + canonicalCost.ToString("R", CultureInfo.InvariantCulture);
            rewritten.valueBefore = evt.valueAfter + canonicalCost;
            return true;
        }

        internal static bool TryGetStoredPartPurchaseCost(string detail, out float partPurchaseCost)
        {
            partPurchaseCost = 0f;

            string costStr = GameStateEventDisplay.ExtractDetailField(detail, "cost");
            return !string.IsNullOrEmpty(costStr) &&
                   float.TryParse(costStr, NumberStyles.Float, CultureInfo.InvariantCulture,
                       out partPurchaseCost);
        }

        private static bool TryResolveLegacyPartPurchaseCharge(
            GameStateEvent evt,
            IReadOnlyList<GameStateEvent> sourceEvents,
            out float canonicalCost)
        {
            canonicalCost = 0f;

            // Only trust the paired FundsChanged delta when the window contains a single
            // part purchase. Batched unlocks can coalesce multiple RnDPartPurchase deltas
            // into one resource event, which is ambiguous per purchase. Do not fall back
            // to current difficulty or part metadata — that can silently rewrite old
            // history using today's runtime semantics instead of the saved savefile facts.
            if (TryGetUnambiguousPartPurchaseChargeFromFundsEvent(
                evt, sourceEvents, out canonicalCost))
            {
                return true;
            }

            // Older stock-bypass saves never emitted RnDPartPurchase funds events at all,
            // but pre-fix Parsek still persisted a non-zero PartPurchased row. Restore
            // only that exact "single purchase, zero funds events" shape.
            if (!IsKnownLegacyBypassPartPurchaseShape(evt, sourceEvents))
                return false;

            bool bypassEntryPurchaseAfterResearch;
            if (!GameStateRecorder.TryGetBypassEntryPurchaseAfterResearch(
                out bypassEntryPurchaseAfterResearch) || !bypassEntryPurchaseAfterResearch)
            {
                return false;
            }

            canonicalCost = 0f;
            return true;
        }

        private static bool TryGetUnambiguousPartPurchaseChargeFromFundsEvent(
            GameStateEvent target,
            IReadOnlyList<GameStateEvent> sourceEvents,
            out float canonicalCost)
        {
            canonicalCost = 0f;
            int partPurchasesInWindow = 0;
            int fundsEventsInWindow = 0;
            GameStateEvent fundsMatch = default(GameStateEvent);
            ScanPartPurchaseWindow(
                target,
                sourceEvents,
                out partPurchasesInWindow,
                out fundsEventsInWindow,
                out fundsMatch);

            if (partPurchasesInWindow != 1 || fundsEventsInWindow != 1)
                return false;

            double delta = fundsMatch.valueAfter - fundsMatch.valueBefore;
            if (delta >= -0.01)
                return false;

            canonicalCost = (float)(-delta);
            return true;
        }

        private static bool IsKnownLegacyBypassPartPurchaseShape(
            GameStateEvent target,
            IReadOnlyList<GameStateEvent> sourceEvents)
        {
            int partPurchasesInWindow = 0;
            int fundsEventsInWindow = 0;
            GameStateEvent ignoredFundsEvent;
            ScanPartPurchaseWindow(
                target,
                sourceEvents,
                out partPurchasesInWindow,
                out fundsEventsInWindow,
                out ignoredFundsEvent);
            return partPurchasesInWindow == 1 && fundsEventsInWindow == 0;
        }

        private static void ScanPartPurchaseWindow(
            GameStateEvent target,
            IReadOnlyList<GameStateEvent> sourceEvents,
            out int partPurchasesInWindow,
            out int fundsEventsInWindow,
            out GameStateEvent fundsMatch)
        {
            partPurchasesInWindow = 0;
            fundsEventsInWindow = 0;
            fundsMatch = default(GameStateEvent);
            if (sourceEvents == null || sourceEvents.Count == 0)
                return;

            for (int i = 0; i < sourceEvents.Count; i++)
            {
                var candidate = sourceEvents[i];
                if (candidate.epoch != target.epoch)
                    continue;
                if (Math.Abs(candidate.ut - target.ut) > ResourceCoalesceEpsilon)
                    continue;

                if (candidate.eventType == GameStateEventType.PartPurchased)
                {
                    partPurchasesInWindow++;
                    continue;
                }

                if (candidate.eventType == GameStateEventType.FundsChanged &&
                    string.Equals(candidate.key, "RnDPartPurchase", StringComparison.Ordinal))
                {
                    fundsEventsInWindow++;
                    fundsMatch = candidate;
                }
            }
        }

        #endregion

        #region Committed Science Subjects

        /// <summary>
        /// Merges pending science subjects into the committed store.
        /// For each subject, keeps the maximum science value (handles partial experiments).
        /// </summary>
        internal static void CommitScienceSubjects(List<PendingScienceSubject> pending)
        {
            if (pending == null || pending.Count == 0) return;

            int added = 0, updated = 0;
            for (int i = 0; i < pending.Count; i++)
            {
                string id = pending[i].subjectId;
                float science = pending[i].science;

                float existing;
                if (committedScienceSubjects.TryGetValue(id, out existing))
                {
                    if (science > existing)
                    {
                        committedScienceSubjects[id] = science;
                        updated++;
                    }
                }
                else
                {
                    committedScienceSubjects[id] = science;
                    added++;
                }
            }

            ParsekLog.Info("GameStateStore",
                $"CommitScienceSubjects: {added} added, {updated} updated (total={committedScienceSubjects.Count})");
        }

        /// <summary>
        /// Tries to get the committed science value for a subject.
        /// Returns false if the subject has not been committed.
        /// </summary>
        /// <summary>
        /// Returns a snapshot of committed subject IDs for iteration (e.g., by the
        /// save recovery migration in <see cref="LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad"/>).
        /// Returns a new list so callers can mutate the underlying store while iterating.
        /// </summary>
        internal static List<string> GetCommittedScienceSubjectIds()
        {
            return new List<string>(committedScienceSubjects.Keys);
        }

        internal static bool TryGetCommittedSubjectScience(string subjectId, out float science)
        {
            return committedScienceSubjects.TryGetValue(subjectId, out science);
        }

        /// <summary>
        /// Rebuilds the committed science subjects dictionary from the given
        /// (subjectId, scienceAmount) pairs. Used after recording deletion or
        /// ledger reconciliation to prune stale entries from deleted recordings.
        /// </summary>
        internal static void RebuildCommittedScienceSubjects(
            IEnumerable<KeyValuePair<string, float>> subjectCredits)
        {
            int before = committedScienceSubjects.Count;
            committedScienceSubjects.Clear();
            int count = 0;
            foreach (var kv in subjectCredits)
            {
                committedScienceSubjects[kv.Key] = kv.Value;
                count++;
            }
            ParsekLog.Info("GameStateStore",
                $"RebuildCommittedScienceSubjects: before={before}, rebuilt with {count} subjects");
        }

        internal static int CommittedScienceSubjectCount => committedScienceSubjects.Count;
        internal static int OriginalScienceValueCount => originalScienceValues.Count;

        internal static bool TryGetOriginalScience(string subjectId, out float science)
        {
            return originalScienceValues.TryGetValue(subjectId, out science);
        }

        /// <summary>
        /// Records the original (pre-mutation) science value for a subject.
        /// Only stores the first value — subsequent calls for the same subject are ignored,
        /// preserving the true pre-Parsek baseline.
        /// Called by ScienceSubjectPatch before mutating ScienceSubject.science.
        /// </summary>
        internal static void RecordOriginalScience(string subjectId, float originalScience)
        {
            if (originalScienceValues.ContainsKey(subjectId)) return;
            originalScienceValues[subjectId] = originalScience;
            ParsekLog.Verbose("GameStateStore",
                $"Recorded original science for {subjectId}: {originalScience:F1}");
        }

        internal static void ClearScienceSubjects()
        {
            int count = committedScienceSubjects.Count;

            // Clear committed first so the Harmony patch becomes a no-op
            committedScienceSubjects.Clear();

            // Restore KSP R&D state to pre-mutation values
            RestoreScienceInRnD();

            originalScienceValues.Clear();
            ParsekLog.Info("GameStateStore",
                $"Cleared {count} committed science subjects and restored R&D state");
        }

        /// <summary>
        /// Restores ScienceSubject.science values in KSP's R&D to their
        /// pre-mutation originals. Must be called AFTER clearing committedScienceSubjects
        /// so the Harmony postfix is a no-op during subject lookup.
        /// </summary>
        private static void RestoreScienceInRnD()
        {
            if (originalScienceValues.Count == 0) return;

            // ResearchAndDevelopment is only available in career/science mode during gameplay
            if (ResearchAndDevelopment.Instance == null)
            {
                ParsekLog.Verbose("GameStateStore",
                    "R&D instance unavailable — skipping science restore (values will persist in save)");
                return;
            }

            int restored = 0;
            foreach (var kvp in originalScienceValues)
            {
                ScienceSubject subject = ResearchAndDevelopment.GetSubjectByID(kvp.Key);
                if (subject != null && subject.science != kvp.Value)
                {
                    ParsekLog.Verbose("GameStateStore",
                        $"Restoring R&D science: {kvp.Key} {subject.science:F1} → {kvp.Value:F1}");
                    subject.science = kvp.Value;
                    restored++;
                }
            }

            if (restored > 0)
                ParsekLog.Info("GameStateStore", $"Restored {restored} science subjects in R&D");
        }

        /// <summary>
        /// Serializes committed science subjects into a SCIENCE_SUBJECTS ConfigNode on the parent.
        /// </summary>
        internal static void SerializeScienceSubjectsInto(ConfigNode parent)
        {
            if (committedScienceSubjects.Count == 0 && originalScienceValues.Count == 0) return;

            ConfigNode sciNode = parent.AddNode("SCIENCE_SUBJECTS");
            foreach (var kvp in committedScienceSubjects)
            {
                ConfigNode entry = sciNode.AddNode("SUBJECT");
                entry.AddValue("id", kvp.Key);
                entry.AddValue("science", kvp.Value.ToString("R", CultureInfo.InvariantCulture));
            }

            if (originalScienceValues.Count > 0)
            {
                ConfigNode origNode = sciNode.AddNode("ORIGINALS");
                foreach (var kvp in originalScienceValues)
                {
                    ConfigNode entry = origNode.AddNode("SUBJECT");
                    entry.AddValue("id", kvp.Key);
                    entry.AddValue("science", kvp.Value.ToString("R", CultureInfo.InvariantCulture));
                }
            }
        }

        /// <summary>
        /// Deserializes committed science subjects from a SCIENCE_SUBJECTS ConfigNode on the parent.
        /// </summary>
        internal static void DeserializeScienceSubjectsFrom(ConfigNode parent)
        {
            ConfigNode sciSubjectsNode = parent.GetNode("SCIENCE_SUBJECTS");
            if (sciSubjectsNode == null) return;

            ConfigNode[] subjectNodes = sciSubjectsNode.GetNodes("SUBJECT");
            if (subjectNodes != null)
            {
                foreach (var sn in subjectNodes)
                {
                    string id = sn.GetValue("id");
                    string sciStr = sn.GetValue("science");
                    float sci;
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(sciStr) &&
                        float.TryParse(sciStr, System.Globalization.NumberStyles.Float,
                            CultureInfo.InvariantCulture, out sci))
                    {
                        committedScienceSubjects[id] = sci;
                    }
                }
            }

            ConfigNode origNode = sciSubjectsNode.GetNode("ORIGINALS");
            if (origNode != null)
            {
                ConfigNode[] origSubjects = origNode.GetNodes("SUBJECT");
                if (origSubjects != null)
                {
                    foreach (var sn in origSubjects)
                    {
                        string id = sn.GetValue("id");
                        string sciStr = sn.GetValue("science");
                        float sci;
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(sciStr) &&
                            float.TryParse(sciStr, System.Globalization.NumberStyles.Float,
                                CultureInfo.InvariantCulture, out sci))
                        {
                            originalScienceValues[id] = sci;
                        }
                    }
                }
            }
        }

        #endregion

        #region Baseline Management

        internal static void AddBaseline(GameStateBaseline baseline)
        {
            if (baseline == null) return;
            baselines.Add(baseline);
            ParsekLog.Info("GameStateStore", $"Game state baseline captured at UT {baseline.ut:F0} (total={baselines.Count})");
        }

        /// <summary>
        /// Captures a baseline if none exist or if a new one is warranted.
        /// Called from RecordingStore.CommitRecordingDirect() and FinalizeTreeCommit() as the single funnel point.
        /// Silently skipped in test environments (SuppressLogging = true).
        /// </summary>
        internal static void CaptureBaselineIfNeeded()
        {
            // Skip in test environments where Unity/KSP APIs aren't available
            if (SuppressLogging) return;

            try
            {
                ParsekLog.Verbose("GameStateStore", "CaptureBaselineIfNeeded: capturing baseline...");
                var baseline = GameStateBaseline.CaptureCurrentState();
                AddBaseline(baseline);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("GameStateStore", $"Failed to capture baseline: {ex.Message}");
            }
        }

        internal static void ClearBaselines()
        {
            int count = baselines.Count;
            baselines.Clear();
            ParsekLog.Verbose("GameStateStore", $"Cleared {count} baselines");
        }

        #endregion

        #region File I/O

        internal static bool SaveEventFile()
        {
            string path = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildGameStateEventsRelativePath());
            if (path == null)
            {
                ParsekLog.Warn("GameStateStore", "Cannot resolve game state events path — save skipped");
                return false;
            }

            try
            {
                string dir = RecordingPaths.EnsureGameStateDirectory();
                if (string.IsNullOrEmpty(dir))
                {
                    ParsekLog.Warn("GameStateStore", "EnsureGameStateDirectory returned null during SaveEventFile");
                    return false;
                }

                // Events are stored in insertion (capture) order, not UT order.
                // After reverts, events from an abandoned future branch precede
                // events from the new branch — UT-sorting would interleave them
                // and corrupt facility/building cache seeding.

                var rootNode = new ConfigNode("PARSEK_GAME_STATE");
                rootNode.AddValue("version", 1);

                foreach (var e in events)
                {
                    ConfigNode eventNode = rootNode.AddNode("GAME_STATE_EVENT");
                    e.SerializeInto(eventNode);
                }

                foreach (var snap in contractSnapshots)
                    snap.SerializeInto(rootNode);

                SerializeScienceSubjectsInto(rootNode);

                SafeWriteConfigNode(rootNode, path);

                ParsekLog.Info("GameStateStore",
                    $"Saved {events.Count} game state events, {contractSnapshots.Count} contract snapshots, " +
                    $"{committedScienceSubjects.Count} science subjects to {path}");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("GameStateStore", $"Failed to save game state events: {ex.Message}");
                return false;
            }
        }

        internal static bool LoadEventFile()
        {
            string currentSave = HighLogic.SaveFolder;
            if (currentSave != lastSaveFolder)
            {
                initialLoadDone = false;
                lastSaveFolder = currentSave;
                ParsekLog.Verbose("GameStateStore", $"Save folder changed to '{currentSave}' — resetting event load state");
            }

            if (initialLoadDone)
            {
                ParsekLog.Verbose("GameStateStore", "LoadEventFile: already loaded, skipping");
                return true;
            }

            initialLoadDone = true;
            int prevEvents = events.Count;
            int prevSnapshots = contractSnapshots.Count;
            events.Clear();
            contractSnapshots.Clear();
            committedScienceSubjects.Clear();
            originalScienceValues.Clear();
            ParsekLog.Verbose("GameStateStore",
                $"LoadEventFile: cleared prior state ({prevEvents} events, {prevSnapshots} snapshots) before loading");

            string path = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildGameStateEventsRelativePath());
            if (path == null || !File.Exists(path))
            {
                ParsekLog.Info("GameStateStore", "No game state events file found — starting fresh");
                return true;
            }

            try
            {
                ConfigNode rootNode = ConfigNode.Load(path);
                if (rootNode == null)
                {
                    ParsekLog.Warn("GameStateStore", "Failed to parse game state events file");
                    return false;
                }

                int version = 1;
                string versionStr = rootNode.GetValue("version");
                if (!string.IsNullOrEmpty(versionStr) && !int.TryParse(versionStr, out version))
                {
                    ParsekLog.Warn("GameStateStore", $"Invalid game state events version '{versionStr}'");
                    version = 1;
                }
                if (version != 1)
                {
                    ParsekLog.Warn("GameStateStore", $"Unsupported game state events version={version} (expected 1)");
                }

                // ConfigNode.Load returns the file contents directly
                ConfigNode[] eventNodes = rootNode.GetNodes("GAME_STATE_EVENT");
                if (eventNodes != null)
                {
                    foreach (var en in eventNodes)
                        events.Add(GameStateEvent.DeserializeFrom(en));
                }

                ConfigNode[] snapNodes = rootNode.GetNodes("CONTRACT_SNAPSHOT");
                if (snapNodes != null)
                {
                    foreach (var sn in snapNodes)
                        contractSnapshots.Add(ContractSnapshot.DeserializeFrom(sn));
                }

                DeserializeScienceSubjectsFrom(rootNode);

                ParsekLog.Info("GameStateStore",
                    $"Loaded {events.Count} game state events, {contractSnapshots.Count} contract snapshots, " +
                    $"{committedScienceSubjects.Count} science subjects from {path}");

                // Log event type distribution for diagnostics
                if (events.Count > 0)
                {
                    string distribution = BuildEventTypeDistribution(events);
                    ParsekLog.Verbose("GameStateStore", $"Event type distribution: {distribution}");
                }

                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("GameStateStore", $"Failed to load game state events: {ex.Message}");
                return false;
            }
        }

        internal static bool SaveBaseline(GameStateBaseline baseline)
        {
            if (baseline == null) return false;

            string relativePath = RecordingPaths.BuildBaselineRelativePath(baseline.ut);
            string path = RecordingPaths.ResolveSaveScopedPath(relativePath);
            if (path == null)
            {
                ParsekLog.Warn("GameStateStore", "Cannot resolve baseline path — save skipped");
                return false;
            }

            try
            {
                string dir = RecordingPaths.EnsureGameStateDirectory();
                if (string.IsNullOrEmpty(dir))
                {
                    ParsekLog.Warn("GameStateStore", "EnsureGameStateDirectory returned null during SaveBaseline");
                    return false;
                }

                var rootNode = new ConfigNode("PARSEK_BASELINE");
                baseline.SerializeInto(rootNode);

                SafeWriteConfigNode(rootNode, path);

                ParsekLog.Verbose("GameStateStore", $"Saved baseline at UT {baseline.ut:F0} to {path}");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("GameStateStore", $"Failed to save baseline: {ex.Message}");
                return false;
            }
        }

        internal static bool LoadBaselines()
        {
            baselines.Clear();

            string dir = RecordingPaths.ResolveGameStateDirectory();
            if (dir == null || !Directory.Exists(dir))
            {
                ParsekLog.Verbose("GameStateStore", "No game state directory found — no baselines to load");
                return true;
            }

            try
            {
                string[] files = Directory.GetFiles(dir, "baseline_*.pgsb");
                ParsekLog.Verbose("GameStateStore", $"Found {files.Length} baseline files in {dir}");

                foreach (string file in files)
                {
                    ConfigNode rootNode = ConfigNode.Load(file);
                    if (rootNode != null)
                    {
                        var baseline = GameStateBaseline.DeserializeFrom(rootNode);
                        baselines.Add(baseline);
                    }
                    else
                    {
                        ParsekLog.Warn("GameStateStore", $"Failed to parse baseline file '{file}'");
                    }
                }

                // Sort baselines by UT
                baselines.Sort((a, b) => a.ut.CompareTo(b.ut));

                ParsekLog.Info("GameStateStore", $"Loaded {baselines.Count} baselines");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("GameStateStore", $"Failed to load baselines: {ex.Message}");
                return false;
            }
        }

        private static void SafeWriteConfigNode(ConfigNode node, string path)
        {
            FileIOUtils.SafeWriteConfigNode(node, path, "GameStateStore");
        }

        /// <summary>
        /// Builds a comma-separated distribution of event types from the given events list.
        /// Pure function — no side effects, no Unity dependencies.
        /// Example output: "ContractAccepted=2, FundsChanged=5, TechResearched=1"
        /// </summary>
        internal static string BuildEventTypeDistribution(IReadOnlyList<GameStateEvent> eventList)
        {
            var typeCounts = new Dictionary<GameStateEventType, int>();
            for (int i = 0; i < eventList.Count; i++)
            {
                var type = eventList[i].eventType;
                if (typeCounts.ContainsKey(type))
                    typeCounts[type]++;
                else
                    typeCounts[type] = 1;
            }

            var parts = new List<string>();
            foreach (var kvp in typeCounts)
                parts.Add($"{kvp.Key}={kvp.Value}");
            return string.Join(", ", parts);
        }

        #endregion

        #region Testing Support

        internal static void ResetForTesting()
        {
            events.Clear();
            contractSnapshots.Clear();
            baselines.Clear();
            committedScienceSubjects.Clear();
            originalScienceValues.Clear();
            initialLoadDone = false;
            lastSaveFolder = null;
        }

        #endregion
    }
}
