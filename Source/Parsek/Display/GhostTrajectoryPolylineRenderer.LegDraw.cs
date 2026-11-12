using System;
using System.Collections.Generic;
using UnityEngine;
using Vectrosity;

namespace Parsek.Display
{
    // Leg line drawing: the last-good hold, the per-leg draw / deactivate decisions, TryDrawLeg and the Vectrosity map-line build, style, transform and draw helpers.
    internal static partial class GhostTrajectoryPolylineRenderer
    {
        /// <summary>
        /// Marker ride-robustness (pan-stability): the last on-line position the marker successfully
        /// rode for a recording, kept so a TRANSIENT ride dropout (the leg was not drawn this frame -
        /// the dominant case during an active map-camera pan - or the head sits in an inter-leg gap
        /// inside the recording's overall span) holds the marker on the line instead of snapping to the
        /// body-fixed head. Stamped on every successful <c>RodeLeg</c> in
        /// <see cref="TryAnchorMarkerToPolyline(string,double,out Vector3,out MapRenderTrace.MarkerRideReason,out int)"/>;
        /// consumed by <see cref="TryHoldLastGood"/>, which bounds it by frame-age + head-UT delta so a
        /// genuine orbital-phase exit (head past the last leg, or a long stall) still falls through to
        /// the deep fallback. Cleared with the ownership sets in <see cref="Clear"/>, per-recording in
        /// <see cref="ReleaseForRecording"/>, and on scene switch via the Driver's HandleLevelWasLoaded.
        /// </summary>
        private struct LastGoodOnLine
        {
            public Vector3 worldPos;
            public double headUT;
            public int frame;
            public int legIndex;
        }

        /// <summary>
        /// Pure: does a fresh, near-UT last-good on-line position exist for this recording? Returns true
        /// (and the held world position + leg index) when a cached entry exists, is within
        /// <see cref="LastGoodMaxFrameAge"/> frames, and the live head is within
        /// <see cref="LastGoodMaxHeadUtDeltaSeconds"/> of the cached head. False (deep fallback) when no
        /// cache entry, the entry is stale by frame, or the head has moved too far in UT. xUnit-testable
        /// (no Unity calls); the cache is seeded via the dictionary the ride path stamps.
        /// </summary>
        private static bool TryHoldLastGood(
            string recordingId, double headUT, int frame, out Vector3 worldPos, out int legIndex)
        {
            worldPos = Vector3.zero;
            legIndex = -1;
            if (string.IsNullOrEmpty(recordingId)) return false;
            if (!lastGoodOnLine.TryGetValue(recordingId, out var lg)) return false;
            if (frame - lg.frame > LastGoodMaxFrameAge) return false;
            if (System.Math.Abs(headUT - lg.headUT) > LastGoodMaxHeadUtDeltaSeconds) return false;
            worldPos = lg.worldPos;
            legIndex = lg.legIndex;
            return true;
        }

        /// <summary>
        /// Pure classifier: is the head inside the recording's overall leg span (between the first leg's
        /// start and the last leg's end) yet outside every individual leg - i.e. in an inter-leg GAP
        /// (e.g. a connector/deorbit seam between two drawn legs)? Distinguishes the held-hold case (gap
        /// inside the span) from a genuine orbital-phase exit (head before the first leg or past the last
        /// leg), which must fall through to the deep fallback. xUnit-testable without Unity.
        /// </summary>
        internal static bool IsHeadInInterLegGap(
            double firstStartUT, double lastEndUT, double headUT)
            => headUT >= firstStartUT && headUT <= lastEndUT;

        /// <summary>
        /// Decision for the per-frame deactivation sweep: a cached leg line
        /// should be hidden this frame when it is currently active but was NOT
        /// drawn this frame (loop-hidden, suppressed, body missing, fewer than
        /// two points, or its recording removed from <c>CommittedRecordings</c>).
        /// Pure so the contract is xUnit-testable without a Unity VectorLine.
        /// </summary>
        internal static bool ShouldDeactivateLeg(
            bool currentlyActive, int lastDrawnFrame, int drawFrame)
            => currentlyActive && lastDrawnFrame != drawFrame;

