using System;
using System.Collections.Generic;
using UnityEngine;
using Vectrosity;

namespace Parsek.Display
{
    // Observability: the M-A7 co-draw probe, drawn-set diff and change lines, the rendered snapshot and the rendered-vs-recorded leg geometry parity capture.
    internal static partial class GhostTrajectoryPolylineRenderer
    {
        /// <summary>
        /// Read-only visitor over the ghost polyline LEG cache for the automation-only
        /// map-geometry dump (<c>MapGeometryRecorder</c>): hands every cached leg that owns a
        /// <c>VectorLine</c> to <paramref name="onLeg"/> as (recordingId, legIndex, leg). The leg
        /// is a COPY of the cached struct; the visitor writes nothing back, so nothing drawn
        /// changes. The caller decides what counts as drawn (<c>vectorLine.active</c>,
        /// <c>lastDrawnFrame</c>).
        /// </summary>
        internal static void VisitCachedLegs(Action<string, int, LegPolyline> onLeg)
        {
            if (onLeg == null) return;
            foreach (var kv in polylineCache)
            {
                LegPolyline[] legs = kv.Value.legs;
                if (legs == null) continue;
                for (int l = 0; l < legs.Length; l++)
                {
                    if (legs[l].vectorLine == null) continue;
                    onLeg(kv.Key, l, legs[l]);
                }
            }
        }

        /// <summary>
        /// Read-only visitor over the FORWARD-ARC cache for the map-geometry dump: hands every
        /// cached arc that owns a <c>VectorLine</c> to <paramref name="onArc"/> as (recordingId,
        /// boundaryOverlapSecondary, arcIndex, arc). The recording id has the boundary-overlap
        /// secondary namespace suffix removed (<see cref="ForwardArcDictKey"/>), and the flag says
        /// which namespace the arc came from. Writes nothing back.
        /// </summary>
        internal static void VisitCachedForwardArcs(Action<string, bool, int, ForwardArc> onArc)
        {
            if (onArc == null) return;
            foreach (var kv in forwardArcCache)
            {
                ForwardArc[] arcs = kv.Value.arcs;
                if (arcs == null) continue;
                bool secondary = IsForwardSecondaryDictKey(kv.Key, out string recordingId);
                for (int a = 0; a < arcs.Length; a++)
                {
                    if (arcs[a].vectorLine == null) continue;
                    onArc(recordingId, secondary, a, arcs[a]);
                }
            }
        }

        /// <summary>One seam-bridge line as the map-geometry dump reads it: the leg it joins
        /// (recording id, leg index, side), the body its scaled-space points are centred on, the
        /// line itself and the frame it last drew.</summary>
        internal struct BridgeLineView
        {
            internal string LegRecordingId;
            internal int LegIndex;
            internal bool AtLegStart;
            internal string BodyName;
            internal VectorLine Line;
            internal int LastDrawnFrame;
        }

        /// <summary>
        /// Read-only visitor over the SEAM-BRIDGE line cache for the map-geometry dump: hands
        /// every cached bridge that owns a <c>VectorLine</c> and has drawn at least once to
        /// <paramref name="onBridge"/>, in ordinal key order (the cache key is
        /// <c>legRecordingId|legIndex|s|e</c>). Writes nothing back. The caller decides what
        /// counts as drawn (<c>Line.active</c>, <c>LastDrawnFrame</c>).
        /// </summary>
        internal static void VisitCachedBridgeLines(Action<BridgeLineView> onBridge)
        {
            if (onBridge == null || bridgeLineByRecording.Count == 0) return;
            var keys = new List<string>(bridgeLineByRecording.Keys);
            keys.Sort(StringComparer.Ordinal);
            for (int i = 0; i < keys.Count; i++)
            {
                BridgeLineEntry e = bridgeLineByRecording[keys[i]];
                if (e == null || e.line == null || e.legRecordingId == null) continue;
                onBridge(new BridgeLineView
                {
                    LegRecordingId = e.legRecordingId,
                    LegIndex = e.legIndex,
                    AtLegStart = e.atLegStart,
                    BodyName = e.bodyName,
                    Line = e.line,
                    LastDrawnFrame = e.lastDrawnFrame,
                });
            }
        }

