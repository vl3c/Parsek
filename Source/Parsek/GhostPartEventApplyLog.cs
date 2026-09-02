using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// P8 step 1: the per-family observability layer over
    /// <see cref="GhostPlaybackLogic.ApplyPartEvents"/>.
    ///
    /// Before this existed the applier emitted exactly one aggregate line per call
    /// ("Applied N part events for ghost #N"), so no log-reading test could tell
    /// "the recorded event moved the ghost" from "the handler early-returned because
    /// the ghost carries nothing for that pid". Every family now reports an OUTCOME
    /// that comes from the handler's OWN early-return branch (the outcome-returning
    /// core is the real method; the historical bool/void signature is a thin wrapper
    /// over it), so there is no second copy of a guard predicate that could drift.
    ///
    /// COST DISCIPLINE, because this runs per ghost per frame with hundreds of ghosts
    /// possible in one scene: the tally is allocated LAZILY on the first consumed
    /// event, so a ghost whose event cursor is already caught up (the overwhelmingly
    /// common frame) pays nothing at all. The flush is one
    /// <see cref="ParsekLog.VerboseRateLimited"/> per (recording, family, surface)
    /// actually touched, keyed so the first occurrence always emits.
    /// </summary>
    internal enum GhostPartEventOutcome
    {
        /// The handler wrote the ghost state or pose this event asks for.
        Applied,

        /// An info existed and was ALREADY in the requested state; the handler
        /// deliberately did nothing (re-arming would produce a visible hitch).
        AlreadyInState,

        /// The event updated playback bookkeeping only; the visual is written by a
        /// per-frame driver (a blinking light is driven by UpdateBlinkingLights).
        DeferredToDriver,

        /// Recognised but deliberately ignored: the legacy wheel-motor /
        /// wheel-steering robotic events whose value is derived from the trajectory.
        LegacyEventIgnored,

        /// The ghost carries no dictionary for this family at all.
        NoFamilyState,

        /// The family dictionary exists but has no entry for this part (or engine key).
        NoInfoForPart,

        /// Colour-changer entries exist for the part but none is a Pattern-A cabin
        /// light, so a light event has nothing on that surface to toggle.
        NoCabinLightEntry,

        /// An info exists but resolved to no usable transform / material / emitter.
        NoResolvedVisual,

        /// An info exists but the pose this event needs was never sampled when the
        /// ghost was built (a chute with no semi-deployed sample).
        PoseNotSampled,

        /// The applier switch has no arm for this event type (Docked / Undocked, and
        /// any forward-compat member an older build materialises off a newer sidecar).
        UnhandledEventType,
    }

    /// <summary>
    /// The ghost sub-surface a tallied outcome describes. A family with a CASCADE
    /// (a cargo bay tries the animated deployable first and falls back to jettison
    /// panels) or with two independent surfaces (a light event drives both Unity
    /// Light components and Pattern-A colour-changer materials) reports one line per
    /// surface, which is the only way a reader can tell which half fired.
    /// </summary>
    internal enum GhostPartEventSurface
    {
        Visibility,
        Parachute,
        EngineFx,
        EngineAudio,
        RcsFx,
        Deployable,
        JettisonPanel,
        Fairing,
        Heat,
        Light,
        ColorChanger,
        BlinkState,
        ConverterLoop,
        Eva,
        Robotic,
        Inventory,
    }

    internal static class GhostPartEventApplyLog
    {
        internal const string Subsystem = "GhostPartEvents";

        /// <summary>
        /// Rate-limit interval for the PER-FRAME families only (see
        /// <see cref="IsPerFrameFamily"/>). Deliberately longer than the 5 s default:
        /// those families emit on every playback interval per ghost, and a crowded
        /// scene multiplies that by the ghost count. The first occurrence per
        /// (recording, family, surface) always emits.
        /// </summary>
        internal const double RateLimitSeconds = 15.0;

        /// <summary>
        /// THE RATE-LIMIT POLICY, and it is per FAMILY rather than uniform.
        ///
        /// The house convention reserves <c>VerboseRateLimited</c> for per-frame paths
        /// and logs bounded per-item counts plainly. A UNIFORM rate limit was
        /// measurably wrong here: with one 15 s key per (recording, family, surface),
        /// a batch that APPLIED pid A at t=0 and SKIPPED pid B at t=5 suppressed the
        /// second line outright, and the skip a lane is hunting survived only as a
        /// <c>suppressed=N</c> counter on some later emission. The skip is the whole
        /// reason this instrument exists; it must not be what the limiter eats.
        ///
        /// PER-FRAME (rate-limited, keyed rec+family+surface): the three families the
        /// recorder samples continuously rather than on a state change -
        /// <see cref="PartEventType.EngineThrottle"/>,
        /// <see cref="PartEventType.RCSThrottle"/> and
        /// <see cref="PartEventType.RoboticPositionSample"/>. Their line count is
        /// unbounded in the flight length.
        ///
        /// EVERYTHING ELSE: plain <see cref="ParsekLog.Verbose"/>, one line per
        /// occurrence. These are bounded, event-driven families - a stage fires once,
        /// a bay opens once, a chute cuts once - and an occurrence is already one
        /// AGGREGATED line per ApplyPartEvents call that consumed at least one event
        /// of that family, so the volume is bounded by the recorded event count.
        /// </summary>
        internal static bool IsPerFrameFamily(PartEventType family)
        {
            switch (family)
            {
                case PartEventType.EngineThrottle:
                case PartEventType.RCSThrottle:
                case PartEventType.RoboticPositionSample:
                    return true;
                default:
                    return false;
            }
        }

        internal static string OutcomeToken(GhostPartEventOutcome outcome)
        {
            switch (outcome)
            {
                case GhostPartEventOutcome.Applied: return "applied";
                case GhostPartEventOutcome.AlreadyInState: return "already-in-state";
                case GhostPartEventOutcome.DeferredToDriver: return "deferred-to-driver";
                case GhostPartEventOutcome.LegacyEventIgnored: return "legacy-event-ignored";
                case GhostPartEventOutcome.NoFamilyState: return "no-family-state";
                case GhostPartEventOutcome.NoInfoForPart: return "no-info-for-part";
                case GhostPartEventOutcome.NoCabinLightEntry: return "no-cabin-light-entry";
                case GhostPartEventOutcome.NoResolvedVisual: return "no-resolved-visual";
                case GhostPartEventOutcome.PoseNotSampled: return "pose-not-sampled";
                case GhostPartEventOutcome.UnhandledEventType: return "unhandled-event-type";
                default: return "unknown";
            }
        }

        internal static string SurfaceToken(GhostPartEventSurface surface)
        {
            switch (surface)
            {
                case GhostPartEventSurface.Visibility: return "visibility";
                case GhostPartEventSurface.Parachute: return "parachute";
                case GhostPartEventSurface.EngineFx: return "engine-fx";
                case GhostPartEventSurface.EngineAudio: return "engine-audio";
                case GhostPartEventSurface.RcsFx: return "rcs-fx";
                case GhostPartEventSurface.Deployable: return "deployable";
                case GhostPartEventSurface.JettisonPanel: return "jettison-panel";
                case GhostPartEventSurface.Fairing: return "fairing";
                case GhostPartEventSurface.Heat: return "heat";
                case GhostPartEventSurface.Light: return "light";
                case GhostPartEventSurface.ColorChanger: return "colorchanger";
                case GhostPartEventSurface.BlinkState: return "blink-state";
                case GhostPartEventSurface.ConverterLoop: return "converter-loop";
                case GhostPartEventSurface.Eva: return "eva";
                case GhostPartEventSurface.Robotic: return "robotic";
                case GhostPartEventSurface.Inventory: return "inventory";
                default: return "unknown";
            }
        }

        /// <summary>
        /// True when the outcome means the ghost was actually written. Everything else
        /// counts as a SKIP and carries a reason class, including the two deliberate
        /// no-ops (already-in-state, deferred-to-driver) - a reader who wants to know
        /// "did anything move?" must not have those hidden inside `applied`.
        /// </summary>
        internal static bool CountsAsApplied(GhostPartEventOutcome outcome)
            => outcome == GhostPartEventOutcome.Applied;

        /// <summary>
        /// THE line grammar. Every token is whitespace-free by construction (the
        /// family name is an enum member name, the surface and reason come from the
        /// closed vocabularies above, and the three numbers are invariant-formatted),
        /// so a harness parser can split on whitespace without the vessel-name /
        /// free-text tail problem that shapes ghostlife.py's parser.
        ///
        /// `pid` is the REPRESENTATIVE part: the first pid that SKIPPED in this batch
        /// when there was any skip, otherwise the first pid that applied. A skip is
        /// what a reader is hunting, so it wins the slot.
        /// </summary>
        internal static string FormatLine(
            PartEventType family,
            GhostPartEventSurface surface,
            int recIdx,
            uint pid,
            int applied,
            int skipped,
            GhostPartEventOutcome reason)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "apply family={0} surface={1} rec={2} pid={3} applied={4} skipped={5} reason={6}",
                family,
                SurfaceToken(surface),
                recIdx,
                pid,
                applied,
                skipped,
                OutcomeToken(reason));
        }

        internal static string RateLimitKey(
            int recIdx, PartEventType family, GhostPartEventSurface surface)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "apply-{0}-{1}-{2}", recIdx, family, SurfaceToken(surface));
        }
    }

    /// <summary>
    /// Per-call accumulator. One instance lives for the duration of a single
    /// <see cref="GhostPlaybackLogic.ApplyPartEvents"/> call; the batch-counting
    /// convention in `.claude/CLAUDE.md` is exactly this shape - increment inside the
    /// loop, emit ONE rate-limited summary per family after it.
    /// </summary>
    internal sealed class GhostPartEventApplyTally
    {
        private struct Entry
        {
            public PartEventType family;
            public GhostPartEventSurface surface;
            public int applied;
            public int skipped;
            public uint appliedPid;
            public bool hasAppliedPid;
            public uint skippedPid;
            public bool hasSkippedPid;
            public GhostPartEventOutcome firstSkipReason;
        }

        // Key packs family and surface into one int; both are small, dense enums and
        // a struct key would need an IEquatable to avoid boxing on every lookup.
        private readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();

        private static int Key(PartEventType family, GhostPartEventSurface surface)
            => ((int)family << 8) | (int)surface;

        internal int DistinctLineCount => entries.Count;

        internal void Record(
            PartEventType family,
            GhostPartEventSurface surface,
            uint pid,
            GhostPartEventOutcome outcome)
        {
            int key = Key(family, surface);
            Entry entry;
            if (!entries.TryGetValue(key, out entry))
            {
                entry = new Entry
                {
                    family = family,
                    surface = surface,
                    firstSkipReason = GhostPartEventOutcome.Applied,
                };
            }

            if (GhostPartEventApplyLog.CountsAsApplied(outcome))
            {
                entry.applied++;
                if (!entry.hasAppliedPid)
                {
                    entry.appliedPid = pid;
                    entry.hasAppliedPid = true;
                }
            }
            else
            {
                entry.skipped++;
                if (!entry.hasSkippedPid)
                {
                    entry.skippedPid = pid;
                    entry.hasSkippedPid = true;
                    entry.firstSkipReason = outcome;
                }
            }

            entries[key] = entry;
        }

        /// <summary>
        /// Renders every accumulated (family, surface) line, in no guaranteed order.
        /// Exposed separately from <see cref="Flush"/> so the grammar is assertable
        /// without a log sink.
        /// </summary>
        internal List<string> RenderLines(int recIdx)
        {
            var lines = new List<string>(entries.Count);
            foreach (var kv in entries)
            {
                Entry entry = kv.Value;
                lines.Add(GhostPartEventApplyLog.FormatLine(
                    entry.family,
                    entry.surface,
                    recIdx,
                    entry.hasSkippedPid ? entry.skippedPid : entry.appliedPid,
                    entry.applied,
                    entry.skipped,
                    entry.hasSkippedPid ? entry.firstSkipReason : GhostPartEventOutcome.Applied));
            }
            return lines;
        }

        internal void Flush(int recIdx)
        {
            foreach (var kv in entries)
            {
                Entry entry = kv.Value;
                string line = GhostPartEventApplyLog.FormatLine(
                    entry.family,
                    entry.surface,
                    recIdx,
                    entry.hasSkippedPid ? entry.skippedPid : entry.appliedPid,
                    entry.applied,
                    entry.skipped,
                    entry.hasSkippedPid ? entry.firstSkipReason : GhostPartEventOutcome.Applied);

                // Per-family policy, NOT a uniform limiter - see
                // GhostPartEventApplyLog.IsPerFrameFamily for why a uniform 15 s key
                // silently ate the skip lines this instrument exists to surface.
                if (GhostPartEventApplyLog.IsPerFrameFamily(entry.family))
                {
                    ParsekLog.VerboseRateLimited(
                        GhostPartEventApplyLog.Subsystem,
                        GhostPartEventApplyLog.RateLimitKey(recIdx, entry.family, entry.surface),
                        line,
                        GhostPartEventApplyLog.RateLimitSeconds);
                }
                else
                {
                    ParsekLog.Verbose(GhostPartEventApplyLog.Subsystem, line);
                }
            }
        }
    }
}
