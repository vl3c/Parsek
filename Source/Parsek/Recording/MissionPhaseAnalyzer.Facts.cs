using System;
using System.Collections.Generic;

namespace Parsek
{
    // Stage 1 (design section 4.3): the line's sections become neutral spans with facts only:
    // regime, body, burns (one definition shared with the version-3 burn cuts), engine time,
    // and the orbit facts at each span end with their evidence tier. No meaning is given here.
    internal static partial class MissionPhaseAnalyzer
    {
        /// <summary>A burn shorter than this with no delta-v reading is an RCS tap.</summary>
        internal const double RcsTapSeconds = 0.5;

        /// <summary>Engine events this far outside a burn's sections still time that burn (the
        /// recorder closes a powered section one sample after ignition).</summary>
        internal const double EngineMatchSeconds = 1.5;

        /// <summary>Two ground-level firings closer than this are one burn (staging gaps).</summary>
        internal const double FloorBurnMergeSeconds = RecordingOptimizer.BurnMergeGapSeconds;

        /// <summary>A coast whose altitude stays within this fraction of its radius ...</summary>
        internal const double HoldTightFraction = 0.001;
        /// <summary>... for at least this long reads as a held, bound and clear orbit (inferred).</summary>
        internal const double HoldTightSeconds = 120.0;
        /// <summary>The looser hold: within this fraction for a quarter of the circular period.</summary>
        internal const double HoldLooseFraction = 0.02;

        internal enum ProfileRegime { Surface = 0, Air, Low, Coast, Burn }

        private enum ItemType { Floor = 0, Coast, Burn }

        private sealed class Sec
        {
            public Recording Rec;
            public TrackSection S;
            public ProfileRegime Regime;
            public string Body;
            public double Start;
            public double End;
            public bool OnRails => S.referenceFrame == ReferenceFrame.OrbitalCheckpoint;
            public bool Relative => S.referenceFrame == ReferenceFrame.Relative;
        }

        private sealed class EngineFiring
        {
            public double Start;
            public double End;
            public readonly List<string> Parts = new List<string>();
        }

        private sealed class FloorBurn
        {
            public double Start;
            public double End;
            public readonly List<string> Parts = new List<string>();
        }

        private sealed class Item
        {
            public ItemType Type;
            public ProfileRegime Regime;
            public int First;
            public int Last;
            public string Body;
            public double Start;
            public double End;

            // Floor.
            public readonly List<FloorBurn> FloorBurns = new List<FloorBurn>();

            // Coast.
            public bool HasElements;
            public bool ElementsFromTerminal;
            public OrbitSegment FirstElements;
            public OrbitSegment LastElements;
            public double HeldAltitude = double.NaN;
            public string EncounterBody;
            public bool DirectEncounter;
            public bool EncounterIsExit;
            public ProfileOrbit StartOrbit;
            public ProfileOrbit EndOrbit;

            // Burn.
            public double BurnStart;
            public double BurnEnd;
            public bool EngineTimed;
            public readonly List<string> Engines = new List<string>();
            public ProfileOrbit Before;
            public ProfileOrbit After;
            public ProfileBurnKind BurnKind;
            public string BurnRule;
            public bool InTransfer;

            // Atmosphere pass membership (Air items entered from space).
            public PassGroup Pass;
            public bool PassStart;
        }

        // ------------------------------------------------------------------ sections

        private static void BuildSections(Ctx ctx)
        {
            double cursor = double.NegativeInfinity;
            string lastBody = null;
            for (int p = 0; p < ctx.Line.Pieces.Count; p++)
            {
                Recording rec = ctx.Line.Pieces[p];
                if (rec?.TrackSections == null) continue;
                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    TrackSection s = rec.TrackSections[i];
                    if (s.source == TrackSectionSource.Predicted) continue;
                    if (double.IsNaN(s.startUT) || double.IsNaN(s.endUT)) continue;
                    // Pieces that share an instant (a chain boundary, a parent and its
                    // continuation) are de-duplicated by UT: the later piece wins.
                    if (s.endUT < cursor - 1e-6) continue;
                    double start = Math.Max(s.startUT, cursor);
                    string body = SectionBody(s) ?? lastBody ?? rec.StartBodyName;
                    var sec = new Sec { Rec = rec, S = s, Body = body, Start = start, End = s.endUT };
                    sec.Regime = RegimeOf(ctx, sec);
                    TrimEarlierSections(ctx, start);
                    ctx.Secs.Add(sec);
                    ctx.SectionView.Add(s);
                    cursor = Math.Max(cursor, s.endUT);
                    if (!string.IsNullOrEmpty(body)) lastBody = body;
                }
            }
        }