        /// <summary>
        /// Per-leg head-UT visibility gate: a non-orbital leg is drawn only
        /// while the ghost's current playback position (<paramref name="headUT"/>,
        /// in the recording's own timeline) lies within the leg's recorded
        /// [<paramref name="legStartUT"/>, <paramref name="legEndUT"/>] span
        /// (inclusive). Outside that span the leg is skipped and the
        /// deactivation sweep hides it, so the polyline tracks the moving ghost
        /// (visible only where the ghost currently is) instead of painting the
        /// whole recorded path continuously. Pure so the contract is xUnit
        /// testable without Unity.
        /// </summary>
        internal static bool ShouldDrawLegAtHeadUT(
            double legStartUT, double legEndUT, double headUT)
            => headUT >= legStartUT && headUT <= legEndUT;

        /// <summary>
        /// Phase 8b.1 no-double-draw routing predicate (Driver side): should this recording's current
        /// non-orbital leg be drawn by the OWNED <c>TracedPathTreatment</c> instead of the Driver's
        /// direct <c>TryDrawLeg</c> call? True exactly when the Director owns the ghost's active leg as a
        /// fresh TracedPath this frame (<paramref name="directorOwnsTracedPath"/> =
        /// <c>ShadowRenderDriver.IsTracedPathOwnedThisFrame(pid, frame)</c>). The Driver routes through
        /// the treatment when true and STANDS DOWN on its own direct call; it draws directly when false.
        /// Because the treatment's draw and the Driver's stand-down are the SAME boolean - and the icon-
        /// drive / orbit-line patches read the same predicate to suppress the stock proto - the leg can
        /// never be drawn twice (treatment + Driver, or polyline + proto) on any frame. Pure mirror of
        /// <c>TracedPathTreatment.ShouldOwnLeg</c>; xUnit-testable without Unity.
        /// </summary>
        internal static bool ShouldDrawLegOwnedByTreatment(bool directorOwnsTracedPath)
            => directorOwnsTracedPath;

        /// <summary>
        /// Pan-stability split predicate (FIX 1): WILL this leg draw this frame? The Driver's decide
        /// pass (at <c>[DefaultExecutionOrder(-50)]</c>) publishes leg ownership BEFORE the actual point
        /// recompute + <c>Draw3D</c> moves to the post-camera-pan <c>Camera.onPreCull</c> slot, so the
        /// ownership publish can no longer key on the real draw return. This mirrors <see cref="TryDrawLeg"/>'s
        /// only NON-degenerate early returns: the body must be resolved (the body-null skip lives at the
        /// decide-pass call site, which <c>continue</c>s before reaching here) and the leg must carry at
        /// least two points (<c>m &lt; 2</c> skip). The lazy VectorLine-inflate-failure path
        /// (<c>BuildLegVectorLine</c> returns null) is treated as will-draw for ownership purposes: it is
        /// effectively never hit in map view, and keeping the predicate the exact mirror of the
        /// non-degenerate returns means will-draw == actual-draw for every real leg, so the order-0
        /// <c>GhostOrbitLinePatch</c> still observes "polyline owns this phase iff a leg draws" with no
        /// decision-without-draw gap. Pure; xUnit-testable without Unity.
        /// </summary>
        internal static bool WillLegDraw(int pointCount, bool bodyResolved)
            => bodyResolved && pointCount >= 2;

