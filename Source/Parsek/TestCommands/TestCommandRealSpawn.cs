using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>What the RealSpawn pre-press gate concludes.</summary>
    internal enum RealSpawnGateDecision
    {
        /// <summary>Press the row's warp button.</summary>
        Proceed,

        /// <summary><c>rec=</c> names no committed recording.</summary>
        UnknownRecording,

        /// <summary>The recording already spawned its real vessel.</summary>
        AlreadySpawned,

        /// <summary>The Real Spawn Control table draws no row for the recording.</summary>
        NotACandidate,

        /// <summary>The row exists but its warp button is drawn disabled.</summary>
        ButtonDisabled,

        /// <summary>The row is a "Leaves" row: its Warp jumps to just before the ghost's
        /// departure, which spawns nothing.</summary>
        RowWarpsToDeparture,
    }

    /// <summary>What one completion poll of a pressed RealSpawn concludes.</summary>
    internal enum RealSpawnCompletionDecision
    {
        StillWaiting,
        Spawned,
        Abandoned,
        Timeout,
    }

    /// <summary>
    /// Pure half of the automation-only <c>RealSpawn</c> seam verb (D18 player-action
    /// half, todo D18-REALSPAWN-RECOVER-SEAM-VERB-PAIR): press the Real Spawn Control
    /// table's warp button on the row of ONE committed recording, through the button's
    /// own click body (<c>SpawnControlUI.ExecuteRowWarp</c> -&gt;
    /// <c>ParsekFlight.WarpToRecordingEnd</c>), then hold the head until the playback
    /// loop has spawned that recording's real vessel and answer its persistentId.
    ///
    /// <para>Grammar: <c>cmd=RealSpawn rec=&lt;recordingId&gt;</c>. The id is usually a
    /// handle (<c>${chains.chain0tip}</c> from <c>ListHandles kind=chains</c>, or a
    /// <c>kind=committed</c> row). TWO-PHASE, RequiresFlight, 120 s budget (the TimeJump
    /// size: the jump is synchronous, the spawn lands within the next frames).</para>
    ///
    /// <para>The row comes from the same <c>ParsekFlight.NearbySpawnCandidates</c> list
    /// and the same <c>SpawnControlPresentation.BuildRowPresentation</c> the draw pass
    /// uses, so the verb can press exactly what a player can press and nothing else: no
    /// row, a greyed button, or a "Leaves" row's departure warp is a REJECTED before any side
    /// effect.</para>
    /// </summary>
    internal static class TestCommandRealSpawn
    {
        internal const string Verb = "RealSpawn";
        internal const string RecKey = "rec";

        internal const string RecArgMissingReason = "realspawn-rec-arg-missing";
        internal const string HostUnavailableReason = "realspawn-host-unavailable";
        internal const string UnknownRecordingReason = "realspawn-unknown-recording";
        internal const string AlreadySpawnedReason = "realspawn-already-spawned";
        internal const string NotACandidateReason = "realspawn-not-a-candidate";
        internal const string ButtonDisabledReason = "realspawn-button-disabled";
        internal const string RowWarpsToDepartureReason = "realspawn-row-warps-to-departure";

        // Post-press ERROR terminals (the verb acted and the game did not follow).
        internal const string WarpNotAppliedReason = "realspawn-warp-not-applied";
        internal const string SpawnAbandonedReason = "realspawn-spawn-abandoned";
        internal const string SpawnTimeoutReason = "realspawn-spawn-timeout";

        /// <summary>The REJECTED vocabulary, in gate order. hlib.REALSPAWN_REASONS mirrors
        /// it (RealSpawnRecoverSourceSyncTests).</summary>
        internal static readonly string[] Reasons =
        {
            "realspawn-rec-arg-missing",
            "realspawn-host-unavailable",
            "realspawn-unknown-recording",
            "realspawn-already-spawned",
            "realspawn-not-a-candidate",
            "realspawn-button-disabled",
            "realspawn-row-warps-to-departure",
        };

        /// <summary>The post-press ERROR vocabulary.</summary>
        internal static readonly string[] ErrorReasons =
        {
            "realspawn-warp-not-applied",
            "realspawn-spawn-abandoned",
            "realspawn-spawn-timeout",
        };

        /// <summary>Slack on the "did the clock reach endUT" read-back after the press.
        /// <c>WarpToRecordingEnd</c> sets the clock to exactly <c>EndUT</c>.</summary>
        internal const double WarpReadBackToleranceSeconds = 0.01;

        /// <summary>Returns null when <c>rec=</c> is present, else the refusal reason.</summary>
        internal static string ValidateRecArg(string raw)
            => string.IsNullOrEmpty(raw) ? RecArgMissingReason : null;

        /// <summary>
        /// The pre-press gate, in the order a player meets it: the recording must exist,
        /// must not have spawned already, must have a row in the table, the row's button
        /// must be live, and it must warp to the spawn (a "Ready" row).
        /// </summary>
        internal static RealSpawnGateDecision DecideGate(
            bool recordingFound, bool alreadySpawned, bool hasRow,
            bool buttonEnabled, bool usesDepartureWarp)
        {
            if (!recordingFound) return RealSpawnGateDecision.UnknownRecording;
            if (alreadySpawned) return RealSpawnGateDecision.AlreadySpawned;
            if (!hasRow) return RealSpawnGateDecision.NotACandidate;
            if (!buttonEnabled) return RealSpawnGateDecision.ButtonDisabled;
            if (usesDepartureWarp) return RealSpawnGateDecision.RowWarpsToDeparture;
            return RealSpawnGateDecision.Proceed;
        }

        /// <summary>The REJECTED reason for a non-Proceed gate, or null for Proceed.</summary>
        internal static string GateReason(RealSpawnGateDecision gate)
        {
            switch (gate)
            {
                case RealSpawnGateDecision.UnknownRecording: return UnknownRecordingReason;
                case RealSpawnGateDecision.AlreadySpawned: return AlreadySpawnedReason;
                case RealSpawnGateDecision.NotACandidate: return NotACandidateReason;
                case RealSpawnGateDecision.ButtonDisabled: return ButtonDisabledReason;
                case RealSpawnGateDecision.RowWarpsToDeparture: return RowWarpsToDepartureReason;
                default: return null;
            }
        }

        /// <summary>True when the press moved the clock to the recording's end:
        /// <c>WarpToRecordingEnd</c> only logs and returns on an invalid jump, so this
        /// read-back is the only way to tell a refused warp from a performed one.</summary>
        internal static bool WarpApplied(double utAfterPress, double endUT)
            => utAfterPress + WarpReadBackToleranceSeconds >= endUT;

        /// <summary>
        /// One completion poll. Spawned wins over everything (the bookkeeping the playback
        /// loop and the chain-tip path both write); a recording the spawn path gave up on
        /// is a fast ERROR; the budget is the catch-all.
        /// </summary>
        internal static RealSpawnCompletionDecision DecideCompletion(
            bool recordingFound, bool vesselSpawned, uint spawnedPid, bool abandoned,
            double elapsedSeconds, double budgetSeconds)
        {
            if (recordingFound && vesselSpawned && spawnedPid != 0)
                return RealSpawnCompletionDecision.Spawned;
            if (recordingFound && abandoned)
                return RealSpawnCompletionDecision.Abandoned;
            if (elapsedSeconds >= budgetSeconds)
                return RealSpawnCompletionDecision.Timeout;
            return RealSpawnCompletionDecision.StillWaiting;
        }

        /// <summary>The OK payload: <c>rec= pid= vessel= endUT= loaded=</c>. <c>pid</c> is
        /// the spawned vessel's KSP-unique persistentId, the handle a following
        /// <c>SimulateStockSwitchClick pid=</c> or <c>Recover pid=</c> consumes.</summary>
        internal static List<KeyValuePair<string, string>> BuildOkPayload(
            string recordingId, uint spawnedPid, string vesselName, double endUT, bool loaded)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("rec", recordingId ?? string.Empty),
                new KeyValuePair<string, string>("pid", spawnedPid.ToString(ic)),
                new KeyValuePair<string, string>("vessel", vesselName ?? string.Empty),
                new KeyValuePair<string, string>("endUT", endUT.ToString("R", ic)),
                new KeyValuePair<string, string>("loaded", loaded ? "true" : "false"),
            };
        }

        /// <summary>The grep-stable press line.</summary>
        internal static string FormatPressedLine(
            string recordingId, int recordingIndex, string vesselName, double endUT, double utBefore)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "realspawn pressed rec=" + (recordingId ?? string.Empty)
                + " index=" + recordingIndex.ToString(ic)
                + " vessel=" + (vesselName ?? string.Empty)
                + " endUT=" + endUT.ToString("F1", ic)
                + " utBefore=" + utBefore.ToString("F1", ic);
        }

        /// <summary>The grep-stable completion line.</summary>
        internal static string FormatCompleteLine(
            string recordingId, uint spawnedPid, string vesselName, bool loaded, double elapsed)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "realspawn complete rec=" + (recordingId ?? string.Empty)
                + " pid=" + spawnedPid.ToString(ic)
                + " vessel=" + (vesselName ?? string.Empty)
                + " loaded=" + (loaded ? "true" : "false")
                + " elapsed=" + elapsed.ToString("F1", ic) + "s";
        }
    }
}
