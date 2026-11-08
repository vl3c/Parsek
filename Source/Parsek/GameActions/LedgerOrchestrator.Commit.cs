using System;
using System.Collections.Generic;
using System.Globalization;
using KscActionExpectation = Parsek.KscActionExpectationClassifier.KscActionExpectation;

namespace Parsek
{
    internal static partial class LedgerOrchestrator
    {
        private struct RecordingScienceWindow
        {
            public string RecordingId;
            public string ChainId;
            public double StartUT;
            public double EndUT;
        }

        /// <param name="repSeedOriginOverride">
        /// The <see cref="ReputationSeedOrigin"/> already established for the surrounding
        /// commit BATCH, or null to establish it here. A whole tree is one commit as far
        /// as the reputation seed is concerned: the seed is captured off the live pool
        /// once, at the first recording of the batch, and that pool has already taken
        /// every death the batch is about to file. Establishing it per recording instead
        /// would let the batch's FIRST recording create the seed and then read
        /// <see cref="ReputationSeedOrigin.PreExisting"/> for a death filed by a LATER
        /// recording of the same batch, which is the double-subtraction this parameter
        /// exists to prevent. <see cref="NotifyLedgerTreeCommitted"/> passes it; the
        /// single-recording commit paths pass null.
        /// <para>
        /// ONE OVERRIDE VALUE IS NOT CARRIED FORWARD:
        /// <see cref="ReputationSeedOrigin.NotYetCaptured"/> describes no seed at all -
        /// it is the batch's PREDICTION that one will be captured later off a live pool.
        /// A recording earlier in the same batch falsifies that prediction whenever the
        /// seed gets created before this recording files: in a tree commit (one walk after
        /// every recording is filed) by a later recording's own seed ensure, on the
        /// single-recording path by its step-6 recalc
        /// (<see cref="SeedInitialResourceBalances"/> -> EnsureInitialReputationSeed),
        /// and <see cref="RestampInsideSeedRowsAgainstCareerStartSeed"/> can only flip
        /// rows that already exist, so a later recording stamped from the stale
        /// prediction would never be repaired. A null or NotYetCaptured override is
        /// therefore RE-ESTABLISHED per recording at step 3c-post; every definite value
        /// is used exactly as passed.
        /// </para>
        /// </param>
        internal static void OnRecordingCommitted(
            string recordingId, double startUT, double endUT,
            IReadOnlyList<PendingScienceSubject> pendingScienceOverride,
            ref bool scienceActionsAddedToLedger,
            ReputationSeedOrigin? repSeedOriginOverride = null)
        {
            var filed = FileRecordingCommitRows(
                recordingId, startUT, endUT, pendingScienceOverride,
                ref scienceActionsAddedToLedger, repSeedOriginOverride);

            // 6. Recalculate + patch. Must run BEFORE ReconcileEarningsWindow so the
            // walk populates derived fields (Transformed* / EffectiveRep / EffectiveScience)
            // that the reconciliation switch now reads. See #440B. Cut at the live clock
            // when committed rows lie ahead of it (TREE-COMMIT-WALKS-PAST-THE-CLOCK).
            RecalculateAndPatchAfterCommit(RecordingCommitRecalcReason);

            CompleteRecordingCommitAfterWalk(filed);
        }

        /// <summary>
        /// What <see cref="FileRecordingCommitRows"/> filed for one recording, carried to
        /// the post-walk reconciliation (<see cref="CompleteRecordingCommitAfterWalk"/>).
        /// </summary>
        internal struct FiledRecordingCommit
        {
            public string RecordingId;
            public double StartUT;
            public double EndUT;
            public IReadOnlyList<GameStateEvent> Events;
            public List<GameAction> Actions;
        }

        /// <summary>
        /// Steps 1-5 of a recording commit: convert the recording's captured events,
        /// pending science, vessel cost, crew and death-penalty rows, dedup them and add
        /// them to the ledger. Runs NO ledger walk: the caller walks once
        /// (<see cref="RecalculateAndPatch"/>) and then calls
        /// <see cref="CompleteRecordingCommitAfterWalk"/>. A tree commit files every
        /// recording first and walks once (OPTIMIZER-SPLIT-RETAG-COMMIT-WALK-TRANSIENT-CLAMP):
        /// a walk between two recordings of one batch sees the batch partly filed, while
        /// the live pools already hold all of it.
        /// </summary>
        private static FiledRecordingCommit FileRecordingCommitRows(
            string recordingId, double startUT, double endUT,
            IReadOnlyList<PendingScienceSubject> pendingScienceOverride,
            ref bool scienceActionsAddedToLedger,
            ReputationSeedOrigin? repSeedOriginOverride)
        {
            // Test-only fault injection (see OnRecordingCommittedFaultInjector doc).
            OnRecordingCommittedFaultInjector?.Invoke(recordingId);

            Initialize();

            // A committed recording no longer waits on the idle-on-pad check, whichever
            // recovery-payout branch the cost rows below take.
            InFlightRecoveryRequest.ClearLabRecoveryMarks(new[] { recordingId }, "commit");

            // What the build step reads, prepared on the COMMITTED recording and the
            // career: the recording's crew end states are populated in place (they are
            // persisted with it), and the reputation seed is ensured before any row of this
            // commit exists, so the death-penalty producer knows where the seed came from
            // and the seed's refusal branch cannot see this commit's own fresh rows.
            var rec = FindRecordingById(recordingId);
            if (NeedsCrewEndStatePopulation(rec))
                KerbalsModule.PopulateCrewEndStates(rec);
            ReputationSeedOrigin repSeedOrigin =
                ResolveCommitReputationSeedOrigin(recordingId, repSeedOriginOverride);

            // Steps 1-3e: the pure row build (design section 2.12 Rule 5). The merge
            // dialog's dominance check runs the same build over a pending tree.
            IReadOnlyList<PendingScienceSubject> pendingSource =
                pendingScienceOverride ?? GameStateRecorder.PendingScienceSubjects;
            var events = GameStateStore.Events;
            RecordingCommitRowBuild build = BuildRecordingCommitRows(
                rec, recordingId, startUT, endUT, events, pendingSource, Ledger.Actions,
                repSeedOrigin);

            // The build's two deferred writes: the rollout row it found is adopted onto
            // this recording, and the pending stock-payout mark it read is consumed.
            ApplyRecordingCommitBuildClaims(build, startUT);

            // 4. Deduplicate: remove actions already in the ledger from KSC real-time writes.
            // KSC events (tech, facility, hire, milestone) written via OnKscSpending may overlap
            // with the recording's time range. Compare by type + UT + key to avoid double-adding.
            var actions = build.Actions;
            var scienceActions = build.ScienceActions;
            int beforeDedup = actions.Count;
            actions = DeduplicateAgainstLedger(actions);
            int dedupRemoved = beforeDedup - actions.Count;

            // 5. Add to ledger
            Ledger.AddActions(actions, LedgerRowWriter.StockApplied);
            if (scienceActions.Count > 0)
            {
                // Mirror only after all pre-ledger work succeeded and the resulting
                // ScienceEarning actions are safely in the ledger. This keeps the
                // committed-subject cache aligned with the real persistence point.
                GameStateStore.CommitScienceActions(scienceActions);
                scienceActionsAddedToLedger = true;
            }

            // Test-only post-persist fault injection. Fires after both ledger add and
            // committed-science mirroring succeeded, but before the caller can mark the
            // overall commit as complete.
            OnRecordingCommittedPostSciencePersistFaultInjector?.Invoke(recordingId);

            ParsekLog.Info(Tag,
                $"Committed recording '{recordingId}': {actions.Count} actions added to ledger " +
                $"(events={events.Count}, science={scienceActions.Count}, cost={build.CostCount}, " +
                $"kerbals={build.KerbalCount}, dedup={dedupRemoved}, startUT={startUT:F1}, endUT={endUT:F1})");

            return new FiledRecordingCommit
            {
                RecordingId = recordingId,
                StartUT = startUT,
                EndUT = endUT,
                Events = events,
                Actions = actions,
            };
        }

