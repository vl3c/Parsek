using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Rendering
{
    /// <summary>
    /// Phase 6 commit-time anchor-candidate builder (design doc §17.3.1, §18
    /// Phase 6, §7.2 — §7.10). Pure function over a single
    /// <see cref="Recording"/>: walks <see cref="Recording.TrackSections"/>
    /// and the recording's view of its <see cref="RecordingTree.BranchPoints"/>,
    /// emitting <see cref="AnchorCandidate"/> entries per
    /// (recordingId, sectionIndex). The output is what the
    /// <c>.pann AnchorCandidatesList</c> block persists; session-time ε
    /// resolution lives in <see cref="AnchorPropagator"/>.
    ///
    /// <para>
    /// HR-3: the scan is deterministic and idempotent — two calls on the
    /// same recording produce byte-identical output (modulo dictionary
    /// iteration order, which is normalized by the caller via UT sorting).
    /// HR-1: the recording is read-only; nothing is mutated.
    /// HR-15: no live KSP state is read here — pure function.
    /// </para>
    /// </summary>
    internal static class AnchorCandidateBuilder
    {
        /// <summary>
        /// Phase 6 settings-flag gate. Mirrors the
        /// <see cref="ParsekSettings.useAnchorTaxonomy"/> property; set by
        /// callers that need to early-out without standing up the full
        /// KSP <c>HighLogic.CurrentGame</c>. Production code reads
        /// <see cref="ParsekSettings.Current"/> directly via
        /// <see cref="ResolveUseAnchorTaxonomy"/>.
        /// </summary>
        internal static bool? UseAnchorTaxonomyOverrideForTesting;

        // Dedup the "skipping, flag off" Verbose so per-recording bursts
        // don't spam the log. Cleared by ResetForTesting.
        private static readonly object s_flagOffLogLock = new object();
        private static bool s_flagOffLogged;

        // Reviewer Nit removed: TreeLookupOverrideForTesting was an
        // unused public surface left over from an earlier sketch.
        // BuildAndStorePerSection takes the RecordingTree explicitly,
        // so xUnit injects the tree at the call site rather than via a
        // static seam.

        /// <summary>
        /// Pure helper: scans <paramref name="rec"/>'s sections + branch
        /// points + orbit segments and returns per-section candidate arrays.
        /// Each list entry maps a section index to its UT-sorted, priority-
        /// resolved candidate array. Sections with no candidates are NOT
        /// included in the result (caller can distinguish "no candidates"
        /// from "section not in result" by absence).
        /// </summary>
        internal static List<KeyValuePair<int, AnchorCandidate[]>> Compute(
            Recording rec, RecordingTree tree)
        {
            var perSection = new Dictionary<int, List<AnchorCandidate>>();
            if (rec == null || rec.TrackSections == null || rec.TrackSections.Count == 0)
                return new List<KeyValuePair<int, AnchorCandidate[]>>();

            // Per-source emit. Each helper is an independent O(N) pass; the
            // overall scan is O(sections + branchPoints + orbitSegments).
            EmitDockMergeCandidates(rec, tree, perSection);
            EmitSplitCandidates(rec, tree, perSection);
            EmitRelativeBoundaryCandidates(rec, perSection);
            EmitOrbitalCheckpointAndSoiCandidates(rec, perSection);
            EmitSurfaceContinuousMarkers(rec, perSection);
            EmitLoopMarkers(rec, perSection);

            // Collapse duplicates per (sectionIndex, UT, side) by §7.11
            // priority and freeze each section's candidate list as a
            // UT-sorted, then enum-stable array.
            var output = new List<KeyValuePair<int, AnchorCandidate[]>>(perSection.Count);
            foreach (var kvp in perSection)
            {
                int sectionIdx = kvp.Key;
                List<AnchorCandidate> raw = kvp.Value;
                AnchorCandidate[] resolved = SelectWinners(raw);
                if (resolved.Length == 0) continue;
                output.Add(new KeyValuePair<int, AnchorCandidate[]>(sectionIdx, resolved));
            }

            // Stable order by sectionIndex so the .pann byte layout is
            // deterministic across runs (HR-3).
            output.Sort((a, b) => a.Key.CompareTo(b.Key));
            return output;
        }

        /// <summary>
        /// Commit/load-time entry point. Walks the recording, emits
        /// candidates, and stores the result in
        /// <see cref="SectionAnnotationStore"/>. Mirrors
        /// <see cref="SmoothingPipeline.FitAndStorePerSection"/> in shape:
        /// clear-then-populate (HR-10), Verbose batch summary at the end.
        /// No-op when the <see cref="ParsekSettings.useAnchorTaxonomy"/>
        /// flag is off.
        /// </summary>
        internal static void BuildAndStorePerSection(Recording rec, RecordingTree tree)
        {
            if (rec == null) return;
            if (!ResolveUseAnchorTaxonomy())
            {
                lock (s_flagOffLogLock)
                {
                    if (!s_flagOffLogged)
                    {
                        s_flagOffLogged = true;
                        ParsekLog.Verbose("Pipeline-Anchor",
                            "useAnchorTaxonomy=false, skipping AnchorCandidateBuilder");
                    }
                }
                return;
            }

            string recordingId = rec.RecordingId;

            // Compute first, then publish. We deliberately do not clear
            // existing candidates before Compute returns — RemoveRecording
            // removes BOTH splines and candidates, and the spline pipeline
            // owns the clear semantics. Phase 6 just overwrites per
            // (recordingId, sectionIndex) inside the established lock.
            var perSection = Compute(rec, tree);

            int totalCandidates = 0;
            int dockCount = 0, splitCount = 0, relCount = 0, orbitCount = 0,
                soiCount = 0, surfaceCount = 0, loopCount = 0, otherCount = 0;

            // Reviewer P2-1: BranchPointType differentiation for the
            // DockOrMerge byte-aliased split sources (Undock / EVA /
            // JointBreak). Build an optional UT -> bpType lookup so the
            // per-candidate log can carry the originating bp type even
            // though the AnchorSource byte is shared. Bounded by the
            // BranchPoint count (small handfuls per recording).
            Dictionary<double, BranchPointType> bpTypeByUT = null;
            if (tree != null && tree.BranchPoints != null)
            {
                bpTypeByUT = new Dictionary<double, BranchPointType>(tree.BranchPoints.Count);
                for (int i = 0; i < tree.BranchPoints.Count; i++)
                {
                    BranchPoint bp = tree.BranchPoints[i];
                    if (bp == null) continue;
                    bpTypeByUT[bp.UT] = bp.Type;
                }
            }

            for (int i = 0; i < perSection.Count; i++)
            {
                int sectionIdx = perSection[i].Key;
                AnchorCandidate[] arr = perSection[i].Value;
                SectionAnnotationStore.PutAnchorCandidates(recordingId, sectionIdx, arr);
                totalCandidates += arr.Length;
                for (int k = 0; k < arr.Length; k++)
                {
                    AnchorCandidate cand = arr[k];
                    // Reviewer P2-4: per-candidate Verbose at commit time
                    // (design doc §19.2 Stage 3 row 1). Bounded by the per-
                    // recording candidate count (a few per section, a few
                    // sections per recording — well under the ~20 batch-log
                    // ceiling). Includes the originating BranchPointType
                    // so DockOrMerge byte aliasing of split causes (P2-1)
                    // surfaces in telemetry without bumping AnchorSource.
                    string bpTypeLabel = "<n/a>";
                    if (bpTypeByUT != null && bpTypeByUT.TryGetValue(cand.UT, out BranchPointType bpType))
                        bpTypeLabel = bpType.ToString();
                    ParsekLog.Verbose("Pipeline-Anchor", string.Format(CultureInfo.InvariantCulture,
                        "Anchor candidate computed: recordingId={0} sectionIndex={1} candidateUT={2} candidateType={3} side={4} bpType={5}",
                        recordingId, sectionIdx,
                        cand.UT.ToString("R", CultureInfo.InvariantCulture),
                        cand.Source, cand.Side, bpTypeLabel));
                    switch (cand.Source)
                    {
                        case AnchorSource.DockOrMerge:
                            // P2-1: count Dock/Board separately from
                            // Undock/EVA/JointBreak via the bp type lookup.
                            // When the candidate has no matching BranchPoint
                            // (shouldn't happen for DockOrMerge byte; defensive)
                            // we count it under "split" so totals balance.
                            if (bpTypeByUT != null && bpTypeByUT.TryGetValue(cand.UT, out BranchPointType resolvedBp)
                                && (resolvedBp == BranchPointType.Dock || resolvedBp == BranchPointType.Board))
                            {
                                dockCount++;
                            }
                            else
                            {
                                splitCount++;
                            }
                            break;
                        case AnchorSource.RelativeBoundary:  relCount++;     break;
                        case AnchorSource.OrbitalCheckpoint: orbitCount++;   break;
                        case AnchorSource.SoiTransition:     soiCount++;     break;
                        case AnchorSource.SurfaceContinuous: surfaceCount++; break;
                        case AnchorSource.Loop:              loopCount++;    break;
                        default:
                            // Reviewer Nit: real per-source counter rather
                            // than total-minus-everything-else subtraction
                            // so a future enum value silently rolls into
                            // "other" instead of being misattributed.
                            otherCount++;
                            break;
                    }
                }
            }

            ParsekLog.Verbose("Pipeline-Anchor", string.Format(CultureInfo.InvariantCulture,
                "AnchorCandidateBuilder summary: recordingId={0} sections={1} candidatesEmittedTotal={2} " +
                "perSourceCounts=[Dock/Merge={3} Split(EVA/Undock/JointBreak)={4} RelativeBoundary={5} OrbitalCheckpoint={6} " +
                "SoiTransition={7} SurfaceContinuous={8} Loop={9} other={10}]",
                recordingId,
                rec.TrackSections != null ? rec.TrackSections.Count : 0,
                totalCandidates,
                dockCount, splitCount, relCount, orbitCount, soiCount, surfaceCount, loopCount, otherCount));
        }

        /// <summary>
        /// Resolve the live <see cref="ParsekSettings.useAnchorTaxonomy"/>
        /// value, with a test override hook. Default true (Phase 6 ships
        /// with the flag on).
        /// </summary>
        internal static bool ResolveUseAnchorTaxonomy()
        {
            if (UseAnchorTaxonomyOverrideForTesting.HasValue)
                return UseAnchorTaxonomyOverrideForTesting.Value;
            try
            {
                ParsekSettings cur = ParsekSettings.Current;
                if (cur != null) return cur.useAnchorTaxonomy;
            }
            catch
            {
                // ParsekSettings.Current can throw under xUnit (HighLogic
                // unavailable). Treat as "use the compiled default" which
                // for Phase 6 is true.
            }
            return true;
        }

        /// <summary>Test-only: resets dedup + override hooks.</summary>
        internal static void ResetForTesting()
        {
            UseAnchorTaxonomyOverrideForTesting = null;
            lock (s_flagOffLogLock) { s_flagOffLogged = false; }
        }

        // -------------------------------------------------------------------
        //  Per-source emitters
        // -------------------------------------------------------------------

        // §7.2 Dock / Board (merge events).
        private static void EmitDockMergeCandidates(
            Recording rec, RecordingTree tree, Dictionary<int, List<AnchorCandidate>> output)
        {
            if (tree == null || tree.BranchPoints == null) return;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                BranchPoint bp = tree.BranchPoints[i];
                if (bp == null) continue;
                if (bp.Type != BranchPointType.Dock && bp.Type != BranchPointType.Board) continue;

                bool asParent = bp.ParentRecordingIds != null
                    && bp.ParentRecordingIds.Contains(rec.RecordingId);
                bool asChild = bp.ChildRecordingIds != null
                    && bp.ChildRecordingIds.Contains(rec.RecordingId);
                if (!asParent && !asChild) continue;

                int sectionIdx = TrajectoryMath.FindTrackSectionForUT(rec.TrackSections, bp.UT);
                if (sectionIdx < 0) continue;

                if (asParent)
                    AddCandidate(output, sectionIdx, new AnchorCandidate(bp.UT, AnchorSource.DockOrMerge, AnchorSide.End));
                if (asChild)
                    AddCandidate(output, sectionIdx, new AnchorCandidate(bp.UT, AnchorSource.DockOrMerge, AnchorSide.Start));
            }
        }

        // §7.3 Undock / EVA / JointBreak (split events). Maps to the
        // DockOrMerge byte today — see class docstring for the rationale and
        // the Phase 6 risk #1 entry in the plan.
        private static void EmitSplitCandidates(
            Recording rec, RecordingTree tree, Dictionary<int, List<AnchorCandidate>> output)
        {
            if (tree == null || tree.BranchPoints == null) return;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                BranchPoint bp = tree.BranchPoints[i];
                if (bp == null) continue;
                if (bp.Type != BranchPointType.Undock
                    && bp.Type != BranchPointType.EVA
                    && bp.Type != BranchPointType.JointBreak)
                {
                    continue;
                }

                bool asParent = bp.ParentRecordingIds != null
                    && bp.ParentRecordingIds.Contains(rec.RecordingId);
                bool asChild = bp.ChildRecordingIds != null
                    && bp.ChildRecordingIds.Contains(rec.RecordingId);
                if (!asParent && !asChild) continue;

                int sectionIdx = TrajectoryMath.FindTrackSectionForUT(rec.TrackSections, bp.UT);
                if (sectionIdx < 0) continue;

                if (asParent)
                    AddCandidate(output, sectionIdx, new AnchorCandidate(bp.UT, AnchorSource.DockOrMerge, AnchorSide.End));
                if (asChild)
                    AddCandidate(output, sectionIdx, new AnchorCandidate(bp.UT, AnchorSource.DockOrMerge, AnchorSide.Start));
            }
        }

        // §7.4 Adjacent ABSOLUTE <-> RELATIVE boundaries. Candidate lands on
        // the ABSOLUTE side (the RELATIVE side is exact via the resolver).
        private static void EmitRelativeBoundaryCandidates(
            Recording rec, Dictionary<int, List<AnchorCandidate>> output)
        {
            if (rec.TrackSections == null) return;
            for (int i = 0; i < rec.TrackSections.Count - 1; i++)
            {
                TrackSection a = rec.TrackSections[i];
                TrackSection b = rec.TrackSections[i + 1];
                bool aRel = a.referenceFrame == ReferenceFrame.Relative;
                bool bRel = b.referenceFrame == ReferenceFrame.Relative;
                bool aAbs = a.referenceFrame == ReferenceFrame.Absolute;
                bool bAbs = b.referenceFrame == ReferenceFrame.Absolute;
                if (!((aAbs && bRel) || (aRel && bAbs))) continue;

                // Boundary UT is the shared seam. Use the start of the later
                // section as the canonical boundary value.
                double boundaryUT = b.startUT;

                if (aAbs && bRel)
                {
                    // ABSOLUTE -> RELATIVE: candidate on the ABSOLUTE side's End.
                    AddCandidate(output, i,
                        new AnchorCandidate(boundaryUT, AnchorSource.RelativeBoundary, AnchorSide.End));
                }
                else // aRel && bAbs
                {
                    // RELATIVE -> ABSOLUTE: candidate on the ABSOLUTE side's Start.
                    AddCandidate(output, i + 1,
                        new AnchorCandidate(boundaryUT, AnchorSource.RelativeBoundary, AnchorSide.Start));
                }
            }
        }

        // §7.5 / §7.6 OrbitalCheckpoint and SOI transition boundaries.
        private static void EmitOrbitalCheckpointAndSoiCandidates(
            Recording rec, Dictionary<int, List<AnchorCandidate>> output)
        {
            if (rec.TrackSections == null) return;
            for (int i = 0; i < rec.TrackSections.Count - 1; i++)
            {
                TrackSection a = rec.TrackSections[i];
                TrackSection b = rec.TrackSections[i + 1];
                bool aCk = a.referenceFrame == ReferenceFrame.OrbitalCheckpoint;
                bool bCk = b.referenceFrame == ReferenceFrame.OrbitalCheckpoint;
                bool aAbs = a.referenceFrame == ReferenceFrame.Absolute;
                bool bAbs = b.referenceFrame == ReferenceFrame.Absolute;
                if (!((aAbs && bCk) || (aCk && bAbs))) continue;

                double boundaryUT = b.startUT;
                bool soi = TryDetectSoiAtCheckpointBoundary(a, b);
                AnchorSource src = soi ? AnchorSource.SoiTransition : AnchorSource.OrbitalCheckpoint;

                if (aAbs && bCk)
                {
                    // ABSOLUTE -> Checkpoint: candidate on ABSOLUTE side's End.
                    AddCandidate(output, i,
                        new AnchorCandidate(boundaryUT, src, AnchorSide.End));
                    if (soi)
                    {
                        // §7.6: SOI candidates land on both sides of the
                        // boundary. Add the post-side too (the checkpoint
                        // section is technically analytical, but we use the
                        // first sample of the upcoming ABSOLUTE-or-checkpoint
                        // section to seed the post-SOI ε; for the
                        // ABSOLUTE -> Checkpoint case, the post-side is the
                        // checkpoint section itself which has no ABSOLUTE
                        // neighbours yet — skip the second emission to avoid
                        // a duplicate candidate that would lose the §7.11
                        // tiebreak anyway).
                    }
                }
                else // aCk && bAbs
                {
                    // Checkpoint -> ABSOLUTE: candidate on ABSOLUTE side's Start.
                    AddCandidate(output, i + 1,
                        new AnchorCandidate(boundaryUT, src, AnchorSide.Start));
                }
            }
        }

        private static bool TryDetectSoiAtCheckpointBoundary(in TrackSection a, in TrackSection b)
        {
            // Scan whichever side has checkpoints + frames for a body change
            // across the boundary. The two adjacent sections must reference
            // different bodies (recorded body name) for this to be a SOI.
            string aBody = LastBodyName(a);
            string bBody = FirstBodyName(b);
            if (string.IsNullOrEmpty(aBody) || string.IsNullOrEmpty(bBody)) return false;
            return !string.Equals(aBody, bBody, StringComparison.Ordinal);
        }

        private static string LastBodyName(in TrackSection s)
        {
            if (s.frames != null && s.frames.Count > 0)
                return s.frames[s.frames.Count - 1].bodyName;
            if (s.checkpoints != null && s.checkpoints.Count > 0)
                return s.checkpoints[s.checkpoints.Count - 1].bodyName;
            if (s.absoluteFrames != null && s.absoluteFrames.Count > 0)
                return s.absoluteFrames[s.absoluteFrames.Count - 1].bodyName;
            return null;
        }

        private static string FirstBodyName(in TrackSection s)
        {
            if (s.frames != null && s.frames.Count > 0)
                return s.frames[0].bodyName;
            if (s.checkpoints != null && s.checkpoints.Count > 0)
                return s.checkpoints[0].bodyName;
            if (s.absoluteFrames != null && s.absoluteFrames.Count > 0)
                return s.absoluteFrames[0].bodyName;
            return null;
        }

        // §7.9 SurfaceContinuous markers — Phase 6 emits the candidate; the
        // per-frame raycast that resolves ε is Phase 7 work.
        private static void EmitSurfaceContinuousMarkers(
            Recording rec, Dictionary<int, List<AnchorCandidate>> output)
        {
            if (rec.TrackSections == null) return;
            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                TrackSection s = rec.TrackSections[i];
                if (s.environment != SegmentEnvironment.SurfaceMobile) continue;
                AddCandidate(output, i,
                    new AnchorCandidate(s.startUT, AnchorSource.SurfaceContinuous, AnchorSide.Start));
            }
        }

        // §7.10 Loop marker — emit when the recording has a loop interval +
        // anchor vessel id configured. Reviewer P2-2: the "never configured"
        // sentinel is LoopTiming.UntouchedLoopIntervalSentinel (10.0),
        // not 0 — a fresh Recording has LoopIntervalSeconds == 10.0 by
        // field-initializer and `LoopPlayback == false`. Gate on
        // LoopPlayback (real user intent) so the sentinel default does not
        // emit a phantom Loop candidate.
        private static void EmitLoopMarkers(
            Recording rec, Dictionary<int, List<AnchorCandidate>> output)
        {
            if (!rec.LoopPlayback) return;
            if (rec.LoopAnchorVesselId == 0u) return;
            if (rec.LoopIntervalSeconds <= LoopTiming.UntouchedLoopIntervalSentinel) return;
            if (rec.TrackSections == null || rec.TrackSections.Count == 0) return;
            // Apply to the first eligible section only. "Eligible" here is
            // a forward-compatible "first non-empty section" — the loop
            // resolver in playback owns the per-cycle phase math, Phase 6
            // just marks the section start.
            TrackSection s0 = rec.TrackSections[0];
            AddCandidate(output, 0,
                new AnchorCandidate(s0.startUT, AnchorSource.Loop, AnchorSide.Start));
        }

        // -------------------------------------------------------------------
        //  Helpers
        // -------------------------------------------------------------------

        private static void AddCandidate(
            Dictionary<int, List<AnchorCandidate>> output, int sectionIdx, AnchorCandidate c)
        {
            if (sectionIdx < 0) return;
            if (!output.TryGetValue(sectionIdx, out var list))
            {
                list = new List<AnchorCandidate>();
                output[sectionIdx] = list;
            }
            list.Add(c);
        }

        /// <summary>
        /// Collapse duplicate candidates at the same (UT, side) by §7.11
        /// priority and return a frozen, UT-sorted array.
        /// </summary>
        private static AnchorCandidate[] SelectWinners(List<AnchorCandidate> raw)
        {
            if (raw == null || raw.Count == 0) return new AnchorCandidate[0];

            // Group by (UT, Side). Values within a group compete via
            // AnchorPriority; the winner is what we keep.
            var winners = new Dictionary<(double, AnchorSide), AnchorCandidate>();
            for (int i = 0; i < raw.Count; i++)
            {
                AnchorCandidate c = raw[i];
                var key = (c.UT, c.Side);
                if (!winners.TryGetValue(key, out AnchorCandidate existing))
                {
                    winners[key] = c;
                }
                else if (AnchorPriority.ShouldReplace(existing.Source, c.Source))
                {
                    winners[key] = c;
                }
            }

            var arr = new AnchorCandidate[winners.Count];
            int idx = 0;
            foreach (var kvp in winners)
                arr[idx++] = kvp.Value;
            // Sort by UT, then enum value for stable ordering. Equal UTs
            // with different sides keep the start-before-end ordering by
            // AnchorSide enum value (0 < 1).
            Array.Sort(arr, (x, y) =>
            {
                int cmp = x.UT.CompareTo(y.UT);
                if (cmp != 0) return cmp;
                cmp = ((int)x.Side).CompareTo((int)y.Side);
                if (cmp != 0) return cmp;
                return ((int)x.Source).CompareTo((int)y.Source);
            });
            return arr;
        }
    }
}
