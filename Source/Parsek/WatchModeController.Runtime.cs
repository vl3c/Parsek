using System;
using System.Runtime.CompilerServices;
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
            catch (MissingMethodException)
            {
                // Same fallback for non-Unity unit-test environments.
            }
        }

        private static Vessel GetActiveVesselSafe()
        {
            return ReadActiveVesselGuarded(ReadActiveVesselCore);
        }

        /// <summary>
        /// Reads <c>FlightGlobals.ActiveVessel</c>. Kept in its own
        /// <see cref="MethodImplOptions.NoInlining"/> core because mono runs <c>FlightGlobals</c>' failing static
        /// initializer at JIT time of the CALLING method, so the guard must not share a frame with the read.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Vessel ReadActiveVesselCore()
        {
            return FlightGlobals.ActiveVessel;
        }

        /// <summary>
        /// The exception guard behind <see cref="GetActiveVesselSafe"/>, taking the read as a delegate so every arm
        /// is drivable headlessly (no test host can make the real <c>FlightGlobals</c> throw on demand).
        ///
        /// <para>FOUR arms, two populations. The Security / MethodAccess / MissingMethod triple is this file's
        /// standard non-Unity unit-test-host fallback and stays silent. The fourth,
        /// <see cref="NullReferenceException"/>, is a LIVE-GAME fact measured 2026-08-08 on
        /// `V7M-minmus-player-loop` (MOON-LOOP-FINDINGS finding 2): <c>FlightGlobals.ActiveVessel</c> dereferences
        /// <c>FlightGlobals.fetch</c>, which is already gone by the time <c>ParsekFlight.OnDestroy</c> drives
        /// ExitWatchMode -> RestoreCameraAfterWatchExit, so every run that ENDED inside watch mode threw one Parsek
        /// NRE into KSP.log. Verbose rather than Warn: quitting while watching a ghost is normal, and both callers
        /// already treat a null active vessel as "no camera target to restore".</para>
        /// </summary>
        internal static Vessel ReadActiveVesselGuarded(Func<Vessel> reader)
        {
            if (reader == null)
                return null;

            try
            {
                return reader();
            }
            catch (NullReferenceException)
            {
                // Scene teardown: FlightGlobals (or its fetch singleton) is already destroyed.
                ParsekLog.Verbose("CameraFollow",
                    "GetActiveVesselSafe: FlightGlobals unavailable (scene teardown) - active vessel treated as null");
                return null;
            }
            catch (System.Security.SecurityException)
            {
                return null;
            }
            catch (MethodAccessException)
            {
                return null;
            }
            catch (MissingMethodException)
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
            catch (MissingMethodException)
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
            catch (MissingMethodException)
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
            catch (MissingMethodException)
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
            catch (MissingMethodException)
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
            catch (MissingMethodException)
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
            catch (MissingMethodException)
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
            catch (MissingMethodException)
            {
                return null;
            }
        }
    }
}
