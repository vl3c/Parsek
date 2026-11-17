using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.TestCommands
{
    /// <summary>Where a launch-site spawn point's position came from.</summary>
    internal enum SitePointSource
    {
        /// <summary>Neither the cached value nor a transform could be read.</summary>
        None = 0,

        /// <summary>The spawn point's own cached <c>latitude</c> / <c>longitude</c> /
        /// <c>altitude</c> (<c>latlonaltSet</c> true): what stock spawns a vessel from.</summary>
        Cache = 1,

        /// <summary>The spawn point's transform, read through the body, because the cache
        /// was not set yet.</summary>
        Transform = 2,
    }

    /// <summary>One spawn point of one stock launch site.</summary>
    internal struct LaunchSitePointRow
    {
        internal string SiteName;
        internal string PointName;

        /// <summary>True for a space center facility site (the KSC's own pad and runway),
        /// false for a stock alternate launch site.</summary>
        internal bool Facility;

        /// <summary>The site's <c>EditorFacility</c> name (<c>VAB</c>, <c>SPH</c>, ...).</summary>
        internal string Editor;
        internal SitePointSource Source;
        internal double Latitude;
        internal double Longitude;
        internal double Altitude;
    }

    /// <summary>One fixed terrain sample point and the height measured there.</summary>
    internal struct TerrainSampleRow
    {
        internal string Name;
        internal double Latitude;
        internal double Longitude;

        /// <summary>False when the body or its terrain could not be read.</summary>
        internal bool Measured;
        internal double Height;
    }

    /// <summary>A fixed point the sites read-back measures the terrain height at.</summary>
    internal struct SiteSamplePoint
    {
        internal readonly string Name;
        internal readonly double Latitude;
        internal readonly double Longitude;

        internal SiteSamplePoint(string name, double latitude, double longitude)
        {
            Name = name;
            Latitude = latitude;
            Longitude = longitude;
        }
    }

    /// <summary>Everything <c>ListHandles kind=sites</c> reads from the live game.</summary>
    internal struct SitesReading
    {
        /// <summary>The home body's name; null when no body could be read.</summary>
        internal string BodyName;

        /// <summary>The working space center's position, from its live transform.</summary>
        internal bool SpaceCenterFound;
        internal double SpaceCenterLatitude;
        internal double SpaceCenterLongitude;
        internal double SpaceCenterAltitude;

        /// <summary>The recovery reference point: the latitude / longitude the space
        /// center cached at its start, which stock's recovery distance reads.</summary>
        internal bool RecoveryFound;
        internal double RecoveryLatitude;
        internal double RecoveryLongitude;

        /// <summary>Every spawn point of every stock launch site. Null is read as empty;
        /// the builder sorts it.</summary>
        internal List<LaunchSitePointRow> LaunchSites;

        /// <summary>One row per <see cref="TestCommandSitesReadback.SamplePoints"/> entry,
        /// in table order. Null is read as empty.</summary>
        internal List<TerrainSampleRow> Samples;
    }

    /// <summary>
    /// Pure half of <c>ListHandles kind=sites</c> (multiplayer T-07): the stock site
    /// baseline a race save is compared against (design section 7.3: the working space
    /// center, the launch sites, the recovery reference point and the terrain under the
    /// sites). The applier (<c>ParsekTestCommandAddon.ListHandlesSites.cs</c>) reads the
    /// live values into a <see cref="SitesReading"/>; the wire keys, the order, the
    /// number formats and the one-log-line-per-fact shape live here.
    ///
    /// <para><b>Fixed formats, not "R".</b> The positions are read from world-space
    /// transforms, whose last bits move with the floating origin from run to run, so a
    /// round-trip format would make an exact pin impossible. Degrees carry seven decimals
    /// (about a centimetre on Kerbin) and metres three; a spec pins the stable leading
    /// digits and regexes the tail.</para>
    /// </summary>
    internal static class TestCommandSitesReadback
    {
        /// <summary>Prefix of every per-fact Info line.</summary>
        internal const string SitesLogLinePrefix = "listhandles sites";

        /// <summary>Fact word of the working space center line.</summary>
        internal const string SpaceCenterFact = "spaceCenter";

        /// <summary>Fact word of the recovery reference point line.</summary>
        internal const string RecoveryFact = "recovery";

        /// <summary>Fact word of a launch-site spawn point line.</summary>
        internal const string LaunchSiteFact = "launchSite";

        /// <summary>Fact word of a terrain sample line.</summary>
        internal const string TerrainFact = "terrain";

        /// <summary>A launch site of the space center itself (pad, runway).</summary>
        internal const string FacilitySiteKind = "facility";

        /// <summary>A stock alternate launch site.</summary>
        internal const string AlternateSiteKind = "alternate";

        /// <summary>The value a fact that could not be read shows.</summary>
        internal const string NoneValue = "none";

        /// <summary>Most spawn points enumerated in one response; <c>count</c> is the
        /// full total and <c>truncated</c> says when it was cut.</summary>
        internal const int MaxLaunchSitePoints = 16;

        /// <summary>
        /// The fixed terrain sample points, in report order. Today the stock KSC pad and
        /// the two ends of its runway: the pad and the runway's west threshold are the
        /// stock coordinates <c>SpawnCollisionDetector</c> already uses
        /// (<c>KscPadLatitude/Longitude</c>, <c>KscRunwayLatitude/Longitude</c>), and the
        /// east end is the threshold moved 0.23 degrees east along the same latitude
        /// (about 2.4 km, the strip's length on Kerbin; the race site table, R2-01,
        /// carries the measured runway end). The race sites' four patches join this table
        /// when the site table exists (R2-12).
        /// </summary>
        internal static readonly SiteSamplePoint[] SamplePoints =
        {
            new SiteSamplePoint("pad", -0.0972, -74.5575),
            new SiteSamplePoint("runwayWest", -0.0502, -74.7300),
            new SiteSamplePoint("runwayEast", -0.0502, -74.5000),
        };

        /// <summary>The keys of a launch-site line after <c>index=</c>, in the order the
        /// line writes them (each is <c>site&lt;i&gt;</c> + key in the payload, the site
        /// name itself being <c>site&lt;i&gt;</c>).</summary>
        internal static readonly string[] LaunchSiteLogLineKeys =
        {
            "point", "kind", "editor", "lat", "lon", "alt", "src",
        };

        /// <summary>The keys of a terrain line after <c>index=</c> and <c>name=</c>.</summary>
        internal static readonly string[] TerrainLogLineKeys = { "lat", "lon", "height" };

        /// <summary>Degrees with seven decimals, invariant; <see cref="NoneValue"/> for
        /// a value that is not finite.</summary>
        internal static string FormatDegrees(double value)
            => IsFinite(value)
                ? value.ToString("F7", CultureInfo.InvariantCulture)
                : NoneValue;

        /// <summary>Metres with three decimals, invariant; <see cref="NoneValue"/> for a
        /// value that is not finite.</summary>
        internal static string FormatMetres(double value)
            => IsFinite(value)
                ? value.ToString("F3", CultureInfo.InvariantCulture)
                : NoneValue;

        /// <summary>The wire token of a spawn point's source.</summary>
        internal static string SourceToken(SitePointSource source)
        {
            switch (source)
            {
                case SitePointSource.Cache: return "cache";
                case SitePointSource.Transform: return "transform";
                default: return NoneValue;
            }
        }

        /// <summary>
        /// Facility sites first, then the alternates; inside each, ordinal by site name
        /// and then by spawn point name. Stock list order is not a contract, and a spec
        /// names a site by index.
        /// </summary>
        internal static List<LaunchSitePointRow> SortLaunchSites(IReadOnlyList<LaunchSitePointRow> rows)
        {
            var sorted = rows != null
                ? new List<LaunchSitePointRow>(rows)
                : new List<LaunchSitePointRow>();
            sorted.Sort((a, b) =>
            {
                if (a.Facility != b.Facility)
                    return a.Facility ? -1 : 1;
                int byName = string.CompareOrdinal(a.SiteName ?? string.Empty, b.SiteName ?? string.Empty);
                if (byName != 0)
                    return byName;
                return string.CompareOrdinal(a.PointName ?? string.Empty, b.PointName ?? string.Empty);
            });
            return sorted;
        }

        /// <summary>
        /// <c>kind=sites count=&lt;n&gt; truncated=&lt;b&gt; body=&lt;name&gt;
        /// scLat= scLon= scAlt= recoveryLat= recoveryLon=</c>, then per enumerated spawn
        /// point <c>site&lt;i&gt;=&lt;site&gt; site&lt;i&gt;point site&lt;i&gt;kind
        /// site&lt;i&gt;editor site&lt;i&gt;lat site&lt;i&gt;lon site&lt;i&gt;alt
        /// site&lt;i&gt;src</c>, then <c>samples=&lt;n&gt;</c> and per sample
        /// <c>sample&lt;j&gt;=&lt;name&gt; sample&lt;j&gt;lat sample&lt;j&gt;lon
        /// sample&lt;j&gt;height</c>. <c>count</c> is the number of spawn points; a value
        /// that could not be read is <c>none</c>.
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildPayload(SitesReading r)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            List<LaunchSitePointRow> sites = SortLaunchSites(r.LaunchSites);
            int total = sites.Count;
            int shown = Math.Min(total, MaxLaunchSitePoints);

            var payload = new List<KeyValuePair<string, string>>
            {
                Kv("kind", TestCommandListHandles.SitesKindToken),
                Kv("count", total.ToString(ic)),
                Kv("truncated", total > MaxLaunchSitePoints ? "true" : "false"),
                Kv("body", string.IsNullOrEmpty(r.BodyName) ? NoneValue : r.BodyName),
                Kv("scLat", r.SpaceCenterFound ? FormatDegrees(r.SpaceCenterLatitude) : NoneValue),
                Kv("scLon", r.SpaceCenterFound ? FormatDegrees(r.SpaceCenterLongitude) : NoneValue),
                Kv("scAlt", r.SpaceCenterFound ? FormatMetres(r.SpaceCenterAltitude) : NoneValue),
                Kv("recoveryLat", r.RecoveryFound ? FormatDegrees(r.RecoveryLatitude) : NoneValue),
                Kv("recoveryLon", r.RecoveryFound ? FormatDegrees(r.RecoveryLongitude) : NoneValue),
            };
            for (int i = 0; i < shown; i++)
            {
                LaunchSitePointRow row = sites[i];
                string prefix = "site" + i.ToString(ic);
                bool known = row.Source != SitePointSource.None;
                payload.Add(Kv(prefix, row.SiteName ?? string.Empty));
                payload.Add(Kv(prefix + "point", row.PointName ?? string.Empty));
                payload.Add(Kv(prefix + "kind", row.Facility ? FacilitySiteKind : AlternateSiteKind));
                payload.Add(Kv(prefix + "editor", string.IsNullOrEmpty(row.Editor) ? NoneValue : row.Editor));
                payload.Add(Kv(prefix + "lat", known ? FormatDegrees(row.Latitude) : NoneValue));
                payload.Add(Kv(prefix + "lon", known ? FormatDegrees(row.Longitude) : NoneValue));
                payload.Add(Kv(prefix + "alt", known ? FormatMetres(row.Altitude) : NoneValue));
                payload.Add(Kv(prefix + "src", SourceToken(row.Source)));
            }

            int samples = r.Samples != null ? r.Samples.Count : 0;
            payload.Add(Kv("samples", samples.ToString(ic)));
            for (int j = 0; j < samples; j++)
            {
                TerrainSampleRow s = r.Samples[j];
                string prefix = "sample" + j.ToString(ic);
                payload.Add(Kv(prefix, s.Name ?? string.Empty));
                payload.Add(Kv(prefix + "lat", FormatDegrees(s.Latitude)));
                payload.Add(Kv(prefix + "lon", FormatDegrees(s.Longitude)));
                payload.Add(Kv(prefix + "height", s.Measured ? FormatMetres(s.Height) : NoneValue));
            }
            return payload;
        }

        /// <summary>
        /// One Info line per fact, read out of the built payload so the log cannot
        /// disagree with the wire:
        /// <c>listhandles sites spaceCenter body=.. lat=.. lon=.. alt=..</c>,
        /// <c>listhandles sites recovery lat=.. lon=..</c>, one
        /// <c>listhandles sites launchSite index=&lt;i&gt; name='..' point='..' kind=..
        /// editor=.. lat=.. lon=.. alt=.. src=..</c> per enumerated spawn point and one
        /// <c>listhandles sites terrain index=&lt;j&gt; name=.. lat=.. lon=.. height=..</c>
        /// per sample. The names are quoted because a spawn point name may hold spaces.
        /// Empty for every other family.
        /// </summary>
        internal static List<string> LogLines(
            IReadOnlyList<KeyValuePair<string, string>> payload, ListHandlesKind kind)
        {
            var lines = new List<string>();
            if (kind != ListHandlesKind.Sites || payload == null)
                return lines;
            CultureInfo ic = CultureInfo.InvariantCulture;

            lines.Add(SitesLogLinePrefix + " " + SpaceCenterFact
                + " body=" + ValueOrEmpty(payload, "body")
                + " lat=" + ValueOrEmpty(payload, "scLat")
                + " lon=" + ValueOrEmpty(payload, "scLon")
                + " alt=" + ValueOrEmpty(payload, "scAlt"));
            lines.Add(SitesLogLinePrefix + " " + RecoveryFact
                + " lat=" + ValueOrEmpty(payload, "recoveryLat")
                + " lon=" + ValueOrEmpty(payload, "recoveryLon"));

            for (int i = 0; i < MaxLaunchSitePoints; i++)
            {
                string prefix = "site" + i.ToString(ic);
                string name = ValueOrNull(payload, prefix);
                if (name == null)
                    break;
                var sb = new StringBuilder(SitesLogLinePrefix);
                sb.Append(' ').Append(LaunchSiteFact);
                sb.Append(" index=").Append(i.ToString(ic));
                sb.Append(" name='").Append(name).Append('\'');
                for (int k = 0; k < LaunchSiteLogLineKeys.Length; k++)
                {
                    string key = LaunchSiteLogLineKeys[k];
                    string value = ValueOrEmpty(payload, prefix + key);
                    sb.Append(' ').Append(key).Append('=');
                    if (key == "point")
                        sb.Append('\'').Append(value).Append('\'');
                    else
                        sb.Append(value);
                }
                lines.Add(sb.ToString());
            }

            int samples;
            if (!int.TryParse(ValueOrEmpty(payload, "samples"), NumberStyles.Integer, ic, out samples))
                samples = 0;
            for (int j = 0; j < samples; j++)
            {
                string prefix = "sample" + j.ToString(ic);
                var sb = new StringBuilder(SitesLogLinePrefix);
                sb.Append(' ').Append(TerrainFact);
                sb.Append(" index=").Append(j.ToString(ic));
                sb.Append(" name=").Append(ValueOrEmpty(payload, prefix));
                for (int k = 0; k < TerrainLogLineKeys.Length; k++)
                    sb.Append(' ').Append(TerrainLogLineKeys[k]).Append('=')
                      .Append(ValueOrEmpty(payload, prefix + TerrainLogLineKeys[k]));
                lines.Add(sb.ToString());
            }
            return lines;
        }

        private static bool IsFinite(double value)
            => !double.IsNaN(value) && !double.IsInfinity(value);

        private static string ValueOrNull(IReadOnlyList<KeyValuePair<string, string>> payload, string key)
        {
            for (int i = 0; i < payload.Count; i++)
                if (payload[i].Key == key)
                    return payload[i].Value ?? string.Empty;
            return null;
        }

        private static string ValueOrEmpty(IReadOnlyList<KeyValuePair<string, string>> payload, string key)
            => ValueOrNull(payload, key) ?? string.Empty;

        private static KeyValuePair<string, string> Kv(string key, string value)
            => new KeyValuePair<string, string>(key, value);
    }
}
