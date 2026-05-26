using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // Pure adapter: turns every looping Mission's selection into a span-clock LoopUnitSet
    // (GhostPlaybackLogic.LoopUnit / LoopUnitSet), one unit per looping Mission. Multiple
    // Missions loop concurrently, at most one per tree (enforced by MissionStore), so their
    // committed indices are disjoint and each owns its own span clock. Each unit carries TWO
    // cadences from the same user input: a span-clock cadence (never shorter than the span,
    // so a single span instance always plays in full - used by KSC, the Tracking Station,
    // and the flight no-overlap branch) and an overlap cadence (the TRUE launch-to-launch
    // period, Auto = the global auto-loop interval, cap-clamped to MaxOverlapMissionInstances).
    // When the overlap cadence is shorter than the span the flight engine relaunches the
    // whole mission on that cadence so several staggered instances play concurrently, exactly
    // like a single recording with period < duration. Pure: no Unity calls, no shared mutable
    // state, no recording mutation.
    internal static class MissionLoopUnitBuilder
    {
        // Set true in tests to silence the single per-build Verbose summary.
        internal static bool SuppressLogging;

        /// <summary>
        /// Builds the LoopUnitSet for every looping Mission (one unit per Mission). Multiple Missions
        /// loop concurrently - at most one per tree (enforced by MissionStore), so their committed
        /// indices are disjoint and each Mission owns its own span clock. Returns
        /// <see cref="GhostPlaybackLogic.LoopUnitSet.Empty"/> when nothing loops or no looping Mission
        /// maps to any committed member. Member indices are committed-list indices (the alignment
        /// invariant the engine consumes). Collisions (only possible if the one-per-tree invariant is
        /// violated upstream): a unit whose owner index is already taken is dropped wholesale with a
        /// warn (the realistic same-tree case, since same-tree variants share the trunk root and so
        /// the same earliest/owner index); a stray shared member index is kept on its FIRST claimant
        /// in OwnerByIndex, so the engine's per-index dispatch always routes a shared index to one
        /// unit.
        /// </summary>
        internal static GhostPlaybackLogic.LoopUnitSet Build(
            IReadOnlyList<Mission> missions,
            IReadOnlyList<RecordingTree> trees,
            IReadOnlyList<Recording> committed,
            double autoLoopIntervalSeconds)
        {
            if (missions == null)
                return GhostPlaybackLogic.LoopUnitSet.Empty;

            Dictionary<string, int> indexById = BuildIndexById(committed);

            var unitsByOwner = new Dictionary<int, GhostPlaybackLogic.LoopUnit>();
            var ownerByIndex = new Dictionary<int, int>();
            int builtUnits = 0;

            for (int mi = 0; mi < missions.Count; mi++)
            {
                Mission mission = missions[mi];
                if (mission == null || !mission.LoopPlayback)
                    continue;

                if (!TryBuildMissionUnit(
                        mission, trees, committed, indexById, autoLoopIntervalSeconds,
                        out GhostPlaybackLogic.LoopUnit unit, out int[] memberArray))
                    continue;

                // Owner-index collision across units: only reachable if two looping Missions share a
                // tree (one-per-tree violated upstream). The earlier unit wins its owner slot.
                if (unitsByOwner.ContainsKey(unit.OwnerIndex))
                {
                    ParsekLog.Warn("Mission",
                        $"MissionLoopUnit: mission='{mission.Name}' tree={mission.TreeId} owner index " +
                        $"{unit.OwnerIndex} already owned by another looping unit; skipping (expected " +
                        "one loop per tree)");
                    continue;
                }

                // Member-index collision: keep the first claimant so OwnerByIndex and the unit's
                // MemberIndices never disagree. Defensive - disjoint trees never collide.
                int claimedConflicts = 0;
                for (int k = 0; k < memberArray.Length; k++)
                {
                    int idx = memberArray[k];
                    if (ownerByIndex.ContainsKey(idx))
                        claimedConflicts++;
                    else
                        ownerByIndex[idx] = unit.OwnerIndex;
                }
                if (claimedConflicts > 0)
                    ParsekLog.Warn("Mission",
                        $"MissionLoopUnit: mission='{mission.Name}' tree={mission.TreeId} had " +
                        $"{claimedConflicts} member index(es) already claimed by another looping unit; " +
                        "kept the first claimant (expected one loop per tree)");

                unitsByOwner[unit.OwnerIndex] = unit;
                builtUnits++;
            }

            if (builtUnits == 0)
                return GhostPlaybackLogic.LoopUnitSet.Empty;

            return new GhostPlaybackLogic.LoopUnitSet(unitsByOwner, ownerByIndex);
        }

        /// <summary>
        /// Builds one span-clock <see cref="GhostPlaybackLogic.LoopUnit"/> for a single looping
        /// Mission. Returns false (and logs why at Verbose) when the tree is missing or the
        /// selection maps to no committed members. <paramref name="indexById"/> is the shared
        /// committed id -> index map (built once by <see cref="Build"/>).
        /// </summary>
        private static bool TryBuildMissionUnit(
            Mission mission,
            IReadOnlyList<RecordingTree> trees,
            IReadOnlyList<Recording> committed,
            Dictionary<string, int> indexById,
            double autoLoopIntervalSeconds,
            out GhostPlaybackLogic.LoopUnit unit,
            out int[] memberArray)
        {
            unit = default;
            memberArray = System.Array.Empty<int>();

            // 1. Resolve its tree by TreeId.
            RecordingTree tree = FindTree(trees, mission.TreeId);
            if (tree == null)
            {
                ParsekLog.Verbose("Mission",
                    $"MissionLoopUnit: mission='{mission.Name}' treeId={mission.TreeId ?? "<null>"} " +
                    "tree not found; no unit");
                return false;
            }

            // 2. Structure + through-line view + composition tree (the intervals the interval-level
            //    start/end-trim selection toggles).
            MissionStructure structure = MissionStructureBuilder.Build(tree);
            MissionThroughLineView view = MissionThroughLineBuilder.Build(structure);
            List<MissionCompositionNode> compRoots = MissionCompositionBuilder.Build(structure);

            // 3-5. Per-vessel render windows from the interval-level selection, mapped to committed
            //      indices + their TRIMMED render window (the vessel window intersected with the
            //      member's own [StartUT, EndUT]; a member entirely outside the window is dropped).
            //      Extracted into ComputeTrimmedMemberWindows so the Missions UI (period-cell span
            //      display + watch target) consumes the IDENTICAL member set + windows this builder
            //      uses - otherwise the UI (which keyed off the legacy ExcludedThroughLineHeadIds)
            //      would ignore the interval-level trims the loop actually applies. With no
            //      exclusions every window spans the whole vessel (no behavior change).
            var memberWindowByIndex = ComputeTrimmedMemberWindows(
                view, compRoots, committed, mission.ExcludedIntervalKeys, indexById,
                out int vesselWindowCount, out int skippedNotCommitted);
            var memberIndices = new List<int>(memberWindowByIndex.Keys);
            if (memberIndices.Count == 0)
            {
                ParsekLog.Verbose("Mission",
                    $"MissionLoopUnit: mission='{mission.Name}' tree={tree.Id} " +
                    $"vessels={vesselWindowCount} no committed members in window; no unit");
                return false;
            }

            // Sort by the TRIMMED render start (the member's effective start), tiebreak by index.
            memberIndices.Sort((a, b) =>
            {
                int cmp = memberWindowByIndex[a].StartUT.CompareTo(memberWindowByIndex[b].StartUT);
                if (cmp != 0)
                    return cmp;
                return a.CompareTo(b);
            });

            // 5. Span = [min trimmed start, max trimmed end] over the members.
            double spanStartUT = double.PositiveInfinity;
            double spanEndUT = double.NegativeInfinity;
            for (int i = 0; i < memberIndices.Count; i++)
            {
                GhostPlaybackLogic.LoopUnit.MemberWindow w = memberWindowByIndex[memberIndices[i]];
                if (w.StartUT < spanStartUT) spanStartUT = w.StartUT;
                if (w.EndUT > spanEndUT) spanEndUT = w.EndUT;
            }

            // 6. Two cadences from the same user input:
            //
            //    (a) Span-clock cadence: never shorter than the span so a SINGLE span instance never
            //        truncates. Auto = span; an explicit period is raised to the span when shorter,
            //        both floored at MinCycleDuration. Consumed by the single-instance scenes (KSC,
            //        Tracking Station) and the flight engine's no-overlap branch.
            //
            //    (b) Overlap cadence: the TRUE launch-to-launch period (Auto = the GLOBAL auto-loop
            //        interval, same as single recordings - NOT the span; an explicit period kept
            //        as-is). Floored at MinCycleDuration, then cap-clamped so ceil(span / cadence)
            //        stays within MaxOverlapMissionInstances (mirrors the per-recording
            //        ComputeEffectiveLaunchCadence cap, but over the SPAN at mission granularity).
            //        When this is shorter than the span the flight engine overlaps the whole mission
            //        with itself; when >= span it falls back to the single span instance.
            double span = spanEndUT - spanStartUT;
            double cadence = mission.LoopTimeUnit == LoopTimeUnit.Auto
                ? Math.Max(span, LoopTiming.MinCycleDuration)
                : Math.Max(Math.Max(mission.LoopIntervalSeconds, span), LoopTiming.MinCycleDuration);

            double rawOverlapPeriod = mission.LoopTimeUnit == LoopTimeUnit.Auto
                ? autoLoopIntervalSeconds
                : mission.LoopIntervalSeconds;
            // ComputeEffectiveLaunchCadence floors at MinCycleDuration and raises only as far as the
            // cap needs; pass the SPAN as the per-instance "duration" so ceil(span/cadence) is the
            // live mission-instance count.
            double overlapCadence = GhostPlaybackLogic.ComputeEffectiveLaunchCadence(
                rawOverlapPeriod, span, GhostPlayback.MaxOverlapMissionInstances);

            // 7. Owner = earliest-start member (first after the StartUT sort).
            int ownerIndex = memberIndices[0];

            // 7b. Phase anchor: the UT the loop was enabled at. The span clock measures phase from
            //     this (elapsed = currentUT - phaseAnchorUT) so re-enabling the loop restarts from
            //     the recording's start. An unset (NaN) anchor falls back to spanStartUT, which
            //     reproduces the old absolute-phase behavior.
            double phaseAnchorUT = double.IsNaN(mission.LoopAnchorUT)
                ? spanStartUT
                : mission.LoopAnchorUT;

            // 8. Build the unit (carrying the per-member trimmed render windows).
            memberArray = memberIndices.ToArray();
            unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex, memberArray, spanStartUT, spanEndUT, cadence, phaseAnchorUT,
                overlapCadence, memberWindowByIndex);

            if (!SuppressLogging)
            {
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Verbose("Mission",
                    $"MissionLoopUnit: mission='{mission.Name}' tree={tree.Id} " +
                    $"members={memberArray.Length} skipped={skippedNotCommitted} " +
                    $"span=[{spanStartUT.ToString("R", ic)},{spanEndUT.ToString("R", ic)}] " +
                    $"spanDur={span.ToString("R", ic)} unit={mission.LoopTimeUnit} " +
                    $"cadence={cadence.ToString("R", ic)} " +
                    $"overlapCadence={overlapCadence.ToString("R", ic)} " +
                    $"overlaps={(overlapCadence < span ? "yes" : "no")} owner={ownerIndex} " +
                    $"phaseAnchor={phaseAnchorUT.ToString("R", ic)}");
            }

            return true;
        }

        /// <summary>
        /// Maps a Mission's interval-level selection (<see cref="Mission.ExcludedIntervalKeys"/>) to
        /// its committed loop members + their TRIMMED render windows, keyed by committed index. This
        /// is the single source of truth for "which recordings does this looped mission include, and
        /// over what time window" - <see cref="TryBuildMissionUnit"/> builds the actual span clock
        /// from it, and the Missions UI (period-cell effective-cadence display + watch target) reads
        /// it so the displayed cadence and watch candidates match the looped reality exactly (rather
        /// than the legacy through-line-head selection, which ignored interval trims).
        ///
        /// Each included vessel's render window (from <see cref="MissionIntervalSelection.ComputeRenderWindows"/>)
        /// is intersected with each member recording's own [StartUT, EndUT]; a member entirely
        /// outside the window is dropped, and the first claimant wins on duplicate RecordingIds.
        /// <paramref name="indexById"/> is the committed RecordingId -> index map (pass the shared one
        /// from <see cref="Build"/>, or null to build it here). Empty exclusions => every window spans
        /// the whole vessel, so each member keeps its full range. Pure: no Unity, no shared state.
        /// </summary>
        internal static Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow> ComputeTrimmedMemberWindows(
            MissionThroughLineView view,
            List<MissionCompositionNode> compRoots,
            IReadOnlyList<Recording> committed,
            ICollection<string> excludedIntervalKeys,
            Dictionary<string, int> indexById,
            out int vesselWindowCount,
            out int skippedNotCommitted)
        {
            var memberWindowByIndex = new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>();
            vesselWindowCount = 0;
            skippedNotCommitted = 0;
            if (view == null || compRoots == null || committed == null)
                return memberWindowByIndex;
            if (indexById == null)
                indexById = BuildIndexById(committed);

            Dictionary<string, MissionIntervalSelection.RenderWindow> vesselWindows =
                MissionIntervalSelection.ComputeRenderWindows(compRoots, excludedIntervalKeys);
            vesselWindowCount = vesselWindows.Count;
            foreach (var vw in vesselWindows)
            {
                if (!view.ByHeadId.TryGetValue(vw.Key, out MissionThroughLine tl))
                    continue;
                double winStart = vw.Value.StartUT;
                double winEnd = vw.Value.EndUT;
                var members = tl.MemberLegIds;
                for (int i = 0; i < members.Count; i++)
                {
                    string id = members[i];
                    if (string.IsNullOrEmpty(id))
                        continue;
                    if (!indexById.TryGetValue(id, out int idx))
                    {
                        skippedNotCommitted++;
                        continue;
                    }
                    if (memberWindowByIndex.ContainsKey(idx))
                        continue; // first wins on duplicate ids
                    Recording rec = committed[idx];
                    double rStart = Math.Max(winStart, rec.StartUT);
                    double rEnd = Math.Min(winEnd, rec.EndUT);
                    if (rEnd <= rStart)
                        continue; // member entirely outside the vessel's render window (trimmed off)
                    memberWindowByIndex[idx] =
                        new GhostPlaybackLogic.LoopUnit.MemberWindow(rStart, rEnd);
                }
            }
            return memberWindowByIndex;
        }

        /// <summary>
        /// Cheap change-detection signature over the inputs that shape the Mission
        /// <see cref="GhostPlaybackLogic.LoopUnitSet"/>. Shared by every scene driver (flight engine,
        /// KSC, tracking station) so the allocating, Verbose-logging <see cref="Build"/> only fires on
        /// an actual input change while the cached set is pushed every frame. Mirrors Build's "every
        /// looping mission, in list order" rule: for EACH looping mission it folds in Id, TreeId,
        /// LoopIntervalSeconds, LoopTimeUnit, LoopAnchorUT, sorted ExcludedThroughLineHeadIds, and its
        /// tree's BranchPoints.Count + Recordings.Count; then the global
        /// <paramref name="autoLoopIntervalSeconds"/> (which sets an Auto mission's overlap cadence),
        /// the committed-list count, and a rolling RecordingId hash. Constant "none:" prefix when no
        /// mission loops, so toggling looping off still rebuilds to Empty exactly once. Pure: no Unity
        /// calls, no shared mutable state.
        /// </summary>
        internal static string BuildSignature(
            IReadOnlyList<Mission> missions,
            IReadOnlyList<RecordingTree> trees,
            IReadOnlyList<Recording> committed,
            double autoLoopIntervalSeconds)
        {
            var ic = CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder(128);

            int loopingCount = 0;
            if (missions != null)
            {
                for (int mi = 0; mi < missions.Count; mi++)
                {
                    Mission m = missions[mi];
                    if (m == null || !m.LoopPlayback)
                        continue;
                    loopingCount++;
                    sb.Append(m.Id ?? "<noid>").Append('|');
                    sb.Append(m.TreeId ?? "<notree>").Append('|');
                    sb.Append(m.LoopIntervalSeconds.ToString("R", ic)).Append('|');
                    sb.Append(m.LoopTimeUnit.ToString()).Append('|');
                    // Phase anchor: re-enabling the loop re-anchors the span clock, so a changed
                    // anchor must force a rebuild even when nothing else about the mission moved.
                    sb.Append(m.LoopAnchorUT.ToString("R", ic)).Append('|');
                    // Sorted + joined so set order never perturbs the signature. The interval-level
                    // trim keys (ExcludedIntervalKeys) now drive the unit's members + span, so fold
                    // those in (a start/end-trim change must rebuild). The legacy through-line-head
                    // set is folded too so a mixed/old save still rebuilds when it changes.
                    var excluded = new List<string>(m.ExcludedThroughLineHeadIds);
                    excluded.Sort(StringComparer.Ordinal);
                    for (int e = 0; e < excluded.Count; e++)
                        sb.Append(excluded[e] ?? "").Append(',');
                    sb.Append('~');
                    var excludedIntervals = new List<string>(m.ExcludedIntervalKeys);
                    excludedIntervals.Sort(StringComparer.Ordinal);
                    for (int e = 0; e < excludedIntervals.Count; e++)
                        sb.Append(excludedIntervals[e] ?? "").Append(',');
                    sb.Append(';');
                    // Tree topology: a mid-session merge / re-parent can change the unit's
                    // members or span without adding/renaming any committed RecordingId, so the
                    // committed hash below would not move. Fold this looping tree's branch +
                    // recording counts in so a topology change still forces a rebuild.
                    RecordingTree loopTree = FindTree(trees, m.TreeId);
                    sb.Append((loopTree?.BranchPoints?.Count ?? 0).ToString(ic)).Append('/');
                    sb.Append((loopTree?.Recordings?.Count ?? 0).ToString(ic)).Append('#');
                }
            }

            if (loopingCount == 0)
                sb.Append("none:");
            else
                // Auto missions take their overlap cadence from the GLOBAL auto-loop interval, so a
                // change to that setting (with any Auto mission looping) must rebuild even when nothing
                // about the missions moved. Folding it in once unconditionally is cheap.
                sb.Append(autoLoopIntervalSeconds.ToString("R", ic)).Append('|');

            // Committed-list identity: count + a rolling hash of RecordingIds (member indices are
            // committed-list indices, so any add/remove/reorder must invalidate the cached set).
            int count = committed?.Count ?? 0;
            sb.Append(count.ToString(ic)).Append('|');
            int rollingHash = 17;
            for (int i = 0; i < count; i++)
            {
                string id = committed[i]?.RecordingId ?? "";
                unchecked { rollingHash = rollingHash * 31 + StringComparer.Ordinal.GetHashCode(id); }
            }
            sb.Append(rollingHash.ToString(ic));
            return sb.ToString();
        }

        private static RecordingTree FindTree(IReadOnlyList<RecordingTree> trees, string treeId)
        {
            if (trees == null || string.IsNullOrEmpty(treeId))
                return null;
            for (int i = 0; i < trees.Count; i++)
                if (trees[i] != null && trees[i].Id == treeId)
                    return trees[i];
            return null;
        }

        private static Dictionary<string, int> BuildIndexById(IReadOnlyList<Recording> committed)
        {
            var indexById = new Dictionary<string, int>();
            if (committed == null)
                return indexById;
            for (int i = 0; i < committed.Count; i++)
            {
                Recording rec = committed[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                if (!indexById.ContainsKey(rec.RecordingId))
                    indexById[rec.RecordingId] = i; // first wins on duplicate ids
            }
            return indexById;
        }
    }
}
