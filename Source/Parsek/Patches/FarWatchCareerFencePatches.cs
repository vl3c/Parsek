using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Contracts;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Pure decisions of the far-watch career fences (docs/dev/design-far-watch.md 10.1 /
    /// 10.2): every stock condition that reads the flown ship is fenced both ways while the
    /// camera drone stands in for it, so a skipped poll or handler can neither complete nor
    /// undo a parameter. Contracts are never paused as a whole.
    /// </summary>
    internal static class FarWatchFencePolicy
    {
        internal const string Tag = "FarWatch";

        /// <summary>The 18 polled parameter classes of audit D (OnUpdate reads the active vessel).</summary>
        internal static readonly string[] PolledParameterTypeNames =
        {
            "Contracts.Parameters.PartTest",
            "Contracts.Parameters.ReachAltitudeEnvelope",
            "Contracts.Parameters.ReachBiome",
            "Contracts.Parameters.ReachSpeedEnvelope",
            "FinePrint.Contracts.Parameters.AsteroidParameter",
            "FinePrint.Contracts.Parameters.CometParameter",
            "FinePrint.Contracts.Parameters.CrewCapacityParameter",
            "FinePrint.Contracts.Parameters.CrewTraitParameter",
            "FinePrint.Contracts.Parameters.LocationAndSituationParameter",
            "FinePrint.Contracts.Parameters.MobileBaseParameter",
            "FinePrint.Contracts.Parameters.PartRequestParameter",
            "FinePrint.Contracts.Parameters.ResourcePossessionParameter",
            "FinePrint.Contracts.Parameters.SpecificOrbitParameter",
            "FinePrint.Contracts.Parameters.SpecificVesselParameter",
            "FinePrint.Contracts.Parameters.StabilityParameter",
            "FinePrint.Contracts.Parameters.StationaryPointParameter",
            "FinePrint.Contracts.Parameters.SurveyWaypointParameter",
            "FinePrint.Contracts.Parameters.VesselSystemsParameter",
        };

        /// <summary>Patch points: 18 polls, 4 event handlers, the experiment judge, the reset.</summary>
        internal const int ContractPatchPointCount = 24;

        /// <summary>The two ungated milestone nodes (10.2).</summary>
        internal const int MilestonePatchPointCount = 2;

        internal static bool IsPolledParameterTypeName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
                return false;
            return Array.IndexOf(PolledParameterTypeNames, fullName) >= 0;
        }

        /// <summary>A polled class reads only the active vessel: fence while the drone is it.</summary>
        internal static bool ShouldFencePoll(bool droneActive)
        {
            return droneActive;
        }

        /// <summary>An event-driven class is judged on the event's own vessel.</summary>
        internal static bool ShouldFenceEvent(bool eventVesselIsDrone)
        {
            return eventVesselIsDrone;
        }

        /// <summary>
        /// ResetContracts re-evaluates every contract against the active vessel: fence the calls
        /// that would see the drone, let the ones that see the ship run (stock's switch-back).
        /// </summary>
        internal static bool ShouldFenceReset(bool activeIsDrone)
        {
            return activeIsDrone;
        }

        /// <summary>
        /// Rendezvous tracks a pair of loaded vessels: the drone loading beside the active ship
        /// (start) and the ship reloading beside the active drone (end) are both refused.
        /// </summary>
        internal static bool ShouldFenceRendezvous(bool loadedIsDrone, bool activeIsDrone)
        {
            return loadedIsDrone || activeIsDrone;
        }

        internal static string FormatFenced(string site, uint vesselPid, bool droneActive)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "fenced {0} vessel={1} droneActive={2}", site ?? "?", vesselPid, droneActive);
        }

        internal static string FormatResolved(string group, int resolved, int expected, IList<string> missing)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "career fences {0} resolved={1}/{2} missing=[{3}]",
                group ?? "?", resolved, expected,
                missing != null && missing.Count > 0 ? string.Join(",", missing) : "");
        }

        internal static void LogFenced(string site, Vessel v, bool droneActive)
        {
            uint pid = 0;
            try { pid = v != null ? v.persistentId : 0u; } catch (Exception) { }
            ParsekLog.VerboseRateLimited(Tag, "fenced-" + (site ?? "?"),
                FormatFenced(site, pid, droneActive), 5.0);
        }

        internal static bool EventVesselIsDrone(Vessel v)
        {
            return v != null && CameraDroneIdentity.Exists && CameraDrone.Is(v);
        }

        /// <summary>How many patch points resolved at load, for the in-game FarWatch category.</summary>
        internal static int ResolvedPollCount { get; set; }
        internal static int ResolvedOtherCount { get; set; }
    }

    /// <summary>OnUpdate prefixes on the 18 polled classes, resolved per declaring type.</summary>
    [HarmonyPatch]
    internal static class FarWatchPolledParameterFencePatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var targets = new List<MethodBase>();
            var missing = new List<string>();
            foreach (string name in FarWatchFencePolicy.PolledParameterTypeNames)
            {
                Type t = AccessTools.TypeByName(name);
                MethodInfo m = t != null ? AccessTools.DeclaredMethod(t, "OnUpdate", Type.EmptyTypes) : null;
                if (m == null)
                    missing.Add(name);
                else
                    targets.Add(m);
            }
            FarWatchFencePolicy.ResolvedPollCount = targets.Count;
            string line = FarWatchFencePolicy.FormatResolved("poll", targets.Count,
                FarWatchFencePolicy.PolledParameterTypeNames.Length, missing);
            if (missing.Count > 0)
                ParsekLog.Warn(FarWatchFencePolicy.Tag, line);
            else
                ParsekLog.Info(FarWatchFencePolicy.Tag, line);
            return targets;
        }

        static bool Prefix(object __instance, MethodBase __originalMethod)
        {
            if (ParsekGameModeGate.CheckInert("FarWatchPolledParameterFencePatch.Prefix")) return true; // S9 game-mode gate
            if (!CameraDroneIdentity.Exists)
                return true;
            bool droneActive = CameraDrone.IsActive();
            if (!FarWatchFencePolicy.ShouldFencePoll(droneActive))
                return true;
            string site = (__originalMethod != null && __originalMethod.DeclaringType != null
                ? __originalMethod.DeclaringType.Name : "?") + ".OnUpdate";
            FarWatchFencePolicy.LogFenced(site, null, droneActive);
            return false;
        }
    }

    internal static class FarWatchFenceTargets
    {
        internal static MethodBase Resolve(Type t, string method, Type[] args)
        {
            MethodBase m = t != null ? AccessTools.DeclaredMethod(t, method, args) : null;
            string site = (t != null ? t.Name : "?") + "." + method;
            if (m == null)
                ParsekLog.Warn(FarWatchFencePolicy.Tag,
                    "career fence target missing " + site + " - that condition is not fenced during a far watch");
            else
            {
                FarWatchFencePolicy.ResolvedOtherCount++;
                ParsekLog.Info(FarWatchFencePolicy.Tag, "career fence target resolved " + site);
            }
            return m;
        }
    }

    [HarmonyPatch]
    internal static class FarWatchReachDestinationFencePatch
    {
        static MethodBase TargetMethod()
        {
            return FarWatchFenceTargets.Resolve(typeof(Contracts.Parameters.ReachDestination), "OnVesselSOIChange",
                new[] { typeof(GameEvents.HostedFromToAction<Vessel, CelestialBody>) });
        }

        static bool Prefix(GameEvents.HostedFromToAction<Vessel, CelestialBody> __0)
        {
            if (ParsekGameModeGate.CheckInert("FarWatchReachDestinationFencePatch.Prefix")) return true; // S9 game-mode gate
            Vessel v = __0.host;
            if (!FarWatchFencePolicy.ShouldFenceEvent(FarWatchFencePolicy.EventVesselIsDrone(v)))
                return true;
            FarWatchFencePolicy.LogFenced("ReachDestination.OnVesselSOIChange", v, true);
            return false;
        }
    }

    [HarmonyPatch]
    internal static class FarWatchReachSituationFencePatch
    {
        static MethodBase TargetMethod()
        {
            return FarWatchFenceTargets.Resolve(typeof(Contracts.Parameters.ReachSituation), "OnVesselSituationChange",
                new[] { typeof(GameEvents.HostedFromToAction<Vessel, Vessel.Situations>) });
        }

        static bool Prefix(GameEvents.HostedFromToAction<Vessel, Vessel.Situations> __0)
        {
            if (ParsekGameModeGate.CheckInert("FarWatchReachSituationFencePatch.Prefix")) return true; // S9 game-mode gate
            Vessel v = __0.host;
            if (!FarWatchFencePolicy.ShouldFenceEvent(FarWatchFencePolicy.EventVesselIsDrone(v)))
                return true;
            FarWatchFencePolicy.LogFenced("ReachSituation.OnVesselSituationChange", v, true);
            return false;
        }
    }

    [HarmonyPatch]
    internal static class FarWatchReachFlightEnvelopeFencePatch
    {
        static MethodBase TargetMethod()
        {
            return FarWatchFenceTargets.Resolve(typeof(Contracts.Parameters.ReachFlightEnvelope), "OnVesselChange",
                new[] { typeof(Vessel) });
        }

        static bool Prefix(Vessel __0)
        {
            if (ParsekGameModeGate.CheckInert("FarWatchReachFlightEnvelopeFencePatch.Prefix")) return true; // S9 game-mode gate
            if (!FarWatchFencePolicy.ShouldFenceEvent(FarWatchFencePolicy.EventVesselIsDrone(__0)))
                return true;
            FarWatchFencePolicy.LogFenced("ReachFlightEnvelope.OnVesselChange", __0, true);
            return false;
        }
    }

    [HarmonyPatch]
    internal static class FarWatchVesselDestinationFencePatch
    {
        static MethodBase TargetMethod()
        {
            return FarWatchFenceTargets.Resolve(
                typeof(FinePrint.Contracts.Parameters.VesselDestinationParameter), "CheckFlightLog",
                new[] { typeof(Vessel) });
        }

        static bool Prefix(Vessel __0)
        {
            if (ParsekGameModeGate.CheckInert("FarWatchVesselDestinationFencePatch.Prefix")) return true; // S9 game-mode gate
            if (!FarWatchFencePolicy.ShouldFenceEvent(FarWatchFencePolicy.EventVesselIsDrone(__0)))
                return true;
            FarWatchFencePolicy.LogFenced("VesselDestinationParameter.CheckFlightLog", __0, true);
            return false;
        }
    }

    /// <summary>The survey experiment judge reads the active vessel.</summary>
    [HarmonyPatch]
    internal static class FarWatchSurveyExperimentFencePatch
    {
        static MethodBase TargetMethod()
        {
            return FarWatchFenceTargets.Resolve(
                typeof(FinePrint.Contracts.Parameters.SurveyWaypointParameter), "CheckExperimentResults",
                new[] { typeof(ScienceData) });
        }

        static bool Prefix()
        {
            if (ParsekGameModeGate.CheckInert("FarWatchSurveyExperimentFencePatch.Prefix")) return true; // S9 game-mode gate
            if (!CameraDroneIdentity.Exists)
                return true;
            bool droneActive = CameraDrone.IsActive();
            if (!FarWatchFencePolicy.ShouldFencePoll(droneActive))
                return true;
            FarWatchFencePolicy.LogFenced("SurveyWaypointParameter.CheckExperimentResults", null, droneActive);
            return false;
        }
    }

    /// <summary>
    /// ResetContracts (private) runs on onVesselChange and OnFlightGlobalsReady: the calls
    /// that see the drone as the active vessel are fenced, the ones that see the ship run.
    /// </summary>
    [HarmonyPatch]
    internal static class FarWatchResetContractsFencePatch
    {
        static MethodBase TargetMethod()
        {
            return FarWatchFenceTargets.Resolve(typeof(ContractSystem), "ResetContracts", Type.EmptyTypes);
        }

        static bool Prefix()
        {
            if (ParsekGameModeGate.CheckInert("FarWatchResetContractsFencePatch.Prefix")) return true; // S9 game-mode gate
            if (!CameraDroneIdentity.Exists)
                return true;
            bool droneActive = CameraDrone.IsActive();
            if (!FarWatchFencePolicy.ShouldFenceReset(droneActive))
                return true;
            ParsekLog.Info(FarWatchFencePolicy.Tag,
                "fenced ContractSystem.ResetContracts droneActive=True (the switch's calls that see the ship run)");
            return false;
        }
    }

    /// <summary>
    /// The mandatory Rendezvous fence (10.2): keyed on the drone existing, because the drone
    /// loads beside the active ship at the start and the ship reloads beside the active drone
    /// at the end.
    /// </summary>
    [HarmonyPatch]
    internal static class FarWatchRendezvousFencePatch
    {
        static MethodBase TargetMethod()
        {
            return FarWatchFenceTargets.Resolve(typeof(KSPAchievements.Rendezvous), "OnVesselLoaded",
                new[] { typeof(Vessel) });
        }

        static bool Prefix(Vessel __0)
        {
            if (ParsekGameModeGate.CheckInert("FarWatchRendezvousFencePatch.Prefix")) return true; // S9 game-mode gate
            if (!CameraDroneIdentity.Exists)
                return true;
            bool loadedIsDrone = __0 != null && CameraDrone.Is(__0);
            bool activeIsDrone = CameraDrone.IsActive();
            if (!FarWatchFencePolicy.ShouldFenceRendezvous(loadedIsDrone, activeIsDrone))
                return true;
            uint pid = 0;
            try { pid = __0 != null ? __0.persistentId : 0u; } catch (Exception) { }
            ParsekLog.Info(FarWatchFencePolicy.Tag, string.Format(CultureInfo.InvariantCulture,
                "fenced Rendezvous.OnVesselLoaded vessel={0} loadedIsDrone={1} droneActive={2}",
                pid, loadedIsDrone, activeIsDrone));
            return false;
        }
    }

    [HarmonyPatch]
    internal static class FarWatchCelestialBodyReturnFencePatch
    {
        static MethodBase TargetMethod()
        {
            return FarWatchFenceTargets.Resolve(typeof(KSPAchievements.CelestialBodyReturn), "OnVesselSituationChange",
                new[] { typeof(GameEvents.HostedFromToAction<Vessel, Vessel.Situations>) });
        }

        static bool Prefix(GameEvents.HostedFromToAction<Vessel, Vessel.Situations> __0)
        {
            if (ParsekGameModeGate.CheckInert("FarWatchCelestialBodyReturnFencePatch.Prefix")) return true; // S9 game-mode gate
            Vessel v = __0.host;
            if (!FarWatchFencePolicy.ShouldFenceEvent(FarWatchFencePolicy.EventVesselIsDrone(v)))
                return true;
            FarWatchFencePolicy.LogFenced("CelestialBodyReturn.OnVesselSituationChange", v, true);
            return false;
        }
    }
}
