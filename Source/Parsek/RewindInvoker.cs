using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Phase 6 of Rewind-to-Staging (design §6.3 + §6.4): orchestrates the
    /// user-initiated rewind-to-rewind-point flow. The flow is:
    ///
    /// <list type="number">
    ///   <item><description><c>CanInvoke</c> precondition gates (§7.5 / §7.29 / §7.34)</description></item>
    ///   <item><description><c>ShowDialog</c> displays the confirmation popup</description></item>
    ///   <item><description>On confirm, <c>RunInvoke</c> captures the reconciliation bundle, loads the RP's quicksave, runs the post-load strip, activates the selected child vessel, and atomically writes the provisional re-fly recording + <see cref="ReFlySessionMarker"/></description></item>
    /// </list>
    ///
    /// <para>
    /// [ERS-exempt — Phase 6] The invoker correlates live vessels to the RP's
    /// PidSlotMap/RootPartPidMap via raw <c>Vessel.persistentId</c> reads; this
    /// is a physical identity correlation at load time, not a supersede-aware
    /// recording lookup, so routing through <see cref="EffectiveState.ComputeERS"/>
    /// would not apply. The file is allowlisted in
    /// <c>scripts/ers-els-audit-allowlist.txt</c> with a call-site rationale.
    /// </para>
    /// </summary>
    internal static class RewindInvoker
    {
        private const string InvokeTag = "Rewind";
        private const string UITag = "RewindUI";
        private const string SessionTag = "ReFlySession";

        // Test seam: allows unit tests to capture the synchronous atomic block
        // phase boundaries without running the full coroutine. Set to non-null
        // in tests to record each checkpoint; the invoker calls it at key
        // points during the §6.3 step 4 phase 1+2 critical section.
        internal static Action<string> CheckpointHookForTesting;

        /// <summary>
        /// Returns <c>true</c> if the Rewind button for <paramref name="rp"/>
        /// should be enabled. Checks four preconditions per §6.3:
        /// <list type="bullet">
        ///   <item><description>RP is not Corrupted</description></item>
        ///   <item><description>Quicksave file exists on disk (§7.34)</description></item>
        ///   <item><description>No other re-fly session is active (§7.5)</description></item>
        ///   <item><description>Deep-parse precondition passes — every PART node in the quicksave resolves via <see cref="PartLoader.getPartInfoByName"/> (§7.29)</description></item>
        /// </list>
        /// The result + reason are cached per RP for 60s via
        /// <see cref="PreconditionCache"/> so repeated UI draws do not re-parse
        /// the .sfs every frame.
        /// </summary>
        internal static bool CanInvoke(RewindPoint rp, out string reason)
        {
            if (rp == null)
            {
                reason = "rewind point is null";
                return false;
            }

            if (rp.Corrupted)
            {
                reason = "Rewind point is marked corrupted";
                return false;
            }

            if (string.IsNullOrEmpty(rp.QuicksaveFilename))
            {
                reason = "Rewind point has no quicksave file";
                return false;
            }

            string abs = ResolveAbsoluteQuicksavePath(rp);
            if (string.IsNullOrEmpty(abs) || !File.Exists(abs))
            {
                reason = $"Quicksave file missing on disk: {rp.QuicksaveFilename}";
                return false;
            }

            var scenario = ParsekScenario.Instance;
            if (!object.ReferenceEquals(null, scenario) && scenario.ActiveReFlySessionMarker != null)
            {
                reason = "Another re-fly session is already active";
                return false;
            }

            // Deep-parse PartLoader precondition (cached per RP).
            if (!PreconditionCache.IsValid(rp))
            {
                var result = PartLoaderPrecondition.Check(rp, abs);
                PreconditionCache.Store(rp, result);
            }

            var cached = PreconditionCache.Get(rp);
            if (cached.HasValue && !cached.Value.Passed)
            {
                reason = cached.Value.Reason ?? "Deep-parse precondition failed";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// Spawns the "Rewind?" confirmation PopupDialog. On accept, starts
        /// the <see cref="RunInvoke"/> coroutine on <see cref="ParsekScenario.Instance"/>.
        /// </summary>
        internal static void ShowDialog(RewindPoint rp, int selectedSlotIndex)
        {
            if (rp == null)
            {
                ParsekLog.Warn(UITag, "ShowDialog called with null RP");
                return;
            }
            if (rp.ChildSlots == null || selectedSlotIndex < 0 || selectedSlotIndex >= rp.ChildSlots.Count)
            {
                ParsekLog.Warn(UITag,
                    $"ShowDialog: invalid slot index {selectedSlotIndex} (slots={rp.ChildSlots?.Count ?? 0}) for rp={rp.RewindPointId}");
                return;
            }

            var selected = rp.ChildSlots[selectedSlotIndex];
            string slotName = selected?.OriginChildRecordingId ?? "<unknown>";
            string title = "Parsek - Rewind to Staging";
            var ic = CultureInfo.InvariantCulture;
            string utText = rp.UT.ToString("F1", ic);
            string message =
                $"Rewind to rewind point {rp.RewindPointId} at UT {utText}?\n" +
                $"Spawning the selected child (slot {selectedSlotIndex}, origin={slotName}) live; " +
                $"merged siblings will play as ghosts.\n\n" +
                "Career state during this attempt stays as it is now. Supersede on merge " +
                "retires only kerbal-death events; contract / milestone / facility / strategy " +
                "/ tech / science / funds state is unchanged.";

            var capturedRp = rp;
            var capturedSlotIdx = selectedSlotIndex;
            var capturedSelected = selected;

            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekRewindInvoke",
                    message,
                    title,
                    HighLogic.UISkin,
                    new DialogGUIButton("Rewind", () =>
                    {
                        ParsekLog.Info(UITag,
                            $"Invoked rec={capturedSelected?.OriginChildRecordingId ?? "<none>"} " +
                            $"rp={capturedRp.RewindPointId} slot={capturedSlotIdx}");
                        StartInvoke(capturedRp, capturedSelected);
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Info(UITag,
                            $"Cancelled rp={capturedRp.RewindPointId} slot={capturedSlotIdx}");
                    })
                ),
                false, HighLogic.UISkin);
        }

        private static void StartInvoke(RewindPoint rp, ChildSlot selected)
        {
            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario))
            {
                ParsekLog.Error(InvokeTag,
                    $"StartInvoke: no live ParsekScenario instance (rp={rp.RewindPointId})");
                return;
            }

            try
            {
                scenario.StartCoroutine(RunInvoke(rp, selected));
            }
            catch (Exception ex)
            {
                ParsekLog.Error(InvokeTag,
                    $"StartInvoke: failed to start coroutine: {ex.Message}");
            }
        }

        /// <summary>
        /// Multi-step coroutine that executes the full invocation sequence
        /// per design §6.3 / §6.4. Bundled here rather than in
        /// <see cref="ParsekScenario"/> because the bundle + strip + marker
        /// write + bundle-restore-on-failure are a single logical transaction.
        /// </summary>
        internal static IEnumerator RunInvoke(RewindPoint rp, ChildSlot selected)
        {
            string sessionId = "sess_" + Guid.NewGuid().ToString("N");
            ParsekLog.Info(InvokeTag,
                $"RunInvoke begin: sess={sessionId} rp={rp.RewindPointId} " +
                $"slot={selected?.SlotIndex ?? -1}");

            // Step 1: capture reconciliation bundle
            ReconciliationBundle bundle;
            try
            {
                bundle = ReconciliationBundle.Capture();
            }
            catch (Exception ex)
            {
                ParsekLog.Error(InvokeTag,
                    $"Invocation failed: bundle capture threw: {ex.Message}");
                yield break;
            }

            // Step 2: subscribe post-load hook, then trigger the load
            var loadMonitor = new PostLoadMonitor();
            loadMonitor.Arm();

            string relativeName = StripSfsExtension(rp.QuicksaveFilename);
            bool loadIssued = false;
            try
            {
                ParsekLog.Info(InvokeTag,
                    $"Loading quicksave: relative='{rp.QuicksaveFilename}' loadName='{relativeName}'");
                Game game = GamePersistence.LoadGame(
                    relativeName, HighLogic.SaveFolder, true, false);
                if (game == null)
                {
                    ParsekLog.Error(InvokeTag,
                        $"Invocation failed: load error (GamePersistence.LoadGame returned null) " +
                        $"rp={rp.RewindPointId}");
                    loadMonitor.Unarm();
                    HandleQuicksaveMissing(rp);
                    TryRestoreBundle(bundle);
                    yield break;
                }

                HighLogic.CurrentGame = game;
                HighLogic.LoadScene(GameScenes.FLIGHT);
                loadIssued = true;
            }
            catch (Exception ex)
            {
                ParsekLog.Error(InvokeTag,
                    $"Invocation failed: load error: {ex.Message} rp={rp.RewindPointId}");
                loadMonitor.Unarm();
                HandleQuicksaveMissing(rp);
                TryRestoreBundle(bundle);
                yield break;
            }

            if (!loadIssued)
            {
                loadMonitor.Unarm();
                yield break;
            }

            // Step 3: wait for onGameStateLoad to fire (or 10s timeout)
            double waitBudget = 10.0;
            double waited = 0.0;
            while (!loadMonitor.Fired && waited < waitBudget)
            {
                yield return null;
                waited += Mathf.Max(Time.unscaledDeltaTime, 1f / 60f);
            }
            loadMonitor.Unarm();

            if (!loadMonitor.Fired)
            {
                ParsekLog.Error(InvokeTag,
                    $"Invocation failed: onGameStateLoad timeout after {waited:F1}s rp={rp.RewindPointId}");
                TryRestoreBundle(bundle);
                yield break;
            }

            // Step 4: reconcile, strip, activate, atomically write provisional + marker
            ReconciliationBundle.Restore(bundle);

            PostLoadStripResult stripResult;
            try
            {
                stripResult = PostLoadStripper.Strip(rp, selected?.SlotIndex ?? -1);
            }
            catch (Exception ex)
            {
                ParsekLog.Error(InvokeTag,
                    $"Invocation failed: strip threw: {ex.Message}");
                yield break;
            }

            if (stripResult.SelectedVessel == null)
            {
                ParsekLog.Error(InvokeTag,
                    $"Activate failed: selected vessel not present on reload " +
                    $"rp={rp.RewindPointId} slot={selected?.SlotIndex ?? -1}");
                yield break;
            }

            try
            {
                FlightGlobals.SetActiveVessel(stripResult.SelectedVessel);
            }
            catch (Exception ex)
            {
                ParsekLog.Error(InvokeTag,
                    $"Activate failed: SetActiveVessel threw: {ex.Message}");
                yield break;
            }

            // §6.3 step 4 phases 1 + 2: atomic provisional + marker write.
            // NO yield between checkpoints A and B.
            AtomicMarkerWrite(rp, selected, stripResult, sessionId);

            // Post-atomic: recalculate ledger
            try
            {
                LedgerOrchestrator.RecalculateAndPatch();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(InvokeTag,
                    $"Post-invoke ledger recalculate threw (non-fatal): {ex.Message}");
            }

            ParsekLog.Info(InvokeTag,
                $"Invocation complete: sess={sessionId} rp={rp.RewindPointId} " +
                $"slot={selected?.SlotIndex ?? -1} activePid={stripResult.SelectedVessel.persistentId}");
        }

        /// <summary>
        /// §6.3 step 4 critical section. Runs synchronously; throws MUST
        /// leave the global state untouched (we roll back the provisional
        /// add before rethrowing). NO yield, NO await, NO deferred save.
        /// </summary>
        internal static void AtomicMarkerWrite(
            RewindPoint rp, ChildSlot selected,
            PostLoadStripResult stripResult, string sessionId)
        {
            if (rp == null) throw new ArgumentNullException(nameof(rp));
            if (selected == null) throw new ArgumentNullException(nameof(selected));
            if (stripResult.SelectedVessel == null)
                throw new InvalidOperationException("AtomicMarkerWrite: no selected vessel");

            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario))
                throw new InvalidOperationException("AtomicMarkerWrite: no ParsekScenario instance");

            // Resolve the origin recording so we can clone its lineage metadata.
            Recording originChild = FindRecordingById(selected.OriginChildRecordingId);

            var provisional = BuildProvisionalRecording(rp, selected, originChild, sessionId, stripResult);

            CheckpointHookForTesting?.Invoke("CheckpointA:BeforeProvisional");
            RecordingStore.AddProvisional(provisional);
            CheckpointHookForTesting?.Invoke("CheckpointA:AfterProvisional");

            ReFlySessionMarker marker;
            try
            {
                marker = new ReFlySessionMarker
                {
                    SessionId = sessionId,
                    TreeId = provisional.TreeId,
                    ActiveReFlyRecordingId = provisional.RecordingId,
                    OriginChildRecordingId = selected.OriginChildRecordingId,
                    RewindPointId = rp.RewindPointId,
                    InvokedUT = SafeNow(),
                    InvokedRealTime = DateTime.UtcNow.ToString("o"),
                };

                CheckpointHookForTesting?.Invoke("CheckpointB:BeforeMarker");
                scenario.ActiveReFlySessionMarker = marker;
                scenario.BumpSupersedeStateVersion();
                CheckpointHookForTesting?.Invoke("CheckpointB:AfterMarker");
            }
            catch
            {
                // Roll back the provisional to keep the state paired.
                RecordingStore.RemoveCommittedInternal(provisional);
                throw;
            }

            ParsekLog.Info(SessionTag,
                $"Started sess={sessionId} rp={rp.RewindPointId} slot={selected.SlotIndex} " +
                $"provisional={provisional.RecordingId} " +
                $"origin={selected.OriginChildRecordingId ?? "<none>"} " +
                $"tree={provisional.TreeId ?? "<none>"}");
        }

        internal static Recording BuildProvisionalRecording(
            RewindPoint rp, ChildSlot selected, Recording originChild,
            string sessionId, PostLoadStripResult stripResult)
        {
            var rec = new Recording
            {
                RecordingId = "rec_" + Guid.NewGuid().ToString("N"),
                MergeState = MergeState.NotCommitted,
                CreatingSessionId = sessionId,
                SupersedeTargetId = selected.OriginChildRecordingId,
                ProvisionalForRpId = rp.RewindPointId,
                ParentBranchPointId = originChild?.ParentBranchPointId ?? rp.BranchPointId,
                TreeId = originChild?.TreeId,
                VesselPersistentId = stripResult.SelectedVessel != null
                    ? stripResult.SelectedVessel.persistentId
                    : 0u,
                VesselName = stripResult.SelectedVessel != null
                    ? stripResult.SelectedVessel.vesselName
                    : (originChild?.VesselName ?? "Re-fly"),
                PlaybackEnabled = false,
            };
            return rec;
        }

        private static Recording FindRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            // NOTE: raw CommittedRecordings read — this is the only code path
            // that can locate the origin recording by id at invoke time, since
            // the Phase 1-5 types carry the recording id but not a pre-resolved
            // reference. Allowlisted per [ERS-exempt — Phase 6] file-level note.
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return null;
            for (int i = 0; i < committed.Count; i++)
            {
                var r = committed[i];
                if (r == null) continue;
                if (string.Equals(r.RecordingId, recordingId, StringComparison.Ordinal))
                    return r;
            }
            return null;
        }

        private static double SafeNow()
        {
            try { return Planetarium.GetUniversalTime(); }
            catch { return 0.0; }
        }

        internal static string ResolveAbsoluteQuicksavePath(RewindPoint rp)
        {
            if (rp == null || string.IsNullOrEmpty(rp.QuicksaveFilename))
                return null;
            return RecordingPaths.ResolveSaveScopedPath(rp.QuicksaveFilename);
        }

        private static string StripSfsExtension(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return relativePath;
            const string ext = ".sfs";
            if (relativePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return relativePath.Substring(0, relativePath.Length - ext.Length);
            return relativePath;
        }

        private static void HandleQuicksaveMissing(RewindPoint rp)
        {
            if (rp == null) return;
            string abs = ResolveAbsoluteQuicksavePath(rp);
            if (!string.IsNullOrEmpty(abs) && !File.Exists(abs))
            {
                string previousFilename = rp.QuicksaveFilename;
                rp.QuicksaveFilename = null;
                rp.Corrupted = true;
                ParsekLog.Warn(InvokeTag,
                    $"Quicksave cleared: rp={rp.RewindPointId} missingFile='{previousFilename}' " +
                    $"(marking Corrupted)");
            }
        }

        private static void TryRestoreBundle(ReconciliationBundle bundle)
        {
            try
            {
                ReconciliationBundle.Restore(bundle);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(InvokeTag,
                    $"ReconciliationBundle.Restore threw on failure rollback: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------
        // Per-RP precondition cache (60s TTL). Populated by CanInvoke.
        // ----------------------------------------------------------------

        internal struct PreconditionResult
        {
            public bool Passed;
            public string Reason;
            public DateTime CheckedAtUtc;
        }

        internal static class PreconditionCache
        {
            private static readonly Dictionary<string, PreconditionResult> cache
                = new Dictionary<string, PreconditionResult>();
            private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

            internal static void InvalidateForTesting()
            {
                cache.Clear();
            }

            internal static void Invalidate(RewindPoint rp)
            {
                if (rp == null || string.IsNullOrEmpty(rp.RewindPointId)) return;
                cache.Remove(rp.RewindPointId);
            }

            internal static bool IsValid(RewindPoint rp)
            {
                if (rp == null || string.IsNullOrEmpty(rp.RewindPointId)) return false;
                PreconditionResult result;
                if (!cache.TryGetValue(rp.RewindPointId, out result)) return false;
                return DateTime.UtcNow - result.CheckedAtUtc < Ttl;
            }

            internal static PreconditionResult? Get(RewindPoint rp)
            {
                if (rp == null || string.IsNullOrEmpty(rp.RewindPointId)) return null;
                PreconditionResult result;
                if (cache.TryGetValue(rp.RewindPointId, out result)) return result;
                return null;
            }

            internal static void Store(RewindPoint rp, PreconditionResult result)
            {
                if (rp == null || string.IsNullOrEmpty(rp.RewindPointId)) return;
                cache[rp.RewindPointId] = result;
            }
        }

        // ----------------------------------------------------------------
        // Deep-parse PartLoader precondition (§7.29)
        // ----------------------------------------------------------------

        internal static class PartLoaderPrecondition
        {
            // Test seam: when non-null, used instead of PartLoader.getPartInfoByName.
            internal static Func<string, bool> PartExistsOverrideForTesting;

            internal static PreconditionResult Check(RewindPoint rp, string absolutePath)
            {
                var result = new PreconditionResult
                {
                    Passed = true,
                    Reason = null,
                    CheckedAtUtc = DateTime.UtcNow,
                };

                if (rp == null) return result;
                if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
                {
                    result.Passed = false;
                    result.Reason = "Quicksave file missing on disk";
                    return result;
                }

                try
                {
                    ConfigNode root = ConfigNode.Load(absolutePath);
                    if (root == null)
                    {
                        result.Passed = false;
                        result.Reason = "Quicksave file failed to parse";
                        MarkCorrupted(rp, result.Reason);
                        return result;
                    }

                    var missing = new List<string>();
                    CollectMissingParts(root, missing);

                    if (missing.Count > 0)
                    {
                        result.Passed = false;
                        result.Reason = $"Missing parts: {string.Join(", ", missing.ToArray())}";
                        MarkCorrupted(rp, result.Reason);
                    }
                }
                catch (Exception ex)
                {
                    result.Passed = false;
                    result.Reason = $"Quicksave parse threw: {ex.Message}";
                    MarkCorrupted(rp, result.Reason);
                }

                return result;
            }

            private static void CollectMissingParts(ConfigNode node, List<string> missing)
            {
                if (node == null) return;

                // Walk the whole tree; PART nodes may appear under GAME/FLIGHTSTATE/VESSEL/PART.
                foreach (ConfigNode child in node.GetNodes())
                {
                    if (child == null) continue;
                    if (string.Equals(child.name, "PART", StringComparison.Ordinal))
                    {
                        string partName = child.GetValue("name");
                        if (string.IsNullOrEmpty(partName)) continue;
                        if (!PartExists(partName) && !missing.Contains(partName))
                            missing.Add(partName);
                    }
                    else
                    {
                        CollectMissingParts(child, missing);
                    }
                }
            }

            private static bool PartExists(string partName)
            {
                if (PartExistsOverrideForTesting != null)
                    return PartExistsOverrideForTesting(partName);
                try
                {
                    return PartLoader.getPartInfoByName(partName) != null;
                }
                catch
                {
                    return false;
                }
            }

            private static void MarkCorrupted(RewindPoint rp, string reason)
            {
                if (rp == null) return;
                rp.Corrupted = true;
                ParsekLog.Warn(InvokeTag,
                    $"Precondition failed: rp={rp.RewindPointId} reason='{reason}' (marked Corrupted)");
            }
        }

        // ----------------------------------------------------------------
        // GameEvents.onGameStateLoad subscription helper.
        // ----------------------------------------------------------------

        private sealed class PostLoadMonitor
        {
            private EventData<ConfigNode>.OnEvent handler;
            public bool Fired { get; private set; }

            public void Arm()
            {
                Fired = false;
                handler = _ =>
                {
                    Fired = true;
                };
                try
                {
                    GameEvents.onGameStateLoad.Add(handler);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(InvokeTag,
                        $"PostLoadMonitor.Arm: subscribe threw: {ex.Message}");
                }
            }

            public void Unarm()
            {
                if (handler == null) return;
                try
                {
                    GameEvents.onGameStateLoad.Remove(handler);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(InvokeTag,
                        $"PostLoadMonitor.Unarm: unsubscribe threw: {ex.Message}");
                }
                handler = null;
            }
        }
    }
}
