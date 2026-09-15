using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Pure decision for the kerbal-death reputation penalty row (design
    /// docs/parsek-rewind-to-separation-design.md section 7.16).
    ///
    /// <para>
    /// When a crewed vessel is destroyed in flight, stock applies ONE reputation hit
    /// keyed <c>TransactionReasons.VesselLoss</c> per vessel loss - not one per kerbal
    /// aboard - and <c>GameStateRecorder.OnReputationChanged</c> captures it as a
    /// recording-scoped <see cref="GameStateEventType.ReputationChanged"/> event. This
    /// class turns "the recording's crew end states + the recording's captured
    /// reputation events" into "emit one
    /// <see cref="GameActionType.ReputationPenalty"/> row, of this magnitude, at this
    /// UT - or emit nothing, for this named reason".
    /// </para>
    ///
    /// <para>
    /// THE MAGNITUDE IS NEVER FABRICATED. It is only ever the magnitude stock already
    /// took off the pool (<c>valueBefore - valueAfter</c>), which is why
    /// <c>ReputationModule.ProcessRepPenalty</c> treats this source as PRE-CURVED. A
    /// recording with dead crew but no captured event (an injected fixture, a
    /// suppressed event, a sub-threshold penalty) yields NO row: a synthesized -10
    /// would be a guess at a number the curve makes install-state-dependent.
    /// </para>
    ///
    /// Pure - no KSP state access, no logging. The caller logs.
    /// </summary>
    internal static class KerbalDeathRepPenalty
    {
        /// <summary>
        /// The <see cref="GameStateEvent.key"/> a crew-death reputation hit carries.
        /// It is <c>TransactionReasons.VesselLoss.ToString()</c>: stock has no
        /// "CrewKilled" transaction reason at all (the only such symbol in
        /// Assembly-CSharp is <c>GameEvents.onCrewKilled</c>), and both CL-1-pod-impact
        /// flights measured <c>Added -9.999828 (-10) reputation: 'VesselLoss'</c>.
        /// <c>PostWalkActionReconciler</c> maps the row back to this same key.
        /// </summary>
        internal const string VesselLossEventKey = "VesselLoss";

        /// <summary>Reason text when the recording carries no <see cref="KerbalEndState.Dead"/> crew.</summary>
        internal const string ReasonNoDeadCrew = "no dead crew";

        /// <summary>Reason text when dead crew exist but no captured VesselLoss event does.</summary>
        internal const string ReasonNoVesselLossEvent = "no captured VesselLoss event";

        /// <summary>
        /// Outcome of <see cref="Decide"/>. <see cref="Emit"/> false means no row;
        /// <see cref="Reason"/> then names why, for the caller's one Info line.
        /// </summary>
        internal struct Decision
        {
            /// <summary>True when the caller must emit exactly one ReputationPenalty row.</summary>
            public bool Emit;

            /// <summary>
            /// Positive applied magnitude, i.e. <c>valueBefore - valueAfter</c> of the
            /// chosen event. Zero when <see cref="Emit"/> is false.
            /// </summary>
            public float Magnitude;

            /// <summary>UT of the chosen event. Zero when <see cref="Emit"/> is false.</summary>
            public double UT;

            /// <summary>How many crew members ended <see cref="KerbalEndState.Dead"/>.</summary>
            public int DeadCount;

            /// <summary>
            /// How many recording-scoped VesselLoss candidates were found. Greater than
            /// one means the caller should log the ambiguity: the nearest to the death
            /// reference UT was taken.
            /// </summary>
            public int CandidateCount;

            /// <summary>Named refusal reason, or null when <see cref="Emit"/> is true.</summary>
            public string Reason;
        }

        /// <summary>
        /// Counts crew members whose end state is <see cref="KerbalEndState.Dead"/>.
        /// A null list counts zero.
        /// </summary>
        internal static int CountDeadCrew(IReadOnlyList<KerbalEndState> crewEndStates)
        {
            if (crewEndStates == null) return 0;
            int dead = 0;
            for (int i = 0; i < crewEndStates.Count; i++)
            {
                if (crewEndStates[i] == KerbalEndState.Dead)
                    dead++;
            }
            return dead;
        }

        /// <summary>
        /// True iff <paramref name="evt"/> is a reputation LOSS keyed
        /// <see cref="VesselLossEventKey"/> and tagged to <paramref name="recordingId"/>.
        ///
        /// <para>
        /// The tag must match EXACTLY and both sides must be non-empty. An untagged
        /// VesselLoss event is a career-level capture with no owning flight, and
        /// attributing one to a recording would let an unrelated loss pay for this
        /// recording's death. A non-loss (zero or positive) delta under the same key is
        /// not a penalty and is skipped rather than emitted as a negative magnitude.
        /// </para>
        /// </summary>
        internal static bool IsRecordingScopedVesselLoss(GameStateEvent evt, string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return false;
            if (evt.eventType != GameStateEventType.ReputationChanged) return false;
            if (!string.Equals(evt.key, VesselLossEventKey, StringComparison.Ordinal)) return false;
            if (!string.Equals(evt.recordingId ?? "", recordingId, StringComparison.Ordinal)) return false;
            return (evt.valueBefore - evt.valueAfter) > 0.0;
        }

        /// <summary>
        /// Decides whether <paramref name="recordingId"/> gets a kerbal-death
        /// reputation penalty row.
        ///
        /// <para>
        /// AT MOST ONE ROW PER RECORDING regardless of how many crew died: stock
        /// applies one VesselLoss hit per vessel loss. When more than one scoped
        /// VesselLoss candidate exists (a destroyed vessel that lost a second piece
        /// under the same recording tag), the one NEAREST
        /// <paramref name="deathReferenceUT"/> - the recording's end - wins, with ties
        /// going to the earlier entry in <paramref name="events"/>.
        /// </para>
        /// </summary>
        /// <param name="crewEndStates">End states of the recording's crew.</param>
        /// <param name="events">Any event list; only recording-scoped VesselLoss rows are read.</param>
        /// <param name="recordingId">The recording the row would be scoped to.</param>
        /// <param name="deathReferenceUT">The recording's end UT - where a death lands.</param>
        internal static Decision Decide(
            IReadOnlyList<KerbalEndState> crewEndStates,
            IReadOnlyList<GameStateEvent> events,
            string recordingId,
            double deathReferenceUT)
        {
            var decision = new Decision();
            decision.DeadCount = CountDeadCrew(crewEndStates);

            if (decision.DeadCount == 0)
            {
                decision.Reason = ReasonNoDeadCrew;
                return decision;
            }

            int chosen = -1;
            double bestDistance = 0.0;
            int candidates = 0;
            int eventCount = events == null ? 0 : events.Count;
            for (int i = 0; i < eventCount; i++)
            {
                var evt = events[i];
                if (!IsRecordingScopedVesselLoss(evt, recordingId))
                    continue;

                candidates++;
                double distance = Math.Abs(evt.ut - deathReferenceUT);
                if (chosen < 0 || distance < bestDistance)
                {
                    chosen = i;
                    bestDistance = distance;
                }
            }

            decision.CandidateCount = candidates;
            if (chosen < 0)
            {
                decision.Reason = ReasonNoVesselLossEvent;
                return decision;
            }

            var winner = events[chosen];
            decision.Emit = true;
            decision.Magnitude = (float)(winner.valueBefore - winner.valueAfter);
            decision.UT = winner.ut;
            return decision;
        }
    }
}
