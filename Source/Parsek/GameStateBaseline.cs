using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Contracts;

namespace Parsek
{
    public class GameStateBaseline
    {
        public double ut;
        public double funds;
        public double science;
        public float reputation;

        // Whether science / reputation above are the career's real values at capture. A
        // cold Space Center load runs ParsekScenario.OnLoad BEFORE the R&D and Reputation
        // scenario modules load (a KSP-written career saves them after ParsekScenario), so
        // their singletons are still missing and both fields read 0 even when the save holds
        // 400 science / 25 reputation. False: the game mode creates that singleton and it
        // had not loaded, so the field is no reading. True: a real reading, or a mode with no
        // such singleton (its 0 IS the value). Null: not recorded (a baseline written before
        // these keys existed, or built in code); see DecideCurrencyValueKnown.
        public bool? scienceKnown;
        public bool? reputationKnown;

        public List<string> researchedTechIds = new List<string>();
        public Dictionary<string, float> facilityLevels = new Dictionary<string, float>();
        public Dictionary<string, bool> buildingIntact = new Dictionary<string, bool>();
        public List<ConfigNode> activeContracts = new List<ConfigNode>();
        public List<CrewEntry> crewEntries = new List<CrewEntry>();

        public struct CrewEntry
        {
            public string name;
            public string status; // Available, Assigned, Dead, Missing
            public string trait;  // Pilot, Engineer, Scientist, Tourist

            public void SerializeInto(ConfigNode node)
            {
                node.AddValue("name", name ?? "");
                node.AddValue("status", status ?? "");
                node.AddValue("trait", trait ?? "");
            }

            public static CrewEntry DeserializeFrom(ConfigNode node)
            {
                return new CrewEntry
                {
                    name = node.GetValue("name") ?? "",
                    status = node.GetValue("status") ?? "",
                    trait = node.GetValue("trait") ?? ""
                };
            }
        }

        public static GameStateBaseline CaptureCurrentState()
        {
            var baseline = new GameStateBaseline();
            baseline.ut = Planetarium.GetUniversalTime();

            // Funds, Science, Reputation
            if (Funding.Instance != null)
                baseline.funds = Funding.Instance.Funds;
            if (ResearchAndDevelopment.Instance != null)
                baseline.science = ResearchAndDevelopment.Instance.Science;
            if (Reputation.Instance != null)
                baseline.reputation = Reputation.Instance.reputation;

            var expectedCurrencies = CurrencyScenarioReadiness.ExpectedForCurrentGame();
            var loadedCurrencies = CurrencyScenarioReadiness.LoadedNow();
            baseline.scienceKnown = DecideCurrencyValueCaptured(
                expectedCurrencies, loadedCurrencies, CurrencySingletons.ResearchAndDevelopment);
            baseline.reputationKnown = DecideCurrencyValueCaptured(
                expectedCurrencies, loadedCurrencies, CurrencySingletons.Reputation);

            // Tech tree — iterate all tech nodes and record the unlocked ones
            if (ResearchAndDevelopment.Instance != null && AssetBase.RnDTechTree != null &&
                AssetBase.RnDTechTree.GetTreeTechs() != null)
            {
                foreach (var tech in AssetBase.RnDTechTree.GetTreeTechs())
                {
                    if (tech != null && !string.IsNullOrEmpty(tech.techID) &&
                        ResearchAndDevelopment.GetTechnologyState(tech.techID) == RDTech.State.Available)
                    {
                        baseline.researchedTechIds.Add(tech.techID);
                    }
                }
            }

            // Facility levels
            if (ScenarioUpgradeableFacilities.protoUpgradeables != null)
            {
                foreach (var kvp in ScenarioUpgradeableFacilities.protoUpgradeables)
                {
                    if (kvp.Value != null && kvp.Value.facilityRefs != null && kvp.Value.facilityRefs.Count > 0)
                    {
                        var facility = kvp.Value.facilityRefs[0];
                        if (facility != null)
                            baseline.facilityLevels[kvp.Key] = facility.GetNormLevel();
                    }
                }
            }

            // Building intact states — best-effort, only populated when DestructibleBuilding
            // objects are loaded (KSC scene). Empty in Flight/Editor/Tracking Station.
            var destructibles = Object.FindObjectsOfType<DestructibleBuilding>();
            if (destructibles != null)
            {
                foreach (var db in destructibles)
                {
                    if (db != null && !string.IsNullOrEmpty(db.id))
                        baseline.buildingIntact[db.id] = !db.IsDestroyed;
                }
            }
            if (baseline.buildingIntact.Count == 0)
            {
                ParsekLog.Verbose("Baseline",
                    $"Captured baseline at ut={baseline.ut:F0} with no building state entries (scene={HighLogic.LoadedScene})");
            }

            // Active contracts
            if (ContractSystem.Instance != null)
            {
                var contracts = ContractSystem.Instance.Contracts;
                if (contracts != null)
                {
                    foreach (var contract in contracts)
                    {
                        if (contract != null && contract.ContractState == Contract.State.Active)
                        {
                            var contractNode = new ConfigNode("CONTRACT");
                            contract.Save(contractNode);
                            baseline.activeContracts.Add(contractNode);
                        }
                    }
                }
            }

            // Crew roster
            if (HighLogic.CurrentGame != null && HighLogic.CurrentGame.CrewRoster != null)
            {
                foreach (var crew in HighLogic.CurrentGame.CrewRoster.Crew)
                {
                    if (crew != null)
                    {
                        baseline.crewEntries.Add(new CrewEntry
                        {
                            name = crew.name,
                            status = crew.rosterStatus.ToString(),
                            trait = crew.trait ?? ""
                        });
                    }
                }
            }

            ParsekLog.Info("Baseline",
                $"Captured baseline at ut={baseline.ut:F0}: tech={baseline.researchedTechIds.Count}, " +
                $"facilities={baseline.facilityLevels.Count}, buildings={baseline.buildingIntact.Count}, " +
                $"contracts={baseline.activeContracts.Count}, crew={baseline.crewEntries.Count}, " +
                $"science={FormatCurrencyValue(baseline.science, baseline.scienceKnown)}, " +
                $"reputation={FormatCurrencyValue(baseline.reputation, baseline.reputationKnown)} " +
                $"(currency singletons expected={CurrencyScenarioReadiness.Format(expectedCurrencies)}, " +
                $"loaded={CurrencyScenarioReadiness.Format(loadedCurrencies)})");

            return baseline;
        }

