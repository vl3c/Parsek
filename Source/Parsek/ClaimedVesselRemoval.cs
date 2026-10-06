using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The live surface of a vessel the Ghost Chain Rule removes from the world (the flight
    /// ghosting of a claimed vessel, the chain-tip replacement of a stale one). Abstracted so
    /// the removal order is testable without KSP.
    /// </summary>
    internal interface IClaimedVesselRemovalTarget
    {
        uint PersistentId { get; }
        string VesselName { get; }

        /// <summary>Takes every kerbal off the vessel's parts and returns their names.</summary>
        List<string> DetachCrew();

        /// <summary>Removes the vessel from the game.</summary>
        void Destroy();

        /// <summary>Sets a detached kerbal Available when it is still Assigned; true when it changed.</summary>
        bool ReleaseCrew(string name);

        /// <summary>
        /// Puts every detached kerbal back where it was taken from (the removal failed, so
        /// the vessel stays); returns how many went back.
        /// </summary>
        int ReattachCrew();
    }

    internal struct ClaimedVesselRemovalResult
    {
        internal List<string> DetachedCrew;
        internal int ReleasedCrew;
    }

    /// <summary>
    /// Removes a claimed vessel without killing its crew. Stock <c>Vessel.Die()</c> on an
    /// UNLOADED vessel runs <c>MurderCrew</c>: every kerbal aboard goes Dead (or Missing),
    /// <c>onCrewKilled</c> fires and a career pays the kerbal-death reputation loss. A claimed
    /// vessel is not destroyed, only held back until its recorded end state spawns, so the crew
    /// are taken off the parts first (Die then finds nobody aboard) and set Available, all
    /// under the crew suppression guard so Parsek records no roster event. Available is the
    /// state the later spawn expects: the chain tip's snapshot seats whoever it carries
    /// (<c>VesselSpawner.RespawnVessel</c> / <c>SpawnAtPosition</c>), and a kerbal already
    /// aboard another live vessel is kept out of that snapshot, so nobody is booked twice.
    /// </summary>
    internal static class ClaimedVesselRemoval
    {
        private const string Tag = "ClaimRemoval";
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        internal static ClaimedVesselRemovalResult Remove(IClaimedVesselRemovalTarget target, string context)
        {
            var result = new ClaimedVesselRemovalResult { DetachedCrew = new List<string>() };
            if (target == null)
                return result;

            using (SuppressionGuard.Crew())
            {
                List<string> crew = target.DetachCrew() ?? new List<string>();
                result.DetachedCrew = crew;
                try
                {
                    target.Destroy();
                }
                catch (Exception ex)
                {
                    // The vessel may still stand: never leave it crewless. Its kerbals go back
                    // aboard (still Assigned: nothing was released yet) before the failure
                    // propagates to the caller's own recovery.
                    int reattached = 0;
                    try
                    {
                        reattached = target.ReattachCrew();
                    }
                    catch (Exception reattachEx)
                    {
                        ParsekLog.Error(Tag,
                            string.Format(ic,
                                "Claimed vessel crew could not be put back ({0}): pid={1}: {2}: {3}",
                                string.IsNullOrEmpty(context) ? "(none)" : context,
                                target.PersistentId, reattachEx.GetType().Name, reattachEx.Message));
                    }
                    ParsekLog.Warn(Tag,
                        string.Format(ic,
                            "Claimed vessel removal failed ({0}): pid={1} name=\"{2}\" {3}: {4}; " +
                            "reattached {5}/{6} kerbal(s)",
                            string.IsNullOrEmpty(context) ? "(none)" : context,
                            target.PersistentId, target.VesselName ?? "(null)",
                            ex.GetType().Name, ex.Message, reattached, crew.Count));
                    throw;
                }
                for (int i = 0; i < crew.Count; i++)
                {
                    if (target.ReleaseCrew(crew[i]))
                        result.ReleasedCrew++;
                }
            }

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Claimed vessel removed ({0}): pid={1} name=\"{2}\" crewDetached={3} [{4}] crewSetAvailable={5} " +
                    "- no recovery, no ledger row, no crew loss",
                    string.IsNullOrEmpty(context) ? "(none)" : context,
                    target.PersistentId,
                    target.VesselName ?? "(null)",
                    result.DetachedCrew.Count,
                    string.Join(", ", result.DetachedCrew.ToArray()),
                    result.ReleasedCrew));
            return result;
        }
    }

    /// <summary>
    /// <see cref="IClaimedVesselRemovalTarget"/> over a live <see cref="Vessel"/> and / or its
    /// <see cref="ProtoVessel"/>. A loaded vessel's kerbals leave through
    /// <c>Part.RemoveCrewmember</c>; an unloaded (or proto-only) vessel's through its part
    /// snapshots and the ProtoVessel's crew list, which is what <c>MurderCrew</c> reads.
    /// <paramref name="protoVessels"/>, when given, is the flight state the ProtoVessel is
    /// removed from (the Space Center and the Tracking Station); null leaves it to KSP.
    /// </summary>
    internal sealed class LiveClaimedVesselRemovalTarget : IClaimedVesselRemovalTarget
    {
        private readonly Vessel vessel;
        private readonly ProtoVessel proto;
        private readonly IList<ProtoVessel> protoVessels;
        private readonly Dictionary<string, ProtoCrewMember> detached =
            new Dictionary<string, ProtoCrewMember>(StringComparer.Ordinal);
        private readonly List<KeyValuePair<Part, ProtoCrewMember>> detachedFromParts =
            new List<KeyValuePair<Part, ProtoCrewMember>>();
        private readonly List<KeyValuePair<ProtoPartSnapshot, ProtoCrewMember>> detachedFromSnapshots =
            new List<KeyValuePair<ProtoPartSnapshot, ProtoCrewMember>>();

        internal LiveClaimedVesselRemovalTarget(Vessel vessel, ProtoVessel proto, IList<ProtoVessel> protoVessels)
        {
            this.vessel = vessel;
            this.proto = proto ?? (vessel != null ? vessel.protoVessel : null);
            this.protoVessels = protoVessels;
        }

        public uint PersistentId
        {
            get { return vessel != null ? vessel.persistentId : (proto != null ? proto.persistentId : 0u); }
        }

        public string VesselName
        {
            get { return vessel != null ? vessel.vesselName : (proto != null ? proto.vesselName : null); }
        }

        public List<string> DetachCrew()
        {
            var names = new List<string>();
            if (vessel != null && vessel.loaded)
            {
                if (vessel.parts == null)
                    return names;
                for (int p = 0; p < vessel.parts.Count; p++)
                {
                    Part part = vessel.parts[p];
                    if (part == null || part.protoModuleCrew == null || part.protoModuleCrew.Count == 0)
                        continue;
                    ProtoCrewMember[] crew = part.protoModuleCrew.ToArray();
                    for (int c = 0; c < crew.Length; c++)
                    {
                        if (crew[c] == null)
                            continue;
                        part.RemoveCrewmember(crew[c]);
                        detachedFromParts.Add(new KeyValuePair<Part, ProtoCrewMember>(part, crew[c]));
                        Note(crew[c], names);
                    }
                }
                return names;
            }

            if (proto == null || proto.protoPartSnapshots == null)
                return names;
            for (int p = 0; p < proto.protoPartSnapshots.Count; p++)
            {
                ProtoPartSnapshot pps = proto.protoPartSnapshots[p];
                if (pps == null || pps.protoModuleCrew == null || pps.protoModuleCrew.Count == 0)
                    continue;
                ProtoCrewMember[] crew = pps.protoModuleCrew.ToArray();
                for (int c = 0; c < crew.Length; c++)
                {
                    if (crew[c] == null)
                        continue;
                    pps.RemoveCrew(crew[c]);
                    proto.RemoveCrew(crew[c]);
                    detachedFromSnapshots.Add(new KeyValuePair<ProtoPartSnapshot, ProtoCrewMember>(pps, crew[c]));
                    Note(crew[c], names);
                }
            }
            return names;
        }

        private void Note(ProtoCrewMember pcm, List<string> names)
        {
            string name = pcm.name ?? "(unnamed)";
            detached[name] = pcm;
            names.Add(name);
        }

        public void Destroy()
        {
            if (vessel != null)
                vessel.Die();
            if (proto != null && protoVessels != null)
                protoVessels.Remove(proto);
        }

        public int ReattachCrew()
        {
            int back = 0;
            for (int i = 0; i < detachedFromParts.Count; i++)
            {
                Part part = detachedFromParts[i].Key;
                ProtoCrewMember pcm = detachedFromParts[i].Value;
                if (part != null && pcm != null && part.AddCrewmember(pcm))
                    back++;
            }
            for (int i = 0; i < detachedFromSnapshots.Count; i++)
            {
                ProtoPartSnapshot pps = detachedFromSnapshots[i].Key;
                ProtoCrewMember pcm = detachedFromSnapshots[i].Value;
                if (pps == null || pcm == null)
                    continue;
                pps.protoModuleCrew.Add(pcm);
                pps.protoCrewNames.Add(pcm.name);
                if (proto != null)
                    proto.AddCrew(pcm);
                back++;
            }
            detachedFromParts.Clear();
            detachedFromSnapshots.Clear();
            detached.Clear();
            return back;
        }

        public bool ReleaseCrew(string name)
        {
            ProtoCrewMember pcm;
            if (name == null || !detached.TryGetValue(name, out pcm) || pcm == null)
                return false;
            if (pcm.rosterStatus != ProtoCrewMember.RosterStatus.Assigned)
                return false;
            pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available;
            return true;
        }
    }
}