        // A later piece that starts before the previous piece's last section ends wins the
        // overlap: drop the earlier sections that lie wholly after its start.
        private static void TrimEarlierSections(Ctx ctx, double start)
        {
            while (ctx.Secs.Count > 0 && ctx.Secs[ctx.Secs.Count - 1].Start > start + 1e-6)
            {
                ctx.Secs.RemoveAt(ctx.Secs.Count - 1);
                ctx.SectionView.RemoveAt(ctx.SectionView.Count - 1);
            }
            if (ctx.Secs.Count > 0 && ctx.Secs[ctx.Secs.Count - 1].End > start)
                ctx.Secs[ctx.Secs.Count - 1].End = start;
        }

        /// <summary>The optimizer's own rule: a section's body is its first checkpoint's or
        /// first frame's (TrackSection has no body field).</summary>
        internal static string SectionBody(TrackSection s)
        {
            if (s.checkpoints != null && s.checkpoints.Count > 0 && !string.IsNullOrEmpty(s.checkpoints[0].bodyName))
                return s.checkpoints[0].bodyName;
            if (s.frames != null && s.frames.Count > 0 && !string.IsNullOrEmpty(s.frames[0].bodyName))
                return s.frames[0].bodyName;
            if (s.bodyFixedFrames != null && s.bodyFixedFrames.Count > 0)
                return s.bodyFixedFrames[0].bodyName;
            return null;
        }

        // A physics section takes its regime from its environment. An on-rails section is
        // always a coast (a packed vessel cannot thrust); its Air / Coast split comes from its
        // conic against the atmosphere top, never from its label (a background on-rails span
        // carries one fixed label).
        private static ProfileRegime RegimeOf(Ctx ctx, Sec sec)
        {
            if (sec.OnRails)
                return OnRailsInsideAtmosphere(ctx, sec) ? ProfileRegime.Air : ProfileRegime.Coast;
            switch (sec.S.environment)
            {
                case SegmentEnvironment.Atmospheric: return ProfileRegime.Air;
                case SegmentEnvironment.Approach: return ProfileRegime.Low;
                case SegmentEnvironment.SurfaceMobile:
                case SegmentEnvironment.SurfaceStationary: return ProfileRegime.Surface;
                case SegmentEnvironment.ExoPropulsive: return ProfileRegime.Burn;
                default: return ProfileRegime.Coast;
            }
        }

        private static bool OnRailsInsideAtmosphere(Ctx ctx, Sec sec)
        {
            if (sec.S.environment != SegmentEnvironment.Atmospheric) return false;
            var f = ctx.Facts(sec.Body);
            if (!f.Known || f.AtmosphereDepth <= 0 || sec.S.checkpoints == null) return false;
            for (int i = 0; i < sec.S.checkpoints.Count; i++)
            {
                OrbitSegment c = sec.S.checkpoints[i];
                double pe = c.semiMajorAxis * (1 - c.eccentricity);
                if (!double.IsNaN(pe) && pe < f.Radius + f.AtmosphereDepth) return true;
            }
            return false;
        }