        /// <summary>
        /// Step 3c-post of a recording commit: the reputation seed origin this commit's
        /// rows are stamped against. A definite batch override is used as passed; a null
        /// or <see cref="ReputationSeedOrigin.NotYetCaptured"/> one is re-established here
        /// (see the repSeedOriginOverride docstring on <see cref="OnRecordingCommitted"/>).
        /// Ensures (and may write) the seed.
        /// </summary>
        private static ReputationSeedOrigin ResolveCommitReputationSeedOrigin(
            string recordingId, ReputationSeedOrigin? repSeedOriginOverride)
        {
            // A NotYetCaptured batch override (or none) is re-established here rather
            // than reused: it names no seed, only the expectation of one, and an earlier
            // recording of the same batch can have created the seed in its own step-6
            // recalc since. Re-running the ensure then reads PreExisting off that seed -
            // or lands in the refusal branch itself and flips the earlier rows - instead
            // of stamping this recording's death inside a value that never contained it.
            ReputationSeedOrigin repSeedOrigin =
                repSeedOriginOverride.HasValue
                && repSeedOriginOverride.Value != ReputationSeedOrigin.NotYetCaptured
                    ? repSeedOriginOverride.Value
                    : EnsureReputationSeedForCommit();
            if (repSeedOriginOverride.HasValue && repSeedOrigin != repSeedOriginOverride.Value)
            {
                ParsekLog.Info(Tag,
                    $"Reputation seed origin re-established for recording '{recordingId}': " +
                    $"batch override {repSeedOriginOverride.Value} no longer describes the " +
                    $"ledger, this commit reads {repSeedOrigin}");
            }
            return repSeedOrigin;
        }

        /// <summary>
        /// The rows one recording's commit files, built without writing anything (design
        /// section 2.12 Rule 5): no ledger row is added or changed, no seed is written, the
        /// recording is not touched (crew end states it still lacks are computed on a
        /// copy) and nothing is walked or patched. The two writes a commit makes on the
        /// way are returned as claims for <see cref="ApplyRecordingCommitBuildClaims"/>.
        /// </summary>
        internal sealed class RecordingCommitRowBuild
        {
            internal string RecordingId;
            /// <summary>Every row in filing order, before the dedup against the ledger.</summary>
            internal List<GameAction> Actions = new List<GameAction>();
            internal List<GameAction> ScienceActions = new List<GameAction>();
            internal int CostCount;
            internal int KerbalCount;
            /// <summary>The unclaimed rollout row the commit adopts onto the recording, or null.</summary>
            internal GameAction RolloutAdoptionCandidate;
            internal LedgerRolloutAdoption.RolloutAdoptionContext RolloutAdoptionContext;
            /// <summary>True when the recovery branch read the pending stock-payout mark.</summary>
            internal bool ConsumesAwaitingStockPayout;
            /// <summary>The recording the crew rows were read from: the recording itself, or a
            /// copy carrying the crew end states it did not yet have.</summary>
            internal Recording CrewSource;
        }

        /// <summary>
        /// Steps 1-3e of a recording commit as a pure build: convert the recording's
        /// captured <paramref name="events"/>, its <paramref name="pendingScience"/>, the
        /// vessel build cost and recovery, the crew assignments and the kerbal-death
        /// reputation penalty, and stamp the reputation rows against
        /// <paramref name="repSeedOrigin"/> (an explicit input: the build never ensures
        /// the seed). <paramref name="rec"/> may be null (no cost or crew rows, as a commit
        /// of an unknown id files today). <paramref name="ledgerActions"/> is read only, for
        /// the rollout row the recording would adopt.
        /// </summary>
        internal static RecordingCommitRowBuild BuildRecordingCommitRows(
            Recording rec,
            string recordingId,
            double startUT,
            double endUT,
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<PendingScienceSubject> pendingScience,
            IReadOnlyList<GameAction> ledgerActions,
            ReputationSeedOrigin repSeedOrigin,
            RecordingTree ownerTree = null)
        {
            var build = new RecordingCommitRowBuild { RecordingId = recordingId };

            // 1. Convert GameStateStore events to GameActions
            var actions = GameStateEventConverter.ConvertEvents(events, recordingId, startUT, endUT);

            // 2. Convert pending science subjects (the caller passes the routed subset of a
            // tree commit, or the static list on the single-recording path).
            var scienceActions = GameStateEventConverter.ConvertScienceSubjects(
                pendingScience, recordingId, startUT, endUT);
            actions.AddRange(scienceActions);
            build.ScienceActions = scienceActions;

            // 3. Vessel build cost and recovery funds from recording data
            GameAction adoptionCandidate;
            LedgerRolloutAdoption.RolloutAdoptionContext adoptionContext;
            bool consumesPayout;
            var costActions = BuildVesselCostActions(
                rec, recordingId, startUT, endUT, ledgerActions,
                out adoptionCandidate, out adoptionContext, out consumesPayout);
            actions.AddRange(costActions);
            build.CostCount = costActions.Count;
            build.RolloutAdoptionCandidate = adoptionCandidate;
            build.RolloutAdoptionContext = adoptionContext;
            build.ConsumesAwaitingStockPayout = consumesPayout;

            // 3b. Crew end states the recording does not carry yet are computed on a copy.
            Recording crewSource = rec;
            if (NeedsCrewEndStatePopulation(rec))
            {
                crewSource = Recording.DeepClone(rec);
                KerbalsModule.PopulateCrewEndStates(crewSource, ownerTree);
            }
            build.CrewSource = crewSource;

            // 3c. KerbalAssignment actions from vessel crew data
            var kerbalActions = BuildKerbalAssignmentActions(crewSource, recordingId, startUT, endUT);
            actions.AddRange(kerbalActions);
            build.KerbalCount = kerbalActions.Count;

            // 3d. The reputation penalty stock already applied for a crew death on this
            // recording (one row per vessel loss, magnitude read off the captured
            // VesselLoss event - never synthesized). Separate producer: see the method's
            // docstring for why it must not ride inside CreateKerbalAssignmentActions.
            var deathRepActions = BuildKerbalDeathRepPenaltyActions(
                crewSource, recordingId, startUT, endUT, repSeedOrigin);
            actions.AddRange(deathRepActions);

            // 3e. THE INSIDE-SEED STAMP, for every OTHER reputation-changing row this
            // commit is about to file. Steps 1-3d produced them from the recording's
            // captured events, so they are under exactly the production-order rule the
            // death row is: whatever the seed was, or will be, read from had already
            // taken their hit too. Measured on 2026-09-09_1815_CL-4-refly-crew-standin -
            // a +1 'Progression' milestone raised the live pool to 0.999999464, the seed
            // was taken at that value, the walk applied the milestone row again, and
            // PatchReputation wrote 1.00 -> 2.00 into the career. The death row stamps
            // itself in 3d with the same answer, so this pass finds it already stamped.
            StampReputationRowsAgainstSeed(actions, repSeedOrigin, "recording=" + recordingId);

            // 3f. Rows this recording recorded before a live-pool seed was read while it
            // was still uncommitted: the seed holds their hit, but 3e read PreExisting.
            StampRowsRecordedBeforeSeedCapture(
                actions, recordingId, ledgerActions, "recording=" + recordingId);

            build.Actions = actions;
            return build;
        }

