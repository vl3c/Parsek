using System;
using System.Collections.Generic;
using System.Globalization;
using CommNet;
using Experience.Effects;
using UnityEngine;

namespace Parsek
{
    // Live shell of the ghost CommNet relay (design section 15.6). The pure decisions live
    // in GhostCommNet.cs; this file resolves prefabs / roster / positions and owns the
    // CommNode objects. One GhostCommNetManager per FLIGHT and TRACKSTATION host.

    /// <summary>
    /// A free CommNode for a ghost. Stock's <c>position</c> getter reads a transform the node
    /// does not have (and the CommNet UI draws links through it), so it returns
    /// <c>precisePosition</c> instead.
    /// </summary>
    internal sealed class GhostCommNetNode : CommNode
    {
        public override Vector3d position => precisePosition;
    }

    /// <summary>Resolves a recording's world position at a UT; the scene host implements it.</summary>
    internal delegate bool GhostCommNetRecordingPositionResolver(
        string recordingId, int indexHint, double ut, out Vector3d worldPos);

    /// <summary>One committed recording (or chain-ghosted vessel) offered to the manager this tick.</summary>
    internal struct GhostCommNetCandidate
    {
        public string Key;
        public string RecordingId;
        public int RecordingIndex;
        public Recording Recording;
        /// <summary>Despawn snapshot of a chain-ghosted real vessel (null for recordings).</summary>
        public ConfigNode ChainSnapshot;
        public string VesselName;
        public uint VesselPid;
        public string LaunchGuid;
        public GhostCommNetEligibility Eligibility;
        public double WindowStartUT;
        public double EndUT;
    }

    internal static class GhostCommNetDerivation
    {
        private const string Tag = GhostCommNetMath.Tag;
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Derives the antenna / control-point / crew specs of a VESSEL snapshot the way stock
        /// UpdateComm reads an unloaded vessel: prefab modules from PartLoader, the matching
        /// snapshot MODULE by stock FindModule(pm, index) semantics, CanCommUnloaded /
        /// CommPowerUnloaded (upgrades + adjusters) and CanControlUnloaded on the prefab.
        /// </summary>
        internal static GhostCommNetVesselSpec DeriveSpec(ConfigNode vesselNode, string source)
        {
            var spec = new GhostCommNetVesselSpec { SnapshotSource = source ?? "" };
            if (vesselNode == null)
                return spec;

            spec.VesselName = vesselNode.GetValue("name") ?? "";
            spec.VesselTypeName = vesselNode.GetValue("type");
            ConfigNode[] parts = vesselNode.GetNodes("PART");
            for (int p = 0; p < parts.Length; p++)
            {
                uint pid = 0;
                uint.TryParse(parts[p].GetValue("persistentId"), NumberStyles.Integer, IC, out pid);
                int parent;
                if (!int.TryParse(parts[p].GetValue("parent"), NumberStyles.Integer, IC, out parent))
                    parent = -1;
                spec.Parts.Add(new GhostPartNodeSpec { Pid = pid, ParentIndex = parent });

                string[] crew = parts[p].GetValues("crew");
                for (int c = 0; c < crew.Length; c++)
                {
                    string name = crew[c];
                    if (string.IsNullOrEmpty(name)) continue;
                    ProtoCrewMember pcm = LookupCrew(name);
                    bool qualifies = false;
                    if (pcm != null)
                    {
                        try
                        {
                            qualifies = pcm.HasEffect<FullVesselControlSkill>() && !pcm.inactive;
                        }
                        catch (Exception)
                        {
                            qualifies = false;
                        }
                    }
                    else
                    {
                        spec.UnknownCrew++;
                        ParsekLog.Verbose(Tag, string.Format(IC,
                            "Recorded crew \"{0}\" on vessel \"{1}\" is not in the roster; it does not count as a pilot",
                            name, spec.VesselName));
                    }
                    spec.Crew.Add(new GhostCrewSpec
                    {
                        PartPid = pid,
                        Name = name,
                        Known = pcm != null,
                        Qualifies = qualifies,
                    });
                }
            }

            // Stock UpdateComm order: parts last-to-first, modules last-to-first.
            for (int p = parts.Length - 1; p >= 0; p--)
            {
                string partName = parts[p].GetValue("name");
                Part prefab = ResolvePrefab(partName);
                if (prefab == null)
                {
                    spec.PartsMissingPrefab++;
                    continue;
                }

                uint pid = spec.Parts[p].Pid;
                ConfigNode[] moduleNodes = parts[p].GetNodes("MODULE");
                var moduleNames = new List<string>(moduleNodes.Length);
                for (int m = 0; m < moduleNodes.Length; m++)
                    moduleNames.Add(moduleNodes[m].GetValue("name"));

                for (int m = prefab.Modules.Count - 1; m >= 0; m--)
                {
                    PartModule pm = prefab.Modules[m];
                    if (pm == null) continue;
                    bool isRelayEnabler = pm is IRelayEnabler;
                    bool isAntenna = !isRelayEnabler && pm is ICommAntenna;
                    bool isControlPoint = !isRelayEnabler && !isAntenna && pm is ModuleProbeControlPoint;
                    if (!isRelayEnabler && !isAntenna && !isControlPoint)
                        continue;

                    int snapIndex = GhostCommNetMath.ResolveSnapshotModuleIndex(moduleNames, m, pm.moduleName);
                    ProtoPartModuleSnapshot mSnap = null;
                    if (snapIndex >= 0)
                        mSnap = new ProtoPartModuleSnapshot(moduleNodes[snapIndex]);
                    else
                        spec.ModuleSnapshotsMissing++;

                    if (isRelayEnabler)
                    {
                        bool canRelay = SafeBool(() => ((IRelayEnabler)pm).CanRelayUnloaded(mSnap), false,
                            spec.VesselName, partName, "CanRelayUnloaded");
                        spec.RelayEnablers.Add(new GhostRelayEnablerSpec { PartPid = pid, CanRelay = canRelay });
                        continue;
                    }

                    if (isAntenna)
                    {
                        var antenna = (ICommAntenna)pm;
                        bool canComm = SafeBool(() => antenna.CanCommUnloaded(mSnap), false,
                            spec.VesselName, partName, "CanCommUnloaded");
                        double power = 0.0;
                        try
                        {
                            power = antenna.CommPowerUnloaded(mSnap);
                        }
                        catch (Exception ex)
                        {
                            ParsekLog.Warn(Tag, string.Format(IC,
                                "CommPowerUnloaded threw on part '{0}' of \"{1}\": {2}; antenna ignored",
                                partName, spec.VesselName, ex.Message));
                            canComm = false;
                        }
                        var mdt = pm as ModuleDataTransmitter;
                        bool deployGated = mdt != null
                            && mdt.DeployFxModuleIndices != null
                            && mdt.DeployFxModuleIndices.Length > 0;
                        spec.Antennas.Add(new GhostAntennaSpec
                        {
                            PartPid = pid,
                            PartName = partName,
                            ModuleIndex = m,
                            Power = power,
                            IsRelay = antenna.CommType == AntennaType.RELAY,
                            Combinable = antenna.CommCombinable,
                            Exponent = antenna.CommCombinableExponent,
                            SnapshotCanComm = canComm,
                            DeployGated = deployGated,
                            RangeCurve = antenna.CommRangeCurve,
                            ScienceCurve = antenna.CommScienceCurve,
                        });
                        continue;
                    }

                    var cp = (ModuleProbeControlPoint)pm;
                    bool canOperate = SafeBool(() => cp.CanControlUnloaded(mSnap), false,
                        spec.VesselName, partName, "CanControlUnloaded");
                    spec.ControlPoints.Add(new GhostControlPointSpec
                    {
                        PartPid = pid,
                        MinimumCrew = cp.minimumCrew,
                        MultiHop = cp.multiHop,
                        CanOperate = canOperate,
                    });
                }
            }
            return spec;
        }

