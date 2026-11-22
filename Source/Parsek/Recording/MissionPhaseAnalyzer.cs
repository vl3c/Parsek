using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>A closed UT span.</summary>
    internal struct ProfileSpan
    {
        public double StartUT;
        public double EndUT;

        public ProfileSpan(double startUT, double endUT)
        {
            StartUT = startUT;
            EndUT = endUT;
        }
    }

    /// <summary>
    /// One instant the line builder read off the tree (a branch point, the line's terminal,
    /// a Re-Fly bridge) for the analyzer to place: stratum (b)'s anchors. The analyzer only
    /// reads <see cref="Kind"/>, the UTs and the flags; <see cref="Ref"/> is carried through
    /// opaque to the caller (a child line id, a partner text).
    /// </summary>
    internal sealed class ProfileAnchorInput
    {
        public ProfileEventKind Kind;
        public double UT;
        public double EndUT = double.NaN;
        public int Count;
        public int Vessels;
        public string Ref;
        public string RefName;
        public string Cause;
        // Dock: this mission's ship was absorbed into the partner (its leg ended Docked and the
        // merged leg carries the partner's identity).
        public bool OwnShipAbsorbed;
        // Eva: the kerbal's line is only a board back within the short-EVA window, so the EVA
        // is one event row and no child line is drawn.
        public bool Collapsed;
        // End: the terminal state of the line's last piece.
        public TerminalState? Terminal;
        public string Biome;
    }

    /// <summary>
    /// One vessel line's input to <see cref="MissionPhaseAnalyzer.Analyze"/> (design section
    /// 4.1): its pieces in UT order, its anchors, the UT spans the mission's include set
    /// excludes, and the body facts (filled by the caller, so the analyzer names no live type).
    /// </summary>
    internal sealed class VesselLineInput
    {
        public string LineId;
        public bool IsEva;
        public string LaunchBodyName;
        public readonly List<Recording> Pieces = new List<Recording>();
        public readonly List<ProfileAnchorInput> Anchors = new List<ProfileAnchorInput>();
        public readonly List<ProfileSpan> ExcludedSpans = new List<ProfileSpan>();
        public Func<string, RecordingOptimizer.BodyFacts> BodyFacts;
    }

    /// <summary>The orbit facts at one instant (section 4.3: two axes, not one kind).</summary>
    internal struct ProfileOrbit
    {
        public ProfileEvidenceTier Tier;
        public string Body;
        public bool HasElements;
        public OrbitSegment Elements;
        public ProfileOrbitEnergy Energy;
        public ProfilePeriapsis Periapsis;
        // Hindsight: the coast ends in this body's SOI before it reaches periapsis.
        public string Encounter;
        // The coast ends in that SOI with no burn between (the encounter is this orbit's own).
        public bool DirectEncounter;
        // The encountered body is the parent: the coast leaves this body's SOI (an escape).
        public bool EncounterIsExit;
        // Inferred tier: the altitude that held.
        public double HeldAltitude;
        public double ApoapsisAltitude;
        public double PeriapsisAltitude;
        public double InclinationDeg;

        public bool IsKnown => Tier != ProfileEvidenceTier.None;

        /// <summary>Stock's three kinds: encounter beats any periapsis reading.</summary>
        public ProfileOrbitKind Kind
        {
            get
            {
                if (Tier == ProfileEvidenceTier.None) return ProfileOrbitKind.Unknown;
                if (!string.IsNullOrEmpty(Encounter)) return ProfileOrbitKind.Leaving;
                if (Periapsis == ProfilePeriapsis.InAir || Periapsis == ProfilePeriapsis.SubSurface)
                    return ProfileOrbitKind.Falling;
                if (Energy == ProfileOrbitEnergy.Escaping) return ProfileOrbitKind.Leaving;
                if (Energy == ProfileOrbitEnergy.Bound && Periapsis == ProfilePeriapsis.Clear)
                    return ProfileOrbitKind.InOrbit;
                return ProfileOrbitKind.Unknown;
            }
        }
    }

    /// <summary>One named instant or burn (section 3.2).</summary>
    internal sealed class ProfileEvent
    {
        public ProfileEventKind Kind;
        public double UT;
        public double EndUT = double.NaN;
        public string Body;
        public string Rule;
        public ProfileEvidenceTier Tier;
        public ProfileAnchorInput Anchor;
        public int Count;

        // Burns.
        public ProfileBurnKind BurnKind;
        public ProfileOrbit Before;
        public ProfileOrbit After;
        public bool EngineTimed;
        public readonly List<string> Engines = new List<string>();
        public double AltitudeAtStart = double.NaN;
        public double SpentRecorded = double.NaN;
        public double LeftRecorded = double.NaN;
        public double SpentEstimate = double.NaN;
        // Engine time (summed over the burns a merged row stands for).
        public double BurnSeconds;
        public string DeltaVGap;
        // A burn row that stands for several burns (Approach burns (3)).
        public int MergedBurns = 1;
        // A ground-level burn (landing burn) rather than a space burn.
        public bool FloorBurn;

        // Stagings: the recorded fall in Left the dropped fuel caused.
        public double StagingDrop = double.NaN;

        // Touchdown / splashdown.
        public double SurfaceAltitude = double.NaN;
        public string Biome;
    }

    /// <summary>A span of one line (section 3.1), with the events and sub-phases inside it.</summary>
    internal sealed class ProfilePhase
    {
        public ProfilePhaseKind Kind;
        public double StartUT;
        public double EndUT;
        public string Body;
        public string TargetBody;
        public string Rule;
        public ProfileEvidenceTier Tier = ProfileEvidenceTier.Structural;
        // Rendezvous / Docked: the dock anchor (partner, which side kept the identity).
        public ProfileAnchorInput Anchor;
        public readonly List<ProfileEvent> Events = new List<ProfileEvent>();
        public readonly List<ProfilePhase> SubPhases = new List<ProfilePhase>();
        public ProfileOrbit FirstOrbit;
        public ProfileOrbit LastOrbit;
        public double MaxAltitude = double.NaN;
        public double ExitAltitude = double.NaN;
        public double DriveDistance = double.NaN;
        public string Biome;
        public bool Clipped;
        public string UnclassifiedReason;
        public double SpentRecorded = double.NaN;
        public double LeftAtEnd = double.NaN;
        public int Skips;
        public int Passes;

        public double Duration => EndUT - StartUT;
    }

    /// <summary>What the analyzer could not read (section 4.7).</summary>
    internal sealed class ProfileDataGap
    {
        public double UT;
        public string Reason;
    }

    /// <summary>
    /// The analyzer's output for one line: the ordered phases with their sub-phases, events and
    /// burns, the orbit facts and evidence tier behind every element, and the data gaps.
    /// </summary>
    internal sealed class PhasePlan
    {
        public string LineId;
        public double StartUT = double.NaN;
        public double EndUT = double.NaN;
        public bool IsEva;
        public readonly List<ProfilePhase> Phases = new List<ProfilePhase>();
        public readonly List<ProfileDataGap> Gaps = new List<ProfileDataGap>();
        public TerminalState? Terminal;
        public string EndBody;
        public double SpentRecorded = double.NaN;
        public double LeftAtEnd = double.NaN;
        public double LeftAtStart = double.NaN;
        public bool HasSnapshots;
        public int Pieces;
        public int Sections;
        public int Burns;
        public int Events;
        public int DvEstimated;
        public int DvRecorded;
        public int Inferred;
        public int OrbitUnknown;
        public int Unclassified;
        public int ClippedByInclude;
        public int DroppedByInclude;
        public int Reflown;
        // Null when the phases tile the line (the totality check); else what broke it.
        public string TilingProblem;
    }

    /// <summary>
    /// The Mission Profile's phase analyzer (docs/dev/design-mission-profile.md section 4):
    /// one vessel line in, its phases, events and burns out. Three stages: segmentation into
    /// neutral spans with facts only (<c>.Facts.cs</c>), interpretation in strata over the
    /// whole line with full hindsight (orbits, anchors, atmosphere passes in <c>.Passes.cs</c>,
    /// burns in <c>.Burns.cs</c>), and grouping into phases (<c>.Grouping.cs</c>). Never reads
    /// rows, roles or recording boundaries (row independence). Pure: no Unity, no statics, no
    /// store, no logging; the caller logs one summary from the plan.
    /// </summary>
    internal static partial class MissionPhaseAnalyzer
    {
        /// <summary>Analyzes one vessel line. Never throws on malformed data; an empty line
        /// returns an empty plan with a <c>section missing</c> gap.</summary>
        internal static PhasePlan Analyze(VesselLineInput line)
        {
            var plan = new PhasePlan { LineId = line?.LineId, IsEva = line != null && line.IsEva };
            if (line == null || line.Pieces.Count == 0)
            {
                plan.Gaps.Add(new ProfileDataGap { UT = double.NaN, Reason = GapSectionMissing });
                return plan;
            }

            var ctx = new Ctx(line, plan);
            BuildSections(ctx);
            plan.Pieces = line.Pieces.Count;
            plan.Sections = ctx.Secs.Count;
            ReadLineEnds(ctx);
            if (ctx.Secs.Count == 0)
            {
                plan.Gaps.Add(new ProfileDataGap { UT = plan.StartUT, Reason = GapSectionMissing });
                return plan;
            }

            BuildEngineIntervals(ctx);
            BuildItems(ctx);
            ResolveOrbits(ctx);
            if (line.IsEva)
            {
                GroupEva(ctx);
            }
            else
            {
                ResolvePasses(ctx);
                NameBurns(ctx);
                GroupPhases(ctx);
            }
            PlaceEvents(ctx);
            ApplyExclusions(ctx);
            CheckTotality(ctx);
            ComputeDeltaV(ctx);
            CountPlan(ctx);
            return plan;
        }

        internal const string GapSectionMissing = "section missing";
        internal const string GapOrbitUnknown = "orbit unknown (no orbit sample)";
        internal const string GapOrbitInferred = "orbit inferred from altitude";
        internal const string GapDeltaVNoStoredOrbit = "delta-v unknown (no stored orbit on one side)";
        internal const string GapDeltaVLongBurn = "delta-v unknown (burn longer than 120 s)";
        internal const string GapDeltaVStagingUnread = "delta-v unknown (no reading just before a staging)";

        /// <summary>One analysis's working state.</summary>
        private sealed class Ctx
        {
            public readonly VesselLineInput Line;
            public readonly PhasePlan Plan;
            public readonly List<Sec> Secs = new List<Sec>();
            public readonly List<TrackSection> SectionView = new List<TrackSection>();
            public int[] BurnRunEnd;
            public readonly List<ProfileSpan> EngineOn = new List<ProfileSpan>();
            public readonly List<EngineFiring> Firings = new List<EngineFiring>();
            public readonly List<Item> Items = new List<Item>();
            public readonly List<PassGroup> PassGroups = new List<PassGroup>();
            public readonly List<DeltaVSnapshot> Snapshots = new List<DeltaVSnapshot>();
            public readonly Dictionary<string, RecordingOptimizer.BodyFacts> FactsCache =
                new Dictionary<string, RecordingOptimizer.BodyFacts>(StringComparer.Ordinal);

            public Ctx(VesselLineInput line, PhasePlan plan)
            {
                Line = line;
                Plan = plan;
            }

            public RecordingOptimizer.BodyFacts Facts(string body)
            {
                if (string.IsNullOrEmpty(body)) return default(RecordingOptimizer.BodyFacts);
                if (FactsCache.TryGetValue(body, out var f)) return f;
                try
                {
                    f = Line.BodyFacts != null ? Line.BodyFacts(body) : default(RecordingOptimizer.BodyFacts);
                }
                catch (Exception)
                {
                    f = default(RecordingOptimizer.BodyFacts);
                }
                FactsCache[body] = f;
                return f;
            }

            public bool HasAtmosphere(string body)
            {
                var f = Facts(body);
                return f.Known && f.AtmosphereDepth > 0;
            }
        }
    }
}
