using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Switches on, at its committed UT, a strategy the committed timeline activates while
    /// the clock runs in a scene (todo STRATEGY-ACTIVATION-CROSSED-IN-FLIGHT-NOT-APPLIED).
    /// After a rewind to before a committed Space Center activation, stock has the strategy
    /// off; <see cref="StrategyExpiryGate"/> only sees ACTIVE strategies and
    /// <see cref="StrategyStatePatcher.PatchStrategies"/> runs only from a
    /// ledger recalculation, so a flight that flew across the activation earned its contract
    /// rewards without the strategy's diversion. The committed career paid the setup cost at
    /// that UT, so the strategy is switched on there exactly, the way the patch does
    /// (<c>Strategy.Load</c> with the committed date and commitment, no cost, no capture).
    ///
    /// <para>Fires only on a CROSSING: a committed activation dated after the previous
    /// frame's clock and at or before this frame's. A strategy the committed timeline has on
    /// but stock has off for another reason (a later patch's own decision, a conflict, a mod)
    /// is never fought every frame.</para>
    ///
    /// <para><b>Cost.</b> Runs once per frame from the <c>StrategySystem.Update</c> postfix.
    /// The frame path is one clock read and one compare against the cached next activation
    /// UT; the index is re-read at most once a second of real time (a new committed row)
    /// or when the clock reaches that UT.</para>
    /// </summary>
    internal static class StrategyActivationGate
    {
        private const string Tag = "StrategyReservation";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        internal const double IndexRefreshRealSeconds = 1.0;

        private static double lastNowUT = double.NaN;
        private static object lastSystem;
        private static double nextActivationUT = double.PositiveInfinity;
        private static CommittedFutureIndex cachedIndex;
        private static double lastRefreshRealTime = double.NegativeInfinity;

        /// <summary>Test seams. Null in production.</summary>
        internal static Func<double> RealTimeProviderForTesting;
        internal static Func<bool> IsCareerProviderForTesting;

        /// <summary>
        /// Pure: the ids among <paramref name="stockInactiveIds"/> whose committed rows hold
        /// an activation dated in (<paramref name="previousUT"/>, <paramref name="nowUT"/>] and
        /// leave the strategy on at <paramref name="nowUT"/> (the latest row at or before now
        /// is that activation, not a later deactivation). Sorted, ordinal.
        /// </summary>
        internal static List<string> SelectCrossedActivations(
            IEnumerable<string> stockInactiveIds,
            Func<string, StrategyCommittedRows> rowsOf,
            double previousUT,
            double nowUT)
        {
            var result = new List<string>();
            if (stockInactiveIds == null || rowsOf == null) return result;
            if (double.IsNaN(previousUT) || double.IsNaN(nowUT) || nowUT <= previousUT) return result;
            foreach (string id in stockInactiveIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                var rows = rowsOf(id);
                if (rows == null || !rows.HasActivation) continue;
                int last = rows.LastAtOrBefore(nowUT);
                if (last < 0 || !rows.IsActivation(last)) continue;
                if (rows.UTAt(last) <= previousUT) continue;
                result.Add(id);
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>
        /// Pure: the earliest committed activation UT after <paramref name="afterUT"/> among
        /// <paramref name="entries"/> (UT ascending or not), or +Infinity.
        /// </summary>
        internal static double NextActivationAfter(IReadOnlyList<CommittedFutureEntry> entries, double afterUT)
        {
            double next = double.PositiveInfinity;
            if (entries == null) return next;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e != null && e.UT > afterUT && e.UT < next) next = e.UT;
            }
            return next;
        }

        /// <summary>
        /// Pure: whether this frame can have crossed a committed activation. Not on the first
        /// frame of a stock system (a scene change or a load), nor when the clock did not move
        /// forward (pause, or a rewind back).
        /// </summary>
        internal static bool IsCrossingFrame(double previousUT, double nowUT, bool newSystem)
        {
            return !newSystem && !double.IsNaN(previousUT) && !double.IsNaN(nowUT) && nowUT > previousUT;
        }

        /// <summary>The postfix body.</summary>
        internal static void OnStrategySystemUpdate(Strategies.StrategySystem system)
        {
            if (system == null || system.Strategies == null) return;
            if (GameStateRecorder.IsReplayingActions || GameStateRecorder.SuppressResourceEvents) return;
            if (!IsCareer()) return;
            if (ParsekScenario.IsOnLoadInProgress) return;

            double now = CommittedFutureIndexCache.CurrentUT();
            double previous = lastNowUT;
            lastNowUT = now;
            bool newSystem = !ReferenceEquals(system, lastSystem);
            lastSystem = system;
            if (!IsCrossingFrame(previous, now, newSystem))
            {
                // First frame of this stock system (a scene change or a load, whose clock
                // can be anywhere), or the clock went back (a rewind): nothing was crossed
                // here; the state patch owns the state at a load. Re-read the index from the
                // new clock.
                Refresh(now, force: true);
                return;
            }

            double realNow = RealTime();
            if (realNow - lastRefreshRealTime >= IndexRefreshRealSeconds)
                Refresh(previous, force: false);
            if (now < nextActivationUT) return;

            var index = cachedIndex ?? CommittedFutureIndexCache.Current;
            var inactive = new List<string>();
            var byId = new Dictionary<string, Strategies.Strategy>(StringComparer.Ordinal);
            for (int i = 0; i < system.Strategies.Count; i++)
            {
                var s = system.Strategies[i];
                string id = s?.Config?.Name;
                if (string.IsNullOrEmpty(id) || byId.ContainsKey(id)) continue;
                byId[id] = s;
                // A one-shot exchanger's exchange is already a ledger row; switching it on
                // would pay it again (StrategyStatePatcher.IsOneShotExchanger).
                if (!s.IsActive && !StrategyStatePatcher.IsOneShotExchanger(s)) inactive.Add(id);
            }

            var crossed = SelectCrossedActivations(inactive, index.StrategyRows, previous, now);
            int activated = 0, failed = 0;
            for (int i = 0; i < crossed.Count; i++)
            {
                string id = crossed[i];
                var rows = index.StrategyRows(id);
                double activateUT = rows.UTAt(rows.LastAtOrBefore(now));
                float factor = StrategyStatePatcher.CommittedActivationFactor(id, activateUT, byId[id].Factor);
                using (SuppressionGuard.ResourcesAndReplay())
                    byId[id].Load(StrategyStatePatcher.BuildActivationNode(id, activateUT, factor));
                if (byId[id].IsActive)
                {
                    activated++;
                    ParsekLog.Info(Tag,
                        "Committed strategy activation reached: strategy=" + id
                        + " activateUT=" + activateUT.ToString("F1", IC)
                        + " factor=" + factor.ToString("F2", IC)
                        + " nowUT=" + now.ToString("F1", IC) + " (switched on without cost or capture)");
                }
                else
                {
                    failed++;
                    ParsekLog.Warn(Tag, "Committed strategy activation reached but stock left strategy="
                                        + id + " off (nowUT=" + now.ToString("F1", IC) + ")");
                }
            }
            if (activated > 0)
                StrategyStatePatcher.RefreshAdministration();
            Refresh(now, force: true);
        }

        private static void Refresh(double afterUT, bool force)
        {
            var index = CommittedFutureIndexCache.Current;
            lastRefreshRealTime = RealTime();
            if (!force && ReferenceEquals(index, cachedIndex)) return;
            cachedIndex = index;
            nextActivationUT = NextActivationAfter(
                index.FutureEntriesOfKind(CommittedFutureKind.StrategyActivate, afterUT), afterUT);
        }

        private static double RealTime()
        {
            var seam = RealTimeProviderForTesting;
            if (seam != null) return seam();
            return ReadLiveRealTime();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double ReadLiveRealTime()
        {
            return UnityEngine.Time.realtimeSinceStartup;
        }

        private static bool IsCareer()
        {
            var seam = IsCareerProviderForTesting;
            if (seam != null) return seam();
            try
            {
                return ReadLiveIsCareer();
            }
            catch (Exception)
            {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ReadLiveIsCareer()
        {
            return HighLogic.CurrentGame != null && HighLogic.CurrentGame.Mode == Game.Modes.CAREER;
        }

        internal static void ResetForTesting()
        {
            lastNowUT = double.NaN;
            lastSystem = null;
            nextActivationUT = double.PositiveInfinity;
            cachedIndex = null;
            lastRefreshRealTime = double.NegativeInfinity;
            RealTimeProviderForTesting = null;
            IsCareerProviderForTesting = null;
        }
    }

    /// <summary>
    /// Postfix on the private per-frame <c>Strategies.StrategySystem.Update()</c> (stock calls
    /// <c>Update()</c> only on active strategies there; decompiled KSP 1.12.5). Hosts
    /// <see cref="StrategyActivationGate"/>.
    /// </summary>
    [HarmonyPatch]
    internal static class StrategySystemUpdateActivationPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("StrategyReservation",
                    "StrategySystem.Update() not found - a committed strategy activation the clock reaches mid-scene waits for the next recalculation");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return typeof(Strategies.StrategySystem).GetMethod(
                "Update",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                Type.EmptyTypes,
                null);
        }

        static void Postfix(Strategies.StrategySystem __instance)
        {
            if (ParsekGameModeGate.CheckInert("StrategySystemUpdateActivationPatch.Postfix")) return; // S9 game-mode gate
            try
            {
                StrategyActivationGate.OnStrategySystemUpdate(__instance);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("StrategyReservation", "strategy-activation-gate-threw",
                    "StrategySystem.Update postfix threw: " + ex.Message);
            }
        }
    }
}
