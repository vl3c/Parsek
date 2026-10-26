using System;
using System.Reflection;
using Contracts;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// The conversion-cause scopes (design 3.15 step 7): a prefix opens a
    /// <see cref="ConversionCauseScope"/> naming the award stock is about to pay, and a
    /// finalizer closes it even when stock throws. A strategy conversion leg captured inside
    /// stock's currency-modifier query while one is open (and for a reason that award pays
    /// with) names that award as its cause, so the replay scales the leg by the award's
    /// counted share. KSP 1.12.5, decompiled: each award method below calls
    /// <c>Funding.AddFunds</c> / <c>ResearchAndDevelopment.AddScience</c> /
    /// <c>Reputation.AddReputation</c> itself with the reason listed, and each of those runs
    /// one <c>CurrencyModifierQuery</c> that fires <c>OnCurrencyModified</c> inside the call.
    /// A contract's advance (<c>AwardAdvance</c>) has no scope: an accept row is always
    /// counted, so its legs have nothing to follow.
    /// </summary>
    internal static class ConversionCauseScopePatchSupport
    {
        internal const string Tag = "ConversionCause";

        /// <summary>Opens a scope unless Parsek is inert in this game mode; returns its token (0: none).</summary>
        internal static int Open(string gateName, GameActionType type, string key, params string[] reasons)
        {
            if (ParsekGameModeGate.CheckInert(gateName))
                return 0;
            try
            {
                return ConversionCauseScope.Open(type, key, reasons);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"{gateName}: opening the conversion cause scope threw: {ex.Message}");
                return 0;
            }
        }

        internal static Exception Close(Exception exception, int token)
        {
            if (token > 0)
                ConversionCauseScope.Close(token);
            return exception;
        }
    }

    /// <summary>A milestone's reward: <c>ProgressNode.AwardProgress</c>, reason <c>Progression</c>.</summary>
    [HarmonyPatch(typeof(ProgressNode), "AwardProgress",
        typeof(string), typeof(float), typeof(float), typeof(float), typeof(CelestialBody))]
    internal static class MilestoneConversionCauseScopePatch
    {
        static void Prefix(ProgressNode __instance, out int __state)
        {
            string key = null;
            try { key = __instance != null ? GameStateRecorder.QualifyMilestoneId(__instance) : null; }
            catch (Exception) { key = null; }
            __state = ConversionCauseScopePatchSupport.Open("MilestoneConversionCauseScopePatch.Prefix",
                GameActionType.MilestoneAchievement, key, TransactionReasons.Progression.ToString());
        }

        static Exception Finalizer(Exception __exception, int __state)
        {
            return ConversionCauseScopePatchSupport.Close(__exception, __state);
        }
    }

    /// <summary>A contract's completion reward: <c>Contract.AwardCompletion</c>, reason <c>ContractReward</c>.</summary>
    [HarmonyPatch]
    internal static class ContractCompleteConversionCauseScopePatch
    {
        static MethodBase TargetMethod() => ConversionCauseScopeTargets.Contract("AwardCompletion");

        static void Prefix(Contract __instance, out int __state)
        {
            __state = ConversionCauseScopePatchSupport.Open("ContractCompleteConversionCauseScopePatch.Prefix",
                GameActionType.ContractComplete, ConversionCauseScopeTargets.GuidOf(__instance),
                TransactionReasons.ContractReward.ToString());
        }

        static Exception Finalizer(Exception __exception, int __state)
        {
            return ConversionCauseScopePatchSupport.Close(__exception, __state);
        }
    }

    /// <summary>A contract's failure penalty: <c>Contract.PenalizeFailure</c>, reason <c>ContractPenalty</c>.</summary>
    [HarmonyPatch]
    internal static class ContractFailConversionCauseScopePatch
    {
        static MethodBase TargetMethod() => ConversionCauseScopeTargets.Contract("PenalizeFailure");

        static void Prefix(Contract __instance, out int __state)
        {
            __state = ConversionCauseScopePatchSupport.Open("ContractFailConversionCauseScopePatch.Prefix",
                GameActionType.ContractFail, ConversionCauseScopeTargets.GuidOf(__instance),
                TransactionReasons.ContractPenalty.ToString());
        }

        static Exception Finalizer(Exception __exception, int __state)
        {
            return ConversionCauseScopePatchSupport.Close(__exception, __state);
        }
    }

    /// <summary>A contract's cancellation penalty: <c>Contract.PenalizeCancellation</c>, reason <c>ContractPenalty</c>.</summary>
    [HarmonyPatch]
    internal static class ContractCancelConversionCauseScopePatch
    {
        static MethodBase TargetMethod() => ConversionCauseScopeTargets.Contract("PenalizeCancellation");

        static void Prefix(Contract __instance, out int __state)
        {
            __state = ConversionCauseScopePatchSupport.Open("ContractCancelConversionCauseScopePatch.Prefix",
                GameActionType.ContractCancel, ConversionCauseScopeTargets.GuidOf(__instance),
                TransactionReasons.ContractPenalty.ToString());
        }

        static Exception Finalizer(Exception __exception, int __state)
        {
            return ConversionCauseScopePatchSupport.Close(__exception, __state);
        }
    }

    /// <summary>A contract parameter's own reward: <c>ContractParameter.AwardCompletion</c>, reason <c>ContractReward</c>.</summary>
    [HarmonyPatch]
    internal static class ContractParameterConversionCauseScopePatch
    {
        static MethodBase TargetMethod() => ConversionCauseScopeTargets.ParameterAwardCompletion();

        static void Prefix(ContractParameter __instance, out int __state)
        {
            string key = null;
            try
            {
                Contract root = __instance != null ? __instance.Root : null;
                if (root != null)
                    key = ConversionCause.ParameterKey(root.ContractGuid.ToString(),
                        ContractParameterRewardCapturePatch.ResolveParameterId(__instance));
            }
            catch (Exception) { key = null; }
            __state = ConversionCauseScopePatchSupport.Open("ContractParameterConversionCauseScopePatch.Prefix",
                GameActionType.ContractParameterComplete, key, TransactionReasons.ContractReward.ToString());
        }

        static Exception Finalizer(Exception __exception, int __state)
        {
            return ConversionCauseScopePatchSupport.Close(__exception, __state);
        }
    }

    /// <summary>A science subject's payout: <c>ResearchAndDevelopment.SubmitScienceData</c>,
    /// reason <c>ScienceTransmission</c> or <c>VesselRecovery</c>.</summary>
    [HarmonyPatch]
    internal static class ScienceSubjectConversionCauseScopePatch
    {
        static MethodBase TargetMethod() => DeployedScienceSubmitScopePatch.ResolveTargetMethodForTesting();

        static void Prefix(ScienceSubject subject, out int __state)
        {
            __state = ConversionCauseScopePatchSupport.Open("ScienceSubjectConversionCauseScopePatch.Prefix",
                GameActionType.ScienceEarning, subject != null ? subject.id : null,
                TransactionReasons.ScienceTransmission.ToString(), TransactionReasons.VesselRecovery.ToString());
        }

        static Exception Finalizer(Exception __exception, int __state)
        {
            return ConversionCauseScopePatchSupport.Close(__exception, __state);
        }
    }

    /// <summary>An orbital survey's bonus: <c>ModuleOrbitalSurveyor.CompleteSurvey</c>, reason <c>ScienceTransmission</c>.</summary>
    [HarmonyPatch(typeof(ModuleOrbitalSurveyor), "CompleteSurvey")]
    internal static class SurveyConversionCauseScopePatch
    {
        static void Prefix(ModuleOrbitalSurveyor __instance, out int __state)
        {
            string body = null;
            try
            {
                if (__instance != null && __instance.vessel != null && __instance.vessel.mainBody != null)
                    body = __instance.vessel.mainBody.bodyName;
            }
            catch (Exception) { body = null; }
            __state = ConversionCauseScopePatchSupport.Open("SurveyConversionCauseScopePatch.Prefix",
                GameActionType.ScienceLabEarning, body, TransactionReasons.ScienceTransmission.ToString());
        }

        static Exception Finalizer(Exception __exception, int __state)
        {
            return ConversionCauseScopePatchSupport.Close(__exception, __state);
        }
    }

    /// <summary>Target resolution shared by the contract scopes (and the tests).</summary>
    internal static class ConversionCauseScopeTargets
    {
        internal static MethodBase Contract(string name)
        {
            var method = AccessTools.Method(typeof(Contract), name, Type.EmptyTypes);
            if (method == null)
                ParsekLog.Warn(ConversionCauseScopePatchSupport.Tag,
                    $"Contracts.Contract.{name}() not found - a strategy leg of this award stays as captured. " +
                    "Harmony will skip this patch (caught by ParsekHarmony try/catch).");
            return method;
        }

        internal static MethodBase ParameterAwardCompletion()
        {
            var method = AccessTools.Method(typeof(ContractParameter), "AwardCompletion", Type.EmptyTypes);
            if (method == null)
                ParsekLog.Warn(ConversionCauseScopePatchSupport.Tag,
                    "Contracts.ContractParameter.AwardCompletion() not found - a strategy leg of a parameter reward " +
                    "stays as captured. Harmony will skip this patch (caught by ParsekHarmony try/catch).");
            return method;
        }

        internal static string GuidOf(Contract contract)
        {
            try { return contract != null ? contract.ContractGuid.ToString() : null; }
            catch (Exception) { return null; }
        }
    }
}
