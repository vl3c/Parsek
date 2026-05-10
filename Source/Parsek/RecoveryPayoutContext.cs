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
                HasFundsEarned = hasFundsEarned,
                FundsEarned = fundsEarned,
                BeforeMissionFunds = beforeMissionFunds,
                TotalFunds = totalFunds
            };

            contexts.Add(context);
            return context;
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
                   ";" + DetailFundsEarnedKey + "=" + context.FundsEarned.ToString("R", ic) +
                   ";" + DetailRecoveryFactorKey + "=" + context.RecoveryFactor.ToString("R", ic);
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
