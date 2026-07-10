using System;
using UnityEngine;

namespace Parsek.InGameTests.Helpers
{
    /// <summary>
    /// Stock Bug #4803 late-switch guard for programmatic scene reloads (test-runner
    /// baseline restores and quickload-resume helpers only; no product code).
    ///
    /// <para><b>The hole this closes (2026-07-10 soft-freeze):</b> KSP's persistent
    /// FlightCamera survives a FLIGHT-&gt;FLIGHT reload only because stock
    /// <c>FlightCamera.OnSceneSwitch</c> (a <c>GameEvents.onGameSceneLoadRequested</c>
    /// handler) re-parents its pivot under the DontDestroyOnLoad PSystemSetup root via
    /// <c>SetTargetTransform(PSystemSetup.Instance.transform)</c>. That rescue fires
    /// synchronously INSIDE <c>FlightDriver.StartAndFocusVessel</c> /
    /// <c>HighLogic.LoadScene</c>. Any vessel switch that lands AFTER the commit call
    /// returns but BEFORE the old scene unloads (observed 2026-07-10: a same-frame late
    /// switch to a transient EVA kerbal, "Hudmy Kerman", from an unidentified caller
    /// with a live-vs-save vessel-index mismatch) re-fires stock
    /// <c>FlightCamera.OnVesselChange</c>, which re-parents the pivot under the doomed
    /// vessel. The unload then destroys camera+pivot with that vessel,
    /// <c>FlightCamera.fetch</c> goes null, and the NEW scene's
    /// <c>FlightDriver.Start</c> NREs in <c>SetModeImmediate</c>, leaving FlightGlobals
    /// half-initialized and every per-frame consumer NRE-flooding permanently.</para>
    ///
    /// <para><b>Contract:</b> <see cref="Arm"/> just before the scene-load commit;
    /// while armed, every <c>GameEvents.onVesselChange</c> immediately re-pins the
    /// pivot back onto the DDOL PSystemSetup root (the exact rescue stock
    /// OnSceneSwitch performs, re-applied after the late switch;
    /// <c>SetTargetTransform</c> also detaches the OnJustAboutToBeDestroyed callback
    /// from the dying vessel per the decompiled <c>SetTarget</c>).
    /// <c>GameEvents.onLevelWasLoaded</c> disarms: it fires in the NEW scene after
    /// Awake/OnEnable but BEFORE Start() methods, i.e. before
    /// <c>FlightDriver.Start</c>'s legitimate <c>SetActiveVessel</c>, which must NOT
    /// be intercepted (re-pinning then would break the new scene's camera targeting).
    /// A realtime TTL fail-safe disarms on the next event after a failed load so the
    /// handlers can never re-pin a genuinely later vessel switch, and the runner's
    /// Cancel/teardown paths call <see cref="Disarm"/> explicitly (Unity StopCoroutine
    /// skips finally blocks, mirroring the batch exception monitor's teardown).</para>
    /// </summary>
    internal static class FlightCameraReloadPin
    {
        private const string Tag = "TestRunner";

        /// <summary>
        /// Fail-safe realtime TTL for the arm window. The window normally closes at the
        /// new scene's onLevelWasLoaded (seconds at most); a load that never completes
        /// must not leave the handlers live forever, so the first event past the TTL
        /// disarms instead of re-pinning. Generous because a heavily modded FLIGHT
        /// reload can legitimately take tens of seconds.
        /// </summary>
        internal const float ArmTtlSeconds = 60f;

        private static bool armed;
        private static float armedAtRealtime;
        private static string armContext;
        private static int rePinCount;

        internal static bool IsArmed => armed;

        /// <summary>What an armed-window event should do. Pure decision core.</summary>
        internal enum PinWindowAction { Ignore, RePin, AutoDisarm }

        /// <summary>
        /// Pure decision for an event landing in the (possibly) armed window: not armed
        /// -&gt; ignore; armed past the TTL -&gt; fail-safe disarm WITHOUT re-pinning (the
        /// load this window guarded is long over; a re-pin now would hijack a legitimate
        /// vessel switch); otherwise re-pin. A non-positive TTL disables the fail-safe.
        /// </summary>
        internal static PinWindowAction DecideOnEventWhileArmed(
            bool isArmed, float armedAtRealtime, float nowRealtime, float ttlSeconds)
        {
            if (!isArmed)
                return PinWindowAction.Ignore;
            if (ttlSeconds > 0f && nowRealtime - armedAtRealtime >= ttlSeconds)
                return PinWindowAction.AutoDisarm;
            return PinWindowAction.RePin;
        }

