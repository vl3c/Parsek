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
        /// space. An approach-band exit is kept apart: orbit can be reached inside the band
        /// (the approach debounce), so only a burn that starts right there reads falling.</summary>
        internal enum BurnStartContext { None = 0, FloorExit, ApproachExit }

        /// <summary>The chain pieces before and after the recording (same chain, primary branch,
        /// same tree), so the interplanetary guard reads the whole line a vessel flew and an
        /// earlier cut can never make a later suppressed cut appear.</summary>
        internal sealed class BurnLineContext
        {
            internal readonly List<Recording> Before = new List<Recording>();
            internal readonly List<Recording> After = new List<Recording>();
        }

        /// <summary>What the kind rules need of a body. Unknown body -> no kind.</summary>
        internal struct BodyFacts
        {
            internal bool Known;
            internal double Radius;
            internal double AtmosphereDepth;
            internal double SoiRadius;
            internal double GravParameter;
            internal string ParentName;
            /// <summary>The body's own orbit around its parent (NaN for the root).</summary>
            internal double OrbitSma;
            internal double OrbitEcc;
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
            // Floor facts: the run holds a real surface section (a landing or a splashdown).
            internal bool HasSurface;
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
            EnsureChainCache(committed, ref chains);
            BurnStartContext start = ResolveBurnStartContext(committed, rec, ref chains);
            map.Burns = BuildBurnMap(rec, map, start, ResolveBurnLineContext(rec, chains));
        }

        /// <summary>Builds the per-scan cache: every committed primary-branch chain piece, by
        /// chain id.</summary>
        private static void EnsureChainCache(
            IReadOnlyList<Recording> committed, ref Dictionary<string, List<Recording>> chains)
        {
            if (chains != null || committed == null) return;
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

        /// <summary>The chain piece before (<paramref name="dir"/> -1) or after (+1)
        /// <paramref name="rec"/> on its chain and tree, through the shared chain-index helpers
        /// (a hole in the indices is stepped over), or null.</summary>
        private static Recording ChainNeighbour(Recording rec, List<Recording> pieces, int dir)
        {
            int index = dir < 0
                ? RecordingChainQueries.PreviousChainIndexBefore(rec, pieces)
                : RecordingChainQueries.NextChainIndexAfter(rec, pieces);
            if (index == RecordingChainQueries.NoPreviousChainIndex
                || index == RecordingChainQueries.NoNextChainIndex)
                return null;
            for (int i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i];
                if (!ReferenceEquals(p, rec) && p.ChainIndex == index && p.ChainBranch == rec.ChainBranch
                    && string.Equals(p.TreeId, rec.TreeId, StringComparison.Ordinal))
                    return p;
            }
            return null;
        }

        /// <summary>The chain pieces before and after <paramref name="rec"/>, in chain order, from
        /// the per-scan chain cache (null when the recording is in no chain).</summary>
        internal static BurnLineContext ResolveBurnLineContext(
            Recording rec, Dictionary<string, List<Recording>> chains)
        {
            if (rec == null || chains == null || string.IsNullOrEmpty(rec.ChainId) || rec.ChainBranch != 0)
                return null;
            if (!chains.TryGetValue(rec.ChainId, out var pieces) || pieces.Count < 2) return null;
            var line = new BurnLineContext();
            var cur = rec;
            for (int guard = 0; guard < pieces.Count; guard++)
            {
                cur = ChainNeighbour(cur, pieces, -1);
                if (cur == null) break;
                if (cur.TrackSections != null && cur.TrackSections.Count > 0) line.Before.Insert(0, cur);
            }
            cur = rec;
            for (int guard = 0; guard < pieces.Count; guard++)
            {
                cur = ChainNeighbour(cur, pieces, +1);
                if (cur == null) break;
                if (cur.TrackSections != null && cur.TrackSections.Count > 0) line.After.Add(cur);
            }
            return line.Before.Count == 0 && line.After.Count == 0 ? null : line;
        }

        /// <summary>
        /// FloorExit when the recording's chain predecessor (same chain and tree, primary branch,
        /// ending where this one starts) ends in the atmosphere or on the ground on the body this
        /// one starts on, and this one starts in space; ApproachExit when it ends in the approach
        /// band.
        /// </summary>
        internal static BurnStartContext ResolveBurnStartContext(
            IReadOnlyList<Recording> committed, Recording rec, ref Dictionary<string, List<Recording>> chains)
        {
            if (committed == null || rec?.TrackSections == null || rec.TrackSections.Count == 0
                || string.IsNullOrEmpty(rec.ChainId) || rec.ChainBranch != 0)
                return BurnStartContext.None;
            if (SplitEnvironmentClass(rec.TrackSections[0].environment) != ExoSplitClass)
                return BurnStartContext.None;
            EnsureChainCache(committed, ref chains);
            if (!chains.TryGetValue(rec.ChainId, out var pieces)) return BurnStartContext.None;
            var p = ChainNeighbour(rec, pieces, -1);
            if (p?.TrackSections == null || p.TrackSections.Count == 0) return BurnStartContext.None;
            var last = p.TrackSections[p.TrackSections.Count - 1];
            if (Math.Abs(last.endUT - rec.TrackSections[0].startUT) > 1.0) return BurnStartContext.None;
            if (SplitEnvironmentClass(last.environment) == ExoSplitClass) return BurnStartContext.None;
            string pb = GetSectionBody(p, last);
            string rb = GetSectionBody(rec, rec.TrackSections[0]);
            if (!string.IsNullOrEmpty(pb) && !string.IsNullOrEmpty(rb) && pb != rb) return BurnStartContext.None;
            return SplitEnvironmentClass(last.environment) == ApproachSplitClass
                ? BurnStartContext.ApproachExit : BurnStartContext.FloorExit;
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

        internal static BurnMap BuildBurnMap(
            Recording rec, PassMap passMap, BurnStartContext context, BurnLineContext line = null)
        {
            var result = new BurnMap();
            var sections = rec?.TrackSections;
            if (sections == null || sections.Count == 0) return result;
            var items = BuildBurnItems(rec);
            if (!HasBurn(items)) return result;
            var facts = new Dictionary<string, BodyFacts>(StringComparer.Ordinal);
            AnnotateCoasts(rec, items, context, facts);
            List<double> otherCuts = CollectNonBurnCutUTs(rec, passMap);
            int lineOffset;
            List<BurnItem> lineItems = BuildLineItems(items, line, out lineOffset);
            double lastKept = rec.TrackSections[0].startUT;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Type != BurnItemType.Burn) continue;
                var d = JudgeBurn(rec, items, i, context, facts, result);
                result.Burns.Add(d);
                if (d.Kind == BurnCutKind.None) continue;
                ApplyCutGuards(rec, lineItems, i + lineOffset, d, facts, otherCuts, ref lastKept, result);
                if (d.CutSection > 0) result.AtSection[d.CutSection] = d;
                LogBurnDecision(rec, d);
            }
            return result;
        }

        /// <summary>The recording's items with the chain pieces before and after it around them
        /// (for the guard's body walks only); <paramref name="offset"/> is where the recording's
        /// own items start.</summary>
        private static List<BurnItem> BuildLineItems(List<BurnItem> items, BurnLineContext line, out int offset)
        {
            offset = 0;
            if (line == null) return items;
            var all = new List<BurnItem>();
            foreach (var p in line.Before) all.AddRange(BuildBurnItems(p));
            offset = all.Count;
            all.AddRange(items);
            foreach (var p in line.After) all.AddRange(BuildBurnItems(p));
            return all;
        }

        private static bool HasBurn(List<BurnItem> items)
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i].Type == BurnItemType.Burn) return true;
            return false;
        }

        /// <summary>A physics-frame powered space section: the unit of a burn. Shared with the
        /// Mission Profile's burn definition (MissionPhaseAnalyzer).</summary>
        internal static bool IsBurnSection(TrackSection s)
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
                bool surface = false;
                BurnItemType type;
                if (SplitEnvironmentClass(sections[i].environment) != ExoSplitClass)
                {
                    type = BurnItemType.Floor;
                    while (j + 1 < n && SplitEnvironmentClass(sections[j + 1].environment) != ExoSplitClass) j++;
                    for (int k = i; k <= j && !surface; k++)
                        surface = SplitEnvironmentClass(sections[k].environment) == SurfaceSplitClass;
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
                    HasSurface = surface,
                });
                i = j + 1;
            }
            return items;
        }

        /// <summary>The last section of the burn starting at <paramref name="first"/>: burn
        /// sections on one body, with physics-frame engine-off gaps up to
        /// <see cref="BurnMergeGapSeconds"/> merged in.</summary>
        private static int ExtendBurn(Recording rec, int first)
            => ExtendBurnRun(rec.TrackSections, first);

        /// <summary>
        /// <see cref="ExtendBurn"/> over any section list: the one burn definition the
        /// version-3 cuts and the Mission Profile (MissionPhaseAnalyzer, over a whole vessel
        /// line's sections) share. An on-rails section or an SOI change ends the run.
        /// </summary>
        internal static int ExtendBurnRun(IList<TrackSection> sections, int first)
        {
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
                c.StartsAtFloorExit = prev != null ? IsAtmosphereOrGroundExit(rec, prev)
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
                    && IsCorrectionBurn(items[k - 2], items[k], c.LeavingTarget, c.Body, facts))
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

        /// <summary>A floor run the coast after it leaves from the atmosphere or the ground (not
        /// the approach band: orbit can be reached inside the band).</summary>
        private static bool IsAtmosphereOrGroundExit(Recording rec, BurnItem floor)
        {
            if (floor.Type != BurnItemType.Floor) return false;
            int last = SplitEnvironmentClass(rec.TrackSections[floor.Last].environment);
            return last == AtmosphericSplitClass || last == SurfaceSplitClass;
        }

        /// <summary>
        /// True when the burn between <paramref name="before"/> and <paramref name="after"/>
        /// (on <paramref name="body"/>, the leaving coast ending in <paramref name="target"/>'s
        /// SOI) only corrected the orbit. With elements on both sides: the semi-major axis moved
        /// by at most <see cref="BurnCorrectionRelSma"/>. With elements only before it: the
        /// orbit before already got there (it reaches a moon's orbit band, or it already leaves
        /// for the parent), so the burn before it is the departure. Otherwise false.
        /// </summary>
        private static bool IsCorrectionBurn(
            BurnItem before, BurnItem after, string target, string body, Dictionary<string, BodyFacts> facts)
        {
            if (!before.HasElements) return false;
            if (!after.HasElements) return OrbitAlreadyReaches(before, target, body, facts);
            double a0 = before.LastElements.semiMajorAxis;
            double a1 = after.FirstElements.semiMajorAxis;
            if (double.IsNaN(a0) || double.IsNaN(a1) || Math.Sign(a0) != Math.Sign(a1)) return false;
            return Math.Abs(a1 - a0) / Math.Max(1.0, Math.Abs(a0)) <= BurnCorrectionRelSma;
        }

        /// <summary>True when the orbit of coast <paramref name="c"/> around <paramref name="body"/>
        /// already gets to <paramref name="target"/>: for a moon, its apoapsis and periapsis span
        /// part of the band the moon's SOI sweeps; for the parent, the orbit already leaves.</summary>
        private static bool OrbitAlreadyReaches(
            BurnItem c, string target, string body, Dictionary<string, BodyFacts> facts)
        {
            if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(body)) return false;
            var bodyFacts = ResolveFacts(body, facts);
            var seg = c.LastElements;
            if (string.Equals(bodyFacts.ParentName, target, StringComparison.Ordinal))
                return ClassifyOrbitKind(seg, c.EndUT, bodyFacts) == OrbitKind.Leaving;
            var t = ResolveFacts(target, facts);
            if (!t.Known || !string.Equals(t.ParentName, body, StringComparison.Ordinal)
                || double.IsNaN(t.OrbitSma) || double.IsNaN(t.SoiRadius))
                return false;
            double a = seg.semiMajorAxis;
            double e = seg.eccentricity;
            if (double.IsNaN(a) || double.IsNaN(e)) return false;
            double ap = e < 1 && a > 0 ? a * (1 + e) : double.PositiveInfinity;
            double pe = a * (1 - e);
            double ecc = double.IsNaN(t.OrbitEcc) ? 0 : t.OrbitEcc;
            double low = t.OrbitSma * (1 - ecc) - t.SoiRadius;
            double high = t.OrbitSma * (1 + ecc) + t.SoiRadius;
            return ap >= low && pe <= high;
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
            if (prev == null)
                return context != BurnStartContext.None ? OrbitKind.Falling : OrbitKind.Unknown;
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
        /// vessel goes through the parent to a sibling, or for any arrival-side kind cut
        /// (insertion, injection, capture, capture into the atmosphere, direct descent) at a
        /// body the vessel reached from a sibling through the parent and has not touched down
        /// on since. <paramref name="items"/> is the whole chain line, so a piece cut off the
        /// rest of its line is judged as the line.
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
            bool arrivalSide = d.Kind == BurnCutKind.Insertion || d.Kind == BurnCutKind.Injection
                || d.Kind == BurnCutKind.Capture || d.Kind == BurnCutKind.CaptureToAtmosphere
                || d.Kind == BurnCutKind.DirectDescent;
            if (arrivalSide && GoesBetweenSiblings(items, i, b, parent, -1, facts)) return true;
            bool departureSide = d.Kind == BurnCutKind.Departure || d.Kind == BurnCutKind.Injection;
            return departureSide && GoesBetweenSiblings(items, i, b, parent, +1, facts);
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

        /// <summary>
        /// True when, walking from item <paramref name="i"/> in <paramref name="dir"/> (-1:
        /// where the vessel came from, +1: where it goes), it stays in space around
        /// <paramref name="body"/> (or flies by one of its moons), then around
        /// <paramref name="parent"/>, and then is at another child of the parent. A touchdown
        /// on the way (a floor run with a surface section) ends the walk: the vessel landed, so
        /// this is no transfer. An atmosphere dip or a low pass through a moon's approach band
        /// does not.
        /// </summary>
        private static bool GoesBetweenSiblings(
            List<BurnItem> items, int i, string body, string parent, int dir, Dictionary<string, BodyFacts> facts)
        {
            bool atParent = false;
            for (int k = i + dir; k >= 0 && k < items.Count; k += dir)
            {
                var item = items[k];
                string kb = item.Body;
                if (string.IsNullOrEmpty(kb)) continue;
                bool here = string.Equals(kb, body, StringComparison.Ordinal) || IsChildOf(kb, body, facts);
                if (!atParent && here)
                {
                    if (item.HasSurface) return false;
                    continue;
                }
                if (string.Equals(kb, parent, StringComparison.Ordinal))
                {
                    if (item.HasSurface) return false;
                    atParent = true;
                    continue;
                }
                return atParent && !string.Equals(kb, body, StringComparison.Ordinal) && IsChildOf(kb, parent, facts);
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
            Orbit own = hasParent ? body.orbit : null;
            facts = new BodyFacts
            {
                Known = true,
                Radius = body.Radius,
                AtmosphereDepth = body.atmosphere ? body.atmosphereDepth : 0,
                SoiRadius = hasParent ? body.sphereOfInfluence : double.PositiveInfinity,
                GravParameter = body.gravParameter,
                ParentName = hasParent ? parent.name : null,
                OrbitSma = own != null ? own.semiMajorAxis : double.NaN,
                OrbitEcc = own != null ? own.eccentricity : double.NaN,
            };
            return true;
        }

        // The stock KSP system (KSP 1.12): radius, atmosphere top, SOI, GM, parent, and the
        // body's own orbit (semi-major axis, eccentricity). Filled once
        // by the type initializer and only read after it.
        private static readonly Dictionary<string, BodyFacts> StockBodies =
            new Dictionary<string, BodyFacts>(StringComparer.Ordinal)
            {
                { "Sun", Stock(261600000, 600000, double.PositiveInfinity, 1.1723328e18, null, double.NaN, double.NaN) },
                { "Kerbol", Stock(261600000, 600000, double.PositiveInfinity, 1.1723328e18, null, double.NaN, double.NaN) },
                { "Moho", Stock(250000, 0, 9646663.0, 1.6860938e11, "Sun", 5263138304, 0.2) },
                { "Eve", Stock(700000, 90000, 85109365, 8.1717302e12, "Sun", 9832684544, 0.01) },
                { "Gilly", Stock(13000, 0, 126123.27, 8289449.8, "Eve", 31500000, 0.55) },
                { "Kerbin", Stock(600000, 70000, 84159286, 3.5316e12, "Sun", 13599840256, 0) },
                { "Mun", Stock(200000, 0, 2429559.1, 6.5138398e10, "Kerbin", 12000000, 0) },
                { "Minmus", Stock(60000, 0, 2247428.4, 1.7658e9, "Kerbin", 47000000, 0) },
                { "Duna", Stock(320000, 50000, 47921949, 3.0136321e11, "Sun", 20726155264, 0.051) },
                { "Ike", Stock(130000, 0, 1049598.9, 1.8568369e10, "Duna", 3200000, 0.03) },
                { "Dres", Stock(138000, 0, 32832840, 2.1484489e10, "Sun", 40839348203, 0.145) },
                { "Jool", Stock(6000000, 200000, 2.4559852e9, 2.82528e14, "Sun", 68773560320, 0.05) },
                { "Laythe", Stock(500000, 50000, 3723645.8, 1.962e12, "Jool", 27184000, 0) },
                { "Vall", Stock(300000, 0, 2406401.4, 2.074815e11, "Jool", 43152000, 0) },
                { "Tylo", Stock(600000, 0, 10856518, 2.82528e12, "Jool", 68500000, 0) },
                { "Bop", Stock(65000, 0, 1221060.9, 2.4868349e9, "Jool", 128500000, 0.235) },
                { "Pol", Stock(44000, 0, 1042138.9, 7.2170208e8, "Jool", 179890000, 0.171) },
                { "Eeloo", Stock(210000, 0, 1.1908294e8, 7.4410815e10, "Sun", 90118820000, 0.26) },
            };

        /// <summary>The stock KSP system's facts for <paramref name="name"/>; unknown body ->
        /// not <see cref="BodyFacts.Known"/>.</summary>
        internal static BodyFacts StockBodyFacts(string name)
        {
            if (string.IsNullOrEmpty(name)) return default(BodyFacts);
            return StockBodies.TryGetValue(name, out var f) ? f : default(BodyFacts);
        }

        private static BodyFacts Stock(
            double radius, double atmo, double soi, double mu, string parent, double orbitSma, double orbitEcc)
        {
            return new BodyFacts
            {
                Known = true,
                Radius = radius,
                AtmosphereDepth = atmo,
                SoiRadius = soi,
                GravParameter = mu,
                ParentName = parent,
                OrbitSma = orbitSma,
                OrbitEcc = orbitEcc,
            };
        }
    }
}
