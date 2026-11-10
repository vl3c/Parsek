using System;
using System.Collections.Generic;
using System.Globalization;
using KscActionExpectation = Parsek.KscActionExpectationClassifier.KscActionExpectation;

namespace Parsek
{
    internal static partial class LedgerOrchestrator
    {
        private static void SeedInitialResourceBalances()
        {
            GameStateBaseline initialBaseline;
            bool hasInitialBaseline = TryGetInitialResourceBaseline(out initialBaseline);

            if (!fundsSeedDone)
                fundsSeedDone = EnsureInitialFundsSeed(hasInitialBaseline, initialBaseline);
            if (!scienceSeedDone)
                scienceSeedDone = EnsureInitialScienceSeed(hasInitialBaseline, initialBaseline);
            if (!repSeedDone)
            {
                ReputationSeedOrigin repSeedOrigin;
                repSeedDone = EnsureInitialReputationSeed(
                    hasInitialBaseline, initialBaseline, out repSeedOrigin);
            }
        }

        /// <summary>
        /// Live ProgressTracking read the pre-ledger progress seed capture consults:
        /// presence, the positive OnLoad-done signal
        /// (<see cref="CurrencyScenarioReadiness.IsScenarioModuleLoaded"/>) and the flattened
        /// tree (null when the tracker or its tree is unavailable).
        /// </summary>
        internal struct ProgressSeedProbe
        {
            public bool TrackerPresent;
            public bool TrackerLoaded;
            public List<PreLedgerProgressSeed.NodeObservation> Nodes;
        }

