using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Design section 2.12 Rule 3, "A vessel the committed future uses is held back at
    /// invoke". A Re-Fly loads its rewind point's quicksave, which brings back every vessel
    /// that was live at the rewind UT. The post-load strip removes the slot's siblings; an
    /// unrelated survivor stays. When a committed recording flies that survivor or recovers
    /// it after the rewind UT, the survivor is handed to the existing claimed-vessel hold
    /// (<c>VesselGhoster</c> plus <c>ParsekFlight.UpdateClaimedVesselHoldGhosts</c>): it is
    /// despawned, drawn as a hold ghost in its rewind-save shape until its first claim, and
    /// from then on the committed recording plays it. Its committed rows stay; nothing is
    /// tombstoned at invoke. This replaces the invoke-time retirement of such a vessel's
    /// recovery rows (the old Step 3b of <c>RewindInvoker.ConsumePostLoad</c>).
    ///
    /// <para>
    /// A vessel a committed recording CHANGES after the rewind UT (a dock onto it, a board)
    /// is held here too (<see cref="EvidenceChanges"/>, from <see cref="CollectChangeClaims"/>).
    /// The ghost-chain feed (<c>ParsekFlight.EvaluateAndApplyGhostChains</c>) also holds most
    /// such vessels, but it drops every claim of a tree whose leaves have all ended and of a
    /// chain that ends destroyed or recovered, so on its own a vessel a committed flight docks
    /// with before that flight is lost stayed live and the attempt could change it. A vessel
    /// the chain feed already holds is left to it.
    /// </para>
    ///
    /// <para>
    /// The held entries are saved with the session marker
    /// (<see cref="ReFlySessionMarker.InvokeHolds"/>), so a reload during the session redraws
    /// the hold from the rewind point's own save even though the vessel is no longer in the
    /// world.
    /// </para>
    ///
    /// <para>
    /// Identity is POSITIVE launch identity
    /// (<see cref="VesselLaunchIdentity.LiveVesselIsPositivelyRecordedLaunch"/>): pid equal
    /// and both launch guids known and equal. Holding a vessel removes it from the world, so
    /// a relaunch of the same craft (a craft-baked pid) must never match on pid alone.
    /// </para>
    /// </summary>
    internal static class ReFlyInvokeHold
    {
        internal const string EvidenceRecovery = "recovery";
        internal const string EvidenceFlies = "flies";
        internal const string EvidenceChanges = "changes";

        /// <summary>Child node of the session marker holding one <see cref="Entry"/>.</summary>
        internal const string EntryNodeName = "INVOKE_HOLD";

        /// <summary>
        /// One dock or board onto a vessel by a committed recording: the vessel's pid and the
        /// launch the claim pertains to, the claim UT and the claiming recording.
        /// </summary>
        internal sealed class ChangeClaim
        {
            public uint Pid;
            public string LaunchGuid;
            public double UT;
            public string RecordingId;
            public string Kind;
        }

        /// <summary>One survivor the committed future uses.</summary>
        internal sealed class Entry
        {
            public uint Pid;
            public string LaunchGuid;

            /// <summary>The committed recording whose future use decided the hold (the earliest claim).</summary>
            public string RecordingId;

            /// <summary>UT of the first committed use after the rewind UT: the hold ghost shows until then.</summary>
            public double FirstClaimUT;

            /// <summary><see cref="EvidenceRecovery"/>, <see cref="EvidenceFlies"/> or <see cref="EvidenceChanges"/>.</summary>
            public string Evidence;

            /// <summary>
            /// Recordings of this launch with trajectory after the rewind UT: their end spawn
            /// claim on the held pid is released, so the vessel comes back at their end.
            /// </summary>
            public List<string> FlyingRecordingIds = new List<string>();
        }

        /// <summary>
        /// Pure: the survivors the committed future uses after <paramref name="rewindUT"/>.
        /// <paramref name="excludedPids"/> are never held (the re-flown vessel, the stripped
        /// vessels). A recording of the survivor's launch counts when it ends after the rewind
        /// UT (it flies the vessel then; first claim = its start, or the rewind UT when it is
        /// already playing) or carries recovery evidence after it (a <c>Recovered</c> terminal
        /// ending after the rewind UT, or a post-rewind recovery row; first claim = the
        /// earliest recovery UT). <paramref name="matchedWithoutFuture"/> counts survivors that
        /// matched a recording with neither.
        /// </summary>
        internal static List<Entry> Classify(
            IReadOnlyList<(uint pid, string guid)> survivors,
            ICollection<uint> excludedPids,
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyList<GameAction> ledgerActions,
            double rewindUT,
            out int matchedWithoutFuture)
        {
            return Classify(survivors, excludedPids, committedRecordings, ledgerActions,
                null, rewindUT, out matchedWithoutFuture);
        }

        /// <summary>
        /// <see cref="Classify(IReadOnlyList{ValueTuple{uint, string}}, ICollection{uint}, IReadOnlyList{Recording}, IReadOnlyList{GameAction}, double, out int)"/>
        /// plus the docks and boards of <paramref name="changeClaims"/>: a survivor a claim
        /// after the rewind UT names (pid equal, launch guids known and equal) is held with
        /// <see cref="EvidenceChanges"/> until the claim's UT, unless an earlier use decides it.
        /// </summary>
        internal static List<Entry> Classify(
            IReadOnlyList<(uint pid, string guid)> survivors,
            ICollection<uint> excludedPids,
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyList<GameAction> ledgerActions,
            IReadOnlyList<ChangeClaim> changeClaims,
            double rewindUT,
            out int matchedWithoutFuture)
        {
            matchedWithoutFuture = 0;
            var result = new List<Entry>();
            if (survivors == null || survivors.Count == 0) return result;
            bool hasRecordings = committedRecordings != null && committedRecordings.Count > 0;
            bool hasClaims = changeClaims != null && changeClaims.Count > 0;
            if (!hasRecordings && !hasClaims) return result;
            if (double.IsNaN(rewindUT) || double.IsInfinity(rewindUT)) return result;

            var seen = new HashSet<uint>();
            for (int s = 0; s < survivors.Count; s++)
            {
                uint pid = survivors[s].pid;
                string guid = survivors[s].guid;
                if (pid == 0u || !seen.Add(pid)) continue;
                if (excludedPids != null && excludedPids.Contains(pid)) continue;

                Entry entry = null;
                bool matched = false;
                for (int r = 0; hasRecordings && r < committedRecordings.Count; r++)
                {
                    Recording rec = committedRecordings[r];
                    if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) continue;
                    if (!VesselLaunchIdentity.LiveVesselIsPositivelyRecordedLaunch(rec, pid, guid)) continue;
                    matched = true;

                    double claimUT;
                    string evidence;
                    if (!TryFirstFutureUse(rec, ledgerActions, rewindUT, out claimUT, out evidence))
                        continue;

                    if (entry == null)
                        entry = new Entry { Pid = pid, LaunchGuid = guid, FirstClaimUT = double.PositiveInfinity };
                    if (evidence == EvidenceFlies)
                        entry.FlyingRecordingIds.Add(rec.RecordingId);
                    if (claimUT < entry.FirstClaimUT)
                    {
                        entry.FirstClaimUT = claimUT;
                        entry.RecordingId = rec.RecordingId;
                        entry.Evidence = evidence;
                    }
                }

                for (int c = 0; hasClaims && c < changeClaims.Count; c++)
                {
                    ChangeClaim claim = changeClaims[c];
                    if (claim == null || claim.Pid != pid) continue;
                    if (!GuidsPositivelyEqual(claim.LaunchGuid, guid)) continue;
                    matched = true;
                    if (double.IsNaN(claim.UT) || claim.UT <= rewindUT) continue;
                    if (entry == null)
                        entry = new Entry { Pid = pid, LaunchGuid = guid, FirstClaimUT = double.PositiveInfinity };
                    if (claim.UT < entry.FirstClaimUT)
                    {
                        entry.FirstClaimUT = claim.UT;
                        entry.RecordingId = claim.RecordingId;
                        entry.Evidence = EvidenceChanges;
                    }
                }

                if (entry != null)
                    result.Add(entry);
                else if (matched)
                    matchedWithoutFuture++;
            }
            return result;
        }

        /// <summary>
        /// Pure: every Dock and Board branch point of <paramref name="committedTrees"/> after
        /// <paramref name="rewindUT"/> that names a target vessel, as a <see cref="ChangeClaim"/>.
        /// The claiming recording is the branch point's first parent (the vessel that docked or
        /// the kerbal that boarded), as in <c>GhostChainWalker</c>; the claim's launch is the
        /// stamped target guid, else the target pid's guid in the claiming tree. A claim whose
        /// claiming recording <paramref name="claimantSkipped"/> names (it no longer plays: a
        /// Re-Fly superseded it, or it is the session's own replaced flight) is dropped and
        /// counted in <paramref name="skipped"/>. Unlike the chain walker it keeps the claims of
        /// trees whose leaves have all ended.
        /// </summary>
        internal static List<ChangeClaim> CollectChangeClaims(
            IReadOnlyList<RecordingTree> committedTrees,
            double rewindUT,
            Func<string, bool> claimantSkipped,
            out int skipped)
        {
            skipped = 0;
            var result = new List<ChangeClaim>();
            if (committedTrees == null || double.IsNaN(rewindUT) || double.IsInfinity(rewindUT))
                return result;
            for (int t = 0; t < committedTrees.Count; t++)
            {
                RecordingTree tree = committedTrees[t];
                if (tree?.BranchPoints == null) continue;
                for (int b = 0; b < tree.BranchPoints.Count; b++)
                {
                    BranchPoint bp = tree.BranchPoints[b];
                    if (bp == null || bp.TargetVesselPersistentId == 0u) continue;
                    if (bp.Type != BranchPointType.Dock && bp.Type != BranchPointType.Board) continue;
                    if (!(bp.UT > rewindUT)) continue;
                    string claimant = bp.ParentRecordingIds != null && bp.ParentRecordingIds.Count > 0
                        ? bp.ParentRecordingIds[0] : null;
                    if (string.IsNullOrEmpty(claimant)) continue;
                    if (claimantSkipped != null && claimantSkipped(claimant))
                    {
                        skipped++;
                        continue;
                    }
                    result.Add(new ChangeClaim
                    {
                        Pid = bp.TargetVesselPersistentId,
                        LaunchGuid = MissionCrossTreeDock.ResolveClaimLaunchGuid(
                            tree, bp.TargetVesselPersistentId, bp.TargetVesselGuid),
                        UT = bp.UT,
                        RecordingId = claimant,
                        Kind = bp.Type == BranchPointType.Dock ? "dock" : "board",
                    });
                }
            }
            return result;
        }

        private static bool GuidsPositivelyEqual(string a, string b)
        {
            string na = VesselLaunchIdentity.NormalizeGuid(a);
            string nb = VesselLaunchIdentity.NormalizeGuid(b);
            return na != null && nb != null && string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Writes <paramref name="entry"/> as an <see cref="EntryNodeName"/> child of <paramref name="parent"/>.</summary>
        internal static void SaveEntry(ConfigNode parent, Entry entry)
        {
            if (parent == null || entry == null || entry.Pid == 0u) return;
            var ic = CultureInfo.InvariantCulture;
            ConfigNode node = parent.AddNode(EntryNodeName);
            node.AddValue("pid", entry.Pid.ToString(ic));
            if (!string.IsNullOrEmpty(entry.LaunchGuid))
                node.AddValue("launchGuid", entry.LaunchGuid);
            if (!string.IsNullOrEmpty(entry.RecordingId))
                node.AddValue("recordingId", entry.RecordingId);
            node.AddValue("firstClaimUT", entry.FirstClaimUT.ToString("R", ic));
            if (!string.IsNullOrEmpty(entry.Evidence))
                node.AddValue("evidence", entry.Evidence);
            if (entry.FlyingRecordingIds != null)
            {
                for (int i = 0; i < entry.FlyingRecordingIds.Count; i++)
                {
                    if (!string.IsNullOrEmpty(entry.FlyingRecordingIds[i]))
                        node.AddValue("flyingRecordingId", entry.FlyingRecordingIds[i]);
                }
            }
        }

        /// <summary>Reads one <see cref="EntryNodeName"/> node; null when it names no vessel or no claim UT.</summary>
        internal static Entry LoadEntry(ConfigNode node)
        {
            if (node == null) return null;
            var ic = CultureInfo.InvariantCulture;
            uint pid;
            double firstClaimUT;
            if (!uint.TryParse(node.GetValue("pid"), NumberStyles.Integer, ic, out pid) || pid == 0u)
                return null;
            if (!double.TryParse(node.GetValue("firstClaimUT"), NumberStyles.Float, ic, out firstClaimUT)
                || double.IsNaN(firstClaimUT))
                return null;
            var entry = new Entry
            {
                Pid = pid,
                LaunchGuid = NullIfEmpty(node.GetValue("launchGuid")),
                RecordingId = NullIfEmpty(node.GetValue("recordingId")),
                FirstClaimUT = firstClaimUT,
                Evidence = NullIfEmpty(node.GetValue("evidence")),
            };
            string[] flying = node.GetValues("flyingRecordingId");
            if (flying != null)
            {
                for (int i = 0; i < flying.Length; i++)
                {
                    if (!string.IsNullOrEmpty(flying[i]))
                        entry.FlyingRecordingIds.Add(flying[i]);
                }
            }
            return entry;
        }

        private static string NullIfEmpty(string s)
        {
            return string.IsNullOrEmpty(s) ? null : s;
        }

        /// <summary>
        /// Pure: the VESSEL node of <paramref name="flightState"/> for a held entry's vessel
        /// (persistentId equal, launch guid not conclusively another launch), or null.
        /// </summary>
        internal static ConfigNode FindHeldVesselNode(ConfigNode flightState, uint pid, string launchGuid)
        {
            if (flightState == null || pid == 0u) return null;
            ConfigNode[] vessels = flightState.GetNodes("VESSEL");
            for (int i = 0; i < vessels.Length; i++)
            {
                uint nodePid;
                if (!uint.TryParse(vessels[i].GetValue("persistentId"), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out nodePid) || nodePid != pid)
                    continue;
                if (VesselLaunchIdentity.GuidsConclusivelyDiffer(
                        VesselLaunchIdentity.TryReadVesselGuid(vessels[i]), launchGuid))
                    continue;
                return vessels[i];
            }
            return null;
        }

        /// <summary>
        /// Pure: what a reload does with one saved hold entry. <c>claim-passed</c> when the
        /// clock is at or past its first claim (the committed recording plays the vessel now),
        /// <c>already-held</c> when the chain feed holds it, <c>other-launch</c> when a live
        /// vessel with that pid is conclusively another launch, <c>hold-live</c> when the
        /// vessel is live (despawn it as the invoke did), <c>no-snapshot</c> when it is gone and
        /// the rewind point's save has no copy, else <c>restore</c>.
        /// </summary>
        internal static string DecideReloadAction(
            Entry entry, double currentUT, bool alreadyGhosted, bool live, string liveGuid,
            bool hasRewindSaveCopy)
        {
            if (entry == null || entry.Pid == 0u) return "invalid";
            if (!double.IsNaN(currentUT) && currentUT >= entry.FirstClaimUT) return "claim-passed";
            if (alreadyGhosted) return "already-held";
            if (live)
            {
                if (VesselLaunchIdentity.GuidsConclusivelyDiffer(liveGuid, entry.LaunchGuid))
                    return "other-launch";
                return "hold-live";
            }
            return hasRewindSaveCopy ? "restore" : "no-snapshot";
        }

        /// <summary>
        /// Pure: the first UT after <paramref name="rewindUT"/> at which <paramref name="rec"/>
        /// uses its vessel, and why. False when it uses it no more after the rewind UT.
        /// </summary>
        internal static bool TryFirstFutureUse(
            Recording rec, IReadOnlyList<GameAction> ledgerActions, double rewindUT,
            out double claimUT, out string evidence)
        {
            claimUT = double.NaN;
            evidence = null;
            if (rec == null) return false;

            bool terminalRecovered = rec.TerminalStateValue == TerminalState.Recovered;
            if (rec.EndUT > rewindUT && !terminalRecovered)
            {
                claimUT = Math.Max(rec.StartUT, rewindUT);
                evidence = EvidenceFlies;
                return true;
            }

            double earliestRecovery = double.PositiveInfinity;
            if (terminalRecovered && rec.EndUT > rewindUT)
                earliestRecovery = Math.Max(rec.StartUT, rewindUT);
            if (ledgerActions != null
                && (terminalRecovered
                    || ResurrectionRetirementEligibility.VesselOutlivedTerminal(rec.TerminalStateValue)))
            {
                for (int a = 0; a < ledgerActions.Count; a++)
                {
                    GameAction action = ledgerActions[a];
                    if (!RecoveredRecordingEvidence.IsRecoveryRow(action, rec.RecordingId, rewindUT))
                        continue;
                    if (action.UT < earliestRecovery)
                        earliestRecovery = action.UT;
                }
            }
            if (double.IsPositiveInfinity(earliestRecovery))
                return false;
            claimUT = earliestRecovery;
            evidence = EvidenceRecovery;
            return true;
        }
    }
}