        /// <summary>
        /// Pure inverse of <see cref="ForwardArcDictKey"/>: true when <paramref name="dictKey"/>
        /// is a boundary-overlap secondary key, with <paramref name="recordingId"/> the id it
        /// was built from; otherwise false and the key itself.
        /// </summary>
        internal static bool IsForwardSecondaryDictKey(string dictKey, out string recordingId)
        {
            recordingId = dictKey;
            if (string.IsNullOrEmpty(dictKey)
                || dictKey.Length <= ForwardSecondaryCacheSuffix.Length
                || !dictKey.EndsWith(ForwardSecondaryCacheSuffix, StringComparison.Ordinal))
                return false;
            recordingId = dictKey.Substring(0, dictKey.Length - ForwardSecondaryCacheSuffix.Length);
            return true;
        }

        /// <summary>
        /// M-A7 co-draw probe: is ANY cached ghost leg for <paramref name="recordingId"/> STILL SHOWING
        /// a mesh right now - i.e. does it have a <c>VectorLine</c> whose <c>active</c> is true - over
        /// the route leg's own span [<paramref name="legStartUT"/>, <paramref name="legEndUT"/>]?
        ///
        /// <para>THE SPAN ARGUMENT IS PART OF THE MEASUREMENT, not a narrowing of it: since the
        /// arbitration became per leg, a route member the ghost paints only PARTLY legitimately draws
        /// its unpainted legs, and a recording-wide <c>active</c> read would report each of those
        /// correct draws as a violation. Overlap is the same strict test the arbitration uses
        /// (<see cref="LegSpansOverlap"/>), applied to the LIVE Vectrosity state rather than to the
        /// maintained membership set - which is what keeps this an INSTRUMENT of the arbitration
        /// instead of a restatement of it: a membership set that drifts from the meshes it mirrors
        /// (a missed hide path) reds here.</para>
        ///
        /// <para>The <c>active</c> read ALONE is the measurement, deliberately. The route draw pass
        /// runs after the ghost path's deactivation sweep, so by the time this is asked, every leg the
        /// ghost path did NOT draw this frame has already been deactivated. A leg still reading
        /// <c>active</c> here is therefore exactly the defect the probe exists for: a mesh painted on
        /// an earlier frame that nothing retired, about to be overpainted by the route overview line.
        /// A <c>lastDrawnFrame &gt;= frame - 1</c> disjunct would ADD the legitimate case - a leg the
        /// ghost path drew normally this frame or last - and report it as a violation, so it is not
        /// used here. (<c>EmitRenderedPolylineSnapshot</c> keeps that convention because it is
        /// answering the different question "what did we draw recently", from the onPreCull slot.)</para>
        ///
        /// <para>This is NOT the ownership signal (<see cref="IsRenderingNonOrbitalLeg"/> is, and the
        /// route line's no-double-draw arbitration keeps consulting that one). It exists so the
        /// render-composition manifest can record an explicit CO-DRAW VIOLATION on the one frame shape
        /// the ownership set cannot see: the LateUpdate walk early-returned after the -50 clear
        /// (controller defer / map not enabled), leaving the drew-set empty while a prior frame's
        /// ghost leg mesh is still active, so the route overview line paints a second identical line
        /// over it. Aggregate skip counts alone cannot prove the absence of such a frame; a violation
        /// RECORD can. Read-only; Unity-coupled (reads <c>VectorLine.active</c>), so it is called only
        /// from the live route draw path.</para>
        /// </summary>
        internal static bool IsAnyLegActiveForRecording(
            string recordingId, double legStartUT, double legEndUT)
        {
            if (string.IsNullOrEmpty(recordingId)) return false;
            if (!polylineCache.TryGetValue(recordingId, out LegPolylineSet cached)) return false;
            LegPolyline[] legs = cached.legs;
            if (legs == null) return false;
            for (int i = 0; i < legs.Length; i++)
            {
                LegPolyline leg = legs[i];
                if (leg.vectorLine == null || !leg.vectorLine.active) continue;
                if (LegSpansOverlap(leg.startUT, leg.endUT, legStartUT, legEndUT)) return true;
            }
            return false;
        }

