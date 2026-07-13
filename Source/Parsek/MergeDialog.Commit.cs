using System.Collections.Generic;

namespace Parsek
{
    public static partial class MergeDialog
    {
        // ================================================================
        // Tree commit / discard — extracted from the dialog button lambdas
        // so that unit tests can exercise the post-button work directly
        // (the lambda itself isn't reachable from outside Unity).
        //
        // Phase C of the ledger / lump-sum reconciliation fix
        // (`docs/dev/done/plans/fix-ledger-lump-sum-reconciliation.md`) wires
        // <see cref="RecordingStore.MarkTreeAsApplied"/> into the merge path
        // so committed tree recordings are immediately marked fully applied
        // (the in-flight commit path <see cref="ParsekFlight.CommitTreeFlight"/>
        // already does the equivalent inline; this brings the merge dialog
        // into parity).
        //
        // Discard intentionally does NOT mark applied: the tree is being
        // wiped from storage, so there is no surviving caller that needs
        // its recording indexes advanced.
        // ================================================================

        /// <summary>
        /// Implements the "Merge to Timeline" branch of the dialog.
        /// <paramref name="playerRequestedSeal"/> is set ONLY by the
        /// "Merge &amp; Seal" button shown on a not-yet-sealable Re-Fly
        /// attempt; it closes the re-fly slot after the commit (same effect
        /// as the Recordings-window Seal button) by flipping the slot's
        /// effective tip to <see cref="MergeState.Immutable"/>. The
        /// "Commit (don't seal)" button leaves it <c>false</c>, so the tip
        /// stays <see cref="MergeState.CommittedProvisional"/> (open).
        /// </summary>
        internal static void MergeCommit(
            RecordingTree tree,
            Dictionary<string, bool> decisions,
            int spawnCount,
            bool playerRequestedSeal = false,
            bool refreshQuicksaveAfterCommit = true)
        {
            if (tree == null)
            {
                ParsekLog.Warn("MergeDialog", "MergeCommit: tree is null — nothing to commit");
                return;
            }

            // M1 (PR #876 round-5 review): RecordingStore.CommitPendingTree
            // operates on the global pendingTree slot. If a caller routes a
            // session-resolved tree from CommittedTrees (Bug-C dialog path),
            // CommitPendingTree would no-op on null/mismatch and the dialog
            // would disappear with nothing committed. Refuse the commit when
            // the passed tree isn't the one CommitPendingTree will act on, so
            // we never silently misbehave.
            var pendingForGuard = RecordingStore.PendingTree;
            if (pendingForGuard == null
                || !object.ReferenceEquals(pendingForGuard, tree))
            {
                ParsekLog.Warn("MergeDialog",
                    $"merge-commit-tree-mismatch " +
                    $"passedTreeId={tree.Id ?? "<null>"} " +
                    $"pendingTreeId={pendingForGuard?.Id ?? "<null>"} — " +
                    "refusing commit (CommitPendingTree would no-op or commit the wrong tree)");
                ClearPendingFlag("merge-commit-tree-mismatch refused");
                return;
            }

            var scenarioForAdoption = ParsekScenario.Instance;
            string activeReFlyTargetId =
                !object.ReferenceEquals(null, scenarioForAdoption)
                    ? scenarioForAdoption.ActiveReFlySessionMarker?.ActiveReFlyRecordingId
                    : null;
            var activeReFlyTargetHint =
                CaptureOptimizationSurvivorHint(tree, activeReFlyTargetId);

            ApplyVesselDecisions(tree, decisions);
            // Collect after ApplyVesselDecisions so candidates reflect the actual
            // post-decision snapshot state, but before optimization renames/splits/merges tips.
            var retainedParentChainTipCandidates =
                CollectRetainedParentChainTipAdoptionCandidates(
                    tree, decisions, activeReFlyTargetId);
            RecordingStore.CommitPendingTree();
            // Phase C/F: mark recordings fully applied after the tree moves
            // from pending to committed state.
            RecordingStore.MarkTreeAsApplied(tree);
            RecordingStore.RunOptimizationPass();
            AdoptExistingSourceVesselsForRetainedParentChainTips(
                tree,
                decisions,
                activeReFlyTargetId,
                retainedParentChainTipCandidates);
            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);
            CrewReservationManager.SwapReservedCrewInFlight();

            // Phase 8 of Rewind-to-Staging (design §6.6 steps 2-3): if a
            // re-fly session is active, write the supersede relations for the
            // origin subtree and flip the provisional's MergeState AFTER the
            // tree commits (so the provisional has moved from pending-tree
            // storage into the committed list) and BEFORE firing
            // OnTreeCommitted (so downstream chain evaluators see the
            // superseded subtree hidden from ERS).
            ReFlyMergeCommitResult reFlyResult =
                TryCommitReFlySupersede(activeReFlyTargetHint, playerRequestedSeal);

            // #292 + rewind-staging follow-up: refresh quicksave only after the
            // re-fly staged commit has either completed or been bypassed.
            // Interrupted re-fly commits intentionally skip the refresh so F9
            // cannot resurrect a half-committed session from a stale snapshot.
            //
            // refreshQuicksaveAfterCommit=false is passed by the silent
            // auto-commit path (ParsekScenario.AutoCommitPendingTreeOutsideFlight),
            // which runs INSIDE ParsekScenario.OnLoad: a GamePersistence.SaveGame
            // there would re-enter every ScenarioModule.OnSave mid-load (the
            // "never SaveGame from inside OnLoad" rule) and would snapshot before
            // the OnLoad ledger recalc. The commit is still durable via the next
            // normal OnSave, matching the old ghost-only auto-commit which never
            // refreshed the quicksave either. See
            // docs/dev/plans/silent-full-fidelity-autocommit.md.
            if (refreshQuicksaveAfterCommit
                && reFlyResult != ReFlyMergeCommitResult.Interrupted)
            {
                RecordingStore.RefreshQuicksaveAfterMerge(
                    "merge dialog Tree Merge", tree.Recordings.Count);
            }

