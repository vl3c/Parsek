using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Parsek
{
    /// <summary>
    /// Version-3 split rules: a space row is cut where a burn changes the orbit KIND
    /// (falling / in orbit / leaving), owner rulings Q-P1, the 2026-11-20 case table and
    /// "Yes, deorbit row" (docs/handoff/recording-split-review/atmosphere-pass-analysis.md
    /// sections 6-8). A burn that arrives in orbit ends the row before it (cut at the burn
    /// end: circularization, capture, raising the low point after aerobraking); a burn that
    /// leaves an orbit opens the next row (cut at the burn start: transfer, deorbit, direct
    /// descent). Every other burn stays inside its row.
    ///
    /// The kind is read only from data the recording already carries: the elements of its
    /// on-rails checkpoint sections and its terminal orbit, its own later SOI change, and an
    /// atmosphere / ground boundary (the vessel is at the floor there). Recorded point
    /// velocities are never read (their frame is not reliable near a planet). A burn with no
    /// such evidence on both sides is never cut. Design: optimizer-persistence-split.md
    /// section 14.
    /// </summary>
    internal static partial class RecordingOptimizer
    {
        /// <summary>A kind cut never makes a row shorter than this next to another cut or the
        /// recording's start or end: the kind cut is dropped (a suicide burn just above the
        /// approach band, an insertion right at the atmosphere exit).</summary>
        internal const double BurnCutMinRowSeconds = 45.0;

        /// <summary>Engine-off gaps up to this long inside one burn (staging) are merged. A gap
        /// spent on rails always ends the burn.</summary>
        internal const double BurnMergeGapSeconds = 10.0;

        /// <summary>A burn whose orbit before and after differ by at most this relative
        /// semi-major-axis step is a correction, not the departure (a transfer burn moves the
        /// semi-major axis by several times, or makes it negative). Wider than the re-aim
        /// classifier's 5 % heliocentric a-step: a mid-course correction of the mun-orbit
        /// fixture's transfer moved it 5.1 %.</summary>
        internal const double BurnCorrectionRelSma = 0.25;

        internal enum OrbitKind { Unknown = 0, Falling, InOrbit, Leaving }

        internal enum BurnCutKind
        {
            None = 0,
            Insertion,           // falling -> in orbit, cut at the burn end
            Capture,             // leaving -> in orbit, cut at the burn end
            CaptureToAtmosphere, // leaving -> falling at an atmosphere body, cut at the burn end
            Injection,           // falling -> leaving, cut at the burn end
            Departure,           // in orbit -> leaving, cut at the burn start
            Deorbit,             // in orbit -> falling, cut at the burn start
            DirectDescent,       // leaving -> falling at an airless body, cut at the burn start
        }

        /// <summary>How the recording starts, read from its chain predecessor: a recording cut
        /// at an atmosphere exit starts at the floor even though its own first section is in
        /// space.</summary>
        internal enum BurnStartContext { None = 0, FloorExit }

        /// <summary>What the kind rules need of a body. Unknown body -> no kind.</summary>
        internal struct BodyFacts
        {
            internal bool Known;
            internal double Radius;
            internal double AtmosphereDepth;
            internal double SoiRadius;
            internal double GravParameter;
            internal string ParentName;
        }

        private enum BurnItemType { Floor, Coast, Burn }

        private sealed class BurnItem
        {
            internal BurnItemType Type;
            internal int First;
            internal int Last;
            internal string Body;
            internal double StartUT;
            internal double EndUT;
            // Coast facts.
            internal bool LeavingByLookahead;
            internal string LeavingTarget;
            internal bool EnteredFromParent;
            internal bool EndsAtFloorEntry;
            internal bool StartsAtFloorExit;
            internal bool HasElements;
            internal OrbitSegment FirstElements;
            internal OrbitSegment LastElements;
            internal bool ElementsFromTerminal;
        }

        /// <summary>One judged burn: its kinds, the cut it makes, or why not.</summary>
        internal sealed class BurnDecision
        {
            internal int FirstSection;
            internal int LastSection;
            internal string Body;
            internal OrbitKind Before;
            internal OrbitKind After;
            internal BurnCutKind Kind;
            internal int CutSection = -1;
            internal bool Split;
            internal SplitBoundaryReason Reason;
        }

        /// <summary>The version-3 view of a recording: its burns and the boundary each kind
        /// cut (or each dropped kind cut) sits on.</summary>
        internal sealed class BurnMap
        {
            internal readonly List<BurnDecision> Burns = new List<BurnDecision>();
            internal readonly Dictionary<int, BurnDecision> AtSection = new Dictionary<int, BurnDecision>();
            internal int Unjudged;
            internal int KindKept;
            internal int ShortRow;
            internal int TransferWhole;
        }

        /// <summary>True when <paramref name="rec"/> is judged by the version-3 kind cuts:
        /// stamped 3 or later, judged by the version-2 rules, and neither an EVA kerbal nor
        /// debris (owner ruling: no kind cuts for those).</summary>
        internal static bool UsesBurnCutRules(Recording rec)
        {
            return rec != null
                && rec.SplitRuleVersion >= SplitRuleVersionBurnCuts
                && UsesCurrentSplitRules(rec)
                && !rec.IsDebris;
        }

        /// <summary>
        /// The burn map a split scan attaches to <paramref name="map"/>, with the start context
        /// read from the recording's chain predecessor in <paramref name="committed"/>.
        /// <paramref name="chains"/> is a per-scan cache (null on the first call).
        /// </summary>
        internal static void AttachBurnMapForScan(
            IReadOnlyList<Recording> committed, Recording rec, PassMap map,
            ref Dictionary<string, List<Recording>> chains)
        {
            if (map == null || !UsesBurnCutRules(rec)) return;
            map.Burns = BuildBurnMap(rec, map, ResolveBurnStartContext(committed, rec, ref chains));
        }

        /// <summary>
        /// FloorExit when the recording's chain predecessor (same chain, primary branch, the
        /// piece that ends where this one starts) ends below the floor (atmosphere, approach
        /// band or ground) on the body this one starts on, and this one starts in space.
        /// </summary>
        internal static BurnStartContext ResolveBurnStartContext(
            IReadOnlyList<Recording> committed, Recording rec, ref Dictionary<string, List<Recording>> chains)
        {
            if (committed == null || rec?.TrackSections == null || rec.TrackSections.Count == 0
                || string.IsNullOrEmpty(rec.ChainId) || rec.ChainBranch != 0)
                return BurnStartContext.None;
            if (SplitEnvironmentClass(rec.TrackSections[0].environment) != ExoSplitClass)
                return BurnStartContext.None;
            if (chains == null)
            {
                chains = new Dictionary<string, List<Recording>>(StringComparer.Ordinal);
                for (int i = 0; i < committed.Count; i++)
                {
                    var r = committed[i];
                    if (r == null || string.IsNullOrEmpty(r.ChainId) || r.ChainBranch != 0) continue;
                    if (!chains.TryGetValue(r.ChainId, out var list))
                        chains[r.ChainId] = list = new List<Recording>();
                    list.Add(r);
                }
            }
            if (!chains.TryGetValue(rec.ChainId, out var pieces)) return BurnStartContext.None;
            double start = rec.TrackSections[0].startUT;
            for (int i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i];
                if (ReferenceEquals(p, rec) || p.TrackSections == null || p.TrackSections.Count == 0
                    || !string.Equals(p.TreeId, rec.TreeId, StringComparison.Ordinal))
                    continue;
                var last = p.TrackSections[p.TrackSections.Count - 1];
                if (Math.Abs(last.endUT - start) > 1.0) continue;
                if (SplitEnvironmentClass(last.environment) == ExoSplitClass) continue;
                string pb = GetSectionBody(p, last);
                string rb = GetSectionBody(rec, rec.TrackSections[0]);
                if (!string.IsNullOrEmpty(pb) && !string.IsNullOrEmpty(rb) && pb != rb) continue;
                return BurnStartContext.FloorExit;
            }
            return BurnStartContext.None;
        }

        /// <summary>
        /// The version-3 boundary check, consulted by <see cref="IsSplittableBoundaryV2"/> at a
        /// boundary inside space on one body (which the version-2 rules never cut). True when
        /// the boundary is a judged kind cut; <paramref name="split"/> then says whether it is
        /// cut or was dropped (short row, interplanetary transfer kept whole).
        /// </summary>
        private static bool TryClassifyBurnBoundary(
            Recording rec, int s, PassMap map, out SplitBoundaryReason reason, out bool split)
        {
            reason = SplitBoundaryReason.NotABoundary;
            split = false;
            if (map == null || !UsesBurnCutRules(rec)) return false;
            if (map.Burns == null) map.Burns = BuildBurnMap(rec, map, BurnStartContext.None);
            if (!map.Burns.AtSection.TryGetValue(s, out var d)) return false;
            reason = d.Reason;
            split = d.Split;
            return true;
        }

        // ------------------------------------------------------------ the map

        internal static BurnMap BuildBurnMap(Recording rec, PassMap passMap, BurnStartContext context)
        {
            var result = new BurnMap();
            var sections = rec?.TrackSections;
            if (sections == null || sections.Count == 0) return result;
            var items = BuildBurnItems(rec);
            if (!HasBurn(items)) return result;
            var facts = new Dictionary<string, BodyFacts>(StringComparer.Ordinal);
            AnnotateCoasts(rec, items, context, facts);
            List<double> otherCuts = CollectNonBurnCutUTs(rec, passMap);
            double lastKept = rec.TrackSections[0].startUT;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Type != BurnItemType.Burn) continue;
                var d = JudgeBurn(rec, items, i, context, facts, result);
                result.Burns.Add(d);
                if (d.Kind == BurnCutKind.None) continue;
                ApplyCutGuards(rec, items, i, d, facts, otherCuts, ref lastKept, result);
                if (d.CutSection > 0) result.AtSection[d.CutSection] = d;
                LogBurnDecision(rec, d);
            }
            return result;
        }

        private static bool HasBurn(List<BurnItem> items)
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i].Type == BurnItemType.Burn) return true;
            return false;
        }

        private static bool IsBurnSection(TrackSection s)
        {
            return s.environment == SegmentEnvironment.ExoPropulsive
                && s.referenceFrame != ReferenceFrame.OrbitalCheckpoint;
        }

        private static List<BurnItem> BuildBurnItems(Recording rec)
        {
            var sections = rec.TrackSections;
            int n = sections.Count;
            var items = new List<BurnItem>();
            int i = 0;
            while (i < n)
            {
                int j = i;
                BurnItemType type;
                if (SplitEnvironmentClass(sections[i].environment) != ExoSplitClass)
                {
                    type = BurnItemType.Floor;
                    while (j + 1 < n && SplitEnvironmentClass(sections[j + 1].environment) != ExoSplitClass) j++;
                }
                else if (IsBurnSection(sections[i]))
                {
                    type = BurnItemType.Burn;
                    j = ExtendBurn(rec, i);
                }
                else
                {
                    type = BurnItemType.Coast;
                    while (j + 1 < n && SplitEnvironmentClass(sections[j + 1].environment) == ExoSplitClass
                        && !IsBurnSection(sections[j + 1]) && !SectionBodyChanged(sections[j], sections[j + 1]))
                        j++;
                }
                items.Add(new BurnItem
                {
                    Type = type,
                    First = i,
                    Last = j,
                    Body = GetSectionBody(rec, sections[i]),
                    StartUT = sections[i].startUT,
                    EndUT = sections[j].endUT,
                });
                i = j + 1;
            }
            return items;
        }

        /// <summary>The last section of the burn starting at <paramref name="first"/>: burn
        /// sections on one body, with physics-frame engine-off gaps up to
        /// <see cref="BurnMergeGapSeconds"/> merged in.</summary>
        private static int ExtendBurn(Recording rec, int first)
        {
            var sections = rec.TrackSections;
            int n = sections.Count;
            int last = first;
            while (true)
            {
                int k = last + 1;
                double gap = 0;
                while (k < n && SplitEnvironmentClass(sections[k].environment) == ExoSplitClass
                    && !IsBurnSection(sections[k])
                    && sections[k].referenceFrame != ReferenceFrame.OrbitalCheckpoint
                    && !SectionBodyChanged(sections[k - 1], sections[k]))
                {
                    gap += sections[k].endUT - sections[k].startUT;
                    if (gap > BurnMergeGapSeconds) break;
                    k++;
                }
                if (k >= n || gap > BurnMergeGapSeconds || !IsBurnSection(sections[k])
                    || SectionBodyChanged(sections[k - 1], sections[k]))
                    return last;
                last = k;
            }
        }

        private static void AnnotateCoasts(
            Recording rec, List<BurnItem> items, BurnStartContext context, Dictionary<string, BodyFacts> facts)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var c = items[i];
                if (c.Type != BurnItemType.Coast) continue;
                BurnItem prev = i > 0 ? items[i - 1] : null;
                BurnItem next = i + 1 < items.Count ? items[i + 1] : null;
                c.StartsAtFloorExit = prev != null ? prev.Type == BurnItemType.Floor
                    : context == BurnStartContext.FloorExit;
                c.EndsAtFloorEntry = next != null && next.Type == BurnItemType.Floor
                    && IsFloorEntryFalling(rec, next);
                if (prev != null && prev.Type == BurnItemType.Coast && IsKnownBodyPair(prev.Body, c.Body))
                    c.EnteredFromParent = string.Equals(ResolveFacts(c.Body, facts).ParentName, prev.Body,
                        StringComparison.Ordinal);
                FindCoastElements(rec, c, next == null);
                if (next != null && next.Type == BurnItemType.Coast && IsKnownBodyPair(c.Body, next.Body))
                {
                    c.LeavingByLookahead = true;
                    c.LeavingTarget = next.Body;
                }
            }
            // Leaving is judged from what the recording did next: walk back from each SOI
            // change over the burns that only corrected the orbit, so a Mun transfer ellipse
            // (closed, inside Kerbin's SOI) and the coasts before a mid-course correction read
            // as leaving too.
            for (int i = items.Count - 1; i >= 0; i--)
            {
                var c = items[i];
                if (c.Type != BurnItemType.Coast || !c.LeavingByLookahead) continue;
                int k = i;
                while (k - 2 >= 0 && items[k - 1].Type == BurnItemType.Burn
                    && items[k - 2].Type == BurnItemType.Coast
                    && string.Equals(items[k - 2].Body, c.Body, StringComparison.Ordinal)
                    && IsCorrectionBurn(items[k - 2], items[k]))
                {
                    items[k - 2].LeavingByLookahead = true;
                    items[k - 2].LeavingTarget = c.LeavingTarget;
                    k -= 2;
                }
            }
        }

        private static bool IsKnownBodyPair(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
                && !string.Equals(a, b, StringComparison.Ordinal);
        }

        /// <summary>A floor run entered from space proves the coast before it was falling: the
        /// atmosphere or the ground (the vessel is at the floor, coming down). An approach-band
        /// run counts only when it ends on the ground: the band alone is also flown by a low
        /// pass on an escape path.</summary>
        private static bool IsFloorEntryFalling(Recording rec, BurnItem floor)
        {
            var sections = rec.TrackSections;
            int first = SplitEnvironmentClass(sections[floor.First].environment);
            if (first == AtmosphericSplitClass || first == SurfaceSplitClass) return true;
            for (int i = floor.First; i <= floor.Last; i++)
                if (SplitEnvironmentClass(sections[i].environment) == SurfaceSplitClass) return true;
            return false;
        }

        private static void FindCoastElements(Recording rec, BurnItem c, bool lastItem)
        {
            var sections = rec.TrackSections;
            for (int i = c.First; i <= c.Last; i++)
            {
                var s = sections[i];
                if (s.referenceFrame != ReferenceFrame.OrbitalCheckpoint || s.checkpoints == null) continue;
                for (int k = 0; k < s.checkpoints.Count; k++)
                {
                    var seg = s.checkpoints[k];
                    if (seg.isPredicted || seg.semiMajorAxis == 0 || double.IsNaN(seg.semiMajorAxis)) continue;
                    if (!string.IsNullOrEmpty(c.Body) && !string.Equals(seg.bodyName, c.Body, StringComparison.Ordinal))
                        continue;
                    if (!c.HasElements) c.FirstElements = seg;
                    c.LastElements = seg;
                    c.HasElements = true;
                }
            }
            if (c.HasElements || !lastItem || !rec.TerminalStateValue.HasValue) return;
            if (string.IsNullOrEmpty(rec.TerminalOrbitBody) || rec.TerminalOrbitSemiMajorAxis == 0
                || !string.Equals(rec.TerminalOrbitBody, c.Body, StringComparison.Ordinal))
                return;
            c.FirstElements = c.LastElements = rec.TerminalOrbitAsSegment();
            c.HasElements = true;
            c.ElementsFromTerminal = true;
        }

        /// <summary>True when the burn between <paramref name="before"/> and
        /// <paramref name="after"/> only corrected the orbit: both sides carry elements on the
        /// same body and the semi-major axis moved by at most <see cref="BurnCorrectionRelSma"/>.</summary>
        private static bool IsCorrectionBurn(BurnItem before, BurnItem after)
        {
            if (!before.HasElements || !after.HasElements) return false;
            double a0 = before.LastElements.semiMajorAxis;
            double a1 = after.FirstElements.semiMajorAxis;
            if (double.IsNaN(a0) || double.IsNaN(a1) || Math.Sign(a0) != Math.Sign(a1)) return false;
            return Math.Abs(a1 - a0) / Math.Max(1.0, Math.Abs(a0)) <= BurnCorrectionRelSma;
        }

        // ------------------------------------------------------------ the kind

        /// <summary>The kind of coast <paramref name="c"/> at its start or end.</summary>
        private static OrbitKind CoastKind(BurnItem c, bool atEnd, Dictionary<string, BodyFacts> facts)
        {
            if (c.LeavingByLookahead) return OrbitKind.Leaving;
            if (c.HasElements)
            {
                var seg = atEnd ? c.LastElements : c.FirstElements;
                double ut = atEnd ? c.EndUT : c.StartUT;
                var k = ClassifyOrbitKind(seg, ut, ResolveFacts(c.Body, facts));
                if (k != OrbitKind.Unknown) return k;
            }
            // Crossed into a moon's or planet's SOI from its parent and no burn since: the
            // conic reaches beyond the SOI, so the vessel leaves again unless it comes down.
            if (c.EnteredFromParent) return c.EndsAtFloorEntry ? OrbitKind.Falling : OrbitKind.Leaving;
            if (c.EndsAtFloorEntry) return OrbitKind.Falling;
            // Out of the atmosphere / off the ground and no burn since: the conic passes inside
            // the floor (an escape this way would have shown as an SOI change above).
            if (c.StartsAtFloorExit) return OrbitKind.Falling;
            return OrbitKind.Unknown;
        }

        /// <summary>
        /// The orbit kind of <paramref name="seg"/> at <paramref name="ut"/> around a body with
        /// <paramref name="body"/>'s facts: in orbit = closed, inside the SOI, low point above
        /// the floor (atmosphere top or ground); falling = low point below the floor and not
        /// yet passed (a closed path inside the SOI always comes back to it); leaving = open,
        /// or beyond the SOI and not coming down first. Unknown when a fact is missing.
        /// </summary>
        internal static OrbitKind ClassifyOrbitKind(OrbitSegment seg, double ut, BodyFacts body)
        {
            double a = seg.semiMajorAxis;
            double e = seg.eccentricity;
            if (!body.Known || double.IsNaN(a) || double.IsNaN(e) || a == 0 || e < 0) return OrbitKind.Unknown;
            double floor = body.Radius + Math.Max(0, body.AtmosphereDepth);
            double pe = a * (1 - e);
            bool closed = e < 1 && a > 0;
            double soi = double.IsNaN(body.SoiRadius) || body.SoiRadius <= 0
                ? double.PositiveInfinity : body.SoiRadius;
            if (closed && a * (1 + e) <= soi)
                return pe < floor ? OrbitKind.Falling : OrbitKind.InOrbit;
            if (pe >= floor) return OrbitKind.Leaving;
            int inbound = IsInbound(seg, ut, body.GravParameter);
            if (inbound < 0) return OrbitKind.Unknown;
            return inbound == 1 ? OrbitKind.Falling : OrbitKind.Leaving;
        }

        /// <summary>1 when the vessel is moving toward the low point at <paramref name="ut"/>,
        /// 0 when away from it, -1 when it cannot be told.</summary>
        private static int IsInbound(OrbitSegment seg, double ut, double mu)
        {
            double a = Math.Abs(seg.semiMajorAxis);
            if (double.IsNaN(mu) || mu <= 0 || a <= 0 || double.IsNaN(seg.meanAnomalyAtEpoch)) return -1;
            double n = Math.Sqrt(mu / (a * a * a));
            double m = seg.meanAnomalyAtEpoch + n * (ut - seg.epoch);
            if (double.IsNaN(m) || double.IsInfinity(m)) return -1;
            if (seg.eccentricity >= 1) return m < 0 ? 1 : 0;
            double twoPi = 2 * Math.PI;
            m %= twoPi;
            if (m < 0) m += twoPi;
            return m > Math.PI ? 1 : 0;
        }

        private static BurnDecision JudgeBurn(
            Recording rec, List<BurnItem> items, int i, BurnStartContext context,
            Dictionary<string, BodyFacts> facts, BurnMap result)
        {
            var b = items[i];
            BurnItem prev = i > 0 ? items[i - 1] : null;
            BurnItem next = i + 1 < items.Count ? items[i + 1] : null;
            var d = new BurnDecision { FirstSection = b.First, LastSection = b.Last, Body = b.Body };
            d.Before = KindBeforeBurn(b, prev, context, facts);
            d.After = KindAfterBurn(rec, b, next, facts);
            if (d.Before == OrbitKind.Unknown || d.After == OrbitKind.Unknown)
            {
                result.Unjudged++;
                return d;
            }
            if (d.Before == d.After)
            {
                result.KindKept++;
                return d;
            }
            d.Kind = ResolveBurnCutKind(d.Before, d.After, ResolveFacts(b.Body, facts));
            bool atEnd = d.Kind == BurnCutKind.Insertion || d.Kind == BurnCutKind.Capture
                || d.Kind == BurnCutKind.CaptureToAtmosphere || d.Kind == BurnCutKind.Injection;
            if (atEnd && next != null && next.Type == BurnItemType.Coast) d.CutSection = b.Last + 1;
            else if (!atEnd && prev != null && prev.Type == BurnItemType.Coast) d.CutSection = b.First;
            d.Reason = ReasonForBurnCut(d.Kind);
            d.Split = d.CutSection > 0;
            if (!d.Split) result.Unjudged++;
            return d;
        }

        private static OrbitKind KindBeforeBurn(
            BurnItem b, BurnItem prev, BurnStartContext context, Dictionary<string, BodyFacts> facts)
        {
            if (prev == null) return context == BurnStartContext.FloorExit ? OrbitKind.Falling : OrbitKind.Unknown;
            if (prev.Type == BurnItemType.Floor) return OrbitKind.Falling;
            if (prev.Type != BurnItemType.Coast || !string.Equals(prev.Body, b.Body, StringComparison.Ordinal))
                return OrbitKind.Unknown;
            return CoastKind(prev, atEnd: true, facts);
        }

        private static OrbitKind KindAfterBurn(
            Recording rec, BurnItem b, BurnItem next, Dictionary<string, BodyFacts> facts)
        {
            if (next == null)
            {
                // The recording ends inside the burn: its terminal orbit is the orbit it reached.
                if (!rec.TerminalStateValue.HasValue || rec.TerminalOrbitSemiMajorAxis == 0
                    || !string.Equals(rec.TerminalOrbitBody, b.Body, StringComparison.Ordinal))
                    return OrbitKind.Unknown;
                return ClassifyOrbitKind(rec.TerminalOrbitAsSegment(), b.EndUT, ResolveFacts(b.Body, facts));
            }
            if (next.Type == BurnItemType.Floor)
                return IsFloorEntryFalling(rec, next) ? OrbitKind.Falling : OrbitKind.Unknown;
            if (next.Type != BurnItemType.Coast || !string.Equals(next.Body, b.Body, StringComparison.Ordinal))
                return OrbitKind.Unknown;
            return CoastKind(next, atEnd: false, facts);
        }

        internal static BurnCutKind ResolveBurnCutKind(OrbitKind before, OrbitKind after, BodyFacts body)
        {
            if (after == OrbitKind.InOrbit)
                return before == OrbitKind.Falling ? BurnCutKind.Insertion : BurnCutKind.Capture;
            if (before == OrbitKind.InOrbit)
                return after == OrbitKind.Leaving ? BurnCutKind.Departure : BurnCutKind.Deorbit;
            if (before == OrbitKind.Falling) return BurnCutKind.Injection;
            return body.AtmosphereDepth > 0 ? BurnCutKind.CaptureToAtmosphere : BurnCutKind.DirectDescent;
        }

        private static SplitBoundaryReason ReasonForBurnCut(BurnCutKind kind)
        {
            switch (kind)
            {
                case BurnCutKind.Insertion: return SplitBoundaryReason.OrbitInsertionBurnEnd;
                case BurnCutKind.Capture: return SplitBoundaryReason.CaptureBurnEnd;
                case BurnCutKind.CaptureToAtmosphere: return SplitBoundaryReason.CaptureToAtmosphereBurnEnd;
                case BurnCutKind.Injection: return SplitBoundaryReason.InjectionBurnEnd;
                case BurnCutKind.Departure: return SplitBoundaryReason.DepartureBurnStart;
                case BurnCutKind.Deorbit: return SplitBoundaryReason.DeorbitBurnStart;
                case BurnCutKind.DirectDescent: return SplitBoundaryReason.DirectDescentBurnStart;
                default: return SplitBoundaryReason.NotABoundary;
            }
        }

        // ------------------------------------------------------------ guards

        private static void ApplyCutGuards(
            Recording rec, List<BurnItem> items, int i, BurnDecision d, Dictionary<string, BodyFacts> facts,
            List<double> otherCuts, ref double lastKept, BurnMap result)
        {
            if (!d.Split) return;
            if (IsInterplanetaryTransferCut(items, i, d, facts))
            {
                d.Split = false;
                d.Reason = SplitBoundaryReason.SuppressedInterplanetaryTransfer;
                result.TransferWhole++;
                return;
            }
            double ut = rec.TrackSections[d.CutSection].startUT;
            double prevCut = lastKept;
            double nextCut = rec.TrackSections[rec.TrackSections.Count - 1].endUT;
            for (int k = 0; k < otherCuts.Count; k++)
            {
                if (otherCuts[k] <= ut && otherCuts[k] > prevCut) prevCut = otherCuts[k];
                if (otherCuts[k] > ut && otherCuts[k] < nextCut) nextCut = otherCuts[k];
            }
            if (ut - prevCut < BurnCutMinRowSeconds || nextCut - ut < BurnCutMinRowSeconds)
            {
                d.Split = false;
                d.Reason = SplitBoundaryReason.SuppressedBurnShortRow;
                result.ShortRow++;
                return;
            }
            lastKept = ut;
        }

        /// <summary>
        /// Phase 0 (re-aim): a transfer between two bodies that orbit the same parent (Kerbin to
        /// Duna around the Sun, Vall to Tylo around Jool) stays one row. The re-aim classifier
        /// needs the parking orbit, the coast around the common parent and the arrival in ONE
        /// recording, and the loiter cut compresses the parking orbit of that recording (todo
        /// REAIM-CLASSIFIER-FRAGILE-TO-MEMBER-SPLITS). So no kind cut is made for a burn around
        /// the parent between two of its children, for a departure / injection after which the
        /// recording reaches the parent and then a sibling, or for a capture / descent at a
        /// body the recording reached from a sibling through the parent.
        /// </summary>
        private static bool IsInterplanetaryTransferCut(
            List<BurnItem> items, int i, BurnDecision d, Dictionary<string, BodyFacts> facts)
        {
            string b = d.Body;
            var body = ResolveFacts(b, facts);
            if (!body.Known) return false;
            string before = AdjacentOtherBody(items, i, b, -1, out _);
            string after = AdjacentOtherBody(items, i, b, +1, out _);
            if (IsChildOf(before, b, facts) && IsChildOf(after, b, facts)
                && !string.Equals(before, after, StringComparison.Ordinal))
                return true;
            string parent = body.ParentName;
            if (string.IsNullOrEmpty(parent)) return false;
            switch (d.Kind)
            {
                case BurnCutKind.Departure:
                case BurnCutKind.Injection:
                    return ReachesSiblingThroughParent(items, i, b, parent, +1, facts);
                case BurnCutKind.Capture:
                case BurnCutKind.CaptureToAtmosphere:
                case BurnCutKind.DirectDescent:
                    return ReachesSiblingThroughParent(items, i, b, parent, -1, facts);
                default:
                    return false;
            }
        }

        /// <summary>The first body other than <paramref name="body"/> walking from item
        /// <paramref name="i"/> in <paramref name="dir"/> (-1 earlier, +1 later), or null.</summary>
        private static string AdjacentOtherBody(List<BurnItem> items, int i, string body, int dir, out int at)
        {
            for (int k = i + dir; k >= 0 && k < items.Count; k += dir)
            {
                string kb = items[k].Body;
                if (!string.IsNullOrEmpty(kb) && !string.Equals(kb, body, StringComparison.Ordinal))
                {
                    at = k;
                    return kb;
                }
            }
            at = -1;
            return null;
        }

        /// <summary>True when, walking from item <paramref name="i"/> in <paramref name="dir"/>,
        /// the recording is at some point around <paramref name="parent"/> next to a time at
        /// another child of it (later: parent then sibling; earlier: sibling then parent).</summary>
        private static bool ReachesSiblingThroughParent(
            List<BurnItem> items, int i, string body, string parent, int dir, Dictionary<string, BodyFacts> facts)
        {
            bool atParent = false;
            for (int k = i + dir; k >= 0 && k < items.Count; k += dir)
            {
                string kb = items[k].Body;
                if (string.IsNullOrEmpty(kb)) continue;
                if (string.Equals(kb, parent, StringComparison.Ordinal)) { atParent = true; continue; }
                if (atParent && !string.Equals(kb, body, StringComparison.Ordinal) && IsChildOf(kb, parent, facts))
                    return true;
                if (atParent) atParent = false;
            }
            return false;
        }

        private static bool IsChildOf(string child, string parent, Dictionary<string, BodyFacts> facts)
        {
            if (string.IsNullOrEmpty(child) || string.IsNullOrEmpty(parent)) return false;
            return string.Equals(ResolveFacts(child, facts).ParentName, parent, StringComparison.Ordinal);
        }

        /// <summary>The UTs of the boundaries the version-2 rules cut (class or body changes):
        /// the neighbours a kind cut keeps its distance from.</summary>
        private static List<double> CollectNonBurnCutUTs(Recording rec, PassMap passMap)
        {
            var uts = new List<double>();
            var sections = rec.TrackSections;
            for (int s = 1; s < sections.Count; s++)
            {
                bool classChange = SplitEnvironmentClass(sections[s - 1].environment)
                    != SplitEnvironmentClass(sections[s].environment);
                if (!classChange && !SectionBodyChanged(sections[s - 1], sections[s])) continue;
                if (IsSplittableBoundaryV2(rec, s, passMap, out _)) uts.Add(sections[s].startUT);
            }
            return uts;
        }

        private static void LogBurnDecision(Recording rec, BurnDecision d)
        {
            var ic = CultureInfo.InvariantCulture;
            var sections = rec.TrackSections;
            string outcome = d.Split ? "cut" : d.Reason.ToString();
            ParsekLog.VerboseOnChange("Optimizer",
                identity: string.Format(ic, "burn-kind|{0}|{1:R}", rec.RecordingId, sections[d.FirstSection].startUT),
                stateKey: string.Format(ic, "{0}|{1}|{2}|{3}", d.Before, d.After, d.Kind, outcome),
                message: string.Format(ic,
                    "Burn kind change: rec={0} body={1} burn=[{2:F2},{3:F2}] {4} -> {5} ({6}) {7}{8}",
                    rec.RecordingId, d.Body ?? "?", sections[d.FirstSection].startUT,
                    sections[d.LastSection].endUT, d.Before, d.After, d.Kind, outcome,
                    d.CutSection > 0
                        ? string.Format(ic, " at sec={0} ut={1:F2}", d.CutSection, sections[d.CutSection].startUT)
                        : ""));
        }

        // ------------------------------------------------------------ roles

        /// <summary>The role of the row a kind cut starts, or null for any other cut. A deorbit
        /// row is a "descent" row at a body with no atmosphere; a capture into an aerobraking
        /// orbit is an aerobraking row when its first pass ends back in space.</summary>
        internal static string ResolveBurnCutTailRole(SplitBoundaryReason reason, Recording tail)
        {
            switch (reason)
            {
                case SplitBoundaryReason.OrbitInsertionBurnEnd:
                case SplitBoundaryReason.CaptureBurnEnd:
                    return HoldsInterplanetaryTransfer(tail) ? SegmentRoles.Transfer : SegmentRoles.Orbit;
                case SplitBoundaryReason.DepartureBurnStart:
                case SplitBoundaryReason.InjectionBurnEnd:
                    return SegmentRoles.Transfer;
                case SplitBoundaryReason.DirectDescentBurnStart:
                    return SegmentRoles.Descent;
                case SplitBoundaryReason.DeorbitBurnStart:
                    return TailBodyHasAtmosphere(tail) ? SegmentRoles.Deorbit : SegmentRoles.Descent;
                case SplitBoundaryReason.CaptureToAtmosphereBurnEnd:
                    return FirstPassFromSpaceIsBraking(tail) ? SegmentRoles.Aerobraking : SegmentRoles.Deorbit;
                default:
                    return null;
            }
        }

        /// <summary>The role of the first half of a split: the content-read role
        /// (<see cref="ResolveHeadRoleAfterCut"/>), except that an unclassified space row cut by
        /// a kind cut is named by the cut (circularization before an insertion, orbit before a
        /// departure or deorbit, transfer before a capture), and one that holds an
        /// interplanetary transfer kept whole is a transfer row.</summary>
        internal static string ResolveHeadRoleAfterSplit(Recording head, SplitBoundaryReason reason)
        {
            string role = ResolveHeadRoleAfterCut(head);
            if (role != null && !string.Equals(role, SegmentRoles.Space, StringComparison.Ordinal)) return role;
            if (ReasonForBurnCutIsKind(reason) && HoldsInterplanetaryTransfer(head)) return SegmentRoles.Transfer;
            switch (reason)
            {
                case SplitBoundaryReason.OrbitInsertionBurnEnd: return SegmentRoles.Circularization;
                case SplitBoundaryReason.InjectionBurnEnd: return SegmentRoles.Injection;
                case SplitBoundaryReason.DepartureBurnStart:
                case SplitBoundaryReason.DeorbitBurnStart:
                    return SegmentRoles.Orbit;
                case SplitBoundaryReason.CaptureBurnEnd:
                case SplitBoundaryReason.CaptureToAtmosphereBurnEnd:
                case SplitBoundaryReason.DirectDescentBurnStart:
                    return SegmentRoles.Transfer;
                default:
                    return role;
            }
        }

        private static bool ReasonForBurnCutIsKind(SplitBoundaryReason reason)
        {
            return reason >= SplitBoundaryReason.OrbitInsertionBurnEnd
                && reason <= SplitBoundaryReason.DirectDescentBurnStart;
        }

        /// <summary>True when the row holds a burn whose kind cut the interplanetary guard
        /// dropped: the row is (part of) a transfer between two children of one body.</summary>
        internal static bool HoldsInterplanetaryTransfer(Recording rec)
        {
            if (!UsesBurnCutRules(rec) || rec.TrackSections == null || rec.TrackSections.Count < 2) return false;
            var burns = BuildBurnMap(rec, BuildPassMap(rec), BurnStartContext.None);
            for (int i = 0; i < burns.Burns.Count; i++)
                if (burns.Burns[i].Reason == SplitBoundaryReason.SuppressedInterplanetaryTransfer) return true;
            return false;
        }

        private static bool TailBodyHasAtmosphere(Recording tail)
        {
            if (tail?.TrackSections == null || tail.TrackSections.Count == 0) return false;
            return ResolveBodyFacts(GetSectionBody(tail, tail.TrackSections[0])).AtmosphereDepth > 0;
        }

        private static bool FirstPassFromSpaceIsBraking(Recording tail)
        {
            if (tail?.TrackSections == null || tail.TrackSections.Count == 0) return false;
            var map = BuildPassMap(tail);
            for (int i = 0; i < map.Stretches.Count; i++)
                if (map.Stretches[i].FromSpace)
                    return map.Stretches[i].Kind == PassKind.Braking;
            return false;
        }

        // ------------------------------------------------------------ body facts

        private static BodyFacts ResolveFacts(string name, Dictionary<string, BodyFacts> cache)
        {
            if (string.IsNullOrEmpty(name)) return default(BodyFacts);
            if (cache != null && cache.TryGetValue(name, out var f)) return f;
            f = ResolveBodyFacts(name);
            if (cache != null) cache[name] = f;
            return f;
        }

        /// <summary>The live body when the game has one by that name (a planet pack's bodies
        /// included); with no live body registry (headless) the stock system.</summary>
        internal static BodyFacts ResolveBodyFacts(string name)
        {
            if (string.IsNullOrEmpty(name)) return default(BodyFacts);
            try
            {
                if (TryReadLiveBodyFactsCore(name, out var live)) return live;
            }
            catch (Exception)
            {
                // No live registry (unit tests, early load): the stock table below.
            }
            return StockBodyFacts(name);
        }

        // Keeps the FlightGlobals read (and its static initializer, which fails headless) out
        // of every method a test calls.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryReadLiveBodyFactsCore(string name, out BodyFacts facts)
        {
            facts = default(BodyFacts);
            if (FlightGlobals.Bodies == null) return false;
            CelestialBody body = FlightGlobals.GetBodyByName(name);
            if (object.ReferenceEquals(body, null)) return false;
            CelestialBody parent = body.referenceBody;
            bool hasParent = !object.ReferenceEquals(parent, null) && !object.ReferenceEquals(parent, body);
            facts = new BodyFacts
            {
                Known = true,
                Radius = body.Radius,
                AtmosphereDepth = body.atmosphere ? body.atmosphereDepth : 0,
                SoiRadius = hasParent ? body.sphereOfInfluence : double.PositiveInfinity,
                GravParameter = body.gravParameter,
                ParentName = hasParent ? parent.name : null,
            };
            return true;
        }

        // The stock KSP system (KSP 1.12): radius, atmosphere top, SOI, GM, parent. Filled once
        // by the type initializer and only read after it.
        private static readonly Dictionary<string, BodyFacts> StockBodies =
            new Dictionary<string, BodyFacts>(StringComparer.Ordinal)
            {
                { "Sun", Stock(261600000, 600000, double.PositiveInfinity, 1.1723328e18, null) },
                { "Kerbol", Stock(261600000, 600000, double.PositiveInfinity, 1.1723328e18, null) },
                { "Moho", Stock(250000, 0, 9646663.0, 1.6860938e11, "Sun") },
                { "Eve", Stock(700000, 90000, 85109365, 8.1717302e12, "Sun") },
                { "Gilly", Stock(13000, 0, 126123.27, 8289449.8, "Eve") },
                { "Kerbin", Stock(600000, 70000, 84159286, 3.5316e12, "Sun") },
                { "Mun", Stock(200000, 0, 2429559.1, 6.5138398e10, "Kerbin") },
                { "Minmus", Stock(60000, 0, 2247428.4, 1.7658e9, "Kerbin") },
                { "Duna", Stock(320000, 50000, 47921949, 3.0136321e11, "Sun") },
                { "Ike", Stock(130000, 0, 1049598.9, 1.8568369e10, "Duna") },
                { "Dres", Stock(138000, 0, 32832840, 2.1484489e10, "Sun") },
                { "Jool", Stock(6000000, 200000, 2.4559852e9, 2.82528e14, "Sun") },
                { "Laythe", Stock(500000, 50000, 3723645.8, 1.962e12, "Jool") },
                { "Vall", Stock(300000, 0, 2406401.4, 2.074815e11, "Jool") },
                { "Tylo", Stock(600000, 0, 10856518, 2.82528e12, "Jool") },
                { "Bop", Stock(65000, 0, 1221060.9, 2.4868349e9, "Jool") },
                { "Pol", Stock(44000, 0, 1042138.9, 7.2170208e8, "Jool") },
                { "Eeloo", Stock(210000, 0, 1.1908294e8, 7.4410815e10, "Sun") },
            };

        /// <summary>The stock KSP system's facts for <paramref name="name"/>; unknown body ->
        /// not <see cref="BodyFacts.Known"/>.</summary>
        internal static BodyFacts StockBodyFacts(string name)
        {
            if (string.IsNullOrEmpty(name)) return default(BodyFacts);
            return StockBodies.TryGetValue(name, out var f) ? f : default(BodyFacts);
        }

        private static BodyFacts Stock(double radius, double atmo, double soi, double mu, string parent)
        {
            return new BodyFacts
            {
                Known = true,
                Radius = radius,
                AtmosphereDepth = atmo,
                SoiRadius = soi,
                GravParameter = mu,
                ParentName = parent,
            };
        }
    }
}