        /// <summary>
        /// PURE set diff for the polyline render-EVENT appear/disappear emit: given the recordings drawn on
        /// the PREVIOUS frame (<paramref name="previous"/>) and the recordings drawn on the CURRENT frame
        /// (<paramref name="current"/>), fills <paramref name="appeared"/> (in current, not previous) and
        /// <paramref name="disappeared"/> (in previous, not current), each sorted Ordinal for deterministic
        /// output. The output lists are cleared first. No Unity dependency, so it is unit-testable directly;
        /// the caller owns updating <paramref name="previous"/> := <paramref name="current"/> after emitting.
        /// </summary>
        internal static void DiffDrawnSets(
            HashSet<string> previous, HashSet<string> current,
            List<string> appeared, List<string> disappeared)
        {
            appeared.Clear();
            disappeared.Clear();
            if (current != null)
                foreach (string id in current)
                    if (previous == null || !previous.Contains(id))
                        appeared.Add(id);
            if (previous != null)
                foreach (string id in previous)
                    if (current == null || !current.Contains(id))
                        disappeared.Add(id);
            appeared.Sort(StringComparer.Ordinal);
            disappeared.Sort(StringComparer.Ordinal);
        }

        /// <summary>
        /// Diff <paramref name="current"/> against <paramref name="previous"/> and emit ONE
        /// <c>phase=PolylineLegChange event=appear|disappear</c> MapRenderTrace structural EVENT per
        /// recording that started / stopped drawing on <paramref name="surface"/> (Polyline = current
        /// element, PolylineForwardArc = the forward chain ahead of the icon), then copy current into
        /// previous so the next frame diffs against this one. Each line carries the recordingId in BOTH the
        /// pid= and recId= slots (the polyline is a recording-scoped surface that may have no proto ghost,
        /// so the recordingId IS its identity). Emitted as IMPORTANT (no detailed-window open) so the
        /// appear/disappear shows even with Verbose off, matching the GhostCreated/GhostDestroyed tier. The
        /// whole method is IsEnabled-gated by the caller; the diff scratch lists are reused. Read-only.
        /// </summary>
        private static void EmitPolylineDrawSetChanges(
            MapRenderTrace.RenderSurface surface,
            HashSet<string> previous, HashSet<string> current, double currentUT)
        {
            DiffDrawnSets(previous, current, diffAppearedScratch, diffDisappearedScratch);
            if (diffAppearedScratch.Count == 0 && diffDisappearedScratch.Count == 0)
                return; // steady state: nothing changed, no copy needed (sets are equal)
            for (int i = 0; i < diffAppearedScratch.Count; i++)
                EmitPolylineLegChange(surface, diffAppearedScratch[i], "appear", currentUT);
            for (int i = 0; i < diffDisappearedScratch.Count; i++)
                EmitPolylineLegChange(surface, diffDisappearedScratch[i], "disappear", currentUT);
            previous.Clear();
            foreach (string id in current)
                previous.Add(id);
        }

        private static void EmitPolylineLegChange(
            MapRenderTrace.RenderSurface surface, string recordingId, string ev, double currentUT)
        {
            // Soft per-(surface, recordingId, event) wall-clock floor so a per-frame oscillating draw-set
            // (warp-reseed-lag line-blink) collapses to onset + a ~1/s heartbeat instead of flooding Info
            // every frame; a stable appear/disappear emits immediately (floor long expired). The caller copies
            // previous:=current regardless, so a throttled transition is simply not re-detected. Only reached
            // when MapRenderTrace.IsEnabled (caller-gated), so disabled play pays nothing for this.
            string key = ((int)surface).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "|" + recordingId + "|" + ev;
            double now = UnityEngine.Time.realtimeSinceStartup;
            if (legChangeLastEmitRealtime.TryGetValue(key, out double last)
                && now - last < LegChangeEmitMinIntervalSeconds)
                return;
            // Warp safeguard: bound the dict before inserting a NEW key (recordingIds are stable, so this is
            // belt-and-braces; clearing only drops other keys' floors, harmless - they re-emit on next change).
            if (!legChangeLastEmitRealtime.ContainsKey(key) && legChangeLastEmitRealtime.Count >= 4096)
                legChangeLastEmitRealtime.Clear();
            legChangeLastEmitRealtime[key] = now;

            MapRenderTrace.EmitStructural(
                "PolylineLegChange", surface, recordingId, currentUT, currentUT,
                windowSeconds: 0.0,
                details: string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "event={0} scene={1} warp={2:F0}x",
                    ev, HighLogic.LoadedScene, TimeWarp.CurrentRate),
                recId: recordingId);
        }

