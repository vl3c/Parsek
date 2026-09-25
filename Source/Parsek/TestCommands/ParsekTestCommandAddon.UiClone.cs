using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// GUI-census partial: the applier for <c>UiAction op=clone</c>, the Missions tab's Clone
    /// button. The whole button body is one call, <c>MissionStore.Clone(mission)</c>, and
    /// this applier makes that call; the vocabulary, the settle predicate and the payload
    /// live in the pure sibling <see cref="TestCommandUiClone"/>.
    ///
    /// <para>Two-phase like <c>op=select</c>: the write is synchronous, and the read-back
    /// after one drawn frame is what separates "the store call returned a copy" from "the
    /// copy is still in the store the next frame's draw read".</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private void UiActionCloneOp(ParsedCommand cmd, UiWindowSpec spec)
        {
            if (!TestCommandUiClone.WindowHasCloneAffordance(spec.Name))
            {
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiClone.CloneUnsupportedWindowReason
                    + $" window={spec.Name}");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiClone.CloneUnsupportedWindowReason} window={spec.Name} "
                    + $"valid={TestCommandUiClone.CloneableWindowNames}");
                return;
            }

            string requestedMission = ArgOrNull(cmd, TestCommandUiState.MissionArg);
            if (!TryResolveSelectMission(requestedMission, out Mission source,
                                         out RecordingTree _))
            {
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiClone.CloneNoMissionReason
                    + $" window={spec.Name} mission={requestedMission ?? "-"} "
                    + $"missions={Int(MissionStore.Missions.Count)}");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiClone.CloneNoMissionReason} "
                    + $"mission={requestedMission ?? "-"} "
                    + $"missions={Int(MissionStore.Missions.Count)} "
                    + $"trees={Int(RecordingStore.CommittedTrees.Count)}");
                return;
            }

            int countBefore = MissionStore.Missions.Count;
            // The Clone button's body, verbatim (MissionsWindowUI mission header).
            Mission copy = MissionStore.Clone(source);
            if (copy == null)
            {
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiClone.CloneNotAppliedReason
                    + $" mission={source.Id} (store returned no copy)");
                SetExecResult("ERROR", null,
                    $"{TestCommandUiClone.CloneNotAppliedReason} mission={source.Id}");
                return;
            }

            uiActionPending = new UiActionPending
            {
                Op = UiActionOp.Clone,
                Window = spec.Name,
                StartFrame = Time.frameCount,
                CloneSourceId = source.Id,
                CloneSourceTreeId = source.TreeId,
                CloneCopyId = copy.Id,
                CloneCountBefore = countBefore,
            };
            ParsekLog.Info(Tag, $"uiaction clone initiated window={spec.Name} "
                + $"mission={source.Id} copy={copy.Id} tree={source.TreeId} "
                + $"missionsBefore={Int(countBefore)} (awaiting one drawn frame)");
            SetExecResult(PendingVerdict, null, null);
        }

        private void CompleteUiActionClone(UiActionSettleContext ctx, UiActionPending pending)
        {
            Mission copy = MissionStore.FindById(pending.CloneCopyId);
            int countAfter = MissionStore.Missions.Count;
            if (!TestCommandUiClone.CloneSettledAsRequested(
                    copy != null, pending.CloneCountBefore, countAfter,
                    pending.CloneSourceTreeId, copy?.TreeId))
            {
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiClone.CloneNotAppliedReason
                    + $" mission={pending.CloneSourceId} copy={pending.CloneCopyId} "
                    + $"found={Bool(copy != null)} missionsBefore={Int(pending.CloneCountBefore)} "
                    + $"missionsAfter={Int(countAfter)} frames={Int(ctx.Frames)}");
                EmitExecutedTerminal(ctx.Id, ctx.Seq, ctx.Verb, "ERROR", null,
                    $"{TestCommandUiClone.CloneNotAppliedReason} "
                    + $"mission={pending.CloneSourceId} copy={pending.CloneCopyId}",
                    dequeueHead: true);
                return;
            }

            int excluded = copy.ExcludedIntervalKeys.Count;
            int links = copy.IncludedForeignDockLinkIds.Count;
            ParsekLog.Info(Tag, $"uiaction clone window={pending.Window} "
                + $"mission={pending.CloneSourceId} copy={copy.Id} name='{copy.Name}' "
                + $"tree={copy.TreeId} missions={Int(countAfter)} excluded={Int(excluded)} "
                + $"links={Int(links)} loop={Bool(copy.LoopPlayback)} frames={Int(ctx.Frames)}");
            EmitExecutedTerminal(ctx.Id, ctx.Seq, ctx.Verb, "OK",
                TestCommandUiClone.BuildClonePayload(
                    pending.Window, pending.CloneSourceId, copy.Id, copy.Name, countAfter,
                    excluded, links, copy.LoopPlayback),
                null, dequeueHead: true);
        }
    }
}
