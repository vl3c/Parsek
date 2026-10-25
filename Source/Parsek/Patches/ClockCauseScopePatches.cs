using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// submits with no source vessel; a submission a vessel of the live flight makes (its own
    /// science, or a ground station's data a kerbal carried aboard) is left alone.</para>
    /// <para>A submission from an UNLOADED vessel the live flight does not record opens the
    /// same scope (todo LEDGER-EVENTS-OF-MOD-BACKGROUND-SOURCES-TAGGED-TO-THE-FLIGHT): stock
    /// transmits only from a loaded vessel, but a mod (Kerbalism-style background science)
    /// transmits from unloaded ones, and the award, the body science milestone it reaches and
    /// any contract it completes belong to no flight. A loaded vessel outside the tree stays
    /// the flight's: the flight's EVA kerbal can run and transmit its experiment
    /// (<c>DeployExperimentExternal</c>). A recovery (its own scope names the owner) and a
    /// submission with no live recorder are left alone.</para>
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
            if (IsGroundStationSubmission(subjectId, source != null))
            {
                GameStateRecorder.EnterClockCauseScope("ground-station science " + subjectId);
                GameStateRecorder.GroundStationSubmissionReasonKey = SubmissionReasonKey(xmitScalar);
                __state = true;
                return;
            }
            if (source == null) return;
            uint pid = source.persistentId;
            Vessel live = source.vesselRef;
            uint refPid = live != null ? live.persistentId : 0u;
            bool loaded = live != null && live.loaded;
            bool inFlight = GameStateRecorder.IsLiveFlightVessel(pid)
                || (refPid != 0u && refPid != pid && GameStateRecorder.IsLiveFlightVessel(refPid));
            if (!GameStateRecorder.IsVesselEventOutsideTheFlight(
                    GameStateRecorder.HasLiveRecorder(), RecoveryDispatchScope.IsOpen, loaded, inFlight))
                return;
            GameStateRecorder.EnterClockCauseScope(string.Format(CultureInfo.InvariantCulture,
                "science {0} from unloaded vessel '{1}' pid={2}, which the live flight does not record",
                subjectId ?? "(none)", source.vesselName ?? "", pid));
            __state = true;
        }

        static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state)
            {
                GameStateRecorder.GroundStationSubmissionReasonKey = null;
                GameStateRecorder.ExitClockCauseScope("science submitted by no flight");
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

    /// <summary>
    /// Marks the two stock resource scanners' calls (todo
    /// LEDGER-EVENTS-OF-MOD-BACKGROUND-SOURCES-TAGGED-TO-THE-FLIGHT, the SCANsat half).
    /// Decompiled KSP 1.12.5: <c>ModuleOrbitalSurveyor.CompleteSurvey</c> pays the survey's
    /// science and calls <c>ResourceMap.UnlockPlanet</c>, and
    /// <c>ModuleBiomeScanner.RunAnalysis</c> calls <c>ResourceMap.UnlockBiome</c>; these are
    /// the only stock callers outside the Making History editor. An unlock with no stock scanner
    /// call open is a mod's (SCANsat's background coverage), which no flight causes, so
    /// <see cref="ResourceScanUnlockPlanetPatch"/> / <see cref="ResourceScanUnlockBiomePatch"/>
    /// file it untagged (<see cref="IsUnlockByNoFlight"/>). A stock scan keeps its flight tag
    /// whichever vessel carries the scanner: stock runs a scanner only on a loaded vessel, and
    /// the flight's EVA kerbal can run one on a vessel outside the tree (both events are
    /// <c>externalToEVAOnly</c>), which is still the flight's doing.
    /// </summary>
    [HarmonyPatch]
    internal static class ResourceScannerScopePatch
    {
        private const string Tag = "ClockCauseScope";

        /// <summary>How many stock scanner calls are open (nested only in theory).</summary>
        internal static int OpenScannerCalls { get; private set; }

        static IEnumerable<MethodBase> TargetMethods()
        {
            var targets = ResolveTargetMethodsForTesting();
            if (targets.Count < 2)
                ParsekLog.Warn(Tag,
                    $"Resource scanner targets resolved {targets.Count}/2 - a stock scan may be filed under no flight.");
            return targets;
        }

        internal static List<MethodBase> ResolveTargetMethodsForTesting()
        {
            var targets = new List<MethodBase>();
            var survey = AccessTools.Method(typeof(ModuleOrbitalSurveyor), "CompleteSurvey",
                new[] { typeof(ScienceData), typeof(Vessel), typeof(bool) });
            if (survey != null) targets.Add(survey);
            var analysis = AccessTools.Method(typeof(ModuleBiomeScanner), "RunAnalysis", Type.EmptyTypes);
            if (analysis != null) targets.Add(analysis);
            return targets;
        }

        /// <summary>Pure: true when a resource unlock belongs to no flight because no stock
        /// scanner call is open (a mod's scan), while a live recorder would claim it and no
        /// recovery owns it.</summary>
        internal static bool IsUnlockByNoFlight(bool hasLiveRecorder, bool recoveryScopeOpen, bool stockScannerOpen)
        {
            return hasLiveRecorder && !recoveryScopeOpen && !stockScannerOpen;
        }

        static void Prefix(out bool __state)
        {
            __state = false;
            if (ParsekGameModeGate.CheckInert("ResourceScannerScopePatch.Prefix")) return; // S9 game-mode gate
            EnterScannerCall();
            __state = true;
        }

        static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state) ExitScannerCall();
            return __exception;
        }

        internal static void EnterScannerCall()
        {
            OpenScannerCalls++;
        }

        internal static void ExitScannerCall()
        {
            if (OpenScannerCalls > 0) OpenScannerCalls--;
        }

        /// <summary>Files one captured unlock: inside the clock-cause scope when no flight
        /// causes it (<see cref="IsUnlockByNoFlight"/>), else as today.</summary>
        internal static void CaptureUnlock(double ut, string bodyName, string biomeName)
        {
            bool noFlight = IsUnlockByNoFlight(GameStateRecorder.HasLiveRecorder(),
                RecoveryDispatchScope.IsOpen, OpenScannerCalls > 0);
            if (!noFlight)
            {
                GameStateRecorder.OnResourceScanUnlocked(ut, bodyName, biomeName);
                return;
            }
            GameStateRecorder.EnterClockCauseScope("resource scan of " + (bodyName ?? "?")
                + " with no stock scanner (a mod's scan)");
            try
            {
                GameStateRecorder.OnResourceScanUnlocked(ut, bodyName, biomeName);
            }
            finally
            {
                GameStateRecorder.ExitClockCauseScope("resource scan with no stock scanner");
            }
        }

        internal static void ResetForTesting()
        {
            OpenScannerCalls = 0;
        }
    }
}
