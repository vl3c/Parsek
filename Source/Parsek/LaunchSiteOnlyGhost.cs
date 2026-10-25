using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Owner ruling 2026-10-25 (todo PAD-VESSEL-GHOST-HOLDS-THE-PAD): a vessel whose whole
    /// recorded flight stays inside a launch-site circle and ends parked there (retired by the
    /// 2026-09-23 end-of-flight rule) never shows a ghost. Typical case: a rocket on the pad
    /// that a kerbal steps out of and never launches; its ghost would otherwise sit on the pad
    /// for as long as the player waited there. The kerbal's own EVA recording is untouched.
    ///
    /// <para>Visual-only, the same contract as <see cref="Recording.PlaybackEnabled"/> = false
    /// (bug #433): the hidden recording still completes at past-end, so the retirement, crew and
    /// ledger paths run exactly as for a visible one. The rule is DERIVED from the committed
    /// trees, never stored, so a later commit that takes the same vessel off the pad (a visit
    /// that launches it) shows every one of its recordings again, and the player's own
    /// visibility toggle is never written.</para>
    /// </summary>
    internal static class LaunchSiteOnlyGhost
    {
        internal const string Tag = "LaunchSiteGhost";

        /// <summary>Retirement decision source; tests replace it.</summary>
        internal static Func<Recording, VesselSpawner.KscRetirementDecision> RetirementForTesting;

        /// <summary>Home-world radius by body name, null when the body is not the home world
        /// or unknown; tests replace it.</summary>
        internal static Func<string, double?> HomeRadiusForTesting;

        private static int cachedStateVersion = int.MinValue;
        private static int cachedTreeCount = -1;
        private static HashSet<string> hiddenRecordingIds = new HashSet<string>(StringComparer.Ordinal);
        private static string lastHiddenSignature = string.Empty;

        internal static void ResetForTesting()
        {
            RetirementForTesting = null;
            HomeRadiusForTesting = null;
            InvalidateCache();
            lastHiddenSignature = string.Empty;
        }

        internal static void InvalidateCache()
        {
            cachedStateVersion = int.MinValue;
            cachedTreeCount = -1;
            hiddenRecordingIds = new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>True when this committed recording's ghost is hidden by the rule.</summary>
        internal static bool IsHidden(Recording rec)
        {
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId) || string.IsNullOrEmpty(rec.TreeId))
                return false;
            EnsureCache();
            return hiddenRecordingIds.Contains(rec.RecordingId);
        }

        private static void EnsureCache()
        {
            List<RecordingTree> trees = RecordingStore.CommittedTrees;
            int treeCount = trees?.Count ?? 0;
            if (cachedStateVersion == RecordingStore.StateVersion && cachedTreeCount == treeCount)
                return;
            cachedStateVersion = RecordingStore.StateVersion;
            cachedTreeCount = treeCount;
            hiddenRecordingIds = ComputeHiddenRecordingIds(
                trees,
                RetirementForTesting ?? VesselSpawner.EvaluateKscEndOfFlightRetirement,
                HomeRadiusForTesting ?? ResolveHomeRadius,
                SafeAltSiteCircles(),
                logChanges: true);
        }

        private static IList<LaunchSiteCircle> SafeAltSiteCircles()
        {
            try
            {
                return LaunchSiteExclusionZones.GetAltSiteCircles();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static double? ResolveHomeRadius(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName))
                return null;
            try
            {
                if (!VesselSpawner.TryResolveBodyByName(bodyName, out CelestialBody body)
                    || object.ReferenceEquals(body, null) || !body.isHomeWorld)
                    return null;
                return body.Radius;
            }
            catch (Exception ex)
            {
                if (!IsHeadlessAccessFailure(ex))
                    throw;
                return null;
            }
        }

        private static bool IsHeadlessAccessFailure(Exception ex)
        {
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                if (current is System.Security.SecurityException
                    || current is MethodAccessException
                    || current is MissingMethodException)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Every recording id the rule hides across <paramref name="trees"/>. A vessel is one
        /// tree's non-EVA recordings sharing a vessel pid. Logs one Info line per hidden vessel
        /// and a summary, only when the hidden set changed since the last call.
        /// </summary>
        internal static HashSet<string> ComputeHiddenRecordingIds(
            IList<RecordingTree> trees,
            Func<Recording, VesselSpawner.KscRetirementDecision> retirement,
            Func<string, double?> homeRadius,
            IList<LaunchSiteCircle> altCircles,
            bool logChanges)
        {
            var hidden = new HashSet<string>(StringComparer.Ordinal);
            var lines = new List<string>();
            int vesselsChecked = 0;
            int vesselsHidden = 0;
            if (trees != null)
            {
                for (int t = 0; t < trees.Count; t++)
                {
                    RecordingTree tree = trees[t];
                    if (tree?.Recordings == null)
                        continue;
                    var groups = new Dictionary<uint, List<Recording>>();
                    foreach (Recording rec in tree.Recordings.Values)
                    {
                        if (rec == null || rec.VesselPersistentId == 0
                            || !string.IsNullOrEmpty(rec.EvaCrewName)
                            || string.IsNullOrEmpty(rec.RecordingId))
                            continue;
                        if (!groups.TryGetValue(rec.VesselPersistentId, out List<Recording> list))
                        {
                            list = new List<Recording>();
                            groups[rec.VesselPersistentId] = list;
                        }
                        list.Add(rec);
                    }
                    foreach (KeyValuePair<uint, List<Recording>> g in groups)
                    {
                        vesselsChecked++;
                        if (!IsLaunchSiteOnlyVessel(g.Value, retirement, homeRadius, altCircles,
                                out string site))
                            continue;
                        vesselsHidden++;
                        var ids = new List<string>();
                        for (int i = 0; i < g.Value.Count; i++)
                        {
                            hidden.Add(g.Value[i].RecordingId);
                            ids.Add(g.Value[i].RecordingId);
                        }
                        ids.Sort(StringComparer.Ordinal);
                        lines.Add(string.Format(CultureInfo.InvariantCulture,
                            "Ghost hidden: vessel '{0}' pid={1} never left the launch site ({2}) " +
                            "tree='{3}' recordings={4} ids=[{5}]",
                            g.Value[0].VesselName, g.Key, site, tree.TreeName, ids.Count,
                            string.Join(",", ids.ToArray())));
                    }
                }
            }

            if (logChanges)
            {
                lines.Sort(StringComparer.Ordinal);
                string signature = string.Join("\n", lines.ToArray());
                if (!string.Equals(signature, lastHiddenSignature, StringComparison.Ordinal))
                {
                    lastHiddenSignature = signature;
                    for (int i = 0; i < lines.Count; i++)
                        ParsekLog.Info(Tag, lines[i]);
                    ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                        "Launch-site ghost rule: vesselsChecked={0} vesselsHidden={1} recordingsHidden={2}",
                        vesselsChecked, vesselsHidden, hidden.Count));
                }
            }
            return hidden;
        }

        /// <summary>
        /// Pure: one vessel's recordings never left a launch-site circle and the flight ended
        /// parked there. Every recording: no orbit segment, only Absolute track sections, at
        /// least one point, and every point on the home world inside a launch-site circle. At
        /// least one recording carries a terminal state, and every recording that carries one
        /// is retired at a launch site (so a vessel that docked, was boarded, was destroyed or
        /// recovered keeps its ghost).
        /// </summary>
        internal static bool IsLaunchSiteOnlyVessel(
            IList<Recording> vesselRecordings,
            Func<Recording, VesselSpawner.KscRetirementDecision> retirement,
            Func<string, double?> homeRadius,
            IList<LaunchSiteCircle> altCircles,
            out string site)
        {
            site = null;
            if (vesselRecordings == null || vesselRecordings.Count == 0
                || retirement == null || homeRadius == null)
                return false;

            bool anyTerminal = false;
            for (int r = 0; r < vesselRecordings.Count; r++)
            {
                Recording rec = vesselRecordings[r];
                if (rec == null || rec.Points == null || rec.Points.Count == 0)
                    return false;
                if (rec.OrbitSegments != null && rec.OrbitSegments.Count > 0)
                    return false;
                if (rec.TrackSections != null)
                {
                    for (int s = 0; s < rec.TrackSections.Count; s++)
                    {
                        if (rec.TrackSections[s].referenceFrame != ReferenceFrame.Absolute)
                            return false;
                    }
                }
                for (int p = 0; p < rec.Points.Count; p++)
                {
                    TrajectoryPoint pt = rec.Points[p];
                    double? radius = homeRadius(pt.bodyName);
                    if (!radius.HasValue)
                        return false;
                    LaunchSiteZoneHit hit = LaunchSiteExclusionZones.Classify(
                        pt.latitude, pt.longitude, radius.Value,
                        SpawnCollisionDetector.DefaultKscExclusionRadiusMeters, altCircles);
                    if (!hit.IsHit)
                        return false;
                    if (site == null)
                        site = LaunchSiteExclusionZones.DescribeSite(hit.SiteName) + " "
                            + SpawnCollisionDetector.DescribeKscExclusionZone(hit.Kind);
                }
                if (rec.TerminalStateValue.HasValue)
                {
                    anyTerminal = true;
                    if (!retirement(rec).Retire)
                        return false;
                }
            }
            if (!anyTerminal)
                site = null;
            return anyTerminal;
        }
    }
}
