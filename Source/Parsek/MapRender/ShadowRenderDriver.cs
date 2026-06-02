using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek.Display;

namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 4 (design §6.7): runs the new render pipeline in DECISION-ONLY SHADOW. Per map-ghost
    /// instance it builds the chain, samples it at the live UT, decides the intent, and records that
    /// intent via <see cref="GhostRenderReconciler.NoteIntent"/> — and writes NOTHING to the stock
    /// surfaces (the still-live old patches own those). The end-of-frame <see cref="MapRenderProbe"/>
    /// then reconciles the recorded intent against the OLD path's rendered truth, surfacing
    /// decision-vs-old-truth / gap-vs-retire divergence: the signal that tells us whether the new
    /// single-owner Director would have rendered the same thing as today's scattered coordination.
    ///
    /// <para>Wholly gated by the caller on <see cref="MapRenderTrace.IsEnabled"/> (the off-by-default
    /// <c>mapRenderTracing</c> setting), so normal play pays nothing. Consumes the loop units from the
    /// scene adapter (the <see cref="MissionLoopUnitBuilder.Build"/> output), not the engine
    /// passthrough.</para>
    ///
    /// <para><b>Shadow scope (MVP): faithful, single-instance members only — decided PER MEMBER.</b>
    /// Re-aim is per member, not per mission (design §4): only the heliocentric (Sun-relative) member
    /// is re-synthesized, so ONLY it is skipped; the Kerbin-departure and destination-arrival members
    /// of the SAME re-aimed mission are faithful and ARE shadowed (they render their recorded surface
    /// tracks). Overlap members are still skipped (their per-instance phasing is not modelled by a
    /// single pid→recording resolve). Skips carry a logged reason and land fully with the re-aim /
    /// overlap wiring in a later phase. Both the faithful same-body case (Mun/Minmus) AND the faithful
    /// departure/arrival members of an interplanetary mission are validated here.</para>
    /// </summary>
    internal static class ShadowRenderDriver
    {
        /// <summary>Why a ghost was shadowed or skipped this frame.</summary>
        internal enum ShadowScope
        {
            /// <summary>Faithful single-instance (no unit, or a non-overlap non-re-aim unit) → shadow it.</summary>
            Faithful = 0,
            /// <summary>Re-aim member: raw recording lacks the synthesized transfer → skip (later phase).</summary>
            SkipReaim = 1,
            /// <summary>Overlap member: per-instance phasing not modelled here → skip (later phase).</summary>
            SkipOverlap = 2,
        }

        // Per-pid prior intent (for the Director's gap-hold) + per-pid cached chain. Pruned each frame
        // to whatever pids the scene still reports, so a scene switch / ghost retire drops stale state.
        private static readonly Dictionary<uint, GhostRenderIntent> priorIntentByPid =
            new Dictionary<uint, GhostRenderIntent>();
        private static readonly Dictionary<uint, CachedChain> chainByPid =
            new Dictionary<uint, CachedChain>();
        private static readonly List<uint> stalePidScratch = new List<uint>();

        private struct CachedChain
        {
            public string Signature;
            public GhostRenderChain Chain;
        }

        internal static void Reset()
        {
            priorIntentByPid.Clear();
            chainByPid.Clear();
        }

        /// <summary>
        /// PURE scope classifier (design §4). Re-aim is decided PER MEMBER, not per mission: only the
        /// heliocentric (Sun-relative) member of a re-aimed mission is re-synthesized, so it is skipped
        /// (<paramref name="memberIsHeliocentric"/>); the Kerbin-departure and destination-arrival
        /// members of that same mission are FAITHFUL and DO render, so they are shadowed. Overlap (the
        /// unit's true launch cadence shorter than its span → several instances live at once) is still
        /// skipped — its per-instance phasing is a later phase. A member with no owning unit is a
        /// faithful non-loop recording → shadow. <paramref name="spanSeconds"/> &lt;= 0 is treated as
        /// non-overlap (degenerate span).
        /// </summary>
        internal static ShadowScope ClassifyScope(
            bool memberIsHeliocentric, bool hasUnit, double overlapCadenceSeconds, double spanSeconds)
        {
            if (memberIsHeliocentric)
                return ShadowScope.SkipReaim;
            if (hasUnit && spanSeconds > 0.0 && overlapCadenceSeconds > 0.0
                && overlapCadenceSeconds < spanSeconds - 1.0)
                return ShadowScope.SkipOverlap;
            return ShadowScope.Faithful;
        }

        /// <summary>
        /// PURE pipeline composition for one ghost: assemble → sample → decide. Built against the
        /// injected <paramref name="surface"/> (null in tests). This is the whole new render decision
        /// for a faithful ghost, free of any scene/world coupling, so it is unit-testable end to end.
        /// </summary>
        internal static GhostRenderIntent DecideForGhost(
            IPlaybackTrajectory traj, int committedIndex, double windowStartUT, double windowEndUT,
            double currentUT, GhostPlaybackLogic.LoopUnitSet units,
            GhostTrajectoryPolylineRenderer.BodySurfaceProvider surface, GhostRenderIntent prior)
        {
            GhostRenderChain chain = ChainAssembler.Build(
                traj, committedIndex, instanceKey: 0, windowStartUT, windowEndUT,
                faithfulFallback: false, surface: surface);
            GhostSample sample = ChainSampler.Sample(chain, currentUT, units);
            return GhostRenderDirector.Decide(sample, prior, traj?.VesselName);
        }

        /// <summary>
        /// Run one shadow frame over every ghost the scene reports. Decision-only: it never touches a
        /// stock surface. Caller MUST gate on <see cref="MapRenderTrace.IsEnabled"/> so this is free in
        /// normal play.
        /// </summary>
        internal static void RunFrame(IGhostMapScene scene)
        {
            if (scene == null || !scene.IsActive)
                return;

            IReadOnlyCollection<uint> pids = scene.GhostPids;
            GhostPlaybackLogic.LoopUnitSet units = scene.LoopUnits;
            double currentUT = scene.CurrentUT;
            var surface = scene.BodySurface;

            int shadowed = 0, skipReaim = 0, skipOverlap = 0, unresolved = 0;
            if (pids != null)
            {
                foreach (uint pid in pids)
                {
                    if (!scene.TryResolveGhost(pid, out IPlaybackTrajectory traj, out int idx) || traj == null)
                    {
                        unresolved++;
                        continue;
                    }

                    ShadowScope scope = ClassifyScopeForMember(units, idx, traj, scene,
                        out double wStart, out double wEnd, traj.StartUT, traj.EndUT);
                    if (scope == ShadowScope.SkipReaim) { skipReaim++; continue; }
                    if (scope == ShadowScope.SkipOverlap) { skipOverlap++; continue; }

                    GhostRenderChain chain = GetOrBuildChain(pid, traj, idx, wStart, wEnd, surface);
                    GhostSample sample = ChainSampler.Sample(chain, currentUT, units);
                    priorIntentByPid.TryGetValue(pid, out GhostRenderIntent prior);
                    GhostRenderIntent intent = GhostRenderDirector.Decide(sample, prior, traj.VesselName);
                    priorIntentByPid[pid] = intent;

                    GhostRenderReconciler.NoteIntent(pid, intent);
                    EmitLocateIntent(pid, currentUT, sample, intent);
                    shadowed++;
                }
            }

            PruneStaleState(pids);

            ParsekLog.VerboseRateLimited("MapRender", "shadow-frame-summary",
                string.Format(CultureInfo.InvariantCulture,
                    "shadow frame ghosts={0} shadowed={1} skipReaim={2} skipOverlap={3} unresolved={4}",
                    pids?.Count ?? 0, shadowed, skipReaim, skipOverlap, unresolved),
                5.0);
        }

        // Resolve a member's window (trimmed if it belongs to a unit) and classify its shadow scope.
        private static ShadowScope ClassifyScopeForMember(
            GhostPlaybackLogic.LoopUnitSet units, int idx, IPlaybackTrajectory traj, IGhostMapScene scene,
            out double windowStartUT, out double windowEndUT,
            double fallbackStartUT, double fallbackEndUT)
        {
            windowStartUT = fallbackStartUT;
            windowEndUT = fallbackEndUT;
            bool heliocentric = TrajectoryHasHeliocentricLeg(traj, scene);
            if (units != null && units.TryGetUnitForMember(idx, out GhostPlaybackLogic.LoopUnit unit))
            {
                windowStartUT = unit.MemberStartUT(idx, fallbackStartUT);
                windowEndUT = unit.MemberEndUT(idx, fallbackEndUT);
                return ClassifyScope(heliocentric, true, unit.OverlapCadenceSeconds,
                    unit.SpanEndUT - unit.SpanStartUT);
            }
            return ClassifyScope(heliocentric, false, 0.0, 0.0);
        }

        // A member carries a heliocentric (re-aimed) leg iff its recorded orbit is around a star (the
        // Sun). Only that member is re-synthesized; the Kerbin-departure / destination-arrival members
        // of the same mission are faithful (design §4). Uses the scene's live-body star check so the
        // pure ClassifyScope stays Unity-free.
        private static bool TrajectoryHasHeliocentricLeg(IPlaybackTrajectory traj, IGhostMapScene scene)
        {
            var segs = traj?.OrbitSegments;
            if (segs == null || scene == null)
                return false;
            for (int i = 0; i < segs.Count; i++)
                if (scene.IsStarBody(segs[i].bodyName))
                    return true;
            return false;
        }

        private static GhostRenderChain GetOrBuildChain(
            uint pid, IPlaybackTrajectory traj, int idx, double wStart, double wEnd,
            GhostTrajectoryPolylineRenderer.BodySurfaceProvider surface)
        {
            string sig = string.Format(CultureInfo.InvariantCulture, "{0}|{1:R}|{2:R}|{3}|{4}",
                traj.RecordingId ?? "?", wStart, wEnd,
                traj.OrbitSegments?.Count ?? 0, traj.Points?.Count ?? 0);
            if (chainByPid.TryGetValue(pid, out CachedChain cached) && cached.Signature == sig)
                return cached.Chain;

            GhostRenderChain chain = ChainAssembler.Build(
                traj, idx, instanceKey: 0, wStart, wEnd, faithfulFallback: false, surface: surface);
            chainByPid[pid] = new CachedChain { Signature = sig, Chain = chain };
            return chain;
        }

        // §13 locate + intent diagnostic (design §13), rate-limited per pid so a steady chain does not
        // spam. Emits the located segment, coverage tri-state, active treatment, and drive UT.
        private static void EmitLocateIntent(
            uint pid, double currentUT, GhostSample sample, GhostRenderIntent intent)
        {
            ParsekLog.VerboseRateLimited("MapRender", "shadow-locate-" + pid.ToString(CultureInfo.InvariantCulture),
                string.Format(CultureInfo.InvariantCulture,
                    "shadow pid={0} coverage={1} segIdx={2} treatment={3} visible={4} driveUT={5:F3} body={6}",
                    pid, sample.Coverage, sample.SegmentIndex, intent.Treatment, intent.Visible,
                    intent.DriveUT, intent.FrameBodyName ?? "?"),
                2.0);
        }

        private static void PruneStaleState(IReadOnlyCollection<uint> livePids)
        {
            stalePidScratch.Clear();
            foreach (var kv in priorIntentByPid)
                if (livePids == null || !livePids.Contains(kv.Key))
                    stalePidScratch.Add(kv.Key);
            for (int i = 0; i < stalePidScratch.Count; i++)
            {
                priorIntentByPid.Remove(stalePidScratch[i]);
                chainByPid.Remove(stalePidScratch[i]);
            }
        }
    }
}
