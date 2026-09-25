using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Parsek.Tests.Generators;

namespace Parsek.Tests
{
    /// <summary>
    /// Builds the committed <c>harness/fixtures/saves/stock-screen-census</c> fixture BY
    /// CONSTRUCTION from <c>career-earned-ksc</c>: a vessel-less career at UT 409.56 whose
    /// committed timeline, after the current clock, does every thing the stock-screen
    /// annotation program (docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md)
    /// marks or blocks. The GUI census lane <c>GUI-28-census-stock-screens</c> photographs
    /// the stock screens over it.
    ///
    /// <para>What the committed future holds, and which screen shows it (all rows are
    /// strictly after the save clock, so <see cref="CommittedFutureIndex.IsFuture"/> holds
    /// for each one for the whole lane: the Space Center clock runs at 1x and the
    /// earliest row is 30000 s ahead):</para>
    /// <list type="bullet">
    /// <item>R&amp;D: <c>basicRocketry</c> researched (KSC-origin ScienceSpending).</item>
    /// <item>Mission Control Available: the Offered survey <see cref="AcceptedLaterContractGuid"/>
    ///   accepted (KSC-origin ContractAccept), which also fills the second of the two
    ///   level-1 slots while the Active contract below still holds the first, so every
    ///   other Offered contract carries the C2 slot block.</item>
    /// <item>Mission Control Active: <see cref="ActiveContractGuid"/> (Active in stock since
    ///   UT 360, the <c>career-earned-pad</c> D8 splice verbatim) completed by the
    ///   committed flight.</item>
    /// <item>Astronaut Complex: the applicant <see cref="HiredLaterApplicant"/> hired
    ///   (KSC-origin KerbalHire); <see cref="ReservedKerbal"/> held by the committed
    ///   flight <see cref="FlightName"/>, a synthetic recording tree launched and
    ///   recovered in the future (a Recovered crew end state, so the hold runs to its
    ///   EndUT).</item>
    /// <item>Administration: <see cref="ActiveStrategy"/> active in stock since UT 390 (a
    ///   past KSC-origin StrategyActivate row keeps the ledger in step) and deactivated
    ///   later; <see cref="ActivatedLaterStrategy"/> activated later.</item>
    /// <item>KSC facility menu: the Tracking Station upgraded to level 2.</item>
    /// <item>Part tooltip (VAB/SPH and R&amp;D): <see cref="PurchasedLaterPart"/> is taken
    ///   out of the start node's purchased list and bought later; the difficulty's
    ///   BypassEntryPurchaseAfterResearch is turned OFF, without which the part block is
    ///   inert.</item>
    /// </list>
    ///
    /// <para>Everything is appended to the base's own files: the ledger rows are
    /// serialized through <see cref="GameAction.SerializeInto"/>, the flight tree through
    /// <see cref="RecordingTree.Save"/> and its sidecars through
    /// <see cref="ScenarioWriter.WriteSidecarFiles"/>, so every key is the one production
    /// writes. Text files keep the base's CRLF line endings. The builder is
    /// deterministic; <c>StockScreenCensusFixtureTests</c> rebuilds it and compares it with
    /// the committed tree, so a hand edit on either side reds the suite.</para>
    /// </summary>
    internal static class StockScreenCensusFixture
    {
        internal const string FixtureName = "stock-screen-census";
        internal const string BaseName = "career-earned-ksc";
        internal const string CraftDonorName = "gs1-two-stage-pad";
        internal const string CraftFileName = "Jumping Flea.craft";

        internal const string BaseTitle = "career-science-pad (CAREER)";
        internal const string Title = "stock-screen-census (CAREER)";

        /// <summary>The save clock (FLIGHTSTATE UT) the base carries and this fixture keeps.</summary>
        internal const double SaveUT = 409.55999999991747;

