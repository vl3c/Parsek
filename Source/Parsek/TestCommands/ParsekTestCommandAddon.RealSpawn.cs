using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin Unity applier for the two-phase <c>RealSpawn</c> verb; the contract is on
    /// <see cref="TestCommandRealSpawn"/>.
    ///
    /// <para><b>THE BUTTON'S OWN BODY.</b> The row comes from
    /// <c>SpawnControlUI.TryFindRowForRecordingForTesting</c> (the draw pass's own candidate
    /// list and row model) and the press is <c>SpawnControlUI.ExecuteRowWarp</c>, the one
    /// body behind the drawn button and <c>UiAction op=warp</c>. So the
    /// <c>Real Spawn Control: warp to ...</c> line, the <c>WarpToRecordingEnd</c> time jump
    /// and the spawn the playback loop performs afterwards are the ones a player's click
    /// produces. The seam spawns nothing itself: it waits for the product's own spawn
    /// bookkeeping (<c>Recording.VesselSpawned</c> + <c>SpawnedVesselPersistentId</c>),
    /// which the end-of-recording spawn and the chain-tip spawn both write.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private string realSpawnRecordingId;
        private string realSpawnVesselName;
        private double realSpawnEndUT;

        private void RealSpawnImpl(ParsedCommand cmd)
        {
            string recId = ArgOrNull(cmd, TestCommandRealSpawn.RecKey);
            string argReason = TestCommandRealSpawn.ValidateRecArg(recId);
            if (argReason != null)
            {
                RejectRealSpawn(argReason, "rec=");
                return;
            }

            ParsekFlight flight = ParsekFlight.Instance;
            if (flight == null)
            {
                RejectRealSpawn(TestCommandRealSpawn.HostUnavailableReason, "flight=false");
                return;
            }

            Recording rec = RecordingStore.TryFindCommittedRecordingById(recId);
            double currentUT = Planetarium.GetUniversalTime();
            NearbySpawnCandidate cand = default(NearbySpawnCandidate);
            SpawnCandidateRowPresentation row = default(SpawnCandidateRowPresentation);
            int candidateCount = 0;
            bool hasRow = rec != null && SpawnControlUI.TryFindRowForRecordingForTesting(
                flight, recId, currentUT, out cand, out row, out candidateCount);
            bool alreadySpawned = rec != null && rec.VesselSpawned && rec.SpawnedVesselPersistentId != 0;

            RealSpawnGateDecision gate = TestCommandRealSpawn.DecideGate(
                rec != null, alreadySpawned, hasRow, row.WarpButtonEnabled, row.UsesDepartureWarp);
            if (gate != RealSpawnGateDecision.Proceed)
            {
                CultureInfo ic = CultureInfo.InvariantCulture;
                string detail = $"rec={recId}";
                if (rec != null)
                    detail += $" vessel={rec.VesselName} endUT={rec.EndUT.ToString("F1", ic)} "
                        + $"currentUT={currentUT.ToString("F1", ic)}";
                if (gate == RealSpawnGateDecision.AlreadySpawned)
                    detail += $" pid={rec.SpawnedVesselPersistentId.ToString(ic)}";
                else if (gate == RealSpawnGateDecision.NotACandidate)
                    detail += $" candidates={Int(candidateCount)}";
                else if (gate == RealSpawnGateDecision.ButtonDisabled)
                    detail += $" disabledReason={row.WarpButtonDisabledReason ?? string.Empty}";
                else if (gate == RealSpawnGateDecision.RowWarpsToDeparture)
                    detail += $" departureUT={cand.departureUT.ToString("F1", ic)}";
                RejectRealSpawn(TestCommandRealSpawn.GateReason(gate), detail);
                return;
            }

            ParsekLog.Info(Tag, TestCommandRealSpawn.FormatPressedLine(
                recId, cand.recordingIndex, rec.VesselName, rec.EndUT, currentUT));
            SpawnControlUI.PressRowWarpForTesting(cand, row, flight);

            double utAfter = Planetarium.GetUniversalTime();
            if (!TestCommandRealSpawn.WarpApplied(utAfter, rec.EndUT))
            {
                CultureInfo ic = CultureInfo.InvariantCulture;
                ParsekLog.Warn(Tag, $"realspawn error reason={TestCommandRealSpawn.WarpNotAppliedReason} "
                    + $"rec={recId} endUT={rec.EndUT.ToString("F1", ic)} utAfter={utAfter.ToString("F1", ic)}");
                SetExecResult("ERROR", null, TestCommandRealSpawn.WarpNotAppliedReason);
                return;
            }

            realSpawnRecordingId = recId;
            realSpawnVesselName = rec.VesselName;
            realSpawnEndUT = rec.EndUT;
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteRealSpawn(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds(TestCommandRealSpawn.Verb);
            Recording rec = RecordingStore.TryFindCommittedRecordingById(realSpawnRecordingId);
            bool abandoned = rec != null && (rec.SpawnAbandoned || rec.TerminalSpawnCannotSpawnSafely);
            RealSpawnCompletionDecision decision = TestCommandRealSpawn.DecideCompletion(
                rec != null,
                rec != null && rec.VesselSpawned,
                rec != null ? rec.SpawnedVesselPersistentId : 0u,
                abandoned, elapsed, budget);
            if (decision == RealSpawnCompletionDecision.StillWaiting)
                return;

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            string recId = realSpawnRecordingId;
            ClearTwoPhase();
            CultureInfo ic = CultureInfo.InvariantCulture;

            switch (decision)
            {
                case RealSpawnCompletionDecision.Spawned:
                {
                    uint pid = rec.SpawnedVesselPersistentId;
                    bool loaded = IsLiveVesselLoaded(pid);
                    ParsekLog.Info(Tag, TestCommandRealSpawn.FormatCompleteLine(
                        recId, pid, rec.VesselName, loaded, elapsed));
                    EmitExecutedTerminal(id, seq, verb, "OK",
                        TestCommandRealSpawn.BuildOkPayload(recId, pid, rec.VesselName, rec.EndUT, loaded),
                        null, dequeueHead: true);
                    break;
                }
                case RealSpawnCompletionDecision.Abandoned:
                    ParsekLog.Error(Tag, $"realspawn error reason={TestCommandRealSpawn.SpawnAbandonedReason} "
                        + $"rec={recId} spawnAbandoned={Bool(rec.SpawnAbandoned)} "
                        + $"cannotSpawnSafely={Bool(rec.TerminalSpawnCannotSpawnSafely)} "
                        + $"elapsed={elapsed.ToString("F1", ic)}s");
                    EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                        TestCommandRealSpawn.SpawnAbandonedReason, dequeueHead: true);
                    break;
                case RealSpawnCompletionDecision.Timeout:
                    TestCommandDiagnostics.Timeout(id, verb, elapsed, TestCommandRealSpawn.SpawnTimeoutReason);
                    ParsekLog.Error(Tag, $"realspawn error reason={TestCommandRealSpawn.SpawnTimeoutReason} "
                        + $"rec={recId} vessel={realSpawnVesselName ?? string.Empty} "
                        + $"endUT={realSpawnEndUT.ToString("F1", ic)} found={Bool(rec != null)} "
                        + $"elapsed={elapsed.ToString("F1", ic)}s - the clock reached endUT but no spawn landed");
                    EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                        TestCommandRealSpawn.SpawnTimeoutReason, dequeueHead: true);
                    break;
            }
            realSpawnRecordingId = null;
            realSpawnVesselName = null;
        }

        private static bool IsLiveVesselLoaded(uint pid)
        {
            if (pid == 0 || FlightGlobals.Vessels == null)
                return false;
            for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
            {
                Vessel v = FlightGlobals.Vessels[i];
                if (v != null && v.persistentId == pid)
                    return v.loaded;
            }
            return false;
        }

        private void RejectRealSpawn(string reason, string detail)
        {
            ParsekLog.Warn(Tag, $"realspawn rejected reason={reason} {detail}");
            SetExecResult("REJECTED", null, reason);
        }
    }
}
