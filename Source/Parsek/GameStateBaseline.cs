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

            // Building intact states — find all DestructibleBuilding instances in scene
            var destructibles = Object.FindObjectsOfType<DestructibleBuilding>();
            if (destructibles != null)
            {
                foreach (var db in destructibles)
                {
                    if (db != null && !string.IsNullOrEmpty(db.id))
                        baseline.buildingIntact[db.id] = !db.IsDestroyed;
                }
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

            return baseline;
        }

        public void SerializeInto(ConfigNode node)
        {
            node.AddValue("ut", ut.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("funds", funds.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("science", science.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("reputation", reputation.ToString("R", CultureInfo.InvariantCulture));

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
                double.TryParse(utStr, NumberStyles.Float, CultureInfo.InvariantCulture, out baseline.ut);

            string fundsStr = node.GetValue("funds");
            if (fundsStr != null)
                double.TryParse(fundsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out baseline.funds);

            string sciStr = node.GetValue("science");
            if (sciStr != null)
                double.TryParse(sciStr, NumberStyles.Float, CultureInfo.InvariantCulture, out baseline.science);

            string repStr = node.GetValue("reputation");
            if (repStr != null)
                float.TryParse(repStr, NumberStyles.Float, CultureInfo.InvariantCulture, out baseline.reputation);

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

            return baseline;
        }
    }
}