        // ---- the Active contract: career-earned-pad's D8 splice, byte for byte ----
        internal const string ActiveContractGuid = "07c8e34d-0464-4416-a973-1e2b472bc347";
        internal const string ActiveContractType = "PartTest";
        internal const string ActiveContractTitle = "Test TD-12 Decoupler on an escape trajectory out of Kerbin.";
        internal const string ActiveContractBaseValues =
            "21600,9201600,24750,68062.501475215,27225.000590086,9,14.54545,12,21615.14,0,0,0";
        internal const string ActiveContractValues =
            "21600,9201600,0,68062.501475215,27225,9,14.54545,12,21615.14,360,9201960,0";
        internal const double ActiveContractAcceptUT = 360;
        internal const double ActiveContractDeadlineUT = 9201960;

        // ---- the Offered contract the committed timeline accepts ----
        internal const string AcceptedLaterContractGuid = "90e4faaf-2029-4c6b-8bc1-40226bb0fc27";
        internal const string AcceptedLaterContractType = "SurveyContract";
        internal const string AcceptedLaterContractTitle = "Conduct a focused observational survey of Kerbin.";
        /// <summary>An Offered contract nothing committed touches: the slot-block subject.</summary>
        internal const string SlotBlockedContractGuid = "375b4446-c861-4b4d-bf97-ef38407246a4";

        internal const string ActiveStrategy = "AppreciationCampaignCfg";
        internal const string ActivatedLaterStrategy = "OutsourcedResearchCfg";
        internal const double ActiveStrategyActivateUT = 390;
        internal const double ActiveStrategySetupCost = 70750;
        /// <summary>The base save's funds pool.</summary>
        internal const double BaseFunds = 536558;

        internal const string ResearchedLaterTech = "basicRocketry";
        internal const string UpgradedLaterFacility = "SpaceCenter/TrackingStation";
        internal const string PurchasedLaterPart = "probeCoreSphere.v2";
        internal const string HiredLaterApplicant = "Verhat Kerman";
        internal const string ReservedKerbal = "Bill Kerman";

        // ---- the committed future, UT ascending ----
        internal const double TechResearchUT = 30000;
        internal const double ContractAcceptUT = 50000;
        internal const double KerbalHireUT = 70000;
        internal const double FacilityUpgradeUT = 90000;
        internal const double PartPurchaseUT = 110000;
        internal const double FlightStartUT = 130000;
        internal const double ContractCompleteUT = 130060;
        internal const double FlightEndUT = 130120;
        internal const double StrategyDeactivateUT = 150000;
        internal const double StrategyActivateUT = 160000;

        internal const string FlightRecordingId = "5c0c7e3a1b2d4f60a9e8d7c6b5a4f301";
        internal const string FlightName = "Census Hopper";
        internal const uint FlightVesselPid = 3171000101;

        /// <summary>The highest KSC-scoped <c>seq</c> in the base ledger (FirstCrewToSurvive).</summary>
        internal const int BaseLastKscSeq = 1;

        private const string Crlf = "\r\n";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        internal static string SavesDir(string repoRoot)
        {
            return Path.Combine(repoRoot, "harness", "fixtures", "saves");
        }

        internal static string TargetDir(string repoRoot)
        {
            return Path.Combine(SavesDir(repoRoot), FixtureName);
        }

        /// <summary>
        /// Every file the fixture holds, keyed by its '/'-separated path relative to the
        /// fixture directory. Pure over the base fixture and the craft donor.
        /// </summary>
        internal static SortedDictionary<string, byte[]> BuildFiles(string repoRoot)
        {
            string baseDir = Path.Combine(SavesDir(repoRoot), BaseName);
            if (!Directory.Exists(baseDir))
                throw new InvalidOperationException("base fixture not found: " + baseDir);

            var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);

            // Everything the base carries, copied, apart from the three files derived below.
            foreach (string path in Directory.GetFiles(baseDir, "*", SearchOption.AllDirectories))
            {
                string rel = path.Substring(baseDir.Length + 1).Replace('\\', '/');
                files[rel] = File.ReadAllBytes(path);
            }

            string sfs = Encoding.UTF8.GetString(files["persistent.sfs"]);
            files["persistent.sfs"] = Encoding.UTF8.GetBytes(BuildSave(sfs));

            string ledger = Encoding.UTF8.GetString(files["Parsek/GameState/ledger.pgld"]);
            files["Parsek/GameState/ledger.pgld"] = Encoding.UTF8.GetBytes(BuildLedger(ledger));