        private static void ReadLineEnds(Ctx ctx)
        {
            PhasePlan plan = ctx.Plan;
            List<Recording> pieces = ctx.Line.Pieces;
            double start = double.PositiveInfinity, end = double.NegativeInfinity;
            List<DeltaVSnapshot> merged = null;
            for (int i = 0; i < pieces.Count; i++)
            {
                Recording r = pieces[i];
                if (r == null) continue;
                start = Math.Min(start, r.StartUT);
                end = Math.Max(end, r.EndUT);
                merged = DeltaVSnapshotList.Concat(merged, r.DeltaVSnapshots);
            }
            if (ctx.Secs.Count > 0)
            {
                start = Math.Min(start, ctx.Secs[0].Start);
                end = Math.Max(end, ctx.Secs[ctx.Secs.Count - 1].End);
            }
            plan.StartUT = double.IsInfinity(start) ? double.NaN : start;
            plan.EndUT = double.IsInfinity(end) ? double.NaN : end;
            Recording last = pieces[pieces.Count - 1];
            plan.Terminal = last?.TerminalStateValue;
            plan.EndBody = !string.IsNullOrEmpty(last?.TerminalOrbitBody) ? last.TerminalOrbitBody
                : ctx.Secs.Count > 0 ? ctx.Secs[ctx.Secs.Count - 1].Body : last?.StartBodyName;
            if (merged != null) ctx.Snapshots.AddRange(merged);
            plan.HasSnapshots = ctx.Snapshots.Count > 0;
        }

        // ------------------------------------------------------------------ engines

        // "Any engine thrusting" over the line, from the engine part events of every piece:
        // burn time is engine time (ignite to last shutdown).
        private static void BuildEngineIntervals(Ctx ctx)
        {
            var events = new List<PartEvent>();
            foreach (Recording r in ctx.Line.Pieces)
            {
                if (r?.PartEvents == null) continue;
                for (int i = 0; i < r.PartEvents.Count; i++)
                {
                    PartEventType t = r.PartEvents[i].eventType;
                    if (t == PartEventType.EngineIgnited || t == PartEventType.EngineShutdown
                        || t == PartEventType.EngineThrottle)
                        events.Add(r.PartEvents[i]);
                }
            }
            events.Sort((a, b) => a.ut.CompareTo(b.ut));
            var on = new Dictionary<ulong, string>();
            EngineFiring open = null;
            for (int i = 0; i < events.Count; i++)
            {
                PartEvent e = events[i];
                ulong key = ((ulong)e.partPersistentId << 8) | (uint)e.moduleIndex;
                bool fires = e.eventType == PartEventType.EngineIgnited
                    || (e.eventType == PartEventType.EngineThrottle && e.value > 0f);
                if (fires) on[key] = e.partName;
                else on.Remove(key);
                if (on.Count > 0 && open == null)
                    open = new EngineFiring { Start = e.ut };
                if (open != null && fires && !open.Parts.Contains(e.partName ?? ""))
                    open.Parts.Add(e.partName ?? "");
                if (on.Count == 0 && open != null)
                {
                    open.End = e.ut;
                    ctx.Firings.Add(open);
                    open = null;
                }
            }
            if (open != null)
            {
                open.End = ctx.Plan.EndUT;
                ctx.Firings.Add(open);
            }
        }

        // ------------------------------------------------------------------ items

        private static void BuildItems(Ctx ctx)
        {
            bool[] burnSec = MarkBurnSections(ctx);
            int n = ctx.Secs.Count;
            int i = 0;
            while (i < n)
            {
                Sec s = ctx.Secs[i];
                int j = i;
                Item item;
                if (burnSec[i])
                {
                    j = ctx.BurnRunEnd[i];
                    item = NewItem(ctx, ItemType.Burn, ProfileRegime.Burn, i, j);
                    TimeBurn(ctx, item);
                }
                else if (s.Regime == ProfileRegime.Coast || s.Regime == ProfileRegime.Burn)
                {
                    while (j + 1 < n && !burnSec[j + 1] && IsSpace(ctx.Secs[j + 1].Regime)
                        && SameBody(ctx.Secs[j].Body, ctx.Secs[j + 1].Body))
                        j++;
                    item = NewItem(ctx, ItemType.Coast, ProfileRegime.Coast, i, j);
                }
                else
                {
                    while (j + 1 < n && ctx.Secs[j + 1].Regime == s.Regime
                        && SameBody(ctx.Secs[j].Body, ctx.Secs[j + 1].Body))
                        j++;
                    item = NewItem(ctx, ItemType.Floor, s.Regime, i, j);
                }
                ctx.Items.Add(item);
                i = j + 1;
            }
            AttachFloorBurns(ctx);
        }

        private static bool IsSpace(ProfileRegime r) => r == ProfileRegime.Coast || r == ProfileRegime.Burn;

        private static bool SameBody(string a, string b)
            => string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || string.Equals(a, b, StringComparison.Ordinal);