        /// <summary>
        /// Pure: whether a baseline captured while <paramref name="loaded"/> currency
        /// singletons had finished their OnLoad holds a real value for
        /// <paramref name="singleton"/>. True when that singleton had loaded, or when the
        /// game mode never creates it (the 0 the field defaults to IS the value). False when
        /// the mode creates it and it had not loaded yet: the field's 0 is no reading.
        /// </summary>
        internal static bool DecideCurrencyValueCaptured(
            CurrencySingletons expected,
            CurrencySingletons loaded,
            CurrencySingletons singleton)
        {
            if ((expected & singleton) == CurrencySingletons.None)
                return true;
            return (loaded & singleton) != CurrencySingletons.None;
        }

        /// <summary>
        /// Pure: whether a baseline's science / reputation value may seed the ledger. A
        /// recorded flag is the answer. A baseline without one (written before the flag
        /// existed, or built in code) keeps the historical reading - the value is trusted -
        /// except for the one shape the early-capture defect leaves and a real reading
        /// cannot: a 0 value beside an EMPTY researched-tech list in a mode that creates
        /// R&amp;D. A loaded R&amp;D always reports its starting node researched, so an empty
        /// list means R&amp;D had not loaded; a KSP-written career saves Reputation after
        /// R&amp;D, so Reputation had not loaded either.
        /// </summary>
        internal static bool DecideCurrencyValueKnown(
            bool? recorded,
            double value,
            int researchedTechCount,
            bool singletonExpected)
        {
            if (recorded.HasValue)
                return recorded.Value;
            return !(value == 0.0 && researchedTechCount == 0 && singletonExpected);
        }

        /// <summary>See <see cref="DecideCurrencyValueKnown"/>.</summary>
        internal bool IsScienceKnown(CurrencySingletons expectedForMode)
        {
            return DecideCurrencyValueKnown(
                scienceKnown,
                science,
                researchedTechIds != null ? researchedTechIds.Count : 0,
                (expectedForMode & CurrencySingletons.ResearchAndDevelopment) != CurrencySingletons.None);
        }

        /// <summary>
        /// See <see cref="DecideCurrencyValueKnown"/>. The legacy heuristic reads the
        /// R&amp;D-not-loaded signature, so it applies only where the mode creates both.
        /// </summary>
        internal bool IsReputationKnown(CurrencySingletons expectedForMode)
        {
            const CurrencySingletons both =
                CurrencySingletons.ResearchAndDevelopment | CurrencySingletons.Reputation;
            return DecideCurrencyValueKnown(
                reputationKnown,
                reputation,
                researchedTechIds != null ? researchedTechIds.Count : 0,
                (expectedForMode & both) == both);
        }

