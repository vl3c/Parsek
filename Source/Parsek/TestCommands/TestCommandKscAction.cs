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
        /// <c>Contract.Cancel()</c> (the Cancel button's <c>OnClickCancel</c> ends in it).</summary>
        CancelContract,

        /// <summary>Dismiss an Available crew member through the Astronaut Complex's own
        /// call, <c>KerbalRoster.SackAvailable</c> (the dismiss button ends in it). Unlike
        /// <see cref="DismissKerbal"/> it asks no Parsek predicate first.</summary>
        SackKerbal,

        /// <summary>Press the Administration screen's Accept / Cancel button for the
        /// strategy the OPEN screen has selected (<c>Administration.BtnInputAccept</c>, with
        /// the state the button shows: accept when the strategy is inactive, else cancel).</summary>
        PressStrategyButton,

        /// <summary>Buy one part through the purchase primitive both R&amp;D purchase paths end
        /// in, <c>RDTech.PurchasePart</c>, on the OPEN R&amp;D screen's own node for the part's
        /// tech.</summary>
        PurchasePart,

        /// <summary>Press the OPEN R&amp;D screen's purchase-all button for one researched node
        /// (<c>KSP.UI.Screens.RDController.ActionButtonClick("purchase")</c> with the node selected).</summary>
        PurchaseAllParts,

        /// <summary>Click an available kerbal's assign button in the OPEN VAB/SPH crew dialog
        /// (<c>BaseCrewAssignmentDialog.ListItemButtonClick(V, row)</c>), after stock's Clear
        /// button empties the seats when none is free.</summary>
        AssignCrew,

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

        /// <summary>press-strategy / purchase-part / purchase-all / assign-crew: the stock
        /// screen the sub-action presses a control on is open.</summary>
        public bool ScreenOpen;

        /// <summary>press-strategy: the open Administration screen has the named strategy
        /// selected.</summary>
        public bool TargetSelected;

        /// <summary>purchase-part: the part's tech is researched; purchase-all: the node is
        /// researched (stock shows the purchase button on a researched node only).</summary>
        public bool PrerequisiteMet;
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
                case "press-strategy": return KscActionKind.PressStrategyButton;
                case "purchase-part": return KscActionKind.PurchasePart;
                case "purchase-all": return KscActionKind.PurchaseAllParts;
                case "assign-crew": return KscActionKind.AssignCrew;
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
                case KscActionKind.PressStrategyButton: return "strategy-button";
                case KscActionKind.PurchasePart:
                case KscActionKind.PurchaseAllParts: return "part-purchase";
                case KscActionKind.AssignCrew: return "crew-assign";
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
                    // No Parsek predicate here: the point is to reach stock's own call and
                    // let the dismissal backstop answer.
                    if (!inputs.IsDismissable) { d.RejectReason = "kerbal-not-dismissable"; return d; }
                    break;

                case KscActionKind.PressStrategyButton:
                    if (!inputs.ScreenOpen) { d.RejectReason = "administration-not-open"; return d; }
                    if (!inputs.TargetSelected) { d.RejectReason = "strategy-not-selected"; return d; }
                    break;

                case KscActionKind.PurchasePart:
                    if (!inputs.ScreenOpen) { d.RejectReason = "rnd-not-open"; return d; }
                    if (!inputs.PrerequisiteMet) { d.RejectReason = "part-tech-not-researched"; return d; }
                    if (inputs.AlreadyApplied) { d.RejectReason = "part-already-purchased"; return d; }
                    break;

                case KscActionKind.PurchaseAllParts:
                    if (!inputs.ScreenOpen) { d.RejectReason = "rnd-not-open"; return d; }
                    if (!inputs.PrerequisiteMet) { d.RejectReason = "node-not-researched"; return d; }
                    if (inputs.AlreadyApplied) { d.RejectReason = "nothing-to-purchase"; return d; }
                    break;

                case KscActionKind.AssignCrew:
                    if (!inputs.ScreenOpen) { d.RejectReason = "crew-dialog-not-open"; return d; }
                    if (inputs.AlreadyApplied) { d.RejectReason = "kerbal-already-assigned"; return d; }
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
                case KscActionKind.DismissKerbal: return "unknown-kerbal";
                case KscActionKind.ActivateStrategy:
                case KscActionKind.DeactivateStrategy: return "unknown-strategy";
                case KscActionKind.AcceptContract:
                case KscActionKind.DeclineContract:
                case KscActionKind.CancelContract: return "unknown-contract";
                case KscActionKind.SackKerbal:
                case KscActionKind.AssignCrew: return "unknown-kerbal";
                case KscActionKind.PressStrategyButton: return "unknown-strategy";
                case KscActionKind.PurchasePart: return "unknown-part";
                case KscActionKind.PurchaseAllParts: return "unknown-tech-node";
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
                case KscActionKind.DeclineContract: return ExecuteDeclineOrCancelContract(contract, cancel: false);
                case KscActionKind.CancelContract: return ExecuteDeclineOrCancelContract(contract, cancel: true);
                case KscActionKind.SackKerbal: return ExecuteSackKerbal(kerbal);
                case KscActionKind.PressStrategyButton: return ExecutePressStrategyButton(strategy);
                case KscActionKind.PurchasePart: return ExecutePurchasePart(part);
                case KscActionKind.PurchaseAllParts: return ExecutePurchaseAll(node);
                case KscActionKind.AssignCrew: return ExecuteAssignCrew(kerbal);
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
        // Stock-UI click-block proofs (KB-4). Each sub-action below makes the call a stock
        // control makes (Mission Control's Decline / Cancel, the Astronaut Complex's dismiss,
        // the Administration Accept / Cancel button, R&D's part purchase and purchase-all, the
        // crew dialog's assign button) and reads the target's own state plus the funds,
        // science and reputation pools on both sides, so a refused click reads as "nothing
        // changed" (the not-applied line). None asks a Parsek predicate before stock's call:
        // the Parsek backstop on that call is what the lane is proving.
        // ------------------------------------------------------------------

        private static double LiveReputation()
            => Reputation.Instance != null ? Reputation.Instance.reputation : 0.0;

        /// <summary>The not-applied line with the reputation pool appended (the contract and
        /// strategy calls move reputation when stock lets them through).</summary>
        internal static string AppendRepFields(string line, double repBefore, double repAfter)
        {
            var ic = CultureInfo.InvariantCulture;
            return line
                + " repBefore=" + repBefore.ToString("R", ic)
                + " repAfter=" + repAfter.ToString("R", ic)
                + " repDelta=" + (repAfter - repBefore).ToString("R", ic);
        }

        private static KscActionExecOutcome ExecuteDeclineOrCancelContract(string contractArg, bool cancel)
        {
            string action = cancel ? "cancel-contract" : "decline-contract";
            Contracts.Contract.State required = cancel
                ? Contracts.Contract.State.Active
                : Contracts.Contract.State.Offered;
            bool argPresent = !string.IsNullOrEmpty(contractArg);
            Contracts.Contract contract = argPresent ? ResolveContract(contractArg) : null;

            var inputs = new KscActionInputs
            {
                ArgPresent = argPresent,
                TargetResolves = contract != null,
                AlreadyApplied = contract != null && contract.ContractState != required,
            };
            KscActionDecision d = Decide(action, contractArg, inputs);
            if (!d.Accepted)
                return Refuse(action, contractArg, d.RejectReason);

            // Mission Control's Decline / Cancel buttons end in these calls;
            // ContractDeclinePatch / ContractCancelPatch refuse them for a contract the
            // committed timeline accepts / resolves later.
            string stateBefore = contract.ContractState.ToString();
            double fundsBefore = LiveFunds();
            double scienceBefore = LiveScience();
            double repBefore = LiveReputation();
            try
            {
                if (cancel) contract.Cancel();
                else contract.Decline();
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag, "kscaction " + action + " threw: " + ex.GetType().Name + ": " + ex.Message);
            }

            if (contract.ContractState == required)
            {
                ParsekLog.Info(Tag, AppendRepFields(FormatNotAppliedLine(action, contractArg, "state",
                    stateBefore, contract.ContractState.ToString(),
                    fundsBefore, LiveFunds(), scienceBefore, LiveScience()), repBefore, LiveReputation()));
                return Refuse(action, contractArg, "blocked-committed");
            }

            string observed = contract.ContractState.ToString();
            LogApplied(action, contractArg, d.ManifestKind, "state=" + observed);
            return KscActionExecOutcome.Ok(OkPayload(action, contractArg, "state", observed));
        }

        private static KscActionExecOutcome ExecuteSackKerbal(string kerbal)
        {
            const string action = "sack-kerbal";
            bool argPresent = !string.IsNullOrEmpty(kerbal);
            KerbalRoster roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
            ProtoCrewMember crew = (argPresent && roster != null) ? roster[kerbal] : null;
            // The Astronaut Complex lists only Available crew with a dismiss button.
            bool dismissable = crew != null
                && crew.rosterStatus == ProtoCrewMember.RosterStatus.Available
                && crew.type == ProtoCrewMember.KerbalType.Crew;

            var inputs = new KscActionInputs
            {
                ArgPresent = argPresent,
                TargetResolves = crew != null,
                IsDismissable = dismissable,
            };
            KscActionDecision d = Decide(action, kerbal, inputs);
            if (!d.Accepted)
                return Refuse(action, kerbal, d.RejectReason);

            string typeBefore = crew.type.ToString();
            double fundsBefore = LiveFunds();
            double scienceBefore = LiveScience();
            try { roster.SackAvailable(crew); }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag, "kscaction sack-kerbal SackAvailable threw: " + ex.GetType().Name + ": " + ex.Message);
            }

            // Confirm: KerbalSackPatch refuses SackAvailable for a kerbal the committed
            // timeline holds; a sacked kerbal leaves the crew (stock moves him out of the
            // roster's crew list).
            bool stillCrew = roster.Exists(kerbal) && roster[kerbal] != null
                && roster[kerbal].type == ProtoCrewMember.KerbalType.Crew;
            if (stillCrew)
            {
                ParsekLog.Info(Tag, FormatNotAppliedLine(action, kerbal, "type",
                    typeBefore, roster[kerbal].type.ToString(),
                    fundsBefore, LiveFunds(), scienceBefore, LiveScience()));
                return Refuse(action, kerbal, "blocked-committed");
            }

            int crewCount = SafeActiveCrewCount(roster);
            string observed = crewCount.ToString(CultureInfo.InvariantCulture);
            LogApplied(action, kerbal, d.ManifestKind, "crewCount=" + observed);
            return KscActionExecOutcome.Ok(OkPayload(action, kerbal, "crewCount", observed));
        }

        private static KscActionExecOutcome ExecutePressStrategyButton(string strategyArg)
        {
            const string action = "press-strategy";
            bool argPresent = !string.IsNullOrEmpty(strategyArg);
            Strategies.Strategy strategy = argPresent ? ResolveStrategy(strategyArg) : null;
            var admin = KSP.UI.Screens.Administration.Instance;
            var selected = admin != null && admin.SelectedWrapper != null ? admin.SelectedWrapper.strategy : null;

            var inputs = new KscActionInputs
            {
                ArgPresent = argPresent,
                TargetResolves = strategy != null,
                ScreenOpen = admin != null,
                TargetSelected = selected != null && selected.Config != null && selected.Config.Name == strategyArg,
            };
            KscActionDecision d = Decide(action, strategyArg, inputs);
            if (!d.Accepted)
                return Refuse(action, strategyArg, d.RejectReason);

            // The button's own state: Accept on an inactive strategy, Cancel on an active one
            // (Administration.SetSelectedStrategy). AdministrationButtonBackstopPatch refuses
            // BtnInputAccept for either when the committed timeline relies on the strategy.
            string state = strategy.IsActive ? "cancel" : "accept";
            MethodInfo press = typeof(KSP.UI.Screens.Administration).GetMethod("BtnInputAccept",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
                new[] { typeof(string) }, null);
            if (press == null)
                return Refuse(action, strategyArg, "button-not-found");

            bool activeBefore = strategy.IsActive;
            double fundsBefore = LiveFunds();
            double scienceBefore = LiveScience();
            double repBefore = LiveReputation();
            ParsekLog.Info(Tag, "kscaction press-strategy pressing state=" + state + " strategy=" + strategyArg);
            try { press.Invoke(admin, new object[] { state }); }
            catch (System.Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                ParsekLog.Warn(Tag, "kscaction press-strategy BtnInputAccept threw: " + inner.GetType().Name + ": " + inner.Message);
            }

            // Stock's own allowed path opens a confirmation dialog and changes nothing until
            // it is answered, so an unchanged strategy is not proof of a block by itself: the
            // lane pins the backstop's Blocking line.
            if (strategy.IsActive == activeBefore)
            {
                ParsekLog.Info(Tag, AppendRepFields(FormatNotAppliedLine(action, strategyArg, "active",
                    activeBefore ? "True" : "False", strategy.IsActive ? "True" : "False",
                    fundsBefore, LiveFunds(), scienceBefore, LiveScience()), repBefore, LiveReputation()));
                return Refuse(action, strategyArg, "button-no-effect");
            }

            string observed = strategy.IsActive ? "True" : "False";
            LogApplied(action, strategyArg, d.ManifestKind, "active=" + observed);
            return KscActionExecOutcome.Ok(OkPayload(action, strategyArg, "active", observed));
        }

        private static KSP.UI.Screens.RDNode FindRdNode(KSP.UI.Screens.RDController controller, string techId)
        {
            if (controller == null || controller.nodes == null || string.IsNullOrEmpty(techId)) return null;
            for (int i = 0; i < controller.nodes.Count; i++)
            {
                KSP.UI.Screens.RDNode n = controller.nodes[i];
                if (n != null && n.tech != null && n.tech.techID == techId)
                    return n;
            }
            return null;
        }

        private static KscActionExecOutcome ExecutePurchasePart(string partArg)
        {
            const string action = "purchase-part";
            bool argPresent = !string.IsNullOrEmpty(partArg);
            AvailablePart ap = argPresent ? PartLoader.getPartInfoByName(partArg) : null;
            KSP.UI.Screens.RDController controller = KSP.UI.Screens.RDController.Instance;
            KSP.UI.Screens.RDNode node = ap != null ? FindRdNode(controller, ap.TechRequired) : null;
            bool researched = ap != null
                && ResearchAndDevelopment.GetTechnologyState(ap.TechRequired) == RDTech.State.Available;

            var inputs = new KscActionInputs
            {
                ArgPresent = argPresent,
                TargetResolves = ap != null,
                ScreenOpen = controller != null && node != null,
                PrerequisiteMet = researched,
                AlreadyApplied = ap != null && ResearchAndDevelopment.PartModelPurchased(ap),
            };
            KscActionDecision d = Decide(action, partArg, inputs);
            if (!d.Accepted)
                return Refuse(action, partArg, d.RejectReason);

            // RDTech.PurchasePart on the R&D screen's own node: the R&D part tooltip's
            // purchase and purchase-all both end here. RDTechPurchasePartPatch refuses it for
            // a part the committed timeline buys later.
            double fundsBefore = LiveFunds();
            double scienceBefore = LiveScience();
            try { node.tech.PurchasePart(ap); }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag, "kscaction purchase-part PurchasePart threw: " + ex.GetType().Name + ": " + ex.Message);
            }

            bool purchasedNow = ResearchAndDevelopment.PartModelPurchased(ap);
            if (!purchasedNow)
            {
                ParsekLog.Info(Tag, FormatNotAppliedLine(action, partArg, "purchased",
                    "False", "False", fundsBefore, LiveFunds(), scienceBefore, LiveScience()));
                return Refuse(action, partArg, "blocked-committed");
            }

            string observed = LiveFunds().ToString("R", CultureInfo.InvariantCulture);
            LogApplied(action, partArg, d.ManifestKind, "funds=" + observed);
            return KscActionExecOutcome.Ok(OkPayload(action, partArg, "fundsAfter", observed));
        }

        private static int CountPurchased(RDTech tech)
        {
            if (tech == null || tech.partsAssigned == null) return 0;
            int n = 0;
            for (int i = 0; i < tech.partsAssigned.Count; i++)
                if (ResearchAndDevelopment.PartModelPurchased(tech.partsAssigned[i])) n++;
            return n;
        }

        private static KscActionExecOutcome ExecutePurchaseAll(string techId)
        {
            const string action = "purchase-all";
            bool argPresent = !string.IsNullOrEmpty(techId);
            KSP.UI.Screens.RDController controller = KSP.UI.Screens.RDController.Instance;
            KSP.UI.Screens.RDNode node = argPresent ? FindRdNode(controller, techId) : null;
            bool researched = argPresent
                && ResearchAndDevelopment.GetTechnologyState(techId) == RDTech.State.Available;
            int assigned = node != null && node.tech != null && node.tech.partsAssigned != null
                ? node.tech.partsAssigned.Count : 0;
            int purchasedBefore = node != null ? CountPurchased(node.tech) : 0;

            var inputs = new KscActionInputs
            {
                ArgPresent = argPresent,
                TargetResolves = argPresent && ResolveProtoTech(techId) != null,
                ScreenOpen = controller != null && node != null,
                PrerequisiteMet = researched,
                AlreadyApplied = node != null && purchasedBefore >= assigned,
            };
            KscActionDecision d = Decide(action, techId, inputs);
            if (!d.Accepted)
                return Refuse(action, techId, d.RejectReason);

            MethodInfo click = typeof(KSP.UI.Screens.RDController).GetMethod("ActionButtonClick",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
                new[] { typeof(string) }, null);
            if (click == null)
                return Refuse(action, techId, "button-not-found");

            // Select the node the way a click on it does, then press the side panel's
            // purchase-all (the researched-node state of the action button).
            // RnDPurchaseAllPatch skips each part the committed timeline buys later and names
            // them in one dialog; RDTechPurchasePartPatch refuses each one again.
            double fundsBefore = LiveFunds();
            double scienceBefore = LiveScience();
            try
            {
                controller.node_selected = node;
                controller.ShowNodePanel(node);
                click.Invoke(controller, new object[] { "purchase" });
            }
            catch (System.Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                ParsekLog.Warn(Tag, "kscaction purchase-all ActionButtonClick threw: " + inner.GetType().Name + ": " + inner.Message);
            }

            int purchasedAfter = CountPurchased(node.tech);
            if (purchasedAfter == purchasedBefore)
            {
                ParsekLog.Info(Tag, FormatNotAppliedLine(action, techId, "purchased",
                    purchasedBefore.ToString(CultureInfo.InvariantCulture) + "/" + assigned.ToString(CultureInfo.InvariantCulture),
                    purchasedAfter.ToString(CultureInfo.InvariantCulture) + "/" + assigned.ToString(CultureInfo.InvariantCulture),
                    fundsBefore, LiveFunds(), scienceBefore, LiveScience()));
                return Refuse(action, techId, "blocked-committed");
            }

            string observed = purchasedAfter.ToString(CultureInfo.InvariantCulture) + "/" + assigned.ToString(CultureInfo.InvariantCulture);
            LogApplied(action, techId, d.ManifestKind, "purchased=" + observed);
            return KscActionExecOutcome.Ok(OkPayload(action, techId, "purchased", observed));
        }

        private static KSP.UI.UIListItem FindCrewDialogRow(KSP.UI.UIList list, string kerbal)
        {
            if (list == null) return null;
            for (int i = 0; i < 256; i++)
            {
                KSP.UI.UIListItem item = list.GetUilistItemAt(i);
                if (item == null) break;
                if (StockUiCrewDialogDecoration.KerbalNameOf(item) == kerbal) return item;
            }
            return null;
        }

        private static bool CrewDialogHasEmptySeat(KSP.UI.UIList crewList)
        {
            if (crewList == null) return false;
            for (int i = 0; i < 256; i++)
            {
                KSP.UI.UIListItem item = crewList.GetUilistItemAt(i);
                if (item == null) break;
                var row = item.GetComponent<KSP.UI.CrewListItem>();
                if (row != null && row.isEmpty) return true;
            }
            return false;
        }

        private static bool ManifestHolds(KSP.UI.CrewAssignmentDialog dialog, string kerbal)
        {
            VesselCrewManifest manifest = dialog != null ? dialog.GetManifest(false) : null;
            if (manifest == null) return false;
            foreach (ProtoCrewMember pcm in manifest.GetAllCrew(false))
                if (pcm != null && pcm.name == kerbal) return true;
            return false;
        }

        private static KscActionExecOutcome ExecuteAssignCrew(string kerbal)
        {
            const string action = "assign-crew";
            bool argPresent = !string.IsNullOrEmpty(kerbal);
            var dialog = KSP.UI.CrewAssignmentDialog.Instance;
            KerbalRoster roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
            bool exists = argPresent && roster != null && roster.Exists(kerbal);
            bool assigned = argPresent && ManifestHolds(dialog, kerbal);
            KSP.UI.UIListItem row = argPresent && dialog != null ? FindCrewDialogRow(dialog.scrollListAvail, kerbal) : null;

            var inputs = new KscActionInputs
            {
                ArgPresent = argPresent,
                TargetResolves = exists,
                ScreenOpen = dialog != null,
                AlreadyApplied = assigned || (dialog != null && exists && row == null),
            };
            KscActionDecision d = Decide(action, kerbal, inputs);
            if (!d.Accepted)
                return Refuse(action, kerbal, d.RejectReason);

            MethodInfo click = typeof(KSP.UI.BaseCrewAssignmentDialog).GetMethod("ListItemButtonClick",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
                new[] { typeof(KSP.UI.CrewListItem.ButtonTypes), typeof(KSP.UI.CrewListItem) }, null);
            if (click == null)
                return Refuse(action, kerbal, "button-not-found");

            // Stock's assign button seats the kerbal in the first EMPTY seat and does nothing
            // when every seat is taken, so the seats are emptied first with the dialog's own
            // Clear button (an editor manifest change, no career state).
            if (!CrewDialogHasEmptySeat(dialog.scrollListCrew))
            {
                ParsekLog.Info(Tag, "kscaction assign-crew no empty seat - pressing the crew dialog's Clear first");
                try { dialog.ButtonClear(); }
                catch (System.Exception ex)
                {
                    ParsekLog.Warn(Tag, "kscaction assign-crew ButtonClear threw: " + ex.GetType().Name + ": " + ex.Message);
                }
                row = FindCrewDialogRow(dialog.scrollListAvail, kerbal);
                if (row == null)
                    return Refuse(action, kerbal, "kerbal-already-assigned");
            }
            bool emptySeat = CrewDialogHasEmptySeat(dialog.scrollListCrew);

            string statusBefore = roster[kerbal].rosterStatus.ToString();
            double fundsBefore = LiveFunds();
            double scienceBefore = LiveScience();
            try
            {
                click.Invoke(dialog, new object[] { KSP.UI.CrewListItem.ButtonTypes.V, row.GetComponent<KSP.UI.CrewListItem>() });
            }
            catch (System.Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                ParsekLog.Warn(Tag, "kscaction assign-crew ListItemButtonClick threw: " + inner.GetType().Name + ": " + inner.Message);
            }

            // Confirm: CrewDialogMoveToSeatPatch refuses the seat for a kerbal the committed
            // timeline reserves; an allowed click puts him in the dialog's manifest.
            if (!ManifestHolds(dialog, kerbal))
            {
                ParsekLog.Info(Tag, FormatNotAppliedLine(action, kerbal, "seated",
                    "False", "False", fundsBefore, LiveFunds(), scienceBefore, LiveScience())
                    + " emptySeat=" + (emptySeat ? "True" : "False")
                    + " rosterBefore=" + statusBefore + " rosterAfter=" + roster[kerbal].rosterStatus);
                return Refuse(action, kerbal, emptySeat ? "blocked-committed" : "no-empty-seat");
            }

            LogApplied(action, kerbal, d.ManifestKind, "seated=True");
            return KscActionExecOutcome.Ok(OkPayload(action, kerbal, "seated", "True"));
        }
    }
}