        /// <summary>
        /// What a tree commit would file, built without committing (design section 2.12
        /// Rule 5): <see cref="Rows"/> holds every row of every recording of the tree in
        /// filing order, after the same dedup the commit applies (against the ledger and
        /// the rows the tree filed before), and <see cref="CrewSources"/> the recording (or
        /// crew-populated copy) each recording's crew rows were read from.
        /// </summary>
        internal sealed class TreeCommitRowPreview
        {
            internal readonly List<GameAction> Rows = new List<GameAction>();
            internal readonly Dictionary<string, Recording> CrewSources =
                new Dictionary<string, Recording>(StringComparer.Ordinal);
            internal int RecordingCount;
            internal int BuiltCount;
            internal int DedupRemoved;
        }

        /// <summary>
        /// The pure row build of <see cref="NotifyLedgerTreeCommitted"/> over a tree that
        /// is not committed yet (the merge dialog's pending or live tree): the same chain
        /// gap closure, Re-Fly attempt window, pending-science routing and per-recording
        /// <see cref="BuildRecordingCommitRows"/>, with the reputation seed origin read
        /// (never ensured: <see cref="PeekReputationSeedOriginForPreview"/>). Writes no
        /// ledger row, no seed, no recording, no pending-science or payout state. The
        /// commit-time optimizer split that runs between this preview and the real filing
        /// moves rows between the halves of a recording but changes no amount and no UT.
        /// </summary>
        internal static TreeCommitRowPreview BuildTreeCommitRowsPreview(RecordingTree tree)
        {
            var preview = new TreeCommitRowPreview();
            if (tree == null || tree.Recordings == null)
                return preview;

            var chainEndUTs = BuildChainEndUtMap(tree);
            var pendingSnapshot = GameStateRecorder.PendingScienceSubjects.Count == 0
                ? EmptyPendingScience
                : (IReadOnlyList<PendingScienceSubject>)
                    new List<PendingScienceSubject>(GameStateRecorder.PendingScienceSubjects);
            var pendingByRecording = BuildTreePendingScienceRouting(tree, chainEndUTs, pendingSnapshot);
            ReputationSeedOrigin repSeedOrigin = PeekReputationSeedOriginForPreview();
            var events = GameStateStore.Events;
            var ledger = Ledger.Actions;
            var seen = new List<GameAction>(ledger.Count + 16);
            seen.AddRange(ledger);

            using (ParsekLog.SuppressScope())
            {
                foreach (var rec in tree.Recordings.Values)
                {
                    if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                        continue;
                    preview.RecordingCount++;
                    double startUT = AdjustStartUtForChainGap(rec, rec.StartUT, chainEndUTs);
                    startUT = AdjustStartUtForReFlyProvisional(rec, startUT, ResolveRewindPointUt);
                    double endUT = rec.EndUT;

                    IReadOnlyList<PendingScienceSubject> perRecordingPending;
                    if (!pendingByRecording.TryGetValue(rec.RecordingId, out perRecordingPending))
                        perRecordingPending = EmptyPendingScience;

                    RecordingCommitRowBuild build = BuildRecordingCommitRows(
                        rec, rec.RecordingId, startUT, endUT, events, perRecordingPending,
                        ledger, repSeedOrigin, tree);
                    preview.CrewSources[rec.RecordingId] = build.CrewSource ?? rec;
                    preview.BuiltCount += build.Actions.Count;
                    var kept = DeduplicateAgainst(seen, build.Actions);
                    preview.DedupRemoved += build.Actions.Count - kept.Count;
                    preview.Rows.AddRange(kept);
                    seen.AddRange(kept);
                }
            }

            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Verbose(Tag,
                $"BuildTreeCommitRowsPreview: tree='{tree.TreeName}' " +
                $"recordings={preview.RecordingCount.ToString(ic)} built={preview.BuiltCount.ToString(ic)} " +
                $"dedup={preview.DedupRemoved.ToString(ic)} rows={preview.Rows.Count.ToString(ic)} " +
                $"repSeedOrigin={repSeedOrigin}");
            return preview;
        }

        /// <summary>
        /// The reputation seed origin a commit preview stamps its rows against, read
        /// without ensuring the seed: <see cref="ReputationSeedOrigin.PreExisting"/> when
        /// the ledger already holds a <see cref="GameActionType.ReputationInitial"/> row,
        /// else <see cref="ReputationSeedOrigin.CreatedThisCommitFromCareerBaseline"/>. The
        /// preview writes no seed, so the ledger it is compared against walks from no seed
        /// either; stamping the preview's rows inside a seed that does not exist would hide
        /// them from that walk. Both values leave every row outside the seed.
        /// </summary>
        internal static ReputationSeedOrigin PeekReputationSeedOriginForPreview()
        {
            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                if (actions[i] != null && actions[i].Type == GameActionType.ReputationInitial)
                    return ReputationSeedOrigin.PreExisting;
            }
            return ReputationSeedOrigin.CreatedThisCommitFromCareerBaseline;
        }

        /// <summary>
        /// The append step's two deferred writes from a <see cref="RecordingCommitRowBuild"/>:
        /// adopt the rollout row the build found onto the recording, and consume the
        /// pending stock-payout mark the build read.
        /// </summary>
        private static void ApplyRecordingCommitBuildClaims(RecordingCommitRowBuild build, double startUT)
        {
            if (build == null) return;
            if (build.RolloutAdoptionCandidate != null)
            {
                LedgerRolloutAdoption.ApplyRolloutAdoption(
                    build.RolloutAdoptionCandidate, build.RecordingId, startUT,
                    build.RolloutAdoptionContext);
            }
            if (build.ConsumesAwaitingStockPayout)
                InFlightRecoveryRequest.TryConsumeAwaitingStockPayout(build.RecordingId);
        }

        /// <summary>
        /// The post-walk half of a recording commit (step 5b). Must run after the walk
        /// that includes this recording's rows.
        /// </summary>
        private static void CompleteRecordingCommitAfterWalk(FiledRecordingCommit filed)
        {
            // 5b. #394 / #440B: commit-time reconciliation -- compare the sum of dropped
            // FundsChanged/ReputationChanged/ScienceChanged events (which the converter
            // intentionally drops, see GameStateEventConverter:121-130) against the
            // sum of effective emitted Funds/Rep/Science actions in the same UT window.
            // Log WARN on mismatch. Log-only: the drop block itself must stay, because
            // re-emitting those events would double-count against recovery + contract +
            // milestone channels (review section 5.1 regression guard).
            // #440B: now runs AFTER RecalculateAndPatch so it can read post-walk derived
            // fields; mirrors ReconcilePostWalk semantics and closes the double-WARN risk.
            ReconcileEarningsWindow(
                filed.Events, filed.Actions, filed.StartUT, filed.EndUT, filed.RecordingId);
        }

