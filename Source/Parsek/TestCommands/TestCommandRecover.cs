using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>What one completion poll of a pressed Recover concludes.</summary>
    internal enum RecoverCompletionDecision
    {
        StillWaiting,
        Recovered,
        ReturnedToMenu,
        Timeout,
    }

    /// <summary>
    /// Pure half of the automation-only <c>Recover</c> seam verb (D18 player-action half,
    /// todo D18-REALSPAWN-RECOVER-SEAM-VERB-PAIR): press the flight scene's stock Recover
    /// button (the altimeter's <c>AltimeterSliderButtons.vesselRecoveryButton</c>) for the
    /// ACTIVE vessel, by invoking that button's own <c>onClick</c>, so stock runs its
    /// recovery exactly as for a player.
    ///
    /// <para>Stock order (decompiled KSP 1.12.5): the click handler
    /// <c>AltimeterSliderButtons.recoverVessel</c> re-checks
    /// <c>FlightGlobals.ClearToSave() == CLEAR</c> and
    /// <c>Parameters.Flight.CanLeaveToSpaceCenter</c>, then fires
    /// <c>GameEvents.OnVesselRecoveryRequested(FlightGlobals.ActiveVessel)</c>.
    /// <c>VesselRetrieval.onVesselRecoveryRequested</c> remembers <c>v.id</c>, saves
    /// <c>persistent</c> and loads SPACECENTER; 8 frames after the Space Center loads,
    /// <c>VesselRetrieval.recoverVessels</c> fires <c>onVesselRecovered(pv, false)</c> and
    /// destroys the vessel. Parsek's own handlers (<c>ParsekScenario.OnVesselRecoveryRequested</c>
    /// arming <c>InFlightRecoveryRequest</c>, then <c>OnVesselRecovered</c>) fire from those
    /// stock events; the seam fires none of them itself.</para>
    ///
    /// <para>Grammar: <c>cmd=Recover pid=&lt;persistentId&gt;</c>. <c>pid</c> must be the
    /// active vessel's: the stock button only ever recovers the active vessel, so the spec
    /// switches first (<c>SimulateStockSwitchClick pid=</c>) and names the vessel it means.
    /// TWO-PHASE, RequiresFlight, 120 s budget (the ExitToSpaceCenter class: a scene exit
    /// that re-reads no save).</para>
    /// </summary>
    internal static class TestCommandRecover
    {
        internal const string Verb = "Recover";
        internal const string PidKey = "pid";

        internal const string PidArgMissingReason = "recover-pid-arg-missing";
        internal const string PidArgInvalidReason = "recover-pid-arg-invalid";
        internal const string NoActiveVesselReason = "recover-no-active-vessel";
        internal const string NotActiveVesselReason = "recover-not-active-vessel";
        internal const string ButtonUnavailableReason = "recover-button-unavailable";
        internal const string ButtonLockedReason = "recover-button-locked";
        internal const string NotClearToSaveReason = "recover-not-clear-to-save";
        internal const string CannotLeaveReason = "recover-cannot-leave-to-space-center";

        // Post-press ERROR terminals.
        internal const string RequestNotFiredReason = "recover-request-not-fired";
        internal const string ReturnedToMenuReason = "recover-failed-returned-to-menu";
        internal const string TimeoutReason = "recover-timeout";

        /// <summary>The REJECTED vocabulary, in gate order. The wedge guard's refusal is
        /// ExitToSpaceCenter's <c>dialog-required variant=</c>, reused verbatim and
        /// deliberately not listed here. hlib.RECOVER_REASONS mirrors it
        /// (RealSpawnRecoverSourceSyncTests).</summary>
        internal static readonly string[] Reasons =
        {
            "recover-pid-arg-missing",
            "recover-pid-arg-invalid",
            "recover-no-active-vessel",
            "recover-not-active-vessel",
            "recover-button-unavailable",
            "recover-button-locked",
            "recover-not-clear-to-save",
            "recover-cannot-leave-to-space-center",
        };

        /// <summary>The post-press ERROR vocabulary.</summary>
        internal static readonly string[] ErrorReasons =
        {
            "recover-request-not-fired",
            "recover-failed-returned-to-menu",
            "recover-timeout",
        };

        /// <summary>The stock <c>ClearToSaveStatus.CLEAR</c> name, the only status the
        /// click handler acts on.</summary>
        internal const string ClearToSaveClear = "CLEAR";

        /// <summary>Parses <c>pid=</c> (invariant, decimal uint, nonzero); returns null on
        /// success or the refusal reason.</summary>
        internal static string ParsePid(string raw, out uint pid)
        {
            pid = 0;
            if (string.IsNullOrEmpty(raw))
                return PidArgMissingReason;
            if (!uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed)
                || parsed == 0)
                return PidArgInvalidReason;
            pid = parsed;
            return null;
        }

        /// <summary>
        /// The pre-press gate, in the order the stock button meets it: an active vessel
        /// that is the named one, a live Recover button that stock has UNLOCKED (landed or
        /// splashed on the home world, horizontal surface speed under 0.3 m/s, not paused,
        /// recovery allowed), then the two checks the click handler itself re-runs. Each
        /// refusal stands for a click that would do nothing (or pop a dialog no seam verb
        /// answers), so the seam must not click. Returns null to proceed.
        /// </summary>
        internal static string DecideGate(
            bool hasActiveVessel, uint activePid, uint pid,
            bool buttonFound, bool buttonInteractable,
            string clearToSaveStatus, bool canLeaveToSpaceCenter)
        {
            if (!hasActiveVessel) return NoActiveVesselReason;
            if (activePid != pid) return NotActiveVesselReason;
            if (!buttonFound) return ButtonUnavailableReason;
            if (!buttonInteractable) return ButtonLockedReason;
            if (clearToSaveStatus != ClearToSaveClear) return NotClearToSaveReason;
            if (!canLeaveToSpaceCenter) return CannotLeaveReason;
            return null;
        }

        /// <summary>
        /// One completion poll. OK needs BOTH a settled Space Center with a game loaded AND
        /// stock's own <c>onVesselRecovered</c> for the pid: the recovery runs 8 frames
        /// AFTER the scene settles, so a scene read alone would answer before the vessel was
        /// recovered. A MAINMENU settle is a fast failure; the budget is the catch-all.
        /// </summary>
        internal static RecoverCompletionDecision DecideCompletion(
            double elapsedSeconds, TestCommandScene scene, bool gameLoaded,
            bool recoveredObserved, double budgetSeconds)
        {
            if (scene == TestCommandScene.SpaceCenter && gameLoaded && recoveredObserved)
                return RecoverCompletionDecision.Recovered;
            if (scene == TestCommandScene.MainMenu)
                return RecoverCompletionDecision.ReturnedToMenu;
            if (elapsedSeconds >= budgetSeconds)
                return RecoverCompletionDecision.Timeout;
            return RecoverCompletionDecision.StillWaiting;
        }

        /// <summary>The OK payload: <c>pid= vessel= scene= recovered=true</c>.</summary>
        internal static List<KeyValuePair<string, string>> BuildOkPayload(
            uint pid, string vesselName, string sceneName)
        {
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("pid", pid.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("vessel", vesselName ?? string.Empty),
                new KeyValuePair<string, string>("scene", sceneName ?? string.Empty),
                new KeyValuePair<string, string>("recovered", "true"),
            };
        }

        /// <summary>The grep-stable press line.</summary>
        internal static string FormatPressedLine(uint pid, string vesselName, string situation, double ut)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "recover pressed pid=" + pid.ToString(ic)
                + " vessel=" + (vesselName ?? string.Empty)
                + " situation=" + (situation ?? string.Empty)
                + " ut=" + ut.ToString("F1", ic);
        }

        /// <summary>The grep-stable completion line.</summary>
        internal static string FormatCompleteLine(uint pid, string vesselName, string sceneName, double elapsed)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "recover complete pid=" + pid.ToString(ic)
                + " vessel=" + (vesselName ?? string.Empty)
                + " scene=" + (sceneName ?? string.Empty)
                + " elapsed=" + elapsed.ToString("F1", ic) + "s";
        }
    }
}