            // Switch/Fly segment merge hook: when an active session is armed
            // and the commit succeeded, clear the marker (plan §"Merge and
            // Discard Scope": "On Merge, commit the pending tree normally
            // and clear the switch marker only after the commit succeeds.").
            // If a committed-tree restore attempt is also armed, clear it
            // too — the commit promoted the clone to the new committed tree.
            var switchSegmentScenario = ParsekScenario.Instance;
            if (!object.ReferenceEquals(null, switchSegmentScenario)
                && switchSegmentScenario.ActiveSwitchSegmentSession != null)
            {
                switchSegmentScenario.ClearSwitchSegmentSession("scoped-merge-success");
                if (RecordingStore.HasCommittedTreeRestoreAttempt)
                {
                    RecordingStore.ClearCommittedTreeRestoreAttempt(
                        "scoped-merge-success switch-segment");
                }
            }

            ClearPendingFlag("merge dialog commit button");
            OnTreeCommitted?.Invoke(tree);
            // M6 Record-Supply-Run helper: one-time non-blocking prompt when
            // this commit produced an eligible route candidate. After
            // OnTreeCommitted so downstream state (ghost chains) is settled;
            // internally gated (test batch, restore window, prompted-once,
            // dismissed) and never throws into the commit path.
            Logistics.RouteRunPrompt.NotifyTreeCommitted(tree);
            if (spawnCount > 0)
                ParsekLog.ScreenMessage(
                    $"Merged - {spawnCount} vessel(s) will appear after ghost playback", 3f);
            else
                ParsekLog.ScreenMessage(
                    "Merged to timeline (no surviving vessels)", 3f);
            ParsekLog.Info("MergeDialog",
                $"User chose: Tree Merge (tree='{tree.TreeName}', " +
                $"recordings={tree.Recordings.Count}, spawnable={spawnCount})");
        }

        /// <summary>
        /// Phase 8 of Rewind-to-Staging (design §6.6 steps 2-3): if a re-fly
        /// session is active at merge time, write the supersede relations for
        /// the origin subtree and flip the provisional's MergeState. Skipped
        /// silently when no session is active — the regular tree-merge flow
        /// is unchanged.
        /// </summary>
        internal static ReFlyMergeCommitResult TryCommitReFlySupersede()
            => TryCommitReFlySupersede(null, playerRequestedSeal: false);