        /// <summary>
        /// Notifies the ledger orchestrator about each recording in a committed tree.
        /// Files every recording's rows (the steps of OnRecordingCommitted before its
        /// walk), then runs ONE recalculation + patch for the whole tree (cut at the live
        /// clock when committed rows lie ahead of it, see
        /// <see cref="RecalculateAndPatchAfterCommit"/>), then each recording's post-walk
        /// reconciliation.
        /// Chain continuations (ChainIndex > 0) have their startUT extended backward
        /// to the predecessor segment's EndUT, closing inter-segment gaps created by
        /// the optimizer's TrackSection splits so that events in those gaps are not lost.
        /// </summary>
        internal static void NotifyLedgerTreeCommitted(RecordingTree tree)
        {
            if (tree == null) return;

            // Build predecessor EndUT lookup for chain gap closure.
            // Key: "chainId:chainIndex", Value: EndUT of that segment.
            var chainEndUTs = BuildChainEndUtMap(tree);

            // #397 / #528 follow-up: snapshot PendingScienceSubjects ONCE, then route each
            // subject to the recording it actually belongs to. Tagged subjects keep their
            // tagged recording, while untagged subjects are attributed by captureUT window.
            // This preserves valid mixed-recording science without reopening the old
            // duplication bug where every recording re-read and re-emitted the full batch.
            var pendingSnapshot = GameStateRecorder.PendingScienceSubjects.Count == 0
                ? EmptyPendingScience
                : (IReadOnlyList<PendingScienceSubject>)
                    new List<PendingScienceSubject>(GameStateRecorder.PendingScienceSubjects);
            int pendingBefore = pendingSnapshot.Count;
            var pendingByRecording = BuildTreePendingScienceRouting(tree, chainEndUTs, pendingSnapshot);
            int routedPending = 0;
            foreach (var routed in pendingByRecording.Values)
                routedPending += routed.Count;
            int removedPending = 0;

            // ONE reputation-seed ensure for the WHOLE tree, before any recording of it
            // is committed. A tree is filed as a single act: the live pool read here has
            // already taken every death in it, so a death filed by the tree's third
            // recording is just as inside that seed as one filed by its first. Ensuring
            // per recording would report PreExisting to every recording after the first
            // and subtract those deaths twice.
            //
            // The one answer that does NOT survive the batch is NotYetCaptured, which
            // names no seed: FileRecordingCommitRows re-establishes it per recording,
            // because a recording filed earlier in this same loop can make the next
            // recording's ensure create the seed. See the repSeedOriginOverride docstring.
            ReputationSeedOrigin repSeedOrigin = EnsureReputationSeedForCommit();

            int gapsClosed = 0;
            int reFlyWindowsExtended = 0;
            int retiredProvisionalRows = 0;
            bool commitSucceeded = false;
            // Every recording's rows are filed before ONE walk runs
            // (OPTIMIZER-SPLIT-RETAG-COMMIT-WALK-TRANSIENT-CLAMP). The live pools already
            // hold the whole tree's effects, so a walk between two recordings sees a
            // partial batch: after the commit-time optimizer split moved a milestone to the
            // second half, the first half's own walk ran one award short and the funds
            // drawdown guard clamped. The reputation-seed re-establish per recording still
            // runs in FileRecordingCommitRows, against the same ledger the per-recording
            // walk's ensure used to see.
            var filedCommits = new List<FiledRecordingCommit>(tree.Recordings.Count);
            try
            {
                foreach (var rec in tree.Recordings.Values)
                {
                    FileTreeRecordingCommit(
                        rec, chainEndUTs, pendingByRecording, repSeedOrigin, filedCommits,
                        ref removedPending, ref gapsClosed, ref reFlyWindowsExtended);
                }

                Recording retiredProvisional = ResolveRetiredReFlyProvisionalForCommit(tree);
                if (retiredProvisional != null)
                    retiredProvisionalRows = FileRetiredReFlyProvisionalRows(
                        retiredProvisional.RecordingId, repSeedOrigin, "retired-provisional-tree-commit");

                commitSucceeded = true;
            }
            catch
            {
                // Rows already filed for earlier recordings stay in the ledger; walk them
                // so live state matches what was filed, as the per-recording walks did
                // before the batch walk. The original exception still propagates.
                WalkAfterFailedTreeCommit(filedCommits, retiredProvisionalRows);
                throw;
            }
            finally
            {
                // #397 / #528 follow-up: successful routed subsets are removed from the
                // global pending list as they become safe to discard. Unrelated retained
                // science from earlier failures stays pending across later successful
                // commits, and failing subsets stay visible so the caller can decide how
                // to recover; current production callers surface the exception instead of
                // retrying the whole commit batch.
                int remainingPending = GameStateRecorder.PendingScienceSubjects.Count;
                LogTreeCommitPendingScienceOutcome(
                    commitSucceeded, pendingBefore, routedPending, removedPending, remainingPending);
            }

            // 6. One walk for the whole tree, then each recording's post-walk
            // reconciliation (it reads the walk's derived fields). An empty tree files
            // nothing and walks nothing.
            WalkAndCompleteTreeCommit(tree, chainEndUTs, filedCommits, retiredProvisionalRows);

            // #390: prune events consumed by milestone creation + ledger conversion
            GameStateStore.PruneProcessedEvents();

            ParsekLog.Verbose(Tag,
                $"NotifyLedgerTreeCommitted: tree='{tree.TreeName}' " +
                $"recordings={tree.Recordings.Count}, chainSegments={chainEndUTs.Count}, " +
                $"gapsClosed={gapsClosed}, reFlyWindowsExtended={reFlyWindowsExtended}, " +
                $"retiredProvisionalRows={retiredProvisionalRows}");
        }

        /// <summary>
        /// One recording of <see cref="NotifyLedgerTreeCommitted"/>: widen its window for a
        /// chain gap and a Re-Fly provisional, file its rows (no walk) into
        /// <paramref name="filedCommits"/>, and remove its routed pending science from the
        /// static list once that science is safe to discard. An exception propagates after
        /// the science already in the ledger has been removed.
        /// </summary>
        internal static void FileTreeRecordingCommit(
            Recording rec,
            Dictionary<string, double> chainEndUTs,
            Dictionary<string, IReadOnlyList<PendingScienceSubject>> pendingByRecording,
            ReputationSeedOrigin repSeedOrigin,
            List<FiledRecordingCommit> filedCommits,
            ref int removedPending,
            ref int gapsClosed,
            ref int reFlyWindowsExtended)
        {
            double startUT = rec.StartUT;
            double endUT = rec.EndUT;

            // Close chain segment gap: extend startUT backward to predecessor's EndUT
            double adjusted = AdjustStartUtForChainGap(rec, startUT, chainEndUTs);
            if (adjusted < startUT) gapsClosed++;
            startUT = adjusted;

            // A Re-Fly attempt owns the awards tagged to it before its recorder bound.
            double reFlyAdjusted = AdjustStartUtForReFlyProvisional(
                rec, startUT, ResolveRewindPointUt);
            if (reFlyAdjusted < startUT) reFlyWindowsExtended++;
            startUT = reFlyAdjusted;

            IReadOnlyList<PendingScienceSubject> perRecordingPending;
            if (!pendingByRecording.TryGetValue(rec.RecordingId, out perRecordingPending))
                perRecordingPending = EmptyPendingScience;

            bool scienceAddedToLedger = false;
            try
            {
                filedCommits.Add(FileRecordingCommitRows(
                    rec.RecordingId,
                    startUT,
                    endUT,
                    perRecordingPending,
                    ref scienceAddedToLedger,
                    repSeedOrigin));
                removedPending += RemovePendingSubjectsFromStatic(perRecordingPending);
            }
            catch
            {
                if (scienceAddedToLedger)
                    removedPending += RemovePendingSubjectsFromStatic(perRecordingPending);
                throw;
            }
        }