        // ------------------------------------------------------------------
        // Per-leg draw mechanics (Phase 8b.0 extraction)
        //
        // The single-leg build + conic-anchor + VectorLine submit pipeline,
        // pulled OUT of the autonomous Driver's per-frame walk verbatim and
        // parameterized on EXPLICIT inputs so a future TracedPathTreatment
        // (Phase 8b.1) can draw one leg through the SAME code instead of going
        // through the Driver's CommittedRecordings walk. The Driver now calls
        // TryDrawLeg per leg, so its per-frame output is byte-identical to the
        // pre-extraction inlined body (the code below IS that body, with the
        // captured locals passed as arguments). This is a mechanical move +
        // parameterization, not a logic change: no draw decision, no anchor
        // math, no VectorLine state, and no activation/deactivation differs.
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds (lazily, once), positions, fills, conic-anchors, and draws a
        /// single non-orbital leg's own VectorLine for ONE frame, exactly as the
        /// Driver's per-leg inner loop did inline. Scene-agnostic: every input it
        /// needs is passed explicitly (the resolved <paramref name="body"/>, the
        /// owning <paramref name="rec"/> for the conic-anchor, the target
        /// <paramref name="targetLayer"/> and <paramref name="drawFrame"/>, and the
        /// <paramref name="recordingId"/> / <paramref name="legIndex"/> used only to
        /// name the lazily-inflated line). Mutates <paramref name="leg"/> in place
        /// (inflates <c>vectorLine</c>, fills <c>scratchScaledSpace</c>, stamps
        /// <c>lastDrawnFrame</c>) so the caller writes the struct back into its
        /// cached array, identical to the old <c>set.legs[li] = leg</c>.
        ///
        /// Returns true when the leg was drawn this frame (the old <c>anyDrawn</c>
        /// signal), false when it was skipped because it has fewer than two points
        /// or its VectorLine could not be inflated (identical skip conditions to the
        /// inlined body; the body-missing skip stays at the call site since it owns
        /// the body resolution + the rate-limited missing-body log).
        /// </summary>
        internal static bool TryDrawLeg(
            ref LegPolyline leg, Recording rec, CelestialBody body,
            int targetLayer, int drawFrame, string recordingId, int legIndex,
            bool requireConicAnchor = false)
        {
            int m = leg.PointCount;
            if (m < 2) return false;

            // Inflate this leg's own VectorLine lazily. One line PER
            // leg: a single shared line drawn once per leg via
            // drawStart/drawEnd range slicing does NOT work, because
            // VectorLine.Draw3D() zeroes every vertex outside the
            // current window on each call, leaving only the last leg.
            // Bug 1: rebuild the line when the map-line mode flipped (3D Draw3D <-> 2D Draw at the
            // far-zoom threshold) - Vectrosity's in-place swap leaves the 2D canvas line non-rendering, so
            // (like stock) the line is recreated on flip. See RebuildLineForMode.
            int legWantMode = MapLineUses3D() ? 1 : 2;
            if (leg.vectorLine != null && leg.lineMode != legWantMode)
            {
                leg.vectorLine = RebuildLineForMode(leg.vectorLine, m);
            }
            if (leg.vectorLine == null)
                leg.vectorLine = BuildLegVectorLine(recordingId, legIndex, m);
            leg.lineMode = legWantMode;
            if (leg.vectorLine == null) return false;

            // Layer 31 always; in 3D mode also zero the world transform so the absolute-scaled-space mesh
            // is not double-transformed (matches OrbitRendererBase's REDRAW path). In 2D mode the line is
            // a Vectrosity canvas child and the world-position reset would shove it off the canvas plane
            // (Bug 1 zoom-out vanish), so it is skipped there. See PrepareMapLineTransform.
            PrepareMapLineTransform(leg.vectorLine, targetLayer);

            // CRITICAL geometry (warp-strobe fix, take 2 - strobe-free AND never invisible).
            // A body-fixed surface point must follow the planet's spin. Two failed approaches and
            // why, from the strobe probe:
            //   (a) ABSOLUTE: scratchScaledSpace[i] = LocalToScaledSpace(GetWorldSurfacePosition(.))
            //       STROBES under warp into a parallel duplicate - ScaledSpace.totalOffset (the
            //       scaled-origin recentering) oscillates every RENDER frame and our non-registered
            //       VectorLine inherits it (|dPlive| up to ~750 scaled units, alternating with 0).
            //   (b) FREEZE: capture each point in scaledBody-local once and re-project - kills the
            //       strobe but goes INVISIBLE under warp, because a capture taken at a strobe phase
            //       bakes a bad totalOffset into the local and lands the line off-screen until a
            //       clean 1x recapture.
            // The probe shows GetWorldSurfacePosition is frame-stable (|dWorld|=0) and
            // scaledBody.transform is a registered ScaledSpace object whose position is frame-stable
            // (|dPfrozen|~0). So build the point from the STABLE pieces only: the scaled body centre
            // (scaledXform.position) plus the body-relative surface offset (world - body.position) *
            // invScale. That offset is totalOffset- AND floating-origin-free (both terms live in the
            // same frame, so it cancels), and equals LocalToScaledSpace(world) exactly when
            // scaledXform.position == LocalToScaledSpace(body.position) - but it never touches the
            // strobing totalOffset. Recomputed LIVE every frame: strobe-free at all warps, and with
            // no capture it can never go stale / invisible. scaledXform also feeds the conic anchor.
            var scaledBody = body.scaledBody;
            Transform scaledXform = scaledBody != null ? scaledBody.transform : null;
            bool parityFilled = false;
            if (scaledXform != null)
            {
                Vector3 bodyCentreScaled = scaledXform.position;
                double invScale = ScaledSpace.InverseScaleFactor;
                Vector3d bodyPos = body.position;
                // PARITY SNAPSHOT (tracing-only): record the body-relative offsets THIS draw builds the
                // vertices from, plus the exact centre float added in, so the parity capture diffs the
                // drawn points3 against a SAME-FRAME reference. Re-evaluating the surface positions /
                // centre at capture time (a different part of the frame) skews the reference by a frame
                // of body translation + rotation (x warp) - pure artifact, not draw drift. Null / not
                // stamped in normal play: zero cost while tracing is off.
                double[] parityRel = null;
                if (MapRenderTrace.IsEnabled)
                {
                    parityRel = leg.parityRecordedRel;
                    if (parityRel == null || parityRel.Length != m * 3)
                        parityRel = new double[m * 3];
                }
                for (int i = 0; i < m; i++)
                {
                    Vector3d world = body.GetWorldSurfacePosition(
                        leg.lats[i], leg.lons[i], leg.alts[i]);
                    Vector3d rel = world - bodyPos;
                    leg.scratchScaledSpace[i] = bodyCentreScaled + (Vector3)(rel * invScale);
                    if (parityRel != null)
                    {
                        parityRel[i * 3] = rel.x;
                        parityRel[i * 3 + 1] = rel.y;
                        parityRel[i * 3 + 2] = rel.z;
                    }
                }
                if (parityRel != null)
                {
                    leg.parityRecordedRel = parityRel;
                    leg.parityDrawCentreScaled = bodyCentreScaled;
                    parityFilled = true; // frame stamped at the draw write-back below
                }
            }
            else
            {
                // No scaled body available (should not happen in map view): fall back to the
                // direct absolute path. Strobes under warp, but at least renders on the surface.
                // No parity snapshot: this branch strobes by design, so the parity lens skips it
                // (parityDrawFrame stays != lastDrawnFrame).
                for (int i = 0; i < m; i++)
                {
                    Vector3d world = body.GetWorldSurfacePosition(
                        leg.lats[i], leg.lons[i], leg.alts[i]);
                    leg.scratchScaledSpace[i] =
                        (Vector3)ScaledSpace.LocalToScaledSpace(world);
                }
            }

            // CONIC ANCHOR: for a vacuum maneuver between two orbits (escape burn, orbit
            // raise) rotate the captured body-fixed scaled points onto the faithful bracketing
            // conic seam so the leg CONNECTS the loiter/hyperbola lines instead of drawing
            // ~96 deg off under the loop shift. No-op for legs not bracketed both sides (launch
            // ascent / descent-to-surface) and where body-fixed already matches the conic.
            bool anchored = TryAnchorLegToConicSeam(rec, leg, body, scaledXform);
            // Persist this draw's anchor outcome so the marker-ride fallback knows whether the drawn line
            // is conic-rotated off the body-fixed head (hold the last on-line point) or is the raw
            // body-fixed points (the caller's fresh body-fixed head is already on the line). Written back
            // into the cached leg via the same struct write-back as lastDrawnFrame below.
            leg.wasAnchored = anchored;

            // RUN-LEG body-fixed hide (playtest-5 rule, 2026-06-09): a persistent run leg (icon NOT on
            // it, requireConicAnchor=true) that could not be anchored to the inertial conic seam would
            // draw BODY-FIXED and visibly rotate with the planet against the inertial arcs (the observed
            // gap-then-overlap sweep at the chain handoff). Skip the draw - the deactivation sweep hides
            // any previously drawn line this frame. The decide-side IsRunLegAnchorCandidate pre-filter
            // already drops one-sided legs; this catches the residual-rejected remainder (the
            // seam-does-not-meet-the-leg guard inside TryAnchorLegToConicSeam, e.g. a mis-bracketed
            // arrival hyperbola). Head-gated current legs (requireConicAnchor=false) keep drawing
            // body-fixed - the live ghost is glued to the pad/terrain there, so body-fixed is correct.
            if (requireConicAnchor && !anchored)
            {
                // Invalidate the tracing-only parity snapshot: the fill above already overwrote the
                // snapshot array IN PLACE (it is aliased by the cache), but this leg will NOT draw this
                // frame - points3 still hold the PREVIOUS draw's (possibly conic-anchored) vertices.
                // Without this, the stale stamps can be pairwise EQUAL (parityDrawFrame ==
                // lastDrawnFrame, both from the prior drawn frame), so the capture's coherence check
                // would pass and diff the old anchored points3 against the fresh RAW snapshot - a false
                // parity-drift of anchor-rotation magnitude on the anchored->seam-rejected transition.
                // 0 = "never filled"; the next successful draw refills and restamps.
                leg.parityDrawFrame = 0;
                ParsekLog.VerboseRateLimited(Tag, "runleg-anchor-reject." + recordingId,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Run leg hidden (conic anchor unavailable): rec={0} leg={1} [{2:F1},{3:F1}] " +
                        "body={4} - body-fixed leg would rotate against the inertial run",
                        recordingId, legIndex, leg.startUT, leg.endUT, leg.bodyName ?? "(null)"),
                    3.0);
                return false;
            }

