using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // Pure core of the ghost CommNet relay and control point (design section 15.6).
    // Nothing in this file calls Unity or live KSP state: the live shell
    // (GhostCommNetManager.cs) resolves prefabs, the roster and positions and feeds
    // plain values in. Every decision here is unit-tested in GhostCommNetTests.

    /// <summary>One antenna that can communicate, in stock iteration order.</summary>
    internal struct GhostAntennaPowerInput
    {
        public double Power;
        public bool IsRelay;
        public bool Combinable;
        public double Exponent;
    }

    /// <summary>
    /// Result of stock's CommNetVessel.UpdateComm antenna combination. Antenna indices
    /// point into the input list (-1 = none) and select the range / science curves.
    /// </summary>
    internal struct GhostCommNetPowers
    {
        public double TransmitPower;
        public double RelayPower;
        public int TransmitAntenna;
        public int RelayAntenna;
        public int ScienceAntenna;
        public bool TransmitCombined;
        public bool RelayCombined;

        public static GhostCommNetPowers None => new GhostCommNetPowers
        {
            TransmitAntenna = -1,
            RelayAntenna = -1,
            ScienceAntenna = -1,
        };

        public bool Equals(GhostCommNetPowers other)
        {
            return TransmitPower == other.TransmitPower
                && RelayPower == other.RelayPower
                && TransmitAntenna == other.TransmitAntenna
                && RelayAntenna == other.RelayAntenna
                && ScienceAntenna == other.ScienceAntenna
                && TransmitCombined == other.TransmitCombined
                && RelayCombined == other.RelayCombined;
        }
    }

    /// <summary>A derived antenna of the recorded vessel (prefab values + snapshot state).</summary>
    internal struct GhostAntennaSpec
    {
        public uint PartPid;
        public string PartName;
        public int ModuleIndex;
        public double Power;
        public bool IsRelay;
        public bool Combinable;
        public double Exponent;
        /// <summary>Stock <c>CanCommUnloaded</c> on the snapshot module.</summary>
        public bool SnapshotCanComm;
        /// <summary>The prefab transmitter is gated on a deploy animation (DeployFxModules).</summary>
        public bool DeployGated;
        /// <summary>Opaque range curve (a KSP DoubleCurve in the live shell; null in tests).</summary>
        public object RangeCurve;
        public object ScienceCurve;
    }

    internal struct GhostControlPointSpec
    {
        public uint PartPid;
        public int MinimumCrew;
        public bool MultiHop;
        /// <summary>Stock <c>CanControlUnloaded</c> on the snapshot module.</summary>
        public bool CanOperate;
    }

    internal struct GhostCrewSpec
    {
        public uint PartPid;
        public string Name;
        /// <summary>Roster member has FullVesselControlSkill and is not inactive.</summary>
        public bool Qualifies;
        /// <summary>False when the name is not in the roster (never counts).</summary>
        public bool Known;
    }

    internal struct GhostRelayEnablerSpec
    {
        public uint PartPid;
        public bool CanRelay;
    }

    internal struct GhostPartNodeSpec
    {
        public uint Pid;
        public int ParentIndex;
    }

    /// <summary>Everything the timeline needs about one recorded vessel, derived once and cached.</summary>
    internal sealed class GhostCommNetVesselSpec
    {
        public string VesselName = "";
        /// <summary>The snapshot's VESSEL <c>type</c> value (null when absent).</summary>
        public string VesselTypeName;
        /// <summary>Antennas in stock UpdateComm order (parts last-to-first, modules last-to-first).</summary>
        public readonly List<GhostAntennaSpec> Antennas = new List<GhostAntennaSpec>();
        public readonly List<GhostControlPointSpec> ControlPoints = new List<GhostControlPointSpec>();
        public readonly List<GhostCrewSpec> Crew = new List<GhostCrewSpec>();
        public readonly List<GhostRelayEnablerSpec> RelayEnablers = new List<GhostRelayEnablerSpec>();
        /// <summary>Snapshot PART order with parent indices, for decouple subtrees.</summary>
        public readonly List<GhostPartNodeSpec> Parts = new List<GhostPartNodeSpec>();
        public int PartsMissingPrefab;
        public int ModuleSnapshotsMissing;
        public int UnknownCrew;
        public string SnapshotSource = "";
    }

    /// <summary>The node state at one UT: powers before rangeModifier plus control flags.</summary>
    internal struct GhostCommNetState
    {
        public GhostCommNetPowers Powers;
        public bool IsControlSource;
        public bool IsControlSourceMultiHop;
        public int ActiveAntennas;

        public bool Equals(GhostCommNetState other)
        {
            return Powers.Equals(other.Powers)
                && IsControlSource == other.IsControlSource
                && IsControlSourceMultiHop == other.IsControlSourceMultiHop
                && ActiveAntennas == other.ActiveAntennas;
        }

        /// <summary>A node is worth registering when it can relay, or is a control point that can link at all.</summary>
        public bool HasCapability =>
            Powers.RelayPower > 0.0
            || (IsControlSource && (Powers.RelayPower + Powers.TransmitPower) > 0.0);
    }

    /// <summary>Piecewise-constant node state over a recording's UT range.</summary>
    internal sealed class GhostCommNetTimeline
    {
        // Uts[0] is -infinity; state i holds for Uts[i] <= ut < Uts[i+1].
        internal readonly List<double> Uts = new List<double>();
        internal readonly List<GhostCommNetState> States = new List<GhostCommNetState>();

        internal int Count => States.Count;

        internal bool HasCapability
        {
            get
            {
                for (int i = 0; i < States.Count; i++)
                    if (States[i].HasCapability) return true;
                return false;
            }
        }

        internal int IndexAt(double ut)
        {
            if (States.Count == 0) return -1;
            int lo = 0, hi = Uts.Count - 1, found = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (Uts[mid] <= ut) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return found;
        }

        internal GhostCommNetState Sample(double ut)
        {
            int i = IndexAt(ut);
            return i < 0 ? new GhostCommNetState { Powers = GhostCommNetPowers.None } : States[i];
        }
    }

    internal enum GhostCommNetAvailability
    {
        Available,
        CommNetDisabled,
        NetworkNotReady,
        ForeignNetworkTypes,
    }

    /// <summary>Per-recording inputs of the relay window predicate, gathered by the scene host.</summary>
    internal struct GhostCommNetEligibilityInput
    {
        public bool HasRecordingId;
        public bool IsDebris;
        public bool HasRenderableData;
        public bool SupersededByRelation;
        public bool RewindRetired;
        public bool SessionSuppressed;
        public bool ExternalVesselSuppressed;
        /// <summary>BUG-B scope gate for the REAL run (the spawn variant, so loop members are gated too).</summary>
        public bool HistoricalNeverReplayed;
        public double ActivationStartUT;
        public double EndUT;
        public bool NeedsSpawn;
        public bool VesselSpawned;
        public bool SpawnAbandoned;
        public bool CannotSpawnSafely;
        public bool IsMidChain;
        public double ChainEndUT;
        public bool ChainSuccessorStarted;
        /// <summary>
        /// The recording's real terminal spawn is owned by a later continuation (an
        /// intermediate ghost-chain link, or a terminal spawn superseded by a continuation),
        /// so the vessel it ended as sits on rails at its end state until that continuation
        /// takes over at <see cref="ContinuationHoldUntilUT"/>.
        /// </summary>
        public bool HasContinuationHold;
        public double ContinuationHoldUntilUT;
    }

    internal struct GhostCommNetEligibility
    {
        public bool Eligible;
        /// <summary>Past EndUT, held at the end position until a spawn or a chain continuation takes over.</summary>
        public bool HoldAtEnd;
        /// <summary>
        /// In the window, and the host already knows the node will be held once EndUT passes
        /// (spawn pending, or a mid-chain segment before its chain's end). Lets the CommNet
        /// hook bridge the EndUT seam before the host's next tick turns it into a hold.
        /// </summary>
        public bool ExpectHoldPastEnd;
        public string Reason;
    }

    /// <summary>Where a node held past its recording's end is placed at the current UT.</summary>
    internal enum GhostCommNetHeldPositionSource
    {
        /// <summary>No sound source: the node is dark while held.</summary>
        None,
        /// <summary>The recorded terminal surface position; body-fixed, so it turns with the body.</summary>
        TerminalSurface,
        /// <summary>The terminal orbit the spawn path would use, propagated to the current UT.</summary>
        TerminalOrbit,
        /// <summary>The recording's own end sample, which is body-fixed (no orbit or anchor at EndUT).</summary>
        RecordedEndBodyFixed,
    }

    /// <summary>What the CommNet pre-update hook does with a node on one rebuild.</summary>
    internal struct GhostCommNetHookSample
    {
        public bool Lit;
        /// <summary>Past EndUT: state from EndUT, position from the held source at the current UT.</summary>
        public bool Held;
        /// <summary>UT the antenna / control state is sampled at.</summary>
        public double StateUT;
        public string DarkReason;
    }

    internal static class GhostCommNetMath
    {
        internal const string Tag = "GhostCommNet";
        internal const string NodeNamePrefix = "ParsekGhost:";
        internal const string ReasonChainGapHold = "chain-gap-hold";
        internal const string ReasonContinuationGapHold = "continuation-gap-hold";
        internal const string ReasonWindowEnded = "window-ended";
        internal const string ReasonInWindow = "in-window";
        /// <summary>UT slack when comparing a recording's end with a takeover UT.</summary>
        internal const double ContinuationTakeoverToleranceSeconds = 1e-3;
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Stock <c>CommNetVessel.UpdateComm</c> antenna combination (KSP 1.12.5), transcribed
        /// verbatim. <paramref name="antennas"/> holds only antennas that can communicate, in
        /// stock iteration order. Stock quirk kept: with no non-relay antenna the combined
        /// relay power is the raw sum of the combinable relays (no exponent weighting).
        /// </summary>
        internal static GhostCommNetPowers ComputeVesselPowers(
            IList<GhostAntennaPowerInput> antennas,
            bool relayEnablerActive,
            double rangeModifier)
        {
            var result = GhostCommNetPowers.None;
            int bestDirect = -1;          // commAntenna2
            int bestRelay = -1;           // commAntenna3
            int bestCombDirect = -1;      // commAntenna4
            int bestCombRelay = -1;       // commAntenna5
            double maxDirect = 0.0;       // num2
            double sumCombDirect = 0.0;   // num3
            double expCombDirect = 0.0;   // num4
            double maxCombDirect = 0.0;   // num5
            double maxRelay = 0.0;        // num6
            double sumCombRelay = 0.0;    // num7
            double expCombRelay = 0.0;    // num8
            double maxCombRelay = 0.0;    // num9

            int count = antennas != null ? antennas.Count : 0;
            for (int i = 0; i < count; i++)
            {
                GhostAntennaPowerInput a = antennas[i];
                double p = a.Power;
                if (!a.IsRelay)
                {
                    if (p > maxDirect)
                    {
                        bestDirect = i;
                        maxDirect = p;
                    }
                    if (!a.Combinable)
                        continue;
                    sumCombDirect += p;
                    expCombDirect += p * a.Exponent;
                    if (bestCombDirect != -1 && !(p > maxCombDirect))
                        continue;
                    bestCombDirect = i;
                    maxCombDirect = p;
                    continue;
                }

                if (p > maxRelay)
                {
                    bestRelay = i;
                    maxRelay = p;
                    if (a.Combinable)
                        bestCombRelay = i;
                }
                if (!a.Combinable)
                    continue;
                sumCombRelay += p;
                expCombRelay += p * a.Exponent;
                if (bestCombRelay != -1 && !(p > maxCombRelay))
                    continue;
                bestCombRelay = i;
                maxCombRelay = p;
            }

            if (bestDirect != -1)
            {
                double total = sumCombDirect + sumCombRelay;
                bool directStronger = maxCombDirect > maxCombRelay;
                if (total > 0.0)
                {
                    double strongest = directStronger ? maxCombDirect : maxCombRelay;
                    if (total != strongest)
                    {
                        double y = (expCombDirect + expCombRelay) / total;
                        total = strongest * Math.Pow(total / strongest, y);
                    }
                    if (sumCombRelay > 0.0 && sumCombRelay != maxCombRelay)
                    {
                        sumCombRelay = maxCombRelay
                            * Math.Pow(sumCombRelay / maxCombRelay, expCombRelay / sumCombRelay);
                    }
                }
                if (sumCombDirect > 0.0 && total > maxDirect)
                {
                    if (bestCombRelay != -1)
                        bestDirect = directStronger ? bestCombDirect : bestCombRelay;
                    else
                        bestDirect = bestCombDirect;
                    result.TransmitCombined = true;
                }
                else
                {
                    total = maxDirect;
                    result.TransmitCombined = false;
                }
                result.TransmitPower = total;
                result.TransmitAntenna = bestDirect;
                result.ScienceAntenna = bestDirect;
            }

            if (bestRelay != -1)
            {
                double relay;
                if (sumCombRelay > maxRelay)
                {
                    relay = sumCombRelay;
                    bestRelay = bestCombRelay;
                    result.RelayCombined = true;
                }
                else
                {
                    relay = maxRelay;
                    result.RelayCombined = false;
                }
                result.RelayPower = relay;
                result.RelayAntenna = bestRelay;
                if (bestDirect == -1 || relay > result.TransmitPower)
                    result.ScienceAntenna = bestRelay;
            }

            if (relayEnablerActive && result.TransmitPower > result.RelayPower)
            {
                result.RelayPower = result.TransmitPower;
                result.RelayCombined = result.TransmitCombined;
                result.RelayAntenna = result.TransmitAntenna;
                result.TransmitPower = 0.0;
            }

            result.TransmitPower *= rangeModifier;
            result.RelayPower *= rangeModifier;
            return result;
        }

        /// <summary>
        /// Stock control-point rule (CommNetVessel.UpdateComm): every operable
        /// ModuleProbeControlPoint makes the vessel a control source when its minimumCrew is 0
        /// or the vessel carries at least minimumCrew qualifying crew; any operable module with
        /// multiHop sets the multi-hop flag, whether or not that module made the vessel a
        /// source (stock sets both flags independently).
        /// </summary>
        internal static void DecideControlPoint(
            IList<GhostControlPointSpec> operableControlPoints,
            int qualifyingCrew,
            out bool isControlSource,
            out bool isControlSourceMultiHop)
        {
            isControlSource = false;
            isControlSourceMultiHop = false;
            int count = operableControlPoints != null ? operableControlPoints.Count : 0;
            for (int i = 0; i < count; i++)
            {
                GhostControlPointSpec cp = operableControlPoints[i];
                if (!cp.CanOperate)
                    continue;
                if (cp.MinimumCrew > 0)
                {
                    if (qualifyingCrew >= cp.MinimumCrew)
                        isControlSource = true;
                }
                else
                {
                    isControlSource = true;
                }
                if (cp.MultiHop)
                    isControlSourceMultiHop = true;
            }
        }

        /// <summary>
        /// Stock's vessel types that get no CommNet node (CommNetVessel.OnStart): Debris,
        /// SpaceObject, Unknown, Flag and DeployedSciencePart. An absent or unparsable type
        /// keeps the node (stock has no such vessel; the recording is still a vessel).
        /// </summary>
        internal static bool VesselTypeGetsNode(string vesselTypeName)
        {
            if (string.IsNullOrEmpty(vesselTypeName))
                return true;
            VesselType type;
            try
            {
                type = (VesselType)Enum.Parse(typeof(VesselType), vesselTypeName.Trim(), true);
            }
            catch (ArgumentException)
            {
                return true;
            }
            if (!Enum.IsDefined(typeof(VesselType), type))
                return true;
            if (type == VesselType.Flag) return false;
            if (type == VesselType.DeployedSciencePart) return false;
            return type > VesselType.Unknown;
        }

        /// <summary>
        /// Stock <c>ProtoPartSnapshot.FindModule(pm, index)</c>: the snapshot module at the
        /// prefab index when its name matches, otherwise the LAST snapshot module with the
        /// prefab module's name; -1 when none.
        /// </summary>
        internal static int ResolveSnapshotModuleIndex(
            IList<string> snapshotModuleNames, int prefabModuleIndex, string prefabModuleName)
        {
            if (snapshotModuleNames == null)
                return -1;
            if (prefabModuleIndex >= 0
                && prefabModuleIndex < snapshotModuleNames.Count
                && snapshotModuleNames[prefabModuleIndex] == prefabModuleName)
                return prefabModuleIndex;
            for (int i = snapshotModuleNames.Count - 1; i >= 0; i--)
            {
                if (snapshotModuleNames[i] == prefabModuleName)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Builds the piecewise-constant node state for a recorded vessel. Breakpoints are the
        /// part events that change which antennas / control points / crew exist or can
        /// communicate: DeployableExtended / Retracted / Broken on deploy-gated antennas,
        /// Decoupled (removes the part's subtree, as ghost playback hides it) and Destroyed
        /// (removes the part). Powers are stored before rangeModifier.
        /// </summary>
        internal static GhostCommNetTimeline BuildTimeline(
            GhostCommNetVesselSpec spec, IList<PartEvent> partEvents)
        {
            var timeline = new GhostCommNetTimeline();
            if (spec == null)
                return timeline;

            var relevantPids = new HashSet<uint>();
            var deployGatedPids = new HashSet<uint>();
            for (int i = 0; i < spec.Antennas.Count; i++)
            {
                relevantPids.Add(spec.Antennas[i].PartPid);
                if (spec.Antennas[i].DeployGated)
                    deployGatedPids.Add(spec.Antennas[i].PartPid);
            }
            for (int i = 0; i < spec.ControlPoints.Count; i++)
                relevantPids.Add(spec.ControlPoints[i].PartPid);
            for (int i = 0; i < spec.Crew.Count; i++)
                relevantPids.Add(spec.Crew[i].PartPid);
            for (int i = 0; i < spec.RelayEnablers.Count; i++)
                relevantPids.Add(spec.RelayEnablers[i].PartPid);

            // Decoupling a part removes its whole subtree, so a decouple event on any snapshot
            // part can matter; collect the events that can change state.
            var events = new List<PartEvent>();
            int eventCount = partEvents != null ? partEvents.Count : 0;
            var snapshotPids = new HashSet<uint>();
            for (int i = 0; i < spec.Parts.Count; i++)
                snapshotPids.Add(spec.Parts[i].Pid);
            for (int i = 0; i < eventCount; i++)
            {
                PartEvent e = partEvents[i];
                switch (e.eventType)
                {
                    case PartEventType.DeployableExtended:
                    case PartEventType.DeployableRetracted:
                    case PartEventType.DeployableBroken:
                        if (deployGatedPids.Contains(e.partPersistentId))
                            events.Add(e);
                        break;
                    case PartEventType.Decoupled:
                        if (snapshotPids.Contains(e.partPersistentId)
                            || relevantPids.Contains(e.partPersistentId))
                            events.Add(e);
                        break;
                    case PartEventType.Destroyed:
                        if (relevantPids.Contains(e.partPersistentId))
                            events.Add(e);
                        break;
                }
            }
            // Stable sort by UT (List.Sort is unstable; order by UT then original index).
            var order = new List<int>(events.Count);
            for (int i = 0; i < events.Count; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                int c = events[a].ut.CompareTo(events[b].ut);
                return c != 0 ? c : a.CompareTo(b);
            });
            var sorted = new List<PartEvent>(events.Count);
            for (int i = 0; i < order.Count; i++) sorted.Add(events[order[i]]);

            var childrenByIndex = BuildChildIndex(spec.Parts);

            GhostCommNetState initial = EvaluateStateAt(spec, sorted, double.NegativeInfinity, childrenByIndex);
            timeline.Uts.Add(double.NegativeInfinity);
            timeline.States.Add(initial);
            for (int i = 0; i < sorted.Count; i++)
            {
                double ut = sorted[i].ut;
                if (i + 1 < sorted.Count && sorted[i + 1].ut == ut)
                    continue;
                GhostCommNetState s = EvaluateStateAt(spec, sorted, ut, childrenByIndex);
                if (s.Equals(timeline.States[timeline.States.Count - 1]))
                    continue;
                timeline.Uts.Add(ut);
                timeline.States.Add(s);
            }
            return timeline;
        }

        private static List<int>[] BuildChildIndex(List<GhostPartNodeSpec> parts)
        {
            var children = new List<int>[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                int parent = parts[i].ParentIndex;
                if (parent < 0 || parent >= parts.Count || parent == i)
                    continue;
                if (children[parent] == null) children[parent] = new List<int>();
                children[parent].Add(i);
            }
            return children;
        }

        /// <summary>Pids removed by Decoupled (subtree) / Destroyed (part) events at or before <paramref name="ut"/>.</summary>
        internal static HashSet<uint> ComputeRemovedPids(
            GhostCommNetVesselSpec spec, IList<PartEvent> sortedEvents, double ut)
        {
            return ComputeRemovedPids(spec, sortedEvents, ut, BuildChildIndex(spec.Parts));
        }

        private static HashSet<uint> ComputeRemovedPids(
            GhostCommNetVesselSpec spec, IList<PartEvent> sortedEvents, double ut, List<int>[] children)
        {
            var removed = new HashSet<uint>();
            for (int i = 0; i < sortedEvents.Count; i++)
            {
                PartEvent e = sortedEvents[i];
                if (e.ut > ut) break;
                if (e.eventType == PartEventType.Destroyed)
                {
                    removed.Add(e.partPersistentId);
                }
                else if (e.eventType == PartEventType.Decoupled)
                {
                    removed.Add(e.partPersistentId);
                    int root = -1;
                    for (int p = 0; p < spec.Parts.Count; p++)
                    {
                        if (spec.Parts[p].Pid == e.partPersistentId) { root = p; break; }
                    }
                    if (root < 0) continue;
                    var stack = new Stack<int>();
                    stack.Push(root);
                    var visited = new HashSet<int>();
                    while (stack.Count > 0)
                    {
                        int n = stack.Pop();
                        if (!visited.Add(n)) continue;
                        removed.Add(spec.Parts[n].Pid);
                        if (children[n] == null) continue;
                        for (int c = 0; c < children[n].Count; c++) stack.Push(children[n][c]);
                    }
                }
            }
            return removed;
        }

        /// <summary>
        /// Can a deploy-gated antenna communicate at <paramref name="ut"/>? The last deploy event
        /// at or before it decides (Extended = yes, Retracted / Broken = no). Before its first
        /// event the antenna is in the opposite state of that event (a first Broken implies it
        /// was extended, since stock antennas break while deployed). With no events the
        /// snapshot's own CanCommUnloaded decides.
        /// </summary>
        internal static bool DeployGatedCanCommAt(
            uint partPid, IList<PartEvent> sortedEvents, double ut, bool snapshotCanComm)
        {
            PartEventType? last = null;
            PartEventType? first = null;
            for (int i = 0; i < sortedEvents.Count; i++)
            {
                PartEvent e = sortedEvents[i];
                if (e.partPersistentId != partPid) continue;
                if (e.eventType != PartEventType.DeployableExtended
                    && e.eventType != PartEventType.DeployableRetracted
                    && e.eventType != PartEventType.DeployableBroken)
                    continue;
                if (first == null) first = e.eventType;
                if (e.ut <= ut) last = e.eventType;
            }
            if (last.HasValue)
                return last.Value == PartEventType.DeployableExtended;
            if (first.HasValue)
                return first.Value != PartEventType.DeployableExtended;
            return snapshotCanComm;
        }

        private static GhostCommNetState EvaluateStateAt(
            GhostCommNetVesselSpec spec, List<PartEvent> sortedEvents, double ut, List<int>[] children)
        {
            HashSet<uint> removed = ComputeRemovedPids(spec, sortedEvents, ut, children);

            var inputs = new List<GhostAntennaPowerInput>(spec.Antennas.Count);
            var inputAntennaIndex = new List<int>(spec.Antennas.Count);
            for (int i = 0; i < spec.Antennas.Count; i++)
            {
                GhostAntennaSpec a = spec.Antennas[i];
                if (removed.Contains(a.PartPid)) continue;
                bool canComm = a.DeployGated
                    ? DeployGatedCanCommAt(a.PartPid, sortedEvents, ut, a.SnapshotCanComm)
                    : a.SnapshotCanComm;
                if (!canComm) continue;
                inputs.Add(new GhostAntennaPowerInput
                {
                    Power = a.Power,
                    IsRelay = a.IsRelay,
                    Combinable = a.Combinable,
                    Exponent = a.Exponent,
                });
                inputAntennaIndex.Add(i);
            }

            bool relayEnabler = false;
            for (int i = 0; i < spec.RelayEnablers.Count; i++)
            {
                if (spec.RelayEnablers[i].CanRelay && !removed.Contains(spec.RelayEnablers[i].PartPid))
                {
                    relayEnabler = true;
                    break;
                }
            }

            GhostCommNetPowers powers = ComputeVesselPowers(inputs, relayEnabler, 1.0);
            powers.TransmitAntenna = MapIndex(powers.TransmitAntenna, inputAntennaIndex);
            powers.RelayAntenna = MapIndex(powers.RelayAntenna, inputAntennaIndex);
            powers.ScienceAntenna = MapIndex(powers.ScienceAntenna, inputAntennaIndex);

            int qualifying = 0;
            for (int i = 0; i < spec.Crew.Count; i++)
            {
                GhostCrewSpec c = spec.Crew[i];
                if (c.Known && c.Qualifies && !removed.Contains(c.PartPid))
                    qualifying++;
            }
            var operable = new List<GhostControlPointSpec>(spec.ControlPoints.Count);
            for (int i = 0; i < spec.ControlPoints.Count; i++)
            {
                if (!removed.Contains(spec.ControlPoints[i].PartPid))
                    operable.Add(spec.ControlPoints[i]);
            }
            DecideControlPoint(operable, qualifying, out bool control, out bool multiHop);

            return new GhostCommNetState
            {
                Powers = powers,
                IsControlSource = control,
                IsControlSourceMultiHop = multiHop,
                ActiveAntennas = inputs.Count,
            };
        }

        private static int MapIndex(int inputIndex, List<int> map)
        {
            return inputIndex >= 0 && inputIndex < map.Count ? map[inputIndex] : -1;
        }

        /// <summary>
        /// The relay window of a committed recording (design 15.6). The real run only: the
        /// recording's own UT window from its ghost activation to its end, then held at the
        /// end position while its spawn is pending, while a mid-chain segment waits for its
        /// continuation, or while a later continuation that owns its terminal spawn has not
        /// taken the vessel over yet. The playback-enabled toggle is display only and is not
        /// an input.
        /// </summary>
        internal static GhostCommNetEligibility EvaluateEligibility(
            GhostCommNetEligibilityInput input, double currentUT)
        {
            if (!input.HasRecordingId) return Excluded("no-recording-id");
            if (input.IsDebris) return Excluded("debris");
            if (!input.HasRenderableData) return Excluded("no-trajectory");
            if (input.RewindRetired) return Excluded("rewind-retired");
            if (input.SupersededByRelation) return Excluded("superseded");
            if (input.SessionSuppressed) return Excluded("re-fly-session-suppressed");
            if (input.ExternalVesselSuppressed) return Excluded("real-vessel-exists");
            if (input.HistoricalNeverReplayed) return Excluded("historical-never-replayed");
            if (double.IsNaN(currentUT) || currentUT < input.ActivationStartUT)
                return Excluded("before-window");
            if (currentUT <= input.EndUT)
                return new GhostCommNetEligibility
                {
                    Eligible = true,
                    Reason = ReasonInWindow,
                    ExpectHoldPastEnd = ExpectsHoldPastEnd(input),
                };
            if (input.VesselSpawned) return Excluded("vessel-spawned");
            if (input.NeedsSpawn && !input.SpawnAbandoned && !input.CannotSpawnSafely)
                return new GhostCommNetEligibility { Eligible = true, HoldAtEnd = true, Reason = "held-for-spawn" };
            if (input.IsMidChain && currentUT <= input.ChainEndUT && !input.ChainSuccessorStarted)
                return new GhostCommNetEligibility { Eligible = true, HoldAtEnd = true, Reason = ReasonChainGapHold };
            if (input.HasContinuationHold && currentUT < input.ContinuationHoldUntilUT)
                return new GhostCommNetEligibility { Eligible = true, HoldAtEnd = true, Reason = ReasonContinuationGapHold };
            if (input.SpawnAbandoned || input.CannotSpawnSafely) return Excluded("spawn-abandoned");
            return Excluded(ReasonWindowEnded);
        }

        private static GhostCommNetEligibility Excluded(string reason)
        {
            return new GhostCommNetEligibility { Eligible = false, Reason = reason };
        }

        /// <summary>Will this in-window recording be held once its EndUT passes?</summary>
        internal static bool ExpectsHoldPastEnd(GhostCommNetEligibilityInput input)
        {
            if (input.VesselSpawned) return false;
            if (input.NeedsSpawn && !input.SpawnAbandoned && !input.CannotSpawnSafely) return true;
            if (input.HasContinuationHold && input.EndUT < input.ContinuationHoldUntilUT) return true;
            return input.IsMidChain && input.EndUT < input.ChainEndUT;
        }

        /// <summary>
        /// Should a scene host that ticks slower than every frame (the Tracking Station, every
        /// 0.25 s of real time) work out a recording's post-end hold while it is still in its
        /// window? Only near the end: <paramref name="lookaheadSeconds"/> of game time covers
        /// the next host tick, so the CommNet hook knows to keep the node lit across EndUT
        /// before the host's next tick turns it into a hold. Earlier ticks skip the cost.
        /// </summary>
        internal static bool ShouldPredictHoldPastEnd(double currentUT, double endUT, double lookaheadSeconds)
        {
            if (double.IsNaN(currentUT) || double.IsNaN(endUT) || currentUT > endUT)
                return false;
            if (double.IsNaN(lookaheadSeconds) || lookaheadSeconds < 0.0)
                lookaheadSeconds = 0.0;
            return endUT - currentUT <= lookaheadSeconds;
        }

        /// <summary>
        /// Game-time lookahead for <see cref="ShouldPredictHoldPastEnd"/>: two host ticks at the
        /// current warp rate, never less than one second.
        /// </summary>
        internal static double HoldPredictionLookaheadSeconds(double tickIntervalRealSeconds, double warpRate)
        {
            if (double.IsNaN(warpRate) || warpRate < 1.0) warpRate = 1.0;
            if (double.IsNaN(tickIntervalRealSeconds) || tickIntervalRealSeconds < 0.0) tickIntervalRealSeconds = 0.0;
            return Math.Max(1.0, 2.0 * tickIntervalRealSeconds * warpRate);
        }

        /// <summary>
        /// Does a later continuation own this recording's real terminal spawn, so that the
        /// vessel it ended as keeps existing, on rails at its end state, until that
        /// continuation takes it over (design 15.6 scenario 15)? Either the ghost-chain walker
        /// suppressed an otherwise ready spawn because a later claim continues the vessel, or
        /// the terminal spawn was superseded by a continuation recording. The superseded case
        /// is checked structurally: a spawnable terminal on a leaf that is not debris, ghost-only
        /// or an undock branch, and has not spawned, been adopted or been destroyed.
        /// </summary>
        internal static bool ContinuationOwnsTerminalSpawn(
            bool chainIntermediateWouldSpawn,
            bool terminalSpawnSuperseded,
            bool hasSpawnableTerminal,
            bool isLeaf,
            bool isDebris,
            bool isGhostOnly,
            bool isBranchGhostOnly,
            bool alreadySpawnedOrDestroyed)
        {
            if (isDebris || alreadySpawnedOrDestroyed)
                return false;
            if (chainIntermediateWouldSpawn)
                return true;
            if (!terminalSpawnSuperseded)
                return false;
            return hasSpawnableTerminal && isLeaf && !isGhostOnly && !isBranchGhostOnly;
        }

        /// <summary>
        /// When the continuation takes the vessel over, for a recording that ended at
        /// <paramref name="endUT"/>: the earliest of the later ghost-chain claims on the vessel
        /// (<paramref name="claimUTs"/>, a dock or board merges it into another vessel whose own
        /// recording carries it) and the activation of a later recording of the same vessel
        /// (<paramref name="carrierWindows"/>, activation start and end). Carriers that ended by
        /// <paramref name="endUT"/> are history. A carrier already running at
        /// <paramref name="endUT"/> carries the vessel itself, so there is no hold (NaN); so is
        /// a vessel with no known takeover, which is never held open-ended.
        /// </summary>
        internal static double ResolveContinuationHoldUntilUT(
            double endUT,
            IList<double> claimUTs,
            IList<KeyValuePair<double, double>> carrierWindows)
        {
            const double tol = ContinuationTakeoverToleranceSeconds;
            if (double.IsNaN(endUT))
                return double.NaN;
            double until = double.PositiveInfinity;
            int claims = claimUTs != null ? claimUTs.Count : 0;
            for (int i = 0; i < claims; i++)
            {
                double ut = claimUTs[i];
                if (double.IsNaN(ut) || ut < endUT - tol) continue;
                if (ut < until) until = ut;
            }
            int carriers = carrierWindows != null ? carrierWindows.Count : 0;
            for (int i = 0; i < carriers; i++)
            {
                double start = carrierWindows[i].Key;
                double end = carrierWindows[i].Value;
                if (double.IsNaN(start) || double.IsNaN(end) || end <= endUT + tol) continue;
                if (start <= endUT + tol) return double.NaN;
                if (start < until) until = start;
            }
            return double.IsPositiveInfinity(until) ? double.NaN : until;
        }

        /// <summary>
        /// Collects the takeover UTs of <paramref name="rec"/> and resolves its continuation
        /// hold (<see cref="ResolveContinuationHoldUntilUT"/>). Claims: every link of
        /// <paramref name="chain"/> (the ghost chain that suppressed its spawn; null when none).
        /// Carriers: every other committed recording of the same launch (baked pid AND a launch
        /// guid that does not conclusively differ), and the recording its terminal spawn is
        /// superseded by. <paramref name="source"/> names what ends the hold.
        /// </summary>
        internal static bool TryResolveContinuationHold(
            Recording rec,
            IReadOnlyList<Recording> committed,
            GhostChain chain,
            List<double> claimScratch,
            List<KeyValuePair<double, double>> carrierScratch,
            out double holdUntilUT,
            out string source)
        {
            holdUntilUT = double.NaN;
            source = "none";
            if (rec == null)
                return false;
            var claims = claimScratch ?? new List<double>();
            var carriers = carrierScratch ?? new List<KeyValuePair<double, double>>();
            claims.Clear();
            carriers.Clear();
            if (chain != null && chain.Links != null)
            {
                for (int i = 0; i < chain.Links.Count; i++)
                    claims.Add(chain.Links[i].ut);
            }
            string supersededBy = rec.TerminalSpawnSupersededByRecordingId;
            int count = committed != null ? committed.Count : 0;
            for (int i = 0; i < count; i++)
            {
                Recording other = committed[i];
                if (other == null || ReferenceEquals(other, rec))
                    continue;
                if (!string.IsNullOrEmpty(rec.RecordingId)
                    && string.Equals(other.RecordingId, rec.RecordingId, StringComparison.Ordinal))
                    continue;
                bool isSuperseder = !string.IsNullOrEmpty(supersededBy)
                    && string.Equals(other.RecordingId, supersededBy, StringComparison.Ordinal);
                if (!isSuperseder && !VesselLaunchIdentity.RecordingsShareLaunch(rec, other))
                    continue;
                carriers.Add(new KeyValuePair<double, double>(
                    PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(other), other.EndUT));
            }
            holdUntilUT = ResolveContinuationHoldUntilUT(rec.EndUT, claims, carriers);
            if (double.IsNaN(holdUntilUT))
                return false;
            source = DescribeContinuationTakeover(holdUntilUT, claims, carriers);
            return true;
        }

        /// <summary>
        /// Fills the continuation-hold fields of a scene host's eligibility input for one
        /// committed recording, and logs the outcome once per change. Cheap early-out for the
        /// common case (no chain suppression, no superseded terminal spawn). A vessel that is
        /// real in the scene carries its own stock node and is never held.
        /// </summary>
        internal static void ApplyContinuationHold(
            ref GhostCommNetEligibilityInput input,
            Recording rec,
            IReadOnlyList<Recording> committed,
            Dictionary<uint, GhostChain> chains,
            bool chainIntermediateWouldSpawn,
            Func<Recording, bool> realVesselExists,
            string scene,
            List<double> claimScratch,
            List<KeyValuePair<double, double>> carrierScratch)
        {
            input.HasContinuationHold = false;
            input.ContinuationHoldUntilUT = 0.0;
            if (rec == null)
                return;
            bool superseded = !string.IsNullOrEmpty(rec.TerminalSpawnSupersededByRecordingId);
            if (!chainIntermediateWouldSpawn && !superseded)
                return;

            string key = rec.RecordingId;
            bool spawnable = rec.TerminalStateValue.HasValue
                && GhostPlaybackLogic.IsSpawnableTerminal(rec.TerminalStateValue.Value);
            bool owns = ContinuationOwnsTerminalSpawn(
                chainIntermediateWouldSpawn,
                superseded,
                spawnable,
                string.IsNullOrEmpty(rec.ChildBranchPointId),
                rec.IsDebris,
                rec.IsGhostOnly,
                rec.ChainBranch > 0,
                rec.VesselSpawned || rec.SpawnedVesselPersistentId != 0 || rec.VesselDestroyed);
            if (!owns)
            {
                if (ParsekLog.IsVerboseEnabled)
                    LogContinuationHold(key, rec.VesselName, scene, false, rec.EndUT, 0.0, null,
                        "terminal spawn not owned by a continuation");
                return;
            }
            if (realVesselExists != null && realVesselExists(rec))
            {
                if (ParsekLog.IsVerboseEnabled)
                    LogContinuationHold(key, rec.VesselName, scene, false, rec.EndUT, 0.0, null,
                        "real vessel exists and carries its own node");
                return;
            }
            GhostChain chain = chains != null ? GhostChainWalker.FindIntermediateLinkChain(chains, rec) : null;
            if (TryResolveContinuationHold(rec, committed, chain, claimScratch, carrierScratch,
                    out double until, out string source))
            {
                input.HasContinuationHold = true;
                input.ContinuationHoldUntilUT = until;
                if (ParsekLog.IsVerboseEnabled)
                    LogContinuationHold(key, rec.VesselName, scene, true, rec.EndUT, until, source, null);
                return;
            }
            if (ParsekLog.IsVerboseEnabled)
                LogContinuationHold(key, rec.VesselName, scene, false, rec.EndUT, 0.0, null,
                    "no takeover after the recording's end, or a later recording already carries the vessel");
        }

        private static string DescribeContinuationTakeover(
            double until, List<double> claims, List<KeyValuePair<double, double>> carriers)
        {
            for (int i = 0; i < carriers.Count; i++)
                if (carriers[i].Key == until) return "later-recording-of-vessel";
            for (int i = 0; i < claims.Count; i++)
                if (claims[i] == until) return "ghost-chain-claim";
            return "unknown";
        }

        /// <summary>
        /// Registration precedence when several candidates stand for one physical vessel:
        /// recordings in their window or spawn / chain holds first (pass 1), then a
        /// chain-ghosted vessel's despawn-snapshot node (pass 2, before its first claim), then
        /// continuation gap holds (pass 3). Pass 2 and 3 yield to an identity already relaying,
        /// so one vessel never carries two nodes.
        /// </summary>
        internal static int RegistrationPass(bool isChainGhostedVessel, string eligibilityReason)
        {
            if (isChainGhostedVessel) return 2;
            return eligibilityReason == ReasonContinuationGapHold ? 3 : 1;
        }

        /// <summary>
        /// The CommNet pre-update hook's decision for one rebuild. Before the window: dark.
        /// In the window: state and position at the current UT. Past EndUT: held (state at
        /// EndUT, position from the held source at the current UT) when the host holds it or
        /// already expects to; otherwise dark, even between two host ticks, so a destroyed
        /// vessel never relays past its recorded end.
        /// </summary>
        internal static GhostCommNetHookSample ResolveHookSample(
            double now, double windowStartUT, double endUT, bool holdAtEnd, bool expectHoldPastEnd)
        {
            if (holdAtEnd)
                return new GhostCommNetHookSample { Lit = true, Held = true, StateUT = endUT };
            if (double.IsNaN(now) || now < windowStartUT)
                return new GhostCommNetHookSample { DarkReason = "before-window", StateUT = now };
            if (now <= endUT)
                return new GhostCommNetHookSample { Lit = true, StateUT = now };
            if (expectHoldPastEnd)
                return new GhostCommNetHookSample { Lit = true, Held = true, StateUT = endUT };
            return new GhostCommNetHookSample { DarkReason = "past-end", StateUT = endUT };
        }

        /// <summary>
        /// Where a held node sits. A recorded terminal surface position wins (body-fixed, and
        /// what a landed spawn uses). A non-surface end with the spawn path's terminal orbit is
        /// propagated to the current UT, so the node stays with its planet through a warp-long
        /// spawn hold. Otherwise the recording's own end sample is used only when it is
        /// body-fixed (no orbit segment and no anchor-relative section at EndUT); an orbit or
        /// anchor end without a propagatable orbit has no sound position and stays dark.
        /// </summary>
        internal static GhostCommNetHeldPositionSource ChooseHeldPositionSource(
            bool hasTerminalSurfacePosition,
            bool terminalIsSurface,
            bool hasTerminalOrbit,
            bool endSampleIsBodyFixed)
        {
            if (hasTerminalSurfacePosition)
                return GhostCommNetHeldPositionSource.TerminalSurface;
            if (!terminalIsSurface && hasTerminalOrbit)
                return GhostCommNetHeldPositionSource.TerminalOrbit;
            if (endSampleIsBodyFixed)
                return GhostCommNetHeldPositionSource.RecordedEndBodyFixed;
            return GhostCommNetHeldPositionSource.None;
        }

        /// <summary>
        /// Registration only happens against stock CommNet types. RemoteTech and the difficulty
        /// switch both disable CommNet, so stock creates no CommNetScenario; RealAntennas /
        /// CommNetManager-derived networks replace the network or range model types.
        /// </summary>
        internal static GhostCommNetAvailability DecideAvailability(
            bool scenarioPresent, Type networkType, Type commNetType, Type rangeModelType)
        {
            if (!scenarioPresent)
                return GhostCommNetAvailability.CommNetDisabled;
            if (networkType == null || commNetType == null)
                return GhostCommNetAvailability.NetworkNotReady;
            if (networkType != typeof(CommNet.CommNetNetwork)
                || commNetType != typeof(CommNet.CommNetwork)
                || rangeModelType != typeof(CommNet.CommRangeModel))
                return GhostCommNetAvailability.ForeignNetworkTypes;
            return GhostCommNetAvailability.Available;
        }

        /// <summary>Keys to add and remove to turn <paramref name="registered"/> into <paramref name="desired"/>.</summary>
        internal static void ComputeRegistrationDiff(
            ICollection<string> desired,
            ICollection<string> registered,
            List<string> toAdd,
            List<string> toRemove)
        {
            toAdd.Clear();
            toRemove.Clear();
            if (registered != null)
            {
                foreach (string key in registered)
                {
                    if (desired == null || !desired.Contains(key))
                        toRemove.Add(key);
                }
            }
            if (desired != null)
            {
                foreach (string key in desired)
                {
                    if (registered == null || !registered.Contains(key))
                        toAdd.Add(key);
                }
            }
            toAdd.Sort(StringComparer.Ordinal);
            toRemove.Sort(StringComparer.Ordinal);
        }

        /// <summary>
        /// A chain-ghosted real vessel keeps its own node only while no committed recording
        /// of the same launch (baked pid AND a launch guid that does not conclusively differ)
        /// is already relaying, so one physical vessel never counts twice.
        /// </summary>
        internal static bool ChainVesselCoveredByRecording(
            uint chainPid,
            string chainLaunchGuid,
            IList<KeyValuePair<uint, string>> relayingRecordingIdentities)
        {
            if (relayingRecordingIdentities == null) return false;
            for (int i = 0; i < relayingRecordingIdentities.Count; i++)
            {
                var id = relayingRecordingIdentities[i];
                if (id.Key != chainPid) continue;
                if (VesselLaunchIdentity.GuidsConclusivelyDiffer(id.Value, chainLaunchGuid)) continue;
                return true;
            }
            return false;
        }

        internal static string NodeNameForRecording(string recordingId)
        {
            return NodeNamePrefix + (recordingId ?? "");
        }

        internal static string ChainKey(uint pid)
        {
            return "chain:" + pid.ToString(IC);
        }

        internal static void LogAvailability(string scene, GhostCommNetAvailability availability, string typeDetail)
        {
            switch (availability)
            {
                case GhostCommNetAvailability.CommNetDisabled:
                    ParsekLog.Info(Tag, "CommNet is off in scene " + scene
                        + " (difficulty setting, or a mod such as RemoteTech that replaces CommNet): no ghost CommNet nodes");
                    break;
                case GhostCommNetAvailability.ForeignNetworkTypes:
                    ParsekLog.Info(Tag, "CommNet network types are not stock in scene " + scene
                        + " (" + (typeDetail ?? "") + "); a CommNet-replacing mod (RealAntennas, CommNetManager)"
                        + " is active, ghost CommNet nodes are skipped");
                    break;
                case GhostCommNetAvailability.NetworkNotReady:
                    ParsekLog.Verbose(Tag, "CommNet network not initialized yet in scene " + scene);
                    break;
                default:
                    ParsekLog.Info(Tag, "Stock CommNet available in scene " + scene + ": ghost CommNet nodes enabled");
                    break;
            }
        }

        internal static void LogRegistered(
            string key, string nodeName, string vesselName, string scene, string reason, double ut,
            double windowStartUT, double endUT, bool holdAtEnd, bool dark,
            GhostCommNetState state, double rangeModifier)
        {
            ParsekLog.Info(Tag, string.Format(IC,
                "Registered ghost node: key={0} node={1} vessel=\"{2}\" scene={3} reason={4} ut={5:F1} " +
                "window=[{6:F1},{7:F1}] hold={8} dark={9} {10}",
                key, nodeName, vesselName ?? "", scene, reason ?? "(none)", ut,
                windowStartUT, endUT, holdAtEnd, dark, FormatState(state, rangeModifier)));
        }

        internal static void LogRemoved(string key, string vesselName, string scene, string reason, double ut)
        {
            ParsekLog.Info(Tag, string.Format(IC,
                "Removed ghost node: key={0} vessel=\"{1}\" scene={2} reason={3} ut={4:F1}",
                key, vesselName ?? "", scene, reason ?? "(none)", ut));
        }

        internal static void LogWindowChange(
            string key, string vesselName, string fromReason, string toReason, double ut, double endUT)
        {
            ParsekLog.Info(Tag, string.Format(IC,
                "Ghost node window: key={0} vessel=\"{1}\" {2} -> {3} ut={4:F1} endUT={5:F1}",
                key, vesselName ?? "", fromReason ?? "(none)", toReason ?? "(none)", ut, endUT));
        }

        internal static void LogStateChange(
            string key, string vesselName, double ut, GhostCommNetState state, double rangeModifier)
        {
            ParsekLog.Info(Tag, string.Format(IC,
                "Ghost node state change: key={0} vessel=\"{1}\" ut={2:F1} {3}",
                key, vesselName ?? "", ut, FormatState(state, rangeModifier)));
        }

        /// <summary>Once per held entry (per EndUT): where the held node is placed and why.</summary>
        internal static void LogHeldSource(
            string key, string vesselName, string scene, GhostCommNetHeldPositionSource source, double endUT, string detail)
        {
            ParsekLog.Info(Tag, string.Format(IC,
                "Held ghost node position source: key={0} vessel=\"{1}\" scene={2} source={3} endUT={4:F1} {5}",
                key, vesselName ?? "", scene, source, endUT, detail ?? ""));
        }

        /// <summary>
        /// The held-node position line (<c>GhostCommNetManager.NoteHeldPosition</c>): the node's
        /// distance from the held body's centre and the angle swept along a terminal orbit since
        /// EndUT (0 for surface and end-sample holds).
        /// </summary>
        internal static string FormatHeldPosition(
            string key, string vesselName, string scene, GhostCommNetHeldPositionSource source,
            double ut, double endUT, double radius, double sweptDeg)
        {
            return string.Format(IC,
                "Held ghost node position: key={0} vessel=\"{1}\" scene={2} source={3} ut={4:F1} " +
                "sinceEnd={5:F1} radius={6:F0} sweptDeg={7:F2}",
                key, vesselName ?? "", scene, source, ut, ut - endUT, radius, sweptDeg);
        }

        /// <summary>
        /// Once per change of a recording's continuation hold (design 15.6 scenario 15): the
        /// recording's terminal spawn is owned by a later continuation, and until when its node
        /// stands for the vessel on rails at its end state.
        /// </summary>
        internal static void LogContinuationHold(
            string key, string vesselName, string scene, bool hold, double endUT, double untilUT,
            string source, string why)
        {
            string state = hold
                ? string.Format(IC, "until={0:R}|{1}", untilUT, source ?? "")
                : "none|" + (why ?? "");
            string message = hold
                ? string.Format(IC,
                    "Continuation hold: key={0} vessel=\"{1}\" scene={2} endUT={3:F1} until={4:F1} takeover={5} " +
                    "(a later continuation owns the terminal spawn; the node stays at the end state until it takes over)",
                    key, vesselName ?? "", scene, endUT, untilUT, source ?? "")
                : string.Format(IC,
                    "Continuation hold: key={0} vessel=\"{1}\" scene={2} endUT={3:F1} none ({4})",
                    key, vesselName ?? "", scene, endUT, why ?? "");
            ParsekLog.VerboseOnChange(Tag, "cont-hold|" + (scene ?? "") + "|" + (key ?? ""), state, message);
        }

        internal static void LogRebind(int readded, string scene, string reason, double rangeModifier)
        {
            ParsekLog.Info(Tag, string.Format(IC,
                "Re-added {0} ghost node(s) to the new CommNet network in scene {1} ({2}), rangeModifier={3:R}",
                readded, scene, reason, rangeModifier));
        }

        /// <summary>
        /// One summary line per derivation (cached per recording). Info for a vessel that can
        /// relay or control; Verbose for the many that cannot, so a large timeline does not
        /// flood the log with antenna-less vessels.
        /// </summary>
        internal static void LogDerived(string key, GhostCommNetVesselSpec spec, GhostCommNetTimeline timeline)
        {
            string line = FormatSpecSummary(key, spec, timeline);
            if (timeline != null && timeline.HasCapability)
                ParsekLog.Info(Tag, line);
            else
                ParsekLog.Verbose(Tag, line);
        }

        internal static string FormatState(GhostCommNetState state, double rangeModifier)
        {
            return string.Format(IC,
                "relay={0:R} transmit={1:R} relayCombined={2} transmitCombined={3} control={4} multiHop={5} antennas={6}",
                state.Powers.RelayPower * rangeModifier,
                state.Powers.TransmitPower * rangeModifier,
                state.Powers.RelayCombined,
                state.Powers.TransmitCombined,
                state.IsControlSource,
                state.IsControlSourceMultiHop,
                state.ActiveAntennas);
        }

        internal static string FormatSpecSummary(string key, GhostCommNetVesselSpec spec, GhostCommNetTimeline timeline)
        {
            int relay = 0, direct = 0, gated = 0;
            for (int i = 0; i < spec.Antennas.Count; i++)
            {
                if (spec.Antennas[i].IsRelay) relay++; else direct++;
                if (spec.Antennas[i].DeployGated) gated++;
            }
            int known = 0, qualifying = 0;
            for (int i = 0; i < spec.Crew.Count; i++)
            {
                if (spec.Crew[i].Known) known++;
                if (spec.Crew[i].Known && spec.Crew[i].Qualifies) qualifying++;
            }
            return string.Format(IC,
                "Derived antennas: key={0} vessel=\"{1}\" source={2} type={3} parts={4} relayAntennas={5} " +
                "otherAntennas={6} deployGated={7} controlPoints={8} relayEnablers={9} crew={10} " +
                "knownCrew={11} pilots={12} unknownCrew={13} partsMissingPrefab={14} " +
                "moduleSnapshotsMissing={15} timelineStates={16} capable={17}",
                key,
                spec.VesselName ?? "",
                spec.SnapshotSource ?? "",
                spec.VesselTypeName ?? "(none)",
                spec.Parts.Count,
                relay,
                direct,
                gated,
                spec.ControlPoints.Count,
                spec.RelayEnablers.Count,
                spec.Crew.Count,
                known,
                qualifying,
                spec.UnknownCrew,
                spec.PartsMissingPrefab,
                spec.ModuleSnapshotsMissing,
                timeline != null ? timeline.Count : 0,
                timeline != null && timeline.HasCapability);
        }
    }
}
