using System;
using HarmonyLib;
using UnityEngine;

namespace Parsek.Patches
{
    [HarmonyPatch(typeof(FloatingOrigin), "setOffset", new Type[] { typeof(Vector3d), typeof(Vector3d) })]
    internal static class FloatingOriginSetOffsetPatch
    {
        static void Postfix(Vector3d refPos, Vector3d nonFrame)
        {
            try
            {
                ReFlySettleStabilityTracker.RecordFloatingOriginShift(
                    refPos,
                    nonFrame,
                    Time.frameCount,
                    Time.realtimeSinceStartup);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("ReFlySettle",
                    $"FloatingOrigin.setOffset postfix failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
