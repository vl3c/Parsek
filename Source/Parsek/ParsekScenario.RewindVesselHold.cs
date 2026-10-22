using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    public partial class ParsekScenario
    {
        /// <summary>
        /// Installs the vessel holds the loaded node carries when the policy cell says the save
        /// owns them (a cold load), clears them on a plain rewind (which then computes its own)
        /// and on a Re-Fly start or discard, and keeps memory's on every in-session load. See
        /// <see cref="LoadReconcilePolicy"/>, <see cref="LoadStateCategory.RewindVesselHolds"/>.
        /// </summary>
        private static void LoadRewindVesselHolds(ConfigNode node, EarlyLoadKind earlyLoadKind)
        {
            LoadReconcileDecision decision = LoadReconcilePolicy.DecideEarly(
                earlyLoadKind, LoadStateCategory.RewindVesselHolds);
            switch (decision.Action)
            {
                case LoadReconcileAction.Save:
                    RewindVesselHold.Replace(RewindVesselHold.LoadFrom(node), "load " + earlyLoadKind);
                    return;
                case LoadReconcileAction.Clear:
                    RewindVesselHold.Clear("load " + earlyLoadKind);
                    return;
                default:
                    ParsekLog.Verbose(RewindVesselHold.Tag, string.Format(CultureInfo.InvariantCulture,
                        "Load ({0}): rewind vessel holds kept from memory ({1}): holds={2}",
                        earlyLoadKind, decision.Action, RewindVesselHold.Holds.Count));
                    return;
            }
        }

        /// <summary>
        /// Rewind design 10.13: the plain rewind holds back every vessel of its world that the
        /// committed future uses after <paramref name="rewindUT"/> (<see cref="RewindVesselHold"/>).
        /// Runs in <c>HandleRewindOnLoad</c> after the future-vessel strips and before the spawn
        /// reconcile and the orphaned-crew rescue, so the held vessels leave flightState before
        /// any scene loads them and their crew go Available with the stripped vessels' crew.
        /// Logs one line per held vessel and one summary.
        /// </summary>
        private void ApplyRewindVesselHolds(double rewindUT)
        {
            var flightState = HighLogic.CurrentGame?.flightState;
            if (flightState == null || flightState.protoVessels == null)
            {
                RewindVesselHold.Replace(null, "plain-rewind-no-flightstate");
                return;
            }
            List<ProtoVessel> protoVessels = flightState.protoVessels;
            var survivors = CollectSurvivingVesselIdentities(protoVessels);
            var guidByPid = new Dictionary<uint, string>();
            for (int i = 0; i < survivors.Count; i++)
            {
                if (survivors[i].pid != 0u && !guidByPid.ContainsKey(survivors[i].pid))
                    guidByPid[survivors[i].pid] = survivors[i].guid;
            }
            Func<uint, string> guidOf = pid =>
            {
                string g;
                return guidByPid.TryGetValue(pid, out g) ? g : null;
            };

            List<RecordingTree> trees = RecordingStore.CommittedTrees;
            Dictionary<uint, GhostChain> chains = trees != null && trees.Count > 0
                ? GhostChainWalker.ComputeAllGhostChains(trees, rewindUT)
                : null;
            List<ReFlyInvokeHold.Entry> chainEntries =
                RewindVesselHold.CollectChainHolds(chains, rewindUT, guidOf);

            var supersededClaimants = GhostChainWalker.BuildSupersededClaimantMap(RecordingSupersedes);
            int claimsSkipped;
            List<ReFlyInvokeHold.ChangeClaim> changeClaims = ReFlyInvokeHold.CollectChangeClaims(
                trees, rewindUT, id => supersededClaimants.ContainsKey(id), out claimsSkipped);
            int matchedWithoutFuture;
            List<ReFlyInvokeHold.Entry> futureEntries = ReFlyInvokeHold.Classify(
                survivors, null, EffectiveState.ComputeERS(), EffectiveState.ComputeELS(),
                changeClaims, rewindUT, out matchedWithoutFuture);

            List<ReFlyInvokeHold.Entry> merged = RewindVesselHold.Merge(chainEntries, futureEntries);
            var held = new List<RewindVesselHold.Hold>();
            int notInWorld = 0, claimsReleased = 0;
            for (int m = 0; m < merged.Count; m++)
            {
                ReFlyInvokeHold.Entry entry = merged[m];
                int index = FindHeldProtoVesselIndex(protoVessels, entry.Pid, entry.LaunchGuid);
                if (index < 0)
                {
                    notInWorld++;
                    continue;
                }
                ProtoVessel pv = protoVessels[index];
                ConfigNode snapshot = new ConfigNode(RewindVesselHold.SnapshotNodeName);
                try
                {
                    pv.Save(snapshot);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(RewindVesselHold.Tag, string.Format(CultureInfo.InvariantCulture,
                        "Rewind hold: snapshot of pid={0} threw {1}: {2}; held without a hold ghost shape",
                        entry.Pid, ex.GetType().Name, ex.Message));
                    snapshot = null;
                }
                if (string.IsNullOrEmpty(entry.LaunchGuid) && pv.vesselID != Guid.Empty)
                    entry.LaunchGuid = pv.vesselID.ToString("N");
                protoVessels.RemoveAt(index);

                for (int r = 0; r < entry.FlyingRecordingIds.Count; r++)
                {
                    if (VesselGhoster.ReleaseTipSpawnClaimOnGhostedVessel(
                            FindEffectiveRecordingById(entry.FlyingRecordingIds[r]), entry.Pid, "rewind-hold"))
                        claimsReleased++;
                }

                var hold = new RewindVesselHold.Hold
                {
                    Entry = entry,
                    VesselName = pv.vesselName,
                    Snapshot = snapshot,
                };
                held.Add(hold);
                ParsekLog.Info(RewindVesselHold.Tag, RewindVesselHold.FormatHeldLine(hold));
            }

            RewindVesselHold.Replace(held, "plain-rewind");
            ParsekLog.Info(RewindVesselHold.Tag, string.Format(CultureInfo.InvariantCulture,
                "Rewind vessel hold: survivors={0} held={1} chainHolds={2} futureHolds={3} notInWorld={4} " +
                "matchedWithoutFuture={5} changeClaims={6} claimsSkipped={7} spawnClaimsReleased={8} cutoffUT={9:F1}",
                survivors.Count, held.Count, chainEntries.Count, futureEntries.Count, notInWorld,
                matchedWithoutFuture, changeClaims.Count, claimsSkipped, claimsReleased, rewindUT));
        }

        /// <summary>
        /// The index in <paramref name="protoVessels"/> of the held vessel: persistentId equal and
        /// launch guid not conclusively another launch; -1 when absent. A ghost map ProtoVessel
        /// is never a match.
        /// </summary>
        private static int FindHeldProtoVesselIndex(List<ProtoVessel> protoVessels, uint pid, string launchGuid)
        {
            if (protoVessels == null || pid == 0u)
                return -1;
            for (int i = 0; i < protoVessels.Count; i++)
            {
                ProtoVessel pv = protoVessels[i];
                if (pv == null || pv.persistentId != pid || GhostMapPresence.IsGhostMapVessel(pv.persistentId))
                    continue;
                string guid = pv.vesselID != Guid.Empty ? pv.vesselID.ToString("N") : null;
                if (VesselLaunchIdentity.GuidsConclusivelyDiffer(guid, launchGuid))
                    continue;
                return i;
            }
            return -1;
        }

        private static Recording FindEffectiveRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return null;
            IReadOnlyList<Recording> committed = EffectiveState.ComputeERS();
            for (int i = 0; i < committed.Count; i++)
            {
                if (committed[i] != null && committed[i].RecordingId == recordingId)
                    return committed[i];
            }
            return null;
        }
    }
}
