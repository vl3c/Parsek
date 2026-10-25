using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Opens the recorder's clock-cause scope (<see cref="GameStateRecorder.InClockCauseScope"/>)
    /// around a Breaking Ground ground station's science submission (owner ruling 2026-10-24:
    /// an event no flight causes is filed under no flight; todos
    /// CONTRACT-COMPLETED-BY-DEPLOYED-SCIENCE-TAGGED-TO-THE-FLIGHT and
    /// STRATEGY-CONVERSION-OF-DEPLOYED-SCIENCE-TAGGED-TO-THE-FLIGHT).
    /// <para>Stock funnel (decompiled, KSP 1.12.5): <c>DeployedScienceExperiment</c> calls
    /// <c>ResearchAndDevelopment.SubmitScienceData(amount, subject)</c>, which delegates to the
    /// six-argument overload patched here. Inside that one call stock runs
    /// <c>AddScience</c> (a strategy's conversion of the award fires there), then
    /// <c>OnScienceRecieved</c>, whose <c>CollectDeployedScience</c> parameter completes its
    /// contract synchronously (<c>SetComplete</c> -> <c>Contract.OnParameterStateChange</c>
    /// -> <c>SetState(Completed)</c> -> <c>onCompleted</c>). The award itself is untagged by
    /// the 2026-09-26 deployed-science ruling; the scope files the conversion, the contract
    /// completion and its reward the same way, whatever the order stock's subscribers run in.</para>
    /// <para>A deployed subject id starts with <c>deployed</c>
    /// (<see cref="GameStateRecorder.IsDeployedScienceSubjectId"/>), and the ground station
    /// submits with no source vessel; every submission a vessel makes (its own science, or a
    /// ground station's data a kerbal carried aboard) is left alone.</para>
    /// </summary>
    [HarmonyPatch]
    internal static class DeployedScienceSubmitScopePatch
    {
        private const string Tag = "ClockCauseScope";

        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn(Tag,
                    "ResearchAndDevelopment.SubmitScienceData(float, float, ScienceSubject, float, ProtoVessel, bool) not found - " +
                    "a contract a ground station's science completes will be filed under the live flight. " +
                    "Harmony will skip this patch (caught by ParsekHarmony try/catch).");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(ResearchAndDevelopment), "SubmitScienceData",
                new[] { typeof(float), typeof(float), typeof(ScienceSubject), typeof(float), typeof(ProtoVessel), typeof(bool) });
        }

        /// <summary>True when a submission is a ground station's own: the subject is a
        /// deployed experiment's and no vessel submits it. A kerbal can also gather a ground
        /// station's data into a vessel's container (<c>ModuleGroundPart</c>); that vessel's
        /// transmission or recovery passes its <c>ProtoVessel</c> as the source, and whatever
        /// it completes (a body science milestone) stays the flight's. Pure.</summary>
        internal static bool IsGroundStationSubmission(string subjectId, bool hasSourceVessel)
        {
            return !hasSourceVessel && GameStateRecorder.IsDeployedScienceSubjectId(subjectId);
        }

        /// <summary>The reason stock's <c>AddScience</c> gives a submission (decompiled:
        /// <c>xmitScalar != 1</c> is ScienceTransmission, otherwise VesselRecovery; a ground
        /// station submits with the default 1). Pure.</summary>
        internal static string SubmissionReasonKey(float xmitScalar)
        {
            return xmitScalar != 1f
                ? TransactionReasons.ScienceTransmission.ToString()
                : TransactionReasons.VesselRecovery.ToString();
        }

        static void Prefix(ScienceSubject subject, float xmitScalar, ProtoVessel source, out bool __state)
        {
            __state = false;
            if (ParsekGameModeGate.CheckInert("DeployedScienceSubmitScopePatch.Prefix")) return; // S9 game-mode gate
            string subjectId = subject != null ? subject.id : null;
            if (!IsGroundStationSubmission(subjectId, source != null)) return;
            GameStateRecorder.EnterClockCauseScope("ground-station science " + subjectId);
            GameStateRecorder.GroundStationSubmissionReasonKey = SubmissionReasonKey(xmitScalar);
            __state = true;
        }

        static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state)
            {
                GameStateRecorder.GroundStationSubmissionReasonKey = null;
                GameStateRecorder.ExitClockCauseScope("ground-station science");
            }
            return __exception;
        }
    }

    /// <summary>
    /// Opens the clock-cause scope around a Sentinel telescope's detection
    /// (CONTRACT-COMPLETED-BY-DEPLOYED-SCIENCE-TAGGED-TO-THE-FLIGHT, the comet half). Stock's
    /// <c>SentinelScenario</c> coroutine scans every deployed Sentinel about once a day and, on a
    /// random draw, spawns a comet (<c>SpawnComet(Vessel, CometOrbitType)</c>, which fires
    /// <c>onSentinelCometDetected</c>; <c>CometDetectionParameter</c> then calls
    /// <c>SetComplete</c> and <c>Root.Complete()</c> inside that fire) or an asteroid
    /// (<c>SentinelParameter.DiscoverAsteroid</c>, which completes its contract inside the
    /// call). Both run on the scan clock while any flight is live, and the telescope works
    /// on its own once deployed, like a ground station, so the completion and its reward are
    /// filed under no flight.
    /// </summary>
    [HarmonyPatch]
    internal static class SentinelDetectionScopePatch
    {
        private const string Tag = "ClockCauseScope";

        static IEnumerable<MethodBase> TargetMethods()
        {
            var targets = ResolveTargetMethodsForTesting();
            if (targets.Count < 2)
                ParsekLog.Warn(Tag,
                    $"Sentinel detection targets resolved {targets.Count}/2 - a contract a Sentinel scan completes " +
                    "may be filed under the live flight.");
            return targets;
        }

        internal static List<MethodBase> ResolveTargetMethodsForTesting()
        {
            var targets = new List<MethodBase>();
            MethodBase spawnComet = null;
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(SentinelMission.SentinelScenario)))
            {
                if (m.Name != "SpawnComet") continue;
                var ps = m.GetParameters();
                // The private (Vessel, CometOrbitType) overload: every public overload reaches it.
                if (ps.Length == 2 && ps[0].ParameterType == typeof(Vessel)
                    && ps[1].ParameterType != typeof(string))
                    spawnComet = m;
            }
            if (spawnComet != null) targets.Add(spawnComet);
            var discover = AccessTools.Method(typeof(SentinelMission.SentinelParameter), "DiscoverAsteroid");
            if (discover != null) targets.Add(discover);
            return targets;
        }

        static void Prefix(MethodBase __originalMethod, out bool __state)
        {
            __state = false;
            if (ParsekGameModeGate.CheckInert("SentinelDetectionScopePatch.Prefix")) return; // S9 game-mode gate
            GameStateRecorder.EnterClockCauseScope("Sentinel detection " + (__originalMethod != null ? __originalMethod.Name : "?"));
            __state = true;
        }

        static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state) GameStateRecorder.ExitClockCauseScope("Sentinel detection");
            return __exception;
        }
    }
}
