using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// How a no-op switch-segment discard would resolve, used by the scene-exit
    /// hook to pick a teardown path. Computed live-side from the segment tree /
    /// restore-attempt state; the pure classifier does not produce it.
    /// </summary>
    internal enum SwitchSegmentDisposition
    {
        /// <summary>Not classified / not applicable.</summary>
        None = 0,

        /// <summary>The segment subtree IS the whole live active tree (a fresh
        /// standalone tree from a Fly / Switch-To to a vessel unrelated to any
        /// Parsek tree). Safe to drop the whole active tree — identical to the
        /// idle-on-pad whole-tree teardown.</summary>
        Standalone = 1,

        /// <summary>The segment was appended to a restored clone of an
        /// already-committed tree. The committed original is untouched in the
        /// committed store; the clone wrapper can be dropped. Scene-exit handling
        /// is DEFERRED (covered by the in-flight re-switch hook).</summary>
        CommittedRestoreClone = 2,

        /// <summary>The segment continues a BG-tracked member of a still-live
        /// active tree that has other content beyond the segment. Scene-exit
        /// handling is DEFERRED (the rest of the tree must still commit).</summary>
        BgMemberOrMixed = 3,
    }

    /// <summary>
    /// Pure predicate: did a resumed (Tracking-Station Fly / KSC marker Fly / map
    /// Switch-To) recording segment change anything meaningful, or is it a no-op
    /// that can be auto-discarded so we do not prolong the ghost state for a
    /// segment that did nothing?
    ///
    /// <para>"Meaningful" (segment KEPT) means any of: a structural / geometry /
    /// dock / undock / deploy / parachute / robotic / inventory part event, or an
    /// engine / RCS event at positive throttle (see <see cref="IsMeaningfulPartEvent"/>);
    /// any segment event other than the recording-discontinuity
    /// <see cref="SegmentEventType.TimeJump"/> marker (controller change, part
    /// add/remove/destroy, crew loss — see the gate's note: this is a defensive
    /// surface, segment events are not flushed at the live decision point and
    /// nothing meaningful is missed because those events ride flushed part
    /// events); a flag plant; a dock target; any non-boring track section
    /// (atmospheric flight, exo-propulsive thrust, surface motion / rover driving,
    /// or airless-body approach — see <see cref="GhostPlaybackLogic.IsBoringEnvironment"/>);
    /// or a change in orbit elements between the segment's first and last orbital
    /// samples.</para>
    ///
    /// <para>Conservative bias: when in doubt, KEEP (return non-null
    /// <c>keepReason</c>). The decision reuses already-recorded data only — there
    /// is no event for an intra-vessel fuel transfer and the vessel resource
    /// totals do not change on one, so a coasting segment whose only activity was
    /// a tank-to-tank transfer is NOT detectable here and would be discarded; see
    /// the plan's Known Limitation.</para>
    ///
    /// <para>EC drain, light / thermal-animation toggles, and pure time-warp
    /// coasting are deliberately NOT meaningful.</para>
    /// </summary>
    internal static class SwitchSegmentNoOpClassifier
    {
        /// <summary>Fractional tolerance on semi-major-axis change (0.1%).</summary>
        internal const double OrbitSemiMajorAxisToleranceFraction = 1e-3;

        /// <summary>Absolute tolerance on eccentricity change.</summary>
        internal const double OrbitEccentricityToleranceAbs = 1e-3;

        /// <summary>Tolerance on inclination / LAN / argument-of-periapsis change (degrees).</summary>
        internal const double OrbitAngleToleranceDeg = 0.1;

        private struct OrbitSample
        {
            public double inc, ecc, sma, lan, aop;
            public string body;
        }

        /// <summary>
        /// Returns true when <paramref name="segment"/> is a no-op (safe to
        /// auto-discard). On a false return, <paramref name="keepReason"/> names
        /// the first gate that decided to keep the segment (for logging).
        /// </summary>
        /// <param name="segment">The resumed segment recording (already flushed
        /// from the live recorder by the caller).</param>
        /// <param name="segmentHasDescendants">True when the segment spawned
        /// descendant recordings (dock / undock / EVA / decouple / breakup
        /// children) — computed live-side from the tree topology.</param>
        internal static bool IsNoOpSegment(
            Recording segment, bool segmentHasDescendants, out string keepReason)
        {
            keepReason = null;

            if (segment == null)
            {
                keepReason = "segment-null";
                return false;
            }

            // A sealed destruction terminal means the vessel was destroyed during
            // the segment — a world-state change worth keeping. (Also caught by the
            // Destroyed part event below, but the sealed flag is the cheapest gate.)
            if (segment.VesselDestroyed)
            {
                keepReason = "vessel-destroyed";
                return false;
            }

            // Descendants (debris / EVA / dock children) → something happened.
            if (segmentHasDescendants)
            {
                keepReason = "has-descendants";
                return false;
            }

            // Meaningful part events (see IsMeaningfulPartEvent): structural /
            // geometry events always count; engine / RCS events count only when
            // actively firing (positive throttle). Cosmetic light / thermal
            // events and engine/RCS "off" transitions (shutdown / stop / zero
            // throttle, including the zero-throttle resume seeds the recorder
            // emits) do NOT count — a real burn is independently caught by the
            // ExoPropulsive track section + the positive-throttle event.
            if (segment.PartEvents != null)
            {
                for (int i = 0; i < segment.PartEvents.Count; i++)
                {
                    PartEvent evt = segment.PartEvents[i];
                    if (IsMeaningfulPartEvent(evt))
                    {
                        keepReason = "part-event:" + evt.eventType;
                        return false;
                    }
                }
            }

            // Any segment event except the TimeJump discontinuity marker counts:
            // CrewLost, Controller*, Part{Added,Removed,Destroyed}, CrewTransfer.
            // (Do NOT reuse IsGhostingSegmentEvent here — it returns false for
            // CrewTransfer and the crew/controller types, which this gate keeps.)
            //
            // NOTE: this gate is a forward-looking / defensive surface, not a
            // live keep-path today. The scene-exit and re-switch flush
            // (FlushRecorderIntoActiveTreeForSerialization) does NOT move
            // recorder.SegmentEvents into the tree recording, so at the live
            // decision point this list is empty. In practice nothing is missed:
            // breakage / part-destruction segment events are accompanied by a
            // flushed Decoupled / Destroyed PartEvent (caught above), and a pure
            // intra-vessel crew transfer is not recorded at all (no
            // onCrewTransferred surface) — documented as a known limitation
            // alongside fuel transfer, same as any internal-state change with no
            // visual ghost representation.
            if (segment.SegmentEvents != null)
            {
                for (int i = 0; i < segment.SegmentEvents.Count; i++)
                {
                    SegmentEventType t = segment.SegmentEvents[i].type;
                    if (t != SegmentEventType.TimeJump)
                    {
                        keepReason = "segment-event:" + t;
                        return false;
                    }
                }
            }

            // A flag was planted.
            if (segment.FlagEvents != null && segment.FlagEvents.Count > 0)
            {
                keepReason = "flag-event";
                return false;
            }

            // A docking happened (redundant with the Docked part event + dock
            // child branch point, kept as a cheap defense).
            if (segment.DockTargetVesselPid != 0)
            {
                keepReason = "dock-target";
                return false;
            }

            // Data-loss safeguard (mirrors IsActiveTreeIdleOnPad's Bug #290d
            // "0 points = data-loss, not idle" guard): if the segment carries no
            // trajectory payload at all — no usable points, no sections, no orbit
            // segments — we cannot confirm it is genuinely empty (a sidecar that
            // failed to load looks the same as a switch that recorded nothing), so
            // KEEP rather than risk discarding a recording whose data simply did
            // not load. A genuine no-op coast / sit always has >= 2 points and a
            // boring section, so this only spares the near-empty sub-second case
            // (whose ghost cost is negligible anyway).
            int pointCount = segment.Points?.Count ?? 0;
            int sectionCount = segment.TrackSections?.Count ?? 0;
            int orbitSegmentCount = segment.OrbitSegments?.Count ?? 0;
            if (pointCount < 2 && sectionCount == 0 && orbitSegmentCount == 0)
            {
                keepReason = "insufficient-data";
                return false;
            }

            // Every track section must be "boring": ExoBallistic (coasting in
            // space) or SurfaceStationary (sitting still). Any Atmospheric,
            // ExoPropulsive (thrust), SurfaceMobile (rover driving), or Approach
            // (airless-body descent) section means the vessel actually did
            // something. (An empty section list is vacuously boring; the orbit
            // gate below and the no-event gates above carry the decision then.)
            if (segment.TrackSections != null)
            {
                for (int i = 0; i < segment.TrackSections.Count; i++)
                {
                    SegmentEnvironment env = segment.TrackSections[i].environment;
                    if (!GhostPlaybackLogic.IsBoringEnvironment(env))
                    {
                        keepReason = "non-boring-section:" + env;
                        return false;
                    }
                }
            }

            // Defense (load-bearing only when sections are sparse / missing but
            // orbital checkpoints exist): orbit elements must not have changed
            // between the segment's first and last orbital samples.
            if (!OrbitElementsUnchanged(segment, out string orbitReason))
            {
                keepReason = orbitReason;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Compares the first and last orbital samples drawn (in list order) from
        /// the segment's <see cref="Recording.OrbitSegments"/> and any
        /// <see cref="ReferenceFrame.OrbitalCheckpoint"/> track-section checkpoints.
        /// Returns true (unchanged) when there are fewer than two samples (nothing
        /// to compare — the boring-section gate carries the decision). A body
        /// change between first and last is treated as "changed" (an SOI crossing
        /// is a non-trivial trajectory event and the elements live in different
        /// frames).
        /// </summary>
        internal static bool OrbitElementsUnchanged(Recording segment, out string reason)
        {
            reason = null;

            // Collect every orbital sample with its UT, then compare the
            // earliest vs the latest by UT. OrbitSegments and OrbitalCheckpoint
            // checkpoints are gathered from two sources and concatenated, so
            // iteration order is NOT guaranteed to be UT-ascending — sort by UT
            // (startUT) before picking first/last so a changed orbit cannot
            // slip past via an out-of-order list.
            var samples = new List<(double ut, OrbitSample s)>();
            void Consider(OrbitSegment os)
            {
                samples.Add((os.startUT, new OrbitSample
                {
                    inc = os.inclination,
                    ecc = os.eccentricity,
                    sma = os.semiMajorAxis,
                    lan = os.longitudeOfAscendingNode,
                    aop = os.argumentOfPeriapsis,
                    body = os.bodyName,
                }));
            }

            if (segment.OrbitSegments != null)
            {
                for (int i = 0; i < segment.OrbitSegments.Count; i++)
                    Consider(segment.OrbitSegments[i]);
            }

            if (segment.TrackSections != null)
            {
                for (int i = 0; i < segment.TrackSections.Count; i++)
                {
                    TrackSection sec = segment.TrackSections[i];
                    if (sec.referenceFrame != ReferenceFrame.OrbitalCheckpoint
                        || sec.checkpoints == null)
                        continue;
                    for (int j = 0; j < sec.checkpoints.Count; j++)
                        Consider(sec.checkpoints[j]);
                }
            }

            if (samples.Count == 0)
                return true; // no orbital data — nothing to compare

            samples.Sort((a, b) => a.ut.CompareTo(b.ut));
            OrbitSample first = samples[0].s;
            OrbitSample last = samples[samples.Count - 1].s;

            if (!string.Equals(first.body, last.body, StringComparison.Ordinal))
            {
                reason = "orbit-body-change";
                return false;
            }

            double smaRef = Math.Max(Math.Abs(first.sma), 1.0);
            if (Math.Abs(last.sma - first.sma) / smaRef > OrbitSemiMajorAxisToleranceFraction)
            {
                reason = "orbit-sma-change";
                return false;
            }
            if (Math.Abs(last.ecc - first.ecc) > OrbitEccentricityToleranceAbs)
            {
                reason = "orbit-ecc-change";
                return false;
            }
            if (AngleDeltaDeg(first.inc, last.inc) > OrbitAngleToleranceDeg)
            {
                reason = "orbit-inc-change";
                return false;
            }
            if (AngleDeltaDeg(first.lan, last.lan) > OrbitAngleToleranceDeg)
            {
                reason = "orbit-lan-change";
                return false;
            }
            if (AngleDeltaDeg(first.aop, last.aop) > OrbitAngleToleranceDeg)
            {
                reason = "orbit-aop-change";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Whether a part event represents the player actually doing something to
        /// the vessel (so the segment must be KEPT). Distinct from
        /// <see cref="RecordingOptimizer.IsInertPartEventForTailTrim"/>, which
        /// treats <see cref="PartEventType.EngineShutdown"/> as non-inert (a
        /// shutdown marks end-of-burn for tail-trim) — wrong here: a coasting
        /// resumed segment carries zero-throttle / shutdown engine seeds at its
        /// start UT and those must NOT block a no-op discard.
        ///
        /// <para>Structural / geometry events (decouple, destroy, dock, undock,
        /// parachute, deployables, gear, cargo, fairing, inventory, robotics)
        /// always count. Engine / RCS events count only when actively firing
        /// (positive throttle); their "off" transitions (shutdown / stop / zero
        /// throttle) do not — a genuine burn is independently caught by the
        /// ExoPropulsive (or Atmospheric) track section plus the positive-throttle
        /// event. Cosmetic light / thermal-animation events never count.</para>
        /// </summary>
        internal static bool IsMeaningfulPartEvent(PartEvent evt)
        {
            switch (evt.eventType)
            {
                // Cosmetic only — never meaningful.
                case PartEventType.LightOn:
                case PartEventType.LightOff:
                case PartEventType.LightBlinkEnabled:
                case PartEventType.LightBlinkDisabled:
                case PartEventType.LightBlinkRate:
                case PartEventType.ThermalAnimationHot:
                case PartEventType.ThermalAnimationCold:
                case PartEventType.ThermalAnimationMedium:
                    return false;

                // Engine / RCS "off" transitions — not activity on their own.
                case PartEventType.EngineShutdown:
                case PartEventType.RCSStopped:
                    return false;

                // Engine / RCS firing — meaningful only at positive throttle.
                case PartEventType.EngineIgnited:
                case PartEventType.EngineThrottle:
                case PartEventType.RCSActivated:
                case PartEventType.RCSThrottle:
                    return evt.value > 0f;

                // Everything structural / geometry — meaningful.
                default:
                    return true;
            }
        }

        /// <summary>Smallest absolute difference between two angles (degrees),
        /// accounting for 360-degree wraparound.</summary>
        private static double AngleDeltaDeg(double a, double b)
        {
            double d = Math.Abs(a - b) % 360.0;
            return d > 180.0 ? 360.0 - d : d;
        }
    }
}
