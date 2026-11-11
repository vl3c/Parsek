using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Stateless formatting helpers for the recordings table.
    /// </summary>
    internal static class RecordingsTableFormatters
    {
        /// <summary>The start-position prefix for an EVA whose source vessel resolved.</summary>
        internal const string EvaFromPrefix = "EVA from ";

        internal static string FormatAltitude(double meters)
        {
            if (meters < 1000) return $"{(int)meters}m";
            if (meters < 1000000) return (meters / 1000).ToString("F1", CultureInfo.InvariantCulture) + "km";
            return (meters / 1000000).ToString("F1", CultureInfo.InvariantCulture) + "Mm";
        }

        internal static string FormatSpeed(double mps)
        {
            if (mps < 1000) return $"{(int)mps}m/s";
            return (mps / 1000).ToString("F1", CultureInfo.InvariantCulture) + "km/s";
        }

        internal static string FormatDistance(double meters)
        {
            if (meters < 1000) return $"{(int)meters}m";
            if (meters < 1000000) return (meters / 1000).ToString("F1", CultureInfo.InvariantCulture) + "km";
            return (meters / 1000000).ToString("F1", CultureInfo.InvariantCulture) + "Mm";
        }

        /// <summary>
        /// Formats the recording start position for the expanded stats column.
        /// Priority: launch site > EVA from vessel > situation + biome + body > biome + body > body.
        /// </summary>
        internal static string FormatStartPosition(Recording rec, string parentVesselName = null)
        {
            // EVA: show source vessel
            if (!string.IsNullOrEmpty(rec.EvaCrewName))
            {
                if (!string.IsNullOrEmpty(parentVesselName))
                    return EvaFromPrefix + parentVesselName;
                return FormatSituationLocation(rec.StartSituation, rec.StartBiome, rec.StartBodyName, "EVA");
            }

            // Launch from a site
            if (!string.IsNullOrEmpty(rec.LaunchSiteName))
                return !string.IsNullOrEmpty(rec.StartBodyName)
                    ? rec.LaunchSiteName + ", " + rec.StartBodyName
                    : rec.LaunchSiteName;

            // General: use situation + biome + body
            return FormatSituationLocation(rec.StartSituation, rec.StartBiome, rec.StartBodyName, null);
        }

        /// <summary>
        /// Formats the recording end position for the expanded stats column.
        /// Matches timeline style: "Orbiting {body}", "{biome}, {body}", "Boarded {vessel}".
        /// Body fallback priority for terminal recordings: TerminalOrbitBody -> StartBodyName.
        /// Body fallback priority for mid-segments: display body path -> SegmentBodyName
        /// -> last point body -> StartBodyName.
        /// </summary>
        internal static string FormatEndPosition(Recording rec, string parentVesselName = null)
        {
            if (!rec.TerminalStateValue.HasValue)
            {
                // No terminal state (chain mid-segment or interior tree recording).
                // Fallback: display body path -> SegmentBodyName -> last trajectory point body -> StartBodyName.
                string segBody = RecordingSegmentLabels.GetSegmentBodyDisplayLabel(rec);

                string segmentLabel = RecordingSegmentLabels.GetSegmentPhaseLabel(rec, segBody);
                if (!string.IsNullOrEmpty(segmentLabel))
                    return segmentLabel;
                if (!string.IsNullOrEmpty(segBody))
                    return segBody;
                return "-";
            }

            string body = rec.TerminalOrbitBody;
            if (string.IsNullOrEmpty(body) && !string.IsNullOrEmpty(rec.StartBodyName))
                body = rec.StartBodyName;

            switch (rec.TerminalStateValue.Value)
            {
                case TerminalState.Orbiting:
                    return !string.IsNullOrEmpty(body) ? "Orbiting " + body : "Orbiting";
                case TerminalState.Docked:
                    return !string.IsNullOrEmpty(body) ? "Docked, " + body : "Docked";

                case TerminalState.Landed:
                case TerminalState.Splashed:
                    if (!string.IsNullOrEmpty(rec.EndBiome) && !string.IsNullOrEmpty(body))
                        return rec.EndBiome + ", " + body;
                    if (!string.IsNullOrEmpty(body))
                        return body;
                    return rec.TerminalStateValue.Value.ToString();

                case TerminalState.Destroyed:
                    return !string.IsNullOrEmpty(body) ? "Destroyed, " + body : "Destroyed";
                case TerminalState.Recovered:
                    return !string.IsNullOrEmpty(body) ? "Recovered, " + body : "Recovered";
                case TerminalState.Disassembled:
                    return !string.IsNullOrEmpty(body) ? "Disassembled, " + body : "Disassembled";
                case TerminalState.SubOrbital:
                    return !string.IsNullOrEmpty(body) ? "SubOrbital, " + body : "SubOrbital";
                case TerminalState.Boarded:
                    return !string.IsNullOrEmpty(parentVesselName)
                        ? "Boarded " + parentVesselName
                        : "Boarded";

                default:
                    return "-";
            }
        }

        /// <summary>
        /// The "what this flight used and carried" clause of a recording row's Status hover,
        /// as ONE line (the Recordings help strip is single-line, so no hard newlines):
        /// "Used 3,400 Liquid Fuel, 4,150 Oxidizer; gained 200 Ore; unloaded 4 OX-STAT;
        /// crew -2 Engineer". Only entries that changed are named, and only when the recording
        /// has BOTH a start and an end manifest of that kind (a still-open recording has no end
        /// yet, so there is no change to report). Null when nothing changed. Resources and
        /// inventory parts are named through <paramref name="resourceTitle"/> /
        /// <paramref name="partTitle"/> (the live site passes the stock display names); a null
        /// resolver or a null / empty answer keeps the internal name. Pure for unit testing.
        /// </summary>
        internal static string FormatManifestChangeSummary(
            Dictionary<string, ResourceAmount> startResources,
            Dictionary<string, ResourceAmount> endResources,
            Dictionary<string, InventoryItem> startInventory,
            Dictionary<string, InventoryItem> endInventory,
            Dictionary<string, int> startCrew,
            Dictionary<string, int> endCrew,
            System.Func<string, string> resourceTitle = null,
            System.Func<string, string> partTitle = null)
        {
            var used = new List<string>();
            var gained = new List<string>();
            if (startResources != null && endResources != null)
            {
                foreach (var key in MergedKeys(startResources.Keys, endResources.Keys))
                {
                    double startAmt = startResources.TryGetValue(key, out var sa) ? sa.amount : 0;
                    double endAmt = endResources.TryGetValue(key, out var ea) ? ea.amount : 0;
                    double delta = endAmt - startAmt;
                    string amount = FormatManifestAmount(System.Math.Abs(delta));
                    if (amount == null) continue;
                    string entry = amount + " " + Resolve(resourceTitle, key);
                    (delta < 0 ? used : gained).Add(entry);
                }
            }

            var unloaded = new List<string>();
            var loaded = new List<string>();
            if (startInventory != null && endInventory != null)
            {
                foreach (var key in MergedKeys(startInventory.Keys, endInventory.Keys))
                {
                    int startCount = startInventory.TryGetValue(key, out var si) ? si.count : 0;
                    int endCount = endInventory.TryGetValue(key, out var ei) ? ei.count : 0;
                    int delta = endCount - startCount;
                    if (delta == 0) continue;
                    string entry = System.Math.Abs(delta).ToString(CultureInfo.InvariantCulture)
                        + " " + Resolve(partTitle, key);
                    (delta < 0 ? unloaded : loaded).Add(entry);
                }
            }

            var crew = new List<string>();
            if (startCrew != null && endCrew != null)
            {
                foreach (var key in MergedKeys(startCrew.Keys, endCrew.Keys))
                {
                    int startCount = startCrew.TryGetValue(key, out var sc) ? sc : 0;
                    int endCount = endCrew.TryGetValue(key, out var ec) ? ec : 0;
                    int delta = endCount - startCount;
                    if (delta == 0) continue;
                    crew.Add((delta > 0 ? "+" : "-")
                        + System.Math.Abs(delta).ToString(CultureInfo.InvariantCulture) + " " + key);
                }
            }

            var clauses = new List<string>();
            if (used.Count > 0) clauses.Add("used " + string.Join(", ", used));
            if (gained.Count > 0) clauses.Add("gained " + string.Join(", ", gained));
            if (unloaded.Count > 0) clauses.Add("unloaded " + string.Join(", ", unloaded));
            if (loaded.Count > 0) clauses.Add("loaded " + string.Join(", ", loaded));
            if (crew.Count > 0) clauses.Add("crew " + string.Join(", ", crew));
            if (clauses.Count == 0) return null;

            string text = string.Join("; ", clauses);
            return char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        /// <summary>
        /// A resource amount for the change summary: whole units with thousands separators at
        /// 100 and above, one decimal from 1, two below. Null when the amount rounds to zero
        /// at that precision (float noise on a tank that did not really change).
        /// </summary>
        internal static string FormatManifestAmount(double amount)
        {
            string text;
            if (amount >= 100)
                text = amount.ToString("N0", CultureInfo.InvariantCulture);
            else if (amount >= 1)
                text = amount.ToString("0.#", CultureInfo.InvariantCulture);
            else
                text = amount.ToString("0.##", CultureInfo.InvariantCulture);
            return text == "0" ? null : text;
        }

        private static SortedSet<string> MergedKeys(IEnumerable<string> a, IEnumerable<string> b)
        {
            var keys = new SortedSet<string>(System.StringComparer.Ordinal);
            foreach (var k in a) if (k != null) keys.Add(k);
            foreach (var k in b) if (k != null) keys.Add(k);
            return keys;
        }

        private static string Resolve(System.Func<string, string> title, string key)
        {
            if (title == null) return key;
            string resolved = title(key);
            return string.IsNullOrEmpty(resolved) ? key : resolved;
        }

        /// <summary>
        /// Formats situation + biome + body into a compact location string.
        /// "Flying, Shores, Kerbin" or "Orbiting, Kerbin" or "Kerbin" etc.
        /// </summary>
        private static string FormatSituationLocation(string situation, string biome, string body, string prefix)
        {
            bool hasSit = !string.IsNullOrEmpty(situation);
            bool hasBiome = !string.IsNullOrEmpty(biome);
            bool hasBody = !string.IsNullOrEmpty(body);

            string label = prefix ?? (hasSit ? situation : null);

            if (label != null && hasBiome && hasBody)
                return label + ", " + biome + ", " + body;
            if (label != null && hasBody)
                return label + ", " + body;
            if (hasBiome && hasBody)
                return biome + ", " + body;
            if (hasBody)
                return body;
            if (label != null)
                return label;
            return "-";
        }
    }
}
