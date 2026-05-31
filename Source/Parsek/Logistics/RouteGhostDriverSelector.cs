using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// Per-frame ghost-driving SELECTOR for Supply Routes (design §0.4; plan
    /// Phase 3 task 1). Filters the committed routes down to the ones that should
    /// render a looping ghost RIGHT NOW (<see cref="RouteStatusPolicy.GhostDriving"/>),
    /// and materializes each as its route-owned backing <see cref="Mission"/> via
    /// <see cref="RouteBackingMission.BuildMission"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The returned missions are appended (by the three host push seams:
    /// <c>ParsekFlight</c> / <c>ParsekKSC</c> / <c>ParsekTrackingStation</c>
    /// <c>DriveMissionLoopUnits</c>) to a NEW unioned list alongside
    /// <c>MissionStore.Missions</c>, and that unioned list is passed as the single
    /// argument to the EXISTING <c>MissionLoopUnitBuilder.Build</c> /
    /// <c>BuildSignature</c>. Route missions fold into the existing signature +
    /// owner/member collision logging automatically by being in the passed list —
    /// no union-side LoopUnitSet helper, and no edit to any locked Missions file.
    /// </para>
    /// <para>
    /// Reads ONLY <see cref="RouteStore.CommittedRoutes"/> (the route store
    /// surface), so it sits outside the ERS/ELS grep gate (mirrors
    /// <see cref="RouteTreeGuard"/>). Pure with respect to Unity: the caller passes
    /// <paramref name="currentUT"/> in. <see cref="RouteBackingMission.BuildMission"/>
    /// uses <c>currentUT</c> for its Verbose log only — render phase is owned by the
    /// loop clock, not the Mission anchor (Phase 0 pin).
    /// </para>
    /// </remarks>
    internal static class RouteGhostDriverSelector
    {
        private const string Tag = "RouteGhost";

        /// <summary>
        /// Selects the backing <see cref="Mission"/> objects for every committed
        /// route in a <see cref="RouteStatusPolicy.GhostDriving"/> status and
        /// returns them as a fresh list (never null). Routes in a non-ghost-driving
        /// status (Paused / EndpointLost / MissingSourceRecording / SourceChanged)
        /// and null routes are skipped. A null-yielding
        /// <see cref="RouteBackingMission.BuildMission"/> (null route) never reaches
        /// the result. Emits a rate-limited per-frame summary (shared key).
        /// </summary>
        /// <param name="routes">Committed routes, normally
        /// <see cref="RouteStore.CommittedRoutes"/>.</param>
        /// <param name="currentUT">Game UT, threaded into
        /// <see cref="RouteBackingMission.BuildMission"/> for its diagnostic log.</param>
        internal static IReadOnlyList<Mission> SelectGhostDrivingBackingMissions(
            IReadOnlyList<Route> routes, double currentUT)
        {
            var result = new List<Mission>();
            if (routes == null)
                return result;

            int ghostDriving = 0;
            int skippedByStatus = 0;
            int skippedNull = 0;
            for (int i = 0; i < routes.Count; i++)
            {
                Route r = routes[i];
                if (r == null)
                {
                    skippedNull++;
                    continue;
                }
                if (!RouteStatusPolicy.GhostDriving(r.Status))
                {
                    skippedByStatus++;
                    continue;
                }

                Mission mission = RouteBackingMission.BuildMission(r, currentUT);
                if (mission == null)
                {
                    skippedNull++;
                    continue;
                }
                result.Add(mission);
                ghostDriving++;
            }

            var ic = CultureInfo.InvariantCulture;
            ParsekLog.VerboseRateLimited(Tag, "select-ghost-driving",
                $"SelectGhostDrivingBackingMissions: ghostDriving={ghostDriving.ToString(ic)} " +
                $"skippedByStatus={skippedByStatus.ToString(ic)} skippedNull={skippedNull.ToString(ic)} " +
                $"totalRoutes={routes.Count.ToString(ic)}",
                2.0);
            return result;
        }
    }
}
