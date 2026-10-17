using System;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony prefix / postfix pairs on the two stock paths that pay out the Mobile
    /// Processing Lab's stored science (LAB-SCIENCE-NEVER-LEDGERED). Decompiled KSP 1.12.5:
    /// <c>ModuleScienceLab.OnTransmissionComplete</c> (the lab's own
    /// <c>OnTriggeredDataTransmission</c> handler, for its <c>sciencelab@</c> data) calls
    /// <c>ResearchAndDevelopment.AddScience(storedScience, ScienceTransmission)</c>, and
    /// <c>ModuleScienceLab.RecoverScienceLabs</c> calls <c>AddScience(sum, VesselRecovery)</c>.
    /// Neither has a science subject: triggered data never opens an <c>RnDCommsStream</c>, so
    /// no <c>SubmitScienceData</c> and no <c>OnScienceRecieved</c> fire, and the ledger's
    /// subject channel never saw lab science.
    ///
    /// The prefix reads the R&amp;D pool; the postfix hands the pool before and after to
    /// <see cref="GameStateRecorder.OnScienceLabCredited"/> when the call credited science.
    /// A lab handler that returned early (another part's data, an aborted transmission)
    /// moves no science and is ignored silently, since every lab sees every triggered
    /// transmission. Never rethrows into the stock call.
    /// </summary>
    [HarmonyPatch(typeof(ModuleScienceLab), "OnTransmissionComplete")]
    internal static class ScienceLabTransmissionCreditPatch
    {
        private const string Tag = "ScienceLabCapture";

        internal static void Prefix(out float __state)
        {
            __state = ScienceLabCreditCapture.ReadPool();
        }

        internal static void Postfix(ModuleScienceLab __instance, float __state)
        {
            if (ParsekGameModeGate.CheckInert("ScienceLabTransmissionCreditPatch.Postfix")) return; // S9 game-mode gate
            try
            {
                string vesselName = __instance != null && __instance.vessel != null
                    ? Recording.ResolveLocalizedName(__instance.vessel.vesselName)
                    : null;
                ScienceLabCreditCapture.Report(__state, "ScienceTransmission", vesselName);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"OnTransmissionComplete postfix threw while capturing the lab credit: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(ModuleScienceLab), nameof(ModuleScienceLab.RecoverScienceLabs))]
    internal static class ScienceLabRecoveryCreditPatch
    {
        private const string Tag = "ScienceLabCapture";

        internal static void Prefix(out float __state)
        {
            __state = ScienceLabCreditCapture.ReadPool();
        }

        internal static void Postfix(ProtoVessel protoVessel, float __state)
        {
            if (ParsekGameModeGate.CheckInert("ScienceLabRecoveryCreditPatch.Postfix")) return; // S9 game-mode gate
            try
            {
                string vesselName = protoVessel != null
                    ? Recording.ResolveLocalizedName(protoVessel.vesselName)
                    : null;
                ScienceLabCreditCapture.Report(__state, "VesselRecovery", vesselName);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"RecoverScienceLabs postfix threw while capturing the lab credit: {ex}");
            }
        }
    }

    /// <summary>Shared pool read and report for the two lab credit patches.</summary>
    internal static class ScienceLabCreditCapture
    {
        internal static float ReadPool()
        {
            try
            {
                return ResearchAndDevelopment.Instance != null
                    ? ResearchAndDevelopment.Instance.Science
                    : float.NaN;
            }
            catch (Exception)
            {
                return float.NaN;
            }
        }

        /// <summary>
        /// Pure: true when the pool read around a lab call shows a credit worth recording.
        /// A missing read (no R&amp;D: sandbox) or no positive move is not one.
        /// </summary>
        internal static bool IsCredit(float before, float after)
        {
            if (float.IsNaN(before) || float.IsNaN(after)) return false;
            if (float.IsInfinity(before) || float.IsInfinity(after)) return false;
            return !GameStateRecorder.IsScienceDeltaBelowThreshold(after - before) && after > before;
        }

        internal static void Report(float before, string reasonKey, string vesselName)
        {
            float after = ReadPool();
            if (!IsCredit(before, after))
                return;
            GameStateRecorder.OnScienceLabCredited(
                Planetarium.GetUniversalTime(), before, after, reasonKey, vesselName);
        }
    }
}
