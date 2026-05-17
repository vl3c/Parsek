using System;
using System.Globalization;
using HarmonyLib;
using KSP.UI.Screens;

namespace Parsek.Patches
{
    /// <summary>
    /// Arms a <see cref="StockActionIntentMarker"/> when the player clicks Fly on a
    /// nearby-vessel marker at KSC. The marker authorizes immediate switch-segment
    /// start when the FLIGHT scene-load tail consumes it in Phase C.
    ///
    /// Decompiled KSCVesselMarkers.FlyVessel(Vessel v) (KSP 1.12.5 Assembly-CSharp.dll,
    /// load-bearing excerpt): the handler calls
    /// <c>FlightDriver.StartAndFocusVessel("persistent", FlightGlobals.Vessels.IndexOf(v))</c>,
    /// which saves the "persistent" SFS and transitions to FLIGHT. The Prefix arms
    /// BEFORE the save+scene-transition so the serialized marker survives into
    /// FLIGHT. The method is <c>void</c>, so there is no synchronous early-return
    /// signal a Postfix could read for refusal cleanup; cross-run-orphan + TTL handle
    /// the leak case where the scene transition never completes.
    /// </summary>
    // FlyVessel is non-public on KSCVesselMarkers, so we use the string-based
    // HarmonyPatch attribute. The argument type is captured to disambiguate from
    // any future overload; HarmonyPatch's typeof([])-based overload disambiguates
    // by parameter types.
    [HarmonyPatch(typeof(KSCVesselMarkers), "FlyVessel", new Type[] { typeof(Vessel) })]
    internal static class KscVesselMarkerFlyPatch
    {
        static void Prefix(Vessel v)
        {
            if (v == null)
            {
                ParsekLog.Warn("SwitchIntentPatch",
                    "KSC marker Fly intent not armed: vessel argument is null");
                return;
            }

            // KSC marker Fly is never used for Parsek ghosts (ghost vessels are not
            // physics-loaded at KSC), but guard defensively in case a future ghost
            // surfaces here — if it does, we must NOT arm because the click won't
            // produce a scene transition.
            if (GhostMapPresence.IsGhostMapVessel(v.persistentId))
            {
                ParsekLog.Verbose("SwitchIntentPatch",
                    $"KSC marker Fly intent not armed: target is a ghost vessel pid={v.persistentId}");
                return;
            }

            var scenario = ParsekScenario.Instance;
            if (scenario == null)
            {
                ParsekLog.Warn("SwitchIntentPatch",
                    $"KSC marker Fly intent not armed: ParsekScenario.Instance is null targetPid={v.persistentId}");
                return;
            }

            var marker = new StockActionIntentMarker
            {
                IntentId = Guid.NewGuid(),
                Action = StockActionType.KscMarkerFly,
                TargetVesselPersistentId = v.persistentId,
                SourceScene = StockActionSourceScene.SpaceCenter,
                CapturedRealtime = UnityEngine.Time.realtimeSinceStartup,
                CapturedUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0.0,
                ProcessSessionId = ParsekProcess.ProcessSessionId,
            };
            scenario.ArmStockActionIntent(marker);
            ParsekLog.Info("SwitchIntentPatch",
                $"KSC marker Fly intent armed: intentId={marker.IntentId:D} action={marker.Action} " +
                $"targetPid={marker.TargetVesselPersistentId} sourceScene={marker.SourceScene} " +
                $"capturedUT={marker.CapturedUT.ToString("R", CultureInfo.InvariantCulture)}");
        }
    }
}
