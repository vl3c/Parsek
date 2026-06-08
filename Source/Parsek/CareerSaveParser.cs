using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Pure, headless-testable parser for a KSP career .sfs ConfigNode into a
    /// <see cref="CareerSaveSnapshot"/>. This is the INDEPENDENT ground-truth
    /// read: it never touches the Parsek ledger or any live KSP singleton, so
    /// the reconstruction can be diffed against KSP's own serialization.
    ///
    /// Every facet is independently optional: a missing SCENARIO leaves the
    /// matching HasX flag false / collection empty and never throws. Every
    /// double parse uses InvariantCulture (comma-locale machines must not break).
    ///
    /// See docs/dev/design-ledger-groundtruth-harness.md.
    /// </summary>
    internal static class CareerSaveParser
    {
        private const string Tag = "LedgerGroundTruth";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Parse a loaded .sfs ConfigNode. Handles the root-vs-GAME wrapper:
        /// if the root has no FLIGHTSTATE child, descends into the GAME child
        /// (mirrors QuickloadResumeHelpers.ValidateQuicksaveStructure). Returns
        /// a snapshot with Parsed=false + a reason when the shape is
        /// unrecognizable; otherwise Parsed=true with each facet filled in as
        /// available.
        /// </summary>
        internal static CareerSaveSnapshot Parse(ConfigNode root)
        {
            var snapshot = new CareerSaveSnapshot();

            if (root == null)
            {
                snapshot.Parsed = false;
                snapshot.Reason = "root ConfigNode was null";
                ParsekLog.Verbose(Tag, "Parse: root ConfigNode null -> Parsed=false");
                return snapshot;
            }

            // Root-vs-GAME wrapper descent. ConfigNode.Load() returns a node
            // already containing the file contents, so the GAME node may be the
            // root itself (its children are FLIGHTSTATE/SCENARIO/...) or wrapped.
            ConfigNode gameNode = root;
            if (gameNode.GetNode("FLIGHTSTATE") == null)
            {
                ConfigNode wrapped = root.GetNode("GAME");
                if (wrapped != null)
                {
                    gameNode = wrapped;
                    ParsekLog.Verbose(Tag, "Parse: descended into GAME wrapper node");
                }
            }

            // Recognizability: a career save has SCENARIO nodes and/or a
            // FLIGHTSTATE. If neither is present the shape is unrecognizable.
            ConfigNode flightState = gameNode.GetNode("FLIGHTSTATE");
            ConfigNode[] scenarios = gameNode.GetNodes("SCENARIO");
            bool hasScenarios = scenarios != null && scenarios.Length > 0;
            if (flightState == null && !hasScenarios)
            {
                snapshot.Parsed = false;
                snapshot.Reason = "no FLIGHTSTATE and no SCENARIO nodes (not a career save shape)";
                ParsekLog.Verbose(Tag,
                    "Parse: no FLIGHTSTATE and no SCENARIO -> Parsed=false");
                return snapshot;
            }

            snapshot.Parsed = true;

            ParseFunds(gameNode, snapshot);
            ParseScience(gameNode, snapshot);
            ParseReputation(gameNode, snapshot);
            ParseFacilities(gameNode, snapshot);
            ParseContracts(gameNode, snapshot);
            ParseMilestones(gameNode, snapshot);
            ParseVessels(flightState, snapshot);

            ParsekLog.Verbose(Tag,
                $"Parse: complete hasFunds={snapshot.HasFunds.ToString(IC)} " +
                $"hasScience={snapshot.HasScience.ToString(IC)} " +
                $"hasRep={snapshot.HasRep.ToString(IC)} " +
                $"subjects={snapshot.SubjectScience.Count.ToString(IC)} " +
                $"facilities={snapshot.FacilityLevelFrac.Count.ToString(IC)} " +
                $"activeContracts={snapshot.ActiveContractGuids.Count.ToString(IC)} " +
                $"allContracts={snapshot.ContractGuidsAllStates.Count.ToString(IC)} " +
                $"completedMilestones={snapshot.CompletedMilestoneIds.Count.ToString(IC)} " +
                $"allMilestones={snapshot.AllMilestoneIds.Count.ToString(IC)} " +
                $"vessels={snapshot.Vessels.Count.ToString(IC)}");

            return snapshot;
        }

        /// <summary>Finds the first SCENARIO node with a matching `name` value.</summary>
        private static ConfigNode FindScenario(ConfigNode gameNode, string scenarioName)
        {
            ConfigNode[] scenarios = gameNode.GetNodes("SCENARIO");
            if (scenarios == null)
                return null;

            foreach (var sc in scenarios)
            {
                if (sc == null)
                    continue;
                string name = sc.GetValue("name");
                if (string.Equals(name, scenarioName, StringComparison.Ordinal))
                    return sc;
            }
            return null;
        }

        private static void ParseFunds(ConfigNode gameNode, CareerSaveSnapshot snapshot)
        {
            ConfigNode funding = FindScenario(gameNode, "Funding");
            if (funding == null)
            {
                ParsekLog.Verbose(Tag, "ParseFunds: no Funding SCENARIO -> HasFunds=false");
                return;
            }

            if (TryGetDouble(funding, "funds", out double funds))
            {
                snapshot.HasFunds = true;
                snapshot.Funds = funds;
                ParsekLog.Verbose(Tag,
                    $"ParseFunds: funds={funds.ToString("R", IC)}");
            }
            else
            {
                ParsekLog.Verbose(Tag, "ParseFunds: Funding SCENARIO has no parsable funds -> HasFunds=false");
            }
        }

        private static void ParseScience(ConfigNode gameNode, CareerSaveSnapshot snapshot)
        {
            ConfigNode rnd = FindScenario(gameNode, "ResearchAndDevelopment");
            if (rnd == null)
            {
                ParsekLog.Verbose(Tag, "ParseScience: no ResearchAndDevelopment SCENARIO -> HasScience=false");
                return;
            }

            if (TryGetDouble(rnd, "sci", out double sci))
            {
                snapshot.HasScience = true;
                snapshot.SciencePool = sci;
            }

            // Per-subject Science{ id, sci, cap } children.
            ConfigNode[] subjects = rnd.GetNodes("Science");
            int subjectCount = 0;
            if (subjects != null)
            {
                foreach (var sub in subjects)
                {
                    if (sub == null)
                        continue;
                    string id = sub.GetValue("id");
                    if (string.IsNullOrEmpty(id))
                        continue;
                    TryGetDouble(sub, "sci", out double subjSci);
                    snapshot.SubjectScience[id] = subjSci;
                    subjectCount++;
                }
            }

            ParsekLog.Verbose(Tag,
                $"ParseScience: hasScience={snapshot.HasScience.ToString(IC)} " +
                $"sciencePool={snapshot.SciencePool.ToString("R", IC)} subjects={subjectCount.ToString(IC)}");
        }

        private static void ParseReputation(ConfigNode gameNode, CareerSaveSnapshot snapshot)
        {
            ConfigNode rep = FindScenario(gameNode, "Reputation");
            if (rep == null)
            {
                ParsekLog.Verbose(Tag, "ParseReputation: no Reputation SCENARIO -> HasRep=false");
                return;
            }

            if (TryGetDouble(rep, "rep", out double repVal))
            {
                snapshot.HasRep = true;
                snapshot.Reputation = repVal;
                ParsekLog.Verbose(Tag, $"ParseReputation: rep={repVal.ToString("R", IC)}");
            }
            else
            {
                ParsekLog.Verbose(Tag, "ParseReputation: Reputation SCENARIO has no parsable rep -> HasRep=false");
            }
        }

        private static void ParseFacilities(ConfigNode gameNode, CareerSaveSnapshot snapshot)
        {
            ConfigNode facScenario = FindScenario(gameNode, "ScenarioUpgradeableFacilities");
            if (facScenario == null)
            {
                ParsekLog.Verbose(Tag,
                    "ParseFacilities: no ScenarioUpgradeableFacilities SCENARIO -> empty");
                return;
            }

            // Children are named by facility id ("SpaceCenter/LaunchPad" etc.),
            // each carrying a `lvl` normalized fraction. The node name itself is
            // the facility id (already == ledger FacilityId, no remap).
            int facCount = 0;
            foreach (ConfigNode child in EnumerateChildNodes(facScenario))
            {
                if (child == null)
                    continue;
                string facilityId = child.name;
                if (string.IsNullOrEmpty(facilityId))
                    continue;
                if (!TryGetDouble(child, "lvl", out double lvl))
                    continue;
                snapshot.FacilityLevelFrac[facilityId] = lvl;
                facCount++;
            }

            ParsekLog.Verbose(Tag, $"ParseFacilities: parsed {facCount.ToString(IC)} facility level(s)");
        }

        private static void ParseContracts(ConfigNode gameNode, CareerSaveSnapshot snapshot)
        {
            ConfigNode cs = FindScenario(gameNode, "ContractSystem");
            if (cs == null)
            {
                ParsekLog.Verbose(Tag, "ParseContracts: no ContractSystem SCENARIO -> empty");
                return;
            }

            ConfigNode contractsNode = cs.GetNode("CONTRACTS");
            if (contractsNode == null)
            {
                ParsekLog.Verbose(Tag, "ParseContracts: ContractSystem has no CONTRACTS node -> empty");
                return;
            }

            ConfigNode[] contracts = contractsNode.GetNodes("CONTRACT");
            int active = 0;
            int total = 0;
            if (contracts != null)
            {
                foreach (var c in contracts)
                {
                    if (c == null)
                        continue;
                    string guid = c.GetValue("guid");
                    if (string.IsNullOrEmpty(guid))
                        continue;
                    snapshot.ContractGuidsAllStates.Add(guid);
                    total++;
                    string state = c.GetValue("state");
                    if (string.Equals(state, "Active", StringComparison.Ordinal))
                    {
                        snapshot.ActiveContractGuids.Add(guid);
                        active++;
                    }
                }
            }

            ParsekLog.Verbose(Tag,
                $"ParseContracts: total={total.ToString(IC)} active={active.ToString(IC)}");
        }

        private static void ParseMilestones(ConfigNode gameNode, CareerSaveSnapshot snapshot)
        {
            ConfigNode pt = FindScenario(gameNode, "ProgressTracking");
            if (pt == null)
            {
                ParsekLog.Verbose(Tag, "ParseMilestones: no ProgressTracking SCENARIO -> empty");
                return;
            }

            ConfigNode progress = pt.GetNode("Progress");
            if (progress == null)
            {
                ParsekLog.Verbose(Tag, "ParseMilestones: ProgressTracking has no Progress node -> empty");
                return;
            }

            // Recursively walk the Progress tree. Top-level milestone nodes use
            // their bare id; body-subtree children produce qualified ids
            // "<Body>/<Child>" (matching KspStatePatcher.PatchProgressNodeTree).
            // Emit BOTH the qualified and bare child id. A node is "completed"
            // when it carries a `completed` field (a `reached`-only node like
            // RecordsDepth is NOT completed). crew{}/vessel{} sub-nodes are data,
            // not milestones, and are not walked.
            int completedCount = 0;
            int allCount = 0;
            WalkProgress(progress, "", snapshot, ref completedCount, ref allCount);

            ParsekLog.Verbose(Tag,
                $"ParseMilestones: all={allCount.ToString(IC)} completed={completedCount.ToString(IC)} " +
                $"(emitted ids: all={snapshot.AllMilestoneIds.Count.ToString(IC)} " +
                $"completed={snapshot.CompletedMilestoneIds.Count.ToString(IC)})");
        }

        /// <summary>
        /// Recursively walks the ProgressTracking subtree. <paramref name="pathPrefix"/>
        /// is the qualifying body prefix ("" at the top level, "Mun" inside the
        /// Mun body subtree).
        /// </summary>
        private static void WalkProgress(
            ConfigNode node,
            string pathPrefix,
            CareerSaveSnapshot snapshot,
            ref int completedCount,
            ref int allCount)
        {
            foreach (ConfigNode child in EnumerateChildNodes(node))
            {
                if (child == null)
                    continue;

                string childName = child.name;
                if (string.IsNullOrEmpty(childName))
                    continue;

                // crew{}/vessel{} sub-nodes are per-milestone data payloads, not
                // milestones in their own right.
                if (string.Equals(childName, "crew", StringComparison.Ordinal)
                    || string.Equals(childName, "vessel", StringComparison.Ordinal))
                {
                    continue;
                }

                // A node is a milestone if it carries `completed` or `reached`,
                // OR it is a leaf/body container we should descend into. We treat
                // any node carrying completed/reached as a milestone; nodes
                // without either are body-subtree containers whose children are
                // qualified milestones.
                bool hasCompleted = child.HasValue("completed");
                bool hasReached = child.HasValue("reached");
                bool isMilestone = hasCompleted || hasReached;

                if (isMilestone)
                {
                    string qualified = string.IsNullOrEmpty(pathPrefix)
                        ? childName
                        : pathPrefix + "/" + childName;

                    snapshot.AllMilestoneIds.Add(qualified);
                    // Bare child id too, for the safety fallback the recalc uses.
                    snapshot.AllMilestoneIds.Add(childName);
                    allCount++;

                    if (hasCompleted)
                    {
                        snapshot.CompletedMilestoneIds.Add(qualified);
                        snapshot.CompletedMilestoneIds.Add(childName);
                        completedCount++;
                    }
                }
                else
                {
                    // Body-subtree container: descend, qualifying children by the
                    // container's name (e.g. "Mun" -> "Mun/Landing").
                    string childPrefix = string.IsNullOrEmpty(pathPrefix)
                        ? childName
                        : pathPrefix + "/" + childName;
                    WalkProgress(child, childPrefix, snapshot, ref completedCount, ref allCount);
                }
            }
        }

        private static void ParseVessels(ConfigNode flightState, CareerSaveSnapshot snapshot)
        {
            if (flightState == null)
            {
                ParsekLog.Verbose(Tag, "ParseVessels: no FLIGHTSTATE -> empty");
                return;
            }

            ConfigNode[] vessels = flightState.GetNodes("VESSEL");
            if (vessels == null)
            {
                ParsekLog.Verbose(Tag, "ParseVessels: FLIGHTSTATE has no VESSEL nodes -> empty");
                return;
            }

            int parsed = 0;
            foreach (var v in vessels)
            {
                if (v == null)
                    continue;

                var sv = new SaveVessel
                {
                    Pid = v.GetValue("pid"),
                    Name = v.GetValue("name"),
                    Type = v.GetValue("type"),
                    ResourceTotals = new Dictionary<string, double>(StringComparer.Ordinal)
                };

                uint persistentId = 0;
                string pidStr = v.GetValue("persistentId");
                if (!string.IsNullOrEmpty(pidStr))
                    uint.TryParse(pidStr, NumberStyles.Integer, IC, out persistentId);
                sv.PersistentId = persistentId;

                // Sum each PART's RESOURCE amount per resource name.
                ConfigNode[] parts = v.GetNodes("PART");
                if (parts != null)
                {
                    foreach (var part in parts)
                    {
                        if (part == null)
                            continue;
                        ConfigNode[] resources = part.GetNodes("RESOURCE");
                        if (resources == null)
                            continue;
                        foreach (var res in resources)
                        {
                            if (res == null)
                                continue;
                            string resName = res.GetValue("name");
                            if (string.IsNullOrEmpty(resName))
                                continue;
                            TryGetDouble(res, "amount", out double amount);
                            double prior;
                            sv.ResourceTotals.TryGetValue(resName, out prior);
                            sv.ResourceTotals[resName] = prior + amount;
                        }
                    }
                }

                snapshot.Vessels.Add(sv);
                parsed++;
            }

            ParsekLog.Verbose(Tag, $"ParseVessels: parsed {parsed.ToString(IC)} vessel(s)");
        }

        /// <summary>
        /// Enumerates direct child nodes of a ConfigNode. ConfigNode exposes
        /// children via GetNodes() (all), which is the portable accessor here.
        /// </summary>
        private static IEnumerable<ConfigNode> EnumerateChildNodes(ConfigNode node)
        {
            if (node == null)
                yield break;
            ConfigNode[] all = node.GetNodes();
            if (all == null)
                yield break;
            for (int i = 0; i < all.Length; i++)
                yield return all[i];
        }

        /// <summary>
        /// InvariantCulture double parse of a ConfigNode value. Returns false
        /// (and value=0) when the key is absent or unparsable.
        /// </summary>
        private static bool TryGetDouble(ConfigNode node, string key, out double value)
        {
            value = 0.0;
            if (node == null)
                return false;
            string raw = node.GetValue(key);
            if (string.IsNullOrEmpty(raw))
                return false;
            return double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, IC, out value);
        }
    }
}
