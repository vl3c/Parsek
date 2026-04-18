using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    public enum GameStateEventType
    {
        ContractOffered,     // 0
        ContractAccepted,    // 1
        ContractCompleted,   // 2
        ContractFailed,      // 3
        ContractCancelled,   // 4
        ContractDeclined,    // 5
        TechResearched,      // 6
        PartPurchased,       // 7
        CrewHired,           // 8
        CrewRemoved,         // 9
        CrewStatusChanged,   // 10
        FacilityUpgraded,    // 11
        FacilityDowngraded,  // 12
        BuildingDestroyed,   // 13
        BuildingRepaired,    // 14
        FundsChanged,        // 15
        ScienceChanged,      // 16
        ReputationChanged,   // 17
        MilestoneAchieved,   // 18
        KerbalRescued        // 19
    }

    public struct GameStateEvent
    {
        private const double LegacyPartPurchaseCompatUtEpsilon = 0.1;
        private const double LegacyPartPurchaseCompatFundsTolerance = 1.0;
        private const double LegacyPartPurchaseCompatAmountTolerance = 0.01;

        public double ut;
        public GameStateEventType eventType;
        public string key;       // contract GUID, tech ID, kerbal name, facility ID
        public string detail;    // semicolon-separated extra info (optional)
        public double valueBefore;
        public double valueAfter;
        public uint epoch;       // branch epoch — incremented on revert, filters abandoned branches
        public string recordingId; // #431: id of the flight recording that captured this event; empty for career-level events

        public void SerializeInto(ConfigNode node)
        {
            node.AddValue("ut", ut.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("type", (int)eventType);
            if (!string.IsNullOrEmpty(key))
                node.AddValue("key", key);
            if (!string.IsNullOrEmpty(detail))
                node.AddValue("detail", detail);
            if (valueBefore != 0)
                node.AddValue("valBefore", valueBefore.ToString("R", CultureInfo.InvariantCulture));
            if (valueAfter != 0)
                node.AddValue("valAfter", valueAfter.ToString("R", CultureInfo.InvariantCulture));
            if (epoch != 0)
                node.AddValue("epoch", epoch.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(recordingId))
                node.AddValue("recordingId", recordingId);
        }

        public static GameStateEvent DeserializeFrom(ConfigNode node)
        {
            var e = new GameStateEvent();

            string utStr = node.GetValue("ut");
            if (utStr != null)
            {
                if (!double.TryParse(utStr, NumberStyles.Float, CultureInfo.InvariantCulture, out e.ut))
                    ParsekLog.Warn("GameStateEvent", $"Failed to parse 'ut' value '{utStr}'");
            }

            string typeStr = node.GetValue("type");
            if (typeStr != null)
            {
                int typeInt;
                if (int.TryParse(typeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out typeInt))
                {
                    if (Enum.IsDefined(typeof(GameStateEventType), typeInt))
                    {
                        e.eventType = (GameStateEventType)typeInt;
                    }
                    else
                    {
                        ParsekLog.Warn("GameStateEvent", $"Unknown event type id '{typeInt}' while deserializing");
                    }
                }
                else
                {
                    ParsekLog.Warn("GameStateEvent", $"Failed to parse event type value '{typeStr}'");
                }
            }

            e.key = node.GetValue("key") ?? "";
            e.detail = node.GetValue("detail") ?? "";

            string valBeforeStr = node.GetValue("valBefore");
            if (valBeforeStr != null)
            {
                if (!double.TryParse(valBeforeStr, NumberStyles.Float, CultureInfo.InvariantCulture, out e.valueBefore))
                    ParsekLog.Warn("GameStateEvent", $"Failed to parse valBefore '{valBeforeStr}'");
            }

            string valAfterStr = node.GetValue("valAfter");
            if (valAfterStr != null)
            {
                if (!double.TryParse(valAfterStr, NumberStyles.Float, CultureInfo.InvariantCulture, out e.valueAfter))
                    ParsekLog.Warn("GameStateEvent", $"Failed to parse valAfter '{valAfterStr}'");
            }

            string epochStr = node.GetValue("epoch");
            if (epochStr != null)
                uint.TryParse(epochStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out e.epoch);

            // Pre-#431 saves have no recordingId; treat as untagged career events.
            e.recordingId = node.GetValue("recordingId") ?? "";

            return e;
        }

        /// <summary>
        /// #451 follow-up: rewrites the predecessor commit's bad bypass=true auto-unlock
        /// shape (<c>cost=&lt;entryCost&gt;;entryCost=&lt;entryCost&gt;</c>) back to a free
        /// purchase on load. The migration is intentionally narrow:
        /// <list type="bullet">
        /// <item><description>the PartPurchased row must have both tokens with the same positive value;</description></item>
        /// <item><description>its numeric before/after delta must match that value (the predecessor also wrote the wrong charged delta there);</description></item>
        /// <item><description>a same-batch TechResearched row must list this part in <c>parts=</c> at the same UT window; and</description></item>
        /// <item><description>no same-batch FundsChanged(<c>RnDPartPurchase</c>) debit may exist, which distinguishes the free bypass path from a real paid unlock.</description></item>
        /// </list>
        /// This covers both live event-store loads and committed milestone loads without
        /// relying on the player's current difficulty setting.
        /// </summary>
        internal static int NormalizeLegacyPartPurchaseCostsForLoad(
            List<GameStateEvent> events, string sourceLabel)
        {
            if (events == null || events.Count == 0)
                return 0;

            int migrated = 0;
            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                string entryCostStr;
                double entryCost;
                if (!IsLegacyFreePartPurchaseCandidate(evt, out entryCostStr, out entryCost))
                    continue;

                if (!HasNearbyTechResearchedAutoUnlock(events, evt.ut, evt.key))
                    continue;

                if (HasMatchingRnDPartPurchaseDebit(events, evt.ut, entryCost))
                    continue;

                evt.detail = ReplaceDetailFieldValue(evt.detail, "cost", "0");
                evt.valueBefore = evt.valueAfter;
                events[i] = evt;
                migrated++;

                ParsekLog.Info("GameStateEvent",
                    $"NormalizeLegacyPartPurchaseCostsForLoad: rewrote legacy free unlock " +
                    $"part='{evt.key}' entryCost={entryCostStr} source='{sourceLabel ?? "(unknown)"}' " +
                    $"ut={evt.ut.ToString("R", CultureInfo.InvariantCulture)}");
            }

            return migrated;
        }

        private static bool IsLegacyFreePartPurchaseCandidate(
            GameStateEvent evt, out string entryCostStr, out double entryCost)
        {
            entryCostStr = null;
            entryCost = 0;

            if (evt.eventType != GameStateEventType.PartPurchased ||
                string.IsNullOrEmpty(evt.detail))
                return false;

            string costStr = GameStateEventDisplay.ExtractDetailField(evt.detail, "cost");
            entryCostStr = GameStateEventDisplay.ExtractDetailField(evt.detail, "entryCost");
            if (string.IsNullOrEmpty(costStr) || string.IsNullOrEmpty(entryCostStr))
                return false;

            double cost;
            if (!double.TryParse(costStr, NumberStyles.Float, CultureInfo.InvariantCulture, out cost) ||
                !double.TryParse(entryCostStr, NumberStyles.Float, CultureInfo.InvariantCulture, out entryCost))
                return false;

            if (cost <= 0 || entryCost <= 0)
                return false;

            if (Math.Abs(cost - entryCost) > LegacyPartPurchaseCompatAmountTolerance)
                return false;

            double observedDelta = evt.valueBefore - evt.valueAfter;
            if (Math.Abs(observedDelta - entryCost) > LegacyPartPurchaseCompatFundsTolerance)
                return false;

            return true;
        }

        private static bool HasNearbyTechResearchedAutoUnlock(
            List<GameStateEvent> events, double partPurchaseUt, string partName)
        {
            if (string.IsNullOrEmpty(partName))
                return false;

            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (evt.eventType != GameStateEventType.TechResearched)
                    continue;

                if (Math.Abs(evt.ut - partPurchaseUt) > LegacyPartPurchaseCompatUtEpsilon)
                    continue;

                string parts = GameStateEventDisplay.ExtractDetailField(evt.detail, "parts");
                if (string.IsNullOrEmpty(parts))
                    continue;

                string[] unlockedParts = parts.Split(',');
                for (int j = 0; j < unlockedParts.Length; j++)
                {
                    if (string.Equals(unlockedParts[j].Trim(), partName, StringComparison.Ordinal))
                        return true;
                }
            }

            return false;
        }

        private static bool HasMatchingRnDPartPurchaseDebit(
            List<GameStateEvent> events, double partPurchaseUt, double expectedEntryCost)
        {
            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (evt.eventType != GameStateEventType.FundsChanged ||
                    !string.Equals(evt.key, "RnDPartPurchase", StringComparison.Ordinal))
                    continue;

                if (Math.Abs(evt.ut - partPurchaseUt) > LegacyPartPurchaseCompatUtEpsilon)
                    continue;

                double observedDelta = evt.valueAfter - evt.valueBefore;
                if (Math.Abs(observedDelta + expectedEntryCost) <= LegacyPartPurchaseCompatFundsTolerance)
                    return true;
            }

            return false;
        }

        private static string ReplaceDetailFieldValue(string detail, string fieldName, string newValue)
        {
            if (string.IsNullOrEmpty(detail))
                return detail;

            string prefix = fieldName + "=";
            string[] parts = detail.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string trimmed = parts[i].Trim();
                if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                    parts[i] = prefix + newValue;
            }

            return string.Join(";", parts);
        }
    }

    internal static class GameStateEventDisplay
    {
        internal static string GetDisplayCategory(GameStateEventType type)
        {
            switch (type)
            {
                case GameStateEventType.ContractOffered:
                case GameStateEventType.ContractAccepted:
                case GameStateEventType.ContractCompleted:
                case GameStateEventType.ContractFailed:
                case GameStateEventType.ContractCancelled:
                case GameStateEventType.ContractDeclined:
                    return "Contract";
                case GameStateEventType.TechResearched:
                    return "Tech";
                case GameStateEventType.PartPurchased:
                    return "Part";
                case GameStateEventType.CrewHired:
                case GameStateEventType.CrewRemoved:
                case GameStateEventType.CrewStatusChanged:
                case GameStateEventType.KerbalRescued:
                    return "Crew";
                case GameStateEventType.FacilityUpgraded:
                    return "Upgrade";
                case GameStateEventType.FacilityDowngraded:
                    return "Downgrade";
                case GameStateEventType.BuildingDestroyed:
                case GameStateEventType.BuildingRepaired:
                    return "Building";
                case GameStateEventType.FundsChanged:
                    return "Funds";
                case GameStateEventType.ScienceChanged:
                    return "Science";
                case GameStateEventType.ReputationChanged:
                    return "Reputation";
                case GameStateEventType.MilestoneAchieved:
                    return "Milestone";
                default:
                    return "Event";
            }
        }

        internal static string GetDisplayDescription(GameStateEvent e)
        {
            string key = e.key ?? "";
            string detail = e.detail ?? "";

            switch (e.eventType)
            {
                case GameStateEventType.ContractOffered:
                    return FormatContractDescription(key, detail, "offered");
                case GameStateEventType.ContractAccepted:
                    return FormatContractDescription(key, detail, "accepted");
                case GameStateEventType.ContractCompleted:
                    return FormatContractDescription(key, detail, "completed");
                case GameStateEventType.ContractFailed:
                    return FormatContractDescription(key, detail, "failed");
                case GameStateEventType.ContractCancelled:
                    return FormatContractDescription(key, detail, "cancelled");
                case GameStateEventType.ContractDeclined:
                    return FormatContractDescription(key, detail, "declined");
                case GameStateEventType.TechResearched:
                {
                    string cost = ExtractDetailField(detail, "cost");
                    return cost != null ? $"\"{key}\" ({cost} sci)" : $"\"{key}\"";
                }
                case GameStateEventType.PartPurchased:
                {
                    string cost = ExtractDetailField(detail, "cost");
                    return cost != null ? $"\"{key}\" ({cost} funds)" : $"\"{key}\"";
                }
                case GameStateEventType.CrewHired:
                {
                    string trait = ExtractDetailField(detail, "trait");
                    return trait != null ? $"Hired {key} ({trait})" : $"Hired {key}";
                }
                case GameStateEventType.CrewRemoved:
                {
                    string trait = ExtractDetailField(detail, "trait");
                    return trait != null ? $"Removed {key} ({trait})" : $"Removed {key}";
                }
                case GameStateEventType.CrewStatusChanged:
                {
                    string from = ExtractDetailField(detail, "from");
                    string to = ExtractDetailField(detail, "to");
                    if (from != null && to != null)
                        return $"{key} {from} \u2192 {to}";
                    return $"{key} status changed";
                }
                case GameStateEventType.FacilityUpgraded:
                {
                    string levelInfo = FormatLevelChange(e.valueBefore, e.valueAfter);
                    return levelInfo != null ? $"\"{key}\" {levelInfo}" : $"\"{key}\"";
                }
                case GameStateEventType.FacilityDowngraded:
                {
                    string levelInfo = FormatLevelChange(e.valueBefore, e.valueAfter);
                    return levelInfo != null ? $"\"{key}\" {levelInfo}" : $"\"{key}\"";
                }
                case GameStateEventType.BuildingDestroyed:
                    return $"\"{key}\" destroyed";
                case GameStateEventType.BuildingRepaired:
                    return $"\"{key}\" repaired";
                case GameStateEventType.FundsChanged:
                case GameStateEventType.ScienceChanged:
                case GameStateEventType.ReputationChanged:
                {
                    var ic = CultureInfo.InvariantCulture;
                    double delta = e.valueAfter - e.valueBefore;
                    string sign = delta >= 0 ? "+" : "";
                    return $"{sign}{delta.ToString("N0", ic)} ({e.valueBefore.ToString("N0", ic)} \u2192 {e.valueAfter.ToString("N0", ic)})";
                }
                case GameStateEventType.MilestoneAchieved:
                    return $"\"{key}\" achieved";
                case GameStateEventType.KerbalRescued:
                {
                    string trait = ExtractDetailField(detail, "trait");
                    return trait != null ? $"Rescued {key} ({trait})" : $"Rescued {key}";
                }
                default:
                    return key;
            }
        }

        private static string FormatContractDescription(string key, string detail, string verb)
        {
            // detail may contain title as "title=Some Title;..."
            string title = ExtractDetailField(detail, "title");
            string display = title ?? key;
            return $"\"{display}\" {verb}";
        }

        private static string FormatLevelChange(double before, double after)
        {
            if (before == 0 && after == 0)
                return null;
            // KSP facility levels are normalized 0-1; convert to integer-ish levels
            int lvBefore = (int)System.Math.Round(before * 2);
            int lvAfter = (int)System.Math.Round(after * 2);
            return $"(lv {lvBefore} \u2192 {lvAfter})";
        }

        internal static string ExtractDetailField(string detail, string fieldName)
        {
            if (string.IsNullOrEmpty(detail))
                return null;

            string prefix = fieldName + "=";
            int startIdx = -1;

            // Check if detail starts with the field
            if (detail.StartsWith(prefix, System.StringComparison.Ordinal))
            {
                startIdx = prefix.Length;
            }
            else
            {
                // Look for ";fieldName="
                string semicolonPrefix = ";" + prefix;
                int pos = detail.IndexOf(semicolonPrefix, System.StringComparison.Ordinal);
                if (pos >= 0)
                    startIdx = pos + semicolonPrefix.Length;
            }

            if (startIdx < 0)
                return null;

            int endIdx = detail.IndexOf(';', startIdx);
            return endIdx >= 0 ? detail.Substring(startIdx, endIdx - startIdx) : detail.Substring(startIdx);
        }
    }

    public struct ContractSnapshot
    {
        public string contractGuid;
        public ConfigNode contractNode;

        public void SerializeInto(ConfigNode parentNode)
        {
            ConfigNode snapNode = parentNode.AddNode("CONTRACT_SNAPSHOT");
            snapNode.AddValue("guid", contractGuid ?? "");
            if (contractNode != null)
                snapNode.AddNode(contractNode);
        }

        public static ContractSnapshot DeserializeFrom(ConfigNode snapNode)
        {
            var snap = new ContractSnapshot();
            snap.contractGuid = snapNode.GetValue("guid") ?? "";
            ConfigNode contractChild = snapNode.GetNode("CONTRACT");
            if (contractChild != null)
                snap.contractNode = contractChild;
            return snap;
        }
    }

    public struct PendingScienceSubject
    {
        public string subjectId;
        public float science;
        public float subjectMaxValue;
    }
}