            CopyLegIntoVectorLine(leg.vectorLine, leg.scratchScaledSpace, 0);
            leg.vectorLine.drawStart = 0;
            leg.vectorLine.drawEnd = m - 1;
            // Reactivate if a prior frame's sweep hid this leg, then
            // draw and stamp the frame. The single write-back below
            // persists BOTH the lazily-inflated line AND the frame
            // stamp into the cached array (set.legs is the same array
            // reference the dict holds, so writing set.legs[li]
            // carries through without re-storing the struct).
            if (!leg.vectorLine.active) leg.vectorLine.active = true;
            // Bug 1 fix: draw in the stock map-line mode (Draw3D below the far-zoom threshold, 2D Draw()
            // past it) so the leg never vanishes on zoom-out. See DrawMapLine.
            DrawMapLine(leg.vectorLine);
            leg.lastDrawnFrame = drawFrame;
            // Stamp the parity snapshot as belonging to THIS draw only after the draw actually happened.
            // NOTE: a stale stamp alone is NOT protection - the fill mutates the aliased snapshot array
            // in place, and the stale parityDrawFrame can EQUAL the stale lastDrawnFrame (both from the
            // prior drawn frame), which passes the capture's coherence check. The anchor-reject early-out
            // above therefore invalidates the stamp EXPLICITLY (parityDrawFrame = 0) on its exit path.
            if (parityFilled)
                leg.parityDrawFrame = drawFrame;
            return true;
        }

