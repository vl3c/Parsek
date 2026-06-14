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
    // Implemented here: ExtractConstraints (Phase 0), Solve with the single-constraint Tier-1
    // phase-lock (Phase 1), and the whole-multiples joint best-fit for incommensurate
    // multi-constraint configs (Phase 2: FindBestJointMultiple / JointStepResidual). Phase 2.5
    // (zero-drift per-window reschedule) and Phase 4 (cross-parent / interplanetary) remain
    // deferred; cross-parent targets are reported unsupported (no-lock sentinel).

    /// <summary>The kind of phase requirement an included segment imposes.</summary>
    internal enum ConstraintKind
    {
        /// <summary>A surface/atmospheric segment on a rotating body must place over its
        /// ground spot and connect to its inertial orbit: repeats every body rotation period.</summary>
        Rotation,

        /// <summary>An SOI entry into a body (capture, transient flyby, or gravity assist)
        /// must reach that body where it will be: repeats every body orbit period (direct
        /// child) or the synodic period (sibling/cross-parent, Phase 4).</summary>
        Orbital,

        /// <summary>The included span rendezvouses with another vessel (M4a, design note
        /// docs/dev/design-mission-phasing-alignment.md section 5): the anchor vessel must
        /// sit at its recorded-relative orbital phase, so the constraint repeats every
        /// anchor-vessel orbit period (read LIVE per build, design D1).</summary>
        VesselOrbital
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
        /// Rotation and VesselOrbital constraints.</summary>
        public bool RelativeToParent;

        /// <summary>For VesselOrbital constraints: the anchor vessel's persistentId (the live
        /// vessel the rendezvous Relative sections anchor to). 0 for non-vessel kinds.
        /// <see cref="BodyName"/> then holds the body the anchor ORBITS (display + the
        /// same-parent check).</summary>
        public uint AnchorVesselPid;

        public override string ToString()
        {
            var ic = CultureInfo.InvariantCulture;
            if (Kind == ConstraintKind.VesselOrbital)
                return $"VesselOrbital({AnchorVesselPid.ToString(ic)}@{BodyName ?? "?"}) " +
                       $"P={PeriodSeconds.ToString("R", ic)} off={PhaseOffsetSeconds.ToString("R", ic)}";
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
    /// How the zero-drift schedule treats a TRANSITED (non-launch) body's surface-handoff rotation
    /// constraint (e.g. a Mun landing). The launch-body rotation (the pad) ALWAYS keeps its tight
    /// 0.25 deg tolerance; this only governs the landing on a body the mission travels to. A
    /// player-settable A/B flag (<c>ParsekSettings.transitedBodyRotationMode</c>) because it trades
    /// the relaunch cadence against the approach-&gt;landing handoff seam. See
    /// docs/dev/plans/zero-drift-reschedule.md.
    /// </summary>
    internal enum TransitedBodyRotationMode
    {
        /// <summary>Drop the transited-body rotation constraint entirely: the body's orbital (SOI)
        /// tolerance governs its phase, the body-fixed landing self-anchors. SHORTEST cadence
        /// (~15 Kerbin days for the stock Mun), largest handoff seam (up to the SOI tolerance).</summary>
        Drop,

        /// <summary>Keep the transited-body rotation constraint but at a LOOSE tolerance
        /// (<see cref="TransitedBodyLooseRotationDegrees"/>, a few degrees). MEDIUM cadence (~1-2
        /// Kerbin months), small handoff seam (a few km).</summary>
        Loose,

        /// <summary>Keep the transited-body rotation constraint at the TIGHT 0.25 deg launch-pad
        /// tolerance (the original behavior). LONGEST cadence (~1.65 Kerbin years for the stock Mun
        /// land-and-return), pixel-perfect handoff. The no-regression default for unwired callers.</summary>
        Tight
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

        /// <summary>D3 drift amber (design note 3.4): set when an emitted VesselOrbital
        /// constraint's LIVE anchor period has drifted past tolerance from the RECORDED
        /// rendezvous-time orbit (when anchor recording data exists to compare). Display-only:
        /// never affects <see cref="Support"/> or the emitted period (live wins). Null = none.</summary>
        public string DriftAmberReason;
    }

    /// <summary>
    /// The result of solving an extracted constraint set for the recurrence period P + the next
    /// faithful launch window. Pure value; nothing persisted. See the design doc Data Model.
    /// </summary>
    internal struct PeriodicitySolution
    {
        /// <summary>The recurrence period (MinCycleDuration when unconstrained). NaN on the
        /// "do not phase-lock" sentinel (an unsupported config).</summary>
        public double P;

        /// <summary>The smallest UT0 + k*P &gt;= now (k may be negative for a future-dated UT0).
        /// NaN on the sentinel.</summary>
        public double NextWindowUT;

        /// <summary>The worst (max) circular phase error (seconds) of the DROPPED constraints over
        /// one cadence step at the chosen joint multiple (Phase 2) - i.e. the per-cycle drift the
        /// fixed cadence carries (the launch-pad offset for the Mun case). 0 for a single-constraint
        /// or tidally-collapsed config (nothing dropped).</summary>
        public double ResidualSeconds;

        /// <summary>True when EVERY dropped constraint is within ITS OWN physics-derived tolerance at
        /// the chosen joint multiple (not just the worst residual vs one tolerance). A
        /// single-constraint / tidal-collapse config is always within tolerance (residual 0).</summary>
        public bool WithinTolerance;

        /// <summary>The Support classification carried through from extraction. When != Supported
        /// the solution is the sentinel (no phase-lock).</summary>
        public Support Support;

        /// <summary>True when the caller should phase-lock (anchor = NextWindowUT, cadence
        /// quantized to a multiple of P). False = keep today's behavior (the sentinel).</summary>
        public bool ShouldPhaseLock;

        /// <summary>Which rule set P (for the diagnostic summary): "unconstrained",
        /// "single-rotation", "single-orbital", "tidal-collapse", "joint-best-fit" (a multi-constraint
        /// joint near-resonance at m&gt;1), or "dominant-intercept" (multi-constraint but m=1 was the
        /// best alignment within the search bound).</summary>
        public string Method;

        /// <summary>The "do not phase-lock" sentinel for an unsupported / non-lockable config.
        /// The caller keeps today's arbitrary-phase behavior.</summary>
        internal static PeriodicitySolution NoLock(Support support, string method)
        {
            return new PeriodicitySolution
            {
                P = double.NaN,
                NextWindowUT = double.NaN,
                ResidualSeconds = double.NaN,
                WithinTolerance = false,
                Support = support,
                ShouldPhaseLock = false,
                Method = method
            };
        }
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

        /// <summary>Gravitational parameter (GM, m^3/s^2) of the body itself - the mu for an orbit
        /// AROUND this body, used to compute a loiter segment's orbital period
        /// (T = 2*pi*sqrt(a^3/mu)). Re-aim loiter compression. NaN for an unknown body.</summary>
        double GravParameter(string bodyName);

        /// <summary>Resolves a vessel by persistentId to its CURRENT orbit. False when the vessel
        /// does not exist in the save, has no orbit, the orbit is not closed (ecc &gt;= 1 /
        /// degenerate period), or <paramref name="recordedVesselGuid"/> conclusively differs from
        /// the live vessel's launch Guid (persistentId is craft-baked, NOT launch-unique: a fresh
        /// launch of the same craft reuses the pid and must not read as the recorded anchor;
        /// null/empty guid falls back to pid-only, the VesselLaunchIdentity contract).
        /// periodSeconds = elliptical orbital period; orbitBodyName = the body it orbits. Loaded
        /// and on-rails vessels both resolve (design note D1).</summary>
        bool TryGetVesselOrbit(
            uint vesselPid, string recordedVesselGuid,
            out double periodSeconds, out string orbitBodyName);
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
        /// 2. a surface/atmospheric segment on body B emits Rotation(B) ONLY when the included set
        ///    ALSO contains an inertial-orbit segment of B (the ascent->orbit / orbit->descent
        ///    hand-off). The phase offset = segment start UT - UT0. The rotation phase only matters
        ///    to line a surface segment's hand-off up to an inertial orbit of the same body; a pure
        ///    surface/atmospheric arc renders at its correct ground location at any universe time
        ///    (it rotates with B) and imposes NO phase constraint. Only ONE Rotation(B) per body is
        ///    emitted (the earliest surface start): rotation inheritance is a property of the
        ///    included SET (multiple same-body surface legs collapse to that body's single rotation
        ///    lock); a SECOND distinct surface body or a second incompatible same-body offset is what
        ///    makes the config over-constrained (Phase 2).
        /// 3. an inertial orbit segment contributes NO new constraint of its own (it only enables
        ///    rule 2's Rotation(B) hand-off when an included surface segment of B exists; a bare
        ///    inertial orbit with no surface segment of B, and a bare surface arc with no inertial
        ///    orbit of B, are both free).
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
            uint selfPid = 0;     // the SELF launch line: the earliest member's vessel identity
            string selfGuid = null;
            foreach (var kv0 in memberWindows)
            {
                if (kv0.Value.StartUT < ut0)
                {
                    ut0 = kv0.Value.StartUT;
                    Recording r0 = kv0.Key >= 0 && kv0.Key < committed.Count ? committed[kv0.Key] : null;
                    selfPid = r0 != null ? r0.VesselPersistentId : 0;
                    selfGuid = r0 != null ? r0.RecordedVesselGuid : null;
                }
            }
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

            var vesselAnchors = new Dictionary<string, VesselAnchorInfo>();

            foreach (var kv in memberWindows)
            {
                int idx = kv.Key;
                if (idx < 0 || idx >= committed.Count)
                    continue;
                Recording rec = committed[idx];
                if (rec == null)
                    continue;
                GhostPlaybackLogic.LoopUnit.MemberWindow win = kv.Value;

                // Rendezvous / dock collection (M4a): a Relative TrackSection inside the included
                // window aligns this member to ANOTHER vessel, not a body. Collect the distinct
                // vessel anchors; TryBuildVesselOrbitalConstraint below either emits a VesselOrbital
                // constraint (exactly one same-parent closed-orbit anchor) or rejects fail-closed.
                CollectVesselAnchorsWithinWindow(rec, win, vesselAnchors);

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

            // 3. Rotation constraints: ONE per surface body (earliest start), launch body first,
            //    emitted in recorded-UT (offset) order. A Rotation(B) constraint is emitted ONLY
            //    when the INCLUDED set has BOTH a surface/atmospheric segment of B AND an
            //    inertial-orbit segment of B (the ascent->orbit / orbit->descent hand-off). The
            //    rotation phase only matters to line that hand-off up over the launch/landing site:
            //    a surface arc is recorded surface-relative and renders at its correct ground
            //    location at ANY universe time (it rotates with B), so a surface-only /
            //    atmospheric-only config of B imposes NO phase constraint (design "What determines
            //    P" rule 1/2 + the "no-inertial-arc -> MinCycleDuration" edge case). Note that the
            //    launch body's own orbit segments are kept in earliestOrbitStartByBody (rule 4 only
            //    skips them as SOI ENTRIES), so the hand-off check sees them here.
            var rotationBodiesSorted = new List<KeyValuePair<string, double>>(earliestSurfaceStartByBody);
            rotationBodiesSorted.Sort((a, b) =>
            {
                int cmp = a.Value.CompareTo(b.Value);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.Key, b.Key);
            });
            foreach (var rb in rotationBodiesSorted)
            {
                // Hand-off gate: require an inertial-orbit segment of the SAME body B in the
                // included set. No orbit of B -> a pure surface/atmospheric arc -> no rotation
                // constraint (faithful at any time).
                if (!earliestOrbitStartByBody.ContainsKey(rb.Key))
                    continue;
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

            // 5. Vessel rendezvous (M4a + M4c): the supported shape (exactly ONE foreign
            //    closed-orbit anchor orbiting the launch body or a transited body) emits a
            //    VesselOrbital constraint and keeps whatever Support the body rules computed
            //    (same-parent destinations schedule; cross-parent destinations stay
            //    UnsupportedCrossParent and the re-aim arrival hold consumes the constraint);
            //    every other shape rejects fail-closed. A REJECTED rendezvous outranks
            //    cross-parent for the report (it is never solvable, even in Phase 4), so it is
            //    set last, preserving the pre-M4a ordering.
            string missionTag = BuildMissionTag(view);
            if (vesselAnchors.Count > 0)
            {
                VesselOrbitalClassification cls = ClassifyVesselOrbitalConstraint(
                    vesselAnchors, launchBody, ut0, selfPid, selfGuid, bodyInfo, committed,
                    result.Constraints,
                    out PhaseConstraint stationConstraint, out string rejectReason,
                    out string driftAmber);
                if (cls == VesselOrbitalClassification.Emitted)
                {
                    result.Constraints.Add(stationConstraint);
                    result.DriftAmberReason = driftAmber;
                }
                else if (cls == VesselOrbitalClassification.Rejected)
                {
                    result.Support = Support.UnsupportedRendezvous;
                    result.UnsupportedReason = rejectReason;
                }
                // NoForeignAnchor: every vessel-anchored section was an intra-mission pair
                // (both dock partners included) - no constraint, Support untouched.
            }
            LogDriftAmberTransition(missionTag, result.DriftAmberReason);

            LogSummary(
                missionTag,
                result,
                memberWindows.Count,
                null);
            return result;
        }

        // Fraction of a body's rotation period that counts as "within tolerance" for a rotation
        // constraint (a small fraction of a degree of spin). Starting value per the design's
        // "Still open" note; refined by playtest. 0.25 degrees / 360 = ~0.0007 of a full turn.
        private const double RotationToleranceFraction = 0.25 / 360.0;

        // The LOOSE rotation tolerance (in degrees) for a TRANSITED (non-launch) body's surface
        // handoff under TransitedBodyRotationMode.Loose. A landing on a transited body (e.g. the Mun)
        // is recorded body-fixed, so it self-anchors to the live surface; its rotation only affects
        // the approach->landing handoff SEAM (a small deep-space discontinuity), which tolerates far
        // more than the tight 0.25 deg launch-pad value. A few degrees keeps the seam small (a few km)
        // while shortening the faithful-window cadence from ~1.65 Kerbin years (Tight) to ~1-2 Kerbin
        // months. Dropping the constraint entirely (Mode.Drop) shortens it further to ~15 Kerbin days
        // (seam up to the SOI tolerance). Tunable; A/B-tested in playtest.
        private const double TransitedBodyLooseRotationDegrees = 5.0;

        // The VesselOrbital (station) phase tolerance in degrees of the anchor's orbit (design
        // note 5.3). Not a player setting. Widened 1.0 -> 3.0 on 2026-06-11 playtest evidence: at
        // 1 degree (a 6.8s budget for a ~2434s LKO station) the knob's rev ladder slips out of
        // tolerance after 2-3 consecutive daily windows (the d=k family drifts ~2.5s/window for
        // the playtest geometry) and the schedule then waits ~40 days for the next lattice
        // coincidence - the Missions window showed an 18-day AVERAGE interval despite a 6h next
        // launch. 3 degrees (~20s) sustains ~8 consecutive windows per family and multiplies the
        // lattice hit rate, collapsing the average to days. Cost: the recorded ABSOLUTE final
        // approach can visually miss the live station by up to ~40 km at the tolerance edge
        // (typical accepted windows land far tighter); the dock loop-clock marker is unaffected.
        private const double StationPhaseToleranceDegrees = 3.0;

        // Phase 2 joint best-fit (the FIXED-cadence FALLBACK, used now only when a drifting config's
        // schedule is rejected for overlap or cannot build - the common drifting case takes the
        // zero-drift per-window schedule instead, NOT this m): the relaunch period is a whole multiple
        // m of the DOMINANT constraint's period (so the dominant body, e.g. the Mun intercept, stays
        // locked at every relaunch), searched up to this many multiples for the m that best RE-ALIGNS
        // the other ("dropped") constraints. m=1 is the old Tier-1 behavior (drop the residual
        // wholesale); a larger m trades a longer relaunch period for a tighter joint alignment. 16
        // keeps the relaunch period watchable (this best-fit picks m=9 / ~14.5 days for the stock Mun
        // periods; the launch-pad residual drops from ~9688s/161deg at m=1 to ~993s/16deg). A fixed
        // cadence still DRIFTS on incommensurate periods (the residual is the per-cycle drift); the
        // zero-drift per-window reschedule (now implemented - see TryBuildRelaunchSchedule +
        // docs/dev/plans/zero-drift-reschedule.md) is what closes that for the in-game Mun config.
        private const int MaxJointMultiples = 16;

        // Two multiples whose worst dropped-constraint phase error differ by less than this (seconds)
        // are a tie; the smaller m (shorter relaunch period) wins, so we never lengthen the period
        // for a negligible alignment gain.
        private const double JointResidualTieEpsilonSeconds = 1.0;

        // === Zero-drift per-window reschedule (docs/dev/plans/zero-drift-reschedule.md) ===

        // How many whole multiples of the ANCHOR period (the tightest constraint, e.g. the launch
        // pad - see SelectAnchorConstraintIndex) to scan forward from a relaunch when searching for
        // the next faithful window (the next k where every OTHER constraint is within its tolerance).
        // k counts ANCHOR-PERIOD steps, NOT the longest period. It must span at least one
        // within-tolerance recurrence for the supported configs: a simple launch + orbit-to-Mun (pad
        // + Mun SOI tolerance, ~3%) has its first faithful window at k ~= 13 pad rotations; a config
        // that also LANDS on a tidally-locked body adds a tight (0.25 deg) Rotation(body) constraint
        // whose recurrence is hundreds of pad rotations (~700 for the stock Mun land-and-return), so
        // 4096 covers both with headroom and leaves room for planet packs. A too-small bound does NOT
        // cause runaway drift (the search always restarts from the previous launch and picks the next
        // good k); it just yields more amber (over-tolerance) bounded-best launches. The search is
        // O(this) cheap CircularPhaseError calls per relaunch (returning early at the first faithful
        // k), amortized + cached. Documented tunable, like MaxJointMultiples.
        internal const int ScheduleLookaheadMultiples = 4096;

        // M4b phasing-loiter knob (docs/dev/plans/mission-loiter-knob.md section 3): the cap on
        // EXTENDING the phasing loiter past its recorded rev count (design D6). An LKO parking rev
        // is ~32-45 min, so +10 bounds the added per-cycle dead time to roughly 5-7 hours (about one
        // pad day); a phase unreachable within +10 revs is usually reachable at one of the NEXT pad
        // windows instead (the outer scan compensates). A constant, not a setting.
        internal const int MaxExtraLoiterRevs = 10;

        // Hard safety cap on cached schedule launches, a CPU valve against a pathological tiny
        // dominant period (malformed body data) that would otherwise generate astronomically many
        // launches. Far above any realistic within-tolerance launch count over a long game. On hit,
        // the resolver logs a rate-limited Warn and parks at the last cached launch (graceful, no
        // crash, no unbounded CPU); realistic configs never approach it.
        internal const int MaxScheduleSteps = 8192;

        // Two periods within this relative tolerance are treated as equal (tidal-collapse): such
        // constraints line up at every window, so a config whose dropped constraints all share the
        // dominant period does not drift and gets no schedule.
        private const double PeriodEqualityRelTolerance = 1e-6;

        /// <summary>
        /// Tier-1 solver (design doc Proposed design / Tier 1; plan Phase 1). Locks the SINGLE
        /// dominant constraint and returns P + the next faithful launch UT + the (knowingly
        /// dropped) residual. Pure. The full joint best-fit + accurate multi-constraint residual is
        /// Phase 2; this deliberately does NOT implement it.
        ///
        /// Rules:
        /// - <paramref name="support"/> != Supported -> the NoLock sentinel (caller keeps today's
        ///   arbitrary-phase behavior; never mis-schedules a cross-parent / rendezvous /
        ///   multi-constraint-pre-P2 config).
        /// - 0 constraints -> P = MinCycleDuration (unconstrained = free loop).
        /// - exactly one Rotation -> P = rotationPeriod; one direct-child Orbital -> P =
        ///   C.orbit.period; a Rotation+direct-child-Orbital pair on a tidally-locked body (equal
        ///   periods) -> that one period (collapses for free).
        /// - the Mun case (Rotation(Kerbin) + Orbital(Mun), differing periods) -> lock Orbital(Mun)
        ///   (the dominant intercept) and KNOWINGLY drop the Rotation(Kerbin) residual (logged).
        ///   Any other multiple-independent-constraint set still locks the dominant intercept and
        ///   logs the residual.
        /// - NextWindowUT = smallest UT0 + k*P &gt;= nowUT (k may be negative for a future-dated
        ///   UT0). Guards rotationPeriod &lt;= 0 / NaN / P &lt;= 0 -> MinCycleDuration / no lock.
        /// </summary>
        internal static PeriodicitySolution Solve(
            IReadOnlyList<PhaseConstraint> constraints,
            Support support,
            double ut0,
            double nowUT,
            IBodyInfo bodyInfo)
        {
            // Unsupported config: never phase-lock (keep today's behavior).
            if (support != Support.Supported)
            {
                PeriodicitySolution sentinel = PeriodicitySolution.NoLock(support, "unsupported-no-lock");
                LogSolve("?", sentinel, ut0, nowUT);
                return sentinel;
            }

            int count = constraints?.Count ?? 0;

            // 0 constraints -> unconstrained free loop at the minimum cycle.
            if (count == 0)
            {
                double pFree = LoopTiming.MinCycleDuration;
                var free = new PeriodicitySolution
                {
                    P = pFree,
                    NextWindowUT = NextWindow(ut0, pFree, nowUT),
                    ResidualSeconds = 0.0,
                    WithinTolerance = true,
                    Support = Support.Supported,
                    ShouldPhaseLock = true,
                    Method = "unconstrained"
                };
                LogSolve("?", free, ut0, nowUT);
                return free;
            }

            // Pick the dominant constraint: the Orbital intercept (the dominant visual break) if
            // any, else the single Rotation. Among multiple Orbital constraints, the longest period
            // dominates (the hardest window to hit). Among multiple Rotations with no Orbital, the
            // longest period dominates.
            int dominantIdx = SelectDominantConstraintIndex(constraints);
            PhaseConstraint dominant = constraints[dominantIdx];
            double p = dominant.PeriodSeconds;

            // Guard a degenerate dominant period (should not happen post-extraction, but be safe):
            // fall back to the free loop rather than divide-by-zero / no lock.
            if (double.IsNaN(p) || double.IsInfinity(p) || p <= 0.0)
            {
                double pFree = LoopTiming.MinCycleDuration;
                var guarded = new PeriodicitySolution
                {
                    P = pFree,
                    NextWindowUT = NextWindow(ut0, pFree, nowUT),
                    ResidualSeconds = 0.0,
                    WithinTolerance = true,
                    Support = Support.Supported,
                    ShouldPhaseLock = true,
                    Method = "degenerate-dominant-period-free"
                };
                LogSolve(dominant.BodyName, guarded, ut0, nowUT);
                return guarded;
            }

            // === Joint best-fit over whole multiples of the dominant period (Phase 2) ===
            // NOTE: for a DRIFTING config the loop builder uses this Solve result only for the
            // diagnostic P / residual / method; the actual in-game cadence comes from the zero-drift
            // per-window schedule (TryBuildRelaunchSchedule), NOT this fixed m*P. This branch is the
            // fixed-cadence FALLBACK (schedule rejected/unbuildable).
            // The dominant constraint is locked EXACTLY: the relaunch period P is a whole multiple m
            // of its period, so the dominant body (e.g. the Mun intercept) is in its recorded
            // position at EVERY relaunch for any m. We then search small m to best RE-ALIGN the other
            // ("dropped") constraints over one cadence step - the joint near-resonance. m=1 is the old
            // Tier-1 behavior (the dropped residual taken wholesale); a larger m trades a longer
            // relaunch period for a tighter joint alignment (this best-fit picks m=9 / ~993s/16deg for
            // the stock Mun periods, down from ~9688s/161deg at m=1). Single-constraint and tidal-lock
            // configs skip the search (m=1, residual 0 - they line up at every window). The fixed
            // cadence cannot hold incommensurate periods aligned forever (the residual IS the
            // per-cycle drift); the zero-drift per-window reschedule closes that for drifting configs.
            int multiple;
            double residual;
            string method;
            bool withinTolerance;
            if (count == 1)
            {
                multiple = 1;
                residual = 0.0;
                method = dominant.Kind == ConstraintKind.Orbital ? "single-orbital"
                    : dominant.Kind == ConstraintKind.VesselOrbital ? "single-vessel-orbital"
                    : "single-rotation";
                withinTolerance = true; // nothing dropped
            }
            else if (AllDroppedSharePeriod(constraints, dominantIdx, p))
            {
                // Tidal-lock collapse: every dropped constraint shares the dominant period, so they
                // line up at every window (e.g. Mun rotation + Mun intercept). Lock at m=1, residual 0.
                multiple = 1;
                residual = 0.0;
                method = "tidal-collapse";
                withinTolerance = true;
            }
            else
            {
                // Incommensurate multi-constraint: joint best-fit. Search whole multiples m of the
                // dominant period for the one that minimizes the worst dropped-constraint phase error
                // over one cadence step (m*P); the dominant stays locked at any m.
                multiple = FindBestJointMultiple(constraints, dominantIdx, p, out residual);
                // Within tolerance iff EVERY dropped constraint is within ITS OWN tolerance at the
                // chosen step (not the worst residual vs a single tolerance, which could compare a
                // short-period constraint's residual against a long-period constraint's larger
                // tolerance when 3+ constraints are dropped).
                withinTolerance = AllDroppedWithinTolerance(
                    constraints, dominantIdx, multiple * p, bodyInfo);
                // m>1 = a genuine joint near-resonance was found; m=1 = the dominant period itself was
                // already the best alignment within the search bound (old Tier-1 wholesale drop).
                method = multiple > 1 ? "joint-best-fit" : "dominant-intercept";
            }

            double effectiveP = multiple * p;
            double nextWindow = NextWindow(ut0, effectiveP, nowUT);
            string dominantMissed =
                DescribeWorstDroppedConstraint(constraints, dominantIdx, effectiveP);

            var sol = new PeriodicitySolution
            {
                P = effectiveP,
                NextWindowUT = nextWindow,
                ResidualSeconds = residual,
                WithinTolerance = withinTolerance,
                Support = Support.Supported,
                ShouldPhaseLock = true,
                Method = method
            };
            LogSolve(dominant.BodyName, sol, ut0, nowUT, dominantMissed);
            return sol;
        }

        /// <summary>
        /// Joint near-resonance search (Phase 2): the whole multiple m in [1, <see
        /// cref="MaxJointMultiples"/>] of <paramref name="dominantPeriod"/> that MINIMIZES the worst
        /// circular phase error of the DROPPED constraints over one cadence step (m * dominantPeriod).
        /// The dominant constraint is locked exactly at any m (m*P is a whole multiple of its period);
        /// a larger m can re-align the dropped constraints better at the cost of a longer relaunch
        /// period. Prefers the SMALLEST m on a (near-)tie (within <see
        /// cref="JointResidualTieEpsilonSeconds"/>) so the relaunch period stays as short as the
        /// alignment allows. Pure. <paramref name="bestResidual"/> = the worst dropped phase error at
        /// the chosen m (the per-cycle drift the fixed cadence carries).
        /// </summary>
        internal static int FindBestJointMultiple(
            IReadOnlyList<PhaseConstraint> constraints, int dominantIdx, double dominantPeriod,
            out double bestResidual)
        {
            int bestM = 1;
            bestResidual = JointStepResidual(constraints, dominantIdx, dominantPeriod, 1);
            for (int m = 2; m <= MaxJointMultiples; m++)
            {
                double r = JointStepResidual(constraints, dominantIdx, dominantPeriod, m);
                if (r < bestResidual - JointResidualTieEpsilonSeconds)
                {
                    bestResidual = r;
                    bestM = m;
                }
            }
            return bestM;
        }

        /// <summary>
        /// The worst (max) circular phase error of the DROPPED constraints (every constraint except
        /// <paramref name="dominantIdx"/>) over one cadence step <paramref name="multiple"/> *
        /// <paramref name="dominantPeriod"/>. 0 when nothing is dropped. Pure.
        /// </summary>
        internal static double JointStepResidual(
            IReadOnlyList<PhaseConstraint> constraints, int dominantIdx, double dominantPeriod,
            int multiple)
        {
            double step = multiple * dominantPeriod;
            double worst = 0.0;
            for (int i = 0; i < constraints.Count; i++)
            {
                if (i == dominantIdx)
                    continue;
                double err = CircularPhaseError(step, constraints[i].PeriodSeconds);
                if (err > worst)
                    worst = err;
            }
            return worst;
        }

        // For the diagnostic log only: the dropped constraint carrying the worst phase error at the
        // chosen cadence step (null when nothing is dropped).
        private static string DescribeWorstDroppedConstraint(
            IReadOnlyList<PhaseConstraint> constraints, int dominantIdx, double step)
        {
            double worst = -1.0;
            string desc = null;
            for (int i = 0; i < constraints.Count; i++)
            {
                if (i == dominantIdx)
                    continue;
                double err = CircularPhaseError(step, constraints[i].PeriodSeconds);
                if (err > worst)
                {
                    worst = err;
                    desc = constraints[i].ToString();
                }
            }
            return desc;
        }

        /// <summary>
        /// Selects the dominant constraint index (the one whose period sets P in Tier 1). The
        /// Orbital intercept is the dominant visual break, so any Orbital outranks any Rotation
        /// (a VesselOrbital station rendezvous ranks WITH Orbital); within a kind, the LONGEST
        /// period dominates (the hardest window to hit). Ties broken by the smaller phase offset,
        /// then index, for determinism.
        /// </summary>
        internal static int SelectDominantConstraintIndex(IReadOnlyList<PhaseConstraint> constraints)
        {
            int best = 0;
            for (int i = 1; i < constraints.Count; i++)
            {
                if (IsMoreDominant(constraints[i], constraints[best]))
                    best = i;
            }
            return best;
        }

        /// <summary>
        /// True when EVERY non-dominant constraint shares the dominant period (within a tiny
        /// tolerance) - the tidal-lock collapse case, where the constraints line up at every window.
        /// </summary>
        private static bool AllDroppedSharePeriod(
            IReadOnlyList<PhaseConstraint> constraints, int dominantIdx, double dominantPeriod)
        {
            const double tol = 1e-6;
            for (int i = 0; i < constraints.Count; i++)
            {
                if (i == dominantIdx)
                    continue;
                if (Math.Abs(constraints[i].PeriodSeconds - dominantPeriod) > tol * Math.Max(1.0, dominantPeriod))
                    return false;
            }
            return true;
        }

        private static bool IsMoreDominant(PhaseConstraint candidate, PhaseConstraint current)
        {
            // Orbital beats Rotation. VesselOrbital ranks WITH Orbital: a station rendezvous is an
            // intercept-style constraint (the dominant visual break), not a surface-handoff one.
            bool candOrbital = candidate.Kind == ConstraintKind.Orbital
                || candidate.Kind == ConstraintKind.VesselOrbital;
            bool curOrbital = current.Kind == ConstraintKind.Orbital
                || current.Kind == ConstraintKind.VesselOrbital;
            if (candOrbital != curOrbital)
                return candOrbital;
            // Same kind: longer period dominates.
            if (candidate.PeriodSeconds > current.PeriodSeconds)
                return true;
            if (candidate.PeriodSeconds < current.PeriodSeconds)
                return false;
            // Equal period: earlier offset dominates (deterministic).
            return candidate.PhaseOffsetSeconds < current.PhaseOffsetSeconds;
        }

        /// <summary>
        /// The smallest UT0 + k*P that is &gt;= nowUT (k may be negative when UT0 is in the future).
        /// P must be &gt; 0.
        /// </summary>
        internal static double NextWindow(double ut0, double p, double nowUT)
        {
            if (double.IsNaN(ut0) || double.IsNaN(p) || p <= 0.0)
                return double.NaN;
            // k = ceil((now - ut0) / P). Use a small epsilon so a window landing exactly on now is
            // treated as "now" rather than skipping to the next one.
            double kReal = (nowUT - ut0) / p;
            double k = Math.Ceiling(kReal - 1e-9);
            double window = ut0 + k * p;
            // Floating-point guard: ensure window >= now (and not more than one P past).
            if (window < nowUT - 1e-6)
                window += p;
            return window;
        }

        /// <summary>
        /// Circular phase error: how far <paramref name="deltaSeconds"/> is from a whole multiple
        /// of <paramref name="period"/>, in seconds (always in [0, period/2]). Returns 0 for a
        /// non-positive / degenerate period (no constraint to miss).
        /// </summary>
        internal static double CircularPhaseError(double deltaSeconds, double period)
        {
            if (double.IsNaN(period) || double.IsInfinity(period) || period <= 0.0)
                return 0.0;
            double m = deltaSeconds % period;
            if (m < 0.0)
                m += period;
            return Math.Min(m, period - m);
        }

        // === Zero-drift per-window reschedule solver (plan section 2) ===========================

        /// <summary>
        /// The smallest faithful launch UT &gt; <paramref name="afterUT"/>: the ANCHOR constraint is
        /// pinned EXACTLY (candidate launches are <c>UT0 + k*anchorPeriod</c>) and every OTHER
        /// constraint must fall within its own tolerance there. Returns the first such launch in the
        /// look-ahead window, else the BOUNDED-BEST (min worst-other-residual) launch in the window -
        /// never accumulating, since the residual at launch k is the ABSOLUTE worst other-body error
        /// (the phase offsets cancel, plan section 2.1). NaN on a degenerate anchor period. The anchor
        /// is chosen by <see cref="SelectAnchorConstraintIndex"/> (the tightest-tolerance constraint,
        /// e.g. the launch pad) which maximizes the faithful-window frequency. Pure.
        /// <paramref name="residualSeconds"/> = the worst other-constraint phase error at the chosen
        /// launch; <paramref name="withinTolerance"/> = whether it was within tolerance (vs the
        /// bounded-best fallback). See docs/dev/plans/zero-drift-reschedule.md.
        /// </summary>
        internal static double NextJointNearCoincidenceUT(
            double afterUT, double ut0, double anchorPeriod,
            IReadOnlyList<double> otherPeriods, IReadOnlyList<double> otherTolerances,
            int lookaheadMultiples,
            out double residualSeconds, out bool withinTolerance)
        {
            residualSeconds = double.NaN;
            withinTolerance = false;
            if (double.IsNaN(ut0) || double.IsNaN(afterUT)
                || double.IsNaN(anchorPeriod) || double.IsInfinity(anchorPeriod)
                || anchorPeriod <= 0.0)
                return double.NaN;

            // k of afterUT (small epsilon so a launch landing exactly on afterUT is not re-returned).
            long kPrev = (long)Math.Floor((afterUT - ut0) / anchorPeriod + 1e-6);
            long kStart = kPrev + 1;
            if (kStart < 1)
                kStart = 1; // k=0 is UT0 itself (the original recorded play), never a relaunch.

            if (TryFindNextScheduleK(
                    anchorPeriod, otherPeriods, otherTolerances, kStart, lookaheadMultiples,
                    out long k, out residualSeconds, out withinTolerance))
                return ut0 + k * anchorPeriod;
            return double.NaN;
        }

        /// <summary>
        /// Scans whole anchor-multiples k in [<paramref name="kStart"/>,
        /// kStart + <paramref name="lookaheadMultiples"/>) for the FIRST k where every OTHER
        /// constraint is within its tolerance; if none, the k with the smallest worst-other residual
        /// in that window (ties -> smallest k). Returns false only on a degenerate anchor period or a
        /// non-positive look-ahead. The other-residual at k is
        /// <c>max_j CircularPhaseError(k*anchorPeriod, otherPeriods[j])</c> - an absolute function of
        /// k, so picking good k never accumulates. The anchor itself is exact at every k. Pure.
        /// </summary>
        internal static bool TryFindNextScheduleK(
            double anchorPeriod,
            IReadOnlyList<double> otherPeriods, IReadOnlyList<double> otherTolerances,
            long kStart, int lookaheadMultiples,
            out long foundK, out double residualSeconds, out bool withinTolerance)
        {
            return TryFindNextScheduleK(
                anchorPeriod, otherPeriods, otherTolerances,
                null, null, 0.0, 0, 0,
                kStart, lookaheadMultiples,
                out foundK, out _, out residualSeconds, out withinTolerance);
        }

        /// <summary>
        /// The phasing-knob scan (M4b, docs/dev/plans/mission-loiter-knob.md section 3.3): like the
        /// base overload, but with a second SHIFTABLE constraint group whose events the per-cycle
        /// loiter re-time moves by <c>d * shiftStepSeconds</c> (d = kept revs minus recorded revs).
        /// Per candidate k the UNSHIFTABLE group is evaluated at <c>k * anchorPeriod</c> exactly as
        /// today, then d is enumerated in [<paramref name="shiftMin"/>, <paramref name="shiftMax"/>]
        /// by |d| ascending with d &lt; 0 first on magnitude ties (prefer the cut - the shorter
        /// cycle): the shiftable residual at (k, d) is
        /// <c>max_j CircularPhaseError(k*anchorPeriod + d*shiftStepSeconds, shiftPeriods[j])</c>.
        /// Accept the first k where BOTH groups pass (short-circuiting the inner loop on the first
        /// in-tolerance d); else the bounded-best (k, d) minimizing
        /// <c>max(worstUnshift, min_d worstShift)</c> with ties to the earlier k then the smaller
        /// |d|. An empty/degenerate shiftable group reduces EXACTLY to the base scan
        /// (<paramref name="foundShiftRevs"/> = 0). Pure.
        /// </summary>
        internal static bool TryFindNextScheduleK(
            double anchorPeriod,
            IReadOnlyList<double> otherPeriods, IReadOnlyList<double> otherTolerances,
            IReadOnlyList<double> shiftPeriods, IReadOnlyList<double> shiftTolerances,
            double shiftStepSeconds, long shiftMin, long shiftMax,
            long kStart, int lookaheadMultiples,
            out long foundK, out long foundShiftRevs,
            out double residualSeconds, out bool withinTolerance)
        {
            foundK = 0;
            foundShiftRevs = 0;
            residualSeconds = double.NaN;
            withinTolerance = false;
            if (double.IsNaN(anchorPeriod) || double.IsInfinity(anchorPeriod) || anchorPeriod <= 0.0)
                return false;
            if (lookaheadMultiples <= 0)
                return false;

            int count = otherPeriods?.Count ?? 0;
            int shiftCount = shiftPeriods?.Count ?? 0;
            bool knob = shiftCount > 0
                && !double.IsNaN(shiftStepSeconds) && !double.IsInfinity(shiftStepSeconds)
                && shiftStepSeconds > 0.0
                && shiftMax >= shiftMin;
            long bestK = -1;
            long bestShift = 0;
            double bestResidual = double.PositiveInfinity;

            for (int step = 0; step < lookaheadMultiples; step++)
            {
                long k = kStart + step;
                double delta = k * anchorPeriod;
                double worst = 0.0;
                bool allWithin = true;
                for (int j = 0; j < count; j++)
                {
                    double err = CircularPhaseError(delta, otherPeriods[j]);
                    if (err > worst)
                        worst = err;
                    double tol = (otherTolerances != null && j < otherTolerances.Count)
                        ? otherTolerances[j] : 0.0;
                    if (err > tol)
                        allWithin = false;
                }

                long chosenShift = 0;
                bool shiftWithin = true;
                double worstShiftAtChosen = 0.0;
                if (knob)
                {
                    shiftWithin = false;
                    worstShiftAtChosen = double.PositiveInfinity;
                    // |d| ascending, d < 0 before d > 0 at equal magnitude: the smallest timeline
                    // change wins, and on a magnitude tie the CUT (shorter cycle, less dead time)
                    // beats the extension. Short-circuits on the first in-tolerance d; the strict <
                    // on the bounded-best update preserves the same preference order when none fits.
                    long maxMag = Math.Max(Math.Abs(shiftMin), Math.Abs(shiftMax));
                    for (long mag = 0; mag <= maxMag && !shiftWithin; mag++)
                    {
                        for (int sign = 0; sign < 2; sign++)
                        {
                            long d = sign == 0 ? -mag : mag;
                            if (mag == 0 && sign == 1)
                                continue; // d = 0 once
                            if (d < shiftMin || d > shiftMax)
                                continue;
                            double shifted = delta + d * shiftStepSeconds;
                            double worstShift = 0.0;
                            bool allShiftWithin = true;
                            for (int j = 0; j < shiftCount; j++)
                            {
                                double err = CircularPhaseError(shifted, shiftPeriods[j]);
                                if (err > worstShift)
                                    worstShift = err;
                                double tol = (shiftTolerances != null && j < shiftTolerances.Count)
                                    ? shiftTolerances[j] : 0.0;
                                if (err > tol)
                                    allShiftWithin = false;
                            }
                            if (allShiftWithin)
                            {
                                chosenShift = d;
                                worstShiftAtChosen = worstShift;
                                shiftWithin = true;
                                break;
                            }
                            if (worstShift < worstShiftAtChosen)
                            {
                                worstShiftAtChosen = worstShift;
                                chosenShift = d;
                            }
                        }
                    }
                }

                double worstCombined = Math.Max(worst, knob ? worstShiftAtChosen : 0.0);
                if (allWithin && shiftWithin)
                {
                    foundK = k;
                    foundShiftRevs = chosenShift;
                    residualSeconds = worstCombined;
                    withinTolerance = true;
                    return true;
                }
                if (worstCombined < bestResidual)
                {
                    bestResidual = worstCombined;
                    bestK = k;
                    bestShift = chosenShift;
                }
            }

            // No within-tolerance k in the window: the bounded-best (min absolute residual) launch.
            foundK = bestK < 0 ? kStart : bestK;
            foundShiftRevs = bestK < 0 ? 0 : bestShift;
            residualSeconds = double.IsPositiveInfinity(bestResidual) ? 0.0 : bestResidual;
            withinTolerance = false;
            return true;
        }

        /// <summary>
        /// Selects the ANCHOR constraint for the zero-drift schedule: the one with the SMALLEST duty
        /// cycle (tolerance / period) - the tightest band, hardest to satisfy. Pinning the tightest
        /// constraint EXACTLY and letting the looser ones fall within their tolerance MAXIMIZES the
        /// faithful-window frequency: the window rate is roughly anchorPeriod / product(other duty
        /// cycles), so you never want to divide the period by the smallest duty - pin it instead. For
        /// a Mun mission this picks the launch-pad rotation (tight, a fraction of a degree), NOT the
        /// Mun intercept (a generous SOI-width tolerance), so faithful windows recur every few days
        /// instead of every few years - and the launch lands pixel-perfect over the pad. Ties broken
        /// by shorter period, then index. Pure. Degenerate-period constraints are skipped.
        /// </summary>
        internal static int SelectAnchorConstraintIndex(
            IReadOnlyList<PhaseConstraint> constraints, IBodyInfo bodyInfo,
            string launchBodyName = null,
            TransitedBodyRotationMode mode = TransitedBodyRotationMode.Tight)
        {
            int best = 0;
            double bestDuty = double.PositiveInfinity;
            double bestPeriod = double.PositiveInfinity;
            for (int i = 0; i < constraints.Count; i++)
            {
                double p = constraints[i].PeriodSeconds;
                if (double.IsNaN(p) || double.IsInfinity(p) || p <= 0.0)
                    continue;
                double duty = ScheduleToleranceSecondsFor(constraints[i], bodyInfo, launchBodyName, mode) / p;
                bool better = duty < bestDuty - 1e-12
                    || (duty <= bestDuty + 1e-12 && p < bestPeriod);
                if (better)
                {
                    best = i;
                    bestDuty = duty;
                    bestPeriod = p;
                }
            }
            return best;
        }

        /// <summary>
        /// The tolerance (seconds) the SCHEDULE uses for a constraint, applying the
        /// <see cref="TransitedBodyRotationMode"/>: a TRANSITED (non-launch) body's Rotation constraint
        /// (e.g. a Mun landing) gets the LOOSE tolerance (<see cref="TransitedBodyLooseRotationDegrees"/>)
        /// under <see cref="TransitedBodyRotationMode.Loose"/>; everything else (the launch-body
        /// rotation = the pad, all Orbital constraints, and every constraint under
        /// <see cref="TransitedBodyRotationMode.Tight"/>) uses the normal physics tolerance
        /// <see cref="ToleranceSecondsFor"/>. <see cref="TransitedBodyRotationMode.Drop"/> is handled by
        /// the caller pre-filtering the transited-body rotation out, so it never reaches here. A
        /// null/empty <paramref name="launchBodyName"/> treats nothing as transited (everything tight),
        /// matching the unwired / fixed-cadence path. Pure.
        /// </summary>
        internal static double ScheduleToleranceSecondsFor(
            PhaseConstraint c, IBodyInfo bodyInfo, string launchBodyName, TransitedBodyRotationMode mode)
        {
            if (mode == TransitedBodyRotationMode.Loose
                && c.Kind == ConstraintKind.Rotation
                && !string.IsNullOrEmpty(launchBodyName)
                && c.BodyName != launchBodyName)
            {
                double p = c.PeriodSeconds;
                if (!double.IsNaN(p) && !double.IsInfinity(p) && p > 0.0)
                    return p * (TransitedBodyLooseRotationDegrees / 360.0);
            }
            return ToleranceSecondsFor(c, bodyInfo);
        }

        /// <summary>
        /// True when <paramref name="c"/> is a TRANSITED-body surface rotation constraint (a Rotation
        /// constraint on a body that is NOT the launch body, e.g. a Mun landing). Such constraints are
        /// what <see cref="TransitedBodyRotationMode"/> governs (dropped under Drop, loosened under
        /// Loose). A null/empty launch body treats nothing as transited.
        /// </summary>
        internal static bool IsTransitedBodyRotation(PhaseConstraint c, string launchBodyName)
        {
            return c.Kind == ConstraintKind.Rotation
                && !string.IsNullOrEmpty(launchBodyName)
                && c.BodyName != launchBodyName;
        }

        /// <summary>
        /// Builds the zero-drift relaunch schedule for a phase-locked, drifting (multi-constraint
        /// incommensurate) config, or returns false (no schedule -&gt; the caller keeps the existing
        /// fixed cadence) for unsupported / single-constraint / tidal-collapse / unconstrained
        /// configs. The ANCHOR constraint (the tightest-tolerance one, via
        /// <see cref="SelectAnchorConstraintIndex"/> - the launch pad for a Mun mission) is locked
        /// EXACTLY; the remaining (other) constraints with a DISTINCT period must fall within their
        /// own physics tolerance at each launch. Anchoring on the tightest constraint MAXIMIZES the
        /// faithful-window frequency (the densest attainable cadence); <paramref name="minSpacingSeconds"/>
        /// then THROTTLES the schedule DOWN to the player's chosen relaunch period (0 = every faithful
        /// window = the maximum cadence). Degenerate other-periods (NaN / non-positive) are FILTERED
        /// (with a Warn) rather than read as spuriously satisfied. <paramref name="floorUT"/> = the
        /// first-play floor (max of the loop reference and spanEndUT); the first scheduled launch is at
        /// or after it. Pure apart from the degenerate-period Warn. See
        /// docs/dev/plans/zero-drift-reschedule.md sections 2/4.
        /// </summary>
        internal static bool TryBuildRelaunchSchedule(
            IReadOnlyList<PhaseConstraint> constraints,
            Support support,
            double ut0,
            double floorUT,
            IBodyInfo bodyInfo,
            out MissionRelaunchSchedule schedule,
            double minSpacingSeconds = 0.0,
            string launchBodyName = null,
            TransitedBodyRotationMode mode = TransitedBodyRotationMode.Tight,
            PhasingKnobInput knobInput = null)
        {
            schedule = null;
            if (support != Support.Supported)
                return false;
            if (double.IsNaN(ut0) || double.IsNaN(floorUT))
                return false;
            int rawCount = constraints?.Count ?? 0;
            if (rawCount < 2)
                return false; // need at least two constraints to drift

            // TransitedBodyRotationMode.Drop: exclude a TRANSITED (non-launch) body's rotation
            // constraint (e.g. a Mun landing). That body's phase is already pinned within its SOI by
            // its Orbital constraint, and the body-fixed landing self-anchors to the live surface, so
            // the tight 0.25 deg rotation lock only over-constrains the cadence. Loose/Tight keep it
            // (its tolerance comes from ScheduleToleranceSecondsFor below: loosened under Loose, the
            // normal tight 0.25 deg under Tight). The launch-body rotation (the pad) is never dropped.
            int droppedTransited = 0;
            var effective = new List<PhaseConstraint>(rawCount);
            for (int i = 0; i < rawCount; i++)
            {
                if (mode == TransitedBodyRotationMode.Drop
                    && IsTransitedBodyRotation(constraints[i], launchBodyName))
                {
                    droppedTransited++;
                    continue;
                }
                effective.Add(constraints[i]);
            }
            if (effective.Count < 2)
                return false; // dropping left too few constraints to drift

            // Anchor on the TIGHTEST-tolerance constraint (the pad), not the longest period (the Mun):
            // pinning the tightest exactly and letting the looser ones float within tolerance is what
            // maximizes the faithful-window frequency (plan section 2.2). The duty cycle (hence the
            // anchor choice) respects the mode (a Loose transited-body rotation is wider-band).
            int anchorIdx = SelectAnchorConstraintIndex(effective, bodyInfo, launchBodyName, mode);
            double anchorPeriod = effective[anchorIdx].PeriodSeconds;
            if (double.IsNaN(anchorPeriod) || double.IsInfinity(anchorPeriod) || anchorPeriod <= 0.0)
                return false;

            // M4b phasing-knob partition rules 3/4 (docs/dev/plans/mission-loiter-knob.md section
            // 3.2), evaluated on the SAME post-Drop effective list / anchor / mode-adjusted
            // tolerances the schedule itself uses. Rule 3: the anchor's reference event must lie
            // BEFORE the phasing run starts (the anchor is pinned exactly at k*T_anchor; an event
            // after the loiter would be moved off that pin by the rev shift). Failing a rule
            // disengages the knob and builds the schedule exactly as today (fail closed).
            bool knobEligible = knobInput != null;
            string knobDisengageReason = null;
            if (knobEligible)
            {
                double anchorEventUT = ut0 + effective[anchorIdx].PhaseOffsetSeconds;
                if (!(anchorEventUT < knobInput.RunStartUT - 1e-6))
                {
                    knobEligible = false;
                    knobDisengageReason = "anchor constraint's reference event is not before the phasing run";
                }
            }

            var periods = new List<double>(effective.Count - 1);
            var tolerances = new List<double>(effective.Count - 1);
            var shiftPeriods = new List<double>();
            var shiftTolerances = new List<double>();
            int filtered = 0;
            bool anyDistinct = false;
            for (int i = 0; i < effective.Count; i++)
            {
                if (i == anchorIdx)
                    continue;
                double p = effective[i].PeriodSeconds;
                if (double.IsNaN(p) || double.IsInfinity(p) || p <= 0.0)
                {
                    filtered++;
                    continue;
                }
                double tol = ScheduleToleranceSecondsFor(effective[i], bodyInfo, launchBodyName, mode);
                // Shiftable (plan 3.2): the constraint's reference event is at or after the phasing
                // run end, so the per-launch rev shift moves it by d*T_park. Everything earlier
                // (the pad, a pre-loiter event) is evaluated at the launch exactly as today.
                if (knobEligible && ut0 + effective[i].PhaseOffsetSeconds >= knobInput.RunEndUT - 1e-6)
                {
                    shiftPeriods.Add(p);
                    shiftTolerances.Add(tol);
                }
                else
                {
                    periods.Add(p);
                    tolerances.Add(tol);
                }
                if (Math.Abs(p - anchorPeriod) > PeriodEqualityRelTolerance * Math.Max(1.0, anchorPeriod))
                    anyDistinct = true;
            }

            if (filtered > 0 && !SuppressLogging)
                ParsekLog.Warn("MissionPeriodicity",
                    $"TryBuildRelaunchSchedule: filtered {filtered.ToString(CultureInfo.InvariantCulture)} " +
                    "non-anchor constraint(s) with a degenerate (NaN/non-positive) period; they are not " +
                    "scheduled (bad body data?)");

            // Rule 4: at least one shiftable constraint, else d has nothing to serve.
            if (knobEligible && shiftPeriods.Count == 0)
            {
                knobEligible = false;
                knobDisengageReason = "no shiftable constraint (no reference event after the phasing run)";
            }
            if (knobInput != null && !knobEligible && !SuppressLogging)
                ParsekLog.Verbose("MissionPeriodicity",
                    $"phasing knob disengaged: {knobDisengageReason}; schedule built without it");

            PhasingKnobConfig knobConfig = null;
            if (knobEligible)
            {
                knobConfig = new PhasingKnobConfig
                {
                    RunStartUT = knobInput.RunStartUT,
                    RunEndUT = knobInput.RunEndUT,
                    PeriodSeconds = knobInput.PeriodSeconds,
                    RecordedRevs = knobInput.RecordedRevs,
                    StaticCuts = knobInput.StaticCuts,
                    SpanSeconds = knobInput.SpanSeconds,
                    ShiftPeriods = shiftPeriods.ToArray(),
                    ShiftTolerances = shiftTolerances.ToArray(),
                    ShiftMin = 1 - knobInput.RecordedRevs,
                    ShiftMax = MaxExtraLoiterRevs,
                };
                if (!SuppressLogging)
                {
                    var kic = CultureInfo.InvariantCulture;
                    ParsekLog.Verbose("MissionPeriodicity",
                        $"phasing knob engaged: run=[{knobInput.RunStartUT.ToString("F0", kic)}," +
                        $"{knobInput.RunEndUT.ToString("F0", kic)}] " +
                        $"T={knobInput.PeriodSeconds.ToString("F1", kic)}s " +
                        $"R={knobInput.RecordedRevs.ToString(kic)} " +
                        $"staticCuts={(knobInput.StaticCuts?.Count ?? 0).ToString(kic)} " +
                        $"shiftable={shiftPeriods.Count.ToString(kic)} " +
                        $"keptRevsBounds=[1,{(knobInput.RecordedRevs + MaxExtraLoiterRevs).ToString(kic)}]");
                }
            }

            // No valid distinct-period other constraint -> single-constraint / tidal-collapse:
            // a uniform schedule == today's fixed cadence, so no schedule (keep fixed cadence).
            // With the knob engaged the shiftable group counts: a pad + station mission has its
            // only other constraint in the SHIFT group, and the schedule is exactly what gives the
            // knob its per-window seam.
            if ((periods.Count == 0 && shiftPeriods.Count == 0) || !anyDistinct)
                return false;
            if (periods.Count == 0 && knobConfig == null)
                return false; // shiftable-only without an engaged knob is the old empty-others case

            var candidate = new MissionRelaunchSchedule(
                ut0, anchorPeriod, periods.ToArray(), tolerances.ToArray(), floorUT,
                ScheduleLookaheadMultiples, minSpacingSeconds, knobConfig);
            if (double.IsNaN(candidate.FirstLaunchUT))
                return false; // could not resolve a first launch (degenerate)

            schedule = candidate;
            return true;
        }

        // True iff EVERY dropped constraint (all except dominantIdx) is within ITS OWN physics-derived
        // tolerance at the cadence <paramref name="step"/> (= multiple * dominantPeriod). Each
        // constraint's circular phase error at the step is compared against its own tolerance, so a
        // short-period constraint is never judged against a long-period constraint's larger tolerance
        // (which matters once 3+ constraints are dropped). Pure.
        internal static bool AllDroppedWithinTolerance(
            IReadOnlyList<PhaseConstraint> constraints, int dominantIdx, double step, IBodyInfo bodyInfo)
        {
            for (int i = 0; i < constraints.Count; i++)
            {
                if (i == dominantIdx)
                    continue;
                double err = CircularPhaseError(step, constraints[i].PeriodSeconds);
                if (err > ToleranceSecondsFor(constraints[i], bodyInfo))
                    return false;
            }
            return true;
        }

        internal static double ToleranceSecondsFor(PhaseConstraint c, IBodyInfo bodyInfo)
        {
            // VesselOrbital FIRST: a station has no SOI, so falling through to the Orbital
            // SoiRadius/OrbitalVelocity formula below (evaluated on the ORBITED body's name in
            // BodyName, e.g. Kerbin's own SOI width) would yield a wildly-wrong huge tolerance.
            // The station phase tolerance is 1 degree of its orbit (design note 5.3: ~12 km
            // along-track for a 100 km LKO orbit, inside what the recorded final approach absorbs
            // since the Relative section follows the live station anyway).
            if (c.Kind == ConstraintKind.VesselOrbital)
                return c.PeriodSeconds * (StationPhaseToleranceDegrees / 360.0);

            if (c.Kind == ConstraintKind.Rotation)
                return c.PeriodSeconds * RotationToleranceFraction;

            // Orbital: SoiRadius / OrbitalVelocity (the time the body crosses its own SOI).
            if (bodyInfo != null)
            {
                double soi = bodyInfo.SoiRadius(c.BodyName);
                double vel = bodyInfo.OrbitalVelocity(c.BodyName);
                if (!double.IsNaN(soi) && !double.IsNaN(vel) && vel > 0.0)
                    return soi / vel;
            }
            // Fallback: a small fraction of the orbital period.
            return c.PeriodSeconds * RotationToleranceFraction;
        }

        private static void LogSolve(
            string dominantBody,
            PeriodicitySolution sol,
            double ut0,
            double nowUT,
            string dominantMissed = null)
        {
            if (SuppressLogging)
                return;
            var ic = CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder(128);
            sb.Append("Solve: dominant=").Append(dominantBody ?? "<none>")
              .Append(" method=").Append(sol.Method ?? "?")
              .Append(" P=").Append(sol.P.ToString("R", ic))
              .Append(" ut0=").Append(ut0.ToString("R", ic))
              .Append(" now=").Append(nowUT.ToString("R", ic))
              .Append(" nextWindow=").Append(sol.NextWindowUT.ToString("R", ic))
              .Append(" residual=").Append(sol.ResidualSeconds.ToString("R", ic))
              .Append(" withinTol=").Append(sol.WithinTolerance ? "yes" : "no")
              .Append(" lock=").Append(sol.ShouldPhaseLock ? "yes" : "no")
              .Append(" support=").Append(sol.Support.ToString());
            if (!string.IsNullOrEmpty(dominantMissed))
                sb.Append(" missed=").Append(dominantMissed);
            ParsekLog.Verbose("MissionPeriodicity", sb.ToString());

            // Over-constrained signal (design Diagnostic Logging): Warn when the Tier-1 residual
            // exceeds tolerance so a config that cannot loop accurately is never a silent branch.
            if (sol.ShouldPhaseLock && !sol.WithinTolerance && sol.ResidualSeconds > 0.0)
            {
                ParsekLog.Warn("MissionPeriodicity",
                    $"Solve: Tier-1 residual {sol.ResidualSeconds.ToString("R", ic)}s exceeds tolerance " +
                    $"(dropped constraint {dominantMissed ?? "?"}); window is best-effort until the " +
                    "Phase-2 joint best-fit lands");
            }
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

        // Hard cap on the body-reference walk so a malformed planet-pack graph (a cycle) can never
        // hang the walk. Far deeper than any real Kerbol/RSS hierarchy (root -> planet -> moon is 3).
        private const int MaxBodyHierarchyDepth = 32;

        /// <summary>
        /// Walks the body-reference chain from <paramref name="bodyName"/> up to the root (the Sun has
        /// no parent, so <see cref="IBodyInfo.ReferenceBodyName"/> returns null there). Returns the chain
        /// ordered child-to-root INCLUDING the input body AND the root, e.g. for Ike:
        /// ["Ike", "Duna", "Sun"]; for the Sun itself: ["Sun"]. Empty when the body is null/unknown.
        /// Cycle-safe (stops at the first repeat or <see cref="MaxBodyHierarchyDepth"/>). Pure; reads
        /// only <see cref="IBodyInfo.ReferenceBodyName"/>. (Salvaged from the abandoned faithful-cross-
        /// parent PR #968; re-aim uses it to classify a recorded mission's SOI segments by body.)
        /// </summary>
        internal static List<string> AncestorChain(string bodyName, IBodyInfo bodyInfo)
        {
            var chain = new List<string>();
            if (string.IsNullOrEmpty(bodyName) || bodyInfo == null)
                return chain;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string cur = bodyName;
            while (!string.IsNullOrEmpty(cur) && seen.Add(cur) && chain.Count < MaxBodyHierarchyDepth)
            {
                chain.Add(cur);
                cur = bodyInfo.ReferenceBodyName(cur);
            }
            return chain;
        }

        /// <summary>
        /// Finds the lowest common ancestor (LCA) of two bodies in the reference-body graph. On success
        /// returns true and the LCA plus each side's downward chain to (but EXCLUDING) it, each
        /// INCLUDING its own endpoint body:
        ///   - commonAncestor: "Sun" for Kerbin/Duna, "Kerbin" for Kerbin/Mun, "Eve" for Eve/Gilly.
        ///   - launchToAncestor: [launchBody, ..., direct child of the LCA]; EMPTY when the launch body
        ///     IS the LCA (the same-parent case, e.g. [] for Kerbin/Mun).
        ///   - targetToAncestor: [targetBody, ..., direct child of the LCA], e.g. [Mun] for Kerbin/Mun,
        ///     [Duna] for Kerbin/Duna, [Ike, Duna] for Kerbin/Ike.
        /// Returns false (and empty out lists) when the two chains are disconnected (a planet-pack with
        /// unrelated roots). Pure. (Salvaged from PR #968.) Re-aim uses the result to tell whether a
        /// mission is same-parent (LCA == launch body -> faithful replay) or cross-parent (LCA deeper
        /// -> re-aim), and to identify the heliocentric leg (the LCA-bodied segment) to re-synthesize.
        /// </summary>
        internal static bool TryFindCommonAncestor(
            string launchBody, string targetBody, IBodyInfo bodyInfo,
            out string commonAncestor, out List<string> launchToAncestor, out List<string> targetToAncestor)
        {
            commonAncestor = null;
            launchToAncestor = new List<string>();
            targetToAncestor = new List<string>();
            if (string.IsNullOrEmpty(launchBody) || string.IsNullOrEmpty(targetBody) || bodyInfo == null)
                return false;

            List<string> launchChain = AncestorChain(launchBody, bodyInfo);
            List<string> targetChain = AncestorChain(targetBody, bodyInfo);
            if (launchChain.Count == 0 || targetChain.Count == 0)
                return false;

            var targetSet = new HashSet<string>(targetChain, StringComparer.Ordinal);
            string anc = null;
            for (int i = 0; i < launchChain.Count; i++)
            {
                if (targetSet.Contains(launchChain[i]))
                {
                    anc = launchChain[i];
                    break;
                }
            }
            if (anc == null)
                return false; // disconnected body graph (unrelated roots)

            commonAncestor = anc;
            for (int i = 0; i < launchChain.Count && launchChain[i] != anc; i++)
                launchToAncestor.Add(launchChain[i]);
            for (int i = 0; i < targetChain.Count && targetChain[i] != anc; i++)
                targetToAncestor.Add(targetChain[i]);
            return true;
        }

        /// <summary>
        /// One distinct vessel anchor collected from the included windows' Relative sections
        /// (keyed by anchorVesselId in the collection dictionary). Several Relative sections to
        /// the SAME pid collapse to the earliest overlap UT (timeline rigidity, design note 5.2).
        /// </summary>
        internal struct VesselAnchorInfo
        {
            /// <summary>The anchorVesselId as RECORDED on the section(s). 0 for the
            /// anchor-recording-only shape: the recorder deliberately zeroes the pid whenever it
            /// stamps an anchorRecordingId (FlightRecorder serialization checkpoints), which is the
            /// NORMAL recorded shape when the anchor vessel is itself recorded - the classifier
            /// resolves the pid through the committed anchor recording.</summary>
            public uint SectionPid;

            /// <summary>The earliest UT any Relative section to this anchor overlaps the
            /// included window - the FIRST rendezvous, the one the constraint aligns.</summary>
            public double EarliestUT;

            /// <summary>The anchor recording id carried by the section(s), when recorded. The
            /// pid-resolution source for the anchor-recording-only shape and the D3 drift
            /// comparison source; NEVER a period derivation source (design note 3.2).</summary>
            public string AnchorRecordingId;

            /// <summary>Identity of the member recording CARRYING the sections. The dock merge
            /// pulls the foreign partner's segments into the tree with MUTUAL anchoring (the
            /// partner's sections anchor the mission's own craft), so a section whose anchor
            /// resolves to the mission's SELF launch line is REATTRIBUTED to its owner: the
            /// owner is the partner the rendezvous was with, at that section's UT.</summary>
            public uint OwnerPid;
            public string OwnerGuid;
            public string OwnerRecordingId;
        }

        /// <summary>
        /// Collects the vessel anchors of a member's Relative-frame TrackSections that overlap
        /// the included window (rendezvous / dock). A non-loop Relative section carries an
        /// anchorRecordingId / anchorVesselId; that is an alignment to a vessel, not a body.
        /// Parent-anchored debris is NOT a rendezvous (it rides its own parent and is excluded
        /// from mission legs upstream), so this only collects from a controlled member that
        /// records a Relative section to a foreign anchor inside its window. Entries are keyed
        /// by the recorded identity ("pid:N" or, for the recorder's anchor-recording-only shape
        /// where the pid is deliberately zeroed, "rec:id"); the classifier resolves both forms
        /// to a live pid and merges them. Pure over its inputs.
        /// </summary>
        internal static void CollectVesselAnchorsWithinWindow(
            Recording rec,
            GhostPlaybackLogic.LoopUnit.MemberWindow win,
            Dictionary<string, VesselAnchorInfo> anchors)
        {
            // A genuine debris/parent-anchored recording is not a mission leg (debris excluded
            // upstream) and its Relative sections anchor to its own parent - never treat those as
            // a rendezvous.
            if (!string.IsNullOrEmpty(rec.ParentAnchorRecordingId))
                return;
            if (rec.TrackSections == null)
                return;
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
                if (!anchoredToVessel)
                    continue;
                // Keyed per (owner member, recorded anchor identity): the classifier reattributes
                // self-anchored entries to their OWNER, so entries must not merge across owners.
                string key = "o:" + rec.RecordingId + "|" + (sec.anchorVesselId != 0
                    ? "pid:" + sec.anchorVesselId.ToString(CultureInfo.InvariantCulture)
                    : "rec:" + sec.anchorRecordingId);
                if (anchors.TryGetValue(key, out VesselAnchorInfo existing))
                {
                    bool earlier = segStart < existing.EarliestUT;
                    if (earlier)
                        existing.EarliestUT = segStart;
                    // Prefer the earliest section's recording id; backfill when still empty.
                    if (!string.IsNullOrEmpty(sec.anchorRecordingId)
                        && (earlier || string.IsNullOrEmpty(existing.AnchorRecordingId)))
                        existing.AnchorRecordingId = sec.anchorRecordingId;
                    anchors[key] = existing;
                }
                else
                {
                    anchors[key] = new VesselAnchorInfo
                    {
                        SectionPid = sec.anchorVesselId,
                        EarliestUT = segStart,
                        AnchorRecordingId = sec.anchorRecordingId,
                        OwnerPid = rec.VesselPersistentId,
                        OwnerGuid = rec.RecordedVesselGuid,
                        OwnerRecordingId = rec.RecordingId
                    };
                }
            }
        }

        /// <summary>The outcome of the M4a rendezvous classification.</summary>
        internal enum VesselOrbitalClassification
        {
            /// <summary>Exactly one foreign anchor: the VesselOrbital constraint was emitted.</summary>
            Emitted,
            /// <summary>Every vessel-anchored section was an intra-mission relative pair (the
            /// mission's own segments anchoring each other, e.g. both dock partners included):
            /// no foreign target exists, no constraint, Support untouched.</summary>
            NoForeignAnchor,
            /// <summary>Fail closed: Support becomes UnsupportedRendezvous with the reason.</summary>
            Rejected
        }

        /// <summary>
        /// M4a classifier (design note 5.2 / D7, self-partition revision from the 2026-06-11
        /// playtest): emits the VesselOrbital constraint for the SUPPORTED rendezvous shape -
        /// exactly ONE foreign same-parent closed-orbit vessel - or fails closed with a reason.
        /// The dock merge pulls the foreign partner's segments into the tree with MUTUAL
        /// anchoring (partner sections anchor the mission's own craft; craft sections anchor the
        /// partner), so raw anchors are first PARTITIONED against the mission's SELF launch line
        /// (<paramref name="selfPid"/>/<paramref name="selfGuid"/>, the earliest member's
        /// identity): a section whose anchor resolves to SELF is REATTRIBUTED to its owning
        /// member's vessel (the partner the rendezvous was with, at that section's UT); a
        /// foreign-anchored section is a direct target. Both directions of one rendezvous merge
        /// on the resolved identity (guid-gated, craft-baked pids are not launch-unique), keeping
        /// the EARLIEST rendezvous UT. Rules, in order:
        /// 0. null launch body -&gt; Rejected;
        /// 1. resolve + partition + merge: unresolvable anchor recording ids, pid-less partner
        ///    members, or 2+ DISTINCT foreign identities -&gt; Rejected; all-self -&gt;
        ///    NoForeignAnchor (not a reject);
        /// 2. the target does not resolve to a live closed orbit of the SAME launch -&gt; Rejected;
        /// 3. the target must orbit a body the mission's included window actually transits: the
        ///    launch body (the M4a LKO-resupply shape) or any body carrying an emitted Orbital
        ///    constraint in <paramref name="bodyConstraints"/> (M4c Tier 2: a depot around the
        ///    destination). A station around a body with no Orbital constraint has no alignment
        ///    basis -&gt; Rejected;
        /// 4. else Emitted (period = LIVE target period, offset = earliest rendezvous UT - ut0).
        ///    Emission NEVER touches Support, so a same-parent destination (Mun depot) stays
        ///    Supported and feeds the zero-drift schedule, while a cross-parent destination
        ///    (Duna depot) stays UnsupportedCrossParent and feeds the re-aim arrival hold.
        /// <paramref name="driftAmberReason"/> is the display-only D3 surface (set only on emit);
        /// never affects Support. Pure.
        /// </summary>
        internal static VesselOrbitalClassification ClassifyVesselOrbitalConstraint(
            Dictionary<string, VesselAnchorInfo> anchors,
            string launchBody,
            double ut0,
            uint selfPid,
            string selfGuid,
            IBodyInfo bodyInfo,
            IReadOnlyList<Recording> committed,
            IReadOnlyList<PhaseConstraint> bodyConstraints,
            out PhaseConstraint constraint,
            out string rejectReason,
            out string driftAmberReason)
        {
            constraint = default;
            rejectReason = null;
            driftAmberReason = null;

            // Rule 0: no launch body (an orbit-only / degenerate config) - fail closed; the
            // same-parent check below is meaningless without it.
            if (string.IsNullOrEmpty(launchBody))
            {
                rejectReason = "rendezvous with no launch body to phase against (orbit-only config)";
                return VesselOrbitalClassification.Rejected;
            }

            // Rule 1: resolve each entry's recorded anchor identity, partition against the SELF
            // launch line, and merge the surviving foreign candidates.
            uint pid = 0;
            string recordedGuid = null;
            double earliestUT = double.PositiveInfinity;
            string compareRecordingId = null;
            bool haveCandidate = false;
            foreach (var kv in anchors)
            {
                VesselAnchorInfo raw = kv.Value;
                uint anchorPid = raw.SectionPid;
                string anchorGuid = null;
                if (anchorPid == 0)
                {
                    Recording anchorRec = FindCommittedRecordingById(committed, raw.AnchorRecordingId);
                    if (anchorRec == null)
                    {
                        rejectReason =
                            $"rendezvous anchor recording '{raw.AnchorRecordingId}' not in the " +
                            "committed set (cannot resolve the live anchor)";
                        return VesselOrbitalClassification.Rejected;
                    }
                    if (anchorRec.VesselPersistentId == 0)
                    {
                        rejectReason =
                            $"rendezvous anchor recording '{raw.AnchorRecordingId}' carries no " +
                            "vessel pid (cannot resolve the live anchor)";
                        return VesselOrbitalClassification.Rejected;
                    }
                    anchorPid = anchorRec.VesselPersistentId;
                    anchorGuid = anchorRec.RecordedVesselGuid;
                }
                else if (!string.IsNullOrEmpty(raw.AnchorRecordingId))
                {
                    // A pid-stamped section that also names the anchor recording: harvest the
                    // launch guid for the identity gates when the recorded pids agree.
                    Recording anchorRec = FindCommittedRecordingById(committed, raw.AnchorRecordingId);
                    if (anchorRec != null && anchorRec.VesselPersistentId == anchorPid)
                        anchorGuid = anchorRec.RecordedVesselGuid;
                }

                // Partition: a SELF-anchored section lives on a PARTNER member (the dock merge's
                // mutual anchoring) - the rendezvous target is the OWNER's vessel at that UT.
                uint candPid;
                string candGuid;
                string candRecId;
                bool anchorIsSelf = selfPid != 0 && anchorPid == selfPid
                    && !VesselLaunchIdentity.GuidsConclusivelyDiffer(selfGuid, anchorGuid);
                if (anchorIsSelf)
                {
                    bool ownerIsSelf = raw.OwnerPid == selfPid
                        && !VesselLaunchIdentity.GuidsConclusivelyDiffer(selfGuid, raw.OwnerGuid);
                    if (ownerIsSelf)
                        continue; // an intra-self relative pair carries no foreign target
                    if (raw.OwnerPid == 0)
                    {
                        rejectReason =
                            $"rendezvous partner member '{raw.OwnerRecordingId}' carries no " +
                            "vessel pid (cannot resolve the live partner)";
                        return VesselOrbitalClassification.Rejected;
                    }
                    candPid = raw.OwnerPid;
                    candGuid = raw.OwnerGuid;
                    candRecId = raw.OwnerRecordingId;
                }
                else
                {
                    candPid = anchorPid;
                    candGuid = anchorGuid;
                    candRecId = raw.AnchorRecordingId;
                }

                if (!haveCandidate)
                {
                    haveCandidate = true;
                    pid = candPid;
                    recordedGuid = candGuid;
                    earliestUT = raw.EarliestUT;
                    compareRecordingId = candRecId;
                }
                else if (candPid == pid
                    && !VesselLaunchIdentity.GuidsConclusivelyDiffer(recordedGuid, candGuid))
                {
                    // Both directions of the same rendezvous (and any later re-rendezvous): keep
                    // the FIRST rendezvous UT and its comparison recording. The recId backfill
                    // from a LATER entry is safe even though earliestUT comes from an earlier
                    // one: merged entries are guid-gated to the SAME vessel, so any merged
                    // recording is a valid drift-comparison source, and ComputeDriftAmberReason
                    // only compares when that recording has a segment COVERING earliestUT
                    // (otherwise null = no amber, the best available outcome when the earliest
                    // entry carried no recording id).
                    if (raw.EarliestUT < earliestUT)
                    {
                        earliestUT = raw.EarliestUT;
                        if (!string.IsNullOrEmpty(candRecId))
                            compareRecordingId = candRecId;
                    }
                    if (string.IsNullOrEmpty(compareRecordingId))
                        compareRecordingId = candRecId;
                    if (string.IsNullOrEmpty(recordedGuid))
                        recordedGuid = candGuid;
                }
                else
                {
                    rejectReason = candPid == pid
                        ? $"two rendezvous anchors share pid={pid.ToString(CultureInfo.InvariantCulture)} " +
                          "but are conclusively different launches (multi-rendezvous)"
                        : "multiple distinct vessel anchors (multi-rendezvous)";
                    return VesselOrbitalClassification.Rejected;
                }
            }
            if (!haveCandidate)
                return VesselOrbitalClassification.NoForeignAnchor;

            // Rule 2: the target must EXIST in the save with a closed orbit AND be the recorded
            // LAUNCH (guid-gated: a craft-baked pid reused by a different launch of the same
            // craft must not read as the recorded station; design D1 / 3.2). Never derive a
            // period from the anchor RECORDING's OrbitSegments - a window computed from recorded
            // data while the live anchor is gone advertises an alignment whose approach member
            // loop playback skips/retires.
            if (bodyInfo == null
                || !bodyInfo.TryGetVesselOrbit(pid, recordedGuid, out double livePeriod, out string orbitBodyName))
            {
                rejectReason =
                    $"rendezvous anchor vessel pid={pid.ToString(CultureInfo.InvariantCulture)} " +
                    "not in save / no closed orbit (or a different launch of the same craft)";
                return VesselOrbitalClassification.Rejected;
            }

            // Rule 3 (M4c split): the station must orbit a body the mission's included window
            // actually TRANSITS - the launch body (M4a: the LKO-resupply shape) or any body with
            // an emitted Orbital constraint (M4c Tier 2: a depot around the destination). The
            // emit below never touches Support, so a same-parent destination (Mun depot) stays
            // Supported and the zero-drift schedule + M4b knob align the station, while a
            // cross-parent destination (Duna depot) stays UnsupportedCrossParent and the re-aim
            // arrival hold aligns it (T_station for T_rot). A station around a body with no
            // Orbital constraint (never transited, or transited with degenerate orbit data, e.g.
            // a Sun-orbiting depot - OrbitPeriod(Sun) is degenerate) has no alignment basis:
            // fail closed.
            if (orbitBodyName != launchBody
                && !HasOrbitalConstraintForBody(bodyConstraints, orbitBodyName))
            {
                rejectReason =
                    $"rendezvous anchor pid={pid.ToString(CultureInfo.InvariantCulture)} orbits " +
                    $"'{orbitBodyName ?? "?"}', for which the mission emitted no Orbital " +
                    "constraint (not transited, or degenerate orbit data)";
                return VesselOrbitalClassification.Rejected;
            }

            // Rule 4: emit. The LIVE period is the alignment truth (design D1/D3).
            constraint = new PhaseConstraint
            {
                Kind = ConstraintKind.VesselOrbital,
                BodyName = orbitBodyName,
                PeriodSeconds = livePeriod,
                PhaseOffsetSeconds = earliestUT - ut0,
                RelativeToParent = false,
                AnchorVesselPid = pid
            };
            driftAmberReason = ComputeDriftAmberReason(
                compareRecordingId, earliestUT, livePeriod, bodyInfo, committed);
            return VesselOrbitalClassification.Emitted;
        }

        /// <summary>True when the constraint list carries an Orbital constraint for
        /// <paramref name="bodyName"/> - the classifier's rule-3 "transited body" test (the
        /// Orbital list is complete before the rendezvous classification runs; the launch body
        /// is checked separately because its own orbit segments are never an SOI entry). Pure.</summary>
        internal static bool HasOrbitalConstraintForBody(
            IReadOnlyList<PhaseConstraint> constraints, string bodyName)
        {
            if (constraints == null || string.IsNullOrEmpty(bodyName))
                return false;
            for (int i = 0; i < constraints.Count; i++)
            {
                PhaseConstraint c = constraints[i];
                if (c.Kind == ConstraintKind.Orbital && c.BodyName == bodyName)
                    return true;
            }
            return false;
        }

        /// <summary>The committed recording with the given RecordingId, or null. The anchor
        /// resolution + drift-comparison lookup (rendezvous Relative sections reference their
        /// anchor by recording id; the recorder zeroes the section pid in that shape).</summary>
        internal static Recording FindCommittedRecordingById(
            IReadOnlyList<Recording> committed, string recordingId)
        {
            if (committed == null || string.IsNullOrEmpty(recordingId))
                return null;
            for (int i = 0; i < committed.Count; i++)
            {
                Recording r = committed[i];
                if (r != null && r.RecordingId == recordingId)
                    return r;
            }
            return null;
        }

        // D3 drift amber (design note 3.4): the relative period delta past which the live anchor
        // orbit is flagged as drifted from the recorded rendezvous-time orbit. 2%: a period delta
        // that accumulates a full tolerance-width of phase error within ~one cadence.
        internal const double StationDriftAmberRelTolerance = 0.02;

        /// <summary>
        /// The D3 drift-amber reason, or null when no comparison is possible / the drift is within
        /// tolerance. Compares the LIVE anchor period against the RECORDED rendezvous-time orbit:
        /// the anchor recording (when <paramref name="anchorRecordingId"/> resolves in
        /// <paramref name="committed"/>) supplies the non-predicted OrbitSegment covering the
        /// recorded rendezvous UT, whose period comes from its semi-major axis + the segment
        /// body's mu (<see cref="Reaim.ReaimLoiterCompressor.OrbitalPeriod"/>). No recording / no
        /// covering segment / unknown mu -&gt; no comparison, no amber. Display-only: never affects
        /// Support or the emitted (live) period. Pure.
        /// </summary>
        internal static string ComputeDriftAmberReason(
            string anchorRecordingId,
            double rendezvousUT,
            double livePeriodSeconds,
            IBodyInfo bodyInfo,
            IReadOnlyList<Recording> committed)
        {
            if (string.IsNullOrEmpty(anchorRecordingId) || committed == null || bodyInfo == null)
                return null;
            if (double.IsNaN(livePeriodSeconds) || double.IsInfinity(livePeriodSeconds)
                || livePeriodSeconds <= 0.0)
                return null;

            Recording anchorRec = FindCommittedRecordingById(committed, anchorRecordingId);
            if (anchorRec == null || anchorRec.OrbitSegments == null)
                return null;

            for (int i = 0; i < anchorRec.OrbitSegments.Count; i++)
            {
                OrbitSegment seg = anchorRec.OrbitSegments[i];
                if (seg.isPredicted)
                    continue;
                if (seg.startUT > rendezvousUT || seg.endUT < rendezvousUT)
                    continue;
                double recordedPeriod = Reaim.ReaimLoiterCompressor.OrbitalPeriod(
                    seg.semiMajorAxis, bodyInfo.GravParameter(seg.bodyName));
                if (double.IsNaN(recordedPeriod) || recordedPeriod <= 0.0)
                    return null; // non-elliptical / unknown-mu recorded segment: no comparison
                double relDelta = Math.Abs(recordedPeriod - livePeriodSeconds) / livePeriodSeconds;
                if (relDelta > StationDriftAmberRelTolerance)
                {
                    return "station orbit drifted ~" +
                        (relDelta * 100.0).ToString("0.0", CultureInfo.InvariantCulture) +
                        "% since recording";
                }
                return null;
            }
            return null; // no covering non-predicted segment: no comparison
        }

        // D3 drift-amber transition log: last reason per mission tag, so the set/clear/changed
        // Info line fires once per transition, not per build (the UI re-extracts every frame with
        // SuppressLogging set; suppressed extractions neither log nor consume the transition, so
        // the next unsuppressed build still reports it).
        private static readonly Dictionary<string, string> lastDriftAmberReasonByTag =
            new Dictionary<string, string>(StringComparer.Ordinal);

        internal static void ResetDriftAmberLogForTesting()
        {
            lastDriftAmberReasonByTag.Clear();
        }

        private static void LogDriftAmberTransition(string missionTag, string reason)
        {
            if (SuppressLogging)
                return;
            lastDriftAmberReasonByTag.TryGetValue(missionTag, out string prev);
            if (string.Equals(prev, reason, StringComparison.Ordinal))
                return;
            lastDriftAmberReasonByTag[missionTag] = reason;
            ParsekLog.Info("MissionPeriodicity",
                reason != null
                    ? $"Drift amber SET: {missionTag} {reason}"
                    : $"Drift amber CLEARED: {missionTag}");
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
    /// A zero-drift relaunch schedule for one phase-locked, drifting (multi-constraint
    /// incommensurate) looping Mission. Immutable inputs + a lazily-extended cache of the
    /// non-uniform relaunch UTs; pure over the snapshotted inputs (reads no live state), so the
    /// engine's and the UI's separately-built copies produce identical schedules. Main-thread only
    /// (KSP/Unity is single-threaded), so the mutable cache needs no locking; this object is held
    /// as a nullable field on the immutable <see cref="GhostPlaybackLogic.LoopUnit"/> struct - struct
    /// copies share the one cache object, which is the intended aliasing. The launches are
    /// <c>UT0 + k*anchorPeriod</c> for an increasing, non-uniform sequence of anchor-multiples k
    /// (each with every OTHER constraint within tolerance when reachable, else bounded-best),
    /// generated by <see cref="MissionPeriodicity.TryFindNextScheduleK"/>. The anchor is the
    /// tightest-tolerance constraint (the pad), so this is the DENSEST attainable cadence;
    /// <see cref="MinSpacingSeconds"/> throttles it down to the player's chosen relaunch period
    /// (0 = every faithful window = the maximum cadence). See
    /// docs/dev/plans/zero-drift-reschedule.md section 3.
    /// </summary>
    /// <summary>
    /// Builder-side input for the M4b phasing-loiter knob: the detected phasing run (the LAST
    /// compressible parking loiter ending before the rendezvous/SOI guard), the static cuts for
    /// EARLIER compressible runs (keepRevs = 1, identical every launch), and the recorded span
    /// (for the per-launch non-overlap spacing). Built by MissionLoopUnitBuilder after its
    /// engagement checks; <see cref="MissionPeriodicity.TryBuildRelaunchSchedule"/> turns it into a
    /// <see cref="PhasingKnobConfig"/> by partitioning the constraint set (rules 3/4 of the plan).
    /// </summary>
    internal sealed class PhasingKnobInput
    {
        public double RunStartUT;
        public double RunEndUT;
        public double PeriodSeconds;     // T_park
        public long RecordedRevs;        // R
        public IReadOnlyList<GhostPlaybackLogic.LoopCut> StaticCuts; // earlier runs; sorted; all end before RunStartUT
        public double SpanSeconds;       // recorded span duration (spacing under extension)
    }

    /// <summary>
    /// The resolved knob configuration a <see cref="MissionRelaunchSchedule"/> solves with: the
    /// phasing run plus the SHIFTABLE constraint partition (reference event after the run end, so a
    /// loiter re-time of d revs moves it by d * T_park). Constructed only by
    /// <see cref="MissionPeriodicity.TryBuildRelaunchSchedule"/> when every engagement rule holds.
    /// </summary>
    internal sealed class PhasingKnobConfig
    {
        public double RunStartUT;
        public double RunEndUT;
        public double PeriodSeconds;
        public long RecordedRevs;
        public IReadOnlyList<GhostPlaybackLogic.LoopCut> StaticCuts;
        public double SpanSeconds;
        public double[] ShiftPeriods;
        public double[] ShiftTolerances;
        public long ShiftMin;            // 1 - RecordedRevs (keep at least the final rev)
        public long ShiftMax;            // +MaxExtraLoiterRevs
    }

    /// <summary>
    /// Per-launch loop-clock timing for one scheduled launch of a knob-engaged mission: the kept
    /// rev count k_N, the residual/tolerance verdict, and the PRECOMPUTED cut list + extension the
    /// span clock applies for that launch. TRANSIENT like the schedule that owns it (never
    /// persisted; rebuilt on every unit build). Cuts are recorded-UT windows (sorted ascending);
    /// the extension wraps the LAST recorded rev of the phasing run, which is cut-free by
    /// construction (the phasing cut exists only when d &lt; 0 and ends at least one whole rev
    /// before the run end).
    /// </summary>
    internal struct LaunchTimingEntry
    {
        public long KeptRevs;                  // k_N = RecordedRevs + d
        public double ResidualSeconds;         // combined worst residual at the chosen (k, d)
        public bool WithinTolerance;
        public IReadOnlyList<GhostPlaybackLogic.LoopCut> Cuts; // static cuts (+ phasing cut when d < 0)
        public double ExtensionSeconds;        // d > 0 ? d * T_park : 0
        public double ExtensionWrapStartUT;    // RunEndUT - T_park (recorded; valid when ExtensionSeconds > 0)
        public double ExtensionWrapPeriod;     // T_park
        public double EffectiveSpanSeconds;    // span - totalCut + extension (per-launch spacing)
    }

    internal sealed class MissionRelaunchSchedule
    {
        // Eagerly probe this many launches at construction to determine MinIntervalSeconds (the
        // builder's overlap-REJECT estimate). A bounded prefix; the rest is generated lazily. This
        // only feeds the builder's decision whether to ATTACH the schedule (reject if MinInterval <
        // span); the non-overlap INVARIANT itself does not depend on it, because the builder sets
        // OverlapCadenceSeconds = max(span, MinInterval) >= span UNCONDITIONALLY, so a scheduled
        // unit's UnitMemberOverlaps is false regardless of any later interval. A larger prefix just
        // makes the reject estimate tighter.
        private const int MinIntervalProbeLaunches = 8;

        private readonly double ut0;
        private readonly double anchorPeriod;
        private readonly double[] otherPeriods;
        private readonly double[] otherTolerances;
        private readonly double floorUT;
        private readonly int lookaheadMultiples;
        private readonly double minSpacing;   // player throttle: relaunches are >= this far apart

        // M4b phasing-loiter knob: when non-null, every launch is resolved with the shiftable-group
        // scan and materializes a LaunchTimingEntry in `timings` (index-aligned with `launches`).
        private readonly PhasingKnobConfig knob;
        private readonly List<LaunchTimingEntry> timings = new List<LaunchTimingEntry>();

        // Cached relaunch UTs in increasing order (launches[0] == FirstLaunchUT). Grown on demand.
        private readonly List<double> launches = new List<double>();
        private long lastK;        // anchor-multiple index of the last cached launch
        private bool capWarned;     // rate-limit the safety-cap Warn

        // R3 tolerance accounting over the cached prefix (docs/dev/plans/zero-drift-reschedule-hardening.md
        // section 6 R2/R3). TryFindNextScheduleK reports per-launch withinTolerance + the worst
        // other-constraint residual; ExtendOnce (and the constructor's L_0 resolve) previously dropped
        // both via `out _`. We now FOLD them into these running aggregates as the cache grows, so the UI
        // can tint the T- countdown amber off the SCHEDULE's own worst launch (a genuinely over-tolerance
        // bounded-best launch) instead of the fixed-fit Solution.WithinTolerance (which is false for the
        // in-tolerance stock Mun). Additive + cheap (two scalar updates per resolved launch); nothing is
        // serialized (the schedule is always re-derived).
        private bool allLaunchesWithinTolerance = true;  // true unless some resolved launch was bounded-best
        private double worstResidualSeconds;             // max worst-other residual over the cached prefix

        /// <summary>The first scheduled relaunch UT (= the unit's phase anchor). NaN if none could
        /// be resolved (degenerate inputs).</summary>
        internal double FirstLaunchUT { get; }

        /// <summary>The minimum relaunch interval over the eager prefix. Defensive: the span-floored
        /// throttle guarantees every interval is &gt;= span, so this is &gt;= span by construction (the
        /// overlap-reject gate, kept as a belt-and-suspenders check, always passes for a built
        /// schedule). NOT a good user-facing display value because consecutive faithful k's often hit
        /// the SAME small gap (~13 anchor periods) across very different cadence regimes - use
        /// <see cref="AverageIntervalSeconds"/> for the period cell instead.</summary>
        internal double MinIntervalSeconds { get; }

        /// <summary>The MEAN relaunch interval over the eager prefix - the representative cadence the
        /// UI shows in the period cell. Unlike <see cref="MinIntervalSeconds"/> this reflects the
        /// TYPICAL gap (the schedule's actual pace), so the cell visibly differs between modes (e.g.
        /// the transited-body rotation A/B): Drop reads as days, Loose as weeks/months, Tight as
        /// years.</summary>
        internal double AverageIntervalSeconds { get; }

        /// <summary>The MAXIMUM relaunch interval over the same eager prefix that
        /// <see cref="MinIntervalSeconds"/> / <see cref="AverageIntervalSeconds"/> sample. Paired with
        /// the min, this gives the UI a min-max RANGE for the "varies" period cell ("~13d-1mo") so the
        /// player sees the loop's general cadence band, not a single mean. Like Min/Average it is the
        /// PREFIX max (bounded by <see cref="MinIntervalProbeLaunches"/>), an approximation of the
        /// early cadence, not a global guarantee. Falls back to the anchor period when only one launch
        /// resolved (no gap to measure). Transient; never serialized.</summary>
        internal double MaxIntervalSeconds { get; }

        /// <summary>The player throttle (the requested relaunch period). 0 = every faithful window
        /// (the maximum attainable cadence).</summary>
        internal double MinSpacingSeconds => minSpacing;

        /// <summary>
        /// True iff EVERY launch resolved so far (over the lazily-grown cached prefix) found a
        /// within-tolerance window; false once any launch fell to the bounded-best fallback (no within-tol
        /// k in <see cref="MissionPeriodicity.ScheduleLookaheadMultiples"/>), i.e. a genuinely
        /// OVER-tolerance launch. This is the SCHEDULE's own worst-launch tolerance flag (R3): the UI tints
        /// the T- countdown amber off THIS, not the fixed m*P-fit <c>PeriodicitySolution.WithinTolerance</c>
        /// (which is false for the stock Mun whose ACTUAL scheduled launches are within tolerance by
        /// construction). Grows monotonically more pessimistic as the cache extends (it can only flip
        /// true -&gt; false, never back), so a far-future warp that uncovers a bounded-best launch keeps the
        /// flag false. See docs/dev/plans/zero-drift-reschedule-hardening.md section 6 R2/R3.
        /// </summary>
        internal bool AllLaunchesWithinTolerance => allLaunchesWithinTolerance;

        /// <summary>
        /// The worst other-constraint phase residual (seconds) over the cached prefix - the largest amount
        /// any non-anchor constraint missed its recorded position by, across the launches resolved so far.
        /// 0 when no launch has been resolved or every residual was 0. Diagnostic companion to
        /// <see cref="AllLaunchesWithinTolerance"/>; grows monotonically as the cache extends.
        /// </summary>
        internal double WorstResidualSeconds => worstResidualSeconds;

        internal MissionRelaunchSchedule(
            double ut0, double anchorPeriod,
            double[] otherPeriods, double[] otherTolerances,
            double floorUT, int lookaheadMultiples, double minSpacingSeconds = 0.0,
            PhasingKnobConfig knobConfig = null)
        {
            this.ut0 = ut0;
            this.anchorPeriod = anchorPeriod;
            this.otherPeriods = otherPeriods ?? System.Array.Empty<double>();
            this.otherTolerances = otherTolerances ?? System.Array.Empty<double>();
            this.floorUT = floorUT;
            this.lookaheadMultiples = lookaheadMultiples;
            this.minSpacing = (double.IsNaN(minSpacingSeconds) || minSpacingSeconds < 0.0) ? 0.0 : minSpacingSeconds;
            this.knob = knobConfig;
            FirstLaunchUT = double.NaN;
            MinIntervalSeconds = double.NaN;
            MaxIntervalSeconds = double.NaN;

            if (double.IsNaN(ut0) || double.IsNaN(floorUT)
                || double.IsNaN(anchorPeriod) || double.IsInfinity(anchorPeriod) || anchorPeriod <= 0.0)
                return;

            // L_0: first qualifying k with UT0 + k*anchorPeriod at or after the first-play floor
            // (ceil-based kStart so a launch exactly at the floor is included), k >= 1. The throttle
            // does NOT apply to L_0 (there is no previous relaunch); it spaces SUBSEQUENT launches.
            long kFloor = (long)Math.Ceiling((floorUT - ut0) / anchorPeriod - 1e-6);
            if (kFloor < 1)
                kFloor = 1;
            if (!ResolveLaunch(kFloor, out long k0, out double r0, out bool w0))
                return;
            launches.Add(ut0 + k0 * anchorPeriod);
            lastK = k0;
            FirstLaunchUT = launches[0];
            FoldLaunchTolerance(r0, w0);  // R3: account L_0's tolerance

            // Eager prefix to determine BOTH the min interval (defensive gate, see MinIntervalSeconds)
            // and the MEAN interval (the user-facing display cadence). Min often coincides across
            // modes when consecutive faithful k's hit the same small gap, so it is NOT representative;
            // mean over the prefix is.
            double minInterval = double.PositiveInfinity;
            double maxInterval = double.NegativeInfinity;
            for (int i = 0; i < MinIntervalProbeLaunches; i++)
            {
                if (!ExtendOnce())
                    break;
                double interval = launches[launches.Count - 1] - launches[launches.Count - 2];
                if (interval < minInterval)
                    minInterval = interval;
                if (interval > maxInterval)
                    maxInterval = interval;
            }
            MinIntervalSeconds = double.IsPositiveInfinity(minInterval) ? anchorPeriod : minInterval;
            // Max over the same prefix; falls back to the anchor period when no gap was measured
            // (a single resolved launch), matching MinIntervalSeconds / AverageIntervalSeconds.
            MaxIntervalSeconds = double.IsNegativeInfinity(maxInterval) ? anchorPeriod : maxInterval;
            // Mean = (L_last - L_0) / (N - 1) over the cached prefix. Falls back to the anchor period
            // when only one launch (no gap to average).
            AverageIntervalSeconds = launches.Count >= 2
                ? (launches[launches.Count - 1] - launches[0]) / (launches.Count - 1)
                : anchorPeriod;
        }

        // Appends one more launch after the cached tail, honoring the player throttle: the next
        // launch is the first faithful window whose anchor-multiple is at least the throttle skip
        // (minSpacing) past the last launch. False on the safety cap or a degenerate generation
        // (which cannot happen post-construction for a valid anchor period).
        private bool ExtendOnce()
        {
            if (double.IsNaN(FirstLaunchUT))
                return false;
            if (launches.Count >= MissionPeriodicity.MaxScheduleSteps)
            {
                if (!capWarned)
                {
                    capWarned = true;
                    ParsekLog.Warn("MissionPeriodicity",
                        $"MissionRelaunchSchedule: reached MaxScheduleSteps " +
                        $"({MissionPeriodicity.MaxScheduleSteps.ToString(CultureInfo.InvariantCulture)}); " +
                        "parking at the last cached launch (pathological short anchor period?)");
                }
                return false;
            }
            // Throttle: skip ahead so the next launch is >= lastLaunch + max(minSpacing,
            // effSpan_last), snapped to the anchor grid; then search forward for the next faithful
            // window from there. The effSpan_last term is the M4b non-overlap guarantee under loiter
            // EXTENSION: minSpacing >= span covers cut-only launches (effSpan <= span), but an
            // extended launch's cycle runs past the recorded span, so the next launch must also
            // clear the prior entry's per-launch effective span. Knob-less schedules have no
            // timings and keep the pure-minSpacing throttle byte-identical.
            double spacing = minSpacing;
            if (timings.Count == launches.Count && timings.Count > 0)
            {
                double effSpanLast = timings[timings.Count - 1].EffectiveSpanSeconds;
                if (!double.IsNaN(effSpanLast) && effSpanLast > spacing)
                    spacing = effSpanLast;
            }
            long throttleK = (long)Math.Ceiling(
                (launches[launches.Count - 1] + spacing - ut0) / anchorPeriod - 1e-9);
            long kStart = Math.Max(lastK + 1, throttleK);
            if (!ResolveLaunch(kStart, out long k, out double resid, out bool within))
                return false;
            launches.Add(ut0 + k * anchorPeriod);
            lastK = k;
            FoldLaunchTolerance(resid, within);  // R3: account this launch's tolerance
            return true;
        }

        // Resolves the next launch from kStart: knob-less schedules run the base scan; a
        // knob-engaged schedule runs the shiftable-group scan and materializes the chosen
        // per-launch timing entry (index-aligned with `launches` - the caller appends the launch
        // right after this returns true). Pure apart from the rate-limited per-window Verbose.
        private bool ResolveLaunch(long kStart, out long k, out double resid, out bool within)
        {
            if (knob == null)
            {
                return MissionPeriodicity.TryFindNextScheduleK(
                    anchorPeriod, otherPeriods, otherTolerances,
                    kStart, lookaheadMultiples, out k, out resid, out within);
            }
            if (!MissionPeriodicity.TryFindNextScheduleK(
                    anchorPeriod, otherPeriods, otherTolerances,
                    knob.ShiftPeriods, knob.ShiftTolerances,
                    knob.PeriodSeconds, knob.ShiftMin, knob.ShiftMax,
                    kStart, lookaheadMultiples,
                    out k, out long d, out resid, out within))
                return false;
            timings.Add(BuildTimingEntry(d, resid, within));
            if (!MissionPeriodicity.SuppressLogging)
            {
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.VerboseRateLimited(
                    "MissionPeriodicity",
                    // One key per mission (ut0 + anchor identity): bounded by mission count.
                    $"knob-window.{ut0.ToString("R", ic)}.{anchorPeriod.ToString("R", ic)}",
                    $"knob window: launch#{launches.Count.ToString(ic)} k={k.ToString(ic)} " +
                    $"d={d.ToString(ic)} keptRevs={(knob.RecordedRevs + d).ToString(ic)} " +
                    $"residual={resid.ToString("F1", ic)}s within={within.ToString(ic)}");
            }
            return true;
        }

        // Materializes the per-launch timing for a chosen rev shift d: static cuts plus the phasing
        // cut for d < 0 (appended - static cuts all end before the phasing run starts, so order is
        // preserved), or the last-rev extension for d > 0 (the wrap window [RunEnd - T, RunEnd) is
        // cut-free by construction). EffectiveSpanSeconds drives the per-launch spacing and the
        // clock's tail boundary.
        private LaunchTimingEntry BuildTimingEntry(long d, double residualSeconds, bool withinTolerance)
        {
            IReadOnlyList<GhostPlaybackLogic.LoopCut> cuts = knob.StaticCuts;
            double extension = 0.0;
            if (d < 0)
            {
                var merged = new List<GhostPlaybackLogic.LoopCut>(
                    (knob.StaticCuts?.Count ?? 0) + 1);
                if (knob.StaticCuts != null)
                    merged.AddRange(knob.StaticCuts);
                merged.Add(new GhostPlaybackLogic.LoopCut
                {
                    StartUT = knob.RunStartUT,
                    LengthSeconds = -d * knob.PeriodSeconds,
                });
                cuts = merged;
            }
            else if (d > 0)
            {
                extension = d * knob.PeriodSeconds;
            }
            double totalCut = GhostPlaybackLogic.TotalCutLength(cuts);
            return new LaunchTimingEntry
            {
                KeptRevs = knob.RecordedRevs + d,
                ResidualSeconds = residualSeconds,
                WithinTolerance = withinTolerance,
                Cuts = cuts,
                ExtensionSeconds = extension,
                ExtensionWrapStartUT = knob.RunEndUT - knob.PeriodSeconds,
                ExtensionWrapPeriod = knob.PeriodSeconds,
                EffectiveSpanSeconds = knob.SpanSeconds - totalCut + extension,
            };
        }

        /// <summary>True when this schedule was built with the M4b phasing-loiter knob (per-launch
        /// timing entries exist for every cached launch).</summary>
        internal bool HasPhasingKnob => knob != null;

        /// <summary>
        /// The per-launch loop-clock timing for schedule entry <paramref name="cycleIndex"/> (the
        /// index <see cref="TryResolveActiveLaunch"/> returns). False for knob-less schedules and
        /// out-of-range indices - the span clock then runs the plain scheduled path. The entry for
        /// any index resolved by TryResolveActiveLaunch always exists (timings grow in lockstep
        /// with the launch cache).
        /// </summary>
        internal bool TryGetLaunchTiming(long cycleIndex, out LaunchTimingEntry entry)
        {
            entry = default;
            // timings is index-aligned with launches BY CONSTRUCTION: ResolveLaunch is the only
            // appender of timings and every true return is immediately followed by the caller's
            // launches.Add (ctor L_0 + ExtendOnce). The long-vs-Count comparison also makes the
            // (int) cast below safe: any cycleIndex above int.MaxValue fails the range check first.
            if (knob == null || cycleIndex < 0 || cycleIndex >= timings.Count)
                return false;
            entry = timings[(int)cycleIndex];
            return true;
        }

        // R3: fold one resolved launch's tolerance into the running aggregates. AllLaunchesWithinTolerance
        // can only flip true -> false (a single bounded-best launch makes the whole schedule "amber"); the
        // worst residual is the running max. Cheap: two scalar updates per launch, no allocation. A NaN
        // residual (degenerate) is ignored for the worst-residual max but a non-within launch still clears
        // the all-within flag. See docs/dev/plans/zero-drift-reschedule-hardening.md section 6 R2/R3.
        private void FoldLaunchTolerance(double residualSeconds, bool withinTolerance)
        {
            // M4b amber transition: the first knob-engaged launch that falls to bounded-best (the
            // phase target unreachable within the rev bounds at every k in the lookahead) logs ONCE
            // at Info (the flag only ever flips true -> false, so this fires at most once per
            // schedule build). Knob-less schedules keep today's silent fold.
            if (!withinTolerance && allLaunchesWithinTolerance && knob != null
                && !MissionPeriodicity.SuppressLogging)
            {
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Info("MissionPeriodicity",
                    "phasing knob: a scheduled launch could not reach the shiftable phase target " +
                    $"within keptRevs bounds [1, {(knob.RecordedRevs + knob.ShiftMax).ToString(ic)}] " +
                    $"(residual={residualSeconds.ToString("F1", ic)}s); launching bounded-best - " +
                    "the T- cell tints amber");
            }
            if (!withinTolerance)
                allLaunchesWithinTolerance = false;
            if (!double.IsNaN(residualSeconds) && !double.IsInfinity(residualSeconds)
                && residualSeconds > worstResidualSeconds)
                worstResidualSeconds = residualSeconds;
        }

        /// <summary>
        /// The active (most recent) scheduled launch at or before <paramref name="currentUT"/>, and
        /// its 0-based schedule index. False (parked) when <paramref name="currentUT"/> is before
        /// the first launch. Lazily extends the cache to cover <paramref name="currentUT"/>.
        /// </summary>
        internal bool TryResolveActiveLaunch(double currentUT, out double launchUT, out long cycleIndex)
        {
            launchUT = double.NaN;
            cycleIndex = 0;
            if (double.IsNaN(FirstLaunchUT) || double.IsNaN(currentUT) || currentUT < FirstLaunchUT)
                return false;
            while (launches[launches.Count - 1] <= currentUT)
                if (!ExtendOnce())
                    break;
            // Largest launch <= currentUT (binary search; the list is increasing).
            int lo = 0, hi = launches.Count - 1, idx = 0;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (launches[mid] <= currentUT) { idx = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            launchUT = launches[idx];
            cycleIndex = idx;
            return true;
        }

        /// <summary>
        /// The next scheduled relaunch strictly after <paramref name="currentUT"/> (the first launch
        /// when parked before it). Returns NaN if no future launch can be resolved because the
        /// safety cap was reached (so the UI shows "not aligned" rather than a past target / a
        /// negative countdown) - this only happens for a pathological short anchor period that the
        /// builder's overlap gate already rejects, so realistic schedules always return a future UT.
        /// Drives the UI "Time to launch" countdown and the "Warp to..." target.
        /// </summary>
        internal double NextLaunchAfter(double currentUT)
        {
            if (double.IsNaN(FirstLaunchUT))
                return double.NaN;
            if (double.IsNaN(currentUT) || currentUT < FirstLaunchUT)
                return FirstLaunchUT;
            while (launches[launches.Count - 1] <= currentUT)
                if (!ExtendOnce())
                    break;
            // Smallest launch strictly > currentUT (binary search; the list is increasing). The UI
            // calls this every frame, so this must not be an O(cacheSize) scan as the cache grows
            // (review S2). NaN when none (the safety cap was reached) -> the UI shows "not aligned".
            int lo = 0, hi = launches.Count - 1, idx = -1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (launches[mid] > currentUT) { idx = mid; hi = mid - 1; }
                else lo = mid + 1;
            }
            return idx >= 0 ? launches[idx] : double.NaN;
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

        public double GravParameter(string bodyName)
        {
            CelestialBody b = Find(bodyName);
            return b != null ? b.gravParameter : double.NaN;
        }

        public bool TryGetVesselOrbit(
            uint vesselPid, string recordedVesselGuid,
            out double periodSeconds, out string orbitBodyName)
        {
            periodSeconds = double.NaN;
            orbitBodyName = null;
            if (vesselPid == 0)
                return false;
            // The same pid resolution loop playback uses (FlightGlobals.Vessels scan with the
            // ghost-map-vessel guard, so a Parsek map proto ghost never reads as a live anchor).
            // Loaded and packed/on-rails vessels both carry a usable Orbit (design D1).
            Vessel v = FlightRecorder.FindVesselByPid(vesselPid);
            if (v == null)
                return false;
            // Launch-identity gate: the craft-baked pid is reused by every launch of the craft,
            // so a recorded guid that conclusively differs from the live vessel's means this is a
            // DIFFERENT launch, not the recorded anchor (VesselLaunchIdentity contract; unknown
            // guid falls back to pid-only).
            if (VesselLaunchIdentity.GuidsConclusivelyDiffer(recordedVesselGuid, v.id.ToString()))
                return false;
            Orbit orbit = v.orbit;
            if (orbit == null)
                return false;
            double ecc = orbit.eccentricity;
            double period = orbit.period;
            // Closed (elliptical) orbits only: a hyperbolic/parabolic or degenerate-period anchor
            // is not a repeatable phase reference - fail closed (design note 3.2).
            if (double.IsNaN(ecc) || ecc >= 1.0)
                return false;
            if (double.IsNaN(period) || double.IsInfinity(period) || period <= 0.0)
                return false;
            periodSeconds = period;
            orbitBodyName = orbit.referenceBody != null ? orbit.referenceBody.bodyName : null;
            return true;
        }
    }
}
