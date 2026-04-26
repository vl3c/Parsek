using System.Collections.Generic;
using ClickThroughFix;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Real Spawn Control window extracted from ParsekUI.
    /// Shows nearby spawn candidates with sort, warp, and departure controls.
    /// </summary>
    internal class SpawnControlUI
    {
        private readonly ParsekUI parentUI;

        // Spawn Control window
        private bool showSpawnControlWindow;
        private Rect spawnControlWindowRect;
        private bool spawnControlWindowHasInputLock;
        private const string SpawnControlInputLockId = "Parsek_SpawnControlWindow";

        // Spawn Control sort state
        private SpawnControlSortColumn spawnSortColumn = SpawnControlSortColumn.Distance;
        private bool spawnSortAscending = true;
        private bool isResizingSpawnControlWindow;
        private Vector2 spawnControlScrollPos;

        // Cached sorted candidate list -- re-sorted only when source data or sort state changes
        private List<NearbySpawnCandidate> cachedSortedCandidates = new List<NearbySpawnCandidate>();
        private int cachedCandidateCount = -1;
        private int cachedProximityGeneration = -1;
        private SpawnControlSortColumn cachedSortColumn = SpawnControlSortColumn.Distance;
        private bool cachedSortAscending = true;

        // Window drag tracking for position logging
        private Rect lastSpawnControlWindowRect;

        // Spawn Control column widths (matches recordings window style)
        private const float SpawnColW_Name = 0f;    // expand
        private const float SpawnColW_Dist = 55f;
        private const float SpawnColW_RelSpeed = 70f;
        private const float SpawnColW_SpawnTime = 100f;
        private const float SpawnColW_Countdown = 95f;
        private const float SpawnColW_State = 110f;
        private const float SpawnColW_Warp = 85f;

        private const float SpacingSmall = 3f;
        private const float MinWindowWidth = 350f;
        private const float MinWindowHeight = 150f;

        public bool IsOpen
        {
            get { return showSpawnControlWindow; }
            set { showSpawnControlWindow = value; }
        }

        internal SpawnControlUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
        }

        internal static string ResolveAutoCloseReason(
            bool inFlight,
            bool hasFlight,
            int candidateCount)
        {
            if (!inFlight)
                return "not-in-flight";
            if (!hasFlight)
                return "flight-null";
            if (candidateCount <= 0)
                return "zero-candidates";
            return null;
        }

        internal static string FormatAutoCloseSummary(string reason, int candidateCount)
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "Real Spawn Control auto-close: reason={0} candidates={1}",
                string.IsNullOrEmpty(reason) ? "none" : reason,
                candidateCount);
        }

        private static void LogAutoClose(string reason, int candidateCount)
        {
            if (string.IsNullOrEmpty(reason))
                return;

            ParsekLog.VerboseOnChange("UI",
                "spawn-control-auto-close",
                reason,
                FormatAutoCloseSummary(reason, candidateCount));
        }

        public void DrawIfOpen(Rect mainWindowRect, ParsekFlight flight, bool inFlight)
        {
            if (!showSpawnControlWindow)
            {
                ReleaseInputLock();
                return;
            }

            // Auto-close when no nearby candidates
            int candidateCount = flight?.NearbySpawnCandidates?.Count ?? 0;
            string autoCloseReason = ResolveAutoCloseReason(
                inFlight,
                flight != null,
                candidateCount);
            if (!string.IsNullOrEmpty(autoCloseReason))
            {
                LogAutoClose(autoCloseReason, candidateCount);
                showSpawnControlWindow = false;
                ReleaseInputLock();
                return;
            }

            if (spawnControlWindowRect.width < 1f)
            {
                float x = mainWindowRect.x + mainWindowRect.width + 10;
                spawnControlWindowRect = new Rect(x, mainWindowRect.y, 750, 200);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.Verbose("UI",
                    $"Real Spawn Control window initial position: x={spawnControlWindowRect.x.ToString("F0", ic)} y={spawnControlWindowRect.y.ToString("F0", ic)}");
            }

            ParsekUI.HandleResizeDrag(ref spawnControlWindowRect, ref isResizingSpawnControlWindow,
                MinWindowWidth, MinWindowHeight, "Real Spawn Control window");

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            if (opaqueWindowStyle == null)
                return;
            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                spawnControlWindowRect = ClickThruBlocker.GUILayoutWindow(
                    "ParsekSpawnControl".GetHashCode(),
                    spawnControlWindowRect,
                    (id) => DrawSpawnControlWindow(id, flight),
                    "Parsek - Real Spawn Control",
                    opaqueWindowStyle,
                    GUILayout.Width(spawnControlWindowRect.width),
                    GUILayout.Height(spawnControlWindowRect.height)
                );
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }
            parentUI.LogWindowPosition("SpawnControl", ref lastSpawnControlWindowRect, spawnControlWindowRect);

            if (spawnControlWindowRect.Contains(Event.current.mousePosition))
            {
                if (!spawnControlWindowHasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, SpawnControlInputLockId);
                    spawnControlWindowHasInputLock = true;
                }
            }
            else
            {
                ReleaseInputLock();
            }
        }

        public void ReleaseInputLock()
        {
            if (!spawnControlWindowHasInputLock) return;
            InputLockManager.RemoveControlLock(SpawnControlInputLockId);
            spawnControlWindowHasInputLock = false;
        }

        private void DrawSpawnSortableHeader(string label, SpawnControlSortColumn col, float width)
        {
            DrawSpawnSortableHeader(label, col, false, width);
        }

        private void DrawSpawnSortableHeader(string label, SpawnControlSortColumn col, bool expand)
        {
            DrawSpawnSortableHeader(label, col, expand, 0);
        }

        private void DrawSpawnSortableHeader(string label, SpawnControlSortColumn col, bool expand, float width)
        {
            parentUI.DrawSortableHeaderCore(label, col, ref spawnSortColumn, ref spawnSortAscending, width, expand, () =>
            {
                ParsekLog.Verbose("UI", $"Spawn sort changed: column={spawnSortColumn}, ascending={spawnSortAscending}");
            });
        }

        private void DrawSpawnControlWindow(int windowID, ParsekFlight flight)
        {
            // Breathing room below the title bar — matches Timeline's visual spacing.
            GUILayout.Space(5);

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            double currentUT = Planetarium.GetUniversalTime();
            var candidates = flight.NearbySpawnCandidates;

            if (candidates.Count == 0)
            {
                GUILayout.Label("No nearby craft to spawn.");
                if (GUILayout.Button("Close"))
                    showSpawnControlWindow = false;
                GUI.DragWindow();
                return;
            }

            // Header row with sortable columns
            GUILayout.BeginHorizontal();
            DrawSpawnSortableHeader("Craft", SpawnControlSortColumn.Name, true);
            DrawSpawnSortableHeader("Dist", SpawnControlSortColumn.Distance, SpawnColW_Dist);
            DrawSpawnSortableHeader("Rel Speed", SpawnControlSortColumn.RelativeSpeed, SpawnColW_RelSpeed);
            DrawSpawnSortableHeader("Spawns at", SpawnControlSortColumn.SpawnTime, SpawnColW_SpawnTime);
            DrawSpawnSortableHeader("In T-", SpawnControlSortColumn.SpawnTime, SpawnColW_Countdown);
            GUILayout.Label("State", parentUI.GetColumnHeaderStyle(), GUILayout.Width(SpawnColW_State));
            GUILayout.Label("", GUILayout.Width(SpawnColW_Warp));
            GUILayout.EndHorizontal();

            // Re-sort when candidate list, sort state, or departure info changes
            int gen = flight.ProximityCheckGeneration;
            if (candidates.Count != cachedCandidateCount
                || gen != cachedProximityGeneration
                || spawnSortColumn != cachedSortColumn
                || spawnSortAscending != cachedSortAscending)
            {
                cachedSortedCandidates = SpawnControlPresentation.SortCandidates(
                    candidates,
                    spawnSortColumn,
                    spawnSortAscending);
                cachedCandidateCount = candidates.Count;
                cachedProximityGeneration = gen;
                cachedSortColumn = spawnSortColumn;
                cachedSortAscending = spawnSortAscending;
            }
            var sorted = cachedSortedCandidates;

            DrawSpawnCandidateRows(sorted, currentUT, ic, flight);

            DrawSpawnControlBottomBar(candidates, currentUT, flight);
        }

        private void DrawSpawnCandidateRows(List<NearbySpawnCandidate> sorted,
            double currentUT, System.Globalization.CultureInfo ic, ParsekFlight flight)
        {
            spawnControlScrollPos = GUILayout.BeginScrollView(spawnControlScrollPos, GUILayout.ExpandHeight(true));
            // Dark list-area background (matches Career State / Recordings body look).
            GUILayout.BeginVertical(GUI.skin.box);
            for (int i = 0; i < sorted.Count; i++)
            {
                var cand = sorted[i];
                double delta = cand.endUT - currentUT;
                SpawnCandidateRowPresentation row =
                    SpawnControlPresentation.BuildRowPresentation(
                        cand, currentUT,
                        ParsekFlight.NearbySpawnRadius,
                        ParsekFlight.MaxRelativeSpeed);

                GUILayout.BeginHorizontal();
                GUILayout.Label(cand.vesselName, GUILayout.ExpandWidth(true));

                // Distance + Rel Speed share a green tint when both gates pass (FF button enable
                // preconditions) so the user can read the window at a glance: green = warpable.
                Color savedColor = GUI.contentColor;
                if (row.ConditionsMet)
                    GUI.contentColor = new Color(0.55f, 1f, 0.55f);
                GUILayout.Label(
                    string.Format(ic, "{0:F0}m", cand.distance),
                    GUILayout.Width(SpawnColW_Dist));
                GUILayout.Label(
                    SpawnControlPresentation.FormatRelativeSpeed(cand.relativeSpeed, ic),
                    GUILayout.Width(SpawnColW_RelSpeed));
                GUI.contentColor = savedColor;

                GUILayout.Label(
                    KSPUtil.PrintDateCompact(cand.endUT, true),
                    GUILayout.Width(SpawnColW_SpawnTime));
                GUILayout.Label(
                    SelectiveSpawnUI.FormatCountdown(delta),
                    GUILayout.Width(SpawnColW_Countdown));

                // State column: departure info
                if (row.StateTone != SpawnCandidateStateTone.None)
                {
                    var prevColor = GUI.contentColor;
                    GUI.contentColor = row.StateTone == SpawnCandidateStateTone.DepartingNow
                        ? new Color(1f, 0.65f, 0.2f) // orange
                        : new Color(1f, 1f, 0.4f);    // yellow
                    GUILayout.Label(row.StateText, GUILayout.Width(SpawnColW_State));
                    GUI.contentColor = prevColor;
                }
                else
                {
                    GUILayout.Label("", GUILayout.Width(SpawnColW_State));
                }

                // Warp button: "FF-Depart" for departing, "FF-Spawn" for normal
                GUI.enabled = row.WarpButtonEnabled;
                if (GUILayout.Button(row.WarpButtonLabel, GUILayout.Width(SpawnColW_Warp)))
                {
                    if (row.UsesDepartureWarp)
                    {
                        ParsekLog.Info("UI",
                            string.Format(ic,
                                "Real Spawn Control: warp to departure '{0}' recording #{1} depUT={2:F1}",
                                cand.vesselName, cand.recordingIndex, cand.departureUT));
                        flight.WarpToDeparture(cand.recordingIndex, cand.departureUT);
                    }
                    else
                    {
                        ParsekLog.Info("UI",
                            string.Format(ic,
                                "Real Spawn Control: warp to '{0}' recording #{1}",
                                cand.vesselName, cand.recordingIndex));
                        flight.WarpToRecordingEnd(cand.recordingIndex);
                    }
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
            GUILayout.EndScrollView();
        }

        private void DrawSpawnControlBottomBar(List<NearbySpawnCandidate> candidates,
            double currentUT, ParsekFlight flight)
        {
            // Bottom section -- pinned to window bottom
            GUILayout.FlexibleSpace();

            GUILayout.BeginHorizontal();
            var next = SelectiveSpawnUI.FindNextSpawnCandidate(candidates, currentUT, ParsekFlight.NearbySpawnRadius, ParsekFlight.MaxRelativeSpeed);
            GUI.enabled = next != null;
            string tooltip = next != null
                ? SelectiveSpawnUI.FormatNextSpawnTooltip(next, currentUT) : "";
            if (GUILayout.Button(new GUIContent("Warp to Next Real Spawn", tooltip),
                GUILayout.ExpandWidth(true)))
            {
                ParsekLog.Info("UI", "Real Spawn Control: Warp to Next Real Spawn clicked");
                flight.WarpToNextCraftSpawn();
            }
            GUI.enabled = true;
            if (GUILayout.Button("Close", GUILayout.Width(132)))
            {
                showSpawnControlWindow = false;
                ParsekLog.Verbose("UI", "Real Spawn Control window closed");
            }
            GUILayout.EndHorizontal();

            string guiTooltip = GUI.tooltip ?? "";
            if (guiTooltip.Length > 0)
            {
                GUILayout.Space(SpacingSmall);
                GUILayout.Label(guiTooltip, GUI.skin.box);
            }
            else
            {
                GUILayout.Label("", GUILayout.Height(0));
            }

            ParsekUI.DrawResizeHandle(spawnControlWindowRect, ref isResizingSpawnControlWindow,
                "Real Spawn Control window");

            GUI.DragWindow();
        }
    }
}