        /// <summary>
        /// Constructs a fresh per-leg <c>LineType.Continuous</c>
        /// VectorLine sized to hold exactly this leg's points. One line
        /// per leg (see <see cref="LegPolyline.vectorLine"/>): a single
        /// shared line drawn once per leg via <c>drawStart</c>/<c>drawEnd</c>
        /// range slicing does NOT work, because <c>Draw3D()</c> zeroes
        /// every vertex outside the current window on each call. Uses
        /// <c>MapView.OrbitLinesMaterial</c> (the SOLID stock orbit-line
        /// material, NOT the dotted/dashed one) with the same 5f width and
        /// distance/direction fade, so each leg reads as one unbroken
        /// orbit-style line with no dashes, gaps, or interruptions (mirrors
        /// <c>OrbitRendererBase.MakeLine</c>). A per-line vertex colour via
        /// <see cref="VectorLine.SetColor(Color)"/> is set to the EXACT stock
        /// vessel orbit-line grey so the polyline matches the ghost's own
        /// orbit arcs; being a per-line colour it does not mutate the shared
        /// material (which would dim every stock orbit line).
        /// </summary>
        internal static VectorLine BuildLegVectorLine(
            string recordingId, int legIndex, int pointCount)
        {
            return BuildContinuousMapLine(
                "ParsekGhostTrajectoryPolyline-" + recordingId + "-leg" + legIndex, pointCount);
        }

