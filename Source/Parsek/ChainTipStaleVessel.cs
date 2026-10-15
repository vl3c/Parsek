using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Where the stale vessel a chain-tip replacement removed was the player's map focus,
    /// navigation target or Tracking Station selection, so the scene can hand them to the
    /// spawned tip (same pid, new Vessel object).
    /// </summary>
    internal struct StaleChainVesselFocus
    {
        internal bool WasNavigationTarget;
        internal bool WasMapFocus;
        internal bool WasTrackingStationSelected;
    }

    /// <summary>
    /// Everything the pure decision reads about one live claimed vessel and the tip, gathered
    /// by the live side (or a test) beforehand. A tip that ends several claimed vessels gets
    /// one of these per live claimed vessel, each carrying the same tip-level facts.
    /// </summary>
    internal struct StaleVesselEvidence
    {
        /// <summary>The claimed pid the live vessel below carries (the one replaced on a yes).</summary>
        internal uint ClaimedPid;
        /// <summary>A live vessel with that claimed pid exists.</summary>
        internal bool LiveExists;
        /// <summary>That vessel's launch guid (<c>Vessel.id</c>, "N" form); null when unknown.</summary>
        internal string LiveGuid;
        /// <summary>This session saw the playhead strictly before the tip's start.</summary>
        internal bool PlayheadSeenBeforeTip;
        /// <summary>The live vessel's <c>lastUT</c>; NaN when unknown.</summary>
        internal double LiveLastUT;
        /// <summary>The live vessel is the active vessel or recorded by the live tree.</summary>
        internal bool LiveInUse;
        /// <summary>Why the tip cannot spawn right now (null when it can).</summary>
        internal string TipSpawnBlocker;
        /// <summary>
        /// A live vessel already matches the tip's own identity (pid and launch), which the
        /// site adopts. It blocks the replacement only when the tip's pid is not one of the
        /// live claimed vessels being replaced (then that vessel is the stale one itself).
        /// </summary>
        internal bool TipIdentityLive;
    }

    /// <summary>What the live side reads about the vessel carrying a claimed pid.</summary>
    internal struct LiveClaimedVesselProbe
    {
        internal bool Exists;
        internal string Guid;
        internal double LastUT;
    }

    /// <summary>One vessel a chain-tip replacement removed, with the snapshot taken just before.</summary>
    internal sealed class RemovedStaleVessel
    {
        internal uint Pid;
        internal ConfigNode Snapshot;
        internal StaleChainVesselFocus Focus;
    }

    /// <summary>
    /// A chain-tip replacement in progress: every removed vessel's pid and the snapshot taken
    /// just before its removal (crew still aboard), so the site can put them all back when
    /// the tip then spawns no vessel. The site must call
    /// <see cref="ChainTipStaleVessel.CompleteReplacement"/> after its spawn attempt.
    /// </summary>
    internal sealed class StaleVesselReplacement
    {
        internal readonly List<RemovedStaleVessel> Removed = new List<RemovedStaleVessel>();
        internal string RemovedName;
        internal string Scene;
        internal int Index;

        /// <summary>The removed vessels' pids, in removal order.</summary>
        internal List<uint> RemovedPids
        {
            get
            {
                var pids = new List<uint>(Removed.Count);
                for (int i = 0; i < Removed.Count; i++)
                    pids.Add(Removed[i].Pid);
                return pids;
            }
        }

        /// <summary>
        /// Where any removed vessel was the player's focus, navigation target or selection:
        /// all of it moves to the one tip that replaces them.
        /// </summary>
        internal StaleChainVesselFocus Focus
        {
            get
            {
                var focus = default(StaleChainVesselFocus);
                for (int i = 0; i < Removed.Count; i++)
                {
                    focus.WasNavigationTarget |= Removed[i].Focus.WasNavigationTarget;
                    focus.WasMapFocus |= Removed[i].Focus.WasMapFocus;
                    focus.WasTrackingStationSelected |= Removed[i].Focus.WasTrackingStationSelected;
                }
                return focus;
            }
        }
    }

    /// <summary>
    /// The Ghost Chain Rule (design 12.5, 13.2) at the end-of-recording spawn sites outside
    /// the flight chain path.
    ///
    /// <para>Only the flight scene despawns a vessel a committed future mission claims
    /// (<see cref="VesselGhoster"/>, on flight load while the chain's spawn UT is still
    /// ahead). The Space Center and the Tracking Station never do, and a flight load past the
    /// spawn UT no longer ghosts the chain, so after a rewind or a load to before the claim
    /// the claimed vessel can still be live in its pre-claim form when the chain tip's spawn
    /// UT passes. When the station kept its pid through the dock (it was the dominant
    /// vessel), the stale station carries the tip's pid and launch guid and the ordinary
    /// adoption check accepts it; when the transport was dominant, the station half got a new
    /// pid on undock, the tip spawns beside the stale station, and the station is there twice.
    /// Either way the recorded change (a fuel transfer, a docked module) is lost. So the live
    /// vessel looked for is the one with the chain's CLAIMED pid
    /// (<see cref="GhostChain.OriginalVesselPid"/>), found through the chain whose tip the
    /// spawning recording is, not the tip's own pid.</para>
    ///
    /// <para>Two independent pieces of evidence must agree before a live vessel is called
    /// stale, because a pid and launch-guid match alone cannot tell the pre-claim original
    /// from the tip's own result (the station half keeps both). First, the timeline: this
    /// session saw the live playhead strictly before the tip's start
    /// (<see cref="PlaybackScopeTracker.WasPlayheadSeenBeforeActivation"/>), so a rewind or a
    /// load put the world before the vessel took its recorded final form. The flight
    /// contract draws the same line at load time (ghost while the spawn UT is ahead).
    /// Second, the vessel itself: its <c>lastUT</c> (the last UT KSP simulated it in
    /// physics; FlightIntegrator stamps it every physics frame and nothing advances it on
    /// rails) lies before the chain's last claim. Every claim (a dock, a boarding, a part
    /// event) happens with the claimed vessel in physics, so the vessel that went through
    /// every recorded change carries a later <c>lastUT</c>, while one loaded from before the
    /// last of them (before a dock, or between two committed docks) does not. The
    /// second check keeps a session that rewound and then loaded a save from after the change
    /// from replacing the real vessel there; the first keeps normal play, including a commit
    /// made seconds after the tip began, on the adoption path.</para>
    ///
    /// <para>Every live claimed vessel the tip ends, and never one the tip's own identity can
    /// stand for. Every claimed pid whose lineage ends in the tip is probed and every live one
    /// is replaced: a tip that ends two claimed vessels (a tug docks to a depot, then carries
    /// it to a station: the walker folds the station's chain into the depot's) removes both
    /// and spawns once as their recorded merged form. Each live claimed vessel must pass every
    /// check on its own (its launch, its <c>lastUT</c>, not in the player's hands); one doubt
    /// keeps all of them. Counted by live vessel, not by pid: one station claimed twice
    /// (again under the new pid it took in a dock it did not dominate) has only its original
    /// pid live after a rewind, so it is one vessel replaced. A live vessel matching the tip's
    /// own pid and launch, when the tip's pid is not one of the live claimed vessels, is the
    /// site's adoption, so nothing is removed either.</para>
    ///
    /// <para>A replacement never loses a vessel. The tip must be able to spawn before
    /// anything is removed (<see cref="ResolveTipSpawnBlocker"/>), each removal needs a
    /// snapshot of the vessel taken just before it (crew still aboard), a removal that fails
    /// puts back the vessels already removed, and when the spawn attempt then yields no
    /// vessel (a failure, an abandon, an exception) the site's
    /// <see cref="CompleteReplacement"/> respawns every snapshot with its identity. The
    /// removal itself goes through <see cref="ClaimedVesselRemoval"/> (crew taken off and set
    /// Available, then <c>Vessel.Die()</c>: no recovery, so no funds and no ledger row, and no
    /// crew loss) and the tip spawns from its snapshot with its identity preserved.</para>
    /// </summary>
    internal static class ChainTipStaleVessel
    {
        private const string Tag = "ChainTip";
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        internal const string ReasonReplace = "stale-pre-claim-vessel";
        internal const string ReasonNoRecording = "no-recording";
        internal const string ReasonTipSpawned = "tip-already-spawned";
        internal const string ReasonNoLiveVessel = "no-live-same-launch-vessel";
        internal const string ReasonNotChainTip = "not-chain-tip";
        internal const string ReasonDifferentLaunch = "different-launch";
        internal const string ReasonTerminated = "terminated-chain";
        internal const string ReasonNotRewoundBeforeTip = "not-rewound-before-tip";
        internal const string ReasonSimulatedSinceClaim = "live-vessel-simulated-since-claim";
        internal const string ReasonTipCannotSpawn = "tip-cannot-spawn";
        internal const string ReasonInUse = "live-vessel-in-use";
        internal const string ReasonTipIdentityLive = "tip-identity-live";

        internal const string BlockerAbandoned = "abandoned";
        internal const string BlockerAttemptCap = "attempt-cap";
        internal const string BlockerTerminalOrbitHold = "terminal-orbit-hold";
        internal const string BlockerNoSnapshot = "no-snapshot";
        internal const string BlockerRetiresAtKsc = "retires-at-ksc";

        /// <summary>
        /// Test seam replacing the live removal: (pid, scene) -> the snapshot taken before
        /// the vessel was removed, or null when it was not removed.
        /// </summary>
        internal static Func<uint, string, ConfigNode> RemoveOverrideForTesting;

        /// <summary>Test seam replacing the live restore: (snapshot, pid, scene) -> restored pid (0 = failed).</summary>
        internal static Func<ConfigNode, uint, string, uint> RestoreOverrideForTesting;

        /// <summary>Test seam replacing the live in-use check (active vessel, recorded by the live tree).</summary>
        internal static Func<uint, bool> LiveVesselInUseOverrideForTesting;

        /// <summary>Test seam replacing the live probe of the vessel with a claimed pid.</summary>
        internal static Func<uint, LiveClaimedVesselProbe> LiveVesselProbeOverrideForTesting;

        /// <summary>How many times this class walked the committed trees for chains itself (tests read it).</summary>
        internal static int ChainWalksForTesting;

        internal static void ResetForTesting()
        {
            RemoveOverrideForTesting = null;
            RestoreOverrideForTesting = null;
            LiveVesselInUseOverrideForTesting = null;
            LiveVesselProbeOverrideForTesting = null;
            ChainWalksForTesting = 0;
        }

        /// <summary>
        /// The ghost chain whose tip is <paramref name="tipRecordingId"/>, or null. The chain's
        /// key is the CLAIMED vessel's pid, which the tip does not carry when the claimed vessel
        /// lost its pid in a dock it did not dominate.
        /// </summary>
        internal static GhostChain FindChainForTip(Dictionary<uint, GhostChain> chains, string tipRecordingId)
        {
            if (chains == null || string.IsNullOrEmpty(tipRecordingId))
                return null;
            foreach (var kvp in chains)
            {
                GhostChain chain = kvp.Value;
                if (chain != null
                    && string.Equals(chain.TipRecordingId, tipRecordingId, StringComparison.Ordinal))
                    return chain;
            }
            return null;
        }

        /// <summary>
        /// Every ghost chain whose tip is <paramref name="tipRecordingId"/> (two claims on one
        /// vessel under two pids can leave two chains ending at the same tip).
        /// </summary>
        internal static List<GhostChain> FindChainsForTip(Dictionary<uint, GhostChain> chains, string tipRecordingId)
        {
            var found = new List<GhostChain>();
            if (chains == null || string.IsNullOrEmpty(tipRecordingId))
                return found;
            foreach (var kvp in chains)
            {
                GhostChain chain = kvp.Value;
                if (chain != null
                    && string.Equals(chain.TipRecordingId, tipRecordingId, StringComparison.Ordinal))
                    found.Add(chain);
            }
            return found;
        }

        /// <summary>
        /// Every claimed pid whose lineage ends in this tip: the original pid of each chain whose
        /// tip it is, plus each link's own claimed pid (a chain folds in another when its tip
        /// carries the other chain's claimed pid, <c>GhostChainWalker.MergeCrossTreeLinks</c>,
        /// and only the links remember the folded-in claim). A Dock / Board link claims its
        /// branch point's target pid; a background-event link claims its recording's pid.
        /// </summary>
        internal static HashSet<uint> ResolveClaimedPidsForTip(
            Dictionary<uint, GhostChain> chains, string tipRecordingId, IList<RecordingTree> trees)
        {
            var pids = new HashSet<uint>();
            if (chains == null || string.IsNullOrEmpty(tipRecordingId))
                return pids;
            foreach (var kvp in chains)
            {
                GhostChain chain = kvp.Value;
                if (chain == null
                    || !string.Equals(chain.TipRecordingId, tipRecordingId, StringComparison.Ordinal))
                    continue;
                if (chain.OriginalVesselPid != 0)
                    pids.Add(chain.OriginalVesselPid);
                if (chain.Links == null)
                    continue;
                for (int i = 0; i < chain.Links.Count; i++)
                {
                    uint linkPid = ResolveLinkClaimedPid(chain.Links[i], trees);
                    if (linkPid != 0)
                        pids.Add(linkPid);
                }
            }
            return pids;
        }

        internal static uint ResolveLinkClaimedPid(ChainLink link, IList<RecordingTree> trees)
        {
            if (trees == null)
                return 0u;
            for (int t = 0; t < trees.Count; t++)
            {
                RecordingTree tree = trees[t];
                if (tree == null || !string.Equals(tree.Id, link.treeId, StringComparison.Ordinal))
                    continue;
                if (!string.IsNullOrEmpty(link.branchPointId))
                {
                    for (int b = 0; b < tree.BranchPoints.Count; b++)
                    {
                        BranchPoint bp = tree.BranchPoints[b];
                        if (bp != null && bp.Id == link.branchPointId)
                            return bp.TargetVesselPersistentId;
                    }
                    return 0u;
                }
                Recording rec;
                if (!string.IsNullOrEmpty(link.recordingId)
                    && tree.Recordings != null
                    && tree.Recordings.TryGetValue(link.recordingId, out rec)
                    && rec != null)
                    return rec.VesselPersistentId;
                return 0u;
            }
            return 0u;
        }

        /// <summary>
        /// The launch guid the live vessel carrying <paramref name="claimedPid"/> must not
        /// conclusively differ from: the chain's when the chain is keyed by that pid (its guid in
        /// the claiming tree), else the folded claim's own launch when the chain folded in the
        /// chain keyed by that pid, else the tip's own when the tip still carries that pid; null
        /// (pid only) otherwise.
        /// </summary>
        internal static string ExpectedClaimedGuid(Recording tip, GhostChain chain, uint claimedPid)
        {
            if (chain != null && chain.OriginalVesselPid == claimedPid && !string.IsNullOrEmpty(chain.LaunchGuid))
                return chain.LaunchGuid;
            if (chain != null && chain.ClaimedVessels != null && chain.OriginalVesselPid != claimedPid)
            {
                for (int i = 0; i < chain.ClaimedVessels.Count; i++)
                {
                    if (chain.ClaimedVessels[i].Pid == claimedPid
                        && !string.IsNullOrEmpty(chain.ClaimedVessels[i].LaunchGuid))
                        return chain.ClaimedVessels[i].LaunchGuid;
                }
            }
            if (tip != null && claimedPid != 0 && tip.VesselPersistentId == claimedPid)
                return tip.RecordedVesselGuid;
            return null;
        }

        /// <summary>
        /// Pure, one live claimed vessel: <see cref="ShouldReplaceStaleLiveVessels"/> over that
        /// vessel's evidence alone (no live vessel when <c>LiveExists</c> is false).
        /// </summary>
        internal static bool ShouldReplaceStaleLiveVessel(
            Recording rec,
            Dictionary<uint, GhostChain> chains,
            StaleVesselEvidence evidence,
            out string reason)
        {
            return ShouldReplaceStaleLiveVessels(
                rec, chains, new List<StaleVesselEvidence> { evidence }, out reason);
        }

        /// <summary>
        /// Pure: should the live vessels carrying the CLAIMED pids that end at the tip
        /// <paramref name="rec"/> be replaced by the tip snapshot instead of adopted (or left
        /// beside it)? True only when <paramref name="rec"/> is the tip of a non-terminated
        /// ghost chain, the tip has not spawned, at least one live claimed vessel exists, and
        /// for every live one: its launch guid does not conclusively differ from the claimed
        /// one, it was last simulated before the chain's last claim (NaN = unknown, never
        /// stale), and it is not in the player's hands; plus the playhead was seen before the
        /// tip's start this session (a rewind or a load), the tip can spawn now, and no live
        /// vessel already stands for the tip's own identity on a pid that is not one of them.
        /// One vessel in doubt keeps all of them. Every other answer leaves the site on its
        /// existing path; <paramref name="reason"/> names the deciding check.
        /// </summary>
        internal static bool ShouldReplaceStaleLiveVessels(
            Recording rec,
            Dictionary<uint, GhostChain> chains,
            IList<StaleVesselEvidence> liveClaimed,
            out string reason)
        {
            return DecideStaleLiveVessels(rec, chains, liveClaimed, out reason, out _);
        }

        /// <summary>
        /// <see cref="ShouldReplaceStaleLiveVessels"/> naming the claimed pid that decided a
        /// per-vessel refusal (0 when the reason is not about one vessel).
        /// </summary>
        internal static bool DecideStaleLiveVessels(
            Recording rec,
            Dictionary<uint, GhostChain> chains,
            IList<StaleVesselEvidence> liveClaimed,
            out string reason,
            out uint decidingPid)
        {
            decidingPid = 0;
            if (rec == null || rec.VesselPersistentId == 0 || string.IsNullOrEmpty(rec.RecordingId))
            {
                reason = ReasonNoRecording;
                return false;
            }
            if (rec.VesselSpawned || rec.SpawnedVesselPersistentId != 0)
            {
                reason = ReasonTipSpawned;
                return false;
            }
            List<GhostChain> tipChains = FindChainsForTip(chains, rec.RecordingId);
            if (tipChains.Count == 0)
            {
                reason = ReasonNotChainTip;
                return false;
            }
            if (tipChains[0].IsTerminated)
            {
                reason = ReasonTerminated;
                return false;
            }

            var live = new List<StaleVesselEvidence>();
            if (liveClaimed != null)
            {
                for (int i = 0; i < liveClaimed.Count; i++)
                {
                    if (liveClaimed[i].LiveExists && liveClaimed[i].ClaimedPid != 0)
                        live.Add(liveClaimed[i]);
                }
            }
            if (live.Count == 0)
            {
                reason = ReasonNoLiveVessel;
                return false;
            }

            bool tipPidAmongLive = false;
            bool tipIdentityLive = false;
            bool playheadSeenBeforeTip = true;
            string tipSpawnBlocker = null;
            for (int i = 0; i < live.Count; i++)
            {
                StaleVesselEvidence e = live[i];
                if (e.ClaimedPid == rec.VesselPersistentId)
                    tipPidAmongLive = true;
                tipIdentityLive |= e.TipIdentityLive;
                playheadSeenBeforeTip &= e.PlayheadSeenBeforeTip;
                if (tipSpawnBlocker == null && !string.IsNullOrEmpty(e.TipSpawnBlocker))
                    tipSpawnBlocker = e.TipSpawnBlocker;
            }

            for (int i = 0; i < live.Count; i++)
            {
                StaleVesselEvidence e = live[i];
                GhostChain claimedChain = ChainKeyedBy(tipChains, e.ClaimedPid);
                if (VesselLaunchIdentity.GuidsConclusivelyDiffer(
                        e.LiveGuid, ExpectedClaimedGuid(rec, claimedChain, e.ClaimedPid)))
                {
                    reason = ReasonDifferentLaunch;
                    decidingPid = e.ClaimedPid;
                    return false;
                }
            }
            if (!tipPidAmongLive && tipIdentityLive)
            {
                reason = ReasonTipIdentityLive;
                decidingPid = live[0].ClaimedPid;
                return false;
            }
            if (!playheadSeenBeforeTip)
            {
                reason = ReasonNotRewoundBeforeTip;
                return false;
            }
            double lastClaimUT = LatestClaimUTOverChains(tipChains);
            for (int i = 0; i < live.Count; i++)
            {
                StaleVesselEvidence e = live[i];
                if (double.IsNaN(e.LiveLastUT) || double.IsNaN(lastClaimUT)
                    || e.LiveLastUT >= lastClaimUT)
                {
                    reason = ReasonSimulatedSinceClaim;
                    decidingPid = e.ClaimedPid;
                    return false;
                }
            }
            if (!string.IsNullOrEmpty(tipSpawnBlocker))
            {
                reason = ReasonTipCannotSpawn;
                return false;
            }
            for (int i = 0; i < live.Count; i++)
            {
                if (live[i].LiveInUse)
                {
                    reason = ReasonInUse;
                    decidingPid = live[i].ClaimedPid;
                    return false;
                }
            }

            reason = ReasonReplace;
            return true;
        }

        /// <summary>
        /// Why the tip cannot spawn right now, read before anything is removed; null when it
        /// can. A tip abandoned, out of spawn attempts, held by the terminal-orbit safety, with
        /// no snapshot, or whose flight ended parked in the KSC exclusion zone (retired, and a
        /// live counterpart is adopted instead, design 13.1) would leave nothing in the removed
        /// vessel's place.
        /// </summary>
        internal static string ResolveTipSpawnBlocker(Recording rec)
        {
            if (rec == null)
                return BlockerNoSnapshot;
            if (rec.SpawnAbandoned)
                return BlockerAbandoned;
            if (rec.SpawnAttempts >= VesselSpawner.MaxSpawnAttempts)
                return BlockerAttemptCap;
            if (TerminalOrbitSpawnSafety.HasActiveHold(rec))
                return BlockerTerminalOrbitHold;
            if (rec.VesselSnapshot == null)
                return BlockerNoSnapshot;
            if (VesselSpawner.EvaluateKscEndOfFlightRetirement(rec).Retire)
                return BlockerRetiresAtKsc;
            return null;
        }

        /// <summary>The latest claim UT over every chain ending at one tip, or NaN when none carries one.</summary>
        internal static double LatestClaimUTOverChains(List<GhostChain> tipChains)
        {
            double latest = double.NaN;
            if (tipChains == null)
                return latest;
            for (int i = 0; i < tipChains.Count; i++)
            {
                double ut = LatestClaimUT(tipChains[i]);
                if (!double.IsNaN(ut) && (double.IsNaN(latest) || ut > latest))
                    latest = ut;
            }
            return latest;
        }

        private static GhostChain ChainKeyedBy(List<GhostChain> tipChains, uint claimedPid)
        {
            for (int i = 0; i < tipChains.Count; i++)
            {
                if (tipChains[i].OriginalVesselPid == claimedPid)
                    return tipChains[i];
            }
            return tipChains.Count > 0 ? tipChains[0] : null;
        }

        /// <summary>The UT of the chain's latest claim, or NaN when it carries none.</summary>
        internal static double LatestClaimUT(GhostChain chain)
        {
            if (chain == null || chain.Links == null || chain.Links.Count == 0)
                return double.NaN;
            double latest = double.NegativeInfinity;
            for (int i = 0; i < chain.Links.Count; i++)
            {
                double ut = chain.Links[i].ut;
                if (!double.IsNaN(ut) && ut > latest)
                    latest = ut;
            }
            return double.IsNegativeInfinity(latest) ? double.NaN : latest;
        }

        /// <summary>
        /// Shared entry for the end-of-recording sites that resolve the live source vessel
        /// themselves (the Space Center end spawn and the flight leaf spawn): defers to
        /// <see cref="TryReplaceStaleLiveVessel"/> with the chains walked on demand. The #226
        /// replay bypass never adopts, so it never replaces either. Returns the replacement in
        /// progress (the caller spawns the tip with its identity preserved, then calls
        /// <see cref="CompleteReplacement"/>), or null.
        /// </summary>
        internal static StaleVesselReplacement TryReplaceStaleSourceBeforeSpawn(
            Recording rec,
            string scene,
            int index,
            bool allowExistingSourceDuplicate = false)
        {
            if (allowExistingSourceDuplicate || !IsReplacementCandidate(rec))
                return null;
            return TryReplaceStaleLiveVessel(rec, null, scene, index);
        }

        /// <summary>
        /// Evaluates <see cref="ShouldReplaceStaleLiveVessels"/> for a recording about to spawn
        /// (or be adopted) at its end, probing every claimed pid that ends at the tip, and on a
        /// yes removes every live claimed vessel. <paramref name="chains"/> may be null: the
        /// chains are then walked from the committed trees. Returns the replacement in
        /// progress, or null to leave the site on its existing path, including when a removal
        /// itself is refused or fails (the vessels already removed are put back first).
        /// </summary>
        internal static StaleVesselReplacement TryReplaceStaleLiveVessel(
            Recording rec,
            Dictionary<uint, GhostChain> chains,
            string scene,
            int index)
        {
            if (!IsReplacementCandidate(rec))
                return null;
            // Only a tip the playhead stood before this session can be replaced; every other
            // spawn candidate stops here, before the chain walk and the live probe.
            bool seenBeforeTip = PlaybackScopeTracker.WasPlayheadSeenBeforeActivation(rec.RecordingId);
            if (!seenBeforeTip)
                return null;

            if (chains == null)
            {
                ChainWalksForTesting++;
                chains = GhostChainWalker.ComputeAllGhostChains(RecordingStore.CommittedTrees, 0.0);
            }
            List<GhostChain> tipChains = FindChainsForTip(chains, rec.RecordingId);
            if (tipChains.Count == 0 || tipChains[0].IsTerminated)
                return null;
            GhostChain chain = tipChains[0];

            // Probe every claimed pid ending here; every live one is a vessel the tip replaces
            // (a tip that ends a depot and a station replaces both and spawns once). A pid is
            // one vessel at a time, so counting live pids counts live vessels.
            HashSet<uint> claimedPids = ResolveClaimedPidsForTip(
                chains, rec.RecordingId, RecordingStore.CommittedTrees);
            var sortedClaimed = new List<uint>(claimedPids);
            sortedClaimed.Sort();
            var live = new List<StaleVesselEvidence>();
            var livePids = new List<uint>();
            for (int i = 0; i < sortedClaimed.Count; i++)
            {
                LiveClaimedVesselProbe p = ProbeLiveVessel(sortedClaimed[i]);
                if (!p.Exists)
                    continue;
                livePids.Add(sortedClaimed[i]);
                live.Add(new StaleVesselEvidence
                {
                    ClaimedPid = sortedClaimed[i],
                    LiveExists = true,
                    LiveGuid = p.Guid,
                    LiveLastUT = p.LastUT,
                    PlayheadSeenBeforeTip = seenBeforeTip
                });
            }
            if (live.Count == 0)
                return null;

            // The spawn re-hydrates a dropped snapshot the same way; doing it here keeps a
            // dropped in-memory copy from reading as "cannot spawn".
            RecordingStore.TryHydrateVesselSnapshotFromSidecar(rec);
            string tipSpawnBlocker = ResolveTipSpawnBlocker(rec);
            bool tipPidAmongLive = livePids.Contains(rec.VesselPersistentId);
            bool tipIdentityLive = !tipPidAmongLive
                && VesselSpawner.MaterializedSourceVesselExists(rec, logAdoptionRejection: false);
            var inUseWhy = new Dictionary<uint, string>();
            for (int i = 0; i < live.Count; i++)
            {
                StaleVesselEvidence e = live[i];
                e.TipSpawnBlocker = tipSpawnBlocker;
                e.TipIdentityLive = tipIdentityLive;
                e.LiveInUse = IsLiveVesselInUse(e.ClaimedPid, out string why);
                if (why != null)
                    inUseWhy[e.ClaimedPid] = why;
                live[i] = e;
            }

            bool replace = DecideStaleLiveVessels(rec, chains, live, out string reason, out uint decidingPid);
            string sceneLabel = string.IsNullOrEmpty(scene) ? "(none)" : scene;
            if (decidingPid == 0)
                decidingPid = livePids[0];
            if (!replace)
            {
                if (reason == ReasonInUse || reason == ReasonTipCannotSpawn || reason == ReasonTipIdentityLive)
                {
                    inUseWhy.TryGetValue(decidingPid, out string decidingWhy);
                    ParsekLog.Info(Tag,
                        string.Format(ic,
                            "Stale chain-tip vessel kept ({0}) #{1} \"{2}\": live claimed pid={3} reason={4} ({5}) - " +
                            "left as before, the recorded tip state is not applied rec={6} tipPid={7} liveClaimedPids={8}",
                            sceneLabel, index, rec.VesselName ?? "(null)", decidingPid,
                            reason,
                            DescribeKeptDetail(reason, decidingWhy, tipSpawnBlocker),
                            rec.RecordingId, rec.VesselPersistentId, JoinPids(livePids)));
                }
                else
                {
                    StaleVesselEvidence deciding = live[0];
                    for (int i = 0; i < live.Count; i++)
                    {
                        if (live[i].ClaimedPid == decidingPid)
                            deciding = live[i];
                    }
                    ParsekLog.VerboseRateLimited(Tag,
                        "stale-tip-keep|" + rec.RecordingId + "|" + reason,
                        string.Format(ic,
                            "Live vessel on a claimed pid left as it is ({0}) #{1} \"{2}\": claimedPid={3} reason={4} " +
                            "liveLastUT={5} lastClaimUT={6} rec={7} tipPid={8} liveClaimedPids={9}",
                            sceneLabel, index, rec.VesselName ?? "(null)", decidingPid,
                            reason, deciding.LiveLastUT.ToString("F1", ic),
                            LatestClaimUTOverChains(tipChains).ToString("F1", ic), rec.RecordingId,
                            rec.VesselPersistentId, JoinPids(livePids)));
                }
                return null;
            }

            var liveLastUTs = new List<string>(live.Count);
            for (int i = 0; i < live.Count; i++)
                liveLastUTs.Add(live[i].LiveLastUT.ToString("F1", ic));
            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Replacing stale pre-claim vessel ({0}) #{1} \"{2}\": live pid={3} {4} the chain's " +
                    "original before its recorded change; the tip spawns once from its snapshot with identity " +
                    "preserved rec={5} tipPid={6} claimedPids={7} liveLastUT={8} lastClaimUT={9} spawnUT={10}",
                    sceneLabel, index, rec.VesselName ?? "(null)", JoinPids(livePids),
                    livePids.Count > 1 ? "are" : "is",
                    rec.RecordingId, rec.VesselPersistentId, JoinPids(sortedClaimed),
                    string.Join(",", liveLastUTs.ToArray()), LatestClaimUTOverChains(tipChains).ToString("F1", ic),
                    chain.SpawnUT.ToString("F1", ic)));

            var replacement = new StaleVesselReplacement
            {
                RemovedName = rec.VesselName,
                Scene = sceneLabel,
                Index = index
            };
            for (int i = 0; i < livePids.Count; i++)
            {
                uint pid = livePids[i];
                ConfigNode removedSnapshot = RemoveStaleVessel(pid, sceneLabel, out StaleChainVesselFocus focus);
                if (removedSnapshot == null)
                {
                    ParsekLog.Warn(Tag,
                        string.Format(ic,
                            "Stale chain-tip vessel removal failed ({0}) #{1} \"{2}\" pid={3} - left as before " +
                            "rec={4} alreadyRemoved={5}",
                            sceneLabel, index, rec.VesselName ?? "(null)", pid,
                            rec.RecordingId, replacement.Removed.Count > 0 ? JoinPids(replacement.RemovedPids) : "(none)"));
                    // Every claimed vessel goes or none does: put back the ones already removed.
                    if (replacement.Removed.Count > 0)
                    {
                        GhostPlaybackLogic.InvalidateVesselCache();
                        RestoreRemovedVessels(rec, replacement, "a later removal failed");
                    }
                    return null;
                }
                replacement.Removed.Add(new RemovedStaleVessel
                {
                    Pid = pid,
                    Snapshot = removedSnapshot,
                    Focus = focus
                });
            }

            GhostPlaybackLogic.InvalidateVesselCache();
            return replacement;
        }

        /// <summary>
        /// Finishes a replacement after the site's spawn attempt: when the tip produced a
        /// vessel the replacement is done; otherwise (a failed or abandoned spawn, an
        /// exception) every removed vessel is respawned from its pre-removal snapshot with its
        /// identity, so a replacement never loses one. Returns true when it restored them all.
        /// </summary>
        internal static bool CompleteReplacement(Recording rec, StaleVesselReplacement replacement)
        {
            if (replacement == null)
                return false;

            if (rec != null && rec.SpawnedVesselPersistentId != 0)
            {
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Chain-tip replacement complete ({0}) #{1} \"{2}\": removed pid={3}, tip spawned pid={4}",
                        replacement.Scene, replacement.Index, replacement.RemovedName ?? "(null)",
                        JoinPids(replacement.RemovedPids), rec.SpawnedVesselPersistentId));
                return false;
            }

            return RestoreRemovedVessels(rec, replacement, "tip spawned no vessel");
        }

        /// <summary>
        /// Undoes a replacement whose site is about to leave the tip unspawned by this path (it
        /// found the tip's own identity live and adopts it instead): every removed vessel is
        /// respawned from its pre-removal snapshot. Returns true when it restored them all.
        /// </summary>
        internal static bool AbortReplacement(Recording rec, StaleVesselReplacement replacement, string why)
        {
            if (replacement == null)
                return false;
            return RestoreRemovedVessels(rec, replacement, string.IsNullOrEmpty(why) ? "aborted" : why);
        }

        private static string JoinPids(List<uint> pids)
        {
            var parts = new List<string>(pids.Count);
            for (int i = 0; i < pids.Count; i++)
                parts.Add(pids[i].ToString(ic));
            return string.Join(",", parts.ToArray());
        }

        private static string DescribeKeptDetail(string reason, string inUseWhy, string tipSpawnBlocker)
        {
            if (reason == ReasonInUse)
                return inUseWhy ?? "in use";
            if (reason == ReasonTipCannotSpawn)
                return tipSpawnBlocker;
            if (reason == ReasonTipIdentityLive)
                return "a live vessel matches the tip's own pid and launch";
            return reason;
        }

        /// <summary>
        /// Respawns every vessel the replacement removed, last removed first. True when all
        /// came back; each one that did not is an Error naming its pid.
        /// </summary>
        private static bool RestoreRemovedVessels(Recording rec, StaleVesselReplacement replacement, string why)
        {
            int restoredCount = 0;
            for (int i = replacement.Removed.Count - 1; i >= 0; i--)
            {
                RemovedStaleVessel removedVessel = replacement.Removed[i];
                uint restoredPid = RestoreStaleVessel(removedVessel, replacement.Scene);
                if (restoredPid == 0)
                {
                    ParsekLog.Error(Tag,
                        string.Format(ic,
                            "Chain-tip replacement undone ({0}) #{1} \"{2}\": {7}, and the removed vessel pid={3} " +
                            "could not be restored (vesselSpawned={4} abandoned={5} attempts={6}) - reload a save to recover it",
                            replacement.Scene, replacement.Index, replacement.RemovedName ?? "(null)",
                            removedVessel.Pid,
                            rec != null && rec.VesselSpawned, rec != null && rec.SpawnAbandoned,
                            rec != null ? rec.SpawnAttempts : 0, why));
                    continue;
                }

                restoredCount++;
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "Chain-tip replacement undone ({0}) #{1} \"{2}\": {8}; restored the removed vessel " +
                        "pid={3} as restoredPid={4} (vesselSpawned={5} abandoned={6} attempts={7})",
                        replacement.Scene, replacement.Index, replacement.RemovedName ?? "(null)",
                        removedVessel.Pid, restoredPid,
                        rec != null && rec.VesselSpawned, rec != null && rec.SpawnAbandoned,
                        rec != null ? rec.SpawnAttempts : 0, why));
            }
            if (restoredCount > 0)
                GhostPlaybackLogic.InvalidateVesselCache();
            return replacement.Removed.Count > 0 && restoredCount == replacement.Removed.Count;
        }

        private static bool IsReplacementCandidate(Recording rec)
        {
            return rec != null
                && rec.VesselPersistentId != 0
                && !string.IsNullOrEmpty(rec.RecordingId)
                && !rec.VesselSpawned
                && rec.SpawnedVesselPersistentId == 0;
        }

        /// <summary>
        /// The live (or proto-only) vessel with <paramref name="pid"/>: whether it exists, its
        /// launch guid and its <c>lastUT</c> (an unloaded vessel's equals its ProtoVessel's).
        /// </summary>
        private static LiveClaimedVesselProbe ProbeLiveVessel(uint pid)
        {
            if (LiveVesselProbeOverrideForTesting != null)
                return LiveVesselProbeOverrideForTesting(pid);

            try
            {
                return ProbeLiveVesselCore(pid);
            }
            catch (Exception ex) when (IsHeadlessAccessFailure(ex))
            {
                return new LiveClaimedVesselProbe { LastUT = double.NaN };
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static LiveClaimedVesselProbe ProbeLiveVesselCore(uint pid)
        {
            Vessel vessel;
            ProtoVessel proto;
            FindLiveVessel(pid, out vessel, out proto);
            if (vessel != null)
            {
                return new LiveClaimedVesselProbe
                {
                    Exists = true,
                    Guid = vessel.id != Guid.Empty ? vessel.id.ToString("N") : null,
                    LastUT = vessel.lastUT
                };
            }
            if (proto != null)
            {
                return new LiveClaimedVesselProbe
                {
                    Exists = true,
                    Guid = proto.vesselID != Guid.Empty ? proto.vesselID.ToString("N") : null,
                    LastUT = proto.lastUT
                };
            }
            return new LiveClaimedVesselProbe { LastUT = double.NaN };
        }

        /// <summary>
        /// True when the live vessel is in the player's hands: the active vessel (removing it
        /// would yank the camera and the player's control), or a vessel the live recording tree
        /// records (removing it would stamp a destruction terminal on a live recording).
        /// </summary>
        private static bool IsLiveVesselInUse(uint pid, out string why)
        {
            why = null;
            if (LiveVesselInUseOverrideForTesting != null)
            {
                bool inUse = LiveVesselInUseOverrideForTesting(pid);
                if (inUse)
                    why = "in use (test seam)";
                return inUse;
            }

            try
            {
                return IsLiveVesselInUseCore(pid, out why);
            }
            catch (Exception ex) when (IsHeadlessAccessFailure(ex))
            {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsLiveVesselInUseCore(uint pid, out string why)
        {
            why = null;
            Vessel active = FlightGlobals.ActiveVessel;
            if (active != null && active.persistentId == pid)
            {
                why = "the active vessel";
                return true;
            }

            ParsekFlight flight = ParsekFlight.Instance;
            RecordingTree liveTree = flight != null ? flight.ActiveTreeForSerialization : null;
            if (liveTree != null && liveTree.BackgroundMap != null && liveTree.BackgroundMap.ContainsKey(pid))
            {
                why = "recorded by the live tree";
                return true;
            }
            return false;
        }

        private static ConfigNode RemoveStaleVessel(uint pid, string scene, out StaleChainVesselFocus focus)
        {
            focus = default(StaleChainVesselFocus);
            if (RemoveOverrideForTesting != null)
                return RemoveOverrideForTesting(pid, scene);

            try
            {
                return RemoveStaleVesselCore(pid, scene, out focus);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "Stale chain-tip vessel removal threw ({0}) pid={1}: {2}: {3}",
                        scene, pid, ex.GetType().Name, ex.Message));
                return null;
            }
        }

        /// <summary>
        /// Snapshots the stale vessel (crew still aboard, so a restore seats them again), then
        /// removes it through <see cref="ClaimedVesselRemoval"/>. No snapshot, no removal.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ConfigNode RemoveStaleVesselCore(uint pid, string scene, out StaleChainVesselFocus focus)
        {
            focus = default(StaleChainVesselFocus);

            Vessel vessel;
            ProtoVessel proto;
            FindLiveVessel(pid, out vessel, out proto);
            if (vessel == null && proto == null)
                return null;
            if (vessel != null && vessel == FlightGlobals.ActiveVessel)
                return null;

            ConfigNode snapshot;
            if (vessel != null)
            {
                snapshot = VesselSpawner.TryBackupSnapshot(vessel);
            }
            else
            {
                snapshot = new ConfigNode("VESSEL");
                proto.Save(snapshot);
            }
            if (snapshot == null)
                return null;

            if (vessel != null)
                focus = CaptureFocus(vessel);

            var flightState = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.flightState : null;
            ClaimedVesselRemoval.Remove(
                new LiveClaimedVesselRemovalTarget(
                    vessel, proto, flightState != null ? flightState.protoVessels : null),
                "chain-tip-replace " + scene);
            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Stale chain-tip vessel removed ({0}): pid={1} hadVessel={2} navTarget={3} mapFocus={4} tsSelected={5}",
                    scene, pid, vessel != null,
                    focus.WasNavigationTarget, focus.WasMapFocus, focus.WasTrackingStationSelected));
            return snapshot;
        }

        private static uint RestoreStaleVessel(RemovedStaleVessel removedVessel, string scene)
        {
            if (removedVessel == null || removedVessel.Snapshot == null)
                return 0;
            if (RestoreOverrideForTesting != null)
                return RestoreOverrideForTesting(removedVessel.Snapshot, removedVessel.Pid, scene);

            try
            {
                return RestoreStaleVesselCore(removedVessel.Snapshot);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "Stale chain-tip vessel restore threw ({0}) pid={1}: {2}: {3}",
                        scene, removedVessel.Pid, ex.GetType().Name, ex.Message));
                return 0;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static uint RestoreStaleVesselCore(ConfigNode snapshot)
        {
            using (SuppressionGuard.Crew())
            {
                return VesselSpawner.RespawnVessel(snapshot, null, preserveIdentity: true);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FindLiveVessel(uint pid, out Vessel vessel, out ProtoVessel proto)
        {
            vessel = null;
            proto = null;
            var vessels = FlightGlobals.Vessels;
            if (vessels != null)
            {
                for (int i = 0; i < vessels.Count; i++)
                {
                    Vessel v = vessels[i];
                    if (v != null && v.persistentId == pid && !GhostMapPresence.IsGhostMapVessel(v.persistentId))
                    {
                        vessel = v;
                        proto = v.protoVessel;
                        return;
                    }
                }
            }

            var flightState = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.flightState : null;
            if (flightState == null || flightState.protoVessels == null)
                return;
            for (int i = 0; i < flightState.protoVessels.Count; i++)
            {
                ProtoVessel pv = flightState.protoVessels[i];
                if (pv != null && pv.persistentId == pid && !GhostMapPresence.IsGhostMapVessel(pv.persistentId))
                {
                    proto = pv;
                    return;
                }
            }
        }

        private static StaleChainVesselFocus CaptureFocus(Vessel vessel)
        {
            var focus = default(StaleChainVesselFocus);
            if (FlightGlobals.fetch != null && FlightGlobals.fetch.VesselTarget != null)
                focus.WasNavigationTarget = FlightGlobals.fetch.VesselTarget.GetVessel() == vessel;
            if (PlanetariumCamera.fetch != null && vessel.mapObject != null)
                focus.WasMapFocus = PlanetariumCamera.fetch.target == vessel.mapObject;
            if (HighLogic.LoadedScene == GameScenes.TRACKSTATION)
            {
                SpaceTracking tracking = UnityEngine.Object.FindObjectOfType<SpaceTracking>();
                focus.WasTrackingStationSelected = tracking != null && tracking.SelectedVessel == vessel;
            }
            return focus;
        }

        private static bool IsHeadlessAccessFailure(Exception ex)
        {
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                if (current is System.Security.SecurityException
                    || current is MethodAccessException
                    || current is MissingMethodException
                    || current is TypeInitializationException
                    || current is NullReferenceException)
                    return true;
            }
            return false;
        }
    }
}
