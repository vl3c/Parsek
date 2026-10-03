using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// One recording whose orbital terminal spawn must keep the time jump's frozen
    /// geometry: its ghost stood in the loaded physics bubble when an epoch-shift jump
    /// crossed its EndUT.
    /// </summary>
    internal struct TerminalOrbitJumpShift
    {
        internal string RecordingId;
        internal string VesselName;
        internal double PreJumpUT;
        internal double JumpTargetUT;
        internal double LagSeconds;
        internal double GhostSeparationMeters;
    }

    /// <summary>
    /// Design section 14.5 steps 4 and 6 for an ORBITAL tip. The epoch-shift jump
    /// (<see cref="TimeJumpManager.ExecuteJump"/>) keeps every loaded vessel at its
    /// pre-jump state vector by re-epoching its orbit to the target UT, which is a pure
    /// time translation of the orbit by the jump delta. A tip whose ghost stood in the
    /// same bubble must get the same translation, or it spawns on its recorded orbit at
    /// the new UT, n * delta further along than where its ghost stood. This class arms a
    /// per-recording lag at jump time and hands it to the terminal-orbit spawn resolver,
    /// which evaluates the recorded orbit's phase at (spawnUT - lag) and keeps spawnUT as
    /// the epoch. A surface tip needs nothing (the body carries both vessels), so only a
    /// recording whose spawn uses the recorded terminal orbit is armed.
    /// </summary>
    internal static class TimeJumpTerminalOrbitShift
    {
        private const string Tag = "TimeJump";
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        // A shift belongs to the jump that armed it; a clock behind that jump's target
        // means the jump was rewound away and the shift no longer describes this timeline.
        internal const double RewoundToleranceSeconds = 1e-3;

        private static readonly Dictionary<string, TerminalOrbitJumpShift> pending =
            new Dictionary<string, TerminalOrbitJumpShift>(StringComparer.Ordinal);

        // Recordings whose jump-shifted spawn resolution has already been logged at Info, so
        // a blocked tip retried every frame does not repeat the line.
        private static readonly HashSet<string> spawnResolutionLogged =
            new HashSet<string>(StringComparer.Ordinal);

        internal static int PendingCount => pending.Count;

        /// <summary>
        /// Pure: the shift a pending (still unspawned) recording carries after ANOTHER
        /// epoch-shift jump froze its ghost again. The second jump translates the loaded
        /// vessels by its own delta, so the lags add and the shift belongs to the new jump.
        /// </summary>
        internal static TerminalOrbitJumpShift AccumulateShift(
            TerminalOrbitJumpShift existing, double preJumpUT, double jumpTargetUT,
            double ghostSeparationMeters)
        {
            existing.LagSeconds += jumpTargetUT - preJumpUT;
            existing.PreJumpUT = preJumpUT;
            existing.JumpTargetUT = jumpTargetUT;
            existing.GhostSeparationMeters = ghostSeparationMeters;
            return existing;
        }

        /// <summary>
        /// True the first time a spawn resolution for this armed shift is logged; false for
        /// every repeat until the shift is released, re-armed or cleared.
        /// </summary>
        internal static bool TryMarkSpawnResolutionLogged(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return false;
            return spawnResolutionLogged.Add(recordingId);
        }

        internal const string ReasonArm = "arm";
        internal const string ReasonEndNotCrossed = "end-not-crossed";
        internal const string ReasonNotTerminalOrbit = "not-recorded-terminal-orbit";
        internal const string ReasonAlreadySpawned = "already-spawned";
        internal const string ReasonGhostOutsideBubble = "ghost-outside-bubble";
        internal const string ReasonInvalidJump = "invalid-jump";

        /// <summary>
        /// Pure: should this recording's terminal spawn inherit the jump's epoch shift?
        /// Returns <see cref="ReasonArm"/> or the skip reason.
        /// </summary>
        internal static string ClassifyCapture(
            double endUT, double preJumpUT, double jumpTargetUT,
            bool usesRecordedTerminalOrbit, bool alreadySpawned,
            double ghostSeparationMeters, double bubbleMeters)
        {
            if (!IsFiniteValue(preJumpUT) || !IsFiniteValue(jumpTargetUT)
                || jumpTargetUT <= preJumpUT)
                return ReasonInvalidJump;
            if (!IsFiniteValue(endUT) || endUT <= preJumpUT || endUT > jumpTargetUT)
                return ReasonEndNotCrossed;
            if (!usesRecordedTerminalOrbit)
                return ReasonNotTerminalOrbit;
            if (alreadySpawned)
                return ReasonAlreadySpawned;
            if (!IsFiniteValue(ghostSeparationMeters) || ghostSeparationMeters < 0.0
                || ghostSeparationMeters > bubbleMeters)
                return ReasonGhostOutsideBubble;
            return ReasonArm;
        }

        /// <summary>
        /// Pure: the UT at which the recorded terminal orbit's PHASE is evaluated for a
        /// spawn at <paramref name="spawnUT"/>. With no lag it is the spawn UT itself
        /// (the unchanged path); with a jump lag it is the instant the ghost was frozen at,
        /// shifted forward by however long the spawn trailed the jump.
        /// </summary>
        internal static double ResolvePhaseUT(double spawnUT, double lagSeconds)
        {
            if (!IsFiniteValue(spawnUT) || !IsFiniteValue(lagSeconds) || lagSeconds <= 0.0)
                return spawnUT;
            return spawnUT - lagSeconds;
        }

        /// <summary>
        /// Pure: is an armed shift still this timeline's? False once the clock is behind
        /// the jump that armed it (a rewind or quickload before the jump target).
        /// </summary>
        internal static bool IsShiftLiveAt(TerminalOrbitJumpShift shift, double currentUT)
        {
            if (!IsFiniteValue(currentUT) || !IsFiniteValue(shift.JumpTargetUT))
                return false;
            return currentUT >= shift.JumpTargetUT - RewoundToleranceSeconds;
        }

        /// <summary>
        /// Arms a shift for every bubble ghost whose terminal-orbit spawn the jump from
        /// <paramref name="preJumpUT"/> to <paramref name="jumpTargetUT"/> crosses. Called
        /// by <see cref="TimeJumpManager.ExecuteJump"/> before the clock moves. Returns
        /// the number armed.
        /// </summary>
        internal static int CaptureForJump(
            IList<KeyValuePair<Recording, double>> bubbleGhosts,
            double preJumpUT, double jumpTargetUT,
            double bubbleMeters)
        {
            if (bubbleGhosts == null || bubbleGhosts.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "Terminal-orbit jump shift: no bubble ghosts at T0={0:F1} target={1:F1}",
                        preJumpUT, jumpTargetUT));
                return 0;
            }

            int armed = 0;
            int accumulated = 0;
            int endNotCrossed = 0;
            int notTerminalOrbit = 0;
            int alreadySpawned = 0;
            int outsideBubble = 0;
            double lag = jumpTargetUT - preJumpUT;
            for (int i = 0; i < bubbleGhosts.Count; i++)
            {
                Recording rec = bubbleGhosts[i].Key;
                double separation = bubbleGhosts[i].Value;
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;

                // A tip still unspawned from an earlier jump (a blocked spawn held beside the
                // player) is frozen again by this one: its lag grows by this jump's delta.
                if (!rec.VesselSpawned
                    && pending.TryGetValue(rec.RecordingId, out TerminalOrbitJumpShift existing)
                    && IsShiftLiveAt(existing, preJumpUT)
                    && IsFiniteValue(separation) && separation >= 0.0 && separation <= bubbleMeters
                    && jumpTargetUT > preJumpUT)
                {
                    TerminalOrbitJumpShift grown = AccumulateShift(existing, preJumpUT, jumpTargetUT, separation);
                    pending[rec.RecordingId] = grown;
                    spawnResolutionLogged.Remove(rec.RecordingId);
                    accumulated++;
                    ParsekLog.Info(Tag,
                        string.Format(ic,
                            "Terminal-orbit jump shift accumulated: rec={0} vessel={1} T0={2:F1} target={3:F1} lag={4:F1}s (was {5:F1}s) ghostSeparation={6:F1}m",
                            rec.RecordingId, rec.VesselName ?? "(unknown)",
                            preJumpUT, jumpTargetUT, grown.LagSeconds, existing.LagSeconds, separation));
                    continue;
                }

                bool usesTerminalOrbit = VesselSpawner.ShouldUseRecordedTerminalOrbitSpawnState(
                    rec, !string.IsNullOrEmpty(rec.EvaCrewName));
                string decision = ClassifyCapture(
                    rec.EndUT, preJumpUT, jumpTargetUT,
                    usesTerminalOrbit, rec.VesselSpawned,
                    separation, bubbleMeters);
                switch (decision)
                {
                    case ReasonArm:
                        pending[rec.RecordingId] = new TerminalOrbitJumpShift
                        {
                            RecordingId = rec.RecordingId,
                            VesselName = rec.VesselName,
                            PreJumpUT = preJumpUT,
                            JumpTargetUT = jumpTargetUT,
                            LagSeconds = lag,
                            GhostSeparationMeters = separation,
                        };
                        spawnResolutionLogged.Remove(rec.RecordingId);
                        armed++;
                        ParsekLog.Info(Tag,
                            string.Format(ic,
                                "Terminal-orbit jump shift armed: rec={0} vessel={1} T0={2:F1} target={3:F1} lag={4:F1}s ghostSeparation={5:F1}m",
                                rec.RecordingId, rec.VesselName ?? "(unknown)",
                                preJumpUT, jumpTargetUT, lag, separation));
                        break;
                    case ReasonEndNotCrossed: endNotCrossed++; break;
                    case ReasonNotTerminalOrbit: notTerminalOrbit++; break;
                    case ReasonAlreadySpawned: alreadySpawned++; break;
                    case ReasonGhostOutsideBubble: outsideBubble++; break;
                }
            }

            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "Terminal-orbit jump shift capture: bubbleGhosts={0} armed={1} accumulated={2} endNotCrossed={3} notTerminalOrbit={4} alreadySpawned={5} outsideBubble={6}",
                    bubbleGhosts.Count, armed, accumulated, endNotCrossed, notTerminalOrbit,
                    alreadySpawned, outsideBubble));
            return armed + accumulated;
        }

        /// <summary>
        /// The live shift for <paramref name="recordingId"/> at <paramref name="currentUT"/>.
        /// A shift left behind by a rewind is dropped here.
        /// </summary>
        internal static bool TryGetShift(
            string recordingId, double currentUT, out TerminalOrbitJumpShift shift)
        {
            shift = default(TerminalOrbitJumpShift);
            if (string.IsNullOrEmpty(recordingId) || pending.Count == 0)
                return false;
            if (!pending.TryGetValue(recordingId, out shift))
                return false;
            if (IsShiftLiveAt(shift, currentUT))
                return true;

            pending.Remove(recordingId);
            spawnResolutionLogged.Remove(recordingId);
            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Terminal-orbit jump shift dropped: rec={0} reason=clock-behind-jump currentUT={1:F1} target={2:F1}",
                    recordingId, currentUT, shift.JumpTargetUT));
            shift = default(TerminalOrbitJumpShift);
            return false;
        }

        internal static void Release(string recordingId, string reason)
        {
            if (string.IsNullOrEmpty(recordingId))
                return;
            spawnResolutionLogged.Remove(recordingId);
            if (!pending.Remove(recordingId))
                return;
            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "Terminal-orbit jump shift released: rec={0} reason={1}",
                    recordingId, reason ?? "(none)"));
        }

        internal static void Clear(string reason)
        {
            spawnResolutionLogged.Clear();
            if (pending.Count == 0)
                return;
            int count = pending.Count;
            pending.Clear();
            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Terminal-orbit jump shifts cleared: count={0} reason={1}",
                    count, reason ?? "(none)"));
        }

        internal static void ResetForTesting()
        {
            pending.Clear();
            spawnResolutionLogged.Clear();
        }

        private static bool IsFiniteValue(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
