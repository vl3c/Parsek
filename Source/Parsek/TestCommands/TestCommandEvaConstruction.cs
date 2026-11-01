using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;

namespace Parsek.TestCommands
{
    /// <summary>What the attach check made of one posed frame.</summary>
    internal enum ConstructionCheckVerdict
    {
        /// <summary>Stock's own <c>CheckAttach</c> found the requested node pair in range,
        /// with nothing but the target and the held part inside the held part's colliders.</summary>
        Ready,

        /// <summary>Stock holds no part (the slot click has not landed, or it was dropped).</summary>
        NoHeldPart,

        /// <summary><c>CheckAttach</c> found no attachment at all.</summary>
        NotPossible,

        /// <summary>An attachment, but onto a part other than the target.</summary>
        WrongParent,

        /// <summary>The target part, but another node pair than the requested one.</summary>
        WrongNode,

        /// <summary>The attach position is beyond <c>GameSettings.EVA_CONSTRUCTION_RANGE</c>
        /// from the kerbal (stock's own second test).</summary>
        OutOfRange,

        /// <summary>The held part's trigger colliders touch something other than the target
        /// or itself (stock's own <c>cargoColliders</c> veto).</summary>
        Blocked,
    }

    /// <summary>Per-poll decision while a construction press is pending.</summary>
    internal enum ConstructionPressDecision
    {
        Wait,
        Press,
        GiveUp,
    }

    /// <summary>The pure detach-target verdict.</summary>
    internal enum DetachTargetDecision
    {
        Found,

        /// <summary>No loaded non-EVA vessel carries a part of that name.</summary>
        NotFound,

        /// <summary>Parts of that name exist, but none is one stock lets a kerbal take off
        /// (root part, has children, no <c>ModuleCargoPart</c>, or crewed).</summary>
        NotDetachable,

        /// <summary>A detachable one exists, but none within construction range.</summary>
        OutOfRange,
    }

    /// <summary>One part a detach could take off: a part of the named kind on a loaded
    /// non-EVA vessel, with the facts stock's <c>DetachInput</c> / <c>CanPartBeEdited</c>
    /// test.</summary>
    internal struct DetachCandidate
    {
        public int Index;
        public string PartName;
        public double DistanceMeters;
        public bool IsRoot;
        public int ChildCount;
        public bool HasCargoModule;
        public bool HasCrew;
    }

    internal struct DetachTargetChoice
    {
        public DetachTargetDecision Decision;
        public DetachCandidate Candidate;

        /// <summary>The nearest detachable candidate, in range or not;
        /// <see cref="double.MaxValue"/> when none.</summary>
        public double NearestDistanceMeters;
    }

    /// <summary>One IL site the construction injection swaps for an armed-frame shim.</summary>
    internal struct EvaIlSite
    {
        public string Name;
        public OpCode[] OpCodes;
        public string DeclaringType;
        public string Member;

        /// <summary>Required parameter count of a matched method; -1 for any (fields).</summary>
        public int ParameterCount;
    }

    /// <summary>What the detach does with the part it now holds.</summary>
    internal enum DetachFollowUp
    {
        Stow,
        Drop
    }

    /// <summary>A loaded vessel that appeared after the drop was requested.</summary>
    internal struct DroppedVesselCandidate
    {
        internal uint VesselPid;
        internal int PartCount;
        internal string FirstPartName;
    }

