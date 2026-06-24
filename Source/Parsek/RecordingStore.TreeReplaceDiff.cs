using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Parsek
{
    public static partial class RecordingStore
    {
        private static bool HasRecordingTopologyDifference(
            RecordingTree existing,
            RecordingTree incoming)
        {
            if (existing?.Recordings == null || incoming?.Recordings == null)
                return false;

            foreach (var kvp in incoming.Recordings)
            {
                Recording incomingRec = kvp.Value;
                if (incomingRec == null ||
                    !existing.Recordings.TryGetValue(kvp.Key, out var existingRec) ||
                    existingRec == null)
                {
                    continue;
                }

                if (!string.Equals(
                        existingRec.ParentBranchPointId,
                        incomingRec.ParentBranchPointId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        existingRec.ChildBranchPointId,
                        incomingRec.ChildBranchPointId,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasRecordingPayloadDifference(
            RecordingTree existing,
            RecordingTree incoming)
        {
            if (existing?.Recordings == null || incoming?.Recordings == null)
                return false;

            foreach (var kvp in incoming.Recordings)
            {
                Recording incomingRec = kvp.Value;
                if (incomingRec == null ||
                    !existing.Recordings.TryGetValue(kvp.Key, out var existingRec) ||
                    existingRec == null)
                {
                    continue;
                }

                if (HasRecordingPayloadDifference(existingRec, incomingRec))
                    return true;
            }

            return false;
        }

        private static bool HasRecordingPayloadDifference(Recording existing, Recording incoming)
        {
            return CountOf(existing.Points) != CountOf(incoming.Points) ||
                CountOf(existing.OrbitSegments) != CountOf(incoming.OrbitSegments) ||
                CountOf(existing.PartEvents) != CountOf(incoming.PartEvents) ||
                CountOf(existing.FlagEvents) != CountOf(incoming.FlagEvents) ||
                CountOf(existing.SegmentEvents) != CountOf(incoming.SegmentEvents) ||
                CountOf(existing.TrackSections) != CountOf(incoming.TrackSections) ||
                !SameDouble(existing.StartUT, incoming.StartUT) ||
                !SameDouble(existing.EndUT, incoming.EndUT) ||
                existing.VesselPersistentId != incoming.VesselPersistentId ||
                existing.SpawnedVesselPersistentId != incoming.SpawnedVesselPersistentId ||
                existing.VesselSpawned != incoming.VesselSpawned ||
                existing.TerminalStateValue != incoming.TerminalStateValue ||
                existing.EndpointPhase != incoming.EndpointPhase ||
                !string.Equals(existing.EndpointBodyName, incoming.EndpointBodyName, StringComparison.Ordinal) ||
                !string.Equals(existing.TerminalOrbitBody, incoming.TerminalOrbitBody, StringComparison.Ordinal);
        }

        private static int CountOf<T>(ICollection<T> items)
        {
            return items?.Count ?? 0;
        }

        private static bool SameDouble(double left, double right)
        {
            if (double.IsNaN(left) && double.IsNaN(right))
                return true;

            return left.Equals(right);
        }

        private static bool HasBranchPointTopologyDifference(
            RecordingTree existing,
            RecordingTree incoming)
        {
            if (existing?.BranchPoints == null || incoming?.BranchPoints == null)
                return false;

            var existingById = existing.BranchPoints
                .Where(bp => bp != null && !string.IsNullOrEmpty(bp.Id))
                .GroupBy(bp => bp.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            foreach (var incomingBp in incoming.BranchPoints)
            {
                if (incomingBp == null ||
                    string.IsNullOrEmpty(incomingBp.Id) ||
                    !existingById.TryGetValue(incomingBp.Id, out var existingBp))
                {
                    continue;
                }

                if (!SameStringSet(existingBp.ParentRecordingIds, incomingBp.ParentRecordingIds) ||
                    !SameStringSet(existingBp.ChildRecordingIds, incomingBp.ChildRecordingIds))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SameStringSet(List<string> left, List<string> right)
        {
            if (left == null || left.Count == 0)
                return right == null || right.Count == 0;
            if (right == null || left.Count != right.Count)
                return false;

            var set = new HashSet<string>(left, StringComparer.Ordinal);
            return set.SetEquals(right);
        }
    }
}
