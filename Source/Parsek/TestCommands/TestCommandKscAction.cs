using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Parsek.TestCommands
{
    /// <summary>The <c>KscAction</c> sub-action selected by the <c>action</c> arg.</summary>
    internal enum KscActionKind
    {
        ResearchNode,
        UpgradeFacility,
        HireKerbal,
        DismissKerbal,

        /// <summary>An unrecognized <c>action</c> arg (REJECTED unknown-action).</summary>
        Unknown,
    }

    /// <summary>
    /// Pure accept-or-typed-refusal decision for a <c>KscAction</c> sub-action, mirroring
    /// <see cref="SettingWhitelist.TryApply"/>'s pure-decider split. The addon RESOLVES the
    /// live target (an <c>RDTech</c>, a live SPACECENTER-scene <c>SpaceCenterBuilding</c>, a
    /// <c>ProtoCrewMember</c>) and reads the current cost / balance / state, passes the
    /// primitives to <see cref="TestCommandKscAction.Decide"/>, and this returns accept or a
    /// typed refusal. The applier then invokes the real stock API only on accept and CONFIRMS
    /// the effect before reporting OK. The verb never writes a ledger row.
    /// </summary>
    internal struct KscActionDecision
    {
        public bool Accepted;

        /// <summary>Reject reason when <see cref="Accepted"/> is false: unknown-action /
        /// missing-arg / unknown-tech-node / node-already-unlocked / insufficient-science /
        /// unknown-facility / facility-at-max / insufficient-funds / unknown-kerbal /
        /// kerbal-not-applicant / kerbal-not-dismissable / kerbal-parsek-managed.</summary>
        public string RejectReason;

        public KscActionKind Kind;
        public string Target;

        /// <summary>The seam-declared manifest kind the harness attaches (M-B2):
        /// tech-unlock / facility-upgrade / kerbal-hire / kerbal-dismiss.</summary>
        public string ManifestKind;
    }

    /// <summary>
    /// The current-state / cost primitives the addon samples from live KSP for the pure
    /// <see cref="TestCommandKscAction.Decide"/> to reason over. Only the fields relevant to
    /// the resolved <see cref="KscActionKind"/> are read; the rest stay default.
    /// </summary>
    internal struct KscActionInputs
    {
        /// <summary>The action-specific target arg (node / facility / kerbal) is non-empty.</summary>
        public bool ArgPresent;

        /// <summary>The live target resolved (tech node in the tree / building for the id /
        /// kerbal exists in the roster).</summary>
        public bool TargetResolves;

        /// <summary>research: node already researched; upgrade: facility already at max level.</summary>
        public bool AlreadyApplied;

        /// <summary>hire: the resolved kerbal is in the applicant pool.</summary>
        public bool IsApplicant;

        /// <summary>dismiss: the kerbal is removable (not assigned / not a tourist).</summary>
        public bool IsDismissable;

        /// <summary>dismiss: <c>LedgerOrchestrator.Kerbals.IsManaged(name)</c> is true.</summary>
        public bool IsParsekManaged;

        /// <summary>The action cost (science for research; funds for upgrade / hire).</summary>
        public double CostAmount;

        /// <summary>The available balance for the cost facet (science pool or funds pool).</summary>
        public double AvailableAmount;

        /// <summary>false = the cost facet is science; true = funds. Selects the
        /// insufficient-science vs insufficient-funds refusal.</summary>
        public bool CostIsFunds;
    }

    internal static class TestCommandKscAction
    {
        /// <summary>Exact kebab-case parse of the <c>action</c> arg into a
        /// <see cref="KscActionKind"/>; anything else -&gt; <see cref="KscActionKind.Unknown"/>.</summary>
        internal static KscActionKind ParseKind(string action)
        {
            switch (action)
            {
                case "research-node": return KscActionKind.ResearchNode;
                case "upgrade-facility": return KscActionKind.UpgradeFacility;
                case "hire-kerbal": return KscActionKind.HireKerbal;
                case "dismiss-kerbal": return KscActionKind.DismissKerbal;
                default: return KscActionKind.Unknown;
            }
        }

        /// <summary>The seam-declared manifest kind (M-B2) for a sub-action; empty for
        /// <see cref="KscActionKind.Unknown"/>.</summary>
        internal static string ManifestKindFor(KscActionKind kind)
        {
            switch (kind)
            {
                case KscActionKind.ResearchNode: return "tech-unlock";
                case KscActionKind.UpgradeFacility: return "facility-upgrade";
                case KscActionKind.HireKerbal: return "kerbal-hire";
                case KscActionKind.DismissKerbal: return "kerbal-dismiss";
                default: return string.Empty;
            }
        }

        /// <summary>
        /// Pure accept / typed-refusal decision. Order: unknown-action, then missing-arg,
        /// then unknown-target (per-kind reason), then the per-kind idempotency /
        /// dismissability / affordability boundaries. On accept the field is never touched
        /// here (that is the applier's job); the applier still CONFIRMS the stock call's
        /// effect before reporting OK (a committed-action guard patch can silently block it).
        /// </summary>
        internal static KscActionDecision Decide(string action, string target, KscActionInputs inputs)
        {
            KscActionKind kind = ParseKind(action);
            var d = new KscActionDecision
            {
                Kind = kind,
                Target = target,
                ManifestKind = ManifestKindFor(kind),
                Accepted = false,
            };

            if (kind == KscActionKind.Unknown)
            {
                d.RejectReason = "unknown-action";
                return d;
            }

            if (!inputs.ArgPresent)
            {
                d.RejectReason = "missing-arg";
                return d;
            }

            if (!inputs.TargetResolves)
            {
                d.RejectReason = UnknownTargetReason(kind);
                return d;
            }

            switch (kind)
            {
                case KscActionKind.ResearchNode:
                    if (inputs.AlreadyApplied) { d.RejectReason = "node-already-unlocked"; return d; }
                    if (inputs.CostAmount > inputs.AvailableAmount) { d.RejectReason = "insufficient-science"; return d; }
                    break;

                case KscActionKind.UpgradeFacility:
                    if (inputs.AlreadyApplied) { d.RejectReason = "facility-at-max"; return d; }
                    if (inputs.CostAmount > inputs.AvailableAmount) { d.RejectReason = "insufficient-funds"; return d; }
                    break;

                case KscActionKind.HireKerbal:
                    if (!inputs.IsApplicant) { d.RejectReason = "kerbal-not-applicant"; return d; }
                    if (inputs.CostAmount > inputs.AvailableAmount) { d.RejectReason = "insufficient-funds"; return d; }
                    break;

                case KscActionKind.DismissKerbal:
                    if (inputs.IsParsekManaged) { d.RejectReason = "kerbal-parsek-managed"; return d; }
                    if (!inputs.IsDismissable) { d.RejectReason = "kerbal-not-dismissable"; return d; }
                    break;
            }

            d.Accepted = true;
            return d;
        }

        private static string UnknownTargetReason(KscActionKind kind)
        {
            switch (kind)
            {
                case KscActionKind.ResearchNode: return "unknown-tech-node";
                case KscActionKind.UpgradeFacility: return "unknown-facility";
                case KscActionKind.HireKerbal:
                case KscActionKind.DismissKerbal: return "unknown-kerbal";
                default: return "unknown-target";
            }
        }

        // ------------------------------------------------------------------
        // Unity applier: resolve the live target, call the pure Decide, invoke the real
        // stock API on accept, and CONFIRM the effect before reporting OK. All KSP-touching
        // work is isolated here; the pure Decide / ParseKind above stay xUnit-covered. The
        // outcome is mapped to SetExecResult by the addon body (sibling-owned).
        // ------------------------------------------------------------------

        private const string Tag = "TestCommands";

        /// <summary>
        /// Result the addon maps to <c>SetExecResult(Verdict, Payload, Msg)</c>. A refusal
        /// (pure Decide decline OR a guard-blocked stock call) is <c>REJECTED</c> with the
        /// reason in <see cref="Msg"/>; a confirmed effect is <c>OK</c> with the payload.
        /// </summary>
        internal struct KscActionExecOutcome
        {
            public string Verdict;
            public List<KeyValuePair<string, string>> Payload;
            public string Msg;

            internal static KscActionExecOutcome Reject(string reason)
                => new KscActionExecOutcome { Verdict = "REJECTED", Payload = null, Msg = reason };

            internal static KscActionExecOutcome Ok(List<KeyValuePair<string, string>> payload)
                => new KscActionExecOutcome { Verdict = "OK", Payload = payload, Msg = null };
        }

        /// <summary>
        /// Perform a <c>KscAction</c> sub-action against live KSP. The addon extracts the
        /// args (<c>action</c>, plus <c>node</c> / <c>facility</c> / <c>kerbal</c>) and passes
        /// them here; the scene / career-readiness gates are already applied at dispatch. On
        /// accept the real stock method is invoked and its EFFECT confirmed before OK; a
        /// guard-blocked call (no observed effect) is REJECTED <c>blocked-committed</c>.
        /// </summary>
        internal static KscActionExecOutcome Execute(string action, string node, string facility, string kerbal)
        {
            KscActionKind kind = ParseKind(action);
            switch (kind)
            {
                case KscActionKind.ResearchNode: return ExecuteResearchNode(node);
                case KscActionKind.UpgradeFacility: return ExecuteUpgradeFacility(facility);
                case KscActionKind.HireKerbal: return ExecuteHireKerbal(kerbal);
                case KscActionKind.DismissKerbal: return ExecuteDismissKerbal(kerbal);
                default:
                    ParsekLog.Warn(Tag, "kscaction refused action=" + (action ?? string.Empty) + " reason=unknown-action target=");
                    return KscActionExecOutcome.Reject("unknown-action");
            }
        }

        private static KscActionExecOutcome Refuse(string action, string target, string reason)
        {
            ParsekLog.Warn(Tag, string.Format(CultureInfo.InvariantCulture,
                "kscaction refused action={0} reason={1} target={2}", action, reason, target ?? string.Empty));
            return KscActionExecOutcome.Reject(reason);
        }

        private static List<KeyValuePair<string, string>> OkPayload(
            string action, string target, string observedKey, string observedValue)
        {
            var p = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("action", action ?? string.Empty),
                new KeyValuePair<string, string>("target", target ?? string.Empty),
                new KeyValuePair<string, string>("applied", "true"),
            };
            if (observedKey != null)
                p.Add(new KeyValuePair<string, string>(observedKey, observedValue ?? string.Empty));
            return p;
        }

        private static void LogApplied(string action, string target, string manifestKind, string observed)
        {
            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "kscaction action={0} target={1} applied=true manifestKind={2} observedAfter={3}",
                action, target ?? string.Empty, manifestKind, observed));
        }

        private static KscActionExecOutcome ExecuteResearchNode(string node)
        {
            const string action = "research-node";
            bool argPresent = !string.IsNullOrEmpty(node);

            RDTech tech = argPresent ? ResolveTech(node) : null;
            bool alreadyResearched = argPresent
                && ResearchAndDevelopment.GetTechnologyState(node) == RDTech.State.Available;
            double science = ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0.0;
            double cost = tech != null ? tech.scienceCost : 0.0;

            var inputs = new KscActionInputs
            {
                ArgPresent = argPresent,
                TargetResolves = tech != null,
                AlreadyApplied = alreadyResearched,
                CostAmount = cost,
                AvailableAmount = science,
                CostIsFunds = false,
            };

            KscActionDecision d = Decide(action, node, inputs);
            if (!d.Accepted)
                return Refuse(action, node, d.RejectReason);

            // Drive the real stock research-buy path (spends science, fires UnlockTech).
            try { tech.ResearchTech(); }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag, "kscaction research-node ResearchTech threw: " + ex.GetType().Name + ": " + ex.Message);
            }

            // Confirm the effect: a committed-action guard (TechResearchSpendPatch) can
            // silently block the buy, leaving the node un-researched.
            bool researchedNow = ResearchAndDevelopment.GetTechnologyState(node) == RDTech.State.Available;
            if (!researchedNow)
                return Refuse(action, node, "blocked-committed");

            double scienceAfter = ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0.0;
            string observed = scienceAfter.ToString("R", CultureInfo.InvariantCulture);
            LogApplied(action, node, d.ManifestKind, "science=" + observed);
            return KscActionExecOutcome.Ok(OkPayload(action, node, "scienceAfter", observed));
        }

        private static RDTech ResolveTech(string node)
        {
            if (AssetBase.RnDTechTree == null) return null;
            // RDNode.tech is the real RDTech the RnD UI button drives (ResearchTech spends
            // science and fires UnlockTech, and is the method TechResearchSpendPatch gates).
            ProtoRDNode[] nodes = AssetBase.RnDTechTree.GetTreeNodes();
            if (nodes == null) return null;
            for (int i = 0; i < nodes.Length; i++)
            {
                ProtoRDNode n = nodes[i];
                if (n != null && n.tech != null && n.tech.techID == node)
                {
                    // Build the real RDTech the RnD UI drives from the config proto node
                    // (techID / scienceCost). ResearchTech() then spends science and fires
                    // UnlockTech, and is the method TechResearchSpendPatch gates.
                    ProtoTechNode proto = n.tech;
                    RDTech tech = new RDTech
                    {
                        techID = proto.techID,
                        scienceCost = proto.scienceCost,
                    };
                    return tech;
                }
            }
            return null;
        }

        private static KscActionExecOutcome ExecuteUpgradeFacility(string facility)
        {
            const string action = "upgrade-facility";
            bool argPresent = !string.IsNullOrEmpty(facility);

            SpaceCenterBuilding building = argPresent ? ResolveBuilding(facility) : null;
            Upgradeables.UpgradeableFacility fac = building != null ? building.Facility : null;
            bool atMax = fac != null && fac.FacilityLevel >= fac.MaxLevel;
            double cost = building != null ? SafeUpgradeCost(building) : 0.0;
            double funds = Funding.Instance != null ? Funding.Instance.Funds : 0.0;

            var inputs = new KscActionInputs
            {
                ArgPresent = argPresent,
                TargetResolves = building != null,
                AlreadyApplied = atMax,
                CostAmount = cost,
                AvailableAmount = funds,
                CostIsFunds = true,
            };

            KscActionDecision d = Decide(action, facility, inputs);
            if (!d.Accepted)
                return Refuse(action, facility, d.RejectReason);

            int levelBefore = fac.FacilityLevel;
            try { InvokeUpgradeFacility(building); }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag, "kscaction upgrade-facility UpgradeFacility threw: " + ex.GetType().Name + ": " + ex.Message);
            }

            // Confirm: FacilityUpgradeSpendPatch can block a committed upgrade (no debit,
            // no level bump).
            int levelAfter = building.Facility != null ? building.Facility.FacilityLevel : levelBefore;
            if (levelAfter <= levelBefore)
                return Refuse(action, facility, "blocked-committed");

            string observed = levelAfter.ToString(CultureInfo.InvariantCulture);
            LogApplied(action, facility, d.ManifestKind, "level=" + observed);
            return KscActionExecOutcome.Ok(OkPayload(action, facility, "level", observed));
        }

        private static SpaceCenterBuilding ResolveBuilding(string facility)
        {
            SpaceCenterBuilding[] buildings = UnityEngine.Object.FindObjectsOfType<SpaceCenterBuilding>();
            if (buildings == null) return null;
            for (int i = 0; i < buildings.Length; i++)
            {
                var b = buildings[i];
                if (b == null || b.Facility == null) continue;
                string id = b.Facility.id;
                if (string.IsNullOrEmpty(id)) continue;
                if (id == facility || id.EndsWith("/" + facility))
                    return b;
            }
            return null;
        }

        // SpaceCenterBuilding.UpgradeFacility(bool) and GetUpgradeCost() are non-public
        // instance methods (KSP gates them via Harmony by name for exactly this reason), so
        // the seam invokes them by reflection. Invoking UpgradeFacility still runs the real
        // stock method AND the FacilityUpgradeSpendPatch prefix (Harmony patches the method
        // itself), so a committed-upgrade block still fires.
        private static readonly MethodInfo UpgradeFacilityMethod =
            typeof(SpaceCenterBuilding).GetMethod("UpgradeFacility",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(bool) }, null);

        private static readonly MethodInfo GetUpgradeCostMethod =
            typeof(SpaceCenterBuilding).GetMethod("GetUpgradeCost",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, System.Type.EmptyTypes, null);

        private static void InvokeUpgradeFacility(SpaceCenterBuilding building)
        {
            if (UpgradeFacilityMethod == null)
            {
                ParsekLog.Warn(Tag, "kscaction upgrade-facility: SpaceCenterBuilding.UpgradeFacility(bool) not found via reflection");
                return;
            }
            UpgradeFacilityMethod.Invoke(building, new object[] { true });
        }

        private static double SafeUpgradeCost(SpaceCenterBuilding building)
        {
            try
            {
                if (GetUpgradeCostMethod == null) return 0.0;
                object v = GetUpgradeCostMethod.Invoke(building, null);
                return v is float f ? f : (v is double dd ? dd : 0.0);
            }
            catch { return 0.0; }
        }

        private static KscActionExecOutcome ExecuteHireKerbal(string kerbal)
        {
            const string action = "hire-kerbal";
            bool argPresent = !string.IsNullOrEmpty(kerbal);
            KerbalRoster roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;

            ProtoCrewMember applicant = null;
            bool existsAnywhere = false;
            if (argPresent && roster != null)
            {
                applicant = roster.Applicants.FirstOrDefault(a => a != null && a.name == kerbal);
                existsAnywhere = applicant != null || roster.Exists(kerbal);
            }

            int activeCrew = roster != null ? SafeActiveCrewCount(roster) : 0;
            double cost = GameStateRecorder.ComputeHireCost(activeCrew);
            double funds = Funding.Instance != null ? Funding.Instance.Funds : 0.0;

            var inputs = new KscActionInputs
            {
                ArgPresent = argPresent,
                TargetResolves = existsAnywhere,
                IsApplicant = applicant != null,
                CostAmount = cost,
                AvailableAmount = funds,
                CostIsFunds = true,
            };

            KscActionDecision d = Decide(action, kerbal, inputs);
            if (!d.Accepted)
                return Refuse(action, kerbal, d.RejectReason);

            // Mirror the stock debit (the Astronaut Complex UI charges; HireApplicant does
            // not). The CrewRecruited reason key is load-bearing for the ledger classifier.
            if (Funding.Instance != null)
                Funding.Instance.AddFunds(-cost, TransactionReasons.CrewRecruited);
            try { roster.HireApplicant(applicant); }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag, "kscaction hire-kerbal HireApplicant threw: " + ex.GetType().Name + ": " + ex.Message);
            }

            // Confirm: KerbalHirePatch can block a committed hire (applicant stays an
            // applicant). If blocked, refund the debit we mirrored so we leave no residue.
            bool hiredNow = applicant.type == ProtoCrewMember.KerbalType.Crew;
            if (!hiredNow)
            {
                if (Funding.Instance != null)
                    Funding.Instance.AddFunds(cost, TransactionReasons.CrewRecruited);
                return Refuse(action, kerbal, "blocked-committed");
            }

            double fundsAfter = Funding.Instance != null ? Funding.Instance.Funds : 0.0;
            string observed = fundsAfter.ToString("R", CultureInfo.InvariantCulture);
            LogApplied(action, kerbal, d.ManifestKind, "funds=" + observed);
            return KscActionExecOutcome.Ok(OkPayload(action, kerbal, "fundsAfter", observed));
        }

        private static int SafeActiveCrewCount(KerbalRoster roster)
        {
            try { return roster.GetActiveCrewCount(); }
            catch { return 0; }
        }

        private static KscActionExecOutcome ExecuteDismissKerbal(string kerbal)
        {
            const string action = "dismiss-kerbal";
            bool argPresent = !string.IsNullOrEmpty(kerbal);
            KerbalRoster roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;

            ProtoCrewMember crew = (argPresent && roster != null) ? roster[kerbal] : null;
            bool managed = argPresent && (LedgerOrchestrator.Kerbals?.IsManaged(kerbal) ?? false);
            bool dismissable = crew != null
                && crew.rosterStatus != ProtoCrewMember.RosterStatus.Assigned
                && crew.type != ProtoCrewMember.KerbalType.Tourist;

            var inputs = new KscActionInputs
            {
                ArgPresent = argPresent,
                TargetResolves = crew != null,
                IsParsekManaged = managed,
                IsDismissable = dismissable,
            };

            KscActionDecision d = Decide(action, kerbal, inputs);
            if (!d.Accepted)
                return Refuse(action, kerbal, d.RejectReason);

            try { roster.Remove(crew); }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag, "kscaction dismiss-kerbal Remove threw: " + ex.GetType().Name + ": " + ex.Message);
            }

            // Confirm: KerbalDismissalPatch's IsManaged block can refuse the stock Remove.
            bool removedNow = !roster.Exists(kerbal);
            if (!removedNow)
                return Refuse(action, kerbal, "blocked-committed");

            int crewCount = SafeActiveCrewCount(roster);
            string observed = crewCount.ToString(CultureInfo.InvariantCulture);
            LogApplied(action, kerbal, d.ManifestKind, "crewCount=" + observed);
            return KscActionExecOutcome.Ok(OkPayload(action, kerbal, "crewCount", observed));
        }
    }
}
