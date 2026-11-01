using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Decides which committed recordings are in legitimate "replay scope" versus
    /// purely historical, so the ghost-playback machinery stays dormant during a
    /// normal forward playthrough (BUG-B).
    ///
    /// Parsek's design (README "How It Works") replays a committed recording — and
    /// spawns its terminal vessel — only after the player REWINDS to before that
    /// recording, so its UT window lies in the player's forward path again. During
    /// normal forward play KSP's universal time only ever advances, so every
    /// committed recording's window is already historical the moment it is committed.
    /// Without this gate the engine treats "live UT is past the recorded end" as
    /// "playback finished" and spawns each recording's terminal vessel, and renders a
    /// ghost for any recording whose orbital-extrapolated window still straddles the
    /// live UT — even though the player never replayed anything.
    ///
    /// A recording enters replay scope the first time the live playhead is observed
    /// at or before its activation-start UT (the post-rewind case; a rewind drops the
    /// playhead onto the target recording's launch). A recording NOT in scope, whose
    /// window the playhead has only ever moved forward through/past, is "historical"
    /// and must produce no ghost, no map icon, and no spawned vessel.
    ///
    /// The discriminant keys on the recording's activation START (its real launch),
    /// not its end: an orbital recording's recorded/extrapolated END can run ahead of
    /// the live UT, but its START always lies behind the playhead during forward play.
    ///
    /// Keyed by recordingId (a stable per-recording GUID), so the latch is shared
    /// across the flight, Space Center, and Tracking Station scenes and cannot collide
    /// across saves. Cleared on return to the main menu (game unload).
    ///
    /// The latch is part of the save (<see cref="NodeName"/> in the PARSEK node): a save
    /// written while a recording replays carries it, so a load of that save (a Cold load,
    /// which resets every save-scoped state first) puts the recording back in replay scope
    /// and its ghost keeps drawing and relaying from the loaded UT (owner ruling 2026-11-01:
    /// every current ghost is drawn after any load, even mid-flight; todo
    /// REPLAY-SCOPE-LOST-ON-SAVE-LOAD). A save written during plain forward play carries no
    /// latch for a recording already behind the playhead, so BUG-B holds across a load too.
    /// The load policy is <see cref="LoadStateCategory.PlaybackReplayScope"/>.
    /// </summary>
    internal static class PlaybackScopeTracker
    {
        private static readonly HashSet<string> recordingsInReplayScope = new HashSet<string>();

        /// <summary>
        /// Recordings the live playhead was seen STRICTLY before (no tolerance) at a ready
        /// clock. Narrower than the replay-scope latch: the tolerance window also catches a
        /// commit made within a few seconds of a recording's start, when the live world
        /// already holds the recording's result. The Ghost Chain Rule's stale-vessel test
        /// (<see cref="ChainTipStaleVessel"/>) reads this one, because it replaces a live
        /// vessel and must only do so when the playhead really was wound back before it.
        /// </summary>
        private static readonly HashSet<string> recordingsSeenBeforeActivation = new HashSet<string>();

        /// <summary>
        /// A rewind lands the playhead on (or a few physics frames past) the target
        /// recording's launch UT, which equals that recording's activation-start to
        /// within timing jitter. Any real, spawnable mission recording is far longer
        /// than this, so a normal forward-play commit (currentUT already well past the
        /// recording's start) never falls inside the tolerance window.
        /// </summary>
        internal const double ActivationToleranceSeconds = 2.0;

        /// <summary>
        /// Records the live playhead position relative to a recording's activation
        /// start. If the playhead is at or before that start (within tolerance), the
        /// recording's window lies in the player's forward path — mark it in scope.
        /// Cheap; safe to call every frame for every committed recording.
        /// </summary>
        internal static void NotePlayhead(string recordingId, double currentUT, double activationStartUT)
        {
            if (string.IsNullOrEmpty(recordingId)) return;
            if (currentUT <= activationStartUT + ActivationToleranceSeconds)
                recordingsInReplayScope.Add(recordingId);
            if (currentUT < activationStartUT && IsSweepClockReady(currentUT))
                recordingsSeenBeforeActivation.Add(recordingId);
        }

        /// <summary>
        /// True iff the live playhead was seen strictly before the recording's activation
        /// start at a ready clock this session (a rewind or a load to before it).
        /// </summary>
        internal static bool WasPlayheadSeenBeforeActivation(string recordingId)
        {
            return !string.IsNullOrEmpty(recordingId)
                && recordingsSeenBeforeActivation.Contains(recordingId);
        }

        /// <summary>
        /// True iff the recording is historical: the playhead is past its activation
        /// start (outside tolerance) and the recording was never observed in replay
        /// scope. Such a recording must stay dormant during normal forward play.
        /// Returns false for a null/empty id (caller cannot identify the recording, so
        /// fall back to the pre-existing behaviour rather than suppress blindly).
        /// </summary>
        internal static bool IsHistoricalNeverReplayed(
            string recordingId, double currentUT, double activationStartUT)
        {
            if (string.IsNullOrEmpty(recordingId)) return false;
            if (currentUT <= activationStartUT + ActivationToleranceSeconds) return false;
            return !recordingsInReplayScope.Contains(recordingId);
        }

        /// <summary>
        /// Notes the playhead for every recording in <paramref name="recordings"/> (the
        /// per-frame sweep FLIGHT and the Space Center run inline over the committed list, and
        /// the Tracking Station runs through this). Returns how many were newly latched. A
        /// recording whose activation start is already behind the playhead (outside tolerance)
        /// is never latched here, so a genuinely historical recording stays historical no
        /// matter which scene sweeps it. A clock that is not ready (UT not positive, or NaN:
        /// a scene that has not loaded the save's time yet) sweeps nothing, because UT 0 would
        /// sit before every recording and latch the whole history.
        /// </summary>
        internal static int NotePlayheadSweep(IReadOnlyList<Recording> recordings, double currentUT)
        {
            if (!IsSweepClockReady(currentUT))
                return 0;
            int newlyLatched = 0;
            int count = recordings != null ? recordings.Count : 0;
            for (int i = 0; i < count; i++)
            {
                Recording rec = recordings[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                if (recordingsInReplayScope.Contains(rec.RecordingId)
                    && recordingsSeenBeforeActivation.Contains(rec.RecordingId))
                    continue;
                bool wasInScope = recordingsInReplayScope.Contains(rec.RecordingId);
                NotePlayhead(rec.RecordingId, currentUT,
                    PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(rec));
                if (!wasInScope && recordingsInReplayScope.Contains(rec.RecordingId))
                    newlyLatched++;
            }
            return newlyLatched;
        }

        /// <summary>True when <paramref name="currentUT"/> is a loaded game clock a sweep may trust.</summary>
        internal static bool IsSweepClockReady(double currentUT)
        {
            return !double.IsNaN(currentUT) && !double.IsInfinity(currentUT) && currentUT > 0.0;
        }

        /// <summary>True iff the recording has been latched into replay scope.</summary>
        internal static bool IsInReplayScope(string recordingId)
        {
            return !string.IsNullOrEmpty(recordingId)
                && recordingsInReplayScope.Contains(recordingId);
        }

        /// <summary>The PARSEK child node that carries the latch in a save.</summary>
        internal const string NodeName = "PLAYBACK_REPLAY_SCOPE";
        internal const string InScopeKey = "inScope";
        internal const string SeenBeforeActivationKey = "seenBeforeActivation";
        private const string Tag = "PlaybackScope";

        internal static int InReplayScopeCount => recordingsInReplayScope.Count;
        internal static int SeenBeforeActivationCount => recordingsSeenBeforeActivation.Count;

        /// <summary>
        /// Writes the latch into the PARSEK node (ids sorted, so a save is deterministic),
        /// replacing any copy the node already holds. An empty latch writes no node.
        /// </summary>
        internal static void SaveInto(ConfigNode parsekNode)
        {
            if (parsekNode == null)
                return;
            parsekNode.RemoveNodes(NodeName);
            if (recordingsInReplayScope.Count == 0 && recordingsSeenBeforeActivation.Count == 0)
                return;
            ConfigNode scope = parsekNode.AddNode(NodeName);
            var ids = new List<string>(recordingsInReplayScope);
            ids.Sort(StringComparer.Ordinal);
            for (int i = 0; i < ids.Count; i++)
                scope.AddValue(InScopeKey, ids[i]);
            ids = new List<string>(recordingsSeenBeforeActivation);
            ids.Sort(StringComparer.Ordinal);
            for (int i = 0; i < ids.Count; i++)
                scope.AddValue(SeenBeforeActivationKey, ids[i]);
        }

        /// <summary>
        /// Replaces the latch with the copy <paramref name="parsekNode"/> carries (none when it
        /// carries no <see cref="NodeName"/> node: a save written during plain forward play, or
        /// by a build before this node). The Cold-load install.
        /// </summary>
        internal static void InstallFromSave(ConfigNode parsekNode, string source)
        {
            int droppedInScope = recordingsInReplayScope.Count;
            int droppedSeen = recordingsSeenBeforeActivation.Count;
            recordingsInReplayScope.Clear();
            recordingsSeenBeforeActivation.Clear();
            int added = AddFrom(parsekNode, out int addedSeen);
            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "Replay scope installed from the save ({0}): inScope={1} seenBeforeActivation={2} "
                + "replacedInScope={3} replacedSeen={4} node={5}",
                source ?? "unspecified", added, addedSeen, droppedInScope, droppedSeen,
                parsekNode?.GetNode(NodeName) != null ? "present" : "absent"));
        }

        /// <summary>
        /// Adds the copy <paramref name="parsekNode"/> carries to the latch, keeping what memory
        /// holds: a rewind or a Re-Fly lands in the world of a save written earlier in the
        /// timeline, whose replaying recordings replay again from that save's UT. Returns how
        /// many recordings entered replay scope.
        /// </summary>
        internal static int UnionFromSave(ConfigNode parsekNode, string source)
        {
            int beforeInScope = recordingsInReplayScope.Count;
            int beforeSeen = recordingsSeenBeforeActivation.Count;
            int read = AddFrom(parsekNode, out int readSeen);
            int newInScope = recordingsInReplayScope.Count - beforeInScope;
            int newSeen = recordingsSeenBeforeActivation.Count - beforeSeen;
            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "Replay scope merged from the save ({0}): savedInScope={1} savedSeen={2} "
                + "newInScope={3} newSeen={4} inScope={5} seenBeforeActivation={6}",
                source ?? "unspecified", read, readSeen, newInScope, newSeen,
                recordingsInReplayScope.Count, recordingsSeenBeforeActivation.Count));
            return newInScope;
        }

        /// <summary>
        /// <see cref="UnionFromSave"/> over a parsed game's scenario protos (the rewind save
        /// before the Space Center loads it; its OnLoad node is persistent.sfs).
        /// </summary>
        internal static int UnionFromScenarios(IList<ProtoScenarioModule> scenarios, string source)
        {
            if (scenarios != null)
            {
                for (int i = 0; i < scenarios.Count; i++)
                {
                    ProtoScenarioModule proto = scenarios[i];
                    if (proto != null
                        && string.Equals(proto.moduleName, nameof(ParsekScenario), StringComparison.Ordinal))
                    {
                        return UnionFromSave(proto.GetData(), source);
                    }
                }
            }
            ParsekLog.Info(Tag, "Replay scope merged from the save (" + (source ?? "unspecified")
                + "): no Parsek scenario in the parsed save, nothing to merge");
            return 0;
        }

        private static int AddFrom(ConfigNode parsekNode, out int addedSeen)
        {
            addedSeen = 0;
            ConfigNode scope = parsekNode?.GetNode(NodeName);
            if (scope == null)
                return 0;
            int added = 0;
            string[] inScope = scope.GetValues(InScopeKey);
            for (int i = 0; i < inScope.Length; i++)
            {
                if (string.IsNullOrEmpty(inScope[i]))
                    continue;
                recordingsInReplayScope.Add(inScope[i]);
                added++;
            }
            string[] seen = scope.GetValues(SeenBeforeActivationKey);
            for (int i = 0; i < seen.Length; i++)
            {
                if (string.IsNullOrEmpty(seen[i]))
                    continue;
                recordingsSeenBeforeActivation.Add(seen[i]);
                addedSeen++;
            }
            return added;
        }

        /// <summary>Clears all scope state. Call on return to the main menu / game unload.</summary>
        internal static void Reset()
        {
            recordingsInReplayScope.Clear();
            recordingsSeenBeforeActivation.Clear();
        }

        internal static void ResetForTesting()
        {
            recordingsInReplayScope.Clear();
            recordingsSeenBeforeActivation.Clear();
        }
    }
}
