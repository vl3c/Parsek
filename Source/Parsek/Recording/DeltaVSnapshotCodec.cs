using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    /// <summary>
    /// The optional <c>DELTAV</c> child node of a recording's metadata node: one
    /// <c>SNAPSHOT</c> value line per <see cref="DeltaVSnapshot"/>. Additive under
    /// design-save-format-policy.md (no format bump): an absent node means no snapshots, an
    /// empty list writes no node, so every recording without snapshots stays byte-identical.
    ///
    /// Line layout (invariant culture, doubles round-trip "R"):
    /// <c>ut,trigger,Ok,vacuum,seaLevel,actual,stages</c> where <c>stages</c> is
    /// <c>stage:vacuum:burnTime</c> items joined by <c>;</c> (empty when the vessel has no
    /// stage with delta-v), or <c>ut,trigger,Unknown</c> for a snapshot without figures.
    /// </summary>
    internal static class DeltaVSnapshotCodec
    {
        internal const string NodeName = "DELTAV";
        internal const string ValueName = "SNAPSHOT";

        private const string OkToken = "Ok";
        private const string UnknownToken = "Unknown";

        internal static void Serialize(ConfigNode recNode, List<DeltaVSnapshot> snapshots)
        {
            if (recNode == null || snapshots == null || snapshots.Count == 0)
                return;

            ConfigNode node = null;
            for (int i = 0; i < snapshots.Count; i++)
            {
                DeltaVSnapshot snapshot = snapshots[i];
                if (snapshot == null)
                    continue;
                if (node == null)
                    node = recNode.AddNode(NodeName);
                node.AddValue(ValueName, FormatLine(snapshot));
            }
        }

        /// <summary>
        /// Reads the snapshots back, or null when the node is absent or holds no readable
        /// line. A malformed line is skipped; one Warn per recording names the count.
        /// </summary>
        internal static List<DeltaVSnapshot> Deserialize(ConfigNode recNode, string recordingId)
        {
            ConfigNode node = recNode?.GetNode(NodeName);
            if (node == null)
                return null;

            string[] lines = node.GetValues(ValueName);
            List<DeltaVSnapshot> result = null;
            int skipped = 0;
            string firstBad = null;
            for (int i = 0; i < lines.Length; i++)
            {
                if (TryParseLine(lines[i], out DeltaVSnapshot snapshot))
                {
                    if (result == null)
                        result = new List<DeltaVSnapshot>(lines.Length);
                    result.Add(snapshot);
                }
                else
                {
                    skipped++;
                    if (firstBad == null)
                        firstBad = lines[i] ?? "(null)";
                }
            }

            if (skipped > 0)
            {
                ParsekLog.Warn("DeltaV",
                    string.Format(CultureInfo.InvariantCulture,
                        "Recording {0}: skipped {1} malformed {2} line(s) of {3} in {4} (first='{5}')",
                        recordingId ?? "(no id)", skipped, ValueName, lines.Length, NodeName,
                        firstBad));
            }

            return result;
        }

        internal static string FormatLine(DeltaVSnapshot snapshot)
        {
            var ic = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(64);
            sb.Append(snapshot.UT.ToString("R", ic));
            sb.Append(',');
            sb.Append(snapshot.Trigger.ToString());
            sb.Append(',');
            if (!snapshot.IsOk)
            {
                sb.Append(UnknownToken);
                return sb.ToString();
            }

            sb.Append(OkToken);
            sb.Append(',');
            sb.Append(snapshot.TotalVacuum.ToString("R", ic));
            sb.Append(',');
            sb.Append(snapshot.TotalSeaLevel.ToString("R", ic));
            sb.Append(',');
            sb.Append(snapshot.TotalActual.ToString("R", ic));
            sb.Append(',');
            for (int i = 0; i < snapshot.Stages.Count; i++)
            {
                DeltaVStageReading stage = snapshot.Stages[i];
                if (i > 0)
                    sb.Append(';');
                sb.Append(stage.Stage.ToString(ic));
                sb.Append(':');
                sb.Append(stage.VacuumDeltaV.ToString("R", ic));
                sb.Append(':');
                sb.Append(stage.BurnTimeSeconds.ToString("R", ic));
            }
            return sb.ToString();
        }

        internal static bool TryParseLine(string line, out DeltaVSnapshot snapshot)
        {
            snapshot = null;
            if (string.IsNullOrEmpty(line))
                return false;

            string[] fields = line.Split(',');
            if (fields.Length < 3)
                return false;
            if (!TryParseFiniteDouble(fields[0], out double ut))
                return false;
            if (!TryParseTrigger(fields[1], out DeltaVTrigger trigger))
                return false;

            string status = fields[2].Trim();
            if (status == UnknownToken)
            {
                if (fields.Length != 3)
                    return false;
                snapshot = DeltaVSnapshot.Unknown(ut, trigger);
                return true;
            }

            if (status != OkToken || fields.Length != 7)
                return false;
            if (!TryParseFiniteDouble(fields[3], out double vacuum)
                || !TryParseFiniteDouble(fields[4], out double seaLevel)
                || !TryParseFiniteDouble(fields[5], out double actual))
                return false;

            var stages = new List<DeltaVStageReading>();
            string stageText = fields[6].Trim();
            if (stageText.Length > 0)
            {
                string[] items = stageText.Split(';');
                for (int i = 0; i < items.Length; i++)
                {
                    string[] parts = items[i].Split(':');
                    if (parts.Length != 3)
                        return false;
                    if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out int stageNumber))
                        return false;
                    if (!TryParseFiniteDouble(parts[1], out double stageVacuum)
                        || !TryParseFiniteDouble(parts[2], out double burnTime))
                        return false;
                    stages.Add(new DeltaVStageReading(stageNumber, stageVacuum, burnTime));
                }
            }

            snapshot = new DeltaVSnapshot(
                ut, trigger, DeltaVSnapshotStatus.Ok, vacuum, seaLevel, actual, stages);
            return true;
        }

        private static bool TryParseTrigger(string text, out DeltaVTrigger trigger)
        {
            trigger = default(DeltaVTrigger);
            if (string.IsNullOrEmpty(text))
                return false;
            string name = text.Trim();
            // Names only: a bare number would read as a defined-or-not enum value.
            if (name.Length == 0 || char.IsDigit(name[0]) || name[0] == '-')
                return false;
            return Enum.TryParse(name, false, out trigger)
                && Enum.IsDefined(typeof(DeltaVTrigger), trigger);
        }

        private static bool TryParseFiniteDouble(string text, out double value)
        {
            if (text != null
                && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && !double.IsNaN(value)
                && !double.IsInfinity(value))
                return true;
            value = 0.0;
            return false;
        }
    }
}