        internal static ProgressSeedProbe ReadProgressSeedProbe()
        {
            var provider = ProgressSeedProbeForTesting;
            if (provider != null)
                return provider();
            return ReadProgressSeedProbeLive();
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static ProgressSeedProbe ReadProgressSeedProbeLive()
        {
            var probe = new ProgressSeedProbe();
            var tracker = ProgressTracking.Instance;
            if (tracker == null)
                return probe;

            probe.TrackerPresent = true;
            probe.TrackerLoaded = CurrencyScenarioReadiness.IsScenarioModuleLoaded(tracker);
            if (probe.TrackerLoaded && tracker.achievementTree != null)
                probe.Nodes = KspStatePatcher.ObserveProgressTreeForSeed(tracker.achievementTree);
            return probe;
        }

        /// <summary>
        /// Captures the save's <see cref="PreLedgerProgressSeed"/> once: the stock progress
        /// the career earned before its ledger began. Runs at the top of every recalc and
        /// returns immediately once a seed exists. Defers (and the milestone patch skips)
        /// until ProgressTracking's OnLoad has provably run, because before it the live tree
        /// is freshly generated and reads every node unreached. Returns true once a seed
        /// exists.
        ///
        /// <para>
        /// Reads the WHOLE ledger (any effective state, tombstoned and after-cutoff rows
        /// included) and every captured game-state event, so a node the ledger owns anywhere
        /// on the timeline, or is about to own through a pending flight, is never seeded.
        /// That is what keeps a rewound-away node out: its row still exists after the
        /// cutoff, so the seed never contains it and the patch still clears it.
        /// </para>
        /// </summary>
        internal static bool EnsurePreLedgerProgressSeed()
        {
            if (Ledger.ProgressSeed.Captured)
                return true;

            var probe = ReadProgressSeedProbe();
            string deferral = PreLedgerProgressSeed.DescribeCaptureDeferral(
                probe.TrackerPresent, probe.TrackerLoaded, probe.Nodes != null);
            if (deferral != null)
            {
                ParsekLog.VerboseOnChange(Tag, "pre-ledger-progress-seed-defer", deferral,
                    "PreLedgerProgressSeed: deferring capture - " + deferral +
                    " (milestone patch skipped until captured)");
                return false;
            }

            PreLedgerProgressSeed.CaptureStats stats;
            var seed = PreLedgerProgressSeed.Capture(probe.Nodes, Ledger.Actions, GameStateStore.Events, out stats);
            if (!Ledger.TrySetProgressSeed(seed))
                return Ledger.ProgressSeed.Captured;

            var sampleIds = new List<string>(seed.OneShotIds);
            sampleIds.Sort(StringComparer.Ordinal);
            ParsekLog.Info(Tag,
                "PreLedgerProgressSeed: captured from the live ProgressTracking tree: " +
                string.Format(CultureInfo.InvariantCulture,
                    "nodes={0} liveComplete={1} seededNodes={2} excludedByLedgerRow={3} " +
                    "excludedByEvent={4} seededRecords={5} seededRecordThresholds={6} " +
                    "recordsOwnedByLedger={7} recordsUnresolved={8} ledgerActions={9}",
                    stats.NodesObserved, stats.LiveComplete, stats.OneShotSeeded,
                    stats.ExcludedByLedgerRow, stats.ExcludedByEvent, stats.RecordsSeeded,
                    stats.RecordThresholdsSeeded, stats.RecordsOwnedByLedger,
                    stats.RecordsUnresolved, Ledger.Actions.Count) +
                (sampleIds.Count > 0
                    ? " ids=[" + KspStatePatcher.ComposeBoundedIdentitySample(sampleIds, 10) + "]"
                    : ""));
            return true;
        }

        /// <summary>
        /// Ensures the career's <see cref="GameActionType.ReputationInitial"/> seed exists
        /// AT THE START OF A COMMIT and reports WHERE it came from, so the kerbal-death
        /// reputation-penalty producer can decide whether the penalty it is about to file
        /// is already inside that seed value.
        ///
        /// <para>
        /// ORDERING IS THE WHOLE POINT. The seed is otherwise ensured deep inside
        /// <see cref="RecalculateAndPatchCore"/>, which a commit reaches only at its LAST
        /// step - after the producer has already run and after its row is in the ledger.
        /// Two things break in that order: the producer cannot see whether the seed will
        /// be a live-pool read (which would already contain the death), and the seed's
        /// own refusal branch would see this commit's fresh KerbalDeath
        /// <see cref="GameActionType.ReputationPenalty"/> row through
        /// <see cref="LedgerHasReputationTimelineActions"/> and refuse the live pool
        /// because of it. Running the ensure BEFORE the producer removes both.
        /// </para>
        ///
        /// <para>
        /// Idempotent: the later call inside the recalc finds the seed already present,
        /// or still legitimately deferred, and does nothing new.
        /// </para>
        /// </summary>
        internal static ReputationSeedOrigin EnsureReputationSeedForCommit()
        {
            GameStateBaseline initialBaseline;
            bool hasInitialBaseline = TryGetInitialResourceBaseline(out initialBaseline);

            ReputationSeedOrigin origin;
            bool done = EnsureInitialReputationSeed(
                hasInitialBaseline, initialBaseline, out origin);
            if (done)
                repSeedDone = true;

            return origin;
        }

        /// <summary>
        /// Live currency-pool read the funds / science seeds consult: presence, the
        /// positive OnLoad-done signal (<see cref="CurrencyScenarioReadiness.IsScenarioModuleLoaded"/>)
        /// and the value. A LOADED zero is a real zero; an unloaded or absent one is not.
        /// </summary>
        internal struct CurrencyPoolProbe
        {
            public bool FundingPresent;
            public bool FundingLoaded;
            public double Funds;
            public bool ScienceSingletonPresent;
            public bool ScienceSingletonLoaded;
            public float Science;
            // The reputation seed's live read (presence + value only: its zero pool defers
            // rather than trusting a loaded zero, see EnsureInitialReputationSeed).
            public bool ReputationSingletonPresent;
            public float ReputationValue;
        }

        internal static CurrencyPoolProbe ReadCurrencyPoolProbe()
        {
            var provider = CurrencyPoolProbeForTesting;
            if (provider != null)
                return provider();

            var probe = new CurrencyPoolProbe();
            var funding = Funding.Instance;
            if (funding != null)
            {
                probe.FundingPresent = true;
                probe.FundingLoaded = CurrencyScenarioReadiness.IsScenarioModuleLoaded(funding);
                probe.Funds = funding.Funds;
            }

            var rnd = ResearchAndDevelopment.Instance;
            if (rnd != null)
            {
                probe.ScienceSingletonPresent = true;
                probe.ScienceSingletonLoaded = CurrencyScenarioReadiness.IsScenarioModuleLoaded(rnd);
                probe.Science = rnd.Science;
            }

            var reputation = global::Reputation.Instance;
            if (reputation != null)
            {
                probe.ReputationSingletonPresent = true;
                probe.ReputationValue = reputation.reputation;
            }

            return probe;
        }

        internal enum FundsSeedDecision
        {
            AlreadySeeded,
            SeedFromBaseline,
            SeedFromLivePool,
            SeedConfirmedZero,
            Defer
        }

        /// <summary>
        /// Pure: how the career's FundsInitial seed should be created or repaired.
        ///
        /// <para>A zero pool used to defer forever (the seed accepted non-zero values only,
        /// because an unloaded Funding also reads 0). The KSP-SETTINGS-AUDIT S4 branch takes
        /// a zero ONLY when Funding's OnLoad has provably run AND the ledger carries no funds
        /// history - a StartingFunds = 0 career at its first load. With history, a zero pool
        /// says nothing about the start value (the player may have spent it all), so the
        /// seed keeps deferring exactly as before.</para>
        /// </summary>
        internal static FundsSeedDecision DecideInitialFundsSeed(
            bool hasSeed,
            float existingSeedFunds,
            bool existingSeedConfirmedZero,
            bool hasInitialBaseline,
            double baselineFunds,
            CurrencyPoolProbe probe,
            bool hasFundsTimelineActions)
        {
            if (hasSeed && (existingSeedFunds != 0f || existingSeedConfirmedZero))
                return FundsSeedDecision.AlreadySeeded;

            if (hasInitialBaseline && baselineFunds != 0.0)
                return FundsSeedDecision.SeedFromBaseline;

            if (probe.FundingPresent && probe.Funds != 0.0)
                return FundsSeedDecision.SeedFromLivePool;

            if (probe.FundingPresent && probe.FundingLoaded && probe.Funds == 0.0
                && !hasFundsTimelineActions)
                return FundsSeedDecision.SeedConfirmedZero;

            return FundsSeedDecision.Defer;
        }

        private static bool EnsureInitialFundsSeed(bool hasInitialBaseline, GameStateBaseline initialBaseline)
        {
            GameAction existingSeed;
            bool hasSeed = TryGetFundsSeed(out existingSeed);
            var probe = ReadCurrencyPoolProbe();
            var decision = DecideInitialFundsSeed(
                hasSeed,
                hasSeed ? existingSeed.InitialFunds : 0f,
                hasSeed && existingSeed.InitialFundsConfirmedZero,
                hasInitialBaseline,
                hasInitialBaseline ? initialBaseline.funds : 0.0,
                probe,
                LedgerHasFundsTimelineActions());

            switch (decision)
            {
                case FundsSeedDecision.AlreadySeeded:
                    return true;
                case FundsSeedDecision.SeedFromBaseline:
                    Ledger.SeedInitialFunds(initialBaseline.funds);
                    return true;
                case FundsSeedDecision.SeedFromLivePool:
                    Ledger.SeedInitialFunds(probe.Funds);
                    return true;
                case FundsSeedDecision.SeedConfirmedZero:
                    Ledger.SeedConfirmedZeroFunds();
                    return true;
                default:
                    ParsekLog.Verbose(Tag,
                        "SeedInitialFunds: deferring - " +
                        (!probe.FundingPresent
                            ? "Funding.Instance null"
                            : !probe.FundingLoaded
                                ? "Funding present but its OnLoad has not run"
                                : "pool reads 0 and the ledger already carries funds history") +
                        (hasInitialBaseline ? " (initial baseline has zero funds)" : ""));
                    return false;
            }
        }

        private static bool TryGetFundsSeed(out GameAction seed)
        {
            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action != null && action.Type == GameActionType.FundsInitial)
                {
                    seed = action;
                    return true;
                }
            }

