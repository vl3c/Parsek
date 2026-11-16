using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Pure core of the <c>GhostFxIntegrity</c> in-game category
    /// (<see cref="GhostFxIntegrityInGameTests"/>): after a Re-Fly, a revert or a rewind, are
    /// a replayed ghost's engine plumes the ones its recording says, owned once, and only on
    /// ghosts of recordings the timeline still shows? (todo NEXT-TESTS-TO-BUILD-2026-11-11
    /// item 7, audit B8.)
    ///
    /// <para>The engine power a ghost should hold is re-derived here from the recording's own
    /// part events, independently of the applier: the events the ghost has consumed (its
    /// <c>partEventIndex</c> cursor) are walked in order and the last engine event of each
    /// (part, module) key wins (EngineIgnited / EngineThrottle take the event's value,
    /// EngineShutdown zero), a Decoupled event zeroes every engine in the decoupled subtree
    /// and a Destroyed event zeroes the destroyed part's own engines. A recording with no
    /// engine event at all starts every engine at full power, the debris-booster auto-start
    /// the ghost build applies.</para>
    /// </summary>
    internal static class GhostFxIntegrity
    {
        /// <summary>The applier copies the event value into the info verbatim, so the two
        /// agree to the float or not at all.</summary>
        internal const float PowerEpsilon = 1e-4f;

        /// <summary>A key is failed only when it disagrees on this many consecutive sampled
        /// frames: a ghost re-primed in the sampled frame resets its cursor before the walk
        /// that catches it up, and one frame of that is not a defect.</summary>
        internal const int MismatchFramesToFail = 2;

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        internal enum EngineVerdict
        {
            Ok,
            PowerMismatch,
            DarkWhileOn,
            LitWhileOff,
        }

        internal static bool IsEngineEvent(PartEventType type)
        {
            return type == PartEventType.EngineIgnited
                || type == PartEventType.EngineThrottle
                || type == PartEventType.EngineShutdown;
        }

        /// <summary>True when the recording carries at least one engine event of any key.</summary>
        internal static bool HasAnyEngineEvent(IList<PartEvent> events)
        {
            if (events == null) return false;
            for (int i = 0; i < events.Count; i++)
                if (IsEngineEvent(events[i].eventType)) return true;
            return false;
        }

        /// <summary>
        /// The power the ghost engine (<paramref name="pid"/>, <paramref name="moduleIndex"/>)
        /// should hold after the first <paramref name="appliedCount"/> events of the recording
        /// were applied. A null event list starts at zero (the ghost build auto-starts only when
        /// it has a list to read).
        /// </summary>
        internal static float ExpectedEnginePower(
            IList<PartEvent> events, int appliedCount, uint pid, int moduleIndex,
            Dictionary<uint, List<uint>> tree)
        {
            if (events == null) return 0f;
            float power = HasAnyEngineEvent(events) ? 0f : 1f;
            int n = Math.Min(Math.Max(appliedCount, 0), events.Count);
            for (int i = 0; i < n; i++)
            {
                PartEvent e = events[i];
                switch (e.eventType)
                {
                    case PartEventType.EngineIgnited:
                    case PartEventType.EngineThrottle:
                        if (e.partPersistentId == pid && e.moduleIndex == moduleIndex)
                            power = e.value;
                        break;
                    case PartEventType.EngineShutdown:
                        if (e.partPersistentId == pid && e.moduleIndex == moduleIndex)
                            power = 0f;
                        break;
                    case PartEventType.Decoupled:
                        if (GhostPlaybackLogic.CollectSubtreePids(e.partPersistentId, tree).Contains(pid))
                            power = 0f;
                        break;
                    case PartEventType.Destroyed:
                        if (e.partPersistentId == pid)
                            power = 0f;
                        break;
                }
            }
            return power;
        }

        /// <summary>
        /// One engine's verdict for one frame. <paramref name="fxVisible"/> is false when the
        /// ghost or the engine's part is hidden, or the ghost's FX are suppressed (distance,
        /// warp, pause): the power is still judged then, the plume is not.
        /// </summary>
        internal static EngineVerdict JudgeEngine(
            float expectedPower, float livePower, bool fxVisible, int systems, int playing)
        {
            if (Math.Abs(expectedPower - livePower) > PowerEpsilon)
                return EngineVerdict.PowerMismatch;
            if (!fxVisible)
                return EngineVerdict.Ok;
            if (expectedPower > 0f && systems > 0 && playing == 0)
                return EngineVerdict.DarkWhileOn;
            if (expectedPower <= 0f && playing > 0)
                return EngineVerdict.LitWhileOff;
            return EngineVerdict.Ok;
        }

        /// <summary>One particle system an engine or RCS info holds, as the ownership check
        /// reads it.</summary>
        internal struct FxSystemRef
        {
            public string GhostKey;
            public uint PartPid;
            public int InstanceId;
            public bool Destroyed;
            public bool UnderOwnGhost;
        }

        internal sealed class OwnershipCounts
        {
            public int Systems;
            public int Destroyed;
            public int ForeignRoot;
            public int Duplicates;
            public int SharedWithinPart;
            public readonly List<string> Problems = new List<string>();
            public bool Clean => Destroyed == 0 && ForeignRoot == 0 && Duplicates == 0;
        }

        /// <summary>
        /// Counts the ownership problems over every system the live infos hold: a destroyed
        /// system still referenced (an orphan reference), a system that is not under its own
        /// ghost's root (detached or parented to another object), and one system held by two
        /// different ghosts or two different parts (a duplicate). Two modules of ONE part
        /// sharing a system are counted apart and not failed.
        /// </summary>
        internal static OwnershipCounts CountOwnership(IList<FxSystemRef> refs)
        {
            var c = new OwnershipCounts();
            if (refs == null) return c;
            var firstOwner = new Dictionary<int, FxSystemRef>();
            for (int i = 0; i < refs.Count; i++)
            {
                FxSystemRef r = refs[i];
                c.Systems++;
                if (r.Destroyed)
                {
                    c.Destroyed++;
                    AddProblem(c, "destroyed system on ghost=" + r.GhostKey + " pid=" + r.PartPid.ToString(IC));
                    continue;
                }
                if (!r.UnderOwnGhost)
                {
                    c.ForeignRoot++;
                    AddProblem(c, "system off its ghost root on ghost=" + r.GhostKey + " pid=" + r.PartPid.ToString(IC));
                }
                FxSystemRef owner;
                if (firstOwner.TryGetValue(r.InstanceId, out owner))
                {
                    if (string.Equals(owner.GhostKey, r.GhostKey, StringComparison.Ordinal)
                        && owner.PartPid == r.PartPid)
                    {
                        c.SharedWithinPart++;
                    }
                    else
                    {
                        c.Duplicates++;
                        AddProblem(c, "system shared by ghost=" + owner.GhostKey + " pid=" + owner.PartPid.ToString(IC)
                            + " and ghost=" + r.GhostKey + " pid=" + r.PartPid.ToString(IC));
                    }
                }
                else
                {
                    firstOwner[r.InstanceId] = r;
                }
            }
            return c;
        }

        private static void AddProblem(OwnershipCounts c, string problem)
        {
            if (c.Problems.Count < 10) c.Problems.Add(problem);
        }

        /// <summary>Recording ids held by more than one live primary ghost.</summary>
        internal static List<string> SharedRecordingIds(IList<string> liveRecordingIds)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var shared = new List<string>();
            if (liveRecordingIds == null) return shared;
            for (int i = 0; i < liveRecordingIds.Count; i++)
            {
                string id = liveRecordingIds[i];
                if (string.IsNullOrEmpty(id)) continue;
                if (!seen.Add(id) && !shared.Contains(id)) shared.Add(id);
            }
            return shared;
        }

        /// <summary>Live ghost recording ids the timeline holds inactive (superseded by a
        /// Re-Fly merge, or retired by a rewind), each named with its reason.</summary>
        internal static List<string> LiveInactive(
            IList<string> liveRecordingIds, IDictionary<string, TimelineInactiveReason> inactive)
        {
            var hits = new List<string>();
            if (liveRecordingIds == null || inactive == null) return hits;
            for (int i = 0; i < liveRecordingIds.Count; i++)
            {
                string id = liveRecordingIds[i];
                TimelineInactiveReason reason;
                if (string.IsNullOrEmpty(id) || !inactive.TryGetValue(id, out reason)) continue;
                if (reason == TimelineInactiveReason.None) continue;
                hits.Add(id + "(" + reason + ")");
            }
            return hits;
        }

        internal static string FormatEngineSummary(
            string scene, int frames, int ghosts, int engines, int judged, int ok, int bad,
            int lit, int dark, int fxHidden, int unsupported)
        {
            return string.Format(IC,
                "engine-fx vs recording: scene={0} frames={1} ghosts={2} engines={3} judged={4} ok={5} " +
                "bad={6} lit={7} dark={8} fxHidden={9} unsupported={10}",
                scene ?? "?", frames, ghosts, engines, judged, ok, bad, lit, dark, fxHidden, unsupported);
        }

        /// <summary>Names present in both samples: a ghost root the engine does not own in
        /// two sampled frames is abandoned, where one frame of it can be a root whose deferred
        /// destroy has not run yet.</summary>
        internal static List<string> InBothSamples(IList<string> first, IList<string> second)
        {
            var hits = new List<string>();
            if (first == null || second == null) return hits;
            var later = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < second.Count; i++)
                if (second[i] != null) later.Add(second[i]);
            for (int i = 0; i < first.Count; i++)
            {
                string name = first[i];
                if (name != null && later.Contains(name) && !hits.Contains(name)) hits.Add(name);
            }
            return hits;
        }

        internal static string FormatOwnershipSummary(
            string scene, int ghosts, OwnershipCounts c, int sharedRecordings, int unownedRoots)
        {
            c = c ?? new OwnershipCounts();
            return string.Format(IC,
                "fx ownership: scene={0} ghosts={1} systems={2} destroyed={3} foreignRoot={4} " +
                "duplicates={5} sharedWithinPart={6} sharedRecordings={7} unownedGhostRoots={8}",
                scene ?? "?", ghosts, c.Systems, c.Destroyed, c.ForeignRoot, c.Duplicates,
                c.SharedWithinPart, sharedRecordings, unownedRoots);
        }

        internal static string FormatSupersedeSummary(
            string scene, int ghosts, int supersedeRows, int inactiveIds, int replayingInactive,
            int indexMismatch)
        {
            return string.Format(IC,
                "inactive recordings: scene={0} ghosts={1} supersedeRows={2} inactiveIds={3} " +
                "replayingInactive={4} indexMismatch={5}",
                scene ?? "?", ghosts, supersedeRows, inactiveIds, replayingInactive, indexMismatch);
        }
    }
}
