using System;
using UnityEngine;

namespace Parsek
{
    internal partial class WatchModeController
    {
        internal static Func<float> RealtimeNow = GetRealtimeSafe;
        internal static Func<double> CurrentUTNow = GetCurrentUTSafe;
        internal static Func<float> CurrentWarpRateNow = GetCurrentWarpRateSafe;

        private static void RemoveWatchModeControlLockSafe()
        {
            try
            {
                InputLockManager.RemoveControlLock(WatchModeLockId);
            }
            catch (System.Security.SecurityException)
            {
                // Unit-test host does not provide the real KSP input manager.
            }
            catch (MethodAccessException)
            {
                // Same fallback for non-Unity unit-test environments.
            }
        }

        private static Vessel GetActiveVesselSafe()
        {
            try
            {
                return FlightGlobals.ActiveVessel;
            }
            catch (System.Security.SecurityException)
            {
                return null;
            }
            catch (MethodAccessException)
            {
                return null;
            }
        }

        private static bool IsUnityObjectAvailable(UnityEngine.Object obj)
        {
            if (ReferenceEquals(obj, null))
                return false;

            try
            {
                return obj != null;
            }
            catch (System.Security.SecurityException)
            {
                return true;
            }
            catch (MethodAccessException)
            {
                return true;
            }
        }

        private static void DestroyUnityObjectSafe(UnityEngine.Object obj)
        {
            if (!IsUnityObjectAvailable(obj))
                return;

            try
            {
                UnityEngine.Object.Destroy(obj);
            }
            catch (System.Security.SecurityException)
            {
                // Unit-test host may provide inert Unity objects without runtime backing.
            }
            catch (MethodAccessException)
            {
                // Same fallback for non-Unity unit-test environments.
            }
        }

        /// <summary>
        /// Returns true when an overlap cycle lifecycle event (ExplosionHoldStart / End)
        /// should be ignored because it concerns a cycle the user is not watching.
        /// Real cycle indices are >= 0; sentinel values (-1 ready-for-next, -2 holding)
        /// never match a real event cycle index, so they too are ignored and cannot be
        /// clobbered mid-flight. Relies on the invariant that
        /// <c>GhostPlaybackEngine.UpdateExpireAndPositionOverlaps</c> (the only emission
        /// site for these two action types, around line 1186) always populates
        /// <c>NewCycleIndex</c> from the expiring ghost's non-negative
        /// <c>loopCycleIndex</c>; a hypothetical negative emission would silently match
        /// a sentinel.
        /// </summary>
        internal static bool ShouldIgnoreOverlapCycleEvent(long eventCycleIndex, long watchedCycleIndex)
        {
            return eventCycleIndex != watchedCycleIndex;
        }

        private static float GetRealtimeSafe()
        {
            try
            {
                return Time.time;
            }
            catch (System.Security.SecurityException)
            {
                return 0f;
            }
            catch (MethodAccessException)
            {
                return 0f;
            }
        }

        private static double GetCurrentUTSafe()
        {
            try
            {
                return Planetarium.GetUniversalTime();
            }
            catch (System.Security.SecurityException)
            {
                return double.NaN;
            }
            catch (MethodAccessException)
            {
                return double.NaN;
            }
        }

        private static float GetCurrentWarpRateSafe()
        {
            try
            {
                return TimeWarp.CurrentRate;
            }
            catch (System.Security.SecurityException)
            {
                return 1f;
            }
            catch (MethodAccessException)
            {
                return 1f;
            }
        }

        private static int GetFrameCountSafe()
        {
            try
            {
                return Time.frameCount;
            }
            catch (System.Security.SecurityException)
            {
                return -1;
            }
            catch (MethodAccessException)
            {
                return -1;
            }
        }

        private static FlightCamera GetFlightCameraSafe()
        {
            try
            {
                return FlightCamera.fetch;
            }
            catch (System.Security.SecurityException)
            {
                return null;
            }
            catch (MethodAccessException)
            {
                return null;
            }
        }
    }
}