        private static ReFlyMergeCommitResult TryCommitReFlySupersede(
            RecordingIdentityHint activeReFlyTargetHint,
            bool playerRequestedSeal)
        {
            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario))
            {
                ParsekLog.Verbose("MergeDialog",
                    "TryCommitReFlySupersede: no scenario instance — skipping re-fly commit path");
                return ReFlyMergeCommitResult.NotApplicable;
            }

            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null)
            {
                ParsekLog.Verbose("MergeDialog",
                    "TryCommitReFlySupersede: no active re-fly session marker — " +
                    "regular tree-merge flow, no supersede relations to write");
                return ReFlyMergeCommitResult.NotApplicable;
            }

            string provisionalId = marker.ActiveReFlyRecordingId;
            if (string.IsNullOrEmpty(provisionalId))
            {
                ParsekLog.Warn("MergeDialog",
                    $"TryCommitReFlySupersede: marker sess={marker.SessionId ?? "<no-id>"} " +
                    "has no ActiveReFlyRecordingId — cannot commit supersede; " +
                    "leaving marker in place for load-time sweep");
                return ReFlyMergeCommitResult.Interrupted;
            }

            Recording provisional = FindCommittedRecording(provisionalId);
            if (provisional == null)
            {
                provisional = ResolveOptimizedRecordingSurvivor(
                    activeReFlyTargetHint,
                    marker);
                if (provisional != null)
                {
                    ParsekLog.Info("MergeDialog",
                        $"TryCommitReFlySupersede: resolved optimized-away active " +
                        $"Re-Fly recording id={provisionalId} to survivor " +
                        $"id={provisional.RecordingId ?? "<no-id>"} " +
                        $"tree={provisional.TreeId ?? "<none>"} " +
                        $"chainId={provisional.ChainId ?? "<none>"} " +
                        $"chainBranch={provisional.ChainBranch} " +
                        $"sourcePid={provisional.VesselPersistentId}");
                    // Keep the marker pointed at the optimized survivor for the
                    // supersede cleanup below; MergeCommit's captured active target
                    // remains the pre-optimization id by design.
                    marker.ActiveReFlyRecordingId = provisional.RecordingId;
                    provisionalId = provisional.RecordingId;
                }
            }
            if (provisional == null)
            {
                ParsekLog.Warn("MergeDialog",
                    $"TryCommitReFlySupersede: provisional rec={provisionalId} " +
                    "not found in committed list after tree commit; " +
                    "leaving marker in place for load-time sweep");
                return ReFlyMergeCommitResult.Interrupted;
            }


            ParsekLog.Info("MergeDialog",
                $"TryCommitReFlySupersede: invoking MergeJournalOrchestrator for " +
                $"sess={marker.SessionId ?? "<no-id>"} provisional={provisionalId} " +
                $"origin={marker.OriginChildRecordingId ?? "<none>"}");

            bool ok;
            try
            {
                ok = MergeJournalOrchestrator.RunMerge(marker, provisional);
            }
            catch (System.Exception ex)
            {
                ParsekLog.Error("MergeDialog",
                    $"TryCommitReFlySupersede: orchestrator threw {ex.GetType().Name}: {ex.Message} — " +
                    $"journal will drive recovery on next load");
                ParsekLog.ScreenMessage(
                    "Merge interrupted — will finish on next load", 3f);
                return ReFlyMergeCommitResult.Interrupted;
            }

            if (!ok)
            {
                ParsekLog.Error("MergeDialog",
                    $"TryCommitReFlySupersede: orchestrator returned false for " +
                    $"sess={marker.SessionId ?? "<no-id>"} provisional={provisionalId}");
                ParsekLog.ScreenMessage("Merge commit skipped (see log)", 3f);
                return ReFlyMergeCommitResult.Interrupted;
            }

            // "Merge & Seal": the player asked to close the slot now even
            // though the outcome did not auto-seal. Reuse the same path the
            // Recordings-window Seal button uses, which flips the slot's
            // effective tip CommittedProvisional -> Immutable (the single
            // open/closed source of truth after collapse-seal-into-mergestate).
            // "Commit (don't seal)" leaves playerRequestedSeal false, so the
            // tip stays CommittedProvisional (open / re-flyable) and the slot
            // remains an Unfinished Flight. Failure to resolve the slot is
            // non-fatal — the merge already committed, so warn and point the
            // player at the manual Seal affordance. TrySeal does its own
            // persist + RP reap, a second durable pass after the merge
            // journal's; that redundancy is intentional and cheap for a
            // one-off interactive action.
            if (playerRequestedSeal)
            {
                ApplyPlayerRequestedSeal(provisional);
            }
            else
            {
                // Diagnostic: make "Commit (don't seal)" leave a positive trace
                // so a log reader can tell it apart from "Merge & Seal" (which
                // logs via ApplyPlayerRequestedSeal). Without this, only the
                // seal path was visible. The player declined to seal, but the
                // merge's terminal classification may still have auto-sealed the
                // tip to Immutable (e.g. a landed / destroyed outcome via
                // SupersedeCommit), so report the provisional's actual resulting
                // MergeState rather than assuming it stayed open — the tip
                // MergeState is the single open/closed source of truth.
                MergeState resultState = provisional != null
                    ? provisional.MergeState
                    : MergeState.NotCommitted;
                string disposition = resultState == MergeState.Immutable
                    ? "tip auto-sealed to Immutable by terminal classification"
                    : $"slot left open at {resultState}";
                ParsekLog.Info("MergeDialog",
                    $"Re-Fly merge committed WITHOUT player seal (player chose Commit-don't-seal); " +
                    $"{disposition} rec={provisional?.RecordingId ?? "<no-id>"}");
            }

            return ReFlyMergeCommitResult.Completed;
        }

        // internal (not private) so the MergeAndSealReFlyClosesSlot in-game
        // test can drive the exact Merge & Seal post-merge step.
        internal static void ApplyPlayerRequestedSeal(Recording provisional)
        {
            string recId = provisional?.RecordingId ?? "<no-id>";
            if (provisional == null)
            {
                ParsekLog.Warn("MergeDialog",
                    "Merge & Seal: resolved provisional is null after merge — " +
                    "cannot seal; player can seal from the Recordings window");
                ParsekLog.ScreenMessage(
                    "Merged, but could not seal — seal it from the Recordings window", 4f);
                return;
            }

            string sealReason;
            bool sealed_ = UnfinishedFlightSealHandler.TrySeal(provisional, out sealReason);
            if (sealed_)
            {
                ParsekLog.Info("MergeDialog",
                    $"Merge & Seal: sealed re-fly slot after merge rec={recId}");
                ParsekLog.ScreenMessage("Re-Fly slot sealed", 3f);
            }
            else
            {
                ParsekLog.Warn("MergeDialog",
                    $"Merge & Seal: seal failed after merge rec={recId} " +
                    $"reason={sealReason ?? "<none>"} — player can seal from the Recordings window");
                ParsekLog.ScreenMessage(
                    "Merged, but could not seal — seal it from the Recordings window", 4f);
            }
        }


        // Raw committed-list scan by id. Kept local to the merge path so we
        // don't need another allowlist entry; the recording we're hunting for
        // is the provisional that was added by RewindInvoker.AddProvisional
        // (NotCommitted, filtered out of ERS), so ERS routing is not the right
        // lookup.
        private static Recording FindCommittedRecording(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return null;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null) continue;
                if (string.Equals(rec.RecordingId, recordingId, System.StringComparison.Ordinal))
                    return rec;
            }
            return null;
        }

        private sealed class RecordingIdentityHint
        {
            internal string RecordingId;
            internal string TreeId;
            internal string ChainId;
            internal int ChainBranch;
            internal uint VesselPersistentId;
        }

        private static RecordingIdentityHint CaptureOptimizationSurvivorHint(
            RecordingTree tree,
            string recordingId)
        {
            if (tree == null
                || tree.Recordings == null
                || string.IsNullOrEmpty(recordingId))
            {
                return null;
            }

            Recording rec;
            if (!tree.Recordings.TryGetValue(recordingId, out rec) || rec == null)
                return null;

            return CaptureRecordingIdentityHint(rec);
        }

        private static RecordingIdentityHint CaptureRecordingIdentityHint(Recording rec)
        {
            if (rec == null)
                return null;

            return new RecordingIdentityHint
            {
                RecordingId = rec.RecordingId,
                TreeId = rec.TreeId,
                ChainId = rec.ChainId,
                ChainBranch = rec.ChainBranch,
                VesselPersistentId = rec.VesselPersistentId,
            };
        }

        private static Recording ResolveOptimizedRecordingSurvivor(
            RecordingIdentityHint hint,
            ReFlySessionMarker marker)
        {
            if (hint == null)
                return null;

            Recording exact = FindCommittedRecording(hint.RecordingId);
            if (exact != null)
                return exact;

            Recording origin = marker != null
                ? FindCommittedRecording(marker.OriginChildRecordingId)
                : null;
            if (IsOptimizationSurvivorForHint(origin, hint))
                return origin;

            Recording supersedeTarget = marker != null
                ? FindCommittedRecording(marker.SupersedeTargetId)
                : null;
            if (IsOptimizationSurvivorForHint(supersedeTarget, hint))
                return supersedeTarget;

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null
                || string.IsNullOrEmpty(hint.ChainId)
                || hint.VesselPersistentId == 0u)
            {
                return null;
            }

            Recording best = null;
            for (int i = 0; i < committed.Count; i++)
            {
                Recording candidate = committed[i];
                if (candidate == null || string.IsNullOrEmpty(candidate.RecordingId))
                    continue;
                if (!IsOptimizationSurvivorForHint(candidate, hint))
                    continue;

                if (best == null || candidate.ChainIndex < best.ChainIndex)
                    best = candidate;
            }

            return best;
        }

        private static bool IsOptimizationSurvivorForHint(
            Recording candidate,
            RecordingIdentityHint hint)
        {
            return IsRecordingIdentityMatch(candidate, hint, allowExactRecordingId: false);
        }

        private static bool IsRecordingIdentityMatch(
            Recording candidate,
            RecordingIdentityHint hint,
            bool allowExactRecordingId)
        {
            if (candidate == null || hint == null)
                return false;
            if (allowExactRecordingId
                && !string.IsNullOrEmpty(hint.RecordingId)
                && string.Equals(
                    hint.RecordingId,
                    candidate.RecordingId,
                    System.StringComparison.Ordinal))
            {
                return true;
            }
            if (string.IsNullOrEmpty(candidate.RecordingId))
                return false;
            if (string.IsNullOrEmpty(hint.ChainId)
                || string.IsNullOrEmpty(candidate.ChainId)
                || !string.Equals(
                    candidate.ChainId,
                    hint.ChainId,
                    System.StringComparison.Ordinal))
            {
                return false;
            }
            if (candidate.ChainBranch != hint.ChainBranch)
                return false;
            if (hint.VesselPersistentId == 0u
                || candidate.VesselPersistentId == 0u
                || candidate.VesselPersistentId != hint.VesselPersistentId)
            {
                return false;
            }
            if (!string.IsNullOrEmpty(hint.TreeId)
                && !string.Equals(
                    candidate.TreeId,
                    hint.TreeId,
                    System.StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        // ================================================================
        // Per-vessel persist/ghost-only decisions
        // ================================================================

        /// <summary>
        /// Determines whether a recording can actually spawn as a real vessel after merge.
        /// This reuses the same intrinsic spawn policy as timeline playback so the dialog's
        /// default "spawnable" count stays aligned with runtime behavior.
        /// </summary>
        internal static bool CanPersistVessel(Recording rec, RecordingTree treeContext = null)
        {
            if (rec == null)
                return false;

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec,
                isActiveChainMember: false,
                isChainLooping: false,
                treeContext: treeContext);
            return needsSpawn;
        }

        /// <summary>
        /// Builds default persist/ghost-only decisions for all leaf recordings in a tree.
        /// Surviving vessels default to persist (true), destroyed/recovered default to ghost-only (false).
        /// Keys are RecordingId. Pure static for testability.
        /// </summary>
        internal static Dictionary<string, bool> BuildDefaultVesselDecisions(RecordingTree tree)
            => BuildDefaultVesselDecisions(tree, null, null);

        /// <summary>
        /// Builds default persist/ghost-only decisions for a tree, additionally
        /// forcing every non-leaf recording listed in
        /// <paramref name="suppressedRecordingIds"/> to ghost-only — except the
        /// recording identified by <paramref name="activeReFlyTargetId"/>, which
        /// is the live Re-Fly target the player is currently flying and must
        /// stay spawnable.
        ///
        /// <para>
        /// Bug fix (refly-suppressed-non-leaf): without this branch a parent
        /// recording in the suppressed subtree (e.g. an upper stage that has
        /// child decoupling/breakup branches) keeps <c>VesselSnapshot</c> set,
        /// and <c>GhostPlaybackLogic.ShouldSpawnAtRecordingEnd</c> later spawns
        /// it as a clickable real vessel alongside the playback ghost. The
        /// only branches the leaf walk reaches are chain tips, so non-leaf
        /// suppressed recordings would otherwise be silently retained.
        /// </para>
        /// </summary>
        internal static Dictionary<string, bool> BuildDefaultVesselDecisions(
            RecordingTree tree,
            HashSet<string> suppressedRecordingIds,
            string activeReFlyTargetId)
        {
            var decisions = new Dictionary<string, bool>();
            if (tree == null)
                return decisions;

            var leaves = tree.GetAllLeaves();
            for (int i = 0; i < leaves.Count; i++)
            {
                var leaf = leaves[i];
                bool canPersist = CanPersistVessel(leaf, tree);
                decisions[leaf.RecordingId] = canPersist;
                ParsekLog.Verbose("MergeDialog",
                    $"BuildDefaultVesselDecisions: leaf='{leaf.RecordingId}' vessel='{leaf.VesselName}' " +
                    $"terminal={leaf.TerminalStateValue?.ToString() ?? "null"} " +
                    $"hasSnapshot={leaf.VesselSnapshot != null} canPersist={canPersist}");
            }

            // Bug #271: in always-tree mode with breakup-continuous design, the active
            // recording may be non-leaf (it has ChildBranchPointId from breakup branches)
            // but is still the main vessel's recording that should be spawnable. Include
            // it in decisions if it wasn't already covered as a leaf.
            if (!string.IsNullOrEmpty(tree.ActiveRecordingId)
                && !decisions.ContainsKey(tree.ActiveRecordingId))
            {
                Recording activeRec;
                if (tree.Recordings.TryGetValue(tree.ActiveRecordingId, out activeRec))
                {
                    bool canPersist = CanPersistVessel(activeRec, tree);
                    decisions[activeRec.RecordingId] = canPersist;
                    ParsekLog.Verbose("MergeDialog",
                        $"BuildDefaultVesselDecisions: active-nonleaf='{activeRec.RecordingId}' " +
                        $"vessel='{activeRec.VesselName}' " +
                        $"terminal={activeRec.TerminalStateValue?.ToString() ?? "null"} " +
                        $"hasSnapshot={activeRec.VesselSnapshot != null} canPersist={canPersist}");
                }
            }

            // Re-Fly suppressed-subtree pass: for every recording whose id appears
            // in the suppression closure, force ghost-only — even if it is a
            // non-leaf the leaf walk above never visited. The sole exception is
            // the live Re-Fly target itself, which must stay spawnable. Note we
            // walk the closure ids (not tree.Recordings) because the closure is
            // computed against committed recordings and may name records that
            // are not in the pending tree (chain siblings, etc.); we only act
            // when the id ALSO exists in this tree.
            if (suppressedRecordingIds != null && suppressedRecordingIds.Count > 0)
            {
                int forced = 0;
                int skippedActiveTarget = 0;
                int alreadyGhostOnly = 0;
                int notInTree = 0;
                foreach (string suppressedId in suppressedRecordingIds)
                {
                    if (string.IsNullOrEmpty(suppressedId)) continue;
                    if (!string.IsNullOrEmpty(activeReFlyTargetId)
                        && string.Equals(suppressedId, activeReFlyTargetId, System.StringComparison.Ordinal))
                    {
                        skippedActiveTarget++;
                        ParsekLog.Verbose("MergeDialog",
                            $"BuildDefaultVesselDecisions: keeping active Re-Fly target spawnable " +
                            $"id='{suppressedId}' (in suppressed subtree but is the live target)");
                        continue;
                    }
                    Recording rec;
                    if (!tree.Recordings.TryGetValue(suppressedId, out rec) || rec == null)
                    {
                        notInTree++;
                        continue;
                    }
                    bool priorPersistDecision;
                    if (decisions.TryGetValue(suppressedId, out priorPersistDecision) && !priorPersistDecision)
                    {
                        alreadyGhostOnly++;
                        continue;
                    }
                    decisions[suppressedId] = false;
                    forced++;
                    ParsekLog.Info("MergeDialog",
                        $"BuildDefaultVesselDecisions: forcing ghost-only on suppressed " +
                        $"id='{suppressedId}' vessel='{rec.VesselName}' " +
                        $"terminal={rec.TerminalStateValue?.ToString() ?? "null"} " +
                        $"isLeaf={rec.ChildBranchPointId == null} " +
                        $"priorDecision={(decisions.ContainsKey(suppressedId) ? "set" : "unset")}");
                }
                ParsekLog.Info("MergeDialog",
                    $"BuildDefaultVesselDecisions: suppressed-subtree pass complete " +
                    $"closureSize={suppressedRecordingIds.Count} forcedGhostOnly={forced} " +
                    $"skippedActiveTarget={skippedActiveTarget} alreadyGhostOnly={alreadyGhostOnly} " +
                    $"notInTree={notInTree} activeTarget='{activeReFlyTargetId ?? "<none>"}'");
            }

            ApplyActiveReFlyParentChainDefaults(
                tree,
                decisions,
                activeReFlyTargetId,
                suppressedRecordingIds);

            return decisions;
        }


        private static void ApplyActiveReFlyParentChainDefaults(
            RecordingTree tree,
            Dictionary<string, bool> decisions,
            string activeReFlyTargetId,
            HashSet<string> suppressedRecordingIds)
        {
            // This runs only while BuildDefaultVesselDecisions is constructing
            // the dialog's initial defaults. Keep this path decision-only:
            // MergeCommit stamps spawned state by adopting already-materialized
            // source vessels only after the player actually accepts the merge.
            // Parent-chain terminal tips can be stale old-future cleanup, but
            // they can also be legitimate future materialized vessels. Keep the
            // dialog aligned with the normal runtime spawn predicate unless the
            // tip is explicitly suppressed.
            if (tree == null || decisions == null || string.IsNullOrEmpty(activeReFlyTargetId))
                return;

            var parentTips = CollectActiveReFlyParentChainTerminalTipIds(
                tree, activeReFlyTargetId);
            if (parentTips == null || parentTips.Count == 0)
                return;

            int forced = 0;
            int retainedSpawnable = 0;
            int alreadyGhostOnly = 0;
            int missing = 0;

            foreach (string tipId in parentTips)
            {
                if (string.IsNullOrEmpty(tipId)) continue;

                Recording rec;
                if (!tree.Recordings.TryGetValue(tipId, out rec) || rec == null)
                {
                    missing++;
                    continue;
                }

                bool priorDecision;
                bool hadPriorDecision = decisions.TryGetValue(tipId, out priorDecision);
                if (hadPriorDecision && !priorDecision)
                {
                    alreadyGhostOnly++;
                    continue;
                }

                bool explicitlySuppressed = suppressedRecordingIds != null
                    && suppressedRecordingIds.Contains(tipId)
                    && !string.Equals(
                        tipId,
                        activeReFlyTargetId,
                        System.StringComparison.Ordinal);
                bool canPersist = hadPriorDecision
                    ? priorDecision
                    : CanPersistVessel(rec, tree);

                if (canPersist && !explicitlySuppressed)
                {
                    decisions[tipId] = true;
                    retainedSpawnable++;
                    ParsekLog.Info("MergeDialog",
                        $"BuildDefaultVesselDecisions: retaining active Re-Fly parent-chain " +
                        $"terminal tip spawnable id='{tipId}' vessel='{rec.VesselName}' " +
                        $"terminal={rec.TerminalStateValue?.ToString() ?? "null"} " +
                        $"hasSnapshot={rec.VesselSnapshot != null} " +
                        $"priorDecision={(hadPriorDecision ? "set" : "unset")} " +
                        $"reason=normal-spawn-policy activeTarget='{activeReFlyTargetId}'");
                    continue;
                }

                decisions[tipId] = false;
                forced++;
                ParsekLog.Info("MergeDialog",
                    $"BuildDefaultVesselDecisions: defaulting active Re-Fly parent-chain " +
                    $"terminal tip to ghost-only id='{tipId}' vessel='{rec.VesselName}' " +
                    $"terminal={rec.TerminalStateValue?.ToString() ?? "null"} " +
                    $"hasSnapshot={rec.VesselSnapshot != null} " +
                    $"priorDecision={(hadPriorDecision ? "set" : "unset")} " +
                    $"canPersist={canPersist} explicitlySuppressed={explicitlySuppressed} " +
                    $"activeTarget='{activeReFlyTargetId}'");
            }

            ParsekLog.Info("MergeDialog",
                $"BuildDefaultVesselDecisions: active Re-Fly parent-chain pass complete " +
                $"candidates={parentTips.Count} forcedGhostOnly={forced} " +
                $"retainedSpawnable={retainedSpawnable} " +
                $"alreadyGhostOnly={alreadyGhostOnly} missing={missing} " +
                $"activeTarget='{activeReFlyTargetId}'");
        }

        internal static int AdoptExistingSourceVesselsForRetainedParentChainTips(
            RecordingTree tree,
            Dictionary<string, bool> decisions,
            string activeReFlyTargetId)
        {
            var retainedParentChainTipCandidates =
                CollectRetainedParentChainTipAdoptionCandidates(
                    tree, decisions, activeReFlyTargetId);
            return AdoptExistingSourceVesselsForRetainedParentChainTips(
                tree,
                decisions,
                activeReFlyTargetId,
                retainedParentChainTipCandidates);
        }

        private static int AdoptExistingSourceVesselsForRetainedParentChainTips(
            RecordingTree tree,
            Dictionary<string, bool> decisions,
            string activeReFlyTargetId,
            List<RecordingIdentityHint> retainedParentChainTipCandidates)
        {
            if (tree == null || decisions == null || string.IsNullOrEmpty(activeReFlyTargetId))
                return 0;

            var currentTipRecords = CollectCurrentParentChainTipAdoptionRecords(
                tree,
                activeReFlyTargetId,
                retainedParentChainTipCandidates);
            if (currentTipRecords == null || currentTipRecords.Count == 0)
                return 0;

            int checkedSpawnable = 0;
            int adoptedExistingSource = 0;
            int skippedNotSpawnable = 0;
            int missing = 0;

            for (int i = 0; i < currentTipRecords.Count; i++)
            {
                Recording rec = currentTipRecords[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                {
                    missing++;
                    continue;
                }
                string tipId = rec.RecordingId;

                bool retainedByPreOptimizationTip =
                    IsRetainedParentChainTipAdoptionCandidate(
                        rec,
                        retainedParentChainTipCandidates);
                if (retainedByPreOptimizationTip)
                {
                    if (!CanPersistVessel(rec, tree))
                    {
                        skippedNotSpawnable++;
                        continue;
                    }
                }
                else
                {
                    bool persist;
                    bool hadDecision = TryResolvePersistDecisionForOptimizedTip(
                        tree, decisions, rec, out persist);
                    if (hadDecision && !persist)
                    {
                        skippedNotSpawnable++;
                        continue;
                    }

                    if (!hadDecision && !CanPersistVessel(rec, tree))
                    {
                        skippedNotSpawnable++;
                        continue;
                    }
                }

                checkedSpawnable++;
                if (VesselSpawner.TryAdoptExistingSourceVesselForSpawn(
                    rec,
                    "MergeDialog",
                    $"MergeCommit parent-chain tip '{tipId}'"))
                {
                    adoptedExistingSource++;
                }
            }

            ParsekLog.Info("MergeDialog",
                $"MergeCommit: active Re-Fly parent-chain adoption pass complete " +
                $"candidates={currentTipRecords.Count} checkedSpawnable={checkedSpawnable} " +
                $"adoptedExistingSource={adoptedExistingSource} " +
                $"skippedNotSpawnable={skippedNotSpawnable} missing={missing} " +
                $"retainedPreOptimizationTips={retainedParentChainTipCandidates?.Count ?? 0} " +
                $"activeTarget='{activeReFlyTargetId}'");
            return adoptedExistingSource;
        }

        private static List<Recording> CollectCurrentParentChainTipAdoptionRecords(
            RecordingTree tree,
            string activeReFlyTargetId,
            List<RecordingIdentityHint> retainedParentChainTipCandidates)
        {
            var result = new List<Recording>();
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            if (tree == null || tree.Recordings == null)
                return result;

            if (retainedParentChainTipCandidates != null
                && retainedParentChainTipCandidates.Count > 0)
            {
                foreach (Recording rec in tree.Recordings.Values)
                {
                    AddCurrentParentChainTipIfMatch(
                        result,
                        seen,
                        rec,
                        IsRetainedParentChainTipAdoptionCandidate(
                            rec,
                            retainedParentChainTipCandidates));
                }
                return result;
            }

            var parentTips = CollectActiveReFlyParentChainTerminalTipIds(
                tree, activeReFlyTargetId);
            if (parentTips == null || parentTips.Count == 0)
                return result;

            foreach (string tipId in parentTips)
            {
                if (string.IsNullOrEmpty(tipId))
                    continue;
                Recording rec;
                if (!tree.Recordings.TryGetValue(tipId, out rec))
                    continue;
                AddCurrentParentChainTipIfMatch(result, seen, rec, rec != null);
            }

            return result;
        }

        private static void AddCurrentParentChainTipIfMatch(
            List<Recording> result,
            HashSet<string> seen,
            Recording rec,
            bool matched)
        {
            if (!matched
                || result == null
                || seen == null
                || rec == null
                || string.IsNullOrEmpty(rec.RecordingId))
            {
                return;
            }
            if (!seen.Add(rec.RecordingId))
                return;
            result.Add(rec);
        }

        private static List<RecordingIdentityHint> CollectRetainedParentChainTipAdoptionCandidates(
            RecordingTree tree,
            Dictionary<string, bool> decisions,
            string activeReFlyTargetId)
        {
            var result = new List<RecordingIdentityHint>();
            if (tree == null || decisions == null || string.IsNullOrEmpty(activeReFlyTargetId))
                return result;

            var parentTips = CollectActiveReFlyParentChainTerminalTipIds(
                tree, activeReFlyTargetId);
            if (parentTips == null || parentTips.Count == 0)
                return result;

            foreach (string tipId in parentTips)
            {
                if (string.IsNullOrEmpty(tipId)) continue;

                Recording rec;
                if (!tree.Recordings.TryGetValue(tipId, out rec) || rec == null)
                    continue;

                bool persist;
                bool hadDecision = TryResolvePersistDecisionForOptimizedTip(
                    tree, decisions, rec, out persist);
                if (hadDecision && !persist)
                    continue;
                if (!CanPersistVessel(rec, tree))
                    continue;

                var hint = CaptureRecordingIdentityHint(rec);
                if (hint != null)
                    result.Add(hint);
            }

            return result;
        }

        private static bool IsRetainedParentChainTipAdoptionCandidate(
            Recording rec,
            List<RecordingIdentityHint> candidates)
        {
            if (rec == null || candidates == null || candidates.Count == 0)
                return false;

            for (int i = 0; i < candidates.Count; i++)
            {
                RecordingIdentityHint candidate = candidates[i];
                if (candidate == null)
                    continue;

                if (IsRecordingIdentityMatch(rec, candidate, allowExactRecordingId: true))
                    return true;
            }

            return false;
        }

        internal static bool TryResolvePersistDecisionForOptimizedTip(
            RecordingTree tree,
            Dictionary<string, bool> decisions,
            Recording rec,
            out bool persist)
        {
            persist = false;
            if (rec == null || decisions == null)
                return false;

            if (!string.IsNullOrEmpty(rec.RecordingId)
                && decisions.TryGetValue(rec.RecordingId, out persist))
            {
                return true;
            }

            if (tree == null
                || tree.Recordings == null
                || string.IsNullOrEmpty(rec.ChainId))
            {
                return false;
            }

            Recording best = null;
            bool bestDecision = false;
            foreach (var candidate in tree.Recordings.Values)
            {
                if (candidate == null || string.IsNullOrEmpty(candidate.RecordingId))
                    continue;
                bool candidateDecision;
                if (!decisions.TryGetValue(candidate.RecordingId, out candidateDecision))
                    continue;
                if (!string.Equals(candidate.ChainId, rec.ChainId, System.StringComparison.Ordinal))
                    continue;
                if (candidate.ChainBranch != rec.ChainBranch)
                    continue;
                if (!string.IsNullOrEmpty(rec.TreeId)
                    && !string.Equals(candidate.TreeId, rec.TreeId, System.StringComparison.Ordinal))
                    continue;
                if (candidate.ChainIndex > rec.ChainIndex)
                    continue;
                if (best == null || candidate.ChainIndex > best.ChainIndex)
                {
                    best = candidate;
                    bestDecision = candidateDecision;
                }
            }

            if (best == null)
                return false;

            persist = bestDecision;
            return true;
        }

        internal static HashSet<string> CollectActiveReFlyParentChainTerminalTipIds(
            RecordingTree tree,
            string activeReFlyTargetId)
        {
            var result = new HashSet<string>(System.StringComparer.Ordinal);
            if (tree == null || tree.Recordings == null || string.IsNullOrEmpty(activeReFlyTargetId))
                return result;

            Recording activeRec;
            if (!tree.Recordings.TryGetValue(activeReFlyTargetId, out activeRec) || activeRec == null)
                return result;

            HashSet<string> activeChainIds = CollectSameChainRecordingIds(tree, activeRec);
            var parentBranchPointIds = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (string activeChainId in activeChainIds)
            {
                Recording chainMember;
                if (string.IsNullOrEmpty(activeChainId)
                    || !tree.Recordings.TryGetValue(activeChainId, out chainMember)
                    || chainMember == null)
                    continue;
                if (!string.IsNullOrEmpty(chainMember.ParentBranchPointId))
                    parentBranchPointIds.Add(chainMember.ParentBranchPointId);
            }

            foreach (string parentBpId in parentBranchPointIds)
            {
                BranchPoint bp = FindBranchPoint(tree, parentBpId);
                if (bp == null)
                {
                    ParsekLog.Verbose("MergeDialog",
                        $"CollectActiveReFlyParentChainTerminalTipIds: parent bp '{parentBpId}' missing " +
                        $"for activeTarget='{activeReFlyTargetId}'");
                    continue;
                }

                if (bp.ParentRecordingIds == null || bp.ParentRecordingIds.Count != 1)
                {
                    int parentCount = bp.ParentRecordingIds != null ? bp.ParentRecordingIds.Count : 0;
                    ParsekLog.Verbose("MergeDialog",
                        $"CollectActiveReFlyParentChainTerminalTipIds: skip bp={bp.Id} " +
                        $"parentCount={parentCount} activeTarget='{activeReFlyTargetId}' " +
                        "(not a single-parent old-future branch)");
                    continue;
                }

                string parentId = bp.ParentRecordingIds[0];
                if (string.IsNullOrEmpty(parentId) || activeChainIds.Contains(parentId))
                    continue;

                Recording parentRec;
                if (!tree.Recordings.TryGetValue(parentId, out parentRec) || parentRec == null)
                    continue;

                Recording terminalRec = EffectiveState.ResolveChainTerminalRecording(parentRec, tree);
                if (terminalRec == null || string.IsNullOrEmpty(terminalRec.RecordingId))
                    continue;

                if (activeChainIds.Contains(terminalRec.RecordingId)
                    || string.Equals(terminalRec.RecordingId, activeReFlyTargetId,
                        System.StringComparison.Ordinal))
                {
                    continue;
                }

                if (terminalRec.VesselPersistentId != 0u
                    && activeRec.VesselPersistentId != 0u
                    && terminalRec.VesselPersistentId == activeRec.VesselPersistentId)
                {
                    ParsekLog.Verbose("MergeDialog",
                        $"CollectActiveReFlyParentChainTerminalTipIds: skip parent terminal " +
                        $"id='{terminalRec.RecordingId}' because it shares vessel pid " +
                        $"{terminalRec.VesselPersistentId} with activeTarget='{activeReFlyTargetId}'");
                    continue;
                }

                if (!IsTerminalLinkedToParentBranch(bp, parentRec, terminalRec))
                {
                    ParsekLog.Verbose("MergeDialog",
                        $"CollectActiveReFlyParentChainTerminalTipIds: skip parent terminal " +
                        $"id='{terminalRec.RecordingId}' for bp={bp.Id}; terminal is not linked " +
                        "to the active target's parent branch");
                    continue;
                }

                result.Add(terminalRec.RecordingId);
            }

            return result;
        }

        private static HashSet<string> CollectSameChainRecordingIds(
            RecordingTree tree,
            Recording rec)
        {
            var result = new HashSet<string>(System.StringComparer.Ordinal);
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return result;

            result.Add(rec.RecordingId);
            if (tree == null || tree.Recordings == null || string.IsNullOrEmpty(rec.ChainId))
                return result;

            foreach (var cand in tree.Recordings.Values)
            {
                if (cand == null || string.IsNullOrEmpty(cand.RecordingId)) continue;
                if (!string.Equals(cand.ChainId, rec.ChainId, System.StringComparison.Ordinal)) continue;
                if (cand.ChainBranch != rec.ChainBranch) continue;
                if (!string.IsNullOrEmpty(rec.TreeId)
                    && !string.Equals(cand.TreeId, rec.TreeId, System.StringComparison.Ordinal))
                    continue;
                result.Add(cand.RecordingId);
            }

            return result;
        }

        private static bool IsTerminalLinkedToParentBranch(
            BranchPoint bp,
            Recording parentRec,
            Recording terminalRec)
        {
            if (bp == null || parentRec == null || terminalRec == null)
                return false;
            if (object.ReferenceEquals(parentRec, terminalRec))
                return true;
            if (string.Equals(parentRec.ChildBranchPointId, bp.Id, System.StringComparison.Ordinal))
                return true;
            if (string.Equals(terminalRec.ChildBranchPointId, bp.Id, System.StringComparison.Ordinal))
                return true;
            if (bp.ParentRecordingIds != null)
            {
                for (int i = 0; i < bp.ParentRecordingIds.Count; i++)
                {
                    if (string.Equals(bp.ParentRecordingIds[i], terminalRec.RecordingId,
                        System.StringComparison.Ordinal))
                        return true;
                }
            }
            if (!string.IsNullOrEmpty(parentRec.ChainId)
                && string.Equals(parentRec.ChainId, terminalRec.ChainId, System.StringComparison.Ordinal)
                && parentRec.ChainBranch == terminalRec.ChainBranch
                && terminalRec.ChainIndex >= parentRec.ChainIndex
                && (string.IsNullOrEmpty(parentRec.TreeId)
                    || string.Equals(parentRec.TreeId, terminalRec.TreeId, System.StringComparison.Ordinal)))
            {
                return true;
            }
            return false;
        }

        private static BranchPoint FindBranchPoint(RecordingTree tree, string branchPointId)
        {
            if (tree == null || tree.BranchPoints == null || string.IsNullOrEmpty(branchPointId))
                return null;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                var bp = tree.BranchPoints[i];
                if (bp != null && string.Equals(bp.Id, branchPointId, System.StringComparison.Ordinal))
                    return bp;
            }
            return null;
        }

        /// <summary>
        /// Applies vessel decisions to the tree: nulls VesselSnapshot on recordings
        /// that are marked ghost-only (false in decisions dict).
        /// </summary>
        internal static void ApplyVesselDecisions(RecordingTree tree, Dictionary<string, bool> decisions)
        {
            if (tree == null || decisions == null)
                return;

            foreach (var kvp in decisions)
            {
                if (!kvp.Value) // ghost-only
                {
                    Recording rec;
                    if (tree.Recordings.TryGetValue(kvp.Key, out rec))
                    {
                        if (rec.VesselSnapshot != null)
                        {
                            // Preserve GhostVisualSnapshot for ghost rendering if not already set
                            if (rec.GhostVisualSnapshot == null)
                                rec.GhostVisualSnapshot = rec.VesselSnapshot.CreateCopy();
                            CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                            rec.VesselSnapshot = null;
                            ParsekLog.Info("MergeDialog",
                                $"ApplyVesselDecisions: ghost-only for '{rec.VesselName}' (id={kvp.Key}), " +
                                $"spawn snapshot nulled, ghostVisual={rec.GhostVisualSnapshot != null}");
                        }
                    }
                }
            }
        }
    }
}