        private static Item NewItem(Ctx ctx, ItemType type, ProfileRegime regime, int first, int last)
        {
            return new Item
            {
                Type = type,
                Regime = regime,
                First = first,
                Last = last,
                Body = ctx.Secs[first].Body,
                Start = ctx.Secs[first].Start,
                End = ctx.Secs[last].End,
            };
        }

        // The burn runs (RecordingOptimizer.ExtendBurnRun over the whole line), with RCS taps
        // (shorter than RcsTapSeconds of engine time) dropped back to coast.
        private static bool[] MarkBurnSections(Ctx ctx)
        {
            int n = ctx.Secs.Count;
            var marks = new bool[n];
            ctx.BurnRunEnd = new int[n];
            int i = 0;
            while (i < n)
            {
                if (!RecordingOptimizer.IsBurnSection(ctx.SectionView[i]))
                {
                    i++;
                    continue;
                }
                int last = Math.Max(i, Math.Min(n - 1, RecordingOptimizer.ExtendBurnRun(ctx.SectionView, i)));
                var probe = NewItem(ctx, ItemType.Burn, ProfileRegime.Burn, i, last);
                TimeBurn(ctx, probe);
                if (probe.BurnEnd - probe.BurnStart >= RcsTapSeconds)
                {
                    for (int k = i; k <= last; k++) marks[k] = true;
                    ctx.BurnRunEnd[i] = last;
                }
                i = last + 1;
            }
            return marks;
        }

        // Burn time is engine time: the firings that overlap the run's sections (a firing
        // continuous across the run's start, such as an ascent burn carried across the
        // atmosphere exit, counts whole). The section span stands in when no engine event
        // exists (RCS only, old data).
        private static void TimeBurn(Ctx ctx, Item b)
        {
            double lo = b.Start - EngineMatchSeconds, hi = b.End + EngineMatchSeconds;
            double start = double.NaN, end = double.NaN;
            for (int i = 0; i < ctx.Firings.Count; i++)
            {
                EngineFiring f = ctx.Firings[i];
                if (f.End < lo || f.Start > hi) continue;
                start = double.IsNaN(start) ? f.Start : Math.Min(start, f.Start);
                end = double.IsNaN(end) ? f.End : Math.Max(end, f.End);
                for (int k = 0; k < f.Parts.Count; k++)
                    if (!b.Engines.Contains(f.Parts[k])) b.Engines.Add(f.Parts[k]);
            }
            b.EngineTimed = !double.IsNaN(start);
            b.BurnStart = b.EngineTimed ? Math.Max(start, PreviousItemEnd(ctx)) : b.Start;
            b.BurnEnd = b.EngineTimed ? Math.Min(end, b.End + EngineMatchSeconds) : b.End;
            if (b.BurnEnd < b.BurnStart) b.BurnEnd = b.BurnStart;
        }

        private static double PreviousItemEnd(Ctx ctx)
        {
            for (int i = ctx.Items.Count - 1; i >= 0; i--)
                if (ctx.Items[i].Type == ItemType.Burn) return ctx.Items[i].BurnEnd;
            return double.NegativeInfinity;
        }

        // Firings at ground level (the atmosphere, the low band, the surface) that no space
        // burn covers become floor burns on the floor item their midpoint lies in; gaps up to
        // FloorBurnMergeSeconds merge (staging, throttle flicker).
        private static void AttachFloorBurns(Ctx ctx)
        {
            FloorBurn open = null;
            Item openItem = null;
            for (int i = 0; i < ctx.Firings.Count; i++)
            {
                EngineFiring f = ctx.Firings[i];
                if (CoveredBySpaceBurn(ctx, f)) continue;
                Item floor = FloorItemAt(ctx, 0.5 * (f.Start + f.End));
                if (floor == null) continue;
                if (open != null && openItem == floor && f.Start - open.End <= FloorBurnMergeSeconds)
                {
                    open.End = Math.Max(open.End, f.End);
                    foreach (string p in f.Parts) if (!open.Parts.Contains(p)) open.Parts.Add(p);
                    continue;
                }
                open = new FloorBurn { Start = f.Start, End = f.End };
                open.Parts.AddRange(f.Parts);
                openItem = floor;
                floor.FloorBurns.Add(open);
            }
            foreach (Item it in ctx.Items)
                it.FloorBurns.RemoveAll(fb => fb.End - fb.Start < RcsTapSeconds);
        }

