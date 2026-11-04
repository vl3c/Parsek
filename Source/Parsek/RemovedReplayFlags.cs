using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The flags the player removed from the world (todo LOOP-REPLAY-REPLANTS-REMOVED-FLAG,
    /// replay-theory audit R5). A recording's <see cref="FlagEvent"/> plants a real flag vessel
    /// when replay reaches its UT, deduplicated only by "a flag already stands within 1 m"
    /// (<c>GhostPlaybackLogic.FlagExistsAtPosition</c>). That dedup reads the world, so once the
    /// player recovers, terminates or picks up the flag, every later walk over the event plants
    /// it again: each loop cycle, each overlap ghost, each chain-loop unit cycle and each fresh
    /// playback state after a scene change.
    ///
    /// <para>
    /// A flag is planted once, at its moment; what the player does to it afterwards is theirs.
    /// So every removal of a flag vessel leaves a tombstone here (body, position, the UT of the
    /// removal), and a flag event at the same spot whose plant UT is not after the removal is
    /// never planted again. A load that moves the world back before the removal (a plain
    /// rewind, a Re-Fly start or discard, a stock revert) drops the tombstones after the loaded
    /// world's UT, so the flag comes back when replay reaches its moment
    /// (<see cref="LoadStateCategory.RemovedReplayFlags"/>). A cold load (F9, Load Game) installs
    /// the save's own copy: the world of the save it describes.
    /// </para>
    ///
    /// <para>
    /// Position, not recording identity, is the key on purpose: a chain split, a Re-Fly HEAD /
    /// TIP split and the optimizer clone a flag event onto several recordings, and the flag the
    /// live flight planted is the same world object the event describes.
    /// </para>
    /// </summary>
    internal static class RemovedReplayFlags
    {
        internal const string Tag = "FlagReplay";
        internal const string NodeName = "REMOVED_REPLAY_FLAGS";
        internal const string EntryNodeName = "FLAG";

        /// <summary>
        /// How far a flag event may sit from a removed flag and still be the same flag. Looser
        /// than the 1 m plant dedup: a flag that toppled before it was removed has moved its
        /// root part, and a live-planted flag settles a little after the plant event is captured.
        /// </summary>
        internal const double MatchToleranceMeters = 3.0;

        /// <summary>Two removal reports of one flag (a recovery fires both the recovery and the
        /// destroy event) collapse into one tombstone within this many seconds.</summary>
        internal const double DuplicateWindowSeconds = 5.0;

        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        /// <summary>One removed flag.</summary>
        internal struct Tombstone
        {
            public string BodyName;
            public double BodyRadius;
            public double Latitude;
            public double Longitude;
            public double Altitude;
            public double RemovedUT;
            public string FlagName;
            public string Source;
        }

        private static readonly List<Tombstone> tombstones = new List<Tombstone>();

        internal static IReadOnlyList<Tombstone> Tombstones => tombstones;

        internal static int Count => tombstones.Count;

        internal static void ResetForTesting()
        {
            tombstones.Clear();
        }

        /// <summary>
        /// Pure: the straight-line distance in metres between two surface positions on a sphere
        /// of <paramref name="bodyRadius"/>. Matches the world-space 3D check the plant dedup uses
        /// (no lat / lon box, so a high latitude or a small body reads correctly).
        /// </summary>
        internal static double SurfaceDistanceMeters(
            double lat1, double lon1, double alt1,
            double lat2, double lon2, double alt2,
            double bodyRadius)
        {
            const double deg = Math.PI / 180.0;
            double r1 = bodyRadius + alt1;
            double r2 = bodyRadius + alt2;
            double la1 = lat1 * deg, lo1 = lon1 * deg, la2 = lat2 * deg, lo2 = lon2 * deg;
            double x1 = r1 * Math.Cos(la1) * Math.Cos(lo1);
            double y1 = r1 * Math.Cos(la1) * Math.Sin(lo1);
            double z1 = r1 * Math.Sin(la1);
            double x2 = r2 * Math.Cos(la2) * Math.Cos(lo2);
            double y2 = r2 * Math.Cos(la2) * Math.Sin(lo2);
            double z2 = r2 * Math.Sin(la2);
            double dx = x1 - x2, dy = y1 - y2, dz = z1 - z2;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>
        /// Pure: does <paramref name="t"/> stand for the flag <paramref name="evt"/> plants? Same
        /// body, within <see cref="MatchToleranceMeters"/>, and the flag was planted no later than
        /// it was removed (a flag planted at the same spot after the removal is a new flag).
        /// </summary>
        internal static bool Matches(Tombstone t, FlagEvent evt)
        {
            if (string.IsNullOrEmpty(t.BodyName) || !string.Equals(t.BodyName, evt.bodyName, StringComparison.Ordinal))
                return false;
            if (!IsFinite(evt.ut) || evt.ut > t.RemovedUT)
                return false;
            if (!IsFinite(evt.latitude) || !IsFinite(evt.longitude) || !IsFinite(evt.altitude))
                return false;
            double d = SurfaceDistanceMeters(
                t.Latitude, t.Longitude, t.Altitude,
                evt.latitude, evt.longitude, evt.altitude, t.BodyRadius);
            return d <= MatchToleranceMeters;
        }

        /// <summary>The tombstone that keeps <paramref name="evt"/> from being planted again, or -1.</summary>
        internal static int FindBlocking(FlagEvent evt)
        {
            for (int i = 0; i < tombstones.Count; i++)
            {
                if (Matches(tombstones[i], evt))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Pure: does a vessel removal count as the player removing a flag? Only a flag vessel,
        /// never a ghost map vessel, and not while a rewind strips its world (that removal is the
        /// rewind's, and the rewind save's world decides what stands).
        /// </summary>
        internal static bool ShouldRecordRemoval(bool isFlag, bool isGhostMapVessel, bool rewinding)
        {
            return isFlag && !isGhostMapVessel && !rewinding;
        }

        /// <summary>
        /// Records that the player removed a flag. Ignores a non-finite or position-less report
        /// and a second report of a flag already recorded within <see cref="DuplicateWindowSeconds"/>.
        /// Returns true when a new tombstone was added.
        /// </summary>
        internal static bool RecordRemoval(Tombstone t)
        {
            if (string.IsNullOrEmpty(t.BodyName) || !IsFinite(t.BodyRadius) || t.BodyRadius <= 0.0
                || !IsFinite(t.Latitude) || !IsFinite(t.Longitude) || !IsFinite(t.Altitude)
                || !IsFinite(t.RemovedUT))
            {
                ParsekLog.Verbose(Tag, string.Format(ic,
                    "Flag removal not recorded: unusable report name='{0}' body={1} ut={2} source={3}",
                    t.FlagName ?? "", t.BodyName ?? "(none)", t.RemovedUT.ToString("R", ic), t.Source ?? "?"));
                return false;
            }

            for (int i = 0; i < tombstones.Count; i++)
            {
                Tombstone e = tombstones[i];
                if (!string.Equals(e.BodyName, t.BodyName, StringComparison.Ordinal))
                    continue;
                if (Math.Abs(e.RemovedUT - t.RemovedUT) > DuplicateWindowSeconds)
                    continue;
                double d = SurfaceDistanceMeters(
                    e.Latitude, e.Longitude, e.Altitude, t.Latitude, t.Longitude, t.Altitude, e.BodyRadius);
                if (d <= MatchToleranceMeters)
                {
                    ParsekLog.Verbose(Tag, string.Format(ic,
                        "Flag removal already recorded: '{0}' on {1} ut={2} source={3} (first source={4})",
                        t.FlagName ?? "", t.BodyName, t.RemovedUT.ToString("R", ic), t.Source ?? "?", e.Source ?? "?"));
                    return false;
                }
            }

            tombstones.Add(t);
            ParsekLog.Info(Tag, string.Format(ic,
                "Flag removed by the player: '{0}' on {1} at ({2:F5},{3:F5},{4:F1}) ut={5} source={6}; "
                + "replays will not plant it again (tombstones={7})",
                t.FlagName ?? "", t.BodyName, t.Latitude, t.Longitude, t.Altitude,
                t.RemovedUT.ToString("R", ic), t.Source ?? "?", tombstones.Count));
            return true;
        }

        /// <summary>
        /// Drops every tombstone of a removal after <paramref name="loadedWorldUT"/>: the loaded
        /// world is from before it, so the flag stands there again or is still to be planted.
        /// A non-finite UT drops nothing (and says so). Returns how many were dropped.
        /// </summary>
        internal static int DropRemovedAfter(double loadedWorldUT, string reason)
        {
            if (!IsFinite(loadedWorldUT))
            {
                if (tombstones.Count > 0)
                    ParsekLog.Warn(Tag, string.Format(ic,
                        "Removed-flag tombstones not reconciled ({0}): the loaded UT is unknown, kept {1}",
                        reason ?? "unspecified", tombstones.Count));
                return 0;
            }

            int dropped = tombstones.RemoveAll(t => t.RemovedUT > loadedWorldUT);
            if (dropped > 0)
                ParsekLog.Info(Tag, string.Format(ic,
                    "Removed-flag tombstones after the loaded UT dropped ({0}): loadedUT={1} dropped={2} kept={3}",
                    reason ?? "unspecified", loadedWorldUT.ToString("R", ic), dropped, tombstones.Count));
            else
                ParsekLog.Verbose(Tag, string.Format(ic,
                    "Removed-flag tombstones reconciled ({0}): loadedUT={1} none after it, kept {2}",
                    reason ?? "unspecified", loadedWorldUT.ToString("R", ic), tombstones.Count));
            return dropped;
        }

        /// <summary>Replaces the set with a save's copy (a cold load).</summary>
        internal static void Replace(IList<Tombstone> next, string reason)
        {
            int before = tombstones.Count;
            tombstones.Clear();
            if (next != null)
                tombstones.AddRange(next);
            ParsekLog.Verbose(Tag, string.Format(ic,
                "Removed-flag tombstones replaced ({0}): before={1} after={2}",
                reason ?? "unspecified", before, tombstones.Count));
        }

        /// <summary>Writes the set as a <see cref="NodeName"/> child (nothing when empty).</summary>
        internal static int SaveInto(ConfigNode parsekNode)
        {
            if (parsekNode == null)
                return 0;
            parsekNode.RemoveNodes(NodeName);
            if (tombstones.Count == 0)
                return 0;
            ConfigNode root = parsekNode.AddNode(NodeName);
            for (int i = 0; i < tombstones.Count; i++)
            {
                Tombstone t = tombstones[i];
                ConfigNode n = root.AddNode(EntryNodeName);
                n.AddValue("body", t.BodyName ?? "");
                n.AddValue("radius", t.BodyRadius.ToString("R", ic));
                n.AddValue("lat", t.Latitude.ToString("R", ic));
                n.AddValue("lon", t.Longitude.ToString("R", ic));
                n.AddValue("alt", t.Altitude.ToString("R", ic));
                n.AddValue("removedUT", t.RemovedUT.ToString("R", ic));
                if (!string.IsNullOrEmpty(t.FlagName))
                    n.AddValue("name", t.FlagName);
                if (!string.IsNullOrEmpty(t.Source))
                    n.AddValue("source", t.Source);
            }
            return tombstones.Count;
        }

        /// <summary>Reads what <see cref="SaveInto"/> wrote; skips an entry with a missing or unparsable field.</summary>
        internal static List<Tombstone> LoadFrom(ConfigNode parsekNode)
        {
            var result = new List<Tombstone>();
            ConfigNode root = parsekNode?.GetNode(NodeName);
            if (root == null)
                return result;
            ConfigNode[] nodes = root.GetNodes(EntryNodeName);
            int skipped = 0;
            for (int i = 0; i < nodes.Length; i++)
            {
                ConfigNode n = nodes[i];
                string body = n.GetValue("body");
                if (string.IsNullOrEmpty(body)
                    || !TryRead(n, "radius", out double radius)
                    || !TryRead(n, "lat", out double lat)
                    || !TryRead(n, "lon", out double lon)
                    || !TryRead(n, "alt", out double alt)
                    || !TryRead(n, "removedUT", out double ut))
                {
                    skipped++;
                    continue;
                }
                result.Add(new Tombstone
                {
                    BodyName = body,
                    BodyRadius = radius,
                    Latitude = lat,
                    Longitude = lon,
                    Altitude = alt,
                    RemovedUT = ut,
                    FlagName = n.GetValue("name"),
                    Source = n.GetValue("source"),
                });
            }
            if (skipped > 0)
                ParsekLog.Warn(Tag, string.Format(ic,
                    "Removed-flag tombstones: skipped {0} unreadable entr{1} of {2}",
                    skipped, skipped == 1 ? "y" : "ies", nodes.Length));
            return result;
        }

        private static bool TryRead(ConfigNode n, string key, out double value)
        {
            value = double.NaN;
            string s = n.GetValue(key);
            return !string.IsNullOrEmpty(s)
                && double.TryParse(s, NumberStyles.Float, ic, out value)
                && IsFinite(value);
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
    }
}