        /// <summary>
        /// Shared builder for a stock-orbit-styled <c>LineType.Continuous</c> map line with
        /// <paramref name="pointCount"/> zero points (width 5f, <see cref="ApplyStockOrbitLineStyle"/>).
        /// Backs <see cref="BuildLegVectorLine"/>, <see cref="BuildForwardArcVectorLine"/>, and the Bug 1
        /// <see cref="RebuildLineForMode"/> rebuild (which preserves the prior line's name).
        /// </summary>
        internal static VectorLine BuildContinuousMapLine(string name, int pointCount)
        {
            if (pointCount <= 0) return null;
            var points = new List<Vector3>(pointCount);
            for (int i = 0; i < pointCount; i++)
                points.Add(Vector3.zero);
            var line = new VectorLine(name, points, 5f, LineType.Continuous);
            ApplyStockOrbitLineStyle(line);
            return line;
        }

        /// <summary>
        /// Bug 1 rebuild-on-flip: destroys <paramref name="existing"/> and recreates it (same name +
        /// <paramref name="pointCount"/>, stock orbit-line style) for the current map-line mode. Vectrosity's
        /// in-place <c>VectorObject3D</c>&lt;-&gt;<c>VectorObject2D</c> swap (toggling <c>Draw3D()</c>/
        /// <c>Draw()</c> on ONE line at the far-zoom 2D flip) leaves the 2D canvas Graphic non-rendering, so
        /// the line vanishes past the threshold; stock sidesteps this by rebuilding the line on every mode
        /// flip (<c>OrbitRendererBase</c> calls <c>MakeLine</c>), which this mirrors. The caller re-copies
        /// the points into the fresh line the same frame, so no geometry is lost.
        /// </summary>
        internal static VectorLine RebuildLineForMode(VectorLine existing, int pointCount)
        {
            string name = existing != null ? existing.name : "ParsekGhostMapLine";
            if (existing != null)
            {
                var old = existing;
                VectorLine.Destroy(ref old);
            }
            return BuildContinuousMapLine(name, pointCount);
        }