        /// <summary>
        /// The catch side of <see cref="NotifyLedgerTreeCommitted"/>: when any rows were
        /// filed before the failure, walk them once; a walk that throws is logged and
        /// swallowed, so the caller rethrows the original exception.
        /// </summary>
        internal static void WalkAfterFailedTreeCommit(
            List<FiledRecordingCommit> filedCommits, int retiredProvisionalRows)
        {
            if (filedCommits.Count > 0 || retiredProvisionalRows > 0)
            {
                try
                {
                    RecalculateAndPatchAfterCommit(TreeCommitFailedRecalcReason);
                }
                catch (Exception walkEx)
                {
                    ParsekLog.Warn(Tag,
                        $"NotifyLedgerTreeCommitted: walk after a failed commit threw " +
                        $"(filed={filedCommits.Count.ToString(CultureInfo.InvariantCulture)}): " +
                        walkEx.Message);
                }
            }
        }

        /// <summary>
        /// The finally side of <see cref="NotifyLedgerTreeCommitted"/>: one Verbose line
        /// naming what happened to the pending science (cleared, preserved, retained,
        /// already removed, partially retained), or none when a success touched none.
        /// </summary>
        internal static void LogTreeCommitPendingScienceOutcome(
            bool commitSucceeded, int pendingBefore, int routedPending, int removedPending,
            int remainingPending)
        {
            if (commitSucceeded)
            {
                if (remainingPending == 0 && (pendingBefore > 0 || removedPending > 0))
                {
                    ParsekLog.Verbose(Tag,
                        $"NotifyLedgerTreeCommitted: cleared PendingScienceSubjects " +
                        $"(snapshot={pendingBefore}, routed={routedPending}, removed={removedPending})");
                }
                else if (removedPending > 0 || remainingPending > 0)
                {
                    ParsekLog.Verbose(Tag,
                        $"NotifyLedgerTreeCommitted: preserved unrelated PendingScienceSubjects after success " +
                        $"(snapshot={pendingBefore}, routed={routedPending}, removed={removedPending}, " +
                        $"remaining={remainingPending})");
                }
            }
            else
            {
                if (removedPending == 0)
                {
                    ParsekLog.Verbose(Tag,
                        $"NotifyLedgerTreeCommitted: retained PendingScienceSubjects after failure " +
                        $"(snapshot={pendingBefore}, routed={routedPending}, removed={removedPending}, " +
                        $"remaining={remainingPending})");
                }
                else if (remainingPending == 0)
                {
                    ParsekLog.Verbose(Tag,
                        $"NotifyLedgerTreeCommitted: already removed PendingScienceSubjects before failure " +
                        $"(snapshot={pendingBefore}, routed={routedPending}, removed={removedPending}, " +
                        $"remaining={remainingPending})");
                }
                else
                {
                    ParsekLog.Verbose(Tag,
                        $"NotifyLedgerTreeCommitted: partially retained PendingScienceSubjects after failure " +
                        $"(snapshot={pendingBefore}, routed={routedPending}, removed={removedPending}, " +
                        $"remaining={remainingPending})");
                }
            }
        }

        /// <summary>
        /// Step 6 of <see cref="NotifyLedgerTreeCommitted"/>: when the tree filed any rows,
        /// ONE walk for the whole tree, then each recording's post-walk reconciliation (it
        /// reads the walk's derived fields). An empty tree files nothing and walks nothing.
        /// </summary>
        internal static void WalkAndCompleteTreeCommit(
            RecordingTree tree,
            Dictionary<string, double> chainEndUTs,
            List<FiledRecordingCommit> filedCommits,
            int retiredProvisionalRows)
        {
            if (filedCommits.Count > 0 || retiredProvisionalRows > 0)
            {
                ParsekLog.Info(Tag,
                    $"NotifyLedgerTreeCommitted: filed rows for " +
                    $"{filedCommits.Count.ToString(CultureInfo.InvariantCulture)} recording(s) of " +
                    $"tree '{tree.TreeName}' before one ledger walk " +
                    $"(chainSegments={chainEndUTs.Count.ToString(CultureInfo.InvariantCulture)})");
                RecalculateAndPatchAfterCommit(TreeCommitRecalcReason);
                for (int i = 0; i < filedCommits.Count; i++)
                    CompleteRecordingCommitAfterWalk(filedCommits[i]);
            }
        }

        private static Dictionary<string, IReadOnlyList<PendingScienceSubject>> BuildTreePendingScienceRouting(
            RecordingTree tree,
            Dictionary<string, double> chainEndUTs,
            IReadOnlyList<PendingScienceSubject> pendingSnapshot)
        {
            var routed = new Dictionary<string, IReadOnlyList<PendingScienceSubject>>(StringComparer.Ordinal);
            var mutableBuckets = new Dictionary<string, List<PendingScienceSubject>>(StringComparer.Ordinal);
            var windows = BuildRecordingScienceWindows(tree, chainEndUTs);

            for (int i = 0; i < windows.Count; i++)
            {
                string recordingId = windows[i].RecordingId;
                if (string.IsNullOrEmpty(recordingId) || mutableBuckets.ContainsKey(recordingId))
                    continue;

                mutableBuckets[recordingId] = new List<PendingScienceSubject>();
            }

            if (pendingSnapshot != null && pendingSnapshot.Count > 0 && windows.Count > 0)
            {
                for (int i = 0; i < pendingSnapshot.Count; i++)
                {
                    string ownerRecordingId = ResolveTreePendingScienceOwnerRecordingId(
                        pendingSnapshot[i],
                        windows,
                        tree != null ? tree.ActiveRecordingId : null);
                    if (string.IsNullOrEmpty(ownerRecordingId))
                        continue;

                    List<PendingScienceSubject> bucket;
                    if (mutableBuckets.TryGetValue(ownerRecordingId, out bucket))
                        bucket.Add(pendingSnapshot[i]);
                }
            }

            foreach (var kvp in mutableBuckets)
            {
                routed[kvp.Key] = kvp.Value.Count == 0
                    ? EmptyPendingScience
                    : (IReadOnlyList<PendingScienceSubject>)kvp.Value;
            }

            return routed;
        }

        private static List<RecordingScienceWindow> BuildRecordingScienceWindows(
            RecordingTree tree,
            Dictionary<string, double> chainEndUTs)
        {
            var windows = new List<RecordingScienceWindow>();
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
                return windows;

            foreach (var rec in tree.Recordings.Values)
            {
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;

                windows.Add(new RecordingScienceWindow
                {
                    RecordingId = rec.RecordingId,
                    ChainId = rec.ChainId,
                    StartUT = AdjustStartUtForReFlyProvisional(
                        rec,
                        AdjustStartUtForChainGap(rec, rec.StartUT, chainEndUTs),
                        ResolveRewindPointUt),
                    EndUT = rec.EndUT
                });
            }

            return windows;
        }