            seed = null;
            return false;
        }

        private static bool EnsureInitialScienceSeed(bool hasInitialBaseline, GameStateBaseline initialBaseline)
        {
            if (LedgerHasSeed(GameActionType.ScienceInitial))
                return true;

            bool scienceBaselineUnread = false;
            if (hasInitialBaseline)
            {
                if (initialBaseline.IsScienceKnown(ExpectedCurrencySingletonsForBaseline()))
                {
                    Ledger.SeedInitialScience((float)initialBaseline.science);
                    return true;
                }

                // CAREER-COLD-SC-LOAD-SEEDS-SCIENCE-REP-ZERO: the career-start baseline was
                // captured before R&D loaded (a cold Space Center load runs ParsekScenario's
                // OnLoad ahead of it), so its 0 is no reading. Treat it as no baseline for
                // science and fall through to the live pool, which DeferredSeed reaches once
                // R&D has loaded.
                LogBaselineCurrencyNotCaptured("science", initialBaseline);
                scienceBaselineUnread = true;
            }

            if (LedgerHasScienceTimelineActions())
            {
                Ledger.SeedInitialScience(0f);
                LogSeedRefusal("SeedInitialScience", "science", scienceBaselineUnread);
                return true;
            }

            var probe = ReadCurrencyPoolProbe();
            if (!probe.ScienceSingletonPresent)
                return false;

            if (probe.Science == 0f)
            {
                // KSP-SETTINGS-AUDIT S4: a Science-mode or fresh career at 0 science used to
                // defer forever. A zero is taken only off an R&D singleton whose OnLoad has
                // provably run (no science history exists here - that branch returned above).
                if (!probe.ScienceSingletonLoaded)
                {
                    ParsekLog.Verbose(Tag,
                        "SeedInitialScience: deferring - R&D present but its OnLoad has not run");
                    return false;
                }

                Ledger.SeedInitialScience(0f);
                ParsekLog.Verbose(Tag,
                    "SeedInitialScience: seeded a loaded zero pool (no baseline, no science history)");
                return true;
            }

            Ledger.SeedInitialScience(probe.Science);
            return true;
        }

        /// <summary>
        /// Creates the career's single <see cref="GameActionType.ReputationInitial"/> row
        /// if it does not exist yet. Returns true once a seed exists (created here or
        /// already present), false while the live-pool read is still legitimately
        /// deferred.
        /// </summary>
        /// <param name="hasInitialBaseline">Whether a career-start baseline was found.</param>
        /// <param name="initialBaseline">That baseline; read only when the flag is true.</param>
        /// <param name="origin">
        /// WHERE the seed came from as seen by THIS call. The kerbal-death rep-penalty
        /// producer reads it to decide whether the penalty it is about to file is already
        /// inside the seeded value; every other caller discards it.
        /// </param>
        private static bool EnsureInitialReputationSeed(
            bool hasInitialBaseline,
            GameStateBaseline initialBaseline,
            out ReputationSeedOrigin origin)
        {
            if (LedgerHasSeed(GameActionType.ReputationInitial))
            {
                origin = ReputationSeedOrigin.PreExisting;
                return true;
            }

            bool reputationBaselineUnread = false;
            if (hasInitialBaseline)
            {
                if (initialBaseline.IsReputationKnown(ExpectedCurrencySingletonsForBaseline()))
                {
                    // A career-start value: it predates every flight this commit files.
                    Ledger.SeedInitialReputation(initialBaseline.reputation);
                    origin = ReputationSeedOrigin.CreatedThisCommitFromCareerBaseline;
                    RestampInsideSeedRowsAgainstCareerStartSeed(origin);
                    return true;
                }

                // Same early-capture shape as the science seed: Reputation had not loaded,
                // so the baseline carries no reputation reading. The branches below then
                // decide exactly as they do with no baseline at all.
                LogBaselineCurrencyNotCaptured("reputation", initialBaseline);
                reputationBaselineUnread = true;
            }

            if (LedgerHasReputationTimelineActions())
            {
                // This branch DELIBERATELY refuses the live pool and seeds career start
                // (0 - what a stock career begins at), so its value predates the flight
                // just as a baseline does.
                Ledger.SeedInitialReputation(0f);
                LogSeedRefusal("SeedInitialReputation", "reputation", reputationBaselineUnread);
                origin = ReputationSeedOrigin.CreatedThisCommitFromRefusalFallback;
                RestampInsideSeedRowsAgainstCareerStartSeed(origin);
                return true;
            }

            // BELT AND BRACES, ahead of the live-pool read and ahead of the deferral
            // reasons below so it names itself when it fires. A milestone row that still
            // carries all-zero rewards has not been enriched yet
            // (GameStateRecorder.OnProgressComplete emits BuildMilestoneDetail(0, 0, 0)
            // and the AwardProgressPatch postfix fills it in afterwards), which means
            // stock has not applied that award to its pools either. Reading the live pool
            // now would seed the PRE-award figure, and every row stamped inside it would
            // be zeroed by the walk once enriched - the reconstruction short by the award,
            // persisted, and never flipped back.
            //
            // The milestone door defers its own recalc one frame so this should not be
            // reachable from there (ShouldDeferKscRecalcOneFrame); this guard covers a
            // SYNCHRONOUS recalc arriving from any other producer inside the same window,
            // and the no-frame-host fallback.
            //
            // It only ever refuses the LIVE-POOL branch: the career-start branches above
            // have already returned, so the answer here is "defer", never "seed something
            // else". A mode whose milestones legitimately award nothing therefore just
            // keeps deferring a seed that would patch nothing anyway, and the first real
            // reputation row still reaches the refusal branch above.
            int unenrichedMilestones = CountUnenrichedMilestoneRows();
            if (unenrichedMilestones > 0)
            {
                origin = ReputationSeedOrigin.NotYetCaptured;
                LogReputationSeedMilestoneDeferral(unenrichedMilestones);
                return false;
            }

            var repProbe = ReadCurrencyPoolProbe();
            bool noReputationInstance = !repProbe.ReputationSingletonPresent;
            if (noReputationInstance
                || Math.Abs(repProbe.ReputationValue) <= 0.01f)
            {
                // Still deferred: the seed is EXPECTED to be captured off a LATER live
                // pool, one that has already taken any death this commit is filing. If
                // that later capture turns out to be a career-start branch instead, the
                // rows stamped on this expectation are flipped back outside there.
                //
                // The reason matters when reading a flight back: a null singleton is a
                // scene/load-order fact (the scene-exit commit runs during OnLoad, before
                // the destination scene's career ScenarioModules have awoken), while a
                // ~0 pool is a genuinely fresh career. CL-2-pod-impact-ledger's deferral
                // looked like the second and was the first.
                origin = ReputationSeedOrigin.NotYetCaptured;
                LogReputationSeedPoolDeferral(noReputationInstance, repProbe);
                return false;
            }

            // Live-pool read: any death that already lowered the pool is inside the
            // value being seeded.
            SeedInitialReputationFromLivePool(repProbe);
            origin = ReputationSeedOrigin.CreatedThisCommitFromLivePool;
            return true;
        }

