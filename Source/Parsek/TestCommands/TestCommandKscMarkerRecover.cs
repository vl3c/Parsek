using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>Where a pressed <c>KscMarkerRecover</c> stands between polls.</summary>
    internal enum KscRecoverPhase
    {
        /// <summary>Waiting for the vessel's Space Center marker to exist and finish its setup.</summary>
        FindingMarker,
        /// <summary>The marker's Recover button was pressed; waiting for stock's recovery.</summary>
        Recovering,
    }

    /// <summary>What one completion poll of a pressed <c>KscMarkerRecover</c> does.</summary>
    internal enum KscRecoverPollAction
    {
        NotYet,
        PressRecover,
        MarkerNotFound,
        ButtonLocked,
        DismissSummary,
        NotRecovered,
        Ok,
        ReturnedToMenu,
        Timeout,
    }

    /// <summary>
    /// Pure half of the automation-only <c>KscMarkerRecover</c> seam verb: recover a landed
    /// or splashed vessel on the home world through its STOCK Space Center vessel marker,
    /// the way a player does it without leaving the Space Center: open the marker, press
    /// its Recover button.
    ///
    /// <para>Stock order (decompiled KSP 1.12.5): <c>KSCVesselMarkers.Awake</c> spawns, 15
    /// frames later (<c>SpawnVesselMarkers</c>), one <c>KSCVesselMarker</c> per vessel in
    /// <c>FlightGlobals.Vessels</c> that is <c>LandedOrSplashed</c> on
    /// <c>Planetarium.fetch.Home</c> and is neither <c>DeployedSciencePart</c> nor
    /// <c>DroppedPart</c>, and clears them while a facility screen is open. A marker wires
    /// its buttons in <c>CreateWindowContent</c> (from <c>AnchoredDialog.Start</c>);
    /// <c>Marker</c>'s click expands the panel (refused while <c>locked</c>, the KSC_UI
    /// input-lock state), and <c>RecoverButton</c>'s click runs
    /// <c>OnRecoverButtonInput</c> -> <c>Dismiss(Recover)</c> ->
    /// <c>KSCVesselMarkers.OnMarkerDismiss</c>, which one frame later runs the private
    /// <c>RecoverVessel</c>: <c>ShipConstruction.RecoverVesselFromFlight(v.protoVessel,
    /// flightState)</c> (firing <c>onVesselRecovered(pv, false)</c> first), a
    /// <c>persistent</c> save and a marker refresh. No confirm popup, no scene change, and
    /// <c>OnVesselRecoveryRequested</c> is not fired. The bool is stock's <c>quick</c>
    /// flag; FALSE here, so <c>VesselRecovery.OnVesselRecovered</c> opens a
    /// <c>MissionRecoveryDialog</c> in a game with Funding / Reputation / R&amp;D or for a
    /// crewed vessel.</para>
    ///
    /// <para>Grammar: <c>cmd=KscMarkerRecover pid=&lt;persistentId&gt;</c>, issued at the
    /// Space Center. TWO-PHASE, RequiresGameLoaded, 60 s budget.</para>
    /// </summary>
    internal static class TestCommandKscMarkerRecover
    {
        internal const string Verb = "KscMarkerRecover";
        internal const string PidKey = "pid";

        internal const string PidArgMissingReason = "kscrecover-pid-arg-missing";
        internal const string PidArgInvalidReason = "kscrecover-pid-arg-invalid";
        internal const string WrongSceneReason = "kscrecover-wrong-scene";
        internal const string VesselNotFoundReason = "kscrecover-vessel-not-found";
        internal const string TargetIsGhostReason = "kscrecover-target-is-ghost";
        internal const string NotRecoverableReason = "kscrecover-not-recoverable";
        // Decided after polling for the marker, before any click: late REJECTEDs (the
        // PlantFlag flag-lock-stable precedent), because the seam pressed nothing.
        internal const string MarkerNotFoundReason = "kscrecover-marker-not-found";
        internal const string ButtonLockedReason = "kscrecover-button-locked";

        // Post-press ERROR terminals.
        internal const string NotRecoveredReason = "kscrecover-not-recovered";
        internal const string ReturnedToMenuReason = "kscrecover-returned-to-menu";
        internal const string TimeoutReason = "kscrecover-timeout";

        /// <summary>The REJECTED vocabulary, in gate order (the marker pair last: decided by
        /// the poll). hlib.KSCRECOVER_REASONS mirrors it (KscMarkerRecoverSourceSyncTests).</summary>
        internal static readonly string[] Reasons =
        {
            "kscrecover-pid-arg-missing",
            "kscrecover-pid-arg-invalid",
            "kscrecover-wrong-scene",
            "kscrecover-vessel-not-found",
            "kscrecover-target-is-ghost",
            "kscrecover-not-recoverable",
            "kscrecover-marker-not-found",
            "kscrecover-button-locked",
        };

        /// <summary>The post-press ERROR vocabulary.</summary>
        internal static readonly string[] ErrorReasons =
        {
            "kscrecover-not-recovered",
            "kscrecover-returned-to-menu",
            "kscrecover-timeout",
        };

        /// <summary>Frames the seam waits for the vessel's marker to exist with its buttons
        /// wired. Stock spawns the markers 15 frames after <c>KSCVesselMarkers.Awake</c>
        /// and re-spawns them when a facility screen closes.</summary>
        internal const int MarkerWaitFrames = 180;

        /// <summary>Frames the seam waits, after the press, for <c>onVesselRecovered</c>.
        /// Stock recovers one frame after the dismiss.</summary>
        internal const int RecoverWaitFrames = 60;

        /// <summary>Frames after the observed recovery before OK, so stock's same-call save
        /// and its one-frame-later marker refresh have run.</summary>
        internal const int SettleFrames = 2;

        /// <summary>Parses <c>pid=</c> (invariant, ASCII decimal uint, nonzero); returns
        /// null on success or the refusal reason. Recover's parse, with this verb's tokens.</summary>
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

        /// <summary>Stock's marker predicate (<c>KSCVesselMarkers.SpawnVesselMarkers</c>):
        /// landed or splashed on the home world, and not a deployed science part or a
        /// dropped part.</summary>
        internal static bool HasStockMarker(
            bool landedOrSplashed, bool onHomeWorld, bool deployedSciencePart, bool droppedPart)
        {
            return landedOrSplashed && onHomeWorld && !deployedSciencePart && !droppedPart;
        }

        /// <summary>
        /// The pre-press gate, decided at dispatch before any side effect: the scene, and a
        /// real (non-ghost) vessel with that pid that stock gives a Space Center marker.
        /// Returns null to proceed.
        /// </summary>
        internal static string DecideGate(
            TestCommandScene scene, bool vesselFound, bool isGhost, bool hasStockMarker)
        {
            if (scene != TestCommandScene.SpaceCenter) return WrongSceneReason;
            if (isGhost) return TargetIsGhostReason;
            if (!vesselFound) return VesselNotFoundReason;
            if (!hasStockMarker) return NotRecoverableReason;
            return null;
        }

        /// <summary>
        /// One completion poll. A MAINMENU scene is a fast failure in every phase. While
        /// finding the marker nothing has been pressed, so a missing or unwired marker past
        /// <see cref="MarkerWaitFrames"/> and a locked one are REJECTED. After the press,
        /// a summary dialog is closed first; OK needs the recovery observed (by Guid or pid),
        /// <see cref="SettleFrames"/> since, no summary dialog, and a SPACECENTER with a
        /// game loaded.
        /// </summary>
        internal static KscRecoverPollAction DecidePoll(
            KscRecoverPhase phase, TestCommandScene scene, bool gameLoaded, int framesInPhase,
            bool markerFound, bool markerWired, bool markerLocked, bool recoverInteractable,
            bool recoveredObserved, int framesSinceRecovered, bool summaryDialogOpen,
            bool expired)
        {
            if (scene == TestCommandScene.MainMenu)
                return KscRecoverPollAction.ReturnedToMenu;

            switch (phase)
            {
                case KscRecoverPhase.FindingMarker:
                    if (expired) return KscRecoverPollAction.Timeout;
                    bool inKsc = scene == TestCommandScene.SpaceCenter && gameLoaded;
                    if (!inKsc || !markerFound || !markerWired)
                        return framesInPhase >= MarkerWaitFrames
                            ? KscRecoverPollAction.MarkerNotFound
                            : KscRecoverPollAction.NotYet;
                    if (markerLocked || !recoverInteractable)
                        return KscRecoverPollAction.ButtonLocked;
                    return KscRecoverPollAction.PressRecover;

                case KscRecoverPhase.Recovering:
                    if (summaryDialogOpen) return KscRecoverPollAction.DismissSummary;
                    if (!recoveredObserved)
                        return expired || framesInPhase >= RecoverWaitFrames
                            ? KscRecoverPollAction.NotRecovered
                            : KscRecoverPollAction.NotYet;
                    if (expired) return KscRecoverPollAction.Timeout;
                    if (scene != TestCommandScene.SpaceCenter || !gameLoaded)
                        return KscRecoverPollAction.NotYet;
                    return framesSinceRecovered >= SettleFrames
                        ? KscRecoverPollAction.Ok
                        : KscRecoverPollAction.NotYet;
            }
            return KscRecoverPollAction.NotYet;
        }

        /// <summary>The OK payload: <c>pid= vessel= scene= recovered=true quick=</c>.</summary>
        internal static List<KeyValuePair<string, string>> BuildOkPayload(
            uint pid, string vesselName, string sceneName, bool quick)
        {
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("pid", pid.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("vessel", vesselName ?? string.Empty),
                new KeyValuePair<string, string>("scene", sceneName ?? string.Empty),
                new KeyValuePair<string, string>("recovered", "true"),
                new KeyValuePair<string, string>("quick", quick ? "true" : "false"),
            };
        }

        /// <summary>The grep-stable Recover-press line.</summary>
        internal static string FormatPressedLine(
            uint pid, string vesselName, string situation, int markers, int framesWaited, double ut)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "kscrecover pressed pid=" + pid.ToString(ic)
                + " vessel=" + (vesselName ?? string.Empty)
                + " situation=" + (situation ?? string.Empty)
                + " markers=" + markers.ToString(ic)
                + " framesWaited=" + framesWaited.ToString(ic)
                + " ut=" + ut.ToString("F1", ic);
        }

        /// <summary>The grep-stable recovery read-back line.</summary>
        internal static string FormatObservedLine(uint pid, string vesselName, bool quick)
        {
            return "kscrecover observed onVesselRecovered pid=" + pid.ToString(CultureInfo.InvariantCulture)
                + " vessel=" + (vesselName ?? string.Empty)
                + " quick=" + (quick ? "true" : "false");
        }

        /// <summary>The grep-stable completion line.</summary>
        internal static string FormatCompleteLine(
            uint pid, string vesselName, string sceneName, bool quick, double elapsed)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "kscrecover complete pid=" + pid.ToString(ic)
                + " vessel=" + (vesselName ?? string.Empty)
                + " scene=" + (sceneName ?? string.Empty)
                + " quick=" + (quick ? "true" : "false")
                + " elapsed=" + elapsed.ToString("F1", ic) + "s";
        }

        /// <summary>The grep-stable refusal / failure line (<c>rejected</c> or <c>error</c>).</summary>
        internal static string FormatTerminalLine(
            bool rejected, string reason, uint pid, string detail, double elapsed)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "kscrecover " + (rejected ? "rejected" : "error")
                + " reason=" + (reason ?? string.Empty)
                + " pid=" + pid.ToString(ic)
                + (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail)
                + " elapsed=" + elapsed.ToString("F1", ic) + "s";
        }
    }
}