        /// <summary>
        /// OBSERVABILITY (descent render-window tracing): emit one line per RENDERED polyline object - every
        /// cached leg AND forward-arc whose Vectrosity <c>VectorLine</c> is live this frame (active, or drawn
        /// this frame). These VectorLines are NOT in <see cref="GhostMapPresence.ghostMapVesselPids"/>, so the
        /// per-pid <see cref="MapRenderProbe"/> never sees them - they are exactly the "rendered but not logged"
        /// extra trajectory lines the per-frame map-scene snapshot exists to catch. <c>staleActive</c> flags a
        /// VectorLine still <c>active</c> but NOT drawn this frame (a one-shot Draw3D mesh that the deactivation
        /// sweep has not yet hidden - a candidate stray line). Walks both static caches; Unity-coupled (reads
        /// <c>VectorLine.active</c>), so it is driven only from the live probe, never xUnit. Each line is handed
        /// to <paramref name="emit"/>; the caller owns the surface/prefix.
        /// </summary>
        internal static void EmitRenderedPolylineSnapshot(int currentFrame, System.Action<string> emit)
        {
            if (emit == null) return;
            var ic = System.Globalization.CultureInfo.InvariantCulture;

            // FRAME-LAG NOTE: this runs from MapRenderProbe.LateUpdate, but Draw3D + the per-frame deactivation
            // sweep (RunDeactivationSweep) run LATER, in OnMapCameraPreCull. So a leg redrawn EVERY frame is
            // stamped lastDrawnFrame == currentFrame-1 when read here (the prior frame's preCull), never
            // currentFrame. "drawnRecently" therefore means lastDrawnFrame >= currentFrame-1 (the steady-state
            // baseline for a continuously-drawn leg), and staleActive flags only a GENUINELY lingering mesh:
            // active but NOT redrawn for 2+ frames (lastDrawnFrame < currentFrame-1) - the case the sweep should
            // have hidden. (The earlier `== currentFrame` form flagged every redrawn leg as stale: 3825 false
            // positives in the 2026-06-21 capture.)
            foreach (var kv in polylineCache)
            {
                LegPolyline[] legs = kv.Value.legs;
                if (legs == null) continue;
                for (int l = 0; l < legs.Length; l++)
                {
                    LegPolyline leg = legs[l];
                    bool hasLine = leg.vectorLine != null;
                    bool active = hasLine && leg.vectorLine.active;
                    bool drawnRecently = leg.lastDrawnFrame >= currentFrame - 1;
                    if (!active && !drawnRecently) continue; // not rendered recently
                    emit(string.Format(ic,
                        "polyline-leg rec={0} leg={1}/{2} body={3} ut=[{4}-{5}] active={6} drawnRecently={7} "
                        + "staleActive={8} pts={9} lastDrawnFrame={10}",
                        kv.Key, l, legs.Length, leg.bodyName ?? "(null)",
                        leg.startUT.ToString("F1", ic), leg.endUT.ToString("F1", ic),
                        active, drawnRecently, active && !drawnRecently, leg.PointCount, leg.lastDrawnFrame));
                }
            }

            foreach (var kv in forwardArcCache)
            {
                ForwardArc[] arcs = kv.Value.arcs;
                if (arcs == null) continue;
                for (int a = 0; a < arcs.Length; a++)
                {
                    ForwardArc arc = arcs[a];
                    bool hasLine = arc.vectorLine != null;
                    bool active = hasLine && arc.vectorLine.active;
                    bool drawnRecently = arc.lastDrawnFrame >= currentFrame - 1;
                    if (!active && !drawnRecently) continue; // not rendered recently
                    emit(string.Format(ic,
                        "forward-arc rec={0} arc={1}/{2} body={3} ut=[{4}-{5}] active={6} drawnRecently={7} "
                        + "staleActive={8} lastDrawnFrame={9}",
                        kv.Key, a, arcs.Length, arc.bodyName ?? "(null)",
                        arc.startUT.ToString("F1", ic), arc.endUT.ToString("F1", ic),
                        active, drawnRecently, active && !drawnRecently, arc.lastDrawnFrame));
                }
            }
        }

