namespace Parsek.TestCommands
{
    /// <summary>
    /// GUI-seam partial: the applier for <c>UiAction op=warp window=spawncontrol</c>, which
    /// presses the Real Spawn Control table's FIRST row warp button.
    ///
    /// <para><b>THE BUTTON'S OWN BODY, NOT A SECOND SPELLING OF IT.</b> The row button's
    /// click handler is one method, <c>SpawnControlUI.ExecuteRowWarp</c>, and this op
    /// reaches it through <c>SpawnControlUI.TryPressFirstRowWarpForTesting</c>, which builds
    /// the row from the same sort and the same <c>SpawnControlPresentation</c> row model the
    /// draw pass uses. So the <c>Real Spawn Control: warp to ...</c> line and the
    /// flight-controller warp it triggers are exactly the ones a player's click produces.
    /// No synthesised input and no new player-facing surface.</para>
    ///
    /// <para><b>A GREYED BUTTON IS NOT PRESSED.</b> A row outside the warp gates (too far,
    /// closing too fast, or already past its UT) draws its button disabled, and a player
    /// cannot click it; the op answers REJECTED <c>warp-button-disabled</c> with the
    /// button's own disabled-hover text rather than warping anyway. REJECTED rather than
    /// ERROR because nothing was acted on.</para>
    ///
    /// <para><b>ONE-PHASE.</b> The warp itself is synchronous (<c>WarpToRecordingEnd</c> /
    /// <c>WarpToDeparture</c> run the time jump inside the call), and what follows it - the
    /// spawn at recording end - belongs to the playback loop, which a lane observes through
    /// its own log contracts rather than through this op's read-back.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private void UiActionWarpOp(ParsekUI ui, UiWindowSpec spec)
        {
            if (!TestCommandUiAction.WindowHasRowWarpButton(spec.Name))
            {
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiAction.WarpUnsupportedWindowReason
                    + $" window={spec.Name}");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiAction.WarpUnsupportedWindowReason} window={spec.Name} "
                    + $"valid={TestCommandUiAction.SpawnControlWindow}");
                return;
            }

            ParsekFlight flight = ParsekFlight.Instance;
            SpawnControlUI window = ui.GetSpawnControlUI();
            if (flight == null || window == null)
            {
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiAction.HostUnavailableReason
                    + $" op={TestCommandUiAction.WarpOpToken} window={spec.Name} "
                    + $"flight={Bool(flight != null)} window={Bool(window != null)}");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiAction.HostUnavailableReason} window={spec.Name}");
                return;
            }

            if (!window.TryPressFirstRowWarpForTesting(flight,
                    out NearbySpawnCandidate cand, out SpawnCandidateRowPresentation row,
                    out string refusal, out string disabledReason))
            {
                string detail = refusal == SpawnControlUI.WarpRefusalButtonDisabled
                    ? $" vessel={cand.vesselName} recording={cand.recordingIndex} "
                      + $"disabledReason={disabledReason ?? string.Empty}"
                    : string.Empty;
                ParsekLog.Warn(Tag, $"uiaction rejected reason={refusal} "
                    + $"window={spec.Name}{detail}");
                SetExecResult("REJECTED", null, $"{refusal} window={spec.Name}{detail}");
                return;
            }

            ParsekLog.Info(Tag, $"uiaction warp window={spec.Name} vessel={cand.vesselName} "
                + $"recording={cand.recordingIndex} departure={Bool(row.UsesDepartureWarp)}");
            SetExecResult("OK",
                TestCommandUiAction.BuildWarpPayload(
                    spec.Name, cand.vesselName, cand.recordingIndex, row.UsesDepartureWarp),
                null);
        }
    }
}
