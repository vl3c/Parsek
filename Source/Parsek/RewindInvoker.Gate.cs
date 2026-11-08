using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Parsek
{
    // The invoke gate: CanInvoke and its evaluation, the future-rewind-point test and its clock read, the decision log line, the invokable-scene test and the cached precondition types (part loader).
    internal static partial class RewindInvoker
    {
        /// <summary>
        /// Returns <c>true</c> if the Rewind button for <paramref name="rp"/>
        /// should be enabled. Checks five preconditions per §6.3 / §7.22:
        /// <list type="bullet">
        ///   <item><description>Scene is FLIGHT / SPACECENTER / TRACKSTATION — not a scene transition (§7.22)</description></item>
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
            => CanInvoke(rp, null, out reason);

        /// <summary>
        /// <see cref="CanInvoke(RewindPoint, out string)"/> for one slot of the rewind
        /// point. With <paramref name="selected"/> set it also re-checks the design
        /// section 2.12 Rule 1 value gate (precondition 7, design section 6.8): a slot
        /// whose retire set holds a sealing row is refused with
        /// <c>recordingAction:Type:ActionId</c>, so a ledger that changed after the
        /// Unfinished Flights row was drawn cannot slip through.
        /// </summary>
        internal static bool CanInvoke(RewindPoint rp, ChildSlot selected, out string reason)
        {
            bool canInvoke = EvaluateCanInvoke(rp, selected, out reason);
            LogCanInvokeDecision(rp, canInvoke, reason);
            return canInvoke;
        }

        private static bool EvaluateCanInvoke(RewindPoint rp, ChildSlot selected, out string reason)
        {
            if (rp == null)
            {
                reason = "rewind point is null";
                return false;
            }

            // §7.22: reject during scene transitions. A scene reload is already
            // in flight, so firing another LoadGame on top of it would either
            // stomp state or deadlock Unity's loader.
            if (!IsInvokableScene(HighLogic.LoadedScene))
            {
                reason = "Scene transition in progress - please wait";
                return false;
            }

            // Context-level guard: another invocation is already mid-flight
            // (pre-load phase captured, post-load hasn't consumed yet).
            if (RewindInvokeContext.Pending)
            {
                reason = "Another rewind invocation is already in flight";
                return false;
            }

            if (rp.Corrupted)
            {
                reason = "Rewind point is marked corrupted";
                return false;
            }

            // Operator ruling 2026-09-23: an RP survives a plain rewind, but one that
            // lies in the player's future cannot be re-flown until the clock reaches it.
            double nowUT = ResolveNowUTForCanInvoke();
            if (IsRewindPointInFuture(rp.UT, nowUT))
            {
                reason = FutureRewindPointReason;
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

            string sealingSummary;
            if (SlotRetireSetHoldsSealingRow(rp, selected, out sealingSummary))
            {
                reason = UnfinishedFlightClassifier.RecordingActionReasonPrefix + sealingSummary;
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
        /// Precondition 7 (design section 2.12 Rule 1): the selected slot's retire set,
        /// rooted at its current effective tip, holds a sealing row. No slot (the
        /// rewind-point-only overload) skips the check.
        /// </summary>
        private static bool SlotRetireSetHoldsSealingRow(
            RewindPoint rp, ChildSlot selected, out string summary)
        {
            summary = null;
            if (rp == null || selected == null)
                return false;
            Recording tip = SupersedeCommit.ResolveRetireSetTip(null, selected);
            if (tip == null)
            {
                ParsekLog.VerboseRateLimited(InvokeTag,
                    "caninvoke-retire-no-tip|" + (rp.RewindPointId ?? "<no-id>") + "|"
                        + selected.SlotIndex.ToString(CultureInfo.InvariantCulture),
                    $"CanInvoke: value gate skipped rp={rp.RewindPointId ?? "<no-id>"} " +
                    $"slot={selected.SlotIndex.ToString(CultureInfo.InvariantCulture)} " +
                    $"origin={selected.OriginChildRecordingId ?? "<none>"} reason=tip-unresolved");
                return false;
            }
            return SupersedeCommit.RetireSetHoldsSealingRow(tip, rp, selected, out summary);
        }

        /// <summary>
        /// Pure future-RP decision: true when <paramref name="rewindPointUT"/> is later than
        /// <paramref name="nowUT"/> by more than <see cref="RewindPointFutureToleranceSeconds"/>.
        /// Exactly at the RP UT is NOT the future. An unknown clock (NaN, no Planetarium)
        /// cannot place the RP, so the gate stays open rather than blocking every RP; every
        /// invokable scene (FLIGHT / SPACECENTER / TRACKSTATION) has a clock.
        /// </summary>
        internal static bool IsRewindPointInFuture(double rewindPointUT, double nowUT)
        {
            if (double.IsNaN(nowUT) || double.IsNaN(rewindPointUT)
                || double.IsInfinity(nowUT) || double.IsInfinity(rewindPointUT))
                return false;
            return rewindPointUT - nowUT > RewindPointFutureToleranceSeconds;
        }

        /// <summary>
        /// The future-RP gate against the live clock, for a caller that must decide
        /// BEFORE it tears state down (RevertInterceptor.RetryHandler).
        /// </summary>
        internal static bool IsRewindPointInFutureNow(RewindPoint rp, out double nowUT)
        {
            nowUT = ResolveNowUTForCanInvoke();
            return rp != null && IsRewindPointInFuture(rp.UT, nowUT);
        }

        private static double ResolveNowUTForCanInvoke()
        {
            var hook = NowUtProviderForTesting;
            if (hook != null)
                return hook();
            try { return ReadPlanetariumUniversalTimeCore(); }
            catch (Exception) { return double.NaN; }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static double ReadPlanetariumUniversalTimeCore()
        {
            if (Planetarium.fetch == null)
                return double.NaN;
            return Planetarium.GetUniversalTime();
        }

        private static void LogCanInvokeDecision(RewindPoint rp, bool canInvoke, string reason)
        {
            string rpId = GetRewindPointIdForLog(rp);
            string normalizedReason = string.IsNullOrEmpty(reason) ? "<none>" : reason;
            string identity = "CanInvoke|" + rpId;
            string stateKey = canInvoke ? "enabled" : "disabled|" + normalizedReason;
            string quicksave = rp == null || string.IsNullOrEmpty(rp.QuicksaveFilename)
                ? "<none>"
                : rp.QuicksaveFilename;
            string absoluteQuicksave = null;
            if (rp != null && !string.IsNullOrEmpty(rp.QuicksaveFilename))
            {
                try { absoluteQuicksave = ResolveAbsoluteQuicksavePath(rp); }
                catch (Exception ex) { absoluteQuicksave = "<resolve-error:" + ex.GetType().Name + ">"; }
            }

            ParsekLog.VerboseOnChange(
                InvokeTag,
                identity,
                stateKey,
                canInvoke
                    ? $"CanInvoke: enabled rp={rpId} scene={SafeLoadedSceneForCanInvokeLog()} " +
                      $"quicksave='{quicksave}' path='{FormatCanInvokePath(absoluteQuicksave)}'"
                    : $"CanInvoke: disabled rp={rpId} reason='{normalizedReason}' " +
                      $"rpUT={FormatUtForCanInvokeLog(rp?.UT)} nowUT={FormatUtForCanInvokeLog(ResolveNowUTForCanInvoke())} " +
                      $"scene={SafeLoadedSceneForCanInvokeLog()} corrupted={FormatNullableBool(rp?.Corrupted)} " +
                      $"quicksave='{quicksave}' path='{FormatCanInvokePath(absoluteQuicksave)}' " +
                      $"pendingInvoke={RewindInvokeContext.Pending} activeSession={FormatActiveSessionForCanInvokeLog()}");
        }

        private static string GetRewindPointIdForLog(RewindPoint rp)
        {
            if (rp == null)
                return "<null>";
            return string.IsNullOrEmpty(rp.RewindPointId) ? "<no-id>" : rp.RewindPointId;
        }

        private static string FormatCanInvokePath(string path)
        {
            return string.IsNullOrEmpty(path) ? "<none>" : path;
        }

        private static string FormatUtForCanInvokeLog(double? ut)
        {
            if (!ut.HasValue) return "<null>";
            return double.IsNaN(ut.Value) ? "<unknown>" : ut.Value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string FormatNullableBool(bool? value)
        {
            return value.HasValue ? value.Value.ToString() : "<null>";
        }

        private static string SafeLoadedSceneForCanInvokeLog()
        {
            try { return HighLogic.LoadedScene.ToString(); }
            catch (Exception ex) { return "<scene-error:" + ex.GetType().Name + ">"; }
        }

        private static string FormatActiveSessionForCanInvokeLog()
        {
            try
            {
                var scenario = ParsekScenario.Instance;
                if (object.ReferenceEquals(null, scenario) || scenario.ActiveReFlySessionMarker == null)
                    return "none";

                string sessionId = scenario.ActiveReFlySessionMarker.SessionId;
                return string.IsNullOrEmpty(sessionId) ? "<no-session>" : sessionId;
            }
            catch (Exception ex)
            {
                return "<session-error:" + ex.GetType().Name + ">";
            }
        }

        /// <summary>§7.22: scenes where Rewind invocation is allowed.</summary>
        private static bool IsInvokableScene(GameScenes scene)
        {
            return scene == GameScenes.FLIGHT
                || scene == GameScenes.SPACECENTER
                || scene == GameScenes.TRACKSTATION;
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

            /// <summary>
            /// Clears every cached precondition result. Called on scene unload
            /// so the dict does not grow unbounded across long sessions.
            /// </summary>
            internal static void ClearAll()
            {
                int n = cache.Count;
                cache.Clear();
                if (n > 0)
                    ParsekLog.Verbose(InvokeTag,
                        $"PreconditionCache.ClearAll: dropped {n} cached result(s)");
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
    }
}