        /// <summary>
        /// Opens the re-pin window. Call immediately BEFORE the scene-load commit
        /// (<c>FlightDriver.StartAndFocusVessel</c> / <c>Game.Start()</c>) so the
        /// window brackets both the synchronous stock OnSceneSwitch rescue and any
        /// late same-frame (or later pre-unload) vessel switch. Idempotent:
        /// unsubscribe-before-subscribe guarantees exactly one handler even if a prior
        /// failed load leaked the subscription (Remove of an absent handler is a no-op,
        /// mirroring BeginBatchExceptionMonitor).
        /// </summary>
        internal static void Arm(string context)
        {
            GameEvents.onVesselChange.Remove(OnVesselChangeWhileArmed);
            GameEvents.onVesselChange.Add(OnVesselChangeWhileArmed);
            GameEvents.onLevelWasLoaded.Remove(OnLevelLoadedWhileArmed);
            GameEvents.onLevelWasLoaded.Add(OnLevelLoadedWhileArmed);
            armed = true;
            armedAtRealtime = Time.realtimeSinceStartup;
            armContext = context;
            rePinCount = 0;
            ParsekLog.Verbose(Tag,
                $"Camera re-pin window armed ({context}): any vessel switch before the next scene "
                + "load lands re-pins the FlightCamera pivot onto the DDOL PSystemSetup root "
                + "(stock Bug #4803 late-switch guard)");
        }

        /// <summary>
        /// Closes the re-pin window. Idempotent (safe to call when never armed); the
        /// GameEvents removes run unconditionally so a desynced flag can never strand a
        /// live handler. Called by onLevelWasLoaded (normal close), the TTL fail-safe,
        /// and the runner's Cancel / batch-teardown paths.
        /// </summary>
        internal static void Disarm(string reason)
        {
            GameEvents.onVesselChange.Remove(OnVesselChangeWhileArmed);
            GameEvents.onLevelWasLoaded.Remove(OnLevelLoadedWhileArmed);
            if (!armed)
                return;
            armed = false;
            ParsekLog.Verbose(Tag,
                $"Camera re-pin window disarmed ({reason}): armedBy='{armContext}' rePins={rePinCount}");
            armContext = null;
        }

        /// <summary>
        /// Unconditional end-of-frame re-pin: covers same-frame switches that fire
        /// through code paths other than onVesselChange. The caller (the restore core
        /// coroutine) yields WaitForEndOfFrame after the commit, then calls this; it
        /// pins if (and only if) the window is still armed. Exception-safe: runs during
        /// scene teardown.
        /// </summary>
        internal static void PinNowIfArmed(string context)
        {
            try
            {
                PinWindowAction action = DecideOnEventWhileArmed(
                    armed, armedAtRealtime, Time.realtimeSinceStartup, ArmTtlSeconds);
                if (action == PinWindowAction.AutoDisarm)
                {
                    ParsekLog.Warn(Tag,
                        $"Camera re-pin window exceeded {ArmTtlSeconds:F0}s TTL at end-of-frame pin "
                        + $"({context}); fail-safe disarm without re-pin (armedBy='{armContext}')");
                    Disarm("ttl-expired-at-end-of-frame-pin");
                    return;
                }
                if (action != PinWindowAction.RePin)
                {
                    ParsekLog.Verbose(Tag,
                        $"End-of-frame camera pin skipped ({context}): window not armed");
                    return;
                }
                if (TryPinCameraToDdolRoot())
                {
                    rePinCount++;
                    ParsekLog.Info(Tag,
                        $"End-of-frame camera pin ({context}): FlightCamera pivot pinned onto the DDOL "
                        + "PSystemSetup root before the old scene unloads (stock Bug #4803 same-frame guard)");
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"End-of-frame camera pin ({context}) threw: {ex.GetType().Name}: {ex.Message} - proceeding (backstops apply)");
            }
        }

