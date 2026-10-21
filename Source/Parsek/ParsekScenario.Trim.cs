using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    public partial class ParsekScenario
    {
        /// <summary>
        /// Truncates a live active-tree recording to the current quickload-resume UT so
        /// post-load recording can continue from the restored timeline without appending
        /// stale samples/events from the pre-load future. Returns true when any payload was
        /// removed or clipped.
        /// </summary>
        internal static bool TrimRecordingPastUT(Recording rec, double cutoffUT)
        {
            if (rec == null || double.IsNaN(cutoffUT) || double.IsInfinity(cutoffUT))
                return false;

            bool mutated = false;
            bool closedBeforeCutoff = IsClosedBeforeCutoff(rec, cutoffUT);

            mutated |= RemoveItemsPastUT(rec.Points, cutoffUT, p => p.ut);
            mutated |= TrimOrbitSegmentsPastUT(rec.OrbitSegments, cutoffUT);
            mutated |= RemoveItemsPastUT(rec.PartEvents, cutoffUT, e => e.ut);
            mutated |= RemoveItemsPastUT(rec.FlagEvents, cutoffUT, e => e.ut);
            mutated |= RemoveItemsPastUT(rec.SegmentEvents, cutoffUT, e => e.ut);
            mutated |= TrimTrackSectionsPastUT(rec.TrackSections, cutoffUT);

            if (!double.IsNaN(rec.ExplicitStartUT) && rec.ExplicitStartUT > cutoffUT)
            {
                rec.ExplicitStartUT = cutoffUT;
                mutated = true;
            }

            // A recording still open at the cutoff continues from it, so its end is the cutoff.
            // One closed before the cutoff (a terminal, or a split it handed on at) keeps its own
            // end: stamping the cutoff would make it read as existing until the resume moment.
            if (rec.ExplicitEndUT > cutoffUT
                || (double.IsNaN(rec.ExplicitEndUT) && !closedBeforeCutoff))
            {
                rec.ExplicitEndUT = cutoffUT;
                mutated = true;
            }

            if (mutated)
                rec.MarkFilesDirty();

            return mutated;
        }

        /// <summary>
        /// Pure: true when <paramref name="rec"/> ended at or before <paramref name="cutoffUT"/>
        /// by a terminal state or at a split it handed on at (a child branch point).
        /// </summary>
        internal static bool IsClosedBeforeCutoff(Recording rec, double cutoffUT)
        {
            if (rec == null)
                return false;
            if (!rec.TerminalStateValue.HasValue && string.IsNullOrEmpty(rec.ChildBranchPointId))
                return false;
            return rec.EndUT <= cutoffUT;
        }

        /// <summary>
        /// Cuts every recording of <paramref name="tree"/> back to <paramref name="cutoffUT"/> and
        /// prunes the recordings that start at or after it, except the ids in
        /// <paramref name="keepWhole"/>: committed history the quicksave already shows
        /// (<see cref="CollectQuicksaveHistoryKeptWhole"/>), which no load may cut or prune
        /// (owner ruling D2). Returns true when anything changed.
        /// </summary>
        internal static bool TrimRecordingTreePastUT(
            RecordingTree tree, double cutoffUT, HashSet<string> keepWhole = null)
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0
                || double.IsNaN(cutoffUT) || double.IsInfinity(cutoffUT))
            {
                return false;
            }

            bool mutated = false;
            int trimmedCount = 0;
            int keptWholeCount = 0;
            HashSet<string> futureOnlyIds = CollectFutureOnlyRecordingIds(tree, cutoffUT);
            if (futureOnlyIds != null && keepWhole != null)
                futureOnlyIds.RemoveWhere(keepWhole.Contains);
            int recordingCountBeforeTrim = tree.Recordings.Count;
            foreach (KeyValuePair<string, Recording> kvp in tree.Recordings)
            {
                if (keepWhole != null && keepWhole.Contains(kvp.Key))
                {
                    keptWholeCount++;
                    continue;
                }
                if (TrimRecordingPastUT(kvp.Value, cutoffUT))
                {
                    mutated = true;
                    trimmedCount++;
                }
            }

            if (mutated || keptWholeCount > 0 || (futureOnlyIds != null && futureOnlyIds.Count > 0))
            {
                int prunedRecordings = PruneFutureOnlyRecordings(tree, futureOnlyIds);
                int prunedBranchPoints = RemoveEmptyBranchPoints(tree);
                if (prunedRecordings > 0 || prunedBranchPoints > 0)
                    mutated = true;

                tree.RebuildBackgroundMap();
                ParsekLog.Info("Scenario",
                    $"Quickload tree trim: tree='{tree.TreeName}' cutoffUT={cutoffUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"trimmedRecordings={trimmedCount}/{recordingCountBeforeTrim} " +
                    $"prunedFutureRecordings={prunedRecordings} prunedBranchPoints={prunedBranchPoints} " +
                    $"keptWholeHistory={keptWholeCount} " +
                    $"backgroundEntries={tree.BackgroundMap.Count}");
            }

            return mutated;
        }

        /// <summary>
        /// Bug #610: scope of the quickload-resume tail trim. Tree-wide is correct
        /// for F9 quickload — the world rewound, every recording's post-cutoff data
        /// is stale and future-only recordings never existed at the resume UT.
        /// Re-Fly is different: the splice has already restored post-RP recordings
        /// that represent OTHER vessels' continued timelines and the re-flown
        /// vessel's destroyed-fork; tree-wide trimming would clip and prune them.
        /// Only the in-place continuation target (the active rec) needs its tail
        /// trimmed so the recorder can append fresh post-cutoff data without
        /// colliding with the pre-cutoff timeline.
        /// </summary>
        internal enum QuickloadTrimScope
        {
            TreeWide = 0,
            ActiveRecOnly = 1,
        }

        /// <summary>
        /// Picks the trim scope based on whether an active Re-Fly session pins
        /// this tree. Pure function so the decision is unit-testable. The
        /// <paramref name="reason"/> string is appended to the resume-prep log
        /// line so the chosen branch is auditable from KSP.log alone (#610).
        /// </summary>
        internal static QuickloadTrimScope ChooseQuickloadTrimScope(
            string treeId,
            ReFlySessionMarker marker,
            out string reason)
        {
            if (marker == null)
            {
                reason = "no-active-refly-marker";
                return QuickloadTrimScope.TreeWide;
            }
            if (string.IsNullOrEmpty(marker.TreeId))
            {
                reason = $"refly-marker-has-no-treeid sess={marker.SessionId ?? "<no-id>"}";
                return QuickloadTrimScope.TreeWide;
            }
            if (string.IsNullOrEmpty(treeId))
            {
                reason = $"resume-tree-has-no-id markerTree={marker.TreeId}";
                return QuickloadTrimScope.TreeWide;
            }
            if (!string.Equals(marker.TreeId, treeId, StringComparison.Ordinal))
            {
                reason = $"refly-marker-tree-mismatch markerTree={marker.TreeId} resumeTree={treeId} sess={marker.SessionId ?? "<no-id>"}";
                return QuickloadTrimScope.TreeWide;
            }
            reason = $"refly-active sess={marker.SessionId ?? "<no-id>"} markerTree={marker.TreeId} originRec={marker.OriginChildRecordingId ?? "<null>"}";
            return QuickloadTrimScope.ActiveRecOnly;
        }

        // ------------------------------------------------------------------
        // Quicksave facts: what the loaded save says about each member of its
        // active tree. The quickload-resume trim leaves whole the members the
        // quicksave already shows as committed history.
        // ------------------------------------------------------------------

        /// <summary>
        /// What the quicksave itself says about one member of its active tree, read from the
        /// save node BEFORE the restore hydrates or refreshes anything. Only node metadata is
        /// used: the trajectory sidecar can be newer than the save at the same epoch (a commit
        /// flushes dirty sidecars without advancing a positive epoch,
        /// <c>RecordingStore.ShouldAdvanceSidecarEpochOnFlush</c>), so a hydrated trajectory end
        /// is not evidence of what the member was at the save moment. The terminal state, the
        /// explicit start / end UTs and the branch-point UTs live in the node itself, written by
        /// OnSave at the save moment.
        /// </summary>
        internal struct QuicksaveMemberFacts
        {
            internal bool HasTerminal;
            internal TerminalState? Terminal;
            internal double ExplicitStartUT;
            internal double ExplicitEndUT;
            internal double StartBranchUT;
            internal double EndBranchUT;
            internal bool CommittedInQuicksave;
        }

        /// <summary>The quicksave's facts about every member of one restored active tree.</summary>
        internal sealed class QuicksaveTreeFacts
        {
            internal string TreeId;
            internal string ActiveRecordingId;
            // The save node also holds a COMMITTED tree with this id (a copy-on-write restore
            // clone of a tree committed before the save).
            internal bool TreeCommittedInQuicksave;
            internal readonly Dictionary<string, QuicksaveMemberFacts> Members =
                new Dictionary<string, QuicksaveMemberFacts>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Pure: the ids of every recording the save node holds in a COMMITTED tree (active and
        /// pending tree nodes skipped). OnSave writes every committed tree before the active one,
        /// so a member whose id is here was committed history when the quicksave was taken (a
        /// copy-on-write restore clone shares its committed tree's ids). One exception:
        /// <c>SaveTreeRecordings</c> drops a committed tree it cannot serialize (Error) when no
        /// last-known-good node can be carried forward; the clone shares the failing recordings'
        /// ids and sidecars, so the same failure keeps its active node out of that save too
        /// (<c>SaveActiveTreeIfAny</c> writes both-or-neither).
        /// </summary>
        internal static HashSet<string> CollectQuicksaveCommittedRecordingIds(ConfigNode scenarioNode)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (scenarioNode == null)
                return ids;
            foreach (ConfigNode treeNode in scenarioNode.GetNodes("RECORDING_TREE"))
            {
                if (treeNode == null || IsActiveTreeNode(treeNode) || IsPendingTreeNode(treeNode))
                    continue;
                foreach (ConfigNode recNode in treeNode.GetNodes("RECORDING"))
                {
                    string recordingId = recNode?.GetValue("recordingId");
                    if (!string.IsNullOrEmpty(recordingId))
                        ids.Add(recordingId);
                }
            }
            return ids;
        }

        /// <summary>
        /// Pure: the ids of every COMMITTED tree the save node holds (active and pending tree
        /// nodes skipped).
        /// </summary>
        internal static HashSet<string> CollectQuicksaveCommittedTreeIds(ConfigNode scenarioNode)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (scenarioNode == null)
                return ids;
            foreach (ConfigNode treeNode in scenarioNode.GetNodes("RECORDING_TREE"))
            {
                if (treeNode == null || IsActiveTreeNode(treeNode) || IsPendingTreeNode(treeNode))
                    continue;
                string treeId = treeNode.GetValue("id");
                if (!string.IsNullOrEmpty(treeId))
                    ids.Add(treeId);
            }
            return ids;
        }

        /// <summary>
        /// Pure: captures <see cref="QuicksaveTreeFacts"/> from an active tree just loaded from a
        /// save node, before hydration, salvage or the same-id refresh change any member.
        /// </summary>
        internal static QuicksaveTreeFacts CaptureQuicksaveTreeFacts(
            RecordingTree loadedTree, HashSet<string> committedIdsInQuicksave,
            HashSet<string> committedTreeIdsInQuicksave = null)
        {
            if (loadedTree == null || string.IsNullOrEmpty(loadedTree.Id))
                return null;

            var branchUTs = new Dictionary<string, double>(StringComparer.Ordinal);
            if (loadedTree.BranchPoints != null)
            {
                for (int i = 0; i < loadedTree.BranchPoints.Count; i++)
                {
                    BranchPoint bp = loadedTree.BranchPoints[i];
                    if (bp != null && !string.IsNullOrEmpty(bp.Id))
                        branchUTs[bp.Id] = bp.UT;
                }
            }

            var facts = new QuicksaveTreeFacts
            {
                TreeId = loadedTree.Id,
                ActiveRecordingId = loadedTree.ActiveRecordingId,
                TreeCommittedInQuicksave = committedTreeIdsInQuicksave != null
                    && committedTreeIdsInQuicksave.Contains(loadedTree.Id),
            };
            if (loadedTree.Recordings == null)
                return facts;

            foreach (KeyValuePair<string, Recording> kvp in loadedTree.Recordings)
            {
                Recording rec = kvp.Value;
                if (string.IsNullOrEmpty(kvp.Key) || rec == null)
                    continue;
                facts.Members[kvp.Key] = new QuicksaveMemberFacts
                {
                    HasTerminal = rec.TerminalStateValue.HasValue,
                    Terminal = rec.TerminalStateValue,
                    ExplicitStartUT = rec.ExplicitStartUT,
                    ExplicitEndUT = rec.ExplicitEndUT,
                    StartBranchUT = LookupBranchUT(branchUTs, rec.ParentBranchPointId),
                    EndBranchUT = LookupBranchUT(branchUTs, rec.ChildBranchPointId),
                    CommittedInQuicksave = committedIdsInQuicksave != null
                        && committedIdsInQuicksave.Contains(kvp.Key),
                };
            }
            return facts;
        }

        private static double LookupBranchUT(Dictionary<string, double> branchUTs, string branchPointId)
        {
            return !string.IsNullOrEmpty(branchPointId) && branchUTs.TryGetValue(branchPointId, out double ut)
                ? ut
                : double.NaN;
        }

        /// <summary>
        /// Pure: true when the quicksave already shows <paramref name="recordingId"/> as history
        /// that reaches past the cutoff, so the resume trim must leave it whole. That is a recording the save holds in a committed tree, or one whose
        /// quicksaved copy already carries a terminal state, an explicit end after the cutoff, an
        /// end at a branch point after the cutoff, or a start after the cutoff: none of these can
        /// describe a member being recorded live when the quicksave was taken, because a live
        /// member's samples, stamps and branch points all lie at or before the save moment. The
        /// quicksave's own active recording is exempt from the terminal fact: it was live by
        /// definition, and a stale Destroyed from a NullSolver fallback can sit on it
        /// (<c>IncompleteBallisticSceneExitFinalizer.ClearStaleDestroyedTerminalForResume</c>).
        /// A member absent from the quicksave (spliced in, or created after the save), or no facts
        /// at all (the resumed tree did not come from a save node), answers false. The terminal
        /// fact is not trusted alone: when <paramref name="current"/> (the member as the restore
        /// left it) shows the ending restamped after the save (<see cref="IsEndRestampedAfterQuicksave"/>),
        /// the restamp is the abandoned future and the member answers false with
        /// <see cref="RestampedAfterQuicksaveReason"/>.
        /// </summary>
        internal static bool IsCommittedHistoryAtQuicksave(
            QuicksaveTreeFacts facts, string recordingId, double cutoffUT, out string reason,
            Recording current = null)
        {
            reason = null;
            if (facts == null || string.IsNullOrEmpty(recordingId)
                || !facts.Members.TryGetValue(recordingId, out QuicksaveMemberFacts member))
            {
                return false;
            }

            if (member.CommittedInQuicksave)
            {
                reason = "committed-in-quicksave";
                return true;
            }
            if (string.Equals(recordingId, facts.ActiveRecordingId, StringComparison.Ordinal))
                return false;
            if (member.HasTerminal)
            {
                if (IsEndRestampedAfterQuicksave(member, current, cutoffUT))
                {
                    reason = RestampedAfterQuicksaveReason;
                    return false;
                }
                reason = "terminal-in-quicksave";
                return true;
            }
            if (member.ExplicitEndUT > cutoffUT)
            {
                reason = "end-after-cutoff-in-quicksave";
                return true;
            }
            if (member.EndBranchUT > cutoffUT)
            {
                reason = "branch-after-cutoff-in-quicksave";
                return true;
            }
            if (member.ExplicitStartUT > cutoffUT || member.StartBranchUT > cutoffUT)
            {
                reason = "start-after-cutoff-in-quicksave";
                return true;
            }
            return false;
        }

        internal const string RestampedAfterQuicksaveReason = "restamped-after-quicksave";

        /// <summary>
        /// Pure: a member the quicksave holds as already ended (a terminal, an explicit end at or
        /// before the cutoff) whose restored copy carries a DIFFERENT terminal and an explicit end
        /// after the cutoff. Only an event after the save writes that: the recovery or termination
        /// stamp (<c>ParsekScenario.ApplyTerminalEventStamp</c>, Recovered / Destroyed over a
        /// situation terminal, <see cref="CanOverwriteTerminalState"/>) sets both, and the same-id
        /// refresh brings the committed copy's stamp onto the restored member. Commit bookkeeping
        /// is not a restamp: a split first half or a trimmed tail ends at or before the old end,
        /// and finalization fills a missing explicit end without touching an existing terminal.
        /// </summary>
        internal static bool IsEndRestampedAfterQuicksave(
            QuicksaveMemberFacts member, Recording current, double cutoffUT)
        {
            // No captured terminal value: nothing to compare, so the terminal fact stands
            // (the conservative side: committed history is never retired).
            if (current == null || !member.HasTerminal || !member.Terminal.HasValue
                || double.IsNaN(cutoffUT) || double.IsInfinity(cutoffUT))
            {
                return false;
            }
            if (member.ExplicitEndUT > cutoffUT)
                return false;
            if (!(current.ExplicitEndUT > cutoffUT))
                return false;
            return !Nullable.Equals(member.Terminal, current.TerminalStateValue);
        }

        /// <summary>
        /// Pure: the members the quickload-resume trim must leave whole, the committed history
        /// the quicksave already shows (<see cref="IsCommittedHistoryAtQuicksave"/> over
        /// <paramref name="quicksaveFacts"/>, each member read as the restore left it). The
        /// active recording is never one: the recorder appends to it from the cutoff on, so it
        /// is always cut there. Null when there are no facts (nothing is kept whole).
        /// </summary>
        internal static HashSet<string> CollectQuicksaveHistoryKeptWhole(
            RecordingTree tree, string activeRecordingId, double cutoffUT, QuicksaveTreeFacts quicksaveFacts)
        {
            if (quicksaveFacts == null || tree?.Recordings == null
                || double.IsNaN(cutoffUT) || double.IsInfinity(cutoffUT))
            {
                return null;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Recording> kvp in tree.Recordings)
            {
                if (kvp.Value == null
                    || string.Equals(kvp.Key, activeRecordingId, StringComparison.Ordinal))
                {
                    continue;
                }
                if (IsCommittedHistoryAtQuicksave(quicksaveFacts, kvp.Key, cutoffUT, out _, kvp.Value))
                    ids.Add(kvp.Key);
            }
            return ids;
        }

        /// <summary>
        /// The quickload-resume trim, called once from
        /// <c>FlightRecorder.PrepareQuickloadResumeStateIfNeeded</c> when the recorder resumes a
        /// restored tree: the active recording alone under the Re-Fly scope, else the whole tree
        /// with the quicksave's committed history left whole
        /// (<see cref="CollectQuicksaveHistoryKeptWhole"/>). Returns whether it changed anything.
        /// </summary>
        internal static bool TrimForQuickloadResume(
            RecordingTree tree,
            Recording activeRec,
            double resumeUT,
            QuickloadTrimScope scope,
            QuicksaveTreeFacts quicksaveFacts)
        {
            if (scope == QuickloadTrimScope.ActiveRecOnly)
                return TrimRecordingPastUT(activeRec, resumeUT);
            HashSet<string> keepWhole = CollectQuicksaveHistoryKeptWhole(
                tree, activeRec?.RecordingId, resumeUT, quicksaveFacts);
            return TrimRecordingTreePastUT(tree, resumeUT, keepWhole);
        }

        /// <summary>Reason the in-session load passes to <see cref="Ledger.RetireAbandonedFutureActions"/>
        /// for the untagged rows made after the loaded save.</summary>
        internal const string UntaggedAfterSaveReason = "quickload-untagged-after-save";

        /// <summary>
        /// Pure: the clock just before an in-session load, the latest usable (finite, positive)
        /// of the flight scene-change stamp (<paramref name="preChangeUT"/>), the stamp every
        /// scene request takes (<paramref name="sceneRequestClockUT"/>) and the Planetarium
        /// reading inside OnLoad (<paramref name="planetariumUT"/>, still the outgoing clock at
        /// the Space Center and the Tracking Station, the loaded clock in flight). NaN when none
        /// is usable.
        /// </summary>
        internal static double ResolvePreLoadClockUT(
            double preChangeUT, double sceneRequestClockUT, double planetariumUT)
        {
            double best = double.NaN;
            foreach (double ut in new[] { preChangeUT, sceneRequestClockUT, planetariumUT })
            {
                if (double.IsNaN(ut) || double.IsInfinity(ut) || ut <= 0.0)
                    continue;
                if (double.IsNaN(best) || ut > best)
                    best = ut;
            }
            return best;
        }

        /// <summary>
        /// Pure: whether an in-session load back in time retires <paramref name="action"/> as a
        /// row made after the loaded save (owner ruling 2026-10-19, "a quickload also undoes Space
        /// Center purchases made after the quicksave"; rewind design section 2.12 Rule 3). Only an
        /// untagged row (a Space Center purchase, upgrade, hire or contract action, a launch-pad
        /// row from before a recording started) dated strictly after <paramref name="saveUT"/> and
        /// no later than the clock before the load (<paramref name="preLoadClockUT"/>): a row
        /// past that clock was already in the ledger as committed future before the load (a
        /// rewind keeps such rows, ruling D2) and is not part of what the player did after the
        /// save. A row the loaded save lists in <paramref name="knownAtSave"/> already existed
        /// when the save was written (a D2 row the clock has since passed) and stays; null means
        /// the save carries no list and the window alone decides. Never a seed.
        /// </summary>
        internal static bool ShouldRetireUntaggedRowAfterSave(
            GameAction action, double saveUT, double preLoadClockUT, ICollection<string> knownAtSave = null)
        {
            if (!IsUntaggedSaveScopedRow(action))
                return false;
            if (knownAtSave != null && action.ActionId != null && knownAtSave.Contains(action.ActionId))
                return false;
            return action.UT > saveUT && action.UT <= preLoadClockUT;
        }

        /// <summary>Pure: an untagged, non-seed ledger row, the set the save's id list
        /// and the in-session retire both cover.</summary>
        internal static bool IsUntaggedSaveScopedRow(GameAction action)
        {
            if (action == null || !string.IsNullOrEmpty(action.RecordingId))
                return false;
            return !RecalculationEngine.IsSeedType(action.Type);
        }

        /// <summary>Child node of the PARSEK scenario node that lists, by ActionId, the untagged
        /// rows dated after the save that the ledger already held when the save was written.</summary>
        internal const string UntaggedFutureRowsNodeName = "UNTAGGED_FUTURE_ROWS";

        /// <summary>Margin below the save clock that the list also covers: listing a row that
        /// existed at the save never retires anything, so the list errs wide.</summary>
        internal const double UntaggedFutureRowsListMarginSeconds = 1.0;

        /// <summary>
        /// Pure over the node: writes <see cref="UntaggedFutureRowsNodeName"/> into the PARSEK
        /// scenario node, one <c>id</c> value per untagged, non-seed row of
        /// <paramref name="actions"/> dated after <paramref name="saveClockUT"/> (less a one-second
        /// margin; every such row when the clock is unusable), plus the save clock as
        /// <c>saveUT</c>. Always written, even empty: the node's presence is what tells a later
        /// in-session load that the list is exact (a save from before the list has none and the
        /// load falls back to the clock window). These rows already existed when the save was
        /// written (a rewind left them as committed future, ruling D2), so a load of this save
        /// keeps them. A scenario-node key, not a recording shape change.
        /// </summary>
        internal static int WriteUntaggedFutureRowsNode(
            ConfigNode scenarioNode, double saveClockUT, IEnumerable<GameAction> actions)
        {
            if (scenarioNode == null)
                return 0;
            scenarioNode.RemoveNodes(UntaggedFutureRowsNodeName);
            ConfigNode list = scenarioNode.AddNode(UntaggedFutureRowsNodeName);
            bool clockUsable = !double.IsNaN(saveClockUT) && !double.IsInfinity(saveClockUT) && saveClockUT > 0.0;
            if (clockUsable)
                list.AddValue("saveUT", saveClockUT.ToString("R", CultureInfo.InvariantCulture));

            int count = 0;
            if (actions != null)
            {
                double floorUT = saveClockUT - UntaggedFutureRowsListMarginSeconds;
                foreach (GameAction action in actions)
                {
                    if (!IsUntaggedSaveScopedRow(action) || string.IsNullOrEmpty(action.ActionId))
                        continue;
                    if (clockUsable && !(action.UT > floorUT))
                        continue;
                    list.AddValue("id", action.ActionId);
                    count++;
                }
            }

            ParsekLog.Verbose(LoadReconcilePolicy.LogTag,
                $"Untagged future rows listed in the save: count={count.ToString(CultureInfo.InvariantCulture)} " +
                $"saveUT={saveClockUT.ToString("R", CultureInfo.InvariantCulture)}");
            return count;
        }

        /// <summary>
        /// Reads the loaded save's <see cref="UntaggedFutureRowsNodeName"/> list. Returns the id
        /// set with <paramref name="mode"/> <c>exact</c>; null with <c>window-legacy</c> when the
        /// save has no list (written before it existed), or with <c>window-save-mismatch</c> when
        /// the list's <c>saveUT</c> is not the loaded save's clock (the node came from another
        /// save), so the in-session retire falls back to the clock window alone.
        /// </summary>
        internal static HashSet<string> ReadUntaggedFutureRowIds(
            ConfigNode scenarioNode, double loadedSaveUT, out string mode)
        {
            ConfigNode list = scenarioNode?.GetNode(UntaggedFutureRowsNodeName);
            if (list == null)
            {
                mode = "window-legacy";
                ParsekLog.Verbose(LoadReconcilePolicy.LogTag,
                    $"Untagged rows after the save: no list in the loaded save mode={mode}");
                return null;
            }

            string savedText = list.GetValue("saveUT");
            if (!string.IsNullOrEmpty(savedText)
                && double.TryParse(savedText, NumberStyles.Float, CultureInfo.InvariantCulture, out double listSaveUT)
                && !double.IsNaN(loadedSaveUT) && !double.IsInfinity(loadedSaveUT)
                && Math.Abs(listSaveUT - loadedSaveUT) > UntaggedFutureRowsListMarginSeconds)
            {
                mode = "window-save-mismatch";
                ParsekLog.Info(LoadReconcilePolicy.LogTag,
                    $"Untagged rows after the save: the list belongs to another save " +
                    $"listSaveUT={listSaveUT.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"loadedSaveUT={loadedSaveUT.ToString("R", CultureInfo.InvariantCulture)} mode={mode}");
                return null;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in list.GetValues("id"))
            {
                if (!string.IsNullOrEmpty(id))
                    ids.Add(id);
            }
            mode = "exact";
            ParsekLog.Verbose(LoadReconcilePolicy.LogTag,
                $"Untagged rows after the save: list read count={ids.Count.ToString(CultureInfo.InvariantCulture)} mode={mode}");
            return ids;
        }

        /// <summary>
        /// The in-session load's retire of untagged rows made after the loaded save, run from
        /// <see cref="OnLoad"/> beside the route reconcile and before the ledger recalculation,
        /// on the load kinds whose <see cref="LoadStateCategory.UntaggedLedgerRowsAfterCutoff"/>
        /// cell is <see cref="LoadReconcileAction.ReconcileAtCutoff"/> (a quickload in flight and
        /// every other in-session load: the Space Center or Tracking Station load of an earlier
        /// save, and the load of a flight quicksave from the Space Center). The window
        /// (<paramref name="saveUT"/>, <paramref name="preLoadClockUT"/>] is empty on a forward or
        /// same-instant scene change, so those touch nothing. Inside the window a row the save's
        /// id list names (<paramref name="knownAtSave"/>, from <see cref="ReadUntaggedFutureRowIds"/>)
        /// stays, so the load restores the ledger exactly as the save held it; a save with no list
        /// passes null and <paramref name="listMode"/> names the fallback. Removes rows only, never
        /// adds one, so it is OnLoad-safe. Returns the number retired.
        /// </summary>
        internal static int RetireUntaggedRowsAfterSaveAtInSessionLoad(
            LoadKind kind, double saveUT, double preLoadClockUT,
            ICollection<string> knownAtSave = null, string listMode = null)
        {
            string mode = knownAtSave != null ? "exact" : (listMode ?? "window-legacy");
            if (LoadReconcilePolicy.Decide(kind, LoadStateCategory.UntaggedLedgerRowsAfterCutoff).Action
                != LoadReconcileAction.ReconcileAtCutoff)
            {
                ParsekLog.Verbose(LoadReconcilePolicy.LogTag,
                    $"Untagged rows after the save: not this load's reconcile kind={kind}");
                return 0;
            }

            string saveText = saveUT.ToString("R", CultureInfo.InvariantCulture);
            string clockText = preLoadClockUT.ToString("R", CultureInfo.InvariantCulture);
            bool saveUsable = !double.IsNaN(saveUT) && !double.IsInfinity(saveUT) && saveUT > 0.0;
            bool clockUsable = !double.IsNaN(preLoadClockUT) && !double.IsInfinity(preLoadClockUT);
            if (!saveUsable || !clockUsable || preLoadClockUT <= saveUT)
            {
                ParsekLog.Verbose(LoadReconcilePolicy.LogTag,
                    $"Untagged rows after the save: nothing to retire kind={kind} reason=no-window " +
                    $"saveUT={saveText} clockUT={clockText} mode={mode}");
                return 0;
            }

            int seedsKept = 0;
            int keptKnown = 0;
            int retired = Ledger.RetireAbandonedFutureActions(action =>
            {
                if (action.UT > saveUT && action.UT <= preLoadClockUT
                    && string.IsNullOrEmpty(action.RecordingId))
                {
                    if (RecalculationEngine.IsSeedType(action.Type))
                        seedsKept++;
                    else if (knownAtSave != null && action.ActionId != null && knownAtSave.Contains(action.ActionId))
                        keptKnown++;
                }
                return ShouldRetireUntaggedRowAfterSave(action, saveUT, preLoadClockUT, knownAtSave);
            }, UntaggedAfterSaveReason);
            string tail = $"keptKnown={keptKnown.ToString(CultureInfo.InvariantCulture)} mode={mode}";

            if (retired > 0)
            {
                ParsekLog.Info(LoadReconcilePolicy.LogTag,
                    $"Untagged rows after the save retired: kind={kind} saveUT={saveText} clockUT={clockText} " +
                    $"retired={retired.ToString(CultureInfo.InvariantCulture)} " +
                    $"seedsKept={seedsKept.ToString(CultureInfo.InvariantCulture)} {tail}");
            }
            else
            {
                ParsekLog.Verbose(LoadReconcilePolicy.LogTag,
                    $"Untagged rows after the save: nothing to retire kind={kind} reason=empty " +
                    $"saveUT={saveText} clockUT={clockText} {tail}");
            }
            return retired;
        }

        private static HashSet<string> CollectFutureOnlyRecordingIds(RecordingTree tree, double cutoffUT)
        {
            HashSet<string> futureOnlyIds = null;
            foreach (KeyValuePair<string, Recording> kvp in tree.Recordings)
            {
                string recordingId = kvp.Key;
                Recording rec = kvp.Value;
                if (rec == null
                    || rec.SidecarLoadFailed
                    || string.Equals(recordingId, tree.ActiveRecordingId, StringComparison.Ordinal)
                    || string.Equals(recordingId, tree.RootRecordingId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (rec.StartUT >= cutoffUT)
                {
                    if (futureOnlyIds == null)
                        futureOnlyIds = new HashSet<string>(StringComparer.Ordinal);
                    futureOnlyIds.Add(recordingId);
                }
            }

            return futureOnlyIds;
        }

        private static int PruneFutureOnlyRecordings(RecordingTree tree, HashSet<string> futureOnlyIds)
        {
            if (tree == null || futureOnlyIds == null || futureOnlyIds.Count == 0)
                return 0;

            int removed = 0;
            foreach (string recordingId in futureOnlyIds)
            {
                if (tree.Recordings.Remove(recordingId))
                    removed++;
            }

            if (removed == 0)
                return 0;

            if (tree.BranchPoints != null)
            {
                for (int i = 0; i < tree.BranchPoints.Count; i++)
                {
                    BranchPoint bp = tree.BranchPoints[i];
                    bp.ParentRecordingIds?.RemoveAll(id => futureOnlyIds.Contains(id));
                    bp.ChildRecordingIds?.RemoveAll(id => futureOnlyIds.Contains(id));
                }
            }

            foreach (Recording rec in tree.Recordings.Values)
            {
                if (rec != null && futureOnlyIds.Contains(rec.ParentRecordingId))
                    rec.ParentRecordingId = null;
            }

            return removed;
        }

        /// <summary>
        /// Removes every branch point with no parent or no child and clears the links to them.
        /// </summary>
        private static int RemoveEmptyBranchPoints(RecordingTree tree)
        {
            if (tree == null || tree.BranchPoints == null || tree.BranchPoints.Count == 0)
                return 0;

            HashSet<string> removedBranchPointIds = null;
            int removed = 0;
            for (int i = tree.BranchPoints.Count - 1; i >= 0; i--)
            {
                BranchPoint bp = tree.BranchPoints[i];
                int parentCount = bp.ParentRecordingIds != null ? bp.ParentRecordingIds.Count : 0;
                int childCount = bp.ChildRecordingIds != null ? bp.ChildRecordingIds.Count : 0;
                if (parentCount > 0 && childCount > 0)
                    continue;

                if (removedBranchPointIds == null)
                    removedBranchPointIds = new HashSet<string>(StringComparer.Ordinal);
                removedBranchPointIds.Add(bp.Id);
                tree.BranchPoints.RemoveAt(i);
                removed++;
            }

            if (removedBranchPointIds == null || removedBranchPointIds.Count == 0)
                return 0;

            foreach (Recording rec in tree.Recordings.Values)
            {
                if (rec == null)
                    continue;

                if (!string.IsNullOrEmpty(rec.ParentBranchPointId)
                    && removedBranchPointIds.Contains(rec.ParentBranchPointId))
                {
                    rec.ParentBranchPointId = null;
                }

                if (!string.IsNullOrEmpty(rec.ChildBranchPointId)
                    && removedBranchPointIds.Contains(rec.ChildBranchPointId))
                {
                    rec.ChildBranchPointId = null;
                }
            }

            return removed;
        }

        private static bool TrimOrbitSegmentsPastUT(List<OrbitSegment> segments, double cutoffUT)
        {
            if (segments == null || segments.Count == 0)
                return false;

            bool mutated = false;
            for (int i = segments.Count - 1; i >= 0; i--)
            {
                OrbitSegment seg = segments[i];
                if (seg.startUT >= cutoffUT)
                {
                    segments.RemoveAt(i);
                    mutated = true;
                    continue;
                }

                if (seg.endUT > cutoffUT)
                {
                    seg.endUT = cutoffUT;
                    segments[i] = seg;
                    mutated = true;
                }
            }

            return mutated;
        }

        private static bool TrimTrackSectionsPastUT(List<TrackSection> sections, double cutoffUT)
        {
            if (sections == null || sections.Count == 0)
                return false;

            bool mutated = false;
            for (int i = sections.Count - 1; i >= 0; i--)
            {
                TrackSection section = sections[i];
                if (section.startUT >= cutoffUT)
                {
                    sections.RemoveAt(i);
                    mutated = true;
                    continue;
                }

                bool sectionMutated = false;
                if (section.endUT > cutoffUT)
                {
                    section.endUT = cutoffUT;
                    sectionMutated = true;
                }

                sectionMutated |= TrimTrackSectionFramesPastUT(ref section, cutoffUT);
                sectionMutated |= TrimTrackSectionBodyFixedFramesPastUT(ref section, cutoffUT);
                sectionMutated |= TrimTrackSectionCheckpointsPastUT(ref section, cutoffUT);

                bool hasFrames = section.frames != null && section.frames.Count > 0;
                bool hasCheckpoints = section.checkpoints != null && section.checkpoints.Count > 0;
                if (!hasFrames && !hasCheckpoints)
                {
                    sections.RemoveAt(i);
                    mutated = true;
                    continue;
                }

                if (sectionMutated)
                {
                    RecomputeTrimmedTrackSectionMetadata(ref section);
                    sections[i] = section;
                    mutated = true;
                }
            }

            return mutated;
        }

        private static bool TrimTrackSectionFramesPastUT(ref TrackSection section, double cutoffUT)
        {
            if (section.frames == null || section.frames.Count == 0)
                return false;

            int originalCount = section.frames.Count;
            for (int i = section.frames.Count - 1; i >= 0; i--)
            {
                if (section.frames[i].ut > cutoffUT)
                    section.frames.RemoveAt(i);
            }

            if (section.frames.Count == 0)
                section.frames = null;

            int remainingCount = section.frames != null ? section.frames.Count : 0;
            return remainingCount != originalCount;
        }

        // The body-fixed surface of a parent-anchored Relative section is sampled alongside
        // `frames` and is the primary playback surface, so it is cut at the same boundary.
        private static bool TrimTrackSectionBodyFixedFramesPastUT(ref TrackSection section, double cutoffUT)
        {
            if (section.bodyFixedFrames == null || section.bodyFixedFrames.Count == 0)
                return false;

            int originalCount = section.bodyFixedFrames.Count;
            for (int i = section.bodyFixedFrames.Count - 1; i >= 0; i--)
            {
                if (section.bodyFixedFrames[i].ut > cutoffUT)
                    section.bodyFixedFrames.RemoveAt(i);
            }

            if (section.bodyFixedFrames.Count == 0)
                section.bodyFixedFrames = null;

            int remainingCount = section.bodyFixedFrames != null ? section.bodyFixedFrames.Count : 0;
            return remainingCount != originalCount;
        }

        private static bool TrimTrackSectionCheckpointsPastUT(ref TrackSection section, double cutoffUT)
        {
            if (section.checkpoints == null || section.checkpoints.Count == 0)
                return false;

            bool mutated = false;
            for (int i = section.checkpoints.Count - 1; i >= 0; i--)
            {
                OrbitSegment checkpoint = section.checkpoints[i];
                if (checkpoint.startUT >= cutoffUT)
                {
                    section.checkpoints.RemoveAt(i);
                    mutated = true;
                    continue;
                }

                if (checkpoint.endUT > cutoffUT)
                {
                    checkpoint.endUT = cutoffUT;
                    section.checkpoints[i] = checkpoint;
                    mutated = true;
                }
            }

            if (section.checkpoints.Count == 0)
                section.checkpoints = null;

            return mutated;
        }

        private static void RecomputeTrimmedTrackSectionMetadata(ref TrackSection section)
        {
            section.sampleRateHz = 0f;
            section.minAltitude = float.NaN;
            section.maxAltitude = float.NaN;

            if (section.frames == null || section.frames.Count == 0)
                return;

            for (int i = 0; i < section.frames.Count; i++)
            {
                float alt = (float)section.frames[i].altitude;
                if (float.IsNaN(section.minAltitude) || alt < section.minAltitude)
                    section.minAltitude = alt;
                if (float.IsNaN(section.maxAltitude) || alt > section.maxAltitude)
                    section.maxAltitude = alt;
            }

            double duration = section.endUT - section.startUT;
            if (duration > 0.0 && section.frames.Count > 1)
                section.sampleRateHz = (float)(section.frames.Count / duration);
        }

        private static bool RemoveItemsPastUT<T>(List<T> items, double cutoffUT, Func<T, double> getUT)
        {
            if (items == null || items.Count == 0)
                return false;

            int originalCount = items.Count;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (getUT(items[i]) > cutoffUT)
                    items.RemoveAt(i);
            }

            return items.Count != originalCount;
        }
    }
}
