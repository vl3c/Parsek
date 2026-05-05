using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using KSP.UI;
using KSP.UI.Screens;

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

    /// <summary>
    /// Stock Astronaut Complex moves list rows before calling KerbalRoster.HireApplicant,
    /// so block at the button handler before stock mutates the open list UI.
    /// </summary>
    [HarmonyPatch]
    internal static class AstronautComplexHireRecruitPatch
    {
        private static bool applicantLookupWarned;

        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("KerbalHirePatch",
                    "AstronautComplex.HireRecruit(UIList, UIList, UIListItem) not found - stock Astronaut Complex hire pre-block will not apply. " +
                    "KerbalRoster.HireApplicant backup patch remains active.");

            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return typeof(AstronautComplex).GetMethod(
                "HireRecruit",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(UIList), typeof(UIList), typeof(UIListItem) },
                null);
        }

        static bool Prefix(UIListItem listItem)
        {
            ProtoCrewMember applicant;
            if (!TryGetApplicant(listItem, out applicant))
                return true;

            return KerbalHirePatch.ShouldAllowHire(applicant.name);
        }

        private static bool TryGetApplicant(UIListItem listItem, out ProtoCrewMember applicant)
        {
            applicant = null;
            if (listItem == null)
            {
                ParsekLog.Verbose("KerbalHirePatch",
                    "AstronautComplex.HireRecruit pre-block bypass - listItem was null");
                return false;
            }

            try
            {
                applicant = listItem.Data as ProtoCrewMember;
                if (applicant != null)
                    return true;

                var crewListItem = listItem.GetComponentInChildren<CrewListItem>(true);
                applicant = crewListItem != null ? crewListItem.GetCrewRef() : null;
                if (applicant != null)
                    return true;
            }
            catch (Exception ex)
            {
                LogApplicantLookupWarning(
                    "AstronautComplex.HireRecruit pre-block applicant lookup failed; stock UI pre-block skipped (" +
                    ex.Message + ")");
                return false;
            }

            LogApplicantLookupWarning(
                "AstronautComplex.HireRecruit pre-block could not resolve a ProtoCrewMember from the selected row; " +
                "stock UI pre-block skipped");
            return false;
        }

        private static void LogApplicantLookupWarning(string message)
        {
            if (applicantLookupWarned)
                return;

            applicantLookupWarned = true;
            ParsekLog.Warn("KerbalHirePatch", message);
        }
    }
}
