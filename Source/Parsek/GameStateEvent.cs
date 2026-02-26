using System;
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
        ReputationChanged    // 17
    }

    public struct GameStateEvent
    {
        public double ut;
        public GameStateEventType eventType;
        public string key;       // contract GUID, tech ID, kerbal name, facility ID
        public string detail;    // semicolon-separated extra info (optional)
        public double valueBefore;
        public double valueAfter;
        public uint epoch;       // branch epoch — incremented on revert, filters abandoned branches

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

            return e;
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
}
