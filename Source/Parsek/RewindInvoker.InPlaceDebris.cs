using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Parsek
{
    // In-place continuation debris: the pre-existing debris kill set, its protected pids and left-alone names, and the strip that removes it.
    internal static partial class RewindInvoker
    {
        /// <summary>
        /// Bug #587: pre-existing debris-vessel-supplement to <see cref="PostLoadStripper.Strip"/>
        /// for the in-place continuation Re-Fly path. <see cref="PostLoadStripper.Strip"/>
        /// keys on <see cref="RewindPoint.PidSlotMap"/> and only kills siblings registered in
        /// that map; pre-existing debris vessels carried in the rewind quicksave's
        /// <c>protoVessels</c> from prior career flights are left in scene by design.
        /// For an in-place continuation Re-Fly, however, a leftover vessel can be the
        /// quicksave copy of a vessel the actively re-flown tree recorded as Destroyed (or is
        /// superseding), and KSP-stock patched-conics treats it as an encounter candidate --
        /// producing the phantom "Kerbin Encounter T+" + 50x warp cap that the playtest
        /// captured.
        /// <para>
        /// Returns the list of left-alone PIDs that should be killed, from
        /// <paramref name="leftAlonePids"/> (a DEBRIS-only survey, see
        /// <see cref="IsDebrisKillSurveyCandidate"/>):
        /// <list type="number">
        /// <item>by launch identity: the vessel is the same launch as a Destroyed-terminal or
        /// suppressed-subtree recording in the marker's tree (parent-chain recordings stay
        /// name-only, see below)
        /// (<see cref="VesselLaunchIdentity.CandidateMatchesRecording"/> over the recorded
        /// source vessel and its spawn: pid plus a launch guid that does not conclusively
        /// differ, the same gate the rewind strip uses), whatever either is named, so a
        /// renamed vessel's quicksave copy is still removed;</item>
        /// <item>by the prior-career relic rule: a debris vessel of ANOTHER launch whose name
        /// equals a kill-eligible recording's name (debris of an earlier flight of the same
        /// craft, the playtest's phantom-encounter cause). No identity reaches those; the name
        /// is a deliberate, debris-scoped heuristic here, never an identity claim. It is also
        /// the ONLY rule for parent-chain recordings, which keep their original name matching.</item>
        /// </list>
        /// The <paramref name="protectedPids"/> parameter excludes the actively re-flown
        /// vessel + the marker's recording vessel pid so #573's strip-kill protection is
        /// preserved, and a candidate that is the active Re-Fly recording's own launch is
        /// never killed.
        /// </para>
        /// <para>
        /// Pure static; the caller is responsible for the actual <c>Vessel.Die()</c>
        /// invocations. The decision is keyed on (a) marker is in-place continuation,
        /// (b) recording is in the marker's tree, (c) recording's terminal state is
        /// Destroyed -- so a future career run with an alive "Kerbal X Debris" in
        /// orbit at rewind-point time cannot trip the kill (the recording would not
        /// be Destroyed-terminal there).
        /// </para>
        /// </summary>
        /// <param name="marker">Live re-fly marker. Returns empty list when null or not in-place.</param>
        /// <param name="trees">Committed trees from <c>RecordingStore.CommittedTrees</c>.</param>
        /// <param name="leftAlonePids">Left-alone vessels: pid, launch guid (null when
        /// unknown) and name (the name is only logged).</param>
        /// <param name="protectedPids">Pids that must NOT be killed (selected vessel + marker active).</param>
        /// <param name="sessionSuppressedRecordingIds">Recording ids in the
        /// session-suppressed subtree (typically
        /// <see cref="EffectiveState.ComputeSessionSuppressedSubtree"/>'s
        /// result for the live marker). Bug #587 follow-up: the original
        /// fix only killed leftovers matching <c>Destroyed</c>-terminal
        /// recordings, but the 2026-04-25 playtest captured a non-Destroyed
        /// pre-existing vessel in the supersede subtree (a phantom that
        /// would be replaced by the re-fly's new tail) being kept alive in
        /// scene. Recordings in the suppressed subtree are now
        /// also kill-eligible. Pass <c>null</c> in tests when only the
        /// Destroyed-terminal predicate is exercised — backwards-compatible
        /// with the original signature.</param>
        internal static List<uint> ResolveInPlaceContinuationDebrisToKill(
            ReFlySessionMarker marker,
            IReadOnlyList<RecordingTree> trees,
            IReadOnlyList<(uint pid, string guid, string vesselName)> leftAlonePids,
            HashSet<uint> protectedPids,
            IReadOnlyCollection<string> sessionSuppressedRecordingIds = null)
        {
            var kill = new List<uint>();
            if (marker == null) return kill;
            // Accept both legacy in-place (active == origin) and the
            // post-#734 fork shape (InPlaceContinuation flag, active != origin).
            // Both keep the player on the SAME physical vessel as origin, so
            // the parent-chain doubled-vessel cleanup applies identically.
            // Pure placeholder Re-Fly (the player flies a fresh vessel) is
            // skipped -- that active vessel is legitimately alive in scene.
            if (!ReFlySessionMarker.IsInPlaceContinuation(marker))
                return kill;
            if (string.IsNullOrEmpty(marker.TreeId)) return kill;
            if (trees == null || trees.Count == 0) return kill;
            if (leftAlonePids == null || leftAlonePids.Count == 0) return kill;

            // Locate the marker's tree.
            RecordingTree markerTree = null;
            for (int i = 0; i < trees.Count; i++)
            {
                if (trees[i] == null) continue;
                if (string.Equals(trees[i].Id, marker.TreeId, StringComparison.Ordinal))
                {
                    markerTree = trees[i];
                    break;
                }
            }
            if (markerTree == null) return kill;

            // Hoist the suppressed-subtree input into a local HashSet for O(1)
            // membership checks. EffectiveState.ComputeSessionSuppressedSubtree
            // returns a defensive HashSet copy; tests pass a HashSet or array.
            // The local rebuild also handles the IReadOnlyCollection-without-
            // Contains-method case at no measurable cost (subtree size is
            // typically <10 ids).
            HashSet<string> suppressedSet = null;
            if (sessionSuppressedRecordingIds != null && sessionSuppressedRecordingIds.Count > 0)
            {
                suppressedSet = new HashSet<string>(sessionSuppressedRecordingIds, StringComparer.Ordinal);
            }

            // Build the kill-eligible recording set: Destroyed-terminal recordings
            // (the original #587 predicate) PLUS any recording in the
            // session-suppressed subtree (#587 follow-up) PLUS the active Re-Fly
            // recording's parent chain. Suppressed-subtree recordings are being
            // superseded by the in-place continuation - a pre-existing vessel that IS
            // one of their vessels is a phantom from the old timeline the re-fly is
            // overwriting, and would produce the "second Kerbal X-shaped object" the
            // user sees.
            //
            // CRITICAL: the active Re-Fly recording is never kill-eligible, and neither
            // is any candidate that is its own launch. The active recording is itself
            // the head of the suppressed subtree, so it would otherwise be picked up by
            // the suppressed-subtree predicate. The protected-pids check below already
            // shields the LIVE vessel; the identity skip is the second layer.
            //
            // The identity arm runs ONLY against Destroyed-terminal and suppressed-subtree
            // recordings (identityEligible). A parent-chain recording keeps its original
            // name-only matching: the parent chain's earliest leg shares its launch with
            // vessels that split off later under another name (a root R0 and its debris
            // continuation D keep R0's pid + guid), so an identity match there would Die()
            // a vessel the name rule never reached.
            var killEligible = new List<Recording>();
            var identityEligible = new List<Recording>();
            var killEligibleNames = new HashSet<string>(StringComparer.Ordinal);
            Recording activeRec = null;
            int destroyedTerminalRecs = 0;
            int suppressedSubtreeRecs = 0;
            int parentChainRecs = 0;
            foreach (var rec in markerTree.Recordings.Values)
            {
                if (rec == null) continue;
                if (string.Equals(rec.RecordingId, marker.ActiveReFlyRecordingId,
                        StringComparison.Ordinal))
                {
                    activeRec = rec;
                    continue; // never kill the active Re-Fly target's own vessel
                }

                bool destroyedTerminal = rec.TerminalStateValue == TerminalState.Destroyed;
                bool inSuppressedSubtree = suppressedSet != null
                    && !string.IsNullOrEmpty(rec.RecordingId)
                    && suppressedSet.Contains(rec.RecordingId);
                bool inActiveParentChain = false;
                if (!string.IsNullOrEmpty(rec.RecordingId))
                {
                    string walkTrace;
                    inActiveParentChain = GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                        rec.RecordingId,
                        marker.ActiveReFlyRecordingId,
                        trees,
                        out walkTrace);
                }

                if (!destroyedTerminal && !inSuppressedSubtree && !inActiveParentChain) continue;

                killEligible.Add(rec);
                if (destroyedTerminal || inSuppressedSubtree)
                    identityEligible.Add(rec);
                if (!string.IsNullOrEmpty(rec.VesselName))
                    killEligibleNames.Add(rec.VesselName);
                if (destroyedTerminal) destroyedTerminalRecs++;
                if (inSuppressedSubtree) suppressedSubtreeRecs++;
                if (inActiveParentChain) parentChainRecs++;
            }
            if (killEligible.Count == 0) return kill;

            int killedByIdentity = 0;
            int killedByDebrisName = 0;
            int keptActiveIdentity = 0;
            for (int i = 0; i < leftAlonePids.Count; i++)
            {
                var (pid, guid, name) = leftAlonePids[i];
                if (pid == 0u) continue;
                if (protectedPids != null && protectedPids.Contains(pid)) continue;
                if (activeRec != null
                    && VesselLaunchIdentity.CandidateMatchesRecording(
                        activeRec, pid, guid, matchSource: true, matchSpawn: true))
                {
                    keptActiveIdentity++;
                    continue;
                }

                // Identity first: the quicksave copy of a Destroyed-terminal or
                // suppressed-subtree recording's own vessel (or of its spawn), whatever
                // either is called now. Parent-chain recordings are name-only (above).
                if (VesselLaunchIdentity.FindMatchingRecording(
                        identityEligible, pid, guid, matchSource: true, matchSpawn: true) != null)
                {
                    kill.Add(pid);
                    killedByIdentity++;
                    continue;
                }

                // Prior-career relic rule (#587): the survey admits DEBRIS only
                // (IsDebrisKillSurveyCandidate), and debris of an EARLIER launch of the
                // same craft carries the same "<Craft> Debris" name on a near-identical
                // orbit - the phantom-encounter cause. Those vessels are not this tree's
                // launch, so no identity reaches them; the name is the deliberate,
                // debris-scoped heuristic for them, never an identity claim.
                if (!string.IsNullOrEmpty(name) && killEligibleNames.Contains(name))
                {
                    kill.Add(pid);
                    killedByDebrisName++;
                }
            }

            if ((kill.Count > 0 || keptActiveIdentity > 0) && !ParsekLog.SuppressLogging)
            {
                ParsekLog.Verbose(InvokeTag,
                    $"ResolveInPlaceContinuationDebrisToKill: matched {kill.Count} pid(s) " +
                    $"(byLaunchIdentity={killedByIdentity} byPriorCareerDebrisName={killedByDebrisName}) " +
                    $"against {killEligible.Count} kill-eligible recording(s) " +
                    $"(identityEligible={identityEligible.Count}) " +
                    $"(destroyedTerminal={destroyedTerminalRecs} suppressedSubtree={suppressedSubtreeRecs} " +
                    $"parentChain={parentChainRecs}) " +
                    $"keptActiveLaunch={keptActiveIdentity} " +
                    $"in tree '{markerTree.Id}'");
            }

            return kill;
        }

        /// <summary>
        /// Bug #587 follow-up (PR #558 P2 review): build a stable snapshot of
        /// kill targets from a live <c>IList</c> before any
        /// <c>Vessel.Die()</c> calls run. Walking the live
        /// <c>FlightGlobals.Vessels</c> while calling <c>Die()</c> shifts
        /// subsequent indices and can skip consecutive matching debris -- the
        /// exact multi-debris pattern this PR claims to fix. The snapshot
        /// captures every entry whose <paramref name="pidGetter"/> returns a
        /// pid in <paramref name="killPids"/>, so subsequent iteration of the
        /// returned list is unaffected by source-list mutations.
        ///
        /// <para>Generic over <typeparamref name="T"/> so unit tests can pin
        /// the contract against a fake type without depending on KSP's
        /// <c>Vessel</c>.</para>
        /// </summary>
        /// <param name="liveSource">Live source list (typically
        /// <c>FlightGlobals.Vessels</c>).</param>
        /// <param name="killPids">Pids to kill.</param>
        /// <param name="pidGetter">Extracts the pid for an item; null items
        /// pass-through and are skipped.</param>
        internal static List<T> SnapshotKillTargets<T>(
            IList<T> liveSource,
            HashSet<uint> killPids,
            Func<T, uint> pidGetter) where T : class
        {
            var result = new List<T>();
            if (liveSource == null || killPids == null || killPids.Count == 0
                || pidGetter == null)
            {
                return result;
            }
            for (int i = 0; i < liveSource.Count; i++)
            {
                T item = liveSource[i];
                if (item == null) continue;
                uint pid = pidGetter(item);
                if (pid == 0u) continue;
                if (!killPids.Contains(pid)) continue;
                result.Add(item);
            }
            return result;
        }

        /// <summary>
        /// Builds the protected-pid set passed to
        /// <see cref="ResolveInPlaceContinuationDebrisToKill"/> at the
        /// in-place continuation strip-supplement seam. Three sources:
        /// <list type="bullet">
        ///   <item><description><see cref="PostLoadStripResult.SelectedPid"/>
        ///     — #573 contract, the actively re-flown vessel.</description></item>
        ///   <item><description>The marker's active recording's
        ///     <see cref="Recording.VesselPersistentId"/> from
        ///     <paramref name="committedRecs"/>. Same pid as Selected for
        ///     in-place continuation today, but kept as belt-and-suspenders
        ///     against a future code path that diverges them.</description></item>
        ///   <item><description><see cref="PostLoadStripResult.PreservedFlagPids"/>
        ///     — #fix-refly-preserve-flag-vessels follow-up: planted-flag
        ///     vessels the stripper deliberately left alone must also be
        ///     immune to the secondary kill walk, otherwise a flag whose
        ///     <c>vesselName</c> collides with a recording's
        ///     <c>VesselName</c> (player-typed labels, or KSP's default
        ///     kerbal-name-derived label clashing with an EVA recording)
        ///     would still be despawned by <c>Vessel.Die()</c>, silently
        ///     erasing the FlagPlant career milestone.</description></item>
        /// </list>
        /// Pure static so tests can pin the contract without a live KSP scene.
        /// </summary>
        internal static HashSet<uint> BuildProtectedPidsForInPlaceContinuation(
            PostLoadStripResult stripResult,
            ReFlySessionMarker marker,
            IReadOnlyList<Recording> committedRecs)
        {
            var protectedPids = new HashSet<uint>();
            if (stripResult.SelectedPid != 0u)
                protectedPids.Add(stripResult.SelectedPid);
            if (marker != null && committedRecs != null
                && !string.IsNullOrEmpty(marker.ActiveReFlyRecordingId))
            {
                for (int i = 0; i < committedRecs.Count; i++)
                {
                    var rec = committedRecs[i];
                    if (rec == null) continue;
                    if (string.Equals(rec.RecordingId, marker.ActiveReFlyRecordingId,
                            StringComparison.Ordinal))
                    {
                        if (rec.VesselPersistentId != 0u)
                            protectedPids.Add(rec.VesselPersistentId);
                        break;
                    }
                }
            }

            int flagsAddedCount = 0;
            if (stripResult.PreservedFlagPids != null)
            {
                for (int i = 0; i < stripResult.PreservedFlagPids.Count; i++)
                {
                    uint flagPid = stripResult.PreservedFlagPids[i];
                    if (flagPid == 0u) continue;
                    if (protectedPids.Add(flagPid)) flagsAddedCount++;
                }
            }

            if (flagsAddedCount > 0 && !ParsekLog.SuppressLogging)
            {
                ParsekLog.Info(InvokeTag,
                    $"BuildProtectedPidsForInPlaceContinuation: shielded " +
                    $"{flagsAddedCount} preserved flag pid(s) from in-place " +
                    $"continuation kill walk (treeId='{marker?.TreeId ?? "<null>"}', " +
                    $"activeRec='{marker?.ActiveReFlyRecordingId ?? "<null>"}') " +
                    $"-- planted-flag career milestones immune to name-collision " +
                    $"Die() even when their vesselName matches an in-scope recording");
            }

            return protectedPids;
        }

        /// <summary>
        /// Bug #587 kill-survey eligibility: ONLY <see cref="VesselType.Debris"/>
        /// may become a candidate for the name-matched pre-existing-debris kill.
        /// <para>
        /// This predicate used to be the inverse - "skip <see cref="VesselType.Flag"/>,
        /// survey everything else" - which was harmless only because the survey
        /// set was empty in practice: the Re-Fly temp-save scrub deleted every
        /// vessel but the selected slot, and the selected pid is excluded upstream,
        /// so <see cref="ResolveInPlaceContinuationDebrisToKill"/> never had a
        /// candidate in production. Once the scrub was narrowed to preserve
        /// unrelated vessels, "survey everything else" meant the player's whole
        /// fleet, and the kill matches on EXACT vessel name against a set that
        /// includes the active recording's parent chain - so a prior-career craft
        /// sharing the re-flown craft's name (KSP's default naming makes that the
        /// common case) would be silently <c>Die()</c>d inside a crew-suppression
        /// guard, with no ledger row.
        /// </para>
        /// <para>
        /// Debris-only matches the mechanism's own documented intent (`#587` was
        /// filed and changelogged as removing "pre-existing DEBRIS vessels ...
        /// whose name matches a Destroyed-terminal recording"), so a real craft is
        /// now ineligible regardless of name collision. Fails CLOSED: an
        /// unreadable <see cref="IStrippableVessel.VesselType"/> yields "not a
        /// candidate", because including a vessel of unknown type risks killing a
        /// real one. That is the opposite of the old predicate's fail direction,
        /// and deliberately so - the old one failed toward "protect", this one
        /// fails toward "protect" as well, but the branch that means protection
        /// has moved.
        /// </para>
        /// </summary>
        internal static bool IsDebrisKillSurveyCandidate(IStrippableVessel v)
        {
            if (v == null) return false;
            VesselType type;
            // Mirror LiveVesselAdapter.VesselType's defensive try/catch: KSP's
            // Vessel getter walks managed Unity state and can throw on a
            // half-destroyed GameObject mid-strip.
            try { type = v.VesselType; }
            catch { return false; }
            return type == VesselType.Debris;
        }

        /// <summary>
        /// #fix-refly-preserve-flag-vessels survey-level filter: builds the
        /// <c>(pid, guid, name)</c> entries of live vessels that
        /// <see cref="PostLoadStripper"/> left in scene, EXCLUDING entries that
        /// must be shielded from the secondary in-place continuation kill walk.
        ///
        /// <para>Exclusion order (every skip increments a counter for the one-shot
        /// summary log):</para>
        /// <list type="bullet">
        ///   <item><description>Zero or null pid / name.</description></item>
        ///   <item><description>Ghost-map ProtoVessels (Parsek-owned).</description></item>
        ///   <item><description>Pids in <see cref="PostLoadStripResult.StrippedPids"/>
        ///     (already dead, only present if the Die() event hasn't drained
        ///     from <c>FlightGlobals</c> yet).</description></item>
        ///   <item><description><see cref="PostLoadStripResult.SelectedPid"/>
        ///     (#573 contract: the actively re-flown vessel).</description></item>
        ///   <item><description>Every non-debris vessel
        ///     (<see cref="IsDebrisKillSurveyCandidate"/>): only
        ///     <see cref="VesselType.Debris"/> may become a kill candidate. This
        ///     subsumes the old Flag-only skip and is what keeps the preserved
        ///     fleet out of the kill - a real craft must never enter the
        ///     leftAlone survey, whatever its identity or name.</description></item>
        /// </list>
        /// Returns an empty list (never null) on null/empty input. Pure /
        /// static so tests can pin the contract against
        /// <c>IStrippableVessel</c> stubs.
        /// </summary>
        internal static List<(uint pid, string guid, string name)> BuildLeftAlonePidNamesForInPlaceContinuation(
            IList<IStrippableVessel> liveVessels,
            PostLoadStripResult stripResult,
            Func<uint, bool> isGhostMapVessel)
        {
            var result = new List<(uint pid, string guid, string name)>();
            if (liveVessels == null || liveVessels.Count == 0) return result;
            Func<uint, bool> ghostCheck = isGhostMapVessel ?? (_ => false);

            int skippedNonDebris = 0;
            int skippedGhostMap = 0;
            int skippedStripped = 0;
            int skippedSelected = 0;
            int includedCount = 0;
            // First skipped vessel's identifiers captured for the one-shot
            // Verbose log, so playtest log readers can confirm the skip ran
            // without spamming a line per vessel.
            uint firstSkippedPid = 0u;
            string firstSkippedName = null;

            for (int i = 0; i < liveVessels.Count; i++)
            {
                var v = liveVessels[i];
                if (v == null) continue;
                uint pid;
                try { pid = v.PersistentId; }
                catch { continue; }
                if (pid == 0u) continue;
                if (ghostCheck(pid)) { skippedGhostMap++; continue; }
                if (stripResult.StrippedPids != null
                    && stripResult.StrippedPids.Contains(pid))
                {
                    skippedStripped++;
                    continue;
                }
                if (stripResult.SelectedPid == pid)
                {
                    skippedSelected++;
                    continue;
                }
                // Survey-level eligibility: DEBRIS ONLY. See
                // IsDebrisKillSurveyCandidate -- this subsumes the old
                // VesselType.Flag skip (a flag is not debris) and is what keeps
                // the preserved fleet out of a name-matched kill.
                // BuildProtectedPidsForInPlaceContinuation's PreservedFlagPids
                // branch is retained as the downstream safety net.
                if (!IsDebrisKillSurveyCandidate(v))
                {
                    if (skippedNonDebris == 0)
                    {
                        firstSkippedPid = pid;
                        try { firstSkippedName = v.VesselName; }
                        catch { firstSkippedName = null; }
                    }
                    skippedNonDebris++;
                    continue;
                }
                string name;
                try { name = v.VesselName; }
                catch { name = null; }
                if (string.IsNullOrEmpty(name)) continue;
                string guid;
                try { guid = VesselLaunchIdentity.NormalizeGuid(v.LaunchGuid); }
                catch { guid = null; }
                result.Add((pid, guid, name));
                includedCount++;
            }

            if (skippedNonDebris > 0 && !ParsekLog.SuppressLogging)
            {
                string safeName = string.IsNullOrEmpty(firstSkippedName) ? "<unnamed>" : firstSkippedName;
                ParsekLog.Verbose(InvokeTag,
                    $"Strip post-supplement: skipping non-debris v={firstSkippedPid} name='{safeName}' " +
                    $"from leftAlone survey -- only VesselType.Debris is kill-eligible " +
                    $"(totalNonDebrisSkipped={skippedNonDebris} included={includedCount} " +
                    $"skippedGhostMap={skippedGhostMap} skippedStripped={skippedStripped} " +
                    $"skippedSelected={skippedSelected})");
            }

            return result;
        }

        /// <summary>
        /// Bug #587: production caller for <see cref="ResolveInPlaceContinuationDebrisToKill"/>.
        /// Runs after <see cref="AtomicMarkerWrite"/> (so the marker is set + the
        /// re-fly-active short-circuit in <c>RunSpawnDeathChecks</c> is engaged) and
        /// kills pre-existing debris that would confuse KSP-stock patched conics.
        /// Each <c>Vessel.Die()</c> runs inside a <see cref="SuppressionGuard.Crew"/>
        /// to mirror <c>PostLoadStripper.StripVessel</c>'s silent-removal contract --
        /// no CrewKilled / CrewRemoved fanout into the ledger from this cleanup.
        ///
        /// <para>#fix-refly-preserve-flag-vessels protects preserved
        /// <see cref="VesselType.Flag"/> vessels through THREE coexisting
        /// layers — belt-and-suspenders for the planted-flag career
        /// milestone:</para>
        /// <list type="number">
        ///   <item><description><b>Primary strip bypass:</b>
        ///     <see cref="PostLoadStripper.ShouldPreserveVesselType"/>
        ///     short-circuits the strip BEFORE the slot-map / strict-unmatched
        ///     paths, so the flag is never recorded in
        ///     <see cref="PostLoadStripResult.StrippedPids"/> and never
        ///     receives a <c>Vessel.Die()</c> from <c>PostLoadStripper</c>.</description></item>
        ///   <item><description><b>Survey-level gate:</b>
        ///     <see cref="BuildLeftAlonePidNamesForInPlaceContinuation"/> admits
        ///     ONLY <see cref="VesselType.Debris"/> into the
        ///     <c>leftAlonePidNames</c> list, via
        ///     <see cref="IsDebrisKillSurveyCandidate"/> — so a name-colliding
        ///     flag never enters the kill-set construction, and neither does any
        ///     other real craft. This layer used to be the inverse (skip
        ///     <c>Flag</c>, survey everything else), which was safe only while
        ///     the Re-Fly scrub left a single vessel in scene; the debris-only
        ///     gate resolves the original review note's concern at the source
        ///     for the whole preserved fleet, not just for flags.</description></item>
        ///   <item><description><b>Kill-set protection:</b>
        ///     <see cref="BuildProtectedPidsForInPlaceContinuation"/> still adds
        ///     <see cref="PostLoadStripResult.PreservedFlagPids"/> to the
        ///     <c>protectedPids</c> set passed to
        ///     <see cref="ResolveInPlaceContinuationDebrisToKill"/>. Redundant
        ///     given (2) today, kept as a safety net so a future refactor that
        ///     accidentally drops the survey-level skip (e.g., a change to the
        ///     adapter's vessel-type fallback, or a new survey path that
        ///     bypasses the helper) cannot silently regress flag preservation.</description></item>
        /// </list>
        /// Both (2) and (3) are exercised independently by
        /// <c>Bug587StripPreExistingDebrisTests</c> regression guards so a
        /// future refactor cannot drop one layer unnoticed.
        /// </summary>
        internal static void StripPreExistingDebrisForInPlaceContinuation(
            PostLoadStripResult stripResult)
        {
            var scenario = ParsekScenario.Instance;
            if (scenario == null) return;
            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null) return;

            // Build the leftAlone (pid, name) pairs from live FlightGlobals because
            // the strip result only retains names; we need pids to issue Die() calls.
            // The strip already enumerated FlightGlobals, but did not pair pid+name
            // in the public surface. Re-enumerate via the same IStrippableVessel
            // abstraction PostLoadStripper uses so the VesselType-aware skip
            // (#fix-refly-preserve-flag-vessels survey-level layer) runs against
            // the production-adapter's defensive vessel-type access. We also retain
            // the raw IList<Vessel> for SnapshotKillTargets downstream (which
            // operates on the live Vessel objects to issue Die() calls).
            IList<Vessel> liveVessels;
            try { liveVessels = FlightGlobals.Vessels; }
            catch { liveVessels = null; }
            if (liveVessels == null || liveVessels.Count == 0) return;

            var liveStrippable = new List<IStrippableVessel>();
            foreach (var sv in DefaultVesselEnumeration.Instance.EnumerateVessels())
            {
                if (sv == null) continue;
                liveStrippable.Add(sv);
            }
            if (liveStrippable.Count == 0) return;

            var leftAlonePidNames = BuildLeftAlonePidNamesForInPlaceContinuation(
                liveStrippable, stripResult, GhostMapPresence.IsGhostMapVessel);

            // Protect the selected slot vessel + the marker's active recording's pid
            // (#573 contract: never kill the actively re-flown vessel) + every
            // VesselType.Flag pid the stripper preserved (#fix-refly-preserve-flag-vessels
            // follow-up: belt-and-suspenders alongside the survey-level gate above.
            // Even though BuildLeftAlonePidNamesForInPlaceContinuation now admits
            // ONLY VesselType.Debris at the source (so a Flag cannot reach the
            // resolver at all), this kill-set protection stays as a redundant
            // safety net so a future refactor that accidentally bypasses the
            // survey helper cannot silently revive name-collision Die() on a
            // planted-flag career milestone).
            var protectedPids = BuildProtectedPidsForInPlaceContinuation(
                stripResult, marker, RecordingStore.CommittedRecordings);

            // Bug #587 follow-up: also broaden the kill set to recording names in
            // the session-suppressed subtree (recordings being superseded by this
            // in-place continuation). The original predicate only killed
            // Destroyed-terminal name matches; the 2026-04-25 playtest caught a
            // non-Destroyed pre-existing vessel surviving the strip and showing up
            // as a clickable "doubled" copy of the booster's upper stage. The
            // suppressed-subtree closure is the authoritative scope of "this
            // re-fly is overwriting these recordings", so any matching real
            // vessel from the old timeline is phantom and should be cleaned up.
            IReadOnlyCollection<string> suppressedSubtree;
            try { suppressedSubtree = EffectiveState.ComputeSessionSuppressedSubtree(marker); }
            catch (Exception ex)
            {
                ParsekLog.Warn(InvokeTag,
                    $"StripPreExistingDebrisForInPlaceContinuation: ComputeSessionSuppressedSubtree threw " +
                    $"({ex.GetType().Name}: {ex.Message}) — falling back to Destroyed-terminal-only kill set");
                suppressedSubtree = null;
            }

            var kill = ResolveInPlaceContinuationDebrisToKill(
                marker,
                RecordingStore.CommittedTrees,
                leftAlonePidNames,
                protectedPids,
                suppressedSubtree);
            if (kill.Count == 0) return;

            // Bug #587 follow-up (PR #558 P2 review): snapshot the targets
            // BEFORE iterating Die(). Vessel.Die() removes the vessel from
            // FlightGlobals.Vessels, so walking the live IList while calling
            // Die() shifts subsequent indices and can skip consecutive
            // matching debris -- exactly the multi-debris case this PR is
            // supposed to handle. SnapshotKillTargets builds a stable list
            // before any Die() runs.
            var killSet = new HashSet<uint>(kill);
            var killTargets = SnapshotKillTargets(
                liveVessels, killSet, v => v == null ? 0u : v.persistentId);

            int killed = 0;
            var killedNames = new List<string>();
            for (int i = 0; i < killTargets.Count; i++)
            {
                var v = killTargets[i];
                if (v == null) continue;
                string name = v.vesselName ?? "<unnamed>";
                try
                {
                    using (SuppressionGuard.Crew())
                    {
                        v.Die();
                    }
                    killed++;
                    killedNames.Add(name);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(InvokeTag,
                        $"Strip post-supplement: Die() threw for v={v.persistentId} " +
                        $"name='{name}': {ex.Message}");
                }
            }

            if (killed > 0)
            {
                string joined = string.Join(", ", killedNames.ToArray());
                ParsekLog.Warn(InvokeTag,
                    $"Strip post-supplement: killed {killed} pre-existing tree-collision vessel(s) " +
                    $"for in-place continuation re-fly: [{joined}] " +
                    $"(same launch as, or a prior-career debris namesake of, a Destroyed-terminal, session-suppressed, or parent-chain recording in tree '{marker.TreeId}'; " +
                    $"left in scene by PostLoadStripper because no PidSlotMap entry; " +
                    $"would otherwise trip KSP patched conics into a phantom encounter -- bug #587)");
            }
        }
    }
}
