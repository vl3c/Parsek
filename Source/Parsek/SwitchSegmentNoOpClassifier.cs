using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

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
    /// coasting are deliberately NOT meaningful. Neither is motion the physics
    /// makes on its own (owner ruling 2026-11-01: the player has to actually move
    /// the vehicle): wheel suspension / steering / motor robotic events, robotic
    /// and converter "off" transitions, EVA ragdoll events, and a SurfaceMobile
    /// section in which the vessel never got
    /// <see cref="SurfaceMotionMinDisplacementMeters"/> from where it started.</para>
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
            // Check BOTH the bool and the terminal: some destruction paths set only
            // TerminalStateValue=Destroyed (ApplyDestroyedFallback) and some set
            // only the bool; either is an unambiguous destruction.
            if (segment.VesselDestroyed)
            {
                keepReason = "vessel-destroyed";
                return false;
            }
            if (segment.TerminalStateValue == TerminalState.Destroyed)
            {
                keepReason = "terminal-destroyed";
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
            // events, "off" transitions (shutdown / stop / zero throttle,
            // including the zero-throttle resume seeds the recorder emits) and
            // physics-made motion (wheel robotic events, ragdoll) do NOT count;
            // a real burn is independently caught by the ExoPropulsive track
            // section + the positive-throttle event, a real drive by the
            // surface-motion gate below.
            var passive = new PassiveSignalTally();
            if (segment.PartEvents != null)
            {
                for (int i = 0; i < segment.PartEvents.Count; i++)
                {
                    PartEvent evt = segment.PartEvents[i];
                    if (IsMeaningfulPartEvent(evt, segment, ref passive))
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
            // space), SurfaceStationary (sitting still), or a SurfaceMobile
            // section whose frames show no real displacement (settling). Any
            // Atmospheric, ExoPropulsive (thrust), real SurfaceMobile (rover
            // driving), or Approach (airless-body descent) section means the
            // vessel actually did something. (An empty section list is vacuously boring; the orbit
            // gate below and the no-event gates above carry the decision then.)
            if (segment.TrackSections != null)
            {
                for (int i = 0; i < segment.TrackSections.Count; i++)
                {
                    TrackSection sec = segment.TrackSections[i];
                    if (!IsBoringSection(sec, double.NaN, ref passive))
                    {
                        keepReason = "non-boring-section:" + sec.environment;
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

            LogPassiveSignalsIgnored("IsNoOpSegment", segment, double.NaN, passive);
            return true;
        }

        /// <summary>
        /// Tail variant of <see cref="IsNoOpSegment"/> for the NO-SESSION
        /// committed-restore resume path (<c>ResumeCommittedActiveRecording</c>),
        /// where the recorder appends a live tail onto an EXISTING committed
        /// recording instead of creating a fresh segment recording. The committed
        /// history before <paramref name="sinceUT"/> (the live-resume anchor,
        /// <c>liveResumeSessionStartUT</c>) is NOT a no-op (it is the whole
        /// mission), so this only inspects content at or after the resume anchor:
        /// did the RESUME change anything?
        ///
        /// <para>Same "meaningful" definition as <see cref="IsNoOpSegment"/>, but
        /// every gate is filtered to <c>ut &gt;= sinceUT</c> (sections: any
        /// section whose <c>endUT &gt; sinceUT</c> that is non-boring; orbit:
        /// elements of the segment covering the resume anchor vs the last). A
        /// no-op result means the resume coasted/sat — reverting the clone to its
        /// committed original (which survives in <c>committedTrees</c>, copy-on-write)
        /// loses no replayable content. Dock targets / descendants are caught via
        /// the post-anchor Docked / Decoupled / branch part events.</para>
        /// </summary>
        internal static bool IsNoOpResumeTail(
            Recording segment, double sinceUT, out string keepReason)
        {
            keepReason = null;

            if (segment == null)
            {
                keepReason = "segment-null";
                return false;
            }
            if (double.IsNaN(sinceUT))
            {
                // No resume anchor — cannot scope the tail; keep conservatively.
                keepReason = "no-resume-anchor";
                return false;
            }

            // A destruction is meaningful to the RESUME only if it happened WITHIN
            // the window. The destroyed terminal seals at the recording's end, so
            // scope these two gates by EndUT — unlike IsNoOpSegment (the session
            // path, where the segment IS the resume), the no-session caller runs
            // this tail check across EVERY recording in the restored clone,
            // including old committed debris destroyed in its ORIGINAL flight
            // (boosters / spent stages). Those end far before the resume anchor
            // (EndUT < sinceUT) and must NOT block the discard — otherwise every
            // real mission tree (which always carries destroyed debris) would
            // refuse to auto-discard a do-nothing resume. An in-window destruction
            // ends at EndUT >= sinceUT (and is independently caught by the scoped
            // Destroyed part event below).
            if (segment.EndUT >= sinceUT)
            {
                if (segment.VesselDestroyed)
                {
                    keepReason = "vessel-destroyed";
                    return false;
                }
                if (segment.TerminalStateValue == TerminalState.Destroyed)
                {
                    keepReason = "terminal-destroyed";
                    return false;
                }
            }

            var passive = new PassiveSignalTally();
            if (segment.PartEvents != null)
            {
                for (int i = 0; i < segment.PartEvents.Count; i++)
                {
                    PartEvent evt = segment.PartEvents[i];
                    if (evt.ut >= sinceUT && IsMeaningfulPartEvent(evt, segment, ref passive))
                    {
                        keepReason = "part-event:" + evt.eventType;
                        return false;
                    }
                }
            }

            if (segment.SegmentEvents != null)
            {
                for (int i = 0; i < segment.SegmentEvents.Count; i++)
                {
                    SegmentEvent se = segment.SegmentEvents[i];
                    if (se.ut >= sinceUT && se.type != SegmentEventType.TimeJump)
                    {
                        keepReason = "segment-event:" + se.type;
                        return false;
                    }
                }
            }

            if (segment.FlagEvents != null)
            {
                for (int i = 0; i < segment.FlagEvents.Count; i++)
                {
                    if (segment.FlagEvents[i].ut >= sinceUT)
                    {
                        keepReason = "flag-event";
                        return false;
                    }
                }
            }

            // Any track section that extends past the resume anchor must be boring.
            if (segment.TrackSections != null)
            {
                for (int i = 0; i < segment.TrackSections.Count; i++)
                {
                    TrackSection sec = segment.TrackSections[i];
                    if (sec.endUT > sinceUT
                        && !IsBoringSection(sec, sinceUT, ref passive))
                    {
                        keepReason = "non-boring-section:" + sec.environment;
                        return false;
                    }
                }
            }

            // Orbit elements must not have changed across the resume window.
            if (!OrbitElementsUnchangedSince(segment, sinceUT, out string orbitReason))
            {
                keepReason = orbitReason;
                return false;
            }

            LogPassiveSignalsIgnored("IsNoOpResumeTail", segment, sinceUT, passive);
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
            => OrbitElementsUnchangedSince(segment, double.NaN, out reason);

        /// <summary>
        /// As <see cref="OrbitElementsUnchanged"/>, but when <paramref name="sinceUT"/>
        /// is finite only considers orbital samples whose segment extends past it
        /// (<c>endUT &gt; sinceUT</c>) — i.e. the orbit across a resume window,
        /// ignoring the committed history before the resume anchor. Used by the
        /// no-session committed-resume tail check.
        /// </summary>
        internal static bool OrbitElementsUnchangedSince(
            Recording segment, double sinceUT, out string reason)
        {
            reason = null;
            bool filtered = !double.IsNaN(sinceUT);

            // Collect every orbital sample with its UT, then compare the
            // earliest vs the latest by UT. OrbitSegments and OrbitalCheckpoint
            // checkpoints are gathered from two sources and concatenated, so
            // iteration order is NOT guaranteed to be UT-ascending — sort by UT
            // (startUT) before picking first/last so a changed orbit cannot
            // slip past via an out-of-order list.
            var samples = new List<(double ut, OrbitSample s)>();
            void Consider(OrbitSegment os)
            {
                // Skip segments entirely before the resume anchor (committed
                // history); keep ones overlapping or after it.
                if (filtered && os.endUT <= sinceUT)
                    return;
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
        /// shutdown marks end-of-burn for tail-trim) - wrong here: a coasting
        /// resumed segment carries zero-throttle / shutdown engine seeds at its
        /// start UT and those must NOT block a no-op discard.
        ///
        /// <para>The rule (owner ruling 2026-11-01): only the player acting on the
        /// vessel counts; motion the physics produces on its own does not. So:</para>
        /// <list type="bullet">
        /// <item>Structural / geometry events (decouple, destroy, dock, undock,
        /// parachute, deployables, gear, cargo, fairing, inventory) always count.</item>
        /// <item>Engine / RCS events count only when actively firing (positive
        /// throttle); their "off" transitions do not.</item>
        /// <item>Robotic motion counts for servos, hinges, pistons and rotors, but
        /// NOT for a wheel module (suspension compression, steering, motor): a
        /// parked rover's suspension wiggles by a millimetre with nobody driving,
        /// and a real drive is caught by the surface-motion gate instead
        /// (<see cref="IsBoringSection"/>). An event whose module cannot be
        /// resolved still counts (conservative).</item>
        /// <item>"Off" transitions never count on their own: RoboticMotionStopped
        /// (the recorder also writes a synthetic one when it closes a recording),
        /// ConverterDeactivated (a drill or ISRU stops by itself when a tank fills
        /// or power runs out).</item>
        /// <item>An EVA ragdoll start / end is a physics outcome; a kerbal that
        /// actually moved is caught by the surface-motion or environment gates.</item>
        /// <item>Cosmetic light / thermal-animation events never count.</item>
        /// </list>
        /// </summary>
        internal static bool IsMeaningfulPartEvent(PartEvent evt)
        {
            var ignored = new PassiveSignalTally();
            return IsMeaningfulPartEvent(evt, null, ref ignored);
        }

        /// <summary>
        /// As <see cref="IsMeaningfulPartEvent(PartEvent)"/>, resolving a robotic
        /// event's module against <paramref name="owner"/>'s vessel snapshots first
        /// (part prefab second), and counting what it ignores into
        /// <paramref name="passive"/> for the caller's summary line.
        /// </summary>
        internal static bool IsMeaningfulPartEvent(
            PartEvent evt, Recording owner, ref PassiveSignalTally passive)
        {
            switch (evt.eventType)
            {
                // Cosmetic only - never meaningful.
                case PartEventType.LightOn:
                case PartEventType.LightOff:
                case PartEventType.LightBlinkEnabled:
                case PartEventType.LightBlinkDisabled:
                case PartEventType.LightBlinkRate:
                case PartEventType.ThermalAnimationHot:
                case PartEventType.ThermalAnimationCold:
                case PartEventType.ThermalAnimationMedium:
                    return false;

                // "Off" transitions - not activity on their own.
                case PartEventType.EngineShutdown:
                case PartEventType.RCSStopped:
                    return false;
                case PartEventType.RoboticMotionStopped:
                case PartEventType.ConverterDeactivated:
                    passive.StopTransitions++;
                    return false;

                // Physics outcomes on an EVA kerbal, not input.
                case PartEventType.EvaRagdollStarted:
                case PartEventType.EvaRagdollEnded:
                    passive.Ragdoll++;
                    return false;

                // Engine / RCS firing - meaningful only at positive throttle.
                case PartEventType.EngineIgnited:
                case PartEventType.EngineThrottle:
                case PartEventType.RCSActivated:
                case PartEventType.RCSThrottle:
                    return evt.value > 0f;

                // Robotic motion - meaningful unless the module is a wheel.
                case PartEventType.RoboticMotionStarted:
                case PartEventType.RoboticPositionSample:
                    if (IsWheelRoboticEvent(evt, owner))
                    {
                        passive.WheelRobotic++;
                        return false;
                    }
                    return true;

                // Everything structural / geometry - meaningful.
                default:
                    return true;
            }
        }

        /// <summary>
        /// True when a robotic event's module resolves to a wheel module
        /// (<see cref="FlightRecorder.IsWheelRoboticModuleName"/>). The event
        /// carries the part's ROBOTIC ordinal (only robotic-classified modules
        /// counted, <see cref="FlightRecorder.CacheRoboticModules"/>), not the
        /// module name, so the name is looked up in the owner recording's vessel
        /// snapshots (by part pid, then by part name), then on the part prefab.
        /// Unresolved returns false, which keeps the event meaningful.
        /// </summary>
        internal static bool IsWheelRoboticEvent(PartEvent evt, Recording owner)
        {
            string moduleName = ResolveRoboticModuleName(evt, owner);
            return moduleName != null && FlightRecorder.IsWheelRoboticModuleName(moduleName);
        }

        internal static string ResolveRoboticModuleName(PartEvent evt, Recording owner)
        {
            if (owner != null)
            {
                if (TryResolveRoboticModuleNameFromSnapshot(owner.VesselSnapshot,
                        evt.partPersistentId, evt.partName, evt.moduleIndex, out string name)
                    || TryResolveRoboticModuleNameFromSnapshot(owner.GhostVisualSnapshot,
                        evt.partPersistentId, evt.partName, evt.moduleIndex, out name))
                    return name;
            }

            Func<string, IList<string>> prefabLookup =
                PrefabModuleNamesForTesting ?? LivePrefabModuleNames;
            IList<string> modules = prefabLookup(evt.partName);
            return RoboticModuleNameAtOrdinal(modules, evt.moduleIndex);
        }

        /// <summary>Test seam: replaces the live part-prefab module-name lookup.</summary>
        internal static Func<string, IList<string>> PrefabModuleNamesForTesting;

        /// <summary>
        /// Pure: the module name at robotic ordinal <paramref name="roboticOrdinal"/>
        /// of the PART in <paramref name="vesselSnapshot"/> whose persistentId is
        /// <paramref name="partPid"/>, else of the first PART named
        /// <paramref name="partName"/> (every instance of a part carries the same
        /// module list). False when neither resolves.
        /// </summary>
        internal static bool TryResolveRoboticModuleNameFromSnapshot(
            ConfigNode vesselSnapshot, uint partPid, string partName, int roboticOrdinal,
            out string moduleName)
        {
            moduleName = null;
            if (vesselSnapshot == null)
                return false;
            ConfigNode[] parts = vesselSnapshot.GetNodes("PART");
            if (parts == null || parts.Length == 0)
                return false;

            ConfigNode byName = null;
            for (int i = 0; i < parts.Length; i++)
            {
                ConfigNode part = parts[i];
                if (part == null) continue;
                if (partPid != 0
                    && uint.TryParse(part.GetValue("persistentId"), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out uint pid)
                    && pid == partPid)
                {
                    moduleName = RoboticModuleNameAtOrdinal(ModuleNames(part), roboticOrdinal);
                    if (moduleName != null)
                        return true;
                }
                if (byName == null && !string.IsNullOrEmpty(partName)
                    && string.Equals(part.GetValue("name"), partName, StringComparison.Ordinal))
                    byName = part;
            }

            if (byName != null)
                moduleName = RoboticModuleNameAtOrdinal(ModuleNames(byName), roboticOrdinal);
            return moduleName != null;
        }

        private static List<string> ModuleNames(ConfigNode part)
        {
            var names = new List<string>();
            ConfigNode[] modules = part.GetNodes("MODULE");
            if (modules == null) return names;
            for (int i = 0; i < modules.Length; i++)
                names.Add(modules[i]?.GetValue("name"));
            return names;
        }

        /// <summary>Pure: the <paramref name="roboticOrdinal"/>-th robotic-classified
        /// name in <paramref name="moduleNames"/> (the recorder's own numbering), or
        /// null.</summary>
        internal static string RoboticModuleNameAtOrdinal(IList<string> moduleNames, int roboticOrdinal)
        {
            if (moduleNames == null || roboticOrdinal < 0)
                return null;
            int ordinal = 0;
            for (int i = 0; i < moduleNames.Count; i++)
            {
                string name = moduleNames[i];
                if (!FlightRecorder.IsRoboticModuleName(name)) continue;
                if (ordinal == roboticOrdinal)
                    return name;
                ordinal++;
            }
            return null;
        }

        private static IList<string> LivePrefabModuleNames(string partName)
        {
            if (string.IsNullOrEmpty(partName))
                return null;
            try
            {
                return LivePrefabModuleNamesCore(partName);
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited("SwitchSegment", "noop-prefab-lookup-failed-" + partName,
                    $"No-op check: prefab module lookup for '{partName}' threw " +
                    $"{ex.GetType().Name}: {ex.Message}; robotic event kept as meaningful", 30.0);
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IList<string> LivePrefabModuleNamesCore(string partName)
        {
            AvailablePart info = PartLoader.getPartInfoByName(partName);
            Part prefab = info?.partPrefab;
            if (prefab == null || prefab.Modules == null)
                return null;
            var names = new List<string>(prefab.Modules.Count);
            for (int i = 0; i < prefab.Modules.Count; i++)
                names.Add(prefab.Modules[i]?.moduleName);
            return names;
        }

        /// <summary>A surface section counts as surface MOTION only when the vessel
        /// moved at least this far (horizontally) across the window. Below it the
        /// SurfaceMobile label came from physics settling: a rover bouncing on its
        /// suspension after a load, a kerbal shuffling in place.</summary>
        internal const double SurfaceMotionMinDisplacementMeters = 1.0;

        /// <summary>
        /// Whether a track section shows the vessel doing nothing. ExoBallistic and
        /// SurfaceStationary are boring (<see cref="GhostPlaybackLogic.IsBoringEnvironment"/>);
        /// a SurfaceMobile section is boring too when its frames show the vessel never
        /// got <see cref="SurfaceMotionMinDisplacementMeters"/> from where the window
        /// started (<paramref name="sinceUT"/> NaN = the whole section). Every other
        /// environment, and a SurfaceMobile section whose displacement cannot be
        /// measured, is not boring (conservative).
        /// </summary>
        internal static bool IsBoringSection(
            TrackSection sec, double sinceUT, ref PassiveSignalTally passive)
        {
            if (GhostPlaybackLogic.IsBoringEnvironment(sec.environment))
                return true;
            if (sec.environment != SegmentEnvironment.SurfaceMobile)
                return false;
            if (!TryMeasureSurfaceDisplacement(sec, sinceUT, out double meters))
                return false;
            if (meters >= SurfaceMotionMinDisplacementMeters)
                return false;
            passive.SurfaceSettleSections++;
            if (meters > passive.MaxSettleMeters)
                passive.MaxSettleMeters = meters;
            return true;
        }

        /// <summary>
        /// Pure over the section's Absolute frames: the largest horizontal distance
        /// any frame in the window reaches from the window's reference position (the
        /// last frame at or before <paramref name="sinceUT"/>, else the first frame
        /// in the window). False when the section is not Absolute, has fewer than two
        /// usable frames, or a body radius does not resolve.
        /// </summary>
        internal static bool TryMeasureSurfaceDisplacement(
            TrackSection sec, double sinceUT, out double maxMeters)
        {
            maxMeters = 0.0;
            if (sec.referenceFrame != ReferenceFrame.Absolute || sec.frames == null)
                return false;
            bool filtered = !double.IsNaN(sinceUT);

            int refIndex = -1;
            int firstInWindow = -1;
            for (int i = 0; i < sec.frames.Count; i++)
            {
                double ut = sec.frames[i].ut;
                if (filtered && ut <= sinceUT)
                    refIndex = i;
                else if (firstInWindow < 0 && (!filtered || ut >= sinceUT))
                    firstInWindow = i;
            }
            if (firstInWindow < 0)
                return false;
            if (refIndex < 0)
                refIndex = firstInWindow;

            TrajectoryPoint reference = sec.frames[refIndex];
            Func<string, double> radiusLookup = BodyRadiusForTesting ?? LiveBodyRadius;
            double radius = radiusLookup(reference.bodyName);
            if (double.IsNaN(radius) || radius <= 0.0)
                return false;

            int measured = refIndex == firstInWindow ? 1 : 2;
            for (int i = firstInWindow; i < sec.frames.Count; i++)
            {
                TrajectoryPoint p = sec.frames[i];
                if (!string.Equals(p.bodyName, reference.bodyName, StringComparison.Ordinal))
                    return false;
                if (i != firstInWindow) measured++;
                double d = SpawnCollisionDetector.SurfaceDistance(
                    reference.latitude, reference.longitude, p.latitude, p.longitude, radius);
                if (d > maxMeters)
                    maxMeters = d;
            }
            return measured >= 2;
        }

        /// <summary>Test seam: replaces the live body-radius lookup.</summary>
        internal static Func<string, double> BodyRadiusForTesting;

        private static double LiveBodyRadius(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName))
                return double.NaN;
            try
            {
                return LiveBodyRadiusCore(bodyName);
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited("SwitchSegment", "noop-body-radius-failed-" + bodyName,
                    $"No-op check: body radius lookup for '{bodyName}' threw " +
                    $"{ex.GetType().Name}: {ex.Message}; surface section kept as motion", 30.0);
                return double.NaN;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double LiveBodyRadiusCore(string bodyName)
        {
            CelestialBody body = FlightGlobals.GetBodyByName(bodyName);
            return body != null ? body.Radius : double.NaN;
        }

        /// <summary>What a no-op check ignored as physics, not player input.</summary>
        internal struct PassiveSignalTally
        {
            public int WheelRobotic;
            public int StopTransitions;
            public int Ragdoll;
            public int SurfaceSettleSections;
            public double MaxSettleMeters;

            public bool Any => WheelRobotic > 0 || StopTransitions > 0 || Ragdoll > 0
                || SurfaceSettleSections > 0;
        }

        private static void LogPassiveSignalsIgnored(
            string check, Recording rec, double sinceUT, PassiveSignalTally passive)
        {
            if (!passive.Any)
                return;
            ParsekLog.Verbose("SwitchSegment",
                $"{check}: no-op despite passive signals rec={rec?.RecordingId ?? "<null>"} " +
                $"sinceUT={(double.IsNaN(sinceUT) ? "none" : sinceUT.ToString("R", CultureInfo.InvariantCulture))} " +
                $"wheelRobotic={passive.WheelRobotic} stopTransitions={passive.StopTransitions} " +
                $"ragdoll={passive.Ragdoll} settleSections={passive.SurfaceSettleSections} " +
                $"maxSettleM={passive.MaxSettleMeters.ToString("F2", CultureInfo.InvariantCulture)}");
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
