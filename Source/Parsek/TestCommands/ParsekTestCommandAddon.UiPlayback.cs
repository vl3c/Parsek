using System.Collections.Generic;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// GUI-census partial: the applier for <c>UiAction op=playback</c>, the per-recording
    /// ghost-playback tick box of the Recordings tab. The arg parses, the selection rule,
    /// the read-back predicate and the payload shape live in the pure sibling
    /// <see cref="TestCommandUiAction"/>; this file resolves the live recording set, drives
    /// the production writer and reads it back.
    ///
    /// <para><b>THE SAME ARGUMENT AS THE REST OF <c>UiAction</c>.</b> The box is a plain
    /// <c>GUILayout.Toggle</c> whose handler calls exactly one method -
    /// <c>RecordingStore.SetRecordingPlaybackEnabled</c>, THE single writer of
    /// <c>Recording.PlaybackEnabled</c> for all four affordances (per-row, select-all
    /// header, the two group / chain-block headers). So the seam calls that same method and
    /// reads the flag back. No synthesised input, no new player-facing surface, and no
    /// second writer of the field: assigning <c>rec.PlaybackEnabled</c> here would bypass
    /// the writer's log line and its <c>RecordingPlaybackEnabledChanged</c> raise, which is
    /// what brings the Tracking Station's quarter-second lifecycle tick forward to the same
    /// frame.</para>
    ///
    /// <para><b>WHY IT NAMES NO WINDOW.</b> Every other state-driving op addresses a window
    /// because the state it writes belongs to one. This flag belongs to the RECORDING, and
    /// the surfaces it gates - the flight ghost, the map icon, the orbit line, the Tracking
    /// Station row - are drawn by three different hosts, none of which owns it. So
    /// <c>op=playback</c> is absent from <c>TestCommandUiAction.OpNeedsWindow</c> (and from
    /// <c>hlib.UIACTION_OPS_NEEDING_WINDOW</c>), and a <c>window=</c> beside it is a
    /// pre-launch spec error rather than a silently ignored arg.</para>
    ///
    /// <para><b>THE SET IS THE ERS, NOT THE RAW COMMITTED LIST.</b> The select-all header
    /// iterates the store's raw committed list directly (the literal is not spelled here:
    /// <c>scripts/grep-audit-ers-els.ps1</c> is a RAW text scan, so a comment naming it
    /// fails the gate exactly the way a call would), which the header can do because
    /// <c>RecordingsTableUI</c> is an ERS-allowlisted file. A seam file is not, and
    /// the routing rule is the point rather than the paperwork: a superseded recording is
    /// not something a lane can see, photograph or assert on, so flipping its box would be
    /// a write no read-back could witness. <c>EffectiveState.ComputeERS()</c> returns the
    /// same <c>Recording</c> REFERENCES, so the flip reaches the real objects.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // ================================================================ op=playback

        private void UiActionPlaybackOp(ParsedCommand cmd)
        {
            string rawState = ArgOrNull(cmd, TestCommandUiState.StateArg);
            if (!TestCommandUiAction.TryParsePlaybackState(
                    rawState, out bool wantEnabled, out string stateReject))
            {
                ParsekLog.Warn(Tag, $"uiaction rejected reason={stateReject} "
                    + $"state={rawState ?? string.Empty}");
                SetExecResult("REJECTED", null,
                    stateReject == TestCommandUiAction.StateArgMissingReason
                        ? stateReject
                        : $"{stateReject} state={rawState ?? string.Empty} valid="
                          + $"{TestCommandUiState.StateTrueToken},"
                          + $"{TestCommandUiState.StateFalseToken}");
                return;
            }

            TestCommandUiAction.ResolvePlaybackSelection(
                ArgOrNull(cmd, TestCommandUiState.RecordingArg),
                out string recordingId, out string echoToken);

            IReadOnlyList<Recording> effective = EffectiveState.ComputeERS();
            List<Recording> considered =
                TestCommandUiAction.SelectPlaybackTargets(effective, recordingId);
            if (considered == null)
            {
                // A named id that is in no effective recording. REJECTED and not a
                // cheerful "0 changed": a lane that names a recording and gets a success
                // cannot tell a typo'd id from a box that was already right. The message
                // carries the count so "known=0" (an empty save) reads differently from
                // "known=7" (a wrong id).
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiAction.RecordingUnknownReason
                    + $" recording={recordingId} known={Int(effective.Count)}");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiAction.RecordingUnknownReason} recording={recordingId} "
                    + $"known={Int(effective.Count)}");
                return;
            }

            int changed = 0;
            for (int i = 0; i < considered.Count; i++)
            {
                // THE single writer. It no-ops (returning false) on an unchanged value, so
                // `changed` counts real flips and an already-correct recording is neither a
                // log line nor a failure.
                if (RecordingStore.SetRecordingPlaybackEnabled(considered[i], wantEnabled))
                    changed++;
            }

            // TWO-PHASE: the flag gates DRAWN state (the ghost, the map icon, the orbit
            // line, the Tracking Station row), so a same-Update read-back would compare
            // the field with the value just written to it - the defect the settle exists
            // to remove. The considered IDS are carried across the frame rather than the
            // Recording references, so the settle re-resolves against the live set and a
            // recording that went away between the write and the draw reads as
            // `playback-not-applied` instead of being silently counted as correct.
            uiActionPending = new UiActionPending
            {
                Op = UiActionOp.Playback,
                Window = string.Empty,
                StartFrame = Time.frameCount,
                PlaybackRecordingToken = echoToken,
                PlaybackRecordingId = recordingId,
                PlaybackState = wantEnabled,
                PlaybackChanged = changed,
            };
            ParsekLog.Info(Tag, $"uiaction playback initiated recording={echoToken} "
                + $"state={Bool(wantEnabled)} (awaiting one drawn frame)");
            SetExecResult(PendingVerdict, null, null);
        }

        private void CompleteUiActionPlayback(UiActionSettleContext ctx,
                                              UiActionPending pending)
        {
            IReadOnlyList<Recording> effective = EffectiveState.ComputeERS();
            List<Recording> considered = TestCommandUiAction.SelectPlaybackTargets(
                effective, pending.PlaybackRecordingId);
            int total = considered != null ? considered.Count : 0;
            int agreeing = TestCommandUiAction.CountPlaybackAgreeing(
                considered, pending.PlaybackState);

            // A named recording that has vanished (considered == null) reaches the
            // predicate as agreeing=0 over considered=1, i.e. NOT applied - which is the
            // true statement: nothing on screen now carries the state we wrote.
            int consideredCount = considered != null
                ? total
                : (pending.PlaybackRecordingId != null ? 1 : 0);
            if (!TestCommandUiAction.PlaybackAppliedToEveryConsidered(
                    agreeing, consideredCount))
            {
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiAction.PlaybackNotAppliedReason
                    + $" recording={pending.PlaybackRecordingToken} "
                    + $"state={Bool(pending.PlaybackState)} agreeing={Int(agreeing)} "
                    + $"total={Int(consideredCount)} frames={Int(ctx.Frames)}");
                EmitExecutedTerminal(ctx.Id, ctx.Seq, ctx.Verb, "ERROR", null,
                    $"{TestCommandUiAction.PlaybackNotAppliedReason} "
                    + $"recording={pending.PlaybackRecordingToken}",
                    dequeueHead: true);
                return;
            }

            ParsekLog.Info(Tag,
                $"uiaction playback recording={pending.PlaybackRecordingToken} "
                + $"state={Bool(pending.PlaybackState)} "
                + $"changed={Int(pending.PlaybackChanged)} total={Int(total)}");
            EmitExecutedTerminal(ctx.Id, ctx.Seq, ctx.Verb, "OK",
                TestCommandUiAction.BuildPlaybackPayload(
                    pending.PlaybackRecordingToken, pending.PlaybackState,
                    pending.PlaybackChanged, total),
                null, dequeueHead: true);
        }
    }
}
