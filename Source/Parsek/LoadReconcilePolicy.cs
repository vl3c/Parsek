using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// What a <see cref="ParsekScenario.OnLoad"/> is, as far as reconciling Parsek state goes.
    /// <see cref="LoadReconcilePolicy.ClassifyEarly"/> names the first five kinds in the OnLoad
    /// prologue; <see cref="LoadReconcilePolicy.ClassifyRefined"/> splits an in-session load into
    /// the last three once the revert detector has run.
    /// </summary>
    internal enum LoadKind
    {
        /// <summary>First load of a save folder in this session (game start, main menu, save-folder
        /// change, the test-batch crash-reconcile reload, the isolated batch baseline restore).</summary>
        Cold,
        /// <summary>Plain rewind (Rewind-to-Launch, Warp-to-game-start): <c>RewindContext.IsRewinding</c>.</summary>
        PlainRewind,
        /// <summary>Re-Fly invoke or Retry: <c>RewindInvokeContext.Pending</c>.</summary>
        ReFlyStart,
        /// <summary>The load Discard Re-fly issues (<see cref="DiscardReFlyLoadIntent"/>).</summary>
        DiscardReFly,
        /// <summary>A stock revert (<c>RevertDetector</c> consumed a revert kind, not a vessel switch).</summary>
        StockRevert,
        /// <summary>FLIGHT to FLIGHT with the clock moved backwards and no revert: F9 in flight.</summary>
        QuickloadFlight,
        /// <summary>Every other in-session load: scene change, same-instant reload, vessel-switch
        /// reload, F9 at the Space Center or Tracking Station (no clock stamp outside flight, so it
        /// reads as a scene change), F9 from the Space Center into a flight quicksave.</summary>
        InSessionOther,
    }

    /// <summary>
    /// The load kind as known in the OnLoad prologue, before the revert detector runs. The
    /// three refined in-session kinds share <see cref="InSession"/>; every state the prologue
    /// consumes must therefore have one decision for all three
    /// (<see cref="LoadReconcilePolicy.DecideEarly"/>).
    /// </summary>
    internal enum EarlyLoadKind
    {
        Cold,
        PlainRewind,
        ReFlyStart,
        DiscardReFly,
        InSession,
    }

    /// <summary>
    /// A piece of Parsek state a load must take a position on. The first nine are the staging
    /// nodes <c>ParsekScenario.LoadRewindStagingState</c> reads (see
    /// <see cref="LoadReconcilePolicy.StagingNodeCategories"/>).
    /// </summary>
    internal enum LoadStateCategory
    {
        SupersedeRows,
        RewindRetirements,
        LedgerTombstones,
        MergeJournal,
        RewindPoints,
        ReFlyMarker,
        TestBatchMarker,
        SwitchSegmentSession,
        StockActionIntent,
        /// <summary>Tagged game-state events of a resumed tree's recordings after the resume UT.</summary>
        AbandonedFutureEvents,
        /// <summary>Recording-tagged ledger rows of a resumed tree's recordings after the resume UT.</summary>
        AbandonedFutureLedgerRows,
        /// <summary>Terminal state and crew end states a resumed tree's members carry from after the resume UT.</summary>
        AbandonedFutureEndStates,
        /// <summary>Ledger rows with no recording id (KSC actions, launch-pad earnings) after the load's cutoff.</summary>
        UntaggedLedgerRowsAfterCutoff,
        /// <summary><c>GameStateRecorder.PendingScienceSubjects</c> (never serialized).</summary>
        PendingScience,
        Routes,
        CrewReplacements,
        KerbalSlots,
        GroupHierarchy,
        Missions,
        Milestones,
    }

    /// <summary>What a load does with one category today.</summary>
    internal enum LoadReconcileAction
    {
        /// <summary>The load installs the copy the loaded save carries (for state kept in an
        /// external Parsek file, the copy a cold load reads).</summary>
        Save,
        /// <summary>The in-memory state survives; the load ignores the save's copy.</summary>
        Memory,
        /// <summary>The plain-rewind carry reinstalls the in-memory copy captured when the rewind started.</summary>
        RewindCarry,
        /// <summary>The Re-Fly reconciliation bundle, captured before the load, replaces the state after it.</summary>
        Bundle,
        /// <summary>Kept from memory and reconciled against the load's cutoff UT.</summary>
        ReconcileAtCutoff,
        /// <summary>Split by owner: <c>ParsekScenario.ApplyInSessionStagedStateHandoffStepB</c>
        /// keeps memory's copy for points of trees committed in memory, the loaded copy for every
        /// other point, and both sides' session-scoped points
        /// (<c>InSessionStagedStateHandoff.MergeRewindPointsByOwner</c>).</summary>
        OwnerPartition,
        /// <summary>Reconciled when the recorder resumes the restored tree:
        /// <c>ParsekScenario.TrimAndReconcileForQuickloadResume</c> retires the trimmed set's
        /// state from after the resume UT (the abandoned future).</summary>
        ReconcileAtResume,
        /// <summary>Rows after the load's cutoff are removed.</summary>
        Prune,
        /// <summary>The state is emptied or nulled.</summary>
        Clear,
        /// <summary>The load leaves the state as it is: no save copy is read and nothing reconciles
        /// it (the reason says when the cell is not reachable at all).</summary>
        Keep,
    }

    /// <summary>
    /// One cell of the policy table. <see cref="Action"/> is what the code does TODAY, so a
    /// consumer can switch on it without changing behaviour. <see cref="KnownGapTodoId"/> is
    /// non-null when today's behaviour is a filed defect: the fix flips <see cref="Action"/> to
    /// the target and drops the id. <see cref="Reason"/> names the owning code by method and
    /// any runtime condition that further gates the action.
    /// </summary>
    internal readonly struct LoadReconcileDecision : IEquatable<LoadReconcileDecision>
    {
        internal readonly LoadReconcileAction Action;
        internal readonly string KnownGapTodoId;
        internal readonly string Reason;

        internal LoadReconcileDecision(LoadReconcileAction action, string knownGapTodoId, string reason)
        {
            Action = action;
            KnownGapTodoId = knownGapTodoId;
            Reason = reason;
        }

        internal bool IsKnownGap => KnownGapTodoId != null;

        public bool Equals(LoadReconcileDecision other)
        {
            return Action == other.Action
                && string.Equals(KnownGapTodoId, other.KnownGapTodoId, StringComparison.Ordinal)
                && string.Equals(Reason, other.Reason, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is LoadReconcileDecision other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int h = (int)Action;
                h = h * 397 ^ (KnownGapTodoId?.GetHashCode() ?? 0);
                h = h * 397 ^ (Reason?.GetHashCode() ?? 0);
                return h;
            }
        }

        public override string ToString()
        {
            return Action + (KnownGapTodoId != null ? " [gap " + KnownGapTodoId + "]" : "")
                + ": " + (Reason ?? "");
        }
    }

    /// <summary>A file that calls <c>GamePersistence.LoadGame(</c>, and what its loads classify as.</summary>
    internal readonly struct KnownLoadInitiator
    {
        internal readonly int CallCount;
        internal readonly IReadOnlyList<EarlyLoadKind> Kinds;
        internal readonly string Note;

        internal KnownLoadInitiator(int callCount, EarlyLoadKind[] kinds, string note)
        {
            CallCount = callCount;
            Kinds = kinds;
            Note = note;
        }
    }

    /// <summary>
    /// The load-kind x state-category policy for <see cref="ParsekScenario.OnLoad"/>: where each
    /// listed piece of Parsek state comes from on each kind of load, as the code does it today,
    /// lives in <see cref="Decide"/>. Pure: no Unity or KSP calls. <c>LoadReconcilePolicyTests</c>
    /// pins the table shape, the classifiers and the known-gap ids against the todo file;
    /// <c>LoadReconcileWiringGateTests</c> pins the declared <c>GamePersistence.LoadGame(</c> call
    /// sites, the node names of the two staging methods, and where OnLoad classifies relative to
    /// named steps. A state category with no staging node, or a load that reuses a declared
    /// initiator, is not caught mechanically.
    /// </summary>
    internal static class LoadReconcilePolicy
    {
        internal const string GapColdLoadAbandonedFuture = "COLD-LOAD-INTO-OLDER-FLIGHT-SAVE-KEEPS-ABANDONED-FUTURE";

        internal const string LogTag = "LoadPolicy";

        /// <summary>
        /// Every node name <c>ParsekScenario.LoadRewindStagingState</c> reads or
        /// <c>SaveRewindStagingState</c> writes, with the category that owns it.
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, LoadStateCategory> StagingNodeCategories =
            new Dictionary<string, LoadStateCategory>(StringComparer.Ordinal)
            {
                { "REWIND_POINTS", LoadStateCategory.RewindPoints },
                { "RECORDING_SUPERSEDES", LoadStateCategory.SupersedeRows },
                { "RECORDING_REWIND_RETIREMENTS", LoadStateCategory.RewindRetirements },
                { "LEDGER_TOMBSTONES", LoadStateCategory.LedgerTombstones },
                { ReFlySessionMarker.NodeName, LoadStateCategory.ReFlyMarker },
                { MergeJournal.NodeName, LoadStateCategory.MergeJournal },
                { StockActionIntentMarker.NodeName, LoadStateCategory.StockActionIntent },
                { SwitchSegmentSession.NodeName, LoadStateCategory.SwitchSegmentSession },
                { TestBatchMarker.NodeName, LoadStateCategory.TestBatchMarker },
            };

        /// <summary>
        /// Every file under <c>Source/Parsek</c> (repo-relative to it, forward slashes) that calls
        /// <c>GamePersistence.LoadGame(</c>, with the number of real calls (comments and string
        /// literals excluded) and the early kinds its loads classify as.
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, KnownLoadInitiator> KnownLoadInitiators =
            new Dictionary<string, KnownLoadInitiator>(StringComparer.Ordinal)
            {
                {
                    "ParsekScenario.cs",
                    new KnownLoadInitiator(1, new[] { EarlyLoadKind.Cold },
                        "DeferredReloadAfterTestBatchCrashReconcile; PrepareInMemoryStateForTestBatchCrashReload set initialLoadDone=false first")
                },
                {
                    "RecordingStore.cs",
                    new KnownLoadInitiator(1, new[] { EarlyLoadKind.PlainRewind },
                        "ExecuteRewindSaveLoad (Rewind-to-Launch, Warp-to-game-start); the rewind context is armed before the load")
                },
                {
                    "RewindInvoker.cs",
                    new KnownLoadInitiator(1, new[] { EarlyLoadKind.ReFlyStart },
                        "StartInvoke; RewindInvokeContext.Pending is set before the load")
                },
                {
                    "RevertInterceptor.cs",
                    new KnownLoadInitiator(1, new[] { EarlyLoadKind.DiscardReFly },
                        "DiscardReFlyHandler via TryLoadGameAndAssign; arms DiscardReFlyLoadIntent after a successful load")
                },
                {
                    "TestCommands/ParsekTestCommandAddon.cs",
                    new KnownLoadInitiator(1, new[] { EarlyLoadKind.InSession, EarlyLoadKind.Cold },
                        "the LoadGame seam verb; Cold when save= names another save folder (DetectSaveFolderChange)")
                },
                {
                    "InGameTests/Helpers/QuickloadResumeHelpers.cs",
                    new KnownLoadInitiator(2, new[] { EarlyLoadKind.InSession, EarlyLoadKind.Cold },
                        "in-game quickload helpers; Cold after PrepareForIsolatedBatchFlightBaselineRestore (batch baseline restore)")
                },
            };

        // ------------------------------------------------------------------
        // Classification
        // ------------------------------------------------------------------

        /// <summary>
        /// Prologue classification, in OnLoad's own precedence: the cold branch runs whenever
        /// <c>initialLoadDone</c> is false; the in-session branch returns through
        /// <c>HandleRewindOnLoad</c> before the Re-Fly post-load dispatch while rewinding; the Re-Fly
        /// post-load dispatch runs whatever else the load is. Two combinations the code supports
        /// but no initiator produces (a rewind or a Re-Fly invoke whose own load finds
        /// <c>initialLoadDone</c> false) classify Cold even though the prologue's rewind carry or
        /// the cold path's Re-Fly dispatch would still run.
        /// </summary>
        internal static EarlyLoadKind ClassifyEarly(
            bool initialLoadDone, bool isRewinding, bool reFlyInvokePending, bool discardReFlyPending)
        {
            if (!initialLoadDone) return EarlyLoadKind.Cold;
            if (isRewinding) return EarlyLoadKind.PlainRewind;
            if (reFlyInvokePending) return EarlyLoadKind.ReFlyStart;
            if (discardReFlyPending) return EarlyLoadKind.DiscardReFly;
            return EarlyLoadKind.InSession;
        }

        /// <summary>
        /// Splits an in-session load once the revert detector has run; every other early kind
        /// is already final. A revert wins over the clock signal because the revert branch owns
        /// the load (<c>ShouldRunQuickloadDiscard</c> refuses on a revert). A ReFlyStart or
        /// DiscardReFly load keeps its kind even when the in-session branch reads isRevert (a
        /// revert kind <c>RevertDetector</c> still holds because its own load never reached the
        /// in-session branch), so the revert branch keeps keying on isRevert, not on this kind.
        /// </summary>
        internal static LoadKind ClassifyRefined(
            EarlyLoadKind early, bool isRevert, bool isFlightToFlight, bool utWentBackwards)
        {
            if (early != EarlyLoadKind.InSession)
                return ToLoadKind(early);
            if (isRevert) return LoadKind.StockRevert;
            if (isFlightToFlight && utWentBackwards) return LoadKind.QuickloadFlight;
            return LoadKind.InSessionOther;
        }

        /// <summary>The refined kind of an early kind that needs no refinement.</summary>
        internal static LoadKind ToLoadKind(EarlyLoadKind early)
        {
            switch (early)
            {
                case EarlyLoadKind.Cold: return LoadKind.Cold;
                case EarlyLoadKind.PlainRewind: return LoadKind.PlainRewind;
                case EarlyLoadKind.ReFlyStart: return LoadKind.ReFlyStart;
                case EarlyLoadKind.DiscardReFly: return LoadKind.DiscardReFly;
                case EarlyLoadKind.InSession:
                    throw new ArgumentException(
                        "InSession needs ClassifyRefined: the revert detector decides the kind", nameof(early));
            }
            throw new ArgumentOutOfRangeException(nameof(early), early, "unknown EarlyLoadKind");
        }

        /// <summary>The once-per-load classification line (logged under <see cref="LogTag"/>).</summary>
        internal static string FormatClassificationLine(
            EarlyLoadKind early, LoadKind refined, string scene,
            bool initialLoadDone, bool rewinding, bool reFlyInvoke, string discardReFlyTarget,
            string handoff)
        {
            return "Load classified: early=" + early
                + " refined=" + refined
                + " scene=" + (string.IsNullOrEmpty(scene) ? "<none>" : scene)
                + " initialLoadDone=" + (initialLoadDone ? "true" : "false")
                + " rewinding=" + (rewinding ? "true" : "false")
                + " reFlyInvoke=" + (reFlyInvoke ? "true" : "false")
                + " discardReFly=" + (string.IsNullOrEmpty(discardReFlyTarget) ? "none" : discardReFlyTarget)
                + " handoff=" + (string.IsNullOrEmpty(handoff) ? "absent" : handoff);
        }

        // ------------------------------------------------------------------
        // Decisions
        // ------------------------------------------------------------------

        /// <summary>
        /// The categories OnLoad consumes in its prologue (<c>LoadCrewAndGroupState</c>,
        /// <c>LoadRewindStagingState</c> and the rewind carries), before the revert detector runs.
        /// </summary>
        internal static bool IsConsumedEarly(LoadStateCategory category)
        {
            switch (category)
            {
                case LoadStateCategory.SupersedeRows:
                case LoadStateCategory.RewindRetirements:
                case LoadStateCategory.LedgerTombstones:
                case LoadStateCategory.MergeJournal:
                case LoadStateCategory.RewindPoints:
                case LoadStateCategory.ReFlyMarker:
                case LoadStateCategory.TestBatchMarker:
                case LoadStateCategory.SwitchSegmentSession:
                case LoadStateCategory.StockActionIntent:
                case LoadStateCategory.CrewReplacements:
                case LoadStateCategory.KerbalSlots:
                case LoadStateCategory.GroupHierarchy:
                    return true;
                case LoadStateCategory.AbandonedFutureEvents:
                case LoadStateCategory.AbandonedFutureLedgerRows:
                case LoadStateCategory.AbandonedFutureEndStates:
                case LoadStateCategory.UntaggedLedgerRowsAfterCutoff:
                case LoadStateCategory.PendingScience:
                case LoadStateCategory.Routes:
                case LoadStateCategory.Missions:
                case LoadStateCategory.Milestones:
                    return false;
            }
            throw new ArgumentOutOfRangeException(nameof(category), category, "unknown LoadStateCategory");
        }

        /// <summary>
        /// The decision for an early kind. <see cref="EarlyLoadKind.InSession"/> answers with the
        /// decision all three refined in-session kinds share, and throws when they differ (a
        /// consumer that runs before the revert detector could not pick the right one).
        /// </summary>
        internal static LoadReconcileDecision DecideEarly(EarlyLoadKind early, LoadStateCategory category)
        {
            if (early != EarlyLoadKind.InSession)
                return Decide(ToLoadKind(early), category);

            var revert = Decide(LoadKind.StockRevert, category);
            var quickload = Decide(LoadKind.QuickloadFlight, category);
            var other = Decide(LoadKind.InSessionOther, category);
            if (!revert.Equals(quickload) || !revert.Equals(other))
            {
                throw new InvalidOperationException(
                    "LoadReconcilePolicy.DecideEarly: the refined in-session kinds disagree on "
                    + category + " (StockRevert=" + revert + "; QuickloadFlight=" + quickload
                    + "; InSessionOther=" + other + "); it cannot be decided before the revert detector runs");
            }
            return revert;
        }

        /// <summary>
        /// Today's decision for one (kind, category) cell. Exhaustive: an unknown kind or
        /// category throws <see cref="ArgumentOutOfRangeException"/>.
        /// </summary>
        internal static LoadReconcileDecision Decide(LoadKind kind, LoadStateCategory category)
        {
            switch (category)
            {
                case LoadStateCategory.SupersedeRows:
                case LoadStateCategory.RewindRetirements:
                case LoadStateCategory.LedgerTombstones:
                    return DecideStagedList(kind, category);
                case LoadStateCategory.MergeJournal: return DecideMergeJournal(kind);
                case LoadStateCategory.RewindPoints: return DecideRewindPoints(kind);
                case LoadStateCategory.ReFlyMarker: return DecideReFlyMarker(kind);
                case LoadStateCategory.TestBatchMarker: return DecideTestBatchMarker(kind);
                case LoadStateCategory.SwitchSegmentSession: return DecideSwitchSegmentSession(kind);
                case LoadStateCategory.StockActionIntent: return DecideStockActionIntent(kind);
                case LoadStateCategory.AbandonedFutureEvents: return DecideAbandonedFutureEvents(kind);
                case LoadStateCategory.AbandonedFutureLedgerRows: return DecideAbandonedFutureLedgerRows(kind);
                case LoadStateCategory.AbandonedFutureEndStates: return DecideAbandonedFutureEndStates(kind);
                case LoadStateCategory.UntaggedLedgerRowsAfterCutoff: return DecideUntaggedLedgerRows(kind);
                case LoadStateCategory.PendingScience: return DecidePendingScience(kind);
                case LoadStateCategory.Routes: return DecideRoutes(kind);
                case LoadStateCategory.CrewReplacements: return DecideCrewReplacements(kind);
                case LoadStateCategory.KerbalSlots: return DecideKerbalSlots(kind);
                case LoadStateCategory.GroupHierarchy: return DecideGroupHierarchy(kind);
                case LoadStateCategory.Missions: return DecideMissions(kind);
                case LoadStateCategory.Milestones: return DecideMilestones(kind);
            }
            throw new ArgumentOutOfRangeException(nameof(category), category, "unknown LoadStateCategory");
        }

        private static LoadReconcileDecision Today(LoadReconcileAction action, string reason)
        {
            return new LoadReconcileDecision(action, null, reason);
        }

        private static LoadReconcileDecision Gap(LoadReconcileAction action, string todoId, string reason)
        {
            return new LoadReconcileDecision(action, todoId, reason);
        }

        private static ArgumentOutOfRangeException UnknownKind(LoadKind kind)
        {
            return new ArgumentOutOfRangeException(nameof(kind), kind, "unknown LoadKind");
        }

        private const string StagingFromNode =
            "ParsekScenario.LoadRewindStagingState installs the loaded node's copy";
        private const string InSessionHandoffInstalls =
            "ParsekScenario.ApplyInSessionStagedStateHandoffStepA installs the copy the previous scenario "
            + "instance handed off when it was torn down (InSessionStagedStateHandoff, captured in OnDestroy) "
            + "over the loaded node's, as committed recordings and the ledger stay in memory too";
        private const string BundleRestores =
            "RewindInvoker.ConsumePostLoad -> ReconciliationBundle.Restore replaces the loaded copy with the pre-invoke capture";
        private const string SweepValidates =
            StagingFromNode + "; LoadTimeSweep.Run (MarkerValidator) validates it against memory";

        private static LoadReconcileDecision DecideStagedList(LoadKind kind, LoadStateCategory category)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                    return Today(LoadReconcileAction.Save, StagingFromNode);
                case LoadKind.PlainRewind:
                    return Today(LoadReconcileAction.RewindCarry,
                        "RecordingStore.ReinstallRewindCarriedStagedListsAfterLoad"
                        + (category == LoadStateCategory.SupersedeRows
                            ? "; RecordingStore.ReapplyRewindSupersedeDropAfterLoad then drops the rewound tree's rows"
                            : ""));
                case LoadKind.ReFlyStart:
                    return Today(LoadReconcileAction.Bundle, BundleRestores);
                case LoadKind.DiscardReFly:
                case LoadKind.StockRevert:
                case LoadKind.QuickloadFlight:
                case LoadKind.InSessionOther:
                    return Today(LoadReconcileAction.Memory,
                        InSessionHandoffInstalls + " (RecordingStore.MergeCarriedStagedList); "
                        + "ParsekScenario.ApplyInSessionStagedStateHandoffStepB then hands the rows naming a tree "
                        + "a quickload resumed from the save (the committed-copy restore, ResumeFromQuicksave) "
                        + "back to the save, except rows the loaded marker's resumed Re-Fly attempt wrote "
                        + "(owner ruling OQ-1)");
            }
            throw UnknownKind(kind);
        }

        private static LoadReconcileDecision DecideMergeJournal(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                    return Today(LoadReconcileAction.Save, StagingFromNode);
                case LoadKind.PlainRewind:
                    return Today(LoadReconcileAction.RewindCarry,
                        "RecordingStore.ReinstallRewindCarriedStagedListsAfterLoad, when ShouldCarryMergeJournal "
                        + "(capture null or Complete); otherwise the loaded journal stands");
                case LoadKind.ReFlyStart:
                    return Today(LoadReconcileAction.Bundle, BundleRestores);
                case LoadKind.DiscardReFly:
                    return Today(LoadReconcileAction.Memory,
                        InSessionHandoffInstalls + "; DiscardReFlyHandler nulled it, so none (ShouldCarryMergeJournal)");
                case LoadKind.StockRevert:
                case LoadKind.QuickloadFlight:
                case LoadKind.InSessionOther:
                    return Today(LoadReconcileAction.Memory,
                        InSessionHandoffInstalls + " when memory's is null or Complete (RecordingStore.ShouldCarryMergeJournal); "
                        + "an in-flight one keeps the loaded journal with a Warn");
            }
            throw UnknownKind(kind);
        }

        private static LoadReconcileDecision DecideRewindPoints(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                    return Today(LoadReconcileAction.Save, StagingFromNode);
                case LoadKind.PlainRewind:
                    return Today(LoadReconcileAction.RewindCarry,
                        "RecordingStore.ReinstallRewindCarriedRewindPointsAfterLoad");
                case LoadKind.ReFlyStart:
                    return Today(LoadReconcileAction.Bundle, BundleRestores);
                case LoadKind.DiscardReFly:
                case LoadKind.StockRevert:
                case LoadKind.QuickloadFlight:
                case LoadKind.InSessionOther:
                    return Today(LoadReconcileAction.OwnerPartition,
                        "ParsekScenario.ApplyInSessionStagedStateHandoffStepB, after the active-tree detach: memory's "
                        + "copy for points of trees committed in memory (DiscardReFlyHandler's origin-RP promotion "
                        + "included), the loaded copy for the resumed or reverted flight's and unknown owners' points "
                        + "(their quicksave files stay, owner ruling OQ-3), both sides' session-scoped points "
                        + "(LoadTimeSweep.Run then spares or purges them against the marker)");
            }
            throw UnknownKind(kind);
        }

        private static LoadReconcileDecision DecideReFlyMarker(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                    return Today(LoadReconcileAction.Save, SweepValidates);
                case LoadKind.PlainRewind:
                    return Today(LoadReconcileAction.Clear,
                        "ParsekScenario.HandleRewindOnLoad -> ClearActiveReFlyMarkerForPlainRewind");
                case LoadKind.ReFlyStart:
                    return Today(LoadReconcileAction.Bundle,
                        BundleRestores + ", then RewindInvoker.ConsumePostLoad writes the new session's marker");
                case LoadKind.DiscardReFly:
                    return Today(LoadReconcileAction.Memory,
                        InSessionHandoffInstalls + "; DiscardReFlyHandler cleared it, so step A clears the loaded one "
                        + "through ClearActiveReFlySessionMarker");
                case LoadKind.StockRevert:
                case LoadKind.QuickloadFlight:
                case LoadKind.InSessionOther:
                    return Today(LoadReconcileAction.Save, SweepValidates);
            }
            throw UnknownKind(kind);
        }

        private static LoadReconcileDecision DecideTestBatchMarker(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                case LoadKind.PlainRewind:
                case LoadKind.ReFlyStart:
                case LoadKind.DiscardReFly:
                case LoadKind.StockRevert:
                case LoadKind.QuickloadFlight:
                case LoadKind.InSessionOther:
                    return Today(LoadReconcileAction.Save,
                        StagingFromNode + "; ParsekScenario.RunTestBatchCrashReconcile reads it (not on the rewind branch)");
            }
            throw UnknownKind(kind);
        }

        private static LoadReconcileDecision DecideSwitchSegmentSession(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                case LoadKind.PlainRewind:
                case LoadKind.ReFlyStart:
                case LoadKind.DiscardReFly:
                case LoadKind.StockRevert:
                case LoadKind.QuickloadFlight:
                case LoadKind.InSessionOther:
                    return Today(LoadReconcileAction.Save, StagingFromNode);
            }
            throw UnknownKind(kind);
        }

        private static LoadReconcileDecision DecideStockActionIntent(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                case LoadKind.PlainRewind:
                case LoadKind.ReFlyStart:
                case LoadKind.DiscardReFly:
                case LoadKind.StockRevert:
                case LoadKind.QuickloadFlight:
                case LoadKind.InSessionOther:
                    return Today(LoadReconcileAction.Save,
                        StagingFromNode + ", then ValidateLoadedStockActionIntentFreshness clears a stale one");
            }
            throw UnknownKind(kind);
        }

        private const string RevertResumesNothing =
            "not reachable: the revert branch unstashes the pending tree (RecordingStore.UnstashPendingTreeOnRevert) "
            + "before the Limbo dispatch, so no quickload resume or trim is armed";
        private const string DiscardResumesNothing =
            "not reachable: DiscardReFlyHandler arms RecordingStore.ArmNextActiveTreeRestoreSuppression, "
            + "so the load restores no active tree and arms no trim";
        private const string RewindResumesNothing =
            "not reachable: TryRestoreActiveTreeNode returns early while rewinding, so no quickload trim is armed";
        private const string ResumeTrimOnlyCutsTrajectories =
            "the quickload resume trim (ParsekScenario.TrimRecordingTreePastUT) cuts trajectories only";
        private const string ResumeReconcileSkips =
            "ParsekScenario.TrimAndReconcileForQuickloadResume runs the trim but skips this category on this kind";

        private static LoadReconcileDecision DecideAbandonedFutureEvents(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                    return Gap(LoadReconcileAction.Keep, GapColdLoadAbandonedFuture,
                        "GameStateStore.LoadEventFile reads the external file as last written, on the first cold load "
                        + "of a save folder only (memory after); " + ResumeTrimOnlyCutsTrajectories
                        + " (COLD-LOAD-INTO-OLDER-FLIGHT-SAVE-KEEPS-ABANDONED-FUTURE; the in-session fix does not cover it)");
                case LoadKind.PlainRewind:
                    return Today(LoadReconcileAction.Keep, RewindResumesNothing);
                case LoadKind.ReFlyStart:
                    return Today(LoadReconcileAction.Keep,
                        "the event store is not reloaded in session and no Re-Fly path purges by UT; "
                        + ResumeReconcileSkips);
                case LoadKind.DiscardReFly:
                    return Today(LoadReconcileAction.Keep, DiscardResumesNothing
                        + "; the attempt's own events were purged by MergeDialog.PruneActiveReFlyAttemptOwnedTopology");
                case LoadKind.StockRevert:
                    return Today(LoadReconcileAction.Keep, RevertResumesNothing
                        + "; the reverted flight's events stay for an F9 back, hidden by recording-id visibility");
                case LoadKind.QuickloadFlight:
                case LoadKind.InSessionOther:
                    return Today(LoadReconcileAction.ReconcileAtResume,
                        "FlightRecorder.PrepareQuickloadResumeStateIfNeeded -> "
                        + "ParsekScenario.TrimAndReconcileForQuickloadResume purges the trimmed set's tagged events "
                        + "after the resume UT (GameStateStore.PurgeEventsForRecordingAfterUT; every event of a pruned "
                        + "recording), never an untagged event or one of committed history (a recording still committed, "
                        + "or one the quicksave already shows as history: IsCommittedHistoryAtQuicksave, even when the "
                        + "trim prunes it)");
            }
            throw UnknownKind(kind);
        }

        private static LoadReconcileDecision DecideAbandonedFutureLedgerRows(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                    return Gap(LoadReconcileAction.Keep, GapColdLoadAbandonedFuture,
                        "LedgerOrchestrator.OnLoad reads the ledger file as last written; the cold active-tree restore runs "
                        + "before OnKspLoad, so Ledger.Reconcile counts the restored tree's ids as known "
                        + "(RecordingStore.BuildKnownRecordingIds) and keeps its earnings at any UT and its spendings in "
                        + "FLIGHT / SPACECENTER (COLD-LOAD-INTO-OLDER-FLIGHT-SAVE-KEEPS-ABANDONED-FUTURE; the in-session "
                        + "fix does not cover it)");
                case LoadKind.PlainRewind:
                    return Today(LoadReconcileAction.Keep, RewindResumesNothing);
                case LoadKind.ReFlyStart:
                    return Today(LoadReconcileAction.Bundle,
                        BundleRestores + " (only route rows after the loaded UT are retired); " + ResumeReconcileSkips);
                case LoadKind.DiscardReFly:
                    return Today(LoadReconcileAction.Keep, DiscardResumesNothing);
                case LoadKind.StockRevert:
                    return Today(LoadReconcileAction.Keep, RevertResumesNothing);
                case LoadKind.QuickloadFlight:
                case LoadKind.InSessionOther:
                    return Today(LoadReconcileAction.ReconcileAtResume,
                        "FlightRecorder.PrepareQuickloadResumeStateIfNeeded -> "
                        + "ParsekScenario.TrimAndReconcileForQuickloadResume -> Ledger.RetireAbandonedFutureActions "
                        + "removes the trimmed set's recording-tagged rows that happened after the resume UT, every row "
                        + "of a pruned recording and the KerbalAssignment row of a recording whose end state it cleared "
                        + "(owner ruling OQ-2; untagged KSC rows, route rows, seeds and committed history stay: a "
                        + "recording still committed, or one the quicksave already shows as history, "
                        + "IsCommittedHistoryAtQuicksave, even when the trim prunes it)");
            }
            throw UnknownKind(kind);
        }

        private static LoadReconcileDecision DecideAbandonedFutureEndStates(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                    return Today(LoadReconcileAction.Save,
                        "TryRestoreActiveTreeNode restores the tree from the node and splices only from committed "
                        + "trees LoadRecordingTrees read from that same node; " + ResumeReconcileSkips);
                case LoadKind.PlainRewind:
                    return Today(LoadReconcileAction.Keep, RewindResumesNothing);
                case LoadKind.ReFlyStart:
                    return Today(LoadReconcileAction.Keep,
                        ResumeReconcileSkips + ": the Re-Fly load resumes under the ActiveRecOnly scope once "
                        + "AtomicMarkerWrite refreshes it, the active recording is the session's fresh provisional, "
                        + "and the supersede merge owns the re-flown flight's history");
                case LoadKind.DiscardReFly:
                    return Today(LoadReconcileAction.Keep, DiscardResumesNothing);
                case LoadKind.StockRevert:
                    return Today(LoadReconcileAction.Keep, RevertResumesNothing);
                case LoadKind.QuickloadFlight:
                case LoadKind.InSessionOther:
                    return Today(LoadReconcileAction.ReconcileAtResume,
                        "FlightRecorder.PrepareQuickloadResumeStateIfNeeded -> "
                        + "ParsekScenario.TrimAndReconcileForQuickloadResume clears the terminal and crew end states "
                        + "of every trimmed recording that ended after the resume UT (not committed history: a recording "
                        + "still committed, or one the quicksave already shows as history, IsCommittedHistoryAtQuicksave)");
            }
            throw UnknownKind(kind);
        }

        private static LoadReconcileDecision DecideUntaggedLedgerRows(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                    return Today(LoadReconcileAction.Keep,
                        "scene rule: LedgerOrchestrator.OnKspLoad -> Ledger.Reconcile never drops an untagged earning "
                        + "by UT (contract completions aside); outside FLIGHT / SPACECENTER with a ready clock (UT > 0) "
                        + "it drops untagged spendings, contract lifecycle rows and other untagged rows past the clock; "
                        + "FLIGHT / SPACECENTER, or a clock not yet ready, keep them all (the Tracking Station half is "
                        + "TRACKING-STATION-LEDGER-CUTOFF-ALIGN)");
                case LoadKind.PlainRewind:
                    return Today(LoadReconcileAction.Keep,
                        "ParsekScenario.HandleRewindOnLoad retires route rows only; the recalculation walks to the cutoff");
                case LoadKind.ReFlyStart:
                    return Today(LoadReconcileAction.Bundle, BundleRestores);
                case LoadKind.DiscardReFly:
                    return Today(LoadReconcileAction.Keep,
                        "the revert prune runs only when the in-session branch reads isRevert");
                case LoadKind.StockRevert:
                    return Today(LoadReconcileAction.Prune,
                        "ParsekScenario.OnLoad revert branch -> Ledger.PruneOrphanActionsAfterUT at ResolveRevertPruneCutoff");
                case LoadKind.QuickloadFlight:
                case LoadKind.InSessionOther:
                    return Today(LoadReconcileAction.Keep,
                        "a quickload keeps untagged KSC rows (QL-R2, designed)");
            }
            throw UnknownKind(kind);
        }

        private static LoadReconcileDecision DecidePendingScience(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                    return Today(LoadReconcileAction.Keep,
                        "cleared wholesale only when DiscardStalePendingState discards a stale pending tree "
                        + "(RecordingStore.DiscardPendingTree abandon path)");
                case LoadKind.PlainRewind:
                    return Today(LoadReconcileAction.Keep, "the rewind branch does not touch it");
                case LoadKind.ReFlyStart:
                    return Today(LoadReconcileAction.Bundle,
                        BundleRestores + " (entries captured after the loaded UT are dropped)");
                case LoadKind.DiscardReFly:
                    return Today(LoadReconcileAction.Clear,
                        "RevertInterceptor.DiscardReFlyHandler, before its load, through the merge-dialog discard's "
                        + "session-state half (MergeDialog.EndDiscardedReFlySession)");
                case LoadKind.StockRevert:
                    return Today(LoadReconcileAction.Clear, "RecordingStore.UnstashPendingTreeOnRevert, unconditionally");
                case LoadKind.QuickloadFlight:
                    return Today(LoadReconcileAction.Clear,
                        "ParsekScenario.DiscardStashedOnQuickload; skipped while the loaded save carries a Re-Fly marker "
                        + "(ShouldRunQuickloadDiscard)");
                case LoadKind.InSessionOther:
                    return Today(LoadReconcileAction.Keep, "the scene-change path does not touch it");
            }
            throw UnknownKind(kind);
        }

        private static LoadReconcileDecision DecideRoutes(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                    return Today(LoadReconcileAction.Save, "RouteStore.LoadRoutesFrom + RevalidateSources on the cold path");
                case LoadKind.PlainRewind:
                    return Today(LoadReconcileAction.ReconcileAtCutoff,
                        "ParsekScenario.HandleRewindOnLoad -> Ledger.RetireFutureRouteActionsAtRewind + "
                        + "RouteRewindClassifier.ReconcileStoreAtRewind at RewindAdjustedUT, then each kept route "
                        + "takes its loop position back from the rewind save's own route copy (read from the parsed "
                        + "save in RecordingStore.ExecuteRewindSaveLoad; the OnLoad node is persistent.sfs)");
                case LoadKind.ReFlyStart:
                    return Today(LoadReconcileAction.Bundle,
                        BundleRestores + " and reconciles routes at the loaded UT (RouteRewindClassifier.ReconcileStoreAtRewind), "
                        + "then each kept route takes its loop position back from the RP quicksave's own route copy "
                        + "(the OnLoad node)");
                case LoadKind.DiscardReFly:
                case LoadKind.StockRevert:
                case LoadKind.QuickloadFlight:
                    return Today(LoadReconcileAction.ReconcileAtCutoff, InSessionRouteReconcile);
                case LoadKind.InSessionOther:
                    return Today(LoadReconcileAction.ReconcileAtCutoff,
                        InSessionRouteReconcile + ", only when route state lies after the loaded save "
                        + "(an F9 at the Space Center or Tracking Station, or into a flight quicksave from the "
                        + "Space Center); a forward or same-instant scene change finds none and touches nothing");
            }
            throw UnknownKind(kind);
        }

        private const string InSessionRouteReconcile =
            "ParsekScenario.OnLoad -> RouteLoadReconcile.ReconcileAtInSessionLoad, before the recalculation: "
            + "Ledger.RetireFutureRouteActionsAtRewind + RouteRewindClassifier.ReconcileStoreAtRewind at the "
            + "loaded save's UT (a stock revert: the earlier of it and the revert prune's launch boundary), "
            + "then each kept route takes its loop position and owed recovery credit back from the loaded "
            + "save's own route copy";

        private const string CrewAndSlotsFromNode =
            "ParsekScenario.LoadCrewAndGroupState reads the loaded node";
        private const string CrewSkippedOnRewind =
            "ParsekScenario.LoadCrewAndGroupState returns early while rewinding";

        private static LoadReconcileDecision DecideCrewReplacements(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                case LoadKind.DiscardReFly:
                case LoadKind.StockRevert:
                case LoadKind.QuickloadFlight:
                case LoadKind.InSessionOther:
                    return Today(LoadReconcileAction.Save,
                        CrewAndSlotsFromNode + " (CrewReservationManager.LoadCrewReplacements)");
                case LoadKind.PlainRewind:
                    return Today(LoadReconcileAction.Memory, CrewSkippedOnRewind);
                case LoadKind.ReFlyStart:
                    return Today(LoadReconcileAction.Bundle, BundleRestores);
            }
            throw UnknownKind(kind);
        }

        private static LoadReconcileDecision DecideKerbalSlots(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                case LoadKind.ReFlyStart:
                case LoadKind.DiscardReFly:
                case LoadKind.StockRevert:
                case LoadKind.QuickloadFlight:
                case LoadKind.InSessionOther:
                    return Today(LoadReconcileAction.Save,
                        CrewAndSlotsFromNode + " (LedgerOrchestrator.Kerbals.LoadSlots; the Re-Fly bundle does not carry slots)");
                case LoadKind.PlainRewind:
                    return Today(LoadReconcileAction.Memory, CrewSkippedOnRewind);
            }
            throw UnknownKind(kind);
        }

        private static LoadReconcileDecision DecideGroupHierarchy(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                    return Today(LoadReconcileAction.Save,
                        "ParsekScenario.LoadCrewAndGroupState (ShouldLoadGroupHierarchyFromSave: cold, not rewinding)");
                case LoadKind.PlainRewind:
                    return Today(LoadReconcileAction.Memory, CrewSkippedOnRewind);
                case LoadKind.ReFlyStart:
                    return Today(LoadReconcileAction.Bundle, BundleRestores);
                case LoadKind.DiscardReFly:
                case LoadKind.StockRevert:
                case LoadKind.QuickloadFlight:
                case LoadKind.InSessionOther:
                    return Today(LoadReconcileAction.Memory,
                        "ParsekScenario.ShouldLoadGroupHierarchyFromSave is false in session");
            }
            throw UnknownKind(kind);
        }

        private static LoadReconcileDecision DecideMissions(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                    return Today(LoadReconcileAction.Save, "MissionStore.Load on the cold path");
                case LoadKind.PlainRewind:
                case LoadKind.ReFlyStart:
                case LoadKind.DiscardReFly:
                case LoadKind.StockRevert:
                case LoadKind.QuickloadFlight:
                case LoadKind.InSessionOther:
                    return Today(LoadReconcileAction.Memory,
                        "MissionStore is loaded on the cold path only (the Re-Fly bundle does not carry it)");
            }
            throw UnknownKind(kind);
        }

        private static LoadReconcileDecision DecideMilestones(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                    return Today(LoadReconcileAction.Save,
                        "MilestoneStore.LoadMilestoneFile (first cold load of a save folder) + RestoreMutableState(node)");
                case LoadKind.PlainRewind:
                    return Today(LoadReconcileAction.Save,
                        "list kept in memory; ParsekScenario.HandleRewindOnLoad -> MilestoneStore.RestoreMutableState(node, resetUnmatched: true)");
                case LoadKind.ReFlyStart:
                    return Today(LoadReconcileAction.Bundle, BundleRestores);
                case LoadKind.StockRevert:
                    return Today(LoadReconcileAction.Save,
                        "list kept in memory; revert branch -> MilestoneStore.RestoreMutableState(node, resetUnmatched: true)");
                case LoadKind.DiscardReFly:
                case LoadKind.QuickloadFlight:
                case LoadKind.InSessionOther:
                    return Today(LoadReconcileAction.Save,
                        "list kept in memory; scene-change branch -> MilestoneStore.RestoreMutableState(node)");
            }
            throw UnknownKind(kind);
        }
    }
}