        /// <summary>
        /// <see cref="EnsureInitialReputationSeed"/>'s line for a seed deferred because
        /// milestone rows still carry zero rewards.
        /// </summary>
        internal static void LogReputationSeedMilestoneDeferral(int unenrichedMilestones)
        {
            ParsekLog.Verbose(Tag,
                "EnsureInitialReputationSeed: deferring the reputation seed - " +
                unenrichedMilestones.ToString(CultureInfo.InvariantCulture) +
                " milestone row(s) still carry zero rewards, so stock has not applied " +
                "the award yet and the live pool is the PRE-award figure");
        }

        /// <summary>
        /// <see cref="EnsureInitialReputationSeed"/>'s line for a seed deferred because the
        /// live reputation pool is absent or about zero.
        /// </summary>
        internal static void LogReputationSeedPoolDeferral(
            bool noReputationInstance, CurrencyPoolProbe repProbe)
        {
            ParsekLog.Verbose(Tag,
                "EnsureInitialReputationSeed: deferring the reputation seed - " +
                (noReputationInstance
                    ? "Reputation.Instance null"
                    : "pool ~0, deferring (reputation="
                        + repProbe.ReputationValue.ToString("R", CultureInfo.InvariantCulture)
                        + ")"));
        }

        /// <summary>
        /// <see cref="EnsureInitialReputationSeed"/>'s live-pool seed: the pool's value,
        /// with the uncommitted recordings and the capture UT when they can be read.
        /// </summary>
        internal static void SeedInitialReputationFromLivePool(CurrencyPoolProbe repProbe)
        {
            // The live pool is a snapshot of EVERY stock change to date, including any
            // reputation movement - a death, a milestone, a contract outcome - from a
            // flight that is already RECORDED but not committed yet (the active tree, or
            // the pending tree a quickload restores). Their rows are produced later, by a
            // commit that reads PreExisting, so the seed names those recordings and the
            // read's UT, and the commit stamps their earlier rows inside
            // (ReputationSeedMembership.IsRecordedBeforeSeedCapture; todo
            // QUICKLOAD-REPUTATION-WALK-TWICE-LIVE, QL-2 / QL-2b).
            List<string> uncommitted = null;
            double captureUT = double.NaN;
            if (TryReadUncommittedRecordingsAtSeedCapture(out uncommitted, out captureUT))
                Ledger.SeedInitialReputation(repProbe.ReputationValue, uncommitted, captureUT);
            else
                Ledger.SeedInitialReputation(repProbe.ReputationValue);
        }

