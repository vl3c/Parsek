using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony prefix on <c>KerbalRoster.GetNextApplicant()</c>, the applicant pick of stock
    /// auto-hire (<c>Difficulty.AutoHireCrews</c>). Stock's only caller is the shortfall loop
    /// in <c>KerbalRoster.DefaultCrewForVessel</c> (decompiled, KSP 1.12.5): for each seat
    /// the available crew cannot fill it takes the first Applicant in roster order (or, when
    /// this returns null, a fresh <c>GetNewKerbal(Applicant)</c>), calls
    /// <c>HireApplicant</c> and seats him without checking the outcome.
    ///
    /// <para>After a rewind to before a committed hire the applicant that hire names is back
    /// in the roster; <see cref="KerbalHirePatch"/> refuses his hire (a committed future
    /// hires him), yet stock still seated him unhired, claimed he was hired, and with two
    /// seats short picked him twice. This prefix skips applicants a committed future hires
    /// (the same predicate as <see cref="KerbalHirePatch"/>), returning the first free
    /// applicant, or null when none is left so stock takes its own "no applicant" path and
    /// generates a fresh one. <see cref="KerbalHirePatch"/> stays the backstop.</para>
    /// </summary>
    [HarmonyPatch]
    internal static class KerbalAutoHireApplicantPatch
    {
        private const string Tag = "KerbalHirePatch";

        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn(Tag,
                    "KerbalRoster.GetNextApplicant() not found - auto-hire will not skip committed-future-hire applicants. " +
                    "KerbalRoster.HireApplicant backup patch remains active.");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(KerbalRoster), nameof(KerbalRoster.GetNextApplicant), Type.EmptyTypes);
        }

        static bool Prefix(KerbalRoster __instance, ref ProtoCrewMember __result)
        {
            if (ParsekGameModeGate.CheckInert("KerbalAutoHireApplicantPatch.Prefix")) return true; // S9 game-mode gate
            if (__instance == null) return true;

            var applicants = new List<ProtoCrewMember>();
            for (int i = 0; i < __instance.Count; i++)
            {
                ProtoCrewMember pcm = __instance[i];
                if (pcm != null && pcm.type == ProtoCrewMember.KerbalType.Applicant)
                    applicants.Add(pcm);
            }

            var names = new List<string>(applicants.Count);
            for (int i = 0; i < applicants.Count; i++)
                names.Add(applicants[i].name);

            int skipped;
            int pick = SelectApplicantIndex(names, IsAutoHireApplicantBlocked, out skipped);
            if (skipped == 0)
                return true; // nothing reserved: stock's own pick is the same one

            __result = pick >= 0 ? applicants[pick] : null;
            ParsekLog.Info(Tag,
                string.Format(CultureInfo.InvariantCulture,
                    "auto-hire applicant pick: skipped={0} applicant(s) a committed future hires; picked={1}",
                    skipped,
                    pick >= 0 ? names[pick] : "(none - stock generates a new applicant)"));
            return false;
        }

        /// <summary>
        /// The first applicant (in roster order) the predicate does not block, or -1 when
        /// every applicant is blocked or there is none. <paramref name="skipped"/> counts the
        /// blocked applicants ahead of the pick (all of them when -1). Pure.
        /// </summary>
        internal static int SelectApplicantIndex(
            IList<string> applicantNamesInRosterOrder,
            Func<string, bool> isBlocked,
            out int skipped)
        {
            skipped = 0;
            if (applicantNamesInRosterOrder == null) return -1;
            for (int i = 0; i < applicantNamesInRosterOrder.Count; i++)
            {
                string name = applicantNamesInRosterOrder[i];
                if (isBlocked != null && isBlocked(name))
                {
                    skipped++;
                    ParsekLog.Verbose(Tag,
                        "auto-hire skips applicant name=" + (name ?? "(null)") + " - a committed future hires him");
                    continue;
                }
                return i;
            }
            return -1;
        }

        /// <summary>
        /// True when a committed future row hires this applicant, so auto-hire must not take
        /// him now (the predicate <see cref="KerbalHirePatch.ShouldAllowHire"/> refuses on,
        /// without its dialog). Parsek's own replay is never blocked.
        /// </summary>
        internal static bool IsAutoHireApplicantBlocked(string kerbalName)
        {
            if (string.IsNullOrEmpty(kerbalName)) return false;
            if (GameStateRecorder.IsReplayingActions) return false;
            var index = CommittedFutureIndexCache.Current;
            double nowUT = CommittedFutureIndexCache.CurrentUT();
            return StockUiReservationPredicates.IsKerbalHireBlocked(index, kerbalName, nowUT);
        }
    }
}