        /// <summary>Invariant rendering of a captured currency value: "absent" when the
        /// capture recorded that the singleton had not loaded.</summary>
        internal static string FormatCurrencyValue(double value, bool? known)
        {
            if (known.HasValue && !known.Value)
                return "absent";
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        public void SerializeInto(ConfigNode node)
        {
            node.AddValue("ut", ut.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("funds", funds.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("science", science.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("reputation", reputation.ToString("R", CultureInfo.InvariantCulture));
            if (scienceKnown.HasValue)
                node.AddValue("scienceKnown", scienceKnown.Value ? "True" : "False");
            if (reputationKnown.HasValue)
                node.AddValue("reputationKnown", reputationKnown.Value ? "True" : "False");

            // Tech
            ConfigNode techNode = node.AddNode("TECH_IDS");
            foreach (string techId in researchedTechIds)
                techNode.AddValue("id", techId);

            // Facilities
            ConfigNode facNode = node.AddNode("FACILITY_LEVELS");
            foreach (var kvp in facilityLevels)
                facNode.AddValue(kvp.Key, kvp.Value.ToString("R", CultureInfo.InvariantCulture));

            // Buildings
            ConfigNode bldNode = node.AddNode("BUILDING_INTACT");
            foreach (var kvp in buildingIntact)
                bldNode.AddValue(kvp.Key, kvp.Value.ToString());

            // Contracts
            ConfigNode contractsNode = node.AddNode("ACTIVE_CONTRACTS");
            foreach (var contractCfg in activeContracts)
                contractsNode.AddNode(contractCfg);

            // Crew
            ConfigNode crewNode = node.AddNode("CREW_ROSTER");
            foreach (var entry in crewEntries)
            {
                ConfigNode entryNode = crewNode.AddNode("CREW");
                entry.SerializeInto(entryNode);
            }
        }

        public static GameStateBaseline DeserializeFrom(ConfigNode node)
        {
            var baseline = new GameStateBaseline();

            string utStr = node.GetValue("ut");
            if (utStr != null)
            {
                if (!double.TryParse(utStr, NumberStyles.Float, CultureInfo.InvariantCulture, out baseline.ut))
                    ParsekLog.Warn("Baseline", $"Failed to parse baseline UT '{utStr}'");
            }

            string fundsStr = node.GetValue("funds");
            if (fundsStr != null)
            {
                if (!double.TryParse(fundsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out baseline.funds))
                    ParsekLog.Warn("Baseline", $"Failed to parse baseline funds '{fundsStr}'");
            }

            string sciStr = node.GetValue("science");
            if (sciStr != null)
            {
                if (!double.TryParse(sciStr, NumberStyles.Float, CultureInfo.InvariantCulture, out baseline.science))
                    ParsekLog.Warn("Baseline", $"Failed to parse baseline science '{sciStr}'");
            }

            string repStr = node.GetValue("reputation");
            if (repStr != null)
            {
                if (!float.TryParse(repStr, NumberStyles.Float, CultureInfo.InvariantCulture, out baseline.reputation))
                    ParsekLog.Warn("Baseline", $"Failed to parse baseline reputation '{repStr}'");
            }

            baseline.scienceKnown = ParseOptionalBool(node, "scienceKnown");
            baseline.reputationKnown = ParseOptionalBool(node, "reputationKnown");

            // Tech
            ConfigNode techNode = node.GetNode("TECH_IDS");
            if (techNode != null)
            {
                string[] ids = techNode.GetValues("id");
                if (ids != null)
                    baseline.researchedTechIds.AddRange(ids);
            }

            // Facilities
            ConfigNode facNode = node.GetNode("FACILITY_LEVELS");
            if (facNode != null)
            {
                foreach (ConfigNode.Value v in facNode.values)
                {
                    float level;
                    if (float.TryParse(v.value, NumberStyles.Float, CultureInfo.InvariantCulture, out level))
                        baseline.facilityLevels[v.name] = level;
                    else
                        ParsekLog.Warn("Baseline", $"Failed to parse facility level '{v.value}' for '{v.name}'");
                }
            }

            // Buildings
            ConfigNode bldNode = node.GetNode("BUILDING_INTACT");
            if (bldNode != null)
            {
                foreach (ConfigNode.Value v in bldNode.values)
                {
                    bool intact;
                    if (bool.TryParse(v.value, out intact))
                        baseline.buildingIntact[v.name] = intact;
                    else
                        ParsekLog.Warn("Baseline", $"Failed to parse building intact value '{v.value}' for '{v.name}'");
                }
            }

            // Contracts
            ConfigNode contractsNode = node.GetNode("ACTIVE_CONTRACTS");
            if (contractsNode != null)
            {
                foreach (ConfigNode contractCfg in contractsNode.GetNodes("CONTRACT"))
                    baseline.activeContracts.Add(contractCfg);
            }

            // Crew
            ConfigNode crewNode = node.GetNode("CREW_ROSTER");
            if (crewNode != null)
            {
                foreach (ConfigNode entryNode in crewNode.GetNodes("CREW"))
                    baseline.crewEntries.Add(CrewEntry.DeserializeFrom(entryNode));
            }

            ParsekLog.Verbose("Baseline",
                $"Deserialized baseline at ut={baseline.ut:F0}: tech={baseline.researchedTechIds.Count}, " +
                $"facilities={baseline.facilityLevels.Count}, buildings={baseline.buildingIntact.Count}, " +
                $"contracts={baseline.activeContracts.Count}, crew={baseline.crewEntries.Count}, " +
                $"scienceKnown={FormatOptionalBool(baseline.scienceKnown)}, " +
                $"reputationKnown={FormatOptionalBool(baseline.reputationKnown)}");

            return baseline;
        }

        private static bool? ParseOptionalBool(ConfigNode node, string key)
        {
            string text = node.GetValue(key);
            if (text == null)
                return null;
            bool value;
            if (bool.TryParse(text, out value))
                return value;
            ParsekLog.Warn("Baseline", $"Failed to parse baseline {key} '{text}' - treated as not recorded");
            return null;
        }

        private static string FormatOptionalBool(bool? value)
        {
            return value.HasValue ? (value.Value ? "True" : "False") : "unrecorded";
        }
    }
}
