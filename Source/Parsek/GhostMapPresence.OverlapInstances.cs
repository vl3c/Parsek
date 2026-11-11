using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Per-(recording, cycle) overlap ghost map instances and the boundary-overlap secondary
    // instance.
    internal static partial class GhostMapPresence
    {
        // =====================================================================
        //  Per-instance overlap map presence (slice i)
        //  docs/dev/plans/maprender-overlap-per-instance.md
        //
        //  Mirrors the proven KSC per-instance overlap model (ParsekKSC.UpdateOverlapKsc /
        //  SpawnKscGhost / kscOverlapGhosts) on the map + Tracking Station presence layer:
        //  ONE map ProtoVessel per LIVE overlap cycle so the N map icons match the N flight
        //  meshes 1:1. The schedule resolves through the PURE
        //  GhostPlaybackLogic.TryResolveAutoLoopLaunchSchedule so flight AND Tracking Station
        //  (no flight engine present) resolve identically; the cycle math reuses
        //  GhostPlaybackLogic.GetActiveCycles + ComputeOverlapCyclePlaybackUT verbatim.
        // =====================================================================

        /// <summary>
        /// PURE create/destroy decision for the per-instance overlap lifecycle: given the cycles that
        /// currently have a live instance, the active window <c>[firstCycle, lastCycle]</c>, and the
        /// per-frame spawn budget, decides which cycles to create (live but missing, honoring the cap +
        /// throttle) and which to destroy (expired below firstCycle). Unity-free so the policy is
        /// xUnit-pinnable in isolation. <paramref name="spawnBudget"/> is the remaining
        /// <see cref="GhostPlayback.MaxSpawnsPerFrame"/> headroom this frame; once exhausted,
        /// remaining live-but-missing cycles are deferred to a later frame (the create path re-runs
        /// every lifecycle tick), exactly like the flight engine's spawn throttle.
        /// </summary>
        internal static void DecideOverlapInstanceChanges(
            IEnumerable<long> existingCycles,
            long firstCycle,
            long lastCycle,
            int spawnBudget,
            out List<long> toCreate,
            out List<long> toDestroy)
        {
            toCreate = new List<long>();
            toDestroy = new List<long>();
            var present = new HashSet<long>();
            if (existingCycles != null)
            {
                foreach (long c in existingCycles)
                {
                    present.Add(c);
                    // Expired: a cycle that has dropped out of the live window (below firstCycle, the
                    // cap clamp or natural completion) OR somehow ahead of the newest cycle.
                    if (c < firstCycle || c > lastCycle)
                        toDestroy.Add(c);
                }
            }

            if (lastCycle < firstCycle)
                return;

            // Create newest-first so under a throttle the most-recently-launched (most visible)
            // instances win the budget; older live cycles fill in on subsequent ticks.
            int budget = spawnBudget;
            for (long c = lastCycle; c >= firstCycle; c--)
            {
                if (present.Contains(c))
                    continue;
                if (budget <= 0)
                    break;
                toCreate.Add(c);
                budget--;
            }
        }

        /// <summary>
        /// Slice (i) core: ensure ONE live map ProtoVessel per active overlap cycle for an
        /// overlap-looped recording, mirroring ParsekKSC.UpdateOverlapKsc. Resolves the schedule via
        /// the pure auto-launch-queue resolver, computes <c>[firstCycle, lastCycle]</c> via
        /// <see cref="GhostPlaybackLogic.GetActiveCycles"/>, destroys instances whose cycle dropped
        /// out of the window, and creates missing live cycles (each at its own
        /// <c>effUT = ComputeOverlapCyclePlaybackUT(cycle)</c> with
        /// <c>loopEpochShiftSeconds = currentUT - effUT</c>), throttled by the per-frame spawn budget.
        /// ALL N instances (including the newest cycle) live in
        /// <see cref="overlapInstanceVessels"/> (single-ownership rule). Returns the number of
        /// instances created this call so the caller can debit a shared per-frame spawn counter.
        /// <paramref name="warpRate"/> + <paramref name="sweepGameSeconds"/> feed the high-warp
        /// collapse gate (<see cref="ShouldCollapseOverlapToSingleInstance"/>): while a sweep spans a
        /// launch cycle above the ghost-hide warp, the recording keeps one re-pointed map vessel
        /// instead (<see cref="EnsureCollapsedOverlapInstance"/>).
        /// </summary>
        internal static int EnsureOverlapInstances(
            int recIdx,
            Recording rec,
            double currentUT,
            IReadOnlyList<Recording> committed,
            GhostPlaybackLogic.LoopUnitSet loopUnits,
            ref int frameSpawnCount,
            float warpRate = 1f,
            double sweepGameSeconds = 0.0)
        {
            if (rec == null)
                return 0;

            if (!ResolveOverlapSchedule(
                    rec, recIdx, committed, loopUnits,
                    out double playbackStartUT, out double scheduleStartUT,
                    out double duration, out double effectiveCadence, out double cycleDuration))
            {
                // Not a resolvable loop (defensive — caller already gated on IsOverlapRecording):
                // tear down any leftover instances for this index.
                DestroyAllOverlapInstancesForRecording(recIdx, "overlap-schedule-unresolved");
                return 0;
            }

            // Before the schedule's first launch: no instances yet.
            if (currentUT < scheduleStartUT)
            {
                DestroyAllOverlapInstancesForRecording(recIdx, "overlap-before-schedule-start");
                return 0;
            }

            // Step 2: suppress this overlap recording's OWN instance ghosts ONLY while
            // its guid-gated launch-matched live vessel is loaded AND it was the LIVE
            // docking anchor of an in-window relative member this-or-last frame (the
            // Step-1 live-bind event), NOT for the whole loop (the hard-coded
            // loopMemberInWindow:true overlap path would otherwise re-create the double
            // the lifecycle pass suppresses only during the actual docking overlap).
            // Keyed on the guid-gated RealVesselExistsForRecording, NOT the pid-only
            // IsMaterializedForMapPresence, so a same-craft different-launch vessel
            // never suppresses. Best-effort on this map path: the live-bind is stamped
            // at member resolution (ghost (re)creation), not per on-rails frame, so the
            // duplicate can briefly show until the next resolve (see
            // WasLiveBoundThisOrLastFrame).
            bool liveLaunchMatchedAnchorOfActiveMember =
                rec != null
                && GhostPlaybackLogic.RealVesselExistsForRecording(rec)
                && RelativeAnchorResolver.WasLiveBoundThisOrLastFrame(rec.RecordingId);

            long firstCycle, lastCycle;
            GhostPlaybackLogic.GetActiveCycles(
                currentUT,
                scheduleStartUT,
                scheduleStartUT + duration,
                effectiveCadence,
                GhostPlayback.MaxOverlapGhostsPerRecording,
                out firstCycle, out lastCycle);

            // Collect the cycles currently live for THIS recording index.
            var existing = new List<long>();
            foreach (var kvp in overlapInstanceVessels)
            {
                if (kvp.Key.recIdx == recIdx)
                    existing.Add(kvp.Key.cycle);
            }

            // High-warp collapse (todo OVERLAP-MAP-GHOSTS-CHURN-AT-HIGH-WARP): when this sweep spans
            // a launch cycle above the ghost-hide warp, the window moves past live instances on every
            // sweep; keep one map vessel and re-point it instead of creating and destroying vessels.
            if (ShouldCollapseOverlapToSingleInstance(warpRate, sweepGameSeconds, cycleDuration))
            {
                return EnsureCollapsedOverlapInstance(
                    recIdx, rec, currentUT, playbackStartUT, scheduleStartUT, duration, cycleDuration,
                    firstCycle, lastCycle, existing, liveLaunchMatchedAnchorOfActiveMember,
                    ref frameSpawnCount);
            }

            int spawnBudget = GhostPlayback.MaxSpawnsPerFrame - frameSpawnCount;
            if (spawnBudget < 0) spawnBudget = 0;

            DecideOverlapInstanceChanges(
                existing, firstCycle, lastCycle, spawnBudget,
                out List<long> toCreate, out List<long> toDestroy);

            int destroyed = 0;
            for (int i = 0; i < toDestroy.Count; i++)
            {
                RemoveOverlapInstance(recIdx, toDestroy[i], "overlap-cycle-expired");
                destroyed++;
            }

            int created = 0;
            for (int i = 0; i < toCreate.Count; i++)
            {
                // Respect the shared per-frame throttle (mirror the flight engine's MaxSpawnsPerFrame
                // gate); DecideOverlapInstanceChanges already bounded toCreate by the budget, but
                // re-check against the live counter in case multiple recordings spawn this frame.
                if (GhostPlaybackLogic.ShouldThrottleSpawn(frameSpawnCount, GhostPlayback.MaxSpawnsPerFrame))
                {
                    ParsekLog.VerboseRateLimited(Tag, "overlap-instance-throttle",
                        string.Format(ic,
                            "Overlap per-instance create throttled rec=#{0} \"{1}\" " +
                            "(used {2}/{3} spawns this frame, deferring remaining cycles)",
                            recIdx, rec.VesselName ?? "(null)",
                            frameSpawnCount, GhostPlayback.MaxSpawnsPerFrame),
                        1.0);
                    break;
                }

                long cycle = toCreate[i];
                double effUT = GhostPlaybackLogic.ComputeOverlapCyclePlaybackUT(
                    currentUT,
                    scheduleStartUT,
                    playbackStartUT,
                    duration,
                    cycleDuration,
                    cycle);
                double loopEpochShiftSeconds = currentUT - effUT;

                Vessel inst = CreateOverlapInstanceVessel(
                    recIdx, rec, cycle, effUT, loopEpochShiftSeconds,
                    liveLaunchMatchedAnchorOfActiveMember);
                if (inst != null)
                {
                    created++;
                    frameSpawnCount++;
                }
            }

            if (created > 0 || destroyed > 0)
            {
                ParsekLog.VerboseRateLimited(Tag, "overlap-lifecycle-" + recIdx,
                    string.Format(ic,
                        "Overlap per-instance lifecycle rec=#{0} \"{1}\" cycles=[{2}..{3}] " +
                        "created={4} destroyed={5} liveNow={6} currentUT={7:F1}",
                        recIdx, rec.VesselName ?? "(null)", firstCycle, lastCycle,
                        created, destroyed, GetOverlapInstanceCount(recIdx), currentUT),
                    2.0);
            }

            return created;
        }

        /// <summary>
        /// Per-frame overlap sweep, the SOLE create/destroy authority for overlap recordings on the
        /// map + Tracking Station presence layers. Drives each overlap recording through
        /// <see cref="EnsureOverlapInstances"/> (gated on
        /// <see cref="ShouldDriveOverlapPerInstance"/>) and reaps any leftover per-instance vessels
        /// for indices that are no longer overlap recordings (gate flipped off, recording stopped
        /// looping, period grew past duration, or the committed list shrank). Threads a per-frame
        /// spawn counter so a burst of newly-relaunching cycles is throttled exactly like the flight
        /// engine. Called from both <see cref="UpdateFlightMapGhostLifecycle"/> and
        /// <see cref="UpdateTrackingStationGhostLifecycle"/>. <paramref name="loopUnits"/> is the
        /// per-frame <see cref="GhostPlaybackEngine.CurrentLoopUnits"/> the scene drivers already hold
        /// (null-coalesced to Empty), so a Mission-tab loop (source b) drives the per-instance path
        /// even when <c>rec.LoopPlayback</c> is false. With the gate OFF nothing here runs (every
        /// recording fails <see cref="ShouldDriveOverlapPerInstance"/>) AND the leftover reaper tears
        /// down any instances created while the gate was on, so the scene falls cleanly back to the
        /// legacy one-per-recording path.
        /// </summary>
        internal static void RunOverlapPerInstanceSweep(
            double currentUT, IReadOnlyList<Recording> committed,
            GhostPlaybackLogic.LoopUnitSet loopUnits)
        {
            if (loopUnits == null)
                loopUnits = GhostPlaybackLogic.LoopUnitSet.Empty;

            // Game time the cycle window moved since the previous sweep + the live warp rate: the
            // inputs of the high-warp collapse gate EnsureOverlapInstances applies per recording.
            double sweepGameSeconds = ResolveOverlapSweepGameSeconds(lastOverlapSweepUT, currentUT);
            lastOverlapSweepUT = currentUT;
            float warpRate = ReadOverlapSweepWarpRate();
            ResetOverlapCollapseTally();

            // Snapshot the set of indices that currently have any per-instance vessel, so we can
            // reap the ones that should no longer be driven per-instance after the create pass below.
            HashSet<int> indicesWithInstances = null;
            if (overlapInstanceVessels.Count > 0)
            {
                indicesWithInstances = new HashSet<int>();
                foreach (var kvp in overlapInstanceVessels)
                    indicesWithInstances.Add(kvp.Key.recIdx);
            }

            int frameSpawnCount = 0;
            HashSet<int> drivenThisFrame = null;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    var rec = committed[i];
                    bool shouldDrive = ShouldDriveOverlapPerInstance(rec, i, committed, loopUnits);

                    // GATE-DECISION TRACE (the blind spot that cost us a playtest): one rate-limited
                    // line PER committed recording explaining the verdict, BEFORE the continue. A
                    // re-fly Mission member shows isMember=true unitOverlaps=true even when
                    // rec.LoopPlayback=false, so a "one icon instead of N" report is diagnosable from
                    // the log alone.
                    LogOverlapGateDecision(i, rec, committed, loopUnits, shouldDrive);

                    if (!shouldDrive)
                    {
                        // BOUNDARY-OVERLAP secondary (docs/dev/plan-launch-boundary-overlap.md 5.1): a launch-hold
                        // re-aim unit member is NON-overlap (ShouldDriveOverlapPerInstance is false), so its PRIMARY
                        // map vessel lives in vesselsByRecordingIndex via the per-index create path. During the
                        // borrow window of a zero-slack loop the early-launching NEXT instance N+1 needs its own map
                        // ProtoVessel (icon + escape conic). Create it INSIDE this sweep so the reaper below spares
                        // it (register the index in drivenThisFrame) and tears it down automatically the frame the
                        // borrow window ends. This reuses the create/seed/reap plumbing verbatim - the secondary's
                        // in-SOI escape conic resolves to a Segment source (verified for aa48920e), so it is
                        // loop-shift driven exactly like an overlap instance. Inert (NoSecondary) for every
                        // non-launch-hold member and every already-aligned loop.
                        if (TryEnsureBoundaryOverlapSecondaryInstance(
                                i, rec, currentUT, committed, loopUnits, ref frameSpawnCount))
                        {
                            if (drivenThisFrame == null) drivenThisFrame = new HashSet<int>();
                            drivenThisFrame.Add(i);
                        }
                        continue;
                    }
                    EnsureOverlapInstances(i, rec, currentUT, committed, loopUnits, ref frameSpawnCount,
                        warpRate, sweepGameSeconds);
                    if (drivenThisFrame == null) drivenThisFrame = new HashSet<int>();
                    drivenThisFrame.Add(i);
                }
            }

            // Reap: any index that has per-instance vessels but was NOT driven this frame is no longer
            // an overlap recording under the active gate (or fell off the committed list). Tear its
            // instances down so the legacy one-per-recording path can resume cleanly (or so the gate-off
            // fallback renders the single legacy icon).
            if (indicesWithInstances != null)
            {
                foreach (int idx in indicesWithInstances)
                {
                    if (drivenThisFrame != null && drivenThisFrame.Contains(idx))
                        continue;
                    DestroyAllOverlapInstancesForRecording(idx, "overlap-no-longer-per-instance");
                }
            }

            LogOverlapCollapseSummary(warpRate, sweepGameSeconds);
        }

        /// <summary>
        /// BOUNDARY-OVERLAP secondary map presence (docs/dev/plan-launch-boundary-overlap.md 5.1): when committed
        /// recording <paramref name="recIdx"/> is a launch-hold re-aim unit member that carries a live
        /// boundary-overlap secondary this frame (the early-launching NEXT instance N+1, during the borrow window of
        /// a zero-slack loop), ensure ONE <see cref="overlapInstanceVessels"/> entry keyed (recIdx, secondaryCycle)
        /// seeded at the secondary's loop-mapped sample UT, and reap any stale secondary cycle for this index. Reuses
        /// <see cref="CreateOverlapInstanceVessel"/> (so the in-SOI escape conic resolves to a Segment source and is
        /// loop-shift driven exactly like an overlap instance). Returns true when this index has a live
        /// boundary-overlap secondary this frame (so the caller registers it in <c>drivenThisFrame</c> and the
        /// reaper spares it); false otherwise (NoSecondary -> nothing created, any stale secondary already reaped).
        /// Inert for every non-member, every non-launch-hold member, and every already-aligned loop. Single-ownership
        /// is preserved: the PRIMARY stays in <see cref="vesselsByRecordingIndex"/>; the secondary lives ONLY here.
        /// </summary>
        private static bool TryEnsureBoundaryOverlapSecondaryInstance(
            int recIdx, Recording rec, double currentUT,
            IReadOnlyList<Recording> committed,
            GhostPlaybackLogic.LoopUnitSet loopUnits,
            ref int frameSpawnCount)
        {
            if (rec == null || loopUnits == null || loopUnits.Count == 0)
                return false;
            if (!loopUnits.TryGetUnitForMember(recIdx, out GhostPlaybackLogic.LoopUnit unit))
                return false;
            if (!unit.LaunchHoldEngaged)
                return false;

            // Resolve the dual-clock frame for THIS member: primary sample UT + the optional boundary-overlap
            // secondary in this member's window. The PRIMARY is handled by the per-index create path (this method
            // only owns the secondary). NoSecondary -> reap any stale secondary for this index and return false.
            double memberStartUT = unit.MemberStartUT(recIdx, rec.StartUT);
            double memberEndUT = unit.MemberEndUT(recIdx, rec.EndUT);
            GhostPlaybackLogic.BoundaryOverlapSecondaryDecision decision =
                GhostPlaybackLogic.DecideBoundaryOverlapSecondaryRender(
                    currentUT, unit.PhaseAnchorUT, unit.SpanStartUT, unit.SpanEndUT, unit.CadenceSeconds,
                    memberStartUT, memberEndUT, out double secondaryUT, out long secondaryCycle,
                    unit.RelaunchSchedule, unit.LoiterCuts,
                    unit.ArrivalHoldSeconds, unit.ArrivalHoldAtUT, unit.ArrivalAlignPeriodSeconds,
                    unit.LaunchBodyRotationPeriodSeconds, unit.LaunchHoldEngaged, unit.RecordedSoiExitUT,
                    unit.ArrivalJointSecondaryPeriodSeconds, unit.ArrivalJointSecondaryToleranceSeconds,
                    unit.ArrivalJointMaxWholeHoldPeriods);

            if (decision != GhostPlaybackLogic.BoundaryOverlapSecondaryDecision.Render)
            {
                // No live secondary this frame: reap any stale boundary-secondary instance for this index. (The
                // outer reaper would also catch it via drivenThisFrame, but tear it down here so a NoSecondary frame
                // does not leave a lingering icon for one extra frame.)
                ReapBoundaryOverlapSecondaryInstances(recIdx, secondaryCycle: long.MinValue,
                    reason: "boundary-overlap-secondary-window-ended");
                return false;
            }

            // Reap any stale secondary cycle for this index (the borrow window's instance advanced to the next
            // N+1), keeping only the current secondaryCycle.
            ReapBoundaryOverlapSecondaryInstances(recIdx, secondaryCycle,
                reason: "boundary-overlap-secondary-cycle-advanced");

            // Already live for this cycle: nothing to create, but report the live secondary so the reaper spares it.
            if (overlapInstanceVessels.ContainsKey((recIdx, secondaryCycle)))
                return true;

            // Respect the shared per-frame spawn throttle exactly like EnsureOverlapInstances.
            if (GhostPlaybackLogic.ShouldThrottleSpawn(frameSpawnCount, GhostPlayback.MaxSpawnsPerFrame))
            {
                ParsekLog.VerboseRateLimited(Tag,
                    string.Format(ic, "boundary-overlap-secondary-throttle-{0}", recIdx),
                    string.Format(ic,
                        "Boundary-overlap secondary create throttled rec=#{0} \"{1}\" cycle={2} " +
                        "(used {3}/{4} spawns this frame)",
                        recIdx, rec.VesselName ?? "(null)", secondaryCycle,
                        frameSpawnCount, GhostPlayback.MaxSpawnsPerFrame),
                    1.0);
                // Still report the secondary live so the reaper does not destroy a (deferred) create next frame.
                return true;
            }

            double loopEpochShiftSeconds = currentUT - secondaryUT;
            Vessel inst = CreateOverlapInstanceVessel(
                recIdx, rec, secondaryCycle, secondaryUT, loopEpochShiftSeconds,
                out TrackingStationGhostSource diagSource, out OrbitSegment diagSegment,
                liveLaunchMatchedAnchorOfActiveMember: false);

            // SEAM-RENDER OBSERVABILITY 2 (docs/dev/design-reaim-launch-hold-seam.md): the secondary's MAP
            // presence (icon + escape conic) first-create truth, INCLUDING the create-returned-null /
            // no-accepted-source case (the leading-hypothesis smoking gun). created=false + source=none means
            // the post-ascent in-SOI escape window has NO accepted Segment yet, so the icon/conic only
            // materializes once the secondaryLoopUT reaches the recorded escape Segment - that lag (the few
            // minutes the prompt reports) is lagFromSpanStartSec = secondaryLoopUT - spanStart at the moment of
            // create. segmentUT names the covering Segment span when one resolved. Rate-limited per
            // (recIdx, secondaryCycle) so each borrow window's first-create truth is captured exactly once;
            // logging-only, no control-flow effect (created is read from the create result, not a new gate).
            string segUtStr = (diagSource == TrackingStationGhostSource.Segment
                               || diagSource == TrackingStationGhostSource.EndpointTail)
                ? string.Format(ic, "{0:F1}-{1:F1}", diagSegment.startUT, diagSegment.endUT)
                : "n/a";
            double lagFromSpanStart = secondaryUT - unit.SpanStartUT;
            ParsekLog.VerboseRateLimited(Tag,
                string.Format(ic, "boundary-overlap-secondary-map-presence-{0}-{1}", recIdx, secondaryCycle),
                string.Format(ic,
                    "boundary-overlap secondary map-presence: created={0} currentUT={1:R} secondaryLoopUT={2:R} " +
                    "source={3} segmentUT={4} lagFromSpanStartSec={5:R} rec=#{6} \"{7}\" cycle={8}",
                    inst != null, currentUT, secondaryUT, diagSource, segUtStr, lagFromSpanStart,
                    recIdx, rec.VesselName ?? "(null)", secondaryCycle),
                2.0);

            if (inst != null)
            {
                // M1: tag this pid so the vesselsByRecordingIndex-reader fall-through
                // (GetGhostVesselPidForRecording / HasGhostVesselForRecording) does not let the secondary
                // shadow the launch recording's identity. Dropped on reap in RemoveOverlapInstance.
                boundaryOverlapSecondaryPids.Add(inst.persistentId);
                frameSpawnCount++;
                ParsekLog.VerboseRateLimited(Tag,
                    string.Format(ic, "boundary-overlap-secondary-create-{0}", recIdx),
                    string.Format(ic,
                        "Created boundary-overlap secondary map vessel rec=#{0} \"{1}\" cycle={2} " +
                        "secondaryUT={3:F1} loopShift={4:F1}",
                        recIdx, rec.VesselName ?? "(null)", secondaryCycle, secondaryUT, loopEpochShiftSeconds),
                    2.0);
            }
            // Report live even if the create deferred (no Segment yet): the reaper must not tear down a pending one.
            return true;
        }

        /// <summary>
        /// Reaps boundary-overlap secondary instances for <paramref name="recIdx"/>, destroying every per-instance
        /// vessel for this index whose cycle is NOT <paramref name="secondaryCycle"/> (pass <c>long.MinValue</c> to
        /// reap all). Only touches entries whose cycle differs from the live secondary cycle, so it never destroys a
        /// genuine self-overlap instance (a launch-hold unit member never has self-overlap instances anyway). The
        /// primary in <see cref="vesselsByRecordingIndex"/> is untouched.
        /// </summary>
        private static void ReapBoundaryOverlapSecondaryInstances(int recIdx, long secondaryCycle, string reason)
        {
            List<long> stale = null;
            foreach (var kvp in overlapInstanceVessels)
            {
                if (kvp.Key.recIdx == recIdx && kvp.Key.cycle != secondaryCycle)
                {
                    if (stale == null) stale = new List<long>();
                    stale.Add(kvp.Key.cycle);
                }
            }
            if (stale == null)
                return;
            for (int i = 0; i < stale.Count; i++)
                RemoveOverlapInstance(recIdx, stale[i], reason);
        }

        /// <summary>
        /// Per-recording gate-decision trace (rate-limited, per-index key). Surfaces every input to the
        /// overlap verdict so a "one icon instead of N" report is diagnosable from the log WITHOUT a
        /// rebuild: the standalone source (a) inputs (rec.LoopPlayback +
        /// IsOverlapLoop result), the Mission source (b) inputs (isMember / overlapCadence / span /
        /// unitOverlaps), the resolved schedule tuple (scheduleStart / duration / effectiveCadence), and
        /// the final ShouldDriveOverlapPerInstance verdict + the live cycle window when driven. Reuses
        /// the same pure resolvers the verdict uses, so the log can never disagree with the decision.
        /// Unity-free (reads only the pure resolvers + the test-overridable <c>CurrentUTNow</c>), so the
        /// xUnit log-assertion test can drive it directly.
        /// </summary>
        internal static void LogOverlapGateDecision(
            int recIdx, Recording rec, IReadOnlyList<Recording> committed,
            GhostPlaybackLogic.LoopUnitSet loopUnits, bool shouldDrive)
        {
            // Source (a) inputs.
            bool recLoopPlayback = rec != null && rec.LoopPlayback;
            double autoDuration = rec != null && GhostPlaybackEngine.ShouldLoopPlayback(rec)
                ? GhostPlaybackEngine.EffectiveLoopDuration(rec) : double.NaN;
            double autoInterval = double.NaN;
            bool autoOverlap = false;
            if (recLoopPlayback && !double.IsNaN(autoDuration) && autoDuration > LoopTiming.MinLoopDurationSeconds)
            {
                autoInterval = ResolveOverlapLoopIntervalSeconds(rec, recIdx, committed);
                autoOverlap = GhostPlaybackLogic.IsOverlapLoop(autoInterval, autoDuration);
            }

            // Source (b) inputs.
            bool isMember = loopUnits != null
                && loopUnits.TryGetUnitForMember(recIdx, out GhostPlaybackLogic.LoopUnit unit);
            double overlapCadence = double.NaN;
            double span = double.NaN;
            bool unitOverlaps = false;
            if (isMember)
            {
                loopUnits.TryGetUnitForMember(recIdx, out unit);
                overlapCadence = unit.OverlapCadenceSeconds;
                span = unit.SpanEndUT - unit.SpanStartUT;
                unitOverlaps = GhostPlaybackLogic.UnitMemberOverlaps(unit);
            }

            // Resolved schedule tuple (whichever source won), + the live cycle window when driven.
            string scheduleStr = "(none)";
            string cycleWindowStr = "(not-driven)";
            if (ResolveOverlapSchedule(
                    rec, recIdx, committed, loopUnits,
                    out _, out double scheduleStartUT,
                    out double duration, out double effectiveCadence, out _))
            {
                scheduleStr = string.Format(ic,
                    "scheduleStart={0:F1} duration={1:F1} effectiveCadence={2:F1}",
                    scheduleStartUT, duration, effectiveCadence);
                if (shouldDrive)
                {
                    OverlapCyclesForTesting(rec, recIdx, committed, loopUnits, CurrentUTNow(),
                        out long firstCycle, out long lastCycle);
                    cycleWindowStr = string.Format(ic,
                        "cycles=[{0}..{1}] liveNow={2}", firstCycle, lastCycle,
                        GetOverlapInstanceCount(recIdx));
                }
            }

            string message = string.Format(ic,
                "Overlap gate decision rec=#{0} \"{1}\": "
                + "(a) loopPlayback={2} autoIsOverlapLoop={3} | "
                + "(b) isMember={4} overlapCadence={5:F1} span={6:F1} unitOverlaps={7} | "
                + "{8} | verdict shouldDrive={9} {10}",
                recIdx, rec?.VesselName ?? "(null)",
                recLoopPlayback, autoOverlap,
                isMember, overlapCadence, span, unitOverlaps,
                scheduleStr, shouldDrive, cycleWindowStr);

            // This verdict is STABLE across the vast majority of frames (almost always
            // "not-driven"), so the old per-index time-based rate limit still re-emitted
            // every interval forever: ~48k lines (~15% of all verbose output) in the
            // 2026-06-07 career playtest. Switch to change-detection so the line fires
            // only when the decision actually flips. Identity is the stable RecordingId
            // (committed recordings always have one), so a different recording reusing a
            // positional index gets its own identity; the positional "idx-N" fallback is
            // best-effort and only reachable for an id-less recording (not expected here).
            // The state key carries only the boolean decision facets so the continuously
            // varying cadence / span / cycle-window floats don't defeat coalescing while
            // a recording is driven.
            string identity = !string.IsNullOrEmpty(rec?.RecordingId)
                ? rec.RecordingId
                : "idx-" + recIdx.ToString(ic);
            string stateKey = string.Format(ic, "{0}|{1}|{2}|{3}|{4}",
                shouldDrive, recLoopPlayback, autoOverlap, isMember, unitOverlaps);
            ParsekLog.VerboseOnChange(Tag, identity, stateKey, message);
        }

        /// <summary>
        /// Create ONE per-instance overlap map ProtoVessel for (recIdx, cycle) at its loop-mapped
        /// <paramref name="effUT"/>. Mirrors ParsekKSC.SpawnKscGhost's "fresh object per instance, no
        /// per-index early-return" model rather than the per-index
        /// <see cref="CreateGhostVesselFromSource"/> funnel (which early-returns the existing
        /// per-index vessel and writes the single <see cref="vesselsByRecordingIndex"/> slot via
        /// <see cref="TrackRecordingGhostVessel"/>). Registers PER INSTANCE: the new pid into
        /// <see cref="ghostMapVesselPids"/>, the pid-&gt;index / pid-&gt;id reverse maps, the per-pid
        /// loop epoch shift, and the per-pid orbit bounds the icon-drive / arc-clip patches read. The
        /// orbit source is resolved at <paramref name="effUT"/> exactly like the per-index create.
        /// </summary>
        private static Vessel CreateOverlapInstanceVessel(
            int recIdx, Recording rec, long cycle, double effUT, double loopEpochShiftSeconds,
            bool liveLaunchMatchedAnchorOfActiveMember = false)
        {
            // Diagnostic-only overload-style wrapper: the boundary-overlap secondary create
            // (TryEnsureBoundaryOverlapSecondaryInstance) reads back the resolved source / covering
            // segment to emit observability 2 (the map-presence pre-Segment gap). The regular overlap
            // sweep does not need them, so it routes through this thin wrapper that discards them.
            return CreateOverlapInstanceVessel(
                recIdx, rec, cycle, effUT, loopEpochShiftSeconds,
                out _, out _, liveLaunchMatchedAnchorOfActiveMember);
        }

        private static Vessel CreateOverlapInstanceVessel(
            int recIdx, Recording rec, long cycle, double effUT, double loopEpochShiftSeconds,
            out TrackingStationGhostSource diagSource, out OrbitSegment diagSegment,
            bool liveLaunchMatchedAnchorOfActiveMember = false)
        {
            diagSource = TrackingStationGhostSource.None;
            diagSegment = default(OrbitSegment);

            if (overlapInstanceVessels.ContainsKey((recIdx, cycle)))
                return overlapInstanceVessels[(recIdx, cycle)];

            // Resolve the covering orbit source at this instance's effUT (segment / state-vector /
            // terminal-orbit), the same resolution the per-index create uses.
            int cachedStateVectorIndex = -1;
            TrackingStationGhostSource source = ResolveMapPresenceGhostSource(
                rec,
                false,
                IsMaterializedForMapPresence(rec),
                effUT,
                true,
                "overlap-instance-create",
                ref cachedStateVectorIndex,
                out OrbitSegment segment,
                out TrajectoryPoint stateVectorPoint,
                out string skipReason,
                recordingIndex: recIdx,
                // A live overlap instance always has a non-zero epoch shift, so it is "in window".
                loopMemberInWindow: true,
                liveLaunchMatchedAnchorOfActiveMember: liveLaunchMatchedAnchorOfActiveMember);

            // Read-back-only: expose the resolved source + covering segment to the boundary-overlap
            // secondary diagnostic. No control-flow effect (the create decisions below read `source` /
            // `segment` directly, exactly as before).
            diagSource = source;
            diagSegment = segment;

            if (!IsMapCreateAcceptedSource(source))
            {
                // Throttle key is per-RECORDING, NOT per-cycle: at high time warp the overlap cycle
                // index advances every frame, so a per-cycle key yields a fresh key each frame and
                // defeats VerboseRateLimited (a per-frame flood). Every cycle of a non-orbital recording
                // skips for the same reason, so one line per recording per interval suffices; the cycle
                // stays in the message for context.
                ParsekLog.VerboseRateLimited(Tag,
                    string.Format(ic, "overlap-instance-no-source-{0}", recIdx),
                    string.Format(ic,
                        "Overlap per-instance create skipped rec=#{0} \"{1}\" cycle={2} effUT={3:F1} " +
                        "source={4} reason={5} (no map-visible orbit this cycle yet)",
                        recIdx, rec.VesselName ?? "(null)", cycle, effUT, source,
                        skipReason ?? "(none)"),
                    2.0);
                return null;
            }

            // Build the ProtoVessel directly (fresh KSP-unique pid via CreateVesselNode vesselID:0).
            // Segment / EndpointTail seed the orbit from the covering segment; TerminalOrbit /
            // state-vector seed from the recording's terminal/endpoint orbit.
            string logContext = string.Format(ic,
                "overlap instance rec=#{0} cycle={1} (source={2})", recIdx, cycle, source);
            Vessel vessel;
            switch (source)
            {
                case TrackingStationGhostSource.Segment:
                case TrackingStationGhostSource.EndpointTail:
                    vessel = BuildAndLoadGhostProtoVessel(
                        rec, segment, logContext,
                        source == TrackingStationGhostSource.EndpointTail
                            ? "overlap-instance-endpoint-tail"
                            : "overlap-instance-segment");
                    break;
                case TrackingStationGhostSource.TerminalOrbit:
                    vessel = BuildAndLoadGhostProtoVessel(rec, logContext);
                    break;
                case TrackingStationGhostSource.StateVector:
                case TrackingStationGhostSource.StateVectorSoiGap:
                    vessel = BuildAndLoadGhostProtoVesselFromStateVector(
                        rec, stateVectorPoint, effUT, loopEpochShiftSeconds, logContext);
                    break;
                default:
                    return null;
            }

            if (vessel == null)
                return null;

            // Register this instance PER PID (single-ownership store + the per-pid maps the icon /
            // arc / marker paths read). Do NOT write vesselsByRecordingIndex (that is the legacy
            // one-per-recording slot; overlap recordings live solely in overlapInstanceVessels).
            uint pid = vessel.persistentId;
            overlapInstanceVessels[(recIdx, cycle)] = vessel;
            ghostMapVesselPids.Add(pid);
            vesselPidToRecordingIndex[pid] = recIdx;
            if (!string.IsNullOrEmpty(rec.RecordingId))
                vesselPidToRecordingId[pid] = rec.RecordingId;
            else
                vesselPidToRecordingId.Remove(pid);

            if (source == TrackingStationGhostSource.Segment
                || source == TrackingStationGhostSource.EndpointTail)
            {
                // Segment / orbit path: seed the RAW recorded epoch + record the loop shift + per-pid
                // bounds so GhostOrbitIconDrivePatch drives the OrbitDriver at effUT = liveUT - shift
                // every frame (slice i: the per-pid ghostOrbitEpochShift IS the phase, NOT an
                // instanceKey). Mirrors the per-index segment contract (ApplyOrbitToVessel).
                ApplyOrbitToVessel(vessel, segment, logContext, loopEpochShiftSeconds);
            }
            else
            {
                // Terminal-orbit + state-vector instances mirror the per-index NON-segment contract:
                // the orbit is seeded at the SHIFTED epoch (live UT) and stock OrbitDriver propagates
                // it at the live Planetarium clock - there is no raw-epoch + effUT-drive contract
                // (no OrbitSegments / bounds). So this branch MUST NOT register ghostOrbitEpochShift /
                // ghostOrbitLoopShiftedPids: doing so makes GhostOrbitIconDrivePatch bail on the
                // no-bounds branch and stock then advances the icon by `shift` along the orbit (wrong
                // phase). Clear any stale segment-drive entries (defensive; the pid is fresh here, but
                // this mirrors the per-index state-vector clear at GhostMapPresence.cs:7969-7973).
                ghostOrbitBounds.Remove(pid);
                ghostBodyFrameOrbitBounds.Remove(pid);
                ghostOrbitLoopShiftedPids.Remove(pid);
                ghostOrbitEpochShift.Remove(pid);
            }

            lifecycleCreatedThisTick++;

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Created overlap per-instance map vessel rec=#{0} \"{1}\" cycle={2} pid={3} " +
                    "source={4} effUT={5:F1} loopShift={6:F1}",
                    recIdx, rec.VesselName ?? "(null)", cycle, pid, source, effUT, loopEpochShiftSeconds));

            return vessel;
        }

        /// <summary>
        /// Remove ONE per-instance overlap map ProtoVessel for (recIdx, cycle): Die() the vessel,
        /// drop all per-pid map entries, and clear the store slot. Mirrors
        /// <see cref="RemoveGhostVesselForRecording"/> but keyed on the (recIdx, cycle) instance and
        /// the per-instance store (never touches <see cref="vesselsByRecordingIndex"/>).
        /// </summary>
        internal static void RemoveOverlapInstance(int recIdx, long cycle, string reason)
        {
            if (!overlapInstanceVessels.TryGetValue((recIdx, cycle), out Vessel vessel))
                return;

            uint ghostPid = vessel != null ? vessel.persistentId : 0u;

            if (vessel != null)
            {
                BeginGhostTeardown();
                try { vessel.Die(); }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag,
                        string.Format(ic,
                            "RemoveOverlapInstance: Die() threw for rec=#{0} cycle={1}: {2}",
                            recIdx, cycle, ex.Message));
                }
                finally
                {
                    EndGhostTeardown();
                }
            }

            if (ghostPid != 0)
            {
                boundaryOverlapSecondaryPids.Remove(ghostPid); // M1: no-op for genuine self-overlap instances
                ghostMapVesselPids.Remove(ghostPid);
                ghostsWithSuppressedIcon.Remove(ghostPid);
                ghostNoBoundsSuppressLastFrame.Remove(ghostPid);
                ghostOrbitBounds.Remove(ghostPid);
                ghostBodyFrameOrbitBounds.Remove(ghostPid);
                ghostLastAppliedOrbitBody.Remove(ghostPid);
                ghostLastAppliedOrbitElements.Remove(ghostPid);
                ghostOrbitLoopShiftedPids.Remove(ghostPid);
                ghostOrbitEpochShift.Remove(ghostPid);
                ghostIconDrivePropagation.Remove(ghostPid);
                vesselPidToRecordingIndex.Remove(ghostPid);
                vesselPidToRecordingId.Remove(ghostPid);
            }
            overlapInstanceVessels.Remove((recIdx, cycle));
            lifecycleDestroyedThisTick++;

            ParsekLog.VerboseRateLimited(Tag, "overlap-remove-" + recIdx,
                string.Format(ic,
                    "Removed overlap per-instance map vessel rec=#{0} cycle={1} pid={2} reason={3}",
                    recIdx, cycle, ghostPid, reason ?? "(none)"),
                2.0);
        }

        /// <summary>
        /// Destroy every per-instance overlap map vessel for a recording index (e.g. when the
        /// recording is no longer an overlap loop, became inactive, or the gate flipped off). Mirrors
        /// ParsekKSC.DestroyAllKscOverlapGhosts.
        /// </summary>
        internal static void DestroyAllOverlapInstancesForRecording(int recIdx, string reason)
        {
            List<long> cycles = null;
            foreach (var kvp in overlapInstanceVessels)
            {
                if (kvp.Key.recIdx != recIdx)
                    continue;
                if (cycles == null) cycles = new List<long>();
                cycles.Add(kvp.Key.cycle);
            }
            if (cycles == null)
                return;
            for (int i = 0; i < cycles.Count; i++)
                RemoveOverlapInstance(recIdx, cycles[i], reason);
        }

        /// <summary>
        /// Number of live per-instance overlap map vessels for a recording index. Exposed for the
        /// in-game test (asserts map count == flight overlap count + 1, capped at the per-recording
        /// cap).
        /// </summary>
        internal static int GetOverlapInstanceCount(int recIdx)
        {
            int count = 0;
            foreach (var kvp in overlapInstanceVessels)
            {
                if (kvp.Key.recIdx == recIdx)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// The pid of the NEWEST live overlap instance (highest cycle) for a recording index, or 0
        /// when none. Used by the <see cref="vesselsByRecordingIndex"/>-reader fall-through
        /// (<see cref="GetGhostVesselPidForRecording"/> / <see cref="HasGhostVesselForRecording"/>) so
        /// watch-camera focus, TS-Fly, UI marker suppression, and the polyline owner resolve get the
        /// newest instance's pid (matching the legacy "one icon == newest cycle" behavior) instead of 0.
        /// </summary>
        internal static uint GetNewestOverlapInstancePidForRecording(
            int recIdx, bool excludeBoundarySecondary = false)
        {
            uint newestPid = 0;
            long newestCycle = long.MinValue;
            foreach (var kvp in overlapInstanceVessels)
            {
                if (kvp.Key.recIdx != recIdx)
                    continue;
                if (kvp.Value == null)
                    continue;
                // M1: the vesselsByRecordingIndex-reader fall-through excludes the boundary-overlap secondary
                // so it never shadows the launch recording's identity (the polyline's second-head resolution
                // passes false and still gets it). No-op for genuine self-overlap instances (not in the set).
                if (excludeBoundarySecondary && boundaryOverlapSecondaryPids.Contains(kvp.Value.persistentId))
                    continue;
                if (kvp.Key.cycle > newestCycle)
                {
                    newestCycle = kvp.Key.cycle;
                    newestPid = kvp.Value.persistentId;
                }
            }
            return newestPid;
        }

        /// <summary>
        /// Test-only (Unity-free): resolve the active cycle window [firstCycle, lastCycle] this
        /// recording's per-instance path WOULD drive at <paramref name="currentUT"/>, without minting
        /// any ProtoVessel. Mirrors the exact schedule resolution + GetActiveCycles call inside
        /// <see cref="EnsureOverlapInstances"/> so xUnit can pin the cycle-set equivalence with the
        /// flight engine. Returns (0, -1) (empty window) when the recording is not a resolvable loop
        /// or currentUT precedes the schedule start.
        /// </summary>
        internal static void OverlapCyclesForTesting(
            Recording rec, int recIdx, IReadOnlyList<Recording> committed,
            GhostPlaybackLogic.LoopUnitSet loopUnits, double currentUT,
            out long firstCycle, out long lastCycle)
        {
            firstCycle = 0;
            lastCycle = -1;
            if (!ResolveOverlapSchedule(
                    rec, recIdx, committed, loopUnits,
                    out _, out double scheduleStartUT,
                    out double duration, out double effectiveCadence, out _))
                return;
            if (currentUT < scheduleStartUT)
                return;
            GhostPlaybackLogic.GetActiveCycles(
                currentUT, scheduleStartUT, scheduleStartUT + duration,
                effectiveCadence, GhostPlayback.MaxOverlapGhostsPerRecording,
                out firstCycle, out lastCycle);
        }

        /// <summary>Test-only: snapshot the live (recIdx, cycle) keys for an index.</summary>
        internal static List<long> GetOverlapInstanceCyclesForTesting(int recIdx)
        {
            var cycles = new List<long>();
            foreach (var kvp in overlapInstanceVessels)
            {
                if (kvp.Key.recIdx == recIdx)
                    cycles.Add(kvp.Key.cycle);
            }
            cycles.Sort();
            return cycles;
        }

        /// <summary>
        /// Slice (iii): resolve the per-instance playback head UTs that the flight-map marker path
        /// should ride for an OVERLAP recording this frame - one entry per live overlap cycle, each
        /// at its own <see cref="GhostPlaybackLogic.ComputeOverlapCyclePlaybackUT"/> head (NOT the
        /// span-clock collapse of <see cref="GhostPlaybackLogic.ResolveTrackingStationSampleUT"/>).
        /// The cycle SET is byte-identical to the slice-(i) <see cref="OverlapCyclesForTesting"/> /
        /// <see cref="EnsureOverlapInstances"/> set (same gate + same schedule + same GetActiveCycles
        /// call), so the N markers on the shared polyline match the N flight meshes.
        ///
        /// Returns false (so the caller's legacy single-marker tail runs byte-identically) when the
        /// recording is not driven by the per-instance path (gate off / non-overlap), the schedule is
        /// unresolvable, or <paramref name="currentUT"/> precedes the first launch. On true,
        /// <paramref name="outBuffer"/> holds (cycle, headUT) for each live cycle in
        /// [firstCycle, lastCycle].
        /// </summary>
        internal static bool TryGetLiveOverlapHeadUTs(
            Recording rec, int recIdx, IReadOnlyList<Recording> committed,
            GhostPlaybackLogic.LoopUnitSet loopUnits, double currentUT,
            List<(long cycle, double headUT)> outBuffer)
        {
            if (outBuffer == null)
                return false;
            outBuffer.Clear();

            if (!ShouldDriveOverlapPerInstance(rec, recIdx, committed, loopUnits))
                return false;

            if (!ResolveOverlapSchedule(
                    rec, recIdx, committed, loopUnits,
                    out double playbackStartUT, out double scheduleStartUT,
                    out double duration, out double effectiveCadence, out double cycleDuration))
                return false;

            if (currentUT < scheduleStartUT)
                return false;

            GhostPlaybackLogic.GetActiveCycles(
                currentUT, scheduleStartUT, scheduleStartUT + duration,
                effectiveCadence, GhostPlayback.MaxOverlapGhostsPerRecording,
                out long firstCycle, out long lastCycle);

            for (long cycle = firstCycle; cycle <= lastCycle; cycle++)
            {
                double headUT = GhostPlaybackLogic.ComputeOverlapCyclePlaybackUT(
                    currentUT, scheduleStartUT, playbackStartUT,
                    duration, cycleDuration, cycle);
                outBuffer.Add((cycle, headUT));
            }

            return true;
        }

        /// <summary>
        /// Launch-&gt;escape seam render: resolve whether a launch-hold re-aim member has a LIVE
        /// boundary-overlap SECONDARY head this frame whose in-SOI ASCENT icon must be drawn as an
        /// ADDITIVE non-proto polyline marker (the early-launching instance N+1's ascent during the
        /// borrow window of a zero-slack re-aim launch loop). The secondary's own proto icon + escape
        /// conic come from the map-presence sweep ONCE its escape <c>OrbitSegment</c> materializes;
        /// this only covers the PRE-SEGMENT ascent gap, where no proto can exist (an atmospheric ascent
        /// has no orbit, so <c>ResolveMapPresenceGhostSource</c> returns None and the create defers).
        /// The caller rides this UT on the SHARED polyline via <c>DrawOneOverlapInstanceMarker</c> /
        /// <c>DrawOneTsOverlapInstanceMarker</c>, which self-gates per cycle (a live non-suppressed
        /// proto icon for the cycle skips the marker, so there is no double once the Segment exists).
        ///
        /// Returns false - with <paramref name="secondaryUT"/>/<paramref name="secondaryCycle"/>
        /// defaulted - for every non-map surface, debris recording, null recording, and any member
        /// without a live secondary head this frame (every non-launch-hold member and every ALIGNED
        /// loop, where <c>ResolveTrackingStationSampleFrame</c> reports <c>hasSecondary=false</c>), so
        /// the caller's additive marker draw stays inert there. Map-surface only because the secondary
        /// head needs the live UT + the shared map polyline to ride (flight view passes currentUT=0 and
        /// has no map polyline).
        /// </summary>
        internal static bool TryResolveBoundaryOverlapSecondaryMarker(
            bool isMapSurface, Recording rec, int recIdx, double liveUT,
            GhostPlaybackLogic.LoopUnitSet loopUnits,
            out double secondaryUT, out long secondaryCycle)
        {
            secondaryUT = liveUT;
            secondaryCycle = 0;
            if (!isMapSurface || rec == null || rec.IsDebris)
                return false;
            GhostPlaybackLogic.ResolveTrackingStationSampleFrame(
                recIdx, rec.StartUT, rec.EndUT, liveUT, loopUnits,
                out _, out bool hasSecondary, out secondaryUT, out secondaryCycle);
            return hasSecondary;
        }

        /// <summary>
        /// Slice (iii) orbital-interaction join: resolve the live per-instance ProtoVessel pid for a
        /// specific overlap (recIdx, cycle), or 0 when no instance exists for that cycle (the
        /// pure-suborbital case - <see cref="overlapInstanceVessels"/> never holds a state-vector cycle
        /// - or a cycle not yet materialized this frame). Lets the marker path decide per-cycle whether
        /// the polyline marker is redundant with a live proto icon (skip) or is the sole indicator
        /// (draw).
        /// </summary>
        internal static uint TryGetOverlapInstancePidForCycle(int recIdx, long cycle)
        {
            if (overlapInstanceVessels.TryGetValue((recIdx, cycle), out Vessel inst) && inst != null)
                return inst.persistentId;
            return 0u;
        }

        /// <summary>
        /// Read-only reverse of <see cref="TryGetOverlapInstancePidForCycle"/>: the overlap
        /// (recIdx, cycle) a live per-instance ProtoVessel pid belongs to, or false when the pid
        /// is not an overlap instance (a primary ghost). A linear walk of the instance map,
        /// which holds a handful of entries; read by the automation-only map-geometry dump for
        /// a ghost's loop cycle, never on a per-frame path.
        /// </summary>
        internal static bool TryGetOverlapCycleForPid(uint pid, out int recIdx, out long cycle)
        {
            recIdx = -1;
            cycle = 0;
            if (pid == 0u)
                return false;
            foreach (var kv in overlapInstanceVessels)
            {
                if (kv.Value != null && kv.Value.persistentId == pid)
                {
                    recIdx = kv.Key.recIdx;
                    cycle = kv.Key.cycle;
                    return true;
                }
            }
            return false;
        }
    }
}
