using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>What became of the spawn a Real Spawn Control Warp jumped to.</summary>
    internal enum SpawnWarpOutcomeKind
    {
        /// <summary>No answer yet: the spawn has not run, or is still retrying.</summary>
        Pending,
        /// <summary>The vessel exists (spawned, or an existing vessel was adopted). The
        /// spawner already says so ("has appeared!"), so the watch stays silent.</summary>
        Spawned,
        /// <summary>The spawn was refused for good; the message names why.</summary>
        Refused,
        /// <summary>The spawn is held and may still happen later; the message names the
        /// hold.</summary>
        Held,
        /// <summary>The recording left the committed list; nothing to report.</summary>
        Gone
    }

    internal struct SpawnWarpOutcome
    {
        internal SpawnWarpOutcomeKind Kind;

        /// <summary>The player-facing clause for Refused / Held: lower case, no trailing
        /// period. Empty otherwise.</summary>
        internal string Reason;

        /// <summary>The short log token for the outcome's cause.</summary>
        internal string Cause;
    }

    /// <summary>
    /// The recording state the outcome watch reads, copied from the live recording each
    /// tick so <see cref="SpawnWarpOutcomeWatch.Evaluate"/> stays pure.
    /// </summary>
    internal struct SpawnWarpOutcomeInput
    {
        internal bool RecordingFound;
        internal uint SpawnedVesselPersistentId;
        internal bool VesselSpawned;
        internal bool SpawnAbandoned;
        /// <summary><see cref="VesselSpawner.IsSettledAsKscRetirement"/>; read only once the
        /// recording reads abandoned.</summary>
        internal bool KscRetired;
        internal bool WalkbackExhausted;
        internal int CollisionBlockCount;
        /// <summary>The recording is a ghost-chain tip whose spawn is blocked by a vessel
        /// in the way (<c>GhostChain.SpawnBlocked</c>); the chain path keeps retrying.</summary>
        internal bool ChainSpawnBlocked;
        internal int SpawnAttempts;
        internal bool TerminalSpawnCannotSpawnSafely;
        internal bool TerminalSpawnSafetyDeferred;
        internal string TerminalSpawnSafetyReasonCode;
        internal double TerminalSpawnNextAttemptUT;
        /// <summary>True when the terminal-orbit decision was taken at or after the Warp's
        /// target UT, so a soft hold left over from an earlier attempt is not read as this
        /// spawn's answer.</summary>
        internal bool TerminalDecisionAfterWarp;
        /// <summary>The abandon reason the spawner noted for this recording
        /// (<see cref="SpawnWarpOutcomeWatch.NoteAbandonReason"/>), or null.</summary>
        internal string AbandonNote;
        /// <summary>Real seconds since the Warp.</summary>
        internal double ElapsedSeconds;
    }

    /// <summary>
    /// Pure decisions behind the one message a Real Spawn Control Warp owes when the vessel
    /// it jumped to does not appear (todo GUI-P4-WARP-TO-SPAWN-NAMES-AN-OUTCOME-IT-CANNOT-PROMISE).
    /// The window hides rows whose spawn is refused for a reason it can see ahead of time
    /// (<see cref="GhostPlaybackLogic.IsNearbySpawnCandidateEligible"/>), so what is left are
    /// the refusals found only at spawn time, after the row has left the list. The Warp is
    /// the event that changed something (it moved the clock), so the message rides on it;
    /// a spawn that succeeds keeps the spawner's own "has appeared!" line and adds nothing.
    /// </summary>
    internal static class SpawnWarpOutcomeWatch
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>Real seconds the watch waits for a spawn that neither succeeds nor
        /// reports a refusal. Covers the collision retry window
        /// (<see cref="VesselSpawner.MaxCollisionBlocks"/> frames) at ordinary frame rates.</summary>
        internal const double TimeoutSeconds = 10.0;

        /// <summary>How long the outcome message stays on screen.</summary>
        internal const float MessageSeconds = 8f;

        internal const string ReasonKscRetired =
            "its flight ended parked at the launch site, which leaves no vessel";
        internal const string ReasonWalkbackExhausted =
            "something is in the way of its spawn spot, and no clear spot was found along its path";
        internal const string ReasonCollisionBlocked =
            "something is in the way of its spawn spot";
        internal const string ReasonLoadFailed =
            "the game could not load the vessel";
        internal const string ReasonAbandonedUnknown =
            "Parsek gave up on the spawn; KSP.log names the reason";
        internal const string ReasonNoLivingCrew =
            "none of its crew is alive to fly it";
        internal const string ReasonInvalidSnapshot =
            "its saved vessel could not be rebuilt";
        internal const string ReasonOrbitTooLow =
            "its orbit dips too low to keep a vessel";
        internal const string ReasonOrbitUnresolved =
            "its recorded orbit could not be worked out";
        internal const string ReasonDiedAfterSpawn =
            "the vessel was destroyed right after it spawned";
        internal const string ReasonNoAnswer =
            "Parsek is holding its spawn; KSP.log names the reason";

        // Abandon reasons the spawner notes at the abandon sites the recording's fields do
        // not tell apart (no living crew, an invalid snapshot). Keyed by recording id;
        // cleared when a watch arms so an old note never names a new refusal.
        private static readonly Dictionary<string, string> abandonNotes =
            new Dictionary<string, string>(StringComparer.Ordinal);

        internal static void NoteAbandonReason(Recording rec, string reason)
        {
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId) || string.IsNullOrEmpty(reason))
                return;
            abandonNotes[rec.RecordingId] = reason;
        }

        internal static string GetAbandonNote(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return null;
            return abandonNotes.TryGetValue(recordingId, out string note) ? note : null;
        }

        internal static void ClearAbandonNote(string recordingId)
        {
            if (!string.IsNullOrEmpty(recordingId))
                abandonNotes.Remove(recordingId);
        }

        internal static void ResetForTesting()
        {
            abandonNotes.Clear();
        }

        /// <summary>
        /// Pure: the outcome so far. A vessel that exists wins; then a final refusal (the
        /// abandon flags, the terminal-orbit latch, the attempt limit); then a hold that may
        /// still lift; then Pending until <see cref="TimeoutSeconds"/>, after which an
        /// unanswered spawn reports as held (a collision retry still running names the
        /// blocker). <paramref name="formatDate"/> prints a UT as the house date; null prints
        /// the raw UT.
        /// </summary>
        internal static SpawnWarpOutcome Evaluate(
            SpawnWarpOutcomeInput input, Func<double, string> formatDate)
        {
            if (!input.RecordingFound)
                return Outcome(SpawnWarpOutcomeKind.Gone, string.Empty, "recording-gone");
            if (input.SpawnedVesselPersistentId != 0 && !input.SpawnAbandoned)
                return Outcome(SpawnWarpOutcomeKind.Spawned, string.Empty, "spawned");

            if (input.SpawnAbandoned || input.VesselSpawned)
            {
                if (input.KscRetired)
                    return Outcome(SpawnWarpOutcomeKind.Refused, ReasonKscRetired, "ksc-retired");
                if (input.WalkbackExhausted)
                    return Outcome(SpawnWarpOutcomeKind.Refused, ReasonWalkbackExhausted, "walkback-exhausted");
                if (input.CollisionBlockCount >= VesselSpawner.MaxCollisionBlocks)
                    return Outcome(SpawnWarpOutcomeKind.Refused, ReasonCollisionBlocked, "collision-blocked");
                if (!string.IsNullOrEmpty(input.AbandonNote))
                    return Outcome(SpawnWarpOutcomeKind.Refused, input.AbandonNote, "noted-abandon");
                return Outcome(SpawnWarpOutcomeKind.Refused, ReasonAbandonedUnknown, "abandoned");
            }

            if (input.TerminalSpawnCannotSpawnSafely)
                return Outcome(SpawnWarpOutcomeKind.Refused,
                    TerminalOrbitRefusalReason(input.TerminalSpawnSafetyReasonCode),
                    "terminal-orbit-" + (input.TerminalSpawnSafetyReasonCode ?? "unknown"));

            if (input.TerminalSpawnSafetyDeferred && input.TerminalDecisionAfterWarp)
                return Outcome(SpawnWarpOutcomeKind.Held,
                    TerminalOrbitHoldReason(input.TerminalSpawnNextAttemptUT, formatDate),
                    "terminal-orbit-deferred");

            if (input.SpawnAttempts >= VesselSpawner.MaxSpawnAttempts)
                return Outcome(SpawnWarpOutcomeKind.Refused, ReasonLoadFailed, "attempts-exhausted");

            if (!(input.ElapsedSeconds >= TimeoutSeconds))
                return Outcome(SpawnWarpOutcomeKind.Pending, string.Empty, "pending");

            if (input.CollisionBlockCount > 0 || input.ChainSpawnBlocked)
                return Outcome(SpawnWarpOutcomeKind.Held, ReasonCollisionBlocked, "collision-retrying");
            return Outcome(SpawnWarpOutcomeKind.Held, ReasonNoAnswer, "no-answer");
        }

        /// <summary>Pure: the refusal clause for a latched terminal-orbit refusal.</summary>
        internal static string TerminalOrbitRefusalReason(string reasonCode)
        {
            switch (reasonCode)
            {
                case TerminalOrbitSpawnSafety.ReasonSpawnedVesselDied:
                    return ReasonDiedAfterSpawn;
                case TerminalOrbitSpawnSafety.ReasonTerminalOrbitResolutionFailed:
                case TerminalOrbitSpawnSafety.ReasonNonFinitePropagatedAltitude:
                case TerminalOrbitSpawnSafety.ReasonNonFinitePeriapsis:
                    return ReasonOrbitUnresolved;
                default:
                    return ReasonOrbitTooLow;
            }
        }

        /// <summary>Pure: the hold clause for a terminal-orbit spawn waiting for its orbit
        /// to climb clear of the atmosphere.</summary>
        internal static string TerminalOrbitHoldReason(double nextAttemptUT, Func<double, string> formatDate)
        {
            if (double.IsNaN(nextAttemptUT) || double.IsInfinity(nextAttemptUT))
                return "it is too low in the atmosphere right now; it spawns once its orbit climbs clear";
            string date = formatDate != null ? formatDate(nextAttemptUT) : null;
            if (string.IsNullOrEmpty(date))
                date = nextAttemptUT.ToString("F0", IC);
            return string.Format(IC,
                "it is too low in the atmosphere right now; it spawns on {0}, once its orbit climbs clear",
                date);
        }

        /// <summary>Pure: the screen message for a Refused or Held outcome; empty for every
        /// other kind (they say nothing).</summary>
        internal static string FormatMessage(SpawnWarpOutcomeKind kind, string vesselName, string reason)
        {
            string name = string.IsNullOrEmpty(vesselName) ? "The vessel" : vesselName;
            switch (kind)
            {
                case SpawnWarpOutcomeKind.Refused:
                    return string.Format(IC, "{0} did not spawn: {1}.", name, reason);
                case SpawnWarpOutcomeKind.Held:
                    return string.Format(IC, "{0} has not spawned yet: {1}.", name, reason);
                default:
                    return string.Empty;
            }
        }

        /// <summary>Pure: true for the kinds that end the watch.</summary>
        internal static bool IsResolved(SpawnWarpOutcomeKind kind)
        {
            return kind != SpawnWarpOutcomeKind.Pending;
        }

        private static SpawnWarpOutcome Outcome(SpawnWarpOutcomeKind kind, string reason, string cause)
        {
            return new SpawnWarpOutcome { Kind = kind, Reason = reason, Cause = cause };
        }
    }
}
