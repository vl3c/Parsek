using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    internal struct RecoveredVesselIdentity
    {
        public string RawName;
        public string NormalizedName;

        public bool HasName =>
            !string.IsNullOrEmpty(RawName) ||
            !string.IsNullOrEmpty(NormalizedName);

        public string DisplayName =>
            !string.IsNullOrEmpty(NormalizedName) ? NormalizedName : (RawName ?? "");

        public static RecoveredVesselIdentity FromRawName(string rawName)
        {
            return FromNames(rawName, Recording.ResolveLocalizedName(rawName));
        }

        public static RecoveredVesselIdentity FromNames(string rawName, string normalizedName)
        {
            return new RecoveredVesselIdentity
            {
                RawName = rawName ?? "",
                NormalizedName = normalizedName ?? rawName ?? ""
            };
        }

        public bool Matches(RecoveredVesselIdentity other)
        {
            if (!HasName || !other.HasName)
                return false;

            return NamesEqual(RawName, other.RawName) ||
                   NamesEqual(RawName, other.NormalizedName) ||
                   NamesEqual(NormalizedName, other.RawName) ||
                   NamesEqual(NormalizedName, other.NormalizedName);
        }

        public bool MatchesName(string name)
        {
            if (string.IsNullOrEmpty(name) || !HasName)
                return false;

            if (NamesEqual(name, RawName) || NamesEqual(name, NormalizedName))
                return true;

            string resolved = Recording.ResolveLocalizedName(name);
            return NamesEqual(resolved, RawName) || NamesEqual(resolved, NormalizedName);
        }

        public string FormatForLog()
        {
            if (string.IsNullOrEmpty(RawName) ||
                string.Equals(RawName, NormalizedName, StringComparison.Ordinal))
                return $"vessel='{DisplayName}'";

            return $"vessel='{DisplayName}' rawVessel='{RawName}'";
        }

        private static bool NamesEqual(string a, string b)
        {
            return !string.IsNullOrEmpty(a) &&
                   !string.IsNullOrEmpty(b) &&
                   string.Equals(a, b, StringComparison.Ordinal);
        }
    }

    internal sealed class RecoveryPayoutContext
    {
        public uint PersistentId;
        public RecoveredVesselIdentity Identity;
        public VesselType VesselType;
        public double Ut;
        public float RecoveryFactor;
        public bool HasFundsEarned;
        public double FundsEarned;
        public double BeforeMissionFunds;
        public double TotalFunds;
        public bool UsedForFundsEvent;
    }

    internal static class RecoveryPayoutContextStore
    {
        private const double ContextMatchWindowSeconds = 5.0;
        private const string DetailVesselKey = "vessel";
        private const string DetailRawVesselKey = "rawVessel";
        private const string DetailPidKey = "pid";
        private const string DetailVesselTypeKey = "vesselType";
        private const string DetailFundsEarnedKey = "fundsEarned";
        private const string DetailRecoveryFactorKey = "recoveryFactor";

        private static readonly List<RecoveryPayoutContext> contexts =
            new List<RecoveryPayoutContext>();

        internal static void ResetForTesting()
        {
            contexts.Clear();
        }

        internal static void Clear(string reason)
        {
            if (contexts.Count > 0)
            {
                ParsekLog.Verbose("RecoveryPayoutContext",
                    $"Clear ({reason ?? ""}): dropping {contexts.Count} recovery payout context(s)");
            }

            contexts.Clear();
        }

        internal static RecoveryPayoutContext Remember(
            uint persistentId,
            string rawVesselName,
            VesselType vesselType,
            double ut,
            float recoveryFactor,
            bool hasFundsEarned,
            double fundsEarned,
            double beforeMissionFunds,
            double totalFunds)
        {
            var identity = RecoveredVesselIdentity.FromRawName(rawVesselName);
            if (!identity.HasName)
                return null;

            TrimExpired(ut);

            var context = new RecoveryPayoutContext
            {
                PersistentId = persistentId,
                Identity = identity,
                VesselType = vesselType,
                Ut = ut,
                RecoveryFactor = recoveryFactor,
                HasFundsEarned = hasFundsEarned &&
                                 FundsSnapshotIsAuthoritative(
                                     beforeMissionFunds,
                                     fundsEarned,
                                     totalFunds),
                FundsEarned = fundsEarned,
                BeforeMissionFunds = beforeMissionFunds,
                TotalFunds = totalFunds
            };

            contexts.Add(context);
            return context;
        }

        /// <summary>
        /// Fills in the payout half of an already-remembered context once stock has actually
        /// computed it.
        ///
        /// <para>
        /// <b>Why a second seam.</b> <see cref="Remember"/> runs at
        /// <c>onVesselRecoveryProcessing</c>, where the dialog's payout fields are
        /// structurally not yet populated (see <see cref="FundsSnapshotIsAuthoritative"/>),
        /// so every production context starts out payout-unknown. KSP fires
        /// <c>onVesselRecoveryProcessingComplete</c> as the LAST statement of
        /// <c>VesselRecovery.OnVesselRecovered</c>, immediately after stamping
        /// <c>totalFunds</c> and adding the currency-modifier delta into <c>fundsEarned</c> -
        /// the first moment the snapshot is coherent. Refreshing here keeps the zero-payout /
        /// below-threshold suppression working on a REAL expectation instead of leaving it
        /// permanently unknown.
        /// </para>
        ///
        /// <para>
        /// Never downgrades: a context that is already authoritative is left alone, and an
        /// incoherent completion snapshot (or the <c>quick</c> recovery path, which fires
        /// completion with a null dialog and so never reaches here) leaves the context
        /// unknown, which routes to deferred pairing - the safe direction.
        /// </para>
        /// </summary>
        internal static bool TryRefreshPayoutFromCompletion(
            uint persistentId,
            RecoveredVesselIdentity identity,
            double ut,
            double fundsEarned,
            double beforeMissionFunds,
            double totalFunds,
            out RecoveryPayoutContext context)
        {
            if (!TryFind(persistentId, identity, ut, out context))
                return false;

            if (context.HasFundsEarned)
                return false;

            if (!FundsSnapshotIsAuthoritative(beforeMissionFunds, fundsEarned, totalFunds))
                return false;

            context.FundsEarned = fundsEarned;
            context.BeforeMissionFunds = beforeMissionFunds;
            context.TotalFunds = totalFunds;
            context.HasFundsEarned = true;
            return true;
        }

        internal static bool TryFind(
            uint persistentId,
            RecoveredVesselIdentity identity,
            double ut,
            out RecoveryPayoutContext context)
        {
            int index = FindBestIndex(persistentId, identity, ut);
            if (index < 0)
            {
                context = null;
                return false;
            }

            context = contexts[index];
            return true;
        }

        internal static bool TryFindForFundsEvent(double ut, out RecoveryPayoutContext context)
        {
            return TryFindForFundsEvent(ut, double.NaN, out context);
        }

        internal static bool TryFindForFundsEvent(
            double ut,
            double fundsDelta,
            out RecoveryPayoutContext context)
        {
            TrimExpired(ut);

            if (!double.IsNaN(fundsDelta) && !double.IsInfinity(fundsDelta))
            {
                for (int i = contexts.Count - 1; i >= 0; i--)
                {
                    var candidate = contexts[i];
                    if (candidate.UsedForFundsEvent)
                        continue;
                    if (Math.Abs(candidate.Ut - ut) > ContextMatchWindowSeconds)
                        continue;
                    if (!FundsEarnedMatchesDelta(candidate, fundsDelta))
                        continue;

                    context = candidate;
                    return true;
                }
            }

            for (int i = contexts.Count - 1; i >= 0; i--)
            {
                var candidate = contexts[i];
                if (candidate.UsedForFundsEvent)
                    continue;
                if (Math.Abs(candidate.Ut - ut) > ContextMatchWindowSeconds)
                    continue;
                if (!double.IsNaN(fundsDelta) &&
                    !double.IsInfinity(fundsDelta) &&
                    candidate.HasFundsEarned)
                    continue;

                context = candidate;
                return true;
            }

            context = null;
            return false;
        }

        internal static string BuildFundsEventDetail(RecoveryPayoutContext context)
        {
            if (context == null || !context.Identity.HasName)
                return null;

            var ic = CultureInfo.InvariantCulture;
            return DetailVesselKey + "=" + Escape(context.Identity.DisplayName) +
                   ";" + DetailRawVesselKey + "=" + Escape(context.Identity.RawName) +
                   ";" + DetailPidKey + "=" + context.PersistentId.ToString(ic) +
                   ";" + DetailVesselTypeKey + "=" + Escape(context.VesselType.ToString()) +
                   ";" + DetailFundsEarnedKey + "=" + FormatExpectedFundsForDetail(context) +
                   ";" + DetailRecoveryFactorKey + "=" + context.RecoveryFactor.ToString("R", ic);
        }

        /// <summary>
        /// Renders the detail's <c>fundsEarned</c> field, which is stock's EXPECTATION as of
        /// the moment the event was stamped - not the amount actually paid (that is the
        /// event's own <c>valueAfter - valueBefore</c>, and it is the only figure the ledger
        /// ever uses).
        ///
        /// <para>
        /// Writes the literal <c>(unknown)</c> rather than a raw <c>0</c> when the expectation
        /// is not yet known. At the processing seam it is STRUCTURALLY zero - KSP has not
        /// computed the payout when <see cref="Remember"/> runs (see
        /// <see cref="FundsSnapshotIsAuthoritative"/>) - so a raw <c>0</c> would sit in the
        /// persisted <c>events.pgse</c> of every recovery, permanently, next to a real
        /// non-zero credit, and read to anyone debugging a collected save as "stock expected
        /// to pay nothing here". That misreading is exactly the bug this file was changed to
        /// fix; it should not survive in the artifact. Nothing parses this field back
        /// (<see cref="ExtractIdentityFromFundsEventDetail"/> reads only the vessel keys), and
        /// a future parser meets a token it must handle rather than a plausible zero.
        /// </para>
        /// </summary>
        private static string FormatExpectedFundsForDetail(RecoveryPayoutContext context)
        {
            return context.HasFundsEarned
                ? context.FundsEarned.ToString("R", CultureInfo.InvariantCulture)
                : "(unknown)";
        }

        internal static bool TryBuildFundsEventDetail(double ut, out string detail)
        {
            return TryBuildFundsEventDetail(ut, double.NaN, out detail);
        }

        internal static bool TryBuildFundsEventDetail(
            double ut,
            double fundsDelta,
            out string detail)
        {
            if (TryFindForFundsEvent(ut, fundsDelta, out RecoveryPayoutContext context))
            {
                detail = BuildFundsEventDetail(context);
                if (!string.IsNullOrEmpty(detail))
                    context.UsedForFundsEvent = true;
                return !string.IsNullOrEmpty(detail);
            }

            detail = null;
            return false;
        }

        internal static RecoveredVesselIdentity ExtractIdentityFromFundsEventDetail(string detail)
        {
            if (string.IsNullOrEmpty(detail))
                return default(RecoveredVesselIdentity);

            string normalizedName = ExtractDetailValue(detail, DetailVesselKey);
            string rawName = ExtractDetailValue(detail, DetailRawVesselKey);
            if (!string.IsNullOrEmpty(normalizedName) || !string.IsNullOrEmpty(rawName))
                return RecoveredVesselIdentity.FromNames(rawName, normalizedName);

            // Backward-compatible unit-test/legacy path: older recovery events used
            // detail as the plain vessel name, not key/value metadata.
            return RecoveredVesselIdentity.FromRawName(detail);
        }

        internal static string DescribeExpectedFunds(RecoveryPayoutContext context)
        {
            if (context == null || !context.HasFundsEarned)
                return "expectedFunds=(unknown)";

            return "expectedFunds=" +
                   context.FundsEarned.ToString("F1", CultureInfo.InvariantCulture);
        }

        private static bool FundsEarnedMatchesDelta(
            RecoveryPayoutContext context,
            double fundsDelta)
        {
            if (context == null || !context.HasFundsEarned)
                return false;

            double expected = context.FundsEarned;
            double tolerance = Math.Max(
                0.01,
                Math.Abs(expected - (double)(float)expected));
            return Math.Abs(expected - fundsDelta) <= tolerance;
        }

        /// <summary>
        /// True only when a <c>MissionRecoveryDialog</c> funds snapshot is INTERNALLY
        /// COHERENT and can therefore be read as stock's own payout expectation.
        ///
        /// <para>
        /// <b>Why coherence and not "any field is non-zero".</b> Decompile-verified against
        /// KSP 1.12.5 <c>VesselRecovery.OnVesselRecovered</c>: the dialog's
        /// <c>beforeMissionFunds</c> is stamped BEFORE
        /// <c>GameEvents.onVesselRecoveryProcessing.Fire</c>, while <c>totalFunds</c> is
        /// stamped AFTER it and <c>fundsEarned</c> is never assigned before the fire at all
        /// (the part-value payout is produced by the event's own subscribers). So at
        /// Parsek's recovery-processing seam the snapshot is structurally
        /// <c>before=&lt;real&gt;, earned=0, total=0</c> - a shape the old "any field is
        /// non-zero" heuristic accepted as an authoritative "stock will pay zero", which
        /// then suppressed the deferred pairing for a recovery stock went on to pay in full
        /// (CAREER-RECOVERY-FUNDS-NOT-LEDGERED: 4558 funds observed as a
        /// <c>FundsChanged(VesselRecovery)</c> event and never written as a ledger row).
        /// </para>
        ///
        /// <para>
        /// A snapshot taken once stock HAS computed the payout satisfies
        /// <c>total == before + earned</c>; one taken before it does not. Reading the
        /// incoherent shape as "unknown" keeps the deferred-pairing path, which takes its
        /// amount from the actual <c>FundsChanged(VesselRecovery)</c> delta rather than from
        /// the dialog - so the guard now fails OPEN (queue and pair) instead of CLOSED
        /// (silently drop a real credit).
        /// </para>
        /// </summary>
        internal static bool FundsSnapshotIsAuthoritative(
            double beforeMissionFunds,
            double fundsEarned,
            double totalFunds)
        {
            if (double.IsNaN(beforeMissionFunds) ||
                double.IsNaN(fundsEarned) ||
                double.IsNaN(totalFunds) ||
                double.IsInfinity(beforeMissionFunds) ||
                double.IsInfinity(fundsEarned) ||
                double.IsInfinity(totalFunds))
                return false;

            // A real zero-payout recovery still carries the player's mission funds.
            // The default all-zero snapshot stays "unknown" so we keep the deferred-pairing
            // path instead of silently suppressing a payout.
            if (beforeMissionFunds == 0.0 && fundsEarned == 0.0 && totalFunds == 0.0)
                return false;

            double expectedTotal = beforeMissionFunds + fundsEarned;
            double tolerance = Math.Max(
                FundsSnapshotCoherenceToleranceFunds,
                Math.Abs(expectedTotal - (double)(float)expectedTotal));
            return Math.Abs(totalFunds - expectedTotal) <= tolerance;
        }

        /// <summary>
        /// Absolute funds tolerance for the <see cref="FundsSnapshotIsAuthoritative"/>
        /// coherence check. Stock stores the three fields as doubles but computes them from
        /// float currency, so an exact equality test would reject a perfectly good snapshot.
        /// </summary>
        private const double FundsSnapshotCoherenceToleranceFunds = 0.01;

        private static int FindBestIndex(
            uint persistentId,
            RecoveredVesselIdentity identity,
            double ut)
        {
            int pidMatch = FindBestIndex(
                ut,
                candidate => persistentId != 0 &&
                             candidate.PersistentId != 0 &&
                             candidate.PersistentId == persistentId);
            if (pidMatch >= 0)
                return pidMatch;

            if (!identity.HasName)
                return -1;

            return FindBestIndex(
                ut,
                candidate => candidate.Identity.Matches(identity));
        }

        private static int FindBestIndex(
            double ut,
            Func<RecoveryPayoutContext, bool> predicate)
        {
            int bestIndex = -1;
            double bestDistance = double.MaxValue;

            // LOAD-BEARING PAIR: tail-first iteration + STRICT `<`.
            //
            // Together they mean the NEWEST candidate wins a tie, and ties are the normal case
            // here - two recoveries of the same craft inside the 5s window carry the same pid
            // and often the same UT to the millisecond. TryRefreshPayoutFromCompletion depends
            // on this to reach the context Remember just added for the craft currently being
            // recovered, rather than an older sibling already stamped onto an earlier funds
            // event.
            //
            // Flipping EITHER half silently redirects refreshes to the wrong context: iterating
            // head-first, or relaxing to `<=`, both hand the tie to the OLDEST candidate. No
            // assertion at the call site would catch it - the refresh would simply fill a stale
            // context and leave the live one unknown - so change these two lines only together
            // and only deliberately. Held by
            // TryRefreshPayoutFromCompletion_SamePidSameUt_FillsTheNewestContext.
            for (int i = contexts.Count - 1; i >= 0; i--)
            {
                var candidate = contexts[i];
                double distance = Math.Abs(candidate.Ut - ut);
                if (distance > ContextMatchWindowSeconds)
                    continue;

                if (!predicate(candidate))
                    continue;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private static void TrimExpired(double currentUt)
        {
            // Recovery-processing and funds callbacks are only expected to pair in
            // monotonic live-time order. Rewind paths clear the store before old UTs
            // are replayed, so a symmetric window keeps both forward stale contexts
            // and unexpected future contexts from stamping a new event.
            for (int i = contexts.Count - 1; i >= 0; i--)
            {
                if (Math.Abs(contexts[i].Ut - currentUt) > ContextMatchWindowSeconds)
                    contexts.RemoveAt(i);
            }
        }

        private static string ExtractDetailValue(string detail, string key)
        {
            if (string.IsNullOrEmpty(detail) || string.IsNullOrEmpty(key))
                return null;

            string prefix = key + "=";
            string[] parts = detail.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i] ?? "";
                if (!part.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                return Unescape(part.Substring(prefix.Length));
            }

            return null;
        }

        private static string Escape(string value)
        {
            return Uri.EscapeDataString(value ?? "");
        }

        private static string Unescape(string value)
        {
            return Uri.UnescapeDataString(value ?? "");
        }
    }
}