    /// <summary>
    /// Pure decision core for the two EVA construction actions of the <c>EvaGroundScience</c>
    /// seam verb, <c>action=attach</c> and <c>action=detach</c> (the live proof of
    /// EVA-CONSTRUCTION-ON-FOREIGN-VESSEL-UNCLAIMED, lane EVA-11). The Unity applier is
    /// <c>ParsekTestCommandAddon.EvaConstruction.cs</c>.
    ///
    /// <para>WHAT STOCK DOES (decompiled KSP 1.12.5 <c>EVAConstructionModeEditor</c>).
    /// <c>Update</c>, with the construction panel open in Place mode, runs
    /// <c>UpdatePartPlacementPosition</c> (poses the held part from the mouse ray and calls the
    /// private <c>CheckAttach</c>), then, unless <c>EDITOR_SOFT_LOCK</c> is held,
    /// <c>AttachInput</c> and <c>DetachInput</c>. <c>AttachInput</c> attaches only on
    /// <c>Input.GetMouseButtonUp(0)</c> with a held part, an attachment, <c>attachmentPossible</c>,
    /// the pointer off the UI and the weight check passing; then <c>AttachPart</c> and
    /// <c>OnEVAConstructionModePartAttached(parentVessel, part)</c>. <c>DetachInput</c> detaches
    /// <c>Mouse.HoveredPart</c> on the same mouse-up when it is a cargo part within
    /// <c>EVA_CONSTRUCTION_RANGE</c>, not the root, the cursor is off every PAW / cargo pane,
    /// off the construction panel and off the UI, and <c>CanPartBeEdited</c> passes; then
    /// <c>DetachPart</c> and <c>OnEVAConstructionModePartDetached(vessel, part)</c>.</para>
    ///
    /// <para>WHAT THE SEAM CHANGES, and only on one armed frame: the mouse and pointer reads
    /// inside those two methods (an IL swap of each call site for a shim that answers like a
    /// click on the armed frame and passes through otherwise), the hovered part (the target),
    /// and for an attach the held part's pose (a postfix on <c>UpdatePartPlacementPosition</c>
    /// that puts the held node on the target node and re-runs stock's own <c>CheckAttach</c>).
    /// Every other guard - the weight check, <c>CanPartBeEdited</c>, the range tests, the
    /// collider veto - stays stock's, and stock fires the GameEvents.</para>
    /// </summary>
    internal static class TestCommandEvaConstruction
    {
        /// <summary>Attach: the part name to attach to; detach: the part name to take off.</summary>
        internal const string TargetArg = "target";

        /// <summary>Attach only: the target part's attach node id (for example <c>top</c>).</summary>
        internal const string NodeArg = "node";

        /// <summary>Attach only, optional: the held part's node to put on the target node.</summary>
        internal const string HeldNodeArg = "heldNode";

        /// <summary>Detach only, optional: what happens to the held part, <c>stow</c> (the
        /// default: a click on the kerbal's empty slot) or <c>drop</c> (the held part is posed
        /// <see cref="DropOffsetMeters"/> beside the kerbal and the panel is closed, which runs
        /// stock's own <c>ForceDrop</c> -> <c>DropAttachablePart</c>: the part becomes its own
        /// <c>DroppedPart</c> vessel).</summary>
        internal const string ThenArg = "then";

        /// <summary>Horizontal distance from the kerbal at which a dropped part is set down.</summary>
        internal const double DropOffsetMeters = 2.0;

        /// <summary>Consecutive Ready checks before the press is armed (the held part's trigger
        /// colliders are filled by physics, so the pose must hold a few frames before stock's
        /// collider veto reads the posed position).</summary>
        internal const int ReadyChecksBeforePress = 5;

        /// <summary>Frames to wait after a press before another one.</summary>
        internal const int FramesBetweenPresses = 15;

        /// <summary>Presses allowed before the action gives up.</summary>
        internal const int MaxPresses = 3;

        /// <summary>Frames to wait for stock to build the construction panel's slot for the
        /// kerbal's inventory, or to hold the clicked part.</summary>
        internal const int UiWaitFrames = 240;

        /// <summary>Frames the kerbal's weld must have run before it can count as finished
        /// (the weld state starts a frame or two after the attach).</summary>
        internal const int MinWeldFrames = 30;

        /// <summary>Upper bound on the weld wait; past it the action finishes anyway (the
        /// attach already happened) and says so.</summary>
        internal const int WeldWaitCapFrames = 900;

        /// <summary>Frames the final state must hold before OK.</summary>
        internal const int SettleFrames = 30;

