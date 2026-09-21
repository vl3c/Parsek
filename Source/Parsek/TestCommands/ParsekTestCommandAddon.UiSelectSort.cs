using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// GUI-census partial: the appliers for <c>UiAction op=sort</c> (a sortable table's
    /// column + direction) and <c>UiAction op=select</c> (the Missions tab's per-vessel and
    /// partner-journey include affordances). Every vocabulary, arg rule and payload shape
    /// lives in the pure sibling <see cref="TestCommandUiSelectSort"/>; this file writes the
    /// live fields, calls the live production helpers, and reads back.
    ///
    /// <para>THE SAME ARGUMENT AS THE REST OF <c>UiAction</c>: each write below is the whole
    /// body of a <c>GUILayout</c> header click or checkbox handler - the two sort fields plus
    /// that window's own cache invalidation, or
    /// <c>MissionVesselRowBuilder.ApplyVesselInclusion</c> plus the selection stamp. No
    /// synthesised input, no new player-facing surface.</para>
    ///
    /// <para><b><c>op=select</c> EDITS THE SAVE.</b> <c>Mission.ExcludedIntervalKeys</c> and
    /// <c>Mission.IncludedForeignDockLinkIds</c> are persisted by the Mission codec, so a
    /// lane that drives this op leaves the edit in the career it ran on. Such a lane runs on
    /// a THROWAWAY STAGED SAVE - a lane rule, not one this op can enforce (see the pure
    /// sibling's header). Every other op in this family writes view state that dies with the
    /// process.</para>
    ///
    /// <para>WHICH PRODUCTION TAIL IS REPRODUCED AND WHICH IS NOT. Including a partner
    /// journey on an ALREADY-LOOPING mission widens its spanned tree set, so the click calls
    /// <c>MissionStore.ClearLoopsConflictingWith</c> right after the membership write; without
    /// that tail a lane could leave two conflicting loops armed, so the applier runs it with
    /// its own reason string. The click's other tail - <c>AnnounceClearedLoops</c>, an
    /// on-screen message - is deliberately NOT reproduced: a screen message is for a player
    /// who clicked, and an unattended census has nobody to tell (and it would land in a
    /// capture). The cleared counts go on the log line instead.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // ================================================================ op=sort

        private void UiActionSortOp(ParsedCommand cmd, ParsekUI ui, UiWindowSpec spec)
        {
            // The live tab, read through the ONE resolver the state op uses, because on the
            // Missions window it SCOPES the column vocabulary: both tabs draw an index and a
            // name column, so a bare token cannot say which table it meant.
            string tabToken = ResolveLiveTabToken(ui, spec);

            string rawColumn = ArgOrNull(cmd, TestCommandUiSelectSort.ColumnArg);
            if (!TestCommandUiSelectSort.TryResolveColumn(
                    spec.Name, tabToken, rawColumn, out int columnIndex,
                    out string columnReject))
            {
                string detail;
                if (columnReject == TestCommandUiSelectSort.SortUnsupportedWindowReason)
                {
                    detail = $"{columnReject} window={spec.Name} "
                        + $"valid={TestCommandUiSelectSort.SortableWindowNames}";
                }
                else if (columnReject == TestCommandUiSelectSort.SortColumnInvalidReason
                         || columnReject
                            == TestCommandUiSelectSort.SortColumnNotOnTabReason)
                {
                    detail = $"{columnReject} window={spec.Name} "
                        + $"tab={TestCommandUiSelectSort.TabField(tabToken)} "
                        + $"column={rawColumn ?? string.Empty} "
                        + $"columns={TestCommandUiSelectSort.SortColumnNamesFor(spec.Name, tabToken)}";
                }
                else
                {
                    detail = $"{columnReject} window={spec.Name} "
                        + $"tab={TestCommandUiSelectSort.TabField(tabToken)}";
                }
                ParsekLog.Warn(Tag, $"uiaction rejected reason={columnReject} "
                    + $"window={spec.Name} tab={TestCommandUiSelectSort.TabField(tabToken)} "
                    + $"column={rawColumn ?? string.Empty}");
                SetExecResult("REJECTED", null, detail);
                return;
            }

            string rawDir = ArgOrNull(cmd, TestCommandUiSelectSort.DirArg);
            if (!TestCommandUiSelectSort.TryParseDirection(
                    rawDir, out bool ascending, out string dirReject))
            {
                ParsekLog.Warn(Tag, $"uiaction rejected reason={dirReject} "
                    + $"window={spec.Name} dir={rawDir ?? string.Empty}");
                SetExecResult("REJECTED", null,
                    $"{dirReject} window={spec.Name} dir={rawDir ?? string.Empty} "
                    + $"valid={TestCommandUiSelectSort.DirTokenNames}");
                return;
            }

            UiSortHandle handle = ResolveSortHandle(ui, spec.Name, tabToken);
            int beforeColumn = handle.GetColumn();
            bool beforeAscending = handle.GetAscending();
            // Both fields go through the window's OWN accessors, which each carry that
            // window's cache invalidation: the Recordings tab's calls InvalidateSort(), the
            // Logistics one clears both section row counts, and the Missions tab and Spawn
            // Control re-sort from the live tuple with no cache to clear. Without the
            // invalidation a capture would photograph the OLD row order under the new arrow -
            // the one failure mode a sort op has.
            if (beforeColumn != columnIndex) handle.SetColumn(columnIndex);
            if (beforeAscending != ascending) handle.SetAscending(ascending);

            bool changed = beforeColumn != columnIndex || beforeAscending != ascending;

            uiActionPending = new UiActionPending
            {
                Op = UiActionOp.Sort,
                Window = spec.Name,
                StartFrame = Time.frameCount,
                SortColumn = rawColumn,
                SortAscending = ascending,
                SortTab = tabToken,
                SortChanged = changed,
            };
            ParsekLog.Info(Tag, $"uiaction sort initiated window={spec.Name} "
                + $"tab={TestCommandUiSelectSort.TabField(tabToken)} column={rawColumn} "
                + $"index={Int(columnIndex)} "
                + $"dir={TestCommandUiSelectSort.DirToken(ascending)} "
                + $"changed={Bool(changed)} (awaiting one drawn frame)");
            SetExecResult(PendingVerdict, null, null);
        }

        private void CompleteUiActionSort(UiActionSettleContext ctx, UiActionPending pending)
        {
            // The read-back is taken after a drawn frame for the `expand` reason: the write
            // changes DRAWN state, so only a frame that ran makes the pair a statement about
            // the game. The one thing that could disagree is a window re-clamping its own
            // sort tuple during the draw, which nothing does today - so a disagreement here
            // would be a genuine finding.
            // The index is re-derived from the stored TOKEN rather than carried, because the
            // pending struct's sort fields are the wire values. Re-resolved against the tab
            // the op WROTE to, not the live one: that is the table the write landed in, and
            // nothing an unattended run does can move the tab while the FIFO head is held.
            // A failure is therefore unreachable (both halves were validated pre-write) and
            // means the pure column table changed under us - loud rather than silent, the
            // throwing-default rule.
            if (!TestCommandUiSelectSort.TryResolveColumn(
                    pending.Window, pending.SortTab, pending.SortColumn,
                    out int wantIndex, out string reResolveReject))
            {
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiSelectSort.SortNotAppliedReason
                    + $" window={pending.Window} column={pending.SortColumn} "
                    + $"tab={TestCommandUiSelectSort.TabField(pending.SortTab)} "
                    + $"(column no longer resolves: {reResolveReject})");
                EmitExecutedTerminal(ctx.Id, ctx.Seq, ctx.Verb, "ERROR", null,
                    $"{TestCommandUiSelectSort.SortNotAppliedReason} "
                    + $"window={pending.Window} column={pending.SortColumn}",
                    dequeueHead: true);
                return;
            }

            UiSortHandle handle = ResolveSortHandle(ctx.Ui, pending.Window, pending.SortTab);
            int afterColumn = handle.GetColumn();
            bool afterAscending = handle.GetAscending();
            if (afterColumn != wantIndex || afterAscending != pending.SortAscending)
            {
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiSelectSort.SortNotAppliedReason
                    + $" window={pending.Window} column={pending.SortColumn} "
                    + $"want={Int(wantIndex)}/{TestCommandUiSelectSort.DirToken(pending.SortAscending)} "
                    + $"after={Int(afterColumn)}/{TestCommandUiSelectSort.DirToken(afterAscending)} "
                    + $"frames={Int(ctx.Frames)}");
                EmitExecutedTerminal(ctx.Id, ctx.Seq, ctx.Verb, "ERROR", null,
                    $"{TestCommandUiSelectSort.SortNotAppliedReason} "
                    + $"window={pending.Window} column={pending.SortColumn} "
                    + $"dir={TestCommandUiSelectSort.DirToken(pending.SortAscending)}",
                    dequeueHead: true);
                return;
            }

            ParsekLog.Info(Tag, $"uiaction sort window={pending.Window} "
                + $"tab={TestCommandUiSelectSort.TabField(pending.SortTab)} "
                + $"column={pending.SortColumn} "
                + $"dir={TestCommandUiSelectSort.DirToken(pending.SortAscending)} "
                + $"changed={Bool(pending.SortChanged)} frames={Int(ctx.Frames)}");
            EmitExecutedTerminal(ctx.Id, ctx.Seq, ctx.Verb, "OK",
                TestCommandUiSelectSort.BuildSortPayload(
                    pending.Window, pending.SortTab, pending.SortColumn,
                    pending.SortAscending, pending.SortChanged),
                null, dequeueHead: true);
        }

        /// <summary>One sortable table's live sort pair. A row per table for the reason
        /// <see cref="UiWindowHandle"/> is a row per window: a mis-wired table is then one
        /// site rather than one arm of four parallel switches that a headless suite cannot
        /// witness.</summary>
        private struct UiSortHandle
        {
            internal Func<int> GetColumn;
            internal Action<int> SetColumn;
            internal Func<bool> GetAscending;
            internal Action<bool> SetAscending;
        }

        private static UiSortHandle ResolveSortHandle(ParsekUI ui, string window,
                                                      string tabToken)
        {
            if (window == TestCommandUiAction.MissionsWindow)
            {
                // ONE window, TWO tables. The wire token is the window's, and the live tab is
                // what says which of its two sort states the op drives - the same fact that
                // scopes the column vocabulary.
                if (string.Equals(tabToken, TestCommandUiSelectSort.RecordingsTabToken,
                                  StringComparison.Ordinal))
                {
                    RecordingsTableUI rt = ui.GetRecordingsTableUI();
                    return new UiSortHandle
                    {
                        GetColumn = () => rt.SortColumnIndexForTesting,
                        SetColumn = i => rt.SortColumnIndexForTesting = i,
                        GetAscending = () => rt.SortAscendingForTesting,
                        SetAscending = v => rt.SortAscendingForTesting = v,
                    };
                }
                MissionsWindowUI mw = ui.GetMissionsUI();
                return new UiSortHandle
                {
                    GetColumn = () => mw.SortColumnIndexForTesting,
                    SetColumn = i => mw.SortColumnIndexForTesting = i,
                    GetAscending = () => mw.SortAscendingForTesting,
                    SetAscending = v => mw.SortAscendingForTesting = v,
                };
            }
            if (window == TestCommandUiAction.LogisticsWindow)
            {
                LogisticsWindowUI lw = ui.GetLogisticsUI();
                return new UiSortHandle
                {
                    GetColumn = () => lw.RouteSortColumnIndexForTesting,
                    SetColumn = i => lw.RouteSortColumnIndexForTesting = i,
                    GetAscending = () => lw.RouteSortAscendingForTesting,
                    SetAscending = v => lw.RouteSortAscendingForTesting = v,
                };
            }
            if (window == TestCommandUiAction.SpawnControlWindow)
            {
                SpawnControlUI sc = ui.GetSpawnControlUI();
                return new UiSortHandle
                {
                    GetColumn = () => sc.SortColumnIndexForTesting,
                    SetColumn = i => sc.SortColumnIndexForTesting = i,
                    GetAscending = () => sc.SortAscendingForTesting,
                    SetAscending = v => sc.SortAscendingForTesting = v,
                };
            }
            // Unreachable through the parse (TryResolveColumn rejected first), so a miss here
            // means the pure window predicate and this resolver have drifted.
            throw new InvalidOperationException(
                "no sortable table for op=sort window=" + (window ?? "<null>"));
        }

        // ================================================================ op=select

        private void UiActionSelectOp(ParsedCommand cmd, ParsekUI ui, UiWindowSpec spec)
        {
            string rawInclude = ArgOrNull(cmd, TestCommandUiSelectSort.IncludeArg);
            string rawKey = ArgOrNull(cmd, TestCommandUiState.KeyArg);
            if (!TestCommandUiSelectSort.TryParseSelectKey(
                    spec.Name, rawKey, includeGiven: rawInclude != null,
                    scope: out UiSelectScope scope, prefix: out string prefix,
                    value: out string value, rejectReason: out string keyReject))
            {
                string detail;
                if (keyReject == TestCommandUiSelectSort.SelectUnsupportedWindowReason)
                {
                    detail = $"{keyReject} window={spec.Name} "
                        + $"valid={TestCommandUiSelectSort.SelectableWindowNames}";
                }
                else if (keyReject == TestCommandUiSelectSort.SelectKeyInvalidReason)
                {
                    detail = $"{keyReject} window={spec.Name} "
                        + $"key={rawKey ?? string.Empty} "
                        + $"prefixes={TestCommandUiSelectSort.SelectPrefixNames}";
                }
                else
                {
                    detail = $"{keyReject} window={spec.Name} key={rawKey ?? string.Empty}";
                }
                ParsekLog.Warn(Tag, $"uiaction rejected reason={keyReject} "
                    + $"window={spec.Name} key={rawKey ?? string.Empty}");
                SetExecResult("REJECTED", null, detail);
                return;
            }

            bool include;
            if (scope == UiSelectScope.Single)
            {
                if (!TestCommandUiSelectSort.TryParseInclude(
                        rawInclude, out include, out string includeReject))
                {
                    ParsekLog.Warn(Tag, $"uiaction rejected reason={includeReject} "
                        + $"window={spec.Name} include={rawInclude ?? string.Empty}");
                    SetExecResult("REJECTED", null,
                        $"{includeReject} window={spec.Name} "
                        + $"include={rawInclude ?? string.Empty}");
                    return;
                }
            }
            else
            {
                include = TestCommandUiSelectSort.BulkScopeIncludes(scope);
            }

            string requestedMission = ArgOrNull(cmd, TestCommandUiState.MissionArg);
            if (!TryResolveSelectMission(requestedMission, out Mission mission,
                                         out RecordingTree tree))
            {
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiSelectSort.SelectNoMissionReason
                    + $" window={spec.Name} missions={Int(MissionStore.Missions.Count)}");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiSelectSort.SelectNoMissionReason} "
                    + $"missions={Int(MissionStore.Missions.Count)} "
                    + $"trees={Int(RecordingStore.CommittedTrees.Count)}");
                return;
            }

            MissionsWindowUI missionsUi = ui.GetMissionsUI();
            List<MissionVesselRow> rows = missionsUi.VesselRowsForTesting(tree);
            List<ForeignDockLink> links = missionsUi.ForeignDockLinksForTesting(
                tree, RecordingStore.CommittedTrees);

            int changed;
            if (scope != UiSelectScope.Single)
            {
                changed = ApplyBulkSelection(mission, rows, links, include);
            }
            else if (prefix == TestCommandUiSelectSort.VesselKeyPrefix)
            {
                MissionVesselRow row = FindVesselRow(rows, value);
                if (row == null)
                {
                    RejectSelectKeyUnknown(rawKey, CollectVesselHeadIds(rows));
                    return;
                }
                changed = ApplyVesselSelection(mission, row, include);
            }
            else
            {
                if (!LinkExists(links, value))
                {
                    RejectSelectKeyUnknown(rawKey, CollectLinkIds(links));
                    return;
                }
                changed = ApplyLinkSelection(mission, value, include);
            }

            uiActionPending = new UiActionPending
            {
                Op = UiActionOp.Select,
                Window = spec.Name,
                StartFrame = Time.frameCount,
                SelectKey = rawKey,
                SelectInclude = include,
                SelectChanged = changed,
                SelectMissionId = mission.Id,
            };
            ParsekLog.Info(Tag, $"uiaction select initiated window={spec.Name} "
                + $"mission={mission.Id} key={rawKey} "
                + $"include={TestCommandUiSelectSort.IncludeToken(include)} "
                + $"changed={Int(changed)} "
                + $"excluded={Int(mission.ExcludedIntervalKeys.Count)} "
                + $"links={Int(mission.IncludedForeignDockLinkIds.Count)} "
                + "(awaiting one drawn frame)");
            SetExecResult(PendingVerdict, null, null);
        }

        private void RejectSelectKeyUnknown(string rawKey, List<string> known)
        {
            ParsekLog.Warn(Tag, "uiaction rejected reason="
                + TestCommandUiSelectSort.SelectKeyUnknownReason
                + $" key={rawKey ?? string.Empty} known={Int(known.Count)}");
            SetExecResult("REJECTED", null,
                $"{TestCommandUiSelectSort.SelectKeyUnknownReason} "
                + $"key={rawKey ?? string.Empty} known={Int(known.Count)} "
                + $"exist={TestCommandUiState.FormatCandidates(known, TestCommandUiState.CandidateListCap)}");
        }

        private void CompleteUiActionSelect(UiActionSettleContext ctx,
                                            UiActionPending pending)
        {
            Mission mission = FindMissionById(pending.SelectMissionId);
            if (mission == null)
            {
                // The mission was removed between the write and the settle (a tree deletion
                // reaper pass). ERROR rather than OK over counts nobody owns any more.
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiSelectSort.SelectNotAppliedReason
                    + $" mission={pending.SelectMissionId} (mission gone by settle)");
                EmitExecutedTerminal(ctx.Id, ctx.Seq, ctx.Verb, "ERROR", null,
                    $"{TestCommandUiSelectSort.SelectNotAppliedReason} "
                    + $"mission={pending.SelectMissionId}",
                    dequeueHead: true);
                return;
            }

            int excluded = mission.ExcludedIntervalKeys.Count;
            int links = mission.IncludedForeignDockLinkIds.Count;

            if (!VerifySettledSelection(ctx, mission, pending, excluded, links))
                return;

            ParsekLog.Info(Tag, $"uiaction select window={pending.Window} "
                + $"mission={mission.Id} key={pending.SelectKey} "
                + $"include={TestCommandUiSelectSort.IncludeToken(pending.SelectInclude)} "
                + $"changed={Int(pending.SelectChanged)} excluded={Int(excluded)} "
                + $"links={Int(links)} frames={Int(ctx.Frames)}");
            EmitExecutedTerminal(ctx.Id, ctx.Seq, ctx.Verb, "OK",
                TestCommandUiSelectSort.BuildSelectPayload(
                    pending.Window, mission.Id, pending.SelectKey, pending.SelectInclude,
                    pending.SelectChanged, excluded, links),
                null, dequeueHead: true);
        }

        /// <summary>
        /// The post-settle check for a SINGLE key: the named vessel's classified inclusion,
        /// or the named link's membership, must agree with the request. Returns false when it
        /// has already emitted the ERROR terminal.
        ///
        /// <para>A BULK step is report-only by design: <c>key=all</c> over a mission with a
        /// partial hand-authored selection legitimately lands on a mixture of per-row
        /// outcomes, and its honest report is the counts.</para>
        /// </summary>
        private bool VerifySettledSelection(UiActionSettleContext ctx, Mission mission,
                                            UiActionPending pending, int excluded, int links)
        {
            if (!TestCommandUiSelectSort.TryParseSelectKey(
                    pending.Window, pending.SelectKey, includeGiven: false,
                    scope: out UiSelectScope scope, prefix: out string prefix,
                    value: out string value, rejectReason: out string _))
                return true;
            if (scope != UiSelectScope.Single) return true;

            bool agrees;
            string after;
            if (prefix == TestCommandUiSelectSort.VesselKeyPrefix)
            {
                RecordingTree tree = FindCommittedTree(mission.TreeId);
                MissionVesselRow row = tree == null
                    ? null
                    : FindVesselRow(ctx.Ui.GetMissionsUI().VesselRowsForTesting(tree), value);
                // A row that no longer resolves, or one with NO interval keys of its own, is
                // report-only rather than an ERROR. The first is a tree that changed under
                // the frame; the second is a real shape - ClassifyInclusion answers `All` for
                // an empty interval list and ApplyVesselInclusion writes nothing, so an
                // `include=false` against it would read as a failure to follow when there was
                // nothing to write.
                if (row == null
                    || MissionVesselRowBuilder.IntervalKeys(row).Count == 0)
                    return true;
                MissionVesselInclusion inclusion =
                    MissionVesselRowBuilder.ClassifyInclusion(
                        row, mission.ExcludedIntervalKeys);
                MissionVesselInclusion want = pending.SelectInclude
                    ? MissionVesselInclusion.All
                    : MissionVesselInclusion.None;
                agrees = inclusion == want;
                after = inclusion.ToString();
            }
            else
            {
                bool member = mission.IncludedForeignDockLinkIds.Contains(value);
                agrees = member == pending.SelectInclude;
                after = TestCommandUiSelectSort.IncludeToken(member);
            }

            if (agrees) return true;

            ParsekLog.Error(Tag, "uiaction error reason="
                + TestCommandUiSelectSort.SelectNotAppliedReason
                + $" mission={mission.Id} key={pending.SelectKey} "
                + $"want={TestCommandUiSelectSort.IncludeToken(pending.SelectInclude)} "
                + $"after={after} excluded={Int(excluded)} links={Int(links)} "
                + $"frames={Int(ctx.Frames)}");
            EmitExecutedTerminal(ctx.Id, ctx.Seq, ctx.Verb, "ERROR", null,
                $"{TestCommandUiSelectSort.SelectNotAppliedReason} "
                + $"mission={mission.Id} key={pending.SelectKey} after={after}",
                dequeueHead: true);
            return false;
        }

        // ----- the live writes, one production path per affordance -----

        /// <summary>
        /// The mission <c>op=select</c> drives: the FIRST <c>MissionStore</c> entry whose
        /// tree is committed.
        ///
        /// <para>"The first" is the honest selector, the <c>recording=first</c> rationale: a
        /// Mission id is save-specific, so a COMMITTED spec cannot name one, and the include
        /// affordance looks and behaves the same over any mission - so naming the first and
        /// REPORTING its id in the payload is the only form a committed lane can write. The
        /// committed-tree gate is not cosmetic: a mission over an uncommitted tree draws no
        /// vessel rows at all, so its row list would be empty and every key a
        /// <c>select-key-unknown</c>.</para>
        /// </summary>
        /// <param name="requestedId">An explicit <c>mission=</c>, or null for the
        /// default. NAMED, a lane gets exactly that mission or a typed refusal; ABSENT, the
        /// first mission over a committed tree, which is the only form a COMMITTED spec can
        /// write (a Mission id is save-specific). The selector exists because a dense
        /// fixture has several missions and "the first" is then a coin toss the lane cannot
        /// see; the default is unchanged, so every step written before it behaves
        /// identically.</param>
        private static bool TryResolveSelectMission(string requestedId,
                                                    out Mission mission,
                                                    out RecordingTree tree)
        {
            mission = null;
            tree = null;
            if (!string.IsNullOrEmpty(requestedId))
            {
                Mission named = MissionStore.FindById(requestedId);
                if (named == null) return false;
                RecordingTree namedTree = FindCommittedTree(named.TreeId);
                // A mission over an UNCOMMITTED tree draws no vessel rows, so every key
                // would answer select-key-unknown: refused here, where the message can say
                // which mission was named, rather than there.
                if (namedTree == null) return false;
                mission = named;
                tree = namedTree;
                return true;
            }
            IReadOnlyList<Mission> missions = MissionStore.Missions;
            for (int i = 0; i < missions.Count; i++)
            {
                Mission m = missions[i];
                if (m == null) continue;
                RecordingTree t = FindCommittedTree(m.TreeId);
                if (t == null) continue;
                mission = m;
                tree = t;
                return true;
            }
            return false;
        }

        private static RecordingTree FindCommittedTree(string treeId)
        {
            if (string.IsNullOrEmpty(treeId)) return null;
            List<RecordingTree> trees = RecordingStore.CommittedTrees;
            for (int i = 0; i < trees.Count; i++)
                if (trees[i] != null
                    && string.Equals(trees[i].Id, treeId, StringComparison.Ordinal))
                    return trees[i];
            return null;
        }

        private static Mission FindMissionById(string missionId)
            => MissionStore.FindById(missionId);

        /// <summary>
        /// Resolves a <c>vessel:</c> value against the live rows in TWO rungs: the row's own
        /// <c>OwnerHeadId</c>, then any one of that row's interval keys. Both exist because a
        /// lane may hold either - a head id from a handle listing, or an interval key read
        /// off an <c>op=expand</c> answer - and both name exactly one row.
        /// </summary>
        private static MissionVesselRow FindVesselRow(List<MissionVesselRow> rows,
                                                      string value)
        {
            MissionVesselRow byHead = FindVesselRowByHead(rows, value);
            return byHead ?? FindVesselRowByIntervalKey(rows, value);
        }

        private static MissionVesselRow FindVesselRowByHead(List<MissionVesselRow> rows,
                                                            string value)
        {
            if (rows == null) return null;
            for (int i = 0; i < rows.Count; i++)
            {
                MissionVesselRow row = rows[i];
                if (row == null) continue;
                if (string.Equals(row.OwnerHeadId, value, StringComparison.Ordinal))
                    return row;
                MissionVesselRow child = FindVesselRowByHead(row.Children, value);
                if (child != null) return child;
            }
            return null;
        }

        private static MissionVesselRow FindVesselRowByIntervalKey(
            List<MissionVesselRow> rows, string value)
        {
            if (rows == null) return null;
            for (int i = 0; i < rows.Count; i++)
            {
                MissionVesselRow row = rows[i];
                if (row == null) continue;
                List<string> keys = MissionVesselRowBuilder.IntervalKeys(row);
                for (int k = 0; k < keys.Count; k++)
                    if (string.Equals(keys[k], value, StringComparison.Ordinal))
                        return row;
                MissionVesselRow child = FindVesselRowByIntervalKey(row.Children, value);
                if (child != null) return child;
            }
            return null;
        }

        private static List<string> CollectVesselHeadIds(List<MissionVesselRow> rows)
        {
            var ids = new List<string>();
            CollectVesselHeadIdsInto(rows, ids);
            return ids;
        }

        private static void CollectVesselHeadIdsInto(List<MissionVesselRow> rows,
                                                     List<string> into)
        {
            if (rows == null) return;
            for (int i = 0; i < rows.Count; i++)
            {
                MissionVesselRow row = rows[i];
                if (row == null) continue;
                if (!string.IsNullOrEmpty(row.OwnerHeadId)) into.Add(row.OwnerHeadId);
                CollectVesselHeadIdsInto(row.Children, into);
            }
        }

        private static List<string> CollectLinkIds(List<ForeignDockLink> links)
        {
            var ids = new List<string>();
            if (links == null) return ids;
            for (int i = 0; i < links.Count; i++)
                if (links[i] != null && !string.IsNullOrEmpty(links[i].LinkId))
                    ids.Add(links[i].LinkId);
            return ids;
        }

        private static bool LinkExists(List<ForeignDockLink> links, string linkId)
        {
            if (links == null) return false;
            for (int i = 0; i < links.Count; i++)
                if (links[i] != null
                    && string.Equals(links[i].LinkId, linkId, StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>
        /// The per-vessel include, through the production path the checkbox calls:
        /// <c>ApplyVesselInclusion</c> over the vessel's OWN explicit interval keys (never a
        /// child vessel's - the non-cascading contract is untouched), then the selection
        /// stamp, and only when something changed, exactly as the click does.
        ///
        /// <para>ONE DELIBERATE DIFFERENCE FROM THE CLICK: the click has no direction of its
        /// own - it reads the row's current classification and flips it - while this op takes
        /// <c>include=</c> and is therefore IDEMPOTENT. That is what a lane needs (a step
        /// that says "include this vessel" must mean it whatever the row was in), and it is
        /// why the payload carries <c>changed=</c>.</para>
        /// </summary>
        private static int ApplyVesselSelection(Mission mission, MissionVesselRow row,
                                                bool include)
        {
            int changed = MissionVesselRowBuilder.ApplyVesselInclusion(
                row, include, mission.ExcludedIntervalKeys);
            if (changed > 0) MissionsWindowUI.StampSelectionEditForTesting(mission);
            ParsekLog.Info(Tag, $"uiaction select vessel mission={mission.Id} "
                + $"head={row.OwnerHeadId} vessel={row.VesselName} "
                + $"include={TestCommandUiSelectSort.IncludeToken(include)} "
                + $"keysChanged={Int(changed)}");
            return changed;
        }

        /// <summary>The partner-journey include, through the production membership write plus
        /// its conflicting-loop tail (see the file header for what is and is not
        /// reproduced).</summary>
        private static int ApplyLinkSelection(Mission mission, string linkId, bool include)
        {
            if (!ApplyLinkMembership(mission, linkId, include)) return 0;
            ClearLoopsConflictingWithAfterInclude(mission, include, 1);
            return 1;
        }

        private static bool ApplyLinkMembership(Mission mission, string linkId, bool include)
        {
            bool was = mission.IncludedForeignDockLinkIds.Contains(linkId);
            if (was == include) return false;
            if (include) mission.IncludedForeignDockLinkIds.Add(linkId);
            else mission.IncludedForeignDockLinkIds.Remove(linkId);
            ParsekLog.Info(Tag, $"uiaction select link mission={mission.Id} link={linkId} "
                + $"include={TestCommandUiSelectSort.IncludeToken(include)}");
            return true;
        }

        /// <summary>
        /// Including a partner journey on an ALREADY-LOOPING mission widens its spanned tree
        /// set, which can newly conflict with a loop on the foreign tree; the production
        /// click clears that conflict right here, and skipping it would let a lane leave two
        /// conflicting loops armed. Only on the INCLUDE direction and only when a membership
        /// actually moved, both as the click gates it.
        /// </summary>
        private static void ClearLoopsConflictingWithAfterInclude(Mission mission,
                                                                  bool include, int moved)
        {
            if (moved <= 0 || !include || !mission.LoopPlayback) return;
            MissionStore.ClearLoopsConflictingWith(
                mission, RecordingStore.CommittedTrees,
                out int clearedSameTree, out int clearedCrossTree,
                "SeamPartnerJourneyInclude");
            ParsekLog.Info(Tag, $"uiaction select loops cleared mission={mission.Id} "
                + $"sameTree={Int(clearedSameTree)} crossTree={Int(clearedCrossTree)}");
        }

        /// <summary>
        /// <c>key=all</c> / <c>key=none</c>: every vessel row (including separated children)
        /// and every derived partner journey of the resolved mission.
        ///
        /// <para>This is the affordance a census actually needs - "exclude everything", so
        /// the greyed-row form of the tab has a picture - and the ids it would otherwise have
        /// to name are save-specific, so a committed spec could not carry them.</para>
        /// </summary>
        private static int ApplyBulkSelection(Mission mission, List<MissionVesselRow> rows,
                                              List<ForeignDockLink> links, bool include)
        {
            int changed = ApplyBulkVesselSelection(mission, rows, include);
            if (changed > 0) MissionsWindowUI.StampSelectionEditForTesting(mission);

            int linkChanged = 0;
            if (links != null)
            {
                for (int i = 0; i < links.Count; i++)
                {
                    ForeignDockLink link = links[i];
                    if (link == null || string.IsNullOrEmpty(link.LinkId)) continue;
                    if (ApplyLinkMembership(mission, link.LinkId, include)) linkChanged++;
                }
            }
            // ONE clear for the whole bulk step rather than one per link: the call is over the
            // mission's spanned set, which every included link has already widened by now, so
            // running it per link would repeat identical work and log it N times.
            ClearLoopsConflictingWithAfterInclude(mission, include, linkChanged);

            ParsekLog.Info(Tag, $"uiaction select bulk mission={mission.Id} "
                + $"include={TestCommandUiSelectSort.IncludeToken(include)} "
                + $"keysChanged={Int(changed)} linksChanged={Int(linkChanged)}");
            return changed + linkChanged;
        }

        private static int ApplyBulkVesselSelection(Mission mission,
                                                    List<MissionVesselRow> rows,
                                                    bool include)
        {
            if (rows == null) return 0;
            int changed = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                MissionVesselRow row = rows[i];
                if (row == null) continue;
                // Per ROW rather than one flat key sweep, because ApplyVesselInclusion is the
                // production writer and it is row-scoped; walking the children explicitly is
                // what keeps "every vessel" honest without teaching this op the cascade the
                // interval-key contract forbids.
                changed += MissionVesselRowBuilder.ApplyVesselInclusion(
                    row, include, mission.ExcludedIntervalKeys);
                changed += ApplyBulkVesselSelection(mission, row.Children, include);
            }
            return changed;
        }
    }
}