        private static bool CoveredBySpaceBurn(Ctx ctx, EngineFiring f)
        {
            foreach (Item it in ctx.Items)
                if (it.Type == ItemType.Burn && f.Start <= it.BurnEnd && f.End >= it.BurnStart)
                    return true;
            return false;
        }

        private static Item FloorItemAt(Ctx ctx, double ut)
        {
            foreach (Item it in ctx.Items)
                if (it.Type == ItemType.Floor && ut >= it.Start && ut <= it.End) return it;
            return null;
        }

        // ------------------------------------------------------------------ orbits (stratum a)

        private static void ResolveOrbits(Ctx ctx)
        {
            int lastBurnIndex = -1;
            for (int i = 0; i < ctx.Items.Count; i++)
                if (ctx.Items[i].Type == ItemType.Burn) lastBurnIndex = i;
            double lastBurnEnd = lastBurnIndex >= 0 ? ctx.Items[lastBurnIndex].BurnEnd : double.NegativeInfinity;

            for (int i = 0; i < ctx.Items.Count; i++)
            {
                Item c = ctx.Items[i];
                if (c.Type != ItemType.Coast) continue;
                FindElements(ctx, c, i == ctx.Items.Count - 1, lastBurnEnd);
                if (!c.HasElements) c.HeldAltitude = HeldAltitude(ctx, c);
            }
            MarkEncounters(ctx);
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                Item c = ctx.Items[i];
                if (c.Type != ItemType.Coast) continue;
                c.StartOrbit = CoastOrbit(ctx, i, atEnd: false);
                c.EndOrbit = CoastOrbit(ctx, i, atEnd: true);
            }
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                if (ctx.Items[i].Type != ItemType.Burn) continue;
                ctx.Items[i].Before = BurnBeforeOrbit(ctx, i);
                ctx.Items[i].After = BurnAfterOrbit(ctx, i);
            }
        }

        // Stored tier: the elements of a non-predicted on-rails checkpoint in the coast; the
        // terminal orbit only for the line's last coast, and only when its epoch is at or after
        // the end of the line's last burn (a terminal can carry an orbit burns later changed).
        private static void FindElements(Ctx ctx, Item c, bool lastItem, double lastBurnEnd)
        {
            for (int i = c.First; i <= c.Last; i++)
            {
                Sec s = ctx.Secs[i];
                if (!s.OnRails || s.S.checkpoints == null) continue;
                for (int k = 0; k < s.S.checkpoints.Count; k++)
                {
                    OrbitSegment seg = s.S.checkpoints[k];
                    if (seg.isPredicted || seg.semiMajorAxis == 0 || double.IsNaN(seg.semiMajorAxis)) continue;
                    if (!string.IsNullOrEmpty(c.Body) && !string.Equals(seg.bodyName, c.Body, StringComparison.Ordinal))
                        continue;
                    if (!c.HasElements) c.FirstElements = seg;
                    c.LastElements = seg;
                    c.HasElements = true;
                }
            }
            if (c.HasElements || !lastItem) return;
            Recording last = ctx.Line.Pieces[ctx.Line.Pieces.Count - 1];
            if (last == null || !last.TerminalStateValue.HasValue || last.TerminalOrbitSemiMajorAxis == 0
                || double.IsNaN(last.TerminalOrbitSemiMajorAxis)
                || !string.Equals(last.TerminalOrbitBody, c.Body, StringComparison.Ordinal)
                || last.TerminalOrbitEpoch < lastBurnEnd)
                return;
            c.FirstElements = c.LastElements = last.TerminalOrbitAsSegment();
            c.HasElements = true;
            c.ElementsFromTerminal = true;
        }

        // Inferred tier: a physics coast whose altitude holds (tightly for two minutes, or
        // within 2 % for a quarter of the circular period at that radius) is bound and clear.
        // Per-section min / max altitude is a bound only, never a sample.
        private static double HeldAltitude(Ctx ctx, Item c)
        {
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            double start = double.NaN, end = double.NaN;
            for (int i = c.First; i <= c.Last; i++)
            {
                Sec s = ctx.Secs[i];
                if (s.OnRails || s.Relative || float.IsNaN(s.S.minAltitude) || float.IsNaN(s.S.maxAltitude))
                    continue;
                min = Math.Min(min, s.S.minAltitude);
                max = Math.Max(max, s.S.maxAltitude);
                if (double.IsNaN(start)) start = s.Start;
                end = s.End;
            }
            if (double.IsNaN(start) || max < min) return double.NaN;
            var f = ctx.Facts(c.Body);
            if (!f.Known) return double.NaN;
            double radius = f.Radius + 0.5 * (min + max);
            double floor = f.Radius + Math.Max(0, f.AtmosphereDepth);
            if (f.Radius + min < floor) return double.NaN;
            double span = end - start;
            double spread = (max - min) / radius;
            if (spread <= HoldTightFraction && span >= HoldTightSeconds) return 0.5 * (min + max);
            double period = f.GravParameter > 0 ? 2 * Math.PI * Math.Sqrt(radius * radius * radius / f.GravParameter) : double.NaN;
            if (spread <= HoldLooseFraction && !double.IsNaN(period) && span >= 0.25 * period)
                return 0.5 * (min + max);
            return double.NaN;
        }

        // Encounter (hindsight): a coast that ends in an SOI change is an encounter of the next
        // body; walking back over burns that only corrected the orbit (the semi-major axis moved
        // by at most BurnCorrectionRelSma) marks the coasts before them too.
        private static void MarkEncounters(Ctx ctx)
        {
            List<Item> items = ctx.Items;
            for (int i = 0; i + 1 < items.Count; i++)
            {
                Item c = items[i], next = items[i + 1];
                if (c.Type != ItemType.Coast || !IsSpaceItem(next) || SameBodyStrict(c.Body, next.Body))
                    continue;
                if (string.IsNullOrEmpty(c.Body) || string.IsNullOrEmpty(next.Body)) continue;
                c.EncounterBody = next.Body;
                c.DirectEncounter = true;
                c.EncounterIsExit = string.Equals(ctx.Facts(c.Body).ParentName, next.Body, StringComparison.Ordinal);
                int k = i;
                while (k - 2 >= 0 && items[k - 1].Type == ItemType.Burn && items[k - 2].Type == ItemType.Coast
                    && SameBodyStrict(items[k - 2].Body, c.Body) && IsCorrection(items[k - 2], items[k]))
                {
                    items[k - 2].EncounterBody = c.EncounterBody;
                    items[k - 2].EncounterIsExit = c.EncounterIsExit;
                    k -= 2;
                }
            }
        }

        private static bool IsSpaceItem(Item it) => it.Type == ItemType.Coast || it.Type == ItemType.Burn;

        private static bool SameBodyStrict(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);

        private static bool IsCorrection(Item before, Item after)
        {
            if (!before.HasElements || !after.HasElements) return false;
            double a0 = before.LastElements.semiMajorAxis, a1 = after.FirstElements.semiMajorAxis;
            if (double.IsNaN(a0) || double.IsNaN(a1) || Math.Sign(a0) != Math.Sign(a1)) return false;
            return Math.Abs(a1 - a0) / Math.Max(1.0, Math.Abs(a0)) <= RecordingOptimizer.BurnCorrectionRelSma;
        }

        // The orbit facts at one end of coast i, strongest tier first.
        private static ProfileOrbit CoastOrbit(Ctx ctx, int i, bool atEnd)
        {
            Item c = ctx.Items[i];
            var f = ctx.Facts(c.Body);
            ProfileOrbit o;
            if (c.HasElements)
                o = FromSegment(atEnd ? c.LastElements : c.FirstElements, f, ProfileEvidenceTier.Stored);
            else if (!double.IsNaN(c.HeldAltitude))
                o = Held(c.HeldAltitude, c.Body);
            else
                o = StructuralCoastOrbit(ctx, i);
            o.Body = c.Body;
            if (!string.IsNullOrEmpty(c.EncounterBody))
            {
                o.Encounter = c.EncounterBody;
                o.DirectEncounter = c.DirectEncounter;
                o.EncounterIsExit = c.EncounterIsExit;
                if (o.Tier == ProfileEvidenceTier.None) o.Tier = ProfileEvidenceTier.Structural;
            }
            return o;
        }

        private static ProfileOrbit StructuralCoastOrbit(Ctx ctx, int i)
        {
            Item c = ctx.Items[i];
            Item prev = i > 0 ? ctx.Items[i - 1] : null;
            if (EndsAtFloorEntry(ctx, i) || (prev != null && IsFloorExit(prev)))
                return Falling(ctx, c.Body, ProfileEvidenceTier.Structural);
            if (prev != null && prev.Type == ItemType.Coast && !SameBodyStrict(prev.Body, c.Body)
                && string.Equals(ctx.Facts(c.Body).ParentName, prev.Body, StringComparison.Ordinal))
                return new ProfileOrbit { Tier = ProfileEvidenceTier.Structural, Energy = ProfileOrbitEnergy.Escaping };
            return default(ProfileOrbit);
        }

        // A floor run entered from space proves the coast before it was falling: the
        // atmosphere or the ground; the low band only when the ground follows it.
        private static bool EndsAtFloorEntry(Ctx ctx, int i)
        {
            for (int k = i + 1; k < ctx.Items.Count; k++)
            {
                Item it = ctx.Items[k];
                if (it.Type != ItemType.Floor) return false;
                if (it.Regime == ProfileRegime.Air || it.Regime == ProfileRegime.Surface) return true;
            }
            return false;
        }

        private static bool IsFloorExit(Item floor)
            => floor.Type == ItemType.Floor
               && (floor.Regime == ProfileRegime.Air || floor.Regime == ProfileRegime.Surface
                   || floor.Regime == ProfileRegime.Low);

        private static ProfileOrbit Falling(Ctx ctx, string body, ProfileEvidenceTier tier)
            => new ProfileOrbit
            {
                Tier = tier,
                Body = body,
                Energy = ProfileOrbitEnergy.Bound,
                Periapsis = ctx.HasAtmosphere(body) ? ProfilePeriapsis.InAir : ProfilePeriapsis.SubSurface,
            };

        private static ProfileOrbit Held(double altitude, string body)
            => new ProfileOrbit
            {
                Tier = ProfileEvidenceTier.Inferred,
                Body = body,
                Energy = ProfileOrbitEnergy.Bound,
                Periapsis = ProfilePeriapsis.Clear,
                HeldAltitude = altitude,
                ApoapsisAltitude = altitude,
                PeriapsisAltitude = altitude,
                InclinationDeg = double.NaN,
            };

        /// <summary>The two-axis orbit facts of stored elements around a body.</summary>
        internal static ProfileOrbit FromSegment(OrbitSegment seg, RecordingOptimizer.BodyFacts f, ProfileEvidenceTier tier)
        {
            var o = new ProfileOrbit
            {
                Tier = tier,
                Body = seg.bodyName,
                HasElements = true,
                Elements = seg,
                InclinationDeg = seg.inclination,
                HeldAltitude = double.NaN,
            };
            double a = seg.semiMajorAxis, e = seg.eccentricity;
            bool closed = e < 1 && a > 0;
            double ap = closed ? a * (1 + e) : double.PositiveInfinity;
            double pe = a * (1 - e);
            double radius = f.Known ? f.Radius : 0;
            o.ApoapsisAltitude = ap - radius;
            o.PeriapsisAltitude = pe - radius;
            if (!f.Known)
            {
                o.Energy = closed ? ProfileOrbitEnergy.Bound : ProfileOrbitEnergy.Escaping;
                return o;
            }
            double soi = double.IsNaN(f.SoiRadius) || f.SoiRadius <= 0 ? double.PositiveInfinity : f.SoiRadius;
            o.Energy = closed && ap <= soi ? ProfileOrbitEnergy.Bound : ProfileOrbitEnergy.Escaping;
            double floor = f.Radius + Math.Max(0, f.AtmosphereDepth);
            o.Periapsis = pe < f.Radius ? ProfilePeriapsis.SubSurface
                : pe < floor ? ProfilePeriapsis.InAir : ProfilePeriapsis.Clear;
            return o;
        }
    }
}
