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
    /// <para><b>Shadow scope: faithful + overlap single-instance members — decided PER MEMBER.</b>
    /// Re-aim is per member, not per mission (design §4): only the heliocentric (Sun-relative) member
    /// is re-synthesized, so ONLY it is skipped; the Kerbin-departure and destination-arrival members
    /// of the SAME re-aimed mission are faithful and ARE shadowed (they render their recorded surface
    /// tracks). OVERLAP members (a looped mission whose launch cadence is shorter than its span, so it
    /// relaunches and several staggered instances run at once) are NOW shadowed too (integration #2):
    /// the MAP has no per-instance model — an overlapping mission renders as exactly ONE ghost at the
    /// SELECTED cycle's span-clock head-UT, chosen by the SAME pure span clock
    /// (<see cref="GhostPlaybackLogic.ResolveTrackingStationSampleUT"/>) the legacy single head uses
    /// (the unit's <c>CadenceSeconds</c>, raised to at least the span, gives a single span instance).
    /// The N simultaneous instances are flight-MESH-only (<c>GhostPlaybackEngine.overlapGhosts</c>),
    /// out of scope here. Re-aim skips still carry a logged reason. Both the faithful same-body case
    /// (Mun/Minmus) AND the faithful departure/arrival members of an interplanetary mission are
    /// validated here.</para>
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
            /// <summary>Overlap member classification (RETAINED for <see cref="ClassifyScope"/> + its tests).
            /// PRODUCTION NO LONGER ACTS ON THIS: integration #2 lifted the RunFrame overlap skip, so an
            /// overlap member now flows through the normal assemble→sample→decide path and renders one ghost
            /// at the span-clock head-UT (the map has no per-instance model). The enum value stays so the pure
            /// classifier and its unit tests keep documenting the overlap predicate.</summary>
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
            // The typed PhaseChain successor (migration plan §5 / Phase 3). Built from the SAME inputs as
            // Chain (PhaseFactory wraps the assembler's geometry), cached under the SAME signature, and
            // consumed by the sampler/director ONLY when MapRenderFlags.MapRenderPhaseSpineDrive is ON. It
            // is null when neither the flag is on NOR tracing is on (no consumer needs it). The Phase-2
            // shadow already builds a factory chain when tracing is on for byte-parity; when the spine flag
            // is on it is also built (regardless of tracing) so the spine has something to drive.
            public PhaseChain PhaseChain;
            // True when this cached chain was assembled from RE-AIMED OrbitSegments (the override differed
            // from the recorded list by reference). Stored WITH the chain so the per-frame skip decision
            // (ShouldSkipReaimSegment) uses the SAME resolve the chain was built from - never a second
            // UT->window mapping that could disagree with the cached geometry.
            public bool HasReaimedSegments;
        }

        // The Director's current StockConic seed per pid (the inertial OrbitSegment + frame body),
        // frame-stamped, for the Phase-8a gated drive: GhostOrbitIconDrivePatch re-asserts THIS conic so
        // the icon rides the same elements the line is drawn from. Populated each shadow frame for a
        // visible StockConic ghost; read same-frame by the patch (the shadow runs in Update, the patch
        // in LateUpdate). Independent of the reconciler store (works even with tracing off, as long as
        // the shadow runs).
        private struct StockConicSeed
        {
            public int Frame;
            public OrbitSegment Seg;
            public string BodyName;
        }

        private static readonly Dictionary<uint, StockConicSeed> seedByPid =
            new Dictionary<uint, StockConicSeed>();

        // Per-pid frame stamp: the frame the Director's active segment for this ghost was a TracedPath
        // (a non-orbital leg - ascent / burn / descent - the autonomous polyline owns). Under the gate
        // the icon-drive + line patches read this and HARD-SUPPRESS the stock proto icon + line, so the
        // legacy gap-glide can't stock-propagate a per-frame synthesized orbit and teleport the icon
        // across it (the s15 escape-burn teleport). Mirrors seedByPid: written each shadow frame, read
        // with the same +/-SeedFreshnessFrames tolerance, keyed on the intent (not a FixedUpdate stamp).
        // KEPT (migration plan Phase 5): this is the legacy side-channel stamp the flag-OFF owned-draw
        // routing + every proto/marker consumer reads via IsDirectorTracedPathActive. Phase 4a adds an
        // intent-sourced sibling (tracedPathIntentByPid) for the flag-ON path WITHOUT touching this.
        private static readonly Dictionary<uint, int> tracedPathByPid = new Dictionary<uint, int>();

        // Phase 4a (migration plan §6.6a — re-home the TracedPath polyline draw to the intent): the
        // INTENT-SOURCED sibling of tracedPathByPid, written ONLY when the typed-spine flag is ON
        // (PhaseSpineDriveActive). Same frame-stamp + same +/-SeedFreshnessFrames freshness contract, so
        // the flag-ON owned-draw routing reads the SAME shape the legacy side-channel offers - but the
        // stamp is sourced from the spine's GhostRenderIntent (intent.Treatment == TracedPath), the design
        // §8 "scene.Apply(intent)" sourcing, not the autonomous walk. By construction it is stamped from
        // the SAME intent, in the SAME RunFrame pass, on the SAME frame as tracedPathByPid, so the two are
        // byte-identical when the flag is on - which is exactly why the flag-ON owned-draw routing can read
        // it WITHOUT desyncing the proto/marker consumers that still read tracedPathByPid (no double-draw,
        // no gap). Flag OFF: never written, so the flag-OFF routing falls through to tracedPathByPid and is
        // byte-identical to today. Additive; nothing here is deleted (the Phase-5 cutover removes both).
        private static readonly Dictionary<uint, int> tracedPathIntentByPid = new Dictionary<uint, int>();

        internal static void Reset()
        {
            priorIntentByPid.Clear();
            chainByPid.Clear();
            seedByPid.Clear();
            tracedPathByPid.Clear();
            tracedPathIntentByPid.Clear();
        }

        /// <summary>The shadow always runs: the Director pipeline is unconditional (8e S4 dropped the
        /// director-drive gate), so the StockConic seed is always populated for the patch (and the
        /// reconciler reads it when render tracing is on). Kept as a property for call-site stability.</summary>
        internal static bool Enabled => true;

        /// <summary>
        /// In-game test seam (migration plan §5 / Phase 3): force the typed-spine drive ON regardless of
        /// the default-OFF compile-time <see cref="MapRenderFlags.MapRenderPhaseSpineDrive"/> const, so a
        /// flag-ON in-game test can exercise the PhaseChain spine without a rebuild. ORed with the const in
        /// <see cref="PhaseSpineDriveActive"/>. Default false (normal play reads only the const). Tests MUST
        /// restore it in a finally; a leaked true would silently flip every later ghost onto the new spine.
        ///
        /// <para><b>Callers MUST <see cref="Reset"/> after flipping this</b> (or run with tracing on so the
        /// PhaseChain is always built). <see cref="BuildChainSignature"/> intentionally OMITS the spine flag
        /// from the cache key (the flag does not change the geometry, only which spine consumes it), so a
        /// chain cached while the flag was off can carry a null <see cref="CachedChain.PhaseChain"/>; without
        /// a <see cref="Reset"/> that stale cache entry would persist after the flip and the flag-ON branch
        /// would silently fall back to the assembler chain (phaseChain == null). The in-game test seam
        /// (<see cref="PhaseSpineSwapInGameTest"/>) calls <see cref="Reset"/> on every flip for this reason.</para>
        /// </summary>
        internal static bool ForceSpineDriveForTesting = false;

        /// <summary>
        /// The single source of truth for "drive the sampler/director off the typed PhaseChain this frame":
        /// the default-OFF compile-time flag OR the in-game test seam. With BOTH off (normal play) the C#
        /// const-fold elides the new spine branch entirely (the seam is a runtime read, so the JIT keeps the
        /// branch, but the false seam means it is never taken), so flag-OFF behavior is byte-identical.
        /// </summary>
        internal static bool PhaseSpineDriveActive =>
            MapRenderFlags.MapRenderPhaseSpineDrive || ForceSpineDriveForTesting;

        /// <summary>
        /// Max frame gap between the shadow recording a StockConic seed and the icon-drive patch reading
        /// it. The shadow (ParsekFlight/TS LateUpdate) and the patch (OrbitDriver LateUpdate) both run in
        /// LateUpdate with no defined relative order, so requiring the exact same frame can miss when the
        /// patch runs first (it would see the previous frame's seed). The seed is the segment's inertial
        /// elements, which are constant across a segment, so a 1-2 frame-old seed is identical in practice
        /// (the patch supplies its own driveUT; only the elements come from the seed).
        /// </summary>
        internal const int SeedFreshnessFrames = 2;

        /// <summary>
        /// True when the Phase-8a director-drive is ACTIVE for <paramref name="pid"/> this frame: the
        /// Director recorded a fresh StockConic seed (the gate was dropped in 8e S4; the predicate now
        /// gates purely on seed/body freshness).
        /// This is the SINGLE source of truth shared by all three consumers (the icon-drive Prefix that
        /// bakes the epoch, the arc-clip Prefix that switches to live bounds, and the probe that measures
        /// against the live clock), so they never disagree on a frame. Keyed on the SEED (refreshed by the
        /// shadow in Update, every render frame) rather than a per-frame stamp written in FixedUpdate, so
        /// it stays correct on render frames where no FixedUpdate ran (the orbit keeps its baked epoch from
        /// the prior FixedUpdate, and this still reports active - no stale-stamp metric artifact / arc
        /// flicker).
        /// </summary>
        internal static bool IsDirectorDriveActive(uint pid, int currentFrame)
        {
            // Gate on a fresh seed AND a resolvable seed body - the SAME condition the icon-drive Prefix
            // uses to decide it actually bakes (GhostOrbitIconDrivePatch resolves seedBody via
            // FlightGlobals.GetBodyByName(dirBody) and only goes director when non-null). Keeping the two
            // sites on one predicate means the arc-clip (which switches to LIVE bounds when this is true)
            // can never disagree with the icon-drive (which would otherwise fall to the legacy effUT path
            // on an unresolvable body) for a one-frame icon/line split. A real recorded body name always
            // resolves, so this is a no-op in normal play; it only closes the degenerate null/unknown-body
            // gap deterministically.
            return TryGetFreshStockConicSeed(pid, currentFrame, out _, out string seedBody)
                && FlightGlobals.GetBodyByName(seedBody) != null;
        }

        /// <summary>
        /// True when the Director's active segment for <paramref name="pid"/> was a
        /// TracedPath (non-orbital leg) within <see cref="SeedFreshnessFrames"/> of
        /// <paramref name="currentFrame"/> (the gate was dropped in 8e S4; the predicate now gates purely
        /// on the TracedPath stamp freshness). The icon-drive + line patches read this to HARD-SUPPRESS the
        /// stock proto icon + line on those frames (the autonomous polyline owns the leg), so the legacy
        /// gap-glide can't stock-propagate a synthesized orbit and teleport the icon. Same shared-signal
        /// shape as <see cref="IsDirectorDriveActive"/> - keyed on the shadow's per-segment intent, not a
        /// FixedUpdate stamp, so it stays correct on no-FixedUpdate render frames.
        /// </summary>
        internal static bool IsDirectorTracedPathActive(uint pid, int currentFrame)
        {
            return tracedPathByPid.TryGetValue(pid, out int f)
                && System.Math.Abs(currentFrame - f) <= SeedFreshnessFrames;
        }

        /// <summary>
        /// Phase 4a (migration plan §6.6a): the INTENT-SOURCED TracedPath-active signal - true when the
        /// spine's <see cref="GhostRenderIntent"/> for <paramref name="pid"/> was a visible TracedPath
        /// within <see cref="SeedFreshnessFrames"/> of <paramref name="currentFrame"/>, as stamped from
        /// the intent into <see cref="tracedPathIntentByPid"/>. Written ONLY when
        /// <see cref="PhaseSpineDriveActive"/> is on, so this returns false in flag-OFF play (no stamp).
        /// Same freshness contract as <see cref="IsDirectorTracedPathActive"/>; the flag-ON owned-draw
        /// routing reads THIS (the intent) instead of the legacy side-channel, while the proto/marker
        /// consumers keep reading <see cref="IsDirectorTracedPathActive"/> - the two are byte-identical
        /// when the flag is on (same intent, same pass, same frame), so the consumers never disagree.
        /// </summary>
        internal static bool IsDirectorTracedPathActiveFromIntent(uint pid, int currentFrame)
        {
            return tracedPathIntentByPid.TryGetValue(pid, out int f)
                && System.Math.Abs(currentFrame - f) <= SeedFreshnessFrames;
        }

        /// <summary>
        /// Phase 4a (migration plan §6.6a): the FLAG-AWARE "does the OWNED TracedPath treatment draw this
        /// ghost's leg this frame (and the autonomous Driver-direct draw stand down for it)?" signal the
        /// polyline Driver routes on. Flag ON (<see cref="PhaseSpineDriveActive"/>): source the decision
        /// from the spine's intent (<see cref="IsDirectorTracedPathActiveFromIntent"/>) - the design §8
        /// "scene.Apply(intent)" sourcing, re-homed off the autonomous walk's side-channel. Flag OFF
        /// (default): fall through to the legacy <see cref="IsDirectorTracedPathActive"/> side-channel -
        /// BYTE-IDENTICAL to today (the prior-rewrite routing is untouched). The two underlying stamps are
        /// written from the SAME intent in the SAME pass, so the flag flip never changes WHICH frames own
        /// the leg; it only re-points the SOURCE of the decision, keeping flag-ON consistent with the
        /// proto/marker consumers (no double-draw, no gap) and flag-OFF unchanged.
        /// </summary>
        internal static bool IsTracedPathOwnedThisFrame(uint pid, int currentFrame)
        {
            return PhaseSpineDriveActive
                ? IsDirectorTracedPathActiveFromIntent(pid, currentFrame)
                : IsDirectorTracedPathActive(pid, currentFrame);
        }

        /// <summary>
        /// Test-only seam (Phase 4a): stamps the two per-pid TracedPath frame maps the Unity-coupled
        /// <see cref="RunFrame"/> populates, so the freshness predicates + the flag-aware selector
        /// (<see cref="IsTracedPathOwnedThisFrame"/>) can be exercised from xUnit without a live KSP.
        /// <paramref name="legacyFrame"/> stamps the legacy side-channel (<c>tracedPathByPid</c>, the
        /// flag-OFF + proto/marker source); <paramref name="intentFrame"/> stamps the intent-sourced
        /// sibling (<c>tracedPathIntentByPid</c>, the flag-ON source). A negative frame removes the stamp.
        /// Mirrors the production contract (RunFrame stamps BOTH on the flag, only the legacy off it), but
        /// the seam lets a test stamp them independently to prove the selector reads the right one.
        /// Cleared by <see cref="Reset"/>.
        /// </summary>
        internal static void SetTracedPathStampsForTesting(uint pid, int legacyFrame, int intentFrame)
        {
            if (legacyFrame < 0) tracedPathByPid.Remove(pid);
            else tracedPathByPid[pid] = legacyFrame;
            if (intentFrame < 0) tracedPathIntentByPid.Remove(pid);
            else tracedPathIntentByPid[pid] = intentFrame;
        }

        /// <summary>
        /// True when the Director is TRACKING <paramref name="pid"/> this frame -
        /// i.e. it has a fresh StockConic seed OR a fresh TracedPath stamp (8e S4: the gate was dropped).
        /// Used to plug the no-bounds
        /// leak: when the legacy gap-glide clears a ghost's segment bounds at a loiter->burn transition,
        /// the icon-drive early-returns to stock and the line Postfix falls into the `terminal-visible`
        /// (full-ellipse) branch, showing the proto icon on the per-frame synthesized burn orbit BEFORE
        /// the chain switches to TracedPath. If the Director is tracking the ghost AT ALL, stock must not
        /// be allowed to show that phantom - suppress until the Director re-establishes a StockConic drive
        /// (the hyperbolic) or a TracedPath suppress. NOT a substitute for IsDirectorDriveActive: a ghost
        /// the Director actively drives WITH bounds still renders via the StockConic path; this only gates
        /// the legacy no-bounds fallback.
        /// </summary>
        internal static bool IsDirectorTracking(uint pid, int currentFrame)
        {
            return IsDirectorDriveActive(pid, currentFrame)
                || IsDirectorTracedPathActive(pid, currentFrame);
        }

        /// <summary>
        /// True when the Director recorded a StockConic seed for <paramref name="pid"/> within
        /// <see cref="SeedFreshnessFrames"/> of <paramref name="currentFrame"/>. Returns the inertial
        /// <see cref="OrbitSegment"/> + frame body the Phase-8a drive re-asserts. Stale seeds (no shadow
        /// run for this pid recently) are dropped, so the patch falls back to the legacy drive.
        /// </summary>
        internal static bool TryGetFreshStockConicSeed(
            uint pid, int currentFrame, out OrbitSegment seg, out string bodyName)
        {
            if (seedByPid.TryGetValue(pid, out StockConicSeed s)
                && System.Math.Abs(currentFrame - s.Frame) <= SeedFreshnessFrames)
            {
                seg = s.Seg;
                bodyName = s.BodyName;
                return true;
            }
            seg = default(OrbitSegment);
            bodyName = null;
            return false;
        }

        /// <summary>
        /// PURE scope classifier (design §4). NOTE: production now decides re-aim PER ACTIVE SEGMENT (see
        /// <see cref="ShouldSkipReaimSegment"/>), not per member - a single-recording interplanetary
        /// flight has both faithful (Kerbin escape / destination arrival) and re-aimed (heliocentric)
        /// legs, so only the currently-flown heliocentric leg is skipped. This predicate retains the
        /// <paramref name="memberIsHeliocentric"/> input for the per-member classification it still
        /// models (and its tests), but the production call site (<see cref="ClassifyOverlapForMember"/>)
        /// passes false and applies re-aim per segment. Overlap (the unit's true launch cadence shorter
        /// than its span → several instances live at once) is still skipped per member - its per-instance
        /// phasing is a later phase. A member with no owning unit is a faithful non-loop recording →
        /// shadow. <paramref name="spanSeconds"/> &lt;= 0 is treated as non-overlap (degenerate span).
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

            int shadowed = 0, skipReaim = 0, overlapShadowed = 0, unresolved = 0;
            if (pids != null)
            {
                foreach (uint pid in pids)
                {
                    if (!scene.TryResolveGhost(pid, out IPlaybackTrajectory traj, out int idx) || traj == null)
                    {
                        unresolved++;
                        continue;
                    }

                    // Resolve this member's trimmed render window (interval-level start/end trim if it
                    // belongs to a unit). Integration #2: overlap members are NO LONGER skipped here. On
                    // the map there is no per-instance model — an overlapping mission renders as exactly
                    // ONE ghost at the SELECTED cycle's span-clock head-UT, which ChainSampler.Sample
                    // resolves through the SAME pure clock (ResolveTrackingStationSampleUT, driven by the
                    // unit's span-raised CadenceSeconds) the legacy single head uses. So an overlap member
                    // flows through the normal assemble→sample→decide path like any faithful single
                    // instance. Re-aim is still decided PER ACTIVE SEGMENT after Decide (below).
                    ShadowScope scope = ClassifyOverlapForMember(
                        units, idx, out double wStart, out double wEnd, traj.StartUT, traj.EndUT);
                    if (scope == ShadowScope.SkipOverlap)
                        overlapShadowed++; // counted for diagnostics; it now PROCEEDS, not skipped

                    GhostRenderChain chain = GetOrBuildChain(
                        pid, traj, idx, wStart, wEnd, currentUT, units, surface,
                        out bool chainHasReaimedSegments, out PhaseChain phaseChain);

                    // THE SPINE SWAP (migration plan §5 / Phase 3). Flag OFF (default): sample the legacy
                    // ChainAssembler-built GhostRenderChain — byte-identical to pre-Phase-3 behavior. Flag
                    // ON: sample the typed PhaseChain instead. Both run the SAME span clock + coverage
                    // classify + 3-case Decide, and the factory geometry byte-matches the assembler
                    // (Phase-2 parity), so the emitted intent + side-channel stamps + draw are identical.
                    // The director's prior-intent gap-hold is shared (one priorIntentByPid map), so the swap
                    // does not change the hold contract.
                    priorIntentByPid.TryGetValue(pid, out GhostRenderIntent prior);
                    GhostSample sample;
                    GhostRenderIntent intent;
                    if (PhaseSpineDriveActive && phaseChain != null)
                    {
                        sample = ChainSampler.Sample(phaseChain, currentUT, units);
                        intent = GhostRenderDirector.Decide(sample, prior, traj.VesselName);
                    }
                    else
                    {
                        sample = ChainSampler.Sample(chain, currentUT, units);
                        intent = GhostRenderDirector.Decide(sample, prior, traj.VesselName);
                    }
                    priorIntentByPid[pid] = intent;

                    // Per-ACTIVE-SEGMENT re-aim skip (design §4, refined from per-member): only the
                    // heliocentric (Sun-relative) LEG of a re-aim owner is replaced. Now COVERAGE-AWARE:
                    //  - re-aimed window + ON the heliocentric leg (chainHasReaimedSegments && sampleInSegment)
                    //    -> DO NOT skip: render the re-aimed conic (THE FIX - kills icon-off-orbit).
                    //  - re-aimed window + TRIM GAP / held interior gap (sampleInSegment=false) -> SKIP (hide),
                    //    matching the legacy hide-in-gap contract; without this the Director would drive a held
                    //    stale Sun conic across the gap (the review's bug case).
                    //  - declined window (chainHasReaimedSegments=false) on a re-aim owner's wrong-aimed Sun leg
                    //    -> SKIP.
                    //  - faithful NON-owner Sun leg (a real non-looped interplanetary recording) -> DO NOT skip.
                    // A single-recording interplanetary flight's FAITHFUL Kerbin-escape / destination-arrival
                    // legs (frame body NOT a star) are never matched here and render with the director-drive.
                    bool memberIsReaimOwner =
                        units != null && units.TryGetUnitForMember(idx, out GhostPlaybackLogic.LoopUnit ownerUnit)
                        && ownerUnit.IsReaim;
                    bool sampleInSegment = sample.Coverage == Coverage.InSegment;
                    if (ShouldSkipReaimSegment(
                            intent.Visible, scene.IsStarBody(intent.FrameBodyName),
                            memberIsReaimOwner, chainHasReaimedSegments, sampleInSegment))
                    {
                        skipReaim++;
                        continue;
                    }

                    // Record the Director's StockConic seed this frame so the Phase-8a gated patch can
                    // re-assert this inertial conic (one-source icon+line). Frame-stamped for same-frame
                    // freshness at the patch site.
                    if (intent.Visible && intent.Treatment == Treatment.StockConic && intent.Payload.HasConic)
                        seedByPid[pid] = new StockConicSeed
                        {
                            Frame = UnityFrame(),
                            Seg = intent.Payload.Conic,
                            BodyName = intent.FrameBodyName
                        };
                    // A visible TracedPath leg (ascent / burn / descent): stamp it so the icon-drive +
                    // line patches suppress the stock proto icon + line under the gate (the polyline owns
                    // it). Mutually exclusive with the StockConic seed above (one treatment per intent).
                    else if (intent.Visible && intent.Treatment == Treatment.TracedPath)
                    {
                        int tracedFrame = UnityFrame();
                        // KEPT (legacy side-channel): every proto/marker consumer + the flag-OFF owned-draw
                        // routing reads this via IsDirectorTracedPathActive. Stamped in BOTH flag states so
                        // flag-OFF is byte-identical to today.
                        tracedPathByPid[pid] = tracedFrame;
                        // Phase 4a (intent-sourced sibling): stamp the flag-ON owned-draw signal from the
                        // SAME intent in the SAME pass. Written ONLY under the flag so flag-OFF never grows
                        // a stamp here (its routing keeps falling through to tracedPathByPid). On the flag
                        // these two stamps are identical by construction, so the flag-ON owned routing and
                        // the proto/marker consumers (which read tracedPathByPid) can never disagree.
                        if (PhaseSpineDriveActive)
                            tracedPathIntentByPid[pid] = tracedFrame;
                    }

                    GhostRenderReconciler.NoteIntent(pid, intent);
                    EmitLocateIntent(pid, currentUT, sample, intent);
                    shadowed++;
                }
            }

            PruneStaleState(pids);

            ParsekLog.VerboseRateLimited("MapRender", "shadow-frame-summary",
                string.Format(CultureInfo.InvariantCulture,
                    "shadow frame ghosts={0} shadowed={1} skipReaim={2} overlapShadowed={3} unresolved={4}",
                    pids?.Count ?? 0, shadowed, skipReaim, overlapShadowed, unresolved),
                5.0);
        }

        // PURE, COVERAGE-AWARE: decide whether to skip the ACTIVE heliocentric (star-relative) segment of a
        // re-aim OWNER. The skip-lift (the critical fix from review): once the Director feeds the re-aimed
        // OrbitSegments, the owner's in-window heliocentric leg is aimed at the target's CURRENT position and
        // MUST render (no longer skipped to legacy) - that is what kills the icon-off-orbit bug. But the skip
        // is still required where the re-aimed geometry does NOT cover the frame, otherwise the Director would
        // drive a held stale Sun conic across the trim gap.
        //
        // skip = intentVisible && frameBodyIsStar && memberIsReaimOwner
        //        && !(chainHasReaimedSegments && sampleInSegment)
        //
        //  - re-aimed window + ON the heliocentric leg (chainHasReaimedSegments && sampleInSegment) => DRAW.
        //  - re-aimed window + TRIM GAP / held interior gap (sampleInSegment=false)                 => SKIP.
        //  - declined window (chainHasReaimedSegments=false) on the wrong-aimed recorded Sun leg     => SKIP.
        //  - faithful NON-owner Sun leg (real non-looped interplanetary recording, owner=false)      => DRAW.
        //  - hidden intent (intentVisible=false)                                                     => never skip.
        // The Kerbin-escape / destination-arrival legs (frame body NOT a star) never reach the skip test, so
        // they always render. <paramref name="frameBodyIsStar"/> is the live
        // <c>IGhostMapScene.IsStarBody(intent.FrameBodyName)</c> result (kept out so this stays Unity-free /
        // unit-testable); <paramref name="sampleInSegment"/> is <c>sample.Coverage == Coverage.InSegment</c>
        // for THIS frame (false for a held interior gap, which intent.Visible alone cannot distinguish).
        internal static bool ShouldSkipReaimSegment(
            bool intentVisible, bool frameBodyIsStar, bool memberIsReaimOwner,
            bool chainHasReaimedSegments, bool sampleInSegment)
        {
            return intentVisible && frameBodyIsStar && memberIsReaimOwner
                && !(chainHasReaimedSegments && sampleInSegment);
        }

        // Resolve a member's window (trimmed if it belongs to a unit) and classify its overlap scope
        // (re-aim is decided per active segment via ShouldSkipReaimSegment after Decide). Integration #2:
        // the RunFrame caller no longer DROPS a SkipOverlap member — the scope is used only for a
        // diagnostic counter; the member proceeds through the normal pipeline. The window resolution
        // (trimmed MemberStartUT/MemberEndUT) is the load-bearing output now.
        private static ShadowScope ClassifyOverlapForMember(
            GhostPlaybackLogic.LoopUnitSet units, int idx,
            out double windowStartUT, out double windowEndUT,
            double fallbackStartUT, double fallbackEndUT)
        {
            windowStartUT = fallbackStartUT;
            windowEndUT = fallbackEndUT;
            if (units != null && units.TryGetUnitForMember(idx, out GhostPlaybackLogic.LoopUnit unit))
            {
                windowStartUT = unit.MemberStartUT(idx, fallbackStartUT);
                windowEndUT = unit.MemberEndUT(idx, fallbackEndUT);
                // Heliocentric flag false: re-aim is handled per active segment, this classifies overlap.
                return ClassifyScope(false, true, unit.OverlapCadenceSeconds,
                    unit.SpanEndUT - unit.SpanStartUT);
            }
            return ClassifyScope(false, false, 0.0, 0.0);
        }

        private static GhostRenderChain GetOrBuildChain(
            uint pid, IPlaybackTrajectory traj, int idx, double wStart, double wEnd,
            double currentUT, GhostPlaybackLogic.LoopUnitSet units,
            GhostTrajectoryPolylineRenderer.BodySurfaceProvider surface,
            out bool chainHasReaimedSegments, out PhaseChain phaseChain)
        {
            // Resolve the EFFECTIVE OrbitSegments for THIS frame: a re-aim owner's in-window heliocentric
            // leg comes back re-aimed (aimed at the target's CURRENT position), every faithful member /
            // declined window comes back as the recorded list (same reference). The resolver lives in
            // GhostMapPresence (already [ERS-exempt]-allowlisted), so the new read stays inside the exempt
            // file. windowIndex is the synodic window (-1 for non-re-aim / declined / pre-first-window).
            List<OrbitSegment> recorded = traj.OrbitSegments;
            List<OrbitSegment> effective = GhostMapPresence.ResolveEffectiveMapOrbitSegments(
                idx, traj.RecordingId, recorded, currentUT, units, out long windowIndex);
            // Reference inequality == "this build used re-aimed segments" (the resolver returns the recorded
            // list unchanged for faithful members / declined windows). Stored with the cached entry so the
            // per-frame skip decision uses the SAME resolve - not a second Ut->window mapping.
            chainHasReaimedSegments = !ReferenceEquals(effective, recorded);

            string sig = BuildChainSignature(traj, wStart, wEnd, windowIndex);
            if (chainByPid.TryGetValue(pid, out CachedChain cached) && cached.Signature == sig)
            {
                chainHasReaimedSegments = cached.HasReaimedSegments;
                phaseChain = cached.PhaseChain;
                return cached.Chain;
            }

            // Feed the re-aimed override (CANDIDATE (a)) only when it differs from the recorded list; the
            // recorded Points still source the TracedPath body-relative legs inside ChainAssembler. When the
            // member is a re-aim owner, pass the plan's common ancestor so the in-window heliocentric segment
            // is marked Transfer/isGenerated (cosmetic).
            IReadOnlyList<OrbitSegment> overrideSegs = chainHasReaimedSegments ? effective : null;
            string reaimAncestor = chainHasReaimedSegments ? ResolveReaimAncestor(units, idx) : null;
            GhostRenderChain chain = ChainAssembler.Build(
                traj, idx, instanceKey: 0, wStart, wEnd, faithfulFallback: false, surface: surface,
                orbitSegmentsOverride: overrideSegs, reaimAncestorBody: reaimAncestor);

            // Build the typed PhaseChain (migration plan §5 / Phase 3) when ANY consumer needs it: the
            // spine flag is ON (the sampler/director drive off it), OR tracing is on (the Phase-2 shadow
            // byte-parity comparator asserts against it). When neither is on it stays null and no factory
            // work runs, so flag-OFF normal play pays nothing beyond the unchanged assembler build above.
            // Tolerant of any factory throw: a phase-chain build must never destabilize the live render
            // path, so an exception leaves phaseChain null (the spine falls back to the assembler chain in
            // RunFrame) and is logged + swallowed.
            phaseChain = null;
            if (PhaseSpineDriveActive || MapRenderTrace.IsEnabled)
            {
                try
                {
                    phaseChain = PhaseFactory.BuildPhaseChain(
                        traj, idx, instanceKey: 0, wStart, wEnd, faithfulFallback: false, surface: surface,
                        orbitSegmentsOverride: overrideSegs, reaimAncestorBody: reaimAncestor);

                    // Phase 2 SHADOW byte-parity assertion (migration plan §4): assert the factory's emitted
                    // geometry byte-matches the assembler's chain, emitting the factory-parity Tier-C anomaly
                    // on a mismatch. Gated on tracing (the anomaly sink is). Reuses the just-built phaseChain
                    // so a single factory build serves both the assertion and the spine.
                    if (MapRenderTrace.IsEnabled)
                        AssertFactoryParity(traj, wStart, phaseChain, chain);

                    // Tier-A structural event on a (re)build: the phase chain's count + kinds + provenance.
                    // Emitted only when tracing is on (EmitStructural early-returns otherwise) so it is a
                    // pure observability line, not a hot-path cost in flag-ON-tracing-off play.
                    EmitPhaseChainAssembled(pid, currentUT, phaseChain);
                }
                catch (System.Exception ex)
                {
                    phaseChain = null;
                    ParsekLog.Warn("MapRender", string.Format(CultureInfo.InvariantCulture,
                        "phase-chain build threw for rec={0}: {1}", traj?.RecordingId ?? "?", ex.Message));
                }
            }

            chainByPid[pid] = new CachedChain
            {
                Signature = sig,
                Chain = chain,
                PhaseChain = phaseChain,
                HasReaimedSegments = chainHasReaimedSegments,
            };
            return chain;
        }

        // Tier-A `phase-chain-assembled` structural event (migration plan §5): one Info line on a
        // (re)build carrying the phase count + kinds + provenance + seam summary, so a tracing-on run can
        // confirm the typed spine assembled the expected phases. EmitStructural early-returns when tracing
        // is off, so this is free in normal play. ProtoOrbitLine is the closest render surface (the spine
        // ultimately drives the proto icon/line for a conic phase).
        private static void EmitPhaseChainAssembled(uint pid, double currentUT, PhaseChain phaseChain)
        {
            if (!MapRenderTrace.IsEnabled || phaseChain == null)
                return;

            string details = string.Format(CultureInfo.InvariantCulture,
                "phases={0} window=[{1:F1},{2:F1}] faithfulFallback={3} kinds={4} prov={5} seams={6}",
                phaseChain.PhaseCount, phaseChain.WindowStartUt, phaseChain.WindowEndUt,
                phaseChain.IsFaithfulFallback,
                SummarizePhaseKinds(phaseChain), SummarizeProvenance(phaseChain),
                SummarizeSeams(phaseChain));

            MapRenderTrace.EmitStructural(
                "PhaseChainAssembled", MapRenderTrace.RenderSurface.ProtoOrbitLine,
                pid.ToString(CultureInfo.InvariantCulture), currentUT, 0.0,
                MapRenderTrace.SegmentChangeWindowSeconds, details, phaseChain.RecordingId);
        }

        private static string SummarizePhaseKinds(PhaseChain chain)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < chain.PhaseCount; i++)
            {
                if (i > 0) sb.Append('+');
                sb.Append(PhaseKindTokens.ToToken(chain.Phases[i].Kind));
            }
            return sb.Length == 0 ? "(none)" : sb.ToString();
        }

        private static string SummarizeProvenance(PhaseChain chain)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < chain.PhaseCount; i++)
            {
                if (i > 0) sb.Append('+');
                sb.Append(SegmentProvenanceTokens.ToToken(chain.Phases[i].Provenance));
            }
            return sb.Length == 0 ? "(none)" : sb.ToString();
        }

        private static string SummarizeSeams(PhaseChain chain)
        {
            int rigid = 0, flexibleSoi = 0, other = 0;
            for (int i = 0; i < chain.PhaseCount; i++)
            {
                PhaseSeam seam = chain.Phases[i].TrailingSeam;
                if (seam == null) continue;
                switch (seam.Kind)
                {
                    case PhaseSeamKind.Rigid: rigid++; break;
                    case PhaseSeamKind.FlexibleSoi: flexibleSoi++; break;
                    default: other++; break;
                }
            }
            return string.Format(CultureInfo.InvariantCulture,
                "rigid={0} flexibleSoi={1} other={2}", rigid, flexibleSoi, other);
        }

        // Phase 2 SHADOW byte-parity assertion (migration plan §4), now run against the ALREADY-BUILT
        // PhaseChain (Phase 3 builds it once for both the assertion and the spine). Asserts the factory's
        // emitted geometry byte-matches the assembler's chain; on a mismatch emits the factory-parity
        // Tier-C anomaly (rate-limited per recording, carrying the diverging field). The caller already
        // gated on MapRenderTrace.IsEnabled and wrapped the build + this call in a try/catch, so a compare
        // here never destabilizes the live (assembler-driven) render path.
        private static void AssertFactoryParity(
            IPlaybackTrajectory traj, double wStart, PhaseChain factoryChain, GhostRenderChain assemblerChain)
        {
            GeometryParityComparator.ParityResult result =
                GeometryParityComparator.Compare(factoryChain, assemblerChain);
            if (!result.IsMatch)
            {
                MapRenderTrace.EmitFactoryParity(
                    traj.RecordingId, wStart,
                    string.Format(CultureInfo.InvariantCulture,
                        "diverging={0} seg={1} countMismatch={2} {3}",
                        result.DivergingField, result.SegmentIndex, result.CountMismatch,
                        result.Detail ?? string.Empty));
            }
        }

        // The chain cache signature. The window token (|w{windowIndex}) is the load-bearing discriminator
        // under re-aim: the RECORDED OrbitSegments.Count does NOT change across synodic windows, so without
        // the window token a window advance (new re-aimed geometry, same recorded count) would NOT invalidate
        // the cache and the stale prior-window chain would keep rendering. windowIndex = -1 for every
        // non-re-aim member, so the token is constant there and the signature is identical to the pre-wiring
        // shape modulo the trailing "|w-1" (still unique per member, still rebuilds on the same triggers).
        internal static string BuildChainSignature(
            IPlaybackTrajectory traj, double wStart, double wEnd, long windowIndex)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}|{1:R}|{2:R}|{3}|{4}|w{5}",
                traj.RecordingId ?? "?", wStart, wEnd,
                traj.OrbitSegments?.Count ?? 0, traj.Points?.Count ?? 0, windowIndex);
        }

        // The re-aim plan's common-ancestor (star) body for marking the synthesized heliocentric segment
        // Transfer/isGenerated. Null when the member is not a resolvable re-aim unit.
        private static string ResolveReaimAncestor(GhostPlaybackLogic.LoopUnitSet units, int idx)
        {
            if (units != null && units.TryGetUnitForMember(idx, out GhostPlaybackLogic.LoopUnit unit)
                && unit.IsReaim)
                return unit.ReaimPlan.Value.CommonAncestor;
            return null;
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
                seedByPid.Remove(stalePidScratch[i]);
                tracedPathByPid.Remove(stalePidScratch[i]);
                tracedPathIntentByPid.Remove(stalePidScratch[i]);
            }
        }

        // Isolated Unity-native read (Time.frameCount): only ever JIT-compiled in-game, since the unit
        // tests exercise DecideForGhost / ClassifyScope directly and never call RunFrame.
        private static int UnityFrame() => UnityEngine.Time.frameCount;
    }
}