        private static string ResolveTreePendingScienceOwnerRecordingId(
            PendingScienceSubject subject,
            IReadOnlyList<RecordingScienceWindow> windows,
            string activeRecordingId)
        {
            string subjectRecordingId = subject.recordingId ?? "";
            if (!string.IsNullOrEmpty(subjectRecordingId))
            {
                for (int i = 0; i < windows.Count; i++)
                {
                    if (!string.Equals(windows[i].RecordingId, subjectRecordingId, StringComparison.Ordinal))
                        continue;

                    RecordingScienceWindow tagged = windows[i];

                    // Belt-and-suspenders (re-fly pending-science audit): the tag
                    // alone used to be authoritative, so a stale subject leaking
                    // across a rewind (captureUT after the rewind cutoff, tag
                    // pointing at the recording that becomes the kept pre-rewind
                    // HEAD after the origin split) landed a ScienceEarning row on
                    // a non-superseded recording. Sanity-check the tag against
                    // the tagged recording's UT window; an implausible captureUT
                    // (NaN/Inf/<=0, e.g. synthetic entries) gives no basis to
                    // distrust the tag, so those keep tag routing unchanged.
                    double captureUT = subject.captureUT;
                    bool capturePlausible = !double.IsNaN(captureUT)
                        && !double.IsInfinity(captureUT)
                        && captureUT > 0.0;
                    if (!capturePlausible
                        || (captureUT >= tagged.StartUT && captureUT <= tagged.EndUT))
                        return tagged.RecordingId;

                    // Window mismatch: fall back to what the non-tag path would
                    // do — attribute by capture-UT window. If another window
                    // covers the capture, route there (the downstream tag check
                    // in ConvertScienceSubjects then skips it as cross-recording,
                    // so no science is credited from a mis-tagged capture). If
                    // NO window covers it, keep the tag: post-window tagged
                    // captures are legitimate for on-rails flights whose
                    // trajectory bounds end before the last live capture (the
                    // BUG-A rule in ConvertScienceSubjects owns that case).
                    string fallback = PickScienceWindowRecordingId(captureUT, windows, activeRecordingId);
                    string captureUtText = captureUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    string windowText =
                        $"[{tagged.StartUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, " +
                        $"{tagged.EndUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}]";
                    if (string.IsNullOrEmpty(fallback))
                    {
                        ParsekLog.Verbose(Tag,
                            $"Tree science routing: subject '{subject.subjectId}' tagged '{subjectRecordingId}' " +
                            $"has captureUT={captureUtText} outside the tagged window {windowText} and no other " +
                            "recording window covers it — keeping tag attribution (post-window on-rails captures " +
                            "are legitimate; the per-subject window checks in ConvertScienceSubjects decide)");
                        return tagged.RecordingId;
                    }

                    // Chain carve-out: when the covering window belongs to a
                    // CHAIN SIBLING of the tagged recording, do NOT re-route.
                    // The science windows are built with chain-gap closure
                    // (AdjustStartUtForChainGap pulls segment N's StartUT back
                    // to the predecessor's EndUT), so the legitimate BUG-A
                    // shape on a chained flight — recorder live through
                    // on-rails warp, capture tagged segment A after A's
                    // trajectory bounds ended — lands the captureUT inside the
                    // successor's ADJUSTED window. Re-routing there would make
                    // the downstream cross-recording check drop the science
                    // entirely (credited to NEITHER segment). Within one chain
                    // the tag is authoritative (it is one physical flight);
                    // the stale re-fly leak this sanity check targets is
                    // already removed by the ReconciliationBundle cutoff-drop
                    // on the rewind success path.
                    string fallbackChainId = null;
                    for (int j = 0; j < windows.Count; j++)
                    {
                        if (string.Equals(windows[j].RecordingId, fallback, StringComparison.Ordinal))
                        {
                            fallbackChainId = windows[j].ChainId;
                            break;
                        }
                    }
                    if (!string.IsNullOrEmpty(tagged.ChainId)
                        && string.Equals(tagged.ChainId, fallbackChainId, StringComparison.Ordinal))
                    {
                        ParsekLog.Verbose(Tag,
                            $"Tree science routing: subject '{subject.subjectId}' tagged '{subjectRecordingId}' " +
                            $"has captureUT={captureUtText} outside the tagged window {windowText}, but the " +
                            $"covering window '{fallback}' is a chain sibling (chain={tagged.ChainId}) — " +
                            "keeping tag attribution (chain-gap-adjusted coverage; the tag is authoritative " +
                            "within one chained flight)");
                        return tagged.RecordingId;
                    }

                    ParsekLog.Warn(Tag,
                        $"Tree science routing: subject '{subject.subjectId}' tagged '{subjectRecordingId}' " +
                        $"has captureUT={captureUtText} outside the tagged window {windowText} — " +
                        $"re-routing by capture UT to '{fallback}'");
                    return fallback;
                }

                return null;
            }

            return PickScienceWindowRecordingId(subject.captureUT, windows, activeRecordingId);
        }

        private static string PickScienceWindowRecordingId(
            double captureUT,
            IReadOnlyList<RecordingScienceWindow> windows,
            string activeRecordingId)
        {
            if (double.IsNaN(captureUT) || double.IsInfinity(captureUT))
                return null;
            if (captureUT < 0.0)
                return null;

            bool found = false;
            RecordingScienceWindow best = default(RecordingScienceWindow);
            for (int i = 0; i < windows.Count; i++)
            {
                var window = windows[i];
                if (captureUT < window.StartUT || captureUT > window.EndUT)
                    continue;

                if (!found)
                {
                    best = window;
                    found = true;
                    continue;
                }

                if (window.StartUT > best.StartUT)
                {
                    best = window;
                    continue;
                }

                if (window.StartUT < best.StartUT)
                    continue;

                if (window.EndUT < best.EndUT)
                {
                    best = window;
                    continue;
                }

                if (window.EndUT > best.EndUT)
                    continue;

                bool windowIsActive = string.Equals(window.RecordingId, activeRecordingId, StringComparison.Ordinal);
                bool bestIsActive = string.Equals(best.RecordingId, activeRecordingId, StringComparison.Ordinal);
                if (windowIsActive && !bestIsActive)
                {
                    best = window;
                    continue;
                }

                if (!windowIsActive && bestIsActive)
                    continue;

                if (string.CompareOrdinal(window.RecordingId ?? "", best.RecordingId ?? "") < 0)
                    best = window;
            }

            return found ? best.RecordingId : null;
        }

        private static int RemovePendingSubjects(
            List<PendingScienceSubject> remainingPending,
            IReadOnlyList<PendingScienceSubject> processedPending)
        {
            if (remainingPending == null || remainingPending.Count == 0)
                return 0;
            if (processedPending == null || processedPending.Count == 0)
                return 0;

            int removed = 0;
            for (int i = 0; i < processedPending.Count; i++)
            {
                if (remainingPending.Remove(processedPending[i]))
                    removed++;
            }

            return removed;
        }