        internal static readonly EvaIlSite MouseButtonUpSite = new EvaIlSite
        {
            Name = "GetMouseButtonUp",
            OpCodes = new[] { System.Reflection.Emit.OpCodes.Call },
            DeclaringType = "UnityEngine.Input",
            Member = "GetMouseButtonUp",
            ParameterCount = 1,
        };

        internal static readonly EvaIlSite PointerOverGameObjectSite = new EvaIlSite
        {
            Name = "IsPointerOverGameObject",
            OpCodes = new[] { System.Reflection.Emit.OpCodes.Callvirt, System.Reflection.Emit.OpCodes.Call },
            DeclaringType = "UnityEngine.EventSystems.EventSystem",
            Member = "IsPointerOverGameObject",
            ParameterCount = 0,
        };

        internal static readonly EvaIlSite CursorOverPaneSite = new EvaIlSite
        {
            Name = "IsCursorOverAnyPAWOrCargoPane",
            OpCodes = new[] { System.Reflection.Emit.OpCodes.Ldfld },
            DeclaringType = "UIPartActionControllerInventory",
            Member = "IsCursorOverAnyPAWOrCargoPane",
            ParameterCount = -1,
        };

        internal static readonly EvaIlSite PanelHoverSite = new EvaIlSite
        {
            Name = "Hover",
            OpCodes = new[] { System.Reflection.Emit.OpCodes.Callvirt, System.Reflection.Emit.OpCodes.Call },
            DeclaringType = "EVAConstructionModeController",
            Member = "get_Hover",
            ParameterCount = 0,
        };

        internal static readonly EvaIlSite HoveredPartSite = new EvaIlSite
        {
            Name = "HoveredPart",
            OpCodes = new[] { System.Reflection.Emit.OpCodes.Ldsfld },
            DeclaringType = "Mouse",
            Member = "HoveredPart",
            ParameterCount = -1,
        };

        /// <summary>The reads <c>AttachInput</c> takes from the mouse and the UI.</summary>
        internal static readonly EvaIlSite[] AttachInputSites =
        {
            MouseButtonUpSite,
            PointerOverGameObjectSite,
        };

        /// <summary>The reads <c>DetachInput</c> takes from the mouse and the UI.</summary>
        internal static readonly EvaIlSite[] DetachInputSites =
        {
            HoveredPartSite,
            MouseButtonUpSite,
            CursorOverPaneSite,
            PanelHoverSite,
            PointerOverGameObjectSite,
        };

        /// <summary>Does the IL instruction (<paramref name="op"/>, <paramref name="operand"/>)
        /// read <paramref name="site"/>? Matched on opcode, declaring type full name, member
        /// name and, for a method, its parameter count.</summary>
        internal static bool MatchesSite(OpCode op, object operand, EvaIlSite site)
        {
            if (site.OpCodes == null) return false;
            bool opMatches = false;
            for (int i = 0; i < site.OpCodes.Length; i++)
                if (site.OpCodes[i] == op) { opMatches = true; break; }
            if (!opMatches) return false;
            MemberInfo member = operand as MemberInfo;
            if (member == null || member.Name != site.Member) return false;
            Type declaring = member.DeclaringType;
            if (declaring == null || declaring.FullName != site.DeclaringType) return false;
            if (site.ParameterCount >= 0)
            {
                MethodBase method = member as MethodBase;
                if (method == null || method.GetParameters().Length != site.ParameterCount) return false;
            }
            return true;
        }

        /// <summary>How many instructions of <paramref name="body"/> read each site, keyed by
        /// site name (every site present, zero when unread).</summary>
        internal static Dictionary<string, int> CountSites(
            IEnumerable<KeyValuePair<OpCode, object>> body, IList<EvaIlSite> sites)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (sites == null) return counts;
            for (int s = 0; s < sites.Count; s++) counts[sites[s].Name] = 0;
            if (body == null) return counts;
            foreach (KeyValuePair<OpCode, object> ins in body)
                for (int s = 0; s < sites.Count; s++)
                    if (MatchesSite(ins.Key, ins.Value, sites[s]))
                        counts[sites[s].Name]++;
            return counts;
        }

