using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Ground-truth signal for the loop-relative live-anchor double-suppression
    /// (PR #1136 Step 2 follow-up). The relative-anchor resolver's host delegate
    /// (<c>ParsekFlight.TryResolveLiveLaunchMatchedAnchorPoseForResolver</c>, invoked
    /// from <c>RelativeAnchorResolver.TryBindLiveLaunchMatchedAnchorPose</c>) stamps
    /// here every time it actually live-binds a focus member to its anchor's
    /// launch-matched live vessel (the exact moment that logs
    /// <c>relative-anchor-live-bind ... source=live</c>).
    /// The flight + map double-suppression sites then ask "was this anchor live-bound
    /// this or last frame?" instead of independently re-deriving the loop window.
    ///
    /// The original Step-2 predicate re-derived the in-window membership through
    /// <c>ResolveTrackingStationSampleUT</c> on the previous-frame loop-unit set, which
    /// drifted from the engine's actual per-frame loop mapping and returned false on
    /// every frame in the 2026-06-13 playtest (0 suppressions despite the resolver
    /// binding source=live twice). Keying off the resolver's own bind eliminates the
    /// duplicated mapping math, so the suppression cannot disagree with the bind that
    /// caused the double in the first place.
    ///
    /// Recency is frame-count based (not a per-frame clear) so it is robust to the
    /// ordering between the flight playback update, the engine positioning pass (which
    /// triggers the resolver), and the separate map-presence update: a consumer in
    /// frame N sees binds stamped in frames N-1 / N. Stale cross-scene entries expire
    /// naturally (delta &gt; window) and are pruned opportunistically.
    ///
    /// The current frame comes through <see cref="SetFrameCountProvider"/> (wired in-game
    /// to <c>Time.frameCount</c>); the default returns 0 so the headless unit tests that
    /// exercise the suppression sites never touch the Unity ECall. The no-argument
    /// production methods read that provider; the explicit-frame overloads are for
    /// deterministic unit tests.
    /// </summary>
    internal static class LiveAnchorBindTracker
    {
        // Default recency window in frames. 2 absorbs the one-frame ordering gap
        // between ComputePlaybackFlags (reads) and the positioning pass (writes), plus
        // a single skipped frame at high time warp, without leaving the anchor's own
        // ghost suppressed for a visible stretch after the dependent member leaves.
        internal const int DefaultRecencyWindowFrames = 2;

        // Above this many tracked anchors, prune entries older than this many frames.
        // Anchors are a handful of stations per session, so this is a runaway backstop.
        private const int PruneThresholdCount = 64;
        private const int PruneMaxAgeFrames = 600;

        private static readonly Dictionary<string, int> boundFrameByRecordingId =
            new Dictionary<string, int>();

        // Safe default: 0 (no Unity ECall). In-game wiring overrides this with
        // () => Time.frameCount; headless tests leave it at the default.
        private static Func<int> frameCountProvider = () => 0;

        /// <summary>Wire the live frame source (in-game: <c>() =&gt; Time.frameCount</c>).</summary>
        internal static void SetFrameCountProvider(Func<int> provider)
        {
            frameCountProvider = provider ?? (() => 0);
        }

        private static int CurrentFrame => frameCountProvider();

        /// <summary>Stamp <paramref name="anchorRecordingId"/> as live-bound at the current frame.</summary>
        internal static void RecordLiveBind(string anchorRecordingId)
        {
            RecordLiveBind(anchorRecordingId, CurrentFrame);
        }

        /// <summary>Stamp <paramref name="anchorRecordingId"/> as live-bound at <paramref name="frameCount"/>.</summary>
        internal static void RecordLiveBind(string anchorRecordingId, int frameCount)
        {
            if (string.IsNullOrEmpty(anchorRecordingId))
                return;
            boundFrameByRecordingId[anchorRecordingId] = frameCount;
            if (boundFrameByRecordingId.Count > PruneThresholdCount)
                PruneOlderThan(frameCount, PruneMaxAgeFrames);
        }

        /// <summary>True iff the anchor was live-bound within the recency window of the current frame.</summary>
        internal static bool WasLiveBoundRecently(string anchorRecordingId)
        {
            return WasLiveBoundRecently(anchorRecordingId, CurrentFrame, DefaultRecencyWindowFrames);
        }

        /// <summary>
        /// True iff <paramref name="anchorRecordingId"/> was live-bound within
        /// <paramref name="windowFrames"/> frames of <paramref name="frameCount"/>.
        /// </summary>
        internal static bool WasLiveBoundRecently(
            string anchorRecordingId,
            int frameCount,
            int windowFrames = DefaultRecencyWindowFrames)
        {
            if (string.IsNullOrEmpty(anchorRecordingId))
                return false;
            return boundFrameByRecordingId.TryGetValue(anchorRecordingId, out int boundFrame)
                && IsRecentBind(boundFrame, frameCount, windowFrames);
        }

        /// <summary>Pure recency predicate: the bind is recent when it is in [current - window, current].</summary>
        internal static bool IsRecentBind(int boundFrame, int currentFrame, int windowFrames)
        {
            int delta = currentFrame - boundFrame;
            return delta >= 0 && delta <= windowFrames;
        }

        private static void PruneOlderThan(int currentFrame, int maxAgeFrames)
        {
            List<string> stale = null;
            foreach (KeyValuePair<string, int> kv in boundFrameByRecordingId)
            {
                if (currentFrame - kv.Value > maxAgeFrames)
                    (stale ?? (stale = new List<string>())).Add(kv.Key);
            }
            if (stale == null)
                return;
            for (int i = 0; i < stale.Count; i++)
                boundFrameByRecordingId.Remove(stale[i]);
        }

        /// <summary>Drop all tracked binds (scene change / test reset).</summary>
        internal static void Clear()
        {
            boundFrameByRecordingId.Clear();
        }

        internal static void ResetForTesting()
        {
            boundFrameByRecordingId.Clear();
            frameCountProvider = () => 0;
        }
    }
}
