using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // Pure adapter: turns a looping Mission's selection into a span-clock LoopUnitSet
    // (GhostPlaybackLogic.LoopUnit / LoopUnitSet). v1 = one Mission loops at a time. There
    // is NO engine wiring yet (that is Phase D); this is exercised only by unit tests for
    // now. The whole selected mission span loops on one shared clock at a cadence that is
    // never shorter than the span, so the mission always plays in full. Pure: no Unity
    // calls, no shared mutable state, no recording mutation.
    internal static class MissionLoopUnitBuilder
    {
        // Set true in tests to silence the single per-build Verbose summary.
        internal static bool SuppressLogging;

        /// <summary>
        /// Builds the LoopUnitSet for the first looping Mission. Returns
        /// <see cref="GhostPlaybackLogic.LoopUnitSet.Empty"/> when nothing loops, the tree is
        /// missing, or the selection maps to no committed members. Member indices are
        /// committed-list indices (the alignment invariant the engine consumes).
        /// </summary>
        internal static GhostPlaybackLogic.LoopUnitSet Build(
            IReadOnlyList<Mission> missions,
            IReadOnlyList<RecordingTree> trees,
            IReadOnlyList<Recording> committed)
        {
            // 1. First looping mission, else dormant.
            Mission mission = FindLoopingMission(missions);
            if (mission == null)
                return GhostPlaybackLogic.LoopUnitSet.Empty;

            // 2. Resolve its tree by TreeId.
            RecordingTree tree = FindTree(trees, mission.TreeId);
            if (tree == null)
            {
                ParsekLog.Verbose("Mission",
                    $"MissionLoopUnit: mission='{mission.Name}' treeId={mission.TreeId ?? "<null>"} " +
                    "tree not found; no unit");
                return GhostPlaybackLogic.LoopUnitSet.Empty;
            }

            // 3. Through-line view + included heads via the shared selection rule.
            MissionStructure structure = MissionStructureBuilder.Build(tree);
            MissionThroughLineView view = MissionThroughLineBuilder.Build(structure);
            HashSet<string> includedHeads =
                MissionSelection.ComputeIncludedHeadIds(view, mission.ExcludedThroughLineHeadIds);

            // 4. Union of every included through-line's member legs.
            var includedRecordingIds = new HashSet<string>();
            foreach (string head in includedHeads)
            {
                if (!view.ByHeadId.TryGetValue(head, out MissionThroughLine tl))
                    continue;
                var members = tl.MemberLegIds;
                for (int i = 0; i < members.Count; i++)
                    if (!string.IsNullOrEmpty(members[i]))
                        includedRecordingIds.Add(members[i]);
            }

            // 5. id -> committed index (first wins on duplicates). Map included ids to indices,
            //    skipping ids absent from committed, then sort by StartUT (tiebreak by index).
            Dictionary<string, int> indexById = BuildIndexById(committed);
            var memberIndices = new List<int>();
            int skippedNotCommitted = 0;
            foreach (string id in includedRecordingIds)
            {
                if (indexById.TryGetValue(id, out int idx))
                    memberIndices.Add(idx);
                else
                    skippedNotCommitted++;
            }
            if (memberIndices.Count == 0)
            {
                ParsekLog.Verbose("Mission",
                    $"MissionLoopUnit: mission='{mission.Name}' tree={tree.Id} " +
                    $"includedHeads={includedHeads.Count} no committed members; no unit");
                return GhostPlaybackLogic.LoopUnitSet.Empty;
            }

            memberIndices.Sort((a, b) =>
            {
                int cmp = committed[a].StartUT.CompareTo(committed[b].StartUT);
                if (cmp != 0)
                    return cmp;
                return a.CompareTo(b);
            });

            // 6. Span = [min StartUT, max EndUT] over the members.
            double spanStartUT = double.PositiveInfinity;
            double spanEndUT = double.NegativeInfinity;
            for (int i = 0; i < memberIndices.Count; i++)
            {
                Recording rec = committed[memberIndices[i]];
                if (rec.StartUT < spanStartUT)
                    spanStartUT = rec.StartUT;
                if (rec.EndUT > spanEndUT)
                    spanEndUT = rec.EndUT;
            }

            // 7. Cadence: never shorter than the span so the mission never truncates. Auto =
            //    span; an explicit period is raised to the span when shorter, and both are
            //    floored at MinCycleDuration. The period sets the gap between repeats, not a cut.
            double span = spanEndUT - spanStartUT;
            double cadence = mission.LoopTimeUnit == LoopTimeUnit.Auto
                ? Math.Max(span, LoopTiming.MinCycleDuration)
                : Math.Max(Math.Max(mission.LoopIntervalSeconds, span), LoopTiming.MinCycleDuration);

            // 8. Owner = earliest-start member (first after the StartUT sort).
            int ownerIndex = memberIndices[0];

            // 9. Build the single unit + lookup maps.
            int[] memberArray = memberIndices.ToArray();
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex, memberArray, spanStartUT, spanEndUT, cadence);
            var unitsByOwner = new Dictionary<int, GhostPlaybackLogic.LoopUnit> { { ownerIndex, unit } };
            var ownerByIndex = new Dictionary<int, int>();
            for (int i = 0; i < memberArray.Length; i++)
                ownerByIndex[memberArray[i]] = ownerIndex;

            if (!SuppressLogging)
            {
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Verbose("Mission",
                    $"MissionLoopUnit: mission='{mission.Name}' tree={tree.Id} " +
                    $"members={memberArray.Length} skipped={skippedNotCommitted} " +
                    $"span=[{spanStartUT.ToString("R", ic)},{spanEndUT.ToString("R", ic)}] " +
                    $"spanDur={span.ToString("R", ic)} unit={mission.LoopTimeUnit} " +
                    $"cadence={cadence.ToString("R", ic)} owner={ownerIndex}");
            }

            return new GhostPlaybackLogic.LoopUnitSet(unitsByOwner, ownerByIndex);
        }

        private static Mission FindLoopingMission(IReadOnlyList<Mission> missions)
        {
            if (missions == null)
                return null;
            for (int i = 0; i < missions.Count; i++)
                if (missions[i] != null && missions[i].LoopPlayback)
                    return missions[i];
            return null;
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
