using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Phase 8 of Rewind-to-Staging (design §5.3 / §5.5 / §6.6 step 2-3 /
    /// §7.17 / §7.43 / §10.4): commits a re-fly session's supersede relations
    /// for the subtree rooted at the marker's supersede target, falling back
    /// to <c>OriginChildRecordingId</c> for legacy markers,
    /// and flips the provisional's <see cref="MergeState"/> according to
    /// the Unfinished Flights safety policy: safe retry slots stay
    /// <see cref="MergeState.CommittedProvisional"/>, while closed or
    /// career/world-changing outcomes become <see cref="MergeState.Immutable"/>.
    ///
    /// <para>
    /// This commit step is what "hides the origin subtree from ERS" — it
    /// appends one <see cref="RecordingSupersedeRelation"/> per recording in
    /// the closure, all pointing at the provisional's id. The closure is the
    /// same forward-only, merge-guarded walk used by the physical-visibility
    /// layer (<see cref="EffectiveState.ComputeSessionSuppressedSubtree"/>);
    /// after commit, the ERS cache invalidates on the
    /// <see cref="ParsekScenario.BumpSupersedeStateVersion"/> bump and the
    /// subtree flips from "session-suppressed" to "superseded" — same
    /// invisibility, different mechanism.
    /// </para>
    ///
    /// <para>
    /// Phase 8 does NOT write a <see cref="MergeJournal"/>; the journaled
    /// staged commit lands in Phase 10. If this call crashes mid-way, Phase
    /// 13's load-time sweep will clean up the dangling marker + partial
    /// supersede relations.
    /// </para>
    /// </summary>
    internal static class SupersedeCommit
    {
        private const string Tag = "Supersede";
        private const string SessionTag = "ReFlySession";
        private const string LedgerSwapTag = "LedgerSwap";
        private const char WorldActionCacheKeySeparator = '\u001f';

        private static readonly object worldActionSafetyCacheLock = new object();
        private static readonly Dictionary<string, WorldActionSafetyCacheEntry> worldActionSafetyCache
            = new Dictionary<string, WorldActionSafetyCacheEntry>(StringComparer.Ordinal);
        private static object worldActionSafetyCacheScenarioIdentity;
        private static int worldActionSafetyCacheLedgerVersion = int.MinValue;
        private static int worldActionSafetyCacheStoreVersion = int.MinValue;
        private static int worldActionSafetyCacheSupersedeVersion = int.MinValue;

        /// <summary>
        /// Idempotent: appends supersede relations for every id in the
        /// forward-only merge-guarded subtree closure of
        /// <paramref name="marker"/>, flips
        /// <paramref name="provisional"/>.<see cref="Recording.MergeState"/>,
        /// clears <see cref="Recording.SupersedeTargetId"/>, and clears
        /// <see cref="ParsekScenario.ActiveReFlySessionMarker"/>.
        ///
        /// <para>
        /// Retained as the synchronous legacy entry point for direct callers
        /// (Phase 8 tests, the <c>SessionMerger</c> advisory path, and the
        /// <see cref="InGameTests.MergeCrashedReFlyCreatesCPSupersedeTest"/> /
        /// <see cref="InGameTests.MergeLandedReFlyCreatesImmutableSupersedeTest"/>
        /// live-scene tests). Phase 10's <see cref="MergeJournalOrchestrator.RunMerge"/>
        /// invokes the decomposed helpers directly so it can insert journal
        /// phase markers between them.
        /// </para>
        /// </summary>
        internal static void CommitSupersede(ReFlySessionMarker marker, Recording provisional)
        {
            if (marker == null)
            {
                ParsekLog.Warn(Tag, "CommitSupersede: marker is null — nothing to commit");
                return;
            }
            if (provisional == null)
            {
                ParsekLog.Warn(Tag, "CommitSupersede: provisional is null — nothing to commit");
                return;
            }

            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario))
            {
                ParsekLog.Warn(Tag, "CommitSupersede: no ParsekScenario instance — nothing to commit");
                return;
            }

            PreflightMergeStateClassification(marker, provisional, scenario);

            if (scenario.RecordingSupersedes == null)
                scenario.RecordingSupersedes = new List<RecordingSupersedeRelation>();
            if (scenario.LedgerTombstones == null)
                scenario.LedgerTombstones = new List<LedgerTombstone>();

            IReadOnlyCollection<string> subtree = AppendRelations(marker, provisional, scenario);

            string newRecordingId = provisional.RecordingId;
            double ut = SafeNow();
            string nowIso = DateTime.UtcNow.ToString("o");

            // Tombstones run AFTER the supersede relations land (so the
            // relations describe "what's superseded" before the ELS recomputes)
            // and BEFORE the MergeState flip's version bump so a single ELS
            // rebuild covers both changes. The subtree is rooted at
            // SupersedeTargetId when present; earlier origin -> prior-tip
            // actions were already handled by the previous merge.
            CommitTombstones(marker, subtree, newRecordingId, ut, nowIso, scenario);

            FlipMergeStateAndClearTransient(marker, provisional, scenario, preserveMarker: false);
        }

        /// <summary>
        /// Phase 10 decomposed helper (design §6.6 step 3): compute the
        /// forward-only merge-guarded subtree closure rooted at the marker's
        /// supersede target (or origin child recording for legacy markers) and append one
        /// <see cref="RecordingSupersedeRelation"/> per descendant pointing at
        /// the provisional. Idempotent: pre-existing relations are skipped
        /// with a Verbose log. Returns the closure so the downstream
        /// tombstone scan reuses the same walk.
        /// </summary>
        internal static IReadOnlyCollection<string> AppendRelations(
            ReFlySessionMarker marker, Recording provisional, ParsekScenario scenario)
            => AppendRelations(marker, provisional, scenario, extraSelfSkipRecordingIds: null);

        /// <summary>
        /// Optimizer-split-aware overload. <paramref name="extraSelfSkipRecordingIds"/>
        /// names additional recording ids whose closure entry must be filtered
        /// out as "self-links" — used by the in-place-continuation branch of
        /// <see cref="MergeDialog.TryCommitReFlySupersede"/> when
        /// <see cref="RecordingStore.RunOptimizationPass"/> has split the
        /// in-place provisional into a chain HEAD + TIP. The caller passes the
        /// HEAD id here while passing the TIP as the <paramref name="provisional"/>
        /// parameter so <see cref="ValidateSupersedeTarget"/> sees the tip's
        /// non-null terminal state, but neither the head nor the tip ends up
        /// with a row pointing at the other. Both segments are part of the
        /// same in-place flight; superseding either would collapse the chain
        /// in ERS.
        /// </summary>
        internal static IReadOnlyCollection<string> AppendRelations(
            ReFlySessionMarker marker, Recording provisional, ParsekScenario scenario,
            IReadOnlyCollection<string> extraSelfSkipRecordingIds)
        {
            // Use ReferenceEquals to skip Unity's Object == null override,
            // which would treat a destroyed ScenarioModule as null even when
            // the test fixture installed a plain-CLR instance without a
            // Unity lifecycle. The outer CommitSupersede / RunMerge call sites
            // have already validated scenario before dispatching here.
            if (marker == null || provisional == null
                || object.ReferenceEquals(null, scenario))
                return new List<string>();

            string closureRoot = marker.SupersedeTargetId ?? marker.OriginChildRecordingId;
            IReadOnlyCollection<string> subtree =
                EffectiveState.ComputeSubtreeClosureInternal(marker, closureRoot);
            int subtreeCount = subtree?.Count ?? 0;
            string newRecordingId = provisional.RecordingId;
            string originId = marker.OriginChildRecordingId;

            double ut = SafeNow();
            string nowIso = DateTime.UtcNow.ToString("o");
            var ic = CultureInfo.InvariantCulture;

            // Invariant: a supersede target is by design a recording with at
            // least one trajectory point AND a non-null terminal state. A
            // placeholder (zero points) cannot validly replace a destroyed
            // origin -- the placeholder-redirect bug class shipped twice in
            // 2026-04 (items 5, 568). Catch it at commit time so the next
            // variant fails loud instead of silently poisoning ERS with a
            // zero-trajectory replacement.
            //
            // Optimizer-split chain-tip note: when the in-place continuation
            // path resolves the chain tip pre-call (see
            // MergeDialog.TryCommitReFlySupersede's chain-tip-resolve block),
            // the recording passed here is the TIP and validation against
            // its terminal payload passes naturally. The HEAD's
            // null-terminal post-split is filtered out via
            // extraSelfSkipRecordingIds so it never reaches the row-write
            // step.
            string invariantReason;
            if (!ValidateSupersedeTarget(provisional, out invariantReason))
            {
                ParsekLog.Warn(Tag,
                    $"AppendRelations invariant violation: provisional={provisional?.RecordingId ?? "<null>"} " +
                    $"reason={invariantReason} -- refusing to write supersede rows in this batch");
#if DEBUG
                throw new InvalidOperationException(
                    $"AppendRelations invariant violation: provisional={provisional?.RecordingId ?? "<null>"} reason={invariantReason}");
#else
                return new List<string>();
#endif
            }

            HashSet<string> extraSkip = null;
            if (extraSelfSkipRecordingIds != null && extraSelfSkipRecordingIds.Count > 0)
            {
                extraSkip = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in extraSelfSkipRecordingIds)
                {
                    if (!string.IsNullOrEmpty(id))
                        extraSkip.Add(id);
                }
            }

            int added = 0;
            int skippedExisting = 0;
            int skippedSelfLink = 0;
            int skippedExtraSelfLink = 0;
            if (subtree != null)
            {
                foreach (string oldId in subtree)
                {
                    if (string.IsNullOrEmpty(oldId)) continue;
                    // In-place continuation guard: when the marker's
                    // OriginChildRecordingId == ActiveReFlyRecordingId, the
                    // subtree closure includes the origin itself, which is
                    // also the provisional. A row where old==new would form
                    // a 1-node cycle that poisons EffectiveRecordingId
                    // (`cycle detected` WARN every lookup) and pin the
                    // recording invisible to ERS. Skip the trivial self-link
                    // so callers (in particular the in-place-continuation
                    // branch of MergeDialog.TryCommitReFlySupersede) can
                    // safely invoke AppendRelations to write rows for the
                    // sibling/parent recordings without producing the cycle.
                    if (string.Equals(oldId, newRecordingId, StringComparison.Ordinal))
                    {
                        skippedSelfLink++;
                        ParsekLog.Verbose(Tag,
                            $"AppendRelations: skip self-link old={oldId} new={newRecordingId} " +
                            $"(in-place continuation; origin == provisional)");
                        continue;
                    }
                    // Optimizer-split chain-head guard: when the in-place
                    // provisional was split into HEAD + TIP, the caller
                    // passes the TIP as `provisional` so validation passes,
                    // and adds the HEAD's id here. Filter the closure entry
                    // for HEAD so HEAD does not end up with a row pointing
                    // at TIP — both halves of the in-place chain are part
                    // of the new flight; superseding either silently
                    // collapses ERS via EffectiveRecordingId redirect.
                    if (extraSkip != null && extraSkip.Contains(oldId))
                    {
                        skippedExtraSelfLink++;
                        ParsekLog.Verbose(Tag,
                            $"AppendRelations: skip extra-self-link old={oldId} new={newRecordingId} " +
                            $"(in-place continuation chain-head; tip is the supersede target)");
                        continue;
                    }
                    if (RelationExists(scenario.RecordingSupersedes, oldId, newRecordingId))
                    {
                        skippedExisting++;
                        ParsekLog.Verbose(Tag,
                            $"CommitSupersede: skip existing relation old={oldId} new={newRecordingId}");
                        continue;
                    }
                    var rel = new RecordingSupersedeRelation
                    {
                        RelationId = "rsr_" + Guid.NewGuid().ToString("N"),
                        OldRecordingId = oldId,
                        NewRecordingId = newRecordingId,
                        UT = ut,
                        CreatedRealTime = nowIso,
                    };
                    scenario.RecordingSupersedes.Add(rel);
                    added++;
                    ParsekLog.Info(Tag,
                        $"rel={rel.RelationId} old={rel.OldRecordingId} new={rel.NewRecordingId}");
                }
            }

            ParsekLog.Info(Tag,
                $"Added {added.ToString(ic)} supersede relations for subtree rooted at {closureRoot ?? "<none>"} " +
                $"(origin={originId ?? "<none>"} subtreeCount={subtreeCount.ToString(ic)} " +
                $"skippedExisting={skippedExisting.ToString(ic)} " +
                $"skippedSelfLink={skippedSelfLink.ToString(ic)} " +
                $"skippedExtraSelfLink={skippedExtraSelfLink.ToString(ic)})");

            return subtree ?? new List<string>();
        }

        /// <summary>
        /// Phase 10 decomposed helper (design §6.6 steps 2, 5, 6, 11): flip
        /// <paramref name="provisional"/>.<see cref="Recording.MergeState"/>
        /// to <see cref="MergeState.Immutable"/> or
        /// <see cref="MergeState.CommittedProvisional"/> based on terminal
        /// kind, clear the transient <see cref="Recording.SupersedeTargetId"/>,
        /// bump supersede state version so the ERS cache invalidates, and
        /// (unless <paramref name="preserveMarker"/> is set) clear
        /// <see cref="ParsekScenario.ActiveReFlySessionMarker"/>.
        ///
        /// <para>
        /// <paramref name="preserveMarker"/> is set by
        /// <see cref="MergeJournalOrchestrator.RunMerge"/> so the marker
        /// clear is deferred until AFTER Durable Save #1 (design §6.6
        /// step 11). Legacy <see cref="CommitSupersede"/> keeps the original
        /// synchronous behavior with <paramref name="preserveMarker"/> =
        /// <c>false</c>.
        /// </para>
        /// </summary>
        internal static void FlipMergeStateAndClearTransient(
            ReFlySessionMarker marker, Recording provisional, ParsekScenario scenario,
            bool preserveMarker)
        {
            if (marker == null || provisional == null
                || object.ReferenceEquals(null, scenario)) return;

            MergeStateClassification classification =
                ClassifyMergeStateOrThrow(marker, provisional, scenario, logFallback: true);

            provisional.MergeState = classification.NewState;
            ApplyAutoSealAfterSafetyClose(classification, provisional, scenario);

            string priorTarget = provisional.SupersedeTargetId;
            provisional.SupersedeTargetId = null;

            scenario.BumpSupersedeStateVersion();

            ParsekLog.Info(Tag,
                $"provisional={provisional.RecordingId ?? "<no-id>"} mergeState={classification.NewState} terminalKind={classification.Kind} " +
                $"qualifies={classification.ClassifierQualifies} " +
                $"slot={(classification.ClassifierResolvedSlot ? classification.SlotListIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) : "<none>")} " +
                $"rp={(classification.ClassifierResolvedSlot ? classification.RewindPointId ?? "<no-rp>" : "<none>")} " +
                $"focusSlot={(classification.ClassifierResolvedSlot ? classification.FocusSlotIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) : "<none>")} " +
                $"focusSlotOverride={(classification.ClassifierResolvedSlot ? classification.SlotListIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) : "<none>")} " +
                $"classifierReason={classification.ClassifierReason ?? "<none>"} " +
                $"autoSeal={classification.AutoSealSlot} " +
                $"autoSealReason={classification.AutoSealReason ?? "<none>"} " +
                $"priorTarget={priorTarget ?? "<none>"}");

            if (preserveMarker) return;

            string sessionId = marker.SessionId;
            scenario.ActiveReFlySessionMarker = null;
            Parsek.Rendering.RenderSessionState.Clear("marker-cleared");
            scenario.BumpSupersedeStateVersion();
            // Drop any forced CanRevertToPostInit override now that the
            // session is committed. Normally this commit happens at scene
            // exit (KSC), so it's a no-op there; defensive against any future
            // call site that commits while still in flight.
            ReFlyRevertButtonGate.Apply("SupersedeCommit:marker-cleared");

            // #688 follow-up: drop any captured pre-Re-Fly anchor trajectory
            // snapshot now that the session is committed. The snapshot was
            // only needed to feed the per-frame anchor offset while the live
            // recording was being trimmed/extended; post-merge the recording's
            // own data is final and the snapshot would otherwise linger in
            // memory and in the .sfs as dead weight (the codec writes a
            // full PRE_REFLY_ANCHOR node whenever HasPreReFlyAnchorTrajectory
            // returns true). Idempotent — clears all recordings tagged with
            // this session id, even though under the in-place contract only
            // one recording carries a snapshot per session.
            ClearPreReFlyAnchorSnapshotsForSession(sessionId);

            ParsekLog.Info(SessionTag,
                $"End reason=merged sess={sessionId ?? "<no-id>"} provisional={provisional.RecordingId ?? "<no-id>"}");
        }

        internal static int ClearPreReFlyAnchorSnapshotsForSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return 0;
            int cleared = 0;
            var recordings = RecordingStore.CommittedRecordings;
            if (recordings != null)
            {
                for (int i = 0; i < recordings.Count; i++)
                {
                    var rec = recordings[i];
                    if (rec == null) continue;
                    if (!string.Equals(
                            rec.PreReFlyAnchorSessionId, sessionId, StringComparison.Ordinal))
                        continue;
                    rec.ClearPreReFlyAnchorTrajectory();
                    cleared++;
                }
            }
            if (cleared > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"Cleared {cleared} pre-Re-Fly anchor snapshot(s) for sess={sessionId}");
            }
            return cleared;
        }

        /// <summary>
        /// Side-effect-free guard for call sites that will append supersede
        /// relations and tombstones before flipping the provisional state.
        /// Stable-leaf/EVA outcomes need a slot-aware verdict; if that lookup
        /// is impossible, fail before any durable merge mutations land.
        /// </summary>
        internal static void PreflightMergeStateClassification(
            ReFlySessionMarker marker, Recording provisional, ParsekScenario scenario)
        {
            if (marker == null || provisional == null
                || object.ReferenceEquals(null, scenario)) return;
            ClassifyMergeStateOrThrow(marker, provisional, scenario, logFallback: false);
        }

        private struct MergeStateClassification
        {
            public TerminalKind Kind;
            public MergeState NewState;
            public string ClassifierReason;
            public bool ClassifierResolvedSlot;
            public bool ClassifierQualifies;
            public int SlotListIndex;
            public string RewindPointId;
            public int FocusSlotIndex;
            public RewindPoint RewindPoint;
            public ChildSlot Slot;
            public bool AutoSealSlot;
            public string AutoSealReason;
        }

        private enum ReFlyCloseReasonKind
        {
            None,
            ClassifierClosed,
            RecordingAction,
            UnsafeClassifierReason,
            StructuralMutation,
        }

        private struct ReFlyCloseReason
        {
            public ReFlyCloseReasonKind Kind;
            public string Detail;

            public bool HasValue => Kind != ReFlyCloseReasonKind.None;

            public string ToLogString()
            {
                switch (Kind)
                {
                    case ReFlyCloseReasonKind.ClassifierClosed:
                        return "classifierClosed:" + (Detail ?? "<none>");
                    case ReFlyCloseReasonKind.RecordingAction:
                        return "recordingAction:" + (Detail ?? "<none>");
                    case ReFlyCloseReasonKind.UnsafeClassifierReason:
                        return "unsafeClassifierReason:" + (Detail ?? "<none>");
                    case ReFlyCloseReasonKind.StructuralMutation:
                        return "structuralMutation:" + (Detail ?? "<none>");
                    default:
                        return null;
                }
            }
        }

        private struct WorldActionSafetyCacheEntry
        {
            public bool HasAction;
            public string ActionSummary;
        }

        private static MergeStateClassification ClassifyMergeStateOrThrow(
            ReFlySessionMarker marker,
            Recording provisional,
            ParsekScenario scenario,
            bool logFallback)
        {
            TerminalKind kind = TerminalKindClassifier.Classify(provisional);
            var result = new MergeStateClassification
            {
                Kind = kind,
                NewState = (kind == TerminalKind.Crashed)
                    ? MergeState.CommittedProvisional
                    : MergeState.Immutable,
                ClassifierReason = "fallback:" + kind,
                SlotListIndex = -1,
                FocusSlotIndex = -1,
            };

            RewindPoint rp;
            int slotListIndex;
            string slotRejectReason;
            if (TryResolveSlotForMergeClassification(
                    marker, provisional, scenario, out rp, out slotListIndex,
                    out slotRejectReason)
                && rp?.ChildSlots != null
                && slotListIndex >= 0
                && slotListIndex < rp.ChildSlots.Count)
            {
                var slot = rp.ChildSlots[slotListIndex];
                string classifierReason;
                // Treat the merge-time slot as the de-facto focus: the player
                // just Re-Flew this slot themselves, so a stable Orbiting /
                // SubOrbital terminal is a concluded outcome, not a "non-focus
                // unconcluded leaf" left over from background flight. Without
                // this override the classifier would compare against the
                // capture-time rp.FocusSlotIndex (whichever vessel happened to
                // be active when the RP was recorded) and keep the slot
                // re-flyable, blocking auto-seal. Natural / non-Re-Fly call
                // sites do not pass an override and continue to use the
                // static focus.
                bool classifierQualifies = UnfinishedFlightClassifier.TryQualify(
                    provisional, slot, rp, false, out classifierReason,
                    treeContext: null, allowNotCommitted: true,
                    focusSlotOverride: slotListIndex);

                ReFlyCloseReason closeReason;
                bool keepSlotOpen = ShouldKeepReFlySlotOpenAfterMerge(
                    provisional, marker, classifierQualifies, classifierReason,
                    out closeReason);
                result.NewState = keepSlotOpen
                    ? MergeState.CommittedProvisional
                    : MergeState.Immutable;
                result.ClassifierReason = classifierReason;
                result.ClassifierResolvedSlot = true;
                result.ClassifierQualifies = classifierQualifies;
                result.SlotListIndex = slotListIndex;
                result.RewindPointId = rp.RewindPointId;
                result.FocusSlotIndex = rp.FocusSlotIndex;
                result.RewindPoint = rp;
                result.Slot = slot;
                if (ShouldAutoSealReFlySlotAfterMerge(
                    provisional, classifierQualifies, closeReason))
                {
                    result.AutoSealSlot = true;
                    result.AutoSealReason = closeReason.ToLogString();
                }
                return result;
            }

            string slotLookupFailure =
                $"Site B-1 slot lookup failed for provisional={provisional.RecordingId ?? "<no-id>"} " +
                $"markerOrigin={marker.OriginChildRecordingId ?? "<none>"} " +
                $"markerTarget={marker.SupersedeTargetId ?? "<none>"} " +
                $"rp={marker.RewindPointId ?? "<none>"} reason={slotRejectReason ?? "slot-index-invalid"}; " +
                $"terminal={DescribeTerminalForLogs(provisional)}";
            if (!IsInPlaceContinuation(marker, provisional)
                && RequiresSlotAwareMergeClassification(provisional))
            {
                ParsekLog.Error(Tag,
                    slotLookupFailure +
                    "; aborting because stable-leaf classification cannot safely fall back");
                throw new InvalidOperationException(slotLookupFailure);
            }

            if (logFallback)
            {
                if (IsInPlaceContinuation(marker, provisional))
                {
                    ParsekLog.Verbose(Tag,
                        slotLookupFailure +
                        "; in-place continuation: using v0.9 terminalKind classifier");
                }
                else
                {
                    ParsekLog.Error(Tag,
                        slotLookupFailure + "; falling back to v0.9 terminalKind classifier");
                }
            }

            return result;
        }

        internal static bool IsTerminalFailureReFlyOutcome(Recording rec)
        {
            Recording terminalRec = EffectiveState.ResolveChainTerminalRecording(rec) ?? rec;
            TerminalState? terminal = terminalRec?.TerminalStateValue;
            if (!terminal.HasValue) return false;
            if (terminal.Value == TerminalState.Destroyed)
                return true;
            return !string.IsNullOrEmpty(terminalRec.EvaCrewName)
                && terminal.Value != TerminalState.Boarded;
        }

        private static bool ShouldKeepReFlySlotOpenAfterMerge(
            Recording rec,
            ReFlySessionMarker marker,
            bool classifierQualifies,
            string classifierReason,
            out ReFlyCloseReason closeReason)
        {
            closeReason = default(ReFlyCloseReason);
            if (!classifierQualifies)
            {
                if (TryBuildRecordingActionCloseReason(
                        classifierReason, out closeReason))
                    return false;

                closeReason = new ReFlyCloseReason
                {
                    Kind = ReFlyCloseReasonKind.ClassifierClosed,
                    Detail = classifierReason,
                };
                return false;
            }

            string actionSummary;
            if (TryFindRetryBlockingWorldAction(rec, out actionSummary))
            {
                closeReason = new ReFlyCloseReason
                {
                    Kind = ReFlyCloseReasonKind.RecordingAction,
                    Detail = actionSummary,
                };
                return false;
            }

            if (IsTerminalFailureReFlyOutcome(rec))
                return true;

            // Structural-mutation seal: a Re-Fly that produced a sibling /
            // child vessel via decouple, stage, undock, joint break, etc. is
            // a concluded re-flight even when the chain tip itself is still
            // a "safe stable retry" leaf (Orbiting / SubOrbital non-focus or
            // a Stashed slot). Player intent, by playtest contract: any
            // shape change to the Re-Fly target during the session means
            // they want to keep the run, not retry — close the slot here.
            // Crashed / stranded-EVA outcomes return earlier above so the
            // existing terminal-failure retry path is preserved.
            string structuralDetail;
            if (HasReFlySessionStructuralMutation(rec, marker, out structuralDetail))
            {
                closeReason = new ReFlyCloseReason
                {
                    Kind = ReFlyCloseReasonKind.StructuralMutation,
                    Detail = structuralDetail,
                };
                return false;
            }

            if (!IsSafeStableRetryClassifierReason(classifierReason))
            {
                closeReason = new ReFlyCloseReason
                {
                    Kind = ReFlyCloseReasonKind.UnsafeClassifierReason,
                    Detail = classifierReason,
                };
                return false;
            }

            return true;
        }

        /// <summary>
        /// Returns true when the Re-Fly session created at least one
        /// structural-event branch point in the provisional's tree after
        /// the rewind point's UT — i.e. a decouple / stage / undock /
        /// joint-break / EVA produced a sibling vessel during this
        /// Re-Fly. Detection is on tree topology rather than
        /// <see cref="Recording.CreatingSessionId"/> because
        /// <c>CreateBreakupChildRecording</c> does not propagate the
        /// session id to debris / controlled-child recordings, so a
        /// session-tagged-recording scan would miss the most common
        /// structural events (visible in the playtest log as
        /// <c>Coalescer ProcessBreakupEvent: ... child created</c>).
        ///
        /// <para>
        /// The cutoff is the resolved rewind point's <c>UT</c>, NOT
        /// <c>marker.InvokedUT</c>. <c>InvokedUT</c> is the live UT at
        /// the moment the player clicked Re-Fly — typically much later
        /// than the rewind point's UT, because the RP quicksave throws
        /// the player back to an earlier saved state. A player who
        /// clicked Re-Fly at UT=300, was rewound to UT=100, and then
        /// decoupled at UT=150 must trip this gate; using
        /// <c>marker.InvokedUT</c> would incorrectly reject that
        /// branch point as "pre-rewind". If the marker's RP cannot be
        /// resolved on the live scenario (defensive: should not happen
        /// during a normal Re-Fly merge), the helper falls back to
        /// <c>marker.InvokedUT</c> so the gate at least catches the
        /// post-invocation tail.
        /// </para>
        ///
        /// <para>
        /// Non-structural branch-point types
        /// (<see cref="BranchPointType.Dock"/>,
        /// <see cref="BranchPointType.Board"/>,
        /// <see cref="BranchPointType.Launch"/>,
        /// <see cref="BranchPointType.Terminal"/>) are excluded — they
        /// either attach to a pre-existing vessel without creating a
        /// new one, or mark tree boundaries. Caller is expected to gate
        /// by terminal kind (crashed / EVA outcomes use the existing
        /// retry path before this check fires).
        /// </para>
        /// </summary>
        internal static bool HasReFlySessionStructuralMutation(
            Recording rec,
            ReFlySessionMarker marker,
            out string detail)
        {
            detail = null;
            if (rec == null || marker == null) return false;
            if (string.IsNullOrEmpty(marker.TreeId)) return false;
            if (string.IsNullOrEmpty(rec.TreeId)) return false;
            if (!string.Equals(rec.TreeId, marker.TreeId,
                    StringComparison.Ordinal))
                return false;

            // Session-local baseline: when the marker was created we
            // snapshotted every existing BranchPoint.Id in the tree.
            // Without that baseline, the load-time splice path
            // (`SpliceMissingCommittedRecordingsIntoLoadedTree`) re-grafts
            // pre-Re-Fly post-RP branch points back into the loaded tree
            // and they look identical to session-authored ones — so the
            // gate would auto-seal a stashed slot the player never
            // mutated. Conservatively skip the gate when the baseline is
            // absent (legacy markers created before this field shipped).
            if (marker.PreSessionBranchPointIds == null) return false;

            ParsekScenario scenario = ParsekScenario.Instance;
            double? cutoffSource = TryResolveReFlyStructuralCutoffUT(
                marker, scenario, out string cutoffOriginLabel);
            if (!cutoffSource.HasValue) return false;

            RecordingTree tree = RecordingStore.CommittedTrees != null
                ? RecordingStore.CommittedTrees.Find(t =>
                    t != null
                    && string.Equals(t.Id, marker.TreeId,
                        StringComparison.Ordinal))
                : null;
            if (tree == null || tree.BranchPoints == null) return false;

            HashSet<string> preSessionSet = new HashSet<string>(
                marker.PreSessionBranchPointIds, StringComparer.Ordinal);
            HashSet<string> lineageSet = BuildReFlyTargetLineageRecordingIds(rec);

            // A small UT slack absorbs floating-point round-trip drift
            // between the resolved cutoff UT and a branch-point UT that
            // arose from the same physics frame. The rewind point itself
            // does not author its own branch point at its UT, so this is
            // exclusion-safe.
            const double UtSlackSeconds = 0.001;
            double cutoff = cutoffSource.Value + UtSlackSeconds;

            int matchedCount = 0;
            int spliceExcludedCount = 0;
            int lineageExcludedCount = 0;
            string firstBpId = null;
            BranchPointType? firstBpType = null;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                var bp = tree.BranchPoints[i];
                if (bp == null) continue;
                if (!IsStructuralBranchPointType(bp.Type)) continue;
                if (bp.UT < cutoff) continue;
                if (!string.IsNullOrEmpty(bp.Id)
                    && preSessionSet.Contains(bp.Id))
                {
                    // Pre-existing BP re-spliced into the loaded tree by
                    // SpliceMissingCommittedRecordingsIntoLoadedTree —
                    // not a Re-Fly-session mutation.
                    spliceExcludedCount++;
                    continue;
                }
                if (!IsBranchPointInReFlyTargetLineage(bp, lineageSet))
                {
                    // Same tree, but the BP's parent recordings don't
                    // intersect the Re-Fly target's lineage (provisional +
                    // chain segments). A background vessel that staged /
                    // undocked / joint-broke during this session in the
                    // same tree authored its own BP with its own parent
                    // ids — that's not a mutation of the player-chosen
                    // slot and must not auto-seal it.
                    lineageExcludedCount++;
                    continue;
                }

                matchedCount++;
                if (firstBpId == null)
                {
                    firstBpId = bp.Id;
                    firstBpType = bp.Type;
                }
            }

            if (matchedCount == 0) return false;

            var ic = CultureInfo.InvariantCulture;
            detail = $"branchPoints={matchedCount.ToString(ic)}" +
                $" spliceExcluded={spliceExcludedCount.ToString(ic)}" +
                $" lineageExcluded={lineageExcludedCount.ToString(ic)}" +
                $" firstBp={firstBpId ?? "<no-id>"}" +
                $" firstType={(firstBpType.HasValue ? firstBpType.Value.ToString() : "<none>")}" +
                $" sinceUT={cutoffSource.Value.ToString("F2", ic)}" +
                $" cutoffOrigin={cutoffOriginLabel}" +
                $" baseline={marker.PreSessionBranchPointIds.Count.ToString(ic)}" +
                $" lineage={lineageSet.Count.ToString(ic)}";
            return true;
        }

        /// <summary>
        /// Builds the set of recording ids that count as the Re-Fly target's
        /// lineage for structural-mutation detection: the provisional itself
        /// plus every committed recording in the same tree / chain /
        /// chain-branch as the provisional. Optimizer-split tails of the
        /// in-place chain share the provisional's <see cref="Recording.ChainId"/>
        /// + <see cref="Recording.ChainBranch"/> and are therefore included.
        /// A BranchPoint whose <see cref="BranchPoint.ParentRecordingIds"/>
        /// does not intersect this set was authored by an unrelated vessel
        /// (background sibling in the same tree) and must not trip the gate.
        /// </summary>
        private static HashSet<string> BuildReFlyTargetLineageRecordingIds(Recording provisional)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (provisional == null) return set;
            if (!string.IsNullOrEmpty(provisional.RecordingId))
                set.Add(provisional.RecordingId);

            string treeId = provisional.TreeId;
            string chainId = provisional.ChainId;
            int chainBranch = provisional.ChainBranch;
            if (string.IsNullOrEmpty(treeId) || string.IsNullOrEmpty(chainId))
                return set;

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return set;
            for (int i = 0; i < committed.Count; i++)
            {
                var cand = committed[i];
                if (cand == null) continue;
                if (object.ReferenceEquals(cand, provisional)) continue;
                if (string.IsNullOrEmpty(cand.RecordingId)) continue;
                if (!string.Equals(cand.TreeId, treeId, StringComparison.Ordinal))
                    continue;
                if (!string.Equals(cand.ChainId, chainId, StringComparison.Ordinal))
                    continue;
                if (cand.ChainBranch != chainBranch) continue;
                set.Add(cand.RecordingId);
            }
            return set;
        }

        private static bool IsBranchPointInReFlyTargetLineage(
            BranchPoint bp, HashSet<string> lineageSet)
        {
            if (bp == null || lineageSet == null || lineageSet.Count == 0)
                return false;
            var parents = bp.ParentRecordingIds;
            if (parents == null || parents.Count == 0)
                return false;
            for (int i = 0; i < parents.Count; i++)
            {
                string pid = parents[i];
                if (!string.IsNullOrEmpty(pid) && lineageSet.Contains(pid))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Resolves the UT cutoff used by structural-mutation detection.
        /// Prefers the marker's rewind point UT (where the player was
        /// placed back to by the RP quicksave); falls back to
        /// <c>marker.InvokedUT</c> if the RP cannot be located on the
        /// live scenario. Returns null only when neither source supplies
        /// a finite, non-negative UT.
        /// </summary>
        private static double? TryResolveReFlyStructuralCutoffUT(
            ReFlySessionMarker marker,
            ParsekScenario scenario,
            out string originLabel)
        {
            originLabel = "<none>";
            if (marker == null) return null;

            if (!object.ReferenceEquals(null, scenario)
                && scenario.RewindPoints != null
                && !string.IsNullOrEmpty(marker.RewindPointId))
            {
                for (int i = 0; i < scenario.RewindPoints.Count; i++)
                {
                    var candidate = scenario.RewindPoints[i];
                    if (candidate == null) continue;
                    if (!string.Equals(candidate.RewindPointId,
                            marker.RewindPointId, StringComparison.Ordinal))
                        continue;
                    if (double.IsNaN(candidate.UT)
                        || double.IsInfinity(candidate.UT)
                        || candidate.UT < 0)
                        break;
                    originLabel = "rpUT";
                    return candidate.UT;
                }
            }

            if (!double.IsNaN(marker.InvokedUT)
                && !double.IsInfinity(marker.InvokedUT)
                && marker.InvokedUT >= 0)
            {
                originLabel = "invokedUT";
                return marker.InvokedUT;
            }

            return null;
        }

        private static bool IsStructuralBranchPointType(BranchPointType type)
        {
            // Dock / Board / Launch / Terminal are not "the player changed
            // the vessel's shape": Dock and Board attach to a pre-existing
            // vessel without spawning a new one mid-flight; Launch is the
            // tree root; Terminal marks the recording's end. Everything
            // else (Undock, EVA, JointBreak, Breakup) creates a new
            // sibling vessel and counts as structural mutation.
            switch (type)
            {
                case BranchPointType.Undock:
                case BranchPointType.EVA:
                case BranchPointType.JointBreak:
                case BranchPointType.Breakup:
                    return true;
                default:
                    return false;
            }
        }

        private static bool ShouldAutoSealReFlySlotAfterMerge(
            Recording rec,
            bool classifierQualifies,
            ReFlyCloseReason closeReason)
        {
            if (!closeReason.HasValue)
                return false;
            if (closeReason.Kind == ReFlyCloseReasonKind.RecordingAction)
                return true;
            if (closeReason.Kind == ReFlyCloseReasonKind.StructuralMutation)
                return true;
            if (classifierQualifies)
                return true;

            if (closeReason.Kind != ReFlyCloseReasonKind.ClassifierClosed)
                return false;

            if (string.Equals(closeReason.Detail, "downstreamBp",
                    StringComparison.Ordinal))
                return true;
            if (string.Equals(closeReason.Detail, "stableTerminal",
                    StringComparison.Ordinal))
                return IsHardSafetyTerminal(rec);
            // The Re-Fly target slot (player-chosen, either static focus or
            // promoted via focusSlotOverride) reached a stable Orbiting /
            // SubOrbital terminal. Per playtest contract: a successful
            // Re-Fly to stable state seals the slot — the player is done
            // with that line of flight, no further retry expected.
            if (string.Equals(closeReason.Detail, "stableTerminalFocusSlot",
                    StringComparison.Ordinal))
                return true;

            return false;
        }

        private static bool TryBuildRecordingActionCloseReason(
            string classifierReason,
            out ReFlyCloseReason closeReason)
        {
            closeReason = default(ReFlyCloseReason);
            if (string.IsNullOrEmpty(classifierReason)
                || !classifierReason.StartsWith(
                    UnfinishedFlightClassifier.RecordingActionReasonPrefix,
                    StringComparison.Ordinal))
                return false;

            closeReason = new ReFlyCloseReason
            {
                Kind = ReFlyCloseReasonKind.RecordingAction,
                Detail = classifierReason.Substring(
                    UnfinishedFlightClassifier.RecordingActionReasonPrefix.Length),
            };
            return true;
        }

        private static bool IsHardSafetyTerminal(Recording rec)
        {
            Recording terminalRec = EffectiveState.ResolveChainTerminalRecording(rec) ?? rec;
            TerminalState? terminal = terminalRec?.TerminalStateValue;
            if (!terminal.HasValue)
                return false;

            return terminal.Value == TerminalState.Recovered
                || terminal.Value == TerminalState.Docked
                || terminal.Value == TerminalState.Boarded;
        }

        private static bool IsSafeStableRetryClassifierReason(string classifierReason)
        {
            return string.Equals(classifierReason, "stableLeafUnconcluded",
                    StringComparison.Ordinal)
                || string.Equals(classifierReason, "stashedStableLeaf",
                    StringComparison.Ordinal);
        }

        internal static bool TryFindRecordingScopedWorldAction(
            Recording rec,
            out string actionSummary)
        {
            return TryFindRecordingScopedWorldAction(
                rec, ignoreMilestoneAchievements: false, out actionSummary);
        }

        internal static bool TryFindRetryBlockingWorldAction(
            Recording rec,
            out string actionSummary)
        {
            return TryFindRecordingScopedWorldAction(
                rec, ignoreMilestoneAchievements: true, out actionSummary);
        }

        private static bool TryFindRecordingScopedWorldAction(
            Recording rec,
            bool ignoreMilestoneAchievements,
            out string actionSummary)
        {
            actionSummary = null;
            string cacheKey = BuildWorldActionSafetyCacheKey(
                rec, ignoreMilestoneAchievements);
            if (cacheKey == null)
                return false;

            ParsekScenario scenario = ParsekScenario.Instance;
            WorldActionSafetyCacheEntry cached;
            if (TryGetCachedWorldActionSafetyVerdict(
                    cacheKey, scenario, out cached))
            {
                actionSummary = cached.ActionSummary;
                return cached.HasAction;
            }

            var computed = ComputeRecordingScopedWorldAction(
                rec, ignoreMilestoneAchievements);
            CacheWorldActionSafetyVerdict(cacheKey, scenario, computed);
            actionSummary = computed.ActionSummary;
            return computed.HasAction;
        }

        private static WorldActionSafetyCacheEntry ComputeRecordingScopedWorldAction(
            Recording rec,
            bool ignoreMilestoneAchievements)
        {
            var result = new WorldActionSafetyCacheEntry();
            var recordingIds = CollectRecordingIdsForSafetyGate(rec);
            if (recordingIds.Count == 0)
                return result;

            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null || string.IsNullOrEmpty(action.RecordingId))
                    continue;
                if (!recordingIds.Contains(action.RecordingId))
                    continue;
                if (ignoreMilestoneAchievements
                    && action.Type == GameActionType.MilestoneAchievement)
                    continue;
                if (!IsWorldStateChangingRecordingAction(action, actions))
                    continue;

                result.HasAction = true;
                result.ActionSummary = action.Type + ":" + (action.ActionId ?? "<no-action-id>");
                return result;
            }

            return result;
        }

        private static bool TryGetCachedWorldActionSafetyVerdict(
            string cacheKey,
            ParsekScenario scenario,
            out WorldActionSafetyCacheEntry entry)
        {
            lock (worldActionSafetyCacheLock)
            {
                EnsureWorldActionSafetyCacheCurrent(scenario);
                return worldActionSafetyCache.TryGetValue(cacheKey, out entry);
            }
        }

        private static void CacheWorldActionSafetyVerdict(
            string cacheKey,
            ParsekScenario scenario,
            WorldActionSafetyCacheEntry entry)
        {
            lock (worldActionSafetyCacheLock)
            {
                EnsureWorldActionSafetyCacheCurrent(scenario);
                worldActionSafetyCache[cacheKey] = entry;
            }
        }

        internal static void ResetWorldActionSafetyCacheForTesting()
        {
            lock (worldActionSafetyCacheLock)
            {
                worldActionSafetyCache.Clear();
                worldActionSafetyCacheScenarioIdentity = null;
                worldActionSafetyCacheLedgerVersion = int.MinValue;
                worldActionSafetyCacheStoreVersion = int.MinValue;
                worldActionSafetyCacheSupersedeVersion = int.MinValue;
            }
        }

        private static void EnsureWorldActionSafetyCacheCurrent(
            ParsekScenario scenario)
        {
            int supersedeVersion = !object.ReferenceEquals(null, scenario)
                ? scenario.SupersedeStateVersion
                : 0;
            if (ReferenceEquals(worldActionSafetyCacheScenarioIdentity, scenario)
                && worldActionSafetyCacheLedgerVersion == Ledger.StateVersion
                && worldActionSafetyCacheStoreVersion == RecordingStore.StateVersion
                && worldActionSafetyCacheSupersedeVersion == supersedeVersion)
                return;

            worldActionSafetyCache.Clear();
            worldActionSafetyCacheScenarioIdentity = scenario;
            worldActionSafetyCacheLedgerVersion = Ledger.StateVersion;
            worldActionSafetyCacheStoreVersion = RecordingStore.StateVersion;
            worldActionSafetyCacheSupersedeVersion = supersedeVersion;
        }

        private static string BuildWorldActionSafetyCacheKey(
            Recording rec,
            bool ignoreMilestoneAchievements)
        {
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return null;

            string mode = ignoreMilestoneAchievements ? "retry" : "strict";
            return mode
                + WorldActionCacheKeySeparator
                + rec.RecordingId
                + WorldActionCacheKeySeparator
                + (rec.SupersedeTargetId ?? string.Empty)
                + WorldActionCacheKeySeparator
                + (rec.TreeId ?? string.Empty)
                + WorldActionCacheKeySeparator
                + (rec.ChainId ?? string.Empty)
                + WorldActionCacheKeySeparator
                + rec.ChainBranch.ToString(CultureInfo.InvariantCulture);
        }

        private static HashSet<string> CollectRecordingIdsForSafetyGate(Recording rec)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            AddRecordingId(ids, rec);
            AddRecordingId(ids, rec?.SupersedeTargetId);

            Recording terminalRec = EffectiveState.ResolveChainTerminalRecording(rec) ?? rec;
            AddRecordingId(ids, terminalRec);

            AddMatchingChainRecordingIds(ids, rec, RecordingStore.CommittedRecordings);
            for (int i = 0; i < RecordingStore.CommittedTrees.Count; i++)
            {
                var tree = RecordingStore.CommittedTrees[i];
                if (tree?.Recordings == null) continue;
                AddMatchingChainRecordingIds(ids, rec, tree.Recordings.Values);
            }
            AddSupersedeLineageRecordingIds(
                ids, ParsekScenario.Instance?.RecordingSupersedes);

            return ids;
        }

        private static void AddRecordingId(HashSet<string> ids, Recording rec)
        {
            if (ids == null || rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return;
            ids.Add(rec.RecordingId);
        }

        private static void AddRecordingId(HashSet<string> ids, string recordingId)
        {
            if (ids == null || string.IsNullOrEmpty(recordingId))
                return;
            ids.Add(recordingId);
        }

        private static void AddMatchingChainRecordingIds(
            HashSet<string> ids,
            Recording anchor,
            IEnumerable<Recording> candidates)
        {
            if (ids == null || anchor == null || candidates == null)
                return;
            if (string.IsNullOrEmpty(anchor.ChainId))
                return;

            foreach (var candidate in candidates)
            {
                if (candidate == null || string.IsNullOrEmpty(candidate.RecordingId))
                    continue;
                if (!string.Equals(candidate.ChainId, anchor.ChainId,
                        StringComparison.Ordinal))
                    continue;
                if (candidate.ChainBranch != anchor.ChainBranch)
                    continue;
                if (!string.IsNullOrEmpty(anchor.TreeId)
                    && !string.IsNullOrEmpty(candidate.TreeId)
                    && !string.Equals(candidate.TreeId, anchor.TreeId,
                        StringComparison.Ordinal))
                    continue;

                ids.Add(candidate.RecordingId);
            }
        }

        private static void AddSupersedeLineageRecordingIds(
            HashSet<string> ids,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            if (ids == null || supersedes == null || supersedes.Count == 0)
                return;

            bool added;
            do
            {
                added = false;
                for (int i = 0; i < supersedes.Count; i++)
                {
                    var rel = supersedes[i];
                    if (rel == null
                        || string.IsNullOrEmpty(rel.OldRecordingId)
                        || string.IsNullOrEmpty(rel.NewRecordingId))
                        continue;
                    if (!ids.Contains(rel.NewRecordingId)
                        || ids.Contains(rel.OldRecordingId))
                        continue;

                    ids.Add(rel.OldRecordingId);
                    added = true;
                }
            }
            while (added);
        }

        internal static bool IsWorldStateChangingRecordingAction(
            GameAction action,
            IReadOnlyList<GameAction> sameTimelineActions)
        {
            if (action == null || string.IsNullOrEmpty(action.RecordingId))
                return false;

            switch (action.Type)
            {
                case GameActionType.FundsInitial:
                case GameActionType.ScienceInitial:
                case GameActionType.ReputationInitial:
                    return false;
            }

            if (TombstoneEligibility.IsEligible(action))
                return false;

            GameAction pairedDeathAction;
            if (TombstoneEligibility.TryPairBundledRepPenalty(
                    action, sameTimelineActions, out pairedDeathAction))
                return false;

            return true;
        }

        private static void ApplyAutoSealAfterSafetyClose(
            MergeStateClassification classification,
            Recording provisional,
            ParsekScenario scenario)
        {
            if (!classification.AutoSealSlot || classification.Slot == null
                || object.ReferenceEquals(null, scenario))
                return;

            if (classification.Slot.Sealed)
                return;

            classification.Slot.Sealed = true;
            classification.Slot.SealedRealTime =
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            scenario.BumpSupersedeStateVersion();
            ParsekLog.Info(Tag,
                $"Auto-sealed re-fly slot={classification.SlotListIndex.ToString(CultureInfo.InvariantCulture)} " +
                $"rec={provisional?.RecordingId ?? "<no-id>"} " +
                $"rp={classification.RewindPoint?.RewindPointId ?? "<no-rp>"} " +
                $"terminal={DescribeTerminalForLogs(provisional)} " +
                $"reason={classification.AutoSealReason ?? "<none>"}");
        }

        private static bool TryResolveSlotForMergeClassification(
            ReFlySessionMarker marker,
            Recording provisional,
            ParsekScenario scenario,
            out RewindPoint rp,
            out int slotListIndex,
            out string rejectReason)
        {
            if (UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                    provisional, out rp, out slotListIndex, out rejectReason))
                return true;

            return TryResolveSlotByMarkerTarget(
                marker, provisional, scenario, out rp, out slotListIndex, out rejectReason);
        }

        private static bool TryResolveSlotByMarkerTarget(
            ReFlySessionMarker marker,
            Recording provisional,
            ParsekScenario scenario,
            out RewindPoint rp,
            out int slotListIndex,
            out string rejectReason)
        {
            rp = null;
            slotListIndex = -1;
            rejectReason = null;
            if (marker == null || provisional == null)
            {
                rejectReason = "marker-or-provisional-null";
                return false;
            }

            string slotTarget = marker.SupersedeTargetId ?? marker.OriginChildRecordingId;
            if (string.IsNullOrEmpty(slotTarget))
            {
                rejectReason = "noMarkerSlotTarget";
                return false;
            }

            string parentBp = provisional.ParentBranchPointId;
            string childBp = provisional.ChildBranchPointId;
            bool hasBranchLink =
                !string.IsNullOrEmpty(parentBp)
                || !string.IsNullOrEmpty(childBp);

            if (object.ReferenceEquals(null, scenario) || scenario.RewindPoints == null)
            {
                rejectReason = "noScenario";
                return false;
            }

            IReadOnlyList<RecordingSupersedeRelation> supersedes =
                scenario.RecordingSupersedes
                ?? (IReadOnlyList<RecordingSupersedeRelation>)Array.Empty<RecordingSupersedeRelation>();
            var targetRec = new Recording { RecordingId = slotTarget };
            bool matchedRp = false;
            for (int i = 0; i < scenario.RewindPoints.Count; i++)
            {
                var candidate = scenario.RewindPoints[i];
                if (candidate == null) continue;
                bool matchesParent = !string.IsNullOrEmpty(parentBp)
                    && string.Equals(candidate.BranchPointId, parentBp, StringComparison.Ordinal);
                bool matchesChild = !string.IsNullOrEmpty(childBp)
                    && string.Equals(candidate.BranchPointId, childBp, StringComparison.Ordinal);
                if (!matchesParent && !matchesChild)
                    continue;

                matchedRp = true;
                int resolved = EffectiveState.ResolveRewindPointSlotIndexForRecording(
                    candidate, targetRec, supersedes);
                if (resolved < 0)
                    continue;

                rp = candidate;
                slotListIndex = resolved;
                return true;
            }

            if (!IsInPlaceContinuation(marker, provisional)
                && TryResolveSlotByOriginTarget(
                    marker,
                    targetRec,
                    scenario,
                    supersedes,
                    out rp,
                    out slotListIndex))
            {
                return true;
            }

            if (!hasBranchLink)
            {
                rejectReason = "noParentBp";
                return false;
            }

            rejectReason = matchedRp ? "noMatchingMarkerTargetSlot" : "noMatchingRP";
            return false;
        }

        private static bool TryResolveSlotByOriginTarget(
            ReFlySessionMarker marker,
            Recording targetRec,
            ParsekScenario scenario,
            IReadOnlyList<RecordingSupersedeRelation> supersedes,
            out RewindPoint rp,
            out int slotListIndex)
        {
            rp = null;
            slotListIndex = -1;
            if (targetRec == null
                || object.ReferenceEquals(null, scenario)
                || scenario.RewindPoints == null)
                return false;

            string markerRpId = marker?.RewindPointId;
            if (!string.IsNullOrEmpty(markerRpId))
            {
                for (int i = 0; i < scenario.RewindPoints.Count; i++)
                {
                    var candidate = scenario.RewindPoints[i];
                    if (candidate == null) continue;
                    if (!string.Equals(candidate.RewindPointId, markerRpId, StringComparison.Ordinal))
                        continue;

                    int resolved = EffectiveState.ResolveRewindPointSlotIndexForRecording(
                        candidate, targetRec, supersedes);
                    if (resolved < 0)
                        return false;

                    rp = candidate;
                    slotListIndex = resolved;
                    return true;
                }
            }

            return UnfinishedFlightClassifier.TryResolveRewindPointByOriginSlot(
                targetRec,
                scenario.RewindPoints,
                supersedes,
                out rp,
                out slotListIndex);
        }

        private static bool RequiresSlotAwareMergeClassification(Recording rec)
        {
            if (rec == null) return false;
            Recording terminalRec = EffectiveState.ResolveChainTerminalRecording(rec) ?? rec;
            TerminalState? terminal = terminalRec.TerminalStateValue;
            if (!terminal.HasValue) return false;
            if (terminal.Value == TerminalState.Orbiting
                || terminal.Value == TerminalState.SubOrbital)
                return true;
            return !string.IsNullOrEmpty(terminalRec.EvaCrewName)
                && terminal.Value != TerminalState.Boarded;
        }

        private static bool IsInPlaceContinuation(ReFlySessionMarker marker, Recording provisional)
        {
            return marker != null
                && provisional != null
                && !string.IsNullOrEmpty(marker.OriginChildRecordingId)
                && string.Equals(
                    marker.OriginChildRecordingId,
                    provisional.RecordingId,
                    StringComparison.Ordinal);
        }

        private static string DescribeTerminalForLogs(Recording rec)
        {
            if (rec == null) return "<null>";
            Recording terminalRec = EffectiveState.ResolveChainTerminalRecording(rec) ?? rec;
            return terminalRec.TerminalStateValue.HasValue
                ? terminalRec.TerminalStateValue.Value.ToString()
                : "<none>";
        }

        /// <summary>
        /// Phase 9 of Rewind-to-Staging (design §6.6 step 4 / §7.13-§7.17 /
        /// §7.41 / §7.44 / §10.4): walk <see cref="Ledger.Actions"/> and
        /// append <see cref="LedgerTombstone"/>s for every action in the
        /// supersede subtree that is v1 tombstone-eligible.
        ///
        /// <para>
        /// v1 narrow scope retires only <see cref="GameActionType.KerbalAssignment"/>
        /// +Dead actions and the <see cref="GameActionType.ReputationPenalty"/>
        /// bundled with them (see <see cref="TombstoneEligibility"/>). All
        /// other action types — contract accepts / completes / fails /
        /// cancels, milestones, facility upgrades / repairs / destruction,
        /// strategies, tech research, science spending, funds spending,
        /// vessel-destruction rep — stay in ELS even when their source
        /// recording is superseded (§7.13-§7.15, §7.44).
        /// </para>
        ///
        /// <para>
        /// Idempotent: an action that already carries a tombstone (matched by
        /// <see cref="GameAction.ActionId"/>) is skipped. Null-scoped actions
        /// (<see cref="GameAction.RecordingId"/> == null) are never tombstoned
        /// (§7.41).
        /// </para>
        ///
        /// <para>
        /// After appending, bumps
        /// <see cref="ParsekScenario.TombstoneStateVersion"/> so the ELS cache
        /// invalidates on the next <see cref="EffectiveState.ComputeELS"/>
        /// call, then asks <see cref="CrewReservationManager.RecomputeAfterTombstones"/>
        /// to re-derive the reservation dictionary — death-tombstoned kerbals
        /// return to active (§7.16).
        /// </para>
        /// </summary>
        internal static void CommitTombstones(
            ReFlySessionMarker marker,
            IReadOnlyCollection<string> subtreeIds,
            string retiringRecordingId,
            double mergeUT,
            string nowIso,
            ParsekScenario scenario)
        {
            // ReferenceEquals to bypass Unity's Object == null override (test
            // fixtures install plain-CLR ParsekScenario instances without a
            // Unity lifecycle; scenario == null would be true there even
            // though the reference is valid).
            if (ReferenceEquals(null, scenario))
            {
                ParsekLog.Warn(LedgerSwapTag, "CommitTombstones: no scenario — skipping");
                return;
            }
            if (scenario.LedgerTombstones == null)
                scenario.LedgerTombstones = new List<LedgerTombstone>();

            string originId = marker != null ? (marker.OriginChildRecordingId ?? "<no-origin>") : "<no-marker>";

            // No subtree → nothing to retire. Still log the advisory line so
            // the operational trace shows merge-time narrow-scope reinforcement.
            if (subtreeIds == null || subtreeIds.Count == 0)
            {
                ParsekLog.Info(LedgerSwapTag,
                    $"Tombstoned 0 (KerbalDeath=0, repBundled=0); 0 type-ineligible " +
                    $"(Contract=0, Milestone=0, Facility=0, Strategy=0, Tech=0, Science=0, Funds=0, RepUnbundled=0)");
                ParsekLog.Info(Tag,
                    "Narrow v1 effects: tombstoned 0 actions; career state (contracts/milestones/facilities/strategies/tech) unchanged.");
                CrewReservationManager.RecomputeAfterTombstones();
                return;
            }

            // Pre-index existing tombstones by ActionId for O(1) idempotence.
            var alreadyTombstoned = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < scenario.LedgerTombstones.Count; i++)
            {
                var t = scenario.LedgerTombstones[i];
                if (t == null || string.IsNullOrEmpty(t.ActionId)) continue;
                alreadyTombstoned.Add(t.ActionId);
            }

            // Group actions in the subtree scope by RecordingId so the rep-
            // bundling rule can scan a bounded slice per candidate. Materialize
            // the subtree into a HashSet so InSupersedeScope gets O(1) lookup
            // and the ICollection<string> contract is met.
            var subtreeSet = new HashSet<string>(subtreeIds, StringComparer.Ordinal);
            var actions = Ledger.Actions;
            var sliceByRecording = new Dictionary<string, List<GameAction>>(StringComparer.Ordinal);
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a == null) continue;
                if (!TombstoneAttributionHelper.InSupersedeScope(a, subtreeSet))
                    continue;
                List<GameAction> slice;
                if (!sliceByRecording.TryGetValue(a.RecordingId, out slice))
                {
                    slice = new List<GameAction>();
                    sliceByRecording[a.RecordingId] = slice;
                }
                slice.Add(a);
            }

            int kerbalDeathCount = 0;
            int repBundledCount = 0;
            int contractIneligible = 0;
            int milestoneIneligible = 0;
            int facilityIneligible = 0;
            int strategyIneligible = 0;
            int techIneligible = 0;
            int scienceIneligible = 0;
            int fundsIneligible = 0;
            int repUnbundledIneligible = 0;
            int otherIneligible = 0;

            foreach (var kv in sliceByRecording)
            {
                var slice = kv.Value;
                for (int i = 0; i < slice.Count; i++)
                {
                    var a = slice[i];
                    if (string.IsNullOrEmpty(a.ActionId))
                    {
                        // Shouldn't happen post-Phase-1 (every action gets an
                        // ActionId at construction / legacy hash on load) but
                        // tolerate defensively; can't tombstone without an id.
                        continue;
                    }
                    if (alreadyTombstoned.Contains(a.ActionId))
                    {
                        ParsekLog.Verbose(LedgerSwapTag,
                            $"Skip: action '{a.ActionId}' already tombstoned (idempotent re-entry)");
                        continue;
                    }

                    bool eligible = false;
                    if (TombstoneEligibility.IsEligible(a))
                    {
                        eligible = true;
                        kerbalDeathCount++;
                    }
                    else if (a.Type == GameActionType.ReputationPenalty)
                    {
                        GameAction paired;
                        if (TombstoneEligibility.TryPairBundledRepPenalty(a, slice, out paired))
                        {
                            eligible = true;
                            repBundledCount++;
                        }
                        else
                        {
                            repUnbundledIneligible++;
                        }
                    }
                    else
                    {
                        CountIneligibleByType(a.Type,
                            ref contractIneligible,
                            ref milestoneIneligible,
                            ref facilityIneligible,
                            ref strategyIneligible,
                            ref techIneligible,
                            ref scienceIneligible,
                            ref fundsIneligible,
                            ref otherIneligible);
                    }

                    if (!eligible) continue;

                    var tomb = new LedgerTombstone
                    {
                        TombstoneId = "tomb_" + Guid.NewGuid().ToString("N"),
                        ActionId = a.ActionId,
                        RetiringRecordingId = retiringRecordingId,
                        UT = mergeUT,
                        CreatedRealTime = nowIso,
                    };
                    scenario.LedgerTombstones.Add(tomb);
                    alreadyTombstoned.Add(a.ActionId);

                    ParsekLog.Verbose(LedgerSwapTag,
                        $"tomb={tomb.TombstoneId} action={a.ActionId} type={a.Type} " +
                        $"rec={a.RecordingId} ut={a.UT.ToString("R", CultureInfo.InvariantCulture)}");
                }
            }

            int tombstoned = kerbalDeathCount + repBundledCount;
            int typeIneligible = contractIneligible + milestoneIneligible + facilityIneligible
                + strategyIneligible + techIneligible + scienceIneligible + fundsIneligible
                + repUnbundledIneligible + otherIneligible;

            ParsekLog.Info(LedgerSwapTag,
                $"Tombstoned {tombstoned} (KerbalDeath={kerbalDeathCount}, repBundled={repBundledCount}); " +
                $"{typeIneligible} type-ineligible " +
                $"(Contract={contractIneligible}, Milestone={milestoneIneligible}, " +
                $"Facility={facilityIneligible}, Strategy={strategyIneligible}, Tech={techIneligible}, " +
                $"Science={scienceIneligible}, Funds={fundsIneligible}, RepUnbundled={repUnbundledIneligible})");

            // §10.4 advisory log reinforcing the narrow scope for humans reading KSP.log.
            ParsekLog.Info(Tag,
                $"Narrow v1 effects: tombstoned {tombstoned} actions; career state " +
                $"(contracts/milestones/facilities/strategies/tech) unchanged.");

            if (tombstoned > 0)
            {
                scenario.BumpTombstoneStateVersion();
            }

            // Design §6.6 step 6 / §7.16: reservation walker re-derives so
            // kerbals whose death was just tombstoned return to active.
            CrewReservationManager.RecomputeAfterTombstones();
        }

        private static void CountIneligibleByType(
            GameActionType type,
            ref int contract,
            ref int milestone,
            ref int facility,
            ref int strategy,
            ref int tech,
            ref int science,
            ref int funds,
            ref int other)
        {
            switch (type)
            {
                case GameActionType.ContractAccept:
                case GameActionType.ContractComplete:
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                    contract++;
                    break;
                case GameActionType.MilestoneAchievement:
                    milestone++;
                    break;
                case GameActionType.FacilityUpgrade:
                case GameActionType.FacilityRepair:
                case GameActionType.FacilityDestruction:
                    facility++;
                    break;
                case GameActionType.StrategyActivate:
                case GameActionType.StrategyDeactivate:
                    strategy++;
                    break;
                case GameActionType.ScienceSpending:
                    tech++;
                    break;
                case GameActionType.ScienceEarning:
                case GameActionType.ScienceInitial:
                    science++;
                    break;
                case GameActionType.FundsEarning:
                case GameActionType.FundsSpending:
                case GameActionType.FundsInitial:
                    funds++;
                    break;
                default:
                    other++;
                    break;
            }
        }

        /// <summary>
        /// Design invariant for re-fly merge supersede targets: the recording
        /// pointed at by <see cref="RecordingSupersedeRelation.NewRecordingId"/>
        /// must have at least one trajectory point AND a non-null terminal
        /// state. Returns true iff the target satisfies both clauses;
        /// otherwise <paramref name="reason"/> carries one of "null recording",
        /// "null Points", "empty Points", or "null TerminalState".
        /// </summary>
        internal static bool ValidateSupersedeTarget(Recording rec, out string reason)
        {
            if (rec == null)
            {
                reason = "null recording";
                return false;
            }
            if (rec.Points == null)
            {
                reason = "null Points";
                return false;
            }
            if (rec.Points.Count == 0)
            {
                reason = "empty Points";
                return false;
            }
            if (!rec.TerminalStateValue.HasValue)
            {
                reason = "null TerminalState";
                return false;
            }
            reason = null;
            return true;
        }

        private static bool RelationExists(
            List<RecordingSupersedeRelation> relations, string oldId, string newId)
        {
            if (relations == null || relations.Count == 0) return false;
            for (int i = 0; i < relations.Count; i++)
            {
                var rel = relations[i];
                if (rel == null) continue;
                if (string.Equals(rel.OldRecordingId, oldId, StringComparison.Ordinal)
                    && string.Equals(rel.NewRecordingId, newId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static double SafeNow()
        {
            try
            {
                return Planetarium.GetUniversalTime();
            }
            catch
            {
                // Unit tests outside Unity: Planetarium is unavailable.
                return 0.0;
            }
        }
    }
}
