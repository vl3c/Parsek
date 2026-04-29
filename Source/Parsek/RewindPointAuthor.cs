using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Rewind-to-Staging (Phase 4, design §5.1 + §6.1 + §7.1 + §7.4 + §7.19 +
    /// §7.27): creates a <see cref="RewindPoint"/> at a multi-controllable split.
    ///
    /// <para>
    /// <b>Two-step flow.</b> <see cref="Begin"/> runs synchronously in the same
    /// frame as <c>CreateSplitBranch</c>, builds the RP stub with an empty
    /// <see cref="RewindPoint.PidSlotMap"/> / <see cref="RewindPoint.RootPartPidMap"/>,
    /// appends it to <see cref="ParsekScenario.RewindPoints"/>, and stamps
    /// <see cref="BranchPoint.RewindPointId"/>. A coroutine started on the live
    /// <see cref="ParsekScenario"/> then defers one frame and performs the
    /// expensive work: high-warp drop, PID-map population from live vessels, a
    /// stock KSP <c>GamePersistence.SaveGame</c> to a temporary filename in the
    /// save root, and an atomic move to
    /// <c>saves/&lt;save&gt;/Parsek/RewindPoints/&lt;rpId&gt;.sfs</c>. The warp rate
    /// is restored in a <c>finally</c> so a mid-save throw does not leave the
    /// player stuck at rate 0.
    /// </para>
    ///
    /// <para>
    /// <b>Partial-failure tolerance (§7.4):</b> a slot whose live vessel cannot
    /// be resolved is marked <see cref="ChildSlot.Disabled"/> with a reason
    /// string; the RP is kept. If every slot fails, the RP is marked
    /// <see cref="RewindPoint.Corrupted"/> and kept for diagnostic visibility.
    /// The save file is never deleted on the failure path; a later session merge
    /// + reap decides whether to retain or discard it.
    /// </para>
    /// </summary>
    internal static class RewindPointAuthor
    {
        // Test seam: allows unit tests to run the normally-coroutine-backed
        // deferred body synchronously (no Unity MonoBehaviour runtime). When
        // non-null, Begin() invokes this instead of StartCoroutine().
        internal static Action<RewindPoint, RewindPointAuthorContext> SyncRunForTesting;

        /// <summary>
        /// Public entry point. Validates scene, builds the RP stub, wires it into
        /// <see cref="ParsekScenario.RewindPoints"/> + <see cref="BranchPoint.RewindPointId"/>,
        /// and schedules the deferred capture coroutine. Returns the RP on success
        /// or null on scene-guard failure / invalid scenario.
        /// </summary>
        internal static RewindPoint Begin(
            BranchPoint branchPoint,
            List<ChildSlot> childSlots,
            List<uint> controllableChildPids)
        {
            return Begin(branchPoint, childSlots, controllableChildPids, context: null);
        }

        internal static RewindPoint Begin(
            BranchPoint branchPoint,
            List<ChildSlot> childSlots,
            List<uint> controllableChildPids,
            RewindPointAuthorContext context)
        {
            if (branchPoint == null)
            {
                ParsekLog.Warn("RewindSave", "Begin: branchPoint is null");
                return null;
            }
            if (childSlots == null || childSlots.Count == 0)
            {
                ParsekLog.Warn("RewindSave",
                    $"Begin: childSlots is null/empty for bp={branchPoint.Id}");
                return null;
            }

            // Scene guard (§6.1, §7.27). A scene-level check here catches KSC /
            // tracking-station / editor invocations synchronously. The deferred
            // coroutine re-checks to cover mid-frame transitions.
            GameScenes scene = HighLogic.LoadedScene;
            if (scene != GameScenes.FLIGHT)
            {
                ParsekLog.Warn("RewindSave", $"Aborted: scene={scene}");
                return null;
            }

            ParsekScenario scenario = ParsekScenario.Instance;
            // Bypass Unity.Object's overloaded == null: MonoBehaviour instances
            // installed via SetInstanceForTesting don't have a native side, so
            // `scenario == null` returns true even though the reference is live.
            if (object.ReferenceEquals(null, scenario))
            {
                ParsekLog.Warn("RewindSave",
                    $"Aborted: no live ParsekScenario instance (bp={branchPoint.Id})");
                return null;
            }

            double ut = 0.0;
            try { ut = Planetarium.GetUniversalTime(); }
            catch { /* Planetarium may not be available in tests */ }

            string rpId = "rp_" + Guid.NewGuid().ToString("N");

            // Defensive: validate rpId so the later BuildRewindPointRelativePath call
            // will not return null. Guid.ToString("N") is always safe, but logging the
            // assumption up-front makes a future mutation visible.
            if (!RecordingPaths.ValidateRecordingId(rpId))
            {
                ParsekLog.Error("RewindSave",
                    $"Begin: generated rpId '{rpId}' fails validation (this is a bug)");
                return null;
            }

            // Finalize the context before the RP is built so focus-slot
            // capture can use the same injected flight-globals provider and
            // recording resolver as the deferred PID-map population.
            var ctx = context ?? new RewindPointAuthorContext();
            if (ctx.FlightGlobals == null) ctx.FlightGlobals = FlightGlobalsProvider.Default;
            if (ctx.SaveAction == null) ctx.SaveAction = DefaultSaveAction;
            if (ctx.ScenePathsProvider == null) ctx.ScenePathsProvider = DefaultScenePaths.Instance;
            if (ctx.TimeWarp == null) ctx.TimeWarp = DefaultTimeWarpProvider.Instance;
            if (ctx.Scenario == null) ctx.Scenario = scenario;
            if (ctx.SaveRootProvider == null) ctx.SaveRootProvider = DefaultSaveRootProvider.Instance;

            var rp = new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = branchPoint.Id,
                UT = ut,
                CreatedRealTime = DateTime.UtcNow.ToString("o"),
                QuicksaveFilename = $"Parsek/RewindPoints/{rpId}.sfs",
                ChildSlots = childSlots,
                FocusSlotIndex = ResolveFocusSlotIndex(childSlots, ctx.RecordingResolver, ctx.FlightGlobals),
                SessionProvisional = true,
                Corrupted = false,
                // If a re-fly session is active, the RP is speculative within that session context.
                CreatingSessionId = scenario.ActiveReFlySessionMarker?.SessionId,
                PidSlotMap = new Dictionary<uint, int>(),
                RootPartPidMap = new Dictionary<uint, int>()
            };

            // Synchronous wiring: the RP must be in ParsekScenario.RewindPoints
            // and the BranchPoint must carry its id BEFORE the deferred frame so
            // that an OnSave triggered mid-coroutine captures the in-progress RP
            // (§6.1 atomicity note; §7.27 + §7.19 / §10.1 observability).
            if (scenario.RewindPoints == null)
                scenario.RewindPoints = new List<RewindPoint>();
            scenario.RewindPoints.Add(rp);
            branchPoint.RewindPointId = rpId;

            ParsekLog.Info("Rewind",
                $"RewindPoint begin: rp={rpId} bp={branchPoint.Id} slots={childSlots.Count} " +
                $"controllablePids={(controllableChildPids?.Count ?? 0)} " +
                $"focusSlot={rp.FocusSlotIndex} " +
                $"ut={ut.ToString("F2", CultureInfo.InvariantCulture)}");

            // Test seam: run deferred body synchronously when tests have installed a hook.
            if (SyncRunForTesting != null)
            {
                try { SyncRunForTesting(rp, ctx); }
                catch (Exception ex)
                {
                    ParsekLog.Error("RewindSave",
                        $"SyncRunForTesting threw for rp={rpId}: {ex.Message}");
                }
                return rp;
            }

            scenario.StartCoroutine(RunDeferred(rp, branchPoint, ctx));
            return rp;
        }

        /// <summary>
        /// Deferred capture coroutine. Re-checks scene, captures PID maps from live
        /// vessels, drops high warp, executes stock save + atomic move, restores warp.
        /// Never throws: all failures land as Warn/Error logs and RP field updates.
        /// </summary>
        internal static IEnumerator RunDeferred(
            RewindPoint rp,
            BranchPoint branchPoint,
            RewindPointAuthorContext ctx)
        {
            yield return null; // one-frame defer (§6.1 deferred quicksave)

            // Re-check scene guard: if the player quit to KSC / reverted between
            // Begin and the deferred frame, roll back the synchronous wiring.
            GameScenes scene = HighLogic.LoadedScene;
            if (scene != GameScenes.FLIGHT)
            {
                ParsekLog.Warn("RewindSave", $"Aborted mid-coroutine: scene={scene}");
                RollbackBegin(rp, branchPoint, ctx.Scenario);
                yield break;
            }

            ExecuteDeferredBody(rp, branchPoint, ctx);
        }

        /// <summary>
        /// The deferred body factored out of the coroutine so unit tests can drive
        /// it without running a Unity coroutine scheduler. Handles PID map capture,
        /// warp drop/restore (finally), save, and atomic move.
        /// </summary>
        internal static void ExecuteDeferredBody(
            RewindPoint rp,
            BranchPoint branchPoint,
            RewindPointAuthorContext ctx)
        {
            if (rp == null)
            {
                ParsekLog.Warn("RewindSave", "ExecuteDeferredBody: rp is null");
                return;
            }
            ctx = ctx ?? new RewindPointAuthorContext();
            var flightGlobals = ctx.FlightGlobals ?? FlightGlobalsProvider.Default;
            var save = ctx.SaveAction ?? DefaultSaveAction;
            var paths = ctx.ScenePathsProvider ?? DefaultScenePaths.Instance;
            var warp = ctx.TimeWarp ?? DefaultTimeWarpProvider.Instance;
            var saveRoot = ctx.SaveRootProvider ?? DefaultSaveRootProvider.Instance;

            int priorWarpRate = warp.GetCurrentRateIndex();
            bool warpDropped = false;
            if (priorWarpRate > 1)
            {
                warp.SetRate(0, instant: true);
                warpDropped = true;
                ParsekLog.Info("RewindSave",
                    $"Warp dropped from {priorWarpRate} to 0 for rp={rp.RewindPointId}");
            }

            try
            {
                // NOTE: PID maps are populated BEFORE the save delegate runs, not after as in design §6.1 step 5.
                // Functionally equivalent — both happen inside the same try/finally; vessels can't mutate
                // between these two synchronous steps. Kept in this order for clarity and to tolerate a save
                // failure without losing the live-vessel correlation.
                //
                // [ERS-exempt] RP capture matches live vessels to committed child slots
                // by raw PID correlation. Populating PidSlotMap / RootPartPidMap is a
                // setup-time operation that needs the untombstoned VesselPersistentId
                // of each child slot's origin recording to correlate against live
                // vessels. ERS walks would filter by supersede visibility, which does
                // not exist yet at Phase 4's speculative-RP creation time (§5.1).
                int populated = 0;
                int disabled = 0;
                if (rp.ChildSlots != null)
                {
                    for (int i = 0; i < rp.ChildSlots.Count; i++)
                    {
                        var slot = rp.ChildSlots[i];
                        if (slot == null) continue;
                        uint? pid = ResolveSlotVesselPid(slot, ctx.RecordingResolver);
                        VesselSnapshot snapshot = default;
                        bool hasSnapshot = false;
                        if (pid.HasValue)
                            hasSnapshot = flightGlobals.TryGetVesselSnapshot(pid.Value, out snapshot);

                        if (!hasSnapshot)
                        {
                            slot.Disabled = true;
                            slot.DisabledReason = "no-live-vessel";
                            disabled++;
                            ParsekLog.Warn("Rewind",
                                $"Slot {i} disabled: no live vessel for rec={slot.OriginChildRecordingId ?? "(null)"} " +
                                $"(rp={rp.RewindPointId})");
                            continue;
                        }

                        rp.PidSlotMap[snapshot.VesselPersistentId] = slot.SlotIndex;
                        if (snapshot.HasRootPart)
                            rp.RootPartPidMap[snapshot.RootPartPersistentId] = slot.SlotIndex;
                        else
                        {
                            ParsekLog.Warn("Rewind",
                                $"Slot {i}: live vessel pid={snapshot.VesselPersistentId} has no rootPart; " +
                                $"RootPartPidMap entry skipped (rp={rp.RewindPointId})");
                        }
                        populated++;
                    }
                }

                int totalSlots = rp.ChildSlots?.Count ?? 0;
                ParsekLog.Info("Rewind",
                    $"RewindPoint slot capture: rp={rp.RewindPointId} " +
                    $"populated={populated}/{totalSlots} disabled={disabled}");

                if (populated == 0 && totalSlots > 0)
                {
                    rp.Corrupted = true;
                    ParsekLog.Warn("Rewind",
                        $"All slots disabled; RP unusable (rp={rp.RewindPointId}). Keeping for diagnostic visibility.");
                    // Do NOT return — continue with the save so the on-disk quicksave
                    // also survives for diagnostic/manual recovery.
                }

                // Perform stock KSP save to a temporary filename in the save root.
                string tempName = $"Parsek_TempRP_{rp.RewindPointId}";
                string saveFolder = paths.GetSaveFolder();
                var sw = Stopwatch.StartNew();
                try
                {
                    string saveResult = save(tempName, saveFolder);
                    sw.Stop();
                    if (string.IsNullOrEmpty(saveResult))
                    {
                        ParsekLog.Error("RewindSave",
                            $"Failed rp={rp.RewindPointId} reason=SaveGame returned empty");
                        RollbackBegin(rp, branchPoint, ctx.Scenario);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    ParsekLog.Error("RewindSave",
                        $"Failed rp={rp.RewindPointId} reason={ex.Message}");
                    RollbackBegin(rp, branchPoint, ctx.Scenario);
                    return;
                }

                // Resolve absolute src/dst and atomically move into the RewindPoints
                // subdirectory. EnsureRewindPointsDirectory creates it when needed.
                string destDir = paths.EnsureRewindPointsDirectory();
                if (string.IsNullOrEmpty(destDir))
                {
                    ParsekLog.Error("RewindSave",
                        $"Failed rp={rp.RewindPointId} reason=rewind-points-dir-unresolved");
                    RollbackBegin(rp, branchPoint, ctx.Scenario);
                    return;
                }

                string saveDir = saveRoot.GetSaveDirectory();
                string src = Path.Combine(saveDir, tempName + ".sfs");
                string dst = Path.Combine(destDir, rp.RewindPointId + ".sfs");

                long bytes = 0;
                try
                {
                    FileIOUtils.SafeMove(src, dst, "RewindSave");
                    try { bytes = new FileInfo(dst).Length; } catch { /* best-effort */ }
                }
                catch (Exception ex)
                {
                    ParsekLog.Error("RewindSave",
                        $"Failed rp={rp.RewindPointId} reason=move:{ex.Message}");
                    RollbackBegin(rp, branchPoint, ctx.Scenario);
                    return;
                }

                string relPath = RecordingPaths.BuildRewindPointRelativePath(rp.RewindPointId) ?? dst;
                ParsekLog.Info("RewindSave",
                    $"Wrote rp={rp.RewindPointId} path={relPath} bytes={bytes} ms={sw.ElapsedMilliseconds}");
            }
            finally
            {
                if (warpDropped)
                {
                    try
                    {
                        warp.SetRate(priorWarpRate, instant: true);
                        ParsekLog.Info("RewindSave",
                            $"Warp restored to {priorWarpRate} for rp={rp.RewindPointId}");
                    }
                    catch (Exception ex)
                    {
                        ParsekLog.Warn("RewindSave",
                            $"Warp restore to {priorWarpRate} failed for rp={rp.RewindPointId}: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Resolves the expected <c>Vessel.persistentId</c> for a child slot. Callers
        /// may inject <paramref name="resolver"/> (tests + contexts where the child
        /// recording is not yet in <c>CommittedRecordings</c>); otherwise the default
        /// walks <see cref="RecordingStore.CommittedRecordings"/>.
        /// </summary>
        internal static uint? ResolveSlotVesselPid(ChildSlot slot,
            Func<string, uint?> resolver)
        {
            if (slot == null) return null;
            string recId = slot.OriginChildRecordingId;
            if (string.IsNullOrEmpty(recId)) return null;

            if (resolver != null)
                return resolver(recId);

            // Default: scan committed recordings. At Phase 4's speculative-RP
            // creation time the child recording may not yet be in the committed
            // list (still in activeTree); callers should install a resolver that
            // checks the active tree first. Returning null here causes the slot
            // to be marked Disabled with reason "no-live-vessel", which is the
            // correct partial-failure behavior per §7.4.
            var list = RecordingStore.CommittedRecordings;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var rec = list[i];
                    if (rec == null) continue;
                    if (string.Equals(rec.RecordingId, recId, StringComparison.Ordinal))
                        return rec.VesselPersistentId;
                }
            }
            return null;
        }

        internal static int ResolveFocusSlotIndex(
            List<ChildSlot> childSlots,
            Func<string, uint?> resolver,
            IFlightGlobalsProvider flightGlobals)
        {
            if (childSlots == null || childSlots.Count == 0)
                return -1;
            if (flightGlobals == null)
                return -1;

            uint? activePid = flightGlobals.GetActiveVesselPid();
            if (!activePid.HasValue || activePid.Value == 0u)
                return -1;

            int firstMatch = -1;
            int matches = 0;
            for (int i = 0; i < childSlots.Count; i++)
            {
                var slot = childSlots[i];
                uint? slotPid = ResolveSlotVesselPid(slot, resolver);
                if (slotPid.HasValue && slotPid.Value == activePid.Value)
                {
                    if (firstMatch < 0)
                        firstMatch = i;
                    matches++;
                }
            }

            if (matches > 1)
            {
                ParsekLog.Verbose("Rewind",
                    $"ResolveFocusSlotIndex: activePid={activePid.Value} matched {matches} slots; " +
                    $"using first slot={firstMatch}");
            }

            if (firstMatch >= 0)
                return firstMatch;

            return -1;
        }

        private static void RollbackBegin(RewindPoint rp, BranchPoint branchPoint, ParsekScenario scenario)
        {
            // Use ReferenceEquals to bypass Unity.Object's overloaded == null:
            // a unit-test fixture installed via SetInstanceForTesting returns
            // null from scenario?.RewindPoints even when the reference is live.
            if (!object.ReferenceEquals(null, scenario) && scenario.RewindPoints != null && rp != null)
            {
                int removed = 0;
                for (int i = scenario.RewindPoints.Count - 1; i >= 0; i--)
                {
                    if (scenario.RewindPoints[i] == rp)
                    {
                        scenario.RewindPoints.RemoveAt(i);
                        removed++;
                    }
                }
                if (removed > 0)
                {
                    RecordingsTableUI.ClearRewindSlotCanInvokeLogState(rp.RewindPointId);
                    ParsekLog.Info("Rewind",
                        $"RewindPoint rolled back: rp={rp.RewindPointId} removals={removed}");
                }
            }
            if (branchPoint != null && rp != null
                && string.Equals(branchPoint.RewindPointId, rp.RewindPointId, StringComparison.Ordinal))
            {
                branchPoint.RewindPointId = null;
            }
        }

        // --- Default implementations of injected seams ---

        internal static readonly Func<string, string, string> DefaultSaveAction =
            (tempName, saveFolder) => GamePersistence.SaveGame(tempName, saveFolder, SaveMode.OVERWRITE);

        internal interface IScenePaths
        {
            string GetSaveFolder();
            string EnsureRewindPointsDirectory();
        }

        private sealed class DefaultScenePaths : IScenePaths
        {
            internal static readonly IScenePaths Instance = new DefaultScenePaths();
            public string GetSaveFolder() => HighLogic.SaveFolder ?? "";
            public string EnsureRewindPointsDirectory() => RecordingPaths.EnsureRewindPointsDirectory();
        }

        internal interface ITimeWarpProvider
        {
            int GetCurrentRateIndex();
            void SetRate(int rateIndex, bool instant);
        }

        private sealed class DefaultTimeWarpProvider : ITimeWarpProvider
        {
            internal static readonly ITimeWarpProvider Instance = new DefaultTimeWarpProvider();
            public int GetCurrentRateIndex()
            {
                try { return TimeWarp.CurrentRateIndex; }
                catch { return 0; }
            }
            public void SetRate(int rateIndex, bool instant)
            {
                try { TimeWarp.SetRate(rateIndex, instant); }
                catch (Exception ex)
                {
                    ParsekLog.Warn("RewindSave",
                        $"TimeWarp.SetRate({rateIndex}, instant={instant}) failed: {ex.Message}");
                }
            }
        }

        internal interface ISaveRootProvider
        {
            string GetSaveDirectory();
        }

        private sealed class DefaultSaveRootProvider : ISaveRootProvider
        {
            internal static readonly ISaveRootProvider Instance = new DefaultSaveRootProvider();
            public string GetSaveDirectory()
            {
                string root = KSPUtil.ApplicationRootPath ?? "";
                string saveFolder = HighLogic.SaveFolder ?? "";
                if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(saveFolder))
                    return "";
                return Path.GetFullPath(Path.Combine(root, "saves", saveFolder));
            }
        }
    }

    /// <summary>
    /// Bundle of injectable dependencies for <see cref="RewindPointAuthor"/>.
    /// Any field left null falls back to the default implementation that talks
    /// to KSP. Unit tests install mocks for <see cref="FlightGlobals"/>,
    /// <see cref="SaveAction"/>, <see cref="TimeWarp"/>, and the path / save-root
    /// providers to avoid Unity dependencies.
    /// </summary>
    internal sealed class RewindPointAuthorContext
    {
        public IFlightGlobalsProvider FlightGlobals;

        /// <summary>
        /// Resolver that returns the expected <c>Vessel.persistentId</c> for a
        /// given <c>ChildSlot.OriginChildRecordingId</c>. When null, the author
        /// falls back to scanning <see cref="RecordingStore.CommittedRecordings"/>.
        /// Callers at the flight-scene split site should pass a resolver that
        /// consults the active tree because freshly-created child recordings are
        /// not yet in the committed list.
        /// </summary>
        public Func<string, uint?> RecordingResolver;

        /// <summary>
        /// Delegate that performs the stock KSP save and returns the save path
        /// string (empty/null = failure). Defaults to <c>GamePersistence.SaveGame</c>.
        /// </summary>
        public Func<string, string, string> SaveAction;

        public RewindPointAuthor.IScenePaths ScenePathsProvider;
        public RewindPointAuthor.ITimeWarpProvider TimeWarp;
        public RewindPointAuthor.ISaveRootProvider SaveRootProvider;

        /// <summary>
        /// Scenario instance to wire the RP into. Tests supply a fixture;
        /// production code relies on <c>ParsekScenario.Instance</c>, which
        /// <see cref="RewindPointAuthor.Begin"/> captures up-front.
        /// </summary>
        public ParsekScenario Scenario;
    }
}