        /// <summary>The first site (in table order) not read exactly once, or null when every
        /// site is read exactly once: the swap must replace one known call, never guess.</summary>
        internal static string FirstBadSite(IDictionary<string, int> counts, IList<EvaIlSite> sites)
        {
            if (sites == null || counts == null) return "no-sites";
            for (int s = 0; s < sites.Count; s++)
            {
                int n;
                if (!counts.TryGetValue(sites[s].Name, out n) || n != 1) return sites[s].Name;
            }
            return null;
        }

        /// <summary>Site counts as <c>name:n,name:n</c> (table order) for the log.</summary>
        internal static string FormatSiteCounts(IDictionary<string, int> counts, IList<EvaIlSite> sites)
        {
            if (sites == null || counts == null) return "<none>";
            var parts = new List<string>();
            for (int s = 0; s < sites.Count; s++)
            {
                int n;
                counts.TryGetValue(sites[s].Name, out n);
                parts.Add(sites[s].Name + ":" + n.ToString(CultureInfo.InvariantCulture));
            }
            return string.Join(",", parts.ToArray());
        }

        /// <summary>Validates the attach args: the held part, the target part and its node
        /// are all required. Returns false with the refusal reason.</summary>
        internal static bool TryValidateAttachArgs(string part, string target, string node, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(part) || part == "-") { error = "missing-part"; return false; }
            if (string.IsNullOrEmpty(target)) { error = "attach-target-missing"; return false; }
            if (string.IsNullOrEmpty(node)) { error = "attach-node-missing"; return false; }
            return true;
        }

        /// <summary>
        /// Parses the detach <see cref="ThenArg"/>: absent or empty is <see cref="DetachFollowUp.Stow"/>
        /// (the EVA-11 grammar is unchanged); <c>stow</c> / <c>drop</c> (case-insensitive) name the
        /// follow-up; anything else refuses <c>detach-then-unknown</c>.
        /// </summary>
        internal static bool TryParseDetachFollowUp(string raw, out DetachFollowUp followUp, out string error)
        {
            error = null;
            followUp = DetachFollowUp.Stow;
            if (string.IsNullOrEmpty(raw)) return true;
            if (string.Equals(raw, "stow", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(raw, "drop", StringComparison.OrdinalIgnoreCase))
            {
                followUp = DetachFollowUp.Drop;
                return true;
            }
            error = "detach-then-unknown";
            return false;
        }

        /// <summary>
        /// Did the drop happen? Stock's <c>DropAttachablePart</c> clears the held part and adds one
        /// single-part vessel named after the held part. True when nothing is held any more and a
        /// new loaded vessel (not one seen before the drop) carries exactly one part of that name.
        /// </summary>
        internal static bool IsDropSettled(bool stillHolding, IList<DroppedVesselCandidate> newVessels,
            string partName, out uint droppedVesselPid)
        {
            droppedVesselPid = 0u;
            if (stillHolding || newVessels == null || string.IsNullOrEmpty(partName)) return false;
            for (int i = 0; i < newVessels.Count; i++)
            {
                DroppedVesselCandidate c = newVessels[i];
                if (c.PartCount == 1 && string.Equals(c.FirstPartName, partName, StringComparison.Ordinal))
                {
                    droppedVesselPid = c.VesselPid;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Validates the detach args: the target part name is required.</summary>
        internal static bool TryValidateDetachArgs(string target, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(target)) { error = "detach-target-missing"; return false; }
            return true;
        }

        /// <summary>
        /// The held part's node to put on the target node: the requested one when it is free;
        /// else the node named opposite the target's (<c>top</c> onto <c>bottom</c> and back,
        /// the usual stack pairing); else the first free one; null when the held part has no
        /// free stack node.
        /// </summary>
        internal static string ChooseHeldNode(IList<string> freeStackNodes, string targetNodeId, string requested)
        {
            if (freeStackNodes == null || freeStackNodes.Count == 0) return null;
            if (!string.IsNullOrEmpty(requested))
                return freeStackNodes.Contains(requested) ? requested : null;
            string opposite = targetNodeId == "top" ? "bottom" : targetNodeId == "bottom" ? "top" : null;
            if (opposite != null && freeStackNodes.Contains(opposite)) return opposite;
            return freeStackNodes[0];
        }

        /// <summary>One posed frame's verdict, from what stock's <c>CheckAttach</c> returned and
        /// stock's own range and collider tests. Checked in stock's order: possible, parent,
        /// nodes, range, colliders.</summary>
        internal static ConstructionCheckVerdict DecideCheck(bool heldPart, bool possible,
            bool parentIsTarget, bool otherNodeIsTarget, bool callerNodeIsHeld, bool inRange,
            int blockingColliders)
        {
            if (!heldPart) return ConstructionCheckVerdict.NoHeldPart;
            if (!possible) return ConstructionCheckVerdict.NotPossible;
            if (!parentIsTarget) return ConstructionCheckVerdict.WrongParent;
            if (!otherNodeIsTarget || !callerNodeIsHeld) return ConstructionCheckVerdict.WrongNode;
            if (!inRange) return ConstructionCheckVerdict.OutOfRange;
            if (blockingColliders > 0) return ConstructionCheckVerdict.Blocked;
            return ConstructionCheckVerdict.Ready;
        }

        /// <summary>
        /// One poll while a press is pending. <paramref name="consecutiveReady"/> counts the
        /// posed frames in a row that read Ready. A press is followed by
        /// <see cref="FramesBetweenPresses"/> frames for stock to act; after
        /// <see cref="MaxPresses"/> presses the action gives up.
        /// </summary>
        internal static ConstructionPressDecision DecidePress(int consecutiveReady, int pressesSoFar,
            int framesSinceLastPress)
        {
            if (pressesSoFar > 0 && framesSinceLastPress < FramesBetweenPresses)
                return ConstructionPressDecision.Wait;
            if (pressesSoFar >= MaxPresses) return ConstructionPressDecision.GiveUp;
            if (consecutiveReady < ReadyChecksBeforePress) return ConstructionPressDecision.Wait;
            return ConstructionPressDecision.Press;
        }

        /// <summary>
        /// The part a detach takes off: among candidates named <paramref name="partName"/> that
        /// stock would let a kerbal detach (not the root, no children, a cargo part, no crew),
        /// the NEAREST within <paramref name="rangeMeters"/>; ties keep the lower index.
        /// </summary>
        internal static DetachTargetChoice ChooseDetachTarget(IEnumerable<DetachCandidate> candidates,
            string partName, double rangeMeters)
        {
            var choice = new DetachTargetChoice
            {
                Decision = DetachTargetDecision.NotFound,
                NearestDistanceMeters = double.MaxValue,
            };
            if (candidates == null || string.IsNullOrEmpty(partName)) return choice;
            bool anyNamed = false;
            bool found = false;
            DetachCandidate best = default(DetachCandidate);
            foreach (DetachCandidate c in candidates)
            {
                if (c.PartName != partName) continue;
                anyNamed = true;
                if (c.IsRoot || c.ChildCount > 0 || !c.HasCargoModule || c.HasCrew) continue;
                if (c.DistanceMeters < choice.NearestDistanceMeters)
                    choice.NearestDistanceMeters = c.DistanceMeters;
                if (!(c.DistanceMeters < rangeMeters)) continue;
                if (!found || c.DistanceMeters < best.DistanceMeters
                    || (c.DistanceMeters == best.DistanceMeters && c.Index < best.Index))
                {
                    best = c;
                    found = true;
                }
            }
            if (found)
            {
                choice.Decision = DetachTargetDecision.Found;
                choice.Candidate = best;
            }
            else if (choice.NearestDistanceMeters < double.MaxValue)
            {
                choice.Decision = DetachTargetDecision.OutOfRange;
            }
            else if (anyNamed)
            {
                choice.Decision = DetachTargetDecision.NotDetachable;
            }
            return choice;
        }

        /// <summary>The refusal reason token of a detach-target verdict.</summary>
        internal static string DetachRefusalReason(DetachTargetDecision decision)
        {
            switch (decision)
            {
                case DetachTargetDecision.NotFound: return "detach-target-not-found";
                case DetachTargetDecision.NotDetachable: return "detach-target-not-detachable";
                case DetachTargetDecision.OutOfRange: return "out-of-range";
                default: return null;
            }
        }

        /// <summary>Has the weld after an attach finished? Not before
        /// <see cref="MinWeldFrames"/>; then once the kerbal is out of the weld states; and at
        /// <see cref="WeldWaitCapFrames"/> regardless (<paramref name="capped"/> says so).</summary>
        internal static bool IsWeldSettled(bool welding, int framesSinceAttach, out bool capped)
        {
            capped = false;
            if (framesSinceAttach >= WeldWaitCapFrames)
            {
                capped = welding;
                return true;
            }
            if (framesSinceAttach < MinWeldFrames) return false;
            return !welding;
        }

        /// <summary>The attach / detach completion: the part moved, the kerbal holds nothing,
        /// held for the settle window; else wait out the budget.</summary>
        internal static GroundScienceCompletionDecision DecideCompletion(double elapsed, double budget,
            bool done, int settledFrames)
        {
            if (done && settledFrames >= SettleFrames) return GroundScienceCompletionDecision.CompleteOk;
            return elapsed >= budget
                ? GroundScienceCompletionDecision.Timeout
                : GroundScienceCompletionDecision.StillWaiting;
        }

        private static string F2(double v)
            => double.IsNaN(v) || double.IsInfinity(v) ? "nan" : v.ToString("F2", CultureInfo.InvariantCulture);

        /// <summary>The grep-stable per-check line of the attach pose.</summary>
        internal static string FormatAttachCheckLine(int check, ConstructionCheckVerdict verdict,
            bool possible, string potentialParent, uint potentialParentPid, string callerNode,
            string otherNode, string wantTarget, string wantNode, string heldNode,
            double attachDistance, double range, string blockers)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "evagroundscience attach check n=" + check.ToString(ic)
                + " verdict=" + verdict
                + " possible=" + (possible ? "true" : "false")
                + " potentialParent=" + (string.IsNullOrEmpty(potentialParent) ? "<none>" : potentialParent)
                + " potentialParentPid=" + potentialParentPid.ToString(ic)
                + " callerNode=" + (string.IsNullOrEmpty(callerNode) ? "<none>" : callerNode)
                + " otherNode=" + (string.IsNullOrEmpty(otherNode) ? "<none>" : otherNode)
                + " want=" + (wantTarget ?? "?") + "/" + (wantNode ?? "?")
                + " heldNode=" + (heldNode ?? "?")
                + " attachDistance=" + F2(attachDistance)
                + " range=" + F2(range)
                + " blockers=" + (string.IsNullOrEmpty(blockers) ? "<none>" : blockers);
        }

        /// <summary>The OK payload of an attach or detach.</summary>
        internal static List<KeyValuePair<string, string>> BuildCompletePayload(
            EvaGroundScienceAction action, string partName, uint partPid, uint vesselPid,
            int slot, int presses, double distanceMeters, int vesselParts)
        {
            List<KeyValuePair<string, string>> payload = TestCommandEvaGroundScience.BuildCompletePayload(
                action, partName, partPid, vesselPid, slot, presses, distanceMeters);
            payload.Add(new KeyValuePair<string, string>("vesselParts",
                vesselParts.ToString(CultureInfo.InvariantCulture)));
            return payload;
        }
    }
}
