using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Status of <see cref="SwitchSegmentBuilder.ResolveSwitchContinuationParent"/>.
    /// Drives the consume site's decision between attaching the new switch
    /// segment under a parent recording vs. starting a standalone tree.
    /// See plan §"Parent Selection Risk" and §"Behavior by Entry Path".
    /// </summary>
    internal enum SwitchContinuationParentStatus
    {
        /// <summary>Exactly one terminal-leaf recording matched the focused
        /// vessel; <see cref="SwitchContinuationParentResolution.TerminalLeafRecordingId"/>
        /// is the chosen parent.</summary>
        UniqueTerminalLeafFound = 0,

        /// <summary>No recording in the tree matches the focused vessel —
        /// fall through to plan §"Goal 5" standalone tree creation.</summary>
        NoMatchUseStandalone = 1,

        /// <summary>Two or more recordings walk forward to distinct terminal
        /// leaves matching the focused vessel — start a standalone segment
        /// rather than guessing. Caller logs `ambiguous-parent-start-standalone`
        /// (resolver also logs at Warn level with the candidate IDs).</summary>
        AmbiguousStartStandalone = 2,

        /// <summary>Reserved for the Phase C consume site's Goal-4
        /// "BG-lookup failed for any reason" fallthrough: orphaned recording
        /// in tree with no BG entry, BG entry without an in-tree recording
        /// shell, PID collision, restore-time tree-state corruption. Resolver
        /// itself never produces this status (it is purely tree-shape based);
        /// Phase A.4 reserves the enum slot so the live-vessel-side wrapper
        /// can emit one uniform reason out of <see cref="SwitchSegmentBuilder.CreateSwitchContinuationSegment"/>'s
        /// caller. Drives the `bg-lookup-failed-start-standalone` log line per
        /// plan §"Behavior by Entry Path → Goal 4".</summary>
        BgLookupFailedStartStandalone = 3,
    }

    /// <summary>
    /// Result of <see cref="SwitchSegmentBuilder.ResolveSwitchContinuationParent"/>.
    /// </summary>
    internal struct SwitchContinuationParentResolution
    {
        public SwitchContinuationParentStatus Status;

        /// <summary>Non-null only when <see cref="Status"/> is
        /// <see cref="SwitchContinuationParentStatus.UniqueTerminalLeafFound"/>.</summary>
        public string TerminalLeafRecordingId;

        /// <summary>Candidate terminal-leaf IDs collected during forward walk.
        /// Populated even when <see cref="Status"/> is ambiguous, so callers
        /// can log diagnostics; always non-null.</summary>
        public List<string> CandidateRecordingIds;

        /// <summary>Free-form reason text included in logs (e.g. the
        /// candidate count or the matched-but-no-walk explanation).</summary>
        public string AmbiguityReason;
    }

    /// <summary>
    /// Result of <see cref="SwitchSegmentBuilder.CreateSwitchContinuationSegment"/>.
    /// </summary>
    internal struct SwitchContinuationCreationResult
    {
        public bool Created;
        public string NewRecordingId;
        public string NewBranchPointId;
        /// <summary>Null when standalone (no parent).</summary>
        public string ParentRecordingId;
        public BranchPointType BranchType;
        /// <summary>Null on success; one of `parent-not-terminal-leaf`,
        /// `parent-not-in-tree` on refusal.</summary>
        public string FailureReason;
    }

    /// <summary>
    /// Pure tree-mutation helper for switch/Fly continuation segments
    /// (plan §"Segment Creation"). The helper performs steps 3-5, 6, 7,
    /// and 8 of the contract — it does NOT flush background recorder
    /// boundary state (step 2) or remove the focused vessel from
    /// background maps (step 9). Those steps belong to the live-side
    /// wrapper that owns the <see cref="Vessel"/> reference; the wrapper
    /// must complete its parent-side boundary flush BEFORE calling this
    /// helper, then call this helper, then perform the BG-map removal.
    /// The helper is intentionally side-effect-free with respect to
    /// background recorder state and Unity globals, so the same code
    /// path covers tests, headless fixtures, and live FLIGHT.
    /// </summary>
    internal static class SwitchSegmentBuilder
    {
        /// <summary>
        /// Walks the tree from any recording matching <paramref name="focusedVesselPersistentId"/>
        /// (filtered optionally by <paramref name="focusedVesselRecordingIdHint"/>) forward
        /// through <see cref="Recording.ChildBranchPointId"/> / branch-point
        /// <see cref="BranchPoint.ChildRecordingIds"/> to terminal leaves
        /// (<see cref="Recording.ChildBranchPointId"/> == null), keeping only those
        /// terminal leaves whose own <see cref="Recording.VesselPersistentId"/> matches
        /// the focused vessel PID. The resolver is purely read-only — it never
        /// mutates the tree (plan §"Parent Selection Risk" item 1).
        /// <para>A recording whose <see cref="Recording.ChildBranchPointId"/> is
        /// non-empty but whose referenced branch point is missing/empty in
        /// <see cref="RecordingTree.BranchPoints"/> is treated as a tree-corruption
        /// signal: it is NOT added to the candidate-leaf list (the creator
        /// rejects any parent with a non-empty ChildBranchPointId with
        /// <c>parent-not-terminal-leaf</c>, so adding it would only produce a
        /// downstream refused creation), and a Warn line is emitted with
        /// <c>reason=dangling-childbranchpoint</c> plus the offending recording
        /// and missing branch-point IDs so the corruption surfaces in logs.
        /// The resolver returns whatever status the remaining matches produce,
        /// typically <see cref="SwitchContinuationParentStatus.NoMatchUseStandalone"/>
        /// when no other candidate exists.</para>
        /// </summary>
        internal static SwitchContinuationParentResolution ResolveSwitchContinuationParent(
            RecordingTree tree,
            uint focusedVesselPersistentId,
            string focusedVesselRecordingIdHint = null)
        {
            var result = new SwitchContinuationParentResolution
            {
                Status = SwitchContinuationParentStatus.NoMatchUseStandalone,
                TerminalLeafRecordingId = null,
                CandidateRecordingIds = new List<string>(),
                AmbiguityReason = null,
            };

            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
            {
                LogResolverNoMatch(tree, focusedVesselPersistentId, "empty-tree");
                result.AmbiguityReason = "empty-tree";
                return result;
            }

            // Step 1: find recordings matching the focused vessel PID (and
            // optionally the hint ID).
            var matches = new List<Recording>();
            foreach (var kvp in tree.Recordings)
            {
                Recording rec = kvp.Value;
                if (rec == null) continue;
                if (focusedVesselPersistentId != 0
                    && rec.VesselPersistentId != focusedVesselPersistentId)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(focusedVesselRecordingIdHint)
                    && !string.Equals(rec.RecordingId, focusedVesselRecordingIdHint,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                matches.Add(rec);
            }

            if (matches.Count == 0)
            {
                LogResolverNoMatch(tree, focusedVesselPersistentId, "no-pid-match");
                result.AmbiguityReason = "no-pid-match";
                return result;
            }

            // Steps 2-3: for each match, walk forward to its terminal leaf(s).
            // Track distinct terminal-leaf IDs found.
            var leaves = new HashSet<string>(StringComparer.Ordinal);
            foreach (Recording m in matches)
            {
                WalkToTerminalLeaves(tree, m, focusedVesselPersistentId, leaves,
                    new HashSet<string>(StringComparer.Ordinal));
            }

            foreach (string leafId in leaves)
                result.CandidateRecordingIds.Add(leafId);

            if (result.CandidateRecordingIds.Count == 0)
            {
                // Matches existed but none of them (or their descendants) was
                // a PID-coherent terminal leaf. Fall through to standalone.
                LogResolverNoMatch(tree, focusedVesselPersistentId, "no-terminal-leaf");
                result.AmbiguityReason = "no-terminal-leaf";
                return result;
            }

            if (result.CandidateRecordingIds.Count == 1)
            {
                result.Status = SwitchContinuationParentStatus.UniqueTerminalLeafFound;
                result.TerminalLeafRecordingId = result.CandidateRecordingIds[0];
                ParsekLog.Info("SwitchSegmentResolver",
                    string.Format(CultureInfo.InvariantCulture,
                        "unique-terminal-leaf treeId={0} focusedPid={1} leafRecId={2}",
                        tree.Id ?? "<null>",
                        focusedVesselPersistentId,
                        result.TerminalLeafRecordingId));
                return result;
            }

            // More than one candidate -> ambiguous; start standalone.
            result.Status = SwitchContinuationParentStatus.AmbiguousStartStandalone;
            result.AmbiguityReason = "multiple-terminal-leaves";
            ParsekLog.Warn("SwitchSegmentResolver",
                string.Format(CultureInfo.InvariantCulture,
                    "ambiguous-parent-start-standalone treeId={0} focusedPid={1} candidateCount={2} candidates={3}",
                    tree.Id ?? "<null>",
                    focusedVesselPersistentId,
                    result.CandidateRecordingIds.Count,
                    string.Join(",", result.CandidateRecordingIds.ToArray())));
            return result;
        }

        private static void LogResolverNoMatch(
            RecordingTree tree, uint focusedVesselPersistentId, string reason)
        {
            ParsekLog.Info("SwitchSegmentResolver",
                string.Format(CultureInfo.InvariantCulture,
                    "no-match-use-standalone treeId={0} focusedPid={1} reason={2}",
                    tree?.Id ?? "<null>",
                    focusedVesselPersistentId,
                    reason));
        }

        /// <summary>
        /// Recursive forward walk: follows ChildBranchPointId on each visited
        /// recording into the branch point's ChildRecordingIds, only accepting
        /// children whose VesselPersistentId still matches the focused vessel.
        /// Each PID-coherent terminal leaf (ChildBranchPointId == null) is
        /// added to <paramref name="leaves"/>. Visited recording IDs are
        /// tracked in <paramref name="visited"/> to short-circuit cycles
        /// in malformed trees.
        /// </summary>
        private static void WalkToTerminalLeaves(
            RecordingTree tree,
            Recording rec,
            uint focusedVesselPersistentId,
            HashSet<string> leaves,
            HashSet<string> visited)
        {
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return;
            if (!visited.Add(rec.RecordingId))
                return;

            // Terminal: PID must still match.
            if (string.IsNullOrEmpty(rec.ChildBranchPointId))
            {
                if (focusedVesselPersistentId == 0
                    || rec.VesselPersistentId == focusedVesselPersistentId)
                {
                    leaves.Add(rec.RecordingId);
                }
                return;
            }

            // Find the outgoing branch point.
            BranchPoint bp = null;
            if (tree.BranchPoints != null)
            {
                for (int i = 0; i < tree.BranchPoints.Count; i++)
                {
                    BranchPoint candidate = tree.BranchPoints[i];
                    if (candidate != null
                        && string.Equals(candidate.Id, rec.ChildBranchPointId,
                            StringComparison.Ordinal))
                    {
                        bp = candidate;
                        break;
                    }
                }
            }

            if (bp == null || bp.ChildRecordingIds == null
                || bp.ChildRecordingIds.Count == 0)
            {
                // Dangling ChildBranchPointId: the recording claims a
                // continuation but the referenced branch point is missing or
                // empty in tree.BranchPoints. We deliberately do NOT add this
                // recording to leaves — the creator (CreateSwitchContinuationSegment)
                // rejects any parent with a non-empty ChildBranchPointId with
                // `parent-not-terminal-leaf`, so adding it here would only
                // produce a refused creation downstream. A non-null
                // ChildBranchPointId is, by contract, "not a terminal leaf",
                // and a dangling reference is a tree-corruption signal that
                // must surface in the logs, not be papered over by treating
                // the recording as a leaf.
                ParsekLog.Warn("SwitchSegmentResolver",
                    string.Format(CultureInfo.InvariantCulture,
                        "reason=dangling-childbranchpoint treeId={0} recordingId={1} missingBpId={2}",
                        tree?.Id ?? "<null>",
                        rec.RecordingId,
                        rec.ChildBranchPointId ?? "<null>"));
                return;
            }

            for (int i = 0; i < bp.ChildRecordingIds.Count; i++)
            {
                string childId = bp.ChildRecordingIds[i];
                if (string.IsNullOrEmpty(childId)) continue;
                Recording child;
                if (!tree.Recordings.TryGetValue(childId, out child) || child == null)
                    continue;

                // Only follow children that still represent the same vessel —
                // breakup debris and EVA kerbal exits produce children with
                // different PIDs that must not be treated as continuations of
                // the focused vessel.
                if (focusedVesselPersistentId != 0
                    && child.VesselPersistentId != focusedVesselPersistentId)
                {
                    continue;
                }

                WalkToTerminalLeaves(tree, child, focusedVesselPersistentId,
                    leaves, visited);
            }
        }

        /// <summary>
        /// Creates a new <see cref="BranchPointType.VesselSwitchContinuation"/>
        /// segment under <paramref name="parentRecordingIdOrNull"/> (or as
        /// the standalone root of the tree when null). Pure with respect to
        /// background recorder state, Unity globals, and live Vessel objects.
        ///
        /// <para>Steps 3-8 of plan §"Segment Creation":
        /// step 3-5 build a new Recording with the focused-vessel identity
        /// fields and an initial boundary sample; step 6 stamps
        /// <see cref="Recording.SwitchSegmentSessionId"/>; step 7 attaches it
        /// to the chosen terminal-leaf parent via a fresh branch point;
        /// step 8 sets <see cref="RecordingTree.ActiveRecordingId"/>.</para>
        ///
        /// <para>Step 2 (parent-side boundary flush via
        /// <c>BackgroundRecorder.OnVesselRemovedFromBackground</c>) and step 9
        /// (BG-map removal) are the wrapper's job — this helper assumes the
        /// caller has already flushed the parent and will perform the BG-map
        /// removal after the helper returns.</para>
        /// </summary>
        // LOW 10 (PR #876 review): the helper does NOT take a focusedRootPartPid
        // parameter. `Recording` has no field that corresponds to "root part PID
        // at vessel snapshot time" (peers store vessel PIDs and dock-target
        // vessel PID only), so a parameter that only fed a log line was dropped
        // to avoid implying a stored field exists. Re-add it ONLY if you also
        // add a real Recording field (e.g. `RootPartPersistentId`) and assign
        // it from the parameter here — passing it purely for diagnostics fakes
        // a contract the type does not carry.
        internal static SwitchContinuationCreationResult CreateSwitchContinuationSegment(
            RecordingTree tree,
            string parentRecordingIdOrNull,
            uint focusedVesselPersistentId,
            string focusedVesselName,
            double switchUT,
            SwitchSegmentEntryReason entryReason,
            Guid intentId,
            Guid sessionId,
            string newRecordingId,
            string newBranchPointId,
            Func<double, TrajectoryPoint> initialBoundaryPointFactory,
            uint sourceVesselPersistentId = 0)
        {
            var result = new SwitchContinuationCreationResult
            {
                Created = false,
                NewRecordingId = newRecordingId,
                NewBranchPointId = newBranchPointId,
                ParentRecordingId = parentRecordingIdOrNull,
                BranchType = BranchPointType.VesselSwitchContinuation,
                FailureReason = null,
            };

            if (tree == null)
            {
                return Refuse(result, "tree-null", focusedVesselPersistentId,
                    focusedVesselName, sourceVesselPersistentId, switchUT,
                    entryReason, intentId, sessionId);
            }

            if (string.IsNullOrEmpty(newRecordingId))
            {
                return Refuse(result, "new-recording-id-missing", focusedVesselPersistentId,
                    focusedVesselName, sourceVesselPersistentId, switchUT,
                    entryReason, intentId, sessionId);
            }

            if (initialBoundaryPointFactory == null)
            {
                return Refuse(result, "boundary-factory-missing", focusedVesselPersistentId,
                    focusedVesselName, sourceVesselPersistentId, switchUT,
                    entryReason, intentId, sessionId);
            }

            // Validate parent attachment preconditions before any mutation.
            Recording parentRec = null;
            if (!string.IsNullOrEmpty(parentRecordingIdOrNull))
            {
                if (tree.Recordings == null
                    || !tree.Recordings.TryGetValue(parentRecordingIdOrNull, out parentRec)
                    || parentRec == null)
                {
                    return Refuse(result, "parent-not-in-tree", focusedVesselPersistentId,
                        focusedVesselName, sourceVesselPersistentId, switchUT,
                        entryReason, intentId, sessionId);
                }

                if (!string.IsNullOrEmpty(parentRec.ChildBranchPointId))
                {
                    return Refuse(result, "parent-not-terminal-leaf", focusedVesselPersistentId,
                        focusedVesselName, sourceVesselPersistentId, switchUT,
                        entryReason, intentId, sessionId);
                }

                // Also require a fresh branch-point id for attached creation.
                if (string.IsNullOrEmpty(newBranchPointId))
                {
                    return Refuse(result, "new-branch-point-id-missing", focusedVesselPersistentId,
                        focusedVesselName, sourceVesselPersistentId, switchUT,
                        entryReason, intentId, sessionId);
                }
            }

            // Steps 3-5: build the new Recording with focused-vessel identity
            // fields, format/schema versions, the SwitchSegment session stamp,
            // and an initial boundary sample so the segment is non-empty.
            TrajectoryPoint boundary = initialBoundaryPointFactory(switchUT);
            var newRec = new Recording
            {
                RecordingId = newRecordingId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
                TreeId = tree.Id,
                VesselPersistentId = focusedVesselPersistentId,
                // Resolve KSP localization keys (e.g. stock-craft "#autoLOC_501224"
                // -> "Jumping Flea") at the single point that constructs the
                // continuation Recording so any future fifth caller is immune
                // to passing a raw token. Idempotent: already-resolved strings
                // and non-"#" prefixes fall through unchanged. Diagnostic logs
                // elsewhere still see the raw value for KSP.log grep parity.
                VesselName = Recording.ResolveLocalizedName(focusedVesselName) ?? string.Empty,
                ExplicitStartUT = switchUT,
                // Step 6: stamp the SwitchSegmentSession owner. We deliberately
                // do not touch CreatingSessionId — that field is owned by Re-Fly.
                SwitchSegmentSessionId = sessionId.ToString("D", CultureInfo.InvariantCulture),
            };
            newRec.Points.Add(boundary);

            // Step 7: attach under the parent terminal leaf via a fresh
            // VesselSwitchContinuation branch point (or skip when standalone).
            BranchPoint bp = null;
            if (parentRec != null)
            {
                bp = new BranchPoint
                {
                    Id = newBranchPointId,
                    UT = switchUT,
                    Type = BranchPointType.VesselSwitchContinuation,
                    ParentRecordingIds = new List<string> { parentRec.RecordingId },
                    ChildRecordingIds = new List<string> { newRecordingId },
                };
                parentRec.ChildBranchPointId = newBranchPointId;
                newRec.ParentBranchPointId = newBranchPointId;
                if (tree.BranchPoints == null)
                    tree.BranchPoints = new List<BranchPoint>();
                tree.BranchPoints.Add(bp);
            }

            // Step 8: register the new recording and set it active.
            tree.AddOrReplaceRecording(newRec);
            tree.ActiveRecordingId = newRecordingId;

            result.Created = true;
            result.FailureReason = null;
            result.NewBranchPointId = parentRec != null ? newBranchPointId : null;
            result.ParentRecordingId = parentRec != null ? parentRec.RecordingId : null;

            // Step 10: log the segment creation with every diagnostic field
            // the plan calls out (intent ID, parent ID, segment ID, tree ID,
            // source PID, focused PID, reason, UT).
            ParsekLog.Info("SwitchSegment",
                string.Format(CultureInfo.InvariantCulture,
                    "created intentId={0} sessionId={1} treeId={2} parentRecId={3} segmentRecId={4} branchPointId={5} sourcePid={6} focusedPid={7} focusedName='{8}' reason={9} ut={10}",
                    intentId.ToString("D", CultureInfo.InvariantCulture),
                    sessionId.ToString("D", CultureInfo.InvariantCulture),
                    tree.Id ?? "<null>",
                    result.ParentRecordingId ?? "<standalone>",
                    newRecordingId,
                    result.NewBranchPointId ?? "<none>",
                    sourceVesselPersistentId,
                    focusedVesselPersistentId,
                    focusedVesselName ?? string.Empty,
                    entryReason,
                    switchUT.ToString("R", CultureInfo.InvariantCulture)));

            return result;
        }

        /// <summary>
        /// Stamps <paramref name="reason"/> as the failure reason on
        /// <paramref name="result"/>, emits the standard refusal log line, and
        /// returns the result. Folds the six byte-identical precondition-failure
        /// blocks in <see cref="CreateSwitchContinuationSegment"/>; the
        /// <see cref="LogCreationRefused"/> payload is identical across the sites,
        /// so only <paramref name="reason"/> varies.
        /// </summary>
        private static SwitchContinuationCreationResult Refuse(
            SwitchContinuationCreationResult result,
            string reason,
            uint focusedVesselPersistentId,
            string focusedVesselName,
            uint sourceVesselPersistentId,
            double switchUT,
            SwitchSegmentEntryReason entryReason,
            Guid intentId,
            Guid sessionId)
        {
            result.FailureReason = reason;
            LogCreationRefused(result, focusedVesselPersistentId,
                focusedVesselName, sourceVesselPersistentId, switchUT,
                entryReason, intentId, sessionId);
            return result;
        }

        private static void LogCreationRefused(
            SwitchContinuationCreationResult result,
            uint focusedVesselPersistentId,
            string focusedVesselName,
            uint sourceVesselPersistentId,
            double switchUT,
            SwitchSegmentEntryReason entryReason,
            Guid intentId,
            Guid sessionId)
        {
            ParsekLog.Warn("SwitchSegment",
                string.Format(CultureInfo.InvariantCulture,
                    "refused failureReason={0} intentId={1} sessionId={2} parentRecId={3} segmentRecId={4} sourcePid={5} focusedPid={6} focusedName='{7}' reason={8} ut={9}",
                    result.FailureReason ?? "<null>",
                    intentId.ToString("D", CultureInfo.InvariantCulture),
                    sessionId.ToString("D", CultureInfo.InvariantCulture),
                    result.ParentRecordingId ?? "<standalone>",
                    result.NewRecordingId ?? "<null>",
                    sourceVesselPersistentId,
                    focusedVesselPersistentId,
                    focusedVesselName ?? string.Empty,
                    entryReason,
                    switchUT.ToString("R", CultureInfo.InvariantCulture)));
        }
    }
}
