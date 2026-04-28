using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Helper for VesselRollout ledger actions and later recording adoption.
    /// Kept behind LedgerOrchestrator wrappers so the public test/call surface stays stable.
    /// </summary>
    internal static class LedgerRolloutAdoption
    {
        private const string Tag = "LedgerOrchestrator";

        internal struct RolloutAdoptionContext
        {
            public uint VesselPersistentId;
            public string VesselName;
            public string LaunchSiteName;
            public bool IsLegacyBareKey;
        }

        internal static void RecordVesselRolloutSpending(
            double ut,
            double cost,
            RolloutAdoptionContext context,
            Func<int> allocateKscSequence,
            Action<GameAction, double> reconcileKscAction,
            Action recalculateAndPatch)
        {
            if (cost <= 0)
            {
                ParsekLog.Verbose(Tag,
                    $"OnVesselRolloutSpending: non-positive cost={cost:F1} at UT={ut:F1}, skipping");
                return;
            }

            int sequence = allocateKscSequence();
            var action = new GameAction
            {
                UT = ut,
                Type = GameActionType.FundsSpending,
                RecordingId = null,
                FundsSpent = (float)cost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                DedupKey = BuildRolloutDedupKey(ut, context),
                Sequence = sequence
            };

            Ledger.AddAction(action);

            ParsekLog.Info(Tag,
                $"VesselRollout spending recorded: cost={cost:F0}, UT={ut:F1}, dedupKey={action.DedupKey}, " +
                $"context={FormatRolloutAdoptionContext(context)}");

            reconcileKscAction(action, ut);
            recalculateAndPatch();
        }

        internal static GameAction TryAdoptRolloutAction(
            string recordingId,
            double startUT,
            Recording rec,
            System.Collections.Generic.IReadOnlyList<GameAction> actions)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            if (rec == null)
            {
                ParsekLog.Verbose(Tag,
                    $"TryAdoptRolloutAction: recording '{recordingId}' not found, cannot match rollout context");
                return null;
            }

            RolloutAdoptionContext recordingContext = CreateRolloutAdoptionContext(rec);
            if (!CanMatchRolloutAdoptionContext(recordingContext))
            {
                ParsekLog.Verbose(Tag,
                    $"TryAdoptRolloutAction: recording '{recordingId}' missing rollout context " +
                    $"({FormatRolloutAdoptionContext(recordingContext)}), skipping adoption");
                return null;
            }

            if (actions == null)
                return null;

            for (int i = actions.Count - 1; i >= 0; i--)
            {
                var a = actions[i];
                if (a == null) continue;
                if (a.Type != GameActionType.FundsSpending) continue;
                if (a.FundsSpendingSource != FundsSpendingSource.VesselBuild) continue;
                if (!string.IsNullOrEmpty(a.RecordingId)) continue;
                if (string.IsNullOrEmpty(a.DedupKey)) continue;
                if (!a.DedupKey.StartsWith(LedgerOrchestrator.RolloutDedupPrefix, StringComparison.Ordinal)) continue;
                if (a.UT > startUT + 0.5) continue;
                if (startUT - a.UT > LedgerOrchestrator.RolloutAdoptionWindowSeconds) continue;
                if (!RolloutAdoptionContextsMatch(ParseRolloutAdoptionContext(a.DedupKey), recordingContext))
                    continue;

                string oldDedup = a.DedupKey;
                a.RecordingId = recordingId;
                a.DedupKey = null;
                ParsekLog.Info(Tag,
                    $"TryAdoptRolloutAction: recording '{recordingId}' adopted rollout action " +
                    $"(UT={a.UT:F1}, cost={a.FundsSpent:F0}, oldDedupKey={oldDedup}, " +
                    $"startUT={startUT:F1}, lag={startUT - a.UT:F1}s, " +
                    $"context={FormatRolloutAdoptionContext(recordingContext)})");
                return a;
            }

            ParsekLog.Verbose(Tag,
                $"TryAdoptRolloutAction: no unclaimed rollout action within {LedgerOrchestrator.RolloutAdoptionWindowSeconds:F0}s " +
                $"before startUT={startUT:F1} for recording '{recordingId}' " +
                $"with context {FormatRolloutAdoptionContext(recordingContext)}");
            return null;
        }

        internal static bool CanRecordingAdoptRolloutAction(Recording rec)
        {
            if (rec == null || string.IsNullOrEmpty(rec.StartSituation))
                return false;

            return rec.StartSituation.Equals("Prelaunch", StringComparison.OrdinalIgnoreCase)
                || rec.StartSituation.Equals("PRELAUNCH", StringComparison.OrdinalIgnoreCase);
        }

        internal static RolloutAdoptionContext ResolveCurrentRolloutAdoptionContext()
        {
            try
            {
                Vessel activeVessel = FlightGlobals.ActiveVessel;
                if (activeVessel != null)
                {
                    string launchSiteName = FlightRecorder.ResolveLaunchSiteName(activeVessel, false);
                    if (string.IsNullOrEmpty(launchSiteName))
                        launchSiteName = TryResolveLaunchSiteNameFromFlightDriver();

                    return CreateRolloutAdoptionContext(
                        activeVessel.persistentId,
                        Recording.ResolveLocalizedName(activeVessel.vesselName),
                        launchSiteName);
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"ResolveCurrentRolloutAdoptionContext: ActiveVessel lookup failed: {ex.Message}");
            }

            return CreateRolloutAdoptionContext(
                0u,
                null,
                TryResolveLaunchSiteNameFromFlightDriver());
        }

        private static string TryResolveLaunchSiteNameFromFlightDriver()
        {
            try
            {
                return FlightRecorder.HumanizeLaunchSiteName(FlightDriver.LaunchSiteName);
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag, $"TryResolveLaunchSiteNameFromFlightDriver failed: {ex.Message}");
                return null;
            }
        }

        internal static RolloutAdoptionContext CreateRolloutAdoptionContext(Recording rec)
        {
            if (rec == null)
                return default(RolloutAdoptionContext);

            return CreateRolloutAdoptionContext(
                rec.VesselPersistentId,
                rec.VesselName,
                rec.LaunchSiteName);
        }

        internal static RolloutAdoptionContext CreateRolloutAdoptionContext(
            uint vesselPersistentId,
            string vesselName,
            string launchSiteName)
        {
            return new RolloutAdoptionContext
            {
                VesselPersistentId = vesselPersistentId,
                VesselName = NormalizeRolloutContextText(vesselName),
                LaunchSiteName = NormalizeRolloutContextText(launchSiteName)
            };
        }

        private static string NormalizeRolloutContextText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return value.Trim();
        }

        private static bool CanMatchRolloutAdoptionContext(RolloutAdoptionContext context)
        {
            if (context.VesselPersistentId != 0)
                return true;

            return !string.IsNullOrEmpty(context.VesselName)
                && !string.IsNullOrEmpty(context.LaunchSiteName);
        }

        private static bool RolloutAdoptionContextsMatch(
            RolloutAdoptionContext actionContext,
            RolloutAdoptionContext recordingContext)
        {
            if (actionContext.IsLegacyBareKey)
                return true;

            if (actionContext.VesselPersistentId != 0 && recordingContext.VesselPersistentId != 0)
                return actionContext.VesselPersistentId == recordingContext.VesselPersistentId;

            return !string.IsNullOrEmpty(actionContext.VesselName)
                && !string.IsNullOrEmpty(recordingContext.VesselName)
                && !string.IsNullOrEmpty(actionContext.LaunchSiteName)
                && !string.IsNullOrEmpty(recordingContext.LaunchSiteName)
                && actionContext.VesselName.Equals(recordingContext.VesselName, StringComparison.OrdinalIgnoreCase)
                && actionContext.LaunchSiteName.Equals(recordingContext.LaunchSiteName, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildRolloutDedupKey(double ut, RolloutAdoptionContext context)
        {
            return LedgerOrchestrator.RolloutDedupPrefix
                + ut.ToString("R", CultureInfo.InvariantCulture)
                + "|pid=" + context.VesselPersistentId.ToString(CultureInfo.InvariantCulture)
                + "|site=" + Uri.EscapeDataString(context.LaunchSiteName ?? string.Empty)
                + "|vessel=" + Uri.EscapeDataString(context.VesselName ?? string.Empty);
        }

        private static RolloutAdoptionContext ParseRolloutAdoptionContext(string dedupKey)
        {
            if (string.IsNullOrEmpty(dedupKey)
                || !dedupKey.StartsWith(LedgerOrchestrator.RolloutDedupPrefix, StringComparison.Ordinal))
                return default(RolloutAdoptionContext);

            var context = default(RolloutAdoptionContext);
            string[] parts = dedupKey.Split('|');
            if (parts.Length == 1)
            {
                context.IsLegacyBareKey = true;
                return context;
            }

            for (int i = 1; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.StartsWith("pid=", StringComparison.Ordinal))
                {
                    uint.TryParse(
                        part.Substring(4),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out context.VesselPersistentId);
                }
                else if (part.StartsWith("site=", StringComparison.Ordinal))
                {
                    context.LaunchSiteName = NormalizeRolloutContextText(
                        Uri.UnescapeDataString(part.Substring(5)));
                }
                else if (part.StartsWith("vessel=", StringComparison.Ordinal))
                {
                    context.VesselName = NormalizeRolloutContextText(
                        Uri.UnescapeDataString(part.Substring(7)));
                }
            }

            return context;
        }

        private static string FormatRolloutAdoptionContext(RolloutAdoptionContext context)
        {
            return $"pid={context.VesselPersistentId}, vessel='{context.VesselName ?? "(null)"}', " +
                $"site='{context.LaunchSiteName ?? "(null)"}'";
        }
    }
}