        /// <summary>
        /// Styles a fresh <c>LineType.Continuous</c> VectorLine to look EXACTLY like a stock vessel orbit
        /// line so a polyline leg / forward arc is indistinguishable from the ghost's own orbit arcs: the
        /// SOLID <c>MapView.OrbitLinesMaterial</c> (NOT the dotted/dashed material), the same 5f width and
        /// distance/direction fade (mirrors <c>OrbitRendererBase.MakeLine</c>), and the stock vessel
        /// orbit-line grey via a PER-LINE vertex colour (so the shared material is never dimmed). Shared by
        /// the leg lines (<see cref="BuildLegVectorLine"/>) and the forward-arc lines
        /// (<see cref="BuildForwardArcVectorLine"/>) so current + future read as one continuous line, the
        /// uniform style the plan requires. _FadeStrength / _FadeSign are global GameSettings values set on
        /// the SHARED material (idempotent: stock sets the same values every orbit line), so this does not
        /// disturb real orbit lines.
        /// </summary>
        private static void ApplyStockOrbitLineStyle(VectorLine line)
        {
            if (line == null) return;
            Material orbitMat = MapView.OrbitLinesMaterial;
            if (orbitMat != null)
            {
                line.texture = orbitMat.mainTexture;
                line.material = orbitMat;
                orbitMat.SetFloat("_FadeStrength", GameSettings.ORBIT_FADE_STRENGTH);
                orbitMat.SetFloat("_FadeSign",
                    GameSettings.ORBIT_FADE_DIRECTION_INV ? -1f : 1f);
            }
            line.continuousTexture = true;
            line.UpdateImmediate = true;
            // EXACT stock vessel orbit-line colour: KSP's OrbitRenderer seeds an unfocused vessel with
            // SetColor(new Color(0.71,0.71,0.71,1)) and draws the line at orbitColor = nodeColor * 0.5
            // (alpha preserved) with lineOpacity 1 (OrbitRenderer.GetOrbitColour / OrbitRendererBase),
            // i.e. the mid-grey below. Per-line vertex colour, so the shared OrbitLinesMaterial is untouched.
            Color stockNode = new Color(0.71f, 0.71f, 0.71f, 1f);
            Color stockOrbit = stockNode * 0.5f;
            stockOrbit.a = stockNode.a;
            line.SetColor(stockOrbit);
        }

        /// <summary>
        /// Pure draw-mode decision mirroring stock <c>OrbitRendererBase.DrawSpline</c>: a map / scaled
        /// orbit line is drawn with <c>VectorLine.Draw3D()</c> only when <c>MapView.Draw3DLines</c> is
        /// true, otherwise with the 2D <c>VectorLine.Draw()</c> screen-space path. Stock flips
        /// <c>MapView.Draw3DLines</c> to false (<c>MapView.UpdateMap</c>) when the map camera zooms out
        /// past <c>max3DlineDrawDist</c> (1500) or too many orbits are on screen
        /// (<c>MAP_MAX_ORBIT_BEFORE_FORCE2D</c> = 150), precisely because Vectrosity's <c>Draw3D</c>
        /// world-space vertex reconstruction (<c>cam3D.ScreenToWorldPoint</c> at each point's far camera
        /// distance) degrades at far zoom and drops the line. Parsek previously ALWAYS called
        /// <c>Draw3D()</c>, so its legs / arcs / bridges vanished past that same 1500 threshold while stock
        /// orbit lines (flipped to 2D) persisted (Bug 1: looped-mission trajectory lines vanishing when
        /// zoomed out). When MapView is absent (<paramref name="mapViewPresent"/> false, e.g. a transient
        /// DDOL tick during a scene switch) default to 3D - the prior behavior, and never an NPE. Kept
        /// <c>internal static</c> + parameterized for headless unit-testability (no <c>MapView</c> ECall).
        /// </summary>
        internal static bool ShouldDraw3DMapLine(bool mapViewPresent, bool mapViewDraw3DLines)
        {
            return !mapViewPresent || mapViewDraw3DLines;
        }

