using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>Where a pressed <c>TrackingStationRecover</c> stands between polls.</summary>
    internal enum TsRecoverPhase
    {
        /// <summary>The building was clicked; waiting for a settled Tracking Station.</summary>
        EnteringTrackingStation,
        /// <summary>Selecting the vessel in the Tracking Station list (SetVessel).</summary>
        Selecting,
        /// <summary>The Recover button was pressed; waiting for stock's confirm popup.</summary>
        Confirming,
        /// <summary>Stock recovered the vessel; closing any summary and pressing Leave.</summary>
        Recovered,
        /// <summary>Leave was pressed; waiting for a settled Space Center.</summary>
        Leaving,
    }

    /// <summary>What one completion poll of a pressed <c>TrackingStationRecover</c> does.</summary>
    internal enum TsRecoverPollAction
    {
        NotYet,
        DismissIntro,
        IntroNotDismissed,
        SelectVessel,
        SelectFailed,
        PressRecover,
        ButtonLocked,
        AnswerConfirm,
        ConfirmNotFound,
        DismissSummary,
        PressLeave,
        PressLeaveForced,
        Ok,
        ReturnedToMenu,
        Timeout,
    }

    /// <summary>
    /// Pure half of the automation-only <c>TrackingStationRecover</c> seam verb: recover a
    /// (usually NON-active, unloaded) vessel through the STOCK Tracking Station Recover path,
    /// the way a player does it from the Space Center: click the Tracking Station building,
    /// select the vessel, press Recover, confirm the "Recover Vessel" popup, close the
    /// recovery summary, press Leave.
    ///
    /// <para>Stock order (decompiled KSP 1.12.5): <c>SpaceCenterBuilding.OnLeftClick</c>
    /// (damage under 70%) -> <c>EnterBuilding</c> -> <c>TrackingStationBuilding.OnClicked</c>,
    /// which saves <c>persistent</c> and loads TRACKSTATION when
    /// <c>Parameters.SpaceCenter.CanGoInTrackingStation</c> (otherwise it pops the
    /// "FacilityLocked" dialog). In the Tracking Station <c>SpaceTracking.SetVessel(v, true)</c>
    /// unlocks <c>RecoverButton</c> for a recoverable owned vessel when
    /// <c>Parameters.TrackingStation.CanAbortVessel</c> and the mission allows recovery; its
    /// listener <c>BtnOnclick_RecoverSelectedVessel</c> spawns the "Recover Vessel"
    /// <c>MultiOptionDialog</c>, whose confirm button runs <c>OnRecoverConfirm</c>:
    /// <c>GameEvents.onVesselRecovered.Fire(vessel.protoVessel, false)</c>, destroy, save.
    /// The bool is stock's <c>quick</c> flag (VesselRecovery reads it to skip the summary),
    /// NOT "from tracking station", and the TS path passes FALSE - so stock's
    /// <c>VesselRecovery.OnVesselRecovered</c> opens a <c>MissionRecoveryDialog</c> in any
    /// game with Funding / Reputation / R&amp;D or a crewed vessel. Leave is
    /// <c>SpaceTracking.LeaveBtn</c> -> <c>BtnOnClick_LeaveTrackingStation</c>, which saves and
    /// loads SPACECENTER.</para>
    ///
    /// <para>Grammar: <c>cmd=TrackingStationRecover pid=&lt;persistentId&gt;</c>, issued at
    /// the Space Center. TWO-PHASE, RequiresGameLoaded, 120 s budget (the Recover size).</para>
    /// </summary>
    internal static class TestCommandTrackingStationRecover
    {
        internal const string Verb = "TrackingStationRecover";
        internal const string PidKey = "pid";

        internal const string PidArgMissingReason = "tsrecover-pid-arg-missing";
        internal const string PidArgInvalidReason = "tsrecover-pid-arg-invalid";
        internal const string WrongSceneReason = "tsrecover-wrong-scene";
        internal const string VesselNotFoundReason = "tsrecover-vessel-not-found";
        internal const string TargetIsGhostReason = "tsrecover-target-is-ghost";
        internal const string NotRecoverableReason = "tsrecover-not-recoverable";
        internal const string BuildingNotFoundReason = "tsrecover-building-not-found";
        internal const string FacilityClosedReason = "tsrecover-facility-closed";
        // Decided in the Tracking Station, after the building click: a late REJECTED (the
        // PlantFlag flag-lock-stable precedent) because the click a seam would make next is
        // one stock would ignore.
        internal const string ButtonLockedReason = "tsrecover-button-locked";

        // Post-press ERROR terminals.
        internal const string SelectFailedReason = "tsrecover-select-failed";
        internal const string ConfirmNotFoundReason = "tsrecover-confirm-not-found";
        internal const string NotRecoveredReason = "tsrecover-not-recovered";
        internal const string ReturnedToMenuReason = "tsrecover-returned-to-menu";
        internal const string TimeoutReason = "tsrecover-timeout";
        internal const string IntroNotDismissedReason = "tsrecover-intro-not-dismissed";

        /// <summary>The REJECTED vocabulary, in gate order (button-locked last: it is the
        /// one refusal decided inside the Tracking Station). hlib.TSRECOVER_REASONS mirrors
        /// it (TrackingStationRecoverSourceSyncTests).</summary>
        internal static readonly string[] Reasons =
        {
            "tsrecover-pid-arg-missing",
            "tsrecover-pid-arg-invalid",
            "tsrecover-wrong-scene",
            "tsrecover-vessel-not-found",
            "tsrecover-target-is-ghost",
            "tsrecover-not-recoverable",
            "tsrecover-building-not-found",
            "tsrecover-facility-closed",
            "tsrecover-button-locked",
        };

        /// <summary>The post-press ERROR vocabulary.</summary>
        internal static readonly string[] ErrorReasons =
        {
            "tsrecover-select-failed",
            "tsrecover-confirm-not-found",
            "tsrecover-not-recovered",
            "tsrecover-returned-to-menu",
            "tsrecover-timeout",
            "tsrecover-intro-not-dismissed",
        };

        /// <summary>The stock confirm popup's <c>MultiOptionDialog</c> name.</summary>
        internal const string ConfirmDialogName = "Recover Vessel";

        /// <summary>The stock method the confirm button's callback runs first; the button is
        /// identified by it rather than by position or by its localized label.</summary>
        internal const string ConfirmCallbackMethodName = "OnRecoverConfirm";

        /// <summary>Stock's building click opens the repair menu, not the building, at or
        /// above this structural damage (<c>SpaceCenterBuilding.OnLeftClick</c>).</summary>
        internal const float BuildingDamageThreshold = 70f;

        /// <summary>Frames a fresh Tracking Station is left to build its vessel list and
        /// buttons before the seam selects.</summary>
        internal const int TrackingStationSettleFrames = 15;

        /// <summary>Frames the seam retries <c>SetVessel</c> (stock drops a second call in
        /// one frame) before it gives up.</summary>
        internal const int SelectRetryFrames = 30;

        /// <summary>Frames the seam waits for the confirm popup after the Recover press.</summary>
        internal const int ConfirmWaitFrames = 30;

        /// <summary>The control lock stock's new-game intro (<c>ScenarioNewGameIntro</c>, a
        /// sandbox/science-save tutorial) sets over <c>TRACKINGSTATION_ALL</c> on a save's
        /// first Tracking Station visit (<c>tsComplete = False</c>). It holds until the player
        /// presses the intro page's button, which sets <c>tsComplete</c>, closes the window
        /// and saves; while it holds, stock's <c>unlockUI</c> never re-enables Leave
        /// (decompiled KSP 1.12.5; CI-7 `2026-10-03_1548` measured it held from arrival).</summary>
        internal const string IntroLockId = "intro_TS";

        /// <summary>Frames past the settle the seam waits for the intro lock to clear after
        /// pressing the intro's button.</summary>
        internal const int IntroWaitFrames = 60;

        /// <summary>Frames the seam waits, after the recovery and with no summary dialog up,
        /// for stock to re-enable Leave. Stock's <c>unlockUI</c> runs from inside
        /// <c>OnRecoverConfirm</c> and re-enables Leave only while no control lock covers
        /// <c>TRACKINGSTATION_UI</c>; it re-runs only when such a lock is removed. Past this
        /// the seam names the locks and invokes the Leave button's click handler itself, and
        /// says so in its answer (<c>leaveForced=true</c>), so a lane can forbid it.</summary>
        internal const int LeaveWaitFrames = 60;

        /// <summary><c>ControlTypes.TRACKINGSTATION_UI</c> (decompiled KSP 1.12.5).</summary>
        internal const ulong TrackingStationUiLockBit = 0x400000000000UL;

        /// <summary>The ids of the control locks in <paramref name="lockStack"/> that cover
        /// the Tracking Station UI, sorted and comma-joined; <c>(none)</c> when there are
        /// none.</summary>
        internal static string DescribeTrackingStationLocks(
            IEnumerable<KeyValuePair<string, ulong>> lockStack)
        {
            var ids = new List<string>();
            if (lockStack != null)
            {
                foreach (var kv in lockStack)
                {
                    if ((kv.Value & TrackingStationUiLockBit) != 0UL)
                        ids.Add(string.IsNullOrEmpty(kv.Key) ? "(unnamed)" : kv.Key);
                }
            }
            if (ids.Count == 0) return "(none)";
            ids.Sort(StringComparer.Ordinal);
            return string.Join(",", ids.ToArray());
        }

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

        /// <summary>
        /// The pre-click gate, decided at the Space Center before any side effect: the
        /// scene, a real (non-ghost) recoverable vessel with that pid, and a Tracking Station
        /// building whose click would open the building (open in this game mode, allowed by
        /// the game parameters, damaged under the repair-menu threshold). Returns null to
        /// proceed.
        /// </summary>
        internal static string DecideGate(
            TestCommandScene scene, bool vesselFound, bool isGhost, bool isRecoverable,
            bool buildingFound, bool buildingOpen, bool canGoInTrackingStation, float damage)
        {
            if (scene != TestCommandScene.SpaceCenter) return WrongSceneReason;
            if (isGhost) return TargetIsGhostReason;
            if (!vesselFound) return VesselNotFoundReason;
            if (!isRecoverable) return NotRecoverableReason;
            if (!buildingFound) return BuildingNotFoundReason;
            if (!buildingOpen || !canGoInTrackingStation || damage >= BuildingDamageThreshold)
                return FacilityClosedReason;
            return null;
        }

        /// <summary>
        /// One completion poll. A MAINMENU scene is a fast failure in every phase and the
        /// budget is the catch-all; otherwise the phase decides. OK needs the recovery
        /// observed (phase Leaving is reached only after it) AND a settled Space Center with
        /// a game loaded.
        /// </summary>
        internal static TsRecoverPollAction DecidePoll(
            TsRecoverPhase phase, TestCommandScene scene, bool gameLoaded,
            bool trackingStationUp, int framesInPhase, bool vesselSelected,
            bool recoverButtonInteractable, bool confirmPopupFound,
            bool summaryDialogOpen, bool leaveButtonInteractable, bool expired,
            bool introLockHeld = false)
        {
            if (scene == TestCommandScene.MainMenu)
                return TsRecoverPollAction.ReturnedToMenu;
            if (expired)
                return TsRecoverPollAction.Timeout;

            bool inTs = scene == TestCommandScene.TrackingStation && gameLoaded && trackingStationUp;
            switch (phase)
            {
                case TsRecoverPhase.EnteringTrackingStation:
                    if (!inTs || framesInPhase < TrackingStationSettleFrames)
                        return TsRecoverPollAction.NotYet;
                    if (introLockHeld)
                        return framesInPhase >= TrackingStationSettleFrames + IntroWaitFrames
                            ? TsRecoverPollAction.IntroNotDismissed
                            : TsRecoverPollAction.DismissIntro;
                    return TsRecoverPollAction.SelectVessel;

                case TsRecoverPhase.Selecting:
                    if (!inTs) return TsRecoverPollAction.NotYet;
                    if (!vesselSelected)
                        return framesInPhase >= SelectRetryFrames
                            ? TsRecoverPollAction.SelectFailed
                            : TsRecoverPollAction.SelectVessel;
                    return recoverButtonInteractable
                        ? TsRecoverPollAction.PressRecover
                        : TsRecoverPollAction.ButtonLocked;

                case TsRecoverPhase.Confirming:
                    if (confirmPopupFound) return TsRecoverPollAction.AnswerConfirm;
                    return framesInPhase >= ConfirmWaitFrames
                        ? TsRecoverPollAction.ConfirmNotFound
                        : TsRecoverPollAction.NotYet;

                case TsRecoverPhase.Recovered:
                    if (summaryDialogOpen) return TsRecoverPollAction.DismissSummary;
                    if (!inTs) return TsRecoverPollAction.NotYet;
                    if (leaveButtonInteractable) return TsRecoverPollAction.PressLeave;
                    return framesInPhase >= LeaveWaitFrames
                        ? TsRecoverPollAction.PressLeaveForced
                        : TsRecoverPollAction.NotYet;

                case TsRecoverPhase.Leaving:
                    if (scene == TestCommandScene.SpaceCenter && gameLoaded)
                        return TsRecoverPollAction.Ok;
                    return TsRecoverPollAction.NotYet;
            }
            return TsRecoverPollAction.NotYet;
        }

        /// <summary>The OK payload: <c>pid= vessel= scene= recovered=true quick= leaveForced=</c>,
        /// plus <c>leaveLocks=</c> when the seam had to force the Leave press.</summary>
        internal static List<KeyValuePair<string, string>> BuildOkPayload(
            uint pid, string vesselName, string sceneName, bool quick,
            bool leaveForced = false, string leaveLocks = null)
        {
            var payload = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("pid", pid.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("vessel", vesselName ?? string.Empty),
                new KeyValuePair<string, string>("scene", sceneName ?? string.Empty),
                new KeyValuePair<string, string>("recovered", "true"),
                new KeyValuePair<string, string>("quick", quick ? "true" : "false"),
                new KeyValuePair<string, string>("leaveForced", leaveForced ? "true" : "false"),
            };
            if (leaveForced)
                payload.Add(new KeyValuePair<string, string>("leaveLocks", leaveLocks ?? "(none)"));
            return payload;
        }

        /// <summary>The grep-stable building-click line.</summary>
        internal static string FormatEnterLine(uint pid, string vesselName, string situation, double ut)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "tsrecover enter pid=" + pid.ToString(ic)
                + " vessel=" + (vesselName ?? string.Empty)
                + " situation=" + (situation ?? string.Empty)
                + " ut=" + ut.ToString("F1", ic);
        }

        /// <summary>The grep-stable Recover-press line.</summary>
        internal static string FormatPressedLine(uint pid, string vesselName, int framesInTs)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "tsrecover pressed pid=" + pid.ToString(ic)
                + " vessel=" + (vesselName ?? string.Empty)
                + " framesInTs=" + framesInTs.ToString(ic);
        }

        /// <summary>The grep-stable completion line.</summary>
        internal static string FormatCompleteLine(
            uint pid, string vesselName, string sceneName, bool quick, double elapsed,
            bool leaveForced = false)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "tsrecover complete pid=" + pid.ToString(ic)
                + " vessel=" + (vesselName ?? string.Empty)
                + " scene=" + (sceneName ?? string.Empty)
                + " quick=" + (quick ? "true" : "false")
                + " leaveForced=" + (leaveForced ? "true" : "false")
                + " elapsed=" + elapsed.ToString("F1", ic) + "s";
        }
    }
}
