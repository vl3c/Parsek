using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // Mission periodicity (launch-window phase-locked looping). See
    // docs/dev/design-mission-periodicity.md. This is the PURE math heart of the
    // feature: it turns the trimmed member set of a looping Mission (the same
    // MissionLoopUnitBuilder.ComputeTrimmedMemberWindows output the span clock + cadence
    // already use) into the ordered list of phase constraints the included config imposes,
    // and (Phase 1) solves that list for the recurrence period P + the next faithful launch
    // window. No Unity calls, no shared mutable state, no recording mutation: all live-body
    // data is read through the IBodyInfo seam so the whole thing is unit-testable against
    // synthetic bodies.
    //
    // Phase 0 (this commit's scope): ExtractConstraints only.
    // Phase 1 adds Solve.

    /// <summary>The kind of phase requirement an included segment imposes.</summary>
    internal enum ConstraintKind
    {
        /// <summary>A surface/atmospheric segment on a rotating body must place over its
        /// ground spot and connect to its inertial orbit: repeats every body rotation period.</summary>
        Rotation,

        /// <summary>An SOI entry into a body (capture, transient flyby, or gravity assist)
        /// must reach that body where it will be: repeats every body orbit period (direct
        /// child) or the synodic period (sibling/cross-parent, Phase 4).</summary>
        Orbital
    }

    /// <summary>
    /// One phase requirement imposed by one constraining included segment. Derived (computed
    /// each loop-unit build); never persisted. See the design doc Data Model.
    /// </summary>
    internal struct PhaseConstraint
    {
        /// <summary>Rotation (surface segment) or Orbital (SOI entry).</summary>
        public ConstraintKind Kind;

        /// <summary>B (Rotation) or C (Orbital) - the body whose phase this constrains.</summary>
        public string BodyName;

        /// <summary>The recurrence period in seconds: rotation period, orbit period, or
        /// (cross-parent, Phase 4) the synodic period.</summary>
        public double PeriodSeconds;

        /// <summary>The segment's recorded UT minus UT0 - the fixed offset of this
        /// constraint within the recorded mission.</summary>
        public double PhaseOffsetSeconds;

        /// <summary>For Orbital constraints: false = the target orbits the launch body
        /// directly (same-parent, e.g. Mun from Kerbin, recurrence = C.orbit.period);
        /// true = the target is a sibling of the launch body (cross-parent, e.g. Duna,
        /// recurrence = the synodic period - not yet solvable, Phase 4). Always false for
        /// Rotation constraints.</summary>
        public bool RelativeToParent;

        public override string ToString()
        {
            var ic = CultureInfo.InvariantCulture;
            string rel = Kind == ConstraintKind.Orbital
                ? (RelativeToParent ? " cross-parent" : " same-parent")
                : "";
            return $"{Kind}({BodyName ?? "?"}){rel} P={PeriodSeconds.ToString("R", ic)} " +
                   $"off={PhaseOffsetSeconds.ToString("R", ic)}";
        }
    }

    /// <summary>
    /// Whether the extracted constraint set is solvable by the current phase, or a reason it
    /// is not yet. An unsupported config falls back to today's arbitrary-phase looping rather
    /// than being mis-scheduled. See the design doc Data Model.
    /// </summary>
    internal enum Support
    {
        /// <summary>Solvable now (single-body or same-parent intercept).</summary>
        Supported,

        /// <summary>A sibling/interplanetary target (cross-parent). Recurrence is the synodic
        /// period; not solvable until Phase 4.</summary>
        UnsupportedCrossParent,

        /// <summary>The included span aligns to another vessel (rendezvous / dock), not a
        /// celestial body. Out of scope for faithful looping (detected + reported).</summary>
        UnsupportedRendezvous,

        /// <summary>More than one independent constraint with differing periods, which needs
        /// the joint best-fit (Phase 2). Tier 1 / Phase 1 still locks the dominant constraint;
        /// this flag marks "the residual is real and unmodeled until Phase 2."</summary>
        UnsupportedMultiConstraintPreP2
    }

    /// <summary>
    /// The result of extracting the phase constraints for one looping Mission's trimmed config.
    /// Pure value: launch body + UT0 + the ordered constraint list + the Support classification.
    /// </summary>
    internal struct ConstraintExtraction
    {
        /// <summary>The constraints the included segments impose, in recorded-UT order.</summary>
        public List<PhaseConstraint> Constraints;

        /// <summary>Supported, or the reason the set is not yet solvable.</summary>
        public Support Support;

        /// <summary>The launch body: the body of the earliest included surface/atmospheric
        /// segment, else the bodyName of the earliest included OrbitSegment. Null when the
        /// config has no resolvable body.</summary>
        public string LaunchBodyName;

        /// <summary>The recorded launch UT = the trimmed mission's span start (earliest
        /// included member trimmed StartUT). NaN when no members are included.</summary>
        public double UT0;

        /// <summary>For unsupported configs: the body/segment that triggered the flag, for
        /// the diagnostic summary. Null when Supported.</summary>
        public string UnsupportedReason;
    }

    /// <summary>
    /// Test seam over FlightGlobals: the live-body data the extractor + solver read. A
    /// FlightGlobals-backed implementation lives in <see cref="FlightGlobalsBodyInfo"/>; tests
    /// pass a fake with synthetic bodies. Never hardcode stock values - planet packs (RSS, etc.)
    /// change every period, so all of these are read at build time.
    /// </summary>
    internal interface IBodyInfo
    {
        /// <summary>Sidereal rotation period in seconds (0 or NaN = no usable rotation).</summary>
        double RotationPeriod(string bodyName);

        /// <summary>Orbit period in seconds about the body's reference body (0/NaN for the Sun
        /// or a body with no orbit).</summary>
        double OrbitPeriod(string bodyName);

        /// <summary>The parent (reference) body's name, or null for the root (Sun) / unknown.</summary>
        string ReferenceBodyName(string bodyName);

        /// <summary>Sphere-of-influence radius in metres (for the Phase 2 tolerance formula).</summary>
        double SoiRadius(string bodyName);

        /// <summary>Approximate orbital velocity in m/s (for the Phase 2 tolerance formula).</summary>
        double OrbitalVelocity(string bodyName);
    }

    internal static class MissionPeriodicity
    {
        // Set true in tests to silence the per-extraction / per-solve Verbose summaries.
        internal static bool SuppressLogging;

        // Environments that constrain a body's ROTATION phase (a surface/atmospheric segment
        // must sit over its ground spot + connect to its inertial orbit). Approach is an
        // airless-body low pass that is still surface-rotation-coupled.
        private static bool IsRotationConstrainingEnvironment(SegmentEnvironment env)
        {
            return env == SegmentEnvironment.Atmospheric
                || env == SegmentEnvironment.SurfaceMobile
                || env == SegmentEnvironment.SurfaceStationary
                || env == SegmentEnvironment.Approach;
        }

        /// <summary>
        /// Extracts the ordered phase-constraint list for one looping Mission's TRIMMED config.
        /// Pure. <paramref name="view"/> / <paramref name="compRoots"/> are the through-line +
        /// composition read models (built once by the caller); <paramref name="committed"/> is
        /// the committed recording list (member indices index into it);
        /// <paramref name="excludedIntervalKeys"/> is the Mission's interval-level trim;
        /// <paramref name="bodyInfo"/> is the live-body seam.
        ///
        /// Rules (design doc "What determines P" + Edge cases):
        /// 1. launch body = body of the earliest INCLUDED surface/atmospheric segment, else the
        ///    bodyName of the earliest included OrbitSegment.
        /// 2. a surface/atmospheric segment on body B -> Rotation(B), phase offset = segment
        ///    start UT - UT0. Only ONE Rotation(B) per body is emitted (the earliest), because
        ///    rotation inheritance is a property of the included SET (multiple same-body surface
        ///    legs collapse to that body's single rotation lock); a SECOND distinct surface body
        ///    or a second incompatible same-body offset is what makes the config over-constrained
        ///    (Phase 2). To keep Phase 0/1 honest we still emit the earliest per body once.
        /// 3. an inertial orbit segment contributes NO new constraint (rule 2 already covers B if
        ///    any included surface segment of B exists; a bare inertial orbit is free).
        /// 4. an SOI entry into body C (any bodyName across the included OrbitSegments /
        ///    OrbitalCheckpoint checkpoints other than the launch body) -> Orbital(C): direct
        ///    child (C.referenceBody == launchBody) keeps period = C.OrbitPeriod and is Supported;
        ///    sibling (C.referenceBody == launchBody.referenceBody) or deeper is
        ///    UnsupportedCrossParent (Phase 4).
        /// </summary>
        internal static ConstraintExtraction ExtractConstraints(
            MissionThroughLineView view,
            List<MissionCompositionNode> compRoots,
            IReadOnlyList<Recording> committed,
            ICollection<string> excludedIntervalKeys,
            IBodyInfo bodyInfo)
        {
            var result = new ConstraintExtraction
            {
                Constraints = new List<PhaseConstraint>(),
                Support = Support.Supported,
                LaunchBodyName = null,
                UT0 = double.NaN,
                UnsupportedReason = null
            };

            if (committed == null || bodyInfo == null)
            {
                LogSummary("m=?", result, 0, "no committed/bodyInfo");
                return result;
            }

            // 1. The trimmed member set (committed index -> [StartUT, EndUT]) - the SAME source
            //    of truth the span + cadence use. UT0 = the trimmed span start.
            Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow> memberWindows =
                MissionLoopUnitBuilder.ComputeTrimmedMemberWindows(
                    view, compRoots, committed, excludedIntervalKeys, null,
                    out _, out _);
            if (memberWindows.Count == 0)
            {
                LogSummary("m=?", result, 0, "no included members");
                return result;
            }

            double ut0 = double.PositiveInfinity;
            foreach (var w in memberWindows.Values)
                if (w.StartUT < ut0)
                    ut0 = w.StartUT;
            result.UT0 = ut0;

            // 2. Earliest included surface/atmospheric segment (across all members) -> the launch
            //    body + its rotation constraint. Track the earliest surface segment START per body
            //    (rule 2) and the global earliest as the launch-body fallback chain.
            //    Also collect the earliest included OrbitSegment START per body for the SOI rules.
            var earliestSurfaceStartByBody = new Dictionary<string, double>();
            double earliestSurfaceStartGlobal = double.PositiveInfinity;
            string earliestSurfaceBody = null;

            var earliestOrbitStartByBody = new Dictionary<string, double>();
            double earliestOrbitStartGlobal = double.PositiveInfinity;
            string earliestOrbitBody = null;

            bool sawRendezvous = false;
            string rendezvousReason = null;

            foreach (var kv in memberWindows)
            {
                int idx = kv.Key;
                if (idx < 0 || idx >= committed.Count)
                    continue;
                Recording rec = committed[idx];
                if (rec == null)
                    continue;
                GhostPlaybackLogic.LoopUnit.MemberWindow win = kv.Value;

                // Rendezvous / dock detection: a Relative TrackSection inside the included window
                // aligns this member to ANOTHER vessel, not a body. The solver only models bodies,
                // so flag it (detected + reported, not solved - design doc Edge cases).
                if (!sawRendezvous && HasRendezvousWithinWindow(rec, win, out string rdReason))
                {
                    sawRendezvous = true;
                    rendezvousReason = rdReason;
                }

                // Surface/atmospheric segments within the trimmed window -> per-body rotation source.
                ScanSurfaceSegmentsWithinWindow(
                    rec, win, earliestSurfaceStartByBody,
                    ref earliestSurfaceStartGlobal, ref earliestSurfaceBody);

                // OrbitSegments (flat cache) + OrbitalCheckpoint checkpoints within the trimmed
                // window -> per-body orbit-start source for the SOI rules. Scanning both surfaces
                // catches a backgrounded / checkpoint-bridged SOI change (CLAUDE.md note).
                ScanOrbitSegmentsWithinWindow(
                    rec, win, earliestOrbitStartByBody,
                    ref earliestOrbitStartGlobal, ref earliestOrbitBody);
            }

            // Launch body: earliest surface body, else earliest orbit body.
            string launchBody = earliestSurfaceBody ?? earliestOrbitBody;
            result.LaunchBodyName = launchBody;

            // 3. Rotation constraints: one per surface body (earliest start), launch body first.
            //    Emit in recorded-UT order (offset order).
            var rotationBodiesSorted = new List<KeyValuePair<string, double>>(earliestSurfaceStartByBody);
            rotationBodiesSorted.Sort((a, b) =>
            {
                int cmp = a.Value.CompareTo(b.Value);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.Key, b.Key);
            });
            foreach (var rb in rotationBodiesSorted)
            {
                double rotPeriod = bodyInfo.RotationPeriod(rb.Key);
                // Guard zero / NaN / negative (a non-rotating or degenerate body imposes no
                // rotation constraint - design doc Edge cases). Retrograde rotation has a negative
                // period in some data sources; use its magnitude.
                if (double.IsNaN(rotPeriod) || double.IsInfinity(rotPeriod) || rotPeriod == 0.0)
                    continue;
                if (rotPeriod < 0.0)
                    rotPeriod = -rotPeriod;
                result.Constraints.Add(new PhaseConstraint
                {
                    Kind = ConstraintKind.Rotation,
                    BodyName = rb.Key,
                    PeriodSeconds = rotPeriod,
                    PhaseOffsetSeconds = rb.Value - ut0,
                    RelativeToParent = false
                });
            }

            // 4. Orbital constraints: every included orbit body OTHER than the launch body is an
            //    SOI entry. Emit in recorded-UT (orbit-start) order.
            var orbitBodiesSorted = new List<KeyValuePair<string, double>>(earliestOrbitStartByBody);
            orbitBodiesSorted.Sort((a, b) =>
            {
                int cmp = a.Value.CompareTo(b.Value);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.Key, b.Key);
            });
            foreach (var ob in orbitBodiesSorted)
            {
                string body = ob.Key;
                if (string.IsNullOrEmpty(body))
                    continue;
                if (launchBody != null && body == launchBody)
                    continue; // the launch body's own orbit segments are not an SOI entry

                double orbitPeriod = bodyInfo.OrbitPeriod(body);
                bool crossParent = !IsSameParentTarget(body, launchBody, bodyInfo);

                if (double.IsNaN(orbitPeriod) || double.IsInfinity(orbitPeriod) || orbitPeriod <= 0.0)
                {
                    // Degenerate orbit data: skip the constraint rather than emit a divide-by-zero
                    // period (the body is still recorded as transited, but we cannot schedule it).
                    continue;
                }

                result.Constraints.Add(new PhaseConstraint
                {
                    Kind = ConstraintKind.Orbital,
                    BodyName = body,
                    PeriodSeconds = orbitPeriod,
                    PhaseOffsetSeconds = ob.Value - ut0,
                    RelativeToParent = crossParent
                });

                if (crossParent && result.Support == Support.Supported)
                {
                    result.Support = Support.UnsupportedCrossParent;
                    result.UnsupportedReason =
                        $"orbital target '{body}' is not a direct child of launch body " +
                        $"'{launchBody ?? "?"}' (synodic, Phase 4)";
                }
            }

            // Rendezvous outranks cross-parent for the report (it is never solvable, even in
            // Phase 4), so set it last if detected.
            if (sawRendezvous)
            {
                result.Support = Support.UnsupportedRendezvous;
                result.UnsupportedReason = rendezvousReason
                    ?? "included span aligns to another vessel (rendezvous/dock)";
            }

            LogSummary(
                BuildMissionTag(view),
                result,
                memberWindows.Count,
                null);
            return result;
        }

        /// <summary>
        /// Scans a recording's surface/atmospheric TrackSections that intersect the trimmed
        /// window, recording the earliest start UT per body. The section's body is resolved from
        /// its frames (per-frame bodyName), else an overlapping OrbitSegment, else the
        /// recording-level body fields.
        /// </summary>
        private static void ScanSurfaceSegmentsWithinWindow(
            Recording rec,
            GhostPlaybackLogic.LoopUnit.MemberWindow win,
            Dictionary<string, double> earliestSurfaceStartByBody,
            ref double earliestSurfaceStartGlobal,
            ref string earliestSurfaceBody)
        {
            if (rec.TrackSections == null)
                return;
            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                TrackSection sec = rec.TrackSections[i];
                if (!IsRotationConstrainingEnvironment(sec.environment))
                    continue;
                // Intersect the section against the trimmed window; a section entirely outside
                // the window is trimmed off and imposes no constraint.
                double segStart = Math.Max(sec.startUT, win.StartUT);
                double segEnd = Math.Min(sec.endUT, win.EndUT);
                if (segEnd <= segStart)
                    continue;
                string body = ResolveSectionBody(rec, sec);
                if (string.IsNullOrEmpty(body))
                    continue;
                RecordEarliest(
                    earliestSurfaceStartByBody, body, segStart,
                    ref earliestSurfaceStartGlobal, ref earliestSurfaceBody);
            }
        }

        /// <summary>
        /// Scans a recording's orbit segments (flat OrbitSegments cache + OrbitalCheckpoint
        /// section checkpoints) that intersect the trimmed window, recording the earliest start
        /// UT per body. Scanning both surfaces catches a backgrounded / checkpoint-bridged SOI
        /// change (per CLAUDE.md: an on-rails SOI handoff surfaces as a bodyName change in the
        /// checkpoint-wrapped segments, not per-frame TrackSections). Predicted (extrapolated)
        /// segments are excluded - a predicted ballistic tail is not a recorded intercept.
        /// </summary>
        private static void ScanOrbitSegmentsWithinWindow(
            Recording rec,
            GhostPlaybackLogic.LoopUnit.MemberWindow win,
            Dictionary<string, double> earliestOrbitStartByBody,
            ref double earliestOrbitStartGlobal,
            ref string earliestOrbitBody)
        {
            if (rec.OrbitSegments != null)
            {
                for (int i = 0; i < rec.OrbitSegments.Count; i++)
                {
                    OrbitSegment seg = rec.OrbitSegments[i];
                    if (seg.isPredicted)
                        continue;
                    AccumulateOrbitSegmentBody(
                        seg, win, earliestOrbitStartByBody,
                        ref earliestOrbitStartGlobal, ref earliestOrbitBody);
                }
            }

            if (rec.TrackSections != null)
            {
                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    TrackSection sec = rec.TrackSections[i];
                    if (sec.referenceFrame != ReferenceFrame.OrbitalCheckpoint || sec.checkpoints == null)
                        continue;
                    for (int c = 0; c < sec.checkpoints.Count; c++)
                    {
                        OrbitSegment seg = sec.checkpoints[c];
                        if (seg.isPredicted)
                            continue;
                        AccumulateOrbitSegmentBody(
                            seg, win, earliestOrbitStartByBody,
                            ref earliestOrbitStartGlobal, ref earliestOrbitBody);
                    }
                }
            }
        }

        private static void AccumulateOrbitSegmentBody(
            OrbitSegment seg,
            GhostPlaybackLogic.LoopUnit.MemberWindow win,
            Dictionary<string, double> earliestOrbitStartByBody,
            ref double earliestOrbitStartGlobal,
            ref string earliestOrbitBody)
        {
            double segStart = Math.Max(seg.startUT, win.StartUT);
            double segEnd = Math.Min(seg.endUT, win.EndUT);
            if (segEnd <= segStart)
                return;
            if (string.IsNullOrEmpty(seg.bodyName))
                return;
            RecordEarliest(
                earliestOrbitStartByBody, seg.bodyName, segStart,
                ref earliestOrbitStartGlobal, ref earliestOrbitBody);
        }

        private static void RecordEarliest(
            Dictionary<string, double> byBody,
            string body,
            double startUT,
            ref double earliestGlobal,
            ref string earliestBody)
        {
            if (!byBody.TryGetValue(body, out double existing) || startUT < existing)
                byBody[body] = startUT;
            if (startUT < earliestGlobal)
            {
                earliestGlobal = startUT;
                earliestBody = body;
            }
        }

        /// <summary>
        /// Resolves which body a surface/atmospheric TrackSection is on. Prefers the section's
        /// per-frame bodyName (the authoritative per-sample reference), then an OrbitSegment
        /// overlapping the section's UT range, then the recording-level SegmentBodyName /
        /// StartBodyName. Null when no body can be resolved.
        /// </summary>
        internal static string ResolveSectionBody(Recording rec, TrackSection sec)
        {
            if (sec.frames != null)
            {
                for (int i = 0; i < sec.frames.Count; i++)
                {
                    string b = sec.frames[i].bodyName;
                    if (!string.IsNullOrEmpty(b))
                        return b;
                }
            }
            if (sec.bodyFixedFrames != null)
            {
                for (int i = 0; i < sec.bodyFixedFrames.Count; i++)
                {
                    string b = sec.bodyFixedFrames[i].bodyName;
                    if (!string.IsNullOrEmpty(b))
                        return b;
                }
            }
            if (rec.OrbitSegments != null)
            {
                for (int i = 0; i < rec.OrbitSegments.Count; i++)
                {
                    OrbitSegment seg = rec.OrbitSegments[i];
                    if (string.IsNullOrEmpty(seg.bodyName))
                        continue;
                    // Overlap test: any temporal intersection with the section.
                    if (seg.endUT > sec.startUT && seg.startUT < sec.endUT)
                        return seg.bodyName;
                }
            }
            if (!string.IsNullOrEmpty(rec.SegmentBodyName))
                return rec.SegmentBodyName;
            if (!string.IsNullOrEmpty(rec.StartBodyName))
                return rec.StartBodyName;
            return null;
        }

        /// <summary>
        /// A target body C is "same-parent" when it orbits the launch body directly
        /// (C.referenceBody == launchBody). Used by rule 4 to pick the recurrence period
        /// (C.orbit.period for same-parent; the synodic period for cross-parent, Phase 4).
        /// </summary>
        internal static bool IsSameParentTarget(string targetBody, string launchBody, IBodyInfo bodyInfo)
        {
            if (string.IsNullOrEmpty(targetBody) || string.IsNullOrEmpty(launchBody) || bodyInfo == null)
                return false;
            string parent = bodyInfo.ReferenceBodyName(targetBody);
            return !string.IsNullOrEmpty(parent) && parent == launchBody;
        }

        /// <summary>
        /// Detects whether the included window contains a Relative-frame TrackSection that aligns
        /// to another vessel (rendezvous / dock). A non-loop Relative section carries an
        /// anchorRecordingId / anchorVesselId; that is an alignment to a vessel, not a body, so
        /// the body-only solver cannot model it. Parent-anchored debris is NOT a rendezvous (it
        /// rides its own parent and is excluded from mission legs upstream), so this only fires on
        /// a controlled member that records a Relative section to a foreign anchor inside its
        /// window.
        /// </summary>
        private static bool HasRendezvousWithinWindow(
            Recording rec,
            GhostPlaybackLogic.LoopUnit.MemberWindow win,
            out string reason)
        {
            reason = null;
            // A genuine debris/parent-anchored recording is not a mission leg (debris excluded
            // upstream) and its Relative sections anchor to its own parent - never treat those as
            // a rendezvous.
            if (!string.IsNullOrEmpty(rec.ParentAnchorRecordingId))
                return false;
            if (rec.TrackSections == null)
                return false;
            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                TrackSection sec = rec.TrackSections[i];
                if (sec.referenceFrame != ReferenceFrame.Relative)
                    continue;
                double segStart = Math.Max(sec.startUT, win.StartUT);
                double segEnd = Math.Min(sec.endUT, win.EndUT);
                if (segEnd <= segStart)
                    continue;
                bool anchoredToVessel = sec.anchorVesselId != 0
                    || !string.IsNullOrEmpty(sec.anchorRecordingId);
                if (anchoredToVessel)
                {
                    reason = $"member '{rec.RecordingId}' has a Relative section anchored to " +
                             $"another vessel within the included window (rendezvous/dock)";
                    return true;
                }
            }
            return false;
        }

        private static string BuildMissionTag(MissionThroughLineView view)
        {
            return view != null && !string.IsNullOrEmpty(view.TreeId)
                ? $"tree={view.TreeId}"
                : "tree=?";
        }

        private static void LogSummary(
            string missionTag,
            ConstraintExtraction result,
            int includedMembers,
            string emptyReason)
        {
            if (SuppressLogging)
                return;
            var ic = CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder(128);
            sb.Append("ExtractConstraints: ").Append(missionTag)
              .Append(" members=").Append(includedMembers.ToString(ic))
              .Append(" launchBody=").Append(result.LaunchBodyName ?? "<none>")
              .Append(" ut0=").Append(result.UT0.ToString("R", ic))
              .Append(" support=").Append(result.Support.ToString())
              .Append(" constraints=").Append((result.Constraints?.Count ?? 0).ToString(ic));
            if (result.Constraints != null)
            {
                sb.Append(" [");
                for (int i = 0; i < result.Constraints.Count; i++)
                {
                    if (i > 0) sb.Append("; ");
                    sb.Append(result.Constraints[i].ToString());
                }
                sb.Append("]");
            }
            if (!string.IsNullOrEmpty(result.UnsupportedReason))
                sb.Append(" why=").Append(result.UnsupportedReason);
            if (!string.IsNullOrEmpty(emptyReason))
                sb.Append(" note=").Append(emptyReason);
            ParsekLog.Verbose("MissionPeriodicity", sb.ToString());
        }
    }

    /// <summary>
    /// FlightGlobals-backed <see cref="IBodyInfo"/>. Thin: it just reads the live celestial
    /// bodies. Untested directly (it is the Unity-bound seam); the extractor/solver are tested
    /// against a fake IBodyInfo.
    /// </summary>
    internal sealed class FlightGlobalsBodyInfo : IBodyInfo
    {
        internal static readonly FlightGlobalsBodyInfo Instance = new FlightGlobalsBodyInfo();

        private static CelestialBody Find(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName) || FlightGlobals.Bodies == null)
                return null;
            for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
            {
                CelestialBody b = FlightGlobals.Bodies[i];
                if (b != null && b.bodyName == bodyName)
                    return b;
            }
            return null;
        }

        public double RotationPeriod(string bodyName)
        {
            CelestialBody b = Find(bodyName);
            return b != null ? b.rotationPeriod : double.NaN;
        }

        public double OrbitPeriod(string bodyName)
        {
            CelestialBody b = Find(bodyName);
            return b != null && b.orbit != null ? b.orbit.period : double.NaN;
        }

        public string ReferenceBodyName(string bodyName)
        {
            CelestialBody b = Find(bodyName);
            return b != null && b.referenceBody != null ? b.referenceBody.bodyName : null;
        }

        public double SoiRadius(string bodyName)
        {
            CelestialBody b = Find(bodyName);
            return b != null ? b.sphereOfInfluence : double.NaN;
        }

        public double OrbitalVelocity(string bodyName)
        {
            CelestialBody b = Find(bodyName);
            if (b == null || b.orbit == null)
                return double.NaN;
            // Mean orbital velocity = circumference / period (2*pi*a / T). Robust against the
            // current-position API not being available; good enough for the tolerance estimate.
            double period = b.orbit.period;
            if (double.IsNaN(period) || double.IsInfinity(period) || period <= 0.0)
                return double.NaN;
            return 2.0 * Math.PI * b.orbit.semiMajorAxis / period;
        }
    }
}