        /// <summary>
        /// A1 cutover-instrument (design §14, ParityMode.Synthesized polyline leg): for every cached leg
        /// whose Vectrosity <c>VectorLine</c> is RENDERED this frame, hand the caller two flat
        /// <c>[x0,y0,z0, x1,...]</c> XYZ-triple arrays in ONE consistent BODY-RELATIVE WORLD metres frame:
        /// the RENDERED polyline geometry (the leg's live <c>VectorLine.points3</c>, each scaled-space
        /// vertex inverted back to a body-relative world offset by
        /// <see cref="Parsek.MapRender.RenderGeometrySampler.RenderedScaledVertexToBodyRelative"/>) and the
        /// RECORDED reference geometry - plus the leg's float-quantization METROLOGY FLOOR in metres (the
        /// last callback arg; see <see cref="Parsek.MapRender.RenderGeometrySampler.DrawnVertexQuantizationFloorMeters"/>)
        /// the caller clamps its tolerance up to.
        ///
        /// <para><b>FRAME-COHERENT capture (the false-drift fix, take 2).</b> BOTH sides come from the
        /// DRAW's own frame: <see cref="TryDrawLeg"/> snapshots the body-relative offsets it builds the
        /// vertices from (<see cref="LegPolyline.parityRecordedRel"/>) plus the exact centre float it added
        /// in (<see cref="LegPolyline.parityDrawCentreScaled"/>), tracing-gated; this capture inverts
        /// <c>points3</c> against that RECORDED centre and diffs against that RECORDED snapshot. It never
        /// re-reads <c>GetWorldSurfacePosition</c> / <c>body.position</c> / <c>scaledXform.position</c> at
        /// capture time: the Driver draws in a different part of the frame than the probe (exec order -50
        /// vs +10000, with ScaledSpace's own update in between), so a capture-time re-read skews the
        /// reference by one frame of body translation AND rotation, scaled by warp - the live FP signature
        /// this replaced (a ~280 m 1x floor = one frame of the body's heliocentric motion, with 16-24 km
        /// spikes under warp; the FIRST capture inverse additionally strobed through
        /// <c>ScaledSpace.totalOffset</c>). Subtracting the identical recorded centre float also cancels
        /// the vertex sum's quantization of the centre exactly. A leg whose snapshot is missing or from a
        /// different draw than its <c>points3</c> (<see cref="LegPolyline.parityDrawFrame"/> !=
        /// <see cref="LegPolyline.lastDrawnFrame"/>: the absolute-fallback draw branch, or a leg not yet
        /// redrawn since tracing came on) is SKIPPED this frame (batch-counted, rate-limited summary) and
        /// captured on its next traced draw.</para>
        ///
        /// <para>CONIC-ANCHORED legs are SKIPPED (a known, stated limitation). A leg the draw rotated ~96 deg
        /// onto the bracketing conic seam (<see cref="TryAnchorLegToConicSeam"/> returned true, recorded in
        /// <see cref="LegPolyline.wasAnchored"/>) draws its <c>points3</c> INTENTIONALLY far from its raw
        /// recorded surface track, so diffing rendered-<c>points3</c> against the raw <c>lats/lons/alts</c>
        /// would report ~body-radius drift on EVERY correctly-anchored leg (a false positive) while a leg
        /// that FAILED to anchor would read ~0 - exactly backwards. The anchor transform is a per-point Slerp
        /// of two live-computed <c>FromToRotation</c> quaternions, not a cheaply recoverable single transform
        /// to rebuild the reference in, so this lens deliberately covers ONLY non-anchored body-fixed legs
        /// (descent / atmospheric / surface - the descent re-stitch the audit cares about). For those the
        /// rendered <c>points3</c> ARE the raw body-fixed points, so rendered == recorded by construction and
        /// any genuine mis-draw drifts. The two arrays use the SAME index set (the first
        /// <c>min(points3.Count, leg.PointCount)</c> samples), so the diff is a like-for-like recorded-vs-
        /// rendered comparison even though the oracle itself is count-independent.</para>
        ///
        /// <para>Unity-coupled (reads <c>VectorLine.points3</c> + <c>ScaledSpace.ScaleFactor</c>; the
        /// reference and centre come from the draw-frame snapshot, no capture-time body reads), so it is
        /// driven ONLY from the live <see cref="MapRenderProbe"/> (never xUnit) and only while tracing is
        /// on. NaN/Inf-safe: a non-finite vertex or snapshot value is written through unchanged (the oracle
        /// filters non-finite points and never raises a false anomaly). Each drawn leg is handed to
        /// <paramref name="onLeg"/> as
        /// <c>(recordingId, legIndex, legCount, bodyName, renderedBodyRelFlat, recordedBodyRelFlat,
        /// quantizationFloorMeters)</c>; the caller owns the oracle diff + emit so the decision math stays
        /// pure/unit-testable.</para>
        /// </summary>
        internal static void CaptureRenderedVsRecordedLegGeometry(
            int currentFrame,
            System.Action<string, int, int, string, double[], double[], double> onLeg)
        {
            if (onLeg == null) return;

            int skippedNoSnapshot = 0;
            foreach (var kv in polylineCache)
            {
                LegPolyline[] legs = kv.Value.legs;
                if (legs == null) continue;
                for (int l = 0; l < legs.Length; l++)
                {
                    LegPolyline leg = legs[l];
                    VectorLine line = leg.vectorLine;
                    if (line == null) continue;
                    bool active = line.active;
                    bool drawnRecently = leg.lastDrawnFrame >= currentFrame - 1;
                    if (!active && !drawnRecently) continue; // not rendered recently

                    // SKIP conic-anchored legs: a leg the last draw rotated onto the bracketing conic seam
                    // (TryAnchorLegToConicSeam returned true, stamped here) draws its points3 ~96 deg off the
                    // RAW recorded surface track on PURPOSE, so diffing rendered-points3 against the raw
                    // recorded snapshot would report ~body-radius drift on EVERY correctly-anchored leg (false
                    // positive) - and a leg that FAILED to anchor (the real regression) reads ~0 (exactly
                    // backwards). The anchor rotation is a per-point Slerp of two live-computed FromToRotation
                    // quaternions, not a cheaply recoverable single transform, so this lens does NOT cover
                    // anchored vacuum-maneuver legs - a STATED limitation. It validates ONLY non-anchored
                    // body-fixed legs (descent / atmospheric / surface - the descent re-stitch the audit cares
                    // about). The skip decision is the pure ShouldCaptureLegForParity (xUnit-covered).
                    if (!ShouldCaptureLegForParity(leg.wasAnchored)) continue;

                    var points3 = line.points3;
                    if (points3 == null || points3.Count == 0) continue;

                    // FRAME-COHERENT reference: the diff runs against the parity snapshot the DRAW itself
                    // took (leg.parityRecordedRel + parityDrawCentreScaled, filled inside the vertex-build
                    // loop while tracing is on), NEVER a capture-time re-read. The draw and this probe run
                    // in different parts of the frame: re-evaluating GetWorldSurfacePosition/body.position/
                    // scaledXform.position here skews the reference by a frame of body translation +
                    // rotation (x warp) - the pre-fix ~280m@1x floor and 16-24km warp spikes were exactly
                    // that artifact. parityDrawFrame must EQUAL lastDrawnFrame so the snapshot and points3
                    // came from the SAME draw (a leg drawn via the absolute fallback, or tracing enabled
                    // mid-flight before this leg redrew, is skipped this frame and captured on its next
                    // draw). Subtracting the recorded centre float also cancels the vertex-sum quantization
                    // of the centre exactly.
                    if (leg.parityRecordedRel == null
                        || leg.parityDrawFrame != leg.lastDrawnFrame
                        || leg.parityRecordedRel.Length < 3)
                    {
                        skippedNoSnapshot++;
                        continue;
                    }

                    // Like-for-like index set: points3 hold this leg's drawn samples 1:1 with the snapshot
                    // (both written from the same loop). Diff over the common prefix so a transiently-
                    // resized line never misaligns.
                    int m = leg.PointCount;
                    if (m > points3.Count) m = points3.Count;
                    if (m > leg.parityRecordedRel.Length / 3) m = leg.parityRecordedRel.Length / 3;
                    if (m <= 0) continue;

                    Vector3d bodyCentreScaled = (Vector3d)leg.parityDrawCentreScaled;
                    double scaleFactor = ScaledSpace.ScaleFactor;
                    var renderedFlat = new double[m * 3];
                    var recordedFlat = new double[m * 3];
                    for (int i = 0; i < m; i++)
                    {
                        // RENDERED: the drawn scaled-space vertex float -> body-relative world offset,
                        // inverted against the SAME centre float the draw added in (cancels the centre,
                        // totalOffset, and the centre's own float quantization outright).
                        Vector3d renderedRel =
                            Parsek.MapRender.RenderGeometrySampler.RenderedScaledVertexToBodyRelative(
                                points3[i], bodyCentreScaled, scaleFactor);
                        renderedFlat[i * 3] = renderedRel.x;
                        renderedFlat[i * 3 + 1] = renderedRel.y;
                        renderedFlat[i * 3 + 2] = renderedRel.z;

                        // RECORDED reference: the draw-frame snapshot of the leg's body-fixed surface
                        // track, body-relative (what the draw ACTUALLY encoded into the vertices).
                        recordedFlat[i * 3] = leg.parityRecordedRel[i * 3];
                        recordedFlat[i * 3 + 1] = leg.parityRecordedRel[i * 3 + 1];
                        recordedFlat[i * 3 + 2] = leg.parityRecordedRel[i * 3 + 2];
                    }

                    // METROLOGY FLOOR: the drawn Vector3 vertex quantizes at the CENTRE's float ulp (x
                    // scaleFactor in real metres) - on a body far from the scaled origin (a non-focused
                    // body in the TS) that noise can exceed a small leg's 0.1% scale tolerance. The oracle
                    // clamps its tolerance up to this floor: sub-floor deviations are float noise, not
                    // geometry; a real mis-draw above the floor still fires.
                    double noiseFloor =
                        Parsek.MapRender.RenderGeometrySampler.DrawnVertexQuantizationFloorMeters(
                            bodyCentreScaled, scaleFactor);

                    onLeg(kv.Key, l, legs.Length, leg.bodyName ?? "(null)", renderedFlat, recordedFlat,
                        noiseFloor);
                }
            }

            // Batch summary (convention: one line after the loop, never per-item). Rate-limited: this runs
            // per frame while tracing is on; a sustained skip usually just means legs drawn before tracing
            // came on.
            if (skippedNoSnapshot > 0)
                ParsekLog.VerboseRateLimited(Tag, "parity-capture-skip",
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Leg parity capture skipped {0} leg(s) with no same-draw snapshot (absolute-fallback "
                        + "draw, or drawn before tracing enabled; captured on their next traced draw)",
                        skippedNoSnapshot),
                    10.0);
        }

        /// <summary>
        /// PURE decision (A1 polyline-leg parity lens): should this leg be included in the rendered-vs-
        /// recorded leg-geometry parity capture (<see cref="CaptureRenderedVsRecordedLegGeometry"/>)? A
        /// CONIC-ANCHORED leg (<see cref="LegPolyline.wasAnchored"/>: the draw rotated its points ~96 deg onto
        /// the bracketing conic seam) is EXCLUDED - its rendered points3 are INTENTIONALLY far from the raw
        /// recorded surface track, so a rendered-vs-raw-recorded diff would false-fire on a CORRECT anchor
        /// (and read ~0 on a leg that FAILED to anchor - exactly backwards). A NON-anchored body-fixed leg
        /// (descent / atmospheric / surface) draws the raw body-fixed points, so rendered == recorded by
        /// construction and a genuine mis-draw drifts -> INCLUDED. The anchor's per-point Slerp is not cheaply
        /// recoverable to rebuild the reference in, so anchored vacuum-maneuver legs are a STATED, documented
        /// lens limitation. xUnit-testable without Unity.
        /// </summary>
        internal static bool ShouldCaptureLegForParity(bool legWasConicAnchored)
            => !legWasConicAnchored;
    }
}
