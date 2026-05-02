using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony prefix on KerbalRoster.HireApplicant to block hiring kerbals
    /// that are already committed in unreplayed milestones.
    /// </summary>
    [HarmonyPatch]
    internal static class KerbalHirePatch
    {
        static MethodBase TargetMethod()
        {
            var method = AccessTools.Method(
                typeof(KerbalRoster),
                nameof(KerbalRoster.HireApplicant),
                new[] { typeof(ProtoCrewMember) });

            if (method == null)
                ParsekLog.Warn("KerbalHirePatch",
                    "KerbalRoster.HireApplicant(ProtoCrewMember) not found — kerbal hire click-block will not apply. " +
                    "Harmony will skip this patch (caught by ParsekHarmony try/catch).");

            return method;
        }

        static bool Prefix(ProtoCrewMember ap)
        {
            if (ap == null) return true;
            return ShouldAllowHire(ap.name);
        }

        internal static bool ShouldAllowHire(string kerbalName)
        {
            if (string.IsNullOrEmpty(kerbalName)) return true;

            if (!(ParsekSettings.Current?.blockCommittedActions ?? true))
            {
                ParsekLog.Verbose("KerbalHirePatch",
                    "feature disabled by ParsekSettings");
                return true;
            }

            if (GameStateRecorder.IsReplayingActions)
            {
                ParsekLog.Verbose("KerbalHirePatch",
                    "bypass — replay in progress");
                return true;
            }

            var committedHires = MilestoneStore.GetCommittedKerbalHireNames();
            if (!committedHires.Contains(kerbalName))
                return true;

            var ev = MilestoneStore.FindCommittedEvent(
                GameStateEventType.CrewHired, kerbalName);

            string utValue = ev.HasValue
                ? ev.Value.ut.ToString("F0", CultureInfo.InvariantCulture)
                : "unknown";
            string utStr = ev.HasValue ? " at UT " + utValue : "";

            ParsekLog.Info("KerbalHirePatch",
                $"blocking hire for name={kerbalName} — committed at UT {utValue}");

            CommittedActionDialog.ShowBlocked(
                "Cannot hire \"" + kerbalName + "\"",
                "This kerbal is already committed on your timeline" + utStr + ".",
                "");

            return false;
        }
    }
}
