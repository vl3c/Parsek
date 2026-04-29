using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Stateless formatting helpers for the recordings table.
    /// </summary>
    internal static class RecordingsTableFormatters
    {
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
                    return "EVA from " + parentVesselName;
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
        /// Body fallback priority for mid-segments: SegmentBodyName -> last point body -> StartBodyName.
        /// </summary>
        internal static string FormatEndPosition(Recording rec, string parentVesselName = null)
        {
            if (!rec.TerminalStateValue.HasValue)
            {
                // No terminal state (chain mid-segment or interior tree recording).
                // Fallback: SegmentBodyName -> last trajectory point body -> StartBodyName.
                string segBody = rec.SegmentBodyName;
                if (string.IsNullOrEmpty(segBody) && rec.Points != null && rec.Points.Count > 0)
                    segBody = rec.Points[rec.Points.Count - 1].bodyName;
                if (string.IsNullOrEmpty(segBody))
                    segBody = rec.StartBodyName;

                string segmentLabel = RecordingStore.GetSegmentPhaseLabel(rec);
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
        /// Formats a resource manifest for tooltip display.
        /// If both start and end: "Resources:\n  LiquidFuel: 3600.0 -> 200.0 (-3400.0)"
        /// If start only: "Resources at start:\n  LiquidFuel: 3600.0 / 3600.0"
        /// If both null: returns null (no section shown).
        /// </summary>
        internal static string FormatResourceManifest(
            Dictionary<string, ResourceAmount> start,
            Dictionary<string, ResourceAmount> end)
        {
            if (start == null && end == null)
                return null;

            var keys = new SortedSet<string>();
            if (start != null)
                foreach (var k in start.Keys) keys.Add(k);
            if (end != null)
                foreach (var k in end.Keys) keys.Add(k);

            if (keys.Count == 0)
                return null;

            bool hasEnd = end != null;
            var lines = new List<string>();
            lines.Add(hasEnd ? "Resources:" : "Resources at start:");

            foreach (var key in keys)
            {
                if (hasEnd)
                {
                    double startAmt = 0;
                    double endAmt = 0;
                    if (start != null && start.TryGetValue(key, out var startRa))
                        startAmt = startRa.amount;
                    if (end.TryGetValue(key, out var endRa))
                        endAmt = endRa.amount;

                    double delta = endAmt - startAmt;
                    string sign = delta >= 0 ? "+" : "";
                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "  {0}: {1:F1} \u2192 {2:F1} ({3}{4:F1})",
                        key,
                        startAmt,
                        endAmt,
                        sign,
                        delta));
                }
                else
                {
                    double amt = 0;
                    double max = 0;
                    if (start.TryGetValue(key, out var ra))
                    {
                        amt = ra.amount;
                        max = ra.maxAmount;
                    }
                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "  {0}: {1:F1} / {2:F1}",
                        key, amt, max));
                }
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Formats an inventory manifest for tooltip display.
        /// If both start and end: "Inventory:\n  solarPanels5: 4 -> 0 (-4)"
        /// If start only: "Inventory at start:\n  solarPanels5: 4"
        /// If both null: returns null (no section shown).
        /// </summary>
        internal static string FormatInventoryManifest(
            Dictionary<string, InventoryItem> start,
            Dictionary<string, InventoryItem> end)
        {
            if (start == null && end == null)
                return null;

            var keys = new SortedSet<string>();
            if (start != null)
                foreach (var k in start.Keys) keys.Add(k);
            if (end != null)
                foreach (var k in end.Keys) keys.Add(k);

            if (keys.Count == 0)
                return null;

            bool hasEnd = end != null;
            var lines = new List<string>();
            lines.Add(hasEnd ? "Inventory:" : "Inventory at start:");

            foreach (var key in keys)
            {
                if (hasEnd)
                {
                    int startCount = 0;
                    int endCount = 0;
                    if (start != null && start.TryGetValue(key, out var startItem))
                        startCount = startItem.count;
                    if (end.TryGetValue(key, out var endItem))
                        endCount = endItem.count;

                    int delta = endCount - startCount;
                    string sign = delta >= 0 ? "+" : "";
                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "  {0}: {1} \u2192 {2} ({3}{4})",
                        key,
                        startCount,
                        endCount,
                        sign,
                        delta));
                }
                else
                {
                    int count = 0;
                    if (start.TryGetValue(key, out var item))
                        count = item.count;
                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "  {0}: {1}",
                        key, count));
                }
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Formats a crew manifest for tooltip display.
        /// If both start and end: "Crew:\n  Pilot: 1 -> 1 (+0)\n  Engineer: 2 -> 0 (-2)"
        /// If start only: "Crew at start:\n  Pilot: 1\n  Engineer: 2"
        /// If both null: returns null (no section shown).
        /// </summary>
        internal static string FormatCrewManifest(
            Dictionary<string, int> start,
            Dictionary<string, int> end)
        {
            if (start == null && end == null)
                return null;

            var keys = new SortedSet<string>();
            if (start != null)
                foreach (var k in start.Keys) keys.Add(k);
            if (end != null)
                foreach (var k in end.Keys) keys.Add(k);

            if (keys.Count == 0)
                return null;

            bool hasEnd = end != null;
            var lines = new List<string>();
            lines.Add(hasEnd ? "Crew:" : "Crew at start:");

            foreach (var key in keys)
            {
                if (hasEnd)
                {
                    int startCount = 0;
                    int endCount = 0;
                    if (start != null && start.TryGetValue(key, out var sc))
                        startCount = sc;
                    if (end.TryGetValue(key, out var ec))
                        endCount = ec;

                    int delta = endCount - startCount;
                    string sign = delta >= 0 ? "+" : "";
                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "  {0}: {1} \u2192 {2} ({3}{4})",
                        key,
                        startCount,
                        endCount,
                        sign,
                        delta));
                }
                else
                {
                    int count = 0;
                    if (start.TryGetValue(key, out var sc))
                        count = sc;
                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "  {0}: {1}",
                        key, count));
                }
            }

            return string.Join("\n", lines);
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