        private static bool SafeBool(Func<bool> f, bool fallback, string vessel, string part, string what)
        {
            try
            {
                return f();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, string.Format(IC,
                    "{0} threw on part '{1}' of \"{2}\": {3}; treated as {4}",
                    what, part ?? "?", vessel ?? "?", ex.Message, fallback));
                return fallback;
            }
        }

        private static Part ResolvePrefab(string partName)
        {
            if (string.IsNullOrEmpty(partName)) return null;
            AvailablePart ap = PartLoader.getPartInfoByName(partName.Replace('_', '.'));
            return ap != null ? ap.partPrefab : null;
        }

        private static ProtoCrewMember LookupCrew(string name)
        {
            var roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
            if (roster == null) return null;
            try
            {
                return roster[name];
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Owns the ghost CommNodes of one scene host. The host calls <see cref="Tick"/> from its
    /// Update with every committed recording (and FLIGHT chain-ghosted vessel) and their
    /// relay-window verdicts; registration and removal happen here, never inside the CommNet
    /// pre-update hook (stock iterates nodes by index there).
    /// </summary>
    internal sealed class GhostCommNetManager
    {
        private const string Tag = GhostCommNetMath.Tag;
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private sealed class Derived
        {
            public object SnapshotRef;
            public object EventsRef;
            public int EventsCount;
            public GhostCommNetVesselSpec Spec;
            public GhostCommNetTimeline Timeline;
        }

        private sealed class Entry
        {
            public GhostCommNetManager Owner;
            public string Key;
            public string RecordingId;
            public int IndexHint;
            public string VesselName;
            public uint VesselPid;
            public string LaunchGuid;
            public GhostCommNetNode Node;
            public GhostCommNetVesselSpec Spec;
            public GhostCommNetTimeline Timeline;
            public bool HoldAtEnd;
            public bool ExpectHoldPastEnd;
            public double WindowStartUT;
            public double EndUT;
            public Recording Rec;
            public string DarkLogKey;
            // Held-position source, resolved once per EndUT (see ResolveHeldSource).
            public bool HeldSourceResolved;
            public double HeldSourceEndUT;
            public GhostCommNetHeldPositionSource HeldSource;
            public CelestialBody HeldBody;
            public Orbit HeldOrbit;
            public SurfacePosition HeldSurface;
            public ConfigNode ChainSnapshot;
            public Orbit ChainOrbit;
            public bool ChainOrbitTried;
            public int LastLoggedStateIndex = -1;
            public bool Dark;
            public string Reason;

            public void PreUpdate()
            {
                Owner.RunPreUpdate(this);
            }
        }

        private readonly string scene;
        private readonly GhostCommNetRecordingPositionResolver resolveRecordingPosition;
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<string, Derived> derived = new Dictionary<string, Derived>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> lastExclusion = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> desiredScratch = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> seenScratch = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> toAdd = new List<string>();
        private readonly List<string> toRemove = new List<string>();
        private readonly List<string> pruneScratch = new List<string>();
        private readonly Dictionary<string, GhostCommNetCandidate> candidateByKey =
            new Dictionary<string, GhostCommNetCandidate>(StringComparer.Ordinal);
        private readonly List<KeyValuePair<uint, string>> relayingIdentities = new List<KeyValuePair<uint, string>>();
        private CommNetwork boundNetwork;
        private GhostCommNetAvailability? lastAvailability;
        private double rangeModifier = 1.0;
        private bool subscribed;
        private readonly string tickSummaryKey;
        private float nextTickSummaryRealtime;
        private static DoubleCurve defaultRangeCurve;

        internal GhostCommNetManager(string scene, GhostCommNetRecordingPositionResolver resolver)
        {
            this.scene = scene ?? "?";
            tickSummaryKey = "tick-summary-" + this.scene;
            resolveRecordingPosition = resolver;
            GameEvents.CommNet.OnNetworkInitialized.Add(OnNetworkInitialized);
            subscribed = true;
            ParsekLog.Info(Tag, "Ghost CommNet manager created for scene " + this.scene);
        }

        internal int RegisteredCount => entries.Count;

        internal bool TryGetRegisteredNode(string key, out CommNode node)
        {
            node = null;
            if (key != null && entries.TryGetValue(key, out Entry e))
            {
                node = e.Node;
                return true;
            }
            return false;
        }

        internal List<string> RegisteredKeys()
        {
            return new List<string>(entries.Keys);
        }

        /// <summary>
        /// The last exclusion reason noted for a candidate key that holds no node (in-game
        /// tests). False when the key was not a candidate on the last tick, or is eligible.
        /// </summary>
        internal bool TryGetExclusionReason(string key, out string reason)
        {
            reason = null;
            return key != null && lastExclusion.TryGetValue(key, out reason);
        }

        /// <summary>Timeline state of a registered node at a UT, with the current rangeModifier (in-game tests).</summary>
        internal bool TryDescribeRegistered(
            string key, out GhostCommNetState state, out double appliedRangeModifier,
            out bool holdAtEnd, out double endUT, out string recordingId, out int indexHint)
        {
            state = default(GhostCommNetState);
            appliedRangeModifier = rangeModifier;
            holdAtEnd = false;
            endUT = 0;
            recordingId = null;
            indexHint = -1;
            if (key == null || !entries.TryGetValue(key, out Entry e))
                return false;
            double now = Planetarium.GetUniversalTime();
            double sampleUT = e.HoldAtEnd || now > e.EndUT ? e.EndUT : now;
            state = e.Timeline.Sample(sampleUT);
            holdAtEnd = e.HoldAtEnd;
            endUT = e.EndUT;
            recordingId = e.RecordingId;
            indexHint = e.IndexHint;
            return true;
        }

        /// <summary>The hook's decision for a registered node at <paramref name="now"/> (in-game tests).</summary>
        internal bool TryDescribeHookSample(string key, double now, out GhostCommNetHookSample sample)
        {
            sample = default(GhostCommNetHookSample);
            if (key == null || !entries.TryGetValue(key, out Entry e))
                return false;
            sample = GhostCommNetMath.ResolveHookSample(
                now, e.WindowStartUT, e.EndUT, e.HoldAtEnd, e.ExpectHoldPastEnd);
            return true;
        }

        /// <summary>Where a held registered node sits at <paramref name="now"/> (in-game tests).</summary>
        internal bool TryResolveHeldPosition(string key, double now, out Vector3d pos)
        {
            pos = Vector3d.zero;
            if (key == null || !entries.TryGetValue(key, out Entry e))
                return false;
            return TryResolveHeldPositionCore(e, now, out pos);
        }

        internal static DoubleCurve DefaultRangeCurve
        {
            get
            {
                if (defaultRangeCurve == null)
                {
                    // Stock ModuleDataTransmitter.OnAwake default: (0,0) -> (1,1).
                    var c = new DoubleCurve();
                    c.Add(0.0, 0.0, 0.0, 0.0);
                    c.Add(1.0, 1.0, 0.0, 0.0);
                    defaultRangeCurve = c;
                }
                return defaultRangeCurve;
            }
        }

        private GhostCommNetAvailability ReadAvailability(out CommNetwork network)
        {
            network = null;
            bool scenarioPresent = CommNetScenario.Instance != null;
            Type networkType = null, commNetType = null, rangeModelType = null;
            if (scenarioPresent)
            {
                CommNetNetwork instance = CommNetNetwork.Instance;
                if (instance != null)
                {
                    networkType = instance.GetType();
                    network = instance.CommNet;
                    if (network != null) commNetType = network.GetType();
                }
                IRangeModel model = CommNetScenario.RangeModel;
                rangeModelType = model != null ? model.GetType() : null;
            }
            return GhostCommNetMath.DecideAvailability(scenarioPresent, networkType, commNetType, rangeModelType);
        }

        private void NoteAvailability(GhostCommNetAvailability availability, CommNetwork network)
        {
            if (lastAvailability.HasValue && lastAvailability.Value == availability)
                return;
            lastAvailability = availability;
            string typeDetail = availability == GhostCommNetAvailability.ForeignNetworkTypes
                ? string.Format(IC, "network={0} commNet={1} rangeModel={2}",
                    CommNetNetwork.Instance != null ? CommNetNetwork.Instance.GetType().FullName : "(null)",
                    network != null ? network.GetType().FullName : "(null)",
                    CommNetScenario.RangeModel != null ? CommNetScenario.RangeModel.GetType().FullName : "(null)")
                : null;
            GhostCommNetMath.LogAvailability(scene, availability, typeDetail);
        }

        /// <summary>
        /// Registers, updates and removes ghost nodes to match the eligible candidates.
        /// </summary>
        internal void Tick(List<GhostCommNetCandidate> candidates, double currentUT)
        {
            GhostCommNetAvailability availability = ReadAvailability(out CommNetwork network);
            NoteAvailability(availability, network);
            if (availability != GhostCommNetAvailability.Available)
            {
                if (entries.Count > 0)
                    RemoveAll("commnet-" + availability.ToString());
                return;
            }

            rangeModifier = ReadRangeModifier();
            if (!ReferenceEquals(network, boundNetwork))
                Rebind(network, "network-changed");

            candidateByKey.Clear();
            desiredScratch.Clear();
            seenScratch.Clear();
            relayingIdentities.Clear();
            int eligible = 0, typeIgnored = 0, incapable = 0, covered = 0;

            int count = candidates != null ? candidates.Count : 0;
            // Pass 1: recordings (chain vessels are checked against them in pass 2).
            for (int i = 0; i < count; i++)
            {
                GhostCommNetCandidate c = candidates[i];
                if (string.IsNullOrEmpty(c.Key) || c.ChainSnapshot != null)
                    continue;
                seenScratch.Add(c.Key);
                candidateByKey[c.Key] = c;
                if (!c.Eligibility.Eligible)
                {
                    NoteExclusion(c.Key, c.VesselName, c.Eligibility.Reason);
                    continue;
                }
                eligible++;
                Derived d = GetDerived(c.Key, c.Recording);
                if (d == null || !GhostCommNetMath.VesselTypeGetsNode(d.Spec.VesselTypeName))
                {
                    typeIgnored++;
                    NoteExclusion(c.Key, c.VesselName, "stock-ignored-vessel-type:" + (d?.Spec.VesselTypeName ?? "?"));
                    continue;
                }
                if (!d.Timeline.HasCapability)
                {
                    incapable++;
                    NoteExclusion(c.Key, c.VesselName, "no-relay-or-control-capability");
                    continue;
                }
                desiredScratch.Add(c.Key);
                relayingIdentities.Add(new KeyValuePair<uint, string>(
                    c.Recording != null ? c.Recording.VesselPersistentId : c.VesselPid,
                    c.Recording != null ? c.Recording.RecordedVesselGuid : c.LaunchGuid));
            }

            // Pass 2: chain-ghosted real vessels (FLIGHT only).
            for (int i = 0; i < count; i++)
            {
                GhostCommNetCandidate c = candidates[i];
                if (string.IsNullOrEmpty(c.Key) || c.ChainSnapshot == null)
                    continue;
                seenScratch.Add(c.Key);
                candidateByKey[c.Key] = c;
                if (!c.Eligibility.Eligible)
                {
                    NoteExclusion(c.Key, c.VesselName, c.Eligibility.Reason);
                    continue;
                }
                if (GhostCommNetMath.ChainVesselCoveredByRecording(c.VesselPid, c.LaunchGuid, relayingIdentities))
                {
                    covered++;
                    NoteExclusion(c.Key, c.VesselName, "covered-by-recording-node");
                    continue;
                }
                eligible++;
                Derived d = GetDerivedChain(c.Key, c.ChainSnapshot);
                if (!GhostCommNetMath.VesselTypeGetsNode(d.Spec.VesselTypeName))
                {
                    typeIgnored++;
                    NoteExclusion(c.Key, c.VesselName, "stock-ignored-vessel-type:" + (d.Spec.VesselTypeName ?? "?"));
                    continue;
                }
                if (!d.Timeline.HasCapability)
                {
                    incapable++;
                    NoteExclusion(c.Key, c.VesselName, "no-relay-or-control-capability");
                    continue;
                }
                desiredScratch.Add(c.Key);
            }

            GhostCommNetMath.ComputeRegistrationDiff(desiredScratch, entries.Keys, toAdd, toRemove);
            for (int i = 0; i < toRemove.Count; i++)
            {
                string key = toRemove[i];
                string reason;
                if (candidateByKey.TryGetValue(key, out GhostCommNetCandidate gone))
                    reason = lastExclusion.TryGetValue(key, out string ex) ? ex : gone.Eligibility.Reason;
                else
                    reason = "left-timeline";
                RemoveEntry(key, reason);
            }
            for (int i = 0; i < toAdd.Count; i++)
            {
                string key = toAdd[i];
                if (candidateByKey.TryGetValue(key, out GhostCommNetCandidate c))
                    AddEntry(c, network);
            }

            // Refresh per-tick fields and log timeline state transitions.
            foreach (var kv in entries)
            {
                Entry e = kv.Value;
                if (candidateByKey.TryGetValue(e.Key, out GhostCommNetCandidate c))
                {
                    e.IndexHint = c.RecordingIndex;
                    e.HoldAtEnd = c.Eligibility.HoldAtEnd;
                    e.ExpectHoldPastEnd = c.Eligibility.ExpectHoldPastEnd;
                    e.WindowStartUT = c.WindowStartUT;
                    e.EndUT = c.EndUT;
                    e.Rec = c.Recording;
                    if (e.Reason != c.Eligibility.Reason)
                    {
                        GhostCommNetMath.LogWindowChange(
                            e.Key, e.VesselName, e.Reason, c.Eligibility.Reason, currentUT, e.EndUT);
                        e.Reason = c.Eligibility.Reason;
                    }
                }
                double sampleUT = e.HoldAtEnd || currentUT > e.EndUT ? e.EndUT : currentUT;
                int stateIndex = e.Timeline.IndexAt(sampleUT);
                if (stateIndex != e.LastLoggedStateIndex)
                {
                    if (e.LastLoggedStateIndex >= 0)
                    {
                        GhostCommNetMath.LogStateChange(
                            e.Key, e.VesselName, sampleUT, e.Timeline.Sample(sampleUT), rangeModifier);
                    }
                    e.LastLoggedStateIndex = stateIndex;
                }
            }

            // Forget derivations and exclusion notes for keys that left the candidate set.
            PruneUnseen();

            // Per-frame path: build the summary only when verbose logging is on and the
            // 5 s real-time gate is open, so a quiet tick allocates nothing here.
            if (ParsekLog.IsVerboseEnabled && Time.realtimeSinceStartup >= nextTickSummaryRealtime)
            {
                nextTickSummaryRealtime = Time.realtimeSinceStartup + 5f;
                ParsekLog.VerboseRateLimited(Tag, tickSummaryKey,
                    string.Format(IC,
                        "Ghost CommNet tick ({0}): candidates={1} eligible={2} typeIgnored={3} incapable={4} " +
                        "chainCovered={5} registered={6} added={7} removed={8} rangeModifier={9:R}",
                        scene, count, eligible, typeIgnored, incapable, covered, entries.Count,
                        toAdd.Count, toRemove.Count, rangeModifier),
                    5.0);
            }
        }

        private void NoteExclusion(string key, string vesselName, string reason)
        {
            if (lastExclusion.TryGetValue(key, out string prev) && prev == reason)
                return;
            lastExclusion[key] = reason;
            ParsekLog.VerboseOnChange(Tag, "excl|" + scene + "|" + key, reason ?? "",
                string.Format(IC, "No ghost node for key={0} vessel=\"{1}\": {2}", key, vesselName ?? "", reason ?? "(none)"));
        }

        private void PruneUnseen()
        {
            pruneScratch.Clear();
            foreach (var k in derived.Keys) if (!seenScratch.Contains(k)) pruneScratch.Add(k);
            for (int i = 0; i < pruneScratch.Count; i++) derived.Remove(pruneScratch[i]);
            pruneScratch.Clear();
            foreach (var k in lastExclusion.Keys) if (!seenScratch.Contains(k)) pruneScratch.Add(k);
            for (int i = 0; i < pruneScratch.Count; i++) lastExclusion.Remove(pruneScratch[i]);
            // An eligible key clears its exclusion note so a later exclusion logs again.
            foreach (var k in desiredScratch) lastExclusion.Remove(k);
        }

        private Derived GetDerived(string key, Recording rec)
        {
            if (rec == null) return null;
            ConfigNode snapshot = rec.VesselSnapshot ?? rec.GhostVisualSnapshot;
            string source = rec.VesselSnapshot != null ? "vessel-snapshot" : "ghost-visual-snapshot";
            if (derived.TryGetValue(key, out Derived d)
                && ReferenceEquals(d.SnapshotRef, snapshot)
                && ReferenceEquals(d.EventsRef, rec.PartEvents)
                && d.EventsCount == (rec.PartEvents != null ? rec.PartEvents.Count : 0))
                return d;

            GhostCommNetVesselSpec spec = GhostCommNetDerivation.DeriveSpec(snapshot, snapshot != null ? source : "none");
            if (string.IsNullOrEmpty(spec.VesselName)) spec.VesselName = rec.VesselName ?? "";
            GhostCommNetTimeline timeline = GhostCommNetMath.BuildTimeline(spec, rec.PartEvents);
            d = new Derived
            {
                SnapshotRef = snapshot,
                EventsRef = rec.PartEvents,
                EventsCount = rec.PartEvents != null ? rec.PartEvents.Count : 0,
                Spec = spec,
                Timeline = timeline,
            };
            derived[key] = d;
            GhostCommNetMath.LogDerived(key, spec, timeline);
            if (entries.TryGetValue(key, out Entry e))
            {
                e.Spec = spec;
                e.Timeline = timeline;
                e.LastLoggedStateIndex = -1;
            }
            return d;
        }

        private Derived GetDerivedChain(string key, ConfigNode snapshot)
        {
            if (derived.TryGetValue(key, out Derived d) && ReferenceEquals(d.SnapshotRef, snapshot))
                return d;
            GhostCommNetVesselSpec spec = GhostCommNetDerivation.DeriveSpec(snapshot, "chain-despawn-snapshot");
            GhostCommNetTimeline timeline = GhostCommNetMath.BuildTimeline(spec, null);
            d = new Derived { SnapshotRef = snapshot, Spec = spec, Timeline = timeline };
            derived[key] = d;
            GhostCommNetMath.LogDerived(key, spec, timeline);
            return d;
        }

        private void AddEntry(GhostCommNetCandidate c, CommNetwork network)
        {
            Derived d = derived[c.Key];
            var node = new GhostCommNetNode();
            node.name = c.ChainSnapshot != null
                ? GhostCommNetMath.NodeNamePrefix + c.Key
                : GhostCommNetMath.NodeNameForRecording(c.RecordingId);
            string vesselName = !string.IsNullOrEmpty(c.VesselName) ? c.VesselName : d.Spec.VesselName;
            node.displayName = vesselName ?? "";
            node.isHome = false;
            node.distanceOffset = 0.0;
            node.isControlSource = false;
            node.isControlSourceMultiHop = false;
            node.antennaRelay.power = 0.0;
            node.antennaRelay.rangeCurve = DefaultRangeCurve;
            node.antennaTransmit.power = 0.0;
            node.antennaTransmit.rangeCurve = DefaultRangeCurve;

            var e = new Entry
            {
                Owner = this,
                Key = c.Key,
                RecordingId = c.RecordingId,
                IndexHint = c.RecordingIndex,
                VesselName = vesselName,
                VesselPid = c.VesselPid,
                LaunchGuid = c.LaunchGuid,
                Node = node,
                Spec = d.Spec,
                Timeline = d.Timeline,
                HoldAtEnd = c.Eligibility.HoldAtEnd,
                ExpectHoldPastEnd = c.Eligibility.ExpectHoldPastEnd,
                WindowStartUT = c.WindowStartUT,
                EndUT = c.EndUT,
                Rec = c.Recording,
                DarkLogKey = "dark-" + scene + "-" + c.Key,
                ChainSnapshot = c.ChainSnapshot,
                Reason = c.Eligibility.Reason,
            };
            node.OnNetworkPreUpdate = e.PreUpdate;
            // Prime powers and position so the node is correct on its first rebuild.
            RunPreUpdate(e);
            network.Add(node);
            entries[c.Key] = e;

            double now = Planetarium.GetUniversalTime();
            double sampleUT = e.HoldAtEnd || now > e.EndUT ? e.EndUT : now;
            e.LastLoggedStateIndex = e.Timeline.IndexAt(sampleUT);
            GhostCommNetMath.LogRegistered(
                c.Key, node.name, vesselName, scene, c.Eligibility.Reason, now,
                e.WindowStartUT, e.EndUT, e.HoldAtEnd, e.Dark,
                e.Timeline.Sample(sampleUT), rangeModifier);
        }

        private void RemoveEntry(string key, string reason)
        {
            if (!entries.TryGetValue(key, out Entry e))
                return;
            entries.Remove(key);
            e.Node.OnNetworkPreUpdate = null;
            CommNetwork current = CommNetNetwork.Instance != null ? CommNetNetwork.Instance.CommNet : null;
            if (current != null && ReferenceEquals(current, boundNetwork))
                current.Remove(e.Node);
            GhostCommNetMath.LogRemoved(key, e.VesselName, scene, reason, Planetarium.GetUniversalTime());
        }

        private void RemoveAll(string reason)
        {
            var keys = new List<string>(entries.Keys);
            for (int i = 0; i < keys.Count; i++)
                RemoveEntry(keys[i], reason);
        }

        private void Rebind(CommNetwork network, string reason)
        {
            boundNetwork = network;
            if (network == null || entries.Count == 0)
                return;
            int readded = 0;
            foreach (var kv in entries)
            {
                RunPreUpdate(kv.Value);
                network.Add(kv.Value.Node);
                readded++;
            }
            GhostCommNetMath.LogRebind(readded, scene, reason, rangeModifier);
        }

        private void OnNetworkInitialized()
        {
            GhostCommNetAvailability availability = ReadAvailability(out CommNetwork network);
            if (availability != GhostCommNetAvailability.Available)
            {
                ParsekLog.Verbose(Tag, "OnNetworkInitialized in scene " + scene + ": availability=" + availability);
                return;
            }
            rangeModifier = ReadRangeModifier();
            if (!ReferenceEquals(network, boundNetwork))
                Rebind(network, "OnNetworkInitialized");
        }

        private static double ReadRangeModifier()
        {
            try
            {
                var game = HighLogic.CurrentGame;
                if (game == null || game.Parameters == null) return 1.0;
                CommNetParams p = game.Parameters.CustomParams<CommNetParams>();
                return p != null ? (double)p.rangeModifier : 1.0;
            }
            catch (Exception)
            {
                return 1.0;
            }
        }

        private void RunPreUpdate(Entry e)
        {
            GhostCommNetNode node = e.Node;
            try
            {
                double now = Planetarium.GetUniversalTime();
                GhostCommNetHookSample sample = GhostCommNetMath.ResolveHookSample(
                    now, e.WindowStartUT, e.EndUT, e.HoldAtEnd, e.ExpectHoldPastEnd);
                if (!sample.Lit)
                {
                    SetDark(e, sample.DarkReason, now);
                    return;
                }
                // Held: the antenna / control state stays the recording's END state, but the
                // position is where the vessel about to spawn is NOW (never the stale EndUT point).
                Vector3d pos;
                bool resolved = sample.Held
                    ? TryResolveHeldPositionCore(e, now, out pos)
                    : TryResolvePosition(e, now, out pos);
                if (!resolved || !IsFinite(pos))
                {
                    SetDark(e, sample.Held ? "held-position-unresolved" : "position-unresolved", now);
                    return;
                }
                node.precisePosition = pos;
                GhostCommNetState state = e.Timeline.Sample(sample.StateUT);
                double m = rangeModifier;
                node.antennaRelay.power = state.Powers.RelayPower * m;
                node.antennaRelay.combined = state.Powers.RelayCombined;
                node.antennaRelay.rangeCurve = CurveOf(e.Spec, state.Powers.RelayAntenna, true);
                node.antennaTransmit.power = state.Powers.TransmitPower * m;
                node.antennaTransmit.combined = state.Powers.TransmitCombined;
                node.antennaTransmit.rangeCurve = CurveOf(e.Spec, state.Powers.TransmitAntenna, true);
                DoubleCurve science = CurveOf(e.Spec, state.Powers.ScienceAntenna, false);
                if (science != null) node.scienceCurve = science;
                node.isControlSource = state.IsControlSource;
                node.isControlSourceMultiHop = state.IsControlSourceMultiHop;
                if (e.Dark)
                {
                    e.Dark = false;
                    ParsekLog.Verbose(Tag, string.Format(IC,
                        "Ghost node lit again: key={0} vessel=\"{1}\" ut={2:F1} held={3}",
                        e.Key, e.VesselName, now, sample.Held));
                }
            }
            catch (Exception ex)
            {
                SetDark(e, "exception:" + ex.GetType().Name, double.NaN);
                ParsekLog.WarnRateLimited(Tag, "preupdate-exception-" + e.Key, string.Format(IC,
                    "Ghost node pre-update threw for key={0} vessel=\"{1}\": {2}: {3}",
                    e.Key, e.VesselName, ex.GetType().Name, ex.Message));
            }
        }

        private void SetDark(Entry e, string why, double ut)
        {
            GhostCommNetNode node = e.Node;
            node.antennaRelay.power = 0.0;
            node.antennaTransmit.power = 0.0;
            node.isControlSource = false;
            node.isControlSourceMultiHop = false;
            e.Dark = true;
            if (ParsekLog.IsVerboseEnabled)
                ParsekLog.VerboseRateLimited(Tag, e.DarkLogKey ?? ("dark-" + scene + "-" + e.Key), string.Format(IC,
                    "Ghost node dark this rebuild: key={0} vessel=\"{1}\" reason={2} ut={3:F1}",
                    e.Key, e.VesselName, why, ut));
        }

        /// <summary>
        /// The winning antenna's range curve (<paramref name="rangeCurve"/> true, never null:
        /// stock evaluates both curves on every link) or science curve (null when none).
        /// </summary>
        private static DoubleCurve CurveOf(GhostCommNetVesselSpec spec, int antennaIndex, bool rangeCurve)
        {
            if (spec != null && antennaIndex >= 0 && antennaIndex < spec.Antennas.Count)
            {
                GhostAntennaSpec a = spec.Antennas[antennaIndex];
                var curve = (rangeCurve ? a.RangeCurve : a.ScienceCurve) as DoubleCurve;
                if (curve != null) return curve;
            }
            return rangeCurve ? DefaultRangeCurve : null;
        }

        /// <summary>
        /// Position of a node held past EndUT at <paramref name="now"/>. Chain-ghosted vessels
        /// are never held. The source is chosen once per EndUT and logged once.
        /// </summary>
        private bool TryResolveHeldPositionCore(Entry e, double now, out Vector3d pos)
        {
            pos = Vector3d.zero;
            if (e.ChainSnapshot != null)
                return TryResolveSnapshotPosition(e, now, out pos);
            if (!e.HeldSourceResolved || e.HeldSourceEndUT != e.EndUT)
                ResolveHeldSource(e, now);
            switch (e.HeldSource)
            {
                case GhostCommNetHeldPositionSource.TerminalSurface:
                    pos = e.HeldBody.GetWorldSurfacePosition(
                        e.HeldSurface.latitude, e.HeldSurface.longitude, e.HeldSurface.altitude);
                    return true;
                case GhostCommNetHeldPositionSource.TerminalOrbit:
                    pos = e.HeldOrbit.getPositionAtUT(now);
                    return true;
                case GhostCommNetHeldPositionSource.RecordedEndBodyFixed:
                    return resolveRecordingPosition != null
                        && resolveRecordingPosition(e.RecordingId, e.IndexHint, e.EndUT, out pos);
                default:
                    return false;
            }
        }

        private void ResolveHeldSource(Entry e, double now)
        {
            e.HeldSourceResolved = true;
            e.HeldSourceEndUT = e.EndUT;
            e.HeldBody = null;
            e.HeldOrbit = null;
            Recording rec = e.Rec;
            string detail = "no-recording";
            bool hasSurface = false, terminalIsSurface = false, hasOrbit = false, endBodyFixed = false;
            if (rec != null)
            {
                terminalIsSurface = rec.TerminalStateValue == TerminalState.Landed
                    || rec.TerminalStateValue == TerminalState.Splashed;
                if (rec.TerminalPosition.HasValue)
                {
                    CelestialBody sb = FindBody(rec.TerminalPosition.Value.body);
                    if (sb != null)
                    {
                        hasSurface = true;
                        e.HeldBody = sb;
                        e.HeldSurface = rec.TerminalPosition.Value;
                    }
                }
                if (!hasSurface && !terminalIsSurface)
                {
                    // The same orbit the terminal spawn builds, so node and vessel coincide.
                    string bodyName = RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out string endpointBody)
                        ? endpointBody
                        : rec.TerminalOrbitBody;
                    CelestialBody ob = FindBody(bodyName);
                    try
                    {
                        if (ob != null
                            && VesselSpawner.TryBuildRecordedTerminalOrbitForSpawn(rec, ob, now, out Orbit orbit)
                            && orbit != null)
                        {
                            hasOrbit = true;
                            e.HeldBody = ob;
                            e.HeldOrbit = orbit;
                        }
                    }
                    catch (Exception ex)
                    {
                        ParsekLog.Warn(Tag, string.Format(IC,
                            "Held-node terminal orbit build threw for key={0}: {1}", e.Key, ex.Message));
                    }
                }
                int sectionIdx = TrajectoryMath.FindTrackSectionForUT(rec.TrackSections, e.EndUT);
                bool anchorOrCheckpointEnd = sectionIdx >= 0
                    && (rec.TrackSections[sectionIdx].referenceFrame == ReferenceFrame.Relative
                        || rec.TrackSections[sectionIdx].referenceFrame == ReferenceFrame.OrbitalCheckpoint);
                endBodyFixed = !anchorOrCheckpointEnd
                    && !TrajectoryMath.FindOrbitSegment(rec.OrbitSegments, e.EndUT).HasValue;
                detail = string.Format(IC, "terminal={0} surfacePos={1} orbit={2} endBodyFixed={3} body={4}",
                    rec.TerminalStateValue.HasValue ? rec.TerminalStateValue.Value.ToString() : "(none)",
                    hasSurface, hasOrbit, endBodyFixed, e.HeldBody != null ? e.HeldBody.name : "(none)");
            }
            e.HeldSource = GhostCommNetMath.ChooseHeldPositionSource(hasSurface, terminalIsSurface, hasOrbit, endBodyFixed);
            GhostCommNetMath.LogHeldSource(e.Key, e.VesselName, scene, e.HeldSource, e.EndUT, detail);
        }

        private static CelestialBody FindBody(string name)
        {
            if (string.IsNullOrEmpty(name) || FlightGlobals.Bodies == null)
                return null;
            for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
            {
                if (FlightGlobals.Bodies[i] != null && FlightGlobals.Bodies[i].name == name)
                    return FlightGlobals.Bodies[i];
            }
            return null;
        }

        private bool TryResolvePosition(Entry e, double ut, out Vector3d pos)
        {
            pos = Vector3d.zero;
            if (e.ChainSnapshot != null)
                return TryResolveSnapshotPosition(e, ut, out pos);
            if (resolveRecordingPosition == null)
                return false;
            return resolveRecordingPosition(e.RecordingId, e.IndexHint, ut, out pos);
        }

        /// <summary>
        /// A chain-ghosted vessel before its first claim sits on rails, so its despawn
        /// snapshot's own orbit (or landed coordinates) is its position.
        /// </summary>
        private static bool TryResolveSnapshotPosition(Entry e, double ut, out Vector3d pos)
        {
            pos = Vector3d.zero;
            ConfigNode v = e.ChainSnapshot;
            string sit = v.GetValue("sit") ?? "";
            bool landed = sit == "LANDED" || sit == "SPLASHED" || sit == "PRELAUNCH"
                || v.GetValue("landed") == "True" || v.GetValue("splashed") == "True";
            ConfigNode orbitNode = v.GetNode("ORBIT");
            if (landed)
            {
                if (orbitNode == null) return false;
                int refIndex;
                if (!int.TryParse(orbitNode.GetValue("REF"), NumberStyles.Integer, IC, out refIndex))
                    return false;
                if (FlightGlobals.Bodies == null || refIndex < 0 || refIndex >= FlightGlobals.Bodies.Count)
                    return false;
                double lat, lon, alt;
                if (!double.TryParse(v.GetValue("lat"), NumberStyles.Float, IC, out lat)
                    || !double.TryParse(v.GetValue("lon"), NumberStyles.Float, IC, out lon)
                    || !double.TryParse(v.GetValue("alt"), NumberStyles.Float, IC, out alt))
                    return false;
                pos = FlightGlobals.Bodies[refIndex].GetWorldSurfacePosition(lat, lon, alt);
                return true;
            }
            if (!e.ChainOrbitTried)
            {
                e.ChainOrbitTried = true;
                if (orbitNode != null)
                {
                    try
                    {
                        e.ChainOrbit = new OrbitSnapshot(orbitNode).Load();
                    }
                    catch (Exception ex)
                    {
                        ParsekLog.Warn(Tag, string.Format(IC,
                            "Chain ghost node orbit unreadable: key={0} vessel=\"{1}\": {2}",
                            e.Key, e.VesselName, ex.Message));
                        e.ChainOrbit = null;
                    }
                }
            }
            if (e.ChainOrbit == null) return false;
            pos = e.ChainOrbit.getPositionAtUT(ut);
            return true;
        }

        private static bool IsFinite(Vector3d v)
        {
            return !double.IsNaN(v.x) && !double.IsInfinity(v.x)
                && !double.IsNaN(v.y) && !double.IsInfinity(v.y)
                && !double.IsNaN(v.z) && !double.IsInfinity(v.z);
        }

        /// <summary>Removes every node and unsubscribes; the host calls this from OnDestroy.</summary>
        internal void Shutdown(string reason)
        {
            if (entries.Count > 0)
                RemoveAll(reason);
            derived.Clear();
            lastExclusion.Clear();
            if (subscribed)
            {
                GameEvents.CommNet.OnNetworkInitialized.Remove(OnNetworkInitialized);
                subscribed = false;
            }
            ParsekLog.Info(Tag, "Ghost CommNet manager shut down for scene " + scene + ": " + (reason ?? ""));
        }
    }
}