        /// <summary>
        /// Live wrapper over <see cref="ShouldDraw3DMapLine"/>: reads the runtime MapView state (the
        /// <c>MapView.Draw3DLines</c> read short-circuited behind the <c>MapView.fetch</c> null check) and
        /// returns whether map lines are in stock 3D (<c>Draw3D()</c>) mode this frame, false in the 2D
        /// (<c>Draw()</c>) mode stock flips to past the far-zoom threshold. Used by both
        /// <see cref="DrawMapLine"/> and the draw-site transform setup so they agree within a frame.
        /// </summary>
        internal static bool MapLineUses3D()
        {
            bool present = MapView.fetch != null;
            return ShouldDraw3DMapLine(present, present && MapView.Draw3DLines);
        }

        /// <summary>
        /// Applies a leg / arc / bridge line's per-frame transform for the CURRENT map-line mode, called
        /// right before <see cref="DrawMapLine"/>. The layer (stock orbit-line layer 31) is set in BOTH
        /// modes. The world-transform zeroing (<c>position = 0</c>, identity rotation, unit scale) is done
        /// ONLY in 3D (<c>Draw3D()</c>) mode: there the line is an unparented world mesh of ABSOLUTE
        /// scaled-space points, so its GameObject transform must be the identity or the mesh inherits a
        /// parent transform and drifts as the map camera pans. In 2D (<c>Draw()</c>) mode the line lives
        /// under KSP's Vectrosity ScreenSpaceCamera canvas (<c>vectorCam</c>, layer 31); forcing world
        /// <c>position = 0</c> every frame shoves the canvas child off the canvas plane so it never paints
        /// - the Bug 1 zoom-out vanish past the 2D-flip threshold. In 2D the canvas-managed RectTransform
        /// is left untouched (stock writes position only on recalculation, which is why stock 2D orbit
        /// lines render and Parsek's did not).
        /// </summary>
        internal static void PrepareMapLineTransform(VectorLine line, int targetLayer)
        {
            if (line == null || line.rectTransform == null) return;
            line.rectTransform.gameObject.layer = targetLayer;
            if (!MapLineUses3D()) return;
            var xform = line.rectTransform;
            xform.position = Vector3.zero;
            xform.rotation = Quaternion.identity;
            xform.localScale = Vector3.one;
        }

        /// <summary>
        /// Draws a leg / arc / bridge line in the SAME map-line mode stock orbit lines use this frame:
        /// <c>Draw3D()</c> when <see cref="ShouldDraw3DMapLine"/> (the live <c>MapView.Draw3DLines</c>),
        /// else the 2D <c>Draw()</c> path. This IS the Bug 1 fix - past the stock far-zoom 2D-flip
        /// threshold the 2D path renders the line through Vectrosity's <c>vectorCam</c> overlay on the same
        /// shared canvas + layer 31 stock orbit lines use, so Parsek lines stop vanishing on zoom-out. The
        /// <c>MapView.Draw3DLines</c> read is short-circuited behind the <c>MapView.fetch</c> null check so
        /// it never NPEs during a scene transition. Replaces the reverted provisional mesh-bounds override,
        /// which was a confirmed no-op: the real vanish lives INSIDE <c>Draw3D</c> (a screen-space path the
        /// AABB frustum cull never reached), and in 2D mode Vectrosity destroys the MeshFilter, so there is
        /// no mesh to stamp at all.
        /// </summary>
        internal static void DrawMapLine(VectorLine line)
        {
            if (line == null) return;
            if (MapLineUses3D())
                line.Draw3D();
            else
                line.Draw();
        }

        /// <summary>
        /// Copies a leg's scratch <c>Vector3[]</c> into the leg's own
        /// VectorLine's <c>points3</c> list starting at the given offset
        /// (0 for per-leg lines).
        /// </summary>
        internal static void CopyLegIntoVectorLine(
            VectorLine line, Vector3[] scratch, int startIdx)
        {
            if (line == null || scratch == null) return;
            var points3 = line.points3;
            if (points3 == null) return;
            for (int i = 0; i < scratch.Length; i++)
            {
                int dst = startIdx + i;
                if (dst < 0 || dst >= points3.Count) continue;
                points3[dst] = scratch[i];
            }
        }
    }
}