            foreach (var kv in BuildFlightSidecars())
                files["Parsek/Recordings/" + kv.Key] = kv.Value;

            string craftPath = Path.Combine(SavesDir(repoRoot), CraftDonorName, "Ships", "VAB", CraftFileName);
            if (!File.Exists(craftPath))
                throw new InvalidOperationException("craft donor not found: " + craftPath);
            files["Ships/VAB/" + CraftFileName] = File.ReadAllBytes(craftPath);

            return files;
        }

        // ------------------------------------------------------------------ save

        internal static string BuildSave(string baseText)
        {
            if (baseText.IndexOf(Crlf, StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("base persistent.sfs is not CRLF");
            var lines = new List<string>(baseText.Split(new[] { Crlf }, StringSplitOptions.None));

            ReplaceExactlyOnce(lines, "\tTitle = " + BaseTitle, "\tTitle = " + Title);
            ReplaceExactlyOnce(lines, "\t\t\tBypassEntryPurchaseAfterResearch = True",
                "\t\t\tBypassEntryPurchaseAfterResearch = False");

            // The funds pool after the past strategy activation's setup cost: stock charged it
            // when the strategy was activated, and the ledger walk charges the same row.
            int funding = FindScenario(lines, "Funding");
            ReplaceInBlockExactlyOnce(lines, funding, "\t\tfunds = " + BaseFunds.ToString("R", IC),
                "\t\tfunds = " + (BaseFunds - ActiveStrategySetupCost).ToString("R", IC));

            // The part the committed timeline buys: unpurchased now.
            int rnd = FindScenario(lines, "ResearchAndDevelopment");
            int startTech = FindChildWithValue(lines, rnd, "Tech", "id", "start");
            RemoveExactlyOnce(lines, startTech, "\t\t\tpart = " + PurchasedLaterPart);

            // The active strategy, in stock's own STRATEGY shape (name / date / factor).
            int strategySystem = FindScenario(lines, "StrategySystem");
            int strategies = FindChild(lines, strategySystem, "STRATEGIES");
            int strategiesEnd = BlockEnd(lines, strategies);
            if (strategiesEnd != strategies + 2)
                throw new InvalidOperationException("base STRATEGIES is not empty");
            lines.InsertRange(strategiesEnd, new[]
            {
                "\t\t\tSTRATEGY",
                "\t\t\t{",
                "\t\t\t\tname = " + ActiveStrategy,
                "\t\t\t\tdate = " + ActiveStrategyActivateUT.ToString("R", IC),
                "\t\t\t\tfactor = 0.0500000007",
                "\t\t\t}",
            });

            // The Active contract (career-earned-pad's splice): state and values pack.
            int contractSystem = FindScenario(lines, "ContractSystem");
            int contracts = FindChild(lines, contractSystem, "CONTRACTS");
            int contract = FindChildWithValue(lines, contracts, "CONTRACT", "guid", ActiveContractGuid);
            ReplaceInBlockExactlyOnce(lines, contract, "\t\t\t\tstate = Offered", "\t\t\t\tstate = Active");
            ReplaceInBlockExactlyOnce(lines, contract, "\t\t\t\tvalues = " + ActiveContractBaseValues,
                "\t\t\t\tvalues = " + ActiveContractValues);

            // The committed flight's tree, after the base's own tree.
            int parsek = FindScenario(lines, "ParsekScenario");
            int slots = FindChild(lines, parsek, "KERBAL_SLOTS");
            lines.InsertRange(slots, SerializeNode(BuildFlightTreeNode(), "RECORDING_TREE", 2));

            return string.Join(Crlf, lines);
        }

        // ------------------------------------------------------------------ ledger

        /// <summary>The rows appended to the base ledger, in append order.</summary>
        internal static List<GameAction> BuildLedgerRows()
        {
            int seq = BaseLastKscSeq;
            var rows = new List<GameAction>();

            // PAST rows (UT <= the save clock): the stock state the save already carries.
            rows.Add(new GameAction
            {
                UT = ActiveContractAcceptUT,
                Type = GameActionType.ContractAccept,
                ActionId = "act_5c1a7f0b6d3e42a9b8f04e17c92d6a35",
                Sequence = ++seq,
                ContractId = ActiveContractGuid,
                ContractType = ActiveContractType,
                ContractTitle = ActiveContractTitle,
                AdvanceFunds = 0f,
                DeadlineUT = ActiveContractDeadlineUT,
                FundsPenalty = 27225f,
                RepPenalty = 12f,
            });
            rows.Add(new GameAction
            {
                UT = ActiveStrategyActivateUT,
                Type = GameActionType.StrategyActivate,
                ActionId = "act_1f0e2d3c4b5a49688776655443322101",
                Sequence = ++seq,
                StrategyId = ActiveStrategy,
                SourceResource = StrategyResource.Funds,
                TargetResource = StrategyResource.Reputation,
                Commitment = 0.05f,
                SetupCost = (float)ActiveStrategySetupCost,
            });

            // FUTURE rows: the committed timeline the stock screens annotate.
            rows.Add(new GameAction
            {
                UT = TechResearchUT,
                Type = GameActionType.ScienceSpending,
                ActionId = "act_1f0e2d3c4b5a49688776655443322102",
                Sequence = ++seq,
                NodeId = ResearchedLaterTech,
                Cost = 5f,
            });
            rows.Add(new GameAction
            {
                UT = ContractAcceptUT,
                Type = GameActionType.ContractAccept,
                ActionId = "act_1f0e2d3c4b5a49688776655443322103",
                Sequence = ++seq,
                ContractId = AcceptedLaterContractGuid,
                ContractType = AcceptedLaterContractType,
                ContractTitle = AcceptedLaterContractTitle,
                AdvanceFunds = 0f,
                DeadlineUT = ContractAcceptUT + 46008000,
                FundsPenalty = 7953.75f,
                RepPenalty = 1.904762f,
            });
            rows.Add(new GameAction
            {
                UT = KerbalHireUT,
                Type = GameActionType.KerbalHire,
                ActionId = "act_1f0e2d3c4b5a49688776655443322104",
                Sequence = ++seq,
                KerbalName = HiredLaterApplicant,
                KerbalRole = "Engineer",
                HireCost = 62000f,
            });
            rows.Add(new GameAction
            {
                UT = FacilityUpgradeUT,
                Type = GameActionType.FacilityUpgrade,
                ActionId = "act_1f0e2d3c4b5a49688776655443322105",
                Sequence = ++seq,
                FacilityId = UpgradedLaterFacility,
                ToLevel = 2,
                FacilityCost = 150000f,
            });
            rows.Add(new GameAction
            {
                UT = PartPurchaseUT,
                Type = GameActionType.FundsSpending,
                ActionId = "act_1f0e2d3c4b5a49688776655443322106",
                Sequence = ++seq,
                FundsSpent = 1400f,
                FundsSpendingSource = FundsSpendingSource.Other,
                DedupKey = PurchasedLaterPart,
            });
            rows.Add(new GameAction
            {
                UT = FlightStartUT,
                Type = GameActionType.KerbalAssignment,
                ActionId = "act_1f0e2d3c4b5a49688776655443322107",
                RecordingId = FlightRecordingId,
                Sequence = 1,
                KerbalName = ReservedKerbal,
                KerbalRole = "Engineer",
                StartUT = (float)FlightStartUT,
                EndUT = (float)FlightEndUT,
                KerbalEndStateField = KerbalEndState.Recovered,
            });
            rows.Add(new GameAction
            {
                UT = ContractCompleteUT,
                Type = GameActionType.ContractComplete,
                ActionId = "act_1f0e2d3c4b5a49688776655443322108",
                RecordingId = FlightRecordingId,
                Sequence = 2,
                ContractId = ActiveContractGuid,
                ContractTitle = ActiveContractTitle,
                FundsReward = 68062.5f,
                RepReward = 14.54545f,
                ScienceReward = 9f,
            });
            rows.Add(new GameAction
            {
                UT = StrategyDeactivateUT,
                Type = GameActionType.StrategyDeactivate,
                ActionId = "act_1f0e2d3c4b5a49688776655443322109",
                Sequence = ++seq,
                StrategyId = ActiveStrategy,
            });
            rows.Add(new GameAction
            {
                UT = StrategyActivateUT,
                Type = GameActionType.StrategyActivate,
                ActionId = "act_1f0e2d3c4b5a4968877665544332210a",
                Sequence = ++seq,
                StrategyId = ActivatedLaterStrategy,
                SourceResource = StrategyResource.Funds,
                TargetResource = StrategyResource.Science,
                Commitment = 0.05f,
                SetupCost = 76100f,
            });
            return rows;
        }

        internal static string BuildLedger(string baseText)
        {
            if (!baseText.EndsWith("}" + Crlf, StringComparison.Ordinal))
                throw new InvalidOperationException("base ledger does not end with a CRLF-terminated node");
            var sb = new StringBuilder(baseText);
            var holder = new ConfigNode("LEDGER");
            foreach (var row in BuildLedgerRows())
                row.SerializeInto(holder);
            foreach (ConfigNode node in holder.nodes)
                foreach (string line in SerializeNode(node, "GAME_ACTION", 0))
                    sb.Append(line).Append(Crlf);
            return sb.ToString();
        }

        // ------------------------------------------------------------------ flight

        internal static RecordingBuilder BuildFlightBuilder()
        {
            // A pad hop: the pad at launch, up, and back down near it at recovery.
            const double padLat = -0.097207;
            const double padLon = -74.557671;
            var b = new RecordingBuilder(FlightName).WithRecordingId(FlightRecordingId);
            b.AddPoint(FlightStartUT, padLat, padLon, 68.5);
            b.AddPoint(FlightStartUT + 20, padLat, padLon, 900);
            b.AddPoint(FlightStartUT + 60, padLat - 0.001, padLon + 0.002, 1600);
            b.AddPoint(FlightStartUT + 100, padLat - 0.002, padLon + 0.004, 400);
            b.AddPoint(FlightEndUT, padLat - 0.0025, padLon + 0.005, 66);
            b.WithTerminalState((int)TerminalState.Recovered);
            b.WithLaunchIdentity("Launch Pad", "Prelaunch");
            b.WithVesselSnapshot(VesselSnapshotBuilder.FleaRocket(FlightName, ReservedKerbal, FlightVesselPid));
            return b;
        }

        internal static RecordingTree BuildFlightTree()
        {
            RecordingBuilder builder = BuildFlightBuilder();
            RecordingTree tree = ScenarioWriter.MaterializeTree(new[] { builder });
            Recording rec = tree.Recordings[FlightRecordingId];
            // The trajectory travels in the sidecar; load it so the tree record carries the
            // pointCount production writes.
            RecordingStore.DeserializeTrajectoryFrom(builder.BuildTrajectoryNode(), rec);
            rec.CrewEndStates = new Dictionary<string, KerbalEndState>(StringComparer.Ordinal)
            {
                { ReservedKerbal, KerbalEndState.Recovered }
            };
            rec.CrewEndStatesResolved = true;
            return tree;
        }

        internal static ConfigNode BuildFlightTreeNode()
        {
            var node = new ConfigNode("RECORDING_TREE");
            BuildFlightTree().Save(node);
            return node;
        }

        /// <summary>The flight's sidecars (<c>.prec</c>, <c>_vessel.craft</c>, <c>_ghost.craft</c>),
        /// written by the production path into a scratch directory and read back.</summary>
        internal static SortedDictionary<string, byte[]> BuildFlightSidecars()
        {
            var result = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            string dir = Path.Combine(Path.GetTempPath(), "parsek-stock-census-" + Guid.NewGuid().ToString("N"));
            try
            {
                var writer = new ScenarioWriter().WithV3Format();
                writer.AddRecordingAsTree(BuildFlightBuilder());
                writer.WriteSidecarFiles(dir);
                string recDir = Path.Combine(dir, "Parsek", "Recordings");
                // Every committed fixture keeps its readable .prec.txt trajectory mirror
                // (CommittedFixtureMirrorTests; OptimizerTransferCohesionTests globs it);
                // the snapshot mirror is harvest exhaust no committed fixture carries.
                foreach (string path in Directory.GetFiles(recDir))
                    if (!path.EndsWith(".craft.txt", StringComparison.Ordinal))
                        result[Path.GetFileName(path)] = File.ReadAllBytes(path);
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            return result;
        }

        // ------------------------------------------------------------------ text helpers

        /// <summary>A ConfigNode as CRLF-free lines, tab-indented the way KSP writes a save.</summary>
        internal static List<string> SerializeNode(ConfigNode node, string name, int indent)
        {
            var lines = new List<string>();
            string tabs = new string('\t', indent);
            lines.Add(tabs + name);
            lines.Add(tabs + "{");
            foreach (ConfigNode.Value v in node.values)
                lines.Add(tabs + "\t" + v.name + " = " + v.value);
            foreach (ConfigNode child in node.nodes)
                lines.AddRange(SerializeNode(child, child.name, indent + 1));
            lines.Add(tabs + "}");
            return lines;
        }

        private static int IndentOf(string line)
        {
            int n = 0;
            while (n < line.Length && line[n] == '\t') n++;
            return n;
        }

        /// <summary>The line index of the closing brace of the node whose name is at <paramref name="nameLine"/>.</summary>
        internal static int BlockEnd(List<string> lines, int nameLine)
        {
            string tabs = new string('\t', IndentOf(lines[nameLine]));
            if (lines[nameLine + 1] != tabs + "{")
                throw new InvalidOperationException("no opening brace after line " + nameLine + ": " + lines[nameLine]);
            for (int i = nameLine + 2; i < lines.Count; i++)
                if (lines[i] == tabs + "}")
                    return i;
            throw new InvalidOperationException("unterminated node at line " + nameLine);
        }

        private static int FindScenario(List<string> lines, string name)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i] != "\tSCENARIO") continue;
                int end = BlockEnd(lines, i);
                for (int j = i + 2; j < end; j++)
                    if (lines[j] == "\t\tname = " + name)
                        return i;
            }
            throw new InvalidOperationException("SCENARIO " + name + " not found");
        }

        private static int FindChild(List<string> lines, int parent, string childName)
        {
            string tabs = new string('\t', IndentOf(lines[parent]) + 1);
            int end = BlockEnd(lines, parent);
            for (int i = parent + 2; i < end; i++)
                if (lines[i] == tabs + childName && lines[i + 1] == tabs + "{")
                    return i;
            throw new InvalidOperationException(childName + " not found under line " + parent);
        }

        private static int FindChildWithValue(List<string> lines, int parent, string childName, string key, string value)
        {
            string tabs = new string('\t', IndentOf(lines[parent]) + 1);
            int end = BlockEnd(lines, parent);
            for (int i = parent + 2; i < end; i++)
            {
                if (lines[i] != tabs + childName || lines[i + 1] != tabs + "{") continue;
                int childEnd = BlockEnd(lines, i);
                for (int j = i + 2; j < childEnd; j++)
                    if (lines[j] == tabs + "\t" + key + " = " + value)
                        return i;
                i = childEnd;
            }
            throw new InvalidOperationException(childName + " with " + key + " = " + value + " not found");
        }

        private static void ReplaceExactlyOnce(List<string> lines, string from, string to)
        {
            int at = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i] != from) continue;
                if (at >= 0) throw new InvalidOperationException("more than one line reads '" + from + "'");
                at = i;
            }
            if (at < 0) throw new InvalidOperationException("no line reads '" + from + "'");
            lines[at] = to;
        }

        private static void ReplaceInBlockExactlyOnce(List<string> lines, int block, string from, string to)
        {
            int end = BlockEnd(lines, block);
            int at = -1;
            for (int i = block + 2; i < end; i++)
            {
                if (lines[i] != from) continue;
                if (at >= 0) throw new InvalidOperationException("more than one line reads '" + from + "' in the block");
                at = i;
            }
            if (at < 0) throw new InvalidOperationException("no line reads '" + from + "' in the block at " + block);
            lines[at] = to;
        }

        private static void RemoveExactlyOnce(List<string> lines, int block, string line)
        {
            int end = BlockEnd(lines, block);
            for (int i = block + 2; i < end; i++)
            {
                if (lines[i] != line) continue;
                lines.RemoveAt(i);
                return;
            }
            throw new InvalidOperationException("no line reads '" + line + "' in the block at " + block);
        }
    }
}
