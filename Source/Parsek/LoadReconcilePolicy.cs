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
        /// <summary>A load that rebuilds Parsek from the save: game start, main menu, save-folder
        /// change, the test-batch crash-reconcile reload, the isolated batch baseline restore, and
        /// every load of a save file stock announced (<see cref="SaveFileLoadIntent"/>: F9, the
        /// pause-menu Load) that is not a revert (design-load-restores-the-save.md 4.1).</summary>
        Cold,
        /// <summary>Plain rewind (Rewind-to-Launch, Warp-to-game-start): <c>RewindContext.IsRewinding</c>.</summary>
        PlainRewind,
        /// <summary>Re-Fly invoke or Retry: <c>RewindInvokeContext.Pending</c>.</summary>
        ReFlyStart,
        /// <summary>The load Discard Re-fly issues (<see cref="DiscardReFlyLoadIntent"/>).</summary>
        DiscardReFly,
        /// <summary>A stock revert (<c>RevertDetector</c> consumed a revert kind, not a vessel switch).</summary>
        StockRevert,
        /// <summary>FLIGHT to FLIGHT with the clock moved backwards and no revert. No longer
        /// produced by stock F9, which is a save-file load and classifies <see cref="Cold"/>:
        /// only a load no stock path announced reaches it (a harness <c>LoadGame</c> without
        /// <c>playerLoad=true</c>, another mod's loader; the saveStamp canary warns on it).</summary>
        QuickloadFlight,
        /// <summary>Every other in-session load: scene change, same-instant reload, vessel-switch
        /// reload, Tracking Station Fly, and a load of a save file no stock path announced (the
        /// saveStamp canary warns on it). Stock F9 and the pause-menu Load classify
        /// <see cref="Cold"/>.</summary>
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
        /// <summary><c>VesselNameStore</c>: the player's vessel name decisions (design-vessel-and-mission-names.md 7.6).</summary>
        VesselNames,
        /// <summary><c>StockPoolReference</c>: the drawdown guard's per-pool last written value,
        /// counted rows (committed-value rule plan 2.4), saved in the PARSEK node; kept leaks are
        /// <c>StockPoolAdjustment</c> ledger rows.</summary>
        StockPoolReference,
        /// <summary><c>RewindVesselHold</c>: the vessels the last plain rewind held back for their
        /// committed future (rewind design 10.13), with the rewind save's copy of each, saved in
        /// the PARSEK node.</summary>
        RewindVesselHolds,
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
        /// <summary>Rows after the load's cutoff are removed.</summary>
        Prune,
        /// <summary>The state is emptied or nulled.</summary>
        Clear,
        /// <summary>The load leaves the state as it is: no save copy is read and nothing reconciles
        /// it (the reason says when the cell is not reachable at all).</summary>
        Keep,
        /// <summary>The in-memory state survives, minus what the cancelled flight added: the
        /// vessel name decisions a stock rename made inside the reverted flight (or the
        /// discarded Re-Fly attempt) are undone, newest first, after the revert detector ran
        /// (<c>VesselNameStore.UndoCurrentFlightEpoch</c>; design-vessel-and-mission-names.md
        /// 7.6, ruling 5).</summary>
        MemoryUndoRevertedFlight,
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
                        "the LoadGame seam verb; Cold when save= names another save folder (DetectSaveFolderChange) "
                        + "or when the verb announces the load as the player's (playerLoad=true fires "
                        + "onGameStatePostLoad, a save-file load)")
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
        /// post-load dispatch runs whatever else the load is. A load of a save file stock announced
        /// (<paramref name="saveFileLoadPending"/>, <see cref="SaveFileLoadIntent"/>) that is not a
        /// revert (<paramref name="revertPending"/>: <c>RevertDetector</c> armed, read without
        /// consuming it; stock fires the save-file event inside a revert too) classifies Cold after
        /// the rewind, Re-Fly start and Discard Re-fly checks (design-load-restores-the-save.md 4.1);
        /// OnLoad then sets <c>initialLoadDone</c> false so the cold branch runs. A rewind or a Re-Fly
        /// invoke whose own load finds <c>initialLoadDone</c> false (no initiator produces it; only a
        /// cold load that threw earlier leaves the flag false) classifies Cold, and the save-scoped
        /// reset then clears its context as stale (GC1-SAVE-SCOPED-STATE-LEFTOVERS), so neither the
        /// prologue's rewind carry nor the cold path's Re-Fly dispatch runs for it.
        /// </summary>
        internal static EarlyLoadKind ClassifyEarly(
            bool initialLoadDone, bool isRewinding, bool reFlyInvokePending, bool discardReFlyPending,
            bool saveFileLoadPending, bool revertPending)
        {
            if (!initialLoadDone) return EarlyLoadKind.Cold;
            if (isRewinding) return EarlyLoadKind.PlainRewind;
            if (reFlyInvokePending) return EarlyLoadKind.ReFlyStart;
            if (discardReFlyPending) return EarlyLoadKind.DiscardReFly;
            if (saveFileLoadPending && !revertPending) return EarlyLoadKind.Cold;
            return EarlyLoadKind.InSession;
        }

        internal const string ColdReasonBoot = "boot";
        internal const string ColdReasonMainMenu = "main-menu";
        internal const string ColdReasonFolderChange = "folder-change";
        internal const string ColdReasonSaveFile = "save-file";
        internal const string ColdReasonBatch = "batch";

        /// <summary>Every value <see cref="ResolveColdReason"/> returns for a Cold load.</summary>
        internal static readonly IReadOnlyList<string> ColdReasons = new[]
        {
            ColdReasonBoot, ColdReasonMainMenu, ColdReasonFolderChange, ColdReasonSaveFile, ColdReasonBatch,
        };

        /// <summary>
        /// Pure: why a load is Cold, or null for any other kind. A Cold load that found
        /// <c>initialLoadDone</c> true at classification is a save-file load (the only rule that
        /// classifies Cold past it). Otherwise: the first cold load of the process is the boot; a
        /// load after the main-menu transition finds no previous folder
        /// (<c>OnMainMenuTransition</c> nulls it); a different folder is a folder change
        /// (<c>DetectSaveFolderChange</c>); anything else re-armed the cold path in process (the
        /// in-game test batch's baseline restore or crash reconcile).
        /// </summary>
        internal static string ResolveColdReason(
            EarlyLoadKind early, bool initialLoadDoneAtClassify,
            int coldLoadsBefore, string saveFolderBeforeDetect, string currentSaveFolder)
        {
            if (early != EarlyLoadKind.Cold)
                return null;
            if (initialLoadDoneAtClassify)
                return ColdReasonSaveFile;
            if (coldLoadsBefore <= 0)
                return ColdReasonBoot;
            if (saveFolderBeforeDetect == null)
                return ColdReasonMainMenu;
            if (!string.Equals(saveFolderBeforeDetect, currentSaveFolder, StringComparison.Ordinal))
                return ColdReasonFolderChange;
            return ColdReasonBatch;
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

        /// <summary>The once-per-load classification line (logged under <see cref="LogTag"/>).
        /// <paramref name="coldReason"/> is null for a non-Cold load; <paramref name="saveFileLoad"/>
        /// is whether this load consumed a live save-file intent, with its frame age
        /// (<paramref name="intentAgeFrames"/>, -1 when none) and the loaded game's UT
        /// (<paramref name="intentNodeUT"/>, NaN when none).</summary>
        internal static string FormatClassificationLine(
            EarlyLoadKind early, LoadKind refined, string scene,
            bool initialLoadDone, bool rewinding, bool reFlyInvoke, string discardReFlyTarget,
            string handoff, string coldReason, bool saveFileLoad, bool revertPending,
            int intentAgeFrames, double intentNodeUT)
        {
            return "Load classified: early=" + early
                + " refined=" + refined
                + " scene=" + (string.IsNullOrEmpty(scene) ? "<none>" : scene)
                + " initialLoadDone=" + (initialLoadDone ? "true" : "false")
                + " rewinding=" + (rewinding ? "true" : "false")
                + " reFlyInvoke=" + (reFlyInvoke ? "true" : "false")
                + " discardReFly=" + (string.IsNullOrEmpty(discardReFlyTarget) ? "none" : discardReFlyTarget)
                + " handoff=" + (string.IsNullOrEmpty(handoff) ? "absent" : handoff)
                + " coldReason=" + (string.IsNullOrEmpty(coldReason) ? "none" : coldReason)
                + " saveFileLoad=" + (saveFileLoad ? "true" : "false")
                + " revertPending=" + (revertPending ? "true" : "false")
                + " intentAgeFrames=" + intentAgeFrames.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " nodeUT=" + (double.IsNaN(intentNodeUT) || double.IsInfinity(intentNodeUT)
                    ? "NaN"
                    : intentNodeUT.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        }

        // ------------------------------------------------------------------
        // The saveStamp canary (design 4.1)
        // ------------------------------------------------------------------

        /// <summary>The Parsek-node key OnSave writes a fresh stamp under.</summary>
        internal const string SaveStampKey = "saveStamp";

        /// <summary>
        /// Pure: the loads the canary checks. Only the two in-session kinds that read a node the
        /// previous OnSave wrote: a stock revert reads the launch or prelaunch snapshot (an older
        /// stamp by design), every other kind is not an in-session read of the last save.
        /// </summary>
        internal static bool ShouldCheckSaveStamp(LoadKind refined)
        {
            return refined == LoadKind.InSessionOther || refined == LoadKind.QuickloadFlight;
        }

        /// <summary>
        /// Pure: true when a checked load read a node stamped by a save this process did not
        /// write last. Silent when either side has no stamp (a save from an older build, or no
        /// OnSave yet in this process). Never reclassifies: the canary finds unannounced loads.
        /// </summary>
        internal static bool IsForeignSaveStamp(string loadedStamp, string lastWrittenStamp)
        {
            if (string.IsNullOrEmpty(loadedStamp) || string.IsNullOrEmpty(lastWrittenStamp))
                return false;
            return !string.Equals(loadedStamp, lastWrittenStamp, StringComparison.Ordinal);
        }

        /// <summary>The canary's Warn text.</summary>
        internal static string FormatForeignSaveStampWarning(
            LoadKind refined, string scene, string loadedStamp, string lastWrittenStamp)
        {
            return "In-session load read a save this session did not just write: stamp=" + (loadedStamp ?? "<none>")
                + " lastWritten=" + (lastWrittenStamp ?? "<none>")
                + " refined=" + refined
                + " scene=" + (string.IsNullOrEmpty(scene) ? "<none>" : scene)
                + " (no stock save-file load was announced; the load keeps the in-session path)";
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
                case LoadStateCategory.StockPoolReference:
                case LoadStateCategory.RewindVesselHolds:
                    return true;
                case LoadStateCategory.UntaggedLedgerRowsAfterCutoff:
                case LoadStateCategory.PendingScience:
                case LoadStateCategory.Routes:
                case LoadStateCategory.Missions:
                case LoadStateCategory.Milestones:
                case LoadStateCategory.VesselNames:
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
                case LoadStateCategory.UntaggedLedgerRowsAfterCutoff: return DecideUntaggedLedgerRows(kind);
                case LoadStateCategory.PendingScience: return DecidePendingScience(kind);
                case LoadStateCategory.Routes: return DecideRoutes(kind);
                case LoadStateCategory.CrewReplacements: return DecideCrewReplacements(kind);
                case LoadStateCategory.KerbalSlots: return DecideKerbalSlots(kind);
                case LoadStateCategory.GroupHierarchy: return DecideGroupHierarchy(kind);
                case LoadStateCategory.Missions: return DecideMissions(kind);
                case LoadStateCategory.Milestones: return DecideMilestones(kind);
                case LoadStateCategory.VesselNames: return DecideVesselNames(kind);
                case LoadStateCategory.StockPoolReference: return DecideStockPoolReference(kind);
                case LoadStateCategory.RewindVesselHolds: return DecideRewindVesselHolds(kind);
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

        /// <summary>
        /// Appended to every QuickloadFlight cell the prologue does not consume (the shared early
        /// cells say it on the enum member): stock F9 is a save-file load and classifies Cold
        /// (design-load-restores-the-save.md 4.1), so the column documents a load no stock path
        /// announced. Phase 3 deletes the mechanisms only this column reaches.
        /// </summary>
        internal const string QuickloadFlightUnreachedByStockF9 =
            " [QuickloadFlight is no longer produced by stock F9, a save-file load that classifies Cold: only a "
            + "FLIGHT to FLIGHT load with the clock backwards that no stock path announced reaches it]";

        /// <summary>Appended to the InSessionOther cells that once served an F9 at the Space Center or
        /// into a flight quicksave from the Space Center.</summary>
        internal const string InSessionOtherNoStockLoad =
            " [stock F9 and the pause-menu Load classify Cold now: only a scene change, a vessel-switch reload, "
            + "Tracking Station Fly or a load no stock path announced reaches this cell]";

        private static LoadReconcileDecision QuickloadOnly(LoadReconcileAction action, string reason)
        {
            return new LoadReconcileDecision(action, null, reason + QuickloadFlightUnreachedByStockF9);
        }

        private static LoadReconcileDecision InSessionOtherOnly(LoadReconcileAction action, string reason)
        {
            return new LoadReconcileDecision(action, null, reason + InSessionOtherNoStockLoad);
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
                        InSessionHandoffInstalls + " (RecordingStore.MergeCarriedStagedList)");
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

        private const string ColdSessionReset =
            "ParsekScenario.ResetSaveScopedSessionState forgets the store and its once-per-folder guard; ";
        private const string ColdGameStateReader = "ParsekScenario.LoadGameStateFromNodeOrFiles";
        private const string ColdFilesFallback =
            "a save without the node (an older build) reads the external file as last written";

        private static LoadReconcileDecision DecideUntaggedLedgerRows(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                    return Today(LoadReconcileAction.Keep,
                        "scene rule: LedgerOrchestrator.OnKspLoad -> Ledger.Reconcile never drops an untagged earning "
                        + "by UT (contract completions aside); outside FLIGHT / SPACECENTER / TRACKSTATION with a ready "
                        + "clock (UT > 0) it drops untagged spendings, contract lifecycle rows and other untagged rows past "
                        + "the clock; FLIGHT / SPACECENTER / TRACKSTATION, or a clock not yet ready, keep them all");
                case LoadKind.PlainRewind:
                    return Today(LoadReconcileAction.Keep,
                        "ParsekScenario.HandleRewindOnLoad retires nothing here (route events live in the route event log, "
                        + "Routes); the recalculation walks to the cutoff");
                case LoadKind.ReFlyStart:
                    return Today(LoadReconcileAction.Bundle, BundleRestores);
                case LoadKind.DiscardReFly:
                    return Today(LoadReconcileAction.Keep,
                        "the revert prune runs only when the in-session branch reads isRevert");
                case LoadKind.StockRevert:
                    return Today(LoadReconcileAction.Prune,
                        "ParsekScenario.OnLoad revert branch -> PruneRevertedFlightLedgerRows at ResolveRevertPruneCutoff "
                        + "(only rows added since the flight entry; the rows the entry held stay, O2), "
                        + "then LedgerOrchestrator.PruneRevertedFlightStrategyConversionRows "
                        + "for the reverted flight's tagged strategy conversion rows");
                case LoadKind.QuickloadFlight:
                    return QuickloadOnly(LoadReconcileAction.Keep, UntaggedRowsKept);
                case LoadKind.InSessionOther:
                    return InSessionOtherOnly(LoadReconcileAction.Keep, UntaggedRowsKept);
            }
            throw UnknownKind(kind);
        }

        private const string UntaggedRowsKept =
                        "nothing retires them: the in-memory ledger keeps every untagged row and the recalculation walks "
                        + "to the cutoff. A player's load back in time is a Cold save-file load whose ledger is the save's "
                        + "own (design-load-restores-the-save.md), so Phase 3 deleted the in-session retire of the "
                        + "untagged rows made after the save";

        private static LoadReconcileDecision DecidePendingScience(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                    return Today(LoadReconcileAction.Save,
                        ColdSessionReset + "the cold branch restores GAME_STATE/PENDING_SCIENCE ("
                        + ColdGameStateReader + "); a save without the node has none");
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
                    return QuickloadOnly(LoadReconcileAction.Clear,
                        "ParsekScenario.DiscardStashedOnQuickload; skipped while the loaded save carries a Re-Fly marker "
                        + "(ShouldRunQuickloadDiscard)");
                case LoadKind.InSessionOther:
                    return InSessionOtherOnly(LoadReconcileAction.Keep, "the scene-change path does not touch it");
            }
            throw UnknownKind(kind);
        }

        private static LoadReconcileDecision DecideRoutes(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                    return Today(LoadReconcileAction.Save,
                        "RouteStore.LoadRoutesFrom on the cold path replaces the routes, the route event log "
                        + "(ROUTE_EVENT_LOG) and the retired route cargo with the save's own PARSEK node's, then "
                        + "RevalidateSources; legacy route rows in the save's ledger are dropped at ledger load");
                case LoadKind.PlainRewind:
                    return Today(LoadReconcileAction.ReconcileAtCutoff,
                        "ParsekScenario.HandleRewindOnLoad -> RouteLoadReconcile.ReconcileAtGoBackRewind: "
                        + "RouteEventLog.RetireFutureRouteActionsAtRewind + RouteRewindClassifier.ReconcileStoreAtRewind at "
                        + "the rewind save's own UT (before the lead-time windback; RewindAdjustedUT when no route copy "
                        + "was read), then each kept route takes its loop position back from that save's route copy "
                        + "(read from the parsed save in RecordingStore.ExecuteRewindSaveLoad; the OnLoad node is "
                        + "persistent.sfs); a cutoff ahead of the clock becomes the route state floor "
                        + "(RouteStore.StateFloorUT) that saves carry until the clock passes it");
                case LoadKind.ReFlyStart:
                    return Today(LoadReconcileAction.Bundle,
                        BundleRestores + ", retires the captured route events after the loaded UT, and reconciles routes there "
                        + "(RouteRewindClassifier.ReconcileStoreAtRewind), "
                        + "raised to the route state floor the RP quicksave carries, then each kept route takes its "
                        + "loop position back from the RP quicksave's own route copy (the OnLoad node)");
                case LoadKind.DiscardReFly:
                case LoadKind.StockRevert:
                    return Today(LoadReconcileAction.ReconcileAtCutoff, InSessionRouteReconcile);
                case LoadKind.QuickloadFlight:
                    return QuickloadOnly(LoadReconcileAction.ReconcileAtCutoff, InSessionRouteReconcile);
                case LoadKind.InSessionOther:
                    return InSessionOtherOnly(LoadReconcileAction.ReconcileAtCutoff,
                        InSessionRouteReconcile + ", only when route state lies after the loaded save "
                        + "(a load no stock path announced); a forward or same-instant scene change finds none and "
                        + "touches nothing");
            }
            throw UnknownKind(kind);
        }

        private const string InSessionRouteReconcile =
            "ParsekScenario.OnLoad -> RouteLoadReconcile.ReconcileAtInSessionLoad, before the recalculation: "
            + "RouteEventLog.RetireFutureRouteActionsAtRewind + RouteRewindClassifier.ReconcileStoreAtRewind at the "
            + "loaded save's UT (a stock revert: the earlier of it and the revert prune's launch boundary), "
            + "raised to the route state floor the save carries (written inside a go-back rewind's lead-time "
            + "window, the outgoing node of a scene change in it included), then each kept route takes its loop "
            + "position back from the loaded save's own route copy";

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

        private const string MissionsColdOnly =
            "MissionStore is loaded on the cold path only (the Re-Fly bundle does not carry it)";

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
                    return Today(LoadReconcileAction.Memory, MissionsColdOnly);
                case LoadKind.QuickloadFlight:
                    return QuickloadOnly(LoadReconcileAction.Memory, MissionsColdOnly);
                case LoadKind.InSessionOther:
                    return InSessionOtherOnly(LoadReconcileAction.Memory, MissionsColdOnly);
            }
            throw UnknownKind(kind);
        }

        private const string MilestonesSceneChange =
            "list kept in memory; scene-change branch -> MilestoneStore.RestoreMutableState(node)";

        private static LoadReconcileDecision DecideMilestones(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                    return Today(LoadReconcileAction.Save,
                        ColdSessionReset + "MilestoneStore.LoadMilestonesFromSaveNode reads GAME_STATE/PARSEK_MILESTONES ("
                        + ColdGameStateReader + "; " + ColdFilesFallback + ") + RestoreMutableState(node)");
                case LoadKind.PlainRewind:
                    return Today(LoadReconcileAction.Save,
                        "list kept in memory; ParsekScenario.HandleRewindOnLoad -> MilestoneStore.RestoreMutableState(node, resetUnmatched: true)");
                case LoadKind.ReFlyStart:
                    return Today(LoadReconcileAction.Bundle, BundleRestores);
                case LoadKind.StockRevert:
                    return Today(LoadReconcileAction.Save,
                        "list kept in memory; revert branch -> MilestoneStore.RestoreMutableState(node, resetUnmatched: true)");
                case LoadKind.DiscardReFly:
                    return Today(LoadReconcileAction.Save, MilestonesSceneChange);
                case LoadKind.QuickloadFlight:
                    return QuickloadOnly(LoadReconcileAction.Save, MilestonesSceneChange);
                case LoadKind.InSessionOther:
                    return InSessionOtherOnly(LoadReconcileAction.Save, MilestonesSceneChange);
            }
            throw UnknownKind(kind);
        }

        private const string VesselNamesMemory =
            "VesselNameStore is static memory loaded on the cold path only; a decision names the "
            + "vessel across rewinds and reloads, and the OnLoad tail re-applies it (recordings and the "
            + "save's proto vessels), the post-load pass the live vessels";
        private const string VesselNamesUndo =
            "ParsekScenario.OnLoad, after ClassifyRefined -> VesselNameStore.UndoCurrentFlightEpoch drops "
            + "the StockDialog / StockPartNaming decisions of the current flight epoch newest first, "
            + "restoring the decisions they replaced; ParsekRename decisions and a later one replacing "
            + "a stock decision stay (ruling 5); then as Memory: ";

        // Vessel names design 7.6 (ruling 5): a stock revert and Discard Re-fly cancel a flight
        // epoch and undo its stock renames. A Re-Fly Retry classifies as ReFlyStart, which a fresh
        // Re-Fly invoke shares (and that one cancels nothing), so the Retry undo runs in
        // RevertInterceptor.RetryHandler before its load, not here.
        private static LoadReconcileDecision DecideVesselNames(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                    return Today(LoadReconcileAction.Save,
                        "VesselNameStore.Load on the cold path (ParsekScenario.OnLoad, before the committed trees load); "
                        + "a save-file load (F9, the Load menu) is Cold, so its names are the save's (design 7.6 ruling 5 "
                        + "no longer applies to it)");
                case LoadKind.StockRevert:
                    return Today(LoadReconcileAction.MemoryUndoRevertedFlight,
                        VesselNamesUndo + VesselNamesMemory);
                case LoadKind.DiscardReFly:
                    return Today(LoadReconcileAction.MemoryUndoRevertedFlight,
                        VesselNamesUndo + "the epoch is the discarded Re-Fly attempt's "
                        + "(RewindInvoker.RunStripActivateMarker started it); " + VesselNamesMemory);
                case LoadKind.ReFlyStart:
                    return Today(LoadReconcileAction.Memory,
                        VesselNamesMemory + "; a Retry (it classifies ReFlyStart too) has already undone the "
                        + "cancelled attempt's stock decisions in RevertInterceptor.RetryHandler, before its load; "
                        + "a fresh Re-Fly invoke undoes nothing");
                case LoadKind.PlainRewind:
                    return Today(LoadReconcileAction.Memory, VesselNamesMemory);
                case LoadKind.QuickloadFlight:
                    return QuickloadOnly(LoadReconcileAction.Memory, VesselNamesMemory);
                case LoadKind.InSessionOther:
                    return InSessionOtherOnly(LoadReconcileAction.Memory, VesselNamesMemory);
            }
            throw UnknownKind(kind);
        }
    
        private const string StockPoolReferenceFromSave =
            "ParsekScenario.LoadStockPoolReference -> StockPoolReference.LoadFrom installs the loaded node's "
            + "STOCK_POOL_REFERENCE: lastWritten and the counted rows must describe the pool of the same .sfs "
            + "(a save made mid-flight holds awards whose rows do not exist yet, a quicksave from before a merge "
            + "does not count the merged rows); a node without it starts each pool afresh at its first patch "
            + "(the live pool with every row counted)";
        private const string StockPoolReferenceAuthoritative =
            "; the first patch after this load is authoritative (a time-travel signal), so it writes the target "
            + "(earlier kept leaks are StockPoolAdjustment rows of the walk) and re-baselines, carrying the stepped "
            + "part of counted rows the ledger no longer holds as one adjustment row";

        // Committed-value rule plan 2.4: the reference and the live pool always come from the
        // same .sfs, so every kind installs the save's copy. Rows the in-memory ledger keeps
        // past the loaded clock are committed future (never stepped), and rows the loaded
        // reference counted that the ledger no longer holds are stepped back at the first patch.
        private static LoadReconcileDecision DecideStockPoolReference(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                    return Today(LoadReconcileAction.Save, StockPoolReferenceFromSave);
                case LoadKind.PlainRewind:
                case LoadKind.ReFlyStart:
                    return Today(LoadReconcileAction.Save,
                        StockPoolReferenceFromSave + StockPoolReferenceAuthoritative);
                case LoadKind.DiscardReFly:
                    return Today(LoadReconcileAction.Save,
                        StockPoolReferenceFromSave + "; the node is persistent.sfs's own PARSEK node, kept by "
                        + "RevertInterceptor.DiscardReFlyHandler around the rewind point's game, written before the "
                        + "attempt added any row" + StockPoolReferenceAuthoritative);
                // Consumed in the prologue, before the revert detector: the three refined
                // in-session kinds share one cell (a stock revert's flight rows were never
                // committed, so the save's reference describes the reverted pool exactly).
                case LoadKind.StockRevert:
                case LoadKind.QuickloadFlight:
                case LoadKind.InSessionOther:
                    return Today(LoadReconcileAction.Save, StockPoolReferenceFromSave);
            }
            throw UnknownKind(kind);
        }

        private const string RewindVesselHoldsMemory =
            "RewindVesselHold is static memory: the vessels a plain rewind held back stay held across "
            + "scene changes and reverts, which reload a world that no longer holds them";

        // Rewind design 10.13: the holds describe the world of the save that holds them (the
        // held vessels are absent from it), so a cold load takes the save's; a plain rewind
        // computes its own from the rewind save's world; a Re-Fly takes its own invoke holds;
        // a Discard Re-fly computes its own from the rewind point's world it lands in.
        private static LoadReconcileDecision DecideRewindVesselHolds(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.Cold:
                    return Today(LoadReconcileAction.Save,
                        "ParsekScenario.LoadRewindVesselHolds -> RewindVesselHold.LoadFrom installs the loaded node's "
                        + "REWIND_VESSEL_HOLDS: the save's world lacks exactly those vessels");
                case LoadKind.PlainRewind:
                    return Today(LoadReconcileAction.Clear,
                        "cleared in the prologue; HandleRewindOnLoad -> ApplyRewindVesselHolds then holds what the "
                        + "committed future uses in the rewind save's world");
                case LoadKind.ReFlyStart:
                    return Today(LoadReconcileAction.Clear,
                        "the Re-Fly invoke holds the vessels its rewind point's world brings back "
                        + "(RewindInvoker.HoldResurrectedVesselsWithCommittedFuture, saved on its marker)");
                case LoadKind.DiscardReFly:
                    return Today(LoadReconcileAction.Clear,
                        "cleared in the prologue; the in-session branch's ApplyDiscardReFlyVesselHolds then holds "
                        + "what the committed future uses in the rewind point's world the discard lands in");
                // Consumed in the prologue, before the revert detector: one cell for all three.
                case LoadKind.StockRevert:
                case LoadKind.QuickloadFlight:
                case LoadKind.InSessionOther:
                    return Today(LoadReconcileAction.Memory, RewindVesselHoldsMemory);
            }
            throw UnknownKind(kind);
        }
    }
}
