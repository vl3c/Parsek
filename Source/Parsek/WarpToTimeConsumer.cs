using System;
using System.Collections;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Process-lifetime addon that consumes a pending <see cref="WarpToTimeRequest"/> once
    /// the Space Center scene has loaded and settled, running the final forward time-jump.
    /// Also hosts a one-frame defer helper used by the flight warp paths so the in-flight
    /// commit's spawned leaves are not materialized into a scene that the follow-up rewind
    /// / scene-load is about to discard.
    ///
    /// <para>Hosted as <c>[KSPAddon(Instantly, true)]</c> + <c>DontDestroyOnLoad</c> so it
    /// survives the flight->Space Center transition and is alive in the Space Center scene
    /// (mirrors <see cref="InGameTests.TestRunnerShortcut"/>). It must NOT live on a
    /// scene-scoped component (ParsekScenario / ParsekFlight / ParsekKSC), which would be
    /// destroyed mid-warp.</para>
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class WarpToTimeConsumer : MonoBehaviour
    {
        private const string Tag = "WarpTime";
        // ~5s at 60fps: upper bound on waiting for the rewind UT adjustment to settle.
        private const int RewindSettleGuardFrames = 300;

        private static WarpToTimeConsumer instance;
        internal static WarpToTimeConsumer Instance => instance;

        void Awake()
        {
            if (instance != null)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);
            GameEvents.onLevelWasLoaded.Add(OnLevelWasLoaded);
            ParsekLog.Verbose(Tag, "WarpToTimeConsumer initialized");
        }

        void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
                GameEvents.onLevelWasLoaded.Remove(OnLevelWasLoaded);
            }
        }

        private void OnLevelWasLoaded(GameScenes scene)
        {
            if (scene != GameScenes.SPACECENTER)
                return;

            // Capture the career-start snapshot once for a brand-new career (independent of any
            // pending warp). Idempotent: skips if a snapshot already exists or this is not a
            // fresh career (see CareerStartSnapshot.ShouldCapture).
            StartCoroutine(MaybeCaptureCareerStart());

            if (!WarpToTimeRequest.HasPending)
                return;

            if (WarpToTimeRequest.IsStale())
            {
                ParsekLog.Info(Tag,
                    "Pending warp ignored: armed by a different process session " +
                    "(orphaned across restart) — clearing");
                WarpToTimeRequest.Clear();
                return;
            }

            ParsekLog.Verbose(Tag,
                string.Format(CultureInfo.InvariantCulture,
                    "Space Center loaded with pending warp targetUT={0:F1} — starting consumer",
                    WarpToTimeRequest.TargetUT));
            StartCoroutine(ConsumePendingWarp());
        }

        private IEnumerator MaybeCaptureCareerStart()
        {
            // Defer a frame so the recording store + Planetarium are settled.
            yield return null;

            bool exists = CareerStartSnapshot.Exists();
            // ERS-routed recording count (raw CommittedRecordings reads trip the grep audit);
            // a fresh career has 0 visible recordings either way.
            int recCount = EffectiveState.ComputeERS()?.Count ?? 0;
            double now = Planetarium.GetUniversalTime();
            if (!CareerStartSnapshot.ShouldCapture(exists, recCount, now, ParsekTimeFormat.SecsPerDay))
            {
                ParsekLog.Verbose(Tag,
                    string.Format(CultureInfo.InvariantCulture,
                        "Career-start snapshot not captured (exists={0} recordings={1} now={2:F1})",
                        exists, recCount, now));
                yield break;
            }

            ParsekLog.Info(Tag,
                string.Format(CultureInfo.InvariantCulture,
                    "Capturing career-start snapshot for new career (recordings={0} now={1:F1})",
                    recCount, now));
            CareerStartSnapshot.Capture();
        }

        private IEnumerator ConsumePendingWarp()
        {
            // Let new-scene singletons (Planetarium, resource singletons) spin up.
            yield return null;

            double target = WarpToTimeRequest.TargetUT;
            ParsekLog.Verbose(Tag,
                string.Format(CultureInfo.InvariantCulture,
                    "Consumer entry: target={0:F1} now={1:F1} rewindPending={2}",
                    target, Planetarium.GetUniversalTime(), RecordingStore.RewindUTAdjustmentPending));

            // Sequence AFTER any in-flight rewind UT adjustment. The plain Rewind-to-Launch
            // path (InitiateRewind -> HandleRewindOnLoad) sets RewindUTAdjustmentPending
            // synchronously in OnLoad (before any yield), and ApplyRewindResourceAdjustment
            // clears it ONLY after it has set Planetarium UT to the post-rewind (launch
            // lead-time) point. So flag==false is a reliable "UT is settled" signal: we never
            // observe it false while the UT is still pre-rewind. For the non-rewind
            // flight->KSC forward case the flag is already false and this loop is a no-op.
            int guard = RewindSettleGuardFrames;
            int waited = 0;
            while (RecordingStore.RewindUTAdjustmentPending && guard-- > 0)
            {
                waited++;
                yield return null;
            }

            if (RecordingStore.RewindUTAdjustmentPending)
            {
                // Guard expired with the rewind still unsettled — refuse to jump from a stale
                // (pre-rewind) UT rather than risk landing at the wrong time.
                ParsekLog.Error(Tag,
                    string.Format(CultureInfo.InvariantCulture,
                        "Pending warp aborted: rewind UT adjustment did not settle within {0} frames (target={1:F1})",
                        RewindSettleGuardFrames, target));
                WarpToTimeRequest.Clear();
                yield break;
            }

            WarpToTimeRequest.Clear();

            double now = Planetarium.GetUniversalTime();
            if (now < target - WarpToTimeMath.AtTargetEpsilonSeconds)
            {
                ParsekLog.Info(Tag,
                    string.Format(CultureInfo.InvariantCulture,
                        "Consuming pending warp: forward jump now={0:F1} -> target={1:F1} (delta={2:F1}s, waited={3} frames)",
                        now, target, target - now, waited));
                TimeJumpManager.ExecuteForwardJump(target);
            }
            else
            {
                ParsekLog.Info(Tag,
                    string.Format(CultureInfo.InvariantCulture,
                        "Consuming pending warp: already at/after target (now={0:F1} target={1:F1}, waited={2} frames) — no forward jump",
                        now, target, waited));
            }
        }

        /// <summary>
        /// Runs <paramref name="action"/> after one frame. Used by the flight warp paths to
        /// defer the rewind / scene-load until after the in-flight commit's synchronous work
        /// (including throwaway leaf spawns) is past. Falls back to running immediately if no
        /// addon instance exists (should not happen in a normal game).
        /// </summary>
        internal static void RunNextFrame(Action action)
        {
            if (action == null) return;
            if (instance == null)
            {
                ParsekLog.Warn(Tag, "RunNextFrame: no consumer instance — running immediately");
                action();
                return;
            }
            instance.StartCoroutine(instance.DeferOneFrame(action));
        }

        private IEnumerator DeferOneFrame(Action action)
        {
            yield return null;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag, $"Deferred warp action failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        internal static void ResetForTesting()
        {
            instance = null;
        }
    }
}
