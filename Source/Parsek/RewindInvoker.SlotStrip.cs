using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Parsek
{
    // Disabled-slot strip and left-alone survey: the disabled sibling slot vessels to strip, the selected-slot presence test and the left-alone name collision warning.
    internal static partial class RewindInvoker
    {
        /// <summary>
        /// Live half of the #16 item-7 disabled-slot supplement: resolves the vessels the
        /// pure <see cref="ResolveDisabledSlotVesselsToStrip"/> names and <c>Die()</c>s them.
        /// The Unity walk stays here; the decision is pure and unit-tested.
        /// </summary>
        private static List<uint> StripDisabledSlotVessels(
            RewindPoint rp, int selectedSlotIndex, uint selectedVesselPid)
        {
            var killed = new List<uint>();
            var liveVessels = FlightGlobals.Vessels;
            if (rp == null || liveVessels == null) return killed;

            var identities = new List<(uint pid, string guid)>();
            for (int i = 0; i < liveVessels.Count; i++)
            {
                Vessel v = liveVessels[i];
                if (v == null) continue;
                if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;
                identities.Add((v.persistentId, AnchorDetector.TryReadLiveVesselGuid(v)));
            }

            var scenario = ParsekScenario.Instance;
            var supersedes = object.ReferenceEquals(null, scenario)
                ? null : scenario.RecordingSupersedes;
            var kill = ResolveDisabledSlotVesselsToStrip(
                rp, selectedSlotIndex,
                RecordingStore.CommittedRecordings, supersedes, identities,
                selectedVesselPid);
            if (kill.Count == 0) return killed;

            var killSet = new HashSet<uint>(kill);
            var killTargets = SnapshotKillTargets(
                liveVessels, killSet, v => v == null ? 0u : v.persistentId);

            var killedNames = new List<string>();
            for (int i = 0; i < killTargets.Count; i++)
            {
                Vessel v = killTargets[i];
                if (v == null) continue;
                string name = v.vesselName ?? "<unnamed>";
                uint pid = v.persistentId;
                try
                {
                    using (SuppressionGuard.Crew())
                    {
                        v.Die();
                    }
                    killed.Add(pid);
                    killedNames.Add(name);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(InvokeTag,
                        $"Disabled-slot strip: Die() threw for v={pid} " +
                        $"name='{name}': {ex.Message}");
                }
            }

            if (killed.Count > 0)
            {
                ParsekLog.Warn(InvokeTag,
                    $"Disabled-slot strip: removed {killed.Count} vessel(s) belonging to DISABLED " +
                    $"child slot(s) of rp={rp.RewindPointId}: [{string.Join(", ", killedNames.ToArray())}] " +
                    "(no PidSlotMap entry, so PostLoadStripper could not see them; they would " +
                    "otherwise be present as a real vessel AND a replaying ghost)");
            }

            return killed;
        }

        /// <summary>
        /// Pure decision for the #16 item-7 supplement: which live vessels belong to a
        /// DISABLED, non-selected child slot of <paramref name="rp"/> and must therefore be
        /// removed alongside the enabled non-selected slots?
        ///
        /// <para>
        /// A slot is disabled when the RP author could not correlate its origin recording to a
        /// live vessel (<c>DisabledReason="no-live-vessel"</c>), which is exactly why it has no
        /// <c>PidSlotMap</c> entry and why <see cref="PostLoadStripper"/> classifies its craft
        /// as unrelated. Matching is done on launch identity against the slot's EFFECTIVE tip
        /// recording (supersede-walked, so a re-flown slot resolves to the recording that
        /// actually stands), never by name: name matching is what the #587 pass needs and it
        /// would delete a player's unrelated same-named craft here.
        /// </para>
        /// <para>
        /// The two identity arms are deliberately asymmetric. The SPAWN arm goes through
        /// <see cref="VesselLaunchIdentity.CandidateMatchesRecording"/> (spawn pids are
        /// KSP-unique; an adoption stamp there already refuses a conclusive guid disagreement).
        /// The SOURCE arm requires a POSITIVE guid match
        /// (<see cref="VesselLaunchIdentity.LiveVesselIsPositivelyRecordedLaunch"/>) rather than
        /// the family's degrading <c>LiveVesselIsRecordedLaunch</c>, because the unknown-guid
        /// fallback direction is destructive HERE: everywhere else pid-only IS the pre-guid
        /// behavior being preserved, but the pre-guid behavior of this pass (the preserved-fleet
        /// world) was to leave the vessel alone, so degrading would aim a pid coincidence at
        /// <c>Die()</c>. The consequence is deliberate: a disabled slot whose recording carries
        /// no guid strips nothing, leaving the #587 third-facet shape (real vessel + replaying
        /// ghost) rather than risking a stranger.
        /// </para>
        /// <para>
        /// Known escape (safe direction, filed as a todo follow-up): a disabled slot's craft that
        /// is live under a DIFFERENT vessel pid than the recording carries - docking pid churn
        /// between recording and RP capture, which is the very reason the author could not
        /// correlate it - matches neither <c>PidSlotMap</c> nor this pass, so it stays both a real
        /// vessel and a replaying ghost. This pass restores the invariant for pid-stable disabled
        /// slots only.
        /// </para>
        /// <para>
        /// Three exclusions keep the pass narrow: a pid the RP maps to ANY slot is left to the
        /// normal strip (it is either the selected vessel or an already-handled sibling), the
        /// selected slot itself is never a candidate even if it is somehow marked disabled, and
        /// <paramref name="selectedVesselPid"/> - the pid the strip actually RESOLVED as the
        /// re-fly target - is protected outright. That last one is not redundant: the stripper
        /// can resolve the selected vessel through <c>RootPartPidMap</c> when KSP regenerated its
        /// vessel pid on load, in which case that pid is absent from <c>PidSlotMap</c>, and a
        /// craft-baked pid collision with a disabled sibling's recording could otherwise name
        /// the vessel the player is about to fly.
        /// </para>
        /// </summary>
        internal static List<uint> ResolveDisabledSlotVesselsToStrip(
            RewindPoint rp,
            int selectedSlotIndex,
            IReadOnlyList<Recording> recordings,
            IReadOnlyList<RecordingSupersedeRelation> supersedes,
            IReadOnlyList<(uint pid, string guid)> liveVessels,
            uint selectedVesselPid = 0u)
        {
            var kill = new List<uint>();
            if (rp == null || rp.ChildSlots == null
                || recordings == null || recordings.Count == 0
                || liveVessels == null || liveVessels.Count == 0)
            {
                return kill;
            }

            for (int s = 0; s < rp.ChildSlots.Count; s++)
            {
                ChildSlot slot = rp.ChildSlots[s];
                if (slot == null || !slot.Disabled) continue;
                if (slot.SlotIndex == selectedSlotIndex) continue;
                if (string.IsNullOrEmpty(slot.OriginChildRecordingId)) continue;

                string effectiveId = slot.EffectiveRecordingId(supersedes);
                Recording rec = null;
                for (int r = 0; r < recordings.Count; r++)
                {
                    if (recordings[r] != null
                        && string.Equals(recordings[r].RecordingId, effectiveId, StringComparison.Ordinal))
                    {
                        rec = recordings[r];
                        break;
                    }
                }
                if (rec == null) continue;

                for (int i = 0; i < liveVessels.Count; i++)
                {
                    uint pid = liveVessels[i].pid;
                    if (pid == 0u) continue;
                    // Never the vessel the strip resolved as the re-fly target.
                    if (selectedVesselPid != 0u && pid == selectedVesselPid) continue;
                    // A mapped pid is the normal strip's business (selected or enabled sibling).
                    if (rp.PidSlotMap != null && rp.PidSlotMap.ContainsKey(pid)) continue;
                    if (kill.Contains(pid)) continue;
                    bool matches =
                        // Spawn arm: a genuine Parsek spawn pid is KSP-unique and an adoption
                        // stamp already refuses a conclusive guid disagreement, so the shared
                        // gate is the right one there.
                        VesselLaunchIdentity.CandidateMatchesRecording(
                            rec, pid, liveVessels[i].guid, matchSource: false, matchSpawn: true)
                        // Source arm: CONCLUSIVE guid match only, no pid-only fallback.
                        || VesselLaunchIdentity.LiveVesselIsPositivelyRecordedLaunch(
                            rec, pid, liveVessels[i].guid);
                    if (matches)
                    {
                        kill.Add(pid);
                    }
                }
            }

            return kill;
        }

        /// <summary>
        /// Pure half of <see cref="IsFlightReady"/>: does this live vessel's
        /// (vesselPid, rootPartPid) pair identify the RP's SELECTED slot? Matches on either
        /// key, mirroring <see cref="ClassifySlotAffinity"/> - the strip resolves the selected
        /// vessel through the root-part map when the vessel pid was regenerated on load, so a
        /// vessel-pid-only readiness test would wait out the full deferral for a slot that is
        /// in fact present.
        /// </summary>
        internal static bool SelectedSlotVesselIsPresent(
            uint vesselPid, uint rootPartPid, ReFlySlotPidSets selectedSlotPids)
        {
            if (vesselPid != 0u
                && selectedSlotPids.SelectedVesselPids != null
                && selectedSlotPids.SelectedVesselPids.Contains(vesselPid))
            {
                return true;
            }
            return rootPartPid != 0u
                && selectedSlotPids.SelectedRootPartPids != null
                && selectedSlotPids.SelectedRootPartPids.Contains(rootPartPid);
        }

        internal static void WarnOnLeftAloneNameCollisions(PostLoadStripResult stripResult)
        {
            if (stripResult.LeftAlonePidNames == null || stripResult.LeftAlonePidNames.Count == 0)
                return;

            // Project to names for the tree-collision intersection. This is
            // still a name-keyed match against committed recordings — the
            // pid scope only constrains the live-survey step downstream.
            IEnumerable<string> treeNames = EnumerateCommittedVesselNames();
            var collisions = PostLoadStripper.FindTreeNameCollisions(
                ProjectLeftAloneNames(stripResult.LeftAlonePidNames), treeNames);
            if (collisions == null || collisions.Count == 0)
                return;

            // PR #577 P2 review: do NOT walk the full live FlightGlobals list
            // when counting survivors. Instead, take the (pid, name) pairs
            // the stripper deliberately left alone (the only set that can
            // legitimately produce a "Strip left N" survivor) and
            // belt-and-suspenders strip out the active re-fly vessel,
            // freshly-stripped pids, and any GhostMap ProtoVessel pid.
            var collisionSet = new HashSet<string>(collisions, StringComparer.Ordinal);
            HashSet<uint> liveVesselPids = SnapshotLiveVesselPids();
            var survey = SurveyLiveLeftAloneCollisions(
                stripResult.LeftAlonePidNames,
                collisionSet,
                liveVesselPids,
                stripResult.SelectedPid,
                stripResult.StrippedPids,
                static pid => GhostMapPresence.IsGhostMapVessel(pid));
            EmitStripLeftAloneWarn(collisions, survey);
        }

        /// <summary>
        /// Pure helper: emit the strip-left-alone diagnostic and the matching
        /// player toast based on a live-vessel survey result. Split out so
        /// unit tests can exercise the message format without
        /// <see cref="FlightGlobals"/> wiring.
        /// </summary>
        internal static void EmitStripLeftAloneWarn(
            List<string> collidingNames,
            LeftAloneSurveyResult survey)
        {
            if (collidingNames == null || collidingNames.Count == 0)
                return;
            int liveVesselCount = survey.LiveCollidingVesselCount;
            if (liveVesselCount <= 0)
            {
                // Post-supplement kill drained every match (or every
                // surviving leftAlone pid was excluded as the active re-fly
                // vessel / freshly stripped / a ghost ProtoVessel). Verbose
                // is the right tier for a "nothing to warn about"
                // diagnostic — keeps a trail without pretending there is
                // an issue.
                ParsekLog.Verbose(InvokeTag,
                    $"Strip left no live pre-existing vessel(s) whose name matches a tree recording " +
                    $"(post-supplement kill drained the colliding set): collidingNames={collidingNames.Count} " +
                    $"[{string.Join(", ", collidingNames.ToArray())}] " +
                    $"(leftAlonePidsAlive={survey.LeftAlonePidsAliveCount} " +
                    $"excludedSelected={survey.ExcludedSelectedCount} " +
                    $"excludedStripped={survey.ExcludedStrippedCount} " +
                    $"excludedGhostMap={survey.ExcludedGhostMapCount})");
                return;
            }

            var stillPresentNames = survey.StillPresentNames ?? new List<string>();
            string joinedLiveNames = string.Join(", ", stillPresentNames.ToArray());
            ParsekLog.Warn(InvokeTag,
                $"Strip left vessels={liveVesselCount} collidingNames={stillPresentNames.Count} " +
                $"(leftAlonePidsAlive={survey.LeftAlonePidsAliveCount} " +
                $"excludedSelected={survey.ExcludedSelectedCount} " +
                $"excludedStripped={survey.ExcludedStrippedCount} " +
                $"excludedGhostMap={survey.ExcludedGhostMapCount}) " +
                $"pre-existing vessel(s) whose name matches a tree recording: [{joinedLiveNames}] — not " +
                $"related to the re-fly, will appear as second Kerbal X-shaped object in scene");
            ShowUserError(
                $"Heads up: pre-existing vessel(s) [{joinedLiveNames}] share a name with your re-fly " +
                $"tree. Any second Kerbal X in scene is NOT a Parsek ghost — it predates the rewind.");
        }

        /// <summary>
        /// Result of <see cref="SurveyLiveLeftAloneCollisions"/>: the
        /// post-supplement, defense-filtered survivor count plus exclusion
        /// counters that ride along into the structured log.
        /// </summary>
        internal struct LeftAloneSurveyResult
        {
            /// <summary>Total leftAlone vessel instances still alive AND in the colliding-name set.</summary>
            public int LiveCollidingVesselCount;

            /// <summary>Distinct names that still have at least one live colliding instance.</summary>
            public List<string> StillPresentNames;

            /// <summary>Count of leftAlone pids still in <see cref="FlightGlobals.Vessels"/> after defensive exclusions.</summary>
            public int LeftAlonePidsAliveCount;

            /// <summary>Count of leftAlone entries excluded because they matched <see cref="PostLoadStripResult.SelectedPid"/>.</summary>
            public int ExcludedSelectedCount;

            /// <summary>Count of leftAlone entries excluded because they matched <see cref="PostLoadStripResult.StrippedPids"/>.</summary>
            public int ExcludedStrippedCount;

            /// <summary>Count of leftAlone entries excluded because <see cref="GhostMapPresence.IsGhostMapVessel(uint)"/> returned true.</summary>
            public int ExcludedGhostMapCount;
        }

        /// <summary>
        /// Pure helper: walk the leftAlone (pid, name) pairs from
        /// <see cref="PostLoadStripResult.LeftAlonePidNames"/>, drop entries
        /// matching the selected/stripped/ghost-map exclusion sets, then
        /// intersect the survivors with the colliding-name set against the
        /// live-pid snapshot. Yields the per-name instance count and the
        /// exclusion counters used in the WARN's structured payload.
        /// </summary>
        /// <param name="leftAlonePidNames">The (pid, name) pairs the stripper left alone.</param>
        /// <param name="collisionNames">Names that match a committed-recording in the re-fly tree.</param>
        /// <param name="liveVesselPids">Pids currently in <c>FlightGlobals.Vessels</c>.</param>
        /// <param name="selectedPid">PR #577 P2 defense: the actively re-flown vessel pid is never a "leftover".</param>
        /// <param name="strippedPids">PR #577 P2 defense: pids the stripper just killed (death may lag the live list by a frame).</param>
        /// <param name="isGhostMapVessel">PR #577 P2 defense: GhostMap ProtoVessel pid predicate (delegate-injected for unit tests).</param>
        internal static LeftAloneSurveyResult SurveyLiveLeftAloneCollisions(
            IReadOnlyList<(uint pid, string name)> leftAlonePidNames,
            HashSet<string> collisionNames,
            HashSet<uint> liveVesselPids,
            uint selectedPid,
            IList<uint> strippedPids,
            Func<uint, bool> isGhostMapVessel)
        {
            var result = new LeftAloneSurveyResult
            {
                LiveCollidingVesselCount = 0,
                StillPresentNames = new List<string>(),
                LeftAlonePidsAliveCount = 0,
                ExcludedSelectedCount = 0,
                ExcludedStrippedCount = 0,
                ExcludedGhostMapCount = 0,
            };
            if (leftAlonePidNames == null || leftAlonePidNames.Count == 0)
                return result;

            HashSet<uint> strippedSet = null;
            if (strippedPids != null && strippedPids.Count > 0)
            {
                strippedSet = new HashSet<uint>();
                for (int i = 0; i < strippedPids.Count; i++) strippedSet.Add(strippedPids[i]);
            }

            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < leftAlonePidNames.Count; i++)
            {
                var (pid, name) = leftAlonePidNames[i];
                if (pid == 0u) continue;
                if (string.IsNullOrEmpty(name)) continue;

                // Defensive exclusions (PR #577 P2 review). All three are
                // belt-and-suspenders: the stripper does not put any of these
                // pids into LeftAlonePidNames in the first place. But if a
                // future code path leaks one in — e.g. selected slot match
                // ambiguity, or strip ordering with a delayed Die() — these
                // explicit filters keep the WARN honest.
                if (selectedPid != 0u && pid == selectedPid)
                {
                    result.ExcludedSelectedCount++;
                    continue;
                }
                if (strippedSet != null && strippedSet.Contains(pid))
                {
                    result.ExcludedStrippedCount++;
                    continue;
                }
                if (isGhostMapVessel != null && isGhostMapVessel(pid))
                {
                    result.ExcludedGhostMapCount++;
                    continue;
                }

                // Liveness check: is this leftAlone pid still in the live
                // vessel list? StripPreExistingDebrisForInPlaceContinuation
                // may have killed it between Strip and this WARN.
                if (liveVesselPids == null || !liveVesselPids.Contains(pid))
                    continue;

                result.LeftAlonePidsAliveCount++;

                if (collisionNames == null || !collisionNames.Contains(name))
                    continue;

                result.LiveCollidingVesselCount++;
                if (seenNames.Add(name)) result.StillPresentNames.Add(name);
            }
            return result;
        }

        private static IEnumerable<string> ProjectLeftAloneNames(
            IList<(uint pid, string name)> leftAlonePidNames)
        {
            if (leftAlonePidNames == null) yield break;
            for (int i = 0; i < leftAlonePidNames.Count; i++)
            {
                string n = leftAlonePidNames[i].name;
                if (!string.IsNullOrEmpty(n)) yield return n;
            }
        }

        private static HashSet<uint> SnapshotLiveVesselPids()
        {
            var pids = new HashSet<uint>();
            IList<Vessel> liveVessels;
            try { liveVessels = FlightGlobals.Vessels; }
            catch { liveVessels = null; }
            if (liveVessels == null) return pids;
            for (int i = 0; i < liveVessels.Count; i++)
            {
                var v = liveVessels[i];
                if (v == null) continue;
                uint pid = 0u;
                try { pid = v.persistentId; } catch { /* defensive */ }
                if (pid != 0u) pids.Add(pid);
            }
            return pids;
        }

        private static IEnumerable<string> EnumerateCommittedVesselNames()
        {
            var scenario = ParsekScenario.Instance;
            if (scenario == null) yield break;

            // Pull names straight from the committed-recording list. Any recording
            // in an active tree is in scope here; the goal is a cheap diagnostic,
            // not a strict ERS-filtered view.
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) yield break;

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null) continue;
                string name = rec.VesselName;
                if (!string.IsNullOrEmpty(name)) yield return name;
            }
        }
    }
}
