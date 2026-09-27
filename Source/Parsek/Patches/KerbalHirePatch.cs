using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using KSP.UI;
using KSP.UI.Screens;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony prefix on KerbalRoster.HireApplicant to block hiring a kerbal a
    /// committed future row hires (<see cref="CommittedFutureIndex"/>).
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
                    "KerbalRoster.HireApplicant(ProtoCrewMember) not found - kerbal hire click-block will not apply. " +
                    "Harmony will skip this patch (caught by ParsekHarmony try/catch).");

            return method;
        }

        static bool Prefix(ProtoCrewMember ap)
        {
            if (ParsekGameModeGate.CheckInert("KerbalHirePatch.Prefix")) return true; // S9 game-mode gate
            if (ap == null) return true;
            return ShouldAllowHire(ap.name);
        }

        internal static bool ShouldAllowHire(string kerbalName)
        {
            if (string.IsNullOrEmpty(kerbalName)) return true;

            if (GameStateRecorder.IsReplayingActions)
            {
                ParsekLog.Verbose("KerbalHirePatch",
                    "bypass - replay in progress");
                return true;
            }

            var index = CommittedFutureIndexCache.Current;
            double nowUT = CommittedFutureIndexCache.CurrentUT();
            if (!StockUiReservationPredicates.IsKerbalHireBlocked(index, kerbalName, nowUT))
                return true;

            var entry = index.FirstFuture(CommittedFutureKind.KerbalHire, kerbalName, nowUT);
            ParsekLog.Info("KerbalHirePatch",
                $"blocking hire for name={kerbalName} - committed future hire " +
                $"ut={entry.UT.ToString("F0", CultureInfo.InvariantCulture)} " +
                $"nowUT={nowUT.ToString("F0", CultureInfo.InvariantCulture)} " +
                $"recording={entry.RecordingId ?? "(ksc)"}");

            var text = StockUiReservationPredicates.ExplainKerbalHire(
                index, kerbalName, nowUT, ReservationExplanation.DefaultDateFormatter);
            CommittedActionDialog.ShowBlocked(
                "Cannot hire \"" + kerbalName + "\"",
                text.Body,
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
            if (ParsekGameModeGate.CheckInert("AstronautComplexHireRecruitPatch.Prefix")) return true; // S9 game-mode gate
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