        internal static IReadOnlyList<PendingScienceSubject> BuildPendingScienceSubsetForRecording(
            IReadOnlyList<PendingScienceSubject> pendingSnapshot,
            string recordingId,
            double startUT,
            double endUT)
        {
            if (pendingSnapshot == null || pendingSnapshot.Count == 0)
                return EmptyPendingScience;

            var routed = new List<PendingScienceSubject>();
            string ownerRecordingId = recordingId ?? "";
            for (int i = 0; i < pendingSnapshot.Count; i++)
            {
                var subject = pendingSnapshot[i];
                string subjectRecordingId = subject.recordingId ?? "";
                if (!string.IsNullOrEmpty(subjectRecordingId))
                {
                    if (string.Equals(subjectRecordingId, ownerRecordingId, StringComparison.Ordinal))
                        routed.Add(subject);
                    continue;
                }

                if (DoesUntaggedScienceCaptureMatchWindow(subject.captureUT, startUT, endUT))
                    routed.Add(subject);
            }

            return routed.Count == 0
                ? EmptyPendingScience
                : (IReadOnlyList<PendingScienceSubject>)routed;
        }

        /// <summary>
        /// Returns the commit-window start UT for a direct single-recording commit.
        /// Chain continuations can have optimizer-introduced gaps between segments; when
        /// the immediate predecessor is already committed, extend the window backward to
        /// that predecessor's EndUT so the standalone path matches tree-commit routing.
        /// </summary>
        internal static double ResolveStandaloneCommitWindowStartUt(Recording rec, double startUT)
        {
            if (rec == null ||
                !rec.IsChainRecording ||
                rec.ChainIndex <= 0 ||
                string.IsNullOrEmpty(rec.ParentRecordingId))
            {
                return startUT;
            }

            var predecessor = FindRecordingById(rec.ParentRecordingId);
            if (predecessor == null ||
                !string.Equals(predecessor.ChainId ?? "", rec.ChainId ?? "", StringComparison.Ordinal) ||
                predecessor.ChainIndex != rec.ChainIndex - 1)
            {
                return startUT;
            }

            var chainEndUTs = new Dictionary<string, double>
            {
                { $"{rec.ChainId}:{predecessor.ChainIndex}", predecessor.EndUT }
            };
            return AdjustStartUtForChainGap(rec, startUT, chainEndUTs);
        }

        internal static int RemovePendingSubjectsFromStatic(
            IReadOnlyList<PendingScienceSubject> processedPending)
        {
            return RemovePendingSubjects(GameStateRecorder.PendingScienceSubjects, processedPending);
        }

        /// <summary>
        /// Finalizes a standalone recording commit's scoped pending-science subset.
        /// Successful commits (or failures after science already reached the ledger)
        /// discard only the subset routed to that recording. Pre-ledger failures keep
        /// the subset pending so the caller can decide how to recover without losing the
        /// science data. Current production callers surface the exception instead of
        /// retrying the whole commit batch.
        /// </summary>
        internal static void FinalizeScopedPendingScienceCommit(
            string logTag,
            string commitContext,
            int pendingBefore,
            IReadOnlyList<PendingScienceSubject> pendingForCommit,
            bool commitSucceeded,
            bool scienceAddedToLedger)
        {
            pendingForCommit = pendingForCommit ?? EmptyPendingScience;

            int removed = 0;
            if (commitSucceeded || scienceAddedToLedger)
                removed = RemovePendingSubjectsFromStatic(pendingForCommit);

            int currentPending = GameStateRecorder.PendingScienceSubjects.Count;
            if (commitSucceeded || scienceAddedToLedger)
            {
                if (currentPending == 0 && (pendingBefore > 0 || removed > 0))
                {
                    ParsekLog.Verbose(logTag,
                        $"{commitContext}: cleared PendingScienceSubjects " +
                        $"(before={pendingBefore}, removed={removed})");
                }
                else if (removed > 0 || currentPending > 0)
                {
                    ParsekLog.Verbose(logTag,
                        $"{commitContext}: preserved unrelated PendingScienceSubjects after success " +
                        $"(before={pendingBefore}, removed={removed}, remaining={currentPending})");
                }
            }
            else if (pendingBefore > 0 || currentPending > 0)
            {
                ParsekLog.Verbose(logTag,
                    $"{commitContext}: retained PendingScienceSubjects after failure " +
                    $"(before={pendingBefore}, remaining={currentPending})");
            }
        }

        private static bool DoesUntaggedScienceCaptureMatchWindow(
            double captureUT,
            double startUT,
            double endUT)
        {
            if (double.IsNaN(captureUT) || double.IsInfinity(captureUT))
                return false;
            if (captureUT < 0.0)
                return false;
            if (captureUT < startUT)
                return false;
            if (captureUT > endUT)
                return false;

            return true;
        }

