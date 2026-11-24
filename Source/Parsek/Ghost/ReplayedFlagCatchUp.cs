using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Replays the flag plants and pick-ups of a recording whose replay is over, whether or not a
    /// ghost was ever on screen for it (todo FLAG-LOST-WHEN-REPLAY-HAD-NO-GHOST-2026-11-24). The
    /// ghost walks (<see cref="GhostPlaybackLogic.ApplyFlagEvents"/>) need a ghost, or a rendered
    /// frame inside the recording's window: a window passed under high warp, in the Tracking
    /// Station, by a time jump or by a load runs none of them, and the Space Center never draws a
    /// recording off Kerbin.
    ///
    /// <para>
    /// One instance per scene host (flight, Space Center), asked once per frame per recording. A
    /// recording is walked once per scene, when the clock is past its end and past its last flag
    /// event, so no event of the future is ever applied. The walk is idempotent in the world (a
    /// standing flag, the removal tombstones), so a later scene's walk finds its work done.
    /// </para>
    ///
    /// <para>
    /// The gates are the end spawn's: a historical recording (the playhead was never wound back
    /// before it, <see cref="PlaybackScopeTracker"/>) owns no replay, its flags are the live
    /// flight's own and what the player did to them since is theirs; a timeline-inactive, session
    /// suppressed or currently flown recording replays nothing.
    /// </para>
    /// </summary>
    internal sealed class ReplayedFlagCatchUp
    {
        /// <summary>Failed walks of one recording before this scene gives up; the next scene load
        /// starts over.</summary>
        internal const int MaxAttemptsPerScene = 3;

        private readonly string host;

        // Recordings with nothing left to do in this scene.
        private readonly HashSet<string> settled = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> failedAttempts =
            new Dictionary<string, int>(StringComparer.Ordinal);

        internal ReplayedFlagCatchUp(string host)
        {
            this.host = host;
        }

        /// <summary>
        /// Pure: the UT past which a recording's whole flag history is in the past, its end or its
        /// last flag plant / pick-up, whichever is later (a pick-up made in the frame before a
        /// kerbal boards lands after the last sample).
        /// </summary>
        internal static double ResolveDueUT(Recording rec)
        {
            return GhostPlaybackLogic.ResolveTimelineEndFlagWalkUT(rec, rec.EndUT);
        }

        /// <summary>Pure: does the recording hold a flag plant or a flag pick-up?</summary>
        internal static bool HasFlagHistory(Recording rec)
        {
            if (rec == null)
                return false;
            if (rec.FlagEvents != null && rec.FlagEvents.Count > 0)
                return true;
            List<SegmentEvent> segs = rec.SegmentEvents;
            if (segs == null)
                return false;
            for (int i = 0; i < segs.Count; i++)
            {
                if (segs[i].type == SegmentEventType.FlagPickedUp)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Asks for <paramref name="rec"/>'s walk. <paramref name="replays"/> is the host's end
        /// spawn gate without the vessel half (in replay scope or the active Re-Fly session's tree,
        /// timeline-active, not session-suppressed, not the recording being flown). Returns true
        /// when a walk ran.
        /// </summary>
        internal bool Tick(Recording rec, double currentUT, bool replays)
        {
            if (!replays || rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return false;
            string id = rec.RecordingId;
            if (currentUT <= rec.EndUT)
            {
                // Wound back inside this scene: the walk is owed again when the clock returns.
                settled.Remove(id);
                failedAttempts.Remove(id);
                return false;
            }
            if (settled.Contains(id))
                return false;
            if (!HasFlagHistory(rec))
            {
                settled.Add(id);
                return false;
            }
            if (currentUT <= ResolveDueUT(rec))
                return false;

            var (eligible, planted, alreadyPresent, failed, removedByPlayer) =
                GhostPlaybackLogic.SpawnFlagVesselsUpToUT(rec, currentUT, settledFlagMatch: true);
            CultureInfo ic = CultureInfo.InvariantCulture;
            string line = string.Format(ic,
                "Flag catch-up: rec={0} host={1} currentUT={2} endUT={3} eligible={4} planted={5} "
                + "alreadyPresent={6} removedByPlayer={7} failed={8}",
                id, host ?? "?", currentUT.ToString("R", ic), rec.EndUT.ToString("R", ic),
                eligible, planted, alreadyPresent, removedByPlayer, failed);
            if (planted > 0)
                ParsekLog.Info(RemovedReplayFlags.Tag, line);
            else
                ParsekLog.Verbose(RemovedReplayFlags.Tag, line);

            if (failed == 0)
            {
                settled.Add(id);
                failedAttempts.Remove(id);
                return true;
            }

            failedAttempts.TryGetValue(id, out int attempts);
            attempts++;
            failedAttempts[id] = attempts;
            if (attempts >= MaxAttemptsPerScene)
            {
                settled.Add(id);
                ParsekLog.Warn(RemovedReplayFlags.Tag, string.Format(ic,
                    "Flag catch-up gave up in this scene: rec={0} host={1} failed={2} attempts={3}; "
                    + "the next scene load tries again",
                    id, host ?? "?", failed, attempts));
            }
            return true;
        }

        /// <summary>Forgets every recording (scene teardown).</summary>
        internal void Reset()
        {
            settled.Clear();
            failedAttempts.Clear();
        }
    }
}