        // While armed, ANY vessel switch in the commit-to-unload window is a threat to
        // the camera (stock FlightCamera.OnVesselChange has just re-parented the pivot
        // under the switch target, which the imminent unload will destroy). Re-pin the
        // pivot straight back onto the DDOL root and log a Warn naming the vessel so
        // the late switch is diagnosable from KSP.log. Exception-safe: this runs during
        // scene teardown.
        private static void OnVesselChangeWhileArmed(Vessel vessel)
        {
            try
            {
                PinWindowAction action = DecideOnEventWhileArmed(
                    armed, armedAtRealtime, Time.realtimeSinceStartup, ArmTtlSeconds);
                if (action == PinWindowAction.AutoDisarm)
                {
                    ParsekLog.Warn(Tag,
                        $"Camera re-pin window exceeded {ArmTtlSeconds:F0}s TTL on a vessel change; "
                        + $"fail-safe disarm without re-pin (armedBy='{armContext}')");
                    Disarm("ttl-expired-on-vessel-change");
                    return;
                }
                if (action != PinWindowAction.RePin)
                    return;

                string vesselName = vessel != null ? vessel.vesselName : "<null>";
                bool pinned = TryPinCameraToDdolRoot();
                if (pinned)
                    rePinCount++;
                ParsekLog.Warn(Tag,
                    $"Late vessel switch to '{vesselName}' inside the reload commit window "
                    + $"(armedBy='{armContext}'); "
                    + (pinned
                        ? "re-pinned the FlightCamera pivot onto the DDOL PSystemSetup root so the "
                          + "camera survives the old scene's unload (stock Bug #4803 late-switch guard)."
                        : "could NOT re-pin the FlightCamera pivot (see preceding warning); the "
                          + "reload's camera bring-up may fail (stock Bug #4803)."));
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Camera re-pin onVesselChange handler threw: {ex.GetType().Name}: {ex.Message} - proceeding (backstops apply)");
            }
        }

        // Normal close of the window: onLevelWasLoaded fires in the NEW scene after
        // Awake/OnEnable but BEFORE Start() methods, i.e. before FlightDriver.Start's
        // legitimate SetActiveVessel -> onVesselChange, so disarming here guarantees
        // the new scene's real camera targeting is never intercepted. Exception-safe.
        private static void OnLevelLoadedWhileArmed(GameScenes scene)
        {
            try
            {
                Disarm($"level-loaded:{scene}");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Camera re-pin onLevelWasLoaded handler threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // The exact rescue stock FlightCamera.OnSceneSwitch performs on
        // onGameSceneLoadRequested, re-applied after the late switch. SetTargetTransform
        // also detaches the OnJustAboutToBeDestroyed callback from the dying vessel
        // (decompiled FlightCamera.SetTarget), so the camera no longer dies with it.
        private static bool TryPinCameraToDdolRoot()
        {
            FlightCamera cam = FlightCamera.fetch;
            if (cam == null)
            {
                // In FLIGHT a null fetch means the persistent camera was already
                // destroyed (the corruption this guard exists to prevent) - Warn. In a
                // non-FLIGHT commit window (e.g. a TRACKSTATION->TRACKSTATION prime
                // restore in a session that never entered FLIGHT) no camera exists yet
                // and there is simply nothing to pin - Verbose, not a fault.
                if (HighLogic.LoadedScene == GameScenes.FLIGHT)
                {
                    ParsekLog.Warn(Tag,
                        "Camera re-pin skipped: FlightCamera.fetch is null (camera already destroyed; "
                        + "stock Bug #4803 corruption may already have happened)");
                }
                else
                {
                    ParsekLog.Verbose(Tag,
                        $"Camera re-pin skipped: no FlightCamera exists in {HighLogic.LoadedScene}; nothing to pin");
                }
                return false;
            }
            PSystemSetup psystem = PSystemSetup.Instance;
            if (psystem == null)
            {
                ParsekLog.Warn(Tag,
                    "Camera re-pin skipped: PSystemSetup.Instance is null; no DDOL root to pin onto");
                return false;
            }
            cam.SetTargetTransform(psystem.transform);
            return true;
        }
    }
}
