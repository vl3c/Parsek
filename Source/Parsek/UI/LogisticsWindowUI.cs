using System.Collections.Generic;
using System.Globalization;
using ClickThroughFix;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// v0 Logistics window: single Supply Routes tab listing every committed
    /// route with a "Send Once" button. Send Once arms a single dispatch and
    /// fires it at the next moment the route's per-cycle conditions allow
    /// (funds, resources, endpoint resolved, orbital alignment / transfer
    /// window); after that cycle delivers, the route transitions to Paused
    /// (auto-cycle off). It is not an immediate force-dispatch and does not
    /// re-enable the recurring auto-cycle.
    /// Available in both FLIGHT and SPACECENTER (the main UI toggles it from
    /// either scene). Mirrors the minimal-window pattern from
    /// <see cref="SpawnControlUI"/>: ClickThruBlocker window, resize handle,
    /// CAMERACONTROLS input lock when the mouse is inside.
    /// </summary>
    internal class LogisticsWindowUI
    {
        private readonly ParsekUI parentUI;

        private bool showWindow;
        private Rect windowRect;
        private bool windowHasInputLock;
        private const string InputLockId = "Parsek_LogisticsWindow";

        private bool isResizing;
        private Vector2 scrollPos;
        private Rect lastWindowRect;

        // Column widths (matches the recordings/spawn-control look)
        private const float ColW_Name = 0f;       // expand
        private const float ColW_Origin = 110f;
        private const float ColW_Destination = 200f;
        private const float ColW_Interval = 80f;
        private const float ColW_Transit = 70f;
        private const float ColW_Cycles = 50f;
        private const float ColW_Status = 110f;
        private const float ColW_Action = 95f;

        private const float SpacingSmall = 3f;
        private const float SpacingLarge = 8f;
        private const float MinWindowWidth = 750f;
        private const float MinWindowHeight = 180f;

        public bool IsOpen
        {
            get { return showWindow; }
            set { showWindow = value; }
        }

        internal LogisticsWindowUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
        }

        public void DrawIfOpen(Rect mainWindowRect)
        {
            if (!showWindow)
            {
                ReleaseInputLock();
                return;
            }

            if (windowRect.width < 1f)
            {
                float x = mainWindowRect.x + mainWindowRect.width + 10;
                windowRect = new Rect(x, mainWindowRect.y, 850, 260);
                ParsekLog.Verbose("UI",
                    $"Logistics window initial position: x={windowRect.x.ToString("F0", CultureInfo.InvariantCulture)} y={windowRect.y.ToString("F0", CultureInfo.InvariantCulture)}");
            }

            ParsekUI.HandleResizeDrag(ref windowRect, ref isResizing,
                MinWindowWidth, MinWindowHeight, "Logistics window");

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            if (opaqueWindowStyle == null)
                return;
            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                windowRect = ClickThruBlocker.GUILayoutWindow(
                    "ParsekLogistics".GetHashCode(),
                    windowRect,
                    DrawWindow,
                    "Parsek - Logistics",
                    opaqueWindowStyle,
                    GUILayout.Width(windowRect.width),
                    GUILayout.Height(windowRect.height));
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }
            parentUI.LogWindowPosition("Logistics", ref lastWindowRect, windowRect);

            if (windowRect.Contains(Event.current.mousePosition))
            {
                if (!windowHasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, InputLockId);
                    windowHasInputLock = true;
                }
            }
            else
            {
                ReleaseInputLock();
            }
        }

        public void ReleaseInputLock()
        {
            if (!windowHasInputLock) return;
            InputLockManager.RemoveControlLock(InputLockId);
            windowHasInputLock = false;
        }

        private void DrawWindow(int windowID)
        {
            GUILayout.Space(5);

            // Tabs row — only Supply Routes for v0. The header row is kept so
            // future tabs (Deliveries log, Funds summary, etc.) can be added
            // without restructuring the window.
            GUILayout.BeginHorizontal();
            GUILayout.Label("Supply Routes", parentUI.GetColumnHeaderStyle());
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(SpacingSmall);

            // Column header
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", parentUI.GetColumnHeaderStyle(), GUILayout.ExpandWidth(true));
            GUILayout.Label("Origin", parentUI.GetColumnHeaderStyle(), GUILayout.Width(ColW_Origin));
            GUILayout.Label("Destination", parentUI.GetColumnHeaderStyle(), GUILayout.Width(ColW_Destination));
            GUILayout.Label("Interval", parentUI.GetColumnHeaderStyle(), GUILayout.Width(ColW_Interval));
            GUILayout.Label("Transit", parentUI.GetColumnHeaderStyle(), GUILayout.Width(ColW_Transit));
            GUILayout.Label("Cycles", parentUI.GetColumnHeaderStyle(), GUILayout.Width(ColW_Cycles));
            GUILayout.Label("Status", parentUI.GetColumnHeaderStyle(), GUILayout.Width(ColW_Status));
            GUILayout.Label("", GUILayout.Width(ColW_Action));
            GUILayout.EndHorizontal();

            IReadOnlyList<Route> routes = RouteStore.CommittedRoutes;
            int routeCount = routes?.Count ?? 0;

            scrollPos = GUILayout.BeginScrollView(scrollPos, GUILayout.ExpandHeight(true));
            GUILayout.BeginVertical(GUI.skin.box);

            if (routeCount == 0)
            {
                GUILayout.Label("No supply routes yet. Record a one-way transport that completes a single dock window and confirm the \"Create Supply Route?\" dialog on commit.");
            }
            else
            {
                double currentUT = TryGetCurrentUT();
                for (int i = 0; i < routeCount; i++)
                {
                    DrawRouteRow(routes[i], currentUT);
                }
            }

            GUILayout.EndVertical();
            GUILayout.EndScrollView();

            // Bottom bar
            GUILayout.Space(SpacingSmall);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Routes: {routeCount}");
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", GUILayout.Width(132)))
            {
                showWindow = false;
                ParsekLog.Verbose("UI", "Logistics window closed");
            }
            GUILayout.EndHorizontal();

            ParsekUI.DrawResizeHandle(windowRect, ref isResizing, "Logistics window");
            GUI.DragWindow();
        }

        private void DrawRouteRow(Route route, double currentUT)
        {
            if (route == null) return;

            GUILayout.BeginHorizontal();
            GUILayout.Label(route.Name ?? "<unnamed>", GUILayout.ExpandWidth(true));
            GUILayout.Label(FormatOrigin(route), GUILayout.Width(ColW_Origin));
            GUILayout.Label(FormatDestination(route), GUILayout.Width(ColW_Destination));
            GUILayout.Label(FormatDuration(route.DispatchInterval), GUILayout.Width(ColW_Interval));
            GUILayout.Label(FormatDuration(route.TransitDuration), GUILayout.Width(ColW_Transit));
            GUILayout.Label(route.CompletedCycles.ToString(CultureInfo.InvariantCulture),
                GUILayout.Width(ColW_Cycles));

            // Status with a tint for the wait/blocked states so eye-scan is quick.
            Color prevColor = GUI.contentColor;
            GUI.contentColor = StatusColor(route.Status);
            GUILayout.Label(route.Status.ToString(), GUILayout.Width(ColW_Status));
            GUI.contentColor = prevColor;

            // Send Once button — disables the auto-cycle loop and arms a
            // single dispatch. The route fires at the next moment its
            // per-cycle conditions allow (funds, resources, endpoint resolved,
            // orbital alignment / transfer window). After the cycle delivers,
            // the route transitions to Paused (auto-cycle off). Refused only
            // for states the orchestrator can't recover into a one-shot
            // (InTransit / MissingSource / SourceChanged / EndpointLost);
            // Paused routes un-pause on click.
            bool sendable = IsSendNowEnabled(route);
            string tooltip = sendable
                ? "Send one cycle at the next moment conditions allow (funds, resources, endpoint, orbital alignment). The route auto-pauses after that cycle delivers; click Send Once again to fire another one-shot."
                : $"Send Once disabled: status={route.Status}";
            GUI.enabled = sendable;
            if (GUILayout.Button(new GUIContent("Send Once", tooltip), GUILayout.Width(ColW_Action)))
            {
                bool ok = RouteOrchestrator.TrySendOneCycleNow(route, currentUT);
                ParsekLog.Info("UI",
                    $"Logistics: Send Once clicked route={route.Id ?? "<none>"} name='{route.Name ?? "<none>"}' result={(ok ? "nudged" : "rejected")}");
            }
            GUI.enabled = true;

            GUILayout.EndHorizontal();
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static double TryGetCurrentUT()
        {
            try
            {
                return Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0.0;
            }
            catch
            {
                return 0.0;
            }
        }

        private static string FormatOrigin(Route route)
        {
            if (route.IsKscOrigin) return "KSC (funds)";
            if (route.Origin.VesselPersistentId != 0u
                || !string.IsNullOrEmpty(route.Origin.BodyName))
            {
                return FormatEndpointShort(route.Origin);
            }
            return "—";
        }

        private static string FormatDestination(Route route)
        {
            if (route.Stops == null || route.Stops.Count == 0)
                return "—";
            RouteStop stop = route.Stops[0];
            if (stop == null) return "—";
            return FormatEndpointShort(stop.Endpoint);
        }

        private static string FormatEndpointShort(RouteEndpoint ep)
        {
            if (string.IsNullOrEmpty(ep.BodyName)) return "—";
            string sit = ep.IsSurface ? "surface" : "orbit";
            return string.Format(CultureInfo.InvariantCulture,
                "{0} ({1}) {2:F2}°,{3:F2}°",
                ep.BodyName, sit, ep.Latitude, ep.Longitude);
        }

        internal static string FormatDuration(double seconds)
        {
            if (seconds <= 0.0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
                return "—";
            if (seconds < 60.0)
                return string.Format(CultureInfo.InvariantCulture, "{0:F0}s", seconds);
            if (seconds < 3600.0)
                return string.Format(CultureInfo.InvariantCulture, "{0:F1}m", seconds / 60.0);
            if (seconds < 86400.0)
                return string.Format(CultureInfo.InvariantCulture, "{0:F1}h", seconds / 3600.0);
            return string.Format(CultureInfo.InvariantCulture, "{0:F1}d", seconds / 21600.0); // Kerbin days
        }

        private static Color StatusColor(RouteStatus status)
        {
            switch (status)
            {
                case RouteStatus.Active:
                case RouteStatus.InTransit:
                    return new Color(0.55f, 1f, 0.55f); // green
                case RouteStatus.WaitingForResources:
                case RouteStatus.WaitingForFunds:
                case RouteStatus.DestinationFull:
                    return new Color(1f, 1f, 0.4f); // yellow
                case RouteStatus.Paused:
                    return new Color(0.7f, 0.7f, 0.7f); // grey
                case RouteStatus.EndpointLost:
                case RouteStatus.MissingSourceRecording:
                case RouteStatus.SourceChanged:
                    return new Color(1f, 0.4f, 0.4f); // red
                default:
                    return Color.white;
            }
        }

        // Pure helper for test reachability — also used by the row draw to
        // gate the button without duplicating the predicate. Mirrors
        // RouteOrchestrator.TrySendOneCycleNow's status-accept set: Active,
        // recoverable wait states, AND Paused (Send Once un-pauses for one
        // cycle). Refused for InTransit (cycle already in flight) and the
        // three "source/endpoint broken" states that need separate recovery.
        internal static bool IsSendNowEnabled(Route route)
        {
            if (route == null) return false;
            return route.Status == RouteStatus.Active
                || route.Status == RouteStatus.WaitingForResources
                || route.Status == RouteStatus.WaitingForFunds
                || route.Status == RouteStatus.DestinationFull
                || route.Status == RouteStatus.Paused;
        }
    }
}
