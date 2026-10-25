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
    /// The prefix reads the R&amp;D pool and the gross payout stock is about to pass to
    /// <c>AddScience</c> (the lab's <c>storedScience</c>, or the recovered labs' summed
    /// <c>storedScience</c> the way stock sums it). The postfix reports that gross to
    /// <see cref="GameStateRecorder.OnScienceLabCredited"/> when the call moved the pool.
    /// The gross, not the pool delta, is the credit: a converter strategy (Patents
    /// Licensing) takes its share inside <c>AddScience</c> through
    /// <c>OnCurrencyModified</c>, which the ledger already records as its own
    /// <c>StrategyScienceDebit</c>, so a net credit would charge the take twice, the same
    /// way a <c>ScienceEarning</c> stores the gross subject value. A lab handler that
    /// returned early (another part's data, an aborted transmission) moves no science and
    /// is ignored silently, since every lab sees every triggered transmission. Never
    /// rethrows into the stock call.
    /// </summary>
    [HarmonyPatch(typeof(ModuleScienceLab), "OnTransmissionComplete")]
    internal static class ScienceLabTransmissionCreditPatch
    {
        private const string Tag = "ScienceLabCapture";

        internal static void Prefix(ModuleScienceLab __instance, out ScienceLabCreditCapture.CallState __state)
        {
            __state = new ScienceLabCreditCapture.CallState
            {
                PoolBefore = ScienceLabCreditCapture.ReadPool(),
                Gross = __instance != null ? __instance.storedScience : 0f,
            };
        }

        internal static void Postfix(ModuleScienceLab __instance, ScienceLabCreditCapture.CallState __state)
        {
            if (ParsekGameModeGate.CheckInert("ScienceLabTransmissionCreditPatch.Postfix")) return; // S9 game-mode gate
            try
            {
                string vesselName = __instance != null && __instance.vessel != null
                    ? Recording.ResolveLocalizedName(__instance.vessel.vesselName)
                    : null;
                ScienceLabCreditCapture.Report(__state, "ScienceTransmission", vesselName,
                    __instance != null && __instance.vessel != null ? __instance.vessel.persistentId : 0u);
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

        internal static void Prefix(ProtoVessel protoVessel, out ScienceLabCreditCapture.CallState __state)
        {
            __state = new ScienceLabCreditCapture.CallState
            {
                PoolBefore = ScienceLabCreditCapture.ReadPool(),
                Gross = ScienceLabCreditCapture.SumStoredScience(protoVessel),
            };
        }

        internal static void Postfix(ProtoVessel protoVessel, ScienceLabCreditCapture.CallState __state)
        {
            if (ParsekGameModeGate.CheckInert("ScienceLabRecoveryCreditPatch.Postfix")) return; // S9 game-mode gate
            try
            {
                string vesselName = protoVessel != null
                    ? Recording.ResolveLocalizedName(protoVessel.vesselName)
                    : null;
                ScienceLabCreditCapture.Report(__state, "VesselRecovery", vesselName,
                    protoVessel != null ? protoVessel.persistentId : 0u);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"RecoverScienceLabs postfix threw while capturing the lab credit: {ex}");
            }
        }
    }

    /// <summary>
    /// Harmony prefix / postfix on <c>ModuleOrbitalSurveyor.CompleteSurvey</c>, the M700's
    /// survey completion. Decompiled KSP 1.12.5: once the survey's triggered transmission
    /// lands, stock calls <c>AddScience(SciBonus * mainBody.scienceValues.RecoveryValue,
    /// ScienceTransmission)</c> with no science subject, the same bare credit the lab pays,
    /// and then unlocks the body's resource map. Without this capture the bonus had no
    /// ledger row: the pool check kept it later as an untagged adjustment ("Kept your
    /// earned science"), dated to that check and tied to no flight. The bonus is recorded
    /// through the lab channel, as a <see cref="GameStateEventType.ScienceLabCredited"/>
    /// event that names the surveyed body, so it is tagged to the flight that ran the
    /// survey. A call that returned early (another part's data, an aborted transmission,
    /// no R&amp;D) moves no science and is ignored. Never rethrows into the stock call.
    /// </summary>
    [HarmonyPatch(typeof(ModuleOrbitalSurveyor), "CompleteSurvey")]
    internal static class OrbitalSurveyCreditPatch
    {
        private const string Tag = "ScienceLabCapture";

        internal static void Prefix(ModuleOrbitalSurveyor __instance, out ScienceLabCreditCapture.CallState __state)
        {
            float gross = 0f;
            try
            {
                if (__instance != null && __instance.vessel != null && __instance.vessel.mainBody != null
                    && __instance.vessel.mainBody.scienceValues != null)
                    gross = SurveyBonus(__instance.SciBonus, __instance.vessel.mainBody.scienceValues.RecoveryValue);
            }
            catch (Exception)
            {
                gross = 0f;
            }
            __state = new ScienceLabCreditCapture.CallState
            {
                PoolBefore = ScienceLabCreditCapture.ReadPool(),
                Gross = gross,
            };
        }

        internal static void Postfix(ModuleOrbitalSurveyor __instance, ScienceLabCreditCapture.CallState __state)
        {
            if (ParsekGameModeGate.CheckInert("OrbitalSurveyCreditPatch.Postfix")) return; // S9 game-mode gate
            try
            {
                Vessel v = __instance != null ? __instance.vessel : null;
                string vesselName = v != null ? Recording.ResolveLocalizedName(v.vesselName) : null;
                string bodyName = v != null && v.mainBody != null ? v.mainBody.bodyName : null;
                ScienceLabCreditCapture.Report(__state, "ScienceTransmission", vesselName,
                    v != null ? v.persistentId : 0u, string.IsNullOrEmpty(bodyName) ? "?" : bodyName);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"CompleteSurvey postfix threw while capturing the survey bonus: {ex}");
            }
        }

        /// <summary>Pure: the bonus stock pays for a survey, computed the way stock computes it.</summary>
        internal static float SurveyBonus(int sciBonus, float recoveryValue)
        {
            return (float)sciBonus * recoveryValue;
        }
    }

    /// <summary>Shared pool read and report for the lab and orbital survey credit patches.</summary>
    internal static class ScienceLabCreditCapture
    {
        /// <summary>What a prefix reads before the stock lab call.</summary>
        internal struct CallState
        {
            internal float PoolBefore;
            internal float Gross;
        }

        /// <summary>
        /// Runtime: the science stock's <c>RecoverScienceLabs</c> pays for this vessel - every
        /// <c>ModuleScienceLab</c> snapshot's positive <c>storedScience</c> values, summed.
        /// </summary>
        internal static float SumStoredScience(ProtoVessel protoVessel)
        {
            if (protoVessel == null || protoVessel.protoPartSnapshots == null)
                return 0f;
            float sum = 0f;
            foreach (ProtoPartSnapshot pps in protoVessel.protoPartSnapshots)
            {
                if (pps == null || pps.modules == null) continue;
                foreach (ProtoPartModuleSnapshot m in pps.modules)
                {
                    if (m == null || m.moduleName != "ModuleScienceLab" || m.moduleValues == null)
                        continue;
                    sum += SumPositiveValues(m.moduleValues.GetValues("storedScience"));
                }
            }
            return sum;
        }

        /// <summary>
        /// Runtime: the same sum read from a LIVE vessel, before stock turns it into the
        /// <c>ProtoVessel</c> it recovers: every <c>ModuleScienceLab</c>'s
        /// <c>storedScience</c> above float epsilon. Used when the flight Recover request arms.
        /// </summary>
        internal static float SumLiveStoredScience(Vessel vessel)
        {
            if (vessel == null || vessel.parts == null) return 0f;
            float sum = 0f;
            foreach (Part part in vessel.parts)
            {
                if (part == null || part.Modules == null) continue;
                foreach (PartModule pm in part.Modules)
                {
                    var lab = pm as ModuleScienceLab;
                    if (lab != null && lab.storedScience > float.Epsilon)
                        sum += lab.storedScience;
                }
            }
            return sum;
        }

        /// <summary>Pure: the sum of the parseable values above float epsilon, as stock sums them.</summary>
        internal static float SumPositiveValues(string[] values)
        {
            if (values == null) return 0f;
            float sum = 0f;
            foreach (string raw in values)
            {
                float v;
                if (float.TryParse(raw, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out v)
                    && v > float.Epsilon)
                    sum += v;
            }
            return sum;
        }

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
        /// Pure: true when the pool read around a lab call shows the lab paid out, so the
        /// gross is a credit worth recording. A missing read (no R&amp;D: sandbox), no
        /// positive move, or no gross is not one.
        /// </summary>
        internal static bool IsCredit(float before, float after, float gross)
        {
            if (float.IsNaN(before) || float.IsNaN(after) || float.IsNaN(gross)) return false;
            if (float.IsInfinity(before) || float.IsInfinity(after) || float.IsInfinity(gross)) return false;
            if (GameStateRecorder.IsScienceDeltaBelowThreshold(after - before) || after <= before)
                return false;
            return !GameStateRecorder.IsScienceDeltaBelowThreshold(gross) && gross > 0f;
        }

        /// <summary>
        /// Reports the lab's gross payout. The event carries the pool before the call and
        /// that pool plus the gross, so its delta is the credit before any strategy take.
        /// </summary>
        internal static void Report(CallState state, string reasonKey, string vesselName, uint vesselPersistentId = 0u,
            string surveyBodyName = null)
        {
            float after = ReadPool();
            if (!IsCredit(state.PoolBefore, after, state.Gross))
                return;
            if (Math.Abs((after - state.PoolBefore) - state.Gross) > 0.01f)
                ParsekLog.Verbose("ScienceLabCapture",
                    $"{(surveyBodyName != null ? "Survey bonus" : "Lab payout")} net of a strategy take: gross={state.Gross.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"poolDelta={(after - state.PoolBefore).ToString("R", System.Globalization.CultureInfo.InvariantCulture)} reason='{reasonKey}'");
            GameStateRecorder.OnScienceLabCredited(
                Planetarium.GetUniversalTime(), state.PoolBefore, state.PoolBefore + state.Gross,
                reasonKey, vesselName, vesselPersistentId, surveyBodyName);
        }
    }
}
