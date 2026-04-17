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
        private enum SpawnSortColumn { Name, Distance, SpawnTime }
        private SpawnSortColumn spawnSortColumn = SpawnSortColumn.Distance;
        private bool spawnSortAscending = true;
        private bool isResizingSpawnControlWindow;
        private Vector2 spawnControlScrollPos;

        // Cached sorted candidate list -- re-sorted only when source data or sort state changes
        private List<NearbySpawnCandidate> cachedSortedCandidates = new List<NearbySpawnCandidate>();
        private int cachedCandidateCount = -1;
        private int cachedProximityGeneration = -1;
        private SpawnSortColumn cachedSortColumn = SpawnSortColumn.Distance;
        private bool cachedSortAscending = true;

        // Window drag tracking for position logging
        private Rect lastSpawnControlWindowRect;

        // Spawn Control column widths (matches recordings window style)
        private const float SpawnColW_Name = 0f;    // expand
        private const float SpawnColW_Dist = 55f;
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

        public void DrawIfOpen(Rect mainWindowRect, ParsekFlight flight, bool inFlight)
        {
            if (!showSpawnControlWindow)
            {
                ReleaseInputLock();
                return;
            }

            // Auto-close when no nearby candidates
            if (!inFlight || flight == null || flight.NearbySpawnCandidates.Count == 0)
            {
                showSpawnControlWindow = false;
                ReleaseInputLock();
                return;
            }

            if (spawnControlWindowRect.width < 1f)
            {
                float x = mainWindowRect.x + mainWindowRect.width + 10;
                spawnControlWindowRect = new Rect(x, mainWindowRect.y, 680, 200);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.Verbose("UI",
                    $"Real Spawn Control window initial position: x={spawnControlWindowRect.x.ToString("F0", ic)} y={spawnControlWindowRect.y.ToString("F0", ic)}");
            }

            ParsekUI.HandleResizeDrag(ref spawnControlWindowRect, ref isResizingSpawnControlWindow,
                MinWindowWidth, MinWindowHeight, "Real Spawn Control window");

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            spawnControlWindowRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekSpawnControl".GetHashCode(),
                spawnControlWindowRect,
                (id) => DrawSpawnControlWindow(id, flight),
                "Parsek - Real Spawn Control",
                opaqueWindowStyle,
                GUILayout.Width(spawnControlWindowRect.width),
                GUILayout.Height(spawnControlWindowRect.height)
            );
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

        private void DrawSpawnSortableHeader(string label, SpawnSortColumn col, float width)
        {
            DrawSpawnSortableHeader(label, col, false, width);
        }

        private void DrawSpawnSortableHeader(string label, SpawnSortColumn col, bool expand)
        {
            DrawSpawnSortableHeader(label, col, expand, 0);
        }

        private void DrawSpawnSortableHeader(string label, SpawnSortColumn col, bool expand, float width)
        {
            parentUI.DrawSortableHeaderCore(label, col, ref spawnSortColumn, ref spawnSortAscending, width, expand, () =>
            {
                ParsekLog.Verbose("UI", $"Spawn sort changed: column={spawnSortColumn}, ascending={spawnSortAscending}");
            });
        }

        private int CompareSpawnCandidates(NearbySpawnCandidate a, NearbySpawnCandidate b)
        {
            int cmp;
            switch (spawnSortColumn)
            {
                case SpawnSortColumn.Name:
                    cmp = string.Compare(a.vesselName, b.vesselName,
                        System.StringComparison.OrdinalIgnoreCase);
                    break;
                case SpawnSortColumn.SpawnTime:
                    cmp = a.endUT.CompareTo(b.endUT);
                    break;
                default: // Distance
                    cmp = a.distance.CompareTo(b.distance);
                    break;
            }
            return spawnSortAscending ? cmp : -cmp;
        }

        private void DrawSpawnControlWindow(int windowID, ParsekFlight flight)
        {
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
            DrawSpawnSortableHeader("Craft", SpawnSortColumn.Name, true);
            DrawSpawnSortableHeader("Dist", SpawnSortColumn.Distance, SpawnColW_Dist);
            DrawSpawnSortableHeader("Spawns at", SpawnSortColumn.SpawnTime, SpawnColW_SpawnTime);
            DrawSpawnSortableHeader("In T-", SpawnSortColumn.SpawnTime, SpawnColW_Countdown);
            GUILayout.Label("State", GUILayout.Width(SpawnColW_State));
            GUILayout.Label("", GUILayout.Width(SpawnColW_Warp));
            GUILayout.EndHorizontal();

            // Re-sort when candidate list, sort state, or departure info changes
            int gen = flight.ProximityCheckGeneration;
            if (candidates.Count != cachedCandidateCount
                || gen != cachedProximityGeneration
                || spawnSortColumn != cachedSortColumn
                || spawnSortAscending != cachedSortAscending)
            {
                cachedSortedCandidates.Clear();
                for (int ci = 0; ci < candidates.Count; ci++)
                    cachedSortedCandidates.Add(candidates[ci]);
                cachedSortedCandidates.Sort(CompareSpawnCandidates);
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
            for (int i = 0; i < sorted.Count; i++)
            {
                var cand = sorted[i];
                double delta = cand.endUT - currentUT;
                bool canWarp = cand.endUT > currentUT;

                GUILayout.BeginHorizontal();
                GUILayout.Label(cand.vesselName, GUILayout.ExpandWidth(true));
                GUILayout.Label(
                    string.Format(ic, "{0:F0}m", cand.distance),
                    GUILayout.Width(SpawnColW_Dist));
                GUILayout.Label(
                    KSPUtil.PrintDateCompact(cand.endUT, true),
                    GUILayout.Width(SpawnColW_SpawnTime));
                GUILayout.Label(
                    SelectiveSpawnUI.FormatCountdown(delta),
                    GUILayout.Width(SpawnColW_Countdown));

                // State column: departure info
                if (cand.willDepart)
                {
                    double depDelta = cand.departureUT - currentUT;
                    string stateText;
                    if (depDelta <= 0)
                        stateText = string.Format(ic, "Departing \u2192 {0}", cand.destination ?? "?");
                    else
                        stateText = string.Format(ic, "Departs {0}",
                            SelectiveSpawnUI.FormatCountdown(depDelta));
                    var prevColor = GUI.contentColor;
                    GUI.contentColor = depDelta <= 0
                        ? new Color(1f, 0.65f, 0.2f) // orange
                        : new Color(1f, 1f, 0.4f);    // yellow
                    GUILayout.Label(stateText, GUILayout.Width(SpawnColW_State));
                    GUI.contentColor = prevColor;
                }
                else
                {
                    GUILayout.Label("", GUILayout.Width(SpawnColW_State));
                }

                // Warp button: "FF-Depart" for departing, "FF-Spawn" for normal
                if (cand.willDepart)
                {
                    bool canWarpDep = cand.departureUT > currentUT;
                    GUI.enabled = canWarpDep;
                    if (GUILayout.Button("FF-Depart", GUILayout.Width(SpawnColW_Warp)))
                    {
                        ParsekLog.Info("UI",
                            string.Format(ic,
                                "Real Spawn Control: warp to departure '{0}' recording #{1} depUT={2:F1}",
                                cand.vesselName, cand.recordingIndex, cand.departureUT));
                        flight.WarpToDeparture(cand.recordingIndex, cand.departureUT);
                    }
                    GUI.enabled = true;
                }
                else
                {
                    GUI.enabled = canWarp;
                    if (GUILayout.Button("FF-Spawn", GUILayout.Width(SpawnColW_Warp)))
                    {
                        ParsekLog.Info("UI",
                            string.Format(ic,
                                "Real Spawn Control: warp to '{0}' recording #{1}",
                                cand.vesselName, cand.recordingIndex));
                        flight.WarpToRecordingEnd(cand.recordingIndex);
                    }
                    GUI.enabled = true;
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        private void DrawSpawnControlBottomBar(List<NearbySpawnCandidate> candidates,
            double currentUT, ParsekFlight flight)
        {
            // Bottom section -- pinned to window bottom
            GUILayout.FlexibleSpace();

            GUILayout.BeginHorizontal();
            var next = SelectiveSpawnUI.FindNextSpawnCandidate(candidates, currentUT);
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
