using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// GUI-census partial: the appliers for the three STATE ops - <c>expand</c>,
    /// <c>target</c> and <c>picker</c>. The key grammar, the selector rules and the payload
    /// shapes live in the pure sibling <see cref="TestCommandUiState"/>; this file writes
    /// the live collections and calls the live openers, and nothing else.
    ///
    /// <para>
    /// THE SAME ARGUMENT AS THE REST OF <c>UiAction</c>: each of these surfaces is gated by
    /// a plain field or reached by a plain method whose only other caller is a
    /// <c>GUILayout.Button</c> handler, so the seam does the same write and reads it back.
    /// No synthesised input, no new player-facing surface.
    /// </para>
    ///
    /// <para>
    /// WHERE THE KEY LISTS COME FROM. Each window class enumerates its OWN keys
    /// (<c>EnumerateGroupNamesForTesting</c> and friends), because only the class knows
    /// which collection is keyed by what - and because the enumeration has to agree with
    /// the tree the player sees. The seam composes the wire key from a prefix and the raw
    /// value; a key naming something the live window does not have is a REJECTED that lists
    /// what it does have, so a spec author reads the answer instead of the source.
    /// </para>
    ///
    /// <para>
    /// WHY <c>op=target</c> EXISTS ALONGSIDE <c>op=open</c>. <c>op=open window=structure</c>
    /// raises <c>IsOpen</c> and nothing else, so the window draws the "Nothing to show."
    /// chrome: the census's <c>ksc-structure-advanced</c> label is a picture of an empty
    /// box. The populated forms are built by <c>OpenForMission</c> / <c>OpenForRoute</c>,
    /// which set the target AND rebuild before raising the flag. So the op calls those, and
    /// reports the row count they produced.
    /// </para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // ================================================================ op=expand

        private void UiActionExpandOp(ParsedCommand cmd, ParsekUI ui, UiWindowSpec spec)
        {
            if (!TestCommandUiState.TryParseState(
                    ArgOrNull(cmd, TestCommandUiState.StateArg), out bool wantExpanded,
                    out string stateReject))
            {
                string raw = ArgOrNull(cmd, TestCommandUiState.StateArg) ?? string.Empty;
                ParsekLog.Warn(Tag, $"uiaction rejected reason={stateReject} state={raw}");
                SetExecResult("REJECTED", null, $"{stateReject} state={raw}");
                return;
            }

            string rawKey = ArgOrNull(cmd, TestCommandUiState.KeyArg);
            if (!TestCommandUiState.TryParseExpandKey(
                    spec.Name, rawKey,
                    stateGiven: ArgOrNull(cmd, TestCommandUiState.StateArg) != null,
                    scope: out UiExpandScope scope, prefix: out string prefix,
                    value: out string value, rejectReason: out string keyReject))
            {
                string detail = keyReject == TestCommandUiState.ExpandUnsupportedWindowReason
                    ? $"{keyReject} window={spec.Name} "
                      + $"valid={TestCommandUiState.ExpandableWindowNames}"
                    : keyReject == TestCommandUiState.ExpandKeyInvalidReason
                        ? $"{keyReject} window={spec.Name} key={rawKey ?? string.Empty} "
                          + $"prefixes={TestCommandUiState.ExpandPrefixNamesFor(spec.Name)}"
                        : $"{keyReject} window={spec.Name}";
                ParsekLog.Warn(Tag, $"uiaction rejected reason={keyReject} "
                    + $"window={spec.Name} key={rawKey ?? string.Empty}");
                SetExecResult("REJECTED", null, detail);
                return;
            }

            List<UiExpandSet> sets = ResolveExpandSets(ui, spec.Name);
            int changed = 0;

            if (scope == UiExpandScope.Single)
            {
                UiExpandSet set = null;
                for (int i = 0; i < sets.Count; i++)
                    if (sets[i].Prefix == prefix) { set = sets[i]; break; }
                // Unreachable through the parse (the prefix came from that window's own
                // list), so a null here means the resolver and the pure prefix table have
                // drifted - loud rather than silent.
                if (set == null)
                {
                    ParsekLog.Error(Tag, "uiaction error reason="
                        + TestCommandUiAction.ThrewReason
                        + $" window={spec.Name} prefix={prefix} has no live set");
                    SetExecResult("ERROR", null, TestCommandUiAction.ThrewReason);
                    return;
                }
                List<string> known = set.Enumerate();
                if (!known.Contains(value))
                {
                    ParsekLog.Warn(Tag, "uiaction rejected reason="
                        + TestCommandUiState.ExpandKeyUnknownReason
                        + $" window={spec.Name} key={rawKey} known={Int(known.Count)}");
                    SetExecResult("REJECTED", null,
                        $"{TestCommandUiState.ExpandKeyUnknownReason} key={rawKey} "
                        + $"known={Int(known.Count)} "
                        + $"exist={TestCommandUiState.FormatCandidates(known, TestCommandUiState.CandidateListCap)}");
                    return;
                }
                if (set.Set(value, wantExpanded)) changed++;
            }
            else
            {
                // key=all / key=none: every key the window can enumerate, across every one
                // of its sets. This is the affordance a census actually needs - "open every
                // group folder" - and the ids it would otherwise have to name are
                // save-specific, so a committed spec could not carry them.
                wantExpanded = scope == UiExpandScope.All;
                for (int i = 0; i < sets.Count; i++)
                {
                    List<string> known = sets[i].Enumerate();
                    for (int k = 0; k < known.Count; k++)
                        if (sets[i].Set(known[k], wantExpanded)) changed++;
                }
            }

            int expandedCount = 0;
            int total = 0;
            for (int i = 0; i < sets.Count; i++)
            {
                expandedCount += sets[i].Count();
                total += sets[i].Enumerate().Count;
            }

            uiActionPending = new UiActionPending
            {
                Op = UiActionOp.Expand,
                Window = spec.Name,
                StartFrame = Time.frameCount,
                ExpandKey = rawKey,
                ExpandState = wantExpanded,
                ExpandChanged = changed,
            };
            ParsekLog.Info(Tag, $"uiaction expand initiated window={spec.Name} "
                + $"key={rawKey} state={Bool(wantExpanded)} changed={Int(changed)} "
                + $"expanded={Int(expandedCount)} total={Int(total)} "
                + "(awaiting one drawn frame)");
            SetExecResult(PendingVerdict, null, null);
        }

        private void CompleteUiActionExpand(UiActionSettleContext ctx,
                                            UiActionPending pending)
        {
            // The read-back is taken AFTER a drawn frame for the reason `open` is
            // two-phase: a draw is the only thing that could disagree with the write. It
            // never does today - every writer of these sets other than this op is a button
            // handler, and the seam synthesises no clicks - so a settled count that differs
            // from the executed one would be a genuine finding rather than noise, which is
            // what makes holding the frame worth it.
            List<UiExpandSet> sets = ResolveExpandSets(ctx.Ui, pending.Window);
            int expandedCount = 0;
            int total = 0;
            for (int i = 0; i < sets.Count; i++)
            {
                expandedCount += sets[i].Count();
                total += sets[i].Enumerate().Count;
            }

            ParsekLog.Info(Tag, $"uiaction expand window={pending.Window} "
                + $"key={pending.ExpandKey} state={Bool(pending.ExpandState)} "
                + $"changed={Int(pending.ExpandChanged)} expanded={Int(expandedCount)} "
                + $"total={Int(total)} frames={Int(ctx.Frames)}");
            EmitExecutedTerminal(ctx.Id, ctx.Seq, ctx.Verb, "OK",
                TestCommandUiState.BuildExpandPayload(
                    pending.Window, pending.ExpandKey, pending.ExpandState,
                    pending.ExpandChanged, expandedCount, total),
                null, dequeueHead: true);
        }

        /// <summary>One addressable expansion collection: its wire prefix, its enumeration
        /// and its read / write. A row per collection, for the reason
        /// <see cref="UiWindowHandle"/> is a row per window: a mis-wired set is then one
        /// site rather than one arm of a switch nothing can witness.</summary>
        private sealed class UiExpandSet
        {
            internal string Prefix;
            internal Func<List<string>> Enumerate;
            internal Func<string, bool, bool> Set;
            internal Func<int> Count;
        }

        private static List<UiExpandSet> ResolveExpandSets(ParsekUI ui, string window)
        {
            var sets = new List<UiExpandSet>();
            if (window == TestCommandUiAction.MissionsWindow)
            {
                RecordingsTableUI rt = ui.GetRecordingsTableUI();
                MissionsWindowUI mw = ui.GetMissionsUI();
                sets.Add(new UiExpandSet
                {
                    Prefix = TestCommandUiState.GroupKeyPrefix,
                    Enumerate = rt.EnumerateGroupNamesForTesting,
                    Set = rt.SetGroupExpandedForTesting,
                    Count = () => rt.ExpandedGroupCountForTesting,
                });
                sets.Add(new UiExpandSet
                {
                    Prefix = TestCommandUiState.ChainKeyPrefix,
                    Enumerate = rt.EnumerateChainIdsForTesting,
                    Set = rt.SetChainExpandedForTesting,
                    Count = () => rt.ExpandedChainCountForTesting,
                });
                sets.Add(new UiExpandSet
                {
                    Prefix = TestCommandUiState.VesselKeyPrefix,
                    Enumerate = mw.EnumerateVesselExpandKeysForTesting,
                    Set = mw.SetVesselExpandedForTesting,
                    Count = () => mw.ExpandedVesselCountForTesting,
                });
                sets.Add(new UiExpandSet
                {
                    Prefix = TestCommandUiState.LegKeyPrefix,
                    Enumerate = mw.EnumerateLegExpandKeysForTesting,
                    Set = mw.SetLegExpandedForTesting,
                    // INVERTED on the production side (collapsedLegs holds what is
                    // COLLAPSED), so the count of EXPANDED legs is the enumeration minus
                    // the collapsed set. Expressed here rather than on the wire, which
                    // always speaks "expanded".
                    // Clamped at zero: collapsedLegs can hold a key the enumeration no
                    // longer produces (a mission deleted since the click), and a negative
                    // `expanded=` on the wire would read as a seam defect.
                    Count = () => Math.Max(
                        0,
                        mw.EnumerateLegExpandKeysForTesting().Count
                            - mw.CollapsedLegCountForTesting),
                });
                sets.Add(new UiExpandSet
                {
                    Prefix = TestCommandUiState.DigestKeyPrefix,
                    Enumerate = mw.EnumerateDigestKeysForTesting,
                    Set = mw.SetDigestExpandedForTesting,
                    Count = () => mw.ExpandedDigestCountForTesting,
                });
                return sets;
            }
            if (window == TestCommandUiAction.LogisticsWindow)
            {
                LogisticsWindowUI lw = ui.GetLogisticsUI();
                sets.Add(new UiExpandSet
                {
                    Prefix = TestCommandUiState.RowKeyPrefix,
                    Enumerate = lw.EnumerateRowKeysForTesting,
                    Set = lw.SetRowExpandedForTesting,
                    Count = () => lw.ExpandedRowCountForTesting,
                });
                return sets;
            }
            return sets;
        }

        // ================================================================ op=target

        private void UiActionTargetOp(ParsedCommand cmd, ParsekUI ui, UiWindowSpec spec)
        {
            if (!TestCommandUiState.TryParseTarget(
                    spec.Name, ArgOrNull(cmd, TestCommandUiState.MissionArg),
                    ArgOrNull(cmd, TestCommandUiState.RouteArg),
                    out UiTargetKind kind, out string wanted, out string reject))
            {
                string detail = reject == TestCommandUiState.TargetUnsupportedWindowReason
                    ? $"{reject} window={spec.Name} "
                      + $"valid={TestCommandUiAction.StructureWindow}"
                    : $"{reject} window={spec.Name}";
                ParsekLog.Warn(Tag, $"uiaction rejected reason={reject} "
                    + $"window={spec.Name}");
                SetExecResult("REJECTED", null, detail);
                return;
            }

            StructureListWindowUI structure = ui.GetStructureListUI();
            string resolvedId;
            string title;
            List<string> candidates;
            if (kind == UiTargetKind.Mission)
            {
                if (!TryResolveMissionTarget(wanted, out resolvedId, out title,
                                             out candidates))
                {
                    RejectTargetNotFound(kind, wanted, candidates);
                    return;
                }
                structure.OpenForMission(resolvedId, title);
            }
            else
            {
                if (!TryResolveRouteTarget(wanted, out resolvedId, out title,
                                           out candidates))
                {
                    RejectTargetNotFound(kind, wanted, candidates);
                    return;
                }
                structure.OpenForRoute(resolvedId, title);
            }

            uiActionPending = new UiActionPending
            {
                Op = UiActionOp.Target,
                Window = spec.Name,
                StartFrame = Time.frameCount,
                TargetKind = kind,
                TargetId = resolvedId,
                TargetTitle = title,
                TargetSteps = structure.StepCountForTesting,
            };
            ParsekLog.Info(Tag, $"uiaction target initiated window={spec.Name} "
                + $"target={TestCommandUiState.TargetKindToken(kind)} id={resolvedId} "
                + $"title={title} steps={Int(structure.StepCountForTesting)} "
                + "(awaiting one drawn frame)");
            SetExecResult(PendingVerdict, null, null);
        }

        private void RejectTargetNotFound(UiTargetKind kind, string wanted,
                                          List<string> candidates)
        {
            ParsekLog.Warn(Tag, "uiaction rejected reason="
                + TestCommandUiState.TargetNotFoundReason
                + $" target={TestCommandUiState.TargetKindToken(kind)} "
                + $"wanted={wanted} known={Int(candidates.Count)}");
            SetExecResult("REJECTED", null,
                $"{TestCommandUiState.TargetNotFoundReason} "
                + $"target={TestCommandUiState.TargetKindToken(kind)} wanted={wanted} "
                + $"exist={TestCommandUiState.FormatCandidates(candidates, TestCommandUiState.CandidateListCap)}");
        }

        private void CompleteUiActionTarget(UiActionSettleContext ctx,
                                            UiActionPending pending)
        {
            StructureListWindowUI structure = ctx.Ui.GetStructureListUI();
            bool open = structure.IsOpen;
            if (!open)
            {
                // The opener raised the flag and a drawn frame put it back down. Reported
                // rather than accepted, for the `window-self-closed` reason: a census that
                // believes it opened the Log would otherwise photograph a scene without it.
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiState.TargetNotOpenedReason
                    + $" id={pending.TargetId} frames={Int(ctx.Frames)}");
                EmitExecutedTerminal(ctx.Id, ctx.Seq, ctx.Verb, "ERROR", null,
                    $"{TestCommandUiState.TargetNotOpenedReason} id={pending.TargetId}",
                    dequeueHead: true);
                return;
            }

            int steps = structure.StepCountForTesting;
            ParsekLog.Info(Tag, $"uiaction target window={pending.Window} "
                + $"target={TestCommandUiState.TargetKindToken(pending.TargetKind)} "
                + $"id={pending.TargetId} title={pending.TargetTitle} "
                + $"mode={structure.TargetModeForTesting} steps={Int(steps)} "
                + $"frames={Int(ctx.Frames)}");
            EmitExecutedTerminal(ctx.Id, ctx.Seq, ctx.Verb, "OK",
                TestCommandUiState.BuildTargetPayload(
                    pending.Window, pending.TargetKind, pending.TargetId,
                    pending.TargetTitle, steps, true),
                null, dequeueHead: true);
        }

        /// <summary>
        /// Resolves a <c>mission=</c> value to the TREE ID the Structure window's opener
        /// takes. Three rungs, in this order: the tree id itself, the Mission's display
        /// NAME, then the tree's own name. A census spec names what a reviewer can read off
        /// the window, which is the mission name; the id rung exists so a
        /// <c>${step.field}</c> chain from a handle listing works unchanged.
        /// </summary>
        private static bool TryResolveMissionTarget(string wanted, out string treeId,
                                                    out string title,
                                                    out List<string> candidates)
        {
            treeId = null;
            title = null;
            candidates = new List<string>();
            IReadOnlyList<RecordingTree> trees = RecordingStore.CommittedTrees;
            IReadOnlyList<Mission> missions = MissionStore.Missions;

            for (int i = 0; i < missions.Count; i++)
            {
                Mission m = missions[i];
                if (m == null) continue;
                if (!string.IsNullOrEmpty(m.Name)) candidates.Add(m.Name);
                if (string.Equals(m.TreeId, wanted, StringComparison.Ordinal)
                    || string.Equals(m.Name, wanted, StringComparison.Ordinal))
                {
                    treeId = m.TreeId;
                    title = string.IsNullOrEmpty(m.Name) ? m.TreeId : m.Name;
                    return true;
                }
            }
            for (int i = 0; i < trees.Count; i++)
            {
                RecordingTree t = trees[i];
                if (t == null) continue;
                if (string.Equals(t.Id, wanted, StringComparison.Ordinal)
                    || string.Equals(t.TreeName, wanted, StringComparison.Ordinal))
                {
                    treeId = t.Id;
                    title = string.IsNullOrEmpty(t.TreeName) ? t.Id : t.TreeName;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Resolves a <c>route=</c> value to a route id: the id itself, then the
        /// route's display name.</summary>
        private static bool TryResolveRouteTarget(string wanted, out string routeId,
                                                  out string title,
                                                  out List<string> candidates)
        {
            routeId = null;
            title = null;
            candidates = new List<string>();
            IReadOnlyList<Logistics.Route> routes = Logistics.RouteStore.CommittedRoutes;
            for (int i = 0; i < routes.Count; i++)
            {
                Logistics.Route r = routes[i];
                if (r == null) continue;
                if (!string.IsNullOrEmpty(r.Name)) candidates.Add(r.Name);
                if (string.Equals(r.Id, wanted, StringComparison.Ordinal)
                    || string.Equals(r.Name, wanted, StringComparison.Ordinal))
                {
                    routeId = r.Id;
                    title = string.IsNullOrEmpty(r.Name) ? r.Id : r.Name;
                    return true;
                }
            }
            return false;
        }

        // ================================================================ op=picker

        private void UiActionPickerOp(ParsedCommand cmd, ParsekUI ui, UiWindowSpec spec)
        {
            if (!TestCommandUiState.TryParsePicker(
                    spec.Name, ArgOrNull(cmd, TestCommandUiState.GroupArg),
                    ArgOrNull(cmd, TestCommandUiState.RecordingArg),
                    ArgOrNull(cmd, TestCommandUiState.RouteArg),
                    out UiPickerMode mode, out string wanted, out string reject))
            {
                string detail = reject == TestCommandUiState.PickerUnsupportedWindowReason
                    ? $"{reject} window={spec.Name} "
                      + $"valid={TestCommandUiState.PickerWindowNames}"
                    : $"{reject} window={spec.Name}";
                ParsekLog.Warn(Tag, $"uiaction rejected reason={reject} "
                    + $"window={spec.Name}");
                SetExecResult("REJECTED", null, detail);
                return;
            }

            string target = wanted;
            bool opened;
            if (mode == UiPickerMode.SetParent)
            {
                RecordingsTableUI rt = ui.GetRecordingsTableUI();
                opened = rt.TryOpenGroupPickerForGroupForTesting(wanted);
                if (!opened)
                {
                    RejectPickerTargetNotFound(mode, wanted,
                        rt.EnumerateGroupNamesForTesting());
                    return;
                }
            }
            else if (mode == UiPickerMode.Manage)
            {
                RecordingsTableUI rt = ui.GetRecordingsTableUI();
                bool takeFirst = wanted == TestCommandUiState.RecordingFirstToken;
                opened = rt.TryOpenGroupPickerForRecordingForTesting(
                    wanted, takeFirst, out string resolvedId);
                if (!opened)
                {
                    RejectPickerTargetNotFound(mode, wanted,
                        rt.EnumerateRecordingIdsForTesting());
                    return;
                }
                target = resolvedId;
            }
            else
            {
                LogisticsWindowUI lw = ui.GetLogisticsUI();
                if (!TryResolveRouteTarget(wanted, out string routeId, out string title,
                                           out List<string> candidates))
                {
                    RejectPickerTargetNotFound(mode, wanted, candidates);
                    return;
                }
                Logistics.RouteStore.TryGetRoute(routeId, out Logistics.Route route);
                opened = lw.OpenLinkPickerForTesting(route);
                if (!opened)
                {
                    // The production opener declined (a route with no id), which is a
                    // target problem rather than a draw problem.
                    RejectPickerTargetNotFound(mode, wanted, candidates);
                    return;
                }
                target = routeId;
            }

            uiActionPending = new UiActionPending
            {
                Op = UiActionOp.Picker,
                Window = spec.Name,
                StartFrame = Time.frameCount,
                PickerMode = mode,
                PickerTarget = target,
            };
            ParsekLog.Info(Tag, $"uiaction picker initiated window={spec.Name} "
                + $"picker={TestCommandUiState.PickerModeToken(mode)} target={target} "
                + "(awaiting one drawn frame)");
            SetExecResult(PendingVerdict, null, null);
        }

        private void RejectPickerTargetNotFound(UiPickerMode mode, string wanted,
                                                List<string> candidates)
        {
            ParsekLog.Warn(Tag, "uiaction rejected reason="
                + TestCommandUiState.PickerTargetNotFoundReason
                + $" picker={TestCommandUiState.PickerModeToken(mode)} wanted={wanted} "
                + $"known={Int(candidates != null ? candidates.Count : 0)}");
            SetExecResult("REJECTED", null,
                $"{TestCommandUiState.PickerTargetNotFoundReason} "
                + $"picker={TestCommandUiState.PickerModeToken(mode)} wanted={wanted} "
                + $"exist={TestCommandUiState.FormatCandidates(candidates, TestCommandUiState.CandidateListCap)}");
        }

        private void CompleteUiActionPicker(UiActionSettleContext ctx,
                                            UiActionPending pending)
        {
            bool open = pending.PickerMode == UiPickerMode.Link
                ? ctx.Ui.GetLogisticsUI().LinkPickerOpenForTesting
                : ctx.Ui.GetRecordingsTableUI().IsGroupPickerOpen;
            if (!open)
            {
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiState.PickerNotOpenedReason
                    + $" picker={TestCommandUiState.PickerModeToken(pending.PickerMode)} "
                    + $"target={pending.PickerTarget} frames={Int(ctx.Frames)}");
                EmitExecutedTerminal(ctx.Id, ctx.Seq, ctx.Verb, "ERROR", null,
                    $"{TestCommandUiState.PickerNotOpenedReason} "
                    + $"target={pending.PickerTarget}",
                    dequeueHead: true);
                return;
            }

            ParsekLog.Info(Tag, $"uiaction picker window={pending.Window} "
                + $"picker={TestCommandUiState.PickerModeToken(pending.PickerMode)} "
                + $"target={pending.PickerTarget} open=true frames={Int(ctx.Frames)}");
            EmitExecutedTerminal(ctx.Id, ctx.Seq, ctx.Verb, "OK",
                TestCommandUiState.BuildPickerPayload(
                    pending.Window, pending.PickerMode, pending.PickerTarget, true),
                null, dequeueHead: true);
        }
    }
}
