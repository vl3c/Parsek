using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Identity of ONE half of a settled dock seam, exactly as that half's own
    /// <c>ModuleDockingNode.vesselInfo</c> (<c>DockedVesselInfo</c>) carries it: the half's
    /// PRE-dock vessel name, vessel type and root part <c>flightID</c>. Decompiled
    /// (KSP 1.12.5) <c>ModuleDockingNode.DockToVessel</c> writes a <c>DockedVesselInfo</c> on
    /// BOTH nodes, each describing ITS OWN half, and the record round-trips through the
    /// node's <c>DOCKEDVESSEL</c> save node. There is NO vessel pid and NO launch guid in it.
    ///
    /// <para><see cref="RootPartUId"/> is a KSP part <c>flightID</c>: assigned per launch and
    /// NOT baked into the <c>.craft</c>, so unlike <c>persistentId</c> it is a launch-unique
    /// key and needs no guid gate to be trusted as an identity.</para>
    /// </summary>
    internal readonly struct DockSeamHalfIdentity
    {
        /// <summary>False when that side's node carried no <c>vesselInfo</c> at all.</summary>
        public readonly bool HasInfo;
        public readonly string VesselName;
        public readonly int VesselType; // (int)VesselType; -1 = unknown
        public readonly uint RootPartUId;

        internal DockSeamHalfIdentity(bool hasInfo, string vesselName, int vesselType, uint rootPartUId)
        {
            HasInfo = hasInfo;
            VesselName = vesselName;
            VesselType = vesselType;
            RootPartUId = rootPartUId;
        }
    }

    /// <summary>
    /// Whether a settled dock seam's node PAIR can be captured as a start-docked origin
    /// candidate at all. Explicit values: the names are interpolated into the producer's
    /// skip lines and asserted by tests.
    ///
    /// <para>THERE IS NO ORIGIN CHOICE HERE (P12). A route candidate is defined by
    /// TRANSFERS AND DOCKS, not by a vessel type: the transport takes cargo at one docked
    /// partner and delivers it at another, and bases are ordinary vessels. So capture
    /// records BOTH halves and defers the choice to the UNDOCK, where the half the RUN was
    /// flying is the transport and the other one is the origin (see
    /// <see cref="RouteProofCapture.ResolveTransportHalfAtUndock"/>).</para>
    /// </summary>
    internal enum DockSeamPairAdmission
    {
        /// <summary>Both halves have a usable identity and can own cargo - capture the pair.</summary>
        Admitted,
        /// <summary>A half is debris / EVA / a flag / a space object: not a valid cargo owner.</summary>
        InvalidCargoOwner,
        /// <summary>A half carried no <c>vesselInfo</c> or a zero root part id.</summary>
        HalfIdentityMissing,
    }

    /// <summary>
    /// Start-docked seam-pair candidate used by
    /// <see cref="RouteProofCapture.TryResolveStartDockedSeamPair"/>. ONE settled dock seam on
    /// the merged vessel, carrying BOTH halves of the seam's node PAIR with NO origin chosen:
    /// the undock binds that (P12).
    ///
    /// <para>ONE producer builds these (see
    /// <c>FlightRecorder.CaptureStartRouteOriginProofIfDocked</c>): a SETTLED DOCK SEAM
    /// (<see cref="RouteProofCapture.IsSettledDockSeam"/>). The pre-2026-09-02 reading (a part
    /// whose <c>part.parent</c> still belongs to a different vessel) is COUNTED but no longer
    /// emits: it produced zero candidates on both hosts that have measured it, and no
    /// mechanism is claimed for when it could be non-zero.</para>
    ///
    /// <para>NO VESSEL PID IS CARRIED, deliberately. <c>Part.Couple</c> destroys the absorbed
    /// half's <c>Vessel</c>, so that half's pid is unrecoverable; and the surviving half's pid
    /// is the merged vessel's, which names whichever half stock made dominant rather than
    /// either half in particular. Identity is each half's <c>RootPartUId</c>. Derivation:
    /// <c>docs/dev/research/origin-proof-partner-identity-memo.md</c>.</para>
    ///
    /// <para>The body-fixed descriptor fields are the MERGED pair's, which is exactly right:
    /// while the halves are docked they occupy the same place, so the merged vessel's body,
    /// coordinates and situation ARE both halves'.</para>
    /// </summary>
    internal readonly struct DockSeamPairCandidate
    {
        /// <summary>The seam part on the merged vessel (diagnostic).</summary>
        public readonly uint PartPersistentId;
        /// <summary>The near half: the node scanned on the merged vessel. NOT "the origin".</summary>
        public readonly DockSeamHalfIdentity Near;
        /// <summary>The far half: the docked partner part's facing node. NOT "the transport".</summary>
        public readonly DockSeamHalfIdentity Far;
        /// <summary>Near half's part persistentIds on the merged vessel; null when unresolvable.</summary>
        public readonly List<uint> NearPartPersistentIds;
        /// <summary>Far half's part persistentIds on the merged vessel; null when unresolvable.</summary>
        public readonly List<uint> FarPartPersistentIds;
        public readonly int MergedVesselSituation; // (int)Vessel.Situations; -1 = unknown
        public readonly string MergedVesselBodyName;
        public readonly double MergedVesselLatitude;
        public readonly double MergedVesselLongitude;
        public readonly double MergedVesselAltitude;

        internal DockSeamPairCandidate(
            uint partPersistentId,
            DockSeamHalfIdentity near,
            DockSeamHalfIdentity far,
            List<uint> nearPartPersistentIds,
            List<uint> farPartPersistentIds,
            int mergedVesselSituation,
            string mergedVesselBodyName,
            double mergedVesselLatitude,
            double mergedVesselLongitude,
            double mergedVesselAltitude)
        {
            PartPersistentId = partPersistentId;
            Near = near;
            Far = far;
            NearPartPersistentIds = nearPartPersistentIds;
            FarPartPersistentIds = farPartPersistentIds;
            MergedVesselSituation = mergedVesselSituation;
            MergedVesselBodyName = mergedVesselBodyName;
            MergedVesselLatitude = mergedVesselLatitude;
            MergedVesselLongitude = mergedVesselLongitude;
            MergedVesselAltitude = mergedVesselAltitude;
        }
    }

    /// <summary>
    /// Outcome of the pure start-docked seam-pair resolver. <see cref="Captured"/> is the
    /// only branch that produces a usable PAIR; all others identify the specific reason the
    /// recording should NOT carry a RouteOriginProof.
    /// </summary>
    internal enum OriginProofDetection
    {
        Captured,
        NoExternalCoupling,
        ActiveVesselPrelaunch,
        PartnerPrelaunch,
        /// <summary>Every candidate carried a zero root part id on a half.</summary>
        PartnerPidZero,
        /// <summary>Two or more seams named DIFFERENT pairs on one merged vessel.</summary>
        PartnerAmbiguous,
    }

    /// <summary>
    /// Which half of the captured pair the UNDOCK bound the origin to, or why it could not.
    /// Only <see cref="BoundToHalfA"/> / <see cref="BoundToHalfB"/> produce an origin;
    /// everything else leaves the proof unbound, and an unbound proof is never forwarded as
    /// an origin.
    ///
    /// <para>The two BOUND values are produced twice over: first by
    /// <see cref="RouteProofCapture.ClassifyUndockOriginBinding"/> reading the post-split
    /// FOCUS, then possibly re-pointed by
    /// <see cref="RouteProofCapture.ResolveTransportHalfAtUndock"/>, which outranks focus
    /// with the run's own actions. The REFUSAL values only ever come from the first.</para>
    /// </summary>
    internal enum OriginUndockBinding
    {
        BoundToHalfA,
        BoundToHalfB,
        /// <summary>The proof carries no pending pair (already bound, or pre-P12).</summary>
        NoPairPending,
        /// <summary>The active side matched neither captured half.</summary>
        ActiveHalfUnresolved,
        /// <summary>The active side still owns parts of BOTH halves - this split did not separate the pair.</summary>
        BothHalvesActive,
        /// <summary>Both sides fell inside ONE half: an unrelated seam split, not this pair's.</summary>
        UnrelatedSeam,
    }

    /// <summary>
    /// Whether the origin half's LIVE vessel pid may be stamped onto the proof at the bind.
    /// The root part id is the identity either way; the pid is a cheaper corroborating key.
    /// </summary>
    internal enum OriginPidStampDecision
    {
        /// <summary>No live vessel resolved for the origin half - keep the pid slot at 0.</summary>
        NoLiveVessel,
        /// <summary>Stamped; the launch guids agree that this is not the recorded launch.</summary>
        Stamped,
        /// <summary>Stamped, but a guid was unknown on one side (pid-only fallback).</summary>
        StampedGuidUnknown,
        /// <summary>
        /// REFUSED: the candidate origin pid IS the recorded launch's, and the guids do not
        /// conclusively differ - stamping it would make the recorded vessel its own origin.
        /// </summary>
        RefusedSameLaunch,
    }

    internal static class RouteProofCapture
    {
        /// <summary>
        /// Pure / static. True when the partner situation pins the origin to the
        /// surface: LANDED or SPLASHED. Mirrors
        /// <c>RouteEndpointResolver.IsSurfaceSituation</c> minus PRELAUNCH, which
        /// <see cref="TryResolveStartDockedOriginPartner"/> already excludes for
        /// partners.
        /// </summary>
        internal static bool IsSurfaceOriginSituation(int situation)
        {
            return situation == (int)Vessel.Situations.LANDED
                || situation == (int)Vessel.Situations.SPLASHED;
        }

        /// <summary>
        /// Pure / static. True when one <c>ModuleDockingNode</c> on the active vessel records a
        /// SETTLED cross-vessel dock - the seam the start-docked origin proof is derived from.
        ///
        /// <para>Why this predicate and not <c>part.parent.vessel != activeVessel</c>:
        /// <c>Part.Couple</c> reassigns <c>vessel</c> across the whole absorbed subtree, so after
        /// a settled dock every part reads <c>p.parent.vessel == v</c> and the parent-identity
        /// candidate list is empty. That is ROUTE-ORIGIN-PROOF-PRODUCER-UNREACHABLE, confirmed
        /// live by H56's probe (<c>externalParentParts=0 ... outcome=no-external-coupling</c>).
        /// The docking node keeps its OWN docked-partner information across the couple: stock
        /// <c>ModuleDockingNode.DockToVessel</c> sets <c>vesselInfo</c> (the pre-dock vessel
        /// identity of each half) and <c>dockedPartUId</c> (the partner part's flightID), and both
        /// round-trip through the node's <c>DOCKEDVESSEL</c> / <c>dockUId</c> save keys - so the
        /// seam survives save/load, which the parent-identity reading never could.</para>
        ///
        /// <para>Each input is load-bearing. <paramref name="hasDockedVesselInfo"/> is the
        /// CROSS-VESSEL discriminator: stock creates <c>vesselInfo</c> only in
        /// <c>DockToVessel</c>, never for an editor-preattached port pair and never in
        /// <c>DockToSameVessel</c>, so a craft built with two ports stuck together cannot forge an
        /// origin. <paramref name="dockedPartUId"/> non-zero rejects a node that never recorded a
        /// partner part. <paramref name="partnerPartResolvesOnSameVessel"/> is the SETTLED half:
        /// after an undock the partner part lives on a different <c>Vessel</c>, so a node that
        /// kept a stale <c>vesselInfo</c> cannot keep producing a seam.</para>
        /// </summary>
        internal static bool IsSettledDockSeam(
            bool hasDockedVesselInfo,
            uint dockedPartUId,
            bool partnerPartResolvesOnSameVessel)
        {
            return hasDockedVesselInfo
                && dockedPartUId != 0
                && partnerPartResolvesOnSameVessel;
        }

        // THERE IS NO IsDepotVesselType (P12, deleted). The operator's ruling: there is no
        // "base" type that matters - bases are ordinary vessels. A route candidate is defined
        // by TRANSFERS AND DOCKS, so no code decides anything on a VesselType; the type is
        // carried as an informational / logged / serialized field only. What replaced it is
        // the pair capture below plus ClassifyUndockOriginBinding.

        /// <summary>
        /// Pure / static. True when a vessel type can own cargo at all. The reject set is the
        /// design doc's own: "the start-docked vessel is a ghost/EVA/debris/invalid cargo
        /// owner -> route analysis rejects the candidate" (section 7). Anything that is not a
        /// craft type - <c>Debris</c>, <c>SpaceObject</c>, <c>Unknown</c>, <c>EVA</c>,
        /// <c>Flag</c>, the deployed-science and dropped-part types, and any unknown (-1) -
        /// fails.
        /// </summary>
        internal static bool IsValidCargoOwnerVesselType(int vesselType)
        {
            switch (vesselType)
            {
                case (int)VesselType.Probe:
                case (int)VesselType.Relay:
                case (int)VesselType.Rover:
                case (int)VesselType.Lander:
                case (int)VesselType.Ship:
                case (int)VesselType.Plane:
                case (int)VesselType.Station:
                case (int)VesselType.Base:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// One docking node on the partner part, reduced to the two fields the far-half
        /// lookup reads. Lets the multi-port decision be pinned headlessly - the live
        /// version takes a <c>Part</c>, which xUnit cannot construct.
        /// </summary>
        internal readonly struct SeamNodeRecord
        {
            public readonly bool HasVesselInfo;
            public readonly uint DockedPartUId;

            internal SeamNodeRecord(bool hasVesselInfo, uint dockedPartUId)
            {
                HasVesselInfo = hasVesselInfo;
                DockedPartUId = dockedPartUId;
            }
        }

        /// <summary>
        /// Pure / static. Index of the node on the partner part that points BACK at the
        /// seam part (<paramref name="facingFlightId"/>), or -1.
        ///
        /// <para>FAILS CLOSED, and that is the whole content of the helper. A part can
        /// carry several <c>ModuleDockingNode</c>s (multi-port adapters, shielded ports),
        /// so "the first node that has a <c>vesselInfo</c>" is, on such a part, ANOTHER
        /// seam's far half - and handing that to
        /// <see cref="SelectStartDockedOriginHalf"/> would let a third vessel's identity be
        /// selected as the depot. Only a node whose <c>dockedPartUId</c> names our own part
        /// describes THIS seam. -1 becomes HalfIdentityMissing and the seam contributes no
        /// candidate: for this producer "no proof" is always the right failure, and "a
        /// proof about the wrong craft" never is.</para>
        /// </summary>
        internal static int SelectFacingSeamNodeIndex(
            IReadOnlyList<SeamNodeRecord> partnerNodes,
            uint facingFlightId)
        {
            if (partnerNodes == null || facingFlightId == 0)
                return -1;
            for (int i = 0; i < partnerNodes.Count; i++)
            {
                if (!partnerNodes[i].HasVesselInfo) continue;
                if (partnerNodes[i].DockedPartUId != facingFlightId) continue;
                return i;
            }
            return -1;
        }

        /// <summary>
        /// THE PAIR ADMISSION RULE (P12, replacing the depot-typed selection). Pure / static:
        /// given the two halves of a settled dock seam as their own nodes recorded them,
        /// decide whether the PAIR can be captured. It does NOT decide which half is the
        /// origin - nothing at capture can, and the previous rule's attempt to
        /// (<c>vesselType == Base || Station</c>) was deleted with
        /// ROUTE-ORIGIN-PROOF-REQUIRES-A-PLAYER-TYPED-DEPOT.
        ///
        /// <para>Nothing here reads which half stock made dominant on the merge, which half
        /// keeps the <c>Vessel</c> across <c>Part.Undock</c>, or which node stock labelled
        /// docker / dockee - the last of those looks like an initiator record and is not one,
        /// because <c>ModuleDockingNode</c> dispatches it as
        /// <c>if (GetDominantVessel(base.vessel, otherNode.vessel) == base.vessel)
        /// otherNode.DockToVessel(this)</c>, i.e. the docker is simply the non-dominant half
        /// (decompiled KSP 1.12.5). Full derivation:
        /// <c>docs/dev/research/origin-proof-partner-identity-memo.md</c>.</para>
        ///
        /// Decision contract (in order):
        ///   1. either half missing its info, or a zero root part id -> HalfIdentityMissing
        ///   2. either half not a valid cargo owner                  -> InvalidCargoOwner
        ///   3. otherwise                                            -> Admitted
        /// </summary>
        internal static DockSeamPairAdmission ClassifyStartDockedSeamPair(
            DockSeamHalfIdentity near,
            DockSeamHalfIdentity far)
        {
            if (!near.HasInfo || !far.HasInfo || near.RootPartUId == 0 || far.RootPartUId == 0)
                return DockSeamPairAdmission.HalfIdentityMissing;

            if (!IsValidCargoOwnerVesselType(near.VesselType)
                || !IsValidCargoOwnerVesselType(far.VesselType))
            {
                return DockSeamPairAdmission.InvalidCargoOwner;
            }

            // Both halves are the same physical craft: a seam that names one identity twice
            // cannot be split into an origin and a transport at any later moment.
            if (near.RootPartUId == far.RootPartUId)
                return DockSeamPairAdmission.HalfIdentityMissing;

            return DockSeamPairAdmission.Admitted;
        }

        /// <summary>
        /// One part of the merged vessel reduced to what the seam split reads: its own
        /// <c>flightID</c>, its <c>persistentId</c>, and the INDEX of its parent in the same
        /// list (-1 for the root). Lets the split be pinned headlessly - the live version
        /// walks <c>Vessel.parts</c>, which xUnit cannot construct.
        /// </summary>
        internal readonly struct SeamPartRecord
        {
            public readonly uint FlightId;
            public readonly uint PersistentId;
            public readonly int ParentIndex;

            internal SeamPartRecord(uint flightId, uint persistentId, int parentIndex)
            {
                FlightId = flightId;
                PersistentId = persistentId;
                ParentIndex = parentIndex;
            }
        }

        /// <summary>
        /// Pure / static. Splits the merged vessel's part set into the two halves of ONE dock
        /// seam by CUTTING THE SEAM EDGE and taking the two connected components: the
        /// component containing <paramref name="seamPartFlightId"/> is the near side, the one
        /// containing <paramref name="partnerPartFlightId"/> is the far side.
        ///
        /// <para>THIS IS HOW A HALF'S PART SET IS KNOWN WHILE BOTH HALVES ARE ONE VESSEL, and
        /// it is what makes the transport manifests transport-scoped at the bind
        /// (ROUTE-ORIGIN-PROOF-TRANSPORT-MANIFESTS-INCLUDE-THE-DEPOT). A settled
        /// <c>Part.Couple</c> attaches the docked subtree under the seam, so the merged part
        /// list is ONE tree and the seam is one parent/child edge in it; removing that edge
        /// yields exactly two components.</para>
        ///
        /// <para>FAILS CLOSED. Returns false - and no part sets - when either id is missing,
        /// when the two named parts are not parent and child of each other (so the "seam" is
        /// not an edge and the components are not the halves), or when a component walk does
        /// not account for every part. A wrong part set would silently mis-scope every
        /// manifest downstream, so "no split" is the only acceptable failure.</para>
        /// </summary>
        internal static bool TrySplitPartsAcrossSeam(
            IReadOnlyList<SeamPartRecord> parts,
            uint seamPartFlightId,
            uint partnerPartFlightId,
            out List<uint> nearSidePartPids,
            out List<uint> farSidePartPids)
        {
            nearSidePartPids = null;
            farSidePartPids = null;
            if (parts == null || parts.Count == 0) return false;
            if (seamPartFlightId == 0 || partnerPartFlightId == 0) return false;
            if (seamPartFlightId == partnerPartFlightId) return false;

            int seamIndex = -1;
            int partnerIndex = -1;
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].FlightId == seamPartFlightId) seamIndex = i;
                else if (parts[i].FlightId == partnerPartFlightId) partnerIndex = i;
            }
            if (seamIndex < 0 || partnerIndex < 0) return false;

            // The seam must be an actual parent/child edge, in either direction.
            bool seamIsChild = parts[seamIndex].ParentIndex == partnerIndex;
            bool partnerIsChild = parts[partnerIndex].ParentIndex == seamIndex;
            if (!seamIsChild && !partnerIsChild) return false;

            // Undirected adjacency, minus the seam edge.
            var adjacency = new List<int>[parts.Count];
            for (int i = 0; i < parts.Count; i++) adjacency[i] = new List<int>();
            for (int i = 0; i < parts.Count; i++)
            {
                int parent = parts[i].ParentIndex;
                if (parent < 0 || parent >= parts.Count) continue;
                if ((i == seamIndex && parent == partnerIndex)
                    || (i == partnerIndex && parent == seamIndex))
                {
                    continue; // the cut
                }
                adjacency[i].Add(parent);
                adjacency[parent].Add(i);
            }

            List<uint> near = CollectComponentPartPids(parts, adjacency, seamIndex, out int nearCount);
            List<uint> far = CollectComponentPartPids(parts, adjacency, partnerIndex, out int farCount);
            if (near == null || far == null) return false;
            // Every part must land in exactly one component, or the merged list was not one
            // tree and the two components are not the halves.
            if (nearCount + farCount != parts.Count) return false;

            nearSidePartPids = near;
            farSidePartPids = far;
            return true;
        }

        private static List<uint> CollectComponentPartPids(
            IReadOnlyList<SeamPartRecord> parts,
            List<int>[] adjacency,
            int startIndex,
            out int visitedCount)
        {
            visitedCount = 0;
            var seen = new bool[parts.Count];
            var stack = new Stack<int>();
            stack.Push(startIndex);
            seen[startIndex] = true;
            var pids = new List<uint>();
            while (stack.Count > 0)
            {
                int index = stack.Pop();
                visitedCount++;
                uint pid = parts[index].PersistentId;
                if (pid != 0 && !pids.Contains(pid))
                    pids.Add(pid);
                List<int> neighbours = adjacency[index];
                for (int i = 0; i < neighbours.Count; i++)
                {
                    int next = neighbours[i];
                    if (seen[next]) continue;
                    seen[next] = true;
                    stack.Push(next);
                }
            }
            pids.Sort();
            return pids;
        }

        /// <summary>
        /// Pure resolver: given the active vessel's situation/EVA flag and a list of admitted
        /// seam-pair candidates, decide whether exactly ONE start-docked PAIR exists. No KSP
        /// dependency; logging happens at the call site so callers can attach context (vessel
        /// name, recording id, etc.).
        ///
        /// <para>The identity key is the PAIR of root part uids, unordered: two seams between
        /// the same two craft (a two-port dock) are ONE pair, while two DIFFERENT partners on
        /// one merged stack are ambiguous - neither can be the single origin candidate, and
        /// which one the transport later leaves is not decidable from the pair alone.</para>
        ///
        /// Decision contract (in order):
        ///   1. activeVesselIsEva == true          -> NoExternalCoupling
        ///   2. activeVesselSituation == PRELAUNCH -> ActiveVesselPrelaunch
        ///   3. empty candidate list               -> NoExternalCoupling
        ///   4. distinct non-zero, non-PRELAUNCH pairs == 1 -> Captured (index out)
        ///      else all halves' root ids == 0     -> PartnerPidZero
        ///      else all valid candidates PRELAUNCH-> PartnerPrelaunch
        ///      else 2+ distinct valid pairs       -> PartnerAmbiguous
        ///
        /// <para>The PRELAUNCH-per-candidate branch is retained but is unreachable from the
        /// seam producer: a settled pair is ONE vessel, so a candidate's situation is the
        /// active vessel's, which step 2 already rejected. It stays because the resolver is
        /// pure and a future producer may feed candidates with independent situations.</para>
        /// </summary>
        internal static OriginProofDetection TryResolveStartDockedSeamPair(
            int activeVesselSituation,
            bool activeVesselIsEva,
            IReadOnlyList<DockSeamPairCandidate> candidates,
            out int chosenCandidateIndex)
        {
            chosenCandidateIndex = -1;

            if (activeVesselIsEva)
                return OriginProofDetection.NoExternalCoupling;

            if (activeVesselSituation == (int)Vessel.Situations.PRELAUNCH)
                return OriginProofDetection.ActiveVesselPrelaunch;

            if (candidates == null || candidates.Count == 0)
                return OriginProofDetection.NoExternalCoupling;

            bool sawAnyCandidate = false;
            bool sawAnyNonZeroRoots = false;
            bool sawAnyNonPrelaunchValid = false;
            // Distinct unordered {rootA, rootB} pairs, and the first candidate index of each.
            var distinctPairs = new List<string>();
            var firstIndexOfPair = new List<int>();

            for (int i = 0; i < candidates.Count; i++)
            {
                DockSeamPairCandidate c = candidates[i];
                sawAnyCandidate = true;
                if (c.Near.RootPartUId == 0 || c.Far.RootPartUId == 0)
                    continue;
                sawAnyNonZeroRoots = true;
                if (c.MergedVesselSituation == (int)Vessel.Situations.PRELAUNCH)
                    continue;
                sawAnyNonPrelaunchValid = true;
                string key = FormatUnorderedPairKey(c.Near.RootPartUId, c.Far.RootPartUId);
                if (!distinctPairs.Contains(key))
                {
                    distinctPairs.Add(key);
                    firstIndexOfPair.Add(i);
                }
            }

            if (!sawAnyCandidate)
                return OriginProofDetection.NoExternalCoupling;

            if (distinctPairs.Count == 1)
            {
                chosenCandidateIndex = firstIndexOfPair[0];
                return OriginProofDetection.Captured;
            }

            if (distinctPairs.Count >= 2)
                return OriginProofDetection.PartnerAmbiguous;

            if (!sawAnyNonZeroRoots)
                return OriginProofDetection.PartnerPidZero;

            if (!sawAnyNonPrelaunchValid)
                return OriginProofDetection.PartnerPrelaunch;

            // Defensive: theoretically unreachable, but keep a deterministic answer.
            return OriginProofDetection.NoExternalCoupling;
        }

        /// <summary>
        /// Order-independent key for one seam pair: the two root part uids, smaller first.
        /// Two nodes of the same two-port dock produce the same key.
        /// </summary>
        internal static string FormatUnorderedPairKey(uint rootA, uint rootB)
        {
            uint lo = rootA <= rootB ? rootA : rootB;
            uint hi = rootA <= rootB ? rootB : rootA;
            return lo.ToString(CultureInfo.InvariantCulture) + "|"
                + hi.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Logistics start-docked origin proof producer (pure / static).
        /// Handles the gloops-mode early-skip, null-snapshot warn-skip, the
        /// <see cref="TryResolveStartDockedOriginPartner"/> dispatch, and the per-branch
        /// log emission. Returns the populated <paramref name="proof"/> +
        /// <paramref name="transportPartPersistentIds"/> on the Captured branch and
        /// <c>null</c> on every benign-or-degenerate branch.
        ///
        /// <paramref name="vesselContext"/> is interpolated into log strings as
        /// <c>vessel='{vesselContext}'</c>. Production passes the live vessel name;
        /// tests pass <c>&lt;test&gt;</c>.
        ///
        /// <paramref name="recordingVesselId"/> is interpolated into log strings as
        /// <c>recId={recordingVesselId}</c>.
        ///
        /// Both <see cref="FlightRecorder.CaptureStartRouteOriginProofIfDocked"/> and the
        /// unit tests call this helper directly so the producer logic stays in one place.
        /// </summary>
        internal static void BuildStartRouteOriginProof(
            int activeVesselSituation,
            bool activeVesselIsEva,
            IReadOnlyList<DockSeamPairCandidate> candidates,
            int settledDockSeamsScanned,
            ConfigNode snapshot,
            bool isGloopsMode,
            string vesselContext,
            uint recordingVesselId,
            out RouteOriginProof proof,
            out List<uint> transportPartPersistentIds)
        {
            proof = null;
            transportPartPersistentIds = null;

            if (isGloopsMode)
            {
                ParsekLog.Verbose("Recorder",
                    $"RouteOriginProof skipped: gloops mode recId={recordingVesselId} vessel='{vesselContext}'");
                return;
            }
            if (snapshot == null)
            {
                ParsekLog.Warn("Recorder",
                    $"RouteOriginProof skipped: no last good snapshot recId={recordingVesselId} " +
                    $"vessel='{vesselContext}'");
                return;
            }

            int candidateCount = candidates?.Count ?? 0;
            OriginProofDetection outcome = TryResolveStartDockedSeamPair(
                activeVesselSituation,
                activeVesselIsEva,
                candidates ?? new List<DockSeamPairCandidate>(),
                out int chosenIndex);

            switch (outcome)
            {
                case OriginProofDetection.Captured:
                {
                    DockSeamPairCandidate c = candidates[chosenIndex];
                    // The MERGED part set stays the pre-bind scope: at capture neither half is
                    // the transport yet, so the pair's own manifest is the only honest
                    // baseline. The bind REPLACES both with the transport half's own
                    // (ROUTE-ORIGIN-PROOF-TRANSPORT-MANIFESTS-INCLUDE-THE-DEPOT), and an
                    // unbound proof is never an origin, so the merged scope is never costed.
                    var mergedPids = VesselSpawner.CollectPartPersistentIds(snapshot);
                    Dictionary<string, ResourceAmount> startRes =
                        VesselSpawner.ExtractResourceManifest(snapshot, mergedPids);
                    List<InventoryPayloadItem> startInv =
                        VesselSpawner.ExtractInventoryPayloadItems(snapshot, mergedPids);

                    proof = new RouteOriginProof
                    {
                        // Origin identity slots stay EMPTY at capture: no rule at capture can
                        // say which half is the depot, and the previous one (vesselType ==
                        // Base / Station) was deleted with the operator's ruling. The undock
                        // binds them.
                        StartDockedOriginVesselPid = 0u,
                        StartDockedOriginRootPartUId = 0u,
                        StartDockedOriginBindState = StartDockedOriginBindState.PairPendingBinding,
                        StartDockedPair = new StartDockedSeamPair
                        {
                            HalfA = BuildSeamHalf(c.Near, c.NearPartPersistentIds, snapshot),
                            HalfB = BuildSeamHalf(c.Far, c.FarPartPersistentIds, snapshot),
                        },
                        StartTransportResources = startRes,
                        StartTransportInventory = startInv,
                        // The descriptor is the MERGED pair's, which is both halves' while
                        // docked - so it is already the origin's whichever half binds.
                        StartDockedOriginBodyName = c.MergedVesselBodyName,
                        StartDockedOriginLatitude = c.MergedVesselLatitude,
                        StartDockedOriginLongitude = c.MergedVesselLongitude,
                        StartDockedOriginAltitude = c.MergedVesselAltitude,
                        StartDockedOriginSituation = c.MergedVesselSituation,
                        StartDockedOriginIsSurface = IsSurfaceOriginSituation(c.MergedVesselSituation),
                    };
                    transportPartPersistentIds = mergedPids;

                    StartDockedSeamHalf halfA = proof.StartDockedPair.HalfA;
                    StartDockedSeamHalf halfB = proof.StartDockedPair.HalfB;
                    ParsekLog.Info("Recorder",
                        $"RouteOriginProof pair captured: recId={recordingVesselId} vessel='{vesselContext}' " +
                        $"halfARoot={halfA.RootPartUId.ToString(CultureInfo.InvariantCulture)} " +
                        $"halfAName='{halfA.VesselName ?? "<none>"}' " +
                        $"halfAType={halfA.VesselType.ToString(CultureInfo.InvariantCulture)} " +
                        $"halfAParts={halfA.PartPersistentIds?.Count ?? 0} " +
                        $"halfARes={halfA.StartResources?.Count ?? 0} " +
                        $"halfBRoot={halfB.RootPartUId.ToString(CultureInfo.InvariantCulture)} " +
                        $"halfBName='{halfB.VesselName ?? "<none>"}' " +
                        $"halfBType={halfB.VesselType.ToString(CultureInfo.InvariantCulture)} " +
                        $"halfBParts={halfB.PartPersistentIds?.Count ?? 0} " +
                        $"halfBRes={halfB.StartResources?.Count ?? 0} " +
                        $"bindState={proof.StartDockedOriginBindState} " +
                        $"candidates={candidateCount} " +
                        $"mergedParts={mergedPids?.Count ?? 0} " +
                        $"startRes={startRes?.Count ?? 0} startInv={startInv?.Count ?? 0} " +
                        $"partnerBody={(string.IsNullOrEmpty(proof.StartDockedOriginBodyName) ? "<none>" : proof.StartDockedOriginBodyName)} " +
                        $"partnerSituation={proof.StartDockedOriginSituation.ToString(CultureInfo.InvariantCulture)} " +
                        $"surface={(proof.StartDockedOriginIsSurface ? "1" : "0")}");
                    break;
                }
                case OriginProofDetection.NoExternalCoupling:
                    // INFO, NOT VERBOSE, and only on this branch. A recording that STARTED on
                    // a settled docked pair and still captured nothing is the one case a
                    // player can act on. Since P12 deleted the vessel-type requirement the
                    // remaining causes are structural: a half whose docking node carried no
                    // vesselInfo or a zero root id, or a half that is debris / EVA / a flag /
                    // a space object and cannot own cargo at all. It is a one-shot at
                    // recording start, i.e. an event, and it is silent on the ordinary
                    // undocked start.
                    //
                    // IT KEYS ON THE SCANNED SEAM COUNT, NOT ON candidates.Count. A seam
                    // rejected by the admission rule adds NO candidate (the producer loop
                    // skips it), so an accepted-list test can never be true on the very case
                    // the message exists for: every rejected start arrives here with
                    // candidates.Count == 0 and settledDockSeamsScanned > 0.
                    if (settledDockSeamsScanned > 0)
                    {
                        ParsekLog.Info("Recorder",
                            $"RouteOriginProof skipped: unusable seam pair recId={recordingVesselId} " +
                            $"vessel='{vesselContext}' seams={settledDockSeamsScanned.ToString(CultureInfo.InvariantCulture)} " +
                            $"candidates={candidateCount} " +
                            $"isEva={activeVesselIsEva} " +
                            $"(a docked half carried no usable identity or cannot own cargo, " +
                            $"so no supply origin was recorded)");
                    }
                    else
                    {
                        ParsekLog.Verbose("Recorder",
                            $"RouteOriginProof skipped: no external coupling recId={recordingVesselId} " +
                            $"vessel='{vesselContext}' candidates={candidateCount} isEva={activeVesselIsEva}");
                    }
                    break;
                case OriginProofDetection.ActiveVesselPrelaunch:
                    ParsekLog.Verbose("Recorder",
                        $"RouteOriginProof skipped: active vessel PRELAUNCH recId={recordingVesselId} " +
                        $"vessel='{vesselContext}' candidates={candidateCount}");
                    break;
                case OriginProofDetection.PartnerPrelaunch:
                    ParsekLog.Verbose("Recorder",
                        $"RouteOriginProof skipped: partner PRELAUNCH recId={recordingVesselId} " +
                        $"vessel='{vesselContext}' candidates={candidateCount}");
                    break;
                case OriginProofDetection.PartnerPidZero:
                    ParsekLog.Warn("Recorder",
                        $"RouteOriginProof skipped: partner root=0 recId={recordingVesselId} " +
                        $"vessel='{vesselContext}' candidates={candidateCount}");
                    break;
                case OriginProofDetection.PartnerAmbiguous:
                {
                    var distinctPairs = new List<string>();
                    if (candidates != null)
                    {
                        for (int i = 0; i < candidates.Count; i++)
                        {
                            DockSeamPairCandidate amb = candidates[i];
                            if (amb.Near.RootPartUId == 0 || amb.Far.RootPartUId == 0) continue;
                            if (amb.MergedVesselSituation == (int)Vessel.Situations.PRELAUNCH) continue;
                            string key = FormatUnorderedPairKey(amb.Near.RootPartUId, amb.Far.RootPartUId);
                            if (!distinctPairs.Contains(key)) distinctPairs.Add(key);
                        }
                    }
                    ParsekLog.Warn("Recorder",
                        $"RouteOriginProof skipped: ambiguous partners recId={recordingVesselId} " +
                        $"vessel='{vesselContext}' candidates={candidateCount} " +
                        $"distinctPairs=[{string.Join(",", distinctPairs.ToArray())}]");
                    break;
                }
            }
        }

        /// <summary>
        /// Lifts one captured seam half into its persisted identity plus its transient
        /// working data: the half's own part set on the merged vessel and the manifests
        /// scoped to it. A null / empty part set leaves the manifests null, which the bind
        /// reads as "this half's start manifest is unknown" (the pickup then falls to the
        /// carried branch rather than inventing a delta).
        /// </summary>
        private static StartDockedSeamHalf BuildSeamHalf(
            DockSeamHalfIdentity identity,
            List<uint> partPersistentIds,
            ConfigNode snapshot)
        {
            var half = new StartDockedSeamHalf
            {
                RootPartUId = identity.RootPartUId,
                VesselName = identity.VesselName,
                VesselType = identity.VesselType,
                PartPersistentIds = partPersistentIds != null && partPersistentIds.Count > 0
                    ? new List<uint>(partPersistentIds)
                    : null,
            };
            if (half.PartPersistentIds != null && snapshot != null)
            {
                half.StartResources =
                    VesselSpawner.ExtractResourceManifest(snapshot, half.PartPersistentIds);
                // MEASURED-AND-EMPTY IS NOT THE SAME AS NEVER-MEASURED, and the extractor
                // cannot say which it means: ExtractInventoryPayloadItems returns null both
                // when it found no items and when there was nothing to look at. The pickup
                // rule needs the distinction - a null baseline must not yield a gain (it has
                // nothing to compare against), while a half that really carried no items at
                // the start MUST let a container arriving later read as one. This half was
                // measured, so its baseline is explicit even when it is empty.
                half.StartInventory =
                    VesselSpawner.ExtractInventoryPayloadItems(snapshot, half.PartPersistentIds)
                    ?? new List<InventoryPayloadItem>();
            }
            return half;
        }

        // ===================================================================
        // THE UNDOCK BIND (P12). Capture defers the origin choice; this is where it is made.
        // ===================================================================

        /// <summary>
        /// THE FOCUS READING of the binding, pure / static. At the undock the pair separates
        /// into an ACTIVE side (the half KSP gave focus to) and a BACKGROUND side, matched by
        /// PART SET rather than by vessel pid: a <c>persistentId</c> is craft-baked and never
        /// proves physical identity, while the merged part persistentIds captured per half at
        /// recording start are exactly the parts each side is made of.
        ///
        /// <para>THIS IS NO LONGER THE WHOLE RULE, and the demotion is the fix for
        /// ROUTE-ORIGIN-PROOF-BIND-FOLLOWS-FOCUS-NOT-THE-RUN. Focus is only a PROXY for "which
        /// half is the run's transport", and on the operator's 2026-09-03 relay it was simply
        /// wrong: rover C drove to rover B, docked, TOOK 154.4 LiquidFuel off B and undocked,
        /// but B was the dominant half of the merge and KSP still held focus on B one frame
        /// after the split, so this classifier named C - the transport itself - as its own
        /// supply origin. <see cref="ResolveTransportHalfAtUndock"/> now consults this reading
        /// LAST, behind two ACTION-derived signals (operator ruling 2026-09-02: a route comes
        /// from what the vessels DID, not from what they are or which one the camera
        /// followed).</para>
        ///
        /// <para>Its REFUSALS stay terminal, and deliberately so: they are shape statements
        /// about the SPLIT ("this seam did not separate the pair"), not identity guesses, and
        /// no cargo reading may overrule them.</para>
        ///
        /// <para>Part-set OVERLAP, not equality: EVA construction or a mid-window decoupler
        /// can add or drop parts on either side during the docked span, and a strict equality
        /// test would refuse to bind an otherwise clean run. Overlap with BOTH halves on the
        /// active side is the one shape that must not bind - it means this split did not
        /// separate the pair.</para>
        ///
        /// Decision contract (in order):
        ///   1. no pending pair (or a half without a part set)   -> NoPairPending
        ///   2. the active side overlaps BOTH halves             -> BothHalvesActive
        ///   3. the active side overlaps NEITHER half            -> ActiveHalfUnresolved
        ///   4. the background side does not overlap the OTHER half -> UnrelatedSeam
        ///   5. active overlaps A -> BoundToHalfB; active overlaps B -> BoundToHalfA
        /// </summary>
        internal static OriginUndockBinding ClassifyUndockOriginBinding(
            IReadOnlyList<uint> halfAPartPids,
            IReadOnlyList<uint> halfBPartPids,
            IReadOnlyList<uint> activeSidePartPids,
            IReadOnlyList<uint> backgroundSidePartPids)
        {
            if (halfAPartPids == null || halfAPartPids.Count == 0
                || halfBPartPids == null || halfBPartPids.Count == 0)
            {
                return OriginUndockBinding.NoPairPending;
            }
            if (activeSidePartPids == null || activeSidePartPids.Count == 0)
                return OriginUndockBinding.ActiveHalfUnresolved;

            bool activeInA = SetsOverlap(activeSidePartPids, halfAPartPids);
            bool activeInB = SetsOverlap(activeSidePartPids, halfBPartPids);

            if (activeInA && activeInB)
                return OriginUndockBinding.BothHalvesActive;
            if (!activeInA && !activeInB)
                return OriginUndockBinding.ActiveHalfUnresolved;

            bool backgroundInA = SetsOverlap(backgroundSidePartPids, halfAPartPids);
            bool backgroundInB = SetsOverlap(backgroundSidePartPids, halfBPartPids);

            if (activeInA)
            {
                // The origin half is B; the background side must actually be it.
                return backgroundInB
                    ? OriginUndockBinding.BoundToHalfB
                    : OriginUndockBinding.UnrelatedSeam;
            }
            return backgroundInA
                ? OriginUndockBinding.BoundToHalfA
                : OriginUndockBinding.UnrelatedSeam;
        }

        /// <summary>
        /// Pure / static. True when <paramref name="window"/> is a WITNESSED DOCK of exactly
        /// this seam pair: its own transport part set overlaps <paramref name="halfPartPids"/>
        /// and its endpoint part set overlaps <paramref name="otherHalfPartPids"/>. Overlap,
        /// not equality, for the same reason <see cref="ClassifyUndockOriginBinding"/> uses
        /// overlap - parts move across a docked span.
        ///
        /// <para>WHY THE WINDOW IS THE AUTHORITY ON WHICH HALF IS THE TRANSPORT. A connection
        /// window is opened at the dock by <see cref="BuildDockRouteConnectionWindow"/> from
        /// the RECORDING'S OWN pre-couple part set - the run's subject - and the partner's.
        /// It therefore names the transport from the run rather than from focus, and it is
        /// already right on both hops of the operator's relay where the focus reading is
        /// wrong on the first.</para>
        /// </summary>
        internal static bool WindowNamesHalfAsTransport(
            RouteConnectionWindow window,
            IReadOnlyList<uint> halfPartPids,
            IReadOnlyList<uint> otherHalfPartPids)
        {
            return window != null
                && SetsOverlap(window.TransportPartPersistentIds, halfPartPids)
                && SetsOverlap(window.EndpointPartPersistentIds, otherHalfPartPids);
        }

        /// <summary>
        /// Pure / static. Scans a recording's connection windows for one that WITNESSED this
        /// seam pair's dock, and reports which half it names as the transport.
        ///
        /// <para>Returns true as soon as ANY window straddles the pair - that is the
        /// "the dock was witnessed inside this recording" fact
        /// <see cref="ClassifyOriginBindGate"/> needs, and it holds even when two windows
        /// disagree about the direction (a dock, an undock and a re-dock of the same pair on
        /// one recording). A disagreement sets BOTH out flags, which
        /// <see cref="ResolveTransportHalfAtUndock"/> reads as "no window verdict" and falls
        /// through to the next signal - it never guesses between them.</para>
        /// </summary>
        internal static bool TryResolveWitnessedDockTransportHalf(
            IReadOnlyList<RouteConnectionWindow> windows,
            IReadOnlyList<uint> halfAPartPids,
            IReadOnlyList<uint> halfBPartPids,
            out bool halfAIsTransport,
            out bool halfBIsTransport)
        {
            halfAIsTransport = false;
            halfBIsTransport = false;
            if (windows == null || windows.Count == 0)
                return false;

            bool witnessed = false;
            for (int i = 0; i < windows.Count; i++)
            {
                RouteConnectionWindow w = windows[i];
                if (WindowNamesHalfAsTransport(w, halfAPartPids, halfBPartPids))
                {
                    halfAIsTransport = true;
                    witnessed = true;
                }
                if (WindowNamesHalfAsTransport(w, halfBPartPids, halfAPartPids))
                {
                    halfBIsTransport = true;
                    witnessed = true;
                }
            }
            return witnessed;
        }

        /// <summary>
        /// Which evidence named the transport half at an undock bind. Stamped into the bind
        /// line as <c>transportSignal=</c> so a reader can tell a run-derived answer from the
        /// focus fallback without re-deriving anything.
        /// </summary>
        internal enum SeamTransportHalfSignal
        {
            /// <summary>A connection window on this recording witnessed the pair's dock and named its transport.</summary>
            WitnessedDockWindow = 0,
            /// <summary>
            /// No witnessed dock, and the docked-span cargo flow CORROBORATES the focus
            /// reading: the half focus called the transport is the half whose cargo rose.
            /// </summary>
            DockedSpanGain = 1,
            /// <summary>Neither action signal resolved: the half KSP gave focus to after the split.</summary>
            PostSplitActiveSide = 2,
            /// <summary>
            /// No witnessed dock, and the docked-span cargo flow CONTRADICTS the focus
            /// reading. The binding is left on focus and the bind is REFUSED - see
            /// <see cref="SeamTransportHalfDecision.FlowContradictsFocus"/>.
            /// </summary>
            DockedSpanGainContradictsFocus = 3,
        }

        /// <summary>
        /// Outcome of <see cref="ResolveTransportHalfAtUndock"/>: the binding to use, which
        /// evidence produced it, and how that evidence relates to the focus reading (the live
        /// caller logs both disagreement flags naming both halves: Info when the window
        /// settled it, Warn when nothing did and the bind is refused).
        /// </summary>
        internal sealed class SeamTransportHalfDecision
        {
            public OriginUndockBinding Binding;
            public SeamTransportHalfSignal Signal;
            /// <summary>True when the witnessed dock window re-pointed the binding away from focus.</summary>
            public bool OverrodeActiveSide;
            /// <summary>
            /// True when the ONLY action evidence available (the docked-span cargo flow, with
            /// no window to arbitrate) says the other half was the taker. The binding is left
            /// where focus put it and <see cref="ClassifyOriginBindGate"/> refuses.
            /// </summary>
            public bool FlowContradictsFocus;
        }

        /// <summary>
        /// THE BINDING RULE since the 2026-09-02 operator ruling, pure / static. A route
        /// candidate is made of ACTIONS - "if a vessel docked, took fuel or cargo FROM another
        /// vessel, undocked, went to a second vessel, docked, transferred fuel TO it,
        /// undocked - that is a potential valid route" - so the transport half is the half the
        /// RUN was flying, and the origin is the other one.
        ///
        /// <list type="number">
        /// <item><b>The witnessed dock window OUTRANKS EVERYTHING.</b> When a connection window
        /// on this recording straddles the pair it already names the transport, derived at the
        /// dock from the recording's OWN pre-couple part set - before the merge could rename
        /// anything and independently of where the camera ended up. This is the signal that
        /// fixes the operator's hop 1.</item>
        /// <item><b>Otherwise the post-split active side</b>
        /// (<see cref="ClassifyUndockOriginBinding"/>), with the docked-span cargo flow as a
        /// CROSS-CHECK ONLY.</item>
        /// </list>
        ///
        /// <para>WHY THE CARGO FLOW CROSS-CHECKS RATHER THAN DECIDES, which is the one thing
        /// in this file it is easiest to get backwards. "The half that gained is the transport"
        /// is FALSE in general: on a DELIVERY the ENDPOINT gains, so a start-docked run that
        /// hands fuel to its depot and leaves would have the DEPOT named as the transport and
        /// the run itself stamped as the origin - the very inversion this package exists to
        /// remove, reintroduced through the other door. And in the no-window case focus is not
        /// merely a proxy: the recorder FOLLOWS the focused vessel across a split
        /// (<c>ParsekFlight.DeferredUndockBranch</c>), so the active half IS the half this
        /// recording continues on. So when the flow contradicts focus the honest answer is
        /// that the two available signals disagree and neither is authoritative: the decision
        /// carries <see cref="SeamTransportHalfDecision.FlowContradictsFocus"/>, the caller
        /// Warns naming both halves, and the bind is refused. A wrong origin is worse than a
        /// missing one - it moves which vessel a route DEBITS.</para>
        ///
        /// <para>FAIL-CLOSED ON A REFUSAL: when the focus reading is not a binding at all
        /// (<see cref="OriginUndockBinding.BothHalvesActive"/> and friends) it is returned
        /// unchanged. Those are statements about the SPLIT - the same-craft identical-part-set
        /// case among them - and a cargo delta cannot make an unseparated pair separate.</para>
        /// </summary>
        internal static SeamTransportHalfDecision ResolveTransportHalfAtUndock(
            OriginUndockBinding activeSideBinding,
            bool windowNamesHalfATransport,
            bool windowNamesHalfBTransport,
            bool halfAGained,
            bool halfBGained)
        {
            var decision = new SeamTransportHalfDecision
            {
                Binding = activeSideBinding,
                Signal = SeamTransportHalfSignal.PostSplitActiveSide,
                OverrodeActiveSide = false,
                FlowContradictsFocus = false
            };

            if (activeSideBinding != OriginUndockBinding.BoundToHalfA
                && activeSideBinding != OriginUndockBinding.BoundToHalfB)
            {
                return decision;
            }

            // The origin is the half the transport is NOT: transport A -> BoundToHalfB.
            if (windowNamesHalfATransport ^ windowNamesHalfBTransport)
            {
                OriginUndockBinding resolved = windowNamesHalfATransport
                    ? OriginUndockBinding.BoundToHalfB
                    : OriginUndockBinding.BoundToHalfA;
                decision.Binding = resolved;
                decision.Signal = SeamTransportHalfSignal.WitnessedDockWindow;
                decision.OverrodeActiveSide = resolved != activeSideBinding;
                return decision;
            }

            if (halfAGained ^ halfBGained)
            {
                // Focus says the transport is the half the ORIGIN is not.
                bool focusTransportIsA = activeSideBinding == OriginUndockBinding.BoundToHalfB;
                bool flowAgrees = focusTransportIsA ? halfAGained : halfBGained;
                decision.Signal = flowAgrees
                    ? SeamTransportHalfSignal.DockedSpanGain
                    : SeamTransportHalfSignal.DockedSpanGainContradictsFocus;
                decision.FlowContradictsFocus = !flowAgrees;
            }

            return decision;
        }

        /// <summary>
        /// Whether an undock may write an origin onto the proof, and if not, why.
        /// </summary>
        internal enum OriginBindGate
        {
            /// <summary>The transport half GAINED admitted cargo across the span. The validating bind.</summary>
            BindGain = 0,
            /// <summary>
            /// No dock was witnessed inside the recording (the start-docked family), so the
            /// load that put the cargo aboard happened before the recorder existed and cannot
            /// be measured. The pair is still real evidence and is stamped as it always was,
            /// UNVALIDATED - which is not an origin
            /// (<c>RouteAnalysisEngine.HasDockedOriginProof</c> refuses it).
            /// </summary>
            BindStartDockedCarried = 1,
            /// <summary>
            /// The dock WAS witnessed and the transport half did not gain: this seam is a
            /// DELIVERY (or a no-op re-dock). Under the ruling a vessel the transport only
            /// gave cargo TO is a destination, never an origin, so nothing is written at all.
            /// </summary>
            SkipDeliveryWindow = 2,
            /// <summary>The dock was witnessed but nothing was measured at the undock.</summary>
            SkipNoUndockManifest = 3,
            /// <summary>
            /// No dock was witnessed AND the docked-span cargo flow contradicts the focus
            /// reading: the two available signals disagree about which half was the run and
            /// neither is authoritative. Writing an origin here would be a coin flip on which
            /// vessel a route DEBITS, so nothing is written.
            /// </summary>
            SkipFlowContradictsFocus = 4,
        }

        /// <summary>
        /// THE BIND GATE, pure / static. Closes the second half of
        /// ROUTE-ORIGIN-PROOF-BIND-FOLLOWS-FOCUS-NOT-THE-RUN: on the operator's relay the
        /// binder stamped lander A - the vessel rover C had just DELIVERED 200 LiquidFuel to -
        /// as C's supply origin, on a window where the transport only LOST cargo.
        ///
        /// <para>The discriminator is whether the recording WITNESSED the dock. When it did,
        /// a real pickup would have been measured, so the absence of a gain is positive
        /// evidence that this partner supplied nothing and NOTHING is written. When it did not
        /// (the recording opened already docked), the load predates the recorder and the
        /// unvalidated <see cref="OriginPickupKind.Carried"/> stamp stays exactly as it was -
        /// it is the operator-facing discriminator between "left with nothing" and "left with
        /// cargo it already had", and it is not an origin either way.</para>
        ///
        /// <para><paramref name="flowContradictsFocus"/> is the third outcome, and it can only
        /// arise in the no-witnessed-dock case (see
        /// <see cref="ResolveTransportHalfAtUndock"/>): the cargo says the OTHER half was the
        /// taker, focus says this one, and nothing arbitrates. That refuses too. A Gain is
        /// checked first and the two cannot co-occur - a contradiction means the half being
        /// measured is exactly the half that did NOT gain.</para>
        /// </summary>
        internal static OriginBindGate ClassifyOriginBindGate(
            OriginPickupKind transportPickup,
            bool dockWitnessedInRecording,
            bool flowContradictsFocus = false)
        {
            if (transportPickup == OriginPickupKind.Gain)
                return OriginBindGate.BindGain;
            if (flowContradictsFocus)
                return OriginBindGate.SkipFlowContradictsFocus;
            if (!dockWitnessedInRecording)
                return OriginBindGate.BindStartDockedCarried;
            return transportPickup == OriginPickupKind.NoUndockManifest
                ? OriginBindGate.SkipNoUndockManifest
                : OriginBindGate.SkipDeliveryWindow;
        }

        /// <summary>
        /// Pure / static. True when the gate refuses to write anything onto the proof.
        /// </summary>
        internal static bool IsBindRefusal(OriginBindGate gate)
        {
            return gate == OriginBindGate.SkipDeliveryWindow
                || gate == OriginBindGate.SkipNoUndockManifest
                || gate == OriginBindGate.SkipFlowContradictsFocus;
        }

        private static bool SetsOverlap(IReadOnlyList<uint> a, IReadOnlyList<uint> b)
        {
            if (a == null || b == null || a.Count == 0 || b.Count == 0) return false;
            var set = new HashSet<uint>(b);
            for (int i = 0; i < a.Count; i++)
            {
                if (set.Contains(a[i])) return true;
            }
            return false;
        }

        /// <summary>
        /// Pure / static. Whether the origin half's LIVE vessel pid may be stamped onto the
        /// proof at the bind, GUID-GATED through the same identity surface every other
        /// live-vessel-vs-recording site uses (<see cref="VesselLaunchIdentity"/>).
        ///
        /// <para>The one thing this refuses is the self-origin: a candidate pid equal to the
        /// RECORDED vessel's, whose launch guids do not conclusively differ, is the recorded
        /// launch itself, and stamping it would make the run its own supply origin. A
        /// conclusive guid difference clears the same pid (a craft-baked
        /// <c>persistentId</c> is reused verbatim on every launch of a craft file, so an
        /// equal pid alone proves nothing). An unknown guid on either side degrades to
        /// pid-only, which is what every other site here does - and it is safe in this
        /// direction because the root part id, not the pid, is the identity the resolver
        /// tries first.</para>
        /// </summary>
        internal static OriginPidStampDecision DecideOriginPidStamp(
            uint originVesselPid,
            string originVesselGuid,
            uint recordedVesselPid,
            string recordedVesselGuid)
        {
            if (originVesselPid == 0)
                return OriginPidStampDecision.NoLiveVessel;

            bool guidKnownBothSides =
                !string.IsNullOrEmpty(originVesselGuid) && !string.IsNullOrEmpty(recordedVesselGuid);

            if (recordedVesselPid != 0 && originVesselPid == recordedVesselPid)
            {
                if (!VesselLaunchIdentity.GuidsConclusivelyDiffer(recordedVesselGuid, originVesselGuid))
                    return OriginPidStampDecision.RefusedSameLaunch;
            }

            return guidKnownBothSides
                ? OriginPidStampDecision.Stamped
                : OriginPidStampDecision.StampedGuidUnknown;
        }

        /// <summary>
        /// THE TRANSFER RULE, pure / static. Classifies how the pickup at the origin was
        /// witnessed, from the TRANSPORT HALF's own manifests at recording start and at the
        /// undock.
        ///
        /// <para>Design 19.2.2 item 2 defines the "Loaded" provenance as "a recorded
        /// connection window in which cargo FLOWED from another vessel ONTO the transport" -
        /// a FLOW test - and 19.2.1 makes origin CAUSAL: "what matters is the witnessed event
        /// that put each unit of cargo on the transport". Three readings, ONE of which
        /// validates:</para>
        /// <list type="bullet">
        /// <item><see cref="OriginPickupKind.Gain"/> - an admitted resource ROSE across the
        /// span. THE ONLY VALIDATING CLASS: it is the flow the doctrine names.</item>
        /// <item><see cref="OriginPickupKind.Carried"/> - no rise, but the transport leaves
        /// carrying admitted cargo. OBSERVED, NEVER VALIDATING. Nothing flowed at this seam,
        /// and admitting it would validate residual anything: a pure DELIVERY undock, where
        /// the transport cargo went DOWN, still leaves monopropellant or leftover fuel aboard
        /// and would read as a pickup at the vessel it just delivered to. Kept as a
        /// classification because it is the discriminator an operator needs in the log
        /// between "left with nothing" and "left with cargo it already had".</item>
        /// <item><see cref="OriginPickupKind.None"/> - the transport leaves empty.</item>
        /// </list>
        ///
        /// <para>ADMITTED SET: everything except the always-ignored environmental resources
        /// (ElectricCharge, IntakeAir - <see cref="Logistics.ResourceTransferability.IsAlwaysIgnored"/>).
        /// Deliberately NOT the full <c>IsRoutableResource</c> definition check: this is a
        /// REJECTION-direction gate, and that check's undefined-name exclusion is
        /// admission-direction only (design plan D2), so uninstalling a mod must not be able
        /// to turn a no-pickup run into a validated one.</para>
        ///
        /// <para>A null start manifest is not an error: the gain branch is simply
        /// unevaluable and the classification falls to carried / none. A null UNDOCK manifest
        /// is <see cref="OriginPickupKind.NoUndockManifest"/> - nothing was measured, so
        /// nothing is validated.</para>
        /// </summary>
        internal static OriginPickupKind ClassifyOriginPickup(
            Dictionary<string, ResourceAmount> startTransportResources,
            Dictionary<string, ResourceAmount> undockTransportResources,
            List<InventoryPayloadItem> startTransportInventory = null,
            List<InventoryPayloadItem> undockTransportInventory = null)
        {
            bool haveResources = undockTransportResources != null;
            bool haveInventory = undockTransportInventory != null;
            if (!haveResources && !haveInventory)
                return OriginPickupKind.NoUndockManifest;

            const double epsilon = 1e-6;
            bool sawCargo = false;

            if (haveResources)
            {
                foreach (KeyValuePair<string, ResourceAmount> kvp in undockTransportResources)
                {
                    if (Logistics.ResourceTransferability.IsAlwaysIgnored(kvp.Key)) continue;
                    if (kvp.Value.amount <= epsilon) continue;
                    sawCargo = true;

                    if (startTransportResources == null) continue;
                    double startAmount = startTransportResources.TryGetValue(kvp.Key, out ResourceAmount before)
                        ? before.amount
                        : 0.0;
                    if (kvp.Value.amount - startAmount > epsilon)
                        return OriginPickupKind.Gain;
                }
            }

            // INVENTORY COUNTS EXACTLY LIKE A RESOURCE (operator ruling, 2026-09-02): a route
            // candidate comes from ACTIONS - "docked, took fuel OR CARGO from it, undocked".
            // A transport that leaves a seam carrying a container it did not arrive with has
            // been loaded just as surely as one that leaves with more fuel, and the relay
            // oracle's first window moved both at once (+200 LiquidFuel AND a
            // DeployedCentralStation plus an evaChute).
            if (haveInventory)
            {
                Dictionary<string, int> startCounts = SumInventoryQuantities(startTransportInventory);
                foreach (KeyValuePair<string, int> kvp in SumInventoryQuantities(undockTransportInventory))
                {
                    if (kvp.Value <= 0) continue;
                    sawCargo = true;

                    // MIRRORS THE RESOURCE BRANCH ABOVE, and the omission was a fail-open.
                    // SumInventoryQuantities(null) is an EMPTY dict, not a missing one, so
                    // without this guard every item at the undock compared against a
                    // fabricated before=0 and read as a Gain. That is reachable: BuildSeamHalf
                    // leaves BOTH half manifests null when the recorder has no start snapshot,
                    // and the bind clones those nulls onto the proof. No baseline means no
                    // delta can be computed, so the item can only count as carried.
                    if (startTransportInventory == null) continue;
                    startCounts.TryGetValue(kvp.Key, out int before);
                    if (kvp.Value > before)
                        return OriginPickupKind.Gain;
                }
            }

            // A resource or item absent from the undock manifest entirely cannot have risen,
            // so the two walks above are complete.
            return sawCargo ? OriginPickupKind.Carried : OriginPickupKind.None;
        }

        /// <summary>
        /// Pure / static. Total quantity per inventory IDENTITY hash. Items with no identity
        /// are ignored: they cannot be matched across the two snapshots, so counting them
        /// could only invent a delta.
        /// </summary>
        internal static Dictionary<string, int> SumInventoryQuantities(
            List<InventoryPayloadItem> items)
        {
            var totals = new Dictionary<string, int>();
            if (items == null) return totals;
            for (int i = 0; i < items.Count; i++)
            {
                InventoryPayloadItem item = items[i];
                if (item == null || string.IsNullOrEmpty(item.IdentityHash)) continue;
                totals.TryGetValue(item.IdentityHash, out int running);
                totals[item.IdentityHash] = running + item.Quantity;
            }
            return totals;
        }

        /// <summary>
        /// Pure / static. The bind line's <c>pickupDelta=[...]</c> payload: the per-resource
        /// net change across the docked span followed by the net inventory ITEM count, e.g.
        /// <c>LiquidFuel=+200.0;inv:+2</c>.
        ///
        /// <para>WHITESPACE-FREE and <c>;</c>-separated, deliberately: the token is pinned by
        /// scenario regexes, and a space-separated payload cannot be pinned as one field. It
        /// is a SEPARATE formatter from <see cref="FormatRouteResourceDelta"/> (which keeps
        /// its space-separated shape for the route-window delta line) so neither consumer
        /// perturbs the other; a single-resource payload renders identically in both.</para>
        ///
        /// <para>The <c>inv:</c> term is emitted only when either side carried inventory at
        /// all, so a resource-only run's token is unchanged from before inventory was read.</para>
        /// </summary>
        internal static string FormatOriginPickupDelta(
            Dictionary<string, ResourceAmount> startTransportResources,
            Dictionary<string, ResourceAmount> undockTransportResources,
            List<InventoryPayloadItem> startTransportInventory,
            List<InventoryPayloadItem> undockTransportInventory)
        {
            var parts = new List<string>();
            Dictionary<string, double> delta =
                ResourceManifest.ComputeResourceDelta(startTransportResources, undockTransportResources);
            if (delta != null && delta.Count > 0)
            {
                var keys = new List<string>(delta.Keys);
                keys.Sort(StringComparer.Ordinal);
                for (int i = 0; i < keys.Count; i++)
                {
                    double d = delta[keys[i]];
                    parts.Add(string.Format(CultureInfo.InvariantCulture, "{0}={1}{2:F1}",
                        keys[i], d >= 0 ? "+" : string.Empty, d));
                }
            }

            bool anyInventory =
                (startTransportInventory != null && startTransportInventory.Count > 0)
                || (undockTransportInventory != null && undockTransportInventory.Count > 0);
            if (anyInventory)
            {
                int before = 0;
                foreach (KeyValuePair<string, int> kvp in SumInventoryQuantities(startTransportInventory))
                    before += kvp.Value;
                int after = 0;
                foreach (KeyValuePair<string, int> kvp in SumInventoryQuantities(undockTransportInventory))
                    after += kvp.Value;
                int net = after - before;
                parts.Add(string.Format(CultureInfo.InvariantCulture, "inv:{0}{1}",
                    net >= 0 ? "+" : string.Empty, net));
            }

            return parts.Count == 0 ? "(none)" : string.Join(";", parts.ToArray());
        }

        /// <summary>
        /// The pickup kinds that make a bound proof usable as an origin. ONLY
        /// <see cref="OriginPickupKind.Gain"/> (ruling, 2026-09-02, adversarial review F2).
        ///
        /// <para>WHY NOT <see cref="OriginPickupKind.Carried"/>, which an earlier draft
        /// admitted. Design 19.2.2 item 2 defines "Loaded" as "a recorded connection window
        /// in which cargo FLOWED from another vessel ONTO the transport" - a FLOW test - and
        /// 19.2.1 says origin is CAUSAL: "what matters is the witnessed event that put each
        /// unit of cargo on the transport". A transport that leaves a seam merely CARRYING
        /// something witnessed no flow at that seam. Worse, carried validates on residual
        /// anything: a pure DELIVERY undock, where the transport's cargo went DOWN, still
        /// leaves monopropellant / leftover fuel / ore aboard and would validate as a pickup.
        /// The transfer-defined model has one honest test: did the transport half's admitted
        /// cargo RISE across the docked span.</para>
        ///
        /// <para><see cref="OriginPickupKind.Carried"/> is KEPT as an observed classification
        /// - it is the discriminator in the bind log between "left with nothing" and "left
        /// with cargo it already had", which is exactly what an operator reading a
        /// non-validated proof needs to see - but it never validates.</para>
        ///
        /// <para>THE CASE THIS DELIBERATELY FAILS CLOSED ON: a run whose inflow happened
        /// BEFORE the recording started (dock, load, quicksave, reload, start recording,
        /// undock). Filed as ROUTE-ORIGIN-PROOF-PICKUP-PREDATING-THE-RECORDING with the fix
        /// shape; not built here because the evidence lives on a DIFFERENT recording in the
        /// tree and the lookup is neither cheap nor pure at this seam.</para>
        /// </summary>
        internal static bool IsPickupValidated(OriginPickupKind kind)
        {
            return kind == OriginPickupKind.Gain;
        }

        /// <summary>
        /// Binds a pending start-docked seam pair at an UNDOCK: chooses the TRANSPORT half
        /// from the run's own actions (<see cref="ResolveTransportHalfAtUndock"/>), re-scopes
        /// the transport manifests to it, decides whether this seam may name an origin at all
        /// (<see cref="ClassifyOriginBindGate"/>) and stamps the result onto
        /// <paramref name="proof"/>. Returns true when an origin was written.
        ///
        /// <para>Operates entirely on the DTO plus <c>ConfigNode</c> snapshots, so the whole
        /// decision - the half selection, the guid gate and the pickup rule - is drivable
        /// headlessly; the live caller (<c>ParsekFlight.CreateSplitBranch</c>) only supplies
        /// the two post-split snapshots, the recording's connection windows and the live
        /// pids/guids.</para>
        ///
        /// <para>WRITE-ONCE MEANS FIRST VALID BIND WINS. Only a bind that is
        /// PICKUP-VALIDATED is final. An unvalidated <see cref="OriginPickupKind.Carried"/>
        /// stamp is observability, not an origin, and must not lock the slot against a real
        /// pickup measured at a later seam - on the operator's relay a slot locked by the
        /// first unvalidated stamp would have kept the wrong answer forever.</para>
        ///
        /// <para>A binding that does not resolve leaves the proof PENDING, never
        /// half-written: an unbound proof carries no origin identity at all and
        /// <c>RouteAnalysisEngine.HasDockedOriginProof</c> refuses it, so failing to bind
        /// costs a route and can never cost a wrong debit.</para>
        ///
        /// <para>ONLY AN UNDOCK CALLS THIS. Non-undock separations - a joint break, a stack
        /// or radial decoupler - leave the pair PENDING by design; see the call site's own
        /// note in <c>ParsekFlight.CreateSplitBranch</c>.</para>
        ///
        /// <para>The two trailing parameters are OPTIONAL so the pre-fix call shape still
        /// compiles: with no background snapshot and no windows the function reduces to the
        /// start-docked family (no witnessed dock, no measurable second half), which is
        /// exactly the shape every headless caller before this fix was driving.</para>
        /// </summary>
        internal static bool TryBindStartDockedOriginAtUndock(
            RouteOriginProof proof,
            IReadOnlyList<uint> activeSidePartPids,
            IReadOnlyList<uint> backgroundSidePartPids,
            ConfigNode activeSideSnapshot,
            ConfigNode endScopeSnapshot,
            uint originLiveVesselPid,
            string originLiveVesselGuid,
            uint recordedVesselPid,
            string recordedVesselGuid,
            double undockUT,
            string recordingContext,
            ConfigNode backgroundSideSnapshot = null,
            IReadOnlyList<RouteConnectionWindow> recordingConnectionWindows = null,
            uint activeSideLiveVesselPid = 0u,
            string activeSideLiveVesselGuid = null)
        {
            string context = string.IsNullOrEmpty(recordingContext) ? "<none>" : recordingContext;
            string utToken = undockUT.ToString("R", CultureInfo.InvariantCulture);

            if (proof == null)
                return false;

            // WHICH STATES ARE STILL BINDABLE, and why UnboundAtStop is one of them.
            // ONLY BoundAtUndock is final - that is the write-once rule, and it is what stops
            // a later DELIVERY undock from moving the origin onto the delivery endpoint.
            //
            // UnboundAtStop MUST remain bindable. It is a CONCLUSION drawn at a recorder stop
            // ("this recording ended while still docked"), and on the live undock path a stop
            // runs FIRST - OnVesselsUndocking stops the recorder and defers the branch one
            // frame - so the stop always precedes the split that proves the conclusion wrong.
            // MEASURED on H57's 2026-09-02 flight: the stop stamped UnboundAtStop, the deep
            // clone in ApplyCapturedLogisticsMetadataToRecording carried it onto the parent
            // recording, and the bind one frame later refused with reason=NoPairPending -
            // every live bind killed by its own observability stamp. Treating the stamp as
            // advisory rather than terminal is what makes the binder independent of WHICH
            // stop path fired, instead of resting on every caller threading a flag correctly.
            //
            // AND ONLY A VALIDATED BoundAtUndock IS FINAL. An unvalidated Carried stamp is an
            // observation, not an origin - HasDockedOriginProof refuses it - so letting it
            // hold the write-once slot would trade a wrong answer for a missing one. On the
            // operator's relay the first seam stamped Carried and the real pickup was one
            // undock later; first-VALID-bind-wins is what lets the later seam still land.
            bool alreadyBound =
                proof.StartDockedOriginBindState == StartDockedOriginBindState.BoundAtUndock
                && proof.StartDockedOriginPickupValidated;
            bool pairUsable = proof.StartDockedPair != null
                && proof.StartDockedPair.HalfA != null
                && proof.StartDockedPair.HalfB != null;
            if (alreadyBound || !pairUsable)
            {
                ParsekLog.Verbose("Flight",
                    $"RouteOriginProof bind skipped: recording={context} ut={utToken} " +
                    $"reason={(alreadyBound ? "already-bound" : OriginUndockBinding.NoPairPending.ToString())} " +
                    $"bindState={proof.StartDockedOriginBindState}");
                return false;
            }
            bool recoveringFromStopStamp =
                proof.StartDockedOriginBindState == StartDockedOriginBindState.UnboundAtStop;

            StartDockedSeamHalf halfA = proof.StartDockedPair.HalfA;
            StartDockedSeamHalf halfB = proof.StartDockedPair.HalfB;
            OriginUndockBinding activeSideBinding = ClassifyUndockOriginBinding(
                halfA.PartPersistentIds,
                halfB.PartPersistentIds,
                activeSidePartPids,
                backgroundSidePartPids);

            if (activeSideBinding != OriginUndockBinding.BoundToHalfA
                && activeSideBinding != OriginUndockBinding.BoundToHalfB)
            {
                ParsekLog.Info("Flight",
                    $"RouteOriginProof unbound: recording={context} ut={utToken} reason={activeSideBinding} " +
                    $"halfAParts={halfA.PartPersistentIds?.Count ?? 0} " +
                    $"halfBParts={halfB.PartPersistentIds?.Count ?? 0} " +
                    $"activeParts={activeSidePartPids?.Count ?? 0} " +
                    $"backgroundParts={backgroundSidePartPids?.Count ?? 0} " +
                    $"(the proof stays pending and is NOT an origin)");
                return false;
            }

            // THE AUTHORITATIVE SIGNAL: did a connection window on this recording WITNESS this
            // pair's dock, and which half did it call its transport. Also the fact the bind
            // gate keys on - a witnessed dock that moved nothing aboard is a delivery.
            bool dockWitnessed = TryResolveWitnessedDockTransportHalf(
                recordingConnectionWindows,
                halfA.PartPersistentIds,
                halfB.PartPersistentIds,
                out bool windowNamesHalfATransport,
                out bool windowNamesHalfBTransport);

            // THE CROSS-CHECK: which half's own admitted cargo ROSE across the docked span.
            // Each half is measured against the snapshot that actually HOLDS it after the
            // split, which is why the background snapshot has to reach this far - on the
            // operator's relay the transport half was the BACKGROUND side, and reading it off
            // the active snapshot (all the pre-fix code could do) measured nothing.
            OriginPickupKind halfAPickup = MeasureHalfPickupAtUndock(
                halfA, activeSidePartPids, activeSideSnapshot,
                backgroundSidePartPids, backgroundSideSnapshot);
            OriginPickupKind halfBPickup = MeasureHalfPickupAtUndock(
                halfB, activeSidePartPids, activeSideSnapshot,
                backgroundSidePartPids, backgroundSideSnapshot);

            SeamTransportHalfDecision halfDecision = ResolveTransportHalfAtUndock(
                activeSideBinding,
                windowNamesHalfATransport,
                windowNamesHalfBTransport,
                halfAPickup == OriginPickupKind.Gain,
                halfBPickup == OriginPickupKind.Gain);
            OriginUndockBinding binding = halfDecision.Binding;

            bool originIsA = binding == OriginUndockBinding.BoundToHalfA;
            StartDockedSeamHalf originHalf = originIsA ? halfA : halfB;
            StartDockedSeamHalf transportHalf = originIsA ? halfB : halfA;

            if (halfDecision.OverrodeActiveSide || halfDecision.FlowContradictsFocus)
            {
                // NAME BOTH HALVES. The focus reading and the cargo reading disagree, and an
                // operator has to be able to see which vessel the camera was on and which one
                // the cargo says was taking delivery - whether the window settled it
                // (OverrodeActiveSide) or nothing did (FlowContradictsFocus, refused below).
                // LEVEL FOLLOWS THE OUTCOME: a window override is a designed, correct result -
                // the merge made the partner dominant, which KSP decides by vessel type and
                // mass, so it is ordinary on any pickup from a heavier depot - and a Warn there
                // would put an ordinary pickup on the WRN surface the log validator reads. Only
                // the unarbitrated disagreement, which refuses the bind, is a Warn.
                bool activeSaidOriginIsA = activeSideBinding == OriginUndockBinding.BoundToHalfA;
                StartDockedSeamHalf focusTransport = activeSaidOriginIsA ? halfB : halfA;
                StartDockedSeamHalf cargoTransport = halfDecision.FlowContradictsFocus
                    ? (activeSaidOriginIsA ? halfA : halfB)
                    : transportHalf;
                string overriddenLine =
                    $"RouteOriginProof transport half overridden: recording={context} ut={utToken} " +
                    $"signal={halfDecision.Signal} " +
                    $"resolved={(halfDecision.FlowContradictsFocus ? "refused" : "run")} " +
                    $"focusTransportRoot={focusTransport.RootPartUId.ToString(CultureInfo.InvariantCulture)} " +
                    $"focusTransportName='{focusTransport.VesselName ?? "<none>"}' " +
                    $"runTransportRoot={cargoTransport.RootPartUId.ToString(CultureInfo.InvariantCulture)} " +
                    $"runTransportName='{cargoTransport.VesselName ?? "<none>"}' " +
                    $"(the half KSP gave focus to is not the half the cargo says took delivery)";
                if (halfDecision.FlowContradictsFocus)
                    ParsekLog.Warn("Flight", overriddenLine);
                else
                    ParsekLog.Info("Flight", overriddenLine);
            }

            // TRANSPORT-SCOPED MANIFESTS (closes
            // ROUTE-ORIGIN-PROOF-TRANSPORT-MANIFESTS-INCLUDE-THE-DEPOT). The start manifests
            // become the transport half's own, captured at recording start by cutting the
            // seam edge in the merged part tree; the end manifests are re-extracted from the
            // same stop snapshot they always came from, now scoped to the same half, so both
            // ends of the run are measured on one part set.
            //
            // NOTHING IS WRITTEN ONTO THE PROOF UNTIL THE GATE BELOW ADMITS THE SEAM. The
            // measurement is done into locals first so a refused delivery window leaves the
            // proof exactly as it found it, rather than half-written with a re-scoped manifest
            // and no origin.
            List<uint> transportPids = transportHalf.PartPersistentIds;
            bool transportScoped = transportPids != null && transportPids.Count > 0;

            // WHICH SNAPSHOT HOLDS THE TRANSPORT after the split. Before this fix the undock
            // manifest was always read off the ACTIVE snapshot, which silently measured the
            // wrong vessel whenever the run's transport was the backgrounded half - the
            // operator's first relay hop exactly.
            ConfigNode transportSideSnapshot = SelectSnapshotForHalf(
                transportPids, activeSidePartPids, activeSideSnapshot,
                backgroundSidePartPids, backgroundSideSnapshot);

            Dictionary<string, ResourceAmount> startTransportResources = transportScoped
                ? RouteProofMetadata.CloneResourceManifest(transportHalf.StartResources)
                : proof.StartTransportResources;
            List<InventoryPayloadItem> startTransportInventory = transportScoped
                ? RouteProofMetadata.CloneInventoryPayloadItems(transportHalf.StartInventory)
                : proof.StartTransportInventory;

            // THE PICKUP, measured on the transport half at the undock - BOTH cargo kinds,
            // each scoped to the same seam-derived transport part set, so a run that took on
            // a cargo container rather than fuel is witnessed identically (operator ruling).
            bool canMeasure = transportSideSnapshot != null && transportScoped;
            Dictionary<string, ResourceAmount> undockTransportResources = canMeasure
                ? VesselSpawner.ExtractResourceManifest(transportSideSnapshot, transportPids)
                : null;
            List<InventoryPayloadItem> undockTransportInventory = canMeasure
                ? VesselSpawner.ExtractInventoryPayloadItems(transportSideSnapshot, transportPids)
                : null;
            // CLASSIFY AGAINST THE HALF'S OWN BASELINE, not the persisted field. The two
            // differ in exactly one way that matters: the half records an explicit empty list
            // when it was measured and found nothing, whereas the persisted field keeps the
            // codec's null-for-empty convention, and a null there would read as "no baseline"
            // and refuse a real pickup.
            List<InventoryPayloadItem> startInventoryBaseline = transportScoped
                ? transportHalf.StartInventory
                : proof.StartTransportInventory;
            OriginPickupKind pickup = ClassifyOriginPickup(
                startTransportResources, undockTransportResources,
                startInventoryBaseline, undockTransportInventory);
            string pickupDelta = FormatOriginPickupDelta(
                startTransportResources, undockTransportResources,
                startInventoryBaseline, undockTransportInventory);

            // THE GATE. A witnessed dock that moved nothing ONTO the transport is a delivery
            // or a no-op re-dock, and under the ruling neither is an origin; a cargo flow that
            // contradicts focus with no window to arbitrate is refused for want of evidence.
            OriginBindGate gate = ClassifyOriginBindGate(
                pickup, dockWitnessed, halfDecision.FlowContradictsFocus);
            if (IsBindRefusal(gate))
            {
                ParsekLog.Info("Flight",
                    $"RouteOriginProof bind skipped: recording={context} ut={utToken} " +
                    $"reason={gate} " +
                    $"bindState={proof.StartDockedOriginBindState} " +
                    $"candidateOriginRoot={originHalf.RootPartUId.ToString(CultureInfo.InvariantCulture)} " +
                    $"candidateOriginName='{originHalf.VesselName ?? "<none>"}' " +
                    $"transportRoot={transportHalf.RootPartUId.ToString(CultureInfo.InvariantCulture)} " +
                    $"transportSignal={halfDecision.Signal} " +
                    $"dockWitnessed={(dockWitnessed ? "1" : "0")} " +
                    $"pickup={pickup} pickupDelta=[{pickupDelta}] " +
                    $"(nothing came ABOARD the run's transport at this seam, so the partner is " +
                    $"not a supply origin and the proof stays as capture left it)");
                return false;
            }

            if (transportScoped)
            {
                proof.StartTransportResources = startTransportResources;
                proof.StartTransportInventory = startTransportInventory;
                if (endScopeSnapshot != null)
                {
                    proof.EndTransportResources =
                        VesselSpawner.ExtractResourceManifest(endScopeSnapshot, transportPids);
                    proof.EndTransportInventory =
                        VesselSpawner.ExtractInventoryPayloadItems(endScopeSnapshot, transportPids);
                }
            }

            // THE ORIGIN'S LIVE PID FOLLOWS THE ORIGIN HALF, not the background side. The two
            // used to be the same statement because the origin was DEFINED as the background
            // half; now that the run's actions choose the transport, the origin can land on
            // either side of the split and a background-only reading would stamp the
            // TRANSPORT's pid as the origin's. Falls back to the background reading whenever
            // the caller supplied no active-side identity, which is every pre-fix caller.
            uint resolvedOriginPid = originLiveVesselPid;
            string resolvedOriginGuid = originLiveVesselGuid;
            if (activeSideLiveVesselPid != 0u
                && !SetsOverlap(originHalf.PartPersistentIds, backgroundSidePartPids)
                && SetsOverlap(originHalf.PartPersistentIds, activeSidePartPids))
            {
                resolvedOriginPid = activeSideLiveVesselPid;
                resolvedOriginGuid = activeSideLiveVesselGuid;
            }

            OriginPidStampDecision pidDecision = DecideOriginPidStamp(
                resolvedOriginPid, resolvedOriginGuid, recordedVesselPid, recordedVesselGuid);

            proof.StartDockedOriginBindState = StartDockedOriginBindState.BoundAtUndock;
            proof.StartDockedOriginRootPartUId = originHalf.RootPartUId;
            proof.StartDockedOriginVesselName = originHalf.VesselName;
            proof.StartDockedOriginVesselType = originHalf.VesselType;
            proof.StartDockedTransportRootPartUId = transportHalf.RootPartUId;
            proof.StartDockedTransportVesselType = transportHalf.VesselType;
            proof.StartDockedOriginVesselPid =
                (pidDecision == OriginPidStampDecision.Stamped
                 || pidDecision == OriginPidStampDecision.StampedGuidUnknown)
                    ? resolvedOriginPid
                    : 0u;
            proof.StartDockedOriginPickupKind = pickup;
            proof.StartDockedOriginPickupValidated = IsPickupValidated(pickup);

            ParsekLog.Info("Flight",
                $"RouteOriginProof bound at undock: recording={context} ut={utToken} " +
                $"binding={binding} " +
                $"recoveredFromStopStamp={(recoveringFromStopStamp ? "1" : "0")} " +
                $"originHalf={(originIsA ? "A" : "B")} " +
                $"originRoot={originHalf.RootPartUId.ToString(CultureInfo.InvariantCulture)} " +
                $"originName='{originHalf.VesselName ?? "<none>"}' " +
                $"originType={originHalf.VesselType.ToString(CultureInfo.InvariantCulture)} " +
                $"originPid={proof.StartDockedOriginVesselPid.ToString(CultureInfo.InvariantCulture)} " +
                $"guidDecision={pidDecision} " +
                $"transportRoot={transportHalf.RootPartUId.ToString(CultureInfo.InvariantCulture)} " +
                $"transportParts={transportPids?.Count ?? 0} " +
                $"pickup={pickup} pickupValidated={(proof.StartDockedOriginPickupValidated ? "1" : "0")} " +
                $"pickupDelta=[{pickupDelta}] " +
                $"startRes={proof.StartTransportResources?.Count ?? 0} " +
                $"undockRes={undockTransportResources?.Count ?? 0} " +
                $"startInv={proof.StartTransportInventory?.Count ?? 0} " +
                $"undockInv={undockTransportInventory?.Count ?? 0} " +
                $"transportSignal={halfDecision.Signal} " +
                $"dockWitnessed={(dockWitnessed ? "1" : "0")} " +
                $"gate={gate}");

            if (!proof.StartDockedOriginPickupValidated)
            {
                ParsekLog.Info("Flight",
                    $"RouteOriginProof unbound: recording={context} ut={utToken} " +
                    $"reason=pickup-{pickup} " +
                    $"originRoot={originHalf.RootPartUId.ToString(CultureInfo.InvariantCulture)} " +
                    $"(the origin half is bound but nothing was picked up, so the proof is NOT " +
                    $"an origin for route analysis)");
            }

            return true;
        }

        /// <summary>
        /// Picks the post-split snapshot that actually contains a seam half's parts.
        ///
        /// <para>DEFAULTS TO THE ACTIVE SNAPSHOT, and diverts to the background one ONLY when
        /// a background snapshot was supplied AND the half is clearly not on the active side.
        /// That ordering keeps every caller that supplies no background snapshot byte-identical
        /// to the pre-fix reading, and makes the divert self-validating: a half that overlaps
        /// neither side (a fully decoupled seam) reads off the active snapshot exactly as
        /// before rather than silently becoming unmeasurable.</para>
        /// </summary>
        private static ConfigNode SelectSnapshotForHalf(
            IReadOnlyList<uint> halfPartPids,
            IReadOnlyList<uint> activeSidePartPids,
            ConfigNode activeSideSnapshot,
            IReadOnlyList<uint> backgroundSidePartPids,
            ConfigNode backgroundSideSnapshot)
        {
            if (backgroundSideSnapshot == null)
                return activeSideSnapshot;
            if (SetsOverlap(halfPartPids, activeSidePartPids))
                return activeSideSnapshot;
            return SetsOverlap(halfPartPids, backgroundSidePartPids)
                ? backgroundSideSnapshot
                : activeSideSnapshot;
        }

        /// <summary>
        /// Classifies ONE seam half's own cargo movement across the docked span, measured
        /// against whichever post-split snapshot holds it. Feeds the
        /// <see cref="SeamTransportHalfSignal.DockedSpanGain"/> signal; a half that cannot be
        /// measured reads <see cref="OriginPickupKind.NoUndockManifest"/>, which is not a gain
        /// and therefore never re-points the binding.
        /// </summary>
        private static OriginPickupKind MeasureHalfPickupAtUndock(
            StartDockedSeamHalf half,
            IReadOnlyList<uint> activeSidePartPids,
            ConfigNode activeSideSnapshot,
            IReadOnlyList<uint> backgroundSidePartPids,
            ConfigNode backgroundSideSnapshot)
        {
            List<uint> pids = half?.PartPersistentIds;
            if (pids == null || pids.Count == 0)
                return OriginPickupKind.NoUndockManifest;

            ConfigNode snapshot = SelectSnapshotForHalf(
                pids, activeSidePartPids, activeSideSnapshot,
                backgroundSidePartPids, backgroundSideSnapshot);
            if (snapshot == null)
                return OriginPickupKind.NoUndockManifest;

            return ClassifyOriginPickup(
                half.StartResources,
                VesselSpawner.ExtractResourceManifest(snapshot, pids),
                half.StartInventory,
                VesselSpawner.ExtractInventoryPayloadItems(snapshot, pids));
        }

        /// <summary>
        /// Stamps <see cref="StartDockedOriginBindState.UnboundAtStop"/> on a proof whose pair
        /// has not separated, and says so once. Returns true when the state changed.
        ///
        /// <para>THE RULE: a recording that ends while still docked has witnessed no undock,
        /// so no half is the origin and no cargo movement was bracketed. The proof is kept
        /// (the pair is real evidence and the log line is the affordance) but it is never
        /// forwarded as an origin.</para>
        ///
        /// <para>THE STAMP IS ADVISORY, NOT TERMINAL, AND THAT IS LOAD-BEARING. It is written
        /// at a recorder STOP, and on the live undock path a stop runs BEFORE the split
        /// (OnVesselsUndocking stops the recorder, then defers the branch a frame), so the
        /// conclusion can be premature. <see cref="TryBindStartDockedOriginAtUndock"/>
        /// therefore still binds an <c>UnboundAtStop</c> proof; only
        /// <see cref="StartDockedOriginBindState.BoundAtUndock"/> is final. Callers should
        /// still pass <c>stopIsChainBoundary</c> correctly - it keeps the misleading line out
        /// of the log on the normal path - but correctness does not depend on it.</para>
        /// </summary>
        internal static bool MarkStartDockedOriginUnboundAtStop(
            RouteOriginProof proof,
            string recordingContext)
        {
            if (proof == null
                || proof.StartDockedPair == null
                || proof.StartDockedOriginBindState != StartDockedOriginBindState.PairPendingBinding)
            {
                return false;
            }

            proof.StartDockedOriginBindState = StartDockedOriginBindState.UnboundAtStop;
            StartDockedSeamHalf halfA = proof.StartDockedPair.HalfA;
            StartDockedSeamHalf halfB = proof.StartDockedPair.HalfB;
            ParsekLog.Info("Recorder",
                $"RouteOriginProof unbound: recording={(string.IsNullOrEmpty(recordingContext) ? "<none>" : recordingContext)} " +
                $"reason=stopped-while-docked " +
                $"halfARoot={(halfA != null ? halfA.RootPartUId.ToString(CultureInfo.InvariantCulture) : "0")} " +
                $"halfBRoot={(halfB != null ? halfB.RootPartUId.ToString(CultureInfo.InvariantCulture) : "0")} " +
                $"(the recording ended still docked, so no half is the supply origin)");
            return true;
        }

        /// <summary>
        /// Forwards a start-time <see cref="RouteOriginProof"/> onto a captured
        /// <see cref="Recording"/>, re-extracting the end transport manifests scoped to
        /// the same part-pid set captured at start. No-op when either input is null —
        /// callers do not need to guard.
        ///
        /// Both <see cref="FlightRecorder.BuildCaptureRecording"/> and the unit tests
        /// call this helper directly so the forwarding logic stays in one place. The
        /// v0 decoupled-parts contract note lives at the production callsite — see
        /// <c>FlightRecorder.BuildCaptureRecording</c>.
        /// </summary>
        internal static void AttachEndManifestsAndForwardToCapture(
            Recording capture,
            RouteOriginProof pendingProof,
            ICollection<uint> pendingStartPartPersistentIds,
            bool stopIsChainBoundary = false)
        {
            if (capture == null || pendingProof == null || pendingStartPartPersistentIds == null)
                return;

            pendingProof.EndTransportResources =
                VesselSpawner.ExtractResourceManifest(capture.VesselSnapshot, pendingStartPartPersistentIds);
            pendingProof.EndTransportInventory =
                VesselSpawner.ExtractInventoryPayloadItems(capture.VesselSnapshot, pendingStartPartPersistentIds);
            // STOPPED WHILE STILL DOCKED. A chain-boundary stop is the FIRST half of a split
            // (undock / dock / pid change) and the bind runs immediately after it, so only an
            // ordinary stop can conclude that the pair never separated. A chain-boundary stop
            // whose bind then does not resolve simply leaves the proof PENDING, which reads as
            // "not an origin" through the same gate - the stamp is observability, not the gate.
            if (!stopIsChainBoundary)
                MarkStartDockedOriginUnboundAtStop(pendingProof, capture.RecordingId);
            capture.RouteOriginProof = pendingProof;
            ParsekLog.Verbose("Recorder",
                $"BuildCaptureRecording: forwarded RouteOriginProof partner={pendingProof.StartDockedOriginVesselPid} " +
                $"bindState={pendingProof.StartDockedOriginBindState} " +
                $"originRoot={pendingProof.StartDockedOriginRootPartUId.ToString(CultureInfo.InvariantCulture)} " +
                $"startRes={pendingProof.StartTransportResources?.Count ?? 0} " +
                $"endRes={pendingProof.EndTransportResources?.Count ?? 0} " +
                $"startInv={pendingProof.StartTransportInventory?.Count ?? 0} " +
                $"endInv={pendingProof.EndTransportInventory?.Count ?? 0}");
        }

        /// <summary>
        /// M2 / plan D3 birth discriminator (round-2 BLOCKER 1): the run-manifest
        /// START half is captured iff the tree's active Recording is at its BIRTH
        /// (no prior samples of any kind) AND carries no start half yet (the
        /// write-once guard). The flavor of the recorder start (isPromotion or
        /// not) is deliberately NOT the discriminator - split children, merge
        /// children, and chain-segment births all start with isPromotion:true and
        /// MUST capture, while BG-promotion of an existing recording and quickload
        /// resume must NOT (a re-captured mid-run baseline would fold prior gains
        /// into "start cargo" and bypass the gain check).
        ///
        /// Pure / static / testable. Logging happens at the call site.
        /// </summary>
        internal static bool ShouldCaptureRunManifestStartHalf(Recording treeRecording, out string skipReason)
        {
            skipReason = null;
            if (treeRecording == null)
            {
                skipReason = "no-tree-recording";
                return false;
            }
            // Sticky void tombstone (M2 review follow-up): a leg that voided on
            // a background transition must NEVER re-capture, even when it still
            // looks "at birth" (the void can land before the first sample is
            // flushed onto the tree recording). Fail-closed to legacy.
            if (treeRecording.RunManifestVoided)
            {
                skipReason = "manifest-voided";
                return false;
            }
            if (treeRecording.RouteRunManifest != null && treeRecording.RouteRunManifest.HasStartHalf)
            {
                skipReason = "start-half-already-captured";
                return false;
            }
            bool hasPoints = treeRecording.Points != null && treeRecording.Points.Count > 0;
            bool hasOrbitSegments = treeRecording.OrbitSegments != null && treeRecording.OrbitSegments.Count > 0;
            bool hasTrackSections = treeRecording.TrackSections != null && treeRecording.TrackSections.Count > 0;
            if (hasPoints || hasOrbitSegments || hasTrackSections)
            {
                skipReason = "not-at-birth";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Builds the START half of a <see cref="RouteRunCargoManifest"/> from the
        /// recording-start vessel snapshot (M2 / plan D3). Scope = the snapshot's
        /// full part-pid set, identical to the start-docked origin proof's scope
        /// rule. Capture stays PERMISSIVE (plan D2): whatever resource names the
        /// snapshot carries are recorded; undefined-name exclusion happens at
        /// analysis only. Returns null (with a log mirroring the
        /// RouteOriginProof skip branches) for gloops mode, a missing snapshot,
        /// or a snapshot with no usable part pids.
        /// </summary>
        internal static RouteRunCargoManifest BuildRunCargoManifestAtStart(
            ConfigNode snapshot,
            bool isGloopsMode,
            string vesselContext,
            uint recordingVesselId)
        {
            if (isGloopsMode)
            {
                ParsekLog.Verbose("Recorder",
                    $"RouteRunManifest skipped: gloops mode recId={recordingVesselId} vessel='{vesselContext}'");
                return null;
            }
            if (snapshot == null)
            {
                ParsekLog.Warn("Recorder",
                    $"RouteRunManifest skipped: no last good snapshot recId={recordingVesselId} " +
                    $"vessel='{vesselContext}'");
                return null;
            }

            List<uint> transportPids = VesselSpawner.CollectPartPersistentIds(snapshot);
            if (transportPids == null || transportPids.Count == 0)
            {
                ParsekLog.Warn("Recorder",
                    $"RouteRunManifest skipped: snapshot has no part pids recId={recordingVesselId} " +
                    $"vessel='{vesselContext}'");
                return null;
            }

            Dictionary<string, ResourceAmount> startRes =
                VesselSpawner.ExtractResourceManifest(snapshot, transportPids);
            // Empty -> null normalization (M2 review follow-up): the codec
            // drops empty manifests on save (reload yields null) while the
            // hasher emits ".count=0" for an empty dict - an empty-but-non-null
            // manifest would therefore flip the hash after one save/load and
            // mark every route built from it SourceChanged.
            if (startRes != null && startRes.Count == 0)
                startRes = null;

            var manifest = new RouteRunCargoManifest
            {
                TransportPartPersistentIds = transportPids,
                StartTransportResources = startRes,
            };

            ParsekLog.Verbose("Recorder",
                $"RouteRunManifest start: recId={recordingVesselId} vessel='{vesselContext}' " +
                $"parts={transportPids.Count} res={startRes?.Count ?? 0}");
            return manifest;
        }

        /// <summary>
        /// Completes the END half of the pending run manifest at an ACTIVE stop
        /// and forwards a deep clone onto the captured Recording (M2 / plan D3
        /// rule 4). The END manifest is extracted from
        /// <c>capture.VesselSnapshot</c> scoped to the START pid set - NEVER from
        /// a live vessel walk at the stop frame (at the dock pid-change stop the
        /// live vessel is the merged stack and same-frame crossfeed equalization
        /// deflates values; mirrors
        /// <see cref="AttachEndManifestsAndForwardToCapture"/>). The END half is
        /// overwrite-per-active-stop: a chain-boundary stop abandoned by
        /// ResumeAfterFalseAlarm has already completed an END half, and the
        /// eventual real stop must replace it or post-resume drilling
        /// double-counts. No-op when either input is null, or when the capture
        /// carries NO vessel snapshot (M2 review follow-up): completing
        /// against a null snapshot would stamp EndCaptured with a null END that
        /// reads as "complete, resource-less" and inflates the next leg's
        /// bridge delta - leave the manifest start-only instead (degrades to
        /// legacy via the presence gate).
        /// </summary>
        internal static void CompleteRunCargoManifestAtStop(
            Recording capture,
            RouteRunCargoManifest pending)
        {
            if (capture == null || pending == null)
                return;

            if (capture.VesselSnapshot == null)
            {
                ParsekLog.Verbose("Recorder",
                    $"RouteRunManifest end skipped: no capture snapshot " +
                    $"recording={capture.RecordingId ?? "<none>"} (manifest stays start-only)");
                return;
            }

            bool overwrite = pending.EndCaptured;
            Dictionary<string, ResourceAmount> endRes = VesselSpawner.ExtractResourceManifest(
                capture.VesselSnapshot,
                pending.TransportPartPersistentIds);
            // Empty -> null normalization: same hash-stability contract as the
            // START half (the codec drops empty manifests on save).
            if (endRes != null && endRes.Count == 0)
                endRes = null;
            pending.EndTransportResources = endRes;
            pending.EndCaptured = true;
            capture.RouteRunManifest = pending.DeepClone();

            ParsekLog.Verbose("Recorder",
                $"RouteRunManifest end: recording={capture.RecordingId ?? "<none>"} " +
                $"startRes={pending.StartTransportResources?.Count ?? 0} " +
                $"endRes={pending.EndTransportResources?.Count ?? 0} " +
                $"overwrite={(overwrite ? "1" : "0")}");
        }

        /// <summary>
        /// Voids the active tree recording's run manifest on a background
        /// transition (M2 / plan D3 rule 3): the END half of a BG-transiting leg
        /// can never be captured trustworthily, and a voided manifest makes the
        /// analysis presence gate degrade that tree to legacy behavior. ALWAYS
        /// stamps the sticky <see cref="Recording.RunManifestVoided"/> tombstone
        /// (M2 review follow-up) - even when no manifest was captured yet -
        /// so a BG-transited leg that still looks "at birth" can never
        /// re-capture a mid-life START baseline on promotion. Returns true when
        /// a manifest was actually cleared. Warn-logged per the plan's logging
        /// table; tombstone-only marks log Verbose.
        /// </summary>
        internal static bool VoidRunManifestForBackgroundTransition(
            RecordingTree tree,
            string activeRecordingId)
        {
            if (tree == null || string.IsNullOrEmpty(activeRecordingId)
                || tree.Recordings == null
                || !tree.Recordings.TryGetValue(activeRecordingId, out Recording treeRec)
                || treeRec == null)
            {
                return false;
            }

            bool tombstoneNewlySet = !treeRec.RunManifestVoided;
            treeRec.RunManifestVoided = true;

            if (treeRec.RouteRunManifest == null)
            {
                if (tombstoneNewlySet)
                {
                    treeRec.MarkFilesDirty();
                    ParsekLog.Verbose("Recorder",
                        $"RouteRunManifest void tombstone set: recording={activeRecordingId} " +
                        $"reason=background-transition (no manifest captured yet)");
                }
                return false;
            }

            treeRec.RouteRunManifest = null;
            treeRec.MarkFilesDirty();
            ParsekLog.Warn("Recorder",
                $"RouteRunManifest voided: recording={activeRecordingId} reason=background-transition");
            return true;
        }

        internal static RouteConnectionWindow BuildDockRouteConnectionWindow(
            double dockUT,
            uint transferTargetVesselPid,
            RouteConnectionKind transferKind,
            ConfigNode dockedSnapshot,
            ICollection<uint> transportPartPersistentIds,
            ICollection<uint> endpointPartPersistentIds,
            RouteEndpoint? endpointAtDock,
            int transferEndpointSituation,
            ConfigNode endpointPreCoupleSnapshot = null,
            ConfigNode transportPreCoupleSnapshot = null)
        {
            if (transferTargetVesselPid == 0 || dockedSnapshot == null)
                return null;

            List<uint> transportPids = NormalizePartPids(transportPartPersistentIds);
            if (transportPids == null || transportPids.Count == 0)
                return null;

            List<uint> endpointPids = NormalizePartPids(endpointPartPersistentIds);
            if (endpointPids == null || endpointPids.Count == 0)
                endpointPids = DeriveEndpointPartPids(dockedSnapshot, transportPids);

            if (endpointPids == null || endpointPids.Count == 0)
                return null;

            if (!SnapshotContainsAnyPartPersistentId(dockedSnapshot, transportPids) ||
                !SnapshotContainsAnyPartPersistentId(dockedSnapshot, endpointPids))
            {
                ParsekLog.Warn("Flight",
                    $"Route window dock capture failed: docked snapshot does not contain " +
                    $"transport/endpoint part PID sets targetPid={transferTargetVesselPid} " +
                    $"transportParts={transportPids.Count} endpointParts={endpointPids.Count}");
                return null;
            }

            // When the caller provides a pre-couple endpoint snapshot, prefer it for the
            // endpoint baseline. Falling back to the merged-vessel snapshot would inflate
            // DOCK_ENDPOINT_RESOURCES with the transport's contribution because the
            // endpoint-part-PID set may include transport parts after a post-couple
            // FindVesselByPid lookup returned the merged vessel.
            ConfigNode endpointSnapshotForBaseline = endpointPreCoupleSnapshot != null
                ? endpointPreCoupleSnapshot
                : dockedSnapshot;

            // Symmetrically, when the caller provides a pre-couple TRANSPORT snapshot,
            // prefer it for the transport baseline. The merged-vessel snapshot is captured
            // frames after the couple, so any same-frame stock crossfeed equalisation that
            // drained the transport tank into the depot deflates DOCK_TRANSPORT_RESOURCES;
            // a later undock reading then looks like a pickup and trips the strict
            // MixedPickupDelivery gate on an otherwise clean delivery run. The selection is
            // self-validating: a pre-couple snapshot is only used when it actually contains
            // the transport part PID set, so a stale / mismatched snapshot can never produce
            // a wrong manifest (it falls back to the merged snapshot, current behaviour).
            ConfigNode transportSnapshotForBaseline =
                (transportPreCoupleSnapshot != null
                 && SnapshotContainsAnyPartPersistentId(transportPreCoupleSnapshot, transportPids))
                    ? transportPreCoupleSnapshot
                    : dockedSnapshot;

            var window = new RouteConnectionWindow
            {
                WindowId = BuildWindowId(dockUT, transferTargetVesselPid),
                DockUT = dockUT,
                TransferTargetVesselPid = transferTargetVesselPid,
                TransferKind = transferKind != RouteConnectionKind.None
                    ? transferKind
                    : RouteConnectionKind.DockingPort,
                TransportPartPersistentIds = transportPids,
                EndpointPartPersistentIds = endpointPids,
                DockTransportResources =
                    VesselSpawner.ExtractResourceManifest(transportSnapshotForBaseline, transportPids),
                DockEndpointResources =
                    VesselSpawner.ExtractResourceManifest(endpointSnapshotForBaseline, endpointPids),
                DockTransportInventory =
                    VesselSpawner.ExtractInventoryPayloadItems(transportSnapshotForBaseline, transportPids),
                DockEndpointInventory =
                    VesselSpawner.ExtractInventoryPayloadItems(endpointSnapshotForBaseline, endpointPids),
                EndpointAtDock = endpointAtDock,
                TransferEndpointSituation = transferEndpointSituation
            };

            ParsekLog.Verbose("Flight",
                $"Route window dock capture: window={window.WindowId} " +
                $"targetPid={transferTargetVesselPid} transportParts={transportPids.Count} " +
                $"endpointParts={endpointPids.Count} transportRes={window.DockTransportResources?.Count ?? 0} " +
                $"endpointRes={window.DockEndpointResources?.Count ?? 0} " +
                $"transportInv={window.DockTransportInventory?.Count ?? 0} " +
                $"endpointInv={window.DockEndpointInventory?.Count ?? 0}");

            return window;
        }

        internal static bool TryCompleteLatestRouteConnectionWindow(
            Recording recording,
            double undockUT,
            params ConfigNode[] undockSnapshots)
        {
            if (recording?.RouteConnectionWindows == null ||
                recording.RouteConnectionWindows.Count == 0)
            {
                ParsekLog.Verbose("Flight",
                    "Route window undock completion skipped: recording has no connection windows");
                return false;
            }

            for (int i = recording.RouteConnectionWindows.Count - 1; i >= 0; i--)
            {
                RouteConnectionWindow window = recording.RouteConnectionWindows[i];
                if (window == null || window.IsComplete)
                    continue;

                bool completed = CompleteRouteConnectionWindowAtUndock(
                    window,
                    undockUT,
                    undockSnapshots);
                if (completed)
                    recording.MarkFilesDirty();
                return completed;
            }

            ParsekLog.Verbose("Flight",
                $"Route window undock completion skipped: recording={recording.RecordingId ?? "<none>"} " +
                "has no incomplete window");
            return false;
        }

        internal static bool CompleteRouteConnectionWindowAtUndock(
            RouteConnectionWindow window,
            double undockUT,
            params ConfigNode[] undockSnapshots)
        {
            if (window == null || window.TransportPartPersistentIds == null ||
                window.EndpointPartPersistentIds == null)
            {
                ParsekLog.Warn("Flight",
                    "Route window undock completion failed: missing window or part PID sets");
                return false;
            }

            if (!TryVerifyRoutePartSetsSeparated(
                    undockSnapshots,
                    window.TransportPartPersistentIds,
                    window.EndpointPartPersistentIds,
                    out int transportSnapshotCount,
                    out int endpointSnapshotCount,
                    out bool sawOverlap))
            {
                ParsekLog.Warn("Flight",
                    $"Route window undock completion failed: split snapshots do not separate " +
                    $"transport/endpoint part PID sets window={window.WindowId ?? "<none>"} " +
                    $"targetPid={window.TransferTargetVesselPid} snapshots={undockSnapshots?.Length ?? 0} " +
                    $"transportSnapshots={transportSnapshotCount} endpointSnapshots={endpointSnapshotCount} " +
                    $"overlap={sawOverlap}");
                return false;
            }

            // Observational warning when part configuration drifted during the
            // docked window (EVA construction etc.). Doesn't reject the route —
            // disjoint verifier already passed and resource accounting still works
            // for the originally-listed parts. Stock fuel/inventory transfers
            // don't trip this; only outer part-set changes do.
            LogRoutePartSetEqualityWarnings(
                undockSnapshots,
                window.TransportPartPersistentIds,
                window.EndpointPartPersistentIds,
                window.WindowId);

            window.UndockUT = undockUT;
            window.UndockTransportResources = ExtractResourceManifestFromSnapshots(
                undockSnapshots,
                window.TransportPartPersistentIds);
            window.UndockEndpointResources = ExtractResourceManifestFromSnapshots(
                undockSnapshots,
                window.EndpointPartPersistentIds);
            window.UndockTransportInventory = ExtractInventoryPayloadItemsFromSnapshots(
                undockSnapshots,
                window.TransportPartPersistentIds);
            window.UndockEndpointInventory = ExtractInventoryPayloadItemsFromSnapshots(
                undockSnapshots,
                window.EndpointPartPersistentIds);

            ParsekLog.Verbose("Flight",
                $"Route window undock capture: window={window.WindowId ?? "<none>"} " +
                $"targetPid={window.TransferTargetVesselPid} " +
                $"transportRes={window.UndockTransportResources?.Count ?? 0} " +
                $"endpointRes={window.UndockEndpointResources?.Count ?? 0} " +
                $"transportInv={window.UndockTransportInventory?.Count ?? 0} " +
                $"endpointInv={window.UndockEndpointInventory?.Count ?? 0}");

            // Route-window per-resource delta observability (MAJOR 6 / the B-DOCK
            // headline payoff): the recorded net cargo per side (Undock* - Dock*).
            // Info (not Verbose): this is the single observable surface the offline
            // oracle checks the commanded LF/MP transfers against, so it must survive
            // the default log level. Endpoint side is conservation-mirrored.
            ParsekLog.Info("Flight",
                $"Route window delta: window={window.WindowId ?? "<none>"} " +
                $"targetPid={window.TransferTargetVesselPid} " +
                $"transportDelta=[{FormatRouteResourceDelta(window.DockTransportResources, window.UndockTransportResources)}] " +
                $"endpointDelta=[{FormatRouteResourceDelta(window.DockEndpointResources, window.UndockEndpointResources)}]");

            return true;
        }

        /// <summary>
        /// Formats the per-resource net delta (undock - dock) for one route-window side
        /// as a stable ASCII token string, e.g. "LiquidFuel=+40.0 MonoPropellant=-15.0".
        /// Positive = the side GAINED the resource across the docked window; negative =
        /// it lost it. Sorted by resource name (ordinal) for byte-stable output;
        /// "(none)" when there is no delta. Pure / static / testable (the MAJOR-6
        /// route-window delta observability surface asserted by the B-DOCK logContract).
        /// </summary>
        internal static string FormatRouteResourceDelta(
            Dictionary<string, ResourceAmount> dockManifest,
            Dictionary<string, ResourceAmount> undockManifest)
        {
            Dictionary<string, double> delta =
                ResourceManifest.ComputeResourceDelta(dockManifest, undockManifest);
            if (delta == null || delta.Count == 0)
                return "(none)";

            var keys = new List<string>(delta.Keys);
            keys.Sort(StringComparer.Ordinal);
            var parts = new List<string>(keys.Count);
            foreach (string key in keys)
            {
                double d = delta[key];
                parts.Add(string.Format(CultureInfo.InvariantCulture, "{0}={1}{2:F1}",
                    key, d >= 0 ? "+" : string.Empty, d));
            }
            return string.Join(" ", parts.ToArray());
        }

        /// <summary>
        /// Compares an actual part-PID set (captured from a post-undock vessel half)
        /// against the pre-dock expected set. Returns the symmetric difference split
        /// into added (in actual, not in expected) and removed (in expected, not in
        /// actual). Pure / static / testable.
        /// </summary>
        internal static void ComputePartSetDifferences(
            IEnumerable<uint> actualPartPids,
            IEnumerable<uint> expectedPartPids,
            out List<uint> addedPids,
            out List<uint> removedPids)
        {
            addedPids = new List<uint>();
            removedPids = new List<uint>();

            HashSet<uint> actual = actualPartPids != null
                ? new HashSet<uint>(actualPartPids)
                : new HashSet<uint>();
            HashSet<uint> expected = expectedPartPids != null
                ? new HashSet<uint>(expectedPartPids)
                : new HashSet<uint>();

            foreach (uint pid in actual)
            {
                if (!expected.Contains(pid)) addedPids.Add(pid);
            }
            foreach (uint pid in expected)
            {
                if (!actual.Contains(pid)) removedPids.Add(pid);
            }

            addedPids.Sort();
            removedPids.Sort();
        }

        /// <summary>
        /// After the disjoint-set verifier accepts an undock split, walks each
        /// snapshot and warns if its part-PID set is not equal to the expected
        /// pre-dock set. The disjoint verifier is the route-eligibility gate (no
        /// transport/endpoint overlap); this warning is observational — it surfaces
        /// part configuration drift during the docked window (e.g. EVA construction
        /// added or removed parts) without rejecting the route. Stock fuel/inventory
        /// transfers do NOT trip these warnings because they don't change either
        /// side's outer part-PID set.
        /// </summary>
        internal static void LogRoutePartSetEqualityWarnings(
            ConfigNode[] snapshots,
            ICollection<uint> transportPartPersistentIds,
            ICollection<uint> endpointPartPersistentIds,
            string windowId)
        {
            if (snapshots == null) return;
            if (transportPartPersistentIds == null || endpointPartPersistentIds == null) return;

            for (int i = 0; i < snapshots.Length; i++)
            {
                ConfigNode snapshot = snapshots[i];
                if (snapshot == null) continue;

                bool hasTransport = SnapshotContainsAnyPartPersistentId(
                    snapshot, transportPartPersistentIds);
                bool hasEndpoint = SnapshotContainsAnyPartPersistentId(
                    snapshot, endpointPartPersistentIds);
                if (hasTransport == hasEndpoint) continue; // both/neither — disjoint verifier filtered

                ICollection<uint> expected = hasTransport
                    ? transportPartPersistentIds
                    : endpointPartPersistentIds;
                string sideLabel = hasTransport ? "transport" : "endpoint";

                List<uint> actual = VesselSpawner.CollectPartPersistentIds(snapshot)
                    ?? new List<uint>();
                ComputePartSetDifferences(
                    actual,
                    expected,
                    out List<uint> addedPids,
                    out List<uint> removedPids);

                if (addedPids.Count == 0 && removedPids.Count == 0) continue;

                ParsekLog.Warn("Flight",
                    $"Route window part-set drift on undock side='{sideLabel}' " +
                    $"window={windowId ?? "<none>"} " +
                    $"expected={expected.Count} actual={actual.Count} " +
                    $"added={FormatPidList(addedPids)} removed={FormatPidList(removedPids)} " +
                    "(disjoint check still passed — route eligibility unchanged; investigate if " +
                    "ghost replay or resource accounting looks wrong)");
            }
        }

        private static string FormatPidList(List<uint> pids)
        {
            if (pids == null || pids.Count == 0) return "[]";
            var sb = new System.Text.StringBuilder();
            sb.Append('[');
            for (int i = 0; i < pids.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(pids[i].ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static bool TryVerifyRoutePartSetsSeparated(
            ConfigNode[] snapshots,
            ICollection<uint> transportPartPersistentIds,
            ICollection<uint> endpointPartPersistentIds,
            out int transportSnapshotCount,
            out int endpointSnapshotCount,
            out bool sawOverlap)
        {
            transportSnapshotCount = 0;
            endpointSnapshotCount = 0;
            sawOverlap = false;

            if (snapshots == null ||
                transportPartPersistentIds == null || transportPartPersistentIds.Count == 0 ||
                endpointPartPersistentIds == null || endpointPartPersistentIds.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < snapshots.Length; i++)
            {
                bool hasTransport = SnapshotContainsAnyPartPersistentId(
                    snapshots[i],
                    transportPartPersistentIds);
                bool hasEndpoint = SnapshotContainsAnyPartPersistentId(
                    snapshots[i],
                    endpointPartPersistentIds);

                if (hasTransport && hasEndpoint)
                {
                    sawOverlap = true;
                    return false;
                }
                if (hasTransport)
                    transportSnapshotCount++;
                if (hasEndpoint)
                    endpointSnapshotCount++;
            }

            return transportSnapshotCount == 1 && endpointSnapshotCount == 1;
        }

        private static bool SnapshotContainsAnyPartPersistentId(
            ConfigNode snapshot,
            ICollection<uint> partPersistentIds)
        {
            if (snapshot == null || partPersistentIds == null || partPersistentIds.Count == 0)
                return false;

            ConfigNode[] parts = snapshot.GetNodes("PART");
            for (int i = 0; i < parts.Length; i++)
            {
                if (VesselSpawner.TryGetPartPersistentId(parts[i], out uint pid) &&
                    partPersistentIds.Contains(pid))
                {
                    return true;
                }
            }

            return false;
        }

        private static string BuildWindowId(double dockUT, uint transferTargetVesselPid)
        {
            return "dock-" + dockUT.ToString("R", CultureInfo.InvariantCulture)
                + "-target-" + transferTargetVesselPid.ToString(CultureInfo.InvariantCulture);
        }

        private static Dictionary<string, ResourceAmount> ExtractResourceManifestFromSnapshots(
            ConfigNode[] snapshots,
            ICollection<uint> partPersistentIds)
        {
            if (snapshots == null || partPersistentIds == null || partPersistentIds.Count == 0)
                return null;

            Dictionary<string, ResourceAmount> merged = null;
            for (int i = 0; i < snapshots.Length; i++)
            {
                Dictionary<string, ResourceAmount> manifest =
                    VesselSpawner.ExtractResourceManifest(snapshots[i], partPersistentIds);
                if (manifest == null || manifest.Count == 0)
                    continue;

                if (merged == null)
                    merged = new Dictionary<string, ResourceAmount>();
                MergeResourceManifest(merged, manifest);
            }

            return merged != null && merged.Count > 0 ? merged : null;
        }

        private static List<InventoryPayloadItem> ExtractInventoryPayloadItemsFromSnapshots(
            ConfigNode[] snapshots,
            ICollection<uint> partPersistentIds)
        {
            if (snapshots == null || partPersistentIds == null || partPersistentIds.Count == 0)
                return null;

            Dictionary<string, InventoryPayloadItem> merged = null;
            for (int i = 0; i < snapshots.Length; i++)
            {
                List<InventoryPayloadItem> items =
                    VesselSpawner.ExtractInventoryPayloadItems(snapshots[i], partPersistentIds);
                if (items == null || items.Count == 0)
                    continue;

                if (merged == null)
                    merged = new Dictionary<string, InventoryPayloadItem>();
                MergeInventoryPayloadItems(merged, items);
            }

            if (merged == null || merged.Count == 0)
                return null;

            var list = new List<InventoryPayloadItem>(merged.Values);
            list.Sort((a, b) => string.Compare(a.IdentityHash, b.IdentityHash, StringComparison.Ordinal));
            return list;
        }

        private static void MergeResourceManifest(
            Dictionary<string, ResourceAmount> target,
            Dictionary<string, ResourceAmount> source)
        {
            foreach (KeyValuePair<string, ResourceAmount> kvp in source)
            {
                if (target.TryGetValue(kvp.Key, out ResourceAmount existing))
                {
                    existing.amount += kvp.Value.amount;
                    existing.maxAmount += kvp.Value.maxAmount;
                    target[kvp.Key] = existing;
                }
                else
                {
                    target[kvp.Key] = kvp.Value;
                }
            }
        }

        private static void MergeInventoryPayloadItems(
            Dictionary<string, InventoryPayloadItem> target,
            List<InventoryPayloadItem> source)
        {
            for (int i = 0; i < source.Count; i++)
            {
                InventoryPayloadItem item = source[i];
                if (item == null || string.IsNullOrEmpty(item.IdentityHash))
                    continue;

                if (target.TryGetValue(item.IdentityHash, out InventoryPayloadItem existing))
                {
                    existing.Quantity += item.Quantity;
                    existing.SlotsTaken += item.SlotsTaken;
                }
                else
                {
                    target[item.IdentityHash] = item.DeepClone();
                }
            }
        }

        private static List<uint> DeriveEndpointPartPids(
            ConfigNode dockedSnapshot,
            List<uint> transportPartPersistentIds)
        {
            List<uint> allPids = VesselSpawner.CollectPartPersistentIds(dockedSnapshot);
            if (allPids == null || allPids.Count == 0)
                return null;

            var endpoint = new List<uint>();
            for (int i = 0; i < allPids.Count; i++)
            {
                if (!transportPartPersistentIds.Contains(allPids[i]))
                    endpoint.Add(allPids[i]);
            }

            return NormalizePartPids(endpoint);
        }

        private static List<uint> NormalizePartPids(ICollection<uint> source)
        {
            if (source == null || source.Count == 0)
                return null;

            var pids = new List<uint>();
            foreach (uint pid in source)
            {
                if (pid == 0 || pids.Contains(pid))
                    continue;
                pids.Add(pid);
            }

            if (pids.Count == 0)
                return null;

            pids.Sort();
            return pids;
        }
    }
}
