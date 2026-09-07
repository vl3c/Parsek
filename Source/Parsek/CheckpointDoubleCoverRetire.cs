using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Load-time retire path for an OrbitalCheckpoint TrackSection whose span - and, when
    /// it carries one, whose conic - is already carried by other checkpoint sections of
    /// the same recording.
    ///
    /// <para>WHY THIS EXISTS SEPARATELY FROM THE PRODUCER.
    /// <see cref="OrbitSegmentCheckpointBridge"/>'s anti-double-cover guard is PREVENTIVE:
    /// it refuses to ADD a coarse envelope [X,Z] next to existing finer checkpoint sections
    /// [X,Y] + [Y,Z]. Driven over a save written before that guard landed it reports
    /// added=0 clipped=0 skippedCovered=0, so residue already on disk survives every
    /// producer re-run and the analyzer's INV2-NO-DOUBLE-COVER reds the save forever. Its
    /// empty-shell reconcile WOULD retire the payload-less half, but both read paths gate
    /// that off (<c>reconcileEmptySections: false</c> in
    /// <c>TrajectorySidecarBinary.Read</c> and
    /// <c>TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom</c>), so at load nothing
    /// retires anything. This class is the missing retire path for the whole shape.</para>
    ///
    /// <para>THE PREDICATE IS COVERAGE, NEVER TRUNCATION. A section is retired only when
    /// the sections that stay behind already carry (a) its whole UT span and (b) the
    /// identical conic over that whole span. Both halves are checked; a partial overlap,
    /// an uncovered remainder, or a differing conic all mean "keep". So the recording's
    /// coverage union cannot move and no orbital payload is lost - the retired section is
    /// a duplicate description of a span the survivors already describe the same way, or
    /// a shell that described nothing.</para>
    ///
    /// <para>WHICH SIDE SURVIVES depends on which sections carry payload, and on the
    /// MEASURED residue it is the ENVELOPE. The shape on disk in the three
    /// <c>orbital supply route</c> recordings is [T0,T1] payload-LESS shell + [T0,T2]
    /// envelope + [T1,T2] re-clip. A shell is a candidate but never a COVERER, so the
    /// envelope's span is not covered by {shell, re-clip} and it stays, while both the
    /// re-clip (span and conic inside the envelope) and the shell (span inside the
    /// envelope, no conic to lose) go. Where a genuine payload-bearing TILING exists -
    /// [T0,T1] and [T1,T2] both carrying the conic - the widest-first order retires the
    /// envelope and keeps the tiling instead. Either way the coverage union is still and
    /// no boundary that describes anything is lost.</para>
    ///
    /// <para>WHAT IT DOES NOT CLEAR. The three measured recordings each keep one or two
    /// INV2 findings after the pass, and they are a DIFFERENT population: a frame-LESS
    /// <c>Absolute</c> section exactly duplicating the span of the checkpoint section
    /// beside it. Absolute sections are never candidates and never coverers here, by
    /// design, so those stand and are filed separately
    /// (todo EMPTY-ABSOLUTE-SECTION-EXACT-SPAN-DUPLICATE-OF-ITS-CHECKPOINT). Nor is a
    /// PARTIAL overlap repairable in either direction; it is left alone and keeps
    /// reporting. This pass does not make a residue save analyze RED=0.</para>
    ///
    /// <para>IN MEMORY ONLY. The applier never calls <see cref="Recording.MarkFilesDirty"/>:
    /// dirtying at load makes the next FlushDirtyFiles advance
    /// <see cref="Recording.SidecarEpoch"/>, which is a route's captured proof-of-source
    /// field, parking every route over the recording in
    /// <c>SourceChanged/sidecar-epoch-drift</c> permanently. That is the rule PR #1637
    /// established for the load-time flat-list heal, and this pass follows it. The retire
    /// is idempotent and linear, so re-deriving it on every load costs nothing; the next
    /// legitimate sidecar write persists it.</para>
    /// </summary>
    internal static class CheckpointDoubleCoverRetire
    {
        // Same UT / scalar / distance / vector tolerances the checkpoint bridge dedups
        // with. Deliberately NOT an orbit-mechanics equivalence threshold: these compare
        // values produced by the SAME segment after an "R" round trip, and a re-clip
        // copies every element verbatim (OrbitSegmentCheckpointBridge.TryTrimOrbitSegmentToRange
        // moves startUT/endUT and nothing else).
        //
        // WHAT IT BUYS AND WHAT IT COSTS, at the boundary. It is a round-trip slop
        // allowance, so it is the width of the gap the coverage walk will BRIDGE: a hole
        // of 5e-7 s between two survivors reads as covered and the candidate is retired; a
        // hole of 2e-6 s does not and the candidate stays. Both directions are pinned
        // (UtToleranceBoundary_* cells). At UT ~ 7.2e7 (the measured 36c7688b recording) a
        // double's ULP is ~1.5e-8 s, so 1e-6 is ~70 ULP - wide enough for a decimal round
        // trip, and roughly seven orders of magnitude below the smallest real coverage gap
        // the analyzer has ever reported (hundreds of seconds; see
        // Inv2NoDoubleCover.UncoveredSpanToleranceSeconds = 8.0). It is also the minimum
        // span a section must have to be a candidate at all.
        private const double UtTolerance = 1e-6;

        /// <summary>
        /// Pure. Indices of the OrbitalCheckpoint sections that are redundant under the
        /// coverage predicate above, ascending. Empty when nothing is retirable.
        /// </summary>
        /// <remarks>
        /// Deterministic and order-preserving: the returned indices address
        /// <paramref name="sections"/> as given, and the widest-first / highest-index-first
        /// test order makes the survivor of an exact-span duplicate pair the LOWER index.
        ///
        /// <para>A clean recording costs ONE linear walk: the pre-scan returns as soon as it
        /// has established that no section starts before the running covered end, which is
        /// the necessary condition for any section to be covered by others.</para>
        /// </remarks>
        internal static List<int> FindRedundantCheckpointSections(
            IReadOnlyList<TrackSection> sections)
        {
            var drop = new List<int>();
            if (sections == null || sections.Count < 2)
                return drop;

            if (!HasInteriorOverlap(sections))
                return drop;

            var candidates = new List<int>();
            for (int i = 0; i < sections.Count; i++)
            {
                if (IsRetireCandidateSection(sections[i]))
                    candidates.Add(i);
            }
            if (candidates.Count < 2)
                return drop;

            // Widest span first so an envelope is tested before the tiles that cover it;
            // highest index first among equal spans so an exact-span duplicate pair keeps
            // its first occurrence. The order is a preference, not a guarantee: a section
            // is only ever dropped when the ones STILL kept cover it, so an envelope over
            // a shell + a re-clip survives its own turn (a shell covers nothing) and the
            // two narrower sections go instead.
            candidates.Sort((a, b) =>
            {
                double spanA = sections[a].endUT - sections[a].startUT;
                double spanB = sections[b].endUT - sections[b].startUT;
                int c = spanB.CompareTo(spanA);
                return c != 0 ? c : b.CompareTo(a);
            });

            var kept = new HashSet<int>(candidates);
            for (int k = 0; k < candidates.Count; k++)
            {
                int index = candidates[k];
                kept.Remove(index);
                if (SpanCoveredByKept(sections, index, kept)
                    && ConicsCoveredByKept(sections, index, kept))
                {
                    drop.Add(index);
                }
                else
                {
                    kept.Add(index);
                }
            }

            drop.Sort();
            return drop;
        }

        /// <summary>
        /// Applies <see cref="FindRedundantCheckpointSections"/> to a live recording, in
        /// memory. Returns the number of sections retired (0 when there was nothing to do
        /// or when a postcondition refused the pass).
        /// </summary>
        /// <remarks>
        /// TWO POSTCONDITIONS, checked mechanically rather than argued:
        /// <list type="number">
        /// <item>the recording's COVERAGE UNION is byte-identical before and after (the
        /// predicate guarantees it; this catches a future predicate change that does
        /// not);</item>
        /// <item>the set of UTs at which
        /// <see cref="RecordingOptimizer.IsSplittableEnvOrBodyBoundary"/> calls a boundary
        /// splittable is identical before and after. Retiring a section changes which
        /// sections are ADJACENT, and the optimizer's graze walks accumulate duration over
        /// neighbouring same-class runs, so "the union is unchanged" does not by itself
        /// mean "the split decisions are unchanged". Checking it directly does. The check
        /// is paid only when there IS residue.</item>
        /// </list>
        /// Either failing reverts the section list untouched and logs a Warn.
        /// </remarks>
        internal static int TryRetireRedundantCheckpointSections(Recording rec)
        {
            if (rec == null || rec.TrackSections == null || rec.TrackSections.Count < 2)
                return 0;

            List<int> drop = FindRedundantCheckpointSections(rec.TrackSections);
            if (drop.Count == 0)
                return 0;

            List<TrackSection> original = rec.TrackSections;
            List<(double Start, double End)> unionBefore = CoverageUnion(original);
            List<double> splitBefore = SplittableBoundaryUTs(rec);

            var dropSet = new HashSet<int>(drop);
            var retained = new List<TrackSection>(original.Count - drop.Count);
            for (int i = 0; i < original.Count; i++)
            {
                if (!dropSet.Contains(i))
                    retained.Add(original[i]);
            }

            rec.TrackSections = retained;
            List<(double Start, double End)> unionAfter = CoverageUnion(retained);
            List<double> splitAfter = SplittableBoundaryUTs(rec);

            if (!UnionsEqual(unionBefore, unionAfter) || !UtListsEqual(splitBefore, splitAfter))
            {
                rec.TrackSections = original;
                if (!RecordingStore.SuppressLogging)
                {
                    ParsekLog.Warn("RecordingStore",
                        "Checkpoint double-cover retire refused: rec="
                        + (rec.RecordingId ?? "?")
                        + " candidates=" + drop.Count.ToString(CultureInfo.InvariantCulture)
                        + " coverageMoved=" + (UnionsEqual(unionBefore, unionAfter) ? "0" : "1")
                        + " splitDecisionsMoved="
                        + (UtListsEqual(splitBefore, splitAfter) ? "0" : "1"));
                }
                return 0;
            }

            // The section list changed, so the derived caches keyed on it are stale even
            // though the FILE is not. MarkFilesDirty would do this as a side effect; this
            // pass needs the invalidation WITHOUT the dirty flag (see the class remarks).
            rec.CachedStats = null;
            rec.CachedStatsPointCount = 0;
            rec.InvalidateSegmentBodyDisplayLabelCache();

            if (!RecordingStore.SuppressLogging)
            {
                ParsekLog.Info("RecordingStore",
                    "Checkpoint double-cover retired: rec=" + (rec.RecordingId ?? "?")
                    + " dropped=" + drop.Count.ToString(CultureInfo.InvariantCulture)
                    + " kept=" + retained.Count.ToString(CultureInfo.InvariantCulture)
                    + " spans=" + FormatSpans(original, drop));
            }

            return drop.Count;
        }

        /// <summary>
        /// Runs the retire over a list of recordings (the committed store at load time).
        /// Returns the number of sections retired in total; logs ONE summary line per
        /// affected recording and nothing at all when the whole list is clean.
        /// </summary>
        internal static int RetireAcrossRecordings(IReadOnlyList<Recording> recordings)
        {
            if (recordings == null || recordings.Count == 0)
                return 0;

            int retiredSections = 0;
            int affectedRecordings = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                int retired = TryRetireRedundantCheckpointSections(recordings[i]);
                if (retired <= 0)
                    continue;
                retiredSections += retired;
                affectedRecordings++;
            }

            if (retiredSections > 0 && !RecordingStore.SuppressLogging)
            {
                ParsekLog.Info("RecordingStore",
                    "Checkpoint double-cover retire pass: recordings="
                    + recordings.Count.ToString(CultureInfo.InvariantCulture)
                    + " affected=" + affectedRecordings.ToString(CultureInfo.InvariantCulture)
                    + " droppedSections=" + retiredSections.ToString(CultureInfo.InvariantCulture));
            }

            return retiredSections;
        }

        // --- predicate helpers ----------------------------------------------------

        /// <summary>
        /// A retire CANDIDATE: an OrbitalCheckpoint section with no per-frame payload,
        /// over a non-degenerate finite span, that its producer has not flagged as a
        /// bookkeeping seam. Both populations qualify - one carrying a conic, and a
        /// payload-LESS shell carrying nothing at all.
        ///
        /// <para>Absolute / Relative sections are excluded by the reference frame, and an
        /// OrbitalCheckpoint section that somehow carries frames or bodyFixedFrames is
        /// excluded too - those are the recorded surfaces, never a duplicate
        /// description.</para>
        ///
        /// <para>WHY THE SHELL IS A CANDIDATE HERE RATHER THAN THE PRODUCER'S PROBLEM.
        /// <see cref="OrbitSegmentCheckpointBridge.ReconcileEmptySectionsAgainstPayloadCoverage"/>
        /// is the empty-shell owner on the WRITE path, but both READ paths gate it off:
        /// <c>TrajectorySidecarBinary.Read</c> and
        /// <c>TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom</c> each call
        /// <c>EnsureCheckpointSectionsForTopLevelOrbitSegments(..., reconcileEmptySections:
        /// false)</c>, under the normalize-on-rewrite contract that a read must leave the
        /// file byte-identical. So at load nothing retires a shell, and on the measured
        /// residue the shell is HALF the reported overlap: the triple on disk is
        /// <c>[T0,T1]</c> shell + <c>[T0,T2]</c> envelope + <c>[T1,T2]</c> re-clip, and
        /// dropping only the re-clip leaves INV2 reporting shell-vs-envelope. Admitting
        /// the shell is what makes the pass finish the job it starts.</para>
        /// </summary>
        private static bool IsRetireCandidateSection(TrackSection section)
        {
            return section.referenceFrame == ReferenceFrame.OrbitalCheckpoint
                && !section.isBoundarySeam
                && (section.frames == null || section.frames.Count == 0)
                && (section.bodyFixedFrames == null || section.bodyFixedFrames.Count == 0)
                && IsFinite(section.startUT)
                && IsFinite(section.endUT)
                && section.endUT > section.startUT + UtTolerance;
        }

        /// <summary>
        /// A COVERER: a candidate that actually carries a conic. A payload-less shell
        /// describes nothing, so it can never stand in for a section that is dropped -
        /// letting one cover would let two mutually-covering shells retire a real span's
        /// only description, and would let a shell "cover" an envelope's conic.
        ///
        /// <para>Consequence, deliberately: a shell covered ONLY by other shells is not
        /// retirable and INV2 keeps reporting that pair. Nothing in the measured residue
        /// is that shape.</para>
        /// </summary>
        private static bool IsCoveringCheckpointSection(TrackSection section)
        {
            return IsRetireCandidateSection(section)
                && section.checkpoints != null
                && section.checkpoints.Count > 0;
        }

        /// <summary>
        /// True when some section starts strictly before the running covered end - the
        /// necessary condition for any section to be covered by others. One linear walk on
        /// a list that is already sorted by startUT (which
        /// <see cref="OrbitSegmentCheckpointBridge"/> keeps it), one sort-and-walk when it
        /// is not.
        /// </summary>
        private static bool HasInteriorOverlap(IReadOnlyList<TrackSection> sections)
        {
            bool sorted = true;
            double coverEnd = sections[0].endUT;
            double prevStart = sections[0].startUT;
            for (int i = 1; i < sections.Count; i++)
            {
                TrackSection s = sections[i];
                if (s.startUT < prevStart)
                {
                    sorted = false;
                    break;
                }
                prevStart = s.startUT;
                if (s.startUT < coverEnd - UtTolerance)
                    return true;
                if (s.endUT > coverEnd)
                    coverEnd = s.endUT;
            }
            if (sorted)
                return false;

            var spans = new List<(double Start, double End)>(sections.Count);
            for (int i = 0; i < sections.Count; i++)
                spans.Add((sections[i].startUT, sections[i].endUT));
            spans.Sort((a, b) =>
            {
                int c = a.Start.CompareTo(b.Start);
                return c != 0 ? c : a.End.CompareTo(b.End);
            });
            coverEnd = spans[0].End;
            for (int i = 1; i < spans.Count; i++)
            {
                if (spans[i].Start < coverEnd - UtTolerance)
                    return true;
                if (spans[i].End > coverEnd)
                    coverEnd = spans[i].End;
            }
            return false;
        }

        /// <summary>
        /// True when [section.startUT, section.endUT] lies wholly inside the merged spans
        /// of the still-kept PAYLOAD-BEARING candidates, with no gap. Shells do not count
        /// (see <see cref="IsCoveringCheckpointSection"/>).
        /// </summary>
        private static bool SpanCoveredByKept(
            IReadOnlyList<TrackSection> sections, int index, HashSet<int> kept)
        {
            var spans = new List<(double Start, double End)>();
            foreach (int k in kept)
            {
                if (!IsCoveringCheckpointSection(sections[k]))
                    continue;
                spans.Add((sections[k].startUT, sections[k].endUT));
            }
            return RangeCovered(spans, sections[index].startUT, sections[index].endUT);
        }

        /// <summary>
        /// True when every conic the section carries is carried IDENTICALLY, over its whole
        /// span, by the still-kept candidates.
        ///
        /// <para>This is what makes the drop lossless rather than merely span-preserving:
        /// a re-clip differs from its parent only in startUT/endUT (the bridge's trim
        /// copies every element verbatim), so "same conic ignoring the span" is an exact
        /// test, not an orbit-mechanics approximation.</para>
        ///
        /// <para>A payload-LESS shell carries no conic, so this half is vacuously true for
        /// it and the span half alone decides. That is correct rather than a loophole: a
        /// shell describes nothing, so there is nothing to lose.</para>
        /// </summary>
        private static bool ConicsCoveredByKept(
            IReadOnlyList<TrackSection> sections, int index, HashSet<int> kept)
        {
            List<OrbitSegment> segments = sections[index].checkpoints;
            if (segments == null)
                return true;
            for (int i = 0; i < segments.Count; i++)
            {
                OrbitSegment segment = segments[i];
                if (!IsFinite(segment.startUT) || !IsFinite(segment.endUT)
                    || segment.endUT <= segment.startUT + UtTolerance)
                {
                    // A degenerate conic cannot be shown covered; keep the section.
                    return false;
                }

                var spans = new List<(double Start, double End)>();
                foreach (int k in kept)
                {
                    if (!IsCoveringCheckpointSection(sections[k]))
                        continue;
                    List<OrbitSegment> others = sections[k].checkpoints;
                    if (others == null)
                        continue;
                    for (int j = 0; j < others.Count; j++)
                    {
                        if (OrbitSegmentCheckpointBridge.OrbitSegmentConicNearlyEqualsIgnoringSpan(
                                segment, others[j]))
                        {
                            spans.Add((others[j].startUT, others[j].endUT));
                        }
                    }
                }

                if (!RangeCovered(spans, segment.startUT, segment.endUT))
                    return false;
            }
            return true;
        }

        /// <summary>Gap-free coverage of [startUT,endUT] by the merged spans.</summary>
        private static bool RangeCovered(
            List<(double Start, double End)> spans, double startUT, double endUT)
        {
            if (spans == null || spans.Count == 0)
                return false;
            spans.Sort((a, b) =>
            {
                int c = a.Start.CompareTo(b.Start);
                return c != 0 ? c : a.End.CompareTo(b.End);
            });

            double reached = startUT;
            for (int i = 0; i < spans.Count; i++)
            {
                if (spans[i].Start > reached + UtTolerance)
                    break;
                if (spans[i].End > reached)
                    reached = spans[i].End;
                if (reached >= endUT - UtTolerance)
                    return true;
            }
            return reached >= endUT - UtTolerance;
        }

        // --- postcondition helpers ------------------------------------------------

        /// <summary>Merged, sorted [start,end] coverage of a section list.</summary>
        internal static List<(double Start, double End)> CoverageUnion(
            IReadOnlyList<TrackSection> sections)
        {
            var merged = new List<(double Start, double End)>();
            if (sections == null || sections.Count == 0)
                return merged;

            var spans = new List<(double Start, double End)>(sections.Count);
            for (int i = 0; i < sections.Count; i++)
            {
                if (sections[i].endUT >= sections[i].startUT)
                    spans.Add((sections[i].startUT, sections[i].endUT));
            }
            if (spans.Count == 0)
                return merged;

            spans.Sort((a, b) =>
            {
                int c = a.Start.CompareTo(b.Start);
                return c != 0 ? c : a.End.CompareTo(b.End);
            });

            double curStart = spans[0].Start;
            double curEnd = spans[0].End;
            for (int i = 1; i < spans.Count; i++)
            {
                if (spans[i].Start <= curEnd)
                {
                    if (spans[i].End > curEnd)
                        curEnd = spans[i].End;
                }
                else
                {
                    merged.Add((curStart, curEnd));
                    curStart = spans[i].Start;
                    curEnd = spans[i].End;
                }
            }
            merged.Add((curStart, curEnd));
            return merged;
        }

        /// <summary>
        /// The UTs at which the optimizer's §3 predicate calls a boundary splittable,
        /// ascending. The comparison surface for postcondition (2).
        /// </summary>
        internal static List<double> SplittableBoundaryUTs(Recording rec)
        {
            var uts = new List<double>();
            if (rec == null || rec.TrackSections == null)
                return uts;
            for (int s = 1; s < rec.TrackSections.Count; s++)
            {
                RecordingOptimizer.SplitBoundaryReason reason;
                if (RecordingOptimizer.IsSplittableEnvOrBodyBoundary(rec, s, out reason))
                    uts.Add(rec.TrackSections[s].startUT);
            }
            uts.Sort();
            return uts;
        }

        private static bool UnionsEqual(
            List<(double Start, double End)> a, List<(double Start, double End)> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!a[i].Start.Equals(b[i].Start) || !a[i].End.Equals(b[i].End))
                    return false;
            }
            return true;
        }

        private static bool UtListsEqual(List<double> a, List<double> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!a[i].Equals(b[i]))
                    return false;
            }
            return true;
        }

        private static string FormatSpans(List<TrackSection> sections, List<int> drop)
        {
            var ic = CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder("[");
            for (int i = 0; i < drop.Count; i++)
            {
                if (i > 0)
                    sb.Append(' ');
                TrackSection s = sections[drop[i]];
                sb.Append(drop[i].ToString(ic))
                  .Append(":[").Append(s.startUT.ToString("R", ic))
                  .Append(',').Append(s.endUT.ToString("R", ic)).Append(']');
            }
            return sb.Append(']').ToString();
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