        /// <summary>
        /// Builds a lookup of chain segment EndUTs from a recording tree.
        /// Returns a dictionary keyed by "chainId:chainIndex" for O(1) predecessor lookup.
        /// </summary>
        internal static Dictionary<string, double> BuildChainEndUtMap(RecordingTree tree)
        {
            var map = new Dictionary<string, double>();
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec.IsChainRecording && rec.ChainIndex >= 0)
                {
                    map[$"{rec.ChainId}:{rec.ChainIndex}"] = rec.EndUT;
                }
            }
            return map;
        }

        /// <summary>
        /// If the recording is a chain continuation (ChainIndex > 0) and the predecessor
        /// segment's EndUT is earlier than this recording's startUT, returns the predecessor's
        /// EndUT to close the inter-segment gap. Otherwise returns startUT unchanged.
        /// </summary>
        internal static double AdjustStartUtForChainGap(
            Recording rec, double startUT, Dictionary<string, double> chainEndUTs)
        {
            if (rec == null || !rec.IsChainRecording || rec.ChainIndex <= 0)
                return startUT;

            string predKey = $"{rec.ChainId}:{rec.ChainIndex - 1}";
            if (chainEndUTs.TryGetValue(predKey, out double predEndUT) && predEndUT < startUT)
            {
                ParsekLog.Verbose(Tag,
                    $"Chain gap closure: extended startUT from " +
                    $"{startUT.ToString("F1", CultureInfo.InvariantCulture)} to " +
                    $"{predEndUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"for recording '{rec.RecordingId}' " +
                    $"(chain={rec.ChainId}, idx={rec.ChainIndex})");
                return predEndUT;
            }

            return startUT;
        }

        /// <summary>
        /// REFLY-UNBOUND-PROVISIONAL-TAGS-AWARDS-TO-ORIGIN: an award earned while a Re-Fly
        /// session's provisional had no recorder bound is tagged to the provisional
        /// (<see cref="ReFlyAwardTagging"/>), at a UT before the provisional's first trajectory
        /// point. The converter's lower bound would drop it, so a provisional's commit window
        /// opens at its rewind point's UT. Only the provisional's OWN tagged events are read
        /// through this window (the converter scopes by tag), so nothing is taken from another
        /// recording. A chain continuation (an optimizer split's later half) keeps the chain
        /// rule: the split retag already moved every event at or after the cut onto it.
        /// Pure apart from the lookup.
        /// </summary>
        internal static double AdjustStartUtForReFlyProvisional(
            Recording rec, double startUT, Func<string, double> rewindPointUtLookup)
        {
            if (rec == null || string.IsNullOrEmpty(rec.ProvisionalForRpId))
                return startUT;
            if (rec.IsChainRecording && rec.ChainIndex > 0)
                return startUT;
            if (rewindPointUtLookup == null)
                return startUT;

            double rpUT = rewindPointUtLookup(rec.ProvisionalForRpId);
            if (double.IsNaN(rpUT) || double.IsInfinity(rpUT) || rpUT <= 0.0 || rpUT >= startUT)
                return startUT;

            ParsekLog.Verbose(Tag,
                $"Re-Fly attempt window: extended startUT from " +
                $"{startUT.ToString("F1", CultureInfo.InvariantCulture)} to " +
                $"{rpUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"for recording '{rec.RecordingId}' (rp={rec.ProvisionalForRpId})");
            return rpUT;
        }

        /// <summary>
        /// The UT of a rewind point by id, or NaN when the scenario does not hold it. Falls
        /// back to the live Re-Fly marker's own copy of the UT when the marker names the point.
        /// </summary>
        internal static double ResolveRewindPointUt(string rewindPointId)
        {
            if (string.IsNullOrEmpty(rewindPointId))
                return double.NaN;
            var scenario = ParsekScenario.Instance;
            if (ReferenceEquals(null, scenario))
                return double.NaN;
            var points = scenario.RewindPoints;
            if (points != null)
            {
                for (int i = 0; i < points.Count; i++)
                {
                    var rp = points[i];
                    if (rp != null && string.Equals(rp.RewindPointId, rewindPointId, StringComparison.Ordinal))
                        return rp.UT;
                }
            }
            var marker = scenario.ActiveReFlySessionMarker;
            if (marker != null
                && string.Equals(marker.RewindPointId, rewindPointId, StringComparison.Ordinal))
                return marker.RewindPointUT;
            return double.NaN;
        }

        /// <summary>
        /// The Re-Fly provisional of the live session when a prune retired it out of
        /// <paramref name="tree"/> this session (<see cref="ReFlyProvisionalRetirement"/>),
        /// so this commit will not file it; null otherwise.
        /// </summary>
        private static Recording ResolveRetiredReFlyProvisionalForCommit(RecordingTree tree)
        {
            if (tree == null || tree.Recordings == null)
                return null;
            var scenario = ParsekScenario.Instance;
            if (ReferenceEquals(null, scenario))
                return null;
            var marker = scenario.ActiveReFlySessionMarker;
            Recording retired = ReFlyProvisionalRetirement.Peek(marker);
            if (retired == null || string.IsNullOrEmpty(retired.RecordingId))
                return null;
            if (!string.Equals(retired.RecordingId, marker.ActiveReFlyRecordingId, StringComparison.Ordinal))
                return null;
            if (!string.Equals(retired.TreeId, tree.Id, StringComparison.Ordinal))
                return null;
            if (tree.Recordings.ContainsKey(retired.RecordingId)
                || RecordingStore.IsCommittedRecordingId(retired.RecordingId))
                return null;
            return retired;
        }

        /// <summary>
        /// REFLY-UNBOUND-PROVISIONAL-TAGS-AWARDS-TO-ORIGIN, retired-attempt half: the awards
        /// a Re-Fly attempt earned before its recorder bound are tagged to its provisional
        /// (<see cref="ReFlyAwardTagging"/>). When the provisional carried nothing the merge
        /// could play, the scene-exit prune retires it out of the tree and the merge concludes
        /// with no supersede (<c>outcome=concluded-no-supersede</c>), so no recording commit
        /// ever converts those events and the next walk would patch the career below what KSP
        /// already paid. The player merged rather than discarded, so the awards stay: each
        /// event and pending science subject tagged to the retired provisional is filed here as
        /// an untagged row (the shape it had before the attempt owned it, and the shape the
        /// conclusion's ledger-tag re-home gives a retired provisional's rows), and the store
        /// copies are untagged with it. Runs inside the tree commit, before its one walk, so
        /// the walk that applies the tree applies these rows too. Returns the rows added.
        /// </summary>
        internal static int FileRetiredReFlyProvisionalRows(
            string recordingId, ReputationSeedOrigin repSeedOrigin, string reason)
        {
            if (string.IsNullOrEmpty(recordingId))
                return 0;

            Initialize();

            var tagged = new List<GameStateEvent>();
            var storeEvents = GameStateStore.Events;
            for (int i = 0; i < storeEvents.Count; i++)
            {
                var e = storeEvents[i];
                if (!string.Equals(e.recordingId ?? "", recordingId, StringComparison.Ordinal))
                    continue;
                e.recordingId = "";
                tagged.Add(e);
            }

            var subjects = new List<PendingScienceSubject>();
            double minUT = double.MaxValue, maxUT = double.MinValue;
            var pending = GameStateRecorder.PendingScienceSubjects;
            for (int i = 0; i < pending.Count; i++)
            {
                var s = pending[i];
                if (!string.Equals(s.recordingId ?? "", recordingId, StringComparison.Ordinal))
                    continue;
                s.recordingId = "";
                subjects.Add(s);
                if (s.captureUT < minUT) minUT = s.captureUT;
                if (s.captureUT > maxUT) maxUT = s.captureUT;
            }

            if (tagged.Count == 0 && subjects.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"Retired Re-Fly provisional '{recordingId}' ({reason}): no tagged events " +
                    "or pending science to file");
                return 0;
            }

            var actions = GameStateEventConverter.ConvertEvents(
                tagged, null, double.NegativeInfinity, double.PositiveInfinity);
            List<GameAction> scienceActions = null;
            if (subjects.Count > 0)
            {
                scienceActions = GameStateEventConverter.ConvertScienceSubjects(
                    subjects, null, minUT - 1.0, maxUT + 1.0);
                actions.AddRange(scienceActions);
            }

            ReputationSeedOrigin origin = repSeedOrigin != ReputationSeedOrigin.NotYetCaptured
                ? repSeedOrigin
                : EnsureReputationSeedForCommit();
            StampReputationRowsAgainstSeed(actions, origin, "retired-refly-provisional=" + recordingId);
            StampRowsRecordedBeforeSeedCapture(
                actions, recordingId, Ledger.Actions, "retired-refly-provisional=" + recordingId);

            int converted = actions.Count;
            actions = DeduplicateAgainstLedger(actions);
            for (int i = 0; i < actions.Count; i++)
                actions[i].Sequence = AllocateKscSequence();
            Ledger.AddActions(actions, LedgerRowWriter.StockApplied);

            if (scienceActions != null)
            {
                var survivingScience = new List<GameAction>();
                for (int i = 0; i < actions.Count; i++)
                    if (actions[i].Type == GameActionType.ScienceEarning)
                        survivingScience.Add(actions[i]);
                if (survivingScience.Count > 0)
                    GameStateStore.CommitScienceActions(survivingScience);
            }

            int subjectsRemoved = RemovePendingScienceSubjectsForRecordings(
                new[] { recordingId }, reason);
            int eventsUntagged = GameStateStore.RetagEventsForRecording(recordingId, "");

            ParsekLog.Info(Tag,
                $"Re-Fly award handoff: outcome=filed-retired-provisional-rows " +
                $"provisional={recordingId} reason={reason} " +
                $"rows={actions.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"events={tagged.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"science={subjects.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"deduped={(converted - actions.Count).ToString(CultureInfo.InvariantCulture)} " +
                $"subjectsRemoved={subjectsRemoved.ToString(CultureInfo.InvariantCulture)} " +
                $"eventsUntagged={eventsUntagged.ToString(CultureInfo.InvariantCulture)} " +
                "- the attempt was merged, so what it earned before its recorder bound stays");
            return actions.Count;
        }
    }
}
