using System.Collections.Generic;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // The interact column and the mission action buttons: Re-Fly, Go To, Watch, Rewind / Forward, the collapse caret, Watch and Warp To with their disabled reasons, the warp confirmation and the watch target resolution.
    internal partial class MissionsWindowUI
    {
        // ---- the Interact column's cells ----
        //
        // Each draws ONE margin-0 container of exactly ColW_Interact (the header's cell is the
        // same width with the same zero horizontal margin, so the column's right edge and the
        // data columns left of it line up with the header), the inset, its buttons, and a
        // FlexibleSpace. A row draws exactly one of them.

        private void BeginInteractCell()
        {
            GUILayout.BeginHorizontal(GUILayout.Width(ColW_Interact));
            GUILayout.Space(InteractCellInset);
        }

        private static void EndInteractCell()
        {
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void DrawInteractBlank()
        {
            BeginInteractCell();
            EndInteractCell();
        }

        // Fly / Stash + Seal for an unfinished-flight recording, drawn by the Recordings tab's
        // own Re-Fly cell at the Interact geometry: the pair spans one single button width.
        private void DrawInteractReFly(Recording rec, int committedIndex)
        {
            BeginInteractCell();
            ParsekWindowSet.Of(parentUI).GetRecordingsTableUI().DrawReFlyColumnCell(
                rec, committedIndex, Planetarium.GetUniversalTime(), InteractButtonWidth, 0f);
            EndInteractCell();
        }

        // The Docked partner row's "Go to": opens the Missions tab on the partner's mission
        // through the same call the Timeline's GoTo makes (the Recordings window opens itself on
        // the Missions tab and delegates the scroll to RevealMissionForRecording, which only
        // QUEUES the expand of a collapsed target, so a mid-frame click is safe). Blank when there is
        // nothing to go to.
        private void DrawInteractGoTo(Mission mission, string goToRecordingId,
            string partnerMission, string partnerText, string branchPointId)
        {
            BeginInteractCell();
            if (!string.IsNullOrEmpty(goToRecordingId)
                && GUILayout.Button(
                    new GUIContent("Go to",
                        MissionPresentation.BuildPartnerGoToTooltip(partnerMission, partnerText)),
                    interactButtonStyle, GUILayout.Width(InteractButtonWidth)))
            {
                ParsekLog.Info("UI",
                    $"Mission partner GoTo: mission='{mission?.Name}' " +
                    $"recording={goToRecordingId} partnerMission='{partnerMission ?? "<unresolved>"}' " +
                    $"bp={branchPointId ?? "<none>"}");
                ParsekWindowSet.Of(parentUI).GetRecordingsTableUI().ShowMissionForRecording(goToRecordingId);
            }
            EndInteractCell();
        }

        // A mission's first line: Watch alone, at the single width.
        private void DrawInteractWatch(Mission mission, MissionThroughLineView view)
        {
            BeginInteractCell();
            DrawMissionWatchButton(mission, view, InteractButtonWidth, interactButtonStyle);
            EndInteractCell();
        }

        // A mission's second line: Rewind or Forward at the single width, under Watch, when one
        // applies to the mission's root (launch) recording. When neither applies the same
        // button-sized rect is reserved (GetRect draws nothing and takes no control id), so the
        // line keeps its height and the column its alignment in every state.
        // [ERS-exempt] reason: the rewind/forward path is keyed on the RAW committed index (it
        // takes a committed index + recording and resolves the rewind owner / save by
        // identity), not the ERS index; same rationale as the watch button.
        private void DrawInteractRewindForward(MissionThroughLineView view)
        {
            var rewindCommitted = RecordingStore.CommittedRecordings;
            int rootIdx = ResolveMissionRootRecordingIndex(view, rewindCommitted);
            Recording rootRec = rootIdx >= 0 ? rewindCommitted[rootIdx] : null;
            double now = Planetarium.GetUniversalTime();
            RecordingsTableUI table = ParsekWindowSet.Of(parentUI).GetRecordingsTableUI();

            BeginInteractCell();
            if (table.MissionRewindForwardVisible(rootRec, now))
                table.DrawMissionRewindForwardButton(rootRec, rootIdx, now, parentUI.Flight,
                    InteractButtonWidth, interactButtonStyle);
            else
                GUILayoutUtility.GetRect(InteractReservedSlotContent, interactButtonStyle,
                    GUILayout.Width(InteractButtonWidth));
            EndInteractCell();
        }

        // Line 2 of the # column: the mission's collapse caret, a clickable glyph under the index
        // number (down while the mission's rows show, right while they are hidden; the hover says
        // which). `collapsed` is the value the draw loop latched before the bar drew, so the
        // glyph matches the rows this pass draws; the click writes Mission.Collapsed and takes
        // effect next frame. Drawn in both modes and in every state, so the control count is the
        // same on the Layout and Repaint passes.
        private void DrawMissionCollapseCaret(Mission mission, bool collapsed)
        {
            GUIContent content = collapsed ? MissionCaretCollapsedContent : MissionCaretExpandedContent;
            if (GUILayout.Button(content, missionCaretStyle,
                    GUILayout.Width(ColW_Index), GUILayout.MinHeight(CompositionRowMinHeight)))
            {
                bool nowCollapsed = !collapsed;
                mission.Collapsed = nowCollapsed;
                ParsekLog.Info("Mission", $"Mission '{mission.Name}' collapsed={nowCollapsed}");
            }
        }

        // Watch button: follows the whole mission (its currently-watchable member ghost), in
        // flight only. Mirrors the recordings-window Watch button but mission-scoped - it picks
        // the mission's live member and enters watch on it; for a looping mission the engine's
        // unit handoff then carries the camera across stages as the shared clock advances. "W*"
        // when already watching one of this mission's members.
        private void DrawMissionWatchButton(Mission mission, MissionThroughLineView view,
            float width, GUIStyle style)
        {
            if (!parentUI.InFlightMode || parentUI.Flight == null)
            {
                // Keep the column width stable when not in flight (greyed placeholder).
                GUI.enabled = false;
                GUILayout.Button("Watch", style, GUILayout.Width(width));
                DisabledHoverEcho.CarryLastControl(false, MissionWatchDisabledReason(false, false));
                GUI.enabled = true;
                return;
            }

            var flight = parentUI.Flight;
            // [ERS-exempt] reason: the watch path feeds ghost-engine APIs (HasActiveGhost /
            // IsGhostOnSameBody / IsGhostWithinVisualRange / WatchedRecordingIndex /
            // EnterWatchMode) that are keyed on the RAW committed index, not the ERS index
            // (which shifts after a Re-Fly supersede). Same rationale as TimelineWindowUI.
            var committed = RecordingStore.CommittedRecordings;
            int watchTarget = ResolveMissionWatchTarget(mission, view, committed, flight,
                out bool isWatchingThisMission);

            bool canWatch = watchTarget >= 0 || isWatchingThisMission;
            GUI.enabled = canWatch;
            string label = isWatchingThisMission ? "W*" : "Watch";
            bool missionWatchClicked = GUILayout.Button(label, style, GUILayout.Width(width));
            DisabledHoverEcho.CarryLastControl(canWatch, MissionWatchDisabledReason(true, canWatch));
            if (missionWatchClicked)
            {
                if (isWatchingThisMission)
                {
                    flight.ExitWatchMode();
                    ParsekLog.Info("Mission", $"Watch exited for mission '{mission.Name}'");
                }
                else if (watchTarget >= 0)
                {
                    flight.EnterWatchMode(watchTarget);
                    ParsekLog.Info("Mission",
                        $"Watch entered for mission '{mission.Name}' on member recording #{watchTarget}");
                }
            }
            GUI.enabled = true;
        }

        /// <summary>
        /// Why a mission's "Delete" is greyed out - the REAL reason, per mission. The
        /// previous version took no arguments and returned the tree-original sentence as a
        /// constant, so it could not distinguish the three cases
        /// <see cref="MissionStore.CanDelete"/> refuses for, and it kept talking when the
        /// control was live (the one reason function in the mod that did, which is why it
        /// was absent from `DisabledHoverEchoTests`' silence check). Pure for unit testing
        /// apart from the store read the classifier performs.
        /// </summary>
        internal static string MissionDeleteDisabledReason(Mission mission)
        {
            return MissionDeleteDisabledReason(MissionStore.ClassifyDeleteRefusal(mission));
        }

        /// <summary>
        /// The wording half of <see cref="MissionDeleteDisabledReason(Mission)"/>, split out
        /// so every refusal's text is unit-testable without standing up the mission store.
        /// Pure.
        /// </summary>
        internal static string MissionDeleteDisabledReason(MissionStore.MissionDeleteRefusal refusal)
        {
            switch (refusal)
            {
                case MissionStore.MissionDeleteRefusal.None:
                    return string.Empty;
                case MissionStore.MissionDeleteRefusal.NoMission:
                    return "There is no mission selected to delete";
                case MissionStore.MissionDeleteRefusal.NotInStore:
                    return "This mission is no longer in the mission list";
                default:
                    // Every tree keeps its ORIGINAL mission - it is what the recordings hang
                    // off - so only missions cloned onto the same tree can be removed.
                    return "A flight always keeps its first mission";
            }
        }

        /// <summary>
        /// Why a mission's "Watch" is greyed out. Watching parents the camera to a live
        /// ghost, so it needs a flight scene AND a ghost of this mission actually flying
        /// at the current moment. Pure for unit testing.
        /// </summary>
        internal static string MissionWatchDisabledReason(bool inFlightScene, bool canWatch)
        {
            if (!inFlightScene)
                return "Watching only works while you are flying";
            return canWatch ? string.Empty : "Nothing from this mission is flying right now";
        }

        /// <summary>
        /// Why a mission's "Warp to..." is greyed out. The button draws in every Advanced
        /// state (owner request 2026-10-01), so a mission that does not loop greys it too: the
        /// Loop toggle sits right under it, so "turn Loop on" is a fix the player can make,
        /// except on a route-bound tree, where a supply route owns the repeats and the toggle
        /// is gone. The button's own tooltip describes none of the gates, and they are reported
        /// in the order the player would have to fix them. Pure for unit testing.
        /// </summary>
        internal static string MissionWarpToDisabledReason(
            bool warpScene, bool looping, bool routeBound, bool unitBuilt, bool relaunchAhead)
        {
            if (!warpScene)
                return "Warping works in flight or at the Space Center";
            if (!looping)
                return routeBound
                    ? "A supply route repeats this mission, not Loop"
                    : "Turn Loop on to warp to the next launch";
            if (!unitBuilt)
                return "This mission does not repeat on a schedule yet";
            if (!relaunchAhead)
                return "The next launch is not ahead of you";
            return string.Empty;
        }

        // "Warp to..." button: fast-forwards the game clock to this mission's next faithful relaunch
        // (periodicity.NextRelaunchUT, the same UT the "Next launch" countdown targets) using the SAME
        // mechanism as the Recordings/Missions "Forward" button - an IN-PLACE forward jump (no scene
        // change) that lands RewindToLaunchLeadTimeSeconds (15 s) before the launch. In flight it
        // goes through ParsekFlight.FastForwardToEventUT (notifies the recorder, then
        // TimeJumpManager.ExecuteForwardJump); at the Space Center it calls ExecuteForwardJump
        // directly. A confirmation dialog precedes the jump (matching the Forward button). Drawn in
        // every Advanced state (the caller's gate); greyed unless the mission loops, an engine unit
        // was built, the next relaunch is in the future, and we are in a warp-capable scene
        // (flight or Space Center).
        private void DrawMissionWarpToWindowButton(Mission mission, MissionPeriodicityDisplay periodicity)
        {
            bool inFlight = parentUI.InFlightMode;
            bool warpScene = inFlight || HighLogic.LoadedScene == GameScenes.SPACECENTER;
            bool looping = mission != null && mission.LoopPlayback;
            bool routeBound = mission != null
                && RouteTreeGuard.RouteBindingForMission(mission, RecordingStore.CommittedTrees, out _);

            double now = periodicity.Solved ? periodicity.NowUT : Planetarium.GetUniversalTime();
            bool actionable = warpScene && ShouldEnableWarpToWindow(
                looping,
                periodicity.UnitBuilt,
                periodicity.NextRelaunchUT,
                now);
            // Reuse the ENABLE gate itself for the clock leg, with the two upstream legs pinned
            // true so only its clock + finiteness checks answer here. Re-deriving it as
            // `NextRelaunchUT > now` left the button greyed but SILENT through the last second
            // before every relaunch (the gate needs now + 1.0) and for a NaN/Inf relaunch UT.
            string disabledReason = MissionWarpToDisabledReason(
                warpScene, looping, routeBound, periodicity.UnitBuilt,
                ShouldEnableWarpToWindow(true, true, periodicity.NextRelaunchUT, now));
            LogMissionWarpStateTransition(mission, actionable, disabledReason);

            GUI.enabled = actionable;
            bool warpToClicked = GUILayout.Button(
                new GUIContent("Warp to...", MissionPresentation.WarpToButtonTooltip),
                loopGridButtonStyle, GUILayout.Width(LoopGridColumnBWidth));
            // WarpToButtonTooltip is the generic what-it-does sentence and stays attached
            // while the button is greyed, so the carrier resolves which gate actually closed.
            DisabledHoverEcho.CarryLastControl(actionable, disabledReason);
            if (warpToClicked)
                ShowMissionWarpToWindowConfirmation(mission, periodicity.NextRelaunchUT, inFlight);
            GUI.enabled = true;
        }

        private void LogMissionWarpStateTransition(Mission mission, bool actionable, string disabledReason)
        {
            if (mission == null || string.IsNullOrEmpty(mission.Id))
                return;
            string state = DescribeMissionWarpState(actionable, disabledReason);
            if (lastMissionWarpState.TryGetValue(mission.Id, out string previous)
                && string.Equals(previous, state, System.StringComparison.Ordinal))
                return;
            lastMissionWarpState[mission.Id] = state;
            ParsekLog.Verbose("Mission",
                $"Warp to... for '{mission.Name}' ({mission.Id}): {previous ?? "<first draw>"} -> {state}");
        }

        /// <summary>
        /// The "Warp to..." state the transition log keys on: "enabled", or "greyed: reason"
        /// (a greyed button always has a reason; the reason test pins that). Pure for unit
        /// testing.
        /// </summary>
        internal static string DescribeMissionWarpState(bool actionable, string disabledReason)
        {
            if (actionable)
                return "enabled";
            return "greyed: " + (string.IsNullOrEmpty(disabledReason) ? "<no reason>" : disabledReason);
        }

        // Confirmation dialog for the "Warp to..." button, then the in-place forward jump (the same
        // FF mechanism as the Forward button - NO scene change). On confirm: in flight ->
        // ParsekFlight.FastForwardToEventUT; at the Space Center -> a direct ExecuteForwardJump to
        // RewindToLaunchLeadTimeSeconds before the relaunch. now/lead are recomputed at confirm time.
        private void ShowMissionWarpToWindowConfirmation(Mission mission, double relaunchUT, bool inFlight)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            string missionName = mission != null ? mission.Name : "mission";
            string label = string.Format(ic, "next launch of \"{0}\"", missionName);
            double previewNow = Planetarium.GetUniversalTime();
            double previewDelta = TimeJumpManager.ApplyJumpLead(relaunchUT, previewNow) - previewNow;
            string message = string.Format(ic,
                "Fast-forward to just before \"{0}\" next launch at {1}?\n\nTime will advance by {2}.",
                missionName, KSPUtil.PrintDateCompact(relaunchUT, true),
                ParsekTimeFormat.FormatDurationFull(previewDelta));

            var flight = parentUI.Flight;
            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekMissionWarpToWindowConfirm",
                    message,
                    "Confirm: Warp to Launch",
                    HighLogic.UISkin,
                    new DialogGUIButton("Warp", () =>
                    {
                        ParsekLog.Info("Mission", string.Format(ic,
                            "Warp to next window confirmed for '{0}': relaunchUT={1} inFlight={2}",
                            missionName, relaunchUT.ToString("R", ic), inFlight));
                        if (inFlight && flight != null)
                        {
                            flight.FastForwardToEventUT(relaunchUT, label);
                        }
                        else
                        {
                            // Space Center: advance the clock directly (no recorder notify needed).
                            double curUT = Planetarium.GetUniversalTime();
                            double target = TimeJumpManager.ApplyJumpLead(relaunchUT, curUT);
                            if (!TimeJumpManager.IsValidJump(curUT, target))
                            {
                                ParsekLog.Warn("Mission", string.Format(ic,
                                    "Warp to next window aborted (invalid jump): current={0:F1} target={1:F1} for {2}",
                                    curUT, target, label));
                                return;
                            }
                            ParsekLog.Info("Mission", string.Format(ic,
                                "Warp to next window (KSC): jumping to UT={0:F1} for {1} (delta={2:F1}s, relaunchUT={3:F1})",
                                target, label, target - curUT, relaunchUT));
                            TimeJumpManager.ExecuteForwardJump(target);
                            ParsekLog.ScreenMessage(string.Format(ic,
                                "Fast-forwarded to {0} ({1:F0}s)", label, target - curUT), 3f);
                        }
                    }),
                    new DialogGUIButton("Cancel", () =>
                        ParsekLog.Info("Mission", "Warp to next window cancelled"))
                ),
                false, HighLogic.UISkin);
        }

        /// <summary>
        /// Whether the per-mission "Warp to..." button is actionable: the mission must be looping,
        /// have a built engine loop unit, and have a next relaunch UT that is finite and strictly in
        /// the future (more than a second ahead, so an at-or-behind-now relaunch does not offer a
        /// no-op warp). Pure (the scene gate is applied by the caller). The actual warp is the same
        /// in-place forward fast-forward the Forward button uses.
        /// </summary>
        internal static bool ShouldEnableWarpToWindow(
            bool looping, bool unitBuilt, double nextRelaunchUT, double nowUT)
        {
            return looping
                && unitBuilt
                && !double.IsNaN(nextRelaunchUT)
                && !double.IsInfinity(nextRelaunchUT)
                && nextRelaunchUT > nowUT + 1.0;
        }

        // Picks the committed recording index to watch for a mission: the first watchable member
        // (active ghost, same body, in visual range) among the mission's included through-line
        // legs. Also reports whether the currently-watched recording is one of this mission's
        // members (for the "W*" toggle / exit). Returns -1 when nothing is watchable.
        private int ResolveMissionWatchTarget(Mission mission, MissionThroughLineView view,
            IReadOnlyList<Recording> committed, ParsekFlight flight, out bool isWatchingThisMission)
        {
            isWatchingThisMission = false;
            if (committed == null || view == null || flight == null)
                return -1;
            RecordingTree tree = FindTree(RecordingStore.CommittedTrees, mission.TreeId);
            if (tree == null)
                return -1;

            // The TRIMMED loop members keyed by committed index - the SAME set
            // MissionLoopUnitBuilder actually spawns - so a watch target (and the "watching this
            // mission" check) never picks a vessel that the interval-level trim dropped from the
            // loop.
            var windows = MissionLoopUnitBuilder.ComputeTrimmedMemberWindows(
                view, GetCompositionRoots(tree), committed, mission.ExcludedIntervalKeys,
                null, out _, out _);

            int watchedIdx = flight.WatchedRecordingIndex;
            int target = -1;
            for (int i = 0; i < committed.Count; i++)
            {
                if (!windows.ContainsKey(i))
                    continue;
                if (i == watchedIdx)
                    isWatchingThisMission = true;
                if (target < 0 && flight.HasActiveGhost(i) && flight.IsGhostOnSameBody(i)
                    && flight.IsGhostWithinVisualRange(i))
                    target = i;
            }
            return target;
        }

        // The committed index of the mission's ROOT (launch) recording = the earliest-StartUT
        // committed member across all of the mission's through-lines. Used by the header's
        // Rewind/Forward cell so it rewinds the game to the mission's launch (or fast-forwards
        // to it). The legacy R/FF path resolves the rewind owner / save by identity from there,
        // so passing the launch member is correct for both directions. Returns -1 when no
        // committed member is found (the R/FF cell then renders blank).
        private static int ResolveMissionRootRecordingIndex(
            MissionThroughLineView view, IReadOnlyList<Recording> committed)
        {
            if (view == null || committed == null)
                return -1;

            var memberIds = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var kv in view.ByHeadId)
            {
                MissionThroughLine tl = kv.Value;
                if (tl == null) continue;
                for (int m = 0; m < tl.MemberLegIds.Count; m++)
                    if (!string.IsNullOrEmpty(tl.MemberLegIds[m]))
                        memberIds.Add(tl.MemberLegIds[m]);
            }

            int best = -1;
            double bestStart = double.PositiveInfinity;
            for (int i = 0; i < committed.Count; i++)
            {
                Recording rec = committed[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId)
                    || !memberIds.Contains(rec.RecordingId))
                    continue;
                if (rec.StartUT < bestStart)
                {
                    bestStart = rec.StartUT;
                    best = i;
                }
            }
            return best;
        }
    }
}