        /// <summary>
        /// The recordings recorded but not committed at this moment, and the game UT, for a
        /// live-pool reputation seed (<see cref="GameAction.SeedPendingRecordingIds"/>).
        /// False when there are none or the clock cannot be read; the seed then records
        /// nothing, which is the apply-every-row answer.
        /// </summary>
        private static bool TryReadUncommittedRecordingsAtSeedCapture(
            out List<string> recordingIds, out double captureUT)
        {
            recordingIds = null;
            captureUT = double.NaN;
            try
            {
                var provider = UncommittedRecordingIdsForTesting;
                recordingIds = provider != null ? provider() : ReadUncommittedRecordingIdsLive();
                if (recordingIds == null || recordingIds.Count == 0)
                    return false;
                bool inLoad = ParsekScenario.IsOnLoadInProgress;
                captureUT = ResolveSeedCaptureUT(
                    inLoad, inLoad ? ReadLoadedFlightStateUT() : double.NaN,
                    // The raw clock, not the ledger's held now: the capture UT is compared
                    // with recording rows, which keep their trajectory UTs.
                    inLoad ? double.NaN : GetRawClockUT());
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    "EnsureInitialReputationSeed: could not read the uncommitted recordings or " +
                    "the clock at the live-pool seed (" + ex.GetType().Name + "); the seed " +
                    "records none");
                recordingIds = null;
                return false;
            }
            if (double.IsNaN(captureUT) || double.IsInfinity(captureUT))
            {
                recordingIds = null;
                return false;
            }
            return true;
        }

        private static double ReadLoadedFlightStateUT()
        {
            var provider = LoadedFlightStateUTForTesting;
            return provider != null ? provider() : ParsekScenario.ReadLoadedFlightStateUTRaw();
        }

        /// <summary>
        /// Pure: the capture UT a live-pool reputation seed records. Inside a scenario
        /// OnLoad the Planetarium can still read the clock from BEFORE the load (stock
        /// <c>Game.Load</c> runs OnLoad before <c>FlightState.Load</c> sets the time, see
        /// <see cref="ParsekScenario.ResolveColdLoadClockUT"/>); after an F9 back in time
        /// that reading is later than the restored flight's resumed awards, and they would
        /// be marked inside a seed that lacks them. So during OnLoad the loaded save's own
        /// <c>flightState.universalTime</c> is the clock (the
        /// <see cref="KerbalsModule.ResolveWalkClockUT(double?,bool,double,bool,double,double)"/>
        /// rule), else the live Planetarium. Not usable -> NaN, and the seed records nothing.
        /// </summary>
        internal static double ResolveSeedCaptureUT(
            bool onLoadInProgress, double loadedSaveUT, double liveUT)
        {
            double ut = onLoadInProgress ? loadedSaveUT : liveUT;
            if (double.IsNaN(ut) || double.IsInfinity(ut) || ut < 0.0)
                return double.NaN;
            return ut;
        }

        /// <summary>
        /// Recording ids of the pending tree and of the live active tree when it is not a
        /// committed tree, minus any recording a committed tree holds (its rows are already
        /// in the ledger).
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static List<string> ReadUncommittedRecordingIdsLive()
        {
            var ids = new List<string>();
            AddTreeRecordingIds(ids, PendingTreeSlot.PendingTree);
            AddTreeRecordingIds(ids, ParsekFlight.UncommittedActiveTreeForLedger());
            if (ids.Count == 0)
                return ids;

            var committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees != null)
            {
                for (int t = 0; t < committedTrees.Count; t++)
                {
                    var recs = committedTrees[t]?.Recordings;
                    if (recs == null) continue;
                    ids.RemoveAll(id => recs.ContainsKey(id));
                }
            }
            return ids;
        }

        private static void AddTreeRecordingIds(List<string> ids, RecordingTree tree)
        {
            if (tree?.Recordings == null)
                return;
            foreach (var id in tree.Recordings.Keys)
            {
                if (!string.IsNullOrEmpty(id) && !ids.Contains(id))
                    ids.Add(id);
            }
        }

        /// <summary>
        /// Marks inside the reputation seed every reputation-changing row in
        /// <paramref name="actions"/> that <paramref name="recordingId"/> recorded before a
        /// live-pool seed was read while it was uncommitted
        /// (<see cref="ReputationSeedMembership.IsRecordedBeforeSeedCapture"/>). The
        /// production-order stamp (<see cref="StampReputationRowsAgainstSeed"/>) reads
        /// PreExisting for such a commit and so stamps nothing. Additive only; returns how
        /// many rows it changed.
        /// </summary>
        internal static int StampRowsRecordedBeforeSeedCapture(
            IList<GameAction> actions, string recordingId,
            IReadOnlyList<GameAction> ledgerActions, string context)
        {
            if (actions == null || actions.Count == 0 || ledgerActions == null)
                return 0;
            GameAction seed = null;
            for (int i = 0; i < ledgerActions.Count; i++)
            {
                if (ledgerActions[i] != null
                    && ledgerActions[i].Type == GameActionType.ReputationInitial)
                {
                    seed = ledgerActions[i];
                    break;
                }
            }
            if (seed == null || seed.SeedPendingRecordingIds == null)
                return 0;

            int stamped = 0, after = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null) continue;
                if (!ReputationSeedMembership.IsReputationAffectingRow(action.Type)) continue;
                if (action.InsideReputationSeed) continue;
                if (ReputationSeedMembership.IsRecordedBeforeSeedCapture(seed, recordingId, action.UT))
                {
                    action.InsideReputationSeed = true;
                    stamped++;
                }
                else
                {
                    after++;
                }
            }

            if (stamped == 0)
                return 0;

            ParsekLog.Info(Tag,
                $"Reputation seed stamp: {stamped.ToString(CultureInfo.InvariantCulture)} row(s) " +
                $"stamped insideRepSeed=True for {context ?? "(none)"} - recorded before the " +
                $"live-pool seed was read at UT " +
                $"{seed.SeedCaptureUT.ToString("R", CultureInfo.InvariantCulture)} while uncommitted " +
                $"({after.ToString(CultureInfo.InvariantCulture)} later row(s) stay outside)");
            return stamped;
        }

        /// <summary>
        /// The live-write doors' half of the inside-seed stamp. READ-ONLY with respect to
        /// the seed: it never creates one, so the seed's creation point is exactly where
        /// it was before this stamp existed (the door's own tail recalc).
        ///
        /// <para>
        /// WHY IT MUST NOT SEED, and this is an event-ordering fact rather than a
        /// preference. Two of the four sources that reach these doors fire their
        /// GameEvent BEFORE the pool moves: decompiled <c>ProgressNode.Complete()</c>
        /// raises <c>OnProgressComplete</c> before every subclass calls
        /// <c>AwardProgressStandard</c>, and <c>Reputation.AddReputation</c> raises
        /// <c>OnCurrencyModified</c> before assigning <c>rep</c>. A first-capture seed
        /// read from inside the handler would therefore be the PRE-award pool; the row
        /// would be stamped inside a value that does not contain it, the walk would zero
        /// it, and the reconstruction would come out SHORT by the award - silently, since
        /// the stamp logs Info. Contract outcomes and StrategyActivate are ordered the
        /// other way round, but the door has no way to tell them apart from the action.
        /// </para>
        ///
        /// <para>
        /// The rule applied instead is the commit path's: a row produced while no seed
        /// exists is stamped from <see cref="ReputationSeedOrigin.NotYetCaptured"/> - the
        /// seed will be read off a later live pool that HAS taken the award by then. A
        /// seed that already exists is <see cref="ReputationSeedOrigin.PreExisting"/> and
        /// stamps nothing. When the later capture turns out to be a career-start branch,
        /// <see cref="RestampInsideSeedRowsAgainstCareerStartSeed"/> flips the row back
        /// out at the seed's own creation site.
        /// </para>
        ///
        /// <para>
        /// A no-op for every row
        /// <see cref="ReputationSeedMembership.IsReputationAffectingRow"/> answers false
        /// for, so the tech / facility / hire doors are untouched.
        /// </para>
        /// </summary>
        private static void StampLiveWriteRowAgainstSeed(GameAction action, string context)
        {
            if (action == null) return;
            if (!ReputationSeedMembership.IsReputationAffectingRow(action.Type)) return;

            ReputationSeedOrigin origin = LedgerHasSeed(GameActionType.ReputationInitial)
                ? ReputationSeedOrigin.PreExisting
                : ReputationSeedOrigin.NotYetCaptured;
            StampReputationRowsAgainstSeed(new List<GameAction> { action }, origin, context);
        }

        /// <summary>
        /// Stamps <see cref="GameAction.InsideReputationSeed"/> on every reputation-changing
        /// row in <paramref name="actions"/> that is inside the seed described by
        /// <paramref name="repSeedOrigin"/>. Returns how many rows it changed.
        ///
        /// <para>
        /// PRODUCTION ORDER, NOT UT, and that is the whole contract. The rows handed here
        /// are the ones the calling producer is about to file; the origin says whether the
        /// pool the seed came from had already taken their hit. Nothing in this method
        /// reads <see cref="GameAction.UT"/>, deliberately: a re-fly from a RewindPoint
        /// files rows whose game UT is EARLIER than the seed's capture UT and which must
        /// still apply, and the mirror case reads the other way round.
        /// </para>
        ///
        /// <para>
        /// Idempotent and additive-only: a row already stamped inside is left alone and
        /// never counted, and no row is ever flipped OUT here - that is
        /// <see cref="RestampInsideSeedRowsAgainstCareerStartSeed"/>'s job, at the seed's
        /// own creation site. The rows are not in the ledger yet on the commit path, so no
        /// StateVersion bump is owed; the KSC doors stamp before their own
        /// <c>Ledger.AddAction</c> for the same reason.
        /// </para>
        /// </summary>
        internal static int StampReputationRowsAgainstSeed(
            IList<GameAction> actions, ReputationSeedOrigin repSeedOrigin, string context)
        {
            if (actions == null || actions.Count == 0)
                return 0;
            if (!ReputationSeedMembership.IsInsideReputationSeed(repSeedOrigin))
                return 0;

            int stamped = 0, alreadyStamped = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null) continue;
                if (!ReputationSeedMembership.IsReputationAffectingRow(action.Type)) continue;
                if (action.InsideReputationSeed) { alreadyStamped++; continue; }

                action.InsideReputationSeed = true;
                stamped++;
            }

            if (stamped == 0 && alreadyStamped == 0)
                return 0;

            ParsekLog.Info(Tag,
                $"Reputation seed stamp: {stamped.ToString(CultureInfo.InvariantCulture)} row(s) " +
                $"stamped insideRepSeed=True " +
                $"({alreadyStamped.ToString(CultureInfo.InvariantCulture)} already stamped) " +
                $"for {context ?? "(none)"} - repSeedOrigin={repSeedOrigin}");
            return stamped;
        }

        /// <summary>
        /// How many <see cref="GameActionType.MilestoneAchievement"/> rows in the ledger
        /// still carry all-zero rewards, i.e. have not been enriched by the
        /// AwardProgressPatch postfix yet.
        ///
        /// <para>
        /// ALL THREE fields, not just reputation. A row awaiting enrichment is the exact
        /// shape <c>BuildMilestoneDetail(0, 0, 0)</c> produces, and requiring all three to
        /// be zero is what keeps a genuine rep-free-but-funds-paying milestone from
        /// reading as pending forever. A milestone that really awards nothing at all is
        /// indistinguishable from a pending one and is treated as pending; the only cost
        /// is a seed that keeps deferring, in a mode where that seed would patch nothing.
        /// </para>
        /// </summary>
        internal static int CountUnenrichedMilestoneRows()
        {
            var actions = Ledger.Actions;
            int pending = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null) continue;
                if (action.Type != GameActionType.MilestoneAchievement) continue;
                if (action.MilestoneRepAwarded != 0f) continue;
                if (action.MilestoneFundsAwarded != 0f) continue;
                if (action.MilestoneScienceAwarded != 0f) continue;
                pending++;
            }

            return pending;
        }

        /// <summary>True when any milestone row is still awaiting enrichment.</summary>
        internal static bool HasUnenrichedMilestoneRow()
        {
            return CountUnenrichedMilestoneRows() > 0;
        }

        /// <summary>
        /// Flips every reputation-affecting row that is stamped INSIDE the reputation seed
        /// back to OUTSIDE, because the seed this call just created carries a career-start
        /// value that cannot contain any of them.
        ///
        /// <para>
        /// The stamp a producer writes is an assumption about how the seed WILL be
        /// captured: <see cref="ReputationSeedOrigin.NotYetCaptured"/> means "deferred, so
        /// a later LIVE pool will already have taken this hit". When the seed is finally
        /// created by a branch whose value predates the flights - the career-start baseline
        /// or the refusal fallback - that assumption is falsified, and every row resting on
        /// it would otherwise be skipped by <c>ReputationModule.ProcessRepPenalty</c>
        /// against a seed that never contained it. Measured on CL-2-pod-impact-ledger
        /// (2026-09-09_2253): one -10 death row stamped inside at the commit, the seed
        /// created 4 ms later by the refusal branch at 0, the walk reading +2 against a
        /// live -7.99.
        /// </para>
        ///
        /// <para>
        /// A seed created FROM THE LIVE POOL is the case this must NOT touch: that value
        /// already contains every death filed before it, so its inside-seed rows stay
        /// inside and applying them would subtract the same penalty twice. The predicate
        /// (<see cref="KerbalDeathRepPenalty.CareerStartSeedInvalidatesInsideStamps"/>)
        /// carries that distinction; this method only executes it.
        /// </para>
        ///
        /// <para>
        /// Runs at the seed's own creation site so the flip is ATOMIC with the seed row -
        /// no second pass, and no window in which the ledger holds a career-start seed and
        /// a row claiming to be inside it.
        /// </para>
        /// </summary>
        internal static void RestampInsideSeedRowsAgainstCareerStartSeed(ReputationSeedOrigin origin)
        {
            if (!ReputationSeedMembership.CareerStartSeedInvalidatesInsideStamps(origin))
                return;

            var actions = Ledger.Actions;
            int restamped = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null) continue;
                if (!ReputationSeedMembership.IsReputationAffectingRow(action.Type)) continue;
                if (!action.InsideReputationSeed) continue;

                action.InsideReputationSeed = false;
                restamped++;
            }

            if (restamped == 0)
                return;

            // Cached ERS / ELS / recalc consumers key off StateVersion; a flip that does
            // not bump leaves them serving the pre-flip stamps.
            Ledger.BumpStateVersion();

            string seedSource = origin == ReputationSeedOrigin.CreatedThisCommitFromRefusalFallback
                ? "career start"
                : "the career-start baseline";
            ParsekLog.Info(Tag,
                $"SeedInitialReputation: {restamped.ToString(CultureInfo.InvariantCulture)} " +
                $"reputation row(s) were stamped inside a seed that was then " +
                $"read from {seedSource}; re-stamped outside so the walk applies them");
        }

        /// <summary>
        /// The currency singletons the current game mode creates, which
        /// <see cref="GameStateBaseline.IsScienceKnown"/> / <see cref="GameStateBaseline.IsReputationKnown"/>
        /// consult for a baseline that recorded no presence flag. No live game reads as the
        /// full set (the conservative answer: a 0 with the R&amp;D-not-loaded signature is
        /// then not trusted).
        /// </summary>
        private static CurrencySingletons ExpectedCurrencySingletonsForBaseline()
        {
            var provider = ExpectedCurrencySingletonsForTesting;
            if (provider != null)
                return provider();
            try
            {
                return CurrencyScenarioReadiness.ExpectedForCurrentGame();
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseOnChange(Tag, "baseline-currency-mode-read", ex.GetType().Name,
                    "Initial resource baseline: game-mode read failed (" + ex.GetType().Name +
                    ") - treating every currency singleton as expected");
                return CurrencySingletons.All;
            }
        }

        /// <summary>
        /// The history-refusal branch's line. With no career-start baseline at all the
        /// refusal is unexpected and stays a Warn. With a baseline whose value was merely
        /// unread (captured before that scenario module loaded, the normal cold Space Center
        /// load) it is the expected path for a fresh career whose pool is 0 until the first
        /// row - a 0 pool defers the live read - so it is Info and stays off the WRN surface.
        /// </summary>
        private static void LogSeedRefusal(string prefix, string currency, bool baselineUnread)
        {
            string head = prefix + ": refusing to treat current " + currency + " as initial " +
                "because " + currency + " timeline actions already exist and ";
            if (baselineUnread)
            {
                ParsekLog.Info(Tag, head +
                    "the career-start baseline carries no " + currency + " reading " +
                    "(captured before its scenario module loaded) - seeding career start 0");
                return;
            }

            ParsekLog.Warn(Tag, head + "no baseline was available");
        }

        private static void LogBaselineCurrencyNotCaptured(string currency, GameStateBaseline baseline)
        {
            bool? recorded = currency == "science" ? baseline.scienceKnown : baseline.reputationKnown;
            ParsekLog.InfoRateLimited(Tag, "seed-baseline-not-captured:" + currency,
                "SeedInitial: career-start baseline at ut=" +
                baseline.ut.ToString("R", CultureInfo.InvariantCulture) +
                " carries no " + currency + " reading (" +
                (recorded.HasValue
                    ? "its scenario module had not loaded at capture"
                    : "unrecorded flag, 0 beside an empty researched-tech list") +
                ") - not seeding " + currency + " from it; the live pool decides instead");
        }

        private static bool TryGetInitialResourceBaseline(out GameStateBaseline initialBaseline)
        {
            initialBaseline = null;

            var baselines = GameStateStore.Baselines;
            if (baselines == null || baselines.Count == 0)
                return false;

            for (int i = 0; i < baselines.Count; i++)
            {
                var candidate = baselines[i];
                if (candidate == null)
                    continue;

                if (!IsInitialResourceBaseline(candidate))
                    continue;

                if (initialBaseline == null || candidate.ut < initialBaseline.ut)
                    initialBaseline = candidate;
            }

            return initialBaseline != null;
        }

        private static bool IsInitialResourceBaseline(GameStateBaseline candidate)
        {
            if (candidate == null)
                return false;

            return candidate.ut >= -InitialResourceBaselineMaxUtSeconds
                && candidate.ut <= InitialResourceBaselineMaxUtSeconds;
        }

        private static bool LedgerHasSeed(GameActionType seedType)
        {
            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action != null && action.Type == seedType)
                    return true;
            }

            return false;
        }

        private static bool LedgerHasFundsTimelineActions()
        {
            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                if (ActionTouchesFundsBudget(actions[i]))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Pure: true for a row that moves the funds pool (the FundsModule projection set,
        /// read without the Effective gates so a not-yet-walked row still counts as history).
        /// </summary>
        internal static bool ActionTouchesFundsBudget(GameAction action)
        {
            if (action == null)
                return false;

            switch (action.Type)
            {
                case GameActionType.FundsEarning:
                    return action.FundsAwarded != 0f;
                case GameActionType.FundsSpending:
                    return action.FundsSpent != 0f;
                case GameActionType.MilestoneAchievement:
                    return action.MilestoneFundsAwarded != 0f;
                case GameActionType.ContractAccept:
                    return action.AdvanceFunds != 0f;
                case GameActionType.ContractComplete:
                case GameActionType.ContractParameterComplete:
                    return action.FundsReward != 0f || action.TransformedFundsReward != 0f;
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                    return action.FundsPenalty != 0f;
                case GameActionType.FacilityUpgrade:
                case GameActionType.FacilityRepair:
                    return action.FacilityCost != 0f;
                case GameActionType.KerbalHire:
                    return action.HireCost != 0f;
                case GameActionType.StrategyActivate:
                    return action.SetupCost != 0f;
                case GameActionType.StockPoolAdjustment:
                    return action.AdjustmentPool == StockPool.Funds && action.AdjustmentAmount != 0.0;
                default:
                    return false;
            }
        }

        private static bool LedgerHasScienceTimelineActions()
        {
            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (ActionTouchesScienceBudget(action))
                    return true;
            }

            return false;
        }

        private static bool LedgerHasReputationTimelineActions()
        {
            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];

                // A row that already declares itself INSIDE the seed is not independent
                // reputation history: it is part of whatever value the seed will carry,
                // and the module never applies it. Counting it here would let a commit's
                // own KerbalDeath row talk the seed out of the live pool it belongs to,
                // seeding career start with a WARN on a save that was only waiting for a
                // non-zero pool.
                //
                // DELIBERATELY STILL SCOPED TO KerbalDeath, even though the stamp itself
                // was generalized to every reputation-affecting row in 2026-09-15. This
                // predicate does not decide whether a row is applied - it decides WHICH
                // BRANCH creates the seed. Skipping stamped milestone / contract rows here
                // too would talk the refusal branch out of firing on a save whose ledger
                // really does carry reputation history, changing the seed VALUE (and with
                // it the reconstruction) for every such save in one step nothing has
                // flown. Measured consequence of keeping it narrow: CL-2-pod-impact-ledger
                // takes the same refusal branch it took on 2026-09-09_2316, seeds career
                // start, and RestampInsideSeedRowsAgainstCareerStartSeed flips every
                // stamped row back out - so its armed ledger totals are untouched.
                if (action != null
                    && action.InsideReputationSeed
                    && action.Type == GameActionType.ReputationPenalty
                    && action.RepPenaltySource == ReputationPenaltySource.KerbalDeath)
                    continue;

                if (ActionTouchesReputationBudget(action))
                    return true;
            }

            return false;
        }

        private static bool ActionTouchesScienceBudget(GameAction action)
        {
            if (action == null)
                return false;

            switch (action.Type)
            {
                case GameActionType.ScienceEarning:
                case GameActionType.ScienceSpending:
                // Strategy currency-exchange science leg: moves the science pool, so a
                // ledger whose ONLY science row is an exchange still reports science as
                // tracked to LedgerHasScienceTimelineActions.
                case GameActionType.StrategyScienceDebit:
                case GameActionType.StrategyScienceCredit:
                case GameActionType.ScienceLabEarning:
                    return true;
                case GameActionType.ContractComplete:
                case GameActionType.ContractParameterComplete:
                    return action.ScienceReward != 0f || action.TransformedScienceReward != 0f;
                case GameActionType.MilestoneAchievement:
                    return action.MilestoneScienceAwarded != 0f;
                case GameActionType.StrategyActivate:
                    return action.SetupScienceCost != 0f;
                case GameActionType.StockPoolAdjustment:
                    return action.AdjustmentPool == StockPool.Science && action.AdjustmentAmount != 0.0;
                default:
                    return false;
            }
        }

        private static bool ActionTouchesReputationBudget(GameAction action)
        {
            if (action == null)
                return false;

            switch (action.Type)
            {
                case GameActionType.ReputationEarning:
                case GameActionType.ReputationPenalty:
                    return true;
                case GameActionType.ContractAccept:
                    return action.RepPenalty != 0f;
                case GameActionType.ContractComplete:
                case GameActionType.ContractParameterComplete:
                    return action.RepReward != 0f || action.TransformedRepReward != 0f;
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                    return action.RepPenalty != 0f;
                case GameActionType.MilestoneAchievement:
                    return action.MilestoneRepAwarded != 0f;
                case GameActionType.StrategyActivate:
                    return action.SetupReputationCost != 0f;
                case GameActionType.StockPoolAdjustment:
                    return action.AdjustmentPool == StockPool.Reputation && action.AdjustmentAmount != 0.0;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Pure decision (drawdown-guard plan §3.2): the recalc patch is an AUTHORITATIVE
        /// (intended) downward reduction iff ANY of the five time-travel signals is set.
        /// When true, the "Keep what you earned" clamp is bypassed so legitimate time-travel
        /// drawdowns (rewind / re-fly / merge / tombstone / deferred rewind adjustment) apply
        /// unchanged. Pure / unit-testable; the live wrapper reads the five inputs.
        /// </summary>
        internal static bool IsAuthoritativeReduction(
            bool isRewinding, bool hasReFlyMarker, bool hasMergeJournal,
            bool tombstonePath, bool rewindResourceAdjustmentInProgress)
        {
            return isRewinding || hasReFlyMarker || hasMergeJournal
                || tombstonePath || rewindResourceAdjustmentInProgress;
        }

        /// <summary>
        /// The five time-travel signals plus <paramref name="reFlyDiscard"/>: the recalculation
        /// right after a Re-Fly discard removed the attempt's rows and cleared the marker
        /// (rewind design section 2.12 Rule 3, Discard restores <c>L</c>). Without it the guard
        /// would floor the pools up to the live values, keeping what the discarded attempt earned.
        /// </summary>
        internal static bool IsAuthoritativeReduction(
            bool isRewinding, bool hasReFlyMarker, bool hasMergeJournal,
            bool tombstonePath, bool rewindResourceAdjustmentInProgress, bool reFlyDiscard)
        {
            return reFlyDiscard
                || IsAuthoritativeReduction(isRewinding, hasReFlyMarker, hasMergeJournal,
                    tombstonePath, rewindResourceAdjustmentInProgress);
        }
    }
}
