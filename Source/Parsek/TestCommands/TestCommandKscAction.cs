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

        /// <summary>Collapse one KSC building (<c>DestructibleBuilding.Demolish()</c>), the
        /// same call a crash's <c>AddDamage</c> makes.</summary>
        DemolishBuilding,

        /// <summary>Repair a facility's destroyed buildings through the KSC menu's own
        /// call, <c>SpaceCenterBuilding.RepairFacility(true)</c> (funds deducted).</summary>
        RepairFacility,

        /// <summary>Activate one stock strategy through the Administration building's own
        /// call, <c>Strategy.Activate()</c> (setup costs deducted by stock).</summary>
        ActivateStrategy,

        /// <summary>End an active stock strategy through <c>Strategy.Deactivate()</c>, the
        /// Administration building's Cancel path.</summary>
        DeactivateStrategy,

        /// <summary>Accept one Offered contract through Mission Control's own call,
        /// <c>Contract.Accept()</c> (the Accept button's <c>OnClickAccept</c> ends in it; stock
        /// pays the advance).</summary>
        AcceptContract,

        /// <summary>Decline one Offered contract through Mission Control's own call,
        /// <c>Contract.Decline()</c> (the Decline button's <c>OnClickDecline</c> ends in it).</summary>
        DeclineContract,

        /// <summary>Cancel one Active contract through Mission Control's own call,
        /// <c>Contract.Cancel()</c> (the Cancel button's <c>OnClickCancel</c> ends in it; stock
        /// charges the cancellation penalty).</summary>
        CancelContract,

        /// <summary>Dismiss one Available kerbal through the Astronaut Complex's own call,
        /// <c>KerbalRoster.SackAvailable</c> (the row's dismiss button,
        /// <c>AstronautComplex.Xbutton_AvailableCrew</c>, ends in it; the kerbal becomes an
        /// applicant again).</summary>
        SackKerbal,

        /// <summary>Buy one part of a researched node through the R&amp;D purchase primitive,
        /// <c>RDTech.PurchasePart(AvailablePart)</c> (the R&amp;D part tooltip's Purchase and
        /// purchase-all end in it; stock charges the entry cost).</summary>
        PurchasePart,

        /// <summary>Press the Administration building's Accept button on the SELECTED
        /// strategy: <c>Administration.BtnInputAccept("accept")</c>, the button's own handler,
        /// which opens stock's activation confirmation.</summary>
        PressStrategyAccept,

        /// <summary>Press the Administration building's Cancel button on the SELECTED active
        /// strategy: <c>Administration.BtnInputAccept("cancel")</c>, which opens stock's
        /// deactivation confirmation.</summary>
        PressStrategyCancel,

        /// <summary>Put one kerbal in a seat through the VAB/SPH crew dialog's own handlers:
        /// the row's assign button (<c>ListItemButtonClick(V)</c>, which ends in
        /// <c>MoveCrewToEmptySeat</c>) when the craft has an empty seat, else a drop of the row
        /// onto the first seat (<c>DropOnCrewList</c>, the drag's end).</summary>
        SeatCrew,

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
        /// kerbal-not-applicant / kerbal-not-dismissable / kerbal-parsek-managed /
        /// unknown-building / building-already-down / facility-intact / unknown-strategy /
        /// factor-arg-invalid / strategy-already-active / no-strategy-slot /
        /// strategy-not-active / unknown-contract / contract-not-offered. The building
        /// applier adds demolish-not-applied / repair-not-applied when stock's call left no effect; the strategy applier adds
        /// strategy-cannot-activate / strategy-cannot-deactivate (carrying stock's own reason)
        /// and activate-not-applied / deactivate-not-applied.</summary>
        public string RejectReason;

        public KscActionKind Kind;
        public string Target;

        /// <summary>The seam-declared manifest kind the harness attaches (M-B2):
        /// tech-unlock / facility-upgrade / kerbal-hire / kerbal-dismiss /
        /// facility-destruction / facility-repair / strategy-activate / strategy-deactivate /
        /// contract-accept.</summary>
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

        /// <summary>research: node already researched; upgrade: facility already at max level;
        /// demolish: the building is not intact (down, or mid-collapse / mid-repair); repair:
        /// no building of the facility is destroyed; accept: the contract is not Offered.</summary>
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

        /// <summary>activate-strategy: the optional <c>factor</c> arg was present but did not
        /// parse as a commitment in (0, 1].</summary>
        public bool FactorArgInvalid;

        /// <summary>activate-strategy: strategies active now.</summary>
        public int SlotsUsed;

        /// <summary>activate-strategy: the Administration level's active-strategy limit.</summary>
        public int SlotLimit;

        /// <summary>purchase-part: the part's own tech node is researched in stock.</summary>
        public bool TechResearched;
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
                case "demolish-building": return KscActionKind.DemolishBuilding;
                case "repair-facility": return KscActionKind.RepairFacility;
                case "activate-strategy": return KscActionKind.ActivateStrategy;
                case "deactivate-strategy": return KscActionKind.DeactivateStrategy;
                case "accept-contract": return KscActionKind.AcceptContract;
                case "decline-contract": return KscActionKind.DeclineContract;
                case "cancel-contract": return KscActionKind.CancelContract;
                case "sack-kerbal": return KscActionKind.SackKerbal;
                case "purchase-part": return KscActionKind.PurchasePart;
                case "press-strategy-accept": return KscActionKind.PressStrategyAccept;
                case "press-strategy-cancel": return KscActionKind.PressStrategyCancel;
                case "seat-crew": return KscActionKind.SeatCrew;
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
                case KscActionKind.DemolishBuilding: return "facility-destruction";
                case KscActionKind.RepairFacility: return "facility-repair";
                case KscActionKind.ActivateStrategy: return "strategy-activate";
                case KscActionKind.DeactivateStrategy: return "strategy-deactivate";
                case KscActionKind.AcceptContract: return "contract-accept";
                case KscActionKind.DeclineContract: return "contract-decline";
                case KscActionKind.CancelContract: return "contract-cancel";
                case KscActionKind.SackKerbal: return "kerbal-dismiss";
                case KscActionKind.PurchasePart: return "part-purchase";
                case KscActionKind.PressStrategyAccept: return "strategy-activate";
                case KscActionKind.PressStrategyCancel: return "strategy-deactivate";
                case KscActionKind.SeatCrew: return "crew-assign";
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

                case KscActionKind.DemolishBuilding:
                    if (inputs.AlreadyApplied) { d.RejectReason = "building-already-down"; return d; }
                    break;

                case KscActionKind.RepairFacility:
                    if (inputs.AlreadyApplied) { d.RejectReason = "facility-intact"; return d; }
                    if (inputs.CostAmount > inputs.AvailableAmount) { d.RejectReason = "insufficient-funds"; return d; }
                    break;

                case KscActionKind.ActivateStrategy:
                    // Stock's CanBeActivated is the authority on costs, reputation, group
                    // conflicts and the commitment ceiling (the applier asks it next and
                    // refuses with its own reason); the three checked here are the ones a
                    // spec can get wrong without stock ever being asked.
                    if (inputs.FactorArgInvalid) { d.RejectReason = "factor-arg-invalid"; return d; }
                    if (inputs.AlreadyApplied) { d.RejectReason = "strategy-already-active"; return d; }
                    if (inputs.SlotsUsed >= inputs.SlotLimit) { d.RejectReason = "no-strategy-slot"; return d; }
                    break;

                case KscActionKind.DeactivateStrategy:
                    if (inputs.AlreadyApplied) { d.RejectReason = "strategy-not-active"; return d; }
                    break;

                case KscActionKind.AcceptContract:
                    // Stock's Mission Control greys Accept when every slot is used; the seam
                    // does not model the slot count, so a spec pressing Accept over a full
                    // Mission Control reaches stock's Contract.Accept as a caller other than
                    // the button would.
                    if (inputs.AlreadyApplied) { d.RejectReason = "contract-not-offered"; return d; }
                    break;

                case KscActionKind.DeclineContract:
                    if (inputs.AlreadyApplied) { d.RejectReason = "contract-not-offered"; return d; }
                    break;

                case KscActionKind.CancelContract:
                    if (inputs.AlreadyApplied) { d.RejectReason = "contract-not-active"; return d; }
                    break;

                case KscActionKind.SackKerbal:
                    // No Parsek-managed pre-check (dismiss-kerbal has one): the
                    // committed-timeline refusal of a managed kerbal is exactly what this
                    // sub-action presses, so it must reach stock's call.
                    if (!inputs.IsDismissable) { d.RejectReason = "kerbal-not-dismissable"; return d; }
                    break;

                case KscActionKind.PurchasePart:
                    if (!inputs.TechResearched) { d.RejectReason = "tech-not-researched"; return d; }
                    if (inputs.AlreadyApplied) { d.RejectReason = "part-already-purchased"; return d; }
                    if (inputs.CostAmount > inputs.AvailableAmount) { d.RejectReason = "insufficient-funds"; return d; }
                    break;

                case KscActionKind.PressStrategyAccept:
                    if (inputs.AlreadyApplied) { d.RejectReason = "strategy-already-active"; return d; }
                    break;

                case KscActionKind.PressStrategyCancel:
                    if (inputs.AlreadyApplied) { d.RejectReason = "strategy-not-active"; return d; }
                    break;

                case KscActionKind.SeatCrew:
                    if (inputs.AlreadyApplied) { d.RejectReason = "kerbal-already-seated"; return d; }
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
                case KscActionKind.UpgradeFacility:
                case KscActionKind.RepairFacility: return "unknown-facility";
                case KscActionKind.DemolishBuilding: return "unknown-building";
                case KscActionKind.HireKerbal:
                case KscActionKind.DismissKerbal:
                case KscActionKind.SackKerbal:
                case KscActionKind.SeatCrew: return "unknown-kerbal";
                case KscActionKind.ActivateStrategy:
                case KscActionKind.DeactivateStrategy:
                case KscActionKind.PressStrategyAccept:
                case KscActionKind.PressStrategyCancel: return "unknown-strategy";
                case KscActionKind.AcceptContract:
                case KscActionKind.DeclineContract:
                case KscActionKind.CancelContract: return "unknown-contract";
                case KscActionKind.PurchasePart: return "unknown-part";
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
        internal static KscActionExecOutcome Execute(
            string action, string node, string facility, string kerbal, string building = null,
            string strategy = null, string factor = null, string contract = null, string part = null)
        {
            KscActionKind kind = ParseKind(action);
            switch (kind)
            {
                case KscActionKind.ResearchNode: return ExecuteResearchNode(node);
                case KscActionKind.UpgradeFacility: return ExecuteUpgradeFacility(facility);
                case KscActionKind.HireKerbal: return ExecuteHireKerbal(kerbal);
                case KscActionKind.DismissKerbal: return ExecuteDismissKerbal(kerbal);
                case KscActionKind.DemolishBuilding: return ExecuteDemolishBuilding(building);
                case KscActionKind.RepairFacility: return ExecuteRepairFacility(facility);
                case KscActionKind.ActivateStrategy: return ExecuteActivateStrategy(strategy, factor);
                case KscActionKind.DeactivateStrategy: return ExecuteDeactivateStrategy(strategy);
                case KscActionKind.AcceptContract: return ExecuteAcceptContract(contract);
                case KscActionKind.DeclineContract: return ExecuteDeclineContract(contract);
                case KscActionKind.CancelContract: return ExecuteCancelContract(contract);
                case KscActionKind.SackKerbal: return ExecuteSackKerbal(kerbal);
                case KscActionKind.PurchasePart: return ExecutePurchasePart(part);
                case KscActionKind.PressStrategyAccept: return ExecutePressStrategyButton(action, strategy, true);
                case KscActionKind.PressStrategyCancel: return ExecutePressStrategyButton(action, strategy, false);
                case KscActionKind.SeatCrew: return ExecuteSeatCrew(kerbal);
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

            ProtoTechNode proto = argPresent ? ResolveProtoTech(node) : null;
            bool alreadyResearched = argPresent
                && ResearchAndDevelopment.GetTechnologyState(node) == RDTech.State.Available;
            double science = ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0.0;
            double cost = proto != null ? proto.scienceCost : 0.0;

            var inputs = new KscActionInputs
            {
                ArgPresent = argPresent,
                TargetResolves = proto != null,
                AlreadyApplied = alreadyResearched,
                CostAmount = cost,
                AvailableAmount = science,
                CostIsFunds = false,
            };

            KscActionDecision d = Decide(action, node, inputs);
            if (!d.Accepted)
                return Refuse(action, node, d.RejectReason);

            // Drive the real stock research-buy path on a properly-hosted RDTech.
            //
            // RDTech is a MonoBehaviour, so it must NEVER be `new`ed: a `new RDTech` is a
            // detached, host-less shell. With host == null, RDTech.ResearchTech skips its
            // ENTIRE affordability + host.AddScience block (all gated on host != null), and
            // UnlockTech(host != null == false) mutates only the throwaway and never calls
            // host.SetTechState. The result: no science spent, the real R&D node stays
            // unresearched (so the effect-confirm below wrongly REJECTS a valid research),
            // AND UnlockTech still fires a phantom OnTechnologyResearched(Successful) into
            // GameStateRecorder at zero cost (ledger poison). So we build the tech the proven
            // in-repo way (IncompleteBallisticRuntimeTests SpendingGate test): AddComponent it
            // onto a throwaway GameObject, seed it from the proto, set state = Unavailable so
            // ResearchTech takes the purchase path (`if (state != Available)` deduct + unlock),
            // set host = the live R&D singleton so the deduction + SetTechState hit real state,
            // and Warmup() so partsAssigned / partsPurchased are non-null for the
            // UnlockTech(true) -> AutoPurchaseAllParts walk. The GameObject is destroyed in
            // finally so the seam leaves no live RDTech component behind.
            string stateBefore = ResearchAndDevelopment.GetTechnologyState(node).ToString();
            double fundsBefore = LiveFunds();
            RDTech.OperationResult result = RDTech.OperationResult.Failure;
            UnityEngine.GameObject go = null;
            try
            {
                go = new UnityEngine.GameObject("ParsekSeamRDTech");
                RDTech tech = go.AddComponent<RDTech>();
                tech.techID = proto.techID;
                tech.scienceCost = proto.scienceCost;
                tech.state = RDTech.State.Unavailable;
                tech.host = ResearchAndDevelopment.Instance;
                tech.Warmup();
                result = tech.ResearchTech();
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag, "kscaction research-node ResearchTech threw: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (go != null) UnityEngine.Object.Destroy(go);
            }

            // Confirm the effect. TechResearchSpendPatch returns Failure and skips the buy on
            // a committed node, and a Successful result must still register the real node as
            // Available (host.SetTechState). Anything else is a typed refusal, never a false
            // OK: a stock affordability rejection (NotEnoughFunds / ScienceCostLimitExceeded,
            // e.g. from a strategy modifier the raw pool check did not see) maps to
            // insufficient-science; the guard-blocked Failure maps to blocked-committed.
            bool researchedNow = ResearchAndDevelopment.GetTechnologyState(node) == RDTech.State.Available;
            if (result != RDTech.OperationResult.Successful || !researchedNow)
            {
                ParsekLog.Info(Tag, FormatNotAppliedLine(action, node, "state",
                    stateBefore, ResearchAndDevelopment.GetTechnologyState(node).ToString(),
                    fundsBefore, LiveFunds(), science, LiveScience()));
                return Refuse(action, node, MapResearchFailure(result));
            }

            double scienceAfter = ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0.0;
            string observed = scienceAfter.ToString("R", CultureInfo.InvariantCulture);
            LogApplied(action, node, d.ManifestKind, "science=" + observed);
            return KscActionExecOutcome.Ok(OkPayload(action, node, "scienceAfter", observed));
        }

        // Map a non-Successful RDTech.OperationResult to the design's research refusal
        // vocabulary: a stock affordability rejection is insufficient-science; the Failure our
        // own committed-action guard (TechResearchSpendPatch) returns is blocked-committed.
        private static string MapResearchFailure(RDTech.OperationResult result)
        {
            switch (result)
            {
                case RDTech.OperationResult.NotEnoughFunds:
                case RDTech.OperationResult.ScienceCostLimitExceeded:
                    return "insufficient-science";
                default:
                    return "blocked-committed";
            }
        }

        private static ProtoTechNode ResolveProtoTech(string node)
        {
            if (AssetBase.RnDTechTree == null) return null;
            // The proto node carries the node's existence + fixed scienceCost the pure Decide
            // reasons over; the live hosted RDTech is built from it only on accept (above).
            ProtoRDNode[] nodes = AssetBase.RnDTechTree.GetTreeNodes();
            if (nodes == null) return null;
            for (int i = 0; i < nodes.Length; i++)
            {
                ProtoRDNode n = nodes[i];
                if (n != null && n.tech != null && n.tech.techID == node)
                    return n.tech;
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
            double scienceBefore = LiveScience();
            try { InvokeUpgradeFacility(building); }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag, "kscaction upgrade-facility UpgradeFacility threw: " + ex.GetType().Name + ": " + ex.Message);
            }

            // Confirm: FacilityUpgradeSpendPatch can block a committed upgrade (no debit,
            // no level bump).
            int levelAfter = building.Facility != null ? building.Facility.FacilityLevel : levelBefore;
            if (levelAfter <= levelBefore)
            {
                ParsekLog.Info(Tag, FormatNotAppliedLine(action, facility, "level",
                    levelBefore.ToString(CultureInfo.InvariantCulture),
                    levelAfter.ToString(CultureInfo.InvariantCulture),
                    funds, LiveFunds(), scienceBefore, LiveScience()));
                return Refuse(action, facility, "blocked-committed");
            }

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

        /// <summary>
        /// Pure: resolve a building arg against the live DestructibleBuilding ids: an exact
        /// match, else the single id ending in <c>"/" + arg</c> (so a spec can name
        /// <c>ksp_pad_waterTower</c> without the level-specific mesh path). Null when nothing
        /// or more than one id matches.
        /// </summary>
        internal static string ResolveBuildingId(IEnumerable<string> liveIds, string arg)
        {
            if (liveIds == null || string.IsNullOrEmpty(arg))
                return null;
            string suffixMatch = null;
            int suffixMatches = 0;
            foreach (string id in liveIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (id == arg) return id;
                if (id.EndsWith("/" + arg, System.StringComparison.Ordinal))
                {
                    suffixMatch = id;
                    suffixMatches++;
                }
            }
            return suffixMatches == 1 ? suffixMatch : null;
        }

        /// <summary>
        /// Pure: whether any live KSC building is between states. After
        /// <c>Demolish()</c> a building reads not-intact and not-destroyed until its collapse
        /// animation completes; after <c>Repair()</c> the same pair holds until the repair
        /// animation completes. The demolish / repair sub-actions defer on it: stock repairs
        /// only a building whose <c>IsDestroyed</c> is set, and a save taken mid-animation
        /// persists <c>intact = False</c> for a building that is being repaired. Each pair is
        /// (IsIntact, IsDestroyed).
        /// </summary>
        internal static bool AnyStructureSettling(IEnumerable<KeyValuePair<bool, bool>> intactDestroyedPairs)
        {
            if (intactDestroyedPairs == null) return false;
            foreach (var p in intactDestroyedPairs)
            {
                if (!p.Key && !p.Value) return true;
            }
            return false;
        }

        /// <summary>Live read of <see cref="AnyStructureSettling"/> over the scene's buildings.</summary>
        internal static bool LiveStructuresSettling()
        {
            DestructibleBuilding[] all;
            try { all = UnityEngine.Object.FindObjectsOfType<DestructibleBuilding>(); }
            catch { return false; }
            if (all == null) return false;
            var pairs = new List<KeyValuePair<bool, bool>>(all.Length);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null)
                    pairs.Add(new KeyValuePair<bool, bool>(all[i].IsIntact, all[i].IsDestroyed));
            }
            return AnyStructureSettling(pairs);
        }

        private static KscActionExecOutcome ExecuteDemolishBuilding(string buildingArg)
        {
            const string action = "demolish-building";
            bool argPresent = !string.IsNullOrEmpty(buildingArg);

            DestructibleBuilding[] all = UnityEngine.Object.FindObjectsOfType<DestructibleBuilding>();
            DestructibleBuilding db = null;
            if (argPresent && all != null)
            {
                string id = ResolveBuildingId(all.Where(b => b != null).Select(b => b.id), buildingArg);
                if (id != null)
                    db = all.FirstOrDefault(b => b != null && b.id == id);
            }

            var inputs = new KscActionInputs
            {
                ArgPresent = argPresent,
                TargetResolves = db != null,
                AlreadyApplied = db != null && !db.IsIntact,
            };

            KscActionDecision d = Decide(action, buildingArg, inputs);
            if (!d.Accepted)
                return Refuse(action, buildingArg, d.RejectReason);

            try { db.Demolish(); }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag, "kscaction demolish-building Demolish threw: " + ex.GetType().Name + ": " + ex.Message);
            }

            // Confirm: Demolish() clears intact synchronously (destroyed follows when the
            // collapse animation completes).
            if (db.IsIntact)
                return Refuse(action, buildingArg, "demolish-not-applied");

            LogApplied(action, db.id, d.ManifestKind, "intact=false");
            return KscActionExecOutcome.Ok(OkPayload(action, db.id, "intact", "false"));
        }

        private static KscActionExecOutcome ExecuteRepairFacility(string facility)
        {
            const string action = "repair-facility";
            bool argPresent = !string.IsNullOrEmpty(facility);

            SpaceCenterBuilding building = argPresent ? ResolveBuilding(facility) : null;
            int destroyedBefore = building != null ? CountDestroyed(building) : 0;
            double cost = building != null ? SafeRepairCost(building) : 0.0;
            double funds = Funding.Instance != null ? Funding.Instance.Funds : 0.0;

            var inputs = new KscActionInputs
            {
                ArgPresent = argPresent,
                TargetResolves = building != null,
                AlreadyApplied = destroyedBefore == 0,
                CostAmount = cost,
                AvailableAmount = funds,
                CostIsFunds = true,
            };

            KscActionDecision d = Decide(action, facility, inputs);
            if (!d.Accepted)
                return Refuse(action, facility, d.RejectReason);

            // The KSC context menu's own call: RepairFacility(Funding.Instance != null). It
            // runs the CanAfford gate, the StructureRepair debit, then Repair() per building,
            // and FacilityRepairScopePatch wraps it exactly as it wraps the menu's call.
            double fundsBefore = funds;
            try { building.RepairFacility(Funding.Instance != null); }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag, "kscaction repair-facility RepairFacility threw: " + ex.GetType().Name + ": " + ex.Message);
            }

            int destroyedAfter = CountDestroyed(building);
            if (destroyedAfter >= destroyedBefore)
            {
                double fundsNow = Funding.Instance != null ? Funding.Instance.Funds : 0.0;
                ParsekLog.Info(Tag, FormatRepairNotAppliedLine(facility, destroyedBefore, destroyedAfter,
                    fundsBefore, fundsNow));
                return Refuse(action, facility, "repair-not-applied");
            }

            double fundsAfter = Funding.Instance != null ? Funding.Instance.Funds : 0.0;
            string observed = string.Format(CultureInfo.InvariantCulture,
                "repaired={0} cost={1} fundsDelta={2} funds={3}",
                (destroyedBefore - destroyedAfter).ToString(CultureInfo.InvariantCulture),
                // Float, the precision stock computes the cost in, so the value reads the
                // same as the ledger row's FacilityCost.
                ((float)cost).ToString("R", CultureInfo.InvariantCulture),
                ((float)(fundsAfter - fundsBefore)).ToString("R", CultureInfo.InvariantCulture),
                fundsAfter.ToString("R", CultureInfo.InvariantCulture));
            LogApplied(action, facility, d.ManifestKind, observed);
            var payload = OkPayload(action, facility, "repairCost", cost.ToString("R", CultureInfo.InvariantCulture));
            payload.Add(new KeyValuePair<string, string>("fundsAfter", fundsAfter.ToString("R", CultureInfo.InvariantCulture)));
            return KscActionExecOutcome.Ok(payload);
        }

        /// <summary>
        /// The line a repair that left every building down writes before its refusal: the
        /// destroyed counts and the funds pool on both sides of stock's call, so a lane can
        /// read that a refused repair (the committed-repair block) charged nothing.
        /// </summary>
        internal static string FormatRepairNotAppliedLine(
            string facility, int destroyedBefore, int destroyedAfter, double fundsBefore, double fundsAfter)
        {
            var ic = CultureInfo.InvariantCulture;
            return "kscaction repair-facility not applied: target=" + (facility ?? string.Empty)
                + " destroyedBefore=" + destroyedBefore.ToString(ic)
                + " destroyedAfter=" + destroyedAfter.ToString(ic)
                + " fundsBefore=" + fundsBefore.ToString("R", ic)
                + " fundsAfter=" + fundsAfter.ToString("R", ic)
                + " fundsDelta=" + (fundsAfter - fundsBefore).ToString("R", ic);
        }

        /// <summary>
        /// The line a guard-refused research / upgrade / hire / accept writes before its
        /// refusal: the target's own state and the funds and science pools on both sides of
        /// stock's call, so a lane can read that a refused click changed nothing (KB-3 pins
        /// each with a backreference). Keys: <c>&lt;stateKey&gt;Before</c> /
        /// <c>&lt;stateKey&gt;After</c>, then <c>funds*</c> and <c>science*</c> with a delta.
        /// A space in the target or a state is replaced so each value stays one field.
        /// </summary>
        internal static string FormatNotAppliedLine(
            string action, string target, string stateKey, string stateBefore, string stateAfter,
            double fundsBefore, double fundsAfter, double scienceBefore, double scienceAfter)
        {
            var ic = CultureInfo.InvariantCulture;
            return "kscaction " + (action ?? string.Empty) + " not applied: target=" + OneField(target)
                + " " + stateKey + "Before=" + OneField(stateBefore)
                + " " + stateKey + "After=" + OneField(stateAfter)
                + " fundsBefore=" + fundsBefore.ToString("R", ic)
                + " fundsAfter=" + fundsAfter.ToString("R", ic)
                + " fundsDelta=" + (fundsAfter - fundsBefore).ToString("R", ic)
                + " scienceBefore=" + scienceBefore.ToString("R", ic)
                + " scienceAfter=" + scienceAfter.ToString("R", ic)
                + " scienceDelta=" + (scienceAfter - scienceBefore).ToString("R", ic);
        }

        private static string OneField(string value)
            => string.IsNullOrEmpty(value) ? "-" : value.Replace(' ', '_');

        private static double LiveFunds()
            => Funding.Instance != null ? Funding.Instance.Funds : 0.0;

        private static double LiveScience()
            => ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0.0;

        /// <summary>Pure: whether a contract guid string names the arg (case-insensitive,
        /// with or without braces).</summary>
        internal static bool ContractGuidMatches(string guid, string arg)
        {
            if (string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(arg)) return false;
            string a = arg.Trim().TrimStart('{').TrimEnd('}');
            return string.Equals(guid, a, System.StringComparison.OrdinalIgnoreCase);
        }

        private static Contracts.Contract ResolveContract(string arg)
        {
            var system = Contracts.ContractSystem.Instance;
            if (system == null || system.Contracts == null) return null;
            for (int i = 0; i < system.Contracts.Count; i++)
            {
                var c = system.Contracts[i];
                if (c != null && ContractGuidMatches(c.ContractGuid.ToString(), arg))
                    return c;
            }
            return null;
        }

        private static KscActionExecOutcome ExecuteAcceptContract(string contractArg)
        {
            const string action = "accept-contract";
            bool argPresent = !string.IsNullOrEmpty(contractArg);
            Contracts.Contract contract = argPresent ? ResolveContract(contractArg) : null;

            var inputs = new KscActionInputs
            {
                ArgPresent = argPresent,
                TargetResolves = contract != null,
                AlreadyApplied = contract != null && contract.ContractState != Contracts.Contract.State.Offered,
            };

            KscActionDecision d = Decide(action, contractArg, inputs);
            if (!d.Accepted)
                return Refuse(action, contractArg, d.RejectReason);

            // Mission Control's Accept button (MissionControl.OnClickAccept) ends in this
            // call; ContractAcceptPatch refuses it for a contract the committed timeline
            // accepts (or whose slot it needs), before stock pays the advance.
            string stateBefore = contract.ContractState.ToString();
            double fundsBefore = LiveFunds();
            double scienceBefore = LiveScience();
            try { contract.Accept(); }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag, "kscaction accept-contract Accept threw: " + ex.GetType().Name + ": " + ex.Message);
            }

            if (contract.ContractState != Contracts.Contract.State.Active)
            {
                ParsekLog.Info(Tag, FormatNotAppliedLine(action, contractArg, "state",
                    stateBefore, contract.ContractState.ToString(),
                    fundsBefore, LiveFunds(), scienceBefore, LiveScience()));
                return Refuse(action, contractArg, "blocked-committed");
            }

            double fundsAfter = LiveFunds();
            string observed = fundsAfter.ToString("R", CultureInfo.InvariantCulture);
            LogApplied(action, contractArg, d.ManifestKind, "state=Active funds=" + observed);
            var payload = OkPayload(action, contractArg, "state", "Active");
            payload.Add(new KeyValuePair<string, string>("fundsAfter", observed));
            return KscActionExecOutcome.Ok(payload);
        }

        private static int CountDestroyed(SpaceCenterBuilding building)
        {
            if (building == null || building.destructibles == null) return 0;
            int n = 0;
            for (int i = 0; i < building.destructibles.Length; i++)
            {
                var db = building.destructibles[i];
                if (db != null && db.IsDestroyed) n++;
            }
            return n;
        }

        private static double SafeRepairCost(SpaceCenterBuilding building)
        {
            try { return System.Math.Abs(building.GetRepairsCost()); }
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

            // Two-layer affordability. The pure Decide already applied the raw
            // cost > funds precondition (headless-testable, above); the AUTHORITATIVE
            // affordability check is stock's CurrencyModifierQuery here, so any active
            // strategy modifier on CrewRecruited is honored exactly as the Astronaut Complex
            // UI's CanAfford gate would be. A stock decline is insufficient-funds, refused
            // BEFORE the hire runs (and thus before stock's own debit).
            if (!CurrencyModifierQuery
                    .RunQuery(TransactionReasons.CrewRecruited, (float)(-cost), 0f, 0f)
                    .CanAfford())
                return Refuse(action, kerbal, "insufficient-funds");

            // Do NOT debit funds here. Stock charges the recruit cost automatically: the
            // Funding scenario module subscribes to GameEvents.OnCrewmemberHired
            // (Funding.onCrewHired -> AddFunds(-GetRecruitHireCost(count), CrewRecruited)),
            // which KerbalRoster.HireApplicant fires. The Astronaut Complex UI itself never
            // calls AddFunds; it only runs the CanAfford gate above and then HireApplicant.
            // A prior "mirror the stock debit" AddFunds(-cost) here double-charged the pool
            // (seed 500000 -> two -62113 debits -> 375774 instead of 437887), so the ledger
            // oracle flagged a hard divergence. Just drive the hire and let stock charge once.
            string typeBefore = applicant.type.ToString();
            double scienceBefore = LiveScience();
            try { roster.HireApplicant(applicant); }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag, "kscaction hire-kerbal HireApplicant threw: " + ex.GetType().Name + ": " + ex.Message);
            }

            // Confirm: KerbalHirePatch is a Prefix on HireApplicant; on a committed-action
            // block it returns false, so stock HireApplicant never runs, OnCrewmemberHired
            // never fires, and Funding never charges. The applicant stays an applicant and
            // funds are untouched -- no refund needed (there was no debit to undo).
            bool hiredNow = applicant.type == ProtoCrewMember.KerbalType.Crew;
            if (!hiredNow)
            {
                ParsekLog.Info(Tag, FormatNotAppliedLine(action, kerbal, "type",
                    typeBefore, applicant.type.ToString(), funds, LiveFunds(), scienceBefore, LiveScience()));
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

        // ------------------------------------------------------------------
        // Strategies. The Administration building activates a strategy by writing the
        // commitment slider into Strategy.Factor and then calling Strategy.Activate(),
        // which re-runs CanBeActivated and debits the setup costs under
        // TransactionReasons.StrategySetup. The seam does exactly that, so Parsek's
        // StrategyActivatePatch postfix and the StrategySetup funds leg see a player click.
        // CanBeActivated dereferences Administration.Instance (the building's UI screen);
        // the dispatcher defers `administration-not-ready` until the addon has hosted a
        // hidden copy of that screen (ParsekTestCommandAddon.KscStrategy.cs).
        // ------------------------------------------------------------------

        /// <summary>
        /// Pure: parse the optional <c>factor</c> arg (the Administration commitment slider,
        /// 0..1). Absent or empty -&gt; true with <see cref="float.NaN"/> (keep the strategy's
        /// own factor, stock's slider default). Otherwise an invariant-culture float in
        /// (0, 1]; anything else is false.
        /// </summary>
        internal static bool TryParseStrategyFactor(string raw, out float factor)
        {
            factor = float.NaN;
            if (string.IsNullOrEmpty(raw)) return true;
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                return false;
            if (float.IsNaN(f) || float.IsInfinity(f) || f <= 0f || f > 1f)
                return false;
            factor = f;
            return true;
        }

        /// <summary>
        /// Pure: reduce stock's localized CanBeActivated / CanBeDeactivated reason to one
        /// plain line for the refusal msg: rich-text tags dropped, whitespace collapsed,
        /// empty -&gt; <c>(none)</c>.
        /// </summary>
        internal static string SanitizeStockReason(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "(none)";
            var sb = new System.Text.StringBuilder(reason.Length);
            bool inTag = false;
            bool lastSpace = false;
            foreach (char c in reason)
            {
                if (c == '<') { inTag = true; continue; }
                if (inTag) { if (c == '>') inTag = false; continue; }
                if (char.IsWhiteSpace(c))
                {
                    if (!lastSpace && sb.Length > 0) sb.Append(' ');
                    lastSpace = true;
                    continue;
                }
                sb.Append(c);
                lastSpace = false;
            }
            string s = sb.ToString().Trim();
            return s.Length == 0 ? "(none)" : s;
        }

        private static Strategies.Strategy ResolveStrategy(string name)
        {
            var system = Strategies.StrategySystem.Instance;
            if (system == null || system.Strategies == null || string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < system.Strategies.Count; i++)
            {
                var s = system.Strategies[i];
                if (s != null && s.Config != null && s.Config.Name == name)
                    return s;
            }
            return null;
        }

        private static int CountActiveStrategies()
        {
            var system = Strategies.StrategySystem.Instance;
            if (system == null || system.Strategies == null) return 0;
            int n = 0;
            for (int i = 0; i < system.Strategies.Count; i++)
            {
                if (system.Strategies[i] != null && system.Strategies[i].IsActive) n++;
            }
            return n;
        }

        // The slot limit CanBeActivated compares against: the hosted Administration
        // screen's own value when it exists, else the same GameVariables call its Start()
        // makes.
        private static int LiveStrategySlotLimit()
        {
            try
            {
                if (KSP.UI.Screens.Administration.Instance != null)
                    return KSP.UI.Screens.Administration.Instance.MaxActiveStrategies;
                if (GameVariables.Instance != null)
                    return GameVariables.Instance.GetActiveStrategyLimit(
                        ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.Administration));
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag, "kscaction strategy slot limit read threw: " + ex.GetType().Name + ": " + ex.Message);
            }
            return 0;
        }

        private static KscActionExecOutcome ExecuteActivateStrategy(string strategyArg, string factorArg)
        {
            const string action = "activate-strategy";
            bool argPresent = !string.IsNullOrEmpty(strategyArg);
            bool factorOk = TryParseStrategyFactor(factorArg, out float factor);
            Strategies.Strategy strategy = argPresent ? ResolveStrategy(strategyArg) : null;
            int used = CountActiveStrategies();
            int limit = LiveStrategySlotLimit();

            var inputs = new KscActionInputs
            {
                ArgPresent = argPresent,
                TargetResolves = strategy != null,
                AlreadyApplied = strategy != null && strategy.IsActive,
                FactorArgInvalid = !factorOk,
                SlotsUsed = used,
                SlotLimit = limit,
            };

            KscActionDecision d = Decide(action, strategyArg, inputs);
            if (!d.Accepted)
                return Refuse(action, strategyArg, d.RejectReason);

            // The commitment slider's own write (Administration.OnSliderCommitmentValueChanged).
            float factorBefore = strategy.Factor;
            if (!float.IsNaN(factor))
                strategy.Factor = factor;

            string stockReason = null;
            bool canActivate = false;
            try { canActivate = strategy.CanBeActivated(out stockReason); }
            catch (System.Exception ex)
            {
                stockReason = "CanBeActivated threw " + ex.GetType().Name + ": " + ex.Message;
            }
            if (!canActivate)
            {
                strategy.Factor = factorBefore;
                return Refuse(action, strategyArg, "strategy-cannot-activate " + SanitizeStockReason(stockReason));
            }

            double fundsBefore = Funding.Instance != null ? Funding.Instance.Funds : 0.0;
            bool activated = false;
            try { activated = strategy.Activate(); }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag, "kscaction activate-strategy Activate threw: " + ex.GetType().Name + ": " + ex.Message);
            }
            if (!activated || !strategy.IsActive)
            {
                strategy.Factor = factorBefore;
                return Refuse(action, strategyArg, "activate-not-applied");
            }

            double fundsAfter = Funding.Instance != null ? Funding.Instance.Funds : 0.0;
            int usedAfter = CountActiveStrategies();
            string factorText = strategy.Factor.ToString("R", CultureInfo.InvariantCulture);
            string slots = usedAfter.ToString(CultureInfo.InvariantCulture) + "/" + limit.ToString(CultureInfo.InvariantCulture);
            string observed = string.Format(CultureInfo.InvariantCulture,
                "factor={0} slots={1} setupFunds={2} setupSci={3} setupRep={4} fundsDelta={5} dateActivated={6}",
                factorText, slots,
                strategy.InitialCostFunds.ToString("R", CultureInfo.InvariantCulture),
                strategy.InitialCostScience.ToString("R", CultureInfo.InvariantCulture),
                strategy.InitialCostReputation.ToString("R", CultureInfo.InvariantCulture),
                ((float)(fundsAfter - fundsBefore)).ToString("R", CultureInfo.InvariantCulture),
                strategy.DateActivated.ToString("R", CultureInfo.InvariantCulture));
            LogApplied(action, strategyArg, d.ManifestKind, observed);
            var payload = OkPayload(action, strategyArg, "factor", factorText);
            payload.Add(new KeyValuePair<string, string>("slots", slots));
            payload.Add(new KeyValuePair<string, string>("fundsAfter", fundsAfter.ToString("R", CultureInfo.InvariantCulture)));
            return KscActionExecOutcome.Ok(payload);
        }

        private static KscActionExecOutcome ExecuteDeactivateStrategy(string strategyArg)
        {
            const string action = "deactivate-strategy";
            bool argPresent = !string.IsNullOrEmpty(strategyArg);
            Strategies.Strategy strategy = argPresent ? ResolveStrategy(strategyArg) : null;

            var inputs = new KscActionInputs
            {
                ArgPresent = argPresent,
                TargetResolves = strategy != null,
                AlreadyApplied = strategy != null && !strategy.IsActive,
            };

            KscActionDecision d = Decide(action, strategyArg, inputs);
            if (!d.Accepted)
                return Refuse(action, strategyArg, d.RejectReason);

            string stockReason = null;
            bool canDeactivate = false;
            try { canDeactivate = strategy.CanBeDeactivated(out stockReason); }
            catch (System.Exception ex)
            {
                stockReason = "CanBeDeactivated threw " + ex.GetType().Name + ": " + ex.Message;
            }
            if (!canDeactivate)
                return Refuse(action, strategyArg, "strategy-cannot-deactivate " + SanitizeStockReason(stockReason));

            bool deactivated = false;
            try { deactivated = strategy.Deactivate(); }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag, "kscaction deactivate-strategy Deactivate threw: " + ex.GetType().Name + ": " + ex.Message);
            }
            if (!deactivated || strategy.IsActive)
                return Refuse(action, strategyArg, "deactivate-not-applied");

            string slots = CountActiveStrategies().ToString(CultureInfo.InvariantCulture)
                + "/" + LiveStrategySlotLimit().ToString(CultureInfo.InvariantCulture);
            LogApplied(action, strategyArg, d.ManifestKind, "slots=" + slots);
            return KscActionExecOutcome.Ok(OkPayload(action, strategyArg, "slots", slots));
        }

        // ------------------------------------------------------------------
        // The refused-click sub-actions (KB-4 / KB-5): each makes exactly the stock call
        // behind one control Parsek greys for the committed timeline, and CONFIRMS the
        // effect. A call the backstop refused left the target as it was and is REJECTED
        // blocked-committed after a `not applied:` line carrying the target's state and the
        // funds, science and reputation pools on both sides of the call.
        // ------------------------------------------------------------------

        private static double LiveReputation()
            => Reputation.Instance != null ? Reputation.CurrentRep : 0.0;

        /// <summary>
        /// <see cref="FormatNotAppliedLine"/> plus the reputation pool on both sides of the
        /// call (Cancel charges a reputation penalty and a strategy's setup cost can name
        /// reputation), appended as <c>reputationBefore= reputationAfter= reputationDelta=</c>.
        /// </summary>
        internal static string FormatNotAppliedLineWithReputation(
            string action, string target, string stateKey, string stateBefore, string stateAfter,
            double fundsBefore, double fundsAfter, double scienceBefore, double scienceAfter,
            double reputationBefore, double reputationAfter)
        {
            var ic = CultureInfo.InvariantCulture;
            return FormatNotAppliedLine(action, target, stateKey, stateBefore, stateAfter,
                    fundsBefore, fundsAfter, scienceBefore, scienceAfter)
                + " reputationBefore=" + reputationBefore.ToString("R", ic)
                + " reputationAfter=" + reputationAfter.ToString("R", ic)
                + " reputationDelta=" + (reputationAfter - reputationBefore).ToString("R", ic);
        }

        private static KscActionExecOutcome ExecuteDeclineContract(string contractArg)
        {
            return ExecuteContractCall("decline-contract", contractArg,
                Contracts.Contract.State.Offered, c => c.Decline());
        }

        private static KscActionExecOutcome ExecuteCancelContract(string contractArg)
        {
            return ExecuteContractCall("cancel-contract", contractArg,
                Contracts.Contract.State.Active, c => c.Cancel());
        }

        // Decline and Cancel share one shape: the call is stock's own (Mission Control's
        // OnClickDecline / OnClickCancel end in it), the contract must be in the state the
        // button acts on, and the effect is the contract leaving that state.
        // ContractDeclinePatch / ContractCancelPatch refuse it for a contract the committed
        // timeline accepts / resolves later, before stock changes anything.
        private static KscActionExecOutcome ExecuteContractCall(
            string action, string contractArg, Contracts.Contract.State requiredState,
            System.Action<Contracts.Contract> call)
        {
            bool argPresent = !string.IsNullOrEmpty(contractArg);
            Contracts.Contract contract = argPresent ? ResolveContract(contractArg) : null;

            var inputs = new KscActionInputs
            {
                ArgPresent = argPresent,
                TargetResolves = contract != null,
                AlreadyApplied = contract != null && contract.ContractState != requiredState,
            };

            KscActionDecision d = Decide(action, contractArg, inputs);
            if (!d.Accepted)
                return Refuse(action, contractArg, d.RejectReason);

            string stateBefore = contract.ContractState.ToString();
            double fundsBefore = LiveFunds();
            double scienceBefore = LiveScience();
            double repBefore = LiveReputation();
            try { call(contract); }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag, "kscaction " + action + " stock call threw: " + ex.GetType().Name + ": " + ex.Message);
            }

            if (contract.ContractState == requiredState)
            {
                ParsekLog.Info(Tag, FormatNotAppliedLineWithReputation(action, contractArg, "state",
                    stateBefore, contract.ContractState.ToString(),
                    fundsBefore, LiveFunds(), scienceBefore, LiveScience(), repBefore, LiveReputation()));
                return Refuse(action, contractArg, "blocked-committed");
            }

            string stateAfter = contract.ContractState.ToString();
            LogApplied(action, contractArg, d.ManifestKind, "state=" + stateAfter
                + " funds=" + LiveFunds().ToString("R", CultureInfo.InvariantCulture));
            return KscActionExecOutcome.Ok(OkPayload(action, contractArg, "state", stateAfter));
        }

        private static KscActionExecOutcome ExecuteSackKerbal(string kerbal)
        {
            const string action = "sack-kerbal";
            bool argPresent = !string.IsNullOrEmpty(kerbal);
            KerbalRoster roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
            ProtoCrewMember crew = (argPresent && roster != null) ? roster[kerbal] : null;

            // The Astronaut Complex offers its dismiss button on Available crew rows only.
            var inputs = new KscActionInputs
            {
                ArgPresent = argPresent,
                TargetResolves = crew != null,
                IsDismissable = crew != null
                    && crew.type == ProtoCrewMember.KerbalType.Crew
                    && crew.rosterStatus == ProtoCrewMember.RosterStatus.Available,
            };

            KscActionDecision d = Decide(action, kerbal, inputs);
            if (!d.Accepted)
                return Refuse(action, kerbal, d.RejectReason);

            // KerbalSackPatch refuses a kerbal the committed timeline holds or a stand-in
            // covers (KerbalDismissalPatch.ShouldAllowDismissal, the predicate the greyed
            // dismiss button reads) before stock turns him back into an applicant.
            string typeBefore = crew.type.ToString();
            double fundsBefore = LiveFunds();
            double scienceBefore = LiveScience();
            double repBefore = LiveReputation();
            try { roster.SackAvailable(crew); }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag, "kscaction sack-kerbal SackAvailable threw: " + ex.GetType().Name + ": " + ex.Message);
            }

            if (crew.type == ProtoCrewMember.KerbalType.Crew)
            {
                ParsekLog.Info(Tag, FormatNotAppliedLineWithReputation(action, kerbal, "type",
                    typeBefore, crew.type.ToString(),
                    fundsBefore, LiveFunds(), scienceBefore, LiveScience(), repBefore, LiveReputation()));
                return Refuse(action, kerbal, "blocked-committed");
            }

            string typeAfter = crew.type.ToString();
            LogApplied(action, kerbal, d.ManifestKind, "type=" + typeAfter);
            return KscActionExecOutcome.Ok(OkPayload(action, kerbal, "type", typeAfter));
        }

        private static KscActionExecOutcome ExecutePurchasePart(string partArg)
        {
            const string action = "purchase-part";
            bool argPresent = !string.IsNullOrEmpty(partArg);
            AvailablePart ap = null;
            if (argPresent)
            {
                try { ap = PartLoader.getPartInfoByName(partArg); }
                catch (System.Exception) { ap = null; }
            }
            ProtoTechNode proto = ap != null ? ResolveProtoTech(ap.TechRequired) : null;
            bool techResearched = ap != null && !string.IsNullOrEmpty(ap.TechRequired)
                && ResearchAndDevelopment.GetTechnologyState(ap.TechRequired) == RDTech.State.Available;
            bool purchased = ap != null && ResearchAndDevelopment.PartModelPurchased(ap);
            double funds = LiveFunds();

            var inputs = new KscActionInputs
            {
                ArgPresent = argPresent,
                TargetResolves = ap != null && proto != null,
                TechResearched = techResearched,
                AlreadyApplied = purchased,
                CostAmount = ap != null ? ap.entryCost : 0.0,
                AvailableAmount = funds,
                CostIsFunds = true,
            };

            KscActionDecision d = Decide(action, partArg, inputs);
            if (!d.Accepted)
                return Refuse(action, partArg, d.RejectReason);

            // A hosted RDTech built the way the R&D building's own node builds it
            // (RDTech.Start, run by hand because AddComponent defers Start a frame):
            // Warmup fills partsAssigned from the loaded parts, host is the live R&D
            // singleton, and techState / state / partsPurchased come from the host's saved
            // node, so a purchase adds to the REAL purchased list and SetTechState writes the
            // node back whole. RDTechPurchasePartPatch refuses a part the committed timeline
            // buys later before stock adds it (StockUiPartPurchase.TryBlockPurchase).
            string stateBefore = purchased ? "Purchased" : "Unpurchased";
            double scienceBefore = LiveScience();
            double repBefore = LiveReputation();
            ResearchAndDevelopment host = ResearchAndDevelopment.Instance;
            ProtoTechNode node = host != null ? host.GetTechState(ap.TechRequired) : null;
            if (node == null)
                return Refuse(action, partArg, "tech-not-researched");
            UnityEngine.GameObject go = null;
            try
            {
                go = new UnityEngine.GameObject("ParsekSeamRDTechPurchase");
                RDTech tech = go.AddComponent<RDTech>();
                tech.techID = ap.TechRequired;
                tech.scienceCost = proto.scienceCost;
                tech.Warmup();
                tech.host = host;
                if (RDTechTechStateField == null)
                    throw new System.MissingFieldException("RDTech.techState");
                RDTechTechStateField.SetValue(tech, node);
                tech.state = node.state;
                tech.partsPurchased = node.partsPurchased;
                tech.PurchasePart(ap);
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag, "kscaction purchase-part PurchasePart threw: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (go != null) UnityEngine.Object.Destroy(go);
            }

            bool purchasedNow = ResearchAndDevelopment.PartModelPurchased(ap);
            if (!purchasedNow)
            {
                ParsekLog.Info(Tag, FormatNotAppliedLineWithReputation(action, partArg, "state",
                    stateBefore, "Unpurchased",
                    funds, LiveFunds(), scienceBefore, LiveScience(), repBefore, LiveReputation()));
                return Refuse(action, partArg, "blocked-committed");
            }

            string observed = LiveFunds().ToString("R", CultureInfo.InvariantCulture);
            LogApplied(action, partArg, d.ManifestKind, "funds=" + observed);
            return KscActionExecOutcome.Ok(OkPayload(action, partArg, "fundsAfter", observed));
        }

        // RDTech.Start copies the host's saved node into this private field; PurchasePart
        // writes the purchase back through it (techState.UpdateFromTechNode + SetTechState).
        private static readonly FieldInfo RDTechTechStateField =
            typeof(RDTech).GetField("techState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        // Administration.BtnInputAccept(string) is the Accept / Cancel button's own
        // onClickState listener (private); stock's confirmation dialog is the private
        // strategyConfirmationDialog it assigns.
        private static readonly MethodInfo AdministrationBtnInputAcceptMethod =
            typeof(KSP.UI.Screens.Administration).GetMethod("BtnInputAccept",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(string) }, null);

        private static readonly FieldInfo AdministrationConfirmationField =
            typeof(KSP.UI.Screens.Administration).GetField("strategyConfirmationDialog",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        /// <summary>
        /// Pure: what one Administration button press did, from the state on both sides of
        /// the call. The strategy's state moving is an applied press; stock's own
        /// confirmation opening means the press went through to stock (Parsek did not
        /// refuse it; the player's next click would confirm); neither means the backstop
        /// refused it before stock's handler ran.
        /// </summary>
        internal static string ClassifyStrategyPress(bool activeBefore, bool activeAfter, bool confirmationOpened)
        {
            if (activeBefore != activeAfter) return "applied";
            if (confirmationOpened) return "confirmation";
            return "refused";
        }

        private static KscActionExecOutcome ExecutePressStrategyButton(string action, string strategyArg, bool accept)
        {
            bool argPresent = !string.IsNullOrEmpty(strategyArg);
            Strategies.Strategy strategy = argPresent ? ResolveStrategy(strategyArg) : null;

            var inputs = new KscActionInputs
            {
                ArgPresent = argPresent,
                TargetResolves = strategy != null,
                AlreadyApplied = strategy != null && (accept ? strategy.IsActive : !strategy.IsActive),
            };

            KscActionDecision d = Decide(action, strategyArg, inputs);
            if (!d.Accepted)
                return Refuse(action, strategyArg, d.RejectReason);

            // The press acts on the building's selection, as the player's does: the spec
            // opens Administration and selects the strategy (StockScreen) first.
            var admin = KSP.UI.Screens.Administration.Instance;
            if (admin == null || AdministrationBtnInputAcceptMethod == null)
                return Refuse(action, strategyArg, "administration-not-open");
            var selected = admin.SelectedWrapper != null ? admin.SelectedWrapper.strategy : null;
            if (selected == null || selected.Config == null || selected.Config.Name != strategy.Config.Name)
                return Refuse(action, strategyArg, "strategy-not-selected");

            // Stock's Cancel handler returns silently when its own CanBeDeactivated refuses;
            // ask it first so that refusal is stock's, typed, and never read as Parsek's.
            if (!accept)
            {
                string stockReason = null;
                bool canDeactivate = false;
                try { canDeactivate = strategy.CanBeDeactivated(out stockReason); }
                catch (System.Exception ex)
                {
                    stockReason = "CanBeDeactivated threw " + ex.GetType().Name + ": " + ex.Message;
                }
                if (!canDeactivate)
                    return Refuse(action, strategyArg, "strategy-cannot-deactivate " + SanitizeStockReason(stockReason));
            }

            bool activeBefore = strategy.IsActive;
            string stateBefore = activeBefore ? "Active" : "Inactive";
            double fundsBefore = LiveFunds();
            double scienceBefore = LiveScience();
            double repBefore = LiveReputation();
            PopupDialog confirmationBefore = ReadConfirmation(admin);
            try
            {
                AdministrationBtnInputAcceptMethod.Invoke(admin, new object[] { accept ? "accept" : "cancel" });
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag, "kscaction " + action + " BtnInputAccept threw: " + ex.GetType().Name + ": " + ex.Message);
            }
            PopupDialog confirmationAfter = ReadConfirmation(admin);
            bool confirmationOpened = confirmationAfter != null && confirmationAfter != confirmationBefore;

            string outcome = ClassifyStrategyPress(activeBefore, strategy.IsActive, confirmationOpened);
            if (outcome == "refused")
            {
                ParsekLog.Info(Tag, FormatNotAppliedLineWithReputation(action, strategyArg, "state",
                    stateBefore, strategy.IsActive ? "Active" : "Inactive",
                    fundsBefore, LiveFunds(), scienceBefore, LiveScience(), repBefore, LiveReputation()));
                return Refuse(action, strategyArg, "blocked-committed");
            }

            if (outcome == "confirmation")
            {
                // Not refused: stock's confirmation is up. Take it down through its own
                // dismiss (OnPopupDismiss changes nothing), so a lane never runs on behind it.
                ParsekLog.Info(Tag, "kscaction " + action + " reached stock confirmation target="
                    + strategyArg + " - confirmation dismissed, strategy unchanged");
                try { confirmationAfter.Dismiss(); }
                catch (System.Exception ex)
                {
                    ParsekLog.Warn(Tag, "kscaction " + action + " confirmation dismiss threw: " + ex.GetType().Name + ": " + ex.Message);
                }
                var p = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("action", action),
                    new KeyValuePair<string, string>("target", strategyArg),
                    new KeyValuePair<string, string>("applied", "false"),
                    new KeyValuePair<string, string>("confirmation", "dismissed"),
                };
                return KscActionExecOutcome.Ok(p);
            }

            string stateAfter = strategy.IsActive ? "Active" : "Inactive";
            LogApplied(action, strategyArg, d.ManifestKind, "state=" + stateAfter);
            return KscActionExecOutcome.Ok(OkPayload(action, strategyArg, "state", stateAfter));
        }

        private static PopupDialog ReadConfirmation(KSP.UI.Screens.Administration admin)
        {
            if (admin == null || AdministrationConfirmationField == null) return null;
            try
            {
                var popup = AdministrationConfirmationField.GetValue(admin) as PopupDialog;
                // A dismissed popup's GameObject is destroyed; Unity's null check sees it.
                return popup != null ? popup : null;
            }
            catch (System.Exception) { return null; }
        }

        // The crew dialog's two seat entry points are protected virtuals; invoking them by
        // reflection dispatches to CrewAssignmentDialog's overrides, which call the base
        // method CrewDialogMoveToSeatPatch / CrewDialogDropOnCrewListPatch prefix.
        private static readonly MethodInfo CrewListItemButtonClickMethod =
            typeof(KSP.UI.BaseCrewAssignmentDialog).GetMethod("ListItemButtonClick",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(KSP.UI.CrewListItem.ButtonTypes), typeof(KSP.UI.CrewListItem) }, null);

        private static readonly MethodInfo CrewDropOnCrewListMethod =
            typeof(KSP.UI.BaseCrewAssignmentDialog).GetMethod("DropOnCrewList",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(KSP.UI.UIList), typeof(KSP.UI.UIListItem), typeof(int) }, null);

        /// <summary>
        /// Pure: which seat path a press takes over the crew list's rows (<paramref name="isSeat"/>
        /// true = a seat row, <paramref name="empty"/> true = that seat is empty). The row's
        /// assign button seats into the FIRST EMPTY seat (<c>ListItemButtonClick(V)</c> ends
        /// in <c>MoveCrewToEmptySeat</c>), so with one the path is <c>click</c>; with every
        /// seat taken the button does nothing and the only way in is a drag onto a seat
        /// (<c>DropOnCrewList</c>), onto the FIRST seat. No seat at all is <c>none</c>.
        /// </summary>
        internal static string ChooseSeatPath(IList<bool> isSeat, IList<bool> empty, out int seatIndex)
        {
            seatIndex = -1;
            if (isSeat == null || empty == null) return "none";
            int first = -1;
            for (int i = 0; i < isSeat.Count && i < empty.Count; i++)
            {
                if (!isSeat[i]) continue;
                if (first < 0) first = i;
                if (empty[i]) { seatIndex = i; return "click"; }
            }
            if (first < 0) return "none";
            seatIndex = first;
            return "drag";
        }

        private static KscActionExecOutcome ExecuteSeatCrew(string kerbal)
        {
            const string action = "seat-crew";
            bool argPresent = !string.IsNullOrEmpty(kerbal);
            KerbalRoster roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
            bool exists = argPresent && roster != null && roster[kerbal] != null;

            KSP.UI.CrewAssignmentDialog dialog = KSP.UI.CrewAssignmentDialog.Instance;
            bool open = dialog != null && dialog.isActiveAndEnabled
                && dialog.scrollListAvail != null && dialog.scrollListCrew != null;
            KSP.UI.UIListItem availRow = open && argPresent ? FindCrewRow(dialog.scrollListAvail, kerbal) : null;
            KSP.UI.UIListItem seatedRow = open && argPresent ? FindCrewRow(dialog.scrollListCrew, kerbal) : null;

            var inputs = new KscActionInputs
            {
                ArgPresent = argPresent,
                TargetResolves = exists,
                AlreadyApplied = seatedRow != null,
            };

            KscActionDecision d = Decide(action, kerbal, inputs);
            if (!d.Accepted)
                return Refuse(action, kerbal, d.RejectReason);
            if (!open || CrewListItemButtonClickMethod == null || CrewDropOnCrewListMethod == null)
                return Refuse(action, kerbal, "crew-dialog-not-open");
            if (availRow == null)
                return Refuse(action, kerbal, "kerbal-not-listed");

            var isSeat = new List<bool>();
            var empty = new List<bool>();
            for (int i = 0; i < 64; i++)
            {
                KSP.UI.UIListItem item;
                try { item = dialog.scrollListCrew.GetUilistItemAt(i); }
                catch (System.Exception) { break; }
                if (item == null) break;
                KSP.UI.CrewListItem row = item.GetComponent<KSP.UI.CrewListItem>();
                isSeat.Add(row != null);
                empty.Add(row != null && row.isEmpty);
            }
            string path = ChooseSeatPath(isSeat, empty, out int seatIndex);
            if (path == "none")
                return Refuse(action, kerbal, "no-seat");

            KSP.UI.CrewListItem availItem = availRow.GetComponent<KSP.UI.CrewListItem>();
            double fundsBefore = LiveFunds();
            double scienceBefore = LiveScience();
            double repBefore = LiveReputation();
            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "kscaction seat-crew pressing target={0} path={1} seatIndex={2}",
                OneField(kerbal), path, seatIndex));
            try
            {
                if (path == "click")
                    CrewListItemButtonClickMethod.Invoke(dialog,
                        new object[] { KSP.UI.CrewListItem.ButtonTypes.V, availItem });
                else
                    CrewDropOnCrewListMethod.Invoke(dialog,
                        new object[] { dialog.scrollListAvail, availRow, seatIndex });
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag, "kscaction seat-crew " + path + " threw: " + ex.GetType().Name + ": " + ex.Message);
            }

            bool seatedNow = FindCrewRow(dialog.scrollListCrew, kerbal) != null;
            if (!seatedNow)
            {
                bool stillListed = FindCrewRow(dialog.scrollListAvail, kerbal) != null;
                ParsekLog.Info(Tag, FormatNotAppliedLineWithReputation(action, kerbal, "seat",
                    "unseated", stillListed ? "unseated" : "missing",
                    fundsBefore, LiveFunds(), scienceBefore, LiveScience(), repBefore, LiveReputation()));
                return Refuse(action, kerbal, "blocked-committed");
            }

            LogApplied(action, kerbal, d.ManifestKind, "path=" + path);
            return KscActionExecOutcome.Ok(OkPayload(action, kerbal, "path", path));
        }

        private static KSP.UI.UIListItem FindCrewRow(KSP.UI.UIList list, string kerbal)
        {
            if (list == null || string.IsNullOrEmpty(kerbal)) return null;
            for (int i = 0; i < 64; i++)
            {
                KSP.UI.UIListItem item;
                try { item = list.GetUilistItemAt(i); }
                catch (System.Exception) { break; }
                if (item == null) break;
                if (StockUiCrewDialogDecoration.KerbalNameOf(item) == kerbal) return item;
            }
            return null;
        }
    }
}
