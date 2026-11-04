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
        KerbalRescued,       // 19
        StrategyActivated,   // 20
        StrategyDeactivated, // 21

        /// <summary>
        /// A recovered kerbal's flight log was archived into their career log, which is what
        /// stock derives experience from. Explicitly numbered and append-only: an older build
        /// loading a save with this row keeps it verbatim without reading it
        /// (<see cref="GameStateEvent.IsUnreadableTypeNode"/>), which costs the XP re-assert on
        /// that build and nothing else.
        /// </summary>
        ExperienceGained = 22,

        /// <summary>
        /// A resource scan unlocked a body (orbital survey) or one biome (surface scanner) in
        /// stock's <c>ResourceMap</c>. <c>key</c> is the body name; <c>detail</c> carries
        /// <c>biome=&lt;name&gt;</c> for a biome unlock and is empty for a whole-body survey.
        /// Explicitly numbered and append-only, like <see cref="ExperienceGained"/>.
        /// </summary>
        ResourceScanCompleted = 23,

        /// <summary>
        /// The Mobile Processing Lab credited science to the pool with no science subject
        /// (a lab transmission, or the lab's stored science on recovery). <c>key</c> is the
        /// stock reason (<c>ScienceTransmission</c> / <c>VesselRecovery</c>),
        /// <c>valueBefore</c> the pool before the call and <c>valueAfter</c> that pool plus the
        /// gross payout (before any converter strategy's take, which is its own ledger row), and
        /// <c>detail</c> carries <c>vessel=&lt;name&gt;</c>. Recorded beside the stock
        /// <see cref="ScienceChanged"/> event, which stays the reconcile's store side.
        /// Explicitly numbered and append-only, like <see cref="ExperienceGained"/>.
        /// </summary>
        ScienceLabCredited = 24,

        /// <summary>
        /// The player bought a part UPGRADE (a stock <c>PARTUPGRADE</c>) from the editor or
        /// R&amp;D part list tooltip. <c>key</c> is the upgrade name; <c>detail</c> carries
        /// <c>cost=</c> (what stock's <c>Funding.onUpgradePurchased</c> charged: 0 under
        /// bypass-entry-purchase or for a free upgrade) and <c>entryCost=</c> (the upgrade's
        /// price). Explicitly numbered and append-only, like <see cref="ExperienceGained"/>.
        /// </summary>
        PartUpgradePurchased = 25,

        /// <summary>
        /// One contract parameter paid its own completion reward (stock
        /// <c>ContractParameter.AwardCompletion</c>, reason <c>ContractReward</c>), separate from
        /// the contract-level reward <see cref="ContractCompleted"/> carries. <c>key</c> is the
        /// contract guid; <c>detail</c> carries <c>paramId=</c> (the parameter's index path in its
        /// contract, see <see cref="GameStateRecorder.BuildContractParameterIndexPath"/>),
        /// <c>fundsReward=</c> / <c>repReward=</c> / <c>sciReward=</c> and <c>title=</c> (the
        /// contract title). Explicitly numbered and append-only, like
        /// <see cref="ExperienceGained"/>.
        /// </summary>
        ContractParameterCompleted = 26,

        /// <summary>
        /// The player dismissed a crew member at the Astronaut Complex: stock's
        /// <c>KerbalRoster.SackAvailable</c> fired <c>OnCrewmemberSacked</c> (the kerbal becomes an
        /// applicant). <c>key</c> is the kerbal name; <c>detail</c> carries <c>trait=</c>. Not
        /// <see cref="CrewRemoved"/>, which is stock's <c>KerbalRoster.Remove</c> (applicant
        /// expiry, the Lost list's dead removal). Explicitly numbered and append-only, like
        /// <see cref="ExperienceGained"/>.
        /// </summary>
        CrewDismissed = 27
    }

    public struct GameStateEvent
    {
        public double ut;
        public GameStateEventType eventType;
        public string key;       // contract GUID, tech ID, kerbal name, facility ID
        public string detail;    // semicolon-separated extra info (optional)
        public double valueBefore;
        public double valueAfter;
        public string recordingId; // #431: id of the flight recording that captured this event; empty for career-level events
        /// <summary>Real-time capture order (<see cref="CaptureSequence"/>), stamped by
        /// <see cref="GameStateStore.AddEvent"/>; 0 for an event from an older save.</summary>
        public long captureSequence;

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
            if (!string.IsNullOrEmpty(recordingId))
                node.AddValue("recordingId", recordingId);
            if (captureSequence > 0)
                node.AddValue(CaptureSequence.Key, captureSequence.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// True when this build cannot read <paramref name="node"/>'s event type: the <c>type</c>
        /// value is missing, unparseable, or an id this enum does not define (a newer build's
        /// type). Such a node is kept verbatim by its store and written back on save; it is never
        /// deserialized, so no converter, ledger, reconcile or UI path sees it. Pure.
        /// </summary>
        internal static bool IsUnreadableTypeNode(ConfigNode node)
        {
            string typeStr = node?.GetValue("type");
            if (typeStr == null)
                return true;
            return !int.TryParse(typeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int typeInt)
                || !Enum.IsDefined(typeof(GameStateEventType), typeInt);
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

            // Pre-#431 saves have no recordingId; treat as untagged career events.
            e.recordingId = node.GetValue("recordingId") ?? "";
            // Absent on an older save: no capture order (CaptureSequence).
            e.captureSequence = CaptureSequence.Parse(node.GetValue(CaptureSequence.Key));

            return e;
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
                case GameStateEventType.ContractParameterCompleted:
                case GameStateEventType.ContractFailed:
                case GameStateEventType.ContractCancelled:
                case GameStateEventType.ContractDeclined:
                    return "Contract";
                case GameStateEventType.TechResearched:
                    return "Tech";
                case GameStateEventType.PartPurchased:
                    return "Part";
                case GameStateEventType.PartUpgradePurchased:
                    return "Upgrade";
                case GameStateEventType.CrewHired:
                case GameStateEventType.CrewRemoved:
                case GameStateEventType.CrewDismissed:
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
                case GameStateEventType.ScienceLabCredited:
                    return "Science";
                case GameStateEventType.ReputationChanged:
                    return "Reputation";
                case GameStateEventType.MilestoneAchieved:
                    return "Milestone";
                case GameStateEventType.StrategyActivated:
                case GameStateEventType.StrategyDeactivated:
                    return "Strategy";
                case GameStateEventType.ResourceScanCompleted:
                    return "Scan";
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
                case GameStateEventType.ContractParameterCompleted:
                    return FormatContractDescription(key, detail, "objective done");
                case GameStateEventType.ContractFailed:
                    return FormatContractDescription(key, detail, "failed");
                case GameStateEventType.ContractCancelled:
                    return FormatContractDescription(key, detail,
                        ExtractDetailField(detail, GameStateRecorder.ContractRequirementsLostDetailKey)
                            == GameStateRecorder.ContractRequirementsLostDetailValue
                            ? "ended (requirements no longer met)"
                            : "cancelled");
                case GameStateEventType.ContractDeclined:
                    return FormatContractDescription(key, detail, "declined");
                case GameStateEventType.TechResearched:
                {
                    if (ExtractDetailField(detail, GameStateRecorder.FreeTechUnlockDetailKey) == "1")
                        return $"\"{key}\" (free)";
                    string cost = ExtractDetailField(detail, "cost");
                    return cost != null ? $"\"{key}\" ({cost} sci)" : $"\"{key}\"";
                }
                case GameStateEventType.PartPurchased:
                {
                    string cost = ExtractDetailField(detail, "cost");
                    return cost != null ? $"\"{key}\" ({cost} funds)" : $"\"{key}\"";
                }
                case GameStateEventType.PartUpgradePurchased:
                {
                    string cost = ExtractDetailField(detail, "cost");
                    return cost != null ? $"Part upgrade \"{key}\" ({cost} funds)" : $"Part upgrade \"{key}\"";
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
                case GameStateEventType.CrewDismissed:
                {
                    string trait = ExtractDetailField(detail, "trait");
                    return trait != null ? $"Dismissed {key} ({trait})" : $"Dismissed {key}";
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
                case GameStateEventType.StrategyActivated:
                {
                    string title = ExtractDetailField(detail, "title");
                    return title != null ? $"\"{title}\" activated" : $"\"{key}\" activated";
                }
                case GameStateEventType.StrategyDeactivated:
                {
                    string title = ExtractDetailField(detail, "title");
                    return title != null ? $"\"{title}\" deactivated" : $"\"{key}\" deactivated";
                }
                case GameStateEventType.ScienceLabCredited:
                {
                    var ic = CultureInfo.InvariantCulture;
                    double credit = e.valueAfter - e.valueBefore;
                    string surveyed = ExtractDetailField(detail, "survey");
                    if (!string.IsNullOrEmpty(surveyed))
                        return $"Orbital survey of {surveyed}: +{credit.ToString("N1", ic)}";
                    string how = string.Equals(key, "VesselRecovery", StringComparison.Ordinal)
                        ? "recovered" : "transmitted";
                    return $"Lab science {how}: +{credit.ToString("N1", ic)}";
                }
                case GameStateEventType.ResourceScanCompleted:
                {
                    string biome = ExtractDetailField(detail, "biome");
                    return string.IsNullOrEmpty(biome)
                        ? $"Resource scan: {key}"
                        : $"Resource scan: {key} ({biome})";
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

        /// <summary>
        /// Planetarium UT the snapshot was taken at. Accept-time snapshots carry the
        /// accept UT; rewind-point snapshots carry the RP's own quicksave UT. A row with
        /// no <c>ut</c> key reads as 0, which the selection rule treats as "oldest possible".
        /// </summary>
        public double ut;

        /// <summary>
        /// The <c>RewindPoint.RewindPointId</c> this snapshot was captured for, or null
        /// for the accept-time snapshot. Purged with its RP by the reaper.
        /// </summary>
        public string sourceRpId;

        public void SerializeInto(ConfigNode parentNode)
        {
            ConfigNode snapNode = parentNode.AddNode("CONTRACT_SNAPSHOT");
            snapNode.AddValue("guid", contractGuid ?? "");
            snapNode.AddValue("ut", ut.ToString("R", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(sourceRpId))
                snapNode.AddValue("sourceRpId", sourceRpId);
            if (contractNode != null)
                snapNode.AddNode(contractNode);
        }

        public static ContractSnapshot DeserializeFrom(ConfigNode snapNode)
        {
            var snap = new ContractSnapshot();
            snap.contractGuid = snapNode.GetValue("guid") ?? "";

            // Both keys are optional; absent, they read as the accept-time shape.
            string utStr = snapNode.GetValue("ut");
            double parsedUt;
            if (!string.IsNullOrEmpty(utStr) &&
                double.TryParse(utStr, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedUt))
            {
                snap.ut = parsedUt;
            }

            string rpId = snapNode.GetValue("sourceRpId");
            snap.sourceRpId = string.IsNullOrEmpty(rpId) ? null : rpId;

            ConfigNode contractChild = snapNode.GetNode("CONTRACT");
            if (contractChild != null)
                snap.contractNode = contractChild;
            return snap;
        }
    }

    public struct PendingScienceSubject
    {
        public string subjectId;

        /// <summary>
        /// Subject science this ONE submission added, in PRE-multiplier subject units
        /// (stock's <c>scienceValue</c> before <c>ScienceGainMultiplier</c>). The ledger walk
        /// sums <c>ScienceEarning</c> rows as increments, so this must never be the running
        /// <c>subject.science</c> total: a repeated collection of one subject would then be
        /// credited again for every earlier submission.
        /// </summary>
        public float science;
        public float subjectMaxValue;
        public double captureUT;
        public string reasonKey;
        public string recordingId;

        /// <summary>
        /// <c>Career.ScienceGainMultiplier</c> in effect when stock credited this subject.
        /// <see cref="science"/> is the PRE-multiplier subject value (stock adds to
        /// <c>subject.science</c> before it multiplies); the pool received
        /// <c>science * scienceGainMultiplier</c>. 0 (the struct default) means "not
        /// captured" and reads as 1 through <see cref="GameAction.NormalizeScienceGainMultiplier"/>.
        /// </summary>
        public float scienceGainMultiplier;

        /// <summary>
        /// Identity of the ONE stock callback that produced this subject, minted at capture
        /// (<see cref="GameStateRecorder.CaptureScienceSubject"/>) in the
        /// <see cref="GameAction.ActionId"/> format. The <c>ScienceEarning</c> row converted
        /// from it takes this as its <c>ActionId</c>, so a later re-presentation of the same
        /// capture (a commit retry, a discard re-home, a second routing) carries the id of
        /// the row already filed, while a second genuine award of the same subject at the
        /// same instant carries a fresh one (SCIENCE-SAME-SUBJECT-SAME-INSTANT-ROW-DROPPED).
        /// Null on a subject built anywhere else; such a subject keeps the legacy
        /// subject + capture-moment dedup. In memory only, like the pending list itself;
        /// the row's <c>ActionId</c> is what persists.
        /// </summary>
        public string captureActionId;
    }
}
